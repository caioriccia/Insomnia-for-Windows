using System.Globalization;
using System.Text;
using Insomnia.Core.Abstractions;

namespace Insomnia.Core.Store;

/// ILog em texto puro, compartilhado entre o app e o backstop (um arquivo conta a história
/// inteira). Linha: "2026-10-11T18:00:00-03:00 [info] insomnia: mensagem".
///
/// Teto de 1 MiB: antes de acrescentar, se o arquivo passou do teto, ele vira "&lt;nome&gt;.1"
/// (substituindo o .1 anterior) e a linha começa um arquivo novo. Cada linha abre e fecha o
/// arquivo com FileShare.ReadWrite|Delete, para dois processos escreverem e girarem sem se
/// travar. Log nunca lança: falha de disco não pode derrubar quem está logando.
public sealed class FileLog : ILog
{
    public const long DefaultMaxBytes = 1L << 20;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    public FileLog(string path, long maxBytes = DefaultMaxBytes, Func<DateTimeOffset>? now = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        MaxBytes = maxBytes;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Path { get; }
    public string RotatedPath => Path + ".1";
    public long MaxBytes { get; }

    public void Info(string message) => Append("info", message);
    public void Error(string message) => Append("error", message);

    private void Append(string level, string message)
    {
        var stamp = _now().ToString("yyyy-MM-dd'T'HH':'mm':'sszzz", CultureInfo.InvariantCulture);
        var line = $"{stamp} [{level}] insomnia: {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                RotateIfNeeded();
                using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Utf8NoBom.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sem onde registrar: engolido de propósito.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            File.Move(Path, RotatedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Outro processo girou primeiro ou segura o arquivo: segue acrescentando, gira na próxima.
        }
    }
}
