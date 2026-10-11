using System.Buffers.Binary;
using Insomnia.Core.Abstractions;
// System.Windows.Forms (using global do WinForms) também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;
using Insomnia.Windows.Power;
using Insomnia.Windows.Startup;

namespace Insomnia.Windows.Tests.Energia;

/// Conversões puras de energia: GUIDs de §7, POWERBROADCAST_SETTING, SYSTEM_POWER_STATUS
/// e a linha da chave Run. Nenhuma janela, nenhuma API real.
public class PowerParsingTests
{
    [Fact]
    public void GUIDs_sao_os_de_7()
    {
        Assert.Equal(new Guid("4f971e89-eebd-4455-a8de-9e59040e7347"), PowerSettingIds.SubButtons);
        Assert.Equal(new Guid("5ca83367-6e45-459f-a27b-476b1d01c936"), PowerSettingIds.LidAction);
        Assert.Equal(new Guid("de830923-a562-41af-a086-e3a2c6bad2da"), PowerSettingIds.SubEnergySaver);
        Assert.Equal(new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4"), PowerSettingIds.EnergySaverBatteryThreshold);
        Assert.Equal(new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3"), PowerSettingIds.LidSwitchStateChange);
        Assert.Equal(new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548"), PowerSettingIds.AcDcPowerSource);
        Assert.Equal(new Guid("a7ad8041-b45a-4cae-87a3-eecbb468a9e1"), PowerSettingIds.BatteryPercentageRemaining);
        Assert.Equal(3, PowerSettingIds.Notifications.Count);
    }

    private static byte[] Setting(Guid guid, uint data, int? declaredLength = null)
    {
        var raw = new byte[PowerBroadcast.HeaderSize + 4];
        guid.TryWriteBytes(raw.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), (uint)(declaredLength ?? 4));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(20), data);
        return raw;
    }

    [Theory]
    [InlineData(0u, LidState.Closed)]
    [InlineData(1u, LidState.Open)]
    [InlineData(7u, LidState.Unknown)]
    public void Tampa_data_0_fechada_1_aberta(uint data, LidState expected)
    {
        Assert.Equal(expected, PowerBroadcast.LidFromData(data));
        Assert.Equal(new PowerNotification(expected, false), PowerBroadcast.Decode(Setting(PowerSettingIds.LidSwitchStateChange, data)));
    }

    [Fact]
    public void AC_DC_e_percentual_disparam_PowerChanged()
    {
        Assert.Equal(new PowerNotification(null, true), PowerBroadcast.Decode(Setting(PowerSettingIds.AcDcPowerSource, 1)));
        Assert.Equal(new PowerNotification(null, true), PowerBroadcast.Decode(Setting(PowerSettingIds.BatteryPercentageRemaining, 57)));
    }

    [Fact]
    public void GUID_desconhecido_ou_bloco_malformado_e_None()
    {
        Assert.Equal(PowerNotification.None, PowerBroadcast.Decode(Setting(Guid.NewGuid(), 0)));
        Assert.Equal(PowerNotification.None, PowerBroadcast.Decode(new byte[5]));
        Assert.Equal(PowerNotification.None, PowerBroadcast.Decode(Setting(PowerSettingIds.LidSwitchStateChange, 0, declaredLength: 400)));
        // Tampa sem os 4 bytes de dado: evento de tampa, estado desconhecido.
        Assert.Equal(new PowerNotification(LidState.Unknown, false), PowerBroadcast.Decode(Setting(PowerSettingIds.LidSwitchStateChange, 0, declaredLength: 0)));
    }

    [Theory]
    [InlineData((byte)0, (byte)0, (byte)87, false, 87)]      // bateria, 87%
    [InlineData((byte)1, (byte)8, (byte)40, true, 40)]       // AC carregando
    [InlineData((byte)255, (byte)1, (byte)90, true, 90)]     // AC desconhecido ⇒ não conta como bateria
    [InlineData((byte)1, (byte)128, (byte)255, true, null)]  // desktop sem bateria
    [InlineData((byte)0, (byte)255, (byte)50, false, null)]  // flag desconhecida ⇒ ilegível
    [InlineData((byte)0, (byte)2, (byte)255, false, null)]   // percentual desconhecido
    public void SYSTEM_POWER_STATUS_vira_PowerStatus(byte ac, byte flag, byte percent, bool onAc, int? expectedPercent)
    {
        Assert.Equal(new PowerStatus(onAc, expectedPercent), PowerStatusWin.FromNative(ac, flag, percent));
    }

    [Theory]
    [InlineData(@"C:\Users\Caio\AppData\Local\Programs\Insomnia\Insomnia.exe", "\"C:\\Users\\Caio\\AppData\\Local\\Programs\\Insomnia\\Insomnia.exe\"")]
    [InlineData("\"C:\\Program Files\\Insomnia.exe\"", "\"C:\\Program Files\\Insomnia.exe\"")]
    public void Chave_Run_grava_o_caminho_entre_aspas(string exe, string expected)
    {
        Assert.Equal(expected, AutoStartWin.CommandFor(exe));
    }
}
