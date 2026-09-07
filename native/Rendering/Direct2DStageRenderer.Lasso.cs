using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer : IDisposable
{
    private ID2D1PathGeometry? _lassoPreviewGeometry;
    private long _lassoPreviewGeometryRevision = -1;

    private void DrawLassoPreview(StageControl stage)
    {
        if (!stage.LassoPreviewVisible || stage.LassoPreviewPoints.Count < 2) return;

        var points = stage.LassoPreviewPoints;
        var closed = stage.LassoPreviewClosed && points.Count >= 3;
        var path = LassoPreviewGeometry(stage, points, closed);
        if (path is null) return;

        var fill = BrushFor(System.Drawing.Color.FromArgb(34, 112, 204, 255).ToArgb());
        var stroke = BrushFor(System.Drawing.Color.FromArgb(190, 112, 204, 255).ToArgb());
        if (closed) _target!.FillGeometry(path, fill);
        _target!.DrawGeometry(path, stroke, 1f, RoundStrokeStyle());

        // Keep the origin cue light enough that it does not cover artwork.
        var first = ToLassoVector(points[0]);
        _target.DrawEllipse(new Ellipse(first, 4f, 4f), stroke, 1f);
    }

    private ID2D1PathGeometry? LassoPreviewGeometry(
        StageControl stage,
        IReadOnlyList<System.Drawing.Point> points,
        bool closed)
    {
        if (_lassoPreviewGeometry is not null
            && _lassoPreviewGeometryRevision == stage.LassoPreviewRevision)
        {
            return _lassoPreviewGeometry;
        }

        ClearLassoPreviewGeometry();
        var path = _factory!.CreatePathGeometry();
        try
        {
            using (var sink = path.Open())
            {
                sink.BeginFigure(
                    ToLassoVector(points[0]),
                    closed ? FigureBegin.Filled : FigureBegin.Hollow);
                for (var index = 1; index < points.Count; index++)
                {
                    sink.AddLine(ToLassoVector(points[index]));
                }

                sink.EndFigure(closed ? FigureEnd.Closed : FigureEnd.Open);
                sink.Close();
            }

            _lassoPreviewGeometry = path;
            _lassoPreviewGeometryRevision = stage.LassoPreviewRevision;
            return path;
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private void ClearLassoPreviewGeometry()
    {
        _lassoPreviewGeometry?.Dispose();
        _lassoPreviewGeometry = null;
        _lassoPreviewGeometryRevision = -1;
    }

    private static Vector2 ToLassoVector(System.Drawing.Point point) => new(point.X, point.Y);
}
