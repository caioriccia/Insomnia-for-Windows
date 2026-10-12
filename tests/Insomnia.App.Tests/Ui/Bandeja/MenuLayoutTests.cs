namespace Insomnia.App.Tests.Ui.Bandeja;

// Dentro do namespace: Insomnia.App.Tests.Ui.Config/Menu (outras pastas de testes) venceriam os tipos.
using Insomnia.App.Tray;
using Insomnia.App.Ui.Menu;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.Core.Abstractions;
using ConfigModel = Insomnia.Core.Model.Config;
using SessionModel = Insomnia.Core.Model.Session;

/// Contas do menu da bandeja sem janela (desenho-ui §3.6): posição, atualização no lugar e
/// a duração do "Iniciar sessão de …".
public class MenuLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    // Monitor 1920×1080 com a barra de 48 px embaixo.
    private static readonly PxRect WorkArea = new(0, 0, 1920, 1032);

    [Fact]
    public void Abre_com_o_canto_inferior_direito_no_cursor_na_escala_do_monitor()
    {
        var r = MenuLayout.Place(WorkArea, heightEpx: 300, scale: 1.5, cursorX: 1600, cursorY: 1060);

        Assert.Equal(288 * 1.5, r.W);
        Assert.Equal(300 * 1.5, r.H);
        Assert.Equal(1600 - 288 * 1.5, r.X);
        // Cursor sobre a barra: o menu fica preso acima dela, encostado no workArea.
        Assert.Equal(1032 - 300 * 1.5, r.Y);
    }

    [Fact]
    public void Cursor_no_meio_da_tela_poe_o_menu_acima_e_a_esquerda()
    {
        var r = MenuLayout.Place(WorkArea, heightEpx: 200, scale: 1, cursorX: 900, cursorY: 700);
        Assert.Equal(new PxRect(900 - 288, 700 - 200, 288, 200), r);
    }

    [Fact]
    public void Cursor_no_canto_esquerdo_nao_deixa_o_menu_sair_da_tela()
    {
        var r = MenuLayout.Place(WorkArea, heightEpx: 200, scale: 1, cursorX: 10, cursorY: 100);
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Monitor_secundario_usa_o_proprio_workArea()
    {
        // Segundo monitor à direita, barra no topo (workArea começa em 40).
        var area = new PxRect(1920, 40, 2560, 1400);
        var r = MenuLayout.Place(area, heightEpx: 300, scale: 1, cursorX: 1930, cursorY: 20);
        Assert.Equal(1920, r.X);
        Assert.Equal(40, r.Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Escala_invalida_vira_1(double scale)
    {
        var r = MenuLayout.Place(WorkArea, heightEpx: 100, scale: scale, cursorX: 900, cursorY: 700);
        Assert.Equal(288, r.W);
        Assert.Equal(100, r.H);
    }

    [Fact]
    public void So_textos_diferentes_atualiza_no_lugar()
    {
        var before = Rows(Session(TimeSpan.FromHours(3)));
        var after = Rows(Session(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1)));

        Assert.NotEqual(before[0].Text, after[0].Text);   // a contagem do cabeçalho andou
        Assert.True(MenuLayout.SameShape(before, after));
    }

    [Fact]
    public void Sessao_que_comeca_ou_aviso_novo_recriam_as_linhas()
    {
        var idle = Rows(new StatusSnapshot { Now = Now });
        var running = Rows(Session(TimeSpan.FromHours(3)));
        Assert.False(MenuLayout.SameShape(idle, running));   // some "Iniciar sessão de 4h"

        var warned = Rows(Session(TimeSpan.FromHours(3)) with { Warnings = ["Backstop não armado"] });
        Assert.False(MenuLayout.SameShape(running, warned));
    }

    [Fact]
    public void Argumento_diferente_no_mesmo_lugar_recria()
    {
        IReadOnlyList<MenuRow> a = [new(MenuRowKind.Command, null, "Relançar", MenuCommand.Relaunch, "chrome")];
        IReadOnlyList<MenuRow> b = [new(MenuRowKind.Command, null, "Relançar", MenuCommand.Relaunch, "msedge")];
        Assert.False(MenuLayout.SameShape(a, b));
        Assert.True(MenuLayout.SameShape(a, a));
        Assert.True(MenuLayout.SameShape([], []));
    }

    [Theory]
    [InlineData("14400", 14400)]
    [InlineData("1800", 1800)]
    [InlineData(null, 7200)]
    [InlineData("", 7200)]
    [InlineData("abc", 7200)]
    [InlineData("0", 7200)]
    [InlineData("-60", 7200)]
    [InlineData("1.5", 7200)]
    public void Duracao_do_iniciar_vem_do_argumento_ou_do_padrao(string? argument, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MenuLayout.StartDuration(argument, 7200));

    [Fact]
    public void Item_iniciar_do_menu_pede_o_preset_padrao()
    {
        var start = Assert.Single(Rows(new StatusSnapshot { Now = Now }), r => r.Command == MenuCommand.StartDefault);
        Assert.Equal(TimeSpan.FromSeconds(ConfigModel.Default.DefaultPreset), MenuLayout.StartDuration(start.Argument, 60));
    }

    private static StatusSnapshot Session(TimeSpan remaining) =>
        new() { Now = Now, Session = new SessionModel(Now - TimeSpan.FromHours(1), Now + remaining, []), Lid = LidState.Open };

    private static IReadOnlyList<MenuRow> Rows(StatusSnapshot s) =>
        MenuPresenter.From(StatusMenuBuilder.Build(s, ConfigModel.Default));
}
