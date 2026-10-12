using System.Reflection;
using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Tests.Ui.Alicerce;

/// Glifos do §3.1: um caractere cada, na área de uso privado da Segoe Fluent Icons.
public class GlyphsTests
{
    private static IReadOnlyList<(string Name, string Value)> All() =>
        [.. typeof(Glyphs).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))];

    [Fact]
    public void Todos_tem_um_caractere_na_faixa_E700_F8FF()
    {
        var all = All();
        Assert.NotEmpty(all);
        foreach (var (name, value) in all)
        {
            Assert.True(value.Length == 1, $"{name} tem {value.Length} caracteres");
            Assert.InRange(value[0], '', '');
        }
    }

    [Fact]
    public void Tem_os_glifos_do_desenho()
    {
        var names = All().Select(g => g.Name).ToHashSet();
        foreach (var expected in new[]
        {
            "Eye", "EyeClosed", "Pause", "Warning", "Clock", "Add", "Settings", "Laptop", "Battery", "BatteryCharging",
            "Wifi", "WifiError", "Snowflake", "Mute", "Display", "Docker", "ChevronUp", "Power", "OpenPane", "Play",
            "Refresh", "Dismiss", "Globe", "Robot",
        })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void Codigos_do_desenho()
    {
        Assert.Equal("", Glyphs.Eye);
        Assert.Equal("", Glyphs.EyeClosed);
        Assert.Equal("", Glyphs.Warning);
        Assert.Equal("", Glyphs.Power);
    }

    [Fact]
    public void Nao_ha_dois_nomes_para_o_mesmo_glifo()
    {
        var all = All();
        Assert.Equal(all.Count, all.Select(g => g.Value).Distinct().Count());
    }
}
