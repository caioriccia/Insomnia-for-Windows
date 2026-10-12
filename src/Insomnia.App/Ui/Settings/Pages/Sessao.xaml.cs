// Página Sessão. Os presets do SettingsModel são tuplas (campos, que o binding do WPF não lê):
// aqui viram PresetItem, com propriedades, e são refeitos a cada PropertyChanged(Presets).
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Insomnia.App.Ui.Model;

namespace Insomnia.App.Ui.Settings.Pages;

/// Um preset para os chips e o ComboBox. CanRemove = false no último (a lista nunca fica vazia).
public sealed record PresetItem(int Seconds, string Chip, bool IsDefault, bool CanRemove);

public partial class SessaoPage : UserControl
{
    private SettingsModel? _model;
    private bool _syncing;

    public SessaoPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as SettingsModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as SettingsModel);
    }

    private void Attach(SettingsModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        RebuildPresets();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsModel.Presets)) RebuildPresets();
    }

    private void RebuildPresets()
    {
        var presets = _model?.Presets ?? [];
        var items = presets.Select(p => new PresetItem(p.Seconds, p.Chip, p.IsDefault, presets.Count > 1)).ToList();
        _syncing = true;
        try
        {
            PresetChips.ItemsSource = items;
            DefaultPreset.ItemsSource = items;
            DefaultPreset.SelectedItem = items.FirstOrDefault(i => i.IsDefault);
        }
        finally { _syncing = false; }
    }

    private void OnRemovePreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int seconds }) _model?.RemovePreset(seconds);
    }

    private void OnAddPreset(object sender, RoutedEventArgs e) => _model?.AddPreset();

    private void OnNewPresetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _model?.AddPreset();
    }

    private void OnDefaultChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || DefaultPreset.SelectedItem is not PresetItem item) return;
        _model?.SetDefault(item.Seconds);
    }
}
