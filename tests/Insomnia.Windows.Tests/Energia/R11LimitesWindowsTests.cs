using System.Xml.Linq;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Windows.Docker;
using Insomnia.Windows.Scheduler;

namespace Insomnia.Windows.Tests.Energia;

/// R11 Tudo com limite, do lado dos adaptadores: só constantes e XML, nada executa.
public class R11LimitesWindowsTests
{
    [Fact]
    public void R11_teto_do_docker_ps_e_o_mesmo_do_nucleo_5s()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DockerProbeWin.ProbeTimeout);
        Assert.Equal(DockerRule.ProbeTimeout, DockerProbeWin.ProbeTimeout);
    }

    [Fact]
    public void R11_tarefa_do_backstop_morre_em_2_minutos()
    {
        var def = BackstopDefinition.For(@"C:\Programs\Insomnia\Insomnia.exe");
        var root = XDocument.Parse(BackstopTaskXml.Build(def, new DateTime(2026, 10, 11, 18, 0, 0, DateTimeKind.Local))).Root!;
        var ns = BackstopTaskXml.Ns;

        Assert.Equal("PT2M", root.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value);
    }
}
