using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class EditorRestartState
{
    public required VectorProject Project { get; init; }
    public string ProjectManifestPath { get; init; } = "";
    public bool ProjectDirty { get; init; }
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
    TimelineKeyframeKind Kind,
    IReadOnlyDictionary<string, InstanceFrameState> InstanceStates);

internal sealed class TimelineFrameClipboard
{
    public required TimelineClipboardCell[] Cells { get; init; }
    public VectorScene? DrawingSource { get; init; }
}

internal readonly record struct PerformanceRateSample(
    bool HasRenderRate,
    double RenderFps,
    bool HasUpdateRate,
    double UpdatesPerSecond);

internal readonly record struct PenAnchorSnapResult(
    PointF Point,
    bool ObjectSnapped,
    bool AlignX,
    bool AlignY);

internal readonly record struct LineEndpointSnapCandidate(int ObjectIndex, PointF Point);

internal readonly record struct TimelineBlankKeyframeRangePlan(int[] ExtensionFrames, int BlankFrame);

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
    private const double MetricsRefreshSeconds = 1.0;
    private const double MaxFrameSeconds = 0.1;
    private const int IdleTimerIntervalMs = 250;
    private const int PlaybackTimerIntervalMs = 8;
    private const float EndpointConnectionToleranceUnits = 1.25f;
    private const int MaxUndoSnapshots = 32;
    private const long MaxUndoSnapshotBytes = 128L * 1024 * 1024;
    private const float PasteOffsetUnits = 96f;
    private const int MaxFreehandSamples = 16_384;
    private const float FreehandSampleSpacingPixels = 1.25f;
    private const int MaxFreehandPreviewPoints = 256;
    private const int MaxStampBrushPreviewPoints = 96;
    private const int MaxInteractiveLineMergeCandidates = 64;
    private const int MaxInteractiveLineMergeSceneObjects = 512;
    private const double ShapeGradientPreviewIntervalMilliseconds = 33;
    private const int VaultDrawerExpandedWidth = 306;
    private const int VaultDrawerMaximumPixelsPerTick = 64;
    private const double VaultDrawerAnimationMilliseconds = 160;
    private const int InspectorPanelExpandedWidth = 324;
    private const int TimelinePanelDefaultHeight = 192;
    private const double WorkspacePanelAnimationMilliseconds = 180;

    private VectorProject _project = VectorProject.CreateEmpty();
    private string _projectManifestPath = "";
    private bool _projectDirty;
    private ApplicationSettings _applicationSettings = ApplicationSettingsStore.Load();
    private VectorScene _scene;
    private readonly VectorScene _sceneEditStage = new();
    private readonly VectorScene _drawingObjectUnderlayStage = new();
    private readonly VectorScene _onionSkinStage = new();
    private readonly VectorScene _localOnionSkinStage = new();
    private readonly VectorScene _nestedOnionSkinStage = new();
    private readonly VectorScene _dragPreviewStage = new();
    private readonly VectorScene _shapeGradientBrushPreviewStage = new();
    private SceneCompositionResult _sceneCompositionResult = SceneCompositionResult.Empty;
    private SceneCompositionResult _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
    private readonly StageControl _stage;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IdleTimerIntervalMs };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _metricsClock = Stopwatch.StartNew();
    private FixedStepBatcher _updateBatcher = new(TargetUps);
    private readonly DrawSettings _drawSettings = new();
    private readonly Label _fps = MetricLabel("FPS --", 76);
    private readonly Label _draw = MetricLabel("Draw 0", 210);
    private readonly Label _atoms = MetricLabel("Atoms 0", 210);
    private readonly Label _zoom = MetricLabel("Zoom 100%", 112);
    private readonly Label _selected = InspectorLabel("Selected: None");
    private readonly Label _selectedLayer = InspectorLabel("Layer: -");
    private readonly Label _selectedAtoms = InspectorLabel("Atoms: -");
    private readonly Label _objectMetric = InspectorLabel("Objects: 0");
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();
    private bool? _lastDrawingToolsBlocked;
    private readonly ToolMode[] _shapeTools = [ToolMode.Rectangle, ToolMode.Ellipse, ToolMode.Triangle, ToolMode.Polygon, ToolMode.Star];
    private readonly ToolMode[] _lineTools = [ToolMode.Line, ToolMode.Pen, ToolMode.SimplePen, ToolMode.Pencil];
    private readonly ToolMode[] _brushTools = [ToolMode.Brush, ToolMode.PressureBrush];
    private readonly ToolPairGroup _selectionToolGroup = new([ToolMode.Select, ToolMode.Transform]);
    private readonly ToolPairGroup _paintToolGroup = new([ToolMode.Fill, ToolMode.InkBottle]);
    private readonly Dictionary<ToolMode, Button> _shapeFlyoutButtons = new();
    private readonly Dictionary<ToolMode, Button> _lineFlyoutButtons = new();
    private readonly Dictionary<ToolMode, Button> _brushFlyoutButtons = new();
    private readonly System.Windows.Forms.Timer _shapeFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _lineFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _brushFlyoutHideTimer = new();
    private readonly System.Windows.Forms.Timer _vaultDrawerTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _workspacePanelAnimationTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _lineDragPreviewTimer = new() { Interval = 16 };
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
    private readonly Size _timelinePanelMinimumSize;
    private TimelineFrameClipboard? _timelineClipboard;
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _renderFpsStatus = StatusLabel("Render FPS --");
    private readonly ToolStripStatusLabel _animationFpsStatus = StatusLabel("UPS --/300  Animation FPS 30");
    private readonly ToolStripStatusLabel _zoomStatus = StatusLabel("Zoom 100%");
    private readonly ToolStripStatusLabel _devReloadStatus = StatusLabel("Module Reload On");
    private WindowChromeButton? _maximizeButton;
    private Label? _projectTitleLabel;
    private SvgIconButton? _propertiesPanelButton;
    private SvgIconButton? _timelinePanelButton;
    private readonly PlaybackSettingsPanel _playbackSettings = new();
    private readonly ModernNumericUpDown _topPlaybackFps = new()
    {
        Minimum = 1,
        Maximum = 120,
        Value = 30,
        DecimalPlaces = 3,
        Increment = 0.001m,
        WheelAdjustsHoveredDigit = true,
        Suffix = "fps",
        Width = 88,
        Height = Theme.ControlHeightCompact,
        AccessibleName = "Animation frame rate in frames per second"
    };
    private bool _updatingTopPlaybackFps;
    private bool _syncingProjectPlaybackSettings;
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly BrushTipPanel _brushTipPanel = new();
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly DrawingObjectInstancePanel _drawingObjectInstancePanel = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly SceneEditorPanel _sceneEditorPanel = new();
    private readonly LibraryVaultPanel _libraryVaultPanel = new();
    private readonly AnimatedContextMenuStrip _stageContextMenu = new();
    private readonly AnimatedContextMenuStrip _mainMenu = new();
    private readonly ToolStripMenuItem _deleteNestedInstanceMenuItem = new("Delete");
    private readonly ToolStripMenuItem _breakApartMenuItem = new("Break Apart");
    private readonly ToolStripSeparator _deleteNestedInstanceMenuSeparator = new();
    private readonly ToolStripMenuItem _flipHorizontalMenuItem = new("Flip Horizontal");
    private readonly ToolStripMenuItem _flipVerticalMenuItem = new("Flip Vertical");
    private readonly ToolStripSeparator _transformActionsMenuSeparator = new();
    private readonly ToolStripMenuItem _bringForwardMenuItem = new("Bring Forward")
    {
        ShortcutKeys = Keys.Control | Keys.Up
    };
    private readonly ToolStripMenuItem _sendBackwardMenuItem = new("Send Backward")
    {
        ShortcutKeys = Keys.Control | Keys.Down
    };
    private readonly ToolStripSeparator _stackActionsMenuSeparator = new();
    private readonly ToolStripMenuItem _convertLineToFillMenuItem = new("Convert Line to Fill");
    private readonly ToolStripSeparator _lineActionsMenuSeparator = new();
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
    private readonly HashSet<string> _selectedSceneInstanceIds = new(StringComparer.Ordinal);
    private int[] _selectedSceneInstanceObjectIndices = [];
    private bool _sceneInstanceMoveActive;
    private bool _sceneInstancePreviewDirty;
    private readonly HashSet<string> _sceneInstanceTimelineKeyframeEnsuredIds = new(StringComparer.Ordinal);
    private bool _sceneInstanceTimelineDirty;
    private string _dragPreviewDrawingObjectId = "";
    private PointF _dragPreviewPosition;
    private readonly Dictionary<int, PointF> _selectedMoveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurve2Starts = new();
    private readonly Dictionary<int, (PointF Start, PointF End)> _selectedGradientStarts = new();
    private readonly List<LineEndpointEditStart> _lineEndpointEditStarts = new();
    private readonly List<FillBoundaryLineLink> _fillBoundaryLineLinks = new();
    private readonly Dictionary<(int X, int Y), List<LineEndpointSnapCandidate>> _lineEndpointSnapBuckets = new();
    private readonly Stack<DrawingUndoEntry> _undoStack = new();
    private readonly Stack<SceneTimelineUndoEntry> _sceneTimelineUndoStack = new();
    private OnionSkinRangeEditSession? _onionSkinRangeEditSession;
    private readonly List<ClipboardObject> _clipboardObjects = new();
    private readonly List<ClipboardDrawingObjectInstance> _clipboardDrawingObjectInstances = new();
    private readonly List<PointF> _freehandSamples = new(1024);
    private readonly List<PressureBrushSample> _pressureBrushSamples = new(1024);
    private long _shapeGradientPreviewUpdatedAt;
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
    private PointF? _curveControl2Start;
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
    private bool _gradientEditChanged;
    private PointF _transformLastPointer;
    private float _transformLastAngle;
    private bool _geometryDirty;
    private double _renderFps;
    private double _updatesPerSecond;
    private bool _hasRenderRateSample;
    private bool _hasUpdateRateSample;
    private double _playbackAccumulator;
    private int _updatesThisSample;
    private int _rendersThisSample;
    private bool _syncingFrame;
    private bool _viewPanning;
    private bool _viewZooming;
    private bool _viewOrbiting;
    private bool _viewReferencePanning;
    private bool _viewReferenceZooming;
    private bool _spacePanKeyDown;
    private bool _spacePanHeld;
    private bool _spacePanPointerActive;
    private bool _marqueeSelecting;
    private bool _marqueeSelectionCancellationPending;
    private bool _detachedSelectionForMove;
    private bool _pointerHitWasAlreadySelected;
    private bool _selectionWasEmptyOnPointerDown;
    private bool _forceMarqueeOnPointerDown;
    private bool _additiveSelection;
    private DrawingElementHit _pendingClickSelection = DrawingElementHit.None;
    private Point? _marqueeStart;
    private int[] _marqueeSelectionBase = [];
    private string[] _marqueeInstanceSelectionBase = [];
    private Point? _freehandLastScreen;
    private PointF? _penStartWorld;
    private PointF? _penEndWorld;
    private PointF? _penControlWorld;
    private Point? _penEndpointScreen;
    private bool _penSegmentDragging;
    private PointF? _traditionalPenFirstAnchor;
    private PointF? _traditionalPenCurrentAnchor;
    private PointF? _traditionalPenOutgoingHandle;
    private PointF? _traditionalPenPendingAnchor;
    private PointF? _traditionalPenIncomingHandle;
    private PointF? _traditionalPenPendingOutgoingHandle;
    private PointF? _traditionalPenLockedIncomingHandle;
    private Point? _traditionalPenPointerDownScreen;
    private bool _traditionalPenPointerDown;
    private bool _traditionalPenClosing;
    private bool _traditionalPenAnchorEditing;
    private bool _traditionalPenAnchorEditTracksCurrent;
    private int _traditionalPenAnchorEditObject = -1;
    private bool _traditionalPenAnchorEditStartEndpoint;
    private PointF _traditionalPenAnchorEditOriginalCurrent;
    private PointF? _traditionalPenAnchorEditOriginalOutgoing;
    private int _traditionalPenSegmentCount;
    private PointF? _pendingLineDragWorld;
    private RectangleF _lineEndpointSnapCacheBounds;
    private float _lineEndpointSnapBucketSize;
    private int _lineEndpointSnapCacheLayer = -1;
    private bool _linkedFillBoundaryPreviewDirty;
    private VectorScene? _penAnchorCacheScene;
    private long _penAnchorCacheGeometryRevision = -1;
    private int _penAnchorCacheLayer = -1;
    private int _penAnchorCacheFrame = -1;
    private PointF[] _penAnchorCache = [];
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
    private int _vaultDrawerVisibleWidth;
    private int _vaultDrawerAnimationFromWidth;
    private int _vaultDrawerAnimationToWidth;
    private long _vaultDrawerAnimationStartedTimestamp;
    private double _vaultDrawerAnimationDurationMilliseconds = VaultDrawerAnimationMilliseconds;
    private bool _inspectorPanelOpen = true;
    private bool _timelinePanelOpen = true;
    private int _timelinePanelExpandedHeight = TimelinePanelDefaultHeight;
    private int _inspectorPanelAnimationFromWidth = InspectorPanelExpandedWidth;
    private int _inspectorPanelAnimationToWidth = InspectorPanelExpandedWidth;
    private int _timelinePanelAnimationFromHeight = TimelinePanelDefaultHeight;
    private int _timelinePanelAnimationToHeight = TimelinePanelDefaultHeight;
    private long _workspacePanelAnimationStartedTimestamp;
    private double _workspacePanelAnimationDurationMilliseconds = WorkspacePanelAnimationMilliseconds;
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
    private string _stageContextMenuNestedInstanceId = "";
    private int[] _stageContextMenuBreakApartObjects = [];
    private string[] _stageContextMenuBreakApartInstances = [];
    private readonly EditorRestartState? _restartState;
    private bool _restartRequested;

    private readonly record struct LineEndpointEditStart(
        int ObjectIndex,
        bool StartEndpoint,
        PointF OriginalEndpoint,
        PointF OppositeEndpoint,
        PointF Control1,
        PointF Control2,
        bool KeepStraight);

    private readonly record struct DrawingStackKey(long Order, double SubOrder);

    private sealed record DrawingUndoEntry(
        VectorSceneSnapshot Snapshot,
        DrawingObjectDefinition? DrawingObject = null,
        DrawingObjectInstanceDefinition[]? InstanceSnapshot = null,
        int? PlayheadFrame = null,
        TimelineSelectionSnapshot? TimelineSelection = null);

    private sealed record SceneTimelineUndoEntry(
        SceneDefinition Scene,
        AnimationTimelineSnapshot Snapshot,
        SceneLayerSnapshot? LayerSnapshot = null,
        DrawingObjectInstanceDefinition[]? InstanceSnapshot = null,
        int? PlayheadFrame = null,
        TimelineSelectionSnapshot? TimelineSelection = null);

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
        public bool AutoKeyframeChanged { get; set; }
        public bool AutoKeyframePreparationAttempted { get; set; }
        public bool UndoPushed { get; set; }
        public bool HierarchyDirty { get; set; }
        public bool InspectorDirty { get; set; }
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
        int ShapeVertexCount,
        PointF CurveControl,
        PointF CurveControl2,
        PointF[][]? PathWorldContours,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle,
        string? ImportedSvgSource);

    private sealed record ClipboardDrawingObjectInstance(
        string DrawingObjectId,
        string SceneLayerId,
        string Name,
        InstanceFrameState BaseState,
        InstanceStateKeyframe[] StateKeyframes);

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
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = TimelinePanelDefaultHeight };
        _timelinePanelMinimumSize = _timeline.MinimumSize;
        _timeline.PlaybackFps = _playbackSettings.Fps;
        _topPlaybackFps.Value = _playbackSettings.Fps;
        _timeline.FrameWidth = _applicationSettings.TimelineFrameWidth;
        _timeline.FrameHeightPreset = _applicationSettings.TimelineFrameHeight;
        _timeline.AutoKeyframeEnabled = _applicationSettings.TimelineAutoKeyframeEnabled;
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
        _selectionToolGroup.HideTimer.Tick += (_, _) => UpdateToolPairFlyoutVisibility(_selectionToolGroup);
        _paintToolGroup.HideTimer.Tick += (_, _) => UpdateToolPairFlyoutVisibility(_paintToolGroup);
        _vaultDrawerTimer.Tick += (_, _) => TickVaultDrawer();
        _workspacePanelAnimationTimer.Tick += (_, _) => TickWorkspacePanelAnimation();
        _lineDragPreviewTimer.Tick += (_, _) => TickLineDragPreview();
        BuildUi();
        HookEvents();
        UiLocalization.Watch(this);
        UiLocalization.Watch(_mainMenu);
        UiLocalization.Watch(_stageContextMenu);
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
        _stageContextMenu.Items.Add(_deleteNestedInstanceMenuItem);
        _stageContextMenu.Items.Add(_breakApartMenuItem);
        _stageContextMenu.Items.Add(_deleteNestedInstanceMenuSeparator);
        _stageContextMenu.Items.Add(_flipHorizontalMenuItem);
        _stageContextMenu.Items.Add(_flipVerticalMenuItem);
        _stageContextMenu.Items.Add(_transformActionsMenuSeparator);
        _stageContextMenu.Items.Add(_bringForwardMenuItem);
        _stageContextMenu.Items.Add(_sendBackwardMenuItem);
        _stageContextMenu.Items.Add(_stackActionsMenuSeparator);
        _stageContextMenu.Items.Add(_convertLineToFillMenuItem);
        _stageContextMenu.Items.Add(_lineActionsMenuSeparator);
        _stageContextMenu.Items.Add(_mergeAndSimplifyLinesMenuItem);
        _deleteNestedInstanceMenuItem.Click += (_, _) =>
            DeleteNestedDrawingObjectInstance(_stageContextMenuNestedInstanceId);
        _breakApartMenuItem.Click += (_, _) => BreakApartStageSelection();
        _flipHorizontalMenuItem.Click += (_, _) => FlipSelectedDrawingObjects(horizontal: true);
        _flipVerticalMenuItem.Click += (_, _) => FlipSelectedDrawingObjects(horizontal: false);
        _bringForwardMenuItem.Click += (_, _) => MoveSelectedDrawingObjectsInStack(1);
        _sendBackwardMenuItem.Click += (_, _) => MoveSelectedDrawingObjectsInStack(-1);
        _convertLineToFillMenuItem.Click += (_, _) => ConvertStageContextLineToFill();
        _mergeAndSimplifyLinesMenuItem.Click += (_, _) => MergeAndSimplifyLineSegments(_stageContextMenuLocation);
        _stageContextMenu.Opening += (_, _) =>
        {
            _stageContextMenuLineObject = -1;
            _stageContextMenuNestedInstanceId = "";
            _stageContextMenuBreakApartObjects = [];
            _stageContextMenuBreakApartInstances = [];
            if (TryGetStageContextNestedInstance(_stageContextMenuLocation, out var nestedInstance))
            {
                _stageContextMenuNestedInstanceId = nestedInstance.Id;
                if (!_selectedSceneInstanceIds.Contains(nestedInstance.Id))
                {
                    SetSceneInstanceSelection(nestedInstance);
                    UpdateInspector();
                }
            }

            var canDeleteNestedInstance = !string.IsNullOrWhiteSpace(_stageContextMenuNestedInstanceId);
            if (!canDeleteNestedInstance
                && TryGetStageContextDrawingElement(_stageContextMenuLocation, out var contextElement)
                && !_selectedElements.Any(hit => hit.Key == contextElement.Key)
                && !_selectedObjects.Contains(contextElement.Key.ObjectIndex))
            {
                SetSelection(contextElement);
                UpdateInspector();
            }

            var drawingTargets = SelectedActiveDrawingObjectIndices();
            var selectedInstances = SelectedSceneInstances();
            var breakApartWorkspace = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing;
            var hasBreakApartTarget = selectedInstances.Count > 0 || drawingTargets.Length > 0;
            var canBreakInstances = breakApartWorkspace
                && !DrawingToolsBlocked()
                && selectedInstances.Count > 0
                && selectedInstances.All(CanBreakApartInstance);
            var canBreakSvg = breakApartWorkspace
                && !DrawingToolsBlocked()
                && selectedInstances.Count == 0
                && _selectedElements.Count == 0
                && drawingTargets.Length > 0
                && drawingTargets.All(index => IsImportedSvgObject(index) && CanBreakApartObject(index));
            if (canBreakInstances)
            {
                _stageContextMenuBreakApartInstances = selectedInstances.Select(instance => instance.Id).ToArray();
            }
            else if (canBreakSvg)
            {
                _stageContextMenuBreakApartObjects = drawingTargets;
            }
            var commandTargetCount = canDeleteNestedInstance ? selectedInstances.Count : drawingTargets.Length;
            var canBringForward = canDeleteNestedInstance
                ? CanMoveSelectedNestedDrawingObjectsInStack(1)
                : _scene.CanMoveObjectsInLayerStack(drawingTargets, 1, _frame);
            var canSendBackward = canDeleteNestedInstance
                ? CanMoveSelectedNestedDrawingObjectsInStack(-1)
                : _scene.CanMoveObjectsInLayerStack(drawingTargets, -1, _frame);
            _deleteNestedInstanceMenuItem.Visible = canDeleteNestedInstance;
            _breakApartMenuItem.Visible = breakApartWorkspace && hasBreakApartTarget;
            _breakApartMenuItem.Enabled = canBreakInstances || canBreakSvg;
            _deleteNestedInstanceMenuSeparator.Visible = canDeleteNestedInstance || _breakApartMenuItem.Visible;
            _flipHorizontalMenuItem.Visible = true;
            _flipVerticalMenuItem.Visible = true;
            _transformActionsMenuSeparator.Visible = true;
            _bringForwardMenuItem.Visible = true;
            _sendBackwardMenuItem.Visible = true;
            _stackActionsMenuSeparator.Visible = !canDeleteNestedInstance;
            _convertLineToFillMenuItem.Visible = !canDeleteNestedInstance;
            _lineActionsMenuSeparator.Visible = !canDeleteNestedInstance;
            _mergeAndSimplifyLinesMenuItem.Visible = !canDeleteNestedInstance;
            var containsImportedSvg = !canDeleteNestedInstance
                && drawingTargets.Any(IsImportedSvgObject);
            _flipHorizontalMenuItem.Enabled = commandTargetCount > 0 && !containsImportedSvg;
            _flipVerticalMenuItem.Enabled = commandTargetCount > 0 && !containsImportedSvg;
            _bringForwardMenuItem.Enabled = canBringForward;
            _sendBackwardMenuItem.Enabled = canSendBackward;
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
        var menuButton = new SvgIconButton(SvgIconKind.Menu)
        {
            Left = 12,
            Top = 11,
            AccessibleName = "Main menu"
        };
        Theme.StyleButton(menuButton);
        menuButton.MouseEnter += (_, _) => _toolTip.ShowFor(menuButton, "Main menu");
        menuButton.MouseLeave += (_, _) => _toolTip.HideTip();
        menuButton.Click += (_, _) => _mainMenu.Show(menuButton, new Point(0, menuButton.Height + 4));
        top.Controls.Add(menuButton);
        BuildMainMenu();
        var mark = new Label { Text = "V2", Left = 58, Top = 12, Width = 30, Height = 30, ForeColor = Theme.Accent, BackColor = Theme.Top, Font = Theme.UiFont(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        RegisterWindowDrag(mark);
        top.Controls.Add(mark);
        _projectTitleLabel = Theme.Label("Untitled Project", 102, 16, 190, Theme.Text, Theme.UiFont(10, FontStyle.Bold));
        _projectTitleLabel.AutoEllipsis = true;
        RegisterWindowDrag(_projectTitleLabel);
        top.Controls.Add(_projectTitleLabel);
        _propertiesPanelButton = new SvgIconButton(SvgIconKind.PropertiesPanel)
        {
            Left = 300,
            Top = 11,
            AccessibleName = "Properties panel"
        };
        Theme.StyleActiveButton(_propertiesPanelButton);
        _propertiesPanelButton.Click += (_, _) => ToggleInspectorPanel();
        _propertiesPanelButton.MouseEnter += (_, _) => _toolTip.ShowFor(_propertiesPanelButton, "Properties (F1)");
        _propertiesPanelButton.MouseLeave += (_, _) => _toolTip.HideTip();
        top.Controls.Add(_propertiesPanelButton);
        _timelinePanelButton = new SvgIconButton(SvgIconKind.TimelinePanel)
        {
            Left = 342,
            Top = 11,
            AccessibleName = "Timeline panel"
        };
        Theme.StyleActiveButton(_timelinePanelButton);
        _timelinePanelButton.Click += (_, _) => ToggleTimelinePanel();
        _timelinePanelButton.MouseEnter += (_, _) => _toolTip.ShowFor(_timelinePanelButton, "Timeline (F2)");
        _timelinePanelButton.MouseLeave += (_, _) => _toolTip.HideTip();
        top.Controls.Add(_timelinePanelButton);
        var playbackFpsLabel = new Label
        {
            Text = "FPS",
            Width = 34,
            Height = Theme.ControlHeightCompact,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleRight,
            AccessibleName = "Animation frame rate"
        };
        Theme.StyleNumeric(_topPlaybackFps);
        top.Controls.Add(playbackFpsLabel);
        top.Controls.Add(_topPlaybackFps);
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
        top.Resize += (_, _) =>
        {
            PositionWindowChromeButtons(top, restart, minimize, _maximizeButton, close);
            PositionTopPlaybackFpsControls(top, restart, playbackFpsLabel, _topPlaybackFps);
        };
        Resize += (_, _) => UpdateWindowChromeState();
        PositionWindowChromeButtons(top, restart, minimize, _maximizeButton, close);
        PositionTopPlaybackFpsControls(top, restart, playbackFpsLabel, _topPlaybackFps);
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

        var vaultDrawer = new Panel
        {
            Left = 0,
            Top = _workspaceHeader.Height,
            Width = VaultDrawerExpandedWidth,
            BackColor = Theme.Panel,
            Padding = Padding.Empty,
            Visible = false
        };
        _vaultDrawer = vaultDrawer;
        PaintRightBorder(vaultDrawer);
        _libraryVaultPanel.Dock = DockStyle.None;
        _libraryVaultPanel.SetBounds(
            0,
            0,
            VaultDrawerExpandedWidth,
            Math.Max(0, vaultDrawer.ClientSize.Height));
        _libraryVaultPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        vaultDrawer.Controls.Add(_libraryVaultPanel);
        body.Controls.Add(vaultDrawer);

        _inspectorHost.Dock = DockStyle.Right;
        _inspectorHost.Width = InspectorPanelExpandedWidth;
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
        AddToolPairGroup(tools, stagePanel, _selectionToolGroup);
        AddShapeToolGroup(tools, stagePanel);
        AddLineToolGroup(tools, stagePanel);
        AddBrushToolGroup(tools, stagePanel);
        AddToolPairGroup(tools, stagePanel, _paintToolGroup);
        AddTool(tools, SvgIconKind.Eyedropper, ToolMode.Eyedropper, "Eyedropper Tool");
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
        vaultDrawer.BringToFront();
        void LayoutVaultDrawer()
        {
            var top = _workspaceHeader.Bottom;
            var height = Math.Max(0, body.ClientSize.Height - top);
            if (vaultDrawer.Top != top || vaultDrawer.Height != height)
            {
                vaultDrawer.SetBounds(0, top, VaultDrawerExpandedWidth, height);
                _libraryVaultPanel.Height = vaultDrawer.ClientSize.Height;
                ApplyVaultDrawerClip();
            }
        }
        body.Resize += (_, _) => LayoutVaultDrawer();
        LayoutVaultDrawer();
        RefreshToolButtons();
        _workspaceTabs.BringToFront();

        Controls.Add(_timeline);
        _timeline.Resize += (_, _) =>
        {
            if (_timelinePanelOpen
                && !_workspacePanelAnimationTimer.Enabled
                && _timeline.Visible
                && _timeline.Height > 0)
            {
                _timelinePanelExpandedHeight = _timeline.Height;
            }
        };
        BuildStatusBar();
        Controls.Add(_statusBar);
    }

    private void BuildMainMenu()
    {
        var newProject = new ToolStripMenuItem("New Project") { ShortcutKeys = Keys.Control | Keys.N };
        newProject.Click += (_, _) => CreateNewProjectFromCommand();
        var openProject = new ToolStripMenuItem("Open Project...") { ShortcutKeys = Keys.Control | Keys.O };
        openProject.Click += (_, _) => OpenProjectFromDialog();
        var importSvg = new ToolStripMenuItem("Import SVG...");
        importSvg.Click += (_, _) => ImportSvgFromDialog();
        var saveProject = new ToolStripMenuItem("Save Project") { ShortcutKeys = Keys.Control | Keys.S };
        saveProject.Click += (_, _) => SaveProject();
        var saveProjectAs = new ToolStripMenuItem("Save Project As...") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
        saveProjectAs.Click += (_, _) => SaveProjectAs();
        var settings = new ToolStripMenuItem("Settings...");
        settings.Click += (_, _) => ShowSettings();
        _mainMenu.Items.AddRange(new ToolStripItem[]
        {
            newProject,
            openProject,
            importSvg,
            new ToolStripSeparator(),
            saveProject,
            saveProjectAs,
            new ToolStripSeparator(),
            settings
        });
        _mainMenu.Opening += (_, _) => importSvg.Enabled = CanImportSvg();
    }

    private bool CanImportSvg()
    {
        var layer = _scene.ActiveLayer;
        return _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            && !DrawingToolsBlocked()
            && ActiveDrawingObject() is not null
            && (uint)layer < _scene.LayerCount
            && _scene.GetLayerKind(layer) == DrawingLayerKind.Drawing;
    }

    private void ImportSvgFromDialog()
    {
        if (!CanImportSvg()) return;

        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Import SVG"),
            Filter = $"{UiLocalization.T("SVG files")} (*.svg)|*.svg|{UiLocalization.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ImportSvgFile(dialog.FileName, center: null);
    }

    private void ImportSvgFile(string fileName, PointF? center)
    {
        if (!CanImportSvg()) return;
        Cursor = Cursors.WaitCursor;
        VectorSceneSnapshot? snapshot = null;
        try
        {
            // Fully parse and validate before auto-keyframing or otherwise mutating the scene.
            var imported = ImportedSvgRasterizer.Load(fileName);
            var validationScale = Math.Min(
                1f,
                512f / Math.Max(imported.IntrinsicSize.Width, imported.IntrinsicSize.Height));
            _ = ImportedSvgRasterizer.Rasterize(
                imported.Source,
                Math.Max(1, imported.IntrinsicSize.Width * validationScale),
                Math.Max(1, imported.IntrinsicSize.Height * validationScale));
            if (!CanImportSvg()) return;

            var viewport = _stage.VisibleWorldBounds();
            var placement = center ?? new PointF(
                viewport.Left + viewport.Width * 0.5f,
                viewport.Top + viewport.Height * 0.5f);
            var size = ImportedSvgInitialSize(imported.IntrinsicSize, viewport);
            snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var objectIndex = _scene.AddImportedSvgObject(_scene.ActiveLayer, placement, size, imported.Source);
            PushUndoSnapshot(snapshot);
            SetSelection(objectIndex);
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            AppLog.Info($"Imported SVG as one opaque drawing object: {fileName}");
        }
        catch (Exception exception)
        {
            if (snapshot is not null) RestoreCanvasMutationSnapshot(snapshot);
            AppLog.Error($"Unable to import SVG: {fileName}", exception);
            ModernMessageDialog.Show(
                this,
                $"{UiLocalization.T("The SVG could not be imported.")}\r\n\r\n{exception.Message}",
                UiLocalization.T("SVG Import"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private static SizeF ImportedSvgInitialSize(SizeF intrinsicSize, RectangleF viewport)
    {
        var naturalWidth = (double)intrinsicSize.Width * VectorUnits.UnitsPerPixel;
        var naturalHeight = (double)intrinsicSize.Height * VectorUnits.UnitsPerPixel;
        var maximumWidth = Math.Max(1d, viewport.Width * 0.7d);
        var maximumHeight = Math.Max(1d, viewport.Height * 0.7d);
        var scale = Math.Min(1d, Math.Min(maximumWidth / naturalWidth, maximumHeight / naturalHeight));
        return new SizeF(
            Math.Max(1f, (float)Math.Min(maximumWidth, naturalWidth * scale)),
            Math.Max(1f, (float)Math.Min(maximumHeight, naturalHeight * scale)));
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(
            _applicationSettings.ToolShortcutPreset,
            _applicationSettings.Language);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var settings = _applicationSettings with
        {
            ToolShortcutPreset = dialog.SelectedPreset,
            Language = dialog.SelectedLanguage
        };
        if (!ApplicationSettingsStore.TrySave(settings))
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("The settings could not be saved. See the latest log file for details."),
                UiLocalization.T("Settings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _applicationSettings = settings;
        UiLocalization.SetLanguage(settings.Language);
        AppLog.Info($"Tool shortcut preset changed to {_applicationSettings.ToolShortcutPreset}.");
    }

    private void PersistTimelineFrameWidth()
    {
        var width = _timeline.FrameWidth;
        if (_applicationSettings.TimelineFrameWidth == width) return;
        _applicationSettings = _applicationSettings with { TimelineFrameWidth = width };
        if (!ApplicationSettingsStore.TrySave(_applicationSettings))
        {
            AppLog.Warn("The timeline frame width could not be saved; the current session will keep the selected width.");
        }
    }

    private void PersistTimelineFrameHeight()
    {
        var preset = _timeline.FrameHeightPreset;
        if (_applicationSettings.TimelineFrameHeight == preset) return;
        _applicationSettings = _applicationSettings with { TimelineFrameHeight = preset };
        if (!ApplicationSettingsStore.TrySave(_applicationSettings))
        {
            AppLog.Warn("The timeline frame height could not be saved; the current session will keep the selected height.");
        }
    }

    private void PersistTimelineAutoKeyframe()
    {
        var enabled = _timeline.AutoKeyframeEnabled;
        if (_applicationSettings.TimelineAutoKeyframeEnabled == enabled) return;
        _applicationSettings = _applicationSettings with { TimelineAutoKeyframeEnabled = enabled };
        if (!ApplicationSettingsStore.TrySave(_applicationSettings))
        {
            AppLog.Warn("The timeline auto-keyframe setting could not be saved; the current session will keep the selected value.");
        }
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

    private static void PositionTopPlaybackFpsControls(
        Control top,
        Control restart,
        Control label,
        Control input)
    {
        var controlTop = Math.Max(0, (top.ClientSize.Height - input.Height) / 2);
        input.Left = restart.Left - input.Width - 14;
        input.Top = controlTop;
        label.Left = input.Left - label.Width - 4;
        label.Top = controlTop;
    }

    private void ToggleInspectorPanel()
    {
        _inspectorPanelOpen = !_inspectorPanelOpen;
        BeginWorkspacePanelAnimation();
    }

    private void ToggleTimelinePanel()
    {
        if (_timelinePanelOpen
            && !_workspacePanelAnimationTimer.Enabled
            && _timeline.Visible
            && _timeline.Height > 0)
        {
            _timelinePanelExpandedHeight = _timeline.Height;
        }
        _timelinePanelOpen = !_timelinePanelOpen;
        BeginWorkspacePanelAnimation();
    }

    private void BeginWorkspacePanelAnimation()
    {
        if (_inspectorPanelOpen) _inspectorHost.Visible = true;
        if (_timelinePanelOpen) _timeline.Visible = true;
        _inspectorPanelAnimationFromWidth = _inspectorHost.Width;
        _inspectorPanelAnimationToWidth = _inspectorPanelOpen ? InspectorPanelExpandedWidth : 0;
        _timelinePanelAnimationFromHeight = _timeline.Height;
        _timelinePanelAnimationToHeight = _timelinePanelOpen
            ? Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelExpandedHeight)
            : 0;
        if (_timelinePanelAnimationFromHeight != _timelinePanelAnimationToHeight)
        {
            _timeline.MinimumSize = new Size(_timelinePanelMinimumSize.Width, 0);
        }
        _workspacePanelAnimationStartedTimestamp = Stopwatch.GetTimestamp();
        var inspectorDistance = Math.Abs(
            _inspectorPanelAnimationToWidth - _inspectorPanelAnimationFromWidth)
            / (double)InspectorPanelExpandedWidth;
        var timelineDistance = Math.Abs(
            _timelinePanelAnimationToHeight - _timelinePanelAnimationFromHeight)
            / (double)Math.Max(1, _timelinePanelExpandedHeight);
        var distance = Math.Max(inspectorDistance, timelineDistance);
        _workspacePanelAnimationDurationMilliseconds = Math.Max(
            60,
            WorkspacePanelAnimationMilliseconds * distance);
        UpdateWorkspacePanelButtonStyles();
        if (distance <= 0.0001)
        {
            CompleteWorkspacePanelAnimation();
            return;
        }

        _workspacePanelAnimationTimer.Start();
    }

    private void TickWorkspacePanelAnimation()
    {
        var elapsed = Stopwatch.GetElapsedTime(_workspacePanelAnimationStartedTimestamp).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / _workspacePanelAnimationDurationMilliseconds, 0, 1);
        var inspectorWidth = ResolveWorkspacePanelAnimationValue(
            _inspectorPanelAnimationFromWidth,
            _inspectorPanelAnimationToWidth,
            progress);
        var timelineHeight = ResolveWorkspacePanelAnimationValue(
            _timelinePanelAnimationFromHeight,
            _timelinePanelAnimationToHeight,
            progress);
        var body = _inspectorHost.Parent;
        SuspendLayout();
        body?.SuspendLayout();
        try
        {
            if (_inspectorHost.Width != inspectorWidth) _inspectorHost.Width = inspectorWidth;
            if (_timeline.Height != timelineHeight) _timeline.Height = timelineHeight;
        }
        finally
        {
            body?.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
        }

        if (progress >= 1) CompleteWorkspacePanelAnimation();
    }

    internal static int ResolveWorkspacePanelAnimationValue(int from, int to, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        return (int)Math.Round(from + (to - from) * eased);
    }

    private void CompleteWorkspacePanelAnimation()
    {
        _workspacePanelAnimationTimer.Stop();
        _inspectorHost.Width = _inspectorPanelOpen ? InspectorPanelExpandedWidth : 0;
        _timeline.Height = _timelinePanelOpen
            ? Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelExpandedHeight)
            : 0;
        if (_timelinePanelOpen) _timeline.MinimumSize = _timelinePanelMinimumSize;
        _inspectorHost.Visible = _inspectorPanelOpen;
        _timeline.Visible = _timelinePanelOpen;
        UpdateWorkspacePanelButtonStyles();
        _stage.Invalidate();
    }

    private void UpdateWorkspacePanelButtonStyles()
    {
        if (_propertiesPanelButton is not null)
        {
            if (_inspectorPanelOpen) Theme.StyleActiveButton(_propertiesPanelButton);
            else Theme.StyleButton(_propertiesPanelButton);
        }
        if (_timelinePanelButton is not null)
        {
            if (_timelinePanelOpen) Theme.StyleActiveButton(_timelinePanelButton);
            else Theme.StyleButton(_timelinePanelButton);
        }
    }

    private void ToggleVaultDrawer()
    {
        if (_vaultDrawer is null) return;
        _vaultDrawerOpen = !_vaultDrawerOpen;
        if (_vaultDrawerOpen)
        {
            if (!_vaultDrawer.Visible) _vaultDrawerVisibleWidth = 0;
            ApplyVaultDrawerClip();
            _libraryVaultPanel.RefreshProjectObjects();
            _vaultDrawer.Visible = true;
            _vaultDrawer.BringToFront();
            if (_vaultButton is not null) Theme.StyleActiveButton(_vaultButton);
        }
        else if (_vaultButton is not null)
        {
            Theme.StyleButton(_vaultButton);
        }

        _vaultDrawerAnimationFromWidth = _vaultDrawerVisibleWidth;
        _vaultDrawerAnimationToWidth = _vaultDrawerOpen ? VaultDrawerExpandedWidth : 0;
        _vaultDrawerAnimationStartedTimestamp = Stopwatch.GetTimestamp();
        var distance = Math.Abs(_vaultDrawerAnimationToWidth - _vaultDrawerAnimationFromWidth);
        _vaultDrawerAnimationDurationMilliseconds = Math.Max(
            48,
            VaultDrawerAnimationMilliseconds * distance / VaultDrawerExpandedWidth);
        if (distance == 0)
        {
            CompleteVaultDrawerAnimation();
            return;
        }

        _vaultDrawerTimer.Start();
    }

    private void TickVaultDrawer()
    {
        if (_vaultDrawer is null)
        {
            _vaultDrawerTimer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_vaultDrawerAnimationStartedTimestamp).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / _vaultDrawerAnimationDurationMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var desiredVisibleWidth = (int)Math.Round(
            _vaultDrawerAnimationFromWidth
            + (_vaultDrawerAnimationToWidth - _vaultDrawerAnimationFromWidth) * eased);
        var visibleWidth = _vaultDrawerVisibleWidth + Math.Clamp(
            desiredVisibleWidth - _vaultDrawerVisibleWidth,
            -VaultDrawerMaximumPixelsPerTick,
            VaultDrawerMaximumPixelsPerTick);
        if (_vaultDrawerVisibleWidth != visibleWidth) _vaultDrawerVisibleWidth = visibleWidth;
        ApplyVaultDrawerClip();
        PositionVaultToolStrip();
        if (progress >= 1 && _vaultDrawerVisibleWidth == _vaultDrawerAnimationToWidth)
        {
            CompleteVaultDrawerAnimation();
        }
    }

    private void CompleteVaultDrawerAnimation()
    {
        if (_vaultDrawer is null) return;
        _vaultDrawerVisibleWidth = _vaultDrawerOpen ? VaultDrawerExpandedWidth : 0;
        ApplyVaultDrawerClip();
        PositionVaultToolStrip();
        if (!_vaultDrawerOpen)
        {
            _vaultDrawer.Visible = false;
            var previousRegion = _vaultDrawer.Region;
            _vaultDrawer.Region = null;
            previousRegion?.Dispose();
        }
        _vaultDrawerTimer.Stop();
    }

    private void ApplyVaultDrawerClip()
    {
        if (_vaultDrawer is null) return;
        var visibleWidth = Math.Clamp(_vaultDrawerVisibleWidth, 0, VaultDrawerExpandedWidth);
        Region? nextRegion = null;
        if (visibleWidth < VaultDrawerExpandedWidth)
        {
            nextRegion = new Region();
            nextRegion.MakeEmpty();
            if (visibleWidth > 0)
            {
                nextRegion.Union(new Rectangle(0, 0, visibleWidth, _vaultDrawer.ClientSize.Height));
            }
        }

        var previousRegion = _vaultDrawer.Region;
        _vaultDrawer.Region = nextRegion;
        previousRegion?.Dispose();
    }

    private void PositionVaultToolStrip()
    {
        if (_vaultButton?.Parent is not Control tools) return;
        var visibleWidth = _vaultDrawer is { Visible: true }
            ? Math.Clamp(_vaultDrawerVisibleWidth, 0, VaultDrawerExpandedWidth)
            : 0;
        var left = visibleWidth + 8;
        if (tools.Left != left) tools.Left = left;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_restartRequested && e.CloseReason == CloseReason.UserClosing && !TryContinueAfterUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PersistTimelineFrameWidth();
            PersistTimelineFrameHeight();
            _shapeFlyoutHideTimer.Dispose();
            _lineFlyoutHideTimer.Dispose();
            _selectionToolGroup.HideTimer.Dispose();
            _paintToolGroup.HideTimer.Dispose();
            _vaultDrawerTimer.Dispose();
            _workspacePanelAnimationTimer.Dispose();
            _lineDragPreviewTimer.Dispose();
            _timer.Dispose();
            _stageContextMenu.Dispose();
            _mainMenu.Dispose();
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
        _drawingObjectInstancePanel.Dock = DockStyle.Top;
        _drawingObjectInstancePanel.Height = 248;
        _drawingObjectInstancePanel.Visible = false;
        _brushTipPanel.Dock = DockStyle.Top;
        _brushTipPanel.Height = _brushTipPanel.PreferredHeight;
        _brushTipPanel.Visible = false;
        _drawSettingsPanel.Dock = DockStyle.Top;
        _drawSettingsPanel.Height = _drawSettingsPanel.PreferredHeight;
        _basicInspectorPage.Content.Controls.Add(_materialEditor);
        _basicInspectorPage.Content.Controls.Add(_drawingObjectInstancePanel);
        _basicInspectorPage.Content.Controls.Add(_brushTipPanel);
        _basicInspectorPage.Content.Controls.Add(_drawSettingsPanel);
        _basicInspectorPage.Content.Controls.Add(_objectInspector);
        _objectInspector.Visible = false;
        _drawSettingsPanel.Visible = false;
        _drawingObjectInstancePanel.BringToFront();
        _materialEditor.BringToFront();

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

    private bool DrawingToolsBlocked()
    {
        return IsSceneCompositionContext()
            || _scene.IsLayerEffectivelyLocked(_scene.ActiveLayer);
    }

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
        var depth = e.Dimension == SceneDimension.TwoD && e.Projection == CameraProjection.Perspective
            ? Math.Max(scene.Camera.Depth, 1000)
            : scene.Camera.Depth;
        if (scene.Dimension == e.Dimension
            && scene.Camera.Projection == e.Projection
            && scene.Camera.Depth.Equals(depth))
        {
            return;
        }
        scene.Dimension = e.Dimension;
        scene.Camera.Projection = e.Projection;
        scene.Camera.Depth = depth;
        MarkProjectDirty();

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

    private void CreateProjectAssetFolder(string parentFolderId)
    {
        if (!DrawingObjectNameDialog.TryAsk(
                this,
                "New Folder",
                "Folder name",
                "Folder",
                out var name)
            || !_project.TryAddAssetFolder(name, parentFolderId, out var folder)
            || folder is null)
        {
            return;
        }

        _libraryVaultPanel.SelectAssetFolder(folder.Id);
        AppLog.Info($"Created project asset folder: {folder.Name}");
    }

    private void RenameProjectAssetFolder(string folderId)
    {
        var folder = _project.AssetFolders.FirstOrDefault(item =>
            string.Equals(item.Id, folderId, StringComparison.Ordinal));
        if (folder is null
            || !DrawingObjectNameDialog.TryAsk(
                this,
                "Rename Folder",
                "Folder name",
                folder.Name,
                out var name)
            || !_project.TryRenameAssetFolder(folder.Id, name))
        {
            return;
        }

        _libraryVaultPanel.SelectAssetFolder(folder.Id);
        AppLog.Info($"Renamed project asset folder: {folder.Name}");
    }

    private void DuplicateProjectAssetFolder(string folderId)
    {
        if (!_project.TryDuplicateAssetFolder(folderId, out var duplicate) || duplicate is null) return;

        _libraryVaultPanel.SelectAssetFolder(duplicate.Id);
        AppLog.Info($"Duplicated project asset folder: {duplicate.Name}");
    }

    private void MoveProjectAssetFolder(string folderId, string targetFolderId)
    {
        if (!_project.TryMoveAssetFolder(folderId, targetFolderId)) return;
        _libraryVaultPanel.SelectAssetFolder(folderId);
    }

    private void MoveDrawingObjectToAssetFolder(string drawingObjectId, string targetFolderId)
    {
        _project.TryMoveDrawingObjectToAssetFolder(drawingObjectId, targetFolderId);
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
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("A project must retain at least one drawing object."),
                UiLocalization.T("Delete Drawing Object"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var drawingObject = _drawingObjects[index];
        var sceneInstanceCount = _scenes.Sum(scene => scene.Instances.Count(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal)));
        var nestedInstanceCount = _drawingObjects.Sum(container => container.Instances.Count(instance => string.Equals(instance.DrawingObjectId, drawingObjectId, StringComparison.Ordinal)));
        var referenceNotice = sceneInstanceCount + nestedInstanceCount > 0
            ? UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
                ? $"\r\n\r\n同时会移除引用它的 {sceneInstanceCount} 个场景实例和 {nestedInstanceCount} 个嵌套实例。"
                : $"\r\n\r\nThis will also remove {sceneInstanceCount} scene instance(s) and {nestedInstanceCount} nested instance(s) that reference it."
            : string.Empty;
        var deletePrompt = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
            ? $"删除绘制对象“{drawingObject.Name}”？{referenceNotice}"
            : $"Delete drawing object \"{drawingObject.Name}\"?{referenceNotice}";
        if (ModernMessageDialog.Show(
                this,
                deletePrompt,
                UiLocalization.T("Delete Drawing Object"),
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
        CancelTraditionalPenPath();
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
        ApplyProjectPlaybackSettingsToCurrentContext();
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
        CancelTraditionalPenPath();
        CancelPenCurve();
        if (!ReferenceEquals(_scene, _sceneEditStage)) ResetUndoHistory();
        _scene = _sceneEditStage;
        var sceneDefinition = ActiveScene();
        _sceneCompositionResult = SceneCompositionBuilder.Build(
            _sceneEditStage,
            sceneDefinition,
            _drawingObjects,
            _frame,
            _playbackSettings.Fps);
        _stage.BindScene(_scene);
        _stage.ConfigureReferenceView(sceneDefinition);
        if (sceneDefinition is not null) _timeline.BindSceneDefinition(sceneDefinition);
        else _timeline.BindScene(_scene);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _libraryVaultPanel.SetActiveDrawingObject(ActiveDrawingObject()?.Id);
        ApplyProjectPlaybackSettingsToCurrentContext();
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
        _sceneCompositionResult = SceneCompositionBuilder.Build(
            _sceneEditStage,
            ActiveScene(),
            _drawingObjects,
            _frame,
            _playbackSettings.Fps);
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

    private void RebuildEditableInstanceComposition()
    {
        if (IsSceneCompositionContext()) RebuildSceneComposition();
        else RebuildDrawingObjectUnderlay();
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
            _frame,
            _playbackSettings.Fps);
        _sceneInstancePreviewDirty = false;
        RefreshSelectedSceneInstanceObjectIndices();
        _stage.BindUnderlayScene(_drawingObjectUnderlayStage);
        RebuildOnionSkinPreview();
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
    }

    private void RebuildOnionSkinPreview()
    {
        if (_playing || IsSceneCompositionContext() || !_scene.HasOnionSkinPreviewEnabled)
        {
            if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
            return;
        }

        _scene.BuildOnionSkinPreview(_localOnionSkinStage, _frame);
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            _nestedOnionSkinStage,
            ActiveDrawingObject(),
            _drawingObjects,
            _frame,
            _playbackSettings.Fps);
        if (_localOnionSkinStage.ObjectCount == 0 && _nestedOnionSkinStage.ObjectCount == 0)
        {
            if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
        }
        else if (_localOnionSkinStage.ObjectCount == 0)
        {
            _stage.BindOnionSkinScene(_nestedOnionSkinStage);
        }
        else if (_nestedOnionSkinStage.ObjectCount == 0)
        {
            _stage.BindOnionSkinScene(_localOnionSkinStage);
        }
        else
        {
            _onionSkinStage.CombineOnionSkinPreviews([
                (_localOnionSkinStage, 1f, null),
                (_nestedOnionSkinStage, 1f, null)
            ]);
            _stage.BindOnionSkinScene(_onionSkinStage);
        }
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

    private void AddToolPairGroup(FlowLayoutPanel panel, Control overlayParent, ToolPairGroup group)
    {
        var parent = new SvgIconButton(ToolIconKind(group.ActiveTool))
        {
            Margin = new Padding(0, 0, 0, 8),
            Tag = group.ActiveTool,
            AccessibleName = ToolPairName(group.ActiveTool),
            ShowsToolGroupIndicator = true
        };
        group.ParentButton = parent;
        Theme.StyleButton(parent);
        parent.MouseEnter += (_, _) =>
        {
            _toolTip.HideTip();
            ShowToolPairFlyout(group);
        };
        parent.MouseLeave += (_, _) => ScheduleToolPairFlyoutHideAfter(group, 350);
        parent.Click += (_, _) =>
        {
            ActivateTool(group.ActiveTool);
            ShowToolPairFlyout(group);
        };
        panel.Controls.Add(parent);

        var flyout = new FlowLayoutPanel
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
        group.Flyout = flyout;
        PaintFullBorder(flyout);
        flyout.MouseEnter += (_, _) => ShowToolPairFlyout(group);
        flyout.MouseLeave += (_, _) => ScheduleToolPairFlyoutHideAfter(group, 350);
        overlayParent.Controls.Add(flyout);

        foreach (var tool in group.Tools)
        {
            var button = new SvgIconButton(ToolIconKind(tool))
            {
                Margin = new Padding(0, 0, 0, tool == group.Tools[^1] ? 0 : 8),
                Tag = tool,
                AccessibleName = ToolPairName(tool)
            };
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, ToolPairName(tool));
                ShowToolPairFlyout(group);
            };
            button.MouseLeave += (_, _) =>
            {
                _toolTip.HideTip();
                ScheduleToolPairFlyoutHideAfter(group, 350);
            };
            button.Click += (_, _) =>
            {
                ActivateTool(tool);
                HideToolPairFlyout(group);
            };
            group.FlyoutButtons[tool] = button;
            flyout.Controls.Add(button);
        }
    }

    private void ShowToolPairFlyout(ToolPairGroup group)
    {
        if (group.ParentButton is null || group.Flyout is null || group.ParentButton.Parent is not Control tools) return;
        HideShapeToolFlyout();
        HideLineToolFlyout();
        HideBrushToolFlyout();
        if (!ReferenceEquals(group, _selectionToolGroup)) HideToolPairFlyout(_selectionToolGroup);
        if (!ReferenceEquals(group, _paintToolGroup)) HideToolPairFlyout(_paintToolGroup);
        group.HideAtUtc = default;
        tools.PerformLayout();
        group.Flyout.Left = tools.Left + tools.Width - 1;
        group.Flyout.Top = tools.Top + group.ParentButton.Top;
        group.Flyout.Visible = true;
        group.Flyout.BringToFront();
        StartToolPairFlyoutVisibilityCheck(group);
        RefreshToolButtons();
    }

    private void HideToolPairFlyout(ToolPairGroup group)
    {
        group.HideTimer.Stop();
        group.HideAtUtc = default;
        if (group.Flyout is not null) group.Flyout.Visible = false;
    }

    private void ScheduleToolPairFlyoutHideAfter(ToolPairGroup group, int delayMilliseconds)
    {
        if (group.Flyout is null) return;
        group.HideAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, delayMilliseconds));
        StartToolPairFlyoutVisibilityCheck(group);
    }

    private void StartToolPairFlyoutVisibilityCheck(ToolPairGroup group)
    {
        if (group.Flyout is null || !group.Flyout.Visible) return;
        if (!group.HideTimer.Enabled) group.HideTimer.Start();
    }

    private void UpdateToolPairFlyoutVisibility(ToolPairGroup group)
    {
        if (group.ParentButton is null || group.Flyout is null || !group.Flyout.Visible)
        {
            group.HideTimer.Stop();
            return;
        }

        if (group.HideAtUtc != default && DateTime.UtcNow >= group.HideAtUtc)
        {
            HideToolPairFlyout(group);
            return;
        }

        if (group.HideAtUtc != default) return;
        var keepOpen = Rectangle.Union(
            group.ParentButton.RectangleToScreen(group.ParentButton.ClientRectangle),
            group.Flyout.RectangleToScreen(group.Flyout.ClientRectangle));
        keepOpen.Inflate(8, 8);
        if (!keepOpen.Contains(Cursor.Position)) HideToolPairFlyout(group);
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
        HideToolPairFlyout(_selectionToolGroup);
        HideToolPairFlyout(_paintToolGroup);
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
        HideToolPairFlyout(_selectionToolGroup);
        HideToolPairFlyout(_paintToolGroup);
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
        HideToolPairFlyout(_selectionToolGroup);
        HideToolPairFlyout(_paintToolGroup);
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
        HideToolPairFlyout(_selectionToolGroup);
        HideToolPairFlyout(_paintToolGroup);
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
        if (_marqueeSelecting || _marqueeSelectionCancellationPending) FinishLostPointerCapture();
        CancelTemporaryCanvasPan();
        CancelGradientPointer(restore: true);
        HideBrushColorPalette();
        _tool = tool;
        _stage.ClearDrawingPreview();
        if (tool != ToolMode.SimplePen) CancelPenCurve();
        if (tool != ToolMode.Pen) CancelTraditionalPenPath();
        CancelFreehandStroke();
        if (!IsShapeTool(tool)) HideShapeToolFlyout();
        if (!IsLineTool(tool)) HideLineToolFlyout();
        if (!IsBrushTool(tool)) HideBrushToolFlyout();
        if (!_selectionToolGroup.Contains(tool)) HideToolPairFlyout(_selectionToolGroup);
        if (!_paintToolGroup.Contains(tool)) HideToolPairFlyout(_paintToolGroup);
        if (_selectionToolGroup.Contains(tool)) _selectionToolGroup.ActiveTool = tool;
        else if (_paintToolGroup.Contains(tool)) _paintToolGroup.ActiveTool = tool;
        else if (IsShapeTool(tool)) _activeShapeTool = tool;
        else if (IsLineTool(tool)) _activeLineTool = tool;
        else if (IsBrushTool(tool)) _activeBrushTool = tool;
        if (_selectedObject < 0 && UsesStrokeGradient(tool)) _materialEditor.SetGradientPreviewTarget(strokeTarget: true);
        ApplyToolStrokeWidth(tool);
        if (IsBrushTool(tool) || tool == ToolMode.Eraser) SyncBrushTipSettings();
        if (ToolShapeKind(tool) is { } shape)
        {
            _drawSettings.ShapeKind = shape;
            _drawSettings.NotifyChanged();
        }

        RefreshToolButtons();
        _drawSettingsPanel.SetEraserOptionsVisible(_tool == ToolMode.Eraser);
        _drawSettingsPanel.SetShapeDetailToolsVisible(IsShapeTool(_tool));
        _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
        UpdateTransformOverlay();
        UpdateGradientOverlay();
        UpdateTraditionalPenPathHandleOverlay();
        _stage.Focus();
        RefreshInteractionCursorAtPointer();
    }

    private void ImportBrushTip()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = $"{UiLocalization.T("Brush tip images")}|*.png;*.bmp;*.jpg;*.jpeg;*.gif|{UiLocalization.T("All files")}|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = UiLocalization.T("Import 128x128 Brush Tip")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!BrushShape.TryLoad(dialog.FileName, out var brushShape, out var error) || brushShape is null)
        {
            ModernMessageDialog.Show(this, UiLocalization.T(error), UiLocalization.T("Brush Tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void UpdateTraditionalBrushSettings()
    {
        if (!_brushShape.IsTraditionalBrush) return;
        _brushShape = BrushShape.CreateTraditionalBrush(
            _brushTipPanel.TraditionalTipKind,
            _brushTipPanel.TraditionalWidthPercent,
            _brushTipPanel.TraditionalDirectionDegrees);
        _brushTipPanel.SetBrushShape(_brushShape);
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateBrushTipPanelHeight()
    {
        var height = _brushTipPanel.PreferredHeight;
        if (_brushTipPanel.Height == height) return;
        _brushTipPanel.Height = height;
    }

    private void UpdateDrawSettingsPanelHeight()
    {
        var height = _drawSettingsPanel.PreferredHeight;
        if (_drawSettingsPanel.Height == height) return;
        _drawSettingsPanel.Height = height;
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
        if (ApplyTemporaryCanvasPanCursor()) return;
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

        if (_tool is ToolMode.InkBottle or ToolMode.Eyedropper or ToolMode.Gradient)
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
        if (ApplyTemporaryCanvasPanCursor()) return;
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

        if (_tool == ToolMode.Eyedropper)
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

        if (!IsScene3DView() && _tool == ToolMode.Select && ActiveEditableInstances().Count > 0)
        {
            var world = _stage.ScreenToWorld(screen);
            var localHit = IsSceneCompositionContext()
                ? DrawingElementHit.None
                : _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
            var localObjectHasPriority = localHit.IsValid
                && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame);
            _stage.Cursor = !localObjectHasPriority
                && (TryResolveSceneInstanceAt(world, out _) || IsPointerInsideSelectedSceneInstance(world))
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
            EditHandleKind.LineStart or EditHandleKind.LineEnd or EditHandleKind.BezierControl or EditHandleKind.BezierControl2 => Cursors.Cross,
            _ => Cursors.Default
        };
    }

    private void HookEvents()
    {
        _workspaceTabs.SelectedViewChanged += (_, e) => ShowWorkspace(e.SelectedView);
        _workspaceTabs.WorldGridOpacityChanged += (_, _) => _stage.WorldGridOpacity = _workspaceTabs.WorldGridOpacity / 100f;
        _workspaceTabs.WorldGridTypeChanged += (_, _) => _stage.WorldGridType = _workspaceTabs.WorldGridType;
        _brushTipPanel.ImportRequested += (_, _) => ImportBrushTip();
        _brushTipPanel.SoftRoundRequested += (_, _) => RestoreDefaultBrushTip();
        _brushTipPanel.TraditionalBrushRequested += (_, _) => SelectTraditionalBrushTip();
        _brushTipPanel.FrequencyChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.ContinuousChanged += (_, _) => UpdateBrushStrokeSettings();
        _brushTipPanel.HardnessChanged += (_, _) => UpdateDefaultBrushHardness();
        _brushTipPanel.TraditionalSettingsChanged += (_, _) => UpdateTraditionalBrushSettings();
        _brushTipPanel.PreferredHeightChanged += (_, _) => UpdateBrushTipPanelHeight();
        _drawSettingsPanel.PreferredHeightChanged += (_, _) => UpdateDrawSettingsPanelHeight();
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
            MarkProjectDirty();
            if (_lastDrawingToolsBlocked != DrawingToolsBlocked()) RefreshToolButtons();
            UpdateInspectorForTimelineLayerChange();
            _stage.Invalidate();
        };
        _timeline.LayerVisibilityChanged += (_, _) =>
        {
            MarkProjectDirty();
            if (IsSceneCompositionContext()) RebuildSceneComposition();
            else RebuildDrawingObjectUnderlay();
            ClearInactiveSelection();
            UpdateInspector();
            _stage.Invalidate();
        };
        _timeline.AddLayerRequested += (_, _) => AddTimelineLayer();
        _timeline.AddFolderLayerRequested += (_, _) => AddTimelineFolderLayer();
        _timeline.AddMaskLayerRequested += (_, _) => AddTimelineMaskLayer();
        _timeline.FrameWidthCommitted += (_, _) => PersistTimelineFrameWidth();
        _timeline.FrameHeightCommitted += (_, _) => PersistTimelineFrameHeight();
        _timeline.AutoKeyframeChanged += (_, _) => PersistTimelineAutoKeyframe();
        _timeline.CommandRequested += (_, e) => HandleTimelineCommand(e.Command, e.Cells);
        _timeline.LayerMoveRequested += (_, e) => MoveTimelineLayer(e.TrackId, e.TargetTrackId, e.Placement);
        _timeline.RemoveLayersRequested += (_, _) => RemoveTimelineLayers();
        _timeline.LayerRenameRequested += (_, _) => RenameTimelineLayer();
        _timeline.LayerColorRequested += (_, _) => ChooseTimelineLayerColor();
        _timeline.LayerLockRequested += (_, _) => ToggleTimelineLayerLock();
        _timeline.LayerOnionSkinRequested += (_, _) => ToggleTimelineLayerOnionSkin();
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
        _libraryVaultPanel.AssetFolderCreateRequested += (_, e) => CreateProjectAssetFolder(e.ParentFolderId);
        _libraryVaultPanel.AssetFolderRenameRequested += (_, e) => RenameProjectAssetFolder(e.FolderId);
        _libraryVaultPanel.AssetFolderDuplicateRequested += (_, e) => DuplicateProjectAssetFolder(e.FolderId);
        _libraryVaultPanel.AssetFolderMoveRequested += (_, e) => MoveProjectAssetFolder(e.AssetId, e.TargetFolderId);
        _libraryVaultPanel.DrawingObjectMoveRequested += (_, e) => MoveDrawingObjectToAssetFolder(e.AssetId, e.TargetFolderId);
        _sceneEditorPanel.SceneSettingsChanged += (_, e) => UpdateSceneSettings(e);
        _topPlaybackFps.ValueChanged += (_, _) =>
        {
            if (!_updatingTopPlaybackFps) _playbackSettings.Fps = _topPlaybackFps.Value;
        };
        _playbackSettings.FpsChanged += (_, _) =>
        {
            _playbackAccumulator = 0;
            SyncTopPlaybackFps();
            _timeline.PlaybackFps = _playbackSettings.Fps;
            RebuildEditableInstanceComposition();
            UpdateStatusBar();
            PersistProjectPlaybackFps();
        };
        _playbackSettings.LoopPlaybackChanged += (_, _) => PersistProjectLoopPlayback();
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
            PersistProjectPlaybackRange();
        };
        _materialEditor.ContinuousEditStarted += (_, _) => BeginMaterialContinuousEdit();
        _materialEditor.ContinuousEditCompleted += (_, _) => CompleteMaterialContinuousEdit();
        _materialEditor.ContinuousEditCanceled += (_, _) => CancelMaterialContinuousEdit();
        _materialEditor.LineEndpointStyleChanged += (_, e) => ApplyLineEndpointStyle(e.EndpointStyle, e.StartEndpoint);
        _materialEditor.GradientChanged += (_, e) => ApplyGradientToSelection(e);
        _drawingObjectInstancePanel.PlaybackSettingsChanged += (_, e) =>
            ApplyDrawingObjectPlaybackSettings(e);
        _drawingObjectInstancePanel.AnchorChanged += (_, e) =>
            ApplySelectedDrawingObjectAnchor(e.Anchor);
        _drawingObjectInstancePanel.RestoreOriginalSizeRequested += (_, _) =>
            RestoreSelectedDrawingObjectOriginalSize();
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
                        RestoreCanvasMutationSnapshot(snapshot);
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
                _drawSettingsPanel.SetShapeDetailToolsVisible(IsShapeTool(tool));
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
            if (!_stage.Capture) _stage.ClearPenAnchorGuides();
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
            CancelTemporaryCanvasPan();
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
        AssignProjectDocument(VectorProject.CreateEmpty(), "", dirty: false);
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
        SetProjectDirty(false);
        AppLog.Info("New empty project created with one empty drawing object");
    }

    private void CreateNewProjectFromCommand()
    {
        if (!TryContinueAfterUnsavedChanges()) return;
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        CreateNewProject();
    }

    private void OpenProjectFromDialog()
    {
        if (!TryContinueAfterUnsavedChanges()) return;

        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Open Project"),
            Filter = $"{UiLocalization.T("Vector 2D Project")} (*{ProjectVaultStore.ProjectExtension})|*{ProjectVaultStore.ProjectExtension}|{UiLocalization.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_projectManifestPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_projectManifestPath) ?? "";
        }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            var project = ProjectVaultStore.Load(dialog.FileName);
            StopPlayback();
            FinishPointerInteractionForFrameChange();
            LoadProjectDocument(project, Path.GetFullPath(dialog.FileName));
            AppLog.Info($"Opened project asset library: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to open project: {dialog.FileName}", ex);
            ShowProjectFileError("The project could not be opened.", ex);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private bool SaveProject()
    {
        return string.IsNullOrWhiteSpace(_projectManifestPath)
            ? SaveProjectAs()
            : SaveProjectTo(_projectManifestPath);
    }

    private bool SaveProjectAs()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Choose or create a folder for the project asset library."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = !string.IsNullOrWhiteSpace(_projectManifestPath)
                ? Path.GetDirectoryName(_projectManifestPath) ?? ""
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return false;

        var currentDirectory = string.IsNullOrWhiteSpace(_projectManifestPath)
            ? ""
            : Path.GetDirectoryName(_projectManifestPath) ?? "";
        var fileName = string.Equals(
                Path.GetFullPath(dialog.SelectedPath),
                Path.GetFullPath(string.IsNullOrWhiteSpace(currentDirectory) ? dialog.SelectedPath : currentDirectory),
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_projectManifestPath)
                ? Path.GetFileName(_projectManifestPath)
                : SafeProjectFileName(_project.Name) + ProjectVaultStore.ProjectExtension;
        return SaveProjectTo(Path.Combine(dialog.SelectedPath, fileName));
    }

    private bool SaveProjectTo(string manifestPath)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            FinishPointerInteractionForFrameChange();
            _projectManifestPath = ProjectVaultStore.Save(_project, manifestPath);
            SetProjectDirty(false);
            AppLog.Info($"Saved project asset library: {_projectManifestPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to save project: {manifestPath}", ex);
            ShowProjectFileError("The project could not be saved.", ex);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void LoadProjectDocument(VectorProject project, string manifestPath)
    {
        AssignProjectDocument(project, manifestPath, dirty: false);
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        _sceneCompositionResult = SceneCompositionResult.Empty;
        _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
        _activeSceneIndex = 0;
        _activeDrawingObjectIndex = 0;
        _frame = 0;
        _scene = _drawingObjects[0].Scene;
        ResetEditHistory();
        BuildDrawingObjectTabs();
        BindActiveDrawingObjectScene(resetView: true);
        ClearSelection();
        UpdateInspector();
        UpdateStatusBar();
        SetProjectDirty(false);
    }

    private void AssignProjectDocument(VectorProject project, string manifestPath, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project.Changed -= ProjectChanged;
        _project = project;
        _project.Changed += ProjectChanged;
        _projectManifestPath = manifestPath;
        _projectDirty = dirty;
        UpdateProjectTitle();
    }

    private void ProjectChanged(object? sender, EventArgs e) => MarkProjectDirty();

    private void MarkProjectDirty() => SetProjectDirty(true);

    private void SetProjectDirty(bool dirty)
    {
        if (_projectDirty == dirty) return;
        _projectDirty = dirty;
        UpdateProjectTitle();
    }

    private void UpdateProjectTitle()
    {
        var name = string.IsNullOrWhiteSpace(_project.Name) ? UiLocalization.T("Untitled Project") : _project.Name;
        var dirtySuffix = _projectDirty ? " *" : "";
        Text = $"{name}{dirtySuffix} - Vector 2D Animation Engine";
        if (_projectTitleLabel is not null) _projectTitleLabel.Text = name + dirtySuffix;
    }

    private bool TryContinueAfterUnsavedChanges()
    {
        if (!_projectDirty) return true;
        var result = ModernMessageDialog.Show(
            this,
            UiLocalization.T("Save changes to the current project before continuing?"),
            UiLocalization.T("Unsaved Project"),
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        return result switch
        {
            DialogResult.Yes => SaveProject(),
            DialogResult.No => true,
            _ => false
        };
    }

    private void ShowProjectFileError(string message, Exception error)
    {
        ModernMessageDialog.Show(
            this,
            $"{UiLocalization.T(message)}\r\n\r\n{error.Message}",
            UiLocalization.T("Project"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static string SafeProjectFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var normalized = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Untitled Project" : normalized;
    }

    private void RestoreRestartState(EditorRestartState state)
    {
        AppLog.Info("Restoring editor restart session");
        AssignProjectDocument(state.Project, state.ProjectManifestPath, state.ProjectDirty);
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
        _workspaceTabs.WorldGridType = state.StageView.WorldGridType;
        if (_workspaceTabs.SelectedView == state.Workspace) ShowWorkspace(state.Workspace);
        else _workspaceTabs.SelectedView = state.Workspace;
        var selected = state.SelectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .ToArray();
        if (selected.Length > 0) SetSelection(selected);
        else ClearSelection();
        SetProjectDirty(state.ProjectDirty);
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

        if (IsModuleHotReloadEnabled())
        {
            var stateSaved = EditorRestartStore.TrySave(state);
            if (stateSaved && LauncherShutdownSignal.RequestEditorRestart())
            {
                _restartRequested = true;
                AppLog.Info("Editor process restart requested; preserving the current project for the new process");
                ProcessRestartRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            EditorRestartStore.DeletePending();
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
        ProjectManifestPath = _projectManifestPath,
        ProjectDirty = _projectDirty,
        ActiveSceneIndex = _activeSceneIndex,
        ActiveDrawingObjectIndex = _activeDrawingObjectIndex,
        Workspace = _workspaceTabs.SelectedView,
        Frame = _frame,
        SelectedObjects = _selectedObjects.ToArray(),
        StageView = _stage.CaptureViewState(),
        WindowBounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds,
        WindowState = WindowState
    };

    internal EditorRestartState CaptureEditorRestartStateForRecovery() => CaptureEditorRestartState();

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
            var rates = CalculatePerformanceRateSample(
                _rendersThisSample,
                _updatesThisSample,
                metricsElapsed,
                _playing);
            _hasRenderRateSample = rates.HasRenderRate;
            _hasUpdateRateSample = rates.HasUpdateRate;
            _renderFps = rates.RenderFps;
            _updatesPerSecond = rates.UpdatesPerSecond;
            _rendersThisSample = 0;
            _updatesThisSample = 0;
            _metricsClock.Restart();
            UpdatePerformanceMetrics();
        }
    }

    internal static PerformanceRateSample CalculatePerformanceRateSample(
        int completedRenders,
        int processedUpdates,
        double elapsedSeconds,
        bool playing)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return default;
        }

        var hasRenderRate = completedRenders >= 2;
        var hasUpdateRate = playing && processedUpdates > 0;
        return new PerformanceRateSample(
            hasRenderRate,
            hasRenderRate ? completedRenders / elapsedSeconds : 0,
            hasUpdateRate,
            hasUpdateRate ? processedUpdates / elapsedSeconds : 0);
    }

    private int UpdateSimulation(int updateCount, double fixedDeltaSeconds)
    {
        if (!_playing || updateCount <= 0) return 0;

        var frameStep = PlaybackFrameStepSeconds(_playbackSettings.Fps);
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

    internal static double PlaybackFrameStepSeconds(decimal playbackFps)
    {
        return 1.0 / (double)Math.Max(1m, playbackFps);
    }

    private void TogglePlayback()
    {
        if (_playing)
        {
            StopPlayback();
            return;
        }

        _playing = true;
        if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
        _updateBatcher.Reset();
        _playbackAccumulator = 0;
        _timer.Interval = PlaybackTimerIntervalMs;
        _clock.Restart();
        _timeline.IsPlaying = true;
        ResetPerformanceRateSample();
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
        ResetPerformanceRateSample();
        RebuildOnionSkinPreview();
    }

    private void ResetPerformanceRateSample()
    {
        _metricsClock.Restart();
        _rendersThisSample = 0;
        _updatesThisSample = 0;
        _renderFps = 0;
        _updatesPerSecond = 0;
        _hasRenderRateSample = false;
        _hasUpdateRateSample = false;
        UpdatePerformanceMetrics();
    }

    private void UpdatePerformanceMetrics()
    {
        var stats = _stage.LastStats;
        SetLabelText(_fps, _hasRenderRateSample ? $"FPS {_renderFps:0}" : "FPS --");
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
        SetToolStripText(
            _renderFpsStatus,
            _hasRenderRateSample ? $"Render FPS {_renderFps:0}" : "Render FPS --");
        SetToolStripText(
            _animationFpsStatus,
            $"UPS {(_hasUpdateRateSample ? $"{_updatesPerSecond:0}" : "--")}/{TargetUps:0}  Animation FPS {FormatPlaybackFps(_playbackSettings.Fps)}");
        SetToolStripText(_zoomStatus, $"Zoom {_stage.Zoom * 100:0}%");
    }

    private static string FormatPlaybackFps(decimal playbackFps)
    {
        return playbackFps.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
    }

    private void SyncTopPlaybackFps()
    {
        if (_topPlaybackFps.Value == _playbackSettings.Fps) return;
        _updatingTopPlaybackFps = true;
        try
        {
            _topPlaybackFps.Value = _playbackSettings.Fps;
        }
        finally
        {
            _updatingTopPlaybackFps = false;
        }
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
            CancelTraditionalPenPath();
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
        if (keyData == Keys.F1)
        {
            ToggleInspectorPanel();
            return true;
        }
        if (keyData == Keys.F2)
        {
            ToggleTimelinePanel();
            return true;
        }
        if (keyData == (Keys.Control | Keys.N))
        {
            CreateNewProjectFromCommand();
            return true;
        }
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenProjectFromDialog();
            return true;
        }
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveProject();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            SaveProjectAs();
            return true;
        }
        var focusedEditor = ContainsFocusedEditor(this);
        var interactiveControlFocused = ContainsFocusedInteractiveControl(this);
        var commandButtonFocused = ContainsFocusedButton(this);
        var canvasShortcutsEnabled = _materialEditSession is null
            && !focusedEditor
            && (!interactiveControlFocused || commandButtonFocused);
        var stageDeleteEnabled = _materialEditSession is null && !ContainsFocusedTextEditor(this);
        if (keyData == Keys.Space && TryHoldTemporaryCanvasPan()) return true;
        if (keyData == Keys.Escape && (_spacePanHeld || _spacePanPointerActive))
        {
            CancelTemporaryCanvasPan();
            return true;
        }
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
            if (keyData == Keys.Escape && CancelTraditionalPenPath()) return true;
            if (keyData == Keys.Escape && CancelPenCurve()) return true;
            if (!(commandButtonFocused && keyData == Keys.Enter) && HandleTimelineShortcut(keyData)) return true;
            if (TryActivateConfiguredToolShortcut(keyData)) return true;
            if (keyData == Keys.OemOpenBrackets && AdjustFreehandWidth(increase: false)) return true;
            if (keyData == Keys.Oem6 && AdjustFreehandWidth(increase: true)) return true;
            if (!commandButtonFocused && keyData == Keys.Tab && CycleActiveToolGroup(reverse: false)) return true;
            if (!commandButtonFocused && keyData == (Keys.Shift | Keys.Tab) && CycleActiveToolGroup(reverse: true)) return true;
            if (keyData == (Keys.Control | Keys.Z) && UndoLastEdit()) return true;
            if (keyData == (Keys.Control | Keys.X) && CutSelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.C) && CopySelectedObjects()) return true;
            if (keyData == (Keys.Control | Keys.V) && PasteCopiedObjects()) return true;
            if (keyData == (Keys.Control | Keys.Up) && MoveSelectedDrawingObjectsInStack(1)) return true;
            if (keyData == (Keys.Control | Keys.Down) && MoveSelectedDrawingObjectsInStack(-1)) return true;
        }

        if (keyData == Keys.Delete && stageDeleteEnabled && DeleteSelectedObject()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            if (!TryHoldTemporaryCanvasPan()) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
        {
            if (!RefreshPenModifierPreview() && !RefreshTraditionalPenModifierPreview()) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if (RefreshActiveLineDrawingPreview())
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (!ShowBrushColorPalette()) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space)
        {
            var wasHeld = _spacePanHeld;
            _spacePanKeyDown = false;
            ReleaseTemporaryCanvasPan();
            if (!wasHeld) return;
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
        {
            if (!RefreshPenModifierPreview() && !RefreshTraditionalPenModifierPreview()) return;
            e.Handled = true;
            return;
        }
        if (e.KeyCode is not (Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)) return;
        if ((ModifierKeys & Keys.Shift) != Keys.None) return;
        if (RefreshActiveLineDrawingPreview())
        {
            e.Handled = true;
            return;
        }
        HideBrushColorPalette();
        e.Handled = true;
    }

    private bool RefreshActiveLineDrawingPreview()
    {
        if (_tool != ToolMode.Line || _startWorld is not { } start || _lastMouse is not { } pointer) return false;
        UpdateDrawingPreview(start, _stage.ScreenToWorld(pointer), ToolMode.Line);
        return true;
    }

    private bool RefreshPenModifierPreview()
    {
        if (_tool != ToolMode.SimplePen
            || _penSegmentDragging
            || IsSceneCompositionContext()
            || !_stage.IsHandleCreated)
        {
            return false;
        }
        var pointer = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(pointer)) return false;
        UpdatePenHover(pointer);
        return true;
    }

    private bool RefreshTraditionalPenModifierPreview()
    {
        if (_tool != ToolMode.Pen
            || _traditionalPenPointerDown
            || _traditionalPenAnchorEditing
            || IsSceneCompositionContext()
            || !_stage.IsHandleCreated)
        {
            return false;
        }

        var pointer = _stage.PointToClient(Cursor.Position);
        if (!_stage.ClientRectangle.Contains(pointer)) return false;
        UpdateTraditionalPenHover(pointer);
        return true;
    }

    private bool ShowBrushColorPalette()
    {
        if (_brushColorPaletteActive) return true;
        if (!IsBrushTool(_tool)
            || _freehandDrawing
            || _lastMouse is not null
            || IsSceneCompositionContext()
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
                return ExecuteTimelineEdit(
                    TimelineEditKind.InsertFrame,
                    movePlayheadToInsertedFrame: true);
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

    private bool ExecuteTimelineEdit(
        TimelineEditKind edit,
        IReadOnlyList<TimelineFrameCell>? selectedCells = null,
        bool movePlayheadToInsertedFrame = false)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var previousLastFrame = Math.Max(0, context.FrameCount - 1);
        var previousPlayheadFrame = _frame;
        var previousTimelineSelection = edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            ? _timeline.CaptureSelectionSnapshot()
            : null;
        var timeline = context.Timeline;
        var cells = (selectedCells ?? _timeline.SelectedFrameCells)
            .Where(cell => timeline.FindTrack(cell.TrackId) is not null)
            .Distinct()
            .ToArray();
        if (selectedCells is null
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            && cells.Length <= 1
            && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [ResolveTimelineKeyframeInsertionCell(
                timeline,
                _timeline.ActiveTrackId,
                _frame,
                cells.FirstOrDefault())];
        }
        if (cells.Length == 0 && !string.IsNullOrWhiteSpace(_timeline.ActiveTrackId))
        {
            cells = [new TimelineFrameCell(_timeline.ActiveTrackId, _frame)];
        }
        if (cells.Length == 0) return false;

        var preservedInstanceSelectionIds = edit == TimelineEditKind.InsertKeyframe
            ? _selectedSceneInstanceIds.ToArray()
            : [];
        var preservedPrimaryInstanceId = edit == TimelineEditKind.InsertKeyframe
            ? _selectedSceneInstanceId
            : "";
        var singleKeyframeInsertion = cells.Length == 1
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe;
        var singleKeyframeInsertionBeyondTrackEnd = singleKeyframeInsertion
            && TimelineCellIsBeyondTrackEnd(timeline, cells[0]);
        var advanceSingleKeyframeInsertion = singleKeyframeInsertion
            && TimelineKeyframeInsertionShouldAdvance(timeline, cells[0]);
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
        var refreshCurrentComposition = edit != TimelineEditKind.InsertFrame;

        if (singleKeyframeInsertion)
        {
            EnsureTimelineCellFramesExist(timeline, cells);
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
                        var rangeChanged = ApplyTimelineRangeEdit(
                            edit,
                            context,
                            timeline,
                            track,
                            drawingScene,
                            layer,
                            range.Frame,
                            range.Count);
                        changed |= rangeChanged;
                        if (rangeChanged
                            && edit == TimelineEditKind.InsertFrame
                            && TimelineInsertRequiresCompositionRefresh(_frame, range.Frame))
                        {
                            refreshCurrentComposition = true;
                        }
                    }
                    continue;
                }

                if (edit == TimelineEditKind.InsertBlankKeyframe)
                {
                    foreach (var plan in ResolveTimelineBlankKeyframeRangePlans(group.Select(cell => cell.Frame)))
                    {
                        foreach (var frame in plan.ExtensionFrames)
                        {
                            changed |= ApplyTimelineCellEdit(
                                TimelineEditKind.ClearKeyframe,
                                context,
                                timeline,
                                track,
                                drawingScene,
                                layer,
                                frame);
                        }

                        changed |= ApplyTimelineCellEdit(
                            TimelineEditKind.InsertBlankKeyframe,
                            context,
                            timeline,
                            track,
                            drawingScene,
                            layer,
                            plan.BlankFrame);
                    }
                    continue;
                }

                foreach (var frame in group.Select(cell => cell.Frame).Distinct().OrderBy(frame => frame))
                {
                    changed |= ApplyTimelineCellEdit(edit, context, timeline, track, drawingScene, layer, frame);
                }
            }
        }

        if (!changed)
        {
            if (preservedInstanceSelectionIds.Length > 0)
            {
                RestoreTimelineInstanceSelection(preservedInstanceSelectionIds, preservedPrimaryInstanceId);
                UpdateInspector();
            }
            if (advanceSingleKeyframeInsertion) PlayTimelineEditFeedback(edit, cells);
            return advanceSingleKeyframeInsertion;
        }
        var undoPlayheadFrame = edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe
            ? previousPlayheadFrame
            : (int?)null;
        if (vectorSnapshot is not null)
        {
            PushUndoSnapshot(
                vectorSnapshot,
                playheadFrame: undoPlayheadFrame,
                timelineSelection: previousTimelineSelection);
        }
        if (sceneDefinition is not null && sceneSnapshot is not null)
        {
            PushSceneTimelineUndo(
                sceneDefinition,
                sceneSnapshot,
                playheadFrame: undoPlayheadFrame,
                timelineSelection: previousTimelineSelection);
        }

        if (!advanceSingleKeyframeInsertion
            && edit is TimelineEditKind.InsertKeyframe or TimelineEditKind.InsertBlankKeyframe)
        {
            EnsureTimelineCellFramesExist(
                timeline,
                cells,
                trailingFrames: singleKeyframeInsertionBeyondTrackEnd ? 0 : 1);
        }

        var insertedFrameDestination = movePlayheadToInsertedFrame && edit == TimelineEditKind.InsertFrame
            ? ResolveTimelineInsertPlayheadFrame(timeline, cells, _frame)
            : _frame;

        CompleteTimelineMutation(
            context,
            drawingScene,
            previousLastFrame,
            refreshCurrentComposition,
            preservedInstanceSelectionIds,
            preservedPrimaryInstanceId);
        if (movePlayheadToInsertedFrame && edit == TimelineEditKind.InsertFrame)
        {
            SetFrame(insertedFrameDestination);
        }
        PlayTimelineEditFeedback(edit, cells);
        return true;
    }

    private void PlayTimelineEditFeedback(TimelineEditKind edit, IReadOnlyList<TimelineFrameCell> cells)
    {
        var command = edit switch
        {
            TimelineEditKind.InsertFrame => TimelineCommand.InsertFrames,
            TimelineEditKind.RemoveFrame => TimelineCommand.DeleteFrames,
            TimelineEditKind.InsertKeyframe => TimelineCommand.InsertKeyframes,
            TimelineEditKind.InsertBlankKeyframe => TimelineCommand.InsertBlankKeyframes,
            TimelineEditKind.ClearKeyframe => TimelineCommand.ClearKeyframes,
            _ => (TimelineCommand?)null
        };
        if (command is { } value) _timeline.PlayCommandFeedback(value, cells);
    }

    internal static TimelineFrameCell AdvanceTimelineKeyframeShortcutCell(TimelineFrameCell cell)
    {
        return cell.Frame >= int.MaxValue - 1
            ? cell
            : cell with { Frame = cell.Frame + 1 };
    }

    internal static bool TimelineKeyframeInsertionShouldAdvance(
        AnimationTimeline timeline,
        TimelineFrameCell cell)
    {
        var track = timeline.FindTrack(cell.TrackId);
        return track is not null
            && cell.Frame >= 0
            && cell.Frame < track.Duration
            && track.EvaluateExposure(cell.Frame).IsKeyframe;
    }

    internal static bool TimelineCellIsBeyondTrackEnd(AnimationTimeline timeline, TimelineFrameCell cell)
    {
        var track = timeline.FindTrack(cell.TrackId);
        return track is not null && cell.Frame >= track.Duration;
    }

    internal static TimelineFrameCell ResolveTimelineKeyframeInsertionCell(
        AnimationTimeline timeline,
        string activeTrackId,
        int playheadFrame,
        TimelineFrameCell selectedCell = default)
    {
        var playheadCell = new TimelineFrameCell(activeTrackId, Math.Max(0, playheadFrame));
        return string.Equals(selectedCell.TrackId, activeTrackId, StringComparison.Ordinal)
            && TimelineCellIsBeyondTrackEnd(timeline, selectedCell)
                ? selectedCell
                : playheadCell;
    }

    internal static bool TimelineInsertRequiresCompositionRefresh(int currentFrame, int insertionFrame)
    {
        return Math.Max(0, insertionFrame) < Math.Max(0, currentFrame);
    }

    internal static int ResolveTimelineInsertPlayheadFrame(
        AnimationTimeline timeline,
        IEnumerable<TimelineFrameCell> cells,
        int fallbackFrame)
    {
        var destination = Math.Max(0, fallbackFrame);
        var found = false;
        foreach (var group in cells
                     .Where(cell => cell.Frame >= 0)
                     .GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
        {
            var track = timeline.FindTrack(group.Key);
            if (track is null) continue;
            foreach (var range in TimelineFrameRanges(group.Select(cell => cell.Frame)))
            {
                var insertedEnd = range.Frame + range.Count - 1;
                var exposure = track.EvaluateExposure(range.Frame);
                var candidate = exposure.SourceKind is null
                    ? insertedEnd
                    : Math.Max(insertedEnd, exposure.EndFrame);
                destination = found ? Math.Max(destination, candidate) : candidate;
                found = true;
            }
        }

        return found ? destination : Math.Max(0, fallbackFrame);
    }

    internal static IReadOnlyList<TimelineBlankKeyframeRangePlan> ResolveTimelineBlankKeyframeRangePlans(
        IEnumerable<int> frames)
    {
        return TimelineFrameRanges(frames)
            .Select(range => new TimelineBlankKeyframeRangePlan(
                Enumerable.Range(range.Frame, Math.Max(0, range.Count - 1))
                    .Where(frame => frame > 0)
                    .ToArray(),
                range.Frame + range.Count - 1))
            .ToArray();
    }

    private static bool ApplyTimelineRangeEdit(
        TimelineEditKind edit,
        ITimelineContext context,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame,
        int count)
    {
        bool changed;
        if (drawingScene is not null && layer >= 0)
        {
            changed = edit == TimelineEditKind.InsertFrame
                ? drawingScene.InsertTimelineFrame(layer, frame, count)
                : drawingScene.RemoveTimelineFrame(layer, frame, count);
        }
        else
        {
            changed = edit == TimelineEditKind.InsertFrame
                ? timeline.InsertFrame(track.Id, frame, count)
                : timeline.RemoveFrame(track.Id, frame, count);
        }

        if (!changed) return false;
        UpdateInstanceStateFrames(context, track.TargetId, edit, frame, count);
        return true;
    }

    private static bool ApplyTimelineCellEdit(
        TimelineEditKind edit,
        ITimelineContext context,
        AnimationTimeline timeline,
        AnimationTimelineTrack track,
        VectorScene? drawingScene,
        int layer,
        int frame)
    {
        bool changed;
        if (drawingScene is not null && layer >= 0)
        {
            changed = edit switch
            {
                TimelineEditKind.InsertKeyframe => drawingScene.InsertTimelineKeyframe(layer, frame),
                TimelineEditKind.InsertBlankKeyframe => drawingScene.InsertTimelineBlankKeyframe(layer, frame),
                TimelineEditKind.ClearKeyframe => drawingScene.ClearTimelineKeyframe(layer, frame),
                _ => false
            };
        }

        else
        {
            changed = edit switch
            {
                TimelineEditKind.InsertKeyframe => timeline.InsertKeyframe(track.Id, frame),
                TimelineEditKind.InsertBlankKeyframe => timeline.InsertBlankKeyframe(track.Id, frame),
                TimelineEditKind.ClearKeyframe => timeline.ClearKeyframe(track.Id, frame),
                _ => false
            };
        }

        if (changed && edit is (TimelineEditKind.InsertBlankKeyframe or TimelineEditKind.ClearKeyframe))
        {
            RemoveInstanceStateKeyframes(context, track.TargetId, frame);
        }
        else if (changed && edit == TimelineEditKind.InsertKeyframe)
        {
            CaptureInstanceStateKeyframes(context, track.TargetId, frame);
        }

        return changed;
    }

    private static void UpdateInstanceStateFrames(
        ITimelineContext context,
        string layerId,
        TimelineEditKind edit,
        int frame,
        int count)
    {
        if (context is DrawingObjectDefinition drawingObject)
        {
            if (edit == TimelineEditKind.InsertFrame) drawingObject.InsertInstanceStateFrames(layerId, frame, count);
            else drawingObject.RemoveInstanceStateFrames(layerId, frame, count);
        }
        else if (context is SceneDefinition scene)
        {
            if (edit == TimelineEditKind.InsertFrame) scene.InsertInstanceStateFrames(layerId, frame, count);
            else scene.RemoveInstanceStateFrames(layerId, frame, count);
        }
    }

    private static void RemoveInstanceStateKeyframes(ITimelineContext context, string layerId, int frame)
    {
        if (context is DrawingObjectDefinition drawingObject)
        {
            drawingObject.RemoveInstanceStateKeyframes(layerId, frame);
        }
        else if (context is SceneDefinition scene)
        {
            scene.RemoveInstanceStateKeyframes(layerId, frame);
        }
    }

    private static bool CaptureInstanceStateKeyframes(ITimelineContext context, string layerId, int frame)
    {
        if (frame <= 0) return false;
        var changed = false;
        foreach (var instance in TimelineInstancesInLayer(context, layerId))
        {
            changed |= instance.SetStateAtFrame(frame, instance.EvaluateState(frame));
        }

        return changed;
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

    private void CompleteTimelineMutation(
        ITimelineContext context,
        VectorScene? drawingScene,
        int previousLastFrame,
        bool refreshCurrentComposition = true,
        IReadOnlyCollection<string>? preservedInstanceSelectionIds = null,
        string? preservedPrimaryInstanceId = null)
    {
        StopPlayback();
        ClearSelection();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousLastFrame);
        if (drawingScene is not null)
        {
            if (refreshCurrentComposition)
            {
                _hierarchyPanel.RefreshScene();
                RebuildDrawingObjectUnderlay();
            }
            else if (Array.IndexOf(drawingScene.LayerOnionSkin, true) >= 0)
            {
                RebuildOnionSkinPreview();
            }
            _stage.Invalidate();
        }
        else if (refreshCurrentComposition)
        {
            RebuildSceneComposition();
        }
        else
        {
            _stage.Invalidate();
        }

        if (preservedInstanceSelectionIds is { Count: > 0 })
        {
            RestoreTimelineInstanceSelection(preservedInstanceSelectionIds, preservedPrimaryInstanceId);
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
                    exposure.SourceKind ?? TimelineKeyframeKind.Blank,
                    exposure.HasContent
                        ? TimelineInstancesInLayer(context, track.TargetId).ToDictionary(
                            instance => instance.Id,
                            instance => instance.EvaluateState(cell.Frame),
                            StringComparer.Ordinal)
                        : new Dictionary<string, InstanceFrameState>(StringComparer.Ordinal));
            }).ToArray()
        };
        _timeline.PlayCommandFeedback(TimelineCommand.CopyFrames, cells);
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
        var pastedCells = new List<TimelineFrameCell>();

        using (timeline.BeginBatchUpdate())
        {
            foreach (var source in clipboard.Cells)
            {
                var destinationTrackIndex = anchorTrack + source.TrackOffset;
                var destinationFrame = Math.Max(0, anchorFrame + source.FrameOffset);
                if (destinationTrackIndex < 0 || destinationTrackIndex >= timeline.Tracks.Count) continue;
                var track = timeline.Tracks[destinationTrackIndex];
                pastedCells.Add(new TimelineFrameCell(track.Id, destinationFrame));
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
                    if (source.Kind == TimelineKeyframeKind.Populated && source.InstanceStates.Count > 0)
                    {
                        changed |= timeline.InsertKeyframe(track.Id, destinationFrame);
                    }
                    changed |= ApplyClipboardInstanceStates(
                        context,
                        track.TargetId,
                        destinationFrame,
                        source.Kind,
                        source.InstanceStates);
                    continue;
                }

                if (destinationFrame >= track.Duration) timeline.SetTrackDuration(track.Id, destinationFrame + 1);
                changed |= source.Kind == TimelineKeyframeKind.Populated
                    ? timeline.InsertKeyframe(track.Id, destinationFrame)
                    : timeline.InsertBlankKeyframe(track.Id, destinationFrame);
                changed |= ApplyClipboardInstanceStates(
                    context,
                    track.TargetId,
                    destinationFrame,
                    source.Kind,
                    source.InstanceStates);
            }
        }

        if (!changed) return false;
        if (vectorSnapshot is not null) PushUndoSnapshot(vectorSnapshot);
        if (sceneDefinition is not null && sceneSnapshot is not null) PushSceneTimelineUndo(sceneDefinition, sceneSnapshot);
        CompleteTimelineMutation(context, drawingScene, previousLastFrame);
        _timeline.PlayCommandFeedback(TimelineCommand.PasteFrames, pastedCells);
        return true;
    }

    private static IReadOnlyList<DrawingObjectInstanceDefinition> TimelineInstancesInLayer(
        ITimelineContext context,
        string layerId)
    {
        return context switch
        {
            DrawingObjectDefinition drawingObject => drawingObject.InstancesInLayer(layerId),
            SceneDefinition scene => scene.InstancesInLayer(layerId),
            _ => []
        };
    }

    private static bool ApplyClipboardInstanceStates(
        ITimelineContext context,
        string layerId,
        int frame,
        TimelineKeyframeKind kind,
        IReadOnlyDictionary<string, InstanceFrameState> states)
    {
        var changed = false;
        foreach (var instance in TimelineInstancesInLayer(context, layerId))
        {
            if (kind == TimelineKeyframeKind.Blank)
            {
                changed |= instance.RemoveStateKeyframe(frame);
            }
            else if (states.TryGetValue(instance.Id, out var state))
            {
                changed |= instance.SetStateAtFrame(frame, state);
            }
        }

        return changed;
    }

    private void MoveTimelineLayer(string trackId, string targetTrackId, TimelineLayerDropPlacement placement)
    {
        var context = _timeline.Context;
        context.SynchronizeTimelineTracks();
        var track = context.Timeline.FindTrack(trackId);
        var targetTrack = context.Timeline.FindTrack(targetTrackId);
        if (track is null || targetTrack is null) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var sourceLayer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetTrack.TargetId);
            if (sourceLayer < 0 || targetLayer < 0 || sourceLayer == targetLayer) return;
            var snapshot = drawingScene.CreateSnapshot();
            var changed = placement == TimelineLayerDropPlacement.Mask
                ? LinkTimelineMaskLayers(drawingScene, track.TargetId, targetTrack.TargetId)
                : MoveTimelineDrawingLayer(drawingScene, track.TargetId, targetTrack.TargetId, placement);
            if (!changed) return;
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
        var sceneTargetLayer = sceneDefinition.FindLayer(targetTrack.TargetId);
        if (sceneSourceLayer is null || sceneTargetLayer is null) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var targetIndex = sceneDefinition.Layers
            .Select((layer, index) => (layer, index))
            .First(item => string.Equals(item.layer.Id, sceneTargetLayer.Id, StringComparison.Ordinal))
            .index;
        var sceneDestinationLayer = placement == TimelineLayerDropPlacement.Before
            ? targetIndex
            : Math.Min(sceneDefinition.Layers.Count - 1, targetIndex + 1);
        if (!_project.TryMoveSceneLayer(sceneDefinition.Id, sceneSourceLayer.Id, sceneDestinationLayer)) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.RefreshTimeline();
        RebuildSceneComposition();
        UpdateInspector();
    }

    private static bool MoveTimelineDrawingLayer(
        VectorScene drawingScene,
        string sourceLayerId,
        string targetLayerId,
        TimelineLayerDropPlacement placement)
    {
        var sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0 || sourceLayer == targetLayer) return false;
        if (drawingScene.IsLayerDescendantOf(targetLayer, sourceLayer)) return false;

        var requestedParent = placement == TimelineLayerDropPlacement.Inside
            ? targetLayer
            : drawingScene.GetLayerParentIndex(targetLayer);
        if (!drawingScene.CanSetLayerParent(sourceLayer, requestedParent)) return false;

        var changed = drawingScene.SetLayerParent(sourceLayer, requestedParent);
        sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0) return changed;
        changed |= placement == TimelineLayerDropPlacement.Before
            ? drawingScene.MoveLayerBefore(sourceLayer, targetLayer)
            : drawingScene.MoveLayerAfter(sourceLayer, targetLayer);
        return changed;
    }

    private static bool LinkTimelineMaskLayers(VectorScene drawingScene, string sourceLayerId, string targetLayerId)
    {
        var sourceLayer = Array.IndexOf(drawingScene.LayerIds, sourceLayerId);
        var targetLayer = Array.IndexOf(drawingScene.LayerIds, targetLayerId);
        if (sourceLayer < 0 || targetLayer < 0) return false;

        var sourceKind = drawingScene.GetLayerKind(sourceLayer);
        var targetKind = drawingScene.GetLayerKind(targetLayer);
        var sourceIsMask = sourceKind == DrawingLayerKind.Mask && targetKind == DrawingLayerKind.Drawing;
        var targetIsMask = sourceKind == DrawingLayerKind.Drawing && targetKind == DrawingLayerKind.Mask;
        if (!sourceIsMask && !targetIsMask) return false;

        var maskLayerId = sourceIsMask ? sourceLayerId : targetLayerId;
        var contentLayerId = sourceIsMask ? targetLayerId : sourceLayerId;
        var maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        var contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return false;

        var changed = sourceIsMask
            ? drawingScene.ClearMaskLayerLinks(maskLayer)
            : drawingScene.ClearLayerMask(contentLayer);
        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return changed;

        var requestedParent = sourceIsMask
            ? drawingScene.GetLayerParentIndex(contentLayer)
            : drawingScene.GetLayerParentIndex(maskLayer);
        changed |= drawingScene.SetLayerParent(sourceIsMask ? maskLayer : contentLayer, requestedParent);

        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        if (maskLayer < 0 || contentLayer < 0) return changed;
        changed |= sourceIsMask
            ? drawingScene.MoveLayerBefore(maskLayer, contentLayer)
            : drawingScene.MoveLayerAfter(contentLayer, maskLayer);

        maskLayer = Array.IndexOf(drawingScene.LayerIds, maskLayerId);
        contentLayer = Array.IndexOf(drawingScene.LayerIds, contentLayerId);
        return maskLayer >= 0 && contentLayer >= 0 && (drawingScene.SetLayerMask(contentLayer, maskLayer) || changed);
    }

    private IReadOnlyList<string> SelectedTimelineLayerTargetIds()
    {
        var selected = _timeline.SelectedLayerTargetIds;
        if (selected.Count > 0) return selected;
        var active = _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "");
        return active is null ? [] : [active.TargetId];
    }

    private void RemoveTimelineLayers()
    {
        var context = _timeline.Context;
        var targetIds = SelectedTimelineLayerTargetIds();
        if (targetIds.Count == 0) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is not null)
        {
            var removal = drawingScene.ResolveLayerRemovalIndices(targetIds);
            if (!ConfirmTimelineLayerRemoval(removal.Length, drawingScene.LayerCount)) return;

            var sceneSnapshot = drawingScene.CreateSnapshot();
            DrawingObjectInstanceDefinition[]? instanceSnapshot = null;
            bool changed;
            if (context is DrawingObjectDefinition drawingObject)
            {
                instanceSnapshot = drawingObject.CreateInstanceSnapshot();
                changed = _project.TryRemoveDrawingObjectLayers(drawingObject.Id, targetIds);
            }
            else
            {
                changed = drawingScene.RemoveLayers(targetIds);
            }
            if (!changed) return;

            PushUndoSnapshot(sceneSnapshot, context as DrawingObjectDefinition, instanceSnapshot);
            ClearSelection();
            _timeline.RefreshTimeline();
            _timeline.SelectModelActiveTrack();
            RebuildDrawingObjectUnderlay();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneRemovalCount = targetIds
            .Distinct(StringComparer.Ordinal)
            .Count(targetId => sceneDefinition.FindLayer(targetId) is not null);
        if (!ConfirmTimelineLayerRemoval(sceneRemovalCount, sceneDefinition.Layers.Count)) return;

        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        var sceneInstanceSnapshot = sceneDefinition.CreateInstanceSnapshot();
        if (!_project.TryRemoveSceneLayers(sceneDefinition.Id, targetIds)) return;

        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot, sceneInstanceSnapshot);
        ClearSelection();
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        RebuildSceneComposition();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool ConfirmTimelineLayerRemoval(int removalCount, int layerCount)
    {
        if (removalCount <= 0) return false;
        if (removalCount >= layerCount)
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("At least one layer must remain."),
                UiLocalization.T("Delete Layers"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }
        if (removalCount == 1) return true;

        var prompt = string.Format(
            UiLocalization.T("Delete {0} layers and all of their contents?"),
            removalCount);
        return ModernMessageDialog.Show(
            this,
            prompt,
            UiLocalization.T("Delete Layers"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void ChooseTimelineLayerColor()
    {
        var context = _timeline.Context;
        var targetIds = SelectedTimelineLayerTargetIds();
        if (targetIds.Count == 0) return;

        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        var current = Color.Empty;
        var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        if (drawingScene is not null)
        {
            var layers = targetIds
                .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
                .Where(layer => layer >= 0)
                .Distinct()
                .ToArray();
            if (layers.Length == 0) return;
            var activeLayer = string.IsNullOrWhiteSpace(activeTargetId)
                ? -1
                : Array.IndexOf(drawingScene.LayerIds, activeTargetId);
            current = drawingScene.GetLayerColor(activeLayer >= 0 && layers.Contains(activeLayer) ? activeLayer : layers[0]);

            using var picker = new ColorDialog { Color = current, FullOpen = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var snapshot = drawingScene.CreateSnapshot();
            var changed = false;
            foreach (var layer in layers) changed |= drawingScene.SetLayerColor(layer, picker.Color);
            if (!changed) return;
            PushUndoSnapshot(snapshot);
            _timeline.Invalidate();
            return;
        }

        if (context is not SceneDefinition sceneDefinition) return;
        var sceneLayerIds = targetIds
            .Where(targetId => sceneDefinition.FindLayer(targetId) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sceneLayerIds.Length == 0) return;
        var activeSceneLayer = !string.IsNullOrWhiteSpace(activeTargetId)
            ? sceneDefinition.FindLayer(activeTargetId)
            : null;
        current = activeSceneLayer is not null && sceneLayerIds.Contains(activeSceneLayer.Id, StringComparer.Ordinal)
            ? Color.FromArgb(activeSceneLayer.ColorArgb)
            : Color.FromArgb(sceneDefinition.FindLayer(sceneLayerIds[0])!.ColorArgb);

        using (var picker = new ColorDialog { Color = current, FullOpen = true })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
            var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
            var changed = false;
            foreach (var layerId in sceneLayerIds)
            {
                changed |= _project.TrySetSceneLayerColor(sceneDefinition.Id, layerId, picker.Color);
            }

            if (!changed) return;
            PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        }
        _timeline.Invalidate();
    }

    private void RenameTimelineLayer()
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
        if (drawingScene is not null)
        {
            var layer = Array.IndexOf(drawingScene.LayerIds, track.TargetId);
            if (layer < 0
                || !DrawingObjectNameDialog.TryAsk(this, "Rename Layer", "Layer name", drawingScene.LayerNames[layer], out var name))
            {
                return;
            }

            var snapshot = drawingScene.CreateSnapshot();
            if (!drawingScene.RenameLayer(layer, name)) return;
            PushUndoSnapshot(snapshot);
            _timeline.Invalidate();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            return;
        }

        if (context is not SceneDefinition sceneDefinition || sceneDefinition.FindLayer(track.TargetId) is not { } sceneLayer) return;
        if (!DrawingObjectNameDialog.TryAsk(this, "Rename Layer", "Layer name", sceneLayer.Name, out var sceneName)) return;
        var timelineSnapshot = sceneDefinition.Timeline.CreateSnapshot();
        var layerSnapshot = sceneDefinition.CreateLayerSnapshot();
        if (!_project.TryRenameSceneLayer(sceneDefinition.Id, sceneLayer.Id, sceneName)) return;
        PushSceneTimelineUndo(sceneDefinition, timelineSnapshot, layerSnapshot);
        _timeline.Invalidate();
        UpdateInspector();
    }

    private void ToggleTimelineLayerLock()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;
        var layers = SelectedTimelineLayerTargetIds()
            .Select(targetId => Array.IndexOf(drawingScene.LayerIds, targetId))
            .Where(layer => layer >= 0)
            .Distinct()
            .ToArray();
        if (layers.Length == 0) return;
        var activeTargetId = context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId;
        var activeLayer = string.IsNullOrWhiteSpace(activeTargetId)
            ? -1
            : Array.IndexOf(drawingScene.LayerIds, activeTargetId);
        var lockLayers = !drawingScene.LayerLocked[layers.Contains(activeLayer) ? activeLayer : layers[0]];

        var snapshot = drawingScene.CreateSnapshot();
        var changed = false;
        foreach (var layer in layers) changed |= drawingScene.SetLayerLocked(layer, lockLayers);
        if (!changed) return;
        PushUndoSnapshot(snapshot);
        ClearInactiveSelection();
        _timeline.Invalidate();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        RefreshToolButtons();
        _stage.Invalidate();
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

    internal static void EnsureTimelineCellFramesExist(
        AnimationTimeline timeline,
        IEnumerable<TimelineFrameCell> cells,
        int trailingFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(cells);
        trailingFrames = Math.Max(0, trailingFrames);
        using var batchUpdate = timeline.BeginBatchUpdate();
        foreach (var group in cells
                     .Where(cell => cell.Frame >= 0)
                     .GroupBy(cell => cell.TrackId, StringComparer.Ordinal))
        {
            var track = timeline.FindTrack(group.Key);
            if (track is null) continue;
            var lastFrame = group.Max(cell => cell.Frame);
            var requiredDuration = lastFrame >= int.MaxValue - trailingFrames
                ? int.MaxValue
                : lastFrame + trailingFrames + 1;
            if (track.Duration < requiredDuration) timeline.SetTrackDuration(track.Id, requiredDuration);
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

    private void AddTimelineFolderLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        var snapshot = drawingScene.CreateSnapshot();
        drawingScene.AddFolderLayer();
        PushUndoSnapshot(snapshot);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void AddTimelineMaskLayer()
    {
        var context = _timeline.Context;
        var drawingScene = context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        if (drawingScene is null) return;

        var snapshot = drawingScene.CreateSnapshot();
        drawingScene.AddMaskLayer();
        PushUndoSnapshot(snapshot);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        if (context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
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

    private bool TryActivateConfiguredToolShortcut(Keys keyData)
    {
        var preset = _applicationSettings.ToolShortcutPreset;
        if (ToolShortcutMap.IsVaultShortcut(preset, keyData))
        {
            ToggleVaultDrawer();
            return true;
        }

        var tool = ToolShortcutMap.ResolveTool(
            preset,
            keyData,
            _selectionToolGroup.ActiveTool,
            _activeShapeTool,
            _activeLineTool,
            _activeBrushTool,
            _paintToolGroup.ActiveTool);
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
        MarkProjectDirty();
        _stage.ConfigureReferenceView(scene);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        UpdateStatusBar();
    }

    private bool CycleActiveToolGroup(bool reverse)
    {
        if (_selectionToolGroup.Contains(_tool)) return CycleToolPairGroup(_selectionToolGroup, reverse);
        if (_paintToolGroup.Contains(_tool)) return CycleToolPairGroup(_paintToolGroup, reverse);
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

    private bool CycleToolPairGroup(ToolPairGroup group, bool reverse)
    {
        if (DrawingToolsBlocked() && group.Tools.All(IsBasicDrawingOnlyTool)) return false;
        var next = CycleToolGroupMember(group.Tools, group.ActiveTool, reverse);
        ActivateTool(next);
        ShowToolPairFlyout(group);
        ScheduleToolPairFlyoutHideAfter(group, 2000);
        return true;
    }

    internal static ToolMode CycleToolGroupMember(IReadOnlyList<ToolMode> tools, ToolMode activeTool, bool reverse)
    {
        if (tools.Count == 0) return activeTool;
        var index = -1;
        for (var candidate = 0; candidate < tools.Count; candidate++)
        {
            if (tools[candidate] != activeTool) continue;
            index = candidate;
            break;
        }
        if (index < 0) index = 0;
        return reverse
            ? tools[(index - 1 + tools.Count) % tools.Count]
            : tools[(index + 1) % tools.Count];
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

    private void ApplyProjectPlaybackSettingsToCurrentContext()
    {
        var timelineLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var endFrame = Math.Clamp(_project.PlaybackEndFrame, 0, timelineLastFrame);
        var startFrame = Math.Clamp(_project.PlaybackStartFrame, 0, endFrame);

        _syncingProjectPlaybackSettings = true;
        try
        {
            _playbackSettings.Fps = _project.PlaybackFps;
            _playbackSettings.LoopPlayback = _project.LoopPlayback;
            _playbackSettings.SetFrameRange(startFrame, endFrame);
        }
        finally
        {
            _syncingProjectPlaybackSettings = false;
        }
    }

    private void PersistProjectPlaybackFps()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetPlaybackFps(_playbackSettings.Fps);
    }

    private void PersistProjectLoopPlayback()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetLoopPlayback(_playbackSettings.LoopPlayback);
    }

    private void PersistProjectPlaybackRange()
    {
        if (_syncingProjectPlaybackSettings) return;
        _project.TrySetPlaybackRange(_playbackSettings.StartFrame, _playbackSettings.EndFrame);
    }

    private void ApplyBoundTimelineDuration(int previousLastFrame = -1)
    {
        var lastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var followTimelineEnd = previousLastFrame >= 0 && _project.PlaybackEndFrame == previousLastFrame;
        var endFrame = followTimelineEnd
            ? lastFrame
            : Math.Clamp(_project.PlaybackEndFrame, 0, lastFrame);
        var startFrame = Math.Clamp(_project.PlaybackStartFrame, 0, endFrame);
        if (startFrame != _playbackSettings.StartFrame || endFrame != _playbackSettings.EndFrame)
        {
            if (followTimelineEnd)
            {
                _playbackSettings.SetFrameRange(startFrame, endFrame);
            }
            else
            {
                var wasSyncingProjectPlaybackSettings = _syncingProjectPlaybackSettings;
                _syncingProjectPlaybackSettings = true;
                try
                {
                    _playbackSettings.SetFrameRange(startFrame, endFrame);
                }
                finally
                {
                    _syncingProjectPlaybackSettings = wasSyncingProjectPlaybackSettings;
                }
            }
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
        if (session is { AutoKeyframeChanged: true, UndoPushed: false })
        {
            RestoreCanvasMutationSnapshot(session.Snapshot);
            return;
        }
        var hierarchyChanged = session?.HierarchyDirty == true;
        var inspectorChanged = session?.InspectorDirty == true;
        if (mergeFills) hierarchyChanged |= MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        if (mergeLines) hierarchyChanged |= MergeCompatibleLinesAfterDrawingOperation();
        if (hierarchyChanged) _hierarchyPanel.RefreshScene();
        if (hierarchyChanged || inspectorChanged) UpdateInspector();
        if (hierarchyChanged || inspectorChanged) _stage.Invalidate();
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
            && ReferenceEquals(undo.Snapshot, session.Snapshot))
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
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            if (!session.AutoKeyframePreparationAttempted)
            {
                session.AutoKeyframePreparationAttempted = true;
                session.AutoKeyframeChanged = MaterializeAutomaticKeyframesForCanvasEdit();
            }
            return session.Snapshot;
        }

        var snapshot = _scene.CreateSnapshot();
        MaterializeAutomaticKeyframesForCanvasEdit();
        return snapshot;
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

    private void RefreshInspectorAfterMaterialChange()
    {
        var session = _materialEditSession;
        if (session is not null && ReferenceEquals(session.Scene, _scene))
        {
            session.InspectorDirty = true;
            return;
        }

        UpdateInspector();
    }

    private void CaptureUndoSnapshot(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        var snapshot = _scene.CreateSnapshot();
        PushUndoSnapshot(snapshot);
        MaterializeAutomaticKeyframesForCanvasEdit(affectedObjects, affectedLayers);
    }

    private VectorSceneSnapshot CreateCanvasMutationSnapshot(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        var snapshot = _scene.CreateSnapshot();
        MaterializeAutomaticKeyframesForCanvasEdit(affectedObjects, affectedLayers);
        return snapshot;
    }

    private bool MaterializeAutomaticKeyframesForCanvasEdit(
        IEnumerable<int>? affectedObjects = null,
        IEnumerable<int>? affectedLayers = null)
    {
        if (!AutomaticKeyframesEnabledForCurrentScene()) return false;
        var previousLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var layers = ResolveAutomaticKeyframeLayers(affectedObjects, affectedLayers);
        if (layers.Count == 0) return false;

        var changed = false;
        using (_scene.Timeline.BeginBatchUpdate())
        {
            foreach (var layer in layers)
            {
                changed |= _scene.MaterializeAutoKeyframeInPlace(layer, _frame);
            }
        }
        if (!changed) return false;

        RefreshAutomaticKeyframePresentation(previousLastFrame);
        return true;
    }

    private bool AutomaticKeyframesEnabledForCurrentScene()
    {
        if (!_timeline.AutoKeyframeEnabled || IsSceneCompositionContext()) return false;
        var drawingScene = _timeline.Context switch
        {
            VectorScene vectorScene => vectorScene,
            DrawingObjectDefinition drawingObject => drawingObject.Scene,
            _ => null
        };
        return ReferenceEquals(drawingScene, _scene);
    }

    private IReadOnlyList<int> ResolveAutomaticKeyframeLayers(
        IEnumerable<int>? affectedObjects,
        IEnumerable<int>? affectedLayers)
    {
        var layers = new HashSet<int>();
        if (affectedLayers is not null)
        {
            foreach (var layer in affectedLayers)
            {
                if ((uint)layer < _scene.LayerCount) layers.Add(layer);
            }
        }

        var objects = affectedObjects?.ToArray()
            ?? _selectedObjects
                .Concat(_selectedElements.Select(hit => hit.Key.ObjectIndex))
                .Append(_selectedObject)
                .ToArray();
        foreach (var objectIndex in objects)
        {
            if ((uint)objectIndex < _scene.ObjectCount) layers.Add(_scene.ObjectLayer[objectIndex]);
        }
        return layers.OrderBy(layer => layer).ToArray();
    }

    private void RefreshAutomaticKeyframePresentation(int previousLastFrame)
    {
        _timeline.RefreshTimeline();
        if (_timeline.Context.FrameCount - 1 != previousLastFrame) ApplyBoundTimelineDuration(previousLastFrame);
        _hierarchyPanel.RefreshScene();
        RebuildDrawingObjectUnderlay();
        _stage.Invalidate();
    }

    private void RestoreCanvasMutationSnapshot(VectorSceneSnapshot snapshot)
    {
        _scene.RestoreSnapshot(snapshot);
        _scene.EditFrame = _frame;
        _timeline.RefreshTimeline();
        _hierarchyPanel.RefreshScene();
        RebuildDrawingObjectUnderlay();
        SyncSelectionToStage();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void PushUndoSnapshot(
        VectorSceneSnapshot snapshot,
        DrawingObjectDefinition? drawingObject = null,
        DrawingObjectInstanceDefinition[]? instanceSnapshot = null,
        int? playheadFrame = null,
        TimelineSelectionSnapshot? timelineSelection = null)
    {
        MarkProjectDirty();
        _marqueeMaterializationSession = null;
        _undoStack.Push(new DrawingUndoEntry(
            snapshot,
            drawingObject,
            instanceSnapshot,
            playheadFrame,
            timelineSelection));
        var snapshots = _undoStack.ToArray();
        var retainedCount = 0;
        long retainedBytes = 0;
        foreach (var item in snapshots)
        {
            var itemBytes = item.Snapshot.EstimateMemoryBytes();
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
        SceneLayerSnapshot? layerSnapshot = null,
        DrawingObjectInstanceDefinition[]? instanceSnapshot = null,
        int? playheadFrame = null,
        TimelineSelectionSnapshot? timelineSelection = null)
    {
        MarkProjectDirty();
        _sceneTimelineUndoStack.Push(new SceneTimelineUndoEntry(
            scene,
            snapshot,
            layerSnapshot,
            instanceSnapshot,
            playheadFrame,
            timelineSelection));
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
            if (timelineUndo.InstanceSnapshot is not null) activeScene.RestoreInstanceSnapshot(timelineUndo.InstanceSnapshot);
            activeScene.Timeline.RestoreSnapshot(timelineUndo.Snapshot);
            activeScene.SynchronizeTimelineTracks();
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration(previousLastFrame);
            if (timelineUndo.PlayheadFrame is { } scenePlayheadFrame) SetFrame(scenePlayheadFrame);
            else RebuildSceneComposition();
            ClearSelection();
            if (timelineUndo.TimelineSelection is { } sceneTimelineSelection)
            {
                _timeline.RestoreSelectionSnapshot(sceneTimelineSelection);
            }
            UpdateInspector();
            MarkProjectDirty();
            return true;
        }

        if (IsSceneCompositionContext()) return false;
        if (_undoStack.Count == 0) return false;
        var previousDrawingLastFrame = Math.Max(0, _scene.FrameCount - 1);
        var drawingUndo = _undoStack.Pop();
        if (drawingUndo.DrawingObject is not null && drawingUndo.InstanceSnapshot is not null)
        {
            drawingUndo.DrawingObject.RestoreInstanceSnapshot(drawingUndo.InstanceSnapshot);
        }
        _scene.RestoreSnapshot(drawingUndo.Snapshot);
        _scene.EditFrame = _frame;
        ClearSelection();
        _geometryDirty = false;
        CancelTraditionalPenPath();
        CancelPenCurve();
        CancelFreehandStroke();
        _stage.ClearDrawingPreview();
        _stage.ClearMarquee();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration(previousDrawingLastFrame);
        if (drawingUndo.PlayheadFrame is { } drawingPlayheadFrame) SetFrame(drawingPlayheadFrame);
        else RebuildDrawingObjectUnderlay();
        if (drawingUndo.TimelineSelection is { } drawingTimelineSelection)
        {
            _timeline.RestoreSelectionSnapshot(drawingTimelineSelection);
        }
        UpdateInspector();
        _stage.Invalidate();
        MarkProjectDirty();
        return true;
    }

    private bool CutSelectedObjects()
    {
        return CopySelectedObjects() && DeleteSelectedObject();
    }

    private bool CopySelectedObjects()
    {
        if (IsSceneCompositionContext()) return false;
        var selectedInstances = SelectedSceneInstances();
        if (selectedInstances.Count > 0)
        {
            _clipboardObjects.Clear();
            _clipboardDrawingObjectInstances.Clear();
            foreach (var instance in selectedInstances)
            {
                _clipboardDrawingObjectInstances.Add(new ClipboardDrawingObjectInstance(
                    instance.DrawingObjectId,
                    instance.SceneLayerId,
                    instance.Name,
                    instance.EvaluateState(0),
                    instance.StateKeyframes.ToArray()));
            }
            return _clipboardDrawingObjectInstances.Count > 0;
        }

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
            _clipboardDrawingObjectInstances.Clear();
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
                    _scene.GetShapeVertexCount(index),
                    new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]),
                    new PointF(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]),
                    pathContours,
                    _scene.GetLineEndpointStyle(index, startEndpoint: true),
                    _scene.GetLineEndpointStyle(index, startEndpoint: false),
                    _scene.TryGetImportedSvgSource(index, out var importedSvgSource) ? importedSvgSource : null));
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
        if (_clipboardDrawingObjectInstances.Count > 0)
        {
            return PasteCopiedDrawingObjectInstances();
        }
        if (_clipboardObjects.Count == 0) return false;
        var snapshot = CreateCanvasMutationSnapshot(
            affectedLayers: _clipboardObjects.Select(item =>
                Math.Clamp(item.Layer, 0, Math.Max(0, _scene.LayerCount - 1))));
        var pasted = new List<int>(_clipboardObjects.Count);
        foreach (var item in _clipboardObjects)
        {
            var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
            var layer = Math.Clamp(item.Layer, 0, Math.Max(0, _scene.LayerCount - 1));
            int index;
            if (item.Shape == ShapeKind.ImportedSvg && !string.IsNullOrWhiteSpace(item.ImportedSvgSource))
            {
                var center = new PointF(item.Center.X + offset.X, item.Center.Y + offset.Y);
                index = _scene.AddImportedSvgObject(layer, center, item.Size, item.Angle, item.ImportedSvgSource);
            }
            else if (item.Shape == ShapeKind.Path && item.PathWorldContours is { Length: > 0 } pathContours)
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
                index = _scene.AddObject(
                    layer,
                    center,
                    item.Size,
                    item.Angle,
                    item.Stroke,
                    item.FillColor,
                    item.StrokeColor,
                    item.Atoms,
                    item.Shape,
                    item.ShapeVertexCount);
                if (item.Shape == ShapeKind.Line)
                {
                    _scene.CurveControlX[index] = item.CurveControl.X + offset.X;
                    _scene.CurveControlY[index] = item.CurveControl.Y + offset.Y;
                    _scene.CurveControl2X[index] = item.CurveControl2.X + offset.X;
                    _scene.CurveControl2Y[index] = item.CurveControl2.Y + offset.Y;
                    _scene.SetLineEndpointStyle(index, startEndpoint: true, item.StartEndpointStyle);
                    _scene.SetLineEndpointStyle(index, startEndpoint: false, item.EndEndpointStyle);
                }
            }

            if (index >= 0) pasted.Add(index);
        }

        if (pasted.Count == 0)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }
        PushUndoSnapshot(snapshot);
        SetSelection(pasted);
        MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        MergeCompatibleLinesAfterDrawingOperation();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private int[] SelectedActiveDrawingObjectIndices()
    {
        var targets = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && _scene.IsObjectActive(index, _frame))
            .Distinct()
            .ToArray();
        if (targets.Length == 0
            && (uint)_selectedObject < _scene.ObjectCount
            && _scene.IsObjectActive(_selectedObject, _frame))
        {
            targets = [_selectedObject];
        }
        return targets;
    }

    private bool TryPrepareSelectedDrawingObjectsForCommand(
        VectorSceneSnapshot snapshot,
        out int[] targets)
    {
        targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length == 0) return false;
        if (_selectedElements.Count == 0) return true;

        var materialized = _scene.MaterializeSelectedParts(
            _selectedElements.Select(hit => hit.Key).ToArray(),
            _frame);
        if (materialized.Success && materialized.Parts.Length > 0)
        {
            targets = materialized.Parts
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            return targets.Length > 0;
        }

        _scene.RestoreSnapshot(snapshot);
        _scene.EditFrame = _frame;
        SyncSelectionToStage();
        return false;
    }

    private bool FlipSelectedDrawingObjects(bool horizontal)
    {
        if (DrawingToolsBlocked()) return false;
        if (SelectedSceneInstances().Count > 0)
        {
            return FlipSelectedNestedDrawingObjects(horizontal);
        }
        var snapshot = CreateCanvasMutationSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(snapshot, out var targets)
            || targets.Any(IsImportedSvgObject)
            || !_scene.FlipObjects(targets, horizontal))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        SetSelection(targets);
        ApplySelectedFillOverwriteAfterGeometryEdit();
        MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool PasteCopiedDrawingObjectInstances()
    {
        var container = ActiveDrawingObject();
        if (container is null || _clipboardDrawingObjectInstances.Count == 0) return false;

        var offset = new PointF(PasteOffsetUnits, PasteOffsetUnits);
        var pasted = new List<DrawingObjectInstanceDefinition>(_clipboardDrawingObjectInstances.Count);
        foreach (var item in _clipboardDrawingObjectInstances)
        {
            var layerId = container.Scene.ResolveInstanceLayerId(item.SceneLayerId);
            var baseState = item.BaseState with
            {
                X = VectorUnits.Quantize(item.BaseState.X + offset.X),
                Y = VectorUnits.Quantize(item.BaseState.Y + offset.Y)
            };
            if (!_project.TryAddDrawingObjectInstance(
                    container.Id,
                    item.DrawingObjectId,
                    baseState.Position,
                    layerId,
                    out var instance)
                || instance is null)
            {
                continue;
            }

            instance.Name = item.Name;
            instance.SetStateAtFrame(0, baseState);
            instance.RestoreStateKeyframes(item.StateKeyframes.Select(keyframe => keyframe with
            {
                State = keyframe.State with
                {
                    X = VectorUnits.Quantize(keyframe.State.X + offset.X),
                    Y = VectorUnits.Quantize(keyframe.State.Y + offset.Y)
                }
            }));
            pasted.Add(instance);
        }

        if (pasted.Count == 0) return false;
        _timeline.RefreshTimeline();
        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(pasted, pasted[^1]);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool MoveSelectedDrawingObjectsInStack(int direction)
    {
        if (DrawingToolsBlocked() || direction == 0) return false;
        if (SelectedSceneInstances().Count > 0)
        {
            return MoveSelectedNestedDrawingObjectsInStack(direction);
        }
        var snapshot = CreateCanvasMutationSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(snapshot, out var targets)
            || !_scene.MoveObjectsInLayerStack(targets, direction, _frame))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return false;
        }

        SetSelection(targets);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool FlipSelectedNestedDrawingObjects(bool horizontal)
    {
        if (IsSceneCompositionContext()) return false;
        var instances = SelectedSceneInstances();
        if (instances.Count == 0 || !TryGetSelectedSceneInstanceBounds(out var bounds)) return false;

        var centerX = bounds.Left + bounds.Width * 0.5f;
        var centerY = bounds.Top + bounds.Height * 0.5f;
        var changed = false;
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        foreach (var instance in instances)
        {
            EnsureInstanceStateTimelineKeyframe(instance);
            var state = instance.EvaluateState(_frame);
            changed |= instance.SetStateAtFrame(
                _frame,
                horizontal
                    ? state with
                    {
                        X = VectorUnits.Quantize(centerX * 2 - state.X),
                        ScaleX = -state.ScaleX
                    }
                    : state with
                    {
                        Y = VectorUnits.Quantize(centerY * 2 - state.Y),
                        ScaleY = -state.ScaleY
                    });
        }
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        if (!changed) return false;

        _sceneInstanceTimelineDirty = false;
        _timeline.RefreshTimeline();
        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(instances, instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal)));
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    private bool CanMoveSelectedNestedDrawingObjectsInStack(int direction)
    {
        var container = ActiveDrawingObject();
        var instanceIds = SelectedSceneInstances().Select(instance => instance.Id).ToArray();
        return container is not null
            && instanceIds.Length > 0
            && _project.CanMoveDrawingObjectInstancesInLayer(container.Id, instanceIds, direction);
    }

    private bool MoveSelectedNestedDrawingObjectsInStack(int direction)
    {
        if (IsSceneCompositionContext() || direction == 0) return false;
        var container = ActiveDrawingObject();
        var instances = SelectedSceneInstances();
        var instanceIds = instances.Select(instance => instance.Id).ToArray();
        if (container is null
            || instanceIds.Length == 0
            || !_project.TryMoveDrawingObjectInstancesInLayer(container.Id, instanceIds, direction))
        {
            return false;
        }

        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(instances, instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal)));
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

        if (_spacePanHeld && e.Button == MouseButtons.Left && BeginTemporaryCanvasPanPointer(e.Location)) return;

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

        if (TryBeginNestedDrawingObjectPointer(e)) return;

        if (_tool == ToolMode.Pen)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (IsControlPressed() && TryBeginTraditionalPenAnchorEdit(e.Location)) return;
                if (_traditionalPenCurrentAnchor is null)
                {
                    if (TryBeginTraditionalPenSelectedHandleEdit(e.Location)) return;
                    if (TrySelectTraditionalPenPath(e.Location)) return;
                    if (HasTraditionalPenPathSelection()) ClearSelection();
                }
                BeginTraditionalPenPoint(e.Location);
            }
            else if (e.Button == MouseButtons.Right && !CancelTraditionalPenPath()) ShowStageContextMenu(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_penStartWorld is null && IsControlPressed() && TryInsertPenAnchor(e.Location)) return;
                BeginPenSegment(e.Location);
            }
            else if (e.Button == MouseButtons.Right && !CancelPenCurve()) ShowStageContextMenu(e.Location);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowStageContextMenu(e.Location);
            return;
        }

        if (_tool == ToolMode.Eyedropper)
        {
            ApplyEyedropper(e);
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
            if (!_scene.HasSelectableObjectAt(startWorld, _frame, SelectionToleranceWorld()))
            {
                if (e.Button == MouseButtons.Left) BeginBlankMarqueeSelection(e.Location);
                UpdateInspector();
                if (!_stage.MarqueeOverlayActive) _stage.Invalidate();
                UpdateInteractionCursor(e.Location);
                return;
            }
            var hit = _scene.HitTestElement(startWorld, _frame, SelectionToleranceWorld());
            if (hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
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
                if (e.Button == MouseButtons.Left) BeginBlankMarqueeSelection(e.Location);
            }

            UpdateInspector();
            if (!_stage.MarqueeOverlayActive) _stage.Invalidate();
            UpdateInteractionCursor(e.Location);
        }
        else if (_tool == ToolMode.Fill)
        {
            if (e.Button != MouseButtons.Left || _startWorld is not { } fillWorld) return;
            var hit = _scene.HitTestElement(_startWorld.Value, _frame, SelectionToleranceWorld());
            if (TryApplyFillToLine(hit))
            {
                return;
            }

            if (hit.IsValid
                && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                && hit.Key.Kind == DrawingElementKind.Fill
                && IsFillShape(_scene.ShapeKind[hit.Key.ObjectIndex]))
            {
                var color = ActiveColor();
                if (_scene.Argb[hit.Key.ObjectIndex] == color.ToArgb()) return;
                var snapshot = CreateCanvasMutationSnapshot([hit.Key.ObjectIndex]);
                var materialized = hit.Key.Kind == DrawingElementKind.Fill
                    ? _scene.DetachElementForMove(hit, _frame)
                    : hit;
                if (!materialized.IsValid)
                {
                    RestoreCanvasMutationSnapshot(snapshot);
                    return;
                }

                var hitObject = materialized.Key.ObjectIndex;
                _scene.Argb[hitObject] = color.ToArgb();
                _scene.DisableLinearGradient(hitObject);
                var animationContours = _scene.GetFillPartContours(materialized, _frame);
                var overwritten = NormalizeNewPaintObjects([hitObject]);
                if (overwritten.Length > 0) hitObject = overwritten[0];
                SetSelection(hitObject);
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
                var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
                if (!_scene.TryCreateFillFromClosedStrokeRegion(fillWorld, _frame, color, out var created, out var animationContours))
                {
                    RestoreCanvasMutationSnapshot(snapshot);
                    return;
                }

                var overwritten = NormalizeNewPaintObjects([created]);
                if (overwritten.Length > 0) created = overwritten[0];

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

    private void ApplyEyedropper(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = _scene.HitTestElement(_stage.ScreenToWorld(e.Location), _frame, SelectionToleranceWorld());
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount) return;

        var objectIndex = hit.Key.ObjectIndex;
        if (IsImportedSvgObject(objectIndex)) return;
        var strokeTarget = hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke;
        var color = Color.FromArgb(strokeTarget ? _scene.StrokeArgb[objectIndex] : _scene.Argb[objectIndex]);
        var samplesGradient = _scene.HasGradient(objectIndex)
            && (strokeTarget ? _scene.ShapeKind[objectIndex] == ShapeKind.Line : IsFillShape(_scene.ShapeKind[objectIndex]));
        var gradientKind = samplesGradient ? _scene.GetGradientKind(objectIndex) : GradientKind.Solid;
        var gradientStops = samplesGradient
            ? _scene.GetGradientStops(objectIndex)
            : [new GradientStop(0, color), new GradientStop(1, color)];
        if (gradientStops.Length > 0) color = Color.FromArgb(gradientStops[0].Argb);

        _materialEditor.SetGradientPreviewTarget(strokeTarget);
        _materialEditor.SetMaterial(
            strokeTarget ? _materialEditor.Fill : color,
            strokeTarget ? color : _materialEditor.Stroke,
            _materialEditor.StrokeWidth,
            color.A / 255f);
        _materialEditor.SetGradient(gradientKind, gradientStops);
    }

    private bool TryApplyFillToLine(DrawingElementHit hit)
    {
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.Stroke
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            || _scene.ShapeKind[hit.Key.ObjectIndex] != ShapeKind.Line)
        {
            return false;
        }

        var targets = IsShiftPressed()
            ? _scene.GetConnectedStrokeElements(hit, _frame)
                .Select(connected => connected.Key.ObjectIndex)
                .Where(index => _scene.IsObjectSelectable(index, _frame) && _scene.ShapeKind[index] == ShapeKind.Line)
                .Distinct()
                .ToArray()
            : [hit.Key.ObjectIndex];
        if (targets.Length == 0) return true;

        var color = ActiveColor().ToArgb();
        var changed = targets.Any(index => _scene.StrokeArgb[index] != color || _scene.HasGradient(index));
        if (!changed) return true;

        var snapshot = CreateCanvasMutationSnapshot(targets);
        foreach (var objectIndex in targets)
        {
            _scene.StrokeArgb[objectIndex] = color;
            _scene.DisableLinearGradient(objectIndex);
        }

        SetSelection(targets);
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        ClearFillHoverPreview();
        _stage.Invalidate();
        return true;
    }

    private void ApplyInkBottle(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _startWorld is not { } world) return;
        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)) return;
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

        var snapshot = CreateCanvasMutationSnapshot([objectIndex]);
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
            _gradientEditChanged = false;
            _gradientEditSnapshot = CreateCanvasMutationSnapshot([_selectedObject]);
            _gradientHandle = overlayHit.Kind;
            _gradientStopIndex = overlayHit.StopIndex;
            UpdateInteractionCursor(e.Location);
            return;
        }

        var hit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (!hit.IsValid
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            || !SupportsGradient(_scene.ShapeKind[hit.Key.ObjectIndex]))
        {
            FinishPointerInteraction();
            return;
        }

        var objectIndex = hit.Key.ObjectIndex;
        _gradientEditChanged = false;
        _gradientEditSnapshot = CreateCanvasMutationSnapshot([objectIndex]);
        if (!_scene.HasGradient(objectIndex))
        {
            var kind = _materialEditor.GradientKind == GradientKind.Solid
                ? GradientKind.Linear
                : _materialEditor.GradientKind;
            if (kind == GradientKind.ShapeRadial)
            {
                _scene.SetGradientPaint(objectIndex, kind, _materialEditor.GradientStops, start: world);
            }
            else
            {
                _scene.SetGradientPaint(objectIndex, kind, _materialEditor.GradientStops, world, world);
            }
            _gradientEditChanged = true;
        }

        SetSelection(objectIndex);
        _gradientHandle = _scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientHandleKind.Start
            : GradientHandleKind.End;
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

        var snapped = VectorUnits.Quantize(SnapDrawingPoint(world));
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
            if (Math.Abs(position - stops[_gradientStopIndex].Position) <= 0.0001f) return;
            stops[_gradientStopIndex] = new GradientStop(position, stops[_gradientStopIndex].Argb);
            _scene.SetGradientStops(_selectedObject, stops);
            _gradientEditChanged = true;
        }
        else
        {
            if (_gradientHandle == GradientHandleKind.Start)
            {
                if (_scene.GetGradientKind(_selectedObject) is GradientKind.Radial or GradientKind.ShapeRadial)
                {
                    end = new PointF(end.X + snapped.X - start.X, end.Y + snapped.Y - start.Y);
                }
                start = snapped;
                if (_scene.GetGradientKind(_selectedObject) == GradientKind.ShapeRadial
                    && GradientPaintUtilities.TryFindShapeBoundaryPoint(
                        _scene.GetObjectBoundaryContours(_selectedObject),
                        start,
                        new PointF(end.X - start.X, end.Y - start.Y),
                        out var boundary))
                {
                    end = boundary;
                }
            }
            else end = snapped;
            if (start == _scene.GetGradientStart(_selectedObject)
                && end == _scene.GetGradientEnd(_selectedObject)) return;
            _scene.ClearGradientPath(_selectedObject);
            _scene.SetLinearGradientEndpoints(_selectedObject, start, end);
            _gradientEditChanged = true;
        }
        UpdateGradientOverlay();
        _stage.Invalidate();
    }

    private void CompleteGradientPointer()
    {
        if (_gradientEditSnapshot is { } snapshot)
        {
            if (_gradientEditChanged) PushUndoSnapshot(snapshot);
            else RestoreCanvasMutationSnapshot(snapshot);
        }
        _gradientEditSnapshot = null;
        _gradientEditChanged = false;
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
            RestoreCanvasMutationSnapshot(snapshot);
        }

        _gradientEditSnapshot = null;
        _gradientEditChanged = false;
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
            if (_traditionalPenPointerDown) FinishTraditionalPenPoint(e.Location);
            FinishTraditionalPenPointerDrag();
            CancelTraditionalPenPath();
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (_penSegmentDragging) CommitPenSegment();
            FinishPenSegmentPointerDrag();
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
        if (hit.IsValid && IsImportedSvgObject(hit.Key.ObjectIndex))
        {
            _pendingClickSelection = DrawingElementHit.None;
            _marqueeSelecting = false;
            _marqueeStart = null;
            _stage.ClearMarquee();
            SetSelection(hit.Key.ObjectIndex);
            UpdateInspector();
            _stage.Invalidate();
            return;
        }
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
            if (_tool == ToolMode.Pen)
            {
                UpdateTraditionalPenHover(e.Location);
                return;
            }

            if (_tool == ToolMode.SimplePen)
            {
                UpdatePenHover(e.Location);
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

        if (_sceneInstanceMoveActive
            || SelectedSceneInstance() is not null && _activeTransformHandle != TransformHandleKind.None)
        {
            HandleSceneCompositionPointerMove(e, dx, dy);
            return;
        }

        if (_tool == ToolMode.Pen && _traditionalPenAnchorEditing && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location)) QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
            return;
        }

        if (_tool == ToolMode.Pen && _traditionalPenPointerDown && e.Button == MouseButtons.Left)
        {
            UpdateTraditionalPenHandles(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen && _penSegmentDragging && e.Button == MouseButtons.Left)
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

            return;
        }

        if (_viewReferencePanning)
        {
            _stage.PanReferenceCamera(dx, dy);
            return;
        }

        if (_viewReferenceZooming)
        {
            _stage.DollyReferenceCameraByPixels(dy);
            return;
        }

        if (_viewPanning)
        {
            _stage.Pan(dx, dy);
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
            return;
        }

        if (_tool == ToolMode.Hand)
        {
            _stage.Pan(dx, dy);
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
                QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
            }
        }
        else if (_tool == ToolMode.Select && _forceMarqueeOnPointerDown && e.Button == MouseButtons.Left)
        {
            if (PointerDragExceeded(e.Location))
            {
                BeginBlankMarqueeSelection(e.Location);
            }
        }
        else if (_tool == ToolMode.Select && _selectedObject >= 0 && _startWorld is not null && _selectedStart is not null && e.Button == MouseButtons.Left)
        {
            if (!PointerDragExceeded(e.Location)) return;
            QueueMoveSelectedFromPointer(_stage.ScreenToWorld(e.Location));
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
            && hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke
            && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
            && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
            && _stage.IsValidEditableBezierHit(hit))
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

    private bool TryBeginNestedDrawingObjectPointer(MouseEventArgs e)
    {
        if (IsSceneCompositionContext()
            || e.Button != MouseButtons.Left
            || _tool is not (ToolMode.Select or ToolMode.Transform)
            || ActiveDrawingObject()?.Instances.Count is not > 0)
        {
            return false;
        }

        var world = _stage.ScreenToWorld(e.Location);
        if (_tool == ToolMode.Transform
            && SelectedSceneInstance() is not null
            && _stage.HitTestTransformHandle(e.Location) != TransformHandleKind.None)
        {
            BeginSceneCompositionPointer(e);
            return true;
        }

        var localHit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (localHit.IsValid && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame)) return false;
        if (!TryResolveSceneInstanceAt(world, out _) && !IsPointerInsideSelectedSceneInstance(world)) return false;

        BeginSceneCompositionPointer(e);
        return true;
    }

    private void BeginSceneInstanceMovePointer(PointF world)
    {
        if (TryResolveSceneInstanceAt(world, out var instance))
        {
            SetSceneInstanceSelection(instance, additive: IsShiftPressed());
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
            return;
        }

        if (_viewReferencePanning)
        {
            _stage.PanReferenceCamera(dx, dy);
            return;
        }

        if (_viewReferenceZooming)
        {
            _stage.DollyReferenceCameraByPixels(dy);
            return;
        }

        if (_viewPanning)
        {
            _stage.Pan(dx, dy);
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
        var instances = SelectedSceneInstances();
        if (instances.Count == 0) return;
        var dx = world.X - _transformLastPointer.X;
        var dy = world.Y - _transformLastPointer.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;

        foreach (var instance in instances)
        {
            EnsureInstanceStateTimelineKeyframe(instance);
            var position = instance.EvaluatePosition(_frame);
            instance.SetPositionAtFrame(_frame, new PointF(
                VectorUnits.Quantize(position.X + dx),
                VectorUnits.Quantize(position.Y + dy)));
        }
        _sceneInstanceTimelineDirty = true;
        TranslateCustomTransformFocus(dx, dy);
        _transformLastPointer = world;
        PreviewTranslateSelectedSceneInstance(dx, dy);
    }

    private void ApplySceneInstanceTransform(PointF world)
    {
        var instance = SelectedSceneInstance();
        var instances = SelectedSceneInstances();
        if (instance is null || instances.Count == 0 || _activeTransformHandle == TransformHandleKind.None) return;

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
            foreach (var selectedInstance in instances)
            {
                EnsureInstanceStateTimelineKeyframe(selectedInstance);
                var state = selectedInstance.EvaluateState(_frame);
                var origin = RotatePointAround(state.Position, _transformPivot, delta);
                selectedInstance.SetStateAtFrame(_frame, state with
                {
                    X = VectorUnits.Quantize(origin.X),
                    Y = VectorUnits.Quantize(origin.Y),
                    RotationZ = NormalizeDegrees(state.RotationZ + delta * 57.29578f)
                });
            }
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
            foreach (var selectedInstance in instances)
            {
                EnsureInstanceStateTimelineKeyframe(selectedInstance);
                var state = selectedInstance.EvaluateState(_frame);
                if (IsHorizontalSkewHandle(_activeTransformHandle))
                {
                    selectedInstance.SetStateAtFrame(
                        _frame,
                        state with { SkewX = Math.Clamp(state.SkewX + degrees, -80, 80) });
                }
                else
                {
                    selectedInstance.SetStateAtFrame(
                        _frame,
                        state with { SkewY = Math.Clamp(state.SkewY + degrees, -80, 80) });
                }
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
            var canPreviewScale = instances.All(selectedInstance =>
            {
                var state = selectedInstance.EvaluateState(_frame);
                return Math.Abs(state.RotationZ) <= 0.001f
                    && Math.Abs(state.SkewX) <= 0.001f
                    && Math.Abs(state.SkewY) <= 0.001f;
            });
            foreach (var selectedInstance in instances)
            {
                EnsureInstanceStateTimelineKeyframe(selectedInstance);
                var state = selectedInstance.EvaluateState(_frame);
                selectedInstance.SetStateAtFrame(_frame, state with
                {
                    X = VectorUnits.Quantize(pivot.X + (state.X - pivot.X) * scaleX),
                    Y = VectorUnits.Quantize(pivot.Y + (state.Y - pivot.Y) * scaleY),
                    ScaleX = Math.Clamp(state.ScaleX * scaleX, 0.01f, 1000f),
                    ScaleY = Math.Clamp(state.ScaleY * scaleY, 0.01f, 1000f)
                });
            }
            if (canPreviewScale)
            {
                previewTransform = point => new PointF(
                    pivot.X + (point.X - pivot.X) * scaleX,
                    pivot.Y + (point.Y - pivot.Y) * scaleY);
            }
            changed = true;
        }

        if (!changed) return;
        _sceneInstanceTimelineDirty = true;
        _transformLastPointer = world;
        if (previewTransform is not null && PreviewTransformSelectedSceneInstance(previewTransform))
        {
            _transformCurrentBounds = _stage.TransformBounds;
            return;
        }

        RebuildEditableInstanceComposition();
        _transformCurrentBounds = _stage.TransformBounds;
        UpdateInspector();
    }

    private void PreviewTranslateSelectedSceneInstance(float dx, float dy)
    {
        if (_selectedSceneInstanceObjectIndices.Length == 0) return;
        ActiveInstanceCompositionScene().TranslateObjectsForPreview(_selectedSceneInstanceObjectIndices, dx, dy);
        _sceneInstancePreviewDirty = true;
        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
    }

    private void EnsureInstanceStateTimelineKeyframe(DrawingObjectInstanceDefinition instance)
    {
        if (!_sceneInstanceTimelineKeyframeEnsuredIds.Add(instance.Id)) return;

        if (IsSceneCompositionContext())
        {
            var scene = ActiveScene();
            var track = scene?.Timeline.FindTrackByTargetId(instance.SceneLayerId);
            if (scene is null || track is null) return;
            if (_frame >= track.Duration) scene.Timeline.SetTrackDuration(track.Id, _frame + 1);
            var exposure = track.EvaluateExposure(_frame);
            if (!exposure.IsKeyframe || !exposure.HasContent)
            {
                _sceneInstanceTimelineDirty |= scene.Timeline.InsertKeyframe(track.Id, _frame);
            }
            _sceneInstanceTimelineDirty |= CaptureInstanceStateKeyframes(scene, instance.SceneLayerId, _frame);
            return;
        }

        var drawingObject = ActiveDrawingObject();
        if (drawingObject is null) return;
        var layer = Array.IndexOf(drawingObject.Scene.LayerIds, instance.SceneLayerId);
        if (layer >= 0)
        {
            _sceneInstanceTimelineDirty |= drawingObject.Scene.InsertTimelineKeyframe(layer, _frame);
            _sceneInstanceTimelineDirty |= CaptureInstanceStateKeyframes(drawingObject, instance.SceneLayerId, _frame);
        }
    }

    private bool PreviewTransformSelectedSceneInstance(Func<PointF, PointF> transform)
    {
        if (_selectedSceneInstanceObjectIndices.Length == 0) return false;
        ActiveInstanceCompositionScene().TransformObjects(
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
            if (hit.IsValid && _scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)) SetSelection(hit.Key.ObjectIndex);
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

        if (_activeTransformHandle == TransformHandleKind.Move)
        {
            var dx = world.X - _transformLastPointer.X;
            var dy = world.Y - _transformLastPointer.Y;
            if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
            CapturePointerUndoSnapshot();
            _scene.TransformObjects(
                _selectedObjects,
                point => new PointF(point.X + dx, point.Y + dy),
                rebuildGeometryIndex: false);
            TranslateCustomTransformFocus(dx, dy);
        }
        else if (IsRotationHandle(_activeTransformHandle))
        {
            var angle = TransformPointerAngle(world, _transformPivot);
            var delta = NormalizeAngle(angle - _transformLastAngle);
            if (Math.Abs(delta) <= 0.0001f) return;
            CapturePointerUndoSnapshot();
            var cos = MathF.Cos(delta);
            var sin = MathF.Sin(delta);
            var pivot = _transformPivot;
            _scene.TransformObjects(
                _selectedObjects,
                point => new PointF(
                    pivot.X + (point.X - pivot.X) * cos - (point.Y - pivot.Y) * sin,
                    pivot.Y + (point.X - pivot.X) * sin + (point.Y - pivot.Y) * cos),
                rebuildGeometryIndex: false);
            _transformLastAngle = angle;
        }
        else if (IsSkewHandle(_activeTransformHandle))
        {
            if (_selectedObjects.Any(IsImportedSvgObject)) return;
            var factor = SkewFactor(_activeTransformHandle, _transformCurrentBounds, _transformLastPointer, world);
            if (Math.Abs(factor) <= 0.0001f) return;
            CapturePointerUndoSnapshot();
            if (IsHorizontalSkewHandle(_activeTransformHandle))
            {
                var anchorY = _activeTransformHandle == TransformHandleKind.SkewTop
                    ? _transformCurrentBounds.Bottom
                    : _transformCurrentBounds.Top;
                _scene.ShearObjects(
                    _selectedObjects,
                    point => new PointF(point.X + (point.Y - anchorY) * factor, point.Y),
                    rebuildGeometryIndex: false);
            }
            else
            {
                var anchorX = _activeTransformHandle == TransformHandleKind.SkewLeft
                    ? _transformCurrentBounds.Right
                    : _transformCurrentBounds.Left;
                _scene.ShearObjects(
                    _selectedObjects,
                    point => new PointF(point.X, point.Y + (point.X - anchorX) * factor),
                    rebuildGeometryIndex: false);
            }
        }
        else
        {
            var nextBounds = ResizedTransformBounds(_transformCurrentBounds, _transformPivot, _activeTransformHandle, world);
            if (nextBounds.Width <= 0 || nextBounds.Height <= 0) return;
            var scaleX = nextBounds.Width / Math.Max(0.001f, _transformCurrentBounds.Width);
            var scaleY = nextBounds.Height / Math.Max(0.001f, _transformCurrentBounds.Height);
            if (Math.Abs(scaleX - 1) <= 0.0001f && Math.Abs(scaleY - 1) <= 0.0001f) return;
            CapturePointerUndoSnapshot();
            var pivot = _transformPivot;
            _scene.TransformObjects(
                _selectedObjects,
                point => new PointF(
                    pivot.X + (point.X - pivot.X) * scaleX,
                    pivot.Y + (point.Y - pivot.Y) * scaleY),
                rebuildGeometryIndex: false);
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
                || candidate.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
                || (uint)candidate.Key.ObjectIndex >= _scene.ObjectCount
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

    private void MoveSelectedFromPointer(PointF world, bool synchronizeLinkedFills = true)
    {
        if (_selectedObject < 0 || _startWorld is null || _selectedStart is null) return;
        CapturePointerUndoSnapshot();
        if (_activeHandle != EditHandleKind.None)
        {
            var editsWholePenLine = _tool == ToolMode.Pen && _traditionalPenAnchorEditing;
            if (!editsWholePenLine && !EnsureSelectedElementDetachedForMove()) return;
            CaptureFillBoundaryLineLinks();
            ApplyHandleDrag(world);
            if (synchronizeLinkedFills) SynchronizeLinkedFillBoundaries();
        }
        else
        {
            if (!EnsureSelectedElementDetachedForMove()) return;
            CaptureFillBoundaryLineLinks();
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
                    if (_selectedCurve2Starts.TryGetValue(index, out var curve2Start))
                    {
                        _scene.CurveControl2X[index] = VectorUnits.Quantize(curve2Start.X + dxWorld);
                        _scene.CurveControl2Y[index] = VectorUnits.Quantize(curve2Start.Y + dyWorld);
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
                if (_curveControl2Start is not null)
                {
                    _scene.CurveControl2X[_selectedObject] = VectorUnits.Quantize(_curveControl2Start.Value.X + dxWorld);
                    _scene.CurveControl2Y[_selectedObject] = VectorUnits.Quantize(_curveControl2Start.Value.Y + dyWorld);
                }
                TranslateGradientFromEditStart(_selectedObject, dxWorld, dyWorld);
            }

            if (synchronizeLinkedFills) SynchronizeLinkedFillBoundaries();
        }

        _geometryDirty = true;
        if (!synchronizeLinkedFills && _fillBoundaryLineLinks.Count > 0) _linkedFillBoundaryPreviewDirty = true;
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
        if (_activeHandle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && _selectedElements.All(hit => IsWholeLineEndpointEditHit(_scene, hit)))
        {
            SetSelection(_selectedObject);
            return true;
        }

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
            if (_undoCapturedForPointerEdit && _undoStack.TryPop(out var undo))
            {
                RestoreCanvasMutationSnapshot(undo.Snapshot);
            }
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

    internal static bool IsWholeLineEndpointEditHit(VectorScene scene, DrawingElementHit hit)
    {
        return hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < scene.ObjectCount
            && scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
            && hit.StartT <= DrawingTopologyRules.UnitIntersectionTolerance
            && hit.EndT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance;
    }

    private void CaptureFillBoundaryLineLinks()
    {
        if (_fillBoundaryLineLinks.Count > 0) return;

        var translatedObjects = _activeHandle == EditHandleKind.None
            ? _selectedMoveStarts.Keys.ToHashSet()
            : null;
        var editedLines = _selectedMoveStarts.Keys
            .Concat(_lineEndpointEditStarts.Select(edit => edit.ObjectIndex))
            .Append(_selectedObject)
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.ShapeKind[index] is ShapeKind.Line or ShapeKind.Freeform)
            .Distinct();
        foreach (var lineObjectIndex in editedLines)
        {
            var links = _scene.CaptureFillBoundaryLineLinks(lineObjectIndex, _frame);
            _fillBoundaryLineLinks.AddRange(translatedObjects is null
                ? links
                : ExcludeTranslatedFillBoundaryLinks(links, translatedObjects));
        }
    }

    internal static FillBoundaryLineLink[] ExcludeTranslatedFillBoundaryLinks(
        IReadOnlyList<FillBoundaryLineLink> links,
        IReadOnlySet<int> translatedObjects)
    {
        return links
            .Where(link => !translatedObjects.Contains(link.FillObjectIndex))
            .ToArray();
    }

    private void SynchronizeLinkedFillBoundaries()
    {
        if (_fillBoundaryLineLinks.Count == 0
            || _selectedObject < 0
            || _selectedObject >= _scene.ObjectCount)
        {
            return;
        }

        if (_scene.UpdateFillBoundaryLineLinks(_fillBoundaryLineLinks, rebuildGeometryIndex: false))
        {
            _geometryDirty = true;
        }
        _linkedFillBoundaryPreviewDirty = false;
    }

    private void SynchronizeLinkedFillBoundariesForPreview()
    {
        if (_fillBoundaryLineLinks.Count == 0 || !_linkedFillBoundaryPreviewDirty) return;
        SynchronizeLinkedFillBoundaries();
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
        _marqueeInstanceSelectionBase = _additiveSelection ? _selectedSceneInstanceIds.ToArray() : [];
        _pendingClickSelection = DrawingElementHit.None;
        _selectedStart = null;
        _curveControlStart = null;
        _curveControl2Start = null;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _selectedGradientStarts.Clear();
        _lineEndpointEditStarts.Clear();
        _stage.SetMarquee(_marqueeStart.Value, current);
    }

    private void BeginBlankMarqueeSelection(Point current)
    {
        BeginMarqueeFromPendingSelection(current);
        if (_additiveSelection) return;

        _marqueeSelectionCancellationPending = true;
        ClearSelection();
    }

    private void FinalizePendingMarqueeSelectionCancellation()
    {
        if (!_marqueeSelectionCancellationPending) return;
        _marqueeSelectionCancellationPending = false;
        CancelStageSelection();
    }

    private void StageMouseUp(object? sender, MouseEventArgs e)
    {
        if (_brushColorPaletteActive)
        {
            UpdateBrushColorPaletteHover(e.Location);
            return;
        }

        if (_spacePanPointerActive)
        {
            EndTemporaryCanvasPanPointer();
            return;
        }

        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        FlushLineDragPreview();

        if (_marqueeSelecting)
        {
            CompleteMarqueeSelection(e.Location);
            _stage.ClearDrawingPreview();
            FinishPointerInteraction();
            return;
        }

        if (_tool == ToolMode.Pen && _traditionalPenAnchorEditing)
        {
            CompleteTraditionalPenAnchorEdit();
            UpdateTraditionalPenHover(e.Location);
            return;
        }

        if (_tool == ToolMode.Pen)
        {
            if (_traditionalPenPointerDown) FinishTraditionalPenPoint(e.Location);
            FinishTraditionalPenPointerDrag();
            if (_traditionalPenCurrentAnchor is not null) UpdateTraditionalPenHover(e.Location);
            return;
        }

        if (_tool == ToolMode.SimplePen)
        {
            if (_penSegmentDragging)
            {
                UpdatePenControl(e.Location);
                CommitPenSegment();
            }
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
        FinalizePendingMarqueeSelectionCancellation();
        if (_spacePanPointerActive)
        {
            EndTemporaryCanvasPanPointer();
            return;
        }
        if (_viewPanning || _viewZooming || _viewOrbiting || _viewReferencePanning || _viewReferenceZooming)
        {
            EndGlobalViewDrag();
            return;
        }

        FlushLineDragPreview();

        if (_traditionalPenAnchorEditing)
        {
            CompleteTraditionalPenAnchorEdit();
            return;
        }

        if (_traditionalPenPointerDown)
        {
            var pointer = _stage.PointToClient(Cursor.Position);
            FinishTraditionalPenPoint(pointer);
            FinishTraditionalPenPointerDrag();
            _stage.ClearPenAnchorGuides();
            if (_traditionalPenCurrentAnchor is not null) UpdateTraditionalPenHover(pointer);
            return;
        }

        if (_penSegmentDragging)
        {
            if (!CommitPenSegment())
            {
                _penEndWorld = null;
                _penControlWorld = null;
            }
            FinishPenSegmentPointerDrag();
            _stage.ClearDrawingPreview();
            _stage.ClearPenAnchorGuides();
            return;
        }

        if (_gradientEditSnapshot is not null)
        {
            CompleteGradientPointer();
            FinishPointerInteraction();
            return;
        }

        if (_lastMouse is null
            && !_freehandDrawing
            && !_marqueeSelecting
            && !_marqueeSelectionCancellationPending)
        {
            return;
        }
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
        CancelTemporaryCanvasPan();
        if (_lastMouse is not null
            || _freehandDrawing
            || _marqueeSelecting
            || _marqueeSelectionCancellationPending
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming)
        {
            FinishLostPointerCapture();
        }

        _pendingClickSelection = DrawingElementHit.None;
        CancelTraditionalPenPath();
        CancelPenCurve();
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _marqueeSelecting = false;
        _marqueeStart = null;
        _marqueeSelectionBase = [];
        _marqueeInstanceSelectionBase = [];
        _fillBoundaryLineLinks.Clear();
        _stage.ClearMarquee();
    }

    private void FinishPointerInteraction()
    {
        FinalizePendingMarqueeSelectionCancellation();
        ResetLineDragPreview();
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _selectedStart = null;
        _curveControlStart = null;
        _curveControl2Start = null;
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _lineEndpointEditStarts.Clear();
        _fillBoundaryLineLinks.Clear();
        _detachedSelectionForMove = false;
        _pointerHitWasAlreadySelected = false;
        _selectionWasEmptyOnPointerDown = false;
        _forceMarqueeOnPointerDown = false;
        _additiveSelection = false;
        _pendingClickSelection = DrawingElementHit.None;
        _marqueeSelectionBase = [];
        _marqueeInstanceSelectionBase = [];
        _undoCapturedForPointerEdit = false;
        _resizeStartCenter = null;
        _resizeStartSize = null;
        _activeHandle = EditHandleKind.None;
        _activeTransformHandle = TransformHandleKind.None;
        _sceneInstanceMoveActive = false;
        _sceneInstanceTimelineKeyframeEnsuredIds.Clear();
        if (_sceneInstanceTimelineDirty)
        {
            _sceneInstanceTimelineDirty = false;
            _timeline.RefreshTimeline();
        }
        if (_sceneInstancePreviewDirty)
        {
            RebuildEditableInstanceComposition();
            UpdateInspector();
        }
        if (_geometryDirty)
        {
            _scene.CompleteDeferredBuild();
            var fillsOverwritten = ApplySelectedFillOverwriteAfterGeometryEdit();
            var fillsChanged = MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
            var linesChanged = MergeCompatibleLinesAfterDrawingOperation();
            if (fillsOverwritten || fillsChanged || linesChanged) _hierarchyPanel.RefreshScene();
            _geometryDirty = false;
            UpdateInspector();
        }
        UpdateTransformOverlay();
        _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void StageDragEnter(object? sender, DragEventArgs e)
    {
        if (TryResolveDroppedSvgFile(e.Data, out _))
        {
            ClearDrawingObjectDragPreview();
            e.Effect = CanImportSvg() ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }

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
        if (TryResolveDroppedSvgFile(e.Data, out _))
        {
            ClearDrawingObjectDragPreview();
            e.Effect = CanImportSvg() ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }

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
                _frame,
                parentFps: _playbackSettings.Fps);
            _stage.BindDragPreviewScene(_dragPreviewStage);
            return;
        }

        var dx = world.X - _dragPreviewPosition.X;
        var dy = world.Y - _dragPreviewPosition.Y;
        if (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f) return;
        _dragPreviewStage.TranslateAllObjectsForPreview(dx, dy);
        _dragPreviewPosition = world;
        _stage.Invalidate();
    }

    private void QueueMoveSelectedFromPointer(PointF world)
    {
        if (!SelectedGeometryIsLineOnly())
        {
            MoveSelectedFromPointer(world);
            return;
        }

        _pendingLineDragWorld = world;
        if (_lineDragPreviewTimer.Enabled) return;
        ApplyPendingLineDragPreview();
        _lineDragPreviewTimer.Start();
    }

    private void TickLineDragPreview()
    {
        if (ApplyPendingLineDragPreview()) return;
        _lineDragPreviewTimer.Stop();
    }

    private bool ApplyPendingLineDragPreview() => ApplyPendingLineDragPreview(updateLinkedFillPreview: true);

    private bool ApplyPendingLineDragPreview(bool updateLinkedFillPreview)
    {
        if (_pendingLineDragWorld is not { } world) return false;
        _pendingLineDragWorld = null;
        MoveSelectedFromPointer(world, synchronizeLinkedFills: false);
        if (updateLinkedFillPreview) SynchronizeLinkedFillBoundariesForPreview();
        return true;
    }

    private void FlushLineDragPreview()
    {
        _lineDragPreviewTimer.Stop();
        ApplyPendingLineDragPreview(updateLinkedFillPreview: false);
        if (_linkedFillBoundaryPreviewDirty) SynchronizeLinkedFillBoundaries();
    }

    private void ResetLineDragPreview()
    {
        _lineDragPreviewTimer.Stop();
        _pendingLineDragWorld = null;
        _linkedFillBoundaryPreviewDirty = false;
        _lineEndpointSnapBuckets.Clear();
        _lineEndpointSnapCacheBounds = RectangleF.Empty;
        _lineEndpointSnapBucketSize = 0;
        _lineEndpointSnapCacheLayer = -1;
    }

    private bool SelectedGeometryIsLineOnly()
    {
        return _selectedObjects.Count > 0
            && _selectedObjects.All(index =>
                (uint)index < _scene.ObjectCount && _scene.ShapeKind[index] == ShapeKind.Line);
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
        if (TryResolveDroppedSvgFile(e.Data, out var svgFile))
        {
            ClearDrawingObjectDragPreview();
            ImportSvgFile(svgFile, DragEventWorldPosition(e));
            return;
        }

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
        var targetLayerId = container.Scene.ResolveInstanceLayerId(null);
        if (!_project.TryAddDrawingObjectInstance(
                container.Id,
                drawingObject.Id,
                world,
                targetLayerId,
                out var nestedInstance)
            || nestedInstance is null)
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("A drawing object cannot contain itself or create a recursive containment cycle."),
                UiLocalization.T("Drawing Object"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(nestedInstance);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects, _activeDrawingObjectIndex);
        UpdateInspector();
        _stage.Invalidate();
        var targetLayer = Array.IndexOf(container.Scene.LayerIds, nestedInstance.SceneLayerId);
        var targetLayerName = targetLayer >= 0 ? container.Scene.LayerNames[targetLayer] : nestedInstance.SceneLayerId;
        AppLog.Info($"Placed nested drawing object: {drawingObject.Name} -> {container.Name} / {targetLayerName}");
    }

    internal static bool TryResolveDroppedSvgFile(IDataObject? data, out string fileName)
    {
        fileName = "";
        if (data?.GetDataPresent(DataFormats.FileDrop) != true
            || data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length != 1
            || string.IsNullOrWhiteSpace(files[0])
            || !string.Equals(Path.GetExtension(files[0]), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = files[0];
        return true;
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

    private bool MergeSelectedFillsAfterGeometryEdit(bool connectNearby)
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

    private bool ApplySelectedFillOverwriteAfterGeometryEdit()
    {
        if (IsSceneCompositionContext()) return false;
        var selectedFills = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && IsFillShape(_scene.ShapeKind[index]))
            .Distinct()
            .ToArray();
        if (selectedFills.Length == 0) return false;

        var retainedKeys = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount && !IsFillShape(_scene.ShapeKind[index]))
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .ToHashSet();
        var geometryRevision = _scene.GeometryRevision;
        var overwritten = _scene.ApplyFillOverwriteToNewObjects(selectedFills, _frame);
        if (_scene.GeometryRevision == geometryRevision) return false;

        var retainedSelection = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => retainedKeys.Contains(new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index])))
            .Concat(overwritten.Where(index => (uint)index < _scene.ObjectCount))
            .Distinct()
            .ToArray();
        SetSelection(retainedSelection);
        return true;
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

    private bool FinalizeNewLineDrawingOperation(int newObject)
    {
        if ((uint)newObject >= _scene.ObjectCount || _scene.ShapeKind[newObject] != ShapeKind.Line) return false;
        var localBounds = _scene.GetObjectWorldBounds(newObject);
        var layer = _scene.ObjectLayer[newObject];
        var keyframeFrame = _scene.ObjectKeyframeFrame[newObject];
        var changed = MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope([newObject]));

        localBounds.Inflate(6, 6);
        var candidates = _scene.QueryObjects(localBounds, _frame)
            .Where(index => _scene.ShapeKind[index] == ShapeKind.Line
                && _scene.ObjectLayer[index] == layer
                && _scene.ObjectKeyframeFrame[index] == keyframeFrame)
            .Distinct()
            .ToArray();
        if (candidates.Length < 2) return changed;

        var selectedBefore = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        var primaryBefore = (uint)_selectedObject < _scene.ObjectCount ? _selectedObject : -1;
        var materialized = _scene.MaterializeLineIntersections(candidates, _frame);
        if (!materialized.Success || !materialized.Changed) return changed;

        IEnumerable<int> Remap(int source)
        {
            var splitResults = materialized.Parts
                .Where(part => part.Source.ObjectIndex == source)
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            if (splitResults.Length > 0) return splitResults;
            if ((uint)source < materialized.OldToNewObjectIndex.Length
                && materialized.OldToNewObjectIndex[source] >= 0)
            {
                return [materialized.OldToNewObjectIndex[source]];
            }
            return [];
        }

        if (selectedBefore.Length > 0)
        {
            var remapped = selectedBefore
                .Where(index => index != primaryBefore)
                .SelectMany(Remap)
                .Concat(primaryBefore >= 0 ? Remap(primaryBefore) : [])
                .Distinct()
                .ToArray();
            SetSelection(remapped);
        }
        return true;
    }

    private int[] NormalizeNewPaintObjects(IReadOnlyList<int> objectIndices)
    {
        var retained = objectIndices
            .Where(index => (uint)index < _scene.ObjectCount && IsFillShape(_scene.ShapeKind[index]))
            .Distinct()
            .ToArray();
        if (retained.Length == 0
            || !_scene.CanNormalizePaintInteractively(retained, _frame))
        {
            return retained;
        }

        retained = _scene.ApplyFillOverwriteToNewObjects(retained, _frame);
        return _scene.MergeSameColorFillsAroundNewObjects(retained, connectNearby: true, frame: _frame);
    }

    private int[] LocalLineMergeScope(IReadOnlyCollection<int> seedObjects)
    {
        var seeds = seedObjects
            .Where(index => (uint)index < _scene.ObjectCount
                && _scene.ShapeKind[index] == ShapeKind.Line
                && _scene.IsObjectActive(index, _frame))
            .Distinct()
            .ToArray();
        if (seeds.Length == 0) return [];
        if (_scene.ObjectCount > MaxInteractiveLineMergeSceneObjects) return seeds;

        var candidates = new HashSet<int>(seeds);
        foreach (var seed in seeds)
        {
            var bounds = _scene.GetObjectWorldBounds(seed);
            bounds.Inflate(6, 6);
            foreach (var candidate in _scene.QueryObjects(bounds, _frame))
            {
                if (_scene.ShapeKind[candidate] != ShapeKind.Line
                    || _scene.ObjectLayer[candidate] != _scene.ObjectLayer[seed]
                    || _scene.ObjectKeyframeFrame[candidate] != _scene.ObjectKeyframeFrame[seed])
                {
                    continue;
                }

                candidates.Add(candidate);
                if (candidates.Count > MaxInteractiveLineMergeCandidates) return seeds;
            }
        }

        return candidates.OrderBy(index => index).ToArray();
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

    private int FindActiveObjectByStackKey(DrawingStackKey key)
    {
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ObjectOrder[index] == key.Order
                && _scene.ObjectSubOrder[index].Equals(key.SubOrder)
                && _scene.IsObjectActive(index, _frame))
            {
                return index;
            }
        }

        return -1;
    }

    private void CompleteMarqueeSelection(Point endScreen)
    {
        FinalizePendingMarqueeSelectionCancellation();
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
            else if (!TrySetTopologyMarqueeSelection(bounds)
                && !TrySetNestedInstanceMarqueeSelection(bounds))
            {
                RestoreMarqueeBaseSelection();
            }
        }
        else
        {
            RestoreMarqueeBaseSelection();
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
        if (SelectedSceneInstances().Count > 0)
        {
            return DeleteSelectedNestedDrawingObjectInstances();
        }

        var targets = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        if (targets.Length == 0 && _selectedObject >= 0 && _selectedObject < _scene.ObjectCount) targets = new[] { _selectedObject };
        if (targets.Length == 0) return false;
        var snapshot = CreateCanvasMutationSnapshot(targets);

        if (_selectedElements.Count > 0)
        {
            var materialized = _scene.MaterializeSelectedParts(_selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success)
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return false;
            }

            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }

        if (_scene.RemoveObjects(targets) <= 0)
        {
            RestoreCanvasMutationSnapshot(snapshot);
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
        _curveControl2Start = null;
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
        CancelTraditionalPenPath();
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

    private bool TryHoldTemporaryCanvasPan()
    {
        if (_spacePanKeyDown) return _spacePanHeld;
        _spacePanKeyDown = true;
        var pointer = _stage.PointToClient(Cursor.Position);
        var pointerOverStage = _stage.ClientRectangle.Contains(pointer);
        var pointerInteractionActive = _lastMouse is not null
            || _freehandDrawing
            || _marqueeSelecting
            || _traditionalPenPointerDown
            || _penSegmentDragging
            || _gradientEditSnapshot is not null
            || _viewPanning
            || _viewZooming
            || _viewOrbiting
            || _viewReferencePanning
            || _viewReferenceZooming;
        if (!CanStartTemporaryCanvasPan(
                ContainsFocusedEditor(this),
                _stage.ContainsFocus,
                pointerOverStage,
                pointerInteractionActive)
            || _materialEditSession is not null)
        {
            return false;
        }

        _spacePanHeld = true;
        HideBrushColorPalette();
        ApplyTemporaryCanvasPanCursor();
        return true;
    }

    internal static bool CanStartTemporaryCanvasPan(
        bool editorFocused,
        bool stageFocused,
        bool pointerOverStage,
        bool pointerInteractionActive)
    {
        return !editorFocused
            && !pointerInteractionActive
            && (stageFocused || pointerOverStage);
    }

    private bool BeginTemporaryCanvasPanPointer(Point location)
    {
        if (!_spacePanHeld || _spacePanPointerActive) return false;
        _spacePanPointerActive = true;
        _stage.Capture = true;
        _lastMouse = location;
        _viewReferencePanning = IsScene3DView();
        _viewPanning = !_viewReferencePanning;
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private void EndTemporaryCanvasPanPointer()
    {
        if (!_spacePanPointerActive) return;
        _spacePanPointerActive = false;
        _lastMouse = null;
        _viewPanning = false;
        _viewReferencePanning = false;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void ReleaseTemporaryCanvasPan()
    {
        if (!_spacePanHeld && !_spacePanPointerActive) return;
        _spacePanHeld = false;
        EndTemporaryCanvasPanPointer();
        RefreshInteractionCursorAtPointer();
    }

    private void CancelTemporaryCanvasPan()
    {
        _spacePanKeyDown = false;
        ReleaseTemporaryCanvasPan();
    }

    private bool ApplyTemporaryCanvasPanCursor()
    {
        if (!_spacePanHeld && !_spacePanPointerActive) return false;
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        _stage.Cursor = _spacePanPointerActive ? Cursors.SizeAll : Cursors.Hand;
        return true;
    }

    private DrawingObjectInstanceDefinition? SelectedSceneInstance()
    {
        if (string.IsNullOrWhiteSpace(_selectedSceneInstanceId)) return null;
        return ActiveEditableInstances().FirstOrDefault(instance =>
            string.Equals(instance.Id, _selectedSceneInstanceId, StringComparison.Ordinal));
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> SelectedSceneInstances()
    {
        if (_selectedSceneInstanceIds.Count == 0) return [];
        return ActiveEditableInstances()
            .Where(instance => _selectedSceneInstanceIds.Contains(instance.Id))
            .ToArray();
    }

    private IReadOnlyList<DrawingObjectInstanceDefinition> ActiveEditableInstances()
    {
        return IsSceneCompositionContext()
            ? ActiveScene()?.Instances ?? []
            : ActiveDrawingObject()?.Instances ?? [];
    }

    private void RestoreTimelineInstanceSelection(
        IReadOnlyCollection<string> instanceIds,
        string? primaryInstanceId)
    {
        var resolved = ResolveTimelineInstanceSelection(ActiveEditableInstances(), instanceIds, primaryInstanceId);
        if (resolved.Instances.Length == 0) return;
        SetSceneInstanceSelection(resolved.Instances, resolved.Primary);
    }

    internal static (DrawingObjectInstanceDefinition[] Instances, DrawingObjectInstanceDefinition? Primary)
        ResolveTimelineInstanceSelection(
            IReadOnlyList<DrawingObjectInstanceDefinition> availableInstances,
            IReadOnlyCollection<string> instanceIds,
            string? primaryInstanceId)
    {
        ArgumentNullException.ThrowIfNull(availableInstances);
        ArgumentNullException.ThrowIfNull(instanceIds);
        if (instanceIds.Count == 0) return ([], null);

        var selectedIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        var instances = availableInstances
            .Where(instance => selectedIds.Contains(instance.Id))
            .ToArray();
        var primary = instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, primaryInstanceId, StringComparison.Ordinal));
        return (instances, primary ?? instances.LastOrDefault());
    }

    private VectorScene ActiveInstanceCompositionScene()
    {
        return IsSceneCompositionContext() ? _scene : _drawingObjectUnderlayStage;
    }

    private SceneCompositionResult ActiveInstanceCompositionResult()
    {
        return IsSceneCompositionContext() ? _sceneCompositionResult : _drawingObjectUnderlayResult;
    }

    private bool TryResolveSceneInstanceAt(PointF world, out DrawingObjectInstanceDefinition instance)
    {
        return TryResolveCompositionInstance(
            ActiveInstanceCompositionScene(),
            ActiveInstanceCompositionResult(),
            ActiveEditableInstances(),
            world,
            IsSceneCompositionContext() ? _frame : 0,
            SelectionToleranceWorld(),
            out instance);
    }

    internal static bool TryResolveCompositionInstance(
        VectorScene compositionScene,
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        PointF world,
        int frame,
        float toleranceWorld,
        out DrawingObjectInstanceDefinition instance)
    {
        ArgumentNullException.ThrowIfNull(compositionScene);
        ArgumentNullException.ThrowIfNull(compositionResult);
        ArgumentNullException.ThrowIfNull(instances);
        instance = null!;
        var hit = compositionScene.HitTestElement(world, frame, toleranceWorld);
        if (!hit.IsValid || !compositionResult.TryGetOwner(hit.Key.ObjectIndex, out var owner)) return false;
        var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
        instance = instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, rootInstanceId, StringComparison.Ordinal))!;
        return instance is not null;
    }

    internal static IReadOnlyList<DrawingObjectInstanceDefinition> FindCompositionInstancesInsideBounds(
        VectorScene compositionScene,
        SceneCompositionResult compositionResult,
        IReadOnlyList<DrawingObjectInstanceDefinition> instances,
        RectangleF bounds)
    {
        ArgumentNullException.ThrowIfNull(compositionScene);
        ArgumentNullException.ThrowIfNull(compositionResult);
        ArgumentNullException.ThrowIfNull(instances);
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return [];

        var boundsByInstanceId = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        for (var objectIndex = 0; objectIndex < compositionScene.ObjectCount; objectIndex++)
        {
            if (!compositionResult.TryGetOwner(objectIndex, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            var objectBounds = compositionScene.GetObjectWorldBounds(objectIndex);
            boundsByInstanceId[rootInstanceId] = boundsByInstanceId.TryGetValue(rootInstanceId, out var current)
                ? RectangleF.Union(current, objectBounds)
                : objectBounds;
        }

        return instances
            .Where(instance => boundsByInstanceId.TryGetValue(instance.Id, out var instanceBounds)
                && bounds.Contains(instanceBounds.Left, instanceBounds.Top)
                && bounds.Contains(instanceBounds.Right, instanceBounds.Bottom))
            .ToArray();
    }

    private bool TryGetSelectedSceneInstanceBounds(out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        if (SelectedSceneInstances().Count == 0) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        var compositionScene = ActiveInstanceCompositionScene();
        var found = false;
        foreach (var index in _selectedSceneInstanceObjectIndices)
        {
            if ((uint)index >= compositionScene.ObjectCount) continue;
            var objectBounds = compositionScene.GetObjectWorldBounds(index);
            bounds = found ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            found = true;
        }

        return found && bounds.Width > 0.001f && bounds.Height > 0.001f;
    }

    private void RefreshSelectedSceneInstanceObjectIndices()
    {
        if (_selectedSceneInstanceIds.Count == 0)
        {
            _selectedSceneInstanceObjectIndices = [];
            return;
        }

        var compositionScene = ActiveInstanceCompositionScene();
        var compositionResult = ActiveInstanceCompositionResult();
        var indices = new List<int>();
        for (var index = 0; index < compositionScene.ObjectCount; index++)
        {
            if (!compositionResult.TryGetOwner(index, out var owner)) continue;
            var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            if (_selectedSceneInstanceIds.Contains(rootInstanceId)) indices.Add(index);
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

    private void SetSceneInstanceSelection(DrawingObjectInstanceDefinition instance, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(_selectedSceneInstanceId, instance.Id, StringComparison.Ordinal)) _transformFocus = null;
        if (!additive) _selectedSceneInstanceIds.Clear();
        _selectedSceneInstanceIds.Add(instance.Id);
        _selectedSceneInstanceId = instance.Id;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = -1;
        _selectedElement = DrawingElementHit.None;
        SyncSelectionToStage();
        RefreshSelectedSceneInstanceObjectIndices();
        UpdateSceneInstanceSelectionOverlay();
    }

    private void SetSceneInstanceSelection(
        IEnumerable<DrawingObjectInstanceDefinition> instances,
        DrawingObjectInstanceDefinition? primary = null)
    {
        var selected = instances
            .Where(instance => instance is not null)
            .DistinctBy(instance => instance.Id, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
        {
            ClearSelection();
            return;
        }

        _selectedSceneInstanceIds.Clear();
        foreach (var instance in selected) _selectedSceneInstanceIds.Add(instance.Id);
        var nextPrimary = primary is not null && _selectedSceneInstanceIds.Contains(primary.Id)
            ? primary
            : selected[^1];
        if (!string.Equals(_selectedSceneInstanceId, nextPrimary.Id, StringComparison.Ordinal)) _transformFocus = null;
        _selectedSceneInstanceId = nextPrimary.Id;
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
        _selectedSceneInstanceIds.Clear();
        _selectedSceneInstanceObjectIndices = [];
        _sceneInstanceMoveActive = false;
        _transformFocus = null;
        _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
    }

    private void UpdateSceneInstanceSelectionOverlay()
    {
        if (!TryGetSelectedSceneInstanceBounds(out var bounds))
        {
            _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
            return;
        }

        _stage.SetDrawingObjectSelectionOverlay(
            bounds,
            SelectedSceneInstance()?.EvaluatePosition(_frame));
    }

    private void SetSelection(int objectIndex)
    {
        ClearSceneInstanceSelection();
        _transformFocus = null;
        _selectedObjects.Clear();
        _selectedElements.Clear();
        _selectedObject = _scene.IsObjectSelectable(objectIndex, _frame) ? objectIndex : -1;
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
            || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame))
        {
            ClearSelection();
            return;
        }

        if (IsImportedSvgObject(hit.Key.ObjectIndex))
        {
            SetSelection(hit.Key.ObjectIndex);
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
        if (IsImportedSvgObject(hit.Key.ObjectIndex))
        {
            var importedObjects = _selectedObjects.ToList();
            if (!importedObjects.Remove(hit.Key.ObjectIndex)) importedObjects.Add(hit.Key.ObjectIndex);
            if (importedObjects.Count == 0) ClearSelection();
            else SetSelection(importedObjects);
            return;
        }
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
            if (!_scene.IsObjectSelectable(index, _frame) || !seen.Add(index)) continue;
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
                || !_scene.IsObjectSelectable(hit.Key.ObjectIndex, _frame)
                || !seenKeys.Add(hit.Key))
            {
                continue;
            }

            if (seenObjects.Add(hit.Key.ObjectIndex)) _selectedObjects.Add(hit.Key.ObjectIndex);
            if (!IsImportedSvgObject(hit.Key.ObjectIndex)) _selectedElements.Add(hit);
        }

        _selectedElement = primary.IsValid
            && !IsImportedSvgObject(primary.Key.ObjectIndex)
            && _selectedElements.Any(hit => hit.Key == primary.Key)
            ? _selectedElements.First(hit => hit.Key == primary.Key)
            : _selectedElements.Count > 0 ? _selectedElements[^1] : DrawingElementHit.None;
        _selectedObject = primary.IsValid && _selectedObjects.Contains(primary.Key.ObjectIndex)
            ? primary.Key.ObjectIndex
            : _selectedElement.IsValid
                ? _selectedElement.Key.ObjectIndex
                : _selectedObjects.Count > 0 ? _selectedObjects[^1] : -1;
        SyncSelectionToStage();
    }

    private bool IsImportedSvgObject(int objectIndex)
    {
        return (uint)objectIndex < _scene.ObjectCount
            && _scene.ShapeKind[objectIndex] == ShapeKind.ImportedSvg;
    }

    private void SyncSelectionToStage()
    {
        _stage.SetSelection(_selectedObjects, _selectedObject);
        _stage.SetSelectedElements(_selectedElements, _selectedElement);
        UpdateTraditionalPenPathHandleOverlay();
        UpdateTransformOverlay();
        UpdateGradientOverlay();
    }

    private void UpdateTraditionalPenPathHandleOverlay()
    {
        _stage.SetPenPathHandlesVisible(HasTraditionalPenPathSelection());
    }

    private void UpdateTransformOverlay()
    {
        var bounds = RectangleF.Empty;
        if (_tool == ToolMode.Transform)
        {
            if (SelectedSceneInstance() is not null)
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

        if (_undoStack.TryPeek(out var undo) && ReferenceEquals(undo.Snapshot, session.Snapshot)) _undoStack.Pop();
        _scene.RestoreSnapshot(session.Snapshot);
        _scene.EditFrame = _frame;
        _geometryDirty = false;
        _stage.ClearHoveredLineElement();
        return true;
    }

    private bool CanBreakApartObject(int objectIndex)
    {
        if ((uint)objectIndex >= _scene.ObjectCount
            || _scene.ShapeKind[objectIndex] != ShapeKind.ImportedSvg
            || !_scene.IsObjectActive(objectIndex, _frame))
        {
            return false;
        }

        var layer = _scene.ObjectLayer[objectIndex];
        return _scene.GetLayerKind(layer) == DrawingLayerKind.Drawing
            && !_scene.IsLayerEffectivelyLocked(layer);
    }

    private bool CanBreakApartInstance(DrawingObjectInstanceDefinition instance)
    {
        var container = ActiveDrawingObject();
        if (container is null || !container.Instances.Any(candidate => candidate.Id == instance.Id)) return false;
        var layer = Array.IndexOf(container.Scene.LayerIds, instance.SceneLayerId);
        return layer >= 0
            && container.Scene.GetLayerKind(layer) == DrawingLayerKind.Drawing
            && !container.Scene.IsLayerEffectivelyLocked(layer)
            && CanFlattenDrawingObject(instance.DrawingObjectId, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanFlattenDrawingObject(string drawingObjectId, ISet<string> visited)
    {
        if (!visited.Add(drawingObjectId)) return false;
        try
        {
            var drawingObject = _drawingObjects.FirstOrDefault(item => item.Id == drawingObjectId);
            return drawingObject is not null
                && !drawingObject.Scene.HasLayerEffects
                && drawingObject.Instances.All(instance => CanFlattenDrawingObject(instance.DrawingObjectId, visited));
        }
        finally
        {
            visited.Remove(drawingObjectId);
        }
    }

    private void BreakApartStageSelection()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || DrawingToolsBlocked()
            || IsSceneCompositionContext())
        {
            return;
        }
        FinishPointerInteractionForFrameChange();
        if (_stageContextMenuBreakApartInstances.Length > 0) BreakApartNestedInstances();
        else if (_stageContextMenuBreakApartObjects.Length > 0) BreakApartImportedSvgObjects();
    }

    private void BreakApartImportedSvgObjects()
    {
        var targets = _stageContextMenuBreakApartObjects
            .Where(CanBreakApartObject)
            .Distinct()
            .ToArray();
        if (targets.Length != _stageContextMenuBreakApartObjects.Length || targets.Length == 0) return;

        var targetKeys = targets
            .Select(index => new DrawingStackKey(_scene.ObjectOrder[index], _scene.ObjectSubOrder[index]))
            .ToArray();
        var layers = targets.Select(index => (int)_scene.ObjectLayer[index]).Distinct().ToArray();
        var wasProjectDirty = _projectDirty;
        var snapshot = CreateCanvasMutationSnapshot(targets, layers);
        (int[] ProducedObjects, ImportedSvgBreakApproximation Approximations) result;
        try
        {
            var materializedTargets = targetKeys
                .Select(FindActiveObjectByStackKey)
                .Where(index => CanBreakApartObject(index))
                .ToArray();
            if (materializedTargets.Length != targets.Length)
            {
                throw new InvalidOperationException("The selected SVG objects changed before Break Apart could run.");
            }

            result = _scene.BreakApartImportedSvgObjects(materializedTargets);
        }
        catch (Exception exception)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            SetProjectDirty(wasProjectDirty);
            ShowBreakApartError(exception);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(result.ProducedObjects);
        RefreshBreakApartPresentation();
        if (result.Approximations != ImportedSvgBreakApproximation.None)
        {
            AppLog.Info($"Break Apart SVG approximations: {result.Approximations}");
        }
    }

    private void BreakApartNestedInstances()
    {
        var container = ActiveDrawingObject();
        if (container is null) return;
        var requestedIds = _stageContextMenuBreakApartInstances.ToHashSet(StringComparer.Ordinal);
        var instances = container.Instances
            .Where(instance => requestedIds.Contains(instance.Id))
            .ToArray();
        if (instances.Length != requestedIds.Count || instances.Length == 0 || instances.Any(instance => !CanBreakApartInstance(instance)))
        {
            return;
        }

        var prepared = new List<(DrawingObjectInstanceDefinition Instance, int HostLayer, VectorScene Scene)>(instances.Length);
        try
        {
            foreach (var instance in instances)
            {
                var hostLayer = Array.IndexOf(container.Scene.LayerIds, instance.SceneLayerId);
                var flattened = new VectorScene();
                SceneCompositionBuilder.BuildDrawingObjectInstanceForBreakApart(
                    flattened,
                    container,
                    instance,
                    _drawingObjects,
                    _frame,
                    _playbackSettings.Fps);
                if (flattened.ObjectCount == 0) throw new InvalidDataException("The selected instance has no visible geometry at the current frame.");
                prepared.Add((instance, hostLayer, flattened));
            }
        }
        catch (Exception exception)
        {
            ShowBreakApartError(exception);
            return;
        }

        var instanceSnapshot = container.CreateInstanceSnapshot();
        var wasProjectDirty = _projectDirty;
        var hostLayers = prepared.Select(item => item.HostLayer).Distinct().ToArray();
        var sceneSnapshot = CreateCanvasMutationSnapshot(affectedLayers: hostLayers);
        int[] produced;
        try
        {
            using (_scene.Timeline.BeginBatchUpdate())
            {
                foreach (var hostLayer in hostLayers) _scene.MaterializeAutoKeyframeInPlace(hostLayer, _frame);
            }

            var producedObjects = new List<int>();
            foreach (var item in prepared)
            {
                producedObjects.AddRange(_scene.AppendFlattenedSceneToLayer(item.Scene, item.HostLayer, _frame));
            }
            foreach (var item in prepared)
            {
                var hasFutureVisibleState = item.Instance.StateKeyframes.Any(keyframe =>
                    keyframe.Frame > 0 && keyframe.State.Visible);
                if (_frame == 0 && !hasFutureVisibleState)
                {
                    if (!_project.TryRemoveDrawingObjectInstance(container.Id, item.Instance.Id, out _))
                    {
                        throw new InvalidOperationException("A selected drawing object instance could not be removed.");
                    }
                }
                else if (!item.Instance.SetStateAtFrame(
                             _frame,
                             item.Instance.EvaluateState(_frame) with { Visible = false }))
                {
                    throw new InvalidOperationException("A selected drawing object instance could not be hidden at the current frame.");
                }
            }
            produced = producedObjects.ToArray();
        }
        catch (Exception exception)
        {
            container.RestoreInstanceSnapshot(instanceSnapshot);
            _scene.RestoreSnapshot(sceneSnapshot);
            _scene.EditFrame = _frame;
            SetProjectDirty(wasProjectDirty);
            var restored = container.Instances.Where(instance => requestedIds.Contains(instance.Id)).ToArray();
            if (restored.Length > 0) SetSceneInstanceSelection(restored, restored[^1]);
            RefreshBreakApartPresentation();
            ShowBreakApartError(exception);
            return;
        }

        PushUndoSnapshot(sceneSnapshot, container, instanceSnapshot);
        SetSelection(produced);
        RefreshBreakApartPresentation();
        AppLog.Info($"Broke apart {prepared.Count} nested drawing object instance(s) into {produced.Length} local object(s)");
    }

    private void RefreshBreakApartPresentation()
    {
        _timeline.RefreshTimeline();
        _hierarchyPanel.RefreshScene();
        RefreshDrawingObjectAssetPresentation();
        _stage.ClearHoveredLineElement();
        UpdateInspector();
        UpdateStatusBar();
        _stage.Invalidate();
    }

    private void ShowBreakApartError(Exception exception)
    {
        AppLog.Error($"Break Apart failed: {exception}");
        var message = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese
            ? $"无法拆散所选对象。\r\n\r\n{exception.Message}"
            : $"The selected objects could not be broken apart.\r\n\r\n{exception.Message}";
        ModernMessageDialog.Show(
            this,
            message,
            UiLocalization.T("Break Apart"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private bool DeleteNestedDrawingObjectInstance(string instanceId)
    {
        if (IsSceneCompositionContext() || string.IsNullOrWhiteSpace(instanceId)) return false;
        var container = ActiveDrawingObject();
        if (container is null
            || !_project.TryRemoveDrawingObjectInstance(container.Id, instanceId, out var removed)
            || removed is null)
        {
            return false;
        }

        ClearSelection();
        _timeline.RefreshTimeline();
        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Deleted nested drawing object instance: {removed.Name}");
        return true;
    }

    private bool DeleteSelectedNestedDrawingObjectInstances()
    {
        if (IsSceneCompositionContext()) return false;
        var container = ActiveDrawingObject();
        var selected = SelectedSceneInstances();
        if (container is null || selected.Count == 0) return false;

        var removed = new List<DrawingObjectInstanceDefinition>(selected.Count);
        foreach (var instance in selected)
        {
            if (_project.TryRemoveDrawingObjectInstance(container.Id, instance.Id, out var deleted)
                && deleted is not null)
            {
                removed.Add(deleted);
            }
        }
        if (removed.Count == 0) return false;

        ClearSelection();
        _timeline.RefreshTimeline();
        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Deleted {removed.Count} nested drawing object instance(s)");
        return true;
    }

    private void ShowStageContextMenu(Point screen)
    {
        _stageContextMenuLocation = screen;
        _stageContextMenu.Show(_stage, screen);
    }

    private bool TryGetStageContextDrawingElement(Point screen, out DrawingElementHit hit)
    {
        hit = DrawingElementHit.None;
        if (DrawingToolsBlocked()) return false;
        var candidate = _scene.HitTestElement(
            _stage.ScreenToWorld(screen),
            _frame,
            SelectionToleranceWorld());
        if (!candidate.IsValid || !_scene.IsObjectSelectable(candidate.Key.ObjectIndex, _frame)) return false;
        hit = candidate;
        return true;
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

    private bool TryGetStageContextNestedInstance(
        Point screen,
        out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        if (IsSceneCompositionContext() || ActiveDrawingObject()?.Instances.Count is not > 0) return false;

        var world = _stage.ScreenToWorld(screen);
        var localHit = _scene.HitTestElement(world, _frame, SelectionToleranceWorld());
        if (localHit.IsValid && _scene.IsObjectSelectable(localHit.Key.ObjectIndex, _frame)) return false;
        return TryResolveSceneInstanceAt(world, out instance);
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

        var snapshot = CreateCanvasMutationSnapshot(targets);
        var fillIndices = new List<int>(targets.Length);
        foreach (var target in targets)
        {
            if (_scene.TryConvertLineToFill(target, _frame, out var fillIndex))
            {
                fillIndices.Add(fillIndex);
                continue;
            }

            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

        PushUndoSnapshot(snapshot);
        SetSelection(fillIndices);
        _stage.ClearHoveredLineElement();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TrySetNestedInstanceMarqueeSelection(RectangleF bounds)
    {
        if (IsSceneCompositionContext() || bounds.Width <= 0.001f || bounds.Height <= 0.001f) return false;
        var selected = FindCompositionInstancesInsideBounds(
            ActiveInstanceCompositionScene(),
            ActiveInstanceCompositionResult(),
            ActiveEditableInstances(),
            bounds);
        if (_additiveSelection && _marqueeInstanceSelectionBase.Length > 0)
        {
            var baseIds = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
            selected = ActiveEditableInstances()
                .Where(instance => baseIds.Contains(instance.Id) || selected.Contains(instance))
                .ToArray();
        }
        if (selected.Count == 0) return false;
        SetSceneInstanceSelection(selected, selected[^1]);
        return true;
    }

    private void RestoreMarqueeBaseSelection()
    {
        if (!_additiveSelection)
        {
            ClearSelection();
            return;
        }

        if (_marqueeSelectionBase.Length > 0)
        {
            SetSelection(_marqueeSelectionBase);
            return;
        }

        var baseIds = _marqueeInstanceSelectionBase.ToHashSet(StringComparer.Ordinal);
        var instances = ActiveEditableInstances().Where(instance => baseIds.Contains(instance.Id)).ToArray();
        if (instances.Length > 0) SetSceneInstanceSelection(instances, instances[^1]);
        else ClearSelection();
    }

    private void MergeAndSimplifyLineSegments(Point screen)
    {
        if (DrawingToolsBlocked()) return;

        var targets = CollectLineMergeTargets(screen, out var lineWasHit);
        if (targets.Count < 2) return;
        if (lineWasHit) SetSelection(targets);

        var snapshot = CreateCanvasMutationSnapshot(targets);
        if (!MergeCompatibleLinesAfterDrawingOperation(targets))
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

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
        if (_selectedObjects.Any(index => !_scene.IsObjectSelectable(index, _frame)))
        {
            ClearSelection();
        }
    }

    private PointF SnapDrawingPoint(PointF point)
    {
        return _drawSettings.SnapPoint(point, _stage.AdaptiveGridSnapStep);
    }

    private void UpdateDrawingPreview(PointF start, PointF end, ToolMode tool)
    {
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        if (tool == ToolMode.Line)
        {
            start = ResolveDrawingLineEndpoint(start);
            end = ResolveDrawingLineEnd(start, end, temporarilySnapAngle: IsShiftPressed());
        }
        else
        {
            start = VectorUnits.Quantize(SnapDrawingPoint(start));
            end = VectorUnits.Quantize(SnapDrawingPoint(end));
            (start, end) = ResolveShapeDragBounds(
                start,
                end,
                _drawSettings.KeepAspectRatio || IsShiftPressed(),
                IsControlPressed());
        }

        _stage.SetDrawingPreview(
            start,
            end,
            shape,
            ActiveColor(),
            ActiveStrokeUnits(),
            ShapeVertexCountFor(shape));
    }

    internal static (PointF Start, PointF End) ResolveShapeDragBounds(
        PointF anchor,
        PointF pointer,
        bool keepAspectRatio,
        bool fromCenter)
    {
        var dx = pointer.X - anchor.X;
        var dy = pointer.Y - anchor.Y;
        if (keepAspectRatio)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = MathF.CopySign(side, dx == 0 ? 1 : dx);
            dy = MathF.CopySign(side, dy == 0 ? 1 : dy);
        }

        if (!fromCenter) return (anchor, new PointF(anchor.X + dx, anchor.Y + dy));
        return (
            new PointF(anchor.X - dx, anchor.Y - dy),
            new PointF(anchor.X + dx, anchor.Y + dy));
    }

    private int ShapeVertexCountFor(ShapeKind shape)
    {
        return shape switch
        {
            ShapeKind.Polygon => _drawSettings.PolygonSides,
            ShapeKind.Star => _drawSettings.StarPoints,
            _ => 0
        };
    }

    private void BeginTraditionalPenPoint(Point screen)
    {
        _stage.SetPenPathHandlesVisible(false);
        var world = _stage.ScreenToWorld(screen);
        var resolution = ResolvePenAnchorPoint(world);
        if (_traditionalPenCurrentAnchor is not { } current)
        {
            _traditionalPenFirstAnchor = resolution.Point;
            _traditionalPenCurrentAnchor = resolution.Point;
            _traditionalPenOutgoingHandle = null;
            BeginTraditionalPenPointerDrag(screen);
            UpdatePenAnchorGuides(resolution);
            UpdateTraditionalPenDrawingPreview(world);
            return;
        }

        if (IsAltPressed()
            && Distance(current, resolution.Point) <= Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                _stage.ScreenLengthToWorld(9)))
        {
            _traditionalPenOutgoingHandle = null;
            UpdateTraditionalPenDrawingPreview(world);
            return;
        }

        _traditionalPenClosing = _traditionalPenSegmentCount > 0
            && _traditionalPenFirstAnchor is { } first
            && Distance(world, first) <= Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                _stage.ScreenLengthToWorld(10));
        if (_traditionalPenClosing && _traditionalPenFirstAnchor is { } firstAnchor)
        {
            resolution = new PenAnchorSnapResult(firstAnchor, true, false, false);
        }
        else
        {
            resolution = ApplyPenAngleSnap(current, resolution);
            if (IsShiftPressed() && !resolution.ObjectSnapped && !resolution.AlignX && !resolution.AlignY)
            {
                resolution = resolution with
                {
                    Point = ApplyDrawingLineAngleSnap(current, resolution.Point, temporarilySnapAngle: true)
                };
            }
        }

        if (Distance(current, resolution.Point) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            _traditionalPenClosing = false;
            return;
        }

        _traditionalPenPendingAnchor = resolution.Point;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        BeginTraditionalPenPointerDrag(screen);
        UpdatePenAnchorGuides(resolution);
        UpdateTraditionalPenDrawingPreview(world);
    }

    private void BeginTraditionalPenPointerDrag(Point screen)
    {
        _traditionalPenPointerDown = true;
        _traditionalPenPointerDownScreen = screen;
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = _stage.ScreenToWorld(screen);
    }

    private void UpdateTraditionalPenHandles(Point screen)
    {
        if (!_traditionalPenPointerDown
            || _traditionalPenPointerDownScreen is not { } pointerDown
            || Math.Abs(screen.X - pointerDown.X) + Math.Abs(screen.Y - pointerDown.Y) <= 3)
        {
            return;
        }

        var anchor = _traditionalPenPendingAnchor ?? _traditionalPenCurrentAnchor;
        if (anchor is not { } handleAnchor) return;
        var pointer = VectorUnits.Quantize(SnapDrawingPoint(_stage.ScreenToWorld(screen)));
        if (IsShiftPressed())
        {
            pointer = ApplyDrawingLineAngleSnap(handleAnchor, pointer, temporarilySnapAngle: true);
        }

        if (_traditionalPenPendingAnchor is null)
        {
            _traditionalPenOutgoingHandle = pointer;
            UpdateTraditionalPenDrawingPreview(pointer);
            return;
        }

        var mirrored = VectorUnits.Quantize(new PointF(
            handleAnchor.X * 2 - pointer.X,
            handleAnchor.Y * 2 - pointer.Y));
        if (IsAltPressed())
        {
            _traditionalPenLockedIncomingHandle ??= mirrored;
            _traditionalPenIncomingHandle = _traditionalPenLockedIncomingHandle;
            _traditionalPenPendingOutgoingHandle = pointer;
        }
        else
        {
            _traditionalPenLockedIncomingHandle = null;
            _traditionalPenIncomingHandle = mirrored;
            _traditionalPenPendingOutgoingHandle = pointer;
        }

        UpdateTraditionalPenDrawingPreview(pointer);
    }

    private void FinishTraditionalPenPoint(Point screen)
    {
        if (!_traditionalPenPointerDown) return;
        UpdateTraditionalPenHandles(screen);
        if (_traditionalPenPendingAnchor is not null) CommitTraditionalPenSegment();
    }

    private bool CommitTraditionalPenSegment()
    {
        if (_traditionalPenCurrentAnchor is not { } start
            || _traditionalPenPendingAnchor is not { } end
            || Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        var segment = TraditionalPenCubicSegment(
            start,
            _traditionalPenOutgoingHandle,
            _traditionalPenIncomingHandle,
            end);
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        var newObject = _scene.AddCubicCurveSegment(
            _scene.ActiveLayer,
            segment.Start,
            segment.Control1,
            segment.Control2,
            segment.End,
            ActiveStrokeUnits(),
            ActiveColor(),
            ActiveStrokeColor(),
            6);
        ClearSelection();
        FinalizeNewLineDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _traditionalPenSegmentCount++;
        _traditionalPenCurrentAnchor = end;
        _traditionalPenOutgoingHandle = _traditionalPenPendingOutgoingHandle;
        _traditionalPenPendingAnchor = null;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        _penAnchorCacheGeometryRevision = -1;
        _stage.Invalidate();

        if (_traditionalPenClosing) CancelTraditionalPenPath();
        return true;
    }

    internal static CubicDrawingPreviewSegment TraditionalPenCubicSegment(
        PointF start,
        PointF? outgoingHandle,
        PointF? incomingHandle,
        PointF end)
    {
        if (outgoingHandle is null && incomingHandle is null)
        {
            return new CubicDrawingPreviewSegment(
                start,
                Lerp(start, end, 1f / 3f),
                Lerp(start, end, 2f / 3f),
                end);
        }

        return new CubicDrawingPreviewSegment(
            start,
            outgoingHandle ?? start,
            incomingHandle ?? end,
            end);
    }

    private static PointF Midpoint(PointF first, PointF second) => new(
        (first.X + second.X) * 0.5f,
        (first.Y + second.Y) * 0.5f);

    private static PointF Lerp(PointF first, PointF second, float amount) => new(
        first.X + (second.X - first.X) * amount,
        first.Y + (second.Y - first.Y) * amount);

    private void UpdateTraditionalPenDrawingPreview(PointF world)
    {
        if (_traditionalPenCurrentAnchor is not { } start)
        {
            _stage.ClearDrawingPreview();
            _stage.ClearPenDirectionHandles();
            UpdatePenAnchorGuides(ResolvePenAnchorPoint(world));
            return;
        }

        if (_traditionalPenPendingAnchor is { } handleAnchor)
        {
            _stage.SetPenDirectionHandles(
                handleAnchor,
                _traditionalPenIncomingHandle,
                _traditionalPenPendingOutgoingHandle);
        }
        else
        {
            _stage.SetPenDirectionHandles(start, incoming: null, _traditionalPenOutgoingHandle);
        }

        var end = _traditionalPenPendingAnchor;
        PenAnchorSnapResult resolution = default;
        if (end is null)
        {
            resolution = ResolvePenAnchorPoint(world);
            if (_traditionalPenSegmentCount > 0
                && _traditionalPenFirstAnchor is { } first
                && Distance(world, first) <= Math.Max(
                    DrawingTopologyRules.MinStrokeSegmentUnits,
                    _stage.ScreenLengthToWorld(10)))
            {
                resolution = new PenAnchorSnapResult(first, true, false, false);
            }
            else
            {
                resolution = ApplyPenAngleSnap(start, resolution);
            }
            end = resolution.Point;
            UpdatePenAnchorGuides(resolution);
        }

        if (Distance(start, end.Value) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            _stage.ClearDrawingPreview();
            return;
        }

        var segment = TraditionalPenCubicSegment(
            start,
            _traditionalPenOutgoingHandle,
            _traditionalPenIncomingHandle,
            end.Value);
        var curved = _traditionalPenOutgoingHandle is not null || _traditionalPenIncomingHandle is not null;
        if (curved)
        {
            _stage.SetCubicCurveDrawingPreview(
                segment.Start,
                segment.Control1,
                segment.Control2,
                segment.End,
                ActiveStrokeColor(),
                ActiveStrokeUnits());
        }
        else
        {
            _stage.SetDrawingPreview(start, end.Value, ShapeKind.Line, ActiveStrokeColor(), ActiveStrokeUnits());
        }
    }

    private void UpdateTraditionalPenHover(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        var selectedOnly = !IsControlPressed();
        if ((!selectedOnly || HasTraditionalPenPathSelection())
            && TryFindTraditionalPenAnchor(world, selectedOnly, out _, out _, out var anchor))
        {
            _stage.ClearDrawingPreview();
            _stage.SetPenAnchorGuides(anchor, vertical: false, horizontal: false, snapped: true, insertion: false);
            return;
        }

        UpdateTraditionalPenDrawingPreview(world);
    }

    private bool TrySelectTraditionalPenPath(Point screen)
    {
        var hit = _scene.HitTestElement(_stage.ScreenToWorld(screen), _frame, SelectionToleranceWorld());
        var path = TraditionalPenPathElements(_scene, hit, _frame);
        if (path.Length == 0) return false;

        var primary = path.FirstOrDefault(candidate => candidate.Key == hit.Key);
        SetSelection(path, primary.IsValid ? primary : path[0]);
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        _stage.ClearPenDirectionHandles();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }

    internal static DrawingElementHit[] TraditionalPenPathElements(
        VectorScene scene,
        DrawingElementHit seed,
        int frame)
    {
        if (!seed.IsValid
            || seed.Key.Kind != DrawingElementKind.Stroke
            || (uint)seed.Key.ObjectIndex >= scene.ObjectCount
            || scene.ShapeKind[seed.Key.ObjectIndex] != ShapeKind.Line)
        {
            return [];
        }

        return scene.GetConnectedStrokeElements(seed, frame)
            .Where(hit => hit.Key.Kind == DrawingElementKind.Stroke
                && (uint)hit.Key.ObjectIndex < scene.ObjectCount
                && scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                && scene.IsObjectSelectable(hit.Key.ObjectIndex, frame))
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
    }

    private bool HasTraditionalPenPathSelection()
    {
        return _tool == ToolMode.Pen
            && _traditionalPenCurrentAnchor is null
            && _selectedElements.Any(hit => hit.Key.Kind == DrawingElementKind.Stroke
                && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
                && _scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line);
    }

    private bool TryBeginTraditionalPenAnchorEdit(Point screen)
    {
        return TryBeginTraditionalPenAnchorEdit(screen, selectedOnly: false);
    }

    private bool TryBeginTraditionalPenSelectedHandleEdit(Point screen)
    {
        return HasTraditionalPenPathSelection()
            && TryBeginTraditionalPenAnchorEdit(screen, selectedOnly: true);
    }

    private bool TryBeginTraditionalPenAnchorEdit(Point screen, bool selectedOnly)
    {
        var world = _stage.ScreenToWorld(screen);
        if (!TryFindTraditionalPenAnchor(world, selectedOnly, out var objectIndex, out var startEndpoint, out _))
        {
            if (TryFindTraditionalPenControl(screen, selectedOnly, out objectIndex, out var controlHandle))
            {
                BeginTraditionalPenGeometryEdit(screen, world, objectIndex, controlHandle);
                return true;
            }

            return !selectedOnly && TrySelectTraditionalPenPath(screen);
        }

        BeginTraditionalPenGeometryEdit(
            screen,
            world,
            objectIndex,
            startEndpoint ? EditHandleKind.LineStart : EditHandleKind.LineEnd);
        return true;
    }

    private void BeginTraditionalPenGeometryEdit(
        Point screen,
        PointF world,
        int objectIndex,
        EditHandleKind handle)
    {
        var selectedHit = _selectedElements.FirstOrDefault(hit => hit.Key.ObjectIndex == objectIndex);
        if (selectedHit.IsValid)
        {
            SetSelection(_selectedElements.ToArray(), selectedHit);
        }
        else
        {
            SetSelection(objectIndex);
        }
        _activeHandle = handle;
        _traditionalPenAnchorEditing = true;
        _traditionalPenAnchorEditObject = objectIndex;
        _traditionalPenAnchorEditStartEndpoint = handle == EditHandleKind.LineStart;
        _traditionalPenAnchorEditTracksCurrent = handle is EditHandleKind.LineStart or EditHandleKind.LineEnd
            && _traditionalPenCurrentAnchor is { } current
            && _scene.TryGetLineEndpoint(objectIndex, handle == EditHandleKind.LineStart, out var editAnchor)
            && Distance(current, editAnchor) <= EndpointConnectionToleranceUnits;
        if (_traditionalPenAnchorEditTracksCurrent)
        {
            _traditionalPenAnchorEditOriginalCurrent = _traditionalPenCurrentAnchor!.Value;
            _traditionalPenAnchorEditOriginalOutgoing = _traditionalPenOutgoingHandle;
        }
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = world;
        CaptureEditStart(objectIndex);
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool TryFindTraditionalPenControl(
        Point screen,
        bool selectedOnly,
        out int objectIndex,
        out EditHandleKind handle)
    {
        objectIndex = -1;
        handle = EditHandleKind.None;
        var bestDistance = 12f;
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || _scene.ObjectLayer[index] != _scene.ActiveLayer
                || !_scene.IsObjectActive(index, _frame)
                || selectedOnly && !_selectedObjects.Contains(index))
            {
                continue;
            }

            var control1 = _stage.WorldToScreen(_scene.CurveControlX[index], _scene.CurveControlY[index]);
            var dx1 = control1.X - screen.X;
            var dy1 = control1.Y - screen.Y;
            var distance1 = MathF.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (distance1 < bestDistance)
            {
                bestDistance = distance1;
                objectIndex = index;
                handle = EditHandleKind.BezierControl;
            }

            var control2 = _stage.WorldToScreen(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]);
            var dx2 = control2.X - screen.X;
            var dy2 = control2.Y - screen.Y;
            var distance2 = MathF.Sqrt(dx2 * dx2 + dy2 * dy2);
            if (distance2 < bestDistance)
            {
                bestDistance = distance2;
                objectIndex = index;
                handle = EditHandleKind.BezierControl2;
            }
        }

        return objectIndex >= 0;
    }

    private bool TryFindTraditionalPenAnchor(
        PointF world,
        bool selectedOnly,
        out int objectIndex,
        out bool startEndpoint,
        out PointF anchor)
    {
        objectIndex = -1;
        startEndpoint = false;
        anchor = PointF.Empty;
        var tolerance = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, _stage.ScreenLengthToWorld(10));
        var bestDistance = tolerance;
        for (var index = 0; index < _scene.ObjectCount; index++)
        {
            if (_scene.ShapeKind[index] != ShapeKind.Line
                || _scene.ObjectLayer[index] != _scene.ActiveLayer
                || !_scene.IsObjectActive(index, _frame)
                || selectedOnly && !_selectedObjects.Contains(index))
            {
                continue;
            }

            if (_scene.TryGetLineEndpoint(index, startEndpoint: true, out var startCandidate))
            {
                var distance = Distance(world, startCandidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    objectIndex = index;
                    startEndpoint = true;
                    anchor = startCandidate;
                }
            }

            if (_scene.TryGetLineEndpoint(index, startEndpoint: false, out var endCandidate))
            {
                var distance = Distance(world, endCandidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    objectIndex = index;
                    startEndpoint = false;
                    anchor = endCandidate;
                }
            }
        }

        return objectIndex >= 0;
    }

    private void CompleteTraditionalPenAnchorEdit()
    {
        FlushLineDragPreview();
        if (_traditionalPenAnchorEditTracksCurrent
            && (uint)_traditionalPenAnchorEditObject < _scene.ObjectCount
            && _scene.TryGetLineEndpoint(
                _traditionalPenAnchorEditObject,
                _traditionalPenAnchorEditStartEndpoint,
                out var movedAnchor))
        {
            var dx = movedAnchor.X - _traditionalPenAnchorEditOriginalCurrent.X;
            var dy = movedAnchor.Y - _traditionalPenAnchorEditOriginalCurrent.Y;
            _traditionalPenCurrentAnchor = movedAnchor;
            if (_traditionalPenFirstAnchor is { } first
                && Distance(first, _traditionalPenAnchorEditOriginalCurrent) <= EndpointConnectionToleranceUnits)
            {
                _traditionalPenFirstAnchor = movedAnchor;
            }
            if (_traditionalPenAnchorEditOriginalOutgoing is { } outgoing)
            {
                _traditionalPenOutgoingHandle = VectorUnits.Quantize(new PointF(outgoing.X + dx, outgoing.Y + dy));
            }
        }

        _traditionalPenAnchorEditing = false;
        _traditionalPenAnchorEditTracksCurrent = false;
        _traditionalPenAnchorEditObject = -1;
        _traditionalPenAnchorEditOriginalOutgoing = null;
        _penAnchorCacheGeometryRevision = -1;
        FinishPointerInteraction();
    }

    private void FinishTraditionalPenPointerDrag()
    {
        _traditionalPenPointerDown = false;
        _traditionalPenPointerDownScreen = null;
        _traditionalPenClosing = false;
        _lastMouse = null;
        _startScreen = null;
        _startWorld = null;
        _stage.Capture = false;
    }

    private bool CancelTraditionalPenPath()
    {
        var hadPath = _traditionalPenCurrentAnchor is not null
            || _traditionalPenPendingAnchor is not null
            || _traditionalPenAnchorEditing;
        var hadPointer = _traditionalPenPointerDown;
        var hadAnchorEdit = _traditionalPenAnchorEditing;
        _traditionalPenFirstAnchor = null;
        _traditionalPenCurrentAnchor = null;
        _traditionalPenOutgoingHandle = null;
        _traditionalPenPendingAnchor = null;
        _traditionalPenIncomingHandle = null;
        _traditionalPenPendingOutgoingHandle = null;
        _traditionalPenLockedIncomingHandle = null;
        _traditionalPenPointerDownScreen = null;
        _traditionalPenPointerDown = false;
        _traditionalPenClosing = false;
        _traditionalPenAnchorEditing = false;
        _traditionalPenAnchorEditTracksCurrent = false;
        _traditionalPenAnchorEditObject = -1;
        _traditionalPenAnchorEditOriginalOutgoing = null;
        _traditionalPenSegmentCount = 0;
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        _stage.ClearPenDirectionHandles();
        if (hadAnchorEdit)
        {
            FlushLineDragPreview();
            FinishPointerInteraction();
        }
        else if (hadPointer)
        {
            FinishTraditionalPenPointerDrag();
        }
        UpdateTraditionalPenPathHandleOverlay();
        return hadPath;
    }

    private void BeginPenSegment(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        var resolution = ResolvePenAnchorPoint(world);
        if (_penStartWorld is null)
        {
            _penStartWorld = resolution.Point;
            UpdatePenAnchorGuides(resolution);
            UpdatePenDrawingPreview(world);
            return;
        }

        resolution = ApplyPenAngleSnap(_penStartWorld.Value, resolution);
        var end = resolution.Point;
        if (Distance(_penStartWorld.Value, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return;
        _penEndWorld = end;
        _penControlWorld = null;
        _penEndpointScreen = screen;
        _penSegmentDragging = true;
        UpdatePenAnchorGuides(resolution);
        _stage.Capture = true;
        _lastMouse = screen;
        UpdatePenDrawingPreview(world);
    }

    private void UpdatePenControl(Point screen)
    {
        if (_penEndpointScreen is not { } anchor) return;
        if (Math.Abs(screen.X - anchor.X) + Math.Abs(screen.Y - anchor.Y) <= 3) return;
        _penControlWorld = VectorUnits.Quantize(SnapDrawingPoint(_stage.ScreenToWorld(screen)));
        UpdatePenDrawingPreview(_penControlWorld.Value);
    }

    private bool CommitPenSegment()
    {
        if (_penStartWorld is not { } start || _penEndWorld is not { } end) return false;
        if (Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits) return false;
        var control = _penControlWorld ?? new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
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
        FinalizeNewLineDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _penStartWorld = end;
        _penEndWorld = null;
        _penControlWorld = null;
        _stage.Invalidate();
        return true;
    }

    private void UpdatePenDrawingPreview(PointF world)
    {
        if (_penStartWorld is not { } start)
        {
            _stage.ClearDrawingPreview();
            UpdatePenAnchorGuides(ResolvePenAnchorPoint(world));
            return;
        }

        if (_penEndWorld is not { } end)
        {
            var resolution = ResolvePenAnchorPoint(world);
            resolution = ApplyPenAngleSnap(start, resolution);
            end = resolution.Point;
            UpdatePenAnchorGuides(resolution);
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
        var hadCurve = _penStartWorld is not null || _penEndWorld is not null;
        var hadPointerDrag = _penSegmentDragging;
        _penStartWorld = null;
        _penEndWorld = null;
        _penControlWorld = null;
        _penEndpointScreen = null;
        _penSegmentDragging = false;
        _stage.ClearDrawingPreview();
        _stage.ClearPenAnchorGuides();
        if (hadPointerDrag) FinishPenSegmentPointerDrag();
        return hadCurve;
    }

    private void UpdatePenHover(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        if (_penStartWorld is null
            && IsControlPressed()
            && TryGetPenAnchorInsertion(world, out _, out _, out var insertionPoint))
        {
            _stage.ClearDrawingPreview();
            _stage.SetPenAnchorGuides(insertionPoint, vertical: false, horizontal: false, snapped: false, insertion: true);
            return;
        }

        UpdatePenDrawingPreview(world);
    }

    private bool TryInsertPenAnchor(Point screen)
    {
        var world = _stage.ScreenToWorld(screen);
        if (!TryGetPenAnchorInsertion(world, out var objectIndex, out var parameter, out _)) return false;

        var undoSnapshot = CreateCanvasMutationSnapshot([objectIndex]);
        if (!_scene.SplitLineAt(objectIndex, parameter, out var split))
        {
            RestoreCanvasMutationSnapshot(undoSnapshot);
            return false;
        }
        PushUndoSnapshot(undoSnapshot);
        _penAnchorCacheGeometryRevision = -1;
        SetSelection([split.FirstObjectIndex, split.SecondObjectIndex]);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.SetPenAnchorGuides(split.Anchor, vertical: false, horizontal: false, snapped: true, insertion: true);
        _stage.Invalidate();
        return true;
    }

    private bool TryGetPenAnchorInsertion(
        PointF world,
        out int objectIndex,
        out float parameter,
        out PointF insertionPoint)
    {
        objectIndex = -1;
        parameter = 0;
        insertionPoint = PointF.Empty;
        var tolerance = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, _stage.ScreenLengthToWorld(9));
        var hit = _scene.HitTestElement(world, _frame, tolerance);
        if (!hit.IsValid
            || hit.Key.Kind != DrawingElementKind.Stroke
            || (uint)hit.Key.ObjectIndex >= _scene.ObjectCount
            || _scene.ShapeKind[hit.Key.ObjectIndex] != ShapeKind.Line
            || _scene.ObjectLayer[hit.Key.ObjectIndex] != _scene.ActiveLayer
            || !_scene.TryGetClosestPointOnLine(hit.Key.ObjectIndex, world, out parameter, out insertionPoint, out var distance)
            || distance > tolerance
            || parameter <= 0.025f
            || parameter >= 0.975f)
        {
            return false;
        }

        objectIndex = hit.Key.ObjectIndex;
        return true;
    }

    private PenAnchorSnapResult ResolvePenAnchorPoint(PointF world)
    {
        var objectSnapping = EndpointSnappingRequested();
        var alignment = _drawSettings.SnapEnabled && _drawSettings.AlignmentEnabled;
        var gridPoint = VectorUnits.Quantize(SnapDrawingPoint(world));
        if (!objectSnapping && !alignment) return new PenAnchorSnapResult(gridPoint, false, false, false);
        return ResolvePenAnchorSnap(
            world,
            PenAnchorCandidates(),
            Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(9)),
            gridPoint,
            objectSnapping,
            alignment);
    }

    private PenAnchorSnapResult ApplyPenAngleSnap(PointF start, PenAnchorSnapResult resolution)
    {
        if (resolution.ObjectSnapped || resolution.AlignX || resolution.AlignY) return resolution;
        return resolution with { Point = ApplyDrawingLineAngleSnap(start, resolution.Point, temporarilySnapAngle: false) };
    }

    internal static PenAnchorSnapResult ResolvePenAnchorSnap(
        PointF world,
        IReadOnlyList<PointF> anchors,
        float tolerance,
        PointF gridPoint,
        bool objectSnapping,
        bool alignment)
    {
        tolerance = Math.Max(0, tolerance);
        if (objectSnapping)
        {
            var bestDistance = tolerance;
            PointF? best = null;
            foreach (var anchor in anchors)
            {
                var distance = Distance(world, anchor);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = anchor;
            }

            if (best is { } snapped)
            {
                return new PenAnchorSnapResult(VectorUnits.Quantize(snapped), true, false, false);
            }
        }

        if (!alignment) return new PenAnchorSnapResult(VectorUnits.Quantize(gridPoint), false, false, false);
        var bestXDistance = tolerance;
        var bestYDistance = tolerance;
        var alignedX = gridPoint.X;
        var alignedY = gridPoint.Y;
        var alignX = false;
        var alignY = false;
        foreach (var anchor in anchors)
        {
            var xDistance = Math.Abs(world.X - anchor.X);
            if (xDistance < bestXDistance)
            {
                bestXDistance = xDistance;
                alignedX = anchor.X;
                alignX = true;
            }

            var yDistance = Math.Abs(world.Y - anchor.Y);
            if (yDistance >= bestYDistance) continue;
            bestYDistance = yDistance;
            alignedY = anchor.Y;
            alignY = true;
        }

        return new PenAnchorSnapResult(
            VectorUnits.Quantize(new PointF(alignedX, alignedY)),
            false,
            alignX,
            alignY);
    }

    private IReadOnlyList<PointF> PenAnchorCandidates()
    {
        if (ReferenceEquals(_penAnchorCacheScene, _scene)
            && _penAnchorCacheGeometryRevision == _scene.GeometryRevision
            && _penAnchorCacheLayer == _scene.ActiveLayer
            && _penAnchorCacheFrame == _frame)
        {
            return _penAnchorCache;
        }

        var anchors = new List<PointF>();
        for (var objectIndex = 0; objectIndex < _scene.ObjectCount; objectIndex++)
        {
            if (_scene.ShapeKind[objectIndex] != ShapeKind.Line
                || _scene.ObjectLayer[objectIndex] != _scene.ActiveLayer
                || !_scene.IsObjectActive(objectIndex, _frame))
            {
                continue;
            }

            if (_scene.TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start)) anchors.Add(start);
            if (_scene.TryGetLineEndpoint(objectIndex, startEndpoint: false, out var end)) anchors.Add(end);
        }

        _penAnchorCacheScene = _scene;
        _penAnchorCacheGeometryRevision = _scene.GeometryRevision;
        _penAnchorCacheLayer = _scene.ActiveLayer;
        _penAnchorCacheFrame = _frame;
        _penAnchorCache = anchors.ToArray();
        return _penAnchorCache;
    }

    private void UpdatePenAnchorGuides(PenAnchorSnapResult resolution)
    {
        if (!resolution.ObjectSnapped && !resolution.AlignX && !resolution.AlignY)
        {
            _stage.ClearPenAnchorGuides();
            return;
        }

        _stage.SetPenAnchorGuides(
            resolution.Point,
            vertical: resolution.AlignX,
            horizontal: resolution.AlignY,
            snapped: resolution.ObjectSnapped,
            insertion: false);
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
        var tipShape = _freehandBrushStroke || _freehandPressureBrush || _freehandErasing
            ? _brushShape
            : null;
        var previewLimit = tipShape is { IsTraditionalBrush: true, IsRadiallySymmetric: false }
            ? MaxStampBrushPreviewPoints
            : MaxFreehandPreviewPoints;
        if (_freehandPressureBrush)
        {
            var previewSamples = LimitDrawingPreview(_pressureBrushSamples, previewLimit);
            var profile = FreehandStrokeProcessor.CreatePressurePreview(
                previewSamples,
                _freehandStrokeUnits,
                _drawSettings.PressureBrushSmoothing);
            var pressurePreviewPoints = new PointF[profile.Length];
            var previewDiameters = new float[profile.Length];
            for (var index = 0; index < profile.Length; index++)
            {
                pressurePreviewPoints[index] = profile[index].Point;
                previewDiameters[index] = profile[index].Diameter;
            }
            if (TryUpdateShapeGradientBrushPreview(pressurePreviewPoints, previewSamples)) return;
            _stage.SetFreehandPreview(
                pressurePreviewPoints,
                _freehandColor,
                _freehandStrokeUnits,
                previewDiameters,
                tipShape);
            return;
        }

        var boundedPreviewPoints = LimitDrawingPreview(_freehandSamples, previewLimit);
        if (TryUpdateShapeGradientBrushPreview(boundedPreviewPoints)) return;
        _stage.SetFreehandPreview(boundedPreviewPoints, _freehandColor, _freehandStrokeUnits, brushShape: tipShape);
    }

    internal static IReadOnlyList<T> LimitDrawingPreview<T>(IReadOnlyList<T> source, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        maximumCount = Math.Max(2, maximumCount);
        if (source.Count <= maximumCount) return source;

        var result = new T[maximumCount];
        var scale = (source.Count - 1d) / (maximumCount - 1d);
        for (var index = 0; index < maximumCount; index++)
        {
            result[index] = source[(int)Math.Round(index * scale)];
        }
        return result;
    }

    private bool TryUpdateShapeGradientBrushPreview(
        IReadOnlyList<PointF> gradientPath,
        IReadOnlyList<PressureBrushSample>? pressureSamples = null)
    {
        if ((!_freehandBrushStroke && !_freehandPressureBrush)
            || _freehandErasing
            || gradientPath.Count == 0
            || _materialEditor.GetGradientPaintForTarget(strokeTarget: false).Kind != GradientKind.ShapeRadial)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        if (ReferenceEquals(_stage.DragPreviewScene, _shapeGradientBrushPreviewStage)
            && Stopwatch.GetElapsedTime(_shapeGradientPreviewUpdatedAt, now).TotalMilliseconds
                < ShapeGradientPreviewIntervalMilliseconds)
        {
            return true;
        }

        _shapeGradientBrushPreviewStage.CreateEmpty(Math.Max(1, _scene.LayerCount), Math.Max(1, _scene.FrameCount));
        _shapeGradientBrushPreviewStage.EditFrame = 0;
        var previewObjects = _freehandPressureBrush
            ? _shapeGradientBrushPreviewStage.AddPressureBrushStroke(
                _scene.ActiveLayer,
                pressureSamples ?? _pressureBrushSamples,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, _pressureBrushSamples.Count),
                _drawSettings.PressureBrushSmoothing,
                _stage.ScreenLengthToWorld(0.9f),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous)
            : _shapeGradientBrushPreviewStage.AddSoftBrushStroke(
                _scene.ActiveLayer,
                gradientPath,
                _freehandStrokeUnits,
                _freehandColor,
                _brushShape,
                (uint)Math.Max(3, gradientPath.Count),
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous);
        if (previewObjects.Length == 0)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        ApplyFillGradientToBrushObjects(
            _shapeGradientBrushPreviewStage,
            previewObjects,
            gradientPath,
            gradientPath[0],
            gradientPath[^1]);
        var matchingSources = _scene.FindIntersectingMatchingShapeGradientFills(
            _shapeGradientBrushPreviewStage,
            previewObjects,
            _frame,
            maximumResults: 3);
        if (matchingSources.Length == 0 || matchingSources.Length > 2)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        foreach (var source in matchingSources)
        {
            var contours = _scene.GetObjectBoundaryContours(source);
            var copy = _shapeGradientBrushPreviewStage.AddPathObjectContours(
                _scene.ObjectLayer[source],
                contours,
                0,
                Color.FromArgb(_scene.Argb[source]),
                Color.Transparent,
                Math.Max(3u, _scene.AtomCount[source]));
            if (copy < 0) continue;
            _shapeGradientBrushPreviewStage.SetGradientPaint(
                copy,
                GradientKind.ShapeRadial,
                _scene.GetGradientStops(source),
                _scene.GetGradientStart(source),
                _scene.GetGradientEnd(source));
            _shapeGradientBrushPreviewStage.SetShapeGradientMapping(copy, contours);
        }

        var mergedPreview = _shapeGradientBrushPreviewStage.ApplyFillOverwriteToNewObjects(previewObjects, 0);
        if (mergedPreview.Length == 0)
        {
            ClearShapeGradientBrushPreview();
            return false;
        }

        _shapeGradientPreviewUpdatedAt = now;
        _stage.ClearFreehandPreview();
        _stage.BindDragPreviewScene(_shapeGradientBrushPreviewStage, _scene, matchingSources);
        return true;
    }

    private void ClearShapeGradientBrushPreview()
    {
        _shapeGradientPreviewUpdatedAt = 0;
        if (ReferenceEquals(_stage.DragPreviewScene, _shapeGradientBrushPreviewStage))
        {
            _stage.BindDragPreviewScene(null);
        }
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
            var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
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
                var gradientPath = _pressureBrushSamples.Select(sample => sample.Point).ToArray();
                ApplyFillGradientToBrushObjects(
                    objects,
                    gradientPath,
                    _pressureBrushSamples[0].Point,
                    _pressureBrushSamples[^1].Point);
                NormalizeNewPaintObjects(objects);
                _materialEditor.RecordRecentFillColor(_freehandColor);
                ClearSelection();
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
            else
            {
                RestoreCanvasMutationSnapshot(snapshot);
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

        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
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
                ApplyFillGradientToBrushObjects(objects, points, points[0], points[^1]);
                NormalizeNewPaintObjects(objects);
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
                MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope([newObject]));
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
            }
        }

        CancelFreehandStroke();
        _stage.Invalidate();
    }

    private void ApplyFillGradientToBrushObjects(
        IReadOnlyList<int> objects,
        IReadOnlyList<PointF> brushPath,
        PointF brushStart,
        PointF brushEnd)
    {
        ApplyFillGradientToBrushObjects(_scene, objects, brushPath, brushStart, brushEnd);
    }

    private void ApplyFillGradientToBrushObjects(
        VectorScene targetScene,
        IReadOnlyList<int> objects,
        IReadOnlyList<PointF> brushPath,
        PointF brushStart,
        PointF brushEnd)
    {
        var gradient = _materialEditor.GetGradientPaintForTarget(strokeTarget: false);
        if (gradient.Kind == GradientKind.Solid) return;

        var sourceAlpha = Math.Max(1, (int)_freehandColor.A);
        var deltaX = brushEnd.X - brushStart.X;
        var deltaY = brushEnd.Y - brushStart.Y;
        PointF? gradientStart = deltaX * deltaX + deltaY * deltaY > 0.0001f ? brushStart : null;
        PointF? gradientEnd = gradientStart is null ? null : brushEnd;
        PointF[][] shapeMappingContours = [];
        if (gradient.Kind == GradientKind.ShapeRadial)
        {
            var mappingSource = objects.FirstOrDefault(index =>
                (uint)index < targetScene.ObjectCount && IsFillShape(targetScene.ShapeKind[index]), -1);
            if (mappingSource >= 0)
            {
                shapeMappingContours = targetScene.GetObjectBoundaryContours(mappingSource);
                var mappingPoints = shapeMappingContours.SelectMany(contour => contour).ToArray();
                if (mappingPoints.Length > 0)
                {
                    var left = mappingPoints.Min(point => point.X);
                    var right = mappingPoints.Max(point => point.X);
                    var top = mappingPoints.Min(point => point.Y);
                    var bottom = mappingPoints.Max(point => point.Y);
                    var center = VectorUnits.Quantize(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
                    gradientStart = center;
                    gradientEnd = GradientPaintUtilities.TryFindShapeBoundaryPoint(
                        shapeMappingContours,
                        center,
                        new PointF(1f, 0f),
                        out var boundary)
                        ? boundary
                        : new PointF(right, center.Y);
                }
            }
        }

        foreach (var objectIndex in objects)
        {
            if ((uint)objectIndex >= targetScene.ObjectCount || !IsFillShape(targetScene.ShapeKind[objectIndex])) continue;
            var layerAlpha = Color.FromArgb(targetScene.Argb[objectIndex]).A;
            var stops = GradientPaintUtilities.ScaleStopAlpha(gradient.Stops, layerAlpha / (float)sourceAlpha);
            targetScene.SetGradientPaint(objectIndex, gradient.Kind, stops, gradientStart, gradientEnd);
            if (gradient.Kind == GradientKind.Linear) targetScene.SetGradientPath(objectIndex, brushPath);
            else targetScene.ClearGradientPath(objectIndex);
            if (gradient.Kind == GradientKind.ShapeRadial && shapeMappingContours.Length > 0)
            {
                targetScene.SetShapeGradientMapping(objectIndex, shapeMappingContours);
            }
        }
    }

    private void EraseAlongBrushStroke(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return;
        var snapshot = _scene.CreateSnapshot();
        var autoKeyframe = AutomaticKeyframesEnabledForCurrentScene();
        var previousLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        if (!_scene.EraseWithBrushStroke(
                _frame,
                points,
                _freehandStrokeUnits,
                _brushShape,
                _drawSettings.EraseLines,
                _drawSettings.EraseFills,
                _drawSettings.BrushFrequency,
                _drawSettings.BrushContinuous,
                materializeAutoKeyframes: autoKeyframe)) return;

        ClearSelection();
        _stage.ClearHoveredLineElement();
        PushUndoSnapshot(snapshot);
        if (autoKeyframe) RefreshAutomaticKeyframePresentation(previousLastFrame);
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
        ClearShapeGradientBrushPreview();
        _stage.ClearFreehandPreview();
    }

    private float SelectionToleranceWorld() => Math.Max(4, _stage.ScreenLengthToWorld(10));

    private void CaptureEditStart(int objectIndex)
    {
        _selectedStart = new PointF(_scene.X[objectIndex], _scene.Y[objectIndex]);
        _curveControlStart = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        _curveControl2Start = new PointF(_scene.CurveControl2X[objectIndex], _scene.CurveControl2Y[objectIndex]);
        _resizeStartCenter = _selectedStart;
        _resizeStartSize = new SizeF(_scene.Width[objectIndex], _scene.Height[objectIndex]);
        _resizeStartAngle = _scene.Angle[objectIndex];
        _selectedMoveStarts.Clear();
        _selectedCurveStarts.Clear();
        _selectedCurve2Starts.Clear();
        _lineEndpointEditStarts.Clear();
        foreach (var index in _selectedObjects)
        {
            if ((uint)index >= _scene.ObjectCount) continue;
            _selectedMoveStarts[index] = new PointF(_scene.X[index], _scene.Y[index]);
            _selectedCurveStarts[index] = new PointF(_scene.CurveControlX[index], _scene.CurveControlY[index]);
            _selectedCurve2Starts[index] = new PointF(_scene.CurveControl2X[index], _scene.CurveControl2Y[index]);
            if (_scene.HasGradient(index))
            {
                _selectedGradientStarts[index] = (_scene.GetGradientStart(index), _scene.GetGradientEnd(index));
            }
        }

        if (!_selectedMoveStarts.ContainsKey(objectIndex))
        {
            _selectedMoveStarts[objectIndex] = _selectedStart.Value;
            _selectedCurveStarts[objectIndex] = _curveControlStart.Value;
            _selectedCurve2Starts[objectIndex] = _curveControl2Start.Value;
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
            var snapped = VectorUnits.Quantize(SnapDrawingPoint(world));
            _scene.CurveControlX[_selectedObject] = snapped.X;
            _scene.CurveControlY[_selectedObject] = snapped.Y;
            return;
        }

        if (_activeHandle == EditHandleKind.BezierControl2)
        {
            var snapped = VectorUnits.Quantize(SnapDrawingPoint(world));
            _scene.CurveControl2X[_selectedObject] = snapped.X;
            _scene.CurveControl2Y[_selectedObject] = snapped.Y;
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

        var queryBounds = new RectangleF(
            anchor.X - EndpointConnectionToleranceUnits,
            anchor.Y - EndpointConnectionToleranceUnits,
            EndpointConnectionToleranceUnits * 2,
            EndpointConnectionToleranceUnits * 2);
        foreach (var i in _scene.QueryObjects(queryBounds, _frame))
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

        BuildLineEndpointSnapCache(layer);
    }

    private void BuildLineEndpointSnapCache(int layer)
    {
        _lineEndpointSnapBuckets.Clear();
        _lineEndpointSnapCacheLayer = layer;
        _lineEndpointSnapBucketSize = Math.Max(EndpointConnectionToleranceUnits, _stage.ScreenLengthToWorld(10));
        var first = _stage.ScreenToWorld(Point.Empty);
        var second = _stage.ScreenToWorld(new Point(Math.Max(1, _stage.ClientSize.Width), Math.Max(1, _stage.ClientSize.Height)));
        _lineEndpointSnapCacheBounds = RectangleF.FromLTRB(
            Math.Min(first.X, second.X) - _lineEndpointSnapBucketSize,
            Math.Min(first.Y, second.Y) - _lineEndpointSnapBucketSize,
            Math.Max(first.X, second.X) + _lineEndpointSnapBucketSize,
            Math.Max(first.Y, second.Y) + _lineEndpointSnapBucketSize);
        var editedLines = _lineEndpointEditStarts.Select(edit => edit.ObjectIndex).ToHashSet();
        foreach (var candidate in _scene.QueryObjects(_lineEndpointSnapCacheBounds, _frame))
        {
            if (_scene.ShapeKind[candidate] != ShapeKind.Line
                || _scene.ObjectLayer[candidate] != layer
                || editedLines.Contains(candidate))
            {
                continue;
            }

            CacheLineEndpoint(candidate, startEndpoint: true);
            CacheLineEndpoint(candidate, startEndpoint: false);
        }
    }

    private void CacheLineEndpoint(int objectIndex, bool startEndpoint)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        var cell = LineEndpointSnapCell(endpoint);
        if (!_lineEndpointSnapBuckets.TryGetValue(cell, out var candidates))
        {
            candidates = new List<LineEndpointSnapCandidate>(2);
            _lineEndpointSnapBuckets.Add(cell, candidates);
        }

        candidates.Add(new LineEndpointSnapCandidate(objectIndex, endpoint));
    }

    private (int X, int Y) LineEndpointSnapCell(PointF point)
    {
        var size = Math.Max(0.001f, _lineEndpointSnapBucketSize);
        return ((int)MathF.Floor(point.X / size), (int)MathF.Floor(point.Y / size));
    }

    private void CaptureConnectedEndpoint(int objectIndex, bool startEndpoint, PointF anchor)
    {
        if (!_scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint)) return;
        if (Distance(endpoint, anchor) > EndpointConnectionToleranceUnits) return;
        if (!_scene.TryGetLineEndpoint(objectIndex, !startEndpoint, out var opposite)) return;
        var control1 = new PointF(_scene.CurveControlX[objectIndex], _scene.CurveControlY[objectIndex]);
        var control2 = new PointF(_scene.CurveControl2X[objectIndex], _scene.CurveControl2Y[objectIndex]);
        _lineEndpointEditStarts.Add(new LineEndpointEditStart(
            objectIndex,
            startEndpoint,
            endpoint,
            opposite,
            control1,
            control2,
            _scene.IsLineStraight(objectIndex)));
    }

    private void ApplyLineEndpointDrag(PointF world)
    {
        if (_lineEndpointEditStarts.Count == 0) return;
        var layer = _selectedObject >= 0 && _selectedObject < _scene.ObjectCount
            ? _scene.ObjectLayer[_selectedObject]
            : _scene.ActiveLayer;
        var (snapped, _) = ResolveLineEndpointSnap(world, layer, excludeEditedLines: true);

        foreach (var edit in _lineEndpointEditStarts)
        {
            var dx = snapped.X - edit.OriginalEndpoint.X;
            var dy = snapped.Y - edit.OriginalEndpoint.Y;
            var control1 = edit.StartEndpoint
                ? new PointF(edit.Control1.X + dx, edit.Control1.Y + dy)
                : edit.Control1;
            var control2 = edit.StartEndpoint
                ? edit.Control2
                : new PointF(edit.Control2.X + dx, edit.Control2.Y + dy);
            _scene.SetLineEndpoint(
                edit.ObjectIndex,
                edit.StartEndpoint,
                snapped,
                edit.OppositeEndpoint,
                control1,
                control2,
                edit.KeepStraight);
        }
    }

    private PointF ResolveDrawingLineEndpoint(PointF world)
    {
        return ResolveLineEndpointSnap(world, _scene.ActiveLayer, excludeEditedLines: false).Point;
    }

    private PointF ResolveDrawingLineEnd(PointF start, PointF world, bool temporarilySnapAngle = false)
    {
        var (candidate, snappedToObject) = ResolveLineEndpointSnap(
            world,
            _scene.ActiveLayer,
            excludeEditedLines: false);
        return snappedToObject
            ? candidate
            : ApplyDrawingLineAngleSnap(start, candidate, temporarilySnapAngle);
    }

    private (PointF Point, bool SnappedToObject) ResolveLineEndpointSnap(
        PointF world,
        int layer,
        bool excludeEditedLines)
    {
        var temporarilySnapToObjects = IsControlPressed();
        PointF? objectCandidate = null;
        if (EndpointSnappingRequested()
            && TrySnapToNearbyLineEndpoint(world, layer, excludeEditedLines, out var nearbyEndpoint))
        {
            objectCandidate = nearbyEndpoint;
        }

        return (
            _drawSettings.ResolvePointSnap(
                world,
                objectCandidate,
                temporarilySnapToObjects,
                _stage.AdaptiveGridSnapStep),
            objectCandidate.HasValue);
    }

    private PointF ApplyDrawingLineAngleSnap(PointF start, PointF end, bool temporarilySnapAngle)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= DrawingTopologyRules.UnitIntersectionTolerance) return end;
        var angle = _drawSettings.SnapAngle(MathF.Atan2(dy, dx), temporarilySnapAngle);
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
        if (excludeEditedLines
            && layer == _lineEndpointSnapCacheLayer
            && _lineEndpointSnapBucketSize > 0
            && _lineEndpointSnapCacheBounds.Contains(world))
        {
            var cell = LineEndpointSnapCell(world);
            for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
            {
                for (var x = cell.X - 1; x <= cell.X + 1; x++)
                {
                    if (!_lineEndpointSnapBuckets.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var candidate in candidates)
                    {
                        TrySnapToCachedEndpoint(candidate, world, ref snapped, ref bestDistance);
                    }
                }
            }

            return bestDistance < tolerance;
        }

        var queryBounds = new RectangleF(
            world.X - tolerance,
            world.Y - tolerance,
            tolerance * 2,
            tolerance * 2);
        var editedLines = excludeEditedLines
            ? _lineEndpointEditStarts.Select(edit => edit.ObjectIndex).ToHashSet()
            : null;
        foreach (var i in _scene.QueryObjects(queryBounds, _frame))
        {
            if (_scene.ShapeKind[i] != ShapeKind.Line
                || _scene.ObjectLayer[i] != layer
                || !_scene.IsObjectActive(i, _frame)
                || editedLines?.Contains(i) == true)
            {
                continue;
            }

            TrySnapToEndpoint(i, startEndpoint: true, world, ref snapped, ref bestDistance);
            TrySnapToEndpoint(i, startEndpoint: false, world, ref snapped, ref bestDistance);
        }

        return bestDistance < tolerance;
    }

    private static void TrySnapToCachedEndpoint(
        LineEndpointSnapCandidate candidate,
        PointF world,
        ref PointF best,
        ref float bestDistance)
    {
        var distance = Distance(world, candidate.Point);
        if (distance >= bestDistance) return;
        bestDistance = distance;
        best = candidate.Point;
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
        CaptureUndoSnapshot(affectedLayers: [_scene.ActiveLayer]);
        var shape = ToolShapeKind(tool) ?? _drawSettings.ShapeKind;
        int newObject;
        if (tool == ToolMode.Line)
        {
            start = ResolveDrawingLineEndpoint(start);
            end = ResolveDrawingLineEnd(start, end, temporarilySnapAngle: IsShiftPressed());
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
            start = VectorUnits.Quantize(SnapDrawingPoint(start));
            end = VectorUnits.Quantize(SnapDrawingPoint(end));
            (start, end) = ResolveShapeDragBounds(
                start,
                end,
                _drawSettings.KeepAspectRatio || IsShiftPressed(),
                IsControlPressed());
            var center = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
            var width = Math.Max(VectorUnits.FromPixels(4), Math.Abs(end.X - start.X));
            var height = Math.Max(VectorUnits.FromPixels(4), Math.Abs(end.Y - start.Y));
            newObject = _scene.AddObject(
                _scene.ActiveLayer,
                center,
                new SizeF(width, height),
                0,
                ActiveStrokeUnits(),
                ActiveColor(),
                ActiveStrokeColor(),
                24,
                shape,
                ShapeVertexCountFor(shape));
        }

        if (newObject >= 0 && _materialEditor.GradientEnabled)
        {
            _scene.SetGradientPaint(newObject, _materialEditor.GradientKind, _materialEditor.GradientStops);
        }
        if (newObject >= 0 && tool != ToolMode.Line)
        {
            var overwritten = NormalizeNewPaintObjects([newObject]);
            if (overwritten.Length > 0) newObject = overwritten[0];
        }

        SetSelection(newObject);
        if (tool == ToolMode.Line) FinalizeNewLineDrawingOperation(newObject);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
    }

    private void ApplyDrawingObjectPlaybackSettings(DrawingObjectPlaybackSettingsChangedEventArgs settings)
    {
        var instances = SelectedSceneInstances();
        var changed = false;
        foreach (var instance in instances)
        {
            var state = instance.EvaluateState(_frame);
            if (state.PlaybackFps == settings.PlaybackFps
                && state.PlaybackMode == settings.PlaybackMode
                && state.HoldFrame == settings.HoldFrame)
            {
                continue;
            }

            EnsureInstanceStateTimelineKeyframe(instance);
            changed |= instance.SetStateAtFrame(_frame, state with
            {
                PlaybackFps = settings.PlaybackFps,
                PlaybackMode = settings.PlaybackMode,
                HoldFrame = settings.HoldFrame
            });
        }
        if (!changed) return;

        _sceneInstanceTimelineDirty = true;
        _timeline.RefreshTimeline();
        RebuildEditableInstanceComposition();
        UpdateInspector();
    }

    private void RestoreSelectedDrawingObjectOriginalSize()
    {
        var changed = false;
        foreach (var instance in SelectedSceneInstances())
        {
            EnsureInstanceStateTimelineKeyframe(instance);
            changed |= RestoreDrawingObjectOriginalSize(instance, _frame);
        }
        if (!changed) return;

        _sceneInstanceTimelineDirty = true;
        _timeline.RefreshTimeline();
        RebuildEditableInstanceComposition();
        UpdateInspector();
    }

    private void ApplySelectedDrawingObjectAnchor(PointF anchor)
    {
        var selected = SelectedSceneInstance();
        if (selected is null
            || !_project.TrySetDrawingObjectAnchor(selected.DrawingObjectId, anchor))
        {
            UpdateInspector();
            return;
        }

        RebuildEditableInstanceComposition();
        UpdateInspector();
    }

    internal static bool RestoreDrawingObjectOriginalSize(DrawingObjectInstanceDefinition instance, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = instance.EvaluateState(frame);
        if (Math.Abs(state.ScaleX - 1f) <= 0.0001f
            && Math.Abs(state.ScaleY - 1f) <= 0.0001f)
        {
            return false;
        }

        return instance.SetStateAtFrame(frame, state with { ScaleX = 1f, ScaleY = 1f });
    }

    private void UpdateInspector()
    {
        var basicInspectorScrollY = CaptureInspectorScrollPosition(_basicInspectorPage);
        var sceneInspectorScrollY = CaptureInspectorScrollPosition(_sceneEditPage);
        var drawingObjectPanelWasVisible = _drawingObjectInstancePanel.Visible;
        var drawingObjectPanelParent = _drawingObjectInstancePanel.Parent;
        var materialEditorHeight = _materialEditor.Height;
        var materialEditorWasVisible = _materialEditor.Visible;
        var materialEditorParent = _materialEditor.Parent;
        _basicInspectorPage.SuspendContentLayout();
        _sceneEditPage.SuspendContentLayout();
        try
        {
        _sceneEditorPanel.RefreshSceneStats();
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        UpdateLineEndpointStyleControl();
        var selectedSceneInstances = SelectedSceneInstances();
        _drawingObjectInstancePanel.Visible = false;
        _materialEditor.Visible = true;
        if (SelectedSceneInstance() is { } sceneInstance)
        {
            var sceneInstanceState = sceneInstance.EvaluateState(_frame);
            var drawingObject = _drawingObjects.FirstOrDefault(item =>
                string.Equals(item.Id, sceneInstance.DrawingObjectId, StringComparison.Ordinal));
            _drawingObjectInstancePanel.SetInstance(
                sceneInstanceState,
                drawingObject?.FrameCount ?? 1,
                selectedSceneInstances.Any(instance =>
                {
                    var state = instance.EvaluateState(_frame);
                    return Math.Abs(state.ScaleX - 1f) > 0.0001f
                        || Math.Abs(state.ScaleY - 1f) > 0.0001f;
                }),
                drawingObject?.Anchor ?? PointF.Empty,
                drawingObject is not null && selectedSceneInstances.All(instance =>
                    string.Equals(instance.DrawingObjectId, drawingObject.Id, StringComparison.Ordinal)));
            _drawingObjectInstancePanel.Visible = true;
            var drawingLayer = Array.IndexOf(_scene.LayerIds, sceneInstance.SceneLayerId);
            var layerName = IsSceneCompositionContext()
                ? ActiveScene()?.FindLayer(sceneInstance.SceneLayerId)?.Name
                : drawingLayer >= 0
                    ? _scene.LayerNames[drawingLayer]
                    : null;
            _selected.Text = selectedSceneInstances.Count > 1
                ? $"Selected: {selectedSceneInstances.Count} Drawing Object Instances"
                : $"Selected: {sceneInstance.Name}";
            _selectedLayer.Text = $"Layer: {layerName ?? "Missing layer"} / Drawing Object: {drawingObject?.Name ?? "Missing object"}";
            _selectedAtoms.Text = $"Transform: {sceneInstanceState.ScaleX:0.##}, {sceneInstanceState.ScaleY:0.##} / {sceneInstanceState.RotationZ:0.#} deg / skew {sceneInstanceState.SkewX:0.#}, {sceneInstanceState.SkewY:0.#}";
            return;
        }

        var validSelection = _selectedObjects.Where(index => index >= 0 && index < _scene.ObjectCount).ToArray();
        _materialEditor.Visible = !validSelection.Any(IsImportedSvgObject);
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
        var gradientStops = _scene.HasGradient(_selectedObject) ? _scene.GetGradientStops(_selectedObject) : [];
        var materialOpacity = gradientStops.Length > 0
            ? Color.FromArgb(gradientStops[0].Argb).A / 255f
            : shape is ShapeKind.Freeform or ShapeKind.Line
                ? stroke.A / 255f
                : fill.A / 255f;
        if (shape is ShapeKind.Freeform or ShapeKind.Line)
        {
            _materialEditor.SetMaterial(_materialEditor.Fill, stroke, inspectorStrokePoints, materialOpacity);
        }
        else if (shape == ShapeKind.BrushStroke)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, materialOpacity);
        }
        else if (brushFill)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, materialOpacity);
        }
        else
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), stroke, inspectorStrokePoints, materialOpacity);
        }

        _materialEditor.SetGradientPreviewTarget(strokeTarget: shape == ShapeKind.Line);

        if (SupportsGradient(shape))
        {
            _materialEditor.SetGradient(
                _scene.GetGradientKind(_selectedObject),
                _scene.HasGradient(_selectedObject)
                    ? _scene.GetGradientStops(_selectedObject)
                    : shape == ShapeKind.Line
                        ? [new GradientStop(0, stroke), new GradientStop(1, stroke)]
                        : [new GradientStop(0, fill), new GradientStop(1, stroke)]);
        }
        else
        {
            _materialEditor.SetGradient(GradientKind.Solid, [new GradientStop(0, _materialEditor.Fill), new GradientStop(1, _materialEditor.Stroke)]);
        }
        }
        finally
        {
            var drawingObjectLayoutChanged = drawingObjectPanelWasVisible != _drawingObjectInstancePanel.Visible;
            var materialEditorLayoutChanged = materialEditorHeight != _materialEditor.Height
                || materialEditorWasVisible != _materialEditor.Visible;
            var basicLayoutChanged = (drawingObjectLayoutChanged
                    && ReferenceEquals(drawingObjectPanelParent, _basicInspectorPage.Content))
                || (materialEditorLayoutChanged
                    && ReferenceEquals(materialEditorParent, _basicInspectorPage.Content));
            var sceneLayoutChanged = (drawingObjectLayoutChanged
                    && ReferenceEquals(drawingObjectPanelParent, _sceneEditPage.Content))
                || (materialEditorLayoutChanged
                    && ReferenceEquals(materialEditorParent, _sceneEditPage.Content));
            _sceneEditPage.ResumeContentLayout(performLayout: sceneLayoutChanged);
            _basicInspectorPage.ResumeContentLayout(performLayout: basicLayoutChanged);
            if (basicLayoutChanged)
            {
                RestoreInspectorScrollPosition(_basicInspectorPage, basicInspectorScrollY);
            }
            if (sceneLayoutChanged)
            {
                RestoreInspectorScrollPosition(_sceneEditPage, sceneInspectorScrollY);
            }
        }
    }

    private static int CaptureInspectorScrollPosition(ThemedScrollPanel page)
    {
        return page.ScrollPosition;
    }

    private void UpdateInspectorForTimelineLayerChange()
    {
        if (_selectedSceneInstanceIds.Count > 0
            || _selectedObjects.Count > 0
            || _selectedElements.Count > 0
            || _selectedObject >= 0)
        {
            return;
        }

        SetLabelText(_selected, "Selected: None");
        SetLabelText(
            _selectedLayer,
            _scene.LayerNames.Length > 0 && _scene.ActiveLayer >= 0 && _scene.ActiveLayer < _scene.LayerNames.Length
                ? $"Layer: {_scene.LayerNames[_scene.ActiveLayer]}"
                : "Layer: -");
        SetLabelText(_selectedAtoms, "Atoms: -");
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

        var snapshot = CreateCanvasMutationSnapshot(targets.Select(target => target.ObjectIndex));
        var changed = false;
        foreach (var target in targets)
        {
            changed |= _scene.SetLineEndpointStyle(target.ObjectIndex, target.StartEndpoint, endpointStyle);
        }

        if (!changed)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
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
                if (_scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    _scene.StrokeArgb[objectIndex] = _materialEditor.Stroke.ToArgb();
                }
                else
                {
                    _scene.Argb[objectIndex] = _materialEditor.Fill.ToArgb();
                }
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

            var representativeColor = gradient.Stops.Length > 0
                ? Color.FromArgb(gradient.Stops[0].Argb)
                : _scene.ShapeKind[objectIndex] == ShapeKind.Line
                    ? _materialEditor.Stroke
                    : _materialEditor.Fill;
            if (_scene.ShapeKind[objectIndex] == ShapeKind.Line)
            {
                _scene.StrokeArgb[objectIndex] = representativeColor.ToArgb();
            }
            else
            {
                _scene.Argb[objectIndex] = representativeColor.ToArgb();
            }

            changed = true;
        }

        if (!changed)
        {
            if (_materialEditSession is null) RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
        PushMaterialUndoSnapshot(snapshot);
        UpdateGradientOverlay();
        RefreshInspectorAfterMaterialChange();
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
            RestoreCanvasMutationSnapshot(snapshot);
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

        if (!changed)
        {
            if (_materialEditSession is null) RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
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
        if (shape == ShapeKind.ImportedSvg) return false;
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
        if (shape == ShapeKind.ImportedSvg) return false;
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

    internal bool ReloadModulesForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || plan.Modules == HotReloadModule.None) return false;
        if (InvokeRequired)
        {
            BeginInvoke(() => ReloadModulesForHotReload(plan));
            return true;
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
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Module reload failed: {plan.Modules}", ex);
            return false;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    internal bool RebuildWorkbenchForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || _restartRequested) return false;
        if (InvokeRequired)
        {
            BeginInvoke(() => RebuildWorkbenchForHotReload(plan));
            return true;
        }

        try
        {
            var handler = RestartRequested;
            if (handler is null) return false;
            StopPlayback();
            FinishPointerInteractionForFrameChange();
            HideToolFlyouts();
            var state = CaptureEditorRestartState();
            _restartRequested = true;
            AppLog.Info($"Rebuilding workbench after runtime hot reload: {plan.Modules}; types: {plan.UpdatedTypes}");
            handler(this, new EditorRestartRequestedEventArgs(state));
            return true;
        }
        catch (Exception ex)
        {
            _restartRequested = false;
            AppLog.Error($"Workbench rebuild failed during module hot reload: {plan.Modules}", ex);
            return false;
        }
    }

    internal bool RequestProcessRestartForHotReload()
    {
        if (IsDisposed || _restartRequested) return false;
        RequestEditorRestart();
        return _restartRequested;
    }

    internal void SetHotReloadStatus(HotReloadUiState state, long generation)
    {
        if (!IsModuleHotReloadEnabled() || IsDisposed) return;
        var text = state switch
        {
            HotReloadUiState.Applying => "Module Reloading",
            HotReloadUiState.Applied => "Module Reload Applied",
            HotReloadUiState.Recovering => "Module Reload Recovering",
            HotReloadUiState.Failed => "Module Reload Failed",
            _ => "Module Reload On"
        };
        _devReloadStatus.Text = $"{UiLocalization.T(text)} #{generation}";
        _devReloadStatus.ForeColor = state switch
        {
            HotReloadUiState.Applied => Theme.Accent,
            HotReloadUiState.Failed => Theme.Danger,
            HotReloadUiState.Recovering => Theme.Warning,
            _ => Theme.Muted
        };
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
        if (_marqueeSelecting || _marqueeSelectionCancellationPending) FinishLostPointerCapture();
        CancelTemporaryCanvasPan();
        if (view != WorkspaceView.BasicDrawing) HideBrushColorPalette();
        var basicDrawing = view == WorkspaceView.BasicDrawing;
        var sceneEdit = view == WorkspaceView.SceneEditor;
        PlaceDrawingObjectInstancePanel(sceneEdit);
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
            CancelTraditionalPenPath();
            CancelPenCurve();
            CancelFreehandStroke();
            _stage.ClearDrawingPreview();
        }

        RefreshToolButtons();
        _drawSettingsPanel.SetEraserOptionsVisible(_tool == ToolMode.Eraser);
        _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
        ApplyToolCursor();
    }

    private void PlaceDrawingObjectInstancePanel(bool sceneEdit)
    {
        var target = sceneEdit ? _sceneEditPage.Content : _basicInspectorPage.Content;
        if (!ReferenceEquals(_drawingObjectInstancePanel.Parent, target))
        {
            target.Controls.Add(_drawingObjectInstancePanel);
        }
        _drawingObjectInstancePanel.BringToFront();
        if (!sceneEdit) _materialEditor.BringToFront();
    }

    private static bool IsDrawingTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen or ToolMode.Pencil or ToolMode.Brush or ToolMode.PressureBrush;
    }

    private static bool IsBasicDrawingOnlyTool(ToolMode tool)
    {
        return IsDrawingTool(tool) || tool is ToolMode.Fill or ToolMode.InkBottle or ToolMode.Eyedropper or ToolMode.Gradient or ToolMode.Eraser;
    }

    private static bool IsStandardStrokeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen;
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
        return shape is not ShapeKind.Line
            and not ShapeKind.Freeform
            and not ShapeKind.BrushStroke
            and not ShapeKind.ImportedSvg;
    }

    private static bool SupportsGradient(ShapeKind shape) => IsFillShape(shape) || shape == ShapeKind.Line;

    private static bool IsShapeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star;
    }

    private static bool IsLineTool(ToolMode tool)
    {
        return tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen or ToolMode.Pencil;
    }

    private static bool UsesStrokeGradient(ToolMode tool) => tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen;

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
            ToolMode.SimplePen => SvgIconKind.SimplePen,
            ToolMode.Pencil => SvgIconKind.Pencil,
            ToolMode.Brush => SvgIconKind.Brush,
            ToolMode.PressureBrush => SvgIconKind.PressureBrush,
            ToolMode.Fill => SvgIconKind.Fill,
            ToolMode.InkBottle => SvgIconKind.InkBottle,
            ToolMode.Eyedropper => SvgIconKind.Eyedropper,
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
            ToolMode.SimplePen => "Simple Pen Tool",
            ToolMode.Pencil => "Pencil Tool",
            _ => "Line Tool"
        };
    }

    private static string ToolPairName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Transform => "Free Transform Tool",
            ToolMode.Fill => "Fill Tool",
            ToolMode.InkBottle => "Ink Bottle Tool",
            _ => "Select Tool"
        };
    }

    private sealed class ToolPairGroup(ToolMode[] tools)
    {
        public ToolMode[] Tools { get; } = tools;
        public ToolMode ActiveTool { get; set; } = tools[0];
        public SvgIconButton? ParentButton { get; set; }
        public FlowLayoutPanel? Flyout { get; set; }
        public Dictionary<ToolMode, Button> FlyoutButtons { get; } = new();
        public System.Windows.Forms.Timer HideTimer { get; } = new() { Interval = 100 };
        public DateTime HideAtUtc { get; set; }

        public bool Contains(ToolMode tool) => Tools.Contains(tool);
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
        var drawingToolsBlocked = DrawingToolsBlocked();
        _lastDrawingToolsBlocked = drawingToolsBlocked;
        foreach (var (tool, button) in _toolButtons)
        {
            var enabled = !drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool);
            button.Enabled = enabled;
            if (tool == _tool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        RefreshToolPairGroup(_selectionToolGroup, drawingToolsBlocked);
        RefreshToolPairGroup(_paintToolGroup, drawingToolsBlocked);

        if (_shapeToolButton is not null)
        {
            var enabled = !drawingToolsBlocked;
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
            var enabled = !drawingToolsBlocked;
            button.Enabled = enabled;
            if (tool == _activeShapeTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_lineToolButton is not null)
        {
            var enabled = !drawingToolsBlocked;
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
            var enabled = !drawingToolsBlocked;
            button.Enabled = enabled;
            if (tool == _activeLineTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_brushToolButton is not null)
        {
            var enabled = !drawingToolsBlocked;
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
            var enabled = !drawingToolsBlocked;
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

    private void RefreshToolPairGroup(ToolPairGroup group, bool drawingToolsBlocked)
    {
        var groupEnabled = group.Tools.Any(tool => !drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
        if (group.ParentButton is not null)
        {
            group.ParentButton.Enabled = groupEnabled;
            group.ParentButton.Icon = ToolIconKind(group.ActiveTool);
            group.ParentButton.Tag = group.ActiveTool;
            group.ParentButton.AccessibleName = ToolPairName(group.ActiveTool);
            if (group.Contains(_tool)) Theme.StyleActiveButton(group.ParentButton);
            else Theme.StyleButton(group.ParentButton);
            if (!groupEnabled) group.ParentButton.ForeColor = Color.FromArgb(120, Theme.Text);
            group.ParentButton.Invalidate();
        }

        foreach (var (tool, button) in group.FlyoutButtons)
        {
            var enabled = !drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool);
            button.Enabled = enabled;
            if (tool == group.ActiveTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }
    }

    private sealed class DrawingObjectNameDialog : ModernDialogForm
    {
        private readonly TextBox _input = new();

        private DrawingObjectNameDialog(string title, string label, string initialValue)
            : base(title, new Size(440, 224))
        {
            var prompt = new Label
            {
                Text = label,
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            DialogContent.Controls.Add(prompt);

            _input.Dock = DockStyle.Top;
            _input.Height = 30;
            _input.Margin = new Padding(0, 8, 0, 0);
            _input.Text = initialValue;
            _input.SelectAll();
            Theme.StyleTextBox(_input);
            DialogContent.Controls.Add(_input);
            _input.BringToFront();

            var save = AddDialogAction(
                "Rename",
                DialogResult.OK,
                DialogActionStyle.Primary,
                () => !string.IsNullOrWhiteSpace(_input.Text));
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);

            AcceptButton = save;
            CancelButton = cancel;
            Shown += (_, _) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
            UiLocalization.Watch(this);
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
