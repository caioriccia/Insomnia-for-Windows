using Insomnia.App.Ui.Model;

namespace Insomnia.App.Tests.Ui.Modelo;

public class HoldToEndMachineTests
{
    [Fact]
    public void Completa_em_600_ms_uma_vez_so()
    {
        var m = new HoldToEndMachine();
        m.Press(0, 0, 1000);
        Assert.True(m.Holding);

        Assert.False(m.Advance(1300));
        Assert.Equal(0.5, m.Progress, 6);
        Assert.False(m.Advance(1599));
        Assert.True(m.Advance(1600));
        Assert.Equal(1, m.Progress);
        Assert.False(m.Holding);

        Assert.False(m.Advance(1700));
        Assert.False(m.Advance(5000));
        m.Release();                     // soltar depois de completar não desfaz
        Assert.Equal(1, m.Progress);
    }

    [Fact]
    public void Avanco_atrasado_completa_de_uma_vez()
    {
        var m = new HoldToEndMachine();
        m.Press(0, 0, 0);

        Assert.True(m.Advance(2000));
        Assert.Equal(1, m.Progress);
    }

    [Fact]
    public void Soltar_antes_cancela()
    {
        var m = new HoldToEndMachine();
        m.Press(0, 0, 0);
        m.Advance(400);

        m.Release();

        Assert.False(m.Holding);
        Assert.Equal(0, m.Progress);
        Assert.False(m.Advance(700));
    }

    [Fact]
    public void Mover_mais_de_12_cancela()
    {
        var m = new HoldToEndMachine();
        m.Press(100, 100, 0);
        m.Advance(300);

        Assert.False(m.Move(112, 100));   // exatamente 12: ainda vale
        Assert.False(m.Move(108, 108));   // ≈ 11,3 na diagonal
        Assert.True(m.Holding);

        Assert.True(m.Move(109, 109));    // ≈ 12,7
        Assert.False(m.Holding);
        Assert.Equal(0, m.Progress);
        Assert.False(m.Advance(600));
        Assert.False(m.Move(500, 500));   // já cancelado: não cancela de novo
    }

    [Fact]
    public void Progresso_nunca_recua_enquanto_segura()
    {
        var m = new HoldToEndMachine();
        m.Press(0, 0, 0);
        var last = 0.0;
        foreach (var t in new long[] { 50, 120, 90, 300, 299, 450, 10, 599 })
        {
            Assert.False(m.Advance(t));
            Assert.True(m.Progress >= last);
            Assert.InRange(m.Progress, 0, 1);
            last = m.Progress;
        }
        Assert.Equal(599 / 600.0, m.Progress, 6);
    }

    [Fact]
    public void Nova_pressao_recomeca_do_zero()
    {
        var m = new HoldToEndMachine();
        m.Press(0, 0, 0);
        Assert.True(m.Advance(600));

        m.Press(0, 0, 10_000);
        Assert.True(m.Holding);
        Assert.Equal(0, m.Progress);
        Assert.False(m.Advance(10_300));
        Assert.True(m.Advance(10_600));
    }

    [Fact]
    public void Sem_pressao_nada_acontece()
    {
        var m = new HoldToEndMachine();

        Assert.False(m.Advance(1000));
        Assert.False(m.Move(100, 100));
        m.Release();
        Assert.Equal(0, m.Progress);
        Assert.Equal(TimeSpan.FromMilliseconds(600), HoldToEndMachine.Duration);
    }
}
