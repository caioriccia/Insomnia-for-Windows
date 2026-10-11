using System.Collections.Concurrent;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Browser;

/// Um navegador em execução e se ele está livre da limitação (as duas flags presentes).
public sealed record BrowserStatus(string Kind, string DisplayName, bool Unthrottled);

/// O que "Relançar &lt;nome&gt; sem limitação" fez. Só Relaunched e Cancelled são silenciosos; o
/// navegador só foi fechado em LaunchFailed, DidNotStart e (às vezes) Cancelled.
public enum RelaunchOutcome
{
    /// Tudo fechou, o lançamento foi aceito e uma instância apareceu em até 5 s.
    Relaunched,
    /// Cancelado antes de fechar algo, ou durante a espera pelo novo processo (fim de sessão).
    Cancelled,
    /// Nada aberto: nada foi fechado.
    NotRunning,
    /// Linha de comando, caminho ou identidade do processo principal ilegível: nada foi fechado.
    ArgumentsUnreadable,
    /// Ainda havia instância depois de 10 s: nada foi lançado (abriria uma segunda cópia).
    StillRunning,
    /// Fechou, e o lançamento falhou: o navegador não está aberto.
    LaunchFailed,
    /// Fechou, o lançamento foi aceito, e em 5 s não apareceu: o navegador não está aberto.
    DidNotStart,
}

/// Detecta Chrome/Edge/Brave sem as flags anti-limitação e os relança com elas (desenho §7.3,
/// sequência do original). Toda chamada de sistema passa por IBrowserProcesses.
///
/// Relançar: (1) lê a lista; vazia ⇒ NotRunning. (2) Do processo principal, ANTES de fechar
/// qualquer coisa: identidade, linha de comando e caminho do executável, e confere numa nova
/// leitura que é o mesmo processo; qualquer falha ⇒ ArgumentsUnreadable e nada é fechado (um
/// relançamento sem o perfil abriria o perfil errado). (3) Fecha tudo com teto de 10 s e relê:
/// sobrou instância ⇒ StillRunning, nada é lançado. (4) Lança com as duas flags + perfil;
/// exceção ⇒ LaunchFailed. (5) Espera até 5 s aparecer; não apareceu ⇒ DidNotStart.
///
/// O texto de cada desfecho (pt-BR) vira notificação "Navegador não relançado" e fica em
/// LastProblem[kind] até o próximo relançamento daquele navegador.
public sealed class BrowserThrottle
{
    public const string NotRelaunchedTitle = "Navegador não relançado";

    private readonly IBrowserProcesses _processes;
    private readonly ILog _log;
    private readonly INotifier _notifier;
    private readonly ConcurrentDictionary<string, string> _lastProblem = new(StringComparer.OrdinalIgnoreCase);

    public BrowserThrottle(IBrowserProcesses processes, ILog log, INotifier notifier)
    {
        _processes = processes;
        _log = log;
        _notifier = notifier;
    }

    /// Teto para o navegador fechar (R11).
    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// Teto para o navegador relançado aparecer.
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// Intervalo entre leituras durante a espera.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// Espera injetável (os testes passam uma instantânea). Lança OperationCanceledException se ct cancelar.
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// Problema do último relançamento, por kind (texto pt-BR para a linha "⚠ …" do menu).
    public IReadOnlyDictionary<string, string> LastProblem => _lastProblem;

    /// Último resultado de Scan.
    public IReadOnlyList<BrowserStatus> Statuses { get; private set; } = [];

    /// Um status por navegador em execução, na ordem de ChromiumFlags.KnownBrowsers.
    /// Unthrottled só se TODA instância principal legível tem as duas flags. Instância de linha
    /// de comando ilegível fica de fora (com log); navegador só com ilegíveis não aparece.
    /// WMI é lento: não chamar no thread da UI.
    public IReadOnlyList<BrowserStatus> Scan()
    {
        IReadOnlyList<BrowserInstance> instances;
        try { instances = _processes.Scan(); }
        catch (Exception ex)
        {
            _log.Error($"navegadores: leitura dos processos falhou: {ex.Message}");
            Statuses = [];
            return Statuses;
        }

        var result = new List<BrowserStatus>();
        foreach (var group in instances.GroupBy(i => i.Kind, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => KnownIndex(g.Key)))
        {
            var name = ChromiumFlags.DisplayNameFor(group.Key);
            var readable = new List<BrowserInstance>();
            foreach (var instance in group)
            {
                if (string.IsNullOrWhiteSpace(instance.CommandLine))
                    _log.Error($"navegadores: linha de comando de {name} (pid {instance.Pid}) ilegível");
                else
                    readable.Add(instance);
            }
            if (readable.Count == 0) continue;
            result.Add(new BrowserStatus(group.Key, name, readable.All(i => ChromiumFlags.HasBothFlags(i.CommandLine))));
        }

        Statuses = result;
        var throttled = result.Where(s => !s.Unthrottled).Select(s => s.DisplayName).ToList();
        if (throttled.Count > 0) _log.Info($"navegadores limitados: {string.Join(", ", throttled)}");
        return result;
    }

