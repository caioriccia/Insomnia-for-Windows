// Núcleo de sessão (desenho §6.1/§6.2, invariantes R1–R11 de §8).
//
// Toda mudança de sistema passa por RunExclusiveAsync: fila serial dentro do processo +
// lock de arquivo compartilhado com o backstop (10 s) + diário lido do DISCO. Dentro da
// transação vale "tx.Write ANTES do efeito" (R1); para desfazer, "efeito, depois limpar" (R3).
namespace Insomnia.Core.Session;

using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Time;

public sealed class SessionManager
{
    /// Quanto uma transação espera pelo lock de recuperação (R11).
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    /// Retentativa de fim/reconcile com lock ocupado ou restauração pendente (R6).
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private enum RunStatus { Ran, Locked, JournalUnreadable }

    /// Transação em curso no fluxo assíncrono atual. Serve para uma chamada feita DE DENTRO
    /// de um corpo exclusivo (ex.: LidActions chama FloorRuleDriver.EvaluateNowAsync, que
    /// chama SetEnergySaverAsync) reaproveitar a transação em vez de esperar pela fila que
    /// ela mesma segura — o que seria um deadlock. Active cai ao fim do corpo, então um timer
    /// que tenha herdado o contexto não reaproveita uma transação já encerrada.
    private sealed class Ambient(JournalTransaction tx)
    {
        public JournalTransaction Tx { get; } = tx;
        public volatile bool Active = true;
    }

    private readonly SessionDeps _d;
    private readonly RecoveryEngine _engine;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly AsyncLocal<Ambient?> _ambient = new();
    private readonly object _gate = new();

    private int _endTicket;
    private volatile bool _quitRequested;
    private EndReason? _pendingEnd;                       // sob _gate
    private volatile Session? _current;
    private volatile bool _countdownPaused;
    private volatile string? _lastError;
    private volatile bool _energySaverOurs;
    private readonly List<string> _failures = [];          // sob _gate
    private IReadOnlyList<string> _restoreProblems = [];   // sob _gate
    private bool _incompleteNotified;                      // sob _gate
    private IDisposable? _endTimer, _endRetry, _reconcileRetry; // sob _gate

    public SessionManager(SessionDeps deps)
    {
        _d = deps;
        _engine = new RecoveryEngine(deps.SleepGuard, deps.EnergySaver, deps.LidUndo, deps.Clock, deps.Log);
    }

    public Session? Current => _current;
    public bool IsActive => _current is not null;
    /// Só aparência: com a tampa fechada a bandeja para de redesenhar. O fim continua em EndsAt.
    public bool CountdownPaused => _countdownPaused;
    public string? LastError => _lastError;
    /// Lido do último diário visto por uma transação.
    public bool EnergySaverOurs => _energySaverOurs;

    /// Linhas ⚠ do menu: falhas registradas por Fail e problemas da última restauração/undo.
    public IReadOnlyList<string> Warnings
    {
        get { lock (_gate) return [.. _failures, .. _restoreProblems]; }
    }

    public event Action? Changed;

    /// Um fim terminou de um jeito que libera a saída (EndResult.LetsQuitGo) DEPOIS de a pessoa
    /// ter pedido Sair (EndReason.Quit). Cobre a saída adiada: o 1º Quit voltou Locked/Incomplete
    /// sem backstop/SessionRetained, a retentativa de 30 s concluiu, e o app deve fechar sozinho.
    /// Também dispara quando o 1º Quit já restaura; quem assina precisa tolerar o disparo repetido
    /// (o AppHost sai uma vez só). Chega de qualquer thread. Sem pedido de Sair, não dispara.
    public event Action? QuitReady;

    private EndReason? PendingEnd { get { lock (_gate) return _pendingEnd; } }

    // ------------------------------------------------------------------ início

    public async Task<StartOutcome> StartAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (_quitRequested) return StartOutcome.RefusedQuitRequested;
        if (PendingEnd is not null) return StartOutcome.RefusedEndPending;
        if (_current is not null) return StartOutcome.RefusedAlreadyActive;
        var ticket = Volatile.Read(ref _endTicket);

