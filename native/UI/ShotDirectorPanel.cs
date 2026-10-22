using System.Globalization;

namespace VectorAnimationEngine;

/// <summary>Compatibility data used by older director regression fixtures.</summary>
internal readonly record struct ShotDirectorExposure(int StartFrame, int EndFrame, bool HasContent)
{
    public int FrameCount => EndFrame >= StartFrame ? EndFrame - StartFrame + 1 : 0;
    public bool Contains(int frame) => frame >= StartFrame && frame <= EndFrame;
}

/// <summary>Compatibility data for pre-camera director documents. Cameras do not own layers.</summary>
internal readonly record struct ShotDirectorLayer(
    string LayerId,
    string Name,
    int ColorArgb,
    bool IsMask,
    ShotDirectorExposure[] Exposures);

/// <summary>One independent 3D camera presented by the director workspace.</summary>
internal sealed record ShotDirectorItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public int DurationFrames { get; init; }
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public int ColorArgb { get; init; } = SceneShotDefinition.DefaultColorArgb;
    public bool IsActive { get; init; }
    public SceneShotSettings Framing { get; init; } = SceneShotSettings.Default;
    public bool FramingEditable { get; init; }
    public int KeyframeCount { get; init; }
    public int TweenCount { get; init; }

    // Kept for source compatibility with old benchmark fixtures. This is always empty for new state.
    public ShotDirectorLayer[] Layers { get; init; } = [];
    public int LayerCount => 0;
}

/// <summary>Everything the camera director workspace needs to present one scene.</summary>
internal sealed record ShotDirectorState
{
    public bool IsAvailable { get; init; }
    public string ActiveShotId { get; init; } = "";
    public int CurrentFrame { get; init; }
    public int FrameCount { get; init; } = AnimationTimeline.DefaultDuration;
    public decimal PlaybackFps { get; init; } = 30m;
    public int ShotSequenceLength { get; init; }
    public ShotDirectorItem[] Shots { get; init; } = [];
}

internal sealed class ShotDirectorEventArgs(string shotId) : EventArgs
{
    public string ShotId { get; } = shotId;
}

internal sealed class ShotDirectorMoveEventArgs(string shotId, int targetIndex) : EventArgs
{
    public string ShotId { get; } = shotId;
    public int TargetIndex { get; } = targetIndex;
}

internal sealed class ShotDirectorPropertiesEventArgs(
    string shotId,
    string name,
    string detail,
    int durationFrames) : EventArgs
{
    public string ShotId { get; } = shotId;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public int DurationFrames { get; } = durationFrames;
}

internal sealed class ShotDirectorLayerEventArgs(string shotId, string layerId) : EventArgs
{
    public string ShotId { get; } = shotId;
    public string LayerId { get; } = layerId;
}

internal sealed class ShotDirectorFramingEventArgs(string shotId, SceneShotSettings settings) : EventArgs
{
    public string ShotId { get; } = shotId;
    public SceneShotSettings Settings { get; } = settings;
}

/// <summary>
/// Camera list and inspector for the director workspace. Selecting a camera only selects its own
/// timeline track; the director timeline filters presentation to camera tracks without reassigning
/// the scene's animation layers.
/// </summary>
internal sealed partial class ShotDirectorPanel : UserControl
{
    internal const int TitleHeight = Theme.InspectorTitleHeight;
    internal const int ToolbarHeight = 34;
    internal const int DetailHeight = 250;
    internal const int CardWidth = 208;
    internal const int CardHeight = 112;
    internal const int CardGap = 8;
    internal const int GridPadding = 10;
    internal const int ScrollBarWidth = 9;

    private const int InspectorPadding = 8;
    private const int RowHeight = Theme.ControlHeight;
    private const int FieldGap = 8;
    private const int RowStep = RowHeight + FieldGap;
    private const int AxisRowGap = FieldGap;
    private const int AxisGroupPadding = 4;
    private const int ListWidth = 214;
    private const int NarrowLayoutThreshold = 520;
    private const int NarrowListHeight = 176;

    private readonly ToolTip _help = new();
    private readonly Label _title = InspectorModuleStyle.CreateTitle("Cameras");
    private readonly Label _summary = new()
    {
        ForeColor = Theme.Muted,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly FlowLayoutPanel _toolbar = new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        BackColor = Theme.Panel,
        Margin = Padding.Empty,
        Padding = new Padding(8, 3, 8, 0)
    };
    private readonly Button _addShotButton = new() { Text = "New camera", AutoSize = false, Width = 88, Height = 26 };
    private readonly Button _locateButton = new() { Text = "Locate", AutoSize = false, Width = 56, Height = 26 };
    private readonly SvgIconButton _moveUpButton = new(SvgIconKind.ChevronUp) { Width = 26, Height = 26 };
    private readonly SvgIconButton _moveDownButton = new(SvgIconKind.ChevronDown) { Width = 26, Height = 26 };
    private readonly SvgIconButton _removeButton = new(SvgIconKind.Remove) { Width = 26, Height = 26 };
    private readonly SvgIconButton _previewButton = new(SvgIconKind.Camera) { Width = 26, Height = 26, AccessibleName = "Enlarge camera preview" };

