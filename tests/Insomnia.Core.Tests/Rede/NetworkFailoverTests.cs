using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Network;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Rede;

/// NetworkFailover dirigido só por fakes: Wi-Fi, tempo virtual, log, notificações e config. O
/// único efeito real é o handoffs.log, escrito numa pasta temporária do teste.
public sealed class NetworkFailoverTests : IDisposable
{
    private const string Ssid = "Hotspot do Caio";
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "insomnia-rede-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeScheduler _scheduler;
    private readonly FakeWifi _wifi = new();
    private readonly FakeLog _log = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FakeConfigSource _config = new(Config.Default with { HotspotSsid = Ssid });

    public NetworkFailoverTests()
    {
        _scheduler = new FakeScheduler(_clock);
        _wifi.Profiles.Add(Ssid);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string HandoffsPath => Path.Combine(_dir, "Logs", "handoffs.log");

    private NetworkFailover Make(IWifi? wifi = null) =>
        new(wifi ?? _wifi, _scheduler, _clock, _log, _notifier, _config, HandoffsPath);

    private string[] HandoffLines() => File.Exists(HandoffsPath) ? File.ReadAllLines(HandoffsPath) : [];

    [Fact]
    public void Inativo_antes_do_Start_ignora_quedas()
    {
        var f = Make();
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(120));
        Assert.Empty(_scheduler.Requested);
        Assert.Empty(_wifi.ConnectCalls);
        Assert.False(_wifi.Started);
        Assert.False(f.IsRunning);
    }

    [Fact]
    public void Queda_espera_5s_e_tenta_o_perfil_salvo()
    {
        var f = Make();
        f.Start();
        Assert.True(_wifi.Started);
        _wifi.ConnectResult = false;
        _wifi.RaiseConnectivity(false);
        Assert.Equal(S(5), Assert.Single(_scheduler.PendingDelays));
        _scheduler.Advance(S(4.9));
        Assert.Empty(_wifi.ConnectCalls);
        _scheduler.Advance(S(0.1));
        Assert.Equal(new[] { Ssid }, _wifi.ConnectCalls);
        Assert.True(f.InOutage);
    }

