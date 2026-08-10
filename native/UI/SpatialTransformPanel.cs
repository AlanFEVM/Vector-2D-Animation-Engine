namespace VectorAnimationEngine;

internal readonly record struct SpatialTransformValues(
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ);

internal sealed class SpatialTransformValuesChangedEventArgs(SpatialTransformValues values) : EventArgs
{
    public SpatialTransformValues Values { get; } = values;
    public float X => Values.X;
    public float Y => Values.Y;
    public float Z => Values.Z;
    public float RotationX => Values.RotationX;
    public float RotationY => Values.RotationY;
    public float RotationZ => Values.RotationZ;
    public float ScaleX => Values.ScaleX;
    public float ScaleY => Values.ScaleY;
    public float ScaleZ => Values.ScaleZ;
}

internal sealed class SpatialTransformModeChangedEventArgs(SpatialTransformMode mode) : EventArgs
{
    public SpatialTransformMode Mode { get; } = mode;
}

internal sealed class SpatialTransformPanel : UserControl
{
    private const int PanelHeight = 150;
    private const decimal PositionLimit = 5_000_000m;
    private const decimal ThicknessLimit = 5_000_000m;

    private readonly ModernNumericUpDown _positionX = PositionInput("3D position X");
    private readonly ModernNumericUpDown _positionY = PositionInput("3D position Y");
    private readonly ModernNumericUpDown _positionZ = PositionInput("3D position Z");
    private readonly ModernNumericUpDown _rotationX = RotationInput("3D rotation X");
    private readonly ModernNumericUpDown _rotationY = RotationInput("3D rotation Y");
    private readonly ModernNumericUpDown _rotationZ = RotationInput("3D rotation Z");
    private readonly ModernNumericUpDown _scaleX = ScaleInput("3D scale X");
    private readonly ModernNumericUpDown _scaleY = ScaleInput("3D scale Y");
    private readonly ModernNumericUpDown _scaleZ = ThicknessInput("3D scale Z thickness");
    private readonly Button _moveMode = ModeButton("Move", "Move in 3D space");
    private readonly Button _rotateMode = ModeButton("Rotate", "Rotate in 3D space");
    private readonly Button _scaleMode = ModeButton("Scale", "Scale in 3D space");
    private readonly FlowLayoutPanel _modeStrip = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = new Padding(0, 1, 0, 1),
        BackColor = Theme.Panel
    };
    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Panel,
        ColumnCount = 4,
        RowCount = 5,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };
    private readonly Label[] _labels;
    private readonly ModernNumericUpDown[] _inputs;
    private bool _updating;
    private bool _hasState;
    private bool _editingRequested;
    private bool _interactionActive;
    private SpatialTransformMode _mode;

    public SpatialTransformPanel()
    {
        AutoSize = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        Padding = new Padding(6);
        Width = 300;
        Height = PanelHeight;
        MinimumSize = new Size(276, PanelHeight);
        MaximumSize = new Size(0, PanelHeight);
        AccessibleName = "3D transform values";
        AccessibleRole = AccessibleRole.Grouping;

        _inputs =
        [
            _positionX, _positionY, _positionZ,
            _rotationX, _rotationY, _rotationZ,
            _scaleX, _scaleY, _scaleZ
        ];

        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        for (var index = 0; index < 3; index++)
        {
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        }
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        for (var index = 0; index < 3; index++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        }

        var axisX = AxisLabel("X", "X axis");
        var axisY = AxisLabel("Y", "Y axis");
        var axisZ = AxisLabel("Z", "Z axis");
        var position = RowLabel("Position");
        var rotation = RowLabel("Rotation");
        var scale = RowLabel("Scale");
        _labels = [axisX, axisY, axisZ, position, rotation, scale];

        _modeStrip.Controls.AddRange([_moveMode, _rotateMode, _scaleMode]);
        _layout.Controls.Add(_modeStrip, 0, 0);
        _layout.SetColumnSpan(_modeStrip, 4);
        _layout.Controls.Add(axisX, 1, 1);
        _layout.Controls.Add(axisY, 2, 1);
        _layout.Controls.Add(axisZ, 3, 1);
        AddRow(position, 2, _positionX, _positionY, _positionZ);
        AddRow(rotation, 3, _rotationX, _rotationY, _rotationZ);
        AddRow(scale, 4, _scaleX, _scaleY, _scaleZ);

        _moveMode.Click += (_, _) => SetMode(SpatialTransformMode.Move, notify: true);
        _rotateMode.Click += (_, _) => SetMode(SpatialTransformMode.Rotate, notify: true);
        _scaleMode.Click += (_, _) => SetMode(SpatialTransformMode.Scale, notify: true);
        ApplyModePresentation();

        foreach (var input in _inputs)
        {
            Theme.StyleNumeric(input);
            input.ValueChanged += HandleValueChanged;
            input.InteractionStarted += HandleInteractionStarted;
            input.InteractionCompleted += HandleInteractionCompleted;
            input.InteractionCanceled += HandleInteractionCanceled;
        }

        Controls.Add(_layout);
        SetState(null, enabled: false);
    }

    public event EventHandler<SpatialTransformValuesChangedEventArgs>? ValuesChanged;
    public event EventHandler<SpatialTransformModeChangedEventArgs>? ModeChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public int PreferredPanelHeight => PanelHeight;

    public SpatialTransformMode Mode => _mode;

    public SpatialTransformValues Values => new(
        (float)_positionX.Value,
        (float)_positionY.Value,
        (float)_positionZ.Value,
        (float)_rotationX.Value,
        (float)_rotationY.Value,
        (float)_rotationZ.Value,
        (float)_scaleX.Value,
        (float)_scaleY.Value,
        (float)_scaleZ.Value);

    public void SetState(InstanceFrameState? state, bool enabled)
    {
        _hasState = state.HasValue;
        _editingRequested = enabled;
        if (!CanEdit && _interactionActive) CancelPanelInteraction();

        _updating = true;
        try
        {
            var value = state.GetValueOrDefault();
            _positionX.Value = PositionDecimal(value.X);
            _positionY.Value = PositionDecimal(value.Y);
            _positionZ.Value = PositionDecimal(value.Z);
            _rotationX.Value = RotationDecimal(value.RotationX);
            _rotationY.Value = RotationDecimal(value.RotationY);
            _rotationZ.Value = RotationDecimal(value.RotationZ);
            _scaleX.Value = ScaleDecimal(state.HasValue ? value.ScaleX : 1f);
            _scaleY.Value = ScaleDecimal(state.HasValue ? value.ScaleY : 1f);
            _scaleZ.Value = ThicknessDecimal(state.HasValue ? value.ScaleZ : 0f);
        }
        finally
        {
            _updating = false;
        }

        ApplyEnabledState();
    }

    public void SetMode(SpatialTransformMode mode) => SetMode(mode, notify: false);

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled && _interactionActive) CancelPanelInteraction();
        ApplyEnabledState();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_layout is not null) _layout.BackColor = BackColor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _interactionActive) CancelPanelInteraction();
        base.Dispose(disposing);
    }

    private bool CanEdit => Enabled && _hasState && _editingRequested;

    private void HandleValueChanged(object? sender, EventArgs e)
    {
        if (_updating) return;
        var oneShot = !_interactionActive;
        if (oneShot) BeginPanelInteraction();
        ValuesChanged?.Invoke(this, new SpatialTransformValuesChangedEventArgs(Values));
        if (oneShot) CompletePanelInteraction();
    }

    private void HandleInteractionStarted(object? sender, NumericInteractionEventArgs e)
    {
        if (!_updating) BeginPanelInteraction();
    }

    private void HandleInteractionCompleted(object? sender, NumericInteractionEventArgs e)
    {
        if (!_updating) CompletePanelInteraction();
    }

    private void HandleInteractionCanceled(object? sender, NumericInteractionEventArgs e)
    {
        if (!_updating) CancelPanelInteraction();
    }

    private void BeginPanelInteraction()
    {
        if (_interactionActive) return;
        _interactionActive = true;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void CompletePanelInteraction()
    {
        if (!_interactionActive) return;
        _interactionActive = false;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPanelInteraction()
    {
        if (!_interactionActive) return;
        _interactionActive = false;
        InteractionCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyEnabledState()
    {
        var canEdit = CanEdit;
        foreach (var input in _inputs) input.Enabled = canEdit;
        var color = canEdit ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Panel, 0.58f);
        foreach (var label in _labels) label.ForeColor = color;
        AccessibleDescription = canEdit ? "Edit selected instance 3D transform" : "No editable 3D transform selected";
    }

    private void SetMode(SpatialTransformMode mode, bool notify)
    {
        if (!Enum.IsDefined(mode)) return;
        var changed = _mode != mode;
        _mode = mode;
        ApplyModePresentation();
        if (changed && notify) ModeChanged?.Invoke(this, new SpatialTransformModeChangedEventArgs(mode));
    }

    private void ApplyModePresentation()
    {
        StyleModeButton(_moveMode, _mode == SpatialTransformMode.Move);
        StyleModeButton(_rotateMode, _mode == SpatialTransformMode.Rotate);
        StyleModeButton(_scaleMode, _mode == SpatialTransformMode.Scale);
    }

    private static void StyleModeButton(Button button, bool active)
    {
        if (active) Theme.StyleActiveButton(button);
        else Theme.StyleButton(button);
    }

    private void AddRow(
        Label label,
        int row,
        ModernNumericUpDown x,
        ModernNumericUpDown y,
        ModernNumericUpDown z)
    {
        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(x, 1, row);
        _layout.Controls.Add(y, 2, row);
        _layout.Controls.Add(z, 3, row);
    }

    private static Label AxisLabel(string text, string accessibleName)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 0, 2, 0),
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.StaticText
        };
    }

    private static Label RowLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 4, 1),
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        };
    }

    private static ModernNumericUpDown PositionInput(string accessibleName)
    {
        return NumericInput(
            accessibleName,
            -PositionLimit,
            PositionLimit,
            increment: 10m,
            decimalPlaces: 2,
            suffix: "vu");
    }

    private static ModernNumericUpDown RotationInput(string accessibleName)
    {
        return NumericInput(
            accessibleName,
            minimum: -360m,
            maximum: 360m,
            increment: 1m,
            decimalPlaces: 2,
            suffix: "degrees");
    }

    private static ModernNumericUpDown ScaleInput(string accessibleName)
    {
        return NumericInput(
            accessibleName,
            minimum: 0.01m,
            maximum: 1000m,
            increment: 0.01m,
            decimalPlaces: 3,
            suffix: "scale");
    }

    private static ModernNumericUpDown ThicknessInput(string accessibleName)
    {
        return NumericInput(
            accessibleName,
            minimum: 0m,
            maximum: ThicknessLimit,
            increment: 10m,
            decimalPlaces: 2,
            suffix: "vu");
    }

    private static ModernNumericUpDown NumericInput(
        string accessibleName,
        decimal minimum,
        decimal maximum,
        decimal increment,
        int decimalPlaces,
        string suffix)
    {
        return new ModernNumericUpDown
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 1, 2, 1),
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            DecimalPlaces = decimalPlaces,
            WheelAdjustsHoveredDigit = true,
            Suffix = suffix,
            AccessibleName = accessibleName,
            AccessibleDescription = $"{accessibleName}, {suffix}"
        };
    }

    private static Button ModeButton(string text, string accessibleDescription)
    {
        return new Button
        {
            Text = text,
            Width = 78,
            Height = 26,
            Margin = new Padding(0, 0, 4, 0),
            AccessibleName = $"3D transform {text.ToLowerInvariant()} mode",
            AccessibleDescription = accessibleDescription,
            AccessibleRole = AccessibleRole.RadioButton
        };
    }

    private static decimal PositionDecimal(float value) => BoundedDecimal(value, -PositionLimit, PositionLimit, 0m);

    private static decimal RotationDecimal(float value) => BoundedDecimal(value, -360m, 360m, 0m);

    private static decimal ScaleDecimal(float value) => BoundedDecimal(value, 0.01m, 1000m, 1m);

    private static decimal ThicknessDecimal(float value) => BoundedDecimal(value, 0m, ThicknessLimit, 0m);

    private static decimal BoundedDecimal(float value, decimal minimum, decimal maximum, decimal fallback)
    {
        if (!float.IsFinite(value)) return fallback;
        if (value <= (float)minimum) return minimum;
        if (value >= (float)maximum) return maximum;
        return (decimal)value;
    }
}
