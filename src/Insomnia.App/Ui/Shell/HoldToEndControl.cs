// "Segure para encerrar" do painel (desenho-ui §3.5 rodapé, §3.10): botão de largura total,
// 40 de altura, raio 4. Enquanto pressionado, uma faixa Critical cresce da esquerda em 600 ms e o
// texto vira "Encerrando…"; completar dispara Completed. Soltar antes, ou mover mais de 12 epx,
// cancela e a faixa recua em 167 ms. Teclado: Espaço/Enter segurados fazem o mesmo (igual ao
// HoldToEndButton velho). A ação de acessibilidade padrão (Invoke) encerra direto, sem segurar.
//
// Quem conta é a HoldToEndMachine (pura); aqui só se repassa ponteiro/teclado e um
// DispatcherTimer de 15 ms chama Advance.
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Ui.Shell;

public sealed class HoldToEndControl : Border
{
    public const string IdleText = "Segure para encerrar";
    public const string HoldingText = "Encerrando…";
    public const string EndedText = "Sessão encerrada";

    private readonly HoldToEndMachine _machine = new();
    private readonly DispatcherTimer _timer;
    private readonly ScaleTransform _barScale = new(0, 1);
    private readonly Border _overlay;
    private readonly TextBlock _label;
    private bool _completed;

    public HoldToEndControl()
    {
        Height = 40;
        Focusable = true;
        FocusVisualStyle = null;   // o foco de teclado vira traço de acento (OnIsKeyboardFocusWithinChanged)
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        SetResourceReference(CornerRadiusProperty, "Insomnia.Radius.Control");
        SetResourceReference(BackgroundProperty, "Insomnia.Control.Fill");
        SetResourceReference(BorderBrushProperty, "Insomnia.Control.Stroke");
        AutomationProperties.SetName(this, IdleText);

        var bar = new Border { RenderTransform = _barScale, RenderTransformOrigin = new Point(0, 0.5), Opacity = 0.28 };
        bar.SetResourceReference(BackgroundProperty, "Insomnia.Critical");
        bar.SetResourceReference(CornerRadiusProperty, "Insomnia.Radius.Control");

        _overlay = new Border { Opacity = 0 };
        _overlay.SetResourceReference(BackgroundProperty, "Insomnia.Control.Hover");
        _overlay.SetResourceReference(CornerRadiusProperty, "Insomnia.Radius.Control");

        _label = new TextBlock
        {
            Text = IdleText,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "Insomnia.Font.Text");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Insomnia.Text.Primary");

        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(_overlay);
        grid.Children.Add(bar);
        grid.Children.Add(_label);
        Child = grid;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Advance();
    }

    /// A faixa completou (ou a acessibilidade invocou): a sessão deve ser encerrada.
    public event Action? Completed;

    /// Volta ao estado de repouso (texto e faixa), sem animação.
    public void Reset()
    {
        _timer.Stop();
        _machine.Release();
        _completed = false;
        _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _barScale.ScaleX = 0;
        SetLabel(IdleText);
    }

    /// Depois do fim: "Sessão encerrada" até o próximo Reset.
    public void ShowEnded() => SetLabel(EndedText);

    // ------------------------------------------------------------------ ponteiro

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_completed) return;
        Focus();
        CaptureMouse();
        var p = e.GetPosition(this);
        Begin(p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_machine.Holding) return;
        var p = e.GetPosition(this);
        if (_machine.Move(p.X, p.Y)) Cancel();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cancel();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Cancel();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        FadeOverlay(1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        FadeOverlay(0);
    }

    // ------------------------------------------------------------------ teclado

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        if (e.IsRepeat || _machine.Holding || _completed) return;
        Begin(0, 0);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        Cancel();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsMouseCaptured) Cancel();
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        // Foco visível só pelo teclado: traço de acento no lugar do traço do controle.
        if (IsKeyboardFocused && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            SetResourceReference(BorderBrushProperty, "Insomnia.Accent");
        else
            SetResourceReference(BorderBrushProperty, "Insomnia.Control.Stroke");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    // ------------------------------------------------------------------ máquina

    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private void Begin(double x, double y)
    {
        _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _barScale.ScaleX = 0;
        _machine.Press(x, y, NowMs());
        SetLabel(HoldingText);
        _timer.Start();
    }

    private void Advance()
    {
        if (!_machine.Holding) { _timer.Stop(); return; }
        var completed = _machine.Advance(NowMs());
        _barScale.ScaleX = _machine.Progress;
        if (!completed) return;
        _timer.Stop();
        Complete();
    }

    private void Cancel()
    {
        if (_completed) return;
        var wasHolding = _machine.Holding;
        _machine.Release();
        _timer.Stop();
        if (!wasHolding && _barScale.ScaleX == 0) return;
        SetLabel(IdleText);
        var back = new DoubleAnimation(_barScale.ScaleX, 0,
            new Duration(Motion.Scaled(Motion.Short, SystemParameters.ClientAreaAnimation)))
        {
            EasingFunction = new FluentEase(Motion.Accelerate),
        };
        _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
    }

    private void Complete()
    {
        _completed = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _barScale.ScaleX = 1;
        Completed?.Invoke();
    }

    private void SetLabel(string text)
    {
        _label.Text = text;
        AutomationProperties.SetName(this, text);
    }

    private void FadeOverlay(double to)
    {
        var fade = new DoubleAnimation(to, new Duration(Motion.Scaled(Motion.Fast, SystemParameters.ClientAreaAnimation)));
        _overlay.BeginAnimation(OpacityProperty, fade);
    }

    /// Botão para a acessibilidade; Invoke encerra sem segurar.
    private sealed class Peer(HoldToEndControl owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(HoldToEndControl);

        protected override bool IsKeyboardFocusableCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            if (!owner.IsEnabled || owner._completed) return;
            owner.Complete();
        }
    }
}
