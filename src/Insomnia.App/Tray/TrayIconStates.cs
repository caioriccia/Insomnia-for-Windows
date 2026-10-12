// Estado do ícone da bandeja (desenho-ui §3.8) a partir do tom da pílula (§3.9). Puro: o
// TrayController só lê o retrato, pede o tom ao PillPresenter e entrega o resultado ao
// TrayIconRenderer. Qualquer coisa que peça atenção (aviso, fim próximo, crítico) vira o ponto
// de aviso; tampa fechada vira o selo de pausa; sessão sem mais nada, o olho aberto.
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Tray;

public static class TrayIconStates
{
    /// Critical/EndingSoon/Warning ⇒ Warning; Paused ⇒ Paused; sessão ⇒ Running; senão Idle.
    /// Aviso vale mesmo sem sessão (ex.: erro ao retomar o estado); pausa sem sessão não existe.
    public static TrayIconState From(PillTone tone, bool hasSession) => tone switch
    {
        PillTone.Critical or PillTone.EndingSoon or PillTone.Warning => TrayIconState.Warning,
        PillTone.Paused when hasSession => TrayIconState.Paused,
        _ => hasSession ? TrayIconState.Running : TrayIconState.Idle,
    };

    /// Atalho do TrayController: o tom pela mesma regra da pílula.
    public static TrayIconState Of(StatusSnapshot s) => From(PillPresenter.ToneOf(s), s.Session is not null);
}
