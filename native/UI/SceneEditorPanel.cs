namespace VectorAnimationEngine;

internal sealed class SceneSelectionChangedEventArgs : EventArgs
{
    public SceneSelectionChangedEventArgs(int index) => Index = index;

    public int Index { get; }
}

internal sealed class DrawingObjectSelectionChangedEventArgs : EventArgs
{
    public DrawingObjectSelectionChangedEventArgs(int index) => Index = index;

    public int Index { get; }
}

internal sealed class DrawingObjectOpenRequestedEventArgs : EventArgs
{
    public DrawingObjectOpenRequestedEventArgs(string drawingObjectId) => DrawingObjectId = drawingObjectId;

    public string DrawingObjectId { get; }
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
    private readonly Label _mode = InfoLabel();
    private readonly Label _sceneName = InfoLabel();
    private readonly Label _activeObject = InfoLabel();
    private readonly ComboBox _sceneType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cameraProjection = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _stageSize = InfoLabel();
    private readonly Label _sceneStats = InfoLabel();
    private readonly ListBox _scenes = new();
    private readonly ListBox _drawingObjects = new();
    private readonly ListBox _instances = new();
    private readonly Button _addScene = new() { Text = "+ Scene", Width = 82, Height = 28 };
    private readonly Button _addObject = new() { Text = "+ Object", Width = 82, Height = 28 };
    private readonly Button _addInstance = new() { Text = "+ Instance", Width = 92, Height = 28 };
    private VectorScene? _scene;
    private IReadOnlyList<SceneDefinition> _sceneDefinitions = [];
    private IReadOnlyList<DrawingObjectDefinition> _drawingObjectDefinitions = [];
    private int _activeSceneIndex;
    private int _activeDrawingObjectIndex;
    private bool _updating;

    public SceneEditorPanel()
    {
        BackColor = Theme.Panel;
        Padding = new Padding(10);
        MinimumSize = new Size(240, 340);
        _sceneType.Items.AddRange(["2D scene with depth", "3D scene"]);
        _cameraProjection.Items.AddRange(["Orthographic", "Perspective"]);
        Theme.StyleComboBox(_sceneType);
        Theme.StyleComboBox(_cameraProjection);

        BuildUi();
        RefreshText();
    }

    public event EventHandler? AddSceneRequested;
    public event EventHandler? AddDrawingObjectRequested;
    public event EventHandler? AddSceneInstanceRequested;
    public event EventHandler<SceneSettingsChangedEventArgs>? SceneSettingsChanged;
    public event EventHandler<SceneSelectionChangedEventArgs>? SceneSelectionChanged;
    public event EventHandler<DrawingObjectSelectionChangedEventArgs>? DrawingObjectSelectionChanged;
    public event EventHandler<DrawingObjectOpenRequestedEventArgs>? DrawingObjectOpenRequested;

    public void BindScene(VectorScene scene)
    {
        _scene = scene;
        RefreshText();
    }

    public void BindProject(
        IReadOnlyList<SceneDefinition> scenes,
        int activeSceneIndex,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int activeDrawingObjectIndex)
    {
        _sceneDefinitions = scenes;
        _activeSceneIndex = activeSceneIndex;
        _drawingObjectDefinitions = drawingObjects;
        _activeDrawingObjectIndex = activeDrawingObjectIndex;
        RefreshLists();
        RefreshText();
    }

    public void SetActiveDrawingObject(DrawingObjectDefinition? drawingObject)
    {
        _activeObject.Text = drawingObject is null ? "Scene" : $"{drawingObject.Name} ({drawingObject.Kind})";
    }

