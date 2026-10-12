using Insomnia.App.Ui.Native;

namespace Insomnia.App.Tests.Ui.Alicerce;

/// As quatro regras do fallback sólido (desenho-ui §3.2): o painel nunca pode depender do material.
public class BackdropPolicyTests
{
    private const int Win11 = 22631;

    [Theory]
    [InlineData(Backdrop.Acrylic)]
    [InlineData(Backdrop.Mica)]
    public void Windows_10_sempre_solido(Backdrop wanted)
    {
        Assert.Equal(Backdrop.Solid, BackdropPolicy.Choose(wanted, 19045, transparencyEnabled: true, highContrast: false));
        Assert.Equal(Backdrop.Solid, BackdropPolicy.Choose(wanted, 21999, transparencyEnabled: true, highContrast: false));
    }

    [Fact]
    public void Primeiro_build_do_Windows_11_aceita_material()
    {
        Assert.Equal(Backdrop.Acrylic, BackdropPolicy.Choose(Backdrop.Acrylic, 22000, transparencyEnabled: true, highContrast: false));
    }

    [Theory]
    [InlineData(Backdrop.Acrylic)]
    [InlineData(Backdrop.Mica)]
    public void Transparencia_desligada_da_solido(Backdrop wanted)
    {
        Assert.Equal(Backdrop.Solid, BackdropPolicy.Choose(wanted, Win11, transparencyEnabled: false, highContrast: false));
    }

    [Theory]
    [InlineData(Backdrop.Acrylic)]
    [InlineData(Backdrop.Mica)]
    public void Alto_contraste_da_solido(Backdrop wanted)
    {
        Assert.Equal(Backdrop.Solid, BackdropPolicy.Choose(wanted, Win11, transparencyEnabled: true, highContrast: true));
    }

    [Theory]
    [InlineData(Backdrop.Acrylic)]
    [InlineData(Backdrop.Mica)]
    [InlineData(Backdrop.Solid)]
    public void Sem_impedimento_devolve_o_pedido(Backdrop wanted)
    {
        Assert.Equal(wanted, BackdropPolicy.Choose(wanted, Win11, transparencyEnabled: true, highContrast: false));
    }
}
