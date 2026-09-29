using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class AnimatedToolTip : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private float _progress;
    private bool _targetVisible;
    private Point _targetLocation;
    private string _message = "";

    public AnimatedToolTip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Visible = false;
        Enabled = false;
        Font = Theme.UiFont(9.5f, FontStyle.Bold);
        _timer.Tick += (_, _) => TickAnimation();
    }

    public void ShowFor(Control anchor, string message)
    {
        if (anchor.FindForm() is not { } form) return;
        if (Parent != form) Parent = form;

        _message = UiLocalization.T(message);
        using var g = CreateGraphics();
        var textSize = TextRenderer.MeasureText(g, _message, Font, new Size(320, 32), TextFormatFlags.NoPadding);
        Size = new Size(Math.Max(92, textSize.Width + 24), 34);
        var screen = anchor.PointToScreen(new Point(anchor.Width + 12, (anchor.Height - Height) / 2));
        var location = form.PointToClient(screen);
        if (location.X + Width > form.ClientSize.Width - 8)
        {
            screen = anchor.PointToScreen(new Point(-Width - 12, (anchor.Height - Height) / 2));
            location = form.PointToClient(screen);
        }

        _targetLocation = new Point(
            Math.Clamp(location.X, 8, Math.Max(8, form.ClientSize.Width - Width - 8)),
            Math.Clamp(location.Y, 8, Math.Max(8, form.ClientSize.Height - Height - 8)));
        Location = new Point(_targetLocation.X - 10, _targetLocation.Y);

        _targetVisible = true;
        Visible = true;
        BringToFront();
        if (!_timer.Enabled) _timer.Start();
    }

    public void HideTip()
    {
        _targetVisible = false;
        if (!_timer.Enabled) _timer.Start();
    }

    /// <summary>Shows a multiline canvas hint near the pointer without restarting its animation.</summary>
    public void ShowAt(Control anchor, Point point, string message)
    {
        if (anchor.FindForm() is not { } form) return;
        if (Parent != form) Parent = form;
        var scale = Math.Max(1f, anchor.DeviceDpi / 96f);
        var maxWidth = Math.Max(40, Math.Min((int)(440 * scale), anchor.ClientSize.Width - 24));
        var textChanged = _message != message;
        _message = message;
        var textSize = TextRenderer.MeasureText(message, Font, new Size(maxWidth - 24, 0),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        Size = new Size(Math.Min(maxWidth, Math.Max(92, textSize.Width + 24)), textSize.Height + 20);
        var location = form.PointToClient(anchor.PointToScreen(
            new Point(point.X + (int)(20 * scale), point.Y + (int)(24 * scale))));
        var viewport = form.RectangleToClient(anchor.RectangleToScreen(anchor.ClientRectangle));
        if (location.X + Width > viewport.Right - 8) location.X -= Width + (int)(40 * scale);
        if (location.Y + Height > viewport.Bottom - 8) location.Y -= Height + (int)(48 * scale);
        _targetLocation = new Point(
            Math.Clamp(location.X, viewport.Left + 8, Math.Max(viewport.Left + 8, viewport.Right - Width - 8)),
            Math.Clamp(location.Y, viewport.Top + 8, Math.Max(viewport.Top + 8, viewport.Bottom - Height - 8)));
        Location = _targetLocation;
        _targetVisible = true;
        Visible = true;
        AccessibleName = message;
        BringToFront();
        if (textChanged) Invalidate();
        if (_progress < 1f && !_timer.Enabled) _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_progress <= 0.01f || string.IsNullOrWhiteSpace(_message)) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var alpha = (int)Math.Clamp(235 * EaseOut(_progress), 0, 235);
        var parentBackground = Theme.EffectiveBackground(this);
        var tooltipBackground = Theme.Mix(parentBackground, Theme.PanelStrong, alpha / 255f);
        var borderAlpha = (int)Math.Clamp(220 * _progress, 0, 220);
        var textAlpha = (int)Math.Clamp(255 * _progress, 0, 255);
        using var bg = new SolidBrush(Color.FromArgb(alpha, Theme.PanelStrong));
        using var border = new Pen(
            Color.FromArgb(borderAlpha, Theme.ReadableUiColor(tooltipBackground, Theme.Accent)),
            1);
        using var text = new SolidBrush(
            Color.FromArgb(textAlpha, Theme.ReadableText(tooltipBackground, Theme.Text)));
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 7);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        };
        g.DrawString(_message, Font, text, new RectangleF(12, 0, Width - 24, Height), format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private void TickAnimation()
    {
        var target = _targetVisible ? 1f : 0f;
        _progress += (target - _progress) * 0.24f;
        if (Math.Abs(target - _progress) < 0.015f) _progress = target;

        var eased = EaseOut(_progress);
        Location = new Point(_targetLocation.X - (int)Math.Round((1f - eased) * 10), _targetLocation.Y);
        Invalidate();

        if (_progress <= 0f && !_targetVisible)
        {
            Visible = false;
            _timer.Stop();
        }
        else if (_progress >= 1f && _targetVisible)
        {
            _timer.Stop();
        }
    }

    private static float EaseOut(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return 1f - MathF.Pow(1f - value, 3f);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
