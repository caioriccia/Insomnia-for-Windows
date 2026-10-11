using System.Globalization;
using System.Management;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;

namespace Insomnia.Windows.Browser;

/// Uma linha de Win32_Process, já convertida (o que a consulta WMI devolve).
public sealed record BrowserProcessRow(int Pid, int ParentPid, string Name, string? ExecutablePath, string? CommandLine, string? CreationDate, int SessionId);

/// Decisões puras sobre processos de navegador (testáveis sem WMI).
public static class BrowserProcessFilter
{
    /// Processo principal do Chromium: o que não tem "--type=" (renderer, gpu, utility e crashpad
    /// têm). Linha de comando ilegível (null/vazia): decide pelo pai — o principal não é filho de
    /// outro processo do mesmo navegador.
    public static bool IsMainProcess(string? commandLine, bool parentIsSameBrowser)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return !parentIsSameBrowser;
        return !ChromiumFlags.HasTypeSwitch(commandLine);
    }

    /// Os processos principais dos navegadores conhecidos, só da sessão do Windows indicada.
    /// Kind = nome canônico do catálogo (ex.: "chrome.exe"); identidade = hora de criação + bootId
    /// (null se a data não pôde ser lida, o que bloqueia o relançamento).
    public static IReadOnlyList<BrowserInstance> SelectMain(IEnumerable<BrowserProcessRow> rows, int sessionId, string bootId)
    {
        var mine = rows.Where(r => r.SessionId == sessionId && CanonicalKind(r.Name) is not null).ToList();
        var result = new List<BrowserInstance>();
        foreach (var row in mine)
        {
            var kind = CanonicalKind(row.Name)!;
            var parentIsSame = mine.Any(o => o.Pid == row.ParentPid && o.Pid != row.Pid
                && string.Equals(CanonicalKind(o.Name), kind, StringComparison.Ordinal));
            if (!IsMainProcess(row.CommandLine, parentIsSame)) continue;
            var created = ParseWmiDate(row.CreationDate);
            result.Add(new BrowserInstance(
                kind,
                ChromiumFlags.DisplayNameFor(kind),
                row.Pid,
                row.ExecutablePath ?? "",
                row.CommandLine ?? "",
                created is { } c ? new ProcessIdentity(c, bootId) : null));
        }
        return result;
    }

    /// Nome canônico do catálogo para um nome de executável; null se não é navegador conhecido.
    public static string? CanonicalKind(string? exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return null;
        foreach (var (kind, _) in ChromiumFlags.KnownBrowsers)
            if (string.Equals(kind, exeName, StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }

    /// Data DMTF do WMI ("20261011091203.123456-180") ⇒ instante; ilegível ⇒ null.
    public static DateTimeOffset? ParseWmiDate(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 25) return null;
        try
        {
            // Parte local + deslocamento em minutos (sinal incluso), como o WMI escreve.
            var local = DateTime.ParseExact(dmtf[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None);
            var offsetMinutes = int.Parse(dmtf[21..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offsetMinutes));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// Consulta WQL dos navegadores do catálogo.
    public static string WqlQuery() =>
        "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate, SessionId FROM Win32_Process WHERE "
        + string.Join(" OR ", ChromiumFlags.KnownBrowsers.Select(b => $"Name = '{b.Kind}'"));

    /// Converte um objeto WMI numa linha (fora do puro: depende de ManagementBaseObject).
    internal static BrowserProcessRow FromWmi(ManagementBaseObject o) => new(
        Pid: Convert.ToInt32(o["ProcessId"] ?? 0, CultureInfo.InvariantCulture),
        ParentPid: Convert.ToInt32(o["ParentProcessId"] ?? 0, CultureInfo.InvariantCulture),
        Name: o["Name"] as string ?? "",
        ExecutablePath: o["ExecutablePath"] as string,
        CommandLine: o["CommandLine"] as string,
        CreationDate: o["CreationDate"] as string,
        SessionId: Convert.ToInt32(o["SessionId"] ?? -1, CultureInfo.InvariantCulture));
}