    public async Task<RelaunchOutcome> RelaunchUnthrottledAsync(string kind, CancellationToken ct)
    {
        var name = ChromiumFlags.DisplayNameFor(kind);
        _lastProblem.TryRemove(kind, out _);
        var (outcome, detail) = await RelaunchCoreAsync(kind, name, ct).ConfigureAwait(false);
        var text = Explain(outcome, name, detail);
        if (text is not null)
        {
            _lastProblem[kind] = text;
            try { _notifier.Post(NotRelaunchedTitle, text); }
            catch (Exception ex) { _log.Error($"notificação falhou: {ex.Message}"); }
        }
        return outcome;
    }

    private async Task<(RelaunchOutcome, string?)> RelaunchCoreAsync(string kind, string name, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            _log.Info($"relançar {name}: cancelado antes de começar; nada foi fechado");
            return (RelaunchOutcome.Cancelled, null);
        }

        List<BrowserInstance> instances;
        try { instances = OfKind(_processes.Scan(), kind); }
        catch (Exception ex)
        {
            _log.Error($"relançar {name}: leitura dos processos falhou: {ex.Message}; nada foi fechado");
            return (RelaunchOutcome.ArgumentsUnreadable, $"a lista de processos não pôde ser lida: {ex.Message}");
        }
        if (instances.Count == 0)
        {
            _log.Info($"relançar {name}: não está aberto");
            return (RelaunchOutcome.NotRunning, null);
        }

        // (2) Tudo do principal é lido ANTES de fechar. Daqui em diante o kind é o do processo
        // (forma canônica), não o que veio do menu, que pode diferir na caixa.
        var main = instances[0];
        kind = main.Kind;
        IReadOnlyList<string> extra;
        try
        {
            if (main.Identity is null)
                throw new InvalidDataException($"a hora de início do processo {main.Pid} não pôde ser lida para conferi-los");
            if (string.IsNullOrWhiteSpace(main.CommandLine))
                throw new InvalidDataException($"a linha de comando do processo {main.Pid} veio vazia");
            if (string.IsNullOrWhiteSpace(main.ExecutablePath))
                throw new InvalidDataException($"o caminho do executável do processo {main.Pid} não pôde ser lido");
            var again = OfKind(_processes.Scan(), kind);
            if (!again.Any(i => i.Pid == main.Pid && i.Identity == main.Identity))
                throw new InvalidDataException($"o processo {main.Pid} terminou durante a leitura, ou não pôde ser conferido");
            extra = ChromiumFlags.PreservedArgs(main.CommandLine);
        }
        catch (Exception ex)
        {
            _log.Error($"relançar {name}: argumentos ilegíveis (pid {main.Pid}): {ex.Message}; nada foi fechado");
            return (RelaunchOutcome.ArgumentsUnreadable, ex.Message);
        }

