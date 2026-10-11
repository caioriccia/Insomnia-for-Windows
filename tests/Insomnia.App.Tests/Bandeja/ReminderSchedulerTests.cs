using Insomnia.App.Notifications;
using Insomnia.Core.Tests.Fakes;
using SessionModel = Insomnia.Core.Model.Session;

namespace Insomnia.App.Tests.Bandeja;

public class ReminderSchedulerTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeScheduler _scheduler;
    private readonly FakeNotifier _notifier = new();
    private readonly FakeLog _log = new();
    private readonly ReminderScheduler _reminder;

    public ReminderSchedulerTests()
    {
        _scheduler = new FakeScheduler(_clock);
        _reminder = new ReminderScheduler(_scheduler, _clock, _notifier, _log);
    }

    private SessionModel EndingIn(TimeSpan left) => new(_clock.Now, _clock.Now + left, []);

    [Fact]
    public void Arma_em_endsAt_menos_5_minutos()
    {
        var session = EndingIn(TimeSpan.FromHours(1));

        _reminder.Update(session);

        Assert.True(_reminder.IsArmed);
        Assert.Equal(session.EndsAt, _reminder.ArmedFor);
        Assert.Equal([TimeSpan.FromMinutes(55)], _scheduler.PendingDelays);
    }

    [Fact]
    public void Dispara_com_titulo_e_corpo_em_portugues()
    {
        var session = EndingIn(TimeSpan.FromHours(1));
        _reminder.Update(session);

        _scheduler.Advance(TimeSpan.FromMinutes(54));
        Assert.Empty(_notifier.Posts);
        _scheduler.Advance(TimeSpan.FromMinutes(1));

        var post = Assert.Single(_notifier.Posts);
        Assert.Equal("Faltam 5 minutos", post.Title);
        Assert.Equal(ReminderScheduler.Body(session.EndsAt, _clock.Now), post.Body);
        Assert.StartsWith("O sono volta às ", post.Body);
        Assert.EndsWith(". Estenda pela bandeja.", post.Body);
        Assert.False(_reminder.IsArmed);
    }

    [Fact]
    public void Corpo_usa_a_hora_local_do_fim()
    {
        var end = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);    // 22:00 em -03:00
        var now = new DateTimeOffset(2026, 10, 11, 21, 55, 0, TimeSpan.FromHours(-3));
        Assert.Equal("O sono volta às 22:00. Estenda pela bandeja.", ReminderScheduler.Body(end, now));
    }

    [Fact]
    public void Nao_arma_se_faltar_menos_de_5_minutos()
    {
        _reminder.Update(EndingIn(TimeSpan.FromMinutes(4)));

        Assert.False(_reminder.IsArmed);
        Assert.Equal(0, _scheduler.PendingCount);
        _scheduler.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_notifier.Posts);
    }

    [Fact]
    public void Nao_arma_com_exatamente_5_minutos()
    {
        _reminder.Update(EndingIn(TimeSpan.FromMinutes(5)));
        Assert.False(_reminder.IsArmed);
    }

    [Fact]
    public void Sem_sessao_cancela()
    {
        _reminder.Update(EndingIn(TimeSpan.FromHours(1)));
        _reminder.Update(null);

        Assert.False(_reminder.IsArmed);
        _scheduler.Advance(TimeSpan.FromHours(2));
        Assert.Empty(_notifier.Posts);
    }

    [Fact]
    public void Estender_rearma_para_o_novo_fim()
    {
        var session = EndingIn(TimeSpan.FromHours(1));
        _reminder.Update(session);
        var extended = session with { EndsAt = session.EndsAt + TimeSpan.FromHours(1) };

        _reminder.Update(extended);

        Assert.Equal(extended.EndsAt, _reminder.ArmedFor);
        Assert.Equal([TimeSpan.FromMinutes(115)], _scheduler.PendingDelays);
        _scheduler.Advance(TimeSpan.FromMinutes(60));
        Assert.Empty(_notifier.Posts);   // o lembrete antigo não dispara
        _scheduler.Advance(TimeSpan.FromMinutes(55));
        Assert.Single(_notifier.Posts);
    }

    [Fact]
    public void Mesmo_fim_nao_duplica_o_timer()
    {
        var session = EndingIn(TimeSpan.FromHours(1));
        _reminder.Update(session);
        _reminder.Update(session);
        _reminder.Update(session with { });

        Assert.Equal(1, _scheduler.PendingCount);
        Assert.Single(_scheduler.Requested);
    }
}
