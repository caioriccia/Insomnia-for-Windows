using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Model;
using Insomnia.Core.Power;
using Insomnia.Core.Session;

/// Pisos de bateria (desenho §6.2 "Pisos"): FloorRules pura e FloorRuleDriver sobre um manager real.
public class FloorTests
{
    private static readonly Config Cfg = Config.Default; // lowPowerFloor 40, endFloor 10, Economia na tampa ligada

    private static PowerStatus Battery(int? percent) => new(OnAc: false, BatteryPercent: percent);
    private static PowerStatus Ac(int? percent) => new(OnAc: true, BatteryPercent: percent);

    // ------------------------------------------------------------------ FloorRules

    [Fact]
    public void Abaixo_do_piso_de_fim_encerra_so_na_bateria()
    {
        Assert.Equal(EndReason.BatteryFloor, FloorRules.Evaluate(Battery(9), LidState.Open, Cfg, false, 0).End);
        Assert.Null(FloorRules.Evaluate(Ac(9), LidState.Open, Cfg, false, 0).End);
        Assert.Null(FloorRules.Evaluate(Battery(10), LidState.Open, Cfg, false, 0).End); // no piso não encerra
    }

    [Fact]
    public void Piso_de_fim_zero_nunca_encerra()
    {
        var cfg = Cfg with { EndFloor = 0 };
        Assert.Null(FloorRules.Evaluate(Battery(1), LidState.Open, cfg, false, 0).End);
        Assert.Null(FloorRules.Evaluate(Battery(null), LidState.Open, cfg, false, 5).End);
    }

    [Fact]
    public void Bateria_ilegivel_duas_vezes_na_bateria_encerra()
    {
        Assert.Null(FloorRules.Evaluate(Battery(null), LidState.Open, Cfg, false, 1).End);
        Assert.Equal(EndReason.BatteryUnreadable, FloorRules.Evaluate(Battery(null), LidState.Open, Cfg, false, 2).End);
        Assert.Null(FloorRules.Evaluate(Ac(null), LidState.Open, Cfg, false, 2).End);
    }

    [Fact]
    public void Leitura_ilegivel_nunca_liga_a_Economia()
    {
        var d = FloorRules.Evaluate(Battery(null), LidState.Open, Cfg, false, 1);
        Assert.Null(d.EnergySaver);
        Assert.Null(d.Cause);
    }

    [Fact]
    public void Causa_bateria_abaixo_do_piso_de_Economia_liga_se_nao_for_nossa()
    {
        var d = FloorRules.Evaluate(Battery(39), LidState.Open, Cfg, false, 0);
        Assert.Equal<(bool?, EnergySaverCause?)>((true, EnergySaverCause.Battery), (d.EnergySaver, d.Cause));
        Assert.Null(FloorRules.Evaluate(Battery(39), LidState.Open, Cfg, true, 0).EnergySaver); // já é nossa
        Assert.Null(FloorRules.Evaluate(Battery(40), LidState.Open, Cfg, false, 0).Cause);      // no piso, sem causa
        Assert.Null(FloorRules.Evaluate(Ac(20), LidState.Open, Cfg, false, 0).Cause);           // na tomada, sem causa
    }

    [Fact]
    public void Causa_tampa_fechada_depende_da_opcao()
    {
        var d = FloorRules.Evaluate(Ac(90), LidState.Closed, Cfg, false, 0);
        Assert.Equal<(bool?, EnergySaverCause?)>((true, EnergySaverCause.Lid), (d.EnergySaver, d.Cause));
        Assert.Null(FloorRules.Evaluate(Ac(90), LidState.Closed, Cfg with { EnergySaverOnLidClose = false }, false, 0).Cause);
        Assert.Null(FloorRules.Evaluate(Ac(90), LidState.Unknown, Cfg, false, 0).Cause);
        // Bateria vence tampa quando as duas valem.
        Assert.Equal(EnergySaverCause.Battery, FloorRules.Evaluate(Battery(30), LidState.Closed, Cfg, false, 0).Cause);
    }

