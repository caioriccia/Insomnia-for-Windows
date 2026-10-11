using System.Globalization;
using System.Text;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Store;

// Dentro do namespace: o namespace Insomnia.Core.Session (frente 'sessao') venceria o record.
using Insomnia.Core.Model;

/// IStore em disco (desenho §3/§4). Leitura nunca altera nada: um arquivo ilegível fica
/// exatamente onde está (R5). Escritas são atômicas (AtomicFile).
public sealed class FileStore(Paths paths, IClock clock, ILog? log = null) : IStore
{
    public Paths Paths { get; } = paths;

    // ---------- sessão ----------

    public Session? LoadSession()
    {
        var text = Read(Paths.SessionFile);
        return text is null ? null : Parse(Paths.SessionFile, text, JsonCodec.ParseSession);
    }

    public void SaveSession(Session session) => Write(Paths.SessionFile, JsonCodec.SerializeSession(session));

    public void DeleteSession()
    {
        try
        {
            if (File.Exists(Paths.SessionFile)) File.Delete(Paths.SessionFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StoreWriteException(Paths.SessionFile, ex);
        }
    }

    /// Renomeia session.json para session.json.unreadable-&lt;yyyyMMddTHHmmssZ&gt;[-n] e devolve o
    /// caminho novo. Nunca sobrescreve: nome ocupado ganha -1, -2, …, e o próprio rename falha
    /// (em vez de substituir) se um arquivo aparecer no meio. null se não havia session.json.
    public string? MoveAsideUnreadableSession()
    {
        var source = Paths.SessionFile;
        if (!File.Exists(source)) return null;
        var stamp = clock.Now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var baseName = Paths.UnreadableSessionPrefix + stamp;
        var dir = Path.GetDirectoryName(source)!;
        for (var n = 0; n < 10_000; n++)
        {
            var target = Path.Combine(dir, n == 0 ? baseName : $"{baseName}-{n}");
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                File.Move(source, target, overwrite: false);
                return target;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) when (File.Exists(target)) { /* apareceu no meio: tenta o próximo nome */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StoreWriteException(source, ex);
            }
        }
        throw new StoreWriteException(source, new IOException("nenhum nome livre para mover a sessão ilegível"));
    }

    // ---------- diário ----------

    public Journal LoadJournal()
    {
        var text = Read(Paths.JournalFile);
        return text is null ? Journal.Clean : Parse(Paths.JournalFile, text, JsonCodec.ParseJournal);
    }

    public void SaveJournal(Journal journal) => Write(Paths.JournalFile, JsonCodec.SerializeJournal(journal));

    // ---------- config ----------

    /// Ausente ⇒ Config.Default, gravado na hora (falha ao gravar só vai para o log).
    public Config LoadConfig()
    {
        var text = Read(Paths.ConfigFile);
        if (text is not null) return Parse(Paths.ConfigFile, text, JsonCodec.ParseConfig);
        var config = Config.Default;
        try { SaveConfig(config); }
        catch (StoreWriteException ex) { log?.Error($"config.json: não deu para gravar o padrão: {ex.Message}"); }
        return config;
    }

    public void SaveConfig(Config config) => Write(Paths.ConfigFile, JsonCodec.SerializeConfig(config));

    // ---------- comuns ----------

    private static string? Read(string path)
    {
        try { return AtomicFile.ReadAllTextOrNull(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new StoreCorruptException(path, $"não deu para ler: {ex.Message}");
        }
    }

    private static T Parse<T>(string path, string text, Func<string, T> parse)
    {
        try { return parse(text); }
        catch (FormatException ex) { throw new StoreCorruptException(path, ex.Message); }
    }

    private static void Write(string path, string content)
    {
        try { AtomicFile.WriteAllText(path, content); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new StoreWriteException(path, ex);
        }
    }
}
