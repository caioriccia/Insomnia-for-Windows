// Ações ao fechar e abrir a tampa (desenho §7.2, ordem congelada; R1, R3, R8).
// Fechar, sob RunExclusive("lid close"), conferindo após cada passo que o evento ainda é o
// mais novo: (1) apagar tela; (2) áudio: lê → diário → muta; (3) congelar por grupo:
// diário provisório → suspend → promoção (falhou ⇒ resume imediato); (4) Docker com dupla
// checagem; (5) pausa a contagem. Fora do lock: (6) reavalia os pisos (tampa é causa de Economia).
// Abrir: UndoLidActions → ResumeCountdown → reavalia os pisos.
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Power;
using Insomnia.Core.Session;

namespace Insomnia.Core.Lid;

public sealed class LidActions : IDisposable
{
    /// Retentativa de restauração de áudio em saída conectada que falhou.
    public static readonly TimeSpan AudioRetryInterval = TimeSpan.FromSeconds(30);
    public const int AudioRetryLimit = 10;

    private readonly SessionManager _manager;
    private readonly IConfigSource _config;
    private readonly IProcessInspector _inspector;
    private readonly IProcessSignals _signals;
    private readonly IAudioControl _audio;
    private readonly IDisplayPower _display;
    private readonly IDockerProbe _docker;
    private readonly FloorRuleDriver _floors;
    private readonly ILog _log;
    private readonly PlannerEnvironment _env;
    private readonly LidUndo _audioUndo;

    private readonly object _gate = new();
    private int _latestEvent;
    private CancellationTokenSource _eventCts = new();
    private LidState _lid = LidState.Unknown;
    private bool _wasActive;
    private bool _disposed;

    private IScheduler? _scheduler;
    private IDisposable? _audioTimer;
    private int _audioAttempts;
    private Task _audioRetry = Task.CompletedTask;

    public LidActions(SessionManager manager, IConfigSource config, IProcessInspector inspector, IProcessSignals signals,
        IAudioControl audio, IDisplayPower display, IDockerProbe docker, FloorRuleDriver floors, IClock clock, ILog log,
        IScheduler? scheduler = null, PlannerEnvironment? environment = null)
    {
        _manager = manager;
        _config = config;
        _inspector = inspector;
        _signals = signals;
        _audio = audio;
        _display = display;
        _docker = docker;
        _floors = floors;
        _ = clock; // reservado: o contrato o recebe; as ações de tampa não carimbam hora no diário
        _log = log;
        _scheduler = scheduler;
        _env = environment ?? PlannerEnvironment.Current;
        _audioUndo = new LidUndo(inspector, signals, audio, display, log);
        _wasActive = manager.IsActive;
        _audio.DevicesChanged += OnDevicesChanged;
        _manager.Changed += OnManagerChanged;
    }

    /// Estado da tampa como o LidWatcher entregou por último.
    public LidState Lid { get { lock (_gate) return _lid; } }

    /// Número do evento de tampa mais novo entregue.
    public int LatestEvent { get { lock (_gate) return _latestEvent; } }

    /// Última retentativa de áudio disparada (para quem precisa esperar, ex.: testes).
    public Task AudioRetryTask { get { lock (_gate) return _audioRetry; } }

    /// Timer das retentativas de áudio. O LidWatcher liga o dele aqui; o primeiro vence.
    public void AttachScheduler(IScheduler scheduler)
    {
        lock (_gate) _scheduler ??= scheduler;
    }

    /// Estado inicial da tampa (sem evento), ao ligar o observador.
    public void NoteLid(LidState state)
    {
        lock (_gate) _lid = state;
    }

    /// Chamado pelo LidWatcher ASSIM que um evento é entregue, antes de enfileirar a ação:
    /// torna obsoleta a ação anterior ainda não executada e cancela a checagem de Docker em curso.
    public void NoteEvent(int eventNo, LidState state)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (eventNo <= _latestEvent) return;
            _latestEvent = eventNo;
            _lid = state;
            old = _eventCts;
            _eventCts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    public async Task OnCloseAsync(int eventNo)
    {
        if (!IsCurrent(eventNo))
        {
            _log.Info($"tampa fechada (evento {eventNo}): superado por evento mais novo, nada a fazer");
            return;
        }
        if (!_manager.IsActive)
        {
            _log.Info($"tampa fechada (evento {eventNo}) sem sessão: nada a fazer");
            return;
        }

        var ct = TokenFor(eventNo);
        await _manager.RunExclusiveAsync("lid close", tx => CloseBodyAsync(tx, eventNo, ct)).ConfigureAwait(false);
        await EvaluateFloorsAsync().ConfigureAwait(false);
    }

