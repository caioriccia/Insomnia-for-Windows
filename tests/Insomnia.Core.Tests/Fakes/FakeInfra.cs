// Fakes de infraestrutura (desenho §5.1). Nomes congelados; tudo público, com gravação de
// chamadas e botões de falha. Nenhum fake toca disco, relógio real ou sistema.
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Tests.Fakes;

using Insomnia.Core.Model;

/// Linha do tempo opcional compartilhada entre fakes: prova ORDEM entre efeitos de fakes
/// diferentes (ex.: "store.SaveJournal" antes de "sleep.WriteLidAction"). Cada fake que
/// recebe uma Timeline acrescenta "&lt;fake&gt;.&lt;método&gt;[(args)]" a cada chamada.
public sealed class Timeline
{
    private readonly object _gate = new();
    public List<string> Events { get; } = [];

    public void Add(string evt) { lock (_gate) Events.Add(evt); }

    /// Índice do primeiro evento que começa com prefix (-1 se não houver).
    public int IndexOf(string prefix) { lock (_gate) return Events.FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal)); }

    /// Índice do último evento que começa com prefix (-1 se não houver).
    public int LastIndexOf(string prefix) { lock (_gate) return Events.FindLastIndex(e => e.StartsWith(prefix, StringComparison.Ordinal)); }
}

public sealed class FakeClock(DateTimeOffset? start = null) : IClock
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now { get; set; } = start ?? DefaultStart;
    public void Advance(TimeSpan by) => Now += by;
}

/// Agenda em tempo virtual. Advance(by) executa, em ordem de vencimento (empate: ordem de
/// agendamento), tudo que vence até o novo instante, inclusive o que for agendado durante a
/// execução. Com um FakeClock ligado, o relógio acompanha cada vencimento.
public sealed class FakeScheduler(FakeClock? clock = null) : IScheduler
{
    private readonly object _gate = new();
    private readonly List<Item> _items = [];
    private long _seq;

    private DateTimeOffset _now = FakeClock.DefaultStart;

    /// Instante virtual atual (o do relógio ligado, se houver).
    public DateTimeOffset Now => CurrentTime();

    /// Atrasos pedidos em cada Schedule, na ordem.
    public List<TimeSpan> Requested { get; } = [];

    public int PendingCount { get { lock (_gate) return _items.Count(i => !i.Cancelled); } }

    /// Atrasos (a partir de agora) do que ainda está pendente, em ordem de vencimento.
    public IReadOnlyList<TimeSpan> PendingDelays
    {
        get { lock (_gate) return [.. _items.Where(i => !i.Cancelled).OrderBy(i => i.Due).ThenBy(i => i.Seq).Select(i => i.Due - CurrentTime())]; }
    }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        lock (_gate)
        {
            Requested.Add(delay);
            var item = new Item(this, CurrentTime() + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), _seq++, action);
            _items.Add(item);
            return item;
        }
    }

    public void Advance(TimeSpan by)
    {
        var target = CurrentTime() + by;
        while (true)
        {
            Item? next;
            lock (_gate)
            {
                next = _items.Where(i => !i.Cancelled && i.Due <= target).OrderBy(i => i.Due).ThenBy(i => i.Seq).FirstOrDefault();
                if (next is null) break;
                _items.Remove(next);
            }
            SetTime(next.Due);
            next.Action();
        }
        SetTime(target);
    }

    /// Executa tudo o que está pendente agora, sem avançar o tempo (atraso zero).
    public void RunDue() => Advance(TimeSpan.Zero);

    private DateTimeOffset CurrentTime() => clock?.Now ?? _now;

    private void SetTime(DateTimeOffset t)
    {
        if (clock is not null) { if (clock.Now < t) clock.Now = t; }
        else if (_now < t) _now = t;
    }

    private sealed class Item(FakeScheduler owner, DateTimeOffset due, long seq, Action action) : IDisposable
    {
        public DateTimeOffset Due { get; } = due;
        public long Seq { get; } = seq;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }
        public void Dispose() { lock (owner._gate) { Cancelled = true; owner._items.Remove(this); } }
    }
}

