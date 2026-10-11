// Popover da bandeja (desenho §9): janela sem borda perto do ícone, fecha ao perder o foco
// ou com Esc. Contagem grande em cima. Ocioso: presets em chips, campo livre "d h m"
// (DurationParser) e "Iniciar" — Enter sem valor inicia o preset padrão. Rodando: "+15 min",
// "+1 h", "+4 h", campo livre + "Estender" e o botão de segurar para encerrar.
using Insomnia.App.Composition;
using Insomnia.Core.Time;

namespace Insomnia.App.Tray;

public sealed class PopoverForm : Form
{
    private static readonly Color Background = TrayIconRenderer.TileRunning;
    private static readonly Color Surface = TrayIconRenderer.Tile;
    private static readonly Color Ink = TrayIconRenderer.Ink;
    private static readonly Color Muted = TrayIconRenderer.Accent;
    private static readonly Color WarningInk = Color.FromArgb(0xF0, 0xA0, 0x40);

    private readonly AppHost _host;
    private readonly Label _countdown;
    private readonly Label _subtitle;
    private readonly Label _message;
    private readonly FlowLayoutPanel _idlePanel;
    private readonly FlowLayoutPanel _chips;
    private readonly TextBox _startBox;
    private readonly FlowLayoutPanel _runningPanel;
    private readonly TextBox _extendBox;
    private readonly HoldToEndButton _hold;
    private string _chipsKey = "";
    private bool _busy;

