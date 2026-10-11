// Fakes de processos, áudio, tela, Docker, rede, navegador e autostart (desenho §5.1).
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Tests.Fakes;

/// Mundo de processos simulado: IProcessInspector + IProcessSignals sobre a mesma tabela.
/// Identidade = (creationTime, BootId); com BootId "" as identidades ficam nulas.
/// Suspend só tem efeito em pid existente; Signals registra TODAS as tentativas
/// ("suspend"/"resume", pid), inclusive as que falharam.
public sealed class FakeProcessWorld(Timeline? timeline = null) : IProcessInspector, IProcessSignals
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _processes = [];

    public string BootId { get; set; } = "17";
    public HashSet<int> Suspended { get; } = [];
    public bool FailSuspend { get; set; }
    public bool FailResume { get; set; }
    /// Falha só para estes pids (além de FailSuspend/FailResume).
    public HashSet<int> FailSuspendPids { get; } = [];
    public HashSet<int> FailResumePids { get; } = [];
    public List<(string, int)> Signals { get; } = [];
    public int SnapshotCalls { get; private set; }

    public ProcessInfo Add(int pid, string name, int parent, DateTimeOffset creationTime,
        bool hasVisibleWindow = false, string? executablePath = null)
    {
        lock (_gate)
        {
            _processes[pid] = new Entry(pid, parent, name, creationTime, hasVisibleWindow, executablePath);
            Suspended.Remove(pid);
            return Info(_processes[pid]);
        }
    }

    /// O processo morre (some da tabela e do conjunto de suspensos).
    public void Kill(int pid)
    {
        lock (_gate)
        {
            _processes.Remove(pid);
            Suspended.Remove(pid);
        }
    }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (_gate)
        {
            SnapshotCalls++;
            return [.. _processes.Values.OrderBy(e => e.Pid).Select(Info)];
        }
    }

    public ProcessInfo? Lookup(int pid)
    {
        lock (_gate) return _processes.TryGetValue(pid, out var e) ? Info(e) : null;
    }

    public bool Suspend(int pid)
    {
        timeline?.Add($"process.Suspend({pid})");
        lock (_gate)
        {
            Signals.Add(("suspend", pid));
            if (FailSuspend || FailSuspendPids.Contains(pid) || !_processes.ContainsKey(pid)) return false;
            Suspended.Add(pid);
            return true;
        }
    }

    public bool Resume(int pid)
    {
        timeline?.Add($"process.Resume({pid})");
        lock (_gate)
        {
            Signals.Add(("resume", pid));
            if (FailResume || FailResumePids.Contains(pid) || !_processes.ContainsKey(pid)) return false;
            Suspended.Remove(pid);
            return true;
        }
    }

    private ProcessInfo Info(Entry e) => new(
        e.Pid, e.Parent, e.Name,
        string.IsNullOrEmpty(BootId) ? null : new ProcessIdentity(e.CreationTime, BootId),
        Suspended.Contains(e.Pid), e.HasVisibleWindow, e.ExecutablePath);

    private sealed record Entry(int Pid, int Parent, string Name, DateTimeOffset CreationTime, bool HasVisibleWindow, string? ExecutablePath);
}

/// Saídas de áudio simuladas. Padrão: uma saída "Alto-falantes" (volume 0,62, sem mudo) como
/// padrão. Disconnect remove e dispara DevicesChanged; Connect (re)adiciona e dispara.
/// FailRead: ReadDefaultOutput lança AudioUnavailableException. FailSet: SetMute/SetVolume
/// lançam InvalidOperationException.
public sealed class FakeAudio(Timeline? timeline = null) : IAudioControl
{
    public const string SpeakersId = "{0.0.0.00000000}.{fake-speakers}";

    public Dictionary<string, AudioOutput> Devices { get; } = new()
    {
        [SpeakersId] = new AudioOutput(SpeakersId, "Alto-falantes", 0.62f, false),
    };
    public string? DefaultDeviceId { get; set; } = SpeakersId;
    public bool FailRead { get; set; }
    public bool FailSet { get; set; }
    /// "mute(id,true)", "volume(id,0.62)", na ordem.
    public List<string> Calls { get; } = [];

    public event Action? DevicesChanged;

    public AudioOutput ReadDefaultOutput()
    {
        timeline?.Add("audio.ReadDefault");
        if (FailRead) throw new AudioUnavailableException("leitura de áudio falhou (fake)");
        if (DefaultDeviceId is null || !Devices.TryGetValue(DefaultDeviceId, out var d))
            throw new AudioUnavailableException("sem saída padrão (fake)");
        return d;
    }

    public AudioOutput Read(string deviceId) =>
        Devices.TryGetValue(deviceId, out var d) ? d : throw new AudioDeviceMissingException(deviceId);

    public void SetMute(string deviceId, bool muted)
    {
        timeline?.Add($"audio.SetMute({muted})");
        Calls.Add($"mute({deviceId},{muted})");
        var d = Read(deviceId);
        if (FailSet) throw new InvalidOperationException("mudo falhou (fake)");
        Devices[deviceId] = d with { Muted = muted };
    }

