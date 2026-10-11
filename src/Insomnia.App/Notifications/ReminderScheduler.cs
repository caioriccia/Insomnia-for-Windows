// Lembrete "Faltam 5 minutos" (desenho §9; original UI/ReminderScheduler.swift).
// Dispara em endsAt − 5 min. Não arma se faltar menos que isso ao iniciar/estender; é
// rearmado quando o fim muda e cancelado quando não há sessão.
using System.Globalization;
using Insomnia.Core.Abstractions;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Notifications;

public sealed class ReminderScheduler(IScheduler scheduler, IClock clock, INotifier notifier, ILog log) : IDisposable
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);
    public const string Title = "Faltam 5 minutos";

    private readonly object _gate = new();
    private IDisposable? _timer;
    private DateTimeOffset? _armedFor;
    private DateTimeOffset? _seenEnd;

    /// Fim da sessão para o qual o lembrete está armado (null = nada armado).
    public DateTimeOffset? ArmedFor { get { lock (_gate) return _armedFor; } }
    public bool IsArmed => ArmedFor is not null;

    /// Chamado a cada mudança da sessão. Mesmo fim já visto ⇒ nada muda (o Changed do manager é frequente).
    public void Update(SessionModel? session)
    {
        lock (_gate)
        {
            var end = session?.EndsAt;
            if (end == _seenEnd) return;
            _seenEnd = end;
            _timer?.Dispose();
            _timer = null;
            _armedFor = null;
            if (end is not { } endsAt) return;

            var fireIn = endsAt - Lead - clock.Now;
            if (fireIn <= TimeSpan.Zero)
            {
                log.Info("lembrete de 5 minutos não armado: falta menos que isso");
                return;
            }
            _armedFor = endsAt;
            _timer = scheduler.Schedule(fireIn, () => Fire(endsAt));
        }
    }

    public static string Body(DateTimeOffset endsAt, DateTimeOffset now) =>
        $"O sono volta às {endsAt.ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture)}. Estenda pela bandeja.";

    private void Fire(DateTimeOffset endsAt)
    {
        lock (_gate)
        {
            if (_armedFor != endsAt) return;   // rearmado ou cancelado no meio
            _armedFor = null;
            _timer = null;
        }
        try { notifier.Post(Title, Body(endsAt, clock.Now)); }
        catch (Exception ex) { log.Error($"lembrete: notificação falhou: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _armedFor = null;
        }
    }
}
