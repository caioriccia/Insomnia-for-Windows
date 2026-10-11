using System.Text;

namespace Insomnia.Core.Store;

/// Leitura e escrita de arquivo pequeno de estado.
///
/// Escrita atômica: grava num temporário ".&lt;nome&gt;.tmp-&lt;guid&gt;" NA MESMA pasta, força para o
/// disco e troca com File.Move(overwrite: true). Quem lê vê o arquivo velho inteiro ou o novo
/// inteiro, nunca metade. Falha apaga o temporário e propaga a exceção original.
///
/// Erros de E/S de compartilhamento (antivírus, indexador, OneDrive) são tentados de novo
/// algumas vezes com 50 ms de intervalo antes de desistir.
public static class AtomicFile
{
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void WriteAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            Retry(() => File.Move(temp, full, overwrite: true));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// Conteúdo do arquivo, ou null se ele não existe. Bytes que não são UTF-8 válido lançam
    /// (DecoderFallbackException); o chamador trata como ilegível.
    public static string? ReadAllTextOrNull(string path)
    {
        string? result = null;
        var found = true;
        Retry(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
                result = reader.ReadToEnd();
            }
            catch (FileNotFoundException) { found = false; }
            catch (DirectoryNotFoundException) { found = false; }
        });
        return found ? result : null;
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) when (attempt < Attempts && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
