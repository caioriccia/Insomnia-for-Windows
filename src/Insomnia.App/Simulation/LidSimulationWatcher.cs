// Simulação de tampa para ensaio sem fechar o notebook (desenho §3, §9). Só existe em build
// Debug ou com INSOMNIA_LID_SIMULATION definido: o Release distribuído não contém o código.
//
// Decora o IPowerEvents real. A cada segundo procura o arquivo simulate-lid (na pasta de dados,
// ou dentro de INSOMNIA_HOME); conteúdo "closed" ou "open". O arquivo é CONSUMIDO por rename
// (vira simulate-lid.consumed-<guid> e é apagado): o rename é atômico, então um comando nunca
// é lido duas vezes nem pela metade. O estado simulado vale até o próximo evento REAL de tampa,
// que é repassado e apaga a simulação. Energia (AC/bateria) passa direto.
#if DEBUG || INSOMNIA_LID_SIMULATION
using System.IO;
using Insomnia.Core.Abstractions;
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.App.Simulation;

public sealed class LidSimulationWatcher : IPowerEvents, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IPowerEvents _inner;
    private readonly IScheduler _scheduler;
    private readonly ILog _log;
    private readonly object _gate = new();
    private IDisposable? _poll;
    private bool _running;
    private LidState? _simulated;

    public LidSimulationWatcher(IPowerEvents inner, string simulateLidPath, IScheduler scheduler, ILog log)
    {
        _inner = inner;
        FilePath = Path.GetFullPath(simulateLidPath);
        _scheduler = scheduler;
        _log = log;
    }

    public string FilePath { get; }

    public event Action<LidState>? LidChanged;
    public event Action? PowerChanged;

    public LidState CurrentLid { get { lock (_gate) return _simulated ?? _inner.CurrentLid; } }

    /// Estado simulado em vigor (null = segue a tampa real).
    public LidState? Simulated { get { lock (_gate) return _simulated; } }

    public PowerStatus ReadPower() => _inner.ReadPower();

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }
        _inner.LidChanged += OnRealLid;
        _inner.PowerChanged += OnRealPower;
        _inner.Start();
        _log.Info($"simulação de tampa ligada: escreva 'closed' ou 'open' em {FilePath}");
        Arm();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _poll?.Dispose();
            _poll = null;
        }
        _inner.LidChanged -= OnRealLid;
        _inner.PowerChanged -= OnRealPower;
        _inner.Stop();
    }

    public void Dispose() => Stop();

    /// "closed"/"fechada" ⇒ Closed; "open"/"aberta" ⇒ Open; qualquer outra coisa ⇒ null.
    public static LidState? Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "closed" or "fechada" => LidState.Closed,
        "open" or "aberta" => LidState.Open,
        _ => null,
    };

    /// Uma rodada de leitura (o timer chama; os testes também). Devolve o comando aplicado.
    public LidState? PollOnce()
    {
        if (!File.Exists(FilePath)) return null;
        var claimed = FilePath + ".consumed-" + Guid.NewGuid().ToString("N");
        string text;
        try
        {
            File.Move(FilePath, claimed);
            text = File.ReadAllText(claimed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"simulação de tampa: não deu para consumir {FilePath}: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(claimed)) File.Delete(claimed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (Parse(text) is not { } state)
        {
            _log.Error($"simulação de tampa: conteúdo '{text.Trim()}' ignorado (use 'closed' ou 'open')");
            return null;
        }
        lock (_gate) _simulated = state;
        _log.Info($"simulação de tampa: {(state == LidState.Closed ? "fechada" : "aberta")}");
        LidChanged?.Invoke(state);
        return state;
    }

    private void Arm()
    {
        lock (_gate)
        {
            if (!_running) return;
            _poll = _scheduler.Schedule(PollInterval, () =>
            {
                try { PollOnce(); }
                catch (Exception ex) { _log.Error($"simulação de tampa: {ex.Message}"); }
                Arm();
            });
        }
    }

    private void OnRealLid(LidState state)
    {
        lock (_gate) _simulated = null;
        LidChanged?.Invoke(state);
    }

    private void OnRealPower() => PowerChanged?.Invoke();
}
#endif
