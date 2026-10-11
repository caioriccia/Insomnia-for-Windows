// Pisos de bateria (desenho §6.2 "Pisos"): decisão pura + o driver que a aplica durante a sessão.
namespace Insomnia.Core.Power;

using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// End: encerrar a sessão por este motivo. EnergySaver: true liga, false desliga, null não mexe.
/// Cause: causa atual da Economia (mesmo quando não há nada a fazer).
public sealed record FloorDecision(EndReason? End, bool? EnergySaver, EnergySaverCause? Cause);

public static class FloorRules
{
    /// Em ordem: (1) na bateria e abaixo do piso de fim ⇒ BatteryFloor; (2) bateria ilegível
    /// 2x seguidas, na bateria, com piso de fim ⇒ BatteryUnreadable; (3) causa da Economia:
    /// bateria abaixo do piso de Economia, senão tampa fechada com a opção ligada; liga se há
    /// causa e não é nossa, desliga se não há causa e é nossa. Sem histerese (fiel ao original).
    /// Leitura ilegível nunca liga a Economia.
    public static FloorDecision Evaluate(PowerStatus power, LidState lid, Config cfg, bool energySaverOurs, int unreadableMisses)
    {
        var onBattery = !power.OnAc;
        if (onBattery && cfg.EndFloor > 0 && power.BatteryPercent is int p && p < cfg.EndFloor)
            return new FloorDecision(EndReason.BatteryFloor, null, null);
        if (onBattery && cfg.EndFloor > 0 && power.BatteryPercent is null && unreadableMisses >= 2)
            return new FloorDecision(EndReason.BatteryUnreadable, null, null);

        EnergySaverCause? cause = null;
        if (onBattery && power.BatteryPercent is int q && q < cfg.LowPowerFloor) cause = EnergySaverCause.Battery;
        else if (lid == LidState.Closed && cfg.EnergySaverOnLidClose) cause = EnergySaverCause.Lid;

        bool? energySaver = null;
        if (cause is not null && !energySaverOurs) energySaver = true;
        else if (cause is null && energySaverOurs) energySaver = false;
        return new FloorDecision(null, energySaver, cause);
    }
}

/// Aplica FloorRules durante a sessão: ao mudar energia, config, ao começar a sessão e quando
/// o LidWatcher chama EvaluateNowAsync. Bateria ilegível é relida a cada 30 s.
/// Sem fila própria de propósito: a serialização é a do SessionManager (uma fila aqui, com
/// EvaluateNowAsync chamado de dentro de uma transação de tampa, faria deadlock).
public sealed class FloorRuleDriver
{
    public static readonly TimeSpan UnreadableRetry = TimeSpan.FromSeconds(30);

    private readonly SessionManager _manager;
    private readonly IPowerEvents _power;
    private readonly IScheduler _scheduler;
    private readonly IConfigSource _config;
    private readonly Func<LidState> _lid;
    private readonly ILog _log;
    private readonly object _gate = new();

    private int _misses;            // sob _gate
    private IDisposable? _reread;   // sob _gate
    private bool _started, _wasActive;

    public FloorRuleDriver(SessionManager manager, IPowerEvents power, IScheduler scheduler, IConfigSource config, Func<LidState> lid, ILog log)
    {
        _manager = manager;
        _power = power;
        _scheduler = scheduler;
        _config = config;
        _lid = lid;
        _log = log;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _wasActive = _manager.IsActive;
        }
        _power.PowerChanged += OnTrigger;
        _config.Changed += OnTrigger;
        _manager.Changed += OnManagerChanged;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            _reread?.Dispose();
            _reread = null;
            _misses = 0;
        }
        _power.PowerChanged -= OnTrigger;
        _config.Changed -= OnTrigger;
        _manager.Changed -= OnManagerChanged;
    }

    /// Avalia agora. Só age com sessão ativa.
    public async Task EvaluateNowAsync()
    {
        if (!_manager.IsActive)
        {
            lock (_gate) { _misses = 0; _reread?.Dispose(); _reread = null; }
            return;
        }

        var power = _power.ReadPower();
        int misses;
        lock (_gate)
        {
            if (power.BatteryPercent is null && !power.OnAc)
            {
                misses = ++_misses;
                _reread?.Dispose();
                _reread = _scheduler.Schedule(UnreadableRetry, () =>
                {
                    lock (_gate) _reread = null;
                    Forget(EvaluateNowAsync());
                });
            }
            else
            {
                _misses = 0;
                misses = 0;
                _reread?.Dispose();
                _reread = null;
            }
        }

        var decision = FloorRules.Evaluate(power, _lid(), _config.Current, _manager.EnergySaverOurs, misses);
        if (decision.End is { } reason)
        {
            _log.Info($"piso de bateria: encerrando ({reason}; bateria {power.BatteryPercent?.ToString() ?? "ilegível"})");
            await _manager.EndAsync(reason).ConfigureAwait(false);
            return;
        }
        if (decision.EnergySaver is true)
            await _manager.SetEnergySaverAsync(true, decision.Cause!.Value).ConfigureAwait(false);
        else if (decision.EnergySaver is false)
            await _manager.SetEnergySaverAsync(false, EnergySaverCause.Lid).ConfigureAwait(false);
    }

    private void OnTrigger() => Forget(EvaluateNowAsync());

    /// Avalia quando a sessão passa a ativa (começar na bateria baixa já liga a Economia).
    private void OnManagerChanged()
    {
        var active = _manager.IsActive;
        bool rose;
        lock (_gate) { rose = active && !_wasActive; _wasActive = active; }
        if (rose) Forget(EvaluateNowAsync());
    }

    private void Forget(Task task) =>
        task.ContinueWith(t => _log.Error($"regras de bateria falharam: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
