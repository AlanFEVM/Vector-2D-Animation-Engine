using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private readonly Dictionary<SelectionOutlineCacheKey, PointF[][]> _visibleSelectionCache = new();
    private long _visibleSelectionGeometryRevision = -1;
    private long _visibleSelectionSummaryRevision = -1;

    // Highlight geometry is requested once per selected object. Querying the spatial
    // index and materializing every occluder contour per object made the cost grow
    // with selection count times scene density, which is what stalled the Stage as
    // soon as a marquee selected more than a hundred objects. One prepared candidate
    // set per selection build keeps the work linear, and only the occluders that
    // actually overlap the object in question reach Clipper.
    private int[] _selectionOccluderCandidates = [];
    private float[] _selectionOccluderLeft = [];
    private float[] _selectionOccluderTop = [];
    private float[] _selectionOccluderRight = [];
    private float[] _selectionOccluderBottom = [];
    private long _selectionOccluderGeometryRevision = -1;
    private long _selectionOccluderSummaryRevision = -1;
    private long _selectionOccluderContentRevision = -1;
    private int _selectionOccluderFrame = -1;
    private int _selectionOccluderObjectCount = -1;
    private RectangleF _selectionOccluderBounds = RectangleF.Empty;
    private bool _selectionOccluderPrepared;
    private readonly Dictionary<int, Paths64> _selectionOccluderClipPaths = [];
    private readonly Dictionary<ulong, Paths64> _selectionOccluderUnionCache = [];
    private long _selectionOccluderClipContentRevision = -1;

    // A selection that spans thousands of objects stops paying per-object occlusion
    // clipping: the outline stays exact, only the "hidden behind an opaque fill"
    // refinement is skipped once the clip list itself becomes the dominant cost.
    private const int SelectionOccluderClipBudget = 64;
    private const int SelectionOccluderBatchMinimumObjects = 8;

    private readonly record struct SelectionOutlineCacheKey(
        int ObjectIndex, int Frame, bool Closed, int ContourCount, int PointCount, ulong Fingerprint,
        ulong OccluderFingerprint = 0);

    /// <summary>
    /// Prepares the occluder candidates shared by one selection build. Renderers call
    /// this once before drawing the selected outlines so per-object clipping reuses
    /// one spatial query instead of one query per selected object.
    /// </summary>
    internal void PrepareSelectionOutlineBatch(IReadOnlyList<int> objectIndices, int frame)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        if (objectIndices.Count < SelectionOccluderBatchMinimumObjects)
        {
            ClearSelectionOccluderCandidates();
            return;
        }

        var hasBounds = false;
        var bounds = RectangleF.Empty;
        for (var index = 0; index < objectIndices.Count; index++)
        {
            var objectIndex = objectIndices[index];
            if ((uint)objectIndex >= ObjectCount) continue;
            var objectBounds = GetObjectWorldBounds(objectIndex);
            if (objectBounds.IsEmpty) continue;
            bounds = hasBounds ? RectangleF.Union(bounds, objectBounds) : objectBounds;
            hasBounds = true;
        }

        if (!hasBounds)
        {
            ClearSelectionOccluderCandidates();
            return;
        }

        var geometryRevision = GeometryRevision;
        var summaryRevision = SummaryRevision;
        var contentRevision = ActiveContentRevision;
        if (_selectionOccluderPrepared
            && _selectionOccluderGeometryRevision == geometryRevision
            && _selectionOccluderSummaryRevision == summaryRevision
            && _selectionOccluderContentRevision == contentRevision
            && _selectionOccluderFrame == frame
            && _selectionOccluderObjectCount == ObjectCount
            && _selectionOccluderBounds == bounds)
        {
            return;
        }

        var candidates = QueryObjects(bounds, frame, int.MaxValue);
        var count = candidates.Length;
        if (_selectionOccluderLeft.Length < count)
        {
            _selectionOccluderLeft = new float[count];
            _selectionOccluderTop = new float[count];
            _selectionOccluderRight = new float[count];
            _selectionOccluderBottom = new float[count];
        }

        for (var entry = 0; entry < count; entry++)
        {
            var candidateBounds = GetObjectWorldBounds(candidates[entry]);
            _selectionOccluderLeft[entry] = candidateBounds.Left;
            _selectionOccluderTop[entry] = candidateBounds.Top;
            _selectionOccluderRight[entry] = candidateBounds.Right;
            _selectionOccluderBottom[entry] = candidateBounds.Bottom;
        }

        _selectionOccluderCandidates = candidates;
        _selectionOccluderClipPaths.Clear();
        _selectionOccluderUnionCache.Clear();
        _selectionOccluderClipContentRevision = contentRevision;
        _selectionOccluderGeometryRevision = geometryRevision;
        _selectionOccluderSummaryRevision = summaryRevision;
        _selectionOccluderContentRevision = contentRevision;
        _selectionOccluderFrame = frame;
        _selectionOccluderObjectCount = ObjectCount;
        _selectionOccluderBounds = bounds;
        _selectionOccluderPrepared = true;
    }

    internal void ClearSelectionOccluderCandidates()
    {
        if (!_selectionOccluderPrepared && _selectionOccluderCandidates.Length == 0) return;
        _selectionOccluderCandidates = [];
        _selectionOccluderClipPaths.Clear();
        _selectionOccluderUnionCache.Clear();
        _selectionOccluderClipContentRevision = -1;
        _selectionOccluderPrepared = false;
        _selectionOccluderBounds = RectangleF.Empty;
        _selectionOccluderFrame = -1;
    }

    private bool HasPreparedSelectionOccluders(int frame) =>
        _selectionOccluderPrepared
        && _selectionOccluderFrame == frame
        && _selectionOccluderGeometryRevision == GeometryRevision
        && _selectionOccluderSummaryRevision == SummaryRevision
        && _selectionOccluderContentRevision == ActiveContentRevision
        && _selectionOccluderObjectCount == ObjectCount;

    // Selection visibility is derived from current paint/layer state. It must not
    // delete source contours or cache only by GeometryRevision (opacity/lock and
    // layer presentation can change independently).
    private int[] SelectionFillOccluders(int objectIndex, int frame, RectangleF bounds, bool strokePass = false)
    {
        if (!HasPreparedSelectionOccluders(frame))
        {
            return QuerySelectionFillOccluders(objectIndex, frame, bounds, strokePass);
        }

        var objectLayer = ObjectLayer[objectIndex];
        List<int>? matched = null;
        for (var entry = 0; entry < _selectionOccluderCandidates.Length; entry++)
        {
            var candidate = _selectionOccluderCandidates[entry];
            if (candidate == objectIndex) continue;
            if (_selectionOccluderLeft[entry] > bounds.Right
                || _selectionOccluderRight[entry] < bounds.Left
                || _selectionOccluderTop[entry] > bounds.Bottom
                || _selectionOccluderBottom[entry] < bounds.Top) continue;
            var candidateLayer = ObjectLayer[candidate];
            if (candidateLayer > objectLayer) continue;
            if (candidateLayer == objectLayer
                && (strokePass || CompareObjectStack(candidate, objectIndex) <= 0)) continue;
            if (!IsObjectActive(candidate, frame) || !IsOpaqueSelectionFill(candidate)) continue;
            (matched ??= new List<int>(SelectionOccluderClipBudget)).Add(candidate);
            if (matched.Count >= SelectionOccluderClipBudget) break;
        }

        return matched is null ? Array.Empty<int>() : matched.ToArray();
    }

    private int[] QuerySelectionFillOccluders(int objectIndex, int frame, RectangleF bounds, bool strokePass)
    {
        var candidates = QueryObjects(bounds, frame, int.MaxValue);
        List<int>? matched = null;
        foreach (var candidate in candidates)
        {
            if (candidate == objectIndex
                || !IsObjectActive(candidate, frame)
                || !IsOpaqueSelectionFill(candidate)
                || ObjectLayer[candidate] > ObjectLayer[objectIndex]
                || ObjectLayer[candidate] == ObjectLayer[objectIndex]
                && (strokePass || CompareObjectStack(candidate, objectIndex) <= 0))
            {
                continue;
            }

            (matched ??= new List<int>(SelectionOccluderClipBudget)).Add(candidate);
            if (matched.Count >= SelectionOccluderClipBudget) break;
        }

        return matched is null ? Array.Empty<int>() : matched.ToArray();
    }

    private bool IsOpaqueSelectionFill(int objectIndex)
    {
        var layer = (int)ObjectLayer[objectIndex];
        if (ShapeKind[objectIndex] is VectorAnimationEngine.ShapeKind.ImportedSvg or VectorAnimationEngine.ShapeKind.Bitmap
            || !HasFill(objectIndex) || !ShouldRenderLayerContent(layer)
            || IsLayerEffectivelyOutlined(layer)
            || (HasGradient(objectIndex)
                ? GetGradientStops(objectIndex).Any(stop => (uint)stop.Argb >> 24 != 255)
                : (uint)Argb[objectIndex] >> 24 != 255)) return false;
        for (var hop = 0; hop < LayerCount && layer >= 0; hop++)
        {
            // Complex compositing cannot be assumed to erase the pixels below.
            if (LayerOpacity[layer] < 1f || !float.IsFinite(LayerOpacity[layer])
                || LayerBlendModes[layer] != LayerBlendMode.Normal
                || GetLayerSymbolFilters(layer).HasEnabled
                || TryGetMaskLayerIndex(layer, out _)) return false;
            layer = GetLayerParentIndex(layer);
        }
        return true;
    }

    private PointF[][] SelectionOccluderContours(int objectIndex) =>
        TryGetObjectDistortions(objectIndex, out _)
            ? GetDistortedObjectBoundaryContours(objectIndex)
            : FillWorldContours(objectIndex);

    internal bool IsSelectionPointExposed(int objectIndex, PointF world, int frame, bool strokePass = false)
    {
        var bounds = new RectangleF(world.X - 1, world.Y - 1, 2, 2);
        return !SelectionFillOccluders(objectIndex, frame, bounds, strokePass)
            .Any(candidate => FillContainsPoint(candidate, world));
    }

    private bool IsSelectionHitExposed(DrawingElementHit hit, PointF world, int frame)
    {
        var objectIndex = hit.Key.ObjectIndex;
        if (hit.Key.Kind == DrawingElementKind.Fill)
            return IsSelectionPointExposed(objectIndex, world, frame);
        if (!TryInverseMapObjectPoint(objectIndex, world, out var sourceWorld)) return false;
        var points = hit.Key.Kind == DrawingElementKind.Stroke
            ? StrokeSamples(objectIndex).Select(sample => sample.Point).ToArray()
            : HitBoundaryStrokeParts(objectIndex, frame, CollectTopologyCandidates(objectIndex, frame))
                .FirstOrDefault(part => part.PartIndex == hit.Key.PartIndex).Points;
        if (points is null || points.Length == 0) return false;
        var nearest = points[0];
        var bestDistance = Distance(sourceWorld, nearest);
        for (var index = 1; index < points.Length; index++)
        {
            var point = Lerp(points[index - 1], points[index],
                SegmentProjectionT(sourceWorld, points[index - 1], points[index]));
            var distance = Distance(sourceWorld, point);
            if (distance >= bestDistance) continue;
            nearest = point;
            bestDistance = distance;
        }
        return IsSelectionPointExposed(objectIndex, MapObjectPoint(objectIndex, nearest), frame, strokePass: true);
    }

    internal PointF[][] GetVisiblePathSelectionPolylines(int objectIndex, int frame, PointF[][]? contours = null, bool closed = true)
    {
        contours ??= GetDistortedObjectBoundaryContours(objectIndex);
        if (_visibleSelectionGeometryRevision != GeometryRevision
            || _visibleSelectionSummaryRevision != SummaryRevision
            || _selectionOccluderClipContentRevision != ActiveContentRevision)
        {
            _visibleSelectionCache.Clear();
            _selectionOccluderClipPaths.Clear();
            _selectionOccluderUnionCache.Clear();
            _selectionOccluderClipContentRevision = ActiveContentRevision;
            _visibleSelectionGeometryRevision = GeometryRevision;
            _visibleSelectionSummaryRevision = SummaryRevision;
        }

        var occluders = SelectionFillOccluders(objectIndex, frame, GetObjectWorldBounds(objectIndex), strokePass: !HasFill(objectIndex));
        var occluderFingerprint = 14695981039346656037UL;
        foreach (var occluder in occluders)
        {
            occluderFingerprint = unchecked((occluderFingerprint ^ (uint)occluder) * 1099511628211UL);
        }
        var cacheKey = CreateSelectionOutlineCacheKey(objectIndex, frame, closed, contours) with
        {
            OccluderFingerprint = occluderFingerprint
        };
        if (_visibleSelectionCache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = closed ? contours.Select(ClosePolyline).ToArray() : contours;
        if (occluders.Length > 0)
        {
            var paths = new Paths64();
            foreach (var contour in contours)
            {
                if (contour.Length < 2) continue;
                paths.Add(new Path64((closed ? ClosePolyline(contour) : contour)
                    .Select(point => new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)))));
            }

            // One unioned clip instead of one difference per occluder. Subtracting
            // overlapping fills one at a time let the intermediate result explode into
            // thousands of slivers, which is what made a clustered selection take
            // seconds to build.
            var clip = UnionOccluderClipPaths(occluders);
            if (paths.Count > 0 && clip.Count > 0)
            {
                var clipper = new Clipper64();
                clipper.AddOpenSubject(paths);
                clipper.AddClip(clip);
                var visible = new Paths64();
                if (!clipper.Execute(ClipType.Difference, FillRule.EvenOdd, new Paths64(), visible))
                    throw new InvalidOperationException("Selection outline clipping failed.");
                paths = visible;
            }

            result = paths.Select(path => path.Select(point => new PointF(
                (float)(point.X / ClipperCoordinateScale), (float)(point.Y / ClipperCoordinateScale))).ToArray()).ToArray();
        }

        if (_visibleSelectionCache.Count >= 2048) _visibleSelectionCache.Clear();
        _visibleSelectionCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Union of the occluder fills above one selected object, cached per occluder set so
    /// a selection whose members share the same covering fills only pays for the union
    /// once. Rings are oriented consistently first, otherwise overlapping occluders
    /// would cancel each other under the NonZero rule and punch holes in the clip.
    /// </summary>
    private Paths64 UnionOccluderClipPaths(int[] occluders)
    {
        if (occluders.Length == 1) return OccluderClipPaths(occluders[0]);
        var key = 14695981039346656037UL;
        foreach (var occluder in occluders)
        {
            key = unchecked((key ^ (uint)occluder) * 1099511628211UL);
        }

        if (_selectionOccluderUnionCache.TryGetValue(key, out var cached)) return cached;
        var clipper = new Clipper64();
        foreach (var occluder in occluders) clipper.AddSubject(OccluderClipPaths(occluder));
        var union = new Paths64();
        if (!clipper.Execute(ClipType.Union, FillRule.NonZero, union))
            throw new InvalidOperationException("Selection occluder union failed.");

        if (_selectionOccluderUnionCache.Count >= 1024) _selectionOccluderUnionCache.Clear();
        _selectionOccluderUnionCache[key] = union;
        return union;
    }

    private Paths64 OccluderClipPaths(int objectIndex)
    {
        if (_selectionOccluderClipPaths.TryGetValue(objectIndex, out var cached)) return cached;
        var paths = ToClipperPaths(SelectionOccluderContours(objectIndex));
        if (paths.Count > 1)
        {
            var dominant = 0;
            var dominantArea = 0d;
            for (var index = 0; index < paths.Count; index++)
            {
                var area = Math.Abs(Clipper.Area(paths[index]));
                if (area <= dominantArea) continue;
                dominantArea = area;
                dominant = index;
            }

            if (Clipper.Area(paths[dominant]) < 0)
            {
                for (var index = 0; index < paths.Count; index++)
                {
                    paths[index] = new Path64(Enumerable.Reverse(paths[index]));
                }
            }
        }
        else if (paths.Count == 1 && Clipper.Area(paths[0]) < 0)
        {
            paths[0] = new Path64(Enumerable.Reverse(paths[0]));
        }

        _selectionOccluderClipPaths[objectIndex] = paths;
        return paths;
    }

    /// <summary>
    /// Builds a cache key without hashing every contour point. Hashing all points of
    /// the object plus all of its occluders ran once per selected object per rebuild
    /// and dominated the highlight cost; the revision pair already invalidates the
    /// cache on any geometry or presentation change, so the fingerprint only has to
    /// separate the distinct explicit contour sets requested at the same revision.
    /// </summary>
    private static SelectionOutlineCacheKey CreateSelectionOutlineCacheKey(
        int objectIndex,
        int frame,
        bool closed,
        PointF[][] contours)
    {
        var pointCount = 0;
        var fingerprint = 14695981039346656037UL;
        foreach (var contour in contours)
        {
            pointCount += contour.Length;
            fingerprint = unchecked((fingerprint ^ (uint)contour.Length) * 1099511628211UL);
            if (contour.Length == 0) continue;
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(contour[0].X)) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(contour[0].Y)) * 1099511628211UL);
            var middle = contour[contour.Length / 2];
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(middle.X)) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(middle.Y)) * 1099511628211UL);
            var last = contour[^1];
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(last.X)) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ BitConverter.SingleToUInt32Bits(last.Y)) * 1099511628211UL);
        }

        return new SelectionOutlineCacheKey(objectIndex, frame, closed, contours.Length, pointCount, fingerprint);
    }

    private bool HasExposedPathFill(int objectIndex, int frame)
    {
        if (GetSvgStrokeStyle(objectIndex) is not null && !HasFill(objectIndex))
        {
            var stroke = StrokeSamples(objectIndex).Select(sample => MapObjectPoint(objectIndex, sample.Point)).ToArray();
            return stroke.Length == 1 ? IsSelectionPointExposed(objectIndex, stroke[0], frame, strokePass: true)
                : GetVisiblePathSelectionPolylines(objectIndex, frame, [stroke], closed: false).Length > 0;
        }
        if (ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Path || !HasFill(objectIndex) || HasStroke(objectIndex)) return true;
        var occluders = SelectionFillOccluders(objectIndex, frame, GetObjectWorldBounds(objectIndex));
        if (occluders.Length == 0) return true;
        var clip = UnionOccluderClipPaths(occluders);
        if (clip.Count == 0) return true;
        var remaining = Clipper.Difference(
            ToClipperPaths(SelectionOccluderContours(objectIndex)),
            clip,
            FillRule.EvenOdd);
        return remaining.Count > 0;
    }

    private FillBezierSegmentPiece[] ClipExposedFillBezierPieces(int objectIndex, int frame, FillBezierSegmentPiece[] pieces)
    {
        var occluders = SelectionFillOccluders(objectIndex, frame, GetObjectWorldBounds(objectIndex));
        if (occluders.Length == 0) return pieces;
        if (TryGetObjectDistortions(objectIndex, out _)) return pieces;
        var occluderContours = occluders.SelectMany(SelectionOccluderContours).Select(ClosePolyline).ToArray();
        var result = new List<FillBezierSegmentPiece>();
        foreach (var piece in pieces)
        {
            var curve = new CubicBoundarySegment(piece.Start, piece.Control1, piece.Control2, piece.End);
            var samples = SampleCubicSegmentWithParameters(curve);
            var splits = new List<DrawingTopologySplit> { new(0, curve.Start), new(1, curve.End) };
            foreach (var contour in occluderContours)
                AddCurvePolylineIntersections(splits, samples, contour, includeSourceEndpoints: false);
            var normalized = NormalizeStrokeSplits(splits);
            for (var index = 1; index < normalized.Count; index++)
            {
                var start = normalized[index - 1].T;
                var end = normalized[index].T;
                var midpoint = MapObjectPoint(objectIndex, CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, (start + end) * 0.5f));
                if (occluders.Any(candidate => FillContainsPoint(candidate, midpoint))) continue;
                var visible = CubicSubcurve(curve.Start, curve.Control1, curve.Control2, curve.End, start, end);
                var range = piece.EndT - piece.StartT;
                result.Add(piece with
                {
                    PieceIndex = result.Count,
                    StartT = piece.StartT + range * start,
                    EndT = piece.StartT + range * end,
                    Start = visible.Start, Control1 = visible.Control1,
                    Control2 = visible.Control2, End = visible.End,
                    StartIsVirtualAnchor = piece.StartIsVirtualAnchor || start > 0.0001f,
                    EndIsVirtualAnchor = piece.EndIsVirtualAnchor || end < 0.9999f
                });
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// True when the point samples the object's <em>deformed</em> geometry instead of its
    /// source geometry. Fill/paint always lands in the deformed area (the renderer warps the
    /// pixels), so every sample that decides "may this world point touch this object" has to
    /// be warped too. Evaluated per object because this is the hit-test hot path and most
    /// scenes hold no distort envelopes at all.
    /// </summary>
    private bool UsesDistortedHitGeometry(int objectIndex) =>
        _objectDistortions.ContainsKey(objectIndex)
        && ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
        && !IsTopologyStrokeShape(ShapeKind[objectIndex]);

}
