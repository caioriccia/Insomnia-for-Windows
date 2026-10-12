// Força o visual de hover/pressionado num controle parado, para a GalleryWindow mostrar os
// estados lado a lado sem depender do mouse. Os templates de Controls.xaml reagem a
// ControlPreview.State com os mesmos setters de IsMouseOver/IsPressed. No app de verdade
// ninguém liga esta propriedade: o padrão None não muda nada.
using System.Windows;

namespace Insomnia.App.Ui.Controls;

public enum PreviewState { None, Hover, Pressed }

public static class ControlPreview
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PreviewState), typeof(ControlPreview), new FrameworkPropertyMetadata(PreviewState.None));

    public static PreviewState GetState(DependencyObject element) => (PreviewState)element.GetValue(StateProperty);

    public static void SetState(DependencyObject element, PreviewState value) => element.SetValue(StateProperty, value);
}
