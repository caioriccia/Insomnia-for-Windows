using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// Início, extensão e fim (desenho §6.2), nomeando os invariantes de §8.
public class StartEndTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    // ------------------------------------------------------------------ início

    [Fact]
    public async Task R4_R1_R10_inicio_grava_sessao_antes_do_diario_e_diario_e_backstop_antes_de_WriteLidAction()
    {
        var rig = new SessionRig();

        var outcome = await rig.Manager.StartAsync(FourHours);

        Assert.Equal(StartOutcome.Started, outcome);
        var t = rig.Timeline;
        var saveSession = t.IndexOf("store.SaveSession");
        var saveJournal = t.IndexOf("store.SaveJournal");
        var backstop = t.IndexOf("backstop.Install");
        var zero = t.IndexOf("sleep.WriteLidAction(0,0)");
        var hold = t.IndexOf("sleep.Hold(True)");
        Assert.True(saveSession >= 0 && saveSession < saveJournal, "R4: session.json antes do diário");
        Assert.True(saveJournal < backstop, "R4: diário antes do backstop");
        Assert.True(backstop < zero, "R10: backstop armado antes do LIDACTION");
        Assert.True(zero < hold);
        // R1: antes do diário só pode haver a reescrita do MESMO valor (pré-voo de permissão).
        foreach (var e in t.Events.Take(saveJournal).Where(e => e.StartsWith("sleep.WriteLidAction", StringComparison.Ordinal)))
            Assert.Equal("sleep.WriteLidAction(1,1)", e);

        var entry = rig.Store.JournalOnDisk!.LidAction!;
        Assert.Equal((1u, 1u, FakeSchemes.Balanced), (entry.AcPrevious, entry.DcPrevious, entry.SchemeId));
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 0, 0), rig.Sleep.Lid);
        Assert.True(rig.Sleep.Holding);
        Assert.True(rig.Manager.IsActive);
        Assert.Equal(rig.Clock.Now + FourHours, rig.Store.SessionOnDisk!.EndsAt);
        Assert.Equal(rig.Manager.Current, rig.Store.SessionOnDisk);
    }

    [Fact]
    public async Task R10_backstop_nao_armado_nada_muda()
    {
        var rig = new SessionRig();
        rig.Backstop.FailInstall = true;

        var outcome = await rig.Manager.StartAsync(FourHours);

        Assert.Equal(StartOutcome.BackstopNotArmed, outcome);
        AssertNothingChanged(rig);
        Assert.NotNull(rig.Manager.LastError);
    }

    [Fact]
    public async Task R10_instalacao_que_nao_fica_armada_tambem_recusa()
    {
        var rig = new SessionRig();
        rig.Backstop.StateAfterInstall = BackstopState.Different;

        Assert.Equal(StartOutcome.BackstopNotArmed, await rig.Manager.StartAsync(FourHours));
        AssertNothingChanged(rig);
    }

    [Fact]
    public async Task WriteLidAction_falha_restaura_e_nao_deixa_sessao()
    {
        var rig = new SessionRig();
        rig.Sleep.FailWriteIf = v => v.Ac == 0;

        var outcome = await rig.Manager.StartAsync(FourHours);

        Assert.Equal(StartOutcome.PowerWriteFailed, outcome);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), rig.Sleep.Lid);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
        Assert.Null(rig.Store.SessionOnDisk);
        Assert.False(rig.Sleep.Holding);
        Assert.False(rig.Manager.IsActive);
        // R4 também no desfazer do início: session.json some antes de restaurar.
        Assert.True(rig.Timeline.LastIndexOf("store.DeleteSession") < rig.Timeline.LastIndexOf("sleep.WriteLidAction(1,1)"));
    }

    [Fact]
    public async Task R1_diario_que_nao_grava_impede_o_efeito()
    {
        var rig = new SessionRig();
        rig.Store.FailNextJournalWrite = true;

        Assert.Equal(StartOutcome.PowerWriteFailed, await rig.Manager.StartAsync(FourHours));
        AssertNothingChanged(rig);
    }

    [Fact]
    public async Task Pre_voo_sem_permissao_recusa_sem_diarizar()
    {
        var rig = new SessionRig();
        rig.Sleep.FailWrite = true;

        Assert.Equal(StartOutcome.PowerWriteFailed, await rig.Manager.StartAsync(FourHours));
        AssertNothingChanged(rig);
        Assert.Empty(rig.Store.JournalWrites);
    }

    [Fact]
    public async Task Leitura_de_LIDACTION_falha_recusa_sem_mudar_nada()
    {
        var rig = new SessionRig();
        rig.Sleep.FailRead = true;

        Assert.Equal(StartOutcome.PowerWriteFailed, await rig.Manager.StartAsync(FourHours));
        AssertNothingChanged(rig);
    }

    [Fact]
    public async Task R5_sessao_ilegivel_no_inicio_recusa_e_nao_substitui()
    {
        var rig = new SessionRig();
        rig.Store.CorruptSession = true;

        Assert.Equal(StartOutcome.RefusedSessionFileUnreadable, await rig.Manager.StartAsync(FourHours));
        Assert.True(rig.Store.CorruptSession);
        Assert.Empty(rig.Store.SessionWrites);
        Assert.Empty(rig.Store.MovedAside);
        Assert.Empty(rig.Sleep.Writes);
    }

    [Fact]
    public async Task R2_inicio_nao_sobrescreve_valor_anterior_ja_diarizado()
    {
        var rig = new SessionRig();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction(ac: 2, dc: 3);
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        Assert.Equal(StartOutcome.Started, await rig.Manager.StartAsync(FourHours));

        var entry = rig.Store.JournalOnDisk!.LidAction!;
        Assert.Equal((2u, 3u), (entry.AcPrevious, entry.DcPrevious));
    }

    [Fact]
    public async Task Inicio_recusa_com_sessao_ativa_e_depois_de_sair()
    {
        var rig = new SessionRig();
        Assert.Equal(StartOutcome.Started, await rig.Manager.StartAsync(FourHours));
        Assert.Equal(StartOutcome.RefusedAlreadyActive, await rig.Manager.StartAsync(FourHours));

        await rig.Manager.EndAsync(EndReason.Quit);
        Assert.Equal(StartOutcome.RefusedQuitRequested, await rig.Manager.StartAsync(FourHours));
    }

    [Fact]
    public async Task Inicio_com_tampa_fechada_comeca_com_contagem_pausada()
    {
        var rig = new SessionRig { Lid = LidState.Closed };
        await rig.Manager.StartAsync(FourHours);
        Assert.True(rig.Manager.CountdownPaused);
    }

    // ------------------------------------------------------------------ fim

    [Fact]
    public async Task R4_fim_apaga_session_json_antes_de_restaurar()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);

        var result = await rig.Manager.EndAsync(EndReason.User);

        Assert.Equal(EndOutcome.Restored, result.Outcome);
        Assert.True(result.LetsQuitGo);
        var delete = rig.Timeline.LastIndexOf("store.DeleteSession");
        var restore = rig.Timeline.LastIndexOf("sleep.WriteLidAction(1,1)");
        Assert.True(delete >= 0 && delete < restore, "R4: session.json apagado antes do RestoreAll");
        Assert.Null(rig.Store.SessionOnDisk);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), rig.Sleep.Lid);
        Assert.False(rig.Sleep.Holding);
        Assert.False(rig.Manager.IsActive);
        Assert.Contains(rig.Notifier.Posts, p => p.Title == "Sessão encerrada");
    }

    [Theory]
    [InlineData("Read")]          // antes do diário
    [InlineData("Install")]       // diário gravado, antes do efeito
    [InlineData("Write(0,0)")]    // no instante do efeito
    [InlineData("Hold(True)")]    // depois do efeito
    public async Task Fim_durante_inicio_nao_deixa_LIDACTION_0(string at)
    {
        var rig = new SessionRig();
        var clean = ExecutionContext.Capture()!;
        Task<EndResult>? end = null;
        void Trigger(string call)
        {
            if (call == at && end is null)
                end = SessionRig.Outside(clean, () => rig.Manager.EndAsync(EndReason.User));
        }
        rig.HookedSleep.Hook = Trigger;
        rig.HookedBackstop.Hook = Trigger;

        var start = await rig.Manager.StartAsync(FourHours);
        Assert.NotNull(end);
        var result = await end!;

        Assert.NotEqual(StartOutcome.Started, start);
        Assert.Equal(EndOutcome.Restored, result.Outcome);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), rig.Sleep.Lid);
        Assert.False(rig.Store.JournalOnDisk?.IsDirty() ?? false);
        Assert.Null(rig.Store.SessionOnDisk);
        Assert.False(rig.Sleep.Holding);
        Assert.False(rig.Manager.IsActive);
    }

    [Fact]
    public async Task R6_fim_com_lock_ocupado_nao_muda_nada_e_retenta_em_30s()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        var writesBefore = rig.Store.JournalWrites.Count;
        rig.Lock.Held = true;

        var result = await rig.Manager.EndAsync(EndReason.User);

        Assert.Equal(EndOutcome.Locked, result.Outcome);
        Assert.False(result.LetsQuitGo);
        Assert.NotNull(rig.Store.SessionOnDisk);
        Assert.Equal(writesBefore, rig.Store.JournalWrites.Count);
        Assert.Equal(0u, rig.Sleep.Lid.Ac);
        Assert.Equal(StartOutcome.RefusedEndPending, await rig.Manager.StartAsync(FourHours));

        rig.Lock.Held = false;
        rig.Scheduler.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0u, rig.Sleep.Lid.Ac);
        rig.Scheduler.Advance(TimeSpan.FromSeconds(1));
        await SessionRig.Until(() => rig.Sleep.Lid.Ac == 1 && rig.Store.SessionOnDisk is null);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task R6_inicio_com_lock_ocupado_nao_muda_nada()
    {
        var rig = new SessionRig();
        rig.Lock.Held = true;

        Assert.Equal(StartOutcome.Locked, await rig.Manager.StartAsync(FourHours));
        AssertNothingChanged(rig);
        Assert.Empty(rig.Store.Operations);
        Assert.Equal(SessionManager.LockTimeout, rig.Lock.RequestedTimeouts.Single());
    }

    [Fact]
    public async Task Fim_incompleto_com_backstop_armado_libera_a_saida()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        rig.Sleep.FailWriteIf = v => v.Ac == 1;

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.Equal(EndOutcome.Incomplete, result.Outcome);
        Assert.True(result.AgentArmed);
        Assert.True(result.LetsQuitGo);
        Assert.NotNull(rig.Store.JournalOnDisk!.LidAction); // evidência fica para o backstop
        Assert.Contains(rig.Notifier.Posts, p => p.Title == "Restauração incompleta");
        Assert.NotEmpty(rig.Manager.Warnings);
    }

    [Fact]
    public async Task Fim_incompleto_sem_backstop_segura_a_saida_e_retenta()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        rig.Sleep.FailWriteIf = v => v.Ac == 1;
        rig.Backstop.State = BackstopState.Missing;
        rig.Backstop.FailInstall = true;

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.Equal(EndOutcome.Incomplete, result.Outcome);
        Assert.False(result.LetsQuitGo);
        Assert.Contains(SessionManager.RetryDelay, rig.Scheduler.PendingDelays);

        rig.Sleep.FailWriteIf = null;
        rig.Backstop.FailInstall = false;
        rig.Scheduler.Advance(SessionManager.RetryDelay);
        await SessionRig.Until(() => rig.Sleep.Lid.Ac == 1);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    [Fact]
    public async Task Fim_pelo_timer_em_endsAt()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(1));

        rig.Scheduler.Advance(TimeSpan.FromMinutes(59));
        Assert.True(rig.Manager.IsActive);
        rig.Scheduler.Advance(TimeSpan.FromMinutes(1));
        await SessionRig.Until(() => !rig.Manager.IsActive && rig.Sleep.Lid.Ac == 1);
        Assert.Contains(rig.Notifier.Posts, p => p.Body.StartsWith("O tempo acabou", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R2_fim_restaura_o_valor_anterior_original()
    {
        var rig = new SessionRig();
        rig.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 2, 3);
        await rig.Manager.StartAsync(FourHours);
        await rig.Manager.EndAsync(EndReason.User);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 2, 3), rig.Sleep.Lid);
    }

    [Fact]
    public async Task RestoreAll_le_o_disco_e_nao_uma_copia_em_memoria()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        // Outra transação (ou o backstop) gravou entradas que o manager nunca viu em memória.
        rig.Store.JournalOnDisk = rig.Store.JournalOnDisk! with
        {
            EnergySaver = new EnergySaverEntry(FakeSchemes.Balanced, 20, rig.Clock.Now, "lid"),
            FrozenProcesses = [SessionRig.Frozen()],
        };
        rig.Energy.Current = new EnergySaverValues(FakeSchemes.Balanced, 100);

        var result = await rig.Manager.EndAsync(EndReason.User);

        Assert.Equal(EndOutcome.Restored, result.Outcome);
        Assert.Equal(20u, rig.Energy.Current.DcThreshold);
        Assert.Equal(1, rig.LidUndo.Calls);
        Assert.False(rig.Store.JournalOnDisk!.IsDirty());
    }

    // ------------------------------------------------------------------ diário corrompido

    [Fact]
    public async Task R5_diario_corrompido_bloqueia_tudo_e_fica_intacto()
    {
        var rig = new SessionRig();
        rig.Store.CorruptJournal = true;

        Assert.Equal(StartOutcome.JournalUnreadable, await rig.Manager.StartAsync(FourHours));
        Assert.NotNull(rig.Manager.LastError);

        var end = await rig.Manager.EndAsync(EndReason.User);
        Assert.Equal(EndOutcome.JournalUnreadable, end.Outcome);
        Assert.False(end.LetsQuitGo);
        Assert.Equal(0, rig.Scheduler.PendingCount); // sem timer: espera pedido novo

        var ran = false;
        var value = await rig.Manager.RunExclusiveAsync("teste", _ => { ran = true; return Task.FromResult(42); });
        Assert.False(ran);
        Assert.Equal(0, value);

        await rig.Manager.SetEnergySaverAsync(true, EnergySaverCause.Battery);
        await rig.Manager.UndoLidActionsAsync();
        await rig.Manager.ReconcileAsync();

        Assert.True(rig.Store.CorruptJournal);
        Assert.Empty(rig.Store.JournalWrites);
        Assert.Empty(rig.Store.SessionWrites);
        Assert.DoesNotContain("DeleteSession", rig.Store.Operations);
        Assert.Empty(rig.Sleep.Writes);
        Assert.Empty(rig.Energy.Writes);
        Assert.Equal(0, rig.LidUndo.Calls);
        Assert.Equal(StartOutcome.RefusedEndPending, await rig.Manager.StartAsync(FourHours));
    }

    // ------------------------------------------------------------------ extensão

    [Fact]
    public async Task Extensao_rearma_backstop_grava_sessao_e_reagenda_o_timer()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(TimeSpan.FromHours(1));
        var queriesBefore = rig.Backstop.Queries.Count;

        Assert.Equal(ExtendOutcome.Extended, await rig.Manager.ExtendAsync(TimeSpan.FromMinutes(30)));

        Assert.True(rig.Backstop.Queries.Count > queriesBefore);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromMinutes(90), rig.Store.SessionOnDisk!.EndsAt);
        Assert.Equal(new[] { 1800 }, rig.Store.SessionOnDisk.Extensions.ToArray());
        rig.Scheduler.Advance(TimeSpan.FromMinutes(60));
        Assert.True(rig.Manager.IsActive);
        rig.Scheduler.Advance(TimeSpan.FromMinutes(30));
        await SessionRig.Until(() => !rig.Manager.IsActive);
    }

    [Fact]
    public async Task Extensao_respeita_o_teto_e_falha_sem_backstop()
    {
        var rig = new SessionRig();
        Assert.Equal(ExtendOutcome.NoSession, await rig.Manager.ExtendAsync(TimeSpan.FromHours(1)));

        await rig.Manager.StartAsync(TimeSpan.FromHours(1));
        await rig.Manager.ExtendAsync(TimeSpan.FromDays(400));
        Assert.Equal(rig.Clock.Now + TimeSpan.FromSeconds(rig.Config.Current.MaxDuration), rig.Store.SessionOnDisk!.EndsAt);

        var before = rig.Store.SessionOnDisk;
        rig.Backstop.State = BackstopState.Missing;
        rig.Backstop.FailInstall = true;
        Assert.Equal(ExtendOutcome.Failed, await rig.Manager.ExtendAsync(TimeSpan.FromHours(1)));
        Assert.Equal(before, rig.Store.SessionOnDisk);
    }

    // ------------------------------------------------------------------ avisos, contagem

    [Fact]
    public async Task Fail_vai_para_Warnings_e_log_e_dispara_Changed()
    {
        var rig = new SessionRig();
        var changes = 0;
        rig.Manager.Changed += () => changes++;
        rig.Manager.Fail("Slack não congelou");
        Assert.Contains("Slack não congelou", rig.Manager.Warnings);
        Assert.True(rig.Log.Contains("Slack não congelou"));
        Assert.True(changes > 0);

        await rig.Manager.StartAsync(FourHours);
        Assert.Empty(rig.Manager.Warnings); // sessão nova começa sem avisos velhos
        rig.Manager.PauseCountdown();
        Assert.True(rig.Manager.CountdownPaused);
        rig.Manager.ResumeCountdown();
        Assert.False(rig.Manager.CountdownPaused);
    }

    [Fact]
    public async Task RunExclusive_entrega_o_diario_do_disco_e_chamada_aninhada_nao_trava()
    {
        var rig = new SessionRig();
        rig.Store.JournalOnDisk = SessionRig.WithLidAction(2, 2);

        var nested = await rig.Manager.RunExclusiveAsync("fora", async tx =>
        {
            Assert.Equal(2u, tx.Journal.LidAction!.AcPrevious);
            // Ex.: LidActions → FloorRuleDriver.EvaluateNowAsync → SetEnergySaverAsync, de dentro da transação.
            return await rig.Manager.RunExclusiveAsync("dentro", inner => Task.FromResult(ReferenceEquals(inner, tx)));
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(nested);
        Assert.Equal(1, rig.Lock.Acquisitions);
    }

    private static void AssertNothingChanged(SessionRig rig)
    {
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), rig.Sleep.Lid);
        Assert.DoesNotContain(rig.Sleep.Writes, w => w.Ac == 0 || w.Dc == 0);
        Assert.False(rig.Store.JournalOnDisk?.IsDirty() ?? false);
        Assert.Null(rig.Store.SessionOnDisk);
        Assert.False(rig.Sleep.Holding);
        Assert.False(rig.Manager.IsActive);
    }
}
