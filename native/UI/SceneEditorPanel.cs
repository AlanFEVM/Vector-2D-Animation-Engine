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
    private readonly ListBox _scenes = new();
    private readonly ListBox _instances = new();
    private readonly Button _addScene = new() { Text = "+ Scene", Width = 82, Height = 28 };
    private readonly Button _addInstance = new() { Text = "+ Instance", Width = 92, Height = 28 };
    private IReadOnlyList<SceneDefinition> _sceneDefinitions = [];
    private IReadOnlyList<DrawingObjectDefinition> _drawingObjectDefinitions = [];
    private int _activeSceneIndex;
    private bool _updating;

    public SceneEditorPanel()
    {
        BackColor = Theme.Panel;
        Padding = new Padding(10);
        MinimumSize = new Size(240, 260);
        _cameraProjection.Items.AddRange(["Orthographic", "Perspective"]);
        Theme.StyleComboBox(_cameraProjection);

        BuildUi();
        RefreshText();
    }

    public event EventHandler? AddSceneRequested;
    public event EventHandler? AddSceneInstanceRequested;
    public event EventHandler<SceneSettingsChangedEventArgs>? SceneSettingsChanged;
    public event EventHandler<SceneSelectionChangedEventArgs>? SceneSelectionChanged;
    public event EventHandler<DrawingObjectOpenRequestedEventArgs>? DrawingObjectOpenRequested;

    public int PreferredPanelHeight => 300;

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
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Scene & Animation",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 2; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(content, 0, 1);

        AddRow(content, "Scene", _sceneName, 0);
        AddRow(content, "Camera", _cameraProjection, 1);

        var managers = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 8, 0, 0)
        };
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.Controls.Add(managers, 0, 2);

        managers.Controls.Add(HeaderWithButton("Scenes", _addScene), 0, 0);
        ConfigureList(_scenes);
        managers.Controls.Add(_scenes, 0, 1);
        managers.Controls.Add(HeaderWithButton("Scene Instances", _addInstance), 0, 2);
        ConfigureList(_instances);
        managers.Controls.Add(_instances, 0, 3);

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

    private static Panel HeaderWithButton(string text, params Button[] buttons)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = Padding.Empty };
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        foreach (var button in buttons)
        {
            button.Margin = Padding.Empty;
            Theme.StyleButton(button);
            actions.Controls.Add(button);
        }
        panel.Controls.Add(label);
        panel.Controls.Add(actions);
        actions.BringToFront();
        return panel;
    }

    private static void ConfigureList(ListBox list)
    {
        list.Dock = DockStyle.Fill;
        Theme.StyleListBox(list);
    }

    private static void AddRow(TableLayoutPanel parent, string label, Control value, int row)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 8, 3)
        }, 0, row);

        parent.Controls.Add(value, 1, row);
    }

    private static Label InfoLabel() => new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(0, 3, 0, 3)
    };
}
