using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class ColorSwatchEventArgs : EventArgs
{
    public ColorSwatchEventArgs(Color color)
    {
        Color = color;
    }

    public Color Color { get; }
}

internal sealed class ColorComponentSlider : Control
{
    internal const int InteractionRefreshIntervalMilliseconds = 8;
    private const float SurfaceBumpHeight = 3f;
    private const float SurfaceBumpHalfWidth = 14f;
    private readonly System.Windows.Forms.Timer _interactionRefreshTimer = new()
    {
        Interval = InteractionRefreshIntervalMilliseconds
    };
    private int _minimum;
    private int _maximum = 255;
    private int _value;
    private int _interactionStartValue;
    private float _pointerSurfaceX;
    private bool _interacting;
    private bool _hovered;
    private bool _pointerSurfaceActive;

    public ColorComponentSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        MinimumSize = new Size(70, Theme.ControlHeightCompact);
        AccessibleRole = AccessibleRole.Slider;
        _interactionRefreshTimer.Tick += (_, _) => TickInteractionRefresh();
    }

    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value) return;
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            Value = Math.Clamp(_value, _minimum, _maximum);
            Invalidate();
        }
    }

    [DefaultValue(255)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value) return;
            _maximum = value;
            if (_minimum > _maximum) _minimum = _maximum;
            Value = Math.Clamp(_value, _minimum, _maximum);
            Invalidate();
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, _minimum, _maximum);
            if (_value == next) return;
            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Func<float, Color>? GradientColor { get; set; }

    public event EventHandler? ValueChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void RefreshGradient() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _interactionRefreshTimer.Stop();
            _interactionRefreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rail = RailBounds();
        if (rail.Width <= 0 || rail.Height <= 0) return;

        using var surface = CreateSurfacePath(rail);
        DrawGradient(e.Graphics, rail, surface);
        using (var border = new Pen(_hovered || Focused ? Theme.BorderHover : Theme.Border))
        {
            e.Graphics.DrawPath(border, surface);
        }

        var ratio = _maximum <= _minimum ? 0f : (_value - _minimum) / (float)(_maximum - _minimum);
        var thumbX = rail.Left + ratio * Math.Max(0, rail.Width - 1);
        var thumbSurfaceOffset = _pointerSurfaceActive
            ? ResolvePointerSurfaceOffset(thumbX, _pointerSurfaceX)
            : 0f;
        using var shadow = new Pen(Color.FromArgb(210, Color.Black), 3f);
        using var thumb = new Pen(Color.White, 1f);
        e.Graphics.DrawLine(shadow, thumbX, rail.Top - 2f - thumbSurfaceOffset, thumbX, rail.Bottom + 2f + thumbSurfaceOffset);
        e.Graphics.DrawLine(thumb, thumbX, rail.Top - 2f - thumbSurfaceOffset, thumbX, rail.Bottom + 2f + thumbSurfaceOffset);

        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(rail, 3, 3);
            ControlPaint.DrawFocusRectangle(e.Graphics, focus, Theme.Accent, BackColor);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        UpdateHoverSurface(PointToClient(System.Windows.Forms.Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        if (!_interacting) SetPointerSurface(_pointerSurfaceX, active: false);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        BeginInteraction();
        Capture = true;
        UpdateInteractionFromPointer(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interacting && Capture)
        {
            UpdateInteractionFromPointer(e.Location);
            return;
        }

        UpdateHoverSurface(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_interacting) return;
        CompleteInteraction();
        UpdateHoverSurface(e.Location);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _interacting)
        {
            CompleteInteraction();
            UpdateHoverSurface(PointToClient(System.Windows.Forms.Cursor.Position));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || !Focused || e.Delta == 0) return;
        var direction = Math.Sign(e.Delta);
        var step = (ModifierKeys & Keys.Control) != 0 ? 10 : 1;
        BeginInteraction();
        Value += direction * step;
        CompleteInteraction();
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Escape
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _interacting)
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

        BeginInteraction();
        Value = next;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_interacting && IsAdjustmentKey(e.KeyCode)) CompleteInteraction();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_interacting) CompleteInteraction();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled && _interacting) CancelInteraction();
        if (!Enabled) SetPointerSurface(_pointerSurfaceX, active: false);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private void DrawGradient(Graphics graphics, Rectangle rail, GraphicsPath surface)
    {
        var colorAt = GradientColor;
        if (colorAt is null)
        {
            using var fallback = new SolidBrush(Theme.Accent);
            graphics.FillPath(fallback, surface);
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(surface, CombineMode.Intersect);
        var surfaceBounds = Rectangle.FromLTRB(
            rail.Left,
            rail.Top - (int)MathF.Ceiling(SurfaceBumpHeight),
            rail.Right,
            rail.Bottom + (int)MathF.Ceiling(SurfaceBumpHeight));
        DrawCheckerboard(graphics, surfaceBounds);
        var width = Math.Max(1, rail.Width);
        for (var x = 0; x < width; x++)
        {
            var amount = width <= 1 ? 0f : x / (float)(width - 1);
            using var pen = new Pen(colorAt(amount));
            graphics.DrawLine(pen, rail.Left + x, surfaceBounds.Top, rail.Left + x, surfaceBounds.Bottom - 1);
        }
        graphics.Restore(state);
    }

    private GraphicsPath CreateSurfacePath(Rectangle rail)
    {
        var width = Math.Max(1, rail.Width);
        var points = new PointF[width * 2];
        var bottom = rail.Bottom - 1;
        for (var index = 0; index < width; index++)
        {
            var x = rail.Left + index;
            var offset = _pointerSurfaceActive
                ? ResolvePointerSurfaceOffset(x, _pointerSurfaceX)
                : 0f;
            points[index] = new PointF(x, rail.Top - offset);
            points[width * 2 - 1 - index] = new PointF(x, bottom + offset);
        }

        var path = new GraphicsPath();
        if (width == 1)
        {
            path.AddRectangle(new RectangleF(rail.Left, rail.Top, 1f, Math.Max(1, rail.Height)));
        }
        else
        {
            path.AddPolygon(points);
        }
        return path;
    }

    internal static float ResolvePointerSurfaceOffset(float x, float pointerX)
    {
        var distance = Math.Abs(x - pointerX);
        if (distance >= SurfaceBumpHalfWidth) return 0f;
        var phase = distance / SurfaceBumpHalfWidth * MathF.PI / 2f;
        var influence = MathF.Cos(phase);
        return SurfaceBumpHeight * influence * influence;
    }

    private static void DrawCheckerboard(Graphics graphics, Rectangle bounds)
    {
        const int square = 4;
        using var light = new SolidBrush(Color.FromArgb(255, 94, 100, 104));
        using var dark = new SolidBrush(Color.FromArgb(255, 60, 65, 69));
        for (var y = bounds.Top; y < bounds.Bottom; y += square)
        {
            for (var x = bounds.Left; x < bounds.Right; x += square)
            {
                var alternate = ((x - bounds.Left) / square + (y - bounds.Top) / square) % 2 == 0;
                graphics.FillRectangle(alternate ? light : dark, x, y, Math.Min(square, bounds.Right - x), Math.Min(square, bounds.Bottom - y));
            }
        }
    }

    private Rectangle RailBounds()
    {
        const int horizontalPadding = 7;
        const int height = 14;
        return new Rectangle(
            horizontalPadding,
            Math.Max(0, (ClientSize.Height - height) / 2),
            Math.Max(0, ClientSize.Width - horizontalPadding * 2),
            Math.Min(height, ClientSize.Height));
    }

    private void SetValueFromPoint(int x)
    {
        var rail = RailBounds();
        if (rail.Width <= 1 || _maximum <= _minimum)
        {
            Value = _minimum;
            return;
        }

        var ratio = Math.Clamp((x - rail.Left) / (float)(rail.Width - 1), 0f, 1f);
        Value = _minimum + (int)Math.Round((_maximum - _minimum) * ratio, MidpointRounding.AwayFromZero);
    }

    private void TickInteractionRefresh()
    {
        if (!_interacting || !Capture)
        {
            _interactionRefreshTimer.Stop();
            return;
        }

        UpdateInteractionFromPointer(PointToClient(System.Windows.Forms.Cursor.Position));
    }

    private void UpdateInteractionFromPointer(Point location)
    {
        var rail = RailBounds();
        var pointerX = rail.Width > 0
            ? Math.Clamp(location.X, rail.Left, rail.Right - 1)
            : location.X;
        SetPointerSurface(pointerX, active: true);
        SetValueFromPoint(location.X);
    }

    private void UpdateHoverSurface(Point location)
    {
        if (!Enabled || !ClientRectangle.Contains(location))
        {
            SetPointerSurface(_pointerSurfaceX, active: false);
            return;
        }

        var rail = RailBounds();
        var pointerX = rail.Width > 0
            ? Math.Clamp(location.X, rail.Left, rail.Right - 1)
            : location.X;
        SetPointerSurface(pointerX, active: true);
    }

    private void SetPointerSurface(float pointerX, bool active)
    {
        if (_pointerSurfaceActive == active
            && (!active || Math.Abs(_pointerSurfaceX - pointerX) < 0.05f))
        {
            return;
        }

        _pointerSurfaceX = pointerX;
        _pointerSurfaceActive = active;
        Invalidate();
    }

    private bool TryGetKeyboardValue(Keys key, out int value)
    {
        var small = (ModifierKeys & Keys.Control) != 0 ? 10 : 1;
        var large = Math.Max(small, (_maximum - _minimum) / 10);
        value = key switch
        {
            Keys.Left or Keys.Down => _value - small,
            Keys.Right or Keys.Up => _value + small,
            Keys.PageDown => _value - large,
            Keys.PageUp => _value + large,
            Keys.Home => _minimum,
            Keys.End => _maximum,
            _ => _value
        };
        value = Math.Clamp(value, _minimum, _maximum);
        return IsAdjustmentKey(key);
    }

    private static bool IsAdjustmentKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End;
    }

    private void BeginInteraction()
    {
        if (_interacting) return;
        _interacting = true;
        _interactionStartValue = _value;
        _interactionRefreshTimer.Start();
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _interactionRefreshTimer.Stop();
        if (Capture) Capture = false;
        UpdateHoverSurface(PointToClient(System.Windows.Forms.Cursor.Position));
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _interactionRefreshTimer.Stop();
        if (Capture) Capture = false;
        UpdateHoverSurface(PointToClient(System.Windows.Forms.Cursor.Position));
        Value = _interactionStartValue;
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class HsvColorPlane : Control
{
    private float _hue;
    private float _saturation;
    private float _value = 1f;
    private float _interactionHue;
    private float _interactionSaturation;
    private float _interactionValue;
    private bool _interacting;

    public HsvColorPlane()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Cross;
        TabStop = true;
        MinimumSize = new Size(144, 96);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "HSV color field";
    }

    public float Hue => _hue;
    public float Saturation => _saturation;
    public float Value => _value;

    public event EventHandler? ColorChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetHsv(float hue, float saturation, float value)
    {
        SetHsvCore(hue, saturation, value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var field = FieldBounds();
        if (field.Width <= 1 || field.Height <= 1) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var saturationGradient = new LinearGradientBrush(field, Color.White, ColorFromHue(_hue), LinearGradientMode.Horizontal))
        {
            e.Graphics.FillRectangle(saturationGradient, field);
        }

        using (var valueGradient = new LinearGradientBrush(field, Color.FromArgb(0, Color.Black), Color.Black, LinearGradientMode.Vertical))
        {
            e.Graphics.FillRectangle(valueGradient, field);
        }

        using (var border = new Pen(Focused ? Theme.BorderHover : Theme.Border))
        {
            e.Graphics.DrawRectangle(border, field.X, field.Y, field.Width - 1, field.Height - 1);
        }

        var marker = new PointF(
            field.Left + _saturation * Math.Max(0, field.Width - 1),
            field.Top + (1f - _value) * Math.Max(0, field.Height - 1));
        using var outer = new Pen(Color.FromArgb(230, Color.Black), 3f);
        using var inner = new Pen(Color.White, 1.25f);
        e.Graphics.DrawEllipse(outer, marker.X - 5, marker.Y - 5, 10, 10);
        e.Graphics.DrawEllipse(inner, marker.X - 5, marker.Y - 5, 10, 10);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(field, 2, 2), Theme.Accent, BackColor);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        BeginInteraction();
        Capture = true;
        SetFromPoint(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interacting && Capture) SetFromPoint(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _interacting) CompleteInteraction();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _interacting) CompleteInteraction();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Escape
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _interacting)
        {
            CancelInteraction();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        var step = (ModifierKeys & Keys.Control) != 0 ? 0.1f : 0.01f;
        var saturation = _saturation;
        var value = _value;
        switch (e.KeyCode)
        {
            case Keys.Left:
                saturation -= step;
                break;
            case Keys.Right:
                saturation += step;
                break;
            case Keys.Down:
                value -= step;
                break;
            case Keys.Up:
                value += step;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        BeginInteraction();
        if (SetHsvCore(_hue, saturation, value)) ColorChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_interacting && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) CompleteInteraction();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_interacting) CompleteInteraction();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled && _interacting) CancelInteraction();
        Cursor = Enabled ? Cursors.Cross : Cursors.Default;
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private Rectangle FieldBounds()
    {
        return new Rectangle(1, 1, Math.Max(0, ClientSize.Width - 2), Math.Max(0, ClientSize.Height - 2));
    }

    private void SetFromPoint(Point location)
    {
        var field = FieldBounds();
        if (field.Width <= 1 || field.Height <= 1) return;
        var saturation = Math.Clamp((location.X - field.Left) / (float)(field.Width - 1), 0f, 1f);
        var value = 1f - Math.Clamp((location.Y - field.Top) / (float)(field.Height - 1), 0f, 1f);
        if (SetHsvCore(_hue, saturation, value)) ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SetHsvCore(float hue, float saturation, float value)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        saturation = Math.Clamp(saturation, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        if (Math.Abs(_hue - hue) < 0.0001f
            && Math.Abs(_saturation - saturation) < 0.0001f
            && Math.Abs(_value - value) < 0.0001f)
        {
            return false;
        }

        _hue = hue;
        _saturation = saturation;
        _value = value;
        AccessibleDescription = $"Hue {MathF.Round(_hue)} degrees, saturation {MathF.Round(_saturation * 100)} percent, value {MathF.Round(_value * 100)} percent";
        Invalidate();
        return true;
    }

    private void BeginInteraction()
    {
        if (_interacting) return;
        _interacting = true;
        _interactionHue = _hue;
        _interactionSaturation = _saturation;
        _interactionValue = _value;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        if (Capture) Capture = false;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        if (Capture) Capture = false;
        SetHsvCore(_interactionHue, _interactionSaturation, _interactionValue);
        ColorChanged?.Invoke(this, EventArgs.Empty);
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private static Color ColorFromHue(float hue)
    {
        var chroma = 1f;
        var secondary = chroma * (1f - MathF.Abs(hue / 60f % 2f - 1f));
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, secondary, 0f),
            < 120 => (secondary, chroma, 0f),
            < 180 => (0f, chroma, secondary),
            < 240 => (0f, secondary, chroma),
            < 300 => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary)
        };
        return Color.FromArgb(
            (int)Math.Clamp(MathF.Round(red * 255), 0, 255),
            (int)Math.Clamp(MathF.Round(green * 255), 0, 255),
            (int)Math.Clamp(MathF.Round(blue * 255), 0, 255));
    }
}

