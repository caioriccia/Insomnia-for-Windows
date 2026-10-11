// Instância única da UI (desenho §9): Mutex "Local\Insomnia.Ui". A segunda instância não
// abre outra bandeja: sinaliza o evento nomeado e sai; a primeira, ao receber, abre o popover.
namespace Insomnia.App.Composition;

public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\Insomnia.Ui";
    public const string ShowEventName = @"Local\Insomnia.Ui.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// Dono da UI, ou null se outra instância já é (e foi avisada para abrir o popover).
    public static SingleInstance? TryClaim()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExisting();
            return null;
        }
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
    }

    /// Avisa a instância em execução (sem efeito se ela não tem o evento).
    public static bool SignalExisting()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) return false;
        using (existing) return existing.Set();
    }

    /// `action` roda numa thread do pool a cada sinal da segunda instância.
    public void OnShowRequested(Action action)
    {
        _wait?.Unregister(null);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* liberado em outra thread: fechar o handle basta */ }
        _mutex.Dispose();
    }
}
