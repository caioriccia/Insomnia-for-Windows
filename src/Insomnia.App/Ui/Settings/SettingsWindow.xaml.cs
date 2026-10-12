// Janela de Configurações (desenho-ui §3.7). Criada uma vez pelo SettingsPresenter; fechar só
// esconde (como o SettingsForm velho), e só o encerramento do app (ou CloseForGood) fecha de
// verdade. As páginas são criadas na primeira visita e reaproveitadas.
//
// Material: Mica pelo DWM quando BackdropPolicy deixa e o DWM aceita; senão fundo sólido do tema
// (Insomnia.Surface.Solid) — a janela nunca fica preta nem invisível. Mica só aparece se o WPF não
// pintar nada por cima: Background e CompositionTarget.BackgroundColor transparentes (desenho-ui
// §6, risco 2). Tema, transparência ou composição mudando com a janela aberta reaplicam tudo.
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.App.Ui.Settings.Pages;

namespace Insomnia.App.Ui.Settings;

public partial class SettingsWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private readonly SettingsModel _model;
    private readonly Dictionary<SettingsPage, FrameworkElement> _pages = [];
    private HwndSource? _source;
    private bool _placed;
    private bool _closingForGood;

    public SettingsWindow(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        SourceInitialized += (_, _) => OnSourceReady();
        ShowPage(model.Current);
    }

    /// O que o DWM aceitou (Solid quando a política ou o DWM recusaram a Mica).
    public Backdrop EffectiveBackdrop { get; private set; } = Backdrop.Solid;

    /// Mostra (ou traz para a frente) com os valores atuais: relê config e HKCU Run, e as leituras
    /// lentas da página aberta (o modelo só as dispara ao TROCAR de página).
    public void ShowAndActivate()
    {
        _model.Reload();
        if (_model.Current == SettingsPage.Tampa) _ = _model.RefreshWouldFreezeAsync();
        if (_model.Current == SettingsPage.Rede) _ = _model.RefreshProfileStateAsync();

        if (!_placed)
        {
            // Centrada no monitor principal (onde ficam pílula e painel, §4); depois disso, onde a
            // pessoa deixar.
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            _placed = true;
        }

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Vinda do menu da bandeja, o Windows às vezes só pisca na barra de tarefas: um topmost
        // momentâneo traz a janela para cima sem deixá-la presa por cima das outras.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// Fecha de verdade (Dispose do SettingsPresenter).
    public void CloseForGood()
    {
        _closingForGood = true;
        _model.PropertyChanged -= OnModelChanged;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // O SSID digitado e não confirmado não se perde ao fechar.
        _model.CommitSsid();
        base.OnClosing(e);
        if (_closingForGood || Application.Current?.Dispatcher.HasShutdownStarted == true) return;
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ páginas

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsModel.Current)) ShowPage(_model.Current);
    }

    private void ShowPage(SettingsPage page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                SettingsPage.Sessao => new SessaoPage(),
                SettingsPage.Tampa => new TampaPage(),
                SettingsPage.Agentes => new AgentesPage(),
                SettingsPage.Bateria => new BateriaPage(),
                SettingsPage.Rede => new RedePage(),
                _ => new AppPage(),
            };
            _pages[page] = view;
        }
        PageTitle.Text = SettingsModel.Nav.FirstOrDefault(n => n.Page == page)?.Title ?? "";
        PageHost.Content = view;
        PageScroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ DWM

    private void OnSourceReady()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        ApplyTheme(hwnd);
    }

    /// Moldura no tema e Mica pela política; sem Mica, fundo sólido do tema.
    private void ApplyTheme(IntPtr hwnd)
    {
        Dwm.TrySetImmersiveDarkMode(hwnd, Dwm.AppsUseDarkTheme);

        var chosen = BackdropPolicy.Choose(Backdrop.Mica, Dwm.OsBuild, Dwm.TransparencyEnabled, SystemParameters.HighContrast);
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
}
