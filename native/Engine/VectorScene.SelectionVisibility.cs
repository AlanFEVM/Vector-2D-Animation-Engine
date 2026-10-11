using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    // Selection visibility is derived from current paint/layer state. It must not
    // delete source contours or cache only by GeometryRevision (opacity/lock and
    // layer presentation can change independently).
    private int[] SelectionFillOccluders(int objectIndex, int frame, RectangleF bounds, bool strokePass = false)
    {
        return QueryObjects(bounds, frame, int.MaxValue)
            .Where(candidate => candidate != objectIndex
                && IsObjectActive(candidate, frame)
                && IsOpaqueSelectionFill(candidate)
                && (ObjectLayer[candidate] < ObjectLayer[objectIndex]
                    || ObjectLayer[candidate] == ObjectLayer[objectIndex]
                    && !strokePass && CompareObjectStack(candidate, objectIndex) > 0))
            .ToArray();
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
        var paths = new Paths64();
        foreach (var contour in contours)
        {
            if (contour.Length < 2) continue;
            paths.Add(new Path64((closed ? ClosePolyline(contour) : contour)
                .Select(point => new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)))));
        }
        var occluders = SelectionFillOccluders(objectIndex, frame, GetObjectWorldBounds(objectIndex), strokePass: !HasFill(objectIndex));
        foreach (var candidate in occluders)
        {
            if (paths.Count == 0) break;
            var clipper = new Clipper64();
            clipper.AddOpenSubject(paths);
            clipper.AddClip(ToClipperPaths(SelectionOccluderContours(candidate)));
            var visible = new Paths64();
            if (!clipper.Execute(ClipType.Difference, FillRule.EvenOdd, new Paths64(), visible))
                throw new InvalidOperationException("Selection outline clipping failed.");
            paths = visible;
        }
        return paths.Select(path => path.Select(point => new PointF(
            (float)(point.X / ClipperCoordinateScale), (float)(point.Y / ClipperCoordinateScale))).ToArray()).ToArray();
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
        var paths = ToClipperPaths(SelectionOccluderContours(objectIndex));
        foreach (var candidate in SelectionFillOccluders(objectIndex, frame, GetObjectWorldBounds(objectIndex)))
        {
            paths = Clipper.Difference(paths, ToClipperPaths(SelectionOccluderContours(candidate)), FillRule.EvenOdd);
            if (paths.Count == 0) return false;
        }
        return paths.Count > 0;
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
}
