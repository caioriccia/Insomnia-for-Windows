using System.Runtime.InteropServices;

namespace Insomnia.Windows.Power;

/// P/Invoke de energia (powrprof, kernel32, user32). Só declarações: a lógica fica nas
/// classes da pasta. Funções do powrprof devolvem um código Win32 (0 = sucesso).
internal static class PowerNative
{
    internal const uint ErrorSuccess = 0;

    // ---------- powrprof: esquema ativo e índices AC/DC ----------
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint dcValueIndex);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ---------- kernel32: pedido de energia (some com o processo) ----------
    internal const uint PowerRequestContextVersion = 0;
    internal const uint PowerRequestContextSimpleString = 0x1;

    internal enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    /// REASON_CONTEXT com a união no formato "string simples". Os dois ponteiros de
    /// preenchimento cobrem o tamanho da variante Detailed da união (24 bytes em x64).
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public IntPtr Padding1;
        public IntPtr Padding2;
    }

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // ---------- kernel32: bateria/AC ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    // ---------- user32: notificações de configuração de energia ----------
    internal const int DeviceNotifyWindowHandle = 0;
    internal const int WmPowerBroadcast = 0x0218;
    internal const int PbtApmPowerStatusChange = 0x000A;
    internal const int PbtApmResumeAutomatic = 0x0012;
    internal const int PbtPowerSettingChange = 0x8013;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