    private readonly ListBox _cameraList = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed,
        IntegralHeight = false,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.PanelStrong,
        ForeColor = Theme.Text,
        ItemHeight = 48,
        HorizontalScrollbar = false,
        SelectionMode = SelectionMode.One
    };
    private readonly ThemedScrollPanel _inspector = new() { BackColor = Theme.Panel, ShowScrollBar = false };
    private readonly ModernToggleSwitch _simpleCameraStyle = new()
    {
        Text = "Simple camera style",
        AccessibleName = "Simple camera style",
        AccessibleDescription = "Show a clean camera frame. Rotation handles appear only with Free Transform.",
        Checked = true,
        AutoSize = false
    };
    private bool _updatingCameraStyle;

    internal void SetSimpleCameraStyle(bool enabled)
    {
        _updatingCameraStyle = true;
        try { _simpleCameraStyle.Checked = enabled; }
        finally { _updatingCameraStyle = false; }
    }

    internal bool SimpleCameraStyleEnabled => _simpleCameraStyle.Checked;
    private readonly Label _inspectorTitle = InspectorModuleStyle.CreateTitle("Camera");
    private readonly Label _frameInfo = new() { ForeColor = Theme.Muted, BackColor = Theme.Panel, AutoEllipsis = true };
    private readonly ModernTextInput _nameEditor = new();
    private readonly ModernTextInput _detailEditor = new();
    private readonly ComboBox _kindEditor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _aspectRatioEditor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _projectionEditor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _previewModeEditor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private bool _updatingPreviewMode;
    private readonly ModernNumericUpDown _positionX = SpatialInput("Camera position X");
    private readonly ModernNumericUpDown _positionY = SpatialInput("Camera position Y");
    private readonly ModernNumericUpDown _positionZ = SpatialInput("Camera position Z");
    private readonly ModernNumericUpDown _rotationX = RotationInput("Camera rotation X");
    private readonly ModernNumericUpDown _rotationY = RotationInput("Camera rotation Y");
    private readonly ModernNumericUpDown _rotationZ = RotationInput("Camera rotation Z");
    private readonly ModernNumericUpDown _focalLength = new()
    {
        Minimum = (decimal)SceneShotSettings.MinimumFocalLength,
        Maximum = (decimal)SceneShotSettings.MaximumFocalLength,
        Increment = 1,
        DecimalPlaces = 1,
        Suffix = "mm"
    };
    private readonly ModernNumericUpDown _orthographicSize = new()
    {
        Minimum = (decimal)SceneShotSettings.MinimumOrthographicSize,
        Maximum = (decimal)SceneShotSettings.MaximumOrthographicSize,
        Increment = 10,
        DecimalPlaces = 1,
        Suffix = "vu",
        AccessibleDescription = "Minimum camera frame: 1920 x 1080 vu; aspect ratio is preserved."
    };
    private readonly Button _resetCameraButton = new() { Text = "Reset camera", AutoSize = false, Height = 26 };
    private readonly ModernNumericUpDown _zoomEditor = new()
    {
        Minimum = 0.01m,
        Maximum = 10000m,
        Increment = 1m,
        DecimalPlaces = 2,
        Suffix = "%",
        ShowSuffix = true,
        AccessibleName = "Camera zoom"
    };

    private ShotDirectorState _state = new();
    private readonly ModernToggleSwitch _advancedCameraSettings = new()
    {
        Text = "Advanced camera settings",
        AccessibleName = "Advanced camera settings",
        AutoSize = false
    };
    private bool _experimental3DEnabled = true;

    internal void SetExperimental3DEnabled(bool enabled)
    {
        if (_experimental3DEnabled == enabled) return;
        _experimental3DEnabled = enabled;
        UpdateEditors();
        UpdateCommandStates();
        PerformLayout();
    }

    private bool ActiveCameraAllowed => _experimental3DEnabled || ActiveShot()?.Framing.PinsPlanarRotation == true;
    private bool _updatingEditors;
    private bool _framingEditable;
    private Control? _curveEditor;

    public ShotDirectorPanel()
    {
        var cameraEditors = new[] { _positionX, _positionY, _positionZ, _rotationX, _rotationY, _rotationZ, _focalLength, _orthographicSize, _zoomEditor };
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        // The director is hosted by the narrow right inspector. Its content switches to the
        // stacked list/inspector layout below the wide-workbench threshold.
        MinimumSize = new Size(240, 320);
        TabStop = true;

        Theme.StyleToolTip(_help);
        _summary.Visible = false;
        _frameInfo.Visible = false;
        _title.Dock = DockStyle.None;
        _inspectorTitle.Dock = DockStyle.None;
        _inspectorTitle.AutoEllipsis = true;
        BuildToolbar();
        BuildInspector(cameraEditors);
        Controls.Add(_cameraList);
        Controls.Add(_inspector);
        Controls.Add(_toolbar);
        Controls.Add(_title);
        Controls.Add(_summary);
        Controls.Add(_previewButton);
        Controls.Add(_simpleCameraStyle);
        _inspector.Content.Controls.Add(_advancedCameraSettings);
        _advancedCameraSettings.CheckedChanged += (_, _) => PerformLayout();
        _simpleCameraStyle.CheckedChanged += (_, _) =>
        {
            if (!_updatingCameraStyle) CameraStyleChanged?.Invoke(this, EventArgs.Empty);
        };
        _cameraList.DrawItem += DrawCameraItem;
        _cameraList.SelectedIndexChanged += (_, _) => SelectCameraFromList();
        UiLocalization.Watch(this);
        UpdateCommandStates();
        UpdateEditors();
    }

    public event EventHandler? NewShotRequested;
    public event EventHandler? CameraPreviewRequested;
    public event EventHandler? PreviewModeChanged;
    public event EventHandler? CameraStyleChanged;
    public event EventHandler<ShotDirectorEventArgs>? ShotActivated;
    public event EventHandler<ShotDirectorEventArgs>? ShotLocateRequested;
    public event EventHandler<ShotDirectorEventArgs>? ShotRemoveRequested;
    public event EventHandler<ShotDirectorMoveEventArgs>? ShotMoveRequested;
    public event EventHandler<ShotDirectorPropertiesEventArgs>? ShotPropertiesCommitted;
    public event EventHandler? ShotEditStarted;
    public event EventHandler? ShotEditCompleted;
    public event EventHandler<ShotDirectorFramingEventArgs>? ShotFramingCommitted;
    public event EventHandler<ShotDirectorEventArgs>? ShotFramingResetRequested;

    internal ShotDirectorState State => _state;
    internal bool CameraListContainsFocus => _cameraList.ContainsFocus;
    internal ShotWorkspacePreviewMode PreviewMode => (_previewModeEditor.SelectedItem as PreviewModeItem)?.Mode
        ?? ShotWorkspacePreviewMode.ObserveCamera;

    internal void SetPreviewMode(ShotWorkspacePreviewMode mode, bool enabled)
    {
        _updatingPreviewMode = true;
        try
        {
            _previewModeEditor.SelectedIndex = (int)mode;
            _previewModeEditor.Enabled = enabled;
            _previewModeEditor.TabStop = enabled;
        }
        finally
        {
            _updatingPreviewMode = false;
        }
    }

    internal string SelectedShotId => _state.ActiveShotId;
    internal ShotDirectorItem? SelectedShot => FindShot(_state.ActiveShotId);

    internal ShotDirectorItem? FindShot(string? shotId)
    {
        if (string.IsNullOrWhiteSpace(shotId)) return null;
        return _state.Shots.FirstOrDefault(shot => string.Equals(shot.Id, shotId, StringComparison.Ordinal));
    }

    /// <summary>Hosts the shared timeline curve editor below the selected camera properties.</summary>
    internal void AttachTweenCurveEditor(Control editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!ReferenceEquals(_curveEditor, editor))
        {
            DetachTweenCurveEditor();
            _curveEditor = editor;
            _inspector.Content.Controls.Add(editor);
        }

        editor.Dock = DockStyle.None;
        editor.Visible = true;
        if (editor is TweenCurveEditorPanel curvePanel)
        {
            curvePanel.Height = ScaleMetric(curvePanel.PreferredPanelHeight);
        }
        PerformLayout();
    }

    internal void DetachTweenCurveEditor(Control? editor = null)
    {
        if (_curveEditor is null || editor is not null && !ReferenceEquals(_curveEditor, editor)) return;
        var detached = _curveEditor;
        _curveEditor = null;
        _inspector.Content.Controls.Remove(detached);
        detached.Visible = false;
        detached.Parent = null;
        PerformLayout();
    }

    public void Bind(ShotDirectorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _updatingEditors = true;
        try
        {
            _cameraList.BeginUpdate();
            _cameraList.Items.Clear();
            foreach (var shot in state.Shots) _cameraList.Items.Add(shot);
            var selectedIndex = Array.FindIndex(state.Shots, shot => string.Equals(shot.Id, state.ActiveShotId, StringComparison.Ordinal));
            _cameraList.SelectedIndex = selectedIndex;
        }
        finally
        {
            _cameraList.EndUpdate();
            _updatingEditors = false;
        }

        UpdateCommandStates();
        UpdateEditors();
        RefreshSummary();
        Invalidate();
    }

    public void SetPlayhead(int frame)
    {
        if (_state.CurrentFrame == frame) return;
        _state = _state with { CurrentFrame = frame };
        UpdateFrameInfo();
        _cameraList.Invalidate();
    }

    public void RefreshSummary() => _help.SetToolTip(_title, BuildSummaryText());

    public void SetSelectedShotFraming(SceneShotSettings settings, bool editable)
    {
        var selectedIndex = SelectedShotIndex();
        if (selectedIndex >= 0)
        {
            var updatedShots = (ShotDirectorItem[])_state.Shots.Clone();
            updatedShots[selectedIndex] = updatedShots[selectedIndex] with
            {
                Framing = settings,
                FramingEditable = editable
            };
            _state = _state with { Shots = updatedShots };
        }

        _updatingEditors = true;
        try
        {
            SetCameraEditors(settings);
        }
        finally
        {
            _updatingEditors = false;
        }

        _framingEditable = editable;
        ApplyEditorState();
    }

    private void BuildToolbar()
    {
        Theme.StyleButton(_addShotButton);
        Theme.StyleButton(_locateButton);
        Theme.StyleToolbarButton(_moveUpButton);
        Theme.StyleToolbarButton(_moveDownButton);
        Theme.StyleToolbarButton(_removeButton);
        Theme.StyleToolbarButton(_previewButton);
        _addShotButton.Margin = new Padding(0, 0, 0, 0);
        _locateButton.Margin = new Padding(6, 0, 0, 0);
        _moveUpButton.Margin = new Padding(6, 0, 0, 0);
        _moveDownButton.Margin = new Padding(2, 0, 0, 0);
        _removeButton.Margin = new Padding(2, 0, 0, 0);
        _toolbar.Controls.Add(_addShotButton);
        _toolbar.Controls.Add(_locateButton);
        _toolbar.Controls.Add(_moveUpButton);
        _toolbar.Controls.Add(_moveDownButton);
        _toolbar.Controls.Add(_removeButton);
        _previewButton.Click += (_, _) => CameraPreviewRequested?.Invoke(this, EventArgs.Empty);
        _help.SetToolTip(_previewButton, UiLocalization.T("Enlarge camera preview"));
        _addShotButton.Click += (_, _) => NewShotRequested?.Invoke(this, EventArgs.Empty);
        _locateButton.Click += (_, _) => LocateShot(_state.ActiveShotId);
        _moveUpButton.Click += (_, _) => MoveSelectedShot(-1);
        _moveDownButton.Click += (_, _) => MoveSelectedShot(1);
        _removeButton.Click += (_, _) => RemoveShot(_state.ActiveShotId);
        _moveUpButton.AccessibleName = "Move camera earlier";
        _moveDownButton.AccessibleName = "Move camera later";
        _removeButton.AccessibleName = "Delete camera";
        _locateButton.AccessibleName = "Locate camera";
    }

    private void BuildInspector(IReadOnlyList<ModernNumericUpDown> cameraEditors)
    {
        Theme.StyleTextBox(_nameEditor);
        Theme.StyleTextBox(_detailEditor);
        Theme.StyleComboBox(_kindEditor);
        Theme.StyleComboBox(_projectionEditor);
        Theme.StyleComboBox(_aspectRatioEditor);
        Theme.StyleComboBox(_previewModeEditor);
        foreach (var editor in new[] { _kindEditor, _projectionEditor, _aspectRatioEditor, _previewModeEditor })
            Theme.StyleComboBoxSelection(editor, Font.Size, Theme.ControlHeight);
        _previewModeEditor.Items.AddRange(new object[]
        {
            new PreviewModeItem(ShotWorkspacePreviewMode.ObserveCamera, "1 · Observe"),
            new PreviewModeItem(ShotWorkspacePreviewMode.LockCamera, "2 · Locked"),
            new PreviewModeItem(ShotWorkspacePreviewMode.CameraView, "3 · Camera")
        });
        _previewModeEditor.SelectedIndex = 0;
        _previewModeEditor.Enabled = false;
        _previewModeEditor.AccessibleName = "Camera preview mode";
        _previewModeEditor.AccessibleDescription = "Observe the moving camera; lock the camera in the current view; or fit the camera frame to the viewport. Locked and camera views are read-only 2D previews.";
        _help.SetToolTip(_previewModeEditor, UiLocalization.T(_previewModeEditor.AccessibleDescription));
        _previewModeEditor.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingPreviewMode) PreviewModeChanged?.Invoke(this, EventArgs.Empty);
        };
        _aspectRatioEditor.Items.AddRange(new object[]
        {
            new AspectRatioItem(SceneShotAspectRatio.FollowScene, "Follow scene"),
            new AspectRatioItem(SceneShotAspectRatio.Landscape16To9, "16:9"),
            new AspectRatioItem(SceneShotAspectRatio.Portrait9To16, "9:16"),
            new AspectRatioItem(SceneShotAspectRatio.Standard4To3, "4:3"),
            new AspectRatioItem(SceneShotAspectRatio.Photo3To2, "3:2"),
            new AspectRatioItem(SceneShotAspectRatio.Square1To1, "1:1"),
            new AspectRatioItem(SceneShotAspectRatio.Portrait4To5, "4:5"),
            new AspectRatioItem(SceneShotAspectRatio.Ultrawide21To9, "21:9")
        });
        _aspectRatioEditor.AccessibleName = "Camera aspect ratio";
        _kindEditor.Items.Add(new CameraKindItem(SceneShotCameraKind.ThreeD, "3D Camera"));
        _kindEditor.Items.Add(new CameraKindItem(SceneShotCameraKind.TwoD, "2D Camera"));
        _kindEditor.AccessibleName = "Camera type";
        _projectionEditor.Items.Add(new ProjectionItem(CameraProjection.Perspective, "Perspective"));
        _projectionEditor.Items.Add(new ProjectionItem(CameraProjection.Orthographic, "Orthographic"));
        _projectionEditor.AccessibleName = "Camera projection mode";
        foreach (var editor in cameraEditors)
        {
            Theme.StyleNumeric(editor);
            editor.Font = Theme.UiFont(11f);
        }
        Theme.StyleButton(_resetCameraButton);
        _nameEditor.AccessibleName = "Camera name";
        _detailEditor.AccessibleName = "Camera description";

        _inspector.Content.Controls.Add(_inspectorTitle);
        _inspector.Content.Controls.Add(_frameInfo);
        AddEditor(_previewModeEditor, "Preview mode");
        AddEditor(_nameEditor, "Name");
        AddEditor(_detailEditor, "Description");
        AddEditor(_kindEditor, "Type");
        AddEditor(_projectionEditor, "Mode");
        AddEditor(_aspectRatioEditor, "Aspect ratio");
        AddAxisEditors("Position", [_positionX, _positionY, _positionZ], ["X", "Y", "Z"]);
        AddAxisEditors("Rotation", [_rotationX, _rotationY, _rotationZ], ["X", "Y", "Z"]);
        AddEditor(_focalLength, "Focal length");
        AddEditor(_orthographicSize, "Ortho size");
        AddEditor(_zoomEditor, "Zoom");
        _inspector.Content.Controls.Add(_resetCameraButton);

        _nameEditor.Enter += (_, _) => ShotEditStarted?.Invoke(this, EventArgs.Empty);
        _nameEditor.Leave += (_, _) => CompleteTextEdit();
        _detailEditor.Enter += (_, _) => ShotEditStarted?.Invoke(this, EventArgs.Empty);
        _detailEditor.Leave += (_, _) => CompleteTextEdit();
        _nameEditor.KeyDown += HandleEditorKeyDown;
        _detailEditor.KeyDown += HandleEditorKeyDown;
        _projectionEditor.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingEditors) CommitCameraSettings();
        };
        _aspectRatioEditor.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingEditors) CommitCameraSettings();
        };
        _kindEditor.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingEditors) CommitCameraSettings();
        };
        _resetCameraButton.Click += (_, _) =>
        {
            if (_state.ActiveShotId.Length > 0) ShotFramingResetRequested?.Invoke(this, new ShotDirectorEventArgs(_state.ActiveShotId));
        };

        foreach (var editor in cameraEditors)
        {
            editor.Enter += (_, _) => ShotEditStarted?.Invoke(this, EventArgs.Empty);
            editor.InteractionStarted += (_, _) => ShotEditStarted?.Invoke(this, EventArgs.Empty);
            editor.InteractionCompleted += (_, _) => ShotEditCompleted?.Invoke(this, EventArgs.Empty);
            editor.InteractionCanceled += (_, _) => ShotEditCompleted?.Invoke(this, EventArgs.Empty);
            editor.ValueChanged += (_, _) =>
            {
                if (_updatingEditors) return;
                if (editor == _zoomEditor) CommitCameraZoom();
                else CommitCameraSettings();
            };
        }
    }

    private void AddEditor(Control editor, string label)
    {
        var fieldLabel = CreateInspectorLabel(label);
        fieldLabel.Tag = editor;
        editor.Tag = fieldLabel;
        _inspector.Content.Controls.Add(fieldLabel);
        _inspector.Content.Controls.Add(editor);
    }

    private void AddAxisEditors(string label, IReadOnlyList<ModernNumericUpDown> editors, IReadOnlyList<string> axisLabels)
    {
        var rowLabel = CreateInspectorLabel(label);
        rowLabel.Tag = editors.ToArray();
        _inspector.Content.Controls.Add(rowLabel);
        var group = new Panel { BackColor = Theme.Field };
        group.Paint += (_, e) =>
        {
            group.BackColor = Theme.Field;
            using var fill = new SolidBrush(Theme.Field);
            using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Theme.ControlOutline);
            Theme.PaintUiSurface(e.Graphics, fill, border, group.ClientRectangle);
            using var separator = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Mix(Theme.Field, Theme.ControlOutline, 0.45f));
            var visibleRowCount = editors.Count(editor => editor.Visible);
            for (var rowIndex = 1; rowIndex < visibleRowCount; rowIndex++)
            {
                var separatorY = ScaleMetric(AxisGroupPadding + RowHeight * rowIndex);
                e.Graphics.DrawLine(separator, ScaleMetric(8), separatorY, group.Width - ScaleMetric(8), separatorY);
            }
        };
        _inspector.Content.Controls.Add(group);
        foreach (var editor in editors)
        {
            editor.Tag = rowLabel;
            editor.GroupedAppearance = true;
            group.Controls.Add(editor);
        }
        for (var index = 0; index < editors.Count; index++)
        {
            var axisLabel = CreateInspectorLabel(axisLabels[index]);
            axisLabel.Tag = editors[index];
            var axisName = axisLabels[index];
            var axisEditor = editors[index];
            var axisColor = index switch
            {
                0 => Color.FromArgb(190, 110, 120),
                1 => Color.FromArgb(148, 173, 94),
                _ => Color.FromArgb(112, 160, 200)
            };
            axisLabel.Text = "";
            axisLabel.AccessibleName = axisEditor.AccessibleName;
            axisLabel.TextAlign = ContentAlignment.MiddleCenter;
            axisLabel.BackColor = Theme.Field;
            axisLabel.Cursor = Cursors.IBeam;
            axisLabel.Click += (_, _) =>
            {
                if (axisEditor.Enabled) axisEditor.SelectNextControl(null, true, true, true, false);
            };
            axisLabel.Paint += (_, e) =>
            {
                var highContrast = SystemInformation.HighContrast;
                var background = highContrast ? SystemColors.Window : Theme.Mix(Theme.Field, Theme.AccentSurface, 0.65f);
                var bounds = new Rectangle(ScaleMetric(2), ScaleMetric(6), Math.Max(1, axisLabel.Width - ScaleMetric(4)), ScaleMetric(20));
                using var fill = new SolidBrush(background);
                using var border = new Pen(highContrast ? SystemColors.WindowText : Theme.Mix(Theme.ControlOutline, Theme.AccentSurface, 0.5f));
                Theme.PaintUiSurface(e.Graphics, fill, border, bounds);
                using var marker = new SolidBrush(highContrast ? SystemColors.WindowText : Theme.ReadableUiColor(background, axisColor));
                e.Graphics.FillRectangle(marker, bounds.Left, bounds.Top + ScaleMetric(4), ScaleMetric(2), Math.Max(1, bounds.Height - ScaleMetric(8)));
                var foreground = highContrast ? SystemColors.WindowText : Theme.ReadableText(background, axisEditor.Enabled ? Theme.Text : Theme.DisabledText);
                TextRenderer.DrawText(e.Graphics, axisName, axisLabel.Font, bounds, foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            axisEditor.EnabledChanged += (_, _) => axisLabel.Invalidate();
            group.Controls.Add(axisLabel);
        }
    }

    private Label CreateInspectorLabel(string text) => new()
    {
        Text = UiLocalization.T(text),
        ForeColor = Theme.Muted,
        BackColor = Theme.Panel,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private void SelectCameraFromList()
    {
        if (_updatingEditors || _cameraList.SelectedItem is not ShotDirectorItem shot) return;
        if (string.Equals(_state.ActiveShotId, shot.Id, StringComparison.Ordinal))
        {
            UpdateEditors();
            return;
        }

        ShotActivated?.Invoke(this, new ShotDirectorEventArgs(shot.Id));
    }

    private void CommitCameraZoom()
    {
        if (_experimental3DEnabled || ActiveShot() is not { } shot || !ActiveCameraAllowed || !_framingEditable) return;
        var zoom = _zoomEditor.Value / 100m;
        if (shot.Framing.Projection == CameraProjection.Orthographic)
            _orthographicSize.Value = Math.Clamp(28000m / zoom, _orthographicSize.Minimum, _orthographicSize.Maximum);
        else
            _focalLength.Value = Math.Clamp(50m * zoom, _focalLength.Minimum, _focalLength.Maximum);
    }

    private void CommitCameraSettings()
    {
        if (_updatingEditors || ActiveShot() is not { } shot) return;
        if (!ActiveCameraAllowed) return;
        var kind = (_kindEditor.SelectedItem as CameraKindItem)?.Kind ?? SceneShotCameraKind.ThreeD;
        var projection = (_projectionEditor.SelectedItem as ProjectionItem)?.Projection ?? CameraProjection.Perspective;
        var settings = new SceneShotSettings(
            projection,
            new System.Numerics.Vector3((float)_positionX.Value, (float)_positionY.Value, (float)_positionZ.Value),
            new System.Numerics.Vector3((float)_rotationX.Value, (float)_rotationY.Value, (float)_rotationZ.Value),
            (float)_focalLength.Value,
            (float)_orthographicSize.Value) with
        {
            Kind = kind,
            AspectRatio = (_aspectRatioEditor.SelectedItem as AspectRatioItem)?.AspectRatio
                ?? SceneShotAspectRatio.FollowScene
        };
        // Switching to a 2D camera pins X/Y rotation. Applying the invariant before the equality check
        // makes the mode switch itself the committed change even when the numeric fields match.
        settings = settings.EnforceKind();
        if (settings == shot.Framing) return;
        ShotFramingCommitted?.Invoke(this, new ShotDirectorFramingEventArgs(shot.Id, settings));
    }

    private void CompleteTextEdit()
    {
        CommitProperties();
        ShotEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CommitProperties()
    {
        if (_updatingEditors || ActiveShot() is not { } shot) return;
        if (!ActiveCameraAllowed) return;
        var name = _nameEditor.Text.Trim();
        if (name.Length == 0) name = shot.Name;
        var detail = _detailEditor.Text.Trim();
        if (string.Equals(name, shot.Name, StringComparison.Ordinal)
            && string.Equals(detail, shot.Detail, StringComparison.Ordinal)) return;
        ShotPropertiesCommitted?.Invoke(this, new ShotDirectorPropertiesEventArgs(shot.Id, name, detail, shot.DurationFrames));
    }

    private void HandleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitProperties();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            UpdateEditors();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateEditors()
    {
        var shot = ActiveShot();
        var enabled = _state.IsAvailable && shot is not null && ActiveCameraAllowed;
        _updatingEditors = true;
        try
        {
            _nameEditor.Enabled = enabled;
            _detailEditor.Enabled = enabled;
            _kindEditor.Enabled = enabled;
            _projectionEditor.Enabled = enabled;
            _nameEditor.Text = shot?.Name ?? "";
            _detailEditor.Text = shot?.Detail ?? "";
            SetCameraEditors(shot?.Framing ?? SceneShotSettings.Default);
            _framingEditable = shot?.FramingEditable == true;
            var kind = shot?.Framing.Kind ?? SceneShotCameraKind.ThreeD;
            _kindEditor.SelectedIndex = kind == SceneShotCameraKind.TwoD ? 1 : 0;
            var projection = shot?.Framing.Projection ?? CameraProjection.Perspective;
            _projectionEditor.SelectedIndex = projection == CameraProjection.Orthographic ? 1 : 0;
            _inspectorTitle.Text = shot?.Name ?? UiLocalization.T("Camera");
        }
        finally
        {
            _updatingEditors = false;
        }

        UpdateFrameInfo();
        ApplyEditorState();
    }

    private void SetCameraEditors(SceneShotSettings settings)
    {
        _aspectRatioEditor.SelectedIndex = Array.FindIndex(
            _aspectRatioEditor.Items.Cast<AspectRatioItem>().ToArray(), item => item.AspectRatio == settings.AspectRatio);
        _positionX.Value = (decimal)settings.X;
        _positionY.Value = (decimal)settings.Y;
        _positionZ.Value = (decimal)settings.Z;
        _rotationX.Value = (decimal)settings.RotationX;
        _rotationY.Value = (decimal)settings.RotationY;
        _rotationZ.Value = (decimal)settings.RotationDegrees;
        _focalLength.Value = (decimal)settings.FocalLength;
        _orthographicSize.Value = (decimal)settings.OrthographicSize;
        var orthographic = settings.Projection == CameraProjection.Orthographic;
        var projectionIndex = orthographic ? 1 : 0;
        var projectionChanged = _projectionEditor.SelectedIndex != projectionIndex;
        _projectionEditor.SelectedIndex = projectionIndex;
        _zoomEditor.Minimum = orthographic ? 2800000m / _orthographicSize.Maximum : _focalLength.Minimum * 2m;
        _zoomEditor.Maximum = orthographic ? 2800000m / _orthographicSize.Minimum : _focalLength.Maximum * 2m;
        var zoomPercent = orthographic ? 2800000m / (decimal)settings.OrthographicSize : (decimal)settings.FocalLength * 2m;
        _zoomEditor.Value = Math.Clamp(zoomPercent, _zoomEditor.Minimum, _zoomEditor.Maximum);
        if (projectionChanged) PerformLayout();
    }

    private void ApplyEditorState()
    {
        var enabled = _state.IsAvailable && ActiveShot() is not null && _framingEditable && ActiveCameraAllowed;
        foreach (var editor in new[] { _positionX, _positionY, _positionZ, _rotationX, _rotationY, _rotationZ, _focalLength, _orthographicSize, _zoomEditor })
        {
            editor.Enabled = enabled;
            editor.TabStop = enabled;
        }

        // A 2D camera pins X/Y rotation, so those channels are not editable. Z rotation, Z position
        // and the lens fields stay live: the camera keeps real depth and perspective size change.
        var planarRotationEditable = enabled && (_kindEditor.SelectedItem as CameraKindItem)?.Kind != SceneShotCameraKind.TwoD;
        _rotationX.Enabled = planarRotationEditable;
        _rotationX.TabStop = planarRotationEditable;
        _rotationY.Enabled = planarRotationEditable;
        _rotationY.TabStop = planarRotationEditable;

        _aspectRatioEditor.Enabled = enabled;
        _aspectRatioEditor.TabStop = enabled;
        _resetCameraButton.Enabled = enabled;
        _projectionEditor.Enabled = enabled;
        _kindEditor.Enabled = enabled && _experimental3DEnabled;
    }

    private void UpdateCommandStates()
    {
        var available = _state.IsAvailable;
        var selectedIndex = SelectedShotIndex();
        var hasSelection = selectedIndex >= 0;
        _addShotButton.Enabled = available;
        _locateButton.Enabled = available && hasSelection;
        _removeButton.Enabled = available && hasSelection && ActiveCameraAllowed;
        _previewButton.Enabled = available && hasSelection && ActiveCameraAllowed;
        _moveUpButton.Enabled = available && selectedIndex > 0 && ActiveCameraAllowed;
        _moveDownButton.Enabled = available && selectedIndex >= 0 && selectedIndex < _state.Shots.Length - 1 && ActiveCameraAllowed;
    }

    private int SelectedShotIndex()
    {
        for (var index = 0; index < _state.Shots.Length; index++)
        {
            if (string.Equals(_state.Shots[index].Id, _state.ActiveShotId, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private ShotDirectorItem? ActiveShot() => FindShot(_state.ActiveShotId);

    private string BuildSummaryText()
    {
        if (!_state.IsAvailable) return UiLocalization.T("Cameras are available in the Scene & Animation workspace");
        if (_state.Shots.Length == 0) return UiLocalization.T("No cameras yet. Add one to animate a viewpoint.");
        var seconds = _state.FrameCount / (double)Math.Max(1m, _state.PlaybackFps);
        return string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("{0} cameras · {1} frames · {2:0.00} s"),
            _state.Shots.Length,
            _state.FrameCount,
            seconds);
    }

    private void UpdateFrameInfo()
    {
        var shot = ActiveShot();
        if (shot is null)
        {
            _help.SetToolTip(_inspectorTitle, "");
            return;
        }

        var frameInfo = string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("Frame {0} / {1} · {2} keys · {3} tweens"),
            Math.Max(0, _state.CurrentFrame),
            Math.Max(0, _state.FrameCount - 1),
            shot.KeyframeCount,
            shot.TweenCount);
        _help.SetToolTip(_inspectorTitle, frameInfo);
    }

    private void LocateShot(string shotId)
    {
        if (!_state.IsAvailable || shotId.Length == 0) return;
        ShotLocateRequested?.Invoke(this, new ShotDirectorEventArgs(shotId));
    }

    private void RemoveShot(string shotId)
    {
        if (!_state.IsAvailable || shotId.Length == 0) return;
        ShotRemoveRequested?.Invoke(this, new ShotDirectorEventArgs(shotId));
    }

    private void MoveSelectedShot(int direction)
    {
        var index = SelectedShotIndex();
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _state.Shots.Length) return;
        ShotMoveRequested?.Invoke(this, new ShotDirectorMoveEventArgs(_state.Shots[index].Id, target));
    }

    private void DrawCameraItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _cameraList.Items.Count || _cameraList.Items[e.Index] is not ShotDirectorItem shot) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? Theme.Mix(Theme.PanelStrong, Theme.Accent, 0.16f) : Theme.PanelStrong;
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var shotColor = Color.FromArgb(shot.ColorArgb);
        using var bar = new SolidBrush(Color.FromArgb(255, shotColor.R, shotColor.G, shotColor.B));
        e.Graphics.FillRectangle(bar, e.Bounds.Left + ScaleMetric(2), e.Bounds.Top + ScaleMetric(7), ScaleMetric(2), Math.Max(1, e.Bounds.Height - ScaleMetric(14)));
        var projection = shot.Framing.Projection == CameraProjection.Perspective ? "Perspective" : "Orthographic";
        var title = string.Format(CultureInfo.CurrentCulture, "{0}.  {1}", e.Index + 1, shot.Name);
        var meta = !_experimental3DEnabled && !shot.Framing.PinsPlanarRotation
            ? UiLocalization.T("3D camera (disabled)")
            : UiLocalization.T(_experimental3DEnabled ? projection : "2D Camera");
        var textColor = Theme.ReadableText(background, Theme.Text);
        TextRenderer.DrawText(e.Graphics, title, Theme.UiFont(9f), new Rectangle(e.Bounds.Left + ScaleMetric(12), e.Bounds.Top + ScaleMetric(5), e.Bounds.Width - ScaleMetric(18), ScaleMetric(20)), textColor, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, meta, Font, new Rectangle(e.Bounds.Left + ScaleMetric(12), e.Bounds.Top + ScaleMetric(25), e.Bounds.Width - ScaleMetric(18), ScaleMetric(18)), Theme.ReadableText(background, Theme.SecondaryText), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (_state.CurrentFrame >= shot.StartFrame && _state.CurrentFrame <= shot.EndFrame)
        {
            using var marker = new Pen(Theme.Danger, 1.5f);
            e.Graphics.DrawLine(marker, e.Bounds.Left + 12, e.Bounds.Bottom - 5, e.Bounds.Right - 8, e.Bounds.Bottom - 5);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _cameraList.ItemHeight = ScaleMetric(48);
        _toolbar.Padding = new Padding(ScaleMetric(8), ScaleMetric(3), ScaleMetric(8), 0);
        _addShotButton.Size = new Size(ScaleMetric(88), ScaleMetric(26));
        _locateButton.Size = new Size(ScaleMetric(56), ScaleMetric(26));
        foreach (var button in new[] { _moveUpButton, _moveDownButton, _removeButton })
            button.Size = new Size(ScaleMetric(26), ScaleMetric(26));
        var titleHeight = ScaleMetric(TitleHeight);
        var toolbarHeight = ScaleMetric(ToolbarHeight);
        _title.SetBounds(ScaleMetric(4), 0, Math.Max(0, Width - ScaleMetric(42)), ScaleMetric(Theme.InspectorTitleHeight));
        _previewButton.SetBounds(Math.Max(0, Width - ScaleMetric(34)), ScaleMetric(3), ScaleMetric(26), ScaleMetric(26));
        _summary.SetBounds(ScaleMetric(108), ScaleMetric(8), Math.Max(0, Width - ScaleMetric(116)), ScaleMetric(24));
        _toolbar.SetBounds(0, titleHeight, Width, toolbarHeight);
        var compactToolbar = Width < ScaleMetric(254);
        _toolbar.Padding = new Padding(ScaleMetric(compactToolbar ? 4 : 8), ScaleMetric(3), ScaleMetric(compactToolbar ? 4 : 8), 0);
        _locateButton.Margin = new Padding(ScaleMetric(compactToolbar ? 4 : 6), 0, 0, 0);
        _moveUpButton.Margin = _locateButton.Margin;
        _moveDownButton.Margin = new Padding(ScaleMetric(compactToolbar ? 1 : 2), 0, 0, 0);
        _removeButton.Margin = _moveDownButton.Margin;
        _simpleCameraStyle.SetBounds(ScaleMetric(8), titleHeight + toolbarHeight,
            Math.Max(0, Width - ScaleMetric(16)), ScaleMetric(32));
        var contentTop = _simpleCameraStyle.Bottom;
        var narrowLayout = Width < ScaleMetric(NarrowLayoutThreshold);
        if (narrowLayout)
        {
            var listTop = contentTop + ScaleMetric(8);
            var availableHeight = Math.Max(0, Height - contentTop - ScaleMetric(16));
            var listHeight = Math.Min(
                ScaleMetric(NarrowListHeight),
                Math.Max(ScaleMetric(120), availableHeight / 3));
            _cameraList.SetBounds(
                ScaleMetric(8),
                listTop,
                Math.Max(0, Width - ScaleMetric(16)),
                listHeight);
            var inspectorTop = _cameraList.Bottom + ScaleMetric(12);
            _inspector.SetBounds(
                0,
                inspectorTop,
                Math.Max(0, Width),
                Math.Max(0, Height - inspectorTop));
        }
        else
        {
            _cameraList.SetBounds(
                ScaleMetric(8),
                contentTop + ScaleMetric(8),
                Math.Max(150, ScaleMetric(ListWidth)),
                Math.Max(0, Height - contentTop - ScaleMetric(16)));
            var inspectorLeft = _cameraList.Right + ScaleMetric(12);
            _inspector.SetBounds(
                inspectorLeft,
                contentTop,
                Math.Max(0, Width - inspectorLeft),
                Math.Max(0, Height - contentTop));
        }

        var left = ScaleMetric(InspectorPadding);
        var right = Math.Max(left + 80, _inspector.Content.ClientSize.Width - ScaleMetric(8));
        var y = 0;
        _inspectorTitle.SetBounds(ScaleMetric(4), y, Math.Max(0, right - ScaleMetric(4)), ScaleMetric(Theme.InspectorTitleHeight));
        y += ScaleMetric(32);

        LayoutSingleEditor(_previewModeEditor, y, right); y += ScaleMetric(RowStep);
        LayoutSingleEditor(_nameEditor, y, right); y += ScaleMetric(RowStep);
        y = LayoutOptionalEditor(_detailEditor, y, right, _experimental3DEnabled);
        y = LayoutOptionalEditor(_kindEditor, y, right, _experimental3DEnabled);
        var advanced = _experimental3DEnabled || _advancedCameraSettings.Checked;
        y = LayoutOptionalEditor(_projectionEditor, y, right, advanced);
        LayoutSingleEditor(_aspectRatioEditor, y, right); y += ScaleMetric(RowStep);
        _advancedCameraSettings.Visible = !_experimental3DEnabled;
        if (!_experimental3DEnabled)
        {
            _advancedCameraSettings.SetBounds(left, y, Math.Max(1, right - left), ScaleMetric(32));
            y += ScaleMetric(36);
        }
        LayoutAxisRow(y, right, _positionX, _positionY, _positionZ); y += ScaleMetric(RowHeight * (advanced ? 3 : 2) + AxisGroupPadding * 2 + AxisRowGap);
        LayoutAxisRow(y, right, _rotationX, _rotationY, _rotationZ); y += ScaleMetric(_experimental3DEnabled ? RowHeight * 3 + AxisGroupPadding * 2 + AxisRowGap : RowStep);
        var orthographic = (_projectionEditor.SelectedItem as ProjectionItem)?.Projection == CameraProjection.Orthographic;
        y = LayoutOptionalEditor(_focalLength, y, right, _experimental3DEnabled || advanced && !orthographic);
        y = LayoutOptionalEditor(_orthographicSize, y, right, _experimental3DEnabled || advanced && orthographic);
        y = LayoutOptionalEditor(_zoomEditor, y, right, !_experimental3DEnabled);
        _resetCameraButton.SetBounds(left, y, Math.Min(right - left, ScaleMetric(112)), ScaleMetric(26));
        y += ScaleMetric(34);
        if (_curveEditor is { Visible: true } curveEditor)
        {
            var curveHeight = Math.Max(1, curveEditor.Height);
            curveEditor.SetBounds(left, y, Math.Max(80, right - left), curveHeight);
            y += curveHeight + ScaleMetric(14);
        }

        _inspector.Content.PerformLayout();
    }

    private int LayoutOptionalEditor(Control editor, int y, int right, bool visible)
    {
        editor.Visible = visible;
        editor.TabStop = visible && editor.Enabled;
        if (editor.Tag is Label label) label.Visible = visible;
        if (!visible) return y;
        LayoutSingleEditor(editor, y, right);
        return y + ScaleMetric(RowStep);
    }

    private void LayoutSingleEditor(Control editor, int y, int right)
    {
        if (editor.Tag is not Label fieldLabel) return;
        var left = ScaleMetric(InspectorPadding);
        var start = left + (int)Math.Floor((right - left) * 0.48f);
        var fieldHeight = editor is ModernTextInput input ? Math.Max(ScaleMetric(RowHeight), input.PreferredFieldHeight) : ScaleMetric(RowHeight);
        fieldLabel.SetBounds(left, y, Math.Max(1, start - left - ScaleMetric(6)), fieldHeight);
        editor.SetBounds(start, y, Math.Max(1, right - start), fieldHeight);
    }

    private void LayoutAxisRow(int y, int right, params ModernNumericUpDown[] editors)
    {
        if (editors.Length == 0) return;
        var left = ScaleMetric(InspectorPadding);
        var start = left + (int)Math.Floor((right - left) * 0.48f);
        var group = editors[0].Parent;
        if (group is null) return;
        foreach (var editor in editors)
        {
            var visible = IsCameraAxisVisible(editor);
            editor.Visible = visible;
            editor.GroupedAppearance = _experimental3DEnabled || editor != _rotationZ;
            var axisLabel = group.Controls.OfType<Label>().FirstOrDefault(label => ReferenceEquals(label.Tag, editor));
            if (axisLabel is not null) axisLabel.Visible = visible && (_experimental3DEnabled || editor != _rotationZ);
        }
        editors = editors.Where(IsCameraAxisVisible).ToArray();
        var groupPadding = !_experimental3DEnabled && editors[0] == _rotationZ ? 0 : AxisGroupPadding;
        group.SetBounds(start, y, Math.Max(1, right - start), ScaleMetric(RowHeight * editors.Length + groupPadding * 2));
        if (editors[0].Tag is Label fieldLabel)
        {
            fieldLabel.SetBounds(left, y, Math.Max(1, start - left - ScaleMetric(6)), ScaleMetric(RowHeight + groupPadding * 2));
        }

        var inset = groupPadding == 0 ? 0 : ScaleMetric(groupPadding);
        var labelWidth = !_experimental3DEnabled && editors[0] == _rotationZ ? 0 : ScaleMetric(22);
        for (var index = 0; index < editors.Length; index++)
        {
            var editorY = inset + index * ScaleMetric(RowHeight);
            editors[index].SetBounds(inset + labelWidth, editorY, Math.Max(1, group.Width - labelWidth - inset * 2), ScaleMetric(RowHeight));
            var axisLabel = group.Controls.Cast<Control>()
                .OfType<Label>()
                .FirstOrDefault(label => ReferenceEquals(label.Tag, editors[index]));
            axisLabel?.SetBounds(inset, editorY, labelWidth, ScaleMetric(RowHeight));
        }
    }

    private bool IsCameraAxisVisible(ModernNumericUpDown editor)
        => _experimental3DEnabled || editor == _positionZ && _advancedCameraSettings.Checked
            || editor != _positionZ && editor != _rotationX && editor != _rotationY;

    private int ScaleMetric(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96f));

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _help.Dispose();
        base.Dispose(disposing);
    }

    private sealed record CameraKindItem(SceneShotCameraKind Kind, string DisplayName)
    {
        public override string ToString() => UiLocalization.T(DisplayName);
    }

    private sealed record PreviewModeItem(ShotWorkspacePreviewMode Mode, string DisplayName)
    {
        public override string ToString() => UiLocalization.T(DisplayName);
    }

    private sealed record AspectRatioItem(SceneShotAspectRatio AspectRatio, string DisplayName)
    {
        public override string ToString() => UiLocalization.T(DisplayName);
    }

    private sealed record ProjectionItem(CameraProjection Projection, string DisplayName)
    {
        public override string ToString() => UiLocalization.T(DisplayName);
    }

    private static ModernNumericUpDown SpatialInput(string accessibleName) => new()
    {
        Minimum = (decimal)(-SceneShotSettings.MaximumPosition),
        Maximum = (decimal)SceneShotSettings.MaximumPosition,
        Increment = 10,
        DecimalPlaces = 1,
        AccessibleName = accessibleName
    };

    private static ModernNumericUpDown RotationInput(string accessibleName) => new()
    {
        Minimum = (decimal)(-SceneShotSettings.MaximumRotation),
        Maximum = (decimal)SceneShotSettings.MaximumRotation,
        Increment = 1,
        DecimalPlaces = 1,
        Suffix = "deg",
        AccessibleName = accessibleName
    };
}
