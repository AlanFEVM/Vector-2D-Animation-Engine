using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class StageControl : Control
{
    private const int MaxSelectionOutlines = 512;
    private readonly Dictionary<int, SolidBrush> _brushCache = new(512);
    private readonly Pen _gridPen = new(Color.FromArgb(35, 58, 64, 69));
    private readonly Pen _strokePen = new(Color.FromArgb(210, 10, 12, 14));
    private readonly Pen _selectionPen = SelectionPen(Color.FromArgb(255, 255, 235, 120), 2.5f);
    private readonly Pen _selectionGlowPen = SelectionPen(Color.FromArgb(190, 32, 172, 255), 7);
    private readonly Pen _selectionOuterGlowPen = SelectionPen(Color.FromArgb(95, 80, 210, 255), 12);
    private readonly Pen _multiSelectionPen = SelectionPen(Color.FromArgb(235, 112, 220, 255), 1.5f);
    private readonly Pen _multiSelectionGlowPen = SelectionPen(Color.FromArgb(135, 32, 172, 255), 5);
    private readonly Pen _multiSelectionOuterGlowPen = SelectionPen(Color.FromArgb(70, 80, 210, 255), 8);
    private readonly Pen _guidePen = new(Color.FromArgb(190, 112, 204, 255), 1);
    private readonly Pen _previewGuidePen = new(Color.FromArgb(170, 255, 255, 255), 1);
    private readonly Pen _marqueePen = new(Color.FromArgb(230, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
    private readonly SolidBrush _marqueeBrush = new(Color.FromArgb(34, 112, 204, 255));
    private readonly SolidBrush _handleBrush = new(Color.FromArgb(255, 255, 240, 168));
    private readonly SolidBrush _bezierHandleBrush = new(Color.FromArgb(255, 112, 204, 255));
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly Direct2DStageRenderer _direct2DRenderer = new();
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private int _selectedObject = -1;
    private int[] _selectedObjects = Array.Empty<int>();
    private DrawingElementHit[] _selectedElements = Array.Empty<DrawingElementHit>();
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
    private bool _paintFailureLogged;

    private readonly record struct SelectedFillOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingFillPartGeometry[] Parts);

    private readonly record struct SelectedPolylineOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingPolylinePartGeometry[] Parts);

    public VectorScene Scene { get; private set; }
    public VectorScene? UnderlayScene { get; private set; }
    public SceneDimension ReferenceDimension { get; private set; } = SceneDimension.TwoD;
    public CameraProjection ReferenceProjection { get; private set; } = CameraProjection.Orthographic;
    public float ReferenceYaw => _referenceYaw;
    public float ReferencePitch => _referencePitch;
    public float ReferenceDistance => _referenceDistance;
    public float ReferenceZoomScale => _referenceZoomScale;
    public float ReferenceTargetX => _referenceTargetX;
    public float ReferenceTargetY => _referenceTargetY;
    public float ReferenceTargetZ => _referenceTargetZ;
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
    public bool MarqueeVisible { get; private set; }
    public Point MarqueeStart { get; private set; }
    public Point MarqueeEnd { get; private set; }
    public bool DrawingPreviewVisible { get; private set; }
    public PointF DrawingPreviewStart { get; private set; }
    public PointF DrawingPreviewEnd { get; private set; }
    public ShapeKind DrawingPreviewShape { get; private set; } = ShapeKind.Rectangle;
    public Color DrawingPreviewColor { get; private set; } = Color.White;
    public float DrawingPreviewStroke { get; private set; } = 2;
    public bool FreehandPreviewVisible { get; private set; }
    public IReadOnlyList<PointF> FreehandPreviewPoints { get; private set; } = Array.Empty<PointF>();
    public Color FreehandPreviewColor { get; private set; } = Color.White;
    public float FreehandPreviewStroke { get; private set; } = 2;
    public float CameraX { get; private set; }
    public float CameraY { get; private set; }
    public float Zoom { get; private set; } = 1;
    public RenderStats LastStats { get; private set; }

    public StageControl(VectorScene scene)
    {
        Scene = scene;
        DoubleBuffered = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.Opaque
            | ControlStyles.ResizeRedraw
            | ControlStyles.StandardClick
            | ControlStyles.StandardDoubleClick,
            true);
        BackColor = Color.FromArgb(17, 19, 21);
    }

    public void BindScene(VectorScene scene)
    {
        Scene = scene;
        UnderlayScene = null;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        _selectedObject = -1;
        _selectedObjects = Array.Empty<int>();
        ClearDrawingPreview();
        ClearFreehandPreview();
        ClearMarquee();
        _direct2DRenderer.Resize(ClientSize);
        Invalidate();
    }

    public void BindUnderlayScene(VectorScene? scene)
    {
        UnderlayScene = scene is { ObjectCount: > 0 } ? scene : null;
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
        if (_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex)) return EditHandleKind.None;

        var shape = Scene.ShapeKind.Length > objectIndex ? Scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        if (IsFreehandShape(shape) || shape == ShapeKind.Path) return EditHandleKind.None;
        if (shape == ShapeKind.Line)
        {
            if (Scene.TryGetLineEndpoint(objectIndex, startEndpoint: true, out var start) && Distance(screen, WorldToScreen(start)) <= 12) return EditHandleKind.LineStart;
            if (Scene.TryGetLineEndpoint(objectIndex, startEndpoint: false, out var end) && Distance(screen, WorldToScreen(end)) <= 12) return EditHandleKind.LineEnd;
            var control = WorldToScreen(Scene.CurveControlX[objectIndex], Scene.CurveControlY[objectIndex]);
            if (Distance(screen, control) <= 12) return EditHandleKind.BezierControl;
            return EditHandleKind.None;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            if (UnderlayScene is null && _direct2DRenderer.TryRender(this, out var stats))
            {
                LastStats = stats;
                _paintFailureLogged = false;
                return;
            }

            DrawBufferedGdi(e.Graphics);
            _paintFailureLogged = false;
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

        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            LastStats = default;
            DrawMarquee(g);
            return;
        }

        var editableScene = Scene;
        var underlayStats = UnderlayScene is { } underlay
            ? DrawSceneGdi(g, underlay)
            : default;
        var editableStats = DrawSceneGdi(g, editableScene);
        LastStats = new RenderStats(
            underlayStats.VisibleObjects + editableStats.VisibleObjects,
            underlayStats.DrawnObjects + editableStats.DrawnObjects,
            underlayStats.VisibleAtoms + editableStats.VisibleAtoms,
            underlayStats.TileDraws + editableStats.TileDraws,
            underlayStats.ScannedObjects + editableStats.ScannedObjects,
            underlayStats.TileLod || editableStats.TileLod);
        DrawSelection(g);
        DrawDrawingPreview(g);
        DrawFreehandPreview(g);
        DrawMarquee(g);
    }

    private RenderStats DrawSceneGdi(Graphics graphics, VectorScene scene)
    {
        var editableScene = Scene;
        Scene = scene;
        try
        {
            return Scene.ObjectCount < 5000
                ? DrawObjects(graphics)
                : Zoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : Zoom < 0.18f
                        ? DrawTiles(graphics)
                        : DrawObjects(graphics);
        }
        finally
        {
            Scene = editableScene;
        }
    }

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
        DrawingPreviewShape = shape;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        Invalidate();
    }

    public void ClearDrawingPreview()
    {
        if (!DrawingPreviewVisible) return;
        DrawingPreviewVisible = false;
        Invalidate();
    }

    public void SetFreehandPreview(IReadOnlyList<PointF> points, Color color, float stroke)
    {
        FreehandPreviewVisible = points.Count > 0;
        FreehandPreviewPoints = points;
        FreehandPreviewColor = color;
        FreehandPreviewStroke = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), stroke);
        Invalidate();
    }

    public void ClearFreehandPreview()
    {
        if (!FreehandPreviewVisible && FreehandPreviewPoints.Count == 0) return;
        FreehandPreviewVisible = false;
        FreehandPreviewPoints = Array.Empty<PointF>();
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

        return new RenderStats(draws, draws, atoms, draws, draws, true);
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

        return new RenderStats(draws, draws, atoms, draws, draws, true);
    }

    private RenderStats DrawObjects(Graphics g)
    {
        var scene = Scene;
        var bounds = VisibleWorldBounds();
        _renderOrder.Collect(scene, bounds, Frame);
        var drawLimit = Zoom < 0.35f ? 65_000 : 160_000;

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
            if (scene.Stroke[i] > 0) DrawBezierLine(g, i, BrushFor(strokeColor.ToArgb()), screenStroke);
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
        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            Draw3DReferenceGrid(g);
            return;
        }

        var step = Math.Max(24, WorldLengthToScreen(512));
        var origin = WorldToScreen(0, 0);
        for (var x = origin.X % step; x < Width; x += step) g.DrawLine(_gridPen, x, 0, x, Height);
        for (var y = origin.Y % step; y < Height; y += step) g.DrawLine(_gridPen, 0, y, Width, y);
    }

    private void Draw3DReferenceGrid(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var horizonPen = new Pen(Color.FromArgb(40, 112, 204, 255), 1);
        using var gridPen = new Pen(Color.FromArgb(70, 72, 84, 92), 1);
        using var centerPen = new Pen(Color.FromArgb(110, 150, 164, 174), 1.3f);
        using var xPen = new Pen(Color.FromArgb(210, 255, 92, 92), 2);
        using var yPen = new Pen(Color.FromArgb(210, 122, 224, 92), 2);
        using var zPen = new Pen(Color.FromArgb(210, 92, 172, 255), 2);

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

    private readonly record struct Point3(float X, float Y, float Z);

    private void DrawSelection(Graphics g)
    {
        if ((SelectedObject < 0 || SelectedObject >= Scene.ObjectCount) && SelectedObjects.Count == 0) return;
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
            }

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

        g.SmoothingMode = oldMode;
    }

    private void DrawSelectionOutline(Graphics g, int i, bool primary)
    {
        var shape = Scene.ShapeKind.Length > i ? Scene.ShapeKind[i] : ShapeKind.Rectangle;

        if (shape == ShapeKind.Line)
        {
            if (primary) DrawBezierGuides(g, i);
            else DrawBezierOutline(g, i, primary: false);
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

            if (primary && shape != ShapeKind.Path) DrawBoundaryHandles(g, i);
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
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = StrokePen(FreehandPreviewColor, screenWidth);
        if (FreehandPreviewPoints.Count == 1)
        {
            var point = WorldToScreen(FreehandPreviewPoints[0]);
            using var dot = new SolidBrush(FreehandPreviewColor);
            g.FillEllipse(dot, point.X - screenWidth * 0.5f, point.Y - screenWidth * 0.5f, screenWidth, screenWidth);
        }
        else
        {
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
        using var linePen = new Pen(((SolidBrush)brush).Color, screenStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(linePen, path);
        g.SmoothingMode = oldMode;
    }

    private void DrawBezierGuides(Graphics g, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var path = BuildQuadraticPath(start, control, end);
        g.DrawLine(_guidePen, start, control);
        g.DrawLine(_guidePen, control, end);
        DrawSelectionPath(g, path, primary: true);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control, _bezierHandleBrush, 10);
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
        g.DrawPolygon(primary ? _selectionOuterGlowPen : _multiSelectionOuterGlowPen, points);
        g.DrawPolygon(primary ? _selectionGlowPen : _multiSelectionGlowPen, points);
        g.DrawPolygon(primary ? _selectionPen : _multiSelectionPen, points);
    }

    private void DrawSelectionPolyline(Graphics g, PointF[] points, bool primary)
    {
        g.DrawLines(primary ? _selectionOuterGlowPen : _multiSelectionOuterGlowPen, points);
        g.DrawLines(primary ? _selectionGlowPen : _multiSelectionGlowPen, points);
        g.DrawLines(primary ? _selectionPen : _multiSelectionPen, points);
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

    private PointF WorldToScreen(PointF point) => WorldToScreen(point.X, point.Y);

    private SolidBrush BrushFor(int argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        if (_brushCache.Count > 2048)
        {
            foreach (var item in _brushCache.Values) item.Dispose();
            _brushCache.Clear();
        }

        brush = new SolidBrush(Color.FromArgb(argb));
        _brushCache[argb] = brush;
        return brush;
    }
}
