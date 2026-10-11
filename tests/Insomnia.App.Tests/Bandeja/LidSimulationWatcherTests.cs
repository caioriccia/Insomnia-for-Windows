#if DEBUG || INSOMNIA_LID_SIMULATION
using Insomnia.App.Simulation;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.App.Tests.Bandeja;

/// Só uma pasta temporária: nenhum evento real de energia é tocado (FakePowerEvents por baixo).
public sealed class LidSimulationWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "insomnia-lidsim-" + Guid.NewGuid().ToString("N"));
    private readonly FakePowerEvents _real = new() { CurrentLid = LidState.Open };
    private readonly FakeScheduler _scheduler = new();
    private readonly LidSimulationWatcher _watcher;
    private readonly List<LidState> _events = [];

    public LidSimulationWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _watcher = new LidSimulationWatcher(_real, Path.Combine(_dir, "simulate-lid"), _scheduler, new FakeLog());
        _watcher.LidChanged += _events.Add;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("closed", LidState.Closed)]
    [InlineData(" CLOSED\n", LidState.Closed)]
    [InlineData("open", LidState.Open)]
    [InlineData("fechada", LidState.Closed)]
    [InlineData("aberta", LidState.Open)]
    public void Parse_aceita_closed_e_open(string text, LidState expected)
    {
        Assert.Equal(expected, LidSimulationWatcher.Parse(text));
    }

    [Fact]
    public void Parse_recusa_o_resto()
    {
        Assert.Null(LidSimulationWatcher.Parse("meio"));
    }

    [Fact]
    public void Arquivo_e_consumido_e_vira_evento_de_tampa()
    {
        _watcher.Start();
        File.WriteAllText(_watcher.FilePath, "closed");

        _scheduler.Advance(LidSimulationWatcher.PollInterval);

        Assert.Equal([LidState.Closed], _events);
        Assert.Equal(LidState.Closed, _watcher.CurrentLid);
        Assert.False(File.Exists(_watcher.FilePath));
        Assert.Empty(Directory.GetFiles(_dir));   // nada de "consumed" sobrando
    }

    [Fact]
    public void Sem_arquivo_nada_acontece_e_segue_vigiando()
    {
        _watcher.Start();
        _scheduler.Advance(TimeSpan.FromSeconds(3));

        Assert.Empty(_events);
        Assert.Equal(LidState.Open, _watcher.CurrentLid);
        Assert.Equal(1, _scheduler.PendingCount);
    }

    [Fact]
    public void Evento_real_de_tampa_derruba_a_simulacao_e_passa_adiante()
    {
        _watcher.Start();
        File.WriteAllText(_watcher.FilePath, "closed");
        _watcher.PollOnce();

        _real.RaiseLid(LidState.Open);

        Assert.Equal([LidState.Closed, LidState.Open], _events);
        Assert.Null(_watcher.Simulated);
        Assert.Equal(LidState.Open, _watcher.CurrentLid);
    }

    [Fact]
    public void Stop_para_de_vigiar_e_desliga_o_real()
    {
        _watcher.Start();
        Assert.True(_real.Started);

        _watcher.Stop();
        File.WriteAllText(_watcher.FilePath, "closed");
        _scheduler.Advance(TimeSpan.FromSeconds(3));

        Assert.False(_real.Started);
        Assert.Empty(_events);
        Assert.True(File.Exists(_watcher.FilePath));
    }
}
#endif
