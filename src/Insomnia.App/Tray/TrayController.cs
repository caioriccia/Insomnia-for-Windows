// Bandeja (desenho-ui §3.6, §3.8 e §4): NotifyIcon do WinForms — imbatível para isso — sem
// ContextMenuStrip. Clique esquerdo abre/fecha o painel (IShell); clique direito abre o menu
// próprio (MenuFlyoutWindow, Acrylic) no monitor do cursor, montado pelo StatusMenuBuilder a
// partir do último retrato. Ao abrir, dispara uma leitura nova (navegadores, Wi-Fi, diário) e
// se redesenha quando o Changed dela chega — como o original, pode estar uma abertura atrasado.
//
// Ícone: glifo monocromático pelo tom da pílula (TrayIconStates), redesenhado no Changed e no
// Tick do AppHost (o relógio de 1 Hz mora lá; Tick cala com a contagem pausada e Changed
// cobre a virada para pausa). Tooltip com a contagem (TrayTexts.Tooltip).
//
// Notificações: o TrayNotifier continua mostrando balões por este ícone, pelo Control oculto
// criado aqui (o handle dele é o invoker de qualquer thread). O notificador é do AppHost e não
// está no ISessionFacade: em produção o host é o AppHost; fora dele (testes), só o log.
//
// Tudo aqui roda no thread da UI.
using Insomnia.App.Composition;
using Insomnia.App.Ui;
using Insomnia.App.Ui.Menu;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;

namespace Insomnia.App.Tray;

public sealed class TrayController : IDisposable
{
    // NotifyIcon.Text aceita no máximo 127 caracteres (passar disso lança).
    private const int TooltipMax = 127;
    private const string StartFailedTitle = "Sessão não iniciada";

    private readonly ISessionFacade _host;
    private readonly IShell _shell;
    private readonly ISettingsHost _settings;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.Control _invoker = new();
    private readonly TrayIconRenderer _renderer = new();
    private MenuFlyoutWindow? _menu;
    private bool _disposed;

    public TrayController(ISessionFacade host, IShell shell, ISettingsHost settings)
    {
        _host = host;
        _shell = shell;
        _settings = settings;
        _ = _invoker.Handle; // invoker das notificações (BeginInvoke de qualquer thread)
        _icon = new System.Windows.Forms.NotifyIcon { Text = TrayTexts.IdleTooltip, Visible = false };
        _icon.MouseClick += OnIconClick;
        _host.Changed += OnChanged;
        _host.Tick += OnTick;
    }

    public void Show()
    {
        if (_disposed) return;
        UpdateIcon();
        _icon.Visible = true;
        if (_host is AppHost app) app.Notifier.Attach(_icon, _invoker);
    }

    private void OnIconClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (_disposed) return;
        switch (e.Button)
        {
            case System.Windows.Forms.MouseButtons.Left:
                if (_menu is { IsVisible: true } menu) menu.Hide();
                _shell.TogglePanel();
                break;
            case System.Windows.Forms.MouseButtons.Right:
                ShowMenu();
                break;
        }
    }

    // ------------------------------------------------------------------ redesenho

    private void OnChanged() => Redraw();

    private void OnTick() => Redraw();

    private void Redraw()
    {
        if (_disposed) return;
        UpdateIcon();
        if (_menu is { IsVisible: true } menu)
        {
            try { menu.SetRows(BuildRows()); }
            catch (Exception ex) { _host.Log.Error($"menu da bandeja não redesenhado: {ex.Message}"); }
        }
    }

    private void UpdateIcon()
    {
        try
        {
            var s = _host.Snapshot();
            var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
            var icon = _renderer.Update(TrayIconStates.Of(s), size, Dwm.SystemUsesLightTaskbar);
            if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
            var tooltip = TrayTexts.Tooltip(s.Session, s.Now, s.CountdownPaused);
            _icon.Text = tooltip.Length <= TooltipMax ? tooltip : tooltip[..TooltipMax];
        }
        catch (Exception ex)
        {
            _host.Log.Error($"ícone da bandeja não redesenhado: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ menu

    private IReadOnlyList<MenuRow> BuildRows() =>
        MenuPresenter.From(StatusMenuBuilder.Build(_host.Snapshot(), _host.Config.Current));

    private void ShowMenu()
    {
        try
        {
            if (_menu is null)
            {
                _menu = new MenuFlyoutWindow();
                _menu.CommandInvoked += Execute;
            }
            var cursor = System.Windows.Forms.Cursor.Position;   // pixels físicos (PerMonitorV2)
            _menu.ShowAt(BuildRows(), cursor.X, cursor.Y);
        }
        catch (Exception ex)
        {
            _host.Log.Error($"menu da bandeja não aberto: {ex.Message}");
        }
        _host.RefreshStatusInBackground();
    }

    private void Execute(MenuRow row)
    {
        if (_disposed) return;
        switch (row.Command)
        {
            case MenuCommand.OpenPanel:
                _shell.ShowPanel();
                break;
            case MenuCommand.StartDefault:
                _ = StartDefaultAsync(row.Argument);
                break;
            case MenuCommand.Settings:
                _settings.ShowSettings();
                break;
            case MenuCommand.Quit:
                _ = _host.QuitAsync();
                break;
            case MenuCommand.Relaunch when row.Argument is { } kind:
                _ = _host.RelaunchBrowserAsync(kind);
                break;
            case MenuCommand.StopWaitingAudio when row.Argument is { } deviceId:
                _ = _host.StopWaitingAudioAsync(deviceId);
                break;
        }
    }

    /// "Iniciar sessão de {padrão}": recusa (mensagem pt-BR) ou falha vira notificação.
    private async Task StartDefaultAsync(string? argument)
    {
        string? problem;
        try
        {
            var duration = MenuLayout.StartDuration(argument, _host.Config.Current.DefaultPreset);
            problem = await _host.StartSessionAsync(duration);
        }
        catch (Exception ex)
        {
            _host.Log.Error($"sessão do menu não iniciada: {ex.Message}");
            problem = ex.Message;
        }
        if (problem is null) return;
        if (_host is AppHost app) app.Notifier.Post(StartFailedTitle, problem);
        else _host.Log.Info($"notificação: {StartFailedTitle} — {problem}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Changed -= OnChanged;
        _host.Tick -= OnTick;
        if (_host is AppHost app) app.Notifier.Detach();
        _menu?.Hide();
        _icon.Visible = false;
        _icon.Dispose();
        _invoker.Dispose();
        _renderer.Dispose();
    }
}
