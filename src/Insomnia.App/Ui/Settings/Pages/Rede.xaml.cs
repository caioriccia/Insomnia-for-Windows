// Página Rede: o SSID é rascunho até Enter ou sair do campo (como no formulário antigo).
using System.Windows.Controls;
using System.Windows.Input;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings.Pages;

public partial class RedePage : UserControl
{
    public RedePage()
    {
        InitializeComponent();
    }

    private SettingsModel? Model => DataContext as SettingsModel;

    private void OnSsidKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Model?.CommitSsid();
    }

    private void OnSsidLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Model?.CommitSsid();
}
