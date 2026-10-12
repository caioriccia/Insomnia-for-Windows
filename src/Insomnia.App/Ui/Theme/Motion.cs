// CONTRATO CONGELADO (desenho-ui §3.10 e §5.5): durações e curvas Fluent. Puro (sem WPF):
// as curvas são os quatro pontos de controle de uma cúbica de Bézier, que a janela converte
// em KeySpline/easing. Com "Efeitos de animação" desligado no Windows, toda duração vira 0
// (Scaled com SystemParameters.ClientAreaAnimation).
namespace Insomnia.App.Ui.Theme;

public static class Motion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(83), Short = TimeSpan.FromMilliseconds(167), Normal = TimeSpan.FromMilliseconds(333), Hold = TimeSpan.FromMilliseconds(600);

    /// Entrar.
    public static (double X1, double Y1, double X2, double Y2) Decelerate => (0.1, 0.9, 0.2, 1.0);
    /// Sair.
    public static (double X1, double Y1, double X2, double Y2) Accelerate => (0.7, 0.0, 1.0, 0.5);
    /// Mudança no lugar.
    public static (double X1, double Y1, double X2, double Y2) Standard => (0.8, 0.0, 0.2, 1.0);

    /// A duração pedida, ou zero com as animações do sistema desligadas.
    public static TimeSpan Scaled(TimeSpan d, bool animationsEnabled) => animationsEnabled ? d : TimeSpan.Zero;
}
