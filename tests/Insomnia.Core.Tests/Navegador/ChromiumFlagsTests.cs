using Insomnia.Core.Browser;

namespace Insomnia.Core.Tests.Navegador;

public class ChromiumFlagsTests
{
    private const string ChromeExe = "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"";

    [Fact]
    public void Tokenize_separa_por_espaco_e_respeita_aspas_duplas()
    {
        Assert.Equal(
            new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", "--a", "--user-data-dir=C:\\Meu Perfil", "x" },
            ChromiumFlags.Tokenize(ChromeExe + "  --a \t--user-data-dir=\"C:\\Meu Perfil\" x"));
    }

    [Fact]
    public void Tokenize_barra_invertida_so_eh_especial_antes_de_aspas()
    {
        // Caminho comum: barras literais.
        Assert.Equal(new[] { @"C:\Users\caio\AppData" }, ChromiumFlags.Tokenize(@"C:\Users\caio\AppData"));
        // \" = aspa literal; \\" = uma barra e a aspa delimita.
        Assert.Equal(new[] { "a\"b" }, ChromiumFlags.Tokenize("a\\\"b"));
        Assert.Equal(new[] { @"C:\dir\", "next" }, ChromiumFlags.Tokenize("\"C:\\dir\\\\\" next"));
        // Barras no fim de token sem aspas ficam.
        Assert.Equal(new[] { @"x\\", "y" }, ChromiumFlags.Tokenize(@"x\\ y"));
    }

    [Fact]
    public void Tokenize_aspas_simples_sao_literais_no_Windows()
    {
        // Usuário com apóstrofo no nome não pode quebrar o caminho do perfil.
        Assert.Equal(
            new[] { "chrome.exe", @"--user-data-dir=C:\Users\O'Neil\Perfil", "--x" },
            ChromiumFlags.Tokenize(@"chrome.exe --user-data-dir=C:\Users\O'Neil\Perfil --x"));
    }

    [Fact]
    public void Tokenize_vazio_e_argumento_vazio_entre_aspas()
    {
        Assert.Empty(ChromiumFlags.Tokenize(""));
        Assert.Empty(ChromiumFlags.Tokenize("   "));
        Assert.Equal(new[] { "a", "", "b" }, ChromiumFlags.Tokenize("a \"\" b"));
    }

    [Fact]
    public void HasBothFlags_exige_as_duas_como_argumentos_proprios()
    {
        Assert.True(ChromiumFlags.HasBothFlags($"{ChromeExe} --disable-backgrounding-occluded-windows --disable-renderer-backgrounding"));
        Assert.False(ChromiumFlags.HasBothFlags($"{ChromeExe} --disable-renderer-backgrounding"));
        Assert.False(ChromiumFlags.HasBothFlags(ChromeExe));
        // Forma --flag=valor conta; caixa não importa no Windows.
        Assert.True(ChromiumFlags.HasBothFlags($"{ChromeExe} --DISABLE-BACKGROUNDING-OCCLUDED-WINDOWS=1 --disable-renderer-backgrounding"));
    }

    [Fact]
    public void HasBothFlags_flag_escondida_em_valor_entre_aspas_nao_conta()
    {
        var line = $"{ChromeExe} --disable-renderer-backgrounding --app-name=\"x --disable-backgrounding-occluded-windows\"";
        Assert.False(ChromiumFlags.HasBothFlags(line));
        // Nem como prefixo de outra flag.
        Assert.False(ChromiumFlags.HasBothFlags($"{ChromeExe} --disable-renderer-backgrounding --disable-backgrounding-occluded-windows-x"));
    }

    [Fact]
    public void PreservedArgs_leva_so_o_perfil_e_nunca_o_executavel()
    {
        var line = $"{ChromeExe} --user-data-dir=\"C:\\Users\\caio\\Chrome Dev\" --profile-directory=\"Profile 2\" --flag-switches-begin --no-first-run";
        Assert.Equal(
            new[] { @"--user-data-dir=C:\Users\caio\Chrome Dev", "--profile-directory=Profile 2" },
            ChromiumFlags.PreservedArgs(line));
        Assert.Empty(ChromiumFlags.PreservedArgs("--user-data-dir=C:\\x"));   // primeiro token é o executável
        Assert.Empty(ChromiumFlags.PreservedArgs(ChromeExe));
    }

    [Theory]
    [InlineData("chrome.exe --type=renderer --lang=pt-BR", true)]
    [InlineData("chrome.exe --type=gpu-process", true)]
    [InlineData("chrome.exe --profile-directory=Default", false)]
    [InlineData("chrome.exe --app-name=\"--type=renderer\"", false)]
    public void HasTypeSwitch_identifica_processo_auxiliar(string line, bool expected) =>
        Assert.Equal(expected, ChromiumFlags.HasTypeSwitch(line));

    [Fact]
    public void Catalogo_de_navegadores()
    {
        Assert.Equal("Google Chrome", ChromiumFlags.DisplayNameFor("chrome.exe"));
        Assert.Equal("Microsoft Edge", ChromiumFlags.DisplayNameFor("MSEDGE.EXE"));
        Assert.Equal("Brave", ChromiumFlags.DisplayNameFor("brave.exe"));
        Assert.Equal("opera.exe", ChromiumFlags.DisplayNameFor("opera.exe"));
        Assert.True(ChromiumFlags.IsKnownKind("Chrome.exe"));
        Assert.False(ChromiumFlags.IsKnownKind("firefox.exe"));
    }
}
