// Regra Docker (desenho §7.2 passo 4): só congela o Docker Desktop se `docker ps -q` disser
// "ocioso" DUAS vezes — antes de diarizar e logo antes do suspend. Qualquer outra resposta
// (ocupado, erro, timeout, cancelamento por novo evento de tampa) deixa o Docker rodando.
// Limite conhecido: a VM do WSL2 (vmmemWSL) é do sistema e não congela; só UI e backend pausam.
using System.Collections.Immutable;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Lid;

public static class DockerRule
{
    /// Nome do grupo no diário (FrozenProcess.Group) para a árvore do Docker.
    public const string GroupName = "docker";

    /// Raízes da árvore do Docker Desktop.
    public static readonly ImmutableArray<string> RootNames = ["Docker Desktop.exe", "com.docker.backend.exe"];

    /// Árvore do Docker Desktop no snapshot. A regra é dona dela: AgentList e a lista do
    /// Docker NÃO se aplicam (senão o padrão, que tem "Docker Desktop.exe" na AgentList,
    /// anularia a regra); a parte de sistema da denylist vale nos descendentes. null = não está rodando.
    public static FreezeGroup? Group(IReadOnlyList<ProcessInfo> snapshot, PlannerEnvironment env)
    {
        var roots = snapshot.Where(p => RootNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && !FreezePlanner.IsSystemDenied(p, env));
        var members = FreezePlanner.Tree(roots, snapshot, p => FreezePlanner.IsSystemDenied(p, env), env, []);
        return members.Length == 0 ? null : new FreezeGroup(GroupName, members, Explicit: false);
    }

    /// Teto da checagem (R11). O adaptador real tem o mesmo teto; este aqui garante o limite
    /// mesmo com uma sonda que não respeite o seu.
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// Consulta que nunca lança: exceção do adaptador, teto estourado ou cancelamento (novo
    /// evento de tampa) viram Unavailable, sem esperar a sonda terminar. `timeout` só existe
    /// para teste; o padrão é ProbeTimeout.
    public static async Task<DockerProbe> ProbeAsync(IDockerProbe probe, ILog log, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? ProbeTimeout;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(limit);
        try
        {
            return await probe.ProbeAsync(bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Info($"Docker: checagem passou de {limit.TotalSeconds:0.#} s; Docker fica rodando");
            return DockerProbe.Unavailable;
        }
        catch (OperationCanceledException)
        {
            log.Info("Docker: checagem cancelada por novo evento de tampa; Docker fica rodando");
            return DockerProbe.Unavailable;
        }
        catch (Exception ex)
        {
            log.Error($"Docker: checagem falhou ({ex.Message}); Docker fica rodando");
            return DockerProbe.Unavailable;
        }
    }
}
