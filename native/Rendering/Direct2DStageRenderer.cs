using System.Numerics;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DColor = Vortice.Mathematics.Color;
using D2DRect = Vortice.Mathematics.Rect;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;

namespace VectorAnimationEngine;

internal sealed class Direct2DStageRenderer : IDisposable
{
    private const int MaxSelectionOutlines = 512;
    private readonly Dictionary<int, ID2D1SolidColorBrush> _brushCache = new(2048);
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _target;
    private SizeI _targetSize;
    private bool _disabled;

    public bool IsActive => !_disabled && _target is not null;

    public bool TryRender(StageControl stage, out RenderStats stats)
    {
        stats = default;
        if (_disabled || stage.Width <= 0 || stage.Height <= 0) return false;

        try
        {
            EnsureTarget(stage);
            if (_target is null) return false;

            _target.BeginDraw();
            _target.AntialiasMode = AntialiasMode.PerPrimitive;
            var background = ToD2D(stage.BackColor);
            _target.Clear(in background);
            DrawGrid(stage);

            stats = stage.Scene.ObjectCount < 5000
                ? DrawObjects(stage)
                : stage.Zoom < 0.08f
                    ? DrawOverviewTiles(stage)
                    : stage.Zoom < 0.18f
                        ? DrawTiles(stage)
                        : DrawObjects(stage);

            DrawSelection(stage);
            DrawDrawingPreview(stage);
            DrawMarquee(stage);

            var result = _target.EndDraw();
            if (result.Failure)
            {
                ResetTarget();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D rendering failed; falling back to GDI renderer", ex);
            ResetTarget();
            _disabled = true;
            return false;
        }
    }

    public void Resize(System.Drawing.Size clientSize)
    {
        if (_target is null || clientSize.Width <= 0 || clientSize.Height <= 0) return;
        var next = new SizeI(clientSize.Width, clientSize.Height);
        if (next.Width == _targetSize.Width && next.Height == _targetSize.Height) return;
        _target.Resize(next);
        _targetSize = next;
    }

    public void Dispose()
    {
        ResetTarget();
        _factory?.Dispose();
        _factory = null;
    }

    private void EnsureTarget(StageControl stage)
    {
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        var nextSize = new SizeI(Math.Max(1, stage.Width), Math.Max(1, stage.Height));
        if (_target is not null)
        {
            if (nextSize.Width != _targetSize.Width || nextSize.Height != _targetSize.Height)
            {
                Resize(stage.ClientSize);
            }

            return;
        }

        var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore);
        var renderTargetProperties = new RenderTargetProperties(RenderTargetType.Hardware, pixelFormat, 96, 96, RenderTargetUsage.None, FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = stage.Handle,
            PixelSize = nextSize,
            PresentOptions = PresentOptions.Immediately
        };

        _target = _factory.CreateHwndRenderTarget(renderTargetProperties, hwndProperties);
        _targetSize = nextSize;
    }

