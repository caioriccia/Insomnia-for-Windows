// CONTRATO CONGELADO (desenho-ui §5.3). Painel que desce da pílula (§3.5), nos modos Iniciar e
// Sessão, a partir do retrato do estado. Puro: sem relógio (o "agora" vem no StatusSnapshot), sem WPF.
// Textos: §3.11; avisos: os mesmos do StatusMenuBuilder, na mesma ordem e com a mesma deduplicação.
using System.Globalization;
using Insomnia.App.Tray;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Time;
// System.Windows.Forms também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Ui.Model;

// Dentro do namespace de propósito: o namespace de testes Insomnia.App.Tests.Ui.Config (e qualquer
// Ui.Config futuro) venceria o tipo Config numa busca por nome simples.
using Config = Insomnia.Core.Model.Config;

/// Caution = aviso de recuperação; Critical = erro de comando (a vista usa para TrayTexts.Explain).
public enum NoticeKind { Caution, Critical }
public sealed record PanelChip(string Label, TimeSpan Duration, bool Selected);
public sealed record PanelNotice(NoticeKind Kind, string Text, string? ActionLabel = null, MenuCommand Command = MenuCommand.None, string? Argument = null);
public sealed record StatusRow(string Glyph, string Label, string Value);
public sealed record LidBanner(string Title, string Detail);

public sealed record PanelState(
    PanelMode Mode, PillTone Tone,
    string Headline,                       // "3:42:15" ou "4h"
    string Subline,                        // "termina às 22:00[ · pausada com a tampa fechada][ · faltam 5 min]" / "terminaria às 22:00"
    IReadOnlyList<PanelChip> Chips,        // Session: +15 min/+1 h/+4 h; Start: presets
    IReadOnlyList<StatusRow> Rows,
    LidBanner? Lid,
    IReadOnlyList<PanelNotice> Notices,
    bool ExtendEmphasis);                  // fim próximo ⇒ +15 min em acento

