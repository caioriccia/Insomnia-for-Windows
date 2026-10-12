// Animação quadro a quadro para o que o Storyboard não anima: posição e tamanho da JANELA (a
// pílula que desce 8 epx, o painel que cresce da pílula). Um por alvo; Run de novo interrompe o
// anterior sem chamar o "done" dele. Duração zero (animações do Windows desligadas, ou
// Motion.Scaled devolvendo 0) aplica o quadro final na hora, de forma síncrona.
using System.Diagnostics;
using System.Windows.Media;

namespace Insomnia.App.Ui.Shell;

internal sealed class FrameAnimator
{
    /// (fração de tempo 0..1, valor da curva 0..1).
    public delegate void Frame(double time, double eased);

    private readonly Stopwatch _elapsed = new();
    private TimeSpan _duration;
    private (double X1, double Y1, double X2, double Y2) _curve;
    private Frame? _frame;
    private Action? _done;

    public bool IsRunning { get; private set; }

    public void Run(TimeSpan duration, (double X1, double Y1, double X2, double Y2) curve, Frame frame, Action? done = null)
    {
        Stop();
        if (duration <= TimeSpan.Zero)
        {
            frame(1, 1);
            done?.Invoke();
            return;
        }
        _duration = duration;
        _curve = curve;
        _frame = frame;
        _done = done;
        IsRunning = true;
        frame(0, 0);
        _elapsed.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    /// Para onde estiver, sem chamar o "done".
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        CompositionTarget.Rendering -= OnRendering;
        _elapsed.Stop();
        _frame = null;
        _done = null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsRunning || _frame is not { } frame) return;
        var t = Math.Clamp(_elapsed.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        frame(t, ShellMath.Ease(_curve, t));
        if (t < 1) return;
        var done = _done;
        Stop();
        done?.Invoke();
    }
}
