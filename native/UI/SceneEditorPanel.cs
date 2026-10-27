namespace VectorAnimationEngine;

internal sealed class SceneSelectionChangedEventArgs : EventArgs
{
    public SceneSelectionChangedEventArgs(int index) => Index = index;

    public int Index { get; }
}

internal sealed class DrawingObjectOpenRequestedEventArgs : EventArgs
{
    public DrawingObjectOpenRequestedEventArgs(string drawingObjectId) => DrawingObjectId = drawingObjectId;

    public string DrawingObjectId { get; }
}

internal sealed class DrawingObjectAssetRequestedEventArgs(string drawingObjectId) : EventArgs
{
    public string DrawingObjectId { get; } = drawingObjectId;
}

internal sealed class SceneSettingsChangedEventArgs : EventArgs
{
    public SceneSettingsChangedEventArgs(int sceneIndex, SceneDimension dimension, CameraProjection projection)
    {
        SceneIndex = sceneIndex;
        Dimension = dimension;
        Projection = projection;
    }

    public int SceneIndex { get; }
    public SceneDimension Dimension { get; }
    public CameraProjection Projection { get; }
}

internal sealed class SceneEditorPanel : UserControl
{
    private readonly Label _sceneName = InfoLabel();
    private readonly ComboBox _cameraProjection = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _projectionLabel = InspectorModuleStyle.CreateFieldLabel("Camera projection");
    private readonly ListBox _scenes = new();
    private readonly ListBox _instances = new();
    private readonly Button _addScene = new SvgIconButton(SvgIconKind.Add) { Text = "Scene", AccessibleName = "+ Scene", Width = 82, Height = 28 };
    private readonly Button _addInstance = new SvgIconButton(SvgIconKind.Add) { Text = "Instance", AccessibleName = "+ Instance", Width = 104, Height = 28 };
    private IReadOnlyList<SceneDefinition> _sceneDefinitions = [];
    private IReadOnlyList<DrawingObjectDefinition> _drawingObjectDefinitions = [];
    private int _activeSceneIndex;
    private bool _updating;
    private bool _experimental3DEnabled;
    private readonly Label _pageTitle = InspectorModuleStyle.CreateTitle("Scene & Animation", primary: true);
    private readonly Label _sceneHeading = InfoLabel();
    private readonly Label _instanceHeading = InfoLabel();
    private readonly Panel _emptyInstances = new();
    private readonly Label _emptyTitle = InfoLabel();
    private readonly Label _emptyHint = InfoLabel();
    private readonly Button _emptyAdd = new SvgIconButton(SvgIconKind.Add) { Text = "Add instance" };

    internal void SetExperimental3DEnabled(bool enabled)
    {
        _experimental3DEnabled = enabled;
        _cameraProjection.Enabled = enabled;
        _cameraProjection.Visible = enabled;
        _projectionLabel.Visible = enabled;
        Height = PreferredPanelHeight;
        PerformLayout();
    }

    public SceneEditorPanel()
    {
        BackColor = Theme.Panel;
        Font = Theme.UiFont();
        Padding = Padding.Empty;
        MinimumSize = new Size(0, 260);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        _cameraProjection.Items.AddRange(["Orthographic", "Perspective"]);
        InspectorModuleStyle.StyleField(_cameraProjection);
        _projectionLabel.Dock = DockStyle.None;
        _cameraProjection.AccessibleName = "Camera projection";

        BuildUi();
        RefreshText();
    }

    public event EventHandler? AddSceneRequested;
    public event EventHandler? AddSceneInstanceRequested;
    public event EventHandler<SceneSettingsChangedEventArgs>? SceneSettingsChanged;
    public event EventHandler<SceneSelectionChangedEventArgs>? SceneSelectionChanged;
    public event EventHandler<DrawingObjectOpenRequestedEventArgs>? DrawingObjectOpenRequested;

    public int PreferredPanelHeight => ScaleMetric(376 + Math.Clamp(_sceneDefinitions.Count, 1, 3) * 54
        + (_experimental3DEnabled ? 40 : 0));

    public void BindProject(
        IReadOnlyList<SceneDefinition> scenes,
        int activeSceneIndex,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects)
    {
        var selectedInstanceSceneId = ActiveSceneDefinition()?.Id;
        var selectedInstanceId = SelectedInstanceId();
        _sceneDefinitions = scenes;
        _activeSceneIndex = activeSceneIndex;
        _drawingObjectDefinitions = drawingObjects;
        RefreshLists(selectedInstanceSceneId, selectedInstanceId);
        RefreshText();
        _sceneHeading.Text = $"{UiLocalization.T("Scenes")}  {scenes.Count}";
        _instanceHeading.Text = $"{UiLocalization.T("Scene Instances")}  {ActiveSceneDefinition()?.Instances.Count ?? 0}";
        var empty = ActiveSceneDefinition()?.Instances.Count is not > 0;
        _emptyInstances.Visible = empty;
        _instances.Visible = !empty;
        Height = PreferredPanelHeight;
        PerformLayout();
    }

