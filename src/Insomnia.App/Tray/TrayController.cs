// Bandeja (desenho §9): NotifyIcon com ícone redesenhado a cada segundo (alinhado à virada
// do segundo, parado enquanto a contagem está pausada), tooltip com a contagem, popover no
// clique esquerdo e menu de contexto no direito. O menu é montado pelo StatusMenuBuilder a
// partir do último retrato; ao abrir, dispara uma leitura nova (navegadores, Wi-Fi, diário)
// e se redesenha quando ela volta — como o original, pode estar uma abertura atrasado.
//
// Tudo aqui roda no thread da UI.
using Insomnia.App.Composition;
using Insomnia.App.Settings;
using Insomnia.Core.Time;

namespace Insomnia.App.Tray;

public sealed class TrayController : IDisposable
{
    private static readonly Color WarningColor = Color.FromArgb(0xC8, 0x6A, 0x00);

    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private readonly TrayIconRenderer _renderer = new();
    private readonly System.Windows.Forms.Timer _tick = new();
    private PopoverForm? _popover;
    private SettingsForm? _settings;
    private bool _disposed;

    public TrayController(AppHost host)
    {
        _host = host;
        _ = _menu.Handle; // invoker das notificações (BeginInvoke de qualquer thread)
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Text = TrayTexts.IdleTooltip, Visible = false };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopover(); };
        _menu.Opening += (_, _) =>
        {
            RebuildMenu();
            _host.RefreshStatusInBackground();
        };
        _tick.Tick += (_, _) => OnTick();
    }

    public void Show()
    {
        UpdateIcon(force: true);
        _icon.Visible = true;
        _host.Notifier.Attach(_icon, _menu);
        ArmTick();
    }

    /// Algo mudou (sessão, config, rede, leitura de estado): redesenha o que estiver visível.
    public void Refresh()
    {
        if (_disposed) return;
        UpdateIcon(force: true);
        if (_menu.Visible) RebuildMenu();
        if (_popover is { Visible: true } p) p.RefreshState(_host.Snapshot());
    }

    /// Abre o popover (também usado quando uma segunda instância é aberta).
    public void ShowPopover()
    {
        if (_disposed) return;
        _popover ??= new PopoverForm(_host);
        _popover.RefreshState(_host.Snapshot());
        _popover.ShowNear(Cursor.Position);
    }

    public void ShowSettings()
    {
        if (_disposed) return;
        _popover?.Hide();
        if (_settings is null || _settings.IsDisposed)
            _settings = new SettingsForm(_host.Config, _host.AutoStart, _host.Wifi, _host.Inspector,
                _host.Paths.ConfigFile, AppHost.ExecutablePath, _host.Log);
        _settings.ShowAndActivate();
    }

    private void TogglePopover()
    {
        if (_popover is { Visible: true } p) { p.Hide(); return; }
        ShowPopover();
    }

    // ------------------------------------------------------------------ ícone

    private void ArmTick()
    {
        // Próxima virada de segundo (+5 ms de folga), para o texto mudar junto com o relógio.
        var ms = 1000 - DateTime.Now.Millisecond + 5;
        _tick.Interval = Math.Clamp(ms, 50, 1005);
        _tick.Start();
    }

    private void OnTick()
    {
        _tick.Stop();
        if (_disposed) return;
        UpdateIcon(force: false);
        if (_popover is { Visible: true } p) p.Tick(_host.Snapshot());
        ArmTick();
    }

    private void UpdateIcon(bool force)
    {
        var manager = _host.Manager;
        var session = manager.Current;
        var paused = manager.CountdownPaused;
        if (!force && paused) return;   // tampa fechada: nada a redesenhar a cada segundo
        var now = _host.Clock.Now;
        var compact = session is null ? null : SessionMath.FormatCompact(session.Remaining(now));
        try
        {
            var size = SystemInformation.SmallIconSize.Width;
            var icon = _renderer.Update(compact, paused, size);
            if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
            _icon.Text = TrayTexts.Tooltip(session, now, paused);
        }
        catch (Exception ex)
        {
            _host.Log.Error($"ícone da bandeja não redesenhado: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ menu

    private void RebuildMenu()
    {
        _menu.SuspendLayout();
        try
        {
            foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
            _menu.Items.Clear();
            foreach (var line in StatusMenuBuilder.Build(_host.Snapshot()))
                _menu.Items.Add(ToItem(line));
        }
        finally { _menu.ResumeLayout(); }
    }

    private ToolStripItem ToItem(MenuLine line)
    {
        switch (line.Kind)
        {
            case MenuLineKind.Separator:
                return new ToolStripSeparator();
            case MenuLineKind.Info:
                return new ToolStripMenuItem(line.Text) { Enabled = false };
            case MenuLineKind.Warning:
                return new ToolStripMenuItem(line.Text) { ForeColor = WarningColor };
            default:
                var item = new ToolStripMenuItem(line.Text);
                item.Click += (_, _) => Execute(line);
                return item;
        }
    }

    private void Execute(MenuLine line)
    {
        switch (line.Command)
        {
            case MenuCommand.Settings:
                ShowSettings();
                break;
            case MenuCommand.Quit:
                _ = _host.QuitAsync();
                break;
            case MenuCommand.Relaunch when line.Argument is { } kind:
                _ = _host.RelaunchBrowserAsync(kind);
                break;
            case MenuCommand.StopWaitingAudio when line.Argument is { } deviceId:
                _ = _host.StopWaitingAudioAsync(deviceId);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _tick.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _popover?.Dispose();
        _settings?.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
    }
}
