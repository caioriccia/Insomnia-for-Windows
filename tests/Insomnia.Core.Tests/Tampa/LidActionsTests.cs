// Fechar/abrir a tampa com SessionManager real (desenho §7.2; invariantes R1, R3, R8 de §8).
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Tampa;

using Insomnia.Core.Session;

public class LidActionsTests
{
    private static Config Cfg(Func<Config, Config>? change = null)
    {
        var c = Config.Default with { EnergySaverOnLidClose = false };
        return change is null ? c : change(c);
    }

    [Fact]
    public async Task R1_FreezeGravaEntradaProvisoriaNoDiarioAntesDoSuspendEDepoisPromove()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.AddProcess(101, "slack.exe", parent: 100, sec: 1);

        await h.CloseAsync();

        Assert.Contains(100, h.World.Suspended);
        Assert.Contains(101, h.World.Suspended);
        // Escrita provisória (sem identidade) existiu e veio ANTES do primeiro suspend.
        var provisional = h.Store.JournalWrites.FindIndex(j => j.FrozenProcesses.Any(f => f.Pid == 100 && !f.HasIdentity));
        var promoted = h.Store.JournalWrites.FindIndex(j => j.FrozenProcesses.Any(f => f.Pid == 100 && f.HasIdentity));
        Assert.True(provisional >= 0 && promoted > provisional);
        var ev = h.Timeline.Events;
        var firstSuspend = h.Timeline.IndexOf("process.Suspend(");
        var lastWriteBeforeSuspend = ev.Take(firstSuspend).ToList().FindLastIndex(e => e == "store.SaveJournal");
        Assert.True(lastWriteBeforeSuspend >= 0, "diário gravado antes do suspend");
        // No disco: as duas entradas promovidas, com identidade em segundos inteiros.
        var disk = h.Disk.FrozenProcesses;
        Assert.Equal([100, 101], disk.Select(f => f.Pid).Order());
        Assert.All(disk, f => Assert.True(f.HasIdentity));
        Assert.All(disk, f => Assert.Equal("slack.exe", f.Group));
        Assert.Equal(0, disk[0].CreationTime!.Value.Ticks % TimeSpan.TicksPerSecond);
        Assert.True(h.Manager.CountdownPaused);
    }

    [Fact]
    public async Task R1_EscritaProvisoriaFalhaNadaESinalizado()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.Store.FailJournalWriteIf = j => j.FrozenProcesses.Length > 0;

        await h.CloseAsync();

        Assert.DoesNotContain(h.World.Signals, s => s.Item1 == "suspend");
        Assert.Empty(h.World.Suspended);
        Assert.Contains(h.Manager.Warnings, w => w.Contains("slack.exe") && w.Contains("diário não gravou"));
    }

    [Fact]
    public async Task R1_EscritaDePromocaoFalhaRetomaNaHoraELimpaProvisorias()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.Store.FailJournalWriteIf = j => j.FrozenProcesses.Any(f => f.HasIdentity);

        await h.CloseAsync();

        Assert.Equal([("suspend", 100), ("resume", 100)], h.World.Signals);
        Assert.Empty(h.World.Suspended);
        Assert.Empty(h.Disk.FrozenProcesses);
        Assert.Contains(h.Manager.Warnings, w => w.Contains("retomados"));
    }

    [Fact]
    public async Task R8_PromocaoFalhaEResumeFalhaFicaProvisorioENuncaESinalizadoNaAbertura()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.Store.FailJournalWriteIf = j => j.FrozenProcesses.Any(f => f.HasIdentity);
        h.World.FailResume = true;

        await h.CloseAsync();

        var entry = Assert.Single(h.Disk.FrozenProcesses);
        Assert.False(entry.HasIdentity);
        Assert.Contains(h.Manager.Warnings, w => w.Contains("ficaram parados") && w.Contains("pid 100"));

        h.World.FailResume = false;
        h.Store.FailJournalWriteIf = null;
        var resumesBefore = h.World.Signals.Count(s => s.Item1 == "resume");
        await h.OpenAsync();

        Assert.Equal(resumesBefore, h.World.Signals.Count(s => s.Item1 == "resume"));
        Assert.Contains(100, h.World.Suspended);
        Assert.Single(h.Disk.FrozenProcesses);
        Assert.Contains(h.Manager.Warnings, w => w.Contains("não verificável"));
    }

    [Fact]
    public async Task JaSuspensoAntesDoFechamentoNaoENossoNemERetomado()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.World.Suspended.Add(100);

        await h.CloseAsync();
        await h.OpenAsync();

        Assert.Empty(h.World.Signals);
        Assert.Contains(100, h.World.Suspended);
        Assert.Empty(h.Disk.FrozenProcesses);
    }

    [Fact]
    public async Task TampaAbreAntesDeAAcaoDeFecharRodarNadaAcontece()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        var writes = h.Store.JournalWrites.Count;

        var close = h.Deliver(LidState.Closed);
        h.Deliver(LidState.Open);             // chegou antes de a ação de fechar rodar
        await h.Actions.OnCloseAsync(close);

        Assert.Equal(0, h.Display.TurnOffCount);
        Assert.Empty(h.Audio.Calls);
        Assert.Empty(h.World.Signals);
        Assert.Equal(writes, h.Store.JournalWrites.Count);
        Assert.False(h.Manager.CountdownPaused);
    }

    [Fact]
    public async Task SemSessaoFecharNaoFazNada()
    {
        using var h = new TampaHarness(Cfg());
        h.AddProcess(100, "slack.exe");

        await h.CloseAsync();

        Assert.Equal(0, h.Display.TurnOffCount);
        Assert.Empty(h.World.Signals);
        Assert.Empty(h.Store.JournalWrites);
    }

    [Fact]
    public async Task R1_AudioGravaNoDiarioAntesDeMutarEUmaEntradaPorSaida()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        var writesBefore = h.Store.JournalWrites.Count;
        var savesBefore = h.Timeline.Events.Count(e => e == "store.SaveJournal");

        await h.CloseAsync();
        Assert.True(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        var saved = Assert.Single(h.Disk.SavedAudioOutputs);
        Assert.Equal(new SavedAudioOutput(FakeAudio.SpeakersId, "Alto-falantes", 0.62f, false), saved);
        // Exatamente uma gravação (a do áudio) entre o início e o mudo.
        var mute = h.Timeline.IndexOf("audio.SetMute(True)");
        Assert.Equal(savesBefore + 1, h.Timeline.Events.Take(mute).Count(e => e == "store.SaveJournal"));
        Assert.Single(h.Store.JournalWrites[writesBefore].SavedAudioOutputs);

        // Segundo fechamento (sem abrir): não sobrescreve o valor de antes.
        await h.CloseAsync();
        Assert.Equal(saved, Assert.Single(h.Disk.SavedAudioOutputs));
    }

    [Fact]
    public async Task LeituraDeAudioFalhaPulaOMudoMasOFreezeSegue()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.Audio.FailRead = true;

        await h.CloseAsync();

        Assert.Empty(h.Disk.SavedAudioOutputs);
        Assert.Empty(h.Audio.Calls);
        Assert.Contains(100, h.World.Suspended);
        Assert.Equal(1, h.Display.TurnOffCount);
    }

    [Fact]
    public async Task AberturaRestauraTudoEDiarioFicaLimpoDeAcoesDeTampa()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        await h.CloseAsync();

        await h.OpenAsync();

        Assert.Empty(h.World.Suspended);
        Assert.False(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        Assert.False(h.Disk.HasLidActions);
        Assert.Equal(1, h.Display.WakeCount);
        Assert.False(h.Manager.CountdownPaused);
    }

    [Fact]
    public async Task UsuarioDesmutouComATampaFechadaAberturaNaoMexeNoAudio()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        await h.CloseAsync();
        h.Audio.Devices[FakeAudio.SpeakersId] = h.Audio.Devices[FakeAudio.SpeakersId] with { Muted = false, Volume = 0.3f };
        var calls = h.Audio.Calls.Count;

        await h.OpenAsync();

        Assert.Equal(calls, h.Audio.Calls.Count);
        Assert.Equal(0.3f, h.Audio.Devices[FakeAudio.SpeakersId].Volume);
        Assert.Empty(h.Disk.SavedAudioOutputs);
    }

    [Fact]
    public async Task SaidaDesconectadaEsperaNaoSujaEReconexaoRestaura()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        await h.CloseAsync();
        var muted = h.Audio.Devices[FakeAudio.SpeakersId];
        h.Audio.Disconnect(FakeAudio.SpeakersId);
        await h.Actions.AudioRetryTask; // tampa fechada com sessão: espera a abertura

        await h.OpenAsync();

        Assert.Single(h.Disk.SavedAudioOutputs);
        // Fora o LIDACTION da sessão em curso, a saída desconectada não torna o diário sujo.
        Assert.False((h.Disk with { LidAction = null, EnergySaver = null }).IsDirty(new HashSet<string> { FakeAudio.SpeakersId }));

        h.Audio.Connect(muted);               // DevicesChanged ⇒ retentativa imediata
        await h.Actions.AudioRetryTask;

        Assert.Empty(h.Disk.SavedAudioOutputs);
        Assert.False(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        Assert.Equal(0.62f, h.Audio.Devices[FakeAudio.SpeakersId].Volume);
    }

    [Fact]
    public async Task SaidaQueReconectaComTampaFechadaEsperaAAbertura()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        await h.CloseAsync();
        var muted = h.Audio.Devices[FakeAudio.SpeakersId];
        h.Audio.Disconnect(FakeAudio.SpeakersId);
        h.Audio.Connect(muted);
        await h.Actions.AudioRetryTask;

        Assert.True(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        Assert.Single(h.Disk.SavedAudioOutputs);
    }

    [Fact]
    public async Task RestauracaoDeAudioFalhaRetentaACada30sAte10VezesEMudancaDeDispositivoRecomeca()
    {
        using var h = new TampaHarness(Cfg());
        await h.StartSessionAsync();
        await h.CloseAsync();
        h.Audio.FailSet = true;

        await h.OpenAsync();
        Assert.Single(h.Disk.SavedAudioOutputs);

        int VolumeCalls() => h.Audio.Calls.Count(c => c.StartsWith("volume(", StringComparison.Ordinal));
        var afterOpen = VolumeCalls();
        for (var i = 0; i < 12; i++)
        {
            h.Scheduler.Advance(TimeSpan.FromSeconds(30));
            await h.Actions.AudioRetryTask;
        }
        Assert.Equal(afterOpen + 10, VolumeCalls());
        Assert.Single(h.Disk.SavedAudioOutputs);

        h.Audio.FailSet = false;
        h.Audio.RaiseDevicesChanged();
        await h.Actions.AudioRetryTask;

        Assert.Empty(h.Disk.SavedAudioOutputs);
        Assert.False(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
    }

    [Fact]
    public async Task DockerOciosoDuasVezesCongelaComSegundaChecagemDepoisDoDiarioELogoAntesDoSuspend()
    {
        using var h = new TampaHarness(Cfg(c => c with { DockerRule = true, FreezeList = [] }));
        await h.StartSessionAsync();
        h.AddProcess(200, "Docker Desktop.exe");
        h.AddProcess(201, "com.docker.backend.exe", parent: 200, sec: 1);

        await h.CloseAsync();

        Assert.Equal(2, h.Docker.ProbeCount);
        Assert.Contains(200, h.World.Suspended);
        Assert.Contains(201, h.World.Suspended);
        Assert.True(h.Disk.DockerFrozen);
        var ev = h.Timeline.Events;
        var firstProbe = ev.IndexOf("docker.Probe");
        var secondProbe = ev.FindIndex(firstProbe + 1, e => e == "docker.Probe");
        var suspend = h.Timeline.IndexOf("process.Suspend(200)");
        Assert.Contains("store.SaveJournal", ev.Skip(firstProbe).Take(secondProbe - firstProbe));
        Assert.True(suspend > secondProbe);

        await h.OpenAsync();
        Assert.Empty(h.World.Suspended);
        Assert.False(h.Disk.DockerFrozen);
    }

    [Fact]
    public async Task DockerSegundaChecagemOcupadaDeixaDockerRodandoERemoveProvisorias()
    {
        using var h = new TampaHarness(Cfg(c => c with { DockerRule = true, FreezeList = [] }));
        await h.StartSessionAsync();
        h.AddProcess(200, "Docker Desktop.exe");
        h.Docker.Results.Enqueue(DockerProbe.Idle);
        h.Docker.Results.Enqueue(DockerProbe.Busy);

        await h.CloseAsync();

        Assert.Empty(h.World.Signals);
        Assert.Contains(h.Store.JournalWrites, j => j.FrozenProcesses.Any(f => f.Pid == 200 && !f.HasIdentity));
        Assert.Empty(h.Disk.FrozenProcesses);
        Assert.False(h.Disk.DockerFrozen);
    }

    [Theory]
    [InlineData(DockerProbe.Busy)]
    [InlineData(DockerProbe.Unavailable)]
    public async Task DockerPrimeiraChecagemNaoOciosaNemDiariza(DockerProbe result)
    {
        using var h = new TampaHarness(Cfg(c => c with { DockerRule = true, FreezeList = [] }));
        await h.StartSessionAsync();
        h.AddProcess(200, "Docker Desktop.exe");
        h.Docker.Result = result;

        await h.CloseAsync();

        Assert.Equal(1, h.Docker.ProbeCount);
        Assert.Empty(h.World.Signals);
        Assert.DoesNotContain(h.Store.JournalWrites, j => j.FrozenProcesses.Length > 0);
    }

    [Fact]
    public async Task DockerRegraDesligadaNaoChecaNada()
    {
        using var h = new TampaHarness(Cfg(c => c with { FreezeList = [] }));
        await h.StartSessionAsync();
        h.AddProcess(200, "Docker Desktop.exe");

        await h.CloseAsync();

        Assert.Equal(0, h.Docker.ProbeCount);
        Assert.Empty(h.World.Signals);
    }

    [Fact]
    public async Task R3_FimDeSessaoComTampaFechadaRestauraTudo()
    {
        using var h = new TampaHarness(Config.Default with { DockerRule = true });
        await h.StartSessionAsync();
        h.AddProcess(100, "slack.exe");
        h.AddProcess(200, "Docker Desktop.exe");
        h.Power.Power = new PowerStatus(OnAc: false, BatteryPercent: 80); // na bateria: tampa liga Economia
        await h.CloseAsync();
        Assert.Contains(100, h.World.Suspended);
        Assert.Contains(200, h.World.Suspended);
        Assert.True(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        Assert.Equal(100u, h.Energy.Current.DcThreshold);

        var result = await h.Manager.EndAsync(EndReason.User);

        Assert.Equal(EndOutcome.Restored, result.Outcome);
        Assert.Empty(h.World.Suspended);
        Assert.False(h.Audio.Devices[FakeAudio.SpeakersId].Muted);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), h.Sleep.Lid);
        Assert.Equal(20u, h.Energy.Current.DcThreshold);
        Assert.False(h.Disk.IsDirty());
    }

    [Fact]
    public async Task AgentListVenceFreezeListNoFechamento()
    {
        using var h = new TampaHarness(Cfg(c => c with { FreezeList = ["Code.exe", "slack.exe"] }));
        await h.StartSessionAsync();
        h.AddProcess(100, "Code.exe");
        h.AddProcess(101, "slack.exe");

        await h.CloseAsync();

        Assert.DoesNotContain(100, h.World.Suspended);
        Assert.Contains(101, h.World.Suspended);
    }
}
