using Insomnia.Core.Abstractions;
using Microsoft.Win32;

namespace Insomnia.Windows.Startup;

/// IAutoStart real: valor `Insomnia` em HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// (substitui o SMAppService do Mac; não pede aprovação do sistema nem administrador).
/// IsRegistered nunca lança (falha de leitura = não registrado); Register/Unregister lançam
/// se o registro recusar. Unregister é idempotente.
public sealed class AutoStartWin : IAutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Insomnia";

    public bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
        catch (Exception) { return false; }
    }

    public void Register(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
    }

    public void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// Linha de comando gravada na chave Run: o caminho entre aspas (pode ter espaços).
    public static string CommandFor(string exePath) => $"\"{exePath.Trim().Trim('"')}\"";
}
