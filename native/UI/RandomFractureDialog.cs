namespace VectorAnimationEngine;

internal sealed class RandomFractureDialog : ModernDialogForm
{
    private readonly ModernNumericUpDown _fragmentCount = Numeric(
        "Fragment count limit",
        2,
        256,
        24,
        0,
        1);
    private readonly ModernNumericUpDown _edgeCount = Numeric(
        "Fragment edge count",
        3,
        32,
        6,
        0,
        1);
    private readonly ModernNumericUpDown _randomness = Numeric(
        "Fragment randomness",
        0,
        100,
        72,
        0,
        1);
    private readonly ModernNumericUpDown _randomSeed = Numeric(
        "Random seed",
        -9_999_999,
        9_999_999,
        1337,
        0,
        1);
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _forceAlgorithm = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernNumericUpDown _fractureStrength = Numeric(
        "Fracture strength in pixels per frame",
        0,
        500,
        28,
        1,
        1);
    private readonly ModernNumericUpDown _directionAngle = Numeric(
        "Force direction angle in degrees",
        -180,
        180,
        -90,
        1,
        1);
    private readonly ModernNumericUpDown _rotationStrength = Numeric(
        "Rotation strength in degrees per frame",
        -90,
        90,
        3,
        1,
        0.1m);
    private readonly CheckBox _preserveStroke = Check("Preserve stroke", "Keep the source stroke on every fragment", true);
    private readonly CheckBox _generateAnimation = Check("Generate animation", "Create keyframes for the fracture simulation", false);
    private readonly ModernNumericUpDown _animationFrames = Numeric(
        "Animation frame count",
        1,
        240,
        36,
        0,
        1);
    private readonly CheckBox _allowOverlap = Check("Allow fragment overlap", "Allow fragments to overlap during the simulation", false);
    private readonly ModernNumericUpDown _groundPosition = Numeric(
        "Ground position as a percentage of the source height",
        -200,
        400,
        100,
        1,
        1);
    private readonly ModernNumericUpDown _gravity = Numeric(
        "Gravity in pixels per frame squared",
        0,
        50,
        0.8m,
        2,
        0.1m);
    private readonly ModernNumericUpDown _airResistance = Numeric(
        "Air resistance percentage",
        0,
        100,
        2,
        1,
        1);
    private readonly ModernNumericUpDown _bounce = Numeric(
        "Bounce percentage",
        0,
        100,
        35,
        1,
        1);
    private readonly ModernNumericUpDown _previewFps = Numeric(
        "Preview FPS",
        1,
        120,
        30,
        0,
        1);
    private readonly System.Windows.Forms.Timer _previewChangeDebounceTimer = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 34 };
    private readonly Label _previewFrameLabel = new();
    private readonly Label _status = new();
    private Button? _playButton;
    private Button? _resetButton;
    private Button? _applyButton;
    private bool _updating;
    private bool _hasValidPreview;
    private int _previewFrame;

    public RandomFractureDialog(RandomFractureOptions? initialOptions = null)
        : base("Random Fracture", new Size(560, 720))
    {
        MinimumSize = new Size(480, 560);
        AccessibleName = "Random Fracture";
        ConfigureCombos();
        BuildUi();
        WireEvents();
        SetOptions(initialOptions ?? new RandomFractureOptions());
        UpdatePreviewTimerInterval();
        UpdatePreviewControls();

        _applyButton = AddDialogAction(
            "Apply",
            DialogResult.OK,
            DialogActionStyle.Primary,
            () => _hasValidPreview);
        var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
        AcceptButton = _applyButton;
        CancelButton = cancel;
        SetPreviewStatus("Preview pending", valid: false);
        UiLocalization.Watch(this);
    }

    public RandomFractureOptions Options => ReadOptions().Normalize();

    public event EventHandler? PreviewChanged;
    public event EventHandler? PreviewFrameChanged;

    internal int PreviewFrame => _previewFrame;

    internal void SetPreviewStatus(string message, bool valid = true)
    {
        _hasValidPreview = valid;
        _status.Text = UiLocalization.T(string.IsNullOrWhiteSpace(message) ? "Preview ready" : message);
        _status.ForeColor = valid ? Theme.AccentLabel : Theme.Warning;
        if (_applyButton is not null) _applyButton.Enabled = valid;
        UpdatePreviewControls();
        _status.Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _previewChangeDebounceTimer.Stop();
        StopPreviewPlayback();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewChangeDebounceTimer.Stop();
            _previewChangeDebounceTimer.Dispose();
            _previewTimer.Stop();
            _previewTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ConfigureCombos()
    {
        _mode.Items.AddRange(["Linear", "Pointillize"]);
        _mode.AccessibleName = "Fracture mode";
        _mode.AccessibleDescription = "Choose the fracture layout mode";
        Theme.StyleComboBox(_mode);

        _forceAlgorithm.Items.AddRange(["Radial", "Directional", "Random"]);
        _forceAlgorithm.AccessibleName = "Force algorithm";
        _forceAlgorithm.AccessibleDescription = "Choose how fracture force is distributed";
        Theme.StyleComboBox(_forceAlgorithm);
    }

    private void BuildUi()
    {
        DialogContent.Padding = Padding.Empty;
        var scroll = new ThemedScrollPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            AccessibleName = "Random Fracture parameters"
        };
        scroll.ContentPadding = new Padding(20, 14, 20, 18);
        DialogContent.Controls.Add(scroll);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Content.Controls.Add(layout);

        _status.Dock = DockStyle.Fill;
        _status.Height = 28;
        _status.Margin = new Padding(0, 0, 0, 8);
        _status.Padding = new Padding(8, 0, 8, 0);
        _status.BackColor = Theme.PanelStrong;
        _status.Font = Theme.UiFont(8.8f);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        layout.Controls.Add(_status, 0, layout.RowCount++);
        layout.SetColumnSpan(_status, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        AddSection(layout, "Geometry");
        AddField(layout, "Fragment count limit", _fragmentCount);
        AddField(layout, "Fragment edge count", _edgeCount);
        AddField(layout, "Randomness (%)", _randomness);
        AddField(layout, "Random seed", _randomSeed);
        AddField(layout, "Mode", _mode);

        AddSection(layout, "Force");
        AddField(layout, "Force algorithm", _forceAlgorithm);
        AddField(layout, "Fracture strength (px/frame)", _fractureStrength);
        AddField(layout, "Direction angle (degrees)", _directionAngle);
        AddField(layout, "Rotation strength (degrees/frame)", _rotationStrength);
        AddCheckField(layout, _preserveStroke);

        AddSection(layout, "Animation");
        AddCheckField(layout, _generateAnimation);
        AddField(layout, "Animation frames", _animationFrames);
        AddCheckField(layout, _allowOverlap);
        AddField(layout, "Ground position (%)", _groundPosition);
        AddField(layout, "Gravity (px/frame^2)", _gravity);
        AddField(layout, "Air resistance (%)", _airResistance);
        AddField(layout, "Bounce (%)", _bounce);

        AddSection(layout, "Preview");
        AddField(layout, "Preview FPS", _previewFps);
        AddPreviewControls(layout);
    }

    private void WireEvents()
    {
        foreach (var numeric in NumericInputs()) numeric.ValueChanged += (_, _) => QueuePreviewChanged();
        _mode.SelectedIndexChanged += (_, _) => QueuePreviewChanged();
        _forceAlgorithm.SelectedIndexChanged += (_, _) =>
        {
            UpdateDependentState();
            QueuePreviewChanged();
        };
        _preserveStroke.CheckedChanged += (_, _) => QueuePreviewChanged();
        _generateAnimation.CheckedChanged += (_, _) =>
        {
            UpdateDependentState();
            QueuePreviewChanged();
        };
        _allowOverlap.CheckedChanged += (_, _) => QueuePreviewChanged();
        _previewFps.ValueChanged += (_, _) => UpdatePreviewTimerInterval();
        _previewChangeDebounceTimer.Tick += (_, _) => FlushPreviewChanged();
        _previewTimer.Tick += (_, _) => AdvancePreviewFrame();
        _playButton!.Click += (_, _) => TogglePreviewPlayback();
        _resetButton!.Click += (_, _) => ResetPreview();
    }

    private void SetOptions(RandomFractureOptions options)
    {
        options = options.Normalize();
        _updating = true;
        try
        {
            _fragmentCount.Value = options.FragmentCountLimit;
            _edgeCount.Value = options.EdgeCount;
            _randomness.Value = (decimal)Math.Round(options.FragmentRandomness * 100f, 0);
            _randomSeed.Value = options.RandomSeed;
            _mode.SelectedIndex = options.Mode == RandomFractureMode.Linear ? 0 : 1;
            _forceAlgorithm.SelectedIndex = options.ForceAlgorithm switch
            {
                RandomFractureForceAlgorithm.Directional => 1,
                RandomFractureForceAlgorithm.Random => 2,
                _ => 0
            };
            _fractureStrength.Value = (decimal)options.FractureStrength;
            _directionAngle.Value = (decimal)options.DirectionAngleDegrees;
            _rotationStrength.Value = (decimal)options.RotationStrengthDegrees;
            _preserveStroke.Checked = options.PreserveStroke;
            _generateAnimation.Checked = options.GenerateAnimation;
            _animationFrames.Value = options.AnimationFrames;
            _allowOverlap.Checked = options.AllowOverlap;
            _groundPosition.Value = (decimal)options.GroundPositionPercent;
            _gravity.Value = (decimal)options.GravityPixelsPerFrameSquared;
            _airResistance.Value = (decimal)options.AirResistancePercent;
            _bounce.Value = (decimal)options.BouncePercent;
        }
        finally
        {
            _updating = false;
        }

        UpdateDependentState();
    }

    private RandomFractureOptions ReadOptions()
    {
        return new RandomFractureOptions
        {
            FragmentCountLimit = (int)_fragmentCount.Value,
            EdgeCount = (int)_edgeCount.Value,
            FragmentRandomness = (float)_randomness.Value / 100f,
            RandomSeed = (int)_randomSeed.Value,
            Mode = _mode.SelectedIndex == 0 ? RandomFractureMode.Linear : RandomFractureMode.Pointillize,
            ForceAlgorithm = _forceAlgorithm.SelectedIndex switch
            {
                1 => RandomFractureForceAlgorithm.Directional,
                2 => RandomFractureForceAlgorithm.Random,
                _ => RandomFractureForceAlgorithm.Radial
            },
            FractureStrength = (float)_fractureStrength.Value,
            DirectionAngleDegrees = (float)_directionAngle.Value,
            RotationStrengthDegrees = (float)_rotationStrength.Value,
            PreserveStroke = _preserveStroke.Checked,
            GenerateAnimation = _generateAnimation.Checked,
            AnimationFrames = (int)_animationFrames.Value,
            AllowOverlap = _allowOverlap.Checked,
            GroundPositionPercent = (float)_groundPosition.Value,
            GravityPixelsPerFrameSquared = (float)_gravity.Value,
            AirResistancePercent = (float)_airResistance.Value,
            BouncePercent = (float)_bounce.Value
        };
    }

    private void UpdateDependentState()
    {
        var animation = _generateAnimation.Checked;
        _animationFrames.Enabled = animation;
        _allowOverlap.Enabled = animation;
        _groundPosition.Enabled = animation;
        _gravity.Enabled = animation;
        _airResistance.Enabled = animation;
        _bounce.Enabled = animation;
        _directionAngle.Enabled = _forceAlgorithm.SelectedIndex == 1;
        if (!animation)
        {
            StopPreviewPlayback();
            SetPreviewFrame(0, notify: false);
        }

        UpdatePreviewControls();
    }

    private void QueuePreviewChanged()
    {
        if (_updating) return;
        StopPreviewPlayback();
        SetPreviewFrame(0, notify: false);
        _hasValidPreview = false;
        UpdatePreviewControls();
        _previewChangeDebounceTimer.Stop();
        _previewChangeDebounceTimer.Start();
    }

    private void FlushPreviewChanged()
    {
        _previewChangeDebounceTimer.Stop();
        if (_updating || IsDisposed) return;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private int PreviewFrameCount => _generateAnimation.Checked
        ? Math.Max(1, (int)_animationFrames.Value + 1)
        : 1;

    private void TogglePreviewPlayback()
    {
        if (_previewTimer.Enabled)
        {
            StopPreviewPlayback();
            return;
        }

        if (!_hasValidPreview || PreviewFrameCount <= 1) return;
        if (_previewFrame >= PreviewFrameCount - 1) SetPreviewFrame(0, notify: true);
        UpdatePreviewTimerInterval();
        _previewTimer.Start();
        UpdatePreviewControls();
    }

    private void StopPreviewPlayback()
    {
        if (!_previewTimer.Enabled) return;
        _previewTimer.Stop();
        UpdatePreviewControls();
    }

    private void ResetPreview()
    {
        StopPreviewPlayback();
        SetPreviewFrame(0, notify: true);
    }

    private void AdvancePreviewFrame()
    {
        var count = PreviewFrameCount;
        if (!_hasValidPreview || count <= 1)
        {
            StopPreviewPlayback();
            return;
        }

        var next = _previewFrame + 1;
        if (next >= count) next = 0;
        SetPreviewFrame(next, notify: true);
    }

    private void SetPreviewFrame(int frame, bool notify)
    {
        var next = Math.Clamp(frame, 0, PreviewFrameCount - 1);
        if (_previewFrame == next)
        {
            UpdatePreviewControls();
            return;
        }

        _previewFrame = next;
        UpdatePreviewControls();
        if (notify) PreviewFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreviewTimerInterval()
    {
        var fps = Math.Max(1m, _previewFps.Value);
        _previewTimer.Interval = Math.Clamp((int)Math.Round(1000m / fps), 1, 1000);
    }

    private void UpdatePreviewControls()
    {
        var maxFrame = Math.Max(0, PreviewFrameCount - 1);
        _previewFrame = Math.Clamp(_previewFrame, 0, maxFrame);
        _previewFrameLabel.Text = $"{UiLocalization.T("Frame")} {_previewFrame} / {maxFrame}";
        _previewFrameLabel.AccessibleName = _previewFrameLabel.Text;
        var canPlay = _hasValidPreview && maxFrame > 0;
        if (_playButton is not null)
        {
            _playButton.Enabled = canPlay;
            _playButton.Text = _previewTimer.Enabled ? "Pause" : "Play";
            _playButton.AccessibleName = _previewTimer.Enabled ? "Pause preview" : "Play preview";
        }

        if (_resetButton is not null) _resetButton.Enabled = _hasValidPreview;
    }

    private void AddPreviewControls(TableLayoutPanel layout)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            BackColor = Theme.Panel,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            FlowDirection = FlowDirection.LeftToRight
        };
        _playButton = new Button
        {
            Text = "Play",
            Width = 88,
            Height = 32,
            Margin = new Padding(0, 3, 8, 3),
            AccessibleName = "Play preview",
            AccessibleDescription = "Play or pause the fracture preview"
        };
        _resetButton = new Button
        {
            Text = "Reset",
            Width = 88,
            Height = 32,
            Margin = new Padding(0, 3, 14, 3),
            AccessibleName = "Reset preview",
            AccessibleDescription = "Stop the preview and return to frame zero"
        };
        Theme.StyleStandardButton(_playButton);
        Theme.StyleStandardButton(_resetButton);
        _previewFrameLabel.Dock = DockStyle.None;
        _previewFrameLabel.AutoSize = false;
        _previewFrameLabel.Width = 130;
        _previewFrameLabel.Height = 32;
        _previewFrameLabel.Margin = new Padding(0, 3, 0, 3);
        _previewFrameLabel.ForeColor = Theme.Muted;
        _previewFrameLabel.BackColor = Theme.Panel;
        _previewFrameLabel.Font = Theme.UiFont(8.8f);
        _previewFrameLabel.TextAlign = ContentAlignment.MiddleLeft;
        controls.Controls.Add(_playButton);
        controls.Controls.Add(_resetButton);
        controls.Controls.Add(_previewFrameLabel);
        layout.Controls.Add(controls, 0, row);
        layout.SetColumnSpan(controls, 2);
    }

    private IEnumerable<ModernNumericUpDown> NumericInputs()
    {
        yield return _fragmentCount;
        yield return _edgeCount;
        yield return _randomness;
        yield return _randomSeed;
        yield return _fractureStrength;
        yield return _directionAngle;
        yield return _rotationStrength;
        yield return _animationFrames;
        yield return _groundPosition;
        yield return _gravity;
        yield return _airResistance;
        yield return _bounce;
    }

    private static void AddSection(TableLayoutPanel layout, string text)
    {
        var heading = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 10, 0, 0),
            Padding = new Padding(0, 0, 0, 4)
        };
        layout.Controls.Add(heading, 0, layout.RowCount++);
        layout.SetColumnSpan(heading, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 10, 3)
        }, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(input, 1, row);
    }

    private static void AddCheckField(TableLayoutPanel layout, CheckBox checkBox)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(checkBox, 0, row);
        layout.SetColumnSpan(checkBox, 2);
    }

    private static ModernNumericUpDown Numeric(
        string accessibleName,
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimalPlaces,
        decimal increment)
    {
        var input = new ModernNumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            DecimalPlaces = decimalPlaces,
            Increment = increment,
            Value = value,
            AccessibleName = accessibleName,
            AccessibleDescription = accessibleName,
            Suffix = accessibleName
        };
        return input;
    }

    private static CheckBox Check(string text, string description, bool value)
    {
        return new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            Height = 28,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont(),
            AccessibleName = text,
            AccessibleDescription = description,
            Margin = new Padding(0, 3, 0, 3)
        };
    }
}
