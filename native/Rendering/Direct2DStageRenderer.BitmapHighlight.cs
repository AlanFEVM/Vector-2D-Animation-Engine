using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    // Clip the stroke centerline beyond all glow/AA coverage. Keep each
    // object's two original draws and joins; artificial caps remain offscreen.
    private bool DrawClippedBitmapHighlight(Vector2[] points, ID2D1SolidColorBrush glow, ID2D1SolidColorBrush line)
        => DrawClippedBitmapOutline(points, glow, 5f, line, 1.4f, _bitmapHighlightViewport,
            LineStrokeStyle(CapStyle.Round, CapStyle.Round, miterJoin: false));

    private bool DrawClippedBitmapOutline(Vector2[] points, ID2D1SolidColorBrush firstBrush, float firstWidth,
        ID2D1SolidColorBrush? secondBrush, float secondWidth, RectangleF bounds, ID2D1StrokeStyle? strokeStyle)
    {
        var firstOutside = Array.FindIndex(points, point => !bounds.Contains(point.X, point.Y));
        if (firstOutside < 0) return false;
        if (secondBrush is not null && (points.Length == 4 || points.Length == 5 && points[0] == points[4])
            && Enumerable.Range(0, 4).All(index => points[index].X == points[(index + 1) % 4].X
                || points[index].Y == points[(index + 1) % 4].Y))
        {
            var left = points.Min(point => point.X); var right = points.Max(point => point.X);
            var top = points.Min(point => point.Y); var bottom = points.Max(point => point.Y);
            if (right - left > 16 && bottom - top > 16
                && points.All(point => !bounds.Contains(point.X, point.Y)))
            {
                var segments = new List<(Vector2 Start, Vector2 End)>();
                for (var index = 0; index < 4; index++)
                {
                    var start = points[index]; var end = points[(index + 1) % 4];
                    if (ClipHighlightSegment(bounds, ref start, ref end)) segments.Add((start, end));
                }
                foreach (var (start, end) in segments)
                    _target!.DrawLine(start, end, firstBrush, firstWidth, strokeStyle);
                if (secondBrush is not null)
                    foreach (var (start, end) in segments) _target!.DrawLine(start, end, secondBrush, secondWidth, strokeStyle);
                return true;
            }
        }
        using var path = _factory!.CreatePathGeometry();
        using (var sink = path.Open())
        {
            var open = false;
            var previous = Vector2.Zero;
            for (var step = 0; step < points.Length; step++)
            {
                var start = points[(firstOutside + step) % points.Length];
                var end = points[(firstOutside + step + 1) % points.Length];
                if (!ClipHighlightSegment(bounds, ref start, ref end))
                {
                    if (open) { sink.EndFigure(FigureEnd.Open); open = false; }
                    continue;
                }
                if (!open || previous != start)
                {
                    if (open) sink.EndFigure(FigureEnd.Open);
                    sink.BeginFigure(start, FigureBegin.Hollow); open = true;
                }
                sink.AddLine(end); previous = end;
            }
            if (open) sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        _target!.DrawGeometry(path, firstBrush, firstWidth, strokeStyle);
        if (secondBrush is not null) _target.DrawGeometry(path, secondBrush, secondWidth, strokeStyle);
        return true;
    }

    private static bool ClipHighlightSegment(RectangleF bounds, ref Vector2 start, ref Vector2 end)
    {
        var origin = start;
        var delta = end - start;
        var minimum = 0d; var maximum = 1d;
        bool Clip(double direction, double distance)
        {
            if (direction == 0) return distance >= 0;
            var ratio = distance / direction;
            if (direction < 0) { if (ratio > maximum) return false; minimum = Math.Max(minimum, ratio); }
            else { if (ratio < minimum) return false; maximum = Math.Min(maximum, ratio); }
            return true;
        }
        if (!Clip(-delta.X, origin.X - bounds.Left) || !Clip(delta.X, bounds.Right - origin.X)
            || !Clip(-delta.Y, origin.Y - bounds.Top) || !Clip(delta.Y, bounds.Bottom - origin.Y)) return false;
        start = minimum == 0 ? origin : origin + delta * (float)minimum;
        end = maximum == 1 ? end : origin + delta * (float)maximum;
        return true;
    }
}
