namespace VectorAnimationEngine;

internal sealed class ShotDirectorMicroMotionEventArgs(string shotId, SceneShotMicroMotionSettings settings,
    bool useHandheldDefaults = false) : EventArgs
{
    public string ShotId { get; } = shotId;
    public SceneShotMicroMotionSettings Settings { get; } = settings;
    public bool UseHandheldDefaults { get; } = useHandheldDefaults;
}

internal sealed partial class ShotDirectorPanel
{
    private readonly Label _microMotionHeading = DesignCaption("Micro motion");
    private readonly CameraSectionToggle _microMotionDisclosure = new("Camera micro motion", expanded: false);
    private readonly Label _microMotionEnabledCaption = DesignCaption("Enabled", true);
    private readonly ModernToggleSwitch _microMotionEnabled = new()
        { AutoSize = false, AccessibleName = "Enable camera micro motion" };
    private readonly ModernNumericUpDown _microMotionHorizontal = MotionNumber("Horizontal amplitude", 0, 5, 0.25m, 2, "%");
    private readonly ModernNumericUpDown _microMotionVertical = MotionNumber("Vertical amplitude", 0, 5, 0.25m, 2, "%");
    private readonly ModernNumericUpDown _microMotionRotation = MotionNumber("Roll amplitude", 0, 5, 0.15m, 2, "°");
    private readonly ModernNumericUpDown _microMotionSpeed = MotionNumber("Motion speed", 0.01m, 20, 0.8m, 2, "Hz");
    private readonly ModernNumericUpDown _microMotionSeed = MotionNumber("Motion seed", 0, 999999, 1, 0, "");

    private IEnumerable<ModernNumericUpDown> MicroMotionEditors =>
        [_microMotionHorizontal, _microMotionVertical, _microMotionRotation, _microMotionSpeed, _microMotionSeed];

