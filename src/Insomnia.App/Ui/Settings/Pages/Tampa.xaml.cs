// Página Tampa. Os toggles vão direto por binding ao SettingsModel; a lista de congelados grava
// pelos eventos do AppListEditor. "Congelaria agora" é recalculado pelo próprio modelo ao abrir a
// página (SettingsModel.Current) e a cada mudança da lista ou do "congelar todos".
using System.Windows.Controls;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings.Pages;

public partial class TampaPage : UserControl
{
    public TampaPage()
    {
        InitializeComponent();
        FreezeEditor.RunningApps = () => Model?.RunningAppsAsync() ?? Task.FromResult<IReadOnlyList<string>>([]);
        FreezeEditor.AddRequested += exe => Model?.AddFrozen(exe);
        FreezeEditor.RemoveRequested += exe => Model?.RemoveFrozen(exe);
    }

    private SettingsModel? Model => DataContext as SettingsModel;
}