    [Fact]
    public void Sem_causa_e_nossa_desliga_sem_histerese()
    {
        Assert.True(FloorRules.Evaluate(Battery(40), LidState.Open, Cfg, true, 0).EnergySaver is false);
        Assert.Null(FloorRules.Evaluate(Battery(40), LidState.Open, Cfg, false, 0).EnergySaver);
    }

    // ------------------------------------------------------------------ FloorRuleDriver

    private sealed class DriverRig
    {
        public SessionRig Rig { get; } = new();
        public FakePowerEvents Power { get; } = new();
        public FloorRuleDriver Driver { get; }

        public DriverRig()
        {
            Driver = new FloorRuleDriver(Rig.Manager, Power, Rig.Scheduler, Rig.Config, () => Rig.Lid, Rig.Log);
            Driver.Start();
        }
    }

    [Fact]
    public async Task Driver_so_age_com_sessao_ativa()
    {
        var d = new DriverRig();
        d.Power.Power = Battery(5);
        await d.Driver.EvaluateNowAsync();
        Assert.Empty(d.Rig.Energy.Writes);
        Assert.Empty(d.Rig.Store.Operations);
    }

    [Fact]
    public async Task Driver_liga_Economia_na_bateria_baixa_e_avisa()
    {
        var d = new DriverRig();
        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));
        d.Power.RaisePower(Battery(35));
        await SessionRig.Until(() => d.Rig.Manager.EnergySaverOurs);

        Assert.Equal(100u, d.Rig.Energy.Current.DcThreshold);
        Assert.Contains(d.Rig.Notifier.Posts, p => p.Title == "Economia de energia ligada");

        d.Power.RaisePower(Ac(36));
        await SessionRig.Until(() => !d.Rig.Manager.EnergySaverOurs);
        Assert.Equal(20u, d.Rig.Energy.Current.DcThreshold);
    }

    [Fact]
    public async Task Driver_causa_tampa_e_silenciosa()
    {
        var d = new DriverRig();
        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));
        d.Rig.Lid = LidState.Closed;

        await d.Driver.EvaluateNowAsync();

        Assert.True(d.Rig.Manager.EnergySaverOurs);
        Assert.Equal("lid", d.Rig.Store.JournalOnDisk!.EnergySaver!.Cause);
        Assert.Empty(d.Rig.Notifier.Posts);
    }

    [Fact]
    public async Task Driver_encerra_abaixo_do_piso_de_fim()
    {
        var d = new DriverRig();
        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));
        d.Power.Power = Battery(8);

        await d.Driver.EvaluateNowAsync();

        Assert.False(d.Rig.Manager.IsActive);
        Assert.Equal(1u, d.Rig.Sleep.Lid.Ac);
        Assert.Contains(d.Rig.Notifier.Posts, p => p.Body.StartsWith("Bateria abaixo do piso", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Driver_rele_bateria_ilegivel_em_30s_e_encerra_na_segunda()
    {
        var d = new DriverRig();
        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));
        d.Power.Power = Battery(null);

        await d.Driver.EvaluateNowAsync();
        Assert.True(d.Rig.Manager.IsActive);
        Assert.Contains(FloorRuleDriver.UnreadableRetry, d.Rig.Scheduler.PendingDelays);

        d.Rig.Scheduler.Advance(FloorRuleDriver.UnreadableRetry);
        await SessionRig.Until(() => !d.Rig.Manager.IsActive);
        Assert.Contains(d.Rig.Notifier.Posts, p => p.Body.StartsWith("Não deu para ler a bateria", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Driver_leitura_boa_zera_as_falhas()
    {
        var d = new DriverRig();
        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));
        d.Power.Power = Battery(null);
        await d.Driver.EvaluateNowAsync();
        d.Power.Power = Battery(80);
        await d.Driver.EvaluateNowAsync();
        d.Power.Power = Battery(null);
        await d.Driver.EvaluateNowAsync();
        Assert.True(d.Rig.Manager.IsActive);
    }

    [Fact]
    public async Task Driver_avalia_quando_a_sessao_comeca()
    {
        var d = new DriverRig();
        d.Power.Power = Battery(30);

        await d.Rig.Manager.StartAsync(TimeSpan.FromHours(4));

        await SessionRig.Until(() => d.Rig.Manager.EnergySaverOurs);
        d.Driver.Stop();
    }
}