    public async Task OnOpenAsync(int eventNo)
    {
        if (!_manager.IsActive)
        {
            _log.Info($"tampa aberta (evento {eventNo}) sem sessão: nada a fazer");
            return;
        }
        try { await _manager.UndoLidActionsAsync().ConfigureAwait(false); }
        catch (Exception ex) { _manager.Fail($"Desfazer ações da tampa falhou: {ex.Message}"); }
        _manager.ResumeCountdown();
        await EvaluateFloorsAsync().ConfigureAwait(false);
        StartAudioRetry("tampa aberta");
    }

    private async Task<bool> CloseBodyAsync(JournalTransaction tx, int eventNo, CancellationToken ct)
    {
        if (!Still(eventNo)) return false;
        var cfg = _config.Current;
        _log.Info($"tampa fechada (evento {eventNo}): aplicando ações");

        // (1) tela: nada diarizado.
        if (cfg.DarkenDisplayOnLidClose)
        {
            try { _display.TurnOff(); }
            catch (Exception ex) { _log.Error($"tela: apagar falhou ({ex.Message})"); }
        }

        // (2) áudio.
        if (cfg.MuteOnLidClose && Still(eventNo)) MuteSavingCurrent(tx);

        // (3) congelar por grupo.
        if (Still(eventNo))
        {
            IReadOnlyList<ProcessInfo>? snapshot = null;
            try { snapshot = _inspector.Snapshot(); }
            catch (Exception ex) { _manager.Fail($"Congelar apps: não consegui listar processos ({ex.Message})"); }
            if (snapshot is not null)
            {
                foreach (var group in FreezePlanner.Plan(cfg, snapshot, _env))
                {
                    if (!Still(eventNo)) break;
                    await FreezeGroupAsync(tx, group, docker: false, beforeSignal: null).ConfigureAwait(false);
                }
            }
        }

        // (4) Docker.
        if (cfg.DockerRule && Still(eventNo)) await FreezeDockerAsync(tx, eventNo, ct).ConfigureAwait(false);

        // (5) contagem.
        if (Still(eventNo)) _manager.PauseCountdown();
        return true;
    }

    private void MuteSavingCurrent(JournalTransaction tx)
    {
        AudioOutput current;
        try { current = _audio.ReadDefaultOutput(); }
        catch (Exception ex)
        {
            _log.Info($"áudio: leitura da saída padrão falhou ({ex.Message}); mudo pulado, o resto segue");
            return;
        }

        // Uma entrada por deviceId; a que já existe guarda o valor de antes (não é sobrescrita).
        if (!tx.Journal.SavedAudioOutputs.Any(a => a.DeviceId == current.DeviceId))
        {
            var entry = new SavedAudioOutput(current.DeviceId, current.Name, current.Volume, current.Muted);
            try { tx.Write(tx.Journal with { SavedAudioOutputs = tx.Journal.SavedAudioOutputs.Add(entry) }); }
            catch (StoreWriteException ex)
            {
                _manager.Fail($"Áudio não foi mutado: o diário não gravou ({ex.Message})");
                return;
            }
        }

        try { _audio.SetMute(current.DeviceId, true); }
        catch (Exception ex) { _log.Error($"áudio: mutar {current.Name ?? current.DeviceId} falhou ({ex.Message}); entrada fica para a abertura"); }
    }

