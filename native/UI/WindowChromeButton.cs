namespace VectorAnimationEngine;

internal enum WindowChromeButtonKind
{
    Restart,
    Minimize,
    Maximize,
    Restore,
    Close
}

internal sealed class WindowChromeButton : Control
{
    private bool _hovered;
    private bool _pressed;

    public WindowChromeButtonKind Kind { get; set; }

    public WindowChromeButton(WindowChromeButtonKind kind)
    {
        Kind = kind;
        Width = 42;
        Height = 32;
        Anchor = AnchorStyles.Top | AnchorStyles.Right;
        BackColor = Theme.Top;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        Cursor = Cursors.Default;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var background = GetBackColor();
        g.Clear(background);

        var preferredColor = Kind == WindowChromeButtonKind.Close && _hovered ? Color.White : Theme.Text;
        var iconColor = SystemInformation.HighContrast
            ? SystemColors.HighlightText
            : Theme.ReadableUiColor(background, preferredColor);
        using var pen = new Pen(iconColor, 1.7f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        var cx = Width / 2f;
        var cy = Height / 2f;
        switch (Kind)
        {
            case WindowChromeButtonKind.Restart:
                DrawRestartIcon(g, pen, cx, cy);
                break;
            case WindowChromeButtonKind.Minimize:
                g.DrawLine(pen, cx - 5, cy + 4, cx + 5, cy + 4);
                break;
            case WindowChromeButtonKind.Maximize:
                g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                break;
            case WindowChromeButtonKind.Restore:
                g.DrawRectangle(pen, cx - 3, cy - 6, 9, 9);
                g.DrawRectangle(pen, cx - 6, cy - 3, 9, 9);
                break;
            case WindowChromeButtonKind.Close:
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                break;
        }
    }

    // Circular arrow with the gap at the top right. The chevron tip sits exactly
    // on the arc endpoint and points along the clockwise travel direction, so the
    // arrow can never detach from the ring.
    private static void DrawRestartIcon(System.Drawing.Graphics g, System.Drawing.Pen pen, float cx, float cy)
    {
        const float radius = 6f;
        const float startAngle = 42f;
        const float sweepAngle = 280f;
        g.DrawArc(pen, cx - radius, cy - radius, radius * 2f, radius * 2f, startAngle, sweepAngle);

        var end = (startAngle + sweepAngle) * MathF.PI / 180f;
        var tip = new PointF(cx + radius * MathF.Cos(end), cy + radius * MathF.Sin(end));
        var tangent = new PointF(-MathF.Sin(end), MathF.Cos(end));
        const float length = 4.2f;
        var spread = 27f * MathF.PI / 180f;
        PointF Blade(float angle)
        {
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            return new PointF(
                tip.X - length * (tangent.X * cos - tangent.Y * sin),
                tip.Y - length * (tangent.X * sin + tangent.Y * cos));
        }
        g.DrawLine(pen, tip, Blade(spread));
        g.DrawLine(pen, tip, Blade(-spread));
    }

    private Color GetBackColor()
    {
        if (Kind == WindowChromeButtonKind.Close && _hovered)
        {
            return _pressed ? Color.FromArgb(132, 36, 36) : Color.FromArgb(184, 56, 56);
        }

        if (_pressed) return Theme.Accent;
        return _hovered ? Theme.PanelStrong : Theme.Top;
    }
}
