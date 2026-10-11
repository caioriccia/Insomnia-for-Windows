using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// Reconcile na abertura do app e Economia de energia (desenho §6.2).
public class ReconcileEnergyTests
{
    // ------------------------------------------------------------------ reconcile

    [Fact]
    public async Task R2_reconcile_reaplica_sem_sobrescrever_valores_anteriores()
    {
        var rig = new SessionRig();
        var session = rig.ValidSession();
        rig.Store.SessionOnDisk = session;
        rig.Store.JournalOnDisk = SessionRig.WithLidAction(ac: 1, dc: 1);
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 2, 2); // algo voltou o valor depois do reboot

        await rig.Manager.ReconcileAsync();

        Assert.Equal(session, rig.Manager.Current);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 0, 0), rig.Sleep.Lid);
        var entry = rig.Store.JournalOnDisk!.LidAction!;
        Assert.Equal((1u, 1u), (entry.AcPrevious, entry.DcPrevious));
        Assert.Empty(rig.Store.JournalWrites); // a entrada existente não foi tocada
        Assert.True(rig.Sleep.Holding);
        Assert.NotEmpty(rig.Backstop.Queries);
        Assert.Equal(session.EndsAt - rig.Clock.Now, rig.Scheduler.PendingDelays.Single());
    }

    [Fact]
    public async Task R1_reconcile_sem_entrada_diariza_antes_de_zerar()
    {
        var rig = new SessionRig();
        rig.Store.SessionOnDisk = rig.ValidSession();

        await rig.Manager.ReconcileAsync();

        Assert.True(rig.Timeline.IndexOf("store.SaveJournal") < rig.Timeline.IndexOf("sleep.WriteLidAction(0,0)"));
        Assert.Equal((1u, 1u), (rig.Store.JournalOnDisk!.LidAction!.AcPrevious, rig.Store.JournalOnDisk.LidAction.DcPrevious));
    }

    [Fact]
    public async Task Reconcile_com_tampa_aberta_desfaz_acoes_de_tampa()
    {
        var rig = new SessionRig { Lid = LidState.Open };
        rig.Store.SessionOnDisk = rig.ValidSession();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction() with { FrozenProcesses = [SessionRig.Frozen()] };

        await rig.Manager.ReconcileAsync();

        Assert.Equal(1, rig.LidUndo.Calls);
        Assert.Empty(rig.Store.JournalOnDisk!.FrozenProcesses);
        Assert.NotNull(rig.Store.JournalOnDisk.LidAction); // sessão segue: LIDACTION continua nosso
        Assert.False(rig.Manager.CountdownPaused);
    }

    [Theory]
    [InlineData(LidState.Closed)]
    [InlineData(LidState.Unknown)]
    public async Task Reconcile_com_tampa_fechada_ou_desconhecida_mantem(LidState lid)
    {
        var rig = new SessionRig { Lid = lid };
        rig.Store.SessionOnDisk = rig.ValidSession();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction() with { FrozenProcesses = [SessionRig.Frozen()] };

        await rig.Manager.ReconcileAsync();

        Assert.Equal(0, rig.LidUndo.Calls);
        Assert.Single(rig.Store.JournalOnDisk!.FrozenProcesses);
        Assert.Equal(lid == LidState.Closed, rig.Manager.CountdownPaused);
    }

    [Fact]
    public async Task Reconcile_sessao_expirada_encerra_pelo_timer()
    {
        var rig = new SessionRig();
        rig.Store.SessionOnDisk = rig.ExpiredSession();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction();
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        await rig.Manager.ReconcileAsync();

        Assert.Null(rig.Store.SessionOnDisk);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), rig.Sleep.Lid);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
        Assert.False(rig.Manager.IsActive);
    }

    [Fact]
    public async Task R4_reconcile_sem_sessao_e_diario_sujo_desfaz()
    {
        var rig = new SessionRig();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction() with
        {
            EnergySaver = new EnergySaverEntry(FakeSchemes.Balanced, 20, FakeClock.DefaultStart, "battery"),
        };
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);
        rig.Energy.Current = new EnergySaverValues(FakeSchemes.Balanced, 100);

        await rig.Manager.ReconcileAsync();

        Assert.Equal(1u, rig.Sleep.Lid.Ac);
        Assert.Equal(20u, rig.Energy.Current.DcThreshold);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task Reconcile_sem_sessao_e_diario_limpo_nao_mexe()
    {
        var rig = new SessionRig();
        await rig.Manager.ReconcileAsync();
        Assert.Empty(rig.Sleep.Writes);
        Assert.Empty(rig.Store.JournalWrites);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
    }

    [Fact]
    public async Task R5_sessao_ilegivel_e_movida_para_o_lado_nunca_apagada()
    {
        var rig = new SessionRig();
        rig.Store.CorruptSession = true;
        rig.Store.JournalOnDisk = SessionRig.WithLidAction();
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        await rig.Manager.ReconcileAsync();

        Assert.Single(rig.Store.MovedAside);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
        Assert.Equal(1u, rig.Sleep.Lid.Ac); // tratada como expirada
        Assert.True(rig.Timeline.IndexOf("store.MoveAside") < rig.Timeline.IndexOf("sleep.WriteLidAction(1,1)"));
    }

    [Fact]
    public async Task R5_sessao_ilegivel_que_nao_move_segura_a_saida_e_nao_desfaz()
    {
        var rig = new SessionRig();
        rig.Store.CorruptSession = true;
        rig.Store.FailMoveAside = true;
        rig.Store.JournalOnDisk = SessionRig.WithLidAction();
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        await rig.Manager.ReconcileAsync();
        var quit = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.Equal(EndOutcome.SessionRetained, quit.Outcome);
        Assert.False(quit.LetsQuitGo);
        Assert.True(rig.Store.CorruptSession);
        Assert.Equal(0u, rig.Sleep.Lid.Ac);
        Assert.NotNull(rig.Store.JournalOnDisk!.LidAction);
    }

    [Fact]
    public async Task R6_reconcile_com_lock_ocupado_nao_muda_nada_e_retenta()
    {
        var rig = new SessionRig();
        rig.Store.SessionOnDisk = rig.ValidSession();
        rig.Lock.Held = true;

        await rig.Manager.ReconcileAsync();
        Assert.False(rig.Manager.IsActive);
        Assert.Empty(rig.Sleep.Writes);
        Assert.Empty(rig.Store.Operations);

        rig.Lock.Held = false;
        rig.Scheduler.Advance(SessionManager.RetryDelay);
        await SessionRig.Until(() => rig.Manager.IsActive);
        Assert.Equal(0u, rig.Sleep.Lid.Ac);
    }

    // ------------------------------------------------------------------ Economia de energia

    [Fact]
    public async Task R1_Economia_ligada_diariza_antes_e_avisa_quando_e_bateria()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));

        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);

        var entry = rig.Store.JournalOnDisk!.EnergySaver!;
        Assert.Equal((20u, "battery"), (entry.DcThresholdPrevious, entry.Cause));
        Assert.Equal(100u, rig.Energy.Current.DcThreshold);
        Assert.True(rig.Timeline.LastIndexOf("store.SaveJournal") < rig.Timeline.IndexOf("energy.WriteDcThreshold(100)"));
        Assert.True(rig.Manager.EnergySaverOurs);
        Assert.Contains(rig.Notifier.Posts, p => p.Title == "Economia de energia ligada");

        await rig.Manager.SetEnergySaverAsync(false, EnergySaverCause.Battery);
        Assert.Equal(20u, rig.Energy.Current.DcThreshold);
        Assert.Null(rig.Store.JournalOnDisk!.EnergySaver);
        Assert.False(rig.Manager.EnergySaverOurs);
        Assert.True(rig.Timeline.LastIndexOf("energy.WriteDcThreshold(20)") < rig.Timeline.LastIndexOf("store.SaveJournal"));
    }

    [Fact]
    public async Task Economia_pela_tampa_e_silenciosa()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));
        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Lid);
        Assert.Equal(100u, rig.Energy.Current.DcThreshold);
        Assert.Empty(rig.Notifier.Posts);
    }

    [Fact]
    public async Task Economia_ja_ligada_nao_e_assumida()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));
        rig.Energy.Current = new EnergySaverValues(FakeSchemes.Balanced, 100);
        var writes = rig.Store.JournalWrites.Count;

        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);

        Assert.Null(rig.Store.JournalOnDisk!.EnergySaver);
        Assert.Equal(writes, rig.Store.JournalWrites.Count);
        Assert.Empty(rig.Energy.Writes);
        Assert.False(rig.Manager.EnergySaverOurs);

        await rig.Manager.EndAsync(EndReason.User);
        Assert.Equal(100u, rig.Energy.Current.DcThreshold); // a do usuário fica como estava
    }

    [Fact]
    public async Task Economia_que_falha_ao_ligar_volta_e_limpa_a_entrada()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));
        rig.Energy.FailWriteIf = v => v.DcThreshold == 100;

        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);

        Assert.Null(rig.Store.JournalOnDisk!.EnergySaver);
        Assert.Equal(20u, rig.Energy.Current.DcThreshold);
        Assert.False(rig.Manager.EnergySaverOurs);
        Assert.NotEmpty(rig.Manager.Warnings);
    }

    [Fact]
    public async Task R1_Economia_sem_diario_nao_liga()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));
        rig.Store.FailNextJournalWrite = true;

        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);

        Assert.Empty(rig.Energy.Writes);
        Assert.Null(rig.Store.JournalOnDisk!.EnergySaver);
    }

    [Fact]
    public async Task Economia_sem_sessao_nao_liga()
    {
        var rig = new SessionRig();
        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);
        Assert.Empty(rig.Energy.Writes);
    }

    [Fact]
    public async Task Abertura_da_tampa_roda_LidUndo_sob_a_transacao_e_mostra_problemas()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(4));
        rig.LidUndo.Problems.Add("slack.exe: não verificável");

        await rig.Manager.UndoLidActionsAsync();

        Assert.Equal(1, rig.LidUndo.Calls);
        Assert.Contains("slack.exe: não verificável", rig.Manager.Warnings);
    }
}
