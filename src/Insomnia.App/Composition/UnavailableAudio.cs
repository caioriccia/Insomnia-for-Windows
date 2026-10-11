// IAudioControl de reserva para quando o WASAPI não abre (máquina sem áudio, serviço
// parado). Leitura da saída padrão lança AudioUnavailableException — o fechar da tampa
// pula o áudio — e a restauração vira "problema" (a entrada do diário FICA, R3), em vez de
// derrubar a composição inteira.
using Insomnia.Core.Abstractions;

namespace Insomnia.App.Composition;

public sealed class UnavailableAudio(string reason) : IAudioControl
{
    public string Reason { get; } = reason;

    public AudioOutput ReadDefaultOutput() => throw new AudioUnavailableException(Reason);
    public AudioOutput Read(string deviceId) => throw new AudioUnavailableException(Reason);
    public void SetMute(string deviceId, bool muted) => throw new AudioUnavailableException(Reason);
    public void SetVolume(string deviceId, float volume) => throw new AudioUnavailableException(Reason);

    public event Action? DevicesChanged { add { } remove { } }
}
