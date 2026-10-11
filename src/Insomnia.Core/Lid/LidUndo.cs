// Desfaz as ações de tampa registradas no diário (desenho §7.2, R3, R8). Consumido pelo
// SessionManager (abertura da tampa) e pelo RecoveryEngine (fim de sessão e backstop).
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;

namespace Insomnia.Core.Lid;

/// Resultado só do áudio, para a retentativa de LidActions. FailedConnected = saídas
/// conectadas cuja restauração falhou (essas pedem nova tentativa por tempo; as
/// desconectadas esperam o DevicesChanged).
public sealed record AudioRestoreOutcome(IReadOnlyList<string> Problems, IReadOnlySet<string> NotConnected, int FailedConnected)
{
    public static AudioRestoreOutcome Nothing { get; } = new([], new HashSet<string>(), 0);
}

/// Ordem: (a) processos, (b) áudio, (c) acordar a tela. Cada parte: efeito primeiro, depois
/// tx.Write sem as entradas resolvidas; falha ao gravar deixa as entradas (repetir é seguro:
/// resume só com identidade e só se parado; áudio só mexe se ainda mudo). Nunca lança por
/// falha de sistema: tudo vira Problems.
///
/// wakeDisplay = false é o backstop. Ele roda a cada minuto pelo Agendador, sem tampa nem
/// sessão para conferir, e um diário com resíduo que não se resolve sozinho (saída de áudio
/// desconectada, processo não verificável) o faria acordar a tela a cada minuto, sem prazo.
/// Acordar a tela é reação à abertura da tampa, que só o app vê. O padrão (true) é o do app.
public sealed class LidUndo(IProcessInspector inspector, IProcessSignals signals, IAudioControl audio, IDisplayPower display, ILog log, bool wakeDisplay = true) : ILidUndo
{
    /// false ⇒ UndoAsync nunca chama IDisplayPower.Wake (backstop).
    public bool WakesDisplay => wakeDisplay;

    public Task<LidUndoResult> UndoAsync(JournalTransaction tx, CancellationToken ct)
    {
        var problems = new List<string>();
        ResumeProcesses(tx, problems);
        var audioOutcome = RestoreAudio(tx);
        problems.AddRange(audioOutcome.Problems);
        if (wakeDisplay)
        {
            try { display.Wake(); }
            catch (Exception ex) { log.Error($"tela: acordar falhou ({ex.Message})"); }
        }
        return Task.FromResult(new LidUndoResult(problems, audioOutcome.NotConnected));
    }

    /// (a) Retoma o que é comprovadamente nosso e tira do diário o que não é mais.
    /// Entrada sem identidade de processo parado fica (não verificável, R8).
    private void ResumeProcesses(JournalTransaction tx, List<string> problems)
    {
        var journal = tx.Journal;
        if (journal.FrozenProcesses.Length == 0 && !journal.DockerFrozen) return;

        var kept = new List<FrozenProcess>();
        var resumed = 0;
        foreach (var entry in journal.FrozenProcesses)
        {
            ProcessInfo? current;
            try { current = inspector.Lookup(entry.Pid); }
            catch (Exception ex)
            {
                kept.Add(entry);
                problems.Add($"{entry.Name} (pid {entry.Pid}): não consegui consultar o processo ({ex.Message})");
                continue;
            }

            switch (ProcessControl.ResumeDecision(entry, current))
            {
                case ResumeVerdict.Gone:
                    log.Info($"descongelar: {entry.Name} (pid {entry.Pid}) não é mais nosso (saiu, rodando ou pid reusado)");
                    break;
                case ResumeVerdict.Unverifiable:
                    kept.Add(entry);
                    problems.Add($"{entry.Name} (pid {entry.Pid}) está parado sem identidade no diário: não verificável, não foi retomado");
                    break;
                case ResumeVerdict.Resume:
                    bool ok;
                    try { ok = signals.Resume(entry.Pid); }
                    catch (Exception ex) { ok = false; log.Error($"descongelar {entry.Name} (pid {entry.Pid}): {ex.Message}"); }
                    if (ok) resumed++;
                    else
                    {
                        kept.Add(entry);
                        problems.Add($"Não consegui retomar {entry.Name} (pid {entry.Pid})");
                    }
                    break;
            }
        }

        var dockerStill = journal.DockerFrozen && kept.Any(k => k.Group == DockerRule.GroupName);
        if (kept.Count == journal.FrozenProcesses.Length && dockerStill == journal.DockerFrozen) return;
        try
        {
            tx.Write(journal with { FrozenProcesses = [.. kept], DockerFrozen = dockerStill });
            log.Info($"descongelar: {resumed} processo(s) retomado(s), {kept.Count} ficam no diário");
        }
        catch (StoreWriteException ex)
        {
            problems.Add($"Processos retomados, mas o diário não gravou ({ex.Message}); a próxima tentativa repete com segurança");
        }
    }

    /// (b) Restaura cada saída pelo deviceId gravado (não pela padrão atual). Desconectada ⇒
    /// fica e entra em NotConnected. Usuário já desmutou ⇒ só sai do diário. Falha ⇒ fica.
    public AudioRestoreOutcome RestoreAudio(JournalTransaction tx)
    {
        var journal = tx.Journal;
        if (journal.SavedAudioOutputs.Length == 0) return AudioRestoreOutcome.Nothing;

        var problems = new List<string>();
        var notConnected = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<SavedAudioOutput>();
        var failedConnected = 0;
        foreach (var saved in journal.SavedAudioOutputs)
        {
            var label = saved.Name ?? saved.DeviceId;
            try
            {
                var current = audio.Read(saved.DeviceId);
                if (current.Muted)
                {
                    audio.SetVolume(saved.DeviceId, saved.Volume);
                    audio.SetMute(saved.DeviceId, saved.Muted);
                    log.Info($"áudio: {label} restaurada (volume {saved.Volume:0.00}, mudo {saved.Muted})");
                }
                else
                {
                    log.Info($"áudio: {label} já foi desmutada pelo usuário; deixada como está");
                }
            }
            catch (AudioDeviceMissingException)
            {
                kept.Add(saved);
                notConnected.Add(saved.DeviceId);
                log.Info($"áudio: {label} desconectada; espera reconectar");
            }
            catch (Exception ex)
            {
                kept.Add(saved);
                failedConnected++;
                problems.Add($"Saída de áudio {label} não foi restaurada ({ex.Message})");
            }
        }

        if (kept.Count < journal.SavedAudioOutputs.Length)
        {
            try { tx.Write(journal with { SavedAudioOutputs = [.. kept] }); }
            catch (StoreWriteException ex)
            {
                problems.Add($"Áudio restaurado, mas o diário não gravou ({ex.Message}); a próxima tentativa repete com segurança");
            }
        }
        return new AudioRestoreOutcome(problems, notConnected, failedConnected);
    }
}
