using System.Diagnostics;
using System.Numerics;

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
    float ScaleZ,
    Vector3 RotationPivot,
    Vector3 ScalePivot);

internal enum SpatialTransformValueGroup
{
    Position,
    Rotation,
    Scale,
    RotationPivot,
    ScalePivot
}

internal sealed class SpatialTransformValuesChangedEventArgs(
    SpatialTransformValues values,
    SpatialTransformValueGroup group) : EventArgs
{
    public SpatialTransformValues Values { get; } = values;
    public SpatialTransformValueGroup Group { get; } = group;
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

internal sealed class SpatialTransformSpaceChangedEventArgs(SpatialTransformSpace space) : EventArgs
{
    public SpatialTransformSpace Space { get; } = space;
}

internal enum SpatialPivotKind
{
    None,
    Rotation,
    Scale
}

internal sealed class SpatialPivotKindChangedEventArgs(SpatialPivotKind kind) : EventArgs
{
    public SpatialPivotKind Kind { get; } = kind;
}

internal sealed class SpatialTransformPanel : UserControl
{
    private const int PanelHeight = 240;
    private const float FloatingSpringStiffness = 260f;
    private const float FloatingSpringDamping = 22f;
    private const float FloatingPositionEpsilon = 0.0005f;
    private const float FloatingVelocityEpsilon = 0.004f;
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
    private readonly ModernNumericUpDown _rotationPivotX = PositionInput("3D rotation pivot X");
    private readonly ModernNumericUpDown _rotationPivotY = PositionInput("3D rotation pivot Y");
    private readonly ModernNumericUpDown _rotationPivotZ = PositionInput("3D rotation pivot Z");
    private readonly ModernNumericUpDown _scalePivotX = PositionInput("3D scale pivot X");
    private readonly ModernNumericUpDown _scalePivotY = PositionInput("3D scale pivot Y");
    private readonly ModernNumericUpDown _scalePivotZ = PositionInput("3D scale pivot Z");
    private readonly Button _moveMode = ModeButton("Move", "Move in 3D space");
    private readonly Button _rotateMode = ModeButton("Rotate", "Rotate in 3D space");
    private readonly Button _scaleMode = ModeButton("Scale", "Scale in 3D space");
    private readonly Button _rotationPivotMode = PivotButton(
        "R Pivot",
        "Edit the rotation pivot with the 3D move gizmo");
    private readonly Button _scalePivotMode = PivotButton(
        "S Pivot",
        "Edit the scale pivot with the 3D move gizmo");
    private readonly SvgIconButton _resetRotationPivot = ResetPivotButton("Reset rotation pivot");
    private readonly SvgIconButton _resetScalePivot = ResetPivotButton("Reset scale pivot");
    private readonly SegmentedButton _globalSpace = SpaceButton("Global", "Use global 3D axes");
    private readonly SegmentedButton _localSpace = SpaceButton("Local", "Use the selected instance local 3D axes");
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _floatingMotionTimer = new() { Interval = 16 };
    private readonly FlowLayoutPanel _modeStrip = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = new Padding(0, 1, 0, 1),
        BackColor = Theme.Panel
    };
    private readonly FlowLayoutPanel _spaceStrip = new()
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
        ColumnCount = 5,
        RowCount = 8,
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
    private SpatialTransformSpace _space;
    private SpatialPivotKind _pivotKind;
    private bool _spaceSelectionEnabled = true;
    private Point _floatingTargetLocation;
    private float _floatingPosition = 1f;
    private float _floatingVelocity;
    private long _floatingLastTickTimestamp;
    private bool _floatingTargetLocationInitialized;
    private bool _floatingVisibilityRequested;
    private int _floatingMotionGeneration;

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
        AccessibleName = "3D transform floating panel";
        AccessibleDescription = "Edit the selected instance position, rotation, scale, and pivots";
        AccessibleRole = AccessibleRole.Pane;

        _inputs =
        [
            _positionX, _positionY, _positionZ,
            _rotationX, _rotationY, _rotationZ,
            _scaleX, _scaleY, _scaleZ,
            _rotationPivotX, _rotationPivotY, _rotationPivotZ,
            _scalePivotX, _scalePivotY, _scalePivotZ
        ];

        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        for (var index = 0; index < 3; index++)
        {
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        }
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        for (var index = 0; index < 5; index++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        }

        var axisX = AxisLabel("X", "X axis");
        var axisY = AxisLabel("Y", "Y axis");
        var axisZ = AxisLabel("Z", "Z axis");
        var space = RowLabel("Space");
        var position = RowLabel("Position");
        var rotation = RowLabel("Rotation");
        var scale = RowLabel("Scale");
        _labels = [axisX, axisY, axisZ, space, position, rotation, scale];

        _modeStrip.Controls.AddRange([_moveMode, _rotateMode, _scaleMode]);
        _spaceStrip.Controls.AddRange([_globalSpace, _localSpace]);
        _layout.Controls.Add(_modeStrip, 0, 0);
        _layout.SetColumnSpan(_modeStrip, 5);
        _layout.Controls.Add(space, 0, 1);
        _layout.Controls.Add(_spaceStrip, 1, 1);
        _layout.SetColumnSpan(_spaceStrip, 4);
        _layout.Controls.Add(axisX, 1, 2);
        _layout.Controls.Add(axisY, 2, 2);
        _layout.Controls.Add(axisZ, 3, 2);
        AddRow(position, 3, _positionX, _positionY, _positionZ);
        AddRow(rotation, 4, _rotationX, _rotationY, _rotationZ);
        AddRow(scale, 5, _scaleX, _scaleY, _scaleZ);
        AddPivotRow(
            _rotationPivotMode,
            6,
            _rotationPivotX,
            _rotationPivotY,
            _rotationPivotZ,
            _resetRotationPivot);
        AddPivotRow(
            _scalePivotMode,
            7,
            _scalePivotX,
            _scalePivotY,
            _scalePivotZ,
            _resetScalePivot);

        _moveMode.Click += (_, _) => SelectMode(SpatialTransformMode.Move);
        _rotateMode.Click += (_, _) => SelectMode(SpatialTransformMode.Rotate);
        _scaleMode.Click += (_, _) => SelectMode(SpatialTransformMode.Scale);
        _rotationPivotMode.Click += (_, _) => TogglePivotKind(SpatialPivotKind.Rotation);
        _scalePivotMode.Click += (_, _) => TogglePivotKind(SpatialPivotKind.Scale);
        _resetRotationPivot.Click += (_, _) => ResetPivot(SpatialPivotKind.Rotation);
        _resetScalePivot.Click += (_, _) => ResetPivot(SpatialPivotKind.Scale);
        _globalSpace.Click += (_, _) => SetSpaceCore(SpatialTransformSpace.Global, notify: true);
        _localSpace.Click += (_, _) => SetSpaceCore(SpatialTransformSpace.Local, notify: true);
        ApplyModePresentation();
        ApplySpacePresentation();
        ApplyPivotPresentation();
        Theme.StyleToolTip(_toolTip);
        _toolTip.SetToolTip(_rotationPivotMode, _rotationPivotMode.AccessibleDescription);
        _toolTip.SetToolTip(_scalePivotMode, _scalePivotMode.AccessibleDescription);
        _toolTip.SetToolTip(_resetRotationPivot, _resetRotationPivot.AccessibleName);
        _toolTip.SetToolTip(_resetScalePivot, _resetScalePivot.AccessibleName);
        _floatingMotionTimer.Tick += (_, _) => TickFloatingMotion();

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
    public event EventHandler<SpatialTransformSpaceChangedEventArgs>? SpaceChanged;
    public event EventHandler<SpatialPivotKindChangedEventArgs>? PivotKindChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;
    public event EventHandler? InteractionCanceled;

    public int PreferredPanelHeight => PanelHeight;

    public SpatialTransformMode Mode => _mode;
    public SpatialTransformSpace Space => _space;
    public SpatialPivotKind PivotKind => _pivotKind;
    public bool FloatingVisibilityRequested => _floatingVisibilityRequested;
    public bool FloatingMotionActive => _floatingMotionTimer.Enabled;
    public Point FloatingTargetLocation => _floatingTargetLocation;
    public int FloatingMotionGeneration => _floatingMotionGeneration;

    public SpatialTransformValues Values => new(
        (float)_positionX.Value,
        (float)_positionY.Value,
        (float)_positionZ.Value,
        (float)_rotationX.Value,
        (float)_rotationY.Value,
        (float)_rotationZ.Value,
        (float)_scaleX.Value,
        (float)_scaleY.Value,
        (float)_scaleZ.Value,
        new Vector3(
            (float)_rotationPivotX.Value,
            (float)_rotationPivotY.Value,
            (float)_rotationPivotZ.Value),
        new Vector3(
            (float)_scalePivotX.Value,
            (float)_scalePivotY.Value,
            (float)_scalePivotZ.Value));

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
            _rotationPivotX.Value = PositionDecimal(value.RotationPivot.X);
            _rotationPivotY.Value = PositionDecimal(value.RotationPivot.Y);
            _rotationPivotZ.Value = PositionDecimal(value.RotationPivot.Z);
            _scalePivotX.Value = PositionDecimal(value.ScalePivot.X);
            _scalePivotY.Value = PositionDecimal(value.ScalePivot.Y);
            _scalePivotZ.Value = PositionDecimal(value.ScalePivot.Z);
        }
        finally
        {
            _updating = false;
        }

        ApplyEnabledState();
    }

    public void SetMode(SpatialTransformMode mode) => SetMode(mode, notify: false);

    public void SetPivotKind(SpatialPivotKind kind) => SetPivotKind(kind, notify: false);

    public void SetSpace(SpatialTransformSpace space, bool enabled)
    {
        _spaceSelectionEnabled = enabled;
        SetSpaceCore(space, notify: false);
    }

    public void SetFloatingTargetLocation(Point location)
    {
        if (_floatingTargetLocationInitialized && _floatingTargetLocation == location) return;
        _floatingTargetLocation = location;
        _floatingTargetLocationInitialized = true;
        ApplyFloatingLocation();
    }

    public void SetFloatingVisibility(bool visible, bool animate)
    {
        var targetChanged = _floatingVisibilityRequested != visible;
        if (targetChanged)
        {
            _floatingVisibilityRequested = visible;
            _floatingMotionGeneration++;
        }

        if (visible)
        {
            if (!Visible) Visible = true;
            ApplyFloatingLocation();
            BringToFront();
        }

        var target = visible ? 0f : 1f;
        var settled = Math.Abs(_floatingPosition - target) <= FloatingPositionEpsilon
            && Math.Abs(_floatingVelocity) <= FloatingVelocityEpsilon;
        if (!animate || !UiMotion.AnimationsEnabled)
        {
            if (targetChanged || _floatingMotionTimer.Enabled || !settled) CompleteFloatingMotion();
            return;
        }

        if (!targetChanged && (settled || _floatingMotionTimer.Enabled)) return;

        _floatingLastTickTimestamp = Stopwatch.GetTimestamp();
        if (!_floatingMotionTimer.Enabled) _floatingMotionTimer.Start();
    }

    internal static bool StepFloatingSpring(
        ref float position,
        ref float velocity,
        float target,
        float deltaSeconds)
    {
        var dt = Math.Clamp(deltaSeconds, 0.008f, 0.034f);
        velocity += ((target - position) * FloatingSpringStiffness - velocity * FloatingSpringDamping) * dt;
        position += velocity * dt;
        if (!float.IsFinite(position) || !float.IsFinite(velocity))
        {
            position = target;
            velocity = 0;
            return true;
        }

        if (Math.Abs(position - target) > FloatingPositionEpsilon
            || Math.Abs(velocity) > FloatingVelocityEpsilon)
        {
            return false;
        }

        position = target;
        velocity = 0;
        return true;
    }

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
        if (disposing)
        {
            if (_interactionActive) CancelPanelInteraction();
            _floatingMotionTimer.Stop();
            _floatingMotionTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool CanEdit => Enabled && _hasState && _editingRequested;

    private int FloatingHiddenOffset => Math.Max(16, (int)MathF.Round(24f * DeviceDpi / 96f));

    private void TickFloatingMotion()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            CompleteFloatingMotion();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var dt = _floatingLastTickTimestamp == 0
            ? _floatingMotionTimer.Interval / 1000f
            : (float)Stopwatch.GetElapsedTime(_floatingLastTickTimestamp, now).TotalSeconds;
        _floatingLastTickTimestamp = now;
        var target = _floatingVisibilityRequested ? 0f : 1f;
        var settled = StepFloatingSpring(ref _floatingPosition, ref _floatingVelocity, target, dt);
        ApplyFloatingLocation();
        if (settled) CompleteFloatingMotion();
    }

    private void CompleteFloatingMotion()
    {
        _floatingMotionTimer.Stop();
        _floatingLastTickTimestamp = 0;
        _floatingPosition = _floatingVisibilityRequested ? 0f : 1f;
        _floatingVelocity = 0;
        if (_floatingVisibilityRequested)
        {
            if (!Visible) Visible = true;
            SetFloatingLocation(_floatingTargetLocation);
            BringToFront();
            return;
        }

        Visible = false;
        SetFloatingLocation(_floatingTargetLocation);
    }

    private void ApplyFloatingLocation()
    {
        if (!_floatingTargetLocationInitialized) return;
        var motionPresented = _floatingVisibilityRequested || _floatingMotionTimer.Enabled;
        var offset = motionPresented ? (int)MathF.Round(_floatingPosition * FloatingHiddenOffset) : 0;
        SetFloatingLocation(new Point(_floatingTargetLocation.X + offset, _floatingTargetLocation.Y));
    }

    private void SetFloatingLocation(Point location)
    {
        if (Location != location) Location = location;
    }

    private void HandleValueChanged(object? sender, EventArgs e)
    {
        if (_updating || sender is not ModernNumericUpDown input) return;
        var oneShot = !_interactionActive;
        if (oneShot) BeginPanelInteraction();
        ValuesChanged?.Invoke(this, new SpatialTransformValuesChangedEventArgs(Values, ValueGroup(input)));
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
        _rotationPivotMode.Enabled = canEdit;
        _scalePivotMode.Enabled = canEdit;
        _resetRotationPivot.Enabled = canEdit;
        _resetScalePivot.Enabled = canEdit;
        var canChooseSpace = Enabled && _spaceSelectionEnabled && _pivotKind == SpatialPivotKind.None;
        _globalSpace.Enabled = canChooseSpace;
        _localSpace.Enabled = canChooseSpace;
        var color = canEdit ? Theme.Muted : Theme.Mix(Theme.Muted, Theme.Panel, 0.58f);
        foreach (var label in _labels) label.ForeColor = color;
        AccessibleDescription = canEdit
            ? UiLocalization.T("Edit selected instance 3D transform")
            : UiLocalization.T("No editable 3D transform selected");
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
        StyleModeButton(_moveMode, _pivotKind == SpatialPivotKind.None && _mode == SpatialTransformMode.Move);
        StyleModeButton(_rotateMode, _pivotKind == SpatialPivotKind.None && _mode == SpatialTransformMode.Rotate);
        StyleModeButton(_scaleMode, _pivotKind == SpatialPivotKind.None && _mode == SpatialTransformMode.Scale);
    }

    private void SelectMode(SpatialTransformMode mode)
    {
        SetPivotKind(SpatialPivotKind.None, notify: true);
        SetMode(mode, notify: true);
    }

    private void TogglePivotKind(SpatialPivotKind kind)
    {
        SetPivotKind(_pivotKind == kind ? SpatialPivotKind.None : kind, notify: true);
    }

    private void SetPivotKind(SpatialPivotKind kind, bool notify)
    {
        if (!Enum.IsDefined(kind)) return;
        var changed = _pivotKind != kind;
        _pivotKind = kind;
        ApplyModePresentation();
        ApplyPivotPresentation();
        ApplySpacePresentation();
        ApplyEnabledState();
        if (changed && notify) PivotKindChanged?.Invoke(this, new SpatialPivotKindChangedEventArgs(kind));
    }

    private void ApplyPivotPresentation()
    {
        StyleModeButton(_rotationPivotMode, _pivotKind == SpatialPivotKind.Rotation);
        StyleModeButton(_scalePivotMode, _pivotKind == SpatialPivotKind.Scale);
    }

    private void ResetPivot(SpatialPivotKind kind)
    {
        if (!CanEdit || kind == SpatialPivotKind.None) return;
        SetPivotKind(kind, notify: true);
        var inputs = kind == SpatialPivotKind.Rotation
            ? new[] { _rotationPivotX, _rotationPivotY, _rotationPivotZ }
            : new[] { _scalePivotX, _scalePivotY, _scalePivotZ };
        if (inputs.All(input => input.Value == 0)) return;
        var oneShot = !_interactionActive;
        if (oneShot) BeginPanelInteraction();
        _updating = true;
        try
        {
            foreach (var input in inputs) input.Value = 0;
        }
        finally
        {
            _updating = false;
        }
        var group = kind == SpatialPivotKind.Rotation
            ? SpatialTransformValueGroup.RotationPivot
            : SpatialTransformValueGroup.ScalePivot;
        ValuesChanged?.Invoke(this, new SpatialTransformValuesChangedEventArgs(Values, group));
        if (oneShot) CompletePanelInteraction();
    }

    private SpatialTransformValueGroup ValueGroup(ModernNumericUpDown input)
    {
        if (ReferenceEquals(input, _positionX)
            || ReferenceEquals(input, _positionY)
            || ReferenceEquals(input, _positionZ))
        {
            return SpatialTransformValueGroup.Position;
        }
        if (ReferenceEquals(input, _rotationX)
            || ReferenceEquals(input, _rotationY)
            || ReferenceEquals(input, _rotationZ))
        {
            return SpatialTransformValueGroup.Rotation;
        }
        if (ReferenceEquals(input, _scaleX)
            || ReferenceEquals(input, _scaleY)
            || ReferenceEquals(input, _scaleZ))
        {
            return SpatialTransformValueGroup.Scale;
        }
        return ReferenceEquals(input, _rotationPivotX)
            || ReferenceEquals(input, _rotationPivotY)
            || ReferenceEquals(input, _rotationPivotZ)
                ? SpatialTransformValueGroup.RotationPivot
                : SpatialTransformValueGroup.ScalePivot;
    }

    private void SetSpaceCore(SpatialTransformSpace space, bool notify)
    {
        if (!Enum.IsDefined(space)
            || notify && (!_spaceSelectionEnabled || _pivotKind != SpatialPivotKind.None)) return;
        var changed = _space != space;
        _space = space;
        ApplySpacePresentation();
        ApplyEnabledState();
        if (changed && notify) SpaceChanged?.Invoke(this, new SpatialTransformSpaceChangedEventArgs(space));
    }

    private void ApplySpacePresentation()
    {
        var presented = _spaceSelectionEnabled && _pivotKind == SpatialPivotKind.None
            ? _space
            : SpatialTransformSpace.Local;
        Theme.StyleSegmentedButton(_globalSpace, presented == SpatialTransformSpace.Global);
        Theme.StyleSegmentedButton(_localSpace, presented == SpatialTransformSpace.Local);
        _globalSpace.TabStop = presented == SpatialTransformSpace.Global;
        _localSpace.TabStop = presented == SpatialTransformSpace.Local;
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

    private void AddPivotRow(
        Button label,
        int row,
        ModernNumericUpDown x,
        ModernNumericUpDown y,
        ModernNumericUpDown z,
        Button reset)
    {
        _layout.Controls.Add(label, 0, row);
        _layout.Controls.Add(x, 1, row);
        _layout.Controls.Add(y, 2, row);
        _layout.Controls.Add(z, 3, row);
        _layout.Controls.Add(reset, 4, row);
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

    private static Button PivotButton(string text, string accessibleDescription)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 4, 1),
            Font = Theme.UiFont(8.2f),
            AutoEllipsis = true,
            AccessibleName = text.StartsWith('R') ? "Edit rotation pivot" : "Edit scale pivot",
            AccessibleDescription = accessibleDescription,
            AccessibleRole = AccessibleRole.RadioButton
        };
    }

    private static SvgIconButton ResetPivotButton(string accessibleName)
    {
        return new SvgIconButton(SvgIconKind.RestoreSize)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(1, 2, 0, 2),
            AccessibleName = accessibleName,
            AccessibleDescription = accessibleName
        };
    }

    private static SegmentedButton SpaceButton(string text, string accessibleDescription)
    {
        var button = new SegmentedButton
        {
            Text = text,
            Width = 82,
            Height = 26,
            Margin = new Padding(0, 0, 2, 0),
            AccessibleName = $"3D transform {text.ToLowerInvariant()} coordinates",
            AccessibleDescription = accessibleDescription,
            AccessibleRole = AccessibleRole.RadioButton
        };
        Theme.StyleSegmentedButton(button);
        return button;
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
