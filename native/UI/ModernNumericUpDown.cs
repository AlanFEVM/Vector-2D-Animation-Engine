using System.ComponentModel;

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

internal sealed class ModernNumericUpDown : NumericUpDown
{
    private const int LogicalDeadZonePixels = 4;
    private const int LogicalPixelsPerIncrement = 4;

    private TextBoxBase? _editor;
    private EditorMessageWindow? _editorWindow;
    private bool _pointerPending;
    private bool _endingInteraction;
    private int _pressScreenX;
    private int _pressScreenY;
    private int _lastScreenX;
    private int _pixelRemainder;
    private decimal _interactionStartValue;
    private decimal _rawScrubValue;
    private bool _interactionChanged;
    private bool _hovered;
    private string _suffix = string.Empty;
    private Color _visualColor;
    private Color _targetVisualColor;
    private System.Windows.Forms.Timer? _animationTimer;

    public ModernNumericUpDown()
    {
        AutoSize = false;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        Font = Theme.UiFont();
        Height = Theme.ControlHeightCompact;
        MinimumSize = new Size(0, Theme.ControlHeightCompact);
        _visualColor = BackColor;
        _targetVisualColor = BackColor;
        HookEditor();
    }

    public event EventHandler<NumericInteractionEventArgs>? InteractionStarted;
    public event EventHandler<NumericInteractionEventArgs>? InteractionCompleted;
    public event EventHandler<NumericInteractionEventArgs>? InteractionCanceled;

