// Casca das Configurações (desenho-ui §5.2, ISettingsHost). Cria o SettingsModel e a
// SettingsWindow uma vez, na primeira vez que alguém pede (bandeja, engrenagem do painel);
// ShowSettings depois disso só traz a janela para a frente, e fechar a janela só a esconde.
// Thread da UI: o construtor roda no thread do AppHost, e é o Dispatcher dele que leva de volta
// as leituras lentas do modelo (processos, perfis Wi-Fi).
using System.Windows.Threading;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings;

public sealed class SettingsPresenter : ISettingsHost
{
    private readonly ISessionFacade _host;
    private readonly Dispatcher _dispatcher;
    private SettingsModel? _model;
    private SettingsWindow? _window;
    private bool _disposed;

    public SettingsPresenter(ISessionFacade host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// Cria se preciso, traz para a frente. Uma falha ao montar a janela vai para o log e não
    /// derruba quem pediu (o menu da bandeja continua de pé).
    public void ShowSettings()
    {
        if (_disposed) return;
        try
        {
            if (_window is null)
            {
                _model ??= new SettingsModel(_host.Config, _host.AutoStart, _host.Wifi, _host.Inspector,
                    _host.ConfigPath, _host.ExecutablePath, _host.IsSimulationBuild, _host.Log, Post);
                _window = new SettingsWindow(_model);
            }
            _window.ShowAndActivate();
        }
        catch (Exception ex)
        {
            _host.Log.Error($"configurações: não deu para abrir a janela: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _window?.CloseForGood(); }
        catch (Exception ex) { _host.Log.Error($"configurações: não deu para fechar a janela: {ex.Message}"); }
        _window = null;
        _model?.Dispose();
        _model = null;
    }

    private void Post(Action action)
    {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action);
    }
}
