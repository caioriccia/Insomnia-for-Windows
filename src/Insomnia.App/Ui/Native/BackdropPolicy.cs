// CONTRATO CONGELADO (desenho-ui §3.2 e §5.5): qual material pedir ao DWM. Puro.
// Fallback obrigatório: Windows antes do 11 (build < 22000), "Efeitos de transparência"
// desligado ou alto contraste ⇒ Solid. Mesmo com Acrylic/Mica escolhido, quem aplica
// (FlyoutWindow) pinta sólido se o DWM recusar: o painel nunca pode ficar invisível.
namespace Insomnia.App.Ui.Native;

public enum Backdrop { Acrylic, Mica, Solid }

public static class BackdropPolicy
{
    /// Primeiro build do Windows 11 (DWMWA_SYSTEMBACKDROP_TYPE só existe a partir dele).
    public const int Windows11Build = 22000;

    /// build < 22000 ⇒ Solid; !transparencyEnabled ⇒ Solid; highContrast ⇒ Solid; senão wanted.
    public static Backdrop Choose(Backdrop wanted, int osBuild, bool transparencyEnabled, bool highContrast)
    {
        if (osBuild < Windows11Build) return Backdrop.Solid;
        if (!transparencyEnabled) return Backdrop.Solid;
        if (highContrast) return Backdrop.Solid;
        return wanted;
    }
}
