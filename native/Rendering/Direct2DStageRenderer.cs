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
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), CachedFreehandGeometry> _freehandGeometryCache = new();
    private readonly Dictionary<VectorScene, int> _freehandSceneObjectCounts = new(ReferenceEqualityComparer.Instance);
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _target;
    private ID2D1StrokeStyle? _roundStrokeStyle;
    private SizeI _targetSize;
    private IntPtr _targetHwnd;
    private int _consecutiveFailures;
    private bool _disabled;
    private VectorScene? _cachedEditableScene;
    private VectorScene? _cachedUnderlayScene;

    private sealed record CachedFreehandGeometry(GdiPointF[] Points, ID2D1PathGeometry Geometry) : IDisposable
    {
        public void Dispose() => Geometry.Dispose();
    }

    public bool IsActive => !_disabled && _target is not null;

    internal bool HasCachedFreehandGeometry(VectorScene scene)
    {
        return _freehandGeometryCache.Keys.Any(key => ReferenceEquals(key.Scene, scene));
    }

    public bool TryRender(StageControl stage, out RenderStats stats)
    {
        stats = default;
        if (_disabled || stage.Width <= 0 || stage.Height <= 0) return false;
        var drawingStarted = false;
        var editableScene = stage.Scene;

        try
        {
            EnsureTarget(stage);
            if (_target is null) return false;
            PrepareFreehandGeometryCache(editableScene, stage.UnderlayScene);

            _target.BeginDraw();
            drawingStarted = true;
            _target.Transform = Matrix3x2.Identity;
            _target.AntialiasMode = AntialiasMode.PerPrimitive;
            var background = ToD2D(stage.BackColor);
            _target.Clear(in background);
            if (stage.ReferenceDimension == SceneDimension.ThreeD)
            {
                Draw3DReferenceGrid(stage);
                DrawMarquee(stage);
                var gridResult = _target.EndDraw();
                drawingStarted = false;
                if (gridResult.Failure)
                {
                    RecordFailure();
                    ResetTarget();
                    return false;
                }

                _consecutiveFailures = 0;
                return true;
            }

            DrawGrid(stage);

            var underlayStats = default(RenderStats);
            var objectDrawLimit = ObjectDrawLimit(stage.Zoom);
            if (stage.UnderlayScene is { } underlay)
            {
                stage.Scene = underlay;
                var underlayLimit = UsesObjectRenderer(underlay, stage.Zoom)
                    && UsesObjectRenderer(editableScene, stage.Zoom)
                        ? objectDrawLimit - Math.Min(editableScene.ObjectCount, objectDrawLimit * 3 / 4)
                        : objectDrawLimit;
                underlayStats = DrawScene(stage, underlayLimit);
            }

            stage.Scene = editableScene;
            var editableLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects);
            stats = RenderStats.Combine(underlayStats, DrawScene(stage, editableLimit));

            DrawSelection(stage);
            DrawDrawingPreview(stage);
            DrawFreehandPreview(stage);
            DrawMarquee(stage);

            var result = _target.EndDraw();
            drawingStarted = false;
            if (result.Failure)
            {
                RecordFailure();
                ResetTarget();
                return false;
            }

            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D rendering failed; falling back to GDI renderer", ex);
            if (drawingStarted && _target is not null)
            {
                try
                {
                    _target.EndDraw();
                }
                catch
                {
                    // The render target is discarded below.
                }
            }

            RecordFailure();
            ResetTarget();
            return false;
        }
        finally
        {
            stage.Scene = editableScene;
        }
    }

    public void Resize(System.Drawing.Size clientSize)
    {
        if (_target is null || clientSize.Width <= 0 || clientSize.Height <= 0) return;
        var next = new SizeI(clientSize.Width, clientSize.Height);
        if (next.Width == _targetSize.Width && next.Height == _targetSize.Height) return;
        try
        {
            _target.Resize(next);
            _targetSize = next;
        }
        catch (Exception ex)
        {
            AppLog.Error("Direct2D resize failed; render target will be recreated", ex);
            RecordFailure();
            ResetTarget();
        }
    }

    public void ReleaseTarget() => ResetTarget();

    public void Dispose()
    {
        ResetTarget();
        ClearFreehandGeometryCache();
        _roundStrokeStyle?.Dispose();
        _roundStrokeStyle = null;
        _factory?.Dispose();
        _factory = null;
    }

    private void EnsureTarget(StageControl stage)
    {
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        var nextSize = new SizeI(Math.Max(1, stage.Width), Math.Max(1, stage.Height));
        var nextHwnd = stage.Handle;
        if (_target is not null && _targetHwnd != nextHwnd) ResetTarget();
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
            PresentOptions = PresentOptions.None
        };

        _target = _factory.CreateHwndRenderTarget(renderTargetProperties, hwndProperties);
        _targetSize = nextSize;
        _targetHwnd = nextHwnd;
    }

    private RenderStats DrawScene(StageControl stage, int objectDrawLimit)
    {
        return stage.Scene.ObjectCount < 5000
            ? DrawObjects(stage, objectDrawLimit)
            : stage.Zoom < 0.08f
                ? DrawOverviewTiles(stage)
                : stage.Zoom < 0.18f
                    ? DrawTiles(stage)
                    : DrawObjects(stage, objectDrawLimit);
    }

    private static int ObjectDrawLimit(float zoom) => zoom < 0.35f ? 95_000 : 220_000;

    private static bool UsesObjectRenderer(VectorScene scene, float zoom)
    {
        return scene.ObjectCount > 0 && (scene.ObjectCount < 5000 || zoom >= 0.18f);
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

        return new RenderStats(0, 0, atoms, draws, 0, true);
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

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawObjects(StageControl stage, int drawLimit)
    {
        var scene = stage.Scene;
        var bounds = stage.VisibleWorldBounds();
        _renderOrder.Collect(scene, bounds, stage.Frame);

        var drawn = _renderOrder.Draw(
            scene,
            drawLimit,
            index => DrawObject(stage, index, SceneRenderPass.Fill),
            index => DrawObject(stage, index, SceneRenderPass.Stroke));
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private void DrawObject(StageControl stage, int i, SceneRenderPass pass)
    {
        var scene = stage.Scene;
        var screen = stage.WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Height[i]));
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        var brush = BrushFor(scene.Argb[i]);
        var strokeBrush = BrushFor(scene.StrokeArgb.Length > i ? scene.StrokeArgb[i] : GdiColor.FromArgb(238, 242, 241).ToArgb());
        var screenStroke = Math.Max(0.1f, stage.WorldLengthToScreen(scene.Stroke[i]));

        if (shape == ShapeKind.Path && scene.TryGetPathWorldContours(i, out var pathContours))
        {
            DrawPathObject(stage, pathContours, brush, strokeBrush, scene.Stroke[i], screenStroke, pass);
            return;
        }

        if (IsFreehandShape(shape) && scene.TryGetFreehandLocalPoints(i, out var freehandPoints))
        {
            var freehandBrush = shape == ShapeKind.BrushStroke ? brush : strokeBrush;
            DrawFreehandStroke(stage, i, freehandPoints, freehandBrush);
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (scene.Stroke[i] > 0) DrawBezierLine(stage, i, strokeBrush, screenStroke);
            return;
        }

        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateRotation(scene.Angle[i], new Vector2(screen.X, screen.Y));
            DrawLocalShape(shape, brush, strokeBrush, scene.Stroke[i], screenStroke, screen.X, screen.Y, w, h, pass);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawPathObject(StageControl stage, GdiPointF[][] worldContours, ID2D1SolidColorBrush brush, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, SceneRenderPass pass)
    {
        if (worldContours.Length == 0) return;
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var contour in worldContours)
            {
                if (contour.Length < 3) continue;
                sink.BeginFigure(WorldToVector(stage, contour[0]), FigureBegin.Filled);
                for (var i = 1; i < contour.Length; i++) sink.AddLine(WorldToVector(stage, contour[i]));
                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        if (pass == SceneRenderPass.Fill) _target!.FillGeometry(path, brush);
        else if (stroke > 0) _target!.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private void DrawFreehandStroke(StageControl stage, int objectIndex, GdiPointF[] localPoints, ID2D1SolidColorBrush brush)
    {
        if (localPoints.Length == 0) return;
        var scene = stage.Scene;
        var screenWidth = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Stroke[objectIndex]));
        if (localPoints.Length == 1)
        {
            var point = LocalFreehandPointToScreen(stage, objectIndex, localPoints[0]);
            var dot = new Ellipse(point, screenWidth * 0.5f, screenWidth * 0.5f);
            _target!.FillEllipse(dot, brush);
            return;
        }

        var geometry = FreehandGeometry(scene, objectIndex, localPoints);
        var screen = stage.WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
        var scale = VectorUnits.PixelsPerUnit * stage.Zoom;
        var old = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateRotation(scene.Angle[objectIndex])
                * Matrix3x2.CreateTranslation(screen.X, screen.Y);
            _target.DrawGeometry(geometry, brush, scene.Stroke[objectIndex], RoundStrokeStyle());
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawLocalShape(ShapeKind shape, ID2D1SolidColorBrush brush, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, float cx, float cy, float w, float h, SceneRenderPass pass)
    {
        var rect = Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                var center = new Vector2(cx, cy);
                var ellipse = new Ellipse(in center, w * 0.5f, h * 0.5f);
                if (pass == SceneRenderPass.Fill) _target!.FillEllipse(ellipse, brush);
                else if (stroke > 0 && w > 4 && h > 4) _target!.DrawEllipse(ellipse, strokeBrush, screenStroke);
                break;
            case ShapeKind.Triangle:
                DrawPolygon(RegularPolygonPoints(3, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            case ShapeKind.Polygon:
                DrawPolygon(RegularPolygonPoints(6, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            case ShapeKind.Star:
                DrawPolygon(StarPoints(5, cx, cy, w, h, -MathF.PI / 2), brush, strokeBrush, stroke, screenStroke, pass);
                break;
            default:
                if (pass == SceneRenderPass.Fill) _target!.FillRectangle(in rect, brush);
                else if (stroke > 0 && w > 4 && h > 4) _target!.DrawRectangle(in rect, strokeBrush, screenStroke);
                break;
        }
    }

    private void DrawPolygon(Vector2[] points, ID2D1SolidColorBrush fill, ID2D1SolidColorBrush strokeBrush, float stroke, float screenStroke, SceneRenderPass pass)
    {
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (var i = 1; i < points.Length; i++) sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        if (pass == SceneRenderPass.Fill) _target!.FillGeometry(path, fill);
        else if (stroke > 0) _target!.DrawGeometry(path, strokeBrush, screenStroke);
    }

    private void DrawGrid(StageControl stage)
    {
        var step = Math.Max(24, stage.WorldLengthToScreen(512));
        var origin = stage.WorldToScreen(0, 0);
        var brush = BrushFor(GdiColor.FromArgb(35, 58, 64, 69).ToArgb());
        for (var x = origin.X % step; x < stage.Width; x += step) _target!.DrawLine(new Vector2(x, 0), new Vector2(x, stage.Height), brush, 1);
        for (var y = origin.Y % step; y < stage.Height; y += step) _target!.DrawLine(new Vector2(0, y), new Vector2(stage.Width, y), brush, 1);
    }

    private void Draw3DReferenceGrid(StageControl stage)
    {
        var horizon = stage.Height * 0.42f;
        _target!.DrawLine(new Vector2(0, horizon), new Vector2(stage.Width, horizon), BrushFor(GdiColor.FromArgb(40, 112, 204, 255).ToArgb()), 1);

        var step = InfiniteGridStep(stage);
        var lineRadius = InfiniteGridLineRadius(stage);
        var centerX = SnapToGrid(stage.ReferenceTargetX, step);
        var centerZ = SnapToGrid(stage.ReferenceTargetZ, step);
        var minX = centerX - lineRadius * step;
        var maxX = centerX + lineRadius * step;
        var minZ = centerZ - lineRadius * step;
        var maxZ = centerZ + lineRadius * step;
        var grid = BrushFor(GdiColor.FromArgb(76, 72, 84, 92).ToArgb());
        var center = BrushFor(GdiColor.FromArgb(130, 150, 164, 174).ToArgb());
        var xAxis = BrushFor(GdiColor.FromArgb(220, 255, 92, 92).ToArgb());
        var yAxis = BrushFor(GdiColor.FromArgb(220, 122, 224, 92).ToArgb());
        var zAxis = BrushFor(GdiColor.FromArgb(220, 92, 172, 255).ToArgb());

        for (var offset = -lineRadius; offset <= lineRadius; offset++)
        {
            var z = centerZ + offset * step;
            var x = centerX + offset * step;
            DrawProjectedLine(stage, new Vector3(minX, 0, z), new Vector3(maxX, 0, z), Math.Abs(z) < 0.001f ? center : grid, Math.Abs(z) < 0.001f ? 1.4f : 1f);
            DrawProjectedLine(stage, new Vector3(x, 0, minZ), new Vector3(x, 0, maxZ), Math.Abs(x) < 0.001f ? center : grid, Math.Abs(x) < 0.001f ? 1.4f : 1f);
        }

        DrawProjectedLine(stage, new Vector3(minX, 0, 0), new Vector3(maxX, 0, 0), xAxis, 2);
        DrawProjectedLine(stage, new Vector3(0, 0, minZ), new Vector3(0, 0, maxZ), zAxis, 2);
        DrawProjectedLine(stage, new Vector3(0, stage.ReferenceTargetY - 5000, 0), new Vector3(0, stage.ReferenceTargetY + 5000, 0), yAxis, 2);
    }

    private static int InfiniteGridLineRadius(StageControl stage)
    {
        return Math.Clamp((int)MathF.Ceiling(Math.Max(stage.Width, stage.Height) / 14f), 64, 180);
    }

    private static float InfiniteGridStep(StageControl stage)
    {
        var raw = stage.ReferenceDistance / Math.Max(0.35f, stage.ReferenceZoomScale) / 10f;
        if (raw <= 750) return 500;
        if (raw <= 1500) return 1000;
        if (raw <= 3500) return 2000;
        if (raw <= 7000) return 5000;
        return 10000;
    }

    private static float SnapToGrid(float value, float step) => MathF.Round(value / step) * step;

    private void DrawProjectedLine(StageControl stage, Vector3 a, Vector3 b, ID2D1SolidColorBrush brush, float width)
    {
        var ca = CameraSpacePoint(stage, a);
        var cb = CameraSpacePoint(stage, b);
        if (!ClipNear(ref ca, ref cb)) return;
        var pa = ProjectCameraPoint(stage, ca);
        var pb = ProjectCameraPoint(stage, cb);
        if (!LineMayTouchViewport(stage, pa, pb)) return;
        _target!.DrawLine(pa, pb, brush, width);
    }

    private static Vector3 CameraSpacePoint(StageControl stage, Vector3 point)
    {
        var yawCos = MathF.Cos(stage.ReferenceYaw);
        var yawSin = MathF.Sin(stage.ReferenceYaw);
        var pitchCos = MathF.Cos(stage.ReferencePitch);
        var pitchSin = MathF.Sin(stage.ReferencePitch);

        var worldX = point.X - stage.ReferenceTargetX;
        var worldY = point.Y - stage.ReferenceTargetY;
        var worldZ = point.Z - stage.ReferenceTargetZ;
        var x = worldX * yawCos - worldZ * yawSin;
        var z = worldX * yawSin + worldZ * yawCos;
        var y = worldY;
        var py = y * pitchCos - z * pitchSin;
        var pz = y * pitchSin + z * pitchCos + stage.ReferenceDistance;
        return new Vector3(x, py, pz);
    }

    private static bool ClipNear(ref Vector3 a, ref Vector3 b)
    {
        const float near = 120f;
        var aInside = a.Z >= near;
        var bInside = b.Z >= near;
        if (!aInside && !bInside) return false;
        if (aInside && bInside) return true;
        var t = (near - a.Z) / (b.Z - a.Z);
        var clipped = Vector3.Lerp(a, b, Math.Clamp(t, 0, 1));
        if (aInside) b = clipped;
        else a = clipped;
        return true;
    }

    private static Vector2 ProjectCameraPoint(StageControl stage, Vector3 point)
    {
        var scale = 0.035f * stage.ReferenceZoomScale;
        var perspective = stage.ReferenceProjection == CameraProjection.Perspective ? stage.ReferenceDistance / Math.Max(120f, point.Z) : 1f;
        return new Vector2(
            stage.Width * 0.5f + point.X * scale * perspective,
            stage.Height * 0.58f - point.Y * scale * perspective);
    }

    private static bool LineMayTouchViewport(StageControl stage, Vector2 a, Vector2 b)
    {
        const float margin = 320;
        var minX = MathF.Min(a.X, b.X);
        var maxX = MathF.Max(a.X, b.X);
        var minY = MathF.Min(a.Y, b.Y);
        var maxY = MathF.Max(a.Y, b.Y);
        return maxX >= -margin && minX <= stage.Width + margin && maxY >= -margin && minY <= stage.Height + margin;
    }

    private void DrawSelection(StageControl stage)
    {
        if ((stage.SelectedObject < 0 || stage.SelectedObject >= stage.Scene.ObjectCount) && stage.SelectedObjects.Count == 0) return;

        if (stage.SelectedElements.Count > 0)
        {
            foreach (var objectIndex in stage.SelectedElements
                         .Where(hit => (uint)hit.Key.ObjectIndex < stage.Scene.ObjectCount
                             && hit.Key.Kind == DrawingElementKind.Stroke
                             && stage.Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
                             && stage.Scene.IsObjectActive(hit.Key.ObjectIndex, stage.Frame))
                         .Select(hit => hit.Key.ObjectIndex)
                         .Distinct()
                         .Take(MaxSelectionOutlines))
            {
                DrawBezierSelectionContext(stage, objectIndex);
            }

            var drawnElements = 0;
            foreach (var hit in stage.SelectedElements)
            {
                if ((uint)hit.Key.ObjectIndex >= stage.Scene.ObjectCount
                    || hit.Key == stage.SelectedElement.Key
                    || !stage.Scene.IsObjectActive(hit.Key.ObjectIndex, stage.Frame))
                {
                    continue;
                }

                DrawElementSelectionOutline(stage, hit, primary: false);
                drawnElements++;
                if (drawnElements >= MaxSelectionOutlines) break;
            }

            var primaryElement = stage.SelectedElement;
            if (primaryElement.IsValid
                && (uint)primaryElement.Key.ObjectIndex < stage.Scene.ObjectCount
                && stage.Scene.IsObjectActive(primaryElement.Key.ObjectIndex, stage.Frame))
            {
                DrawElementSelectionOutline(stage, primaryElement, primary: true);
            }

            return;
        }

        var drawn = 0;
        foreach (var index in stage.SelectedObjects)
        {
            if (index == stage.SelectedObject || index < 0 || index >= stage.Scene.ObjectCount) continue;
            if (!stage.Scene.IsObjectActive(index, stage.Frame)) continue;
            DrawSelectionOutline(stage, index, primary: false);
            drawn++;
            if (drawn >= MaxSelectionOutlines) break;
        }

        var primary = stage.SelectedObject;
        if (primary >= 0
            && primary < stage.Scene.ObjectCount
            && stage.Scene.IsObjectActive(primary, stage.Frame))
        {
            DrawSelectionOutline(stage, primary, primary: true);
        }
    }

    private void DrawSelectionOutline(StageControl stage, int i, bool primary)
    {
        var shape = stage.Scene.ShapeKind.Length > i ? stage.Scene.ShapeKind[i] : ShapeKind.Rectangle;
        if (shape == ShapeKind.Line)
        {
            if (primary) DrawBezierGuides(stage, i);
            else DrawBezierOutline(stage, i, primary: false);
            return;
        }

        if (IsFreehandShape(shape))
        {
            var freehandVectors = GetBoundaryVectors(stage, i);
            if (freehandVectors.Length == 1)
            {
                DrawSelectionDot(freehandVectors[0], primary);
            }
            else if (freehandVectors.Length > 1)
            {
                DrawSelectionPolyline(freehandVectors, primary);
            }

            return;
        }

        if (shape == ShapeKind.Path && stage.Scene.TryGetPathWorldContours(i, out var contours))
        {
            foreach (var contour in contours)
            {
                var vectors = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (vectors.Length >= 3) DrawSelectionPolyline(CloseSelectionPolyline(vectors), primary);
            }
        }
        else
        {
            var points = GetBoundaryVectors(stage, i);
            if (points.Length < 2) return;
            DrawSelectionPolyline(points, primary);
        }

        if (!primary || shape == ShapeKind.Path) return;
        foreach (var handle in BoundaryHandles()) DrawHandle(WorldToVector(stage, stage.GetBoundaryHandleWorldPoint(i, handle)), BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 9);
    }

    private void DrawElementSelectionOutline(StageControl stage, DrawingElementHit hit, bool primary)
    {
        var objectIndex = hit.Key.ObjectIndex;
        var shape = stage.Scene.ShapeKind[objectIndex];
        if (hit.Key.Kind == DrawingElementKind.Stroke)
        {
            if (shape == ShapeKind.Line)
            {
                DrawBezierGuides(stage, objectIndex, hit.StartT, hit.EndT, primary);
                return;
            }

            var points = stage.GetSelectedStrokePartPoints(hit)
                .Select(point => WorldToVector(stage, point))
                .ToArray();
            if (points.Length == 1) DrawSelectionDot(points[0], primary);
            else if (points.Length > 1) DrawSelectionPolyline(points, primary);
            return;
        }

        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            foreach (var contour in stage.GetSelectedFillPartContours(hit))
            {
                var points = contour.Select(point => WorldToVector(stage, point)).ToArray();
                if (points.Length >= 3) DrawSelectionPolyline(CloseSelectionPolyline(points), primary);
            }

            return;
        }

        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var points = stage.GetSelectedBoundaryPartPoints(hit)
                .Select(point => WorldToVector(stage, point))
                .ToArray();
            if (points.Length > 1) DrawSelectionPolyline(points, primary);
        }
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
        DrawLocalShape(stage.DrawingPreviewShape, fill, stroke, 0, 0, center.X, center.Y, w, h, SceneRenderPass.Fill);
        var rect = Rect(center.X - w * 0.5f, center.Y - h * 0.5f, w, h);
        _target!.DrawRectangle(in rect, stroke, Math.Max(0.1f, stage.WorldLengthToScreen(stage.DrawingPreviewStroke)));
    }

    private void DrawFreehandPreview(StageControl stage)
    {
        if (!stage.FreehandPreviewVisible || stage.FreehandPreviewPoints.Count == 0) return;
        var brush = BrushFor(stage.FreehandPreviewColor.ToArgb());
        var width = Math.Max(0.75f, stage.WorldLengthToScreen(stage.FreehandPreviewStroke));
        if (stage.FreehandPreviewPoints.Count == 1)
        {
            var point = WorldToVector(stage, stage.FreehandPreviewPoints[0]);
            var dot = new Ellipse(point, width * 0.5f, width * 0.5f);
            _target!.FillEllipse(dot, brush);
            return;
        }

        var previous = WorldToVector(stage, stage.FreehandPreviewPoints[0]);
        for (var i = 1; i < stage.FreehandPreviewPoints.Count; i++)
        {
            var current = WorldToVector(stage, stage.FreehandPreviewPoints[i]);
            _target!.DrawLine(previous, current, brush, width, RoundStrokeStyle());
            previous = current;
        }
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

    private void DrawBezierOutline(StageControl stage, int i, bool primary)
    {
        using var path = BuildBezierPath(stage, i);
        DrawSelectionGeometry(path, primary);
    }

    private void DrawBezierGuides(StageControl stage, int i)
    {
        var (start, control, end) = GetBezierScreenPoints(stage, i);
        using var path = BuildBezierPath(stage, i);
        var guide = BrushFor(GdiColor.FromArgb(190, 112, 204, 255).ToArgb());
        _target!.DrawLine(start, control, guide, 1);
        _target.DrawLine(control, end, guide, 1);
        DrawSelectionGeometry(path, primary: true);
        DrawHandle(start, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(end, BrushFor(GdiColor.FromArgb(255, 255, 240, 168).ToArgb()), 8);
        DrawHandle(control, BrushFor(GdiColor.FromArgb(255, 112, 204, 255).ToArgb()), 10);
    }

    private void DrawBezierGuides(StageControl stage, int i, float startT, float endT, bool primary)
    {
        var (start, control, end) = GetBezierScreenPoints(stage, i);
        using var partialPath = BuildBezierSamplePath(start, control, end, startT, endT);
        DrawSelectionGeometry(partialPath, primary);
    }

    private void DrawBezierSelectionContext(StageControl stage, int i)
    {
        using var fullPath = BuildBezierPath(stage, i);
        _target!.DrawGeometry(fullPath, BrushFor(GdiColor.FromArgb(80, 255, 235, 120).ToArgb()), 1.2f);
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
        DrawSelectionGeometry(partialPath, primary: true);
    }

    private void DrawSelectionGeometry(ID2D1PathGeometry path, bool primary)
    {
        _target!.DrawGeometry(path, BrushFor(SelectionOuterGlowColor(primary).ToArgb()), primary ? 12 : 8);
        _target.DrawGeometry(path, BrushFor(SelectionGlowColor(primary).ToArgb()), primary ? 7 : 5);
        _target.DrawGeometry(path, BrushFor(SelectionLineColor(primary).ToArgb()), primary ? 2.5f : 1.5f);
    }

    private void DrawSelectionPolyline(Vector2[] points, bool primary)
    {
        DrawPolyline(points, BrushFor(SelectionOuterGlowColor(primary).ToArgb()), primary ? 12 : 8);
        DrawPolyline(points, BrushFor(SelectionGlowColor(primary).ToArgb()), primary ? 7 : 5);
        DrawPolyline(points, BrushFor(SelectionLineColor(primary).ToArgb()), primary ? 2.5f : 1.5f);
    }

    private static Vector2[] CloseSelectionPolyline(Vector2[] points)
    {
        if (points.Length == 0 || points[0] == points[^1]) return points;
        var result = new Vector2[points.Length + 1];
        Array.Copy(points, result, points.Length);
        result[^1] = points[0];
        return result;
    }

    private void DrawSelectionDot(Vector2 point, bool primary)
    {
        var outer = primary ? 12f : 8f;
        var glow = primary ? 7f : 5f;
        var line = primary ? 2.5f : 1.5f;
        _target!.FillEllipse(new Ellipse(point, outer * 0.5f, outer * 0.5f), BrushFor(SelectionOuterGlowColor(primary).ToArgb()));
        _target.FillEllipse(new Ellipse(point, glow * 0.5f, glow * 0.5f), BrushFor(SelectionGlowColor(primary).ToArgb()));
        _target.FillEllipse(new Ellipse(point, line * 0.5f, line * 0.5f), BrushFor(SelectionLineColor(primary).ToArgb()));
    }

    private static GdiColor SelectionOuterGlowColor(bool primary) => primary
        ? GdiColor.FromArgb(95, 80, 210, 255)
        : GdiColor.FromArgb(70, 80, 210, 255);

    private static GdiColor SelectionGlowColor(bool primary) => primary
        ? GdiColor.FromArgb(190, 32, 172, 255)
        : GdiColor.FromArgb(135, 32, 172, 255);

    private static GdiColor SelectionLineColor(bool primary) => primary
        ? GdiColor.FromArgb(255, 255, 235, 120)
        : GdiColor.FromArgb(235, 112, 220, 255);

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
        _targetHwnd = IntPtr.Zero;
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3) _disabled = true;
    }

    private void ClearBrushCache()
    {
        foreach (var brush in _brushCache.Values) brush.Dispose();
        _brushCache.Clear();
    }

    private ID2D1PathGeometry FreehandGeometry(VectorScene scene, int objectIndex, GdiPointF[] localPoints)
    {
        var key = (scene, objectIndex);
        _freehandGeometryCache.TryGetValue(key, out var cached);
        if (cached is not null)
        {
            if (ReferenceEquals(cached.Points, localPoints)) return cached.Geometry;
        }

        var geometry = _factory!.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Vector2(localPoints[0].X, localPoints[0].Y), FigureBegin.Hollow);
            for (var i = 1; i < localPoints.Length; i++) sink.AddLine(new Vector2(localPoints[i].X, localPoints[i].Y));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        _freehandGeometryCache[key] = new CachedFreehandGeometry(localPoints, geometry);
        cached?.Dispose();
        return geometry;
    }

    private ID2D1StrokeStyle RoundStrokeStyle()
    {
        if (_roundStrokeStyle is not null) return _roundStrokeStyle;
        var properties = new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 1,
            DashStyle = DashStyle.Solid,
            DashOffset = 0
        };
        _roundStrokeStyle = _factory!.CreateStrokeStyle(properties, Array.Empty<float>());
        return _roundStrokeStyle;
    }

    private void PrepareFreehandGeometryCache(VectorScene editableScene, VectorScene? underlayScene)
    {
        var sceneSetChanged = !ReferenceEquals(_cachedEditableScene, editableScene)
            || !ReferenceEquals(_cachedUnderlayScene, underlayScene);
        var editableShrank = _freehandSceneObjectCounts.TryGetValue(editableScene, out var previousEditableCount)
            && editableScene.ObjectCount < previousEditableCount;
        var underlayShrank = underlayScene is not null
            && _freehandSceneObjectCounts.TryGetValue(underlayScene, out var previousUnderlayCount)
            && underlayScene.ObjectCount < previousUnderlayCount;

        if (sceneSetChanged || editableShrank || underlayShrank)
        {
            List<(VectorScene Scene, int ObjectIndex)>? staleKeys = null;
            foreach (var item in _freehandGeometryCache)
            {
                var scene = item.Key.Scene;
                var isEditable = ReferenceEquals(scene, editableScene);
                var isUnderlay = underlayScene is not null && ReferenceEquals(scene, underlayScene);
                if (!isEditable && !isUnderlay)
                {
                    (staleKeys ??= new List<(VectorScene Scene, int ObjectIndex)>()).Add(item.Key);
                    continue;
                }

                var sceneShrank = (isEditable && editableShrank) || (isUnderlay && underlayShrank);
                if (!sceneShrank) continue;
                if ((uint)item.Key.ObjectIndex < scene.ObjectCount
                    && scene.TryGetFreehandLocalPoints(item.Key.ObjectIndex, out var points)
                    && ReferenceEquals(points, item.Value.Points))
                {
                    continue;
                }

                (staleKeys ??= new List<(VectorScene Scene, int ObjectIndex)>()).Add(item.Key);
            }

            if (staleKeys is not null)
            {
                foreach (var key in staleKeys)
                {
                    if (_freehandGeometryCache.Remove(key, out var cached)) cached.Dispose();
                }
            }
        }

        _freehandSceneObjectCounts.Clear();
        _freehandSceneObjectCounts[editableScene] = editableScene.ObjectCount;
        if (underlayScene is not null) _freehandSceneObjectCounts[underlayScene] = underlayScene.ObjectCount;
        _cachedEditableScene = editableScene;
        _cachedUnderlayScene = underlayScene;
    }

    private void ClearFreehandGeometryCache()
    {
        var cachedItems = _freehandGeometryCache.Values.ToArray();
        _freehandGeometryCache.Clear();
        _freehandSceneObjectCounts.Clear();
        _cachedEditableScene = null;
        _cachedUnderlayScene = null;
        foreach (var item in cachedItems) item.Dispose();
    }

    private static D2DRect Rect(float x, float y, float width, float height) => new(x, y, width, height);

    private static D2DColor ToD2D(GdiColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Vector2 ToVector(GdiPointF point) => new(point.X, point.Y);

    private static Vector2 WorldToVector(StageControl stage, GdiPointF point) => ToVector(stage.WorldToScreen(point.X, point.Y));

    private static Vector2 LocalFreehandPointToScreen(StageControl stage, int objectIndex, GdiPointF local)
    {
        var scene = stage.Scene;
        var angle = scene.Angle[objectIndex];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var world = new GdiPointF(
            scene.X[objectIndex] + local.X * cos - local.Y * sin,
            scene.Y[objectIndex] + local.X * sin + local.Y * cos);
        return WorldToVector(stage, world);
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

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
