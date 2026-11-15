using System.Numerics;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal int[] BreakApartBitmapObjects(IEnumerable<int> indices)
    {
        var targets = indices.Distinct().ToArray();
        if (targets.Length == 0 || targets.Any(index => !TryGetBitmapObjectData(index, out var data)
                || data.IsBrokenApart || !IsObjectSelectable(index, EditFrame))) return [];
        foreach (var index in targets)
        {
            var data = _bitmapObjects[index];
            _bitmapObjects[index] = BitmapFillData(data, data.VisibleContours, broken: true);
        }
        InvalidateDeferredTopologyQueries();
        GeometryRevision++;
        return targets;
    }

    private static BitmapObjectData BitmapFillData(BitmapObjectData data, PointF[][]? contours, bool broken) => new()
    {
        ImageAssetId = data.ImageAssetId,
        PlacedSize = data.PlacedSize,
        VisibleContours = contours,
        IsBrokenApart = broken
    };

    private sealed record BitmapSelectionAddition(VectorScene Source, double SubOrder, bool Selected);
    private sealed record BitmapTopologyAddition(DrawingElementKey SourceKey, BitmapSelectionAddition Part);

    private bool TryGetBitmapTopologyInverse(int source, out Matrix3x2 inverse)
    {
        var forward = Matrix3x2.Identity;
        inverse = Matrix3x2.Identity;
        if (_objectDistortions.TryGetValue(source, out var warps))
            foreach (var warp in warps)
            {
                if (!warp.TryGetAffineTransform(out var affine)) return false;
                forward *= affine;
            }
        return Matrix3x2.Invert(forward, out inverse);
    }

    // Part contours remain in source world space, just like the stored image clip.
    // Only cutters cross the inverse warp; rendering applies the image warp once.
    private List<FillRegion> BuildBitmapTopologyRegions(int source, IReadOnlyList<int> candidates)
    {
        var subject = ToClipperPaths(FillWorldContours(source));
        var regions = ExecuteFillRegions(subject, new Paths64());
        if (!TryGetBitmapTopologyInverse(source, out var inverse)) return regions;
        var strokes = new List<TopologyCutterPath>();
        foreach (var candidate in candidates)
        {
            if (candidate == source || ObjectLayer[candidate] != ObjectLayer[source]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[source]
                || ObjectLocked[candidate] || IsLayerEffectivelyLocked(ObjectLayer[candidate])) continue;
            var shape = ShapeKind[candidate];
            var editableBitmap = TryGetBitmapObjectData(candidate, out var bitmap) && bitmap.IsBrokenApart;
            if (IsWholeObjectShape(shape) && !editableBitmap) continue;
            var worldContours = IsTopologyStrokeShape(shape)
                ? new[] { StrokeSamples(candidate).Select(sample => MapObjectPoint(candidate, sample.Point)).ToArray() }
                : GetDistortedObjectBoundaryContours(candidate);
            var contours = worldContours.Select(contour => contour.Select(point =>
            {
                var mapped = Vector2.Transform(new Vector2(point.X, point.Y), inverse);
                return new PointF(mapped.X, mapped.Y);
            }).ToArray()).ToArray();
            if (HasFill(candidate))
            {
                var cutter = ToClipperPaths(contours);
                if (cutter.Count == 0) continue;
                var partitioned = new List<FillRegion>();
                foreach (var region in regions)
                {
                    var paths = ToClipperPaths(region.Contours);
                    partitioned.AddRange(ExecuteFillRegions(Clipper.Intersect(paths, cutter, FillRule.EvenOdd), new Paths64()));
                    partitioned.AddRange(ExecuteFillRegions(Clipper.Difference(paths, cutter, FillRule.EvenOdd), new Paths64()));
                }
                regions = partitioned;
            }
            else if (HasStroke(candidate))
            {
                foreach (var contour in contours)
                {
                    var closed = !IsTopologyStrokeShape(shape)
                        || contour.Length > 2 && SameDrawingUnit(contour[0], contour[^1]);
                    var path = ToClipperPath(contour, closed);
                    if (path.Count >= 2) strokes.Add(new TopologyCutterPath(path, closed));
                }
            }
        }
        if (strokes.Count > 0)
        {
            var areas = BuildTopologyCutterAreas(MergeConnectedOpenCutterPaths(strokes));
            regions = regions.SelectMany(region => ExecuteFillRegions(ToClipperPaths(region.Contours), areas)).ToList();
        }
        regions.Sort(CompareFillRegions);
        return regions;
    }

    private BitmapSelectionAddition CreateBitmapSelectionPart(int source, PointF[][] contours,
        double subOrder, bool selected)
    {
        var normalized = contours.Select(contour => contour.Select(point =>
        {
            var local = WorldToLocal(source, point);
            return new PointF(local.X / Width[source] + 0.5f, local.Y / Height[source] + 0.5f);
        }).ToArray()).ToArray();
        var copy = new VectorScene();
        copy.CreateEmpty();
        copy.EnsureObjectCapacity(1);
        copy.ObjectCount = 1;
        copy.CopyObjectDataFrom(this, source, 0);
        copy._bitmapObjects[0] = BitmapFillData(_bitmapObjects[source], normalized, broken: true);
        return new BitmapSelectionAddition(copy, subOrder, selected);
    }

    private bool PlanBitmapTopologyParts(int source, DrawingElementKey[] keys, int frame,
        List<BitmapTopologyAddition> additions, Dictionary<DrawingElementKey, DrawingElementKey> mapped, bool[] remove)
    {
        if (!IsObjectSelectable(source, frame)) return false;
        var regions = BuildFillRegions(source, frame, CollectTopologyCandidates(source, frame));
        if (keys.Any(key => key.Kind != DrawingElementKind.Fill || key.PartIndex < 0 || key.PartIndex >= regions.Count))
            return false;
        if (regions.Count <= 1)
        {
            foreach (var key in keys) mapped[key] = key;
            return true;
        }
        var selected = keys.ToDictionary(key => key.PartIndex);
        var subOrders = ReplacementSubOrders(source, regions.Count);
        var atoms = Math.Max(3u, AtomCount[source] / (uint)regions.Count);
        for (var part = 0; part < regions.Count; part++)
        {
            var key = selected.GetValueOrDefault(part, DrawingElementKey.None);
            var addition = CreateBitmapSelectionPart(source, regions[part].Contours, subOrders[part], key.IsValid);
            addition.Source.AtomCount[0] = atoms;
            additions.Add(new BitmapTopologyAddition(key, addition));
        }
        remove[source] = true;
        return true;
    }

    // Clip in undeformed world space so texture coordinates are preserved. An
    // affine inverse maps straight selection edges exactly, including mirrors.
    // Curved/folded warps keep whole-object selection until a precise curved
    // clipping representation is available.
    private void PlanBitmapSelectionParts(int source, RectangleF bounds, PointF[]? polygon,
        List<BitmapSelectionAddition> additions, List<int> whole, bool[] remove)
    {
        if (!TryGetBitmapTopologyInverse(source, out var inverse))
        {
            if (polygon is null ? IsObjectGeometryInsideBounds(source, bounds)
                : IsObjectGeometryInsidePolygonCore(source, polygon)) whole.Add(source);
            return;
        }
        polygon ??= [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        var cutter = ToClipperPaths([polygon.Select(point =>
        {
            var mapped = Vector2.Transform(new Vector2(point.X, point.Y), inverse);
            return new PointF(mapped.X, mapped.Y);
        }).ToArray()], minimumAreaUnitsSquared: 0);
        var subject = ToClipperPaths(FillWorldContours(source), minimumAreaUnitsSquared: 0);
        var inside = Clipper.Intersect(subject, cutter, FillRule.EvenOdd);
        if (inside.Count == 0) return;
        var outside = Clipper.Difference(subject, cutter, FillRule.EvenOdd);
        if (outside.Count == 0) { whole.Add(source); return; }
        var subOrders = ReplacementSubOrders(source, 2);
        Add(outside, subOrders[0], selected: false);
        Add(inside, subOrders[1], selected: true);
        remove[source] = true;

        void Add(Paths64 paths, double subOrder, bool selected)
        {
            additions.Add(CreateBitmapSelectionPart(source, FromClipperPaths(paths, normalizeContours: false), subOrder, selected));
        }
    }

    private int AppendBitmapSelectionPart(BitmapSelectionAddition item)
    {
        var index = ObjectCount;
        EnsureObjectCapacity(index + 1);
        ObjectCount++;
        CopyObjectDataFrom(item.Source, 0, index);
        ObjectSubOrder[index] = item.SubOrder;
        VirtualAtomCount += AtomCount[index];
        return index;
    }
}
