// INotifier real: balão do NotifyIcon, que o Windows 11 mostra como toast (desenho §7).
// Toda notificação também vai para o log. Antes de a bandeja existir (ou depois que sumiu),
// fica só o log. Pode ser chamado de qualquer thread: o balão é mostrado no thread da UI.
using Insomnia.Core.Abstractions;

namespace Insomnia.App.Notifications;

public sealed class TrayNotifier(ILog log) : INotifier
{
    public const int BalloonTimeoutMs = 10_000;

    private NotifyIcon? _icon;
    private Control? _invoker;

    /// Liga ao ícone da bandeja; invoker = controle com handle criado no thread da UI.
    public void Attach(NotifyIcon icon, Control invoker)
    {
        _icon = icon;
        _invoker = invoker;
    }

    public void Detach()
    {
        _icon = null;
        _invoker = null;
    }

    public void Post(string title, string body)
    {
        log.Info($"notificação: {title} — {body}");
        var icon = _icon;
        var invoker = _invoker;
        if (icon is null || invoker is null || invoker.IsDisposed || !invoker.IsHandleCreated) return;
        try
        {
            invoker.BeginInvoke(() =>
            {
                try
                {
                    if (icon.Visible) icon.ShowBalloonTip(BalloonTimeoutMs, title, body, ToolTipIcon.None);
                }
                catch (Exception ex) { log.Error($"notificação não exibida: {ex.Message}"); }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            log.Error($"notificação não exibida: {ex.Message}");
        }
    }
}
