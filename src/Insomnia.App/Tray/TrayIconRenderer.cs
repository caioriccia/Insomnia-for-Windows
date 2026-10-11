// Ícone da bandeja (desenho §9): olho fechado quando ocioso; tile escuro com o texto
// compacto (SessionMath.FormatCompact: "4h", "59m", "<1m") quando há sessão. Paleta do
// original: #303336, #17191B, #E6E3DD, #A6BBC3.
//
// HICON: Bitmap.GetHicon cria um handle que o GC não solta; Icon.FromHandle não assume a
// posse. Por isso Update guarda o handle atual e chama DestroyIcon no anterior assim que o
// novo foi entregue (o shell copia o ícone ao receber), e Dispose destrói o último.
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Insomnia.App.Tray;

public sealed class TrayIconRenderer : IDisposable
{
    public static readonly Color Tile = ColorTranslator.FromHtml("#303336");
    public static readonly Color TileRunning = ColorTranslator.FromHtml("#17191B");
    public static readonly Color Ink = ColorTranslator.FromHtml("#E6E3DD");
    public static readonly Color Accent = ColorTranslator.FromHtml("#A6BBC3");

    private Icon? _current;
    private string? _key;

    /// Último ícone entregue (null antes do primeiro Update).
    public Icon? Current => _current;

    /// Devolve o ícone para o estado; mesmo estado e tamanho ⇒ o mesmo objeto (sem novo HICON).
    /// compactText null = ocioso. paused = tampa fechada (texto na cor de destaque).
    public Icon Update(string? compactText, bool paused, int size)
    {
        var key = $"{compactText}|{paused}|{size}";
        if (_current is not null && key == _key) return _current;

        using var bitmap = Render(compactText, paused, size);
        var handle = bitmap.GetHicon();
        var next = Icon.FromHandle(handle);
        var previous = _current;
        _current = next;
        _key = key;
        Destroy(previous);
        return next;
    }

    /// Bitmap ARGB size×size. Puro (GDI+ em memória); não depende da bandeja existir.
    public static Bitmap Render(string? compactText, bool paused, int size)
    {
        size = Math.Clamp(size, 16, 256);
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        float u = size / 24f;   // grade de 24 unidades do original
        using (var tile = RoundedRect(new RectangleF(0.5f * u, 0.5f * u, 23 * u, 23 * u), 5 * u))
        using (var fill = new SolidBrush(compactText is null ? Tile : TileRunning))
            g.FillPath(fill, tile);

        if (compactText is null) DrawClosedEye(g, u);
        else DrawText(g, compactText, paused ? Accent : Ink, size, u);
        return bitmap;
    }

    // Olho fechado: curva inferior da lente (1,5;12)→(22,5;12) e cinco cílios para baixo.
    private static void DrawClosedEye(Graphics g, float u)
    {
        using var pen = new Pen(Ink, Math.Max(1f, 1.75f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(pen, 3f * u, 10f * u, 8f * u, 16f * u, 16f * u, 16f * u, 21f * u, 10f * u);
        foreach (var x in new[] { 0.2f, 0.35f, 0.5f, 0.65f, 0.8f })
        {
            var px = (3f + 18f * x) * u;
            var py = BezierY(x) * u;
            g.DrawLine(pen, px, py + 1.5f * u, px + (x - 0.5f) * 2.5f * u, py + 4.25f * u);
        }
    }

    // y da curva de cima (aproximação da Bézier cúbica de DrawClosedEye) na fração t.
    private static float BezierY(float t)
    {
        float a = 1 - t;
        return a * a * a * 10f + 3 * a * a * t * 16f + 3 * a * t * t * 16f + t * t * t * 10f;
    }

    private static void DrawText(Graphics g, string text, Color color, int size, float u)
    {
        var box = new RectangleF(1.5f * u, 1.5f * u, 21 * u, 21 * u);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        // Maior fonte que cabe na largura (3 caracteres em 16 px precisam encolher).
        var emSize = size * 0.62f;
        Font font = new("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        try
        {
            while (emSize > 6f && g.MeasureString(text, font, PointF.Empty, format).Width > box.Width)
            {
                font.Dispose();
                emSize -= 0.5f;
                font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, box, format);
        }
        finally { font.Dispose(); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Destroy(Icon? icon)
    {
        if (icon is null) return;
        var handle = icon.Handle;
        icon.Dispose();
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }

    public void Dispose()
    {
        Destroy(_current);
        _current = null;
        _key = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
