using System.Collections.Immutable;
using Insomnia.App.Commands;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Store;
using Insomnia.Core.Tests.Fakes;
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.App.Tests.Diagnostico;

/// DiagnosticsCommand pela costura Run(args, deps, saída): INSOMNIA_HOME temporário (via
/// Paths.FromEnvironment) e fakes no lugar de todo adaptador real. Nenhum teste aqui chega ao
/// pré-voo real, ao Agendador, ao WMI ou ao Wi-Fi (R12).
public sealed class DiagnosticsCommandTests : IDisposable
{
    private readonly string _home;
    private readonly Paths _paths;
    private readonly FakeClock _clock = new();
    private readonly FakeLog _log = new();
    private readonly FakeRecoveryLock _lock = new();
    private readonly FakeSleepGuard _sleep = new();
    private readonly FakeEnergySaver _energy = new();
    private readonly FakeBackstopScheduler _backstop = new();
    private readonly FakeWifi _wifi = new();
    private readonly FakeBrowserProcesses _browsers = new();
    private readonly FakeAutoStart _autoStart = new();
    private PowerStatus _power = new(OnAc: true, BatteryPercent: 87);
    private LidState _lid = LidState.Open;
    private bool _appRunning;

    public DiagnosticsCommandTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "insomnia-tests", "diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _paths = Paths.FromEnvironment(name => name == Paths.HomeVariable ? _home : null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DiagnosticsDeps Deps(IWifi? wifi = null, IBrowserProcesses? browsers = null,
        IBackstopScheduler? backstop = null, Func<PowerStatus>? power = null, Func<LidState>? lid = null) => new()
    {
        Paths = _paths,
        Clock = _clock,
        Log = _log,
        Lock = _lock,
        SleepGuard = _sleep,
        EnergySaver = _energy,
        ReadPower = power ?? (() => _power),
        ReadLid = lid ?? (() => _lid),
        Backstop = backstop ?? _backstop,
        BackstopDefinition = BackstopDefinition.For(@"C:\Users\x\AppData\Local\Programs\Insomnia\Insomnia.exe"),
        Wifi = wifi ?? _wifi,
        Browsers = browsers ?? _browsers,
        AutoStart = _autoStart,
        IsAppRunning = () => _appRunning,
        ExecutablePath = @"C:\fake\Insomnia.exe",
        LockTimeout = TimeSpan.FromMilliseconds(10),
    };

    private (int Code, string Text) Run(DiagnosticsDeps? deps = null, params string[] args)
    {
        var output = new StringWriter();
        var code = DiagnosticsCommand.Run(args, deps ?? Deps(), output);
        return (code, output.ToString());
    }

    private FileStore Store => new(_paths, _clock);

    private Session ValidSession() => new(_clock.Now.AddHours(-1), _clock.Now.AddHours(3), ImmutableArray<int>.Empty);

    // ---------- seções e caminhos ----------

    [Fact]
    public void Casa_limpa_imprime_todas_as_secoes_com_os_caminhos_do_INSOMNIA_HOME_e_sai_0()
    {
        var (code, text) = Run();

        Assert.Equal(0, code);
        foreach (var section in new[] { "[Caminhos]", "[Config]", "[Sessão]", "[Diário de recuperação]", "[Energia]", "[Backstop]", "[Rede]", "[Navegadores]", "[App]", "[Resumo]" })
            Assert.Contains(section, text);
        Assert.Contains(Path.Combine(_home, "state.json"), text);
        Assert.Contains(Path.Combine(_home, "session.json"), text);
        Assert.Contains(Path.Combine(_home, "config.json"), text);
        Assert.Contains("ausente: o app grava o padrão", text);
        Assert.Contains("ausente (nenhuma sessão)", text);
        Assert.Contains("limpo", text);
        Assert.Contains("LIDACTION: AC 1 (suspender) · DC 1 (suspender)", text);
        Assert.Contains("ESBATTTHRESHOLD (bateria): 20%", text);
        Assert.Contains("bateria: 87% · na tomada", text);
        Assert.Contains("tampa: aberta", text);
        Assert.Contains("ausente (Missing)", text);
        Assert.Contains("hotspot de reserva: não configurado", text);
        Assert.Contains("nenhum Chrome, Edge ou Brave", text);
        Assert.Contains("tudo certo", text);
    }

    [Fact]
    public void Diagnostico_so_le_config_ausente_nao_e_criada()
    {
        Run();

        Assert.False(File.Exists(_paths.ConfigFile));
        Assert.False(File.Exists(_paths.JournalFile));
        Assert.False(File.Exists(_paths.SessionFile));
    }

    // ---------- pré-voo ----------

    [Fact]
    public void Pre_voo_reescreve_o_mesmo_LIDACTION_sob_o_lock_e_solta_o_lock()
    {
        _sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 2, 1);

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Equal([new LidActionValues(FakeSchemes.Balanced, 2, 1)], _sleep.Writes);
        Assert.Equal(new LidActionValues(FakeSchemes.Balanced, 2, 1), _sleep.Lid);
        Assert.Equal(1, _lock.Acquisitions);
        Assert.False(_lock.IsAcquired);
        Assert.Contains("pré-voo de permissão: ok (reescreveu AC 2 / DC 1", text);
    }

    [Fact]
    public void Sem_pre_voo_nao_escreve_nada()
    {
        var (code, text) = Run(null, DiagnosticsCommand.SkipPreflightFlag);

        Assert.Equal(0, code);
        Assert.Empty(_sleep.Writes);
        Assert.Contains("pré-voo de permissão: pulado (--sem-pre-voo)", text);
    }

    [Fact]
    public void R6_lock_ocupado_pula_o_pre_voo_sem_escrever()
    {
        _lock.Held = true;

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Empty(_sleep.Writes);
        Assert.Contains("lock de recuperação ocupado", text);
    }

    [Fact]
    public void Pre_voo_que_falha_e_problema_e_sai_1()
    {
        _sleep.FailWrite = true;

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("pré-voo de permissão FALHOU", text);
        Assert.False(_lock.IsAcquired);
    }

    [Fact]
    public void LIDACTION_ilegivel_e_problema_e_nao_tenta_escrever()
    {
        _sleep.FailRead = true;

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Empty(_sleep.Writes);
        Assert.Contains("LIDACTION ilegível", text);
    }

    // ---------- arquivos (R5) ----------

    [Fact]
    public void R5_diario_corrompido_e_relatado_e_fica_intacto()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var garbage = "{ isto não é json";
        File.WriteAllText(_paths.JournalFile, garbage);

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("diário CORROMPIDO", text);
        Assert.Equal(garbage, File.ReadAllText(_paths.JournalFile));
    }

    [Fact]
    public void R5_sessao_ilegivel_e_relatada_e_nao_e_movida()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.SessionFile, "lixo");

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("session.json ILEGÍVEL", text);
        Assert.Equal("lixo", File.ReadAllText(_paths.SessionFile));
        Assert.DoesNotContain(Directory.GetFiles(_paths.DataDirectory), f => Path.GetFileName(f).StartsWith(Paths.UnreadableSessionPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Config_corrompida_e_relatada_e_nao_e_sobrescrita()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.ConfigFile, "[1,2");

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("config.json CORROMPIDA", text);
        Assert.Equal("[1,2", File.ReadAllText(_paths.ConfigFile));
    }

    // ---------- diário e sessão ----------

    [Fact]
    public void R4_diario_sujo_sem_sessao_lista_cada_entrada_e_aponta_recuperacao_pendente()
    {
        Store.SaveJournal(Journal.Clean with
        {
            LidAction = new LidActionEntry(FakeSchemes.Balanced, 1, 3, _clock.Now),
            EnergySaver = new EnergySaverEntry(FakeSchemes.Balanced, 20, _clock.Now, "lid"),
            FrozenProcesses = [new FrozenProcess(4312, "slack.exe", null, null, "slack.exe"),
                               new FrozenProcess(77, "Discord.exe", _clock.Now.AddHours(-2), "17", "Discord.exe")],
            DockerFrozen = true,
            SavedAudioOutputs = [new SavedAudioOutput("{0.0.0.00000000}.{a1}", "Alto-falantes", 0.62f, false)],
        });
        _sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("sujo:", text);
        Assert.Contains("anterior AC 1 (suspender) / DC 3 (desligar)", text);
        Assert.Contains("ESBATTTHRESHOLD anterior 20% (causa tampa", text);
        Assert.Contains("slack.exe pid 4312", text);
        Assert.Contains("não verificável", text);       // R8: entrada provisória fica visível
        Assert.Contains("Discord.exe pid 77", text);
        Assert.Contains("boot 17", text);
        Assert.Contains("Docker pausado", text);
        Assert.Contains("Alto-falantes", text);
        Assert.Contains("volume 62%", text);
        Assert.Contains("recuperação pendente", text);
    }

    [Fact]
    public void Sessao_valida_com_backstop_armado_e_diario_sujo_esta_ok()
    {
        Store.SaveSession(ValidSession());
        Store.SaveJournal(Journal.Clean with { LidAction = new LidActionEntry(FakeSchemes.Balanced, 1, 1, _clock.Now) });
        _sleep.Lid = new LidActionValues(FakeSchemes.Balanced, 0, 0);
        _backstop.State = BackstopState.Armed;
        _appRunning = true;

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Contains("válida até", text);
        Assert.Contains("faltam 3:00:00", text);
        Assert.Contains("armada (Armed)", text);
        Assert.Contains("bandeja em execução: sim", text);
        Assert.Contains("hold de execução: deve estar ativo", text);
        Assert.DoesNotContain("recuperação pendente", text);
    }

    [Fact]
    public void R10_sessao_valida_sem_backstop_armado_e_problema()
    {
        Store.SaveSession(ValidSession());
        _backstop.State = BackstopState.Different;

        var (code, text) = Run();

        Assert.Equal(1, code);
        Assert.Contains("diferente do esperado (Different)", text);
        Assert.Contains("sessão válida sem backstop armado", text);
        Assert.Empty(_backstop.Installs);   // diagnóstico não instala nada
    }

    [Fact]
    public void Sessao_expirada_e_relatada()
    {
        Store.SaveSession(new Session(_clock.Now.AddHours(-5), _clock.Now.AddHours(-1), ImmutableArray<int>.Empty));

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Contains("expirada desde", text);
    }

    // ---------- rede, navegadores, bateria ----------

    [Fact]
    public void Hotspot_configurado_sem_perfil_avisa_com_o_texto_do_app()
    {
        Store.SaveConfig(Config.Default with { HotspotSsid = "MeuCelular" });

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Contains("Perfil do hotspot não encontrado", text);

        _wifi.Profiles.Add("MeuCelular");
        var (_, again) = Run();
        Assert.Contains("hotspot de reserva \"MeuCelular\": perfil encontrado", again);
    }

    [Fact]
    public void Navegadores_mostram_limitado_e_sem_limitacao_por_instancia()
    {
        _browsers.Instances.Add(new BrowserInstance("chrome.exe", "Google Chrome", 100, @"C:\c\chrome.exe", "\"C:\\c\\chrome.exe\"", null));
        _browsers.Instances.Add(new BrowserInstance("msedge.exe", "Microsoft Edge", 200, @"C:\e\msedge.exe",
            "\"C:\\e\\msedge.exe\" --disable-backgrounding-occluded-windows --disable-renderer-backgrounding", null));

        var (code, text) = Run();

        Assert.Equal(0, code);
        Assert.Contains("Google Chrome (pid 100) está limitado", text);
        Assert.Contains("Microsoft Edge (pid 200): sem limitação", text);
    }

    [Fact]
    public void Bateria_ilegivel_e_tampa_desconhecida_aparecem_como_tais()
    {
        _power = new PowerStatus(OnAc: false, BatteryPercent: null);
        _lid = LidState.Unknown;

        var (_, text) = Run();

        Assert.Contains("bateria: ilegível ou sem bateria · na bateria", text);
        Assert.Contains("tampa: desconhecida", text);
    }

    [Fact]
    public void Adaptadores_que_lancam_nao_derrubam_o_diagnostico()
    {
        var deps = Deps(
            wifi: new ThrowingWifi(),
            browsers: new ThrowingBrowsers(),
            backstop: new ThrowingBackstop(),
            power: () => throw new InvalidOperationException("sem bateria"),
            lid: () => throw new InvalidOperationException("sem janela"));
        _energy.FailRead = true;
        Store.SaveConfig(Config.Default with { HotspotSsid = "X" });

        var ex = Record.Exception(() => Run(deps));

        Assert.Null(ex);
        var (_, text) = Run(deps);
        Assert.Contains("desconhecido (Unknown)", text);
        Assert.Contains("leitura dos navegadores falhou", text);
        Assert.Contains("bateria ilegível", text);
        Assert.Contains("tampa ilegível", text);
        Assert.Contains("ESBATTTHRESHOLD ilegível", text);
        Assert.Contains("[Resumo]", text);
    }

    // ---------- fakes locais que lançam ----------

    private sealed class ThrowingWifi : IWifi
    {
        public event Action<bool>? ConnectivityChanged { add { } remove { } }
        public bool IsConnected => throw new InvalidOperationException("WLAN desligado");
        public bool HasProfile(string ssid) => throw new InvalidOperationException("WLAN desligado");
        public Task<bool> ConnectAsync(string profileName, CancellationToken ct) => throw new NotSupportedException();
        public void Start() { }
        public void Stop() { }
    }

    private sealed class ThrowingBrowsers : IBrowserProcesses
    {
        public IReadOnlyList<BrowserInstance> Scan() => throw new InvalidOperationException("WMI fora do ar");
        public Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public void Launch(string executablePath, IReadOnlyList<string> args) => throw new NotSupportedException();
    }

    private sealed class ThrowingBackstop : IBackstopScheduler
    {
        public BackstopState Query(BackstopDefinition want) => throw new InvalidOperationException("COM");
        public void Install(BackstopDefinition want) => throw new NotSupportedException();
        public void Remove(string taskName) => throw new NotSupportedException();
    }
}
