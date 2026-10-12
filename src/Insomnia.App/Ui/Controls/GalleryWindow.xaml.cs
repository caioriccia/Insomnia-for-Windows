// Galeria de controles (§5.6): janela de conferência visual, não ligada ao app. Para abrir, de
// um ponto de depuração qualquer no thread de UI: new GalleryWindow().Show().
//
// ThemeMode da janela (WPF 10, ainda experimental: WPF0001) troca entre o tema do sistema, claro
// e escuro sem mexer no resto do app; os tokens Insomnia.* acompanham por DynamicResource.
using System.Windows;

namespace Insomnia.App.Ui.Controls;

public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
#pragma warning disable WPF0001 // ThemeMode é experimental no WPF 10 (desenho-ui §6.1)
        ThemeMode = sender == ThemeLight ? ThemeMode.Light
            : sender == ThemeDark ? ThemeMode.Dark
            : ThemeMode.System;
#pragma warning restore WPF0001
    }
}