    /// Diário provisório (sem identidade) → [beforeSignal] → suspend de quem ainda é o mesmo
    /// processo → promoção com identidade. Promoção falhou ⇒ resume imediato do que parou.
    private async Task FreezeGroupAsync(JournalTransaction tx, FreezeGroup group, bool docker, Func<Task<bool>>? beforeSignal)
    {
        var known = tx.Journal.FrozenProcesses.Select(f => f.Pid).ToHashSet();
        var candidates = group.Members
            .Where(p => !known.Contains(p.Pid) && !p.IsSuspended && p.Identity is { } id && !string.IsNullOrEmpty(id.BootId))
            .ToList();
        if (candidates.Count == 0) return;
        var batch = candidates.Select(p => p.Pid).ToHashSet();

        // R1: diário antes do suspend. Sem diário, nada é sinalizado.
        var provisional = candidates.Select(p => new FrozenProcess(p.Pid, p.Name, null, null, group.Name));
        try { tx.Write(tx.Journal with { FrozenProcesses = tx.Journal.FrozenProcesses.AddRange(provisional) }); }
        catch (StoreWriteException ex)
        {
            _manager.Fail($"{group.Name} não foi congelado: o diário não gravou ({ex.Message})");
            return;
        }

        if (beforeSignal is not null && !await beforeSignal().ConfigureAwait(false))
        {
            DropEntries(tx, batch, group.Name);
            return;
        }

        var stopped = new List<ProcessInfo>();
        foreach (var p in candidates)
        {
            ProcessInfo? now;
            try { now = _inspector.Lookup(p.Pid); }
            catch (Exception ex) { _log.Error($"congelar {p.Name} (pid {p.Pid}): consulta falhou ({ex.Message})"); continue; }
            if (!ProcessControl.IsSuspendable(p, now)) continue;
            bool ok;
            try { ok = _signals.Suspend(p.Pid); }
            catch (Exception ex) { ok = false; _log.Error($"congelar {p.Name} (pid {p.Pid}): {ex.Message}"); }
            if (ok) stopped.Add(p);
        }

        // Promoção: só quem realmente parou ganha identidade; os outros provisórios saem.
        var before = tx.Journal;
        var promoted = before.FrozenProcesses.Where(f => !batch.Contains(f.Pid))
            .Concat(stopped.Select(p => ProcessControl.Promoted(p, group.Name)));
        var updated = before with
        {
            FrozenProcesses = [.. promoted],
            DockerFrozen = before.DockerFrozen || (docker && stopped.Count > 0),
        };
        try
        {
            tx.Write(updated);
            _log.Info($"congelados: {group.Name} ({stopped.Count} de {candidates.Count} processo(s))");
        }
        catch (StoreWriteException ex)
        {
            CancelStops(tx, group, batch, stopped, ex);
        }
    }

    /// A confirmação no diário falhou depois do suspend: retoma na hora o que parou (mesma
    /// identidade e ppid, mesmo que ainda não apareça como parado). O que não voltar fica
    /// no diário como provisório (não verificável) e é dito no aviso.
    private void CancelStops(JournalTransaction tx, FreezeGroup group, HashSet<int> batch, List<ProcessInfo> stopped, StoreWriteException cause)
    {
        var stillStopped = new List<ProcessInfo>();
        foreach (var p in stopped)
        {
            ProcessInfo? now;
            try { now = _inspector.Lookup(p.Pid); }
            catch { stillStopped.Add(p); continue; }
            if (now is null) continue; // saiu: nada a retomar
            bool resumed;
            try { resumed = ProcessControl.CancelDecision(p, now) && _signals.Resume(p.Pid); }
            catch { resumed = false; }
            if (!resumed && ProcessControl.CancelDecision(p, now)) stillStopped.Add(p);
        }

        var keep = stillStopped.Select(p => p.Pid).ToHashSet();
        DropEntries(tx, batch.Where(pid => !keep.Contains(pid)).ToHashSet(), group.Name);

        if (stillStopped.Count == 0)
            _manager.Fail($"{group.Name} não foi congelado: o diário não confirmou ({cause.Message}); processos retomados");
        else
            _manager.Fail($"{group.Name}: o diário não confirmou ({cause.Message}) e ficaram parados: "
                + string.Join(", ", stillStopped.Select(p => $"{p.Name} (pid {p.Pid})")));
    }

    /// Tira entradas provisórias do diário. Falha só registra: provisório de processo que
    /// está rodando sai sozinho na próxima abertura (ResumeDecision = Gone).
    private void DropEntries(JournalTransaction tx, HashSet<int> pids, string group)
    {
        if (pids.Count == 0) return;
        var j = tx.Journal;
        var remaining = j.FrozenProcesses.Where(f => !(pids.Contains(f.Pid) && !f.HasIdentity)).ToList();
        if (remaining.Count == j.FrozenProcesses.Length) return;
        try { tx.Write(j with { FrozenProcesses = [.. remaining] }); }
        catch (StoreWriteException ex) { _log.Error($"{group}: limpar entradas provisórias falhou ({ex.Message}); saem na abertura"); }
    }

