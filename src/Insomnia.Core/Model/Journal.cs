// CONTRATO CONGELADO (desenho §5). Formato em disco: state.json (§4.2), via Store/JsonCodec.
using System.Collections.Immutable;

namespace Insomnia.Core.Model;

/// Valores ANTERIORES de LIDACTION (AC e DC) do esquema que o app zerou.
public sealed record LidActionEntry(Guid SchemeId, uint AcPrevious, uint DcPrevious, DateTimeOffset SetAt);

/// Valor anterior de ESBATTTHRESHOLD. Cause: "battery" ou "lid".
public sealed record EnergySaverEntry(Guid SchemeId, uint DcThresholdPrevious, DateTimeOffset SetAt, string Cause);

/// Processo congelado. Sem CreationTime/BootId = entrada provisória (escrita antes do suspend): nunca é sinalizada.
public sealed record FrozenProcess(int Pid, string Name, DateTimeOffset? CreationTime, string? BootId, string Group)
{
    public bool HasIdentity => CreationTime is not null && !string.IsNullOrEmpty(BootId);
}

/// Saída de áudio como estava antes de mutar.
public sealed record SavedAudioOutput(string DeviceId, string? Name, float Volume, bool Muted);

/// Diário de recuperação: tudo o que o app mudou no sistema e ainda precisa desfazer.
/// Só o persistente entra aqui (R7): o hold de execução morre com o processo e não é diarizado.
public sealed record Journal(
    int Version,
    LidActionEntry? LidAction,
    EnergySaverEntry? EnergySaver,
    ImmutableArray<FrozenProcess> FrozenProcesses,
    bool DockerFrozen,
    ImmutableArray<SavedAudioOutput> SavedAudioOutputs)
{
    public static Journal Clean => new(1, null, null, [], false, []);
    public bool HasLidActions => FrozenProcesses.Length > 0 || DockerFrozen || SavedAudioOutputs.Length > 0;
    /// Saída desconectada (em audioDevicesNotConnected) não torna sujo, como no original.
    public bool IsDirty(IReadOnlySet<string>? audioDevicesNotConnected = null) =>
        LidAction is not null || EnergySaver is not null || FrozenProcesses.Length > 0 || DockerFrozen
        || SavedAudioOutputs.Any(a => audioDevicesNotConnected is null || !audioDevicesNotConnected.Contains(a.DeviceId));
}