internal enum ColorHarmonyMode
{
    Complementary,
    Analogous,
    Triadic,
    SplitComplementary,
    Tetradic
}

/// <summary>
/// An Adobe-style hue ring. The primary marker controls the edited color and
/// the remaining markers preview the active color harmony.
/// </summary>
internal sealed class HarmonyColorWheel : Control
{
    internal const int InteractionRefreshIntervalMilliseconds = 8;
    private const float RingThickness = 18f;
    private const float RingHitPadding = 10f;
    private const float MinimumDragDirectionRadius = 4f;
    private const float SurfaceBumpHeight = 3f;
    private const float SurfaceBumpHalfWidth = 18f;
    private const int SurfaceShaderSegments = 24;
    private Bitmap? _wheelBitmap;
    private int _wheelDiameter;
    private readonly System.Windows.Forms.Timer _interactionRefreshTimer = new()
    {
        Interval = InteractionRefreshIntervalMilliseconds
    };
    private Color _color = Color.FromArgb(79, 179, 162);
    private float _hue = 171f;
    private float _pointerSurfaceHue;
    private Color _interactionStartColor;
    private bool _interacting;
    private bool _hovered;
    private bool _pointerSurfaceActive;
    private ColorHarmonyMode _harmonyMode;

    public HarmonyColorWheel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Cross;
        TabStop = true;
        MinimumSize = new Size(88, 88);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Color harmony wheel";
        _interactionRefreshTimer.Tick += (_, _) => TickInteractionRefresh();
        UpdateAccessibility();
    }

    public Color Color
    {
        get => _color;
        set => SetColor(value);
    }

    public ColorHarmonyMode HarmonyMode
    {
        get => _harmonyMode;
        set
        {
            if (_harmonyMode == value) return;
            _harmonyMode = value;
            UpdateAccessibility();
            Invalidate();
        }
    }

    public Color[] HarmonyColors => CreateHarmonyColors(_color, _harmonyMode, _hue);

    public event EventHandler? ColorChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetColor(Color color)
    {
        if (_color.ToArgb() == color.ToArgb()) return;
        _color = color;
        RgbToHsv(color, out var hue, out var saturation, out _);
        if (saturation > 0.0001f) _hue = hue;
        UpdateAccessibility();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _interactionRefreshTimer.Stop();
            _interactionRefreshTimer.Dispose();
            _wheelBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = WheelBounds();
        if (bounds.Width < 8 || bounds.Height < 8) return;

        EnsureWheelBitmap(bounds.Width);
        if (_wheelBitmap is not null) e.Graphics.DrawImageUnscaled(_wheelBitmap, bounds.Location);
        DrawPointerSurfaceShader(e.Graphics, bounds);

        var center = Center(bounds);
        var innerRadius = Math.Max(1f, bounds.Width / 2f - RingThickness);
        var centerBounds = RectangleF.FromLTRB(
            center.X - innerRadius,
            center.Y - innerRadius,
            center.X + innerRadius,
            center.Y + innerRadius);
        using (var centerBrush = new SolidBrush(Color.FromArgb(255, _color.R, _color.G, _color.B)))
        using (var centerBorder = new Pen(Focused || _hovered ? Theme.BorderHover : Theme.Border))
        {
            e.Graphics.FillEllipse(centerBrush, centerBounds);
            e.Graphics.DrawEllipse(centerBorder, centerBounds);
        }

        var colors = HarmonyColors;
        var offsets = HarmonyOffsets(_harmonyMode);
        for (var index = 0; index < colors.Length; index++)
        {
            DrawHarmonyMarker(e.Graphics, bounds, colors[index], NormalizeHue(_hue + offsets[index]), index == 0);
        }

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, 2, 2), Theme.Accent, BackColor);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left || !TryGetHueFromPoint(e.Location, requireHotZone: true, out var hue)) return;
        Focus();
        BeginInteraction();
        Capture = true;
        Cursor = Cursors.Cross;
        SetPointerSurface(hue, active: true);
        SetHue(hue);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        UpdatePointerCursor(PointToClient(System.Windows.Forms.Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        if (!_interacting)
        {
            Cursor = Enabled ? Cursors.Cross : Cursors.Default;
            SetPointerSurface(_pointerSurfaceHue, active: false);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interacting && Capture)
        {
            UpdateInteractionFromPointer(e.Location);
            return;
        }

        UpdatePointerCursor(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        CompleteInteraction();
        UpdatePointerCursor(e.Location);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _interacting)
        {
            CompleteInteraction();
            UpdatePointerCursor(PointToClient(System.Windows.Forms.Cursor.Position));
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Escape
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _interacting)
        {
            CancelInteraction();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        var step = (ModifierKeys & Keys.Control) != 0 ? 10f : 1f;
        var delta = e.KeyCode switch
        {
            Keys.Left or Keys.Down => -step,
            Keys.Right or Keys.Up => step,
            _ => 0f
        };
        if (Math.Abs(delta) < 0.0001f)
        {
            base.OnKeyDown(e);
            return;
        }

        BeginInteraction();
        SetHue(CurrentHue() + delta);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_interacting && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) CompleteInteraction();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || !Focused || e.Delta == 0) return;
        BeginInteraction();
        SetHue(CurrentHue() + Math.Sign(e.Delta) * ((ModifierKeys & Keys.Control) != 0 ? 10f : 1f));
        CompleteInteraction();
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_interacting) CompleteInteraction();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled && _interacting) CancelInteraction();
        if (!Enabled) SetPointerSurface(_pointerSurfaceHue, active: false);
        Cursor = Enabled ? Cursors.Cross : Cursors.Default;
        base.OnEnabledChanged(e);
        Invalidate();
    }

    internal static Color[] CreateHarmonyColors(Color color, ColorHarmonyMode mode)
    {
        RgbToHsv(color, out var hue, out var saturation, out var value);
        return CreateHarmonyColors(color, mode, hue, saturation, value);
    }

    private static Color[] CreateHarmonyColors(Color color, ColorHarmonyMode mode, float hue)
    {
        RgbToHsv(color, out _, out var saturation, out var value);
        return CreateHarmonyColors(color, mode, hue, saturation, value);
    }

    private static Color[] CreateHarmonyColors(Color color, ColorHarmonyMode mode, float hue, float saturation, float value)
    {
        return HarmonyOffsets(mode)
            .Select(offset => ColorFromHsv(hue + offset, saturation, value, color.A))
            .ToArray();
    }

    private static float[] HarmonyOffsets(ColorHarmonyMode mode) => mode switch
    {
        ColorHarmonyMode.Analogous => [0f, -30f, 30f],
        ColorHarmonyMode.Triadic => [0f, 120f, 240f],
        ColorHarmonyMode.SplitComplementary => [0f, 150f, 210f],
        ColorHarmonyMode.Tetradic => [0f, 90f, 180f, 270f],
        _ => [0f, 180f]
    };

    private Rectangle WheelBounds()
    {
        var size = Math.Max(0, Math.Min(ClientSize.Width, ClientSize.Height) - 8);
        return new Rectangle((ClientSize.Width - size) / 2, (ClientSize.Height - size) / 2, size, size);
    }

    private void EnsureWheelBitmap(int diameter)
    {
        if (_wheelBitmap is not null && _wheelDiameter == diameter) return;
        _wheelBitmap?.Dispose();
        _wheelBitmap = new Bitmap(Math.Max(1, diameter), Math.Max(1, diameter));
        _wheelDiameter = diameter;
        var center = (diameter - 1) / 2f;
        var outerRadius = diameter / 2f;
        var innerRadius = Math.Max(0f, outerRadius - RingThickness);
        for (var y = 0; y < diameter; y++)
        {
            for (var x = 0; x < diameter; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var radius = MathF.Sqrt(dx * dx + dy * dy);
                if (radius < innerRadius || radius > outerRadius) continue;
                var hue = NormalizeHue(MathF.Atan2(dy, dx) * 180f / MathF.PI + 90f);
                _wheelBitmap.SetPixel(x, y, ColorFromHsv(hue, 1f, 1f, 255));
            }
        }
    }

    private void DrawPointerSurfaceShader(Graphics graphics, Rectangle bounds)
    {
        if (!_pointerSurfaceActive || bounds.Width < 8) return;

        var center = Center(bounds);
        var baseRadius = bounds.Width / 2f - 0.5f;
        var innerRadius = Math.Max(1f, baseRadius - 2.5f);
        var outerPoints = new PointF[SurfaceShaderSegments + 1];
        for (var index = 0; index < SurfaceShaderSegments; index++)
        {
            var hue0 = _pointerSurfaceHue - SurfaceBumpHalfWidth
                + SurfaceBumpHalfWidth * 2f * index / SurfaceShaderSegments;
            var hue1 = _pointerSurfaceHue - SurfaceBumpHalfWidth
                + SurfaceBumpHalfWidth * 2f * (index + 1) / SurfaceShaderSegments;
            var outer0 = PointOnHueRadius(center, hue0, ResolvePointerSurfaceRadius(baseRadius, hue0, _pointerSurfaceHue));
            var outer1 = PointOnHueRadius(center, hue1, ResolvePointerSurfaceRadius(baseRadius, hue1, _pointerSurfaceHue));
            var inner0 = PointOnHueRadius(center, hue0, innerRadius);
            var inner1 = PointOnHueRadius(center, hue1, innerRadius);
            outerPoints[index] = outer0;

            using var fill = new SolidBrush(ColorFromHsv((hue0 + hue1) * 0.5f, 1f, 1f, 255));
            graphics.FillPolygon(fill, [inner0, outer0, outer1, inner1]);
            if (index == SurfaceShaderSegments - 1) outerPoints[^1] = outer1;
        }

        using var highlight = new Pen(Color.FromArgb(150, Color.White), 1.15f);
        graphics.DrawLines(highlight, outerPoints);
    }

    private static PointF PointOnHueRadius(PointF center, float hue, float radius)
    {
        var radians = (hue - 90f) * MathF.PI / 180f;
        return new PointF(
            center.X + MathF.Cos(radians) * radius,
            center.Y + MathF.Sin(radians) * radius);
    }

    internal static float ResolvePointerSurfaceRadius(float baseRadius, float hue, float pointerHue)
    {
        var distance = CircularHueDistance(hue, pointerHue);
        if (distance >= SurfaceBumpHalfWidth) return baseRadius;
        var phase = distance / SurfaceBumpHalfWidth * MathF.PI / 2f;
        var influence = MathF.Cos(phase);
        return baseRadius + SurfaceBumpHeight * influence * influence;
    }

    private void DrawHarmonyMarker(Graphics graphics, Rectangle bounds, Color color, float hue, bool primary)
    {
        var center = Center(bounds);
        var radius = Math.Max(1f, bounds.Width / 2f - RingThickness / 2f);
        var radians = (hue - 90f) * MathF.PI / 180f;
        var point = new PointF(
            center.X + MathF.Cos(radians) * radius,
            center.Y + MathF.Sin(radians) * radius);
        var size = primary ? 11f : 8f;
        var marker = new RectangleF(point.X - size / 2f, point.Y - size / 2f, size, size);
        using var fill = new SolidBrush(Color.FromArgb(255, color.R, color.G, color.B));
        using var outer = new Pen(Color.FromArgb(230, Color.Black), primary ? 3f : 2.5f);
        using var inner = new Pen(primary ? Theme.AccentLabel : Color.White, primary ? 1.5f : 1f);
        graphics.FillEllipse(fill, marker);
        graphics.DrawEllipse(outer, marker);
        graphics.DrawEllipse(inner, marker);
    }

    private bool TryGetHueFromPoint(Point point, bool requireHotZone, out float hue)
    {
        return TryResolvePointerHue(point, WheelBounds(), requireHotZone, out hue);
    }

    internal static bool TryResolvePointerHue(
        Point point,
        Rectangle bounds,
        bool requireHotZone,
        out float hue)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            hue = 0f;
            return false;
        }

        var center = Center(bounds);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var radius = MathF.Sqrt(dx * dx + dy * dy);
        if (radius < MinimumDragDirectionRadius)
        {
            hue = 0f;
            return false;
        }

        var outerRadius = bounds.Width / 2f;
        var innerRadius = Math.Max(
            MinimumDragDirectionRadius,
            outerRadius - RingThickness - RingHitPadding);
        if (requireHotZone && (radius < innerRadius || radius > outerRadius + RingHitPadding))
        {
            hue = 0f;
            return false;
        }
        hue = NormalizeHue(MathF.Atan2(dy, dx) * 180f / MathF.PI + 90f);
        return true;
    }

    private void UpdatePointerCursor(Point location)
    {
        if (!Enabled)
        {
            Cursor = Cursors.Default;
            return;
        }

        if (TryGetHueFromPoint(location, requireHotZone: true, out var hue))
        {
            Cursor = Cursors.Hand;
            SetPointerSurface(hue, active: true);
        }
        else
        {
            Cursor = Cursors.Cross;
            SetPointerSurface(_pointerSurfaceHue, active: false);
        }
    }

    private void TickInteractionRefresh()
    {
        if (!_interacting || !Capture)
        {
            _interactionRefreshTimer.Stop();
            return;
        }

        UpdateInteractionFromPointer(PointToClient(System.Windows.Forms.Cursor.Position));
    }

    private void UpdateInteractionFromPointer(Point location)
    {
        if (!TryGetHueFromPoint(location, requireHotZone: false, out var hue)) return;
        SetPointerSurface(hue, active: true);
        SetHue(hue);
    }

    private void SetPointerSurface(float hue, bool active)
    {
        hue = NormalizeHue(hue);
        if (_pointerSurfaceActive == active
            && (!active || CircularHueDistance(_pointerSurfaceHue, hue) < 0.05f))
        {
            return;
        }

        _pointerSurfaceActive = active;
        _pointerSurfaceHue = hue;
        Invalidate();
    }

    private void SetHue(float hue)
    {
        _hue = NormalizeHue(hue);
        RgbToHsv(_color, out _, out var saturation, out var value);
        var next = ColorFromHsv(_hue, saturation, value, _color.A);
        if (_color.ToArgb() == next.ToArgb())
        {
            UpdateAccessibility();
            Invalidate();
            return;
        }
        _color = next;
        UpdateAccessibility();
        Invalidate();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private float CurrentHue() => _hue;

    private static PointF Center(Rectangle bounds) => new(bounds.Left + (bounds.Width - 1) / 2f, bounds.Top + (bounds.Height - 1) / 2f);

    private void BeginInteraction()
    {
        if (_interacting) return;
        _interacting = true;
        _interactionStartColor = _color;
        _interactionRefreshTimer.Start();
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _interactionRefreshTimer.Stop();
        if (Capture) Capture = false;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _interactionRefreshTimer.Stop();
        if (Capture) Capture = false;
        SetColor(_interactionStartColor);
        ColorChanged?.Invoke(this, EventArgs.Empty);
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAccessibility()
    {
        AccessibleDescription = $"{_harmonyMode} harmony. Hue {MathF.Round(CurrentHue())} degrees. Use the ring, arrow keys, or mouse wheel to adjust the primary hue.";
    }

    private static float NormalizeHue(float hue)
    {
        hue %= 360f;
        return hue < 0f ? hue + 360f : hue;
    }

    private static float CircularHueDistance(float first, float second)
    {
        var distance = Math.Abs(NormalizeHue(first) - NormalizeHue(second));
        return Math.Min(distance, 360f - distance);
    }

    private static void RgbToHsv(Color color, out float hue, out float saturation, out float value)
    {
        var red = color.R / 255f;
        var green = color.G / 255f;
        var blue = color.B / 255f;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        hue = delta < 0.0001f
            ? 0f
            : maximum == red
                ? 60f * ((green - blue) / delta % 6f)
                : maximum == green
                    ? 60f * ((blue - red) / delta + 2f)
                    : 60f * ((red - green) / delta + 4f);
        hue = NormalizeHue(hue);
        saturation = maximum < 0.0001f ? 0f : delta / maximum;
        value = maximum;
    }

    private static Color ColorFromHsv(float hue, float saturation, float value, int alpha)
    {
        hue = NormalizeHue(hue);
        saturation = Math.Clamp(saturation, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        var chroma = value * saturation;
        var secondary = chroma * (1f - MathF.Abs(hue / 60f % 2f - 1f));
        var offset = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60f => (chroma, secondary, 0f),
            < 120f => (secondary, chroma, 0f),
            < 180f => (0f, chroma, secondary),
            < 240f => (0f, secondary, chroma),
            < 300f => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary)
        };
        return Color.FromArgb(
            alpha,
            (int)Math.Clamp(MathF.Round((red + offset) * 255f), 0, 255),
            (int)Math.Clamp(MathF.Round((green + offset) * 255f), 0, 255),
            (int)Math.Clamp(MathF.Round((blue + offset) * 255f), 0, 255));
    }
}

