using System.Numerics;
using Vortice.Direct2D1;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    // A ten-thousand object selection records thousands of outline figures. Submitting
    // them as one geometry per highlight colour keeps the replayed frame to a handful
    // of Direct2D calls, so pan, zoom, and drag stay inside the interactive budget
    // without changing how any single outline looks.
    private const int MergedSelectionOutlineThreshold = 64;
    private const int MergedSelectionFallbackBudget = 64;

    // Per-object occlusion clipping costs a topology-free but still non-trivial
    // difference against the covering fills above each selected object. Past this many
    // selected objects the merged overlay draws the exact object outlines without the
    // "hidden behind an opaque fill" refinement: every selected outline is still drawn,
    // only the refinement stops scaling the selection build into seconds.
    private const int MergedSelectionOcclusionClipLimit = 512;

    // Screen-space tolerance for dropping redundant outline vertices. A tenth of a pixel
    // is below what the stroke coverage of the highlight can express, so the outline looks
    // unchanged while the tessellation work shrinks by an order of magnitude on traced art.
    private const float MergedSelectionPointTolerancePixels = 0.35f;

    private readonly List<ID2D1PathGeometry> _mergedSelectionGeometry = [];
    private readonly Dictionary<SelectionHighlightKind, MergedSelectionBuilder> _mergedSelectionBuilders = [];
    private readonly List<(Vector2 Point, SelectionHighlightKind Kind)> _mergedSelectionDots = [];
    private readonly HashSet<int> _mergedSelectionObjects = [];
    private readonly HashSet<int> _mergedSelectionOutlinedObjects = [];
    private int _mergedSelectionFallbacks;
    private bool _mergedSelectionActive;

    private sealed class MergedSelectionBuilder
    {
        public required ID2D1PathGeometry Path { get; init; }
        public required ID2D1GeometrySink Sink { get; init; }
        public int Figures { get; set; }
    }

    private void ClearMergedSelectionGeometry()
    {
        foreach (var path in _mergedSelectionGeometry) path.Dispose();
        _mergedSelectionGeometry.Clear();
        ResetMergedSelectionBuilders();
    }

    private void ResetMergedSelectionBuilders()
    {
        foreach (var builder in _mergedSelectionBuilders.Values) builder.Sink.Dispose();
        _mergedSelectionBuilders.Clear();
        _mergedSelectionDots.Clear();
        _mergedSelectionOutlinedObjects.Clear();
        _mergedSelectionFallbacks = 0;
    }

    private bool MergedSelectionOcclusionClipping =>
        _mergedSelectionObjects.Count <= MergedSelectionOcclusionClipLimit;

    private PointF[][] ResolveMergedContours(
        StageControl stage,
        int objectIndex,
        PointF[][] contours,
        bool closed = true)
    {
        return MergedSelectionOcclusionClipping
            ? stage.Scene.GetVisiblePathSelectionPolylines(objectIndex, stage.Frame, contours, closed)
            : contours;
    }

    /// <summary>
    /// Appends one object outline to the merged geometry. Returns false when the object
    /// needs its dedicated drawing path (text areas, editable bezier lines, mixing
    /// strokes, or a selection that already exhausted the fallback budget).
    /// </summary>
    private bool TryAppendMergedObjectOutline(StageControl stage, int objectIndex, ShapeKind shape)
    {
        if (!_mergedSelectionActive) return false;
        var scene = stage.Scene;

        if (scene.TryGetObjectDistortions(objectIndex, out _))
        {
            var kind = stage.SelectionHighlightForObject(objectIndex, SelectionHighlightKind.Fill);
            foreach (var contour in scene.GetDistortedObjectBoundaryContours(objectIndex))
            {
                AppendMergedContour(stage, contour, closed: contour.Length >= 3, kind);
            }

            return true;
        }

        if (shape == ShapeKind.Path
            && scene.TryGetPathWorldContours(objectIndex, out var pathContours))
        {
            var kind = stage.SelectionHighlightForObject(objectIndex, SelectionHighlightKind.Fill);
            foreach (var contour in ResolveMergedContours(stage, objectIndex, pathContours))
            {
                if (contour.Length >= 3) AppendMergedContour(stage, contour, closed: true, kind);
            }

            return true;
        }

        if (shape == ShapeKind.Freeform || shape == ShapeKind.BrushStroke)
        {
            var world = scene.GetShapeBoundary(objectIndex);
            if (world.Length == 0) return false;
            var kind = stage.SelectionHighlightForObject(objectIndex, StageControl.SelectionHighlightForShape(shape));
            if (world.Length == 1)
            {
                AppendMergedDot(stage, world[0], kind);
                return true;
            }

            foreach (var contour in ResolveMergedContours(stage, objectIndex, [world], closed: false))
            {
                if (contour.Length >= 2) AppendMergedContour(stage, contour, closed: false, kind);
            }

            return true;
        }

        if (shape is ShapeKind.Text or ShapeKind.Line or ShapeKind.MixingStroke
            or ShapeKind.ImportedSvg or ShapeKind.Bitmap)
        {
            return false;
        }

        var bounds = scene.GetObjectWorldBounds(objectIndex);
        if (bounds.Width <= 0 || bounds.Height <= 0) return true;
        var fillKind = stage.SelectionHighlightForObject(objectIndex, SelectionHighlightKind.Fill);
        AppendMergedRectangle(stage, bounds, fillKind);
        return true;
    }

    /// <summary>
    /// Appends one selected element to the merged geometry. A selection that spans many
    /// distinct objects merges whole object outlines instead of per-part outlines:
    /// resolving the highlighted fill/stroke parts of an object costs a topology query
    /// against every overlapping neighbour, which is what made a clustered multi-thousand
    /// object highlight take seconds while the outline looks the same. Small selections
    /// keep the precise per-part outlines.
    /// </summary>
    private bool TryAppendMergedElementOutline(StageControl stage, DrawingElementHit hit)
    {
        if (!_mergedSelectionActive) return false;
        if (hit.Key.Kind is not (DrawingElementKind.Fill or DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke))
        {
            return false;
        }

        if (_mergedSelectionObjects.Count < MergedSelectionOutlineThreshold) return false;
        var objectIndex = hit.Key.ObjectIndex;
        var scene = stage.Scene;
        if ((uint)objectIndex >= scene.ObjectCount) return false;
        var shape = scene.ShapeKind[objectIndex];
        // Editable bezier lines and distorted objects keep their dedicated overlay.
        if (hit.Key.Kind == DrawingElementKind.Stroke && shape == ShapeKind.Line) return false;
        if (scene.TryGetObjectDistortions(objectIndex, out _)) return false;
        // One outline per object: a fill plus its boundary stroke is the same outline.
        if (!_mergedSelectionOutlinedObjects.Add(objectIndex)) return true;
        return TryAppendMergedObjectOutline(stage, objectIndex, shape);
    }

    private MergedSelectionBuilder MergedSelectionBuilderFor(SelectionHighlightKind kind)
    {
        if (_mergedSelectionBuilders.TryGetValue(kind, out var builder)) return builder;
        var path = _factory!.CreatePathGeometry();
        _mergedSelectionGeometry.Add(path);
        builder = new MergedSelectionBuilder { Path = path, Sink = path.Open() };
        _mergedSelectionBuilders[kind] = builder;
        return builder;
    }

    private void AppendMergedContour(StageControl stage, GdiPointF[] world, bool closed, SelectionHighlightKind kind)
    {
        if (world.Length < 2) return;
        var builder = MergedSelectionBuilderFor(kind);
        var sink = builder.Sink;
        // Direct2D tessellates and widens every segment that reaches the sink, and a traced
        // or imported artwork contributes thousands of vertices that sit within a fraction
        // of a pixel of the shape they replace. Simplifying at sub-pixel tolerance keeps
        // the outline identical at the current zoom while removing most of the GPU cost.
        var tolerance = MergedSelectionPointTolerancePixels
            / MathF.Max(0.0001f, VectorUnits.PixelsPerUnit * stage.Zoom);
        var points = SimplifyMergedContour(world, closed, tolerance);
        sink.BeginFigure(WorldToVector(stage, points[0]), FigureBegin.Hollow);
        for (var index = 1; index < points.Length; index++)
        {
            sink.AddLine(WorldToVector(stage, points[index]));
        }

        sink.EndFigure(closed && points[0] != points[^1] ? FigureEnd.Closed : FigureEnd.Open);
        builder.Figures++;
    }

    /// <summary>
    /// Ramer-Douglas-Peucker simplification of one outline. A closed outline is split at
    /// the vertex farthest from its first point so the two halves are simplified as open
    /// polylines; that keeps curvature (a circle stays a small circle) while still
    /// collapsing the redundant vertices a traced contour contributes.
    /// </summary>
    private static GdiPointF[] SimplifyMergedContour(GdiPointF[] world, bool closed, float tolerance)
    {
        if (world.Length <= 4 || tolerance <= 0f) return world;
        var keep = new bool[world.Length];
        keep[0] = true;
        keep[^1] = true;
        if (closed && world[0] == world[^1])
        {
            var split = 1;
            var farthest = -1f;
            for (var index = 1; index < world.Length - 1; index++)
            {
                var offsetX = world[index].X - world[0].X;
                var offsetY = world[index].Y - world[0].Y;
                var distance = offsetX * offsetX + offsetY * offsetY;
                if (distance <= farthest) continue;
                farthest = distance;
                split = index;
            }

            keep[split] = true;
            SimplifyMergedRange(world, 0, split, tolerance, keep);
            SimplifyMergedRange(world, split, world.Length - 1, tolerance, keep);
        }
        else
        {
            SimplifyMergedRange(world, 0, world.Length - 1, tolerance, keep);
        }

        var retained = 0;
        for (var index = 0; index < keep.Length; index++)
        {
            if (keep[index]) retained++;
        }

        if (retained < 2) return world;
        var result = new GdiPointF[retained];
        var target = 0;
        for (var index = 0; index < keep.Length; index++)
        {
            if (!keep[index]) continue;
            result[target++] = world[index];
        }

        return result;
    }

    private static void SimplifyMergedRange(GdiPointF[] world, int first, int last, float tolerance, bool[] keep)
    {
        if (last <= first + 1) return;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((first, last));
        while (pending.Count > 0)
        {
            var (start, end) = pending.Pop();
            if (end <= start + 1) continue;
            var farthest = -1f;
            var farthestIndex = -1;
            for (var index = start + 1; index < end; index++)
            {
                var distance = MergedSelectionChordDistance(world[index], world[start], world[end]);
                if (distance <= farthest) continue;
                farthest = distance;
                farthestIndex = index;
            }

            if (farthestIndex < 0 || farthest <= tolerance) continue;
            keep[farthestIndex] = true;
            pending.Push((start, farthestIndex));
            pending.Push((farthestIndex, end));
        }
    }

    private static float MergedSelectionChordDistance(GdiPointF point, GdiPointF from, GdiPointF to)
    {
        var segmentX = to.X - from.X;
        var segmentY = to.Y - from.Y;
        var lengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (lengthSquared <= 0.000001f)
        {
            var offsetX = point.X - from.X;
            var offsetY = point.Y - from.Y;
            return MathF.Sqrt(offsetX * offsetX + offsetY * offsetY);
        }

        var projection = ((point.X - from.X) * segmentX + (point.Y - from.Y) * segmentY) / lengthSquared;
        projection = Math.Clamp(projection, 0f, 1f);
        var nearestX = from.X + segmentX * projection;
        var nearestY = from.Y + segmentY * projection;
        var deltaX = point.X - nearestX;
        var deltaY = point.Y - nearestY;
        return MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private void AppendMergedBezier(StageControl stage, CubicBoundarySegment segment, SelectionHighlightKind kind)
    {
        var builder = MergedSelectionBuilderFor(kind);
        var sink = builder.Sink;
        sink.BeginFigure(WorldToVector(stage, segment.Start), FigureBegin.Hollow);
        sink.AddBezier(new BezierSegment(
            WorldToVector(stage, segment.Control1),
            WorldToVector(stage, segment.Control2),
            WorldToVector(stage, segment.End)));
        sink.EndFigure(FigureEnd.Open);
        builder.Figures++;
    }

    private void AppendMergedRectangle(StageControl stage, System.Drawing.RectangleF bounds, SelectionHighlightKind kind)
    {
        var builder = MergedSelectionBuilderFor(kind);
        var sink = builder.Sink;
        var topLeft = WorldToVector(stage, new GdiPointF(bounds.Left, bounds.Top));
        var topRight = WorldToVector(stage, new GdiPointF(bounds.Right, bounds.Top));
        var bottomRight = WorldToVector(stage, new GdiPointF(bounds.Right, bounds.Bottom));
        var bottomLeft = WorldToVector(stage, new GdiPointF(bounds.Left, bounds.Bottom));
        sink.BeginFigure(topLeft, FigureBegin.Hollow);
        sink.AddLine(topRight);
        sink.AddLine(bottomRight);
        sink.AddLine(bottomLeft);
        sink.EndFigure(FigureEnd.Closed);
        builder.Figures++;
    }

    private void AppendMergedDot(StageControl stage, GdiPointF world, SelectionHighlightKind kind)
    {
        if (_mergedSelectionDots.Count >= MaxSelectionOutlines) return;
        _mergedSelectionDots.Add((WorldToVector(stage, world), kind));
    }

    private bool TryConsumeMergedSelectionFallback()
    {
        if (_mergedSelectionFallbacks >= MergedSelectionFallbackBudget) return false;
        _mergedSelectionFallbacks++;
        return true;
    }

    private void DrawMergedSelectionGeometry()
    {
        foreach (var builder in _mergedSelectionBuilders.Values)
        {
            builder.Sink.Close();
        }

        foreach (var (kind, builder) in _mergedSelectionBuilders)
        {
            if (builder.Figures == 0) continue;
            DrawSelectionGeometry(builder.Path, primary: false, highlightKind: kind);
        }

        foreach (var (point, kind) in _mergedSelectionDots)
        {
            DrawSelectionDot(point, primary: false, kind);
        }
    }
}
