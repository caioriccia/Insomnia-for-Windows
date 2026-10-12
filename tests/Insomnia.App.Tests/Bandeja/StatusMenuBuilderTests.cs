using System.Collections.Immutable;
using Insomnia.App.Tray;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Tests.Bandeja;

public class StatusMenuBuilderTests
{
    // 18:00 em -03:00; sessão de 4 h que termina às 22:00 locais.
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    private static SessionModel FourHours(TimeSpan elapsed) =>
        new(Now - elapsed, Now - elapsed + TimeSpan.FromHours(4), []);

    private static List<string> Texts(IReadOnlyList<MenuLine> lines) => [.. lines.Select(l => l.Text)];

    [Fact]
    public void Ocioso_sem_extras_mostra_so_contagem_separador_configuracoes_e_sair()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot { Now = Now });

        Assert.Equal(["Sem sessão ativa", "", "Configurações…", "Sair"], Texts(lines));
        Assert.Equal(MenuLineKind.Info, lines[0].Kind);
        Assert.Equal(MenuLineKind.Separator, lines[1].Kind);
        Assert.Equal(MenuCommand.Settings, lines[2].Command);
        Assert.Equal(MenuCommand.Quit, lines[3].Command);
    }

    [Fact]
    public void Build_sem_config_nao_ganha_os_comandos_da_ui_nova()
    {
        // O menu velho (TrayController) não trata OpenPanel/StartDefault: só a sobrecarga com Config os insere.
        foreach (var s in new[] { new StatusSnapshot { Now = Now }, new StatusSnapshot { Now = Now, Session = FourHours(TimeSpan.Zero) } })
            Assert.DoesNotContain(StatusMenuBuilder.Build(s), l => l.Command is MenuCommand.OpenPanel or MenuCommand.StartDefault);
    }

    [Fact]
    public void Sessao_ativa_mostra_contagem_e_hora_do_fim()
    {
        var s = new StatusSnapshot { Now = Now, Session = FourHours(TimeSpan.FromMinutes(36).Add(TimeSpan.FromSeconds(15))) };

        Assert.Equal("Sessão: 3:23:45 · termina 21:23", StatusMenuBuilder.CountdownLine(s));
    }

    [Fact]
    public void Contagem_pausada_avisa_tampa_fechada()
    {
        var s = new StatusSnapshot { Now = Now, Session = FourHours(TimeSpan.Zero), CountdownPaused = true };

        Assert.Equal("Sessão: 4:00:00 · termina 22:00 · pausada com a tampa fechada", StatusMenuBuilder.CountdownLine(s));
    }

    [Fact]
    public void Linha_da_maquina_junta_tampa_bateria_e_wifi()
    {
        var s = new StatusSnapshot
        {
            Now = Now,
            Lid = LidState.Open,
            Power = new PowerStatus(OnAc: true, BatteryPercent: 87),
            WifiConnected = true,
            WifiSsid = "casa",
        };

        Assert.Equal("Tampa: aberta · 87% carregando · Wi-Fi: casa", StatusMenuBuilder.MachineLine(s));
    }

    [Fact]
    public void Linha_da_maquina_omite_partes_desconhecidas()
    {
        Assert.Null(StatusMenuBuilder.MachineLine(new StatusSnapshot { Now = Now }));
        Assert.Equal("Tampa: fechada · 40% na bateria · Wi-Fi: desconectado", StatusMenuBuilder.MachineLine(new StatusSnapshot
        {
            Now = Now,
            Lid = LidState.Closed,
            Power = new PowerStatus(false, 40),
            WifiConnected = false,
        }));
        Assert.Equal("na tomada · Wi-Fi: conectado", StatusMenuBuilder.MachineLine(new StatusSnapshot
        {
            Now = Now,
            Power = new PowerStatus(true, null),
            WifiConnected = true,
        }));
    }

    [Fact]
    public void Linha_de_acoes_conta_apps_docker_e_ultima_queda()
    {
        Assert.Null(StatusMenuBuilder.ActionsLine(new StatusSnapshot { Now = Now }));
        Assert.Equal("1 app congelado", StatusMenuBuilder.ActionsLine(new StatusSnapshot { Now = Now, FrozenApps = 1 }));
        Assert.Equal("3 apps congelados · Docker pausado · última queda 42s", StatusMenuBuilder.ActionsLine(new StatusSnapshot
        {
            Now = Now,
            FrozenApps = 3,
            DockerPaused = true,
            LastGap = TimeSpan.FromSeconds(42),
        }));
    }

    [Fact]
    public void Navegador_limitado_vira_aviso_e_item_de_relancar_por_navegador()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            Browsers =
            [
                new BrowserStatus("chrome", "Google Chrome", Unthrottled: false),
                new BrowserStatus("msedge", "Microsoft Edge", Unthrottled: true),
                new BrowserStatus("brave", "Brave", Unthrottled: false),
            ],
        });

        var warning = Assert.Single(lines, l => l.Kind == MenuLineKind.Warning);
        Assert.Equal("⚠ Google Chrome e Brave estão limitados", warning.Text);
        var relaunch = lines.Where(l => l.Command == MenuCommand.Relaunch).ToList();
        Assert.Equal(["Relançar Google Chrome sem limitação", "Relançar Brave sem limitação"], relaunch.Select(l => l.Text));
        Assert.Equal(["chrome", "brave"], relaunch.Select(l => l.Argument));
        Assert.DoesNotContain(lines, l => l.Text.Contains("Edge", StringComparison.Ordinal));
    }

    [Fact]
    public void Avisos_entram_com_prefixo_e_sem_repeticao_antes_do_separador()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            RelaunchProblems = ImmutableDictionary<string, string>.Empty.Add("chrome", "Google Chrome não está aberto."),
            NetworkWarning = "Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows",
            LastError = "Diário de recuperação ilegível",
            Warnings = ["Diário de recuperação ilegível", "Slack não foi congelado"],
        });

        var warnings = lines.Where(l => l.Kind == MenuLineKind.Warning).Select(l => l.Text).ToList();
        Assert.Equal(
        [
            "⚠ Google Chrome não está aberto.",
            "⚠ Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows",
            "⚠ Diário de recuperação ilegível",
            "⚠ Slack não foi congelado",
        ], warnings);
        var separator = lines.ToList().FindIndex(l => l.Kind == MenuLineKind.Separator);
        Assert.True(lines.ToList().FindLastIndex(l => l.Kind == MenuLineKind.Warning) < separator);
    }

    [Fact]
    public void Saida_de_audio_esperando_ganha_aviso_e_item_parar_de_esperar()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            WaitingAudio = [new WaitingAudio("{0.0.0.00000000}.{fone}", "Fone Bluetooth")],
        });

        Assert.Contains(lines, l => l.Kind == MenuLineKind.Warning && l.Text == "⚠ Fone Bluetooth continua mudo: esperando a saída voltar");
        var stop = Assert.Single(lines, l => l.Command == MenuCommand.StopWaitingAudio);
        Assert.Equal("Parar de esperar Fone Bluetooth", stop.Text);
        Assert.Equal("{0.0.0.00000000}.{fone}", stop.Argument);
    }

    [Fact]
    public void Build_de_simulacao_mostra_a_marca()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot { Now = Now, LidSimulation = true });

        Assert.Contains(lines, l => l.Kind == MenuLineKind.Warning && l.Text == StatusMenuBuilder.SimulationMarker);
    }

    [Fact]
    public void Ordem_completa_das_linhas()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            Session = FourHours(TimeSpan.Zero),
            Lid = LidState.Open,
            Power = new PowerStatus(true, 87),
            FrozenApps = 2,
            Browsers = [new BrowserStatus("chrome", "Google Chrome", false)],
            Warnings = ["Backstop não armado"],
        });

        Assert.Equal(
        [
            "Sessão: 4:00:00 · termina 22:00",
            "Tampa: aberta · 87% carregando",
            "2 apps congelados",
            "⚠ Google Chrome está limitado",
            "Relançar Google Chrome sem limitação",
            "⚠ Backstop não armado",
            "",
            "Configurações…",
            "Sair",
        ], Texts(lines));
    }

    [Fact]
    public void Tooltip_segue_o_formato_do_desenho()
    {
        var session = FourHours(TimeSpan.FromMinutes(36).Add(TimeSpan.FromSeconds(15)));
        Assert.Equal("Insomnia · 3:23:45 · termina 21:23", TrayTexts.Tooltip(session, Now, paused: false));
        Assert.Equal(TrayTexts.IdleTooltip, TrayTexts.Tooltip(null, Now, paused: false));
    }

    [Fact]
    public void Forma_da_contagem_vem_da_duracao_inicial_e_ignora_extensoes()
    {
        // 30 min + extensão de 2 h: continua no formato MM:SS da duração inicial.
        var session = new SessionModel(Now, Now + TimeSpan.FromMinutes(150), [7200]);
        Assert.Equal("150:00", TrayTexts.Countdown(session, Now));
    }
}
