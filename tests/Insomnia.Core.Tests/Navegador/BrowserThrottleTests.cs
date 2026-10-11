using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Navegador;

public class BrowserThrottleTests
{
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Both = " --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";
    private static readonly ProcessIdentity Id1 = new(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), "17");

    private readonly FakeBrowserProcesses _procs = new();
    private readonly FakeLog _log = new();
    private readonly FakeNotifier _notifier = new();
    private int _delays;

    private BrowserThrottle Make() => new(_procs, _log, _notifier)
    {
        Delay = (_, ct) => { _delays++; ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
    };

    private static BrowserInstance Chrome(string args = "", int pid = 100, ProcessIdentity? id = null, string path = ChromePath) =>
        new("chrome.exe", "Google Chrome", pid, path, $"\"{path}\"{args}", id ?? Id1);

    private void AppearOnLaunch() =>
        _procs.OnLaunch = (path, args) => _procs.Instances.Add(Chrome(" " + string.Join(' ', args), pid: 200));

    // ---------- Scan ----------

    [Fact]
    public void Scan_um_status_por_navegador_na_ordem_do_catalogo()
    {
        _procs.Instances.Add(new BrowserInstance("brave.exe", "Brave", 3, @"C:\b\brave.exe", "brave.exe" + Both, Id1));
        _procs.Instances.Add(Chrome());
        _procs.Instances.Add(Chrome(Both, pid: 101));   // outra instância (outro perfil) com flags

        var statuses = Make().Scan();

        Assert.Equal(
            new[] { new BrowserStatus("chrome.exe", "Google Chrome", false), new BrowserStatus("brave.exe", "Brave", true) },
            statuses);
        Assert.True(_log.Contains("navegadores limitados: Google Chrome"));
    }

    [Fact]
    public void Scan_instancia_ilegivel_fica_de_fora()
    {
        _procs.Instances.Add(new BrowserInstance("msedge.exe", "Microsoft Edge", 9, @"C:\e\msedge.exe", "", Id1));
        Assert.Empty(Make().Scan());
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Scan_falha_de_leitura_devolve_vazio_e_loga()
    {
        var t = new BrowserThrottle(new ThrowingProcesses(), _log, _notifier);
        Assert.Empty(t.Scan());
        Assert.True(_log.Contains("leitura dos processos falhou"));
    }

    // ---------- Relançar ----------

    [Fact]
    public async Task Relanca_com_as_duas_flags_e_o_perfil()
    {
        _procs.Instances.Add(Chrome(" --user-data-dir=\"C:\\Users\\caio\\Chrome Dev\" --profile-directory=Default --no-first-run"));
        AppearOnLaunch();
        var t = Make();

        Assert.Equal(RelaunchOutcome.Relaunched, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));

        Assert.Equal(new[] { "chrome.exe" }, _procs.QuitCalls);
        var (path, args) = Assert.Single(_procs.Launches);
        Assert.Equal(ChromePath, path);
        Assert.Equal(
            new[] { ChromiumFlags.Occluded, ChromiumFlags.Renderer, @"--user-data-dir=C:\Users\caio\Chrome Dev", "--profile-directory=Default" },
            args);
        Assert.Empty(_notifier.Posts);
        Assert.False(t.LastProblem.ContainsKey("chrome.exe"));
    }

    [Fact]
    public async Task Nao_aberto_nada_e_fechado()
    {
        var t = Make();
        Assert.Equal(RelaunchOutcome.NotRunning, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Empty(_procs.QuitCalls);
        Assert.Empty(_procs.Launches);
        Assert.Equal("Google Chrome não está aberto. Nada foi fechado nem relançado.", t.LastProblem["chrome.exe"]);
        var post = Assert.Single(_notifier.Posts);
        Assert.Equal("Navegador não relançado", post.Title);
    }

    public static TheoryData<string, BrowserInstance> Ilegiveis => new()
    {
        { "hora de início", new BrowserInstance("chrome.exe", "Google Chrome", 100, ChromePath, "chrome.exe", null) },
        { "linha de comando", new BrowserInstance("chrome.exe", "Google Chrome", 100, ChromePath, "  ", Id1) },
        { "caminho do executável", new BrowserInstance("chrome.exe", "Google Chrome", 100, "", "chrome.exe", Id1) },
    };

    [Theory]
    [MemberData(nameof(Ilegiveis))]
    public async Task Argumentos_ilegiveis_nada_e_fechado(string motivo, BrowserInstance main)
    {
        _procs.Instances.Add(main);
        var t = Make();
        Assert.Equal(RelaunchOutcome.ArgumentsUnreadable, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Empty(_procs.QuitCalls);
        Assert.Empty(_procs.Launches);
        Assert.Contains(motivo, t.LastProblem["chrome.exe"]);
        Assert.StartsWith("Não foi possível ler os argumentos de perfil do Google Chrome (", t.LastProblem["chrome.exe"]);
        Assert.EndsWith("Google Chrome não foi fechado.", t.LastProblem["chrome.exe"]);
    }

    [Fact]
    public async Task Processo_que_some_ou_troca_durante_a_leitura_eh_ilegivel()
    {
        // Mesmo pid, outra hora de início na releitura: o pid passou a nomear outro processo.
        var procs = new SwappingProcesses(Chrome(), Chrome(id: new ProcessIdentity(Id1.CreationTime.AddSeconds(1), "17")));
        var t = new BrowserThrottle(procs, _log, _notifier);
        Assert.Equal(RelaunchOutcome.ArgumentsUnreadable, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Equal(0, procs.Quits);
        Assert.Contains("terminou durante a leitura", t.LastProblem["chrome.exe"]);
    }

    [Fact]
    public async Task Nao_fechou_em_10s_nada_e_lancado()
    {
        _procs.Instances.Add(Chrome());
        _procs.QuitResult = false;
        var t = Make();
        Assert.Equal(RelaunchOutcome.StillRunning, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Empty(_procs.Launches);
        Assert.Equal(
            "Google Chrome não fechou em 10 s, então nada foi relançado. Ele ainda pode fechar mais tarde; se fechar, abra-o de novo você mesmo.",
            t.LastProblem["chrome.exe"]);
    }

    [Fact]
    public async Task Lancamento_que_falha()
    {
        _procs.Instances.Add(Chrome());
        _procs.FailLaunch = true;
        var t = Make();
        Assert.Equal(RelaunchOutcome.LaunchFailed, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Equal(
            "Google Chrome fechou, mas não pôde ser relançado: lançamento falhou (fake). Abra-o você mesmo.",
            t.LastProblem["chrome.exe"]);
    }

    [Fact]
    public async Task Nao_apareceu_em_5s()
    {
        _procs.Instances.Add(Chrome());
        var t = Make();
        Assert.Equal(RelaunchOutcome.DidNotStart, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));
        Assert.Single(_procs.Launches);
        Assert.Equal(20, _delays);   // 5 s / 250 ms: a espera tem teto
        Assert.Equal(
            "Google Chrome fechou e foi pedido para abrir de novo, mas não estava aberto 5 s depois. Abra-o você mesmo.",
            t.LastProblem["chrome.exe"]);
    }

    [Fact]
    public async Task Fim_da_sessao_durante_a_espera_cancela_sem_aviso()
    {
        _procs.Instances.Add(Chrome());
        using var cts = new CancellationTokenSource();
        _procs.OnLaunch = (_, _) => cts.Cancel();
        var t = Make();
        Assert.Equal(RelaunchOutcome.Cancelled, await t.RelaunchUnthrottledAsync("chrome.exe", cts.Token));
        Assert.Empty(_notifier.Posts);
        Assert.False(t.LastProblem.ContainsKey("chrome.exe"));
    }

    [Fact]
    public async Task Cancelado_antes_de_comecar_nao_fecha_nada()
    {
        _procs.Instances.Add(Chrome());
        var t = Make();
        Assert.Equal(RelaunchOutcome.Cancelled, await t.RelaunchUnthrottledAsync("chrome.exe", new CancellationToken(canceled: true)));
        Assert.Empty(_procs.QuitCalls);
    }

    [Fact]
    public async Task Problema_fica_ate_o_proximo_relancamento_do_mesmo_navegador()
    {
        var t = Make();
        await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None);
        Assert.True(t.LastProblem.ContainsKey("chrome.exe"));

        _procs.Instances.Add(Chrome());
        AppearOnLaunch();
        Assert.Equal(RelaunchOutcome.Relaunched, await t.RelaunchUnthrottledAsync("CHROME.EXE", CancellationToken.None));
        Assert.False(t.LastProblem.ContainsKey("chrome.exe"));
    }

    // ---------- textos ----------

    [Fact]
    public void Textos_do_menu_e_da_confirmacao()
    {
        Assert.Null(BrowserThrottle.ThrottledWarning([]));
        Assert.Equal("⚠ Google Chrome está limitado", BrowserThrottle.ThrottledWarning(["Google Chrome"]));
        Assert.Equal("⚠ Google Chrome e Brave estão limitados", BrowserThrottle.ThrottledWarning(["Google Chrome", "Brave"]));
        Assert.Equal("⚠ A, B e C estão limitados", BrowserThrottle.ThrottledWarning(["A", "B", "C"]));
        Assert.Equal("Relançar Brave sem limitação", BrowserThrottle.RelaunchMenuLabel("Brave"));
        Assert.Equal("Fechar e relançar Brave?", BrowserThrottle.ConfirmationTitle("Brave"));
        Assert.Contains("10 s", BrowserThrottle.ConfirmationBody("Brave"));
        Assert.Null(BrowserThrottle.Explain(RelaunchOutcome.Relaunched, "X"));
        Assert.Null(BrowserThrottle.Explain(RelaunchOutcome.Cancelled, "X"));
        foreach (var o in Enum.GetValues<RelaunchOutcome>().Except(new[] { RelaunchOutcome.Relaunched, RelaunchOutcome.Cancelled }))
            Assert.Contains("X", BrowserThrottle.Explain(o, "X", "motivo"));
    }

    // ---------- fakes locais ----------

    private sealed class ThrowingProcesses : IBrowserProcesses
    {
        public IReadOnlyList<BrowserInstance> Scan() => throw new InvalidOperationException("WMI fora");
        public Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public void Launch(string executablePath, IReadOnlyList<string> args) { }
    }

    /// Primeira leitura devolve first; as seguintes, second.
    private sealed class SwappingProcesses(BrowserInstance first, BrowserInstance second) : IBrowserProcesses
    {
        private int _scans;
        public int Quits { get; private set; }
        public IReadOnlyList<BrowserInstance> Scan() => _scans++ == 0 ? [first] : [second];
        public Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct) { Quits++; return Task.FromResult(true); }
        public void Launch(string executablePath, IReadOnlyList<string> args) { }
    }
}