/// IStore em memória. Null em SessionOnDisk/JournalOnDisk/ConfigOnDisk = arquivo ausente.
/// Corrupt* simula arquivo ilegível: Load* lança StoreCorruptException e o "arquivo" fica
/// como está; qualquer Save* que chegar ao diário corrompido aparece em JournalWrites (o
/// teste afirma que não chegou). Operations registra cada chamada, na ordem.
public sealed class FakeStore(Timeline? timeline = null) : IStore
{
    public const string SessionPath = "fake/session.json";
    public const string JournalPath = "fake/state.json";
    public const string ConfigPath = "fake/config.json";

    private readonly object _gate = new();

    public Session? SessionOnDisk { get; set; }
    public Journal? JournalOnDisk { get; set; }
    public Config? ConfigOnDisk { get; set; }

    public bool CorruptSession { get; set; }
    public bool CorruptJournal { get; set; }
    public bool CorruptConfig { get; set; }

    /// Só a próxima SaveJournal falha (StoreWriteException); depois volta ao normal.
    public bool FailNextJournalWrite { get; set; }
    public bool FailAllJournalWrites { get; set; }
    /// Falha quando o predicado diz sim (para falhar só uma escrita específica).
    public Func<Journal, bool>? FailJournalWriteIf { get; set; }
    public bool FailSessionWrites { get; set; }
    public bool FailSessionDelete { get; set; }
    public bool FailMoveAside { get; set; }
    public bool FailConfigWrites { get; set; }

    /// Cada diário gravado com sucesso, na ordem.
    public List<Journal> JournalWrites { get; } = [];
    /// Cada sessão gravada com sucesso, na ordem.
    public List<Session> SessionWrites { get; } = [];
    public List<Config> ConfigWrites { get; } = [];
    /// Caminhos devolvidos por MoveAsideUnreadableSession.
    public List<string> MovedAside { get; } = [];
    /// "LoadSession", "SaveSession", "DeleteSession", "MoveAside", "LoadJournal", "SaveJournal", "LoadConfig", "SaveConfig".
    public List<string> Operations { get; } = [];

    public Session? LoadSession()
    {
        Record("LoadSession");
        lock (_gate)
        {
            if (CorruptSession) throw new StoreCorruptException(SessionPath, "corrompido (fake)");
            return SessionOnDisk;
        }
    }

    public void SaveSession(Session session)
    {
        Record("SaveSession");
        lock (_gate)
        {
            if (FailSessionWrites) throw new StoreWriteException(SessionPath, new IOException("falha simulada"));
            SessionOnDisk = session;
            CorruptSession = false;
            SessionWrites.Add(session);
        }
    }

    public void DeleteSession()
    {
        Record("DeleteSession");
        lock (_gate)
        {
            if (FailSessionDelete) throw new StoreWriteException(SessionPath, new IOException("falha simulada"));
            SessionOnDisk = null;
            CorruptSession = false;
        }
    }

    public string? MoveAsideUnreadableSession()
    {
        Record("MoveAside");
        lock (_gate)
        {
            if (FailMoveAside) throw new StoreWriteException(SessionPath, new IOException("falha simulada"));
            if (SessionOnDisk is null && !CorruptSession) return null;
            var target = $"{SessionPath}.unreadable-{MovedAside.Count}";
            MovedAside.Add(target);
            SessionOnDisk = null;
            CorruptSession = false;
            return target;
        }
    }

    public Journal LoadJournal()
    {
        Record("LoadJournal");
        lock (_gate)
        {
            if (CorruptJournal) throw new StoreCorruptException(JournalPath, "corrompido (fake)");
            return JournalOnDisk ?? Journal.Clean;
        }
    }

