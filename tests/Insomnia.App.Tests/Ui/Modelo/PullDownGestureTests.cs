using Insomnia.App.Ui.Model;

namespace Insomnia.App.Tests.Ui.Modelo;

public class PullDownGestureTests
{
    [Fact]
    public void Clique_curto_sem_mover_e_clique()
    {
        var g = new PullDownGesture();

        Assert.Equal(PullOutcome.None, g.Down(10, 1000));
        Assert.Equal(PullPhase.Pressed, g.Phase);
        Assert.Equal(PullOutcome.Click, g.Up(1120));
        Assert.Equal(PullPhase.Idle, g.Phase);
    }

    [Fact]
    public void Mover_3_epx_e_soltar_ainda_e_clique()
    {
        var g = new PullDownGesture();
        g.Down(10, 0);

        Assert.Equal(PullOutcome.None, g.Move(13, 50));
        Assert.Equal(PullPhase.Pressed, g.Phase);
        Assert.Equal(0, g.Offset);
        Assert.Equal(PullOutcome.Click, g.Up(100));
    }

    [Fact]
    public void Segurar_parado_alem_do_tempo_de_clique_cancela()
    {
        var g = new PullDownGesture();
        g.Down(10, 0);

        Assert.Equal(PullOutcome.Cancel, g.Up(PullDownGesture.ClickMaxMs + 1));
    }

    [Fact]
    public void Puxar_50_epx_abre()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(5, 100);
        Assert.Equal(PullPhase.Pulling, g.Phase);
        g.Move(25, 400);
        g.Move(50, 800);

        Assert.Equal(PullDownGesture.Resist(50), g.Offset);
        Assert.Equal(PullOutcome.Open, g.Up(1500));   // devagar, mas passou do limiar
        Assert.Equal(PullPhase.Idle, g.Phase);
    }

    [Fact]
    public void Puxar_30_epx_devagar_volta()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(10, 100);
        g.Move(20, 200);
        g.Move(30, 300);

        Assert.Equal(30, g.Offset);
        Assert.Equal(PullOutcome.Cancel, g.Up(320));   // 10 epx nos últimos 100 ms = 0,1 epx/ms
    }

    [Fact]
    public void Puxar_30_epx_parado_antes_de_soltar_volta()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(30, 20);

        Assert.Equal(PullOutcome.Cancel, g.Up(400));   // nada se moveu nos últimos 100 ms
    }

    [Fact]
    public void Puxar_30_epx_em_20_ms_abre_por_velocidade()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(30, 20);

        Assert.True(g.Offset < PullDownGesture.OpenThreshold);
        Assert.Equal(PullOutcome.Open, g.Up(20));      // 1,5 epx/ms
    }

    [Fact]
    public void Arrasto_para_cima_nao_puxa()
    {
        var g = new PullDownGesture();
        g.Down(20, 0);
        g.Move(0, 50);

        Assert.Equal(PullPhase.Pressed, g.Phase);
        Assert.Equal(0, g.Offset);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(48, 48)]
    [InlineData(148, 83)]          // 48 + 100 × 0,35
    public void Resistencia(double raw, double expected) =>
        Assert.Equal(expected, PullDownGesture.Resist(raw), 6);

    [Fact]
    public void Offset_cresce_com_resistencia_alem_do_limiar()
    {
        var g = new PullDownGesture();
        g.Down(100, 0);
        g.Move(248, 300);

        Assert.Equal(83, g.Offset, 6);
    }

    [Fact]
    public void Cancelar_durante_o_arrasto()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(30, 50);
        Assert.Equal(PullPhase.Pulling, g.Phase);

        Assert.Equal(PullOutcome.Cancel, g.Cancel());
        Assert.Equal(PullPhase.Idle, g.Phase);
        Assert.Equal(PullOutcome.None, g.Cancel());
        Assert.Equal(PullOutcome.None, g.Up(100));
    }

    [Fact]
    public void Ociosa_ignora_move_e_up()
    {
        var g = new PullDownGesture();

        Assert.Equal(PullOutcome.None, g.Move(80, 10));
        Assert.Equal(PullPhase.Idle, g.Phase);
        Assert.Equal(PullOutcome.None, g.Up(20));
    }

    [Fact]
    public void Novo_down_zera_o_gesto_anterior()
    {
        var g = new PullDownGesture();
        g.Down(0, 0);
        g.Move(60, 100);
        g.Up(500);
        Assert.Equal(PullDownGesture.Resist(60), g.Offset);   // guardado para a animação

        g.Down(0, 1000);
        Assert.Equal(0, g.Offset);
        Assert.Equal(PullOutcome.Click, g.Up(1100));
    }
}
