using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed class MainForm : Form
{
    private readonly VectorScene _scene = new();
    private readonly StageControl _stage;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
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
    private readonly TextBox _layerInput = InputBox("1000");
    private readonly TextBox _objectInput = InputBox("100000");
    private readonly TextBox _atomInput = InputBox("100000000");
    private readonly ComboBox _color = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _stroke = new() { Minimum = 0, Maximum = 12, Value = 2, Width = 160 };
    private readonly Button _play = new() { Text = "Play", Width = 72 };
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();
    private readonly AnimatedToolTip _toolTip = new();
    private readonly WorkspaceTabs _workspaceTabs = new();
    private readonly TimelineStrip _timeline;
    private readonly PlaybackSettingsPanel _playbackSettings = new();
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly LibraryVaultPanel _libraryVaultPanel = new();
    private readonly Panel _basicInspectorPage = new();
    private readonly Panel _objectInspector = new();
    private readonly Panel _animationPage = new();
    private ToolMode _tool = ToolMode.Select;
    private bool _playing;
    private int _frame;
    private int _selectedObject = -1;
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
    private bool _syncingFrame;
    private ShapeKind _lastSettingsShape = ShapeKind.Rectangle;

    public MainForm()
    {
        Text = "Vector 2D Animation Engine";
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(1120, 720);
        BackColor = Theme.App;
        Font = Theme.UiFont();

        _stage = new StageControl(_scene) { Dock = DockStyle.Fill };
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = 192 };
        _drawSettingsPanel = new DrawSettingsPanel(_drawSettings);
        BuildUi();
        HookEvents();
        CreateNewProject();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Top };
        Controls.Add(top);
        top.Controls.Add(new Label { Text = "V2", Left = 12, Top = 10, Width = 34, Height = 30, ForeColor = Theme.Accent, BackColor = Theme.Top, Font = Theme.UiFont(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        top.Controls.Add(Theme.Label("Vector 2D Animation Engine", 56, 14, 260, Theme.Text, Theme.UiFont(10, FontStyle.Bold)));
        _play.Left = 330;
        _play.Top = 10;
        _play.Height = 32;
        Theme.StyleButton(_play);
        top.Controls.Add(_play);
        top.Controls.Add(Theme.Label("Frame", 420, 14, 58, Theme.Muted));
        _frameSlider.Left = 476;
        _frameSlider.Top = 8;
        _frameSlider.Width = 430;
        _frameSlider.Minimum = 0;
        _frameSlider.Maximum = 239;
        _frameSlider.TickFrequency = 24;
        top.Controls.Add(_frameSlider);
        var generate = new Button { Text = "Run Stress Scene", Width = 150, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        generate.Left = Width - 282;
        generate.Top = 10;
        Theme.StyleButton(generate);
        generate.Click += (_, _) => Generate();
        var fit = new Button { Text = "Fit Stage", Width = 104, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        fit.Left = Width - 124;
        fit.Top = 10;
        Theme.StyleButton(fit);
        fit.Click += (_, _) => _stage.Fit();
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
        _workspaceTabs.Dock = DockStyle.Top;
        _workspaceTabs.Height = 44;
        body.Controls.Add(_workspaceTabs);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 52, BackColor = Theme.Top, FlowDirection = FlowDirection.TopDown, Padding = new Padding(7, 10, 7, 6) };
        body.Controls.Add(tools);
        AddTool(tools, "↖", ToolMode.Select, "Select");
        AddTool(tools, "✥", ToolMode.Hand, "Pan View");
        AddTool(tools, "■", ToolMode.Rectangle, "Rectangle Tool");
        AddTool(tools, "○", ToolMode.Ellipse, "Ellipse Tool");
        AddTool(tools, "△", ToolMode.Triangle, "Triangle Tool");
        AddTool(tools, "⬡", ToolMode.Polygon, "Polygon Tool");
        AddTool(tools, "★", ToolMode.Star, "Star Tool");
        AddTool(tools, "╱", ToolMode.Line, "Line Tool");
        AddTool(tools, "●", ToolMode.Fill, "Fill Tool");
        RefreshToolButtons();

        var inspector = new Panel { Dock = DockStyle.Right, Width = 306, BackColor = Theme.Panel, Padding = new Padding(12) };
        body.Controls.Add(inspector);
        BuildInspectorPages(inspector);

        var leftTabs = BuildLeftTabs();
        body.Controls.Add(leftTabs);

        var layerPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10) };
        var layerPage = new TabPage("Layers") { BackColor = Theme.Panel, Padding = new Padding(0) };
        layerPage.Controls.Add(layerPanel);
        leftTabs.TabPages.Add(layerPage);

        layerPanel.Controls.Add(new Label { Text = "Layers", Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
        var layerButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Panel };
        var solo = new Button { Text = "Solo", Width = 78, Height = 32 };
        Theme.StyleButton(solo);
        solo.Click += (_, _) => { _scene.SoloLayer(_scene.ActiveLayer); RefreshLayers(); _stage.Invalidate(); };
        var all = new Button { Text = "All", Width = 60, Height = 32 };
        Theme.StyleButton(all);
        all.Click += (_, _) => { _scene.ShowAllLayers(); RefreshLayers(); _stage.Invalidate(); };
        layerButtons.Controls.Add(solo);
        layerButtons.Controls.Add(all);
        layerPanel.Controls.Add(layerButtons);
        _layers.Dock = DockStyle.Fill;
        _layers.BackColor = Theme.Panel;
        _layers.ForeColor = Theme.Text;
        _layers.Font = Theme.UiFont(9.5f);
        _layers.ItemHeight = 22;
        _layers.BorderStyle = BorderStyle.None;
        layerPanel.Controls.Add(_layers);

        var vaultPage = new TabPage("Library / Vault") { BackColor = Theme.Panel, Padding = new Padding(0) };
        _libraryVaultPanel.Dock = DockStyle.Fill;
        vaultPage.Controls.Add(_libraryVaultPanel);
        leftTabs.TabPages.Add(vaultPage);

        var stagePanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage };
        body.Controls.Add(stagePanel);
        stagePanel.BringToFront();
        var metrics = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Top };
        metrics.Controls.Add(_fps);
        _draw.Left = 92;
        metrics.Controls.Add(_draw);
        _atoms.Left = 310;
        metrics.Controls.Add(_atoms);
        _zoom.Left = 540;
        metrics.Controls.Add(_zoom);
        var zoomIn = new Button { Text = "+", Width = 36, Height = 30, Top = 6, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleButton(zoomIn);
        zoomIn.Left = stagePanel.Width - 44;
        zoomIn.Click += (_, _) => _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), 1.22f);
        var zoomOut = new Button { Text = "-", Width = 36, Height = 30, Top = 6, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Theme.StyleButton(zoomOut);
        zoomOut.Left = stagePanel.Width - 84;
        zoomOut.Click += (_, _) => _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), 0.82f);
        metrics.Controls.Add(zoomIn);
        metrics.Controls.Add(zoomOut);
        stagePanel.Controls.Add(_stage);
        stagePanel.Controls.Add(metrics);
        metrics.BringToFront();
        _workspaceTabs.BringToFront();

        Controls.Add(_timeline);
    }

    private static TabControl BuildLeftTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Left,
            Width = 282,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(132, 34),
            BackColor = Theme.Panel
        };
        tabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == tabs.SelectedIndex;
            using var bg = new SolidBrush(selected ? Theme.PanelStrong : Theme.Top);
            using var text = new SolidBrush(selected ? Theme.Text : Theme.Muted);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Theme.UiFont(9, selected ? FontStyle.Bold : FontStyle.Regular), e.Bounds, selected ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var border = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        };

        return tabs;
    }

    private void BuildInspectorPages(Control inspector)
    {
        _basicInspectorPage.Dock = DockStyle.Fill;
        _basicInspectorPage.BackColor = Theme.Panel;
        _objectInspector.Dock = DockStyle.Top;
        _objectInspector.Height = 270;
        _objectInspector.BackColor = Theme.Panel;
        BuildInspector(_objectInspector);
        _drawSettingsPanel.Dock = DockStyle.Fill;
        _basicInspectorPage.Controls.Add(_drawSettingsPanel);
        _basicInspectorPage.Controls.Add(_objectInspector);

        _animationPage.Dock = DockStyle.Fill;
        _animationPage.BackColor = Theme.Panel;
        _playbackSettings.Dock = DockStyle.Top;
        _playbackSettings.Height = 188;
        _animationPage.Controls.Add(_playbackSettings);

        _materialEditor.Dock = DockStyle.Fill;
        _hierarchyPanel.Dock = DockStyle.Fill;

        inspector.Controls.Add(_hierarchyPanel);
        inspector.Controls.Add(_materialEditor);
        inspector.Controls.Add(_animationPage);
        inspector.Controls.Add(_basicInspectorPage);
        ShowWorkspace(WorkspaceView.BasicDrawing);
    }

    private void BuildInspector(Control parent)
    {
        var y = 8;
        parent.Controls.Add(Theme.Label("Inspector", 0, y, 260, Theme.Text, Theme.UiFont(10, FontStyle.Bold)));
        y += 34;
        AddField(parent, "Layer count", _layerInput, ref y);
        AddField(parent, "Object count", _objectInput, ref y);
        AddField(parent, "Virtual atoms", _atomInput, ref y);
        y += 8;
        foreach (var label in new[] { _selected, _selectedLayer, _selectedAtoms, _objectMetric })
        {
            label.Left = 0;
            label.Top = y;
            label.Width = 250;
            parent.Controls.Add(label);
            y += 26;
        }

        y += 8;
        parent.Controls.Add(Theme.Label("Active color", 0, y, 240, Theme.Muted));
        y += 24;
        _color.Items.AddRange(["Teal", "Amber", "Coral", "Violet", "White"]);
        _color.SelectedIndex = 0;
        _color.Left = 0;
        _color.Top = y;
        _color.Width = 190;
        _color.Height = 30;
        Theme.StyleComboBox(_color);
        parent.Controls.Add(_color);
        y += 42;
        parent.Controls.Add(Theme.Label("Stroke width", 0, y, 240, Theme.Muted));
        y += 24;
        _stroke.Left = 0;
        _stroke.Top = y;
        _stroke.Width = 190;
        _stroke.Height = 30;
        Theme.StyleNumeric(_stroke);
        parent.Controls.Add(_stroke);
    }

    private static void AddField(Control parent, string label, TextBox box, ref int y)
    {
        parent.Controls.Add(Theme.Label(label, 0, y, 240, Theme.Muted));
        y += 22;
        box.Left = 0;
        box.Top = y;
        box.Width = 190;
        box.Height = 28;
        parent.Controls.Add(box);
        y += 38;
    }

    private void AddTool(FlowLayoutPanel panel, string text, ToolMode tool, string displayName)
    {
        var button = new Button { Text = text, Width = 36, Height = 36, Margin = new Padding(0, 0, 0, 8), Tag = tool, AccessibleName = displayName };
        Theme.StyleButton(button);
        button.MouseEnter += (_, _) => _toolTip.ShowFor(button, displayName);
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        button.Click += (_, _) =>
        {
            _tool = tool;
            _stage.ClearDrawingPreview();
            if (ToolShapeKind(tool) is { } shape)
            {
                _drawSettings.ShapeKind = shape;
                _drawSettings.NotifyChanged();
            }
            RefreshToolButtons();
        };
        _toolButtons[tool] = button;
        panel.Controls.Add(button);
    }

    private void HookEvents()
    {
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _timeline.CurrentFrameChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            SetFrame(_timeline.CurrentFrame);
        };
        _playbackSettings.FpsChanged += (_, _) => _timer.Interval = Math.Max(1, (int)Math.Round(1000.0 / _playbackSettings.Fps));
        _playbackSettings.FrameRangeChanged += (_, _) =>
        {
            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
            SyncFrameSliderRange();
            SetFrame(_frame);
        };
        _materialEditor.MaterialChanged += (_, e) =>
        {
            _stroke.Value = (decimal)Math.Clamp(e.StrokeWidth, (float)_stroke.Minimum, (float)_stroke.Maximum);
            if (_selectedObject >= 0 && _selectedObject < _scene.ObjectCount)
            {
                _scene.Argb[_selectedObject] = Color.FromArgb((int)Math.Clamp(e.Opacity * 255, 0, 255), e.Fill).ToArgb();
                _scene.Stroke[_selectedObject] = e.StrokeWidth;
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
                _selectedObject = e.Index;
                _stage.SelectedObject = e.Index;
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
        _stage.MouseWheel += (_, e) => _stage.ZoomAt(e.Location, e.Delta > 0 ? 1.12f : 0.89f);
        _stage.MouseDown += StageMouseDown;
        _stage.MouseMove += StageMouseMove;
        _stage.MouseUp += StageMouseUp;
    }

    private void Generate()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            _scene.Generate(ParseInt(_layerInput.Text, 1000), ParseInt(_objectInput.Text, 100000), ParseLong(_atomInput.Text, 100000000));
            _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
            SyncFrameSliderRange();
            SetFrame(0);
            _selectedObject = -1;
            _stage.SelectedObject = -1;
            RefreshLayers();
            _hierarchyPanel.BindScene(_scene);
            _stage.Fit();
            UpdateInspector();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void CreateNewProject()
    {
        _scene.CreateEmpty();
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
        SyncFrameSliderRange();
        SetFrame(0);
        _selectedObject = -1;
        _stage.SelectedObject = -1;
        RefreshLayers();
        _hierarchyPanel.BindScene(_scene);
        _stage.Fit();
        UpdateInspector();
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
        var elapsed = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        var instant = 1 / elapsed;
        _smoothedFps = _smoothedFps <= 0 ? instant : _smoothedFps * 0.9 + instant * 0.1;
        if (_playing)
        {
            var next = _frame + 1;
            if (next > _playbackSettings.EndFrame)
            {
                if (_playbackSettings.LoopPlayback) next = _playbackSettings.StartFrame;
                else
                {
                    next = _playbackSettings.EndFrame;
                    _playing = false;
                    _play.Text = "Play";
                }
            }

            SetFrame(next);
        }

        var stats = _stage.LastStats;
        _fps.Text = $"FPS {_smoothedFps:0}";
        _draw.Text = stats.TileLod
            ? $"Tiles {CompactFormat.Number(stats.TileDraws)}"
            : $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)}";
        _atoms.Text = $"Atoms {CompactFormat.Number(stats.VisibleAtoms)} / {CompactFormat.Number(_scene.VirtualAtomCount)}";
        _zoom.Text = $"Zoom {_stage.Zoom * 100:0}%";
        _stage.Invalidate();
    }

    private void SetFrame(int frame)
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

        _stage.Invalidate();
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

            _selectedObject = _scene.HitTest(_startWorld.Value, _frame, SelectionToleranceWorld());
            _stage.SelectedObject = _selectedObject;
            if (_selectedObject >= 0) CaptureEditStart(_selectedObject);
            UpdateInspector();
            _stage.Invalidate();
        }
        else if (_tool == ToolMode.Fill)
        {
            var hit = _scene.HitTest(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (hit >= 0)
            {
                _scene.Argb[hit] = ActiveColor().ToArgb();
                _selectedObject = hit;
                _stage.SelectedObject = hit;
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
        if (_tool == ToolMode.Hand)
        {
            _stage.Pan(dx, dy);
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
                var dxWorld = world.X - _startWorld.Value.X;
                var dyWorld = world.Y - _startWorld.Value.Y;
                _scene.X[_selectedObject] = _selectedStart.Value.X + dxWorld;
                _scene.Y[_selectedObject] = _selectedStart.Value.Y + dyWorld;
                if (_curveControlStart is not null)
                {
                    _scene.CurveControlX[_selectedObject] = _curveControlStart.Value.X + dxWorld;
                    _scene.CurveControlY[_selectedObject] = _curveControlStart.Value.Y + dyWorld;
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

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (IsDrawingTool(_tool) && _startWorld is not null && _startScreen is not null)
        {
            var dx = e.X - _startScreen.Value.X;
            var dy = e.Y - _startScreen.Value.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) AddDrawnObject(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }

        _stage.ClearDrawingPreview();

        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
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

    private void UpdateDrawingPreview(PointF start, PointF end, ToolMode tool)
    {
        start = _drawSettings.SnapPoint(start);
        end = _drawSettings.SnapPoint(end);
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool != ToolMode.Line)
        {
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            end = new PointF(start.X + size.Width, start.Y + size.Height);
        }

        _stage.SetDrawingPreview(start, end, shape, ActiveColor(), (float)_materialEditor.StrokeWidth);
    }

    private float SelectionToleranceWorld() => Math.Max(4, 10 / Math.Max(0.02f, _stage.Zoom));

    private void CaptureEditStart(int objectIndex)
    {
        _selectedStart = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
        _curveControlStart = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        _resizeStartCenter = _selectedStart;
        _resizeStartSize = new SizeF(_scene.Width[objectIndex], _scene.Height[objectIndex]);
        _resizeStartAngle = _scene.Angle[objectIndex];
    }

    private void ApplyHandleDrag(PointF world)
    {
        if (_selectedObject < 0 || _resizeStartCenter is null || _resizeStartSize is null) return;
        if (_activeHandle == EditHandleKind.BezierControl)
        {
            var snapped = _drawSettings.SnapPoint(world);
            _scene.CurveControlX[_selectedObject] = snapped.X;
            _scene.CurveControlY[_selectedObject] = snapped.Y;
            return;
        }

        var draggedLocal = WorldToLocalFromEditStart(world);
        var anchor = OppositeCorner(_activeHandle, _resizeStartSize.Value);
        var minSize = 4f;
        var width = Math.Max(minSize, Math.Abs(draggedLocal.X - anchor.X));
        var height = Math.Max(minSize, Math.Abs(draggedLocal.Y - anchor.Y));
        var centerLocal = new PointF((draggedLocal.X + anchor.X) * 0.5f, (draggedLocal.Y + anchor.Y) * 0.5f);
        var centerWorld = LocalToWorldFromEditStart(centerLocal);

        _scene.X[_selectedObject] = centerWorld.X;
        _scene.Y[_selectedObject] = centerWorld.Y;
        _scene.Width[_selectedObject] = width;
        _scene.Height[_selectedObject] = height;
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
        start = _drawSettings.SnapPoint(start);
        end = _drawSettings.SnapPoint(end);
        var center = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        float width;
        float height;
        float angle;
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool == ToolMode.Line)
        {
            width = Math.Max(4, Distance(start, end));
            height = Math.Max(3, (float)_stroke.Value + 2);
            angle = _drawSettings.SnapAngle(MathF.Atan2(end.Y - start.Y, end.X - start.X));
            shape = ShapeKind.Line;
        }
        else
        {
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            width = Math.Max(4, Math.Abs(size.Width));
            height = Math.Max(4, Math.Abs(size.Height));
            angle = 0;
        }

        _selectedObject = _scene.AddObject(_scene.ActiveLayer, center, new SizeF(width, height), angle, (float)_materialEditor.StrokeWidth, ActiveColor(), tool == ToolMode.Line ? 6u : 24u, shape);
        _stage.SelectedObject = _selectedObject;
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void UpdateInspector()
    {
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        if (_selectedObject < 0 || _selectedObject >= _scene.ObjectCount)
        {
            _selected.Text = "Selected: None";
            _selectedLayer.Text = _scene.LayerNames.Length > 0 ? $"Layer: {_scene.LayerNames[_scene.ActiveLayer]}" : "Layer: -";
            _selectedAtoms.Text = "Atoms: -";
            return;
        }

        var layer = _scene.ObjectLayer[_selectedObject];
        _selected.Text = $"Selected: #{_selectedObject}";
        _selectedLayer.Text = $"Layer: {_scene.LayerNames[layer]}";
        _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(_scene.AtomCount[_selectedObject])}";
    }

    private Color ActiveColor()
    {
        if (_workspaceTabs.SelectedView == WorkspaceView.Materials || _materialEditor is not null)
        {
            return Color.FromArgb((int)Math.Clamp(_materialEditor.Opacity * 255, 0, 255), _materialEditor.Fill);
        }

        return _color.SelectedIndex switch
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
        _basicInspectorPage.Visible = view == WorkspaceView.BasicDrawing;
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
    }

    private static Label MetricLabel(string text, int width) => new() { Text = text, Left = 8, Top = 10, Width = width, Height = 22, ForeColor = Theme.Muted, BackColor = Theme.Top, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static Label InspectorLabel(string text) => new() { Text = text, Height = 24, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static TextBox InputBox(string text)
    {
        var box = new TextBox { Text = text, Width = 190, Height = 28 };
        Theme.StyleTextBox(box);
        return box;
    }
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static long ParseLong(string text, long fallback) => long.TryParse(text, out var value) ? value : fallback;
    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
