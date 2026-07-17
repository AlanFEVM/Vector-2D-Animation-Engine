using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class NumericInteractionEventArgs : EventArgs
{
    public NumericInteractionEventArgs(decimal initialValue, decimal value, bool changed)
    {
        InitialValue = initialValue;
        Value = value;
        Changed = changed;
    }

    public decimal InitialValue { get; }
    public decimal Value { get; }
    public bool Changed { get; }
}

internal sealed class ModernNumericUpDown : UserControl
{
    private const int LogicalDeadZonePixels = 4;
    private const int LogicalPixelsPerIncrement = 4;
    private const int LogicalStepperWidth = 18;

    private readonly NumericEditorTextBox _editor = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Field,
        ForeColor = Theme.Text,
        Font = Theme.UiFont(),
        TextAlign = HorizontalAlignment.Left,
        AccessibleRole = AccessibleRole.Text,
        Cursor = Cursors.SizeWE
    };
    private readonly NumericStepButton _increaseButton = new(1) { AccessibleName = "Increase value" };
    private readonly NumericStepButton _decreaseButton = new(-1) { AccessibleName = "Decrease value" };
    private bool _updatingEditorText;
    private bool _pointerPending;
    private bool _endingInteraction;
    private bool _stepperInteracting;
    private int _pressScreenX;
    private int _pressScreenY;
    private int _lastScreenX;
    private int _pixelRemainder;
    private decimal _interactionStartValue;
    private decimal _stepperStartValue;
    private decimal _rawScrubValue;
    private decimal _minimum;
    private decimal _maximum = 100;
    private decimal _increment = 1;
    private decimal _value;
    private int _decimalPlaces;
    private bool _interactionChanged;
    private bool _stepperInteractionChanged;
    private bool _hovered;
    private string _suffix = string.Empty;

    public ModernNumericUpDown()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        AutoSize = false;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Height = Theme.ControlHeightCompact;
        MinimumSize = new Size(0, Theme.ControlHeightCompact);
        AccessibleRole = AccessibleRole.SpinButton;

        _editor.PointerMouseDown += HandleEditorMouseDown;
        _editor.PointerMouseMove += HandleEditorMouseMove;
        _editor.PointerMouseUp += HandleEditorMouseUp;
        _editor.MouseCaptureChanged += HandleEditorCaptureChanged;
        _editor.MouseEnter += (_, _) => UpdateHoverState();
        _editor.MouseLeave += (_, _) => UpdateHoverState();
        _editor.GotFocus += (_, _) => RetargetVisual();
        _editor.LostFocus += (_, _) =>
        {
            CommitEditorText();
            RetargetVisual();
        };
        _editor.KeyDown += HandleEditorKeyDown;

        _increaseButton.PressStarted += (_, _) => BeginStepperInteraction();
        _increaseButton.StepRequested += (_, _) => StepValue(1);
        _increaseButton.PressEnded += (_, _) => EndStepperInteraction(canceled: false);
        _decreaseButton.PressStarted += (_, _) => BeginStepperInteraction();
        _decreaseButton.StepRequested += (_, _) => StepValue(-1);
        _decreaseButton.PressEnded += (_, _) => EndStepperInteraction(canceled: false);

        Controls.Add(_editor);
        Controls.Add(_increaseButton);
        Controls.Add(_decreaseButton);
        Cursor = Cursors.SizeWE;
        UpdateEditorAccessibility();
        UpdateEditorText();
    }

    public event EventHandler? ValueChanged;
    public event EventHandler<NumericInteractionEventArgs>? InteractionStarted;
    public event EventHandler<NumericInteractionEventArgs>? InteractionCompleted;
    public event EventHandler<NumericInteractionEventArgs>? InteractionCanceled;

    [Browsable(false)]
    public bool IsScrubbing { get; private set; }

    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            Value = _value;
        }
    }

    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > _maximum) _minimum = _maximum;
            Value = _value;
        }
    }

    public decimal Increment
    {
        get => _increment;
        set => _increment = Math.Max(0.0000000000000000000000000001m, value);
    }

    public int DecimalPlaces
    {
        get => _decimalPlaces;
        set
        {
            var next = Math.Clamp(value, 0, 28);
            if (_decimalPlaces == next) return;
            _decimalPlaces = next;
            Value = _value;
            UpdateEditorText();
        }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            var next = QuantizeAndClamp(value);
            if (_value == next)
            {
                if (!_editor.Focused) UpdateEditorText();
                return;
            }

            _value = next;
            UpdateEditorText();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Category("Accessibility")]
    [DefaultValue("")]
    [Description("Unit text exposed to accessibility clients without becoming part of the editable numeric text.")]
    public string Suffix
    {
        get => _suffix;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (string.Equals(_suffix, next, StringComparison.Ordinal)) return;
            _suffix = next;
            UpdateEditorAccessibility();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateEditorAccessibility();
        LayoutChildren();
        RetargetVisual();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChildren();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _editor.Font = Font;
        LayoutChildren();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        RetargetVisual();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
        {
            if (IsScrubbing) EndInteraction(canceled: false);
            if (_stepperInteracting) EndStepperInteraction(canceled: false);
        }

        _editor.Enabled = Enabled;
        _increaseButton.Enabled = Enabled;
        _decreaseButton.Enabled = Enabled;
        RetargetVisual();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHoverState();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateHoverState();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!ContainsFocus) return;
        BeginStepperInteraction();
        StepValue(e.Delta > 0 ? 1 : -1);
        EndStepperInteraction(canceled: false);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && IsScrubbing)
        {
            EndInteraction(canceled: true);
            return true;
        }

        if (keyData == Keys.Escape && _stepperInteracting)
        {
            EndStepperInteraction(canceled: true);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var fill = new SolidBrush(ResolveVisualColor());
        using var border = new Pen(ContainsFocus ? Theme.Accent : Theme.Border);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _editor.PointerMouseDown -= HandleEditorMouseDown;
            _editor.PointerMouseMove -= HandleEditorMouseMove;
            _editor.PointerMouseUp -= HandleEditorMouseUp;
            _editor.MouseCaptureChanged -= HandleEditorCaptureChanged;
            _editor.KeyDown -= HandleEditorKeyDown;
        }

        base.Dispose(disposing);
    }

    private void LayoutChildren()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var border = ScaleLogicalPixels(1);
        var buttonWidth = Math.Min(ClientSize.Width, ScaleLogicalPixels(LogicalStepperWidth));
        var buttonLeft = Math.Max(border, ClientSize.Width - buttonWidth - border);
        var availableHeight = Math.Max(2, ClientSize.Height - border * 2);
        var increaseHeight = Math.Max(1, availableHeight / 2);
        _editor.SetBounds(
            border + ScaleLogicalPixels(4),
            Math.Max(border, (ClientSize.Height - TextRenderer.MeasureText("0", Font).Height) / 2),
            Math.Max(1, buttonLeft - border - ScaleLogicalPixels(4)),
            Math.Max(1, TextRenderer.MeasureText("0", Font).Height));
        _increaseButton.SetBounds(buttonLeft, border, buttonWidth, increaseHeight);
        _decreaseButton.SetBounds(buttonLeft, border + increaseHeight, buttonWidth, Math.Max(1, availableHeight - increaseHeight));
    }

    private void HandleEditorMouseDown(object? sender, MouseEventArgs e)
    {
        if (!Enabled || e.Button != MouseButtons.Left) return;
        _editor.SelectionStart = _editor.TextLength;
        _editor.SelectionLength = 0;
        _pointerPending = true;
        _pressScreenX = Control.MousePosition.X;
        _pressScreenY = Control.MousePosition.Y;
        _lastScreenX = _pressScreenX;
        _pixelRemainder = 0;
    }

    private void HandleEditorMouseMove(object? sender, MouseEventArgs e)
    {
        var leftButtonDown = (e.Button & MouseButtons.Left) != 0 || (MouseButtons & MouseButtons.Left) != 0;
        if (!leftButtonDown)
        {
            if (IsScrubbing) EndInteraction(canceled: false);
            else _pointerPending = false;
            return;
        }

        if (!_pointerPending && !IsScrubbing) return;
        var pointer = Control.MousePosition;
        if (!IsScrubbing)
        {
            var dx = pointer.X - _pressScreenX;
            var dy = pointer.Y - _pressScreenY;
            var deadZone = ScaleLogicalPixels(LogicalDeadZonePixels);
            if (Math.Abs(dx) < deadZone || Math.Abs(dx) < Math.Abs(dy)) return;
            BeginInteraction(pointer.X);
        }

        if (IsScrubbing) ApplyPointerMovement(pointer.X);
    }

    private void HandleEditorMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (IsScrubbing)
        {
            ApplyPointerMovement(Control.MousePosition.X);
            EndInteraction(canceled: false);
        }
        else
        {
            _pointerPending = false;
            _editor.SelectAll();
        }
    }

    private void HandleEditorCaptureChanged(object? sender, EventArgs e)
    {
        if (_endingInteraction || _editor.Capture) return;
        if (IsScrubbing) EndInteraction(canceled: false, releaseCapture: false);
        else if ((MouseButtons & MouseButtons.Left) == 0) _pointerPending = false;
    }

    private void HandleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && IsScrubbing)
        {
            EndInteraction(canceled: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            CommitEditorText();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
        CommitEditorText();
        BeginStepperInteraction();
        StepValue(e.KeyCode == Keys.Up ? 1 : -1);
        EndStepperInteraction(canceled: false);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void BeginInteraction(int screenX)
    {
        if (IsScrubbing || _stepperInteracting) return;
        CommitEditorText();
        _pointerPending = false;
        _interactionStartValue = Value;
        _rawScrubValue = Value;
        _interactionChanged = false;
        _lastScreenX = _pressScreenX;
        _pixelRemainder = 0;
        IsScrubbing = true;
        _editor.Capture = true;
        RetargetVisual();
        InteractionStarted?.Invoke(this, new NumericInteractionEventArgs(Value, Value, changed: false));
        ApplyPointerMovement(screenX);
    }

    private void ApplyPointerMovement(int screenX)
    {
        if (!IsScrubbing) return;
        var pixelDelta = screenX - _lastScreenX;
        _lastScreenX = screenX;
        if (pixelDelta == 0) return;

        _pixelRemainder += pixelDelta;
        var wholeIncrements = _pixelRemainder / ScaleLogicalPixels(LogicalPixelsPerIncrement);
        if (wholeIncrements == 0) return;
        _pixelRemainder -= wholeIncrements * ScaleLogicalPixels(LogicalPixelsPerIncrement);
        try
        {
            _rawScrubValue += wholeIncrements * EffectiveIncrement();
        }
        catch (OverflowException)
        {
            _rawScrubValue = wholeIncrements > 0 ? Maximum : Minimum;
        }

        var next = QuantizeAndClamp(_rawScrubValue);
        if (next == Value) return;
        Value = next;
        _interactionChanged = true;
    }

    private void EndInteraction(bool canceled, bool releaseCapture = true)
    {
        if (!IsScrubbing) return;
        _endingInteraction = true;
        var initialValue = _interactionStartValue;
        var changed = _interactionChanged;
        if (canceled && Value != initialValue) Value = initialValue;
        var finalValue = Value;
        IsScrubbing = false;
        _pointerPending = false;
        _pixelRemainder = 0;
        if (releaseCapture && _editor.Capture) _editor.Capture = false;
        _endingInteraction = false;
        RetargetVisual();

        var args = new NumericInteractionEventArgs(initialValue, finalValue, canceled ? changed : finalValue != initialValue);
        if (canceled) InteractionCanceled?.Invoke(this, args);
        else InteractionCompleted?.Invoke(this, args);
    }

    private void BeginStepperInteraction()
    {
        if (_stepperInteracting || IsScrubbing) return;
        CommitEditorText();
        _stepperInteracting = true;
        _stepperStartValue = Value;
        _stepperInteractionChanged = false;
        RetargetVisual();
        InteractionStarted?.Invoke(this, new NumericInteractionEventArgs(Value, Value, changed: false));
    }

    private void StepValue(int direction)
    {
        if (!_stepperInteracting) BeginStepperInteraction();
        if (direction == 0) return;
        decimal next;
        try
        {
            next = Value + EffectiveIncrement() * Math.Sign(direction);
        }
        catch (OverflowException)
        {
            next = direction > 0 ? Maximum : Minimum;
        }

        next = QuantizeAndClamp(next);
        if (next == Value) return;
        Value = next;
        _stepperInteractionChanged = true;
    }

    private void EndStepperInteraction(bool canceled)
    {
        if (!_stepperInteracting) return;
        var initialValue = _stepperStartValue;
        var changed = _stepperInteractionChanged;
        if (canceled && Value != initialValue) Value = initialValue;
        var finalValue = Value;
        _stepperInteracting = false;
        _stepperInteractionChanged = false;
        RetargetVisual();

        var args = new NumericInteractionEventArgs(initialValue, finalValue, canceled ? changed : finalValue != initialValue);
        if (canceled) InteractionCanceled?.Invoke(this, args);
        else InteractionCompleted?.Invoke(this, args);
    }

    private void CommitEditorText()
    {
        if (_updatingEditorText) return;
        if (!decimal.TryParse(_editor.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed))
        {
            UpdateEditorText();
            return;
        }

        Value = parsed;
    }

    private void UpdateEditorText()
    {
        if (_editor.IsDisposed) return;
        var text = Value.ToString($"F{DecimalPlaces}", CultureInfo.CurrentCulture);
        if (string.Equals(_editor.Text, text, StringComparison.Ordinal)) return;
        _updatingEditorText = true;
        try
        {
            _editor.Text = text;
        }
        finally
        {
            _updatingEditorText = false;
        }
    }

    private decimal EffectiveIncrement()
    {
        var modifiers = ModifierKeys;
        var shift = (modifiers & Keys.Shift) != 0;
        var control = (modifiers & Keys.Control) != 0;
        var multiplier = shift && control ? 0.01m : shift ? 0.1m : control ? 10m : 1m;
        return Increment * multiplier;
    }

    private decimal QuantizeAndClamp(decimal value)
    {
        var rounded = decimal.Round(value, DecimalPlaces, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, Minimum, Maximum);
    }

    private void UpdateHoverState()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var hovered = ClientRectangle.Contains(PointToClient(Control.MousePosition));
        if (_hovered == hovered) return;
        _hovered = hovered;
        RetargetVisual();
    }

    private Color ResolveVisualColor()
    {
        if (!Enabled) return Theme.DisabledSurface;
        if (IsScrubbing || _stepperInteracting) return Theme.Mix(Theme.Field, Theme.AccentSurface, 0.72f);
        if (ContainsFocus) return Theme.Mix(Theme.Field, Theme.AccentSurface, 0.42f);
        if (_hovered) return Theme.Mix(Theme.Field, Theme.PanelHover, 0.28f);
        return Theme.Field;
    }

    private void RetargetVisual()
    {
        var background = ResolveVisualColor();
        if (BackColor != background) BackColor = background;
        var foreground = Enabled ? Theme.Text : Theme.DisabledText;
        if (ForeColor != foreground) ForeColor = foreground;
        if (_editor.BackColor != background) _editor.BackColor = background;
        if (_editor.ForeColor != foreground) _editor.ForeColor = foreground;
        var cursor = Enabled ? Cursors.SizeWE : Cursors.Default;
        if (Cursor != cursor) Cursor = cursor;
        if (_editor.Cursor != cursor) _editor.Cursor = cursor;
        _increaseButton.Invalidate();
        _decreaseButton.Invalidate();
        Invalidate();
    }

    private void UpdateEditorAccessibility()
    {
        var description = AccessibleDescription;
        if (!string.IsNullOrEmpty(_suffix))
        {
            var unitDescription = $"Unit: {_suffix}";
            description = string.IsNullOrWhiteSpace(description) ? unitDescription : $"{description}. {unitDescription}";
        }

        _editor.AccessibleName = AccessibleName;
        _editor.AccessibleDescription = description;
    }

    private int ScaleLogicalPixels(int logicalPixels)
    {
        var dpi = IsHandleCreated ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * dpi / 96f));
    }

    private sealed class NumericEditorTextBox : TextBox
    {
        private const int WmMouseMove = 0x0200;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private const int WmLeftButtonDoubleClick = 0x0203;

        public event EventHandler<MouseEventArgs>? PointerMouseDown;
        public event EventHandler<MouseEventArgs>? PointerMouseMove;
        public event EventHandler<MouseEventArgs>? PointerMouseUp;

        protected override void WndProc(ref Message message)
        {
            if (Enabled && message.Msg is WmLeftButtonDown or WmLeftButtonDoubleClick)
            {
                Focus();
                Capture = true;
                PointerMouseDown?.Invoke(this, MouseEvent(message, MouseButtons.Left));
                return;
            }

            if (Enabled && message.Msg == WmMouseMove && Capture)
            {
                PointerMouseMove?.Invoke(this, MouseEvent(message, MouseButtons.Left));
                return;
            }

            if (Enabled && message.Msg == WmLeftButtonUp && Capture)
            {
                PointerMouseUp?.Invoke(this, MouseEvent(message, MouseButtons.Left));
                if (Capture) Capture = false;
                return;
            }

            base.WndProc(ref message);
        }

        private static MouseEventArgs MouseEvent(Message message, MouseButtons button)
        {
            var packed = message.LParam.ToInt64();
            var x = unchecked((short)(packed & 0xffff));
            var y = unchecked((short)((packed >> 16) & 0xffff));
            return new MouseEventArgs(button, 1, x, y, 0);
        }
    }

    private sealed class NumericStepButton : Control
    {
        private readonly System.Windows.Forms.Timer _repeatTimer = new() { Interval = 360 };
        private bool _hovered;
        private bool _pressed;
        private bool _repeatArmed;

        public NumericStepButton(int direction)
        {
            Direction = Math.Sign(direction);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            AccessibleRole = AccessibleRole.PushButton;
            TabStop = false;
            _repeatTimer.Tick += HandleRepeatTick;
        }

        public int Direction { get; }
        public event EventHandler? PressStarted;
        public event EventHandler? StepRequested;
        public event EventHandler? PressEnded;

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
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Parent?.Focus();
            Capture = true;
            _pressed = true;
            _repeatArmed = false;
            _repeatTimer.Interval = 360;
            _repeatTimer.Start();
            PressStarted?.Invoke(this, EventArgs.Empty);
            StepRequested?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) FinishPress();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) FinishPress();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) FinishPress();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var background = !Enabled
                ? Theme.DisabledSurface
                : _pressed
                    ? Theme.AccentSurface
                    : _hovered
                        ? Theme.PanelHover
                        : Theme.PanelStrong;
            using var fill = new SolidBrush(background);
            using var border = new Pen(Theme.Border);
            e.Graphics.FillRectangle(fill, ClientRectangle);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            var centerX = ClientSize.Width * 0.5f;
            var centerY = ClientSize.Height * 0.5f;
            var size = Math.Max(2f, Math.Min(ClientSize.Width, ClientSize.Height) * 0.22f);
            var sign = Direction > 0 ? 1f : -1f;
            using var chevron = new Pen(Enabled ? Theme.Text : Theme.DisabledText, Math.Max(1f, DeviceDpi / 96f))
            {
                LineJoin = LineJoin.Round
            };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(chevron,
            [
                new PointF(centerX - size, centerY + size * sign),
                new PointF(centerX, centerY - size * sign),
                new PointF(centerX + size, centerY + size * sign)
            ]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _repeatTimer.Stop();
                _repeatTimer.Tick -= HandleRepeatTick;
                _repeatTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void HandleRepeatTick(object? sender, EventArgs e)
        {
            if (!_pressed || !Enabled)
            {
                FinishPress();
                return;
            }

            if (!_repeatArmed)
            {
                _repeatArmed = true;
                _repeatTimer.Interval = 70;
            }

            StepRequested?.Invoke(this, EventArgs.Empty);
        }

        private void FinishPress()
        {
            if (!_pressed) return;
            _pressed = false;
            _repeatTimer.Stop();
            if (Capture) Capture = false;
            PressEnded?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }
}
