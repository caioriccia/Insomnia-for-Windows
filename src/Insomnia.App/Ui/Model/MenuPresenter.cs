// Menu da bandeja da UI nova (desenho-ui §3.6/§5.4): traduz as linhas do StatusMenuBuilder em
// linhas com glifo para a MenuFlyoutWindow. Puro: sem System.Windows nem WinForms. Os textos
// continuam vindo do StatusMenuBuilder; aqui só se decide o papel e o glifo de cada linha.
using Insomnia.App.Tray;
using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Ui.Model;

public enum MenuRowKind { Header, Status, Notice, Command, Separator }

public sealed record MenuRow(MenuRowKind Kind, string? Glyph, string Text, MenuCommand Command = MenuCommand.None, string? Argument = null, bool Destructive = false);

public static class MenuPresenter
{
    /// Info ⇒ Header (só a primeira) / Status; Warning ⇒ Notice com Glyphs.Warning (o "⚠ " do
    /// texto sai, o glifo já diz); Action ⇒ Command com glifo pelo comando; Quit ⇒ Destructive.
    /// Separadores preservados, na mesma posição.
    public static IReadOnlyList<MenuRow> From(IReadOnlyList<MenuLine> lines)
    {
        var rows = new List<MenuRow>(lines.Count);
        var headerDone = false;
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case MenuLineKind.Info:
                    rows.Add(new(headerDone ? MenuRowKind.Status : MenuRowKind.Header, null, line.Text));
                    headerDone = true;
                    break;
                case MenuLineKind.Warning:
                    rows.Add(new(MenuRowKind.Notice, Glyphs.Warning, StripWarningMark(line.Text)));
                    break;
                case MenuLineKind.Action:
                    rows.Add(new(MenuRowKind.Command, GlyphFor(line.Command), line.Text, line.Command, line.Argument,
                        Destructive: line.Command == MenuCommand.Quit));
                    break;
                case MenuLineKind.Separator:
                    rows.Add(new(MenuRowKind.Separator, null, ""));
                    break;
            }
        }
        return rows;
    }

    /// Glifo de cada comando (desenho-ui §3.1); None ⇒ sem glifo.
    public static string? GlyphFor(MenuCommand command) => command switch
    {
        MenuCommand.OpenPanel => Glyphs.OpenPane,
        MenuCommand.StartDefault => Glyphs.Play,
        MenuCommand.Settings => Glyphs.Settings,
        MenuCommand.Quit => Glyphs.Power,
        MenuCommand.Relaunch => Glyphs.Refresh,
        MenuCommand.StopWaitingAudio => Glyphs.Dismiss,
        _ => null,
    };

    /// "⚠ Backstop não armado" ⇒ "Backstop não armado".
    private static string StripWarningMark(string text) =>
        text.StartsWith('⚠') ? text[1..].TrimStart() : text;
}
