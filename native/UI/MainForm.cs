using System.Diagnostics;
using System.Numerics;
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

internal readonly record struct TimelineClipboardInstanceState(
    string InstanceId,
    string DrawingObjectId,
    InstanceFrameState State);

internal sealed record TimelineClipboardCell(
    int TrackOffset,
    int FrameOffset,
    int SourceLayer,
    int SourceFrame,
    TimelineKeyframeKind Kind,
    IReadOnlyDictionary<string, InstanceFrameState> InstanceStates,
    SceneLightKind? LightKind = null,
    SceneLightSettings? LightSettings = null)
{
    public IReadOnlyList<TimelineClipboardInstanceState> InstanceStateEntries { get; init; } = [];
}

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

internal sealed partial class MainForm : Form
{
    [Flags]
    private enum DeferredPresentationRefresh
    {
        None = 0,
        Inspector = 1 << 0,
        PenOverlay = 1 << 1,
        TransformOverlay = 1 << 2,
        GradientOverlay = 1 << 3,
        FillEdgeBezierOverlay = 1 << 4,
        Hierarchy = 1 << 5,
        Overlays = PenOverlay | TransformOverlay | GradientOverlay | FillEdgeBezierOverlay,
        All = Inspector | Overlays
    }

    private const int ResizeGripSize = 7;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
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
    private const float LineEndpointSnapRadiusPixels = 32f;
    private const float EndpointConnectionTolerancePixels = 5f;
    private const int MaxUndoSnapshots = 32;
    private const long MaxUndoSnapshotBytes = 128L * 1024 * 1024;
    private const float PasteOffsetUnits = 96f;
    private const float FreehandSampleSpacingPixels = 1.25f;
    private const int MaxFreehandPreviewPoints = 256;
    private const int MaxStampBrushPreviewPoints = 96;
    private const int MaxInteractiveLineMergeCandidates = 64;
    private const int MaxInteractiveLineMergeSceneObjects = 512;
    private const double ShapeGradientPreviewIntervalMilliseconds = 33;
    private const float DefaultTextAreaWidthPixels = 320f;
    private const float DefaultTextAreaHeightPixels = 64f;
    private const int VaultDrawerExpandedWidth = 306;
    private const int VaultDrawerMaximumPixelsPerTick = 64;
    private const double VaultDrawerAnimationMilliseconds = 160;
    private const int InspectorPanelExpandedWidth = 372;
    /// <summary>
    /// Height the timeline opens at: 108 px of chrome (64 header + 28 ruler + 16 scroll bar) plus six
    /// layer rows at 21 px. Four rows was too few to read a scene, and the old value was also spent
    /// as device pixels while the chrome scales with DPI, so high-DPI screens lost rows outright.
    /// </summary>
    private const int TimelinePanelDefaultHeight = 234;
    private const double WorkspacePanelAnimationMilliseconds = 180;

    private VectorProject _project = VectorProject.CreateEmpty();
    private string _projectManifestPath = "";
    private bool _projectDirty;
    private ApplicationSettings _applicationSettings = ApplicationSettingsStore.Load();
    private VectorScene _scene;
    private readonly Icon _applicationIcon = new(typeof(MainForm), "Application.ico");
    private readonly VectorScene _sceneEditStage = new();
    private readonly VectorScene _drawingObjectUnderlayStage = new();
    private readonly VectorScene _onionSkinStage = new();
    private readonly VectorScene _localOnionSkinStage = new();
    private readonly VectorScene _nestedOnionSkinStage = new();
    private readonly VectorScene _sceneOnionSkinStage = new();
    private readonly VectorScene _dragPreviewStage = new();
    private readonly VectorScene _brushAreaPreviewStage = new();
    private readonly VectorScene _shapeGradientBrushPreviewStage = new();
    private SceneCompositionResult _sceneCompositionResult = SceneCompositionResult.Empty;
    private readonly Dictionary<string, SceneDimension> _sceneViewDimensions = new(StringComparer.Ordinal);
    private bool _sceneCompositionCacheValid;
    private bool _lastSceneCompositionReused;
    private SceneDefinition? _sceneCompositionCacheScene;
    private int _sceneCompositionCacheFrame = -1;
    private decimal _sceneCompositionCacheFps;
    private (VectorScene Scene, long GeometryRevision, long SummaryRevision)[] _sceneCompositionSourceRevisions = [];
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
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();
    private bool? _lastDrawingToolsBlocked;
    private readonly ToolMode[] _shapeTools = [ToolMode.Rectangle, ToolMode.Ellipse, ToolMode.Triangle, ToolMode.Polygon, ToolMode.Star];
    private readonly ToolMode[] _lineTools = [ToolMode.Line, ToolMode.Pen, ToolMode.SimplePen, ToolMode.Pencil];
    private readonly ToolMode[] _brushTools = [ToolMode.Brush, ToolMode.PressureBrush, ToolMode.MixingBrush];
    private readonly ToolPairGroup _selectionToolGroup = new([ToolMode.Select, ToolMode.PolygonLasso, ToolMode.FreehandLasso, ToolMode.Transform, ToolMode.Transform3D, ToolMode.Distort]);
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
    private readonly System.Windows.Forms.Timer _instanceAppearancePreviewTimer = new() { Interval = 16 };
    private DateTime _shapeFlyoutHideAtUtc;
    private DateTime _lineFlyoutHideAtUtc;
    private DateTime _brushFlyoutHideAtUtc;
    private readonly Dictionary<string, Button> _drawingObjectTabButtons = new();
    private Button? _activeDrawingObjectTabButton;
    private SvgIconButton? _addDrawingObjectTabButton;
    private IReadOnlyList<SceneDefinition> _scenes => _project.Scenes;
    private IReadOnlyList<DrawingObjectDefinition> _drawingObjects => _project.DrawingObjects;
    private readonly AnimatedToolTip _toolTip = new();
    private readonly WorkspaceTabs _workspaceTabs = new();
    private readonly Panel _workspaceHeader = new();
    private readonly TableLayoutPanel _drawingObjectRow = new();
    private readonly FlowLayoutPanel _drawingObjectTabs = new();
    private readonly DrawSnappingStrip _drawSnappingStrip;
    private readonly TimelineStrip _timeline;
    private Size _timelinePanelMinimumSize;
    private TimelineFrameClipboard? _timelineClipboard;
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _renderFpsStatus = StatusLabel("Render FPS --");
    private readonly ToolStripStatusLabel _animationFpsStatus = StatusLabel("UPS --/300  Animation FPS 30");
    private readonly ToolStripStatusLabel _zoomStatus = StatusLabel("Zoom 100%");
    private readonly ToolStripStatusLabel _devReloadStatus = StatusLabel("Module Reload On");
    private Panel? _topBar;
    private Label? _topPlaybackFpsLabel;
    private WindowChromeButton? _restartWindowButton;
    private WindowChromeButton? _minimizeWindowButton;
    private WindowChromeButton? _maximizeButton;
    private WindowChromeButton? _closeWindowButton;
    private Label? _projectTitleLabel;
    private TextBox? _projectTitleEditor;
    private SvgIconButton? _propertiesPanelButton;
    private SvgIconButton? _timelinePanelButton;
    private readonly DashDock _dashDock = new();
    private readonly WorkspaceColorFlyout _workspaceColorFlyout = new();
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
    private bool _syncingEraserOptions;
    private readonly DrawSettingsPanel _drawSettingsPanel;
    private readonly ShapeSettingsPanel _shapeSettingsPanel;
    private readonly FlowLayoutPanel _eraserOptionsStrip = new();
    private readonly CheckBox _eraseStrokeOption = EraserTargetOption("Stroke", "Erase stroke geometry");
    private readonly CheckBox _eraseFillOption = EraserTargetOption("Fill", "Erase fill geometry");
    private readonly BrushTipPanel _brushTipPanel = new();
    private readonly MixingBrushSettingsPanel _mixingBrushSettingsPanel = new();
    private readonly TextSettingsPanel _textSettingsPanel = new();
    private readonly MaterialEditorPanel _materialEditor = new();
    private readonly DrawingObjectInstancePanel _drawingObjectInstancePanel = new();
    private readonly HierarchyPanel _hierarchyPanel = new();
    private readonly SceneEditorPanel _sceneEditorPanel = new();
    private readonly Button _sceneDimensionButton = new()
    {
        Text = "2D",
        Width = 44,
        Height = 28,
        AccessibleName = "Scene view",
        AccessibleDescription = "Current scene view is 2D. Activate to switch to 3D"
    };
    private readonly Button _sceneProjectionButton = new()
    {
        Text = "Orthographic",
        Width = 92,
        Height = 28,
        Visible = false,
        Enabled = false,
        AccessibleName = "Camera projection",
        AccessibleDescription = "Current camera projection is orthographic. Activate to switch to perspective"
    };
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
    private readonly ToolStripSeparator _symbolExportMenuSeparator = new();
    private readonly ToolStripMenuItem _exportSymbolSvgMenuItem = new("Export Symbol as SVG...");
    private readonly ToolStripMenuItem _exportSymbolFileMenuItem = new("Export Symbol File...");
    private readonly ToolStripMenuItem _importSymbolFileMenuItem = new("Import Symbol File...");
    private readonly Panel _inspectorHost = new();
    private readonly LayerBlendModePanel _layerBlendModePanel = new();
    private readonly ThemedScrollPanel _basicInspectorPage = new();
    private readonly TweenCurveEditorPanel _tweenCurveEditorPanel = new();
    private readonly SelectionSummaryPanel _objectInspector = new();
    private readonly ThemedScrollPanel _sceneEditPage = new();
    private readonly Panel _animationPage = new();
    private readonly FlowLayoutPanel _toolPalette = new();
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
    private readonly Dictionary<string, SceneInstanceTransformPreview> _sceneInstanceTransformPreviews = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sceneInstanceTimelineKeyframeEnsuredIds = new(StringComparer.Ordinal);
    private readonly HashSet<int> _sceneInstanceTimelineEditedFrames = [];
    private bool _sceneInstanceTimelineDirty;
    private string _dragPreviewDrawingObjectId = "";
    private PointF _dragPreviewPosition;
    // Imported-file drops (image / external SVG) preview a throwaway clone of the pending
    // object. The key identifies the file being dragged so the clone is rebuilt only when
    // the hovered file changes, not on every DragOver sample.
    private string _dragPreviewImportedKey = "";
    private VectorScene? _dragPreviewImportedScene;
    private readonly Dictionary<int, PointF> _selectedMoveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurveStarts = new();
    private readonly Dictionary<int, PointF> _selectedCurve2Starts = new();
    private readonly Dictionary<int, (PointF Start, PointF End)> _selectedGradientStarts = new();
    private readonly List<LineEndpointEditStart> _lineEndpointEditStarts = new();
    private readonly List<FillBoundaryLineLink> _fillBoundaryLineLinks = new();
    private readonly List<SharedBoundaryIntersection> _lineEndpointFillIntersections = new();
    private readonly Dictionary<(int X, int Y), List<LineEndpointSnapCandidate>> _lineEndpointSnapBuckets = new();
    private readonly Stack<DrawingUndoEntry> _undoStack = new();
    private readonly Stack<SceneTimelineUndoEntry> _sceneTimelineUndoStack = new();
    private OnionSkinRangeEditSession? _onionSkinRangeEditSession;
    private SceneOnionSkinRangeEditSession? _sceneOnionSkinRangeEditSession;
    private TweenCurveEditSession? _tweenCurveEditSession;
    private readonly List<ClipboardObject> _clipboardObjects = new();
    private readonly List<ClipboardDrawingObjectInstance> _clipboardDrawingObjectInstances = new();
    private readonly TextBox _textEditor = new()
    {
        Visible = false,
        Multiline = true,
        AcceptsReturn = true,
        AcceptsTab = false,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        HideSelection = false,
        ShortcutsEnabled = true,
        MaxLength = TextGeometry.MaximumContentCharacters,
        AccessibleName = "Text editor"
    };
    private VectorScene? _textEditScene;
    private TextObjectData? _textEditData;
    private int _textEditObject = -1;
    private int _textEditLayer = -1;
    private DrawingStackKey? _textEditStackKey;
    private PointF _textEditCenter;
    private PointF? _textEditTopLeft;
    private float _textEditScaleX = 1f;
    private float _textEditScaleY = 1f;
    private Color _textEditColor = Color.White;
    private Font? _textEditorOwnedFont;
    private bool _updatingTextEditor;
    private readonly List<PointF> _freehandSamples = new(1024);
    private readonly List<PressureBrushSample> _pressureBrushSamples = new(1024);
    private MixingBrushRegionAccumulator? _mixingBrushRegionAccumulator;
    private int _mixingBrushLayer = -1;
    private int _mixingBrushFrame = -1;
    private readonly List<PressureBrushPoint> _brushAreaPressureProfile = new(512);
    private readonly List<float> _brushAreaPressureDistances = new(512);
    private int _brushAreaPreviewSampleCount;
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
    private PathBezierSegmentPart? _freehandBezierEditStart;
    private int _freehandBezierEditObject = -1;
    private int _freehandBezierEditSegment = -1;
    private PointF? _resizeStartCenter;
    private SizeF? _resizeStartSize;
    private float _resizeStartAngle;
    private TextObjectData? _textAreaResizeStartData;
    private EditHandleKind _activeHandle = EditHandleKind.None;
    private TransformHandleKind _activeTransformHandle = TransformHandleKind.None;
    private DistortEnvelope _distortStartEnvelope;
    private DistortEnvelope _distortCurrentEnvelope;
    private TransformOverlayFrame _distortSourceFrame;
    private DistortHandleRef _activeDistortVisualHandle;
    private bool _distortVisualHandleActive;
    private bool _distortReplaceExistingWarp;
    private VectorScene? _distortPreviewScene;
    private readonly Dictionary<int, DistortWarp[]> _distortPreviewBaseStacks = new();
    private readonly HashSet<int> _distortPreviewReplaceLast = [];
    private bool _distortPreviewChanged;
    private RectangleF _transformCurrentBounds = RectangleF.Empty;
    private PointF _transformPivot;
    private PointF? _transformFocus;
    private VectorScene.TransformSession? _drawingTransformSession;
    private RectangleF _drawingTransformStartBounds = RectangleF.Empty;
    private PointF _drawingTransformStartPointer;
    private PointF? _drawingTransformStartFocus;
    private float _drawingTransformAccumulatedAngle;

