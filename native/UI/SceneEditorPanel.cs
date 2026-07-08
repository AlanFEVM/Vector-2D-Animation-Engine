namespace VectorAnimationEngine;

internal sealed class SceneEditorPanel : UserControl
{
    private readonly Label _sceneName = InfoLabel();
    private readonly Label _activeObject = InfoLabel();
    private readonly Label _stageSize = InfoLabel();
    private readonly Label _sceneStats = InfoLabel();
    private VectorScene? _scene;
    private DrawingObjectDefinition? _drawingObject;

    public SceneEditorPanel()
    {
        BackColor = Theme.Panel;
        Padding = new Padding(12);
        MinimumSize = new Size(240, 220);

        var title = new Label
        {
            Text = "Scene Editor",
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0, 8, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(content);
        content.BringToFront();

        AddRow(content, "Scene", _sceneName, 0);
        AddRow(content, "Active object", _activeObject, 1);
        AddRow(content, "Stage", _stageSize, 2);
        AddRow(content, "Contents", _sceneStats, 3);
        RefreshText();
    }

    public void BindScene(VectorScene scene)
    {
        _scene = scene;
        RefreshText();
    }

    public void SetActiveDrawingObject(DrawingObjectDefinition? drawingObject)
    {
        _drawingObject = drawingObject;
        RefreshText();
    }

    public void RefreshSceneStats() => RefreshText();

    private void RefreshText()
    {
        _sceneName.Text = "Master Scene";
        _activeObject.Text = _drawingObject is null ? "Scene" : $"{_drawingObject.Name} ({_drawingObject.Kind})";
        _stageSize.Text = _scene is null ? "-" : $"{_scene.StageWidth:0} x {_scene.StageHeight:0} vu";
        _sceneStats.Text = _scene is null ? "-" : $"{_scene.LayerCount} layers, {CompactFormat.Number(_scene.ObjectCount)} objects";
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
