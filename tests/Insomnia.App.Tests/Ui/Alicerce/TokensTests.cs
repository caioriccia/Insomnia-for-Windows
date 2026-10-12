using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Insomnia.App.Tests.Ui.Alicerce;

/// Chaves congeladas do Tokens.xaml (desenho-ui §5.5) e o App.xaml que o mescla. Lê o XAML
/// como XML a partir do fonte: nenhum tipo do WPF é instanciado (sem STA, sem janela).
public class TokensTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Brushes =
    [
        "Insomnia.Text.Primary", "Insomnia.Text.Secondary", "Insomnia.Text.Tertiary", "Insomnia.Text.Disabled",
        "Insomnia.Accent", "Insomnia.Accent.Text", "Insomnia.OnAccent",
        "Insomnia.Surface.Solid", "Insomnia.Surface.Layer", "Insomnia.Card", "Insomnia.Card.Stroke",
        "Insomnia.Control.Fill", "Insomnia.Control.Hover", "Insomnia.Control.Pressed", "Insomnia.Control.Stroke",
        "Insomnia.Control.StrongFill", "Insomnia.Divider", "Insomnia.Flyout.Stroke",
        "Insomnia.Caution", "Insomnia.Caution.Bg", "Insomnia.Critical", "Insomnia.Critical.Bg", "Insomnia.Success",
    ];

    private static string AppDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "src", "Insomnia.App"));

    private static Dictionary<string, XElement> Tokens() =>
        XDocument.Load(Path.Combine(AppDir(), "Ui", "Theme", "Tokens.xaml")).Root!.Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!);

    [Fact]
    public void Todos_os_pinceis_existem_e_seguem_o_tema_por_DynamicResource()
    {
        var tokens = Tokens();
        foreach (var key in Brushes)
        {
            Assert.True(tokens.TryGetValue(key, out var e), $"falta {key}");
            Assert.Equal("SolidColorBrush", e!.Name.LocalName);
            Assert.StartsWith("{DynamicResource ", (string?)e.Attribute("Color") ?? "");
        }
    }

    [Fact]
    public void Nenhum_hex_fixo()
    {
        var text = File.ReadAllText(Path.Combine(AppDir(), "Ui", "Theme", "Tokens.xaml"));
        Assert.DoesNotMatch(@"Color=""#", text);
    }

    [Theory]
    [InlineData("Insomnia.Radius.Control", "4")]
    [InlineData("Insomnia.Radius.Overlay", "8")]
    [InlineData("Insomnia.Radius.Pill", "16")]
    public void Raios(string key, string value)
    {
        var e = Tokens()[key];
        Assert.Equal("CornerRadius", e.Name.LocalName);
        Assert.Equal(value, e.Value.Trim());
    }

    [Theory]
    [InlineData("Insomnia.Font.Icons", "Segoe Fluent Icons, Segoe MDL2 Assets")]
    [InlineData("Insomnia.Font.Text", "Segoe UI Variable Text, Segoe UI")]
    [InlineData("Insomnia.Font.Display", "Segoe UI Variable Display, Segoe UI")]
    [InlineData("Insomnia.Font.Small", "Segoe UI Variable Small, Segoe UI")]
    public void Fontes(string key, string value)
    {
        var e = Tokens()[key];
        Assert.Equal("FontFamily", e.Name.LocalName);
        Assert.Equal(value, e.Value.Trim());
    }

    [Fact]
    public void Os_apelidos_de_acento_do_App_sao_tokens_do_dicionario()
    {
        var tokens = Tokens();
        foreach (var (token, fluent) in Insomnia.App.App.AccentAliases)
        {
            Assert.Contains(token, tokens.Keys);
            Assert.EndsWith("Brush", fluent);
        }
    }

    [Fact]
    public void App_xaml_segue_o_tema_do_sistema_e_mescla_os_tokens()
    {
        var root = XDocument.Load(Path.Combine(AppDir(), "App.xaml")).Root!;
        Assert.Equal("System", (string?)root.Attribute("ThemeMode"));
        Assert.Equal("OnExplicitShutdown", (string?)root.Attribute("ShutdownMode"));
        var sources = root.Descendants().Select(e => (string?)e.Attribute("Source")).Where(s => s is not null).ToList();
        Assert.Contains("Ui/Theme/Tokens.xaml", sources);
    }
}
