using System.Text;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Network;

/// Motorista da FailoverMachine (desenho §7.3): liga os eventos do Wi-Fi e um timer ÚNICO à
/// máquina pura e executa o que ela pede.
///
/// Ativo só entre Start e Stop, que a UI chama no início e no fim da sessão; fora disso
/// ignora tudo. Ao entrar no hotspot: SSID vazio ⇒ log (uma vez por queda) e segue
/// retentando; perfil ausente ⇒ Warning + notificação uma vez por queda e por SSID; senão
/// IWifi.ConnectAsync, com teto de JoinTimeout. Volta do Wi-Fi ⇒ linha em handoffs.log,
/// LastGap e, se a queda passou de 90 s, notificação "Rede voltou".
///
/// Thread-safe: eventos do Wi-Fi e do timer chegam de threads quaisquer e são serializados por
/// um lock; Changed e notificações saem fora do lock. Cada Start abre uma "geração": timer ou
/// tentativa de uma geração anterior que termine depois do Stop é descartado.
public sealed class NetworkFailover
{
    public const string ProfileMissingWarning = "Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows";
    public const string ProfileMissingTitle = "Hotspot não conectado";
    public const string RecoveredTitle = "Rede voltou";
    /// Teto de cada tentativa de conexão (R11: tudo com limite).
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);
    public const long HandoffsMaxBytes = 1L << 20;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IWifi _wifi;
    private readonly IScheduler _scheduler;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly INotifier _notifier;
    private readonly IConfigSource _config;
    private readonly string _handoffsLogPath;

    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly HashSet<string> _notifiedThisOutage = new(StringComparer.OrdinalIgnoreCase);
    private FailoverMachine _machine = new();
    private bool _running;
    private long _generation;
    private long _timerSeq;
    private IDisposable? _timer;
    private CancellationTokenSource? _joinCts;
    private bool _skipLoggedThisOutage;
    private string _lastSsid = "";
    private TimeSpan? _lastGap;
    private string? _warning;

    public NetworkFailover(IWifi wifi, IScheduler scheduler, IClock clock, ILog log, INotifier notifier, IConfigSource config, string handoffsLogPath)
    {
        _wifi = wifi;
        _scheduler = scheduler;
        _clock = clock;
        _log = log;
        _notifier = notifier;
        _config = config;
        _handoffsLogPath = handoffsLogPath;
    }

    public string HandoffsLogPath => _handoffsLogPath;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool InOutage { get { lock (_gate) return _running && _machine.InOutage; } }

    /// Duração da última queda recuperada desde o Start (linha "última queda Ns" do menu).
    public TimeSpan? LastGap { get { lock (_gate) return _lastGap; } }

    /// Aviso para o menu (ex.: perfil do hotspot ausente); null = nada a avisar.
    public string? Warning { get { lock (_gate) return _warning; } }

    /// Estado visível mudou (LastGap, Warning, início/fim de queda, Start/Stop).
    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _generation++;
            _machine = new FailoverMachine();
            _lastGap = null;
            _warning = null;
            _notifiedThisOutage.Clear();
            _skipLoggedThisOutage = false;
            _lastSsid = CurrentSsid();
        }
        // Assina antes de ligar o Wi-Fi: nenhum evento escapa entre os dois.
        _wifi.ConnectivityChanged += OnConnectivityChanged;
        _config.Changed += OnConfigChanged;
        try { _wifi.Start(); }
        catch (Exception ex) { _log.Error($"failover de rede: monitor de Wi-Fi não iniciou: {ex.Message}"); }
        _log.Info("failover de rede iniciado");
        RaiseChanged();

        bool connected;
        try { connected = _wifi.IsConnected; }
        catch (Exception ex)
        {
            _log.Error($"failover de rede: estado do Wi-Fi ilegível: {ex.Message}");
            return;
        }
        // Como o NWPathMonitor do original: o estado inicial conta. Começar já sem Wi-Fi é queda.
        if (!connected) OnConnectivityChanged(false);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _generation++;
            _timer?.Dispose();
            _timer = null;
            _joinCts?.Cancel();
            _joinCts = null;
            _machine = new FailoverMachine();
            _warning = null;
        }
        _wifi.ConnectivityChanged -= OnConnectivityChanged;
        _config.Changed -= OnConfigChanged;
        try { _wifi.Stop(); }
        catch (Exception ex) { _log.Error($"failover de rede: monitor de Wi-Fi não parou: {ex.Message}"); }
        _log.Info("failover de rede parado");
        RaiseChanged();
    }

    private void OnConnectivityChanged(bool connected)
    {
        IReadOnlyList<FailoverOutput> outs;
        long gen;
        lock (_gate)
        {
            if (!_running) return;
            gen = _generation;
            var now = _clock.Now;
            if (connected)
            {
                outs = _machine.PathUp(now);
                if (outs.Count > 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                    _joinCts?.Cancel();
                    _joinCts = null;
                }
            }
            else
            {
                outs = _machine.PathDown(now);
                if (outs.Count > 0)
                {
                    _notifiedThisOutage.Clear();
                    _skipLoggedThisOutage = false;
                }
            }
        }
        if (outs.Count == 0) return;
        if (!connected) _log.Info("Wi-Fi caiu");
        Execute(outs, gen);
        RaiseChanged();
    }

    private void OnTick(long gen, long timerId)
    {
        IReadOnlyList<FailoverOutput> outs;
        lock (_gate)
        {
            if (!_running || gen != _generation || timerId != _timerSeq) return;
            _timer = null;
            outs = _machine.Tick(_clock.Now);
        }
        Execute(outs, gen);
    }

    private void Execute(IReadOnlyList<FailoverOutput> outs, long gen)
    {
        foreach (var o in outs)
        {
            switch (o)
            {
                case FailoverOutput.ScheduleRetry r:
                    lock (_gate)
                    {
                        if (!_running || gen != _generation) return;
                        _timer?.Dispose();
                        var id = ++_timerSeq;
                        _timer = _scheduler.Schedule(r.Delay, () => OnTick(gen, id));
                    }
                    break;
                case FailoverOutput.JoinHotspot:
                    _ = JoinAsync(gen);
                    break;
                case FailoverOutput.Recovered rec:
                    OnRecovered(rec, gen);
                    break;
            }
        }
    }

    private void OnRecovered(FailoverOutput.Recovered rec, long gen)
    {
        var end = rec.Start + rec.Gap;
        lock (_gate)
        {
            if (gen != _generation) return;
            _lastGap = rec.Gap;
        }
        AppendHandoff(FailoverMachine.LogLine(rec.Start, end, rec.Gap));
        var human = FailoverMachine.HumanGap(rec.Gap);
        _log.Info($"Wi-Fi voltou depois de {human}");
        if (rec.Gap >= FailoverMachine.NotifyThreshold)
            Post(RecoveredTitle, $"A rede ficou fora por {human}. Confira os agentes.");
    }

    /// Uma tentativa de entrar no hotspot. Nunca lança.
    private async Task JoinAsync(long gen)
    {
        try
        {
            var ssid = CurrentSsid();
            if (ssid.Length == 0)
            {
                bool logIt;
                lock (_gate) { logIt = !_skipLoggedThisOutage; _skipLoggedThisOutage = true; }
                if (logIt) _log.Info("tentativa de hotspot pulada: nenhum SSID configurado");
                Finish(gen, ok: false);
                return;
            }

            bool hasProfile;
            try { hasProfile = _wifi.HasProfile(ssid); }
            catch (Exception ex)
            {
                _log.Error($"tentativa de hotspot pulada: perfis Wi-Fi ilegíveis: {ex.Message}");
                Finish(gen, ok: false);
                return;
            }

            if (!hasProfile)
            {
                bool notify, changed;
                lock (_gate)
                {
                    if (!_running || gen != _generation) return;
                    changed = _warning != ProfileMissingWarning;
                    _warning = ProfileMissingWarning;
                    notify = _notifiedThisOutage.Add(ssid);
                }
                // A linha nomeia o problema, não o SSID (como o original).
                _log.Info("tentativa de hotspot pulada: perfil não encontrado");
                if (notify) Post(ProfileMissingTitle, ProfileMissingWarning);
                if (changed) RaiseChanged();
                Finish(gen, ok: false);
                return;
            }

            CancellationTokenSource cts;
            int attempt;
            bool cleared;
            lock (_gate)
            {
                if (!_running || gen != _generation) return;
                cleared = _warning == ProfileMissingWarning;
                if (cleared) _warning = null;
                attempt = _machine.Joins;
                cts = new CancellationTokenSource(JoinTimeout);
                _joinCts = cts;
            }
            if (cleared) RaiseChanged();

            _log.Info($"entrando no hotspot {ssid} (tentativa {attempt})");
            bool ok;
            try
            {
                ok = await _wifi.ConnectAsync(ssid, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ok = false;
                _log.Info("tentativa de hotspot interrompida (Wi-Fi voltou, sessão acabou ou passou do teto)");
            }
            catch (Exception ex)
            {
                ok = false;
                _log.Error($"tentativa de hotspot falhou: {ex.Message}");
            }
            if (!ok) _log.Info("hotspot não conectou; tentando de novo com espera");

            lock (_gate)
            {
                if (_joinCts == cts) _joinCts = null;
            }
            cts.Dispose();
            Finish(gen, ok);

            // O evento de conectividade deve chegar sozinho; se o Windows já diz conectado, não espera.
            if (ok && SafeIsConnected()) OnConnectivityChanged(true);
        }
        catch (Exception ex)
        {
            _log.Error($"tentativa de hotspot: erro inesperado: {ex.Message}");
            Finish(gen, ok: false);
        }
    }

    private void Finish(long gen, bool ok)
    {
        lock (_gate)
        {
            if (_running && gen == _generation) _machine.JoinFinished(ok);
        }
    }

    private void OnConfigChanged()
    {
        bool changed;
        lock (_gate)
        {
            if (!_running) return;
            var ssid = CurrentSsid();
            if (string.Equals(ssid, _lastSsid, StringComparison.Ordinal)) return;
            _lastSsid = ssid;
            // SSID novo: o aviso era do anterior, e o novo merece a própria notificação.
            changed = _warning is not null;
            _warning = null;
            _skipLoggedThisOutage = false;
        }
        if (changed) RaiseChanged();
    }

    private string CurrentSsid()
    {
        try { return (_config.Current.HotspotSsid ?? "").Trim(); }
        catch (Exception ex)
        {
            _log.Error($"failover de rede: config ilegível: {ex.Message}");
            return "";
        }
    }

    private bool SafeIsConnected()
    {
        try { return _wifi.IsConnected; }
        catch { return false; }
    }

    private void AppendHandoff(string line)
    {
        lock (_fileGate)
        {
            try
            {
                var full = Path.GetFullPath(_handoffsLogPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var info = new FileInfo(full);
                if (info.Exists && info.Length > HandoffsMaxBytes) File.Move(full, full + ".1", overwrite: true);
                using var fs = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Utf8NoBom.GetBytes(line + Environment.NewLine);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _log.Error($"handoffs.log não gravado: {ex.Message}");
            }
        }
    }

    private void Post(string title, string body)
    {
        try { _notifier.Post(title, body); }
        catch (Exception ex) { _log.Error($"notificação falhou: {ex.Message}"); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.Error($"failover de rede: assinante de Changed falhou: {ex.Message}"); }
    }
}
