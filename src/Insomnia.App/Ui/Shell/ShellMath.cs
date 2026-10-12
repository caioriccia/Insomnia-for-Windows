// Contas da casca (pílula + painel) que não precisam de janela: o "clip" do painel por
// progresso e pelo arrasto, a curva Fluent resolvida, o foco inicial por modo, o auto-recolher
// e as cores de cada tom. Puro (sem WPF): UiShell, PillWindow e PanelWindow só aplicam.
//
// O "clip" é o retângulo visível do painel (desenho-ui §3.10): começa no tamanho da pílula
// (148 × 32, raio 16) e termina no painel (360 × H, raio 8), ancorado no topo-centro. Numa janela
// sem moldura com material do DWM o recorte do WPF não recorta o acrílico (o DWM pinta a janela
// inteira), então quem aplica usa ESTE retângulo como tamanho da própria janela e o raio no
// recorte do conteúdo.
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Shell;

/// Retângulo visível do painel em epx, ancorado no topo-centro.
public readonly record struct ShellClip(double Width, double Height, double Radius);

/// Onde o foco cai ao abrir o painel (§4 "Foco").
public enum PanelFocus { DurationField, FirstExtendChip }

public static class ShellMath
{
    // Medidas do §3.4/§3.5, em epx.
    public const double PillHeight = 32;
    public const double PillMinWidth = 148;
    public const double PillPadding = 14;
    public const double PillRadius = 16;
    public const double TopMargin = 8;
    public const double PanelWidth = 360;
    public const double PanelRadius = 8;
    /// Deslocamento da pílula ao aparecer (translateY −8 → 0).
    public const double PillAppearOffset = -8;

    /// Alça: arrastar para cima pelo menos isto fecha; mexer até HandleClickSlop conta como clique.
    public const double HandleCloseDistance = 48;
    public const double HandleClickSlop = 4;

    /// Painel aberto depois de um início bem-sucedido: recolhe sozinho depois disto (§4, §7.4).
    public static readonly TimeSpan AutoCollapseDelay = TimeSpan.FromMilliseconds(1500);
    /// Painel sobe (fechar): 200 ms Accelerate (§3.10).
    public static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(200);
    /// Ao soltar o arrasto, completar ou voltar leva no máximo isto (§3.10 "Arrasto").
    public static readonly TimeSpan SettleMax = TimeSpan.FromMilliseconds(200);
    /// Clique na bandeja logo depois de o painel fechar por perder o foco: é o mesmo clique
    /// (a bandeja tirou o foco do painel) e não deve reabri-lo.
    public const long ReopenGuardMs = 400;

    /// Clip no progresso p (0 = pílula, 1 = painel aberto), interpolado linearmente; p fora de
    /// 0..1 é preso. startWidth = largura atual da pílula (cresce com o texto; mínimo 148).
    public static ShellClip ClipAt(double progress, double panelHeight, double startWidth = PillMinWidth)
    {
        var p = Clamp01(progress);
        var height = Math.Max(panelHeight, PillHeight);
        var from = Math.Clamp(startWidth, PillMinWidth, Math.Max(PillMinWidth, PanelWidth));
        return new ShellClip(
            Width: Lerp(from, PanelWidth, p),
            Height: Lerp(PillHeight, height, p),
            Radius: Lerp(PillRadius, PanelRadius, p));
    }

    /// Progresso do painel durante o arrasto: altura = 32 + Offset (o Offset do PullDownGesture já
    /// vem com resistência além de 48), presa à altura do painel.
    public static double PullProgress(double offset, double panelHeight)
    {
        var travel = panelHeight - PillHeight;
        if (travel <= 0) return 1;
        return Clamp01(offset / travel);
    }

    /// Clip que segue o arrasto (§3.10): ClipAt(PullProgress(offset)).
    public static ShellClip ClipForPull(double offset, double panelHeight, double startWidth = PillMinWidth) =>
        ClipAt(PullProgress(offset, panelHeight), panelHeight, startWidth);

    /// Opacidade do conteúdo enquanto o arrasto revela o painel: aparece na segunda metade do caminho.
    public static double PullContentOpacity(double progress) => Clamp01((progress - 0.5) * 2);

