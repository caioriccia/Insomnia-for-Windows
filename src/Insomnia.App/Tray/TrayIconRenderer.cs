// Ícone da bandeja (desenho-ui §3.8): glifo monocromático, como os ícones do próprio Windows 11.
//   Idle    = olho fechado (Hide, U+ED1A), traçado
//   Running = olho (RedEye, U+E7B3), preenchido
//   Paused  = olho + pequeno selo de pausa (duas barras cheias) no canto inferior direito
//   Warning = olho + ponto de 5 px em Caution no canto inferior direito
// Cor: branco na barra de tarefas escura, preto na clara (lightTaskbar = SystemUsesLightTheme).
// A contagem saiu do ícone (a pílula a mostra); o tooltip continua com ela.
//
// Os glifos vêm da fonte Segoe Fluent Icons (Windows 11) ou Segoe MDL2 Assets (Windows 10, mesmos
// códigos). Se nenhuma das duas estiver instalada, formas vetoriais equivalentes — o ícone nunca
// sai vazio. Os códigos vêm de Ui/Theme/Glyphs.cs (só constantes: nenhuma dependência de WPF).
//
// HICON: Bitmap.GetHicon cria um handle que o GC não solta; Icon.FromHandle não assume a
// posse. Por isso Update guarda o handle atual e chama DestroyIcon no anterior assim que o
// novo foi entregue (o shell copia o ícone ao receber), e Dispose destrói o último.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Tray;

public enum TrayIconState { Idle, Running, Paused, Warning }

public sealed class TrayIconRenderer : IDisposable
{
    // Insomnia.Caution do §3.3: SystemFillColorCaution no claro / no escuro.
    private static readonly Color CautionOnLight = ColorTranslator.FromHtml("#9D5D00");
    private static readonly Color CautionOnDark = ColorTranslator.FromHtml("#FCE100");

    // Primeira fonte de ícones instalada (null = desenhar vetor). Consultada uma vez só.
    private static readonly Lazy<string?> IconFont = new(FindIconFont);

    private Icon? _current;
    private string? _key;

    /// Último ícone entregue (null antes do primeiro Update).
    public Icon? Current => _current;

    /// Devolve o ícone para o estado; mesmo estado, tamanho e tema ⇒ o mesmo objeto (sem novo HICON).
    public Icon Update(TrayIconState state, int size, bool lightTaskbar)
    {
        var key = $"{state}|{size}|{lightTaskbar}";
        if (_current is not null && key == _key) return _current;

        using var bitmap = Render(state, size, lightTaskbar);
        var handle = bitmap.GetHicon();
        var next = Icon.FromHandle(handle);
        var previous = _current;
        _current = next;
        _key = key;
        Destroy(previous);
        return next;
    }

    /// Bitmap ARGB size×size. Puro (GDI+ em memória); não depende da bandeja existir.
    public static Bitmap Render(TrayIconState state, int size, bool lightTaskbar)
    {
        size = Math.Clamp(size, 16, 256);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;   // ClearType sobre fundo transparente suja a borda
        g.Clear(Color.Transparent);

        var ink = lightTaskbar ? Color.Black : Color.White;
        var font = IconFont.Value;
        float s = size;

        if (state == TrayIconState.Idle)
        {
            if (font is null) DrawClosedEyeVector(g, ink, s);
            else DrawGlyph(g, font, Glyphs.EyeClosed, ink, new RectangleF(0, 0, s, s));
            return bitmap;
        }

        if (font is null) DrawEyeVector(g, ink, s);
        else DrawGlyph(g, font, Glyphs.Eye, ink, new RectangleF(0, 0, s, s));

        if (state == TrayIconState.Paused)
        {
            // Selo de pausa no canto, com um anel vazado para separá-lo do olho. Sempre vetor: o
            // Pause da fonte é traçado e, a 8 px, vira borrão; duas barras cheias leem melhor.
            var d = s * 0.5f;
            var badge = new RectangleF(s - d, s - d, d, d);
            Knockout(g, Inflate(badge, s / 16f));
            DrawPauseVector(g, ink, badge);
        }
        else if (state == TrayIconState.Warning)
        {
            // Ponto de 5 px na grade de 16, escalado; anel vazado de 1 px.
            var d = Math.Max(5f, s * 5f / 16f);
            var dot = new RectangleF(s - d, s - d, d, d);
            Knockout(g, Inflate(dot, s / 16f));
            using var brush = new SolidBrush(lightTaskbar ? CautionOnLight : CautionOnDark);
            g.FillEllipse(brush, dot);
        }
        return bitmap;
    }

