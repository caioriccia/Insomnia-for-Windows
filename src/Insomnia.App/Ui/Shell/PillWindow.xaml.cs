// Pílula (desenho-ui §3.4, §3.9). Só desenha o PillState e repassa o botão esquerdo: quem decide
// clique/arrasto é a UiShell, com o PullDownGesture. A captura do mouse fica AQUI, na pílula, do
// Down ao Up — por isso, durante o arrasto, a janela continua de pé (só o conteúdo some) e o
// painel é que cobre: esconder a janela derrubaria a captura no meio do gesto.
// Direito/meio: nada (o menu é da bandeja).
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.App.Ui.Theme;
using Insomnia.App.Ui.Windows;

namespace Insomnia.App.Ui.Shell;

public partial class PillWindow : FlyoutWindow
{
    private PillState? _state;

    public PillWindow() : base(Backdrop.Acrylic, activatable: false)
    {
        InitializeComponent();
        // Nunca tem foco: não há o que fechar por desativação, e Esc nunca chega aqui.
        CloseOnDeactivate = false;

        Root.MouseLeftButtonDown += OnDown;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnUp;
        Root.LostMouseCapture += (_, _) => PointerLost?.Invoke();
        Root.MouseEnter += (_, _) => FadeOverlay(1);
        Root.MouseLeave += (_, _) => { if (!Root.IsMouseCaptured) FadeOverlay(0); };
        // Escondida no meio do hover/pressão o MouseLeave não vem: volta limpa na próxima vez.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            Overlay.BeginAnimation(OpacityProperty, null);
            Overlay.Opacity = 0;
            Overlay.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Insomnia.Control.Hover");
        };
    }

    /// Solta a captura do mouse (gesto cancelado de fora, ex.: o painel foi fechado no meio).
    public void ReleasePointer()
    {
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
    }

    /// Botão esquerdo apertado; y em epx, relativo à pílula.
    public event Action<double>? PointerDown;
    /// Ponteiro andou com o botão apertado (captura ativa).
    public event Action<double>? PointerMove;
    /// Botão solto (antes de soltar a captura).
    public event Action? PointerUp;
    /// Captura perdida (outra janela tomou o mouse, Esc do sistema…).
    public event Action? PointerLost;

    /// Largura que a pílula pede para o estado atual (epx): conteúdo + 14 de cada lado, mínimo 148.
    public double DesiredPillWidth
    {
        get
        {
            ContentRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return ShellMath.PillWidth(ContentRow.DesiredSize.Width);
        }
    }

    /// Opacidade do conteúdo (aparecer/sumir; zero enquanto o painel a cobre).
    public double ContentOpacity
    {
        get => Root.Opacity;
        set => Root.Opacity = value;
    }

    public void Render(PillState state)
    {
        if (state == _state) return;
        _state = state;
        GlyphText.Text = state.Glyph;
        Label.Text = state.Text;
        var (glyph, text) = ShellMath.PillBrushes(state.Tone);
        GlyphText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, glyph);
        Label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, text);
        if (state.Dot is { } dot)
        {
            Dot.Visibility = Visibility.Visible;
            Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ShellMath.DotBrush(dot));
        }
        else
        {
            Dot.Visibility = Visibility.Collapsed;
        }
        Root.ToolTip = string.IsNullOrEmpty(state.Tooltip) ? null : state.Tooltip;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Root.CaptureMouse();
        Overlay.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Insomnia.Control.Pressed");
        FadeOverlay(1);
        PointerDown?.Invoke(e.GetPosition(this).Y);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!Root.IsMouseCaptured) return;
        PointerMove?.Invoke(e.GetPosition(this).Y);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        Overlay.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Insomnia.Control.Hover");
        if (!Root.IsMouseCaptured) return;
        PointerUp?.Invoke();
        Root.ReleaseMouseCapture();
        if (!Root.IsMouseOver) FadeOverlay(0);
        e.Handled = true;
    }

    private void FadeOverlay(double to)
    {
        var fade = new DoubleAnimation(to, new Duration(Motion.Scaled(Motion.Fast, SystemParameters.ClientAreaAnimation)));
        Overlay.BeginAnimation(OpacityProperty, fade);
    }
}
