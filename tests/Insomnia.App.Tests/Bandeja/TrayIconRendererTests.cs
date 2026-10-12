using System.Drawing;
using Insomnia.App.Tray;

namespace Insomnia.App.Tests.Bandeja;

public class TrayIconRendererTests
{
    private static readonly TrayIconState[] Todos =
        [TrayIconState.Idle, TrayIconState.Running, TrayIconState.Paused, TrayIconState.Warning];

    public static TheoryData<TrayIconState, int, bool> Estados()
    {
        var data = new TheoryData<TrayIconState, int, bool>();
        foreach (var state in Todos)
            foreach (var size in new[] { 16, 20, 24, 32 })
                foreach (var light in new[] { false, true })
                    data.Add(state, size, light);
        return data;
    }

    [Theory]
    [MemberData(nameof(Estados))]
    public void Render_gera_bitmap_do_tamanho_pedido_com_algo_desenhado(TrayIconState state, int size, bool light)
    {
        using var bitmap = TrayIconRenderer.Render(state, size, light);

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        Assert.True(HasOpaquePixel(bitmap));
        Assert.True(HasTransparentPixel(bitmap), "glifo monocromático: o fundo do ícone é transparente");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Estados_diferentes_desenham_diferente(int size)
    {
        foreach (var light in new[] { false, true })
        {
            var desenhos = Todos.Select(s =>
            {
                using var b = TrayIconRenderer.Render(s, size, light);
                return string.Join(',', Pixels(b));
            }).ToList();

            Assert.Equal(Todos.Length, desenhos.Distinct().Count());
        }
    }

    [Fact]
    public void Claro_e_escuro_invertem_a_cor_dominante()
    {
        foreach (var state in Todos)
        {
            using var escura = TrayIconRenderer.Render(state, 32, lightTaskbar: false);
            using var clara = TrayIconRenderer.Render(state, 32, lightTaskbar: true);

            Assert.True(DominantLuminance(escura) > 0.8, $"{state}: barra escura pede glifo claro");
            Assert.True(DominantLuminance(clara) < 0.2, $"{state}: barra clara pede glifo escuro");
        }
    }

    [Fact]
    public void Aviso_tem_ponto_colorido_e_os_outros_estados_sao_monocromaticos()
    {
        foreach (var light in new[] { false, true })
        {
            foreach (var state in Todos)
            {
                using var b = TrayIconRenderer.Render(state, 32, light);
                Assert.Equal(state == TrayIconState.Warning, HasColoredPixel(b));
            }
        }
    }

    [Fact]
    public void Tamanho_fora_da_faixa_e_limitado()
    {
        using var pequeno = TrayIconRenderer.Render(TrayIconState.Running, 4, false);
        using var grande = TrayIconRenderer.Render(TrayIconState.Running, 1024, false);

        Assert.Equal(16, pequeno.Width);
        Assert.Equal(256, grande.Width);
    }

    [Fact]
    public void Update_reaproveita_o_mesmo_icone_para_o_mesmo_estado_e_troca_quando_muda()
    {
        using var renderer = new TrayIconRenderer();

        var first = renderer.Update(TrayIconState.Running, 16, false);
        var same = renderer.Update(TrayIconState.Running, 16, false);
        var otherTheme = renderer.Update(TrayIconState.Running, 16, true);
        var otherState = renderer.Update(TrayIconState.Paused, 16, true);

        Assert.Same(first, same);
        Assert.NotSame(first, otherTheme);
        Assert.NotSame(otherTheme, otherState);
        Assert.Same(otherState, renderer.Current);
        Assert.Equal(16, otherState.Width);
    }

    [Fact]
    public void Dispose_destroi_o_ultimo_icone_sem_lancar()
    {
        var renderer = new TrayIconRenderer();
        renderer.Update(TrayIconState.Idle, 16, false);
        renderer.Update(TrayIconState.Warning, 16, false);

        renderer.Dispose();
        renderer.Dispose();

        Assert.Null(renderer.Current);
    }

    private static bool HasOpaquePixel(Bitmap b) => All(b).Any(c => c.A > 0);

    private static bool HasTransparentPixel(Bitmap b) => All(b).Any(c => c.A == 0);

    // Algum pixel visível fora da escala de cinza (o ponto de Caution).
    private static bool HasColoredPixel(Bitmap b) =>
        All(b).Any(c => c.A > 128 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 60);

    // Luminância média dos pixels, ponderada pelo alfa (0 = preto, 1 = branco).
    private static double DominantLuminance(Bitmap b)
    {
        double sum = 0, weight = 0;
        foreach (var c in All(b))
        {
            if (c.A == 0) continue;
            sum += c.A * (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            weight += c.A;
        }
        return weight == 0 ? 0.5 : sum / weight;
    }

    private static IEnumerable<Color> All(Bitmap b)
    {
        for (var x = 0; x < b.Width; x++)
            for (var y = 0; y < b.Height; y++)
                yield return b.GetPixel(x, y);
    }

    private static List<int> Pixels(Bitmap b) => All(b).Select(c => c.ToArgb()).ToList();
}
