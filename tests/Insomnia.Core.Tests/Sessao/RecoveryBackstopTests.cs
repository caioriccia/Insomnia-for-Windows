using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Backstop;
using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// RecoveryEngine.RestoreAllAsync e BackstopRunner (desenho §6.2, R3, R5, R6, R9).
public class RecoveryBackstopTests
{
    private static Journal Dirty() => SessionRig.WithLidAction() with
    {
        EnergySaver = new EnergySaverEntry(FakeSchemes.Balanced, 20, FakeClock.DefaultStart, "lid"),
        FrozenProcesses = [SessionRig.Frozen()],
    };

    private static SessionRig DirtyRig()
    {
        var rig = new SessionRig();
        rig.Store.JournalOnDisk = Dirty();
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);
        rig.Energy.Current = new EnergySaverValues(FakeSchemes.Balanced, 100);
        return rig;
    }

    private static BackstopRunner Runner(SessionRig rig) => new(rig.Store, rig.Lock, rig.Engine(), rig.Clock, rig.Log);

    // ------------------------------------------------------------------ RestoreAll

    [Fact]
    public async Task R3_RestoreAll_ordem_fixa_efeito_antes_de_limpar()
    {
        var rig = DirtyRig();
        var tx = new JournalTransaction(rig.Store, rig.Store.LoadJournal());

        var report = await rig.Engine().RestoreAllAsync(tx, CancellationToken.None);

        Assert.True(report.Complete);
        Assert.Empty(report.Problems);
        var t = rig.Timeline;
        var lid = t.IndexOf("sleep.WriteLidAction(1,1)");
        var energy = t.IndexOf("energy.WriteDcThreshold(20)");
        var undo = t.IndexOf("lidUndo.Undo");
        Assert.True(lid < energy && energy < undo, "ordem: LIDACTION → Economia → tampa");
        // Cada limpeza vem depois do efeito correspondente.
        Assert.NotNull(rig.Store.JournalWrites[0].EnergySaver);
        Assert.Null(rig.Store.JournalWrites[0].LidAction);
        Assert.True(lid < t.Events.FindIndex(lid + 1, e => e == "store.SaveJournal"));
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task R3_efeito_que_falha_mantem_a_entrada_e_repetir_e_seguro()
    {
        var rig = DirtyRig();
        rig.Sleep.FailWrite = true;
        var engine = rig.Engine();

        var first = await engine.RestoreAllAsync(new JournalTransaction(rig.Store, rig.Store.LoadJournal()), default);
        Assert.False(first.Complete);
        Assert.NotEmpty(first.Problems);
        Assert.NotNull(rig.Store.JournalOnDisk!.LidAction);
        Assert.Null(rig.Store.JournalOnDisk.EnergySaver); // o resto segue

        rig.Sleep.FailWrite = false;
        var second = await engine.RestoreAllAsync(new JournalTransaction(rig.Store, rig.Store.LoadJournal()), default);
        Assert.True(second.Complete);
        Assert.Equal(1u, rig.Sleep.Lid.Ac);
    }

    [Fact]
    public async Task R3_escrita_que_falha_depois_do_efeito_mantem_a_entrada()
    {
        var rig = DirtyRig();
        rig.Store.FailJournalWriteIf = j => j.LidAction is null;

        var report = await rig.Engine().RestoreAllAsync(new JournalTransaction(rig.Store, rig.Store.LoadJournal()), default);

        Assert.False(report.Complete);
        Assert.NotNull(rig.Store.JournalOnDisk!.LidAction);
        Assert.Equal(1u, rig.Sleep.Lid.Ac); // o efeito aconteceu; reescrever o mesmo valor depois é inofensivo
    }

    [Fact]
    public async Task Saida_de_audio_desconectada_nao_torna_incompleto()
    {
        var rig = new SessionRig();
        rig.Store.JournalOnDisk = Journal.Clean with { SavedAudioOutputs = [new SavedAudioOutput("fone", "Fone", 0.5f, false)] };
        rig.LidUndo.NotConnected.Add("fone");

        var report = await rig.Engine().RestoreAllAsync(new JournalTransaction(rig.Store, rig.Store.LoadJournal()), default);

        Assert.True(report.Complete);
        Assert.Single(rig.Store.JournalOnDisk!.SavedAudioOutputs); // fica esperando a saída voltar
    }

    [Fact]
    public async Task R7_diario_limpo_nao_chama_nada()
    {
        var rig = new SessionRig();
        var report = await rig.Engine().RestoreAllAsync(new JournalTransaction(rig.Store, Journal.Clean), default);
        Assert.True(report.Complete);
        Assert.Empty(rig.Timeline.Events);
    }

    // ------------------------------------------------------------------ BackstopRunner

    [Fact]
    public async Task R9_backstop_com_sessao_valida_sai_0_sem_tocar_em_nada()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ValidSession();

        Assert.Equal(0, await Runner(rig).RunAsync(force: false, default));

        Assert.Empty(rig.Sleep.Writes);
        Assert.Empty(rig.Energy.Writes);
        Assert.Empty(rig.Store.JournalWrites);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
        Assert.NotNull(rig.Store.SessionOnDisk);
        Assert.Equal(0, rig.LidUndo.Calls);
        Assert.True(rig.Log.Contains("nada a fazer"));
        Assert.Empty(rig.Notifier.Posts);
    }

    [Fact]
    public async Task Backstop_com_sessao_expirada_restaura_e_apaga()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();

        Assert.Equal(0, await Runner(rig).RunAsync(false, default));

        Assert.Equal(1u, rig.Sleep.Lid.Ac);
        Assert.Equal(20u, rig.Energy.Current.DcThreshold);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
        Assert.Null(rig.Store.SessionOnDisk);
        Assert.True(rig.Timeline.LastIndexOf("lidUndo.Undo") < rig.Timeline.LastIndexOf("store.DeleteSession"));
        Assert.Empty(rig.Notifier.Posts);
    }

    [Fact]
    public async Task R4_backstop_sem_sessao_e_diario_sujo_restaura()
    {
        var rig = DirtyRig();
        Assert.Equal(0, await Runner(rig).RunAsync(false, default));
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task Backstop_limpo_e_sem_sessao_sai_0_sem_escrever()
    {
        var rig = new SessionRig();
        Assert.Equal(0, await Runner(rig).RunAsync(false, default));
        Assert.Empty(rig.Store.JournalWrites);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
    }

    [Fact]
    public async Task Backstop_com_falha_sai_1_e_mantem_session_json()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();
        rig.Sleep.FailWrite = true;

        Assert.Equal(1, await Runner(rig).RunAsync(false, default));

        Assert.NotNull(rig.Store.SessionOnDisk);
        Assert.NotNull(rig.Store.JournalOnDisk!.LidAction);
    }

    [Fact]
    public async Task R5_backstop_com_diario_corrompido_sai_1_e_deixa_intacto()
    {
        var rig = new SessionRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();
        rig.Store.CorruptJournal = true;
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        Assert.Equal(1, await Runner(rig).RunAsync(false, default));

        Assert.True(rig.Store.CorruptJournal);
        Assert.Empty(rig.Store.JournalWrites);
        Assert.Empty(rig.Sleep.Writes);
        Assert.NotNull(rig.Store.SessionOnDisk);
        Assert.True(rig.Log.Contains("evidência mantida"));
    }

    [Fact]
    public async Task R6_backstop_com_lock_ocupado_sai_75_sem_tocar_em_nada()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();
        rig.Lock.Held = true;

        Assert.Equal(75, await Runner(rig).RunAsync(false, default));

        Assert.Empty(rig.Store.Operations);
        Assert.Empty(rig.Sleep.Writes);
        Assert.Equal(BackstopRunner.LockTimeout, rig.Lock.RequestedTimeouts.Single());
    }

    [Fact]
    public async Task R9_backstop_force_age_mesmo_com_sessao_valida()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ValidSession();

        Assert.Equal(0, await Runner(rig).RunAsync(force: true, default));

        Assert.Equal(1u, rig.Sleep.Lid.Ac);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
        Assert.Null(rig.Store.SessionOnDisk);
    }

    [Fact]
    public async Task R5_backstop_move_sessao_ilegivel_em_vez_de_apagar()
    {
        var rig = DirtyRig();
        rig.Store.CorruptSession = true;

        Assert.Equal(0, await Runner(rig).RunAsync(false, default));

        Assert.Single(rig.Store.MovedAside);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task Backstop_e_app_dividem_o_lock_o_backstop_espera_a_transacao_do_app()
    {
        var rig = DirtyRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();
        var runner = Runner(rig);
        int? exitInside = null;

        await rig.Manager.RunExclusiveAsync("app", async _ =>
        {
            exitInside = await runner.RunAsync(false, default);
            return 0;
        });

        Assert.Equal(75, exitInside);
        Assert.NotNull(rig.Store.SessionOnDisk);
    }
}
