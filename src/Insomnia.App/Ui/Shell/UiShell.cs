// Casca da UI (desenho-ui §3.4, §3.5, §3.9, §3.10, §4): a pílula no topo do monitor principal e
// o painel que desce dela. Assina Changed/Tick do host, calcula PillState/PanelState pelos
// presenters, decide quando a pílula aparece (PillVisibilityPolicy + tela cheia a cada Tick),
// posiciona (UiPlacement.TopCenter), anima e executa os comandos do painel. Thread da UI.
//
// Como o painel "desce": numa janela com material do DWM o recorte do WPF não recorta o
// acrílico, então a JANELA do painel é que vai do tamanho da pílula ao tamanho final, quadro a
// quadro (ShellMath.ClipAt), com o conteúdo já no tamanho final, centrado e recortado com o raio
// da vez. A pílula some no primeiro quadro do painel.
//
// Arrasto: a pílula captura o mouse do Down ao Up (PillWindow). Durante o arrasto a janela da
// pílula fica de pé (esconder derrubaria a captura), sem conteúdo e coberta pelo painel, que
// aparece sem ativar e segue o PullDownGesture.Offset; ao soltar, completa (e só então ativa) ou
// volta. Foco negado pelo Windows (desenho-ui §6, risco 4): o painel abre sem foco e o
// Deactivated nunca vem; por isso também fecha pela alça, e um relógio de 1 s fecha quando outra
// janela vai para o primeiro plano por 2 leituras seguidas.
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Insomnia.App.Tray;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.App.Ui.Theme;
using Insomnia.App.Ui.Windows;
using Insomnia.Core.Time;
using Microsoft.Win32;

namespace Insomnia.App.Ui.Shell;

public sealed class UiShell : IShell
{
    private enum PanelPhase { Closed, Pulling, Opening, Open, Closing }

    private readonly ISessionFacade _host;
    private readonly ISettingsHost _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PullDownGesture _pull = new();
    private readonly FrameAnimator _panelMotion = new();
    private readonly FrameAnimator _pillMotion = new();
    private readonly DispatcherTimer _autoCollapse;
    private readonly DispatcherTimer _focusWatch;

    private PillWindow? _pill;
    private PanelWindow? _panel;
    private PanelPhase _phase;
    private double _progress;                              // 0 = pílula, 1 = painel aberto
    private double _contentOpacity;
    private double _panelHeight = ShellMath.PillHeight;    // epx, medido no Render
    private double _startWidth = ShellMath.PillMinWidth;   // largura da pílula quando o painel saiu dela
    private bool _pillShown;                               // pílula na tela (ou aparecendo)
    private bool _quickAppear;                             // reaparecer em 83 ms (fim do fechamento)
    private bool _busy;
    private TimeSpan? _selectedPreset;                     // chip escolhido no modo Iniciar
    private long _dismissedAt = long.MinValue / 2;
    private bool _autoArmed;
    private bool _interacted;
    private long _autoArmedAt;
    private IntPtr _foregroundAtOpen;
    private int _foreignTicks;
    private bool _started;
    private bool _disposed;

