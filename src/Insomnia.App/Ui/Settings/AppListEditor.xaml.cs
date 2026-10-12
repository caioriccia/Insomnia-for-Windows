// Editor de lista de executáveis (desenho-ui §3.7): substitui o NameListEditor do WinForms.
// Identificador = nome do executável sem caixa (SettingsModel.NormalizeExe); a gravação é do
// SettingsModel, chamada pela página via AddRequested/RemoveRequested.
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings;

public partial class AppListEditor : UserControl
{
    public const string NoRunningApps = "Nenhum outro app com janela aberta";
    public const string LoadingRunningApps = "Procurando apps…";

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(AppListEditor),
            new PropertyMetadata(null, (d, e) => ((AppListEditor)d).Chips.ItemsSource = (IEnumerable?)e.NewValue));

    private int _loadTicket;

    public AppListEditor()
    {
        InitializeComponent();
    }

    /// Os nomes já na lista (SettingsModel.FreezeList / AgentList).
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// Lê os apps com janela aberta (SettingsModel.RunningAppsAsync).
    public Func<Task<IReadOnlyList<string>>>? RunningApps { get; set; }

    public event Action<string>? AddRequested;
    public event Action<string>? RemoveRequested;

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) RemoveRequested?.Invoke(name);
    }

    /// Preenche na hora de abrir (a lista de processos muda o tempo todo); só a leitura mais
    /// recente escreve. Os que já estão na lista ficam de fora.
    private async void OnRunningOpened(object? sender, EventArgs e)
    {
        var ticket = ++_loadTicket;
        Running.Items.Clear();
        Running.Items.Add(new ComboBoxItem { Content = LoadingRunningApps, IsEnabled = false });
        IReadOnlyList<string> apps;
        try { apps = RunningApps is { } read ? await read() : []; }
        catch { apps = []; }
        if (ticket != _loadTicket) return;

        var present = new HashSet<string>(ItemsSource?.OfType<string>() ?? [], StringComparer.OrdinalIgnoreCase);
        Running.Items.Clear();
        foreach (var app in apps.Where(a => !present.Contains(a))) Running.Items.Add(app);
        if (Running.Items.Count == 0) Running.Items.Add(new ComboBoxItem { Content = NoRunningApps, IsEnabled = false });
    }

    private void OnRunningPicked(object sender, SelectionChangedEventArgs e)
    {
        if (Running.SelectedItem is not string name) return;
        AddRequested?.Invoke(name);
        // Volta ao texto de convite depois que a seleção terminar de se propagar.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Running.SelectedIndex = -1);
    }

    private void OnTypedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SettingsModel.NormalizeExe(Typed.Text) is not { } name) return;
        AddRequested?.Invoke(name);
        Typed.Text = "";
    }
}
