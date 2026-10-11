// Decisões puras sobre posse de processos (desenho §7.2, R8). Nada aqui sinaliza: quem
// sinaliza é LidActions/LidUndo, depois de perguntar a estas funções.
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;

namespace Insomnia.Core.Lid;

/// O que fazer com uma entrada do diário na hora de retomar.
public enum ResumeVerdict
{
    /// Não é (mais) nosso: processo sumiu, está rodando ou o pid foi reusado. A entrada sai do diário sem sinal.
    Gone,
    /// Entrada sem identidade (provisória) de processo parado: nunca é sinalizada e fica no diário (R8).
    Unverifiable,
    /// Mesma identidade e parado: retomar.
    Resume,
}

/// Prova de posse = pid + identidade de criação + BootId + ppid esperado. Não existe
/// checar-e-sinalizar atômico por pid; a janela de corrida é de um processo por vez.
public static class ProcessControl
{
    /// Antes do suspend: o processo atual ainda é o que o planejador viu (mesma identidade
    /// e mesmo ppid), tem identidade verificável e NÃO está parado (parado = não é nosso).
    public static bool IsSuspendable(ProcessInfo expected, ProcessInfo? current)
    {
        if (current is null || expected.Pid <= 0 || current.Pid != expected.Pid) return false;
        if (expected.Identity is not { } want || string.IsNullOrEmpty(want.BootId)) return false;
        if (current.Identity is not { } have || !SameIdentity(want, have)) return false;
        if (current.ParentPid != expected.ParentPid) return false;
        return !current.IsSuspended;
    }

    /// Na abertura/restauração: ausente ou rodando ⇒ Gone; sem identidade ⇒ Unverifiable;
    /// identidade diferente (pid reusado) ⇒ Gone; igual e parado ⇒ Resume.
    public static ResumeVerdict ResumeDecision(FrozenProcess entry, ProcessInfo? current)
    {
        if (current is null || !current.IsSuspended) return ResumeVerdict.Gone;
        if (!entry.HasIdentity) return ResumeVerdict.Unverifiable;
        if (current.Identity is not { } have) return ResumeVerdict.Gone;
        var want = new ProcessIdentity(entry.CreationTime!.Value, entry.BootId!);
        return SameIdentity(want, have) ? ResumeVerdict.Resume : ResumeVerdict.Gone;
    }

    /// Desfazer um suspend recém-enviado (promoção no diário falhou): mesma checagem de
    /// identidade e ppid, mas sinaliza mesmo que o processo ainda não apareça como parado.
    public static bool CancelDecision(ProcessInfo expected, ProcessInfo? current)
    {
        if (current is null || current.Pid != expected.Pid) return false;
        if (expected.Identity is not { } want || string.IsNullOrEmpty(want.BootId)) return false;
        if (current.Identity is not { } have || !SameIdentity(want, have)) return false;
        return current.ParentPid == expected.ParentPid;
    }

    /// Identidade igual com a resolução do diário: o state.json grava a criação em segundos
    /// inteiros (UTC, §3), então o que voltou do disco perdeu a fração. Comparar com fração
    /// faria todo processo congelado parecer "pid reusado" depois de um restart e nunca ser
    /// retomado. BootId compara exato.
    public static bool SameIdentity(ProcessIdentity a, ProcessIdentity b) =>
        string.Equals(a.BootId, b.BootId, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(a.BootId)
        && TruncateToSecond(a.CreationTime) == TruncateToSecond(b.CreationTime);

    /// Entrada promovida para o diário, já na resolução em que ele será lido de volta.
    public static FrozenProcess Promoted(ProcessInfo process, string group)
    {
        var id = process.Identity ?? throw new ArgumentException("processo sem identidade não é promovido", nameof(process));
        return new FrozenProcess(process.Pid, process.Name, TruncateToSecond(id.CreationTime), id.BootId, group);
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}
