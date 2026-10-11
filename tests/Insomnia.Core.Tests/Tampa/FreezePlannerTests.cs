// FreezePlanner e ProcessControl: decisões puras (desenho §7.2, R8).
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;

namespace Insomnia.Core.Tests.Tampa;

public class FreezePlannerTests
{
    private static readonly PlannerEnvironment Env = TampaHarness.Env;
    private static readonly DateTimeOffset T0 = TampaHarness.T0;

    private static ProcessInfo P(int pid, string name, int parent = 1000, double sec = 0, bool visible = false,
        string? path = null, bool suspended = false, string boot = "17") =>
        new(pid, parent, name, new ProcessIdentity(T0.AddSeconds(sec), boot), suspended, visible, path);

    private static Config Cfg(string[] freeze, bool all = false, string[]? agents = null) =>
        Config.Default with { FreezeList = [.. freeze], FreezeAllApps = all, AgentList = agents is null ? Config.Default.AgentList : [.. agents] };

    [Fact]
    public void GrupoEOProcessoMaisDescendentesEInstanciasDoMesmoExe()
    {
        var snap = new[]
        {
            P(100, "slack.exe"), P(101, "slack.exe", parent: 100, sec: 1), P(102, "crashpad.exe", parent: 101, sec: 2),
            P(300, "slack.exe", sec: 5), P(400, "notepad.exe"),
        };

        var g = Assert.Single(FreezePlanner.Plan(Cfg(["SLACK.EXE"]), snap, Env));

        Assert.Equal("SLACK.EXE", g.Name);
        Assert.True(g.Explicit);
        Assert.Equal([100, 101, 102, 300], g.Members.Select(m => m.Pid).Order());
    }

    [Fact]
    public void FilhoMaisVelhoQueOPaiNaoEDescendente()
    {
        // ppid aponta para um pid reusado: o "filho" nasceu antes do "pai" atual.
        var snap = new[] { P(100, "slack.exe", sec: 10), P(101, "vitima.exe", parent: 100, sec: 1) };

        var g = Assert.Single(FreezePlanner.Plan(Cfg(["slack.exe"]), snap, Env));

        Assert.Equal([100], g.Members.Select(m => m.Pid));
    }

    [Fact]
    public void JaParadoFicaDeForaMasOsFilhosSeguem()
    {
        var snap = new[] { P(100, "slack.exe", suspended: true), P(101, "slack.exe", parent: 100, sec: 1) };

        var g = Assert.Single(FreezePlanner.Plan(Cfg(["slack.exe"]), snap, Env));

        Assert.Equal([101], g.Members.Select(m => m.Pid));
    }

    [Fact]
    public void AgentListVenceFreezeList()
    {
        var snap = new[] { P(100, "Code.exe"), P(101, "slack.exe") };

        var groups = FreezePlanner.Plan(Cfg(["Code.exe", "slack.exe"]), snap, Env);

        Assert.Equal(["slack.exe"], groups.Select(g => g.Name));
    }

    [Fact]
    public void DescendenteNaAgentListCortaASubarvore()
    {
        var snap = new[] { P(100, "slack.exe"), P(101, "node.exe", parent: 100, sec: 1), P(102, "x.exe", parent: 101, sec: 2) };

        var g = Assert.Single(FreezePlanner.Plan(Cfg(["slack.exe"]), snap, Env));

        Assert.Equal([100], g.Members.Select(m => m.Pid));
    }

    [Fact]
    public void ExplicitoContornaProtegidosEmbutidosMasAutomaticoNao()
    {
        var snap = new[] { P(100, "Zoom.exe", visible: true), P(200, "Spotify.exe", visible: true), P(300, "Teams.exe", visible: true) };

        var groups = FreezePlanner.Plan(Cfg(["Zoom.exe"], all: true, agents: []), snap, Env);

        Assert.Equal(["Zoom.exe", "Spotify.exe"], groups.Select(g => g.Name));
        Assert.True(groups[0].Explicit);
        Assert.False(groups[1].Explicit);
    }

    [Fact]
    public void AutomaticoSoPegaQuemTemJanelaVisivel()
    {
        var snap = new[] { P(100, "Spotify.exe", visible: true), P(200, "updater.exe"), P(300, "Spotify.exe", sec: 1) };

        Assert.Equal(["Spotify.exe"], FreezePlanner.AutomaticCandidates(Cfg([], all: true), snap, Env));
        var g = Assert.Single(FreezePlanner.Plan(Cfg([], all: true), snap, Env));
        Assert.Equal([100, 300], g.Members.Select(m => m.Pid)); // todas as instâncias do exe
    }

    [Fact]
    public void FreezeAllAppsDesligadoNaoPegaAutomaticos()
    {
        var snap = new[] { P(100, "Spotify.exe", visible: true) };
        Assert.Empty(FreezePlanner.Plan(Cfg([]), snap, Env));
    }