    public UiShell(ISessionFacade host, ISettingsHost settings)
    {
        _host = host;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Uma folga sobre o 1,5 s: o relógio do DispatcherTimer não é o do Stopwatch.
        _autoCollapse = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = ShellMath.AutoCollapseDelay + TimeSpan.FromMilliseconds(20),
        };
        _autoCollapse.Tick += (_, _) => OnAutoCollapse();
        _focusWatch = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _focusWatch.Tick += (_, _) => OnFocusWatch();
    }

    public bool IsPanelOpen => _phase is PanelPhase.Pulling or PanelPhase.Opening or PanelPhase.Open;

    /// Liga a pílula ao Changed/Tick. Uma vez, por AppHost.Start.
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        EnsureWindows();
        _host.Changed += Refresh;
        _host.Tick += Refresh;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Refresh();
    }

    /// Abre no modo do estado atual (Sessão se há sessão, senão Iniciar).
    public void ShowPanel()
    {
        if (_disposed) return;
        EnsureWindows();
        var panel = _panel!;
        switch (_phase)
        {
            case PanelPhase.Open:
            case PanelPhase.Opening:
                panel.Activate();
                return;
            case PanelPhase.Pulling:
                return;   // o gesto em curso decide
        }

        var mode = PreparePanel();
        if (_phase == PanelPhase.Closed)
        {
            panel.ShowError(null);
            panel.ResetInputs();
            _startWidth = _pillShown ? _pill!.DesiredPillWidth : ShellMath.PillMinWidth;
            ApplyPanelFrame(0, 0);
            panel.Show();
            ShellNative.BringToTop(new WindowInteropHelper(panel).Handle);
        }
        HidePillNow();
        _phase = PanelPhase.Opening;
        panel.Activate();
        panel.FocusInitial(ShellMath.FocusFor(mode));
        var duration = TimeSpan.FromMilliseconds(Motion.Normal.TotalMilliseconds * (1 - _progress));
        AnimatePanel(1, duration, Motion.Decelerate, fadeIn: true, OnOpened);
    }

    public void HidePanel()
    {
        if (_disposed || _panel is null || _phase is PanelPhase.Closed or PanelPhase.Closing) return;
        if (_phase == PanelPhase.Pulling)
        {
            _pull.Cancel();
            _pill?.ReleasePointer();
        }
        StopAutoCollapse();
        _focusWatch.Stop();
        HidePillNow();
        _phase = PanelPhase.Closing;
        var duration = TimeSpan.FromMilliseconds(ShellMath.CloseDuration.TotalMilliseconds * _progress);
        AnimatePanel(0, duration, Motion.Accelerate, fadeIn: false, OnClosed);
    }

    public void TogglePanel()
    {
        if (ShellMath.ToggleOpens(IsPanelOpen, Now() - _dismissedAt)) ShowPanel();
        else if (IsPanelOpen) HidePanel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            _host.Changed -= Refresh;
            _host.Tick -= Refresh;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        _autoCollapse.Stop();
        _focusWatch.Stop();
        _panelMotion.Stop();
        _pillMotion.Stop();
        if (_dispatcher.HasShutdownStarted) return;
        try
        {
            _pill?.Hide();
            _panel?.Hide();
        }
        catch (InvalidOperationException) { }   // janela já fechada pelo fim do app
    }

    // ------------------------------------------------------------------ janelas

    private void EnsureWindows()
    {
        if (_pill is not null) return;
        var pill = new PillWindow();
        pill.PointerDown += OnPillDown;
        pill.PointerMove += OnPillMove;
        pill.PointerUp += OnPillUp;
        pill.PointerLost += OnPillLost;
        pill.DpiChanged += (_, _) => _dispatcher.InvokeAsync(Reposition);

        var panel = new PanelWindow();
        panel.DismissRequested += OnPanelDismissed;
        panel.HandleCloseRequested += HidePanel;
        panel.SettingsRequested += OpenSettings;
        panel.ChipClicked += OnChip;
        panel.StartRequested += OnStart;
        panel.ExtendRequested += OnExtendText;
        panel.NoticeActionRequested += OnNoticeAction;
        panel.EndRequested += OnEnd;
        panel.Interacted += () => _interacted = true;
        panel.DpiChanged += (_, _) => _dispatcher.InvokeAsync(Reposition);

        _pill = pill;
        _panel = panel;
    }

    /// Redesenha tudo o que está na tela (Changed e Tick).
    private void Refresh()
    {
        if (_disposed || _pill is null || _panel is null) return;
        if (Snapshot() is not { } s) return;
        var config = _host.Config.Current;
        if (_selectedPreset is { } chosen && (config.Presets.IsDefault || !config.Presets.Contains((int)chosen.TotalSeconds))) _selectedPreset = null;

        var visible = PillVisibilityPolicy.ShouldShow(s.Session is not null, _phase != PanelPhase.Closed, Dwm.QueryForeground());
        var pill = PillPresenter.From(s, visible);
        _pill.Render(pill);
        SyncPill(pill.Visible);

        // Fechando, o painel fica como estava (o conteúdo já está sumindo).
        if (_phase is PanelPhase.Pulling or PanelPhase.Opening or PanelPhase.Open)
        {
            _panel.Render(PanelPresenter.From(s, config, _selectedPreset));
            _panelHeight = _panel.MeasureContentHeight();
            if (_phase == PanelPhase.Open && !_panelMotion.IsRunning) ApplyPanelFrame(1, 1);
        }
    }

    private StatusSnapshot? Snapshot()
    {
        try { return _host.Snapshot(); }
        catch (Exception ex)
        {
            _host.Log.Error($"painel: retrato ilegível: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// Renderiza o painel com o estado atual e mede a altura; devolve o modo.
    private PanelMode PreparePanel()
    {
        var s = Snapshot();
        if (s is not null) _panel!.Render(PanelPresenter.From(s, _host.Config.Current, _selectedPreset));
        _panelHeight = _panel!.MeasureContentHeight();
        return _panel.Mode ?? PanelMode.Start;
    }

    // ------------------------------------------------------------------ pílula

    private void SyncPill(bool want)
    {
        var pill = _pill!;
        if (_phase == PanelPhase.Pulling) return;   // coberta pelo painel, com a captura do mouse
        if (want)
        {
            var width = pill.DesiredPillWidth;
            if (!_pillShown)
            {
                _pillShown = true;
                AppearPill(width, _quickAppear);
            }
            else if (!_pillMotion.IsRunning)
            {
                PlacePill(width, 0);   // texto cresceu ou o monitor mudou
            }
        }
        else if (_pillShown)
        {
            _pillShown = false;
            var fade = Motion.Scaled(Motion.Fast, SystemParameters.ClientAreaAnimation);
            _pillMotion.Run(fade, Motion.Accelerate, (_, e) => pill.ContentOpacity = 1 - e, () =>
            {
                if (!_pillShown) pill.Hide();
            });
        }
    }

    /// Aparece: opacidade 0 → 1 e −8 → 0 em 167 ms (Decelerate); depois do painel, só a
    /// opacidade em 83 ms.
    private void AppearPill(double width, bool quick)
    {
        var pill = _pill!;
        var animations = SystemParameters.ClientAreaAnimation;
        var offset = quick ? 0 : ShellMath.PillAppearOffset;
        pill.ContentOpacity = 0;
        PlacePill(width, offset);
        pill.Show();
        var duration = Motion.Scaled(quick ? Motion.Fast : Motion.Short, animations);
        _pillMotion.Run(duration, Motion.Decelerate, (_, e) =>
        {
            pill.ContentOpacity = e;
            if (offset != 0) PlacePill(width, offset * (1 - e));
        });
    }

    private void HidePillNow()
    {
        _pillMotion.Stop();
        _pillShown = false;
        _pill?.Hide();
    }

    private void PlacePill(double width, double yOffset) =>
        Place(_pill!, width, ShellMath.PillHeight, yOffset);

    // ------------------------------------------------------------------ arrasto

    private long Now() => _clock.ElapsedMilliseconds;

    private void OnPillDown(double y)
    {
        if (_disposed || _phase != PanelPhase.Closed) return;
        _pull.Down(y, Now());
    }

    private void OnPillMove(double y)
    {
        if (_pull.Phase == PullPhase.Idle) return;
        var before = _pull.Phase;
        _pull.Move(y, Now());
        if (_pull.Phase != PullPhase.Pulling) return;
        if (before != PullPhase.Pulling) BeginReveal();
        if (_phase != PanelPhase.Pulling) return;
        var progress = ShellMath.PullProgress(_pull.Offset, _panelHeight);
        ApplyPanelFrame(progress, ShellMath.PullContentOpacity(progress));
    }

    private void OnPillUp()
    {
        switch (_pull.Up(Now()))
        {
            case PullOutcome.Click when _phase == PanelPhase.Closed:
                ShowPanel();
                break;
            case PullOutcome.Open when _phase == PanelPhase.Pulling:
                SettleOpen();
                break;
            case PullOutcome.Cancel when _phase == PanelPhase.Pulling:
                SettleCancel();
                break;
        }
    }

    private void OnPillLost()
    {
        if (_pull.Cancel() == PullOutcome.Cancel && _phase == PanelPhase.Pulling) SettleCancel();
    }

    /// Começou a puxar: o painel aparece já sob o ponteiro, sem ativar, cobrindo a pílula.
    private void BeginReveal()
    {
        if (_phase != PanelPhase.Closed) return;
        var panel = _panel!;
        PreparePanel();
        panel.ShowError(null);
        panel.ResetInputs();
        _startWidth = _pill!.DesiredPillWidth;
        _pillMotion.Stop();
        _pill.ContentOpacity = 0;
        ApplyPanelFrame(0, 0);
        panel.ShowActivated = false;
        panel.Show();
        panel.ShowActivated = true;
        ShellNative.BringToTop(new WindowInteropHelper(panel).Handle);
        _phase = PanelPhase.Pulling;
    }

    /// Soltou além do limiar (ou com velocidade): completa do ponto atual e só agora ativa.
    private void SettleOpen()
    {
        var panel = _panel!;
        HidePillNow();
        _phase = PanelPhase.Opening;
        panel.Activate();
        panel.FocusInitial(ShellMath.FocusFor(panel.Mode ?? PanelMode.Start));
        AnimatePanel(1, ShellMath.SettleDuration(_progress, 1, Motion.Normal), Motion.Decelerate, fadeIn: true, OnOpened);
    }

    /// Soltou antes: volta para a pílula.
    private void SettleCancel()
    {
        HidePillNow();   // o painel ainda cobre a pílula inteira: some sem se ver
        _phase = PanelPhase.Closing;
        AnimatePanel(0, ShellMath.SettleDuration(_progress, 0, ShellMath.CloseDuration), Motion.Accelerate, fadeIn: false, OnClosed);
    }

    // ------------------------------------------------------------------ painel: movimento

    private void AnimatePanel(double to, TimeSpan duration, (double X1, double Y1, double X2, double Y2) curve, bool fadeIn, Action done)
    {
        var from = _progress;
        var opacityFrom = _contentOpacity;
        var scaled = Motion.Scaled(duration, SystemParameters.ClientAreaAnimation);
        _panelMotion.Run(scaled, curve, (t, e) =>
        {
            var opacity = fadeIn
                ? opacityFrom + (1 - opacityFrom) * ShellMath.FadeInTail(t, scaled, Motion.Short)
                : opacityFrom * ShellMath.FadeOutHead(t, scaled, Motion.Fast);
            ApplyPanelFrame(from + (to - from) * e, opacity);
        }, done);
    }

    private void ApplyPanelFrame(double progress, double opacity)
    {
        _progress = Math.Clamp(progress, 0, 1);
        _contentOpacity = Math.Clamp(opacity, 0, 1);
        var clip = ShellMath.ClipAt(_progress, _panelHeight, _startWidth);
        _panel!.ApplyClip(clip, _contentOpacity);
        Place(_panel, clip.Width, clip.Height, 0);
    }

    private void OnOpened()
    {
        _phase = PanelPhase.Open;
        _foregroundAtOpen = ShellNative.ForegroundWindow();
        _foreignTicks = 0;
        _focusWatch.Start();
    }

    private void OnClosed()
    {
        _panel!.Hide();
        _phase = PanelPhase.Closed;
        _progress = 0;
        _contentOpacity = 0;
        _quickAppear = true;
        try { Refresh(); }
        finally { _quickAppear = false; }
    }

    private void OnPanelDismissed()
    {
        // Comando em andamento: o painel espera o resultado (igual ao popover velho).
        if (_busy || _phase is not (PanelPhase.Opening or PanelPhase.Open)) return;
        _dismissedAt = Now();
        HidePanel();
    }

    /// Foco negado pelo Windows: fecha quando outra janela assume o primeiro plano (2 leituras).
    private void OnFocusWatch()
    {
        var panel = _panel;
        if (panel is null || _phase != PanelPhase.Open)
        {
            _focusWatch.Stop();
            return;
        }
        var foreground = ShellNative.ForegroundWindow();
        if (panel.IsActive || _busy || foreground == IntPtr.Zero
            || foreground == _foregroundAtOpen || foreground == new WindowInteropHelper(panel).Handle)
        {
            _foreignTicks = 0;
            return;
        }
        if (++_foreignTicks < 2) return;
        _dismissedAt = Now();
        HidePanel();
    }

    // ------------------------------------------------------------------ auto-recolher

    private void ArmAutoCollapse()
    {
        _autoArmed = true;
        _interacted = false;
        _autoArmedAt = Now();
        _autoCollapse.Stop();
        _autoCollapse.Start();
    }

    private void StopAutoCollapse()
    {
        _autoArmed = false;
        _autoCollapse.Stop();
    }

    private void OnAutoCollapse()
    {
        _autoCollapse.Stop();
        var armed = _autoArmed;
        _autoArmed = false;
        var hasSession = Snapshot()?.Session is not null;
        var elapsed = TimeSpan.FromMilliseconds(Now() - _autoArmedAt);
        if (ShellMath.ShouldAutoCollapse(armed, elapsed, _interacted, hasSession, _phase == PanelPhase.Open)) HidePanel();
    }

    // ------------------------------------------------------------------ comandos

    private void OnChip(PanelChip chip)
    {
        if (_panel?.Mode == PanelMode.Session)
        {
            Extend(chip.Duration);
            return;
        }
        _selectedPreset = chip.Duration;
        Refresh();
    }

    private async void OnStart(string text)
    {
        if (_busy || _disposed) return;
        var config = _host.Config.Current;
        TimeSpan duration;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Enter no campo vazio (ou Iniciar sem texto): o chip selecionado, ou o padrão.
            duration = _selectedPreset ?? TimeSpan.FromSeconds(config.DefaultPreset);
            if (duration <= TimeSpan.Zero) { _panel?.ShowError(TrayTexts.InvalidDuration); return; }
        }
        else if (DurationParser.Parse(text) is { } parsed)
        {
            duration = parsed;
        }
        else
        {
            _panel?.ShowError(TrayTexts.InvalidDuration);
            return;
        }

        var problem = await RunAsync(() => _host.StartSessionAsync(duration));
        if (_disposed || problem is not null) return;
        _panel?.ResetInputs();
        _selectedPreset = null;
        if (ShellMath.ArmsAutoCollapse(problem)) ArmAutoCollapse();
    }

    private void OnExtendText(string text)
    {
        if (_busy || _disposed) return;
        if (DurationParser.Parse(text) is { } by) Extend(by);
        else _panel?.ShowError(TrayTexts.InvalidDuration);
    }

    private async void Extend(TimeSpan by)
    {
        if (_busy || _disposed) return;
        var problem = await RunAsync(() => _host.ExtendSessionAsync(by));
        if (_disposed || problem is not null) return;
        _panel?.ResetInputs();
    }

    /// Segurou até o fim: encerra, mostra "Sessão encerrada" e recolhe (a pílula não volta).
    /// Se o fim não pegou (erro, ou a sessão continua de pé), o botão é rearmado.
    private async void OnEnd()
    {
        if (_disposed) return;
        if (_busy)
        {
            // Outro comando em curso: o segurar não vale, mas o botão não pode ficar travado.
            _panel?.RearmEnd();
            return;
        }
        var problem = await RunAsync(async () =>
        {
            await _host.EndSessionAsync();
            return null;
        }, refresh: false);
        if (_disposed) return;
        var hasSession = Snapshot()?.Session is not null;
        if (!ShellMath.EndSucceeded(problem, hasSession))
        {
            _panel?.RearmEnd();
            Refresh();
            return;
        }
        _panel?.ShowEnded();
        HidePanel();
    }

    private void OpenSettings()
    {
        HidePanel();
        try { _settings.ShowSettings(); }
        catch (Exception ex) { _host.Log.Error($"painel: Configurações: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void OnNoticeAction(PanelNotice notice)
    {
        switch (notice.Command)
        {
            case MenuCommand.Relaunch when notice.Argument is { Length: > 0 } kind:
                // A confirmação é um MessageBox: o painel sai da frente antes.
                HidePanel();
                Fire(() => _host.RelaunchBrowserAsync(kind), "relançar navegador");
                break;
            case MenuCommand.StopWaitingAudio when notice.Argument is { Length: > 0 } deviceId:
                Fire(() => _host.StopWaitingAudioAsync(deviceId), "parar de esperar a saída de áudio");
                break;
        }
    }

    /// Executa um comando de sessão: um por vez, erro vira InfoBar Critical (some no próximo acerto).
    private async Task<string?> RunAsync(Func<Task<string?>> action, bool refresh = true)
    {
        _busy = true;
        _panel?.SetBusy(true);
        string? problem;
        try
        {
            problem = await action();
        }
        catch (Exception ex)
        {
            _host.Log.Error($"painel: {ex.GetType().Name}: {ex.Message}");
            problem = $"Falhou: {ex.Message}";
        }
        finally
        {
            _busy = false;
            if (!_disposed) _panel?.SetBusy(false);
        }
        if (_disposed) return problem;
        _panel?.ShowError(problem);
        if (refresh) Refresh();
        return problem;
    }

    private async void Fire(Func<Task> action, string what)
    {
        try { await action(); }
        catch (Exception ex) { _host.Log.Error($"painel: {what}: {ex.GetType().Name}: {ex.Message}"); }
        if (!_disposed) Refresh();
    }

    // ------------------------------------------------------------------ posição

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _dispatcher.InvokeAsync(Reposition);
    }

    /// Monitor principal, resolução ou DPI mudaram: recoloca o que está parado na tela.
    private void Reposition()
    {
        if (_disposed || _pill is null) return;
        if (_pillShown && !_pillMotion.IsRunning) PlacePill(_pill.DesiredPillWidth, 0);
        if (_phase == PanelPhase.Open && !_panelMotion.IsRunning) ApplyPanelFrame(1, 1);
    }

    /// Centrado no monitor principal, 8 epx abaixo do topo (ou do work area, com a barra no topo).
    private static void Place(FlyoutWindow window, double widthEpx, double heightEpx, double yOffsetEpx)
    {
        var (bounds, work, scale) = PrimaryMonitor();
        var rect = UiPlacement.TopCenter(bounds, work,
            Math.Round(widthEpx * scale), Math.Round(heightEpx * scale), (ShellMath.TopMargin + yOffsetEpx) * scale);
        window.MoveTo(rect);
    }

    /// Bounds e work area em pixels físicos (o manifesto é PerMonitorV2) e a escala do monitor.
    private static (PxRect Bounds, PxRect Work, double Scale) PrimaryMonitor()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            var fallback = new PxRect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            return (fallback, fallback, 1);
        }
        var b = screen.Bounds;
        var w = screen.WorkingArea;
        var scale = Dwm.ScaleForPoint(b.X + b.Width / 2, b.Y + b.Height / 2);
        return (new PxRect(b.X, b.Y, b.Width, b.Height), new PxRect(w.X, w.Y, w.Width, w.Height), scale);
    }
}
