// Raiz de composição da UI (desenho §9, desenho-ui §5.1): cria os adaptadores reais e o
// núcleo, liga os eventos e é a ISessionFacade que a UI consome. O laço de mensagens é o do
// WPF (Program.RunUi): quando o app deve fechar, o AppHost dispara Exited.
//
// THREAD: construir e chamar Start() no thread da UI — a PowerEventsWindow é uma janela
// nativa e só recebe mensagens no thread que a cria. Os eventos do núcleo (Changed) chegam
// de qualquer thread e são trazidos para a UI por Post (Dispatcher). Chamadas ao núcleo que
// fazem I/O (início, fim, extensão, WMI, Wi-Fi) rodam fora da UI com Task.Run.
//
// Ciclo: Start liga energia/tampa/pisos, a bandeja e a casca; ~1 s depois (já com o estado
// inicial da tampa entregue pela janela) faz o Reconcile. O failover de rede liga e desliga
// com a sessão. Tick: virada de segundo (+5 ms), calada enquanto a contagem está pausada.
// Sair = EndAsync(Quit); só sai se LetsQuitGo — senão fica e registra "saída adiada". A
// retentativa do núcleo (30 s) que conclui o Quit dispara QuitReady e o app fecha sozinho.
using System.Windows.Threading;
using Insomnia.App.Notifications;
using Insomnia.App.Tray;
using Insomnia.App.Ui;
using Insomnia.App.Ui.Settings;
using Insomnia.App.Ui.Shell;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Network;
using Insomnia.Core.Power;
using Insomnia.Core.Session;
using Insomnia.Core.Store;
using Insomnia.Core.Time;
using Insomnia.Windows.Audio;
using Insomnia.Windows.Browser;
using Insomnia.Windows.Display;
using Insomnia.Windows.Docker;
using Insomnia.Windows.Power;
using Insomnia.Windows.Processes;
using Insomnia.Windows.Scheduler;
using Insomnia.Windows.Startup;
using Insomnia.Windows.Wifi;
#if DEBUG || INSOMNIA_LID_SIMULATION
using Insomnia.App.Simulation;
#endif
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.App.Composition;

public sealed class AppHost : ISessionFacade, IDisposable
{
    /// Espera antes do Reconcile: a janela de energia entrega o estado atual da tampa logo após registrar.
    public static readonly TimeSpan ReconcileDelay = TimeSpan.FromSeconds(1);

#if DEBUG || INSOMNIA_LID_SIMULATION
    public static bool IsSimulationBuild => true;
#else
    public static bool IsSimulationBuild => false;
#endif

    /// Caminho do exe em execução (o que o backstop agendado vai chamar).
    public static string ExecutablePath => Environment.ProcessPath ?? Paths.InstalledExecutablePath;

    /// WASAPI real, ou UnavailableAudio se não abrir. owner = o que descartar no fim.
    public static IAudioControl OpenAudio(ILog log, out IDisposable? owner)
    {
        try
        {
            var real = new AudioControlWin();
            owner = real;
            return real;
        }
        catch (Exception ex)
        {
            log.Error($"áudio indisponível ({ex.Message}): mutar ao fechar a tampa fica desligado nesta execução");
            owner = null;
            return new UnavailableAudio(ex.Message);
        }
    }

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tick;
    private readonly PowerEventsWindow _powerWindow;
    private readonly IPowerEvents _power;
    private readonly SleepGuardWin _sleepGuard;
    private readonly IDisposable? _audioOwner;
    private readonly FloorRuleDriver _floors;
    private readonly LidActions _lidActions;
    private readonly LidWatcher _lidWatcher;
    private readonly WifiWin _wifi;
    private readonly ReminderScheduler _reminder;
    private readonly ISettingsHost _settings;
    private readonly IShell _shell;
    private readonly TrayController _tray;
    private readonly CancellationTokenSource _shutdown = new();

    private volatile Journal? _journal;          // último diário lido (só para exibir)
    private volatile WifiReading _wifiReading = new(null, null);   // última leitura (só para exibir)
    private int _refreshing;
    private bool _quitting;
    private int _exiting;
    private bool _started;
    private volatile bool _disposed;

