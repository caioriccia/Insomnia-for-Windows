// IAudioControl sobre WASAPI via NAudio (desenho §7): saída padrão de Render/Console,
// restauração pelo ID do endpoint (não pela padrão atual), DevicesChanged por notificação
// de endpoint. Nenhum teste chama esta classe: o usuário valida no aparelho.
using System.Runtime.InteropServices;
using Insomnia.Core.Abstractions;
using NAudio.CoreAudioApi;

namespace Insomnia.Windows.Audio;

public sealed class AudioControlWin : IAudioControl, IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _notifications;
    private bool _disposed;

    /// Adiciona/remove/troca de estado/troca da saída padrão. Mudança de propriedade (ex.:
    /// volume) não conta. Disparado na thread de trabalho do áudio: quem assina não pode bloquear
    /// (LidActions só agenda trabalho assíncrono).
    public event Action? DevicesChanged;

    public AudioControlWin()
    {
        _enumerator = new MMDeviceEnumerator();
        // Sem SynchronizationContext: os eventos vêm na thread do áudio, e o handler só dispara trabalho assíncrono.
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => Raise();
        _notifications.DeviceRemoved += (_, _) => Raise();
        _notifications.DeviceStateChanged += (_, _) => Raise();
        _notifications.DefaultDeviceChanged += (_, _) => Raise();
    }

    public AudioOutput ReadDefaultOutput()
    {
        lock (_gate)
        {
            try
            {
                if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                    throw new AudioUnavailableException("sem saída de áudio padrão");
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                return Describe(device);
            }
            catch (Exception ex) when (ex is not AudioUnavailableException)
            {
                throw new AudioUnavailableException($"leitura da saída padrão falhou: {ex.Message}");
            }
        }
    }

    public AudioOutput Read(string deviceId)
    {
        lock (_gate)
        {
            using var device = Open(deviceId);
            return Describe(device);
        }
    }

    public void SetMute(string deviceId, bool muted)
    {
        lock (_gate)
        {
            using var device = Open(deviceId);
            device.AudioEndpointVolume.Mute = muted;
        }
    }

    public void SetVolume(string deviceId, float volume)
    {
        lock (_gate)
        {
            using var device = Open(deviceId);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = AudioVolume.Clamp(volume);
        }
    }

    /// Endpoint pelo ID; desconectado ou desconhecido ⇒ AudioDeviceMissingException.
    private MMDevice Open(string deviceId)
    {
        MMDevice device;
        try { device = _enumerator.GetDevice(deviceId); }
        catch (COMException) { throw new AudioDeviceMissingException(deviceId); }
        catch (ArgumentException) { throw new AudioDeviceMissingException(deviceId); }

        bool connected;
        try { connected = AudioVolume.IsConnected(device.State); }
        catch (COMException) { connected = false; }
        if (!connected)
        {
            device.Dispose();
            throw new AudioDeviceMissingException(deviceId);
        }
        return device;
    }

    private static AudioOutput Describe(MMDevice device)
    {
        string? name;
        try { name = device.FriendlyName; }
        catch (COMException) { name = null; }
        var volume = device.AudioEndpointVolume;
        return new AudioOutput(device.ID, name, volume.MasterVolumeLevelScalar, volume.Mute);
    }

    private void Raise()
    {
        try { DevicesChanged?.Invoke(); }
        catch { /* nunca propagar exceção para a thread do áudio */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _notifications.Dispose();
        _enumerator.Dispose();
    }
}
