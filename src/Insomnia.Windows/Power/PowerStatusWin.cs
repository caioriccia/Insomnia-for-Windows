using Insomnia.Core.Abstractions;
// System.Windows.Forms (using global do WinForms) também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.Windows.Power;

/// Bateria e AC via GetSystemPowerStatus. Nunca lança: o que não dá para ler vira
/// BatteryPercent null ("ilegível", que o Core conta como falha de leitura).
public sealed class PowerStatusWin
{
    public PowerStatus Read() =>
        PowerNative.GetSystemPowerStatus(out var s)
            ? FromNative(s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent)
            : new PowerStatus(OnAc: true, BatteryPercent: null);

    /// Conversão pura de SYSTEM_POWER_STATUS.
    /// - ACLineStatus: 0 = bateria, 1 = AC, 255 = desconhecido. Só o 0 explícito conta como
    ///   bateria: na dúvida o Core não encerra a sessão nem liga a Economia por bateria.
    /// - BatteryFlag 128 (sem bateria) ou 255 (desconhecido), ou percentual 255/&gt;100 ⇒ null.
    public static PowerStatus FromNative(byte acLineStatus, byte batteryFlag, byte batteryLifePercent)
    {
        var onAc = acLineStatus != 0;
        int? percent = batteryFlag == 255 || (batteryFlag & 128) != 0 || batteryLifePercent > 100
            ? null
            : batteryLifePercent;
        return new PowerStatus(onAc, percent);
    }
}
