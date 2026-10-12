using System.Windows;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using System.Windows.Controls;
using Insomnia.App.Tray;
using Insomnia.App.Ui;
using Insomnia.App.Ui.Controls;
using Insomnia.App.Ui.Menu;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Settings;
using Insomnia.App.Ui.Shell;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.App.Tests.Ui.Fumaca;

/// Teste de fumaça da UI REAL: com o App de verdade no ar (mesmos dicionários do App.xaml),
/// instancia cada janela SEM mostrá-la e força Measure/Arrange para que Styles, ControlTemplates
/// e DynamicResource sejam avaliados. Foi o que faltou quando o app morreu na abertura com
/// "'System.Windows.ResourceReferenceExpression' não é um valor válido para a propriedade 'Color'":
/// os testes antigos carregavam os dicionários sem o tema Fluent e sem uma Application.
[Collection(WpfCollection.Nome)]
public class JanelasFumacaTests
{
    private static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(new Point(0, 0), element.DesiredSize));
        element.UpdateLayout();
        ForceTemplates(element);
        element.Measure(size);
        element.UpdateLayout();
    }

    /// Aplica o template de todo Control da árvore lógica, para os Setters/Triggers dos estilos
    /// saírem do papel mesmo nos que só aparecem depois do primeiro layout.
    private static void ForceTemplates(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ForceTemplates(child);
        if (root is FrameworkElement fe) fe.ApplyTemplate();
    }

    private static PanelState Painel(PanelMode mode, PillTone tone) => new(
        mode, tone, mode == PanelMode.Session ? "3:42:15" : "Manter acordado",
        mode == PanelMode.Session ? "termina às 22:00" : "",
        [new PanelChip("30 min", TimeSpan.FromMinutes(30), false), new PanelChip("1 h", TimeSpan.FromHours(1), true)],
        [new StatusRow("\uE702", "Rede", "Wi-Fi conectado")],
        mode == PanelMode.Session ? new LidBanner("Tampa fechada", "A sessão continua") : null,
        [new PanelNotice(NoticeKind.Caution, "Aviso de recuperação", "Desfazer", MenuCommand.None),
         new PanelNotice(NoticeKind.Critical, "Erro de comando")],
        mode == PanelMode.Session);

    [Fact]
    public void App_real_sobe_com_os_dicionarios_e_resolve_todos_os_tokens()
    {
        AppWpfHost.Run(app =>
        {
            foreach (var key in new[] { "Insomnia.Text.Primary", "Insomnia.Accent", "Insomnia.OnAccent", "Insomnia.Surface.Solid",
                         "Insomnia.Card", "Insomnia.Control.Fill", "Insomnia.Critical.Bg", "Insomnia.Success" })
                Assert.IsAssignableFrom<System.Windows.Media.Brush>(app.FindResource(key));
        });
    }

    [Fact]
    public void O_acento_do_App_aponta_para_os_pinceis_Fluent_do_tema()
    {
        AppWpfHost.Run(app =>
        {
            foreach (var (token, fluent) in global::Insomnia.App.App.AccentAliases)
                Assert.Same(app.FindResource(fluent), app.FindResource(token));
        });
    }

    [Fact]
    public void Tokens_xaml_sozinho_avalia_todos_os_pinceis_inclusive_a_reserva_do_acento()
    {
        // O App troca os tokens de acento pelo pincel Fluent logo ao subir, o que esconde uma reserva
        // quebrada (foi assim que o app deixou de abrir). Aqui o dicionario e carregado a parte,
        // sem a troca, e cada pincel e lido de verdade.
        AppWpfHost.Run(_ =>
        {
            var tokens = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Insomnia;component/Ui/Theme/Tokens.xaml", UriKind.Relative));
            var scope = new FrameworkElement();
            scope.Resources.MergedDictionaries.Add(tokens);
            var pinceis = tokens.Keys.OfType<string>().Where(k => !k.Contains("Radius") && !k.Contains(".Font.")).ToList();
            Assert.NotEmpty(pinceis);
            foreach (var key in pinceis)
            {
                var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(scope.FindResource(key));
                Assert.True(brush.Color.A > 0, $"{key} sem cor");
            }
        });
    }

    [Fact]
    public void PillWindow_monta_e_mede()
    {
        AppWpfHost.Run(_ =>
        {
            var pill = new PillWindow();
            Layout(pill, new Size(200, 64));
            pill.Render(new PillState(true, "\uE708", "0:42", PillTone.Normal, PillTone.Normal, "dica"));
            pill.Render(new PillState(true, "\uE708", "0:42", PillTone.Critical, PillTone.Warning, "dica"));
            Layout(pill, new Size(200, 64));
            Assert.False(pill.IsVisible);
        });
    }

    [Theory]
    [InlineData(PanelMode.Start, PillTone.Normal)]
    [InlineData(PanelMode.Session, PillTone.Normal)]
    [InlineData(PanelMode.Session, PillTone.EndingSoon)]
    [InlineData(PanelMode.Session, PillTone.Critical)]
    public void PanelWindow_monta_e_mede_nos_dois_modos(PanelMode mode, PillTone tone)
    {
        AppWpfHost.Run(_ =>
        {
            var panel = new PanelWindow();
            panel.Render(Painel(mode, tone));
            panel.ShowError("falhou");
            Layout(panel, new Size(360, 700));
            Assert.Equal(mode, panel.Mode);
            Assert.False(panel.IsVisible);
        });
    }

    [Fact]
    public void MenuFlyoutWindow_monta_as_linhas_e_mede()
    {
        AppWpfHost.Run(_ =>
        {
            var menu = new MenuFlyoutWindow();
            menu.SetRows(
            [
                new MenuRow(MenuRowKind.Header, null, "Insomnia"),
                new MenuRow(MenuRowKind.Status, null, "Sem sessão"),
                new MenuRow(MenuRowKind.Notice, null, "Aviso"),
                new MenuRow(MenuRowKind.Separator, null, ""),
                new MenuRow(MenuRowKind.Command, "\uE713", "Configurações", MenuCommand.None),
                new MenuRow(MenuRowKind.Command, "\uE711", "Sair", MenuCommand.None, null, Destructive: true),
            ]);
            Layout(menu, new Size(288, 600));
            Assert.False(menu.IsVisible);
        });
    }

    [Fact]
    public void SettingsWindow_monta_cada_pagina_e_mede()
    {
        AppWpfHost.Run(_ =>
        {
            var model = new SettingsModel(new FakeConfigSource(), new FakeAutoStart(), new FakeWifi(), new FakeProcessWorld(),
                @"C:\fumaca\config.json", @"C:\fumaca\Insomnia.exe", false, new FakeLog(), action => action());
            var window = new SettingsWindow(model);
            try
            {
                foreach (var page in Enum.GetValues<SettingsPage>())
                {
                    model.Current = page;
                    Layout(window, new Size(900, 640));
                }
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.CloseForGood();
                model.Dispose();
            }
        });
    }

    [Fact]
    public void GalleryWindow_monta_e_mede_com_o_App_no_ar()
    {
        AppWpfHost.Run(_ =>
        {
            var gallery = new GalleryWindow();
            try
            {
                Layout(gallery, new Size(900, 700));
                Assert.False(gallery.IsVisible);
            }
            finally
            {
                gallery.Close();
            }
        });
    }
}
