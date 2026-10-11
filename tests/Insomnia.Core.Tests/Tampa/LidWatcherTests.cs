// LidWatcher: debounce de 2 s pelo IScheduler, descarte de oscilação, entrega numerada em série.
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;

namespace Insomnia.Core.Tests.Tampa;

public class LidWatcherTests
{
    private static TampaHarness NewHarness() => new(Config.Default with { EnergySaverOnLidClose = false });

    [Fact]
    public async Task DebounceDescartaOscilacaoQueVoltaAoEstadoEntregue()
    {
        using var h = NewHarness();
        await h.StartSessionAsync();
        var w = new LidWatcher(h.Power, h.Scheduler, h.Actions, h.Log);
        w.Start();

        h.Power.RaiseLid(LidState.Closed);
        h.Scheduler.Advance(TimeSpan.FromSeconds(1.5));
        h.Power.RaiseLid(LidState.Open);
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        await w.Idle;

        Assert.Equal(0, w.EventCount);
        Assert.Equal(LidState.Open, w.Current);
        Assert.Equal(0, h.Display.TurnOffCount);
        Assert.False(h.Manager.CountdownPaused);
    }

    [Fact]
    public async Task MudancaEstavelPor2sEEntregueENumerada()
    {
        using var h = NewHarness();
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        var w = new LidWatcher(h.Power, h.Scheduler, h.Actions, h.Log);
        w.Start();

        h.Power.RaiseLid(LidState.Closed);
        h.Scheduler.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(0, w.EventCount);
        h.Scheduler.Advance(TimeSpan.FromSeconds(0.1));
        await w.Idle;

        Assert.Equal(1, w.EventCount);
        Assert.Equal(1, h.Actions.LatestEvent);
        Assert.Equal(LidState.Closed, w.Current);
        Assert.Equal(1, h.Display.TurnOffCount);
        Assert.Contains(100, h.World.Suspended);
        Assert.True(h.Manager.CountdownPaused);

        h.Power.RaiseLid(LidState.Open);
        h.Scheduler.Advance(LidWatcher.Debounce);
        await w.Idle;

        Assert.Equal(2, w.EventCount);
        Assert.Empty(h.World.Suspended);
        Assert.False(h.Manager.CountdownPaused);
    }

    [Fact]
    public async Task NovaMudancaDuranteAEsperaReiniciaOsDoisSegundos()
    {
        using var h = NewHarness();
        await h.StartSessionAsync();
        var w = new LidWatcher(h.Power, h.Scheduler, h.Actions, h.Log);
        w.Start();

        h.Power.RaiseLid(LidState.Closed);
        h.Scheduler.Advance(TimeSpan.FromSeconds(1));
        h.Power.RaiseLid(LidState.Open);    // volta: descartado
        h.Scheduler.Advance(TimeSpan.FromSeconds(0.5));
        h.Power.RaiseLid(LidState.Closed);  // de novo: conta 2 s a partir daqui
        h.Scheduler.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(0, w.EventCount);
        h.Scheduler.Advance(TimeSpan.FromSeconds(0.1));
        await w.Idle;

        Assert.Equal(1, w.EventCount);
        Assert.Equal(1, h.Display.TurnOffCount);
    }

    [Fact]
    public async Task EstadoDesconhecidoNaoEEntregueEStopDesliga()
    {
        using var h = NewHarness();
        await h.StartSessionAsync();
        var w = new LidWatcher(h.Power, h.Scheduler, h.Actions, h.Log);
        w.Start();

        h.Power.RaiseLid(LidState.Unknown);
        h.Scheduler.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, w.EventCount);

        h.Power.RaiseLid(LidState.Closed);
        w.Stop();
        h.Scheduler.Advance(TimeSpan.FromSeconds(3));
        await w.Idle;

        Assert.Equal(0, w.EventCount);
        Assert.Equal(0, h.Display.TurnOffCount);
    }

    [Fact]
    public async Task MontadoComoOAppOObservadorDaOSchedulerEOEstadoInicialSemEvento()
    {
        using var h = NewHarness();
        await h.StartSessionAsync();
        // Construtor do contrato (10 parâmetros, sem scheduler): o LidWatcher liga o dele.
        using var bare = new LidActions(h.Manager, h.Config, h.World, h.World, h.Audio, h.Display, h.Docker, h.Floors, h.Clock, h.Log);
        var w = new LidWatcher(h.Power, h.Scheduler, bare, h.Log);
        h.Power.CurrentLid = LidState.Closed;
        w.Start();

        Assert.Equal(LidState.Closed, bare.Lid);
        Assert.Equal(LidState.Closed, w.Current);
        Assert.Equal(0, w.EventCount);

        // Abrir agenda a conferência de áudio de 30 s no scheduler do observador.
        h.Power.RaiseLid(LidState.Open);
        h.Scheduler.Advance(LidWatcher.Debounce);
        await w.Idle;
        Assert.Contains(LidActions.AudioRetryInterval, h.Scheduler.PendingDelays);
    }
}
