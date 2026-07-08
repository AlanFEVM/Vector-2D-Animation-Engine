using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed class MainForm : Form
{
    private const double TargetUps = 300.0;
    private const double TargetRenderFps = 144.0;
    private const double UpdateStepSeconds = 1.0 / TargetUps;
    private const double RenderStepSeconds = 1.0 / TargetRenderFps;
    private const double MaxFrameSeconds = 0.1;

    private readonly VectorScene _scene = new();
    private readonly StageControl _stage;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DrawSettings _drawSettings = new();
    private readonly ListBox _layers = new();
    private readonly TrackBar _frameSlider = new();
    private readonly Label _fps = MetricLabel("FPS 0", 76);
    private readonly Label _draw = MetricLabel("Draw 0", 210);
    private readonly Label _atoms = MetricLabel("Atoms 0", 210);
    private readonly Label _zoom = MetricLabel("Zoom 100%", 112);
    private readonly Label _selected = InspectorLabel("Selected: None");
    private readonly Label _selectedLayer = InspectorLabel("Layer: -");
    private readonly Label _selectedAtoms = InspectorLabel("Atoms: -");
    private readonly Label _objectMetric = InspectorLabel("Objects: 0");
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _stroke = new() { Minimum = 0, Maximum = 12, Value = 2, Width = 160 };
    private readonly Button _play = new() { Text = "Play", Width = 72 };
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();
    private readonly Dictionary<string, Button> _drawingObjectTabButtons = new();
    private readonly List<DrawingObjectDefinition> _drawingObjects = [];
    private readonly AnimatedToolTip _toolTip = new();
    private readonly WorkspaceTabs _workspaceTabs = new();
    private readonly Panel _workspaceHeader = new();
    private readonly FlowLayoutPanel _drawingObjectTabs = new();
    private readonly TimelineStrip _timeline;
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _renderFpsStatus = StatusLabel("Render FPS 0");
    private readonly ToolStripStatusLabel _animationFpsStatus = StatusLabel("Animation FPS 24");
    private readonly ToolStripStatusLabel _zoomStatus = StatusLabel("Zoom 100%");
    private readonly ToolStripStatusLabel _hotReloadStatus = StatusLabel("Hot Reload On");
    private readonly PlaybackSettingsPanel _playbackSettings = new();
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly SceneEditorPanel _sceneEditorPanel = new();
    private readonly LibraryVaultPanel _libraryVaultPanel = new();
    private readonly Panel _basicInspectorPage = new();
    private readonly Panel _objectInspector = new();
    private readonly Panel _animationPage = new();
    private ToolMode _tool = ToolMode.Select;
    private bool _playing;
    private int _frame;
    private int _selectedObject = -1;
    private DrawingElementHit _selectedElement = DrawingElementHit.None;
    private readonly List<int> _selectedObjects = new();
    private readonly Dictionary<int, PointF> _selectedMoveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurveStarts = new();
    private Point? _lastMouse;
    private Point? _startScreen;
    private PointF? _startWorld;
    private PointF? _selectedStart;
    private PointF? _curveControlStart;
    private PointF? _resizeStartCenter;
    private SizeF? _resizeStartSize;
    private float _resizeStartAngle;
    private EditHandleKind _activeHandle = EditHandleKind.None;
    private bool _geometryDirty;
    private double _smoothedFps;
    private double _smoothedUps;
    private double _updateAccumulator;
    private double _renderAccumulator;
    private double _playbackAccumulator;
    private double _timeSinceLastRender;
    private double _upsSampleTime;
    private int _updatesThisSample;
    private bool _renderRequested = true;
    private bool _syncingFrame;
    private bool _updatingStrokeInput;
    private bool _viewPanning;
    private bool _viewZooming;
    private bool _marqueeSelecting;
    private bool _detachedSelectionForMove;
    private Point? _marqueeStart;
    private ShapeKind _lastSettingsShape = ShapeKind.Rectangle;
    private int _activeDrawingObjectIndex;

    public MainForm()
    {
        Text = "Vector 2D Animation Engine";
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(1120, 720);
        BackColor = Theme.App;
        Font = Theme.UiFont();
        KeyPreview = true;

        _stage = new StageControl(_scene) { Dock = DockStyle.Fill };
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = 192 };
        _drawSettingsPanel = new DrawSettingsPanel(_drawSettings);
        CreateDefaultDrawingObjects();
        BuildUi();
        HookEvents();
        CreateNewProject();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Top };
        PaintBottomBorder(top);
        Controls.Add(top);
        top.Controls.Add(new Label { Text = "V2", Left = 14, Top = 11, Width = 30, Height = 30, ForeColor = Theme.Accent, BackColor = Theme.Top, Font = Theme.UiFont(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        top.Controls.Add(Theme.Label("Vector 2D Animation Engine", 58, 15, 260, Theme.Text, Theme.UiFont(10, FontStyle.Bold)));
        _play.Left = 330;
        _play.Top = 11;
        _play.Height = 32;
        Theme.StyleButton(_play);
        top.Controls.Add(_play);
        top.Controls.Add(Theme.Label("Frame", 420, 15, 58, Theme.Muted));
        _frameSlider.Left = 476;
        _frameSlider.Top = 9;
        _frameSlider.Width = 430;
        _frameSlider.Minimum = 0;
        _frameSlider.Maximum = 239;
        _frameSlider.TickFrequency = 24;
        top.Controls.Add(_frameSlider);
        var generate = new Button { Text = "Run Stress Scene", Width = 150, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        generate.Left = Width - 282;
        generate.Top = 11;
        Theme.StyleButton(generate);
        generate.Click += (_, _) => Generate();
        var fit = new Button { Text = "Fit Stage", Width = 104, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        fit.Left = Width - 124;
        fit.Top = 11;
        Theme.StyleButton(fit);
        fit.Click += (_, _) =>
        {
            _stage.Fit();
            UpdateStatusBar();
        };
        top.Controls.Add(generate);
        top.Controls.Add(fit);
        top.Resize += (_, _) =>
        {
            fit.Left = top.ClientSize.Width - fit.Width - 12;
            generate.Left = fit.Left - generate.Width - 8;
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.App };
        Controls.Add(body);
        body.BringToFront();
        _workspaceHeader.Dock = DockStyle.Top;
        _workspaceHeader.Height = 84;
        _workspaceHeader.BackColor = Theme.Top;
        PaintBottomBorder(_workspaceHeader);
        body.Controls.Add(_workspaceHeader);
        _workspaceTabs.Dock = DockStyle.Top;
        _workspaceTabs.Height = 44;
        _workspaceHeader.Controls.Add(_workspaceTabs);
        _drawingObjectTabs.Dock = DockStyle.Bottom;
        _drawingObjectTabs.Height = 40;
        _drawingObjectTabs.BackColor = Theme.Top;
        _drawingObjectTabs.FlowDirection = FlowDirection.LeftToRight;
        _drawingObjectTabs.WrapContents = false;
        _drawingObjectTabs.Padding = new Padding(8, 4, 8, 6);
        _drawingObjectTabs.Margin = Padding.Empty;
        _workspaceHeader.Controls.Add(_drawingObjectTabs);
        BuildDrawingObjectTabs();

        var tools = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 48, BackColor = Theme.Top, FlowDirection = FlowDirection.TopDown, Padding = new Padding(6, 10, 6, 6) };
        PaintRightBorder(tools);
        body.Controls.Add(tools);
        AddTool(tools, SvgIconKind.Select, ToolMode.Select, "Select");
        AddTool(tools, SvgIconKind.Pan, ToolMode.Hand, "Pan View");
        AddTool(tools, SvgIconKind.Rectangle, ToolMode.Rectangle, "Rectangle Tool");
        AddTool(tools, SvgIconKind.Ellipse, ToolMode.Ellipse, "Ellipse Tool");
        AddTool(tools, SvgIconKind.Triangle, ToolMode.Triangle, "Triangle Tool");
        AddTool(tools, SvgIconKind.Polygon, ToolMode.Polygon, "Polygon Tool");
        AddTool(tools, SvgIconKind.Star, ToolMode.Star, "Star Tool");
        AddTool(tools, SvgIconKind.Line, ToolMode.Line, "Line Tool");
        AddTool(tools, SvgIconKind.Fill, ToolMode.Fill, "Fill Tool");
        RefreshToolButtons();

        var vaultDrawer = new Panel { Dock = DockStyle.Left, Width = 306, BackColor = Theme.Panel, Padding = new Padding(0), Visible = false };
        PaintRightBorder(vaultDrawer);
        _libraryVaultPanel.Dock = DockStyle.Fill;
        vaultDrawer.Controls.Add(_libraryVaultPanel);
        body.Controls.Add(vaultDrawer);

        var vaultButton = new SvgIconButton(SvgIconKind.Vault) { Margin = new Padding(0, 16, 0, 8), AccessibleName = "Vault" };
        Theme.StyleButton(vaultButton);
        vaultButton.MouseEnter += (_, _) => _toolTip.ShowFor(vaultButton, "Vault");
        vaultButton.MouseLeave += (_, _) => _toolTip.HideTip();
        vaultButton.Click += (_, _) =>
        {
            vaultDrawer.Visible = !vaultDrawer.Visible;
            if (vaultDrawer.Visible) Theme.StyleActiveButton(vaultButton);
            else Theme.StyleButton(vaultButton);
            body.PerformLayout();
        };
        tools.Controls.Add(vaultButton);

        var rightSidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 610,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        rightSidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 286));
        rightSidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 324));
        rightSidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(rightSidebar);

        var inspector = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(14, 16, 14, 12), AutoScroll = true };
        PaintLeftBorder(inspector);
        rightSidebar.Controls.Add(inspector, 1, 0);
        BuildInspectorPages(inspector);

        var layerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(12, 10, 12, 12)
        };
        layerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        PaintLeftBorder(layerPanel);
        rightSidebar.Controls.Add(layerPanel, 0, 0);

        var layerTitle = new Label { Text = "Layers", Dock = DockStyle.Fill, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        layerPanel.Controls.Add(layerTitle, 0, 0);
        var layerButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Panel, Padding = new Padding(0, 2, 0, 8) };
        var solo = new Button { Text = "Solo", Width = 78, Height = 32 };
        Theme.StyleButton(solo);
        solo.Click += (_, _) => { _scene.SoloLayer(_scene.ActiveLayer); RefreshLayers(); _stage.Invalidate(); };
        var all = new Button { Text = "All", Width = 60, Height = 32 };
        Theme.StyleButton(all);
        all.Click += (_, _) => { _scene.ShowAllLayers(); RefreshLayers(); _stage.Invalidate(); };
        layerButtons.Controls.Add(solo);
        layerButtons.Controls.Add(all);
        layerPanel.Controls.Add(layerButtons, 0, 1);
        _layers.Dock = DockStyle.Fill;
        _layers.BackColor = Theme.Panel;
        _layers.ForeColor = Theme.Text;
        _layers.Font = Theme.UiFont(9.5f);
        _layers.ItemHeight = 22;
        _layers.BorderStyle = BorderStyle.None;
        layerPanel.Controls.Add(_layers, 0, 2);

        var stagePanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage };
        body.Controls.Add(stagePanel);
        stagePanel.BringToFront();
        var metrics = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Top };
        PaintBottomBorder(metrics);
        metrics.Controls.Add(_fps);
        _draw.Left = 92;
        metrics.Controls.Add(_draw);
        _atoms.Left = 310;
        metrics.Controls.Add(_atoms);
        _zoom.Left = 540;
        metrics.Controls.Add(_zoom);
        var zoomIn = new Button { Text = "+", Width = 34, Height = 28, Top = 6, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleButton(zoomIn);
        zoomIn.Left = stagePanel.Width - 44;
        zoomIn.Click += (_, _) =>
        {
            _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), 1.22f);
            UpdateStatusBar();
        };
        var zoomOut = new Button { Text = "-", Width = 34, Height = 28, Top = 6, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleButton(zoomOut);
        zoomOut.Left = stagePanel.Width - 84;
        zoomOut.Click += (_, _) =>
        {
            _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), 0.82f);
            UpdateStatusBar();
        };
        metrics.Controls.Add(zoomIn);
        metrics.Controls.Add(zoomOut);
        stagePanel.Controls.Add(_stage);
        stagePanel.Controls.Add(metrics);
        metrics.BringToFront();
        _workspaceTabs.BringToFront();

        Controls.Add(_timeline);
        BuildStatusBar();
        Controls.Add(_statusBar);
    }

    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.SizingGrip = false;
        _statusBar.BackColor = Theme.Top;
        _statusBar.ForeColor = Theme.Muted;
        _statusBar.Font = Theme.UiFont(9);
        _statusBar.Padding = new Padding(8, 2, 8, 2);
        _statusBar.Items.Add(_renderFpsStatus);
        _statusBar.Items.Add(StatusSeparator());
        _statusBar.Items.Add(_animationFpsStatus);
        _statusBar.Items.Add(StatusSeparator());
        _statusBar.Items.Add(_zoomStatus);
        if (IsHotReloadEnabled())
        {
            _statusBar.Items.Add(StatusSeparator());
            _statusBar.Items.Add(_hotReloadStatus);
        }
    }

    private static void PaintBottomBorder(Control control)
    {
        control.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, control.Height - 1, control.Width, control.Height - 1);
        };
    }

    private static void PaintLeftBorder(Control control)
    {
        control.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, control.Height);
        };
    }

    private static void PaintRightBorder(Control control)
    {
        control.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, control.Width - 1, 0, control.Width - 1, control.Height);
        };
    }

    private void BuildInspectorPages(Control inspector)
    {
        _basicInspectorPage.Dock = DockStyle.Fill;
        _basicInspectorPage.BackColor = Theme.Panel;
        _basicInspectorPage.AutoScroll = true;
        _objectInspector.Dock = DockStyle.Top;
        _objectInspector.Height = 224;
        _objectInspector.BackColor = Theme.Panel;
        BuildInspector(_objectInspector);
        _drawSettingsPanel.Dock = DockStyle.Top;
        _drawSettingsPanel.Height = 304;
        _basicInspectorPage.Controls.Add(_drawSettingsPanel);
        _basicInspectorPage.Controls.Add(_objectInspector);

        _animationPage.Dock = DockStyle.Fill;
        _animationPage.BackColor = Theme.Panel;
        _playbackSettings.Dock = DockStyle.Top;
        _playbackSettings.Height = 188;
        _animationPage.Controls.Add(_playbackSettings);

        _materialEditor.Dock = DockStyle.Fill;
        _hierarchyPanel.Dock = DockStyle.Fill;
        _sceneEditorPanel.Dock = DockStyle.Fill;

        inspector.Controls.Add(_hierarchyPanel);
        inspector.Controls.Add(_materialEditor);
        inspector.Controls.Add(_animationPage);
        inspector.Controls.Add(_sceneEditorPanel);
        inspector.Controls.Add(_basicInspectorPage);
        ShowWorkspace(WorkspaceView.BasicDrawing);
    }

    private void BuildInspector(Control parent)
    {
        parent.Padding = new Padding(0, 4, 0, 8);

        var title = new Label
        {
            Text = "Inspector",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        parent.Controls.Add(title);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < content.RowCount; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, i <= 3 ? 27 : 33));
        parent.Controls.Add(content);
        content.BringToFront();

        var row = 0;
        foreach (var label in new[] { _selected, _selectedLayer, _selectedAtoms, _objectMetric })
        {
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 0, 0, 3);
            content.Controls.Add(label, 0, row);
            content.SetColumnSpan(label, 2);
            row++;
        }

        _color.Items.AddRange(["Teal", "Amber", "Coral", "Violet", "White"]);
        _color.SelectedIndex = 0;
        _color.Dock = DockStyle.Fill;
        _color.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleComboBox(_color);
        AddField(content, "Active color", _color, 4);

        _stroke.Dock = DockStyle.Fill;
        _stroke.Margin = new Padding(0, 3, 0, 3);
        Theme.StyleNumeric(_stroke);
        AddField(content, "Stroke pt", _stroke, 5);
    }

    private static void AddField(TableLayoutPanel parent, string label, Control input, int row)
    {
        parent.Controls.Add(FieldLabel(label), 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 3);
        input.MinimumSize = new Size(120, 28);
        parent.Controls.Add(input, 1, row);
    }

    private void BuildDrawingObjectTabs()
    {
        _drawingObjectTabs.Controls.Clear();
        _drawingObjectTabButtons.Clear();
        for (var i = 0; i < _drawingObjects.Count; i++) AddDrawingObjectTab(i);
        AddNewDrawingObjectButton();
        RefreshToolButtons();
    }

    private void AddDrawingObjectTab(int index)
    {
        var drawingObject = _drawingObjects[index];
        var button = new Button
        {
            Text = drawingObject.Name,
            Width = drawingObject.Name.Length > 10 ? 132 : 108,
            Height = 30,
            Margin = new Padding(0, 0, 6, 0),
            Tag = index,
            AutoEllipsis = true
        };
        Theme.StyleButton(button);
        button.Click += (_, _) => SelectDrawingObject(index);
        button.MouseEnter += (_, _) => _toolTip.ShowFor(button, $"{drawingObject.Name} ({drawingObject.Kind})");
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        var dragStart = Point.Empty;
        button.MouseDown += (_, e) => dragStart = e.Location;
        button.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || drawingObject.Kind == "Scene") return;
            if (Math.Abs(e.X - dragStart.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height / 2) return;
            button.DoDragDrop(drawingObject.ToVaultItem(), DragDropEffects.Copy);
        };
        _drawingObjectTabButtons[drawingObject.Id] = button;
        _drawingObjectTabs.Controls.Add(button);
    }

    private void AddNewDrawingObjectButton()
    {
        var button = new Button
        {
            Text = "+ Object",
            Width = 86,
            Height = 30,
            Margin = new Padding(4, 0, 6, 0),
            AutoEllipsis = true
        };
        Theme.StyleButton(button);
        button.Click += (_, _) =>
        {
            var index = _drawingObjects.Count;
            _drawingObjects.Add(new DrawingObjectDefinition
            {
                Name = $"Drawing Object {index:000}",
                Detail = "Reusable drawing object tab"
            });
            SelectDrawingObject(index);
            BuildDrawingObjectTabs();
        };
        _drawingObjectTabs.Controls.Add(button);
    }

    private void CreateDefaultDrawingObjects()
    {
        _drawingObjects.Clear();
        _drawingObjects.Add(new DrawingObjectDefinition
        {
            Name = "Scene",
            Kind = "Scene",
            Detail = "Master scene editing context"
        });
        _drawingObjects.Add(new DrawingObjectDefinition
        {
            Name = "Drawing Object 001",
            Detail = "Reusable Flash-style drawing object"
        });
        _drawingObjects.Add(new DrawingObjectDefinition
        {
            Name = "Drawing Object 002",
            Detail = "Reusable Flash-style drawing object"
        });
        _activeDrawingObjectIndex = 0;
    }

    private DrawingObjectDefinition? ActiveDrawingObject()
    {
        return _activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjects.Count
            ? _drawingObjects[_activeDrawingObjectIndex]
            : null;
    }

    private void SelectDrawingObject(int index)
    {
        if (index < 0 || index >= _drawingObjects.Count) return;
        _activeDrawingObjectIndex = index;
        _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
        RefreshToolButtons();
        AppLog.Info($"Selected drawing object tab: {_drawingObjects[index].Name}");
    }

    private void AddTool(FlowLayoutPanel panel, SvgIconKind icon, ToolMode tool, string displayName)
    {
        var button = new SvgIconButton(icon) { Margin = new Padding(0, 0, 0, 8), Tag = tool, AccessibleName = displayName };
        Theme.StyleButton(button);
        button.MouseEnter += (_, _) => _toolTip.ShowFor(button, displayName);
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        button.Click += (_, _) => ActivateTool(tool);
        _toolButtons[tool] = button;
        panel.Controls.Add(button);
    }

    private void ActivateTool(ToolMode tool)
    {
        _tool = tool;
        _stage.ClearDrawingPreview();
        if (ToolShapeKind(tool) is { } shape)
        {
            _drawSettings.ShapeKind = shape;
            _drawSettings.NotifyChanged();
        }

        RefreshToolButtons();
    }

    private void HookEvents()
    {
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _timeline.CurrentFrameChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            SetFrame(_timeline.CurrentFrame);
        };
        _playbackSettings.FpsChanged += (_, _) =>
        {
            _playbackAccumulator = 0;
            UpdateStatusBar();
        };
        _playbackSettings.FrameRangeChanged += (_, _) =>
        {
            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
            SyncFrameSliderRange();
            SetFrame(_frame);
        };
        _materialEditor.MaterialChanged += (_, e) =>
        {
            _updatingStrokeInput = true;
            try
            {
                _stroke.Value = (decimal)Math.Clamp(e.StrokeWidth, (float)_stroke.Minimum, (float)_stroke.Maximum);
            }
            finally
            {
                _updatingStrokeInput = false;
            }

            if (_selectedObject >= 0 && _selectedObject < _scene.ObjectCount)
            {
                var strokeUnits = VectorUnits.StrokePointsToUnits(e.StrokeWidth);
                _scene.Argb[_selectedObject] = Color.FromArgb((int)Math.Clamp(e.Opacity * 255, 0, 255), e.Fill).ToArgb();
                _scene.Stroke[_selectedObject] = strokeUnits;
                _scene.StrokeArgb[_selectedObject] = e.Stroke.ToArgb();
                if (_scene.ShapeKind[_selectedObject] == ShapeKind.Line)
                {
                    _scene.Height[_selectedObject] = Math.Max(VectorUnits.FromPixels(3), strokeUnits + VectorUnits.FromPixels(2));
                    _scene.RebuildGeometryIndex();
                }

                _stage.Invalidate();
            }
        };
        _hierarchyPanel.HierarchySelectionChanged += (_, e) =>
        {
            if (e.Kind == HierarchyNodeKind.Layer && e.Index >= 0 && e.Index < _scene.LayerCount)
            {
                _scene.ActiveLayer = e.Index;
                if (e.Index < _layers.Items.Count) _layers.SelectedIndex = e.Index;
                UpdateInspector();
            }
            else if (e.Kind == HierarchyNodeKind.Object && e.Index >= 0 && e.Index < _scene.ObjectCount)
            {
                SetSelection(e.Index);
                UpdateInspector();
                _stage.Invalidate();
            }
        };
        _drawSettings.Changed += (_, _) =>
        {
            if (_drawSettings.ShapeKind == _lastSettingsShape) return;
            _lastSettingsShape = _drawSettings.ShapeKind;
            if (ToolModeForShape(_drawSettings.ShapeKind) is { } tool)
            {
                _tool = tool;
                RefreshToolButtons();
            }
        };

        _play.Click += (_, _) =>
        {
            _playing = !_playing;
            if (_playing) _playbackAccumulator = 0;
            _play.Text = _playing ? "Pause" : "Play";
        };
        _frameSlider.ValueChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            SetFrame(_frameSlider.Value);
        };
        _layers.SelectedIndexChanged += (_, _) =>
        {
            if (_layers.SelectedIndex >= 0 && _layers.SelectedIndex < _scene.LayerCount) _scene.ActiveLayer = _layers.SelectedIndex;
            UpdateInspector();
        };
        _layers.DoubleClick += (_, _) =>
        {
            if (_layers.SelectedIndex >= 0 && _layers.SelectedIndex < _scene.LayerCount)
            {
                _scene.ToggleLayer(_layers.SelectedIndex);
                RefreshLayers();
                _stage.Invalidate();
            }
        };
        _color.SelectedIndexChanged += (_, _) => _materialEditor.Fill = PaletteColor(_color.SelectedIndex);
        _stroke.ValueChanged += (_, _) =>
        {
            if (_updatingStrokeInput) return;
            _materialEditor.StrokeWidth = (float)_stroke.Value;
        };
        _stage.MouseWheel += (_, e) =>
        {
            _stage.ZoomAt(e.Location, e.Delta > 0 ? 1.12f : 0.89f);
            UpdateStatusBar();
        };
        _stage.MouseDown += StageMouseDown;
        _stage.MouseMove += StageMouseMove;
        _stage.MouseUp += StageMouseUp;
    }

    private void Generate()
    {
        AppLog.Info("Generating stress scene");
        Cursor = Cursors.WaitCursor;
        try
        {
            _scene.Generate(1000, 100000, 100000000);
            _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
            SyncFrameSliderRange();
            SetFrame(0);
            ClearSelection();
            RefreshLayers();
            _hierarchyPanel.BindScene(_scene);
            _sceneEditorPanel.BindScene(_scene);
            _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
            _stage.ResetDefaultView();
            UpdateInspector();
            UpdateStatusBar();
            AppLog.Info($"Stress scene generated. Layers: {_scene.LayerCount}, Objects: {_scene.ObjectCount}, Atoms: {_scene.VirtualAtomCount}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to generate stress scene", ex);
            throw;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void CreateNewProject()
    {
        AppLog.Info("Creating new empty project");
        _scene.CreateEmpty();
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
        SyncFrameSliderRange();
        SetFrame(0);
        ClearSelection();
        RefreshLayers();
        _hierarchyPanel.BindScene(_scene);
        _sceneEditorPanel.BindScene(_scene);
        _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
        _stage.ResetDefaultView();
        UpdateInspector();
        UpdateStatusBar();
        AppLog.Info("New empty project created");
    }

    private void RefreshLayers()
    {
        _layers.BeginUpdate();
        _layers.Items.Clear();
        var limit = Math.Min(300, _scene.LayerCount);
        for (var i = 0; i < limit; i++)
        {
            _layers.Items.Add($"{(_scene.LayerVisible[i] ? "●" : "○")} {_scene.LayerNames[i]}  {_scene.LayerOpacity[i]:P0}");
        }
        if (_scene.LayerCount > limit) _layers.Items.Add($"+ {_scene.LayerCount - limit} virtualized layers");
        if (_scene.ActiveLayer < limit) _layers.SelectedIndex = _scene.ActiveLayer;
        _layers.EndUpdate();
    }

    private void Tick()
    {
        var elapsed = Math.Clamp(_clock.Elapsed.TotalSeconds, 0.001, MaxFrameSeconds);
        _clock.Restart();

        _updateAccumulator += elapsed;
        _renderAccumulator += elapsed;
        _timeSinceLastRender += elapsed;
        _upsSampleTime += elapsed;

        var updateCount = 0;
        while (_updateAccumulator >= UpdateStepSeconds)
        {
            UpdateSimulation(UpdateStepSeconds);
            _updateAccumulator -= UpdateStepSeconds;
            updateCount++;
        }

        if (updateCount > 0)
        {
            _updatesThisSample += updateCount;
            if (_upsSampleTime >= 0.25)
            {
                var instantUps = _updatesThisSample / _upsSampleTime;
                _smoothedUps = _smoothedUps <= 0 ? instantUps : _smoothedUps * 0.75 + instantUps * 0.25;
                _updatesThisSample = 0;
                _upsSampleTime = 0;
            }
        }

        if (_renderAccumulator >= RenderStepSeconds || _renderRequested)
        {
            var instantRenderFps = _timeSinceLastRender > 0 ? 1.0 / _timeSinceLastRender : TargetRenderFps;
            _smoothedFps = _smoothedFps <= 0 ? instantRenderFps : _smoothedFps * 0.85 + instantRenderFps * 0.15;
            _renderAccumulator %= RenderStepSeconds;
            _timeSinceLastRender = 0;
            _renderRequested = false;

            var stats = _stage.LastStats;
            _fps.Text = $"FPS {_smoothedFps:0}";
            _draw.Text = stats.TileLod
                ? $"Tiles {CompactFormat.Number(stats.TileDraws)}"
                : $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)}";
            _atoms.Text = $"Atoms {CompactFormat.Number(stats.VisibleAtoms)} / {CompactFormat.Number(_scene.VirtualAtomCount)}";
            _zoom.Text = $"Zoom {_stage.Zoom * 100:0}%";
            UpdateStatusBar();
            _stage.Invalidate();
        }
    }

    private void UpdateSimulation(double deltaSeconds)
    {
        if (!_playing) return;

        _playbackAccumulator += deltaSeconds;
        var frameStep = 1.0 / Math.Max(1, _playbackSettings.Fps);
        while (_playbackAccumulator >= frameStep)
        {
            _playbackAccumulator -= frameStep;
            var next = _frame + 1;
            if (next > _playbackSettings.EndFrame)
            {
                if (_playbackSettings.LoopPlayback) next = _playbackSettings.StartFrame;
                else
                {
                    next = _playbackSettings.EndFrame;
                    _playing = false;
                    _play.Text = "Play";
                    _playbackAccumulator = 0;
                }
            }

            SetFrame(next, invalidate: false);
            _renderRequested = true;
            if (!_playing) break;
        }
    }

    private void UpdateStatusBar()
    {
        _renderFpsStatus.Text = $"Render FPS {_smoothedFps:0}";
        _animationFpsStatus.Text = $"UPS {_smoothedUps:0}/{TargetUps:0}  Animation FPS {_playbackSettings.Fps}";
        _zoomStatus.Text = $"Zoom {_stage.Zoom * 100:0}%";
    }

    private void SetFrame(int frame, bool invalidate = true)
    {
        var next = Math.Clamp(frame, _playbackSettings.StartFrame, _playbackSettings.EndFrame);
        _syncingFrame = true;
        try
        {
            _frame = next;
            if (_frameSlider.Minimum <= next && next <= _frameSlider.Maximum) _frameSlider.Value = next;
            _timeline.CurrentFrame = next;
            _stage.Frame = next;
        }
        finally
        {
            _syncingFrame = false;
        }

        if (invalidate) _stage.Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Delete && !ContainsFocusedEditor(this) && DeleteSelectedObject()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SyncFrameSliderRange()
    {
        _syncingFrame = true;
        try
        {
            _frameSlider.Minimum = _playbackSettings.StartFrame;
            _frameSlider.Maximum = Math.Max(_playbackSettings.StartFrame, _playbackSettings.EndFrame);
            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
        }
        finally
        {
            _syncingFrame = false;
        }
    }

    private void StageMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
        {
            BeginGlobalViewDrag(e);
            return;
        }

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        if (_tool == ToolMode.Select)
        {
            if (_selectedObject >= 0)
            {
                _activeHandle = _stage.HitTestHandle(e.Location, _selectedObject);
                if (_activeHandle != EditHandleKind.None)
                {
                    CaptureEditStart(_selectedObject);
                    UpdateInspector();
                    _stage.Invalidate();
                    return;
                }
            }

            var hit = _scene.HitTestElement(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (hit.IsValid)
            {
                var hitObject = hit.Key.ObjectIndex;
                if (!_selectedObjects.Contains(hitObject)) SetSelection(hit);
                else
                {
                    _selectedObject = hitObject;
                    _selectedElement = hit;
                    _stage.SetSelection(_selectedObjects, _selectedObject);
                    _stage.SetSelectedElement(_selectedElement);
                }

                CaptureEditStart(hitObject);
            }
            else
            {
                ClearSelection();
                _marqueeSelecting = true;
                _marqueeStart = e.Location;
                _stage.SetMarquee(e.Location, e.Location);
            }

            UpdateInspector();
            _stage.Invalidate();
        }
        else if (_tool == ToolMode.Fill)
        {
            var hit = _scene.HitTestElement(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (hit.IsValid)
            {
                var hitObject = hit.Key.ObjectIndex;
                _scene.Argb[hitObject] = ActiveColor().ToArgb();
                SetSelection(hit);
                UpdateInspector();
                _stage.Invalidate();
            }
        }
    }

    private void StageMouseMove(object? sender, MouseEventArgs e)
    {
        if (_lastMouse is null) return;
        var dx = e.X - _lastMouse.Value.X;
        var dy = e.Y - _lastMouse.Value.Y;
        _lastMouse = e.Location;

        if (_viewPanning)
        {
            _stage.Pan(dx, dy);
            UpdateStatusBar();
            return;
        }

        if (_viewZooming)
        {
            var factor = Math.Clamp(Math.Exp(-dy * 0.01), 0.2, 5.0);
            _stage.ZoomAt(e.Location, (float)factor);
            UpdateStatusBar();
            return;
        }

        if (_tool == ToolMode.Hand)
        {
            _stage.Pan(dx, dy);
            UpdateStatusBar();
        }
        else if (_tool == ToolMode.Select && _marqueeSelecting && _marqueeStart is not null && e.Button == MouseButtons.Left)
        {
            _stage.SetMarquee(_marqueeStart.Value, e.Location);
        }
        else if (_tool == ToolMode.Select && _selectedObject >= 0 && _startWorld is not null && _selectedStart is not null && e.Button == MouseButtons.Left)
        {
            var world = _stage.ScreenToWorld(e.Location);
            if (_activeHandle != EditHandleKind.None)
            {
                ApplyHandleDrag(world);
            }
            else
            {
                EnsureSelectedElementDetachedForMove();
                var dxWorld = world.X - _startWorld.Value.X;
                var dyWorld = world.Y - _startWorld.Value.Y;
                if (_selectedMoveStarts.Count > 1)
                {
                    foreach (var item in _selectedMoveStarts)
                    {
                        var index = item.Key;
                        if ((uint)index >= _scene.ObjectCount) continue;
                        _scene.X[index] = VectorUnits.Quantize(item.Value.X + dxWorld);
                        _scene.Y[index] = VectorUnits.Quantize(item.Value.Y + dyWorld);
                        if (_selectedCurveStarts.TryGetValue(index, out var curveStart))
                        {
                            _scene.CurveControlX[index] = VectorUnits.Quantize(curveStart.X + dxWorld);
                            _scene.CurveControlY[index] = VectorUnits.Quantize(curveStart.Y + dyWorld);
                        }
                    }
                }
                else
                {
                    _scene.X[_selectedObject] = VectorUnits.Quantize(_selectedStart.Value.X + dxWorld);
                    _scene.Y[_selectedObject] = VectorUnits.Quantize(_selectedStart.Value.Y + dyWorld);
                    if (_curveControlStart is not null)
                    {
                        _scene.CurveControlX[_selectedObject] = VectorUnits.Quantize(_curveControlStart.Value.X + dxWorld);
                        _scene.CurveControlY[_selectedObject] = VectorUnits.Quantize(_curveControlStart.Value.Y + dyWorld);
                    }
                }
            }

            _geometryDirty = true;
            _stage.Invalidate();
            UpdateInspector();
        }
        else if (IsDrawingTool(_tool) && _startWorld is not null && e.Button == MouseButtons.Left)
        {
            UpdateDrawingPreview(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }
    }

    private void EnsureSelectedElementDetachedForMove()
    {
        if (_detachedSelectionForMove) return;
        _detachedSelectionForMove = true;
        if (!_selectedElement.IsValid || _selectedElement.Key.ObjectIndex != _selectedObject) return;
        var detached = _scene.DetachElementForMove(_selectedElement, _frame);
        if (!detached.IsValid) return;
        if (detached.Distance >= 0 && detached.Key.ObjectIndex == _selectedObject && detached.Key.Kind == _selectedElement.Key.Kind && detached.Key.PartIndex == _selectedElement.Key.PartIndex) return;

        SetSelection(detached);
        CaptureEditStart(detached.Key.ObjectIndex);
        _geometryDirty = true;
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
    }

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (_viewPanning || _viewZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        if (_marqueeSelecting)
        {
            CompleteMarqueeSelection(e.Location);
            _stage.ClearDrawingPreview();
            FinishPointerInteraction();
            return;
        }

        if (IsDrawingTool(_tool) && _startWorld is not null && _startScreen is not null)
        {
            var dx = e.X - _startScreen.Value.X;
            var dy = e.Y - _startScreen.Value.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) AddDrawnObject(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }

        _stage.ClearDrawingPreview();
        FinishPointerInteraction();
    }

    private void FinishPointerInteraction()
    {
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _detachedSelectionForMove = false;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        if (_geometryDirty)
        {
            _scene.RebuildGeometryIndex();
            _geometryDirty = false;
        }

        _stage.Capture = false;
    }

    private void CompleteMarqueeSelection(Point endScreen)
    {
        var startScreen = _marqueeStart ?? endScreen;
        var dx = endScreen.X - startScreen.X;
        var dy = endScreen.Y - startScreen.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 6)
        {
            var a = _stage.ScreenToWorld(startScreen);
            var b = _stage.ScreenToWorld(endScreen);
            var bounds = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            SetSelection(_scene.QueryObjects(bounds, _frame));
        }
        else
        {
            ClearSelection();
        }

        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool DeleteSelectedObject()
    {
        var targets = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (targets.Length == 0 && _selectedObject >= 0 && _selectedObject < _scene.ObjectCount) targets = new[] { _selectedObject };
        if (targets.Length == 0) return false;
        if (_scene.RemoveObjects(targets) <= 0) return false;

        ClearSelection();
        _geometryDirty = false;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private void BeginGlobalViewDrag(MouseEventArgs e)
    {
        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _viewZooming = (ModifierKeys & Keys.Control) == Keys.Control;
        _viewPanning = !_viewZooming;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _stage.Cursor = _viewZooming ? Cursors.SizeNS : Cursors.SizeAll;
    }

    private void EndGlobalViewDrag()
    {
        _lastMouse = null;
        _viewPanning = false;
        _viewZooming = false;
        _stage.Cursor = Cursors.Default;
        _stage.Capture = false;
    }

    private void SetSelection(int objectIndex)
    {
        _selectedObjects.Clear();
        _selectedObject = objectIndex >= 0 && objectIndex < _scene.ObjectCount ? objectIndex : -1;
        _selectedElement = DrawingElementHit.None;
        if (_selectedObject >= 0) _selectedObjects.Add(_selectedObject);
        _stage.SetSelection(_selectedObjects, _selectedObject);
        _stage.SetSelectedElement(_selectedElement);
    }

    private void SetSelection(DrawingElementHit hit)
    {
        if (!hit.IsValid)
        {
            ClearSelection();
            return;
        }

        _selectedObjects.Clear();
        _selectedObject = hit.Key.ObjectIndex;
        _selectedElement = hit;
        _selectedObjects.Add(_selectedObject);
        _stage.SetSelection(_selectedObjects, _selectedObject);
        _stage.SetSelectedElement(_selectedElement);
    }

    private void SetSelection(IEnumerable<int> objectIndices)
    {
        _selectedObjects.Clear();
        _selectedElement = DrawingElementHit.None;
        var seen = new HashSet<int>();
        foreach (var index in objectIndices)
        {
            if (index < 0 || index >= _scene.ObjectCount || !seen.Add(index)) continue;
            _selectedObjects.Add(index);
        }

        _selectedObject = _selectedObjects.Count > 0 ? _selectedObjects[_selectedObjects.Count - 1] : -1;
        _stage.SetSelection(_selectedObjects, _selectedObject);
        _stage.SetSelectedElement(_selectedElement);
    }

    private void ClearSelection() => SetSelection(-1);

    private void UpdateDrawingPreview(PointF start, PointF end, ToolMode tool)
    {
        start = VectorUnits.Quantize(_drawSettings.SnapPoint(start));
        end = VectorUnits.Quantize(_drawSettings.SnapPoint(end));
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool != ToolMode.Line)
        {
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            end = new PointF(start.X + size.Width, start.Y + size.Height);
        }

        _stage.SetDrawingPreview(start, end, shape, ActiveColor(), ActiveStrokeUnits());
    }

    private float SelectionToleranceWorld() => Math.Max(4, _stage.ScreenLengthToWorld(10));

    private void CaptureEditStart(int objectIndex)
    {
        _selectedStart = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
        _curveControlStart = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        _resizeStartCenter = _selectedStart;
        _resizeStartSize = new SizeF(_scene.Width[objectIndex], _scene.Height[objectIndex]);
        _resizeStartAngle = _scene.Angle[objectIndex];
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        foreach (var index in _selectedObjects)
        {
            if ((uint)index >= _scene.ObjectCount) continue;
            _selectedMoveStarts[index] = new PointF(_scene.X[index], _scene.Y[index]);
            _selectedCurveStarts[index] = new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]);
        }

        if (!_selectedMoveStarts.ContainsKey(objectIndex))
        {
            _selectedMoveStarts[objectIndex] = _selectedStart.Value;
            _selectedCurveStarts[objectIndex] = _curveControlStart.Value;
        }
    }

    private void ApplyHandleDrag(PointF world)
    {
        if (_selectedObject < 0 || _resizeStartCenter is null || _resizeStartSize is null) return;
        if (_activeHandle == EditHandleKind.BezierControl)
        {
            var snapped = VectorUnits.Quantize(_drawSettings.SnapPoint(world));
            _scene.CurveControlX[_selectedObject] = snapped.X;
            _scene.CurveControlY[_selectedObject] = snapped.Y;
            return;
        }

        var draggedLocal = WorldToLocalFromEditStart(world);
        var anchor = OppositeCorner(_activeHandle, _resizeStartSize.Value);
        var minSize = VectorUnits.FromPixels(4);
        var width = Math.Max(minSize, Math.Abs(draggedLocal.X - anchor.X));
        var height = Math.Max(minSize, Math.Abs(draggedLocal.Y - anchor.Y));
        var centerLocal = new PointF((draggedLocal.X + anchor.X) * 0.5f, (draggedLocal.Y + anchor.Y) * 0.5f);
        var centerWorld = LocalToWorldFromEditStart(centerLocal);

        _scene.X[_selectedObject] = VectorUnits.Quantize(centerWorld.X);
        _scene.Y[_selectedObject] = VectorUnits.Quantize(centerWorld.Y);
        _scene.Width[_selectedObject] = Math.Max(1, VectorUnits.Quantize(width));
        _scene.Height[_selectedObject] = Math.Max(1, VectorUnits.Quantize(height));
    }

    private PointF WorldToLocalFromEditStart(PointF world)
    {
        var center = _resizeStartCenter!.Value;
        var dx = world.X - center.X;
        var dy = world.Y - center.Y;
        var cos = MathF.Cos(_resizeStartAngle);
        var sin = MathF.Sin(_resizeStartAngle);
        return new PointF(dx * cos + dy * sin, -dx * sin + dy * cos);
    }

    private PointF LocalToWorldFromEditStart(PointF local)
    {
        var center = _resizeStartCenter!.Value;
        var cos = MathF.Cos(_resizeStartAngle);
        var sin = MathF.Sin(_resizeStartAngle);
        return new PointF(center.X + local.X * cos - local.Y * sin, center.Y + local.X * sin + local.Y * cos);
    }

    private static PointF OppositeCorner(EditHandleKind handle, SizeF size)
    {
        var halfW = size.Width * 0.5f;
        var halfH = size.Height * 0.5f;
        return handle switch
        {
            EditHandleKind.BoundsTopLeft => new PointF(halfW, halfH),
            EditHandleKind.BoundsTopRight => new PointF(-halfW, halfH),
            EditHandleKind.BoundsBottomRight => new PointF(-halfW, -halfH),
            EditHandleKind.BoundsBottomLeft => new PointF(halfW, -halfH),
            _ => PointF.Empty
        };
    }

    private void AddDrawnObject(PointF start, PointF end, ToolMode tool)
    {
        start = VectorUnits.Quantize(_drawSettings.SnapPoint(start));
        end = VectorUnits.Quantize(_drawSettings.SnapPoint(end));
        var center = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        float width;
        float height;
        float angle;
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool == ToolMode.Line)
        {
            width = Math.Max(VectorUnits.FromPixels(4), Distance(start, end));
            height = Math.Max(VectorUnits.FromPixels(3), ActiveStrokeUnits() + VectorUnits.FromPixels(2));
            angle = _drawSettings.SnapAngle(MathF.Atan2(end.Y - start.Y, end.X - start.X));
            shape = ShapeKind.Line;
        }
        else
        {
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            width = Math.Max(VectorUnits.FromPixels(4), Math.Abs(size.Width));
            height = Math.Max(VectorUnits.FromPixels(4), Math.Abs(size.Height));
            angle = 0;
        }

        SetSelection(_scene.AddObject(_scene.ActiveLayer, center, new SizeF(width, height), angle, ActiveStrokeUnits(), ActiveColor(), ActiveStrokeColor(), tool == ToolMode.Line ? 6u : 24u, shape));
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void UpdateInspector()
    {
        _sceneEditorPanel.RefreshSceneStats();
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        var validSelection = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (validSelection.Length > 1)
        {
            var firstLayer = _scene.ObjectLayer[validSelection[0]];
            var mixedLayer = false;
            long atoms = 0;
            foreach (var index in validSelection)
            {
                atoms += _scene.AtomCount[index];
                if (_scene.ObjectLayer[index] != firstLayer) mixedLayer = true;
            }

            _selected.Text = $"Selected: {CompactFormat.Number(validSelection.Length)} objects";
            _selectedLayer.Text = mixedLayer ? "Layer: Mixed" : $"Layer: {_scene.LayerNames[firstLayer]}";
            _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(atoms)}";
            return;
        }

        if (validSelection.Length == 1 && _selectedObject != validSelection[0]) _selectedObject = validSelection[0];
        if (_selectedObject < 0 || _selectedObject >= _scene.ObjectCount)
        {
            _selected.Text = "Selected: None";
            _selectedLayer.Text = _scene.LayerNames.Length > 0 ? $"Layer: {_scene.LayerNames[_scene.ActiveLayer]}" : "Layer: -";
            _selectedAtoms.Text = "Atoms: -";
            return;
        }

        var layer = _scene.ObjectLayer[_selectedObject];
        _selected.Text = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == _selectedObject
            ? $"Selected: #{_selectedObject} {_selectedElement.Key.Kind} part {_selectedElement.Key.PartIndex}"
            : $"Selected: #{_selectedObject}";
        _selectedLayer.Text = $"Layer: {_scene.LayerNames[layer]}";
        _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(_scene.AtomCount[_selectedObject])}";
        var fill = Color.FromArgb(_scene.Argb[_selectedObject]);
        var stroke = _scene.StrokeArgb.Length > _selectedObject ? Color.FromArgb(_scene.StrokeArgb[_selectedObject]) : ActiveStrokeColor();
        var strokePoints = VectorUnits.UnitsToStrokePoints(_scene.Stroke[_selectedObject]);
        _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), stroke, strokePoints, fill.A / 255f);
        _updatingStrokeInput = true;
        try
        {
            _stroke.Value = (decimal)Math.Clamp(strokePoints, (float)_stroke.Minimum, (float)_stroke.Maximum);
        }
        finally
        {
            _updatingStrokeInput = false;
        }
    }

    private Color ActiveColor()
    {
        return Color.FromArgb((int)Math.Clamp(_materialEditor.Opacity * 255, 0, 255), _materialEditor.Fill);
    }

    private float ActiveStrokeUnits() => VectorUnits.StrokePointsToUnits((float)_materialEditor.StrokeWidth);

    private Color ActiveStrokeColor() => _materialEditor.Stroke;

    private static Color PaletteColor(int selectedIndex)
    {
        return selectedIndex switch
        {
            1 => Color.FromArgb(213, 151, 74),
            2 => Color.FromArgb(200, 107, 99),
            3 => Color.FromArgb(136, 122, 214),
            4 => Color.FromArgb(238, 242, 241),
            _ => Color.FromArgb(79, 179, 162)
        };
    }

    private void ShowWorkspace(WorkspaceView view)
    {
        var basicDrawing = view == WorkspaceView.BasicDrawing;
        _workspaceHeader.Height = basicDrawing ? 84 : 44;
        _drawingObjectTabs.Visible = basicDrawing;
        _basicInspectorPage.Visible = basicDrawing;
        _sceneEditorPanel.Visible = view == WorkspaceView.SceneEditor;
        _animationPage.Visible = view == WorkspaceView.Animation;
        _materialEditor.Visible = view == WorkspaceView.Materials;
        _hierarchyPanel.Visible = view == WorkspaceView.Hierarchy;
        _timeline.Visible = view != WorkspaceView.BasicDrawing;
    }

    private static bool IsDrawingTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line;
    }

    private static ShapeKind? ToolShapeKind(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Rectangle => ShapeKind.Rectangle,
            ToolMode.Ellipse => ShapeKind.Ellipse,
            ToolMode.Triangle => ShapeKind.Triangle,
            ToolMode.Polygon => ShapeKind.Polygon,
            ToolMode.Star => ShapeKind.Star,
            ToolMode.Line => ShapeKind.Line,
            _ => null
        };
    }

    private static ToolMode? ToolModeForShape(ShapeKind shape)
    {
        return shape switch
        {
            ShapeKind.Rectangle => ToolMode.Rectangle,
            ShapeKind.Ellipse => ToolMode.Ellipse,
            ShapeKind.Triangle => ToolMode.Triangle,
            ShapeKind.Polygon => ToolMode.Polygon,
            ShapeKind.Star => ToolMode.Star,
            ShapeKind.Line => ToolMode.Line,
            _ => null
        };
    }

    private void RefreshToolButtons()
    {
        foreach (var (tool, button) in _toolButtons)
        {
            if (tool == _tool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
        }

        var activeDrawingObject = ActiveDrawingObject();
        foreach (var (id, button) in _drawingObjectTabButtons)
        {
            if (activeDrawingObject is not null && id == activeDrawingObject.Id) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
        }
    }

    private static Label MetricLabel(string text, int width) => new() { Text = text, Left = 8, Top = 10, Width = width, Height = 22, ForeColor = Theme.Muted, BackColor = Theme.Top, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static ToolStripStatusLabel StatusLabel(string text) => new() { Text = text, ForeColor = Theme.Muted, Spring = false, Margin = new Padding(0, 0, 10, 0) };
    private static ToolStripStatusLabel StatusSeparator() => new() { Text = "|", ForeColor = Theme.Border, Margin = new Padding(0, 0, 10, 0) };
    private static bool IsHotReloadEnabled() => Environment.GetEnvironmentVariable("V2D_DEV_HOT_RELOAD") == "1";
    private static Label InspectorLabel(string text) => new() { Text = text, Height = 26, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Muted,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(0, 3, 8, 3)
    };
    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool ContainsFocusedEditor(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is TextBoxBase or NumericUpDown or ComboBox) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedEditor(child)) return true;
        }

        return false;
    }
}