    private void BuildUi()
    {
        ConfigureList(_scenes);
        ConfigureList(_instances);
        _scenes.Dock = _instances.Dock = DockStyle.None;
        _scenes.DrawItem += DrawSceneItem;
        _scenes.AccessibleName = "Scenes";
        _instances.AccessibleName = "Scene Instances";
        _pageTitle.Dock = DockStyle.None;
        InspectorModuleStyle.StylePageTitle(_pageTitle);
        _sceneHeading.Dock = _instanceHeading.Dock = DockStyle.None;
        _sceneHeading.Text = "Scenes";
        _instanceHeading.Text = "Scene Instances";
        _emptyTitle.Text = "No scene instances yet";
        _emptyHint.Text = "Add an instance to start composing your scene.";
        _emptyHint.ForeColor = Theme.PanelSecondaryText;
        _emptyTitle.TextAlign = _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyTitle.BackColor = _emptyHint.BackColor = Color.Transparent;
        _emptyTitle.Dock = _emptyHint.Dock = DockStyle.None;
        _emptyInstances.BackColor = Theme.PanelStrong;
        _emptyInstances.Controls.AddRange([_emptyTitle, _emptyHint, _emptyAdd]);
        _emptyInstances.Paint += (_, e) => SvgIcons.Draw(e.Graphics, SvgIconKind.Objects,
            new Rectangle((_emptyInstances.Width - ScaleMetric(24)) / 2, ScaleMetric(20), ScaleMetric(24), ScaleMetric(24)), Theme.PanelSecondaryText);
        foreach (var button in new[] { _addScene, _addInstance, _emptyAdd }) Theme.StyleStandardButton(button);
        Theme.StyleToolbarButton(_addScene);
        Theme.StyleToolbarButton(_addInstance);
        Theme.StyleStandardButton(_emptyAdd, active: true);
        _emptyAdd.Click += (_, _) => AddSceneInstanceRequested?.Invoke(this, EventArgs.Empty);
        Controls.AddRange([_pageTitle, _sceneHeading, _instanceHeading, _scenes, _instances,
            _addScene, _addInstance, _emptyInstances, _projectionLabel, _cameraProjection]);
        UiLocalization.Watch(this);

        _addScene.Click += (_, _) => AddSceneRequested?.Invoke(this, EventArgs.Empty);
        _addInstance.Click += (_, _) => AddSceneInstanceRequested?.Invoke(this, EventArgs.Empty);
        _scenes.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _scenes.SelectedIndex < 0) return;
            SceneSelectionChanged?.Invoke(this, new SceneSelectionChangedEventArgs(_scenes.SelectedIndex));
        };
        _instances.DoubleClick += (_, _) =>
        {
            var scene = _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count
                ? _sceneDefinitions[_activeSceneIndex]
                : null;
            if (scene is null || _instances.SelectedIndex < 0 || _instances.SelectedIndex >= scene.Instances.Count) return;
            DrawingObjectOpenRequested?.Invoke(
                this,
                new DrawingObjectOpenRequestedEventArgs(scene.Instances[_instances.SelectedIndex].DrawingObjectId));
        };
        _cameraProjection.SelectedIndexChanged += (_, _) => RaiseSceneSettingsChanged();
    }

    private int ScaleMetric(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var inset = ScaleMetric(16);
        var width = Math.Max(1, Width - inset * 2);
        _pageTitle.SetBounds(inset + ScaleMetric(24), ScaleMetric(16), Math.Max(1, width - ScaleMetric(24)), ScaleMetric(28));
        _sceneHeading.SetBounds(inset, ScaleMetric(70), Math.Max(1, width - ScaleMetric(112)), ScaleMetric(28));
        _addScene.SetBounds(Width - inset - ScaleMetric(104), ScaleMetric(68), ScaleMetric(104), ScaleMetric(Theme.ControlHeight));
        _scenes.ItemHeight = ScaleMetric(54);
        _scenes.SetBounds(inset, ScaleMetric(108), width, ScaleMetric(Math.Clamp(_sceneDefinitions.Count, 1, 3) * 54));
        var top = _scenes.Bottom + ScaleMetric(12);
        _cameraProjection.Visible = _experimental3DEnabled;
        _projectionLabel.Visible = _experimental3DEnabled;
        if (_experimental3DEnabled)
        {
            var labelWidth = (int)Math.Round(width * 0.48f);
            _projectionLabel.SetBounds(inset, top, labelWidth, ScaleMetric(40));
            _cameraProjection.SetBounds(inset + labelWidth, top + ScaleMetric(4), Math.Max(1, width - labelWidth), ScaleMetric(Theme.ControlHeight));
            top += ScaleMetric(40);
        }
        top += ScaleMetric(18);
        _instanceHeading.SetBounds(inset, top, Math.Max(1, width - ScaleMetric(112)), ScaleMetric(28));
        _addInstance.SetBounds(Width - inset - ScaleMetric(104), top, ScaleMetric(104), ScaleMetric(Theme.ControlHeight));
        top += ScaleMetric(42);
        _instances.SetBounds(inset, top, width, ScaleMetric(180));
        _emptyInstances.Bounds = _instances.Bounds;
        _emptyTitle.SetBounds(ScaleMetric(8), ScaleMetric(56), Math.Max(1, width - ScaleMetric(16)), ScaleMetric(28));
        _emptyHint.SetBounds(ScaleMetric(8), ScaleMetric(86), Math.Max(1, width - ScaleMetric(16)), ScaleMetric(40));
        _emptyAdd.SetBounds(Math.Max(0, (width - ScaleMetric(132)) / 2), ScaleMetric(134), Math.Min(width, ScaleMetric(132)), ScaleMetric(Theme.ControlHeight));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SvgIcons.Draw(e.Graphics, SvgIconKind.SceneSlate, new Rectangle(ScaleMetric(16), ScaleMetric(21), ScaleMetric(18), ScaleMetric(18)), Theme.PanelSecondaryText);
        InspectorModuleStyle.DrawDivider(this, e.Graphics, ScaleMetric(54));
        InspectorModuleStyle.DrawDivider(this, e.Graphics, _instanceHeading.Top - ScaleMetric(16));
        InspectorModuleStyle.DrawBottomDivider(this, e.Graphics);
    }

    private void DrawSceneItem(object? sender, DrawItemEventArgs e)
    {
        if ((uint)e.Index >= _scenes.Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? Theme.AccentSurface : Theme.Panel;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var iconBounds = new Rectangle(e.Bounds.Left + ScaleMetric(12), e.Bounds.Top + ScaleMetric(16), ScaleMetric(20), ScaleMetric(20));
        SvgIcons.Draw(e.Graphics, SvgIconKind.SceneSlate, iconBounds, selected ? Theme.Accent : Theme.PanelSecondaryText);
        var textBounds = new Rectangle(iconBounds.Right + ScaleMetric(12), e.Bounds.Top + ScaleMetric(7), Math.Max(1, e.Bounds.Width - ScaleMetric(56)), ScaleMetric(22));
        TextRenderer.DrawText(e.Graphics, _scenes.Items[e.Index]?.ToString(), Font, textBounds,
            Theme.ReadableText(background, Theme.Text), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        textBounds.Y += ScaleMetric(22);
        TextRenderer.DrawText(e.Graphics, UiLocalization.T(e.Index == _activeSceneIndex ? "Currently editing" : "Scene"), _scenes.Font, textBounds,
            Theme.ReadableText(background, selected ? Theme.Mix(Theme.PanelSecondaryText, Theme.AccentLabel, 0.45f) : Theme.PanelSecondaryText), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (selected)
        {
            using var accent = new Pen(Theme.Accent, ScaleMetric(2));
            e.Graphics.DrawLine(accent, e.Bounds.Left + 1, e.Bounds.Top, e.Bounds.Left + 1, e.Bounds.Bottom);
        }
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void RefreshLists(string? selectedInstanceSceneId, string? selectedInstanceId)
    {
        var sceneItems = new string[_sceneDefinitions.Count];
        for (var i = 0; i < _sceneDefinitions.Count; i++) sceneItems[i] = _sceneDefinitions[i].Name;

        var drawingObjectNames = new Dictionary<string, string>(_drawingObjectDefinitions.Count, StringComparer.Ordinal);
        for (var i = 0; i < _drawingObjectDefinitions.Count; i++)
        {
            var item = _drawingObjectDefinitions[i];
            drawingObjectNames.TryAdd(item.Id, item.Name);
        }

        var sceneDefinition = ActiveSceneDefinition();
        string[] instanceItems = sceneDefinition is null ? [] : new string[sceneDefinition.Instances.Count];
        var selectedInstanceIndex = -1;
        if (sceneDefinition is not null)
        {
            var preserveInstanceSelection = string.Equals(sceneDefinition.Id, selectedInstanceSceneId, StringComparison.Ordinal);
            for (var i = 0; i < sceneDefinition.Instances.Count; i++)
            {
                var instance = sceneDefinition.Instances[i];
                var source = drawingObjectNames.TryGetValue(instance.DrawingObjectId, out var drawingObjectName)
                    ? drawingObjectName
                    : "Missing object";
                instanceItems[i] = $"{instance.Name} -> {source}";
                if (preserveInstanceSelection
                    && selectedInstanceIndex < 0
                    && string.Equals(instance.Id, selectedInstanceId, StringComparison.Ordinal))
                {
                    selectedInstanceIndex = i;
                }
            }
        }

        var previousUpdating = _updating;
        _updating = true;
        SuspendLayout();
        try
        {
            SynchronizeItems(_scenes, sceneItems);
            SetSelectedIndex(_scenes, _activeSceneIndex);
            SynchronizeItems(_instances, instanceItems);
            SetSelectedIndex(_instances, selectedInstanceIndex);

            var projectionIndex = sceneDefinition is null
                ? -1
                : sceneDefinition.Camera.Projection == CameraProjection.Orthographic ? 0 : 1;
            if (_cameraProjection.SelectedIndex != projectionIndex) _cameraProjection.SelectedIndex = projectionIndex;
        }
        finally
        {
            ResumeLayout(performLayout: false);
            _updating = previousUpdating;
        }
    }

    private SceneDefinition? ActiveSceneDefinition()
    {
        return _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count
            ? _sceneDefinitions[_activeSceneIndex]
            : null;
    }

    private string? SelectedInstanceId()
    {
        var sceneDefinition = ActiveSceneDefinition();
        return sceneDefinition is not null
               && _instances.SelectedIndex >= 0
               && _instances.SelectedIndex < sceneDefinition.Instances.Count
            ? sceneDefinition.Instances[_instances.SelectedIndex].Id
            : null;
    }

    private static void SynchronizeItems(ListBox list, IReadOnlyList<string> desiredItems)
    {
        var sharedCount = Math.Min(list.Items.Count, desiredItems.Count);
        var firstChangedIndex = 0;
        while (firstChangedIndex < sharedCount
               && string.Equals(list.Items[firstChangedIndex] as string, desiredItems[firstChangedIndex], StringComparison.Ordinal))
        {
            firstChangedIndex++;
        }

        if (firstChangedIndex == sharedCount && list.Items.Count == desiredItems.Count) return;

        list.BeginUpdate();
        try
        {
            for (var i = firstChangedIndex; i < sharedCount; i++)
            {
                if (!string.Equals(list.Items[i] as string, desiredItems[i], StringComparison.Ordinal))
                {
                    list.Items[i] = desiredItems[i];
                }
            }

            while (list.Items.Count > desiredItems.Count) list.Items.RemoveAt(list.Items.Count - 1);
            for (var i = list.Items.Count; i < desiredItems.Count; i++) list.Items.Add(desiredItems[i]);
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private static void SetSelectedIndex(ListBox list, int index)
    {
        var normalizedIndex = index >= 0 && index < list.Items.Count ? index : -1;
        if (list.SelectedIndex != normalizedIndex) list.SelectedIndex = normalizedIndex;
    }

    private void RefreshText()
    {
        var sceneDefinition = _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count ? _sceneDefinitions[_activeSceneIndex] : null;
        SetInfoText(_sceneName, sceneDefinition?.Name ?? "Scene");
    }

    private static void SetInfoText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
    }

    private void RaiseSceneSettingsChanged()
    {
        var sceneDefinition = ActiveSceneDefinition();
        if (_updating || sceneDefinition is null || _cameraProjection.SelectedIndex < 0) return;
        var projection = _cameraProjection.SelectedIndex == 0 ? CameraProjection.Orthographic : CameraProjection.Perspective;
        SceneSettingsChanged?.Invoke(this, new SceneSettingsChangedEventArgs(_activeSceneIndex, sceneDefinition.Dimension, projection));
    }


    private static void ConfigureList(ListBox list)
    {
        list.Dock = DockStyle.Fill;
        list.Margin = new Padding(0, 4, 0, 8);
        Theme.StyleListBox(list);
        list.IntegralHeight = false;
    }


    private static Label InfoLabel()
    {
        var label = InspectorModuleStyle.CreateFieldLabel(string.Empty);
        label.BackColor = Theme.Panel;
        label.Margin = new Padding(0, 3, 0, 3);
        return label;
    }
}
