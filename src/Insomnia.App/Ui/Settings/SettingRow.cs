// Cartão de configuração das páginas (desenho-ui §3.7): glifo 20 à esquerda, título Body +
// descrição Caption, controle à direita (o Content) e, opcionalmente, um corpo de largura total
// embaixo (Body: chips, listas de apps). O visual do cartão é o estilo congelado
// Insomnia.SettingCard; o template que monta as colunas mora em SettingsStyles.xaml.
using System.Windows;
using System.Windows.Controls;

namespace Insomnia.App.Ui.Settings;

public class SettingRow : ContentControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    /// Um char de Glyphs (Segoe Fluent Icons); vazio esconde a coluna.
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// Caption sob o título; vazio esconde a linha.
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// Conteúdo de largura total sob a linha de cima; null esconde.
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
}
