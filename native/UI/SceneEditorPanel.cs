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

internal sealed class SceneEditorPanel : UserControl
{
    private readonly Label _mode = InfoLabel();
    private readonly Label _sceneName = InfoLabel();
    private readonly Label _activeObject = InfoLabel();
    private readonly Label _stageSize = InfoLabel();
    private readonly Label _sceneStats = InfoLabel();
    private readonly ListBox _scenes = new();
    private readonly ListBox _drawingObjects = new();
    private readonly Button _addScene = new() { Text = "+ Scene", Width = 82, Height = 28 };
    private readonly Button _addObject = new() { Text = "+ Object", Width = 82, Height = 28 };
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

        BuildUi();
        RefreshText();
    }

    public event EventHandler? AddSceneRequested;
    public event EventHandler? AddDrawingObjectRequested;
    public event EventHandler<SceneSelectionChangedEventArgs>? SceneSelectionChanged;
    public event EventHandler<DrawingObjectSelectionChangedEventArgs>? DrawingObjectSelectionChanged;

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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
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
            RowCount = 5,
            Padding = new Padding(0, 8, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(content, 0, 1);

        AddRow(content, "Mode", _mode, 0);
        AddRow(content, "Scene", _sceneName, 1);
        AddRow(content, "Edit object", _activeObject, 2);
        AddRow(content, "Stage", _stageSize, 3);
        AddRow(content, "Contents", _sceneStats, 4);

        var managers = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 8, 0, 0)
        };
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        managers.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        managers.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        root.Controls.Add(managers, 0, 2);

        managers.Controls.Add(HeaderWithButton("Scenes", _addScene), 0, 0);
        ConfigureList(_scenes);
        managers.Controls.Add(_scenes, 0, 1);
        managers.Controls.Add(HeaderWithButton("Drawing Objects", _addObject), 0, 2);
        ConfigureList(_drawingObjects);
        managers.Controls.Add(_drawingObjects, 0, 3);

        _addScene.Click += (_, _) => AddSceneRequested?.Invoke(this, EventArgs.Empty);
        _addObject.Click += (_, _) => AddDrawingObjectRequested?.Invoke(this, EventArgs.Empty);
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
        _updating = false;
    }

    private void RefreshText()
    {
        _mode.Text = "Scene Edit";
        _sceneName.Text = _activeSceneIndex >= 0 && _activeSceneIndex < _sceneDefinitions.Count ? _sceneDefinitions[_activeSceneIndex].Name : "Master Scene";
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
        _sceneStats.Text = _scene is null ? "-" : $"{_scene.LayerCount} layers, {CompactFormat.Number(_scene.ObjectCount)} objects";
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
