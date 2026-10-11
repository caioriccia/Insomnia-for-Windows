// Helpers puros do áudio (testáveis sem tocar o sistema).
using NAudio.CoreAudioApi;

namespace Insomnia.Windows.Audio;

public static class AudioVolume
{
    /// Escalar de volume aceito pelo WASAPI (MasterVolumeLevelScalar): 0..1. NaN/∞ viram 0
    /// (silêncio é o lado seguro: nunca devolver volume no máximo por um valor inválido do diário).
    public static float Clamp(float volume) =>
        float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0f;

    /// Só saída ativa conta como "conectada". Desplugada, ausente ou desabilitada = desconectada:
    /// a entrada fica no diário esperando (AudioDeviceMissingException).
    public static bool IsConnected(DeviceState state) => state == DeviceState.Active;
}
