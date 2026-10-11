using System.Diagnostics;
using System.Management;
using Insomnia.Core.Abstractions;
using Microsoft.Win32;

namespace Insomnia.Windows.Browser;

/// IBrowserProcesses real (desenho §7, linha "ps -o args="): WMI Win32_Process para ler
/// CommandLine, ExecutablePath e hora de criação dos navegadores da sessão atual; processo
/// principal = sem "--type=". Identidade = CreationDate + BootId.
///
/// WMI demora (~1 s na primeira consulta): nunca chamar no thread da UI (desenho §10.6).
/// Construtor público: BrowserProcessesWin(ILog log). Nada toca o sistema no construtor.
public sealed class BrowserProcessesWin : IBrowserProcesses
{
    private const string BootIdKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private static readonly TimeSpan QuitPoll = TimeSpan.FromMilliseconds(250);

    private readonly ILog _log;
    private readonly Lazy<string> _bootId;
    private readonly int _sessionId;

    public BrowserProcessesWin(ILog log)
    {
        _log = log;
        _bootId = new Lazy<string>(ReadBootId);
        using var self = Process.GetCurrentProcess();
        _sessionId = self.SessionId;
    }

    public IReadOnlyList<BrowserInstance> Scan()
    {
        var rows = new List<BrowserProcessRow>();
        using (var searcher = new ManagementObjectSearcher(BrowserProcessFilter.WqlQuery()))
        using (var results = searcher.Get())
        {
            foreach (var o in results)
            {
                using (o) rows.Add(BrowserProcessFilter.FromWmi(o));
            }
        }
        return BrowserProcessFilter.SelectMain(rows, _sessionId, _bootId.Value);
    }

    /// Fecha com graça: CloseMainWindow em cada janela principal (uma vez por janela; o Chromium
    /// troca a principal quando a anterior fecha), até não sobrar processo do navegador na sessão
    /// ou estourar o teto. true = nenhum restou. Nunca mata processo.
    public async Task<bool> QuitAllAsync(string kind, TimeSpan timeout, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(kind);
        var closed = new HashSet<nint>();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = 0;
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId != _sessionId || p.HasExited) continue;
                        remaining++;
                        p.Refresh();
                        var handle = p.MainWindowHandle;
                        if (handle != 0 && closed.Add(handle)) p.CloseMainWindow();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // Processo saiu entre a lista e a leitura: conta como fechado.
                    }
                }
            }
            if (remaining == 0) return true;
            if (clock.Elapsed >= timeout)
            {
                _log.Info($"navegadores: {kind} ainda com {remaining} processo(s) depois de {(int)timeout.TotalSeconds} s");
                return false;
            }
            await Task.Delay(QuitPoll, ct).ConfigureAwait(false);
        }
    }

    public void Launch(string executablePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
        };
        // ArgumentList faz as aspas do Windows: valor com espaço (perfil) chega inteiro.
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("o Windows não iniciou o processo");
    }

    private string ReadBootId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BootIdKey);
            return key?.GetValue("BootId")?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            _log.Error($"navegadores: BootId ilegível: {ex.Message}");
            return "";
        }
    }
}