    // Glifo da fonte de ícones como contorno (GraphicsPath), centrado pela caixa real do desenho e
    // reduzido se passar da caixa: independe das métricas de linha da fonte.
    private static void DrawGlyph(Graphics g, string family, string glyph, Color color, RectangleF box)
    {
        using var fontFamily = new FontFamily(family);
        using var path = new GraphicsPath();
        path.AddString(glyph, fontFamily, (int)FontStyle.Regular, box.Height, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var scale = Math.Min(1f, Math.Min(box.Width / bounds.Width, box.Height / bounds.Height));
        using var matrix = new Matrix();
        matrix.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        matrix.Scale(scale, scale);
        matrix.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f));
        path.Transform(matrix);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    // Olho aberto, preenchido: amêndoa cheia com a pupila separada por um anel vazado (grade de 16).
    private static void DrawEyeVector(Graphics g, Color ink, float s)
    {
        var u = s / 16f;
        using (var lens = new GraphicsPath())
        {
            lens.AddBezier(1f * u, 8f * u, 4f * u, 3f * u, 12f * u, 3f * u, 15f * u, 8f * u);
            lens.AddBezier(15f * u, 8f * u, 12f * u, 13f * u, 4f * u, 13f * u, 1f * u, 8f * u);
            lens.CloseFigure();
            using var brush = new SolidBrush(ink);
            g.FillPath(brush, lens);
        }
        Knockout(g, new RectangleF(5.2f * u, 5.2f * u, 5.6f * u, 5.6f * u));
        using var pupil = new SolidBrush(ink);
        g.FillEllipse(pupil, 6.4f * u, 6.4f * u, 3.2f * u, 3.2f * u);
    }

    // Olho fechado, traçado: curva inferior da lente e cinco cílios para baixo (grade de 24).
    private static void DrawClosedEyeVector(Graphics g, Color ink, float s)
    {
        var u = s / 24f;
        using var pen = new Pen(ink, Math.Max(1f, 1.75f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(pen, 2f * u, 9f * u, 7.5f * u, 16f * u, 16.5f * u, 16f * u, 22f * u, 9f * u);
        foreach (var x in new[] { 0.2f, 0.35f, 0.5f, 0.65f, 0.8f })
        {
            var px = (2f + 20f * x) * u;
            var py = ClosedEyeY(x) * u;
            g.DrawLine(pen, px, py + 1.5f * u, px + (x - 0.5f) * 3f * u, py + 5f * u);
        }
    }

    // y da curva de DrawClosedEyeVector (Bézier cúbica 9→16→16→9) na fração t.
    private static float ClosedEyeY(float t)
    {
        float a = 1 - t;
        return a * a * a * 9f + 3 * a * a * t * 16f + 3 * a * t * t * 16f + t * t * t * 9f;
    }

    private static void DrawPauseVector(Graphics g, Color ink, RectangleF box)
    {
        // Presas ao pixel inteiro: a 16 px, barra fracionária vira cinza borrado.
        var w = MathF.Max(1f, MathF.Round(box.Width * 0.25f));
        var h = MathF.Round(box.Height * 0.75f);
        var top = MathF.Round(box.Y + (box.Height - h) / 2f);
        using var brush = new SolidBrush(ink);
        g.FillRectangle(brush, MathF.Round(box.X + box.Width * 0.18f), top, w, h);
        g.FillRectangle(brush, MathF.Round(box.Right - box.Width * 0.18f - w), top, w, h);
    }

    // Apaga (alfa 0) um círculo: separa selo/ponto do desenho por baixo.
    private static void Knockout(Graphics g, RectangleF circle)
    {
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillEllipse(clear, circle);
        g.CompositingMode = mode;
    }

    private static RectangleF Inflate(RectangleF r, float by)
    {
        r.Inflate(by, by);
        return r;
    }

    // new FontFamily lança ArgumentException quando a fonte não está instalada (o GDI+ não troca
    // em silêncio por outra, ao contrário de new Font).
    private static string? FindIconFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException) { }
        }
        return null;
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