    /// Duração de completar/voltar a partir do ponto atual: a fração que falta da duração cheia,
    /// nunca acima de SettleMax.
    public static TimeSpan SettleDuration(double fromProgress, double toProgress, TimeSpan full)
    {
        var fraction = Math.Abs(Clamp01(toProgress) - Clamp01(fromProgress));
        var ms = Math.Min(SettleMax.TotalMilliseconds, fraction * Math.Max(0, full.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(Math.Round(ms, MidpointRounding.AwayFromZero));
    }

    /// Opacidade de um "aparece nos últimos `tail`" de uma animação de `total` (§3.10: conteúdo
    /// 0 → 1 nos últimos 167 ms dos 333). timeFraction = tempo decorrido / total (linear, sem curva).
    /// total zero (animações desligadas) ⇒ 1.
    public static double FadeInTail(double timeFraction, TimeSpan total, TimeSpan tail)
    {
        var totalMs = total.TotalMilliseconds;
        if (totalMs <= 0) return 1;
        var tailMs = Math.Clamp(tail.TotalMilliseconds, 0, totalMs);
        if (tailMs <= 0) return Clamp01(timeFraction) >= 1 ? 1 : 0;
        var elapsed = Clamp01(timeFraction) * totalMs;
        return Clamp01((elapsed - (totalMs - tailMs)) / tailMs);
    }

    /// O inverso, para fechar: some nos primeiros `head` da animação (1 → 0). total zero ⇒ 0.
    public static double FadeOutHead(double timeFraction, TimeSpan total, TimeSpan head)
    {
        var totalMs = total.TotalMilliseconds;
        if (totalMs <= 0) return 0;
        var headMs = Math.Clamp(head.TotalMilliseconds, 0, totalMs);
        if (headMs <= 0) return Clamp01(timeFraction) > 0 ? 0 : 1;
        return 1 - Clamp01(Clamp01(timeFraction) * totalMs / headMs);
    }

    /// Cúbica de Bézier de (0,0) a (1,1) com os pontos de controle da curva (Motion.Decelerate…):
    /// devolve y para o x = t. Newton com bisseção de reserva; t fora de 0..1 é preso.
    public static double Ease((double X1, double Y1, double X2, double Y2) curve, double t)
    {
        var x = Clamp01(t);
        if (x is 0 or 1) return x;
        var (x1, y1, x2, y2) = curve;

        var u = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(x1, x2, u) - x;
            if (Math.Abs(error) < 1e-7) return Bezier(y1, y2, u);
            var slope = BezierSlope(x1, x2, u);
            if (Math.Abs(slope) < 1e-6) break;
            u -= error / slope;
            if (u is < 0 or > 1) break;
        }

        double lo = 0, hi = 1;
        u = x;
        for (var i = 0; i < 60; i++)
        {
            var value = Bezier(x1, x2, u);
            if (Math.Abs(value - x) < 1e-7) break;
            if (value < x) lo = u; else hi = u;
            u = (lo + hi) / 2;
        }
        return Bezier(y1, y2, u);
    }

    /// Foco ao abrir o painel: o campo de duração (Iniciar) ou o chip "+15 min" (Sessão).
    public static PanelFocus FocusFor(PanelMode mode) =>
        mode == PanelMode.Start ? PanelFocus.DurationField : PanelFocus.FirstExtendChip;

    /// Auto-recolher (§4): só depois de um início que deu certo (armed), passado o atraso, sem a
    /// pessoa ter mexido no painel nesse meio-tempo, com a sessão ainda de pé e o painel aberto.
    public static bool ShouldAutoCollapse(bool armed, TimeSpan elapsed, bool interacted, bool hasSession, bool panelOpen) =>
        armed && !interacted && hasSession && panelOpen && elapsed >= AutoCollapseDelay;

    /// Início deu certo (StartSessionAsync devolveu null) ⇒ arma o auto-recolher.
    public static bool ArmsAutoCollapse(string? startProblem) => startProblem is null;

    /// "Segure para encerrar" completou: o fim só vale se não houve erro E a sessão sumiu do
    /// retrato. EndAsync devolvendo Locked/JournalUnreadable não lança e deixa a sessão de pé —
    /// nesse caso (e no de erro) o botão tem de voltar a "Segure para encerrar", senão fica morto.
    public static bool EndSucceeded(string? endProblem, bool hasSessionAfter) =>
        endProblem is null && !hasSessionAfter;

    /// TogglePanel abre? Não se já está aberto, nem se acabou de fechar por perder o foco (o
    /// clique na bandeja que tirou o foco é o mesmo que pede o toggle).
    public static bool ToggleOpens(bool panelOpen, long msSinceDismissedByFocus) =>
        !panelOpen && msSinceDismissedByFocus >= ReopenGuardMs;

    /// Alça: fecha com arrasto para cima ≥ 48 ou com clique (o ponteiro andou no máximo 4 epx).
    /// upward = quanto o ponteiro subiu do ponto da pressão; maxTravel = maior distância vista.
    public static bool HandleCloses(double upward, double maxTravel) =>
        upward >= HandleCloseDistance || maxTravel <= HandleClickSlop;

    /// Largura da pílula: conteúdo (glifo, texto, ponto) + 14 de cada lado, mínimo 148, inteira.
    public static double PillWidth(double contentWidth) =>
        Math.Max(PillMinWidth, Math.Ceiling(Math.Max(0, contentWidth) + 2 * PillPadding));

    /// Chaves de Tokens.xaml para glifo e texto da pílula por tom (§3.9).
    public static (string Glyph, string Text) PillBrushes(PillTone tone) => tone switch
    {
        PillTone.Critical => ("Insomnia.Critical", "Insomnia.Critical"),
        PillTone.EndingSoon => ("Insomnia.Caution", "Insomnia.Caution"),
        PillTone.Warning => ("Insomnia.Caution", "Insomnia.Text.Primary"),
        PillTone.Paused => ("Insomnia.Text.Secondary", "Insomnia.Text.Secondary"),
        _ => ("Insomnia.Text.Primary", "Insomnia.Text.Primary"),
    };

    /// Chave do ponto de estado (§3.9): acento no normal/tampa, Caution no aviso/fim próximo, Critical.
    public static string DotBrush(PillTone tone) => tone switch
    {
        PillTone.Critical => "Insomnia.Critical",
        PillTone.EndingSoon or PillTone.Warning => "Insomnia.Caution",
        _ => "Insomnia.Accent",
    };

    /// Cor da sublinha do painel: Caution no fim próximo, Critical no último minuto, senão secundária.
    public static string SublineBrush(PillTone tone) => tone switch
    {
        PillTone.Critical => "Insomnia.Critical",
        PillTone.EndingSoon => "Insomnia.Caution",
        _ => "Insomnia.Text.Secondary",
    };

    private static double Bezier(double p1, double p2, double u)
    {
        var v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }

    private static double BezierSlope(double p1, double p2, double u)
    {
        var v = 1 - u;
        return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
