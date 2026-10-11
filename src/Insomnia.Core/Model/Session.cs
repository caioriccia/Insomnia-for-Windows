// CONTRATO CONGELADO (desenho §5). Formato em disco: session.json (§4.1), via Store/JsonCodec.
using System.Collections.Immutable;

namespace Insomnia.Core.Model;

/// Sessão em curso. Válida ⇔ EndsAt > agora. Extensions = segundos de cada extensão.
public sealed record Session(DateTimeOffset StartedAt, DateTimeOffset EndsAt, ImmutableArray<int> Extensions)
{
    public int Version { get; init; } = 1;
    public bool IsExpired(DateTimeOffset now) => EndsAt <= now;
    public TimeSpan Remaining(DateTimeOffset now) => EndsAt > now ? EndsAt - now : TimeSpan.Zero;
}
