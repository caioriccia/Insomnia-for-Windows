// CONTRATO CONGELADO (desenho-ui §5.3). Pílula do topo (§3.4) a partir do retrato do estado.
// Puro: não lê sistema nem relógio (o "agora" vem no StatusSnapshot). Sem WPF.
using System.Globalization;
using Insomnia.App.Tray;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;

namespace Insomnia.App.Ui.Model;

public enum PillTone { Normal, Paused, Warning, EndingSoon, Critical }

/// Dot = segundo estado da prioridade do §3.9 quando dois coincidem; com um só, o próprio Tone
/// (Normal/Paused ⇒ ponto Accent, Warning/EndingSoon ⇒ Caution, Critical ⇒ Critical); null sem sessão.
public sealed record PillState(bool Visible, string Glyph, string Text, PillTone Tone, PillTone? Dot, string Tooltip);

public static class PillPresenter
{
    public static readonly TimeSpan EndingSoon = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Critical = TimeSpan.FromMinutes(1);

    /// Visible = há sessão && visibility. Tone pela prioridade do §3.9. Glyph de Glyphs.*. Tooltip = TrayTexts.Tooltip.
    public static PillState From(StatusSnapshot s, bool visibility)
    {
        var tones = Tones(s);
        var tone = tones.Count > 0 ? tones[0] : PillTone.Normal;
        PillTone? dot = s.Session is null ? null : tones.Count > 1 ? tones[1] : tone;
        var text = s.Session is { } session ? Compact(session.Remaining(s.Now)) : "";
        return new PillState(
            Visible: s.Session is not null && visibility,
            Glyph: GlyphOf(tone),
            Text: text,
            Tone: tone,
            Dot: dot,
            Tooltip: TrayTexts.Tooltip(s.Session, s.Now, s.CountdownPaused));
    }

    /// "2d 4h" | "3h 42m" | "42m" | "4m 59s" | "42s"; zero ⇒ "0s". Arredonda para cima como SessionMath
    /// (frações de segundo sobem; as unidades maiores truncam, como a contagem "3:42:15" ⇒ "3h 42m").
    public static string Compact(TimeSpan remaining)
    {
        var total = remaining <= TimeSpan.Zero ? 0
            : (remaining.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        var days = total / 86400;
        var hours = total % 86400 / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return total switch
        {
            >= 86400 => string.Create(CultureInfo.InvariantCulture, $"{days}d {hours}h"),
            >= 3600 => string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes}m"),
            >= 300 => string.Create(CultureInfo.InvariantCulture, $"{total / 60}m"),
            >= 60 => string.Create(CultureInfo.InvariantCulture, $"{minutes}m {seconds}s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{seconds}s"),
        };
    }

    /// Crítico > Fim próximo > Aviso > Tampa fechada > Normal (§3.9).
    public static PillTone ToneOf(StatusSnapshot s)
    {
        var tones = Tones(s);
        return tones.Count > 0 ? tones[0] : PillTone.Normal;
    }

    /// Warnings, LastError, NetworkWarning, RelaunchProblems, WaitingAudio, navegador limitado.
    public static bool HasWarnings(StatusSnapshot s) =>
        s.Warnings.Any(w => !string.IsNullOrWhiteSpace(w))
        || !string.IsNullOrWhiteSpace(s.LastError)
        || !string.IsNullOrWhiteSpace(s.NetworkWarning)
        || s.RelaunchProblems.Values.Any(p => !string.IsNullOrWhiteSpace(p))
        || s.WaitingAudio.Count > 0
        || s.Browsers.Any(b => !b.Unthrottled);

    /// Tampa fechada com sessão: a contagem pausou ou a tampa já está fechada (antes do debounce).
    internal static bool LidClosedSession(StatusSnapshot s) =>
        s.Session is not null && (s.CountdownPaused || s.Lid == LidState.Closed);

    // Estados que se aplicam, já na ordem de prioridade (o relógio conta uma vez só: Critical OU EndingSoon).
    private static List<PillTone> Tones(StatusSnapshot s)
    {
        var tones = new List<PillTone>(3);
        if (s.Session is { } session)
        {
            var remaining = session.Remaining(s.Now);
            if (remaining < Critical) tones.Add(PillTone.Critical);
            else if (remaining < EndingSoon) tones.Add(PillTone.EndingSoon);
        }
        if (HasWarnings(s)) tones.Add(PillTone.Warning);
        if (LidClosedSession(s)) tones.Add(PillTone.Paused);
        return tones;
    }

    private static string GlyphOf(PillTone tone) => tone switch
    {
        PillTone.Critical or PillTone.EndingSoon => Glyphs.Clock,
        PillTone.Warning => Glyphs.Warning,
        PillTone.Paused => Glyphs.Pause,
        _ => Glyphs.Eye,
    };
}