internal sealed class GradientStopSelectedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

internal sealed class GradientStopsChangedEventArgs(GradientStop[] stops, int selectedIndex) : EventArgs
{
    public GradientStop[] Stops { get; } = stops.ToArray();
    public int SelectedIndex { get; } = selectedIndex;
}

internal sealed class GradientPreset
{
    public GradientPreset(string name, GradientKind kind, IReadOnlyList<GradientStop> stops, bool isUserSaved = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (kind == GradientKind.Solid) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(stops);
        Name = name;
        Kind = kind;
        Stops = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .ToArray();
        if (Stops.Length < 2) throw new ArgumentException("A gradient preset needs at least two color stops.", nameof(stops));
        IsUserSaved = isUserSaved;
    }

    public string Name { get; }
    public GradientKind Kind { get; }
    public GradientStop[] Stops { get; }
    public bool IsUserSaved { get; }
}

internal sealed class GradientPresetSelectedEventArgs(GradientPreset preset) : EventArgs
{
    public GradientPreset Preset { get; } = preset;
}

internal static class GradientPreviewRenderer
{
    public static void Draw(Graphics graphics, Rectangle bounds, GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var normalized = Normalize(stops);
        if (kind == GradientKind.Solid)
        {
            using var solid = new SolidBrush(Color.FromArgb(normalized[0].Argb));
            graphics.FillRectangle(solid, bounds);
            return;
        }

        if (kind == GradientKind.Linear)
        {
            using var brush = new LinearGradientBrush(bounds, Color.Black, Color.White, LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = normalized.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
                    Positions = normalized.Select(stop => stop.Position).ToArray()
                }
            };
            graphics.FillRectangle(brush, bounds);
            return;
        }