    public AppHost()
    {
        // O Dispatcher do thread da UI: Post funciona de qualquer thread.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tick = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _tick.Tick += (_, _) => OnTick();

        Paths = Paths.FromEnvironment();
        Log = new FileLog(Paths.LogFile);
        Clock = SystemClock.Instance;
        Scheduler = new ThreadPoolScheduler(Log);
        Store = new FileStore(Paths, Clock, Log);
        Config = new ConfigService(Store, Log);
        Notifier = new TrayNotifier(Log);

        _sleepGuard = new SleepGuardWin(Log);
        var energySaver = new EnergySaverWin(Log);
        var backstop = new BackstopSchedulerWin(Log);
        _powerWindow = new PowerEventsWindow(Log);
#if DEBUG || INSOMNIA_LID_SIMULATION
        _power = new LidSimulationWatcher(_powerWindow, Paths.SimulateLidFile, Scheduler, Log);
#else
        _power = _powerWindow;
#endif
        Inspector = new ProcessInspectorWin();
        var signals = new ProcessSignalsWin();
        var audio = OpenAudio(Log, out _audioOwner);
        var display = new DisplayPowerWin(Log);
        var docker = new DockerProbeWin(Log);
        var lidUndo = new LidUndo(Inspector, signals, audio, display, Log);

        Manager = new SessionManager(new SessionDeps(Store, new FileRecoveryLock(Paths.LockFile), Clock, Scheduler, Log, Notifier,
            _sleepGuard, energySaver, backstop, BackstopDefinition.For(ExecutablePath), lidUndo, () => CurrentLid, Config));
        _floors = new FloorRuleDriver(Manager, _power, Scheduler, Config, () => CurrentLid, Log);
        _lidActions = new LidActions(Manager, Config, Inspector, signals, audio, display, docker, _floors, Clock, Log, Scheduler);
        _lidWatcher = new LidWatcher(_power, Scheduler, _lidActions, Log);

        _wifi = new WifiWin(Log);
        Wifi = _wifi;
        Failover = new NetworkFailover(_wifi, Scheduler, Clock, Log, Notifier, Config, Paths.HandoffsLogFile);
        Browsers = new BrowserThrottle(new BrowserProcessesWin(Log), Log, Notifier);
        AutoStart = new AutoStartWin();
        _reminder = new ReminderScheduler(Scheduler, Clock, Notifier, Log);

        // Composição da UI (desenho-ui §5.2): Configurações, casca (pílula/painel) e bandeja.
        // Cada um assina Changed/Tick sozinho no construtor.
        _settings = new SettingsPresenter(this);                     // ELO settings
        _shell = new UiShell(this, _settings);                       // ELO shell
        _tray = new TrayController(this, _shell, _settings);         // ELO bandeja
        Manager.Changed += () => Post(OnSessionChanged);
        // Saída adiada que a retentativa concluiu: fecha sem novo clique em Sair.
        Manager.QuitReady += () => Post(ExitOnce);
        Failover.Changed += () => Post(RaiseChanged);
        Config.Changed += () => Post(RaiseChanged);
    }

    /// Sessão, config, rede ou leitura lenta mudou (thread da UI).
    public event Action? Changed;

    /// Virada de segundo, alinhada ao relógio; calada enquanto a contagem está pausada (thread da UI).
    public event Action? Tick;

    /// O app deve fechar (no lugar do antigo ExitThread): Program.RunUi encerra o laço do WPF.
    public event Action? Exited;

    public Paths Paths { get; }
    public FileLog Log { get; }
    public IClock Clock { get; }
    public ThreadPoolScheduler Scheduler { get; }
    public FileStore Store { get; }
    public ConfigService Config { get; }
    public TrayNotifier Notifier { get; }
    public SessionManager Manager { get; }
    public NetworkFailover Failover { get; }
    public BrowserThrottle Browsers { get; }
    public IAutoStart AutoStart { get; }
    public IWifi Wifi { get; }
    public IProcessInspector Inspector { get; }
    public string ConfigPath => Paths.ConfigFile;

    // ISessionFacade pelos tipos do contrato; o AppHost mantém os concretos (e os estáticos).
    IConfigSource ISessionFacade.Config => Config;
    ILog ISessionFacade.Log => Log;
    string ISessionFacade.ExecutablePath => ExecutablePath;
    bool ISessionFacade.IsSimulationBuild => IsSimulationBuild;

    /// Tampa como o núcleo deve ver: a última entregue pelo LidWatcher (debounce), ou a crua antes da primeira.
    public LidState CurrentLid
    {
        get
        {
            var delivered = _lidWatcher?.Current ?? LidState.Unknown;
            return delivered != LidState.Unknown ? delivered : _power.CurrentLid;
        }
    }

