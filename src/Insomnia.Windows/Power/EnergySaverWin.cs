using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Power;

/// IEnergySaver real: SUB_ENERGYSAVER/ESBATTTHRESHOLD só no índice DC (equivalente ao
/// `pmset -b lowpowermode` do Mac). 100 = Economia sempre ligada na bateria. A posse e o valor
/// anterior são do Core (diário); aqui é só ler e escrever. Lê e escreve lançam
/// InvalidOperationException em falha.
public sealed class EnergySaverWin : IEnergySaver
{
    private readonly ILog _log;

    public EnergySaverWin(ILog log) { _log = log; }

    public EnergySaverValues Read()
    {
        var scheme = PowerScheme.ActiveScheme();
        var dc = PowerScheme.ReadDc(scheme, PowerSettingIds.SubEnergySaver, PowerSettingIds.EnergySaverBatteryThreshold);
        return new EnergySaverValues(scheme, dc);
    }

    public void WriteDcThreshold(Guid schemeId, uint threshold)
    {
        if (threshold > 100) throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "ESBATTTHRESHOLD vai de 0 a 100");
        try
        {
            PowerScheme.WriteDc(schemeId, PowerSettingIds.SubEnergySaver, PowerSettingIds.EnergySaverBatteryThreshold, threshold);
            PowerScheme.ReapplyActive();
            _log.Info($"Economia de energia: ESBATTTHRESHOLD (DC) do esquema {schemeId} = {threshold}");
        }
        catch (Exception e)
        {
            _log.Error($"falha ao escrever ESBATTTHRESHOLD (esquema {schemeId}, {threshold}): {e.Message}");
            throw;
        }
    }
}
