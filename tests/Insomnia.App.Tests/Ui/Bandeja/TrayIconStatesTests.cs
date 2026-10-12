namespace Insomnia.App.Tests.Ui.Bandeja;

// Dentro do namespace: Insomnia.App.Tests.Ui.Config (outra pasta de testes) venceria o tipo Config.
using Insomnia.App.Tray;
using Insomnia.App.Ui.Model;
using Insomnia.Core.Abstractions;
using SessionModel = Insomnia.Core.Model.Session;

/// Tom da pílula ⇒ estado do ícone da bandeja (desenho-ui §3.8/§3.9).
public class TrayIconStatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.FromHours(-3));

    private static SessionModel Ending(TimeSpan remaining) => new(Now - TimeSpan.FromHours(1), Now + remaining, []);

    [Theory]
    [InlineData(PillTone.Critical, true, TrayIconState.Warning)]
    [InlineData(PillTone.EndingSoon, true, TrayIconState.Warning)]
    [InlineData(PillTone.Warning, true, TrayIconState.Warning)]
    [InlineData(PillTone.Warning, false, TrayIconState.Warning)]
    [InlineData(PillTone.Paused, true, TrayIconState.Paused)]
    [InlineData(PillTone.Paused, false, TrayIconState.Idle)]
    [InlineData(PillTone.Normal, true, TrayIconState.Running)]
    [InlineData(PillTone.Normal, false, TrayIconState.Idle)]
    public void Tom_vira_estado_do_icone(PillTone tone, bool hasSession, TrayIconState expected) =>
        Assert.Equal(expected, TrayIconStates.From(tone, hasSession));

    [Fact]
    public void Todo_tom_tem_estado_com_e_sem_sessao()
    {
        foreach (var tone in Enum.GetValues<PillTone>())
        {
            Assert.True(Enum.IsDefined(TrayIconStates.From(tone, true)));
            Assert.True(Enum.IsDefined(TrayIconStates.From(tone, false)));
        }
    }

    [Fact]
    public void Ocioso_sem_aviso_e_olho_fechado() =>
        Assert.Equal(TrayIconState.Idle, TrayIconStates.Of(new StatusSnapshot { Now = Now }));

    [Fact]
    public void Ocioso_com_erro_mostra_o_ponto_de_aviso() =>
        Assert.Equal(TrayIconState.Warning, TrayIconStates.Of(new StatusSnapshot { Now = Now, LastError = "Não deu para retomar." }));

    [Fact]
    public void Sessao_normal_e_olho_aberto() =>
        Assert.Equal(TrayIconState.Running, TrayIconStates.Of(new StatusSnapshot { Now = Now, Session = Ending(TimeSpan.FromHours(3)) }));

    [Fact]
    public void Tampa_fechada_com_contagem_pausada_e_pausa() =>
        Assert.Equal(TrayIconState.Paused, TrayIconStates.Of(new StatusSnapshot
        {
            Now = Now,
            Session = Ending(TimeSpan.FromHours(3)),
            CountdownPaused = true,
            Lid = LidState.Closed,
        }));

    [Fact]
    public void Fim_proximo_e_critico_viram_aviso()
    {
        Assert.Equal(TrayIconState.Warning, TrayIconStates.Of(new StatusSnapshot { Now = Now, Session = Ending(TimeSpan.FromMinutes(4)) }));
        Assert.Equal(TrayIconState.Warning, TrayIconStates.Of(new StatusSnapshot { Now = Now, Session = Ending(TimeSpan.FromSeconds(30)) }));
    }

    [Fact]
    public void Aviso_vence_a_pausa()
    {
        var s = new StatusSnapshot
        {
            Now = Now,
            Session = Ending(TimeSpan.FromHours(3)),
            CountdownPaused = true,
            Lid = LidState.Closed,
            Warnings = ["Backstop não armado"],
        };
        Assert.Equal(TrayIconState.Warning, TrayIconStates.Of(s));
    }
}
