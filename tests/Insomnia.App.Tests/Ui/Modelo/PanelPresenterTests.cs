namespace Insomnia.App.Tests.Ui.Modelo;

// Dentro do namespace: Insomnia.App.Tests.Ui.Config (outra pasta de testes) venceria o tipo Config.
using System.Collections.Immutable;
using Insomnia.App.Tray;
using Insomnia.App.Ui;
using Insomnia.App.Ui.Model;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Config = Insomnia.Core.Model.Config;
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;
using SessionModel = Insomnia.Core.Model.Session;

public class PanelPresenterTests
{
    // 18:00 em -03:00.
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));
    private static readonly Config Cfg = Config.Default;

    private static StatusSnapshot Idle => new() { Now = Now };

    /// Sessão de 4 h que começou há `elapsed`.
    private static StatusSnapshot Running(TimeSpan elapsed) =>
        new() { Now = Now, Session = new SessionModel(Now - elapsed, Now - elapsed + TimeSpan.FromHours(4), []) };

    private static StatusSnapshot Machine(StatusSnapshot s) => s with
    {
        Lid = LidState.Open,
        Power = new PowerStatus(OnAc: true, BatteryPercent: 87),
        WifiConnected = true,
        WifiSsid = "casa",
        FrozenApps = 2,
        DockerPaused = true,
        LastGap = TimeSpan.FromSeconds(42),
    };

    // ------------------------------------------------------------------ modo Iniciar

    [Fact]
    public void Iniciar_tem_um_chip_por_preset_e_seleciona_o_padrao()
    {
        var p = PanelPresenter.From(Idle, Cfg, selectedPreset: null);

        Assert.Equal(PanelMode.Start, p.Mode);
        Assert.Equal(["30m", "1h", "2h", "4h", "8h", "12h", "1d", "3d"], p.Chips.Select(c => c.Label));
        Assert.Equal(Cfg.Presets.Select(s => TimeSpan.FromSeconds(s)), p.Chips.Select(c => c.Duration));
        Assert.Equal(["4h"], p.Chips.Where(c => c.Selected).Select(c => c.Label));
        Assert.Equal("4h", p.Headline);
        Assert.Equal("terminaria às 22:00", p.Subline);
        Assert.False(p.ExtendEmphasis);
    }

    [Fact]
    public void Iniciar_marca_o_preset_escolhido()
    {
        var p = PanelPresenter.From(Idle, Cfg, TimeSpan.FromHours(1));

        Assert.Equal(["1h"], p.Chips.Where(c => c.Selected).Select(c => c.Label));
        Assert.Equal("1h", p.Headline);
        Assert.Equal("terminaria às 19:00", p.Subline);
    }

    [Fact]
    public void Iniciar_em_outro_dia_mostra_a_data()
    {
        var p = PanelPresenter.From(Idle, Cfg, TimeSpan.FromDays(1));

        Assert.Equal("terminaria às 12/10 18:00", p.Subline);
    }

    [Fact]
    public void Iniciar_mostra_so_tampa_bateria_e_wifi_sem_acoes_nem_faixa()
    {
        var s = Machine(Idle) with { Lid = LidState.Closed, CountdownPaused = true };

        var p = PanelPresenter.From(s, Cfg, null);

        Assert.Equal(["Tampa", "Bateria", "Wi-Fi"], p.Rows.Select(r => r.Label));
        Assert.Equal(["fechada", "87 % · carregando", "casa"], p.Rows.Select(r => r.Value));
        Assert.Null(p.Lid);
    }

    // ------------------------------------------------------------------ modo Sessão

    [Fact]
    public void Sessao_mostra_contagem_da_bandeja_e_hora_do_fim()
    {
        var s = Running(TimeSpan.FromMinutes(17) + TimeSpan.FromSeconds(45));

        var p = PanelPresenter.From(s, Cfg, null);

        Assert.Equal(PanelMode.Session, p.Mode);
        Assert.Equal(TrayTexts.Countdown(s.Session!, Now), p.Headline);
        Assert.Equal("3:42:15", p.Headline);
        Assert.Equal("termina às 21:42", p.Subline);
        Assert.Equal(["+15 min", "+1 h", "+4 h"], p.Chips.Select(c => c.Label));
        Assert.Equal(PanelPresenter.ExtendSteps, p.Chips.Select(c => c.Duration));
        Assert.DoesNotContain(p.Chips, c => c.Selected);
        Assert.False(p.ExtendEmphasis);
        Assert.Null(p.Lid);
    }

    [Fact]
    public void Sessao_pausada_avisa_na_sublinha()
    {
        var s = Running(TimeSpan.Zero) with { CountdownPaused = true };

        Assert.Equal("termina às 22:00 · pausada com a tampa fechada", PanelPresenter.From(s, Cfg, null).Subline);
    }

    [Fact]
    public void Fim_proximo_conta_os_minutos_e_destaca_o_mais_15()
    {
        // Faltam 4:30.
        var s = Running(TimeSpan.FromHours(4) - TimeSpan.FromSeconds(270)) with { CountdownPaused = true };

        var p = PanelPresenter.From(s, Cfg, null);

        Assert.Equal("termina às 18:04 · pausada com a tampa fechada · faltam 5 min", p.Subline);
        Assert.True(p.ExtendEmphasis);
        Assert.Equal(PillTone.EndingSoon, p.Tone);
    }

    [Fact]
    public void Cinco_minutos_exatos_ainda_nao_destaca()
    {
        var p = PanelPresenter.From(Running(TimeSpan.FromMinutes(235)), Cfg, null);

        Assert.False(p.ExtendEmphasis);
        Assert.Equal("termina às 18:05", p.Subline);
    }

    [Fact]
    public void Sessao_com_tampa_aberta_tem_acoes_e_ultima_queda_sem_faixa()
    {
        var p = PanelPresenter.From(Machine(Running(TimeSpan.Zero)), Cfg, null);

        Assert.Equal(["Tampa", "Bateria", "Wi-Fi", "Ações", "Última queda"], p.Rows.Select(r => r.Label));
        Assert.Equal(["aberta", "87 % · carregando", "casa", "2 apps congelados · Docker pausado", "42 s"], p.Rows.Select(r => r.Value));
        Assert.Null(p.Lid);
    }

    [Fact]
    public void Tampa_fechada_mostra_a_faixa_com_o_que_esta_ativo()
    {
        var s = Machine(Running(TimeSpan.Zero)) with { Lid = LidState.Closed, CountdownPaused = true, DockerPaused = false };

        var p = PanelPresenter.From(s, Cfg, null);

        Assert.Equal(new LidBanner("Tampa fechada", "2 apps congelados · áudio mudo · tela apagada"), p.Lid);
        Assert.Equal("2 apps congelados · áudio mudo", p.Rows.Single(r => r.Label == "Ações").Value);
        Assert.Equal(PillTone.Paused, p.Tone);
    }

    [Fact]
    public void Faixa_omite_o_que_o_config_nao_faz_e_o_que_nao_aconteceu()
    {
        var cfg = Cfg with { MuteOnLidClose = false };
        var s = Running(TimeSpan.Zero) with { Lid = LidState.Closed, FrozenApps = 1 };

        var p = PanelPresenter.From(s, cfg, null);

        Assert.Equal("1 app congelado · tela apagada", p.Lid!.Detail);
        Assert.Equal("1 app congelado", p.Rows.Single(r => r.Label == "Ações").Value);

        var nada = PanelPresenter.From(Running(TimeSpan.Zero) with { CountdownPaused = true },
            cfg with { DarkenDisplayOnLidClose = false }, null);
        Assert.Equal("", nada.Lid!.Detail);
        Assert.DoesNotContain(nada.Rows, r => r.Label == "Ações");
    }

    [Fact]
    public void Linhas_desconhecidas_somem()
    {
        var p = PanelPresenter.From(Running(TimeSpan.Zero), Cfg, null);

        Assert.Empty(p.Rows);
    }

    [Theory]
    [InlineData(true, 50, "50 % · carregando")]
    [InlineData(false, 50, "50 % · na bateria")]
    [InlineData(true, null, "na tomada")]
    public void Bateria_por_estado(bool onAc, int? pct, string expected)
    {
        var p = PanelPresenter.From(Idle with { Power = new PowerStatus(onAc, pct) }, Cfg, null);

        Assert.Equal(expected, p.Rows.Single(r => r.Label == "Bateria").Value);
    }

    [Fact]
    public void Sem_tomada_e_sem_percentual_a_bateria_some()
    {
        var p = PanelPresenter.From(Idle with { Power = new PowerStatus(false, null) }, Cfg, null);

        Assert.Empty(p.Rows);
    }

    [Theory]
    [InlineData(false, "casa", "desconectado")]
    [InlineData(true, " ", "conectado")]
    [InlineData(true, " casa ", "casa")]
    public void Wifi_por_estado(bool connected, string ssid, string expected)
    {
        var p = PanelPresenter.From(Idle with { WifiConnected = connected, WifiSsid = ssid }, Cfg, null);

        Assert.Equal(expected, p.Rows.Single(r => r.Label == "Wi-Fi").Value);
    }

    [Fact]
    public void ActionsText_so_com_o_que_o_retrato_prova()
    {
        Assert.Equal("", PanelPresenter.ActionsText(Idle));
        Assert.Equal("1 app congelado", PanelPresenter.ActionsText(Idle with { FrozenApps = 1 }));
        Assert.Equal("3 apps congelados · Docker pausado", PanelPresenter.ActionsText(Idle with { FrozenApps = 3, DockerPaused = true }));
    }

    // ------------------------------------------------------------------ avisos

    [Fact]
    public void Avisos_na_ordem_do_menu_com_acoes_e_sem_repetir()
    {
        var s = Running(TimeSpan.Zero) with
        {
            Browsers =
            [
                new BrowserStatus("chrome", "Google Chrome", Unthrottled: false),
                new BrowserStatus("edge", "Microsoft Edge", Unthrottled: true),
            ],
            RelaunchProblems = ImmutableDictionary<string, string>.Empty
                .Add("msedge", "Edge não reabriu")
                .Add("chrome", "Chrome não fechou"),
            NetworkWarning = "Sem rede",
            LastError = "⚠ Sem rede",          // mesma frase com o prefixo: some
            Warnings = ["Bateria fraca", "Sem rede"],
            WaitingAudio = [new WaitingAudio("dev-1", "Fones")],
        };

        var notices = PanelPresenter.From(s, Cfg, null).Notices;

        Assert.Equal(
        [
            "Google Chrome está limitado",
            "Chrome não fechou",
            "Edge não reabriu",
            "Sem rede",
            "Bateria fraca",
            "Fones continua mudo: esperando a saída voltar",
        ], notices.Select(n => n.Text));
        Assert.All(notices, n => Assert.Equal(NoticeKind.Caution, n.Kind));

        Assert.Equal(new PanelNotice(NoticeKind.Caution, "Google Chrome está limitado",
            BrowserThrottle.RelaunchMenuLabel("Google Chrome"), MenuCommand.Relaunch, "chrome"), notices[0]);
        Assert.Equal(new PanelNotice(NoticeKind.Caution, "Fones continua mudo: esperando a saída voltar",
            "Parar de esperar Fones", MenuCommand.StopWaitingAudio, "dev-1"), notices[^1]);
        Assert.All(notices.Skip(1).SkipLast(1), n => Assert.Null(n.ActionLabel));
    }

    [Fact]
    public void Um_aviso_por_navegador_limitado_cada_um_com_seu_relancar()
    {
        var s = Idle with
        {
            Browsers =
            [
                new BrowserStatus("chrome", "Google Chrome", Unthrottled: false),
                new BrowserStatus("brave", "Brave", Unthrottled: false),
            ],
        };

        var notices = PanelPresenter.From(s, Cfg, null).Notices;

        Assert.Equal(["Google Chrome está limitado", "Brave está limitado"], notices.Select(n => n.Text));
        Assert.Equal(["Relançar Google Chrome sem limitação", "Relançar Brave sem limitação"], notices.Select(n => n.ActionLabel));
        Assert.Equal(["chrome", "brave"], notices.Select(n => n.Argument));
        Assert.All(notices, n => Assert.Equal(MenuCommand.Relaunch, n.Command));
    }

    [Fact]
    public void Aviso_de_audio_repetido_ganha_a_acao_em_vez_de_duplicar()
    {
        var s = Idle with
        {
            Warnings = ["Fones continua mudo: esperando a saída voltar"],
            WaitingAudio = [new WaitingAudio("dev-1", "Fones"), new WaitingAudio("dev-2", "Fones")],
        };

        var notices = PanelPresenter.From(s, Cfg, null).Notices;

        // O aviso que já existia vira o par da primeira saída; a segunda saída de mesmo nome
        // mantém a sua ação (o menu também mostra os dois "Parar de esperar").
        Assert.Equal(2, notices.Count);
        Assert.Equal(["dev-1", "dev-2"], notices.Select(n => n.Argument));
        Assert.All(notices, n => Assert.Equal(MenuCommand.StopWaitingAudio, n.Command));
    }

    [Fact]
    public void Avisos_tambem_aparecem_sem_sessao()
    {
        var p = PanelPresenter.From(Idle with { LastError = "não deu" }, Cfg, null);

        Assert.Equal([new PanelNotice(NoticeKind.Caution, "não deu")], p.Notices);
        Assert.Equal(PillTone.Warning, p.Tone);
    }
}
