using Insomnia.Core.Tests.Fakes;
using Insomnia.Core.Time;

namespace Insomnia.Core.Tests.Fundacao;

using Insomnia.Core.Model;

/// SessionMath, contagem, texto compacto, parser e formatador de duração.
public class TimeTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultStart;
    private static readonly TimeSpan Max = TimeSpan.FromDays(30);

    // ---------- Extended ----------

    [Fact]
    public void Extended_soma_ao_fim_e_registra_a_extensao()
    {
        var s = new Session(Now.AddHours(-1), Now.AddHours(1), [600]);
        var e = SessionMath.Extended(s, TimeSpan.FromMinutes(30), Now, Max);
        Assert.Equal(Now.AddHours(1.5), e.EndsAt);
        Assert.Equal(s.StartedAt, e.StartedAt);
        Assert.Equal([600, 1800], e.Extensions.ToArray());
        Assert.Equal([600], s.Extensions.ToArray()); // original intacto
    }

    [Fact]
    public void Extended_nunca_passa_de_agora_mais_o_maximo()
    {
        var s = new Session(Now, Now.AddDays(29), []);
        var e = SessionMath.Extended(s, TimeSpan.FromDays(5), Now, Max);
        Assert.Equal(Now + Max, e.EndsAt);
        Assert.Equal([(int)TimeSpan.FromDays(1).TotalSeconds], e.Extensions.ToArray());
    }

    [Fact]
    public void Extended_de_sessao_expirada_reinicia_de_agora()
    {
        var s = new Session(Now.AddHours(-5), Now.AddHours(-1), []);
        var e = SessionMath.Extended(s, TimeSpan.FromHours(1), Now, Max);
        Assert.Equal(Now.AddHours(1), e.EndsAt);
        Assert.False(e.IsExpired(Now));
        Assert.Equal([7200], e.Extensions.ToArray()); // do fim antigo até o novo
    }

    [Fact]
    public void Extended_com_tempo_negativo_nao_encurta()
    {
        var s = new Session(Now, Now.AddHours(1), []);
        Assert.Equal(s.EndsAt, SessionMath.Extended(s, TimeSpan.FromHours(-1), Now, Max).EndsAt);
    }

    [Fact]
    public void Extended_tolera_extensions_default()
    {
        var s = new Session(Now, Now.AddHours(1), default);
        Assert.Single(SessionMath.Extended(s, TimeSpan.FromMinutes(1), Now, Max).Extensions);
    }

    [Fact]
    public void NewSession_prende_a_duracao()
    {
        Assert.Equal(Now.AddMinutes(1), SessionMath.NewSession(Now, TimeSpan.FromSeconds(5), Max).EndsAt);
        Assert.Equal(Now + Max, SessionMath.NewSession(Now, TimeSpan.FromDays(90), Max).EndsAt);
        Assert.Equal(Now.AddHours(4), SessionMath.NewSession(Now, TimeSpan.FromHours(4), Max).EndsAt);
    }

    // ---------- contagem ----------

    [Theory]
    [InlineData(59 * 60, CountdownShape.Minutes)]
    [InlineData(3600, CountdownShape.Hours)]
    [InlineData(86399, CountdownShape.Hours)]
    [InlineData(86400, CountdownShape.Days)]
    public void ShapeFor_pela_duracao_inicial(int seconds, CountdownShape expected) =>
        Assert.Equal(expected, SessionMath.ShapeFor(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(1800.0, CountdownShape.Minutes, "30:00")]
    [InlineData(1799.2, CountdownShape.Minutes, "30:00")]  // fração arredonda para cima
    [InlineData(9.0, CountdownShape.Minutes, "00:09")]
    [InlineData(0.001, CountdownShape.Minutes, "00:01")]
    [InlineData(0.0, CountdownShape.Minutes, "00:00")]
    [InlineData(-5.0, CountdownShape.Minutes, "00:00")]
    [InlineData(247.0, CountdownShape.Hours, "0:04:07")]
    [InlineData(5025.0, CountdownShape.Hours, "1:23:45")]
    [InlineData(266645.0, CountdownShape.Days, "3d 2:04:05")]
    [InlineData(5025.0, CountdownShape.Days, "1:23:45")]   // menos de um dia: perde o "Nd"
    public void FormatCountdown(double seconds, CountdownShape shape, string expected) =>
        Assert.Equal(expected, SessionMath.FormatCountdown(TimeSpan.FromSeconds(seconds), shape));

    [Theory]
    [InlineData(3 * 86400 + 7200, "3d")]
    [InlineData(4 * 3600 + 59 * 60, "4h")]
    [InlineData(3599, "59m")]
    [InlineData(60, "1m")]
    [InlineData(59, "<1m")]
    [InlineData(0, "<1m")]
    [InlineData(-30, "<1m")]
    public void FormatCompact(int seconds, string expected) =>
        Assert.Equal(expected, SessionMath.FormatCompact(TimeSpan.FromSeconds(seconds)));

    // ---------- duração ----------

    [Theory]
    [InlineData("30", 30 * 60)]
    [InlineData(" 30 ", 30 * 60)]
    [InlineData("1h30m", 90 * 60)]
    [InlineData("1h30", 90 * 60)]
    [InlineData("1H 30M", 90 * 60)]
    [InlineData("3d", 3 * 86400)]
    [InlineData("1d 2h 15m", 86400 + 2 * 3600 + 15 * 60)]
    [InlineData("2h", 7200)]
    [InlineData("45min", 45 * 60)]
    [InlineData("0h30m", 30 * 60)]
    public void Parser_aceita(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DurationParser.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0h0m")]
    [InlineData("abc")]
    [InlineData("1.5h")]
    [InlineData("1,5h")]
    [InlineData("-30")]
    [InlineData("30s")]
    [InlineData("2h 1d")]   // fora de ordem
    [InlineData("1h1h")]    // unidade repetida
    [InlineData("h")]
    [InlineData("1234567")] // número absurdo
    public void Parser_recusa(string text) => Assert.Null(DurationParser.Parse(text));

    [Theory]
    [InlineData(1800, "30m")]
    [InlineData(3600, "1h")]
    [InlineData(7200, "2h")]
    [InlineData(5400, "1h30m")]
    [InlineData(259200, "3d")]
    [InlineData(129600, "1d12h")]
    [InlineData(90061, "1d1h1m")]
    [InlineData(59, "0m")]
    [InlineData(0, "0m")]
    public void Chip(int seconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Chip(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Chip_e_Parse_vao_e_voltam_nos_presets()
    {
        foreach (var preset in Config.Default.Presets)
        {
            var d = TimeSpan.FromSeconds(preset);
            Assert.Equal(d, DurationParser.Parse(DurationFormat.Chip(d)));
        }
    }
}