    public void SaveJournal(Journal journal)
    {
        Record("SaveJournal");
        lock (_gate)
        {
            if (FailNextJournalWrite)
            {
                FailNextJournalWrite = false;
                throw new StoreWriteException(JournalPath, new IOException("falha simulada"));
            }
            if (FailAllJournalWrites || (FailJournalWriteIf?.Invoke(journal) ?? false))
                throw new StoreWriteException(JournalPath, new IOException("falha simulada"));
            JournalOnDisk = journal;
            JournalWrites.Add(journal);
        }
    }

    public Config LoadConfig()
    {
        Record("LoadConfig");
        lock (_gate)
        {
            if (CorruptConfig) throw new StoreCorruptException(ConfigPath, "corrompido (fake)");
            if (ConfigOnDisk is null)
            {
                ConfigOnDisk = Config.Default;
                ConfigWrites.Add(ConfigOnDisk);
            }
            return ConfigOnDisk;
        }
    }

    public void SaveConfig(Config config)
    {
        Record("SaveConfig");
        lock (_gate)
        {
            if (FailConfigWrites) throw new StoreWriteException(ConfigPath, new IOException("falha simulada"));
            ConfigOnDisk = config;
            CorruptConfig = false;
            ConfigWrites.Add(config);
        }
    }

    private void Record(string op)
    {
        lock (_gate) Operations.Add(op);
        timeline?.Add("store." + op);
    }
}

/// Held = true força "ocupado" (outro processo segura). Sem Held, um TryAcquire/AcquireAsync
/// só falha enquanto um handle anterior não foi liberado. AcquireAsync não espera de verdade:
/// ocupado ⇒ RecoveryLockBusyException na hora.
public sealed class FakeRecoveryLock : IRecoveryLock
{
    private readonly object _gate = new();
    public bool Held { get; set; }
    public bool IsAcquired { get; private set; }
    public int Acquisitions { get; private set; }
    public int BusyRefusals { get; private set; }
    public List<TimeSpan> RequestedTimeouts { get; } = [];

    public IDisposable? TryAcquire()
    {
        lock (_gate)
        {
            if (Held || IsAcquired) { BusyRefusals++; return null; }
            IsAcquired = true;
            Acquisitions++;
            return new Handle(this);
        }
    }

    public Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) RequestedTimeouts.Add(timeout);
        return TryAcquire() is { } h ? Task.FromResult(h) : Task.FromException<IDisposable>(new RecoveryLockBusyException());
    }

    private sealed class Handle(FakeRecoveryLock owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner.IsAcquired = false;
        }
    }
}

public sealed class FakeLog : ILog
{
    private readonly object _gate = new();
    /// "[info] msg" / "[error] msg", na ordem.
    public List<string> Lines { get; } = [];
    public List<string> Infos { get; } = [];
    public List<string> Errors { get; } = [];

    public void Info(string message) { lock (_gate) { Lines.Add("[info] " + message); Infos.Add(message); } }
    public void Error(string message) { lock (_gate) { Lines.Add("[error] " + message); Errors.Add(message); } }

    public bool Contains(string fragment) { lock (_gate) return Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal)); }
}

public sealed class FakeNotifier : INotifier
{
    private readonly object _gate = new();
    public List<(string Title, string Body)> Posts { get; } = [];
    public void Post(string title, string body) { lock (_gate) Posts.Add((title, body)); }
}

/// IConfigSource em memória. Update aplica, normaliza, conta e dispara Changed (como o real).
public sealed class FakeConfigSource(Config? initial = null) : IConfigSource
{
    public Config Current { get; private set; } = initial ?? Config.Default;
    public event Action? Changed;
    public int Updates { get; private set; }

    public void Update(Func<Config, Config> change)
    {
        Current = change(Current).Normalized();
        Updates++;
        Changed?.Invoke();
    }

    /// Troca o config sem passar por Update (simula edição externa) e dispara Changed.
    public void Set(Config config)
    {
        Current = config;
        Changed?.Invoke();
    }
}
