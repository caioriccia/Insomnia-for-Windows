using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Scheduler;

/// Gerador e leitor PUROS do XML da tarefa do backstop (§7.1). Sem COM, sem Agendador:
/// é aqui que mora a decisão "a tarefa registrada é a que queremos?", e por isso é testável.
public static class BackstopTaskXml
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public const string Description = "Insomnia: desfaz mudanças de energia se o app morreu e a sessão acabou.";

    /// XML de §7.1. startBoundary é hora LOCAL (o Agendador interpreta sem fuso como local).
    /// O caminho do exe vai sem aspas — o Agendador aceita espaços em &lt;Command&gt;.
    public static string Build(BackstopDefinition def, DateTime startBoundary)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", Description)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "TimeTrigger",
                        new XElement(Ns + "StartBoundary", startBoundary.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "Repetition",
                            new XElement(Ns + "Interval", XmlConvert.ToString(def.Interval)),
                            new XElement(Ns + "StopAtDurationEnd", "false")))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "true"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT2M"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", def.ExecutablePath),
                        new XElement(Ns + "Arguments", def.Arguments)))));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);   // UTF-16, casa com a declaração
        doc.Save(writer, SaveOptions.None);
        return writer.ToString();
    }

    /// Compara o XML de uma tarefa registrada com a definição desejada.
    /// - null/vazio ⇒ Missing;
    /// - Armed só se: uma única ação Exec com o mesmo exe (sem diferenciar caixa, aspas
    ///   ignoradas) e os mesmos argumentos; algum gatilho habilitado repetindo no intervalo
    ///   desejado, sem Duration nem EndBoundary; DisallowStartIfOnBatteries e
    ///   StopIfGoingOnBatteries explicitamente false; tarefa não desabilitada.
    /// - qualquer outra coisa (inclusive XML que não decodifica) ⇒ Different.
    /// Elemento ausente vale o padrão do esquema do Agendador: Enabled ausente = true (o
    /// Agendador omite o padrão ao serializar); as duas de bateria ausentes = TRUE, que é
    /// exatamente o padrão perigoso — o backstop não rodaria na bateria.
    public static BackstopState Matches(string? xml, BackstopDefinition def)
    {
        if (string.IsNullOrWhiteSpace(xml)) return BackstopState.Missing;
        XElement root;
        try { root = XDocument.Parse(xml).Root ?? throw new XmlException("sem raiz"); }
        catch (XmlException) { return BackstopState.Different; }
        if (root.Name.LocalName != "Task") return BackstopState.Different;

        return ActionMatches(root, def) && TriggerMatches(root, def) && SettingsMatch(root)
            ? BackstopState.Armed
            : BackstopState.Different;
    }

    private static bool ActionMatches(XElement root, BackstopDefinition def)
    {
        var actions = Child(root, "Actions")?.Elements().ToList();
        if (actions is not { Count: 1 } || actions[0].Name.LocalName != "Exec") return false;
        var command = Unquote(Child(actions[0], "Command")?.Value);
        var arguments = (Child(actions[0], "Arguments")?.Value ?? "").Trim();
        return string.Equals(command, Unquote(def.ExecutablePath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments, def.Arguments.Trim(), StringComparison.Ordinal);
    }

    private static bool TriggerMatches(XElement root, BackstopDefinition def) =>
        Child(root, "Triggers")?.Elements().Any(trigger =>
        {
            if (Flag(Child(trigger, "Enabled"), true) != true) return false;
            if (Child(trigger, "EndBoundary") is not null) return false;
            var repetition = Child(trigger, "Repetition");
            if (repetition is null) return false;
            if (!string.IsNullOrWhiteSpace(Child(repetition, "Duration")?.Value)) return false;
            return TryDuration(Child(repetition, "Interval")?.Value) == def.Interval;
        }) ?? false;

    private static bool SettingsMatch(XElement root)
    {
        var settings = Child(root, "Settings");
        if (settings is null) return false;
        return Flag(Child(settings, "DisallowStartIfOnBatteries"), true) == false
            && Flag(Child(settings, "StopIfGoingOnBatteries"), true) == false
            && Flag(Child(settings, "Enabled"), true) == true;
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    /// Booleano do esquema (true/false/1/0). Ausente ⇒ padrão; ilegível ⇒ null (não bate).
    private static bool? Flag(XElement? element, bool whenAbsent)
    {
        if (element is null) return whenAbsent;
        try { return XmlConvert.ToBoolean(element.Value.Trim()); }
        catch (FormatException) { return null; }
    }

    private static TimeSpan? TryDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return XmlConvert.ToTimeSpan(text.Trim()); }
        catch (FormatException) { return null; }
    }

    private static string Unquote(string? text) => (text ?? "").Trim().Trim('"').Trim();
}