    private static ModernNumericUpDown MotionNumber(string name, decimal minimum, decimal maximum,
        decimal value, int decimalPlaces, string suffix) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimalPlaces,
        Increment = decimalPlaces == 0 ? 1 : 0.01m, Suffix = suffix, ShowSuffix = suffix.Length > 0,
        AccessibleName = name
    };

    private void InitializeMicroMotion()
    {
        _inspector.Content.Controls.AddRange([_microMotionHeading, _microMotionDisclosure,
            _microMotionEnabledCaption, _microMotionEnabled]);
        InitializeDesignedSection(_microMotionHeading, _microMotionDisclosure);
        _microMotionEnabled.CheckedChanged += (_, _) =>
        {
            if (_updatingEditors) return;
            ShotEditStarted?.Invoke(this, EventArgs.Empty);
            CommitMicroMotion();
            ShotEditCompleted?.Invoke(this, EventArgs.Empty);
            PerformLayout();
        };
        _help.SetToolTip(_microMotionDisclosure, UiLocalization.T("Adds repeatable camera drift without changing framing keys."));
        _help.SetToolTip(_microMotionHeading, UiLocalization.T("Adds repeatable camera drift without changing framing keys."));
        foreach (var editor in MicroMotionEditors)
        {
            Theme.StyleNumeric(editor);
            editor.Font = Theme.UiFont(11f);
            var label = editor.AccessibleName switch
            {
                "Horizontal amplitude" => "Horizontal drift",
                "Vertical amplitude" => "Vertical drift",
                "Roll amplitude" => "Roll",
                "Motion speed" => "Drift speed",
                _ => "Random seed"
            };
            AddEditor(editor, label);
            editor.Enter += (_, _) => { if (!_updatingEditors) ShotEditStarted?.Invoke(this, EventArgs.Empty); };
            editor.Leave += (_, _) => { if (!_updatingEditors) ShotEditCompleted?.Invoke(this, EventArgs.Empty); };
            editor.InteractionStarted += (_, _) => { if (!_updatingEditors) ShotEditStarted?.Invoke(this, EventArgs.Empty); };
            editor.InteractionCompleted += (_, _) => { if (!_updatingEditors) ShotEditCompleted?.Invoke(this, EventArgs.Empty); };
            editor.InteractionCanceled += (_, _) => { if (!_updatingEditors) ShotEditCanceled?.Invoke(this, EventArgs.Empty); };
            editor.ValueChanged += (_, _) => { if (!_updatingEditors) CommitMicroMotion(); };
        }
        _help.SetToolTip(_microMotionHorizontal, UiLocalization.T("Maximum drift as a percentage of the camera frame width."));
        _help.SetToolTip(_microMotionVertical, UiLocalization.T("Maximum drift as a percentage of the camera frame height."));
        _help.SetToolTip(_microMotionSpeed, UiLocalization.T("Higher values make the camera drift faster. Time follows the project FPS."));
        _help.SetToolTip(_microMotionSeed, UiLocalization.T("Change the seed for another repeatable motion pattern."));
    }

    private void SetMicroMotionEditors(SceneShotMicroMotionSettings settings)
    {
        var motion = settings.Normalized();
        var layoutChanged = _microMotionEnabled.Checked != motion.Enabled;
        _microMotionEnabled.Checked = motion.Enabled;
        _microMotionHorizontal.Value = (decimal)motion.HorizontalPercent;
        _microMotionVertical.Value = (decimal)motion.VerticalPercent;
        _microMotionRotation.Value = (decimal)motion.RotationDegrees;
        _microMotionSpeed.Value = (decimal)motion.Speed;
        _microMotionSeed.Value = motion.Seed;
        if (layoutChanged) PerformLayout();
    }

    internal void SetSelectedShotMicroMotion(SceneShotMicroMotionSettings settings)
    {
        var index = SelectedShotIndex();
        if (index < 0) return;
        var shots = (ShotDirectorItem[])_state.Shots.Clone();
        shots[index] = shots[index] with { MicroMotion = settings };
        _state = _state with { Shots = shots };
        _updatingEditors = true;
        try { SetMicroMotionEditors(settings); }
        finally { _updatingEditors = false; }
        ApplyEditorState();
    }

    private void CommitMicroMotion()
    {
        if (_updatingEditors || !_state.IsAvailable || !ActiveCameraAllowed || ActiveShot() is not { } shot) return;
        var motion = shot.MicroMotion with
        {
            Enabled = _microMotionEnabled.Checked,
            HorizontalPercent = (float)_microMotionHorizontal.Value,
            VerticalPercent = (float)_microMotionVertical.Value,
            RotationDegrees = (float)_microMotionRotation.Value,
            Speed = (float)_microMotionSpeed.Value,
            Seed = (int)_microMotionSeed.Value
        };
        if ((motion with { Enabled = shot.MicroMotion.Enabled }) != shot.MicroMotion)
            motion = motion with { HandheldDefaultsInitialized = true };
        if (motion == shot.MicroMotion) return;
        ShotMicroMotionCommitted?.Invoke(this, new ShotDirectorMicroMotionEventArgs(shot.Id, motion));
    }

    private int LayoutMicroMotion(int top, int right, ref int tabIndex)
    {
        var left = ScaleMetric(16);
        var width = Math.Max(1, right - left);
        _microMotionHeading.SetBounds(left, top, Math.Max(1, width - ScaleMetric(36)), ScaleMetric(28));
        _microMotionDisclosure.SetBounds(right - ScaleMetric(28), top, ScaleMetric(28), ScaleMetric(28));
        _microMotionDisclosure.TabIndex = tabIndex++;
        top += ScaleMetric(36);
        _microMotionEnabled.Visible = _microMotionDisclosure.Expanded;
        _microMotionEnabledCaption.Visible = _microMotionDisclosure.Expanded;
        _microMotionEnabled.TabStop = _microMotionDisclosure.Expanded && _microMotionEnabled.Enabled;
        if (_microMotionDisclosure.Expanded)
        {
            LayoutDesignedField(_microMotionEnabled, top, right, _microMotionEnabledCaption);
            _microMotionEnabled.TabIndex = tabIndex++;
            top += ScaleMetric(RowStep);
        }
        foreach (var editor in MicroMotionEditors)
        {
            var visible = _microMotionDisclosure.Expanded;
            editor.Visible = visible;
            if (editor.Tag is Label label) label.Visible = visible;
            editor.TabStop = visible && editor.Enabled;
            if (!visible) continue;
            editor.TabIndex = tabIndex++;
            LayoutDesignedField(editor, top, right);
            top += ScaleMetric(RowStep);
        }
        return top;
    }

    private void ApplyMicroMotionEditorState(bool editable)
    {
        _microMotionEnabled.Enabled = editable;
        _microMotionEnabled.TabStop = editable && _microMotionDisclosure.Expanded;
        foreach (var editor in MicroMotionEditors)
        {
            editor.Enabled = editable && _microMotionEnabled.Checked;
            editor.TabStop = editor.Enabled && _microMotionDisclosure.Expanded;
        }
    }
}
