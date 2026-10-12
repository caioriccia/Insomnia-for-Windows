using Insomnia.App.Ui.Native;

namespace Insomnia.App.Tests.Ui.Alicerce;

/// Posição em pixels físicos (desenho-ui §3.4, §3.6 e §4).
public class UiPlacementTests
{
    // Monitor 1920×1080 com a barra de tarefas (48 px) embaixo.
    private static readonly PxRect Bounds = new(0, 0, 1920, 1080);
    private static readonly PxRect WorkBottomBar = new(0, 0, 1920, 1032);

    [Fact]
    public void TopCenter_centra_no_monitor()
    {
        var r = UiPlacement.TopCenter(Bounds, WorkBottomBar, 200, 48, 12);
        Assert.Equal(860, r.X);
        Assert.Equal(200, r.W);
        Assert.Equal(48, r.H);
    }

    [Fact]
    public void TopCenter_com_barra_embaixo_usa_o_topo_do_monitor()
    {
        var r = UiPlacement.TopCenter(Bounds, WorkBottomBar, 200, 48, 12);
        Assert.Equal(12, r.Y);
    }

    [Fact]
    public void TopCenter_com_barra_no_topo_desce_para_o_work_area()
    {
        var workTopBar = new PxRect(0, 48, 1920, 1032);
        var r = UiPlacement.TopCenter(Bounds, workTopBar, 200, 48, 12);
        Assert.Equal(60, r.Y);
        Assert.Equal(860, r.X);
    }

    [Fact]
    public void TopCenter_centra_em_bounds_mesmo_com_barra_lateral()
    {
        var workLeftBar = new PxRect(64, 0, 1856, 1080);
        var r = UiPlacement.TopCenter(Bounds, workLeftBar, 200, 48, 12);
        Assert.Equal(860, r.X);
        Assert.Equal(12, r.Y);
    }

    [Fact]
    public void TopCenter_em_monitor_secundario_com_coordenadas_negativas()
    {
        var bounds = new PxRect(-2560, -200, 2560, 1440);
        var r = UiPlacement.TopCenter(bounds, bounds, 300, 48, 12);
        Assert.Equal(-1430, r.X);
        Assert.Equal(-188, r.Y);
    }

    [Fact]
    public void NearCursor_poe_o_canto_inferior_direito_no_cursor()
    {
        var r = UiPlacement.NearCursor(WorkBottomBar, 288, 300, 1500, 1060, 0);
        // Cursor na barra (abaixo do work area): o menu encosta no fundo do work area.
        Assert.Equal(new PxRect(1212, 732, 288, 300), r);

        var livre = UiPlacement.NearCursor(WorkBottomBar, 288, 300, 1500, 900, 8);
        Assert.Equal(new PxRect(1212, 592, 288, 300), livre);
    }

    [Fact]
    public void NearCursor_prende_na_esquerda()
    {
        var r = UiPlacement.NearCursor(WorkBottomBar, 288, 300, 100, 900, 0);
        Assert.Equal(0, r.X);
    }

    [Fact]
    public void NearCursor_prende_na_direita()
    {
        // Barra à direita: o work area termina antes do cursor.
        var work = new PxRect(0, 0, 1856, 1080);
        var r = UiPlacement.NearCursor(work, 288, 300, 1900, 900, 0);
        Assert.Equal(1856 - 288, r.X);
    }

    [Fact]
    public void NearCursor_prende_no_topo()
    {
        // Barra no topo: o cursor está acima do work area, o menu desce para dentro dele.
        var work = new PxRect(0, 48, 1920, 1032);
        var r = UiPlacement.NearCursor(work, 288, 300, 1500, 20, 8);
        Assert.Equal(48, r.Y);
    }

    [Fact]
    public void NearCursor_prende_no_fundo()
    {
        var r = UiPlacement.NearCursor(WorkBottomBar, 288, 300, 1500, 1079, 0);
        Assert.Equal(1032 - 300, r.Y);
    }

    [Fact]
    public void NearCursor_maior_que_o_work_area_prende_no_canto_superior_esquerdo()
    {
        var work = new PxRect(100, 50, 200, 100);
        var r = UiPlacement.NearCursor(work, 288, 300, 250, 120, 0);
        Assert.Equal(100, r.X);
        Assert.Equal(50, r.Y);
    }

    [Fact]
    public void NearCursor_fica_sempre_dentro_do_work_area()
    {
        var work = new PxRect(-1920, 0, 1920, 1040);
        foreach (var (cx, cy) in new[] { (-1920.0, 0.0), (-1.0, 1079.0), (-960.0, 520.0), (-5000.0, -5000.0), (5000.0, 5000.0) })
        {
            var r = UiPlacement.NearCursor(work, 288, 300, cx, cy, 8);
            Assert.InRange(r.X, work.X, work.X + work.W - r.W);
            Assert.InRange(r.Y, work.Y, work.Y + work.H - r.H);
        }
    }
}
