namespace Insomnia.Windows.Power;

/// GUIDs de energia do §7 do desenho, num lugar só (o diagnóstico e os testes leem daqui).
public static class PowerSettingIds
{
    /// SUB_BUTTONS: subgrupo "Botões de energia e tampa".
    public static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    /// LIDACTION: 0 = nada, 1 = suspender, 2 = hibernar, 3 = desligar.
    public static readonly Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    /// SUB_ENERGYSAVER: subgrupo "Economia de energia".
    public static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    /// ESBATTTHRESHOLD: liga a Economia abaixo deste percentual; 100 = sempre ligada na bateria.
    public static readonly Guid EnergySaverBatteryThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

    /// GUID_LIDSWITCH_STATE_CHANGE: Data 0 = fechada, 1 = aberta.
    public static readonly Guid LidSwitchStateChange = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    /// GUID_ACDC_POWER_SOURCE: Data 0 = AC, 1 = bateria (DC), 2 = UPS de curta duração.
    public static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    /// GUID_BATTERY_PERCENTAGE_REMAINING: Data = percentual.
    public static readonly Guid BatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");

    /// Os três assinados pela PowerEventsWindow.
    public static IReadOnlyList<Guid> Notifications { get; } = [LidSwitchStateChange, AcDcPowerSource, BatteryPercentageRemaining];
}
