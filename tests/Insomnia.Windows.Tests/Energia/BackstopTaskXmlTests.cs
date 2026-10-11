using System.Xml.Linq;
using Insomnia.Core.Abstractions;
using Insomnia.Windows.Scheduler;

namespace Insomnia.Windows.Tests.Energia;

/// Gerador e leitor puros do XML do backstop (§7.1). Nada aqui toca o Agendador.
public class BackstopTaskXmlTests
{
    private static readonly BackstopDefinition Def = BackstopDefinition.For(@"C:\Users\Caio Teste\AppData\Local\Programs\Insomnia\Insomnia.exe");
    private static readonly DateTime Start = new(2026, 10, 11, 18, 0, 5, DateTimeKind.Local);
    private static readonly XNamespace Ns = BackstopTaskXml.Ns;

    private static XElement Root(string xml) => XDocument.Parse(xml).Root!;

    private static string Value(XElement root, params string[] path)
    {
        var e = root;
        foreach (var name in path) e = e.Element(Ns + name) ?? throw new Xunit.Sdk.XunitException($"falta <{name}>");
        return e.Value;
    }

    /// Edita o XML gerado para simular uma tarefa registrada diferente.
    private static string Edit(Action<XElement> change)
    {
        var doc = XDocument.Parse(BackstopTaskXml.Build(Def, Start));
        change(doc.Root!);
        return doc.ToString();
    }

