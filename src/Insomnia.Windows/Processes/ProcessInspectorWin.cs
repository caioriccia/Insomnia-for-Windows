using System.Runtime.InteropServices;
using System.Security.Principal;
using Insomnia.Core.Abstractions;
using Microsoft.Win32;

namespace Insomnia.Windows.Processes;

/// IProcessInspector real:
/// - lista e ppid por Toolhelp32 (CreateToolhelp32Snapshot);
/// - identidade = GetProcessTimes.Creation (truncada a segundos, ver ProcessIdentityRules) +
///   BootId do registro (leitura de usuário); BootId ilegível ⇒ "" ⇒ identidades nulas;
/// - "parado" por NtQuerySystemInformation(SystemProcessInformation);
/// - janela visível por EnumWindows + IsWindowVisible + GetWindowThreadProcessId;
/// - caminho por QueryFullProcessImageName.
/// Snapshot devolve só processos do USUÁRIO ATUAL (SID do token igual ao nosso); o que não
/// abre com PROCESS_QUERY_LIMITED_INFORMATION fica de fora (também não seria suspensível).
/// Lookup devolve qualquer pid existente; sem acesso ⇒ Identity null. Nunca lançam.
public sealed class ProcessInspectorWin : IProcessInspector
{
    private const string BootIdKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    private readonly SecurityIdentifier? _currentUser = WindowsIdentity.GetCurrent().User;
    private string _bootId = "";

    /// Lido uma vez por boot; se falhar, tenta de novo na próxima consulta.
    public string BootId
    {
        get
        {
            if (_bootId.Length == 0) _bootId = ReadBootId();
            return _bootId;
        }
    }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        var bootId = BootId;
        var suspended = ReadSuspended();
        var visible = VisibleWindowPids();
        var result = new List<ProcessInfo>();
        foreach (var (pid, parent, name) in ToolhelpEntries())
        {
            if (pid <= 4) continue;   // Idle (0) e System (4)
            var handle = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (!OwnedBy(handle, _currentUser)) continue;
                result.Add(Build(handle, pid, parent, name, bootId, suspended, visible));
            }
            finally { ProcessNative.CloseHandle(handle); }
        }
        return result;
    }

    public ProcessInfo? Lookup(int pid)
    {
        if (pid <= 0) return null;
        foreach (var (entryPid, parent, name) in ToolhelpEntries())
        {
            if (entryPid != pid) continue;
            var suspended = ReadSuspended();
            var visible = VisibleWindowPids();
            var handle = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return new ProcessInfo(pid, parent, name, null, suspended.GetValueOrDefault(pid), visible.Contains(pid), null);
            try { return Build(handle, pid, parent, name, BootId, suspended, visible); }
            finally { ProcessNative.CloseHandle(handle); }
        }
        return null;
    }

    private static ProcessInfo Build(IntPtr handle, int pid, int parent, string name, string bootId,
        Dictionary<int, bool> suspended, HashSet<int> visible)
    {
        ProcessIdentity? identity = ProcessNative.GetProcessTimes(handle, out var creation, out _, out _, out _)
            ? ProcessIdentityRules.FromFileTime(creation, bootId)
            : null;
        return new ProcessInfo(pid, parent, name, identity, suspended.GetValueOrDefault(pid), visible.Contains(pid), ImagePath(handle));
    }

    private static List<(int Pid, int Parent, string Name)> ToolhelpEntries()
    {
        var list = new List<(int, int, string)>();
        var snapshot = ProcessNative.CreateToolhelp32Snapshot(ProcessNative.Th32csSnapProcess, 0);
        if (snapshot == ProcessNative.InvalidHandleValue || snapshot == IntPtr.Zero) return list;
        try
        {
            var entry = new ProcessNative.ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessNative.ProcessEntry32>() };
            for (var ok = ProcessNative.Process32First(snapshot, ref entry); ok; ok = ProcessNative.Process32Next(snapshot, ref entry))
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
        }
        finally { ProcessNative.CloseHandle(snapshot); }
        return list;
    }

    private static string? ImagePath(IntPtr handle)
    {
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        return ProcessNative.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static bool OwnedBy(IntPtr process, SecurityIdentifier? user)
    {
        if (user is null) return false;
        if (!ProcessNative.OpenProcessToken(process, ProcessNative.TokenQuery, out var token)) return false;
        try
        {
            ProcessNative.GetTokenInformation(token, ProcessNative.TokenUserClass, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return false;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!ProcessNative.GetTokenInformation(token, ProcessNative.TokenUserClass, buffer, needed, out _)) return false;
                // TOKEN_USER começa com SID_AND_ATTRIBUTES, cujo primeiro campo é o PSID.
                var sid = Marshal.ReadIntPtr(buffer);
                return sid != IntPtr.Zero && new SecurityIdentifier(sid) == user;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (ArgumentException) { return false; }
        finally { ProcessNative.CloseHandle(token); }
    }

    /// pid ⇒ parado, a partir de um único NtQuerySystemInformation. Falha ⇒ mapa vazio.
    private static Dictionary<int, bool> ReadSuspended()
    {
        var size = 1 << 20;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = ProcessNative.NtQuerySystemInformation(ProcessNative.SystemProcessInformationClass, buffer, size, out var needed);
                if (status == ProcessNative.StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (64 << 10));
                    continue;
                }
                if (status < 0) return [];
                var length = needed > 0 && needed <= size ? needed : size;
                unsafe
                {
                    return SystemProcessTable.ParseSuspended(new ReadOnlySpan<byte>((void*)buffer, length), SystemProcessTable.Layout.Current);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    private static HashSet<int> VisibleWindowPids()
    {
        var pids = new HashSet<int>();
        ProcessNative.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (ProcessNative.IsWindowVisible(hwnd) && ProcessNative.GetWindowThreadProcessId(hwnd, out var pid) != 0)
                pids.Add((int)pid);
            return true;
        };
        ProcessNative.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return pids;
    }

    private static string ReadBootId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BootIdKey, writable: false);
            return ProcessIdentityRules.ParseBootId(key?.GetValue("BootId"));
        }
        catch (Exception) { return ""; }
    }
}
