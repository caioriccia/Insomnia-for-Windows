// O que roda em "Insomnia.exe --backstop", a cada minuto, pelo Agendador (desenho §6.2 "Backstop", R9).
// Desfaz o diário quando a sessão acabou e o app não está mais lá para fazê-lo.
// Nunca renova sessão, nunca notifica (só log), nunca toca config.
namespace Insomnia.Core.Backstop;

using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Session;

public sealed class BackstopRunner
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    /// EX_TEMPFAIL: lock ocupado, nada mudou; o Agendador tenta no minuto seguinte (R6).
    public const int ExitLocked = 75;
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly IStore _store;
    private readonly IRecoveryLock _lock;
    private readonly RecoveryEngine _engine;
    private readonly IClock _clock;
    private readonly ILog _log;

    public BackstopRunner(IStore store, IRecoveryLock @lock, RecoveryEngine engine, IClock clock, ILog log)
    {
        _store = store;
        _lock = @lock;
        _engine = engine;
        _clock = clock;
        _log = log;
    }

    /// 0 ok/nada a fazer · 1 falhas (evidência mantida) · 75 lock ocupado.
    /// force: age mesmo com sessão válida (desinstalador).
    public async Task<int> RunAsync(bool force, CancellationToken ct)
    {
        IDisposable handle;
        try { handle = await _lock.AcquireAsync(LockTimeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info($"backstop: lock de recuperação ocupado ({ex.Message}); nada mudou, nova tentativa no próximo minuto");
            return ExitLocked;
        }

        using (handle)
        {
            // Sessão: válida e sem --force ⇒ não age (R9). Ilegível = expirada (e será movida, nunca apagada).
            Session? session = null;
            var unreadable = false;
            try { session = _store.LoadSession(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable = true;
                _log.Error($"backstop: sessão ilegível ({ex.Message}); tratada como expirada");
            }

            if (session is not null && !session.IsExpired(_clock.Now) && !force)
            {
                _log.Info($"backstop: sessão válida até {session.EndsAt:yyyy-MM-ddTHH:mm:ssK}, nada a fazer");
                return ExitOk;
            }

            Journal journal;
            try { journal = _store.LoadJournal(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // R5: nada é decidido nem escrito sobre um diário ilegível.
                _log.Error($"backstop: diário ilegível: nada desfeito, evidência mantida ({ex.Message})");
                return ExitFailed;
            }

            // Sem argumento de propósito: saída de áudio desconectada ou processo não verificável
            // mantêm o diário sujo, e o backstop volta a tentar a cada minuto (o fone pode
            // reconectar). É seguro repetir porque o LidUndo do backstop nunca acorda a tela
            // (BackstopCommand monta com wakeDisplay: false); o resto só age quando há o que desfazer.
            if (journal.IsDirty())
            {
                _log.Info(force ? "backstop --force: restaurando a partir do diário" : "backstop: sessão acabou e o diário está sujo; restaurando");
                var tx = new JournalTransaction(_store, journal);
                var report = await _engine.RestoreAllAsync(tx, ct).ConfigureAwait(false);
                if (!report.Complete)
                {
                    foreach (var problem in report.Problems) _log.Error($"backstop: {problem}");
                    // session.json fica: é a evidência de que ainda há o que desfazer.
                    _log.Error("backstop: restauração incompleta; session.json mantido, nova tentativa no próximo minuto");
                    return ExitFailed;
                }
            }
            else if (session is null && !unreadable)
            {
                return ExitOk; // limpo e sem sessão
            }

            // Tudo desfeito: some com a sessão (a ilegível vai para o lado, nunca é apagada).
            try
            {
                if (unreadable)
                {
                    var moved = _store.MoveAsideUnreadableSession();
                    _log.Info($"backstop: sessão ilegível movida para {moved ?? "(nada a mover)"}");
                }
                else if (session is not null)
                {
                    _store.DeleteSession();
                    _log.Info("backstop: session.json apagado");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"backstop: não foi possível remover a sessão ({ex.Message})");
                return ExitFailed;
            }
            return ExitOk;
        }
    }
}