    public PopoverForm(AppHost host)
    {
        _host = host;
        Text = "Insomnia";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        // Sem Dock: num Form AutoSize, filho Dock=Fill briga com o cálculo de tamanho.
        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(14, 14) };
        _countdown = new Label { AutoSize = true, Font = new Font("Segoe UI", 26f, FontStyle.Bold), ForeColor = Ink, Text = "Insomnia" };
        _subtitle = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(280, 0) };
        root.Controls.Add(_countdown);
        root.Controls.Add(_subtitle);

        // ---- ocioso
        _idlePanel = Column();
        _chips = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(290, 0), WrapContents = true, Margin = new Padding(0, 8, 0, 4) };
        _startBox = Field("ex.: 45, 1h30m, 3d");
        var startButton = ActionButton("Iniciar", (_, _) => _ = CommitStartAsync(_startBox.Text));
        _idlePanel.Controls.Add(_chips);
        _idlePanel.Controls.Add(Row(_startBox, startButton));

        // ---- rodando
        _runningPanel = Column();
        var quick = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        quick.Controls.Add(ActionButton("+15 min", (_, _) => _ = ExtendAsync(TimeSpan.FromMinutes(15))));
        quick.Controls.Add(ActionButton("+1 h", (_, _) => _ = ExtendAsync(TimeSpan.FromHours(1))));
        quick.Controls.Add(ActionButton("+4 h", (_, _) => _ = ExtendAsync(TimeSpan.FromHours(4))));
        _extendBox = Field("ex.: 30, 2h");
        var extendButton = ActionButton("Estender", (_, _) => _ = CommitExtendAsync(_extendBox.Text));
        _hold = new HoldToEndButton { Margin = new Padding(0, 6, 6, 0) };
        _hold.Completed += (_, _) => _ = EndAsync();
        var holdLabel = new Label { AutoSize = true, Text = "Segure para encerrar", ForeColor = Muted, Margin = new Padding(0, 19, 0, 0) };
        _runningPanel.Controls.Add(quick);
        _runningPanel.Controls.Add(Row(_extendBox, extendButton));
        _runningPanel.Controls.Add(Row(_hold, holdLabel));

        _message = new Label { AutoSize = true, ForeColor = WarningInk, MaximumSize = new Size(280, 0), Margin = new Padding(0, 6, 0, 0) };
        root.Controls.Add(_idlePanel);
        root.Controls.Add(_runningPanel);
        root.Controls.Add(_message);
        Controls.Add(root);

        _startBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = CommitStartAsync(_startBox.Text); } };
        _extendBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = CommitExtendAsync(_extendBox.Text); } };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CsDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    /// Mostra perto do ponto (o cursor no clique da bandeja), dentro da área útil da tela.
    public void ShowNear(Point anchor)
    {
        _message.Text = "";
        if (!Visible) Show();
        PerformLayout();
        var area = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X - Width / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        var y = anchor.Y - Height - 12 < area.Top ? anchor.Y + 12 : anchor.Y - Height - 12;
        y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        Location = new Point(x, y);
        Activate();
        (_host.Manager.IsActive ? _extendBox : _startBox).Focus();
    }

    /// Estado mudou: troca os painéis, os chips e os textos.
    public void RefreshState(StatusSnapshot s)
    {
        var running = s.Session is not null;
        _idlePanel.Visible = !running;
        _runningPanel.Visible = running;
        RebuildChips();
        Tick(s);
    }

    /// A cada segundo: só a contagem e a linha de baixo.
    public void Tick(StatusSnapshot s)
    {
        if (s.Session is { } session)
        {
            _countdown.Text = TrayTexts.Countdown(session, s.Now);
            _subtitle.Text = $"termina {TrayTexts.EndLabel(session, s.Now)}" + (s.CountdownPaused ? " · pausada com a tampa fechada" : "");
        }
        else
        {
            _countdown.Text = "Insomnia";
            _subtitle.Text = "Mantém o notebook acordado até a hora marcada, mesmo com a tampa fechada.";
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_busy) Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { e.Handled = true; Hide(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar (Alt+F4) só esconde; quem descarta é a bandeja.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ ações

    private async Task CommitStartAsync(string text)
    {
        TimeSpan duration;
        if (string.IsNullOrWhiteSpace(text))
        {
            var preset = _host.Config.Current.DefaultPreset;
            if (preset <= 0) { Say(TrayTexts.InvalidDuration); return; }
            duration = TimeSpan.FromSeconds(preset);
        }
        else if (DurationParser.Parse(text) is { } parsed) duration = parsed;
        else { Say(TrayTexts.InvalidDuration); return; }
        await StartAsync(duration);
    }

    private async Task CommitExtendAsync(string text)
    {
        if (DurationParser.Parse(text) is not { } by) { Say(string.IsNullOrWhiteSpace(text) ? "Digite quanto estender (ex.: 30, 2h)." : TrayTexts.InvalidDuration); return; }
        await ExtendAsync(by);
    }

    private Task StartAsync(TimeSpan duration) => RunAsync(async () =>
    {
        var problem = await _host.StartSessionAsync(duration);
        if (problem is null) _startBox.Clear();
        return problem;
    });

    private Task ExtendAsync(TimeSpan by) => RunAsync(async () =>
    {
        var problem = await _host.ExtendSessionAsync(by);
        if (problem is null) _extendBox.Clear();
        return problem;
    });

    private Task EndAsync() => RunAsync(async () =>
    {
        await _host.EndSessionAsync();
        return null;
    });

    private async Task RunAsync(Func<Task<string?>> action)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        Say("");
        try
        {
            var problem = await action();
            Say(problem ?? "");
        }
        catch (Exception ex)
        {
            _host.Log.Error($"popover: {ex.Message}");
            Say($"Falhou: {ex.Message}");
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            if (!IsDisposed) RefreshState(_host.Snapshot());
        }
    }

    private void Say(string text)
    {
        if (!IsDisposed) _message.Text = text;
    }

    private void RebuildChips()
    {
        var cfg = _host.Config.Current;
        var key = string.Join(",", cfg.Presets) + "|" + cfg.DefaultPreset;
        if (key == _chipsKey) return;
        _chipsKey = key;
        foreach (Control old in _chips.Controls.Cast<Control>().ToList()) old.Dispose();
        _chips.Controls.Clear();
        foreach (var seconds in cfg.Presets)
        {
            var duration = TimeSpan.FromSeconds(seconds);
            var chip = ActionButton(DurationFormat.Chip(duration), (_, _) => _ = StartAsync(duration));
            if (seconds == cfg.DefaultPreset) chip.FlatAppearance.BorderColor = Muted;
            _chips.Controls.Add(chip);
        }
    }

    // ------------------------------------------------------------------ construção

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static TextBox Field(string placeholder) => new()
    {
        Width = 170,
        PlaceholderText = placeholder,
        BackColor = Surface,
        ForeColor = Ink,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 3, 6, 0),
    };

    private static Button ActionButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Ink,
            Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(0, 0, 6, 6),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Surface;
        button.Click += onClick;
        return button;
    }
}
