// CONTRATO CONGELADO (desenho §5): DurationParser.Parse e DurationFormat.Chip.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Insomnia.Core.Time;

/// Lê o campo livre "d h m" do popover. Número sem unidade = minutos ("30" = 30 min).
/// Aceita "1h30m", "1h30", "3d", "1d 2h 15m", "45min" (sem diferenciar caixa), unidades na
/// ordem d → h → m, cada uma no máximo uma vez. Vazio, zero ou qualquer outra forma = null.
/// Não aplica teto: quem chama prende com SessionMath.Clamp / MaxDuration.
public static partial class DurationParser
{
    [GeneratedRegex(@"^\s*(?:(?<d>\d{1,6})\s*d)?\s*(?:(?<h>\d{1,6})\s*h)?\s*(?:(?<m>\d{1,6})\s*(?:min|m)?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static TimeSpan? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Pattern().Match(text);
        if (!match.Success) return null;
        Group d = match.Groups["d"], h = match.Groups["h"], m = match.Groups["m"];
        if (!d.Success && !h.Success && !m.Success) return null;
        static long Value(Group g) => g.Success ? long.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        var seconds = Value(d) * 86400 + Value(h) * 3600 + Value(m) * 60;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }
}

/// Rótulo curto de duração para chips: "30m", "2h", "1h30m", "3d", "1d12h". Segundos são
/// descartados; unidades zeradas somem; zero (ou negativo) vira "0m".
public static class DurationFormat
{
    public static string Chip(TimeSpan d)
    {
        var totalMinutes = d <= TimeSpan.Zero ? 0 : d.Ticks / TimeSpan.TicksPerMinute;
        if (totalMinutes == 0) return "0m";
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;
        var text = "";
        if (days > 0) text += days.ToString(CultureInfo.InvariantCulture) + "d";
        if (hours > 0) text += hours.ToString(CultureInfo.InvariantCulture) + "h";
        if (minutes > 0) text += minutes.ToString(CultureInfo.InvariantCulture) + "m";
        return text;
    }
}
