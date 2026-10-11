// Observador da tampa com debounce de 2 s (desenho §7.2). O IPowerEvents entrega o sinal
// cru; aqui uma mudança só é entregue se ficar 2 s sem voltar. Mudança que volta ao estado
// já entregue durante a espera é descartada (oscilação). Cada entrega é numerada (eventNo)
// e as ações rodam em série, uma de cada vez, na ordem de entrega.
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Lid;

public sealed class LidWatcher
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly IPowerEvents _power;
    private readonly IScheduler _scheduler;
    private readonly LidActions _actions;
    private readonly ILog _log;

    private readonly object _gate = new();
    private bool _started;
    private LidState _delivered = LidState.Unknown;
    private IDisposable? _pending;
    private int _generation;
    private int _events;
    private Task _tail = Task.CompletedTask;

    public LidWatcher(IPowerEvents power, IScheduler scheduler, LidActions actions, ILog log)
    {
        _power = power;
        _scheduler = scheduler;
        _actions = actions;
        _log = log;
        actions.AttachScheduler(scheduler);
    }

    /// Último estado ENTREGUE (já passado pelo debounce).
    public LidState Current { get { lock (_gate) return _delivered; } }

    /// Quantos eventos já foram entregues.
    public int EventCount { get { lock (_gate) return _events; } }

    /// Termina quando a fila de ações entregues até agora terminar.
    public Task Idle { get { lock (_gate) return _tail; } }

    public void Start()
    {
        LidState initial;
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            initial = _delivered = _power.CurrentLid;
        }
        _actions.NoteLid(initial);
        _power.LidChanged += OnRaw;
        _log.Info($"tampa: observador ligado (estado {Describe(initial)})");
    }

    public void Stop()
    {
        _power.LidChanged -= OnRaw;
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            _pending?.Dispose();
            _pending = null;
            _generation++;
        }
    }

    private void OnRaw(LidState state)
    {
        lock (_gate)
        {
            if (!_started) return;
            if (state == LidState.Unknown) return; // sem leitura: não muda o que foi entregue
            _pending?.Dispose();
            _pending = null;
            var gen = ++_generation;
            if (state == _delivered)
            {
                _log.Info($"tampa: oscilação descartada (voltou a {Describe(state)} antes de 2 s)");
                return;
            }
            _pending = _scheduler.Schedule(Debounce, () => Settle(gen, state));
        }
    }

    private void Settle(int gen, LidState state)
    {
        int eventNo;
        lock (_gate)
        {
            if (!_started || gen != _generation || state == _delivered) return;
            _pending = null;
            _delivered = state;
            eventNo = ++_events;
            _actions.NoteEvent(eventNo, state);
            _tail = RunAfterAsync(_tail, eventNo, state);
        }
        _log.Info($"tampa: {Describe(state)} (evento {eventNo})");
    }

    private async Task RunAfterAsync(Task previous, int eventNo, LidState state)
    {
        // Sai do lock antes de rodar qualquer ação (e da thread do timer).
        await Task.Yield();
        try { await previous.ConfigureAwait(false); } catch { /* cada ação já registra a própria falha */ }
        try
        {
            if (state == LidState.Closed) await _actions.OnCloseAsync(eventNo).ConfigureAwait(false);
            else await _actions.OnOpenAsync(eventNo).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"tampa: ação do evento {eventNo} falhou ({ex.Message})");
        }
    }

    private static string Describe(LidState s) => s switch
    {
        LidState.Open => "aberta",
        LidState.Closed => "fechada",
        _ => "desconhecida",
    };
}
