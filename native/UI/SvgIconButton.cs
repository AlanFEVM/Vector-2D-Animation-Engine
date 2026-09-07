namespace VectorAnimationEngine;

internal sealed class SegmentedButton : Button
{
    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonStateAccessibleObject(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SegmentedButtonNavigation.TryMove(this, e)) return;
        base.OnKeyDown(e);
    }
}

internal class SvgIconButton : Button
{
    private readonly SvgIconBitmapCache _iconCache = new();

    public SvgIconButton(SvgIconKind icon)
    {
        Icon = icon;
        Text = string.Empty;
        Width = Theme.IconButtonSize;
        Height = Theme.IconButtonSize;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public SvgIconKind Icon { get; set; }

    public bool ShowsToolGroupIndicator { get; set; }

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonStateAccessibleObject(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SegmentedButtonNavigation.TryMove(this, e)) return;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var active = UiMotion.IsActive(this);
        var hover = UiMotion.HoverProgress(this);
        var role = Theme.ButtonRoleFor(this);
        var toolbar = role == ButtonVisualRole.Toolbar;
        var borderless = role is ButtonVisualRole.Toolbar or ButtonVisualRole.Segmented;
        var parentBack = Parent?.BackColor ?? Theme.Top;
        using var background = new SolidBrush(BackColor);
        var fillsButton = !borderless || active || hover > 0.01f || Capture;
        var actualBackground = fillsButton ? BackColor : parentBack;
        var foreground = SystemInformation.HighContrast
            ? Enabled ? ForeColor : SystemColors.GrayText
            : Enabled
                ? Theme.ReadableText(actualBackground, ForeColor)
                : Theme.ReadableText(actualBackground, Theme.DisabledText);
        var borderColor = SystemInformation.HighContrast
            ? active ? SystemColors.HighlightText : SystemColors.ControlText
            : Theme.ReadableUiColor(
                actualBackground,
                active ? Theme.Accent : Theme.Mix(Theme.Border, Theme.Accent, hover * 0.42f));
        var borderWidth = Math.Max(1f, DeviceDpi / 96f);
        using var border = new Pen(borderColor, borderWidth);
        e.Graphics.Clear(parentBack);
        if (fillsButton)
        {
            e.Graphics.FillRectangle(background, ClientRectangle);
        }
        if (!borderless || active || hover > 0.05f || Focused)
        {
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        if (toolbar && active)
        {
            using var activeBar = new SolidBrush(SystemInformation.HighContrast ? SystemColors.HighlightText : Theme.Accent);
            var barInset = ScaleLogical(4);
            e.Graphics.FillRectangle(
                activeBar,
                ScaleLogical(1),
                barInset,
                ScaleLogical(2),
                Math.Max(0, Height - barInset * 2));
        }

        var hasLabel = !string.IsNullOrWhiteSpace(Text);
        var iconSlot = hasLabel ? Math.Min(ScaleLogical(Theme.IconButtonSize), Height) : Width;
        var iconBounds = hasLabel
            ? new Rectangle(ScaleLogical(2), 0, iconSlot, Height)
            : ClientRectangle;
        _iconCache.Draw(e.Graphics, Icon, iconBounds, foreground);
        if (hasLabel)
        {
            var textBounds = new Rectangle(
                iconSlot + ScaleLogical(8),
                ScaleLogical(1),
                Math.Max(0, Width - iconSlot - ScaleLogical(14)),
                Math.Max(0, Height - ScaleLogical(2)));
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
            using var indicator = new SolidBrush(
                SystemInformation.HighContrast
                    ? active ? SystemColors.HighlightText : foreground
                    : active ? Theme.Accent : Color.FromArgb(220, foreground));
            var far = ScaleLogical(4);
            var near = ScaleLogical(10);
            e.Graphics.FillPolygon(indicator, new Point[]
            {
                new Point(Width - near, Height - far),
                new Point(Width - far, Height - far),
                new Point(Width - far, Height - near)
            });
        }
        if (Focused && ShowFocusCues)
        {
            var focusInset = ScaleLogical(3);
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(ClientRectangle, -focusInset, -focusInset),
                SystemInformation.HighContrast
                    ? SystemColors.HighlightText
                    : Theme.ReadableUiColor(actualBackground, Theme.AccentLabel),
                actualBackground);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _iconCache.Dispose();
        base.Dispose(disposing);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
}

internal sealed class SvgIconBitmapCache : IDisposable
{
    private Bitmap? _bitmap;
    private SvgIconKind _icon;
    private Size _size;
    private int _colorArgb;
    private float _dpiX;
    private float _dpiY;

    public void Draw(Graphics graphics, SvgIconKind icon, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var size = bounds.Size;
        var colorArgb = color.ToArgb();
        var dpiX = NormalizeDpi(graphics.DpiX);
        var dpiY = NormalizeDpi(graphics.DpiY);
        if (_bitmap is null
            || _icon != icon
            || _size != size
            || _colorArgb != colorArgb
            || Math.Abs(_dpiX - dpiX) > 0.01f
            || Math.Abs(_dpiY - dpiY) > 0.01f)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(
                size.Width,
                size.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            _bitmap.SetResolution(dpiX, dpiY);
            using var iconGraphics = Graphics.FromImage(_bitmap);
            iconGraphics.Clear(Color.Transparent);
            SvgIcons.Draw(iconGraphics, icon, new Rectangle(Point.Empty, size), color);
            _icon = icon;
            _size = size;
            _colorArgb = colorArgb;
            _dpiX = dpiX;
            _dpiY = dpiY;
        }

        graphics.DrawImageUnscaled(_bitmap, bounds.Location);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private static float NormalizeDpi(float dpi) =>
        float.IsFinite(dpi) && dpi > 0f ? Math.Clamp(dpi, 48f, 960f) : 96f;
}

internal sealed class ButtonStateAccessibleObject(Button owner) : Control.ControlAccessibleObject(owner)
{
    public override AccessibleStates State
    {
        get
        {
            var state = base.State;
            if (!UiMotion.IsActive(owner)) return state;
            return owner.AccessibleRole switch
            {
                AccessibleRole.PageTab => state | AccessibleStates.Selected,
                AccessibleRole.RadioButton or AccessibleRole.CheckButton => state | AccessibleStates.Checked,
                _ => state
            };
        }
    }
}

internal static class SegmentedButtonNavigation
{
    public static bool TryMove(Button current, KeyEventArgs e)
    {
        if (current.AccessibleRole != AccessibleRole.RadioButton
            || e.Modifiers != Keys.None
            || current.Parent is null)
        {
            return false;
        }

        var candidates = current.Parent.Controls
            .OfType<Button>()
            .Where(button => button.Visible && button.Enabled && button.AccessibleRole == AccessibleRole.RadioButton);
        if (current.Parent is TableLayoutPanel table)
        {
            var row = table.GetRow(current);
            candidates = candidates.Where(button => table.GetRow(button) == row);
        }

        var buttons = candidates
            .OrderBy(button => button.Top)
            .ThenBy(button => button.Left)
            .ToArray();
        var index = Array.IndexOf(buttons, current);
        if (index < 0 || buttons.Length < 2) return false;

        var targetIndex = e.KeyCode switch
        {
            Keys.Left or Keys.Up => (index - 1 + buttons.Length) % buttons.Length,
            Keys.Right or Keys.Down => (index + 1) % buttons.Length,
            Keys.Home => 0,
            Keys.End => buttons.Length - 1,
            _ => -1
        };
        if (targetIndex < 0) return false;

        buttons[targetIndex].Focus();
        buttons[targetIndex].PerformClick();
        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }
}

internal sealed class SvgToggleButton : CheckBox
{
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private readonly SvgIconBitmapCache _iconCache = new();
    private float _hoverProgress;
    private float _hoverTarget;
    private long _lastMotionTimestamp;

    public SvgToggleButton(SvgIconKind icon, string name)
    {
        Icon = icon;
        Text = string.Empty;
        AccessibleName = name;
        Width = 32;
        Height = 32;
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Empty;
        FlatAppearance.MouseDownBackColor = Color.Empty;
        UseVisualStyleBackColor = false;
        AccessibleRole = AccessibleRole.CheckButton;
        Margin = new Padding(0, 0, 6, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _motionTimer.Tick += (_, _) => TickMotion();
    }

    public SvgIconKind Icon { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentBack = Parent?.BackColor ?? Theme.Panel;
        var baseBack = Checked ? Theme.AccentSurface : parentBack;
        var hoverBack = Checked ? Theme.AccentHoverSurface : Theme.PanelStrong;
        var back = Enabled
            ? Theme.Mix(baseBack, hoverBack, _hoverProgress)
            : Theme.DisabledSurface;
        if (Capture && Enabled) back = Checked ? Theme.AccentPressedSurface : Theme.Panel;
        if (SystemInformation.HighContrast)
        {
            back = Enabled && Checked ? SystemColors.Highlight : SystemColors.Control;
        }
        var fillsButton = Checked || _hoverProgress > 0.01f || Capture;
        var actualBackground = fillsButton ? back : parentBack;
        var fore = SystemInformation.HighContrast
            ? Enabled ? Checked ? SystemColors.HighlightText : SystemColors.ControlText : SystemColors.GrayText
            : Enabled
                ? Theme.ReadableText(actualBackground, Checked ? Theme.AccentLabel : Theme.Text)
                : Theme.ReadableText(actualBackground, Theme.DisabledText);
        using var background = new SolidBrush(back);
        var borderColor = SystemInformation.HighContrast
            ? Checked ? SystemColors.HighlightText : SystemColors.ControlText
            : Theme.ReadableUiColor(
                actualBackground,
                Checked ? Theme.Accent : Theme.Mix(Theme.Border, Theme.Accent, _hoverProgress * 0.45f));
        using var border = new Pen(borderColor);
        e.Graphics.Clear(parentBack);
        if (fillsButton)
        {
            e.Graphics.FillRectangle(background, ClientRectangle);
        }
        if (Checked || _hoverProgress > 0.05f || Focused)
        {
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        if (Checked)
        {
            using var activeBar = new SolidBrush(SystemInformation.HighContrast ? SystemColors.HighlightText : Theme.Accent);
            e.Graphics.FillRectangle(activeBar, 2, Math.Max(0, Height - 3), Math.Max(0, Width - 4), 2);
        }
        _iconCache.Draw(e.Graphics, Icon, ClientRectangle, fore);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(
                e.Graphics,
                Rectangle.Inflate(ClientRectangle, -3, -3),
                SystemInformation.HighContrast
                    ? SystemColors.HighlightText
                    : Theme.ReadableUiColor(actualBackground, Theme.AccentLabel),
                actualBackground);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        SetHoverTarget(1f);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverTarget(0f);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motionTimer.Dispose();
            _iconCache.Dispose();
        }
        base.Dispose(disposing);
    }

    private void TickMotion()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            _hoverProgress = _hoverTarget;
            _motionTimer.Stop();
            Invalidate();
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _lastMotionTimestamp == 0
            ? _motionTimer.Interval
            : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastMotionTimestamp, now).TotalMilliseconds, 1d, 64d);
        _lastMotionTimestamp = now;
        var blend = 1f - MathF.Pow(0.70f, (float)(elapsedMilliseconds / _motionTimer.Interval));
        _hoverProgress += (_hoverTarget - _hoverProgress) * blend;
        if (Math.Abs(_hoverTarget - _hoverProgress) < 0.015f)
        {
            _hoverProgress = _hoverTarget;
            _motionTimer.Stop();
        }

        Invalidate();
    }

    private void SetHoverTarget(float target)
    {
        _hoverTarget = target;
        if (!UiMotion.AnimationsEnabled)
        {
            _hoverProgress = target;
            _motionTimer.Stop();
            Invalidate();
            return;
        }

        _lastMotionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_motionTimer.Enabled) _motionTimer.Start();
    }
}
