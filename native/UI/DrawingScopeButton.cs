using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class DrawingScopeButton : Button
{
    private readonly Icon _applicationIcon = new(typeof(MainForm), "Application.ico");
    private bool _current = true;
    private bool _hover;

    public DrawingScopeButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.UiFont(8.5f, FontStyle.Bold);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    public int PreferredScopeWidth
    {
        get
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var textWidth = new[] { UiLocalization.T("Current scope"), UiLocalization.T("Open scope") }
                .Max(label => TextRenderer.MeasureText(label, Font, Size.Empty, flags).Width);
            return textWidth + (int)Math.Ceiling(36 * DeviceDpi / 96f);
        }
    }

    public bool CurrentScope
    {
        get => _current;
        set
        {
            if (_current == value) return;
            SetScope(value);
        }
    }

    // The scope label stays fixed ("当前"/"打开") and the thumb jumps without
    // animation; the object name is only exposed through the hover tooltip.
    private void SetScope(bool current)
    {
        _current = current;
        AccessibleDescription = _current ? UiLocalization.T("Current scope") : UiLocalization.T("Open scope");
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        CurrentScope = !CurrentScope;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        var background = Parent?.BackColor ?? Theme.Top;
        e.Graphics.Clear(background);
        var surface = SystemInformation.HighContrast ? SystemColors.Control
            : Theme.Mix(Theme.PanelStrong, _current ? Theme.Accent : Theme.Text, _current ? 0.12f : _hover ? 0.07f : 0.025f);
        using var fill = new SolidBrush(surface);
        using var border = new Pen(Theme.ControlOutline, Math.Max(1, scale));
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        void PaintRounded(Rectangle box, Brush brush, Pen? edge, float radius)
        {
            using var path = new GraphicsPath();
            var diameter = Math.Min(Math.Min(box.Width, box.Height), radius * 2);
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
            if (edge is not null) e.Graphics.DrawPath(edge, path);
            e.Graphics.Restore(state);
        }
        PaintRounded(bounds, fill, border, 6 * scale);
        var side = Math.Min(Height - 8 * scale, 24 * scale);
        var left = 4 * scale + (Width - side - 8 * scale) * (_current ? 1 : 0);
        var thumb = new Rectangle((int)left, (int)((Height - side) / 2), (int)side, (int)side);
        using var thumbFill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight
            : Theme.Text);
        PaintRounded(thumb, thumbFill, null, 4 * scale);
        var iconInset = Math.Max(1, (int)Math.Round(2 * scale));
        var iconBounds = Rectangle.Inflate(thumb, -iconInset, -iconInset);
        e.Graphics.DrawIcon(_applicationIcon, iconBounds);
        var leftText = Rectangle.FromLTRB((int)(side + 10 * scale), 0, Width - (int)(6 * scale), Height);
        var rightText = Rectangle.FromLTRB((int)(6 * scale), 0, Width - (int)(side + 10 * scale), Height);
        var labelBounds = _current ? rightText : leftText;
        var textColor = SystemInformation.HighContrast ? SystemColors.ControlText
            : _current ? Theme.AccentLabel : Theme.SecondaryText;
        var label = _current ? UiLocalization.T("Current scope") : UiLocalization.T("Open scope");
        TextRenderer.DrawText(e.Graphics, label, Font, labelBounds,
            textColor, TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) Theme.DrawKeyboardFocus(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _applicationIcon.Dispose();
        base.Dispose(disposing);
    }
}
