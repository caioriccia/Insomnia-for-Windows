// CONTRATO CONGELADO (desenho §5): CountdownShape, SessionMath.Extended/ShapeFor/FormatCountdown/FormatCompact.
// Aritmética pura de sessão: sem relógio, sem efeito colateral.
using System.Collections.Immutable;
using System.Globalization;

namespace Insomnia.Core.Time;

// O using fica DENTRO do namespace de propósito: o namespace Insomnia.Core.Session (da
// frente 'sessao') venceria o record Model.Session numa busca por nome simples.
using Insomnia.Core.Model;

/// Formato da contagem ao vivo, escolhido uma vez pela duração inicial e mantido
/// a sessão inteira (largura estável na bandeja).
public enum CountdownShape { Minutes, Hours, Days }

public static class SessionMath
{
    /// Menor sessão aceita (e menor teto, caso MaxDuration venha absurdo).
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(60);

    /// Prende uma duração pedida em [MinimumDuration, max].
    public static TimeSpan Clamp(TimeSpan duration, TimeSpan max)
    {
        var upper = max > MinimumDuration ? max : MinimumDuration;
        if (duration < MinimumDuration) return MinimumDuration;
        return duration > upper ? upper : duration;
    }

    /// Sessão nova começando em now, com a duração presa por Clamp.
    public static Session NewSession(DateTimeOffset now, TimeSpan duration, TimeSpan max) =>
        new(now, now + Clamp(duration, max), []);

    /// Empurra EndsAt por `by`, nunca além de now + max. Sessão já expirada reinicia
    /// a contagem a partir de now. Registra em Extensions os segundos efetivamente somados.
    public static Session Extended(Session s, TimeSpan by, DateTimeOffset now, TimeSpan max)
    {
        var start = s.EndsAt > now ? s.EndsAt : now;
        var ceiling = now + (max > MinimumDuration ? max : MinimumDuration);
        var proposed = start + (by > TimeSpan.Zero ? by : TimeSpan.Zero);
        var newEnd = proposed < ceiling ? proposed : ceiling;
        var added = (int)Math.Round((newEnd - s.EndsAt).TotalSeconds, MidpointRounding.AwayFromZero);
        var extensions = s.Extensions.IsDefault ? ImmutableArray<int>.Empty : s.Extensions;
        return s with { EndsAt = newEnd, Extensions = extensions.Add(added) };
    }

    /// Menos de 1 h: Minutes; menos de 1 dia: Hours; senão Days.
    public static CountdownShape ShapeFor(TimeSpan initialDuration) =>
        initialDuration >= TimeSpan.FromDays(1) ? CountdownShape.Days
        : initialDuration >= TimeSpan.FromHours(1) ? CountdownShape.Hours
        : CountdownShape.Minutes;

    /// "MM:SS" | "H:MM:SS" | "Nd H:MM:SS". Frações de segundo arredondam para CIMA (uma
    /// sessão nova de 30 min abre em "30:00" e só mostra zeros quando acaba). Negativo vira zero.
    /// Days perde o dia quando falta menos de 24 h.
    public static string FormatCountdown(TimeSpan remaining, CountdownShape shape)
    {
        var total = CeilSeconds(remaining);
        var seconds = total % 60;
        var minutes = total / 60 % 60;
        string Clock(long hours) => string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}");
        switch (shape)
        {
            case CountdownShape.Days:
                var days = total / 86400;
                return days > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{days}d ") + Clock(total % 86400 / 3600)
                    : Clock(total / 3600);
            case CountdownShape.Hours:
                return Clock(total / 3600);
            default:
                return string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{seconds:00}");
        }
    }

    /// Texto curto do ícone, só a maior unidade, arredondando para baixo: "3d", "4h", "59m", "&lt;1m".
    public static string FormatCompact(TimeSpan remaining)
    {
        var totalMinutes = remaining <= TimeSpan.Zero ? 0 : remaining.Ticks / TimeSpan.TicksPerMinute;
        if (totalMinutes < 1) return "<1m";
        if (totalMinutes >= 24 * 60) return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / (24 * 60)}d");
        if (totalMinutes >= 60) return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60}h");
        return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes}m");
    }

    private static long CeilSeconds(TimeSpan t) =>
        t <= TimeSpan.Zero ? 0 : (t.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
}
