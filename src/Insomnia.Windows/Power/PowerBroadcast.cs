using System.Buffers.Binary;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Power;

/// O que uma notificação PBT_POWERSETTINGCHANGE significa para o app.
/// Lid != null ⇒ evento de tampa; PowerChanged ⇒ AC/DC ou percentual mudou.
public readonly record struct PowerNotification(LidState? Lid, bool PowerChanged)
{
    public static PowerNotification None => new(null, false);
}

/// Decodificação pura de POWERBROADCAST_SETTING (Guid PowerSetting; DWORD DataLength;
/// UCHAR Data[DataLength]) — testável sem janela nem Windows.
public static class PowerBroadcast
{
    /// Tamanho do cabeçalho antes de Data: 16 (GUID) + 4 (DataLength).
    public const int HeaderSize = 20;

    /// Classifica um bloco cru. Bloco curto ou DataLength incoerente ⇒ None (nunca lança).
    public static PowerNotification Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HeaderSize) return PowerNotification.None;
        var setting = new Guid(raw[..16]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(16, 4));
        if (length > (uint)(raw.Length - HeaderSize)) return PowerNotification.None;
        return Classify(setting, raw.Slice(HeaderSize, (int)length));
    }

    public static PowerNotification Classify(Guid setting, ReadOnlySpan<byte> data)
    {
        if (setting == PowerSettingIds.LidSwitchStateChange)
            return new PowerNotification(data.Length >= 4 ? LidFromData(BinaryPrimitives.ReadUInt32LittleEndian(data)) : LidState.Unknown, false);
        if (setting == PowerSettingIds.AcDcPowerSource || setting == PowerSettingIds.BatteryPercentageRemaining)
            return new PowerNotification(null, true);
        return PowerNotification.None;
    }

    /// GUID_LIDSWITCH_STATE_CHANGE: 0 = fechada, 1 = aberta, qualquer outro = desconhecido.
    public static LidState LidFromData(uint data) => data switch
    {
        0 => LidState.Closed,
        1 => LidState.Open,
        _ => LidState.Unknown,
    };
}
