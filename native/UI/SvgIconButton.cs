namespace VectorAnimationEngine;

internal class SvgIconButton : Button
{
    public SvgIconButton(SvgIconKind icon)
    {
        Icon = icon;
        Text = string.Empty;
        Width = Theme.IconButtonSize;
        Height = Theme.IconButtonSize;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public SvgIconKind Icon { get; set; }

    public bool ShowsToolGroupIndicator { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var active = UiMotion.IsActive(this);
        var hover = UiMotion.HoverProgress(this);
        var foreground = Enabled ? ForeColor : Theme.DisabledText;
        using var background = new SolidBrush(BackColor);
        using var hoverWash = new SolidBrush(Color.FromArgb((int)Math.Round(24 * hover), Theme.Accent));
        using var border = new Pen(active ? Theme.Accent : Theme.Mix(Theme.Border, Theme.Accent, hover * 0.52f), 1f + hover * 0.25f);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Top);
        e.Graphics.FillRectangle(background, ClientRectangle);
        if (hover > 0.01f) e.Graphics.FillRectangle(hoverWash, new Rectangle(1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2)));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        if (active)
        {
            using var activeBar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(activeBar, 1, 4, 3, Math.Max(0, Height - 8));
        }

        var hasLabel = !string.IsNullOrWhiteSpace(Text);
        var iconBounds = hasLabel
            ? new Rectangle(3, 0, Theme.IconButtonSize, Height)
            : ClientRectangle;
        SvgIcons.Draw(e.Graphics, Icon, iconBounds, foreground);
        if (hasLabel)
        {
            var textBounds = new Rectangle(
                Theme.IconButtonSize + 10,
                1,
                Math.Max(0, Width - Theme.IconButtonSize - 16),
                Math.Max(0, Height - 2));
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (ShowsToolGroupIndicator)
        {
            using var indicator = new SolidBrush(active ? Theme.Accent : Color.FromArgb(220, foreground));
            e.Graphics.FillPolygon(indicator, new Point[]
            {
                new Point(Width - 10, Height - 4),
                new Point(Width - 4, Height - 4),
                new Point(Width - 4, Height - 10)
            });
        }
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(ClientRectangle, -3, -3),
                Theme.AccentLabel,
                BackColor);
        }
    }
}

internal sealed class SvgToggleButton : CheckBox
{
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private float _hoverProgress;
    private float _hoverTarget;

    public SvgToggleButton(SvgIconKind icon, string name)
    {
        Icon = icon;
        Text = string.Empty;
        AccessibleName = name;
        Width = 32;
        Height = 32;
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        Margin = new Padding(0, 0, 6, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _motionTimer.Tick += (_, _) => TickMotion();
    }

    public SvgIconKind Icon { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var baseBack = Checked ? Theme.Accent : Theme.PanelStrong;
        var back = Theme.Mix(baseBack, Checked ? Theme.AccentHoverSurface : Theme.PanelHover, _hoverProgress);
        var fore = Checked ? Theme.AccentText : Theme.Text;
        using var background = new SolidBrush(back);
        using var border = new Pen(Checked ? Theme.Accent : Theme.Mix(Theme.Border, Theme.Accent, _hoverProgress * 0.55f), 1f + _hoverProgress * 0.2f);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        SvgIcons.Draw(e.Graphics, Icon, ClientRectangle, fore);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverTarget = 1f;
        if (!_motionTimer.Enabled) _motionTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTarget = 0f;
        if (!_motionTimer.Enabled) _motionTimer.Start();
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _motionTimer.Dispose();
        base.Dispose(disposing);
    }

    private void TickMotion()
    {
        _hoverProgress += (_hoverTarget - _hoverProgress) * 0.30f;
        if (Math.Abs(_hoverTarget - _hoverProgress) < 0.015f)
        {
            _hoverProgress = _hoverTarget;
            _motionTimer.Stop();
        }

        Invalidate();
    }
}
