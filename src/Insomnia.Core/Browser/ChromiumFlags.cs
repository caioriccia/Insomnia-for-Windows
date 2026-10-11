using System.Collections.Immutable;
using System.Text;

namespace Insomnia.Core.Browser;

/// Navegadores Chromium limitam (throttle) janelas encobertas a menos que tenham sido abertos com
/// as duas flags abaixo (desenho §7.3). Aqui fica o puro: tokenizar a linha de comando, dizer se
/// as duas flags estão lá e o que precisa ser levado no relançamento.
public static class ChromiumFlags
{
    public const string Occluded = "--disable-backgrounding-occluded-windows";
    public const string Renderer = "--disable-renderer-backgrounding";
    public static readonly ImmutableArray<string> Required = [Occluded, Renderer];

    /// Argumentos levados para o relançamento, para abrir o mesmo perfil.
    public static readonly ImmutableArray<string> PreservedPrefixes = ["--user-data-dir=", "--profile-directory="];

    /// Kind (nome do executável, como no resto do app) e nome mostrado ao usuário.
    public static readonly ImmutableArray<(string Kind, string DisplayName)> KnownBrowsers =
    [
        ("chrome.exe", "Google Chrome"),
        ("msedge.exe", "Microsoft Edge"),
        ("brave.exe", "Brave"),
    ];

    /// Nome para o usuário; kind desconhecido volta como veio.
    public static string DisplayNameFor(string kind)
    {
        foreach (var (k, name) in KnownBrowsers)
            if (string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) return name;
        return kind;
    }

    public static bool IsKnownKind(string kind) =>
        KnownBrowsers.Any(b => string.Equals(b.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// Quebra uma linha de comando do Windows (Win32_Process.CommandLine) em argumentos, com as
    /// regras do CommandLineToArgvW, que é como o próprio Chromium lê a linha no Windows:
    /// - espaço/tab fora de aspas separa argumentos;
    /// - aspas duplas abrem e fecham trecho literal (e não entram no argumento);
    /// - barra invertida só é especial ANTES de aspas: 2n barras + aspas = n barras e a aspa
    ///   delimita; 2n+1 barras + aspas = n barras e uma aspa literal; fora disso é literal
    ///   (caminhos como C:\Users\x continuam intactos).
    /// Aspas simples são literais no Windows. Flag escondida dentro de valor entre aspas não vira
    /// argumento próprio.
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var output = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return output;
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                    i++;
                }
                else
                {
                    current.Append('\\', n);
                }
                hasToken = true;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t' || c == '\r' || c == '\n'))
            {
                if (hasToken)
                {
                    output.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
            i++;
        }
        if (hasToken) output.Add(current.ToString());
        return output;
    }

    /// Nome do switch: o que vem antes do primeiro '=' ("--flag=valor" ⇒ "--flag").
    public static string SwitchName(string token)
    {
        var eq = token.IndexOf('=');
        return eq < 0 ? token : token[..eq];
    }

    /// As duas flags estão presentes como argumentos próprios ("--flag" ou "--flag=valor").
    /// O Chromium no Windows não diferencia caixa em nome de switch.
    public static bool HasBothFlags(string commandLine)
    {
        var names = new HashSet<string>(Tokenize(commandLine).Select(SwitchName), StringComparer.OrdinalIgnoreCase);
        return Required.All(names.Contains);
    }

    /// Argumentos de perfil a levar no relançamento (o primeiro token, o executável, fica de fora).
    public static IReadOnlyList<string> PreservedArgs(string commandLine) =>
        [.. Tokenize(commandLine).Skip(1).Where(t => PreservedPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))];

    /// Processo auxiliar do Chromium (renderer, gpu, utility, crashpad...): tem "--type=".
    public static bool HasTypeSwitch(string commandLine) =>
        Tokenize(commandLine).Skip(1).Any(t => string.Equals(SwitchName(t), "--type", StringComparison.OrdinalIgnoreCase));
}
