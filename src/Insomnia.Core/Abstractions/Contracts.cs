// CONTRATOS CONGELADOS (desenho §5). Outras frentes implementam e consomem estas
// assinaturas exatamente como estão: não mude nome, tipo nem semântica.
namespace Insomnia.Core.Abstractions;

public interface IClock { DateTimeOffset Now { get; } }

/// Timer injetável. Ação roda em thread do pool (ou no FakeScheduler, na chamada de Advance).
public interface IScheduler { IDisposable Schedule(TimeSpan delay, Action action); }

public interface ILog { void Info(string message); void Error(string message); }
public interface INotifier { void Post(string title, string body); }

// ---------- armazenamento ----------
public sealed class StoreCorruptException(string path, string reason) : Exception($"{path}: {reason}") { public string Path { get; } = path; }
public sealed class StoreWriteException(string path, Exception inner) : Exception($"{path}: {inner.Message}", inner) { public string Path { get; } = path; }
public sealed class RecoveryLockBusyException() : Exception("lock de recuperação ocupado");

public interface IStore
{
    Model.Session? LoadSession();                 // null = ausente; StoreCorruptException = ilegível (arquivo NÃO tocado)
    void SaveSession(Model.Session session);      // atômica; StoreWriteException
    void DeleteSession();                         // idempotente
    string? MoveAsideUnreadableSession();         // devolve o caminho novo; nunca sobrescreve; null se não havia arquivo
    Model.Journal LoadJournal();                  // ausente = Journal.Clean; StoreCorruptException = corrompido (NÃO tocado)
    void SaveJournal(Model.Journal journal);      // atômica; StoreWriteException
    Model.Config LoadConfig();                    // ausente = Config.Default (e grava); corrompido = StoreCorruptException
    void SaveConfig(Model.Config config);
}

public interface IRecoveryLock
{
    IDisposable? TryAcquire();                                            // null = ocupado
    Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken ct); // tenta a cada 50 ms; RecoveryLockBusyException ao estourar
}

public interface IConfigSource
{
    Model.Config Current { get; }
    event Action? Changed;
    void Update(Func<Model.Config, Model.Config> change);   // aplica, normaliza pisos, grava, dispara Changed
}

/// Diário carregado do DISCO sob o lock. Toda mudança de sistema passa por: tx.Write(novo) ANTES do efeito.
public sealed class JournalTransaction
{
    public JournalTransaction(IStore store, Model.Journal loaded) { Store = store; Journal = loaded; }
    public IStore Store { get; }
    public Model.Journal Journal { get; private set; }
    /// Persiste atomicamente e atualiza Journal. StoreWriteException ⇒ Journal fica como estava; o chamador NÃO executa o efeito.
    public void Write(Model.Journal updated) { Store.SaveJournal(updated); Journal = updated; }
}

// ---------- energia ----------
public readonly record struct LidActionValues(Guid SchemeId, uint Ac, uint Dc);   // 0 = nada, 1 = suspender, 2 = hibernar, 3 = desligar
public readonly record struct EnergySaverValues(Guid SchemeId, uint DcThreshold); // ESBATTTHRESHOLD (0..100); 100 = sempre ligado na bateria

public interface ISleepGuard
{
    /// PowerSetRequest(SystemRequired + ExecutionRequired). Ligado ao processo: some sozinho se ele morrer. Idempotente.
    void HoldSystemAwake(bool hold);
    bool IsHoldingSystemAwake { get; }
    LidActionValues ReadLidAction();                              // esquema ativo; lança InvalidOperationException se não ler
    void WriteLidAction(Guid schemeId, uint ac, uint dc);         // escreve AC e DC e aplica (PowerSetActiveScheme); lança se falhar
}

public interface IEnergySaver
{
    EnergySaverValues Read();
    void WriteDcThreshold(Guid schemeId, uint threshold);         // aplica o esquema; lança se falhar
}

public enum LidState { Open, Closed, Unknown }
public readonly record struct PowerStatus(bool OnAc, int? BatteryPercent);   // null = ilegível / sem bateria

