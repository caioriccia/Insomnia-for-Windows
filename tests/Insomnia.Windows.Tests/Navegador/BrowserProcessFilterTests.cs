using Insomnia.Core.Abstractions;
using Insomnia.Core.Browser;
using Insomnia.Windows.Browser;

namespace Insomnia.Windows.Tests.Navegador;

/// Só o puro do adaptador de navegadores (R12): nenhum teste consulta o WMI nem fecha processo.
public class BrowserProcessFilterTests
{
    private const string Exe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Date = "20261011091203.123456-180";

    private static BrowserProcessRow Row(int pid, int parent, string? cmd, string name = "chrome.exe", int session = 1, string? date = Date) =>
        new(pid, parent, name, Exe, cmd, date, session);

    // Linhas de comando como o WMI devolve para um Chrome real.
    private const string MainLine = "\"" + Exe + "\" --profile-directory=\"Profile 1\"";
    private const string RendererLine = "\"" + Exe + "\" --type=renderer --lang=pt-BR --renderer-client-id=7";
    private const string CrashpadLine = "\"" + Exe + "\" --type=crashpad-handler \"--user-data-dir=C:\\Users\\caio\\AppData\\Local\\Google\\Chrome\\User Data\"";

    [Theory]
    [InlineData(MainLine, false, true)]
    [InlineData(RendererLine, true, false)]
    [InlineData(CrashpadLine, true, false)]
    [InlineData(null, false, true)]     // ilegível e pai não é o navegador: principal
    [InlineData("", true, false)]       // ilegível e filho do navegador: auxiliar
    public void IsMainProcess(string? cmd, bool parentIsSame, bool expected) =>
        Assert.Equal(expected, BrowserProcessFilter.IsMainProcess(cmd, parentIsSame));

    [Fact]
    public void SelectMain_so_principais_da_sessao_atual_com_identidade()
    {
        var rows = new[]
        {
            Row(100, 4, MainLine),
            Row(101, 100, RendererLine),
            Row(102, 100, CrashpadLine),
            Row(103, 100, null),                      // auxiliar ilegível: pai é o Chrome
            Row(200, 4, MainLine, session: 2),        // outro usuário logado
            Row(300, 4, "\"C:\\e\\msedge.exe\"", name: "MSEDGE.EXE", date: "lixo"),
            Row(400, 4, "firefox.exe", name: "firefox.exe"),
        };

        var main = BrowserProcessFilter.SelectMain(rows, sessionId: 1, bootId: "17");

        Assert.Equal(new[] { 100, 300 }, main.Select(m => m.Pid));
        var chrome = main[0];
        Assert.Equal("chrome.exe", chrome.Kind);
        Assert.Equal("Google Chrome", chrome.DisplayName);
        Assert.Equal(Exe, chrome.ExecutablePath);
        Assert.Equal(new ProcessIdentity(new DateTimeOffset(2026, 10, 11, 9, 12, 3, TimeSpan.FromHours(-3)).AddTicks(1234560), "17"), chrome.Identity);
        Assert.Equal(new[] { "--profile-directory=Profile 1" }, ChromiumFlags.PreservedArgs(chrome.CommandLine));

        var edge = main[1];
        Assert.Equal("msedge.exe", edge.Kind);   // nome canônico, mesmo vindo em caixa alta
        Assert.Null(edge.Identity);              // data ilegível ⇒ sem identidade ⇒ relançamento bloqueado
    }

    [Theory]
    [InlineData("20261011091203.123456+000", 2026, 10, 11, 9, 12, 3, 0)]
    [InlineData("20261011091203.000000-180", 2026, 10, 11, 9, 12, 3, -180)]
    public void ParseWmiDate_aceita_DMTF(string dmtf, int y, int mo, int d, int h, int mi, int s, int offset)
    {
        var parsed = BrowserProcessFilter.ParseWmiDate(dmtf);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offset)), parsed.Value.AddTicks(-(parsed.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(TimeSpan.FromMinutes(offset), parsed.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026101109120X.123456-180")]
    public void ParseWmiDate_recusa_lixo(string? dmtf) => Assert.Null(BrowserProcessFilter.ParseWmiDate(dmtf));

    [Fact]
    public void Consulta_WQL_cobre_os_tres_navegadores()
    {
        var q = BrowserProcessFilter.WqlQuery();
        Assert.Contains("FROM Win32_Process", q);
        Assert.Contains("Name = 'chrome.exe'", q);
        Assert.Contains("Name = 'msedge.exe'", q);
        Assert.Contains("Name = 'brave.exe'", q);
        Assert.Contains("CommandLine", q);
        Assert.Contains("CreationDate", q);
    }

    [Theory]
    [InlineData("Chrome.exe", "chrome.exe")]
    [InlineData("brave.exe", "brave.exe")]
    [InlineData("opera.exe", null)]
    [InlineData(null, null)]
    public void CanonicalKind(string? name, string? expected) => Assert.Equal(expected, BrowserProcessFilter.CanonicalKind(name));
}
