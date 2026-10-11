using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Model;
using Insomnia.Core.Power;
using Insomnia.Core.Session;

/// Reapply (reconcile com sessão válida) interrompido por um fim em linha: Fail() dispara
/// Changed, o FloorRuleDriver acorda com a bateria abaixo do piso e encerra a sessão NA MESMA
/// transação. Depois disso o reapply não pode religar o hold, regravar LIDACTION=0 nem armar
/// o timer de uma sessão que já foi desfeita (R4: fim = sessão apagada → RestoreAll; R7: o
/// hold é do processo e não pode sobrar sem sessão).
public class ReapplyEndTests
{
    private static PowerStatus Battery(int percent) => new(OnAc: false, BatteryPercent: percent);

    private sealed class Rig
    {
        public SessionRig S { get; } = new();
        public FakePowerEvents Power { get; } = new();
        public Session Session { get; }

        public Rig(int battery)
        {
            // App morreu no meio da sessão: LIDACTION=0 com o anterior no diário.
            Session = S.ValidSession();
            S.Store.SessionOnDisk = Session;
            S.Store.JournalOnDisk = SessionRig.WithLidAction(ac: 1, dc: 1);
            S.Sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);
            Power.Power = Battery(battery);
            new FloorRuleDriver(S.Manager, Power, S.Scheduler, S.Config, () => S.Lid, S.Log).Start();
        }
    }

    private static void AssertEndedClean(Rig r)
    {
        Assert.Null(r.S.Manager.Current);
        Assert.False(r.S.Sleep.Holding, "R7: hold ligado sem sessão");
        Assert.Empty(r.S.Scheduler.PendingDelays);                                   // nenhum timer de fim órfão
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 1, 1), r.S.Sleep.Lid); // LIDACTION não voltou a 0
        Assert.Null(r.S.Store.SessionOnDisk);
        Assert.False(r.S.Store.JournalOnDisk!.IsDirty());
        Assert.True(r.S.Log.Contains("reapply interrompido"));
    }

    [Fact]
    public async Task R4_R7_backstop_falha_no_reapply_e_piso_encerra_em_linha_sem_sobrar_hold_nem_timer()
    {
        var r = new Rig(battery: 5);
        r.S.Backstop.FailInstall = true; // Fail("Backstop não armado…") ⇒ Changed ⇒ driver encerra

        await r.S.Manager.ReconcileAsync();

        AssertEndedClean(r);
        Assert.DoesNotContain(true, r.S.Sleep.HoldCalls);                 // o hold nem chegou a ser ligado
        Assert.DoesNotContain(r.S.Sleep.Writes, w => w.Ac == 0 && w.Dc == 0); // nada de LIDACTION=0 depois do fim
        Assert.Contains(r.S.Manager.Warnings, w => w.Contains("Backstop não armado"));
    }

    [Fact]
    public async Task R4_R7_fim_em_linha_durante_o_LidUndo_do_reapply_desliga_o_hold_e_nao_arma_timer()
    {
        var r = new Rig(battery: 5);
        r.S.Lid = LidState.Open;
        r.S.Store.JournalOnDisk = r.S.Store.JournalOnDisk! with { FrozenProcesses = [SessionRig.Frozen()] };
        var calls = 0;
        r.S.LidUndo.Behavior = tx =>
        {
            if (calls++ == 0) r.S.Manager.Fail("processo não retomado (teste)"); // primeiro Changed ⇒ driver encerra
            tx.Write(tx.Journal with { FrozenProcesses = [] });
            return new LidUndoResult([], new HashSet<string>());
        };

        await r.S.Manager.ReconcileAsync();

        AssertEndedClean(r);
        Assert.False(r.S.Sleep.HoldCalls[^1]); // ligou para o reapply, o fim desligou, ninguém religou
    }

    [Fact]
    public async Task Contraprova_bateria_acima_do_piso_o_reapply_segue_com_hold_e_timer()
    {
        var r = new Rig(battery: 80);
        r.S.Backstop.FailInstall = true;

        await r.S.Manager.ReconcileAsync();

        Assert.Equal(r.Session, r.S.Manager.Current);
        Assert.True(r.S.Sleep.Holding);
        Assert.Contains(r.Session.EndsAt - r.S.Clock.Now, r.S.Scheduler.PendingDelays);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 0, 0), r.S.Sleep.Lid);
        Assert.False(r.S.Log.Contains("reapply interrompido"));
    }
}
