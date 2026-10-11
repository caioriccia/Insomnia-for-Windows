// Textos pt-BR da bandeja e do popover (desenho §9). Puro: sem relógio, sem sistema.
using System.Globalization;
using Insomnia.Core.Session;
using Insomnia.Core.Time;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Tray;

public static class TrayTexts
{
    public const string IdleTooltip = "Insomnia · sem sessão";

    /// Forma da contagem pela duração INICIAL (sem as extensões): largura estável a sessão inteira.
    public static CountdownShape ShapeOf(SessionModel session)
    {
        var extensions = session.Extensions.IsDefault ? 0 : session.Extensions.Sum(e => (long)e);
        return SessionMath.ShapeFor(session.EndsAt - session.StartedAt - TimeSpan.FromSeconds(extensions));
    }

    public static string Countdown(SessionModel session, DateTimeOffset now) =>
        SessionMath.FormatCountdown(session.Remaining(now), ShapeOf(session));

    /// Hora do fim no fuso de `now`: "22:00"; em outro dia, "13/10 22:00".
    public static string EndLabel(SessionModel session, DateTimeOffset now)
    {
        var end = session.EndsAt.ToOffset(now.Offset);
        return end.Date == now.Date
            ? end.ToString("HH:mm", CultureInfo.InvariantCulture)
            : end.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    }

    /// "Insomnia · 1:23:45 · termina 22:00" (NotifyIcon.Text aceita até 127 caracteres).
    public static string Tooltip(SessionModel? session, DateTimeOffset now, bool paused)
    {
        if (session is null) return IdleTooltip;
        var text = $"Insomnia · {Countdown(session, now)} · termina {EndLabel(session, now)}";
        if (paused) text += " · tampa fechada";
        return text.Length <= 127 ? text : text[..127];
    }

    /// Mensagem do popover para cada recusa de início; null = começou.
    public static string? Explain(StartOutcome outcome) => outcome switch
    {
        StartOutcome.Started => null,
        StartOutcome.RefusedQuitRequested => "O Insomnia está saindo: a sessão não foi iniciada.",
        StartOutcome.RefusedEndPending => "Um encerramento ainda está pendente. Tente de novo em instantes.",
        StartOutcome.RefusedAlreadyActive => "Já existe uma sessão ativa.",
        StartOutcome.RefusedSessionFileUnreadable => "Arquivo de sessão ilegível: feche e abra o Insomnia de novo.",
        StartOutcome.Locked => "Recuperação ocupada por outro processo: nada mudou. Tente de novo.",
        StartOutcome.JournalUnreadable => "Diário de recuperação ilegível: nada foi mudado. Veja o log.",
        StartOutcome.BackstopNotArmed => "Não deu para armar o backstop no Agendador de Tarefas: a sessão não começou.",
        StartOutcome.PowerWriteFailed => "Não deu para mudar a ação da tampa no plano de energia: a sessão não começou.",
        _ => "A sessão não começou.",
    };

    public static string? Explain(ExtendOutcome outcome) => outcome switch
    {
        ExtendOutcome.Extended => null,
        ExtendOutcome.NoSession => "Nenhuma sessão ativa para estender.",
        ExtendOutcome.Locked => "Recuperação ocupada por outro processo: a sessão não foi estendida.",
        ExtendOutcome.JournalUnreadable => "Diário de recuperação ilegível: a sessão não foi estendida.",
        _ => "Não deu para estender a sessão.",
    };

    public const string InvalidDuration = "Duração inválida. Exemplos: 30, 1h30m, 3d.";
}
