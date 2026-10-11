using System.Diagnostics;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Store;

/// Lock de recuperação entre processos (app × backstop): FileStream com FileShare.None
/// sobre .recovery.lock. O Windows solta o handle no Dispose ou quando o processo morre,
/// então não existe lock órfão. O arquivo nunca é apagado.
///
/// Mutex nomeado foi descartado: tem afinidade de thread, e o dono do lock aqui atravessa
/// awaits. Qualquer erro ao abrir conta como "ocupado" (falha fechado, R6).
public sealed class FileRecoveryLock(string path) : IRecoveryLock
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public IDisposable? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            return new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryAcquire() is { } held) return held;
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new RecoveryLockBusyException();
            await Task.Delay(left < RetryInterval ? left : RetryInterval, ct).ConfigureAwait(false);
        }
    }
}
