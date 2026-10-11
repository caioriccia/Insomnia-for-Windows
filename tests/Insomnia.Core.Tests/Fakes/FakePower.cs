// Fakes de energia, tampa e backstop (desenho §5.1).
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Tests.Fakes;

public static class FakeSchemes
{
    /// Esquema "Equilibrado" padrão do Windows.
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
}

/// Lid = LIDACTION atual (padrão: Equilibrado, suspender em AC e DC). Holding = hold de
/// execução ligado. Writes = cada WriteLidAction BEM-SUCEDIDA. FailWrite/FailRead fazem as
/// chamadas lançarem InvalidOperationException; FailWriteIf falha só a escrita escolhida.
public sealed class FakeSleepGuard(Timeline? timeline = null) : ISleepGuard
{
    public LidActionValues Lid { get; set; } = new(FakeSchemes.Balanced, 1, 1);
    public bool Holding { get; set; }
    public List<LidActionValues> Writes { get; } = [];
    public List<bool> HoldCalls { get; } = [];
    public bool FailWrite { get; set; }
    public bool FailRead { get; set; }
    public Func<LidActionValues, bool>? FailWriteIf { get; set; }

    public bool IsHoldingSystemAwake => Holding;

    public void HoldSystemAwake(bool hold)
    {
        timeline?.Add($"sleep.Hold({hold})");
        HoldCalls.Add(hold);
        Holding = hold;
    }

    public LidActionValues ReadLidAction()
    {
        timeline?.Add("sleep.ReadLidAction");
        if (FailRead) throw new InvalidOperationException("leitura de LIDACTION falhou (fake)");
        return Lid;
    }

    public void WriteLidAction(Guid schemeId, uint ac, uint dc)
    {
        var values = new LidActionValues(schemeId, ac, dc);
        timeline?.Add($"sleep.WriteLidAction({ac},{dc})");
        if (FailWrite || (FailWriteIf?.Invoke(values) ?? false))
            throw new InvalidOperationException("escrita de LIDACTION falhou (fake)");
        Lid = values;
        Writes.Add(values);
    }
}

/// Current = ESBATTTHRESHOLD atual (padrão 20 no Equilibrado). Writes = escritas bem-sucedidas.
public sealed class FakeEnergySaver(Timeline? timeline = null) : IEnergySaver
{
    public EnergySaverValues Current { get; set; } = new(FakeSchemes.Balanced, 20);
    public List<EnergySaverValues> Writes { get; } = [];
    public bool FailWrite { get; set; }
    public bool FailRead { get; set; }
    public Func<EnergySaverValues, bool>? FailWriteIf { get; set; }

    public EnergySaverValues Read()
    {
        timeline?.Add("energy.Read");
        if (FailRead) throw new InvalidOperationException("leitura de ESBATTTHRESHOLD falhou (fake)");
        return Current;
    }

    public void WriteDcThreshold(Guid schemeId, uint threshold)
    {
        var values = new EnergySaverValues(schemeId, threshold);
        timeline?.Add($"energy.WriteDcThreshold({threshold})");
        if (FailWrite || (FailWriteIf?.Invoke(values) ?? false))
            throw new InvalidOperationException("escrita de ESBATTTHRESHOLD falhou (fake)");
        Current = values;
        Writes.Add(values);
    }
}

/// RaiseLid muda CurrentLid e dispara LidChanged; RaisePower troca Power (se dado) e dispara
/// PowerChanged. Power padrão: na tomada, 80%. ReadPowerCalls conta leituras.
public sealed class FakePowerEvents : IPowerEvents
{
    public event Action<LidState>? LidChanged;
    public event Action? PowerChanged;

    public LidState CurrentLid { get; set; } = LidState.Open;
    public PowerStatus Power { get; set; } = new(OnAc: true, BatteryPercent: 80);
    public bool Started { get; private set; }
    public int ReadPowerCalls { get; private set; }

    public PowerStatus ReadPower() { ReadPowerCalls++; return Power; }
    public void Start() => Started = true;
    public void Stop() => Started = false;

    public void RaiseLid(LidState state)
    {
        CurrentLid = state;
        LidChanged?.Invoke(state);
    }

    public void RaisePower(PowerStatus? power = null)
    {
        if (power is { } p) Power = p;
        PowerChanged?.Invoke();
    }
}

/// State = o que Query devolve (padrão Missing). Install bem-sucedido põe State =
/// StateAfterInstall (padrão Armed). FailInstall faz Install lançar.
public sealed class FakeBackstopScheduler(Timeline? timeline = null) : IBackstopScheduler
{
    public BackstopState State { get; set; } = BackstopState.Missing;
    public BackstopState StateAfterInstall { get; set; } = BackstopState.Armed;
    public bool FailInstall { get; set; }
    public List<BackstopDefinition> Installs { get; } = [];
    public List<BackstopDefinition> Queries { get; } = [];
    public List<string> Removes { get; } = [];

    public BackstopState Query(BackstopDefinition want)
    {
        timeline?.Add("backstop.Query");
        Queries.Add(want);
        return State;
    }

    public void Install(BackstopDefinition want)
    {
        timeline?.Add("backstop.Install");
        if (FailInstall) throw new InvalidOperationException("instalação da tarefa falhou (fake)");
        Installs.Add(want);
        State = StateAfterInstall;
    }

    public void Remove(string taskName)
    {
        timeline?.Add("backstop.Remove");
        Removes.Add(taskName);
        State = BackstopState.Missing;
    }
}

/// ILidUndo controlável. Padrão: "desfaz tudo" (tira processos, Docker e áudio do diário por
/// tx.Write) e devolve sem problemas. Problems não vazio ⇒ não limpa nada e devolve esses
/// problemas. NotConnected ⇒ saídas com esses ids ficam no diário e voltam em
/// AudioDevicesNotConnected. Nunca lança (como o contrato): falha de escrita vira problema.
public sealed class FakeLidUndo(Timeline? timeline = null) : ILidUndo
{
    public int Calls { get; private set; }
    public List<string> Problems { get; } = [];
    public HashSet<string> NotConnected { get; } = [];
    /// Comportamento próprio, se o teste quiser controlar tudo.
    public Func<JournalTransaction, LidUndoResult>? Behavior { get; set; }

    public Task<LidUndoResult> UndoAsync(JournalTransaction tx, CancellationToken ct)
    {
        timeline?.Add("lidUndo.Undo");
        Calls++;
        if (Behavior is not null) return Task.FromResult(Behavior(tx));
        var notConnected = new HashSet<string>(NotConnected);
        if (Problems.Count > 0) return Task.FromResult(new LidUndoResult([.. Problems], notConnected));
        var problems = new List<string>();
        var j = tx.Journal;
        var cleaned = j with
        {
            FrozenProcesses = [],
            DockerFrozen = false,
            SavedAudioOutputs = [.. j.SavedAudioOutputs.Where(a => notConnected.Contains(a.DeviceId))],
        };
        var changes = j.FrozenProcesses.Length > 0 || j.DockerFrozen
            || j.SavedAudioOutputs.Any(a => !notConnected.Contains(a.DeviceId));
        if (changes)
        {
            try { tx.Write(cleaned); }
            catch (StoreWriteException ex) { problems.Add(ex.Message); }
        }
        return Task.FromResult(new LidUndoResult(problems, notConnected));
    }
}
