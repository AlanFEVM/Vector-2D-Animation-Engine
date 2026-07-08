using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class StageControl : Control
{
    private const int MaxSelectionOutlines = 512;
    private readonly Dictionary<int, SolidBrush> _brushCache = new(512);
    private readonly Pen _gridPen = new(Color.FromArgb(35, 58, 64, 69));
    private readonly Pen _strokePen = new(Color.FromArgb(210, 10, 12, 14));
    private readonly Pen _selectionPen = new(Color.FromArgb(255, 255, 217, 107), 2);
    private readonly Pen _selectionGlowPen = new(Color.FromArgb(135, 32, 172, 255), 6);
    private readonly Pen _multiSelectionPen = new(Color.FromArgb(210, 112, 204, 255), 1);
    private readonly Pen _multiSelectionGlowPen = new(Color.FromArgb(85, 32, 172, 255), 4);
    private readonly Pen _guidePen = new(Color.FromArgb(190, 112, 204, 255), 1);
    private readonly Pen _previewGuidePen = new(Color.FromArgb(170, 255, 255, 255), 1);
    private readonly Pen _marqueePen = new(Color.FromArgb(230, 112, 204, 255), 1) { DashStyle = DashStyle.Dash };
    private readonly SolidBrush _marqueeBrush = new(Color.FromArgb(34, 112, 204, 255));
    private readonly SolidBrush _handleBrush = new(Color.FromArgb(255, 255, 240, 168));
    private readonly SolidBrush _bezierHandleBrush = new(Color.FromArgb(255, 112, 204, 255));
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly Direct2DStageRenderer _direct2DRenderer = new();
    private int _selectedObject = -1;
    private int[] _selectedObjects = Array.Empty<int>();
    private float? _pendingVisibleWorldWidth;

    public VectorScene Scene { get; }
    public int Frame { get; set; }
    public int SelectedObject
    {
        get => _selectedObject;
        set
        {
            _selectedObject = value;
            _selectedObjects = value >= 0 && value < Scene.ObjectCount ? new[] { value } : Array.Empty<int>();
            SelectedElement = DrawingElementHit.None;
            Invalidate();
        }
    }

    public IReadOnlyList<int> SelectedObjects => _selectedObjects;
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
    public float CameraX { get; private set; }
    public float CameraY { get; private set; }
    public float Zoom { get; private set; } = 1;
    public RenderStats LastStats { get; private set; }

    public StageControl(VectorScene scene)
    {
        Scene = scene;
        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(17, 19, 21);
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
        if (_direct2DRenderer.TryRender(this, out var stats))
        {
            LastStats = stats;
            return;
        }

        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;
        DrawGrid(g);

        LastStats = Scene.ObjectCount < 5000
            ? DrawObjects(g)
            : Zoom < 0.08f
                ? DrawOverviewTiles(g)
                : Zoom < 0.18f
                    ? DrawTiles(g)
                    : DrawObjects(g);
        DrawSelection(g);
        DrawDrawingPreview(g);
        DrawMarquee(g);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _direct2DRenderer.Resize(ClientSize);
        if (_pendingVisibleWorldWidth is { } visibleWorldWidth) SetVisibleWorldWidth(visibleWorldWidth);
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
            _multiSelectionPen.Dispose();
            _multiSelectionGlowPen.Dispose();
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
        if (!SelectedElement.IsValid || SelectedElement.Key.ObjectIndex != primaryObject) SelectedElement = DrawingElementHit.None;
        Invalidate();
    }

    public void SetSelectedElement(DrawingElementHit hit)
    {
        SelectedElement = hit;
        Invalidate();
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
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top;
        var bottom = bounds.Bottom;
        scene.GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);

        var visible = 0;
        var drawn = 0;
        var scanned = 0;
        long atoms = 0;
        var drawLimit = Zoom < 0.35f ? 65_000 : 160_000;

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = scene.CellIndex(cx, cy);
                var start = scene.CellStart[cell];
                var end = scene.CellStart[cell + 1];
                scanned += end - start;

                for (var p = start; p < end; p++)
                {
                    var i = scene.CellObjects[p];
                    var layer = scene.ObjectLayer[i];
                    if (!scene.IsLayerActive(layer, Frame)) continue;
                    var halfW = scene.Width[i] * 0.5f;
                    var halfH = scene.Height[i] * 0.5f;
                    if (scene.X[i] + halfW < left || scene.X[i] - halfW > right || scene.Y[i] + halfH < top || scene.Y[i] - halfH > bottom) continue;

                    visible++;
                    atoms += scene.AtomCount[i];
                    if (drawn >= drawLimit) continue;
                    DrawObject(g, i);
                    drawn++;
                }
            }
        }

        return new RenderStats(visible, drawn, atoms, 0, scanned, false);
    }

    private void DrawObject(Graphics g, int i)
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

        if (shape == ShapeKind.Path && scene.TryGetPathWorldPoints(i, out var pathPoints))
        {
            DrawPathObject(g, i, pathPoints, brush, strokeColor, scene.Stroke[i], screenStroke);
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
            DrawLocalShape(g, shape, brush, strokeColor, scene.Stroke[i], screenStroke, w, h);
            g.Restore(state);
            return;
        }

        g.FillRectangle(brush, rect);
        if (scene.Stroke[i] > 0 && w > 4 && h > 4)
        {
            using var pen = StrokePen(strokeColor, screenStroke);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void DrawPathObject(Graphics g, int i, PointF[] worldPoints, Brush brush, Color strokeColor, float stroke, float screenStroke)
    {
        if (worldPoints.Length < 3) return;
        var points = new PointF[worldPoints.Length];
        for (var p = 0; p < worldPoints.Length; p++) points[p] = WorldToScreen(worldPoints[p]);
        g.FillPolygon(brush, points);
        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        g.DrawPolygon(pen, points);
    }

    private void DrawLocalShape(Graphics g, ShapeKind shape, Brush brush, Color strokeColor, float stroke, float screenStroke, float w, float h)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                g.FillEllipse(brush, rect);
                if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawEllipse(pen, rect);
                }
                break;
            case ShapeKind.Line:
                using (var linePen = StrokePen(strokeColor, screenStroke))
                {
                    g.DrawLine(linePen, -w * 0.5f, 0, w * 0.5f, 0);
                }
                break;
            case ShapeKind.Triangle:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(3, w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Polygon:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(6, w, h, -MathF.PI / 2));
                break;
            case ShapeKind.Star:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, StarPoints(5, w, h, -MathF.PI / 2));
                break;
            default:
                g.FillRectangle(brush, rect);
                if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                break;
        }
    }

    private static void DrawPolygonShape(Graphics g, Brush brush, Color strokeColor, float stroke, float screenStroke, PointF[] points)
    {
        g.FillPolygon(brush, points);
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
        var step = Math.Max(24, WorldLengthToScreen(512));
        var origin = WorldToScreen(0, 0);
        for (var x = origin.X % step; x < Width; x += step) g.DrawLine(_gridPen, x, 0, x, Height);
        for (var y = origin.Y % step; y < Height; y += step) g.DrawLine(_gridPen, 0, y, Width, y);
    }

    private void DrawSelection(Graphics g)
    {
        if ((SelectedObject < 0 || SelectedObject >= Scene.ObjectCount) && SelectedObjects.Count == 0) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var drawn = 0;
        foreach (var index in SelectedObjects)
        {
            if (index == SelectedObject || index < 0 || index >= Scene.ObjectCount) continue;
            DrawSelectionOutline(g, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = SelectedObject;
        if (primary >= 0 && primary < Scene.ObjectCount)
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
            if (primary && SelectedElement.Key.Kind == DrawingElementKind.Stroke && SelectedElement.Key.ObjectIndex == i)
            {
                DrawBezierGuides(g, i, SelectedElement.StartT, SelectedElement.EndT);
            }
            else if (primary) DrawBezierGuides(g, i);
            else DrawBezierOutline(g, i, _multiSelectionGlowPen, _multiSelectionPen);
        }
        else
        {
            if (primary && SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke && SelectedElement.Key.ObjectIndex == i)
            {
                DrawBoundaryPartialOutline(g, i, SelectedElement.StartT, SelectedElement.EndT);
            }
            else
            {
                DrawBoundaryOutline(g, i, primary ? _selectionGlowPen : _multiSelectionGlowPen, primary ? _selectionPen : _multiSelectionPen);
            }

            if (primary) DrawBoundaryHandles(g, i);
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
        g.DrawPath(_selectionGlowPen, path);
        g.DrawPath(_selectionPen, path);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control, _bezierHandleBrush, 10);
    }

    private void DrawBezierGuides(Graphics g, int i, float startT, float endT)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var fullPath = BuildQuadraticPath(start, control, end);
        using var partialPath = BuildQuadraticSamplePath(start, control, end, startT, endT);
        g.DrawLine(_guidePen, start, control);
        g.DrawLine(_guidePen, control, end);
        using var mutedPen = new Pen(Color.FromArgb(80, _selectionPen.Color), 1.2f);
        g.DrawPath(mutedPen, fullPath);
        g.DrawPath(_selectionGlowPen, partialPath);
        g.DrawPath(_selectionPen, partialPath);
        DrawHandle(g, start, _handleBrush, 8);
        DrawHandle(g, end, _handleBrush, 8);
        DrawHandle(g, control, _bezierHandleBrush, 10);
    }

    private void DrawBezierOutline(Graphics g, int i, Pen glowPen, Pen pen)
    {
        var (start, control, end) = GetBezierScreenPoints(i);
        using var path = BuildQuadraticPath(start, control, end);
        g.DrawPath(glowPen, path);
        g.DrawPath(pen, path);
    }

    private void DrawBoundaryOutline(Graphics g, int i, Pen glowPen, Pen pen)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;
        g.DrawPolygon(glowPen, points);
        g.DrawPolygon(pen, points);
    }

    private void DrawBoundaryPartialOutline(Graphics g, int i, float startT, float endT)
    {
        var points = GetBoundaryScreenPolyline(i);
        if (points.Length < 2) return;

        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        using var mutedPen = new Pen(Color.FromArgb(80, _selectionPen.Color), 1.2f);
        g.DrawPath(mutedPen, fullPath);
        g.DrawPath(_selectionGlowPen, partialPath);
        g.DrawPath(_selectionPen, partialPath);
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