    /// Liga tudo. No thread da UI.
    public void Start()
    {
        if (_started) return;
        _started = true;
        Log.Info($"Insomnia aberto ({ExecutablePath})");
        if (Config.LoadError is { } configError) Manager.Fail(configError);
        _power.Start();
        _lidWatcher.Start();
        _floors.Start();
        _tray.Show();
        _shell.Start();
        ArmTick();
        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        try
        {
            await Task.Delay(ReconcileDelay, _shutdown.Token);
            await Task.Run(() => Manager.ReconcileAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Manager.Fail($"Não deu para retomar o estado ao abrir: {ex.Message}"); }
        OnSessionChanged();
        RefreshStatusInBackground();
    }

    /// Abre o painel (pedido de uma segunda instância). De qualquer thread.
    public void ShowPanel() => Post(_shell.ShowPanel);

    // ------------------------------------------------------------------ eventos

    /// Executa no thread da UI (de qualquer thread). Sem efeito depois do fim.
    public void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try { _dispatcher.BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Tick (1 Hz)

    private void ArmTick()
    {
        // Próxima virada de segundo (+5 ms de folga), para a contagem mudar junto com o relógio.
        var ms = 1000 - DateTime.Now.Millisecond + 5;
        _tick.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 50, 1005));
        _tick.Start();
    }