        var (status, outcome) = await RunCoreAsync("start", tx => PerformStartAsync(tx, duration, ticket, ct), ct).ConfigureAwait(false);
        var final = status switch
        {
            RunStatus.Locked => StartOutcome.Locked,
            RunStatus.JournalUnreadable => StartOutcome.JournalUnreadable,
            _ => outcome,
        };
        _d.Log.Info($"início: {final}");
        RaiseChanged();
        return final;
    }

    /// Ordem (R4/R10): session.json → diário → backstop → LIDACTION=0 → hold.
    private async Task<StartOutcome> PerformStartAsync(JournalTransaction tx, TimeSpan duration, int ticket, CancellationToken ct)
    {
        if (_quitRequested) return StartOutcome.RefusedQuitRequested;
        if (PendingEnd is not null) return StartOutcome.RefusedEndPending;
        if (_current is not null) return StartOutcome.RefusedAlreadyActive;
        if (EndRequestedSince(ticket)) return StartOutcome.RefusedEndPending;

        // (1) sessão anterior: ilegível é evidência (R5) — não substitui.
        Session? prior;
        try { prior = _d.Store.LoadSession(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError($"Arquivo de sessão ilegível: {ex.Message}. Abra o app de novo para movê-lo para o lado.");
            return StartOutcome.RefusedSessionFileUnreadable;
        }

        var now = _d.Clock.Now;
        var cfg = _d.Config.Current;
        var session = SessionMath.NewSession(now, duration, TimeSpan.FromSeconds(cfg.MaxDuration));
        var journalBefore = tx.Journal;

        // (2) session.json primeiro: "sem sessão + diário sujo" passa a significar sempre "desfazer".
        try { _d.Store.SaveSession(session); }
        catch (Exception ex)
        {
            SetError($"Não foi possível gravar a sessão: {ex.Message}");
            return StartOutcome.PowerWriteFailed;
        }

        // (3) LIDACTION atual + pré-voo (§7/§10.1): reescreve o MESMO valor para provar permissão
        // antes de diarizar. Escrever o valor que já está lá não muda estado (R1 intacto).
        LidActionValues previous;
        try
        {
            previous = _d.SleepGuard.ReadLidAction();
            _d.SleepGuard.WriteLidAction(previous.SchemeId, previous.Ac, previous.Dc);
        }
        catch (Exception ex)
        {
            RollBackStart(tx, journalBefore, prior);
            SetError($"Sem permissão para mudar o plano de energia: {ex.Message}");
            return StartOutcome.PowerWriteFailed;
        }

        // (4) diário ANTES do efeito (R1); entrada existente não é sobrescrita (R2).
        if (tx.Journal.LidAction is null)
        {
            try { tx.Write(tx.Journal with { LidAction = new LidActionEntry(previous.SchemeId, previous.Ac, previous.Dc, now) }); }
            catch (StoreWriteException ex)
            {
                RollBackStart(tx, journalBefore, prior);
                SetError($"Não foi possível gravar o diário de recuperação: {ex.Message}");
                return StartOutcome.PowerWriteFailed;
            }
        }
        else if (tx.Journal.LidAction.SchemeId != previous.SchemeId)
        {
            // O diário guarda valores de OUTRO plano: zerar o plano ativo sem registro violaria R1.
            RollBackStart(tx, journalBefore, prior);
            SetError("O diário tem uma ação de tampa pendente de outro plano de energia. Encerre a sessão anterior antes.");
            return StartOutcome.PowerWriteFailed;
        }
        if (EndRequestedSince(ticket)) { RollBackStart(tx, journalBefore, prior); return StartOutcome.RefusedEndPending; }

        // (5) backstop armado antes de mexer no LIDACTION (R10).
        if (!EnsureBackstopArmed("início"))
        {
            RollBackStart(tx, journalBefore, prior);
            SetError("O backstop não pôde ser instalado no Agendador de Tarefas: sessão não iniciada.");
            return StartOutcome.BackstopNotArmed;
        }
        if (EndRequestedSince(ticket)) { RollBackStart(tx, journalBefore, prior); return StartOutcome.RefusedEndPending; }

        // (6) o efeito.
        try { _d.SleepGuard.WriteLidAction(previous.SchemeId, 0, 0); }
        catch (Exception ex)
        {
            _d.Log.Error($"LIDACTION=0 falhou: {ex.Message}; desfazendo");
            RemoveSessionFileForRollback(prior);
            var report = await _engine.RestoreAllAsync(tx, ct).ConfigureAwait(false);
            SetRestoreProblems(report.Problems);
            SetError($"Não foi possível mudar a ação ao fechar a tampa: {ex.Message}");
            return StartOutcome.PowerWriteFailed;
        }

        // (7) hold de execução (morre com o processo; não é diarizado — R7).
        SetHold(true);

        // Um fim pedido depois do efeito espera na fila e desfaz tudo logo em seguida.
        if (EndRequestedSince(ticket)) return StartOutcome.RefusedEndPending;

        // (8)
        lock (_gate) _failures.Clear();
        _current = session;
        _countdownPaused = _d.LidState() == LidState.Closed;
        ArmEndTimer(session);
        _d.Log.Info($"sessão iniciada até {session.EndsAt:yyyy-MM-ddTHH:mm:ssK}");
        return StartOutcome.Started;
    }

    /// Volta ao estado de antes do início: diário anterior primeiro, depois session.json anterior (ou apaga).
    private void RollBackStart(JournalTransaction tx, Journal journalBefore, Session? prior)
    {
        if (!ReferenceEquals(tx.Journal, journalBefore))
        {
            try { tx.Write(journalBefore); }
            catch (StoreWriteException ex) { _d.Log.Error($"rollback do diário falhou: {ex.Message}"); }
        }
        RemoveSessionFileForRollback(prior);
    }

    private void RemoveSessionFileForRollback(Session? prior)
    {
        try
        {
            if (prior is not null) _d.Store.SaveSession(prior);
            else _d.Store.DeleteSession();
        }
        catch (Exception ex)
        {
            Fail($"Não foi possível desfazer o arquivo de sessão: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ extensão

    public async Task<ExtendOutcome> ExtendAsync(TimeSpan by, CancellationToken ct = default)
    {
        if (_current is null) return ExtendOutcome.NoSession;
        var ticket = Volatile.Read(ref _endTicket);
        var (status, outcome) = await RunCoreAsync("extend", tx => Task.FromResult(PerformExtend(by, ticket)), ct).ConfigureAwait(false);
        var final = status switch
        {
            RunStatus.Locked => ExtendOutcome.Locked,
            RunStatus.JournalUnreadable => ExtendOutcome.JournalUnreadable,
            _ => outcome,
        };
        RaiseChanged();
        return final;
    }

    private ExtendOutcome PerformExtend(TimeSpan by, int ticket)
    {
        var current = _current;
        if (current is null || EndRequestedSince(ticket)) return ExtendOutcome.NoSession;
        var now = _d.Clock.Now;
        var extended = SessionMath.Extended(current, by, now, TimeSpan.FromSeconds(_d.Config.Current.MaxDuration));

        // Backstop rearmado ANTES de mover o prazo.
        if (!EnsureBackstopArmed("extensão"))
        {
            SetError("O backstop não pôde ser instalado: sessão não estendida.");
            return ExtendOutcome.Failed;
        }
        if (EndRequestedSince(ticket)) return ExtendOutcome.NoSession;

        try { _d.Store.SaveSession(extended); }
        catch (Exception ex)
        {
            SetError($"Não foi possível gravar a sessão estendida: {ex.Message}");
            return ExtendOutcome.Failed;
        }
        _current = extended;
        ArmEndTimer(extended);
        _d.Log.Info($"sessão estendida até {extended.EndsAt:yyyy-MM-ddTHH:mm:ssK}");
        return ExtendOutcome.Extended;
    }

    // ------------------------------------------------------------------ fim

    public async Task<EndResult> EndAsync(EndReason reason, CancellationToken ct = default)
    {
        // Invalida início/extensão/energia em curso: eles conferem o ticket depois de cada passo.
        Interlocked.Increment(ref _endTicket);
        if (reason == EndReason.Quit) _quitRequested = true;
        CancelTimer(ref _endRetry);

        var (status, result) = await RunCoreAsync("end", tx => PerformEndAsync(tx, reason, _current is not null, ct), ct).ConfigureAwait(false);
        var final = status switch
        {
            RunStatus.Locked => new EndResult(EndOutcome.Locked, false, [_lastError ?? "lock de recuperação ocupado"]),
            RunStatus.JournalUnreadable => new EndResult(EndOutcome.JournalUnreadable, false, [_lastError ?? "diário ilegível"]),
            _ => result!,
        };
        AfterEnd(reason, final);
        return final;
    }

    /// Ordem (R4): para timers, zera a sessão, apaga session.json, solta o hold, RestoreAll.
    private async Task<EndResult> PerformEndAsync(JournalTransaction tx, EndReason reason, bool sessionExisted, CancellationToken ct)
    {
        CancelTimer(ref _endTimer);
        _current = null;
        _countdownPaused = false;

        // session.json some ANTES de desfazer. Se não sair do lugar, não desfaz: um
        // session.json válido sobrando reaplicaria a sessão na próxima abertura.
        if (!RemoveSessionFile(out var problem))
        {
            SetHold(false);
            SetError(problem);
            return new EndResult(EndOutcome.SessionRetained, false, [problem]);
        }

        SetHold(false);
        var report = await _engine.RestoreAllAsync(tx, ct).ConfigureAwait(false);
        SetRestoreProblems(report.Problems);

        if (report.Complete)
        {
            lock (_gate) _incompleteNotified = false;
            _d.Log.Info($"sessão encerrada ({reason})");
            if (sessionExisted && reason != EndReason.Quit)
                _d.Notifier.Post("Sessão encerrada", EndBody(reason));
            return new EndResult(EndOutcome.Restored, false, report.Problems);
        }

        var armed = EnsureBackstopArmed("restauração incompleta");
        bool notify;
        lock (_gate) { notify = !_incompleteNotified; _incompleteNotified = true; }
        if (notify)
        {
            var first = report.Problems.Count > 0 ? report.Problems[0] + " " : "";
            _d.Notifier.Post("Restauração incompleta",
                first + (armed ? "O backstop tenta de novo a cada minuto." : "Nova tentativa em 30 s."));
        }
        return new EndResult(EndOutcome.Incomplete, armed, report.Problems);
    }

    /// Apaga session.json; se estiver ilegível, MOVE para o lado (R5: nunca apaga evidência).
    private bool RemoveSessionFile(out string problem)
    {
        problem = "";
        try
        {
            _d.Store.LoadSession();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                var moved = _d.Store.MoveAsideUnreadableSession();
                _d.Log.Info($"sessão ilegível movida para {moved ?? "(nada a mover)"}");
                return true;
            }
            catch (Exception moveEx)
            {
                problem = $"Sessão ilegível não pôde ser movida ({moveEx.Message}): nada foi desfeito.";
                _d.Log.Error(problem);
                return false;
            }
        }
        try
        {
            _d.Store.DeleteSession();
            return true;
        }
        catch (Exception ex)
        {
            problem = $"Não foi possível apagar o arquivo de sessão ({ex.Message}): nada foi desfeito.";
            _d.Log.Error(problem);
            return false;
        }
    }

    private void AfterEnd(EndReason reason, EndResult result)
    {
        switch (result.Outcome)
        {
            case EndOutcome.Restored:
            case EndOutcome.Incomplete when result.AgentArmed:
                lock (_gate) _pendingEnd = null;
                break;
            case EndOutcome.JournalUnreadable:
                // Sem timer: espera um pedido novo (R5 — nada se decide sobre evidência ilegível).
                lock (_gate) _pendingEnd = reason;
                break;
            default: // Locked, Incomplete sem backstop, SessionRetained
                lock (_gate)
                {
                    _pendingEnd = reason;
                    _endRetry?.Dispose();
                    _endRetry = Schedule(RetryDelay, () => Forget(() => EndAsync(reason), "retentativa de fim"));
                }
                _d.Log.Info($"fim {reason}: {result.Outcome}; nova tentativa em {RetryDelay.TotalSeconds:0} s");
                break;
        }
        RaiseChanged();
        // Depois do Changed: o menu já mostra o estado final quando o app começa a fechar.
        if (_quitRequested && result.LetsQuitGo) RaiseQuitReady();
    }

    private static string EndBody(EndReason reason) => reason switch
    {
        EndReason.Timer => "O tempo acabou. O notebook volta a dormir normalmente.",
        EndReason.BatteryFloor => "Bateria abaixo do piso de fim. O notebook volta a dormir normalmente.",
        EndReason.BatteryUnreadable => "Não deu para ler a bateria. O notebook volta a dormir normalmente.",
        EndReason.Backstop => "A sessão anterior foi desfeita.",
        EndReason.RecoveryUnavailable => "Recuperação indisponível: a sessão foi encerrada por segurança.",
        EndReason.StartFailed => "A sessão não pôde começar e foi desfeita.",
        _ => "O notebook volta a dormir normalmente.",
    };

    // ------------------------------------------------------------------ reconcile

    /// Na abertura do app: sessão válida ⇒ reaplica; expirada/ilegível ⇒ fim Timer;
    /// sem sessão e diário sujo ⇒ fim Backstop.
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        CancelTimer(ref _reconcileRetry);
        var (status, ended) = await RunCoreAsync("reconcile", tx => PerformReconcileAsync(tx, ct), ct).ConfigureAwait(false);
        if (status == RunStatus.Locked)
        {
            lock (_gate)
            {
                _reconcileRetry?.Dispose();
                _reconcileRetry = Schedule(RetryDelay, () => Forget(() => ReconcileAsync(), "retentativa de reconcile"));
            }
            _d.Log.Info($"reconcile adiado: lock ocupado; nova tentativa em {RetryDelay.TotalSeconds:0} s");
        }
        if (ended is { } e) AfterEnd(e.Reason, e.Result);
        RaiseChanged();
    }

    private async Task<(EndReason Reason, EndResult Result)?> PerformReconcileAsync(JournalTransaction tx, CancellationToken ct)
    {
        Session? session = null;
        var unreadable = false;
        try { session = _d.Store.LoadSession(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unreadable = true;
            _d.Log.Error($"sessão ilegível na abertura: {ex.Message}; tratada como expirada");
        }

        if (session is not null && !session.IsExpired(_d.Clock.Now))
        {
            await ReapplyAsync(tx, session, ct).ConfigureAwait(false);
            return null;
        }
        if (session is not null || unreadable)
            return (EndReason.Timer, await PerformEndAsync(tx, EndReason.Timer, sessionExisted: true, ct).ConfigureAwait(false));
        if (tx.Journal.IsDirty())
        {
            _d.Log.Info("sem sessão e diário sujo: desfazendo");
            return (EndReason.Backstop, await PerformEndAsync(tx, EndReason.Backstop, sessionExisted: false, ct).ConfigureAwait(false));
        }
        return null;
    }

    /// Sessão válida depois de reabrir o app: reaplica sem tocar nos valores anteriores (R2).
    /// Fail() dispara Changed, e um assinante (ex.: FloorRuleDriver) pode encerrar a sessão em
    /// linha, na mesma transação. Depois de cada Fail e de cada await o reapply confere se a
    /// sessão ainda é esta; se não for, para: nada de regravar LIDACTION, ligar o hold ou
    /// armar o timer de uma sessão que já foi desfeita.
    private async Task ReapplyAsync(JournalTransaction tx, Session session, CancellationToken ct)
    {
        var ticket = Volatile.Read(ref _endTicket);
        _current = session;
        bool Ended() => EndRequestedSince(ticket) || !ReferenceEquals(_current, session);

        if (!EnsureBackstopArmed("reconcile"))
            Fail("Backstop não armado: se o app fechar, ninguém desfaz as mudanças de energia.");
        if (Ended()) { LogReapplyAborted(); return; }

        try
        {
            var now = _d.Clock.Now;
            var current = _d.SleepGuard.ReadLidAction();
            var isZero = current.Ac == 0 && current.Dc == 0;
            var entry = tx.Journal.LidAction;
            var mayWrite = true;
            if (entry is null)
            {
                if (isZero)
                {
                    // Já é 0 e o diário não sabe o anterior: não inventa um valor para restaurar.
                    _d.Log.Info("LIDACTION já é 0 sem registro no diário; nada a gravar");
                }
                else
                {
                    tx.Write(tx.Journal with { LidAction = new LidActionEntry(current.SchemeId, current.Ac, current.Dc, now) });
                }
            }
            else if (entry.SchemeId != current.SchemeId)
            {
                mayWrite = false;
                Fail("O plano de energia ativo mudou durante a sessão: a ação da tampa não foi reaplicada.");
            }
            if (mayWrite && !isZero && !Ended()) _d.SleepGuard.WriteLidAction(current.SchemeId, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Não foi possível reaplicar a ação ao fechar a tampa: {ex.Message}");
        }
        if (Ended()) { LogReapplyAborted(); return; }

        SetHold(true);

        var lid = _d.LidState();
        if (lid == LidState.Open && tx.Journal.HasLidActions)
        {
            var result = await _d.LidUndo.UndoAsync(tx, ct).ConfigureAwait(false);
            SetRestoreProblems(result.Problems);
        }
        if (Ended())
        {
            // O fim chegou durante o LidUndo: o hold ligado acima é deste reapply e não pode sobrar.
            if (_current is null) SetHold(false);
            LogReapplyAborted();
            return;
        }
        _countdownPaused = lid == LidState.Closed;
        ArmEndTimer(session);
        _d.Log.Info($"sessão retomada até {session.EndsAt:yyyy-MM-ddTHH:mm:ssK} (tampa {lid})");
    }

    private void LogReapplyAborted() =>
        _d.Log.Info("reapply interrompido: a sessão foi encerrada no meio do caminho");

    // ------------------------------------------------------------------ Economia de energia e tampa

    public async Task SetEnergySaverAsync(bool on, EnergySaverCause cause, CancellationToken ct = default)
    {
        var ticket = Volatile.Read(ref _endTicket);
        await RunCoreAsync("energy saver", tx => Task.FromResult(on ? TurnEnergySaverOn(tx, cause, ticket) : TurnEnergySaverOff(tx)), ct)
            .ConfigureAwait(false);
        RaiseChanged();
    }

    private int TurnEnergySaverOn(JournalTransaction tx, EnergySaverCause cause, int ticket)
    {
        if (_current is null || EndRequestedSince(ticket)) return 0;
        if (tx.Journal.EnergySaver is not null) return 0; // já é nossa (R2)

        EnergySaverValues values;
        try { values = _d.EnergySaver.Read(); }
        catch (Exception ex)
        {
            _d.Log.Error($"Economia de energia: leitura falhou ({ex.Message}); não assumida");
            return 0;
        }
        if (values.DcThreshold >= 100)
        {
            _d.Log.Info("Economia de energia já estava ligada pelo usuário: não é assumida");
            return 0;
        }

        var entry = new EnergySaverEntry(values.SchemeId, values.DcThreshold, _d.Clock.Now, cause == EnergySaverCause.Battery ? "battery" : "lid");
        try { tx.Write(tx.Journal with { EnergySaver = entry }); }
        catch (StoreWriteException ex)
        {
            Fail($"Economia de energia não ligada: diário não gravado ({ex.Message})");
            return 0;
        }
        if (EndRequestedSince(ticket))
        {
            TryClearEnergySaverEntry(tx);
            return 0;
        }

        try { _d.EnergySaver.WriteDcThreshold(values.SchemeId, 100); }
        catch (Exception ex)
        {
            // Tenta voltar; só limpa a entrada se o valor lido for o anterior (senão o backstop ainda precisa dela).
            try { _d.EnergySaver.WriteDcThreshold(values.SchemeId, values.DcThreshold); }
            catch (Exception revertEx) { _d.Log.Error($"Economia de energia: reverter falhou ({revertEx.Message})"); }
            var back = false;
            try { back = _d.EnergySaver.Read().DcThreshold == values.DcThreshold; } catch { back = false; }
            if (back) TryClearEnergySaverEntry(tx);
            Fail($"Não foi possível ligar a Economia de energia: {ex.Message}");
            return 0;
        }

        if (cause == EnergySaverCause.Battery)
            _d.Notifier.Post("Economia de energia ligada", $"Bateria abaixo de {_d.Config.Current.LowPowerFloor}%.");
        _d.Log.Info($"Economia de energia ligada (causa {entry.Cause}; anterior {values.DcThreshold}%)");
        return 0;
    }

    private int TurnEnergySaverOff(JournalTransaction tx)
    {
        if (tx.Journal.EnergySaver is not { } entry) return 0;
        try { _d.EnergySaver.WriteDcThreshold(entry.SchemeId, entry.DcThresholdPrevious); }
        catch (Exception ex)
        {
            Fail($"Não foi possível desligar a Economia de energia: {ex.Message}");
            return 0;
        }
        TryClearEnergySaverEntry(tx);
        if (entry.Cause == "battery")
            _d.Notifier.Post("Economia de energia desligada", "A bateria voltou acima do piso.");
        _d.Log.Info($"Economia de energia desligada (causa {entry.Cause})");
        return 0;
    }

    private void TryClearEnergySaverEntry(JournalTransaction tx)
    {
        try { tx.Write(tx.Journal with { EnergySaver = null }); }
        catch (StoreWriteException ex) { Fail($"Diário não atualizado (Economia de energia): {ex.Message}"); }
    }

    /// Abertura da tampa: LidUndo sob a transação (retoma processos, áudio, tela).
    public async Task UndoLidActionsAsync(CancellationToken ct = default)
    {
        await RunCoreAsync("lid open", async tx =>
        {
            var result = await _d.LidUndo.UndoAsync(tx, ct).ConfigureAwait(false);
            SetRestoreProblems(result.Problems);
            return 0;
        }, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ transação exclusiva

    /// Fila serial + lock de arquivo (10 s) + diário lido do disco. Corrompido ⇒ não roda o
    /// corpo, LastError recebe a mensagem, devolve default. Lock ocupado ⇒ idem (nada muda, R6).
    /// Chamada de dentro de um corpo em curso reaproveita a mesma transação (sem deadlock).
    public async Task<T?> RunExclusiveAsync<T>(string label, Func<JournalTransaction, Task<T>> body, CancellationToken ct = default)
    {
        var (_, value) = await RunCoreAsync(label, body, ct).ConfigureAwait(false);
        RaiseChanged();
        return value;
    }

    private async Task<(RunStatus Status, T? Value)> RunCoreAsync<T>(string label, Func<JournalTransaction, Task<T>> body, CancellationToken ct)
    {
        if (_ambient.Value is { Active: true } outer)
            return (RunStatus.Ran, await body(outer.Tx).ConfigureAwait(false));

        await _queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IDisposable handle;
            try { handle = await _d.Lock.AcquireAsync(LockTimeout, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetError($"Recuperação ocupada por outro processo ({label}): nada mudou.");
                _d.Log.Error($"{label}: lock de recuperação indisponível ({ex.Message}); nada mudou");
                return (RunStatus.Locked, default);
            }

            using (handle)
            {
                Journal journal;
                try { journal = _d.Store.LoadJournal(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // R5: diário ilegível bloqueia tudo e fica intacto.
                    SetError($"Diário de recuperação ilegível: {ex.Message}. Nada é desfeito automaticamente; o arquivo foi mantido.");
                    _d.Log.Error($"{label}: diário ilegível, transação recusada ({ex.Message})");
                    return (RunStatus.JournalUnreadable, default);
                }

                _lastError = null;
                var tx = new JournalTransaction(_d.Store, journal);
                var ambient = new Ambient(tx);
                _ambient.Value = ambient;
                try
                {
                    return (RunStatus.Ran, await body(tx).ConfigureAwait(false));
                }
                finally
                {
                    ambient.Active = false;
                    _ambient.Value = null;
                    _energySaverOurs = tx.Journal.EnergySaver is not null;
                }
            }
        }
        finally
        {
            _queue.Release();
        }
    }

    // ------------------------------------------------------------------ contagem, avisos

    public void PauseCountdown()
    {
        if (_current is null || _countdownPaused) return;
        _countdownPaused = true;
        RaiseChanged();
    }

    public void ResumeCountdown()
    {
        if (!_countdownPaused) return;
        _countdownPaused = false;
        RaiseChanged();
    }

    /// Acrescenta uma linha ⚠ ao menu e ao log.
    public void Fail(string message)
    {
        lock (_gate) _failures.Add(message);
        _d.Log.Error(message);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ auxiliares

    private bool EndRequestedSince(int ticket) => Volatile.Read(ref _endTicket) != ticket;

    private bool EnsureBackstopArmed(string context)
    {
        try
        {
            var want = _d.BackstopDefinition;
            var state = _d.Backstop.Query(want);
            if (state == BackstopState.Armed) return true;
            _d.Log.Info($"backstop {state} ({context}): instalando a tarefa \"{want.TaskName}\"");
            _d.Backstop.Install(want);
            state = _d.Backstop.Query(want);
            if (state == BackstopState.Armed) return true;
            _d.Log.Error($"backstop continua {state} depois de instalar ({context})");
            return false;
        }
        catch (Exception ex)
        {
            _d.Log.Error($"backstop não armado ({context}): {ex.Message}");
            return false;
        }
    }

    private void SetHold(bool hold)
    {
        try { _d.SleepGuard.HoldSystemAwake(hold); }
        catch (Exception ex) { _d.Log.Error($"hold de execução ({hold}) falhou: {ex.Message}"); }
    }

    private void SetError(string message)
    {
        _lastError = message;
        _d.Log.Error(message);
    }

    private void SetRestoreProblems(IReadOnlyList<string> problems)
    {
        lock (_gate) _restoreProblems = [.. problems];
    }

    private void ArmEndTimer(Session session)
    {
        lock (_gate)
        {
            _endTimer?.Dispose();
            var delay = session.EndsAt - _d.Clock.Now;
            _endTimer = Schedule(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, OnDeadline);
        }
    }

    private void OnDeadline()
    {
        var current = _current;
        if (current is null) return;
        if (!current.IsExpired(_d.Clock.Now)) { ArmEndTimer(current); return; } // relógio adiantou o timer
        Forget(() => EndAsync(EndReason.Timer), "fim pelo timer");
    }

    private void CancelTimer(ref IDisposable? timer)
    {
        lock (_gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    /// Ação agendada roda sem a transação do contexto em que foi agendada.
    private IDisposable Schedule(TimeSpan delay, Action action) =>
        _d.Scheduler.Schedule(delay, () =>
        {
            _ambient.Value = null;
            action();
        });

    private void Forget(Func<Task> op, string label)
    {
        Task task;
        try { task = op(); }
        catch (Exception ex) { _d.Log.Error($"{label} falhou: {ex.Message}"); return; }
        task.ContinueWith(t => _d.Log.Error($"{label} falhou: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void RaiseQuitReady()
    {
        try { QuitReady?.Invoke(); }
        catch (Exception ex) { _d.Log.Error($"assinante de QuitReady falhou: {ex.Message}"); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _d.Log.Error($"assinante de Changed falhou: {ex.Message}"); }
    }
}
