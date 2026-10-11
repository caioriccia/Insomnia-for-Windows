using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Core.Model;
using Insomnia.Core.Store;
using Insomnia.Core.Time;
using Insomnia.Windows.Browser;
using Insomnia.Windows.Power;
using Insomnia.Windows.Scheduler;
using Insomnia.Windows.Startup;
using Insomnia.Windows.Wifi;
// System.Windows.Forms (using global do WinForms) também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.App.Commands;

/// Tudo o que o diagnóstico consulta. Run(args) monta com os adaptadores reais; os testes
/// montam com fakes e chamam Run(args, deps, saída) — e assim NUNCA chegam ao pré-voo real.
public sealed class DiagnosticsDeps
{
    public required Paths Paths { get; init; }
    public required IClock Clock { get; init; }
    public required ILog Log { get; init; }
    /// Segura o pré-voo: sem ele, o diagnóstico poderia ler LIDACTION=1, o app gravar 0 no
    /// início de uma sessão, e o pré-voo reescrever 1 por cima.
    public required IRecoveryLock Lock { get; init; }
    public required ISleepGuard SleepGuard { get; init; }
    public required IEnergySaver EnergySaver { get; init; }
    public required Func<PowerStatus> ReadPower { get; init; }
    public required Func<LidState> ReadLid { get; init; }
    public required IBackstopScheduler Backstop { get; init; }
    public required BackstopDefinition BackstopDefinition { get; init; }
    public required IWifi Wifi { get; init; }
    public required IBrowserProcesses Browsers { get; init; }
    public required IAutoStart AutoStart { get; init; }
    /// A instância da bandeja está aberta? (mutex Local\Insomnia.Ui).
    public required Func<bool> IsAppRunning { get; init; }
    /// Exe que está rodando este diagnóstico (só para exibir).
    public string? ExecutablePath { get; init; }
    /// Quanto esperar o lock do pré-voo.
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// `Insomnia.exe --diagnostico [--sem-pre-voo]`: relatório em pt-BR do que o app enxerga nesta
/// máquina. Saída 0 = nada impede o app de funcionar; 1 = há problema (listado no resumo).
///
/// Só lê, com UMA exceção: o pré-voo de permissão, que relê o LIDACTION do esquema ativo e
/// reescreve o MESMO valor (AC e DC), sob o lock de recuperação, para provar que este usuário
/// consegue mudar o plano de energia (desenho §7/§10.1). `--sem-pre-voo` pula essa escrita.
/// Arquivo ilegível nunca é tocado (R5): config, sessão e diário só são lidos; config.json
/// ausente NÃO é criado aqui.
public static class DiagnosticsCommand
{
    public const string SkipPreflightFlag = "--sem-pre-voo";
    public const string UiMutexName = "Local\\Insomnia.Ui";

    /// Ponto de entrada do Program (assinatura congelada).
    public static int Run(string[] args)
    {
        AttachParentConsoleIfNeeded();
        var output = OpenOutput();
        try
        {
            var paths = Paths.FromEnvironment();
            var log = new FileLog(paths.LogFile);
            using var sleepGuard = new SleepGuardWin(log);
            var deps = new DiagnosticsDeps
            {
                Paths = paths,
                Clock = SystemClock.Instance,
                Log = log,
                Lock = new FileRecoveryLock(paths.LockFile),
                SleepGuard = sleepGuard,
                EnergySaver = new EnergySaverWin(log),
                ReadPower = new PowerStatusWin().Read,
                ReadLid = () => ReadLidOnce(log),
                Backstop = new BackstopSchedulerWin(log),
                BackstopDefinition = BackstopDefinition.For(Environment.ProcessPath ?? Paths.InstalledExecutablePath),
                Wifi = new WifiWin(log),
                Browsers = new BrowserProcessesWin(log),
                AutoStart = new AutoStartWin(),
                IsAppRunning = IsUiRunning,
                ExecutablePath = Environment.ProcessPath,
            };
            return Run(args, deps, output);
        }
        catch (Exception e)
        {
            output.WriteLine($"diagnóstico interrompido: {e.Message}");
            return 1;
        }
        finally
        {
            output.Flush();
        }
    }

