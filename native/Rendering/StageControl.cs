using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal readonly record struct StageViewState(
    float CameraX,
    float CameraY,
    float Zoom,
    float ReferenceYaw,
    float ReferencePitch,
    float ReferenceDistance,
    float ReferenceZoomScale,
    float ReferenceTargetX,
    float ReferenceTargetY,
    float ReferenceTargetZ,
    float WorldGridOpacity,
    WorldGridType WorldGridType);

internal readonly record struct TransformOverlayFrame(PointF Origin, PointF AxisX, PointF AxisY)
{
    public PointF TopLeft => Origin;
    public PointF TopRight => Add(Origin, AxisX);
    public PointF BottomLeft => Add(Origin, AxisY);
    public PointF BottomRight => Add(TopRight, AxisY);
    public PointF Center => new(
        Origin.X + (AxisX.X + AxisY.X) * 0.5f,
        Origin.Y + (AxisX.Y + AxisY.Y) * 0.5f);

    public RectangleF Bounds
    {
        get
        {
            var topRight = TopRight;
            var bottomRight = BottomRight;
            var bottomLeft = BottomLeft;
            var left = Math.Min(Math.Min(Origin.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X));
            var top = Math.Min(Math.Min(Origin.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y));
            var right = Math.Max(Math.Max(Origin.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X));
            var bottom = Math.Max(Math.Max(Origin.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y));
            return RectangleF.FromLTRB(left, top, right, bottom);
        }
    }

    public bool IsValid
    {
        get
        {
            if (!IsFinite(Origin) || !IsFinite(AxisX) || !IsFinite(AxisY)) return false;
            var lengthXSquared = AxisX.X * AxisX.X + AxisX.Y * AxisX.Y;
            var lengthYSquared = AxisY.X * AxisY.X + AxisY.Y * AxisY.Y;
            var determinant = AxisX.X * AxisY.Y - AxisX.Y * AxisY.X;
            return lengthXSquared > 0.000001f
                && lengthYSquared > 0.000001f
                && Math.Abs(determinant) > 0.000001f;
        }
    }

    public static TransformOverlayFrame FromBounds(RectangleF bounds) => new(
        new PointF(bounds.Left, bounds.Top),
        new PointF(bounds.Width, 0),
        new PointF(0, bounds.Height));

    private static PointF Add(PointF left, PointF right) => new(left.X + right.X, left.Y + right.Y);
    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}

internal readonly record struct TransformOverlayHandleGeometry(
    TransformHandleKind Kind,
    PointF Point,
    PointF Anchor);

internal readonly record struct TransformOverlayScreenGeometry(
    PointF TopLeft,
    PointF TopRight,
    PointF BottomRight,
    PointF BottomLeft,
    TransformOverlayHandleGeometry[] ResizeHandles,
    TransformOverlayHandleGeometry[] RotationHandles,
    TransformOverlayHandleGeometry[] SkewHandles)
{
    public bool Contains(PointF point)
    {
        var hasPositive = false;
        var hasNegative = false;
        CheckEdge(TopLeft, TopRight, point, ref hasPositive, ref hasNegative);
        CheckEdge(TopRight, BottomRight, point, ref hasPositive, ref hasNegative);
        CheckEdge(BottomRight, BottomLeft, point, ref hasPositive, ref hasNegative);
        CheckEdge(BottomLeft, TopLeft, point, ref hasPositive, ref hasNegative);
        return !(hasPositive && hasNegative);
    }

    private static void CheckEdge(
        PointF start,
        PointF end,
        PointF point,
        ref bool hasPositive,
        ref bool hasNegative)
    {
        var cross = (end.X - start.X) * (point.Y - start.Y)
            - (end.Y - start.Y) * (point.X - start.X);
        if (cross > 0.01f) hasPositive = true;
        else if (cross < -0.01f) hasNegative = true;
    }
}

