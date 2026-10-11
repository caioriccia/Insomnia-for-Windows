// IDockerProbe real: `docker --host npipe:////./pipe/<engine> ps -q`, teto de 5 s (R11).
// Sem engine do Docker Desktop de pé, sem docker.exe, erro ou timeout ⇒ Unavailable
// (o Docker fica rodando). Nenhum teste chama esta classe; a lógica pura está em DockerCli.
using System.Diagnostics;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Docker;

public sealed class DockerProbeWin(ILog? log = null) : IDockerProbe
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<DockerProbe> ProbeAsync(CancellationToken ct)
    {
        var host = DockerCli.ChooseHost(ListPipes());
        if (host is null)
        {
            log?.Info("Docker: engine do Docker Desktop não encontrado; nada a checar");
            return DockerProbe.Unavailable;
        }
        var exe = DockerCli.CandidatePaths(Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .FirstOrDefault(File.Exists);
        if (exe is null)
        {
            log?.Info("Docker: docker.exe não encontrado");
            return DockerProbe.Unavailable;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in DockerCli.Arguments(host)) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return DockerProbe.Unavailable;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return DockerCli.Interpret(process.ExitCode, output, timedOut: false);
        }
        catch (OperationCanceledException)
        {
            log?.Info(ct.IsCancellationRequested ? "Docker: checagem cancelada" : "Docker: `docker ps` passou de 5 s");
            Kill(process);
            return DockerCli.Interpret(null, null, timedOut: true);
        }
        catch (Exception ex)
        {
            log?.Error($"Docker: `docker ps` falhou ({ex.Message})");
            Kill(process);
            return DockerProbe.Unavailable;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static IEnumerable<string> ListPipes()
    {
        try { return Directory.GetFiles(@"\\.\pipe\"); }
        catch { return []; }
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* já saiu */ }
    }
}
