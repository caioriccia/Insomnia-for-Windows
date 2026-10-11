// Arnês da tampa: SessionManager e FloorRuleDriver REAIS sobre os fakes de §5.1, LidUndo
// real como ILidUndo do manager, e LidActions/LidWatcher montados como o app monta.
// Uma Timeline compartilhada prova a ORDEM entre diário e efeito (R1).
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Power;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Tampa;

using Insomnia.Core.Session;

public sealed class TampaHarness : IDisposable
{
    /// Criação com fração de segundo de propósito: o diário guarda segundos inteiros.
    public static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
    public static readonly PlannerEnvironment Env = new(OwnPid: 9999, OwnExeName: "Insomnia.exe", WindowsDirectory: @"C:\Windows");

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
    public FakePowerEvents Power { get; } = new();
    public FakeConfigSource Config { get; }
    public FakeProcessWorld World { get; }
    public FakeAudio Audio { get; }
    public FakeDisplay Display { get; }
    public FakeDocker Docker { get; }
    public LidUndo Undo { get; }
    public SessionManager Manager { get; }
    public FloorRuleDriver Floors { get; }
    public LidActions Actions { get; }

    private int _events;

    public TampaHarness(Config? config = null)
    {
        Scheduler = new FakeScheduler(Clock);
        Store = new FakeStore(Timeline);
        Sleep = new FakeSleepGuard(Timeline);
        Energy = new FakeEnergySaver(Timeline);
        Backstop = new FakeBackstopScheduler(Timeline);
        Config = new FakeConfigSource(config ?? Model.Config.Default);
        World = new FakeProcessWorld(Timeline);
        Audio = new FakeAudio(Timeline);
        Display = new FakeDisplay(Timeline);
        Docker = new FakeDocker(Timeline);
        Undo = new LidUndo(World, World, Audio, Display, Log);
        Manager = new SessionManager(new SessionDeps(Store, Lock, Clock, Scheduler, Log, Notifier, Sleep, Energy, Backstop,
            BackstopDefinition.For(@"C:\Insomnia.exe"), Undo, () => Power.CurrentLid, Config));
        Floors = new FloorRuleDriver(Manager, Power, Scheduler, Config, () => Power.CurrentLid, Log);
        Actions = new LidActions(Manager, Config, World, World, Audio, Display, Docker, Floors, Clock, Log, Scheduler, Env);
    }

    public Journal Disk => Store.JournalOnDisk ?? Journal.Clean;

    public async Task StartSessionAsync()
    {
        var outcome = await Manager.StartAsync(TimeSpan.FromHours(4));
        Assert.Equal(StartOutcome.Started, outcome);
        Assert.True(Manager.IsActive);
    }

    /// Processo com identidade (criação = T0 + sec).
    public ProcessInfo AddProcess(int pid, string name, int parent = 1000, double sec = 0, bool visible = false, string? path = null) =>
        World.Add(pid, name, parent, T0.AddSeconds(sec), visible, path);

    /// Simula a entrega de um evento pelo LidWatcher (sem debounce) e devolve o número.
    public int Deliver(LidState state)
    {
        var n = ++_events;
        Power.CurrentLid = state;
        Actions.NoteEvent(n, state);
        return n;
    }

    public async Task CloseAsync() => await Actions.OnCloseAsync(Deliver(LidState.Closed));
    public async Task OpenAsync() => await Actions.OnOpenAsync(Deliver(LidState.Open));

    public void Dispose() => Actions.Dispose();
}