internal readonly record struct CubicDrawingPreviewSegment(
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct FillEdgeBezierOverlaySegment(
    int PartIndex,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct FillEdgeBezierOverlayHit(int PartIndex, EditHandleKind Handle)
{
    public bool IsValid => PartIndex >= 0;
    public static FillEdgeBezierOverlayHit None => new(-1, EditHandleKind.None);
}

internal enum SelectionHighlightKind
{
    Fill,
    Stroke
}

internal sealed class StageControl : Control
{
    private const int WmPointerUpdate = 0x0245;
    private const int WmPointerDown = 0x0246;
    private const int WmPointerUp = 0x0247;
    private const int WmPointerCaptureChanged = 0x024C;
    private const int MaxPenHistoryEntries = 512;
    private const int MaxSelectionOutlines = 512;
    private const float EndpointHandleHitRadiusPixels = 14;
    private const float ControlHandleHitRadiusPixels = 16;
    private const float MaxControlHandleHitRadiusPixels = 32;
    private const float FillEdgeBezierCurveHitRadiusPixels = 6;
    private const float FillEdgeBezierAnchorHitRadiusPixels = 9;
    private const float FillEdgeBezierControlHitRadiusPixels = 10;
    private const int BrushColorPaletteSwatchSize = 30;
    private const int BrushColorPaletteRadiusPixels = 58;
    private const int MaxBrushColorPaletteColors = 8;
    private const int MaxGdiBrushCacheEntries = 1024;
    private const float FillEdgeCoverageWidthPixels = 0.8f;
    private const double FillAnimationDurationMilliseconds = 420;
    private const double SelectionHighlightCycleMilliseconds = 1_100;
    private const double MarqueePreviewFrameBudgetMilliseconds = 8;
    private const int MarqueePreviewObjectThreshold = 2_000;
    private readonly Dictionary<int, SolidBrush> _brushCache = new(512);
    private readonly Pen _gridPen = new(Color.FromArgb(150, 58, 64, 69));
    private readonly Pen _strokePen = new(Color.FromArgb(210, 10, 12, 14));
    private readonly Pen _selectionPen = SelectionPen(Color.FromArgb(255, 255, 235, 120), 2.5f);
    private readonly Pen _selectionGlowPen = SelectionPen(Color.FromArgb(190, 32, 172, 255), 7);
    private readonly Pen _selectionOuterGlowPen = SelectionPen(Color.FromArgb(95, 80, 210, 255), 12);
    private readonly Pen _multiSelectionPen = SelectionPen(Color.FromArgb(235, 112, 220, 255), 1.5f);
    private readonly Pen _multiSelectionGlowPen = SelectionPen(Color.FromArgb(135, 32, 172, 255), 5);
    private readonly Pen _multiSelectionOuterGlowPen = SelectionPen(Color.FromArgb(70, 80, 210, 255), 8);
    private readonly Pen _drawingObjectSelectionPen = SelectionPen(Color.FromArgb(255, 118, 255, 170), 2.2f);
    private readonly Pen _drawingObjectSelectionGlowPen = SelectionPen(Color.FromArgb(185, 38, 238, 122), 7);
    private readonly Pen _drawingObjectSelectionOuterGlowPen = SelectionPen(Color.FromArgb(82, 24, 255, 104), 14);
    private readonly Pen _guidePen = new(Color.FromArgb(190, 112, 204, 255), 1);
    private readonly Pen _previewGuidePen = new(Color.FromArgb(170, 255, 255, 255), 1);
    private readonly Pen _marqueePen = new(Color.FromArgb(230, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
    private readonly SolidBrush _marqueeBrush = new(Color.FromArgb(34, 112, 204, 255));
    private readonly SolidBrush _handleBrush = new(Color.FromArgb(255, 255, 240, 168));
    private readonly SolidBrush _bezierHandleBrush = new(Color.FromArgb(255, 112, 204, 255));
    private readonly Pen _textAreaGlowPen = new(Color.FromArgb(80, 79, 179, 162), 4f);
    private readonly Pen _textAreaPen = new(Color.FromArgb(255, 79, 179, 162), 1.6f);
    private readonly SolidBrush _textAreaHandleBrush = new(Color.FromArgb(255, 79, 179, 162));
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly System.Windows.Forms.Timer _fillAnimationTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _selectionHighlightTimer = new() { Interval = 40 };
    private readonly Direct2DStageRenderer _direct2DRenderer = new();
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private LayerBlendCompositor? _layerBlendCompositor;
    private readonly MarqueeOverlayWindow _marqueeOverlay;
    private readonly HashSet<uint> _handledPenPointers = [];
    private PointerPenInfo[] _penHistoryBuffer = new PointerPenInfo[32];
    private int _selectedObject = -1;
    private int[] _selectedObjects = Array.Empty<int>();
    private DrawingElementHit[] _selectedElements = Array.Empty<DrawingElementHit>();
    private DrawingElementHit _hoveredLineElement = DrawingElementHit.None;
    private TransformOverlayFrame _transformFrame;
    private RectangleF _transformBounds = RectangleF.Empty;
    private PointF _transformFocus;
    private RectangleF _drawingObjectSelectionBounds = RectangleF.Empty;
    private PointF _drawingObjectAnchor;
    private readonly Dictionary<int, SelectedFillOwnerCacheEntry> _selectedFillCache = new();
    private readonly Dictionary<(int ObjectIndex, DrawingElementKind Kind), SelectedPolylineOwnerCacheEntry> _selectedPolylineCache = new();
    private float? _pendingVisibleWorldWidth;
    private float _referenceYaw = -0.72f;
    private float _referencePitch = 0.76f;
    private float _referenceDistance = 12000;
    private float _referenceZoomScale = 1;
    private float _referenceTargetX;
    private float _referenceTargetY;
    private float _referenceTargetZ;
    private float _worldGridOpacity = 0.1f;
    private WorldGridType _worldGridType;
    private PointF[][] _fillPreviewContours = Array.Empty<PointF[]>();
    private Color _fillPreviewColor = Color.White;
    private PointF[][] _fillAnimationContours = Array.Empty<PointF[]>();
    private PointF _fillAnimationOrigin;
    private Color _fillAnimationColor = Color.White;
    private float _fillAnimationMaxRadiusWorld;
    private float _fillAnimationProgress = 1f;
    private long _fillAnimationStartedAt;
    private float _selectionHighlightPhase;
    private long _selectionHighlightStartedAt;
    private bool _fillToolCursorVisible;
    private Point _fillToolCursorScreen;
    private Color _fillToolCursorColor = Color.White;
    private bool _gradientOverlayVisible;
    private PointF _gradientOverlayStart;
    private PointF _gradientOverlayEnd;
    private Color _gradientOverlayStartColor = Color.White;
    private Color _gradientOverlayEndColor = Color.White;
    private GradientKind _gradientOverlayKind;
    private GradientStop[] _gradientOverlayStops = [new GradientStop(0, Color.White), new GradientStop(1, Color.White)];
    private FillEdgeBezierOverlaySegment[] _fillEdgeBezierOverlaySegments = [];
    private int _fillEdgeBezierOverlayTargetObject = -1;
    private int _fillEdgeBezierOverlayActivePartIndex = -1;
    private long _fillEdgeBezierOverlayRevision;
    private Point _brushColorPaletteCenter;
    private Color[] _brushColorPaletteColors = [];
    private int _brushColorPaletteHoveredIndex = -1;
    private bool _paintFailureLogged;
    private bool _disposingResources;
    private int _scenePassSequence;
    private double _lastFrameRenderMilliseconds;
    private bool _marqueeSceneInvalidationPending;

    private readonly record struct SelectedFillOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingFillPartGeometry[] Parts);

    private readonly record struct SelectedPolylineOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingPolylinePartGeometry[] Parts);

    internal enum PenPointerEventKind
    {
        Down,
        Update,
        Up,
        CaptureLost
    }

    internal readonly record struct PenPointerSample(
        Point Location,
        float Pressure,
        bool IsInContact,
        bool IsPrimary,
        bool IsEraser,
        bool HasBarrelButton,
        uint TimestampMilliseconds);

    internal sealed class PenPointerEventArgs : EventArgs
    {
        public PenPointerEventArgs(uint pointerId, PenPointerEventKind kind, IReadOnlyList<PenPointerSample> samples)
        {
            PointerId = pointerId;
            Kind = kind;
            Samples = samples;
        }

        public uint PointerId { get; }
        public PenPointerEventKind Kind { get; }
        public IReadOnlyList<PenPointerSample> Samples { get; }
        public bool Handled { get; set; }
    }

    public VectorScene Scene { get; internal set; }
    public VectorScene? UnderlayScene { get; private set; }
    public VectorScene? OnionSkinScene { get; private set; }
    public VectorScene? DragPreviewScene { get; private set; }
    private VectorScene? _dragPreviewSourceScene;
    private HashSet<int> _dragPreviewHiddenObjects = [];
    private VectorScene? _editingTextScene;
    private int _editingTextObject = -1;
    public SceneDimension ReferenceDimension { get; private set; } = SceneDimension.TwoD;
    public CameraProjection ReferenceProjection { get; private set; } = CameraProjection.Orthographic;
    public float ReferenceYaw => _referenceYaw;
    public float ReferencePitch => _referencePitch;
    public float ReferenceDistance => _referenceDistance;
    public float ReferenceZoomScale => _referenceZoomScale;
    public float ReferenceTargetX => _referenceTargetX;
    public float ReferenceTargetY => _referenceTargetY;
    public float ReferenceTargetZ => _referenceTargetZ;
    public float WorldGridOpacity
    {
        get => _worldGridOpacity;
        set
        {
            var next = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_worldGridOpacity - next) < 0.0001f) return;
            _worldGridOpacity = next;
            Invalidate();
        }
    }
    public WorldGridType WorldGridType
    {
        get => _worldGridType;
        set
        {
            var next = Enum.IsDefined(value) ? value : VectorAnimationEngine.WorldGridType.Cartesian;
            if (_worldGridType == next) return;
            _worldGridType = next;
            Invalidate();
        }
    }
    internal WorldGridScale CurrentWorldGridScale => WorldGridLayout.Resolve(WorldLengthToScreen(1));
    public float AdaptiveGridSnapStep => CurrentWorldGridScale.StepWorld;
    internal GoldenSpiralGridGeometry ResolveGoldenSpiralGrid() => GoldenSpiralGridLayout.Resolve(
        VisibleWorldBounds(),
        AdaptiveGridSnapStep,
        ScreenLengthToWorld(0.5f));
    internal PolarGridGeometry ResolvePolarGrid() => PolarGridLayout.Resolve(
        VisibleWorldBounds(),
        AdaptiveGridSnapStep);
    public int Frame { get; set; }
    public int SelectedObject
    {
        get => _selectedObject;
        set
        {
            _selectedObject = value;
            _selectedObjects = value >= 0 && value < Scene.ObjectCount ? new[] { value } : Array.Empty<int>();
            SelectedElement = DrawingElementHit.None;
            _selectedElements = Array.Empty<DrawingElementHit>();
            InvalidateSelectedFillCache();
            UpdateSelectionHighlightAnimation();
            Invalidate();
        }
    }

    public IReadOnlyList<int> SelectedObjects => _selectedObjects;
    public IReadOnlyList<DrawingElementHit> SelectedElements => _selectedElements;
    public DrawingElementHit SelectedElement { get; private set; } = DrawingElementHit.None;
    public bool PenPathHandlesVisible { get; private set; }
    public DrawingElementHit HoveredLineElement => _hoveredLineElement;
    public bool TransformMode { get; private set; }
    public bool TransformBoundsVisible { get; private set; }
    public TransformOverlayFrame TransformFrame => _transformFrame;
    public RectangleF TransformBounds => _transformBounds;
    public PointF TransformFocus => _transformFocus;
    public bool DrawingObjectSelectionVisible { get; private set; }
    public RectangleF DrawingObjectSelectionBounds => _drawingObjectSelectionBounds;
    public bool DrawingObjectAnchorVisible { get; private set; }
    public PointF DrawingObjectAnchor => _drawingObjectAnchor;
    internal static Color TextAreaBorderColor => Color.FromArgb(255, 79, 179, 162);
    internal static Color TextAreaGlowColor => Color.FromArgb(80, 79, 179, 162);
    public bool MarqueeVisible { get; private set; }
    public Point MarqueeStart { get; private set; }
    public Point MarqueeEnd { get; private set; }
    internal bool MarqueeLodPreviewActive { get; private set; }
    internal bool MarqueeOverlayActive { get; private set; }
    internal Rectangle MarqueeOverlayScreenBounds => _marqueeOverlay.ScreenBounds;
    internal long MarqueeOverlayUpdateCount => _marqueeOverlay.UpdateCount;
    public bool DrawingPreviewVisible { get; private set; }
    public PointF DrawingPreviewStart { get; private set; }
    public PointF DrawingPreviewEnd { get; private set; }
    public PointF DrawingPreviewControl { get; private set; }
    public bool DrawingPreviewHasCurve { get; private set; }
    public IReadOnlyList<CubicDrawingPreviewSegment> DrawingPreviewCurveSegments { get; private set; } = Array.Empty<CubicDrawingPreviewSegment>();
    public PointF DrawingPreviewControl2 { get; private set; }
    public ShapeKind DrawingPreviewShape { get; private set; } = ShapeKind.Rectangle;
    public int DrawingPreviewShapeVertexCount { get; private set; }
    public Color DrawingPreviewColor { get; private set; } = Color.White;
    public float DrawingPreviewStroke { get; private set; } = 2;
    public bool PenAnchorGuidesVisible { get; private set; }
    public PointF PenAnchorGuidePoint { get; private set; }
    public bool PenAnchorGuideVertical { get; private set; }
    public bool PenAnchorGuideHorizontal { get; private set; }
    public bool PenAnchorGuideSnapped { get; private set; }
    public bool PenAnchorGuideInsertion { get; private set; }
    public bool PenDirectionHandlesVisible { get; private set; }
    public PointF PenDirectionAnchor { get; private set; }
    public PointF? PenDirectionIncoming { get; private set; }
    public PointF? PenDirectionOutgoing { get; private set; }
    public bool FreehandPreviewVisible { get; private set; }
    public IReadOnlyList<PointF> FreehandPreviewPoints { get; private set; } = Array.Empty<PointF>();
    public IReadOnlyList<float> FreehandPreviewDiameters { get; private set; } = Array.Empty<float>();
    public Color FreehandPreviewColor { get; private set; } = Color.White;
    public float FreehandPreviewStroke { get; private set; } = 2;
    public BrushShape? FreehandPreviewBrushShape { get; private set; }
    public bool BrushTipCursorVisible { get; private set; }
    public Point BrushTipCursorScreen { get; private set; }
    public float BrushTipCursorRadiusPixels { get; private set; }
    private float BrushTipCursorDiameterWorld { get; set; }
    public bool BrushTipCursorIsEraser { get; private set; }
    public BrushShape? BrushTipCursorShape { get; private set; }
    public bool FillPreviewVisible => _fillPreviewContours.Length > 0;
    public IReadOnlyList<PointF[]> FillPreviewContours => _fillPreviewContours;
    public Color FillPreviewColor => _fillPreviewColor;
    public bool FillToolCursorVisible => _fillToolCursorVisible;
    public Point FillToolCursorScreen => _fillToolCursorScreen;
    public Color FillToolCursorColor => _fillToolCursorColor;
    public bool GradientOverlayVisible => _gradientOverlayVisible;
    public PointF GradientOverlayStart => _gradientOverlayStart;
    public PointF GradientOverlayEnd => _gradientOverlayEnd;
    public Color GradientOverlayStartColor => _gradientOverlayStartColor;
    public Color GradientOverlayEndColor => _gradientOverlayEndColor;
    public GradientKind GradientOverlayKind => _gradientOverlayKind;
    public IReadOnlyList<GradientStop> GradientOverlayStops => _gradientOverlayStops;
    public bool FillEdgeBezierOverlayVisible => _fillEdgeBezierOverlayTargetObject >= 0
        && _fillEdgeBezierOverlaySegments.Length > 0;
    public int FillEdgeBezierOverlayTargetObject => _fillEdgeBezierOverlayTargetObject;
    public int FillEdgeBezierOverlayActivePartIndex => _fillEdgeBezierOverlayActivePartIndex;
    public IReadOnlyList<FillEdgeBezierOverlaySegment> FillEdgeBezierOverlaySegments => _fillEdgeBezierOverlaySegments;
    public bool FillEdgeBezierPointerEditing { get; private set; }
    internal long FillEdgeBezierOverlayRevision => _fillEdgeBezierOverlayRevision;
    public bool BrushColorPaletteVisible => _brushColorPaletteColors.Length > 0;
    public Point BrushColorPaletteCenter => _brushColorPaletteCenter;
    public IReadOnlyList<Color> BrushColorPaletteColors => _brushColorPaletteColors;
    public int BrushColorPaletteHoveredIndex => _brushColorPaletteHoveredIndex;
    public int BrushColorPaletteRadius => BrushColorPaletteRadiusPixels;
    public bool FillAnimationVisible => _fillAnimationContours.Length > 0 && _fillAnimationProgress < 1f;
    public IReadOnlyList<PointF[]> FillAnimationContours => _fillAnimationContours;
    public PointF FillAnimationOrigin => _fillAnimationOrigin;
    public Color FillAnimationColor => _fillAnimationColor;
    public Color FillAnimationGlowColor => LightenForFillAnimation(_fillAnimationColor);
    public float FillAnimationProgress => _fillAnimationProgress;
    public float FillAnimationBloomProgress => SmoothStep(_fillAnimationProgress);
    public float FillAnimationFade => 1f - SmoothStep(Math.Clamp((_fillAnimationProgress - 0.68f) / 0.32f, 0f, 1f));
    public float FillAnimationBloomRadiusWorld => Math.Max(1f, _fillAnimationMaxRadiusWorld * FillAnimationBloomProgress);
    public float CameraX { get; private set; }
    public float CameraY { get; private set; }
    public float Zoom { get; private set; } = 1;
    public RenderStats LastStats { get; private set; }
    public bool LastFrameUsedDirect2D { get; private set; }
    internal int LastOnionSkinScenePassOrder { get; private set; }
    internal int LastUnderlayScenePassOrder { get; private set; }
    internal int LastEditableScenePassOrder { get; private set; }
    internal bool GpuAccelerationActive => _direct2DRenderer.HardwareAccelerationActive;
    internal bool ImmediateGpuPresentationEnabled => _direct2DRenderer.ImmediatePresentationEnabled;
    internal double LastDirect2DCommandMilliseconds => _direct2DRenderer.LastCommandMilliseconds;
    internal double LastDirect2DPresentMilliseconds => _direct2DRenderer.LastPresentMilliseconds;
    internal int LastDirect2DLodBitmapSubmissions => _direct2DRenderer.LastLodBitmapSubmissions;
    internal int LastDirect2DLodBitmapBuilds => _direct2DRenderer.LastLodBitmapBuilds;
    internal int LastDirect2DLodDetailObjectDraws => _direct2DRenderer.LastLodDetailObjectDraws;
    internal int LastDirect2DGradientBrushCacheBuilds => _direct2DRenderer.LastGradientBrushCacheBuilds;
    internal int LastDirect2DGradientBrushCacheReuses => _direct2DRenderer.LastGradientBrushCacheReuses;
    internal int LastDirect2DShapeGradientBitmapCacheBuilds => _direct2DRenderer.LastShapeGradientBitmapCacheBuilds;
    internal int LastDirect2DShapeGradientBitmapCacheReuses => _direct2DRenderer.LastShapeGradientBitmapCacheReuses;
    internal int LastDirect2DShapeGradientMaskGeometryCacheBuilds => _direct2DRenderer.LastShapeGradientMaskGeometryCacheBuilds;
    internal int LastDirect2DShapeGradientMaskGeometryCacheReuses => _direct2DRenderer.LastShapeGradientMaskGeometryCacheReuses;
    internal int LastDirect2DPathGradientBrushCacheBuilds => _direct2DRenderer.LastPathGradientBrushCacheBuilds;
    internal int LastDirect2DPathGradientBrushCacheReuses => _direct2DRenderer.LastPathGradientBrushCacheReuses;
    internal int LastDirect2DLineGeometryCacheBuilds => _direct2DRenderer.LastLineGeometryCacheBuilds;
    internal int LastDirect2DLineGeometryCacheReuses => _direct2DRenderer.LastLineGeometryCacheReuses;
    internal bool SelectionHighlightAnimating => _selectionHighlightTimer.Enabled;
    internal float SelectionHighlightPulse => 0.5f + 0.5f * MathF.Sin(_selectionHighlightPhase * MathF.Tau);
    internal double LastFrameRenderMilliseconds => _lastFrameRenderMilliseconds;
    public event EventHandler? FrameRendered;
    public event EventHandler? ViewChanged;
    internal event EventHandler<PenPointerEventArgs>? PenPointerInput;

    internal bool HasCachedDirect2DFreehandGeometry(VectorScene scene)
    {
        return _direct2DRenderer.HasCachedFreehandGeometry(scene);
    }

    internal void ReloadRenderingModuleForHotReload()
    {
        ResetFillEdgeBezierOverlay(invalidate: false);
        _direct2DRenderer.ReloadRuntimeResources();
        _layerBlendCompositor?.Dispose();
        _layerBlendCompositor = null;
        ImportedSvgRasterizer.ClearCache();
        foreach (var brush in _brushCache.Values) brush.Dispose();
        _brushCache.Clear();
        _paintFailureLogged = false;
        LastFrameUsedDirect2D = false;
        InvalidateSelectedFillCache();
        Invalidate();
    }

    public StageControl(VectorScene scene)
    {
        Scene = scene;
        _marqueeOverlay = new MarqueeOverlayWindow(this);
        DoubleBuffered = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.Opaque
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.StandardClick
            | ControlStyles.StandardDoubleClick,
            true);
        TabStop = false;
        BackColor = Color.FromArgb(17, 19, 21);
        _fillAnimationTimer.Tick += (_, _) => TickFillAnimation();
        _selectionHighlightTimer.Tick += (_, _) => TickSelectionHighlight();
    }

    public void BindScene(VectorScene scene)
    {
        ResetFillEdgeBezierOverlay(invalidate: false);
        FillEdgeBezierPointerEditing = false;
        Scene = scene;
        UnderlayScene = null;
        OnionSkinScene = null;
        DragPreviewScene = null;
        _dragPreviewSourceScene = null;
        _dragPreviewHiddenObjects.Clear();
        _editingTextScene = null;
        _editingTextObject = -1;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        _selectedObject = -1;
        _selectedObjects = Array.Empty<int>();
        UpdateSelectionHighlightAnimation();
        ClearDrawingPreview();
        ClearFreehandPreview();
        ClearMarquee();
        ClearFillPreview();
        ClearFillAnimation();
        ClearBrushColorPalette();
        _direct2DRenderer.Resize(ClientSize);
        Invalidate();
        RaiseViewChanged();
    }

    public void BindUnderlayScene(VectorScene? scene)
    {
        UnderlayScene = scene is { ObjectCount: > 0 } ? scene : null;
        Invalidate();
    }

    public void BindOnionSkinScene(VectorScene? scene)
    {
        OnionSkinScene = scene is { ObjectCount: > 0 } ? scene : null;
        Invalidate();
    }

    public void BindDragPreviewScene(
        VectorScene? scene,
        VectorScene? sourceScene = null,
        IReadOnlyCollection<int>? hiddenSourceObjects = null)
    {
        DragPreviewScene = scene is { ObjectCount: > 0 } ? scene : null;
        _dragPreviewSourceScene = DragPreviewScene is not null ? sourceScene : null;
        _dragPreviewHiddenObjects = DragPreviewScene is not null && hiddenSourceObjects is { Count: > 0 }
            ? hiddenSourceObjects.Where(index => index >= 0).ToHashSet()
            : [];
        Invalidate();
    }

    internal bool IsHiddenByDragPreview(VectorScene scene, int objectIndex)
    {
        return ReferenceEquals(scene, _dragPreviewSourceScene) && _dragPreviewHiddenObjects.Contains(objectIndex);
    }

    internal bool IsObjectHiddenForRendering(VectorScene scene, int objectIndex)
    {
        return IsHiddenByDragPreview(scene, objectIndex)
            || (ReferenceEquals(scene, _editingTextScene) && objectIndex == _editingTextObject);
    }

    public void SetEditingTextObject(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(objectIndex));
        }
        if (Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            throw new ArgumentException("The editing object must be a text object.", nameof(objectIndex));
        }
        if (ReferenceEquals(_editingTextScene, Scene) && _editingTextObject == objectIndex) return;

        _editingTextScene = Scene;
        _editingTextObject = objectIndex;
        Invalidate();
    }

    public void ClearEditingTextObject()
    {
        if (_editingTextScene is null && _editingTextObject < 0) return;
        _editingTextScene = null;
        _editingTextObject = -1;
        Invalidate();
    }

    public void ConfigureReferenceView(SceneDefinition? scene)
    {
        ReferenceDimension = scene?.Dimension ?? SceneDimension.TwoD;
        ReferenceProjection = scene?.Camera.Projection ?? CameraProjection.Orthographic;
        Invalidate();
    }

    public void RotateReferenceCamera(float dx, float dy)
    {
        _referenceYaw = NormalizeRadians(_referenceYaw + dx * 0.01f);
        _referencePitch = Math.Clamp(_referencePitch + dy * 0.01f, -1.5f, 1.5f);
        Invalidate();
    }

    public void DollyReferenceCamera(float wheelDelta)
    {
        var factor = wheelDelta > 0 ? 0.9f : 1.1f;
        _referenceDistance = Math.Clamp(_referenceDistance * factor, 2000, 80000);
        Invalidate();
    }

    public void DollyReferenceCameraByPixels(float dy)
    {
        var factor = Math.Clamp(Math.Exp(dy * 0.012), 0.2, 5.0);
        _referenceDistance = Math.Clamp(_referenceDistance * (float)factor, 2000, 80000);
        Invalidate();
    }

    public void ZoomReferenceCamera(float factor)
    {
        _referenceZoomScale = Math.Clamp(_referenceZoomScale * factor, 0.25f, 8f);
        Invalidate();
    }

    public void PanReferenceCamera(float dx, float dy)
    {
        var yawCos = MathF.Cos(_referenceYaw);
        var yawSin = MathF.Sin(_referenceYaw);
        var pitchCos = MathF.Cos(_referencePitch);
        var pitchSin = MathF.Sin(_referencePitch);
        var rightX = yawCos;
        var rightZ = -yawSin;
        var upX = -pitchSin * yawSin;
        var upY = pitchCos;
        var upZ = -pitchSin * yawCos;
        var distanceScale = Math.Clamp(_referenceDistance / 12000f, 0.25f, 8f);
        var worldPerPixel = distanceScale / Math.Max(0.002f, 0.035f * _referenceZoomScale);
        var horizontal = -dx * worldPerPixel;
        var vertical = dy * worldPerPixel;

        _referenceTargetX = Math.Clamp(_referenceTargetX + rightX * horizontal + upX * vertical, -5_000_000, 5_000_000);
        _referenceTargetY = Math.Clamp(_referenceTargetY + upY * vertical, -5_000_000, 5_000_000);
        _referenceTargetZ = Math.Clamp(_referenceTargetZ + rightZ * horizontal + upZ * vertical, -5_000_000, 5_000_000);
        Invalidate();
    }

    public void SetReferenceCameraOrientation(float yaw, float pitch)
    {
        _referenceYaw = NormalizeRadians(yaw);
        _referencePitch = Math.Clamp(pitch, -1.5f, 1.5f);
        Invalidate();
    }

    public void ResetReferenceCameraView()
    {
        _referenceYaw = -0.72f;
        _referencePitch = 0.76f;
        _referenceDistance = 12000;
        _referenceZoomScale = 1;
        _referenceTargetX = 0;
        _referenceTargetY = 0;
        _referenceTargetZ = 0;
        Invalidate();
    }

    internal StageViewState CaptureViewState()
    {
        return new StageViewState(
            CameraX,
            CameraY,
            Zoom,
            _referenceYaw,
            _referencePitch,
            _referenceDistance,
            _referenceZoomScale,
            _referenceTargetX,
            _referenceTargetY,
            _referenceTargetZ,
            WorldGridOpacity,
            WorldGridType);
    }

    internal void RestoreViewState(StageViewState state)
    {
        _pendingVisibleWorldWidth = null;
        CameraX = Math.Clamp(state.CameraX, -5_000_000, 5_000_000);
        CameraY = Math.Clamp(state.CameraY, -5_000_000, 5_000_000);
        Zoom = Math.Clamp(state.Zoom, 0.02f, 64f);
        _referenceYaw = NormalizeRadians(state.ReferenceYaw);
        _referencePitch = Math.Clamp(state.ReferencePitch, -1.5f, 1.5f);
        _referenceDistance = Math.Clamp(state.ReferenceDistance, 2000, 80000);
        _referenceZoomScale = Math.Clamp(state.ReferenceZoomScale, 0.25f, 8f);
        _referenceTargetX = Math.Clamp(state.ReferenceTargetX, -5_000_000, 5_000_000);
        _referenceTargetY = Math.Clamp(state.ReferenceTargetY, -5_000_000, 5_000_000);
        _referenceTargetZ = Math.Clamp(state.ReferenceTargetZ, -5_000_000, 5_000_000);
        WorldGridOpacity = state.WorldGridOpacity;
        WorldGridType = state.WorldGridType;
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
    }

    private static float NormalizeRadians(float value)
    {
        while (value > MathF.PI) value -= MathF.Tau;
        while (value < -MathF.PI) value += MathF.Tau;
        return value;
    }

    public void Fit()
    {
        if (Width <= 0 || Height <= 0) return;
        _pendingVisibleWorldWidth = null;
        CameraX = 0;
        CameraY = 0;
        Zoom = (float)Math.Clamp(Math.Min(Width / VectorUnits.ToPixels(Scene.StageWidth), Height / VectorUnits.ToPixels(Scene.StageHeight)) * 0.9, 0.02, 64);
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
    }

    public void ResetDefaultView() => SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);

    public void SetVisibleWorldWidth(float vectorUnits) => SetVisibleWorldWidthCore(vectorUnits, raiseViewChanged: true);

    private void SetVisibleWorldWidthCore(float vectorUnits, bool raiseViewChanged)
    {
        vectorUnits = Math.Clamp(vectorUnits, 1, Scene.StageWidth);
        if (Width <= 0 || Height <= 0)
        {
            _pendingVisibleWorldWidth = vectorUnits;
            return;
        }

        _pendingVisibleWorldWidth = null;
        CameraX = 0;
        CameraY = 0;
        Zoom = (float)Math.Clamp(Width / Math.Max(1, VectorUnits.ToPixels(vectorUnits)), 0.02, 64);
        RefreshBrushTipCursorScale();
        Invalidate();
        if (raiseViewChanged) RaiseViewChanged();
    }

    public PointF ScreenToWorld(Point screen)
    {
        return new PointF(
            CameraX + VectorUnits.FromPixels((screen.X - Width * 0.5f) / Zoom),
            CameraY + VectorUnits.FromPixels((screen.Y - Height * 0.5f) / Zoom));
    }

    public PointF WorldToScreen(float x, float y)
    {
        return new PointF(
            Width * 0.5f + VectorUnits.ToPixels(x - CameraX) * Zoom,
            Height * 0.5f + VectorUnits.ToPixels(y - CameraY) * Zoom);
    }

    public void Pan(float dx, float dy)
    {
        CameraX -= VectorUnits.FromPixels(dx / Zoom);
        CameraY -= VectorUnits.FromPixels(dy / Zoom);
        Invalidate();
        RaiseViewChanged();
    }

    public void ZoomAt(Point screen, float factor)
    {
        var before = ScreenToWorld(screen);
        Zoom = (float)Math.Clamp(Zoom * factor, 0.02, 64);
        var after = ScreenToWorld(screen);
        CameraX += before.X - after.X;
        CameraY += before.Y - after.Y;
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
    }

    public float WorldLengthToScreen(float vectorUnits) => Math.Abs(VectorUnits.ToPixels(vectorUnits)) * Zoom;

    public float ScreenLengthToWorld(float pixels) => Math.Abs(VectorUnits.FromPixels(pixels / Zoom));

    public RectangleF VisibleWorldBounds()
    {
        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Width, Height));
        return RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    public EditHandleKind HitTestHandle(Point screen, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= Scene.ObjectCount) return EditHandleKind.None;
        var shape = Scene.ShapeKind.Length > objectIndex ? Scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        if (SelectedElement.IsValid
            && SelectedElement.Key.ObjectIndex == objectIndex
            && SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var boundaryHandle = HitTestLineBezierHandle(screen, SelectedElement);
            if (boundaryHandle != EditHandleKind.None) return boundaryHandle;
        }
        if (_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex)
            && (shape != ShapeKind.Line
                || !_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex && hit.Key.Kind == DrawingElementKind.Stroke)))
        {
            return EditHandleKind.None;
        }

        if (shape == ShapeKind.Text)
        {
            if (!TextAreaResizeHandlesVisible(objectIndex)) return EditHandleKind.None;
            foreach (var handle in TextAreaResizeHandles())
            {
                if (Distance(screen, WorldToScreen(GetTextAreaHandleWorldPoint(objectIndex, handle))) <= 12)
                {
                    return handle;
                }
            }

            return EditHandleKind.None;
        }
        if (IsFreehandShape(shape) || shape == ShapeKind.Path) return EditHandleKind.None;
        if (shape == ShapeKind.Line)
        {
            var selectedLinePart = SelectedElement.IsValid
                && SelectedElement.Key.ObjectIndex == objectIndex
                && SelectedElement.Key.Kind == DrawingElementKind.Stroke
                ? SelectedElement
                : new DrawingElementHit(new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, 0), 0, 0, 1);
            return HitTestLineBezierHandle(screen, selectedLinePart);
        }

        foreach (var handle in BoundaryHandles())
        {
            var point = WorldToScreen(GetBoundaryHandleWorldPoint(objectIndex, handle));
            if (Distance(screen, point) <= 10) return handle;
        }

        return EditHandleKind.None;
    }

    public PointF GetBoundaryHandleWorldPoint(int objectIndex, EditHandleKind handle)
    {
        var scene = Scene;
        var halfW = scene.Width[objectIndex] * 0.5f;
        var halfH = scene.Height[objectIndex] * 0.5f;
        var local = handle switch
        {
            EditHandleKind.BoundsTopLeft => new PointF(-halfW, -halfH),
            EditHandleKind.BoundsTopRight => new PointF(halfW, -halfH),
            EditHandleKind.BoundsBottomRight => new PointF(halfW, halfH),
            EditHandleKind.BoundsBottomLeft => new PointF(-halfW, halfH),
            _ => PointF.Empty
        };

        return LocalToWorld(objectIndex, local);
    }

    internal PointF GetTextAreaHandleWorldPoint(int objectIndex, EditHandleKind handle)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            return PointF.Empty;
        }

        var halfWidth = Scene.Width[objectIndex] * 0.5f;
        var local = handle switch
        {
            EditHandleKind.TextAreaLeft => new PointF(-halfWidth, 0),
            EditHandleKind.TextAreaRight => new PointF(halfWidth, 0),
            _ => PointF.Empty
        };
        return LocalToWorld(objectIndex, local);
    }

    internal PointF[] GetTextAreaWorldCorners(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            return [];
        }

        var halfWidth = Scene.Width[objectIndex] * 0.5f;
        var halfHeight = Scene.Height[objectIndex] * 0.5f;
        return
        [
            LocalToWorld(objectIndex, new PointF(-halfWidth, -halfHeight)),
            LocalToWorld(objectIndex, new PointF(halfWidth, -halfHeight)),
            LocalToWorld(objectIndex, new PointF(halfWidth, halfHeight)),
            LocalToWorld(objectIndex, new PointF(-halfWidth, halfHeight))
        ];
    }

    internal bool TextAreaOverlayVisible(int objectIndex)
    {
        return !TransformMode
            && (uint)objectIndex < Scene.ObjectCount
            && Scene.ShapeKind[objectIndex] == ShapeKind.Text
            && !(ReferenceEquals(_editingTextScene, Scene) && _editingTextObject == objectIndex);
    }

    internal bool TextAreaResizeHandlesVisible(int objectIndex)
    {
        return TextAreaOverlayVisible(objectIndex)
            && _selectedElements.Length == 0
            && _selectedObjects.Length == 1
            && _selectedObject == objectIndex;
    }

    public void SetTransformOverlay(bool transformMode, RectangleF bounds, PointF? focus = null)
    {
        var frame = bounds.Width > 0.001f && bounds.Height > 0.001f
            ? TransformOverlayFrame.FromBounds(bounds)
            : default;
        SetTransformOverlay(transformMode, frame, focus);
    }

    public void SetTransformOverlay(bool transformMode, TransformOverlayFrame frame, PointF? focus = null)
    {
        var visible = transformMode && frame.IsValid;
        var nextFrame = visible ? frame : default;
        var nextBounds = visible ? frame.Bounds : RectangleF.Empty;
        var nextFocus = visible
            ? focus ?? frame.Center
            : PointF.Empty;
        if (TransformMode == transformMode
            && TransformBoundsVisible == visible
            && _transformFrame == nextFrame
            && _transformFocus == nextFocus)
        {
            return;
        }

        TransformMode = transformMode;
        TransformBoundsVisible = visible;
        _transformFrame = nextFrame;
        _transformBounds = nextBounds;
        _transformFocus = nextFocus;
        InvalidateSelectionState();
    }

    public void SetDrawingObjectSelectionOverlay(RectangleF bounds, PointF? anchor = null)
    {
        var visible = bounds.Width > 0.001f && bounds.Height > 0.001f;
        var anchorVisible = visible
            && anchor is { } point
            && float.IsFinite(point.X)
            && float.IsFinite(point.Y);
        var nextAnchor = anchorVisible ? anchor!.Value : PointF.Empty;
        if (DrawingObjectSelectionVisible == visible
            && _drawingObjectSelectionBounds == (visible ? bounds : RectangleF.Empty)
            && DrawingObjectAnchorVisible == anchorVisible
            && _drawingObjectAnchor == nextAnchor)
        {
            return;
        }

        DrawingObjectSelectionVisible = visible;
        _drawingObjectSelectionBounds = visible ? bounds : RectangleF.Empty;
        DrawingObjectAnchorVisible = anchorVisible;
        _drawingObjectAnchor = nextAnchor;
        InvalidateSelectionState();
    }

    public TransformHandleKind HitTestTransformHandle(Point screen)
    {
        if (!TransformBoundsVisible) return TransformHandleKind.None;
        if (Distance(screen, WorldToScreen(_transformFocus)) <= 10) return TransformHandleKind.Focus;
        var geometry = GetTransformOverlayScreenGeometry();
        foreach (var (kind, point, _) in geometry.ResizeHandles)
        {
            if (Distance(screen, point) <= 10) return kind;
        }

        foreach (var (kind, point, _) in geometry.RotationHandles)
        {
            if (Distance(screen, point) <= 11) return kind;
        }

        foreach (var (kind, point, _) in geometry.SkewHandles)
        {
            if (Distance(screen, point) <= 10) return kind;
        }

        return geometry.Contains(screen) ? TransformHandleKind.Move : TransformHandleKind.None;
    }

    internal TransformOverlayScreenGeometry GetTransformOverlayScreenGeometry()
    {
        var topLeft = WorldToScreen(_transformFrame.TopLeft);
        var topRight = WorldToScreen(_transformFrame.TopRight);
        var bottomRight = WorldToScreen(_transformFrame.BottomRight);
        var bottomLeft = WorldToScreen(_transformFrame.BottomLeft);
        var center = Midpoint(topLeft, bottomRight);
        var top = Midpoint(topLeft, topRight);
        var right = Midpoint(topRight, bottomRight);
        var bottom = Midpoint(bottomLeft, bottomRight);
        var left = Midpoint(topLeft, bottomLeft);
        var axisX = UnitVector(topLeft, topRight);
        var axisY = UnitVector(topLeft, bottomLeft);
        const float offset = 19;

        return new TransformOverlayScreenGeometry(
            topLeft,
            topRight,
            bottomRight,
            bottomLeft,
            [
                new(TransformHandleKind.TopLeft, topLeft, topLeft),
                new(TransformHandleKind.Top, top, top),
                new(TransformHandleKind.TopRight, topRight, topRight),
                new(TransformHandleKind.Right, right, right),
                new(TransformHandleKind.BottomRight, bottomRight, bottomRight),
                new(TransformHandleKind.Bottom, bottom, bottom),
                new(TransformHandleKind.BottomLeft, bottomLeft, bottomLeft),
                new(TransformHandleKind.Left, left, left)
            ],
            [
                new(TransformHandleKind.RotateTopLeft, Offset(topLeft, axisX, -offset, axisY, -offset), topLeft),
                new(TransformHandleKind.RotateTopRight, Offset(topRight, axisX, offset, axisY, -offset), topRight),
                new(TransformHandleKind.RotateBottomRight, Offset(bottomRight, axisX, offset, axisY, offset), bottomRight),
                new(TransformHandleKind.RotateBottomLeft, Offset(bottomLeft, axisX, -offset, axisY, offset), bottomLeft)
            ],
            [
                new(TransformHandleKind.SkewTop, OffsetFromEdge(topLeft, topRight, top, center, offset), top),
                new(TransformHandleKind.SkewRight, OffsetFromEdge(topRight, bottomRight, right, center, offset), right),
                new(TransformHandleKind.SkewBottom, OffsetFromEdge(bottomLeft, bottomRight, bottom, center, offset), bottom),
                new(TransformHandleKind.SkewLeft, OffsetFromEdge(topLeft, bottomLeft, left, center, offset), left)
            ]);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg is WmPointerDown or WmPointerUpdate or WmPointerUp or WmPointerCaptureChanged
            && TryDispatchPenPointer(ref message))
        {
            return;
        }

        base.WndProc(ref message);
    }

    private bool TryDispatchPenPointer(ref Message message)
    {
        var pointerId = unchecked((uint)message.WParam.ToInt64()) & 0xffffu;
        if (pointerId == 0) return false;

        var wasHandled = _handledPenPointers.Contains(pointerId);
        if (message.Msg == WmPointerCaptureChanged)
        {
            if (!wasHandled) return false;
            _handledPenPointers.Remove(pointerId);
            var captureLost = new PenPointerEventArgs(
                pointerId,
                PenPointerEventKind.CaptureLost,
                Array.Empty<PenPointerSample>())
            {
                Handled = true
            };
            PenPointerInput?.Invoke(this, captureLost);
            message.Result = IntPtr.Zero;
            return true;
        }

        var hasPenInfo = TryReadPenPointerSamples(pointerId, out var samples, out var pointerFlags);
        if (!hasPenInfo && !wasHandled) return false;

        var kind = (pointerFlags & PointerFlags.Canceled) != 0
            ? PenPointerEventKind.CaptureLost
            : message.Msg switch
            {
                WmPointerDown => PenPointerEventKind.Down,
                WmPointerUp => PenPointerEventKind.Up,
                _ => PenPointerEventKind.Update
            };
        var eventArgs = new PenPointerEventArgs(pointerId, kind, samples)
        {
            Handled = wasHandled
        };
        PenPointerInput?.Invoke(this, eventArgs);

        if (message.Msg == WmPointerDown && eventArgs.Handled) _handledPenPointers.Add(pointerId);
        if (kind is PenPointerEventKind.Up or PenPointerEventKind.CaptureLost) _handledPenPointers.Remove(pointerId);
        if (!eventArgs.Handled) return false;

        message.Result = IntPtr.Zero;
        return true;
    }

    private bool TryReadPenPointerSamples(
        uint pointerId,
        out PenPointerSample[] samples,
        out PointerFlags currentFlags)
    {
        samples = [];
        currentFlags = PointerFlags.None;
        if (!GetPointerPenInfo(pointerId, out var current)
            || current.PointerInfo.PointerType != PointerInputType.Pen)
        {
            return false;
        }

        currentFlags = current.PointerInfo.PointerFlags;
        var historyCount = (int)Math.Clamp(
            current.PointerInfo.HistoryCount,
            1u,
            (uint)MaxPenHistoryEntries);
        if (historyCount <= 1)
        {
            samples = [CreatePenPointerSample(current)];
            return true;
        }

        if (_penHistoryBuffer.Length < historyCount)
        {
            var capacity = Math.Min(
                MaxPenHistoryEntries,
                Math.Max(historyCount, _penHistoryBuffer.Length * 2));
            Array.Resize(ref _penHistoryBuffer, capacity);
        }

        var entriesCount = (uint)Math.Min(historyCount, _penHistoryBuffer.Length);
        if (!GetPointerPenInfoHistory(pointerId, ref entriesCount, _penHistoryBuffer))
        {
            samples = [CreatePenPointerSample(current)];
            return true;
        }

        var count = Math.Min((int)entriesCount, _penHistoryBuffer.Length);
        var ordered = new List<PenPointerSample>(count);
        for (var index = count - 1; index >= 0; index--)
        {
            var entry = _penHistoryBuffer[index];
            if (entry.PointerInfo.PointerType != PointerInputType.Pen) continue;
            ordered.Add(CreatePenPointerSample(entry));
        }

        samples = ordered.Count > 0 ? ordered.ToArray() : [CreatePenPointerSample(current)];
        return true;
    }

    private PenPointerSample CreatePenPointerSample(PointerPenInfo info)
    {
        var pointer = info.PointerInfo;
        var location = PointToClient(new Point(pointer.PixelLocation.X, pointer.PixelLocation.Y));
        var pressure = (info.PenMask & PenMask.Pressure) != 0
            ? Math.Clamp(info.Pressure / 1024f, 0f, 1f)
            : float.NaN;
        return new PenPointerSample(
            location,
            pressure,
            (pointer.PointerFlags & PointerFlags.InContact) != 0,
            (pointer.PointerFlags & PointerFlags.Primary) != 0,
            (info.PenFlags & (PenFlags.Inverted | PenFlags.Eraser)) != 0,
            (info.PenFlags & PenFlags.Barrel) != 0,
            pointer.Time);
    }

    private enum PointerInputType : uint
    {
        Pen = 3
    }

    [Flags]
    private enum PointerFlags : uint
    {
        None = 0,
        InContact = 0x00000004,
        Primary = 0x00002000,
        Canceled = 0x00008000
    }

    [Flags]
    private enum PenFlags : uint
    {
        None = 0,
        Barrel = 0x00000001,
        Inverted = 0x00000002,
        Eraser = 0x00000004
    }

    [Flags]
    private enum PenMask : uint
    {
        None = 0,
        Pressure = 0x00000001
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public PointerInputType PointerType;
        public uint PointerId;
        public uint FrameId;
        public PointerFlags PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr WindowTarget;
        public NativePoint PixelLocation;
        public NativePoint HimetricLocation;
        public NativePoint PixelLocationRaw;
        public NativePoint HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo PointerInfo;
        public PenFlags PenFlags;
        public PenMask PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo penInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfoHistory(
        uint pointerId,
        ref uint entriesCount,
        [Out] PointerPenInfo[] penInfo);

    protected override void OnPaint(PaintEventArgs e)
    {
        var frameStarted = Stopwatch.GetTimestamp();
        var rendered = false;
        try
        {
            if (_direct2DRenderer.TryRender(this, out var stats))
            {
                LastStats = stats;
                LastFrameUsedDirect2D = true;
                _paintFailureLogged = false;
            }
            else
            {
                DrawBufferedGdi(e.Graphics);
                LastFrameUsedDirect2D = false;
                _paintFailureLogged = false;
            }

            rendered = true;
        }
        catch (Exception ex)
        {
            if (!_paintFailureLogged)
            {
                AppLog.Error("Stage paint failed; drawing emergency background", ex);
                _paintFailureLogged = true;
            }

            try
            {
                _direct2DRenderer.ReleaseTarget();
            }
            catch
            {
                // A corrupted development session may fail before renderer cleanup runs.
            }
            DrawEmergencyBackground(e.Graphics);
            LastStats = default;
            LastFrameUsedDirect2D = false;
        }

        if (rendered)
        {
            _lastFrameRenderMilliseconds = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
            if (MarqueeVisible
                && !MarqueeOverlayActive
                && !MarqueeLodPreviewActive
                && _lastFrameRenderMilliseconds > MarqueePreviewFrameBudgetMilliseconds)
            {
                MarqueeLodPreviewActive = true;
            }
            FrameRendered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrawBufferedGdi(Graphics target)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var buffer = BufferedGraphicsManager.Current.Allocate(target, ClientRectangle);
        DrawGdi(buffer.Graphics);
        buffer.Render(target);
    }

    private void DrawEmergencyBackground(Graphics graphics)
    {
        try
        {
            graphics.ResetTransform();
            graphics.ResetClip();
            graphics.Clear(BackColor);
        }
        catch
        {
            // WinForms will retry painting after the next invalidation.
        }
    }

    private void DrawGdi(Graphics g)
    {
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;
        DrawGrid(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        BeginScenePassOrder();

        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            LastStats = default;
            DrawMarquee(g);
            return;
        }

        var editableScene = Scene;
        var objectDrawLimit = ObjectDrawLimit();
        var underlayStats = default(RenderStats);
        var onionSkinStats = default(RenderStats);
        var underlay = UnderlayScene;
        var underlayLimit = objectDrawLimit;
        var forceEditableObjectRenderer = FillEdgeBezierPointerEditing;
        if (underlay is not null
            && UsesObjectRenderer(underlay)
            && (forceEditableObjectRenderer || UsesObjectRenderer(editableScene)))
        {
            underlayLimit = objectDrawLimit - Math.Min(editableScene.ObjectCount, objectDrawLimit * 3 / 4);
        }

        if (OnionSkinScene is { } onionSkin)
        {
            var reservedUnderlayObjects = underlay is not null && UsesObjectRenderer(underlay)
                ? Math.Min(underlay.ObjectCount, underlayLimit)
                : 0;
            var editableCapacity = Math.Max(0, objectDrawLimit - reservedUnderlayObjects);
            var reservedEditableObjects = forceEditableObjectRenderer || UsesObjectRenderer(editableScene)
                ? Math.Min(editableScene.ObjectCount, editableCapacity)
                : 0;
            var onionSkinLimit = Math.Max(0, editableCapacity - reservedEditableObjects);
            RecordOnionSkinScenePass();
            onionSkinStats = DrawSceneGdi(g, onionSkin, onionSkinLimit);
        }

        if (underlay is not null)
        {
            RecordUnderlayScenePass();
            underlayStats = DrawSceneGdi(g, underlay, underlayLimit);
        }

        var editableLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects - onionSkinStats.DrawnObjects);
        RecordEditableScenePass();
        var editableStats = DrawSceneGdi(
            g,
            editableScene,
            editableLimit,
            forceObjectRenderer: forceEditableObjectRenderer);
        if (DragPreviewScene is { } dragPreview)
        {
            DrawSceneGdi(g, dragPreview, Math.Min(objectDrawLimit, 80_000));
        }
        LastStats = RenderStats.Combine(RenderStats.Combine(onionSkinStats, underlayStats), editableStats);
        if (editableStats.TileLod) DrawLodDetailObjects(g);
        if (!MarqueeLodPreviewActive) DrawActiveMaskOutline(g);
        DrawSelection(g);
        DrawFillEdgeBezierOverlay(g);
        DrawPenAnchorGuides(g);
        DrawDrawingPreview(g);
        DrawPenDirectionHandles(g);
        DrawFreehandPreview(g);
        DrawFillPreview(g);
        DrawFillAnimation(g);
        DrawGradientOverlay(g);
        DrawMarquee(g);
        DrawBrushTipCursor(g);
        DrawBrushColorPalette(g);
        DrawFillToolCursor(g);
    }

    private RenderStats DrawSceneGdi(
        Graphics graphics,
        VectorScene scene,
        int objectDrawLimit,
        bool forceObjectRenderer = false)
    {
        var editableScene = Scene;
        Scene = scene;
        try
        {
            var pixelZoom = EffectivePixelZoom();
            if (forceObjectRenderer)
            {
                return DrawObjects(graphics, int.MaxValue);
            }
            if (SceneRenderOrder.RequiresObjectRenderer(Scene)) return DrawObjects(graphics, objectDrawLimit);
            if (MarqueeLodPreviewActive && Scene.ObjectCount > 0)
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }
            if (Scene.HasDisplayLayerEffects || pixelZoom >= 0.18f) return DrawObjects(graphics, objectDrawLimit);
            if (Scene.ObjectCount >= 5000)
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }

            if (Scene.ObjectCount < SceneRenderOrder.DenseObjectLodMinimumVisibleObjects)
            {
                return DrawObjects(graphics, objectDrawLimit);
            }

            var bounds = VisibleWorldBounds();
            _renderOrder.Collect(Scene, bounds, Frame);
            if (SceneRenderOrder.ShouldUseDenseObjectLod(Scene, pixelZoom, bounds, _renderOrder))
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }

            return DrawCollectedObjects(graphics, objectDrawLimit);
        }
        finally
        {
            Scene = editableScene;
        }
    }

    private int ObjectDrawLimit() => EffectivePixelZoom() < 0.35f ? 65_000 : 160_000;

    internal void BeginScenePassOrder()
    {
        _scenePassSequence = 0;
        LastOnionSkinScenePassOrder = 0;
        LastUnderlayScenePassOrder = 0;
        LastEditableScenePassOrder = 0;
    }

    internal void RecordOnionSkinScenePass() => LastOnionSkinScenePassOrder = ++_scenePassSequence;

    internal void RecordUnderlayScenePass() => LastUnderlayScenePassOrder = ++_scenePassSequence;

    internal void RecordEditableScenePass() => LastEditableScenePassOrder = ++_scenePassSequence;

    private bool UsesObjectRenderer(VectorScene scene)
    {
        return scene.ObjectCount > 0
            && (SceneRenderOrder.RequiresObjectRenderer(scene)
                || scene.ObjectCount < 5000
                || scene.HasDisplayLayerEffects
                || EffectivePixelZoom() >= 0.18f);
    }

    private float EffectivePixelZoom() => Zoom * VectorUnits.PixelsPerUnit;

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // The stage is fully redrawn by Direct2D or the GDI fallback in OnPaint.
        // Letting WinForms erase the background first can produce visible flashes.
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _direct2DRenderer.Resize(ClientSize);
        if (_pendingVisibleWorldWidth is { } visibleWorldWidth) SetVisibleWorldWidthCore(visibleWorldWidth, raiseViewChanged: false);
        if (MarqueeVisible && MarqueeOverlayActive) UpdateMarqueeOverlay();
        RaiseViewChanged();
    }

    private void RaiseViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _marqueeOverlay.Prepare();
        UpdateSelectionHighlightAnimation();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _marqueeOverlay.Prepare();
        else _marqueeOverlay.Hide();
        UpdateSelectionHighlightAnimation();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _marqueeOverlay.Hide();
        _handledPenPointers.Clear();
        if (!_disposingResources) _selectionHighlightTimer.Stop();
        try
        {
            _direct2DRenderer.ReleaseTarget();
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D target release failed during handle destruction", ex);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposingResources = true;
            ResetFillEdgeBezierOverlay(invalidate: false);
            _fillAnimationTimer.Dispose();
            _selectionHighlightTimer.Dispose();
            _direct2DRenderer.Dispose();
            _layerBlendCompositor?.Dispose();
            _layerBlendCompositor = null;
            ImportedSvgRasterizer.ClearCache();
            _marqueeOverlay.Dispose();
            foreach (var item in _brushCache.Values) item.Dispose();
            _brushCache.Clear();
            _gridPen.Dispose();
            _strokePen.Dispose();
            _selectionPen.Dispose();
            _selectionGlowPen.Dispose();
            _selectionOuterGlowPen.Dispose();
            _multiSelectionPen.Dispose();
            _multiSelectionGlowPen.Dispose();
            _multiSelectionOuterGlowPen.Dispose();
            _drawingObjectSelectionPen.Dispose();
            _drawingObjectSelectionGlowPen.Dispose();
            _drawingObjectSelectionOuterGlowPen.Dispose();
            _guidePen.Dispose();
            _previewGuidePen.Dispose();
            _marqueePen.Dispose();
            _marqueeBrush.Dispose();
            _handleBrush.Dispose();
            _bezierHandleBrush.Dispose();
            _textAreaGlowPen.Dispose();
            _textAreaPen.Dispose();
            _textAreaHandleBrush.Dispose();
            _handleBorderPen.Dispose();
        }

        base.Dispose(disposing);
    }

    public void SetDrawingPreview(
        PointF start,
        PointF end,
        ShapeKind shape,
        Color color,
        float stroke,
        int shapeVertexCount = 0)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewEnd = end;
        DrawingPreviewControl = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        DrawingPreviewHasCurve = false;
        DrawingPreviewControl2 = DrawingPreviewControl;
        DrawingPreviewCurveSegments = Array.Empty<CubicDrawingPreviewSegment>();
        DrawingPreviewShape = shape;
        DrawingPreviewShapeVertexCount = shapeVertexCount;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void SetCurveDrawingPreview(PointF start, PointF control, PointF end, Color color, float stroke)
    {
        SetCubicCurveDrawingPreview(
            start,
            Lerp(start, control, 2f / 3f),
            Lerp(end, control, 2f / 3f),
            end,
            color,
            stroke);
    }

    public void SetCubicCurveDrawingPreview(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        Color color,
        float stroke)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewControl = control1;
        DrawingPreviewControl2 = control2;
        DrawingPreviewEnd = end;
        DrawingPreviewHasCurve = true;
        DrawingPreviewCurveSegments = [new CubicDrawingPreviewSegment(start, control1, control2, end)];
        DrawingPreviewShape = ShapeKind.Line;
        DrawingPreviewShapeVertexCount = 0;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void SetCurveDrawingPreview(
        IReadOnlyList<CubicDrawingPreviewSegment> segments,
        Color color,
        float stroke)
    {
        if (segments.Count == 0)
        {
            ClearDrawingPreview();
            return;
        }

        DrawingPreviewVisible = true;
        DrawingPreviewStart = segments[0].Start;
        DrawingPreviewControl = segments[0].Control1;
        DrawingPreviewControl2 = segments[0].Control2;
        DrawingPreviewEnd = segments[^1].End;
        DrawingPreviewHasCurve = true;
        DrawingPreviewCurveSegments = segments.ToArray();
        DrawingPreviewShape = ShapeKind.Line;
        DrawingPreviewShapeVertexCount = 0;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void ClearDrawingPreview()
    {
        if (!DrawingPreviewVisible) return;
        DrawingPreviewVisible = false;
        DrawingPreviewHasCurve = false;
        DrawingPreviewCurveSegments = Array.Empty<CubicDrawingPreviewSegment>();
        Invalidate();
    }

    public void SetPenAnchorGuides(
        PointF point,
        bool vertical,
        bool horizontal,
        bool snapped,
        bool insertion)
    {
        point = VectorUnits.Quantize(point);
        if (PenAnchorGuidesVisible
            && PenAnchorGuidePoint == point
            && PenAnchorGuideVertical == vertical
            && PenAnchorGuideHorizontal == horizontal
            && PenAnchorGuideSnapped == snapped
            && PenAnchorGuideInsertion == insertion)
        {
            return;
        }

        PenAnchorGuidesVisible = true;
        PenAnchorGuidePoint = point;
        PenAnchorGuideVertical = vertical;
        PenAnchorGuideHorizontal = horizontal;
        PenAnchorGuideSnapped = snapped;
        PenAnchorGuideInsertion = insertion;
        Invalidate();
    }

    public void ClearPenAnchorGuides()
    {
        if (!PenAnchorGuidesVisible) return;
        PenAnchorGuidesVisible = false;
        PenAnchorGuideVertical = false;
        PenAnchorGuideHorizontal = false;
        PenAnchorGuideSnapped = false;
        PenAnchorGuideInsertion = false;
        Invalidate();
    }

    public void SetPenDirectionHandles(PointF anchor, PointF? incoming, PointF? outgoing)
    {
        anchor = VectorUnits.Quantize(anchor);
        incoming = incoming is { } inPoint ? VectorUnits.Quantize(inPoint) : null;
        outgoing = outgoing is { } outPoint ? VectorUnits.Quantize(outPoint) : null;
        if (PenDirectionHandlesVisible
            && PenDirectionAnchor == anchor
            && PenDirectionIncoming == incoming
            && PenDirectionOutgoing == outgoing)
        {
            return;
        }

        PenDirectionHandlesVisible = incoming is not null || outgoing is not null;
        PenDirectionAnchor = anchor;
        PenDirectionIncoming = incoming;
        PenDirectionOutgoing = outgoing;
        Invalidate();
    }

    public void ClearPenDirectionHandles()
    {
        if (!PenDirectionHandlesVisible) return;
        PenDirectionHandlesVisible = false;
        PenDirectionIncoming = null;
        PenDirectionOutgoing = null;
        Invalidate();
    }

    public void SetFreehandPreview(
        IReadOnlyList<PointF> points,
        Color color,
        float stroke,
        IReadOnlyList<float>? diameters = null,
        BrushShape? brushShape = null)
    {
        FreehandPreviewVisible = points.Count > 0;
        FreehandPreviewPoints = points;
        FreehandPreviewDiameters = diameters is { Count: > 0 } ? diameters : Array.Empty<float>();
        FreehandPreviewColor = color;
        FreehandPreviewStroke = Math.Max(VectorUnits.MinimumStrokeUnits, stroke);
        FreehandPreviewBrushShape = brushShape;
        Invalidate();
    }

    public void ClearFreehandPreview()
    {
        if (!FreehandPreviewVisible && FreehandPreviewPoints.Count == 0) return;
        FreehandPreviewVisible = false;
        FreehandPreviewPoints = Array.Empty<PointF>();
        FreehandPreviewDiameters = Array.Empty<float>();
        FreehandPreviewBrushShape = null;
        Invalidate();
    }

    public void SetBrushTipCursor(Point screen, BrushShape shape, float diameter, bool eraser)
    {
        var radius = Math.Max(2, WorldLengthToScreen(diameter) * 0.5f);
        if (BrushTipCursorVisible
            && BrushTipCursorScreen == screen
            && ReferenceEquals(BrushTipCursorShape, shape)
            && Math.Abs(BrushTipCursorDiameterWorld - diameter) < 0.001f
            && Math.Abs(BrushTipCursorRadiusPixels - radius) < 0.01f
            && BrushTipCursorIsEraser == eraser)
        {
            return;
        }

        BrushTipCursorVisible = true;
        BrushTipCursorScreen = screen;
        BrushTipCursorShape = shape;
        BrushTipCursorDiameterWorld = diameter;
        BrushTipCursorRadiusPixels = radius;
        BrushTipCursorIsEraser = eraser;
        Invalidate();
    }

    public void ClearBrushTipCursor()
    {
        if (!BrushTipCursorVisible) return;
        BrushTipCursorVisible = false;
        BrushTipCursorShape = null;
        BrushTipCursorDiameterWorld = 0;
        Invalidate();
    }

    private void RefreshBrushTipCursorScale()
    {
        if (!BrushTipCursorVisible || BrushTipCursorShape is null) return;
        BrushTipCursorRadiusPixels = Math.Max(2, WorldLengthToScreen(BrushTipCursorDiameterWorld) * 0.5f);
    }

    public void SetBrushColorPalette(Point center, IReadOnlyList<Color> colors, int hoveredIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var seen = new HashSet<int>();
        var normalized = colors
            .Where(color => !color.IsEmpty && seen.Add(color.ToArgb()))
            .Take(MaxBrushColorPaletteColors)
            .ToArray();
        if (normalized.Length == 0)
        {
            ClearBrushColorPalette();
            return;
        }

        var padding = BrushColorPaletteRadiusPixels + BrushColorPaletteSwatchSize;
        var maximumX = Math.Max(padding, ClientSize.Width - padding);
        var maximumY = Math.Max(padding, ClientSize.Height - padding);
        var clampedCenter = new Point(
            Math.Clamp(center.X, padding, maximumX),
            Math.Clamp(center.Y, padding, maximumY));
        var nextHoveredIndex = Math.Clamp(hoveredIndex, -1, normalized.Length - 1);
        if (_brushColorPaletteCenter == clampedCenter
            && _brushColorPaletteHoveredIndex == nextHoveredIndex
            && _brushColorPaletteColors.SequenceEqual(normalized))
        {
            return;
        }

        _brushColorPaletteCenter = clampedCenter;
        _brushColorPaletteColors = normalized;
        _brushColorPaletteHoveredIndex = nextHoveredIndex;
        Invalidate();
    }

    public void ClearBrushColorPalette()
    {
        if (_brushColorPaletteColors.Length == 0) return;
        _brushColorPaletteColors = [];
        _brushColorPaletteHoveredIndex = -1;
        Invalidate();
    }

    public int HitTestBrushColorPalette(Point screen)
    {
        for (var index = 0; index < _brushColorPaletteColors.Length; index++)
        {
            if (BrushColorPaletteSwatchBounds(index).Contains(screen)) return index;
        }

        return -1;
    }

    internal Rectangle BrushColorPaletteSwatchBounds(int index)
    {
        if ((uint)index >= (uint)_brushColorPaletteColors.Length) return Rectangle.Empty;
        var count = _brushColorPaletteColors.Length;
        var angle = -MathF.PI * 0.5f + MathF.Tau * index / count;
        var x = _brushColorPaletteCenter.X + MathF.Cos(angle) * BrushColorPaletteRadiusPixels;
        var y = _brushColorPaletteCenter.Y + MathF.Sin(angle) * BrushColorPaletteRadiusPixels;
        return new Rectangle(
            (int)MathF.Round(x - BrushColorPaletteSwatchSize * 0.5f),
            (int)MathF.Round(y - BrushColorPaletteSwatchSize * 0.5f),
            BrushColorPaletteSwatchSize,
            BrushColorPaletteSwatchSize);
    }

    public void SetFillPreview(PointF[][] contours, Color color)
    {
        if (contours.Length == 0)
        {
            ClearFillPreview();
            return;
        }

        if (ReferenceEquals(_fillPreviewContours, contours) && _fillPreviewColor.ToArgb() == color.ToArgb()) return;
        _fillPreviewContours = contours;
        _fillPreviewColor = color;
        Invalidate();
    }

    public void ClearFillPreview()
    {
        if (_fillPreviewContours.Length == 0) return;
        _fillPreviewContours = Array.Empty<PointF[]>();
        Invalidate();
    }

    public void SetFillToolCursor(Point screen, Color color)
    {
        if (_fillToolCursorVisible
            && _fillToolCursorScreen == screen
            && _fillToolCursorColor.ToArgb() == color.ToArgb())
        {
            return;
        }

        _fillToolCursorVisible = true;
        _fillToolCursorScreen = screen;
        _fillToolCursorColor = color;
        Invalidate();
    }

    public void ClearFillToolCursor()
    {
        if (!_fillToolCursorVisible) return;
        _fillToolCursorVisible = false;
        Invalidate();
    }

    public void SetFillEdgeBezierOverlay(
        int targetObject,
        IReadOnlyList<FillEdgeBezierOverlaySegment> segments,
        int activePartIndex = -1)
    {
        if ((uint)targetObject >= Scene.ObjectCount || segments.Count == 0)
        {
            ClearFillEdgeBezierOverlay();
            return;
        }

        SetFillEdgeBezierOverlayCore(targetObject, segments.ToArray(), activePartIndex);
    }

    internal void SetOwnedFillEdgeBezierOverlay(
        int targetObject,
        FillEdgeBezierOverlaySegment[] segments,
        int activePartIndex = -1)
    {
        if ((uint)targetObject >= Scene.ObjectCount || segments.Length == 0)
        {
            ClearFillEdgeBezierOverlay();
            return;
        }

        SetFillEdgeBezierOverlayCore(targetObject, segments, activePartIndex);
    }

    private void SetFillEdgeBezierOverlayCore(
        int targetObject,
        FillEdgeBezierOverlaySegment[] segments,
        int activePartIndex)
    {
        var normalizedActivePartIndex = -1;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].PartIndex != activePartIndex) continue;
            normalizedActivePartIndex = activePartIndex;
            break;
        }

        if (FillEdgeBezierOverlayMatches(targetObject, segments, normalizedActivePartIndex)) return;

        var selectionSuppressionMayChange = !FillEdgeBezierOverlayVisible
            || _fillEdgeBezierOverlayTargetObject != targetObject;
        _fillEdgeBezierOverlayTargetObject = targetObject;
        _fillEdgeBezierOverlaySegments = segments;
        _fillEdgeBezierOverlayActivePartIndex = normalizedActivePartIndex;
        _fillEdgeBezierOverlayRevision++;
        _direct2DRenderer.InvalidateFillEdgeBezierOverlay();
        if (selectionSuppressionMayChange) UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    private bool FillEdgeBezierOverlayMatches(
        int targetObject,
        IReadOnlyList<FillEdgeBezierOverlaySegment> segments,
        int activePartIndex)
    {
        if (_fillEdgeBezierOverlayTargetObject != targetObject
            || _fillEdgeBezierOverlayActivePartIndex != activePartIndex
            || _fillEdgeBezierOverlaySegments.Length != segments.Count)
        {
            return false;
        }

        for (var index = 0; index < segments.Count; index++)
        {
            if (_fillEdgeBezierOverlaySegments[index] != segments[index]) return false;
        }

        return true;
    }

    public void SetFillEdgeBezierPointerEditing(bool editing)
    {
        if (FillEdgeBezierPointerEditing == editing) return;
        FillEdgeBezierPointerEditing = editing;
        Invalidate();
    }

    public void ClearFillEdgeBezierOverlay() => ResetFillEdgeBezierOverlay(invalidate: true);

    private void ResetFillEdgeBezierOverlay(bool invalidate)
    {
        var wasVisible = FillEdgeBezierOverlayVisible;
        _fillEdgeBezierOverlayTargetObject = -1;
        _fillEdgeBezierOverlayActivePartIndex = -1;
        _fillEdgeBezierOverlaySegments = [];
        if (!wasVisible) return;

        _fillEdgeBezierOverlayRevision++;
        _direct2DRenderer.InvalidateFillEdgeBezierOverlay();
        UpdateSelectionHighlightAnimation();
        if (invalidate) InvalidateSelectionState();
    }

    public FillEdgeBezierOverlayHit HitTestFillEdgeBezierOverlay(Point screen)
    {
        if (!FillEdgeBezierOverlayVisible) return FillEdgeBezierOverlayHit.None;

        var active = _fillEdgeBezierOverlaySegments.FirstOrDefault(segment =>
            segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex);
        var bestHandle = FillEdgeBezierOverlayHit.None;
        var bestHandleDistance = float.PositiveInfinity;
        var bestHandleIsAnchor = false;
        var bestHandleIsActive = false;
        if (active.PartIndex == _fillEdgeBezierOverlayActivePartIndex && _fillEdgeBezierOverlayActivePartIndex >= 0)
        {
            ConsiderFillEdgeBezierSegmentHandles(
                screen,
                active,
                true,
                ref bestHandle,
                ref bestHandleDistance,
                ref bestHandleIsAnchor,
                ref bestHandleIsActive);
        }

        foreach (var segment in _fillEdgeBezierOverlaySegments)
        {
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex) continue;
            ConsiderFillEdgeBezierSegmentHandles(
                screen,
                segment,
                false,
                ref bestHandle,
                ref bestHandleDistance,
                ref bestHandleIsAnchor,
                ref bestHandleIsActive);
        }

        if (bestHandle.IsValid) return bestHandle;

        if (_fillEdgeBezierOverlayActivePartIndex >= 0
            && CubicCurveHit(screen, active, FillEdgeBezierCurveHitRadiusPixels))
        {
            return new FillEdgeBezierOverlayHit(active.PartIndex, EditHandleKind.None);
        }

        foreach (var segment in _fillEdgeBezierOverlaySegments)
        {
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex) continue;
            if (CubicCurveHit(screen, segment, FillEdgeBezierCurveHitRadiusPixels))
            {
                return new FillEdgeBezierOverlayHit(segment.PartIndex, EditHandleKind.None);
            }
        }

        return FillEdgeBezierOverlayHit.None;
    }

    private void ConsiderFillEdgeBezierSegmentHandles(
        Point screen,
        FillEdgeBezierOverlaySegment segment,
        bool isActive,
        ref FillEdgeBezierOverlayHit best,
        ref float bestDistance,
        ref bool bestIsAnchor,
        ref bool bestIsActive)
    {
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Control1,
            EditHandleKind.BezierControl,
            FillEdgeBezierControlHitRadiusPixels,
            false,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Control2,
            EditHandleKind.BezierControl2,
            FillEdgeBezierControlHitRadiusPixels,
            false,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Start,
            EditHandleKind.LineStart,
            FillEdgeBezierAnchorHitRadiusPixels,
            true,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.End,
            EditHandleKind.LineEnd,
            FillEdgeBezierAnchorHitRadiusPixels,
            true,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
    }

    private void ConsiderFillEdgeBezierHandleCandidate(
        Point screen,
        FillEdgeBezierOverlaySegment segment,
        PointF world,
        EditHandleKind handle,
        float radius,
        bool isAnchor,
        bool isActive,
        ref FillEdgeBezierOverlayHit best,
        ref float bestDistance,
        ref bool bestIsAnchor,
        ref bool bestIsActive)
    {
        var distance = SquaredDistance(screen, WorldToScreen(world));
        if (distance > radius * radius) return;

        const float distanceTieTolerance = 0.001f;
        var closer = distance < bestDistance - distanceTieTolerance;
        var tied = Math.Abs(distance - bestDistance) <= distanceTieTolerance;
        var preferred = tied
            && (isAnchor && !bestIsAnchor
                || isAnchor == bestIsAnchor && isActive && !bestIsActive);
        if (!closer && !preferred) return;

        best = new FillEdgeBezierOverlayHit(segment.PartIndex, handle);
        bestDistance = distance;
        bestIsAnchor = isAnchor;
        bestIsActive = isActive;
    }

    public void SetGradientOverlay(
        PointF start,
        PointF end,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops)
    {
        var normalizedStops = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .GroupBy(stop => stop.Position)
            .Select(group => group.Last())
            .ToList();
        if (normalizedStops.Count == 0) normalizedStops.Add(new GradientStop(0, Color.White));
        if (normalizedStops.Count == 1) normalizedStops.Add(new GradientStop(1, normalizedStops[0].Argb));
        if (normalizedStops[0].Position > 0) normalizedStops.Insert(0, new GradientStop(0, normalizedStops[0].Argb));
        if (normalizedStops[^1].Position < 1) normalizedStops.Add(new GradientStop(1, normalizedStops[^1].Argb));
        var stopsForOverlay = normalizedStops.ToArray();
        var startColor = Color.FromArgb(stopsForOverlay[0].Argb);
        var endColor = Color.FromArgb(stopsForOverlay[^1].Argb);
        if (_gradientOverlayVisible
            && _gradientOverlayStart == start
            && _gradientOverlayEnd == end
            && _gradientOverlayKind == kind
            && _gradientOverlayStartColor.ToArgb() == startColor.ToArgb()
            && _gradientOverlayEndColor.ToArgb() == endColor.ToArgb()
            && _gradientOverlayStops.SequenceEqual(stopsForOverlay))
        {
            return;
        }

        _gradientOverlayVisible = true;
        _gradientOverlayStart = start;
        _gradientOverlayEnd = end;
        _gradientOverlayKind = kind;
        _gradientOverlayStartColor = startColor;
        _gradientOverlayEndColor = endColor;
        _gradientOverlayStops = stopsForOverlay;
        InvalidateSelectionState();
    }

    public void ClearGradientOverlay()
    {
        if (!_gradientOverlayVisible) return;
        _gradientOverlayVisible = false;
        InvalidateSelectionState();
    }

    public GradientOverlayHit HitTestGradientOverlay(Point screen)
    {
        if (!_gradientOverlayVisible) return GradientOverlayHit.None;
        var start = WorldToScreen(_gradientOverlayStart);
        var end = WorldToScreen(_gradientOverlayEnd);
        const float radiusSquared = 10 * 10;
        var startDistance = SquaredDistance(screen, start);
        var endDistance = SquaredDistance(screen, end);
        if (startDistance <= radiusSquared
            || _gradientOverlayKind != GradientKind.ShapeRadial && endDistance <= radiusSquared)
        {
            if (_gradientOverlayKind == GradientKind.ShapeRadial) return new GradientOverlayHit(GradientHandleKind.Start);
            return startDistance <= endDistance
                ? new GradientOverlayHit(GradientHandleKind.Start)
                : new GradientOverlayHit(GradientHandleKind.End);
        }

        for (var index = 1; index < _gradientOverlayStops.Length - 1; index++)
        {
            var point = Lerp(start, end, _gradientOverlayStops[index].Position);
            if (SquaredDistance(screen, point) <= radiusSquared) return new GradientOverlayHit(GradientHandleKind.Stop, index);
        }

        return GradientOverlayHit.None;
    }

    public GradientHandleKind HitTestGradientHandle(Point screen) => HitTestGradientOverlay(screen).Kind;

    public void StartFillAnimation(IReadOnlyList<PointF[]> contours, PointF origin, Color color)
    {
        var copiedContours = contours
            .Where(contour => contour.Length >= 3)
            .Select(contour => contour.ToArray())
            .ToArray();
        if (copiedContours.Length == 0) return;

        _fillAnimationContours = copiedContours;
        _fillAnimationOrigin = origin;
        _fillAnimationColor = color;
        _fillAnimationMaxRadiusWorld = Math.Max(
            1f,
            copiedContours
                .SelectMany(contour => contour)
                .Select(point => Distance(point, origin))
                .DefaultIfEmpty(1f)
                .Max());
        _fillAnimationProgress = 0f;
        _fillAnimationStartedAt = Stopwatch.GetTimestamp();
        if (!_fillAnimationTimer.Enabled) _fillAnimationTimer.Start();
        Invalidate();
    }

    private void TickFillAnimation()
    {
        if (_fillAnimationContours.Length == 0)
        {
            _fillAnimationTimer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_fillAnimationStartedAt).TotalMilliseconds;
        _fillAnimationProgress = (float)Math.Clamp(elapsed / FillAnimationDurationMilliseconds, 0d, 1d);
        if (_fillAnimationProgress >= 1f)
        {
            ClearFillAnimation();
            return;
        }

        Invalidate();
    }

    private void ClearFillAnimation()
    {
        _fillAnimationTimer.Stop();
        _fillAnimationContours = Array.Empty<PointF[]>();
        _fillAnimationMaxRadiusWorld = 0f;
        _fillAnimationProgress = 1f;
        Invalidate();
    }

    private void UpdateSelectionHighlightAnimation()
    {
        if (_disposingResources || Scene is null) return;
        var hasSelection = _selectedObjects.Any(index =>
            (uint)index < Scene.ObjectCount && !SuppressFillEdgeBezierSelectionOutline(index));
        if (hasSelection && Visible && IsHandleCreated)
        {
            if (_selectionHighlightTimer.Enabled) return;
            _selectionHighlightPhase = 0f;
            _selectionHighlightStartedAt = Stopwatch.GetTimestamp();
            _selectionHighlightTimer.Start();
            return;
        }

        _selectionHighlightTimer.Stop();
        _selectionHighlightPhase = 0f;
    }

    private void TickSelectionHighlight()
    {
        if (!Visible
            || !IsHandleCreated
            || !_selectedObjects.Any(index =>
                (uint)index < Scene.ObjectCount && !SuppressFillEdgeBezierSelectionOutline(index)))
        {
            UpdateSelectionHighlightAnimation();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_selectionHighlightStartedAt).TotalMilliseconds;
        _selectionHighlightPhase = (float)(elapsed % SelectionHighlightCycleMilliseconds / SelectionHighlightCycleMilliseconds);
        if (!Capture) Invalidate();
    }

    internal bool SuppressFillEdgeBezierSelectionOutline(int objectIndex)
    {
        if (!FillEdgeBezierOverlayVisible
            || objectIndex != _fillEdgeBezierOverlayTargetObject
            || _selectedObjects.Length != 1
            || _selectedObjects[0] != objectIndex
            || (uint)objectIndex >= Scene.ObjectCount)
        {
            return false;
        }

        if (_selectedElements.Length > 0)
        {
            return _selectedElements.All(hit =>
                hit.Key.ObjectIndex == objectIndex
                && hit.Key.Kind is DrawingElementKind.Fill or DrawingElementKind.BoundaryStroke);
        }

        var shape = Scene.ShapeKind[objectIndex];
        return SelectionHighlightForShape(shape) == SelectionHighlightKind.Fill;
    }

    public void SetSelection(IEnumerable<int> objectIndices, int primaryObject = -1)
    {
        var selected = objectIndices
            .Where(index => index >= 0 && index < Scene.ObjectCount)
            .Distinct()
            .ToArray();

        if (primaryObject < 0 && selected.Length > 0) primaryObject = selected[^1];
        if (primaryObject >= 0 && !selected.Contains(primaryObject)) primaryObject = selected.Length > 0 ? selected[^1] : -1;

        _selectedObjects = selected;
        _selectedObject = primaryObject;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    public void SetSelectedElement(DrawingElementHit hit)
    {
        SetSelectedElements(hit.IsValid ? new[] { hit } : Array.Empty<DrawingElementHit>(), hit);
    }

    public void SetSelectedElements(IEnumerable<DrawingElementHit> hits, DrawingElementHit primary = default)
    {
        var selectedOwners = _selectedObjects.ToHashSet();
        _selectedElements = hits
            .Where(hit => hit.IsValid
                && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                && selectedOwners.Contains(hit.Key.ObjectIndex))
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
        SelectedElement = primary.IsValid && _selectedElements.Any(hit => hit.Key == primary.Key)
            ? _selectedElements.First(hit => hit.Key == primary.Key)
            : _selectedElements.Length > 0 ? _selectedElements[^1] : DrawingElementHit.None;
        InvalidateSelectedFillCache();
        UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    public void SetPenPathHandlesVisible(bool visible)
    {
        if (PenPathHandlesVisible == visible) return;
        PenPathHandlesVisible = visible;
        InvalidateSelectionState();
    }

    public void SetHoveredLineElement(DrawingElementHit hit)
    {
        if (!IsValidEditableBezierHit(hit)) hit = DrawingElementHit.None;
        if (_hoveredLineElement == hit) return;
        _hoveredLineElement = hit;
        Invalidate();
    }

    public void ClearHoveredLineElement() => SetHoveredLineElement(DrawingElementHit.None);

    internal IReadOnlyList<int> GetLodDetailObjectIndices()
    {
        List<int>? detail = null;
        HashSet<int>? seen = null;

        void Add(int objectIndex)
        {
            if ((uint)objectIndex >= Scene.ObjectCount || detail?.Count >= MaxSelectionOutlines) return;
            seen ??= new HashSet<int>();
            if (!seen.Add(objectIndex)) return;
            (detail ??= new List<int>(4)).Add(objectIndex);
        }

        foreach (var objectIndex in SelectedObjects) Add(objectIndex);
        foreach (var hit in SelectedElements) Add(hit.Key.ObjectIndex);
        Add(SelectedObject);
        Add(HoveredLineElement.Key.ObjectIndex);
        return detail is { Count: > 0 } ? detail : Array.Empty<int>();
    }

    public EditHandleKind HitTestHoveredLineHandle(Point screen)
    {
        return IsValidEditableBezierHit(_hoveredLineElement)
            ? HitTestLineBezierHandle(screen, _hoveredLineElement)
            : EditHandleKind.None;
    }

    public EditHandleKind HitTestLineElementHandle(Point screen, DrawingElementHit hit)
    {
        return IsValidEditableBezierHit(hit)
            ? HitTestLineBezierHandle(screen, hit)
            : EditHandleKind.None;
    }

    internal bool IsValidEditableBezierHit(DrawingElementHit hit)
    {
        return TryGetEditableBezierWorldPoints(hit, out _, out _, out _, out _);
    }

    private EditHandleKind HitTestLineBezierHandle(Point screen, DrawingElementHit hit)
    {
        if (!TryGetEditableBezierWorldPoints(hit, out var start, out var control1, out var control2, out var end))
        {
            return EditHandleKind.None;
        }

        if (Distance(screen, WorldToScreen(start)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineStart;
        if (Distance(screen, WorldToScreen(end)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineEnd;
        var controlReach = Math.Max(
            Distance(WorldToScreen(start), WorldToScreen(control1)),
            Distance(WorldToScreen(end), WorldToScreen(control2)));
        var controlHitRadius = Math.Clamp(
            ControlHandleHitRadiusPixels + MathF.Sqrt(controlReach) * 0.35f,
            ControlHandleHitRadiusPixels,
            MaxControlHandleHitRadiusPixels);
        if (Distance(screen, WorldToScreen(control1)) <= controlHitRadius) return EditHandleKind.BezierControl;
        return Distance(screen, WorldToScreen(control2)) <= controlHitRadius
            ? EditHandleKind.BezierControl2
            : EditHandleKind.None;
    }

    internal bool TryGetEditableBezierWorldPoints(
        DrawingElementHit hit,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        start = PointF.Empty;
        control1 = PointF.Empty;
        control2 = PointF.Empty;
        end = PointF.Empty;
        if (!hit.IsValid
            || (uint)hit.Key.ObjectIndex >= Scene.ObjectCount
            || !Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
        {
            return false;
        }

        if (hit.Key.Kind == DrawingElementKind.Stroke
            && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line)
        {
            return TryGetLineBezierWorldPoints(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out start,
                out control1,
                out control2,
                out end);
        }

        if (hit.Key.Kind != DrawingElementKind.BoundaryStroke) return false;
        var points = GetSelectedBoundaryPartPoints(hit);
        if (points.Length != 2) return false;
        start = points[0];
        end = points[1];
        control1 = Lerp(start, end, 1f / 3f);
        control2 = Lerp(start, end, 2f / 3f);
        return true;
    }

    public PointF[][] GetSelectedFillPartContours() => GetSelectedFillPartContours(SelectedElement);

    public PointF[][] GetSelectedFillPartContours(DrawingElementHit hit)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Fill || (uint)hit.Key.ObjectIndex >= Scene.ObjectCount)
        {
            return Array.Empty<PointF[]>();
        }

        var objectIndex = hit.Key.ObjectIndex;
        if (!_selectedFillCache.TryGetValue(objectIndex, out var cached)
            || cached.Frame != Frame
            || cached.Revision != Scene.GeometryRevision)
        {
            cached = new SelectedFillOwnerCacheEntry(
                Frame,
                Scene.GeometryRevision,
                Scene.X[objectIndex],
                Scene.Y[objectIndex],
                Scene.GetFillParts(objectIndex, Frame));
            _selectedFillCache[objectIndex] = cached;
        }

        var contours = Array.Empty<PointF[]>();
        foreach (var part in cached.Parts)
        {
            if (part.PartIndex != hit.Key.PartIndex) continue;
            contours = part.Contours;
            break;
        }

        var dx = Scene.X[objectIndex] - cached.X;
        var dy = Scene.Y[objectIndex] - cached.Y;
        if (Math.Abs(dx) <= 0.001f && Math.Abs(dy) <= 0.001f) return contours;

        var translated = new PointF[contours.Length][];
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var source = contours[contourIndex];
            var contour = new PointF[source.Length];
            for (var i = 0; i < source.Length; i++) contour[i] = new PointF(source[i].X + dx, source[i].Y + dy);
            translated[contourIndex] = contour;
        }

        return translated;
    }

    public PointF[] GetSelectedStrokePartPoints(DrawingElementHit hit)
    {
        return GetSelectedPolylinePartPoints(hit, DrawingElementKind.Stroke);
    }

    public PointF[] GetSelectedBoundaryPartPoints(DrawingElementHit hit)
    {
        return GetSelectedPolylinePartPoints(hit, DrawingElementKind.BoundaryStroke);
    }

    private PointF[] GetSelectedPolylinePartPoints(DrawingElementHit hit, DrawingElementKind kind)
    {
        if (!hit.IsValid || hit.Key.Kind != kind || (uint)hit.Key.ObjectIndex >= Scene.ObjectCount)
        {
            return Array.Empty<PointF>();
        }

        var objectIndex = hit.Key.ObjectIndex;
        var cacheKey = (objectIndex, kind);
        if (!_selectedPolylineCache.TryGetValue(cacheKey, out var cached)
            || cached.Frame != Frame
            || cached.Revision != Scene.GeometryRevision)
        {
            var parts = kind == DrawingElementKind.Stroke
                ? Scene.GetStrokeParts(objectIndex, Frame)
                : Scene.GetBoundaryParts(objectIndex, Frame);
            cached = new SelectedPolylineOwnerCacheEntry(
                Frame,
                Scene.GeometryRevision,
                Scene.X[objectIndex],
                Scene.Y[objectIndex],
                parts);
            _selectedPolylineCache[cacheKey] = cached;
        }

        var points = Array.Empty<PointF>();
        foreach (var part in cached.Parts)
        {
            if (part.PartIndex != hit.Key.PartIndex) continue;
            points = part.Points;
            break;
        }

        var dx = Scene.X[objectIndex] - cached.X;
        var dy = Scene.Y[objectIndex] - cached.Y;
        if (Math.Abs(dx) <= 0.001f && Math.Abs(dy) <= 0.001f) return points;

        var translated = new PointF[points.Length];
        for (var i = 0; i < points.Length; i++) translated[i] = new PointF(points[i].X + dx, points[i].Y + dy);
        return translated;
    }

    private void InvalidateSelectedFillCache()
    {
        _selectedFillCache.Clear();
        _selectedPolylineCache.Clear();
    }

    public void SetMarquee(Point start, Point end)
    {
        if (MarqueeVisible && MarqueeStart == start && MarqueeEnd == end) return;
        MarqueeVisible = true;
        MarqueeStart = start;
        MarqueeEnd = end;
        MarqueeOverlayActive = _marqueeOverlay.TryShow(start, end);
        if (MarqueeOverlayActive)
        {
            MarqueeLodPreviewActive = false;
            return;
        }

        MarqueeLodPreviewActive = ShouldUseMarqueeLodPreview(
            _lastFrameRenderMilliseconds,
            Scene.ObjectCount,
            UnderlayScene?.ObjectCount ?? 0,
            OnionSkinScene?.ObjectCount ?? 0,
            DragPreviewScene?.ObjectCount ?? 0);
        Invalidate();
    }

    public void ClearMarquee()
    {
        if (!MarqueeVisible) return;
        var usedOverlay = MarqueeOverlayActive;
        var pendingSceneInvalidation = _marqueeSceneInvalidationPending;
        _marqueeOverlay.Hide();
        MarqueeVisible = false;
        MarqueeOverlayActive = false;
        MarqueeLodPreviewActive = false;
        _marqueeSceneInvalidationPending = false;
        if (!usedOverlay || pendingSceneInvalidation) Invalidate();
    }

    private void UpdateMarqueeOverlay()
    {
        MarqueeOverlayActive = _marqueeOverlay.TryShow(MarqueeStart, MarqueeEnd);
    }

    private void InvalidateSelectionState()
    {
        if (MarqueeVisible && MarqueeOverlayActive)
        {
            _marqueeSceneInvalidationPending = true;
            return;
        }
        Invalidate();
    }

    internal static bool ShouldUseMarqueeLodPreview(
        double lastFrameMilliseconds,
        int editableObjectCount,
        int underlayObjectCount,
        int onionSkinObjectCount,
        int dragPreviewObjectCount)
    {
        var totalObjects = (long)Math.Max(0, editableObjectCount)
            + Math.Max(0, underlayObjectCount)
            + Math.Max(0, onionSkinObjectCount)
            + Math.Max(0, dragPreviewObjectCount);
        return lastFrameMilliseconds > MarqueePreviewFrameBudgetMilliseconds
            || totalObjects >= MarqueePreviewObjectThreshold;
    }

    private RenderStats DrawOverviewTiles(Graphics g)
    {
        var scene = Scene;
        var tileW = scene.StageWidth / scene.OverviewColumnCount;
        var tileH = scene.StageHeight / scene.OverviewRowCount;
        var viewport = VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.OverviewColumnCount + tx;
                var count = scene.OverviewCount[tile];
                if (count <= 0) continue;
                atoms += scene.OverviewAtoms[tile];
                var wx = tx * tileW - scene.StageWidth * 0.5f;
                var wy = ty * tileH - scene.StageHeight * 0.5f;
                var screen = WorldToScreen(wx, wy);
                g.FillRectangle(BrushFor(scene.OverviewArgb[tile]), screen.X, screen.Y, Math.Max(1, WorldLengthToScreen(tileW) + 1), Math.Max(1, WorldLengthToScreen(tileH) + 1));
                draws++;
            }
        }

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawTiles(Graphics g)
    {
        var scene = Scene;
        var tileW = scene.StageWidth / scene.TileColumnCount;
        var tileH = scene.StageHeight / scene.TileRowCount;
        var viewport = VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.TileColumnCount + tx;
                var count = scene.TileCount[tile];
                if (count <= 0) continue;
                atoms += scene.TileAtoms[tile];
                var wx = tx * tileW - scene.StageWidth * 0.5f;
                var wy = ty * tileH - scene.StageHeight * 0.5f;
                var screen = WorldToScreen(wx, wy);
                g.FillRectangle(BrushFor(scene.TileArgb[tile]), screen.X, screen.Y, Math.Max(1, WorldLengthToScreen(tileW) + 1), Math.Max(1, WorldLengthToScreen(tileH) + 1));
                draws++;
            }
        }

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawObjects(Graphics g, int drawLimit)
    {
        var scene = Scene;
        var bounds = VisibleWorldBounds();
        _renderOrder.Collect(scene, bounds, Frame);

        return DrawCollectedObjects(g, drawLimit);
    }

    private RenderStats DrawCollectedObjects(Graphics g, int drawLimit)
    {
        var scene = Scene;
        if (scene.HasNonNormalLayerBlendModes)
        {
            return DrawCollectedObjectsComposited(g, scene, drawLimit);
        }

        var drawn = _renderOrder.DrawLayers(
            scene,
            drawLimit,
            (layer, objects, start) => DrawLayerObjects(g, scene, layer, objects, start));
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private RenderStats DrawCollectedObjectsComposited(Graphics graphics, VectorScene scene, int drawLimit)
    {
        var starts = new int[scene.LayerCount];
        var skip = Math.Max(0, _renderOrder.VisibleCount - drawLimit);
        var drawn = 0;
        for (var layer = scene.LayerCount - 1; layer >= 0; layer--)
        {
            var objects = _renderOrder.GetLayerObjects(layer);
            var start = Math.Min(skip, objects.Count);
            starts[layer] = start;
            skip -= start;
            drawn += objects.Count - start;
        }

        var compositor = LayerCompositor();
        compositor.CompositeTo(graphics, scene, (layerGraphics, layer) =>
        {
            DrawLayerObjects(
                layerGraphics,
                scene,
                layer,
                _renderOrder.GetLayerObjects(layer),
                starts[layer]);
        });
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private LayerBlendCompositor LayerCompositor()
    {
        var size = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (_layerBlendCompositor is not null && _layerBlendCompositor.Size == size) return _layerBlendCompositor;
        _layerBlendCompositor?.Dispose();
        _layerBlendCompositor = new LayerBlendCompositor(size);
        return _layerBlendCompositor;
    }

    private void DrawLodDetailObjects(Graphics graphics)
    {
        foreach (var objectIndex in GetLodDetailObjectIndices())
        {
            if (!Scene.IsObjectActive(objectIndex, Frame)
                || !Scene.ShouldRenderLayerContent(Scene.ObjectLayer[objectIndex]))
            {
                continue;
            }

            if (Scene.IsLayerEffectivelyOutlined(Scene.ObjectLayer[objectIndex])) DrawObjectOutline(graphics, objectIndex);
            else
            {
                if (SceneRenderOrder.HasFill(Scene.ShapeKind[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Fill);
                if (SceneRenderOrder.HasStroke(Scene.ShapeKind[objectIndex], Scene.Stroke[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Stroke);
            }
        }
    }

    internal bool TryGetActiveMaskLayer(out int layer)
    {
        layer = Scene.ActiveLayer;
        return (uint)layer < Scene.LayerCount
            && Scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            && Scene.IsLayerEffectivelyVisible(layer)
            && !Scene.IsLayerEffectivelyLocked(layer);
    }

    private void DrawActiveMaskOutline(Graphics graphics)
    {
        if (!TryGetActiveMaskLayer(out var maskLayer)) return;
        var maskObjects = Enumerable.Range(0, Scene.ObjectCount)
            .Where(index => Scene.ObjectLayer[index] == maskLayer
                && Scene.IsObjectActive(index, Frame)
                && SceneRenderOrder.HasFill(Scene.ShapeKind[index]))
            .ToArray();
        if (maskObjects.Length == 0) return;

        using var path = CreateMaskPath(Scene, maskObjects);
        if (path.PointCount == 0) return;
        using var fill = new SolidBrush(Color.FromArgb(48, 112, 205, 209));
        using var glow = new Pen(Color.FromArgb(110, 104, 231, 232), 4f);
        using var outline = new Pen(Color.FromArgb(244, 159, 242, 242), 1.4f) { DashStyle = DashStyle.Dash };
        graphics.FillPath(fill, path);
        graphics.DrawPath(glow, path);
        graphics.DrawPath(outline, path);
    }

    private void DrawLayerObjects(Graphics graphics, VectorScene scene, int layer, IReadOnlyList<int> objects, int start)
    {
        var layerKind = scene.GetLayerKind(layer);
        if (!scene.ShouldRenderLayerContent(layer)) return;
        if (layerKind == DrawingLayerKind.Mask)
        {
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
            return;
        }
        if (!scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
            return;
        }

        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var maskPath = CreateMaskPath(scene, _renderOrder.GetLayerObjects(maskLayer));
        if (maskPath.PointCount == 0) return;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(maskPath, CombineMode.Intersect);
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawLayerObjectsUnmasked(
        Graphics graphics,
        VectorScene scene,
        int layer,
        IReadOnlyList<int> objects,
        int start)
    {
        var outlineLayerColor = scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineLayerColor.IsEmpty)
        {
            for (var index = start; index < objects.Count; index++)
            {
                DrawObjectOutline(graphics, objects[index], outlineLayerColor);
            }
            return;
        }

        for (var index = start; index < objects.Count; index++)
        {
            var objectIndex = objects[index];
            if (SceneRenderOrder.HasFill(Scene.ShapeKind[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Fill);
        }

        for (var index = start; index < objects.Count; index++)
        {
            var objectIndex = objects[index];
            if (SceneRenderOrder.HasStroke(Scene.ShapeKind[objectIndex], Scene.Stroke[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Stroke);
        }
    }

    private void DrawObjectOutline(Graphics graphics, int objectIndex)
    {
        var scene = Scene;
        var layer = (uint)objectIndex < scene.ObjectLayer.Length ? scene.ObjectLayer[objectIndex] : -1;
        DrawObjectOutline(graphics, objectIndex, scene.GetEffectiveLayerOutlineColor(layer));
    }

    private void DrawObjectOutline(Graphics graphics, int objectIndex, Color layerColor)
    {
        var scene = Scene;
        if (IsObjectHiddenForRendering(scene, objectIndex)) return;
        var color = OutlineColor(scene, objectIndex, layerColor);
        if (color.A == 0) return;

        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        using var pen = new Pen(color, 1f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (shape == ShapeKind.Line)
            {
                using var lineBrush = new SolidBrush(color);
                DrawBezierLine(graphics, objectIndex, lineBrush, 1f);
                return;
            }

            if (shape == ShapeKind.Freeform && scene.TryGetFreehandWorldPoints(objectIndex, out var centerline))
            {
                DrawOutlinePolyline(graphics, pen, centerline.Select(WorldToScreen).ToArray());
                return;
            }

            if (shape == ShapeKind.BrushStroke && scene.TryGetFreehandWorldPoints(objectIndex, out var brushCenterline))
            {
                using var brushPath = new GraphicsPath(FillMode.Alternate);
                AppendPolygonContours(
                    brushPath,
                    FreehandStrokeProcessor.CreateBrushOutlines(brushCenterline, scene.Stroke[objectIndex]));
                if (brushPath.PointCount > 0) graphics.DrawPath(pen, brushPath);
                return;
            }

            if (shape == ShapeKind.Text && scene.TryGetTextWorldContours(objectIndex, out var textContours))
            {
                using var textPath = new GraphicsPath(FillMode.Alternate);
                AppendPolygonContours(textPath, textContours);
                if (textPath.PointCount > 0) graphics.DrawPath(pen, textPath);
                return;
            }

            if (shape == ShapeKind.ImportedSvg)
            {
                var screen = WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
                var width = Math.Max(0.75f, WorldLengthToScreen(scene.Width[objectIndex]));
                var height = Math.Max(0.75f, WorldLengthToScreen(scene.Height[objectIndex]));
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(screen.X, screen.Y);
                    graphics.RotateTransform(scene.Angle[objectIndex] * 57.29578f);
                    graphics.DrawRectangle(pen, -width * 0.5f, -height * 0.5f, width, height);
                }
                finally
                {
                    graphics.Restore(state);
                }
                return;
            }

            using var path = CreateObjectBoundaryPath(scene, objectIndex);
            if (path.PointCount > 0) graphics.DrawPath(pen, path);
        }
        finally
        {
            graphics.SmoothingMode = oldMode;
        }
    }

    private static void DrawOutlinePolyline(Graphics graphics, Pen pen, IReadOnlyList<PointF> points)
    {
        if (points.Count == 1)
        {
            graphics.DrawEllipse(pen, points[0].X - 0.5f, points[0].Y - 0.5f, 1f, 1f);
        }
        else if (points.Count > 1)
        {
            graphics.DrawLines(pen, points.ToArray());
        }
    }

    private static Color OutlineColor(VectorScene scene, int objectIndex, Color layerColor)
    {
        if (layerColor.IsEmpty) return Color.Empty;
        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        var fillAlpha = SceneRenderOrder.HasFill(shape) && (uint)objectIndex < scene.Argb.Length
            ? Color.FromArgb(scene.Argb[objectIndex]).A
            : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            && (uint)objectIndex < scene.StrokeArgb.Length
                ? Color.FromArgb(scene.StrokeArgb[objectIndex]).A
                : 0;
        var materialAlpha = Math.Max(fillAlpha, strokeAlpha);
        var alpha = (layerColor.A * materialAlpha + 127) / 255;
        return Color.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private GraphicsPath CreateMaskPath(VectorScene scene, IReadOnlyList<int> maskObjects)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var objectIndex in maskObjects)
        {
            if (!SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) continue;
            AppendObjectBoundaryPath(path, scene, objectIndex);
        }

        return path;
    }

    private void DrawObject(Graphics g, int i, SceneRenderPass pass)
    {
        var scene = Scene;
        if (IsObjectHiddenForRendering(scene, i)) return;
        var screen = WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, WorldLengthToScreen(scene.Height[i]));
        var rect = new RectangleF(screen.X - w * 0.5f, screen.Y - h * 0.5f, w, h);
        var brush = BrushFor(scene.Argb[i]);
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        var shapeVertexCount = scene.GetShapeVertexCount(i);
        var strokeColor = StrokeColorFor(i);
        var screenStroke = Math.Max(0.1f, WorldLengthToScreen(scene.Stroke[i]));

        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill
                && scene.TryGetImportedSvgSource(i, out var source)
                && !string.IsNullOrWhiteSpace(source))
            {
                DrawImportedSvg(g, source, screen, w, h, scene.Angle[i], Color.FromArgb(scene.Argb[i]).A / 255f);
            }
            return;
        }

        if (shape == ShapeKind.Text)
        {
            if (pass == SceneRenderPass.Fill && scene.TryGetTextWorldContours(i, out var textContours))
            {
                DrawPathObject(g, i, textContours, brush, Color.Transparent, 0, 0, SceneRenderPass.Fill);
            }
            return;
        }

        if (pass == SceneRenderPass.Fill && shape != ShapeKind.Line && scene.HasGradient(i))
        {
            if (scene.GetGradientKind(i) == GradientKind.Linear && scene.HasGradientPath(i))
            {
                DrawPathGradientFill(g, scene, i);
                return;
            }

            DrawGradientFill(g, scene, i);
            return;
        }

        if (shape == ShapeKind.Path)
        {
            using var path = CreateObjectBoundaryPath(scene, i);
            DrawPathObject(g, path, brush, strokeColor, scene.Stroke[i], screenStroke, pass);
            return;
        }

        if (IsFreehandShape(shape) && scene.TryGetFreehandLocalPoints(i, out var freehandPoints))
        {
            var color = shape == ShapeKind.BrushStroke ? Color.FromArgb(scene.Argb[i]) : strokeColor;
            DrawFreehandStroke(g, i, freehandPoints, color, scene.Stroke[i]);
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (scene.Stroke[i] <= 0) return;
            if (pass == SceneRenderPass.Stroke && scene.HasGradient(i))
            {
                DrawGradientBezierLine(g, scene, i, screenStroke);
            }
            else if (pass == SceneRenderPass.Stroke)
            {
                DrawBezierLine(g, i, BrushFor(strokeColor.ToArgb()), screenStroke);
            }
            return;
        }

        if (Math.Abs(scene.Angle[i]) > 0.01f || shape is not ShapeKind.Rectangle)
        {
            var state = g.Save();
            g.TranslateTransform(screen.X, screen.Y);
            g.RotateTransform(scene.Angle[i] * 57.29578f);
            DrawLocalShape(g, shape, shapeVertexCount, brush, strokeColor, scene.Stroke[i], screenStroke, w, h, pass);
            g.Restore(state);
            return;
        }

        if (pass == SceneRenderPass.Fill)
        {
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
            g.FillRectangle(brush, rect);
        }
        else if (scene.Stroke[i] > 0 && w > 4 && h > 4)
        {
            using var pen = StrokePen(strokeColor, screenStroke);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private static void DrawImportedSvg(
        Graphics graphics,
        string source,
        PointF screenCenter,
        float screenWidth,
        float screenHeight,
        float angleRadians,
        float opacity)
    {
        var raster = ImportedSvgRasterizer.Rasterize(source, screenWidth, screenHeight);
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TranslateTransform(screenCenter.X, screenCenter.Y);
            graphics.RotateTransform(angleRadians * 57.29578f);
            var destination = new RectangleF(
                -screenWidth * 0.5f,
                -screenHeight * 0.5f,
                screenWidth,
                screenHeight);
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(raster.Bitmap, destination);
                return;
            }

            using var attributes = new ImageAttributes();
            var colorMatrix = new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) };
            attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            var destinationPoints = new[]
            {
                new PointF(destination.Left, destination.Top),
                new PointF(destination.Right, destination.Top),
                new PointF(destination.Left, destination.Bottom)
            };
            graphics.DrawImage(
                raster.Bitmap,
                destinationPoints,
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawPathObject(Graphics g, int i, PointF[][] worldContours, Brush brush, Color strokeColor, float stroke, float screenStroke, SceneRenderPass pass)
    {
        if (worldContours.Length == 0) return;
        using var path = new GraphicsPath(FillMode.Alternate);
        AppendPolygonContours(path, worldContours);
        DrawPathObject(g, path, brush, strokeColor, stroke, screenStroke, pass);
    }

    private static void DrawPathObject(
        Graphics graphics,
        GraphicsPath path,
        Brush brush,
        Color strokeColor,
        float stroke,
        float screenStroke,
        SceneRenderPass pass)
    {
        if (path.PointCount == 0) return;
        if (pass == SceneRenderPass.Fill)
        {
            FillPathAntialiased(graphics, path, brush);
            return;
        }

        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        graphics.DrawPath(pen, path);
    }

    private GraphicsPath CreateObjectBoundaryPath(VectorScene scene, int objectIndex)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        AppendObjectBoundaryPath(path, scene, objectIndex);
        return path;
    }

    private void AppendObjectBoundaryPath(GraphicsPath path, VectorScene scene, int objectIndex)
    {
        if (scene.TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
        {
            AppendBezierContours(path, bezierContours);
            return;
        }

        AppendPolygonContours(path, scene.GetObjectBoundaryContours(objectIndex));
    }

    private void AppendBezierContours(GraphicsPath path, IReadOnlyList<PathBezierNode[]> contours)
    {
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            path.StartFigure();
            for (var nodeIndex = 0; nodeIndex < contour.Length; nodeIndex++)
            {
                var current = contour[nodeIndex];
                var next = contour[(nodeIndex + 1) % contour.Length];
                path.AddBezier(
                    WorldToScreen(current.Anchor),
                    WorldToScreen(current.OutgoingControl),
                    WorldToScreen(next.IncomingControl),
                    WorldToScreen(next.Anchor));
            }
            path.CloseFigure();
        }
    }

    private void AppendPolygonContours(GraphicsPath path, IReadOnlyList<PointF[]> contours)
    {
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            path.AddPolygon(contour.Select(WorldToScreen).ToArray());
        }
    }

    private Brush CreateGradientFillBrush(VectorScene scene, int objectIndex)
    {
        var start = WorldToScreen(scene.GetGradientStart(objectIndex));
        var end = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var stops = scene.GetGradientStops(objectIndex);
        var endColor = Color.FromArgb(stops[^1].Argb);
        if (Distance(start, end) < 0.5f) return new SolidBrush(endColor);
        var brush = new LinearGradientBrush(
            start,
            end,
            Color.FromArgb(stops[0].Argb),
            endColor);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
            Positions = stops.Select(stop => stop.Position).ToArray()
        };
        return brush;
    }

    private void DrawGradientFill(Graphics g, VectorScene scene, int objectIndex)
    {
        using var path = CreateObjectBoundaryPath(scene, objectIndex);

        if (path.PointCount == 0) return;
        var gradientStops = scene.GetGradientStops(objectIndex);
        if (scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, gradientStops);
            return;
        }

        if (scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial)
        {
            DrawShapeRadialGradientFill(g, path, scene, objectIndex, gradientStops);
            return;
        }

        using var brush = CreateGradientFillBrush(scene, objectIndex);
        FillPathAntialiased(g, path, brush);
    }

    internal static void FillPathAntialiased(Graphics g, GraphicsPath path, Brush brush)
    {
        var smoothingMode = g.SmoothingMode;
        var pixelOffsetMode = g.PixelOffsetMode;
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawPath(edge, path);
            g.FillPath(brush, path);
        }
        finally
        {
            g.SmoothingMode = smoothingMode;
            g.PixelOffsetMode = pixelOffsetMode;
        }
    }

    private void DrawPathGradientFill(Graphics g, VectorScene scene, int objectIndex)
    {
        if (!scene.TryGetGradientPathWorldPoints(objectIndex, out var pathPoints)
            || pathPoints.Length < 2
            || scene.EstimateGradientPathStrokeWidth(objectIndex) is not > 0)
        {
            return;
        }

        var screenLength = 0f;
        for (var index = 1; index < pathPoints.Length; index++)
        {
            screenLength += Distance(WorldToScreen(pathPoints[index - 1]), WorldToScreen(pathPoints[index]));
        }

        var segments = GradientPaintUtilities.CreatePathGradientSegments(
            pathPoints,
            scene.GetGradientStops(objectIndex),
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        if (segments.Length == 0) return;

        using var mask = CreateObjectBoundaryPath(scene, objectIndex);

        if (mask.PointCount == 0) return;
        var width = Math.Max(1f, WorldLengthToScreen(scene.EstimateGradientPathStrokeWidth(objectIndex) * 1.12f));
        var smoothingMode = g.SmoothingMode;
        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(mask, CombineMode.Intersect);
            foreach (var segment in segments)
            {
                var start = WorldToScreen(segment.Start);
                var end = WorldToScreen(segment.End);
                if (Distance(start, end) < 0.1f) continue;
                using var brush = new LinearGradientBrush(
                    start,
                    end,
                    Color.FromArgb(segment.StartArgb),
                    Color.FromArgb(segment.EndArgb))
                {
                    WrapMode = WrapMode.Clamp
                };
                using var pen = new Pen(brush, width)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                g.DrawLine(pen, start, end);
            }
        }
        finally
        {
            g.Restore(state);
            g.SmoothingMode = smoothingMode;
        }

        using var edgeBrush = CreateGradientFillBrush(scene, objectIndex);
        ReinforcePathEdge(g, mask, edgeBrush);
    }

    private void DrawRadialGradientFill(
        Graphics g,
        GraphicsPath path,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var center = WorldToScreen(scene.GetGradientStart(objectIndex));
        var radiusPoint = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var radius = Math.Max(1f, Distance(center, radiusPoint));
        using var outer = new SolidBrush(Color.FromArgb(stops[^1].Argb));
        g.FillPath(outer, path);
        var state = g.Save();
        try
        {
            g.SetClip(path, CombineMode.Intersect);
            const int rings = 64;
            for (var ring = rings - 1; ring >= 0; ring--)
            {
                var position = ring / (float)(rings - 1);
                using var fill = new SolidBrush(GradientColorAt(stops, position));
                var ringRadius = Math.Max(0.5f, radius * position);
                g.FillEllipse(fill, center.X - ringRadius, center.Y - ringRadius, ringRadius * 2, ringRadius * 2);
            }
        }
        finally
        {
            g.Restore(state);
        }
        using var edge = new SolidBrush(Color.FromArgb(stops[^1].Argb));
        ReinforcePathEdge(g, path, edge);
    }

    private static void ReinforcePathEdge(Graphics graphics, GraphicsPath path, Brush brush)
    {
        var smoothingMode = graphics.SmoothingMode;
        var pixelOffsetMode = graphics.PixelOffsetMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawPath(edge, path);
        }
        finally
        {
            graphics.SmoothingMode = smoothingMode;
            graphics.PixelOffsetMode = pixelOffsetMode;
        }
    }

    private void DrawShapeRadialGradientFill(
        Graphics g,
        GraphicsPath path,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        try
        {
            using var mappingPath = new GraphicsPath(FillMode.Alternate);
            foreach (var contour in scene.GetShapeGradientMappingContours(objectIndex))
            {
                if (contour.Length >= 3) mappingPath.AddPolygon(contour.Select(WorldToScreen).ToArray());
            }
            if (mappingPath.PointCount == 0)
            {
                DrawRadialGradientFill(g, path, scene, objectIndex, stops);
                return;
            }

            using var brush = new PathGradientBrush(mappingPath)
            {
                CenterPoint = WorldToScreen(scene.GetGradientStart(objectIndex)),
                InterpolationColors = new ColorBlend
                {
                    Colors = stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
                    Positions = stops.Select(stop => stop.Position).ToArray()
                }
            };
            FillPathAntialiased(g, path, brush);
        }
        catch (ArgumentException)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, stops);
        }
        catch (OutOfMemoryException)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, stops);
        }
    }

    private static Color GradientColorAt(IReadOnlyList<GradientStop> stops, float position)
    {
        position = Math.Clamp(position, 0f, 1f);
        var previous = stops[0];
        foreach (var current in stops.Skip(1))
        {
            if (position <= current.Position)
            {
                var length = Math.Max(0.0001f, current.Position - previous.Position);
                var amount = Math.Clamp((position - previous.Position) / length, 0f, 1f);
                var from = Color.FromArgb(previous.Argb);
                var to = Color.FromArgb(current.Argb);
                return Color.FromArgb(
                    (int)MathF.Round(from.A + (to.A - from.A) * amount),
                    (int)MathF.Round(from.R + (to.R - from.R) * amount),
                    (int)MathF.Round(from.G + (to.G - from.G) * amount),
                    (int)MathF.Round(from.B + (to.B - from.B) * amount));
            }

            previous = current;
        }

        return Color.FromArgb(stops[^1].Argb);
    }

    private void DrawFreehandStroke(Graphics g, int i, PointF[] localPoints, Color color, float stroke)
    {
        if (localPoints.Length == 0) return;
        var screenWidth = Math.Max(0.75f, WorldLengthToScreen(stroke));
        if (localPoints.Length == 1)
        {
            var point = WorldToScreen(LocalToWorld(i, localPoints[0]));
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, point.X - screenWidth * 0.5f, point.Y - screenWidth * 0.5f, screenWidth, screenWidth);
            return;
        }

        using var pen = StrokePen(color, screenWidth);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var previous = WorldToScreen(LocalToWorld(i, localPoints[0]));
        for (var p = 1; p < localPoints.Length; p++)
        {
            var current = WorldToScreen(LocalToWorld(i, localPoints[p]));
            g.DrawLine(pen, previous, current);
            previous = current;
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawLocalShape(
        Graphics g,
        ShapeKind shape,
        int shapeVertexCount,
        Brush brush,
        Color strokeColor,
        float stroke,
        float screenStroke,
        float w,
        float h,
        SceneRenderPass pass)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                if (pass == SceneRenderPass.Fill)
                {
                    using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
                    g.DrawEllipse(edge, rect);
                    g.FillEllipse(brush, rect);
                }
                else if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawEllipse(pen, rect);
                }
                break;
            case ShapeKind.Line:
                if (pass == SceneRenderPass.Stroke)
                {
                    using var linePen = StrokePen(strokeColor, screenStroke);
                    g.DrawLine(linePen, -w * 0.5f, 0, w * 0.5f, 0);
                }
                break;
            case ShapeKind.Triangle:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(3, w, h, -MathF.PI / 2), pass);
                break;
            case ShapeKind.Polygon:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(PolygonVertexCount(shapeVertexCount), w, h, -MathF.PI / 2), pass);
                break;
            case ShapeKind.Star:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, StarPoints(StarVertexCount(shapeVertexCount), w, h, -MathF.PI / 2), pass);
                break;
            default:
                if (pass == SceneRenderPass.Fill)
                {
                    using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
                    g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
                    g.FillRectangle(brush, rect);
                }
                else if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                break;
        }
    }

    private static void DrawPolygonShape(Graphics g, Brush brush, Color strokeColor, float stroke, float screenStroke, PointF[] points, SceneRenderPass pass)
    {
        if (pass == SceneRenderPass.Fill)
        {
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round
            };
            g.DrawPolygon(edge, points);
            g.FillPolygon(brush, points);
            return;
        }

        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        g.DrawPolygon(pen, points);
    }

    private Color StrokeColorFor(int objectIndex)
    {
        return Scene.StrokeArgb.Length > objectIndex
            ? Color.FromArgb(Scene.StrokeArgb[objectIndex])
            : Color.FromArgb(238, 242, 241);
    }

    private static Pen StrokePen(Color color, float width)
    {
        return new Pen(color, Math.Max(1.5f, width))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
    }

    private static Pen SelectionPen(Color color, float width)
    {
        return new Pen(color, width)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
    }

    private static PointF[] RegularPolygonPoints(int sides, float w, float h, float startAngle)
    {
        var points = new PointF[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new PointF(MathF.Cos(angle) * w * 0.5f, MathF.Sin(angle) * h * 0.5f);
        }

        return points;
    }

    private static PointF[] StarPoints(int points, float w, float h, float startAngle)
    {
        var result = new PointF[points * 2];
        for (var i = 0; i < result.Length; i++)
        {
            var radius = i % 2 == 0 ? 0.5f : 0.23f;
            var angle = startAngle + i * MathF.Tau / result.Length;
            result[i] = new PointF(MathF.Cos(angle) * w * radius, MathF.Sin(angle) * h * radius);
        }

        return result;
    }

    private static int PolygonVertexCount(int value) => Math.Clamp(value == 0 ? 6 : value, 3, 64);

    private static int StarVertexCount(int value) => Math.Clamp(value == 0 ? 5 : value, 3, 32);

    private void DrawGrid(Graphics g)
    {
        if (_worldGridOpacity <= 0.001f) return;
        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            Draw3DReferenceGrid(g);
            return;
        }
        if (_worldGridType == VectorAnimationEngine.WorldGridType.GoldenSpiral)
        {
            DrawGoldenSpiralGrid(g);
            return;
        }
        if (_worldGridType == VectorAnimationEngine.WorldGridType.Polar)
        {
            DrawPolarGrid(g);
            return;
        }

        var scale = CurrentWorldGridScale;
        var bounds = VisibleWorldBounds();
        var origin = WorldToScreen(0, 0);
        DrawWorldGridLines(g, scale, bounds.Left, bounds.Right, vertical: true);
        DrawWorldGridLines(g, scale, bounds.Top, bounds.Bottom, vertical: false);

        if (origin.Y >= 0 && origin.Y <= Height)
        {
            _gridPen.Color = Color.FromArgb(GridAlpha(205), 214, 82, 82);
            _gridPen.Width = 1.6f;
            g.DrawLine(_gridPen, 0, origin.Y, Width, origin.Y);
        }
        if (origin.X >= 0 && origin.X <= Width)
        {
            _gridPen.Color = Color.FromArgb(GridAlpha(205), 82, 190, 122);
            _gridPen.Width = 1.6f;
            g.DrawLine(_gridPen, origin.X, 0, origin.X, Height);
        }
        if (origin.X >= 0 && origin.X <= Width && origin.Y >= 0 && origin.Y <= Height)
        {
            var previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(BrushFor(Color.FromArgb(GridAlpha(230), 224, 232, 234).ToArgb()), origin.X - 3, origin.Y - 3, 6, 6);
            _gridPen.Color = Color.FromArgb(GridAlpha(235), 22, 26, 29);
            _gridPen.Width = 1.2f;
            g.DrawEllipse(_gridPen, origin.X - 4.5f, origin.Y - 4.5f, 9, 9);
            g.SmoothingMode = previousSmoothing;
        }
    }

    private void DrawGoldenSpiralGrid(Graphics graphics)
    {
        var geometry = ResolveGoldenSpiralGrid();
        if (geometry.SpiralPoints.Length < 2) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guidePen = new Pen(Color.FromArgb(GridAlpha(82), 118, 128, 134), 1f);
        using var spiralPen = new Pen(Color.FromArgb(GridAlpha(205), 232, 194, 86), 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        foreach (var segment in geometry.GuideSegments)
        {
            graphics.DrawLine(
                guidePen,
                WorldToScreen(segment.Start.X, segment.Start.Y),
                WorldToScreen(segment.End.X, segment.End.Y));
        }
        var spiral = new PointF[geometry.SpiralPoints.Length];
        for (var index = 0; index < spiral.Length; index++)
        {
            var point = geometry.SpiralPoints[index];
            spiral[index] = WorldToScreen(point.X, point.Y);
        }
        graphics.DrawLines(spiralPen, spiral);
        graphics.SmoothingMode = previousSmoothing;
    }

    private void DrawPolarGrid(Graphics graphics)
    {
        var geometry = ResolvePolarGrid();
        if (geometry.Circles.Length == 0) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var origin = WorldToScreen(geometry.Origin.X, geometry.Origin.Y);
        var scale = CurrentWorldGridScale;
        var visibleBounds = VisibleWorldBounds();
        var arcOverscan = ScreenLengthToWorld(2f);
        visibleBounds.Inflate(arcOverscan, arcOverscan);
        var maximumFastEllipseRadius = Math.Max(Width, Height) * 2f;
        Span<PolarGridArc> visibleArcs = stackalloc PolarGridArc[PolarGridLayout.MaximumVisibleArcsPerCircle];
        foreach (var circle in geometry.Circles)
        {
            var style = WorldGridLayout.ResolveLineStyle(circle.Index, scale, _worldGridOpacity);
            if (style.Color.A == 0) continue;
            var radius = WorldLengthToScreen(circle.Radius);
            _gridPen.Color = style.Color;
            _gridPen.Width = style.Width;
            if (radius <= maximumFastEllipseRadius)
            {
                graphics.DrawEllipse(_gridPen, origin.X - radius, origin.Y - radius, radius * 2, radius * 2);
                continue;
            }

            var arcCount = PolarGridLayout.ResolveVisibleArcs(visibleBounds, circle.Radius, visibleArcs);
            if (arcCount == 0) continue;
            using var path = new GraphicsPath();
            for (var arcIndex = 0; arcIndex < arcCount; arcIndex++)
            {
                var arc = visibleArcs[arcIndex];
                var segmentCount = PolarGridLayout.ResolveArcSegmentCount(radius, arc.SweepAngle);
                var previous = PolarArcScreenPoint(origin, radius, arc.StartAngle);
                path.StartFigure();
                for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
                {
                    var angle = arc.StartAngle + arc.SweepAngle * segmentIndex / segmentCount;
                    var current = PolarArcScreenPoint(origin, radius, angle);
                    path.AddLine(previous, current);
                    previous = current;
                }
            }
            graphics.DrawPath(_gridPen, path);
        }

        for (var index = 0; index < geometry.DiameterSegments.Length; index++)
        {
            if (!PolarGridLayout.TryClipSegment(visibleBounds, geometry.DiameterSegments[index], out var segment)) continue;
            _gridPen.Color = index switch
            {
                0 => Color.FromArgb(GridAlpha(205), 214, 82, 82),
                PolarGridLayout.DiameterCount / 2 => Color.FromArgb(GridAlpha(205), 82, 190, 122),
                _ => Color.FromArgb(GridAlpha(index % 3 == 0 ? 112 : 72), 92, 104, 112)
            };
            _gridPen.Width = index is 0 or PolarGridLayout.DiameterCount / 2
                ? 1.6f
                : index % 3 == 0 ? 1.15f : 0.8f;
            graphics.DrawLine(
                _gridPen,
                WorldToScreen(segment.Start.X, segment.Start.Y),
                WorldToScreen(segment.End.X, segment.End.Y));
        }

        if (origin.X >= 0 && origin.X <= Width && origin.Y >= 0 && origin.Y <= Height)
        {
            graphics.FillEllipse(BrushFor(Color.FromArgb(GridAlpha(230), 224, 232, 234).ToArgb()), origin.X - 3, origin.Y - 3, 6, 6);
            _gridPen.Color = Color.FromArgb(GridAlpha(235), 22, 26, 29);
            _gridPen.Width = 1.2f;
            graphics.DrawEllipse(_gridPen, origin.X - 4.5f, origin.Y - 4.5f, 9, 9);
        }
        graphics.SmoothingMode = previousSmoothing;
    }

    private static PointF PolarArcScreenPoint(PointF origin, float radius, float angle)
    {
        return new PointF(
            (float)(origin.X + Math.Cos(angle) * radius),
            (float)(origin.Y + Math.Sin(angle) * radius));
    }

    private void DrawWorldGridLines(
        Graphics graphics,
        WorldGridScale scale,
        float minimumWorld,
        float maximumWorld,
        bool vertical)
    {
        var (first, last) = WorldGridLayout.VisibleIndexRange(minimumWorld, maximumWorld, scale.StepWorld);
        for (var index = first; index <= last; index++)
        {
            if (index == 0) continue;
            var style = WorldGridLayout.ResolveLineStyle(index, scale, _worldGridOpacity);
            if (style.Color.A == 0) continue;
            var world = index * scale.StepWorld;
            var position = vertical ? WorldToScreen((float)world, 0).X : WorldToScreen(0, (float)world).Y;
            if (vertical && (position < -style.Width || position > Width + style.Width)
                || !vertical && (position < -style.Width || position > Height + style.Width))
            {
                continue;
            }

            _gridPen.Color = style.Color;
            _gridPen.Width = style.Width;
            if (vertical) graphics.DrawLine(_gridPen, position, 0, position, Height);
            else graphics.DrawLine(_gridPen, 0, position, Width, position);
        }
    }

    private void Draw3DReferenceGrid(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var horizonPen = new Pen(Color.FromArgb(GridAlpha(40), 112, 204, 255), 1);
        using var gridPen = new Pen(Color.FromArgb(GridAlpha(70), 72, 84, 92), 1);
        using var centerPen = new Pen(Color.FromArgb(GridAlpha(110), 150, 164, 174), 1.3f);
        using var xPen = new Pen(Color.FromArgb(GridAlpha(210), 255, 92, 92), 2);
        using var yPen = new Pen(Color.FromArgb(GridAlpha(210), 122, 224, 92), 2);
        using var zPen = new Pen(Color.FromArgb(GridAlpha(210), 92, 172, 255), 2);

        var horizonY = Height * 0.42f;
        g.DrawLine(horizonPen, 0, horizonY, Width, horizonY);

        var step = InfiniteGridStep();
        var lineRadius = InfiniteGridLineRadius();
        var centerX = SnapToGrid(_referenceTargetX, step);
        var centerZ = SnapToGrid(_referenceTargetZ, step);
        var minX = centerX - lineRadius * step;
        var maxX = centerX + lineRadius * step;
        var minZ = centerZ - lineRadius * step;
        var maxZ = centerZ + lineRadius * step;

        for (var offset = -lineRadius; offset <= lineRadius; offset++)
        {
            var z = centerZ + offset * step;
            var x = centerX + offset * step;
            DrawProjectedLine(g, new Point3(minX, 0, z), new Point3(maxX, 0, z), Math.Abs(z) < 0.001f ? centerPen : gridPen);
            DrawProjectedLine(g, new Point3(x, 0, minZ), new Point3(x, 0, maxZ), Math.Abs(x) < 0.001f ? centerPen : gridPen);
        }

        DrawProjectedLine(g, new Point3(0, _referenceTargetY - 5000, 0), new Point3(0, _referenceTargetY + 5000, 0), yPen);
        DrawProjectedLine(g, new Point3(minX, 0, 0), new Point3(maxX, 0, 0), xPen);
        DrawProjectedLine(g, new Point3(0, 0, minZ), new Point3(0, 0, maxZ), zPen);
        DrawAxisLabel(g, "X", new Point3(maxX, 0, 0), xPen.Color);
        DrawAxisLabel(g, "Y", new Point3(0, 5000, 0), yPen.Color);
        DrawAxisLabel(g, "Z", new Point3(0, 0, maxZ), zPen.Color);
    }

    private void DrawProjectedLine(Graphics g, Point3 a, Point3 b, Pen pen)
    {
        var ca = CameraSpacePoint(a);
        var cb = CameraSpacePoint(b);
        if (!ClipNear(ref ca, ref cb)) return;
        var pa = ProjectCameraPoint(ca);
        var pb = ProjectCameraPoint(cb);
        if (!LineMayTouchViewport(pa, pb)) return;
        g.DrawLine(pen, pa, pb);
    }

    private void DrawAxisLabel(Graphics g, string text, Point3 point, Color color)
    {
        var projected = ProjectReferencePoint(point);
        if (projected is null) return;
        using var brush = new SolidBrush(color);
        g.DrawString(text, Font, brush, projected.Value.X + 6, projected.Value.Y - 16);
    }

    private PointF? ProjectReferencePoint(Point3 point)
    {
        var cameraPoint = CameraSpacePoint(point);
        if (cameraPoint.Z < 120) return null;
        return ProjectCameraPoint(cameraPoint);
    }

    private Point3 CameraSpacePoint(Point3 point)
    {
        var yawCos = MathF.Cos(_referenceYaw);
        var yawSin = MathF.Sin(_referenceYaw);
        var pitchCos = MathF.Cos(_referencePitch);
        var pitchSin = MathF.Sin(_referencePitch);

        var worldX = point.X - _referenceTargetX;
        var worldY = point.Y - _referenceTargetY;
        var worldZ = point.Z - _referenceTargetZ;
        var x = worldX * yawCos - worldZ * yawSin;
        var z = worldX * yawSin + worldZ * yawCos;
        var y = worldY;
        var py = y * pitchCos - z * pitchSin;
        var pz = y * pitchSin + z * pitchCos + _referenceDistance;
        return new Point3(x, py, pz);
    }

    private PointF ProjectCameraPoint(Point3 point)
    {
        var scale = 0.035f * _referenceZoomScale;
        var perspective = ReferenceProjection == CameraProjection.Perspective ? _referenceDistance / Math.Max(120f, point.Z) : 1f;
        return new PointF(
            Width * 0.5f + point.X * scale * perspective,
            Height * 0.58f - point.Y * scale * perspective);
    }

    private static bool ClipNear(ref Point3 a, ref Point3 b)
    {
        const float near = 120f;
        var aInside = a.Z >= near;
        var bInside = b.Z >= near;
        if (!aInside && !bInside) return false;
        if (aInside && bInside) return true;
        var t = Math.Clamp((near - a.Z) / (b.Z - a.Z), 0, 1);
        var clipped = new Point3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);
        if (aInside) b = clipped;
        else a = clipped;
        return true;
    }

    private bool LineMayTouchViewport(PointF a, PointF b)
    {
        const float margin = 320;
        var minX = MathF.Min(a.X, b.X);
        var maxX = MathF.Max(a.X, b.X);
        var minY = MathF.Min(a.Y, b.Y);
        var maxY = MathF.Max(a.Y, b.Y);
        return maxX >= -margin && minX <= Width + margin && maxY >= -margin && minY <= Height + margin;
    }

    private int InfiniteGridLineRadius()
    {
        return Math.Clamp((int)MathF.Ceiling(Math.Max(Width, Height) / 14f), 64, 180);
    }

    private float InfiniteGridStep()
    {
        var raw = _referenceDistance / Math.Max(0.35f, _referenceZoomScale) / 10f;
        if (raw <= 750) return 500;
        if (raw <= 1500) return 1000;
        if (raw <= 3500) return 2000;
        if (raw <= 7000) return 5000;
        return 10000;
    }

    private static float SnapToGrid(float value, float step) => MathF.Round(value / step) * step;

    private int GridAlpha(int alpha) => (int)MathF.Round(Math.Clamp(alpha * _worldGridOpacity, 0f, 255f));

    private readonly record struct Point3(float X, float Y, float Z);

    private void DrawSelection(Graphics g)
    {
        if ((SelectedObject < 0 || SelectedObject >= Scene.ObjectCount)
            && SelectedObjects.Count == 0
            && !IsValidEditableBezierHit(_hoveredLineElement)
            && !DrawingObjectSelectionVisible)
        {
            return;
        }
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_selectedElements.Length > 0)
        {
            foreach (var objectIndex in _selectedElements
                         .Where(hit => (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                             && hit.Key.Kind == DrawingElementKind.Stroke
                             && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                             && Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                         .Select(hit => hit.Key.ObjectIndex)
                         .Distinct()
                         .Take(MaxSelectionOutlines))
            {
                DrawBezierSelectionContext(g, objectIndex);
            }

            var drawnElements = 0;
            foreach (var hit in _selectedElements)
            {
                if ((uint)hit.Key.ObjectIndex >= Scene.ObjectCount
                    || hit.Key == SelectedElement.Key
                    || SuppressFillEdgeBezierSelectionOutline(hit.Key.ObjectIndex)
                    || !Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                {
                    continue;
                }

                DrawElementSelectionOutline(g, hit, primary: false);
                drawnElements++;
                if (drawnElements >= MaxSelectionOutlines) break;
            }

            if (SelectedElement.IsValid
                && (uint)SelectedElement.Key.ObjectIndex < Scene.ObjectCount
                && !SuppressFillEdgeBezierSelectionOutline(SelectedElement.Key.ObjectIndex)
                && Scene.IsObjectActive(SelectedElement.Key.ObjectIndex, Frame))
            {
                DrawElementSelectionOutline(g, SelectedElement, primary: true);
                if (PenPathHandlesVisible && !TransformMode)
                {
                    foreach (var objectIndex in _selectedElements
                                 .Where(hit => hit.Key.Kind == DrawingElementKind.Stroke
                                     && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                                     && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                                     && Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
                                 .Select(hit => hit.Key.ObjectIndex)
                                 .Distinct()
                                 .Take(MaxSelectionOutlines))
                    {
                        DrawBezierHandles(g, objectIndex);
                    }
                }
                else if (IsValidEditableBezierHit(SelectedElement))
                {
                    if (!TransformMode) DrawBezierHandles(g, SelectedElement);
                }
            }

            DrawDrawingObjectSelectionOverlay(g);
            DrawTransformOverlay(g);
            DrawHoveredLineControls(g);
            g.SmoothingMode = oldMode;
            return;
        }

        var drawn = 0;
        foreach (var index in SelectedObjects)
        {
            if (index == SelectedObject || index < 0 || index >= Scene.ObjectCount) continue;
            if (SuppressFillEdgeBezierSelectionOutline(index)) continue;
            if (!Scene.IsObjectActive(index, Frame)) continue;
            DrawSelectionOutline(g, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = SelectedObject;
        if (primary >= 0
            && primary < Scene.ObjectCount
            && !SuppressFillEdgeBezierSelectionOutline(primary)
            && Scene.IsObjectActive(primary, Frame))
        {
            DrawSelectionOutline(g, primary, primary: true);
        }

        DrawDrawingObjectSelectionOverlay(g);
        DrawTransformOverlay(g);
        DrawHoveredLineControls(g);
        g.SmoothingMode = oldMode;
    }

    private void DrawSelectionOutline(Graphics g, int i, bool primary)
    {
        var shape = Scene.ShapeKind.Length > i ? Scene.ShapeKind[i] : ShapeKind.Rectangle;

        if (shape == ShapeKind.Text)
        {
            if (TextAreaOverlayVisible(i))
            {
                DrawTextAreaOverlay(g, i, primary && TextAreaResizeHandlesVisible(i));
            }
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (primary && !TransformMode) DrawBezierGuides(g, i);
            else DrawBezierOutline(g, i, primary);
        }
        else if (IsFreehandShape(shape))
        {
            var points = GetBoundaryScreenPolyline(i);
            var highlightKind = SelectionHighlightForShape(shape);
            if (points.Length == 1)
            {
                DrawSelectionDot(g, points[0], primary, highlightKind);
            }
            else if (points.Length > 1)
            {
                DrawSelectionPolyline(g, points, primary, highlightKind);
            }
        }
        else
        {
            if (shape == ShapeKind.Path)
            {
                using var path = CreateObjectBoundaryPath(Scene, i);
                if (path.PointCount > 0)
                {
                    DrawSelectionPath(g, path, primary, SelectionHighlightKind.Fill);
                }
            }
            else if (TryGetSelectionWorldContours(i, shape, out var contours))
            {
                foreach (var contour in contours)
                {
                    var points = contour.Select(WorldToScreen).ToArray();
                    if (points.Length >= 3) DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
                }
            }
            else
            {
                DrawBoundaryOutline(g, i, primary, SelectionHighlightForShape(shape));
            }

            if (primary && shape is not ShapeKind.Path and not ShapeKind.Text && !TransformMode) DrawBoundaryHandles(g, i);
        }
    }

    private bool TryGetSelectionWorldContours(int objectIndex, ShapeKind shape, out PointF[][] contours)
    {
        if (shape == ShapeKind.Text) return Scene.TryGetTextWorldContours(objectIndex, out contours);
        contours = Array.Empty<PointF[]>();
        return false;
    }

    private void DrawElementSelectionOutline(Graphics g, DrawingElementHit hit, bool primary)
    {
        var objectIndex = hit.Key.ObjectIndex;
        var shape = Scene.ShapeKind[objectIndex];
        if (hit.Key.Kind == DrawingElementKind.Stroke)
        {
            if (shape == ShapeKind.Line)
            {
                DrawBezierGuides(g, objectIndex, hit.StartT, hit.EndT, primary);
                return;
            }

            var points = GetSelectedStrokePartPoints(hit).Select(WorldToScreen).ToArray();
            if (points.Length == 1) DrawSelectionDot(g, points[0], primary, SelectionHighlightKind.Stroke);
            else if (points.Length > 1) DrawSelectionPolyline(g, points, primary, SelectionHighlightKind.Stroke);
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            foreach (var contour in GetSelectedFillPartContours(hit))
            {
                var points = contour.Select(WorldToScreen).ToArray();
                if (points.Length >= 3) DrawSelectionPolygon(g, points, primary, SelectionHighlightKind.Fill);
            }

            return;
        }

        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var points = GetSelectedBoundaryPartPoints(hit).Select(WorldToScreen).ToArray();
            if (points.Length > 1) DrawSelectionPolyline(g, points, primary, SelectionHighlightKind.Stroke);
        }
    }

    private void DrawFillEdgeBezierOverlay(Graphics graphics)
    {
        if (!FillEdgeBezierOverlayVisible) return;

        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var edge = new Pen(Color.Lime, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var guide = new Pen(Color.Lime, 1f);
        using var handleCore = new SolidBrush(BackColor);
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var segment in _fillEdgeBezierOverlaySegments)
        {
            if (!Finite(segment)) continue;
            path.StartFigure();
            path.AddBezier(
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }

        if (path.PointCount > 0) graphics.DrawPath(edge, path);
        foreach (var segment in _fillEdgeBezierOverlaySegments)
        {
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex || !Finite(segment)) continue;
            DrawFillEdgeBezierSegmentHandles(graphics, segment, guide, handleCore, edge);
        }

        var active = _fillEdgeBezierOverlaySegments.FirstOrDefault(segment =>
            segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex);
        if (_fillEdgeBezierOverlayActivePartIndex >= 0
            && active.PartIndex == _fillEdgeBezierOverlayActivePartIndex
            && Finite(active))
        {
            DrawFillEdgeBezierSegmentHandles(graphics, active, guide, handleCore, edge);
        }

        graphics.SmoothingMode = oldMode;
    }

    private void DrawFillEdgeBezierSegmentHandles(
        Graphics graphics,
        FillEdgeBezierOverlaySegment segment,
        Pen guide,
        Brush handleCore,
        Pen edge)
    {
        var start = WorldToScreen(segment.Start);
        var control1 = WorldToScreen(segment.Control1);
        var control2 = WorldToScreen(segment.Control2);
        var end = WorldToScreen(segment.End);
        graphics.DrawLine(guide, start, control1);
        graphics.DrawLine(guide, end, control2);
        DrawFillEdgeBezierAnchor(graphics, start, handleCore, edge);
        DrawFillEdgeBezierAnchor(graphics, end, handleCore, edge);
        DrawFillEdgeBezierControl(graphics, control1, handleCore, edge);
        DrawFillEdgeBezierControl(graphics, control2, handleCore, edge);
    }

    private static void DrawFillEdgeBezierAnchor(Graphics graphics, PointF point, Brush core, Pen edge)
    {
        const float radius = 3.5f;
        graphics.FillRectangle(core, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        graphics.DrawRectangle(edge, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    private static void DrawFillEdgeBezierControl(Graphics graphics, PointF point, Brush core, Pen edge)
    {
        const float radius = 4f;
        graphics.FillEllipse(core, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(edge, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    private void DrawPenAnchorGuides(Graphics graphics)
    {
        if (!PenAnchorGuidesVisible) return;
        var point = WorldToScreen(PenAnchorGuidePoint);
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guidePen = new Pen(Color.FromArgb(205, 70, 210, 235), 1f) { DashStyle = DashStyle.Dash };
        using var markerPen = new Pen(Color.FromArgb(245, 128, 239, 255), 1.5f);
        using var markerFill = new SolidBrush(Color.FromArgb(210, 18, 48, 55));
        if (PenAnchorGuideVertical) graphics.DrawLine(guidePen, point.X, 0, point.X, Height);
        if (PenAnchorGuideHorizontal) graphics.DrawLine(guidePen, 0, point.Y, Width, point.Y);

        if (PenAnchorGuideInsertion)
        {
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 7),
                new PointF(point.X + 7, point.Y),
                new PointF(point.X, point.Y + 7),
                new PointF(point.X - 7, point.Y)
            };
            graphics.FillPolygon(markerFill, diamond);
            graphics.DrawPolygon(markerPen, diamond);
            graphics.DrawLine(markerPen, point.X - 3, point.Y, point.X + 3, point.Y);
            graphics.DrawLine(markerPen, point.X, point.Y - 3, point.X, point.Y + 3);
        }
        else if (PenAnchorGuideSnapped)
        {
            graphics.FillEllipse(markerFill, point.X - 6, point.Y - 6, 12, 12);
            graphics.DrawEllipse(markerPen, point.X - 6, point.Y - 6, 12, 12);
            graphics.DrawLine(markerPen, point.X - 8, point.Y, point.X + 8, point.Y);
            graphics.DrawLine(markerPen, point.X, point.Y - 8, point.X, point.Y + 8);
        }
        else
        {
            graphics.FillEllipse(markerFill, point.X - 3, point.Y - 3, 6, 6);
            graphics.DrawEllipse(markerPen, point.X - 3, point.Y - 3, 6, 6);
        }

        graphics.SmoothingMode = oldMode;
    }

    private void DrawDrawingPreview(Graphics g)
    {
        if (!DrawingPreviewVisible) return;

        var start = DrawingPreviewStart;
        var end = DrawingPreviewEnd;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 0.001f) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fillColor = Color.FromArgb(72, DrawingPreviewColor);
        var strokeColor = Color.FromArgb(230, DrawingPreviewColor);
        using var fill = new SolidBrush(fillColor);
        using var stroke = new Pen(strokeColor, Math.Max(0.1f, WorldLengthToScreen(DrawingPreviewStroke)))
        {
            DashStyle = DashStyle.Solid,
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        if (DrawingPreviewShape == ShapeKind.Line)
        {
            var a = WorldToScreen(start);
            var b = WorldToScreen(end);
            if (DrawingPreviewHasCurve)
            {
                var segments = DrawingPreviewCurveSegments.Count > 0
                    ? DrawingPreviewCurveSegments
                    : [new CubicDrawingPreviewSegment(start, DrawingPreviewControl, DrawingPreviewControl2, end)];
                foreach (var segment in segments)
                {
                    var segmentStart = WorldToScreen(segment.Start);
                    var segmentControl1 = WorldToScreen(segment.Control1);
                    var segmentControl2 = WorldToScreen(segment.Control2);
                    var segmentEnd = WorldToScreen(segment.End);
                    using var path = BuildCubicPath(segmentStart, segmentControl1, segmentControl2, segmentEnd);
                    g.DrawPath(stroke, path);
                }
                var control1 = WorldToScreen(DrawingPreviewControl);
                var control2 = WorldToScreen(DrawingPreviewControl2);
                if (segments.Count == 1)
                {
                    g.DrawLine(_previewGuidePen, a, control1);
                    g.DrawLine(_previewGuidePen, b, control2);
                }
                DrawHandle(g, a, _handleBrush, 7);
                DrawHandle(g, b, _handleBrush, 7);
                if (segments.Count == 1)
                {
                    DrawHandle(g, control1, _bezierHandleBrush, 9);
                    DrawHandle(g, control2, _bezierHandleBrush, 9);
                }
                g.SmoothingMode = oldMode;
                return;
            }

            g.DrawLine(_previewGuidePen, a, b);
            g.DrawLine(stroke, a, b);
            DrawHandle(g, a, _handleBrush, 7);
            DrawHandle(g, b, _handleBrush, 7);
            g.SmoothingMode = oldMode;
            return;
        }

        var center = WorldToScreen((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        var w = Math.Max(2, WorldLengthToScreen(Math.Abs(dx)));
        var h = Math.Max(2, WorldLengthToScreen(Math.Abs(dy)));
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        DrawPreviewLocalShape(g, DrawingPreviewShape, DrawingPreviewShapeVertexCount, fill, stroke, w, h);
        g.Restore(state);

        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(boundsPen, center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        g.SmoothingMode = oldMode;
    }

    private void DrawPenDirectionHandles(Graphics graphics)
    {
        if (!PenDirectionHandlesVisible) return;
        var anchor = WorldToScreen(PenDirectionAnchor);
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guide = new Pen(Color.FromArgb(220, 112, 204, 255), 1f);
        using var pointFill = new SolidBrush(Color.FromArgb(245, 18, 48, 55));
        using var pointBorder = new Pen(Color.FromArgb(250, 146, 224, 255), 1.4f);
        DrawDirectionPoint(PenDirectionIncoming);
        DrawDirectionPoint(PenDirectionOutgoing);
        graphics.FillRectangle(pointFill, anchor.X - 3.5f, anchor.Y - 3.5f, 7, 7);
        graphics.DrawRectangle(pointBorder, anchor.X - 3.5f, anchor.Y - 3.5f, 7, 7);
        graphics.SmoothingMode = oldMode;

        void DrawDirectionPoint(PointF? world)
        {
            if (world is not { } point) return;
            var screen = WorldToScreen(point);
            graphics.DrawLine(guide, anchor, screen);
            graphics.FillEllipse(pointFill, screen.X - 3.5f, screen.Y - 3.5f, 7, 7);
            graphics.DrawEllipse(pointBorder, screen.X - 3.5f, screen.Y - 3.5f, 7, 7);
        }
    }

    private void DrawFreehandPreview(Graphics g)
    {
        if (!FreehandPreviewVisible || FreehandPreviewPoints.Count == 0) return;
        var screenWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewStroke));
        var variableWidth = FreehandPreviewDiameters.Count == FreehandPreviewPoints.Count;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (FreehandPreviewBrushShape is { IsTraditionalBrush: true, IsRadiallySymmetric: false } tipShape)
        {
            DrawTraditionalBrushPreview(g, tipShape, screenWidth, variableWidth);
            g.SmoothingMode = oldMode;
            return;
        }

        if (FreehandPreviewPoints.Count == 1)
        {
            var point = WorldToScreen(FreehandPreviewPoints[0]);
            if (variableWidth) screenWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[0]));
            using var dot = new SolidBrush(FreehandPreviewColor);
            g.FillEllipse(dot, point.X - screenWidth * 0.5f, point.Y - screenWidth * 0.5f, screenWidth, screenWidth);
        }
        else if (variableWidth)
        {
            using var dot = new SolidBrush(FreehandPreviewColor);
            var previous = WorldToScreen(FreehandPreviewPoints[0]);
            var previousWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[0]));
            g.FillEllipse(dot, previous.X - previousWidth * 0.5f, previous.Y - previousWidth * 0.5f, previousWidth, previousWidth);
            for (var i = 1; i < FreehandPreviewPoints.Count; i++)
            {
                var current = WorldToScreen(FreehandPreviewPoints[i]);
                var currentWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[i]));
                using var pen = StrokePen(FreehandPreviewColor, (previousWidth + currentWidth) * 0.5f);
                g.DrawLine(pen, previous, current);
                g.FillEllipse(dot, current.X - currentWidth * 0.5f, current.Y - currentWidth * 0.5f, currentWidth, currentWidth);
                previous = current;
                previousWidth = currentWidth;
            }
        }
        else
        {
            using var pen = StrokePen(FreehandPreviewColor, screenWidth);
            var previous = WorldToScreen(FreehandPreviewPoints[0]);
            for (var i = 1; i < FreehandPreviewPoints.Count; i++)
            {
                var current = WorldToScreen(FreehandPreviewPoints[i]);
                g.DrawLine(pen, previous, current);
                previous = current;
            }
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawTraditionalBrushPreview(Graphics g, BrushShape tipShape, float defaultWidth, bool variableWidth)
    {
        var contour = tipShape.NormalizedContour(0.5f);
        if (contour.Length < 3) return;

        using var fill = new SolidBrush(FreehandPreviewColor);
        for (var index = 0; index < FreehandPreviewPoints.Count; index++)
        {
            var diameter = variableWidth
                ? Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewDiameters[index]))
                : defaultWidth;
            var center = WorldToScreen(FreehandPreviewPoints[index]);
            var radius = diameter * 0.5f;
            var points = new PointF[contour.Length];
            for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
            {
                points[pointIndex] = new PointF(
                    center.X + contour[pointIndex].X * radius,
                    center.Y + contour[pointIndex].Y * radius);
            }

            g.FillPolygon(fill, points);
        }
    }

    private void DrawBrushTipCursor(Graphics g)
    {
        if (!BrushTipCursorVisible || BrushTipCursorShape is null) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outlineColor = BrushTipCursorIsEraser
            ? Color.FromArgb(235, 255, 120, 120)
            : Color.FromArgb(235, 112, 204, 255);
        foreach (var layer in BrushTipCursorShape.Layers)
        {
            var points = BrushTipCursorShape.NormalizedContour(layer.Threshold)
                .Select(point => new PointF(
                    BrushTipCursorScreen.X + point.X * BrushTipCursorRadiusPixels,
                    BrushTipCursorScreen.Y + point.Y * BrushTipCursorRadiusPixels))
                .ToArray();
            if (points.Length < 3) continue;
            using var fill = new SolidBrush(Color.FromArgb(
                (int)Math.Clamp(42 * layer.Opacity / 0.66f, 8, 42),
                outlineColor));
            using var pen = new Pen(Color.FromArgb(
                (int)Math.Clamp(190 * layer.Opacity / 0.66f, 48, 190),
                outlineColor),
                layer.Threshold >= 0.7f ? 1.4f : 1f);
            g.FillPolygon(fill, points);
            g.DrawPolygon(pen, points);
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawBrushColorPalette(Graphics g)
    {
        if (!BrushColorPaletteVisible) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var center = BrushColorPaletteCenter;
        var radius = BrushColorPaletteRadius + BrushColorPaletteSwatchSize * 0.72f;
        using (var backdrop = new SolidBrush(Color.FromArgb(214, Theme.Top)))
        {
            g.FillEllipse(backdrop, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        using (var backdropOutline = new Pen(Color.FromArgb(225, Theme.BorderHover), 1f))
        {
            g.DrawEllipse(backdropOutline, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        for (var index = 0; index < _brushColorPaletteColors.Length; index++)
        {
            var bounds = BrushColorPaletteSwatchBounds(index);
            using var fill = new SolidBrush(_brushColorPaletteColors[index]);
            using var outline = new Pen(
                index == _brushColorPaletteHoveredIndex
                    ? Color.FromArgb(255, 255, 240, 168)
                    : Color.FromArgb(235, Theme.BorderHover),
                index == _brushColorPaletteHoveredIndex ? 2.4f : 1f);
            g.FillRectangle(fill, bounds);
            g.DrawRectangle(outline, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        }

        using var pointer = new SolidBrush(Color.FromArgb(230, Theme.Text));
        g.FillEllipse(pointer, center.X - 3, center.Y - 3, 6, 6);
        g.SmoothingMode = oldMode;
    }

    private void DrawFillAnimation(Graphics g)
    {
        if (!FillAnimationVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in _fillAnimationContours)
        {
            var points = contour.Select(point => WorldToScreen(point.X, point.Y)).ToArray();
            if (points.Length >= 3) path.AddPolygon(points);
        }

        if (path.PointCount > 0)
        {
            var origin = WorldToScreen(_fillAnimationOrigin);
            var radius = Math.Max(10f, WorldLengthToScreen(FillAnimationBloomRadiusWorld));
            var fade = FillAnimationFade;
            var glowColor = FillAnimationGlowColor;
            var state = g.Save();
            try
            {
                g.SetClip(path, CombineMode.Intersect);
                DrawFillBloom(g, origin, radius, glowColor, fade);
            }
            finally
            {
                g.Restore(state);
            }
        }
        g.SmoothingMode = oldMode;
    }

    private static void DrawFillBloom(Graphics g, PointF origin, float radius, Color color, float fade)
    {
        DrawFillBloomLayer(g, origin, radius, color, 20f * fade);
        DrawFillBloomLayer(g, origin, radius * 0.72f, color, 28f * fade);
        DrawFillBloomLayer(g, origin, radius * 0.38f, color, 38f * fade);

        var waveWidth = Math.Clamp(radius * 0.025f, 2f, 12f);
        var waveAlpha = (int)Math.Clamp(210f * fade, 0f, 210f);
        using var wave = new Pen(Color.FromArgb(waveAlpha, color), waveWidth);
        g.DrawEllipse(wave, origin.X - radius, origin.Y - radius, radius * 2f, radius * 2f);
    }

    private static void DrawFillBloomLayer(Graphics g, PointF origin, float radius, Color color, float alpha)
    {
        if (radius <= 0.5f || alpha <= 0.5f) return;
        using var brush = new SolidBrush(Color.FromArgb((int)Math.Clamp(alpha, 0f, 255f), color));
        g.FillEllipse(brush, origin.X - radius, origin.Y - radius, radius * 2f, radius * 2f);
    }

    private static float SmoothStep(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private static Color LightenForFillAnimation(Color color)
    {
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + (255 - color.R) * 0.38f),
            (int)Math.Round(color.G + (255 - color.G) * 0.38f),
            (int)Math.Round(color.B + (255 - color.B) * 0.38f));
    }

    private void DrawFillPreview(Graphics g)
    {
        if (!FillPreviewVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in _fillPreviewContours)
        {
            var points = contour.Select(point => WorldToScreen(point.X, point.Y)).ToArray();
            if (points.Length >= 3) path.AddPolygon(points);
        }

        if (path.PointCount > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(72, _fillPreviewColor));
            using var outline = new Pen(Color.FromArgb(150, _fillPreviewColor), 1f) { DashStyle = DashStyle.Dash };
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawFillToolCursor(Graphics g)
    {
        if (!_fillToolCursorVisible) return;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = _fillToolCursorScreen.X;
        var y = _fillToolCursorScreen.Y;
        var bucket = new[]
        {
            new PointF(x - 10, y - 12),
            new PointF(x + 3, y - 12),
            new PointF(x + 10, y - 5),
            new PointF(x - 3, y - 5)
        };
        using var fill = new SolidBrush(Color.FromArgb(150, _fillToolCursorColor));
        using var outline = new Pen(Color.FromArgb(235, Theme.Text), 1.4f);
        using var drop = new SolidBrush(Color.FromArgb(220, _fillToolCursorColor));
        using var dropOutline = new Pen(Color.FromArgb(235, Theme.Text), 1f);
        g.FillPolygon(fill, bucket);
        g.DrawPolygon(outline, bucket);
        g.FillEllipse(drop, x + 3, y - 1, 6, 8);
        g.DrawEllipse(dropOutline, x + 3, y - 1, 6, 8);
        g.SmoothingMode = oldMode;
    }

    private void DrawGradientOverlay(Graphics g)
    {
        if (!_gradientOverlayVisible) return;
        var start = WorldToScreen(_gradientOverlayStart);
        var end = WorldToScreen(_gradientOverlayEnd);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var guide = new Pen(Color.FromArgb(230, 255, 244, 166), 1.5f) { DashStyle = DashStyle.Dash };
        using var startFill = new SolidBrush(_gradientOverlayStartColor);
        using var endFill = new SolidBrush(_gradientOverlayEndColor);
        using var outline = new Pen(Color.FromArgb(245, 16, 18, 22), 1.4f);
        if (_gradientOverlayKind == GradientKind.Radial)
        {
            var radius = Distance(start, end);
            using var radialGuide = new Pen(Color.FromArgb(190, 255, 244, 166), 1.25f) { DashStyle = DashStyle.Dash };
            g.DrawEllipse(radialGuide, start.X - radius, start.Y - radius, radius * 2, radius * 2);
        }
        g.DrawLine(guide, start, end);
        g.FillEllipse(startFill, start.X - 6, start.Y - 6, 12, 12);
        g.DrawEllipse(outline, start.X - 6, start.Y - 6, 12, 12);
        if (_gradientOverlayKind == GradientKind.ShapeRadial)
        {
            var edgeMarker = new[]
            {
                new PointF(end.X, end.Y - 4),
                new PointF(end.X + 4, end.Y),
                new PointF(end.X, end.Y + 4),
                new PointF(end.X - 4, end.Y)
            };
            g.FillPolygon(endFill, edgeMarker);
            g.DrawPolygon(outline, edgeMarker);
        }
        else
        {
            g.FillEllipse(endFill, end.X - 6, end.Y - 6, 12, 12);
            g.DrawEllipse(outline, end.X - 6, end.Y - 6, 12, 12);
        }
        for (var index = 1; index < _gradientOverlayStops.Length - 1; index++)
        {
            var point = Lerp(start, end, _gradientOverlayStops[index].Position);
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 6),
                new PointF(point.X + 6, point.Y),
                new PointF(point.X, point.Y + 6),
                new PointF(point.X - 6, point.Y)
            };
            using var fill = new SolidBrush(Color.FromArgb(_gradientOverlayStops[index].Argb));
            g.FillPolygon(fill, diamond);
            g.DrawPolygon(outline, diamond);
        }
        g.SmoothingMode = oldMode;
    }

    private static float SquaredDistance(Point a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private bool CubicCurveHit(Point screen, FillEdgeBezierOverlaySegment segment, float radiusPixels)
    {
        if (!Finite(segment)) return false;
        var start = WorldToScreen(segment.Start);
        var control1 = WorldToScreen(segment.Control1);
        var control2 = WorldToScreen(segment.Control2);
        var end = WorldToScreen(segment.End);
        var controlLength = Distance(start, control1) + Distance(control1, control2) + Distance(control2, end);
        var steps = Math.Clamp((int)MathF.Ceiling(controlLength / 8f), 8, 64);
        var point = new PointF(screen.X, screen.Y);
        var previous = start;
        var radiusSquared = radiusPixels * radiusPixels;
        for (var step = 1; step <= steps; step++)
        {
            var current = CubicPoint(start, control1, control2, end, step / (float)steps);
            if (SquaredDistanceToSegment(point, previous, current) <= radiusSquared) return true;
            previous = current;
        }

        return false;
    }

    private static float SquaredDistanceToSegment(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon)
        {
            dx = point.X - start.X;
            dy = point.Y - start.Y;
            return dx * dx + dy * dy;
        }

        var amount = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
        var closestX = start.X + dx * amount;
        var closestY = start.Y + dy * amount;
        dx = point.X - closestX;
        dy = point.Y - closestY;
        return dx * dx + dy * dy;
    }

    private static bool Finite(FillEdgeBezierOverlaySegment segment)
    {
        return Finite(segment.Start)
            && Finite(segment.Control1)
            && Finite(segment.Control2)
            && Finite(segment.End);
    }

    private static bool Finite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private void DrawMarquee(Graphics g)
    {
        if (!MarqueeVisible || MarqueeOverlayActive) return;
        var rect = Rectangle.FromLTRB(
            Math.Min(MarqueeStart.X, MarqueeEnd.X),
            Math.Min(MarqueeStart.Y, MarqueeEnd.Y),
            Math.Max(MarqueeStart.X, MarqueeEnd.X),
            Math.Max(MarqueeStart.Y, MarqueeEnd.Y));

        if (rect.Width < 2 || rect.Height < 2) return;
        g.FillRectangle(_marqueeBrush, rect);
        g.DrawRectangle(_marqueePen, rect);
    }

    private void DrawPreviewLocalShape(Graphics g, ShapeKind shape, int shapeVertexCount, Brush fill, Pen stroke, float w, float h)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                g.FillEllipse(fill, rect);
                g.DrawEllipse(stroke, rect);
                break;
            case ShapeKind.Triangle:
                DrawPreviewPolygon(g, fill, stroke, RegularPolygonPoints(3, w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Polygon:
                DrawPreviewPolygon(g, fill, stroke, RegularPolygonPoints(PolygonVertexCount(shapeVertexCount), w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Star:
                DrawPreviewPolygon(g, fill, stroke, StarPoints(StarVertexCount(shapeVertexCount), w, h, -MathF.PI / 2));
                break;
            default:
                g.FillRectangle(fill, rect);
                g.DrawRectangle(stroke, rect.X, rect.Y, rect.Width, rect.Height);
                break;
        }
    }

    private static void DrawPreviewPolygon(Graphics g, Brush fill, Pen stroke, PointF[] points)
    {
        g.FillPolygon(fill, points);
        g.DrawPolygon(stroke, points);
    }

    private void DrawBezierLine(Graphics g, int i, Brush brush, float screenStroke)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        var startStyle = Scene.GetLineEndpointStyle(i, startEndpoint: true);
        var endStyle = Scene.GetLineEndpointStyle(i, startEndpoint: false);
        using var linePen = new Pen(brush, screenStroke)
        {
            StartCap = LineCapForEndpoint(startStyle),
            EndCap = LineCapForEndpoint(endStyle),
            LineJoin = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp ? LineJoin.Miter : LineJoin.Round,
            MiterLimit = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp ? 8 : 1
        };

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Scene.IsLineStraight(i)) g.DrawLine(linePen, start, end);
        else
        {
            using var path = BuildCubicPath(start, control1, control2, end);
            g.DrawPath(linePen, path);
        }
        DrawEndpointJoin(g, i, startEndpoint: true, screenStroke, brush);
        DrawEndpointJoin(g, i, startEndpoint: false, screenStroke, brush);
        g.SmoothingMode = oldMode;
    }

    private void DrawGradientBezierLine(Graphics g, VectorScene scene, int objectIndex, float screenStroke)
    {
        if (scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            DrawRadialGradientBezierLine(g, scene, objectIndex, screenStroke);
            return;
        }

        using var gradient = CreateGradientFillBrush(scene, objectIndex);
        DrawBezierLine(g, objectIndex, gradient, screenStroke);
    }

    private void DrawRadialGradientBezierLine(Graphics g, VectorScene scene, int objectIndex, float screenStroke)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(objectIndex);
        var center = WorldToScreen(scene.GetGradientStart(objectIndex));
        var radiusPoint = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var radius = Math.Max(0.5f, Distance(center, radiusPoint));
        var stops = scene.GetGradientStops(objectIndex);
        var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const int segments = 64;
        for (var segment = 0; segment < segments; segment++)
        {
            var startT = segment / (float)segments;
            var endT = (segment + 1) / (float)segments;
            var a = CubicPoint(start, control1, control2, end, startT);
            var b = CubicPoint(start, control1, control2, end, endT);
            var midpoint = CubicPoint(start, control1, control2, end, (startT + endT) * 0.5f);
            var position = Math.Clamp(Distance(midpoint, center) / radius, 0f, 1f);
            using var pen = new Pen(GradientColorAt(stops, position), screenStroke)
            {
                StartCap = segment == 0 ? LineCapForEndpoint(startStyle) : LineCap.Round,
                EndCap = segment == segments - 1 ? LineCapForEndpoint(endStyle) : LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLine(pen, a, b);
        }

        DrawRadialGradientEndpointJoin(g, scene, objectIndex, startEndpoint: true, screenStroke);
        DrawRadialGradientEndpointJoin(g, scene, objectIndex, startEndpoint: false, screenStroke);

        g.SmoothingMode = oldMode;
    }

    private static LineCap LineCapForEndpoint(LineEndpointStyle endpointStyle)
    {
        return endpointStyle == LineEndpointStyle.Sharp ? LineCap.Flat : LineCap.Round;
    }

    private void DrawEndpointJoin(Graphics g, int objectIndex, bool startEndpoint, float screenStroke, Brush brush)
    {
        if (!TryGetEndpointJoins(objectIndex, startEndpoint, screenStroke, out var joint, out var miters)) return;
        foreach (var miter in miters) FillMiterJoin(g, brush, joint, miter);
    }

    private void DrawRadialGradientEndpointJoin(
        Graphics g,
        VectorScene scene,
        int objectIndex,
        bool startEndpoint,
        float screenStroke)
    {
        if (!TryGetEndpointJoins(objectIndex, startEndpoint, screenStroke, out var joint, out var miters)) return;
        using var path = new GraphicsPath(FillMode.Winding);
        foreach (var miter in miters) AddMiterJoinPolygons(path, joint, miter);
        DrawRadialGradientFill(g, path, scene, objectIndex, scene.GetGradientStops(objectIndex));
    }

    private bool TryGetEndpointJoins(
        int objectIndex,
        bool startEndpoint,
        float screenStroke,
        out PointF joint,
        out LineMiterJoin[] miters)
    {
        joint = PointF.Empty;
        miters = Array.Empty<LineMiterJoin>();
        if (Scene.GetLineEndpointStyle(objectIndex, startEndpoint) != LineEndpointStyle.Sharp
            || !Scene.TryGetLineEndpointJunctionForRender(objectIndex, startEndpoint, Frame, out var junction)
            || !junction.AllSharp
            || objectIndex != junction.OwnerObjectIndex)
        {
            return false;
        }

        var current = GetBezierScreenPoints(objectIndex);
        joint = startEndpoint ? current.Start : current.End;
        var interiorPoints = new PointF[junction.Connections.Length + 1];
        interiorPoints[0] = EndpointInteriorPoint(current, startEndpoint);
        for (var index = 0; index < junction.Connections.Length; index++)
        {
            var connection = junction.Connections[index];
            var adjacent = GetBezierScreenPoints(connection.ObjectIndex);
            interiorPoints[index + 1] = EndpointInteriorPoint(adjacent, connection.StartEndpoint);
        }

        miters = LineJoinGeometry.CreateJunctionMiters(joint, interiorPoints, screenStroke * 0.5f);
        return miters.Length > 0;
    }

    private static void FillMiterJoin(Graphics g, Brush brush, PointF joint, LineMiterJoin miter)
    {
        g.FillPolygon(brush, [joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
        g.FillPolygon(brush, [joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
    }

    private static void AddMiterJoinPolygons(GraphicsPath path, PointF joint, LineMiterJoin miter)
    {
        path.AddPolygon([joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
        path.AddPolygon([joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
    }

    private static PointF EndpointInteriorPoint(
        (PointF Start, PointF Control1, PointF Control2, PointF End) curve,
        bool startEndpoint)
    {
        var endpoint = startEndpoint ? curve.Start : curve.End;
        var control = startEndpoint ? curve.Control1 : curve.Control2;
        var controlDistanceSquared = (control.X - endpoint.X) * (control.X - endpoint.X)
            + (control.Y - endpoint.Y) * (control.Y - endpoint.Y);
        if (controlDistanceSquared > 0.01f) return control;
        return startEndpoint ? curve.End : curve.Start;
    }

    private void DrawBezierGuides(Graphics g, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var path = BuildCubicPath(start, control1, control2, end);
        DrawSelectionPath(
            g,
            path,
            true,
            SelectionHighlightKind.Stroke,
            Scene.GetLineEndpointStyle(i, startEndpoint: true),
            Scene.GetLineEndpointStyle(i, startEndpoint: false));
        DrawBezierHandles(g, start, control1, control2, end);
    }

    private void DrawBezierHandles(Graphics g, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        DrawBezierHandles(g, start, control1, control2, end);
    }

    private void DrawBezierHandles(Graphics g, DrawingElementHit hit)
    {
        if (!TryGetEditableBezierWorldPoints(hit, out var start, out var control1, out var control2, out var end)) return;
        DrawBezierHandles(g, WorldToScreen(start), WorldToScreen(control1), WorldToScreen(control2), WorldToScreen(end));
    }

    private void DrawHoveredLineControls(Graphics g)
    {
        var hit = _hoveredLineElement;
        if (!IsValidEditableBezierHit(hit)
            || (SelectedElement.IsValid && SelectedElement.Key == hit.Key)
            || (!SelectedElement.IsValid && SelectedObject == hit.Key.ObjectIndex))
        {
            return;
        }

        if (!TryGetEditableBezierWorldPoints(hit, out var start, out var control1, out var control2, out var end)) return;
        var startScreen = WorldToScreen(start);
        var control1Screen = WorldToScreen(control1);
        var control2Screen = WorldToScreen(control2);
        var endScreen = WorldToScreen(end);
        using var guide = new Pen(Color.FromArgb(120, 112, 204, 255), 1);
        using var endpoint = new SolidBrush(Color.FromArgb(210, 255, 240, 168));
        using var controlBrush = new SolidBrush(Color.FromArgb(210, 112, 204, 255));
        g.DrawLine(guide, startScreen, control1Screen);
        g.DrawLine(guide, endScreen, control2Screen);
        DrawHandle(g, startScreen, endpoint, 7);
        DrawHandle(g, endScreen, endpoint, 7);
        DrawHandle(g, control1Screen, controlBrush, 9);
        DrawHandle(g, control2Screen, controlBrush, 9);
    }

    private void DrawBezierHandles(Graphics g, PointF start, PointF control1, PointF control2, PointF end)
    {
        g.DrawLine(_guidePen, start, control1);
        g.DrawLine(_guidePen, end, control2);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control1, _bezierHandleBrush, 10);
        DrawHandle(g, control2, _bezierHandleBrush, 10);
    }

    private void DrawTransformOverlay(Graphics g)
    {
        if (!TransformBoundsVisible) return;
        var geometry = GetTransformOverlayScreenGeometry();
        var corners = new[] { geometry.TopLeft, geometry.TopRight, geometry.BottomRight, geometry.BottomLeft };
        using var boundsPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
        using var rotationPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1);
        using var focusPen = new Pen(Color.FromArgb(245, 104, 255, 188), 1.5f);
        using var focusBrush = new SolidBrush(Color.FromArgb(220, 30, 82, 69));
        g.DrawPolygon(boundsPen, corners);
        foreach (var (_, point, _) in geometry.ResizeHandles) DrawHandle(g, point, _handleBrush, 8);
        foreach (var (_, point, corner) in geometry.RotationHandles)
        {
            g.DrawLine(rotationPen, corner, point);
            g.FillEllipse(_bezierHandleBrush, point.X - 4, point.Y - 4, 8, 8);
            g.DrawEllipse(_handleBorderPen, point.X - 4, point.Y - 4, 8, 8);
        }
        foreach (var (_, point, edge) in geometry.SkewHandles)
        {
            g.DrawLine(rotationPen, edge, point);
            var diamond = new[]
            {
                new PointF(point.X, point.Y - 5),
                new PointF(point.X + 5, point.Y),
                new PointF(point.X, point.Y + 5),
                new PointF(point.X - 5, point.Y)
            };
            g.FillPolygon(_bezierHandleBrush, diamond);
            g.DrawPolygon(_handleBorderPen, diamond);
        }

        var focus = WorldToScreen(_transformFocus);
        g.FillEllipse(focusBrush, focus.X - 6, focus.Y - 6, 12, 12);
        g.DrawEllipse(focusPen, focus.X - 6, focus.Y - 6, 12, 12);
        g.DrawLine(focusPen, focus.X - 9, focus.Y, focus.X + 9, focus.Y);
        g.DrawLine(focusPen, focus.X, focus.Y - 9, focus.X, focus.Y + 9);
    }

    private void DrawDrawingObjectSelectionOverlay(Graphics g)
    {
        if (!DrawingObjectSelectionVisible) return;
        var rect = WorldToScreenBounds(_drawingObjectSelectionBounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        g.DrawRectangle(_drawingObjectSelectionOuterGlowPen, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawRectangle(_drawingObjectSelectionGlowPen, rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawRectangle(_drawingObjectSelectionPen, rect.X, rect.Y, rect.Width, rect.Height);
        if (DrawingObjectAnchorVisible)
        {
            var anchor = WorldToScreen(_drawingObjectAnchor);
            g.FillEllipse(_handleBrush, anchor.X - 4, anchor.Y - 4, 8, 8);
            g.DrawEllipse(_handleBorderPen, anchor.X - 4, anchor.Y - 4, 8, 8);
            g.DrawLine(_drawingObjectSelectionPen, anchor.X - 9, anchor.Y, anchor.X + 9, anchor.Y);
            g.DrawLine(_drawingObjectSelectionPen, anchor.X, anchor.Y - 9, anchor.X, anchor.Y + 9);
        }
    }

    private RectangleF WorldToScreenBounds(RectangleF bounds)
    {
        var topLeft = WorldToScreen(new PointF(bounds.Left, bounds.Top));
        var bottomRight = WorldToScreen(new PointF(bounds.Right, bounds.Bottom));
        return RectangleF.FromLTRB(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
    }

    private static PointF Midpoint(PointF left, PointF right)
    {
        return new PointF((left.X + right.X) * 0.5f, (left.Y + right.Y) * 0.5f);
    }

    private static PointF UnitVector(PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return length > 0.0001f ? new PointF(dx / length, dy / length) : PointF.Empty;
    }

    private static PointF Offset(
        PointF origin,
        PointF firstAxis,
        float firstDistance,
        PointF secondAxis,
        float secondDistance)
    {
        return new PointF(
            origin.X + firstAxis.X * firstDistance + secondAxis.X * secondDistance,
            origin.Y + firstAxis.Y * firstDistance + secondAxis.Y * secondDistance);
    }

    private static PointF OffsetFromEdge(
        PointF edgeStart,
        PointF edgeEnd,
        PointF edgeMidpoint,
        PointF center,
        float distance)
    {
        var dx = edgeEnd.X - edgeStart.X;
        var dy = edgeEnd.Y - edgeStart.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.0001f) return edgeMidpoint;
        var normalX = -dy / length;
        var normalY = dx / length;
        if (normalX * (edgeMidpoint.X - center.X) + normalY * (edgeMidpoint.Y - center.Y) < 0)
        {
            normalX = -normalX;
            normalY = -normalY;
        }

        return new PointF(edgeMidpoint.X + normalX * distance, edgeMidpoint.Y + normalY * distance);
    }

    private void DrawBezierGuides(Graphics g, int i, float startT, float endT, bool primary)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var partialPath = BuildCubicSamplePath(start, control1, control2, end, startT, endT);
        DrawSelectionPath(
            g,
            partialPath,
            primary,
            SelectionHighlightKind.Stroke,
            startT <= DrawingTopologyRules.UnitIntersectionTolerance
                ? Scene.GetLineEndpointStyle(i, startEndpoint: true)
                : LineEndpointStyle.Round,
            endT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance
                ? Scene.GetLineEndpointStyle(i, startEndpoint: false)
                : LineEndpointStyle.Round);
    }

    private void DrawBezierSelectionContext(Graphics g, int i)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var fullPath = BuildCubicPath(start, control1, control2, end);
        using var mutedPen = new Pen(Color.FromArgb(80, SelectionLineColor(SelectionHighlightKind.Stroke, primary: false)), 1.2f);
        g.DrawPath(mutedPen, fullPath);
    }

    private void DrawBezierOutline(Graphics g, int i, bool primary)
    {
        var (start, control1, control2, end) = GetBezierScreenPoints(i);
        using var path = BuildCubicPath(start, control1, control2, end);
        DrawSelectionPath(
            g,
            path,
            primary,
            SelectionHighlightKind.Stroke,
            Scene.GetLineEndpointStyle(i, startEndpoint: true),
            Scene.GetLineEndpointStyle(i, startEndpoint: false));
    }

    private void DrawBoundaryOutline(Graphics g, int i, bool primary, SelectionHighlightKind highlightKind)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;
        DrawSelectionPolygon(g, points, primary, highlightKind);
    }

    private void DrawBoundaryPartialOutline(Graphics g, int i, float startT, float endT)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;

        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        using var mutedPen = new Pen(Color.FromArgb(80, SelectionLineColor(SelectionHighlightKind.Stroke, primary: false)), 1.2f);
        g.DrawPath(mutedPen, fullPath);
        DrawSelectionPath(g, partialPath, true, SelectionHighlightKind.Stroke);
    }

    private void DrawSelectionPath(
        Graphics g,
        GraphicsPath path,
        bool primary,
        SelectionHighlightKind highlightKind,
        LineEndpointStyle startStyle = LineEndpointStyle.Round,
        LineEndpointStyle endStyle = LineEndpointStyle.Round)
    {
        PrepareSelectionHighlightPens(highlightKind);
        var outer = primary ? _selectionOuterGlowPen : _multiSelectionOuterGlowPen;
        var glow = primary ? _selectionGlowPen : _multiSelectionGlowPen;
        var line = primary ? _selectionPen : _multiSelectionPen;
        var startCap = LineCapForEndpoint(startStyle);
        var endCap = LineCapForEndpoint(endStyle);
        outer.StartCap = glow.StartCap = line.StartCap = startCap;
        outer.EndCap = glow.EndCap = line.EndCap = endCap;
        try
        {
            g.DrawPath(outer, path);
            g.DrawPath(glow, path);
            g.DrawPath(line, path);
        }
        finally
        {
            outer.StartCap = glow.StartCap = line.StartCap = LineCap.Round;
            outer.EndCap = glow.EndCap = line.EndCap = LineCap.Round;
        }
    }

    internal static SelectionHighlightKind SelectionHighlightForShape(ShapeKind shape)
    {
        return shape is ShapeKind.Line or ShapeKind.Freeform
            ? SelectionHighlightKind.Stroke
            : SelectionHighlightKind.Fill;
    }

    internal static Color SelectionLineColor(SelectionHighlightKind highlightKind, bool primary)
    {
        return (highlightKind, primary) switch
        {
            (SelectionHighlightKind.Fill, true) => Color.FromArgb(255, 104, 244, 214),
            (SelectionHighlightKind.Fill, false) => Color.FromArgb(235, 112, 220, 255),
            (SelectionHighlightKind.Stroke, true) => Color.FromArgb(255, 255, 214, 92),
            _ => Color.FromArgb(235, 255, 171, 72)
        };
    }

    internal static Color SelectionGlowColor(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        pulse = Math.Clamp(pulse, 0, 1);
        if (highlightKind == SelectionHighlightKind.Fill)
        {
            return primary
                ? Color.FromArgb(140 + (int)MathF.Round(55 * pulse), 32, 190, 224)
                : Color.FromArgb(100 + (int)MathF.Round(45 * pulse), 32, 172, 220);
        }

        return primary
            ? Color.FromArgb(145 + (int)MathF.Round(65 * pulse), 255, 145, 44)
            : Color.FromArgb(100 + (int)MathF.Round(50 * pulse), 255, 126, 40);
    }

    internal static Color SelectionOuterGlowColor(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        pulse = Math.Clamp(pulse, 0, 1);
        if (highlightKind == SelectionHighlightKind.Fill)
        {
            return primary
                ? Color.FromArgb(55 + (int)MathF.Round(40 * pulse), 20, 150, 180)
                : Color.FromArgb(38 + (int)MathF.Round(30 * pulse), 20, 140, 175);
        }

        return primary
            ? Color.FromArgb(65 + (int)MathF.Round(50 * pulse), 255, 86, 30)
            : Color.FromArgb(45 + (int)MathF.Round(35 * pulse), 240, 78, 28);
    }

    internal static float SelectionOuterGlowWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 7.5f + 2f * pulse : 5.5f + 1.5f * pulse
            : primary ? 10.5f + 3f * pulse : 7f + 2f * pulse;
    }

    internal static float SelectionGlowWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 4.2f + 1.4f * pulse : 3.2f + 1f * pulse
            : primary ? 5.8f + 2.4f * pulse : 4.2f + 1.6f * pulse;
    }

    internal static float SelectionLineWidth(SelectionHighlightKind highlightKind, bool primary, float pulse)
    {
        return highlightKind == SelectionHighlightKind.Fill
            ? primary ? 1.7f + 0.45f * pulse : 1.15f + 0.35f * pulse
            : primary ? 2.2f + 0.7f * pulse : 1.35f + 0.45f * pulse;
    }

    private void PrepareSelectionHighlightPens(SelectionHighlightKind highlightKind)
    {
        var pulse = SelectionHighlightPulse;
        _selectionOuterGlowPen.Color = SelectionOuterGlowColor(highlightKind, primary: true, pulse);
        _selectionOuterGlowPen.Width = SelectionOuterGlowWidth(highlightKind, primary: true, pulse);
        _selectionGlowPen.Color = SelectionGlowColor(highlightKind, primary: true, pulse);
        _selectionGlowPen.Width = SelectionGlowWidth(highlightKind, primary: true, pulse);
        _selectionPen.Color = SelectionLineColor(highlightKind, primary: true);
        _selectionPen.Width = SelectionLineWidth(highlightKind, primary: true, pulse);

        _multiSelectionOuterGlowPen.Color = SelectionOuterGlowColor(highlightKind, primary: false, pulse);
        _multiSelectionOuterGlowPen.Width = SelectionOuterGlowWidth(highlightKind, primary: false, pulse);
        _multiSelectionGlowPen.Color = SelectionGlowColor(highlightKind, primary: false, pulse);
        _multiSelectionGlowPen.Width = SelectionGlowWidth(highlightKind, primary: false, pulse);
        _multiSelectionPen.Color = SelectionLineColor(highlightKind, primary: false);
        _multiSelectionPen.Width = SelectionLineWidth(highlightKind, primary: false, pulse);
    }

    private void DrawSelectionPolygon(Graphics g, PointF[] points, bool primary, SelectionHighlightKind highlightKind)
    {
        if (points.Length < 3) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        path.CloseFigure();
        DrawSelectionPath(g, path, primary, highlightKind);
    }

    private void DrawSelectionPolyline(Graphics g, PointF[] points, bool primary, SelectionHighlightKind highlightKind)
    {
        if (points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        DrawSelectionPath(g, path, primary, highlightKind);
    }

    private void DrawSelectionDot(Graphics g, PointF point, bool primary, SelectionHighlightKind highlightKind)
    {
        PrepareSelectionHighlightPens(highlightKind);
        var pulseScale = 0.92f + 0.16f * SelectionHighlightPulse;
        var outer = (primary ? 12f : 8f) * pulseScale;
        var glow = (primary ? 7f : 5f) * pulseScale;
        var line = (primary ? 2.5f : 1.5f) * pulseScale;
        using var outerBrush = new SolidBrush(primary ? _selectionOuterGlowPen.Color : _multiSelectionOuterGlowPen.Color);
        using var glowBrush = new SolidBrush(primary ? _selectionGlowPen.Color : _multiSelectionGlowPen.Color);
        using var lineBrush = new SolidBrush(primary ? _selectionPen.Color : _multiSelectionPen.Color);
        g.FillEllipse(outerBrush, point.X - outer * 0.5f, point.Y - outer * 0.5f, outer, outer);
        g.FillEllipse(glowBrush, point.X - glow * 0.5f, point.Y - glow * 0.5f, glow, glow);
        g.FillEllipse(lineBrush, point.X - line * 0.5f, point.Y - line * 0.5f, line, line);
    }

    private void DrawBoundaryHandles(Graphics g, int i)
    {
        foreach (var handle in BoundaryHandles())
        {
            DrawHandle(g, WorldToScreen(GetBoundaryHandleWorldPoint(i, handle)), _handleBrush, 9);
        }
    }

    private void DrawTextAreaOverlay(Graphics graphics, int objectIndex, bool drawHandles)
    {
        var corners = GetTextAreaWorldCorners(objectIndex).Select(WorldToScreen).ToArray();
        if (corners.Length != 4) return;
        graphics.DrawPolygon(_textAreaGlowPen, corners);
        graphics.DrawPolygon(_textAreaPen, corners);
        if (!drawHandles) return;

        foreach (var handle in TextAreaResizeHandles())
        {
            DrawHandle(
                graphics,
                WorldToScreen(GetTextAreaHandleWorldPoint(objectIndex, handle)),
                _textAreaHandleBrush,
                9);
        }
    }

    private void DrawHandle(Graphics g, PointF point, Brush brush, float size)
    {
        var rect = new RectangleF(point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        g.FillRectangle(brush, rect);
        g.DrawRectangle(_handleBorderPen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private (PointF Start, PointF Control1, PointF Control2, PointF End) GetBezierScreenPoints(int i)
    {
        var halfW = Scene.Width[i] * 0.5f;
        var start = WorldToScreen(LocalToWorld(i, new PointF(-halfW, 0)));
        var end = WorldToScreen(LocalToWorld(i, new PointF(halfW, 0)));
        var control1 = WorldToScreen(Scene.CurveControlX[i], Scene.CurveControlY[i]);
        var control2 = WorldToScreen(Scene.CurveControl2X[i], Scene.CurveControl2Y[i]);
        return (start, control1, control2, end);
    }

    private (PointF Start, PointF Control1, PointF Control2, PointF End) GetBezierScreenPoints(DrawingElementHit hit)
    {
        if (TryGetLineBezierWorldPoints(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out var start,
                out var control1,
                out var control2,
                out var end))
        {
            return (WorldToScreen(start), WorldToScreen(control1), WorldToScreen(control2), WorldToScreen(end));
        }

        return GetBezierScreenPoints(hit.Key.ObjectIndex);
    }

    internal bool TryGetLineBezierWorldPoints(
        int objectIndex,
        float startT,
        float endT,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        start = PointF.Empty;
        control1 = PointF.Empty;
        control2 = PointF.Empty;
        end = PointF.Empty;
        return Scene.TryGetLineBezierPart(
            objectIndex,
            startT,
            endT,
            out start,
            out control1,
            out control2,
            out end);
    }

    private static GraphicsPath BuildCubicPath(PointF start, PointF control1, PointF control2, PointF end)
    {
        var path = new GraphicsPath();
        path.AddBezier(start, control1, control2, end);
        return path;
    }

    private static GraphicsPath BuildCubicSamplePath(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        float startT,
        float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = new GraphicsPath();
        var previous = CubicPoint(start, control1, control2, end, startT);
        const int samples = 20;
        for (var i = 1; i <= samples; i++)
        {
            var t = startT + (endT - startT) * i / samples;
            var current = CubicPoint(start, control1, control2, end, t);
            path.AddLine(previous, current);
            previous = current;
        }

        return path;
    }

    private static GraphicsPath BuildPolylineSamplePath(PointF[] points, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = new GraphicsPath();
        var previous = PolylinePointAt(points, startT);
        var segments = Math.Max(1, points.Length - 1);
        var samples = Math.Max(2, (int)Math.Ceiling((endT - startT) * segments * 4));
        for (var i = 1; i <= samples; i++)
        {
            var t = startT + (endT - startT) * i / samples;
            var current = PolylinePointAt(points, t);
            path.AddLine(previous, current);
            previous = current;
        }

        return path;
    }

    private static PointF PolylinePointAt(PointF[] points, float t)
    {
        if (points.Length == 0) return PointF.Empty;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Lerp(points[index], points[index + 1], scaled - index);
    }

    private static PointF CubicPoint(PointF start, PointF control1, PointF control2, PointF end, float t)
    {
        var inv = 1 - t;
        return new PointF(
            inv * inv * inv * start.X
                + 3 * inv * inv * t * control1.X
                + 3 * inv * t * t * control2.X
                + t * t * t * end.X,
            inv * inv * inv * start.Y
                + 3 * inv * inv * t * control1.Y
                + 3 * inv * t * t * control2.Y
                + t * t * t * end.Y);
    }

    private PointF LocalToWorld(int i, PointF local)
    {
        var angle = Scene.Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(
            Scene.X[i] + local.X * cos - local.Y * sin,
            Scene.Y[i] + local.X * sin + local.Y * cos);
    }

    private PointF[] GetBoundaryScreenPolyline(int i)
    {
        var boundary = Scene.GetShapeBoundary(i);
        var points = new PointF[boundary.Length];
        for (var p = 0; p < boundary.Length; p++) points[p] = WorldToScreen(boundary[p]);
        return points;
    }

    private static PointF Lerp(PointF a, PointF b, float t)
    {
        return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static IEnumerable<EditHandleKind> BoundaryHandles()
    {
        yield return EditHandleKind.BoundsTopLeft;
        yield return EditHandleKind.BoundsTopRight;
        yield return EditHandleKind.BoundsBottomRight;
        yield return EditHandleKind.BoundsBottomLeft;
    }

    internal static IEnumerable<EditHandleKind> TextAreaResizeHandles()
    {
        yield return EditHandleKind.TextAreaLeft;
        yield return EditHandleKind.TextAreaRight;
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    private static float Distance(Point screen, PointF point)
    {
        var dx = screen.X - point.X;
        var dy = screen.Y - point.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Distance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private PointF WorldToScreen(PointF point) => WorldToScreen(point.X, point.Y);

    private SolidBrush BrushFor(int argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        if (_brushCache.Count >= MaxGdiBrushCacheEntries)
        {
            foreach (var item in _brushCache.Values) item.Dispose();
            _brushCache.Clear();
        }

        brush = new SolidBrush(Color.FromArgb(argb));
        _brushCache[argb] = brush;
        return brush;
    }
}
