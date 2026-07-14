using System.Diagnostics;
using System.Drawing.Drawing2D;

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
    float WorldGridOpacity);

internal sealed class StageControl : Control
{
    private const int MaxSelectionOutlines = 512;
    private const float EndpointHandleHitRadiusPixels = 14;
    private const float ControlHandleHitRadiusPixels = 16;
    private const float MaxControlHandleHitRadiusPixels = 32;
    private const int BrushColorPaletteSwatchSize = 30;
    private const int BrushColorPaletteRadiusPixels = 58;
    private const int MaxBrushColorPaletteColors = 8;
    private const int MaxGdiBrushCacheEntries = 1024;
    private const double FillAnimationDurationMilliseconds = 420;
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
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly System.Windows.Forms.Timer _fillAnimationTimer = new() { Interval = 16 };
    private readonly Direct2DStageRenderer _direct2DRenderer = new();
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private int _selectedObject = -1;
    private int[] _selectedObjects = Array.Empty<int>();
    private DrawingElementHit[] _selectedElements = Array.Empty<DrawingElementHit>();
    private DrawingElementHit _hoveredLineElement = DrawingElementHit.None;
    private RectangleF _transformBounds = RectangleF.Empty;
    private PointF _transformFocus;
    private RectangleF _drawingObjectSelectionBounds = RectangleF.Empty;
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
    private float _worldGridOpacity = 1f;
    private PointF[][] _fillPreviewContours = Array.Empty<PointF[]>();
    private Color _fillPreviewColor = Color.White;
    private PointF[][] _fillAnimationContours = Array.Empty<PointF[]>();
    private PointF _fillAnimationOrigin;
    private Color _fillAnimationColor = Color.White;
    private float _fillAnimationMaxRadiusWorld;
    private float _fillAnimationProgress = 1f;
    private long _fillAnimationStartedAt;
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
    private Point _brushColorPaletteCenter;
    private Color[] _brushColorPaletteColors = [];
    private int _brushColorPaletteHoveredIndex = -1;
    private bool _paintFailureLogged;

