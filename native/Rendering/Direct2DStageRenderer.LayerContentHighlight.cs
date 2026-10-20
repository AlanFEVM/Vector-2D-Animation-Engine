using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const int MinCachedLayerContentHighlightObjects = 24;

    private ID2D1CommandList? _layerContentHighlightCommands;
    private LayerContentHighlightCommandKey _layerContentHighlightCommandKey;
    private PointF _layerContentHighlightCommandCamera;
    private float _layerContentHighlightCommandZoom;
    internal long LayerContentHighlightCommandBuilds { get; private set; }
    internal long LayerContentHighlightCommandReuses { get; private set; }

    private readonly record struct LayerContentHighlightCommandKey(
        StageControl Stage,
        VectorScene Scene,
        long StateRevision,
        int ColorArgb,
        long GeometryRevision,
        long SummaryRevision,
        long ActiveContentRevision,
        int Frame,
        long VisibilityHash,
        System.Drawing.Size Size);

    private void DrawLayerContentHighlight(StageControl stage)
    {
        if (!stage.LayerContentHighlightVisible || stage.RendersReferenceProjection) return;
        if (TryDrawCachedLayerContentHighlight(stage)) return;
        DrawLayerContentHighlightCore(stage);
    }

    private bool TryDrawCachedLayerContentHighlight(StageControl stage)
    {
        var objects = stage.LayerContentHighlightObjects;
        if (objects.Count < MinCachedLayerContentHighlightObjects
            || _target is not ID2D1DeviceContext context)
        {
            ClearLayerContentHighlightCommands();
            return false;
        }

        var key = new LayerContentHighlightCommandKey(
            stage,
            stage.Scene,
            stage.LayerContentHighlightRevision,
            stage.LayerContentHighlightColor.ToArgb(),
            stage.Scene.GeometryRevision,
            stage.Scene.SummaryRevision,
            stage.Scene.ActiveContentRevision,
            stage.Frame,
            ComputeLayerContentHighlightVisibilityHash(stage),
            stage.Size);
        if (_layerContentHighlightCommands is null || key != _layerContentHighlightCommandKey)
        {
            ClearLayerContentHighlightCommands();
            using var previousTarget = context.Target;
            var previousTransform = context.Transform;
            context.EndDraw().CheckError();
            var commands = context.CreateCommandList();
            try
            {
                context.Target = commands;
                context.BeginDraw();
                try { DrawLayerContentHighlightCore(stage); }
                finally { context.EndDraw().CheckError(); }
                commands.Close().CheckError();
                _layerContentHighlightCommands = commands;
                _layerContentHighlightCommandKey = key;
                _layerContentHighlightCommandCamera = new PointF(stage.CameraX, stage.CameraY);
                _layerContentHighlightCommandZoom = stage.Zoom;
                LayerContentHighlightCommandBuilds++;
            }
            catch
            {
                commands.Dispose();
                throw;
            }
            finally
            {
                context.Target = previousTarget;
                context.Transform = previousTransform;
                context.BeginDraw();
            }
        }
        else
        {
            LayerContentHighlightCommandReuses++;
        }

        var scale = stage.Zoom / _layerContentHighlightCommandZoom;
        var translateX = stage.Width * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_layerContentHighlightCommandCamera.X - stage.CameraX) * stage.Zoom;
        var translateY = stage.Height * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_layerContentHighlightCommandCamera.Y - stage.CameraY) * stage.Zoom;
        var transform = context.Transform;
        try
        {
            context.Transform = transform
                * Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateTranslation(translateX, translateY);
            context.DrawImage(_layerContentHighlightCommands);
        }
        finally
        {
            context.Transform = transform;
        }
        return true;
    }

    private void DrawLayerContentHighlightCore(StageControl stage)
    {
        var color = stage.LayerContentHighlightColor;
        var glow = BrushFor(GdiColor.FromArgb(48, color).ToArgb());
        var line = BrushFor(GdiColor.FromArgb(220, color).ToArgb());
        foreach (var objectIndex in stage.LayerContentHighlightObjects)
        {
            if ((uint)objectIndex >= stage.Scene.ObjectCount
                || !stage.IsLayerContentHighlightObject(objectIndex)
                || !stage.Scene.IsObjectActive(objectIndex, stage.Frame)
                || !stage.Scene.ShouldRenderLayerContent(stage.Scene.ObjectLayer[objectIndex]))
            {
                continue;
            }

            DrawLayerContentHighlightObject(stage, objectIndex, glow, line);
        }
    }

    private void DrawLayerContentHighlightObject(
        StageControl stage,
        int objectIndex,
        ID2D1SolidColorBrush glow,
        ID2D1SolidColorBrush line)
    {
        var shape = stage.Scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.Line)
        {
            if (!stage.Scene.TryGetLineCubic(
                    objectIndex,
                    out var start,
                    out var control1,
                    out var control2,
                    out var end))
            {
                return;
            }

            using var path = _factory!.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(WorldToVector(stage, start), FigureBegin.Hollow);
                sink.AddBezier(new BezierSegment(
                    WorldToVector(stage, control1),
                    WorldToVector(stage, control2),
                    WorldToVector(stage, end)));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            DrawLayerContentHighlightGeometry(path, glow, line);
            return;
        }

        if (shape == ShapeKind.MixingStroke)
        {
            foreach (var contour in stage.GetMixingStrokeScreenContours(stage.Scene, objectIndex))
            {
                DrawLayerContentHighlightPolyline(ToVectors(contour), closed: false, glow, line);
            }
            return;
        }

        if (shape == ShapeKind.Path
            && !stage.Scene.HasFill(objectIndex)
            && stage.Scene.TryGetPathWorldContours(objectIndex, out var pathContours))
        {
            foreach (var contour in pathContours)
            {
                if (contour.Length == 0) continue;
                DrawLayerContentHighlightPolyline(ToVectors(stage, contour), closed: false, glow, line);
            }
            return;
        }

        var closed = !IsFreehandShape(shape);
        foreach (var contour in stage.GetLayerContentHighlightWorldContours(objectIndex))
        {
            if (contour.Length == 0) continue;
            DrawLayerContentHighlightPolyline(ToVectors(stage, contour), closed, glow, line);
        }
    }

    private void DrawLayerContentHighlightPolyline(
        Vector2[] points,
        bool closed,
        ID2D1SolidColorBrush glow,
        ID2D1SolidColorBrush line)
    {
        if (points.Length == 0) return;
        if (points.Length == 1)
        {
            _target!.FillEllipse(new Ellipse(points[0], 2.4f, 2.4f), line);
            return;
        }

        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Hollow);
            for (var index = 1; index < points.Length; index++) sink.AddLine(points[index]);
            sink.EndFigure(closed && points.Length >= 3 ? FigureEnd.Closed : FigureEnd.Open);
            sink.Close();
        }
        DrawLayerContentHighlightGeometry(path, glow, line);
    }

    private void DrawLayerContentHighlightGeometry(
        ID2D1PathGeometry geometry,
        ID2D1SolidColorBrush glow,
        ID2D1SolidColorBrush line)
    {
        var strokeStyle = LineStrokeStyle(CapStyle.Round, CapStyle.Round, miterJoin: false);
        _target!.DrawGeometry(geometry, glow, 5f, strokeStyle);
        _target.DrawGeometry(geometry, line, 1.4f, strokeStyle);
    }

    private static Vector2[] ToVectors(GdiPointF[] points)
    {
        var result = new Vector2[points.Length];
        for (var index = 0; index < points.Length; index++) result[index] = ToVector(points[index]);
        return result;
    }

    private static Vector2[] ToVectors(StageControl stage, GdiPointF[] points)
    {
        var result = new Vector2[points.Length];
        for (var index = 0; index < points.Length; index++) result[index] = WorldToVector(stage, points[index]);
        return result;
    }

    private static long ComputeLayerContentHighlightVisibilityHash(StageControl stage)
    {
        var hash = 14695981039346656037UL;
        foreach (var objectIndex in stage.LayerContentHighlightObjects)
        {
            hash = (hash ^ (uint)objectIndex) * 1099511628211UL;
            var active = (uint)objectIndex < stage.Scene.ObjectCount
                && stage.IsLayerContentHighlightObject(objectIndex)
                && stage.Scene.IsObjectActive(objectIndex, stage.Frame)
                && stage.Scene.ShouldRenderLayerContent(stage.Scene.ObjectLayer[objectIndex]);
            hash = (hash ^ (active ? 1UL : 0UL)) * 1099511628211UL;
        }
        return (long)hash;
    }

    private void ClearLayerContentHighlightCommands()
    {
        _layerContentHighlightCommands?.Dispose();
        _layerContentHighlightCommands = null;
        _layerContentHighlightCommandKey = default;
        _layerContentHighlightCommandZoom = 0;
    }

    internal void InvalidateLayerContentHighlightCommands() => ClearLayerContentHighlightCommands();
}
