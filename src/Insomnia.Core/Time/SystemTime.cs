// Implementações reais de IClock e IScheduler, para a raiz de composição (app e backstop).
// Os testes usam FakeClock/FakeScheduler.
using System.Collections.Concurrent;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Time;

/// Relógio do sistema (hora local com deslocamento; o JSON grava sempre em UTC).
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// IScheduler sobre System.Threading.Timer: a ação roda numa thread do pool, uma vez.
/// Exceção da ação vai para o log (sem log, é engolida): exceção não tratada num
/// callback de timer derrubaria o processo, e com ele o hold de energia.
public sealed class ThreadPoolScheduler(ILog? log = null) : IScheduler
{
    // Timer sem referência viva pode ser coletado antes de disparar: o dicionário segura cada um até o fim.
    private readonly ConcurrentDictionary<Entry, byte> _pending = new();

    /// Quantas ações ainda vão rodar (diagnóstico).
    public int PendingCount => _pending.Count;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var entry = new Entry(this, action);
        _pending[entry] = 0;
        entry.Start(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        return entry;
    }

    private void Report(Exception ex) => log?.Error($"tarefa agendada falhou: {ex.Message}");

    private sealed class Entry(ThreadPoolScheduler owner, Action action) : IDisposable
    {
        private Timer? _timer;
        private int _done;

        public void Start(TimeSpan delay)
        {
            _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private void Fire()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Release();
            try { action(); }
            catch (Exception ex) { owner.Report(ex); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Release();
        }

        private void Release()
        {
            _timer?.Dispose();
            owner._pending.TryRemove(this, out _);
        }
    }
}
