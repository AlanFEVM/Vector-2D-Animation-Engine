using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

[DefaultEvent(nameof(ValueChanged))]
[DefaultProperty(nameof(Value))]
internal sealed class ModernSlider : Control
{
    private const int DefaultMinimum = 0;
    private const int DefaultMaximum = 10;
    private const int DefaultSmallChange = 1;
    private const int DefaultLargeChange = 5;
    private const int DefaultTickFrequency = 1;

    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private int _minimum = DefaultMinimum;
    private int _maximum = DefaultMaximum;
    private int _value;
    private int _smallChange = DefaultSmallChange;
    private int _largeChange = DefaultLargeChange;
    private int _tickFrequency = DefaultTickFrequency;
    private int _interactionStartValue;
    private int _wheelDelta;
    private float _hoverProgress;
    private float _hoverTarget;
    private float _pressProgress;
    private float _pressTarget;
    private long _lastMotionTimestamp;
    private InteractionSource _interactionSource;

    public ModernSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);

        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        Cursor = Cursors.Hand;
        MinimumSize = LogicalSize(72, 24);
        _motionTimer.Tick += (_, _) => TickMotion();
    }

    [Category("Behavior")]
    [DefaultValue(DefaultMinimum)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value) return;
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            CoerceValue();
            Invalidate();
        }
    }

    [Category("Behavior")]
    [DefaultValue(DefaultMaximum)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value) return;
            _maximum = value;
            if (_minimum > _maximum) _minimum = _maximum;
            CoerceValue();
            Invalidate();
        }
    }

    [Bindable(true)]
    [Category("Behavior")]
    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            if (value < _minimum || value > _maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Value must be between {Minimum} and {Maximum}.");
            }

            SetValueCore(value);
        }
    }

    [Category("Behavior")]
    [DefaultValue(DefaultSmallChange)]
    public int SmallChange
    {
        get => _smallChange;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _smallChange = value;
        }
    }

    [Category("Behavior")]
    [DefaultValue(DefaultLargeChange)]
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _largeChange = value;
        }
    }

    [Category("Appearance")]
    [DefaultValue(DefaultTickFrequency)]
    public int TickFrequency
    {
        get => _tickFrequency;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (_tickFrequency == value) return;
            _tickFrequency = value;
            Invalidate();
        }
    }

    [Category("Action")]
    public event EventHandler? ValueChanged;

    [Category("Action")]
    public event EventHandler? InteractionStarted;

    [Category("Action")]
    public event EventHandler? InteractionCompleted;

    [Category("Action")]
    public event EventHandler? InteractionCanceled;

    protected override Size DefaultSize => LogicalSize(120, 28);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var rail = RailBounds();
        if (rail.Width <= 0 || rail.Height <= 0) return;

        var enabled = Enabled;
        var highContrast = SystemInformation.HighContrast;
        var controlBackground = Theme.EffectiveBackground(this);
        var railColor = highContrast
            ? enabled ? SystemColors.ControlDark : SystemColors.Control
            : enabled
                ? Theme.Mix(Theme.Field, Theme.Border, 0.72f + _hoverProgress * 0.12f)
                : Theme.Mix(Theme.Field, Theme.DisabledSurface, 0.78f);
        var fillColor = highContrast
            ? enabled ? SystemColors.Highlight : SystemColors.GrayText
            : enabled
                ? Theme.Mix(Theme.Accent, Theme.Text, _hoverProgress * 0.08f)
                : Theme.Mix(Theme.DisabledSurface, Theme.Border, 0.45f);
        var thumbColor = highContrast
            ? enabled ? SystemColors.Highlight : SystemColors.GrayText
            : enabled
                ? Theme.Mix(Theme.Accent, Theme.Text, 0.10f + _hoverProgress * 0.08f)
                : Theme.Mix(Theme.DisabledSurface, Theme.Border, 0.58f);

        if (!highContrast)
        {
            railColor = Theme.ReadableUiColor(controlBackground, railColor);
            fillColor = Theme.ReadableUiColor(railColor, fillColor);
            thumbColor = Theme.ReadableUiColor(railColor, thumbColor);
        }

        DrawTicks(graphics, rail, enabled);

        using (var railBrush = new SolidBrush(railColor))
        using (var railPath = RoundedRectangle(rail, rail.Height / 2f))
        {
            graphics.FillPath(railBrush, railPath);
        }

        var thumbX = ValueToPosition(rail);
        var fill = FillBounds(rail, thumbX);
        if (fill.Width > 0)
        {
            using var fillBrush = new SolidBrush(fillColor);
            using var fillPath = RoundedRectangle(fill, fill.Height / 2f);
            graphics.FillPath(fillBrush, fillPath);
        }

        var baseDiameter = Logical(12);
        var hoverGrowth = Logical(2) * _hoverProgress;
        var pressGrowth = Logical(1) * _pressProgress;
        var diameter = Math.Max(Logical(8), baseDiameter + hoverGrowth + pressGrowth);
        var thumbBounds = new RectangleF(
            thumbX - diameter / 2f,
            rail.Top + rail.Height / 2f - diameter / 2f,
            diameter,
            diameter);

        if (Focused && ShowFocusCues && enabled)
        {
            var focusPadding = Logical(3);
            var focusBounds = RectangleF.Inflate(thumbBounds, focusPadding, focusPadding);
            using var focusPen = new Pen(
                highContrast
                    ? SystemColors.Highlight
                    : Theme.ReadableUiColor(controlBackground, Theme.Accent),
                Math.Max(1f, Logical(1)));
            graphics.DrawEllipse(focusPen, focusBounds);
        }

        if (!highContrast)
        {
            using var shadowBrush = new SolidBrush(Color.FromArgb(enabled ? 72 : 36, Color.Black));
            var shadowOffset = Logical(1);
            graphics.FillEllipse(
                shadowBrush,
                thumbBounds.X,
                thumbBounds.Y + shadowOffset,
                thumbBounds.Width,
                thumbBounds.Height);
        }

        using var thumbBrush = new SolidBrush(thumbColor);
        using var thumbBorder = new Pen(
            highContrast
                ? enabled ? SystemColors.HighlightText : SystemColors.GrayText
                : Theme.ReadableUiColor(
                    thumbColor,
                    enabled ? Theme.Mix(Theme.Accent, Theme.Text, 0.24f) : Theme.DisabledText),
            Math.Max(1f, Logical(1)));
        graphics.FillEllipse(thumbBrush, thumbBounds);
        graphics.DrawEllipse(thumbBorder, thumbBounds);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Enabled) return;
        _hoverTarget = 1f;
        StartMotionIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_interactionSource == InteractionSource.Mouse) return;
        _hoverTarget = 0f;
        StartMotionIfNeeded();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;

        Focus();
        CompleteInteractionIfSource(InteractionSource.Keyboard);
        BeginInteraction(InteractionSource.Mouse);
        Capture = true;
        _pressTarget = 1f;
        _pressProgress = 1f;
        SetValueFromPoint(e.X);
        Invalidate();
        StartMotionIfNeeded();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interactionSource != InteractionSource.Mouse || !Capture) return;
        SetValueFromPoint(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _interactionSource != InteractionSource.Mouse) return;
        CompleteInteraction();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _interactionSource == InteractionSource.Mouse) CompleteInteraction();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var consumesWheel = Enabled
            && Focused
            && e.Delta != 0
            && _smallChange != 0
            && _interactionSource != InteractionSource.Mouse;
        if (e is HandledMouseEventArgs handled) handled.Handled = consumesWheel;
        base.OnMouseWheel(e);
        if (!consumesWheel)
        {
            return;
        }

        _wheelDelta += e.Delta;
        var notches = _wheelDelta / SystemInformation.MouseWheelScrollDelta;
        _wheelDelta %= SystemInformation.MouseWheelScrollDelta;
        if (notches == 0) return;

        CompleteInteractionIfSource(InteractionSource.Keyboard);
        var next = ClampToRange((long)_value + (long)notches * _smallChange);
        if (next != _value)
        {
            BeginInteraction(InteractionSource.Wheel);
            SetValueCore(next);
            CompleteInteraction();
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down
                or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Escape => true,
            _ => base.IsInputKey(keyData)
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _interactionSource != InteractionSource.None)
        {
            CancelInteraction();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (!Enabled || !TryGetKeyboardValue(e.KeyCode, out var next))
        {
            base.OnKeyDown(e);
            return;
        }

        if (_interactionSource == InteractionSource.Mouse)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (next != _value)
        {
            if (_interactionSource == InteractionSource.None) BeginInteraction(InteractionSource.Keyboard);
            _pressTarget = 1f;
            SetValueCore(next);
            StartMotionIfNeeded();
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_interactionSource != InteractionSource.Keyboard || !IsSliderKey(e.KeyCode)) return;
        CompleteInteraction();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_interactionSource == InteractionSource.Keyboard) CompleteInteraction();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled && _interactionSource != InteractionSource.None) CancelInteraction();
        _hoverTarget = Enabled && IsPointerInside() ? 1f : 0f;
        if (!Enabled) _pressTarget = 0f;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        StartMotionIfNeeded();
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        MinimumSize = LogicalSize(72, 24);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _motionTimer.Dispose();
        base.Dispose(disposing);
    }

    private void CoerceValue()
    {
        var next = Math.Clamp(_value, _minimum, _maximum);
        SetValueCore(next);
        if (_interactionSource != InteractionSource.None)
        {
            _interactionStartValue = Math.Clamp(_interactionStartValue, _minimum, _maximum);
        }
    }

    private void SetValueCore(int value)
    {
        if (_value == value) return;
        _value = value;
        Invalidate();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetValueFromPoint(int x)
    {
        var rail = RailBounds();
        if (rail.Width <= 0 || _maximum <= _minimum)
        {
            SetValueCore(_minimum);
            return;
        }

        var ratio = Math.Clamp((x - rail.Left) / rail.Width, 0f, 1f);
        if (RightToLeft == RightToLeft.Yes) ratio = 1f - ratio;
        var range = (long)_maximum - _minimum;
        var next = _minimum + (long)Math.Round(range * ratio, MidpointRounding.AwayFromZero);
        SetValueCore(ClampToRange(next));
    }

    private bool TryGetKeyboardValue(Keys key, out int next)
    {
        var small = _smallChange;
        var large = _largeChange;
        var rightToLeftDirection = RightToLeft == RightToLeft.Yes ? -1 : 1;
        long candidate;
        switch (key)
        {
            case Keys.Right:
                candidate = (long)_value + small * rightToLeftDirection;
                break;
            case Keys.Left:
                candidate = (long)_value - small * rightToLeftDirection;
                break;
            case Keys.Up:
                candidate = (long)_value + small;
                break;
            case Keys.Down:
                candidate = (long)_value - small;
                break;
            case Keys.PageUp:
                candidate = (long)_value + large;
                break;
            case Keys.PageDown:
                candidate = (long)_value - large;
                break;
            case Keys.Home:
                candidate = _minimum;
                break;
            case Keys.End:
                candidate = _maximum;
                break;
            default:
                next = _value;
                return false;
        }

        next = ClampToRange(candidate);
        return true;
    }

    private static bool IsSliderKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End;
    }

    private int ClampToRange(long value)
    {
        return (int)Math.Clamp(value, (long)_minimum, _maximum);
    }

    private void BeginInteraction(InteractionSource source)
    {
        if (_interactionSource != InteractionSource.None) return;
        _interactionSource = source;
        _interactionStartValue = _value;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteractionIfSource(InteractionSource source)
    {
        if (_interactionSource == source) CompleteInteraction();
    }

    private void CompleteInteraction()
    {
        if (_interactionSource == InteractionSource.None) return;
        _interactionSource = InteractionSource.None;
        if (Capture) Capture = false;
        _pressTarget = 0f;
        _hoverTarget = Enabled && IsPointerInside() ? 1f : 0f;
        StartMotionIfNeeded();
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (_interactionSource == InteractionSource.None) return;
        var restore = Math.Clamp(_interactionStartValue, _minimum, _maximum);
        _interactionSource = InteractionSource.None;
        if (Capture) Capture = false;
        SetValueCore(restore);
        _pressTarget = 0f;
        _hoverTarget = Enabled && IsPointerInside() ? 1f : 0f;
        StartMotionIfNeeded();
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private RectangleF RailBounds()
    {
        var railHeight = Logical(4);
        var endPadding = Logical(13);
        var left = Math.Min(ClientSize.Width / 2f, endPadding);
        var right = Math.Max(left, ClientSize.Width - endPadding);
        return new RectangleF(
            left,
            (ClientSize.Height - railHeight) / 2f,
            Math.Max(0f, right - left),
            railHeight);
    }

    private float ValueToPosition(RectangleF rail)
    {
        if (_maximum <= _minimum) return RightToLeft == RightToLeft.Yes ? rail.Right : rail.Left;
        var ratio = (_value - (double)_minimum) / (_maximum - (double)_minimum);
        return RightToLeft == RightToLeft.Yes
            ? rail.Right - (float)(ratio * rail.Width)
            : rail.Left + (float)(ratio * rail.Width);
    }

    private RectangleF FillBounds(RectangleF rail, float thumbX)
    {
        return RightToLeft == RightToLeft.Yes
            ? RectangleF.FromLTRB(thumbX, rail.Top, rail.Right, rail.Bottom)
            : RectangleF.FromLTRB(rail.Left, rail.Top, thumbX, rail.Bottom);
    }

    private void DrawTicks(Graphics graphics, RectangleF rail, bool enabled)
    {
        if (_tickFrequency <= 0 || _maximum <= _minimum) return;

        var tickTop = rail.Bottom + Logical(6);
        var tickBottom = tickTop + Logical(2);
        if (tickBottom >= ClientSize.Height) return;

        var range = (long)_maximum - _minimum;
        var nominalCount = range / _tickFrequency + 1;
        var minimumPixelSpacing = Logical(7);
        var maximumVisibleTicks = Math.Max(1L, (long)Math.Floor(rail.Width / minimumPixelSpacing) + 1);
        var skip = Math.Max(1L, (long)Math.Ceiling(nominalCount / (double)maximumVisibleTicks));
        var step = Math.Max(1L, (long)_tickFrequency * skip);
        using var tickPen = new Pen(
            SystemInformation.HighContrast
                ? enabled ? SystemColors.WindowText : SystemColors.GrayText
                : Theme.ReadableUiColor(
                    Theme.EffectiveBackground(this),
                    enabled ? Theme.Border : Theme.DisabledText),
            Math.Max(1f, Logical(1)));

        for (long offset = 0; offset <= range; offset += step)
        {
            var value = _minimum + offset;
            var ratio = offset / (double)range;
            var x = RightToLeft == RightToLeft.Yes
                ? rail.Right - (float)(ratio * rail.Width)
                : rail.Left + (float)(ratio * rail.Width);
            graphics.DrawLine(tickPen, x, tickTop, x, tickBottom);
            if (range - offset < step) break;
        }

        if (range % step != 0)
        {
            var x = RightToLeft == RightToLeft.Yes ? rail.Left : rail.Right;
            graphics.DrawLine(tickPen, x, tickTop, x, tickBottom);
        }
    }

    private void StartMotionIfNeeded()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            _hoverProgress = _hoverTarget;
            _pressProgress = _pressTarget;
            _motionTimer.Stop();
            Invalidate();
            return;
        }

        var hoverSettled = Math.Abs(_hoverProgress - _hoverTarget) < 0.01f;
        var pressSettled = Math.Abs(_pressProgress - _pressTarget) < 0.01f;
        if ((!hoverSettled || !pressSettled) && !_motionTimer.Enabled)
        {
            _lastMotionTimestamp = Stopwatch.GetTimestamp();
            _motionTimer.Start();
        }
    }

    private void TickMotion()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            _hoverProgress = _hoverTarget;
            _pressProgress = _pressTarget;
            _motionTimer.Stop();
            Invalidate();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _lastMotionTimestamp == 0
            ? _motionTimer.Interval
            : Math.Clamp(Stopwatch.GetElapsedTime(_lastMotionTimestamp, now).TotalMilliseconds, 1d, 64d);
        _lastMotionTimestamp = now;
        var timeScale = (float)(elapsedMilliseconds / _motionTimer.Interval);
        var hoverBlend = 1f - MathF.Pow(0.70f, timeScale);
        var pressBlend = 1f - MathF.Pow(0.66f, timeScale);
        _hoverProgress += (_hoverTarget - _hoverProgress) * hoverBlend;
        _pressProgress += (_pressTarget - _pressProgress) * pressBlend;
        var hoverSettled = Math.Abs(_hoverProgress - _hoverTarget) < 0.01f;
        var pressSettled = Math.Abs(_pressProgress - _pressTarget) < 0.01f;
        if (hoverSettled) _hoverProgress = _hoverTarget;
        if (pressSettled) _pressProgress = _pressTarget;
        Invalidate();
        if (hoverSettled && pressSettled) _motionTimer.Stop();
    }

    private bool IsPointerInside()
    {
        return IsHandleCreated && ClientRectangle.Contains(PointToClient(Cursor.Position));
    }

    private int Logical(int value)
    {
        return Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    }

    private Size LogicalSize(int width, int height)
    {
        return new Size(Logical(width), Logical(height));
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        radius = Math.Clamp(radius, 0f, Math.Min(bounds.Width, bounds.Height) / 2f);
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = radius * 2f;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private enum InteractionSource
    {
        None,
        Mouse,
        Keyboard,
        Wheel
    }
}