    private readonly record struct SelectedFillOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingFillPartGeometry[] Parts);

    private readonly record struct SelectedPolylineOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingPolylinePartGeometry[] Parts);

    public VectorScene Scene { get; internal set; }
    public VectorScene? UnderlayScene { get; private set; }
    public VectorScene? OnionSkinScene { get; private set; }
    public VectorScene? DragPreviewScene { get; private set; }
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
            Invalidate();
        }
    }

    public IReadOnlyList<int> SelectedObjects => _selectedObjects;
    public IReadOnlyList<DrawingElementHit> SelectedElements => _selectedElements;
    public DrawingElementHit SelectedElement { get; private set; } = DrawingElementHit.None;
    public DrawingElementHit HoveredLineElement => _hoveredLineElement;
    public bool TransformMode { get; private set; }
    public bool TransformBoundsVisible { get; private set; }
    public RectangleF TransformBounds => _transformBounds;
    public PointF TransformFocus => _transformFocus;
    public bool DrawingObjectSelectionVisible { get; private set; }
    public RectangleF DrawingObjectSelectionBounds => _drawingObjectSelectionBounds;
    public bool MarqueeVisible { get; private set; }
    public Point MarqueeStart { get; private set; }
    public Point MarqueeEnd { get; private set; }
    public bool DrawingPreviewVisible { get; private set; }
    public PointF DrawingPreviewStart { get; private set; }
    public PointF DrawingPreviewEnd { get; private set; }
    public PointF DrawingPreviewControl { get; private set; }
    public bool DrawingPreviewHasCurve { get; private set; }
    public ShapeKind DrawingPreviewShape { get; private set; } = ShapeKind.Rectangle;
    public Color DrawingPreviewColor { get; private set; } = Color.White;
    public float DrawingPreviewStroke { get; private set; } = 2;
    public bool FreehandPreviewVisible { get; private set; }
    public IReadOnlyList<PointF> FreehandPreviewPoints { get; private set; } = Array.Empty<PointF>();
    public IReadOnlyList<float> FreehandPreviewDiameters { get; private set; } = Array.Empty<float>();
    public Color FreehandPreviewColor { get; private set; } = Color.White;
    public float FreehandPreviewStroke { get; private set; } = 2;
    public bool BrushTipCursorVisible { get; private set; }
    public Point BrushTipCursorScreen { get; private set; }
    public float BrushTipCursorRadiusPixels { get; private set; }
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
    internal double LastDirect2DCommandMilliseconds => _direct2DRenderer.LastCommandMilliseconds;
    internal double LastDirect2DPresentMilliseconds => _direct2DRenderer.LastPresentMilliseconds;
    internal int LastDirect2DLodBitmapSubmissions => _direct2DRenderer.LastLodBitmapSubmissions;
    internal int LastDirect2DLodBitmapBuilds => _direct2DRenderer.LastLodBitmapBuilds;
    public event EventHandler? FrameRendered;

    internal bool HasCachedDirect2DFreehandGeometry(VectorScene scene)
    {
        return _direct2DRenderer.HasCachedFreehandGeometry(scene);
    }

    internal void ReloadRenderingModuleForHotReload()
    {
        _direct2DRenderer.ReloadRuntimeResources();
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
    }

    public void BindScene(VectorScene scene)
    {
        Scene = scene;
        UnderlayScene = null;
        OnionSkinScene = null;
        DragPreviewScene = null;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        _selectedObject = -1;
        _selectedObjects = Array.Empty<int>();
        ClearDrawingPreview();
        ClearFreehandPreview();
        ClearMarquee();
        ClearFillPreview();
        ClearFillAnimation();
        ClearBrushColorPalette();
        _direct2DRenderer.Resize(ClientSize);
        Invalidate();
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

    public void BindDragPreviewScene(VectorScene? scene)
    {
        DragPreviewScene = scene is { ObjectCount: > 0 } ? scene : null;
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
            WorldGridOpacity);
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
        Invalidate();
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
        Invalidate();
    }

    public void ResetDefaultView() => SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);

    public void SetVisibleWorldWidth(float vectorUnits)
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
        Invalidate();
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
    }

    public void ZoomAt(Point screen, float factor)
    {
        var before = ScreenToWorld(screen);
        Zoom = (float)Math.Clamp(Zoom * factor, 0.02, 64);
        var after = ScreenToWorld(screen);
        CameraX += before.X - after.X;
        CameraY += before.Y - after.Y;
        Invalidate();
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
        if (_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex)
            && (shape != ShapeKind.Line
                || !_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex && hit.Key.Kind == DrawingElementKind.Stroke)))
        {
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

    public void SetTransformOverlay(bool transformMode, RectangleF bounds, PointF? focus = null)
    {
        var visible = transformMode && bounds.Width > 0.001f && bounds.Height > 0.001f;
        var nextFocus = visible
            ? focus ?? new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f)
            : PointF.Empty;
        if (TransformMode == transformMode
            && TransformBoundsVisible == visible
            && _transformBounds == bounds
            && _transformFocus == nextFocus)
        {
            return;
        }

        TransformMode = transformMode;
        TransformBoundsVisible = visible;
        _transformBounds = visible ? bounds : RectangleF.Empty;
        _transformFocus = nextFocus;
        Invalidate();
    }

    public void SetDrawingObjectSelectionOverlay(RectangleF bounds)
    {
        var visible = bounds.Width > 0.001f && bounds.Height > 0.001f;
        if (DrawingObjectSelectionVisible == visible
            && _drawingObjectSelectionBounds == (visible ? bounds : RectangleF.Empty))
        {
            return;
        }

        DrawingObjectSelectionVisible = visible;
        _drawingObjectSelectionBounds = visible ? bounds : RectangleF.Empty;
        Invalidate();
    }

    public TransformHandleKind HitTestTransformHandle(Point screen)
    {
        if (!TransformBoundsVisible) return TransformHandleKind.None;
        var rect = TransformScreenBounds();
        if (Distance(screen, WorldToScreen(_transformFocus)) <= 10) return TransformHandleKind.Focus;
        var points = TransformHandlePoints(rect);
        foreach (var (kind, point) in points)
        {
            if (Distance(screen, point) <= 10) return kind;
        }

        foreach (var (kind, point, _) in CornerRotationHandlePoints(rect))
        {
            if (Distance(screen, point) <= 11) return kind;
        }

        foreach (var (kind, point, _) in EdgeSkewHandlePoints(rect))
        {
            if (Distance(screen, point) <= 10) return kind;
        }

        return rect.Contains(screen) ? TransformHandleKind.Move : TransformHandleKind.None;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
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

        if (rendered) FrameRendered?.Invoke(this, EventArgs.Empty);
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
        if (UnderlayScene is { } underlay)
        {
            var underlayLimit = UsesObjectRenderer(underlay)
                && UsesObjectRenderer(editableScene)
                    ? objectDrawLimit - Math.Min(editableScene.ObjectCount, objectDrawLimit * 3 / 4)
                    : objectDrawLimit;
            underlayStats = DrawSceneGdi(g, underlay, underlayLimit);
        }

        if (OnionSkinScene is { } onionSkin)
        {
            var onionSkinLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects);
            onionSkinStats = DrawSceneGdi(g, onionSkin, onionSkinLimit);
        }

        var editableLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects - onionSkinStats.DrawnObjects);
        var editableStats = DrawSceneGdi(g, editableScene, editableLimit);
        if (DragPreviewScene is { } dragPreview)
        {
            DrawSceneGdi(g, dragPreview, Math.Min(objectDrawLimit, 80_000));
        }
        LastStats = RenderStats.Combine(RenderStats.Combine(underlayStats, onionSkinStats), editableStats);
        DrawSelection(g);
        DrawDrawingPreview(g);
        DrawFreehandPreview(g);
        DrawFillPreview(g);
        DrawFillAnimation(g);
        DrawGradientOverlay(g);
        DrawMarquee(g);
        DrawBrushTipCursor(g);
        DrawBrushColorPalette(g);
        DrawFillToolCursor(g);
    }

    private RenderStats DrawSceneGdi(Graphics graphics, VectorScene scene, int objectDrawLimit)
    {
        var editableScene = Scene;
        Scene = scene;
        try
        {
            var pixelZoom = EffectivePixelZoom();
            return Scene.ObjectCount < 5000
                ? DrawObjects(graphics, objectDrawLimit)
                : pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : pixelZoom < 0.18f
                        ? DrawTiles(graphics)
                        : DrawObjects(graphics, objectDrawLimit);
        }
        finally
        {
            Scene = editableScene;
        }
    }

    private int ObjectDrawLimit() => EffectivePixelZoom() < 0.35f ? 65_000 : 160_000;

    private bool UsesObjectRenderer(VectorScene scene)
    {
        return scene.ObjectCount > 0 && (scene.ObjectCount < 5000 || EffectivePixelZoom() >= 0.18f);
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
        if (_pendingVisibleWorldWidth is { } visibleWorldWidth) SetVisibleWorldWidth(visibleWorldWidth);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
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
            _fillAnimationTimer.Dispose();
            _direct2DRenderer.Dispose();
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
            _handleBorderPen.Dispose();
        }

        base.Dispose(disposing);
    }

    public void SetDrawingPreview(PointF start, PointF end, ShapeKind shape, Color color, float stroke)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewEnd = end;
        DrawingPreviewControl = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        DrawingPreviewHasCurve = false;
        DrawingPreviewShape = shape;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void SetCurveDrawingPreview(PointF start, PointF control, PointF end, Color color, float stroke)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewControl = control;
        DrawingPreviewEnd = end;
        DrawingPreviewHasCurve = true;
        DrawingPreviewShape = ShapeKind.Line;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void ClearDrawingPreview()
    {
        if (!DrawingPreviewVisible) return;
        DrawingPreviewVisible = false;
        DrawingPreviewHasCurve = false;
        Invalidate();
    }

    public void SetFreehandPreview(IReadOnlyList<PointF> points, Color color, float stroke, IReadOnlyList<float>? diameters = null)
    {
        FreehandPreviewVisible = points.Count > 0;
        FreehandPreviewPoints = points;
        FreehandPreviewDiameters = diameters is { Count: > 0 } ? diameters : Array.Empty<float>();
        FreehandPreviewColor = color;
        FreehandPreviewStroke = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), stroke);
        Invalidate();
    }

    public void ClearFreehandPreview()
    {
        if (!FreehandPreviewVisible && FreehandPreviewPoints.Count == 0) return;
        FreehandPreviewVisible = false;
        FreehandPreviewPoints = Array.Empty<PointF>();
        FreehandPreviewDiameters = Array.Empty<float>();
        Invalidate();
    }

    public void SetBrushTipCursor(Point screen, BrushShape shape, float diameter, bool eraser)
    {
        var radius = Math.Max(2, WorldLengthToScreen(diameter) * 0.5f);
        if (BrushTipCursorVisible
            && BrushTipCursorScreen == screen
            && ReferenceEquals(BrushTipCursorShape, shape)
            && Math.Abs(BrushTipCursorRadiusPixels - radius) < 0.01f
            && BrushTipCursorIsEraser == eraser)
        {
            return;
        }

        BrushTipCursorVisible = true;
        BrushTipCursorScreen = screen;
        BrushTipCursorShape = shape;
        BrushTipCursorRadiusPixels = radius;
        BrushTipCursorIsEraser = eraser;
        Invalidate();
    }

    public void ClearBrushTipCursor()
    {
        if (!BrushTipCursorVisible) return;
        BrushTipCursorVisible = false;
        BrushTipCursorShape = null;
        Invalidate();
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
        Invalidate();
    }

    public void ClearGradientOverlay()
    {
        if (!_gradientOverlayVisible) return;
        _gradientOverlayVisible = false;
        Invalidate();
    }

    public GradientOverlayHit HitTestGradientOverlay(Point screen)
    {
        if (!_gradientOverlayVisible) return GradientOverlayHit.None;
        var start = WorldToScreen(_gradientOverlayStart);
        var end = WorldToScreen(_gradientOverlayEnd);
        const float radiusSquared = 10 * 10;
        var startDistance = SquaredDistance(screen, start);
        var endDistance = SquaredDistance(screen, end);
        if (startDistance <= radiusSquared || endDistance <= radiusSquared)
        {
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
        Invalidate();
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
        Invalidate();
    }

    public void SetHoveredLineElement(DrawingElementHit hit)
    {
        if (!IsValidLineStrokeHit(hit)) hit = DrawingElementHit.None;
        if (_hoveredLineElement == hit) return;
        _hoveredLineElement = hit;
        Invalidate();
    }

    public void ClearHoveredLineElement() => SetHoveredLineElement(DrawingElementHit.None);

    public EditHandleKind HitTestHoveredLineHandle(Point screen)
    {
        return IsValidLineStrokeHit(_hoveredLineElement)
            ? HitTestLineBezierHandle(screen, _hoveredLineElement)
            : EditHandleKind.None;
    }

    public EditHandleKind HitTestLineElementHandle(Point screen, DrawingElementHit hit)
    {
        return IsValidLineStrokeHit(hit)
            ? HitTestLineBezierHandle(screen, hit)
            : EditHandleKind.None;
    }

    private bool IsValidLineStrokeHit(DrawingElementHit hit)
    {
        return hit.IsValid
            && hit.Key.Kind == DrawingElementKind.Stroke
            && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
            && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
            && Scene.IsObjectActive(hit.Key.ObjectIndex, Frame);
    }

    private EditHandleKind HitTestLineBezierHandle(Point screen, DrawingElementHit hit)
    {
        if (!TryGetLineBezierWorldPoints(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out var start,
                out var control,
                out var end))
        {
            return EditHandleKind.None;
        }

        if (Distance(screen, WorldToScreen(start)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineStart;
        if (Distance(screen, WorldToScreen(end)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineEnd;
        var controlReach = Math.Max(
            Distance(WorldToScreen(start), WorldToScreen(control)),
            Distance(WorldToScreen(end), WorldToScreen(control)));
        var controlHitRadius = Math.Clamp(
            ControlHandleHitRadiusPixels + MathF.Sqrt(controlReach) * 0.35f,
            ControlHandleHitRadiusPixels,
            MaxControlHandleHitRadiusPixels);
        return Distance(screen, WorldToScreen(control)) <= controlHitRadius
            ? EditHandleKind.BezierControl
            : EditHandleKind.None;
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
        MarqueeVisible = true;
        MarqueeStart = start;
        MarqueeEnd = end;
        Invalidate();
    }

    public void ClearMarquee()
    {
        if (!MarqueeVisible) return;
        MarqueeVisible = false;
        Invalidate();
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

        var drawn = _renderOrder.Draw(
            scene,
            drawLimit,
            index => DrawObject(g, index, SceneRenderPass.Fill),
            index => DrawObject(g, index, SceneRenderPass.Stroke));
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private void DrawObject(Graphics g, int i, SceneRenderPass pass)
    {
        var scene = Scene;
        var screen = WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, WorldLengthToScreen(scene.Height[i]));
        var rect = new RectangleF(screen.X - w * 0.5f, screen.Y - h * 0.5f, w, h);
        var brush = BrushFor(scene.Argb[i]);
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        var strokeColor = StrokeColorFor(i);
        var screenStroke = Math.Max(0.1f, WorldLengthToScreen(scene.Stroke[i]));

        if (pass == SceneRenderPass.Fill && shape != ShapeKind.Line && scene.HasGradient(i))
        {
            using var gradient = CreateGradientFillBrush(scene, i);
            DrawGradientFill(g, scene, i, gradient);
            return;
        }

        if (shape == ShapeKind.Path && scene.TryGetPathWorldContours(i, out var pathContours))
        {
            DrawPathObject(g, i, pathContours, brush, strokeColor, scene.Stroke[i], screenStroke, pass);
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
            DrawLocalShape(g, shape, brush, strokeColor, scene.Stroke[i], screenStroke, w, h, pass);
            g.Restore(state);
            return;
        }

        if (pass == SceneRenderPass.Fill)
        {
            g.FillRectangle(brush, rect);
        }
        else if (scene.Stroke[i] > 0 && w > 4 && h > 4)
        {
            using var pen = StrokePen(strokeColor, screenStroke);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void DrawPathObject(Graphics g, int i, PointF[][] worldContours, Brush brush, Color strokeColor, float stroke, float screenStroke, SceneRenderPass pass)
    {
        if (worldContours.Length == 0) return;
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in worldContours)
        {
            if (contour.Length < 3) continue;
            var points = new PointF[contour.Length];
            for (var p = 0; p < contour.Length; p++) points[p] = WorldToScreen(contour[p]);
            path.AddPolygon(points);
        }

        if (path.PointCount == 0) return;
        if (pass == SceneRenderPass.Fill)
        {
            g.FillPath(brush, path);
            return;
        }

        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        g.DrawPath(pen, path);
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

    private void DrawGradientFill(Graphics g, VectorScene scene, int objectIndex, Brush brush)
    {
        using var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in scene.GetObjectBoundaryContours(objectIndex))
        {
            if (contour.Length < 3) continue;
            path.AddPolygon(contour.Select(WorldToScreen).ToArray());
        }

        if (path.PointCount == 0) return;
        if (scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex);
            return;
        }

        g.FillPath(brush, path);
    }

    private void DrawRadialGradientFill(Graphics g, GraphicsPath path, VectorScene scene, int objectIndex)
    {
        var stops = scene.GetGradientStops(objectIndex);
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

    private void DrawLocalShape(Graphics g, ShapeKind shape, Brush brush, Color strokeColor, float stroke, float screenStroke, float w, float h, SceneRenderPass pass)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                if (pass == SceneRenderPass.Fill)
                {
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
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(6, w, h, -MathF.PI / 2), pass);
                break;
            case ShapeKind.Star:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, StarPoints(5, w, h, -MathF.PI / 2), pass);
                break;
            default:
                if (pass == SceneRenderPass.Fill)
                {
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

    private void DrawGrid(Graphics g)
    {
        if (_worldGridOpacity <= 0.001f) return;
        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            Draw3DReferenceGrid(g);
            return;
        }

        var step = Math.Max(24, WorldLengthToScreen(512));
        var origin = WorldToScreen(0, 0);
        _gridPen.Color = Color.FromArgb(GridAlpha(150), 58, 64, 69);
        for (var x = origin.X % step; x < Width; x += step) g.DrawLine(_gridPen, x, 0, x, Height);
        for (var y = origin.Y % step; y < Height; y += step) g.DrawLine(_gridPen, 0, y, Width, y);
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
            && !IsValidLineStrokeHit(_hoveredLineElement)
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
                && Scene.IsObjectActive(SelectedElement.Key.ObjectIndex, Frame))
            {
                DrawElementSelectionOutline(g, SelectedElement, primary: true);
                if (SelectedElement.Key.Kind == DrawingElementKind.Stroke
                    && Scene.ShapeKind[SelectedElement.Key.ObjectIndex] == ShapeKind.Line)
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
            if (!Scene.IsObjectActive(index, Frame)) continue;
            DrawSelectionOutline(g, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = SelectedObject;
        if (primary >= 0 && primary < Scene.ObjectCount && Scene.IsObjectActive(primary, Frame))
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

        if (shape == ShapeKind.Line)
        {
            if (primary && !TransformMode) DrawBezierGuides(g, i);
            else DrawBezierOutline(g, i, primary);
        }
        else if (IsFreehandShape(shape))
        {
            var points = GetBoundaryScreenPolyline(i);
            if (points.Length == 1)
            {
                DrawSelectionDot(g, points[0], primary);
            }
            else if (points.Length > 1)
            {
                DrawSelectionPolyline(g, points, primary);
            }
        }
        else
        {
            if (shape == ShapeKind.Path && Scene.TryGetPathWorldContours(i, out var contours))
            {
                foreach (var contour in contours)
                {
                    var points = contour.Select(WorldToScreen).ToArray();
                    if (points.Length >= 3) DrawSelectionPolygon(g, points, primary);
                }
            }
            else
            {
                DrawBoundaryOutline(g, i, primary);
            }

            if (primary && shape != ShapeKind.Path && !TransformMode) DrawBoundaryHandles(g, i);
        }
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
            if (points.Length == 1) DrawSelectionDot(g, points[0], primary);
            else if (points.Length > 1) DrawSelectionPolyline(g, points, primary);
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            foreach (var contour in GetSelectedFillPartContours(hit))
            {
                var points = contour.Select(WorldToScreen).ToArray();
                if (points.Length >= 3) DrawSelectionPolygon(g, points, primary);
            }

            return;
        }

        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var points = GetSelectedBoundaryPartPoints(hit).Select(WorldToScreen).ToArray();
            if (points.Length > 1) DrawSelectionPolyline(g, points, primary);
        }
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
                var control = WorldToScreen(DrawingPreviewControl);
                using var path = BuildQuadraticPath(a, control, b);
                g.DrawLine(_previewGuidePen, a, control);
                g.DrawLine(_previewGuidePen, control, b);
                g.DrawPath(stroke, path);
                DrawHandle(g, a, _handleBrush, 7);
                DrawHandle(g, b, _handleBrush, 7);
                DrawHandle(g, control, _bezierHandleBrush, 9);
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
        DrawPreviewLocalShape(g, DrawingPreviewShape, fill, stroke, w, h);
        g.Restore(state);

        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(boundsPen, center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        g.SmoothingMode = oldMode;
    }

    private void DrawFreehandPreview(Graphics g)
    {
        if (!FreehandPreviewVisible || FreehandPreviewPoints.Count == 0) return;
        var screenWidth = Math.Max(0.75f, WorldLengthToScreen(FreehandPreviewStroke));
        var variableWidth = FreehandPreviewDiameters.Count == FreehandPreviewPoints.Count;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
        g.FillEllipse(endFill, end.X - 6, end.Y - 6, 12, 12);
        g.DrawEllipse(outline, end.X - 6, end.Y - 6, 12, 12);
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

    private void DrawMarquee(Graphics g)
    {
        if (!MarqueeVisible) return;
        var rect = Rectangle.FromLTRB(
            Math.Min(MarqueeStart.X, MarqueeEnd.X),
            Math.Min(MarqueeStart.Y, MarqueeEnd.Y),
            Math.Max(MarqueeStart.X, MarqueeEnd.X),
            Math.Max(MarqueeStart.Y, MarqueeEnd.Y));

        if (rect.Width < 2 || rect.Height < 2) return;
        g.FillRectangle(_marqueeBrush, rect);
        g.DrawRectangle(_marqueePen, rect);
    }

    private void DrawPreviewLocalShape(Graphics g, ShapeKind shape, Brush fill, Pen stroke, float w, float h)
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
                DrawPreviewPolygon(g, fill, stroke, RegularPolygonPoints(6, w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Star:
                DrawPreviewPolygon(g, fill, stroke, StarPoints(5, w, h, -MathF.PI / 2));
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
        var (start, control, end) = GetBezierScreenPoints(i);
        using var path = BuildQuadraticPath(start, control, end);
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
        g.DrawPath(linePen, path);
        if (brush is SolidBrush solid)
        {
            DrawMiterJoin(g, i, startEndpoint: true, screenStroke, solid.Color);
            DrawMiterJoin(g, i, startEndpoint: false, screenStroke, solid.Color);
        }
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
        var (start, control, end) = GetBezierScreenPoints(objectIndex);
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
            var a = QuadraticPoint(start, control, end, startT);
            var b = QuadraticPoint(start, control, end, endT);
            var midpoint = QuadraticPoint(start, control, end, (startT + endT) * 0.5f);
            var position = Math.Clamp(Distance(midpoint, center) / radius, 0f, 1f);
            using var pen = new Pen(GradientColorAt(stops, position), screenStroke)
            {
                StartCap = segment == 0 ? LineCapForEndpoint(startStyle) : LineCap.Round,
                EndCap = segment == segments - 1 ? LineCapForEndpoint(endStyle) : LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLine(pen, a, b);
        }

        g.SmoothingMode = oldMode;
    }

    private static LineCap LineCapForEndpoint(LineEndpointStyle endpointStyle)
    {
        return endpointStyle == LineEndpointStyle.Sharp ? LineCap.Flat : LineCap.Round;
    }

    private void DrawMiterJoin(Graphics g, int objectIndex, bool startEndpoint, float screenStroke, Color color)
    {
        if (Scene.GetLineEndpointStyle(objectIndex, startEndpoint) != LineEndpointStyle.Sharp
            || !Scene.TryGetLineJoinNeighbor(objectIndex, startEndpoint, Frame, out var neighbor, out var neighborStart)
            || Scene.GetLineEndpointStyle(neighbor, neighborStart) != LineEndpointStyle.Sharp
            || objectIndex > neighbor)
        {
            return;
        }

        var current = GetBezierScreenPoints(objectIndex);
        var adjacent = GetBezierScreenPoints(neighbor);
        var joint = startEndpoint ? current.Start : current.End;
        var currentInterior = EndpointInteriorPoint(current, startEndpoint);
        var adjacentInterior = EndpointInteriorPoint(adjacent, neighborStart);
        if (!LineJoinGeometry.TryCreateMiter(
                joint,
                currentInterior,
                adjacentInterior,
                screenStroke * 0.5f,
                out var miter))
        {
            return;
        }

        using var fill = new SolidBrush(color);
        g.FillPolygon(fill, [joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
        g.FillPolygon(fill, [joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
    }

    private static PointF EndpointInteriorPoint((PointF Start, PointF Control, PointF End) curve, bool startEndpoint)
    {
        var endpoint = startEndpoint ? curve.Start : curve.End;
        var control = curve.Control;
        var controlDistanceSquared = (control.X - endpoint.X) * (control.X - endpoint.X)
            + (control.Y - endpoint.Y) * (control.Y - endpoint.Y);
        if (controlDistanceSquared > 0.01f) return control;
        return startEndpoint ? curve.End : curve.Start;
    }

    private void DrawBezierGuides(Graphics g, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var path = BuildQuadraticPath(start, control, end);
        DrawSelectionPath(g, path, primary: true);
        DrawBezierHandles(g, start, control, end);
    }

    private void DrawBezierHandles(Graphics g, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        DrawBezierHandles(g, start, control, end);
    }

    private void DrawBezierHandles(Graphics g, DrawingElementHit hit)
    {
        var (start, control, end) = GetBezierScreenPoints(hit);
        DrawBezierHandles(g, start, control, end);
    }

    private void DrawHoveredLineControls(Graphics g)
    {
        var hit = _hoveredLineElement;
        if (!IsValidLineStrokeHit(hit)
            || (SelectedElement.IsValid && SelectedElement.Key == hit.Key)
            || (!SelectedElement.IsValid && SelectedObject == hit.Key.ObjectIndex))
        {
            return;
        }

        var (start, control, end) = GetBezierScreenPoints(hit);
        using var guide = new Pen(Color.FromArgb(120, 112, 204, 255), 1);
        using var endpoint = new SolidBrush(Color.FromArgb(210, 255, 240, 168));
        using var controlBrush = new SolidBrush(Color.FromArgb(210, 112, 204, 255));
        g.DrawLine(guide, start, control);
        g.DrawLine(guide, control, end);
        DrawHandle(g, start, endpoint, 7);
        DrawHandle(g, end, endpoint, 7);
        DrawHandle(g, control, controlBrush, 9);
    }

    private void DrawBezierHandles(Graphics g, PointF start, PointF control, PointF end)
    {
        g.DrawLine(_guidePen, start, control);
        g.DrawLine(_guidePen, control, end);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control, _bezierHandleBrush, 10);
    }

    private void DrawTransformOverlay(Graphics g)
    {
        if (!TransformBoundsVisible) return;
        var rect = TransformScreenBounds();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var boundsPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
        using var rotationPen = new Pen(Color.FromArgb(235, 112, 204, 255), 1);
        using var focusPen = new Pen(Color.FromArgb(245, 104, 255, 188), 1.5f);
        using var focusBrush = new SolidBrush(Color.FromArgb(220, 30, 82, 69));
        g.DrawRectangle(boundsPen, rect.X, rect.Y, rect.Width, rect.Height);
        foreach (var (_, point) in TransformHandlePoints(rect)) DrawHandle(g, point, _handleBrush, 8);
        foreach (var (_, point, corner) in CornerRotationHandlePoints(rect))
        {
            g.DrawLine(rotationPen, corner, point);
            g.FillEllipse(_bezierHandleBrush, point.X - 4, point.Y - 4, 8, 8);
            g.DrawEllipse(_handleBorderPen, point.X - 4, point.Y - 4, 8, 8);
        }
        foreach (var (_, point, edge) in EdgeSkewHandlePoints(rect))
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
    }

    private RectangleF TransformScreenBounds()
    {
        return WorldToScreenBounds(_transformBounds);
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

    private static (TransformHandleKind Kind, PointF Point)[] TransformHandlePoints(RectangleF rect)
    {
        var centerX = rect.Left + rect.Width * 0.5f;
        var centerY = rect.Top + rect.Height * 0.5f;
        return
        [
            (TransformHandleKind.TopLeft, new PointF(rect.Left, rect.Top)),
            (TransformHandleKind.Top, new PointF(centerX, rect.Top)),
            (TransformHandleKind.TopRight, new PointF(rect.Right, rect.Top)),
            (TransformHandleKind.Right, new PointF(rect.Right, centerY)),
            (TransformHandleKind.BottomRight, new PointF(rect.Right, rect.Bottom)),
            (TransformHandleKind.Bottom, new PointF(centerX, rect.Bottom)),
            (TransformHandleKind.BottomLeft, new PointF(rect.Left, rect.Bottom)),
            (TransformHandleKind.Left, new PointF(rect.Left, centerY))
        ];
    }

    private static (TransformHandleKind Kind, PointF Point, PointF Corner)[] CornerRotationHandlePoints(RectangleF rect)
    {
        const float offset = 19;
        return
        [
            (TransformHandleKind.RotateTopLeft, new PointF(rect.Left - offset, rect.Top - offset), new PointF(rect.Left, rect.Top)),
            (TransformHandleKind.RotateTopRight, new PointF(rect.Right + offset, rect.Top - offset), new PointF(rect.Right, rect.Top)),
            (TransformHandleKind.RotateBottomRight, new PointF(rect.Right + offset, rect.Bottom + offset), new PointF(rect.Right, rect.Bottom)),
            (TransformHandleKind.RotateBottomLeft, new PointF(rect.Left - offset, rect.Bottom + offset), new PointF(rect.Left, rect.Bottom))
        ];
    }

    private static (TransformHandleKind Kind, PointF Point, PointF Edge)[] EdgeSkewHandlePoints(RectangleF rect)
    {
        const float offset = 19;
        var centerX = rect.Left + rect.Width * 0.5f;
        var centerY = rect.Top + rect.Height * 0.5f;
        return
        [
            (TransformHandleKind.SkewTop, new PointF(centerX, rect.Top - offset), new PointF(centerX, rect.Top)),
            (TransformHandleKind.SkewRight, new PointF(rect.Right + offset, centerY), new PointF(rect.Right, centerY)),
            (TransformHandleKind.SkewBottom, new PointF(centerX, rect.Bottom + offset), new PointF(centerX, rect.Bottom)),
            (TransformHandleKind.SkewLeft, new PointF(rect.Left - offset, centerY), new PointF(rect.Left, centerY))
        ];
    }

    private void DrawBezierGuides(Graphics g, int i, float startT, float endT, bool primary)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var partialPath = BuildQuadraticSamplePath(start, control, end, startT, endT);
        DrawSelectionPath(g, partialPath, primary);
    }

    private void DrawBezierSelectionContext(Graphics g, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var fullPath = BuildQuadraticPath(start, control, end);
        using var mutedPen = new Pen(Color.FromArgb(80, _selectionPen.Color), 1.2f);
        g.DrawPath(mutedPen, fullPath);
    }

    private void DrawBezierOutline(Graphics g, int i, bool primary)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var path = BuildQuadraticPath(start, control, end);
        DrawSelectionPath(g, path, primary);
    }

    private void DrawBoundaryOutline(Graphics g, int i, bool primary)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;
        DrawSelectionPolygon(g, points, primary);
    }

    private void DrawBoundaryPartialOutline(Graphics g, int i, float startT, float endT)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;

        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        using var mutedPen = new Pen(Color.FromArgb(80, _selectionPen.Color), 1.2f);
        g.DrawPath(mutedPen, fullPath);
        DrawSelectionPath(g, partialPath, primary: true);
    }

    private void DrawSelectionPath(Graphics g, GraphicsPath path, bool primary)
    {
        g.DrawPath(primary ? _selectionOuterGlowPen : _multiSelectionOuterGlowPen, path);
        g.DrawPath(primary ? _selectionGlowPen : _multiSelectionGlowPen, path);
        g.DrawPath(primary ? _selectionPen : _multiSelectionPen, path);
    }

    private void DrawSelectionPolygon(Graphics g, PointF[] points, bool primary)
    {
        if (points.Length < 3) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        path.CloseFigure();
        DrawSelectionPath(g, path, primary);
    }

    private void DrawSelectionPolyline(Graphics g, PointF[] points, bool primary)
    {
        if (points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddLines(points);
        DrawSelectionPath(g, path, primary);
    }

    private void DrawSelectionDot(Graphics g, PointF point, bool primary)
    {
        var outer = primary ? 12f : 8f;
        var glow = primary ? 7f : 5f;
        var line = primary ? 2.5f : 1.5f;
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

    private void DrawHandle(Graphics g, PointF point, Brush brush, float size)
    {
        var rect = new RectangleF(point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        g.FillRectangle(brush, rect);
        g.DrawRectangle(_handleBorderPen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private (PointF Start, PointF Control, PointF End) GetBezierScreenPoints(int i)
    {
        var halfW = Scene.Width[i] * 0.5f;
        var start = WorldToScreen(LocalToWorld(i, new PointF(-halfW, 0)));
        var end = WorldToScreen(LocalToWorld(i, new PointF(halfW, 0)));
        var control = WorldToScreen(Scene.CurveControlX[i], Scene.CurveControlY[i]);
        return (start, control, end);
    }

    private (PointF Start, PointF Control, PointF End) GetBezierScreenPoints(DrawingElementHit hit)
    {
        if (TryGetLineBezierWorldPoints(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out var start,
                out var control,
                out var end))
        {
            return (WorldToScreen(start), WorldToScreen(control), WorldToScreen(end));
        }

        return GetBezierScreenPoints(hit.Key.ObjectIndex);
    }

    internal bool TryGetLineBezierWorldPoints(
        int objectIndex,
        float startT,
        float endT,
        out PointF start,
        out PointF control,
        out PointF end)
    {
        start = PointF.Empty;
        control = PointF.Empty;
        end = PointF.Empty;
        if ((uint)objectIndex >= Scene.ObjectCount
            || Scene.ShapeKind[objectIndex] != ShapeKind.Line
            || !Scene.TryGetLineEndpoint(objectIndex, startEndpoint: true, out var fullStart)
            || !Scene.TryGetLineEndpoint(objectIndex, startEndpoint: false, out var fullEnd))
        {
            return false;
        }

        var fullControl = new PointF(Scene.CurveControlX[objectIndex], Scene.CurveControlY[objectIndex]);
        (start, control, end) = QuadraticSubcurve(fullStart, fullControl, fullEnd, startT, endT);
        return true;
    }

    private static GraphicsPath BuildQuadraticPath(PointF start, PointF control, PointF end)
    {
        var c1 = new PointF(start.X + (control.X - start.X) * 2f / 3f, start.Y + (control.Y - start.Y) * 2f / 3f);
        var c2 = new PointF(end.X + (control.X - end.X) * 2f / 3f, end.Y + (control.Y - end.Y) * 2f / 3f);
        var path = new GraphicsPath();
        path.AddBezier(start, c1, c2, end);
        return path;
    }

    private static GraphicsPath BuildQuadraticSamplePath(PointF start, PointF control, PointF end, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = new GraphicsPath();
        var previous = QuadraticPoint(start, control, end, startT);
        const int samples = 20;
        for (var i = 1; i <= samples; i++)
        {
            var t = startT + (endT - startT) * i / samples;
            var current = QuadraticPoint(start, control, end, t);
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

    private static PointF QuadraticPoint(PointF start, PointF control, PointF end, float t)
    {
        var inv = 1 - t;
        return new PointF(
            inv * inv * start.X + 2 * inv * t * control.X + t * t * end.X,
            inv * inv * start.Y + 2 * inv * t * control.Y + t * t * end.Y);
    }

    private static (PointF Start, PointF Control, PointF End) QuadraticSubcurve(
        PointF start,
        PointF control,
        PointF end,
        float startT,
        float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var subStart = QuadraticPoint(start, control, end, startT);
        if (endT - startT <= 0.000001f) return (subStart, subStart, subStart);

        var startToControl = Lerp(start, control, startT);
        var controlToEnd = Lerp(control, end, startT);
        var span = endT - startT;
        var subControl = new PointF(
            subStart.X + (controlToEnd.X - startToControl.X) * span,
            subStart.Y + (controlToEnd.Y - startToControl.Y) * span);
        var subEnd = QuadraticPoint(start, control, end, endT);
        return (subStart, subControl, subEnd);
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
