// Ponto de entrada (desenho §9): um exe, quatro modos.
//   sem argumentos             → UI de bandeja, instância única (Mutex "Local\Insomnia.Ui")
//   --backstop [--force]       → BackstopCommand.Run (o Agendador chama a cada minuto)
//   --diagnostico [...]        → DiagnosticsCommand.Run (frente diagnostico-instalador)
//   --remover-backstop         → apaga a tarefa "Insomnia Backstop" (usado pelo desinstalador)
// Qualquer outra coisa sai com 2 e uma linha no log (é WinExe: não há console para avisar).
using Insomnia.App.Commands;
using Insomnia.App.Composition;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Store;
using Insomnia.Windows.Scheduler;

namespace Insomnia.App;

public enum LaunchMode { Ui, Backstop, Diagnostics, RemoveBackstop, Invalid }

public static class Program
{
    public const string BackstopArg = "--backstop";
    public const string ForceArg = "--force";
    public const string DiagnosticsArg = "--diagnostico";
    public const string RemoveBackstopArg = "--remover-backstop";
    public const int ExitUsage = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return ParseMode(args) switch
            {
                LaunchMode.Ui => RunUi(),
                LaunchMode.Backstop => BackstopCommand.Run(args),
                LaunchMode.Diagnostics => DiagnosticsCommand.Run(args),
                LaunchMode.RemoveBackstop => RemoveBackstop(),
                _ => Usage(args),
            };
        }
        catch (Exception ex)
        {
            // Última rede: o motivo fica no log em vez de sumir com o processo.
            NewLog().Error($"falha fatal ({ex.GetType().Name}): {ex.Message}");
            return 1;
        }
    }

    /// O primeiro argumento decide o modo (sem diferenciar caixa). --backstop só aceita --force
    /// depois; --remover-backstop não aceita nada; --diagnostico repassa o resto.
    public static LaunchMode ParseMode(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return LaunchMode.Ui;
        var first = args[0].Trim();
        var rest = args.Skip(1).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (Is(first, BackstopArg))
            return rest.All(a => Is(a, ForceArg)) ? LaunchMode.Backstop : LaunchMode.Invalid;
        if (Is(first, DiagnosticsArg)) return LaunchMode.Diagnostics;
        if (Is(first, RemoveBackstopArg)) return rest.Count == 0 ? LaunchMode.RemoveBackstop : LaunchMode.Invalid;
        if (first.Length == 0 && rest.Count == 0) return LaunchMode.Ui;
        return LaunchMode.Invalid;
    }

    private static bool Is(string arg, string expected) => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);

    private static FileLog NewLog() => new(Paths.FromEnvironment().LogFile);

    private static int RunUi()
    {
        using var instance = SingleInstance.TryClaim();
        if (instance is null) return 0;   // outra instância abre o popover

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var host = new AppHost();
        Application.ThreadException += (_, e) => host.Log.Error($"erro na UI: {e.Exception.GetType().Name}: {e.Exception.Message}");
        instance.OnShowRequested(host.ShowPopover);
        host.Start();
        Application.Run(host);
        return 0;
    }

    private static int RemoveBackstop()
    {
        ILog log = NewLog();
        try
        {
            var name = BackstopDefinition.For(AppHost.ExecutablePath).TaskName;
            new BackstopSchedulerWin(log).Remove(name);
            log.Info($"tarefa \"{name}\" removida");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error($"não deu para remover o backstop: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(string[] args)
    {
        NewLog().Error($"argumentos não reconhecidos: {string.Join(' ', args)} " +
                       $"(use {BackstopArg} [{ForceArg}], {DiagnosticsArg} ou {RemoveBackstopArg})");
        return ExitUsage;
    }
}
