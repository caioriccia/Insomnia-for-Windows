// Página Bateria: as opções dos dois ComboBox (passo Config.FloorStep). O valor vai e volta por
// binding (SelectedValue ⇄ LowPowerFloor/EndFloor); subir um piso pode mover o outro, e o
// SettingsModel avisa os dois.
using System.Windows.Controls;
using Insomnia.Core.Model;

namespace Insomnia.App.Ui.Settings.Pages;

/// Uma opção de piso: 0 aparece como "Nunca".
public sealed record FloorOption(int Value, string Text)
{
    public const string Never = "Nunca";

    public static IReadOnlyList<FloorOption> Range(int max) =>
        [.. Enumerable.Range(0, max / Config.FloorStep + 1).Select(i => i * Config.FloorStep)
            .Select(v => new FloorOption(v, v == 0 ? Never : $"{v} %"))];
}

public partial class BateriaPage : UserControl
{
    public BateriaPage()
    {
        InitializeComponent();
        LowFloor.ItemsSource = FloorOption.Range(100);
        EndFloor.ItemsSource = FloorOption.Range(Config.MaxEndFloor);
    }
}
