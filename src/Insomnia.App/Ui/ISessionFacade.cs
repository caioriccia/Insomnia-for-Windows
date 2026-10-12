// CONTRATO CONGELADO (desenho-ui §5.1): o que a UI nova consome do AppHost. Nome, tipo e
// semântica não mudam; quem consome não assume mais do que está escrito aqui.
using Insomnia.App.Tray;            // StatusSnapshot (não muda de lugar)
using Insomnia.Core.Abstractions;   // IConfigSource, ILog, IAutoStart, IWifi, IProcessInspector

namespace Insomnia.App.Ui;

/// Implementado por AppHost. Tudo aqui é chamado e disparado NO THREAD DA UI (Dispatcher).
public interface ISessionFacade
{
    StatusSnapshot Snapshot();                 // retrato vivo (como hoje)
    IConfigSource Config { get; }
    ILog Log { get; }
    IAutoStart AutoStart { get; }
    IWifi Wifi { get; }
    IProcessInspector Inspector { get; }
    string ConfigPath { get; }
    string ExecutablePath { get; }
    bool IsSimulationBuild { get; }

    /// Sessão, config, rede ou leitura lenta mudou: redesenhar tudo que estiver visível.
    event Action? Changed;
    /// Virada de segundo (alinhada ao relógio; NÃO dispara com CountdownPaused). Contagens e ícone.
    event Action? Tick;

    Task<string?> StartSessionAsync(TimeSpan duration);   // null = começou; senão mensagem pt-BR
    Task<string?> ExtendSessionAsync(TimeSpan by);
    Task EndSessionAsync();
    Task RelaunchBrowserAsync(string kind);
    Task StopWaitingAudioAsync(string deviceId);
    Task QuitAsync();
    void RefreshStatusInBackground();
}