    private async Task FreezeDockerAsync(JournalTransaction tx, int eventNo, CancellationToken ct)
    {
        var first = await DockerRule.ProbeAsync(_docker, _log, ct).ConfigureAwait(false);
        if (!Still(eventNo)) return;
        if (first != DockerProbe.Idle)
        {
            _log.Info($"Docker: {Describe(first)}; fica rodando");
            return;
        }

        IReadOnlyList<ProcessInfo> snapshot;
        try { snapshot = _inspector.Snapshot(); }
        catch (Exception ex) { _log.Error($"Docker: não consegui listar processos ({ex.Message}); fica rodando"); return; }
        var group = DockerRule.Group(snapshot, _env);
        if (group is null)
        {
            _log.Info("Docker: Docker Desktop não está rodando");
            return;
        }

        // Segunda checagem DEPOIS do diário provisório e logo antes do suspend.
        await FreezeGroupAsync(tx, group, docker: true, beforeSignal: async () =>
        {
            if (!Still(eventNo)) return false;
            var second = await DockerRule.ProbeAsync(_docker, _log, ct).ConfigureAwait(false);
            if (second != DockerProbe.Idle) _log.Info($"Docker: segunda checagem {Describe(second)}; fica rodando");
            return second == DockerProbe.Idle && Still(eventNo);
        }).ConfigureAwait(false);
    }

    private static string Describe(DockerProbe p) => p switch
    {
        DockerProbe.Idle => "ocioso",
        DockerProbe.Busy => "com contêineres rodando",
        _ => "indisponível",
    };

    private async Task EvaluateFloorsAsync()
    {
        try { await _floors.EvaluateNowAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Error($"pisos de bateria: reavaliação falhou ({ex.Message})"); }
    }

    private bool IsCurrent(int eventNo) { lock (_gate) return eventNo >= _latestEvent; }

    private bool Still(int eventNo) => IsCurrent(eventNo) && _manager.IsActive;

    private CancellationToken TokenFor(int eventNo)
    {
        lock (_gate)
        {
            if (eventNo < _latestEvent) return new CancellationToken(canceled: true);
            return _eventCts.Token;
        }
    }

    // ---------- retentativa de áudio ----------

    private void OnDevicesChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _audioAttempts = 0;
            _audioTimer?.Dispose();
            _audioTimer = null;
        }
        RunAudioRetry("dispositivos de áudio mudaram");
    }

    private void OnManagerChanged()
    {
        bool ended;
        lock (_gate)
        {
            var active = _manager.IsActive;
            ended = _wasActive && !active;
            _wasActive = active;
        }
        // Fim de sessão restaurou (ou tentou restaurar) o áudio: confere de novo mais tarde.
        if (ended) StartAudioRetry("sessão encerrada");
    }

    /// Recomeça a contagem e agenda a primeira tentativa para daqui a 30 s.
    private void StartAudioRetry(string why)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _audioAttempts = 0;
        }
        ScheduleAudioRetry(why);
    }

    private void ScheduleAudioRetry(string why)
    {
        lock (_gate)
        {
            if (_disposed || _scheduler is null) return;
            if (_audioAttempts >= AudioRetryLimit)
            {
                _log.Info($"áudio: {AudioRetryLimit} tentativas sem sucesso; espera a próxima mudança de dispositivo");
                return;
            }
            _audioTimer?.Dispose();
            // O timer não herda o contexto assíncrono de quem agendou (ex.: uma transação em curso).
            var flow = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
            try
            {
                _audioTimer = _scheduler.Schedule(AudioRetryInterval, () =>
                {
                    lock (_gate) { _audioTimer = null; _audioAttempts++; }
                    RunAudioRetry(why);
                });
            }
            finally { flow?.Undo(); }
        }
    }

    private void RunAudioRetry(string why)
    {
        var task = AudioRetryAsync(why);
        lock (_gate) _audioRetry = task;
    }

    private async Task AudioRetryAsync(string why)
    {
        try
        {
            // Com sessão e tampa fechada, a saída que voltou espera a abertura.
            if (_manager.IsActive && Lid == LidState.Closed) return;
            var outcome = await _manager.RunExclusiveAsync("audio retry",
                tx => Task.FromResult<AudioRestoreOutcome?>(_audioUndo.RestoreAudio(tx))).ConfigureAwait(false);
            if (outcome is null)
            {
                ScheduleAudioRetry(why); // lock ocupado ou diário ilegível: tenta de novo mais tarde
                return;
            }
            foreach (var p in outcome.Problems) _log.Error($"áudio ({why}): {p}");
            if (outcome.FailedConnected > 0) ScheduleAudioRetry(why);
        }
        catch (Exception ex)
        {
            _log.Error($"áudio: retentativa falhou ({ex.Message})");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _audioTimer?.Dispose();
            _audioTimer = null;
        }
        _audio.DevicesChanged -= OnDevicesChanged;
        _manager.Changed -= OnManagerChanged;
        _eventCts.Dispose();
    }
}
