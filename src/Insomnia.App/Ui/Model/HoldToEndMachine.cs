// CONTRATO CONGELADO (desenho-ui §5.3). "Segure para encerrar" (§3.5 rodapé), sem WPF:
// o controle repassa ponteiro/teclado e um timer chama Advance; a máquina só conta.
namespace Insomnia.App.Ui.Model;

/// Segurar para encerrar. Tempo em ms (monotônico). Sem WPF.
public sealed class HoldToEndMachine
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(600);
    /// Mover mais que isto (epx, distância euclidiana) durante a pressão cancela.
    public const double CancelDistance = 12;

    private double _x, _y;
    private long _startMs;

    public bool Holding { get; private set; }
    /// 0..1. Nunca recua enquanto segura; volta a 0 ao cancelar; fica em 1 depois de completar.
    public double Progress { get; private set; }

    /// Começa a contar a partir de tMs (teclado: qualquer x/y, sem Move).
    public void Press(double x, double y, long tMs)
    {
        _x = x;
        _y = y;
        _startMs = tMs;
        Holding = true;
        Progress = 0;
    }

    /// true = cancelou por distância (> CancelDistance do ponto da pressão).
    public bool Move(double x, double y)
    {
        if (!Holding) return false;
        var dx = x - _x;
        var dy = y - _y;
        if (Math.Sqrt(dx * dx + dy * dy) <= CancelDistance) return false;
        Abort();
        return true;
    }

    /// Soltar antes de completar cancela; depois de completar não faz nada.
    public void Release()
    {
        if (Holding) Abort();
    }

    /// Atualiza Progress; true UMA vez, no avanço que completa (e a máquina para).
    public bool Advance(long tMs)
    {
        if (!Holding) return false;
        var p = Math.Clamp((tMs - _startMs) / Duration.TotalMilliseconds, 0, 1);
        if (p > Progress) Progress = p;
        if (Progress < 1) return false;
        Holding = false;
        return true;
    }

    private void Abort()
    {
        Holding = false;
        Progress = 0;
    }
}