    /// Saída redirecionada (`| Out-Host`): sem console próprio, o .NET escreveria na página
    /// ANSI (1252) e o PowerShell decodifica na página do console (850 por padrão, ou 65001)
    /// — "diagnóstico" virava "diagn¾stico". Escreve na página do console de quem chamou;
    /// sem console, na OEM. No console direto, o Console.Out já escreve Unicode.
    private static TextWriter OpenOutput()
    {
        if (!Console.IsOutputRedirected) return Console.Out;
        try
        {
            var stream = Console.OpenStandardOutput(); // antes do AttachConsole: segura o pipe
            var cp = 0u;
            if (AttachConsole(AttachParentProcess))
            {
                cp = GetConsoleOutputCP();
                FreeConsole();
            }
            if (cp == 0) cp = GetOEMCP();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = cp == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding((int)cp);
            return new StreamWriter(stream, encoding) { AutoFlush = true };
        }
        catch
        {
            return Console.Out;
        }
    }

    /// Costura dos testes: mesmas seções, dependências por parâmetro. Nunca lança.
    public static int Run(string[] args, DiagnosticsDeps deps, TextWriter output)
    {
        var report = new Report(output);
        try
        {
            var skipPreflight = args.Any(a => string.Equals(a, SkipPreflightFlag, StringComparison.OrdinalIgnoreCase));
            var now = deps.Clock.Now;
            deps.Log.Info("diagnóstico: início");
            output.WriteLine($"Insomnia — diagnóstico ({Local(now)})");

            var store = new FileStore(deps.Paths, deps.Clock, deps.Log);
            WritePaths(report, deps);
            var config = WriteConfig(report, deps, store);
            var session = WriteSession(report, store, now);
            var sessionValid = session is not null && !session.IsExpired(now);
            WriteJournal(report, store, sessionValid);
            WriteEnergy(report, deps, skipPreflight, sessionValid);
            WriteBackstop(report, deps, sessionValid);
            WriteNetwork(report, deps, config);
            WriteBrowsers(report, deps);
            WriteApp(report, deps, sessionValid);
        }
        catch (Exception e)
        {
            report.Problem($"diagnóstico interrompido: {e.Message}");
        }

        var code = report.WriteSummary();
        deps.Log.Info($"diagnóstico: fim (saída {code}, {report.ProblemCount} problema(s), {report.WarningCount} aviso(s))");
        return code;
    }

    // ---------- seções ----------

    private static void WritePaths(Report r, DiagnosticsDeps d)
    {
        r.Section("Caminhos");
        var home = Environment.GetEnvironmentVariable(Paths.HomeVariable);
        r.Line($"{Paths.HomeVariable}: {(string.IsNullOrWhiteSpace(home) ? "(não definido)" : home)}");
        r.Line($"config:  {d.Paths.ConfigFile}");
        r.Line($"diário:  {d.Paths.JournalFile}");
        r.Line($"sessão:  {d.Paths.SessionFile}");
        r.Line($"lock:    {d.Paths.LockFile}");
        r.Line($"log:     {d.Paths.LogFile}");
        r.Line($"exe em uso:    {d.ExecutablePath ?? "(desconhecido)"}");
        var installed = Paths.InstalledExecutablePath;
        r.Line($"exe instalado: {installed} ({(File.Exists(installed) ? "existe" : "não existe")})");
    }

