// Helpers puros da checagem do Docker (testáveis sem rodar o CLI).
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Docker;

public static class DockerCli
{
    /// Pipes do engine do Docker Desktop, em ordem de preferência. Fixar o host evita que um
    /// DOCKER_HOST/contexto remoto responda "ocioso" pelo engine errado (como o original fixava o socket).
    public static readonly IReadOnlyList<string> EnginePipes = ["dockerDesktopLinuxEngine", "docker_engine"];

    public const string DefaultInstallSuffix = @"Docker\Docker\resources\bin\docker.exe";

    /// Primeiro pipe do engine que existe; null ⇒ engine do Docker Desktop não está de pé
    /// (Unavailable sem nem rodar o CLI).
    public static string? ChooseHost(IEnumerable<string> existingPipeNames)
    {
        var existing = new HashSet<string>(existingPipeNames.Select(PipeName), StringComparer.OrdinalIgnoreCase);
        var pipe = EnginePipes.FirstOrDefault(existing.Contains);
        return pipe is null ? null : $"npipe:////./pipe/{pipe}";
    }

    /// Onde procurar docker.exe: cada pasta do PATH e depois a instalação padrão do Docker Desktop.
    public static IReadOnlyList<string> CandidatePaths(string? pathVariable, string? programFiles)
    {
        var result = new List<string>();
        foreach (var dir in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(Path.Combine(dir.Trim('"'), "docker.exe"));
        if (!string.IsNullOrWhiteSpace(programFiles))
            result.Add(Path.Combine(programFiles, DefaultInstallSuffix));
        return result;
    }

    /// Argumentos de `docker --host <pipe> ps -q`.
    public static IReadOnlyList<string> Arguments(string host) => ["--host", host, "ps", "-q"];

    /// Ocioso ⇔ saiu com 0 e stdout vazio. Saída não vazia ⇒ ocupado. Erro ou timeout ⇒
    /// indisponível (o Docker fica rodando).
    public static DockerProbe Interpret(int? exitCode, string? stdout, bool timedOut)
    {
        if (timedOut || exitCode is not 0) return DockerProbe.Unavailable;
        return string.IsNullOrWhiteSpace(stdout) ? DockerProbe.Idle : DockerProbe.Busy;
    }

    private static string PipeName(string path)
    {
        var p = path.Replace('/', '\\');
        var i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }
}
