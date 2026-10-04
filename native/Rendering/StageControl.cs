using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    float WorldGridOpacity,
    WorldGridType WorldGridType);

internal readonly record struct TransformOverlayFrame(PointF Origin, PointF AxisX, PointF AxisY)
{
    public PointF TopLeft => Origin;
    public PointF TopRight => Add(Origin, AxisX);
    public PointF BottomLeft => Add(Origin, AxisY);
    public PointF BottomRight => Add(TopRight, AxisY);
    public PointF Center => new(
        Origin.X + (AxisX.X + AxisY.X) * 0.5f,
        Origin.Y + (AxisX.Y + AxisY.Y) * 0.5f);

    public RectangleF Bounds
    {
        get
        {
            var topRight = TopRight;
            var bottomRight = BottomRight;
            var bottomLeft = BottomLeft;
            var left = Math.Min(Math.Min(Origin.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X));
            var top = Math.Min(Math.Min(Origin.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y));
            var right = Math.Max(Math.Max(Origin.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X));
            var bottom = Math.Max(Math.Max(Origin.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y));
            return RectangleF.FromLTRB(left, top, right, bottom);
        }
    }

    public bool IsValid
    {
        get
        {
            if (!IsFinite(Origin) || !IsFinite(AxisX) || !IsFinite(AxisY)) return false;
            var lengthXSquared = AxisX.X * AxisX.X + AxisX.Y * AxisX.Y;
            var lengthYSquared = AxisY.X * AxisY.X + AxisY.Y * AxisY.Y;
            var determinant = AxisX.X * AxisY.Y - AxisX.Y * AxisY.X;
            return lengthXSquared > 0.000001f
                && lengthYSquared > 0.000001f
                && Math.Abs(determinant) > 0.000001f;
        }
    }

    public static TransformOverlayFrame FromBounds(RectangleF bounds) => new(
        new PointF(bounds.Left, bounds.Top),
        new PointF(bounds.Width, 0),
        new PointF(0, bounds.Height));

    private static PointF Add(PointF left, PointF right) => new(left.X + right.X, left.Y + right.Y);
    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}

internal readonly record struct TransformOverlayHandleGeometry(
    TransformHandleKind Kind,
    PointF Point,
    PointF Anchor);

internal readonly record struct TransformOverlayScreenGeometry(
    PointF TopLeft,
    PointF TopRight,
    PointF BottomRight,
    PointF BottomLeft,
    TransformOverlayHandleGeometry[] ResizeHandles,
    TransformOverlayHandleGeometry[] RotationHandles,
    TransformOverlayHandleGeometry[] SkewHandles)
{
    public bool Contains(PointF point)
    {
        var hasPositive = false;
        var hasNegative = false;
        CheckEdge(TopLeft, TopRight, point, ref hasPositive, ref hasNegative);
        CheckEdge(TopRight, BottomRight, point, ref hasPositive, ref hasNegative);
        CheckEdge(BottomRight, BottomLeft, point, ref hasPositive, ref hasNegative);
        CheckEdge(BottomLeft, TopLeft, point, ref hasPositive, ref hasNegative);
        return !(hasPositive && hasNegative);
    }

    private static void CheckEdge(
        PointF start,
        PointF end,
        PointF point,
        ref bool hasPositive,
        ref bool hasNegative)
    {
        var cross = (end.X - start.X) * (point.Y - start.Y)
            - (end.Y - start.Y) * (point.X - start.X);
        if (cross > 0.01f) hasPositive = true;
        else if (cross < -0.01f) hasNegative = true;
    }
}

internal readonly record struct DistortOverlayBezierSegment(
    DistortSide Side,
    Guid StartAnchorId,
    Guid EndAnchorId,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct DistortOverlayVisualHandle(
    DistortHandleRef Reference,
    PointF Point,
    PointF Anchor);

internal readonly record struct DistortOverlayScreenGeometry(
    PointF[][] BoundarySides,
    PointF[] BoundaryContour,
    DistortOverlayBezierSegment[] BezierSegments,
    DistortOverlayVisualHandle[] VisualHandles,
    TransformOverlayHandleGeometry[] Handles)
{
    public PointF TopLeft => BoundarySides is { Length: > 0 } && BoundarySides[0].Length > 0
        ? BoundarySides[0][0]
        : PointF.Empty;
    public PointF TopRight => BoundarySides is { Length: > 0 } && BoundarySides[0].Length > 0
        ? BoundarySides[0][^1]
        : PointF.Empty;
    public PointF BottomRight => BoundarySides is { Length: > 2 } && BoundarySides[2].Length > 0
        ? BoundarySides[2][^1]
        : PointF.Empty;
    public PointF BottomLeft => BoundarySides is { Length: > 2 } && BoundarySides[2].Length > 0
        ? BoundarySides[2][0]
        : PointF.Empty;

    public bool Contains(PointF point)
    {
        if (BoundaryContour is not { Length: >= 3 }) return false;
        var inside = false;
        for (var index = 0; index < BoundaryContour.Length; index++)
        {
            var previous = BoundaryContour[(index + BoundaryContour.Length - 1) % BoundaryContour.Length];
            var current = BoundaryContour[index];
            if ((current.Y > point.Y) == (previous.Y > point.Y)) continue;
            var denominator = previous.Y - current.Y;
            if (Math.Abs(denominator) < 0.0001f) continue;
            var crossingX = (previous.X - current.X) * (point.Y - current.Y) / denominator + current.X;
            if (point.X < crossingX) inside = !inside;
        }

        return inside;
    }
}

internal readonly record struct CubicDrawingPreviewSegment(
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct FillEdgeBezierOverlaySegment(
    int PartIndex,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End,
    float SourceStartT = 0,
    float SourceEndT = 1);

internal readonly record struct FillEdgeBezierOverlayHit(
    int PartIndex,
    EditHandleKind Handle,
    float SourceStartT = 0,
    float SourceEndT = 1)
{
    public bool IsValid => PartIndex >= 0;
    public static FillEdgeBezierOverlayHit None => new(-1, EditHandleKind.None);
}

internal enum SelectionHighlightKind
{
    Fill,
    Stroke
}

internal enum MixingStrokeCoverageCellKind
{
    Circle,
    Start,
    Interior,
    End
}

internal readonly record struct MixingStrokeCoverageCell(
    int Argb,
    MixingStrokeCoverageCellKind Kind,
    PointF Center,
    float Radius,
    PointF Tangent,
    PointF PreviousLeft,
    PointF PreviousRight,
    PointF CenterLeft,
    PointF CenterRight,
    PointF NextLeft,
    PointF NextRight,
    RectangleF Bounds)
{
    private const float QuarterCircleBezier = 0.55228475f;

    internal bool Contains(PointF point)
    {
        if (!Bounds.Contains(point)) return false;
        if (Kind == MixingStrokeCoverageCellKind.Circle)
        {
            return DistanceSquared(point, Center) <= Radius * Radius;
        }

        var offsetX = point.X - Center.X;
        var offsetY = point.Y - Center.Y;
        var tangentDistance = offsetX * Tangent.X + offsetY * Tangent.Y;
        if (Kind == MixingStrokeCoverageCellKind.Start
            && tangentDistance <= 0
            && DistanceSquared(point, Center) <= Radius * Radius)
        {
            return true;
        }
        if (Kind == MixingStrokeCoverageCellKind.End
            && tangentDistance >= 0
            && DistanceSquared(point, Center) <= Radius * Radius)
        {
            return true;
        }

        return Kind switch
        {
            MixingStrokeCoverageCellKind.Start => PointInPolygon(
                point,
                [CenterLeft, NextLeft, NextRight, CenterRight]),
            MixingStrokeCoverageCellKind.Interior => PointInPolygon(
                point,
                [PreviousLeft, CenterLeft, NextLeft, NextRight, CenterRight, PreviousRight]),
            MixingStrokeCoverageCellKind.End => PointInPolygon(
                point,
                [PreviousLeft, CenterLeft, CenterRight, PreviousRight]),
            _ => false
        };
    }

    internal bool Intersects(float width, float height) =>
        Bounds.Right >= 0
        && Bounds.Left <= width
        && Bounds.Bottom >= 0
        && Bounds.Top <= height;

    internal void GetCapCurve(
        out PointF control1,
        out PointF control2,
        out PointF midpoint,
        out PointF control3,
        out PointF control4)
    {
        var direction = Kind == MixingStrokeCoverageCellKind.Start ? -1f : 1f;
        var tangentRadius = Radius * direction;
        var normal = new PointF(-Tangent.Y, Tangent.X);
        var normalControl = Radius * QuarterCircleBezier;
        midpoint = new PointF(
            Center.X + Tangent.X * tangentRadius,
            Center.Y + Tangent.Y * tangentRadius);
        control1 = new PointF(
            CenterLeft.X + Tangent.X * tangentRadius * QuarterCircleBezier,
            CenterLeft.Y + Tangent.Y * tangentRadius * QuarterCircleBezier);
        control2 = new PointF(
            midpoint.X + normal.X * normalControl,
            midpoint.Y + normal.Y * normalControl);
        control3 = new PointF(
            midpoint.X - normal.X * normalControl,
            midpoint.Y - normal.Y * normalControl);
        control4 = new PointF(
            CenterRight.X + Tangent.X * tangentRadius * QuarterCircleBezier,
            CenterRight.Y + Tangent.Y * tangentRadius * QuarterCircleBezier);
    }

    private static bool PointInPolygon(PointF point, ReadOnlySpan<PointF> polygon)
    {
        var inside = false;
        for (var current = 0; current < polygon.Length; current++)
        {
            var previous = current == 0 ? polygon.Length - 1 : current - 1;
            var start = polygon[previous];
            var end = polygon[current];
            if (DistanceToSegmentSquared(point, start, end) <= 0.000001f) return true;
            if ((start.Y > point.Y) == (end.Y > point.Y)) continue;

            var intersectionX = start.X
                + (point.Y - start.Y) * (end.X - start.X) / (end.Y - start.Y);
            if (point.X <= intersectionX) inside = !inside;
        }
        return inside;
    }

    private static float DistanceToSegmentSquared(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return DistanceSquared(point, start);
        var amount = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0f,
            1f);
        var nearest = new PointF(start.X + dx * amount, start.Y + dy * amount);
        return DistanceSquared(point, nearest);
    }

    private static float DistanceSquared(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }
}

internal static class MixingStrokeCoverageBuilder
{
    private const float MinimumScreenDiameter = 0.75f;
    private static readonly ConditionalWeakTable<MixingBrushTrajectorySample[], CachedLocalCells> LocalCellCache = new();

    private sealed record CachedLocalCells(MixingStrokeCoverageCell[] Cells);

    private readonly record struct PreparedSample(
        MixingBrushTrajectorySample Source,
        PointF Center,
        float Radius,
        bool IsValid);

    private readonly record struct Join(
        PointF Left,
        PointF Right,
        PointF Tangent);

    internal static List<MixingStrokeCoverageCell> Build(
        MixingBrushTrajectorySample[] samples,
        Func<PointF, PointF> pointToScreen,
        Func<float, float> diameterToScreen)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(pointToScreen);
        ArgumentNullException.ThrowIfNull(diameterToScreen);

        var prepared = new PreparedSample[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var source = samples[index];
            if (!IsRenderable(source) || ((uint)source.Argb >> 24) == 0) continue;

            var center = pointToScreen(source.Point);
            var screenDiameter = diameterToScreen(source.Diameter);
            if (!IsFinite(center) || !float.IsFinite(screenDiameter) || screenDiameter <= 0) continue;

            prepared[index] = new PreparedSample(
                source,
                center,
                Math.Max(MinimumScreenDiameter, screenDiameter) * 0.5f,
                true);
        }

        var cells = new List<MixingStrokeCoverageCell>(samples.Length);
        var runStart = 0;
        while (runStart < prepared.Length)
        {
            if (!prepared[runStart].IsValid)
            {
                runStart++;
                continue;
            }

            var runEnd = runStart;
            while (runEnd + 1 < prepared.Length
                && prepared[runEnd + 1].IsValid
                && TouchOrOverlap(prepared[runEnd].Source, prepared[runEnd + 1].Source))
            {
                runEnd++;
            }

            AppendRun(cells, prepared, runStart, runEnd);
            runStart = runEnd + 1;
        }

        return cells;
    }

    internal static IReadOnlyList<MixingStrokeCoverageCell> GetLocalCells(
        MixingBrushTrajectorySample[] samples) =>
        LocalCellCache.GetValue(
            samples,
            static source => new CachedLocalCells(Build(
                source,
                static point => point,
                static diameter => diameter).ToArray())).Cells;

    private static void AppendRun(
        List<MixingStrokeCoverageCell> cells,
        PreparedSample[] samples,
        int runStart,
        int runEnd)
    {
        if (runStart == runEnd)
        {
            var sample = samples[runStart];
            cells.Add(new MixingStrokeCoverageCell(
                sample.Source.Argb,
                MixingStrokeCoverageCellKind.Circle,
                sample.Center,
                sample.Radius,
                new PointF(1, 0),
                default,
                default,
                default,
                default,
                default,
                default,
                CircleBounds(sample.Center, sample.Radius)));
            return;
        }

        var joins = new Join[runEnd - runStart];
        for (var sampleIndex = runStart; sampleIndex < runEnd; sampleIndex++)
        {
            var current = samples[sampleIndex];
            var next = samples[sampleIndex + 1];
            var tangent = ResolveTangent(samples, runStart, runEnd, sampleIndex);
            var normal = new PointF(-tangent.Y, tangent.X);
            var midpoint = new PointF(
                (current.Center.X + next.Center.X) * 0.5f,
                (current.Center.Y + next.Center.Y) * 0.5f);
            var radius = (current.Radius + next.Radius) * 0.5f;
            joins[sampleIndex - runStart] = new Join(
                new PointF(midpoint.X + normal.X * radius, midpoint.Y + normal.Y * radius),
                new PointF(midpoint.X - normal.X * radius, midpoint.Y - normal.Y * radius),
                tangent);
        }

        for (var sampleIndex = runStart; sampleIndex <= runEnd; sampleIndex++)
        {
            var sample = samples[sampleIndex];
            var relativeIndex = sampleIndex - runStart;
            var kind = sampleIndex == runStart
                ? MixingStrokeCoverageCellKind.Start
                : sampleIndex == runEnd
                    ? MixingStrokeCoverageCellKind.End
                    : MixingStrokeCoverageCellKind.Interior;
            var previous = relativeIndex > 0 ? joins[relativeIndex - 1] : default;
            var next = relativeIndex < joins.Length ? joins[relativeIndex] : default;
            var tangent = kind switch
            {
                MixingStrokeCoverageCellKind.Start => next.Tangent,
                MixingStrokeCoverageCellKind.End => previous.Tangent,
                _ => CenterTangent(previous.Tangent, next.Tangent)
            };
            var normal = new PointF(-tangent.Y, tangent.X);
            var centerLeft = new PointF(
                sample.Center.X + normal.X * sample.Radius,
                sample.Center.Y + normal.Y * sample.Radius);
            var centerRight = new PointF(
                sample.Center.X - normal.X * sample.Radius,
                sample.Center.Y - normal.Y * sample.Radius);
            var bounds = CellBounds(
                sample.Center,
                sample.Radius,
                previous.Left,
                previous.Right,
                centerLeft,
                centerRight,
                next.Left,
                next.Right,
                kind);

            cells.Add(new MixingStrokeCoverageCell(
                sample.Source.Argb,
                kind,
                sample.Center,
                sample.Radius,
                tangent,
                previous.Left,
                previous.Right,
                centerLeft,
                centerRight,
                next.Left,
                next.Right,
                bounds));
        }
    }

    private static PointF ResolveTangent(
        PreparedSample[] samples,
        int runStart,
        int runEnd,
        int segmentIndex)
    {
        for (var distance = 0; distance < runEnd - runStart; distance++)
        {
            var nextIndex = segmentIndex + distance;
            if (nextIndex < runEnd
                && TryUnitDirection(samples[nextIndex].Center, samples[nextIndex + 1].Center, out var next))
            {
                return next;
            }

            if (distance == 0) continue;
            var previousIndex = segmentIndex - distance;
            if (previousIndex >= runStart
                && TryUnitDirection(samples[previousIndex].Center, samples[previousIndex + 1].Center, out var previous))
            {
                return previous;
            }
        }

        return new PointF(1, 0);
    }

    private static PointF CenterTangent(PointF previous, PointF next)
    {
        var combined = new PointF(previous.X + next.X, previous.Y + next.Y);
        return TryUnitDirection(default, combined, out var tangent) ? tangent : next;
    }

    private static bool TryUnitDirection(PointF start, PointF end, out PointF direction)
    {
        var dx = (double)end.X - start.X;
        var dy = (double)end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 0.000001d)
        {
            direction = default;
            return false;
        }

        direction = new PointF((float)(dx / length), (float)(dy / length));
        return true;
    }

    private static bool TouchOrOverlap(
        MixingBrushTrajectorySample current,
        MixingBrushTrajectorySample next)
    {
        var dx = (double)next.Point.X - current.Point.X;
        var dy = (double)next.Point.Y - current.Point.Y;
        var radiusSum = ((double)current.Diameter + next.Diameter) * 0.5d;
        return dx * dx + dy * dy <= radiusSum * radiusSum;
    }

    private static RectangleF CellBounds(
        PointF center,
        float radius,
        PointF previousLeft,
        PointF previousRight,
        PointF centerLeft,
        PointF centerRight,
        PointF nextLeft,
        PointF nextRight,
        MixingStrokeCoverageCellKind kind)
    {
        var includeRoundCap = kind is MixingStrokeCoverageCellKind.Start or MixingStrokeCoverageCellKind.End;
        var left = includeRoundCap ? center.X - radius : centerLeft.X;
        var top = includeRoundCap ? center.Y - radius : centerLeft.Y;
        var right = includeRoundCap ? center.X + radius : centerLeft.X;
        var bottom = includeRoundCap ? center.Y + radius : centerLeft.Y;
        if (kind != MixingStrokeCoverageCellKind.Start)
        {
            Include(previousLeft, ref left, ref top, ref right, ref bottom);
            Include(previousRight, ref left, ref top, ref right, ref bottom);
        }
        Include(centerLeft, ref left, ref top, ref right, ref bottom);
        Include(centerRight, ref left, ref top, ref right, ref bottom);
        if (kind != MixingStrokeCoverageCellKind.End)
        {
            Include(nextLeft, ref left, ref top, ref right, ref bottom);
            Include(nextRight, ref left, ref top, ref right, ref bottom);
        }
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static RectangleF CircleBounds(PointF center, float radius) =>
        RectangleF.FromLTRB(
            center.X - radius,
            center.Y - radius,
            center.X + radius,
            center.Y + radius);

    private static void Include(
        PointF point,
        ref float left,
        ref float top,
        ref float right,
        ref float bottom)
    {
        left = Math.Min(left, point.X);
        top = Math.Min(top, point.Y);
        right = Math.Max(right, point.X);
        bottom = Math.Max(bottom, point.Y);
    }

    private static bool IsRenderable(MixingBrushTrajectorySample sample) =>
        IsFinite(sample.Point)
        && float.IsFinite(sample.Diameter)
        && sample.Diameter > 0;

    private static bool IsFinite(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);
}

