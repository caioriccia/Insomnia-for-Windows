// Conversores das páginas de Configurações. Puros: só traduzem valor do SettingsModel em texto ou
// visibilidade, sem tocar no modelo.
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Insomnia.App.Ui.Settings;

/// true ⇒ "Ativado"; senão "Desativado" (o rótulo à esquerda do toggle, como no Windows 11).
public sealed class OnOffTextConverter : IValueConverter
{
    public const string On = "Ativado";
    public const string Off = "Desativado";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? On : Off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// Texto vazio (ou null) ⇒ Collapsed; senão Visible. Para mensagens que só existem às vezes
/// (erro de preset, falha do "Iniciar com o Windows", "Congelaria agora").
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
