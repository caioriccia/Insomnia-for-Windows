using Insomnia.Core.Store;

namespace Insomnia.Core.Tests.Fundacao;

/// Pasta temporária própria do teste (fora do OneDrive, em %TEMP%), apagada no fim.
/// Paths aponta tudo para dentro dela, como INSOMNIA_HOME faz.
public sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "insomnia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = Paths.UnderHome(Root);
    }

    public string Root { get; }
    public Paths Paths { get; }

    /// Nomes dos arquivos na pasta (sem subpastas), em ordem.
    public string[] Files() => [.. Directory.GetFiles(Root).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
