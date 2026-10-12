// Curva Fluent (Motion.Decelerate/Accelerate/Standard) como easing do WPF, para as animações de
// Storyboard/BeginAnimation da casca. A conta é a do ShellMath.Ease; EasingMode fica em EaseIn
// para o WPF não espelhar a curva.
using System.Windows;
using System.Windows.Media.Animation;

namespace Insomnia.App.Ui.Shell;

internal sealed class FluentEase : EasingFunctionBase
{
    private readonly (double X1, double Y1, double X2, double Y2) _curve;

    public FluentEase((double X1, double Y1, double X2, double Y2) curve)
    {
        _curve = curve;
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) => ShellMath.Ease(_curve, normalizedTime);

    // Clonar (o WPF clona/congela easings dentro das animações) mantém a curva.
    protected override Freezable CreateInstanceCore() => new FluentEase(_curve);
}