    [Fact]
    public void Backoff_5_10_20_30_30_sem_desistir_com_timer_unico()
    {
        var f = Make();
        f.Start();
        _wifi.ConnectResult = false;
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(5));
        var gaps = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            var next = Assert.Single(_scheduler.PendingDelays);   // nunca mais de um timer
            gaps.Add(next);
            _scheduler.Advance(next);
        }
        Assert.Equal(new[] { S(5), S(10), S(20), S(30), S(30), S(30) }, gaps);
        Assert.Equal(7, _wifi.ConnectCalls.Count);

        // Uma hora depois continua tentando a cada 30 s.
        _scheduler.Advance(TimeSpan.FromHours(1));
        Assert.Equal(7 + 120, _wifi.ConnectCalls.Count);
        Assert.Equal(1, _scheduler.PendingCount);
    }

    [Fact]
    public void Volta_grava_handoffs_e_LastGap_sem_notificar_queda_curta()
    {
        var f = Make();
        var changed = 0;
        f.Changed += () => changed++;
        f.Start();
        _wifi.RaiseConnectivity(false);
        _clock.Advance(S(42));
        _wifi.RaiseConnectivity(true);

        Assert.Equal(S(42), f.LastGap);
        Assert.False(f.InOutage);
        Assert.Equal(0, _scheduler.PendingCount);
        Assert.Equal(new[] { "2026-10-11T18:00:42Z outage start=2026-10-11T18:00:00Z end=2026-10-11T18:00:42Z gap=42s" }, HandoffLines());
        Assert.Empty(_notifier.Posts);
        Assert.True(changed >= 3);   // Start, queda, volta
        Assert.True(_log.Contains("Wi-Fi voltou depois de 42s"));
    }

    [Fact]
    public void Queda_de_90s_ou_mais_notifica_Rede_voltou()
    {
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _clock.Advance(S(130));
        _wifi.RaiseConnectivity(true);
        var post = Assert.Single(_notifier.Posts);
        Assert.Equal("Rede voltou", post.Title);
        Assert.Contains("2m 10s", post.Body);
        Assert.Single(HandoffLines());
    }

    [Fact]
    public void Volta_por_ConnectAsync_ok_sem_evento_tambem_recupera()
    {
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        // FakeWifi.ConnectAsync(ok) marca IsConnected sem disparar o evento.
        _scheduler.Advance(S(5));
        Assert.False(f.InOutage);
        Assert.Equal(S(5), f.LastGap);
        Assert.Single(HandoffLines());
    }

    [Fact]
    public void SSID_vazio_nao_conecta_mas_segue_retentando()
    {
        _config.Update(c => c with { HotspotSsid = "  " });
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_wifi.ConnectCalls);
        Assert.Equal(1, _scheduler.PendingCount);
        Assert.True(f.InOutage);
        // Log da tentativa pulada só uma vez por queda (não enche o log a cada 30 s).
        Assert.Single(_log.Lines, l => l.Contains("nenhum SSID configurado"));
    }

    [Fact]
    public void Perfil_ausente_avisa_e_notifica_uma_vez_por_queda()
    {
        _wifi.Profiles.Clear();
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(TimeSpan.FromMinutes(5));

        Assert.Empty(_wifi.ConnectCalls);
        Assert.Equal(NetworkFailover.ProfileMissingWarning, f.Warning);
        var post = Assert.Single(_notifier.Posts);
        Assert.Equal("Hotspot não conectado", post.Title);
        Assert.Equal("Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows", post.Body);
        Assert.False(_log.Contains(Ssid));   // a linha nomeia o problema, não o SSID

        // Nova queda: notifica de novo.
        _wifi.RaiseConnectivity(true);
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(5));
        Assert.Equal(2, _notifier.Posts.Count(p => p.Title == "Hotspot não conectado"));
    }

    [Fact]
    public void Perfil_criado_depois_limpa_o_aviso_e_conecta()
    {
        _wifi.Profiles.Clear();
        _wifi.ConnectResult = false;
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(5));
        Assert.NotNull(f.Warning);

        _wifi.Profiles.Add(Ssid);
        _scheduler.Advance(S(5));
        Assert.Null(f.Warning);
        Assert.Equal(new[] { Ssid }, _wifi.ConnectCalls);
    }

    [Fact]
    public void Trocar_o_SSID_limpa_o_aviso_do_anterior()
    {
        _wifi.Profiles.Clear();
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(5));
        Assert.NotNull(f.Warning);
        _config.Update(c => c with { HotspotSsid = "Outro" });
        Assert.Null(f.Warning);
    }

    [Fact]
    public void Stop_cancela_o_timer_e_ignora_eventos_depois()
    {
        var f = Make();
        f.Start();
        _wifi.ConnectResult = false;
        _wifi.RaiseConnectivity(false);
        _scheduler.Advance(S(5));
        f.Stop();

        Assert.False(_wifi.Started);
        Assert.Equal(0, _scheduler.PendingCount);
        _wifi.RaiseConnectivity(true);
        _scheduler.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(_wifi.ConnectCalls);
        Assert.Empty(HandoffLines());   // queda interrompida pelo fim da sessão não vira linha
        Assert.Null(f.LastGap);
    }

    [Fact]
    public void Start_ja_sem_WiFi_comeca_a_queda_na_hora()
    {
        _wifi.IsConnected = false;
        var f = Make();
        f.Start();
        Assert.True(f.InOutage);
        Assert.Equal(S(5), Assert.Single(_scheduler.PendingDelays));
    }

    [Fact]
    public void Start_de_novo_numa_nova_sessao_zera_LastGap()
    {
        var f = Make();
        f.Start();
        _wifi.RaiseConnectivity(false);
        _clock.Advance(S(10));
        _wifi.RaiseConnectivity(true);
        Assert.NotNull(f.LastGap);
        f.Stop();
        f.Start();
        Assert.Null(f.LastGap);
    }

    [Fact]
    public void ConnectAsync_que_lanca_eh_logado_e_retentado()
    {
        var wifi = new ThrowingWifi();
        var f = Make(wifi);
        f.Start();
        wifi.Raise(false);
        _scheduler.Advance(S(5 + 5 + 10));
        Assert.Equal(3, wifi.Calls);
        Assert.True(_log.Contains("tentativa de hotspot falhou: WLAN fora"));
        Assert.Equal(1, _scheduler.PendingCount);
    }

    [Fact]
    public void Tentativa_pendurada_nao_abre_outra_em_paralelo()
    {
        var wifi = new HangingWifi();
        var f = Make(wifi);
        f.Start();
        wifi.Raise(false);
        _scheduler.Advance(S(60));
        Assert.Equal(1, wifi.Calls);

        wifi.Complete(false);
        _scheduler.Advance(S(30));
        Assert.Equal(2, wifi.Calls);
    }

    [Fact]
    public void Volta_cancela_a_tentativa_em_andamento()
    {
        var wifi = new HangingWifi();
        var f = Make(wifi);
        f.Start();
        wifi.Raise(false);
        _scheduler.Advance(S(5));
        Assert.False(wifi.Token.IsCancellationRequested);
        wifi.Raise(true);
        Assert.True(wifi.Token.IsCancellationRequested);
    }

    // ---------- fakes locais ----------

    private sealed class ThrowingWifi : IWifi
    {
        public event Action<bool>? ConnectivityChanged;
        public bool IsConnected { get; private set; } = true;
        public int Calls { get; private set; }
        public bool HasProfile(string ssid) => true;
        public Task<bool> ConnectAsync(string profileName, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("WLAN fora");
        }
        public void Start() { }
        public void Stop() { }
        public void Raise(bool connected) { IsConnected = connected; ConnectivityChanged?.Invoke(connected); }
    }

    private sealed class HangingWifi : IWifi
    {
        private TaskCompletionSource<bool> _tcs = new();
        public event Action<bool>? ConnectivityChanged;
        public bool IsConnected { get; private set; } = true;
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool HasProfile(string ssid) => true;
        public Task<bool> ConnectAsync(string profileName, CancellationToken ct)
        {
            Calls++;
            Token = ct;
            _tcs = new TaskCompletionSource<bool>();
            return _tcs.Task;
        }
        public void Complete(bool ok)
        {
            // A continuação só roda inline (determinística) sem SynchronizationContext: o xUnit
            // instala um no thread do teste, e com ele o .NET agenda a continuação no pool.
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { _tcs.SetResult(ok); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        public void Start() { }
        public void Stop() { }
        public void Raise(bool connected) { IsConnected = connected; ConnectivityChanged?.Invoke(connected); }
    }
}