    /// O timer segue armado com a contagem pausada (retoma sozinho quando ela volta); só o
    /// Tick é que não dispara. Falha de quem assina vai para o log e não para o relógio.
    private void OnTick()
    {
        _tick.Stop();
        if (_disposed) return;
        try
        {
            if (!Manager.CountdownPaused) Tick?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"tick da UI: {ex.GetType().Name}: {ex.Message}");
        }
        ArmTick();
    }

    private void OnSessionChanged()
    {
        if (_disposed) return;
        if (Manager.IsActive && !Failover.IsRunning)
        {
            Failover.Start();
            RefreshStatusInBackground();
        }
        else if (!Manager.IsActive && Failover.IsRunning)
        {
            Failover.Stop();
        }
        _reminder.Update(Manager.Current);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ retrato para o menu

    /// Retrato para o menu e o painel: estado vivo do núcleo + o que a última leitura lenta trouxe.
    public StatusSnapshot Snapshot()
    {
        var lid = CurrentLid;
        PowerStatus? power = null;
        try { power = _power.ReadPower(); }
        catch (Exception ex) { Log.Error($"bateria ilegível para o menu: {ex.Message}"); }
        var journal = _journal;

        return new StatusSnapshot
        {
            Session = Manager.Current,
            Now = Clock.Now,
            CountdownPaused = Manager.CountdownPaused,
            Lid = lid,
            Power = power,
            WifiConnected = _wifiReading.Connected,
            WifiSsid = _wifiReading.Ssid,
            FrozenApps = journal is null ? 0 : journal.FrozenProcesses
                .Where(p => !string.Equals(p.Group, DockerRule.GroupName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            DockerPaused = journal?.DockerFrozen ?? false,
            LastGap = Failover.LastGap,
            LidSimulation = IsSimulationBuild,
            Browsers = Browsers.Statuses,
            RelaunchProblems = Browsers.LastProblem,
            NetworkWarning = Failover.Warning,
            LastError = Manager.LastError,
            Warnings = Manager.Warnings,
            // Com a tampa fechada a saída muda é o esperado; aberta, a entrada que sobrou está esperando.
            WaitingAudio = journal is null || lid == LidState.Closed ? []
                : [.. journal.SavedAudioOutputs.Select(a => new WaitingAudio(a.DeviceId, a.Name ?? a.DeviceId))],
        };
    }

    /// Lê fora da UI o que é lento (WMI dos navegadores, Wi-Fi, diário) e redesenha o menu.
    public void RefreshStatusInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                Browsers.Scan();
                ReadWifi();
                try { _journal = Store.LoadJournal(); }
                catch (Exception ex) { _journal = null; Log.Error($"diário ilegível para o menu: {ex.Message}"); }
            }
            catch (Exception ex) { Log.Error($"leitura de estado para o menu falhou: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
            Post(RaiseChanged);
        });
    }

    private void ReadWifi()
    {
        try
        {
            var connected = _wifi.IsConnected;
            var ssid = connected
                ? ManagedNativeWifi.NativeWifi.EnumerateConnectedNetworkSsids().Select(s => s.ToString()).FirstOrDefault()
                : null;
            _wifiReading = new WifiReading(connected, ssid);
        }
        catch (Exception ex)
        {
            _wifiReading = new WifiReading(null, null);
            Log.Error($"Wi-Fi ilegível para o menu: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ comandos

    public async Task<string?> StartSessionAsync(TimeSpan duration)
    {
        var outcome = await Task.Run(() => Manager.StartAsync(duration, _shutdown.Token));
        var text = TrayTexts.Explain(outcome);
        var withDetail = outcome is StartOutcome.PowerWriteFailed or StartOutcome.JournalUnreadable or StartOutcome.BackstopNotArmed;
        if (text is not null && withDetail && Manager.LastError is { Length: > 0 } detail)
            text += " " + detail;
        return text;
    }

    public async Task<string?> ExtendSessionAsync(TimeSpan by)
    {
        var outcome = await Task.Run(() => Manager.ExtendAsync(by, _shutdown.Token));
        return TrayTexts.Explain(outcome);
    }

    public async Task EndSessionAsync()
    {
        if (!Manager.IsActive) return;
        await Task.Run(() => Manager.EndAsync(EndReason.User));
    }

    /// Relança um navegador sem limitação, depois de confirmar com a pessoa.
    public async Task RelaunchBrowserAsync(string kind)
    {
        var name = ChromiumFlags.DisplayNameFor(kind);
        // MessageBox do WPF (o Win32 por baixo já sai no estilo do Windows 11); Cancelar é o padrão.
        var answer = System.Windows.MessageBox.Show(BrowserThrottle.ConfirmationBody(name), BrowserThrottle.ConfirmationTitle(name),
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        try
        {
            var outcome = await Task.Run(() => Browsers.RelaunchUnthrottledAsync(kind, _shutdown.Token));
            Log.Info($"relançar {name}: {outcome}");
        }
        catch (OperationCanceledException) { }
        RefreshStatusInBackground();
    }

    /// "Parar de esperar": tira a saída de áudio do diário sem mexer nela (a pessoa desiste de restaurar).
    public async Task StopWaitingAudioAsync(string deviceId)
    {
        await Task.Run(() => Manager.RunExclusiveAsync("parar de esperar áudio", tx =>
        {
            var kept = tx.Journal.SavedAudioOutputs.RemoveAll(a => string.Equals(a.DeviceId, deviceId, StringComparison.Ordinal));
            if (kept.Length == tx.Journal.SavedAudioOutputs.Length) return Task.FromResult(false);
            try
            {
                tx.Write(tx.Journal with { SavedAudioOutputs = kept });
                Log.Info($"áudio: deixou de esperar a saída {deviceId}");
                return Task.FromResult(true);
            }
            catch (StoreWriteException ex)
            {
                Manager.Fail($"Diário não gravou ao parar de esperar a saída de áudio: {ex.Message}");
                return Task.FromResult(false);
            }
        }));
        RefreshStatusInBackground();
    }

    /// Sair: encerra a sessão e desfaz tudo; se a recuperação não concluiu, NÃO sai.
    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            var result = await Task.Run(() => Manager.EndAsync(EndReason.Quit));
            if (result.LetsQuitGo)
            {
                ExitOnce();
                return;
            }
            Log.Info($"saída adiada: recuperação pendente ({result.Outcome})");
            Notifier.Post("Saída adiada", "Recuperação pendente: o Insomnia continua aberto e tenta de novo.");
        }
        catch (Exception ex)
        {
            Log.Error($"saída adiada: {ex.Message}");
        }
        finally { _quitting = false; }
    }

    /// Fecha o app, no máximo uma vez, no thread da UI. Dois caminhos chegam aqui: o resultado
    /// imediato do Sair e o QuitReady da retentativa de uma saída adiada.
    private void ExitOnce()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        Log.Info("saindo");
        Exited?.Invoke();
    }

    // ------------------------------------------------------------------ fim

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _shutdown.Cancel();
        Try(_tray.Dispose);
        Try(_shell.Dispose);
        Try(_settings.Dispose);
        Notifier.Detach();
        _reminder.Dispose();
        Try(Failover.Stop);
        Try(_floors.Stop);
        Try(_lidWatcher.Stop);
        Try(_power.Stop);
        Try(_lidActions.Dispose);
        Try(() => _audioOwner?.Dispose());
        Try(_wifi.Dispose);
        Try(_sleepGuard.Dispose);
        _powerWindow.Dispose();
        _shutdown.Dispose();
    }

    private sealed record WifiReading(bool? Connected, string? Ssid);

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error($"encerramento: {ex.Message}"); }
    }
}
