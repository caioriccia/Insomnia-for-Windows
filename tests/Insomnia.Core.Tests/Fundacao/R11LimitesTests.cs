using Insomnia.Core.Abstractions;
using Insomnia.Core.Backstop;
using Insomnia.Core.Browser;
using Insomnia.Core.Lid;
using Insomnia.Core.Session;
using Insomnia.Core.Tests.Fakes;
using Insomnia.Core.Tests.Sessao;

namespace Insomnia.Core.Tests.Fundacao;

/// R11 Tudo com limite (desenho §8): lock 10 s, Docker 5 s, quit de navegador 10 s, start 5 s.
/// Cada teste prova o valor E que ele chega a quem espera — não só que a constante existe.
public class R11LimitesTests
{
    // ------------------------------------------------------------------ lock 10 s

    [Fact]
    public async Task R11_lock_do_app_espera_no_maximo_10s()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), SessionManager.LockTimeout);
        var rig = new SessionRig();
        rig.Lock.Held = true;

        Assert.Equal(StartOutcome.Locked, await rig.Manager.StartAsync(TimeSpan.FromHours(1)));

        Assert.Equal(TimeSpan.FromSeconds(10), rig.Lock.RequestedTimeouts.Single());
    }

    [Fact]
    public async Task R11_lock_do_backstop_espera_no_maximo_10s()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), BackstopRunner.LockTimeout);
        var rig = new SessionRig();
        rig.Lock.Held = true;

        Assert.Equal(75, await new BackstopRunner(rig.Store, rig.Lock, rig.Engine(), rig.Clock, rig.Log).RunAsync(false, default));

        Assert.Equal(TimeSpan.FromSeconds(10), rig.Lock.RequestedTimeouts.Single());
    }

    // ------------------------------------------------------------------ Docker 5 s

    /// Sonda que nunca responde sozinha; só termina quando o token que recebeu é cancelado.
    private sealed class HangingProbe : IDockerProbe
    {
        public CancellationToken Received { get; private set; }
        public async Task<DockerProbe> ProbeAsync(CancellationToken ct)
        {
            Received = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return DockerProbe.Idle;
        }
    }

    /// Sonda que ignora o token e nunca termina: o teto tem de valer mesmo assim.
    private sealed class DeafProbe : IDockerProbe
    {
        public Task<DockerProbe> ProbeAsync(CancellationToken ct) => new TaskCompletionSource<DockerProbe>().Task;
    }

    [Fact]
    public void R11_teto_do_Docker_e_5s()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DockerRule.ProbeTimeout);
    }

    [Fact]
    public async Task R11_Docker_que_passa_do_teto_vira_Unavailable_e_a_sonda_e_cancelada()
    {
        var log = new FakeLog();
        var probe = new HangingProbe();

        var result = await DockerRule.ProbeAsync(probe, log, CancellationToken.None, TimeSpan.FromMilliseconds(50));

        Assert.Equal(DockerProbe.Unavailable, result);
        Assert.True(probe.Received.IsCancellationRequested); // o adaptador é avisado para matar o `docker ps`
        Assert.True(log.Contains("checagem passou de"));
    }

    [Fact]
    public async Task R11_Docker_que_ignora_o_cancelamento_nao_prende_quem_espera()
    {
        var log = new FakeLog();
        var task = DockerRule.ProbeAsync(new DeafProbe(), log, CancellationToken.None, TimeSpan.FromMilliseconds(50));

        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, winner);
        Assert.Equal(DockerProbe.Unavailable, await task);
    }

    [Fact]
    public async Task R11_Docker_cancelado_por_evento_de_tampa_continua_com_a_mensagem_de_cancelamento()
    {
        var log = new FakeLog();
        using var cts = new CancellationTokenSource();
        var task = DockerRule.ProbeAsync(new HangingProbe(), log, cts.Token);
        cts.Cancel();

        Assert.Equal(DockerProbe.Unavailable, await task);
        Assert.True(log.Contains("cancelada por novo evento de tampa"));
        Assert.False(log.Contains("checagem passou de"));
    }

    // ------------------------------------------------------------------ quit 10 s, start 5 s

    [Fact]
    public async Task R11_relancar_navegador_fecha_com_teto_de_10s_e_espera_abrir_ate_5s()
    {
        var procs = new FakeBrowserProcesses();
        procs.Instances.Add(new BrowserInstance("chrome.exe", "Google Chrome", 100, @"C:\c\chrome.exe", "\"C:\\c\\chrome.exe\"",
            new ProcessIdentity(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), "17")));
        var delays = new List<TimeSpan>();
        var t = new BrowserThrottle(procs, new FakeLog(), new FakeNotifier())
        {
            Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };
        Assert.Equal(TimeSpan.FromSeconds(10), t.QuitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), t.StartTimeout);

        // Nunca aparece depois do lançamento: a espera para no teto de 5 s.
        Assert.Equal(RelaunchOutcome.DidNotStart, await t.RelaunchUnthrottledAsync("chrome.exe", CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(10), procs.QuitTimeouts.Single());
        Assert.Single(procs.Launches);
        Assert.True(delays.Count > 0);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= TimeSpan.FromSeconds(5));
    }
}