    [Fact]
    public void Build_produz_os_campos_de_7_1()
    {
        var xml = BackstopTaskXml.Build(Def, Start);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", xml, StringComparison.OrdinalIgnoreCase);

        var root = Root(xml);
        Assert.Equal("Task", root.Name.LocalName);
        Assert.Equal("http://schemas.microsoft.com/windows/2004/02/mit/task", root.Name.NamespaceName);
        Assert.Equal("1.4", root.Attribute("version")?.Value);
        Assert.Equal("Insomnia: desfaz mudanças de energia se o app morreu e a sessão acabou.", Value(root, "RegistrationInfo", "Description"));

        Assert.Equal("2026-10-11T18:00:05", Value(root, "Triggers", "TimeTrigger", "StartBoundary"));
        Assert.Equal("true", Value(root, "Triggers", "TimeTrigger", "Enabled"));
        Assert.Equal("PT1M", Value(root, "Triggers", "TimeTrigger", "Repetition", "Interval"));
        Assert.Equal("false", Value(root, "Triggers", "TimeTrigger", "Repetition", "StopAtDurationEnd"));

        Assert.Equal("Author", root.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Attribute("id")?.Value);
        Assert.Equal("InteractiveToken", Value(root, "Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", Value(root, "Principals", "Principal", "RunLevel"));

        var expectedSettings = new Dictionary<string, string>
        {
            ["MultipleInstancesPolicy"] = "IgnoreNew",
            ["DisallowStartIfOnBatteries"] = "false",
            ["StopIfGoingOnBatteries"] = "false",
            ["AllowHardTerminate"] = "true",
            ["StartWhenAvailable"] = "true",
            ["RunOnlyIfNetworkAvailable"] = "false",
            ["AllowStartOnDemand"] = "true",
            ["Enabled"] = "true",
            ["Hidden"] = "true",
            ["RunOnlyIfIdle"] = "false",
            ["WakeToRun"] = "false",
            ["ExecutionTimeLimit"] = "PT2M",
            ["Priority"] = "7",
        };
        foreach (var (name, value) in expectedSettings) Assert.Equal(value, Value(root, "Settings", name));
        Assert.Equal("false", Value(root, "Settings", "IdleSettings", "StopOnIdleEnd"));
        Assert.Equal("false", Value(root, "Settings", "IdleSettings", "RestartOnIdle"));

        Assert.Equal("Author", root.Element(Ns + "Actions")!.Attribute("Context")?.Value);
        Assert.Equal(Def.ExecutablePath, Value(root, "Actions", "Exec", "Command"));
        Assert.Equal("--backstop", Value(root, "Actions", "Exec", "Arguments"));
    }

    [Fact]
    public void Build_escapa_caracteres_de_xml_no_caminho()
    {
        var def = BackstopDefinition.For(@"C:\Users\A&B <x>\Insomnia.exe");
        var xml = BackstopTaskXml.Build(def, Start);
        Assert.Contains("A&amp;B &lt;x&gt;", xml);
        Assert.Equal(BackstopState.Armed, BackstopTaskXml.Matches(xml, def));
    }

    [Fact]
    public void Matches_reconhece_o_proprio_xml_como_Armed()
    {
        Assert.Equal(BackstopState.Armed, BackstopTaskXml.Matches(BackstopTaskXml.Build(Def, Start), Def));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_ausente_e_Missing(string? xml)
    {
        Assert.Equal(BackstopState.Missing, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_aceita_a_serializacao_do_Agendador_que_omite_padroes()
    {
        // O Agendador omite <Enabled>true</Enabled> e <StopAtDurationEnd>false</...> ao
        // devolver o XML registrado (conferido em `schtasks /query /xml` desta máquina).
        var xml = Edit(root =>
        {
            foreach (var e in root.Descendants().Where(e => e.Name.LocalName is "Enabled" or "StopAtDurationEnd").ToList()) e.Remove();
        });
        Assert.Equal(BackstopState.Armed, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_ignora_aspas_e_caixa_do_caminho()
    {
        var xml = Edit(root => root.Descendants(Ns + "Command").Single().Value = "\"" + Def.ExecutablePath.ToUpperInvariant() + "\"");
        Assert.Equal(BackstopState.Armed, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_DisallowStartIfOnBatteries_true_e_Different()
    {
        var xml = Edit(root => root.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value = "true");
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_condicao_de_bateria_ausente_vale_o_padrao_true_e_e_Different()
    {
        var semDisallow = Edit(root => root.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Remove());
        var semStop = Edit(root => root.Descendants(Ns + "StopIfGoingOnBatteries").Single().Remove());
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(semDisallow, Def));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(semStop, Def));
    }

    [Fact]
    public void Matches_StopIfGoingOnBatteries_true_e_Different()
    {
        var xml = Edit(root => root.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value = "true");
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_outro_executavel_e_Different()
    {
        var xml = BackstopTaskXml.Build(BackstopDefinition.For(@"C:\Outro\Insomnia.exe"), Start);
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_outro_argumento_e_Different()
    {
        var xml = BackstopTaskXml.Build(Def with { Arguments = "--backstop --force" }, Start);
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_outro_intervalo_e_Different()
    {
        var xml = BackstopTaskXml.Build(Def with { Interval = TimeSpan.FromMinutes(5) }, Start);
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(xml, Def));
    }

    [Fact]
    public void Matches_repeticao_com_duracao_ou_gatilho_com_fim_e_Different()
    {
        var comDuracao = Edit(root => root.Descendants(Ns + "Repetition").Single().Add(new XElement(Ns + "Duration", "PT1H")));
        var comFim = Edit(root => root.Descendants(Ns + "TimeTrigger").Single().Add(new XElement(Ns + "EndBoundary", "2026-10-12T00:00:00")));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(comDuracao, Def));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(comFim, Def));
    }

    [Fact]
    public void Matches_tarefa_ou_gatilho_desabilitado_e_Different()
    {
        var tarefa = Edit(root => root.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Value = "false");
        var gatilho = Edit(root => root.Descendants(Ns + "TimeTrigger").Single().Element(Ns + "Enabled")!.Value = "false");
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(tarefa, Def));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(gatilho, Def));
    }

    [Fact]
    public void Matches_acao_extra_ou_sem_gatilho_e_Different()
    {
        var acaoExtra = Edit(root => root.Element(Ns + "Actions")!.Add(new XElement(Ns + "Exec", new XElement(Ns + "Command", "calc.exe"))));
        var semGatilho = Edit(root => root.Element(Ns + "Triggers")!.RemoveNodes());
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(acaoExtra, Def));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches(semGatilho, Def));
    }

    [Fact]
    public void Matches_xml_ilegivel_e_Different_sem_lancar()
    {
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches("<Task><nao fecha", Def));
        Assert.Equal(BackstopState.Different, BackstopTaskXml.Matches("<Outro/>", Def));
    }
}