    [Fact]
    public void DenylistDeSistemaValeAteParaAListaExplicita()
    {
        var snap = new[]
        {
            P(100, "explorer.exe", visible: true), P(101, "dwm.exe"), P(102, "Insomnia.exe"), P(9999, "renomeado.exe"),
            P(103, "notepad.exe", visible: true, path: @"C:\Windows\System32\notepad.exe"),
            P(104, "Docker Desktop.exe", visible: true),
        };

        var groups = FreezePlanner.Plan(Cfg(["explorer.exe", "dwm.exe", "Insomnia.exe", "renomeado.exe", "notepad.exe", "Docker Desktop.exe"], all: true, agents: []), snap, Env);

        Assert.Empty(groups);
    }

    [Fact]
    public void UmPidSoEntraEmUmGrupo()
    {
        var snap = new[] { P(100, "slack.exe"), P(101, "Discord.exe", parent: 100, sec: 1) };

        var groups = FreezePlanner.Plan(Cfg(["slack.exe", "Discord.exe"]), snap, Env);

        Assert.Equal(["slack.exe"], groups.Select(g => g.Name));
        Assert.Equal([100, 101], groups[0].Members.Select(m => m.Pid));
    }

    [Fact]
    public void DockerRuleEDonaDaArvoreMesmoComDockerNaAgentList()
    {
        var snap = new[]
        {
            P(200, "Docker Desktop.exe"), P(201, "com.docker.backend.exe", parent: 200, sec: 1),
            P(202, "wsl.exe", parent: 201, sec: 2, path: @"C:\Windows\System32\wsl.exe"),
        };

        Assert.Empty(FreezePlanner.Plan(Cfg(["Docker Desktop.exe"]), snap, Env)); // planejador comum não pega
        var g = DockerRule.Group(snap, Env);

        Assert.NotNull(g);
        Assert.Equal(DockerRule.GroupName, g!.Name);
        Assert.Equal([200, 201], g.Members.Select(m => m.Pid));
        Assert.Null(DockerRule.Group([P(1, "slack.exe")], Env));
    }

    // ---------- ProcessControl ----------

    [Fact]
    public void R8_IsSuspendableExigeMesmaIdentidadeMesmoPpidENaoParado()
    {
        var seen = P(100, "slack.exe", parent: 50);
        Assert.True(ProcessControl.IsSuspendable(seen, seen));
        Assert.False(ProcessControl.IsSuspendable(seen, null));
        Assert.False(ProcessControl.IsSuspendable(seen, seen with { IsSuspended = true }));
        Assert.False(ProcessControl.IsSuspendable(seen, seen with { ParentPid = 51 }));
        Assert.False(ProcessControl.IsSuspendable(seen, P(100, "slack.exe", parent: 50, sec: 60)));
        Assert.False(ProcessControl.IsSuspendable(seen, seen with { Identity = null }));
        var noBoot = P(100, "slack.exe", parent: 50, boot: "");
        Assert.False(ProcessControl.IsSuspendable(noBoot, noBoot));
    }

    [Fact]
    public void R8_ResumeDecisionCobreTodosOsCasos()
    {
        var running = P(100, "slack.exe");
        var stopped = running with { IsSuspended = true };
        var entry = ProcessControl.Promoted(running, "slack.exe");
        var provisional = entry with { CreationTime = null, BootId = null };

        Assert.Equal(ResumeVerdict.Gone, ProcessControl.ResumeDecision(entry, null));
        Assert.Equal(ResumeVerdict.Gone, ProcessControl.ResumeDecision(entry, running));
        Assert.Equal(ResumeVerdict.Unverifiable, ProcessControl.ResumeDecision(provisional, stopped));
        Assert.Equal(ResumeVerdict.Gone, ProcessControl.ResumeDecision(provisional, running));
        Assert.Equal(ResumeVerdict.Gone, ProcessControl.ResumeDecision(entry, P(100, "x.exe", sec: 30, suspended: true)));
        Assert.Equal(ResumeVerdict.Gone, ProcessControl.ResumeDecision(entry, P(100, "slack.exe", suspended: true, boot: "18")));
        Assert.Equal(ResumeVerdict.Resume, ProcessControl.ResumeDecision(entry, stopped));
    }

    [Fact]
    public void CancelDecisionIgnoraEstadoParadoMasExigeIdentidade()
    {
        var seen = P(100, "slack.exe");
        Assert.True(ProcessControl.CancelDecision(seen, seen));                          // stop ainda pendente
        Assert.True(ProcessControl.CancelDecision(seen, seen with { IsSuspended = true }));
        Assert.False(ProcessControl.CancelDecision(seen, P(100, "slack.exe", sec: 9)));
        Assert.False(ProcessControl.CancelDecision(seen, null));
    }

    [Fact]
    public void IdentidadeComparaEmSegundosInteirosPorqueODiarioNaoGuardaFracao()
    {
        var precise = new ProcessIdentity(T0, "17");
        var fromDisk = new ProcessIdentity(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), "17");
        var nextSecond = new ProcessIdentity(fromDisk.CreationTime.AddSeconds(1), "17");

        Assert.True(ProcessControl.SameIdentity(precise, fromDisk));
        Assert.False(ProcessControl.SameIdentity(precise, nextSecond));
        Assert.False(ProcessControl.SameIdentity(precise, precise with { BootId = "18" }));
        Assert.False(ProcessControl.SameIdentity(precise with { BootId = "" }, precise with { BootId = "" }));
    }
}
