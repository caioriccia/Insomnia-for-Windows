// LidUndo isolado (desenho §7.2; R3 e R8): decisões por entrada do diário e o que fica.
using System.Collections.Immutable;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Store;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Tampa;

public class LidUndoTests
{
    private readonly FakeProcessWorld _world = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeDisplay _display = new();
    private readonly FakeLog _log = new();
    private readonly FakeStore _store = new();

    private LidUndo Undo() => new(_world, _world, _audio, _display, _log);

    private JournalTransaction Tx(Journal j)
    {
        _store.JournalOnDisk = j;
        return new JournalTransaction(_store, j);
    }

    private static Journal With(params FrozenProcess[] frozen) => Journal.Clean with { FrozenProcesses = [.. frozen] };

    private static FrozenProcess Entry(int pid, DateTimeOffset? created, string? boot = "17", string group = "slack.exe") =>
        new(pid, "slack.exe", created, created is null ? null : boot, group);

    [Fact]
    public async Task R8_EntradaSemIdentidadeDeProcessoParadoNuncaESinalizadaEFicaNaoVerificavel()
    {
        _world.Add(100, "slack.exe", 1, TampaHarness.T0);
        _world.Suspended.Add(100);
        var tx = Tx(With(Entry(100, null)));

        var r = await Undo().UndoAsync(tx, default);

        Assert.Empty(_world.Signals);
        Assert.Single(tx.Journal.FrozenProcesses);
        Assert.Contains(r.Problems, p => p.Contains("não verificável"));
        Assert.True(tx.Journal.IsDirty());
    }

    [Fact]
    public async Task PidReusadoEGoneSemSinalESaiDoDiario()
    {
        _world.Add(100, "outro.exe", 1, TampaHarness.T0.AddMinutes(5)); // outro processo com o mesmo pid
        _world.Suspended.Add(100);
        var tx = Tx(With(Entry(100, TampaHarness.T0)));

        var r = await Undo().UndoAsync(tx, default);

        Assert.Empty(_world.Signals);
        Assert.Empty(tx.Journal.FrozenProcesses);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public async Task AusenteOuRodandoEGone()
    {
        _world.Add(101, "slack.exe", 1, TampaHarness.T0); // rodando (não suspenso)
        var tx = Tx(With(Entry(100, TampaHarness.T0), Entry(101, TampaHarness.T0)));

        await Undo().UndoAsync(tx, default);

        Assert.Empty(_world.Signals);
        Assert.Empty(tx.Journal.FrozenProcesses);
    }

    [Fact]
    public async Task R3_IdentidadeQueVoltouDoDiscoEmSegundosInteirosAindaRetoma()
    {
        // O processo tem fração de segundo; o state.json não. Ida e volta pelo codec real.
        _world.Add(100, "slack.exe", 1, TampaHarness.T0);
        _world.Suspended.Add(100);
        var promoted = ProcessControl.Promoted(_world.Lookup(100)!, "slack.exe");
        var fromDisk = JsonCodec.ParseJournal(JsonCodec.SerializeJournal(With(promoted)));
        var tx = Tx(fromDisk);

        var r = await Undo().UndoAsync(tx, default);

        Assert.Equal([("resume", 100)], _world.Signals);
        Assert.Empty(tx.Journal.FrozenProcesses);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public async Task R3_ResumeFalhaMantemEntradaERepetirESeguro()
    {
        _world.Add(100, "slack.exe", 1, TampaHarness.T0);
        _world.Suspended.Add(100);
        _world.FailResume = true;
        var tx = Tx(With(Entry(100, TampaHarness.T0)));

        var r1 = await Undo().UndoAsync(tx, default);
        Assert.Single(tx.Journal.FrozenProcesses);
        Assert.Contains(r1.Problems, p => p.Contains("Não consegui retomar"));

        _world.FailResume = false;
        var r2 = await Undo().UndoAsync(tx, default);
        Assert.Empty(tx.Journal.FrozenProcesses);
        Assert.Empty(r2.Problems);
        Assert.Empty(_world.Suspended);
    }

    [Fact]
    public async Task R3_EfeitoAntesDeLimparEscritaFalhaMantemEntradaNoDisco()
    {
        _world.Add(100, "slack.exe", 1, TampaHarness.T0);
        _world.Suspended.Add(100);
        var tx = Tx(With(Entry(100, TampaHarness.T0)));
        _store.FailNextJournalWrite = true;

        var r = await Undo().UndoAsync(tx, default);

        Assert.Empty(_world.Suspended);                     // efeito aconteceu
        Assert.Single(_store.JournalOnDisk!.FrozenProcesses); // entrada ficou
        Assert.Contains(r.Problems, p => p.Contains("diário não gravou"));

        var again = await Undo().UndoAsync(tx, default);    // repetir: rodando ⇒ Gone, sem sinal
        Assert.Single(_world.Signals);
        Assert.Empty(_store.JournalOnDisk!.FrozenProcesses);
        Assert.Empty(again.Problems);
    }

    [Fact]
    public async Task DockerFrozenCaiQuandoNaoSobraProcessoDoDocker()
    {
        _world.Add(200, "Docker Desktop.exe", 1, TampaHarness.T0);
        _world.Suspended.Add(200);
        var tx = Tx(With(Entry(200, TampaHarness.T0, group: DockerRule.GroupName)) with { DockerFrozen = true });

        await Undo().UndoAsync(tx, default);

        Assert.False(tx.Journal.DockerFrozen);
        Assert.Empty(tx.Journal.FrozenProcesses);
    }

    [Fact]
    public async Task SaidaDesconectadaFicaNoDiarioENaoTornaSujo()
    {
        const string gone = "{0.0.0.00000000}.{fone-bt}";
        var tx = Tx(Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput(gone, "Fone", 0.5f, false)] });

        var r = await Undo().UndoAsync(tx, default);

        Assert.Contains(gone, r.AudioDevicesNotConnected);
        Assert.Single(tx.Journal.SavedAudioOutputs);
        Assert.False(tx.Journal.IsDirty(r.AudioDevicesNotConnected));
        Assert.Empty(r.Problems);
        Assert.Empty(_audio.Calls);
    }