    public void RefreshSceneStats() => RefreshText();

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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 224));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Scene Edit",
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
            RowCount = 7,
            Padding = new Padding(0, 8, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(content, 0, 1);

        AddRow(content, "Mode", _mode, 0);
        AddRow(content, "Scene", _sceneName, 1);
        AddRow(content, "Scene type", _sceneType, 2);
        AddRow(content, "Camera", _cameraProjection, 3);
        AddRow(content, "Edit object", _activeObject, 4);
        AddRow(content, "Stage", _stageSize, 5);
        AddRow(content, "Contents", _sceneStats, 6);

        var managers = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(0, 8, 0, 0)
        };
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.Controls.Add(managers, 0, 2);

        managers.Controls.Add(HeaderWithButton("Scenes", _addScene), 0, 0);
        ConfigureList(_scenes);
        managers.Controls.Add(_scenes, 0, 1);
        managers.Controls.Add(HeaderWithButton("Drawing Objects", _addObject), 0, 2);
        ConfigureList(_drawingObjects);
        managers.Controls.Add(_drawingObjects, 0, 3);
        managers.Controls.Add(HeaderWithButton("Scene Instances", _addInstance), 0, 4);
        ConfigureList(_instances);
        managers.Controls.Add(_instances, 0, 5);

        _addScene.Click += (_, _) => AddSceneRequested?.Invoke(this, EventArgs.Empty);
        _addObject.Click += (_, _) => AddDrawingObjectRequested?.Invoke(this, EventArgs.Empty);
        _addInstance.Click += (_, _) => AddSceneInstanceRequested?.Invoke(this, EventArgs.Empty);
        _scenes.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _scenes.SelectedIndex < 0) return;
            SceneSelectionChanged?.Invoke(this, new SceneSelectionChangedEventArgs(_scenes.SelectedIndex));
        };
        _drawingObjects.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _drawingObjects.SelectedIndex < 0) return;
            DrawingObjectSelectionChanged?.Invoke(this, new DrawingObjectSelectionChangedEventArgs(_drawingObjects.SelectedIndex));
        };
        _drawingObjects.DoubleClick += (_, _) =>
        {
            if (_drawingObjects.SelectedIndex < 0 || _drawingObjects.SelectedIndex >= _drawingObjectDefinitions.Count) return;
            DrawingObjectOpenRequested?.Invoke(
                this,
                new DrawingObjectOpenRequestedEventArgs(_drawingObjectDefinitions[_drawingObjects.SelectedIndex].Id));
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
        _sceneType.SelectedIndexChanged += (_, _) => RaiseSceneSettingsChanged();
        _cameraProjection.SelectedIndexChanged += (_, _) => RaiseSceneSettingsChanged();
    }

    private void RefreshLists()
    {
        _updating = true;
        _scenes.Items.Clear();
        for (var i = 0; i < _sceneDefinitions.Count; i++) _scenes.Items.Add(_sceneDefinitions[i].Name);
        if (_activeSceneIndex >= 0 && _activeSceneIndex < _scenes.Items.Count) _scenes.SelectedIndex = _activeSceneIndex;

        _drawingObjects.Items.Clear();
        for (var i = 0; i < _drawingObjectDefinitions.Count; i++)
        {
            var item = _drawingObjectDefinitions[i];
            _drawingObjects.Items.Add($"{item.Name}  [{item.Kind}]");
        }

        if (_activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjects.Items.Count) _drawingObjects.SelectedIndex = _activeDrawingObjectIndex;
        _instances.Items.Clear();
        var sceneDefinition = _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count ? _sceneDefinitions[_activeSceneIndex] : null;
        if (sceneDefinition is not null)
        {
            foreach (var instance in sceneDefinition.Instances)
            {
                var drawingObject = _drawingObjectDefinitions.FirstOrDefault(item => item.Id == instance.DrawingObjectId);
                var source = drawingObject?.Name ?? "Missing object";
                _instances.Items.Add($"{instance.Name} -> {source}");
            }
        }

        if (sceneDefinition is not null)
        {
            _sceneType.SelectedIndex = sceneDefinition.Dimension == SceneDimension.TwoD ? 0 : 1;
            _cameraProjection.SelectedIndex = sceneDefinition.Camera.Projection == CameraProjection.Orthographic ? 0 : 1;
        }
        else
        {
            _sceneType.SelectedIndex = -1;
            _cameraProjection.SelectedIndex = -1;
        }

        _updating = false;
    }

    private void RefreshText()
    {
        _mode.Text = "Scene Edit";
        var sceneDefinition = _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count ? _sceneDefinitions[_activeSceneIndex] : null;
        _sceneName.Text = sceneDefinition?.Name ?? "Scene";
        if (_activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjectDefinitions.Count)
        {
            var drawingObject = _drawingObjectDefinitions[_activeDrawingObjectIndex];
            _activeObject.Text = $"{drawingObject.Name} ({drawingObject.Kind})";
        }
        else
        {
            _activeObject.Text = "Scene";
        }

        _stageSize.Text = _scene is null ? "-" : $"{_scene.StageWidth:0} x {_scene.StageHeight:0} vu";
        var instanceCount = sceneDefinition?.Instances.Count ?? 0;
        _sceneStats.Text = _scene is null ? "-" : $"{_scene.LayerCount} layers, {CompactFormat.Number(_scene.ObjectCount)} objects, {instanceCount} scene instances";
    }

    private void RaiseSceneSettingsChanged()
    {
        if (_updating || _activeSceneIndex < 0) return;
        if (_sceneType.SelectedIndex < 0 || _cameraProjection.SelectedIndex < 0) return;
        var dimension = _sceneType.SelectedIndex == 0 ? SceneDimension.TwoD : SceneDimension.ThreeD;
        var projection = _cameraProjection.SelectedIndex == 0 ? CameraProjection.Orthographic : CameraProjection.Perspective;
        SceneSettingsChanged?.Invoke(this, new SceneSettingsChangedEventArgs(_activeSceneIndex, dimension, projection));
    }

    private static Panel HeaderWithButton(string text, Button button)
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
        button.Dock = DockStyle.Right;
        Theme.StyleButton(button);
        panel.Controls.Add(label);
        panel.Controls.Add(button);
        button.BringToFront();
        return panel;
    }

    private static void ConfigureList(ListBox list)
    {
        list.Dock = DockStyle.Fill;
        list.BackColor = Theme.Panel;
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = Theme.UiFont(9.2f);
        list.ItemHeight = 22;
        list.IntegralHeight = false;
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
