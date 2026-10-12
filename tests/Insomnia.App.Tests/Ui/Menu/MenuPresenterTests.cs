using Insomnia.App.Tray;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Model;
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.App.Tests.Ui.Menu;

public class MenuPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Primeira_info_vira_cabecalho_e_as_outras_viram_status()
    {
        var rows = MenuPresenter.From(StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            Lid = LidState.Open,
            Power = new PowerStatus(true, 87),
            FrozenApps = 2,
        }, Config.Default));

        var header = Assert.Single(rows, r => r.Kind == MenuRowKind.Header);
        Assert.Same(rows[0], header);
        Assert.Equal("Sem sessão ativa", header.Text);
        Assert.Null(header.Glyph);
        Assert.Equal(["Tampa: aberta · 87% carregando", "2 apps congelados"],
            rows.Where(r => r.Kind == MenuRowKind.Status).Select(r => r.Text));
    }

    [Fact]
    public void Comandos_ganham_o_glifo_do_comando_e_levam_comando_e_argumento()
    {
        var rows = MenuPresenter.From(StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            Browsers = [new BrowserStatus("chrome", "Google Chrome", Unthrottled: false)],
            WaitingAudio = [new WaitingAudio("dev-1", "Fone")],
        }, Config.Default));

        MenuRow Cmd(MenuCommand c) => Assert.Single(rows, r => r.Command == c);
        Assert.Equal(Glyphs.Refresh, Cmd(MenuCommand.Relaunch).Glyph);
        Assert.Equal("chrome", Cmd(MenuCommand.Relaunch).Argument);
        Assert.Equal(Glyphs.Dismiss, Cmd(MenuCommand.StopWaitingAudio).Glyph);
        Assert.Equal("dev-1", Cmd(MenuCommand.StopWaitingAudio).Argument);
        Assert.Equal(Glyphs.Play, Cmd(MenuCommand.StartDefault).Glyph);
        Assert.Equal("14400", Cmd(MenuCommand.StartDefault).Argument);
        Assert.Equal(Glyphs.OpenPane, Cmd(MenuCommand.OpenPanel).Glyph);
        Assert.Equal(Glyphs.Settings, Cmd(MenuCommand.Settings).Glyph);
        Assert.Equal(Glyphs.Power, Cmd(MenuCommand.Quit).Glyph);
        Assert.All(rows.Where(r => r.Command != MenuCommand.None), r => Assert.Equal(MenuRowKind.Command, r.Kind));
    }

    [Fact]
    public void So_sair_e_destrutivo()
    {
        var rows = MenuPresenter.From(StatusMenuBuilder.Build(new StatusSnapshot { Now = Now }, Config.Default));

        var destructive = Assert.Single(rows, r => r.Destructive);
        Assert.Equal(MenuCommand.Quit, destructive.Command);
        Assert.Equal("Sair", destructive.Text);
    }

    [Fact]
    public void Avisos_viram_notice_com_glifo_de_aviso_sem_o_simbolo_no_texto()
    {
        var rows = MenuPresenter.From(StatusMenuBuilder.Build(new StatusSnapshot
        {
            Now = Now,
            Warnings = ["Backstop não armado"],
            LidSimulation = true,
        }, Config.Default));

        var notices = rows.Where(r => r.Kind == MenuRowKind.Notice).ToList();
        Assert.Equal(["Build com simulação de tampa (arquivo simulate-lid)", "Backstop não armado"], notices.Select(n => n.Text));
        Assert.All(notices, n => Assert.Equal(Glyphs.Warning, n.Glyph));
        Assert.All(notices, n => Assert.Equal(MenuCommand.None, n.Command));
    }

    [Fact]
    public void Separadores_ficam_na_mesma_posicao()
    {
        var lines = StatusMenuBuilder.Build(new StatusSnapshot { Now = Now, Warnings = ["x"] }, Config.Default);
        var rows = MenuPresenter.From(lines);

        Assert.Equal(lines.Count, rows.Count);
        var lineSeparators = lines.Select((l, i) => (l, i)).Where(p => p.l.Kind == MenuLineKind.Separator).Select(p => p.i).ToList();
        var rowSeparators = rows.Select((r, i) => (r, i)).Where(p => p.r.Kind == MenuRowKind.Separator).Select(p => p.i).ToList();
        Assert.Equal(2, lineSeparators.Count);
        Assert.Equal(lineSeparators, rowSeparators);
        Assert.All(rows.Where(r => r.Kind == MenuRowKind.Separator), r => { Assert.Null(r.Glyph); Assert.Equal("", r.Text); });
    }

    [Fact]
    public void Lista_feita_a_mao_mapeia_cada_tipo()
    {
        var rows = MenuPresenter.From(
        [
            new MenuLine(MenuLineKind.Info, "a"),
            new MenuLine(MenuLineKind.Info, "b"),
            new MenuLine(MenuLineKind.Warning, "⚠ c"),
            new MenuLine(MenuLineKind.Warning, "d"),
            MenuLine.Separator,
            new MenuLine(MenuLineKind.Action, "e"),
        ]);

        Assert.Equal(
            [MenuRowKind.Header, MenuRowKind.Status, MenuRowKind.Notice, MenuRowKind.Notice, MenuRowKind.Separator, MenuRowKind.Command],
            rows.Select(r => r.Kind));
        Assert.Equal(["a", "b", "c", "d", "", "e"], rows.Select(r => r.Text));
        Assert.Null(rows[5].Glyph);   // ação sem comando: sem glifo
        Assert.False(rows[5].Destructive);
    }

    [Fact]
    public void Glifos_dos_comandos_sao_os_do_desenho()
    {
        Assert.Equal(Glyphs.OpenPane, MenuPresenter.GlyphFor(MenuCommand.OpenPanel));
        Assert.Equal(Glyphs.Play, MenuPresenter.GlyphFor(MenuCommand.StartDefault));
        Assert.Equal(Glyphs.Settings, MenuPresenter.GlyphFor(MenuCommand.Settings));
        Assert.Equal(Glyphs.Power, MenuPresenter.GlyphFor(MenuCommand.Quit));
        Assert.Equal(Glyphs.Refresh, MenuPresenter.GlyphFor(MenuCommand.Relaunch));
        Assert.Equal(Glyphs.Dismiss, MenuPresenter.GlyphFor(MenuCommand.StopWaitingAudio));
        Assert.Null(MenuPresenter.GlyphFor(MenuCommand.None));
    }
}
