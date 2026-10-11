using System.Runtime.InteropServices;

namespace Insomnia.Windows.Display;

/// P/Invoke de tela (user32, kernel32). Só declarações.
internal static class DisplayNative
{
    internal static readonly IntPtr HwndBroadcast = new(0xFFFF);
    internal const uint WmSysCommand = 0x0112;
    internal static readonly IntPtr ScMonitorPower = new(0xF170);
    internal static readonly IntPtr MonitorOff = new(2);
    internal static readonly IntPtr MonitorOn = new(-1);
    internal const uint EsDisplayRequired = 0x00000002;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);
}