        if (kind == GradientKind.ShapeRadial)
        {
            var path = new GraphicsPath(FillMode.Winding);
            path.AddPolygon(
            [
                new PointF(bounds.Left + bounds.Width * 0.08f, bounds.Top + bounds.Height * 0.42f),
                new PointF(bounds.Left + bounds.Width * 0.28f, bounds.Top + bounds.Height * 0.08f),
                new PointF(bounds.Left + bounds.Width * 0.86f, bounds.Top + bounds.Height * 0.18f),
                new PointF(bounds.Left + bounds.Width * 0.96f, bounds.Top + bounds.Height * 0.58f),
                new PointF(bounds.Left + bounds.Width * 0.62f, bounds.Top + bounds.Height * 0.92f),
                new PointF(bounds.Left + bounds.Width * 0.16f, bounds.Top + bounds.Height * 0.82f)
            ]);
            using (path)
            using (var brush = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(bounds.Left + bounds.Width * 0.48f, bounds.Top + bounds.Height * 0.5f),
                InterpolationColors = new ColorBlend
                {
                    Colors = normalized.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
                    Positions = normalized.Select(stop => stop.Position).ToArray()
                }
            })
            {
                graphics.FillPath(brush, path);
            }
            return;
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        var centerX = (bounds.Width - 1) / 2f;
        var centerY = (bounds.Height - 1) / 2f;
        var maximumRadius = Math.Max(1f, MathF.Sqrt(centerX * centerX + centerY * centerY));
        for (var y = 0; y < bounds.Height; y++)
        {
            for (var x = 0; x < bounds.Width; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                var position = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / maximumRadius, 0f, 1f);
                bitmap.SetPixel(x, y, Sample(normalized, position));
            }
        }
        graphics.DrawImageUnscaled(bitmap, bounds.Location);
    }

    public static Color Sample(IReadOnlyList<GradientStop> stops, float position)
    {
        var normalized = Normalize(stops);
        position = Math.Clamp(position, 0f, 1f);
        if (position <= normalized[0].Position) return Color.FromArgb(normalized[0].Argb);
        for (var index = 1; index < normalized.Length; index++)
        {
            var after = normalized[index];
            if (position > after.Position) continue;
            var before = normalized[index - 1];
            var amount = (position - before.Position) / Math.Max(0.0001f, after.Position - before.Position);
            return Lerp(Color.FromArgb(before.Argb), Color.FromArgb(after.Argb), amount);
        }
        return Color.FromArgb(normalized[^1].Argb);
    }

    private static GradientStop[] Normalize(IReadOnlyList<GradientStop> stops)
    {
        var normalized = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .ToList();
        if (normalized.Count == 0) normalized.Add(new GradientStop(0, Color.White));
        if (normalized.Count == 1) normalized.Add(new GradientStop(1, normalized[0].Argb));
        if (normalized[0].Position > 0f) normalized.Insert(0, new GradientStop(0, normalized[0].Argb));
        if (normalized[^1].Position < 1f) normalized.Add(new GradientStop(1, normalized[^1].Argb));
        return normalized.ToArray();
    }

    private static Color Lerp(Color start, Color end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(start.A + (end.A - start.A) * amount),
            (int)MathF.Round(start.R + (end.R - start.R) * amount),
            (int)MathF.Round(start.G + (end.G - start.G) * amount),
            (int)MathF.Round(start.B + (end.B - start.B) * amount));
    }
}

