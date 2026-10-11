using System.Net.NetworkInformation;
using Insomnia.Core.Abstractions;
using ManagedNativeWifi;

namespace Insomnia.Windows.Wifi;

/// IWifi sobre o WLAN do Windows via ManagedNativeWifi (desenho §7, linha do hotspot).
///
/// Conectividade: "alguma interface Wi-Fi conectada" (WlanEnumInterfaces). Reavaliada a cada
/// notificação WLAN (ConnectionChanged/InterfaceChanged), a cada NetworkAvailabilityChanged e
/// por sondagem de PollInterval (notificação perdida não deixa a queda sem detecção). Só a
/// mudança vira ConnectivityChanged; o evento sai de thread qualquer.
///
/// Hotspot: o app NÃO guarda senha. O usuário conecta ao hotspot uma vez pelo Windows, que
/// salva o perfil; aqui só se faz WlanConnect pelo nome do perfil (= SSID).
///
/// Construtor público: WifiWin(ILog log). Nada toca o sistema até Start/IsConnected/HasProfile/ConnectAsync.
public sealed class WifiWin : IWifi, IDisposable
{
    /// Teto do WlanConnect (R11).
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ILog _log;
    private readonly object _gate = new();
    private ConnectivityEdge? _edge;
    private NativeWifiPlayer? _player;
    private System.Threading.Timer? _poll;
    private bool _readErrorLogged;

    public WifiWin(ILog log) => _log = log;

    public event Action<bool>? ConnectivityChanged;

    public bool IsConnected => ReadConnected();

    public bool HasProfile(string ssid) => WifiProfiles.Resolve(NativeWifi.EnumerateProfileNames(), ssid) is not null;

    public async Task<bool> ConnectAsync(string profileName, CancellationToken ct)
    {
        var packs = NativeWifi.EnumerateProfiles().ToList();
        var name = WifiProfiles.Resolve(packs.Select(p => p.Name), profileName);
        if (name is null)
        {
            _log.Info("hotspot: perfil não encontrado na hora de conectar");
            return false;
        }
        var pack = packs.First(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        return await NativeWifi.ConnectNetworkAsync(pack.InterfaceInfo.Id, pack.Name, pack.Document.BssType, ConnectTimeout, ct)
            .ConfigureAwait(false);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_edge is not null) return;
            _edge = new ConnectivityEdge(ReadConnected());
            try
            {
                _player = new NativeWifiPlayer();
                _player.ConnectionChanged += OnWlanEvent;
                _player.InterfaceChanged += OnWlanEvent;
            }
            catch (Exception ex)
            {
                // Sem serviço WLAN (máquina sem Wi-Fi): segue só com NetworkChange e sondagem.
                _log.Error($"Wi-Fi: notificações WLAN indisponíveis: {ex.Message}");
                _player = null;
            }
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _poll = new System.Threading.Timer(_ => Evaluate(), null, PollInterval, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_edge is null) return;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _poll?.Dispose();
            _poll = null;
            if (_player is not null)
            {
                _player.ConnectionChanged -= OnWlanEvent;
                _player.InterfaceChanged -= OnWlanEvent;
                try { _player.Dispose(); } catch (Exception ex) { _log.Error($"Wi-Fi: fechar notificações falhou: {ex.Message}"); }
                _player = null;
            }
            _edge = null;
        }
    }

    public void Dispose() => Stop();

    private void OnWlanEvent(object? sender, EventArgs e) => Evaluate();
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Evaluate();

    private void Evaluate()
    {
        ConnectivityEdge? edge;
        lock (_gate) edge = _edge;
        if (edge is null) return;
        if (edge.Update(ReadConnected()) is { } changed)
        {
            try { ConnectivityChanged?.Invoke(changed); }
            catch (Exception ex) { _log.Error($"Wi-Fi: assinante falhou: {ex.Message}"); }
        }
    }

    private bool ReadConnected()
    {
        try
        {
            var connected = NativeWifi.EnumerateInterfaceConnections().Any(i => i.IsConnected);
            _readErrorLogged = false;
            return connected;
        }
        catch (Exception ex)
        {
            if (!_readErrorLogged) _log.Error($"Wi-Fi: estado ilegível (tratado como desconectado): {ex.Message}");
            _readErrorLogged = true;
            return false;
        }
    }
}