    private static Config WriteConfig(Report r, DiagnosticsDeps d, IStore store)
    {
        r.Section("Config");
        if (!File.Exists(d.Paths.ConfigFile))
        {
            // LoadConfig gravaria o padrão; o diagnóstico não cria arquivo.
            r.Line("ausente: o app grava o padrão ao abrir");
            return Config.Default;
        }
        try
        {
            var c = store.LoadConfig();
            r.Line("válida");
            r.Line($"presets: {string.Join(", ", c.Presets.Select(s => DurationFormat.Chip(TimeSpan.FromSeconds(s))))} · padrão {DurationFormat.Chip(TimeSpan.FromSeconds(c.DefaultPreset))}");
            r.Line($"congelar ao fechar a tampa: {(c.FreezeAllApps ? "todos os apps com janela" : List(c.FreezeList))}");
            r.Line($"apagar tela: {YesNo(c.DarkenDisplayOnLidClose)} · mutar: {YesNo(c.MuteOnLidClose)} · Economia: {YesNo(c.EnergySaverOnLidClose)} · Docker: {YesNo(c.DockerRule)}");
            r.Line($"pisos de bateria: Economia abaixo de {c.LowPowerFloor}% · encerrar abaixo de {c.EndFloor}%{(c.EndFloor == 0 ? " (desligado)" : "")}");
            if (!c.FloorsAreOrdered) r.Warning("pisos de bateria fora de ordem no config.json (o app corrige ao abrir)");
            return c;
        }
        catch (StoreCorruptException e)
        {
            r.Problem($"config.json CORROMPIDA: {e.Message} (o app usa o padrão e não sobrescreve o arquivo)");
            return Config.Default;
        }
    }

    private static Session? WriteSession(Report r, IStore store, DateTimeOffset now)
    {
        r.Section("Sessão");
        try
        {
            var s = store.LoadSession();
            if (s is null)
            {
                r.Line("ausente (nenhuma sessão)");
                return null;
            }
            if (s.IsExpired(now))
            {
                r.Line($"expirada desde {Local(s.EndsAt)} (iniciada em {Local(s.StartedAt)})");
                return s;
            }
            var remaining = SessionMath.FormatCountdown(s.Remaining(now), SessionMath.ShapeFor(s.EndsAt - s.StartedAt));
            r.Line($"válida até {Local(s.EndsAt)} · faltam {remaining} (iniciada em {Local(s.StartedAt)})");
            if (s.Extensions.Length > 0)
                r.Line($"extensões: {string.Join(", ", s.Extensions.Select(x => DurationFormat.Chip(TimeSpan.FromSeconds(x))))}");
            return s;
        }
        catch (StoreCorruptException e)
        {
            r.Problem($"session.json ILEGÍVEL: {e.Message} (o app a move para o lado e trata como expirada)");
            return null;
        }
    }

    private static void WriteJournal(Report r, IStore store, bool sessionValid)
    {
        r.Section("Diário de recuperação");
        Journal j;
        try { j = store.LoadJournal(); }
        catch (StoreCorruptException e)
        {
            r.Problem($"diário CORROMPIDO: {e.Message} — nenhuma transação roda e o arquivo fica intacto; examine-o e apague à mão depois de conferir o LIDACTION");
            return;
        }

        if (!j.IsDirty())
        {
            r.Line("limpo");
            return;
        }

        r.Line("sujo:");
        if (j.LidAction is { } lid)
            r.Line($"  - LIDACTION: esquema {lid.SchemeId}, anterior AC {LidLabel(lid.AcPrevious)} / DC {LidLabel(lid.DcPrevious)} (desde {Local(lid.SetAt)})");
        if (j.EnergySaver is { } es)
            r.Line($"  - Economia de energia: esquema {es.SchemeId}, ESBATTTHRESHOLD anterior {es.DcThresholdPrevious}% (causa {CauseLabel(es.Cause)}, desde {Local(es.SetAt)})");
        foreach (var p in j.FrozenProcesses)
        {
            var identity = p.HasIdentity
                ? $"criado {Local(p.CreationTime!.Value)}, boot {p.BootId}"
                : "não verificável (sem identidade: nunca será retomado pelo app)";
            r.Line($"  - congelado: {p.Name} pid {p.Pid} (grupo {p.Group}) · {identity}");
        }
        if (j.DockerFrozen) r.Line("  - Docker pausado");
        foreach (var a in j.SavedAudioOutputs)
            r.Line($"  - áudio: {a.Name ?? "(sem nome)"} [{a.DeviceId}] volume {Math.Round(a.Volume * 100)}%, {(a.Muted ? "estava mudo" : "não estava mudo")}");

        if (!sessionValid)
            r.Problem("recuperação pendente: diário sujo sem sessão válida (o backstop desfaz no próximo minuto; ou rode `Insomnia.exe --backstop --force`)");
        else if (j.FrozenProcesses.Any(p => !p.HasIdentity))
            r.Warning("há processo congelado não verificável no diário");
    }

