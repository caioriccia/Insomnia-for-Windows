// Painel (desenho-ui §3.5): desenha o PanelState e devolve as intenções da pessoa como eventos
// (chip, Iniciar, Estender, engrenagem, ação de aviso, segurar para encerrar, alça, dispensar).
// Não chama o host nem decide nada: quem executa, anima e fecha é a UiShell.
//
// Blocos que mudam com o estado (chips, avisos, linhas) só são remontados quando o conteúdo
// muda de fato — o Tick redesenha a cada segundo e remontar derrubaria o foco do teclado.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Native;
using Insomnia.App.Ui.Theme;
using Insomnia.App.Ui.Windows;

namespace Insomnia.App.Ui.Shell;

public partial class PanelWindow : FlyoutWindow
{
    public const string OtherLabel = "Outro…";

    private PanelMode? _mode;
    private string _chipsKey = "";
    private string _noticesKey = "";
    private string _rowsKey = "";
    private bool _otherOpen;
    private bool _handleDown;
    private double _handleStartY;
    private double _handleTravel;

    public PanelWindow() : base(Backdrop.Acrylic, activatable: true)
    {
        InitializeComponent();

        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        StartButton.Click += (_, _) => StartRequested?.Invoke(StartField.Text);
        ExtendButton.Click += (_, _) => ExtendRequested?.Invoke(ExtendField.Text);
        StartField.KeyDown += (_, e) => OnEnter(e, () => StartRequested?.Invoke(StartField.Text));
        ExtendField.KeyDown += (_, e) => OnEnter(e, () => ExtendRequested?.Invoke(ExtendField.Text));
        HoldToEnd.Completed += () => EndRequested?.Invoke();

        Handle.MouseLeftButtonDown += OnHandleDown;
        Handle.MouseMove += OnHandleMove;
        Handle.MouseLeftButtonUp += OnHandleUp;
        Handle.LostMouseCapture += (_, _) => _handleDown = false;

        // Qualquer clique ou tecla dentro do painel: a pessoa está usando, não recolher sozinho.
        PreviewMouseDown += (_, _) => Interacted?.Invoke();
        PreviewKeyDown += (_, _) => Interacted?.Invoke();
    }

    /// Esc, perda de foco ou Alt+F4 (FlyoutWindow): a UiShell decide e anima o fechamento.
    public event Action? DismissRequested;
    public event Action? HandleCloseRequested;
    public event Action? SettingsRequested;
    public event Action<PanelChip>? ChipClicked;
    public event Action<string>? StartRequested;
    public event Action<string>? ExtendRequested;
    public event Action<PanelNotice>? NoticeActionRequested;
    public event Action? EndRequested;
    public event Action? Interacted;

    /// Modo do último Render (null antes do primeiro).
    public PanelMode? Mode => _mode;

    // ------------------------------------------------------------------ estado

