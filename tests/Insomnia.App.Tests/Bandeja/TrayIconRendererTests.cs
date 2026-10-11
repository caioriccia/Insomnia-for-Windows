using System.Drawing;
using Insomnia.App.Tray;

namespace Insomnia.App.Tests.Bandeja;

public class TrayIconRendererTests
{
    public static TheoryData<string?, bool, int> Estados => new()
    {
        { null, false, 16 },
        { "4h", false, 16 },
        { "59m", false, 16 },
        { "<1m", false, 20 },
        { "30d", true, 24 },
        { "12h", false, 32 },
    };

    [Theory]
    [MemberData(nameof(Estados))]
    public void Render_gera_bitmap_do_tamanho_pedido_com_algo_desenhado(string? text, bool paused, int size)
    {
        using var bitmap = TrayIconRenderer.Render(text, paused, size);

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        Assert.True(HasOpaquePixel(bitmap));
    }

    [Fact]
    public void Ocioso_e_rodando_desenham_diferente()
    {
        using var idle = TrayIconRenderer.Render(null, false, 16);
        using var running = TrayIconRenderer.Render("4h", false, 16);

        Assert.NotEqual(Pixels(idle), Pixels(running));
    }

    [Fact]
    public void Update_reaproveita_o_mesmo_icone_para_o_mesmo_estado_e_troca_quando_muda()
    {
        using var renderer = new TrayIconRenderer();

        var first = renderer.Update("4h", false, 16);
        var same = renderer.Update("4h", false, 16);
        var next = renderer.Update("3h", false, 16);

        Assert.Same(first, same);
        Assert.NotSame(first, next);
        Assert.Same(next, renderer.Current);
        Assert.Equal(16, next.Width);
    }

    [Fact]
    public void Dispose_destroi_o_ultimo_icone_sem_lancar()
    {
        var renderer = new TrayIconRenderer();
        renderer.Update(null, false, 16);
        renderer.Update("59m", false, 16);

        renderer.Dispose();
        renderer.Dispose();

        Assert.Null(renderer.Current);
    }

    private static bool HasOpaquePixel(Bitmap b)
    {
        for (var x = 0; x < b.Width; x++)
            for (var y = 0; y < b.Height; y++)
                if (b.GetPixel(x, y).A > 0) return true;
        return false;
    }

    private static List<int> Pixels(Bitmap b)
    {
        var list = new List<int>();
        for (var x = 0; x < b.Width; x++)
            for (var y = 0; y < b.Height; y++)
                list.Add(b.GetPixel(x, y).ToArgb());
        return list;
    }
}
