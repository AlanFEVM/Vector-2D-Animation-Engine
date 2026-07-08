using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed class StageControl : Control
{
    private readonly Dictionary<int, SolidBrush> _brushCache = new(512);
    private readonly Pen _gridPen = new(Color.FromArgb(35, 58, 64, 69));
    private readonly Pen _strokePen = new(Color.FromArgb(210, 10, 12, 14));
    private readonly Pen _selectionPen = new(Color.FromArgb(255, 255, 217, 107), 2);
    private readonly Pen _selectionGlowPen = new(Color.FromArgb(135, 32, 172, 255), 6);
    private readonly Pen _guidePen = new(Color.FromArgb(190, 112, 204, 255), 1);
    private readonly Pen _previewGuidePen = new(Color.FromArgb(170, 255, 255, 255), 1);
    private readonly SolidBrush _handleBrush = new(Color.FromArgb(255, 255, 240, 168));
    private readonly SolidBrush _bezierHandleBrush = new(Color.FromArgb(255, 112, 204, 255));
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly Direct2DStageRenderer _direct2DRenderer = new();

    public VectorScene Scene { get; }
    public int Frame { get; set; }
    public int SelectedObject { get; set; } = -1;
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
        CameraX = 0;
        CameraY = 0;
        Zoom = (float)Math.Clamp(Math.Min(Width / Scene.StageWidth, Height / Scene.StageHeight) * 0.9, 0.02, 64);
        Invalidate();
    }

    public PointF ScreenToWorld(Point screen)
    {
        return new PointF(CameraX + (screen.X - Width * 0.5f) / Zoom, CameraY + (screen.Y - Height * 0.5f) / Zoom);
    }

    public PointF WorldToScreen(float x, float y)
    {
        return new PointF(Width * 0.5f + (x - CameraX) * Zoom, Height * 0.5f + (y - CameraY) * Zoom);
    }

    public void Pan(float dx, float dy)
    {
        CameraX -= dx / Zoom;
        CameraY -= dy / Zoom;
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

    public EditHandleKind HitTestHandle(Point screen, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= Scene.ObjectCount) return EditHandleKind.None;

        var shape = Scene.ShapeKind.Length > objectIndex ? Scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        if (shape == ShapeKind.Line)
        {
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
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _direct2DRenderer.Resize(ClientSize);
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
            _guidePen.Dispose();
            _previewGuidePen.Dispose();
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

    private RenderStats DrawOverviewTiles(Graphics g)
    {
        var scene = Scene;
        var tileW = scene.StageWidth / scene.OverviewColumnCount;
        var tileH = scene.StageHeight / scene.OverviewRowCount;
        var left = CameraX - Width * 0.5f / Zoom;
        var right = CameraX + Width * 0.5f / Zoom;
        var top = CameraY - Height * 0.5f / Zoom;
        var bottom = CameraY + Height * 0.5f / Zoom;
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
                g.FillRectangle(BrushFor(scene.OverviewArgb[tile]), screen.X, screen.Y, Math.Max(1, tileW * Zoom + 1), Math.Max(1, tileH * Zoom + 1));
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
        var left = CameraX - Width * 0.5f / Zoom;
        var right = CameraX + Width * 0.5f / Zoom;
        var top = CameraY - Height * 0.5f / Zoom;
        var bottom = CameraY + Height * 0.5f / Zoom;
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
                g.FillRectangle(BrushFor(scene.TileArgb[tile]), screen.X, screen.Y, Math.Max(1, tileW * Zoom + 1), Math.Max(1, tileH * Zoom + 1));
                draws++;
            }
        }

        return new RenderStats(draws, draws, atoms, draws, draws, true);
    }

    private RenderStats DrawObjects(Graphics g)
    {
        var scene = Scene;
        var left = CameraX - Width * 0.5f / Zoom;
        var right = CameraX + Width * 0.5f / Zoom;
        var top = CameraY - Height * 0.5f / Zoom;
        var bottom = CameraY + Height * 0.5f / Zoom;
        var bounds = RectangleF.FromLTRB(left, top, right, bottom);
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
        var w = Math.Max(0.75f, scene.Width[i] * Zoom);
        var h = Math.Max(0.75f, scene.Height[i] * Zoom);
        var rect = new RectangleF(screen.X - w * 0.5f, screen.Y - h * 0.5f, w, h);
        var brush = BrushFor(scene.Argb[i]);
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        var strokeColor = StrokeColorFor(i);
        var screenStroke = Math.Max(1.5f, scene.Stroke[i] * Zoom);

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
        var step = Math.Max(24, 512 * Zoom);
        var origin = WorldToScreen(0, 0);
        for (var x = origin.X % step; x < Width; x += step) g.DrawLine(_gridPen, x, 0, x, Height);
        for (var y = origin.Y % step; y < Height; y += step) g.DrawLine(_gridPen, 0, y, Width, y);
    }

    private void DrawSelection(Graphics g)
    {
        var i = SelectedObject;
        if (i < 0 || i >= Scene.ObjectCount) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var shape = Scene.ShapeKind.Length > i ? Scene.ShapeKind[i] : ShapeKind.Rectangle;

        if (shape == ShapeKind.Line)
        {
            DrawBezierGuides(g, i);
        }
        else
        {
            DrawBoundaryOutline(g, i);
            DrawBoundaryHandles(g, i);
        }
        g.SmoothingMode = oldMode;
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
        using var stroke = new Pen(strokeColor, Math.Max(1.5f, DrawingPreviewStroke * Zoom))
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
        var w = Math.Max(2, Math.Abs(dx) * Zoom);
        var h = Math.Max(2, Math.Abs(dy) * Zoom);
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        DrawPreviewLocalShape(g, DrawingPreviewShape, fill, stroke, w, h);
        g.Restore(state);

        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(boundsPen, center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        g.SmoothingMode = oldMode;
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

    private void DrawBoundaryOutline(Graphics g, int i)
    {
        var points = BoundaryHandles().Select(handle => WorldToScreen(GetBoundaryHandleWorldPoint(i, handle))).ToArray();
        g.DrawPolygon(_selectionGlowPen, points);
        g.DrawPolygon(_selectionPen, points);
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

    private PointF LocalToWorld(int i, PointF local)
    {
        var angle = Scene.Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(
            Scene.X[i] + local.X * cos - local.Y * sin,
            Scene.Y[i] + local.X * sin + local.Y * cos);
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
