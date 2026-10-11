using System.Globalization;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Processes;

/// Regras puras de identidade de processo (R8: sinal só com identidade exata).
///
/// Identidade = (instante de criação, BootId). O pid sozinho não basta: o Windows reusa pids,
/// e entre um boot e outro o mesmo (pid, criação) pode reaparecer por acaso.
///
/// O instante é TRUNCADO PARA SEGUNDOS INTEIROS, em UTC. Motivo: o diário grava datas como
/// `yyyy-MM-ddTHH:mm:ssZ` (§3), sem fração. Se a identidade levasse os 100 ns do FILETIME, a
/// entrada relida do disco nunca seria igual (==) à identidade fresca do Lookup, e o Core
/// trataria todo processo congelado como "gone" — sem nunca retomá-lo.
public static class ProcessIdentityRules
{
    /// Identidade a partir de um FILETIME de criação (GetProcessTimes). BootId vazio ou
    /// FILETIME inválido ⇒ null (processo não sinalizável).
    public static ProcessIdentity? FromFileTime(long creationFileTimeUtc, string bootId)
    {
        if (string.IsNullOrEmpty(bootId) || creationFileTimeUtc <= 0) return null;
        DateTime utc;
        try { utc = DateTime.FromFileTimeUtc(creationFileTimeUtc); }
        catch (ArgumentOutOfRangeException) { return null; }
        return new ProcessIdentity(Normalize(new DateTimeOffset(utc, TimeSpan.Zero)), bootId);
    }

    /// Trunca para o segundo inteiro, em UTC (a mesma precisão do diário).
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    /// BootId do registro (`...\Memory Management\PrefetchParameters\BootId`, REG_DWORD).
    /// Aceita o que o RegistryKey.GetValue devolve (int para DWORD; long para QWORD; string
    /// de dígitos; 4 bytes crus). Qualquer outra coisa ⇒ "" (identidades ficam nulas).
    public static string ParseBootId(object? value) => value switch
    {
        int i => ((uint)i).ToString(CultureInfo.InvariantCulture),
        long l when l >= 0 => l.ToString(CultureInfo.InvariantCulture),
        string s when ulong.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n.ToString(CultureInfo.InvariantCulture),
        byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0).ToString(CultureInfo.InvariantCulture),
        _ => "",
    };
}
