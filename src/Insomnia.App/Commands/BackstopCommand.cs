// Modo --backstop [--force] (desenho §6.2 "Backstop", R9): o mesmo exe, chamado pelo
// Agendador a cada minuto. Compõe só o necessário para desfazer (armazenamento, lock,
// energia, LidUndo) e entrega ao BackstopRunner. Sem NotifyIcon, sem janela, sem
// notificação: só log. Código de saída = o do runner (0 ok · 1 falhas · 75 lock ocupado).
using Insomnia.App.Composition;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Backstop;
using Insomnia.Core.Lid;
using Insomnia.Core.Session;
using Insomnia.Core.Store;
using Insomnia.Core.Time;
using Insomnia.Windows.Audio;
using Insomnia.Windows.Display;
using Insomnia.Windows.Power;
using Insomnia.Windows.Processes;

namespace Insomnia.App.Commands;

public static class BackstopCommand
{
    /// Abaixo do ExecutionTimeLimit da tarefa (PT2M, R11): o runner termina antes de ser morto.
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(100);

    public static bool IsForce(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a.Trim(), Program.ForceArg, StringComparison.OrdinalIgnoreCase));

    /// O LidUndo do backstop nunca acorda a tela: ele roda a cada minuto, e um diário com
    /// resíduo permanente (saída desconectada, processo não verificável) acenderia o monitor
    /// a cada minuto, sem prazo. Separado de Run para o teste provar a composição.
    public static LidUndo CreateLidUndo(IProcessInspector inspector, IProcessSignals signals, IAudioControl audio, IDisplayPower display, ILog log) =>
        new(inspector, signals, audio, display, log, wakeDisplay: false);

    public static int Run(string[] args)
    {
        var paths = Paths.FromEnvironment();
        var log = new FileLog(paths.LogFile);
        var force = IsForce(args);
        IDisposable? audioOwner = null;
        SleepGuardWin? sleepGuard = null;
        try
        {
            var clock = SystemClock.Instance;
            var store = new FileStore(paths, clock, log);
            var @lock = new FileRecoveryLock(paths.LockFile);
            sleepGuard = new SleepGuardWin(log);
            var energySaver = new EnergySaverWin(log);
            var audio = AppHost.OpenAudio(log, out audioOwner);
            var lidUndo = CreateLidUndo(new ProcessInspectorWin(), new ProcessSignalsWin(), audio, new DisplayPowerWin(log), log);
            var engine = new RecoveryEngine(sleepGuard, energySaver, lidUndo, clock, log);
            var runner = new BackstopRunner(store, @lock, engine, clock, log);

            using var cts = new CancellationTokenSource(Deadline);
            return runner.RunAsync(force, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            log.Error($"backstop: passou de {Deadline.TotalSeconds:0} s; evidência mantida");
            return BackstopRunner.ExitFailed;
        }
        catch (Exception ex)
        {
            log.Error($"backstop: falhou antes de terminar ({ex.GetType().Name}: {ex.Message}); evidência mantida");
            return BackstopRunner.ExitFailed;
        }
        finally
        {
            audioOwner?.Dispose();
            sleepGuard?.Dispose();
        }
    }
}
