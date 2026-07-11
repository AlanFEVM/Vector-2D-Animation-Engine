using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class MainForm : Form
{
    private const int ResizeGripSize = 7;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int VkMenu = 0x12;
    private const double TargetUps = 300.0;
    private const double MetricsRefreshSeconds = 0.25;
    private const double MaxFrameSeconds = 0.1;
    private const int IdleTimerIntervalMs = 250;
    private const int PlaybackTimerIntervalMs = 8;
    private const float EndpointConnectionToleranceUnits = 1.25f;
    private const int MaxUndoSnapshots = 32;
    private const float PasteOffsetUnits = 96f;
    private const int MaxFreehandSamples = 16_384;
    private const float FreehandSampleSpacingPixels = 1.25f;
    private const int VaultDrawerExpandedWidth = 306;

    private VectorProject _project = VectorProject.CreateEmpty();
    private VectorScene _scene;
    private readonly VectorScene _sceneEditStage = new();
    private readonly VectorScene _drawingObjectUnderlayStage = new();
    private SceneCompositionResult _sceneCompositionResult = SceneCompositionResult.Empty;
    private SceneCompositionResult _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
    private readonly StageControl _stage;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IdleTimerIntervalMs };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _metricsClock = Stopwatch.StartNew();
    private FixedStepBatcher _updateBatcher = new(TargetUps);
    private readonly DrawSettings _drawSettings = new();
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
    private readonly ToolMode[] _shapeTools = [ToolMode.Rectangle, ToolMode.Ellipse, ToolMode.Triangle, ToolMode.Polygon, ToolMode.Star];
    private readonly Dictionary<ToolMode, Button> _shapeFlyoutButtons = new();
    private readonly System.Windows.Forms.Timer _shapeFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _vaultDrawerTimer = new() { Interval = 16 };
    private DateTime _shapeFlyoutHideAtUtc;
    private readonly Dictionary<string, Button> _drawingObjectTabButtons = new();
    private IReadOnlyList<SceneDefinition> _scenes => _project.Scenes;
    private IReadOnlyList<DrawingObjectDefinition> _drawingObjects => _project.DrawingObjects;
    private readonly AnimatedToolTip _toolTip = new();
    private readonly WorkspaceTabs _workspaceTabs = new();
    private readonly Panel _workspaceHeader = new();
    private readonly TableLayoutPanel _drawingObjectRow = new();
    private readonly FlowLayoutPanel _drawingObjectTabs = new();
    private readonly DrawSnappingStrip _drawSnappingStrip;
    private readonly TimelineStrip _timeline;
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _renderFpsStatus = StatusLabel("Render FPS 0");
    private readonly ToolStripStatusLabel _animationFpsStatus = StatusLabel("Animation FPS 24");
    private readonly ToolStripStatusLabel _zoomStatus = StatusLabel("Zoom 100%");
    private readonly ToolStripStatusLabel _devReloadStatus = StatusLabel("Auto Restart On");
    private WindowChromeButton? _maximizeButton;
    private readonly PlaybackSettingsPanel _playbackSettings = new();
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly SceneEditorPanel _sceneEditorPanel = new();
    private readonly LibraryVaultPanel _libraryVaultPanel = new();
    private readonly Panel _basicInspectorPage = new();
    private readonly Panel _objectInspector = new();
    private readonly Panel _sceneEditPage = new();
    private readonly Panel _animationPage = new();
    private SvgIconButton? _shapeToolButton;
    private FlowLayoutPanel? _shapeToolFlyout;
    private Panel? _vaultDrawer;
    private SvgIconButton? _vaultButton;
    private ToolMode _tool = ToolMode.Select;
    private ToolMode _activeShapeTool = ToolMode.Rectangle;
    private bool _playing;
    private int _frame;
    private int _selectedObject = -1;
    private DrawingElementHit _selectedElement = DrawingElementHit.None;
    private readonly List<DrawingElementHit> _selectedElements = new();
    private readonly List<int> _selectedObjects = new();
    private readonly Dictionary<int, PointF> _selectedMoveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurveStarts = new();
    private readonly List<LineEndpointEditStart> _lineEndpointEditStarts = new();
    private readonly Stack<VectorSceneSnapshot> _undoStack = new();
    private readonly Stack<SceneTimelineUndoEntry> _sceneTimelineUndoStack = new();
    private readonly List<ClipboardObject> _clipboardObjects = new();
    private readonly List<PointF> _freehandSamples = new(1024);
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
    private double _playbackAccumulator;
    private int _updatesThisSample;
    private int _rendersThisSample;
    private bool _syncingFrame;
    private bool _updatingStrokeInput;
    private bool _viewPanning;
    private bool _viewZooming;
    private bool _viewOrbiting;
    private bool _viewReferencePanning;
    private bool _viewReferenceZooming;
    private bool _marqueeSelecting;
    private bool _detachedSelectionForMove;
    private bool _pointerHitWasAlreadySelected;
    private bool _selectionWasEmptyOnPointerDown;
    private bool _forceMarqueeOnPointerDown;
    private DrawingElementHit _pendingClickSelection = DrawingElementHit.None;
    private Point? _marqueeStart;
    private Point? _freehandLastScreen;
    private ShapeKind _lastSettingsShape = ShapeKind.Rectangle;
    private int _activeSceneIndex;
    private int _activeDrawingObjectIndex;
    private bool _undoCapturedForPointerEdit;
    private bool _freehandDrawing;
    private bool _freehandBrushStroke;
    private bool _vaultDrawerOpen;
    private Color _freehandColor = Color.White;
    private float _freehandStrokeUnits;
    private float _standardStrokeWidthPoints = 2;
    private float _pencilStrokeWidthPoints = 2;
    private float _brushStrokeWidthPoints = 8;

    private readonly record struct LineEndpointEditStart(int ObjectIndex, bool StartEndpoint, PointF OriginalEndpoint, PointF OppositeEndpoint, PointF Control, bool KeepStraight);

    private readonly record struct DrawingStackKey(long Order, double SubOrder);

    private sealed record SceneTimelineUndoEntry(SceneDefinition Scene, AnimationTimelineSnapshot Snapshot);

    private sealed record ClipboardObject(
        int Layer,
        PointF Center,
        SizeF Size,
        float Angle,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        ShapeKind Shape,
        PointF CurveControl,
        PointF[][]? PathWorldContours);

    public MainForm()
    {
        _scene = _project.DrawingObjects[0].Scene;
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        Text = "Vector 2D Animation Engine";
        FormBorderStyle = FormBorderStyle.None;
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(1120, 720);
        Padding = new Padding(1);
        BackColor = Theme.Border;
        Font = Theme.UiFont();
        KeyPreview = true;

        _stage = new StageControl(_scene) { Dock = DockStyle.Fill };
        _stage.AllowDrop = true;
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = 192 };
        _drawSettingsPanel = new DrawSettingsPanel(_drawSettings);
        _drawSnappingStrip = new DrawSnappingStrip(_drawSettings);
        _shapeFlyoutHideTimer.Interval = 100;
        _shapeFlyoutHideTimer.Tick += (_, _) => UpdateShapeToolFlyoutVisibility();
        _vaultDrawerTimer.Tick += (_, _) => TickVaultDrawer();
        InitializeQuickMaterialInputs();
        BuildUi();
        HookEvents();
        CreateNewProject();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Top };
        PaintBottomBorder(top);
        Controls.Add(top);
        RegisterWindowDrag(top);
        var mark = new Label { Text = "V2", Left = 14, Top = 12, Width = 30, Height = 30, ForeColor = Theme.Accent, BackColor = Theme.Top, Font = Theme.UiFont(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        RegisterWindowDrag(mark);
        top.Controls.Add(mark);
        var title = Theme.Label("Vector 2D Animation Engine", 58, 16, 260, Theme.Text, Theme.UiFont(10, FontStyle.Bold));
        RegisterWindowDrag(title);
        top.Controls.Add(title);
        _play.Left = 330;
        _play.Top = 12;
        _play.Height = 32;
        Theme.StyleButton(_play);
        top.Controls.Add(_play);
        top.Controls.Add(Theme.Label("Frame", 420, 16, 58, Theme.Muted));
        _frameSlider.Left = 476;
        _frameSlider.Top = 10;
        _frameSlider.Width = 430;
        _frameSlider.Minimum = 0;
        _frameSlider.Maximum = 239;
        _frameSlider.TickFrequency = 24;
        top.Controls.Add(_frameSlider);
        var generate = new Button { Text = "Run Stress Scene", Width = 150, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        generate.Left = Width - 282;
        generate.Top = 12;
        Theme.StyleButton(generate);
        generate.Click += (_, _) => Generate();
        var fit = new Button { Text = "Fit Stage", Width = 104, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        fit.Left = Width - 124;
        fit.Top = 12;
        Theme.StyleButton(fit);
        fit.Click += (_, _) =>
        {
            _stage.Fit();
            UpdateStatusBar();
        };
        var minimize = CreateWindowButton(WindowChromeButtonKind.Minimize, "Minimize");
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeButton = CreateWindowButton(WindowChromeButtonKind.Maximize, "Maximize");
        _maximizeButton.Click += (_, _) => ToggleMaximized();
        var close = CreateWindowButton(WindowChromeButtonKind.Close, "Close");
        close.Click += (_, _) => Close();
        top.Controls.Add(generate);
        top.Controls.Add(fit);
        top.Controls.Add(minimize);
        top.Controls.Add(_maximizeButton);
        top.Controls.Add(close);
        top.Resize += (_, _) =>
        {
            PositionTopBarActions(top, minimize, _maximizeButton, close, fit, generate);
        };
        Resize += (_, _) => UpdateWindowChromeState();
        PositionTopBarActions(top, minimize, _maximizeButton, close, fit, generate);
        UpdateWindowChromeState();

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
        _drawingObjectRow.Dock = DockStyle.Bottom;
        _drawingObjectRow.Height = 40;
        _drawingObjectRow.BackColor = Theme.Top;
        _drawingObjectRow.ColumnCount = 2;
        _drawingObjectRow.RowCount = 1;
        _drawingObjectRow.Margin = Padding.Empty;
        _drawingObjectRow.Padding = Padding.Empty;
        _drawingObjectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _drawingObjectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _drawSnappingStrip.Width + 12));
        _drawingObjectRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _workspaceHeader.Controls.Add(_drawingObjectRow);
        _drawingObjectTabs.Dock = DockStyle.Fill;
        _drawingObjectTabs.BackColor = Theme.Top;
        _drawingObjectTabs.FlowDirection = FlowDirection.LeftToRight;
        _drawingObjectTabs.WrapContents = false;
        _drawingObjectTabs.Padding = new Padding(8, 4, 8, 6);
        _drawingObjectTabs.Margin = Padding.Empty;
        _drawSnappingStrip.Dock = DockStyle.Fill;
        _drawSnappingStrip.Margin = Padding.Empty;
        _drawingObjectRow.Controls.Add(_drawingObjectTabs, 0, 0);
        _drawingObjectRow.Controls.Add(_drawSnappingStrip, 1, 0);
        BuildDrawingObjectTabs();

        var vaultDrawer = new Panel { Dock = DockStyle.Left, Width = 0, BackColor = Theme.Panel, Padding = new Padding(0), Visible = false };
        _vaultDrawer = vaultDrawer;
        PaintRightBorder(vaultDrawer);
        _libraryVaultPanel.Dock = DockStyle.Fill;
        vaultDrawer.Controls.Add(_libraryVaultPanel);
        body.Controls.Add(vaultDrawer);

        var inspector = new Panel { Dock = DockStyle.Right, Width = 324, BackColor = Theme.Panel, Padding = new Padding(14, 16, 14, 12), AutoScroll = true };
        PaintLeftBorder(inspector);
        body.Controls.Add(inspector);
        BuildInspectorPages(inspector);

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

        var tools = new FlowLayoutPanel
        {
            Left = 8,
            Top = metrics.Height + 8,
            Width = 48,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6),
            Margin = Padding.Empty
        };
        PaintFullBorder(tools);
        AddTool(tools, SvgIconKind.Select, ToolMode.Select, "Select");
        AddTool(tools, SvgIconKind.Pan, ToolMode.Hand, "Pan View");
        AddShapeToolGroup(tools, stagePanel);
        AddTool(tools, SvgIconKind.Line, ToolMode.Line, "Line Tool");
        AddTool(tools, SvgIconKind.Pencil, ToolMode.Pencil, "Pencil Tool");
        AddTool(tools, SvgIconKind.Brush, ToolMode.Brush, "Brush Tool");
        AddTool(tools, SvgIconKind.Fill, ToolMode.Fill, "Fill Tool");

        var vaultButton = new SvgIconButton(SvgIconKind.Vault) { Margin = new Padding(0, 8, 0, 0), AccessibleName = "Vault" };
        _vaultButton = vaultButton;
        Theme.StyleButton(vaultButton);
        vaultButton.MouseEnter += (_, _) => _toolTip.ShowFor(vaultButton, "Vault");
        vaultButton.MouseLeave += (_, _) => _toolTip.HideTip();
        vaultButton.Click += (_, _) => ToggleVaultDrawer();
        tools.Controls.Add(vaultButton);
        stagePanel.Controls.Add(tools);
        tools.BringToFront();
        RefreshToolButtons();
        _workspaceTabs.BringToFront();

        Controls.Add(_timeline);
        BuildStatusBar();
        Controls.Add(_statusBar);
    }

    private void InitializeQuickMaterialInputs()
    {
        _color.Items.AddRange(["Teal", "Amber", "Coral", "Violet", "White"]);
        _color.SelectedIndex = 0;
        Theme.StyleComboBox(_color);
        Theme.StyleNumeric(_stroke);
    }

    private static WindowChromeButton CreateWindowButton(WindowChromeButtonKind kind, string name)
    {
        return new WindowChromeButton(kind)
        {
            Top = 12,
            AccessibleName = name
        };
    }

    private static void PositionTopBarActions(Control top, Control minimize, Control maximize, Control close, Control fit, Control generate)
    {
        var buttonTop = Math.Max(0, (top.ClientSize.Height - close.Height) / 2);
        close.Left = top.ClientSize.Width - close.Width - 8;
        maximize.Left = close.Left - maximize.Width - 2;
        minimize.Left = maximize.Left - minimize.Width - 2;
        close.Top = buttonTop;
        maximize.Top = buttonTop;
        minimize.Top = buttonTop;
        fit.Left = minimize.Left - fit.Width - 14;
        generate.Left = fit.Left - generate.Width - 8;
    }

    private void ToggleVaultDrawer()
    {
        if (_vaultDrawer is null) return;
        _vaultDrawerOpen = !_vaultDrawerOpen;
        if (_vaultDrawerOpen)
        {
            _vaultDrawer.Visible = true;
            if (_vaultButton is not null) Theme.StyleActiveButton(_vaultButton);
        }
        else if (_vaultButton is not null)
        {
            Theme.StyleButton(_vaultButton);
        }

        if (!_vaultDrawerTimer.Enabled) _vaultDrawerTimer.Start();
    }

    private void TickVaultDrawer()
    {
        if (_vaultDrawer is null)
        {
            _vaultDrawerTimer.Stop();
            return;
        }

        var target = _vaultDrawerOpen ? VaultDrawerExpandedWidth : 0;
        var remaining = target - _vaultDrawer.Width;
        if (remaining == 0)
        {
            if (!_vaultDrawerOpen) _vaultDrawer.Visible = false;
            _vaultDrawerTimer.Stop();
            return;
        }

        var step = Math.Max(12, (int)Math.Ceiling(Math.Abs(remaining) * 0.30));
        _vaultDrawer.Width = Math.Clamp(_vaultDrawer.Width + Math.Sign(remaining) * step, 0, VaultDrawerExpandedWidth);
    }

    private void RegisterWindowDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Minimized) return;
            ReleaseCapture();
            SendMessage(Handle, WmNcLeftButtonDown, HtCaption, 0);
        };
        control.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleMaximized();
        };
    }

    private void ToggleMaximized()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            ApplyMaximizedBounds();
            WindowState = FormWindowState.Maximized;
        }

        UpdateWindowChromeState();
    }

    private void UpdateWindowChromeState()
    {
        if (_maximizeButton is null) return;
        _maximizeButton.Kind = WindowState == FormWindowState.Maximized
            ? WindowChromeButtonKind.Restore
            : WindowChromeButtonKind.Maximize;
        _maximizeButton.AccessibleName = WindowState == FormWindowState.Maximized ? "Restore" : "Maximize";
        _maximizeButton.Invalidate();
        Padding = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyMaximizedBounds();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shapeFlyoutHideTimer.Dispose();
            _vaultDrawerTimer.Dispose();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyMaximizedBounds()
    {
        if (!IsHandleCreated) return;
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (m.Result != (IntPtr)HtClient || WindowState == FormWindowState.Maximized) return;

            var point = PointToClient(GetPointFromLParam(m.LParam));
            var left = point.X <= ResizeGripSize;
            var right = point.X >= ClientSize.Width - ResizeGripSize;
            var top = point.Y <= ResizeGripSize;
            var bottom = point.Y >= ClientSize.Height - ResizeGripSize;

            if (left && top) m.Result = (IntPtr)HtTopLeft;
            else if (right && top) m.Result = (IntPtr)HtTopRight;
            else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
            else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
            else if (left) m.Result = (IntPtr)HtLeft;
            else if (right) m.Result = (IntPtr)HtRight;
            else if (top) m.Result = (IntPtr)HtTop;
            else if (bottom) m.Result = (IntPtr)HtBottom;
            return;
        }

        base.WndProc(ref m);
    }

    private static Point GetPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int keyCode);

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
        if (IsAutoRestartEnabled())
        {
            _statusBar.Items.Add(StatusSeparator());
            _statusBar.Items.Add(_devReloadStatus);
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

    private static void PaintFullBorder(Control control)
    {
        control.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        };
    }

    private void BuildInspectorPages(Control inspector)
    {
        _basicInspectorPage.Dock = DockStyle.Fill;
        _basicInspectorPage.BackColor = Theme.Panel;
        _basicInspectorPage.AutoScroll = true;
        _basicInspectorPage.Padding = new Padding(0, 0, 4, 0);
        _objectInspector.Dock = DockStyle.Top;
        _objectInspector.Height = 142;
        _objectInspector.BackColor = Theme.Panel;
        BuildInspector(_objectInspector);
        _materialEditor.Dock = DockStyle.Top;
        _materialEditor.Height = 252;
        _drawSettingsPanel.Dock = DockStyle.Top;
        _drawSettingsPanel.Height = 164;
        _basicInspectorPage.Controls.Add(_materialEditor);
        _basicInspectorPage.Controls.Add(_drawSettingsPanel);
        _basicInspectorPage.Controls.Add(_objectInspector);

        _sceneEditPage.Dock = DockStyle.Fill;
        _sceneEditPage.BackColor = Theme.Panel;
        _sceneEditPage.AutoScroll = true;
        _hierarchyPanel.Dock = DockStyle.Fill;
        _sceneEditorPanel.Dock = DockStyle.Top;
        _sceneEditorPanel.Height = 388;
        _sceneEditPage.Controls.Add(_hierarchyPanel);
        _sceneEditPage.Controls.Add(_sceneEditorPanel);
        _sceneEditorPanel.BringToFront();

        _animationPage.Dock = DockStyle.Fill;
        _animationPage.BackColor = Theme.Panel;
        _playbackSettings.Dock = DockStyle.Top;
        _playbackSettings.Height = 188;
        _animationPage.Controls.Add(_playbackSettings);

        inspector.Controls.Add(_animationPage);
        inspector.Controls.Add(_sceneEditPage);
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
            RowCount = 4,
            Padding = new Padding(0, 4, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < content.RowCount; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
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
        if (_activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjects.Count) AddDrawingObjectTab(_activeDrawingObjectIndex);
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
            var dragData = new DataObject();
            dragData.SetData(typeof(DrawingObjectDragData), new DrawingObjectDragData(_project.Id, drawingObject.Id));
            dragData.SetData(typeof(VaultItem), drawingObject.ToVaultItem());
            button.DoDragDrop(dragData, DragDropEffects.Copy);
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
        button.Click += (_, _) => AddDrawingObject();
        _drawingObjectTabs.Controls.Add(button);
    }

    private void AddScene()
    {
        _project.AddScene();
        SelectScene(_scenes.Count - 1);
    }

    private void SelectScene(int index)
    {
        if (index < 0 || index >= _scenes.Count) return;
        if (_activeSceneIndex != index) ResetUndoHistory();
        _activeSceneIndex = index;
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        if (_workspaceTabs.SelectedView == WorkspaceView.SceneEditor) BindSceneEditStage(resetView: false);
        _sceneEditorPanel.RefreshSceneStats();
        AppLog.Info($"Selected scene: {_scenes[index].Name}");
    }

    private SceneDefinition? ActiveScene()
    {
        return _activeSceneIndex >= 0 && _activeSceneIndex < _scenes.Count ? _scenes[_activeSceneIndex] : null;
    }

    private bool IsScene3DView()
    {
        return IsSceneCompositionContext()
            && _stage.ReferenceDimension == SceneDimension.ThreeD;
    }

    private bool IsSceneCompositionContext() => _timeline.Context is ICompositionDefinition { CanDraw: false };

    private bool IsNestedInstanceTimelineTrackActive()
    {
        if (_timeline.Context is not DrawingObjectDefinition drawingObject
            || string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            return false;
        }

        var targetId = drawingObject.Timeline.FindTrack(_timeline.ActiveTrackId)?.TargetId;
        return targetId is not null
            && drawingObject.Instances.Any(instance => string.Equals(instance.Id, targetId, StringComparison.Ordinal));
    }

    private bool DrawingToolsBlocked() => IsSceneCompositionContext() || IsNestedInstanceTimelineTrackActive();

    private static bool IsAltPressed()
    {
        return (ModifierKeys & Keys.Alt) == Keys.Alt || (GetKeyState(VkMenu) & 0x8000) != 0;
    }

    private static bool IsShiftPressed()
    {
        return (ModifierKeys & Keys.Shift) == Keys.Shift;
    }

    private static bool IsControlPressed()
    {
        return (ModifierKeys & Keys.Control) == Keys.Control;
    }

    private static bool MouseButtonDown(MouseEventArgs e, MouseButtons button)
    {
        return (e.Button & button) == button;
    }

    private void UpdateSceneSettings(SceneSettingsChangedEventArgs e)
    {
        if (e.SceneIndex < 0 || e.SceneIndex >= _scenes.Count) return;
        var scene = _scenes[e.SceneIndex];
        scene.Dimension = e.Dimension;
        scene.Camera.Projection = e.Projection;
        if (scene.Dimension == SceneDimension.TwoD && scene.Camera.Projection == CameraProjection.Perspective)
        {
            scene.Camera.Depth = Math.Max(scene.Camera.Depth, 1000);
        }

        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        if (_workspaceTabs.SelectedView == WorkspaceView.SceneEditor) _stage.ConfigureReferenceView(scene);
        AppLog.Info($"Updated scene settings: {scene.Name}, {scene.Dimension}, {scene.Camera.Projection}");
    }

    private void AddSceneInstanceFromActiveDrawingObject()
    {
        var scene = ActiveScene();
        var drawingObject = ActiveDrawingObject();
        if (scene is null || drawingObject is null) return;

        var index = scene.Instances.Count + 1;
        if (!_project.TryAddSceneInstance(
                scene.Id,
                drawingObject.Id,
                PointF.Empty,
                scene.Dimension == SceneDimension.TwoD ? index : 0,
                out _))
        {
            return;
        }

        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _sceneEditorPanel.RefreshSceneStats();
        if (_workspaceTabs.SelectedView == WorkspaceView.SceneEditor)
        {
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration();
            RebuildSceneComposition();
        }
        AppLog.Info($"Added scene instance: {drawingObject.Name} -> {scene.Name}");
    }

    private void AddDrawingObject()
    {
        _project.AddDrawingObject();
        SelectDrawingObject(_drawingObjects.Count - 1);
    }

    private DrawingObjectDefinition? ActiveDrawingObject()
    {
        return _activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjects.Count
            ? _drawingObjects[_activeDrawingObjectIndex]
            : null;
    }

    private void BindActiveDrawingObjectScene(bool resetView)
    {
        var drawingObject = ActiveDrawingObject();
        if (drawingObject is null)
        {
            if (_drawingObjects.Count == 0)
            {
                _project.AddDrawingObject("Drawing Object 001");
            }

            _activeDrawingObjectIndex = 0;
            drawingObject = _drawingObjects[0];
        }

        var nextScene = drawingObject.Scene;
        if (!ReferenceEquals(_scene, nextScene)) ResetUndoHistory();
        _scene = nextScene;
        if (_scene.LayerCount <= 0) _scene.CreateEmpty();
        _stage.BindScene(_scene);
        _stage.ConfigureReferenceView(null);
        _timeline.BindContext(drawingObject);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
        SyncFrameSliderRange();
        SetFrame(Math.Clamp(_frame, 0, _scene.FrameCount - 1));
        ClearSelection();
        RefreshLayers();
        _hierarchyPanel.BindScene(_scene);
        _sceneEditorPanel.BindScene(_scene);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _sceneEditorPanel.SetActiveDrawingObject(drawingObject);
        if (resetView) _stage.ResetDefaultView();
        UpdateInspector();
        UpdateStatusBar();
    }

    private void BindSceneEditStage(bool resetView)
    {
        if (!ReferenceEquals(_scene, _sceneEditStage)) ResetUndoHistory();
        _scene = _sceneEditStage;
        var sceneDefinition = ActiveScene();
        _sceneCompositionResult = SceneCompositionBuilder.Build(_sceneEditStage, sceneDefinition, _drawingObjects, _frame);
        _stage.BindScene(_scene);
        _stage.ConfigureReferenceView(sceneDefinition);
        if (sceneDefinition is not null) _timeline.BindSceneDefinition(sceneDefinition);
        else _timeline.BindScene(_scene);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _playbackSettings.SetFrameRange(0, Math.Max(0, (sceneDefinition?.FrameCount ?? _scene.FrameCount) - 1));
        SyncFrameSliderRange();
        SetFrame(Math.Clamp(_frame, 0, _playbackSettings.EndFrame));
        ClearSelection();
        RefreshLayers();
        _hierarchyPanel.BindScene(_scene);
        _sceneEditorPanel.BindScene(_scene);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
        if (resetView) _stage.ResetDefaultView();
        UpdateInspector();
        UpdateStatusBar();
    }

    private void RebuildSceneComposition()
    {
        if (!IsSceneCompositionContext()) return;
        _sceneCompositionResult = SceneCompositionBuilder.Build(_sceneEditStage, ActiveScene(), _drawingObjects, _frame);
        _scene = _sceneEditStage;
        if (!_playing)
        {
            _hierarchyPanel.BindScene(_sceneEditStage);
            _sceneEditorPanel.BindScene(_sceneEditStage);
        }

        _stage.Invalidate();
    }

    private void RebuildDrawingObjectUnderlay()
    {
        if (IsSceneCompositionContext())
        {
            _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
            _stage.BindUnderlayScene(null);
            return;
        }

        _drawingObjectUnderlayResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            _drawingObjectUnderlayStage,
            ActiveDrawingObject(),
            _drawingObjects,
            _frame);
        _stage.BindUnderlayScene(_drawingObjectUnderlayStage);
    }

    private void SelectDrawingObject(int index)
    {
        if (index < 0 || index >= _drawingObjects.Count) return;
        _activeDrawingObjectIndex = index;
        if (_workspaceTabs.SelectedView == WorkspaceView.SceneEditor) BindSceneEditStage(resetView: false);
        else BindActiveDrawingObjectScene(resetView: true);
        BuildDrawingObjectTabs();
        RefreshToolButtons();
        AppLog.Info($"Selected drawing object tab: {_drawingObjects[index].Name}");
    }

    private bool OpenDrawingObjectEditor(string drawingObjectId)
    {
        var index = -1;
        for (var i = 0; i < _drawingObjects.Count; i++)
        {
            if (!string.Equals(_drawingObjects[i].Id, drawingObjectId, StringComparison.Ordinal)) continue;
            index = i;
            break;
        }

        if (index < 0) return false;

        StopPlayback();
        FinishPointerInteractionForFrameChange();
        _activeDrawingObjectIndex = index;
        BuildDrawingObjectTabs();
        if (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing)
        {
            BindActiveDrawingObjectScene(resetView: false);
        }
        else
        {
            _workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
        }

        AppLog.Info($"Opened drawing object editor: {_drawingObjects[index].Name}");
        return true;
    }

    private void AddTool(FlowLayoutPanel panel, SvgIconKind icon, ToolMode tool, string displayName)
    {
        var button = new SvgIconButton(icon) { Margin = new Padding(0, 0, 0, 8), Tag = tool, AccessibleName = displayName };
        Theme.StyleButton(button);
        button.MouseEnter += (_, _) =>
        {
            if (!IsShapeTool(tool)) HideShapeToolFlyout();
            _toolTip.ShowFor(button, displayName);
        };
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        button.Click += (_, _) => ActivateTool(tool);
        _toolButtons[tool] = button;
        panel.Controls.Add(button);
    }

    private void AddShapeToolGroup(FlowLayoutPanel panel, Control overlayParent)
    {
        _shapeToolButton = new SvgIconButton(ToolIconKind(_activeShapeTool))
        {
            Margin = new Padding(0, 0, 0, 8),
            Tag = _activeShapeTool,
            AccessibleName = ShapeToolName(_activeShapeTool)
        };
        Theme.StyleButton(_shapeToolButton);
        _shapeToolButton.MouseEnter += (_, _) =>
        {
            _toolTip.HideTip();
            ShowShapeToolFlyout();
        };
        _shapeToolButton.MouseLeave += (_, _) =>
        {
            StartShapeFlyoutVisibilityCheck();
        };
        _shapeToolButton.Click += (_, _) => ActivateTool(_activeShapeTool);
        panel.Controls.Add(_shapeToolButton);

        _shapeToolFlyout = new FlowLayoutPanel
        {
            Width = 46,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6),
            Visible = false
        };
        PaintFullBorder(_shapeToolFlyout);
        _shapeToolFlyout.MouseEnter += (_, _) => ShowShapeToolFlyout();
        _shapeToolFlyout.MouseLeave += (_, _) => StartShapeFlyoutVisibilityCheck();
        overlayParent.Controls.Add(_shapeToolFlyout);

        foreach (var tool in _shapeTools)
        {
            var button = new SvgIconButton(ToolIconKind(tool))
            {
                Margin = new Padding(0, 0, 0, tool == _shapeTools[^1] ? 0 : 8),
                Tag = tool,
                AccessibleName = ShapeToolName(tool)
            };
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.HideTip();
                ShowShapeToolFlyout();
            };
            button.MouseLeave += (_, _) =>
            {
                StartShapeFlyoutVisibilityCheck();
            };
            button.Click += (_, _) =>
            {
                ActivateTool(tool);
                HideShapeToolFlyout();
            };
            _shapeFlyoutButtons[tool] = button;
            _shapeToolFlyout.Controls.Add(button);
        }
    }

    private void ShowShapeToolFlyout()
    {
        if (_shapeToolButton is null || _shapeToolFlyout is null || _shapeToolButton.Parent is not Control tools) return;
        _shapeFlyoutHideAtUtc = default;
        tools.PerformLayout();
        _toolTip.HideTip();
        _shapeToolFlyout.Left = tools.Left + tools.Width - 1;
        _shapeToolFlyout.Top = tools.Top + _shapeToolButton.Top;
        _shapeToolFlyout.Visible = true;
        _shapeToolFlyout.BringToFront();
        StartShapeFlyoutVisibilityCheck();
        RefreshToolButtons();
    }

    private void HideShapeToolFlyout()
    {
        _shapeFlyoutHideTimer.Stop();
        _shapeFlyoutHideAtUtc = default;
        if (_shapeToolFlyout is null) return;
        _shapeToolFlyout.Visible = false;
    }

    private void ScheduleShapeToolFlyoutHideAfter(int delayMilliseconds)
    {
        if (_shapeToolFlyout is null) return;
        _shapeFlyoutHideAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, delayMilliseconds));
        StartShapeFlyoutVisibilityCheck();
    }

    private void StartShapeFlyoutVisibilityCheck()
    {
        if (_shapeToolFlyout is null || !_shapeToolFlyout.Visible) return;
        if (!_shapeFlyoutHideTimer.Enabled) _shapeFlyoutHideTimer.Start();
    }

    private void UpdateShapeToolFlyoutVisibility()
    {
        if (_shapeToolButton is null || _shapeToolFlyout is null || !_shapeToolFlyout.Visible)
        {
            _shapeFlyoutHideTimer.Stop();
            return;
        }

        if (_shapeFlyoutHideAtUtc != default && DateTime.UtcNow >= _shapeFlyoutHideAtUtc)
        {
            HideShapeToolFlyout();
            return;
        }

        if (_shapeFlyoutHideAtUtc != default) return;
        if (!IsPointerInsideShapeToolFlyout()) HideShapeToolFlyout();
    }

    private bool IsPointerInsideShapeToolFlyout()
    {
        if (_shapeToolButton is null || _shapeToolFlyout is null) return false;
        var keepOpen = Rectangle.Union(
            _shapeToolButton.RectangleToScreen(_shapeToolButton.ClientRectangle),
            _shapeToolFlyout.RectangleToScreen(_shapeToolFlyout.ClientRectangle));
        keepOpen.Inflate(8, 8);
        return keepOpen.Contains(Cursor.Position);
    }

    private void ActivateTool(ToolMode tool)
    {
        if (DrawingToolsBlocked() && IsBasicDrawingOnlyTool(tool)) return;
        RememberToolStrokeWidth(_tool);
        _tool = tool;
        _stage.ClearDrawingPreview();
        CancelFreehandStroke();
        if (!IsShapeTool(tool)) HideShapeToolFlyout();
        ApplyToolStrokeWidth(tool);
        if (ToolShapeKind(tool) is { } shape)
        {
            if (IsShapeTool(tool)) _activeShapeTool = tool;
            _drawSettings.ShapeKind = shape;
            _drawSettings.NotifyChanged();
        }

        RefreshToolButtons();
        ApplyToolCursor();
    }

    private void RememberToolStrokeWidth(ToolMode tool)
    {
        var width = (float)_materialEditor.StrokeWidth;
        if (tool == ToolMode.Pencil) _pencilStrokeWidthPoints = width;
        else if (tool == ToolMode.Brush) _brushStrokeWidthPoints = width;
        else if (IsStandardStrokeTool(tool)) _standardStrokeWidthPoints = width;
    }

    private void ApplyToolStrokeWidth(ToolMode tool)
    {
        float? width = tool switch
        {
            ToolMode.Pencil => _pencilStrokeWidthPoints,
            ToolMode.Brush => _brushStrokeWidthPoints,
            _ when IsStandardStrokeTool(tool) => _standardStrokeWidthPoints,
            _ => null
        };
        if (width is null || Math.Abs(_materialEditor.StrokeWidth - width.Value) < 0.001f) return;

        _materialEditor.SetMaterial(_materialEditor.Fill, _materialEditor.Stroke, width.Value, _materialEditor.Opacity);
        _updatingStrokeInput = true;
        try
        {
            _stroke.Value = (decimal)Math.Clamp(width.Value, (float)_stroke.Minimum, (float)_stroke.Maximum);
        }
        finally
        {
            _updatingStrokeInput = false;
        }
    }

    private void ApplyToolCursor()
    {
        _stage.Cursor = IsDrawingTool(_tool) ? Cursors.Cross : _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
    }

    private void HookEvents()
    {
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _timeline.CurrentFrameChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            StopPlayback();
            SetFrame(_timeline.CurrentFrame);
        };
        _timeline.ActiveLayerChanged += (_, _) =>
        {
            if (IsNestedInstanceTimelineTrackActive() && IsBasicDrawingOnlyTool(_tool))
            {
                ActivateTool(ToolMode.Select);
            }

            RefreshToolButtons();
            UpdateInspector();
            _stage.Invalidate();
        };
        _timeline.LayerVisibilityChanged += (_, _) =>
        {
            if (IsSceneCompositionContext()) RebuildSceneComposition();
            else RebuildDrawingObjectUnderlay();
            ClearInactiveSelection();
            UpdateInspector();
            _stage.Invalidate();
        };
        _sceneEditorPanel.AddSceneRequested += (_, _) => AddScene();
        _sceneEditorPanel.AddDrawingObjectRequested += (_, _) => AddDrawingObject();
        _sceneEditorPanel.AddSceneInstanceRequested += (_, _) => AddSceneInstanceFromActiveDrawingObject();
        _sceneEditorPanel.SceneSelectionChanged += (_, e) => SelectScene(e.Index);
        _sceneEditorPanel.DrawingObjectSelectionChanged += (_, e) => SelectDrawingObject(e.Index);
        _sceneEditorPanel.DrawingObjectOpenRequested += (_, e) => OpenDrawingObjectEditor(e.DrawingObjectId);
        _sceneEditorPanel.SceneSettingsChanged += (_, e) => UpdateSceneSettings(e);
        _playbackSettings.FpsChanged += (_, _) =>
        {
            _playbackAccumulator = 0;
            UpdateStatusBar();
        };
        _playbackSettings.FrameRangeChanged += (_, _) =>
        {
            var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
            var clampedEnd = Math.Clamp(_playbackSettings.EndFrame, 0, timelineLastFrame);
            var clampedStart = Math.Clamp(_playbackSettings.StartFrame, 0, clampedEnd);
            if (clampedStart != _playbackSettings.StartFrame || clampedEnd != _playbackSettings.EndFrame)
            {
                _playbackSettings.SetFrameRange(clampedStart, clampedEnd);
                return;
            }

            _timeline.StartFrame = _playbackSettings.StartFrame;
            _timeline.EndFrame = _playbackSettings.EndFrame;
            SyncFrameSliderRange();
            SetFrame(_frame);
        };
        _materialEditor.MaterialChanged += (_, e) =>
        {
            if (IsSceneCompositionContext()) return;
            if (_tool == ToolMode.Pencil) _pencilStrokeWidthPoints = e.StrokeWidth;
            else if (_tool == ToolMode.Brush) _brushStrokeWidthPoints = e.StrokeWidth;
            else if (IsStandardStrokeTool(_tool)) _standardStrokeWidthPoints = e.StrokeWidth;

            _updatingStrokeInput = true;
            try
            {
                _stroke.Value = (decimal)Math.Clamp(e.StrokeWidth, (float)_stroke.Minimum, (float)_stroke.Maximum);
            }
            finally
            {
                _updatingStrokeInput = false;
            }

            if (_selectedElements.Count > 0)
            {
                ApplyMaterialToSelectedElements(e, VectorUnits.StrokePointsToUnits(e.StrokeWidth));
                return;
            }

            if (_selectedObjects.Count > 1)
            {
                ApplyMaterialToSelectedObjects(e, VectorUnits.StrokePointsToUnits(e.StrokeWidth));
                return;
            }

            if (_selectedObject >= 0 && _selectedObject < _scene.ObjectCount)
            {
                var strokeUnits = VectorUnits.StrokePointsToUnits(e.StrokeWidth);
                var selectedKind = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == _selectedObject
                    ? _selectedElement.Key.Kind
                    : DrawingElementKind.None;
                if (!MaterialChangeAffectsSelection(_selectedObject, selectedKind, e, strokeUnits)) return;
                CaptureUndoSnapshot();
                var editedObject = _selectedObject;
                if (selectedKind != DrawingElementKind.None)
                {
                    var materialized = _scene.DetachElementForMove(_selectedElement, _frame);
                    if (!materialized.IsValid)
                    {
                        if (_undoStack.Count > 0) _undoStack.Pop();
                        return;
                    }

                    SetSelection(materialized);
                    editedObject = materialized.Key.ObjectIndex;
                }

                var shape = _scene.ShapeKind[editedObject];
                var alpha = (int)Math.Clamp(e.Opacity * 255, 0, 255);
                if (selectedKind == DrawingElementKind.Fill)
                {
                    _scene.Argb[editedObject] = Color.FromArgb(alpha, e.Fill).ToArgb();
                    _scene.Stroke[editedObject] = 0;
                    var beforeMergeCount = _scene.ObjectCount;
                    var merged = _scene.MergeSameColorFillsAround(editedObject, frame: _frame);
                    if (_scene.ObjectCount != beforeMergeCount || merged != editedObject) SetSelection(merged);
                }
                else if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
                {
                    var color = Color.FromArgb(alpha, e.Stroke);
                    _scene.Argb[editedObject] = Color.FromArgb(0, color).ToArgb();
                    _scene.StrokeArgb[editedObject] = color.ToArgb();
                    if (IsFreehandShape(shape))
                    {
                        _scene.UpdateFreehandStrokeWidth(editedObject, strokeUnits);
                    }
                    else
                    {
                        _scene.Stroke[editedObject] = strokeUnits;
                        _scene.Height[editedObject] = Math.Max(VectorUnits.FromPixels(3), strokeUnits + VectorUnits.FromPixels(2));
                        _scene.RebuildGeometryIndex();
                    }
                }
                else if (_tool == ToolMode.Brush && IsFillShape(shape))
                {
                    _scene.Argb[editedObject] = Color.FromArgb(alpha, e.Fill).ToArgb();
                    _scene.Stroke[editedObject] = 0;
                    _scene.StrokeArgb[editedObject] = Color.Transparent.ToArgb();
                    var beforeMergeCount = _scene.ObjectCount;
                    var merged = _scene.MergeSameColorFillsAround(editedObject, frame: _frame);
                    if (_scene.ObjectCount != beforeMergeCount || merged != editedObject) SetSelection(merged);
                }
                else if (shape == ShapeKind.BrushStroke)
                {
                    var color = Color.FromArgb(alpha, e.Fill);
                    _scene.Argb[editedObject] = color.ToArgb();
                    _scene.StrokeArgb[editedObject] = color.ToArgb();
                    _scene.UpdateFreehandStrokeWidth(editedObject, strokeUnits);
                }
                else if (shape == ShapeKind.Freeform)
                {
                    var color = Color.FromArgb(alpha, e.Stroke);
                    _scene.Argb[editedObject] = Color.FromArgb(0, color).ToArgb();
                    _scene.StrokeArgb[editedObject] = color.ToArgb();
                    _scene.UpdateFreehandStrokeWidth(editedObject, strokeUnits);
                }
                else
                {
                    _scene.Argb[editedObject] = Color.FromArgb(alpha, e.Fill).ToArgb();
                    _scene.Stroke[editedObject] = strokeUnits;
                    _scene.StrokeArgb[editedObject] = Color.FromArgb(alpha, e.Stroke).ToArgb();
                    if (shape == ShapeKind.Line)
                    {
                        _scene.Height[editedObject] = Math.Max(VectorUnits.FromPixels(3), strokeUnits + VectorUnits.FromPixels(2));
                        _scene.RebuildGeometryIndex();
                    }
                    else
                    {
                        var beforeMergeCount = _scene.ObjectCount;
                        var merged = _scene.MergeSameColorFillsAround(editedObject, frame: _frame);
                        if (_scene.ObjectCount != beforeMergeCount || merged != editedObject) SetSelection(merged);
                    }
                }

                _hierarchyPanel.RefreshScene();
                _stage.Invalidate();
            }
        };
        _hierarchyPanel.HierarchySelectionChanged += (_, e) =>
        {
            if (e.Kind == HierarchyNodeKind.Layer && e.Index >= 0 && e.Index < _scene.LayerCount)
            {
                _scene.ActiveLayer = e.Index;
                RefreshLayers();
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
                if (IsShapeTool(tool)) _activeShapeTool = tool;
                else HideShapeToolFlyout();
                RefreshToolButtons();
            }
        };

        _play.Click += (_, _) => TogglePlayback();
        _frameSlider.ValueChanged += (_, _) =>
        {
            if (_syncingFrame) return;
            SetFrame(_frameSlider.Value);
        };
        _color.SelectedIndexChanged += (_, _) => _materialEditor.Fill = PaletteColor(_color.SelectedIndex);
        _stroke.ValueChanged += (_, _) =>
        {
            if (_updatingStrokeInput) return;
            _materialEditor.StrokeWidth = (float)_stroke.Value;
        };
        _stage.MouseWheel += (_, e) =>
        {
            if (IsScene3DView())
            {
                if (IsControlPressed()) _stage.ZoomReferenceCamera(e.Delta > 0 ? 1.12f : 0.89f);
                else _stage.DollyReferenceCamera(e.Delta);
                UpdateStatusBar();
                return;
            }

            _stage.ZoomAt(e.Location, e.Delta > 0 ? 1.12f : 0.89f);
            UpdateStatusBar();
        };
        _stage.FrameRendered += (_, _) => _rendersThisSample++;
        _stage.MouseDown += StageMouseDown;
        _stage.MouseDoubleClick += StageMouseDoubleClick;
        _stage.MouseMove += StageMouseMove;
        _stage.MouseUp += StageMouseUp;
        _stage.MouseCaptureChanged += StageMouseCaptureChanged;
        _stage.DragEnter += StageDragEnter;
        _stage.DragDrop += StageDragDrop;
        Deactivate += (_, _) => FinishLostPointerCapture();
    }

    private void Generate()
    {
        if (IsSceneCompositionContext())
        {
            AppLog.Info("Stress generation is disabled for non-drawable scene compositions.");
            return;
        }

        AppLog.Info("Generating stress scene");
        Cursor = Cursors.WaitCursor;
        try
        {
            _scene.Generate(1000, 100000, 100000000);
            ResetEditHistory();
            _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
            SyncFrameSliderRange();
            SetFrame(0);
            ClearSelection();
            RefreshLayers();
            _hierarchyPanel.BindScene(_scene);
            _sceneEditorPanel.BindScene(_scene);
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
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
        _project = VectorProject.CreateEmpty();
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = 0;
        _activeDrawingObjectIndex = 0;
        _scene = _drawingObjects[0].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        BindActiveDrawingObjectScene(resetView: true);
        AppLog.Info("New empty project created with one empty drawing object");
    }

    private void ResetEditHistory()
    {
        ResetUndoHistory();
        _clipboardObjects.Clear();
    }

    private void ResetUndoHistory()
    {
        _undoStack.Clear();
        _sceneTimelineUndoStack.Clear();
        _undoCapturedForPointerEdit = false;
    }

    private void RefreshLayers()
    {
        _timeline.RefreshTimeline();
    }

    private void Tick()
    {
        var measuredElapsed = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        var elapsed = _playing
            ? Math.Min(measuredElapsed, MaxFrameSeconds)
            : Math.Min(measuredElapsed, 1.0);
        _clock.Restart();

        if (_playing)
        {
            var updateCount = _updateBatcher.Consume(elapsed);
            if (updateCount > 0)
            {
                _updatesThisSample += UpdateSimulation(updateCount, _updateBatcher.StepSeconds);
            }
        }
        else
        {
            _updateBatcher.Reset();
        }

        var metricsElapsed = _metricsClock.Elapsed.TotalSeconds;
        if (metricsElapsed >= MetricsRefreshSeconds)
        {
            var instantRenderFps = _rendersThisSample / metricsElapsed;
            var instantUps = _updatesThisSample / metricsElapsed;
            _smoothedFps = _smoothedFps <= 0 ? instantRenderFps : _smoothedFps * 0.72 + instantRenderFps * 0.28;
            _smoothedUps = _smoothedUps <= 0 ? instantUps : _smoothedUps * 0.72 + instantUps * 0.28;
            _rendersThisSample = 0;
            _updatesThisSample = 0;
            _metricsClock.Restart();
            UpdatePerformanceMetrics();
        }
    }

    private int UpdateSimulation(int updateCount, double fixedDeltaSeconds)
    {
        if (!_playing || updateCount <= 0) return 0;

        var frameStep = 1.0 / Math.Max(1, _playbackSettings.Fps);
        var start = _playbackSettings.StartFrame;
        var end = _playbackSettings.EndFrame;
        var nextFrame = Math.Clamp(_frame, start, end);
        var processedUpdates = 0;
        var stopAtEnd = false;
        for (var update = 0; update < updateCount; update++)
        {
            processedUpdates++;
            _playbackAccumulator += fixedDeltaSeconds;
            var framesToAdvance = (int)(_playbackAccumulator / frameStep);
            if (framesToAdvance <= 0) continue;

            _playbackAccumulator -= framesToAdvance * frameStep;
            if (_playbackSettings.LoopPlayback)
            {
                var span = Math.Max(1, end - start + 1);
                nextFrame = start + (int)(((long)nextFrame - start + framesToAdvance) % span);
                continue;
            }

            nextFrame = (int)Math.Min(end, (long)nextFrame + framesToAdvance);
            if (nextFrame < end) continue;
            stopAtEnd = true;
            break;
        }

        if (nextFrame != _frame) SetFrame(nextFrame, invalidate: false);
        if (stopAtEnd) StopPlayback();
        return processedUpdates;
    }

    private void TogglePlayback()
    {
        if (_playing)
        {
            StopPlayback();
            return;
        }

        _playing = true;
        _updateBatcher.Reset();
        _playbackAccumulator = 0;
        _timer.Interval = PlaybackTimerIntervalMs;
        _clock.Restart();
        _play.Text = "Pause";
        _timeline.IsPlaying = true;
    }

    private void StopPlayback()
    {
        if (!_playing) return;
        _playing = false;
        _updateBatcher.Reset();
        _playbackAccumulator = 0;
        _timer.Interval = IdleTimerIntervalMs;
        _clock.Restart();
        _play.Text = "Play";
        _timeline.IsPlaying = false;
    }

    private void UpdatePerformanceMetrics()
    {
        var stats = _stage.LastStats;
        SetLabelText(_fps, $"FPS {_smoothedFps:0}");
        SetLabelText(
            _draw,
            stats.TileLod
                ? stats.VisibleObjects > 0 || stats.DrawnObjects > 0
                    ? $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)} + Tiles {CompactFormat.Number(stats.TileDraws)}"
                    : $"Tiles {CompactFormat.Number(stats.TileDraws)}"
                : $"Draw {CompactFormat.Number(stats.DrawnObjects)} / {CompactFormat.Number(stats.VisibleObjects)}");
        var totalAtoms = _scene.VirtualAtomCount;
        if (_stage.UnderlayScene is { } underlay && !ReferenceEquals(underlay, _scene)) totalAtoms += underlay.VirtualAtomCount;
        SetLabelText(_atoms, $"Atoms {CompactFormat.Number(stats.VisibleAtoms)} / {CompactFormat.Number(totalAtoms)}");
        SetLabelText(_zoom, $"Zoom {_stage.Zoom * 100:0}%");
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        SetToolStripText(_renderFpsStatus, $"Render FPS {_smoothedFps:0}");
        SetToolStripText(_animationFpsStatus, $"UPS {_smoothedUps:0}/{TargetUps:0}  Animation FPS {_playbackSettings.Fps}");
        SetToolStripText(_zoomStatus, $"Zoom {_stage.Zoom * 100:0}%");
    }

    private static void SetLabelText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
    }

    private static void SetToolStripText(ToolStripItem item, string text)
    {
        if (!string.Equals(item.Text, text, StringComparison.Ordinal)) item.Text = text;
    }

    private void SetFrame(int frame, bool invalidate = true)
    {
        var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var maximum = Math.Min(_playbackSettings.EndFrame, timelineLastFrame);
        var minimum = Math.Min(_playbackSettings.StartFrame, maximum);
        var next = Math.Clamp(frame, minimum, maximum);
        var frameChanged = next != _frame;
        if (frameChanged) FinishPointerInteractionForFrameChange();
        _syncingFrame = true;
        try
        {
            _frame = next;
            if (IsSceneCompositionContext())
            {
                RebuildSceneComposition();
            }
            else
            {
                _scene.EditFrame = next;
                RebuildDrawingObjectUnderlay();
            }
            if (_frameSlider.Minimum <= next && next <= _frameSlider.Maximum) _frameSlider.Value = next;
            _timeline.CurrentFrame = next;
            _stage.Frame = next;
            if (frameChanged && (_selectedElements.Count > 0 || IsSceneCompositionContext())) ClearSelection();
            ClearInactiveSelection();
        }
        finally
        {
            _syncingFrame = false;
        }

        if (invalidate) _stage.Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var focusedEditor = ContainsFocusedEditor(this);
        if (!focusedEditor && IsTimelineEditShortcut(keyData) && HandleTimelineShortcut(keyData)) return true;
        if (!focusedEditor)
        {
            if (IsScene3DView() && HandleBlender3DShortcut(keyData)) return true;
            if (keyData == Keys.Escape && _freehandDrawing)
            {
                CancelFreehandStroke();
                FinishPointerInteraction();
                return true;
            }
            if (HandleTimelineShortcut(keyData)) return true;
            if (keyData == Keys.V)
            {
                ActivateTool(ToolMode.Select);
                return true;
            }

            if (keyData == Keys.H)
            {
                ActivateTool(ToolMode.Hand);
                return true;
            }
            if (keyData == Keys.N && ActivateDrawingShortcut(ToolMode.Line)) return true;
            if (keyData == Keys.Y && ActivateDrawingShortcut(ToolMode.Pencil)) return true;
            if (keyData == Keys.B && ActivateDrawingShortcut(ToolMode.Brush)) return true;
            if (keyData == Keys.K && ActivateDrawingShortcut(ToolMode.Fill)) return true;
            if (keyData == Keys.OemOpenBrackets && AdjustFreehandWidth(increase: false)) return true;
            if (keyData == Keys.Oem6 && AdjustFreehandWidth(increase: true)) return true;
            if (keyData == Keys.Tab && CycleShapeTool(reverse: false)) return true;
            if (keyData == (Keys.Shift | Keys.Tab) && CycleShapeTool(reverse: true)) return true;
            if (keyData == (Keys.Control | Keys.Z) && UndoLastEdit()) return true;
            if (keyData == (Keys.Control | Keys.C) && CopySelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.V) && PasteCopiedObjects()) return true;
        }

        if (keyData == Keys.Delete && !focusedEditor && DeleteSelectedObject()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsTimelineEditShortcut(Keys keyData)
    {
        return keyData is Keys.F5
            or (Keys.Shift | Keys.F5)
            or Keys.F6
            or (Keys.Shift | Keys.F6)
            or Keys.F7;
    }

    private bool HandleTimelineShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter:
                TogglePlayback();
                return true;
            case Keys.Oemcomma:
                StopPlayback();
                SetFrame(_frame - 1);
                return true;
            case Keys.OemPeriod:
                StopPlayback();
                SetFrame(_frame + 1);
                return true;
            case Keys.Shift | Keys.Oemcomma:
                StopPlayback();
                SetFrame(_playbackSettings.StartFrame);
                return true;
            case Keys.Shift | Keys.OemPeriod:
                StopPlayback();
                SetFrame(_playbackSettings.EndFrame);
                return true;
            case Keys.F5:
                return ExecuteTimelineEdit(TimelineEditKind.InsertFrame);
            case Keys.Shift | Keys.F5:
                return ExecuteTimelineEdit(TimelineEditKind.RemoveFrame);
            case Keys.F6:
                return ExecuteTimelineEdit(TimelineEditKind.InsertKeyframe);
            case Keys.Shift | Keys.F6:
                return ExecuteTimelineEdit(TimelineEditKind.ClearKeyframe);
            case Keys.F7:
                return ExecuteTimelineEdit(TimelineEditKind.InsertBlankKeyframe);
            default:
                return false;
        }
    }

    private bool ExecuteTimelineEdit(TimelineEditKind edit)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var trackId = _timeline.ActiveTrackId;
        if (string.IsNullOrWhiteSpace(trackId)) return false;

        var timeline = context.Timeline;
        var track = timeline.FindTrack(trackId);
        if (track is null) return false;

        VectorSceneSnapshot? vectorSnapshot = null;
        AnimationTimelineSnapshot? sceneSnapshot = null;
        SceneDefinition? sceneDefinition = null;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            vectorSnapshot = drawingScene.CreateSnapshot();
        }
        else if (context is SceneDefinition scene)
        {
            sceneDefinition = scene;
            sceneSnapshot = timeline.CreateSnapshot();
        }

        bool changed;
        var layer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if (drawingScene is not null && layer >= 0)
        {
            changed = edit switch
            {
                TimelineEditKind.InsertFrame => drawingScene.InsertTimelineFrame(layer, _frame),
                TimelineEditKind.RemoveFrame => drawingScene.RemoveTimelineFrame(layer, _frame),
                TimelineEditKind.InsertKeyframe => drawingScene.InsertTimelineKeyframe(layer, _frame),
                TimelineEditKind.InsertBlankKeyframe => drawingScene.InsertTimelineBlankKeyframe(layer, _frame),
                TimelineEditKind.ClearKeyframe => drawingScene.ClearTimelineKeyframe(layer, _frame),
                _ => false
            };
        }
        else
        {
            changed = edit switch
            {
                TimelineEditKind.InsertFrame => timeline.InsertFrame(trackId, _frame),
                TimelineEditKind.RemoveFrame => timeline.RemoveFrame(trackId, _frame),
                TimelineEditKind.InsertKeyframe => timeline.InsertKeyframe(trackId, _frame),
                TimelineEditKind.InsertBlankKeyframe => timeline.InsertBlankKeyframe(trackId, _frame),
                TimelineEditKind.ClearKeyframe => timeline.ClearKeyframe(trackId, _frame),
                _ => false
            };
        }

        if (!changed) return false;
        if (vectorSnapshot is not null) PushUndoSnapshot(vectorSnapshot);
        if (sceneDefinition is not null && sceneSnapshot is not null)
        {
            PushSceneTimelineUndo(sceneDefinition, sceneSnapshot);
        }

        StopPlayback();
        ClearSelection();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousLastFrame);
        if (drawingScene is not null)
        {
            _hierarchyPanel.RefreshScene();
            if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
            _stage.Invalidate();
        }
        else
        {
            RebuildSceneComposition();
        }

        UpdateInspector();
        return true;
    }

    private enum TimelineEditKind
    {
        InsertFrame,
        RemoveFrame,
        InsertKeyframe,
        InsertBlankKeyframe,
        ClearKeyframe
    }

    private bool ActivateDrawingShortcut(ToolMode tool)
    {
        if (DrawingToolsBlocked() && IsBasicDrawingOnlyTool(tool)) return false;
        ActivateTool(tool);
        return true;
    }

    private bool AdjustFreehandWidth(bool increase)
    {
        if (!IsFreehandTool(_tool)) return false;
        var current = Math.Max(0.5f, _materialEditor.StrokeWidth);
        var next = increase ? current * 1.25f : current / 1.25f;
        _materialEditor.StrokeWidth = Math.Clamp(next, 0.5f, 32f);
        return true;
    }

    private bool HandleBlender3DShortcut(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var control = (keyData & Keys.Control) == Keys.Control;
        switch (key)
        {
            case Keys.NumPad1:
                _stage.SetReferenceCameraOrientation(control ? MathF.PI : 0, 0);
                UpdateStatusBar();
                return true;
            case Keys.NumPad3:
                _stage.SetReferenceCameraOrientation(control ? -MathF.PI / 2f : MathF.PI / 2f, 0);
                UpdateStatusBar();
                return true;
            case Keys.NumPad7:
                _stage.SetReferenceCameraOrientation(0, control ? -1.5f : 1.5f);
                UpdateStatusBar();
                return true;
            case Keys.NumPad5:
                ToggleActiveSceneProjection();
                return true;
            case Keys.Home:
                _stage.ResetReferenceCameraView();
                UpdateStatusBar();
                return true;
            default:
                return false;
        }
    }

    private void ToggleActiveSceneProjection()
    {
        var scene = ActiveScene();
        if (scene is null) return;
        scene.Camera.Projection = scene.Camera.Projection == CameraProjection.Perspective
            ? CameraProjection.Orthographic
            : CameraProjection.Perspective;
        _stage.ConfigureReferenceView(scene);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        UpdateStatusBar();
    }

    private bool CycleShapeTool(bool reverse)
    {
        if (IsSceneCompositionContext()) return false;
        var index = Array.IndexOf(_shapeTools, _activeShapeTool);
        if (index < 0) index = 0;
        var next = reverse
            ? (index - 1 + _shapeTools.Length) % _shapeTools.Length
            : (index + 1) % _shapeTools.Length;
        ActivateTool(_shapeTools[next]);
        ShowShapeToolFlyout();
        ScheduleShapeToolFlyoutHideAfter(2000);
        return true;
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

    private void ApplyBoundTimelineDuration(int previousLastFrame = -1)
    {
        var lastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var followTimelineEnd = previousLastFrame >= 0 && _playbackSettings.EndFrame >= previousLastFrame;
        var endFrame = followTimelineEnd
            ? lastFrame
            : Math.Clamp(_playbackSettings.EndFrame, 0, lastFrame);
        var startFrame = Math.Clamp(_playbackSettings.StartFrame, 0, endFrame);
        if (startFrame != _playbackSettings.StartFrame || endFrame != _playbackSettings.EndFrame)
        {
            _playbackSettings.SetFrameRange(startFrame, endFrame);
        }
        else
        {
            SyncFrameSliderRange();
            SetFrame(Math.Clamp(_frame, startFrame, endFrame));
        }
    }

    private void CaptureUndoSnapshot()
    {
        PushUndoSnapshot(_scene.CreateSnapshot());
    }

    private void PushUndoSnapshot(VectorSceneSnapshot snapshot)
    {
        _undoStack.Push(snapshot);
        while (_undoStack.Count > MaxUndoSnapshots)
        {
            var snapshots = _undoStack.Take(MaxUndoSnapshots).Reverse().ToArray();
            _undoStack.Clear();
            foreach (var item in snapshots) _undoStack.Push(item);
        }
    }

    private void PushSceneTimelineUndo(SceneDefinition scene, AnimationTimelineSnapshot snapshot)
    {
        _sceneTimelineUndoStack.Push(new SceneTimelineUndoEntry(scene, snapshot));
        while (_sceneTimelineUndoStack.Count > MaxUndoSnapshots)
        {
            var snapshots = _sceneTimelineUndoStack.Take(MaxUndoSnapshots).Reverse().ToArray();
            _sceneTimelineUndoStack.Clear();
            foreach (var item in snapshots) _sceneTimelineUndoStack.Push(item);
        }
    }

    private void CapturePointerUndoSnapshot()
    {
        if (_undoCapturedForPointerEdit) return;
        CaptureUndoSnapshot();
        _undoCapturedForPointerEdit = true;
    }

    private bool UndoLastEdit()
    {
        if (IsSceneCompositionContext()
            && _timeline.Context is SceneDefinition activeScene
            && _sceneTimelineUndoStack.TryPeek(out var timelineUndo)
            && ReferenceEquals(timelineUndo.Scene, activeScene))
        {
            var previousLastFrame = Math.Max(0, activeScene.FrameCount - 1);
            _sceneTimelineUndoStack.Pop();
            activeScene.Timeline.RestoreSnapshot(timelineUndo.Snapshot);
            activeScene.SynchronizeTimelineTracks();
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration(previousLastFrame);
            RebuildSceneComposition();
            ClearSelection();
            UpdateInspector();
            return true;
        }

        if (IsSceneCompositionContext()) return false;
        if (_undoStack.Count == 0) return false;
        var previousDrawingLastFrame = Math.Max(0, _scene.FrameCount - 1);
        _scene.RestoreSnapshot(_undoStack.Pop());
        _scene.EditFrame = _frame;
        ClearSelection();
        _geometryDirty = false;
        CancelFreehandStroke();
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousDrawingLastFrame);
        RebuildDrawingObjectUnderlay();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool CopySelectedObjects()
    {
        if (IsSceneCompositionContext()) return false;
        var targets = _selectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .ToArray();
        if (targets.Length == 0
            && _selectedObject >= 0
            && _selectedObject < _scene.ObjectCount
            && _scene.IsObjectActive(_selectedObject, _frame))
        {
            targets = new[] { _selectedObject };
        }
        if (targets.Length == 0) return false;

        VectorSceneSnapshot? restoreAfterCopy = null;
        if (_selectedElements.Count > 0)
        {
            restoreAfterCopy = _scene.CreateSnapshot();
            var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success)
            {
                _scene.RestoreSnapshot(restoreAfterCopy);
                return false;
            }

            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }

        try
        {
            _clipboardObjects.Clear();
            foreach (var index in targets)
            {
                PointF[][]? pathContours = null;
                if (_scene.ShapeKind[index] == ShapeKind.Path && _scene.TryGetPathWorldContours(index, out var contours)) pathContours = contours;
                else if (IsFreehandShape(_scene.ShapeKind[index]) && _scene.TryGetFreehandWorldPoints(index, out var freehandPoints)) pathContours = new[] { freehandPoints };
                _clipboardObjects.Add(new ClipboardObject(
                    _scene.ObjectLayer[index],
                    new PointF(_scene.X[index], _scene.Y[index]),
                    new SizeF(_scene.Width[index], _scene.Height[index]),
                    _scene.Angle[index],
                    _scene.Stroke[index],
                    Color.FromArgb(_scene.Argb[index]),
                    Color.FromArgb(_scene.StrokeArgb[index]),
                    _scene.AtomCount[index],
                    _scene.ShapeKind[index],
                    new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]),
                    pathContours));
            }

            return _clipboardObjects.Count > 0;
        }
        finally
        {
            if (restoreAfterCopy is not null)
            {
                _scene.RestoreSnapshot(restoreAfterCopy);
                SyncSelectionToStage();
                _stage.Invalidate();
            }
        }
    }

    private bool PasteCopiedObjects()
    {
        if (DrawingToolsBlocked()) return false;
        if (_clipboardObjects.Count == 0) return false;
        CaptureUndoSnapshot();
        var pasted = new List<int>(_clipboardObjects.Count);
        foreach (var item in _clipboardObjects)
        {
            var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
            var layer = Math.Clamp(item.Layer, 0, Math.Max(0, _scene.LayerCount - 1));
            int index;
            if (item.Shape == ShapeKind.Path && item.PathWorldContours is { Length: > 0 } pathContours)
            {
                var shifted = pathContours
                    .Select(contour => contour.Select(point => new PointF(point.X + offset.X, point.Y + offset.Y)).ToArray())
                    .ToArray();
                index = _scene.AddPathObjectContours(layer, shifted, item.Stroke, item.FillColor, item.StrokeColor, item.Atoms);
            }
            else if (IsFreehandShape(item.Shape) && item.PathWorldContours is { Length: > 0 } freehandContours)
            {
                var shifted = freehandContours[0]
                    .Select(point => new PointF(point.X + offset.X, point.Y + offset.Y))
                    .ToArray();
                var color = item.Shape == ShapeKind.BrushStroke ? item.FillColor : item.StrokeColor;
                index = _scene.AddFreehandStroke(layer, shifted, item.Stroke, color, item.Shape == ShapeKind.BrushStroke, item.Atoms);
            }
            else
            {
                var center = new PointF(item.Center.X + offset.X, item.Center.Y + offset.Y);
                index = _scene.AddObject(layer, center, item.Size, item.Angle, item.Stroke, item.FillColor, item.StrokeColor, item.Atoms, item.Shape);
                if (item.Shape == ShapeKind.Line)
                {
                    _scene.CurveControlX[index] = item.CurveControl.X + offset.X;
                    _scene.CurveControlY[index] = item.CurveControl.Y + offset.Y;
                }
            }

            if (index >= 0) pasted.Add(index);
        }

        if (pasted.Count == 0) return false;
        SetSelection(pasted);
        MergeSelectedFillsAfterGeometryEdit();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private void StageMouseDown(object? sender, MouseEventArgs e)
    {
        if (IsScene3DView() && MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return;
        }

        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return;
        }

        if (IsSceneCompositionContext())
        {
            return;
        }

        if (IsNestedInstanceTimelineTrackActive() && IsBasicDrawingOnlyTool(_tool)) return;

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        _pointerHitWasAlreadySelected = false;
        _selectionWasEmptyOnPointerDown = _selectedObjects.Count == 0;
        _forceMarqueeOnPointerDown = e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control;
        _pendingClickSelection = DrawingElementHit.None;
        if (_tool == ToolMode.Select)
        {
            if (_forceMarqueeOnPointerDown)
            {
                return;
            }

            if (_selectedObject >= 0 && !_selectedElement.IsValid)
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

            if (_startWorld is not { } startWorld) return;
            var hit = _scene.HitTestElement(startWorld, _frame, SelectionToleranceWorld());
            if (hit.IsValid)
            {
                if (_selectionWasEmptyOnPointerDown && e.Button == MouseButtons.Left)
                {
                    _pendingClickSelection = hit;
                    UpdateInspector();
                    _stage.Invalidate();
                    return;
                }

                var hitObject = hit.Key.ObjectIndex;
                var hitPartWasAlreadySelected = _selectedElements.Any(selected => selected.Key == hit.Key);
                var hitWholeObjectWasAlreadySelected = _selectedElements.Count == 0 && _selectedObjects.Contains(hitObject);
                _pointerHitWasAlreadySelected = hitPartWasAlreadySelected || hitWholeObjectWasAlreadySelected;
                if (hitPartWasAlreadySelected)
                {
                    _selectedObject = hitObject;
                    _selectedElement = _selectedElements.First(selected => selected.Key == hit.Key);
                    SyncSelectionToStage();
                }
                else if (hitWholeObjectWasAlreadySelected)
                {
                    _selectedObject = hitObject;
                    _selectedElement = DrawingElementHit.None;
                    SyncSelectionToStage();
                }
                else SetSelection(hit);

                CaptureEditStart(_selectedObject);
            }
            else
            {
                ClearSelection();
                if (e.Button == MouseButtons.Left) BeginMarqueeFromPendingSelection(e.Location);
            }

            UpdateInspector();
            _stage.Invalidate();
        }
        else if (_tool == ToolMode.Fill)
        {
            var hit = _scene.HitTestElement(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (hit.IsValid && hit.Key.Kind == DrawingElementKind.Fill && IsFillShape(_scene.ShapeKind[hit.Key.ObjectIndex]))
            {
                var color = ActiveColor();
                if (_scene.Argb[hit.Key.ObjectIndex] == color.ToArgb()) return;
                var snapshot = _scene.CreateSnapshot();
                var materialized = hit.Key.Kind == DrawingElementKind.Fill
                    ? _scene.DetachElementForMove(hit, _frame)
                    : hit;
                if (!materialized.IsValid)
                {
                    _scene.RestoreSnapshot(snapshot);
                    return;
                }

                var hitObject = materialized.Key.ObjectIndex;
                _scene.Argb[hitObject] = color.ToArgb();
                var beforeMergeCount = _scene.ObjectCount;
                var merged = _scene.MergeSameColorFillsAround(hitObject, frame: _frame);
                if (_scene.ObjectCount != beforeMergeCount || merged != hitObject) SetSelection(merged);
                else SetSelection(materialized);
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                _stage.Invalidate();
            }
        }
        else if (IsFreehandTool(_tool) && e.Button == MouseButtons.Left && _startWorld is { } freehandStart)
        {
            BeginFreehandStroke(e.Location, freehandStart);
        }
    }

    private void StageMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left
            || _forceMarqueeOnPointerDown
            || IsScene3DView())
        {
            return;
        }

        var world = _stage.ScreenToWorld(e.Location);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (IsSceneCompositionContext())
        {
            if (hit.IsValid && _sceneCompositionResult.TryGetOwner(hit.Key.ObjectIndex, out var owner))
            {
                OpenDrawingObjectEditor(owner.DrawingObjectId);
            }

            return;
        }

        if (!hit.IsValid && _stage.UnderlayScene is { } underlay)
        {
            var underlayHit = underlay.HitTestElement(world, 0, SelectionToleranceWorld());
            if (underlayHit.IsValid
                && _drawingObjectUnderlayResult.TryGetOwner(underlayHit.Key.ObjectIndex, out var owner)
                && OpenDrawingObjectEditor(owner.DrawingObjectId))
            {
                return;
            }
        }

        if (_tool != ToolMode.Select) return;
        if (!hit.IsValid || hit.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)) return;

        var connected = _scene.GetConnectedStrokeElements(hit, _frame);
        if (connected.Length == 0) return;

        _pendingClickSelection = DrawingElementHit.None;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
        SetSelection(connected, hit);
        _pointerHitWasAlreadySelected = true;
        CaptureEditStart(_selectedObject);
        UpdateInspector();
        _stage.Invalidate();
    }

    private void StageMouseMove(object? sender, MouseEventArgs e)
    {
        if (_lastMouse is null)
        {
            TryBegin3DViewDragFromMove(e);
            return;
        }

        var dx = e.X - _lastMouse.Value.X;
        var dy = e.Y - _lastMouse.Value.Y;
        _lastMouse = e.Location;

        if (TryPromote3DViewDragFromMove(e))
        {
            if (_viewReferencePanning)
            {
                _stage.PanReferenceCamera(dx, dy);
            }
            else if (_viewReferenceZooming)
            {
                _stage.DollyReferenceCameraByPixels(dy);
            }
            else if (_viewOrbiting)
            {
                _stage.RotateReferenceCamera(dx, dy);
            }

            UpdateStatusBar();
            return;
        }

        if (_viewReferencePanning)
        {
            _stage.PanReferenceCamera(dx, dy);
            UpdateStatusBar();
            return;
        }

        if (_viewReferenceZooming)
        {
            _stage.DollyReferenceCameraByPixels(dy);
            UpdateStatusBar();
            return;
        }

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

        if (_viewOrbiting)
        {
            _stage.RotateReferenceCamera(dx, dy);
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
        else if (_tool == ToolMode.Select && _pendingClickSelection.IsValid && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location))
            {
                var hit = _pendingClickSelection;
                _pendingClickSelection = DrawingElementHit.None;
                SetSelection(hit);
                _pointerHitWasAlreadySelected = true;
                CaptureEditStart(hit.Key.ObjectIndex);
                MoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
            }
        }
        else if (_tool == ToolMode.Select && _forceMarqueeOnPointerDown && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location))
            {
                BeginMarqueeFromPendingSelection(e.Location);
            }
        }
        else if (_tool == ToolMode.Select && _selectedObject >= 0 && _startWorld is not null && _selectedStart is not null && e.Button == MouseButtons.Left)
        {
            if (!PointerDragExceeded(e.Location)) return;
            MoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
        }
        else if (IsFreehandTool(_tool) && _freehandDrawing && e.Button == MouseButtons.Left)
        {
            AppendFreehandSample(e.Location);
        }
        else if (IsDrawingTool(_tool) && _startWorld is not null && e.Button == MouseButtons.Left)
        {
            UpdateDrawingPreview(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }
    }

    private void MoveSelectedFromPointer(PointF world)
    {
        if (_selectedObject < 0 || _startWorld is null || _selectedStart is null) return;
        CapturePointerUndoSnapshot();
        if (_activeHandle != EditHandleKind.None)
        {
            ApplyHandleDrag(world);
        }
        else
        {
            if (!EnsureSelectedElementDetachedForMove()) return;
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
    }

    private bool TryBegin3DViewDragFromMove(MouseEventArgs e)
    {
        if (!IsScene3DView()) return false;
        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return true;
        }

        if (MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return true;
        }

        return false;
    }

    private bool TryPromote3DViewDragFromMove(MouseEventArgs e)
    {
        if (!IsScene3DView()) return false;
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming) return false;
        if (MouseButtonDown(e, MouseButtons.Middle))
        {
            BeginGlobalViewDrag(e);
            return true;
        }

        if (MouseButtonDown(e, MouseButtons.Right) && IsAltPressed())
        {
            BeginGlobalViewDrag(e, referencePan: true);
            return true;
        }

        return false;
    }

    private bool EnsureSelectedElementDetachedForMove()
    {
        if (_detachedSelectionForMove) return _selectedElements.Count == 0;
        _detachedSelectionForMove = true;
        if (_selectedElements.Count == 0) return true;
        var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
        if (!materialized.Success || materialized.Parts.Length == 0)
        {
            if (_undoCapturedForPointerEdit && _undoStack.Count > 0) _undoStack.Pop();
            _undoCapturedForPointerEdit = false;
            return false;
        }

        var primaryPart = materialized.Parts.FirstOrDefault(part => part.Source == _selectedElement.Key);
        var primaryObject = primaryPart.Source.IsValid
            ? primaryPart.Result.ObjectIndex
            : materialized.Parts[^1].Result.ObjectIndex;
        var selectedObjects = materialized.Parts
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .Where(index => index != primaryObject)
            .Append(primaryObject)
            .ToArray();
        SetSelection(selectedObjects);
        CaptureEditStart(_selectedObject);
        _geometryDirty |= materialized.Changed;
        if (materialized.Changed) _hierarchyPanel.RefreshScene();
        UpdateInspector();
        return true;
    }

    private bool PointerDragExceeded(Point current)
    {
        if (_startScreen is not { } start) return false;
        return Math.Abs(current.X - start.X) + Math.Abs(current.Y - start.Y) > 6;
    }

    private void BeginMarqueeFromPendingSelection(Point current)
    {
        _marqueeSelecting = true;
        _marqueeStart = _startScreen ?? current;
        _pendingClickSelection = DrawingElementHit.None;
        _selectedStart = null;
        _curveControlStart = null;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _lineEndpointEditStarts.Clear();
        _stage.SetMarquee(_marqueeStart.Value, current);
    }

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
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

        if (_tool == ToolMode.Select && _pendingClickSelection.IsValid)
        {
            SetSelection(_pendingClickSelection);
            UpdateInspector();
            _stage.Invalidate();
        }

        if (_freehandDrawing)
        {
            AppendFreehandSample(e.Location, force: true);
            CommitFreehandStroke();
            FinishPointerInteraction();
            return;
        }

        if (IsDrawingTool(_tool) && !IsFreehandTool(_tool) && _startWorld is not null && _startScreen is not null)
        {
            var dx = e.X - _startScreen.Value.X;
            var dy = e.Y - _startScreen.Value.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) AddDrawnObject(_startWorld.Value, _stage.ScreenToWorld(e.Location), _tool);
        }

        _stage.ClearDrawingPreview();
        FinishPointerInteraction();
    }

    private void StageMouseCaptureChanged(object? sender, EventArgs e)
    {
        if (_stage.Capture) return;
        FinishLostPointerCapture();
    }

    private void FinishLostPointerCapture()
    {
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        if (_lastMouse is null && !_freehandDrawing && !_marqueeSelecting) return;
        CancelFreehandStroke();
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        FinishPointerInteraction();
        _stage.Invalidate();
    }

    private void FinishPointerInteractionForFrameChange()
    {
        if (_lastMouse is not null
            || _freehandDrawing
            || _marqueeSelecting
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming)
        {
            FinishLostPointerCapture();
        }

        _pendingClickSelection = DrawingElementHit.None;
        _forceMarqueeOnPointerDown = false;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _stage.ClearMarquee();
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
        _lineEndpointEditStarts.Clear();
        _detachedSelectionForMove = false;
        _pointerHitWasAlreadySelected = false;
        _selectionWasEmptyOnPointerDown = false;
        _forceMarqueeOnPointerDown = false;
        _pendingClickSelection = DrawingElementHit.None;
        _undoCapturedForPointerEdit = false;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        if (_geometryDirty)
        {
            MergeSelectedFillsAfterGeometryEdit();
            _scene.RebuildGeometryIndex();
            _geometryDirty = false;
            UpdateInspector();
        }

        _stage.Capture = false;
    }

    private void StageDragEnter(object? sender, DragEventArgs e)
    {
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject))
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        var canPlace = IsSceneCompositionContext()
            ? ActiveScene() is not null
            : ActiveDrawingObject() is { } container && _project.CanContainDrawingObject(container.Id, drawingObject.Id);
        e.Effect = canPlace ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void StageDragDrop(object? sender, DragEventArgs e)
    {
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject)) return;
        var client = _stage.PointToClient(new Point(e.X, e.Y));
        var world = _stage.ScreenToWorld(client);

        if (IsSceneCompositionContext())
        {
            var scene = ActiveScene();
            if (scene is null) return;
            if (!_project.TryAddSceneInstance(
                    scene.Id,
                    drawingObject.Id,
                    world,
                    scene.Dimension == SceneDimension.TwoD ? scene.Instances.Count + 1 : 0,
                    out _))
            {
                return;
            }

            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration();
            RebuildSceneComposition();
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
            _sceneEditorPanel.RefreshSceneStats();
            AppLog.Info($"Placed drawing object in scene: {drawingObject.Name} -> {scene.Name}");
            return;
        }

        var container = ActiveDrawingObject();
        if (container is null) return;
        if (!_project.TryAddDrawingObjectInstance(container.Id, drawingObject.Id, world, out _))
        {
            MessageBox.Show(
                "A drawing object cannot contain itself or create a recursive containment cycle.",
                "Drawing Object",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        RebuildDrawingObjectUnderlay();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _stage.Invalidate();
        AppLog.Info($"Placed nested drawing object: {drawingObject.Name} -> {container.Name}");
    }

    private bool TryResolveDroppedDrawingObject(IDataObject? data, out DrawingObjectDefinition drawingObject)
    {
        drawingObject = null!;
        string? drawingObjectId = null;
        if (data?.GetData(typeof(DrawingObjectDragData)) is DrawingObjectDragData reference
            && string.Equals(reference.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            drawingObjectId = reference.DrawingObjectId;
        }
        else if (data?.GetData(typeof(VaultItem)) is VaultItem item
            && string.Equals(item.ReferenceKind, "DrawingObject", StringComparison.Ordinal))
        {
            drawingObjectId = item.ReferenceId;
        }
        else if (data?.GetData(typeof(VaultItem)) is VaultItem legacyItem)
        {
            const string prefix = "DrawingObjectId:";
            drawingObjectId = legacyItem.Payload
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
                .Trim();
        }

        drawingObject = _drawingObjects.FirstOrDefault(item => item.Id == drawingObjectId)!;
        return drawingObject is not null;
    }

    private void MergeSelectedFillsAfterGeometryEdit()
    {
        var selected = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (selected.Length == 0) return;
        var retainedKeys = selected
            .Where(index => !IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .ToHashSet();
        var fillKeys = selected
            .Where(index => IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        var changed = false;
        foreach (var key in fillKeys)
        {
            var source = FindObjectByStackKey(key);
            if (source < 0 || !IsFillShape(_scene.ShapeKind[source])) continue;
            var beforeCount = _scene.ObjectCount;
            var merged = _scene.MergeSameColorFillsAround(source, connectNearby: false, frame: _frame);
            if ((uint)merged >= _scene.ObjectCount) continue;
            retainedKeys.Add(new DrawingStackKey(_scene.ObjectOrder[merged], _scene.ObjectSubOrder[merged]));
            changed |= _scene.ObjectCount != beforeCount || merged != source;
        }

        if (fillKeys.Length == 0) return;
        var mergedSelection = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => retainedKeys.Contains(new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])))
            .ToArray();
        SetSelection(mergedSelection);
        if (changed) _hierarchyPanel.RefreshScene();
    }

    private int FindObjectByStackKey(DrawingStackKey key)
    {
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ObjectOrder[index] == key.Order && _scene.ObjectSubOrder[index].Equals(key.SubOrder)) return index;
        }

        return -1;
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
            var snapshot = _scene.CreateSnapshot();
            var materialized = _scene.MaterializeMarqueeLineParts(bounds, _frame);
            var objects = _scene.QueryDrawingObjects(bounds, _frame);
            var selectedObjects = objects
                .Where(index => _scene.IsObjectGeometryInsideBounds(index, bounds))
                .Concat(materialized.SelectedObjects)
                .Distinct()
                .ToArray();
            if (materialized.Changed)
            {
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
            }

            if (selectedObjects.Length > 0)
            {
                SetSelection(selectedObjects);
            }
            else if (!TrySetTopologyMarqueeSelection(bounds))
            {
                ClearSelection();
            }
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

    private bool TrySetTopologyMarqueeSelection(RectangleF bounds)
    {
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return false;
        var selected = _scene.QueryDrawingElementsInsideBounds(bounds, _frame);
        if (selected.Length == 0) return false;
        var primary = selected
            .OrderBy(hit => _scene.ObjectLayer[hit.Key.ObjectIndex])
            .ThenByDescending(hit => hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke ? 1 : 0)
            .ThenByDescending(hit => _scene.ObjectOrder[hit.Key.ObjectIndex])
            .ThenByDescending(hit => _scene.ObjectSubOrder[hit.Key.ObjectIndex])
            .First();
        SetTopologyMarqueeSelection(selected, primary);
        return true;
    }

    private void SetTopologyMarqueeSelection(IEnumerable<DrawingElementHit> hits, DrawingElementHit primary)
    {
        var selected = hits.Where(hit => hit.IsValid).ToArray();
        if (selected.Length == 0)
        {
            ClearSelection();
            return;
        }

        SetSelection(selected, primary);
    }

    private bool DeleteSelectedObject()
    {
        if (IsSceneCompositionContext()) return false;
        var targets = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (targets.Length == 0 && _selectedObject >= 0 && _selectedObject < _scene.ObjectCount) targets = new[] { _selectedObject };
        if (targets.Length == 0) return false;
        var snapshot = _scene.CreateSnapshot();

        if (_selectedElements.Count > 0)
        {
            var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success)
            {
                _scene.RestoreSnapshot(snapshot);
                return false;
            }

            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }

        if (_scene.RemoveObjects(targets) <= 0)
        {
            _scene.RestoreSnapshot(snapshot);
            return false;
        }

        PushUndoSnapshot(snapshot);

        ClearSelection();
        _geometryDirty = false;
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private void BeginGlobalViewDrag(MouseEventArgs e, bool referencePan = false)
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
        _pointerHitWasAlreadySelected = false;
        var is3DView = IsScene3DView();
        _viewReferencePanning = is3DView && (referencePan || IsShiftPressed());
        _viewReferenceZooming = is3DView && !referencePan && !IsShiftPressed() && IsControlPressed();
        _viewOrbiting = is3DView && !_viewReferencePanning && !_viewReferenceZooming;
        _viewZooming = !is3DView && IsControlPressed();
        _viewPanning = !is3DView && !_viewZooming;
        CancelFreehandStroke();
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _stage.Cursor = _viewZooming || _viewReferenceZooming ? Cursors.SizeNS : Cursors.SizeAll;
    }

    private void EndGlobalViewDrag()
    {
        _lastMouse = null;
        _viewPanning = false;
        _viewZooming = false;
        _viewOrbiting = false;
        _viewReferencePanning = false;
        _viewReferenceZooming = false;
        ApplyToolCursor();
        _stage.Capture = false;
    }

    private void SetSelection(int objectIndex)
    {
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = objectIndex >= 0 && objectIndex < _scene.ObjectCount ? objectIndex : -1;
        _selectedElement = DrawingElementHit.None;
        if (_selectedObject >= 0) _selectedObjects.Add(_selectedObject);
        SyncSelectionToStage();
    }

    private void SetSelection(DrawingElementHit hit)
    {
        if (!hit.IsValid
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || !_scene.IsObjectActive(hit.Key.ObjectIndex, _frame))
        {
            ClearSelection();
            return;
        }

        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = hit.Key.ObjectIndex;
        _selectedElement = hit;
        _selectedObjects.Add(_selectedObject);
        _selectedElements.Add(hit);
        SyncSelectionToStage();
    }

    private void SetSelection(IEnumerable<int> objectIndices)
    {
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedElement = DrawingElementHit.None;
        var seen = new HashSet<int>();
        foreach (var index in objectIndices)
        {
            if (index < 0 || index >= _scene.ObjectCount || !seen.Add(index)) continue;
            _selectedObjects.Add(index);
        }

        _selectedObject = _selectedObjects.Count > 0 ? _selectedObjects[_selectedObjects.Count - 1] : -1;
        SyncSelectionToStage();
    }

    private void SetSelection(IEnumerable<DrawingElementHit> hits, DrawingElementHit primary = default)
    {
        _selectedObjects.Clear();
        _selectedElements.Clear();
        var seenKeys = new HashSet<DrawingElementKey>();
        var seenObjects = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (!hit.IsValid
                || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
                || !_scene.IsObjectActive(hit.Key.ObjectIndex, _frame)
                || !seenKeys.Add(hit.Key))
            {
                continue;
            }

            _selectedElements.Add(hit);
            if (seenObjects.Add(hit.Key.ObjectIndex)) _selectedObjects.Add(hit.Key.ObjectIndex);
        }

        _selectedElement = primary.IsValid && _selectedElements.Any(hit => hit.Key == primary.Key)
            ? _selectedElements.First(hit => hit.Key == primary.Key)
            : _selectedElements.Count > 0 ? _selectedElements[^1] : DrawingElementHit.None;
        _selectedObject = _selectedElement.IsValid ? _selectedElement.Key.ObjectIndex : -1;
        SyncSelectionToStage();
    }

    private void SyncSelectionToStage()
    {
        _stage.SetSelection(_selectedObjects, _selectedObject);
        _stage.SetSelectedElements(_selectedElements, _selectedElement);
    }

    private void ClearSelection() => SetSelection(-1);

    private void ClearInactiveSelection()
    {
        if (_selectedObjects.Any(index => (uint)index >= _scene.ObjectCount || !_scene.IsObjectActive(index, _frame)))
        {
            ClearSelection();
        }
    }

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

    private void BeginFreehandStroke(Point screen, PointF world)
    {
        _freehandSamples.Clear();
        _freehandSamples.Add(VectorUnits.Quantize(world));
        _freehandLastScreen = screen;
        _freehandDrawing = true;
        _freehandBrushStroke = _tool == ToolMode.Brush;
        _freehandStrokeUnits = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), ActiveStrokeUnits());
        var alpha = (int)Math.Clamp(_materialEditor.Opacity * 255, 0, 255);
        _freehandColor = _freehandBrushStroke
            ? ActiveColor()
            : Color.FromArgb(alpha, ActiveStrokeColor());
        _stage.SetFreehandPreview(_freehandSamples, _freehandColor, _freehandStrokeUnits);
    }

    private void AppendFreehandSample(Point screen, bool force = false)
    {
        if (!_freehandDrawing || _freehandLastScreen is not { } previousScreen || _freehandSamples.Count == 0) return;
        var dx = screen.X - previousScreen.X;
        var dy = screen.Y - previousScreen.Y;
        var distancePixels = MathF.Sqrt(dx * dx + dy * dy);
        if (!force && distancePixels < FreehandSampleSpacingPixels) return;

        var start = _freehandSamples[^1];
        var end = _stage.ScreenToWorld(screen);
        var steps = Math.Max(1, (int)MathF.Ceiling(distancePixels / 2f));
        for (var step = 1; step <= steps && _freehandSamples.Count < MaxFreehandSamples; step++)
        {
            var t = step / (float)steps;
            var point = VectorUnits.Quantize(new PointF(
                start.X + (end.X - start.X) * t,
                start.Y + (end.Y - start.Y) * t));
            if (_freehandSamples[^1] == point) continue;
            _freehandSamples.Add(point);
        }

        _freehandLastScreen = screen;
        _stage.SetFreehandPreview(_freehandSamples, _freehandColor, _freehandStrokeUnits);
    }

    private void CommitFreehandStroke()
    {
        if (!_freehandDrawing || _freehandSamples.Count == 0)
        {
            CancelFreehandStroke();
            return;
        }

        var smoothing = Math.Clamp(_drawSettings.FreehandSmoothing + (_freehandBrushStroke ? 12 : 0), 0, 100);
        var tolerancePixels = _freehandBrushStroke ? 0.9f : 0.65f;
        var points = FreehandStrokeProcessor.Process(
            _freehandSamples,
            smoothing,
            _stage.ScreenLengthToWorld(tolerancePixels));
        if (points.Length == 0)
        {
            CancelFreehandStroke();
            return;
        }

        CaptureUndoSnapshot();
        var newObject = _scene.AddFreehandStroke(
            _scene.ActiveLayer,
            points,
            _freehandStrokeUnits,
            _freehandColor,
            _freehandBrushStroke,
            (uint)Math.Max(3, points.Length));
        if (newObject >= 0)
        {
            if (_freehandBrushStroke) newObject = _scene.MergeSameColorFillsAround(newObject, frame: _frame);
            SetSelection(newObject);
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
        }

        CancelFreehandStroke();
        _stage.Invalidate();
    }

    private void CancelFreehandStroke()
    {
        _freehandDrawing = false;
        _freehandBrushStroke = false;
        _freehandLastScreen = null;
        _freehandSamples.Clear();
        _stage.ClearFreehandPreview();
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
        _lineEndpointEditStarts.Clear();
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

        CaptureLineEndpointEditStart(objectIndex);
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

        if (_activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd)
        {
            ApplyLineEndpointDrag(world);
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

    private void CaptureLineEndpointEditStart(int objectIndex)
    {
        if (_activeHandle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)) return;
        var startEndpoint = _activeHandle == EditHandleKind.LineStart;
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var anchor)) return;
        var layer = _scene.ObjectLayer[objectIndex];

        for (var i = 0; i < _scene.ObjectCount; i++)
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line || _scene.ObjectLayer[i] != layer) continue;
            CaptureConnectedEndpoint(i, startEndpoint: true, anchor);
            CaptureConnectedEndpoint(i, startEndpoint: false, anchor);
        }
    }

    private void CaptureConnectedEndpoint(int objectIndex, bool startEndpoint, PointF anchor)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        if (Distance(endpoint, anchor) > EndpointConnectionToleranceUnits) return;
        if (!_scene.TryGetLineEndpoint(objectIndex, !startEndpoint, out var opposite)) return;
        var control = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        _lineEndpointEditStarts.Add(new LineEndpointEditStart(objectIndex, startEndpoint, endpoint, opposite, control, _scene.IsLineStraight(objectIndex)));
    }

    private void ApplyLineEndpointDrag(PointF world)
    {
        if (_lineEndpointEditStarts.Count == 0) return;
        var snapped = VectorUnits.Quantize(SnapEndpointToNearbyConnection(_drawSettings.SnapPoint(world)));
        foreach (var edit in _lineEndpointEditStarts)
        {
            _scene.SetLineEndpoint(edit.ObjectIndex, edit.StartEndpoint, snapped, edit.OppositeEndpoint, edit.Control, edit.KeepStraight);
        }
    }

    private PointF SnapEndpointToNearbyConnection(PointF world)
    {
        if (!_drawSettings.SnapEnabled || !_drawSettings.SnapToObjects || _selectedObject < 0 || _selectedObject >= _scene.ObjectCount) return world;
        var layer = _scene.ObjectLayer[_selectedObject];
        var tolerance = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(10));
        var best = world;
        var bestDistance = tolerance;
        for (var i = 0; i < _scene.ObjectCount; i++)
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line || _scene.ObjectLayer[i] != layer) continue;
            if (_lineEndpointEditStarts.Any(edit => edit.ObjectIndex == i)) continue;
            TrySnapToEndpoint(i, startEndpoint: true, world, ref best, ref bestDistance);
            TrySnapToEndpoint(i, startEndpoint: false, world, ref best, ref bestDistance);
        }

        return best;
    }

    private void TrySnapToEndpoint(int objectIndex, bool startEndpoint, PointF world, ref PointF best, ref float bestDistance)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        var distance = Distance(world, endpoint);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = endpoint;
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
        if (DrawingToolsBlocked()) return;
        CaptureUndoSnapshot();
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

        var newObject = _scene.AddObject(_scene.ActiveLayer, center, new SizeF(width, height), angle, ActiveStrokeUnits(), ActiveColor(), ActiveStrokeColor(), tool == ToolMode.Line ? 6u : 24u, shape);
        if (tool != ToolMode.Line && newObject >= 0) newObject = _scene.MergeSameColorFillsAround(newObject, frame: _frame);
        SetSelection(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void UpdateInspector()
    {
        _sceneEditorPanel.RefreshSceneStats();
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        var validSelection = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (_selectedElements.Count > 1)
        {
            var kinds = _selectedElements.Select(hit => hit.Key.Kind).Distinct().ToArray();
            var firstLayer = validSelection.Length > 0 ? _scene.ObjectLayer[validSelection[0]] : -1;
            var mixedLayer = validSelection.Any(index => _scene.ObjectLayer[index] != firstLayer);
            var atoms = _selectedElements.Sum(hit => _scene.EstimateElementAtomCount(hit, _frame));
            var kindLabel = kinds.Length == 1 ? kinds[0].ToString() : "Mixed";
            _selected.Text = $"Selected: {CompactFormat.Number(_selectedElements.Count)} {kindLabel} parts";
            _selectedLayer.Text = mixedLayer ? "Layer: Mixed" : firstLayer >= 0 ? $"Layer: {_scene.LayerNames[firstLayer]}" : "Layer: -";
            _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(atoms)}";
            return;
        }

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
        var selectedAtoms = _selectedElement.IsValid
            ? _scene.EstimateElementAtomCount(_selectedElement, _frame)
            : _scene.AtomCount[_selectedObject];
        _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(selectedAtoms)}";
        var fill = Color.FromArgb(_scene.Argb[_selectedObject]);
        var stroke = _scene.StrokeArgb.Length > _selectedObject ? Color.FromArgb(_scene.StrokeArgb[_selectedObject]) : ActiveStrokeColor();
        var strokePoints = VectorUnits.UnitsToStrokePoints(_scene.Stroke[_selectedObject]);
        var shape = _scene.ShapeKind[_selectedObject];
        var brushFill = _tool == ToolMode.Brush && IsFillShape(shape);
        var inspectorStrokePoints = brushFill ? _brushStrokeWidthPoints : strokePoints;
        if (shape is ShapeKind.Freeform or ShapeKind.Line)
        {
            _materialEditor.SetMaterial(_materialEditor.Fill, Color.FromArgb(stroke.R, stroke.G, stroke.B), inspectorStrokePoints, stroke.A / 255f);
        }
        else if (shape == ShapeKind.BrushStroke)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, fill.A / 255f);
        }
        else if (brushFill)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, fill.A / 255f);
        }
        else
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), stroke, inspectorStrokePoints, fill.A / 255f);
        }
        _updatingStrokeInput = true;
        try
        {
            _stroke.Value = (decimal)Math.Clamp(inspectorStrokePoints, (float)_stroke.Minimum, (float)_stroke.Maximum);
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

    private void ApplyMaterialToSelectedElements(MaterialChangedEventArgs material, float strokeUnits)
    {
        var selected = _selectedElements.ToArray();
        var affected = selected
            .Where(hit => MaterialChangeAffectsSelection(hit.Key.ObjectIndex, hit.Key.Kind, material, strokeUnits))
            .Select(hit => hit.Key)
            .ToHashSet();
        if (affected.Count == 0) return;

        var snapshot = _scene.CreateSnapshot();
        var materialized = _scene.MaterializeSelectedParts(affected.ToArray(), _frame);
        if (!materialized.Success)
        {
            _scene.RestoreSnapshot(snapshot);
            SyncSelectionToStage();
            return;
        }

        foreach (var part in materialized.Parts)
        {
            if (!affected.Contains(part.Source)) continue;
            var objectIndex = part.Result.ObjectIndex;
            if (part.Source.Kind == DrawingElementKind.Fill)
            {
                _scene.Argb[objectIndex] = TargetFillArgb(objectIndex, material);
                _scene.Stroke[objectIndex] = 0;
                continue;
            }

            var color = TargetStrokeColor(objectIndex, material);
            _scene.Argb[objectIndex] = Color.FromArgb(0, color).ToArgb();
            _scene.StrokeArgb[objectIndex] = color.ToArgb();
            var targetStrokeUnits = TargetStrokeUnits(objectIndex, material, strokeUnits);
            if ((material.ApplyAll || material.StrokeWidthChanged) && IsFreehandShape(_scene.ShapeKind[objectIndex]))
            {
                _scene.UpdateFreehandStrokeWidth(objectIndex, targetStrokeUnits);
            }
            else if (material.ApplyAll || material.StrokeWidthChanged)
            {
                _scene.Stroke[objectIndex] = targetStrokeUnits;
                _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
            }
        }

        _scene.RebuildGeometryIndex();
        var selectedObjects = materialized.Parts
            .Where(part => affected.Contains(part.Source))
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .ToArray();
        SetSelection(selectedObjects);
        if (affected.Any(key => key.Kind == DrawingElementKind.Fill)) MergeSelectedFillsAfterGeometryEdit();

        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplyMaterialToSelectedObjects(MaterialChangedEventArgs material, float strokeUnits)
    {
        var targets = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (targets.Length < 2) return;
        var snapshot = _scene.CreateSnapshot();
        var changed = false;
        foreach (var objectIndex in targets)
        {
            var shape = _scene.ShapeKind[objectIndex];
            var fillCapable = IsFillShape(shape) || shape == ShapeKind.BrushStroke;
            var strokeCapable = shape is ShapeKind.Line or ShapeKind.Freeform
                || _scene.Stroke[objectIndex] > 0
                || material.ApplyAll
                || material.StrokeWidthChanged;

            if (fillCapable && (material.ApplyAll || material.FillChanged || material.OpacityChanged))
            {
                var fill = TargetFillArgb(objectIndex, material);
                if (_scene.Argb[objectIndex] != fill)
                {
                    _scene.Argb[objectIndex] = fill;
                    changed = true;
                }
            }

            if (!strokeCapable || (!material.ApplyAll && !material.StrokeChanged && !material.StrokeWidthChanged && !material.OpacityChanged))
            {
                continue;
            }

            var strokeColor = TargetStrokeColor(objectIndex, material);
            if (_scene.StrokeArgb[objectIndex] != strokeColor.ToArgb())
            {
                _scene.StrokeArgb[objectIndex] = strokeColor.ToArgb();
                changed = true;
            }

            if (material.ApplyAll || material.StrokeWidthChanged)
            {
                var targetStrokeUnits = TargetStrokeUnits(objectIndex, material, strokeUnits);
                if (Math.Abs(_scene.Stroke[objectIndex] - targetStrokeUnits) > 0.001f)
                {
                    if (IsFreehandShape(shape)) _scene.UpdateFreehandStrokeWidth(objectIndex, targetStrokeUnits);
                    else
                    {
                        _scene.Stroke[objectIndex] = targetStrokeUnits;
                        if (shape == ShapeKind.Line)
                        {
                            _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
                        }
                    }

                    changed = true;
                }
            }
        }

        if (!changed) return;
        _scene.RebuildGeometryIndex();
        var changedFill = material.ApplyAll || material.FillChanged || material.OpacityChanged;
        if (changedFill) MergeSelectedFillsAfterGeometryEdit();
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool MaterialChangeAffectsSelection(int objectIndex, DrawingElementKind selectedKind, MaterialChangedEventArgs material, float strokeUnits)
    {
        if (selectedKind == DrawingElementKind.Fill)
        {
            if (!material.ApplyAll && !material.FillChanged && !material.OpacityChanged) return false;
            return _scene.Argb[objectIndex] != TargetFillArgb(objectIndex, material);
        }

        if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
        {
            if (!material.ApplyAll && !material.StrokeChanged && !material.StrokeWidthChanged && !material.OpacityChanged) return false;
            return _scene.StrokeArgb[objectIndex] != TargetStrokeColor(objectIndex, material).ToArgb()
                || Math.Abs(_scene.Stroke[objectIndex] - TargetStrokeUnits(objectIndex, material, strokeUnits)) > 0.001f;
        }

        var alpha = (int)Math.Clamp(material.Opacity * 255, 0, 255);
        var fill = Color.FromArgb(alpha, material.Fill).ToArgb();
        var stroke = Color.FromArgb(alpha, material.Stroke).ToArgb();
        var shape = _scene.ShapeKind[objectIndex];
        if (_tool == ToolMode.Brush && IsFillShape(shape))
        {
            return _scene.Argb[objectIndex] != fill || _scene.Stroke[objectIndex] > 0;
        }

        if (shape == ShapeKind.BrushStroke)
        {
            return _scene.Argb[objectIndex] != fill
                || _scene.StrokeArgb[objectIndex] != fill
                || Math.Abs(_scene.Stroke[objectIndex] - strokeUnits) > 0.001f;
        }

        if (shape == ShapeKind.Freeform)
        {
            return _scene.StrokeArgb[objectIndex] != stroke || Math.Abs(_scene.Stroke[objectIndex] - strokeUnits) > 0.001f;
        }

        return _scene.Argb[objectIndex] != fill
            || _scene.StrokeArgb[objectIndex] != stroke
            || Math.Abs(_scene.Stroke[objectIndex] - strokeUnits) > 0.001f;
    }

    private int TargetFillArgb(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.Argb[objectIndex]);
        var rgb = material.ApplyAll || material.FillChanged ? material.Fill : current;
        var alpha = material.ApplyAll || material.OpacityChanged
            ? (int)Math.Clamp(material.Opacity * 255, 0, 255)
            : current.A;
        return Color.FromArgb(alpha, rgb).ToArgb();
    }

    private Color TargetStrokeColor(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.StrokeArgb[objectIndex]);
        var rgb = material.ApplyAll || material.StrokeChanged ? material.Stroke : current;
        var alpha = material.ApplyAll || material.OpacityChanged
            ? (int)Math.Clamp(material.Opacity * 255, 0, 255)
            : current.A;
        return Color.FromArgb(alpha, rgb);
    }

    private float TargetStrokeUnits(int objectIndex, MaterialChangedEventArgs material, float requestedStrokeUnits)
    {
        return material.ApplyAll || material.StrokeWidthChanged ? requestedStrokeUnits : _scene.Stroke[objectIndex];
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
        var sceneEdit = view == WorkspaceView.SceneEditor;
        _workspaceHeader.Height = basicDrawing ? 84 : 44;
        _drawingObjectRow.Visible = basicDrawing;
        _basicInspectorPage.Visible = basicDrawing;
        _sceneEditPage.Visible = sceneEdit;
        _animationPage.Visible = view == WorkspaceView.Animation;
        _timeline.Visible = true;
        if (basicDrawing)
        {
            BindActiveDrawingObjectScene(resetView: false);
        }
        else if (sceneEdit)
        {
            BindSceneEditStage(resetView: false);
        }

        if (sceneEdit && IsBasicDrawingOnlyTool(_tool))
        {
            _tool = ToolMode.Select;
            CancelFreehandStroke();
            _stage.ClearDrawingPreview();
        }

        RefreshToolButtons();
        ApplyToolCursor();
    }

    private static bool IsDrawingTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pencil or ToolMode.Brush;
    }

    private static bool IsBasicDrawingOnlyTool(ToolMode tool)
    {
        return IsDrawingTool(tool) || tool == ToolMode.Fill;
    }

    private static bool IsStandardStrokeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line;
    }

    private static bool IsFreehandTool(ToolMode tool)
    {
        return tool is ToolMode.Pencil or ToolMode.Brush;
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    private static bool IsFillShape(ShapeKind shape)
    {
        return shape is not ShapeKind.Line and not ShapeKind.Freeform and not ShapeKind.BrushStroke;
    }

    private static bool IsShapeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star;
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

    private static SvgIconKind ToolIconKind(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Ellipse => SvgIconKind.Ellipse,
            ToolMode.Triangle => SvgIconKind.Triangle,
            ToolMode.Polygon => SvgIconKind.Polygon,
            ToolMode.Star => SvgIconKind.Star,
            ToolMode.Line => SvgIconKind.Line,
            ToolMode.Pencil => SvgIconKind.Pencil,
            ToolMode.Brush => SvgIconKind.Brush,
            ToolMode.Fill => SvgIconKind.Fill,
            ToolMode.Hand => SvgIconKind.Pan,
            ToolMode.Select => SvgIconKind.Select,
            _ => SvgIconKind.Rectangle
        };
    }

    private static string ShapeToolName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Ellipse => "Ellipse Tool",
            ToolMode.Triangle => "Triangle Tool",
            ToolMode.Polygon => "Polygon Tool",
            ToolMode.Star => "Star Tool",
            _ => "Rectangle Tool"
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
            var enabled = !DrawingToolsBlocked() || !IsBasicDrawingOnlyTool(tool);
            button.Enabled = enabled;
            if (tool == _tool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_shapeToolButton is not null)
        {
            var enabled = !DrawingToolsBlocked();
            _shapeToolButton.Enabled = enabled;
            _shapeToolButton.Icon = ToolIconKind(_activeShapeTool);
            _shapeToolButton.Tag = _activeShapeTool;
            _shapeToolButton.AccessibleName = ShapeToolName(_activeShapeTool);
            if (IsShapeTool(_tool)) Theme.StyleActiveButton(_shapeToolButton);
            else Theme.StyleButton(_shapeToolButton);
            if (!enabled) _shapeToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _shapeToolButton.Invalidate();
        }

        foreach (var (tool, button) in _shapeFlyoutButtons)
        {
            var enabled = !DrawingToolsBlocked();
            button.Enabled = enabled;
            if (tool == _activeShapeTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
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
    private static bool IsAutoRestartEnabled() => Environment.GetEnvironmentVariable("V2D_DEV_AUTO_RESTART") == "1";
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
