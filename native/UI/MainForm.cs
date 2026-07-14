using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class EditorRestartState
{
    public required VectorProject Project { get; init; }
    public required int ActiveSceneIndex { get; init; }
    public required int ActiveDrawingObjectIndex { get; init; }
    public required WorkspaceView Workspace { get; init; }
    public required int Frame { get; init; }
    public required int[] SelectedObjects { get; init; }
    public required StageViewState StageView { get; init; }
    public required Rectangle WindowBounds { get; init; }
    public required FormWindowState WindowState { get; init; }
}

internal sealed class EditorRestartRequestedEventArgs(EditorRestartState state) : EventArgs
{
    public EditorRestartState State { get; } = state;
}

internal sealed record TimelineClipboardCell(
    int TrackOffset,
    int FrameOffset,
    int SourceLayer,
    int SourceFrame,
    TimelineKeyframeKind Kind);

internal sealed class TimelineFrameClipboard
{
    public required TimelineClipboardCell[] Cells { get; init; }
    public VectorScene? DrawingSource { get; init; }
}

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
    private const int VkShift = 0x10;
    private const double TargetUps = 300.0;
    private const double MetricsRefreshSeconds = 0.25;
    private const double MaxFrameSeconds = 0.1;
    private const int IdleTimerIntervalMs = 250;
    private const int PlaybackTimerIntervalMs = 8;
    private const float EndpointConnectionToleranceUnits = 1.25f;
    private const int MaxUndoSnapshots = 32;
    private const long MaxUndoSnapshotBytes = 128L * 1024 * 1024;
    private const float PasteOffsetUnits = 96f;
    private const int MaxFreehandSamples = 16_384;
    private const float FreehandSampleSpacingPixels = 1.25f;
    private const int VaultDrawerExpandedWidth = 306;

    private VectorProject _project = VectorProject.CreateEmpty();
    private VectorScene _scene;
    private readonly VectorScene _sceneEditStage = new();
    private readonly VectorScene _drawingObjectUnderlayStage = new();
    private readonly VectorScene _onionSkinStage = new();
    private readonly VectorScene _dragPreviewStage = new();
    private SceneCompositionResult _sceneCompositionResult = SceneCompositionResult.Empty;
    private SceneCompositionResult _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
    private readonly StageControl _stage;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IdleTimerIntervalMs };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _metricsClock = Stopwatch.StartNew();
    private FixedStepBatcher _updateBatcher = new(TargetUps);
    private readonly DrawSettings _drawSettings = new();
    private readonly Label _fps = MetricLabel("FPS 0", 76);
    private readonly Label _draw = MetricLabel("Draw 0", 210);
    private readonly Label _atoms = MetricLabel("Atoms 0", 210);
    private readonly Label _zoom = MetricLabel("Zoom 100%", 112);
    private readonly Label _selected = InspectorLabel("Selected: None");
    private readonly Label _selectedLayer = InspectorLabel("Layer: -");
    private readonly Label _selectedAtoms = InspectorLabel("Atoms: -");
    private readonly Label _objectMetric = InspectorLabel("Objects: 0");
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();
    private readonly ToolMode[] _shapeTools = [ToolMode.Rectangle, ToolMode.Ellipse, ToolMode.Triangle, ToolMode.Polygon, ToolMode.Star];
    private readonly ToolMode[] _lineTools = [ToolMode.Line, ToolMode.Pen, ToolMode.Pencil];
    private readonly ToolMode[] _brushTools = [ToolMode.Brush, ToolMode.PressureBrush];
    private readonly Dictionary<ToolMode, Button> _shapeFlyoutButtons = new();
    private readonly Dictionary<ToolMode, Button> _lineFlyoutButtons = new();
    private readonly Dictionary<ToolMode, Button> _brushFlyoutButtons = new();
    private readonly System.Windows.Forms.Timer _shapeFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _lineFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _brushFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _vaultDrawerTimer = new() { Interval = 16 };
    private DateTime _shapeFlyoutHideAtUtc;
    private DateTime _lineFlyoutHideAtUtc;
    private DateTime _brushFlyoutHideAtUtc;
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
    private TimelineFrameClipboard? _timelineClipboard;
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _renderFpsStatus = StatusLabel("Render FPS 0");
    private readonly ToolStripStatusLabel _animationFpsStatus = StatusLabel("Animation FPS 24");
    private readonly ToolStripStatusLabel _zoomStatus = StatusLabel("Zoom 100%");
    private readonly ToolStripStatusLabel _devReloadStatus = StatusLabel("Module Reload On");
    private WindowChromeButton? _maximizeButton;
    private readonly PlaybackSettingsPanel _playbackSettings = new();
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly BrushTipPanel _brushTipPanel = new();
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly SceneEditorPanel _sceneEditorPanel = new();
    private readonly LibraryVaultPanel _libraryVaultPanel = new();
    private readonly AnimatedContextMenuStrip _stageContextMenu = new();
    private readonly ToolStripMenuItem _convertLineToFillMenuItem = new("Convert Line to Fill");
    private readonly ToolStripMenuItem _mergeAndSimplifyLinesMenuItem = new("Merge and Simplify Lines");
    private readonly Panel _inspectorHost = new();
    private readonly ThemedScrollPanel _basicInspectorPage = new();
    private readonly Panel _objectInspector = new();
    private readonly ThemedScrollPanel _sceneEditPage = new();
    private readonly Panel _animationPage = new();
    private SvgIconButton? _shapeToolButton;
    private FlowLayoutPanel? _shapeToolFlyout;
    private SvgIconButton? _lineToolButton;
    private FlowLayoutPanel? _lineToolFlyout;
    private SvgIconButton? _brushToolButton;
    private FlowLayoutPanel? _brushToolFlyout;
    private Panel? _vaultDrawer;
    private SvgIconButton? _vaultButton;
    private ToolMode _tool = ToolMode.Select;
    private ToolMode _activeShapeTool = ToolMode.Rectangle;
    private ToolMode _activeLineTool = ToolMode.Line;
    private ToolMode _activeBrushTool = ToolMode.Brush;
    private BrushShape _brushShape = BrushShape.CreateTraditionalBrush();
    private bool _playing;
    private int _frame;
    private int _selectedObject = -1;
    private DrawingElementHit _selectedElement = DrawingElementHit.None;
    private readonly List<DrawingElementHit> _selectedElements = new();
    private readonly List<int> _selectedObjects = new();
    private string _selectedSceneInstanceId = "";
    private int[] _selectedSceneInstanceObjectIndices = [];
    private bool _sceneInstanceMoveActive;
    private bool _sceneInstancePreviewDirty;
    private string _dragPreviewDrawingObjectId = "";
    private PointF _dragPreviewPosition;
    private readonly Dictionary<int, PointF> _selectedMoveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurveStarts = new();
    private readonly Dictionary<int, (PointF Start, PointF End)> _selectedGradientStarts = new();
    private readonly List<LineEndpointEditStart> _lineEndpointEditStarts = new();
    private readonly Stack<VectorSceneSnapshot> _undoStack = new();
    private readonly Stack<SceneTimelineUndoEntry> _sceneTimelineUndoStack = new();
    private OnionSkinRangeEditSession? _onionSkinRangeEditSession;
    private readonly List<ClipboardObject> _clipboardObjects = new();
    private readonly List<PointF> _freehandSamples = new(1024);
    private readonly List<PressureBrushSample> _pressureBrushSamples = new(1024);
    private bool _brushColorPaletteActive;
    private Color[] _brushColorPaletteColors = [];
    private Point _brushColorPaletteCenter;
    private int _brushColorPaletteHoverIndex = -1;
    private PointF[][] _fillHoverPreviewContours = [];
    private VectorScene? _fillHoverPreviewScene;
    private long _fillHoverPreviewRevision = -1;
    private int _fillHoverPreviewFrame = -1;
    private Point? _lastMouse;
    private Point? _startScreen;
    private PointF? _startWorld;
    private PointF? _selectedStart;
    private PointF? _curveControlStart;
    private PointF? _resizeStartCenter;
    private SizeF? _resizeStartSize;
    private float _resizeStartAngle;
    private EditHandleKind _activeHandle = EditHandleKind.None;
    private TransformHandleKind _activeTransformHandle = TransformHandleKind.None;
    private RectangleF _transformCurrentBounds = RectangleF.Empty;
    private PointF _transformPivot;
    private PointF? _transformFocus;
    private GradientHandleKind _gradientHandle = GradientHandleKind.None;
    private int _gradientStopIndex = -1;
    private VectorSceneSnapshot? _gradientEditSnapshot;
    private PointF _transformLastPointer;
    private float _transformLastAngle;
    private bool _geometryDirty;
    private double _smoothedFps;
    private double _smoothedUps;
    private double _playbackAccumulator;
    private int _updatesThisSample;
    private int _rendersThisSample;
    private bool _syncingFrame;
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
    private bool _additiveSelection;
    private DrawingElementHit _pendingClickSelection = DrawingElementHit.None;
    private Point? _marqueeStart;
    private int[] _marqueeSelectionBase = [];
    private Point? _freehandLastScreen;
    private PointF? _penStartWorld;
    private PointF? _penEndWorld;
    private PointF? _penControlWorld;
    private Point? _penEndpointScreen;
    private bool _penSegmentDragging;
    private ShapeKind _lastSettingsShape = ShapeKind.Rectangle;
    private int _activeSceneIndex;
    private int _activeDrawingObjectIndex;
    private bool _undoCapturedForPointerEdit;
    private bool _freehandDrawing;
    private bool _freehandBrushStroke;
    private bool _freehandPressureBrush;
    private bool _freehandErasing;
    private long _freehandStartedTimestamp;
    private long _freehandLastSampleTimestamp;
    private bool _vaultDrawerOpen;
    private Color _freehandColor = Color.White;
    private float _freehandStrokeUnits;
    private float _standardStrokeWidthPoints = 2;
    private float _pencilStrokeWidthPoints = 2;
    private float _brushStrokeWidthPoints = 8;
    private float _eraserStrokeWidthPoints = 8;
    private MaterialEditSession? _materialEditSession;
    private MarqueeMaterializationSession? _marqueeMaterializationSession;
    private bool _fillMergePendingAfterMaterialEdit;
    private bool _lineMergePendingAfterMaterialEdit;
    private Point _stageContextMenuLocation;
    private int _stageContextMenuLineObject = -1;
    private readonly EditorRestartState? _restartState;
    private bool _restartRequested;

    private readonly record struct LineEndpointEditStart(int ObjectIndex, bool StartEndpoint, PointF OriginalEndpoint, PointF OppositeEndpoint, PointF Control, bool KeepStraight);

    private readonly record struct DrawingStackKey(long Order, double SubOrder);

    private sealed record SceneTimelineUndoEntry(
        SceneDefinition Scene,
        AnimationTimelineSnapshot Snapshot,
        SceneLayerSnapshot? LayerSnapshot = null);

    private sealed class OnionSkinRangeEditSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public bool Changed { get; set; }
    }

    private sealed class MaterialEditSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public required int[] SelectedObjects { get; init; }
        public required DrawingElementHit[] SelectedElements { get; init; }
        public required DrawingElementHit PrimaryElement { get; init; }
        public bool UndoPushed { get; set; }
        public bool HierarchyDirty { get; set; }
    }

    private sealed record MarqueeMaterializationSession(VectorScene Scene, VectorSceneSnapshot Snapshot);

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
        PointF[][]? PathWorldContours,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle);

    public MainForm()
        : this(null)
    {
    }

    internal static MainForm CreateForRestart(EditorRestartState state) => new(state);

    private MainForm(EditorRestartState? restartState)
    {
        _restartState = restartState;
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
        BuildStageContextMenu();
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = 192 };
        _drawSettingsPanel = new DrawSettingsPanel(_drawSettings);
        _brushTipPanel.SetBrushShape(_brushShape);
        _brushTipPanel.SetBrushStrokeSettings(
            _drawSettings.BrushFrequency,
            _drawSettings.BrushContinuous,
            _brushStrokeWidthPoints,
            _drawSettings.PressureBrushSmoothing);
        _drawSnappingStrip = new DrawSnappingStrip(_drawSettings);
        _shapeFlyoutHideTimer.Interval = 100;
        _shapeFlyoutHideTimer.Tick += (_, _) => UpdateShapeToolFlyoutVisibility();
        _lineFlyoutHideTimer.Interval = 100;
        _lineFlyoutHideTimer.Tick += (_, _) => UpdateLineToolFlyoutVisibility();
        _brushFlyoutHideTimer.Interval = 100;
        _brushFlyoutHideTimer.Tick += (_, _) => UpdateBrushToolFlyoutVisibility();
        _vaultDrawerTimer.Tick += (_, _) => TickVaultDrawer();
        BuildUi();
        HookEvents();
        if (_restartState is null) CreateNewProject();
        else RestoreRestartState(_restartState);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (_restartState is not null)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = _restartState.WindowBounds;
            Shown += (_, _) => RestoreRestartPresentation(_restartState);
        }
    }

    public event EventHandler<EditorRestartRequestedEventArgs>? RestartRequested;
    public event EventHandler? ProcessRestartRequested;

    private void BuildStageContextMenu()
    {
        _stageContextMenu.Items.Add(_convertLineToFillMenuItem);
        _stageContextMenu.Items.Add(new ToolStripSeparator());
        _stageContextMenu.Items.Add(_mergeAndSimplifyLinesMenuItem);
        _convertLineToFillMenuItem.Click += (_, _) => ConvertStageContextLineToFill();
        _mergeAndSimplifyLinesMenuItem.Click += (_, _) => MergeAndSimplifyLineSegments(_stageContextMenuLocation);
        _stageContextMenu.Opening += (_, _) =>
        {
            _stageContextMenuLineObject = -1;
            _mergeAndSimplifyLinesMenuItem.Enabled = !DrawingToolsBlocked()
                && CollectLineMergeTargets(_stageContextMenuLocation, out _).Count >= 2;
            if (TryGetStageContextLineObject(_stageContextMenuLocation, out var objectIndex))
            {
                _stageContextMenuLineObject = objectIndex;
            }

            var selectedLines = _selectedObjects
                .Where(index => _scene.CanConvertLineToFill(index, _frame))
                .Distinct()
                .ToArray();
            var targetCount = _stageContextMenuLineObject >= 0 && !selectedLines.Contains(_stageContextMenuLineObject)
                ? 1
                : selectedLines.Length;
            _convertLineToFillMenuItem.Enabled = targetCount > 0;
            _convertLineToFillMenuItem.Text = targetCount > 1 ? "Convert Lines to Fill" : "Convert Line to Fill";
        };
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
        var restart = CreateWindowButton(WindowChromeButtonKind.Restart, "Restart editor");
        restart.Click += (_, _) => RequestEditorRestart();
        restart.MouseEnter += (_, _) => _toolTip.ShowFor(restart, RestartEditorToolTip());
        restart.MouseLeave += (_, _) => _toolTip.HideTip();
        var minimize = CreateWindowButton(WindowChromeButtonKind.Minimize, "Minimize");
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeButton = CreateWindowButton(WindowChromeButtonKind.Maximize, "Maximize");
        _maximizeButton.Click += (_, _) => ToggleMaximized();
        var close = CreateWindowButton(WindowChromeButtonKind.Close, "Close");
        close.Click += (_, _) => Close();
        top.Controls.Add(restart);
        top.Controls.Add(minimize);
        top.Controls.Add(_maximizeButton);
        top.Controls.Add(close);
        top.Resize += (_, _) => PositionWindowChromeButtons(top, restart, minimize, _maximizeButton, close);
        Resize += (_, _) => UpdateWindowChromeState();
        PositionWindowChromeButtons(top, restart, minimize, _maximizeButton, close);
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

        _inspectorHost.Dock = DockStyle.Right;
        _inspectorHost.Width = 324;
        _inspectorHost.BackColor = Theme.Panel;
        _inspectorHost.Padding = new Padding(14, 16, 14, 12);
        // The active inspector page owns scrolling. A second auto-scroll host resets the child page during refresh.
        _inspectorHost.AutoScroll = false;
        PaintLeftBorder(_inspectorHost);
        body.Controls.Add(_inspectorHost);
        BuildInspectorPages(_inspectorHost);

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
        AddTool(tools, SvgIconKind.Transform, ToolMode.Transform, "Free Transform Tool");
        AddShapeToolGroup(tools, stagePanel);
        AddLineToolGroup(tools, stagePanel);
        AddBrushToolGroup(tools, stagePanel);
        AddTool(tools, SvgIconKind.Fill, ToolMode.Fill, "Fill Tool");
        AddTool(tools, SvgIconKind.InkBottle, ToolMode.InkBottle, "Ink Bottle Tool");
        AddTool(tools, SvgIconKind.Gradient, ToolMode.Gradient, "Gradient Tool");
        AddTool(tools, SvgIconKind.Eraser, ToolMode.Eraser, "Eraser Tool");

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

    private static WindowChromeButton CreateWindowButton(WindowChromeButtonKind kind, string name)
    {
        return new WindowChromeButton(kind)
        {
            Top = 12,
            AccessibleName = name
        };
    }

    private static void PositionWindowChromeButtons(
        Control top,
        Control restart,
        Control minimize,
        Control maximize,
        Control close)
    {
        var buttonTop = Math.Max(0, (top.ClientSize.Height - close.Height) / 2);
        close.Left = top.ClientSize.Width - close.Width - 8;
        maximize.Left = close.Left - maximize.Width - 2;
        minimize.Left = maximize.Left - minimize.Width - 2;
        restart.Left = minimize.Left - restart.Width - 2;
        close.Top = buttonTop;
        maximize.Top = buttonTop;
        minimize.Top = buttonTop;
        restart.Top = buttonTop;
    }

    private void ToggleVaultDrawer()
    {
        if (_vaultDrawer is null) return;
        _vaultDrawerOpen = !_vaultDrawerOpen;
        if (_vaultDrawerOpen)
        {
            _libraryVaultPanel.RefreshProjectObjects();
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
            _lineFlyoutHideTimer.Dispose();
            _vaultDrawerTimer.Dispose();
            _timer.Dispose();
            _stageContextMenu.Dispose();
            _toolTip.Dispose();
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
        Theme.StyleStatusStrip(_statusBar);
        _statusBar.Padding = new Padding(8, 2, 8, 2);
        _statusBar.Items.Add(_renderFpsStatus);
        _statusBar.Items.Add(StatusSeparator());
        _statusBar.Items.Add(_animationFpsStatus);
        _statusBar.Items.Add(StatusSeparator());
        _statusBar.Items.Add(_zoomStatus);
        if (IsModuleHotReloadEnabled())
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
        _basicInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        _objectInspector.Dock = DockStyle.Top;
        _objectInspector.Height = 142;
        _objectInspector.BackColor = Theme.Panel;
        BuildInspector(_objectInspector);
        _materialEditor.Dock = DockStyle.Top;
        _brushTipPanel.Dock = DockStyle.Top;
        _brushTipPanel.Height = 218;
        _brushTipPanel.Visible = false;
        _drawSettingsPanel.Dock = DockStyle.Top;
        _drawSettingsPanel.Height = 164;
        _basicInspectorPage.Content.Controls.Add(_materialEditor);
        _basicInspectorPage.Content.Controls.Add(_brushTipPanel);
        _basicInspectorPage.Content.Controls.Add(_drawSettingsPanel);
        _basicInspectorPage.Content.Controls.Add(_objectInspector);
        _objectInspector.Visible = false;
        _drawSettingsPanel.Visible = false;

        _sceneEditPage.Dock = DockStyle.Fill;
        _sceneEditPage.BackColor = Theme.Panel;
        _hierarchyPanel.Dock = DockStyle.Fill;
        _sceneEditorPanel.Dock = DockStyle.Top;
        _sceneEditorPanel.Height = 508;
        _sceneEditPage.Content.Controls.Add(_hierarchyPanel);
        _sceneEditPage.Content.Controls.Add(_sceneEditorPanel);
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
        foreach (var tab in _drawingObjectTabs.Controls.Cast<Control>().ToArray()) tab.Dispose();
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
        return (ModifierKeys & Keys.Shift) == Keys.Shift || (GetKeyState(VkShift) & 0x8000) != 0;
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

    private void RenameDrawingObject(string drawingObjectId)
    {
        var drawingObject = _drawingObjects.FirstOrDefault(item => string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null) return;
        if (!DrawingObjectNameDialog.TryAsk(this, "Rename Drawing Object", "Drawing object name", drawingObject.Name, out var name)) return;
        if (!_project.TryRenameDrawingObject(drawingObjectId, name)) return;

        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Renamed drawing object: {drawingObject.Name}");
    }

    private void DuplicateDrawingObject(string drawingObjectId)
    {
        if (!_project.TryDuplicateDrawingObject(drawingObjectId, out var duplicate) || duplicate is null) return;

        var index = -1;
        for (var i = 0; i < _drawingObjects.Count; i++)
        {
            if (!ReferenceEquals(_drawingObjects[i], duplicate)) continue;
            index = i;
            break;
        }

        if (index < 0) return;
        SelectDrawingObject(index);
        AppLog.Info($"Duplicated drawing object: {duplicate.Name}");
    }

    private void DeleteDrawingObject(string drawingObjectId)
    {
        var index = -1;
        for (var i = 0; i < _drawingObjects.Count; i++)
        {
            if (!string.Equals(_drawingObjects[i].Id, drawingObjectId, StringComparison.Ordinal)) continue;
            index = i;
            break;
        }

        if (index < 0) return;
        if (_drawingObjects.Count <= 1)
        {
            MessageBox.Show(this, "A project must retain at least one drawing object.", "Delete Drawing Object", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var drawingObject = _drawingObjects[index];
        var sceneInstanceCount = _scenes.Sum(scene => scene.Instances.Count(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal)));
        var nestedInstanceCount = _drawingObjects.Sum(container => container.Instances.Count(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal)));
        var referenceNotice = sceneInstanceCount + nestedInstanceCount > 0
            ? $"\r\n\r\nThis will also remove {sceneInstanceCount} scene instance(s) and {nestedInstanceCount} nested instance(s) that reference it."
            : string.Empty;
        if (MessageBox.Show(
                this,
                $"Delete drawing object \"{drawingObject.Name}\"?{referenceNotice}",
                "Delete Drawing Object",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        var wasSceneCompositionContext = IsSceneCompositionContext();
        if (!_project.TryRemoveDrawingObject(drawingObjectId, out _)) return;

        if (_activeDrawingObjectIndex > index) _activeDrawingObjectIndex--;
        else if (_activeDrawingObjectIndex == index) _activeDrawingObjectIndex = Math.Min(index, _drawingObjects.Count - 1);

        ResetEditHistory();
        if (wasSceneCompositionContext) BindSceneEditStage(resetView: false);
        else BindActiveDrawingObjectScene(resetView: false);
        BuildDrawingObjectTabs();
        AppLog.Info($"Deleted drawing object: {drawingObject.Name}");
    }

    private void RefreshDrawingObjectAssetPresentation()
    {
        BuildDrawingObjectTabs();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
        _libraryVaultPanel.RefreshProjectObjects();
        if (IsSceneCompositionContext()) RebuildSceneComposition();
        else RebuildDrawingObjectUnderlay();
        UpdateInspector();
        UpdateStatusBar();
        _stage.Invalidate();
    }

    private DrawingObjectDefinition? ActiveDrawingObject()
    {
        return _activeDrawingObjectIndex >= 0 && _activeDrawingObjectIndex < _drawingObjects.Count
            ? _drawingObjects[_activeDrawingObjectIndex]
            : null;
    }

    private void BindActiveDrawingObjectScene(bool resetView)
    {
        CancelPenCurve();
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
        _libraryVaultPanel.SetActiveDrawingObject(drawingObject.Id);
        _playbackSettings.SetFrameRange(0, _scene.FrameCount - 1);
        SyncTimelineFrameRange();
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
        CancelPenCurve();
        if (!ReferenceEquals(_scene, _sceneEditStage)) ResetUndoHistory();
        _scene = _sceneEditStage;
        var sceneDefinition = ActiveScene();
        _sceneCompositionResult = SceneCompositionBuilder.Build(_sceneEditStage, sceneDefinition, _drawingObjects, _frame);
        _stage.BindScene(_scene);
        _stage.ConfigureReferenceView(sceneDefinition);
        if (sceneDefinition is not null) _timeline.BindSceneDefinition(sceneDefinition);
        else _timeline.BindScene(_scene);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _libraryVaultPanel.SetActiveDrawingObject(ActiveDrawingObject()?.Id);
        _playbackSettings.SetFrameRange(0, Math.Max(0, (sceneDefinition?.FrameCount ?? _scene.FrameCount) - 1));
        SyncTimelineFrameRange();
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
        _sceneInstancePreviewDirty = false;
        RefreshSelectedSceneInstanceObjectIndices();
        if (!_playing)
        {
            _hierarchyPanel.BindScene(_sceneEditStage);
            _sceneEditorPanel.BindScene(_sceneEditStage);
        }

        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
    }

    private void RebuildDrawingObjectUnderlay()
    {
        if (IsSceneCompositionContext())
        {
            _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
            _stage.BindUnderlayScene(null);
            _stage.BindOnionSkinScene(null);
            return;
        }

        _drawingObjectUnderlayResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            _drawingObjectUnderlayStage,
            ActiveDrawingObject(),
            _drawingObjects,
            _frame);
        _stage.BindUnderlayScene(_drawingObjectUnderlayStage);
        RebuildOnionSkinPreview();
    }

    private void RebuildOnionSkinPreview()
    {
        if (IsSceneCompositionContext())
        {
            _stage.BindOnionSkinScene(null);
            return;
        }

        _scene.BuildOnionSkinPreview(_onionSkinStage, _frame);
        _stage.BindOnionSkinScene(_onionSkinStage);
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
            HideToolFlyouts();
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
            AccessibleName = ShapeToolName(_activeShapeTool),
            ShowsToolGroupIndicator = true
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
                _toolTip.ShowFor(button, ShapeToolName(tool));
                ShowShapeToolFlyout();
            };
            button.MouseLeave += (_, _) =>
            {
                _toolTip.HideTip();
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
        HideLineToolFlyout();
        HideBrushToolFlyout();
        _shapeFlyoutHideAtUtc = default;
        tools.PerformLayout();
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

    private void HideToolFlyouts()
    {
        HideShapeToolFlyout();
        HideLineToolFlyout();
        HideBrushToolFlyout();
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

    private void AddLineToolGroup(FlowLayoutPanel panel, Control overlayParent)
    {
        _lineToolButton = new SvgIconButton(ToolIconKind(_activeLineTool))
        {
            Margin = new Padding(0, 0, 0, 8),
            Tag = _activeLineTool,
            AccessibleName = LineToolName(_activeLineTool),
            ShowsToolGroupIndicator = true
        };
        Theme.StyleButton(_lineToolButton);
        _lineToolButton.MouseEnter += (_, _) =>
        {
            _toolTip.HideTip();
            ShowLineToolFlyout();
        };
        _lineToolButton.MouseLeave += (_, _) => ScheduleLineToolFlyoutHideAfter(350);
        _lineToolButton.Click += (_, _) =>
        {
            ActivateTool(_activeLineTool);
            ShowLineToolFlyout();
        };
        panel.Controls.Add(_lineToolButton);

        _lineToolFlyout = new FlowLayoutPanel
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
        PaintFullBorder(_lineToolFlyout);
        _lineToolFlyout.MouseEnter += (_, _) => ShowLineToolFlyout();
        _lineToolFlyout.MouseLeave += (_, _) => ScheduleLineToolFlyoutHideAfter(350);
        overlayParent.Controls.Add(_lineToolFlyout);

        foreach (var tool in _lineTools)
        {
            var button = new SvgIconButton(ToolIconKind(tool))
            {
                Margin = new Padding(0, 0, 0, tool == _lineTools[^1] ? 0 : 8),
                Tag = tool,
                AccessibleName = LineToolName(tool)
            };
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, LineToolName(tool));
                ShowLineToolFlyout();
            };
            button.MouseLeave += (_, _) =>
            {
                _toolTip.HideTip();
                ScheduleLineToolFlyoutHideAfter(350);
            };
            button.Click += (_, _) =>
            {
                ActivateTool(tool);
                HideLineToolFlyout();
            };
            _lineFlyoutButtons[tool] = button;
            _lineToolFlyout.Controls.Add(button);
        }
    }

    private void AddBrushToolGroup(FlowLayoutPanel panel, Control overlayParent)
    {
        _brushToolButton = new SvgIconButton(ToolIconKind(_activeBrushTool))
        {
            Margin = new Padding(0, 0, 0, 8),
            Tag = _activeBrushTool,
            AccessibleName = BrushToolName(_activeBrushTool),
            ShowsToolGroupIndicator = true
        };
        Theme.StyleButton(_brushToolButton);
        _brushToolButton.MouseEnter += (_, _) =>
        {
            _toolTip.HideTip();
            ShowBrushToolFlyout();
        };
        _brushToolButton.MouseLeave += (_, _) => ScheduleBrushToolFlyoutHideAfter(350);
        _brushToolButton.Click += (_, _) =>
        {
            ActivateTool(_activeBrushTool);
            ShowBrushToolFlyout();
        };
        panel.Controls.Add(_brushToolButton);

        _brushToolFlyout = new FlowLayoutPanel
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
        PaintFullBorder(_brushToolFlyout);
        _brushToolFlyout.MouseEnter += (_, _) => ShowBrushToolFlyout();
        _brushToolFlyout.MouseLeave += (_, _) => ScheduleBrushToolFlyoutHideAfter(350);
        overlayParent.Controls.Add(_brushToolFlyout);

        foreach (var tool in _brushTools)
        {
            var button = new SvgIconButton(ToolIconKind(tool))
            {
                Margin = new Padding(0, 0, 0, tool == _brushTools[^1] ? 0 : 8),
                Tag = tool,
                AccessibleName = BrushToolName(tool)
            };
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, BrushToolName(tool));
                ShowBrushToolFlyout();
            };
            button.MouseLeave += (_, _) =>
            {
                _toolTip.HideTip();
                ScheduleBrushToolFlyoutHideAfter(350);
            };
            button.Click += (_, _) =>
            {
                ActivateTool(tool);
                HideBrushToolFlyout();
            };
            _brushFlyoutButtons[tool] = button;
            _brushToolFlyout.Controls.Add(button);
        }
    }

    private void ShowBrushToolFlyout()
    {
        if (_brushToolButton is null || _brushToolFlyout is null || _brushToolButton.Parent is not Control tools) return;
        HideShapeToolFlyout();
        HideLineToolFlyout();
        _brushFlyoutHideAtUtc = default;
        tools.PerformLayout();
        _brushToolFlyout.Left = tools.Left + tools.Width - 1;
        _brushToolFlyout.Top = tools.Top + _brushToolButton.Top;
        _brushToolFlyout.Visible = true;
        _brushToolFlyout.BringToFront();
        _brushFlyoutHideTimer.Stop();
        RefreshToolButtons();
    }

    private void HideBrushToolFlyout()
    {
        _brushFlyoutHideTimer.Stop();
        _brushFlyoutHideAtUtc = default;
        if (_brushToolFlyout is not null) _brushToolFlyout.Visible = false;
    }

    private void ScheduleBrushToolFlyoutHideAfter(int delayMilliseconds)
    {
        if (_brushToolFlyout is null) return;
        _brushFlyoutHideAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, delayMilliseconds));
        if (_brushToolFlyout.Visible && !_brushFlyoutHideTimer.Enabled) _brushFlyoutHideTimer.Start();
    }

    private void UpdateBrushToolFlyoutVisibility()
    {
        if (_brushToolFlyout is null || !_brushToolFlyout.Visible)
        {
            _brushFlyoutHideTimer.Stop();
            return;
        }

        if (_brushFlyoutHideAtUtc != default && DateTime.UtcNow >= _brushFlyoutHideAtUtc) HideBrushToolFlyout();
    }

    private void ShowLineToolFlyout()
    {
        if (_lineToolButton is null || _lineToolFlyout is null || _lineToolButton.Parent is not Control tools) return;
        HideShapeToolFlyout();
        HideBrushToolFlyout();
        _lineFlyoutHideAtUtc = default;
        tools.PerformLayout();
        _lineToolFlyout.Left = tools.Left + tools.Width - 1;
        _lineToolFlyout.Top = tools.Top + _lineToolButton.Top;
        _lineToolFlyout.Visible = true;
        _lineToolFlyout.BringToFront();
        _lineFlyoutHideTimer.Stop();
        RefreshToolButtons();
    }

    private void HideLineToolFlyout()
    {
        _lineFlyoutHideTimer.Stop();
        _lineFlyoutHideAtUtc = default;
        if (_lineToolFlyout is null) return;
        _lineToolFlyout.Visible = false;
    }

    private void ScheduleLineToolFlyoutHideAfter(int delayMilliseconds)
    {
        if (_lineToolFlyout is null) return;
        _lineFlyoutHideAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, delayMilliseconds));
        StartLineToolFlyoutVisibilityCheck();
    }

    private void StartLineToolFlyoutVisibilityCheck()
    {
        if (_lineToolFlyout is null || !_lineToolFlyout.Visible) return;
        if (!_lineFlyoutHideTimer.Enabled) _lineFlyoutHideTimer.Start();
    }

    private void UpdateLineToolFlyoutVisibility()
    {
        if (_lineToolFlyout is null || !_lineToolFlyout.Visible)
        {
            _lineFlyoutHideTimer.Stop();
            return;
        }

        if (_lineFlyoutHideAtUtc == default || DateTime.UtcNow < _lineFlyoutHideAtUtc) return;
        HideLineToolFlyout();
    }

    private void ActivateTool(ToolMode tool)
    {
        if (DrawingToolsBlocked() && IsBasicDrawingOnlyTool(tool)) return;
        CancelGradientPointer(restore: true);
        HideBrushColorPalette();
        _tool = tool;
        _stage.ClearDrawingPreview();
        if (tool != ToolMode.Pen) CancelPenCurve();
        CancelFreehandStroke();
        if (!IsShapeTool(tool)) HideShapeToolFlyout();
        if (!IsLineTool(tool)) HideLineToolFlyout();
        if (!IsBrushTool(tool)) HideBrushToolFlyout();
        if (IsShapeTool(tool)) _activeShapeTool = tool;
        else if (IsLineTool(tool)) _activeLineTool = tool;
        else if (IsBrushTool(tool)) _activeBrushTool = tool;
        ApplyToolStrokeWidth(tool);
        if (IsBrushTool(tool) || tool == ToolMode.Eraser) SyncBrushTipSettings();
        if (ToolShapeKind(tool) is { } shape)
        {
            _drawSettings.ShapeKind = shape;
            _drawSettings.NotifyChanged();
        }

        RefreshToolButtons();
        _drawSettingsPanel.SetEraserOptionsVisible(_tool == ToolMode.Eraser);
        _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
        UpdateTransformOverlay();
        UpdateGradientOverlay();
        _stage.Focus();
        RefreshInteractionCursorAtPointer();
    }

    private void ImportBrushTip()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Brush tip images|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Import 128x128 Brush Tip"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!BrushShape.TryLoad(dialog.FileName, out var brushShape, out var error) || brushShape is null)
        {
            MessageBox.Show(this, error, "Brush Tip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _brushShape = brushShape;
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void RestoreDefaultBrushTip()
    {
        _brushShape = BrushShape.CreateSoftRound();
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void SelectTraditionalBrushTip()
    {
        _brushShape = BrushShape.CreateTraditionalBrush();
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateDefaultBrushHardness()
    {
        if (!_brushShape.IsDefaultSoftRound) return;
        _brushShape = BrushShape.CreateSoftRound(_brushTipPanel.Hardness);
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateBrushStrokeSettings()
    {
        _drawSettings.BrushFrequency = _brushTipPanel.Frequency;
        _drawSettings.BrushContinuous = _brushTipPanel.Continuous;
        _drawSettings.PressureBrushSmoothing = _brushTipPanel.PressureSmoothing;
        _drawSettings.NotifyChanged();
    }

    private void UpdateBrushTipSize()
    {
        if (!IsBrushTool(_tool) && _tool != ToolMode.Eraser) return;
        var sizePoints = _brushTipPanel.SizePoints;
        if (IsBrushTool(_tool)) _brushStrokeWidthPoints = sizePoints;
        else _eraserStrokeWidthPoints = sizePoints;
        _materialEditor.SetMaterial(_materialEditor.Fill, _materialEditor.Stroke, sizePoints, _materialEditor.Opacity);
        RefreshInteractionCursorAtPointer();
    }

    private float ActiveBrushTipSizePoints() => _tool == ToolMode.Eraser
        ? _eraserStrokeWidthPoints
        : _brushStrokeWidthPoints;

    private void SyncBrushTipSettings()
    {
        _brushTipPanel.SetBrushStrokeSettings(
            _drawSettings.BrushFrequency,
            _drawSettings.BrushContinuous,
            ActiveBrushTipSizePoints(),
            _drawSettings.PressureBrushSmoothing);
    }

    private void ApplyToolStrokeWidth(ToolMode tool)
    {
        float? width = tool switch
        {
            ToolMode.Pencil => _pencilStrokeWidthPoints,
            ToolMode.Eraser => _eraserStrokeWidthPoints,
            _ when IsStandardStrokeTool(tool) => _standardStrokeWidthPoints,
            _ when IsBrushTool(tool) => _brushStrokeWidthPoints,
            _ => null
        };
        if (width is null || Math.Abs(_materialEditor.StrokeWidth - width.Value) < 0.001f) return;

        _materialEditor.SetMaterial(_materialEditor.Fill, _materialEditor.Stroke, width.Value, _materialEditor.Opacity);
    }

    private void ApplyToolCursor()
    {
        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.Fill)
        {
            _stage.ClearBrushTipCursor();
            var screen = _stage.PointToClient(Cursor.Position);
            if (_stage.ClientRectangle.Contains(screen))
            {
                _stage.SetFillToolCursor(screen, ActiveColor());
                UpdateFillHoverPreview(screen);
            }
            else
            {
                _stage.ClearFillToolCursor();
                ClearFillHoverPreview();
            }
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool is ToolMode.InkBottle or ToolMode.Gradient)
        {
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = IsDrawingTool(_tool)
            ? Cursors.Cross
            : _tool == ToolMode.Hand ? Cursors.SizeAll : Cursors.Default;
    }

    private void UpdateInteractionCursor(Point screen)
    {
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            _stage.ClearBrushTipCursor();
            return;
        }

        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.SetBrushTipCursor(screen, _brushShape, ActiveStrokeUnits(), _tool == ToolMode.Eraser);
            _stage.Cursor = Cursors.Cross;
            return;
        }

        _stage.ClearBrushTipCursor();

        if (_tool == ToolMode.Fill)
        {
            _stage.SetFillToolCursor(screen, ActiveColor());
            UpdateFillHoverPreview(screen);
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.InkBottle)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = Cursors.Cross;
            return;
        }

        if (_tool == ToolMode.Gradient)
        {
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _stage.HitTestGradientOverlay(screen).Kind == GradientHandleKind.None
                ? Cursors.Cross
                : Cursors.SizeAll;
            return;
        }

        _stage.ClearFillToolCursor();

        if (IsSceneCompositionContext() && !IsScene3DView() && _tool == ToolMode.Select)
        {
            _stage.Cursor = TryResolveSceneInstanceAt(_stage.ScreenToWorld(screen), out _)
                || IsPointerInsideSelectedSceneInstance(_stage.ScreenToWorld(screen))
                ? Cursors.SizeAll
                : Cursors.Default;
            return;
        }

        if (_tool == ToolMode.Transform)
        {
            var handle = _activeTransformHandle != TransformHandleKind.None
                ? _activeTransformHandle
                : _stage.HitTestTransformHandle(screen);
            _stage.Cursor = CursorForTransformHandle(handle);
            return;
        }

        if (_tool == ToolMode.Select)
        {
            var handle = _activeHandle != EditHandleKind.None
                ? _activeHandle
                : _stage.HitTestHoveredLineHandle(screen);
            if (handle == EditHandleKind.None && _selectedObject >= 0)
            {
                handle = _stage.HitTestHandle(screen, _selectedObject);
            }

            _stage.Cursor = CursorForEditHandle(handle);
            return;
        }

        ApplyToolCursor();
    }

    private void RefreshInteractionCursorAtPointer()
    {
        var screen = _stage.PointToClient(Cursor.Position);
        if (_stage.ClientRectangle.Contains(screen)) UpdateInteractionCursor(screen);
        else ApplyToolCursor();
    }

    private void UpdateFillHoverPreview(Point screen)
    {
        if (_tool != ToolMode.Fill || IsSceneCompositionContext() || IsScene3DView())
        {
            ClearFillHoverPreview();
            return;
        }

        var world = _stage.ScreenToWorld(screen);
        var color = ActiveColor();
        if (ReferenceEquals(_fillHoverPreviewScene, _scene)
            && _fillHoverPreviewRevision == _scene.GeometryRevision
            && _fillHoverPreviewFrame == _frame
            && _fillHoverPreviewContours.Length > 0
            && PointInFillPreview(world, _fillHoverPreviewContours))
        {
            _stage.SetFillPreview(_fillHoverPreviewContours, color);
            return;
        }

        PointF[][] contours;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid && hit.Key.Kind == DrawingElementKind.Fill)
        {
            contours = _scene.GetFillPartContours(hit, _frame);
        }
        else if (!_scene.TryGetClosedStrokeFillRegion(world, _frame, out contours))
        {
            ClearFillHoverPreview();
            return;
        }

        if (contours.Length == 0)
        {
            ClearFillHoverPreview();
            return;
        }

        _fillHoverPreviewContours = contours;
        _fillHoverPreviewScene = _scene;
        _fillHoverPreviewRevision = _scene.GeometryRevision;
        _fillHoverPreviewFrame = _frame;
        _stage.SetFillPreview(contours, color);
    }

    private void ClearFillHoverPreview()
    {
        _fillHoverPreviewContours = [];
        _fillHoverPreviewScene = null;
        _fillHoverPreviewRevision = -1;
        _fillHoverPreviewFrame = -1;
        _stage.ClearFillPreview();
    }

    private static bool PointInFillPreview(PointF point, IReadOnlyList<PointF[]> contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            if (PointOnFillPreviewBoundary(point, contour)) return true;
            if (PointInFillPreviewContour(point, contour)) inside = !inside;
        }

        return inside;
    }

    private static bool PointInFillPreviewContour(PointF point, IReadOnlyList<PointF> contour)
    {
        var inside = false;
        for (int index = 0, previous = contour.Count - 1; index < contour.Count; previous = index++)
        {
            var currentPoint = contour[index];
            var previousPoint = contour[previous];
            if ((currentPoint.Y > point.Y) == (previousPoint.Y > point.Y)) continue;
            var denominator = previousPoint.Y - currentPoint.Y;
            if (Math.Abs(denominator) < 0.0001f) continue;
            var crossingX = (previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / denominator + currentPoint.X;
            if (point.X < crossingX) inside = !inside;
        }

        return inside;
    }

    private static bool PointOnFillPreviewBoundary(PointF point, IReadOnlyList<PointF> contour)
    {
        for (var index = 0; index < contour.Count; index++)
        {
            var start = contour[index];
            var end = contour[(index + 1) % contour.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0001f) continue;
            var parameter = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
            var closest = new PointF(start.X + dx * parameter, start.Y + dy * parameter);
            var distanceX = point.X - closest.X;
            var distanceY = point.Y - closest.Y;
            if (distanceX * distanceX + distanceY * distanceY <= 0.75f * 0.75f) return true;
        }

        return false;
    }

    private static Cursor CursorForTransformHandle(TransformHandleKind handle)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft or TransformHandleKind.BottomRight => Cursors.SizeNWSE,
            TransformHandleKind.TopRight or TransformHandleKind.BottomLeft => Cursors.SizeNESW,
            TransformHandleKind.Top or TransformHandleKind.Bottom => Cursors.SizeNS,
            TransformHandleKind.Left or TransformHandleKind.Right => Cursors.SizeWE,
            TransformHandleKind.Move => Cursors.SizeAll,
            TransformHandleKind.Rotate
                or TransformHandleKind.RotateTopLeft
                or TransformHandleKind.RotateTopRight
                or TransformHandleKind.RotateBottomRight
                or TransformHandleKind.RotateBottomLeft => Cursors.Hand,
            TransformHandleKind.SkewTop or TransformHandleKind.SkewBottom => Cursors.SizeWE,
            TransformHandleKind.SkewLeft or TransformHandleKind.SkewRight => Cursors.SizeNS,
            TransformHandleKind.Focus => Cursors.Cross,
            _ => Cursors.Default
        };
    }

    private static Cursor CursorForEditHandle(EditHandleKind handle)
    {
        return handle switch
        {
            EditHandleKind.BoundsTopLeft or EditHandleKind.BoundsBottomRight => Cursors.SizeNWSE,
            EditHandleKind.BoundsTopRight or EditHandleKind.BoundsBottomLeft => Cursors.SizeNESW,
            EditHandleKind.LineStart or EditHandleKind.LineEnd or EditHandleKind.BezierControl => Cursors.Cross,
            _ => Cursors.Default
        };
    }

    private void HookEvents()
    {
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _workspaceTabs.WorldGridOpacityChanged += (_, _) => _stage.WorldGridOpacity = _workspaceTabs.WorldGridOpacity / 100f;
        _brushTipPanel.ImportRequested += (_, _) => ImportBrushTip();
        _brushTipPanel.SoftRoundRequested += (_, _) => RestoreDefaultBrushTip();
        _brushTipPanel.TraditionalBrushRequested += (_, _) => SelectTraditionalBrushTip();
        _brushTipPanel.FrequencyChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.ContinuousChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.HardnessChanged += (_, _) => UpdateDefaultBrushHardness();
        _brushTipPanel.PressureSmoothingChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.BrushSizeChanged += (_, _) => UpdateBrushTipSize();
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
        _timeline.AddLayerRequested += (_, _) => AddTimelineLayer();
        _timeline.CommandRequested += (_, e) => HandleTimelineCommand(e.Command, e.Cells);
        _timeline.LayerMoveRequested += (_, e) => MoveTimelineLayer(e.TrackId, e.DestinationIndex);
        _timeline.LayerColorRequested += (_, _) => ChooseTimelineLayerColor();
        _timeline.LayerOnionSkinRequested += (_, _) => ToggleTimelineLayerOnionSkin();
        _timeline.OnionSkinRangeRequested += (_, _) => ConfigureTimelineOnionSkinRange();
        _timeline.OnionSkinRangeChanged += (_, e) => SetTimelineOnionSkinRange(e.PreviousFrames, e.NextFrames);
        _timeline.OnionSkinRangeInteractionStarted += (_, _) => BeginTimelineOnionSkinRangeEdit();
        _timeline.OnionSkinRangeInteractionCompleted += (_, _) => CompleteTimelineOnionSkinRangeEdit();
        _timeline.OnionSkinRangeInteractionCanceled += (_, _) => CancelTimelineOnionSkinRangeEdit();
        _sceneEditorPanel.AddSceneRequested += (_, _) => AddScene();
        _sceneEditorPanel.AddDrawingObjectRequested += (_, _) => AddDrawingObject();
        _sceneEditorPanel.AddSceneInstanceRequested += (_, _) => AddSceneInstanceFromActiveDrawingObject();
        _sceneEditorPanel.SceneSelectionChanged += (_, e) => SelectScene(e.Index);
        _sceneEditorPanel.DrawingObjectSelectionChanged += (_, e) => SelectDrawingObject(e.Index);
        _sceneEditorPanel.DrawingObjectOpenRequested += (_, e) => OpenDrawingObjectEditor(e.DrawingObjectId);
        _sceneEditorPanel.DrawingObjectRenameRequested += (_, e) => RenameDrawingObject(e.DrawingObjectId);
        _sceneEditorPanel.DrawingObjectDuplicateRequested += (_, e) => DuplicateDrawingObject(e.DrawingObjectId);
        _sceneEditorPanel.DrawingObjectDeleteRequested += (_, e) => DeleteDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectOpenRequested += (_, e) => OpenDrawingObjectEditor(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectRenameRequested += (_, e) => RenameDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectDuplicateRequested += (_, e) => DuplicateDrawingObject(e.DrawingObjectId);
        _libraryVaultPanel.DrawingObjectDeleteRequested += (_, e) => DeleteDrawingObject(e.DrawingObjectId);
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
            SyncTimelineFrameRange();
            SetFrame(_frame);
        };
        _materialEditor.ContinuousEditStarted += (_, _) => BeginMaterialContinuousEdit();
        _materialEditor.ContinuousEditCompleted += (_, _) => CompleteMaterialContinuousEdit();
        _materialEditor.ContinuousEditCanceled += (_, _) => CancelMaterialContinuousEdit();
        _materialEditor.LineEndpointStyleChanged += (_, e) => ApplyLineEndpointStyle(e.EndpointStyle, e.StartEndpoint);
        _materialEditor.GradientChanged += (_, e) => ApplyGradientToSelection(e);
        _materialEditor.MaterialChanged += (_, e) =>
        {
            if (IsSceneCompositionContext()) return;
            if (e.StrokeWidthChanged)
            {
                if (_tool == ToolMode.Pencil) _pencilStrokeWidthPoints = e.StrokeWidth;
                else if (IsBrushTool(_tool)) _brushStrokeWidthPoints = e.StrokeWidth;
                else if (_tool == ToolMode.Eraser) _eraserStrokeWidthPoints = e.StrokeWidth;
                else if (IsStandardStrokeTool(_tool)) _standardStrokeWidthPoints = e.StrokeWidth;
            }
            if (IsBrushTool(_tool) || _tool == ToolMode.Eraser) SyncBrushTipSettings();

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
                var snapshot = CreateMaterialUndoSnapshot();
                var editedObject = _selectedObject;
                var hierarchyChanged = false;
                if (selectedKind != DrawingElementKind.None)
                {
                    var selectedElement = _selectedElement;
                    var materialized = _scene.MaterializeSelectedParts(new[] { selectedElement.Key }, _frame);
                    if (!materialized.Success || materialized.Parts.Length != 1)
                    {
                        _scene.RestoreSnapshot(snapshot);
                        SyncSelectionToStage();
                        return;
                    }

                    var materializedHit = materialized.Changed
                        ? new DrawingElementHit(materialized.Parts[0].Result, -1, 0, 1)
                        : selectedElement;
                    SetSelection(materializedHit);
                    editedObject = materializedHit.Key.ObjectIndex;
                    hierarchyChanged = materialized.Changed;
                }

                var geometryChanged = false;
                var shape = _scene.ShapeKind[editedObject];
                if (selectedKind == DrawingElementKind.Fill)
                {
                    _scene.Argb[editedObject] = TargetFillArgb(editedObject, e);
                    _scene.DisableLinearGradient(editedObject);
                    if (_scene.Stroke[editedObject] != 0)
                    {
                        _scene.Stroke[editedObject] = 0;
                        geometryChanged = true;
                    }

                    hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
                }
                else if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
                {
                    if (e.ApplyAll || e.StrokeChanged || e.OpacityChanged)
                    {
                        var color = TargetStrokeColor(editedObject, e);
                        _scene.Argb[editedObject] = Color.FromArgb(0, color).ToArgb();
                        _scene.StrokeArgb[editedObject] = color.ToArgb();
                    }

                    if (e.ApplyAll || e.StrokeWidthChanged)
                    {
                        var targetStrokeUnits = TargetStrokeUnits(editedObject, e, strokeUnits);
                        if (Math.Abs(_scene.Stroke[editedObject] - targetStrokeUnits) > 0.001f)
                        {
                            if (IsFreehandShape(shape))
                            {
                                _scene.UpdateFreehandStrokeWidth(editedObject, targetStrokeUnits, rebuildGeometryIndex: false);
                            }
                            else
                            {
                                _scene.Stroke[editedObject] = targetStrokeUnits;
                                _scene.Height[editedObject] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
                            }

                            geometryChanged = true;
                        }
                    }
                }
                else
                {
                    ApplyMaterialToWholeObject(editedObject, e, strokeUnits, out geometryChanged);
                    if ((e.ApplyAll || e.FillChanged || e.OpacityChanged) && IsFillShape(shape))
                    {
                        hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
                    }
                }

                if (geometryChanged) _scene.RebuildGeometryIndex();
                hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();
                PushMaterialUndoSnapshot(snapshot);
                RefreshHierarchyAfterMaterialChange(hierarchyChanged);
                UpdateInspector();
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
            SyncBrushTipSettings();
            if (_drawSettings.ShapeKind == _lastSettingsShape) return;
            _lastSettingsShape = _drawSettings.ShapeKind;
            if (ToolModeForShape(_drawSettings.ShapeKind) is { } tool)
            {
                _tool = tool;
                if (IsShapeTool(tool))
                {
                    _activeShapeTool = tool;
                    HideLineToolFlyout();
                }
                else if (IsLineTool(tool))
                {
                    _activeLineTool = tool;
                    HideShapeToolFlyout();
                }
                else HideToolFlyouts();
                RefreshToolButtons();
            }
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
        _stage.MouseLeave += (_, _) =>
        {
            _stage.ClearHoveredLineElement();
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            if (!_stage.Capture) ApplyToolCursor();
        };
        _stage.MouseUp += StageMouseUp;
        _stage.MouseCaptureChanged += StageMouseCaptureChanged;
        _stage.DragEnter += StageDragEnter;
        _stage.DragOver += StageDragOver;
        _stage.DragLeave += StageDragLeave;
        _stage.DragDrop += StageDragDrop;
        Deactivate += (_, _) =>
        {
            HideBrushColorPalette();
            FinishLostPointerCapture();
        };
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
            SyncTimelineFrameRange();
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
        _libraryVaultPanel.BindProject(_project, () => _frame);
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

    private void RestoreRestartState(EditorRestartState state)
    {
        AppLog.Info("Restoring editor restart session");
        _project = state.Project;
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = Math.Clamp(state.ActiveSceneIndex, 0, Math.Max(0, _scenes.Count - 1));
        _activeDrawingObjectIndex = Math.Clamp(state.ActiveDrawingObjectIndex, 0, Math.Max(0, _drawingObjects.Count - 1));
        _frame = Math.Max(0, state.Frame);
        _scene = _drawingObjects[_activeDrawingObjectIndex].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        _workspaceTabs.WorldGridOpacity = (int)Math.Clamp(MathF.Round(state.StageView.WorldGridOpacity * 100), 0, 100);
        if (_workspaceTabs.SelectedView == state.Workspace) ShowWorkspace(state.Workspace);
        else _workspaceTabs.SelectedView = state.Workspace;
        var selected = state.SelectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .ToArray();
        if (selected.Length > 0) SetSelection(selected);
        else ClearSelection();
    }

    private void RestoreRestartPresentation(EditorRestartState state)
    {
        if (state.WindowState == FormWindowState.Maximized)
        {
            ApplyMaximizedBounds();
            WindowState = FormWindowState.Maximized;
        }

        UpdateWindowChromeState();
        _stage.RestoreViewState(state.StageView);
        UpdateStatusBar();
        AppLog.Info("Editor restart completed with the current project restored");
    }

    private void RequestEditorRestart()
    {
        if (_restartRequested) return;
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        HideToolFlyouts();
        var state = CaptureEditorRestartState();

        if (IsModuleHotReloadEnabled()
            && EditorRestartStore.TrySave(state)
            && LauncherShutdownSignal.RequestEditorRestart())
        {
            _restartRequested = true;
            AppLog.Info("Editor process restart requested; preserving the current project for the new process");
            ProcessRestartRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var handler = RestartRequested;
        if (handler is null) return;
        _restartRequested = true;
        AppLog.Info("Editor window restart requested; preserving the current project in memory");
        handler(this, new EditorRestartRequestedEventArgs(state));
    }

    private EditorRestartState CaptureEditorRestartState() => new()
    {
        Project = _project,
        ActiveSceneIndex = _activeSceneIndex,
        ActiveDrawingObjectIndex = _activeDrawingObjectIndex,
        Workspace = _workspaceTabs.SelectedView,
        Frame = _frame,
        SelectedObjects = _selectedObjects.ToArray(),
        StageView = _stage.CaptureViewState(),
        WindowBounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds,
        WindowState = WindowState
    };

    private static string RestartEditorToolTip() => IsModuleHotReloadEnabled()
        ? "Restart the editor process, apply pending code changes, and preserve the current project"
        : "Restart editor and preserve the current project";

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
        _marqueeMaterializationSession = null;
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
        if (frameChanged)
        {
            HideBrushColorPalette();
            CancelPenCurve();
            FinishPointerInteractionForFrameChange();
        }
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
        var interactiveControlFocused = ContainsFocusedInteractiveControl(this);
        var commandButtonFocused = ContainsFocusedButton(this);
        var canvasShortcutsEnabled = _materialEditSession is null
            && !focusedEditor
            && (!interactiveControlFocused || commandButtonFocused);
        var stageDeleteEnabled = _materialEditSession is null && !ContainsFocusedTextEditor(this);
        if (canvasShortcutsEnabled && IsTimelineEditShortcut(keyData) && HandleTimelineShortcut(keyData)) return true;
        if (canvasShortcutsEnabled)
        {
            if (IsScene3DView() && HandleBlender3DShortcut(keyData)) return true;
            if (keyData == Keys.Escape && _freehandDrawing)
            {
                CancelFreehandStroke();
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && _gradientEditSnapshot is not null)
            {
                CancelGradientPointer(restore: true);
                FinishPointerInteraction();
                return true;
            }
            if (keyData == Keys.Escape && CancelPenCurve()) return true;
            if (!(commandButtonFocused && keyData == Keys.Enter) && HandleTimelineShortcut(keyData)) return true;
            if (TryActivateNumberedToolShortcut(keyData)) return true;
            if (keyData == Keys.OemOpenBrackets && AdjustFreehandWidth(increase: false)) return true;
            if (keyData == Keys.Oem6 && AdjustFreehandWidth(increase: true)) return true;
            if (!commandButtonFocused && keyData == Keys.Tab && CycleActiveToolGroup(reverse: false)) return true;
            if (!commandButtonFocused && keyData == (Keys.Shift | Keys.Tab) && CycleActiveToolGroup(reverse: true)) return true;
            if (keyData == (Keys.Control | Keys.Z) && UndoLastEdit()) return true;
            if (keyData == (Keys.Control | Keys.C) && CopySelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.V) && PasteCopiedObjects()) return true;
        }

        if (keyData == Keys.Delete && stageDeleteEnabled && DeleteSelectedObject()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if (!ShowBrushColorPalette()) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if ((ModifierKeys & Keys.Shift) != Keys.None) return;
        HideBrushColorPalette();
        e.Handled = true;
    }

    private bool ShowBrushColorPalette()
    {
        if (_brushColorPaletteActive) return true;
        if (!IsBrushTool(_tool)
            || _freehandDrawing
            || _lastMouse is not null
            || IsSceneCompositionContext()
            || IsNestedInstanceTimelineTrackActive()
            || ContainsFocusedEditor(this))
        {
            return false;
        }

        var center = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(center)) return false;
        var colors = _materialEditor.RecentColors.Take(8).ToArray();
        if (colors.Length == 0) colors = [ActiveColor()];

        _brushColorPaletteActive = true;
        _brushColorPaletteColors = colors;
        _brushColorPaletteCenter = center;
        _brushColorPaletteHoverIndex = -1;
        _stage.ClearBrushTipCursor();
        _stage.SetBrushColorPalette(center, colors);
        return true;
    }

    private void UpdateBrushColorPaletteHover(Point screen)
    {
        if (!_brushColorPaletteActive) return;
        var hoveredIndex = _stage.HitTestBrushColorPalette(screen);
        if (hoveredIndex == _brushColorPaletteHoverIndex) return;

        _brushColorPaletteHoverIndex = hoveredIndex;
        _stage.SetBrushColorPalette(_brushColorPaletteCenter, _brushColorPaletteColors, hoveredIndex);
        if ((uint)hoveredIndex >= (uint)_brushColorPaletteColors.Length) return;
        _materialEditor.SetFillColorForBrush(_brushColorPaletteColors[hoveredIndex]);
    }

    private void HideBrushColorPalette()
    {
        if (!_brushColorPaletteActive && !_stage.BrushColorPaletteVisible) return;
        _brushColorPaletteActive = false;
        _brushColorPaletteColors = [];
        _brushColorPaletteHoverIndex = -1;
        _stage.ClearBrushColorPalette();
        if (!IsDisposed) RefreshInteractionCursorAtPointer();
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

    private bool ExecuteTimelineEdit(TimelineEditKind edit, IReadOnlyList<TimelineFrameCell>? selectedCells = null)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var timeline = context.Timeline;
        var cells = (selectedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (cells.Length == 0 && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [new TimelineFrameCell(_timeline.ActiveTrackId, _frame)];
        }
        if (cells.Length == 0) return false;

        var advanceSingleKeyframeInsertion = cells.Length == 1
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe;
        if (advanceSingleKeyframeInsertion)
        {
            var advancedCell = AdvanceTimelineKeyframeShortcutCell(cells[0]);
            if (advancedCell == cells[0]) return false;
            cells = [advancedCell];
        }

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var vectorSnapshot = drawingScene?.CreateSnapshot();
        var sceneDefinition = context as SceneDefinition;
        var sceneSnapshot = sceneDefinition is null ? null : timeline.CreateSnapshot();
        var changed = false;

        if (advanceSingleKeyframeInsertion)
        {
            EnsureTimelineFrameExists(timeline, cells[0].Frame);
            ApplyBoundTimelineDuration(previousLastFrame);
            _timeline.SelectSingleFrame(cells[0].TrackId, cells[0].Frame);
        }

        using (timeline.BeginBatchUpdate())
        {
            foreach (var group in cells.GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
            {
                var track = timeline.FindTrack(group.Key);
                if (track is null) continue;
                var layer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                var ranges = TimelineFrameRanges(group.Select(cell => cell.Frame));
                if (edit is TimelineEditKind.InsertFrame or TimelineEditKind.RemoveFrame)
                {
                    foreach (var range in ranges.Reverse())
                    {
                        changed |= ApplyTimelineRangeEdit(edit, timeline, track, drawingScene, layer, range.Frame, range.Count);
                    }
                    continue;
                }

                foreach (var frame in group.Select(cell => cell.Frame).Distinct().OrderBy(frame => frame))
                {
                    changed |= ApplyTimelineCellEdit(edit, timeline, track, drawingScene, layer, frame);
                }
            }
        }

        if (!changed) return advanceSingleKeyframeInsertion;
        if (vectorSnapshot is not null) PushUndoSnapshot(vectorSnapshot);
        if (sceneDefinition is not null && sceneSnapshot is not null) PushSceneTimelineUndo(sceneDefinition, sceneSnapshot);

        if (!advanceSingleKeyframeInsertion
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe)
        {
            EnsureTimelineFrameExists(timeline, cells.Max(cell => cell.Frame) + 1);
        }

        CompleteTimelineMutation(context, drawingScene, previousLastFrame);
        return true;
    }

    internal static TimelineFrameCell AdvanceTimelineKeyframeShortcutCell(TimelineFrameCell cell)
    {
        return cell.Frame >= int.MaxValue - 1
            ? cell
            : cell with { Frame = cell.Frame + 1 };
    }

    private static bool ApplyTimelineRangeEdit(
        TimelineEditKind edit,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame,
        int count)
    {
        if (drawingScene is not null && layer >= 0)
        {
            return edit == TimelineEditKind.InsertFrame
                ? drawingScene.InsertTimelineFrame(layer, frame, count)
                : drawingScene.RemoveTimelineFrame(layer, frame, count);
        }

        return edit == TimelineEditKind.InsertFrame
            ? timeline.InsertFrame(track.Id, frame, count)
            : timeline.RemoveFrame(track.Id, frame, count);
    }

    private static bool ApplyTimelineCellEdit(
        TimelineEditKind edit,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame)
    {
        if (drawingScene is not null && layer >= 0)
        {
            return edit switch
            {
                TimelineEditKind.InsertKeyframe => drawingScene.InsertTimelineKeyframe(layer, frame),
                TimelineEditKind.InsertBlankKeyframe => drawingScene.InsertTimelineBlankKeyframe(layer, frame),
                TimelineEditKind.ClearKeyframe => drawingScene.ClearTimelineKeyframe(layer, frame),
                _ => false
            };
        }

        return edit switch
        {
            TimelineEditKind.InsertKeyframe => timeline.InsertKeyframe(track.Id, frame),
            TimelineEditKind.InsertBlankKeyframe => timeline.InsertBlankKeyframe(track.Id, frame),
            TimelineEditKind.ClearKeyframe => timeline.ClearKeyframe(track.Id, frame),
            _ => false
        };
    }

    private static IReadOnlyList<(int Frame, int Count)> TimelineFrameRanges(IEnumerable<int> frames)
    {
        var ordered = frames.Where(frame => frame >= 0).Distinct().OrderBy(frame => frame).ToArray();
        if (ordered.Length == 0) return [];

        var ranges = new List<(int Frame, int Count)>();
        var start = ordered[0];
        var previous = start;
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index] == previous + 1)
            {
                previous = ordered[index];
                continue;
            }

            ranges.Add((start, previous - start + 1));
            start = previous = ordered[index];
        }

        ranges.Add((start, previous - start + 1));
        return ranges;
    }

    private void CompleteTimelineMutation(ITimelineContext context, VectorScene? drawingScene, int previousLastFrame)
    {
        StopPlayback();
        ClearSelection();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousLastFrame);
        if (drawingScene is not null)
        {
            _hierarchyPanel.RefreshScene();
            RebuildDrawingObjectUnderlay();
            _stage.Invalidate();
        }
        else
        {
            RebuildSceneComposition();
        }

        UpdateInspector();
    }

    private void HandleTimelineCommand(TimelineCommand command, IReadOnlyList<TimelineFrameCell> cells)
    {
        switch (command)
        {
            case TimelineCommand.CopyFrames:
                CopyTimelineFrames(cells);
                break;
            case TimelineCommand.PasteFrames:
                PasteTimelineFrames(cells);
                break;
            case TimelineCommand.InsertFrames:
                ExecuteTimelineEdit(TimelineEditKind.InsertFrame, cells);
                break;
            case TimelineCommand.DeleteFrames:
                ExecuteTimelineEdit(TimelineEditKind.RemoveFrame, cells);
                break;
            case TimelineCommand.InsertKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.InsertKeyframe, cells);
                break;
            case TimelineCommand.InsertBlankKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.InsertBlankKeyframe, cells);
                break;
            case TimelineCommand.ClearKeyframes:
                ExecuteTimelineEdit(TimelineEditKind.ClearKeyframe, cells);
                break;
        }
    }

    private bool CopyTimelineFrames(IReadOnlyList<TimelineFrameCell>? requestedCells = null)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var timeline = context.Timeline;
        var cells = (requestedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (cells.Length == 0 && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [new TimelineFrameCell(_timeline.ActiveTrackId, _frame)];
        }
        if (cells.Length == 0) return false;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        VectorScene? drawingSource = null;
        if (drawingScene is not null)
        {
            drawingSource = new VectorScene();
            drawingSource.RestoreSnapshot(drawingScene.CreateSnapshot());
        }

        var anchorTrack = cells.Min(cell => TrackIndex(timeline, cell.TrackId));
        var anchorFrame = cells.Min(cell => cell.Frame);
        _timelineClipboard = new TimelineFrameClipboard
        {
            DrawingSource = drawingSource,
            Cells = cells.Select(cell =>
            {
                var track = timeline.FindTrack(cell.TrackId)!;
                var exposure = track.EvaluateExposure(Math.Min(cell.Frame, track.Duration - 1));
                var sourceLayer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                return new TimelineClipboardCell(
                    TrackIndex(timeline, cell.TrackId) - anchorTrack,
                    cell.Frame - anchorFrame,
                    sourceLayer,
                    cell.Frame,
                    exposure.SourceKind ?? TimelineKeyframeKind.Blank);
            }).ToArray()
        };
        return true;
    }

    private bool PasteTimelineFrames(IReadOnlyList<TimelineFrameCell>? requestedCells = null)
    {
        var clipboard = _timelineClipboard;
        if (clipboard is null || clipboard.Cells.Length == 0) return false;

        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var timeline = context.Timeline;
        var destinationCells = (requestedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .ToArray();
        var anchorTrack = destinationCells.Length > 0
            ? destinationCells.Min(cell => TrackIndex(timeline, cell.TrackId))
            : TrackIndex(timeline, _timeline.ActiveTrackId);
        var anchorFrame = destinationCells.Length > 0 ? destinationCells.Min(cell => cell.Frame) : _frame;
        if (anchorTrack < 0) return false;

        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var vectorSnapshot = drawingScene?.CreateSnapshot();
        var sceneDefinition = context as SceneDefinition;
        var sceneSnapshot = sceneDefinition is null ? null : timeline.CreateSnapshot();
        var changed = false;

        using (timeline.BeginBatchUpdate())
        {
            foreach (var source in clipboard.Cells)
            {
                var destinationTrackIndex = anchorTrack + source.TrackOffset;
                var destinationFrame = Math.Max(0, anchorFrame + source.FrameOffset);
                if (destinationTrackIndex < 0 || destinationTrackIndex >= timeline.Tracks.Count) continue;
                var track = timeline.Tracks[destinationTrackIndex];
                var destinationLayer = drawingScene is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
                if (drawingScene is not null
                    && destinationLayer >= 0
                    && source.SourceLayer >= 0
                    && clipboard.DrawingSource is not null)
                {
                    changed |= drawingScene.CopyTimelineFrameFrom(
                        clipboard.DrawingSource,
                        source.SourceLayer,
                        source.SourceFrame,
                        destinationLayer,
                        destinationFrame);
                    continue;
                }

                if (destinationFrame >= track.Duration) timeline.SetTrackDuration(track.Id, destinationFrame + 1);
                changed |= source.Kind == TimelineKeyframeKind.Populated
                    ? timeline.InsertKeyframe(track.Id, destinationFrame)
                    : timeline.InsertBlankKeyframe(track.Id, destinationFrame);
            }
        }

        if (!changed) return false;
        if (vectorSnapshot is not null) PushUndoSnapshot(vectorSnapshot);
        if (sceneDefinition is not null && sceneSnapshot is not null) PushSceneTimelineUndo(sceneDefinition, sceneSnapshot);
        CompleteTimelineMutation(context, drawingScene, previousLastFrame);
        return true;
    }

    private void MoveTimelineLayer(string trackId, int destinationTrackIndex)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var track = context.Timeline.FindTrack(trackId);
        if (track is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var sourceLayer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            if (sourceLayer < 0) return;
            var destinationLayer = Math.Clamp(destinationTrackIndex, 0, drawingScene.LayerCount - 1);
            var snapshot = drawingScene.CreateSnapshot();
            if (!drawingScene.MoveLayer(sourceLayer, destinationLayer)) return;
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneSourceLayer = sceneDefinition.FindLayer(track.TargetId);
        if (sceneSourceLayer is null) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneDestinationLayer = Math.Clamp(destinationTrackIndex, 0, sceneDefinition.Layers.Count - 1);
        if (!_project.TryMoveSceneLayer(sceneDefinition.Id, sceneSourceLayer.Id, sceneDestinationLayer)) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        UpdateInspector();
    }

    private void ChooseTimelineLayerColor()
    {
        var context = _timeline.Context;
        var track = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        if (track is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var current = Color.Empty;
        var layer = -1;
        if (drawingScene is not null)
        {
            layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            if (layer < 0) return;
            current = drawingScene.GetLayerColor(layer);
        }
        else if (context is SceneDefinition sceneDefinition && sceneDefinition.FindLayer(track.TargetId) is { } sceneLayer)
        {
            current = Color.FromArgb(sceneLayer.ColorArgb);
        }
        else
        {
            return;
        }

        using var picker = new ColorDialog { Color = current, FullOpen = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (drawingScene is not null)
        {
            var snapshot = drawingScene.CreateSnapshot();
            if (!drawingScene.SetLayerColor(layer, picker.Color)) return;
            PushUndoSnapshot(snapshot);
            _timeline.Invalidate();
            return;
        }

        var scene = (SceneDefinition)context;
        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var layerSnapshot = scene.CreateLayerSnapshot();
        if (!_project.TrySetSceneLayerColor(scene.Id, track.TargetId, picker.Color)) return;
        PushSceneTimelineUndo(scene, timelineSnapshot, layerSnapshot);
        _timeline.Invalidate();
    }

    private void ToggleTimelineLayerOnionSkin()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var track = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        if (drawingScene is null || track is null) return;
        var layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        if (layer < 0) return;
        var snapshot = drawingScene.CreateSnapshot();
        if (!drawingScene.ToggleLayerOnionSkin(layer)) return;
        PushUndoSnapshot(snapshot);
        RebuildDrawingObjectUnderlay();
        _timeline.RefreshOnionSkinControls();
    }

    private void ConfigureTimelineOnionSkinRange()
    {
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        using var dialog = new OnionSkinRangeDialog(
            drawingScene.OnionSkinPreviousFrames,
            drawingScene.OnionSkinNextFrames);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var snapshot = drawingScene.CreateSnapshot();
        var track = _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        var layer = track is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        var enableOnionSkin = layer >= 0
            && !drawingScene.LayerOnionSkin[layer]
            && (dialog.PreviousFrames > 0 || dialog.NextFrames > 0);
        var rangeChanged = drawingScene.SetOnionSkinRange(dialog.PreviousFrames, dialog.NextFrames);
        if (!rangeChanged && !enableOnionSkin) return;
        if (enableOnionSkin) drawingScene.ToggleLayerOnionSkin(layer);
        PushUndoSnapshot(snapshot);
        RebuildDrawingObjectUnderlay();
        _timeline.RefreshOnionSkinControls();
    }

    private void SetTimelineOnionSkinRange(int previousFrames, int nextFrames)
    {
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        var track = _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        var layer = track is null ? -1 : Array.IndexOf(drawingScene.LayerIds, track.TargetId);
        var enableOnionSkin = layer >= 0
            && !drawingScene.LayerOnionSkin[layer]
            && (previousFrames > 0 || nextFrames > 0);
        var session = _onionSkinRangeEditSession;
        var snapshot = session is null || !ReferenceEquals(session.Scene, drawingScene)
            ? drawingScene.CreateSnapshot()
            : null;
        var rangeChanged = drawingScene.SetOnionSkinRange(previousFrames, nextFrames);
        if (!rangeChanged && !enableOnionSkin) return;
        if (enableOnionSkin) drawingScene.ToggleLayerOnionSkin(layer);
        if (session is not null && ReferenceEquals(session.Scene, drawingScene)) session.Changed = true;
        else if (snapshot is not null) PushUndoSnapshot(snapshot);
        RebuildDrawingObjectUnderlay();
        _timeline.RefreshOnionSkinControls();
    }

    private void BeginTimelineOnionSkinRangeEdit()
    {
        if (_onionSkinRangeEditSession is not null) return;
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        _onionSkinRangeEditSession = new OnionSkinRangeEditSession
        {
            Scene = drawingScene,
            Snapshot = drawingScene.CreateSnapshot()
        };
    }

    private void CompleteTimelineOnionSkinRangeEdit()
    {
        var session = _onionSkinRangeEditSession;
        _onionSkinRangeEditSession = null;
        if (session is not null && session.Changed) PushUndoSnapshot(session.Snapshot);
    }

    private void CancelTimelineOnionSkinRangeEdit()
    {
        var session = _onionSkinRangeEditSession;
        _onionSkinRangeEditSession = null;
        if (session is null || !session.Changed) return;

        session.Scene.RestoreSnapshot(session.Snapshot);
        RebuildDrawingObjectUnderlay();
        _timeline.RefreshOnionSkinControls();
    }

    private static int TrackIndex(AnimationTimeline timeline, string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return -1;
        for (var index = 0; index < timeline.Tracks.Count; index++)
        {
            if (string.Equals(timeline.Tracks[index].Id, trackId, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static void EnsureTimelineFrameExists(AnimationTimeline timeline, int frame)
    {
        if (frame < 0) return;
        using var batchUpdate = timeline.BeginBatchUpdate();
        foreach (var track in timeline.Tracks)
        {
            if (track.Duration <= frame) timeline.SetTrackDuration(track.Id, frame + 1);
        }
    }

    private void AddTimelineLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };

        if (drawingScene is not null)
        {
            var snapshot = drawingScene.CreateSnapshot();
            drawingScene.AddLayer();
            PushUndoSnapshot(snapshot);
            _timeline.RefreshTimeline();
            _timeline.SelectModelActiveTrack();
            if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryAddSceneLayer(sceneDefinition.Id, out _)) return;

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        RebuildSceneComposition();
        UpdateInspector();
        _stage.Invalidate();
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

    private bool TryActivateNumberedToolShortcut(Keys keyData)
    {
        if (keyData is Keys.D9 or Keys.NumPad9)
        {
            ToggleVaultDrawer();
            return true;
        }

        var tool = keyData switch
        {
            Keys.D1 or Keys.NumPad1 => ToolMode.Select,
            Keys.D2 or Keys.NumPad2 => ToolMode.Transform,
            Keys.D3 or Keys.NumPad3 => _activeShapeTool,
            Keys.D4 or Keys.NumPad4 => _activeLineTool,
            Keys.D5 or Keys.NumPad5 => _activeBrushTool,
            Keys.D6 or Keys.NumPad6 => ToolMode.Fill,
            Keys.D7 or Keys.NumPad7 => ToolMode.InkBottle,
            Keys.D8 or Keys.NumPad8 => ToolMode.Gradient,
            Keys.D9 or Keys.NumPad9 => ToolMode.Eraser,
            _ => (ToolMode?)null
        };
        return tool is { } selected && ActivateDrawingShortcut(selected);
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

    private bool CycleActiveToolGroup(bool reverse)
    {
        if (IsSceneCompositionContext()) return false;

        if (IsLineTool(_tool))
        {
            var index = Array.IndexOf(_lineTools, _activeLineTool);
            if (index < 0) index = 0;
            var next = reverse
                ? (index - 1 + _lineTools.Length) % _lineTools.Length
                : (index + 1) % _lineTools.Length;
            ActivateTool(_lineTools[next]);
            ShowLineToolFlyout();
            ScheduleLineToolFlyoutHideAfter(2000);
            return true;
        }

        if (IsBrushTool(_tool))
        {
            var index = Array.IndexOf(_brushTools, _activeBrushTool);
            if (index < 0) index = 0;
            var next = reverse
                ? (index - 1 + _brushTools.Length) % _brushTools.Length
                : (index + 1) % _brushTools.Length;
            ActivateTool(_brushTools[next]);
            ShowBrushToolFlyout();
            ScheduleBrushToolFlyoutHideAfter(2000);
            return true;
        }

        var shapeIndex = Array.IndexOf(_shapeTools, _activeShapeTool);
        if (shapeIndex < 0) shapeIndex = 0;
        var shapeNext = reverse
            ? (shapeIndex - 1 + _shapeTools.Length) % _shapeTools.Length
            : (shapeIndex + 1) % _shapeTools.Length;
        ActivateTool(_shapeTools[shapeNext]);
        ShowShapeToolFlyout();
        ScheduleShapeToolFlyoutHideAfter(2000);
        return true;
    }

    private void SyncTimelineFrameRange()
    {
        _syncingFrame = true;
        try
        {
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
            SyncTimelineFrameRange();
            SetFrame(Math.Clamp(_frame, startFrame, endFrame));
        }
    }

    private void BeginMaterialContinuousEdit()
    {
        if (_materialEditSession is not null || IsSceneCompositionContext()) return;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = new MaterialEditSession
        {
            Scene = _scene,
            Snapshot = _scene.CreateSnapshot(),
            SelectedObjects = _selectedObjects.ToArray(),
            SelectedElements = _selectedElements.ToArray(),
            PrimaryElement = _selectedElement
        };
    }

    private void CompleteMaterialContinuousEdit()
    {
        var session = _materialEditSession;
        var mergeFills = _fillMergePendingAfterMaterialEdit;
        var mergeLines = _lineMergePendingAfterMaterialEdit;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = null;
        var hierarchyChanged = session?.HierarchyDirty == true;
        if (mergeFills) hierarchyChanged |= MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        if (mergeLines) hierarchyChanged |= MergeCompatibleLinesAfterDrawingOperation();
        if (!hierarchyChanged) return;
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void CancelMaterialContinuousEdit()
    {
        var session = _materialEditSession;
        _fillMergePendingAfterMaterialEdit = false;
        _lineMergePendingAfterMaterialEdit = false;
        _materialEditSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return;

        if (session.UndoPushed
            && _undoStack.TryPeek(out var undo)
            && ReferenceEquals(undo, session.Snapshot))
        {
            _undoStack.Pop();
        }

        _scene.RestoreSnapshot(session.Snapshot);
        if (session.SelectedElements.Length > 0)
        {
            SetSelection(session.SelectedElements, session.PrimaryElement);
        }
        else if (session.SelectedObjects.Length > 0)
        {
            SetSelection(session.SelectedObjects);
        }
        else
        {
            ClearSelection();
        }

        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void PushMaterialUndoSnapshot(VectorSceneSnapshot fallbackSnapshot)
    {
        var session = _materialEditSession;
        if (session is null || !ReferenceEquals(session.Scene, _scene))
        {
            PushUndoSnapshot(fallbackSnapshot);
            return;
        }

        if (session.UndoPushed) return;
        PushUndoSnapshot(session.Snapshot);
        session.UndoPushed = true;
    }

    private VectorSceneSnapshot CreateMaterialUndoSnapshot()
    {
        var session = _materialEditSession;
        return session is not null && ReferenceEquals(session.Scene, _scene)
            ? session.Snapshot
            : _scene.CreateSnapshot();
    }

    private void RefreshHierarchyAfterMaterialChange(bool hierarchyChanged)
    {
        if (!hierarchyChanged) return;
        var session = _materialEditSession;
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            session.HierarchyDirty = true;
            return;
        }

        _hierarchyPanel.RefreshScene();
    }

    private void CaptureUndoSnapshot()
    {
        PushUndoSnapshot(_scene.CreateSnapshot());
    }

    private void PushUndoSnapshot(VectorSceneSnapshot snapshot)
    {
        _marqueeMaterializationSession = null;
        _undoStack.Push(snapshot);
        var snapshots = _undoStack.ToArray();
        var retainedCount = 0;
        long retainedBytes = 0;
        foreach (var item in snapshots)
        {
            var itemBytes = item.EstimateMemoryBytes();
            if (retainedCount > 0
                && (retainedCount >= MaxUndoSnapshots || retainedBytes + itemBytes > MaxUndoSnapshotBytes))
            {
                break;
            }

            retainedBytes += itemBytes;
            retainedCount++;
        }

        if (retainedCount == snapshots.Length) return;
        _undoStack.Clear();
        for (var index = retainedCount - 1; index >= 0; index--) _undoStack.Push(snapshots[index]);
    }

    private void PushSceneTimelineUndo(
        SceneDefinition scene,
        AnimationTimelineSnapshot snapshot,
        SceneLayerSnapshot? layerSnapshot = null)
    {
        _sceneTimelineUndoStack.Push(new SceneTimelineUndoEntry(scene, snapshot, layerSnapshot));
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
        _marqueeMaterializationSession = null;
        if (IsSceneCompositionContext()
            && _timeline.Context is SceneDefinition activeScene
            && _sceneTimelineUndoStack.TryPeek(out var timelineUndo)
            && ReferenceEquals(timelineUndo.Scene, activeScene))
        {
            var previousLastFrame = Math.Max(0, activeScene.FrameCount - 1);
            _sceneTimelineUndoStack.Pop();
            if (timelineUndo.LayerSnapshot is not null) activeScene.RestoreLayerSnapshot(timelineUndo.LayerSnapshot);
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
        CancelPenCurve();
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
                    pathContours,
                    _scene.GetLineEndpointStyle(index, startEndpoint: true),
                    _scene.GetLineEndpointStyle(index, startEndpoint: false)));
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
                    _scene.SetLineEndpointStyle(index, startEndpoint: true, item.StartEndpointStyle);
                    _scene.SetLineEndpointStyle(index, startEndpoint: false, item.EndEndpointStyle);
                }
            }

            if (index >= 0) pasted.Add(index);
        }

        if (pasted.Count == 0) return false;
        SetSelection(pasted);
        MergeSelectedFillsAfterGeometryEdit();
        MergeCompatibleLinesAfterDrawingOperation();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private void StageMouseDown(object? sender, MouseEventArgs e)
    {
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

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
            BeginSceneCompositionPointer(e);
            return;
        }

        if (IsNestedInstanceTimelineTrackActive() && IsBasicDrawingOnlyTool(_tool)) return;

        if (_tool == ToolMode.Pen)
        {
            if (e.Button == MouseButtons.Left) BeginPenSegment(e.Location);
            else if (e.Button == MouseButtons.Right && !CancelPenCurve()) ShowStageContextMenu(e.Location);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowStageContextMenu(e.Location);
            return;
        }

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        if (_tool == ToolMode.Line) _startWorld = ResolveDrawingLineEndpoint(_startWorld.Value);
        _pointerHitWasAlreadySelected = false;
        _selectionWasEmptyOnPointerDown = _selectedObjects.Count == 0;
        _forceMarqueeOnPointerDown = e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control;
        _additiveSelection = _tool == ToolMode.Select && e.Button == MouseButtons.Left && IsShiftPressed();
        _pendingClickSelection = DrawingElementHit.None;
        if (_tool == ToolMode.Transform)
        {
            BeginTransformPointer(e);
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (_tool == ToolMode.Eraser)
        {
            if (e.Button == MouseButtons.Left && _startWorld is { } eraserStart) BeginFreehandStroke(e.Location, eraserStart);
            else FinishPointerInteraction();
            return;
        }

        if (_tool == ToolMode.Gradient)
        {
            BeginGradientPointer(e);
            return;
        }

        if (_tool == ToolMode.Select)
        {
            var hoveredHandle = _forceMarqueeOnPointerDown || _additiveSelection
                ? EditHandleKind.None
                : _stage.HitTestHoveredLineHandle(e.Location);
            var hoveredLine = _stage.HoveredLineElement;
            if (hoveredHandle != EditHandleKind.None && hoveredLine.IsValid)
            {
                _stage.ClearHoveredLineElement();
                SetSelection(hoveredLine);
                _activeHandle = hoveredHandle;
                _forceMarqueeOnPointerDown = false;
                if (_selectedObject >= 0) CaptureEditStart(_selectedObject);
                UpdateInspector();
                _stage.Invalidate();
                UpdateInteractionCursor(e.Location);
                return;
            }

            _stage.ClearHoveredLineElement();
            if (!_additiveSelection && _selectedObject >= 0)
            {
                _activeHandle = _stage.HitTestHandle(e.Location, _selectedObject);
                if (_activeHandle != EditHandleKind.None)
                {
                    _forceMarqueeOnPointerDown = false;
                    CaptureEditStart(_selectedObject);
                    UpdateInspector();
                    _stage.Invalidate();
                    UpdateInteractionCursor(e.Location);
                    return;
                }
            }

            if (_forceMarqueeOnPointerDown)
            {
                UpdateInteractionCursor(e.Location);
                return;
            }

            if (_startWorld is not { } startWorld) return;
            var hit = _scene.HitTestElement(startWorld, _frame, SelectionToleranceWorld());
            if (hit.IsValid)
            {
                if (_selectionWasEmptyOnPointerDown && e.Button == MouseButtons.Left && !_additiveSelection)
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
                if (_additiveSelection)
                {
                    AddToSelection(hit);
                }
                else if (hitPartWasAlreadySelected)
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

                if (_selectedObject >= 0) CaptureEditStart(_selectedObject);
            }
            else
            {
                if (!_additiveSelection) CancelStageSelection();
                if (e.Button == MouseButtons.Left) BeginMarqueeFromPendingSelection(e.Location);
            }

            UpdateInspector();
            _stage.Invalidate();
            UpdateInteractionCursor(e.Location);
        }
        else if (_tool == ToolMode.Fill)
        {
            if (e.Button != MouseButtons.Left || _startWorld is not { } fillWorld) return;
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
                _scene.DisableLinearGradient(hitObject);
                var animationContours = _scene.GetFillPartContours(materialized, _frame);
                var beforeMergeCount = _scene.ObjectCount;
                var merged = _scene.MergeSameColorFillsAround(hitObject, frame: _frame);
                if (_scene.ObjectCount != beforeMergeCount || merged != hitObject) SetSelection(merged);
                else SetSelection(materialized);
                MergeCompatibleLinesAfterDrawingOperation();
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                ClearFillHoverPreview();
                _stage.StartFillAnimation(animationContours, fillWorld, color);
                _stage.Invalidate();
            }
            else
            {
                var color = ActiveColor();
                var snapshot = _scene.CreateSnapshot();
                if (!_scene.TryCreateFillFromClosedStrokeRegion(fillWorld, _frame, color, out var created, out var animationContours)) return;

                SetSelection(created);
                PushUndoSnapshot(snapshot);
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                ClearFillHoverPreview();
                _stage.StartFillAnimation(animationContours, fillWorld, color);
                _stage.Invalidate();
            }
        }
        else if (_tool == ToolMode.InkBottle)
        {
            ApplyInkBottle(e);
        }
        else if (IsFreehandTool(_tool) && e.Button == MouseButtons.Left && _startWorld is { } freehandStart)
        {
            BeginFreehandStroke(e.Location, freehandStart);
        }
    }

    private void ApplyInkBottle(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world) return;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount) return;
        if (hit.Key.Kind is not DrawingElementKind.Fill and not DrawingElementKind.BoundaryStroke) return;

        var objectIndex = hit.Key.ObjectIndex;
        if (!IsFillShape(_scene.ShapeKind[objectIndex])) return;
        var stroke = ActiveStrokeUnits();
        var strokeColor = ActiveStrokeColor().ToArgb();
        if (Math.Abs(_scene.Stroke[objectIndex] - stroke) < 0.001f
            && _scene.StrokeArgb[objectIndex] == strokeColor)
        {
            return;
        }

        var snapshot = _scene.CreateSnapshot();
        _scene.Stroke[objectIndex] = stroke;
        _scene.StrokeArgb[objectIndex] = strokeColor;
        _scene.RebuildGeometryIndex();
        SetSelection(objectIndex);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void BeginGradientPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world)
        {
            FinishPointerInteraction();
            return;
        }

        var overlayHit = _stage.HitTestGradientOverlay(e.Location);
        if (overlayHit.Kind != GradientHandleKind.None
            && _selectedObject >= 0
            && _scene.HasGradient(_selectedObject))
        {
            _gradientEditSnapshot = _scene.CreateSnapshot();
            _gradientHandle = overlayHit.Kind;
            _gradientStopIndex = overlayHit.StopIndex;
            UpdateInteractionCursor(e.Location);
            return;
        }

        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount || !SupportsGradient(_scene.ShapeKind[hit.Key.ObjectIndex]))
        {
            FinishPointerInteraction();
            return;
        }

        var objectIndex = hit.Key.ObjectIndex;
        _gradientEditSnapshot = _scene.CreateSnapshot();
        if (!_scene.HasGradient(objectIndex))
        {
            var kind = _materialEditor.GradientKind == GradientKind.Solid
                ? GradientKind.Linear
                : _materialEditor.GradientKind;
            _scene.SetGradientPaint(objectIndex, kind, _materialEditor.GradientStops, world, world);
        }

        SetSelection(objectIndex);
        _gradientHandle = GradientHandleKind.End;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
        UpdateInteractionCursor(e.Location);
    }

    private void UpdateGradientHandle(PointF world)
    {
        if (_gradientHandle == GradientHandleKind.None
            || _selectedObject < 0
            || !_scene.HasGradient(_selectedObject))
        {
            return;
        }

        var snapped = VectorUnits.Quantize(_drawSettings.SnapPoint(world));
        var start = _scene.GetGradientStart(_selectedObject);
        var end = _scene.GetGradientEnd(_selectedObject);
        if (_gradientHandle == GradientHandleKind.Stop)
        {
            var stops = _scene.GetGradientStops(_selectedObject);
            if (_gradientStopIndex <= 0 || _gradientStopIndex >= stops.Length - 1) return;
            var axisX = end.X - start.X;
            var axisY = end.Y - start.Y;
            var axisLengthSquared = axisX * axisX + axisY * axisY;
            if (axisLengthSquared < 0.0001f) return;
            var projected = ((snapped.X - start.X) * axisX + (snapped.Y - start.Y) * axisY) / axisLengthSquared;
            var position = Math.Clamp(projected, stops[_gradientStopIndex - 1].Position + 0.01f, stops[_gradientStopIndex + 1].Position - 0.01f);
            stops[_gradientStopIndex] = new GradientStop(position, stops[_gradientStopIndex].Argb);
            _scene.SetGradientStops(_selectedObject, stops);
        }
        else
        {
            if (_gradientHandle == GradientHandleKind.Start)
            {
                if (_scene.GetGradientKind(_selectedObject) == GradientKind.Radial)
                {
                    end = new PointF(end.X + snapped.X - start.X, end.Y + snapped.Y - start.Y);
                }
                start = snapped;
            }
            else end = snapped;
            _scene.SetLinearGradientEndpoints(_selectedObject, start, end);
        }
        UpdateGradientOverlay();
        _stage.Invalidate();
    }

    private void CompleteGradientPointer()
    {
        if (_gradientEditSnapshot is { } snapshot) PushUndoSnapshot(snapshot);
        _gradientEditSnapshot = null;
        _gradientHandle = GradientHandleKind.None;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void CancelGradientPointer(bool restore)
    {
        if (restore && _gradientEditSnapshot is { } snapshot)
        {
            _scene.RestoreSnapshot(snapshot);
            SyncSelectionToStage();
            UpdateInspector();
        }

        _gradientEditSnapshot = null;
        _gradientHandle = GradientHandleKind.None;
        _gradientStopIndex = -1;
        UpdateGradientOverlay();
    }

    private void UpdateGradientOverlay()
    {
        if (_tool != ToolMode.Gradient
            || IsSceneCompositionContext()
            || IsScene3DView()
            || _selectedObject < 0
            || !_scene.HasGradient(_selectedObject))
        {
            _stage.ClearGradientOverlay();
            return;
        }

        _stage.SetGradientOverlay(
            _scene.GetGradientStart(_selectedObject),
            _scene.GetGradientEnd(_selectedObject),
            _scene.GetGradientKind(_selectedObject),
            _scene.GetGradientStops(_selectedObject));
    }

    private void StageMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_tool == ToolMode.Pen)
        {
            CancelPenCurve();
            return;
        }

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
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

        if (_lastMouse is null)
        {
            if (_tool == ToolMode.Pen && _penStartWorld is not null)
            {
                UpdatePenDrawingPreview(_stage.ScreenToWorld(e.Location));
                return;
            }

            UpdateHoveredLineControls(e.Location);
            if (TryBegin3DViewDragFromMove(e)) return;
            UpdateInteractionCursor(e.Location);
            return;
        }

        var dx = e.X - _lastMouse.Value.X;
        var dy = e.Y - _lastMouse.Value.Y;
        _lastMouse = e.Location;
        if (IsBrushTool(_tool) || _tool == ToolMode.Eraser)
        {
            _stage.SetBrushTipCursor(e.Location, _brushShape, ActiveStrokeUnits(), _tool == ToolMode.Eraser);
        }
        else if (_tool == ToolMode.Fill)
        {
            _stage.SetFillToolCursor(e.Location, ActiveColor());
            UpdateFillHoverPreview(e.Location);
        }

        if (_gradientHandle != GradientHandleKind.None && e.Button == MouseButtons.Left)
        {
            UpdateGradientHandle(_stage.ScreenToWorld(e.Location));
            UpdateInteractionCursor(e.Location);
            return;
        }

        if (IsSceneCompositionContext())
        {
            HandleSceneCompositionPointerMove(e, dx, dy);
            return;
        }

        if (_tool == ToolMode.Pen && _penSegmentDragging && e.Button == MouseButtons.Left)
        {
            UpdatePenControl(e.Location);
            return;
        }

        if (_tool == ToolMode.Transform
            && _activeTransformHandle != TransformHandleKind.None
            && e.Button == MouseButtons.Left)
        {
            if (!PointerDragExceeded(e.Location)) return;
            ApplyTransformFromPointer(_stage.ScreenToWorld(e.Location));
            return;
        }

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

    private void UpdateHoveredLineControls(Point screen)
    {
        if (_tool != ToolMode.Select || IsSceneCompositionContext() || IsScene3DView())
        {
            _stage.ClearHoveredLineElement();
            return;
        }

        if (TryFindSelectedLineControlHit(screen, out var selectedLine))
        {
            _stage.SetHoveredLineElement(selectedLine);
            return;
        }

        if (_stage.HitTestHoveredLineHandle(screen) != EditHandleKind.None) return;

        var hit = _scene.HitTestElement(_stage.ScreenToWorld(screen), _frame, SelectionToleranceWorld());
        if (hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line)
        {
            _stage.SetHoveredLineElement(hit);
            return;
        }

        _stage.ClearHoveredLineElement();
    }

    private void BeginSceneCompositionPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || IsScene3DView()) return;
        if (_tool is not (ToolMode.Select or ToolMode.Transform)) return;

        _stage.Capture = true;
        _lastMouse = e.Location;
        _startScreen = e.Location;
        _startWorld = _stage.ScreenToWorld(e.Location);
        _activeTransformHandle = TransformHandleKind.None;
        _sceneInstanceMoveActive = false;

        if (_tool == ToolMode.Transform)
        {
            BeginSceneInstanceTransformPointer(e);
        }
        else
        {
            BeginSceneInstanceMovePointer(_startWorld.Value);
        }

        UpdateInteractionCursor(e.Location);
    }

    private void BeginSceneInstanceMovePointer(PointF world)
    {
        if (TryResolveSceneInstanceAt(world, out var instance))
        {
            SetSceneInstanceSelection(instance);
            _sceneInstanceMoveActive = true;
            _transformLastPointer = world;
        }
        else if (IsPointerInsideSelectedSceneInstance(world))
        {
            _sceneInstanceMoveActive = true;
            _transformLastPointer = world;
        }
        else
        {
            ClearSelection();
        }

        UpdateInspector();
    }

    private void BeginSceneInstanceTransformPointer(MouseEventArgs e)
    {
        var handle = _stage.HitTestTransformHandle(e.Location);
        if (handle == TransformHandleKind.None)
        {
            if (TryResolveSceneInstanceAt(_stage.ScreenToWorld(e.Location), out var instance))
            {
                SetSceneInstanceSelection(instance);
            }
            else
            {
                ClearSelection();
            }

            UpdateInspector();
            return;
        }

        if (SelectedSceneInstance() is null) return;
        _activeTransformHandle = handle;
        _transformCurrentBounds = _stage.TransformBounds;
        _transformLastPointer = _stage.ScreenToWorld(e.Location);
        if (handle == TransformHandleKind.Focus) return;
        _transformPivot = TransformPivotFor(handle, _transformCurrentBounds);
        _transformLastAngle = TransformPointerAngle(_transformLastPointer, _transformPivot);
    }

    private void HandleSceneCompositionPointerMove(MouseEventArgs e, int dx, int dy)
    {
        if (TryPromote3DViewDragFromMove(e))
        {
            if (_viewReferencePanning) _stage.PanReferenceCamera(dx, dy);
            else if (_viewReferenceZooming) _stage.DollyReferenceCameraByPixels(dy);
            else if (_viewOrbiting) _stage.RotateReferenceCamera(dx, dy);
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
            _stage.ZoomAt(e.Location, (float)Math.Clamp(Math.Exp(-dy * 0.01), 0.2, 5.0));
            UpdateStatusBar();
            return;
        }

        if (_viewOrbiting)
        {
            _stage.RotateReferenceCamera(dx, dy);
            UpdateStatusBar();
            return;
        }

        if (IsScene3DView()) return;
        var world = _stage.ScreenToWorld(e.Location);
        if (_tool == ToolMode.Transform
            && _activeTransformHandle != TransformHandleKind.None
            && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) ApplySceneInstanceTransform(world);
            return;
        }

        if (_tool == ToolMode.Select && _sceneInstanceMoveActive && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) MoveSelectedSceneInstance(world);
            return;
        }

        UpdateInteractionCursor(e.Location);
    }

    private void MoveSelectedSceneInstance(PointF world)
    {
        var instance = SelectedSceneInstance();
        if (instance is null) return;
        var dx = world.X - _transformLastPointer.X;
        var dy = world.Y - _transformLastPointer.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;

        instance.X = VectorUnits.Quantize(instance.X + dx);
        instance.Y = VectorUnits.Quantize(instance.Y + dy);
        TranslateCustomTransformFocus(dx, dy);
        _transformLastPointer = world;
        PreviewTranslateSelectedSceneInstance(dx, dy);
    }

    private void ApplySceneInstanceTransform(PointF world)
    {
        var instance = SelectedSceneInstance();
        if (instance is null || _activeTransformHandle == TransformHandleKind.None) return;

        if (_activeTransformHandle == TransformHandleKind.Move)
        {
            MoveSelectedSceneInstance(world);
            return;
        }

        if (_activeTransformHandle == TransformHandleKind.Focus)
        {
            _transformFocus = VectorUnits.Quantize(world);
            UpdateTransformOverlay();
            _stage.Invalidate();
            return;
        }

        var changed = false;
        Func<PointF, PointF>? previewTransform = null;
        if (IsRotationHandle(_activeTransformHandle))
        {
            var angle = TransformPointerAngle(world, _transformPivot);
            var delta = NormalizeAngle(angle - _transformLastAngle);
            if (Math.Abs(delta) <= 0.0001f) return;
            var cos = MathF.Cos(delta);
            var sin = MathF.Sin(delta);
            var pivot = _transformPivot;
            var origin = RotatePointAround(new PointF(instance.X, instance.Y), _transformPivot, delta);
            instance.X = VectorUnits.Quantize(origin.X);
            instance.Y = VectorUnits.Quantize(origin.Y);
            instance.RotationZ = NormalizeDegrees(instance.RotationZ + delta * 57.29578f);
            _transformLastAngle = angle;
            previewTransform = point => new PointF(
                pivot.X + (point.X - pivot.X) * cos - (point.Y - pivot.Y) * sin,
                pivot.Y + (point.X - pivot.X) * sin + (point.Y - pivot.Y) * cos);
            changed = true;
        }
        else if (IsSkewHandle(_activeTransformHandle))
        {
            var factor = SkewFactor(_activeTransformHandle, _transformCurrentBounds, _transformLastPointer, world);
            if (Math.Abs(factor) <= 0.0001f) return;
            var degrees = MathF.Atan(factor) * 57.29578f;
            if (IsHorizontalSkewHandle(_activeTransformHandle))
            {
                instance.SkewX = Math.Clamp(instance.SkewX + degrees, -80, 80);
            }
            else
            {
                instance.SkewY = Math.Clamp(instance.SkewY + degrees, -80, 80);
            }

            changed = true;
        }
        else
        {
            var nextBounds = ResizedTransformBounds(_transformCurrentBounds, _transformPivot, _activeTransformHandle, world);
            if (nextBounds.Width <= 0 || nextBounds.Height <= 0) return;
            var scaleX = nextBounds.Width / Math.Max(0.001f, _transformCurrentBounds.Width);
            var scaleY = nextBounds.Height / Math.Max(0.001f, _transformCurrentBounds.Height);
            if (Math.Abs(scaleX - 1) <= 0.0001f && Math.Abs(scaleY - 1) <= 0.0001f) return;
            var pivot = _transformPivot;
            var canPreviewScale = Math.Abs(instance.RotationZ) <= 0.001f
                && Math.Abs(instance.SkewX) <= 0.001f
                && Math.Abs(instance.SkewY) <= 0.001f;
            instance.ScaleX = Math.Clamp(instance.ScaleX * scaleX, 0.01f, 1000f);
            instance.ScaleY = Math.Clamp(instance.ScaleY * scaleY, 0.01f, 1000f);
            instance.X = VectorUnits.Quantize(pivot.X + (instance.X - pivot.X) * scaleX);
            instance.Y = VectorUnits.Quantize(pivot.Y + (instance.Y - pivot.Y) * scaleY);
            if (canPreviewScale)
            {
                previewTransform = point => new PointF(
                    pivot.X + (point.X - pivot.X) * scaleX,
                    pivot.Y + (point.Y - pivot.Y) * scaleY);
            }
            changed = true;
        }

        if (!changed) return;
        _transformLastPointer = world;
        if (previewTransform is not null && PreviewTransformSelectedSceneInstance(previewTransform))
        {
            _transformCurrentBounds = _stage.TransformBounds;
            return;
        }

        RebuildSceneComposition();
        _transformCurrentBounds = _stage.TransformBounds;
        UpdateInspector();
    }

    private void PreviewTranslateSelectedSceneInstance(float dx, float dy)
    {
        PreviewTransformSelectedSceneInstance(point => new PointF(point.X + dx, point.Y + dy));
    }

    private bool PreviewTransformSelectedSceneInstance(Func<PointF, PointF> transform)
    {
        if (_selectedSceneInstanceObjectIndices.Length == 0) return false;
        _scene.TransformObjects(
            _selectedSceneInstanceObjectIndices,
            transform,
            rebuildGeometryIndex: false);
        _sceneInstancePreviewDirty = true;
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
        return true;
    }

    private void BeginTransformPointer(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var handle = _stage.HitTestTransformHandle(e.Location);
        if (handle == TransformHandleKind.None)
        {
            var hit = _scene.HitTestElement(_stage.ScreenToWorld(e.Location), _frame, SelectionToleranceWorld());
            if (hit.IsValid) SetSelection(hit.Key.ObjectIndex);
            else CancelStageSelection();
            UpdateInspector();
            return;
        }

        _activeTransformHandle = handle;
        _transformCurrentBounds = _stage.TransformBounds;
        _transformLastPointer = _stage.ScreenToWorld(e.Location);
        if (handle == TransformHandleKind.Focus) return;
        _transformPivot = TransformPivotFor(handle, _transformCurrentBounds);
        _transformLastAngle = TransformPointerAngle(_transformLastPointer, _transformPivot);
    }

    private void ApplyTransformFromPointer(PointF world)
    {
        if (_selectedObjects.Count == 0 || _activeTransformHandle == TransformHandleKind.None) return;

        if (_activeTransformHandle == TransformHandleKind.Focus)
        {
            _transformFocus = VectorUnits.Quantize(world);
            UpdateTransformOverlay();
            _stage.Invalidate();
            return;
        }

        CapturePointerUndoSnapshot();

        if (_activeTransformHandle == TransformHandleKind.Move)
        {
            var dx = world.X - _transformLastPointer.X;
            var dy = world.Y - _transformLastPointer.Y;
            if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
            _scene.TransformObjects(_selectedObjects, point => new PointF(point.X + dx, point.Y + dy));
            TranslateCustomTransformFocus(dx, dy);
        }
        else if (IsRotationHandle(_activeTransformHandle))
        {
            var angle = TransformPointerAngle(world, _transformPivot);
            var delta = NormalizeAngle(angle - _transformLastAngle);
            if (Math.Abs(delta) <= 0.0001f) return;
            var cos = MathF.Cos(delta);
            var sin = MathF.Sin(delta);
            var pivot = _transformPivot;
            _scene.TransformObjects(_selectedObjects, point => new PointF(
                pivot.X + (point.X - pivot.X) * cos - (point.Y - pivot.Y) * sin,
                pivot.Y + (point.X - pivot.X) * sin + (point.Y - pivot.Y) * cos));
            _transformLastAngle = angle;
        }
        else if (IsSkewHandle(_activeTransformHandle))
        {
            var factor = SkewFactor(_activeTransformHandle, _transformCurrentBounds, _transformLastPointer, world);
            if (Math.Abs(factor) <= 0.0001f) return;
            if (IsHorizontalSkewHandle(_activeTransformHandle))
            {
                var anchorY = _activeTransformHandle == TransformHandleKind.SkewTop
                    ? _transformCurrentBounds.Bottom
                    : _transformCurrentBounds.Top;
                _scene.ShearObjects(_selectedObjects, point => new PointF(point.X + (point.Y - anchorY) * factor, point.Y));
            }
            else
            {
                var anchorX = _activeTransformHandle == TransformHandleKind.SkewLeft
                    ? _transformCurrentBounds.Right
                    : _transformCurrentBounds.Left;
                _scene.ShearObjects(_selectedObjects, point => new PointF(point.X, point.Y + (point.X - anchorX) * factor));
            }
        }
        else
        {
            var nextBounds = ResizedTransformBounds(_transformCurrentBounds, _transformPivot, _activeTransformHandle, world);
            if (nextBounds.Width <= 0 || nextBounds.Height <= 0) return;
            var scaleX = nextBounds.Width / Math.Max(0.001f, _transformCurrentBounds.Width);
            var scaleY = nextBounds.Height / Math.Max(0.001f, _transformCurrentBounds.Height);
            if (Math.Abs(scaleX - 1) <= 0.0001f && Math.Abs(scaleY - 1) <= 0.0001f) return;
            var pivot = _transformPivot;
            _scene.TransformObjects(_selectedObjects, point => new PointF(
                pivot.X + (point.X - pivot.X) * scaleX,
                pivot.Y + (point.Y - pivot.Y) * scaleY));
        }

        _transformLastPointer = world;
        _geometryDirty = true;
        UpdateTransformOverlay();
        _transformCurrentBounds = _stage.TransformBounds;
        _stage.Invalidate();
    }

    private PointF TransformPivotFor(TransformHandleKind handle, RectangleF bounds)
    {
        return IsRotationHandle(handle) ? ActiveTransformFocus(bounds) : TransformPivot(handle, bounds);
    }

    private PointF ActiveTransformFocus(RectangleF bounds)
    {
        return _transformFocus ?? new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
    }

    private void TranslateCustomTransformFocus(float dx, float dy)
    {
        if (_transformFocus is not { } focus) return;
        _transformFocus = VectorUnits.Quantize(new PointF(focus.X + dx, focus.Y + dy));
    }

    private static bool IsRotationHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.Rotate
            or TransformHandleKind.RotateTopLeft
            or TransformHandleKind.RotateTopRight
            or TransformHandleKind.RotateBottomRight
            or TransformHandleKind.RotateBottomLeft;
    }

    private static bool IsSkewHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.SkewTop
            or TransformHandleKind.SkewRight
            or TransformHandleKind.SkewBottom
            or TransformHandleKind.SkewLeft;
    }

    private static bool IsHorizontalSkewHandle(TransformHandleKind handle)
    {
        return handle is TransformHandleKind.SkewTop or TransformHandleKind.SkewBottom;
    }

    private static float SkewFactor(TransformHandleKind handle, RectangleF bounds, PointF previous, PointF current)
    {
        return handle switch
        {
            TransformHandleKind.SkewTop => -(current.X - previous.X) / Math.Max(1, bounds.Height),
            TransformHandleKind.SkewBottom => (current.X - previous.X) / Math.Max(1, bounds.Height),
            TransformHandleKind.SkewLeft => -(current.Y - previous.Y) / Math.Max(1, bounds.Width),
            TransformHandleKind.SkewRight => (current.Y - previous.Y) / Math.Max(1, bounds.Width),
            _ => 0
        };
    }

    private static PointF TransformPivot(TransformHandleKind handle, RectangleF bounds)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft => new PointF(bounds.Right, bounds.Bottom),
            TransformHandleKind.Top => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Bottom),
            TransformHandleKind.TopRight => new PointF(bounds.Left, bounds.Bottom),
            TransformHandleKind.Right => new PointF(bounds.Left, bounds.Top + bounds.Height * 0.5f),
            TransformHandleKind.BottomRight => new PointF(bounds.Left, bounds.Top),
            TransformHandleKind.Bottom => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top),
            TransformHandleKind.BottomLeft => new PointF(bounds.Right, bounds.Top),
            TransformHandleKind.Left => new PointF(bounds.Right, bounds.Top + bounds.Height * 0.5f),
            _ => new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f)
        };
    }

    private static RectangleF ResizedTransformBounds(RectangleF current, PointF pivot, TransformHandleKind handle, PointF pointer)
    {
        const float minimum = 1f;
        var left = current.Left;
        var top = current.Top;
        var right = current.Right;
        var bottom = current.Bottom;
        switch (handle)
        {
            case TransformHandleKind.TopLeft:
                left = Math.Min(pointer.X, pivot.X - minimum);
                top = Math.Min(pointer.Y, pivot.Y - minimum);
                right = pivot.X;
                bottom = pivot.Y;
                break;
            case TransformHandleKind.Top:
                top = Math.Min(pointer.Y, pivot.Y - minimum);
                bottom = pivot.Y;
                break;
            case TransformHandleKind.TopRight:
                left = pivot.X;
                top = Math.Min(pointer.Y, pivot.Y - minimum);
                right = Math.Max(pointer.X, pivot.X + minimum);
                bottom = pivot.Y;
                break;
            case TransformHandleKind.Right:
                left = pivot.X;
                right = Math.Max(pointer.X, pivot.X + minimum);
                break;
            case TransformHandleKind.BottomRight:
                left = pivot.X;
                top = pivot.Y;
                right = Math.Max(pointer.X, pivot.X + minimum);
                bottom = Math.Max(pointer.Y, pivot.Y + minimum);
                break;
            case TransformHandleKind.Bottom:
                top = pivot.Y;
                bottom = Math.Max(pointer.Y, pivot.Y + minimum);
                break;
            case TransformHandleKind.BottomLeft:
                left = Math.Min(pointer.X, pivot.X - minimum);
                top = pivot.Y;
                right = pivot.X;
                bottom = Math.Max(pointer.Y, pivot.Y + minimum);
                break;
            case TransformHandleKind.Left:
                left = Math.Min(pointer.X, pivot.X - minimum);
                right = pivot.X;
                break;
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static float TransformPointerAngle(PointF point, PointF pivot) => MathF.Atan2(point.Y - pivot.Y, point.X - pivot.X);

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private static PointF RotatePointAround(PointF point, PointF pivot, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var x = point.X - pivot.X;
        var y = point.Y - pivot.Y;
        return new PointF(pivot.X + x * cos - y * sin, pivot.Y + x * sin + y * cos);
    }

    private static float NormalizeDegrees(float degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees <= -180) degrees += 360;
        return degrees;
    }

    private bool TryFindSelectedLineControlHit(Point screen, out DrawingElementHit line)
    {
        line = DrawingElementHit.None;
        var hovered = DrawingElementHit.None;
        var seen = new HashSet<DrawingElementKey>();

        bool TryCandidate(DrawingElementHit candidate)
        {
            if (!candidate.IsValid
                || candidate.Key.Kind != DrawingElementKind.Stroke
                || (uint)candidate.Key.ObjectIndex >= _scene.ObjectCount
                || _scene.ShapeKind[candidate.Key.ObjectIndex] != ShapeKind.Line
                || !seen.Add(candidate.Key)
                || _stage.HitTestLineElementHandle(screen, candidate) == EditHandleKind.None)
            {
                return false;
            }

            hovered = candidate;
            return true;
        }

        if (_selectedElement.IsValid && TryCandidate(_selectedElement))
        {
            line = hovered;
            return true;
        }
        foreach (var candidate in _selectedElements)
        {
            if (!TryCandidate(candidate)) continue;
            line = hovered;
            return true;
        }

        foreach (var objectIndex in _selectedObjects)
        {
            if ((uint)objectIndex >= _scene.ObjectCount || _scene.ShapeKind[objectIndex] != ShapeKind.Line) continue;
            if (TryCandidate(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0),
                    0,
                    0,
                    1)))
            {
                line = hovered;
                return true;
            }
        }

        return false;
    }

    private void MoveSelectedFromPointer(PointF world)
    {
        if (_selectedObject < 0 || _startWorld is null || _selectedStart is null) return;
        CapturePointerUndoSnapshot();
        if (_activeHandle != EditHandleKind.None)
        {
            if (!EnsureSelectedElementDetachedForMove()) return;
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
                    TranslateGradientFromEditStart(index, dxWorld, dyWorld);
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
                TranslateGradientFromEditStart(_selectedObject, dxWorld, dyWorld);
            }
        }

        _geometryDirty = true;
        UpdateGradientOverlay();
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

        var primaryElement = _selectedElement;
        var endpoint = PointF.Empty;
        var materializeEndpointNeighbors = _activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && primaryElement.IsValid
            && primaryElement.Key.Kind == DrawingElementKind.Stroke
            && (uint)primaryElement.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[primaryElement.Key.ObjectIndex] == ShapeKind.Line
            && TryGetStrokePartEndpoint(
                primaryElement,
                _activeHandle == EditHandleKind.LineStart,
                out endpoint);
        var selectedKeys = _selectedElements.Select(hit => hit.Key).ToHashSet();
        if (materializeEndpointNeighbors)
        {
            foreach (var connected in _scene.GetConnectedStrokeElements(primaryElement, _frame))
            {
                if (connected.Key.Kind != DrawingElementKind.Stroke
                    || (uint)connected.Key.ObjectIndex >= _scene.ObjectCount
                    || _scene.ShapeKind[connected.Key.ObjectIndex] != ShapeKind.Line
                    || !StrokePartTouchesEndpoint(connected, endpoint))
                {
                    continue;
                }

                selectedKeys.Add(connected.Key);
            }
        }

        var materialized = _scene.MaterializeSelectedParts(selectedKeys.ToArray(), _frame);
        if (!materialized.Success || materialized.Parts.Length == 0)
        {
            if (_undoCapturedForPointerEdit && _undoStack.Count > 0) _undoStack.Pop();
            _undoCapturedForPointerEdit = false;
            return false;
        }

        var primaryPart = materialized.Parts.FirstOrDefault(part => part.Source == primaryElement.Key);
        var primaryObject = primaryPart.Source.IsValid
            ? primaryPart.Result.ObjectIndex
            : materialized.Parts[^1].Result.ObjectIndex;
        var selectedObjects = materialized.Parts
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .Where(index => index != primaryObject)
            .Append(primaryObject)
            .ToArray();
        if (materializeEndpointNeighbors) SetSelection(primaryObject);
        else SetSelection(selectedObjects);
        CaptureEditStart(_selectedObject);
        _geometryDirty |= materialized.Changed;
        if (materialized.Changed) _hierarchyPanel.RefreshScene();
        UpdateInspector();
        return true;
    }

    private bool TryGetStrokePartEndpoint(DrawingElementHit hit, bool startEndpoint, out PointF endpoint)
    {
        endpoint = PointF.Empty;
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Stroke) return false;
        var points = _scene.GetStrokePartPoints(hit, _frame);
        if (points.Length == 0) return false;
        endpoint = startEndpoint ? points[0] : points[^1];
        return true;
    }

    private bool StrokePartTouchesEndpoint(DrawingElementHit hit, PointF endpoint)
    {
        if (!TryGetStrokePartEndpoint(hit, startEndpoint: true, out var start)
            || !TryGetStrokePartEndpoint(hit, startEndpoint: false, out var end))
        {
            return false;
        }

        return Distance(start, endpoint) <= EndpointConnectionToleranceUnits
            || Distance(end, endpoint) <= EndpointConnectionToleranceUnits;
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
        _marqueeSelectionBase = _additiveSelection ? _selectedObjects.ToArray() : [];
        _pendingClickSelection = DrawingElementHit.None;
        _selectedStart = null;
        _curveControlStart = null;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedGradientStarts.Clear();
        _lineEndpointEditStarts.Clear();
        _stage.SetMarquee(_marqueeStart.Value, current);
    }

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

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

        if (_tool == ToolMode.Pen)
        {
            if (_penSegmentDragging) CommitPenSegment();
            FinishPenSegmentPointerDrag();
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

        if (_penSegmentDragging)
        {
            _penEndWorld = null;
            _penControlWorld = null;
            FinishPenSegmentPointerDrag();
            _stage.ClearDrawingPreview();
            return;
        }

        if (_gradientEditSnapshot is not null)
        {
            CompleteGradientPointer();
            FinishPointerInteraction();
            return;
        }

        if (_lastMouse is null && !_freehandDrawing && !_marqueeSelecting) return;
        if (_gradientEditSnapshot is not null) CancelGradientPointer(restore: true);
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
        CancelPenCurve();
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _marqueeSelectionBase = [];
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
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        _marqueeSelectionBase = [];
        _undoCapturedForPointerEdit = false;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _activeTransformHandle = TransformHandleKind.None;
        _sceneInstanceMoveActive = false;
        if (_sceneInstancePreviewDirty)
        {
            RebuildSceneComposition();
            UpdateInspector();
        }
        if (_geometryDirty)
        {
            var fillsChanged = MergeSelectedFillsAfterGeometryEdit();
            var linesChanged = MergeCompatibleLinesAfterDrawingOperation();
            if (!fillsChanged && !linesChanged) _scene.RebuildGeometryIndex();
            if (fillsChanged || linesChanged) _hierarchyPanel.RefreshScene();
            _geometryDirty = false;
            UpdateInspector();
        }

        UpdateTransformOverlay();
        _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void StageDragEnter(object? sender, DragEventArgs e)
    {
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject))
        {
            ClearDrawingObjectDragPreview();
            e.Effect = DragDropEffects.None;
            return;
        }

        var canPlace = CanPlaceDroppedDrawingObject(drawingObject);
        e.Effect = canPlace ? DragDropEffects.Copy : DragDropEffects.None;
        if (canPlace) UpdateDrawingObjectDragPreview(drawingObject, DragEventWorldPosition(e));
        else ClearDrawingObjectDragPreview();
    }

    private void StageDragOver(object? sender, DragEventArgs e)
    {
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject) || !CanPlaceDroppedDrawingObject(drawingObject))
        {
            ClearDrawingObjectDragPreview();
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Copy;
        UpdateDrawingObjectDragPreview(drawingObject, DragEventWorldPosition(e));
    }

    private void StageDragLeave(object? sender, EventArgs e) => ClearDrawingObjectDragPreview();

    private bool CanPlaceDroppedDrawingObject(DrawingObjectDefinition drawingObject)
    {
        return IsSceneCompositionContext()
            ? ActiveScene() is not null
            : ActiveDrawingObject() is { } container && _project.CanContainDrawingObject(container.Id, drawingObject.Id);
    }

    private PointF DragEventWorldPosition(DragEventArgs e)
    {
        var client = _stage.PointToClient(new Point(e.X, e.Y));
        return _stage.ScreenToWorld(client);
    }

    private void UpdateDrawingObjectDragPreview(DrawingObjectDefinition drawingObject, PointF world)
    {
        if (IsScene3DView())
        {
            ClearDrawingObjectDragPreview();
            return;
        }

        if (!string.Equals(_dragPreviewDrawingObjectId, drawingObject.Id, StringComparison.Ordinal)
            || _stage.DragPreviewScene is null)
        {
            _dragPreviewDrawingObjectId = drawingObject.Id;
            _dragPreviewPosition = world;
            SceneCompositionBuilder.BuildDrawingObjectPreview(
                _dragPreviewStage,
                drawingObject,
                _drawingObjects,
                world,
                _frame);
            _stage.BindDragPreviewScene(_dragPreviewStage);
            return;
        }

        var dx = world.X - _dragPreviewPosition.X;
        var dy = world.Y - _dragPreviewPosition.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
        _dragPreviewStage.TransformObjects(
            Enumerable.Range(0, _dragPreviewStage.ObjectCount),
            point => new PointF(point.X + dx, point.Y + dy),
            rebuildGeometryIndex: false);
        _dragPreviewPosition = world;
        _stage.Invalidate();
    }

    private void ClearDrawingObjectDragPreview()
    {
        if (string.IsNullOrEmpty(_dragPreviewDrawingObjectId) && _stage.DragPreviewScene is null) return;
        _dragPreviewDrawingObjectId = "";
        _dragPreviewPosition = PointF.Empty;
        _stage.BindDragPreviewScene(null);
    }

    private void StageDragDrop(object? sender, DragEventArgs e)
    {
        if (!TryResolveDroppedDrawingObject(e.Data, out var drawingObject))
        {
            ClearDrawingObjectDragPreview();
            return;
        }
        ClearDrawingObjectDragPreview();
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

    private bool MergeSelectedFillsAfterGeometryEdit(bool connectNearby = false)
    {
        var selected = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (selected.Length == 0) return false;
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
            var merged = _scene.MergeSameColorFillsAround(source, connectNearby, frame: _frame);
            if ((uint)merged >= _scene.ObjectCount) continue;
            retainedKeys.Add(new DrawingStackKey(_scene.ObjectOrder[merged], _scene.ObjectSubOrder[merged]));
            changed |= _scene.ObjectCount != beforeCount || merged != source;
        }

        if (fillKeys.Length == 0 || !changed) return false;
        var mergedSelection = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => retainedKeys.Contains(new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])))
            .ToArray();
        SetSelection(mergedSelection);
        return changed;
    }

    private bool MergeCompatibleLinesAfterDrawingOperation(IReadOnlyCollection<int>? objectScope = null)
    {
        if (IsSceneCompositionContext()) return false;

        var selectedBefore = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        var primaryBefore = (uint)_selectedObject < _scene.ObjectCount ? _selectedObject : -1;
        var result = _scene.MergeCompatibleLineSegments(_frame, objectScope);
        if (!result.Changed) return false;

        int Remap(int index)
        {
            return (uint)index < result.OldToNewObjectIndex.Length
                ? result.OldToNewObjectIndex[index]
                : -1;
        }

        var primaryAfter = Remap(primaryBefore);
        var selectedAfter = selectedBefore
            .Select(Remap)
            .Where(index => index >= 0 && index != primaryAfter)
            .Distinct()
            .ToList();
        if (primaryAfter >= 0) selectedAfter.Add(primaryAfter);
        SetSelection(selectedAfter);
        return true;
    }

    private bool MergeCompatibleLinesAfterMaterialChange()
    {
        if (_materialEditSession is not null)
        {
            _lineMergePendingAfterMaterialEdit = true;
            return false;
        }

        return MergeCompatibleLinesAfterDrawingOperation();
    }

    private bool MergeSelectedFillsAfterMaterialChange()
    {
        if (_materialEditSession is not null)
        {
            _fillMergePendingAfterMaterialEdit = true;
            return false;
        }

        return MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
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
            var materializedLines = _scene.MaterializeMarqueeLineParts(bounds, _frame);
            var materializedFills = _scene.MaterializeMarqueeFillParts(bounds, _frame);
            var objects = _scene.QueryDrawingObjects(bounds, _frame);
            var selectedObjects = objects
                .Where(index => _scene.IsObjectGeometryInsideBounds(index, bounds))
                .Concat(materializedLines.SelectedObjects)
                .Concat(materializedFills.SelectedObjects)
                .Distinct()
                .ToArray();
            if (materializedLines.Changed || materializedFills.Changed)
            {
                PushUndoSnapshot(snapshot);
                _marqueeMaterializationSession = new MarqueeMaterializationSession(_scene, snapshot);
                _hierarchyPanel.RefreshScene();
            }

            if (selectedObjects.Length > 0)
            {
                SetSelection(_additiveSelection
                    ? _marqueeSelectionBase.Concat(selectedObjects)
                    : selectedObjects);
            }
            else if (!TrySetTopologyMarqueeSelection(bounds))
            {
                if (_additiveSelection) SetSelection(_marqueeSelectionBase);
                else ClearSelection();
            }
        }
        else
        {
            if (_additiveSelection) SetSelection(_marqueeSelectionBase);
            else ClearSelection();
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
        if (_additiveSelection)
        {
            SetSelection(_marqueeSelectionBase.Concat(selected.Select(hit => hit.Key.ObjectIndex)));
        }
        else
        {
            SetTopologyMarqueeSelection(selected, primary);
        }
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

        ClearSelection();
        MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);

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
        CancelPenCurve();
        CancelFreehandStroke();
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
        _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private DrawingObjectInstanceDefinition? SelectedSceneInstance()
    {
        if (string.IsNullOrWhiteSpace(_selectedSceneInstanceId)) return null;
        return ActiveScene()?.Instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal));
    }

    private bool TryResolveSceneInstanceAt(PointF world, out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        var scene = ActiveScene();
        if (scene is null) return false;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || !_sceneCompositionResult.TryGetOwner(hit.Key.ObjectIndex, out var owner)) return false;
        var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
        instance = scene.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, rootInstanceId, StringComparison.Ordinal))!;
        return instance is not null;
    }

    private bool TryGetSelectedSceneInstanceBounds(out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        if (SelectedSceneInstance() is null) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        var found = false;
        foreach (var index in _selectedSceneInstanceObjectIndices)
        {
            if ((uint)index >= _scene.ObjectCount) continue;
            var objectBounds = _scene.GetObjectWorldBounds(index);
            bounds = found ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            found = true;
        }

        return found && bounds.Width > 0.001f && bounds.Height > 0.001f;
    }

    private void RefreshSelectedSceneInstanceObjectIndices()
    {
        if (string.IsNullOrWhiteSpace(_selectedSceneInstanceId))
        {
            _selectedSceneInstanceObjectIndices = [];
            return;
        }

        var indices = new List<int>();
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (!_sceneCompositionResult.TryGetOwner(index, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            if (string.Equals(rootInstanceId, _selectedSceneInstanceId, StringComparison.Ordinal)) indices.Add(index);
        }

        _selectedSceneInstanceObjectIndices = indices.ToArray();
    }

    private bool IsPointerInsideSelectedSceneInstance(PointF world)
    {
        if (!TryGetSelectedSceneInstanceBounds(out var bounds)) return false;
        var tolerance = SelectionToleranceWorld();
        bounds.Inflate(tolerance, tolerance);
        return bounds.Contains(world);
    }

    private void SetSceneInstanceSelection(DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(_selectedSceneInstanceId, instance.Id, StringComparison.Ordinal)) _transformFocus = null;
        _selectedSceneInstanceId = instance.Id;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = -1;
        _selectedElement = DrawingElementHit.None;
        SyncSelectionToStage();
        RefreshSelectedSceneInstanceObjectIndices();
        UpdateSceneInstanceSelectionOverlay();
    }

    private void ClearSceneInstanceSelection()
    {
        _selectedSceneInstanceId = "";
        _selectedSceneInstanceObjectIndices = [];
        _sceneInstanceMoveActive = false;
        _transformFocus = null;
        _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
    }

    private void UpdateSceneInstanceSelectionOverlay()
    {
        if (!IsSceneCompositionContext() || !TryGetSelectedSceneInstanceBounds(out var bounds))
        {
            _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
            return;
        }

        _stage.SetDrawingObjectSelectionOverlay(bounds);
    }

    private void SetSelection(int objectIndex)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = objectIndex >= 0 && objectIndex < _scene.ObjectCount ? objectIndex : -1;
        _selectedElement = DrawingElementHit.None;
        if (_selectedObject >= 0) _selectedObjects.Add(_selectedObject);
        SyncSelectionToStage();
    }

    private void SetSelection(DrawingElementHit hit)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
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

    private void AddToSelection(DrawingElementHit hit)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount) return;
        if (_selectedElements.Count > 0)
        {
            var elements = _selectedElements.ToList();
            var existing = elements.FindIndex(selected => selected.Key == hit.Key);
            if (existing >= 0) elements.RemoveAt(existing);
            else elements.Add(hit);

            if (elements.Count == 0)
            {
                ClearSelection();
                return;
            }

            SetSelection(elements, existing >= 0 ? elements[^1] : hit);
            return;
        }

        var objects = _selectedObjects.ToList();
        var objectPosition = objects.IndexOf(hit.Key.ObjectIndex);
        if (objectPosition >= 0) objects.RemoveAt(objectPosition);
        else objects.Add(hit.Key.ObjectIndex);

        if (objects.Count == 0)
        {
            ClearSelection();
            return;
        }

        SetSelection(objects);
    }

    private void SetSelection(IEnumerable<int> objectIndices)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
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
        ClearSceneInstanceSelection();
        _transformFocus = null;
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
        UpdateTransformOverlay();
        UpdateGradientOverlay();
    }

    private void UpdateTransformOverlay()
    {
        var bounds = RectangleF.Empty;
        if (_tool == ToolMode.Transform)
        {
            if (IsSceneCompositionContext())
            {
                TryGetSelectedSceneInstanceBounds(out bounds);
            }
            else foreach (var objectIndex in _selectedObjects)
            {
                if ((uint)objectIndex >= _scene.ObjectCount || !_scene.IsObjectActive(objectIndex, _frame)) continue;
                var objectBounds = _scene.GetObjectWorldBounds(objectIndex);
                bounds = bounds.IsEmpty ? objectBounds : RectangleF.Union(bounds, objectBounds);
            }
        }

        _stage.SetTransformOverlay(
            _tool == ToolMode.Transform,
            bounds,
            bounds.IsEmpty ? null : ActiveTransformFocus(bounds));
    }

    private void CancelStageSelection()
    {
        if (RestoreUneditedMarqueeMaterialization())
        {
            ClearSelection();
            _hierarchyPanel.RefreshScene();
            return;
        }

        var linesChanged = MergeCompatibleLinesAfterDrawingOperation();
        ClearSelection();
        if (linesChanged) _hierarchyPanel.RefreshScene();
    }

    private bool RestoreUneditedMarqueeMaterialization()
    {
        var session = _marqueeMaterializationSession;
        _marqueeMaterializationSession = null;
        if (session is null || !ReferenceEquals(session.Scene, _scene)) return false;

        if (_undoStack.TryPeek(out var undo) && ReferenceEquals(undo, session.Snapshot)) _undoStack.Pop();
        _scene.RestoreSnapshot(session.Snapshot);
        _scene.EditFrame = _frame;
        _geometryDirty = false;
        _stage.ClearHoveredLineElement();
        return true;
    }

    private void ShowStageContextMenu(Point screen)
    {
        _stageContextMenuLocation = screen;
        _stageContextMenu.Show(_stage, screen);
    }

    private bool TryGetStageContextLineObject(Point screen, out int objectIndex)
    {
        objectIndex = -1;
        if (DrawingToolsBlocked()) return false;

        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && _scene.CanConvertLineToFill(hit.Key.ObjectIndex, _frame))
        {
            objectIndex = hit.Key.ObjectIndex;
            return true;
        }

        objectIndex = _selectedObjects.FirstOrDefault(index => _scene.CanConvertLineToFill(index, _frame), -1);
        return objectIndex >= 0;
    }

    private void ConvertStageContextLineToFill()
    {
        if (DrawingToolsBlocked() || _stageContextMenuLineObject < 0) return;

        var selectedLines = _selectedObjects
            .Where(index => _scene.CanConvertLineToFill(index, _frame))
            .Distinct()
            .ToArray();
        var targets = selectedLines.Contains(_stageContextMenuLineObject)
            ? selectedLines
            : new[] { _stageContextMenuLineObject };
        if (targets.Length == 0) return;

        var snapshot = _scene.CreateSnapshot();
        var fillIndices = new List<int>(targets.Length);
        foreach (var target in targets)
        {
            if (_scene.TryConvertLineToFill(target, _frame, out var fillIndex))
            {
                fillIndices.Add(fillIndex);
                continue;
            }

            _scene.RestoreSnapshot(snapshot);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(fillIndices);
        _stage.ClearHoveredLineElement();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void MergeAndSimplifyLineSegments(Point screen)
    {
        if (DrawingToolsBlocked()) return;

        var targets = CollectLineMergeTargets(screen, out var lineWasHit);
        if (targets.Count < 2) return;
        if (lineWasHit) SetSelection(targets);

        var snapshot = _scene.CreateSnapshot();
        if (!MergeCompatibleLinesAfterDrawingOperation(targets)) return;

        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private HashSet<int> CollectLineMergeTargets(Point screen, out bool lineWasHit)
    {
        var world = _stage.ScreenToWorld(screen);
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        var targets = new HashSet<int>();
        lineWasHit = hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line;
        if (lineWasHit)
        {
            foreach (var connected in _scene.GetConnectedStrokeElements(hit, _frame))
            {
                var objectIndex = connected.Key.ObjectIndex;
                if ((uint)objectIndex < _scene.ObjectCount && _scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    targets.Add(objectIndex);
                }
            }
        }
        else
        {
            foreach (var objectIndex in _selectedObjects)
            {
                if ((uint)objectIndex < _scene.ObjectCount && _scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    targets.Add(objectIndex);
                }
            }
        }

        IncludeMaterializedLineSiblings(targets);
        return targets;
    }

    private void IncludeMaterializedLineSiblings(HashSet<int> targets)
    {
        if (targets.Count == 0) return;
        var sourceOrders = targets
            .Where(index => (uint)index < _scene.ObjectCount)
            .Select(index => _scene.ObjectOrder[index])
            .ToHashSet();
        if (sourceOrders.Count == 0) return;

        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || !_scene.IsObjectActive(index, _frame)
                || !sourceOrders.Contains(_scene.ObjectOrder[index]))
            {
                continue;
            }

            targets.Add(index);
        }
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
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool == ToolMode.Line)
        {
            start = ResolveDrawingLineEndpoint(start);
            end = ResolveDrawingLineEnd(start, end);
        }
        else
        {
            start = VectorUnits.Quantize(_drawSettings.SnapPoint(start));
            end = VectorUnits.Quantize(_drawSettings.SnapPoint(end));
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            end = new PointF(start.X + size.Width, start.Y + size.Height);
        }

        _stage.SetDrawingPreview(start, end, shape, ActiveColor(), ActiveStrokeUnits());
    }

    private void BeginPenSegment(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        if (_penStartWorld is null)
        {
            _penStartWorld = ResolveDrawingLineEndpoint(world);
            UpdatePenDrawingPreview(world);
            return;
        }

        var end = ResolveDrawingLineEnd(_penStartWorld.Value, world);
        if (Distance(_penStartWorld.Value, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return;
        _penEndWorld = end;
        _penControlWorld = null;
        _penEndpointScreen = screen;
        _penSegmentDragging = true;
        _stage.Capture = true;
        _lastMouse = screen;
        UpdatePenDrawingPreview(world);
    }

    private void UpdatePenControl(Point screen)
    {
        if (_penEndpointScreen is not { } anchor) return;
        if (Math.Abs(screen.X - anchor.X) + Math.Abs(screen.Y - anchor.Y) <= 3) return;
        _penControlWorld = VectorUnits.Quantize(_drawSettings.SnapPoint(_stage.ScreenToWorld(screen)));
        UpdatePenDrawingPreview(_penControlWorld.Value);
    }

    private void CommitPenSegment()
    {
        if (_penStartWorld is not { } start || _penEndWorld is not { } end) return;
        var control = _penControlWorld ?? new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        CaptureUndoSnapshot();
        var newObject = _scene.AddCurveSegment(
            _scene.ActiveLayer,
            start,
            control,
            end,
            ActiveStrokeUnits(),
            ActiveColor(),
            ActiveStrokeColor(),
            6);
        SetSelection(newObject);
        MergeCompatibleLinesAfterDrawingOperation();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _penStartWorld = end;
        _penEndWorld = null;
        _penControlWorld = null;
        _stage.Invalidate();
    }

    private void UpdatePenDrawingPreview(PointF world)
    {
        if (_penStartWorld is not { } start)
        {
            _stage.ClearDrawingPreview();
            return;
        }

        if (_penEndWorld is not { } end)
        {
            end = ResolveDrawingLineEnd(start, world);
            _stage.SetDrawingPreview(start, end, ShapeKind.Line, ActiveColor(), ActiveStrokeUnits());
            return;
        }

        if (_penControlWorld is { } control)
        {
            _stage.SetCurveDrawingPreview(start, control, end, ActiveColor(), ActiveStrokeUnits());
            return;
        }

        _stage.SetDrawingPreview(start, end, ShapeKind.Line, ActiveColor(), ActiveStrokeUnits());
    }

    private void FinishPenSegmentPointerDrag()
    {
        _penSegmentDragging = false;
        _penEndpointScreen = null;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _stage.Capture = false;
    }

    private bool CancelPenCurve()
    {
        if (_penStartWorld is null && _penEndWorld is null) return false;
        _penStartWorld = null;
        _penEndWorld = null;
        _penControlWorld = null;
        _penEndpointScreen = null;
        _penSegmentDragging = false;
        _stage.ClearDrawingPreview();
        return true;
    }

    private void BeginFreehandStroke(Point screen, PointF world)
    {
        _freehandSamples.Clear();
        _freehandSamples.Add(VectorUnits.Quantize(world));
        _pressureBrushSamples.Clear();
        _freehandLastScreen = screen;
        _freehandDrawing = true;
        _freehandBrushStroke = IsBrushTool(_tool);
        _freehandPressureBrush = _tool == ToolMode.PressureBrush;
        _freehandErasing = _tool == ToolMode.Eraser;
        _freehandStartedTimestamp = Stopwatch.GetTimestamp();
        _freehandLastSampleTimestamp = _freehandStartedTimestamp;
        if (_freehandPressureBrush) _pressureBrushSamples.Add(new PressureBrushSample(_freehandSamples[0], 0, 0));
        _freehandStrokeUnits = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), ActiveStrokeUnits());
        _freehandColor = _freehandErasing
            ? Color.FromArgb(150, 255, 120, 120)
            : _freehandBrushStroke
            ? ActiveColor()
            : ActiveStrokeColor();
        UpdateFreehandPreview();
    }

    private void AppendFreehandSample(Point screen, bool force = false)
    {
        if (!_freehandDrawing || _freehandLastScreen is not { } previousScreen || _freehandSamples.Count == 0) return;
        var sampleTimestamp = Stopwatch.GetTimestamp();
        var dx = screen.X - previousScreen.X;
        var dy = screen.Y - previousScreen.Y;
        var distancePixels = MathF.Sqrt(dx * dx + dy * dy);
        if (!force && distancePixels < FreehandSampleSpacingPixels)
        {
            UpdatePressureBrushHoldTime(sampleTimestamp);
            UpdateFreehandPreview();
            return;
        }

        var start = _freehandSamples[^1];
        var end = _stage.ScreenToWorld(screen);
        var steps = Math.Max(1, (int)MathF.Ceiling(distancePixels / 2f));
        var sampleSeconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_freehandLastSampleTimestamp, sampleTimestamp).TotalSeconds);
        var speed = Math.Clamp((float)(distancePixels / sampleSeconds), 0, 5000);
        var previousHeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, _freehandLastSampleTimestamp).TotalSeconds;
        var currentHeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, sampleTimestamp).TotalSeconds;
        for (var step = 1; step <= steps && _freehandSamples.Count < MaxFreehandSamples; step++)
        {
            var t = step / (float)steps;
            var point = VectorUnits.Quantize(new PointF(
                start.X + (end.X - start.X) * t,
                start.Y + (end.Y - start.Y) * t));
            if (_freehandSamples[^1] == point) continue;
            _freehandSamples.Add(point);
            if (_freehandPressureBrush)
            {
                var heldSeconds = previousHeldSeconds + (currentHeldSeconds - previousHeldSeconds) * t;
                _pressureBrushSamples.Add(new PressureBrushSample(point, speed, heldSeconds));
            }
        }

        _freehandLastScreen = screen;
        _freehandLastSampleTimestamp = sampleTimestamp;
        UpdatePressureBrushHoldTime(sampleTimestamp);
        UpdateFreehandPreview();
    }

    private void UpdatePressureBrushHoldTime(long timestamp)
    {
        if (!_freehandPressureBrush || _pressureBrushSamples.Count == 0) return;
        var index = _pressureBrushSamples.Count - 1;
        var sample = _pressureBrushSamples[index];
        _pressureBrushSamples[index] = sample with
        {
            HeldSeconds = (float)Stopwatch.GetElapsedTime(_freehandStartedTimestamp, timestamp).TotalSeconds
        };
    }

    private void UpdateFreehandPreview()
    {
        if (_freehandPressureBrush)
        {
            var profile = FreehandStrokeProcessor.CreatePressurePreview(
                _pressureBrushSamples,
                _freehandStrokeUnits,
                _drawSettings.PressureBrushSmoothing);
            _stage.SetFreehandPreview(
                profile.Select(sample => sample.Point).ToArray(),
                _freehandColor,
                _freehandStrokeUnits,
                profile.Select(sample => sample.Diameter).ToArray());
            return;
        }

        _stage.SetFreehandPreview(_freehandSamples, _freehandColor, _freehandStrokeUnits);
    }

    private void CommitFreehandStroke()
    {
        if (!_freehandDrawing || _freehandSamples.Count == 0)
        {
            CancelFreehandStroke();
            return;
        }

        if (_freehandPressureBrush)
        {
            UpdatePressureBrushHoldTime(Stopwatch.GetTimestamp());
            var snapshot = _scene.CreateSnapshot();
            var objects = _scene.AddPressureBrushStroke(
                _scene.ActiveLayer,
                _pressureBrushSamples,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, _pressureBrushSamples.Count),
                _drawSettings.PressureBrushSmoothing,
                _stage.ScreenLengthToWorld(0.9f),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous);
            if (objects.Length > 0)
            {
                PushUndoSnapshot(snapshot);
                _scene.MergeSameColorFillsAroundNewObjects(objects, connectNearby: true, frame: _frame);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }

            CancelFreehandStroke();
            _stage.Invalidate();
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

        if (_freehandErasing)
        {
            EraseAlongBrushStroke(points);
            CancelFreehandStroke();
            _stage.Invalidate();
            return;
        }

        CaptureUndoSnapshot();
        if (_freehandBrushStroke)
        {
            var objects = _scene.AddSoftBrushStroke(
                _scene.ActiveLayer,
                points,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, points.Length),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous);
            if (objects.Length > 0)
            {
                _scene.MergeSameColorFillsAroundNewObjects(objects, connectNearby: true, frame: _frame);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
        }
        else
        {
            var newObject = _scene.AddFreehandStroke(
                _scene.ActiveLayer,
                points,
                _freehandStrokeUnits,
                _freehandColor,
                brushStroke: false,
                (uint)Math.Max(3, points.Length));
            if (newObject >= 0)
            {
                SetSelection(newObject);
                MergeCompatibleLinesAfterDrawingOperation();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
        }

        CancelFreehandStroke();
        _stage.Invalidate();
    }

    private void EraseAlongBrushStroke(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return;
        var snapshot = _scene.CreateSnapshot();
        if (!_scene.EraseWithBrushStroke(
                _frame,
                points,
                _freehandStrokeUnits,
                _brushShape,
                _drawSettings.EraseLines,
                _drawSettings.EraseFills,
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous)) return;

        ClearSelection();
        _stage.ClearHoveredLineElement();
        MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
    }

    private void CancelFreehandStroke()
    {
        _freehandDrawing = false;
        _freehandBrushStroke = false;
        _freehandPressureBrush = false;
        _freehandErasing = false;
        _freehandLastScreen = null;
        _freehandStartedTimestamp = 0;
        _freehandLastSampleTimestamp = 0;
        _freehandSamples.Clear();
        _pressureBrushSamples.Clear();
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
            if (_scene.HasGradient(index))
            {
                _selectedGradientStarts[index] = (_scene.GetGradientStart(index), _scene.GetGradientEnd(index));
            }
        }

        if (!_selectedMoveStarts.ContainsKey(objectIndex))
        {
            _selectedMoveStarts[objectIndex] = _selectedStart.Value;
            _selectedCurveStarts[objectIndex] = _curveControlStart.Value;
            if (_scene.HasGradient(objectIndex))
            {
                _selectedGradientStarts[objectIndex] = (_scene.GetGradientStart(objectIndex), _scene.GetGradientEnd(objectIndex));
            }
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
        ResizeGradientFromEditStart(_selectedObject, centerWorld, width, height);
        UpdateGradientOverlay();
    }

    private void TranslateGradientFromEditStart(int objectIndex, float dx, float dy)
    {
        if (!_selectedGradientStarts.TryGetValue(objectIndex, out var gradient)) return;
        _scene.SetLinearGradientEndpoints(
            objectIndex,
            new PointF(gradient.Start.X + dx, gradient.Start.Y + dy),
            new PointF(gradient.End.X + dx, gradient.End.Y + dy));
    }

    private void ResizeGradientFromEditStart(int objectIndex, PointF center, float width, float height)
    {
        if (!_selectedGradientStarts.TryGetValue(objectIndex, out var gradient)
            || _resizeStartCenter is not { } startCenter
            || _resizeStartSize is not { } startSize)
        {
            return;
        }

        var scaleX = width / Math.Max(1f, startSize.Width);
        var scaleY = height / Math.Max(1f, startSize.Height);
        PointF ResizePoint(PointF point)
        {
            var local = new PointF(point.X - startCenter.X, point.Y - startCenter.Y);
            var cos = MathF.Cos(_resizeStartAngle);
            var sin = MathF.Sin(_resizeStartAngle);
            var localX = local.X * cos + local.Y * sin;
            var localY = -local.X * sin + local.Y * cos;
            localX *= scaleX;
            localY *= scaleY;
            return new PointF(
                center.X + localX * cos - localY * sin,
                center.Y + localX * sin + localY * cos);
        }

        _scene.SetLinearGradientEndpoints(objectIndex, ResizePoint(gradient.Start), ResizePoint(gradient.End));
    }

    private void CaptureLineEndpointEditStart(int objectIndex)
    {
        if (_activeHandle is not (EditHandleKind.LineStart or EditHandleKind.LineEnd)) return;
        var startEndpoint = _activeHandle == EditHandleKind.LineStart;
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var anchor)) return;
        var layer = _scene.ObjectLayer[objectIndex];

        for (var i = 0; i < _scene.ObjectCount; i++)
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line
                || _scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame))
            {
                continue;
            }

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
        PointF snapped;
        if (EndpointSnappingRequested()
            && _selectedObject >= 0
            && _selectedObject < _scene.ObjectCount
            && TrySnapToNearbyLineEndpoint(
                world,
                _scene.ObjectLayer[_selectedObject],
                excludeEditedLines: true,
                out var endpoint))
        {
            snapped = endpoint;
        }
        else
        {
            snapped = VectorUnits.Quantize(_drawSettings.SnapPoint(world));
        }

        foreach (var edit in _lineEndpointEditStarts)
        {
            _scene.SetLineEndpoint(edit.ObjectIndex, edit.StartEndpoint, snapped, edit.OppositeEndpoint, edit.Control, edit.KeepStraight);
        }
    }

    private PointF ResolveDrawingLineEndpoint(PointF world)
    {
        if (EndpointSnappingRequested()
            && TrySnapToNearbyLineEndpoint(world, _scene.ActiveLayer, excludeEditedLines: false, out var snapped))
        {
            return snapped;
        }

        var candidate = VectorUnits.Quantize(_drawSettings.SnapPoint(world));
        return candidate;
    }

    private PointF ResolveDrawingLineEnd(PointF start, PointF world)
    {
        if (EndpointSnappingRequested()
            && TrySnapToNearbyLineEndpoint(world, _scene.ActiveLayer, excludeEditedLines: false, out var snapped))
        {
            return snapped;
        }

        var candidate = VectorUnits.Quantize(_drawSettings.SnapPoint(world));
        return ApplyDrawingLineAngleSnap(start, candidate);
    }

    private PointF ApplyDrawingLineAngleSnap(PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= DrawingTopologyRules.UnitIntersectionTolerance) return end;
        var angle = _drawSettings.SnapAngle(MathF.Atan2(dy, dx));
        return VectorUnits.Quantize(new PointF(
            start.X + MathF.Cos(angle) * length,
            start.Y + MathF.Sin(angle) * length));
    }

    private bool EndpointSnappingRequested()
    {
        return IsControlPressed() || (_drawSettings.SnapEnabled && _drawSettings.SnapToObjects);
    }

    private bool TrySnapToNearbyLineEndpoint(
        PointF world,
        int layer,
        bool excludeEditedLines,
        out PointF snapped)
    {
        var tolerance = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(10));
        snapped = world;
        var bestDistance = tolerance;
        for (var i = 0; i < _scene.ObjectCount; i++)
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line
                || _scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame)
                || (excludeEditedLines && _lineEndpointEditStarts.Any(edit => edit.ObjectIndex == i)))
            {
                continue;
            }

            TrySnapToEndpoint(i, startEndpoint: true, world, ref snapped, ref bestDistance);
            TrySnapToEndpoint(i, startEndpoint: false, world, ref snapped, ref bestDistance);
        }

        return bestDistance < tolerance;
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
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        int newObject;
        if (tool == ToolMode.Line)
        {
            start = ResolveDrawingLineEndpoint(start);
            end = ResolveDrawingLineEnd(start, end);
            newObject = _scene.AddLineSegment(
                _scene.ActiveLayer,
                start,
                end,
                ActiveStrokeUnits(),
                ActiveColor(),
                ActiveStrokeColor(),
                6);
        }
        else
        {
            start = VectorUnits.Quantize(_drawSettings.SnapPoint(start));
            end = VectorUnits.Quantize(_drawSettings.SnapPoint(end));
            var center = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
            var size = _drawSettings.ApplyAspectRatio(new SizeF(end.X - start.X, end.Y - start.Y));
            var width = Math.Max(VectorUnits.FromPixels(4), Math.Abs(size.Width));
            var height = Math.Max(VectorUnits.FromPixels(4), Math.Abs(size.Height));
            newObject = _scene.AddObject(
                _scene.ActiveLayer,
                center,
                new SizeF(width, height),
                0,
                ActiveStrokeUnits(),
                ActiveColor(),
                ActiveStrokeColor(),
                24,
                shape);
        }

        if (newObject >= 0 && _materialEditor.GradientEnabled)
        {
            _scene.SetGradientPaint(newObject, _materialEditor.GradientKind, _materialEditor.GradientStops);
        }
        else if (newObject >= 0 && tool != ToolMode.Line)
        {
            newObject = _scene.MergeSameColorFillsAround(newObject, frame: _frame);
        }

        SetSelection(newObject);
        MergeCompatibleLinesAfterDrawingOperation();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void UpdateInspector()
    {
        var basicInspectorScrollY = CaptureInspectorScrollPosition(_basicInspectorPage);
        var sceneInspectorScrollY = CaptureInspectorScrollPosition(_sceneEditPage);
        _basicInspectorPage.SuspendContentLayout();
        _sceneEditPage.SuspendContentLayout();
        try
        {
        _sceneEditorPanel.RefreshSceneStats();
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        UpdateLineEndpointStyleControl();
        if (IsSceneCompositionContext() && SelectedSceneInstance() is { } sceneInstance)
        {
            var drawingObject = _drawingObjects.FirstOrDefault(item =>
                string.Equals(item.Id, sceneInstance.DrawingObjectId, StringComparison.Ordinal));
            _selected.Text = $"Selected: {sceneInstance.Name}";
            _selectedLayer.Text = $"Drawing Object: {drawingObject?.Name ?? "Missing object"}";
            _selectedAtoms.Text = $"Transform: {sceneInstance.ScaleX:0.##}, {sceneInstance.ScaleY:0.##} / {sceneInstance.RotationZ:0.#} deg / skew {sceneInstance.SkewX:0.#}, {sceneInstance.SkewY:0.#}";
            return;
        }

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
        var brushFill = IsBrushTool(_tool) && IsFillShape(shape);
        var inspectorStrokePoints = brushFill ? _brushStrokeWidthPoints : strokePoints;
        if (shape is ShapeKind.Freeform or ShapeKind.Line)
        {
            _materialEditor.SetMaterial(_materialEditor.Fill, stroke, inspectorStrokePoints, stroke.A / 255f);
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

        _materialEditor.SetGradientPreviewTarget(strokeTarget: shape == ShapeKind.Line);

        if (SupportsGradient(shape))
        {
            _materialEditor.SetGradient(
                _scene.GetGradientKind(_selectedObject),
                _scene.HasGradient(_selectedObject)
                    ? _scene.GetGradientStops(_selectedObject)
                    : shape == ShapeKind.Line
                        ? [new GradientStop(0, _materialEditor.Fill), new GradientStop(1, stroke)]
                        : [new GradientStop(0, fill), new GradientStop(1, stroke)]);
        }
        else
        {
            _materialEditor.SetGradient(GradientKind.Solid, [new GradientStop(0, _materialEditor.Fill), new GradientStop(1, _materialEditor.Stroke)]);
        }
        }
        finally
        {
            _sceneEditPage.ResumeContentLayout(performLayout: true);
            _basicInspectorPage.ResumeContentLayout(performLayout: true);
            RestoreInspectorScrollPosition(_basicInspectorPage, basicInspectorScrollY);
            RestoreInspectorScrollPosition(_sceneEditPage, sceneInspectorScrollY);
        }
    }

    private static int CaptureInspectorScrollPosition(ThemedScrollPanel page)
    {
        return page.ScrollPosition;
    }

    private void RestoreInspectorScrollPosition(ThemedScrollPanel page, int verticalPosition)
    {
        if (verticalPosition <= 0 || !page.IsHandleCreated || page.IsDisposed) return;
        void Restore()
        {
            if (page.IsDisposed || !page.IsHandleCreated) return;
            page.RestoreScrollPosition(verticalPosition);
        }

        Restore();
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((MethodInvoker)Restore);
    }

    private int[] SelectedLineEndpointStyleTargets()
    {
        if (IsSceneCompositionContext()) return [];
        var selected = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        if (selected.Length == 0 && (uint)_selectedObject < _scene.ObjectCount)
        {
            selected = [_selectedObject];
        }

        return selected.Length > 0 && selected.All(index => _scene.ShapeKind[index] == ShapeKind.Line)
            ? selected
            : [];
    }

    private void UpdateLineEndpointStyleControl()
    {
        var targets = SelectedLineEndpointStyleTargets();
        if (targets.Length == 0)
        {
            _materialEditor.SetLineEndpointStyles(LineEndpointStyle.Round, LineEndpointStyle.Round, visible: false);
            return;
        }

        _materialEditor.SetLineEndpointStyles(
            _scene.GetLineEndpointStyle(targets[0], startEndpoint: true),
            _scene.GetLineEndpointStyle(targets[0], startEndpoint: false),
            visible: true);
    }

    private void ApplyLineEndpointStyle(LineEndpointStyle endpointStyle, bool startEndpoint)
    {
        var targets = EndpointStyleTargets(SelectedLineEndpointStyleTargets(), startEndpoint);
        if (targets.Length == 0) return;

        var snapshot = _scene.CreateSnapshot();
        var changed = false;
        foreach (var target in targets)
        {
            changed |= _scene.SetLineEndpointStyle(target.ObjectIndex, target.StartEndpoint, endpointStyle);
        }

        if (!changed) return;
        var hierarchyChanged = MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        UpdateInspector();
        _stage.Invalidate();
    }

    private (int ObjectIndex, bool StartEndpoint)[] EndpointStyleTargets(IEnumerable<int> seedObjects, bool startEndpoint)
    {
        var targets = new HashSet<(int ObjectIndex, bool StartEndpoint)>();
        foreach (var objectIndex in seedObjects.Where(index => (uint)index < _scene.ObjectCount))
        {
            if (_scene.ShapeKind[objectIndex] != ShapeKind.Line) continue;
            targets.Add((objectIndex, startEndpoint));
            if (_scene.TryGetLineJoinNeighbor(objectIndex, startEndpoint, _frame, out var neighbor, out var neighborStart))
            {
                targets.Add((neighbor, neighborStart));
            }
        }

        return targets.OrderBy(target => target.ObjectIndex).ThenBy(target => target.StartEndpoint).ToArray();
    }

    private Color ActiveColor()
    {
        return _materialEditor.Fill;
    }

    private void ApplyGradientToSelection(GradientChangedEventArgs gradient)
    {
        if (IsSceneCompositionContext()) return;
        var targets = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && SupportsGradient(_scene.ShapeKind[index]))
            .Distinct()
            .ToArray();
        if (targets.Length == 0 && _selectedObject >= 0 && _selectedObject < _scene.ObjectCount && SupportsGradient(_scene.ShapeKind[_selectedObject]))
        {
            targets = [_selectedObject];
        }

        if (targets.Length == 0) return;
        var snapshot = CreateMaterialUndoSnapshot();
        var changed = false;
        foreach (var objectIndex in targets)
        {
            if (!gradient.Enabled)
            {
                if (!_scene.HasGradient(objectIndex)) continue;
                _scene.DisableLinearGradient(objectIndex);
                changed = true;
                continue;
            }

            if (_scene.HasGradient(objectIndex))
            {
                if (_scene.GetGradientKind(objectIndex) == gradient.Kind
                    && _scene.GetGradientStops(objectIndex).SequenceEqual(gradient.Stops))
                {
                    continue;
                }

                _scene.SetGradientPaint(
                    objectIndex,
                    gradient.Kind,
                    gradient.Stops,
                    _scene.GetGradientStart(objectIndex),
                    _scene.GetGradientEnd(objectIndex));
            }
            else
            {
                _scene.SetGradientPaint(objectIndex, gradient.Kind, gradient.Stops);
            }

            changed = true;
        }

        if (!changed) return;
        PushMaterialUndoSnapshot(snapshot);
        UpdateGradientOverlay();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplyMaterialToSelectedElements(MaterialChangedEventArgs material, float strokeUnits)
    {
        var selected = _selectedElements.ToArray();
        var affected = selected
            .Where(hit => MaterialChangeAffectsSelection(hit.Key.ObjectIndex, hit.Key.Kind, material, strokeUnits))
            .Select(hit => hit.Key)
            .ToHashSet();
        if (affected.Count == 0) return;

        var snapshot = CreateMaterialUndoSnapshot();
        var materialized = _scene.MaterializeSelectedParts(affected.ToArray(), _frame);
        if (!materialized.Success)
        {
            _scene.RestoreSnapshot(snapshot);
            SyncSelectionToStage();
            return;
        }

        var geometryChanged = false;
        foreach (var part in materialized.Parts)
        {
            if (!affected.Contains(part.Source)) continue;
            var objectIndex = part.Result.ObjectIndex;
            if (part.Source.Kind == DrawingElementKind.Fill)
            {
                _scene.Argb[objectIndex] = TargetFillArgb(objectIndex, material);
                _scene.DisableLinearGradient(objectIndex);
                if (_scene.Stroke[objectIndex] != 0)
                {
                    _scene.Stroke[objectIndex] = 0;
                    geometryChanged = true;
                }

                continue;
            }

            var color = TargetStrokeColor(objectIndex, material);
            _scene.Argb[objectIndex] = Color.FromArgb(0, color).ToArgb();
            _scene.StrokeArgb[objectIndex] = color.ToArgb();
            if (_scene.ShapeKind[objectIndex] == ShapeKind.Line
                && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged))
            {
                _scene.DisableLinearGradient(objectIndex);
            }
            var targetStrokeUnits = TargetStrokeUnits(objectIndex, material, strokeUnits);
            if ((material.ApplyAll || material.StrokeWidthChanged)
                && Math.Abs(_scene.Stroke[objectIndex] - targetStrokeUnits) > 0.001f)
            {
                if (IsFreehandShape(_scene.ShapeKind[objectIndex]))
                {
                    _scene.UpdateFreehandStrokeWidth(objectIndex, targetStrokeUnits, rebuildGeometryIndex: false);
                }
                else
                {
                    _scene.Stroke[objectIndex] = targetStrokeUnits;
                    _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
                }

                geometryChanged = true;
            }
        }

        if (geometryChanged) _scene.RebuildGeometryIndex();
        var selectedObjects = materialized.Parts
            .Where(part => affected.Contains(part.Source))
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .ToArray();
        SetSelection(selectedObjects);
        var hierarchyChanged = materialized.Changed;
        if (affected.Any(key => key.Kind == DrawingElementKind.Fill))
        {
            hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
        }

        hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();

        PushMaterialUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplyMaterialToSelectedObjects(MaterialChangedEventArgs material, float strokeUnits)
    {
        var targets = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (targets.Length < 2) return;
        var snapshot = CreateMaterialUndoSnapshot();
        var changed = false;
        var geometryChanged = false;
        foreach (var objectIndex in targets)
        {
            changed |= ApplyMaterialToWholeObject(objectIndex, material, strokeUnits, out var objectGeometryChanged);
            geometryChanged |= objectGeometryChanged;
        }

        if (!changed) return;
        if (geometryChanged) _scene.RebuildGeometryIndex();
        var hierarchyChanged = false;
        var changedFill = material.ApplyAll || material.FillChanged || material.OpacityChanged;
        if (changedFill) hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
        hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();
        PushMaterialUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool ApplyMaterialToWholeObject(
        int objectIndex,
        MaterialChangedEventArgs material,
        float requestedStrokeUnits,
        out bool geometryChanged)
    {
        geometryChanged = false;
        var shape = _scene.ShapeKind[objectIndex];
        var fillCapable = IsFillShape(shape) || shape == ShapeKind.BrushStroke;
        var changed = false;

        if (fillCapable && (material.ApplyAll || material.FillChanged || material.OpacityChanged))
        {
            var fill = TargetFillArgb(objectIndex, material);
            if (_scene.Argb[objectIndex] != fill || _scene.HasGradient(objectIndex))
            {
                _scene.Argb[objectIndex] = fill;
                _scene.DisableLinearGradient(objectIndex);
                changed = true;
            }

            if (shape == ShapeKind.BrushStroke && _scene.StrokeArgb[objectIndex] != fill)
            {
                _scene.StrokeArgb[objectIndex] = fill;
                changed = true;
            }
        }

        if (shape != ShapeKind.BrushStroke && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged))
        {
            var stroke = TargetStrokeColor(objectIndex, material).ToArgb();
            if (_scene.StrokeArgb[objectIndex] != stroke)
            {
                _scene.StrokeArgb[objectIndex] = stroke;
                changed = true;
            }

            if (shape == ShapeKind.Line && _scene.HasGradient(objectIndex))
            {
                _scene.DisableLinearGradient(objectIndex);
                changed = true;
            }
        }

        if (material.ApplyAll || material.StrokeWidthChanged)
        {
            var stroke = TargetStrokeUnits(objectIndex, material, requestedStrokeUnits);
            if (Math.Abs(_scene.Stroke[objectIndex] - stroke) > 0.001f)
            {
                if (IsFreehandShape(shape))
                {
                    _scene.UpdateFreehandStrokeWidth(objectIndex, stroke, rebuildGeometryIndex: false);
                }
                else
                {
                    _scene.Stroke[objectIndex] = stroke;
                    if (shape == ShapeKind.Line)
                    {
                        _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2));
                    }
                }

                changed = true;
                geometryChanged = true;
            }
        }

        return changed;
    }

    private bool WholeObjectMaterialWouldChange(int objectIndex, MaterialChangedEventArgs material, float requestedStrokeUnits)
    {
        var shape = _scene.ShapeKind[objectIndex];
        var fillCapable = IsFillShape(shape) || shape == ShapeKind.BrushStroke;
        if (fillCapable
            && (material.ApplyAll || material.FillChanged || material.OpacityChanged)
            && (_scene.Argb[objectIndex] != TargetFillArgb(objectIndex, material) || _scene.HasGradient(objectIndex)))
        {
            return true;
        }

        if (shape == ShapeKind.BrushStroke
            && (material.ApplyAll || material.FillChanged || material.OpacityChanged)
            && _scene.StrokeArgb[objectIndex] != TargetFillArgb(objectIndex, material))
        {
            return true;
        }

        if (shape != ShapeKind.BrushStroke
            && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged)
            && (_scene.StrokeArgb[objectIndex] != TargetStrokeColor(objectIndex, material).ToArgb()
                || (shape == ShapeKind.Line && _scene.HasGradient(objectIndex))))
        {
            return true;
        }

        return (material.ApplyAll || material.StrokeWidthChanged)
            && Math.Abs(_scene.Stroke[objectIndex] - TargetStrokeUnits(objectIndex, material, requestedStrokeUnits)) > 0.001f;
    }

    private bool MaterialChangeAffectsSelection(int objectIndex, DrawingElementKind selectedKind, MaterialChangedEventArgs material, float strokeUnits)
    {
        if (selectedKind == DrawingElementKind.Fill)
        {
            if (!material.ApplyAll && !material.FillChanged && !material.OpacityChanged) return false;
            return _scene.Argb[objectIndex] != TargetFillArgb(objectIndex, material)
                || _scene.HasGradient(objectIndex);
        }

        if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
        {
            if (!material.ApplyAll && !material.StrokeChanged && !material.StrokeWidthChanged && !material.OpacityChanged) return false;
            return _scene.StrokeArgb[objectIndex] != TargetStrokeColor(objectIndex, material).ToArgb()
                || Math.Abs(_scene.Stroke[objectIndex] - TargetStrokeUnits(objectIndex, material, strokeUnits)) > 0.001f
                || (_scene.ShapeKind[objectIndex] == ShapeKind.Line
                    && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged)
                    && _scene.HasGradient(objectIndex));
        }

        return WholeObjectMaterialWouldChange(objectIndex, material, strokeUnits);
    }

    private int TargetFillArgb(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.Argb[objectIndex]);
        var color = material.ApplyAll || material.FillChanged ? material.Fill : current;
        if (material.ApplyAll || material.OpacityChanged)
        {
            color = Color.FromArgb((int)Math.Clamp(material.Opacity * 255, 0, 255), color);
        }

        return color.ToArgb();
    }

    private Color TargetStrokeColor(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.StrokeArgb[objectIndex]);
        var color = material.ApplyAll || material.StrokeChanged ? material.Stroke : current;
        if (material.ApplyAll || material.OpacityChanged)
        {
            color = Color.FromArgb((int)Math.Clamp(material.Opacity * 255, 0, 255), color);
        }

        return color;
    }

    private float TargetStrokeUnits(int objectIndex, MaterialChangedEventArgs material, float requestedStrokeUnits)
    {
        var stroke = material.ApplyAll || material.StrokeWidthChanged ? requestedStrokeUnits : _scene.Stroke[objectIndex];
        return IsFreehandShape(_scene.ShapeKind[objectIndex])
            ? Math.Max(VectorUnits.StrokePointsToUnits(0.5f), stroke)
            : stroke;
    }

    private float ActiveStrokeUnits()
    {
        var points = _tool switch
        {
            ToolMode.Brush => _brushStrokeWidthPoints,
            ToolMode.PressureBrush => _brushStrokeWidthPoints,
            ToolMode.Eraser => _eraserStrokeWidthPoints,
            _ when IsStandardStrokeTool(_tool) => _standardStrokeWidthPoints,
            _ => (float)_materialEditor.StrokeWidth
        };
        return VectorUnits.StrokePointsToUnits(points);
    }

    private Color ActiveStrokeColor() => _materialEditor.Stroke;

    internal void ReloadModulesForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || plan.Modules == HotReloadModule.None) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ReloadModulesForHotReload(plan));
            return;
        }

        var engineChanged = plan.Includes(HotReloadModule.Engine);
        var workspaceChanged = engineChanged || plan.Includes(HotReloadModule.Workspace);
        var timelineChanged = engineChanged || plan.Includes(HotReloadModule.Timeline);
        var inspectorChanged = engineChanged || plan.Includes(HotReloadModule.Inspector);
        SuspendLayout();
        try
        {
            if (engineChanged)
            {
                if (IsSceneCompositionContext()) RebuildSceneComposition();
                else RebuildDrawingObjectUnderlay();
            }

            if (engineChanged || plan.Includes(HotReloadModule.Rendering)) _stage.ReloadRenderingModuleForHotReload();
            if (workspaceChanged) RefreshWorkspaceModulesAfterHotReload();
            if (timelineChanged) _timeline.RefreshTimeline();
            if (inspectorChanged)
            {
                _drawSettings.NotifyChanged();
                SyncBrushTipSettings();
                _drawSettingsPanel.SetEraserOptionsVisible(_tool == ToolMode.Eraser);
                UpdateInspector();
            }

            if (plan.Includes(HotReloadModule.Shell))
            {
                RefreshToolButtons();
                ApplyToolCursor();
            }

            UpdateStatusBar();
            InvalidateControlTree(this);
            AppLog.Info($"Module reload applied without recreating the workbench: {plan.Modules}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Module reload failed: {plan.Modules}", ex);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    internal void RebuildWorkbenchForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || _restartRequested) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => RebuildWorkbenchForHotReload(plan));
            return;
        }

        StopPlayback();
        FinishPointerInteractionForFrameChange();
        HideToolFlyouts();
        _restartRequested = true;
        var state = CaptureEditorRestartState();
        AppLog.Info($"Rebuilding workbench after runtime hot reload: {plan.Modules}; types: {plan.UpdatedTypes}");
        RestartRequested?.Invoke(this, new EditorRestartRequestedEventArgs(state));
    }

    private void RefreshWorkspaceModulesAfterHotReload()
    {
        _hierarchyPanel.RefreshScene();
        _sceneEditorPanel.BindScene(_scene);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        _sceneEditorPanel.SetActiveDrawingObject(ActiveDrawingObject());
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _libraryVaultPanel.SetActiveDrawingObject(ActiveDrawingObject()?.Id);
        if (_vaultDrawerOpen) _libraryVaultPanel.RefreshProjectObjects();
    }

    private static void InvalidateControlTree(Control control)
    {
        control.Invalidate();
        foreach (Control child in control.Controls) InvalidateControlTree(child);
    }

    private void ShowWorkspace(WorkspaceView view)
    {
        if (view != WorkspaceView.BasicDrawing) HideBrushColorPalette();
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
            CancelPenCurve();
            CancelFreehandStroke();
            _stage.ClearDrawingPreview();
        }

        RefreshToolButtons();
        _drawSettingsPanel.SetEraserOptionsVisible(_tool == ToolMode.Eraser);
        _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
        ApplyToolCursor();
    }

    private static bool IsDrawingTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.Pencil or ToolMode.Brush or ToolMode.PressureBrush;
    }

    private static bool IsBasicDrawingOnlyTool(ToolMode tool)
    {
        return IsDrawingTool(tool) || tool is ToolMode.Fill or ToolMode.InkBottle or ToolMode.Gradient or ToolMode.Eraser;
    }

    private static bool IsStandardStrokeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen;
    }

    private static bool IsFreehandTool(ToolMode tool)
    {
        return tool is ToolMode.Pencil or ToolMode.Brush or ToolMode.PressureBrush or ToolMode.Eraser;
    }

    private static bool IsBrushTool(ToolMode tool)
    {
        return tool is ToolMode.Brush or ToolMode.PressureBrush;
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    private static bool IsFillShape(ShapeKind shape)
    {
        return shape is not ShapeKind.Line and not ShapeKind.Freeform and not ShapeKind.BrushStroke;
    }

    private static bool SupportsGradient(ShapeKind shape) => IsFillShape(shape) || shape == ShapeKind.Line;

    private static bool IsShapeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star;
    }

    private static bool IsLineTool(ToolMode tool)
    {
        return tool is ToolMode.Line or ToolMode.Pen or ToolMode.Pencil;
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
            ToolMode.Pen => SvgIconKind.Pen,
            ToolMode.Pencil => SvgIconKind.Pencil,
            ToolMode.Brush => SvgIconKind.Brush,
            ToolMode.PressureBrush => SvgIconKind.PressureBrush,
            ToolMode.Fill => SvgIconKind.Fill,
            ToolMode.InkBottle => SvgIconKind.InkBottle,
            ToolMode.Gradient => SvgIconKind.Gradient,
            ToolMode.Eraser => SvgIconKind.Eraser,
            ToolMode.Transform => SvgIconKind.Transform,
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

    private static string LineToolName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Pen => "Pen Tool",
            ToolMode.Pencil => "Pencil Tool",
            _ => "Line Tool"
        };
    }

    private static string BrushToolName(ToolMode tool)
    {
        return tool == ToolMode.PressureBrush ? "Pressure Brush Tool" : "Brush Tool";
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

        if (_lineToolButton is not null)
        {
            var enabled = !DrawingToolsBlocked();
            _lineToolButton.Enabled = enabled;
            _lineToolButton.Icon = ToolIconKind(_activeLineTool);
            _lineToolButton.Tag = _activeLineTool;
            _lineToolButton.AccessibleName = LineToolName(_activeLineTool);
            if (IsLineTool(_tool)) Theme.StyleActiveButton(_lineToolButton);
            else Theme.StyleButton(_lineToolButton);
            if (!enabled) _lineToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _lineToolButton.Invalidate();
        }

        foreach (var (tool, button) in _lineFlyoutButtons)
        {
            var enabled = !DrawingToolsBlocked();
            button.Enabled = enabled;
            if (tool == _activeLineTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_brushToolButton is not null)
        {
            var enabled = !DrawingToolsBlocked();
            _brushToolButton.Enabled = enabled;
            _brushToolButton.Icon = ToolIconKind(_activeBrushTool);
            _brushToolButton.Tag = _activeBrushTool;
            _brushToolButton.AccessibleName = BrushToolName(_activeBrushTool);
            if (IsBrushTool(_tool)) Theme.StyleActiveButton(_brushToolButton);
            else Theme.StyleButton(_brushToolButton);
            if (!enabled) _brushToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _brushToolButton.Invalidate();
        }

        foreach (var (tool, button) in _brushFlyoutButtons)
        {
            var enabled = !DrawingToolsBlocked();
            button.Enabled = enabled;
            if (tool == _activeBrushTool) Theme.StyleActiveButton(button);
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

    private sealed class DrawingObjectNameDialog : Form
    {
        private readonly TextBox _input = new();

        private DrawingObjectNameDialog(string title, string label, string initialValue)
        {
            Text = title;
            ClientSize = new Size(400, 146);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Panel;
            Font = Theme.UiFont();

            var prompt = new Label
            {
                Text = label,
                Left = 16,
                Top = 14,
                Width = 368,
                Height = 24,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(prompt);

            _input.Left = 16;
            _input.Top = 44;
            _input.Width = 368;
            _input.Height = 28;
            _input.Text = initialValue;
            _input.SelectAll();
            Theme.StyleTextBox(_input);
            Controls.Add(_input);

            var cancel = new Button { Text = "Cancel", Left = 308, Top = 92, Width = 76, Height = 30 };
            Theme.StyleButton(cancel);
            cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            var save = new Button { Text = "Rename", Left = 224, Top = 92, Width = 76, Height = 30 };
            Theme.StyleButton(save);
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_input.Text)) return;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(save);

            AcceptButton = save;
            CancelButton = cancel;
            Shown += (_, _) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
        }

        public static bool TryAsk(IWin32Window owner, string title, string label, string initialValue, out string value)
        {
            using var dialog = new DrawingObjectNameDialog(title, label, initialValue);
            var accepted = dialog.ShowDialog(owner) == DialogResult.OK;
            value = accepted ? dialog._input.Text.Trim() : string.Empty;
            return accepted;
        }
    }

    private static Label MetricLabel(string text, int width) => new() { Text = text, Left = 8, Top = 10, Width = width, Height = 22, ForeColor = Theme.Muted, BackColor = Theme.Top, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static ToolStripStatusLabel StatusLabel(string text) => new() { Text = text, ForeColor = Theme.Muted, Spring = false, Margin = new Padding(0, 0, 10, 0) };
    private static ToolStripStatusLabel StatusSeparator() => new() { Text = "|", ForeColor = Theme.Border, Margin = new Padding(0, 0, 10, 0) };
    private static bool IsModuleHotReloadEnabled() => Environment.GetEnvironmentVariable("V2D_DEV_HOT_RELOAD") == "1";
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

    private static bool ContainsFocusedTextEditor(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is NumericUpDown) return false;
        if (control is TextBoxBase or ComboBox) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedTextEditor(child)) return true;
        }

        return false;
    }

    private static bool ContainsFocusedInteractiveControl(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is ButtonBase or ListControl or TreeView or ListView or ModernSlider) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedInteractiveControl(child)) return true;
        }

        return false;
    }

    private static bool ContainsFocusedButton(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is ButtonBase) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedButton(child)) return true;
        }

        return false;
    }
}
