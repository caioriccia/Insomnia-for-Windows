// CONTRATO CONGELADO (desenho-ui §3.4, §3.6, §4 e §5.5): onde pôr as janelas flutuantes.
// Puro e todo em pixels FÍSICOS: bounds/workArea vêm de System.Windows.Forms.Screen, e o
// tamanho chega já multiplicado pela escala do monitor-alvo (Dwm.ScaleForPoint). Quem chama
// entrega o resultado a FlyoutWindow.MoveTo, que faz a conversão para a janela.
namespace Insomnia.App.Ui.Native;

/// Retângulo em pixels físicos (X, Y = canto superior esquerdo).
public readonly record struct PxRect(double X, double Y, double W, double H);

public static class UiPlacement
{
    /// Centrado em bounds; topo = max(bounds.Y, workArea.Y) + topMargin. Tudo em pixels físicos.
    /// Barra de tarefas no topo empurra o workArea para baixo, e a pílula desce junto; com a
    /// barra embaixo (ou dos lados) o topo é o do monitor.
    public static PxRect TopCenter(PxRect bounds, PxRect workArea, double w, double h, double topMargin)
    {
        var x = bounds.X + (bounds.W - w) / 2;
        var y = Math.Max(bounds.Y, workArea.Y) + topMargin;
        return new PxRect(x, y, w, h);
    }

    /// Menu da bandeja: canto inferior-direito no cursor, preso ao workArea (acima da barra).
    /// gap = folga entre o cursor e a borda de baixo do menu. Se não couber de um lado, encosta
    /// na borda do workArea; maior que o workArea, prende pelo canto superior esquerdo.
    public static PxRect NearCursor(PxRect workArea, double w, double h, double cursorX, double cursorY, double gap)
    {
        var x = Clamp(cursorX - w, workArea.X, workArea.X + workArea.W - w);
        var y = Clamp(cursorY - gap - h, workArea.Y, workArea.Y + workArea.H - h);
        return new PxRect(x, y, w, h);
    }

    /// Como Math.Clamp, mas sem lançar quando max < min (janela maior que a área): vence min.
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
}
