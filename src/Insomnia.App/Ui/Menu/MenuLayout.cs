// Contas do menu da bandeja (desenho-ui §3.6) que não precisam de janela: onde ela abre, se um
// retrato novo só troca textos (atualiza no lugar, sem perder o hover) e que duração o
// "Iniciar sessão de …" pede. Puro: sem System.Windows nem WinForms, testado por unidade.
using System.Globalization;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;

namespace Insomnia.App.Ui.Menu;

public static class MenuLayout
{
    /// Largura do menu em epx (§3.6).
    public const double Width = 288;

    /// Folga entre o cursor e a borda de baixo, em epx: zero, como o menu nativo da bandeja.
    public const double CursorGap = 0;

    /// Retângulo físico do menu: tamanho em epx × escala do monitor do cursor, canto
    /// inferior-direito no cursor, preso ao workArea (UiPlacement.NearCursor).
    public static PxRect Place(PxRect workArea, double heightEpx, double scale, double cursorX, double cursorY)
    {
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        return UiPlacement.NearCursor(workArea, Width * scale, heightEpx * scale, cursorX, cursorY, CursorGap * scale);
    }

    /// Mesma sequência de linhas, só com textos diferentes (a contagem do cabeçalho virou o
    /// segundo, o status mudou)? Então a janela troca os textos no lugar em vez de recriar os
    /// itens — o item sob o mouse continua realçado e o foco do teclado não se perde.
    public static bool SameShape(IReadOnlyList<MenuRow> current, IReadOnlyList<MenuRow> next)
    {
        if (current.Count != next.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a.Kind != b.Kind || a.Glyph != b.Glyph || a.Command != b.Command
                || a.Argument != b.Argument || a.Destructive != b.Destructive)
                return false;
        }
        return true;
    }

    /// Duração do "Iniciar sessão de …": o Argument do StatusMenuBuilder (segundos, invariante);
    /// ausente, inválido ou não positivo ⇒ o preset padrão do config.
    public static TimeSpan StartDuration(string? argument, int defaultPresetSeconds)
    {
        if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(defaultPresetSeconds);
    }
}
