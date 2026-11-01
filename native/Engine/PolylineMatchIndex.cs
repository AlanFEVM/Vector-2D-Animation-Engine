namespace VectorAnimationEngine;

internal sealed class PolylineMatchIndex
{
    private const int LeafSize = 8;
    private readonly MatchBounds[] _bounds;
    private readonly int[] _indices;
    private readonly List<Node> _nodes = [];

    private readonly record struct MatchBounds(float Left, float Top, float Right, float Bottom)
    {
        public bool Contains(MatchBounds other) => Left <= other.Left && Top <= other.Top
            && Right >= other.Right && Bottom >= other.Bottom;

        public static MatchBounds Union(MatchBounds first, MatchBounds second) => new(
            Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));
    }

    private readonly record struct Node(MatchBounds Bounds, int First, int Count, int Left, int Right);

    public PolylineMatchIndex(IReadOnlyList<PointF[]> polylines, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(polylines);
        if (!float.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        _bounds = polylines.Select(points => Bounds(points, tolerance)).ToArray();
        _indices = Enumerable.Range(0, _bounds.Length).ToArray();
        var horizontal = Comparer<int>.Create((first, second) => Compare(first, second, true));
        var vertical = Comparer<int>.Create((first, second) => Compare(first, second, false));
        if (_indices.Length > 0) Build(0, _indices.Length);

        int Compare(int first, int second, bool byX)
        {
            var firstBounds = _bounds[first];
            var secondBounds = _bounds[second];
            var firstCenter = byX ? firstBounds.Left * 0.5f + firstBounds.Right * 0.5f
                : firstBounds.Top * 0.5f + firstBounds.Bottom * 0.5f;
            var secondCenter = byX ? secondBounds.Left * 0.5f + secondBounds.Right * 0.5f
                : secondBounds.Top * 0.5f + secondBounds.Bottom * 0.5f;
            var comparison = firstCenter.CompareTo(secondCenter);
            return comparison != 0 ? comparison : first.CompareTo(second);
        }

        int Build(int first, int count)
        {
            var bounds = _bounds[_indices[first]];
            for (var offset = 1; offset < count; offset++) bounds = MatchBounds.Union(bounds, _bounds[_indices[first + offset]]);
            var nodeIndex = _nodes.Count;
            _nodes.Add(new Node(bounds, first, count, -1, -1));
            if (count <= LeafSize) return nodeIndex;
            Array.Sort(_indices, first, count,
                (double)bounds.Right - bounds.Left >= (double)bounds.Bottom - bounds.Top ? horizontal : vertical);
            var middle = count / 2;
            var left = Build(first, middle);
            var right = Build(first + middle, count - middle);
            _nodes[nodeIndex] = new Node(bounds, first, 0, left, right);
            return nodeIndex;
        }
    }

    public void FindCandidates(PointF start, PointF end, List<int> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (!Finite(start) || !Finite(end))
        {
            destination.AddRange(Enumerable.Range(0, _indices.Length));
            return;
        }
        if (_nodes.Count == 0) return;
        var query = new MatchBounds(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        Collect(0);
        destination.Sort();

        void Collect(int nodeIndex)
        {
            var node = _nodes[nodeIndex];
            if (!node.Bounds.Contains(query)) return;
            if (node.Count == 0)
            {
                Collect(node.Left);
                Collect(node.Right);
                return;
            }
            for (var offset = 0; offset < node.Count; offset++)
            {
                var index = _indices[node.First + offset];
                if (_bounds[index].Contains(query)) destination.Add(index);
            }
        }
    }

    private static MatchBounds Bounds(PointF[] points, float tolerance)
    {
        if (points.Length == 0 || points.Any(point => !Finite(point)))
            return new MatchBounds(float.NegativeInfinity, float.NegativeInfinity,
                float.PositiveInfinity, float.PositiveInfinity);
        var left = points[0].X;
        var right = left;
        var top = points[0].Y;
        var bottom = top;
        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }
        return new MatchBounds(MathF.BitDecrement(left - tolerance), MathF.BitDecrement(top - tolerance),
            MathF.BitIncrement(right + tolerance), MathF.BitIncrement(bottom + tolerance));
    }

    private static bool Finite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