    [Browsable(false)]
    public bool IsScrubbing { get; private set; }

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
        HookEditor();
        RetargetVisual(immediate: true);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (IsScrubbing) EndInteraction(canceled: false, releaseCapture: false);
        _pointerPending = false;
        StopAnimation();
        base.OnHandleDestroyed(e);
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

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_endingInteraction || Capture || _editor?.Capture == true) return;
        HandleCaptureLost();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled && IsScrubbing) EndInteraction(canceled: false);
        if (_editor is not null) _editor.Cursor = Enabled ? Cursors.SizeWE : Cursors.Default;
        RetargetVisual();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        RetargetVisual();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        RetargetVisual();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!ContainsFocus) return;
        base.OnMouseWheel(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && IsScrubbing)
        {
            EndInteraction(canceled: true);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachEditor();
            StopAnimation();
        }

        base.Dispose(disposing);
    }

    private void HookEditor()
    {
        var editor = FindEditor(this);
        if (ReferenceEquals(editor, _editor))
        {
            ApplyVisualColor(_visualColor);
            UpdateEditorAccessibility();
            return;
        }

        DetachEditor();
        _editor = editor;
        if (_editor is null) return;

        _editor.Cursor = Enabled ? Cursors.SizeWE : Cursors.Default;
        _editor.HandleCreated += EditorHandleCreated;
        _editor.HandleDestroyed += EditorHandleDestroyed;
        _editor.MouseEnter += EditorMouseEnter;
        _editor.MouseLeave += EditorMouseLeave;
        _editor.GotFocus += EditorGotFocus;
        _editor.LostFocus += EditorLostFocus;
        _editor.KeyDown += EditorKeyDown;
        AttachEditorWindow();
        ApplyVisualColor(_visualColor);
        UpdateEditorAccessibility();
    }

    private void DetachEditor()
    {
        if (_editor is null) return;
        _editor.HandleCreated -= EditorHandleCreated;
        _editor.HandleDestroyed -= EditorHandleDestroyed;
        _editor.MouseEnter -= EditorMouseEnter;
        _editor.MouseLeave -= EditorMouseLeave;
        _editor.GotFocus -= EditorGotFocus;
        _editor.LostFocus -= EditorLostFocus;
        _editor.KeyDown -= EditorKeyDown;
        ReleaseEditorWindow();
        _editor = null;
    }

    private static TextBoxBase? FindEditor(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBoxBase editor) return editor;
            if (FindEditor(child) is { } nested) return nested;
        }

        return null;
    }

    private void EditorHandleCreated(object? sender, EventArgs e) => AttachEditorWindow();

    private void EditorHandleDestroyed(object? sender, EventArgs e) => ReleaseEditorWindow();

    private void AttachEditorWindow()
    {
        if (_editor is null || !_editor.IsHandleCreated || IsDisposed) return;
        _editorWindow ??= new EditorMessageWindow(this);
        if (_editorWindow.Handle == _editor.Handle) return;
        if (_editorWindow.Handle != IntPtr.Zero) _editorWindow.ReleaseHandle();
        _editorWindow.AssignHandle(_editor.Handle);
    }

    private void ReleaseEditorWindow()
    {
        if (_editorWindow is null || _editorWindow.Handle == IntPtr.Zero) return;
        _editorWindow.ReleaseHandle();
    }

    private void HandleEditorPointerDown()
    {
        if (!Enabled || _editor is null) return;
        var pointer = Control.MousePosition;
        _pointerPending = true;
        _pressScreenX = pointer.X;
        _pressScreenY = pointer.Y;
        _lastScreenX = _pressScreenX;
        _pixelRemainder = 0;
    }

    private void HandleEditorPointerMove(bool leftButtonDown)
    {
        if (!_pointerPending && !IsScrubbing)
        {
            if (!leftButtonDown) return;
            var initialPointer = Control.MousePosition;
            _pointerPending = true;
            _pressScreenX = initialPointer.X;
            _pressScreenY = initialPointer.Y;
            _lastScreenX = initialPointer.X;
            _pixelRemainder = 0;
            if (_editor is not null) _editor.Capture = true;
            return;
        }

        if (!leftButtonDown)
        {
            if (IsScrubbing) EndInteraction(canceled: false);
            else _pointerPending = false;
            return;
        }

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

    private void HandleEditorPointerUp()
    {
        if (IsScrubbing)
        {
            ApplyPointerMovement(Control.MousePosition.X);
            EndInteraction(canceled: false);
        }
        else
        {
            _pointerPending = false;
        }
    }

    private void HandleEditorCaptureChanged()
    {
        if (_endingInteraction || _editor?.Capture == true) return;
        HandleCaptureLost();
    }

    private void EditorMouseEnter(object? sender, EventArgs e) => UpdateHoverState();

    private void EditorMouseLeave(object? sender, EventArgs e) => UpdateHoverState();

    private void EditorGotFocus(object? sender, EventArgs e) => RetargetVisual();

    private void EditorLostFocus(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke((Action)(() =>
        {
            if (!IsDisposed) RetargetVisual();
        }));
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape || !IsScrubbing) return;
        EndInteraction(canceled: true);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void BeginInteraction(int screenX)
    {
        if (IsScrubbing || _editor is null) return;

        ValidateEditText();
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
        if (IsScrubbing && !IsDisposed) ApplyPointerMovement(screenX);
    }

    private void ApplyPointerMovement(int screenX)
    {
        if (!IsScrubbing) return;

        var pixelDelta = screenX - _lastScreenX;
        _lastScreenX = screenX;
        if (pixelDelta == 0) return;

        _pixelRemainder += pixelDelta;
        var pixelsPerIncrement = ScaleLogicalPixels(LogicalPixelsPerIncrement);
        var wholeIncrements = _pixelRemainder / pixelsPerIncrement;
        if (wholeIncrements == 0) return;
        _pixelRemainder -= wholeIncrements * pixelsPerIncrement;

        var step = EffectiveIncrement();
        if (step <= 0) return;

        try
        {
            _rawScrubValue += wholeIncrements * step;
        }
        catch (OverflowException)
        {
            _rawScrubValue = wholeIncrements > 0 ? Maximum : Minimum;
        }

        _rawScrubValue = Math.Clamp(_rawScrubValue, Minimum, Maximum);
        var next = QuantizeAndClamp(_rawScrubValue);
        if (next == Value) return;
        Value = next;
        _interactionChanged = true;
    }

    private decimal EffectiveIncrement()
    {
        var modifiers = ModifierKeys;
        var shift = (modifiers & Keys.Shift) != 0;
        var control = (modifiers & Keys.Control) != 0;
        var multiplier = shift && control
            ? 0.01m
            : shift
                ? 0.1m
                : control
                    ? 10m
                    : 1m;
        return Increment * multiplier;
    }

    private decimal QuantizeAndClamp(decimal value)
    {
        var places = Math.Clamp(DecimalPlaces, 0, 28);
        var rounded = decimal.Round(value, places, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, Minimum, Maximum);
    }

    private void EndInteraction(bool canceled, bool releaseCapture = true)
    {
        if (!IsScrubbing) return;

        _endingInteraction = true;
        var initialValue = _interactionStartValue;
        var changedDuringInteraction = _interactionChanged;
        if (canceled && Value != initialValue) Value = initialValue;
        var finalValue = Value;

        IsScrubbing = false;
        _pointerPending = false;
        _pixelRemainder = 0;
        if (releaseCapture && _editor?.Capture == true) _editor.Capture = false;
        _endingInteraction = false;
        RetargetVisual();

        var args = new NumericInteractionEventArgs(
            initialValue,
            finalValue,
            canceled ? changedDuringInteraction : finalValue != initialValue);
        if (canceled) InteractionCanceled?.Invoke(this, args);
        else InteractionCompleted?.Invoke(this, args);
    }

    private void HandleCaptureLost()
    {
        if (IsScrubbing) EndInteraction(canceled: false, releaseCapture: false);
        else if ((MouseButtons & MouseButtons.Left) == 0) _pointerPending = false;
    }

    private int ScaleLogicalPixels(int logicalPixels)
    {
        var dpi = IsHandleCreated ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * dpi / 96f));
    }

    private void UpdateHoverState()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var hovered = ClientRectangle.Contains(PointToClient(Control.MousePosition));
        if (_hovered == hovered) return;
        _hovered = hovered;
        RetargetVisual();
    }

    private void RetargetVisual(bool immediate = false)
    {
        _targetVisualColor = ResolveVisualColor();
        if (immediate || !IsHandleCreated)
        {
            StopAnimation();
            _visualColor = _targetVisualColor;
            ApplyVisualColor(_visualColor);
            return;
        }

        if (ColorDistance(_visualColor, _targetVisualColor) <= 2)
        {
            StopAnimation();
            _visualColor = _targetVisualColor;
            ApplyVisualColor(_visualColor);
            return;
        }

        if (_animationTimer is not null) return;
        _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animationTimer.Tick += AnimationTick;
        _animationTimer.Start();
    }

    private Color ResolveVisualColor()
    {
        if (!Enabled) return Theme.DisabledSurface;
        if (IsScrubbing) return Theme.Mix(Theme.Field, Theme.AccentSurface, 0.72f);
        if (ContainsFocus) return Theme.Mix(Theme.Field, Theme.AccentSurface, 0.42f);
        if (_hovered) return Theme.Mix(Theme.Field, Theme.PanelHover, 0.28f);
        return Theme.Field;
    }

    private void AnimationTick(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            StopAnimation();
            return;
        }

        _visualColor = Theme.Mix(_visualColor, _targetVisualColor, 0.32f);
        if (ColorDistance(_visualColor, _targetVisualColor) <= 2)
        {
            _visualColor = _targetVisualColor;
            ApplyVisualColor(_visualColor);
            StopAnimation();
            return;
        }

        ApplyVisualColor(_visualColor);
    }

    private void ApplyVisualColor(Color color)
    {
        if (BackColor != color) BackColor = color;
        var foreground = Enabled ? Theme.Text : Theme.DisabledText;
        if (ForeColor != foreground) ForeColor = foreground;
        if (_editor is null) return;
        if (_editor.BackColor != color) _editor.BackColor = color;
        if (_editor.ForeColor != foreground) _editor.ForeColor = foreground;
    }

    private void StopAnimation()
    {
        if (_animationTimer is null) return;
        _animationTimer.Stop();
        _animationTimer.Tick -= AnimationTick;
        _animationTimer.Dispose();
        _animationTimer = null;
    }

    private void UpdateEditorAccessibility()
    {
        if (_editor is null) return;
        var description = AccessibleDescription;
        if (!string.IsNullOrEmpty(_suffix))
        {
            var unitDescription = $"Unit: {_suffix}";
            description = string.IsNullOrWhiteSpace(description)
                ? unitDescription
                : $"{description}. {unitDescription}";
        }

        _editor.AccessibleName = AccessibleName;
        _editor.AccessibleDescription = description;
    }

    private static int ColorDistance(Color left, Color right)
    {
        return Math.Abs(left.R - right.R)
            + Math.Abs(left.G - right.G)
            + Math.Abs(left.B - right.B);
    }

    private sealed class EditorMessageWindow : NativeWindow
    {
        private const int WmMouseMove = 0x0200;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private const int WmCaptureChanged = 0x0215;
        private const int MkLeftButton = 0x0001;

        private readonly ModernNumericUpDown _owner;

        public EditorMessageWindow(ModernNumericUpDown owner)
        {
            _owner = owner;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmLeftButtonDown:
                    base.WndProc(ref m);
                    _owner.HandleEditorPointerDown();
                    return;
                case WmMouseMove:
                    base.WndProc(ref m);
                    var leftButtonDown = (m.WParam.ToInt64() & MkLeftButton) != 0
                        || (Control.MouseButtons & MouseButtons.Left) != 0;
                    _owner.HandleEditorPointerMove(leftButtonDown);
                    return;
                case WmLeftButtonUp:
                    _owner.HandleEditorPointerUp();
                    base.WndProc(ref m);
                    return;
                case WmCaptureChanged:
                    base.WndProc(ref m);
                    _owner.HandleEditorCaptureChanged();
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }
    }
}
