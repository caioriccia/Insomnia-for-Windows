// O pouco de Win32 que só a casca usa e o Dwm (contrato congelado) não expõe: janela em primeiro
// plano (fechar o painel quando o Windows negou o foco a ele — desenho-ui §6, risco 4) e trazer
// uma janela topmost para o topo da pilha sem ativá-la (o painel precisa ficar por cima da pílula
// enquanto o arrasto o revela). Como no Dwm, falha vira valor neutro.
using System.Runtime.InteropServices;

namespace Insomnia.App.Ui.Shell;

internal static class ShellNative
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    /// GetForegroundWindow; falha ⇒ IntPtr.Zero.
    public static IntPtr ForegroundWindow()
    {
        try { return GetForegroundWindow(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    /// Topo da faixa topmost, sem mover, redimensionar nem ativar.
    public static void BringToTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
