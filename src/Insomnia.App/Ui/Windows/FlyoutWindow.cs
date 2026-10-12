// CONTRATO CONGELADO (desenho-ui §3.2, §4 e §5.5): base das janelas flutuantes (pílula,
// painel, menu da bandeja). Sem moldura, fora da barra de tarefas, sempre por cima; no
// SourceInitialized aplica ex-styles, cantos arredondados, material do DWM (com fundo sólido
// se a política ou o DWM recusarem) e moldura no tema. Some (Hide) ao perder o foco ou com
// Esc quando CloseOnDeactivate. Nunca é fechada de verdade: só Hide()/Show() — um Close que
// não seja o encerramento do app vira OnDismiss.
//
// Por que o fundo é transparente: o DWM só pinta Acrylic/Mica se o WPF não pintar nada por
// cima (Background e CompositionTarget.BackgroundColor transparentes, AllowsTransparency
// false). Se a janela sair preta/opaca, é aí que se olha (desenho-ui §6, risco 2).
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Insomnia.App.Ui.Native;

namespace Insomnia.App.Ui.Windows;

public abstract class FlyoutWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private readonly Backdrop _wanted;
    private readonly bool _activatable;
    private HwndSource? _source;

    protected FlyoutWindow(Backdrop wanted, bool activatable)
    {
        _wanted = wanted;
        _activatable = activatable;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = activatable;
        Focusable = activatable;
        Background = Brushes.Transparent;
        EffectiveBackdrop = Backdrop.Solid;   // até o SourceInitialized dizer o que o DWM aceitou
        SourceInitialized += (_, _) => OnSourceReady();
    }

    public bool CloseOnDeactivate { get; set; } = true;

    /// O que o DWM aceitou (Solid quando a política ou o DWM recusaram o material).
    public Backdrop EffectiveBackdrop { get; private set; }

    /// Converte para DIP e posiciona. Recebe pixels físicos (UiPlacement); quem converte é o
    /// próprio Windows (SetWindowPos), que conhece o DPI do monitor de DESTINO — atribuir
    /// Left/Top em DIP usaria a escala do monitor onde a janela está agora e erraria ao
    /// atravessar monitores com escalas diferentes. Sem handle ou se o SetWindowPos falhar,
    /// cai para Left/Top/Width/Height pela TransformFromDevice.
    public void MoveTo(PxRect physical)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (Dwm.TryMoveWindow(hwnd, physical)) return;

        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(physical.X, physical.Y));
        var size = fromDevice.Transform(new Vector(physical.W, physical.H));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = size.X;
        Height = size.Y;
    }

    /// Fecha do jeito da janela flutuante: escondendo.
    protected virtual void OnDismiss() => Hide();

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (CloseOnDeactivate && IsVisible) OnDismiss();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || !CloseOnDeactivate) return;
        e.Handled = true;
        OnDismiss();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // No encerramento do app o WPF fecha as janelas ignorando o cancelamento; fora dele,
        // um Close (Alt+F4, por exemplo) só esconde.
        if (Application.Current?.Dispatcher.HasShutdownStarted == true) return;
        e.Cancel = true;
        OnDismiss();
    }

    // ------------------------------------------------------------------ DWM

    private void OnSourceReady()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        if (_activatable) Dwm.MakeToolWindow(hwnd);
        else Dwm.MakeNoActivateToolWindow(hwnd);
        Dwm.TrySetRoundCorners(hwnd, round: true);
        ApplyTheme(hwnd);
    }

    /// Moldura no tema e material pela política; sem material, fundo sólido do tema.
    private void ApplyTheme(IntPtr hwnd)
    {
        Dwm.TrySetImmersiveDarkMode(hwnd, Dwm.AppsUseDarkTheme);

        var chosen = BackdropPolicy.Choose(_wanted, Dwm.OsBuild, Dwm.TransparencyEnabled, SystemParameters.HighContrast);
        var applied = chosen != Backdrop.Solid && Dwm.TrySetBackdrop(hwnd, chosen);
        if (applied)
        {
            if (_source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
            EffectiveBackdrop = chosen;
        }
        else
        {
            if (chosen != Backdrop.Solid || EffectiveBackdrop != Backdrop.Solid) Dwm.TrySetBackdrop(hwnd, Backdrop.Solid);
            SetResourceReference(BackgroundProperty, "Insomnia.Surface.Solid");
            EffectiveBackdrop = Backdrop.Solid;
        }
    }

    /// Tema, transparência ou composição mudaram: reaplica moldura e material.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOMPOSITIONCHANGED)
            ApplyTheme(hwnd);
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        base.OnClosed(e);
    }
}
