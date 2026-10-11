using System.Runtime.InteropServices;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Display;

/// IDisplayPower real. Substitui o "brilho salvo" do Mac: nada é salvo nem diarizado.
/// - TurnOff: WM_SYSCOMMAND/SC_MONITORPOWER = 2 em broadcast (apaga também monitores externos).
/// - Wake: SC_MONITORPOWER = -1 + SetThreadExecutionState(ES_DISPLAY_REQUIRED) SEM
///   ES_CONTINUOUS (one-shot: só reinicia o timer de tela, não segura nada).
///
/// Desvio consciente do §7 (que diz SendMessage): usa PostMessage. SendMessage para
/// HWND_BROADCAST espera TODA janela de topo responder — e as janelas dos apps que o próprio
/// Insomnia congela não respondem nunca, o que travaria a transação da tampa sob o lock.
/// Nunca lança (contrato): falha vira log.
public sealed class DisplayPowerWin : IDisplayPower
{
    private readonly ILog? _log;

    public DisplayPowerWin() : this(null) { }

    public DisplayPowerWin(ILog? log) { _log = log; }

    public void TurnOff()
    {
        try
        {
            if (!DisplayNative.PostMessage(DisplayNative.HwndBroadcast, DisplayNative.WmSysCommand, DisplayNative.ScMonitorPower, DisplayNative.MonitorOff))
                _log?.Error($"apagar tela: PostMessage falhou (erro Win32 {Marshal.GetLastWin32Error()})");
        }
        catch (Exception e) { _log?.Error($"apagar tela falhou: {e.Message}"); }
    }

    public void Wake()
    {
        try
        {
            if (!DisplayNative.PostMessage(DisplayNative.HwndBroadcast, DisplayNative.WmSysCommand, DisplayNative.ScMonitorPower, DisplayNative.MonitorOn))
                _log?.Error($"acender tela: PostMessage falhou (erro Win32 {Marshal.GetLastWin32Error()})");
            if (DisplayNative.SetThreadExecutionState(DisplayNative.EsDisplayRequired) == 0)
                _log?.Error("acender tela: SetThreadExecutionState(ES_DISPLAY_REQUIRED) falhou");
        }
        catch (Exception e) { _log?.Error($"acender tela falhou: {e.Message}"); }
    }
}
