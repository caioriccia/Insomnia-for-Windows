// CONTRATO CONGELADO (desenho-ui §5.5): P/Invoke do que a UI nova pede ao Windows —
// material (Mica/Acrylic), cantos, moldura escura, ex-styles de janela flutuante, tela cheia,
// preferências de tema e escala por monitor. Cada método engole a falha e devolve bool (ou o
// valor neutro): a UI nunca cai porque o DWM recusou um efeito. Nada aqui é testado por
// unidade (tudo toca o sistema); a lógica de decisão mora em BackdropPolicy e UiPlacement.
using System.Runtime.InteropServices;
using Insomnia.App.Ui.Model;
using Microsoft.Win32;

namespace Insomnia.App.Ui.Native;

public static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // builds 17763–18985 do Windows 10
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1, DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L, WS_EX_NOACTIVATE = 0x08000000L;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// DWMWA_SYSTEMBACKDROP_TYPE 2=Mica 3=Acrylic(transient); Solid = 1 (none). Com material, estende
    /// a moldura sobre a área toda (sem isso o DWM só pinta o material na borda de uma janela sem
    /// moldura); com Solid, desfaz a extensão.
    public static bool TrySetBackdrop(IntPtr hwnd, Backdrop kind)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var margins = kind == Backdrop.Solid ? new Margins(0, 0, 0, 0) : new Margins(-1, -1, -1, -1);
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;
            var type = kind switch
            {
                Backdrop.Mica => DWMSBT_MAINWINDOW,
                Backdrop.Acrylic => DWMSBT_TRANSIENTWINDOW,
                _ => DWMSBT_NONE,
            };
            return SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, type);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// DWMWA_WINDOW_CORNER_PREFERENCE 2 / 1. Antes do Windows 11 o atributo não existe ⇒ false.
    public static bool TrySetRoundCorners(IntPtr hwnd, bool round) =>
        SetIntSafe(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, round ? DWMWCP_ROUND : DWMWCP_DONOTROUND);

    /// DWMWA_USE_IMMERSIVE_DARK_MODE (com o código antigo de reserva): moldura e sombra no tema.
    public static bool TrySetImmersiveDarkMode(IntPtr hwnd, bool dark) =>
        SetIntSafe(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0)
        || SetIntSafe(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, dark ? 1 : 0);

    /// GWL_EXSTYLE |= NOACTIVATE|TOOLWINDOW: nunca recebe foco, fora da barra de tarefas e do Alt+Tab.
    public static void MakeNoActivateToolWindow(IntPtr hwnd) => AddExStyle(hwnd, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    /// GWL_EXSTYLE |= TOOLWINDOW: ativável, mas fora do Alt+Tab.
    public static void MakeToolWindow(IntPtr hwnd) => AddExStyle(hwnd, WS_EX_TOOLWINDOW);

    /// SHQueryUserNotificationState (Fullscreen/Presentation/Normal). Falha ⇒ Normal.
    public static ForegroundKind QueryForeground()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return ForegroundKind.Normal;
            return state switch
            {
                QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN => ForegroundKind.Fullscreen,
                QUNS_PRESENTATION_MODE => ForegroundKind.Presentation,
                _ => ForegroundKind.Normal,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ForegroundKind.Normal; }
    }

    /// Build do Windows (22000+ = Windows 11). Com o manifesto declarando o Windows 10/11, é o real.
    public static int OsBuild => Environment.OSVersion.Version.Build;

    /// HKCU\...\Themes\Personalize\EnableTransparency (ausente = ligado, o padrão do Windows).
    public static bool TransparencyEnabled => ReadPersonalize("EnableTransparency", 1) != 0;

    /// SystemUsesLightTheme: barra de tarefas clara (ausente = escura, o padrão do Windows 11).
    public static bool SystemUsesLightTaskbar => ReadPersonalize("SystemUsesLightTheme", 0) != 0;

    /// AppsUseLightTheme = 0: apps no modo escuro (ausente = claro). Além do contrato: é o que a
    /// moldura das janelas (TrySetImmersiveDarkMode) segue, junto com o ThemeMode="System" do WPF.
    internal static bool AppsUseDarkTheme => ReadPersonalize("AppsUseLightTheme", 1) == 0;

    /// GetDpiForMonitor / 96 no monitor mais próximo do ponto (pixels físicos). Falha ⇒ 1.
    public static double ScaleForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(x, y), MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return 1.0;
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) != 0 || dpiX == 0) return 1.0;
            return dpiX / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 1.0; }
    }

    /// Posiciona e dimensiona em pixels físicos, sem ativar nem mudar a ordem Z. Além do contrato:
    /// é o que FlyoutWindow.MoveTo usa — o Windows converte para o DPI do monitor de destino e o
    /// WPF recebe o WM_DPICHANGED se a janela atravessar monitores.
    internal static bool TryMoveWindow(IntPtr hwnd, PxRect physical)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            return SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(physical.X), (int)Math.Round(physical.Y),
                (int)Math.Round(physical.W), (int)Math.Round(physical.H),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // ------------------------------------------------------------------ auxiliares

    private static bool SetIntSafe(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetInt(hwnd, attribute, value); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static bool SetInt(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;

    private static void AddExStyle(IntPtr hwnd, long bits)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var current = GetExStyle(hwnd);
            if ((current & bits) == bits) return;
            SetExStyle(hwnd, current | bits);
            // O Windows só relê o estilo com SWP_FRAMECHANGED.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    private static void SetExStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
        else SetWindowLong32(hwnd, GWL_EXSTYLE, unchecked((int)style));
    }

    private static int ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return fallback;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left, Right = right, Top = top, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x, Y = y;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
}
