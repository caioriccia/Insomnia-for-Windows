// CONTRATO CONGELADO (desenho §5). Formato em disco: config.json (§4.3), via Store/JsonCodec.
using System.Collections.Immutable;

namespace Insomnia.Core.Model;

/// Preferências do usuário. Durações em segundos. Apps identificados pelo nome do
/// executável (ex.: "slack.exe"), comparados sem diferenciar caixa.
///
/// Pisos de bateria: EndFloor encerra a sessão; LowPowerFloor liga a Economia de
/// energia. Invariante: EndFloor == 0 (fim por bateria desligado) ou EndFloor < LowPowerFloor.
public sealed record Config(
    int Version, ImmutableArray<int> Presets, int DefaultPreset, int MaxDuration,
    ImmutableArray<string> FreezeList, bool FreezeAllApps, bool DockerRule,
    bool MuteOnLidClose, bool DarkenDisplayOnLidClose, bool EnergySaverOnLidClose,
    ImmutableArray<string> AgentList, int LowPowerFloor, int EndFloor,
    string HotspotSsid, bool LaunchAtLogin)
{
    /// Valores do §4.3.
    public static Config Default { get; } = new(
        Version: 1,
        Presets: [1800, 3600, 7200, 14400, 28800, 43200, 86400, 259200],
        DefaultPreset: 14400,
        MaxDuration: 2592000,
        FreezeList: ["slack.exe", "WhatsApp.exe", "Discord.exe"],
        FreezeAllApps: false,
        DockerRule: false,
        MuteOnLidClose: true,
        DarkenDisplayOnLidClose: true,
        EnergySaverOnLidClose: true,
        AgentList: ["Code.exe", "Cursor.exe", "WindowsTerminal.exe", "wt.exe", "pwsh.exe", "powershell.exe", "cmd.exe",
            "claude.exe", "Claude.exe", "ChatGPT.exe", "chrome.exe", "msedge.exe", "brave.exe", "Docker Desktop.exe",
            "ollama.exe", "ollama app.exe", "wsl.exe", "node.exe", "python.exe", "devenv.exe"],
        LowPowerFloor: 40,
        EndFloor: 10,
        HotspotSsid: "",
        LaunchAtLogin: false);

    /// Passo das configurações para os dois pisos; os pisos ficam ao menos um passo afastados.
    public const int FloorStep = 5; public const int MaxEndFloor = 95;

    public bool FloorsAreOrdered => EndFloor == 0 || EndFloor < LowPowerFloor;

    /// Define o piso de fim (0..MaxEndFloor) e, se preciso, SOBE o piso de Economia para EndFloor + passo (teto 100).
    public Config WithEndFloor(int value)
    {
        var end = Math.Clamp(value, 0, MaxEndFloor);
        var low = LowPowerFloor;
        if (end > 0 && low <= end) low = Math.Min(end + FloorStep, 100);
        return this with { EndFloor = end, LowPowerFloor = low };
    }

    /// Define o piso de Economia (0..100) e, se preciso, DESCE o piso de fim para LowPowerFloor − passo (mínimo 0).
    public Config WithLowPowerFloor(int value)
    {
        var low = Math.Clamp(value, 0, 100);
        var end = EndFloor;
        if (end > 0 && end >= low) end = Math.Max(low - FloorStep, 0);
        return this with { LowPowerFloor = low, EndFloor = end };
    }

    /// Corrige um config.json editado à mão: prende os pisos às faixas válidas e, se
    /// estiverem fora de ordem, só SOBE LowPowerFloor (nunca abaixa o fim por bateria).
    public Config Normalized()
    {
        var low = Math.Clamp(LowPowerFloor, 0, 100);
        var end = Math.Clamp(EndFloor, 0, MaxEndFloor);
        if (end > 0 && end >= low) low = Math.Min(end + FloorStep, 100);
        return low == LowPowerFloor && end == EndFloor ? this : this with { LowPowerFloor = low, EndFloor = end };
    }
}
