using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Tests.Ui.Alicerce;

/// Durações e curvas do §3.10; animações desligadas no Windows zeram tudo.
public class MotionTests
{
    [Fact]
    public void Scaled_com_animacoes_devolve_a_duracao()
    {
        Assert.Equal(Motion.Normal, Motion.Scaled(Motion.Normal, animationsEnabled: true));
        Assert.Equal(Motion.Hold, Motion.Scaled(Motion.Hold, animationsEnabled: true));
    }

    [Fact]
    public void Scaled_sem_animacoes_zera()
    {
        foreach (var d in new[] { Motion.Fast, Motion.Short, Motion.Normal, Motion.Hold })
            Assert.Equal(TimeSpan.Zero, Motion.Scaled(d, animationsEnabled: false));
    }

    [Fact]
    public void Duracoes_do_desenho()
    {
        Assert.Equal(83, Motion.Fast.TotalMilliseconds);
        Assert.Equal(167, Motion.Short.TotalMilliseconds);
        Assert.Equal(333, Motion.Normal.TotalMilliseconds);
        Assert.Equal(600, Motion.Hold.TotalMilliseconds);
    }

    [Fact]
    public void Curvas_Fluent()
    {
        Assert.Equal((0.1, 0.9, 0.2, 1.0), Motion.Decelerate);
        Assert.Equal((0.7, 0.0, 1.0, 0.5), Motion.Accelerate);
        Assert.Equal((0.8, 0.0, 0.2, 1.0), Motion.Standard);
    }
}
