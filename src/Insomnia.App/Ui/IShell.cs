// CONTRATO CONGELADO (desenho-ui §5.2): o que a bandeja pede à casca (pílula + painel) e às
// Configurações. Implementados pelas frentes pilula-painel e configuracoes.
namespace Insomnia.App.Ui;

public enum PanelMode { Start, Session }

/// Pílula + painel. Implementado por Ui/Shell/UiShell (frente pilula-painel). Thread da UI.
public interface IShell : IDisposable
{
    bool IsPanelOpen { get; }
    void ShowPanel();          // modo pelo estado atual (Session se há sessão, senão Start)
    void HidePanel();
    void TogglePanel();
    void Start();              // liga a pílula ao Changed/Tick; chamado uma vez por AppHost.Start
}

/// Janela de Configurações. Implementado por Ui/Settings/SettingsPresenter (frente configuracoes).
public interface ISettingsHost : IDisposable
{
    void ShowSettings();       // cria se preciso, traz para a frente
}
