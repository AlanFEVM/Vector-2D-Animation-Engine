using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class ModernToggleSwitch : CheckBox
{
    private const int LogicalTrackWidth = 32;
    private const int LogicalTrackHeight = 18;
    private const int LogicalThumbDiameter = 14;
    private const int LogicalTextGap = 8;
    private const int LogicalControlHeight = 28;

    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private float _checkedProgress;
    private float _checkedTarget;
    private float _hoverProgress;
    private float _hoverTarget;
    private float _pressProgress;
    private float _pressTarget;

    public ModernToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);

        AutoSize = true;
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
        CheckAlign = ContentAlignment.MiddleLeft;
        _checkedProgress = _checkedTarget = Checked ? 1f : 0f;
        _motionTimer.Tick += HandleMotionTick;
        Size = GetPreferredSize(Size.Empty);
    }

    protected override Size DefaultSize => new(88, LogicalControlHeight);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var trackWidth = ScaleLogical(LogicalTrackWidth);
        var controlHeight = ScaleLogical(LogicalControlHeight);
        var textGap = string.IsNullOrEmpty(Text) ? 0 : ScaleLogical(LogicalTextGap);
        var textSize = string.IsNullOrEmpty(Text)
            ? Size.Empty
            : TextRenderer.MeasureText(
                UiLocalization.T(Text),
                Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(
            Padding.Horizontal + trackWidth + textGap + textSize.Width,
            Padding.Vertical + Math.Max(controlHeight, textSize.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var trackWidth = ScaleLogical(LogicalTrackWidth);
        var trackHeight = ScaleLogical(LogicalTrackHeight);
        var thumbDiameter = Math.Min(trackHeight - 2, ScaleLogical(LogicalThumbDiameter));
        var contentHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
        var trackLeft = Padding.Left;
        var trackTop = Padding.Top + Math.Max(0, (contentHeight - trackHeight) / 2);
        var trackBounds = new RectangleF(trackLeft, trackTop, trackWidth, trackHeight);

        var colors = ResolveColors();
        var trackColor = Theme.Mix(colors.TrackOff, colors.TrackOn, _checkedProgress);
        var hoverColor = Theme.Mix(colors.TrackOffHover, colors.TrackOnHover, _checkedProgress);
        var pressedColor = Theme.Mix(colors.TrackOffPressed, colors.TrackOnPressed, _checkedProgress);
        trackColor = Theme.Mix(trackColor, hoverColor, _hoverProgress);
        trackColor = Theme.Mix(trackColor, pressedColor, _pressProgress);

        using (var trackBrush = new SolidBrush(trackColor))
        using (var trackBorder = new Pen(colors.Border))
        {
            FillCapsule(graphics, trackBrush, trackBounds);
            DrawCapsule(graphics, trackBorder, trackBounds);
        }

        var inset = Math.Max(1f, (trackHeight - thumbDiameter) / 2f);
        var thumbTravel = Math.Max(0f, trackWidth - thumbDiameter - inset * 2f);
        var thumbLeft = trackBounds.Left + inset + thumbTravel * EaseOut(_checkedProgress);
        var pressedInset = ScaleLogical(1) * _pressProgress * 0.5f;
        var thumbBounds = new RectangleF(
            thumbLeft + pressedInset,
            trackBounds.Top + inset + pressedInset,
            Math.Max(1f, thumbDiameter - pressedInset * 2f),
            Math.Max(1f, thumbDiameter - pressedInset * 2f));
        using (var shadowBrush = new SolidBrush(colors.ThumbShadow))
        using (var thumbBrush = new SolidBrush(colors.Thumb))
        {
            var shadowBounds = thumbBounds;
            shadowBounds.Offset(0, Math.Max(1, ScaleLogical(1)));
            graphics.FillEllipse(shadowBrush, shadowBounds);
            graphics.FillEllipse(thumbBrush, thumbBounds);
        }

        var textGap = ScaleLogical(LogicalTextGap);
        var textLeft = Padding.Left + trackWidth + (string.IsNullOrEmpty(Text) ? 0 : textGap);
        var textBounds = Rectangle.FromLTRB(
            textLeft,
            Padding.Top,
            Math.Max(textLeft, ClientSize.Width - Padding.Right),
            Math.Max(Padding.Top, ClientSize.Height - Padding.Bottom));
        if (!string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(
                graphics,
                Text,
                Font,
                textBounds,
                colors.Text,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        if (Focused && ShowFocusCues)
        {
            var focusBounds = Rectangle.Inflate(ClientRectangle, -ScaleLogical(1), -ScaleLogical(1));
            if (focusBounds.Width > 0 && focusBounds.Height > 0)
            {
                ControlPaint.DrawFocusRectangle(graphics, focusBounds, colors.Text, Parent?.BackColor ?? BackColor);
            }
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        _checkedTarget = Checked ? 1f : 0f;
        StartMotion();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Enabled) return;
        _hoverTarget = 1f;
        StartMotion();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTarget = 0f;
        _pressTarget = 0f;
        StartMotion();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        _pressTarget = 1f;
        StartMotion();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _pressTarget = 0f;
        StartMotion();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        _pressTarget = 0f;
        StartMotion();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled || e.KeyCode != Keys.Space || e.Modifiers != Keys.None) return;
        _pressTarget = 1f;
        StartMotion();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode != Keys.Space) return;
        _pressTarget = 0f;
        StartMotion();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
        {
            _hoverTarget = 0f;
            _pressTarget = 0f;
        }

        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        StartMotion();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _pressTarget = 0f;
        StartMotion();
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        RequestAutoSizeLayout();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RequestAutoSizeLayout();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        RequestAutoSizeLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RequestAutoSizeLayout();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) SnapToTargets();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _motionTimer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motionTimer.Stop();
            _motionTimer.Tick -= HandleMotionTick;
            _motionTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void HandleMotionTick(object? sender, EventArgs e)
    {
        _checkedProgress = Approach(_checkedProgress, _checkedTarget);
        _hoverProgress = Approach(_hoverProgress, _hoverTarget);
        _pressProgress = Approach(_pressProgress, _pressTarget);
        Invalidate();

        if (IsSettled(_checkedProgress, _checkedTarget)
            && IsSettled(_hoverProgress, _hoverTarget)
            && IsSettled(_pressProgress, _pressTarget))
        {
            SnapToTargets();
        }
    }

    private void StartMotion()
    {
        if (IsDisposed || Disposing) return;
        if (!Visible || !IsHandleCreated)
        {
            SnapToTargets();
            Invalidate();
            return;
        }

        if (!_motionTimer.Enabled) _motionTimer.Start();
    }

    private void SnapToTargets()
    {
        _checkedProgress = _checkedTarget;
        _hoverProgress = _hoverTarget;
        _pressProgress = _pressTarget;
        _motionTimer.Stop();
        if (!IsDisposed) Invalidate();
    }

    private void RequestAutoSizeLayout()
    {
        if (!AutoSize || Disposing || IsDisposed) return;
        Parent?.PerformLayout(this, nameof(AutoSize));
        Size = GetPreferredSize(Size.Empty);
    }

    private int ScaleLogical(int value)
    {
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(value * dpi / 96f));
    }

    private ToggleColors ResolveColors()
    {
        if (SystemInformation.HighContrast)
        {
            return Enabled
                ? new ToggleColors(
                    SystemColors.ControlDark,
                    SystemColors.Highlight,
                    SystemColors.HotTrack,
                    SystemColors.HotTrack,
                    SystemColors.Highlight,
                    SystemColors.Highlight,
                    SystemColors.WindowText,
                    SystemColors.Window,
                    Color.FromArgb(80, SystemColors.WindowText),
                    SystemColors.WindowText)
                : new ToggleColors(
                    SystemColors.Control,
                    SystemColors.Control,
                    SystemColors.Control,
                    SystemColors.Control,
                    SystemColors.Control,
                    SystemColors.Control,
                    SystemColors.GrayText,
                    SystemColors.ControlLight,
                    Color.Transparent,
                    SystemColors.GrayText);
        }

        return Enabled
            ? new ToggleColors(
                Theme.PanelStrong,
                Theme.Accent,
                Theme.PanelHover,
                Theme.AccentHoverSurface,
                Theme.Panel,
                Theme.AccentPressedSurface,
                Theme.Border,
                Theme.Text,
                Color.FromArgb(70, Color.Black),
                ForeColor)
            : new ToggleColors(
                Theme.DisabledSurface,
                Theme.DisabledSurface,
                Theme.DisabledSurface,
                Theme.DisabledSurface,
                Theme.DisabledSurface,
                Theme.DisabledSurface,
                Theme.Border,
                Theme.Muted,
                Color.FromArgb(35, Color.Black),
                Theme.Muted);
    }

    private static float Approach(float value, float target)
    {
        value += (target - value) * 0.30f;
        return IsSettled(value, target) ? target : value;
    }

    private static bool IsSettled(float value, float target) => Math.Abs(target - value) < 0.012f;

    private static float EaseOut(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return 1f - MathF.Pow(1f - value, 3f);
    }

    private static void FillCapsule(Graphics graphics, Brush brush, RectangleF bounds)
    {
        using var path = CapsulePath(bounds);
        graphics.FillPath(brush, path);
    }

    private static void DrawCapsule(Graphics graphics, Pen pen, RectangleF bounds)
    {
        using var path = CapsulePath(RectangleF.Inflate(bounds, -0.5f, -0.5f));
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CapsulePath(RectangleF bounds)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;
        var diameter = Math.Min(bounds.Width, bounds.Height);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
        path.CloseFigure();
        return path;
    }

    private readonly record struct ToggleColors(
        Color TrackOff,
        Color TrackOn,
        Color TrackOffHover,
        Color TrackOnHover,
        Color TrackOffPressed,
        Color TrackOnPressed,
        Color Border,
        Color Thumb,
        Color ThumbShadow,
        Color Text);
}