    private static void WriteEnergy(Report r, DiagnosticsDeps d, bool skipPreflight, bool sessionValid)
    {
        r.Section("Energia");

        LidActionValues? current = null;
        try
        {
            var v = d.SleepGuard.ReadLidAction();
            current = v;
            r.Line($"esquema ativo: {v.SchemeId}");
            r.Line($"LIDACTION: AC {LidLabel(v.Ac)} · DC {LidLabel(v.Dc)}");
            if (sessionValid && (v.Ac != 0 || v.Dc != 0))
                r.Warning("sessão válida mas LIDACTION não está em 0 (o app reaplica ao abrir)");
            else if (!sessionValid && (v.Ac == 0 || v.Dc == 0))
                r.Warning("LIDACTION em \"nada\" sem sessão do Insomnia (veja o diário acima; se estiver limpo, foi você ou outro programa)");
        }
        catch (Exception e)
        {
            r.Problem($"LIDACTION ilegível: {e.Message}");
        }

        if (skipPreflight)
            r.Line($"pré-voo de permissão: pulado ({SkipPreflightFlag})");
        else if (current is not null)
            Preflight(r, d);
        else
            r.Line("pré-voo de permissão: pulado (LIDACTION ilegível)");

        try
        {
            var es = d.EnergySaver.Read();
            r.Line($"ESBATTTHRESHOLD (bateria): {es.DcThreshold}%{(es.DcThreshold >= 100 ? " (Economia sempre ligada na bateria)" : "")}");
        }
        catch (Exception e)
        {
            r.Warning($"ESBATTTHRESHOLD ilegível: {e.Message} (Economia de energia não vai funcionar)");
        }

        try
        {
            var p = d.ReadPower();
            var battery = p.BatteryPercent is { } pct ? $"{pct}%" : "ilegível ou sem bateria";
            r.Line($"bateria: {battery} · {(p.OnAc ? "na tomada" : "na bateria")}");
        }
        catch (Exception e)
        {
            r.Warning($"bateria ilegível: {e.Message}");
        }

        try
        {
            var lid = d.ReadLid();
            r.Line($"tampa: {LidStateLabel(lid)}");
        }
        catch (Exception e)
        {
            r.Warning($"tampa ilegível: {e.Message}");
        }
    }

