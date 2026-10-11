// CONTRATO CONGELADO (desenho §6.1): enums de desfecho, EndResult, SessionDeps, RestoreReport.
// Outras frentes (tampa, app-ui, diagnóstico) consomem estes nomes exatamente assim.
namespace Insomnia.Core.Session;

// Os usings ficam DENTRO do namespace: fora dele, o nome simples "Session" acharia este
// namespace antes do record Model.Session.
using Insomnia.Core.Abstractions;

/// Por que a sessão acabou.
public enum EndReason { Timer, User, Quit, BatteryFloor, BatteryUnreadable, Backstop, RecoveryUnavailable, StartFailed }

public enum StartOutcome { Started, RefusedQuitRequested, RefusedEndPending, RefusedAlreadyActive, RefusedSessionFileUnreadable, Locked, JournalUnreadable, BackstopNotArmed, PowerWriteFailed }

public enum ExtendOutcome { Extended, NoSession, Locked, JournalUnreadable, Failed }

public enum EndOutcome { Restored, Incomplete, Locked, JournalUnreadable, SessionRetained }

/// Desfecho de um fim. LetsQuitGo: o app pode sair (tudo desfeito, ou o backstop armado cobre o resto).
public sealed record EndResult(EndOutcome Outcome, bool AgentArmed, IReadOnlyList<string> Problems)
{
    public bool LetsQuitGo => Outcome is EndOutcome.Restored || (Outcome is EndOutcome.Incomplete && AgentArmed);
}

/// Causa da Economia de energia: bateria abaixo do piso, ou tampa fechada.
public enum EnergySaverCause { Battery, Lid }

/// Tudo de que o SessionManager precisa, injetado (fakes nos testes, adaptadores reais no app).
public sealed record SessionDeps(IStore Store, IRecoveryLock Lock, IClock Clock, IScheduler Scheduler, ILog Log, INotifier Notifier,
    ISleepGuard SleepGuard, IEnergySaver EnergySaver, IBackstopScheduler Backstop, BackstopDefinition BackstopDefinition,
    ILidUndo LidUndo, Func<LidState> LidState, IConfigSource Config);

/// Resultado de RestoreAll. Complete ⇔ o diário ficou limpo (saída de áudio desconectada não conta).
public sealed record RestoreReport(bool Complete, IReadOnlyList<string> Problems);
