// Menu da bandeja (desenho-ui §3.6 e §4): FlyoutWindow ativável com Acrylic, aberta pelo clique
// direito no ícone, no monitor do cursor (MenuLayout.Place ⇒ UiPlacement.NearCursor). Some ao
// perder o foco, com Esc ou ao clicar num item (a FlyoutWindow cuida dos dois primeiros); o
// item clicado sai por CommandInvoked DEPOIS de a janela sumir, para Configurações ou o painel
// abrirem por cima sem disputar o foco.
//
// Foco: antes do Show o processo pede o primeiro plano (SetForegroundWindow) — é o truque de
// todo menu de bandeja: sem ele, o clique fora não desativa a janela e o menu fica preso na
// tela. O clique no ícone chega ao nosso processo, então o Windows permite.
//
// Thread da UI; nenhuma linha aqui roda em teste (as contas moram em MenuLayout).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.App.Ui.Theme;
using Insomnia.App.Ui.Windows;

namespace Insomnia.App.Ui.Menu;

public sealed partial class MenuFlyoutWindow : FlyoutWindow
{
    // Espaço entre o bloco de cabeçalho/status e o que vem depois (referência: 2 + 4 epx).
    private const double AfterInfoGap = 6;

    private IReadOnlyList<MenuRow> _rows = [];
    private readonly List<FrameworkElement> _elements = [];
    private (double X, double Y)? _anchor;

    public MenuFlyoutWindow() : base(Backdrop.Acrylic, activatable: true)
    {
        InitializeComponent();
    }

    /// Item clicado (já com a janela escondida). Thread da UI.
    public event Action<MenuRow>? CommandInvoked;

    /// Monta as linhas e abre com o canto inferior-direito no cursor (pixels físicos).
    public void ShowAt(IReadOnlyList<MenuRow> rows, double cursorX, double cursorY)
    {
        _anchor = (cursorX, cursorY);
        Rebuild(rows);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Place();
        SetForegroundWindow(hwnd);
        Show();
        Activate();
        Place();   // o primeiro Show pode reaplicar o tamanho da janela: confirma o lugar
        Keyboard.Focus(this);
        AnimateIn();
    }

    /// Retrato novo com o menu aberto: só textos mudaram ⇒ troca no lugar (o hover fica);
    /// mudou a forma ⇒ recria as linhas e reposiciona pelo mesmo cursor.
    public void SetRows(IReadOnlyList<MenuRow> rows)
    {
        if (MenuLayout.SameShape(_rows, rows) && _elements.Count == rows.Count)
        {
            for (var i = 0; i < rows.Count; i++) SetText(_elements[i], rows[i].Text);
            _rows = rows;
            return;
        }
        Rebuild(rows);
        if (IsVisible) Place();
    }

    // ------------------------------------------------------------------ linhas

    private void Rebuild(IReadOnlyList<MenuRow> rows)
    {
        _rows = rows;
        _elements.Clear();
        Rows.Children.Clear();
        var afterInfo = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var element = Create(row, i);
            var isInfo = row.Kind is MenuRowKind.Header or MenuRowKind.Status;
            if (afterInfo && !isInfo)
            {
                var m = element.Margin;
                var extra = row.Kind == MenuRowKind.Separator ? AfterInfoGap - m.Top : AfterInfoGap;
                element.Margin = new Thickness(m.Left, m.Top + Math.Max(0, extra), m.Right, m.Bottom);
            }
            afterInfo = isInfo;
            _elements.Add(element);
            Rows.Children.Add(element);
        }
        // Termina num bloco de texto (sem itens): o mesmo respiro embaixo.
        if (afterInfo && _elements.Count > 0)
        {
            var last = _elements[^1];
            var m = last.Margin;
            last.Margin = new Thickness(m.Left, m.Top, m.Right, m.Bottom + AfterInfoGap);
        }
    }

    private FrameworkElement Create(MenuRow row, int index)
    {
        switch (row.Kind)
        {
            case MenuRowKind.Header:
                return Styled(new TextBlock { Text = row.Text }, "Menu.Header");
            case MenuRowKind.Status:
                return Styled(new TextBlock { Text = row.Text }, "Menu.Status");
            case MenuRowKind.Notice:
                return Styled(new ContentControl { Content = row.Text, Tag = row.Glyph ?? Glyphs.Warning }, "Menu.Notice");
            case MenuRowKind.Separator:
                return Styled(new Border(), "Menu.Separator");
            default:
                var button = Styled(new Button { Content = row.Text, Tag = row.Glyph ?? "" }, "Menu.Item");
                // Lê a linha na hora do clique: SetRows pode ter trocado o texto/argumento no lugar.
                button.Click += (_, _) => Invoke(index);
                return button;
        }
    }

    private T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.Style = (Style)FindResource(style);
        return element;
    }

    private static void SetText(FrameworkElement element, string text)
    {
        switch (element)
        {
            case TextBlock block when block.Text != text: block.Text = text; break;
            case ContentControl content when !Equals(content.Content, text): content.Content = text; break;
        }
    }

    private void Invoke(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        OnDismiss();
        CommandInvoked?.Invoke(row);
    }

    // ------------------------------------------------------------------ posição e foco

    /// Mede o conteúdo em epx (288 de largura) e posiciona no monitor do cursor.
    private void Place()
    {
        if (_anchor is not { } anchor) return;
        Root.Measure(new Size(MenuLayout.Width, double.PositiveInfinity));
        var height = Math.Ceiling(Root.DesiredSize.Height);
        Width = MenuLayout.Width;
        Height = height;

        var cursor = new System.Drawing.Point((int)anchor.X, (int)anchor.Y);
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var scale = Dwm.ScaleForPoint(cursor.X, cursor.Y);
        MoveTo(MenuLayout.Place(new PxRect(area.X, area.Y, area.Width, area.Height), height, scale, anchor.X, anchor.Y));
    }

    /// Setas sem foco em item: Down vai ao primeiro comando, Up ao último (depois o
    /// KeyboardNavigation do painel cicla sozinho). Enter/Espaço o próprio botão trata.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key is not (Key.Down or Key.Up)) return;
        if (Keyboard.FocusedElement is Button) return;
        var buttons = _elements.OfType<Button>().ToList();
        if (buttons.Count == 0) return;
        (e.Key == Key.Down ? buttons[0] : buttons[^1]).Focus();
        e.Handled = true;
    }

    /// Abertura (§3.10): opacidade 0→1 e translateY 4→0, 83 ms Decelerate; sem animação do
    /// sistema, aparece direto.
    private void AnimateIn()
    {
        var duration = Motion.Scaled(Motion.Fast, SystemParameters.ClientAreaAnimation);
        if (duration == TimeSpan.Zero)
        {
            Root.BeginAnimation(OpacityProperty, null);
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            Root.Opacity = 1;
            Slide.Y = 0;
            return;
        }
        var (x1, y1, x2, y2) = Motion.Decelerate;
        var spline = new KeySpline(x1, y1, x2, y2);
        Root.BeginAnimation(OpacityProperty, Frames(0, 1, duration, spline));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Frames(4, 0, duration, spline));
    }

    private static DoubleAnimationUsingKeyFrames Frames(double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));
        return animation;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