internal sealed partial class StageControl : Control
{
    private const int WmPointerUpdate = 0x0245;
    private const int WmPointerDown = 0x0246;
    private const int WmPointerUp = 0x0247;
    private const int WmPointerCaptureChanged = 0x024C;
    private const int MaxPenHistoryEntries = 512;
    private const int MaxSelectionOutlines = 512;
    private const float EndpointHandleHitRadiusPixels = 14;
    private const float ControlHandleHitRadiusPixels = 16;
    private const float MaxControlHandleHitRadiusPixels = 32;
    private const float FillEdgeBezierCurveHitRadiusPixels = 6;
    private const float FillEdgeBezierAnchorHitRadiusPixels = 9;
    private const float FillEdgeBezierControlHitRadiusPixels = 10;
    private const int BrushColorPaletteSwatchSize = 30;
    private const int BrushColorPaletteRadiusPixels = 58;
    private const int MaxBrushColorPaletteColors = 8;
    private const int MaxGdiBrushCacheEntries = 1024;
    private const float FillEdgeCoverageWidthPixels = 0.8f;
    private const double FillAnimationDurationMilliseconds = 420;
    private const double SelectionHighlightCycleMilliseconds = 1_100;
    private const int AnimatedSelectionObjectBudget = 512;
    private const long AnimatedSelectionAtomBudget = 8_192;
    private const double MarqueePreviewFrameBudgetMilliseconds = 8;
    private const int MarqueePreviewObjectThreshold = 2_000;
    private const int ZoomLodPreviewObjectThreshold = 320;
    private const int ZoomLodPreviewIdleMilliseconds = 100;
    private const int DistortRasterMaximumDimension = 2048;
    private const int DistortPreviewRasterMaximumDimension = 1024;
    private const long DistortRasterCacheBudgetBytes = 64L * 1024 * 1024;
    private const int DistortMeshMinimumDivisions = 6;
    private const int DistortMeshMaximumDivisions = 24;
    private const int DistortPreviewMeshMinimumDivisions = 4;
    private const int DistortPreviewMeshMaximumDivisions = 5;
    private const float DistortMeshCellPixels = 48f;
    private const double InteractiveMoveFrameBudgetMilliseconds = 8;
    private const double SlowPointerFeedbackMilliseconds = 50;
    private const double SlowPointerLogIntervalMilliseconds = 5_000;
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
    private readonly Pen _textAreaGlowPen = new(Color.FromArgb(80, 79, 179, 162), 4f);
    private readonly Pen _textAreaPen = new(Color.FromArgb(255, 79, 179, 162), 1.6f);
    private readonly SolidBrush _textAreaHandleBrush = new(Color.FromArgb(255, 79, 179, 162));
    private readonly Pen _handleBorderPen = new(Color.FromArgb(255, 16, 18, 22), 1);
    private readonly System.Windows.Forms.Timer _fillAnimationTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _selectionHighlightTimer = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer _selectionSweepTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _zoomLodPreviewTimer = new() { Interval = ZoomLodPreviewIdleMilliseconds };
    private readonly Direct2DStageRenderer _direct2DRenderer = new();
    private readonly SceneRenderOrderBuffer _renderOrder = new();
    private readonly MixingBrushRegionRasterCache _mixingBrushRasterCache = new();
    private readonly Dictionary<DistortRasterCacheKey, Bitmap> _distortRasterCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), DistortedVectorGeometryCacheEntry>
        _distortedVectorGeometryCache = new();
    private long _distortRasterCacheBytes;
    private Bitmap? _gdiBaseFrameBitmap;
    private Size _gdiBaseFrameSize;
    private Reference3DBaseFrameRenderState _gdiBaseFrameRenderState;
    private RenderStats _gdiBaseFrameStats;
    private RenderStats _gdiBaseFrameEditableStats;
    private LayerBlendCompositor? _layerBlendCompositor;
    private readonly MarqueeOverlayWindow _marqueeOverlay;
    private readonly HashSet<uint> _handledPenPointers = [];
    private PointerPenInfo[] _penHistoryBuffer = new PointerPenInfo[32];
    private int _selectedObject = -1;
    private int[] _selectedObjects = Array.Empty<int>();
    private DrawingElementHit[] _selectedElements = Array.Empty<DrawingElementHit>();
    private DrawingElementHit _hoveredLineElement = DrawingElementHit.None;
    private TransformOverlayFrame _transformFrame;
    private DistortEnvelope _distortFrame;
    private VectorScene? _distortPreviewScene;
    private IReadOnlyDictionary<int, DistortWarp[]> _distortPreviewOverrides =
        new Dictionary<int, DistortWarp[]>();
    private RectangleF _transformBounds = RectangleF.Empty;
    private PointF _transformFocus;
    private bool _transformOverlayUsesReferenceProjection;
    private Matrix4x4 _transformOverlayFlatToScene = Matrix4x4.Identity;
    private RectangleF _drawingObjectSelectionBounds = RectangleF.Empty;
    private PointF _drawingObjectAnchor;
    private readonly Dictionary<int, SelectedFillOwnerCacheEntry> _selectedFillCache = new();
    private readonly Dictionary<(int ObjectIndex, DrawingElementKind Kind), SelectedPolylineOwnerCacheEntry> _selectedPolylineCache = new();
    private float? _pendingVisibleWorldWidth;
    private float _referenceYaw = -0.72f;
    private float _referencePitch = 0.76f;
    private float _referenceDistance = DefaultReferenceDistance;
    private float _referenceZoomScale = 1;
    private float _referenceTargetX;
    private float _referenceTargetY;
    private float _referenceTargetZ;
    private float _worldGridOpacity = 0.1f;
    private WorldGridType _worldGridType;
    private PointF[][] _fillPreviewContours = Array.Empty<PointF[]>();
    private Color _fillPreviewColor = Color.White;
    private PointF[][] _fillAnimationContours = Array.Empty<PointF[]>();
    private PointF _fillAnimationOrigin;
    private Color _fillAnimationColor = Color.White;
    private float _fillAnimationMaxRadiusWorld;
    private float _fillAnimationProgress = 1f;
    private long _fillAnimationStartedAt;
    private PointF[][] _selectionSweepContours = Array.Empty<PointF[]>();
    private float _selectionSweepProgress = 1f;
    private long _selectionSweepStartedAt;
    private float _selectionHighlightPhase;
    private long _selectionHighlightStartedAt;
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
    private FillEdgeBezierOverlaySegment[] _fillEdgeBezierOverlaySegments = [];
    private FillEdgeBezierOverlaySegment[] _presentedFillEdgeBezierOverlaySegments = [];
    private int _fillEdgeBezierOverlayTargetObject = -1;
    private int _fillEdgeBezierOverlayActivePartIndex = -1;
    private PointF _fillEdgeBezierOverlayTranslation;
    private long _fillEdgeBezierOverlayRevision;
    private long _presentedFillEdgeBezierOverlayRevision;
    private long _presentedFillEdgeBezierSourceRevision = -1;
    private long _presentedFillEdgeBezierSceneRevision = -1;
    private VectorScene? _presentedFillEdgeBezierScene;
    private IReadOnlyList<DistortWarp>? _presentedFillEdgeBezierDistortions;
    private Point _brushColorPaletteCenter;
    private Color[] _brushColorPaletteColors = [];
    private int _brushColorPaletteHoveredIndex = -1;
    private bool _paintFailureLogged;
    private bool _disposingResources;
    private int _scenePassSequence;
    private double _lastFrameRenderMilliseconds;
    private bool _marqueeSceneInvalidationPending;
    private bool _zoomLodPreviewActive;
    private long _basePresentationRevision;
    private long _reference3DWorkspaceFrameCacheRevision;
    private bool _basePresentationInvalidationPending;
    private int _frame;
    private int _interactiveInputDepth;
    private bool _interactiveFrameRequested;
    private bool _paintInProgress;
    private long _interactiveFrameRequestedAt;
    private long _lastFramePresentedAt;
    private long _pointerDownStartedAt;
    private long _lastSlowPointerLogAt;
    private bool _pointerDownAwaitingPresent;
    private bool _interactiveSynchronousPresentDeferred;
    private int _currentInteractiveRequestCount;
    private int _currentInteractiveCoalescedCount;
    private int _reference3DPlaybackPresentPosted;

    private readonly record struct DistortRasterCacheKey(
        VectorScene Scene,
        int ObjectIndex,
        SceneRenderPass Pass,
        long GeometryRevision,
        long SummaryRevision,
        long PresentationRevision,
        int Frame,
        float CameraX,
        float CameraY,
        float Zoom,
        int ViewWidth,
        int ViewHeight,
        RectangleF SourceScreenBounds,
        int BitmapWidth,
        int BitmapHeight);

    private readonly record struct DistortedVectorGeometryCacheEntry(
        long GeometryRevision,
        IReadOnlyList<DistortWarp> Distortions,
        DistortedVectorGeometry Geometry);

    private readonly record struct SelectedFillOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingFillPartGeometry[] Parts);

    private readonly record struct SelectedPolylineOwnerCacheEntry(int Frame, long Revision, float X, float Y, DrawingPolylinePartGeometry[] Parts);

    internal enum PenPointerEventKind
    {
        Down,
        Update,
        Up,
        CaptureLost
    }

    internal readonly record struct PenPointerSample(
        Point Location,
        float Pressure,
        bool IsInContact,
        bool IsPrimary,
        bool IsEraser,
        bool HasBarrelButton,
        uint TimestampMilliseconds);

    internal sealed class PenPointerEventArgs : EventArgs
    {
        public PenPointerEventArgs(uint pointerId, PenPointerEventKind kind, IReadOnlyList<PenPointerSample> samples)
        {
            PointerId = pointerId;
            Kind = kind;
            Samples = samples;
        }

        public uint PointerId { get; }
        public PenPointerEventKind Kind { get; }
        public IReadOnlyList<PenPointerSample> Samples { get; }
        public bool Handled { get; set; }
    }

    public VectorScene Scene { get; internal set; }
    public VectorScene? UnderlayScene { get; private set; }
    public VectorScene? OnionSkinScene { get; private set; }
    public VectorScene? DragPreviewScene { get; private set; }
    private VectorScene? _dragPreviewSourceScene;
    private HashSet<int> _dragPreviewHiddenObjects = [];
    private VectorScene? _editingTextScene;
    private int _editingTextObject = -1;
    public SceneDimension ReferenceDimension { get; private set; } = SceneDimension.TwoD;
    public CameraProjection ReferenceProjection { get; private set; } = CameraProjection.Orthographic;
    public float ReferenceYaw => _referenceYaw;
    public float ReferencePitch => _referencePitch;
    public float ReferenceDistance => _referenceDistance;
    public float ReferenceZoomScale => _referenceZoomScale;

    /// <summary>
    /// Scale of the camera that actually presents the current view, as a multiple of 100%.
    /// Basic Drawing and the 2D Front scene view are driven by the 2D drawing camera
    /// (<see cref="Zoom"/>); every reference-projected view (3D scenes, side/top directions and the
    /// Shots workspace) is driven by the reference camera. Reporting the wrong one would show a zoom
    /// that does not match what the operator sees, because the three workspaces zoom independently.
    /// </summary>
    public float ActiveViewZoom => UsesReferenceProjection ? _referenceZoomScale : Zoom;
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
    public WorldGridType WorldGridType
    {
        get => _worldGridType;
        set
        {
            var next = Enum.IsDefined(value) ? value : VectorAnimationEngine.WorldGridType.Cartesian;
            if (_worldGridType == next) return;
            _worldGridType = next;
            Invalidate();
        }
    }
    internal WorldGridScale CurrentWorldGridScale => WorldGridLayout.Resolve(WorldLengthToScreen(1));
    public float AdaptiveGridSnapStep => CurrentWorldGridScale.StepWorld;
    internal GoldenSpiralGridGeometry ResolveGoldenSpiralGrid() => GoldenSpiralGridLayout.Resolve(
        VisibleWorldBounds(),
        AdaptiveGridSnapStep,
        ScreenLengthToWorld(0.5f));
    internal PolarGridGeometry ResolvePolarGrid() => PolarGridLayout.Resolve(
        VisibleWorldBounds(),
        AdaptiveGridSnapStep);
    public int Frame
    {
        get => _frame;
        set
        {
            if (_frame == value) return;
            ClearSelectionDragPreviewCore(invalidate: false);
            ClearSelectionFillDragFront(invalidate: false);
            _frame = value;
            RequestStageFrame(
                basePresentationChanged: true,
                preserveReference3DWorkspaceFrameCache: true);
        }
    }

    internal void SetReference3DPlaybackFrame(int value)
    {
        if (_frame == value) return;
        ClearSelectionDragPreviewCore(invalidate: false);
        ClearSelectionFillDragFront(invalidate: false);
        _frame = value;
        RequestStageFrame(
            // A preloaded playback composition has already invalidated the
            // render-plan state when it was installed. Re-invalidating it here
            // adds measurable UI-thread work for every animation frame. Keep
            // the full invalidation for the fallback path, where no prepared
            // raster frame is available.
            basePresentationChanged: !Reference3DPlaybackActive
                || Reference3DPlaybackRasterFrame is null,
            preserveReference3DWorkspaceFrameCache: true,
            requestPaint: true);
    }
    public int SelectedObject
    {
        get => _selectedObject;
        set
        {
            ClearSelectionDragPreviewCore(invalidate: false);
            ClearSelectionFillDragFront();
            _selectedObject = value;
            _selectedObjects = value >= 0 && value < Scene.ObjectCount ? new[] { value } : Array.Empty<int>();
            SelectedElement = DrawingElementHit.None;
            _selectedElements = Array.Empty<DrawingElementHit>();
            InvalidateSelectedFillCache();
            UpdateSelectionHighlightAnimation();
            InvalidateOverlay();
        }
    }

    public IReadOnlyList<int> SelectedObjects => _selectedObjects;
    public IReadOnlyList<DrawingElementHit> SelectedElements => _selectedElements;
    public DrawingElementHit SelectedElement { get; private set; } = DrawingElementHit.None;
    public bool PenPathHandlesVisible { get; private set; }
    public DrawingElementHit HoveredLineElement => _hoveredLineElement;
    public bool TransformMode { get; private set; }
    public bool DistortMode { get; private set; }
    public bool TransformBoundsVisible { get; private set; }
    public bool DistortBoundsVisible { get; private set; }
    public TransformOverlayFrame TransformFrame => _transformFrame;
    internal DistortEnvelope DistortFrame => _distortFrame;
    public RectangleF TransformBounds => _transformBounds;
    public PointF TransformFocus => _transformFocus;
    public bool DrawingObjectSelectionVisible { get; private set; }
    public RectangleF DrawingObjectSelectionBounds => _drawingObjectSelectionBounds;
    public bool DrawingObjectAnchorVisible { get; private set; }
    public PointF DrawingObjectAnchor => _drawingObjectAnchor;
    internal static Color TextAreaBorderColor => Color.FromArgb(255, 79, 179, 162);
    internal static Color TextAreaGlowColor => Color.FromArgb(80, 79, 179, 162);
    public bool MarqueeVisible { get; private set; }
    public Point MarqueeStart { get; private set; }
    public Point MarqueeEnd { get; private set; }
    internal bool MarqueeLodPreviewActive { get; private set; }
    internal bool ZoomLodPreviewActive => _zoomLodPreviewActive;
    internal bool MarqueeOverlayActive { get; private set; }
    internal Rectangle MarqueeOverlayScreenBounds => _marqueeOverlay.ScreenBounds;
    internal long MarqueeOverlayUpdateCount => _marqueeOverlay.UpdateCount;
    public bool DrawingPreviewVisible { get; private set; }
    public PointF DrawingPreviewStart { get; private set; }
    public PointF DrawingPreviewEnd { get; private set; }
    public PointF DrawingPreviewControl { get; private set; }
    public bool DrawingPreviewHasCurve { get; private set; }
    public IReadOnlyList<CubicDrawingPreviewSegment> DrawingPreviewCurveSegments { get; private set; } = Array.Empty<CubicDrawingPreviewSegment>();
    public PointF DrawingPreviewControl2 { get; private set; }
    public ShapeKind DrawingPreviewShape { get; private set; } = ShapeKind.Rectangle;
    public int DrawingPreviewShapeVertexCount { get; private set; }
    public Color DrawingPreviewColor { get; private set; } = Color.White;
    public float DrawingPreviewStroke { get; private set; } = 2;
    public bool PenAnchorGuidesVisible { get; private set; }
    public PointF PenAnchorGuidePoint { get; private set; }
    public bool PenAnchorGuideVertical { get; private set; }
    public bool PenAnchorGuideHorizontal { get; private set; }
    public bool PenAnchorGuideSnapped { get; private set; }
    public bool PenAnchorGuideInsertion { get; private set; }
    public bool PenDirectionHandlesVisible { get; private set; }
    public PointF PenDirectionAnchor { get; private set; }
    public PointF? PenDirectionIncoming { get; private set; }
    public PointF? PenDirectionOutgoing { get; private set; }
    public bool FreehandPreviewVisible { get; private set; }
    public IReadOnlyList<PointF> FreehandPreviewPoints { get; private set; } = Array.Empty<PointF>();
    public IReadOnlyList<float> FreehandPreviewDiameters { get; private set; } = Array.Empty<float>();
    public Color FreehandPreviewColor { get; private set; } = Color.White;
    public float FreehandPreviewStroke { get; private set; } = 2;
    public BrushShape? FreehandPreviewBrushShape { get; private set; }
    public bool BrushTipCursorVisible { get; private set; }
    public Point BrushTipCursorScreen { get; private set; }
    public float BrushTipCursorRadiusPixels { get; private set; }
    private float BrushTipCursorDiameterWorld { get; set; }
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
    public bool FillEdgeBezierOverlayVisible => _fillEdgeBezierOverlayTargetObject >= 0
        && _fillEdgeBezierOverlaySegments.Length > 0;
    public int FillEdgeBezierOverlayTargetObject => _fillEdgeBezierOverlayTargetObject;
    public int FillEdgeBezierOverlayActivePartIndex => _fillEdgeBezierOverlayActivePartIndex;
    public IReadOnlyList<FillEdgeBezierOverlaySegment> FillEdgeBezierOverlaySegments =>
        PresentedFillEdgeBezierOverlaySegments();
    internal PointF FillEdgeBezierOverlayTranslation => _fillEdgeBezierOverlayTranslation;
    public bool FillEdgeBezierPointerEditing { get; private set; }
    internal long FillEdgeBezierOverlayRevision
    {
        get
        {
            PresentedFillEdgeBezierOverlaySegments();
            return _presentedFillEdgeBezierOverlayRevision;
        }
    }
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
    public bool SelectionSweepVisible => _selectionSweepContours.Length > 0 && _selectionSweepProgress < 1f;
    public IReadOnlyList<PointF[]> SelectionSweepContours => _selectionSweepContours;
    public float SelectionSweepProgress => _selectionSweepProgress;
    public float CameraX { get; private set; }
    public float CameraY { get; private set; }
    public float Zoom { get; private set; } = 1;
    public RenderStats LastStats { get; private set; }
    public bool LastFrameUsedDirect2D { get; private set; }
    internal int LastOnionSkinScenePassOrder { get; private set; }
    internal int LastUnderlayScenePassOrder { get; private set; }
    internal int LastEditableScenePassOrder { get; private set; }
    internal bool GpuAccelerationActive => _direct2DRenderer.HardwareAccelerationActive;
    internal bool ImmediateGpuPresentationEnabled => _direct2DRenderer.ImmediatePresentationEnabled;
    internal double LastDirect2DCacheMaintenanceMilliseconds => _direct2DRenderer.LastCacheMaintenanceMilliseconds;
    internal double LastDirect2DCommandMilliseconds => _direct2DRenderer.LastCommandMilliseconds;
    internal double LastDirect2DPresentMilliseconds => _direct2DRenderer.LastPresentMilliseconds;
    internal double LastDirect2DReference3DGridMilliseconds =>
        _direct2DRenderer.LastReference3DGridMilliseconds;
    internal double LastDirect2DReference3DSceneMilliseconds =>
        _direct2DRenderer.LastReference3DSceneMilliseconds;
    internal double LastDirect2DReference3DPlanLookupMilliseconds =>
        _direct2DRenderer.LastReference3DPlanLookupMilliseconds;
    internal double LastDirect2DReference3DOverlayMilliseconds =>
        _direct2DRenderer.LastReference3DOverlayMilliseconds;
    internal double LastDirect2DReference3DBaseStoreMilliseconds =>
        _direct2DRenderer.LastReference3DBaseStoreMilliseconds;
    internal double LastDirect2DReference3DWorkspaceStoreMilliseconds =>
        _direct2DRenderer.LastReference3DWorkspaceStoreMilliseconds;
    internal int LastDirect2DLodBitmapSubmissions => _direct2DRenderer.LastLodBitmapSubmissions;
    internal int LastDirect2DLodBitmapBuilds => _direct2DRenderer.LastLodBitmapBuilds;
    internal int LastDirect2DLodDetailObjectDraws => _direct2DRenderer.LastLodDetailObjectDraws;
    internal int LastDirect2DGradientBrushCacheBuilds => _direct2DRenderer.LastGradientBrushCacheBuilds;
    internal int LastDirect2DGradientBrushCacheReuses => _direct2DRenderer.LastGradientBrushCacheReuses;
    internal int LastDirect2DShapeGradientBitmapCacheBuilds => _direct2DRenderer.LastShapeGradientBitmapCacheBuilds;
    internal int LastDirect2DShapeGradientBitmapCacheReuses => _direct2DRenderer.LastShapeGradientBitmapCacheReuses;
    internal int LastDirect2DShapeGradientMaskGeometryCacheBuilds => _direct2DRenderer.LastShapeGradientMaskGeometryCacheBuilds;
    internal int LastDirect2DShapeGradientMaskGeometryCacheReuses => _direct2DRenderer.LastShapeGradientMaskGeometryCacheReuses;
    internal int LastDirect2DPathGradientBrushCacheBuilds => _direct2DRenderer.LastPathGradientBrushCacheBuilds;
    internal int LastDirect2DPathGradientBrushCacheReuses => _direct2DRenderer.LastPathGradientBrushCacheReuses;
    internal int LastDirect2DReference3DProjectiveGradientDomainFills =>
        _direct2DRenderer.LastReference3DProjectiveGradientDomainFills;
    internal int LastDirect2DReference3DMaterialBitmapCacheBuilds =>
        _direct2DRenderer.LastReference3DMaterialBitmapCacheBuilds;
    internal int LastDirect2DReference3DMaterialBitmapCacheReuses =>
        _direct2DRenderer.LastReference3DMaterialBitmapCacheReuses;
    internal int LastDirect2DReference3DMaterialBitmapSubmissions =>
        _direct2DRenderer.LastReference3DMaterialBitmapSubmissions;
    internal int LastDirect2DReference3DStrokeBatchSubmissions =>
        _direct2DRenderer.LastReference3DStrokeBatchSubmissions;
    internal int LastDirect2DReference3DStrokeBatchObjects =>
        _direct2DRenderer.LastReference3DStrokeBatchObjects;
    internal int LastDirect2DReference3DProjectiveAffineApproximationUses =>
        _direct2DRenderer.LastReference3DProjectiveAffineApproximationUses;
    internal int LastDirect2DReference3DProjectiveScreenMaskBuilds =>
        _direct2DRenderer.LastReference3DProjectiveScreenMaskBuilds;
    internal int LastDirect2DReference3DCpuRasterWorkers =>
        _direct2DRenderer.LastReference3DCpuRasterWorkers;
    internal int LastDirect2DReference3DCpuRasterTiles =>
        _direct2DRenderer.LastReference3DCpuRasterTiles;
    internal int LastDirect2DReference3DCpuRasterCommands =>
        _direct2DRenderer.LastReference3DCpuRasterCommands;
    internal double LastDirect2DReference3DCpuRasterMilliseconds =>
        _direct2DRenderer.LastReference3DCpuRasterMilliseconds;
    internal double LastDirect2DReference3DCpuRasterUploadMilliseconds =>
        _direct2DRenderer.LastReference3DCpuRasterUploadMilliseconds;
    internal int LastDirect2DReference3DCpuRasterBitmapBuilds =>
        _direct2DRenderer.LastReference3DCpuRasterBitmapBuilds;
    internal int LastDirect2DReference3DCpuRasterBitmapReuses =>
        _direct2DRenderer.LastReference3DCpuRasterBitmapReuses;
    internal int LastDirect2DReference3DCpuRasterPreparationWorkers =>
        _direct2DRenderer.LastReference3DCpuRasterPreparationWorkers;
    internal double LastDirect2DReference3DCpuRasterPreparationMilliseconds =>
        _direct2DRenderer.LastReference3DCpuRasterPreparationMilliseconds;
    internal double LastDirect2DReference3DPlaybackBackgroundMilliseconds =>
        _direct2DRenderer.LastReference3DPlaybackBackgroundMilliseconds;
    internal double LastDirect2DReference3DPlaybackBackgroundReadbackMilliseconds =>
        _direct2DRenderer.LastReference3DPlaybackBackgroundReadbackMilliseconds;
    internal double LastDirect2DReference3DPlaybackBitmapWriteMilliseconds =>
        _direct2DRenderer.LastReference3DPlaybackBitmapWriteMilliseconds;
    internal double LastDirect2DReference3DPlaybackBlitMilliseconds =>
        _direct2DRenderer.LastReference3DPlaybackBlitMilliseconds;
    internal double LastDirect2DReference3DPlaybackRasterMilliseconds =>
        _direct2DRenderer.LastReference3DPlaybackRasterMilliseconds;
    internal float LastDirect2DReference3DCpuRasterScale =>
        _direct2DRenderer.LastReference3DCpuRasterScale;
    internal string LastDirect2DReference3DCpuRasterFallbackReason =>
        _direct2DRenderer.LastReference3DCpuRasterFallbackReason;
    internal string LastDirect2DReference3DPlaybackGdiStatus =>
        _direct2DRenderer.LastReference3DPlaybackGdiStatus;
    internal string LastDirect2DReference3DPlaybackGdiRasterFailure =>
        _direct2DRenderer.LastReference3DPlaybackGdiRasterFailure;
    internal int LastDirect2DReference3DPlaybackPresentedFrame =>
        _direct2DRenderer.LastReference3DPlaybackPresentedFrame;
    internal string LastDirect2DReference3DPlaybackRasterFrameMatchFailure =>
        _direct2DRenderer.LastReference3DPlaybackRasterFrameMatchFailure;
    internal long Direct2DReference3DPlaybackRasterFrameMatchCount =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameMatchCount;
    internal long Direct2DReference3DPlaybackRasterFrameMismatchCount =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameMismatchCount;
    internal string Direct2DReference3DPlaybackRasterFrameLastMismatch =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameLastMismatch;
    internal long Direct2DReference3DPlaybackRasterFrameSetCount =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameSetCount;
    internal string Direct2DReference3DPlaybackRasterFrameLastSetType =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameLastSetType;
    internal bool Direct2DReference3DPlaybackRasterFrameLastSetCast =>
        _direct2DRenderer.Reference3DPlaybackRasterFrameLastSetCast;
    internal int LastDirect2DReference3DLocalPathGeometryCacheBuilds =>
        _direct2DRenderer.LastReference3DLocalPathGeometryCacheBuilds;
    internal int LastDirect2DReference3DLocalPathGeometryCacheReuses =>
        _direct2DRenderer.LastReference3DLocalPathGeometryCacheReuses;
    internal int Direct2DReference3DMaterialBitmapCacheEntryCount =>
        _direct2DRenderer.Reference3DMaterialBitmapCacheEntryCount;
    internal long Direct2DReference3DMaterialBitmapCacheBytes =>
        _direct2DRenderer.Reference3DMaterialBitmapCacheBytes;
    internal int LastDirect2DLineGeometryCacheBuilds => _direct2DRenderer.LastLineGeometryCacheBuilds;
    internal int LastDirect2DLineGeometryCacheReuses => _direct2DRenderer.LastLineGeometryCacheReuses;
    internal int LastDirect2DObjectPathGeometryCacheBuilds => _direct2DRenderer.LastObjectPathGeometryCacheBuilds;
    internal int LastDirect2DObjectPathGeometryCacheReuses => _direct2DRenderer.LastObjectPathGeometryCacheReuses;
    internal int LastDirect2DFillEdgeBezierOverlayGeometryBuilds => _direct2DRenderer.LastFillEdgeBezierOverlayGeometryBuilds;
    internal int LastDirect2DBaseFrameCacheBuilds => _direct2DRenderer.LastBaseFrameCacheBuilds;
    internal int LastDirect2DBaseFrameCacheReuses => _direct2DRenderer.LastBaseFrameCacheReuses;
    internal double LastDirect2DBaseFrameCopyMilliseconds => _direct2DRenderer.LastBaseFrameCopyMilliseconds;
    internal int LastDirect2DWorkspaceFrameCacheBuilds =>
        _direct2DRenderer.LastReference3DWorkspaceFrameCacheBuilds;
    internal int LastDirect2DWorkspaceFrameCacheHits =>
        _direct2DRenderer.LastReference3DWorkspaceFrameCacheHits;
    internal int LastDirect2DWorkspaceFrameCacheEvictions =>
        _direct2DRenderer.LastReference3DWorkspaceFrameCacheEvictions;
    internal int Direct2DWorkspaceFrameCacheEntryCount =>
        _direct2DRenderer.Reference3DWorkspaceFrameCacheEntryCount;
    internal long Direct2DWorkspaceFrameCacheBytes =>
        _direct2DRenderer.Reference3DWorkspaceFrameCacheBytes;
    internal bool SelectionHighlightAnimating => _selectionHighlightTimer.Enabled;
    internal float SelectionHighlightPulse => 0.5f + 0.5f * MathF.Sin(_selectionHighlightPhase * MathF.Tau);
    internal double LastFrameRenderMilliseconds => _lastFrameRenderMilliseconds;
    internal long BasePresentationRevision => _basePresentationRevision;
    internal bool Reference3DPlaybackActive { get; private set; }
    internal bool PlaybackActive { get; private set; }
    internal object? Reference3DPlaybackRasterPreparation { get; private set; }
    internal object? Reference3DPlaybackRasterFrame { get; private set; }

    /// <summary>
    /// Host-supplied resolver from an image asset id to its decoded raster. The control only
    /// knows the scene, so the project layer owns asset lookup and file resolution; keeping
    /// it injected also lets the regression run the renderer without a live project.
    /// Returns null when the asset is missing, unreadable, or has no managed copy yet, in
    /// which case the renderer draws nothing rather than a placeholder.
    /// </summary>
    internal Func<string, BitmapImageRaster?>? BitmapImageResolver { get; set; }

    /// <summary>
    /// Host-supplied sampling mode for an image asset, derived from its import filter mode.
    /// Defaults to linear when the host has no opinion, matching a bilinear import.
    /// </summary>
    internal Func<string, BitmapSampling>? BitmapImageSamplingProvider { get; set; }

    internal bool TryDecodeBitmapImage(string imageAssetId, out BitmapImageRaster raster)
    {
        raster = null!;
        if (string.IsNullOrWhiteSpace(imageAssetId) || BitmapImageResolver is null) return false;
        var resolved = BitmapImageResolver(imageAssetId);
        if (resolved is null) return false;
        raster = resolved;
        return true;
    }

    internal BitmapSampling BitmapImageSampling(string imageAssetId)
    {
        if (BitmapImageSamplingProvider is null || string.IsNullOrWhiteSpace(imageAssetId))
        {
            return BitmapSampling.Linear;
        }
        return BitmapImageSamplingProvider(imageAssetId);
    }

    internal void SetReference3DPlaybackActive(bool active)
    {
        if (Reference3DPlaybackActive == active && PlaybackActive == active) return;
        Reference3DPlaybackActive = active;
        PlaybackActive = active;
        if (!active)
        {
            Reference3DPlaybackRasterPreparation = null;
            SetReference3DPlaybackRasterFrame(null);
            // Playback composition frames bypass per-frame editor cache
            // invalidation because their raster result is already complete.
            // Restore the normal cache contract once editing resumes.
            InvalidateReference3DRenderPlanCache();
        }
        _direct2DRenderer.SetReference3DPlaybackRasterActive(this, active);
        if (!active) Interlocked.Exchange(ref _reference3DPlaybackPresentPosted, 0);
    }

    internal void RequestReference3DPlaybackPresent()
    {
        if (!Reference3DPlaybackActive
            || _disposingResources
            || !IsHandleCreated
            || IsDisposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reference3DPlaybackPresentPosted, 1) != 0) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _reference3DPlaybackPresentPosted, 0);
                if (!Reference3DPlaybackActive || _disposingResources || IsDisposed) return;
                // A completed worker frame only needs a paint message. It
                // must not invalidate the render-plan cache or advance any
                // presentation revision on the UI thread.
                base.Invalidate();
            }));
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _reference3DPlaybackPresentPosted, 0);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _reference3DPlaybackPresentPosted, 0);
        }
    }

    internal void SetReference3DPlaybackRasterPreparation(object? preparation)
    {
        Reference3DPlaybackRasterPreparation = preparation;
    }

    internal void SetReference3DPlaybackRasterFrame(object? frame)
    {
        Reference3DPlaybackRasterFrame = frame;
        _direct2DRenderer.SetReference3DPlaybackRasterFrame(frame);
    }

    internal long Reference3DWorkspaceFrameCacheRevision => _reference3DWorkspaceFrameCacheRevision;
    internal bool HasDistortPreview => _distortPreviewScene is not null
        && _distortPreviewOverrides.Count > 0;
    internal int DistortPreviewRasterBuildCount { get; private set; }
    internal int DistortPreviewRasterReuseCount { get; private set; }
    internal int LastGdiDistortRasterBuilds { get; private set; }
    internal int LastGdiDistortRasterReuses { get; private set; }
    internal int LastGdiBaseFrameCacheBuilds { get; private set; }
    internal int LastGdiBaseFrameCacheReuses { get; private set; }
    internal double LastGdiBaseFrameCopyMilliseconds { get; private set; }
    internal double LastPointerDownHandlerMilliseconds { get; private set; }
    internal double LastPointerDownToPaintMilliseconds { get; private set; }
    internal double LastPointerDownToPresentMilliseconds { get; private set; }
    internal double LastInteractiveRequestToPaintMilliseconds { get; private set; }
    internal int LastInteractiveRequestCount { get; private set; }
    internal int LastInteractiveCoalescedRequestCount { get; private set; }
    public event EventHandler? FrameRendered;
    public event EventHandler? ViewChanged;
    internal event EventHandler<PenPointerEventArgs>? PenPointerInput;

    internal bool HasCachedDirect2DFreehandGeometry(VectorScene scene)
    {
        return _direct2DRenderer.HasCachedFreehandGeometry(scene);
    }

    internal void ReloadRenderingModuleForHotReload()
    {
        CompleteReferenceCameraTransition(invalidate: false);
        ClearSelectionDragPreviewCore(invalidate: false);
        ClearSelectionFillDragFront(invalidate: false);
        ResetFrameSchedulerState();
        ResetFillEdgeBezierOverlay(invalidate: false);
        _direct2DRenderer.ReloadRuntimeResources();
        ClearGdiBaseFrameCache();
        _layerBlendCompositor?.Dispose();
        _layerBlendCompositor = null;
        _mixingBrushRasterCache.Clear();
        ClearDistortPreviewCore(invalidate: false);
        _distortedVectorGeometryCache.Clear();
        ImportedSvgRasterizer.ClearCache();
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
        _marqueeOverlay = new MarqueeOverlayWindow(this);
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
        _selectionSweepTimer.Tick += (_, _) => TickSelectionSweep();
        _selectionHighlightTimer.Tick += (_, _) => TickSelectionHighlight();
        _zoomLodPreviewTimer.Tick += (_, _) => EndZoomLodPreview(invalidate: true);
        _reference3DOpticalPreviewTimer.Tick += (_, _) =>
            EndReference3DOpticalInteractionPreview();
        InitializeReferenceCameraTransitions();
        InitializeSpatialTransformGizmoMotion();
    }

    public new void Invalidate() => RequestStageFrame(basePresentationChanged: true);

    internal void InvalidatePreservingWorkspaceFrameCache()
    {
        RequestStageFrame(
            basePresentationChanged: true,
            preserveReference3DWorkspaceFrameCache: true);
    }

    internal void InvalidateOverlay() => RequestStageFrame(basePresentationChanged: false);

    internal void DeferCurrentInteractiveSynchronousPresent()
    {
        if (_interactiveInputDepth > 0) _interactiveSynchronousPresentDeferred = true;
    }

    private void RequestStageFrame(
        bool basePresentationChanged,
        bool preserveReference3DWorkspaceFrameCache = false,
        bool requestPaint = true)
    {
        if (basePresentationChanged) InvalidateReference3DRenderPlanCache();
        if (basePresentationChanged && !preserveReference3DWorkspaceFrameCache)
        {
            unchecked
            {
                _reference3DWorkspaceFrameCacheRevision++;
            }
        }
        if (basePresentationChanged && !_basePresentationInvalidationPending)
        {
            _basePresentationRevision++;
            _basePresentationInvalidationPending = true;
        }
        if (_interactiveInputDepth > 0)
        {
            if (_currentInteractiveRequestCount < int.MaxValue) _currentInteractiveRequestCount++;
            if (_interactiveFrameRequested)
            {
                if (_currentInteractiveCoalescedCount < int.MaxValue) _currentInteractiveCoalescedCount++;
            }
            else
            {
                _interactiveFrameRequested = true;
                _interactiveFrameRequestedAt = Stopwatch.GetTimestamp();
            }
        }

        if (requestPaint) base.Invalidate();
    }

    private void BeginInteractiveInput(bool pointerDown)
    {
        if (_interactiveInputDepth++ > 0) return;
        _currentInteractiveRequestCount = 0;
        _currentInteractiveCoalescedCount = 0;
        if (!pointerDown) return;
        _pointerDownStartedAt = Stopwatch.GetTimestamp();
        _pointerDownAwaitingPresent = true;
        LastPointerDownHandlerMilliseconds = 0;
        LastPointerDownToPaintMilliseconds = 0;
        LastPointerDownToPresentMilliseconds = 0;
    }

    private void EndInteractiveInput(
        bool pointerDown,
        bool forceFinalFrame,
        bool allowSynchronousPresent = true)
    {
        if (_interactiveInputDepth <= 0) return;
        if (_interactiveInputDepth > 1)
        {
            _interactiveInputDepth--;
            return;
        }

        if (forceFinalFrame && !_interactiveFrameRequested)
        {
            InvalidateOverlay();
        }
        _interactiveInputDepth = 0;
        var synchronousPresentDeferred = _interactiveSynchronousPresentDeferred;
        _interactiveSynchronousPresentDeferred = false;

        if (pointerDown && _pointerDownStartedAt != 0)
        {
            LastPointerDownHandlerMilliseconds = ElapsedMilliseconds(
                _pointerDownStartedAt,
                Stopwatch.GetTimestamp());
        }
        LastInteractiveRequestCount = _currentInteractiveRequestCount;
        LastInteractiveCoalescedRequestCount = _currentInteractiveCoalescedCount;
        if (!allowSynchronousPresent)
        {
            if (pointerDown) CancelPendingPointerDownTelemetry();
            return;
        }
        if (!_interactiveFrameRequested || _paintInProgress || !Visible || !IsHandleCreated)
        {
            if (pointerDown && (!Visible || !IsHandleCreated)) CancelPendingPointerDownTelemetry();
            return;
        }

        var shouldPresent = ShouldSynchronouslyPresentInteractiveFrame(
            forceFinalFrame,
            synchronousPresentDeferred,
            _lastFramePresentedAt != 0,
            ElapsedMilliseconds(_lastFramePresentedAt, Stopwatch.GetTimestamp()));
        if (shouldPresent && CanSynchronouslyPresentInteractiveFrame()) Update();
    }

    internal static bool ShouldSynchronouslyPresentInteractiveFrame(
        bool forceFinalFrame,
        bool presentationDeferred,
        bool hasPresentedFrame,
        double elapsedSinceLastFrameMilliseconds)
    {
        return !presentationDeferred
            && (forceFinalFrame
                || !hasPresentedFrame
                || elapsedSinceLastFrameMilliseconds >= InteractiveMoveFrameBudgetMilliseconds);
    }

    private bool CanSynchronouslyPresentInteractiveFrame()
    {
        return LastFrameUsedDirect2D && _direct2DRenderer.HardwareAccelerationActive;
    }

    private void ResetFrameSchedulerState()
    {
        _basePresentationInvalidationPending = false;
        _interactiveInputDepth = 0;
        _interactiveFrameRequested = false;
        _paintInProgress = false;
        _interactiveFrameRequestedAt = 0;
        _lastFramePresentedAt = 0;
        _interactiveSynchronousPresentDeferred = false;
        _currentInteractiveRequestCount = 0;
        _currentInteractiveCoalescedCount = 0;
        CancelPendingPointerDownTelemetry();
        ResetDragFirstMoveTelemetryState();
    }

    private void CancelPendingPointerDownTelemetry()
    {
        _pointerDownStartedAt = 0;
        _pointerDownAwaitingPresent = false;
    }

    private static double ElapsedMilliseconds(long start, long end)
    {
        return start > 0 && end >= start
            ? Math.Max(0, Stopwatch.GetElapsedTime(start, end).TotalMilliseconds)
            : 0;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        CompleteReferenceCameraTransition();
        BeginInteractiveInput(pointerDown: true);
        try
        {
            base.OnMouseDown(e);
        }
        finally
        {
            EndInteractiveInput(pointerDown: true, forceFinalFrame: true);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        BeginInteractiveInput(pointerDown: false);
        try
        {
            base.OnMouseMove(e);
        }
        finally
        {
            EndInteractiveInput(pointerDown: false, forceFinalFrame: false);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        BeginInteractiveInput(pointerDown: false);
        try
        {
            base.OnMouseUp(e);
        }
        finally
        {
            EndInteractiveInput(pointerDown: false, forceFinalFrame: true);
        }
    }

    public void BindScene(VectorScene scene)
    {
        CompleteReferenceCameraTransition(invalidate: false);
        ClearSelectionDragPreviewCore(invalidate: false);
        ResetFillEdgeBezierOverlay(invalidate: false);
        EndZoomLodPreview(invalidate: false);
        SetTransformOverlay(false, RectangleF.Empty);
        SetDistortOverlay(false, default);
        ClearDistortPreviewCore(invalidate: false);
        FillEdgeBezierPointerEditing = false;
        ClearReference3DStateForSceneBinding();
        Scene = scene;
        UnderlayScene = null;
        OnionSkinScene = null;
        DragPreviewScene = null;
        _dragPreviewSourceScene = null;
        _dragPreviewHiddenObjects.Clear();
        _editingTextScene = null;
        _editingTextObject = -1;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        _selectedObject = -1;
        _selectedObjects = Array.Empty<int>();
        UpdateSelectionHighlightAnimation();
        ClearDrawingPreview();
        ClearSnapPointOverlay();
        ClearSceneSnapIndicator();
        ClearFreehandPreview();
        ClearLassoPreviewForLifecycle();
        ClearMarquee();
        ClearFillPreview();
        ClearFillAnimation();
        ClearBrushColorPalette();
        _direct2DRenderer.Resize(ClientSize);
        Invalidate();
        RaiseViewChanged();
    }

    public void BindUnderlayScene(VectorScene? scene)
    {
        var next = scene is { ObjectCount: > 0 } ? scene : null;
        if (ReferenceEquals(UnderlayScene, next)) return;
        UnderlayScene = next;
        Invalidate();
    }

    public void BindOnionSkinScene(VectorScene? scene)
    {
        var next = scene is { ObjectCount: > 0 } ? scene : null;
        if (ReferenceEquals(OnionSkinScene, next)) return;
        OnionSkinScene = next;
        Invalidate();
    }

    public void BindDragPreviewScene(
        VectorScene? scene,
        VectorScene? sourceScene = null,
        IReadOnlyCollection<int>? hiddenSourceObjects = null)
    {
        var nextPreviewScene = scene is { ObjectCount: > 0 } ? scene : null;
        var nextSourceScene = nextPreviewScene is not null ? sourceScene : null;
        var nextHiddenObjects = nextSourceScene is not null && hiddenSourceObjects is { Count: > 0 }
            ? hiddenSourceObjects.Where(index => index >= 0).ToHashSet()
            : [];
        var sourceVisibilityChanged = !ReferenceEquals(_dragPreviewSourceScene, nextSourceScene)
            || !_dragPreviewHiddenObjects.SetEquals(nextHiddenObjects);
        DragPreviewScene = nextPreviewScene;
        _dragPreviewSourceScene = nextSourceScene;
        _dragPreviewHiddenObjects = nextHiddenObjects;
        RequestStageFrame(basePresentationChanged: sourceVisibilityChanged);
    }

    internal bool IsHiddenByDragPreview(VectorScene scene, int objectIndex)
    {
        return ReferenceEquals(scene, _dragPreviewSourceScene) && _dragPreviewHiddenObjects.Contains(objectIndex);
    }

    internal bool RequiresObjectRendererForDragPreview(VectorScene scene)
    {
        return ReferenceEquals(scene, _dragPreviewSourceScene) && _dragPreviewHiddenObjects.Count > 0;
    }

    internal bool IsObjectHiddenForRendering(VectorScene scene, int objectIndex)
    {
        return IsHiddenByDragPreview(scene, objectIndex)
            || (ReferenceEquals(scene, _editingTextScene) && objectIndex == _editingTextObject);
    }

    internal void SetDistortPreview(
        VectorScene scene,
        IReadOnlyDictionary<int, DistortWarp[]> overrides)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(overrides);
        var next = overrides
            .Where(item => (uint)item.Key < scene.ObjectCount
                && item.Value is { Length: > 0 }
                && item.Value.All(distortion => distortion.IsValid))
            .ToDictionary(item => item.Key, item => item.Value);
        var targetsChanged = !ReferenceEquals(_distortPreviewScene, scene)
            || !_distortPreviewOverrides.Keys.OrderBy(index => index)
                .SequenceEqual(next.Keys.OrderBy(index => index));
        if (targetsChanged) ClearDistortRasterCache();
        _distortPreviewScene = next.Count > 0 ? scene : null;
        _distortPreviewOverrides = next;
        RequestStageFrame(basePresentationChanged: true);
    }

    internal void ClearDistortPreview() => ClearDistortPreviewCore(invalidate: true);

    private void ClearDistortPreviewCore(bool invalidate)
    {
        var hadPreview = HasDistortPreview;
        _distortPreviewScene = null;
        _distortPreviewOverrides = new Dictionary<int, DistortWarp[]>();
        _distortedVectorGeometryCache.Clear();
        ClearDistortRasterCache();
        DistortPreviewRasterBuildCount = 0;
        DistortPreviewRasterReuseCount = 0;
        if (invalidate && hadPreview) RequestStageFrame(basePresentationChanged: true);
    }

    private void ClearDistortRasterCache()
    {
        foreach (var bitmap in _distortRasterCache.Values) bitmap.Dispose();
        _distortRasterCache.Clear();
        _distortRasterCacheBytes = 0;
    }

    private bool TryCacheDistortRaster(DistortRasterCacheKey key, Bitmap bitmap)
    {
        var bytes = (long)bitmap.Width * bitmap.Height * 4;
        if (bytes <= 0 || bytes > DistortRasterCacheBudgetBytes) return false;
        while (_distortRasterCacheBytes + bytes > DistortRasterCacheBudgetBytes
            && _distortRasterCache.Count > 0)
        {
            var oldest = _distortRasterCache.First();
            _distortRasterCache.Remove(oldest.Key);
            _distortRasterCacheBytes -= (long)oldest.Value.Width * oldest.Value.Height * 4;
            oldest.Value.Dispose();
        }
        _distortRasterCache[key] = bitmap;
        _distortRasterCacheBytes += bytes;
        return true;
    }

    internal bool SceneHasDistortionsForRendering(VectorScene? scene)
    {
        return scene is not null
            && (scene.HasObjectDistortions
                || ReferenceEquals(scene, _distortPreviewScene) && _distortPreviewOverrides.Count > 0);
    }

    private bool TryGetObjectDistortionsForRendering(
        VectorScene scene,
        int objectIndex,
        out IReadOnlyList<DistortWarp> distortions)
    {
        if (ReferenceEquals(scene, _distortPreviewScene)
            && _distortPreviewOverrides.TryGetValue(objectIndex, out var preview))
        {
            distortions = preview;
            return preview.Length > 0;
        }
        return scene.TryGetObjectDistortionsView(objectIndex, out distortions);
    }

    private PointF MapObjectPointForRendering(VectorScene scene, int objectIndex, PointF point)
    {
        if (!TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions)) return point;
        var mapped = point;
        foreach (var distortion in distortions) mapped = distortion.Map(mapped);
        return mapped;
    }

    private bool TryGetDistortedVectorGeometryForRendering(
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<DistortWarp> distortions,
        out DistortedVectorGeometry geometry)
    {
        var key = (scene, objectIndex);
        if (_distortedVectorGeometryCache.TryGetValue(key, out var cached)
            && cached.GeometryRevision == scene.GeometryRevision
            && ReferenceEquals(cached.Distortions, distortions))
        {
            geometry = cached.Geometry;
            return true;
        }

        if (!scene.TryBuildDistortedVectorGeometry(objectIndex, distortions, out geometry))
        {
            _distortedVectorGeometryCache.Remove(key);
            return false;
        }

        _distortedVectorGeometryCache[key] = new DistortedVectorGeometryCacheEntry(
            scene.GeometryRevision,
            distortions,
            geometry);
        return true;
    }

    private DistortedBezierSegment[] GetPresentedBezierSegments(
        VectorScene scene,
        int objectIndex,
        CubicBoundarySegment source)
    {
        if (!TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions))
        {
            return [new DistortedBezierSegment(source, 0, 0, 1)];
        }

        var presented = VectorScene.BuildDistortedBezierSegmentsWithProvenance([source], distortions);
        return presented.Length > 0
            ? presented
            : [new DistortedBezierSegment(source, 0, 0, 1)];
    }

    private IReadOnlyList<FillEdgeBezierOverlaySegment> PresentedFillEdgeBezierOverlaySegments()
    {
        IReadOnlyList<DistortWarp>? distortions = null;
        if ((uint)_fillEdgeBezierOverlayTargetObject < Scene.ObjectCount)
        {
            if (TryGetObjectDistortionsForRendering(
                    Scene,
                    _fillEdgeBezierOverlayTargetObject,
                    out var resolvedDistortions))
            {
                distortions = resolvedDistortions;
            }
        }

        if (_presentedFillEdgeBezierSourceRevision == _fillEdgeBezierOverlayRevision
            && ReferenceEquals(_presentedFillEdgeBezierScene, Scene)
            && _presentedFillEdgeBezierSceneRevision == Scene.GeometryRevision
            && ReferenceEquals(_presentedFillEdgeBezierDistortions, distortions))
        {
            return _presentedFillEdgeBezierOverlaySegments;
        }

        if (distortions is null || distortions.Count == 0)
        {
            _presentedFillEdgeBezierOverlaySegments = _fillEdgeBezierOverlaySegments;
        }
        else
        {
            var presented = new List<FillEdgeBezierOverlaySegment>(_fillEdgeBezierOverlaySegments.Length * 2);
            foreach (var source in _fillEdgeBezierOverlaySegments)
            {
                var sourceCurve = new CubicBoundarySegment(
                    source.Start,
                    source.Control1,
                    source.Control2,
                    source.End);
                foreach (var derived in VectorScene.BuildDistortedBezierSegmentsWithProvenance(
                             [sourceCurve],
                             distortions))
                {
                    var curve = derived.Curve;
                    presented.Add(new FillEdgeBezierOverlaySegment(
                        source.PartIndex,
                        curve.Start,
                        curve.Control1,
                        curve.Control2,
                        curve.End,
                        derived.SourceStartT,
                        derived.SourceEndT));
                }
            }
            _presentedFillEdgeBezierOverlaySegments = presented.Count > 0
                ? presented.ToArray()
                : _fillEdgeBezierOverlaySegments;
        }

        _presentedFillEdgeBezierSourceRevision = _fillEdgeBezierOverlayRevision;
        _presentedFillEdgeBezierScene = Scene;
        _presentedFillEdgeBezierSceneRevision = Scene.GeometryRevision;
        _presentedFillEdgeBezierDistortions = distortions;
        _presentedFillEdgeBezierOverlayRevision++;
        return _presentedFillEdgeBezierOverlaySegments;
    }

    private PointF[][] GetObjectBoundaryContoursForRendering(VectorScene scene, int objectIndex)
    {
        return TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions)
            ? scene.GetDistortedObjectBoundaryContours(objectIndex, distortions)
            : scene.GetObjectBoundaryContours(objectIndex);
    }

    public void SetEditingTextObject(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(objectIndex));
        }
        if (Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            throw new ArgumentException("The editing object must be a text object.", nameof(objectIndex));
        }
        if (ReferenceEquals(_editingTextScene, Scene) && _editingTextObject == objectIndex) return;

        _editingTextScene = Scene;
        _editingTextObject = objectIndex;
        Invalidate();
    }

    public void ClearEditingTextObject()
    {
        if (_editingTextScene is null && _editingTextObject < 0) return;
        _editingTextScene = null;
        _editingTextObject = -1;
        Invalidate();
    }

    public void ConfigureReferenceView(
        SceneDefinition? scene,
        SceneDimension? viewDimension = null,
        ReferenceCameraMotion motion = ReferenceCameraMotion.Immediate)
    {
        var nextDimension = viewDimension ?? scene?.Dimension ?? SceneDimension.TwoD;
        SetReference3DSceneDefinition(scene);
        ConfigureReferenceViewCore(scene, nextDimension, motion);
    }

    public void RotateReferenceCamera(float dx, float dy)
    {
        CompleteReferenceCameraTransitionForDirectInput();
        _referenceYaw = NormalizeRadians(_referenceYaw + dx * 0.01f);
        _referencePitch = Math.Clamp(
            _referencePitch + dy * 0.01f,
            -ReferenceMaximumPitch,
            ReferenceMaximumPitch);
        Invalidate();
    }

    public void DollyReferenceCamera(float wheelDelta)
    {
        if (!float.IsFinite(wheelDelta) || wheelDelta == 0) return;
        CompleteReferenceCameraTransitionForDirectInput();
        PulseReference3DOpticalInteractionPreview();
        var factor = MathF.Pow(ReferenceWheelDollyBase, wheelDelta / 120f);
        if (!float.IsFinite(factor) || factor <= 0) return;
        if (ReferenceDimension == SceneDimension.ThreeD
            && EffectiveReferenceProjection == CameraProjection.Perspective)
        {
            _referenceDistance = Math.Clamp(_referenceDistance * factor, 2000, 80000);
        }
        else
        {
            _referenceZoomScale = Math.Clamp(_referenceZoomScale / factor, 0.25f, 8f);
        }
        Invalidate();
    }

    public void DollyReferenceCameraByPixels(float dy)
    {
        if (!float.IsFinite(dy)) return;
        CompleteReferenceCameraTransitionForDirectInput();
        var factor = MathF.Exp(dy * ReferencePixelDollyExponent);
        if (!float.IsFinite(factor) || factor <= 0) return;
        if (ReferenceDimension == SceneDimension.ThreeD
            && EffectiveReferenceProjection == CameraProjection.Perspective)
        {
            _referenceDistance = Math.Clamp(_referenceDistance * factor, 2000, 80000);
        }
        else
        {
            _referenceZoomScale = Math.Clamp(_referenceZoomScale / factor, 0.25f, 8f);
        }
        Invalidate();
    }

    public void ZoomReferenceCamera(float factor)
    {
        CompleteReferenceCameraTransitionForDirectInput();
        PulseReference3DOpticalInteractionPreview();
        // Zoom only changes the projection scale. It must not touch the orbit target: the target is
        // subtracted from every world point before projection, so translating it would slide the whole
        // scene across the screen. Doing that on each wheel tick made zooming drift instead of scale,
        // and the drift depended on the current lens, so the gesture felt unstable and did not reverse.
        var previousZoom = _referenceZoomScale;
        _referenceZoomScale = Math.Clamp(_referenceZoomScale * factor, 0.25f, 8f);
        if (Math.Abs(previousZoom - _referenceZoomScale) < 0.0000001f)
        {
            return;
        }

        Invalidate();
    }

    public void PanReferenceCamera(float dx, float dy)
    {
        CompleteReferenceCameraTransitionForDirectInput();
        var yawCos = MathF.Cos(EffectiveReferenceYaw);
        var yawSin = MathF.Sin(EffectiveReferenceYaw);
        var pitchCos = MathF.Cos(EffectiveReferencePitch);
        var pitchSin = MathF.Sin(EffectiveReferencePitch);
        var rightX = yawCos;
        var rightZ = -yawSin;
        var upX = -pitchSin * yawSin;
        var upY = pitchCos;
        var upZ = -pitchSin * yawCos;
        var worldPerPixel = ReferenceWorldUnitsPerPixel();
        var horizontal = -dx * worldPerPixel;
        var vertical = dy * worldPerPixel;

        _referenceTargetX = Math.Clamp(_referenceTargetX + rightX * horizontal + upX * vertical, -5_000_000, 5_000_000);
        _referenceTargetY = Math.Clamp(_referenceTargetY + upY * vertical, -5_000_000, 5_000_000);
        _referenceTargetZ = Math.Clamp(_referenceTargetZ + rightZ * horizontal + upZ * vertical, -5_000_000, 5_000_000);
        Invalidate();
    }

    public void SetReferenceCameraOrientation(
        float yaw,
        float pitch,
        ReferenceCameraMotion motion = ReferenceCameraMotion.Immediate)
    {
        SetReferenceCameraOrientationCore(yaw, pitch, motion);
    }

    public void ResetReferenceCameraView(ReferenceCameraMotion motion = ReferenceCameraMotion.Immediate)
    {
        ResetReferenceCameraViewCore(motion);
    }

    internal StageViewState CaptureViewState()
    {
        var reference = CaptureReferenceCameraFrameForPersistence();
        return new StageViewState(
            CameraX,
            CameraY,
            Zoom,
            reference.Yaw,
            reference.Pitch,
            reference.Distance,
            reference.ZoomScale,
            reference.TargetX,
            reference.TargetY,
            reference.TargetZ,
            WorldGridOpacity,
            WorldGridType);
    }

    internal void RestoreViewState(StageViewState state)
    {
        CancelReferenceCameraTransitionForStateReplacement();
        _pendingVisibleWorldWidth = null;
        EndZoomLodPreview(invalidate: false);
        CameraX = Math.Clamp(state.CameraX, -5_000_000, 5_000_000);
        CameraY = Math.Clamp(state.CameraY, -5_000_000, 5_000_000);
        Zoom = Math.Clamp(state.Zoom, 0.02f, 64f);
        _referenceYaw = NormalizeRadians(state.ReferenceYaw);
        _referencePitch = Math.Clamp(
            state.ReferencePitch,
            -ReferenceMaximumPitch,
            ReferenceMaximumPitch);
        _referenceDistance = Math.Clamp(state.ReferenceDistance, 2000, 80000);
        _referenceZoomScale = Math.Clamp(state.ReferenceZoomScale, 0.25f, 8f);
        _referenceTargetX = Math.Clamp(state.ReferenceTargetX, -5_000_000, 5_000_000);
        _referenceTargetY = Math.Clamp(state.ReferenceTargetY, -5_000_000, 5_000_000);
        _referenceTargetZ = Math.Clamp(state.ReferenceTargetZ, -5_000_000, 5_000_000);
        SynchronizeReferenceProjectionBlend();
        WorldGridOpacity = state.WorldGridOpacity;
        WorldGridType = state.WorldGridType;
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
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
        EndZoomLodPreview(invalidate: false);
        CameraX = 0;
        CameraY = 0;
        Zoom = (float)Math.Clamp(Math.Min(Width / VectorUnits.ToPixels(Scene.StageWidth), Height / VectorUnits.ToPixels(Scene.StageHeight)) * 0.9, 0.02, 64);
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
    }

    public void ResetDefaultView() => SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);

    public void SetVisibleWorldWidth(float vectorUnits) => SetVisibleWorldWidthCore(vectorUnits, raiseViewChanged: true);

    private void SetVisibleWorldWidthCore(float vectorUnits, bool raiseViewChanged)
    {
        vectorUnits = Math.Clamp(vectorUnits, 1, Scene.StageWidth);
        if (Width <= 0 || Height <= 0)
        {
            _pendingVisibleWorldWidth = vectorUnits;
            return;
        }

        _pendingVisibleWorldWidth = null;
        EndZoomLodPreview(invalidate: false);
        CameraX = 0;
        CameraY = 0;
        Zoom = (float)Math.Clamp(Width / Math.Max(1, VectorUnits.ToPixels(vectorUnits)), 0.02, 64);
        RefreshBrushTipCursorScale();
        Invalidate();
        if (raiseViewChanged) RaiseViewChanged();
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
        CompleteReferenceCameraTransitionForDirectInput();
        CameraX -= VectorUnits.FromPixels(dx / Zoom);
        CameraY -= VectorUnits.FromPixels(dy / Zoom);
        Invalidate();
        RaiseViewChanged();
    }

    public void ZoomAt(Point screen, float factor, bool interactivePreview = false)
    {
        CompleteReferenceCameraTransitionForDirectInput();
        var before = ScreenToWorld(screen);
        Zoom = (float)Math.Clamp(Zoom * factor, 0.02, 64);
        var after = ScreenToWorld(screen);
        CameraX += before.X - after.X;
        CameraY += before.Y - after.Y;
        if (interactivePreview && ShouldUseZoomLodPreview(Scene.ObjectCount, Scene.HasDisplayLayerEffects))
        {
            BeginZoomLodPreview();
        }
        else
        {
            EndZoomLodPreview(invalidate: false);
        }
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
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
        if (SelectedElement.IsValid
            && SelectedElement.Key.ObjectIndex == objectIndex
            && SelectedElement.Key.Kind == DrawingElementKind.BoundaryStroke)
        {
            var boundaryHandle = HitTestLineBezierHandle(screen, SelectedElement);
            if (boundaryHandle != EditHandleKind.None) return boundaryHandle;
        }
        if (_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex)
            && (shape is not ShapeKind.Line and not ShapeKind.Freeform
                || !_selectedElements.Any(hit => hit.Key.ObjectIndex == objectIndex && hit.Key.Kind == DrawingElementKind.Stroke)))
        {
            return EditHandleKind.None;
        }

        if (shape == ShapeKind.Text)
        {
            if (!TextAreaResizeHandlesVisible(objectIndex)) return EditHandleKind.None;
            foreach (var handle in TextAreaResizeHandles())
            {
                if (Distance(screen, WorldToScreen(GetTextAreaHandleWorldPoint(objectIndex, handle))) <= 12)
                {
                    return handle;
                }
            }

            return EditHandleKind.None;
        }
        if (shape == ShapeKind.Freeform)
        {
            var selectedFreeformPart = SelectedElement.IsValid
                && SelectedElement.Key.ObjectIndex == objectIndex
                && SelectedElement.Key.Kind == DrawingElementKind.Stroke
                ? SelectedElement
                : _selectedElements.FirstOrDefault(hit =>
                    hit.Key.ObjectIndex == objectIndex
                    && hit.Key.Kind == DrawingElementKind.Stroke);
            return selectedFreeformPart.IsValid
                ? HitTestLineBezierHandle(screen, selectedFreeformPart)
                : EditHandleKind.None;
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

    internal PointF GetTextAreaHandleWorldPoint(int objectIndex, EditHandleKind handle)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            return PointF.Empty;
        }

        var halfWidth = Scene.Width[objectIndex] * 0.5f;
        var local = handle switch
        {
            EditHandleKind.TextAreaLeft => new PointF(-halfWidth, 0),
            EditHandleKind.TextAreaRight => new PointF(halfWidth, 0),
            _ => PointF.Empty
        };
        return LocalToWorld(objectIndex, local);
    }

    internal PointF[] GetTextAreaWorldCorners(int objectIndex)
    {
        if ((uint)objectIndex >= Scene.ObjectCount || Scene.ShapeKind[objectIndex] != ShapeKind.Text)
        {
            return [];
        }

        var halfWidth = Scene.Width[objectIndex] * 0.5f;
        var halfHeight = Scene.Height[objectIndex] * 0.5f;
        return
        [
            LocalToWorld(objectIndex, new PointF(-halfWidth, -halfHeight)),
            LocalToWorld(objectIndex, new PointF(halfWidth, -halfHeight)),
            LocalToWorld(objectIndex, new PointF(halfWidth, halfHeight)),
            LocalToWorld(objectIndex, new PointF(-halfWidth, halfHeight))
        ];
    }

    internal bool TextAreaOverlayVisible(int objectIndex)
    {
        return !(TransformMode || DistortMode)
            && (uint)objectIndex < Scene.ObjectCount
            && Scene.ShapeKind[objectIndex] == ShapeKind.Text
            && !(ReferenceEquals(_editingTextScene, Scene) && _editingTextObject == objectIndex);
    }

    internal bool TextAreaResizeHandlesVisible(int objectIndex)
    {
        return TextAreaOverlayVisible(objectIndex)
            && _selectedElements.Length == 0
            && _selectedObjects.Length == 1
            && _selectedObject == objectIndex;
    }

    public void SetTransformOverlay(
        bool transformMode,
        RectangleF bounds,
        PointF? focus = null,
        Matrix4x4? referenceTransform = null)
    {
        var frame = bounds.Width > 0.001f && bounds.Height > 0.001f
            ? TransformOverlayFrame.FromBounds(bounds)
            : default;
        SetTransformOverlay(transformMode, frame, focus, referenceTransform);
    }

    public void SetTransformOverlay(
        bool transformMode,
        TransformOverlayFrame frame,
        PointF? focus = null,
        Matrix4x4? referenceTransform = null)
    {
        var visible = transformMode && frame.IsValid;
        var nextFrame = visible ? frame : default;
        var nextBounds = visible ? frame.Bounds : RectangleF.Empty;
        var nextFocus = visible
            ? focus ?? frame.Center
            : PointF.Empty;
        var nextUsesReference = visible
            && referenceTransform is { } candidate
            && IsValidTransformOverlayReference(candidate);
        var nextReferenceTransform = nextUsesReference ? referenceTransform!.Value : Matrix4x4.Identity;
        if (TransformMode == transformMode
            && TransformBoundsVisible == visible
            && _transformFrame == nextFrame
            && _transformFocus == nextFocus
            && _transformOverlayUsesReferenceProjection == nextUsesReference
            && _transformOverlayFlatToScene == nextReferenceTransform)
        {
            return;
        }

        TransformMode = transformMode;
        TransformBoundsVisible = visible;
        _transformFrame = nextFrame;
        _transformBounds = nextBounds;
        _transformFocus = nextFocus;
        _transformOverlayUsesReferenceProjection = nextUsesReference;
        _transformOverlayFlatToScene = nextReferenceTransform;
        InvalidateSelectionState();
    }

    public void SetDistortOverlay(
        bool distortMode,
        DistortEnvelope envelope,
        Matrix4x4? referenceTransform = null)
    {
        var visible = distortMode && envelope.IsValid;
        var nextFrame = visible ? envelope : default;
        var nextUsesReference = visible
            && referenceTransform is { } candidate
            && IsValidTransformOverlayReference(candidate);
        var nextReferenceTransform = nextUsesReference ? referenceTransform!.Value : Matrix4x4.Identity;
        if (DistortMode == distortMode
            && DistortBoundsVisible == visible
            && _distortFrame == nextFrame
            && _transformOverlayUsesReferenceProjection == nextUsesReference
            && _transformOverlayFlatToScene == nextReferenceTransform)
        {
            return;
        }

        DistortMode = distortMode;
        DistortBoundsVisible = visible;
        _distortFrame = nextFrame;
        _transformOverlayUsesReferenceProjection = nextUsesReference;
        _transformOverlayFlatToScene = nextReferenceTransform;
        InvalidateSelectionState();
    }

    public void SetDrawingObjectSelectionOverlay(RectangleF bounds, PointF? anchor = null)
    {
        var visible = bounds.Width > 0.001f && bounds.Height > 0.001f;
        var anchorVisible = visible
            && anchor is { } point
            && float.IsFinite(point.X)
            && float.IsFinite(point.Y);
        var nextAnchor = anchorVisible ? anchor!.Value : PointF.Empty;
        if (DrawingObjectSelectionVisible == visible
            && _drawingObjectSelectionBounds == (visible ? bounds : RectangleF.Empty)
            && DrawingObjectAnchorVisible == anchorVisible
            && _drawingObjectAnchor == nextAnchor)
        {
            return;
        }

        DrawingObjectSelectionVisible = visible;
        _drawingObjectSelectionBounds = visible ? bounds : RectangleF.Empty;
        DrawingObjectAnchorVisible = anchorVisible;
        _drawingObjectAnchor = nextAnchor;
        InvalidateSelectionState();
    }

    public TransformHandleKind HitTestTransformHandle(Point screen)
    {
        if (!TransformBoundsVisible) return TransformHandleKind.None;
        var geometry = GetTransformOverlayScreenGeometry();
        var nearestHandle = TransformHandleKind.None;
        var nearestDistance = float.PositiveInfinity;
        foreach (var (kind, point, _) in geometry.ResizeHandles)
        {
            ConsiderHandle(kind, point, 10f);
        }

        foreach (var (kind, point, _) in geometry.RotationHandles)
        {
            ConsiderHandle(kind, point, 11f);
        }

        foreach (var (kind, point, _) in geometry.SkewHandles)
        {
            ConsiderHandle(kind, point, 10f);
        }
        if (nearestHandle != TransformHandleKind.None) return nearestHandle;

        if (IsTransformFocusVisible(geometry)
            && Distance(screen, TransformOverlayPointToScreen(_transformFocus)) <= 10)
        {
            return TransformHandleKind.Focus;
        }

        return geometry.Contains(screen) ? TransformHandleKind.Move : TransformHandleKind.None;

        void ConsiderHandle(TransformHandleKind kind, PointF point, float hitRadius)
        {
            var distance = Distance(screen, point);
            if (distance > hitRadius || distance >= nearestDistance) return;
            nearestDistance = distance;
            nearestHandle = kind;
        }
    }

    internal bool IsTransformFocusVisible(TransformOverlayScreenGeometry geometry)
    {
        var focus = TransformOverlayPointToScreen(_transformFocus);
        const float minimumHandleSeparation = 20f;
        return geometry.ResizeHandles.All(handle => Distance(focus, handle.Point) > minimumHandleSeparation)
            && geometry.RotationHandles.All(handle => Distance(focus, handle.Point) > minimumHandleSeparation)
            && geometry.SkewHandles.All(handle => Distance(focus, handle.Point) > minimumHandleSeparation);
    }

    public TransformHandleKind HitTestDistortHandle(Point screen)
    {
        if (!DistortBoundsVisible) return TransformHandleKind.None;
        var geometry = GetDistortOverlayScreenGeometry();
        foreach (var (kind, point, _) in geometry.Handles)
        {
            if (Distance(screen, point) <= 11) return kind;
        }

        return geometry.Contains(screen) ? TransformHandleKind.Move : TransformHandleKind.None;
    }

    /// <summary>
    /// Resolves the actual curved-envelope handle under the pointer.  The legacy
    /// TransformHandleKind hit test above remains for callers that only understand
    /// the original eight handles.
    /// </summary>
    internal bool TryHitTestDistortVisualHandle(Point screen, out DistortHandleRef handle)
    {
        handle = default;
        if (!DistortBoundsVisible) return false;
        var geometry = GetDistortOverlayScreenGeometry();
        var bestDistance = 11f;
        var found = false;
        foreach (var visual in geometry.VisualHandles)
        {
            var distance = Distance(screen, visual.Point);
            if (distance > bestDistance) continue;
            // Anchors win ties with tangent controls, which makes corner picking
            // deterministic when a control is close to its anchor.
            if (found
                && distance >= bestDistance - 0.001f
                && visual.Reference.Kind != DistortHandleKind.Anchor)
            {
                continue;
            }
            bestDistance = distance;
            handle = visual.Reference;
            found = true;
        }

        return found;
    }

    internal bool TryHitTestDistortBoundary(Point screen, out DistortBoundaryHit hit)
    {
        hit = default;
        if (!DistortBoundsVisible) return false;
        var maximumDistance = Math.Max(0.001f, TransformOverlayScreenLengthToWorld(12f));
        return _distortFrame.TryFindNearestBoundary(
            TransformOverlayScreenToWorld(screen),
            maximumDistance,
            out hit);
    }

    internal DistortOverlayScreenGeometry GetDistortOverlayScreenGeometry()
    {
        if (!_distortFrame.IsValid)
        {
            return new DistortOverlayScreenGeometry([], [], [], [], []);
        }

        var envelope = _distortFrame;
        var sides = Enum.GetValues<DistortSide>()
            .Select(side => envelope.GetBoundaryPolyline(side)
                .Select(TransformOverlayPointToScreen)
                .ToArray())
            .ToArray();
        var contour = envelope.GetBoundaryContour()
            .Select(TransformOverlayPointToScreen)
            .ToArray();
        var segments = Enum.GetValues<DistortSide>()
            .SelectMany(side => envelope.GetSegments(side))
            .Select(segment => new DistortOverlayBezierSegment(
                segment.Side,
                segment.StartAnchorId,
                segment.EndAnchorId,
                TransformOverlayPointToScreen(segment.Start),
                TransformOverlayPointToScreen(segment.Control1),
                TransformOverlayPointToScreen(segment.Control2),
                TransformOverlayPointToScreen(segment.End)))
            .ToArray();
        var visualHandles = envelope.GetVisualHandles()
            .Select(handle =>
            {
                var anchorRef = handle.Reference with { Kind = DistortHandleKind.Anchor };
                var anchor = envelope.TryGetHandlePosition(anchorRef, out var anchorPoint)
                    ? TransformOverlayPointToScreen(anchorPoint)
                    : TransformOverlayPointToScreen(handle.Position);
                return new DistortOverlayVisualHandle(
                    handle.Reference,
                    TransformOverlayPointToScreen(handle.Position),
                    anchor);
            })
            .ToArray();

        var topLeft = sides.Length > 0 && sides[0].Length > 0 ? sides[0][0] : PointF.Empty;
        var topRight = sides.Length > 0 && sides[0].Length > 0 ? sides[0][^1] : PointF.Empty;
        var bottomRight = sides.Length > 2 && sides[2].Length > 0 ? sides[2][^1] : PointF.Empty;
        var bottomLeft = sides.Length > 2 && sides[2].Length > 0 ? sides[2][0] : PointF.Empty;
        var top = Midpoint(topLeft, topRight);
        var right = sides.Length > 1 && sides[1].Length > 0
            ? sides[1][sides[1].Length / 2]
            : Midpoint(topRight, bottomRight);
        var bottom = sides.Length > 2 && sides[2].Length > 0
            ? sides[2][sides[2].Length / 2]
            : Midpoint(bottomLeft, bottomRight);
        var left = sides.Length > 3 && sides[3].Length > 0
            ? sides[3][sides[3].Length / 2]
            : Midpoint(topLeft, bottomLeft);
        return new DistortOverlayScreenGeometry(
            sides,
            contour,
            segments,
            visualHandles,
            [
                new(TransformHandleKind.TopLeft, topLeft, topLeft),
                new(TransformHandleKind.Top, top, top),
                new(TransformHandleKind.TopRight, topRight, topRight),
                new(TransformHandleKind.Right, right, right),
                new(TransformHandleKind.BottomRight, bottomRight, bottomRight),
                new(TransformHandleKind.Bottom, bottom, bottom),
                new(TransformHandleKind.BottomLeft, bottomLeft, bottomLeft),
                new(TransformHandleKind.Left, left, left)
            ]);
    }

    internal TransformOverlayScreenGeometry GetTransformOverlayScreenGeometry()
    {
        var topLeft = TransformOverlayPointToScreen(_transformFrame.TopLeft);
        var topRight = TransformOverlayPointToScreen(_transformFrame.TopRight);
        var bottomRight = TransformOverlayPointToScreen(_transformFrame.BottomRight);
        var bottomLeft = TransformOverlayPointToScreen(_transformFrame.BottomLeft);
        var center = Midpoint(topLeft, bottomRight);
        var top = Midpoint(topLeft, topRight);
        var right = Midpoint(topRight, bottomRight);
        var bottom = Midpoint(bottomLeft, bottomRight);
        var left = Midpoint(topLeft, bottomLeft);
        var axisX = UnitVector(topLeft, topRight);
        var axisY = UnitVector(topLeft, bottomLeft);
        const float offset = 19;

        return new TransformOverlayScreenGeometry(
            topLeft,
            topRight,
            bottomRight,
            bottomLeft,
            [
                new(TransformHandleKind.TopLeft, topLeft, topLeft),
                new(TransformHandleKind.Top, top, top),
                new(TransformHandleKind.TopRight, topRight, topRight),
                new(TransformHandleKind.Right, right, right),
                new(TransformHandleKind.BottomRight, bottomRight, bottomRight),
                new(TransformHandleKind.Bottom, bottom, bottom),
                new(TransformHandleKind.BottomLeft, bottomLeft, bottomLeft),
                new(TransformHandleKind.Left, left, left)
            ],
            [
                new(TransformHandleKind.RotateTopLeft, Offset(topLeft, axisX, -offset, axisY, -offset), topLeft),
                new(TransformHandleKind.RotateTopRight, Offset(topRight, axisX, offset, axisY, -offset), topRight),
                new(TransformHandleKind.RotateBottomRight, Offset(bottomRight, axisX, offset, axisY, offset), bottomRight),
                new(TransformHandleKind.RotateBottomLeft, Offset(bottomLeft, axisX, -offset, axisY, offset), bottomLeft)
            ],
            [
                new(TransformHandleKind.SkewTop, OffsetFromEdge(topLeft, topRight, top, center, offset), top),
                new(TransformHandleKind.SkewRight, OffsetFromEdge(topRight, bottomRight, right, center, offset), right),
                new(TransformHandleKind.SkewBottom, OffsetFromEdge(bottomLeft, bottomRight, bottom, center, offset), bottom),
                new(TransformHandleKind.SkewLeft, OffsetFromEdge(topLeft, bottomLeft, left, center, offset), left)
            ]);
    }

    internal PointF TransformOverlayPointToScreen(PointF point)
    {
        if (_transformOverlayUsesReferenceProjection)
        {
            var scene = Vector3.Transform(
                new Vector3(point.X, point.Y, 0),
                _transformOverlayFlatToScene);
            if (TryProjectScenePosition(scene, out var screen, out _)) return screen;
        }
        return WorldToScreen(point);
    }

    internal PointF TransformOverlayScreenToWorld(Point screen)
    {
        return TryGetTransformOverlayReferencePoint(screen, out var point)
            ? point
            : ScreenToWorld(screen);
    }

    private float TransformOverlayScreenLengthToWorld(float pixels)
    {
        if (!_transformOverlayUsesReferenceProjection) return ScreenLengthToWorld(pixels);
        var distortBounds = _distortFrame.Bounds;
        var center = TransformMode
            ? _transformFrame.Center
            : new PointF(
                distortBounds.Left + distortBounds.Width * 0.5f,
                distortBounds.Top + distortBounds.Height * 0.5f);
        var screen = TransformOverlayPointToScreen(center);
        var distances = new List<float>(2);
        if (TryGetTransformOverlayReferencePoint(
                Point.Round(new PointF(screen.X + pixels, screen.Y)),
                out var horizontal))
        {
            distances.Add(Distance(center, horizontal));
        }
        if (TryGetTransformOverlayReferencePoint(
                Point.Round(new PointF(screen.X, screen.Y + pixels)),
                out var vertical))
        {
            distances.Add(Distance(center, vertical));
        }
        return distances.Count > 0 ? distances.Max() : ScreenLengthToWorld(pixels);
    }

    private bool TryGetTransformOverlayReferencePoint(Point screen, out PointF point)
    {
        point = PointF.Empty;
        if (!_transformOverlayUsesReferenceProjection
            || !TryGetReferenceRay(screen, out var ray))
        {
            return false;
        }

        var origin = Vector3.Transform(Vector3.Zero, _transformOverlayFlatToScene);
        var axisX = Vector3.TransformNormal(Vector3.UnitX, _transformOverlayFlatToScene);
        var axisY = Vector3.TransformNormal(Vector3.UnitY, _transformOverlayFlatToScene);
        var normal = Vector3.Cross(axisX, axisY);
        var denominator = Vector3.Dot(ray.Direction, normal);
        if (normal.LengthSquared() <= 0.00000001f || Math.Abs(denominator) <= 0.00001f)
        {
            return false;
        }

        var distance = Vector3.Dot(origin - ray.Origin, normal) / denominator;
        var offset = ray.Origin + ray.Direction * distance - origin;
        var xx = Vector3.Dot(axisX, axisX);
        var xy = Vector3.Dot(axisX, axisY);
        var yy = Vector3.Dot(axisY, axisY);
        var dx = Vector3.Dot(offset, axisX);
        var dy = Vector3.Dot(offset, axisY);
        var determinant = xx * yy - xy * xy;
        if (!float.IsFinite(determinant) || Math.Abs(determinant) <= 0.00000001f) return false;
        point = new PointF(
            (dx * yy - dy * xy) / determinant,
            (dy * xx - dx * xy) / determinant);
        return float.IsFinite(point.X) && float.IsFinite(point.Y);
    }

    private static bool IsValidTransformOverlayReference(Matrix4x4 transform)
    {
        var origin = Vector3.Transform(Vector3.Zero, transform);
        var axisX = Vector3.TransformNormal(Vector3.UnitX, transform);
        var axisY = Vector3.TransformNormal(Vector3.UnitY, transform);
        var normal = Vector3.Cross(axisX, axisY);
        return float.IsFinite(origin.X) && float.IsFinite(origin.Y) && float.IsFinite(origin.Z)
            && float.IsFinite(axisX.X) && float.IsFinite(axisX.Y) && float.IsFinite(axisX.Z)
            && float.IsFinite(axisY.X) && float.IsFinite(axisY.Y) && float.IsFinite(axisY.Z)
            && normal.LengthSquared() > 0.00000001f;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg is WmPointerDown or WmPointerUpdate or WmPointerUp or WmPointerCaptureChanged
            && TryDispatchPenPointer(ref message))
        {
            return;
        }

        base.WndProc(ref message);
    }

    private bool TryDispatchPenPointer(ref Message message)
    {
        var pointerId = unchecked((uint)message.WParam.ToInt64()) & 0xffffu;
        if (pointerId == 0) return false;

        var wasHandled = _handledPenPointers.Contains(pointerId);
        if (message.Msg == WmPointerCaptureChanged)
        {
            if (!wasHandled) return false;
            _handledPenPointers.Remove(pointerId);
            var captureLost = new PenPointerEventArgs(
                pointerId,
                PenPointerEventKind.CaptureLost,
                Array.Empty<PenPointerSample>())
            {
                Handled = true
            };
            BeginInteractiveInput(pointerDown: false);
            try
            {
                PenPointerInput?.Invoke(this, captureLost);
            }
            finally
            {
                EndInteractiveInput(pointerDown: false, forceFinalFrame: true);
            }
            message.Result = IntPtr.Zero;
            return true;
        }

        var hasPenInfo = TryReadPenPointerSamples(pointerId, out var samples, out var pointerFlags);
        if (!hasPenInfo && !wasHandled) return false;

        var kind = (pointerFlags & PointerFlags.Canceled) != 0
            ? PenPointerEventKind.CaptureLost
            : message.Msg switch
            {
                WmPointerDown => PenPointerEventKind.Down,
                WmPointerUp => PenPointerEventKind.Up,
                _ => PenPointerEventKind.Update
            };
        var eventArgs = new PenPointerEventArgs(pointerId, kind, samples)
        {
            Handled = wasHandled
        };
        var pointerDown = kind == PenPointerEventKind.Down;
        BeginInteractiveInput(pointerDown);
        try
        {
            PenPointerInput?.Invoke(this, eventArgs);
        }
        finally
        {
            EndInteractiveInput(
                pointerDown,
                forceFinalFrame: eventArgs.Handled
                    && (kind is PenPointerEventKind.Down or PenPointerEventKind.Up or PenPointerEventKind.CaptureLost),
                allowSynchronousPresent: eventArgs.Handled);
        }

        if (message.Msg == WmPointerDown && eventArgs.Handled) _handledPenPointers.Add(pointerId);
        if (kind is PenPointerEventKind.Up or PenPointerEventKind.CaptureLost) _handledPenPointers.Remove(pointerId);
        if (!eventArgs.Handled) return false;

        message.Result = IntPtr.Zero;
        return true;
    }

    private bool TryReadPenPointerSamples(
        uint pointerId,
        out PenPointerSample[] samples,
        out PointerFlags currentFlags)
    {
        samples = [];
        currentFlags = PointerFlags.None;
        if (!GetPointerPenInfo(pointerId, out var current)
            || current.PointerInfo.PointerType != PointerInputType.Pen)
        {
            return false;
        }

        currentFlags = current.PointerInfo.PointerFlags;
        var historyCount = (int)Math.Clamp(
            current.PointerInfo.HistoryCount,
            1u,
            (uint)MaxPenHistoryEntries);
        if (historyCount <= 1)
        {
            samples = [CreatePenPointerSample(current)];
            return true;
        }

        if (_penHistoryBuffer.Length < historyCount)
        {
            var capacity = Math.Min(
                MaxPenHistoryEntries,
                Math.Max(historyCount, _penHistoryBuffer.Length * 2));
            Array.Resize(ref _penHistoryBuffer, capacity);
        }

        var entriesCount = (uint)Math.Min(historyCount, _penHistoryBuffer.Length);
        if (!GetPointerPenInfoHistory(pointerId, ref entriesCount, _penHistoryBuffer))
        {
            samples = [CreatePenPointerSample(current)];
            return true;
        }

        var count = Math.Min((int)entriesCount, _penHistoryBuffer.Length);
        var ordered = new List<PenPointerSample>(count);
        for (var index = count - 1; index >= 0; index--)
        {
            var entry = _penHistoryBuffer[index];
            if (entry.PointerInfo.PointerType != PointerInputType.Pen) continue;
            ordered.Add(CreatePenPointerSample(entry));
        }

        samples = ordered.Count > 0 ? ordered.ToArray() : [CreatePenPointerSample(current)];
        return true;
    }

    private PenPointerSample CreatePenPointerSample(PointerPenInfo info)
    {
        var pointer = info.PointerInfo;
        var location = PointToClient(new Point(pointer.PixelLocation.X, pointer.PixelLocation.Y));
        var pressure = (info.PenMask & PenMask.Pressure) != 0
            ? Math.Clamp(info.Pressure / 1024f, 0f, 1f)
            : float.NaN;
        return new PenPointerSample(
            location,
            pressure,
            (pointer.PointerFlags & PointerFlags.InContact) != 0,
            (pointer.PointerFlags & PointerFlags.Primary) != 0,
            (info.PenFlags & (PenFlags.Inverted | PenFlags.Eraser)) != 0,
            (info.PenFlags & PenFlags.Barrel) != 0,
            pointer.Time);
    }

    private enum PointerInputType : uint
    {
        Pen = 3
    }

    [Flags]
    private enum PointerFlags : uint
    {
        None = 0,
        InContact = 0x00000004,
        Primary = 0x00002000,
        Canceled = 0x00008000
    }

    [Flags]
    private enum PenFlags : uint
    {
        None = 0,
        Barrel = 0x00000001,
        Inverted = 0x00000002,
        Eraser = 0x00000004
    }

    [Flags]
    private enum PenMask : uint
    {
        None = 0,
        Pressure = 0x00000001
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public PointerInputType PointerType;
        public uint PointerId;
        public uint FrameId;
        public PointerFlags PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr WindowTarget;
        public NativePoint PixelLocation;
        public NativePoint HimetricLocation;
        public NativePoint PixelLocationRaw;
        public NativePoint HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo PointerInfo;
        public PenFlags PenFlags;
        public PenMask PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo penInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerPenInfoHistory(
        uint pointerId,
        ref uint entriesCount,
        [Out] PointerPenInfo[] penInfo);

    protected override void OnPaint(PaintEventArgs e)
    {
        var frameStarted = Stopwatch.GetTimestamp();
        UpdateSelectionHighlightAnimation();
        var requestedAt = _interactiveFrameRequestedAt;
        var reportsPointerDown = _pointerDownAwaitingPresent && _pointerDownStartedAt != 0;
        var renderedSelectionDragPreviewRevision = SelectionDragPreviewActive
            ? _selectionDragPreviewRevision
            : 0;
        var reportsDragFirstMove = _dragFirstMoveAwaitingPresent
            && _dragFirstMoveStartedAt != 0
            && (!_dragFirstMoveRequiresSelectionPreview || renderedSelectionDragPreviewRevision != 0);
        if (requestedAt != 0)
        {
            LastInteractiveRequestToPaintMilliseconds = ElapsedMilliseconds(requestedAt, frameStarted);
        }
        if (reportsPointerDown)
        {
            LastPointerDownToPaintMilliseconds = ElapsedMilliseconds(_pointerDownStartedAt, frameStarted);
        }
        var rendered = false;
        _paintInProgress = true;
        try
        {
            if (_direct2DRenderer.TryRenderReference3DPlaybackGdi(this, e.Graphics, out var fastStats))
            {
                LastStats = fastStats;
                LastFrameUsedDirect2D = false;
                _paintFailureLogged = false;
                rendered = true;
            }
            else if (_direct2DRenderer.TryRender(this, out var stats))
            {
                if (!HasSoftwareDistortionForCurrentFrame())
                {
                    ClearGdiBaseFrameCache();
                    ClearDistortRasterCache();
                }
                LastStats = stats;
                LastFrameUsedDirect2D = true;
                _paintFailureLogged = false;
            }
            else
            {
                if (!_direct2DRenderer.TryPresentSoftwareFrame(this, DrawGdiFrame))
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
        finally
        {
            _paintInProgress = false;
        }

        if (rendered)
        {
            var presentedAt = Stopwatch.GetTimestamp();
            _lastFramePresentedAt = presentedAt;
            _lastFrameRenderMilliseconds = ElapsedMilliseconds(frameStarted, presentedAt);
            _presentedSelectionDragPreviewRevision = renderedSelectionDragPreviewRevision;
            _basePresentationInvalidationPending = false;
            _interactiveFrameRequested = false;
            _interactiveFrameRequestedAt = 0;
            if (reportsPointerDown)
            {
                _pointerDownAwaitingPresent = false;
                LastPointerDownToPresentMilliseconds = ElapsedMilliseconds(_pointerDownStartedAt, presentedAt);
                _pointerDownStartedAt = 0;
                LogSlowPointerFeedback(presentedAt);
            }
            if (reportsDragFirstMove)
            {
                _dragFirstMoveAwaitingPresent = false;
                LastDragFirstMoveTotalMilliseconds = ElapsedMilliseconds(_dragFirstMoveStartedAt, presentedAt);
                LastDragFirstPresentMilliseconds = ElapsedMilliseconds(_dragFirstMovePresentRequestedAt, presentedAt);
                CompleteDragFirstMoveTelemetryIfReady();
            }
            if (MarqueeVisible
                && !MarqueeOverlayActive
                && !MarqueeLodPreviewActive
                && _lastFrameRenderMilliseconds > MarqueePreviewFrameBudgetMilliseconds)
            {
                MarqueeLodPreviewActive = true;
                Invalidate();
            }
            FrameRendered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LogSlowPointerFeedback(long presentedAt)
    {
        if (LastPointerDownToPresentMilliseconds < SlowPointerFeedbackMilliseconds
            || _lastSlowPointerLogAt != 0
                && ElapsedMilliseconds(_lastSlowPointerLogAt, presentedAt) < SlowPointerLogIntervalMilliseconds)
        {
            return;
        }

        _lastSlowPointerLogAt = presentedAt;
        var backendTiming = LastFrameUsedDirect2D
            ? $"commands={LastDirect2DCommandMilliseconds:0.0} ms, present={LastDirect2DPresentMilliseconds:0.0} ms"
            : "commands=n/a, present=n/a";
        AppLog.Warn(
            $"Slow Stage pointer feedback: total={LastPointerDownToPresentMilliseconds:0.0} ms, "
            + $"handler={LastPointerDownHandlerMilliseconds:0.0} ms, queue={Math.Max(0, LastPointerDownToPaintMilliseconds - LastPointerDownHandlerMilliseconds):0.0} ms, "
            + $"frame={LastFrameRenderMilliseconds:0.0} ms, {backendTiming}, requests={LastInteractiveRequestCount}, "
            + $"coalesced={LastInteractiveCoalescedRequestCount}, backend={(LastFrameUsedDirect2D ? "Direct2D" : "GDI")}.");
    }

    private void DrawBufferedGdi(Graphics target)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var buffer = BufferedGraphicsManager.Current.Allocate(target, ClientRectangle);
        DrawGdiFrame(buffer.Graphics);
        buffer.Render(target);
    }

    private void DrawGdiFrame(Graphics graphics)
    {
        ResetLastGdiFrameTelemetry();
        if (ShouldCacheGdiBaseFrame())
        {
            try
            {
                DrawCachedGdi2D(graphics);
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
            {
                ClearGdiBaseFrameCache();
                DrawGdi(graphics);
            }
        }
        else
        {
            ClearGdiBaseFrameCache();
            if (!HasSoftwareDistortionForCurrentFrame()) ClearDistortRasterCache();
            DrawGdi(graphics);
        }
    }

    private bool ShouldCacheGdiBaseFrame()
    {
        return !PlaybackActive
            && !HasDistortPreview
            && (RendersReferenceProjection || HasSoftwareDistortionForCurrentFrame());
    }

    private bool HasSoftwareDistortionForCurrentFrame()
    {
        return SceneHasDistortionsForRendering(Scene)
            || SceneHasDistortionsForRendering(UnderlayScene)
            || SceneHasDistortionsForRendering(OnionSkinScene)
            || SceneHasDistortionsForRendering(DragPreviewScene);
    }

    private void DrawCachedGdi2D(Graphics graphics)
    {
        if (!CanReuseGdiBaseFrame())
        {
            EnsureGdiBaseFrameBitmap();
            using var baseGraphics = Graphics.FromImage(_gdiBaseFrameBitmap!);
            var editableStats = RendersReferenceProjection
                ? DrawGdiReferenceBase(baseGraphics)
                : DrawGdiBase2D(baseGraphics);
            StoreGdiBaseFrame(editableStats);
            LastGdiBaseFrameCacheBuilds = 1;
        }
        else
        {
            ReplayGdiBaseScenePassOrder();
            LastStats = _gdiBaseFrameStats;
            LastGdiBaseFrameCacheReuses = 1;
        }

        var copyStarted = Stopwatch.GetTimestamp();
        var copyState = graphics.Save();
        try
        {
            graphics.ResetTransform();
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_gdiBaseFrameBitmap!, 0, 0);
        }
        finally
        {
            graphics.Restore(copyState);
        }
        LastGdiBaseFrameCopyMilliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (RendersReferenceProjection) DrawGdiReferenceDynamic(graphics);
        else DrawGdiDynamic2D(graphics, _gdiBaseFrameEditableStats);
    }

    private bool CanReuseGdiBaseFrame()
    {
        return _gdiBaseFrameBitmap is not null
            && _gdiBaseFrameSize == ClientSize
            && _gdiBaseFrameRenderState == CreateReference3DBaseFrameRenderState();
    }

    private void EnsureGdiBaseFrameBitmap()
    {
        if (_gdiBaseFrameBitmap is not null && _gdiBaseFrameSize == ClientSize) return;
        ClearGdiBaseFrameCache();
        _gdiBaseFrameBitmap = new Bitmap(
            ClientSize.Width,
            ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        _gdiBaseFrameSize = ClientSize;
    }

    private void StoreGdiBaseFrame(RenderStats editableStats)
    {
        _gdiBaseFrameRenderState = CreateReference3DBaseFrameRenderState();
        _gdiBaseFrameStats = LastStats;
        _gdiBaseFrameEditableStats = editableStats;
    }

    private void ReplayGdiBaseScenePassOrder()
    {
        BeginScenePassOrder();
        if (OnionSkinScene is not null) RecordOnionSkinScenePass();
        if (UnderlayScene is not null) RecordUnderlayScenePass();
        RecordEditableScenePass();
    }

    private void ClearGdiBaseFrameCache()
    {
        _gdiBaseFrameBitmap?.Dispose();
        _gdiBaseFrameBitmap = null;
        _gdiBaseFrameSize = default;
        _gdiBaseFrameRenderState = default;
        _gdiBaseFrameStats = default;
        _gdiBaseFrameEditableStats = default;
    }

    private void ResetLastGdiFrameTelemetry()
    {
        LastGdiDistortRasterBuilds = 0;
        LastGdiDistortRasterReuses = 0;
        LastGdiBaseFrameCacheBuilds = 0;
        LastGdiBaseFrameCacheReuses = 0;
        LastGdiBaseFrameCopyMilliseconds = 0;
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
        if (RendersReferenceProjection)
        {
            DrawGdiReferenceBase(g);
            DrawGdiReferenceDynamic(g);
            return;
        }

        ResetLastGdiFrameTelemetry();
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;
        DrawGrid(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        BeginScenePassOrder();

        var editableStats = DrawGdiBase2D(g, backgroundAlreadyDrawn: true);
        DrawGdiDynamic2D(g, editableStats);
    }

    private RenderStats DrawGdiReferenceBase(Graphics g)
    {
        ResetLastGdiFrameTelemetry();
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;
        DrawGrid(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        BeginScenePassOrder();
        LastStats = DrawReference3DScene(g);
        return LastStats;
    }

    private void DrawGdiReferenceDynamic(Graphics g)
    {
        if (PlaybackActive) return;

        if (DragPreviewScene is { } dragPreview)
        {
            var editableScene = Scene;
            Scene = dragPreview;
            try
            {
                DrawReference3DCurrentScene(g);
            }
            finally
            {
                Scene = editableScene;
            }
        }
        DrawReference3DSelection(g);
        DrawTransformOverlay(g);
        DrawDistortOverlay(g);
        DrawSnapPointOverlay(g);
        DrawMarquee(g);
        DrawShotFramingGizmoGdi(g);
        if (ReferenceDimension == SceneDimension.ThreeD)
        {
            DrawSpatialTransformGizmoGdi(g);
            DrawSceneLightGizmoGdi(g);
        }
    }

    private RenderStats DrawGdiBase2D(Graphics g, bool backgroundAlreadyDrawn = false)
    {
        if (!backgroundAlreadyDrawn)
        {
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.None;
            DrawGrid(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            BeginScenePassOrder();
        }

        var editableScene = Scene;
        var objectDrawLimit = ObjectDrawLimit();
        var underlayStats = default(RenderStats);
        var onionSkinStats = default(RenderStats);
        var underlay = UnderlayScene;
        var underlayLimit = objectDrawLimit;
        var forceEditableObjectRenderer = SelectionFillDragFrontActive
            || FillEdgeBezierPointerEditing && UsesObjectRenderer(editableScene)
            || RequiresObjectRendererForDragPreview(editableScene);
        if (underlay is not null
            && UsesObjectRenderer(underlay)
            && (forceEditableObjectRenderer || UsesObjectRenderer(editableScene)))
        {
            underlayLimit = objectDrawLimit - Math.Min(editableScene.ObjectCount, objectDrawLimit * 3 / 4);
        }

        if (OnionSkinScene is { } onionSkin)
        {
            var reservedUnderlayObjects = underlay is not null && UsesObjectRenderer(underlay)
                ? Math.Min(underlay.ObjectCount, underlayLimit)
                : 0;
            var editableCapacity = Math.Max(0, objectDrawLimit - reservedUnderlayObjects);
            var reservedEditableObjects = forceEditableObjectRenderer || UsesObjectRenderer(editableScene)
                ? Math.Min(editableScene.ObjectCount, editableCapacity)
                : 0;
            var onionSkinLimit = Math.Max(0, editableCapacity - reservedEditableObjects);
            RecordOnionSkinScenePass();
            onionSkinStats = DrawSceneGdi(g, onionSkin, onionSkinLimit);
        }

        if (underlay is not null)
        {
            RecordUnderlayScenePass();
            underlayStats = DrawSceneGdi(g, underlay, underlayLimit);
        }

        var editableLimit = Math.Max(0, objectDrawLimit - underlayStats.DrawnObjects - onionSkinStats.DrawnObjects);
        RecordEditableScenePass();
        var editableStats = DrawSceneGdi(
            g,
            editableScene,
            editableLimit,
            forceObjectRenderer: forceEditableObjectRenderer);
        LastStats = RenderStats.Combine(RenderStats.Combine(onionSkinStats, underlayStats), editableStats);
        return editableStats;
    }

    private void DrawGdiDynamic2D(Graphics g, RenderStats editableStats)
    {
        var objectDrawLimit = ObjectDrawLimit();
        if (DragPreviewScene is { } dragPreview)
        {
            DrawSceneGdi(g, dragPreview, Math.Min(objectDrawLimit, 80_000));
            // Collision terrain is owned by the editable scene and is shown only
            // as an editor overlay while the transient fracture scene is active.
            if (!PlaybackActive) DrawCollisionTerrainOverlay(g, Scene);
        }
        else if (!PlaybackActive)
        {
            // Collision terrain is editor-only data. Keep it out of the
            // ordinary scene pass while exposing the authored surface here.
            DrawCollisionTerrainOverlay(g, Scene);
        }
        if (editableStats.TileLod) DrawLodDetailObjects(g);
        if (!PlaybackActive)
        {
            if (!MarqueeLodPreviewActive) DrawActiveMaskOutline(g);
            DrawSelection(g);
            DrawSelectionSweep(g);
            DrawMotionTrack(g);
            DrawFillEdgeBezierOverlay(g);
            DrawSnapPointOverlay(g);
            DrawPenAnchorGuides(g);
            DrawDrawingPreview(g);
            DrawPenDirectionHandles(g);
            DrawFreehandPreview(g);
            DrawFillPreview(g);
            DrawFillAnimation(g);
            DrawGradientOverlay(g);
            DrawMarquee(g);
            DrawShotFramingGizmoGdi(g);
            DrawBrushTipCursor(g);
            DrawBrushColorPalette(g);
            DrawFillToolCursor(g);
        }
    }

    private RenderStats DrawSceneGdi(
        Graphics graphics,
        VectorScene scene,
        int objectDrawLimit,
        bool forceObjectRenderer = false)
    {
        var editableScene = Scene;
        Scene = scene;
        try
        {
            var pixelZoom = EffectivePixelZoom();
            if (forceObjectRenderer || scene.HasSymbolFilters)
            {
                return DrawObjects(graphics, int.MaxValue);
            }
            if (HasSceneCompositionMaskClips(Scene)) return DrawObjects(graphics, objectDrawLimit);
            if (SceneHasDistortionsForRendering(Scene)) return DrawObjects(graphics, objectDrawLimit);
            if (SceneRenderOrder.RequiresObjectRenderer(Scene)) return DrawObjects(graphics, objectDrawLimit);
            if (MarqueeLodPreviewActive && Scene.ObjectCount > 0)
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }
            if (ZoomLodPreviewActive
                && Scene.ObjectCount >= SceneRenderOrder.DenseObjectLodMinimumVisibleObjects
                && !Scene.HasDisplayLayerEffects)
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }
            if (Scene.HasDisplayLayerEffects || pixelZoom >= 0.18f) return DrawObjects(graphics, objectDrawLimit);
            if (Scene.ObjectCount >= 5000)
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }

            if (Scene.ObjectCount < SceneRenderOrder.DenseObjectLodMinimumVisibleObjects)
            {
                return DrawObjects(graphics, objectDrawLimit);
            }

            var bounds = VisibleWorldBounds();
            _renderOrder.Collect(Scene, bounds, Frame);
            if (SceneRenderOrder.ShouldUseDenseObjectLod(Scene, pixelZoom, bounds, _renderOrder))
            {
                return pixelZoom < 0.08f
                    ? DrawOverviewTiles(graphics)
                    : DrawTiles(graphics);
            }

            return DrawCollectedObjects(graphics, objectDrawLimit);
        }
        finally
        {
            Scene = editableScene;
        }
    }

    private int ObjectDrawLimit() => EffectivePixelZoom() < 0.35f ? 65_000 : 160_000;

    internal void BeginScenePassOrder()
    {
        _scenePassSequence = 0;
        LastOnionSkinScenePassOrder = 0;
        LastUnderlayScenePassOrder = 0;
        LastEditableScenePassOrder = 0;
    }

    internal void RecordOnionSkinScenePass() => LastOnionSkinScenePassOrder = ++_scenePassSequence;

    internal void RecordUnderlayScenePass() => LastUnderlayScenePassOrder = ++_scenePassSequence;

    internal void RecordEditableScenePass() => LastEditableScenePassOrder = ++_scenePassSequence;

    private bool UsesObjectRenderer(VectorScene scene)
    {
        return scene.ObjectCount > 0
            && (HasSceneCompositionMaskClips(scene)
                || SceneRenderOrder.RequiresObjectRenderer(scene)
                || scene.ObjectCount < 5000
                || scene.HasDisplayLayerEffects
                || EffectivePixelZoom() >= 0.18f);
    }

    private float EffectivePixelZoom() => Zoom * VectorUnits.PixelsPerUnit;

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // The stage is fully redrawn by Direct2D or the GDI fallback in OnPaint.
        // Letting WinForms erase the background first can produce visible flashes.
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        CompleteReferenceCameraTransition(invalidate: false);
        base.OnResize(e);
        ResetFrameSchedulerState();
        _direct2DRenderer.Resize(ClientSize);
        ClearGdiBaseFrameCache();
        if (_pendingVisibleWorldWidth is { } visibleWorldWidth) SetVisibleWorldWidthCore(visibleWorldWidth, raiseViewChanged: false);
        if (MarqueeVisible && MarqueeOverlayActive) UpdateMarqueeOverlay();
        Invalidate();
        RaiseViewChanged();
    }

    private void RaiseViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _marqueeOverlay.Prepare();
        UpdateSelectionHighlightAnimation();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _marqueeOverlay.Prepare();
        else
        {
            CompleteReferenceCameraTransition(invalidate: false);
            CompleteSpatialTransformGizmoMotion(invalidate: false);
            _marqueeOverlay.Hide();
            ClearLassoPreviewForLifecycle();
        }
        UpdateSelectionHighlightAnimation();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CompleteReferenceCameraTransition(invalidate: false);
        CompleteSpatialTransformGizmoMotion(invalidate: false);
        ResetFrameSchedulerState();
        _zoomLodPreviewTimer.Stop();
        _reference3DOpticalPreviewTimer.Stop();
        _marqueeOverlay.Hide();
        ClearLassoPreviewForLifecycle();
        _handledPenPointers.Clear();
        if (!_disposingResources) _selectionHighlightTimer.Stop();
        try
        {
            _direct2DRenderer.ReleaseTarget();
            ClearGdiBaseFrameCache();
            ClearCollisionTerrainOverlayPath();
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
            _disposingResources = true;
            InvalidateReference3DRenderPlanCache();
            ResetFrameSchedulerState();
            ResetFillEdgeBezierOverlay(invalidate: false);
            _fillAnimationTimer.Dispose();
            _selectionSweepTimer.Dispose();
            _selectionHighlightTimer.Dispose();
            _zoomLodPreviewTimer.Dispose();
            _reference3DOpticalPreviewTimer.Dispose();
            DisposeReferenceCameraTransitions();
            DisposeSpatialTransformGizmoMotion();
            _direct2DRenderer.Dispose();
            ClearGdiBaseFrameCache();
            ClearCollisionTerrainOverlayPath();
            _layerBlendCompositor?.Dispose();
            _layerBlendCompositor = null;
            _mixingBrushRasterCache.Dispose();
            ClearDistortPreviewCore(invalidate: false);
            ImportedSvgRasterizer.ClearCache();
            ClearLassoPreviewForLifecycle();
            _lassoPreviewGdiPath.Dispose();
            _marqueeOverlay.Dispose();
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
            _textAreaGlowPen.Dispose();
            _textAreaPen.Dispose();
            _textAreaHandleBrush.Dispose();
            _handleBorderPen.Dispose();
        }

        base.Dispose(disposing);
    }

    public void SetDrawingPreview(
        PointF start,
        PointF end,
        ShapeKind shape,
        Color color,
        float stroke,
        int shapeVertexCount = 0)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewEnd = end;
        DrawingPreviewControl = new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
        DrawingPreviewHasCurve = false;
        DrawingPreviewControl2 = DrawingPreviewControl;
        DrawingPreviewCurveSegments = Array.Empty<CubicDrawingPreviewSegment>();
        DrawingPreviewShape = shape;
        DrawingPreviewShapeVertexCount = shapeVertexCount;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        InvalidateOverlay();
    }

    public void SetCurveDrawingPreview(PointF start, PointF control, PointF end, Color color, float stroke)
    {
        SetCubicCurveDrawingPreview(
            start,
            Lerp(start, control, 2f / 3f),
            Lerp(end, control, 2f / 3f),
            end,
            color,
            stroke);
    }

    public void SetCubicCurveDrawingPreview(
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        Color color,
        float stroke)
    {
        DrawingPreviewVisible = true;
        DrawingPreviewStart = start;
        DrawingPreviewControl = control1;
        DrawingPreviewControl2 = control2;
        DrawingPreviewEnd = end;
        DrawingPreviewHasCurve = true;
        DrawingPreviewCurveSegments = [new CubicDrawingPreviewSegment(start, control1, control2, end)];
        DrawingPreviewShape = ShapeKind.Line;
        DrawingPreviewShapeVertexCount = 0;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        InvalidateOverlay();
    }

    public void SetCurveDrawingPreview(
        IReadOnlyList<CubicDrawingPreviewSegment> segments,
        Color color,
        float stroke)
    {
        if (segments.Count == 0)
        {
            ClearDrawingPreview();
            return;
        }

        DrawingPreviewVisible = true;
        DrawingPreviewStart = segments[0].Start;
        DrawingPreviewControl = segments[0].Control1;
        DrawingPreviewControl2 = segments[0].Control2;
        DrawingPreviewEnd = segments[^1].End;
        DrawingPreviewHasCurve = true;
        DrawingPreviewCurveSegments = segments.ToArray();
        DrawingPreviewShape = ShapeKind.Line;
        DrawingPreviewShapeVertexCount = 0;
        DrawingPreviewColor = color;
        DrawingPreviewStroke = stroke;
        InvalidateOverlay();
    }

    public void ClearDrawingPreview()
    {
        if (!DrawingPreviewVisible) return;
        DrawingPreviewVisible = false;
        DrawingPreviewHasCurve = false;
        DrawingPreviewCurveSegments = Array.Empty<CubicDrawingPreviewSegment>();
        InvalidateOverlay();
    }

    public void SetPenAnchorGuides(
        PointF point,
        bool vertical,
        bool horizontal,
        bool snapped,
        bool insertion)
    {
        point = VectorUnits.Quantize(point);
        if (PenAnchorGuidesVisible
            && PenAnchorGuidePoint == point
            && PenAnchorGuideVertical == vertical
            && PenAnchorGuideHorizontal == horizontal
            && PenAnchorGuideSnapped == snapped
            && PenAnchorGuideInsertion == insertion)
        {
            return;
        }

        PenAnchorGuidesVisible = true;
        PenAnchorGuidePoint = point;
        PenAnchorGuideVertical = vertical;
        PenAnchorGuideHorizontal = horizontal;
        PenAnchorGuideSnapped = snapped;
        PenAnchorGuideInsertion = insertion;
        InvalidateOverlay();
    }

    public void ClearPenAnchorGuides()
    {
        if (!PenAnchorGuidesVisible) return;
        PenAnchorGuidesVisible = false;
        PenAnchorGuideVertical = false;
        PenAnchorGuideHorizontal = false;
        PenAnchorGuideSnapped = false;
        PenAnchorGuideInsertion = false;
        InvalidateOverlay();
    }

    public void SetPenDirectionHandles(PointF anchor, PointF? incoming, PointF? outgoing)
    {
        anchor = VectorUnits.Quantize(anchor);
        incoming = incoming is { } inPoint ? VectorUnits.Quantize(inPoint) : null;
        outgoing = outgoing is { } outPoint ? VectorUnits.Quantize(outPoint) : null;
        if (PenDirectionHandlesVisible
            && PenDirectionAnchor == anchor
            && PenDirectionIncoming == incoming
            && PenDirectionOutgoing == outgoing)
        {
            return;
        }

        PenDirectionHandlesVisible = incoming is not null || outgoing is not null;
        PenDirectionAnchor = anchor;
        PenDirectionIncoming = incoming;
        PenDirectionOutgoing = outgoing;
        InvalidateOverlay();
    }

    public void ClearPenDirectionHandles()
    {
        if (!PenDirectionHandlesVisible) return;
        PenDirectionHandlesVisible = false;
        PenDirectionIncoming = null;
        PenDirectionOutgoing = null;
        InvalidateOverlay();
    }

    public void SetFreehandPreview(
        IReadOnlyList<PointF> points,
        Color color,
        float stroke,
        IReadOnlyList<float>? diameters = null,
        BrushShape? brushShape = null)
    {
        FreehandPreviewVisible = points.Count > 0;
        FreehandPreviewPoints = points;
        FreehandPreviewDiameters = diameters is { Count: > 0 } ? diameters : Array.Empty<float>();
        FreehandPreviewColor = color;
        FreehandPreviewStroke = Math.Max(VectorUnits.MinimumStrokeUnits, stroke);
        FreehandPreviewBrushShape = brushShape;
        InvalidateOverlay();
    }

    public void ClearFreehandPreview()
    {
        if (!FreehandPreviewVisible && FreehandPreviewPoints.Count == 0) return;
        FreehandPreviewVisible = false;
        FreehandPreviewPoints = Array.Empty<PointF>();
        FreehandPreviewDiameters = Array.Empty<float>();
        FreehandPreviewBrushShape = null;
        InvalidateOverlay();
    }

    public void SetBrushTipCursor(Point screen, BrushShape shape, float diameter, bool eraser)
    {
        var radius = Math.Max(2, WorldLengthToScreen(diameter) * 0.5f);
        if (BrushTipCursorVisible
            && BrushTipCursorScreen == screen
            && ReferenceEquals(BrushTipCursorShape, shape)
            && Math.Abs(BrushTipCursorDiameterWorld - diameter) < 0.001f
            && Math.Abs(BrushTipCursorRadiusPixels - radius) < 0.01f
            && BrushTipCursorIsEraser == eraser)
        {
            return;
        }

        BrushTipCursorVisible = true;
        BrushTipCursorScreen = screen;
        BrushTipCursorShape = shape;
        BrushTipCursorDiameterWorld = diameter;
        BrushTipCursorRadiusPixels = radius;
        BrushTipCursorIsEraser = eraser;
        InvalidateOverlay();
    }

    public void ClearBrushTipCursor()
    {
        if (!BrushTipCursorVisible) return;
        BrushTipCursorVisible = false;
        BrushTipCursorShape = null;
        BrushTipCursorDiameterWorld = 0;
        InvalidateOverlay();
    }

    private void RefreshBrushTipCursorScale()
    {
        if (!BrushTipCursorVisible || BrushTipCursorShape is null) return;
        BrushTipCursorRadiusPixels = Math.Max(2, WorldLengthToScreen(BrushTipCursorDiameterWorld) * 0.5f);
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
        InvalidateOverlay();
    }

    public void ClearBrushColorPalette()
    {
        if (_brushColorPaletteColors.Length == 0) return;
        _brushColorPaletteColors = [];
        _brushColorPaletteHoveredIndex = -1;
        InvalidateOverlay();
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
        InvalidateOverlay();
    }

    public void ClearFillPreview()
    {
        if (_fillPreviewContours.Length == 0) return;
        _fillPreviewContours = Array.Empty<PointF[]>();
        InvalidateOverlay();
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
        InvalidateOverlay();
    }

    public void ClearFillToolCursor()
    {
        if (!_fillToolCursorVisible) return;
        _fillToolCursorVisible = false;
        InvalidateOverlay();
    }

    public void SetFillEdgeBezierOverlay(
        int targetObject,
        IReadOnlyList<FillEdgeBezierOverlaySegment> segments,
        int activePartIndex = -1)
    {
        if ((uint)targetObject >= Scene.ObjectCount || segments.Count == 0)
        {
            ClearFillEdgeBezierOverlay();
            return;
        }

        SetFillEdgeBezierOverlayCore(targetObject, segments.ToArray(), activePartIndex);
    }

    internal void SetOwnedFillEdgeBezierOverlay(
        int targetObject,
        FillEdgeBezierOverlaySegment[] segments,
        int activePartIndex = -1)
    {
        if ((uint)targetObject >= Scene.ObjectCount || segments.Length == 0)
        {
            ClearFillEdgeBezierOverlay();
            return;
        }

        SetFillEdgeBezierOverlayCore(targetObject, segments, activePartIndex);
    }

    internal void NotifyOwnedFillEdgeBezierOverlayChanged(
        int targetObject,
        FillEdgeBezierOverlaySegment[] segments,
        int activePartIndex)
    {
        if (targetObject != _fillEdgeBezierOverlayTargetObject
            || !ReferenceEquals(segments, _fillEdgeBezierOverlaySegments))
        {
            SetFillEdgeBezierOverlayCore(targetObject, segments, activePartIndex);
            return;
        }

        _fillEdgeBezierOverlayActivePartIndex = segments.Any(segment => segment.PartIndex == activePartIndex)
            ? activePartIndex
            : -1;
        _fillEdgeBezierOverlayTranslation = PointF.Empty;
        _fillEdgeBezierOverlayRevision++;
        _direct2DRenderer.InvalidateFillEdgeBezierOverlay();
        InvalidateSelectionState();
    }

    private void SetFillEdgeBezierOverlayCore(
        int targetObject,
        FillEdgeBezierOverlaySegment[] segments,
        int activePartIndex)
    {
        var normalizedActivePartIndex = -1;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].PartIndex != activePartIndex) continue;
            normalizedActivePartIndex = activePartIndex;
            break;
        }

        var translationChanged = _fillEdgeBezierOverlayTranslation != PointF.Empty;
        _fillEdgeBezierOverlayTranslation = PointF.Empty;
        if (FillEdgeBezierOverlayMatches(targetObject, segments, normalizedActivePartIndex))
        {
            if (translationChanged) InvalidateSelectionState();
            return;
        }

        var selectionSuppressionMayChange = !FillEdgeBezierOverlayVisible
            || _fillEdgeBezierOverlayTargetObject != targetObject;
        _fillEdgeBezierOverlayTargetObject = targetObject;
        _fillEdgeBezierOverlaySegments = segments;
        _fillEdgeBezierOverlayActivePartIndex = normalizedActivePartIndex;
        _fillEdgeBezierOverlayRevision++;
        _direct2DRenderer.InvalidateFillEdgeBezierOverlay();
        if (selectionSuppressionMayChange) UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    internal void SetFillEdgeBezierOverlayTranslation(PointF translation)
    {
        if (!FillEdgeBezierOverlayVisible) return;
        translation = VectorUnits.Quantize(translation);
        if (_fillEdgeBezierOverlayTranslation == translation) return;
        _fillEdgeBezierOverlayTranslation = translation;
        InvalidateSelectionState();
    }

    internal FillEdgeBezierOverlaySegment TranslatedFillEdgeBezierOverlaySegment(
        FillEdgeBezierOverlaySegment segment)
    {
        if (_fillEdgeBezierOverlayTranslation == PointF.Empty) return segment;
        var dx = _fillEdgeBezierOverlayTranslation.X;
        var dy = _fillEdgeBezierOverlayTranslation.Y;
        return segment with
        {
            Start = new PointF(segment.Start.X + dx, segment.Start.Y + dy),
            Control1 = new PointF(segment.Control1.X + dx, segment.Control1.Y + dy),
            Control2 = new PointF(segment.Control2.X + dx, segment.Control2.Y + dy),
            End = new PointF(segment.End.X + dx, segment.End.Y + dy)
        };
    }

    private bool FillEdgeBezierOverlayMatches(
        int targetObject,
        IReadOnlyList<FillEdgeBezierOverlaySegment> segments,
        int activePartIndex)
    {
        if (_fillEdgeBezierOverlayTargetObject != targetObject
            || _fillEdgeBezierOverlayActivePartIndex != activePartIndex
            || _fillEdgeBezierOverlaySegments.Length != segments.Count)
        {
            return false;
        }

        for (var index = 0; index < segments.Count; index++)
        {
            if (_fillEdgeBezierOverlaySegments[index] != segments[index]) return false;
        }

        return true;
    }

    public void SetFillEdgeBezierPointerEditing(bool editing)
    {
        if (FillEdgeBezierPointerEditing == editing) return;
        FillEdgeBezierPointerEditing = editing;
        Invalidate();
    }

    public void ClearFillEdgeBezierOverlay() => ResetFillEdgeBezierOverlay(invalidate: true);

    private void ResetFillEdgeBezierOverlay(bool invalidate)
    {
        var wasVisible = FillEdgeBezierOverlayVisible;
        _fillEdgeBezierOverlayTargetObject = -1;
        _fillEdgeBezierOverlayActivePartIndex = -1;
        _fillEdgeBezierOverlaySegments = [];
        _fillEdgeBezierOverlayTranslation = PointF.Empty;
        if (!wasVisible) return;

        _fillEdgeBezierOverlayRevision++;
        _direct2DRenderer.InvalidateFillEdgeBezierOverlay();
        UpdateSelectionHighlightAnimation();
        if (invalidate) InvalidateSelectionState();
    }

    public FillEdgeBezierOverlayHit HitTestFillEdgeBezierOverlay(Point screen)
    {
        if (!FillEdgeBezierOverlayVisible) return FillEdgeBezierOverlayHit.None;

        var presented = PresentedFillEdgeBezierOverlaySegments();
        var bestHandle = FillEdgeBezierOverlayHit.None;
        var bestHandleDistance = float.PositiveInfinity;
        var bestHandleIsAnchor = false;
        var bestHandleIsActive = false;
        if (_fillEdgeBezierOverlayActivePartIndex >= 0)
        {
            foreach (var source in presented)
            {
                if (source.PartIndex != _fillEdgeBezierOverlayActivePartIndex) continue;
                ConsiderFillEdgeBezierSegmentHandles(
                    screen,
                    TranslatedFillEdgeBezierOverlaySegment(source),
                    true,
                    ref bestHandle,
                    ref bestHandleDistance,
                    ref bestHandleIsAnchor,
                    ref bestHandleIsActive);
            }
        }

        foreach (var source in presented)
        {
            var segment = TranslatedFillEdgeBezierOverlaySegment(source);
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex) continue;
            ConsiderFillEdgeBezierSegmentHandles(
                screen,
                segment,
                false,
                ref bestHandle,
                ref bestHandleDistance,
                ref bestHandleIsAnchor,
                ref bestHandleIsActive);
        }

        if (bestHandle.IsValid) return bestHandle;

        if (_fillEdgeBezierOverlayActivePartIndex >= 0)
        {
            foreach (var source in presented)
            {
                if (source.PartIndex != _fillEdgeBezierOverlayActivePartIndex) continue;
                var active = TranslatedFillEdgeBezierOverlaySegment(source);
                if (CubicCurveHit(screen, active, FillEdgeBezierCurveHitRadiusPixels))
                {
                    return new FillEdgeBezierOverlayHit(
                        active.PartIndex,
                        EditHandleKind.None,
                        active.SourceStartT,
                        active.SourceEndT);
                }
            }
        }

        foreach (var source in presented)
        {
            var segment = TranslatedFillEdgeBezierOverlaySegment(source);
            if (segment.PartIndex == _fillEdgeBezierOverlayActivePartIndex) continue;
            if (CubicCurveHit(screen, segment, FillEdgeBezierCurveHitRadiusPixels))
            {
                return new FillEdgeBezierOverlayHit(
                    segment.PartIndex,
                    EditHandleKind.None,
                    segment.SourceStartT,
                    segment.SourceEndT);
            }
        }

        return FillEdgeBezierOverlayHit.None;
    }

    private void ConsiderFillEdgeBezierSegmentHandles(
        Point screen,
        FillEdgeBezierOverlaySegment segment,
        bool isActive,
        ref FillEdgeBezierOverlayHit best,
        ref float bestDistance,
        ref bool bestIsAnchor,
        ref bool bestIsActive)
    {
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Control1,
            EditHandleKind.BezierControl,
            FillEdgeBezierControlHitRadiusPixels,
            false,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Control2,
            EditHandleKind.BezierControl2,
            FillEdgeBezierControlHitRadiusPixels,
            false,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.Start,
            EditHandleKind.LineStart,
            FillEdgeBezierAnchorHitRadiusPixels,
            true,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
        ConsiderFillEdgeBezierHandleCandidate(
            screen,
            segment,
            segment.End,
            EditHandleKind.LineEnd,
            FillEdgeBezierAnchorHitRadiusPixels,
            true,
            isActive,
            ref best,
            ref bestDistance,
            ref bestIsAnchor,
            ref bestIsActive);
    }

    private void ConsiderFillEdgeBezierHandleCandidate(
        Point screen,
        FillEdgeBezierOverlaySegment segment,
        PointF world,
        EditHandleKind handle,
        float radius,
        bool isAnchor,
        bool isActive,
        ref FillEdgeBezierOverlayHit best,
        ref float bestDistance,
        ref bool bestIsAnchor,
        ref bool bestIsActive)
    {
        var distance = SquaredDistance(screen, WorldToScreen(world));
        if (distance > radius * radius) return;

        const float distanceTieTolerance = 0.001f;
        var closer = distance < bestDistance - distanceTieTolerance;
        var tied = Math.Abs(distance - bestDistance) <= distanceTieTolerance;
        var preferred = tied
            && (isAnchor && !bestIsAnchor
                || isAnchor == bestIsAnchor && isActive && !bestIsActive);
        if (!closer && !preferred) return;

        best = new FillEdgeBezierOverlayHit(
            segment.PartIndex,
            handle,
            segment.SourceStartT,
            segment.SourceEndT);
        bestDistance = distance;
        bestIsAnchor = isAnchor;
        bestIsActive = isActive;
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
        InvalidateSelectionState();
    }

    public void ClearGradientOverlay()
    {
        if (!_gradientOverlayVisible) return;
        _gradientOverlayVisible = false;
        InvalidateSelectionState();
    }

    public GradientOverlayHit HitTestGradientOverlay(Point screen)
    {
        if (!_gradientOverlayVisible) return GradientOverlayHit.None;
        var start = WorldToScreen(_gradientOverlayStart);
        var end = WorldToScreen(_gradientOverlayEnd);
        const float radiusSquared = 10 * 10;
        var startDistance = SquaredDistance(screen, start);
        var endDistance = SquaredDistance(screen, end);
        if (startDistance <= radiusSquared
            || _gradientOverlayKind != GradientKind.ShapeRadial && endDistance <= radiusSquared)
        {
            if (_gradientOverlayKind == GradientKind.ShapeRadial) return new GradientOverlayHit(GradientHandleKind.Start);
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
        InvalidateOverlay();
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

        InvalidateOverlay();
    }

    private void ClearFillAnimation()
    {
        _fillAnimationTimer.Stop();
        _fillAnimationContours = Array.Empty<PointF[]>();
        _fillAnimationMaxRadiusWorld = 0f;
        _fillAnimationProgress = 1f;
        InvalidateOverlay();
    }

    private const double SelectionSweepDurationMilliseconds = 200;

    public void StartFillSelectionSweep(DrawingElementHit fillHit)
    {
        if (_disposingResources || Scene is null) return;
        if (!fillHit.IsValid || fillHit.Key.Kind != DrawingElementKind.Fill) return;
        var contours = GetSelectedFillPartContours(fillHit)
            .Where(contour => contour.Length >= 3)
            .Select(contour => contour.ToArray())
            .ToArray();
        if (contours.Length == 0) return;

        _selectionSweepContours = contours;
        _selectionSweepProgress = 0f;
        _selectionSweepStartedAt = Stopwatch.GetTimestamp();
        if (!_selectionSweepTimer.Enabled) _selectionSweepTimer.Start();
        InvalidateOverlay();
    }

    private void TickSelectionSweep()
    {
        if (_selectionSweepContours.Length == 0)
        {
            _selectionSweepTimer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_selectionSweepStartedAt).TotalMilliseconds;
        _selectionSweepProgress = (float)Math.Clamp(elapsed / SelectionSweepDurationMilliseconds, 0d, 1d);
        if (_selectionSweepProgress >= 1f)
        {
            ClearSelectionSweep();
            return;
        }

        InvalidateOverlay();
    }

    private void ClearSelectionSweep()
    {
        _selectionSweepTimer.Stop();
        _selectionSweepContours = Array.Empty<PointF[]>();
        _selectionSweepProgress = 1f;
        InvalidateOverlay();
    }

    private void UpdateSelectionHighlightAnimation()
    {
        if (_disposingResources || Scene is null) return;
        var hasSelection = _selectedObjects.Any(index =>
            (uint)index < Scene.ObjectCount && !SuppressFillEdgeBezierSelectionOutline(index));
        if (hasSelection
            && !HasSoftwareDistortionForCurrentFrame()
            && ShouldAnimateSelectionHighlight(Scene.ObjectCount, Scene.VirtualAtomCount)
            && Visible
            && IsHandleCreated)
        {
            if (_selectionHighlightTimer.Enabled) return;
            _selectionHighlightPhase = 0f;
            _selectionHighlightStartedAt = Stopwatch.GetTimestamp();
            _selectionHighlightTimer.Start();
            return;
        }

        _selectionHighlightTimer.Stop();
        _selectionHighlightPhase = 0f;
    }

    private void TickSelectionHighlight()
    {
        if (!Visible
            || !IsHandleCreated
            || HasSoftwareDistortionForCurrentFrame()
            || !ShouldAnimateSelectionHighlight(Scene.ObjectCount, Scene.VirtualAtomCount)
            || !_selectedObjects.Any(index =>
                (uint)index < Scene.ObjectCount && !SuppressFillEdgeBezierSelectionOutline(index)))
        {
            UpdateSelectionHighlightAnimation();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_selectionHighlightStartedAt).TotalMilliseconds;
        _selectionHighlightPhase = (float)(elapsed % SelectionHighlightCycleMilliseconds / SelectionHighlightCycleMilliseconds);
        if (!Capture) InvalidateOverlay();
    }

    internal static bool ShouldAnimateSelectionHighlight(int objectCount, long atomCount)
    {
        return objectCount <= AnimatedSelectionObjectBudget
            && atomCount <= AnimatedSelectionAtomBudget;
    }

    internal bool SuppressFillEdgeBezierSelectionOutline(int objectIndex)
    {
        if (SelectionDragPreviewActive
            || !FillEdgeBezierOverlayVisible
            || objectIndex != _fillEdgeBezierOverlayTargetObject
            || _selectedObjects.Length != 1
            || _selectedObjects[0] != objectIndex
            || (uint)objectIndex >= Scene.ObjectCount)
        {
            return false;
        }

        if (_selectedElements.Length > 0)
        {
            return _selectedElements.All(hit =>
                hit.Key.ObjectIndex == objectIndex
                && hit.Key.Kind is DrawingElementKind.Fill or DrawingElementKind.BoundaryStroke);
        }

        var shape = Scene.ShapeKind[objectIndex];
        return SelectionHighlightForShape(shape) == SelectionHighlightKind.Fill;
    }

    public void SetSelection(IEnumerable<int> objectIndices, int primaryObject = -1)
    {
        var selected = objectIndices
            .Where(index => index >= 0 && index < Scene.ObjectCount)
            .Distinct()
            .ToArray();

        if (primaryObject < 0 && selected.Length > 0) primaryObject = selected[^1];
        if (primaryObject >= 0 && !selected.Contains(primaryObject)) primaryObject = selected.Length > 0 ? selected[^1] : -1;

        ClearSelectionDragPreviewCore(invalidate: false);
        ClearSelectionFillDragFront();
        _selectedObjects = selected;
        _selectedObject = primaryObject;
        SelectedElement = DrawingElementHit.None;
        _selectedElements = Array.Empty<DrawingElementHit>();
        InvalidateSelectedFillCache();
        UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    public bool SetSelectionState(
        IEnumerable<int> objectIndices,
        int primaryObject,
        IEnumerable<DrawingElementHit> hits,
        DrawingElementHit primaryElement = default)
    {
        var selected = objectIndices
            .Where(index => index >= 0 && index < Scene.ObjectCount)
            .Distinct()
            .ToArray();
        if (primaryObject < 0 && selected.Length > 0) primaryObject = selected[^1];
        if (primaryObject >= 0 && !selected.Contains(primaryObject))
        {
            primaryObject = selected.Length > 0 ? selected[^1] : -1;
        }

        var selectedOwners = selected.ToHashSet();
        var elements = hits
            .Where(hit => hit.IsValid
                && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                && selectedOwners.Contains(hit.Key.ObjectIndex))
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
        var element = primaryElement.IsValid && elements.Any(hit => hit.Key == primaryElement.Key)
            ? elements.First(hit => hit.Key == primaryElement.Key)
            : elements.Length > 0 ? elements[^1] : DrawingElementHit.None;

        if (_selectedObject == primaryObject
            && SelectedElement == element
            && _selectedObjects.SequenceEqual(selected)
            && _selectedElements.SequenceEqual(elements))
        {
            return false;
        }

        ClearSelectionDragPreviewCore(invalidate: false);
        ClearSelectionFillDragFront();
        _selectedObjects = selected;
        _selectedObject = primaryObject;
        _selectedElements = elements;
        SelectedElement = element;
        InvalidateSelectedFillCache();
        UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
        return true;
    }

    public void SetSelectedElement(DrawingElementHit hit)
    {
        SetSelectedElements(hit.IsValid ? new[] { hit } : Array.Empty<DrawingElementHit>(), hit);
    }

    public void SetSelectedElements(IEnumerable<DrawingElementHit> hits, DrawingElementHit primary = default)
    {
        var selectedOwners = _selectedObjects.ToHashSet();
        var elements = hits
            .Where(hit => hit.IsValid
                && (uint)hit.Key.ObjectIndex < Scene.ObjectCount
                && selectedOwners.Contains(hit.Key.ObjectIndex))
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
        var element = primary.IsValid && elements.Any(hit => hit.Key == primary.Key)
            ? elements.First(hit => hit.Key == primary.Key)
            : elements.Length > 0 ? elements[^1] : DrawingElementHit.None;
        if (SelectedElement == element && _selectedElements.SequenceEqual(elements)) return;

        ClearSelectionDragPreviewCore(invalidate: false);
        ClearSelectionFillDragFront();
        _selectedElements = elements;
        SelectedElement = element;
        InvalidateSelectedFillCache();
        UpdateSelectionHighlightAnimation();
        InvalidateSelectionState();
    }

    public void SetPenPathHandlesVisible(bool visible)
    {
        if (PenPathHandlesVisible == visible) return;
        PenPathHandlesVisible = visible;
        InvalidateSelectionState();
    }

    public void SetHoveredLineElement(DrawingElementHit hit)
    {
        if (!IsValidEditableBezierHit(hit)) hit = DrawingElementHit.None;
        if (_hoveredLineElement == hit) return;
        _hoveredLineElement = hit;
        InvalidateOverlay();
    }

    public void ClearHoveredLineElement() => SetHoveredLineElement(DrawingElementHit.None);

    internal IReadOnlyList<int> GetLodDetailObjectIndices()
    {
        List<int>? detail = null;
        HashSet<int>? seen = null;

        void Add(int objectIndex)
        {
            if ((uint)objectIndex >= Scene.ObjectCount || detail?.Count >= MaxSelectionOutlines) return;
            seen ??= new HashSet<int>();
            if (!seen.Add(objectIndex)) return;
            (detail ??= new List<int>(4)).Add(objectIndex);
        }

        foreach (var objectIndex in SelectedObjects) Add(objectIndex);
        foreach (var hit in SelectedElements) Add(hit.Key.ObjectIndex);
        Add(SelectedObject);
        Add(HoveredLineElement.Key.ObjectIndex);
        return detail is { Count: > 0 } ? detail : Array.Empty<int>();
    }

    public EditHandleKind HitTestHoveredLineHandle(Point screen)
    {
        return IsValidEditableBezierHit(_hoveredLineElement)
            ? HitTestLineBezierHandle(screen, _hoveredLineElement)
            : EditHandleKind.None;
    }

    public EditHandleKind HitTestLineElementHandle(Point screen, DrawingElementHit hit)
    {
        return IsValidEditableBezierHit(hit)
            ? HitTestLineBezierHandle(screen, hit)
            : EditHandleKind.None;
    }

    internal bool IsValidEditableBezierHit(DrawingElementHit hit)
    {
        return TryGetEditableBezierWorldPoints(hit, out _, out _, out _, out _);
    }

    internal bool ShouldDrawHoveredLineControls()
    {
        var hit = _hoveredLineElement;
        if (!IsValidEditableBezierHit(hit)) return false;

        if (Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line
            && hit.PresentedBezierSegmentIndex >= 0)
        {
            return !SelectedElement.IsValid
                || SelectedElement.Key != hit.Key
                || SelectedElement.PresentedBezierSegmentIndex != hit.PresentedBezierSegmentIndex;
        }

        if (SelectedElement.IsValid && SelectedElement.Key == hit.Key)
        {
            var shape = Scene.ShapeKind[hit.Key.ObjectIndex];
            if (shape != ShapeKind.Freeform
                || SelectedElement.BezierSegmentIndex >= 0
                    && SelectedElement.BezierSegmentIndex == hit.BezierSegmentIndex)
            {
                return false;
            }
        }

        return SelectedElement.IsValid
            || SelectedObject != hit.Key.ObjectIndex
            || Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Freeform;
    }

    internal bool TryResolveEditableBezierHit(
        Point screen,
        DrawingElementHit hit,
        out DrawingElementHit resolved)
    {
        resolved = DrawingElementHit.None;
        if (!hit.IsValid
            || (uint)hit.Key.ObjectIndex >= Scene.ObjectCount
            || !Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
        {
            return false;
        }

        var shape = Scene.ShapeKind[hit.Key.ObjectIndex];
        if (hit.Key.Kind == DrawingElementKind.BoundaryStroke
            || hit.Key.Kind == DrawingElementKind.Stroke && shape == ShapeKind.Line)
        {
            if (!TryGetPresentedEditableBezierSegments(hit, out var presented)) return false;
            var presentedIndex = ClosestPresentedBezierSegment(screen, presented);
            var sourceRange = presented[presentedIndex];
            resolved = hit with
            {
                PresentedBezierSegmentIndex = presentedIndex,
                PresentedSourceStartT = sourceRange.SourceStartT,
                PresentedSourceEndT = sourceRange.SourceEndT
            };
            return true;
        }

        if (hit.Key.Kind != DrawingElementKind.Stroke || shape != ShapeKind.Freeform) return false;
        var maximumDistance = Math.Max(Scene.Stroke[hit.Key.ObjectIndex] * 0.5f, 1)
            + Math.Max(4, ScreenLengthToWorld(10));
        if (!Scene.TryFindClosestFreehandBezierSegment(
                hit.Key.ObjectIndex,
                ScreenToWorld(screen),
                maximumDistance,
                out var segmentIndex,
                out _))
        {
            return false;
        }

        resolved = hit with { BezierSegmentIndex = segmentIndex };
        return true;
    }

    internal static DrawingElementHit ResolvePresentedBezierSourceHit(DrawingElementHit hit)
    {
        var sourceStart = Math.Clamp(hit.PresentedSourceStartT, 0, 1);
        var sourceEnd = Math.Clamp(hit.PresentedSourceEndT, sourceStart, 1);
        if (sourceStart <= DrawingTopologyRules.UnitIntersectionTolerance
            && sourceEnd >= 1f - DrawingTopologyRules.UnitIntersectionTolerance)
        {
            return hit;
        }

        var range = hit.EndT - hit.StartT;
        return hit with
        {
            StartT = hit.StartT + range * sourceStart,
            EndT = hit.StartT + range * sourceEnd,
            PresentedBezierSegmentIndex = -1,
            PresentedSourceStartT = 0,
            PresentedSourceEndT = 1
        };
    }

    private int ClosestPresentedBezierSegment(
        Point screen,
        IReadOnlyList<DistortedBezierSegment> segments)
    {
        var bestIndex = 0;
        var bestDistance = float.PositiveInfinity;
        for (var index = 0; index < segments.Count; index++)
        {
            var distance = SquaredDistanceToCubicScreen(screen, segments[index].Curve);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = index;
        }
        return bestIndex;
    }

    private float SquaredDistanceToCubicScreen(Point screen, CubicBoundarySegment curve)
    {
        var start = WorldToScreen(curve.Start);
        var control1 = WorldToScreen(curve.Control1);
        var control2 = WorldToScreen(curve.Control2);
        var end = WorldToScreen(curve.End);
        var controlLength = Distance(start, control1) + Distance(control1, control2) + Distance(control2, end);
        var steps = Math.Clamp((int)MathF.Ceiling(controlLength / 8f), 8, 64);
        var point = new PointF(screen.X, screen.Y);
        var previous = start;
        var best = float.PositiveInfinity;
        for (var step = 1; step <= steps; step++)
        {
            var current = CubicPoint(start, control1, control2, end, step / (float)steps);
            best = Math.Min(best, SquaredDistanceToSegment(point, previous, current));
            previous = current;
        }
        return best;
    }

    private EditHandleKind HitTestLineBezierHandle(Point screen, DrawingElementHit hit)
    {
        if (!TryGetEditableBezierWorldPoints(hit, out var start, out var control1, out var control2, out var end))
        {
            return EditHandleKind.None;
        }

        if (Distance(screen, WorldToScreen(start)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineStart;
        if (Distance(screen, WorldToScreen(end)) <= EndpointHandleHitRadiusPixels) return EditHandleKind.LineEnd;
        var controlReach = Math.Max(
            Distance(WorldToScreen(start), WorldToScreen(control1)),
            Distance(WorldToScreen(end), WorldToScreen(control2)));
        var controlHitRadius = Math.Clamp(
            ControlHandleHitRadiusPixels + MathF.Sqrt(controlReach) * 0.35f,
            ControlHandleHitRadiusPixels,
            MaxControlHandleHitRadiusPixels);
        if (Distance(screen, WorldToScreen(control1)) <= controlHitRadius) return EditHandleKind.BezierControl;
        return Distance(screen, WorldToScreen(control2)) <= controlHitRadius
            ? EditHandleKind.BezierControl2
            : EditHandleKind.None;
    }

    internal bool TryGetEditableBezierWorldPoints(
        DrawingElementHit hit,
        out PointF start,
        out PointF control1,
        out PointF control2,
        out PointF end)
    {
        if (!TryGetPresentedEditableBezierSegments(hit, out var presented))
        {
            start = PointF.Empty;
            control1 = PointF.Empty;
            control2 = PointF.Empty;
            end = PointF.Empty;
            return false;
        }

        var index = hit.PresentedBezierSegmentIndex;
        var segment = (uint)index < presented.Length
            ? presented[index].Curve
            : presented.Length == 1
                ? presented[0].Curve
                : new CubicBoundarySegment(
                    presented[0].Curve.Start,
                    presented[0].Curve.Control1,
                    presented[^1].Curve.Control2,
                    presented[^1].Curve.End);
        start = segment.Start;
        control1 = segment.Control1;
        control2 = segment.Control2;
        end = segment.End;
        return true;
    }

    internal CubicBoundarySegment[] GetPresentedEditableBezierWorldSegments(DrawingElementHit hit)
    {
        return TryGetPresentedEditableBezierSegments(hit, out var presented)
            ? presented.Select(segment => segment.Curve).ToArray()
            : [];
    }

    private bool TryGetPresentedEditableBezierSegments(
        DrawingElementHit hit,
        out DistortedBezierSegment[] presented)
    {
        presented = [];
        var start = PointF.Empty;
        var control1 = PointF.Empty;
        var control2 = PointF.Empty;
        var end = PointF.Empty;
        if (!hit.IsValid
            || (uint)hit.Key.ObjectIndex >= Scene.ObjectCount
            || !Scene.IsObjectActive(hit.Key.ObjectIndex, Frame))
        {
            return false;
        }

        if (hit.Key.Kind == DrawingElementKind.Stroke
            && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Line)
        {
            if (!TryGetLineBezierWorldPoints(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out start,
                out control1,
                out control2,
                out end))
            {
                return false;
            }
            if (hit.StartT <= DrawingTopologyRules.UnitIntersectionTolerance
                && hit.EndT >= 1f - DrawingTopologyRules.UnitIntersectionTolerance
                && TryGetObjectDistortionsForRendering(
                    Scene,
                    hit.Key.ObjectIndex,
                    out var distortions)
                && TryGetDistortedVectorGeometryForRendering(
                    Scene,
                    hit.Key.ObjectIndex,
                    distortions,
                    out var geometry)
                && geometry.HasOpenStroke)
            {
                presented = geometry.OpenStrokeProvenance;
                return true;
            }
            presented = GetPresentedBezierSegments(
                Scene,
                hit.Key.ObjectIndex,
                new CubicBoundarySegment(start, control1, control2, end));
            return presented.Length > 0;
        }

        if (hit.Key.Kind == DrawingElementKind.Stroke
            && Scene.ShapeKind[hit.Key.ObjectIndex] == ShapeKind.Freeform
            && hit.BezierSegmentIndex >= 0
            && Scene.TryGetFreehandBezierSegment(
                hit.Key.ObjectIndex,
                hit.BezierSegmentIndex,
                out var segment))
        {
            presented = [new DistortedBezierSegment(segment.Curve, 0, 0, 1)];
            return true;
        }

        if (hit.Key.Kind != DrawingElementKind.BoundaryStroke
            || !Scene.TryGetBoundaryBezierSegment(hit, Frame, out var boundary))
        {
            return false;
        }

        presented = GetPresentedBezierSegments(Scene, hit.Key.ObjectIndex, boundary);
        return presented.Length > 0;
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
        if (MarqueeVisible && MarqueeStart == start && MarqueeEnd == end) return;
        var hadLodPreview = MarqueeLodPreviewActive;
        MarqueeVisible = true;
        MarqueeStart = start;
        MarqueeEnd = end;
        MarqueeOverlayActive = _marqueeOverlay.TryShow(start, end);
        if (MarqueeOverlayActive)
        {
            MarqueeLodPreviewActive = false;
            if (hadLodPreview) Invalidate();
            return;
        }

        MarqueeLodPreviewActive = ShouldUseMarqueeLodPreview(
            _lastFrameRenderMilliseconds,
            Scene.ObjectCount,
            UnderlayScene?.ObjectCount ?? 0,
            OnionSkinScene?.ObjectCount ?? 0,
            DragPreviewScene?.ObjectCount ?? 0);
        if (MarqueeLodPreviewActive != hadLodPreview) Invalidate();
        else InvalidateOverlay();
    }

    public void ClearMarquee()
    {
        if (!MarqueeVisible) return;
        var usedOverlay = MarqueeOverlayActive;
        var usedLodPreview = MarqueeLodPreviewActive;
        var pendingSceneInvalidation = _marqueeSceneInvalidationPending;
        _marqueeOverlay.Hide();
        MarqueeVisible = false;
        MarqueeOverlayActive = false;
        MarqueeLodPreviewActive = false;
        _marqueeSceneInvalidationPending = false;
        if (usedLodPreview) Invalidate();
        else if (!usedOverlay || pendingSceneInvalidation) InvalidateOverlay();
    }

    private void UpdateMarqueeOverlay()
    {
        MarqueeOverlayActive = _marqueeOverlay.TryShow(MarqueeStart, MarqueeEnd);
    }

    private void InvalidateSelectionState()
    {
        if (MarqueeVisible && MarqueeOverlayActive)
        {
            _marqueeSceneInvalidationPending = true;
            return;
        }
        InvalidateOverlay();
    }

    private void BeginZoomLodPreview()
    {
        _zoomLodPreviewActive = true;
        _zoomLodPreviewTimer.Stop();
        _zoomLodPreviewTimer.Start();
    }

    private void EndZoomLodPreview(bool invalidate)
    {
        _zoomLodPreviewTimer.Stop();
        if (!_zoomLodPreviewActive) return;
        _zoomLodPreviewActive = false;
        if (invalidate) Invalidate();
    }

    internal static bool ShouldUseZoomLodPreview(int objectCount, bool hasDisplayLayerEffects)
    {
        return objectCount >= ZoomLodPreviewObjectThreshold && !hasDisplayLayerEffects;
    }

    internal static bool ShouldUseMarqueeLodPreview(
        double lastFrameMilliseconds,
        int editableObjectCount,
        int underlayObjectCount,
        int onionSkinObjectCount,
        int dragPreviewObjectCount)
    {
        var totalObjects = (long)Math.Max(0, editableObjectCount)
            + Math.Max(0, underlayObjectCount)
            + Math.Max(0, onionSkinObjectCount)
            + Math.Max(0, dragPreviewObjectCount);
        return lastFrameMilliseconds > MarqueePreviewFrameBudgetMilliseconds
            || totalObjects >= MarqueePreviewObjectThreshold;
    }
}
