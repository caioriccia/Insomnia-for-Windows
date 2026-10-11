using System.Collections.Immutable;
using System.Globalization;

namespace Insomnia.Core.Network;

/// Saída da FailoverMachine: o que o motorista (NetworkFailover) deve fazer.
public abstract record FailoverOutput
{
    private FailoverOutput() { }

    /// Armar o timer ÚNICO da queda para daqui a Delay (substitui o anterior).
    public sealed record ScheduleRetry(TimeSpan Delay) : FailoverOutput;

    /// Tentar entrar no hotspot (perfil Wi-Fi salvo do Windows).
    public sealed record JoinHotspot : FailoverOutput;

    /// O Wi-Fi voltou: a queda começou em Start e durou Gap.
    public sealed record Recovered(DateTimeOffset Start, TimeSpan Gap) : FailoverOutput;
}

/// Failover de hotspot como máquina pura (desenho §7.3): quatro eventos, nenhum relógio,
/// nenhum timer, nenhuma E/S. Quem dirige é dono do tempo e passa o "agora".
///
/// Ritmo (fiel ao original): a primeira tentativa vem 5 s depois da queda; depois de cada
/// tentativa, 5, 10, 20, 30 s e 30 s dali em diante. NÃO desiste: enquanto a queda durar, há
/// sempre uma próxima tentativa agendada. Para só quando o Wi-Fi volta (PathUp) ou quando o
/// motorista é parado.
///
/// Diferenças em relação ao original, ambas de robustez:
/// - Tick antes da hora (timer real adiantado alguns ms) devolve ScheduleRetry do que falta, em
///   vez de nada — senão a queda ficaria sem próxima tentativa para sempre.
/// - Tick com tentativa ainda em andamento (JoinFinished não chegou) não abre uma segunda
///   conexão em paralelo: só reagenda.
///
/// Não é thread-safe: o motorista serializa as chamadas.
public sealed class FailoverMachine
{
    /// Tempo que o Wi-Fi precisa ficar fora antes da primeira tentativa.
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// Espera depois de cada tentativa: 5, 10, 20, 30, 30, 30, ...
    public static readonly ImmutableArray<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    /// Queda a partir da qual a volta é notificada ("Rede voltou").
    public static readonly TimeSpan NotifyThreshold = TimeSpan.FromSeconds(90);

    public DateTimeOffset? OutageStart { get; private set; }
    public int Joins { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public bool JoinInFlight { get; private set; }
    /// Resultado da última tentativa terminada (null = nenhuma terminou nesta queda).
    public bool? LastJoinOk { get; private set; }

    public bool InOutage => OutageStart is not null;

    /// Espera depois da n-ésima tentativa (n começa em 1).
    public static TimeSpan DelayAfterJoin(int joins) => Backoff[Math.Clamp(joins - 1, 0, Backoff.Length - 1)];

    /// Wi-Fi caiu. Queda já em curso: nada (a primeira queda é a que conta).
    public IReadOnlyList<FailoverOutput> PathDown(DateTimeOffset now)
    {
        if (InOutage) return [];
        OutageStart = now;
        Joins = 0;
        JoinInFlight = false;
        LastJoinOk = null;
        NextAttemptAt = now + InitialDelay;
        return [new FailoverOutput.ScheduleRetry(InitialDelay)];
    }

    /// Wi-Fi voltou. Sem queda: nada.
    public IReadOnlyList<FailoverOutput> PathUp(DateTimeOffset now)
    {
        if (OutageStart is not { } start) return [];
        Reset();
        var gap = now - start;
        return [new FailoverOutput.Recovered(start, gap < TimeSpan.Zero ? TimeSpan.Zero : gap)];
    }

    /// O timer da queda disparou.
    public IReadOnlyList<FailoverOutput> Tick(DateTimeOffset now)
    {
        if (!InOutage || NextAttemptAt is not { } due) return [];   // timer velho depois da volta
        if (now < due) return [new FailoverOutput.ScheduleRetry(due - now)];
        if (JoinInFlight)
        {
            var wait = DelayAfterJoin(Math.Max(Joins, 1));
            NextAttemptAt = now + wait;
            return [new FailoverOutput.ScheduleRetry(wait)];
        }
        Joins++;
        JoinInFlight = true;
        var delay = DelayAfterJoin(Joins);
        NextAttemptAt = now + delay;
        return [new FailoverOutput.JoinHotspot(), new FailoverOutput.ScheduleRetry(delay)];
    }

    /// A tentativa de conexão terminou (ok = o Windows aceitou o pedido). Não agenda nada: a
    /// próxima tentativa já foi agendada no Tick, e a volta de verdade chega por PathUp.
    public IReadOnlyList<FailoverOutput> JoinFinished(bool ok)
    {
        if (!InOutage) return [];
        JoinInFlight = false;
        LastJoinOk = ok;
        return [];
    }

    private void Reset()
    {
        OutageStart = null;
        Joins = 0;
        NextAttemptAt = null;
        JoinInFlight = false;
        LastJoinOk = null;
    }

    /// Uma linha por queda em handoffs.log: "&lt;fim&gt; outage start=&lt;início&gt; end=&lt;fim&gt; gap=&lt;N&gt;s" (UTC).
    public static string LogLine(DateTimeOffset start, DateTimeOffset end, TimeSpan gap)
    {
        var e = Iso(end);
        return $"{e} outage start={Iso(start)} end={e} gap={RoundSeconds(gap)}s";
    }

    /// "2m 10s", "45s", "1h 2m 3s", "0s".
    public static string HumanGap(TimeSpan gap)
    {
        var total = RoundSeconds(gap);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h}h");
        if (m > 0) parts.Add($"{m}m");
        if (s > 0 || parts.Count == 0) parts.Add($"{s}s");
        return string.Join(' ', parts);
    }

    private static long RoundSeconds(TimeSpan gap) =>
        (long)Math.Round(Math.Max(0, gap.TotalSeconds), MidpointRounding.AwayFromZero);

    private static string Iso(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
