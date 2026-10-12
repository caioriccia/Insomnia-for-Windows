// CONTRATO CONGELADO (desenho-ui §5.3). Arrasto da pílula para baixo (§4 "Clique"/"Arrasto"):
// clique curto abre, puxar ≥ 48 epx (ou com velocidade) abre, o resto volta. Sem WPF.
namespace Insomnia.App.Ui.Model;

public enum PullPhase { Idle, Pressed, Pulling }
public enum PullOutcome { None, Click, Open, Cancel }

/// Máquina de estados do arrasto da pílula. Coordenadas em epx, tempo em ms (monotônico). Sem WPF.
public sealed class PullDownGesture
{
    public const double ClickSlop = 4;
    public const double OpenThreshold = 48;
    public const double FlingVelocity = 0.8;     // epx/ms, média dos últimos 100 ms
    public const int ClickMaxMs = 400;
    private const long VelocityWindowMs = 100;
    private const double Resistance = 0.35;

    // Posições vistas desde o Down (tempo, y), para a velocidade dos últimos 100 ms.
    private readonly List<(long T, double Y)> _samples = [];
    private double _startY;
    private long _startMs;

    public PullPhase Phase { get; private set; }
    /// ≥ 0, já com resistência. Zero enquanto Pressed; depois de Up/Cancel guarda o último valor
    /// (ponto de partida da animação de completar/voltar) até o próximo Down.
    public double Offset { get; private set; }

    public PullOutcome Down(double y, long tMs)
    {
        _samples.Clear();
        _samples.Add((tMs, y));
        _startY = y;
        _startMs = tMs;
        Offset = 0;
        Phase = PullPhase.Pressed;
        return PullOutcome.None;
    }

    /// None sempre; passa de Pressed a Pulling quando o deslocamento para baixo passa de ClickSlop.
    public PullOutcome Move(double y, long tMs)
    {
        if (Phase == PullPhase.Idle) return PullOutcome.None;
        _samples.Add((tMs, y));
        var dy = y - _startY;
        if (Phase == PullPhase.Pressed && dy > ClickSlop) Phase = PullPhase.Pulling;
        if (Phase == PullPhase.Pulling) Offset = Resist(dy);
        return PullOutcome.None;
    }

    /// Pressed: Click se soltou em até ClickMaxMs, senão Cancel. Pulling: Open se Offset ≥ OpenThreshold
    /// ou velocidade ≥ FlingVelocity, senão Cancel. Volta a Idle. Idle: None.
    public PullOutcome Up(long tMs)
    {
        var phase = Phase;
        Phase = PullPhase.Idle;
        return phase switch
        {
            PullPhase.Pressed => tMs - _startMs <= ClickMaxMs ? PullOutcome.Click : PullOutcome.Cancel,
            PullPhase.Pulling => Offset >= OpenThreshold || Velocity(tMs) >= FlingVelocity ? PullOutcome.Open : PullOutcome.Cancel,
            _ => PullOutcome.None,
        };
    }

    /// Captura perdida / Esc: Cancel se havia gesto em curso; Idle.
    public PullOutcome Cancel()
    {
        var active = Phase != PullPhase.Idle;
        Phase = PullPhase.Idle;
        return active ? PullOutcome.Cancel : PullOutcome.None;
    }

    /// rawDy ≤ 48 ⇒ rawDy; senão 48 + (rawDy−48)×0,35; negativo ⇒ 0.
    public static double Resist(double rawDy)
    {
        if (rawDy <= 0) return 0;
        return rawDy <= OpenThreshold ? rawDy : OpenThreshold + (rawDy - OpenThreshold) * Resistance;
    }

    // Deslocamento para baixo na janela [t − 100 ms, t] (ou desde o Down, se mais curto), em epx/ms.
    // A posição entre amostras é a última conhecida.
    private double Velocity(long tMs)
    {
        var window = Math.Min(VelocityWindowMs, tMs - _startMs);
        if (window <= 0) return 0;
        return (YAt(tMs) - YAt(tMs - window)) / window;
    }

    private double YAt(long tMs)
    {
        var y = _startY;
        foreach (var (t, sampleY) in _samples)
        {
            if (t > tMs) break;
            y = sampleY;
        }
        return y;
    }
}