    /// Relê e reescreve o MESMO LIDACTION sob o lock de recuperação. Lock ocupado ⇒ pula
    /// (o app ou o backstop estão no meio de uma transação; nada é escrito).
    private static void Preflight(Report r, DiagnosticsDeps d)
    {
        IDisposable held;
        try
        {
            held = d.Lock.AcquireAsync(d.LockTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            r.Warning($"pré-voo de permissão: pulado (lock de recuperação ocupado: {e.Message}); rode de novo em instantes");
            return;
        }

        using (held)
        {
            try
            {
                var v = d.SleepGuard.ReadLidAction();
                d.SleepGuard.WriteLidAction(v.SchemeId, v.Ac, v.Dc);
                r.Line($"pré-voo de permissão: ok (reescreveu AC {v.Ac} / DC {v.Dc} no esquema {v.SchemeId})");
            }
            catch (Exception e)
            {
                r.Problem($"pré-voo de permissão FALHOU: {e.Message} — sem permissão para mudar o plano de energia, nenhuma sessão começa (política de grupo?)");
            }
        }
    }

    private static void WriteBackstop(Report r, DiagnosticsDeps d, bool sessionValid)
    {
        r.Section("Backstop");
        var want = d.BackstopDefinition;
        BackstopState state;
        try { state = d.Backstop.Query(want); }
        catch (Exception e)
        {
            d.Log.Error($"diagnóstico: consulta do backstop lançou: {e.Message}");
            state = BackstopState.Unknown;
        }
        var label = state switch
        {
            BackstopState.Armed => "armada (Armed)",
            BackstopState.Missing => "ausente (Missing)",
            BackstopState.Different => "diferente do esperado (Different)",
            _ => "desconhecido (Unknown)",
        };
        r.Line($"tarefa \"{want.TaskName}\": {label}");
        r.Line($"esperado: \"{want.ExecutablePath}\" {want.Arguments} a cada {want.Interval.TotalMinutes:0} min, também na bateria");

        if (state == BackstopState.Armed) return;
        if (sessionValid)
            r.Problem("sessão válida sem backstop armado: se o app morrer, ninguém desfaz o LIDACTION");
        else if (state == BackstopState.Missing)
            r.Line("normal sem sessão: o app instala a tarefa ao iniciar a primeira sessão");
        else
            r.Warning("a tarefa existe mas não bate com este exe (o app a substitui ao iniciar uma sessão)");
    }

    private static void WriteNetwork(Report r, DiagnosticsDeps d, Config config)
    {
        r.Section("Rede");
        try { r.Line($"Wi-Fi conectado: {YesNo(d.Wifi.IsConnected)}"); }
        catch (Exception e) { r.Warning($"Wi-Fi ilegível: {e.Message}"); }

        var ssid = config.HotspotSsid.Trim();
        if (ssid.Length == 0)
        {
            r.Line("hotspot de reserva: não configurado");
            return;
        }
        try
        {
            if (d.Wifi.HasProfile(ssid))
                r.Line($"hotspot de reserva \"{ssid}\": perfil encontrado");
            else
                r.Warning($"Perfil do hotspot não encontrado: conecte-se a \"{ssid}\" uma vez pelo Windows");
        }
        catch (Exception e)
        {
            r.Warning($"hotspot de reserva \"{ssid}\": não deu para ler os perfis Wi-Fi: {e.Message}");
        }
    }

    private static void WriteBrowsers(Report r, DiagnosticsDeps d)
    {
        r.Section("Navegadores");
        IReadOnlyList<BrowserInstance> instances;
        try { instances = d.Browsers.Scan(); }
        catch (Exception e)
        {
            r.Warning($"leitura dos navegadores falhou: {e.Message}");
            return;
        }
        if (instances.Count == 0)
        {
            r.Line("nenhum Chrome, Edge ou Brave em execução");
            return;
        }
        foreach (var i in instances)
        {
            var name = ChromiumFlags.DisplayNameFor(i.Kind);
            if (string.IsNullOrWhiteSpace(i.CommandLine))
            {
                r.Line($"{name} (pid {i.Pid}): linha de comando ilegível");
                continue;
            }
            if (ChromiumFlags.HasBothFlags(i.CommandLine))
                r.Line($"{name} (pid {i.Pid}): sem limitação ({ChromiumFlags.Occluded} {ChromiumFlags.Renderer})");
            else
                r.Warning($"{name} (pid {i.Pid}) está limitado: faltam {ChromiumFlags.Occluded} e/ou {ChromiumFlags.Renderer} (relance pela bandeja)");
        }
    }

    private static void WriteApp(Report r, DiagnosticsDeps d, bool sessionValid)
    {
        r.Section("App");
        bool? running = null;
        try { running = d.IsAppRunning(); }
        catch (Exception e) { r.Warning($"não deu para saber se o app está aberto: {e.Message}"); }
        if (running is { } isRunning)
            r.Line($"bandeja em execução: {YesNo(isRunning)}");

        // O hold (PowerSetRequest) pertence ao processo da bandeja e morre com ele (R7): daqui
        // só dá para deduzir. Confirmação: `powercfg /requests` (pede administrador).
        if (sessionValid && running == true)
            r.Line("hold de execução: deve estar ativo (sessão válida + app aberto); confira em `powercfg /requests`");
        else if (sessionValid && running == false)
            r.Warning("sessão válida com o app fechado: sem hold de execução; o LIDACTION continua em 0 até o fim, quando o backstop desfaz");
        else
            r.Line("hold de execução: inativo (sem sessão)");

        try { r.Line($"iniciar com o Windows: {YesNo(d.AutoStart.IsRegistered())}"); }
        catch (Exception e) { r.Warning($"iniciar com o Windows ilegível: {e.Message}"); }
    }

    // ---------- leitores reais ----------

    /// Lê a tampa uma vez: a janela de eventos recebe o valor atual logo ao registrar, e o laço
    /// de mensagens deste thread (STA do Main) entrega. Sem sensor ⇒ Unknown após 1,5 s.
    private static LidState ReadLidOnce(ILog log)
    {
        using var window = new PowerEventsWindow(log);
        window.Start();
        var deadline = Environment.TickCount64 + 1500;
        var lid = window.CurrentLid;
        while (lid == LidState.Unknown && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(20);
            lid = window.CurrentLid;
        }
        window.Stop();
        return lid;
    }

    private static bool IsUiRunning()
    {
        if (!Mutex.TryOpenExisting(UiMutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// WinExe não tem console: quando chamado de um terminal sem redirecionamento, pega o
    /// console do pai para o relatório aparecer. Com saída redirecionada (`| Out-Host`), o
    /// handle já existe e nada muda.
    private static void AttachParentConsoleIfNeeded()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            AttachConsole(AttachParentProcess);
    }

    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    // ---------- formatação ----------

    private static string Local(DateTimeOffset t) =>
        t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "sim" : "não";

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count == 0 ? "(nenhum)" : string.Join(", ", all);
    }

    internal static string LidLabel(uint value) => value switch
    {
        0 => "0 (nada)",
        1 => "1 (suspender)",
        2 => "2 (hibernar)",
        3 => "3 (desligar)",
        _ => $"{value} (desconhecido)",
    };

    private static string LidStateLabel(LidState s) => s switch
    {
        LidState.Open => "aberta",
        LidState.Closed => "fechada",
        _ => "desconhecida (sem sensor ou sem resposta)",
    };

    private static string CauseLabel(string cause) => cause switch
    {
        "battery" => "bateria",
        "lid" => "tampa",
        _ => cause,
    };

    /// Acumula as linhas e os achados; o resumo decide a saída.
    private sealed class Report(TextWriter output)
    {
        private readonly List<string> _problems = [];
        private readonly List<string> _warnings = [];

        public int ProblemCount => _problems.Count;
        public int WarningCount => _warnings.Count;

        public void Section(string title)
        {
            output.WriteLine();
            output.WriteLine($"[{title}]");
        }

        public void Line(string text) => output.WriteLine("  " + text);

        public void Problem(string text)
        {
            _problems.Add(text);
            output.WriteLine("  ✖ " + text);
        }

        public void Warning(string text)
        {
            _warnings.Add(text);
            output.WriteLine("  ⚠ " + text);
        }

        public int WriteSummary()
        {
            output.WriteLine();
            output.WriteLine("[Resumo]");
            if (_problems.Count == 0)
                output.WriteLine(_warnings.Count == 0 ? "  tudo certo" : $"  sem problemas · {_warnings.Count} aviso(s)");
            else
                output.WriteLine($"  {_problems.Count} problema(s):");
            foreach (var p in _problems) output.WriteLine("  ✖ " + p);
            foreach (var w in _warnings) output.WriteLine("  ⚠ " + w);
            output.Flush();
            return _problems.Count == 0 ? 0 : 1;
        }
    }
}
