using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Backstop;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// BackstopRunner sobre o LidUndo REAL (desenho §6.2 "Backstop", R8, R9): um diário com
/// resíduo que não se resolve sozinho (saída de áudio desconectada, processo não verificável)
/// faz o backstop rodar RestoreAll a cada minuto. Ele pode tentar de novo à vontade, mas não
/// pode acender a tela a cada minuto: o LidUndo do backstop não acorda o monitor.
public class BackstopDisplayTests
{
    private const string FoneId = "{0.0.0.00000000}.{fone-bluetooth}";

    private readonly SessionRig _rig = new();
    private readonly FakeProcessWorld _world = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeDisplay _display = new();

    private BackstopRunner Runner(bool wakeDisplay = false)
    {
        var lidUndo = new LidUndo(_world, _world, _audio, _display, _rig.Log, wakeDisplay);
        var engine = new RecoveryEngine(_rig.Sleep, _rig.Energy, lidUndo, _rig.Clock, _rig.Log);
        return new BackstopRunner(_rig.Store, _rig.Lock, engine, _rig.Clock, _rig.Log);
    }

    private static Journal ComFoneDesconectado() =>
        Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput(FoneId, "Fone Bluetooth", 0.4f, false)] };

    private Journal ComProcessoNaoVerificavel()
    {
        _world.Add(4312, "slack.exe", 1, FakeClock.DefaultStart.AddHours(-9));
        _world.Suspended.Add(4312);
        return Journal.Clean with { FrozenProcesses = [new FrozenProcess(4312, "slack.exe", null, null, "slack.exe")] };
    }

    [Fact]
    public async Task R9_sem_sessao_e_fone_desconectado_no_diario_o_backstop_nao_acorda_a_tela_minuto_apos_minuto()
    {
        _rig.Store.JournalOnDisk = ComFoneDesconectado();
        var runner = Runner();

        for (var minuto = 0; minuto < 5; minuto++)
        {
            Assert.Equal(BackstopRunner.ExitOk, await runner.RunAsync(force: false, default));
            _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(0, _display.WakeCount);
        Assert.Empty(_display.Calls);
        Assert.Empty(_audio.Calls); // desconectada: nada foi mexido, só consultado
        Assert.Equal(FoneId, Assert.Single(_rig.Store.JournalOnDisk!.SavedAudioOutputs).DeviceId); // a entrada espera o fone
        Assert.DoesNotContain("DeleteSession", _rig.Store.Operations);
    }

    [Fact]
    public async Task R8_R9_sem_sessao_e_processo_nao_verificavel_o_backstop_sai_1_mantem_a_entrada_e_nao_acorda_a_tela()
    {
        _rig.Store.JournalOnDisk = ComProcessoNaoVerificavel();
        var runner = Runner();

        for (var minuto = 0; minuto < 3; minuto++)
        {
            Assert.Equal(BackstopRunner.ExitFailed, await runner.RunAsync(force: false, default));
            _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(0, _display.WakeCount);
        Assert.Empty(_world.Signals); // R8: sem identidade nunca é sinalizado
        Assert.Single(_rig.Store.JournalOnDisk!.FrozenProcesses); // evidência mantida
    }

    [Fact]
    public async Task R9_fone_que_reconecta_mudo_e_restaurado_pelo_backstop_ainda_sem_acordar_a_tela()
    {
        _rig.Store.JournalOnDisk = ComFoneDesconectado();
        var runner = Runner();
        Assert.Equal(BackstopRunner.ExitOk, await runner.RunAsync(false, default));

        _audio.Connect(new AudioOutput(FoneId, "Fone Bluetooth", 0f, true));
        Assert.Equal(BackstopRunner.ExitOk, await runner.RunAsync(false, default));

        Assert.Equal(new AudioOutput(FoneId, "Fone Bluetooth", 0.4f, false), _audio.Devices[FoneId]);
        Assert.False(_rig.Store.JournalOnDisk!.IsDirty());
        Assert.Equal(0, _display.WakeCount);
    }

    [Fact]
    public async Task R3_sessao_expirada_o_backstop_desfaz_tampa_e_processos_e_apaga_a_sessao_sem_acordar_a_tela()
    {
        var frozen = SessionRig.Frozen();
        _world.Add(frozen.Pid, frozen.Name, 1, frozen.CreationTime!.Value);
        _world.Suspended.Add(frozen.Pid);
        _rig.Store.JournalOnDisk = SessionRig.WithLidAction() with { FrozenProcesses = [frozen] };
        _rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);
        _rig.Store.SessionOnDisk = _rig.ExpiredSession();

        Assert.Equal(BackstopRunner.ExitOk, await Runner().RunAsync(false, default));

        Assert.Equal(1u, _rig.Sleep.Lid.Ac);
        Assert.Contains(("resume", frozen.Pid), _world.Signals);
        Assert.False(_rig.Store.JournalOnDisk!.IsDirty());
        Assert.Null(_rig.Store.SessionOnDisk);
        Assert.Equal(0, _display.WakeCount);
    }

    [Fact]
    public async Task O_LidUndo_padrao_do_app_continua_acordando_a_tela_na_abertura_da_tampa()
    {
        // Contraprova: só o backstop desliga o Wake; o caminho do app (abrir a tampa) segue igual.
        var journal = ComFoneDesconectado();
        _rig.Store.JournalOnDisk = journal;
        var app = new LidUndo(_world, _world, _audio, _display, _rig.Log);
        Assert.True(app.WakesDisplay);

        await app.UndoAsync(new JournalTransaction(_rig.Store, journal), default);

        Assert.Equal(1, _display.WakeCount);
    }
}