    public void Render(PanelState s)
    {
        var previous = _mode;
        _mode = s.Mode;
        var session = s.Mode == PanelMode.Session;

        HeaderGlyph.Text = session ? Glyphs.Eye : Glyphs.EyeClosed;
        Description.Visibility = session ? Visibility.Collapsed : Visibility.Visible;

        Headline.Text = s.Headline;
        Subline.Text = s.Subline;
        var sublineBrush = ShellMath.SublineBrush(session ? s.Tone : PillTone.Normal);
        Subline.SetResourceReference(TextBlock.ForegroundProperty, sublineBrush);
        SublineGlyph.SetResourceReference(TextBlock.ForegroundProperty, sublineBrush);
        SublineGlyph.Visibility = session ? Visibility.Visible : Visibility.Collapsed;

        RenderChips(s);
        if (!session) _otherOpen = false;
        ExtendRow.Visibility = session && _otherOpen ? Visibility.Visible : Visibility.Collapsed;
        StartField.Visibility = session ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = session ? Visibility.Collapsed : Visibility.Visible;

        if (s.Lid is { } lid)
        {
            LidBanner.Visibility = Visibility.Visible;
            LidTitle.Text = lid.Title;
            LidDetail.Text = lid.Detail;
            LidDetail.Visibility = string.IsNullOrEmpty(lid.Detail) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            LidBanner.Visibility = Visibility.Collapsed;
        }

        RenderNotices(s.Notices);
        RenderRows(s.Rows);

        HoldToEnd.Visibility = session ? Visibility.Visible : Visibility.Collapsed;
        if (session && previous != PanelMode.Session) HoldToEnd.Reset();

        // Troca de modo com o painel na tela: cross-fade de blocos (167 ms Standard).
        if (previous is not null && previous != s.Mode && IsVisible)
        {
            var fade = new DoubleAnimation(0, 1, new Duration(Motion.Scaled(Motion.Short, SystemParameters.ClientAreaAnimation)))
            {
                EasingFunction = new FluentEase(Motion.Standard),
            };
            Body.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// Erro de comando (InfoBar Critical acima do rodapé); null/vazio esconde.
    public void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorBar.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Comando em andamento: cursor de espera (a UiShell ignora novos comandos até terminar).
    public void SetBusy(bool busy) => Cursor = busy ? Cursors.Wait : null;

    /// Limpa os campos, recolhe o "Outro…" e rearma o "Segure para encerrar".
    public void ResetInputs()
    {
        StartField.Clear();
        ExtendField.Clear();
        _otherOpen = false;
        ExtendRow.Visibility = Visibility.Collapsed;
        HoldToEnd.Reset();
    }

    /// Depois de encerrar pelo botão.
    public void ShowEnded() => HoldToEnd.ShowEnded();

    /// O fim não pegou (erro, ou a sessão segue de pé): o botão volta a "Segure para encerrar".
    public void RearmEnd() => HoldToEnd.Reset();

    /// Altura do conteúdo na largura de 360 (epx).
    public double MeasureContentHeight()
    {
        Root.Measure(new Size(ShellMath.PanelWidth, double.PositiveInfinity));
        return Math.Ceiling(Root.DesiredSize.Height);
    }

    /// Recorte do conteúdo (a janela já tem clip.Width × clip.Height): o Root fica centrado na
    /// janela e é recortado no retângulo arredondado; opacity é a do conteúdo.
    public void ApplyClip(ShellClip clip, double opacity)
    {
        var offset = (ShellMath.PanelWidth - clip.Width) / 2;
        Canvas.SetLeft(Root, -offset);
        Root.Clip = new RectangleGeometry(new Rect(offset, 0, clip.Width, clip.Height), clip.Radius, clip.Radius);
        Root.Opacity = Math.Clamp(opacity, 0, 1);
    }

    /// Foco inicial (§4): campo de duração (Iniciar) ou "+15 min" (Sessão). Depois do layout.
    public void FocusInitial(PanelFocus focus)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            UIElement? target = focus == PanelFocus.DurationField
                ? StartField
                : Chips.Children.OfType<Button>().FirstOrDefault();
            if (target is { IsVisible: true })
            {
                target.Focus();
                Keyboard.Focus(target);
            }
        }, DispatcherPriority.Input);
    }

    protected override void OnDismiss() => DismissRequested?.Invoke();

    // ------------------------------------------------------------------ blocos

    private void RenderChips(PanelState s)
    {
        var session = s.Mode == PanelMode.Session;
        var key = $"{s.Mode}|{s.ExtendEmphasis}|" + string.Join(";", s.Chips.Select(c => $"{c.Label}:{c.Duration.Ticks}:{c.Selected}"));
        if (key == _chipsKey) return;
        _chipsKey = key;

        var focused = Chips.Children.OfType<Button>().ToList().FindIndex(b => b.IsKeyboardFocusWithin);
        Chips.Children.Clear();
        for (var i = 0; i < s.Chips.Count; i++)
        {
            var chip = s.Chips[i];
            var accent = session ? i == 0 && s.ExtendEmphasis : chip.Selected;
            var button = Chip(chip.Label, accent);
            button.Click += (_, _) => ChipClicked?.Invoke(chip);
            Chips.Children.Add(button);
        }
        if (session)
        {
            var other = Chip(OtherLabel, accent: false);
            other.Click += (_, _) => ToggleOther();
            Chips.Children.Add(other);
        }

        if (focused >= 0 && Chips.Children.Count > 0)
        {
            var again = (UIElement)Chips.Children[Math.Min(focused, Chips.Children.Count - 1)];
            Dispatcher.InvokeAsync(() => again.Focus(), DispatcherPriority.Input);
        }
    }

    private static Button Chip(string label, bool accent)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        button.SetResourceReference(StyleProperty, accent ? "Insomnia.Chip.Selected" : "Insomnia.Chip");
        return button;
    }

    private void ToggleOther()
    {
        _otherOpen = !_otherOpen;
        ExtendRow.Visibility = _otherOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_otherOpen) Dispatcher.InvokeAsync(() => ExtendField.Focus(), DispatcherPriority.Input);
    }

    private void RenderNotices(IReadOnlyList<PanelNotice> notices)
    {
        var key = string.Join("\n", notices.Select(n => $"{n.Kind}|{n.Text}|{n.ActionLabel}|{n.Command}|{n.Argument}"));
        if (key == _noticesKey) return;
        _noticesKey = key;

        Notices.Children.Clear();
        foreach (var notice in notices)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = notice.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(text);
            if (!string.IsNullOrEmpty(notice.ActionLabel))
            {
                var link = new Button { Content = notice.ActionLabel, Margin = new Thickness(12, 0, 0, 0) };
                link.SetResourceReference(StyleProperty, "Shell.Link");
                link.Click += (_, _) => NoticeActionRequested?.Invoke(notice);
                Grid.SetColumn(link, 1);
                grid.Children.Add(link);
            }

            var bar = new ContentControl { Content = grid, Tag = notice.Kind, Margin = new Thickness(0, 12, 0, 0) };
            bar.SetResourceReference(StyleProperty, "Insomnia.InfoBar");
            Notices.Children.Add(bar);
        }
    }

    private void RenderRows(IReadOnlyList<StatusRow> rows)
    {
        var key = string.Join("\n", rows.Select(r => $"{r.Glyph}|{r.Label}|{r.Value}"));
        if (key == _rowsKey) return;
        _rowsKey = key;

        Rows.Children.Clear();
        foreach (var row in rows)
        {
            var grid = new Grid { Height = 36 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var glyph = new TextBlock { Text = row.Glyph, Width = 16, Margin = new Thickness(0, 0, 10, 0), TextAlignment = TextAlignment.Center };
            glyph.SetResourceReference(StyleProperty, "Shell.Glyph");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Insomnia.Text.Secondary");

            var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);

            var value = new TextBlock
            {
                Text = row.Value,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = row.Value,
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Insomnia.Text.Secondary");
            Grid.SetColumn(value, 2);

            grid.Children.Add(glyph);
            grid.Children.Add(label);
            grid.Children.Add(value);
            Rows.Children.Add(grid);
        }
        Card.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ entrada

    private static void OnEnter(KeyEventArgs e, Action action)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        action();
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        _handleDown = true;
        _handleStartY = e.GetPosition(this).Y;
        _handleTravel = 0;
        Handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_handleDown) return;
        _handleTravel = Math.Max(_handleTravel, Math.Abs(e.GetPosition(this).Y - _handleStartY));
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_handleDown) return;
        _handleDown = false;
        var upward = _handleStartY - e.GetPosition(this).Y;
        var travel = Math.Max(_handleTravel, Math.Abs(upward));
        if (Handle.IsMouseCaptured) Handle.ReleaseMouseCapture();
        e.Handled = true;
        if (ShellMath.HandleCloses(upward, travel)) HandleCloseRequested?.Invoke();
    }
}