    private RenderStats DrawOverviewTiles(StageControl stage)
    {
        var scene = stage.Scene;
        var tileW = scene.StageWidth / scene.OverviewColumnCount;
        var tileH = scene.StageHeight / scene.OverviewRowCount;
        var viewport = stage.VisibleWorldBounds();
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
                var screen = stage.WorldToScreen(tx * tileW - scene.StageWidth * 0.5f, ty * tileH - scene.StageHeight * 0.5f);
                FillRectangle(screen.X, screen.Y, Math.Max(1, stage.WorldLengthToScreen(tileW) + 1), Math.Max(1, stage.WorldLengthToScreen(tileH) + 1), scene.OverviewArgb[tile]);
                draws++;
            }
        }

        return new RenderStats(draws, draws, atoms, draws, draws, true);
    }

    private RenderStats DrawTiles(StageControl stage)
    {
        var scene = stage.Scene;
        var tileW = scene.StageWidth / scene.TileColumnCount;
        var tileH = scene.StageHeight / scene.TileRowCount;
        var viewport = stage.VisibleWorldBounds();
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
                var screen = stage.WorldToScreen(tx * tileW - scene.StageWidth * 0.5f, ty * tileH - scene.StageHeight * 0.5f);
                FillRectangle(screen.X, screen.Y, Math.Max(1, stage.WorldLengthToScreen(tileW) + 1), Math.Max(1, stage.WorldLengthToScreen(tileH) + 1), scene.TileArgb[tile]);
                draws++;
            }
        }

        return new RenderStats(draws, draws, atoms, draws, draws, true);
    }

    private RenderStats DrawObjects(StageControl stage)
    {
        var scene = stage.Scene;
        var bounds = stage.VisibleWorldBounds();
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top;
        var bottom = bounds.Bottom;
        scene.GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);

        var visible = 0;
        var drawn = 0;
        var scanned = 0;
        long atoms = 0;
        var drawLimit = stage.Zoom < 0.35f ? 95_000 : 220_000;

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
                    if (!scene.IsLayerActive(layer, stage.Frame)) continue;
                    var halfW = scene.Width[i] * 0.5f;
                    var halfH = scene.Height[i] * 0.5f;
                    if (scene.X[i] + halfW < left || scene.X[i] - halfW > right || scene.Y[i] + halfH < top || scene.Y[i] - halfH > bottom) continue;

                    visible++;
                    atoms += scene.AtomCount[i];
                    if (drawn >= drawLimit) continue;
                    DrawObject(stage, i);
                    drawn++;
                }
            }
        }

        return new RenderStats(visible, drawn, atoms, 0, scanned, false);
    }

    private void DrawObject(StageControl stage, int i)
    {
        var scene = stage.Scene;
        var screen = stage.WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Height[i]));
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        var brush = BrushFor(scene.Argb[i]);
        var strokeBrush = BrushFor(scene.StrokeArgb.Length > i ? scene.StrokeArgb[i] : GdiColor.FromArgb(238, 242, 241).ToArgb());
        var screenStroke = Math.Max(0.1f, stage.WorldLengthToScreen(scene.Stroke[i]));

        if (shape == ShapeKind.Path && scene.TryGetPathWorldPoints(i, out var pathPoints))
        {
            DrawPathObject(stage, pathPoints, brush, strokeBrush, scene.Stroke[i], screenStroke);
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (scene.Stroke[i] > 0) DrawBezierLine(stage, i, strokeBrush, screenStroke);
            return;
        }

        var old = _target!.Transform;
        _target.Transform = Matrix3x2.CreateRotation(scene.Angle[i], new Vector2(screen.X, screen.Y));
        DrawLocalShape(shape, brush, strokeBrush, scene.Stroke[i], screenStroke, screen.X, screen.Y, w, h);
        _target.Transform = old;
    }

    private void DrawPathObject(StageControl stage, GdiPointF[] worldPoints, ID2D1SolidColorBrush brush, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke)
    {
        if (worldPoints.Length < 3) return;
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(WorldToVector(stage, worldPoints[0]), FigureBegin.Filled);
            for (var i = 1; i < worldPoints.Length; i++) sink.AddLine(WorldToVector(stage, worldPoints[i]));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _target!.FillGeometry(path, brush);
        if (stroke > 0) _target.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private void DrawLocalShape(ShapeKind shape, ID2D1SolidColorBrush brush, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, float cx, float cy, float w, float h)
    {
        var rect = Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                var center = new Vector2(cx, cy);
                var ellipse = new Ellipse(in center, w * 0.5f, h * 0.5f);
                _target!.FillEllipse(ellipse, brush);
                if (stroke > 0 && w > 4 && h > 4) _target.DrawEllipse(ellipse, strokeBrush, screenStroke);
                break;
            case ShapeKind.Triangle:
                DrawPolygon(RegularPolygonPoints(3, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke);
                break;
            case ShapeKind.Polygon:
                DrawPolygon(RegularPolygonPoints(6, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke);
                break;
            case ShapeKind.Star:
                DrawPolygon(StarPoints(5, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke);
                break;
            default:
                _target!.FillRectangle(in rect, brush);
                if (stroke > 0 && w > 4 && h > 4) _target.DrawRectangle(in rect, strokeBrush, screenStroke);
                break;
        }
    }

    private void DrawPolygon(Vector2[] points, ID2D1SolidColorBrush fill, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke)
    {
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (var i = 1; i < points.Length; i++) sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _target!.FillGeometry(path, fill);
        if (stroke > 0) _target.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private void DrawGrid(StageControl stage)
    {
        var step = Math.Max(24, stage.WorldLengthToScreen(512));
        var origin = stage.WorldToScreen(0, 0);
        var brush = BrushFor(GdiColor.FromArgb(35, 58, 64, 69).ToArgb());
        for (var x = origin.X % step; x < stage.Width; x += step) _target!.DrawLine(new Vector2(x, 0), new Vector2(x, stage.Height), brush, 1);
        for (var y = origin.Y % step; y < stage.Height; y += step) _target!.DrawLine(new Vector2(0, y), new Vector2(stage.Width, y), brush, 1);
    }

    private void DrawSelection(StageControl stage)
    {
        if ((stage.SelectedObject < 0 || stage.SelectedObject >= stage.Scene.ObjectCount) && stage.SelectedObjects.Count == 0) return;

        var drawn = 0;
        foreach (var index in stage.SelectedObjects)
        {
            if (index == stage.SelectedObject || index < 0 || index >= stage.Scene.ObjectCount) continue;
            DrawSelectionOutline(stage, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = stage.SelectedObject;
        if (primary >= 0 && primary < stage.Scene.ObjectCount) DrawSelectionOutline(stage, primary, primary: true);
    }

    private void DrawSelectionOutline(StageControl stage, int i, bool primary)
    {
        var shape = stage.Scene.ShapeKind.Length > i ? stage.Scene.ShapeKind[i] : ShapeKind.Rectangle;
        if (shape == ShapeKind.Line)
        {
            if (primary && stage.SelectedElement.Key.Kind == DrawingElementKind.Stroke && stage.SelectedElement.Key.ObjectIndex == i)
            {
                DrawBezierGuides(stage, i, stage.SelectedElement.StartT, stage.SelectedElement.EndT);
            }
            else if (primary) DrawBezierGuides(stage, i);
            else DrawBezierOutline(stage, i, BrushFor(GdiColor.FromArgb(85, 32, 172, 255).ToArgb()), BrushFor(GdiColor.FromArgb(210, 112, 204, 255).ToArgb()));
            return;
        }

        var points = GetBoundaryVectors(stage, i);
        if (points.Length < 2) return;
        if (primary && stage.SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke && stage.SelectedElement.Key.ObjectIndex == i)
        {
            DrawBoundaryPartialOutline(points, stage.SelectedElement.StartT, stage.SelectedElement.EndT);
        }
        else
        {
            DrawPolyline(points, BrushFor((primary ? GdiColor.FromArgb(135, 32, 172, 255) : GdiColor.FromArgb(85, 32, 172, 255)).ToArgb()), primary ? 6 : 4);
            DrawPolyline(points, BrushFor((primary ? GdiColor.FromArgb(255, 255, 217, 107) : GdiColor.FromArgb(210, 112, 204, 255)).ToArgb()), primary ? 2 : 1);
        }

        if (!primary) return;
        foreach (var handle in BoundaryHandles()) DrawHandle(WorldToVector(stage, stage.GetBoundaryHandleWorldPoint(i, handle)), BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 9);
    }

    private void DrawDrawingPreview(StageControl stage)
    {
        if (!stage.DrawingPreviewVisible) return;
        var start = stage.DrawingPreviewStart;
        var end = stage.DrawingPreviewEnd;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 0.001f) return;

        var fill = BrushFor(GdiColor.FromArgb(72, stage.DrawingPreviewColor).ToArgb());
        var stroke = BrushFor(GdiColor.FromArgb(230, stage.DrawingPreviewColor).ToArgb());
        if (stage.DrawingPreviewShape == ShapeKind.Line)
        {
            var a = WorldToVector(stage, start);
            var b = WorldToVector(stage, end);
            _target!.DrawLine(a, b, BrushFor(GdiColor.FromArgb(170, 255, 255, 255).ToArgb()), 1);
            _target.DrawLine(a, b, stroke, Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke)));
            DrawHandle(a, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
            DrawHandle(b, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 7);
            return;
        }

        var center = stage.WorldToScreen((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        var w = Math.Max(2, stage.WorldLengthToScreen(Math.Abs(dx)));
        var h = Math.Max(2, stage.WorldLengthToScreen(Math.Abs(dy)));
        DrawLocalShape(stage.DrawingPreviewShape, fill, stroke, 0, 0, center.X, center.Y, w, h);
        var rect = Rect(center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        _target!.DrawRectangle(in rect, stroke, Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke)));
    }

    private void DrawMarquee(StageControl stage)
    {
        if (!stage.MarqueeVisible) return;
        var left = Math.Min(stage.MarqueeStart.X, stage.MarqueeEnd.X);
        var top = Math.Min(stage.MarqueeStart.Y, stage.MarqueeEnd.Y);
        var right = Math.Max(stage.MarqueeStart.X, stage.MarqueeEnd.X);
        var bottom = Math.Max(stage.MarqueeStart.Y, stage.MarqueeEnd.Y);
        if (right - left < 2 || bottom - top < 2) return;

        var rect = Rect(left, top, right - left, bottom - top);
        _target!.FillRectangle(in rect, BrushFor(GdiColor.FromArgb(34, 112, 204, 255).ToArgb()));
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(230, 112, 204, 255).ToArgb()), 1);
    }

    private void DrawBezierLine(StageControl stage, int i, ID2D1SolidColorBrush brush, float screenStroke)
    {
        using var path = BuildBezierPath(stage, i);
        _target!.DrawGeometry(path, brush, screenStroke);
    }

    private void DrawBezierOutline(StageControl stage, int i, ID2D1SolidColorBrush glow, ID2D1SolidColorBrush pen)
    {
        using var path = BuildBezierPath(stage, i);
        _target!.DrawGeometry(path, glow, 4);
        _target.DrawGeometry(path, pen, 1);
    }

    private void DrawBezierGuides(StageControl stage, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(stage, i);
        using var path = BuildBezierPath(stage, i);
        var guide = BrushFor(GdiColor.FromArgb(190, 112, 204, 255).ToArgb());
        _target!.DrawLine(start, control, guide, 1);
        _target.DrawLine(control, end, guide, 1);
        _target.DrawGeometry(path, BrushFor(GdiColor.FromArgb(135, 32, 172, 255).ToArgb()), 6);
        _target.DrawGeometry(path, BrushFor(GdiColor.FromArgb(255, 255, 217, 107).ToArgb()), 2);
        DrawHandle(start, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(end, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(control, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 10);
    }

    private void DrawBezierGuides(StageControl stage, int i, float startT, float endT)
    {
        var (start, control, end) = GetBezierScreenPoints(stage, i);
        using var fullPath = BuildBezierPath(stage, i);
        using var partialPath = BuildBezierSamplePath(start, control, end, startT, endT);
        var guide = BrushFor(GdiColor.FromArgb(190, 112, 204, 255).ToArgb());
        _target!.DrawLine(start, control, guide, 1);
        _target.DrawLine(control, end, guide, 1);
        _target.DrawGeometry(fullPath, BrushFor(GdiColor.FromArgb(80, 255, 217, 107).ToArgb()), 1.2f);
        _target.DrawGeometry(partialPath, BrushFor(GdiColor.FromArgb(135, 32, 172, 255).ToArgb()), 6);
        _target.DrawGeometry(partialPath, BrushFor(GdiColor.FromArgb(255, 255, 217, 107).ToArgb()), 2);
        DrawHandle(start, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(end, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(control, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 10);
    }

    private ID2D1PathGeometry BuildBezierPath(StageControl stage, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(stage, i);
        var c1 = start + (control - start) * (2f / 3f);
        var c2 = end + (control - end) * (2f / 3f);
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(start, FigureBegin.Hollow);
            var segment = new BezierSegment(in c1, in c2, in end);
            sink.AddBezier(segment);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private ID2D1PathGeometry BuildBezierSamplePath(Vector2 start, Vector2 control, Vector2 end, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(QuadraticPoint(start, control, end, startT), FigureBegin.Hollow);
            const int samples = 20;
            for (var i = 1; i <= samples; i++)
            {
                var t = startT + (endT - startT) * i / samples;
                sink.AddLine(QuadraticPoint(start, control, end, t));
            }

            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private void DrawBoundaryPartialOutline(Vector2[] points, float startT, float endT)
    {
        using var fullPath = BuildPolylineSamplePath(points, 0, 1);
        using var partialPath = BuildPolylineSamplePath(points, startT, endT);
        _target!.DrawGeometry(fullPath, BrushFor(GdiColor.FromArgb(80, 255, 217, 107).ToArgb()), 1.2f);
        _target.DrawGeometry(partialPath, BrushFor(GdiColor.FromArgb(135, 32, 172, 255).ToArgb()), 6);
        _target.DrawGeometry(partialPath, BrushFor(GdiColor.FromArgb(255, 255, 217, 107).ToArgb()), 2);
    }

    private ID2D1PathGeometry BuildPolylineSamplePath(Vector2[] points, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(PolylinePointAt(points, startT), FigureBegin.Hollow);
            var segments = Math.Max(1, points.Length - 1);
            var samples = Math.Max(2, (int)Math.Ceiling((endT - startT) * segments * 4));
            for (var i = 1; i <= samples; i++)
            {
                var t = startT + (endT - startT) * i / samples;
                sink.AddLine(PolylinePointAt(points, t));
            }

            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        return path;
    }

    private static Vector2 PolylinePointAt(Vector2[] points, float t)
    {
        if (points.Length == 0) return default;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Vector2.Lerp(points[index], points[index + 1], scaled - index);
    }

    private static Vector2 QuadraticPoint(Vector2 start, Vector2 control, Vector2 end, float t)
    {
        var inv = 1 - t;
        return start * (inv * inv) + control * (2 * inv * t) + end * (t * t);
    }

    private (Vector2 Start, Vector2 Control, Vector2 End) GetBezierScreenPoints(StageControl stage, int i)
    {
        var halfW = stage.Scene.Width[i] * 0.5f;
        var start = WorldToVector(stage, LocalToWorld(stage, i, new GdiPointF(-halfW, 0)));
        var end = WorldToVector(stage, LocalToWorld(stage, i, new GdiPointF(halfW, 0)));
        var control = ToVector(stage.WorldToScreen(stage.Scene.CurveControlX[i], stage.Scene.CurveControlY[i]));
        return (start, control, end);
    }

    private void DrawOpenPolygon(Vector2[] points, ID2D1SolidColorBrush brush, float width)
    {
        for (var i = 0; i < points.Length; i++) _target!.DrawLine(points[i], points[(i + 1) % points.Length], brush, width);
    }

    private void DrawPolyline(Vector2[] points, ID2D1SolidColorBrush brush, float width)
    {
        for (var i = 0; i < points.Length - 1; i++) _target!.DrawLine(points[i], points[i + 1], brush, width);
    }

    private void DrawHandle(Vector2 point, ID2D1SolidColorBrush brush, float size)
    {
        var rect = Rect(point.X - size * 0.5f, point.Y - size * 0.5f, size, size);
        _target!.FillRectangle(in rect, brush);
        _target.DrawRectangle(in rect, BrushFor(GdiColor.FromArgb(255, 16, 18, 22).ToArgb()), 1);
    }

    private void FillRectangle(float x, float y, float width, float height, int argb)
    {
        var rect = Rect(x, y, width, height);
        _target!.FillRectangle(in rect, BrushFor(argb));
    }

    private ID2D1SolidColorBrush BrushFor(int argb)
    {
        if (_target is null) throw new InvalidOperationException("Direct2D render target is not ready.");
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        if (_brushCache.Count > 4096) ClearBrushCache();

        var color = ToD2D(GdiColor.FromArgb(argb));
        brush = _target.CreateSolidColorBrush(in color, null);
        _brushCache[argb] = brush;
        return brush;
    }

    private void ResetTarget()
    {
        ClearBrushCache();
        _target?.Dispose();
        _target = null;
        _targetSize = default;
    }

    private void ClearBrushCache()
    {
        foreach (var brush in _brushCache.Values) brush.Dispose();
        _brushCache.Clear();
    }

    private static D2DRect Rect(float x, float y, float width, float height) => new(x, y, width, height);

    private static D2DColor ToD2D(GdiColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Vector2 ToVector(GdiPointF point) => new(point.X, point.Y);

    private static Vector2 WorldToVector(StageControl stage, GdiPointF point) => ToVector(stage.WorldToScreen(point.X, point.Y));

    private static Vector2[] GetBoundaryVectors(StageControl stage, int i)
    {
        var boundary = stage.Scene.GetShapeBoundary(i);
        var points = new Vector2[boundary.Length];
        for (var p = 0; p < boundary.Length; p++) points[p] = WorldToVector(stage, boundary[p]);
        return points;
    }

    private static Vector2[] RegularPolygonPoints(int sides, float cx, float cy, float w, float h, float startAngle)
    {
        var points = new Vector2[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new Vector2(cx + MathF.Cos(angle) * w * 0.5f, cy + MathF.Sin(angle) * h * 0.5f);
        }

        return points;
    }

    private static Vector2[] StarPoints(int points, float cx, float cy, float w, float h, float startAngle)
    {
        var result = new Vector2[points * 2];
        for (var i = 0; i < result.Length; i++)
        {
            var radius = i % 2 == 0 ? 0.5f : 0.23f;
            var angle = startAngle + i * MathF.Tau / result.Length;
            result[i] = new Vector2(cx + MathF.Cos(angle) * w * radius, cy + MathF.Sin(angle) * h * radius);
        }

        return result;
    }

    private static GdiPointF LocalToWorld(StageControl stage, int i, GdiPointF local)
    {
        var angle = stage.Scene.Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new GdiPointF(
            stage.Scene.X[i] + local.X * cos - local.Y * sin,
            stage.Scene.Y[i] + local.X * sin + local.Y * cos);
    }

    private static IEnumerable<EditHandleKind> BoundaryHandles()
    {
        yield return EditHandleKind.BoundsTopLeft;
        yield return EditHandleKind.BoundsTopRight;
        yield return EditHandleKind.BoundsBottomRight;
        yield return EditHandleKind.BoundsBottomLeft;
    }
}
