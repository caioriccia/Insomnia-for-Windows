// CONTRATO CONGELADO (desenho-ui §5.3). Quando a pílula pode aparecer (§4 "Tela cheia").
// Puro: quem chama lê o primeiro plano (Dwm.QueryForeground) e o estado do painel.
namespace Insomnia.App.Ui.Model;

/// O que está em primeiro plano, pela SHQueryUserNotificationState.
public enum ForegroundKind { Normal, Fullscreen, Presentation }

public static class PillVisibilityPolicy
{
    /// Só durante sessão, com o painel fechado (ele ocupa o lugar da pílula) e sem tela cheia/apresentação.
    public static bool ShouldShow(bool hasSession, bool panelOpen, ForegroundKind fg) =>
        hasSession && !panelOpen && fg == ForegroundKind.Normal;
}
