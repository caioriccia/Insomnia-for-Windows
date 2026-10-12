namespace Insomnia.App.Tests.Ui.Modelo;

// Dentro do namespace: Insomnia.App.Tests.Ui.Config (outra pasta de testes) venceria o tipo Config.
using Insomnia.App.Tray;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using SessionModel = Insomnia.Core.Model.Session;

public class PillPresenterTests
{
    // 18:00 em -03:00; sessões que terminam daqui a `remaining`.
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    private static SessionModel Ending(TimeSpan remaining) => new(Now - TimeSpan.FromHours(1), Now + remaining, []);

    private static StatusSnapshot With(TimeSpan remaining) => new() { Now = Now, Session = Ending(remaining) };

    [Theory]
    [InlineData(2 * 86400 + 4 * 3600 + 59 * 60, "2d 4h")]
    [InlineData(3 * 3600 + 42 * 60 + 15, "3h 42m")]
    [InlineData(42 * 60 + 59, "42m")]
    [InlineData(5 * 60, "5m")]
    [InlineData(4 * 60 + 59, "4m 59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(42, "42s")]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    public void Compact_por_faixa(int seconds, string expected) =>
        Assert.Equal(expected, PillPresenter.Compact(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Compact_arredonda_fracao_de_segundo_para_cima()
    {
        Assert.Equal("42s", PillPresenter.Compact(TimeSpan.FromSeconds(41.2)));
        Assert.Equal("1s", PillPresenter.Compact(TimeSpan.FromMilliseconds(1)));
        Assert.Equal("4m 59s", PillPresenter.Compact(TimeSpan.FromSeconds(298.5)));
        // 4:59,5 sobe para 5:00 e já sai da faixa dos segundos.
        Assert.Equal("5m", PillPresenter.Compact(TimeSpan.FromSeconds(299.5)));
    }

    [Fact]
    public void Sessao_normal_tem_olho_e_ponto_normal()
    {
        var pill = PillPresenter.From(With(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42)), visibility: true);

        Assert.True(pill.Visible);
        Assert.Equal(PillTone.Normal, pill.Tone);
        Assert.Equal(Glyphs.Eye, pill.Glyph);
        Assert.Equal("3h 42m", pill.Text);
        Assert.Equal(PillTone.Normal, pill.Dot);
    }

    [Fact]
    public void Sem_sessao_nao_aparece()
    {
        var pill = PillPresenter.From(new StatusSnapshot { Now = Now }, visibility: true);

        Assert.False(pill.Visible);
        Assert.Null(pill.Dot);
        Assert.Equal(TrayTexts.IdleTooltip, pill.Tooltip);
    }

    [Fact]
    public void Visibilidade_falsa_esconde_mesmo_com_sessao()
    {
        var pill = PillPresenter.From(With(TimeSpan.FromHours(2)), visibility: false);

        Assert.False(pill.Visible);
        Assert.Equal("2h 0m", pill.Text);
    }

    [Fact]
    public void Tooltip_e_o_da_bandeja()
    {
        var s = With(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(15)) with { CountdownPaused = true };

        var pill = PillPresenter.From(s, visibility: true);

        Assert.Equal(TrayTexts.Tooltip(s.Session, Now, paused: true), pill.Tooltip);
        Assert.Equal("Insomnia · 3:42:15 · termina 21:42 · tampa fechada", pill.Tooltip);
    }

    [Fact]
    public void Tampa_fechada_pausa()
    {
        var s = With(TimeSpan.FromHours(2)) with { CountdownPaused = true };

        var pill = PillPresenter.From(s, visibility: true);

        Assert.Equal(PillTone.Paused, pill.Tone);
        Assert.Equal(Glyphs.Pause, pill.Glyph);
        Assert.Equal(PillTone.Paused, pill.Dot);
    }

    [Fact]
    public void Tampa_fechada_antes_do_debounce_tambem_pausa()
    {
        var s = With(TimeSpan.FromHours(2)) with { Lid = LidState.Closed };

        Assert.Equal(PillTone.Paused, PillPresenter.ToneOf(s));
    }

    [Fact]
    public void Aviso_vence_tampa_e_o_ponto_mostra_a_tampa()
    {
        var s = With(TimeSpan.FromHours(2)) with { CountdownPaused = true, LastError = "algo falhou" };

        var pill = PillPresenter.From(s, visibility: true);

        Assert.Equal(PillTone.Warning, pill.Tone);
        Assert.Equal(Glyphs.Warning, pill.Glyph);
        Assert.Equal(PillTone.Paused, pill.Dot);
    }

    [Fact]
    public void Fim_proximo_vence_aviso()
    {
        var s = With(TimeSpan.FromMinutes(4)) with { Warnings = ["x"] };

        var pill = PillPresenter.From(s, visibility: true);

        Assert.Equal(PillTone.EndingSoon, pill.Tone);
        Assert.Equal(Glyphs.Clock, pill.Glyph);
        Assert.Equal(PillTone.Warning, pill.Dot);
    }

    [Fact]
    public void Critico_vence_aviso_e_tampa()
    {
        var s = With(TimeSpan.FromSeconds(30)) with { NetworkWarning = "rede", CountdownPaused = true };

        var pill = PillPresenter.From(s, visibility: true);

        Assert.Equal(PillTone.Critical, pill.Tone);
        Assert.Equal(Glyphs.Clock, pill.Glyph);
        Assert.Equal(PillTone.Warning, pill.Dot);
        Assert.Equal("30s", pill.Text);
    }

    [Theory]
    [InlineData(300, PillTone.Normal)]       // 5 min exatos ainda não é fim próximo
    [InlineData(299, PillTone.EndingSoon)]
    [InlineData(60, PillTone.EndingSoon)]
    [InlineData(59, PillTone.Critical)]
    public void Limites_de_fim_proximo_e_critico(int seconds, PillTone expected) =>
        Assert.Equal(expected, PillPresenter.ToneOf(With(TimeSpan.FromSeconds(seconds))));

    [Fact]
    public void Sem_sessao_tampa_fechada_nao_pausa()
    {
        var s = new StatusSnapshot { Now = Now, Lid = LidState.Closed, CountdownPaused = true };

        Assert.Equal(PillTone.Normal, PillPresenter.ToneOf(s));
    }

    [Fact]
    public void Cada_fonte_de_aviso_conta()
    {
        var idle = new StatusSnapshot { Now = Now };
        Assert.False(PillPresenter.HasWarnings(idle));
        Assert.False(PillPresenter.HasWarnings(idle with { Warnings = ["  "], LastError = "" }));

        Assert.True(PillPresenter.HasWarnings(idle with { Warnings = ["w"] }));
        Assert.True(PillPresenter.HasWarnings(idle with { LastError = "e" }));
        Assert.True(PillPresenter.HasWarnings(idle with { NetworkWarning = "n" }));
        Assert.True(PillPresenter.HasWarnings(idle with { RelaunchProblems = new Dictionary<string, string> { ["chrome"] = "p" } }));
        Assert.True(PillPresenter.HasWarnings(idle with { WaitingAudio = [new WaitingAudio("id", "Fones")] }));
        Assert.True(PillPresenter.HasWarnings(idle with { Browsers = [new BrowserStatus("chrome", "Google Chrome", Unthrottled: false)] }));
        Assert.False(PillPresenter.HasWarnings(idle with { Browsers = [new BrowserStatus("chrome", "Google Chrome", Unthrottled: true)] }));
    }
}
