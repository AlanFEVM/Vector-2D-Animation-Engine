using System.Numerics;
using Vortice.Direct2D1;
using GdiColor = System.Drawing.Color;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly record struct Reference3DGridCacheKey(
        int Width,
        int Height,
        float Opacity,
        CameraProjection Projection,
        float ProjectionBlend,
        float Yaw,
        float Pitch,
        float Distance,
        float ZoomScale,
        float TargetX,
        float TargetY,
        float TargetZ);

    private sealed class CachedReference3DGrid(
        Reference3DGridCacheKey key,
        ID2D1PathGeometry? horizon,
        ID2D1PathGeometry? grid,
        ID2D1PathGeometry? center,
        ID2D1PathGeometry? xAxis,
        ID2D1PathGeometry? yAxis,
        ID2D1PathGeometry? zAxis) : IDisposable
    {
        public Reference3DGridCacheKey Key { get; } = key;
        public ID2D1PathGeometry? Horizon { get; } = horizon;
        public ID2D1PathGeometry? Grid { get; } = grid;
        public ID2D1PathGeometry? Center { get; } = center;
        public ID2D1PathGeometry? XAxis { get; } = xAxis;
        public ID2D1PathGeometry? YAxis { get; } = yAxis;
        public ID2D1PathGeometry? ZAxis { get; } = zAxis;

        public void Dispose()
        {
            Horizon?.Dispose();
            Grid?.Dispose();
            Center?.Dispose();
            XAxis?.Dispose();
            YAxis?.Dispose();
            ZAxis?.Dispose();
        }
    }

    private readonly record struct Reference3DGridSegment(Vector2 Start, Vector2 End);

    private CachedReference3DGrid? _reference3DGrid;

    private void DrawBatched3DReferenceGrid(StageControl stage)
    {
        if (stage.ReferenceWorldGridOpacity <= 0.001f) return;

        var key = new Reference3DGridCacheKey(
            stage.Width,
            stage.Height,
            stage.ReferenceWorldGridOpacity,
            stage.EffectiveReferenceProjection,
            stage.ReferenceProjectionBlend,
            stage.EffectiveReferenceYaw,
            stage.EffectiveReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ);
        if (_reference3DGrid is null || _reference3DGrid.Key != key)
        {
            var next = CreateReference3DGrid(stage, key);
            _reference3DGrid?.Dispose();
            _reference3DGrid = next;
        }

        var cached = _reference3DGrid;
        if (cached is null) return;

        DrawReference3DGridGeometry(
            cached.Horizon,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 40),
                112,
                204,
                255),
            1f);
        DrawReference3DGridGeometry(
            cached.Grid,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 76),
                72,
                84,
                92),
            1f);
        DrawReference3DGridGeometry(
            cached.Center,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 130),
                150,
                164,
                174),
            1.4f);
        DrawReference3DGridGeometry(
            cached.XAxis,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 220),
                255,
                92,
                92),
            2f);
        DrawReference3DGridGeometry(
            cached.YAxis,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 220),
                122,
                224,
                92),
            2f);
        DrawReference3DGridGeometry(
            cached.ZAxis,
            GdiColor.FromArgb(
                GridAlpha(stage.ReferenceWorldGridOpacity, 220),
                92,
                172,
                255),
            2f);
    }

    private void DrawReference3DGridGeometry(
        ID2D1PathGeometry? geometry,
        GdiColor color,
        float width)
    {
        if (geometry is null) return;
        _target!.DrawGeometry(geometry, BrushFor(color.ToArgb()), width);
    }

    private CachedReference3DGrid CreateReference3DGrid(
        StageControl stage,
        Reference3DGridCacheKey key)
    {
        var horizon = new List<Reference3DGridSegment>(1);
        var grid = new List<Reference3DGridSegment>();
        var center = new List<Reference3DGridSegment>();
        var xAxis = new List<Reference3DGridSegment>(1);
        var yAxis = new List<Reference3DGridSegment>(1);
        var zAxis = new List<Reference3DGridSegment>(1);

        var horizonY = stage.Height * 0.42f;
        horizon.Add(new Reference3DGridSegment(
            new Vector2(0, horizonY),
            new Vector2(stage.Width, horizonY)));

        var step = InfiniteGridStep(stage);
        var lineRadius = InfiniteGridLineRadius(stage);
        var centerX = SnapToGrid(stage.ReferenceTargetX, step);
        var centerY = SnapToGrid(stage.ReferenceTargetY, step);
        var minX = centerX - lineRadius * step;
        var maxX = centerX + lineRadius * step;
        var minY = centerY - lineRadius * step;
        var maxY = centerY + lineRadius * step;

        for (var offset = -lineRadius; offset <= lineRadius; offset++)
        {
            var y = centerY + offset * step;
            var x = centerX + offset * step;
            var destination = Math.Abs(y) < 0.001f ? center : grid;
            AddProjectedReference3DGridLine(
                stage,
                new Vector3(minX, y, 0),
                new Vector3(maxX, y, 0),
                destination);
            destination = Math.Abs(x) < 0.001f ? center : grid;
            AddProjectedReference3DGridLine(
                stage,
                new Vector3(x, minY, 0),
                new Vector3(x, maxY, 0),
                destination);
        }

        AddProjectedReference3DGridLine(
            stage,
            new Vector3(minX, 0, 0),
            new Vector3(maxX, 0, 0),
            xAxis);
        AddProjectedReference3DGridLine(
            stage,
            new Vector3(0, minY, 0),
            new Vector3(0, maxY, 0),
            yAxis);
        AddProjectedReference3DGridLine(
            stage,
            new Vector3(0, 0, -5000),
            new Vector3(0, 0, 5000),
            zAxis);

        return new CachedReference3DGrid(
            key,
            CreateReference3DGridGeometry(horizon),
            CreateReference3DGridGeometry(grid),
            CreateReference3DGridGeometry(center),
            CreateReference3DGridGeometry(xAxis),
            CreateReference3DGridGeometry(yAxis),
            CreateReference3DGridGeometry(zAxis));
    }

    private void AddProjectedReference3DGridLine(
        StageControl stage,
        Vector3 start,
        Vector3 end,
        ICollection<Reference3DGridSegment> destination)
    {
        var cameraStart = CameraSpacePoint(stage, start);
        var cameraEnd = CameraSpacePoint(stage, end);
        if (!ClipNear(ref cameraStart, ref cameraEnd)) return;
        var screenStart = ProjectCameraPoint(stage, cameraStart);
        var screenEnd = ProjectCameraPoint(stage, cameraEnd);
        if (!LineMayTouchViewport(stage, screenStart, screenEnd)) return;
        destination.Add(new Reference3DGridSegment(screenStart, screenEnd));
    }

    private ID2D1PathGeometry? CreateReference3DGridGeometry(
        IReadOnlyList<Reference3DGridSegment> segments)
    {
        if (segments.Count == 0) return null;
        var geometry = _factory!.CreatePathGeometry();
        try
        {
            using var sink = geometry.Open();
            foreach (var segment in segments)
            {
                sink.BeginFigure(segment.Start, FigureBegin.Hollow);
                sink.AddLine(segment.End);
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
            return geometry;
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
    }

    private void ClearReference3DGridCache()
    {
        _reference3DGrid?.Dispose();
        _reference3DGrid = null;
    }
}
