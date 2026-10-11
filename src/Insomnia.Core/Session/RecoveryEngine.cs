// Desfaz tudo o que o diário registra (desenho §6.2 "RestoreAll", invariantes R3 e R7).
// Usado pelo fim de sessão no app e pelo backstop: o mesmo código nos dois lados.
namespace Insomnia.Core.Session;

using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;

public sealed class RecoveryEngine
{
    private readonly ISleepGuard _sleepGuard;
    private readonly IEnergySaver _energySaver;
    private readonly ILidUndo _lidUndo;
    private readonly IClock _clock;
    private readonly ILog _log;

    public RecoveryEngine(ISleepGuard sleepGuard, IEnergySaver energySaver, ILidUndo lidUndo, IClock clock, ILog log)
    {
        _sleepGuard = sleepGuard;
        _energySaver = energySaver;
        _lidUndo = lidUndo;
        _clock = clock;
        _log = log;
    }

    /// Ordem fixa: (1) LIDACTION, (2) Economia de energia, (3) ações de tampa (LidUndo).
    /// Cada passo é efeito → tx.Write sem a entrada; efeito que falha ou escrita que falha
    /// MANTÉM a entrada, e repetir é seguro (reescreve o mesmo valor anterior). O diário é o
    /// da transação, que o chamador carregou do DISCO sob o lock — nunca uma cópia em memória.
    public async Task<RestoreReport> RestoreAllAsync(JournalTransaction tx, CancellationToken ct)
    {
        var problems = new List<string>();
        IReadOnlySet<string> notConnected = new HashSet<string>();

        if (tx.Journal.LidAction is { } lid)
        {
            try
            {
                _sleepGuard.WriteLidAction(lid.SchemeId, lid.AcPrevious, lid.DcPrevious);
                _log.Info($"restaurado: ação da tampa AC {lid.AcPrevious} / DC {lid.DcPrevious} (esquema {lid.SchemeId})");
                Clear(tx, j => j with { LidAction = null }, "ação da tampa", problems);
            }
            catch (Exception ex)
            {
                problems.Add($"Ação ao fechar a tampa não restaurada (AC {lid.AcPrevious}, DC {lid.DcPrevious}): {ex.Message}");
                _log.Error($"restauração da ação da tampa falhou: {ex.Message}");
            }
        }

        if (tx.Journal.EnergySaver is { } saver)
        {
            try
            {
                _energySaver.WriteDcThreshold(saver.SchemeId, saver.DcThresholdPrevious);
                _log.Info($"restaurado: limiar da Economia de energia {saver.DcThresholdPrevious}% (esquema {saver.SchemeId})");
                Clear(tx, j => j with { EnergySaver = null }, "Economia de energia", problems);
            }
            catch (Exception ex)
            {
                problems.Add($"Economia de energia não restaurada ({saver.DcThresholdPrevious}%): {ex.Message}");
                _log.Error($"restauração da Economia de energia falhou: {ex.Message}");
            }
        }

        if (tx.Journal.HasLidActions)
        {
            try
            {
                var result = await _lidUndo.UndoAsync(tx, ct).ConfigureAwait(false);
                problems.AddRange(result.Problems);
                notConnected = result.AudioDevicesNotConnected;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // O contrato diz que LidUndo não lança; se lançar, as entradas ficam no diário.
                problems.Add($"Ações da tampa não desfeitas: {ex.Message}");
                _log.Error($"desfazer ações da tampa falhou: {ex.Message}");
            }
        }

        var complete = !tx.Journal.IsDirty(notConnected);
        if (complete) _log.Info($"restauração completa ({_clock.Now:yyyy-MM-ddTHH:mm:ssK})");
        else _log.Error($"restauração incompleta: {problems.Count} problema(s); o diário mantém o que falta");
        return new RestoreReport(complete, problems);
    }

    /// Grava o diário sem a entrada já desfeita. Falha de escrita deixa a entrada (R3).
    private void Clear(JournalTransaction tx, Func<Journal, Journal> without, string what, List<string> problems)
    {
        try { tx.Write(without(tx.Journal)); }
        catch (StoreWriteException ex)
        {
            problems.Add($"Diário não atualizado depois de restaurar {what}: {ex.Message}");
            _log.Error($"{what} restaurada, mas o diário não foi gravado: {ex.Message}");
        }
    }
}