    public void SetVolume(string deviceId, float volume)
    {
        timeline?.Add("audio.SetVolume");
        Calls.Add(FormattableString.Invariant($"volume({deviceId},{volume})"));
        var d = Read(deviceId);
        if (FailSet) throw new InvalidOperationException("volume falhou (fake)");
        Devices[deviceId] = d with { Volume = volume };
    }

    public void Disconnect(string deviceId)
    {
        Devices.Remove(deviceId);
        DevicesChanged?.Invoke();
    }

    public void Connect(AudioOutput output)
    {
        Devices[output.DeviceId] = output;
        DevicesChanged?.Invoke();
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke();
}

public sealed class FakeDisplay(Timeline? timeline = null) : IDisplayPower
{
    public int TurnOffCount { get; private set; }
    public int WakeCount { get; private set; }
    /// "off" / "wake", na ordem.
    public List<string> Calls { get; } = [];

    public void TurnOff() { timeline?.Add("display.TurnOff"); TurnOffCount++; Calls.Add("off"); }
    public void Wake() { timeline?.Add("display.Wake"); WakeCount++; Calls.Add("wake"); }
}

/// Result = resposta padrão; Results (fila) responde primeiro, uma por chamada (ex.: Idle
/// e depois Busy para a dupla checagem).
public sealed class FakeDocker(Timeline? timeline = null) : IDockerProbe
{
    public DockerProbe Result { get; set; } = DockerProbe.Idle;
    public Queue<DockerProbe> Results { get; } = new();
    public int ProbeCount { get; private set; }

    public Task<DockerProbe> ProbeAsync(CancellationToken ct)
    {
        timeline?.Add("docker.Probe");
        ProbeCount++;
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Result);
    }
}

/// Wi-Fi simulado. Profiles = perfis salvos (sem diferenciar caixa). ConnectAsync registra o
/// pedido em ConnectCalls e devolve ConnectResult; se true, marca IsConnected (sem evento:
/// o teste chama RaiseConnectivity quando quiser simular a notificação).
public sealed class FakeWifi : IWifi
{
    public event Action<bool>? ConnectivityChanged;
    public bool IsConnected { get; set; } = true;
    public HashSet<string> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ConnectResult { get; set; } = true;
    public List<string> ConnectCalls { get; } = [];
    public bool Started { get; private set; }

    public bool HasProfile(string ssid) => Profiles.Contains(ssid);

    public Task<bool> ConnectAsync(string profileName, CancellationToken ct)
    {
        ConnectCalls.Add(profileName);
        if (ConnectResult) IsConnected = true;
        return Task.FromResult(ConnectResult);
    }

    public void Start() => Started = true;
    public void Stop() => Started = false;

    public void RaiseConnectivity(bool connected)
    {
        IsConnected = connected;
        ConnectivityChanged?.Invoke(connected);
    }
}

/// Navegadores simulados. QuitAllAsync: QuitResult true ⇒ remove as instâncias do kind e
/// devolve true; false ⇒ deixa e devolve false. Launch registra em Launches e chama OnLaunch
/// (o teste usa para fazer a instância "aparecer"); FailLaunch faz Launch lançar.
public sealed class FakeBrowserProcesses : IBrowserProcesses
{
    public List<BrowserInstance> Instances { get; } = [];
    public bool QuitResult { get; set; } = true;
    public List<string> QuitCalls { get; } = [];
    public List<(string Path, IReadOnlyList<string> Args)> Launches { get; } = [];
    public bool FailLaunch { get; set; }
    public Action<string, IReadOnlyList<string>>? OnLaunch { get; set; }
    public int ScanCalls { get; private set; }

    public IReadOnlyList<BrowserInstance> Scan() { ScanCalls++; return [.. Instances]; }

    /// Teto pedido em cada QuitAllAsync (R11).
    public List<TimeSpan> QuitTimeouts { get; } = [];

    public Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct)
    {
        QuitCalls.Add(kind);
        QuitTimeouts.Add(timeout);
        if (QuitResult) Instances.RemoveAll(i => i.Kind == kind);
        return Task.FromResult(QuitResult);
    }

    public void Launch(string executablePath, IReadOnlyList<string> args)
    {
        if (FailLaunch) throw new InvalidOperationException("lançamento falhou (fake)");
        Launches.Add((executablePath, args));
        OnLaunch?.Invoke(executablePath, args);
    }
}

public sealed class FakeAutoStart : IAutoStart
{
    public string? RegisteredPath { get; set; }
    public List<string> Calls { get; } = [];
    public bool IsRegistered() => RegisteredPath is not null;
    public void Register(string exePath) { Calls.Add("register"); RegisteredPath = exePath; }
    public void Unregister() { Calls.Add("unregister"); RegisteredPath = null; }
}
