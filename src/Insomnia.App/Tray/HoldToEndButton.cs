// Botão "segure para encerrar" (desenho §9; original UI/HoldToEndButton.swift): o anel se
// fecha em 0,6 s enquanto o botão está pressionado; soltar antes, ou arrastar mais de 12 px,
// cancela sem ação. Teclado: segurar Espaço/Enter faz o mesmo. A ação de acessibilidade
// padrão encerra direto (sem segurar), como o accessibilityAction do original.
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Insomnia.App.Tray;

public sealed class HoldToEndButton : Control
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(0.6);
    public const int CancelDistance = 12;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    private readonly Stopwatch _held = new();
    private Point _pressedAt;
    private float _progress;

    public HoldToEndButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(44, 44);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "Encerrar sessão";
        AccessibleDescription = "Pressione e segure";
        AccessibleRole = AccessibleRole.PushButton;
        _tick.Tick += (_, _) => Advance();
    }

    /// O anel completou: a sessão deve ser encerrada.
    public event EventHandler? Completed;

    /// 0..1 enquanto segura.
    public float Progress => _progress;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressedAt = e.Location;
        Begin();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_held.IsRunning) return;
        var dx = e.X - _pressedAt.X;
        var dy = e.Y - _pressedAt.Y;
        if (dx * dx + dy * dy > CancelDistance * CancelDistance) Cancel();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Cancel();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyCode is Keys.Space or Keys.Enter) && !_held.IsRunning) { Begin(); e.Handled = true; }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) Cancel();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Cancel();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new HoldAccessible(this);

    private void Begin()
    {
        _held.Restart();
        _tick.Start();
        Invalidate();
    }

    private void Cancel()
    {
        if (!_held.IsRunning && _progress == 0) return;
        _held.Reset();
        _tick.Stop();
        _progress = 0;
        Invalidate();
    }

    private void Advance()
    {
        _progress = (float)Math.Min(1.0, _held.Elapsed / HoldDuration);
        Invalidate();
        if (_progress < 1f) return;
        Cancel();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void Fire() => Completed?.Invoke(this, EventArgs.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var side = Math.Min(Width, Height);
        var scale = _held.IsRunning ? 1.12f : 1f;
        var ring = side * 0.62f * scale;
        var rect = new RectangleF((Width - ring) / 2f, (Height - ring) / 2f, ring, ring);
        var stroke = Math.Max(2f, side / 18f);

        using (var track = new Pen(Color.FromArgb(90, TrayIconRenderer.Accent), stroke))
            g.DrawEllipse(track, rect);
        if (_progress > 0)
        {
            using var arc = new Pen(TrayIconRenderer.Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, rect, -90, 360 * _progress);
        }

        // "×" no centro.
        var c = new PointF(Width / 2f, Height / 2f);
        var arm = ring * 0.18f;
        using var x = new Pen(Focused ? TrayIconRenderer.Accent : TrayIconRenderer.Ink, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(x, c.X - arm, c.Y - arm, c.X + arm, c.Y + arm);
        g.DrawLine(x, c.X - arm, c.Y + arm, c.X + arm, c.Y - arm);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    private sealed class HoldAccessible(HoldToEndButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "Encerrar";
        public override void DoDefaultAction() => owner.Fire();
    }
}
