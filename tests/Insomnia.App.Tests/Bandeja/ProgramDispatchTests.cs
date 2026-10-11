using Insomnia.App.Commands;

namespace Insomnia.App.Tests.Bandeja;

/// Só a decisão de modo: nenhum teste chama Main (que abriria a UI, o backstop real ou o Agendador).
public class ProgramDispatchTests
{
    [Fact]
    public void Sem_argumentos_abre_a_UI()
    {
        Assert.Equal(LaunchMode.Ui, Program.ParseMode([]));
    }

    [Theory]
    [InlineData("--backstop")]
    [InlineData("--BACKSTOP")]
    [InlineData(" --backstop ")]
    public void Backstop_sozinho(string arg)
    {
        Assert.Equal(LaunchMode.Backstop, Program.ParseMode([arg]));
    }

    [Fact]
    public void Backstop_com_force()
    {
        Assert.Equal(LaunchMode.Backstop, Program.ParseMode(["--backstop", "--force"]));
        Assert.True(BackstopCommand.IsForce(["--backstop", "--force"]));
        Assert.True(BackstopCommand.IsForce(["--backstop", "--FORCE"]));
        Assert.False(BackstopCommand.IsForce(["--backstop"]));
    }

    [Fact]
    public void Backstop_com_argumento_estranho_e_invalido()
    {
        Assert.Equal(LaunchMode.Invalid, Program.ParseMode(["--backstop", "--agora"]));
    }

    [Fact]
    public void Diagnostico_repassa_o_resto()
    {
        Assert.Equal(LaunchMode.Diagnostics, Program.ParseMode(["--diagnostico"]));
        Assert.Equal(LaunchMode.Diagnostics, Program.ParseMode(["--diagnostico", "--qualquer"]));
    }

    [Fact]
    public void Remover_backstop_nao_aceita_extras()
    {
        Assert.Equal(LaunchMode.RemoveBackstop, Program.ParseMode(["--remover-backstop"]));
        Assert.Equal(LaunchMode.Invalid, Program.ParseMode(["--remover-backstop", "x"]));
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--diagnostic")]
    [InlineData("backstop")]
    [InlineData("/backstop")]
    public void Argumento_desconhecido_e_invalido(string arg)
    {
        Assert.Equal(LaunchMode.Invalid, Program.ParseMode([arg]));
    }

    [Fact]
    public void Force_so_vale_depois_de_backstop()
    {
        Assert.Equal(LaunchMode.Invalid, Program.ParseMode(["--force", "--backstop"]));
    }
}