public static class PanelPresenter
{
    public static readonly TimeSpan[] ExtendSteps = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];
    private static readonly string[] ExtendLabels = ["+15 min", "+1 h", "+4 h"];

    public const string LidTitle = "Tampa fechada";
    private const string Separator = " · ";

    /// selectedPreset: Start ⇒ preset escolhido (null = Config.DefaultPreset). Textos do §3.11. Avisos: mesma ordem/dedup de StatusMenuBuilder.
    public static PanelState From(StatusSnapshot s, Config config, TimeSpan? selectedPreset)
    {
        var tone = PillPresenter.ToneOf(s);
        var notices = Notices(s);
        if (s.Session is not { } session)
        {
            var selected = selectedPreset ?? TimeSpan.FromSeconds(config.DefaultPreset);
            List<PanelChip> chips = config.Presets.IsDefault ? [] : config.Presets
                .Select(seconds => TimeSpan.FromSeconds(seconds))
                .Select(d => new PanelChip(DurationFormat.Chip(d), d, d == selected))
                .ToList();
            // A hora do fim pela mesma regra da sessão de verdade ("22:00" ou "13/10 22:00").
            var wouldEnd = TrayTexts.EndLabel(new SessionModel(s.Now, s.Now + selected, []), s.Now);
            return new PanelState(PanelMode.Start, tone,
                Headline: DurationFormat.Chip(selected),
                Subline: $"terminaria às {wouldEnd}",
                Chips: chips,
                Rows: MachineRows(s),
                Lid: null,
                Notices: notices,
                ExtendEmphasis: false);
        }

        var remaining = session.Remaining(s.Now);
        var endingSoon = remaining < PillPresenter.EndingSoon;
        var subline = $"termina às {TrayTexts.EndLabel(session, s.Now)}";
        if (s.CountdownPaused) subline += " · pausada com a tampa fechada";
        if (endingSoon)
        {
            var minutes = Math.Max(1, (long)Math.Ceiling(remaining.TotalMinutes));
            subline += string.Create(CultureInfo.InvariantCulture, $" · faltam {minutes} min");
        }

        var lidClosed = PillPresenter.LidClosedSession(s);
        var rows = MachineRows(s);
        var actions = Effects(s, config, lidClosed, withDisplay: false);
        if (actions.Length > 0) rows.Add(new StatusRow(Glyphs.Snowflake, "Ações", actions));
        if (s.LastGap is { } gap)
            rows.Add(new StatusRow(Glyphs.Clock, "Última queda",
                string.Create(CultureInfo.InvariantCulture, $"{(long)Math.Round(gap.TotalSeconds)} s")));

        return new PanelState(PanelMode.Session, tone,
            Headline: TrayTexts.Countdown(session, s.Now),
            Subline: subline,
            Chips: [.. ExtendSteps.Select((d, i) => new PanelChip(ExtendLabels[i], d, false))],
            Rows: rows,
            Lid: lidClosed ? new LidBanner(LidTitle, Effects(s, config, lidClosed: true, withDisplay: true)) : null,
            Notices: notices,
            ExtendEmphasis: endingSoon);
    }

    /// "2 apps congelados · Docker pausado · áudio mudo"; "" se nada. Só o que o retrato prova
    /// (apps congelados, Docker); o "áudio mudo" da linha Ações vem do From, que conhece o Config.
    public static string ActionsText(StatusSnapshot s) => Effects(s, config: null, lidClosed: false, withDisplay: false);

    // Efeitos ativos da sessão. O retrato não diz se o áudio foi mudado nem se a tela apagou: com a
    // tampa fechada, esses dois vêm do que o Config manda fazer ao fechar (MuteOnLidClose,
    // DarkenDisplayOnLidClose). Com a tampa aberta eles já foram desfeitos e não aparecem.
    private static string Effects(StatusSnapshot s, Config? config, bool lidClosed, bool withDisplay)
    {
        var parts = new List<string>();
        if (s.FrozenApps == 1) parts.Add("1 app congelado");
        else if (s.FrozenApps > 1) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{s.FrozenApps} apps congelados"));
        if (s.DockerPaused) parts.Add("Docker pausado");
        if (lidClosed && config is not null)
        {
            if (config.MuteOnLidClose) parts.Add("áudio mudo");
            if (withDisplay && config.DarkenDisplayOnLidClose) parts.Add("tela apagada");
        }
        return string.Join(Separator, parts);
    }

    // Tampa, Bateria e Wi-Fi; o que é desconhecido some (mesma regra do MachineLine).
    private static List<StatusRow> MachineRows(StatusSnapshot s)
    {
        var rows = new List<StatusRow>();
        switch (s.Lid)
        {
            case LidState.Open: rows.Add(new StatusRow(Glyphs.Laptop, "Tampa", "aberta")); break;
            case LidState.Closed: rows.Add(new StatusRow(Glyphs.Laptop, "Tampa", "fechada")); break;
        }
        if (s.Power is PowerStatus power)
        {
            if (power.BatteryPercent is { } pct)
                rows.Add(new StatusRow(power.OnAc ? Glyphs.BatteryCharging : Glyphs.Battery, "Bateria",
                    string.Create(CultureInfo.InvariantCulture, $"{pct} % · {(power.OnAc ? "carregando" : "na bateria")}")));
            else if (power.OnAc)
                rows.Add(new StatusRow(Glyphs.BatteryCharging, "Bateria", "na tomada"));
        }
        if (s.WifiConnected is { } connected)
            rows.Add(new StatusRow(connected ? Glyphs.Wifi : Glyphs.WifiError, "Wi-Fi",
                !connected ? "desconectado" : string.IsNullOrWhiteSpace(s.WifiSsid) ? "conectado" : s.WifiSsid.Trim()));
        return rows;
    }

    // Mesma ordem e deduplicação do StatusMenuBuilder.Build, mas em pares aviso+ação: navegadores
    // limitados (um aviso por navegador, com o relançar dele; fora da deduplicação, como no menu),
    // RelaunchProblems por chave, NetworkWarning, LastError, Warnings, saídas de áudio esperando.
    // O "⚠" do menu sai do texto: a InfoBar já desenha o glifo de aviso.
    private static List<PanelNotice> Notices(StatusSnapshot s)
    {
        var notices = new List<PanelNotice>();

        foreach (var b in s.Browsers.Where(b => !b.Unthrottled))
            if (BrowserThrottle.ThrottledWarning([b.DisplayName]) is { } warning)
                notices.Add(new PanelNotice(NoticeKind.Caution, Plain(warning),
                    BrowserThrottle.RelaunchMenuLabel(b.DisplayName), MenuCommand.Relaunch, b.Kind));

        // Chave de deduplicação = o texto sem o "⚠" (o menu compara a linha já com o prefixo: dá no mesmo).
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        void Warn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var plain = Plain(text);
            if (seen.TryAdd(plain, notices.Count)) notices.Add(new PanelNotice(NoticeKind.Caution, plain));
        }
        foreach (var problem in s.RelaunchProblems.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) Warn(problem.Value);
        Warn(s.NetworkWarning);
        Warn(s.LastError);
        foreach (var w in s.Warnings) Warn(w);

        foreach (var audio in s.WaitingAudio)
        {
            var text = $"{audio.Label} continua mudo: esperando a saída voltar";
            var notice = new PanelNotice(NoticeKind.Caution, text,
                $"Parar de esperar {audio.Label}", MenuCommand.StopWaitingAudio, audio.DeviceId);
            // O menu sempre põe a ação, mesmo quando o aviso repetido some: aqui, o aviso igual que
            // ainda não tem ação ganha esta; senão (duas saídas com o mesmo nome) entra outro par.
            if (seen.TryGetValue(text, out var index) && notices[index].ActionLabel is null)
                notices[index] = notice;
            else
            {
                seen.TryAdd(text, notices.Count);
                notices.Add(notice);
            }
        }
        return notices;
    }

    private static string Plain(string text) => text.StartsWith('⚠') ? text[1..].TrimStart() : text;
}
