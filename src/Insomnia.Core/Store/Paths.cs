namespace Insomnia.Core.Store;

/// Onde cada arquivo do app mora (desenho §3).
///
/// Padrão: config em %APPDATA%\Insomnia (pode roamear); diário, sessão, lock, logs e
/// simulate-lid em %LOCALAPPDATA%\Insomnia (nunca roamear: pids e GUID de esquema são
/// desta máquina). INSOMNIA_HOME=&lt;dir&gt; relocaliza TUDO isso para dentro de &lt;dir&gt;; é o
/// mecanismo dos testes e dos ensaios manuais. O exe instalado não é dado e não se move.
public sealed class Paths
{
    public const string HomeVariable = "INSOMNIA_HOME";
    public const string AppFolderName = "Insomnia";
    public const string UnreadableSessionPrefix = "session.json.unreadable-";

    public Paths(string configDirectory, string dataDirectory)
    {
        ConfigDirectory = Path.GetFullPath(configDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// Pasta do config.json.
    public string ConfigDirectory { get; }
    /// Pasta local (não roameia) do diário, sessão, lock, logs e simulate-lid.
    public string DataDirectory { get; }

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public string JournalFile => Path.Combine(DataDirectory, "state.json");
    public string SessionFile => Path.Combine(DataDirectory, "session.json");
    public string LockFile => Path.Combine(DataDirectory, ".recovery.lock");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public string LogFile => Path.Combine(LogsDirectory, "insomnia.log");
    public string HandoffsLogFile => Path.Combine(LogsDirectory, "handoffs.log");
    public string SimulateLidFile => Path.Combine(DataDirectory, "simulate-lid");

    /// Tudo dentro de uma pasta só (o que INSOMNIA_HOME faz).
    public static Paths UnderHome(string home) => new(home, home);

    /// Resolve pelo ambiente: INSOMNIA_HOME se definido e não vazio; senão as pastas do usuário.
    /// getEnv existe para teste (padrão: Environment.GetEnvironmentVariable).
    public static Paths FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var home = getEnv(HomeVariable);
        if (!string.IsNullOrWhiteSpace(home)) return UnderHome(home.Trim());
        return new Paths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));
    }

    /// %LOCALAPPDATA%\Programs\Insomnia\Insomnia.exe (onde o instalador põe o exe).
    public static string InstalledExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppFolderName, "Insomnia.exe");
}
