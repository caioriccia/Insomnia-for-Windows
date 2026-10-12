using Insomnia.App.Tray;
using Insomnia.Core.Model;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Tests.Ui.Menu;

/// Sobrecarga Build(s, config) do menu da UI nova (desenho-ui §3.6/§5.4).
public class StatusMenuBuilderPainelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    private static List<string> Texts(IReadOnlyList<MenuLine> lines) => [.. lines.Select(l => l.Text)];

    [Fact]
    public void Ocioso_tem_iniciar_sessao_do_padrao_e_abrir_painel_antes_de_configuracoes()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot { Now = Now }, Config.Default);

        Assert.Equal(["Sem sessão ativa", "", "Iniciar sessão de 4h", "Abrir painel", "Configurações…", "", "Sair"], Texts(lines));
        var start = Assert.Single(lines, l => l.Command == MenuCommand.StartDefault);
        Assert.Equal(MenuLineKind.Action, start.Kind);
        Assert.Equal("14400", start.Argument);
        Assert.Equal(MenuLineKind.Action, Assert.Single(lines, l => l.Command == MenuCommand.OpenPanel).Kind);
    }

    [Fact]
    public void Com_sessao_so_abrir_painel()
    {
        var session = new SessionModel(Now, Now + TimeSpan.FromHours(4), []);
        var lines = StatusMenuBuilder.Build(new StatusSnapshot { Now = Now, Session = session }, Config.Default);

        Assert.DoesNotContain(lines, l => l.Command == MenuCommand.StartDefault);
        Assert.Equal(["Sessão: 4:00:00 · termina 22:00", "", "Abrir painel", "Configurações…", "", "Sair"], Texts(lines));
    }

    [Fact]
    public void Rotulo_segue_o_preset_padrao_do_config()
    {
        var config = Config.Default with { DefaultPreset = 5400 };
        var start = Assert.Single(StatusMenuBuilder.Build(new StatusSnapshot { Now = Now }, config), l => l.Command == MenuCommand.StartDefault);

        Assert.Equal("Iniciar sessão de 1h30m", start.Text);
        Assert.Equal("5400", start.Argument);
    }

    [Fact]
    public void Avisos_e_acoes_continuam_iguais_ao_menu_velho_antes_do_separador()
    {
        var s = new StatusSnapshot
        {
            Now = Now,
            Warnings = ["Backstop não armado"],
            WaitingAudio = [new WaitingAudio("fone", "Fone")],
        };
        var old = StatusMenuBuilder.Build(s);
        var lines = StatusMenuBuilder.Build(s, Config.Default);

        var firstSeparator = old.ToList().FindIndex(l => l.Kind == MenuLineKind.Separator);
        Assert.Equal(old.Take(firstSeparator + 1), lines.Take(firstSeparator + 1));
    }
}
