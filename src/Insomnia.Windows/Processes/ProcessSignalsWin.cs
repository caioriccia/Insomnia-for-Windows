using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Processes;

/// IProcessSignals real: NtSuspendProcess/NtResumeProcess (ntdll), o equivalente de
/// SIGSTOP/SIGCONT. Handle com PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION.
/// A decisão de QUEM sinalizar (identidade exata, R8) é do Core; aqui só o efeito.
/// Nunca lança: qualquer falha devolve false. Recusa pid ≤ 4 e o próprio processo.
/// Resume em processo não suspenso é no-op no Windows (a contagem de suspensão não fica negativa).
public sealed class ProcessSignalsWin : IProcessSignals
{
    private static readonly int Self = Environment.ProcessId;

    public bool Suspend(int pid) => Signal(pid, suspend: true);

    public bool Resume(int pid) => Signal(pid, suspend: false);

    private static bool Signal(int pid, bool suspend)
    {
        if (pid <= 4 || pid == Self) return false;
        var handle = ProcessNative.OpenProcess(ProcessNative.ProcessSuspendResume | ProcessNative.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var status = suspend ? ProcessNative.NtSuspendProcess(handle) : ProcessNative.NtResumeProcess(handle);
            return status >= 0;   // NT_SUCCESS
        }
        catch (Exception) { return false; }
        finally { ProcessNative.CloseHandle(handle); }
    }
}
