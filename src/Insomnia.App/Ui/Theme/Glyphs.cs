// CONTRATO CONGELADO (desenho-ui §3.1 e §5.5): glifos da fonte Segoe Fluent Icons (com
// Segoe MDL2 Assets de reserva no Windows 10: os códigos são os mesmos). Cada constante é uma
// string de um caractere da área de uso privado, escrito literalmente (invisível na maioria dos
// editores): o nome do glifo no comentário de cada uma é a referência legível.
// Uso: FontFamily = {DynamicResource Insomnia.Font.Icons}.
namespace Insomnia.App.Ui.Theme;

public static class Glyphs
{
    /// View — pílula/menu: sessão normal.
    public const string Eye = "";
    /// Hide3 — ícone da bandeja ocioso; "Sem sessão".
    public const string EyeClosed = "";
    /// Pílula com a tampa fechada.
    public const string Pause = "";
    /// Avisos; pílula em estado de recuperação.
    public const string Warning = "";
    /// Recent — fim próximo; "termina às".
    public const string Clock = "";
    /// Chips "+15 min".
    public const string Add = "";
    /// Engrenagem.
    public const string Settings = "";
    /// Tampa.
    public const string Laptop = "";
    /// Battery10 — bateria.
    public const string Battery = "";
    /// BatteryCharging10 — bateria carregando.
    public const string BatteryCharging = "";
    /// Rede.
    public const string Wifi = "";
    /// WifiError — rede com problema.
    public const string WifiError = "";
    /// Frigid — apps congelados.
    public const string Snowflake = "";
    /// Áudio mudo.
    public const string Mute = "";
    /// Tela apagada.
    public const string Display = "";
    /// Devices3 — Docker pausado.
    public const string Docker = "";
    /// Alça para fechar.
    public const string ChevronUp = "";
    /// Sair.
    public const string Power = "";
    /// OpenPane — "Abrir painel" no menu.
    public const string OpenPane = "";
    /// "Iniciar sessão" no menu.
    public const string Play = "";
    /// "Relançar navegador".
    public const string Refresh = "";
    /// "Parar de esperar".
    public const string Dismiss = "";
    /// Navegador.
    public const string Globe = "";
    /// Página "Agentes" das Configurações (§5.4, SettingsModel.Nav).
    public const string Robot = "";
}
