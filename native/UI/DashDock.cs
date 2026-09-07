using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class DashDock : UserControl
{
    public const int PreferredDockWidth = 196;
    public const int PreferredDockHeight = 48;
    public const int CompactDockWidth = 48;

    private const int SurfaceCornerRadius = 8;

    private readonly ToolTip _toolTip = new();
    private Color _workspaceColor = Theme.Stage;
    private bool _hovered;
    private bool _pressed;
    private bool _compact;

    public DashDock()
    {
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        Font = Theme.UiFont();
        ForeColor = Theme.Text;
        Size = new Size(PreferredDockWidth, PreferredDockHeight);
        MinimumSize = Size;
        MaximumSize = Size;
        TabStop = true;
        AccessibleName = "Workspace color";
        AccessibleDescription = "Open the professional workspace color picker";
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);

        Theme.StyleToolTip(_toolTip);
        SetWorkspaceColor(_workspaceColor);
    }

    public event EventHandler? WorkspaceColorRequested;

    public Color WorkspaceColor => _workspaceColor;

    public bool Compact
    {
        get => _compact;
        set
        {
            var changed = _compact != value;
            _compact = value;
            ApplyResponsiveSize();
            if (changed) Invalidate();
        }
    }

    public void SetWorkspaceColor(Color color)
    {
        color = Color.FromArgb(255, color.R, color.G, color.B);
        _workspaceColor = color;
        var hex = ToHex(color);
        AccessibleDescription = $"Open the professional workspace color picker. Current color {hex}";
        _toolTip.SetToolTip(this, $"{UiLocalization.T("Workspace color")} ({hex})");
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(
        ScaleLogical(Compact ? CompactDockWidth : PreferredDockWidth),
        ScaleLogical(PreferredDockHeight));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var previousSmoothingMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var shadowOffset = ScaleLogical(3);
        var surfaceInset = ScaleLogical(1);
        var trailingInset = ScaleLogical(4);
        var shadowBounds = new Rectangle(
            shadowOffset,
            shadowOffset,
            Math.Max(1, Width - trailingInset),
            Math.Max(1, Height - trailingInset));
        var surfaceBounds = new Rectangle(
            surfaceInset,
            surfaceInset,
            Math.Max(1, Width - trailingInset),
            Math.Max(1, Height - trailingInset));
        using var shadowPath = CreateRoundedRectangle(shadowBounds, ScaleLogical(SurfaceCornerRadius));
        using var surfacePath = CreateRoundedRectangle(surfaceBounds, ScaleLogical(SurfaceCornerRadius));
        using var shadow = new SolidBrush(SystemInformation.HighContrast
            ? Color.Transparent
            : Color.FromArgb(72, Color.Black));
        var surfaceColor = SystemInformation.HighContrast
            ? SystemColors.Control
            : !Enabled
                ? Theme.DisabledSurface
                : _pressed
                    ? Theme.FieldFocus
                    : _hovered
                        ? Theme.PanelHover
                        : Theme.PanelStrong;
        var borderColor = SystemInformation.HighContrast
            ? SystemColors.ControlText
            : !Enabled
                ? Theme.Border
                : Focused
                    ? Theme.Accent
                    : _hovered
                        ? Theme.BorderHover
                        : Theme.Border;
        using var surface = new SolidBrush(surfaceColor);
        using var border = new Pen(borderColor);
        e.Graphics.FillPath(shadow, shadowPath);
        e.Graphics.FillPath(surface, surfacePath);
        e.Graphics.DrawPath(border, surfacePath);

        var swatchSize = ScaleLogical(26);
        var swatchTop = ScaleLogical(11);
        var swatchBounds = Compact
            ? new Rectangle(
                Math.Max(1, (Width - swatchSize) / 2 - surfaceInset),
                swatchTop,
                swatchSize,
                swatchSize)
            : new Rectangle(ScaleLogical(12), swatchTop, swatchSize, swatchSize);
        using var swatch = new SolidBrush(_workspaceColor);
        using var swatchBorder = new Pen(
            SystemInformation.HighContrast
                ? SystemColors.ControlText
                : Theme.ReadableUiColor(_workspaceColor, Theme.BorderHover));
        e.Graphics.FillRectangle(swatch, swatchBounds);
        e.Graphics.DrawRectangle(swatchBorder, swatchBounds);

        e.Graphics.SmoothingMode = previousSmoothingMode;

        if (!Compact)
        {
            var textLeft = swatchBounds.Right + ScaleLogical(10);
            var textWidth = Math.Max(0, Width - textLeft - ScaleLogical(12));
            var titleBounds = new Rectangle(
                textLeft,
                ScaleLogical(5),
                textWidth,
                ScaleLogical(20));
            var titleColor = SystemInformation.HighContrast
                ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                : Theme.ReadableText(surfaceColor, Enabled ? Theme.Text : Theme.DisabledText);
            TextRenderer.DrawText(
                e.Graphics,
                UiLocalization.T("Workspace color"),
                Font,
                titleBounds,
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using var detailFont = Theme.UiFont(8.2f);
            var detailBounds = new Rectangle(
                textLeft,
                ScaleLogical(24),
                textWidth,
                ScaleLogical(18));
            var detailColor = SystemInformation.HighContrast
                ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                : Theme.ReadableText(surfaceColor, Enabled ? Theme.Muted : Theme.DisabledText);
            TextRenderer.DrawText(
                e.Graphics,
                ToHex(_workspaceColor),
                detailFont,
                detailBounds,
                detailColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (Enabled && Focused && ShowFocusCues)
        {
            var focusInset = ScaleLogical(4);
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(surfaceBounds, -focusInset, -focusInset),
                SystemInformation.HighContrast
                    ? SystemColors.ControlText
                    : Theme.ReadableUiColor(surfaceColor, Theme.Accent),
                surfaceColor);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyResponsiveSize();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = Enabled;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        if (!_pressed) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        _pressed = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var activate = Enabled && _pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        _pressed = false;
        Capture = false;
        Invalidate();
        base.OnMouseUp(e);
        if (activate) WorkspaceColorRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!_pressed) return;
        _pressed = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && (e.KeyCode is Keys.Space or Keys.Enter))
        {
            WorkspaceColorRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
        {
            _hovered = false;
            _pressed = false;
            Capture = false;
        }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    private void ApplyResponsiveSize()
    {
        var size = new Size(
            ScaleLogical(Compact ? CompactDockWidth : PreferredDockWidth),
            ScaleLogical(PreferredDockHeight));
        MinimumSize = size;
        MaximumSize = size;
        Size = size;
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
