// Página Agentes: a lista "nunca congelar", gravada pelos eventos do AppListEditor.
using System.Windows.Controls;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings.Pages;

public partial class AgentesPage : UserControl
{
    public AgentesPage()
    {
        InitializeComponent();
        AgentEditor.RunningApps = () => Model?.RunningAppsAsync() ?? Task.FromResult<IReadOnlyList<string>>([]);
        AgentEditor.AddRequested += exe => Model?.AddAgent(exe);
        AgentEditor.RemoveRequested += exe => Model?.RemoveAgent(exe);
    }

    private SettingsModel? Model => DataContext as SettingsModel;
}
