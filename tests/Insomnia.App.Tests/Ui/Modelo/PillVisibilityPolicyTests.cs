using Insomnia.App.Ui.Model;

namespace Insomnia.App.Tests.Ui.Modelo;

public class PillVisibilityPolicyTests
{
    // Tabela-verdade inteira: só aparece com sessão, painel fechado e primeiro plano normal.
    [Theory]
    [InlineData(true, false, ForegroundKind.Normal, true)]
    [InlineData(true, false, ForegroundKind.Fullscreen, false)]
    [InlineData(true, false, ForegroundKind.Presentation, false)]
    [InlineData(true, true, ForegroundKind.Normal, false)]
    [InlineData(true, true, ForegroundKind.Fullscreen, false)]
    [InlineData(true, true, ForegroundKind.Presentation, false)]
    [InlineData(false, false, ForegroundKind.Normal, false)]
    [InlineData(false, false, ForegroundKind.Fullscreen, false)]
    [InlineData(false, false, ForegroundKind.Presentation, false)]
    [InlineData(false, true, ForegroundKind.Normal, false)]
    [InlineData(false, true, ForegroundKind.Fullscreen, false)]
    [InlineData(false, true, ForegroundKind.Presentation, false)]
    public void Tabela_verdade(bool hasSession, bool panelOpen, ForegroundKind fg, bool expected) =>
        Assert.Equal(expected, PillVisibilityPolicy.ShouldShow(hasSession, panelOpen, fg));
}