internal sealed class GradientPresetGrid : Control
{
    private const int Gap = 4;
    private readonly List<GradientPreset> _presets = [];
    private int _hoverIndex = -1;
    private int _selectedIndex = -1;
    private int _focusIndex = -1;

    public GradientPresetGrid()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        TabStop = true;
        MinimumSize = new Size(96, 42);
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Gradient presets";
    }

    public event EventHandler<GradientPresetSelectedEventArgs>? PresetSelected;
    public event EventHandler<GradientPresetSelectedEventArgs>? HoveredPresetChanged;
    public event EventHandler? HoverCleared;

    public void SetPresets(IEnumerable<GradientPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        _presets.Clear();
        _presets.AddRange(presets);
        _hoverIndex = -1;
        _selectedIndex = Math.Clamp(_selectedIndex, -1, _presets.Count - 1);
        _focusIndex = Math.Clamp(_focusIndex, _presets.Count == 0 ? -1 : 0, _presets.Count - 1);
        Invalidate();
    }

    public void SetSelection(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        var selected = _presets.FindIndex(preset => preset.Kind == kind && preset.Stops.SequenceEqual(stops));
        if (_selectedIndex == selected) return;
        _selectedIndex = selected;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        for (var index = 0; index < _presets.Count; index++) DrawPreset(e.Graphics, index, _presets[index]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var index = HitTest(e.Location);
        if ((uint)index >= (uint)_presets.Count) return;
        Focus();
        Select(index);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter or Keys.Space
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_presets.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            Select(Math.Clamp(_focusIndex, 0, _presets.Count - 1));
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        var columns = ColumnCount();
        var next = e.KeyCode switch
        {
            Keys.Left => _focusIndex - 1,
            Keys.Right => _focusIndex + 1,
            Keys.Up => _focusIndex - columns,
            Keys.Down => _focusIndex + columns,
            _ => _focusIndex
        };
        if (next == _focusIndex)
        {
            base.OnKeyDown(e);
            return;
        }

        _focusIndex = Math.Clamp(next, 0, _presets.Count - 1);
        Invalidate();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focusIndex < 0 && _presets.Count > 0) _focusIndex = 0;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void DrawPreset(Graphics graphics, int index, GradientPreset preset)
    {
        var bounds = PresetBounds(index);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var preview = Rectangle.Inflate(bounds, -2, -2);
        GradientPreviewRenderer.Draw(graphics, preview, preset.Kind, preset.Stops);
        var selected = index == _selectedIndex;
        var focused = Focused && index == _focusIndex;
        var hovered = index == _hoverIndex;
        using var border = new Pen(selected ? Theme.Accent : hovered || focused ? Theme.Text : Theme.Border, selected ? 2f : 1f);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (preset.Kind is GradientKind.Radial or GradientKind.ShapeRadial)
        {
            var radius = Math.Max(2, Math.Min(4, preview.Height / 3));
            var center = new Rectangle(preview.Left + preview.Width / 2 - radius, preview.Top + preview.Height / 2 - radius, radius * 2, radius * 2);
            using var marker = new Pen(Color.FromArgb(220, Theme.Text));
            graphics.DrawEllipse(marker, center);
        }
    }

    private int HitTest(Point location)
    {
        for (var index = 0; index < _presets.Count; index++)
        {
            if (PresetBounds(index).Contains(location)) return index;
        }
        return -1;
    }

    private Rectangle PresetBounds(int index)
    {
        var columns = ColumnCount();
        var rows = Math.Max(1, (int)Math.Ceiling(_presets.Count / (double)columns));
        var cellWidth = Math.Max(1, (ClientSize.Width - Gap * (columns - 1)) / columns);
        var cellHeight = Math.Max(1, (ClientSize.Height - Gap * (rows - 1)) / rows);
        var column = index % columns;
        var row = index / columns;
        return new Rectangle(column * (cellWidth + Gap), row * (cellHeight + Gap), cellWidth, cellHeight);
    }

    private int ColumnCount() => Math.Max(1, Math.Min(3, (ClientSize.Width + Gap) / 46));

    private void Select(int index)
    {
        _selectedIndex = index;
        _focusIndex = index;
        AccessibleDescription = $"{_presets[index].Name} {_presets[index].Kind} gradient preset";
        Invalidate();
        PresetSelected?.Invoke(this, new GradientPresetSelectedEventArgs(_presets[index]));
    }

    private void SetHovered(int index)
    {
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        Invalidate();
        if ((uint)index < (uint)_presets.Count) HoveredPresetChanged?.Invoke(this, new GradientPresetSelectedEventArgs(_presets[index]));
        else HoverCleared?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A compact, Adobe-style color-stop rail. End stops stay anchored while inner stops can be dragged.</summary>
internal sealed class GradientStopStrip : Control
{
    private GradientStop[] _stops = [new GradientStop(0, Color.Black), new GradientStop(1, Color.White)];
    private int _selectedIndex;
    private int _dragIndex = -1;
    private bool _interactionActive;
    private GradientStop[] _interactionStartStops = [];
    private int _interactionStartSelected;
    private bool _hovered;

    public GradientStopStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    public event EventHandler<GradientStopSelectedEventArgs>? StopSelected;
    public event EventHandler<GradientStopsChangedEventArgs>? StopsChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public void SetStops(IReadOnlyList<GradientStop> stops, int selectedIndex)
    {
        var normalized = Normalize(stops);
        var nextSelected = Math.Clamp(selectedIndex, 0, normalized.Length - 1);
        if (_stops.SequenceEqual(normalized) && _selectedIndex == nextSelected) return;
        _stops = normalized;
        _selectedIndex = nextSelected;
        AccessibleDescription = $"Gradient stop {nextSelected + 1} of {_stops.Length}, {_stops[nextSelected].Position * 100:0}%";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        var rail = RailBounds;
        DrawChecker(e.Graphics, rail);
        using (var brush = CreateGradientBrush(rail)) e.Graphics.FillRectangle(brush, rail);
        using (var border = new Pen(_hovered || Focused ? Theme.BorderHover : Theme.Border)) e.Graphics.DrawRectangle(border, rail);

        for (var index = 0; index < _stops.Length; index++) DrawStop(e.Graphics, index, StopPoint(index));
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), Theme.Text, BackColor);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        var hit = HitTestStop(e.Location);
        if (hit >= 0)
        {
            Select(hit);
            _dragIndex = hit;
            BeginInteraction();
            Capture = true;
            return;
        }

        if (!RailBounds.Contains(e.Location)) return;
        var position = PositionAt(e.X);
        var insertion = Array.FindIndex(_stops, stop => stop.Position > position);
        if (insertion <= 0 || insertion >= _stops.Length) return;
        var before = _stops[insertion - 1];
        var after = _stops[insertion];
        var amount = (position - before.Position) / Math.Max(0.0001f, after.Position - before.Position);
        var list = _stops.ToList();
        list.Insert(insertion, new GradientStop(position, Lerp(Color.FromArgb(before.Argb), Color.FromArgb(after.Argb), amount)));
        _stops = list.ToArray();
        _selectedIndex = insertion;
        _dragIndex = insertion;
        BeginInteraction();
        RaiseStopsChanged();
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex <= 0 || _dragIndex >= _stops.Length - 1 || !Capture) return;
        var minimum = _stops[_dragIndex - 1].Position + 0.01f;
        var maximum = _stops[_dragIndex + 1].Position - 0.01f;
        var position = Math.Clamp(PositionAt(e.X), minimum, maximum);
        if (Math.Abs(position - _stops[_dragIndex].Position) < 0.0001f) return;
        _stops[_dragIndex] = new GradientStop(position, _stops[_dragIndex].Argb);
        RaiseStopsChanged();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        EndInteraction();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _interactionActive) EndInteraction();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Delete or Keys.Back || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyCode == Keys.Escape && _interactionActive)
        {
            _stops = _interactionStartStops;
            _selectedIndex = _interactionStartSelected;
            _interactionActive = false;
            _dragIndex = -1;
            if (Capture) Capture = false;
            Invalidate();
            InteractionCanceled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode is Keys.Delete or Keys.Back)
        {
            if (_selectedIndex > 0 && _selectedIndex < _stops.Length - 1)
            {
                _stops = _stops.Where((_, index) => index != _selectedIndex).ToArray();
                _selectedIndex--;
                RaiseStopsChanged();
            }
            e.Handled = true;
            return;
        }

        if (e.KeyCode is Keys.Left or Keys.Right && _selectedIndex > 0 && _selectedIndex < _stops.Length - 1)
        {
            var delta = e.KeyCode == Keys.Left ? -0.01f : 0.01f;
            var minimum = _stops[_selectedIndex - 1].Position + 0.01f;
            var maximum = _stops[_selectedIndex + 1].Position - 0.01f;
            var position = Math.Clamp(_stops[_selectedIndex].Position + delta, minimum, maximum);
            if (Math.Abs(position - _stops[_selectedIndex].Position) >= 0.0001f)
            {
                _stops[_selectedIndex] = new GradientStop(position, _stops[_selectedIndex].Argb);
                RaiseStopsChanged();
            }
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private Rectangle RailBounds => new(8, 16, Math.Max(1, Width - 16), Math.Max(12, Math.Min(20, Height - 28)));

    private Point StopPoint(int index)
    {
        var rail = RailBounds;
        return new Point(rail.Left + (int)MathF.Round(_stops[index].Position * rail.Width), rail.Bottom + 7);
    }

    private int HitTestStop(Point point)
    {
        var best = -1;
        var bestDistance = 9 * 9;
        for (var index = 0; index < _stops.Length; index++)
        {
            var stop = StopPoint(index);
            var dx = point.X - stop.X;
            var dy = point.Y - stop.Y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private float PositionAt(int x)
    {
        var rail = RailBounds;
        return Math.Clamp((x - rail.Left) / (float)Math.Max(1, rail.Width), 0f, 1f);
    }

    private void Select(int index)
    {
        if (_selectedIndex == index) return;
        _selectedIndex = index;
        AccessibleDescription = $"Gradient stop {index + 1} of {_stops.Length}, {_stops[index].Position * 100:0}%";
        Invalidate();
        StopSelected?.Invoke(this, new GradientStopSelectedEventArgs(index));
    }

    private void BeginInteraction()
    {
        if (_interactionActive) return;
        _interactionActive = true;
        _interactionStartStops = _stops.ToArray();
        _interactionStartSelected = _selectedIndex;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndInteraction()
    {
        var wasActive = _interactionActive;
        _interactionActive = false;
        _dragIndex = -1;
        if (Capture) Capture = false;
        if (!wasActive) return;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStopsChanged()
    {
        AccessibleDescription = $"Gradient stop {_selectedIndex + 1} of {_stops.Length}, {_stops[_selectedIndex].Position * 100:0}%";
        Invalidate();
        StopsChanged?.Invoke(this, new GradientStopsChangedEventArgs(_stops, _selectedIndex));
    }

    private LinearGradientBrush CreateGradientBrush(Rectangle rail)
    {
        var brush = new LinearGradientBrush(rail, Color.Black, Color.White, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = _stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
            Positions = _stops.Select(stop => stop.Position).ToArray()
        };
        return brush;
    }

    private void DrawStop(Graphics graphics, int index, Point point)
    {
        var points = new[]
        {
            new Point(point.X, point.Y - 6),
            new Point(point.X - 6, point.Y + 1),
            new Point(point.X + 6, point.Y + 1)
        };
        using var fill = new SolidBrush(Color.FromArgb(_stops[index].Argb));
        using var outline = new Pen(index == _selectedIndex ? Theme.Accent : Theme.Text, index == _selectedIndex ? 2f : 1f);
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(outline, points);
    }

    private static GradientStop[] Normalize(IReadOnlyList<GradientStop> stops)
    {
        var normalized = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .GroupBy(stop => stop.Position)
            .Select(group => group.Last())
            .ToList();
        if (normalized.Count == 0) normalized.Add(new GradientStop(0, Color.Black));
        if (normalized.Count == 1) normalized.Add(new GradientStop(1, normalized[0].Argb));
        if (normalized[0].Position > 0) normalized.Insert(0, new GradientStop(0, normalized[0].Argb));
        if (normalized[^1].Position < 1) normalized.Add(new GradientStop(1, normalized[^1].Argb));
        return normalized.ToArray();
    }

    private static Color Lerp(Color start, Color end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(start.A + (end.A - start.A) * amount),
            (int)MathF.Round(start.R + (end.R - start.R) * amount),
            (int)MathF.Round(start.G + (end.G - start.G) * amount),
            (int)MathF.Round(start.B + (end.B - start.B) * amount));
    }

    private static void DrawChecker(Graphics graphics, Rectangle bounds)
    {
        const int checker = 5;
        using var light = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var dark = new SolidBrush(Color.FromArgb(150, 150, 150));
        for (var y = bounds.Top; y < bounds.Bottom; y += checker)
        {
            for (var x = bounds.Left; x < bounds.Right; x += checker)
            {
                var brush = ((x - bounds.Left) / checker + (y - bounds.Top) / checker) % 2 == 0 ? light : dark;
                graphics.FillRectangle(brush, x, y, Math.Min(checker, bounds.Right - x), Math.Min(checker, bounds.Bottom - y));
            }
        }
    }
}

internal sealed class ColorTargetButton : Control
{
    private bool _selected;
    private bool _hovered;
    private Color _swatchColor = Color.White;
    private GradientKind _gradientKind = GradientKind.Solid;
    private GradientStop[] _gradientStops = [new GradientStop(0, Color.White), new GradientStop(1, Color.White)];
    private string _detailText = string.Empty;

    public ColorTargetButton(string text)
    {
        Text = text;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.PanelStrong;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public Color SwatchColor
    {
        get => _swatchColor;
        set
        {
            if (_swatchColor.ToArgb() == value.ToArgb()) return;
            _swatchColor = value;
            Invalidate();
        }
    }

    public void SetGradientPreview(GradientKind kind, IReadOnlyList<GradientStop> stops)
    {
        var normalized = stops.ToArray();
        if (_gradientKind == kind && _gradientStops.SequenceEqual(normalized)) return;
        _gradientKind = kind;
        _gradientStops = normalized.Length > 0 ? normalized : [new GradientStop(0, _swatchColor), new GradientStop(1, _swatchColor)];
        Invalidate();
    }

    [DefaultValue("")]
    public string DetailText
    {
        get => _detailText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_detailText, value, StringComparison.Ordinal)) return;
            _detailText = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var background = Selected ? Theme.AccentSurface : _hovered ? Theme.PanelHover : Theme.PanelStrong;
        var borderColor = Selected ? Theme.Accent : _hovered ? Theme.BorderHover : Theme.Border;
        using var backgroundBrush = new SolidBrush(background);
        using var border = new Pen(borderColor);
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        var swatchSize = Math.Clamp(Height - 18, 20, 30);
        var swatch = new Rectangle(8, Math.Max(5, (Height - swatchSize) / 2), swatchSize, swatchSize);
        DrawChecker(e.Graphics, swatch);
        if (_gradientKind == GradientKind.Solid)
        {
            using var colorBrush = new SolidBrush(SwatchColor);
            e.Graphics.FillRectangle(colorBrush, swatch);
        }
        else
        {
            GradientPreviewRenderer.Draw(e.Graphics, swatch, _gradientKind, _gradientStops);
        }
        using (var swatchBorder = new Pen(Theme.BorderHover)) e.Graphics.DrawRectangle(swatchBorder, swatch);

        var textLeft = swatch.Right + 8;
        var textWidth = Math.Max(0, Width - textLeft - 8);
        var titleBounds = new Rectangle(textLeft, 5, textWidth, Math.Max(0, Height / 2 - 3));
        TextRenderer.DrawText(
            e.Graphics,
            UiLocalization.T(Text),
            Font,
            titleBounds,
            Selected ? Theme.AccentLabel : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (!string.IsNullOrWhiteSpace(DetailText))
        {
            var detailBounds = new Rectangle(textLeft, Height / 2 - 1, textWidth, Math.Max(0, Height / 2 - 4));
            using var detailFont = Theme.UiFont(8.2f);
            TextRenderer.DrawText(
                e.Graphics,
                UiLocalization.T(DetailText),
                detailFont,
                detailBounds,
                Selected ? Theme.AccentLabel : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Theme.Text, background);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Focus();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void DrawChecker(Graphics graphics, Rectangle bounds)
    {
        const int checker = 5;
        using var light = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var dark = new SolidBrush(Color.FromArgb(150, 150, 150));
        for (var y = bounds.Top; y < bounds.Bottom; y += checker)
        {
            for (var x = bounds.Left; x < bounds.Right; x += checker)
            {
                var brush = ((x - bounds.Left) / checker + (y - bounds.Top) / checker) % 2 == 0 ? light : dark;
                graphics.FillRectangle(brush, x, y, Math.Min(checker, bounds.Right - x), Math.Min(checker, bounds.Bottom - y));
            }
        }
    }
}

internal sealed class ColorPaletteGrid : Control
{
    private readonly List<Color> _colors = [];
    private int _hoverIndex = -1;
    private int _focusIndex = -1;
    private Color _selectedColor = Color.Empty;
    private int _swatchSize = 18;
    private int _gap = 4;
    private string _emptyText = string.Empty;

    public ColorPaletteGrid()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
    }

    public Color SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (_selectedColor.ToArgb() == value.ToArgb() && _selectedColor.IsEmpty == value.IsEmpty) return;
            _selectedColor = value;
            Invalidate();
        }
    }

    public IReadOnlyList<Color> Colors => _colors;

    [DefaultValue(18)]
    public int SwatchSize
    {
        get => _swatchSize;
        set
        {
            var next = Math.Clamp(value, 12, 48);
            if (_swatchSize == next) return;
            _swatchSize = next;
            Invalidate();
        }
    }

    [DefaultValue(4)]
    public int Gap
    {
        get => _gap;
        set
        {
            var next = Math.Clamp(value, 0, 12);
            if (_gap == next) return;
            _gap = next;
            Invalidate();
        }
    }

    [DefaultValue("")]
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_emptyText, value, StringComparison.Ordinal)) return;
            _emptyText = value;
            Invalidate();
        }
    }

    public event EventHandler<ColorSwatchEventArgs>? ColorSelected;
    public event EventHandler<ColorSwatchEventArgs>? ColorRemoveRequested;
    public event EventHandler<ColorSwatchEventArgs>? HoveredColorChanged;
    public event EventHandler? HoverCleared;

    public void SetColors(IEnumerable<Color> colors)
    {
        _colors.Clear();
        _colors.AddRange(colors);
        _hoverIndex = -1;
        _focusIndex = _colors.Count == 0 ? -1 : Math.Clamp(_focusIndex, 0, _colors.Count - 1);
        Invalidate();
    }

    public Color? FocusedColor => (uint)_focusIndex < (uint)_colors.Count ? _colors[_focusIndex] : null;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_colors.Count == 0 && !string.IsNullOrWhiteSpace(_emptyText))
        {
            using var emptyFont = Theme.UiFont(8.5f);
            TextRenderer.DrawText(
                e.Graphics,
                UiLocalization.T(_emptyText),
                emptyFont,
                ClientRectangle,
                Theme.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        for (var i = 0; i < _colors.Count; i++) DrawSwatch(e.Graphics, i, _colors[i]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTest(e.Location);
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        Invalidate();
        if ((uint)index < (uint)_colors.Count) HoveredColorChanged?.Invoke(this, new ColorSwatchEventArgs(_colors[index]));
        else HoverCleared?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        Invalidate();
        HoverCleared?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var index = HitTest(e.Location);
        if ((uint)index >= (uint)_colors.Count) return;
        Focus();
        _focusIndex = index;
        Invalidate();
        var args = new ColorSwatchEventArgs(_colors[index]);
        if (e.Button == MouseButtons.Right) ColorRemoveRequested?.Invoke(this, args);
        else if (e.Button == MouseButtons.Left) ColorSelected?.Invoke(this, args);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Space or Keys.Enter or Keys.Delete
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_colors.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            var index = Math.Clamp(_focusIndex, 0, _colors.Count - 1);
            ColorSelected?.Invoke(this, new ColorSwatchEventArgs(_colors[index]));
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete)
        {
            var index = Math.Clamp(_focusIndex, 0, _colors.Count - 1);
            ColorRemoveRequested?.Invoke(this, new ColorSwatchEventArgs(_colors[index]));
            e.Handled = true;
            return;
        }

        var columns = ColumnCount();
        var next = e.KeyCode switch
        {
            Keys.Left => _focusIndex - 1,
            Keys.Right => _focusIndex + 1,
            Keys.Up => _focusIndex - columns,
            Keys.Down => _focusIndex + columns,
            _ => _focusIndex
        };
        if (next == _focusIndex)
        {
            base.OnKeyDown(e);
            return;
        }

        _focusIndex = Math.Clamp(next, 0, _colors.Count - 1);
        Invalidate();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focusIndex < 0 && _colors.Count > 0) _focusIndex = 0;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void DrawSwatch(Graphics graphics, int index, Color color)
    {
        var bounds = SwatchBounds(index);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        DrawChecker(graphics, bounds);
        using (var fill = new SolidBrush(color)) graphics.FillRectangle(fill, bounds);
        var selected = !_selectedColor.IsEmpty && _selectedColor.ToArgb() == color.ToArgb();
        var focused = Focused && index == _focusIndex;
        var hovered = index == _hoverIndex;
        using var border = new Pen(selected ? Theme.Accent : hovered || focused ? Theme.Text : Theme.Border, selected ? 2f : 1f);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (selected)
        {
            using var inner = new Pen(Color.FromArgb(190, Theme.AccentText));
            var inset = Rectangle.Inflate(bounds, -3, -3);
            graphics.DrawRectangle(inner, inset.X, inset.Y, Math.Max(0, inset.Width - 1), Math.Max(0, inset.Height - 1));
        }
    }

    private int HitTest(Point location)
    {
        for (var i = 0; i < _colors.Count; i++)
        {
            if (SwatchBounds(i).Contains(location)) return i;
        }
        return -1;
    }

    private Rectangle SwatchBounds(int index)
    {
        var columns = ColumnCount();
        var column = index % columns;
        var row = index / columns;
        return new Rectangle(column * (_swatchSize + _gap), row * (_swatchSize + _gap), _swatchSize, _swatchSize);
    }

    private int ColumnCount()
    {
        return Math.Max(1, (ClientSize.Width + _gap) / (_swatchSize + _gap));
    }

    private static void DrawChecker(Graphics graphics, Rectangle bounds)
    {
        const int checker = 6;
        using var light = new SolidBrush(Color.FromArgb(205, 205, 205));
        using var dark = new SolidBrush(Color.FromArgb(135, 135, 135));
        for (var y = bounds.Top; y < bounds.Bottom; y += checker)
        {
            for (var x = bounds.Left; x < bounds.Right; x += checker)
            {
                var brush = ((x - bounds.Left) / checker + (y - bounds.Top) / checker) % 2 == 0 ? light : dark;
                graphics.FillRectangle(brush, x, y, Math.Min(checker, bounds.Right - x), Math.Min(checker, bounds.Bottom - y));
            }
        }
    }
}
