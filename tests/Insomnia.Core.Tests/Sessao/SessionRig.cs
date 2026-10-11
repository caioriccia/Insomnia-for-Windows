// Montagem comum dos testes de sessão: SessionManager real sobre os fakes de §5.1, com uma
// Timeline compartilhada para provar ORDEM entre efeitos de fakes diferentes.
using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

// Dentro do namespace: fora, "Session" acharia o namespace Insomnia.Core.Session.
using Insomnia.Core.Model;
using Insomnia.Core.Session;

public sealed class SessionRig
{
    public static readonly BackstopDefinition Definition = BackstopDefinition.For(@"C:\Programs\Insomnia\Insomnia.exe");

    public Timeline Timeline { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeScheduler Scheduler { get; }
    public FakeStore Store { get; }
    public FakeRecoveryLock Lock { get; } = new();
    public FakeLog Log { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeSleepGuard Sleep { get; }
    public FakeEnergySaver Energy { get; }
    public FakeBackstopScheduler Backstop { get; }
    public FakeLidUndo LidUndo { get; }
    public FakeConfigSource Config { get; } = new();
    public LidState Lid { get; set; } = LidState.Open;

    /// Decoradores com gancho: deixam o teste agir NO MEIO de uma transação.
    public HookedSleepGuard HookedSleep { get; }
    public HookedBackstopScheduler HookedBackstop { get; }

    public SessionManager Manager { get; }

    public SessionRig()
    {
        Scheduler = new FakeScheduler(Clock);
        Store = new FakeStore(Timeline);
        Sleep = new FakeSleepGuard(Timeline);
        Energy = new FakeEnergySaver(Timeline);
        Backstop = new FakeBackstopScheduler(Timeline);
        LidUndo = new FakeLidUndo(Timeline);
        HookedSleep = new HookedSleepGuard(Sleep);
        HookedBackstop = new HookedBackstopScheduler(Backstop);
        Manager = new SessionManager(Deps());
    }

    public SessionDeps Deps() => new(Store, Lock, Clock, Scheduler, Log, Notifier, HookedSleep, Energy, HookedBackstop,
        Definition, LidUndo, () => Lid, Config);

    public RecoveryEngine Engine() => new(Sleep, Energy, LidUndo, Clock, Log);

    public Session ValidSession(TimeSpan? remaining = null) =>
        new(Clock.Now - TimeSpan.FromHours(1), Clock.Now + (remaining ?? TimeSpan.FromHours(2)), []);

    public Session ExpiredSession() => new(Clock.Now - TimeSpan.FromHours(3), Clock.Now - TimeSpan.FromMinutes(5), []);

    public static Journal WithLidAction(uint ac = 1, uint dc = 1) =>
        Journal.Clean with { LidAction = new LidActionEntry(FakeSchemes.Balanced, ac, dc, FakeClock.DefaultStart) };

    public static FrozenProcess Frozen(int pid = 4312) =>
        new(pid, "slack.exe", FakeClock.DefaultStart.AddHours(-9), "17", "slack.exe");

    /// Espera uma condição que depende de trabalho assíncrono (retentativas disparadas por timer).
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condição não ficou verdadeira");
            await Task.Delay(10);
        }
    }

    /// Roda uma ação FORA do contexto assíncrono atual, como se viesse de outra fonte (outro
    /// clique, um timer): sem isso, uma chamada feita de dentro de um gancho herdaria a
    /// transação em curso e rodaria em linha, em vez de esperar na fila.
    public static T Outside<T>(ExecutionContext clean, Func<T> action)
    {
        T result = default!;
        ExecutionContext.Run(clean.CreateCopy(), _ => result = action(), null);
        return result;
    }
}

/// ISleepGuard que repassa ao FakeSleepGuard e chama Hook ANTES de cada chamada.
/// Hook recebe "Read", "Write(ac,dc)" ou "Hold(true|false)".
public sealed class HookedSleepGuard(FakeSleepGuard inner) : ISleepGuard
{
    public Action<string>? Hook { get; set; }
    public bool IsHoldingSystemAwake => inner.IsHoldingSystemAwake;
    public void HoldSystemAwake(bool hold) { Hook?.Invoke($"Hold({hold})"); inner.HoldSystemAwake(hold); }
    public LidActionValues ReadLidAction() { Hook?.Invoke("Read"); return inner.ReadLidAction(); }
    public void WriteLidAction(Guid schemeId, uint ac, uint dc) { Hook?.Invoke($"Write({ac},{dc})"); inner.WriteLidAction(schemeId, ac, dc); }
}

/// IBackstopScheduler que repassa ao fake e chama Hook antes de "Query"/"Install".
public sealed class HookedBackstopScheduler(FakeBackstopScheduler inner) : IBackstopScheduler
{
    public Action<string>? Hook { get; set; }
    public BackstopState Query(BackstopDefinition want) { Hook?.Invoke("Query"); return inner.Query(want); }
    public void Install(BackstopDefinition want) { Hook?.Invoke("Install"); inner.Install(want); }
    public void Remove(string taskName) => inner.Remove(taskName);
}
