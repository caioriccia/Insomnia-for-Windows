using Insomnia.App.Ui.Controls;

namespace Insomnia.App.Tests.Ui.Visual;

// Os estilos do §5.6 carregam do assembly e têm as chaves e os tipos congelados. Tudo roda
// no thread STA do App de teste (AppWpfHost), sem mostrar janela (invariante 3 do pacote):
// o WPF só exige STA para criar os objetos, não para exibi-los.
[Collection(Insomnia.App.Tests.Ui.Fumaca.WpfCollection.Nome)]
public class ControlsDictionaryTests
{
    private static readonly Uri ControlsUri = new("/Insomnia;component/Ui/Controls/Controls.xaml", UriKind.Relative);

    public static TheoryData<string, Type> ChavesCongeladas => new()
    {
        { "Insomnia.ToggleSwitch", typeof(System.Windows.Controls.CheckBox) },
        { "Insomnia.Chip", typeof(System.Windows.Controls.Button) },
        { "Insomnia.Chip.Selected", typeof(System.Windows.Controls.Button) },
        { "Insomnia.IconButton", typeof(System.Windows.Controls.Button) },
        { "Insomnia.InfoBar", typeof(System.Windows.Controls.ContentControl) },
        { "Insomnia.NavItem", typeof(System.Windows.Controls.ListBoxItem) },
        { "Insomnia.SettingCard", typeof(System.Windows.Controls.ContentControl) },
        { "Insomnia.DragHandle", typeof(System.Windows.Controls.Border) },
        { "Insomnia.TextField", typeof(System.Windows.Controls.TextBox) },
    };

    [Theory]
    [MemberData(nameof(ChavesCongeladas))]
    public void Controls_xaml_expoe_o_estilo_congelado_com_o_tipo_certo(string key, Type target)
    {
        Sta.Run(() =>
        {
            var dictionary = LoadControls();
            var style = Assert.IsType<System.Windows.Style>(dictionary[key]);
            Assert.Equal(target, style.TargetType);
        });
    }

    [Fact]
    public void Chip_selecionado_herda_do_chip()
    {
        Sta.Run(() =>
        {
            var dictionary = LoadControls();
            var selected = (System.Windows.Style)dictionary["Insomnia.Chip.Selected"];
            Assert.Same(dictionary["Insomnia.Chip"], selected.BasedOn);
        });
    }

    [Fact]
    public void Nenhum_estilo_pinta_cor_fixa_so_tokens_por_DynamicResource()
    {
        Sta.Run(() =>
        {
            var dictionary = LoadControls();
            var fixas = new List<string>();
            foreach (var key in dictionary.Keys.OfType<string>())
            {
                if (dictionary[key] is not System.Windows.Style style) continue;
                foreach (var setter in AllSetters(style))
                {
                    // DynamicResource fica guardado como DynamicResourceExtension; pincel de verdade
                    // só é aceito se for transparente (fundo que recebe clique sem pintar nada).
                    if (setter.Value is System.Windows.Media.SolidColorBrush brush && brush.Color.A != 0)
                        fixas.Add($"{key}: {setter.TargetName}.{setter.Property.Name} = {brush.Color}");
                    if (setter.Value is System.Windows.Media.Brush and not System.Windows.Media.SolidColorBrush)
                        fixas.Add($"{key}: {setter.TargetName}.{setter.Property.Name} = {setter.Value}");
                }
            }
            Assert.Empty(fixas);
        });
    }

    // Mesmo formato do NoticeKind de Ui/Model, sem depender dele (Controls não conhece o modelo).
    private enum AvisoDeTeste { Caution, Critical }

    public static TheoryData<object?, bool> TagsDoInfoBar => new()
    {
        { AvisoDeTeste.Critical, true },
        { "Critical", true },
        { AvisoDeTeste.Caution, false },
        { "Caution", false },
        { null, false },
    };

    [Theory]
    [MemberData(nameof(TagsDoInfoBar))]
    public void InfoBar_troca_para_critico_pelo_nome_do_Tag_enum_ou_string(object? tag, bool critico)
    {
        // Os tokens de aviso entram como pincéis próprios no recurso do controle (no app vêm do
        // Tokens.xaml); o que se prova é que o fundo do Root e a cor do glifo trocam de token.
        Sta.Run(() =>
        {
            var cautionBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Khaki);
            var criticalBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.MistyRose);
            var critical = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Firebrick);
            var infoBar = new System.Windows.Controls.ContentControl
            {
                Style = (System.Windows.Style)LoadControls()["Insomnia.InfoBar"],
                Tag = tag,
                Content = "aviso",
            };
            infoBar.Resources["Insomnia.Caution.Bg"] = cautionBg;
            infoBar.Resources["Insomnia.Critical.Bg"] = criticalBg;
            infoBar.Resources["Insomnia.Critical"] = critical;
            Assert.True(infoBar.ApplyTemplate());
            var root = (System.Windows.Controls.Border)infoBar.Template.FindName("Root", infoBar);
            var glyph = (System.Windows.Controls.TextBlock)infoBar.Template.FindName("Glyph", infoBar);

            Assert.Same(critico ? criticalBg : cautionBg, root.Background);
            Assert.Equal(critico, ReferenceEquals(critical, glyph.Foreground));
        });
    }

    [Fact]
    public void GalleryWindow_e_publica_e_monta_sem_ser_mostrada()
    {
        Assert.True(typeof(GalleryWindow).IsPublic);
        Assert.Equal("Insomnia.App.Ui.Controls", typeof(GalleryWindow).Namespace);

        Sta.Run(() =>
        {
            var window = new GalleryWindow();
            try
            {
                Assert.NotNull(window.Content);
                Assert.False(window.IsVisible);
                // Os estilos de Controls.xaml e os tokens chegam pela própria janela.
                Assert.IsType<System.Windows.Style>(window.FindResource("Insomnia.Chip"));
                Assert.NotNull(window.FindResource("Insomnia.Text.Primary"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static System.Windows.ResourceDictionary LoadControls() =>
        (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(ControlsUri);

    private static IEnumerable<System.Windows.Setter> AllSetters(System.Windows.Style style)
    {
        foreach (var setter in style.Setters.OfType<System.Windows.Setter>())
        {
            yield return setter;
            if (setter.Value is System.Windows.Controls.ControlTemplate template)
                foreach (var nested in TriggerSetters(template.Triggers)) yield return nested;
        }
        foreach (var nested in TriggerSetters(style.Triggers)) yield return nested;
    }

    private static IEnumerable<System.Windows.Setter> TriggerSetters(System.Windows.TriggerCollection triggers)
    {
        foreach (var trigger in triggers)
        {
            var setters = trigger switch
            {
                System.Windows.Trigger t => t.Setters,
                System.Windows.MultiTrigger m => m.Setters,
                System.Windows.DataTrigger d => d.Setters,
                System.Windows.MultiDataTrigger md => md.Setters,
                _ => null,
            };
            if (setters is null) continue;
            foreach (var setter in setters.OfType<System.Windows.Setter>()) yield return setter;
        }
    }
}

// Roda a ação no thread STA do App de teste (um só por processo: ver AppWpfHost) e devolve a
// exceção (com o stack original) ao xUnit.
internal static class Sta
{
    public static void Run(Action action) => Insomnia.App.Tests.Ui.Fumaca.AppWpfHost.Run(_ => action());
}
