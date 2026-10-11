// Menu de clique direito da bandeja (desenho §9), montado a partir de um retrato do estado.
// Puro: não lê sistema nem relógio — quem chama (TrayController) junta o retrato e traduz as
// linhas em ToolStripItems. Ordem das linhas fiel ao original (dossiê UI §1.9).
using System.Collections.Immutable;
using System.Globalization;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Time;
// System.Windows.Forms (using global do WinForms) também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Tray;

/// Uma saída de áudio que ficou muda no diário esperando reconectar (ou nova tentativa).
public sealed record WaitingAudio(string DeviceId, string Label);

/// Tudo o que o menu mostra. Campos nulos/vazios = parte desconhecida, omitida.
public sealed record StatusSnapshot
{
    public SessionModel? Session { get; init; }
    public DateTimeOffset Now { get; init; }
    public bool CountdownPaused { get; init; }
    public LidState Lid { get; init; } = LidState.Unknown;
    public PowerStatus? Power { get; init; }
    /// null = estado do Wi-Fi não lido.
    public bool? WifiConnected { get; init; }
    public string? WifiSsid { get; init; }
    public int FrozenApps { get; init; }
    public bool DockerPaused { get; init; }
    public TimeSpan? LastGap { get; init; }
    /// Build com LidSimulationWatcher (Debug ou INSOMNIA_LID_SIMULATION).
    public bool LidSimulation { get; init; }
    public IReadOnlyList<BrowserStatus> Browsers { get; init; } = [];
    public IReadOnlyDictionary<string, string> RelaunchProblems { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string? NetworkWarning { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<WaitingAudio> WaitingAudio { get; init; } = [];
}

public enum MenuLineKind { Info, Warning, Action, Separator }
public enum MenuCommand { None, Relaunch, StopWaitingAudio, Settings, Quit }

/// Info e Warning saem desabilitados (só leitura); Action é clicável e leva Command/Argument.
public sealed record MenuLine(MenuLineKind Kind, string Text, MenuCommand Command = MenuCommand.None, string? Argument = null)
{
    public static MenuLine Separator { get; } = new(MenuLineKind.Separator, "");
}

public static class StatusMenuBuilder
{
    public const string SettingsLabel = "Configurações…";
    public const string QuitLabel = "Sair";
    public const string SimulationMarker = "⚠ Build com simulação de tampa (arquivo simulate-lid)";

    public static IReadOnlyList<MenuLine> Build(StatusSnapshot s)
    {
        var lines = new List<MenuLine> { new(MenuLineKind.Info, CountdownLine(s)) };

        if (MachineLine(s) is { } machine) lines.Add(new(MenuLineKind.Info, machine));
        if (ActionsLine(s) is { } actions) lines.Add(new(MenuLineKind.Info, actions));
        if (s.LidSimulation) lines.Add(new(MenuLineKind.Warning, SimulationMarker));

        // Navegadores limitados: um aviso e um item de relançar por navegador.
        var throttled = s.Browsers.Where(b => !b.Unthrottled).ToList();
        if (BrowserThrottle.ThrottledWarning([.. throttled.Select(b => b.DisplayName)]) is { } browserWarning)
        {
            lines.Add(new(MenuLineKind.Warning, browserWarning));
            foreach (var b in throttled)
                lines.Add(new(MenuLineKind.Action, BrowserThrottle.RelaunchMenuLabel(b.DisplayName), MenuCommand.Relaunch, b.Kind));
        }

        // Avisos, sem repetir a mesma frase vinda de fontes diferentes.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Warn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var line = text.StartsWith('⚠') ? text : "⚠ " + text;
            if (seen.Add(line)) lines.Add(new(MenuLineKind.Warning, line));
        }
        foreach (var problem in s.RelaunchProblems.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) Warn(problem.Value);
        Warn(s.NetworkWarning);
        Warn(s.LastError);
        foreach (var w in s.Warnings) Warn(w);

        foreach (var audio in s.WaitingAudio)
        {
            Warn($"{audio.Label} continua mudo: esperando a saída voltar");
            lines.Add(new(MenuLineKind.Action, $"Parar de esperar {audio.Label}", MenuCommand.StopWaitingAudio, audio.DeviceId));
        }

        lines.Add(MenuLine.Separator);
        lines.Add(new(MenuLineKind.Action, SettingsLabel, MenuCommand.Settings));
        lines.Add(new(MenuLineKind.Action, QuitLabel, MenuCommand.Quit));
        return lines;
    }

    /// "Sessão: 1:23:45 · termina 22:00" (+ " · pausada com a tampa fechada"); ocioso: "Sem sessão ativa".
    public static string CountdownLine(StatusSnapshot s)
    {
        if (s.Session is not { } session) return "Sem sessão ativa";
        var text = $"Sessão: {TrayTexts.Countdown(session, s.Now)} · termina {TrayTexts.EndLabel(session, s.Now)}";
        return s.CountdownPaused ? text + " · pausada com a tampa fechada" : text;
    }

    /// "Tampa: aberta · 87% carregando · Wi-Fi: casa"; partes desconhecidas somem; null se nada.
    public static string? MachineLine(StatusSnapshot s)
    {
        var parts = new List<string>();
        switch (s.Lid)
        {
            case LidState.Open: parts.Add("Tampa: aberta"); break;
            case LidState.Closed: parts.Add("Tampa: fechada"); break;
        }
        if (s.Power is { } power)
        {
            if (power.BatteryPercent is { } pct)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{pct}% {(power.OnAc ? "carregando" : "na bateria")}"));
            else if (power.OnAc)
                parts.Add("na tomada");
        }
        if (s.WifiConnected is { } connected)
            parts.Add(!connected ? "Wi-Fi: desconectado"
                : string.IsNullOrWhiteSpace(s.WifiSsid) ? "Wi-Fi: conectado" : $"Wi-Fi: {s.WifiSsid.Trim()}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// "2 apps congelados · Docker pausado · última queda 42s"; null se nada.
    public static string? ActionsLine(StatusSnapshot s)
    {
        var parts = new List<string>();
        if (s.FrozenApps == 1) parts.Add("1 app congelado");
        else if (s.FrozenApps > 1) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{s.FrozenApps} apps congelados"));
        if (s.DockerPaused) parts.Add("Docker pausado");
        if (s.LastGap is { } gap)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"última queda {(long)Math.Round(gap.TotalSeconds)}s"));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