public interface IPowerEvents
{
    event Action<LidState>? LidChanged;     // cru, sem debounce (o debounce de 2 s é do Core, LidWatcher)
    event Action? PowerChanged;             // AC/DC ou percentual mudou
    LidState CurrentLid { get; }
    PowerStatus ReadPower();
    void Start();
    void Stop();
}

// ---------- processos ----------
public readonly record struct ProcessIdentity(DateTimeOffset CreationTime, string BootId);
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, ProcessIdentity? Identity, bool IsSuspended, bool HasVisibleWindow, string? ExecutablePath);

public interface IProcessInspector
{
    string BootId { get; }                       // "" se não conseguir ler ⇒ identidades ficam nulas ⇒ nada é sinalizado
    IReadOnlyList<ProcessInfo> Snapshot();       // processos do usuário atual
    ProcessInfo? Lookup(int pid);                // null = não existe
}
public interface IProcessSignals
{
    bool Suspend(int pid);   // NtSuspendProcess; false = falhou (sem exceção)
    bool Resume(int pid);    // NtResumeProcess
}

// ---------- áudio, tela, docker ----------
public sealed class AudioUnavailableException(string reason) : Exception(reason);
public sealed class AudioDeviceMissingException(string deviceId) : Exception($"saída {deviceId} desconectada") { public string DeviceId { get; } = deviceId; }
public sealed record AudioOutput(string DeviceId, string? Name, float Volume, bool Muted);

public interface IAudioControl
{
    AudioOutput ReadDefaultOutput();           // AudioUnavailableException
    AudioOutput Read(string deviceId);         // AudioDeviceMissingException
    void SetMute(string deviceId, bool muted); // AudioDeviceMissingException
    void SetVolume(string deviceId, float volume);
    event Action? DevicesChanged;
}

public interface IDisplayPower { void TurnOff(); void Wake(); }   // sem estado persistente; nunca lançam (só log)

public enum DockerProbe { Idle, Busy, Unavailable }
public interface IDockerProbe { Task<DockerProbe> ProbeAsync(CancellationToken ct); }  // timeout interno 5 s ⇒ Unavailable

// ---------- backstop (Agendador) ----------
public sealed record BackstopDefinition(string TaskName, string ExecutablePath, string Arguments, TimeSpan Interval)
{
    public static BackstopDefinition For(string exePath) => new("Insomnia Backstop", exePath, "--backstop", TimeSpan.FromMinutes(1));
}
public enum BackstopState { Armed, Missing, Different, Unknown }
public interface IBackstopScheduler
{
    BackstopState Query(BackstopDefinition want);   // Armed só se comando, argumento, intervalo e condições de bateria batem
    void Install(BackstopDefinition want);          // cria ou substitui; lança se falhar
    void Remove(string taskName);                   // idempotente
}

// ---------- desfazer ações de tampa (implementado por F4, consumido por F2) ----------
public sealed record LidUndoResult(IReadOnlyList<string> Problems, IReadOnlySet<string> AudioDevicesNotConnected);
public interface ILidUndo
{
    /// Retoma processos (só com identidade exata), restaura áudio (por deviceId), acorda a tela.
    /// Efeito primeiro, depois tx.Write sem a entrada. Entradas não verificáveis/desconectadas FICAM. Nunca lança por falha de sistema.
    Task<LidUndoResult> UndoAsync(JournalTransaction tx, CancellationToken ct);
}

// ---------- rede, navegador, autostart ----------
public interface IWifi
{
    event Action<bool>? ConnectivityChanged;   // true = interface Wi-Fi conectada a alguma rede
    bool IsConnected { get; }
    bool HasProfile(string ssid);
    Task<bool> ConnectAsync(string profileName, CancellationToken ct);   // WlanConnect por perfil salvo; false = não conectou
    void Start();
    void Stop();
}

public sealed record BrowserInstance(string Kind, string DisplayName, int Pid, string ExecutablePath, string CommandLine, ProcessIdentity? Identity);
public interface IBrowserProcesses
{
    IReadOnlyList<BrowserInstance> Scan();  // só processos principais (sem --type=) de chrome.exe / msedge.exe / brave.exe
    Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct);  // fechamento gracioso; true = nenhum restou
    void Launch(string executablePath, IReadOnlyList<string> args);
}

public interface IAutoStart { bool IsRegistered(); void Register(string exePath); void Unregister(); }