    // Rotation is presented as a rigid body turn: the transform box captured at pointer-down is
    // rotated by the same accumulated angle as the geometry instead of being re-derived from the
    // rotated bounds on every move. Recomputing the frame each move made the box (and therefore
    // the pivot and handle positions) jitter, so the fingers/cursor could not track the handle.
    private bool _transformFrameFrozenForRotation;
    private TransformOverlayFrame _transformFrozenFrame;
    private PointF _transformFrozenPivot;
    private PointF? _transformFrozenFocus;
    private InstanceAppearanceEditSession? _instanceAppearanceEditSession;
    private bool _instanceAppearancePreviewPending;
    private GradientHandleKind _gradientHandle = GradientHandleKind.None;
    private int _gradientStopIndex = -1;
    private VectorSceneSnapshot? _gradientEditSnapshot;
    private bool _gradientEditChanged;
    private FillEdgeBezierArmedSession? _fillEdgeBezierArmedSession;
    private FillEdgeBezierEditSession? _fillEdgeBezierEditSession;
    private int _fillEdgeBezierArmGeneration;
    private LineBranchDragSession? _lineBranchDragSession;
    private ArcDragSession? _arcDragSession;
    private CornerDragSession? _cornerDragSession;
    private int _fillEdgeBezierActivePartIndex = -1;
    private int _fillEdgeBezierActivePieceIndex = -1;
    private (int ObjectIndex, int PartIndex)? _fillEdgePreSelect;
    private (int ObjectIndex, int PartIndex, PointF Start, PointF Control1, PointF Control2, PointF End, PointF PointerStart, VectorSceneSnapshot Snapshot)? _fillEdgeNoUiArc;
    private bool _fillEdgeSuppressOverlay;
    private (int ObjectIndex, int PartIndex)? _fillEdgeDetachPending;
    private (int NewObjectIndex, PointF LastWorld, VectorSceneSnapshot Snapshot)? _fillEdgeDetachMoving;
    private FillBezierSegmentPiece[] _fillEdgeBezierOverlayPieces = [];
    private FillEdgeBezierOverlaySegment[] _fillEdgeBezierOverlaySegments = [];
    private int _fillEdgeBezierOverlayBaseObject = -1;
    private PointF _fillEdgeBezierOverlayBasePosition;
    private PointF _transformLastPointer;
    private float _transformLastAngle;
    private bool _geometryDirty;
    private double _renderFps;
    private double _updatesPerSecond;
    private bool _hasRenderRateSample;
    private bool _hasUpdateRateSample;
    private double _playbackAccumulator;
    private bool _playbackWarmupPending;
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
    private PointF? _pendingFillEdgeBezierWorld;
    private PointF? _pendingDrawingTransformWorld;
    private Point? _pendingHoverScreen;
    private bool _pendingFreehandPreview;
    private DeferredPresentationRefresh _pendingPresentationRefresh;
    private bool _pendingInspectorRefreshLineEndpointStyles;
    private int _presentationRefreshGeneration;
    private int _postedPresentationRefreshGeneration = -1;
    private int _frameWorkGeneration;
    private int _postedFrameWorkGeneration = -1;
    private RectangleF _lineEndpointSnapCacheBounds;
    private float _lineEndpointSnapBucketSize;
    private int _lineEndpointSnapCacheLayer = -1;
    private bool _linkedFillBoundaryPreviewDirty;
    private bool _independentMarqueeStrokeMove;
    private VectorScene? _penAnchorCacheScene;
    private long _penAnchorCacheGeometryRevision = -1;
    private int _penAnchorCacheLayer = -1;
    private int _penAnchorCacheFrame = -1;
    private PointF[] _penAnchorCache = [];
    private ShapeKind _lastSettingsShape = ShapeKind.Rectangle;
    private int _activeSceneIndex;
    private int _activeDrawingObjectIndex;
    private bool _undoCapturedForPointerEdit;
    private VectorSceneSnapshot? _pointerUndoSnapshot;
    private VectorSceneSnapshot? _pointerUndoSnapshotWithSharedGeometry;
    private bool _pointerTopologyQueriesInvalidated;
    private bool _freehandDrawing;
    private bool _freehandBrushStroke;
    private bool _freehandPressureBrush;
    private bool _freehandMixingBrush;
    private bool _freehandErasing;
    private uint? _tabletPressurePointerId;
    private float _tabletPressureLastValue = float.NaN;
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
    private int _timelinePanelPreferredHeight = TimelinePanelDefaultHeight;
    private int _timelinePanelExpandedHeight = TimelinePanelDefaultHeight;
    private int _vaultDrawerExpandedWidth = VaultDrawerExpandedWidth;
    private int _inspectorPanelExpandedWidth = InspectorPanelExpandedWidth;
    private int _inspectorPanelAnimationFromWidth = InspectorPanelExpandedWidth;
    private int _inspectorPanelAnimationToWidth = InspectorPanelExpandedWidth;
    private int _timelinePanelAnimationFromHeight = TimelinePanelDefaultHeight;
    private int _timelinePanelAnimationToHeight = TimelinePanelDefaultHeight;
    private long _workspacePanelAnimationStartedTimestamp;
    private double _workspacePanelAnimationDurationMilliseconds = WorkspacePanelAnimationMilliseconds;
    private Color _freehandColor = Color.White;
    private float _freehandStrokeUnits;
    private int _freehandPencilSmoothing;
    private float _freehandPencilWorldPerPixel;
    private int _freehandPencilPreviewSampleCount;
    private long _freehandPencilPreviewUpdatedAt;
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
    private bool _lineEndpointFillIntersectionsCaptured;
    private bool _fillBoundaryLineLinksCaptured;

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
        DrawingObjectSnapPointDefinition[]? SnapPointSnapshot = null,
        int? PlayheadFrame = null,
        TimelineSelectionSnapshot? TimelineSelection = null,
        string? DrawingObjectName = null,
        string? CreatedDrawingObjectId = null,
        long MemoryBytes = 0,
        MarqueeMaterializationSession? MarqueeSession = null,
        VectorSceneSnapshot? MarqueeSnapshot = null);

    private sealed record SceneTimelineUndoEntry(
        SceneDefinition Scene,
        AnimationTimelineSnapshot Snapshot,
        SceneLayerSnapshot? LayerSnapshot = null,
        DrawingObjectInstanceDefinition[]? InstanceSnapshot = null,
        int? PlayheadFrame = null,
        TimelineSelectionSnapshot? TimelineSelection = null,
        string? CreatedDrawingObjectId = null,
        SceneLightDefinition[]? LightSnapshot = null,
        string? SelectedLightId = null,
        SceneOnionSkinState? OnionSkinState = null,
        SceneShotSnapshot[]? ShotSnapshot = null);

    private sealed class OnionSkinRangeEditSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public bool Changed { get; set; }
    }

    private sealed class SceneOnionSkinRangeEditSession
    {
        public required SceneDefinition Scene { get; init; }
        public required SceneOnionSkinState Snapshot { get; init; }
        public bool Changed { get; set; }
    }

    private sealed class TweenCurveEditSession
    {
        public required TweenCurveEditTarget Target { get; init; }
        public required TimelineSelectionSnapshot TimelineSelection { get; init; }
        public VectorSceneSnapshot? DrawingSnapshot { get; init; }
        public AnimationTimelineSnapshot? SceneTimelineSnapshot { get; init; }
        public DrawingObjectInstanceDefinition[]? InstanceSnapshot { get; init; }
        public SceneLightDefinition[]? LightSnapshot { get; init; }
        public SceneShotSnapshot[]? ShotSnapshot { get; init; }
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

    private sealed record SceneInstanceTransformPreview(
        InstanceFrameState StartState,
        VectorScene.TransformSession Session);

    private sealed class InstanceAppearanceEditSession
    {
        public required DrawingObjectInstanceDefinition[] InstanceSnapshot { get; init; }
        public required string[] SelectedInstanceIds { get; init; }
        public required string PrimaryInstanceId { get; init; }
        public SceneDefinition? Scene { get; init; }
        public AnimationTimelineSnapshot? TimelineSnapshot { get; init; }
        public DrawingObjectDefinition? DrawingObject { get; init; }
        public VectorSceneSnapshot? DrawingSceneSnapshot { get; init; }
        public bool Changed { get; set; }
    }

    private sealed record MarqueeMaterializationSession(
        VectorScene Scene,
        VectorSceneSnapshot Snapshot,
        int[] IndependentStrokeObjects);

    private sealed record FillPartSelectionIdentity(PointF[] InteriorProbes);

    private sealed class FillEdgeBezierArmedSession
    {
        public required VectorScene Scene { get; init; }
        public required long GeometryRevision { get; init; }
        public required DrawingStackKey StackKey { get; init; }
        public required int TargetObject { get; init; }
        public required FillBezierSegmentPiece OverlayPiece { get; init; }
        public required EditHandleKind Handle { get; init; }
        public required PointF PointerStart { get; init; }
        public required PointF LatestPointer { get; set; }
        public required Point LatestScreen { get; set; }
        public required FillEdgeBezierOverlaySegment[] PreviewSegments { get; init; }
        public required int PreviewSegmentIndex { get; init; }
        public required int Generation { get; init; }
        public required bool ResetCurvature { get; init; }
        public bool PointerMoved { get; set; }
        public bool CompleteAfterPreparation { get; set; }
        public bool PreparationPosted { get; set; }
    }

    private sealed class FillEdgeBezierEditSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public required DrawingStackKey StackKey { get; init; }
        public required FillPartSelectionIdentity? FillPartSelection { get; init; }
        public required int ObjectIndex { get; init; }
        public required int PartIndex { get; init; }
        public required EditHandleKind Handle { get; init; }
        public required PointF PointerStart { get; init; }
        public required PointF Start { get; init; }
        public required PointF Control1 { get; init; }
        public required PointF Control2 { get; init; }
        public required PointF End { get; init; }
        public required FillBoundaryStrokeLink[] LinkedStrokes { get; init; }
        public required SharedBoundaryIntersection SharedIntersection { get; init; }
        public required bool IncludesAnchorInsertion { get; init; }
        public required PointF[][] InitialFillContours { get; init; }
        public required CubicDrawingPreviewSegment LastAppliedSegment { get; set; }
        public bool BoundaryGeometryChanged { get; set; }
        public bool Changed { get; set; }
    }

    private sealed class LineBranchDragSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public required int ObjectIndex { get; init; }
        public required int Layer { get; init; }
        public required float SourceParameter { get; init; }
        public required PointF Anchor { get; init; }
        public required Color StrokeColor { get; init; }
        public required float Stroke { get; init; }
        public PointF End { get; set; }
        public bool DragExceeded { get; set; }
    }

    private sealed class ArcDragSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        public required int ObjectIndex { get; init; }
        public required PointF Start { get; init; }
        public required PointF End { get; init; }
        public required float Parameter { get; init; }
        public required FillBoundaryLineLink[] FillBoundaryLinks { get; init; }
        public bool DragExceeded { get; set; }
    }

    private sealed class CornerDragSession
    {
        public required VectorScene Scene { get; init; }
        public required VectorSceneSnapshot Snapshot { get; init; }
        /// <summary>First segment of a Line corner split, or the edited Freeform anchor owner.</summary>
        public required int FirstObjectIndex { get; init; }
        public required bool FirstSharedIsStart { get; init; }
        public required PointF FirstOppositeEndpoint { get; init; }
        /// <summary>
        /// Second segment of a Line corner split, or -1 while the session drives a Freeform anchor by
        /// node index instead.
        /// </summary>
        public required int SecondObjectIndex { get; init; }
        public required bool SecondSharedIsStart { get; init; }
        public required PointF SecondOppositeEndpoint { get; init; }
        public required PointF BaseAnchor { get; init; }
        /// <summary>Freeform node being dragged; unused by a split Line corner.</summary>
        public int NodeIndex { get; init; } = -1;
        /// <summary>Freeform node state captured when the drag started.</summary>
        public PathBezierNode BaseCorner { get; init; }
        /// <summary>
        /// Fill boundaries coincident with the dragged stroke, captured while the geometry was
        /// still straight so fills follow the corner while it is dragged.
        /// </summary>
        public FillBoundaryLineLink[] FillBoundaryLinks { get; init; } = Array.Empty<FillBoundaryLineLink>();
        public bool DragExceeded { get; set; }
    }

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
        PathBezierNode[]? FreehandBezierWorldNodes,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle,
        string? ImportedSvgSource,
        string? ImportedSvgName,
        TextObjectData? TextData,
        MixingBrushRegionData? MixingRegion,
        MixingBrushTrajectorySample[]? MixingSamples,
        BitmapObjectData? BitmapData);

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

    internal static FormWindowState NormalizeRestartWindowState(FormWindowState state)
        => state == FormWindowState.Maximized
            ? FormWindowState.Maximized
            : FormWindowState.Normal;

    internal static Rectangle NormalizeRestartWindowBounds(
        Rectangle requestedBounds,
        IReadOnlyList<Rectangle> workingAreas,
        Size minimumSize)
    {
        if (workingAreas.Count == 0) return requestedBounds;

        var target = workingAreas[0];
        long largestIntersectionArea = -1;
        foreach (var workingArea in workingAreas)
        {
            var intersection = Rectangle.Intersect(requestedBounds, workingArea);
            var intersectionArea = Math.Max(0L, intersection.Width) * Math.Max(0L, intersection.Height);
            if (intersectionArea <= largestIntersectionArea) continue;
            largestIntersectionArea = intersectionArea;
            target = workingArea;
        }

        var maximumWidth = Math.Max(1, target.Width);
        var maximumHeight = Math.Max(1, target.Height);
        var minimumWidth = Math.Clamp(minimumSize.Width, 1, maximumWidth);
        var minimumHeight = Math.Clamp(minimumSize.Height, 1, maximumHeight);
        var width = Math.Clamp(requestedBounds.Width, minimumWidth, maximumWidth);
        var height = Math.Clamp(requestedBounds.Height, minimumHeight, maximumHeight);
        var maximumX = target.Right - width;
        var maximumY = target.Bottom - height;
        var x = Math.Clamp(requestedBounds.X, target.Left, maximumX);
        var y = Math.Clamp(requestedBounds.Y, target.Top, maximumY);
        return new Rectangle(x, y, width, height);
    }

    private MainForm(EditorRestartState? restartState)
    {
        _restartState = restartState;
        _scene = _project.DrawingObjects[0].Scene;
        _sceneEditStage.CreateEmpty();
        _drawingObjectUnderlayStage.CreateEmpty();
        Text = "Vector 2D Animation Engine";
        Icon = _applicationIcon;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(1120, 720);
        Padding = new Padding(1);
        BackColor = Theme.Border;
        Font = Theme.UiFont();
        KeyPreview = true;

        _stage = new StageControl(_scene)
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(_applicationSettings.WorkspaceColorArgb)
        };
        _stage.AllowDrop = true;
        BindStageBitmapImageResolvers(_stage);
        _dashDock.SetWorkspaceColor(_stage.BackColor);
        Theme.StyleTextBox(_textEditor);
        _textEditor.BorderStyle = BorderStyle.FixedSingle;
        BuildStageContextMenu();
        BuildScene3DContextMenu();
        _timeline = new TimelineStrip(_scene) { Dock = DockStyle.Bottom, Height = TimelinePanelDefaultHeight };
        _timelinePanelMinimumSize = _timeline.MinimumSize;
        _timeline.PlaybackFps = _playbackSettings.Fps;
        _topPlaybackFps.Value = _playbackSettings.Fps;
        _timeline.FrameWidth = _applicationSettings.TimelineFrameWidth;
        _timeline.FrameHeightPreset = _applicationSettings.TimelineFrameHeight;
        _timeline.AutoKeyframeEnabled = _applicationSettings.TimelineAutoKeyframeEnabled;
        _drawSettingsPanel = new DrawSettingsPanel(_drawSettings);
        _drawSettingsPanel.SetPencilPresentation();
        _shapeSettingsPanel = new ShapeSettingsPanel();
        _brushTipPanel.SetBrushShape(_brushShape);
        _brushTipPanel.SetBrushStrokeSettings(
            _drawSettings.BrushFrequency,
            _drawSettings.BrushContinuous,
            _brushStrokeWidthPoints,
            _drawSettings.PressureBrushSmoothing);
        _mixingBrushSettingsPanel.SetSettings(_drawSettings.MixingBrushSettings);
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
        _instanceAppearancePreviewTimer.Tick += (_, _) => FlushInstanceAppearancePreview();
        BuildUi();
        HookEvents();
        UiLocalization.Watch(this);
        UiLocalization.Watch(_mainMenu);
        UiLocalization.Watch(_stageContextMenu);
        UiLocalization.Watch(_scene3DContextMenu);
        if (_restartState is null) CreateNewProject();
        else RestoreRestartState(_restartState);
        UpdateShotDirectorWorkspace();
        RefreshShotDirector(force: true);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (_restartState is not null)
        {
            StartPosition = FormStartPosition.Manual;
            var workingAreas = Screen.AllScreens
                .OrderByDescending(screen => screen.Primary)
                .Select(screen => screen.WorkingArea)
                .ToArray();
            Bounds = NormalizeRestartWindowBounds(_restartState.WindowBounds, workingAreas, MinimumSize);
            AppLog.Info($"Restored main-window bounds to {Bounds} from {_restartState.WindowBounds}.");
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
        _exportSymbolSvgMenuItem.Click += (_, _) =>
        {
            if (ActiveDrawingObject() is { } drawingObject) ExportDrawingObjectSvg(drawingObject.Id);
        };
        _exportSymbolFileMenuItem.Click += (_, _) =>
        {
            if (ActiveDrawingObject() is { } drawingObject) ExportDrawingObjectSymbolPackage(drawingObject.Id);
        };
        _importSymbolFileMenuItem.Click += (_, _) => ImportDrawingObjectSymbolPackage();
        _stageContextMenu.Items.Add(_symbolExportMenuSeparator);
        _stageContextMenu.Items.Add(_exportSymbolSvgMenuItem);
        _stageContextMenu.Items.Add(_exportSymbolFileMenuItem);
        _stageContextMenu.Items.Add(_importSymbolFileMenuItem);
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
            var canBreakObject = breakApartWorkspace
                && !DrawingToolsBlocked()
                && selectedInstances.Count == 0
                && _selectedElements.Count == 0
                && drawingTargets.Length > 0
                && drawingTargets.All(CanBreakApartObject);
            if (canBreakInstances)
            {
                _stageContextMenuBreakApartInstances = selectedInstances.Select(instance => instance.Id).ToArray();
            }
            else if (canBreakObject)
            {
                _stageContextMenuBreakApartObjects = drawingTargets;
            }
            var commandTargetCount = canDeleteNestedInstance ? selectedInstances.Count : drawingTargets.Length;
            var canBringForward = selectedInstances.Count > 0
                ? CanMoveSelectedInstancesInStack(1)
                : _scene.CanMoveObjectsInLayerStack(drawingTargets, 1, _frame);
            var canSendBackward = selectedInstances.Count > 0
                ? CanMoveSelectedInstancesInStack(-1)
                : _scene.CanMoveObjectsInLayerStack(drawingTargets, -1, _frame);
            _deleteNestedInstanceMenuItem.Visible = canDeleteNestedInstance;
            _breakApartMenuItem.Visible = breakApartWorkspace && hasBreakApartTarget;
            _breakApartMenuItem.Enabled = canBreakInstances || canBreakObject;
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
            var containsNonFlippableObject = !canDeleteNestedInstance
                && drawingTargets.Any(IsWholeObjectOnlyObject);
            _flipHorizontalMenuItem.Enabled = commandTargetCount > 0 && !containsNonFlippableObject;
            _flipVerticalMenuItem.Enabled = commandTargetCount > 0 && !containsNonFlippableObject;
            _bringForwardMenuItem.Enabled = canBringForward;
            _sendBackwardMenuItem.Enabled = canSendBackward;
            _mergeAndSimplifyLinesMenuItem.Enabled = !DrawingToolsBlocked()
                && CollectLineMergeTargets(_stageContextMenuLocation, out _).Count >= 2;
            // Exporting a whole symbol is a document action, so it stays available
            // regardless of which drawing tools are active.
            var exportSymbol = ActiveDrawingObject();
            _symbolExportMenuSeparator.Visible = exportSymbol is not null;
            _exportSymbolSvgMenuItem.Visible = exportSymbol is not null;
            _exportSymbolSvgMenuItem.Enabled = exportSymbol is not null;
            _exportSymbolFileMenuItem.Visible = exportSymbol is not null;
            _exportSymbolFileMenuItem.Enabled = exportSymbol is not null;
            _importSymbolFileMenuItem.Visible = true;
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
        BuildRandomFractureContextMenu();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Theme.Top };
        _topBar = top;
        PaintBottomBorder(top);
        Controls.Add(top);
        RegisterWindowDrag(top);
        var menuButton = new SvgIconButton(SvgIconKind.Menu)
        {
            Left = 10,
            Top = 8,
            AccessibleName = "Main menu"
        };
        Theme.StyleToolbarButton(menuButton);
        menuButton.MouseEnter += (_, _) => _toolTip.ShowFor(menuButton, "Main menu");
        menuButton.MouseLeave += (_, _) => _toolTip.HideTip();
        menuButton.Click += (_, _) => _mainMenu.Show(menuButton, new Point(0, menuButton.Height + 4));
        top.Controls.Add(menuButton);
        BuildMainMenu();
        var mark = new Label { Text = "V2", Left = 52, Top = 10, Width = 30, Height = 30, ForeColor = Theme.Accent, BackColor = Theme.Top, Font = Theme.UiFont(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        RegisterWindowDrag(mark);
        top.Controls.Add(mark);
        _projectTitleLabel = Theme.Label("Untitled Project", 94, 13, 180, Theme.Text, Theme.UiFont(10, FontStyle.Bold));
        _projectTitleLabel.AutoEllipsis = true;
        _projectTitleLabel.Cursor = Cursors.Hand;
        _projectTitleLabel.Click += (_, _) => BeginProjectTitleEdit();
        _projectTitleLabel.MouseEnter += (_, _) => _toolTip.ShowFor(_projectTitleLabel, "Click to rename project");
        _projectTitleLabel.MouseLeave += (_, _) => _toolTip.HideTip();
        top.Controls.Add(_projectTitleLabel);

        _projectTitleEditor = new TextBox
        {
            Left = 94,
            Top = 13,
            Width = 180,
            Height = 23,
            Visible = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Top,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(10, FontStyle.Bold)
        };
        _projectTitleEditor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitProjectTitleEdit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CancelProjectTitleEdit();
            }
        };
        _projectTitleEditor.Leave += (_, _) => CommitProjectTitleEdit();
        top.Controls.Add(_projectTitleEditor);
        top.Paint += (_, e) =>
        {
            if (_projectTitleEditor.Visible)
            {
                using var pen = new Pen(Theme.Accent);
                e.Graphics.DrawLine(pen, _projectTitleEditor.Left, _projectTitleEditor.Bottom, _projectTitleEditor.Right, _projectTitleEditor.Bottom);
            }
        };
        _propertiesPanelButton = new SvgIconButton(SvgIconKind.PropertiesPanel)
        {
            Left = 282,
            Top = 8,
            AccessibleName = "Properties panel",
            AccessibleRole = AccessibleRole.CheckButton
        };
        UpdateToolbarTogglePresentation(_propertiesPanelButton, active: true, name: "Properties panel");
        _propertiesPanelButton.Click += (_, _) => ToggleInspectorPanel();
        _propertiesPanelButton.MouseEnter += (_, _) => _toolTip.ShowFor(_propertiesPanelButton, "Properties (F1)");
        _propertiesPanelButton.MouseLeave += (_, _) => _toolTip.HideTip();
        top.Controls.Add(_propertiesPanelButton);
        _timelinePanelButton = new SvgIconButton(SvgIconKind.TimelinePanel)
        {
            Left = 324,
            Top = 8,
            AccessibleName = "Timeline panel",
            AccessibleRole = AccessibleRole.CheckButton
        };
        UpdateToolbarTogglePresentation(_timelinePanelButton, active: true, name: "Timeline panel");
        _timelinePanelButton.Click += (_, _) => ToggleTimelinePanel();
        _timelinePanelButton.MouseEnter += (_, _) => _toolTip.ShowFor(_timelinePanelButton, "Timeline (F2)");
        _timelinePanelButton.MouseLeave += (_, _) => _toolTip.HideTip();
        top.Controls.Add(_timelinePanelButton);
        _dashDock.Left = 366;
        _dashDock.Top = 1;
        _dashDock.WorkspaceColorRequested += (_, _) => ChooseWorkspaceColor();
        _workspaceColorFlyout.ColorPreviewChanged += (_, _) => PreviewWorkspaceColor(_workspaceColorFlyout.Color);
        _workspaceColorFlyout.ColorEditingCompleted += (_, _) => CommitWorkspaceColor(_workspaceColorFlyout.Color);
        top.Controls.Add(_dashDock);
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
        _topPlaybackFpsLabel = playbackFpsLabel;
        Theme.StyleNumeric(_topPlaybackFps);
        top.Controls.Add(playbackFpsLabel);
        top.Controls.Add(_topPlaybackFps);
        var restart = CreateWindowButton(WindowChromeButtonKind.Restart, "Restart editor");
        _restartWindowButton = restart;
        restart.Click += (_, _) => RequestEditorRestart();
        restart.MouseEnter += (_, _) => _toolTip.ShowFor(restart, RestartEditorToolTip());
        restart.MouseLeave += (_, _) => _toolTip.HideTip();
        var minimize = CreateWindowButton(WindowChromeButtonKind.Minimize, "Minimize");
        _minimizeWindowButton = minimize;
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeButton = CreateWindowButton(WindowChromeButtonKind.Maximize, "Maximize");
        _maximizeButton.Click += (_, _) => ToggleMaximized();
        var close = CreateWindowButton(WindowChromeButtonKind.Close, "Close");
        _closeWindowButton = close;
        close.Click += (_, _) => Close();
        top.Controls.Add(restart);
        top.Controls.Add(minimize);
        top.Controls.Add(_maximizeButton);
        top.Controls.Add(close);
        top.Resize += (_, _) => LayoutTopBar();
        Resize += (_, _) =>
        {
            UpdateWindowChromeState();
            ApplyResponsiveWorkbenchLayout();
        };
        LayoutTopBar();
        UpdateWindowChromeState();

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.App };
        Controls.Add(body);
        body.BringToFront();
        _workspaceHeader.Dock = DockStyle.Top;
        _workspaceHeader.Height = 74;
        _workspaceHeader.BackColor = Theme.Top;
        PaintBottomBorder(_workspaceHeader);
        body.Controls.Add(_workspaceHeader);
        _workspaceTabs.Dock = DockStyle.Top;
        _workspaceTabs.Height = 38;
        _workspaceHeader.Controls.Add(_workspaceTabs);
        _drawingObjectRow.Dock = DockStyle.Bottom;
        _drawingObjectRow.Height = 36;
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
        _drawingObjectTabs.Padding = new Padding(8, 3, 8, 5);
        _drawingObjectTabs.Margin = Padding.Empty;
        _drawingObjectTabs.AccessibleRole = AccessibleRole.PageTabList;
        _drawSnappingStrip.Dock = DockStyle.Fill;
        _drawSnappingStrip.Margin = Padding.Empty;
        _drawingObjectRow.Controls.Add(_drawingObjectTabs, 0, 0);
        _drawingObjectRow.Controls.Add(_drawSnappingStrip, 1, 0);
        BuildDrawingObjectTabs();

        var vaultDrawer = new Panel
        {
            Left = 0,
            Top = _workspaceHeader.Height,
            Width = _vaultDrawerExpandedWidth,
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
            _vaultDrawerExpandedWidth,
            Math.Max(0, vaultDrawer.ClientSize.Height));
        _libraryVaultPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        vaultDrawer.Controls.Add(_libraryVaultPanel);
        body.Controls.Add(vaultDrawer);

        _inspectorHost.Dock = DockStyle.Right;
        _inspectorHost.Width = _inspectorPanelExpandedWidth;
        _inspectorHost.BackColor = Theme.Panel;
        _inspectorHost.Padding = new Padding(12, 12, 12, 10);
        // The active inspector page owns scrolling. A second auto-scroll host resets the child page during refresh.
        _inspectorHost.AutoScroll = false;
        PaintLeftBorder(_inspectorHost);
        body.Controls.Add(_inspectorHost);
        BuildInspectorPages(_inspectorHost);

        var stagePanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage };
        body.Controls.Add(stagePanel);
        stagePanel.BringToFront();
        var metrics = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Top };
        PaintBottomBorder(metrics);
        metrics.Controls.Add(_fps);
        _draw.Left = 92;
        metrics.Controls.Add(_draw);
        _atoms.Left = 310;
        metrics.Controls.Add(_atoms);
        _zoom.Left = 540;
        metrics.Controls.Add(_zoom);
        BuildEraserOptionsStrip(metrics);
        Theme.StyleButton(_sceneDimensionButton);
        _sceneDimensionButton.Top = 3;
        Theme.StyleButton(_sceneProjectionButton);
        _sceneProjectionButton.Top = 3;
        var zoomIn = new SvgIconButton(SvgIconKind.ZoomIn)
        {
            Width = 28,
            Height = 28,
            Top = 3,
            AccessibleName = "Zoom in"
        };
        Theme.StyleToolbarButton(zoomIn);
        zoomIn.Click += (_, _) =>
        {
            ZoomActiveView(1.22f);
            UpdateStatusBar();
        };
        var zoomOut = new SvgIconButton(SvgIconKind.ZoomOut)
        {
            Width = 28,
            Height = 28,
            Top = 3,
            AccessibleName = "Zoom out"
        };
        Theme.StyleToolbarButton(zoomOut);
        zoomOut.Click += (_, _) =>
        {
            ZoomActiveView(0.82f);
            UpdateStatusBar();
        };
        metrics.Controls.Add(_sceneDimensionButton);
        metrics.Controls.Add(_sceneProjectionButton);
        metrics.Controls.Add(zoomIn);
        metrics.Controls.Add(zoomOut);
        void LayoutMetricActions()
        {
            const int rightInset = 6;
            const int actionGap = 6;
            var right = metrics.ClientSize.Width - rightInset;
            if (_sceneDimensionButton.Visible)
            {
                _sceneDimensionButton.Left = right - _sceneDimensionButton.Width;
                right = _sceneDimensionButton.Left - actionGap;
            }

            if (_sceneProjectionButton.Visible)
            {
                _sceneProjectionButton.Left = right - _sceneProjectionButton.Width;
                right = _sceneProjectionButton.Left - actionGap;
            }

            zoomIn.Left = right - zoomIn.Width;
            zoomOut.Left = zoomIn.Left - actionGap - zoomOut.Width;
            _zoom.Visible = _zoom.Right + actionGap <= zoomOut.Left;
            _atoms.Visible = _atoms.Right + actionGap <= zoomOut.Left;
            _draw.Visible = _draw.Right + actionGap <= (_atoms.Visible ? _atoms.Left : zoomOut.Left);
        }
        metrics.Resize += (_, _) => LayoutMetricActions();
        _sceneDimensionButton.VisibleChanged += (_, _) => LayoutMetricActions();
        _sceneProjectionButton.VisibleChanged += (_, _) => LayoutMetricActions();
        LayoutMetricActions();
        _eraserOptionsStrip.BringToFront();
        stagePanel.Controls.Add(_stage);
        _stage.Controls.Add(_textEditor);
        _textEditor.BringToFront();
        stagePanel.Controls.Add(metrics);
        metrics.BringToFront();

        var tools = _toolPalette;
        tools.Left = 6;
        tools.Top = metrics.Height + 6;
        tools.Width = 42;
        tools.AutoSize = true;
        tools.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tools.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        tools.BackColor = Theme.Top;
        tools.FlowDirection = FlowDirection.TopDown;
        tools.WrapContents = false;
        tools.Padding = new Padding(4);
        tools.Margin = Padding.Empty;
        tools.AccessibleName = "Tools";
        PaintFullBorder(tools);
        AddToolPairGroup(tools, stagePanel, _selectionToolGroup);
        AddShapeToolGroup(tools, stagePanel);
        AddLineToolGroup(tools, stagePanel);
        AddBrushToolGroup(tools, stagePanel);
        AddTool(tools, SvgIconKind.Text, ToolMode.Text, "Text Tool");
        AddToolPairGroup(tools, stagePanel, _paintToolGroup);
        AddTool(tools, SvgIconKind.Eyedropper, ToolMode.Eyedropper, "Eyedropper Tool");
        AddTool(tools, SvgIconKind.Gradient, ToolMode.Gradient, "Gradient Tool");
        AddTool(tools, SvgIconKind.Snap, ToolMode.SnapPoint, "Snap Point Tool");
        AddTool(tools, SvgIconKind.Eraser, ToolMode.Eraser, "Eraser Tool");

        var vaultButton = new SvgIconButton(SvgIconKind.Vault)
        {
            Margin = new Padding(0, 4, 0, 0),
            AccessibleName = "Vault",
            AccessibleRole = AccessibleRole.CheckButton
        };
        _vaultButton = vaultButton;
        UpdateToolbarTogglePresentation(vaultButton, active: false, name: "Vault");
        vaultButton.MouseEnter += (_, _) => _toolTip.ShowFor(vaultButton, "Vault");
        vaultButton.MouseLeave += (_, _) => _toolTip.HideTip();
        vaultButton.Click += (_, _) => ToggleVaultDrawer();
        tools.Controls.Add(vaultButton);
        stagePanel.Controls.Add(tools);
        tools.BringToFront();
        AttachSceneSpatialControls(stagePanel, metrics);
        AttachShotPreviewOverlay(stagePanel, metrics);
        stagePanel.Resize += (_, _) => LayoutToolPalette();
        LayoutToolPalette();
        vaultDrawer.BringToFront();
        void LayoutVaultDrawer()
        {
            var top = _workspaceHeader.Bottom;
            var height = Math.Max(0, body.ClientSize.Height - top);
            if (vaultDrawer.Top != top || vaultDrawer.Height != height)
            {
                vaultDrawer.SetBounds(0, top, _vaultDrawerExpandedWidth, height);
                _libraryVaultPanel.Height = vaultDrawer.ClientSize.Height;
                ApplyVaultDrawerClip();
            }
        }
        body.Resize += (_, _) => LayoutVaultDrawer();
        LayoutVaultDrawer();
        RefreshToolButtons();
        _workspaceTabs.BringToFront();

        InitializeShotDirector();
        Controls.Add(_timeline);
        _timeline.Resize += (_, _) =>
        {
            LayoutToolPalette();
            if (_timelinePanelOpen
                && !_workspacePanelAnimationTimer.Enabled
                && _timeline.Visible
                && _timeline.Height > 0
                && !_applyingResponsiveWorkbenchLayout)
            {
                _timelinePanelPreferredHeight = _timeline.Height;
                _timelinePanelExpandedHeight = _timeline.Height;
            }
        };
        _timeline.LocationChanged += (_, _) => LayoutToolPalette();
        _timeline.VisibleChanged += (_, _) => LayoutToolPalette();
        LayoutToolPalette();
        BuildStatusBar();
        Controls.Add(_statusBar);
    }

    /// <summary>
    /// Zooms whichever camera presents the current view. Basic Drawing and the 2D Front scene view
    /// zoom the 2D drawing camera; every reference-projected view (3D scenes, side/top directions and
    /// the Shots workspace) zooms the reference camera. The three workspaces keep independent zoom,
    /// so dispatching matters: without it the toolbar buttons would silently do nothing in 3D.
    /// </summary>
    private void ZoomActiveView(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0f) return;
        if (_stage.RendersReferenceProjection)
        {
            _stage.ZoomReferenceCamera(factor);
            return;
        }

        _stage.ZoomAt(new Point(_stage.Width / 2, _stage.Height / 2), factor);
    }

    private void BuildEraserOptionsStrip(Panel metrics)
    {
        _eraserOptionsStrip.Width = 154;
        _eraserOptionsStrip.Height = metrics.Height;
        _eraserOptionsStrip.Top = 0;
        _eraserOptionsStrip.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _eraserOptionsStrip.BackColor = Theme.Top;
        _eraserOptionsStrip.FlowDirection = FlowDirection.LeftToRight;
        _eraserOptionsStrip.WrapContents = false;
        _eraserOptionsStrip.Padding = new Padding(4, 5, 0, 0);
        _eraserOptionsStrip.Margin = Padding.Empty;
        _eraserOptionsStrip.Visible = false;
        _eraserOptionsStrip.Controls.Add(_eraseStrokeOption);
        _eraserOptionsStrip.Controls.Add(_eraseFillOption);
        _eraseStrokeOption.CheckedChanged += (_, _) => ApplyEraserOptions();
        _eraseFillOption.CheckedChanged += (_, _) => ApplyEraserOptions();
        metrics.Controls.Add(_eraserOptionsStrip);

        void PositionStrip()
        {
            var anchoredLeft = Math.Max(0, metrics.ClientSize.Width - _eraserOptionsStrip.Width - 92);
            _eraserOptionsStrip.Left = anchoredLeft < _zoom.Right
                ? Math.Max(0, _zoom.Left)
                : anchoredLeft;
        }

        metrics.Resize += (_, _) => PositionStrip();
        PositionStrip();
        SyncEraserOptions();
    }

    private void ApplyEraserOptions()
    {
        if (_syncingEraserOptions) return;
        _drawSettings.EraseLines = _eraseStrokeOption.Checked;
        _drawSettings.EraseFills = _eraseFillOption.Checked;
        _drawSettings.NotifyChanged();
    }

    private void SyncEraserOptions()
    {
        _syncingEraserOptions = true;
        _eraseStrokeOption.Checked = _drawSettings.EraseLines;
        _eraseFillOption.Checked = _drawSettings.EraseFills;
        _syncingEraserOptions = false;
    }

    private void UpdateEraserOptionsPresentation()
    {
        var visible = _tool == ToolMode.Eraser
            && (_workspaceTabs.SelectedView == WorkspaceView.BasicDrawing || IsSceneMaskEditing());
        _eraserOptionsStrip.Visible = visible;
        if (visible) _eraserOptionsStrip.BringToFront();
        SyncEraserOptions();
    }

    private void BuildMainMenu()
    {
        var newProject = new ToolStripMenuItem("New Project") { ShortcutKeys = Keys.Control | Keys.N };
        newProject.Click += (_, _) => CreateNewProjectFromCommand();
        var openProject = new ToolStripMenuItem("Open Project...") { ShortcutKeys = Keys.Control | Keys.O };
        openProject.Click += (_, _) => OpenProjectFromDialog();
        var importSvg = new ToolStripMenuItem("Import SVG...");
        importSvg.Click += (_, _) => ImportSvgFromDialog();
        var importImage = new ToolStripMenuItem("Import Image...");
        importImage.Click += (_, _) => ImportImageAsset();
        var saveProject = new ToolStripMenuItem("Save Project") { ShortcutKeys = Keys.Control | Keys.S };
        saveProject.Click += (_, _) => SaveProject();
        var saveProjectAs = new ToolStripMenuItem("Save Project As...") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
        saveProjectAs.Click += (_, _) => SaveProjectAs();
        var renameProject = new ToolStripMenuItem("Rename Project...");
        renameProject.Click += (_, _) => BeginProjectTitleEdit();
        var settings = new ToolStripMenuItem("Settings...");
        settings.Click += (_, _) => ShowSettings();
        var releaseNotes = new ToolStripMenuItem("Release Notes...");
        releaseNotes.Click += (_, _) => ShowReleaseNotes();
        _mainMenu.Items.AddRange(new ToolStripItem[]
        {
            newProject,
            openProject,
            importSvg,
            importImage,
            new ToolStripSeparator(),
            saveProject,
            saveProjectAs,
            renameProject,
            new ToolStripSeparator(),
            settings,
            new ToolStripSeparator(),
            releaseNotes
        });
        _mainMenu.Opening += (_, _) =>
        {
            importSvg.Enabled = CanImportSvg();
            importImage.Enabled = CanPlaceImage();
        };
    }

    private bool CanImportSvg()
    {
        var layer = _scene.ActiveLayer;
        var editableDrawingSurface = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            || IsSceneMaskEditing();
        return editableDrawingSurface
            && !DrawingToolsBlocked()
            && (IsSceneMaskEditing() || ActiveDrawingObject() is not null)
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
            var svgName = ImportedSvgDisplayName(fileName);
            var drawingObject = _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
                ? ActiveDrawingObject()
                : null;
            var renameDrawingObject = ShouldRenameEmptyDrawingObjectForSvgImport(drawingObject);
            snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var objectIndex = _scene.AddImportedSvgObject(
                _scene.ActiveLayer,
                placement,
                size,
                imported.Source,
                svgName);
            PushUndoSnapshot(
                snapshot,
                drawingObject: renameDrawingObject ? drawingObject : null,
                drawingObjectName: renameDrawingObject ? drawingObject?.Name : null);
            SetSelection(objectIndex);
            var drawingObjectRenamed = renameDrawingObject
                && drawingObject is not null
                && _project.TryRenameDrawingObject(drawingObject.Id, svgName);
            if (drawingObjectRenamed) RefreshDrawingObjectAssetPresentation();
            else
            {
                _hierarchyPanel.RefreshScene();
                UpdateInspector();
                _stage.Invalidate();
            }
            if (IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
            AppLog.Info($"Imported SVG as one opaque object: {fileName}");
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

    internal static bool ShouldRenameEmptyDrawingObjectForSvgImport(
        DrawingObjectDefinition? drawingObject)
    {
        return drawingObject is not null
            && drawingObject.Scene.ObjectCount == 0
            && drawingObject.Instances.Count == 0;
    }

    internal static string ImportedSvgDisplayName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (name.Length == 0) return "SVG";
        return name.Length <= 80 ? name : name[..80];
    }

    private void ShowReleaseNotes()
    {
        using var dialog = new ReleaseNotesDialog();
        dialog.ShowDialog(this);
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(
            _applicationSettings.ActiveShortcutProfileId,
            _applicationSettings.CustomShortcutProfiles,
            _applicationSettings.Language,
            _applicationSettings.ColorTheme,
            _applicationSettings.ThemeHueDegrees,
            _applicationSettings.ThemeSaturationPercent,
            _applicationSettings.ThemeBrightnessPercent,
            _applicationSettings.AccentHueDegrees,
            _applicationSettings.AccentSaturationPercent,
            _applicationSettings.AccentBrightnessPercent,
            _applicationSettings.FreeTransformShiftProportionalEnabled);
        dialog.SetCodexSettings(_applicationSettings, AppHost.Current?.CodexBridgeStatus ?? "Stopped");
        void PreviewTheme(object? sender, EventArgs e)
        {
            ApplyThemePreview(
                dialog.SelectedColorTheme,
                dialog.SelectedThemeHueDegrees,
                dialog.SelectedThemeSaturationPercent,
                dialog.SelectedThemeBrightnessPercent,
                dialog.SelectedAccentHueDegrees,
                dialog.SelectedAccentSaturationPercent,
                dialog.SelectedAccentBrightnessPercent,
                dialog);
            CommitThemeColors(dialog);
        }
        dialog.ThemePreviewChanged += PreviewTheme;
        var dialogResult = dialog.ShowDialog(this);
        dialog.ThemePreviewChanged -= PreviewTheme;
        if (dialogResult != DialogResult.OK)
        {
            return;
        }

        var selectedShortcutProfileId = dialog.SelectedShortcutProfileId;
        var customShortcutProfiles = dialog.CustomShortcutProfiles;
        var settings = ApplicationSettingsStore.Normalize(_applicationSettings with
        {
            ToolShortcutPreset = ShortcutProfiles.LegacyPresetForProfile(
                selectedShortcutProfileId,
                customShortcutProfiles),
            ActiveShortcutProfileId = selectedShortcutProfileId,
            CustomShortcutProfiles = customShortcutProfiles,
            Language = dialog.SelectedLanguage,
            ColorTheme = dialog.SelectedColorTheme,
            ThemeHueDegrees = dialog.SelectedThemeHueDegrees,
            ThemeSaturationPercent = dialog.SelectedThemeSaturationPercent,
            ThemeBrightnessPercent = dialog.SelectedThemeBrightnessPercent,
            AccentHueDegrees = dialog.SelectedAccentHueDegrees,
            AccentSaturationPercent = dialog.SelectedAccentSaturationPercent,
            AccentBrightnessPercent = dialog.SelectedAccentBrightnessPercent,
            FreeTransformShiftProportionalEnabled = dialog.FreeTransformShiftProportionalEnabled
        });
        settings = ApplicationSettingsStore.Normalize(dialog.ApplyCodexSettingsTo(settings));
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
        AppHost.Current?.ApplyCodexBridgeSettings(settings);
        Theme.ConfigureColorAdjustments(
            settings.ColorTheme,
            settings.ThemeHueDegrees,
            settings.ThemeSaturationPercent,
            settings.ThemeBrightnessPercent,
            settings.AccentHueDegrees,
            settings.AccentSaturationPercent,
            settings.AccentBrightnessPercent);
        UiLocalization.SetLanguage(settings.Language);
        RefreshSceneOpticsText();
        UpdateSceneDimensionButton();
        AppLog.Info(
            $"Application settings changed: shortcuts={settings.ActiveShortcutProfileId}, "
            + $"colorTheme={settings.ColorTheme}, "
            + $"themeHsb={settings.ThemeHueDegrees}/{settings.ThemeSaturationPercent}/{settings.ThemeBrightnessPercent}, "
            + $"accentHsb={settings.AccentHueDegrees}/{settings.AccentSaturationPercent}/{settings.AccentBrightnessPercent}.");
    }

    private void ApplyThemePreview(
        ApplicationColorTheme colorTheme,
        int themeHueDegrees,
        int themeSaturationPercent,
        int themeBrightnessPercent,
        int accentHueDegrees,
        int accentSaturationPercent,
        int accentBrightnessPercent,
        Control? previewDialog = null)
    {
        themeHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(themeHueDegrees);
        themeSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(themeSaturationPercent);
        themeBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(themeBrightnessPercent);
        accentHueDegrees = ApplicationSettingsStore.NormalizeHueDegrees(accentHueDegrees);
        accentSaturationPercent = ApplicationSettingsStore.NormalizeSaturationPercent(accentSaturationPercent);
        accentBrightnessPercent = ApplicationSettingsStore.NormalizeBrightnessPercent(accentBrightnessPercent);
        if (Theme.ColorTheme == colorTheme
            && Theme.ThemeHueDegrees == themeHueDegrees
            && Theme.ThemeSaturationPercent == themeSaturationPercent
            && Theme.ThemeBrightnessPercent == themeBrightnessPercent
            && Theme.AccentHueDegrees == accentHueDegrees
            && Theme.AccentSaturationPercent == accentSaturationPercent
            && Theme.AccentBrightnessPercent == accentBrightnessPercent)
        {
            return;
        }

        var previousPalette = Theme.CurrentPalette;
        Theme.ConfigureColorAdjustments(
            colorTheme,
            themeHueDegrees,
            themeSaturationPercent,
            themeBrightnessPercent,
            accentHueDegrees,
            accentSaturationPercent,
            accentBrightnessPercent);
        Theme.RefreshControlTree(this, previousPalette);
        if (previewDialog is not null && !previewDialog.IsDisposed)
        {
            Theme.RefreshControlTree(previewDialog, previousPalette);
        }
    }

    private void CommitThemeColors(SettingsDialog dialog)
    {
        var settings = ApplicationSettingsStore.Normalize(_applicationSettings with
        {
            ColorTheme = dialog.SelectedColorTheme,
            ThemeHueDegrees = dialog.SelectedThemeHueDegrees,
            ThemeSaturationPercent = dialog.SelectedThemeSaturationPercent,
            ThemeBrightnessPercent = dialog.SelectedThemeBrightnessPercent,
            AccentHueDegrees = dialog.SelectedAccentHueDegrees,
            AccentSaturationPercent = dialog.SelectedAccentSaturationPercent,
            AccentBrightnessPercent = dialog.SelectedAccentBrightnessPercent
        });
        if (settings == _applicationSettings) return;
        _applicationSettings = settings;
        if (!ApplicationSettingsStore.TrySave(settings))
            AppLog.Warn("Theme colors could not be saved; the current session keeps the selected colors.");
    }

    private void ChooseWorkspaceColor()
    {
        _workspaceColorFlyout.ShowFor(_dashDock, _stage.BackColor);
    }

    private void CommitWorkspaceColor(Color color)
    {
        color = Color.FromArgb(255, color.R, color.G, color.B);
        PreviewWorkspaceColor(color);
        if (_applicationSettings.WorkspaceColorArgb == color.ToArgb()) return;
        _applicationSettings = _applicationSettings with { WorkspaceColorArgb = color.ToArgb() };
        if (!ApplicationSettingsStore.TrySave(_applicationSettings))
        {
            AppLog.Warn("The workspace color could not be saved; the current session will keep the selected color.");
        }
    }

    private void PreviewWorkspaceColor(Color color)
    {
        color = Color.FromArgb(255, color.R, color.G, color.B);
        if (_stage.BackColor.ToArgb() != color.ToArgb())
        {
            _stage.BackColor = color;
            _stage.Invalidate();
        }
        _dashDock.SetWorkspaceColor(color);
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
        _inspectorPanelAnimationToWidth = _inspectorPanelOpen ? _inspectorPanelExpandedWidth : 0;
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
            / (double)Math.Max(1, _inspectorPanelExpandedWidth);
        var timelineDistance = Math.Abs(
            _timelinePanelAnimationToHeight - _timelinePanelAnimationFromHeight)
            / (double)Math.Max(1, _timelinePanelExpandedHeight);
        var distance = Math.Max(inspectorDistance, timelineDistance);
        _workspacePanelAnimationDurationMilliseconds = Math.Max(
            60,
            WorkspacePanelAnimationMilliseconds * distance);
        UpdateWorkspacePanelButtonStyles();
        if (!UiMotion.AnimationsEnabled || distance <= 0.0001)
        {
            CompleteWorkspacePanelAnimation();
            return;
        }

        _workspacePanelAnimationTimer.Start();
    }

    private void TickWorkspacePanelAnimation()
    {
        if (!UiMotion.AnimationsEnabled)
        {
            CompleteWorkspacePanelAnimation();
            return;
        }

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
        _inspectorHost.Width = _inspectorPanelOpen ? _inspectorPanelExpandedWidth : 0;
        _timeline.Height = _timelinePanelOpen
            ? Math.Max(_timelinePanelMinimumSize.Height, _timelinePanelExpandedHeight)
            : 0;
        if (_timelinePanelOpen) _timeline.MinimumSize = _timelinePanelMinimumSize;
        _inspectorHost.Visible = _inspectorPanelOpen;
        _timeline.Visible = _timelinePanelOpen;
        UpdateShotDirectorWorkspace();
        UpdateWorkspacePanelButtonStyles();
        _stage.Invalidate();
    }

    private void UpdateWorkspacePanelButtonStyles()
    {
        if (_propertiesPanelButton is not null)
        {
            UpdateToolbarTogglePresentation(_propertiesPanelButton, _inspectorPanelOpen, "Properties panel");
        }
        if (_timelinePanelButton is not null)
        {
            UpdateToolbarTogglePresentation(_timelinePanelButton, _timelinePanelOpen, "Timeline panel");
        }
    }

    private static void UpdateToolbarTogglePresentation(SvgIconButton button, bool active, string name)
    {
        Theme.StyleToolbarButton(button, active);
        button.AccessibleRole = AccessibleRole.CheckButton;
        button.AccessibleDescription = $"{UiLocalization.T(name)} {UiLocalization.T(active ? "Visible" : "Hidden")}";
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
            if (_vaultButton is not null) UpdateToolbarTogglePresentation(_vaultButton, active: true, name: "Vault");
        }
        else if (_vaultButton is not null)
        {
            UpdateToolbarTogglePresentation(_vaultButton, active: false, name: "Vault");
        }

        _vaultDrawerAnimationFromWidth = _vaultDrawerVisibleWidth;
        _vaultDrawerAnimationToWidth = _vaultDrawerOpen ? _vaultDrawerExpandedWidth : 0;
        _vaultDrawerAnimationStartedTimestamp = Stopwatch.GetTimestamp();
        var distance = Math.Abs(_vaultDrawerAnimationToWidth - _vaultDrawerAnimationFromWidth);
        _vaultDrawerAnimationDurationMilliseconds = Math.Max(
            48,
            VaultDrawerAnimationMilliseconds * distance / Math.Max(1, _vaultDrawerExpandedWidth));
        if (!UiMotion.AnimationsEnabled || distance == 0)
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
        if (!UiMotion.AnimationsEnabled)
        {
            CompleteVaultDrawerAnimation();
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
        _vaultDrawerVisibleWidth = _vaultDrawerOpen ? _vaultDrawerExpandedWidth : 0;
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
        var visibleWidth = Math.Clamp(_vaultDrawerVisibleWidth, 0, _vaultDrawerExpandedWidth);
        Region? nextRegion = null;
        if (visibleWidth < _vaultDrawerExpandedWidth)
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
            ? Math.Clamp(_vaultDrawerVisibleWidth, 0, _vaultDrawerExpandedWidth)
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
        InitializeResolutionAwareWindow();
        ApplyMaximizedBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CancelReferenceCameraRightLook();
        CancelReferenceCameraKeyboardNavigation();
        if (!CommitTextEdit())
        {
            e.Cancel = true;
            return;
        }
        CompleteFillEdgeBezierSessionForContextChange();
        if (!_restartRequested && e.CloseReason == CloseReason.UserClosing && !TryContinueAfterUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }

        CancelRandomFracturePreview();
        StopPlayback();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelRandomFracturePreview();
            StopPlaybackCompositionPreload();
            PersistTimelineFrameWidth();
            PersistTimelineFrameHeight();
            _shapeFlyoutHideTimer.Dispose();
            _lineFlyoutHideTimer.Dispose();
            _selectionToolGroup.HideTimer.Dispose();
            _paintToolGroup.HideTimer.Dispose();
            _vaultDrawerTimer.Dispose();
            _workspacePanelAnimationTimer.Dispose();
            _lineDragPreviewTimer.Dispose();
            _instanceAppearancePreviewTimer.Dispose();
            _referenceCameraKeyboardTimer.Dispose();
            _timer.Dispose();
            _stageContextMenu.Dispose();
            DisposeScene3DContextMenu();
            _mainMenu.Dispose();
            _workspaceColorFlyout.Dispose();
            _toolTip.Dispose();
            _textEditorOwnedFont?.Dispose();
            _applicationIcon.Dispose();
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

        if (m.Msg is WmDisplayChange or WmSettingChange)
        {
            base.WndProc(ref m);
            QueueResolutionAwareLayoutRefresh();
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
        _drawingObjectTabs.SuspendLayout();
        try
        {
            var drawingObject = ActiveDrawingObject();
            if (drawingObject is null)
            {
                _activeDrawingObjectTabButton?.Dispose();
                _activeDrawingObjectTabButton = null;
                _drawingObjectTabButtons.Clear();
            }
            else
            {
                if (_activeDrawingObjectTabButton is null)
                {
                    AddDrawingObjectTab(_activeDrawingObjectIndex);
                }
                var button = _activeDrawingObjectTabButton!;
                if (!Equals(button.Tag, drawingObject.Id))
                {
                    _toolTip.HideTip();
                    button.Tag = drawingObject.Id;
                    _drawingObjectTabButtons.Clear();
                    _drawingObjectTabButtons[drawingObject.Id] = button;
                }
                if (button.Text != drawingObject.Name) button.Text = drawingObject.Name;
                button.AccessibleName = drawingObject.Name;
                var width = drawingObject.Name.Length > 10 ? 132 : 108;
                if (button.Width != width) button.Width = width;
                if (_drawingObjectTabs.Controls.GetChildIndex(button) != 0)
                    _drawingObjectTabs.Controls.SetChildIndex(button, 0);
            }
            if (_addDrawingObjectTabButton is null) AddNewDrawingObjectButton();
        }
        finally
        {
            _drawingObjectTabs.ResumeLayout(performLayout: true);
        }
        RefreshToolButtons();
    }

    private void AddDrawingObjectTab(int index)
    {
        var drawingObject = _drawingObjects[index];
        var button = new SegmentedButton
        {
            Text = drawingObject.Name,
            Width = drawingObject.Name.Length > 10 ? 132 : 108,
            Height = 28,
            Margin = new Padding(0, 0, 6, 0),
            Tag = drawingObject.Id,
            AutoEllipsis = true,
            AccessibleName = drawingObject.Name,
            AccessibleRole = AccessibleRole.PageTab
        };
        Theme.StyleSegmentedButton(button);
        button.Click += (_, _) =>
        {
            for (var currentIndex = 0; currentIndex < _drawingObjects.Count; currentIndex++)
            {
                if (!Equals(button.Tag, _drawingObjects[currentIndex].Id)) continue;
                SelectDrawingObject(currentIndex);
                break;
            }
        };
        button.MouseEnter += (_, _) =>
        {
            var current = _drawingObjects.FirstOrDefault(item => Equals(button.Tag, item.Id));
            if (current is not null) _toolTip.ShowFor(button, $"{current.Name} ({current.Kind})");
        };
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        var dragStart = Point.Empty;
        button.MouseDown += (_, e) => dragStart = e.Location;
        button.MouseMove += (_, e) =>
        {
            var current = _drawingObjects.FirstOrDefault(item => Equals(button.Tag, item.Id));
            if (e.Button != MouseButtons.Left || current is null || current.Kind == "Scene") return;
            if (Math.Abs(e.X - dragStart.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height / 2) return;
            var dragData = new DataObject();
            dragData.SetData(typeof(DrawingObjectDragData), new DrawingObjectDragData(_project.Id, current.Id));
            dragData.SetData(typeof(VaultItem), current.ToVaultItem());
            button.DoDragDrop(dragData, DragDropEffects.Copy);
        };
        _activeDrawingObjectTabButton = button;
        _drawingObjectTabButtons[drawingObject.Id] = button;
        _drawingObjectTabs.Controls.Add(button);
    }

    private void AddNewDrawingObjectButton()
    {
        var button = new SvgIconButton(SvgIconKind.Add)
        {
            Text = "Symbol",
            Width = 88,
            Height = 28,
            Margin = new Padding(4, 0, 6, 0),
            AutoEllipsis = true,
            AccessibleName = "Add Symbol"
        };
        Theme.StyleToolbarButton(button);
        button.Click += (_, _) => AddDrawingObject();
        _addDrawingObjectTabButton = button;
        _drawingObjectTabs.Controls.Add(button);
    }

    private void AddScene()
    {
        if (!CommitTextEdit()) return;
        _project.AddScene();
        SelectScene(_scenes.Count - 1);
    }

    private void SelectScene(int index)
    {
        if (index < 0 || index >= _scenes.Count) return;
        if (!CommitTextEdit()) return;
        if (_activeSceneIndex != index) ResetUndoHistory();
        _activeSceneIndex = index;
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateSceneDimensionButton();
        if (IsSceneWorkspaceSelected) BindSceneEditStage(resetView: false);
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

    private bool IsSceneBuildingContext() => _timeline.Context is SceneDefinition;

    private bool IsShotDirectorContext() =>
        _workspaceTabs.SelectedView == WorkspaceView.ShotDirector
        && IsSceneBuildingContext();

    private bool IsSceneMaskEditing()
    {
        return IsSceneBuildingContext()
            && ActiveScene()?.FindLayer(ActiveScene()!.ActiveLayerId)?.Kind == SceneLayerKind.Mask;
    }

    private bool IsSceneCompositionContext() => IsSceneBuildingContext() && !IsSceneMaskEditing();

    private bool DrawingToolsBlocked()
    {
        return IsSceneCompositionContext()
            || _scene.IsLayerEffectivelyLocked(_scene.ActiveLayer);
    }

    private SceneDimension ActiveSceneViewDimension()
    {
        var scene = ActiveScene();
        if (scene is null) return SceneDimension.TwoD;
        return _sceneViewDimensions.TryGetValue(scene.Id, out var dimension)
            ? dimension
            : scene.Dimension;
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
        var depth = ActiveSceneViewDimension() == SceneDimension.TwoD && e.Projection == CameraProjection.Perspective
            ? Math.Max(scene.Camera.Depth, 1000)
            : scene.Camera.Depth;
        if (scene.Camera.Projection == e.Projection
            && scene.Camera.Depth.Equals(depth))
        {
            return;
        }

        scene.Camera.Projection = e.Projection;
        scene.Camera.Depth = depth;
        MarkProjectDirty();

        UpdateSceneDimensionButton();
        if (IsSceneWorkspaceSelected)
        {
            _stage.ConfigureReferenceView(
                scene,
                ActiveSceneViewDimension(),
                ReferenceCameraMotion.Animated);
        }
        AppLog.Info($"Updated scene camera: {scene.Name}, {scene.Camera.Projection}");
    }

    private void ToggleSceneDimension()
    {
        var scene = ActiveScene();
        if (scene is null || IsSceneMaskEditing()) return;
        // Playback can finish on a scheduler callback before the UI-facing
        // flag is observed. Treat any residual scheduler/Stage/preloader
        // state as active so the dimension switch also repairs that state.
        if (_playing
            || _stage.PlaybackActive
            || _stage.Reference3DPlaybackActive
            || _stage.Reference3DOpticalInteractionPreviewActive
            || _playbackSchedulerState is not null
            || _playbackCompositionPreloader is not null
            || _playbackCompositionSceneActive)
        {
            StopPlayback();
        }
        var dimension = ActiveSceneViewDimension() == SceneDimension.TwoD
            ? SceneDimension.ThreeD
            : SceneDimension.TwoD;
        FinishPointerInteractionForContextChange();
        _sceneViewDimensions[scene.Id] = dimension;
        _project.TrySetSceneDimension(scene.Id, dimension);
        _stage.CompleteReferenceCameraTransition(invalidate: false);
        _stage.ConfigureReferenceView(scene, dimension, ReferenceCameraMotion.Immediate);
        if (dimension == SceneDimension.ThreeD && _tool is ToolMode.Transform or ToolMode.Distort)
        {
            _tool = ToolMode.Transform3D;
        }
        else if (dimension == SceneDimension.TwoD && _tool == ToolMode.Transform3D)
        {
            _tool = ToolMode.Transform;
        }
        if (_selectionToolGroup.Contains(_tool)) _selectionToolGroup.ActiveTool = _tool;
        UpdateSceneDimensionButton();
        UpdateSpatialTransformPanelState();
        RefreshSceneOpticsInspector();
        RefreshToolButtons();
        ApplyToolCursor();
        UpdateTransformOverlay();
        _stage.Focus();
        AppLog.Info($"Switched scene view: {scene.Name}, {dimension}");
    }

    private void UpdateSceneDimensionButton()
    {
        var scene = ActiveScene();
        var maskEditing = IsSceneMaskEditing();
        var isThreeD = scene is not null
            && !maskEditing
            && ActiveSceneViewDimension() == SceneDimension.ThreeD;
        _sceneDimensionButton.Text = isThreeD ? "3D" : "2D";
        _sceneDimensionButton.Enabled = scene is not null && !maskEditing;
        _sceneDimensionButton.AccessibleDescription = maskEditing
            ? "Scene masks are edited on the 2D reference plane"
            : isThreeD
            ? "Current scene view is 3D. Activate to switch to 2D"
            : "Current scene view is 2D. Activate to switch to 3D";

        var projection = scene?.Camera.Projection ?? CameraProjection.Orthographic;
        var projectionAvailable = IsSceneWorkspaceSelected
            && scene is not null
            && !maskEditing;
        _sceneProjectionButton.Text = projection == CameraProjection.Perspective
            ? "Perspective"
            : "Orthographic";
        _sceneProjectionButton.Visible = projectionAvailable;
        _sceneProjectionButton.Enabled = projectionAvailable;
        _sceneProjectionButton.AccessibleDescription = UiLocalization.T(
            projection == CameraProjection.Perspective
                ? "Current camera projection is perspective. Activate to switch to orthographic"
                : "Current camera projection is orthographic. Activate to switch to perspective");
        UpdateSceneSpatialControlsVisibility();
    }

    private string SceneProjectionButtonToolTip()
    {
        return UiLocalization.T(ActiveScene()?.Camera.Projection == CameraProjection.Perspective
            ? "Switch to orthographic projection"
            : "Switch to perspective projection");
    }

    private void AddSceneInstanceFromActiveDrawingObject()
    {
        var scene = ActiveScene();
        var drawingObject = ActiveDrawingObject();
        if (scene is null || drawingObject is null) return;

        if (!_project.TryAddSceneInstance(
                scene.Id,
                drawingObject.Id,
                PointF.Empty,
                0,
                out _))
        {
            return;
        }

        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        if (IsSceneWorkspaceSelected)
        {
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration();
            RebuildSceneComposition();
        }
        AppLog.Info($"Added scene instance: {drawingObject.Name} -> {scene.Name}");
    }

    private void AddDrawingObject()
    {
        if (!CommitTextEdit()) return;
        _project.AddDrawingObject();
        SelectDrawingObject(_drawingObjects.Count - 1);
    }

    private void RenameDrawingObject(string drawingObjectId)
    {
        var drawingObject = _drawingObjects.FirstOrDefault(item => string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null) return;
        if (!DrawingObjectNameDialog.TryAsk(this, "Rename Symbol", "Symbol name", drawingObject.Name, out var name)) return;
        if (!_project.TryRenameDrawingObject(drawingObjectId, name)) return;

        RefreshDrawingObjectAssetPresentation();
        AppLog.Info($"Renamed symbol: {drawingObject.Name}");
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
        AppLog.Info($"Duplicated symbol: {duplicate.Name}");
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

    private void BeginProjectTitleEdit()
    {
        if (_projectTitleEditor is null || _projectTitleLabel is null) return;
        _projectTitleEditor.Text = _project.Name;
        _projectTitleLabel.Visible = false;
        _projectTitleEditor.Visible = true;
        _projectTitleEditor.BringToFront();
        _projectTitleEditor.Focus();
        _projectTitleEditor.SelectAll();
    }

    private void CommitProjectTitleEdit()
    {
        if (_projectTitleEditor is null || !_projectTitleEditor.Visible) return;
        var name = _projectTitleEditor.Text.Trim();
        EndProjectTitleEdit();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, _project.Name, StringComparison.Ordinal)) return;

        _project.Name = name;
        SetProjectDirty(true);
        UpdateProjectTitle();
        AppLog.Info($"Renamed project: {_project.Name}");
    }

    private void CancelProjectTitleEdit()
    {
        if (_projectTitleEditor is null || !_projectTitleEditor.Visible) return;
        EndProjectTitleEdit();
    }

    private void EndProjectTitleEdit()
    {
        if (_projectTitleEditor is null || _projectTitleLabel is null) return;
        _projectTitleEditor.Visible = false;
        _projectTitleLabel.Visible = true;
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
        if (!CommitTextEdit()) return;
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
                UiLocalization.T("A project must retain at least one symbol."),
                UiLocalization.T("Delete Symbol"),
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
            ? $"删除元件“{drawingObject.Name}”？{referenceNotice}"
            : $"Delete symbol \"{drawingObject.Name}\"?{referenceNotice}";
        if (ModernMessageDialog.Show(
                this,
                deletePrompt,
                UiLocalization.T("Delete Symbol"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        var wasSceneBuildingContext = IsSceneBuildingContext();
        if (!_project.TryRemoveDrawingObject(drawingObjectId, out _)) return;

        if (_activeDrawingObjectIndex > index) _activeDrawingObjectIndex--;
        else if (_activeDrawingObjectIndex == index) _activeDrawingObjectIndex = Math.Min(index, _drawingObjects.Count - 1);

        ResetEditHistory();
        if (wasSceneBuildingContext) BindSceneEditStage(resetView: false);
        else BindActiveDrawingObjectScene(resetView: false);
        BuildDrawingObjectTabs();
        AppLog.Info($"Deleted symbol: {drawingObject.Name}");
    }

    private void RefreshDrawingObjectAssetPresentation()
    {
        BuildDrawingObjectTabs();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        _libraryVaultPanel.RefreshProjectObjects();
        if (IsSceneBuildingContext()) RebuildSceneComposition();
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
        CompleteFillEdgeBezierSessionForContextChange();
        CancelTraditionalPenPath();
        CancelPenCurve();
        var drawingObject = ActiveDrawingObject();
        if (drawingObject is null)
        {
            if (_drawingObjects.Count == 0)
            {
                _project.AddDrawingObject("Symbol 001");
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
        if (resetView) _stage.ResetDefaultView();
        UpdateInspector();
        UpdateStatusBar();
    }

    private void BindSceneEditStage(bool resetView)
    {
        CompleteFillEdgeBezierSessionForContextChange();
        CancelTraditionalPenPath();
        CancelPenCurve();
        var sceneDefinition = ActiveScene();
        var nextScene = sceneDefinition?.ActiveMaskScene() ?? _sceneEditStage;
        if (!ReferenceEquals(_scene, nextScene))
        {
            if (ReferenceEquals(_timeline.Context, sceneDefinition)) ResetCanvasUndoHistory();
            else ResetUndoHistory();
        }
        _scene = nextScene;
        if (_scene.LayerCount <= 0) _scene.CreateEmpty();
        _stage.BindScene(_scene);
        _stage.ConfigureReferenceView(
            sceneDefinition,
            sceneDefinition?.ActiveMaskScene() is not null
                ? SceneDimension.TwoD
                : ActiveSceneViewDimension());
        if (sceneDefinition is not null) _timeline.BindSceneDefinition(sceneDefinition);
        else _timeline.BindScene(_scene);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _libraryVaultPanel.SetActiveDrawingObject(ActiveDrawingObject()?.Id);
        ApplyProjectPlaybackSettingsToCurrentContext();
        SyncTimelineFrameRange();
        SetFrame(
            Math.Clamp(_frame, 0, _playbackSettings.EndFrame),
            refreshScenePanels: false,
            reuseCachedSceneComposition: true);
        ClearSelection();
        RefreshLayers();
        if (!_lastSceneCompositionReused || _playing)
        {
            _hierarchyPanel.BindScene(_scene);
        }
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        if (resetView) _stage.ResetDefaultView();
        UpdateInspector();
        UpdateStatusBar();
    }

    private void RebuildSceneComposition(
        bool refreshScenePanels = true,
        bool reuseCachedComposition = false)
    {
        RebuildSceneCompositionCore(
            refreshScenePanels,
            reuseCachedComposition,
            preserveWorkspaceFrameCache: false);
    }

    private void RebuildSceneCompositionPreservingWorkspaceFrameCache(
        bool refreshScenePanels = true,
        bool reuseCachedComposition = false)
    {
        RebuildSceneCompositionCore(
            refreshScenePanels,
            reuseCachedComposition,
            preserveWorkspaceFrameCache: true);
    }

    private void RebuildSceneCompositionCore(
        bool refreshScenePanels,
        bool reuseCachedComposition,
        bool preserveWorkspaceFrameCache)
    {
        if (!IsSceneBuildingContext()) return;
        RestoreEditableCompositionScene();
        var sceneDefinition = ActiveScene();
        _lastSceneCompositionReused = reuseCachedComposition && CanReuseSceneComposition(sceneDefinition);
        if (!_lastSceneCompositionReused)
        {
            _sceneCompositionResult = SceneCompositionBuilder.Build(
                _sceneEditStage,
                sceneDefinition,
                _drawingObjects,
                _frame,
                _playbackSettings.Fps);
            CaptureSceneCompositionCache(sceneDefinition);
        }
        _stage.SetSceneCompositionResult(
            _sceneCompositionResult,
            _sceneEditStage,
            preserveWorkspaceFrameCache: _playing || preserveWorkspaceFrameCache);
        _stage.SetSceneCompositionMaskClips(
            BuildSceneCompositionMaskClips(sceneDefinition),
            preserveWorkspaceFrameCache: _playing || preserveWorkspaceFrameCache);
        if (IsSceneMaskEditing())
        {
            _stage.BindUnderlayScene(_sceneEditStage);
        }
        else
        {
            _stage.BindUnderlayScene(null);
        }
        _sceneInstancePreviewDirty = false;
        RefreshSelectedSceneInstanceObjectIndices();
        RebuildOnionSkinPreview();
        if (!_playing && refreshScenePanels)
        {
            _hierarchyPanel.BindScene(_scene);
        }

        UpdateSceneInstanceSelectionOverlay();
        UpdateTransformOverlay();
        _stage.Invalidate();
    }

    private bool CanReuseSceneComposition(SceneDefinition? sceneDefinition)
    {
        if (!_sceneCompositionCacheValid
            || _sceneInstancePreviewDirty
            || !ReferenceEquals(_sceneCompositionCacheScene, sceneDefinition)
            || _sceneCompositionCacheFrame != _frame
            || _sceneCompositionCacheFps != _playbackSettings.Fps
            || _sceneCompositionSourceRevisions.Length != _drawingObjects.Count)
        {
            return false;
        }

        for (var index = 0; index < _drawingObjects.Count; index++)
        {
            var source = _drawingObjects[index].Scene;
            var cached = _sceneCompositionSourceRevisions[index];
            if (!ReferenceEquals(cached.Scene, source)
                || cached.GeometryRevision != source.GeometryRevision
                || cached.SummaryRevision != source.SummaryRevision)
            {
                return false;
            }
        }

        return true;
    }

    private void CaptureSceneCompositionCache(SceneDefinition? sceneDefinition)
    {
        var revisions = new (VectorScene Scene, long GeometryRevision, long SummaryRevision)[_drawingObjects.Count];
        for (var index = 0; index < _drawingObjects.Count; index++)
        {
            var source = _drawingObjects[index].Scene;
            revisions[index] = (source, source.GeometryRevision, source.SummaryRevision);
        }

        _sceneCompositionCacheScene = sceneDefinition;
        _sceneCompositionCacheFrame = _frame;
        _sceneCompositionCacheFps = _playbackSettings.Fps;
        _sceneCompositionSourceRevisions = revisions;
        _sceneCompositionCacheValid = true;
    }

    private void InvalidateSceneCompositionCache()
    {
        _sceneCompositionCacheValid = false;
        _lastSceneCompositionReused = false;
        _sceneCompositionCacheScene = null;
        _sceneCompositionSourceRevisions = [];
    }

    private void RebuildEditableInstanceComposition()
    {
        if (IsSceneCompositionContext()) RebuildSceneComposition();
        else RebuildDrawingObjectUnderlay();
    }

    private void RebuildDrawingObjectUnderlay()
    {
        if (IsSceneBuildingContext())
        {
            if (IsSceneMaskEditing())
            {
                SynchronizeActiveSceneMaskTimelineContent();
                RebuildSceneComposition();
                return;
            }
            _drawingObjectUnderlayResult = SceneCompositionResult.Empty;
            _stage.BindUnderlayScene(null);
            RebuildOnionSkinPreview();
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
        RefreshMotionTrackToggle();
        RebuildOnionSkinPreviewCore();
        // The track samples the same pointer range the onion skin just resolved, so it is rebuilt
        // after every onion-skin refresh, including the paths that clear the preview.
        RebuildMotionTrackPreview();
    }

    private void RebuildOnionSkinPreviewCore()
    {
        if (_playing)
        {
            if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
            return;
        }

        if (IsSceneBuildingContext())
        {
            RebuildSceneOnionSkinPreview();
            return;
        }

        if (!_scene.HasOnionSkinPreviewEnabled)
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

    /// <summary>
    /// Scene Building onion skin: composes each requested neighbouring frame of
    /// the scene definition and tints it, matching the drawing-mode preview.
    /// </summary>
    private void RebuildSceneOnionSkinPreview()
    {
        var sceneDefinition = ActiveScene();
        if (sceneDefinition is null || !sceneDefinition.HasOnionSkinPreviewEnabled)
        {
            if (_stage.OnionSkinScene is not null) _stage.BindOnionSkinScene(null);
            return;
        }

        SceneCompositionBuilder.BuildSceneOnionSkin(
            _sceneOnionSkinStage,
            sceneDefinition,
            _drawingObjects,
            _frame,
            _playbackSettings.Fps);
        _stage.BindOnionSkinScene(_sceneOnionSkinStage.ObjectCount == 0 ? null : _sceneOnionSkinStage);
    }

    private void SelectDrawingObject(int index)
    {
        if (index < 0 || index >= _drawingObjects.Count) return;
        if (!CommitTextEdit()) return;
        _activeDrawingObjectIndex = index;
        if (IsSceneWorkspaceSelected) BindSceneEditStage(resetView: false);
        else BindActiveDrawingObjectScene(resetView: true);
        BuildDrawingObjectTabs();
        RefreshToolButtons();
        AppLog.Info($"Selected symbol tab: {_drawingObjects[index].Name}");
    }

    private bool OpenDrawingObjectEditor(string drawingObjectId)
    {
        if (!CommitTextEdit()) return false;
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

        AppLog.Info($"Opened symbol editor: {_drawingObjects[index].Name}");
        return true;
    }

    private void AddTool(FlowLayoutPanel panel, SvgIconKind icon, ToolMode tool, string displayName)
    {
        var button = new SvgIconButton(icon) { Margin = new Padding(0, 0, 0, 4), Tag = tool, AccessibleName = displayName };
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

    private static SvgIconButton CreateToolFlyoutButton(ToolMode tool, string displayName, bool isLast)
    {
        return new SvgIconButton(ToolIconKind(tool))
        {
            Width = Theme.ToolFlyoutButtonWidth,
            Margin = new Padding(0, 0, 0, isLast ? 0 : Theme.GapXs),
            Tag = tool,
            Text = UiLocalization.T(displayName),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = displayName
        };
    }

    private void AddToolPairGroup(FlowLayoutPanel panel, Control overlayParent, ToolPairGroup group)
    {
        var parent = new SvgIconButton(ToolIconKind(group.ActiveTool))
        {
            Margin = new Padding(0, 0, 0, 4),
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
            Width = Theme.ToolFlyoutPanelWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4),
            Visible = false
        };
        group.Flyout = flyout;
        PaintFullBorder(flyout);
        flyout.MouseEnter += (_, _) => ShowToolPairFlyout(group);
        flyout.MouseLeave += (_, _) => ScheduleToolPairFlyoutHideAfter(group, 350);
        overlayParent.Controls.Add(flyout);

        foreach (var tool in group.Tools)
        {
            var displayName = ToolPairName(tool);
            var button = CreateToolFlyoutButton(tool, displayName, tool == group.Tools[^1]);
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, displayName);
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
        if (!group.Tools.Any(IsToolVisibleForCurrentContext))
        {
            HideToolPairFlyout(group);
            return;
        }
        HideShapeToolFlyout();
        HideLineToolFlyout();
        HideBrushToolFlyout();
        if (!ReferenceEquals(group, _selectionToolGroup)) HideToolPairFlyout(_selectionToolGroup);
        if (!ReferenceEquals(group, _paintToolGroup)) HideToolPairFlyout(_paintToolGroup);
        group.HideAtUtc = default;
        if (group.Flyout.Visible)
        {
            StartToolPairFlyoutVisibilityCheck(group);
            return;
        }
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
            Margin = new Padding(0, 0, 0, 4),
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
            Width = Theme.ToolFlyoutPanelWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4),
            Visible = false
        };
        PaintFullBorder(_shapeToolFlyout);
        _shapeToolFlyout.MouseEnter += (_, _) => ShowShapeToolFlyout();
        _shapeToolFlyout.MouseLeave += (_, _) => StartShapeFlyoutVisibilityCheck();
        overlayParent.Controls.Add(_shapeToolFlyout);

        foreach (var tool in _shapeTools)
        {
            var displayName = ShapeToolName(tool);
            var button = CreateToolFlyoutButton(tool, displayName, tool == _shapeTools[^1]);
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, displayName);
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
        if (!_shapeTools.Any(IsToolVisibleForCurrentContext))
        {
            HideShapeToolFlyout();
            return;
        }
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
            Margin = new Padding(0, 0, 0, 4),
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
            Width = Theme.ToolFlyoutPanelWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4),
            Visible = false
        };
        PaintFullBorder(_lineToolFlyout);
        _lineToolFlyout.MouseEnter += (_, _) => ShowLineToolFlyout();
        _lineToolFlyout.MouseLeave += (_, _) => ScheduleLineToolFlyoutHideAfter(350);
        overlayParent.Controls.Add(_lineToolFlyout);

        foreach (var tool in _lineTools)
        {
            var displayName = LineToolName(tool);
            var button = CreateToolFlyoutButton(tool, displayName, tool == _lineTools[^1]);
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, displayName);
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
            Margin = new Padding(0, 0, 0, 4),
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
            Width = Theme.ToolFlyoutPanelWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4),
            Visible = false
        };
        PaintFullBorder(_brushToolFlyout);
        _brushToolFlyout.MouseEnter += (_, _) => ShowBrushToolFlyout();
        _brushToolFlyout.MouseLeave += (_, _) => ScheduleBrushToolFlyoutHideAfter(350);
        overlayParent.Controls.Add(_brushToolFlyout);

        foreach (var tool in _brushTools)
        {
            var displayName = BrushToolName(tool);
            var button = CreateToolFlyoutButton(tool, displayName, tool == _brushTools[^1]);
            Theme.StyleButton(button);
            button.MouseEnter += (_, _) =>
            {
                _toolTip.ShowFor(button, displayName);
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
        if (!_brushTools.Any(IsToolVisibleForCurrentContext))
        {
            HideBrushToolFlyout();
            return;
        }
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
        if (!_lineTools.Any(IsToolVisibleForCurrentContext))
        {
            HideLineToolFlyout();
            return;
        }
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

}
