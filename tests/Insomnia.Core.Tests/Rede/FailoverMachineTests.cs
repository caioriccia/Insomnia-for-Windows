using Insomnia.Core.Network;
using static Insomnia.Core.Network.FailoverOutput;

namespace Insomnia.Core.Tests.Rede;

public class FailoverMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static FailoverOutput[] O(params FailoverOutput[] outputs) => outputs;

    [Fact]
    public void Queda_agenda_a_primeira_tentativa_para_5s()
    {
        var m = new FailoverMachine();
        Assert.Equal(O(new ScheduleRetry(S(5))), m.PathDown(T0));
        Assert.True(m.InOutage);
        Assert.Equal(T0, m.OutageStart);
        Assert.Equal(0, m.Joins);
    }

    [Fact]
    public void Piscada_curta_volta_sem_tentar_e_timer_velho_eh_ignorado()
    {
        var m = new FailoverMachine();
        m.PathDown(T0);
        Assert.Equal(O(new Recovered(T0, S(3))), m.PathUp(T0 + S(3)));
        Assert.False(m.InOutage);
        Assert.Equal(0, m.Joins);
        Assert.Empty(m.Tick(T0 + S(5)));
    }

    [Fact]
    public void Tick_adiantado_nao_tenta_e_rearma_o_que_falta()
    {
        var m = new FailoverMachine();
        m.PathDown(T0);
        var outs = m.Tick(T0 + S(4.5));
        Assert.Equal(O(new ScheduleRetry(S(0.5))), outs);
        Assert.Equal(0, m.Joins);
        Assert.Equal(O(new JoinHotspot(), new ScheduleRetry(S(5))), m.Tick(T0 + S(5)));
    }

    [Fact]
    public void Backoff_5_10_20_30_30_e_nunca_desiste()
    {
        var m = new FailoverMachine();
        m.PathDown(T0);
        var t = T0 + S(5);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 40; i++)
        {
            var outs = m.Tick(t);
            Assert.Equal(new JoinHotspot(), outs[0]);
            var retry = Assert.IsType<ScheduleRetry>(outs[^1]);
            delays.Add(retry.Delay);
            m.JoinFinished(ok: false);
            t += retry.Delay;
        }
        Assert.Equal(new[] { S(5), S(10), S(20), S(30), S(30) }, delays.Take(5));
        Assert.All(delays.Skip(3), d => Assert.Equal(S(30), d));
        Assert.Equal(40, m.Joins);
        Assert.True(m.InOutage);   // sem teto de tentativas
    }

    [Fact]
    public void Volta_depois_de_tentativas_informa_o_gap_e_zera()
    {
        var m = new FailoverMachine();
        m.PathDown(T0);
        m.Tick(T0 + S(5));
        m.JoinFinished(ok: true);
        Assert.Equal(O(new Recovered(T0, S(130))), m.PathUp(T0 + S(130)));
        Assert.Equal(0, m.Joins);
        Assert.Null(m.NextAttemptAt);
        Assert.False(m.JoinInFlight);
    }

    [Fact]
    public void Queda_repetida_eh_ignorada_e_volta_sem_queda_tambem()
    {
        var m = new FailoverMachine();
        Assert.Empty(m.PathUp(T0));
        m.PathDown(T0);
        Assert.Empty(m.PathDown(T0 + S(1)));
        Assert.Equal(T0, m.OutageStart);
    }

    [Fact]
    public void Tentativa_em_andamento_nao_abre_outra_so_reagenda()
    {
        var m = new FailoverMachine();
        m.PathDown(T0);
        Assert.Equal(O(new JoinHotspot(), new ScheduleRetry(S(5))), m.Tick(T0 + S(5)));
        Assert.True(m.JoinInFlight);
        Assert.Equal(O(new ScheduleRetry(S(5))), m.Tick(T0 + S(10)));
        Assert.Equal(1, m.Joins);
        m.JoinFinished(ok: false);
        Assert.False(m.JoinInFlight);
        Assert.Equal(false, m.LastJoinOk);
        Assert.Equal(O(new JoinHotspot(), new ScheduleRetry(S(10))), m.Tick(T0 + S(15)));
    }

    [Fact]
    public void JoinFinished_fora_de_queda_nao_faz_nada()
    {
        var m = new FailoverMachine();
        Assert.Empty(m.JoinFinished(ok: true));
        Assert.Null(m.LastJoinOk);
    }

    [Fact]
    public void Linha_do_handoffs_log_em_UTC()
    {
        var start = new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(
            "2026-10-11T18:02:10Z outage start=2026-10-11T18:00:00Z end=2026-10-11T18:02:10Z gap=130s",
            FailoverMachine.LogLine(start, start + S(130), S(130)));
    }

    [Theory]
    [InlineData(130, "2m 10s")]
    [InlineData(45, "45s")]
    [InlineData(3723, "1h 2m 3s")]
    [InlineData(120, "2m")]
    [InlineData(0, "0s")]
    [InlineData(89.5, "1m 30s")]
    public void Gap_legivel(double seconds, string expected) =>
        Assert.Equal(expected, FailoverMachine.HumanGap(S(seconds)));
}