    [Fact]
    public async Task RestauraPeloDeviceIdGravadoNaoPelaSaidaPadraoAtual()
    {
        const string other = "{0.0.0.00000000}.{hdmi}";
        _audio.Devices[other] = new AudioOutput(other, "HDMI", 0.9f, false);
        _audio.DefaultDeviceId = other;
        _audio.Devices[FakeAudio.SpeakersId] = _audio.Devices[FakeAudio.SpeakersId] with { Muted = true, Volume = 0.1f };
        var tx = Tx(Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput(FakeAudio.SpeakersId, "Alto-falantes", 0.62f, false)] });

        await Undo().UndoAsync(tx, default);

        Assert.Equal(new AudioOutput(FakeAudio.SpeakersId, "Alto-falantes", 0.62f, false), _audio.Devices[FakeAudio.SpeakersId]);
        Assert.Equal(0.9f, _audio.Devices[other].Volume);
        Assert.Empty(tx.Journal.SavedAudioOutputs);
    }

    [Fact]
    public async Task UsuarioDesmutouSoRemoveAEntrada()
    {
        _audio.Devices[FakeAudio.SpeakersId] = _audio.Devices[FakeAudio.SpeakersId] with { Volume = 0.2f };
        var tx = Tx(Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput(FakeAudio.SpeakersId, "Alto-falantes", 0.62f, false)] });

        await Undo().UndoAsync(tx, default);

        Assert.Empty(_audio.Calls);
        Assert.Equal(0.2f, _audio.Devices[FakeAudio.SpeakersId].Volume);
        Assert.Empty(tx.Journal.SavedAudioOutputs);
    }

    [Fact]
    public async Task FalhaDeAudioEmSaidaConectadaFicaEVoltaComoProblema()
    {
        _audio.Devices[FakeAudio.SpeakersId] = _audio.Devices[FakeAudio.SpeakersId] with { Muted = true };
        _audio.FailSet = true;
        var tx = Tx(Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput(FakeAudio.SpeakersId, "Alto-falantes", 0.62f, false)] });

        var r = await Undo().UndoAsync(tx, default);
        var outcome = Undo().RestoreAudio(tx);

        Assert.Single(tx.Journal.SavedAudioOutputs);
        Assert.Contains(r.Problems, p => p.Contains("Alto-falantes"));
        Assert.Equal(1, outcome.FailedConnected);
        Assert.True(tx.Journal.IsDirty(r.AudioDevicesNotConnected));
    }

    [Fact]
    public async Task SempreAcordaATelaENuncaLancaQuandoOInspetorFalha()
    {
        var tx = Tx(With(Entry(100, TampaHarness.T0)));
        var undo = new LidUndo(new ThrowingInspector(), _world, _audio, _display, _log);

        var r = await undo.UndoAsync(tx, default);

        Assert.Equal(1, _display.WakeCount);
        Assert.Single(tx.Journal.FrozenProcesses);
        Assert.NotEmpty(r.Problems);
    }

    private sealed class ThrowingInspector : IProcessInspector
    {
        public string BootId => "17";
        public IReadOnlyList<ProcessInfo> Snapshot() => throw new InvalidOperationException("sem acesso");
        public ProcessInfo? Lookup(int pid) => throw new InvalidOperationException("sem acesso");
    }
}
