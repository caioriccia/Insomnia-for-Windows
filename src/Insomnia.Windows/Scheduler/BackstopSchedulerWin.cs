using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Scheduler;

/// IBackstopScheduler real: tarefa na RAIZ do Agendador do usuário atual, via COM
/// `Schedule.Service` (late-bound, `dynamic`), sem administrador.
/// - Query: lê o XML da tarefa registrada e decide com BackstopTaskXml.Matches (puro).
///   Tarefa inexistente ⇒ Missing; desabilitada ⇒ Different; QUALQUER exceção ⇒ Unknown
///   (o Core reinstala: fail-open, §10.5).
/// - Install: RegisterTaskDefinition-equivalente `RegisterTask(nome, xml,
///   TASK_CREATE_OR_UPDATE=6, null, null, TASK_LOGON_INTERACTIVE_TOKEN=3)`. Se o COM falhar,
///   cai no fallback documentado em §7: `schtasks /create /f /xml`. Os dois falhando ⇒ lança.
/// - Remove: idempotente (inexistente = sucesso); outra falha lança.
public sealed class BackstopSchedulerWin : IBackstopScheduler
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(15);

    private readonly ILog _log;

    public BackstopSchedulerWin(ILog log) { _log = log; }

    public BackstopState Query(BackstopDefinition want)
    {
        try
        {
            return WithRootFolder(folder =>
            {
                object? task = null;
                try
                {
                    try { task = folder.GetTask(want.TaskName); }
                    catch (Exception e) when (IsNotFound(e)) { return BackstopState.Missing; }
                    dynamic t = task!;
                    if (!(bool)t.Enabled) return BackstopState.Different;
                    string xml = t.Xml;
                    return BackstopTaskXml.Matches(xml, want);
                }
                finally { Release(task); }
            });
        }
        catch (Exception e)
        {
            _log.Error($"backstop: não consegui consultar a tarefa \"{want.TaskName}\": {e.Message}");
            return BackstopState.Unknown;
        }
    }

    public void Install(BackstopDefinition want)
    {
        var xml = BackstopTaskXml.Build(want, DateTime.Now);
        try
        {
            WithRootFolder<object?>(folder =>
            {
                object? registered = folder.RegisterTask(want.TaskName, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
                Release(registered);
                return null;
            });
            _log.Info($"backstop: tarefa \"{want.TaskName}\" instalada ({want.ExecutablePath} {want.Arguments})");
        }
        catch (Exception comError)
        {
            _log.Error($"backstop: instalação via COM falhou ({comError.Message}); tentando schtasks");
            try
            {
                InstallWithSchtasks(want.TaskName, xml);
                _log.Info($"backstop: tarefa \"{want.TaskName}\" instalada via schtasks");
            }
            catch (Exception fallbackError)
            {
                _log.Error($"backstop: schtasks também falhou: {fallbackError.Message}");
                throw new InvalidOperationException(
                    $"não foi possível instalar a tarefa \"{want.TaskName}\" (COM: {comError.Message}; schtasks: {fallbackError.Message})",
                    comError);
            }
        }
    }

    public void Remove(string taskName)
    {
        try
        {
            WithRootFolder<object?>(folder =>
            {
                try { folder.DeleteTask(taskName, 0); }
                catch (Exception e) when (IsNotFound(e)) { }
                return null;
            });
            _log.Info($"backstop: tarefa \"{taskName}\" removida (ou já não existia)");
        }
        catch (Exception e)
        {
            _log.Error($"backstop: não consegui remover a tarefa \"{taskName}\": {e.Message}");
            throw new InvalidOperationException($"não foi possível remover a tarefa \"{taskName}\": {e.Message}", e);
        }
    }

    /// Conecta ao serviço, abre a pasta raiz e solta os objetos COM no fim.
    private static T WithRootFolder<T>(Func<dynamic, T> body)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Schedule.Service não instanciou");
            dynamic s = service;
            s.Connect();
            folder = s.GetFolder("\\");
            return body(folder!);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    /// ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND como HRESULT, na exceção ou na interna.
    private static bool IsNotFound(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003)) return true;
        return false;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    private static void InstallWithSchtasks(string taskName, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"insomnia-backstop-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);   // UTF-16 com BOM, casa com a declaração
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "/Create", "/TN", taskName, "/XML", file, "/F" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("schtasks não iniciou");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new TimeoutException($"schtasks não terminou em {SchtasksTimeout.TotalSeconds:0} s");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"schtasks saiu com {process.ExitCode}: {(stderr.Result + stdout.Result).Trim()}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
