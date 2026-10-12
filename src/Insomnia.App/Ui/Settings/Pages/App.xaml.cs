// Página App: tudo por binding ao SettingsModel (o toggle volta sozinho se o HKCU Run recusar,
// e LaunchError explica).
using System.Windows.Controls;

namespace Insomnia.App.Ui.Settings.Pages;

public partial class AppPage : UserControl
{
    public AppPage()
    {
        InitializeComponent();
    }
}
