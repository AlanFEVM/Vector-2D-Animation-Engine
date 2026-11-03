namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal DrawingElementHit[] ChangeStrokeSelectionRing(IReadOnlyList<DrawingElementHit> selection, int frame, bool shrink)
    {
        var selected = selection.Where(hit => hit.IsValid && hit.Key.Kind != DrawingElementKind.Fill).DistinctBy(hit => hit.Key).ToArray();
        var selectedKeys = selected.Select(hit => hit.Key).ToHashSet();
        var all = new Dictionary<DrawingElementKey, DrawingElementHit>();
        foreach (var seed in selected)
        {
            if (all.ContainsKey(seed.Key)) continue;
            foreach (var hit in GetConnectedStrokeElements(seed, frame))
                if (IsObjectSelectable(hit.Key.ObjectIndex, frame)) all.TryAdd(hit.Key, hit);
        }
        var endpoints = new Dictionary<DrawingElementKey, (PointF Start, PointF End)>();
        var buckets = new Dictionary<(int X, int Y), List<(DrawingElementKey Key, PointF Point)>>();
        foreach (var hit in all.Values)
        {
            var points = hit.Key.Kind == DrawingElementKind.BoundaryStroke ? GetBoundaryPartPoints(hit, frame) : GetStrokePartPoints(hit, frame);
            if (points.Length < 2) continue;
            endpoints[hit.Key] = (points[0], points[^1]);
            foreach (var point in new[] { points[0], points[^1] })
            {
                var cell = ConnectedEndpointKey(point);
                if (!buckets.TryGetValue(cell, out var list)) buckets[cell] = list = [];
                list.Add((hit.Key, point));
            }
        }
        HashSet<DrawingElementKey> Neighbours(DrawingElementKey key)
        {
            var result = new HashSet<DrawingElementKey>();
            if (!endpoints.TryGetValue(key, out var ends)) return result;
            foreach (var point in new[] { ends.Start, ends.End })
            {
                var cell = ConnectedEndpointKey(point);
                for (var y = cell.Y - 2; y <= cell.Y + 2; y++)
                    for (var x = cell.X - 2; x <= cell.X + 2; x++)
                        if (buckets.TryGetValue((x, y), out var candidates))
                            foreach (var other in candidates)
                                if (other.Key != key && Distance(point, other.Point) <= ConnectedStrokeEndpointToleranceUnits)
                                    result.Add(other.Key);
            }
            return result;
        }
        var next = selectedKeys.ToHashSet();
        foreach (var key in selectedKeys)
        {
            var neighbours = Neighbours(key);
            if (shrink)
            {
                if (neighbours.Count <= 1 || neighbours.Any(other => !selectedKeys.Contains(other))) next.Remove(key);
            }
            else next.UnionWith(neighbours);
        }
        return all.Values.Where(hit => next.Contains(hit.Key)).ToArray();
    }
}