        // (3) Fecha e relê. O fechamento não é cancelável (como o original): pela metade seria pior.
        bool quit;
        try { quit = await _processes.QuitAllAsync(kind, QuitTimeout, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.Error($"relançar {name}: fechamento falhou: {ex.Message}");
            quit = false;
        }
        List<BrowserInstance> remaining;
        try { remaining = OfKind(_processes.Scan(), kind); }
        catch (Exception ex)
        {
            _log.Error($"relançar {name}: releitura falhou depois de fechar: {ex.Message}; nada foi lançado");
            return (RelaunchOutcome.StillRunning, null);
        }
        if (!quit || remaining.Count > 0)
        {
            var pids = string.Join(", ", remaining.Select(i => i.Pid));
            _log.Error($"relançar {name}: ainda aberto depois de {Seconds(QuitTimeout)} s (pids {pids}); nada foi lançado");
            return (RelaunchOutcome.StillRunning, null);
        }

        // (4) Lança: as duas flags + o perfil que ele tinha.
        try
        {
            _processes.Launch(main.ExecutablePath, [.. ChromiumFlags.Required, .. extra]);
        }
        catch (Exception ex)
        {
            _log.Error($"relançar {name}: lançamento falhou: {ex.Message}");
            return (RelaunchOutcome.LaunchFailed, ex.Message);
        }

        // (5) Espera aparecer, contando em esperas (o Delay é o único relógio).
        bool started;
        try { started = await WaitUntilRunningAsync(kind, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _log.Info($"relançar {name}: espera cancelada depois do lançamento; sem aviso");
            return (RelaunchOutcome.Cancelled, null);
        }
        if (!started)
        {
            _log.Error($"relançar {name}: não estava aberto {Seconds(StartTimeout)} s depois do lançamento");
            return (RelaunchOutcome.DidNotStart, null);
        }
        _log.Info($"{name} relançado sem limitação");
        return (RelaunchOutcome.Relaunched, null);
    }

    private async Task<bool> WaitUntilRunningAsync(string kind, CancellationToken ct)
    {
        var sleeps = PollInterval > TimeSpan.Zero ? (int)Math.Ceiling(StartTimeout / PollInterval) : 0;
        for (var n = 0; n < sleeps; n++)
        {
            if (IsRunning(kind)) return true;
            await Delay(PollInterval, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return IsRunning(kind);
    }

    private bool IsRunning(string kind)
    {
        try { return OfKind(_processes.Scan(), kind).Count > 0; }
        catch (Exception ex)
        {
            _log.Error($"navegadores: leitura dos processos falhou: {ex.Message}");
            return false;
        }
    }

    private static List<BrowserInstance> OfKind(IReadOnlyList<BrowserInstance> all, string kind) =>
        [.. all.Where(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase))];

    private static int KnownIndex(string kind)
    {
        for (var i = 0; i < ChromiumFlags.KnownBrowsers.Length; i++)
            if (string.Equals(ChromiumFlags.KnownBrowsers[i].Kind, kind, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    private static int Seconds(TimeSpan t) => (int)Math.Round(t.TotalSeconds);

    // ---------- textos (pt-BR) ----------

    /// Texto da notificação/aviso por desfecho; null para Relaunched e Cancelled (silêncio).
    public static string? Explain(RelaunchOutcome outcome, string name, string? detail = null) => outcome switch
    {
        RelaunchOutcome.Relaunched or RelaunchOutcome.Cancelled => null,
        RelaunchOutcome.NotRunning =>
            $"{name} não está aberto. Nada foi fechado nem relançado.",
        RelaunchOutcome.ArgumentsUnreadable =>
            $"Não foi possível ler os argumentos de perfil do {name} ({detail ?? "motivo desconhecido"}), então relançar poderia abrir o perfil errado. {name} não foi fechado.",
        RelaunchOutcome.StillRunning =>
            $"{name} não fechou em 10 s, então nada foi relançado. Ele ainda pode fechar mais tarde; se fechar, abra-o de novo você mesmo.",
        RelaunchOutcome.LaunchFailed =>
            $"{name} fechou, mas não pôde ser relançado: {detail ?? "motivo desconhecido"}. Abra-o você mesmo.",
        RelaunchOutcome.DidNotStart =>
            $"{name} fechou e foi pedido para abrir de novo, mas não estava aberto 5 s depois. Abra-o você mesmo.",
        _ => null,
    };

    /// Linha de aviso do menu: "⚠ Google Chrome está limitado", "⚠ A e B estão limitados",
    /// "⚠ A, B e C estão limitados". Null se a lista está vazia.
    public static string? ThrottledWarning(IReadOnlyList<string> names) => names.Count switch
    {
        0 => null,
        1 => $"⚠ {names[0]} está limitado",
        _ => $"⚠ {string.Join(", ", names.Take(names.Count - 1))} e {names[^1]} estão limitados",
    };

    /// Item do menu para relançar.
    public static string RelaunchMenuLabel(string name) => $"Relançar {name} sem limitação";

    /// Confirmação antes de relançar (título e corpo, traduzidos do original).
    public static string ConfirmationTitle(string name) => $"Fechar e relançar {name}?";

    public static string ConfirmationBody(string name) =>
        $"O Insomnia fecha o {name} e o abre de novo com as duas flags que impedem a limitação de janelas escondidas. " +
        $"Suas janelas e abas só voltam se o {name} estiver configurado para reabri-las ao iniciar. " +
        $"Se o {name} não tiver fechado em 10 s, nada é relançado.";
}
