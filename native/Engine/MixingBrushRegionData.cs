using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

public readonly record struct MixingBrushRegionVertex(PointF Point, int Argb);

public sealed record MixingBrushRegionData(
    MixingBrushRegionVertex[] Vertices,
    int[] TriangleIndices)
{
    public const float MinimumVertexSpacing = 25f;

    private const float GeometryTolerance = 0.001f;
    private const float SamplingIndexCellSize = MinimumVertexSpacing * 4f;
    private const int MaximumIndexedCellsPerTriangle = 4096;
    private static readonly ConditionalWeakTable<MixingBrushRegionData, TriangleSamplingIndex>
        SamplingIndexCache = new();
    private static readonly ConditionalWeakTable<MixingBrushRegionData, TriangleComponentIndex>
        ComponentIndexCache = new();
    private static readonly ConditionalWeakTable<MixingBrushRegionData, BoundaryContourIndex>
        BoundaryContourCache = new();

    private sealed class TriangleSamplingIndex
    {
        internal TriangleSamplingIndex(
            bool valid,
            Dictionary<(long X, long Y), int[]> buckets,
            int[] globalTriangles)
        {
            Valid = valid;
            Buckets = buckets;
            GlobalTriangles = globalTriangles;
        }

        internal bool Valid { get; }
        internal Dictionary<(long X, long Y), int[]> Buckets { get; }
        internal int[] GlobalTriangles { get; }
    }

    private sealed class TriangleComponentIndex
    {
        internal TriangleComponentIndex(
            bool valid,
            int[] triangleToPartIndex,
            MixingBrushRegionData[] components)
        {
            Valid = valid;
            TriangleToPartIndex = triangleToPartIndex;
            Components = components;
        }

        internal bool Valid { get; }
        internal int[] TriangleToPartIndex { get; }
        internal MixingBrushRegionData[] Components { get; }
    }

    private sealed class BoundaryContourIndex
    {
        internal BoundaryContourIndex(PointF[][] contours)
        {
            Contours = contours;
        }

        internal PointF[][] Contours { get; }
    }

    public MixingBrushRegionData DeepClone() => new(
        Vertices?.ToArray() ?? [],
        TriangleIndices?.ToArray() ?? []);

    public static bool TryNormalize(
        IReadOnlyList<MixingBrushRegionVertex> vertices,
        IReadOnlyList<int> triangleIndices,
        out MixingBrushRegionData region)
    {
        region = new MixingBrushRegionData([], []);
        if (vertices is null
            || triangleIndices is null
            || vertices.Count < 3
            || triangleIndices.Count < 3
            || triangleIndices.Count % 3 != 0)
        {
            return false;
        }

        var normalizedVertices = new MixingBrushRegionVertex[vertices.Count];
        for (var index = 0; index < vertices.Count; index++)
        {
            var source = vertices[index];
            if (!float.IsFinite(source.Point.X) || !float.IsFinite(source.Point.Y)) return false;
            normalizedVertices[index] = source with { Point = VectorUnits.Quantize(source.Point) };
        }

        var normalizedIndices = triangleIndices.ToArray();
        for (var index = 0; index < normalizedIndices.Length; index += 3)
        {
            var first = normalizedIndices[index];
            var second = normalizedIndices[index + 1];
            var third = normalizedIndices[index + 2];
            if ((uint)first >= normalizedVertices.Length
                || (uint)second >= normalizedVertices.Length
                || (uint)third >= normalizedVertices.Length
                || first == second
                || second == third
                || third == first)
            {
                return false;
            }

            var area = SignedDoubleArea(
                normalizedVertices[first].Point,
                normalizedVertices[second].Point,
                normalizedVertices[third].Point);
            if (!double.IsFinite(area) || Math.Abs(area) <= GeometryTolerance) return false;
            if (area < 0)
            {
                normalizedIndices[index + 1] = third;
                normalizedIndices[index + 2] = second;
            }
        }

        var remap = new int[normalizedVertices.Length];
        Array.Fill(remap, -1);
        var compactVertices = new List<MixingBrushRegionVertex>();
        for (var index = 0; index < normalizedIndices.Length; index++)
        {
            var sourceIndex = normalizedIndices[index];
            var destinationIndex = remap[sourceIndex];
            if (destinationIndex < 0)
            {
                destinationIndex = compactVertices.Count;
                remap[sourceIndex] = destinationIndex;
                compactVertices.Add(normalizedVertices[sourceIndex]);
            }
            normalizedIndices[index] = destinationIndex;
        }

        var positions = new HashSet<PointF>();
        if (compactVertices.Any(vertex => !positions.Add(vertex.Point))) return false;
        if (HasOverlappingTriangleInteriors(compactVertices, normalizedIndices)) return false;

        region = new MixingBrushRegionData(compactVertices.ToArray(), normalizedIndices);
        return true;
    }

    public static bool HasMinimumVertexSpacing(IReadOnlyList<MixingBrushRegionVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count < 2) return true;

        var cellSize = MinimumVertexSpacing;
        var cells = new Dictionary<(long X, long Y), List<PointF>>();
        var minimumDistanceSquared = MinimumVertexSpacing * MinimumVertexSpacing;
        foreach (var vertex in vertices)
        {
            if (!float.IsFinite(vertex.Point.X) || !float.IsFinite(vertex.Point.Y)) return false;
            var cellXValue = Math.Floor(vertex.Point.X / cellSize);
            var cellYValue = Math.Floor(vertex.Point.Y / cellSize);
            if (cellXValue <= long.MinValue + 1d
                || cellXValue >= long.MaxValue - 1d
                || cellYValue <= long.MinValue + 1d
                || cellYValue >= long.MaxValue - 1d)
            {
                return false;
            }

            var cellX = (long)cellXValue;
            var cellY = (long)cellYValue;
            for (var y = cellY - 1; y <= cellY + 1; y++)
            {
                for (var x = cellX - 1; x <= cellX + 1; x++)
                {
                    if (!cells.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var candidate in candidates)
                    {
                        var dx = (double)vertex.Point.X - candidate.X;
                        var dy = (double)vertex.Point.Y - candidate.Y;
                        if (dx * dx + dy * dy < minimumDistanceSquared - GeometryTolerance) return false;
                    }
                }
            }

            if (!cells.TryGetValue((cellX, cellY), out var points))
            {
                points = [];
                cells[(cellX, cellY)] = points;
            }
            points.Add(vertex.Point);
        }

        return true;
    }

    public bool TrySampleColor(PointF localPoint, out Color color)
    {
        color = Color.Transparent;
        if (!float.IsFinite(localPoint.X) || !float.IsFinite(localPoint.Y)) return false;
        var samplingIndex = SamplingIndexCache.GetValue(
            this,
            static region => region.BuildTriangleSamplingIndex());
        if (!samplingIndex.Valid) return false;

        var cellXValue = Math.Floor(localPoint.X / SamplingIndexCellSize);
        var cellYValue = Math.Floor(localPoint.Y / SamplingIndexCellSize);
        if (!TryLong(cellXValue, out var cellX) || !TryLong(cellYValue, out var cellY)) return false;
        if (samplingIndex.Buckets.TryGetValue((cellX, cellY), out var candidates)
            && TrySampleTriangles(candidates, localPoint, out color, out _))
        {
            return true;
        }
        return TrySampleTriangles(samplingIndex.GlobalTriangles, localPoint, out color, out _);
    }

    private bool TrySampleTriangles(
        int[] triangleOrdinals,
        PointF localPoint,
        out Color color,
        out int triangleOrdinal)
    {
        color = Color.Transparent;
        triangleOrdinal = -1;
        for (var candidate = 0; candidate < triangleOrdinals.Length; candidate++)
        {
            var triangle = triangleOrdinals[candidate];
            var index = triangle * 3;
            var first = Vertices[TriangleIndices[index]];
            var second = Vertices[TriangleIndices[index + 1]];
            var third = Vertices[TriangleIndices[index + 2]];
            if (!TryBarycentric(localPoint, first.Point, second.Point, third.Point, out var weights)) continue;

            color = InterpolatePremultipliedLinear(first.Argb, second.Argb, third.Argb, weights);
            if (color.A == 0) continue;
            triangleOrdinal = triangle;
            return true;
        }

        return false;
    }

    private TriangleSamplingIndex BuildTriangleSamplingIndex()
    {
        if (!HasSafeTriangleLayout())
        {
            return new TriangleSamplingIndex(false, new Dictionary<(long, long), int[]>(), []);
        }

        var bucketLists = new Dictionary<(long X, long Y), List<int>>();
        var globalTriangles = new List<int>();
        for (var triangle = 0; triangle < TriangleIndices.Length / 3; triangle++)
        {
            var offset = triangle * 3;
            var first = Vertices[TriangleIndices[offset]].Point;
            var second = Vertices[TriangleIndices[offset + 1]].Point;
            var third = Vertices[TriangleIndices[offset + 2]].Point;
            var left = Math.Min(first.X, Math.Min(second.X, third.X));
            var right = Math.Max(first.X, Math.Max(second.X, third.X));
            var top = Math.Min(first.Y, Math.Min(second.Y, third.Y));
            var bottom = Math.Max(first.Y, Math.Max(second.Y, third.Y));
            if (!TryLong(Math.Floor(left / SamplingIndexCellSize), out var minimumX)
                || !TryLong(Math.Floor(right / SamplingIndexCellSize), out var maximumX)
                || !TryLong(Math.Floor(top / SamplingIndexCellSize), out var minimumY)
                || !TryLong(Math.Floor(bottom / SamplingIndexCellSize), out var maximumY))
            {
                return new TriangleSamplingIndex(false, new Dictionary<(long, long), int[]>(), []);
            }

            var cellCount = ((double)maximumX - minimumX + 1d)
                * ((double)maximumY - minimumY + 1d);
            if (!double.IsFinite(cellCount) || cellCount > MaximumIndexedCellsPerTriangle)
            {
                globalTriangles.Add(triangle);
                continue;
            }

            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    if (!bucketLists.TryGetValue((x, y), out var bucket))
                    {
                        bucket = [];
                        bucketLists[(x, y)] = bucket;
                    }
                    bucket.Add(triangle);
                }
            }
        }

        return new TriangleSamplingIndex(
            true,
            bucketLists.ToDictionary(item => item.Key, item => item.Value.ToArray()),
            globalTriangles.ToArray());
    }

    public bool ContainsPaint(PointF localPoint, float tolerance = 0)
    {
        if (TrySampleColor(localPoint, out _)) return true;
        tolerance = Math.Max(0, tolerance);
        if (tolerance <= 0 || !HasSafeTriangleLayout()) return false;

        var toleranceSquared = tolerance * tolerance;
        for (var index = 0; index < TriangleIndices.Length; index += 3)
        {
            var first = Vertices[TriangleIndices[index]];
            var second = Vertices[TriangleIndices[index + 1]];
            var third = Vertices[TriangleIndices[index + 2]];
            if (PaintedEdgeWithin(localPoint, toleranceSquared, first, second)
                || PaintedEdgeWithin(localPoint, toleranceSquared, second, third)
                || PaintedEdgeWithin(localPoint, toleranceSquared, third, first))
            {
                return true;
            }
        }

        return false;
    }

    internal int ConnectedComponentCount
    {
        get
        {
            var index = ComponentIndexCache.GetValue(
                this,
                static region => region.BuildTriangleComponentIndex());
            return index.Valid ? index.Components.Length : 0;
        }
    }

    internal bool TryGetConnectedComponent(int partIndex, out MixingBrushRegionData component)
    {
        var index = ComponentIndexCache.GetValue(
            this,
            static region => region.BuildTriangleComponentIndex());
        if (!index.Valid || (uint)partIndex >= index.Components.Length)
        {
            component = new MixingBrushRegionData([], []);
            return false;
        }

        component = index.Components[partIndex];
        return true;
    }

    internal bool TryHitConnectedComponent(PointF localPoint, float tolerance, out int partIndex)
    {
        partIndex = -1;
        if (!float.IsFinite(localPoint.X) || !float.IsFinite(localPoint.Y)) return false;
        var components = ComponentIndexCache.GetValue(
            this,
            static region => region.BuildTriangleComponentIndex());
        if (!components.Valid) return false;

        var samplingIndex = SamplingIndexCache.GetValue(
            this,
            static region => region.BuildTriangleSamplingIndex());
        if (!samplingIndex.Valid) return false;
        var cellXValue = Math.Floor(localPoint.X / SamplingIndexCellSize);
        var cellYValue = Math.Floor(localPoint.Y / SamplingIndexCellSize);
        if (!TryLong(cellXValue, out var cellX) || !TryLong(cellYValue, out var cellY)) return false;
        if (samplingIndex.Buckets.TryGetValue((cellX, cellY), out var candidates)
            && TrySampleTriangles(candidates, localPoint, out _, out var triangle))
        {
            partIndex = components.TriangleToPartIndex[triangle];
            return partIndex >= 0;
        }
        if (TrySampleTriangles(samplingIndex.GlobalTriangles, localPoint, out _, out triangle))
        {
            partIndex = components.TriangleToPartIndex[triangle];
            return partIndex >= 0;
        }

        tolerance = Math.Max(0, tolerance);
        if (tolerance <= 0) return false;
        var toleranceSquared = tolerance * tolerance;
        var bestDistanceSquared = float.MaxValue;
        for (var triangleOrdinal = 0; triangleOrdinal < TriangleIndices.Length / 3; triangleOrdinal++)
        {
            var offset = triangleOrdinal * 3;
            var first = Vertices[TriangleIndices[offset]];
            var second = Vertices[TriangleIndices[offset + 1]];
            var third = Vertices[TriangleIndices[offset + 2]];
            var distanceSquared = Math.Min(
                PaintedEdgeDistanceSquared(localPoint, first, second),
                Math.Min(
                    PaintedEdgeDistanceSquared(localPoint, second, third),
                    PaintedEdgeDistanceSquared(localPoint, third, first)));
            var candidatePart = components.TriangleToPartIndex[triangleOrdinal];
            if (distanceSquared > toleranceSquared
                || distanceSquared > bestDistanceSquared + GeometryTolerance
                || candidatePart < 0)
            {
                continue;
            }
            if (distanceSquared < bestDistanceSquared - GeometryTolerance || partIndex < 0 || candidatePart < partIndex)
            {
                bestDistanceSquared = distanceSquared;
                partIndex = candidatePart;
            }
        }
        return partIndex >= 0;
    }

    internal MixingBrushRegionData[] CreateConnectedComponents(IReadOnlyList<bool> includedTriangles)
    {
        ArgumentNullException.ThrowIfNull(includedTriangles);
        if (!HasSafeTriangleLayout() || includedTriangles.Count != TriangleIndices.Length / 3) return [];
        return BuildConnectedComponentTriangleOrdinals(includedTriangles)
            .Select(triangles => CreateTriangleSubset(triangles))
            .OfType<MixingBrushRegionData>()
            .ToArray();
    }

    internal PointF[][] GetBoundaryContours()
    {
        return BoundaryContourCache.GetValue(
            this,
            static region => new BoundaryContourIndex(region.BuildBoundaryContours())).Contours;
    }

    internal bool IntersectsBounds(RectangleF bounds, Func<PointF, PointF>? transform = null)
    {
        if (!HasSafeTriangleLayout()) return false;
        transform ??= static point => point;
        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };

        for (var index = 0; index < TriangleIndices.Length; index += 3)
        {
            var first = Vertices[TriangleIndices[index]];
            var second = Vertices[TriangleIndices[index + 1]];
            var third = Vertices[TriangleIndices[index + 2]];
            if (((uint)first.Argb >> 24) == 0
                && ((uint)second.Argb >> 24) == 0
                && ((uint)third.Argb >> 24) == 0)
            {
                continue;
            }

            var firstPoint = transform(first.Point);
            var secondPoint = transform(second.Point);
            var thirdPoint = transform(third.Point);
            if ((PointInRectangle(firstPoint, bounds) && ((uint)first.Argb >> 24) > 0)
                || (PointInRectangle(secondPoint, bounds) && ((uint)second.Argb >> 24) > 0)
                || (PointInRectangle(thirdPoint, bounds) && ((uint)third.Argb >> 24) > 0))
            {
                return true;
            }

            foreach (var corner in corners)
            {
                if (!TryBarycentric(corner, firstPoint, secondPoint, thirdPoint, out var weights)) continue;
                if (InterpolateAlpha(first.Argb, second.Argb, third.Argb, weights) > 0) return true;
            }

            if (PaintedTriangleEdgeIntersectsBounds(firstPoint, first.Argb, secondPoint, second.Argb, bounds)
                || PaintedTriangleEdgeIntersectsBounds(secondPoint, second.Argb, thirdPoint, third.Argb, bounds)
                || PaintedTriangleEdgeIntersectsBounds(thirdPoint, third.Argb, firstPoint, first.Argb, bounds))
            {
                return true;
            }
        }

        return false;
    }

    internal RectangleF CalculateBounds(Func<PointF, PointF>? transform = null)
    {
        if (Vertices is null || Vertices.Length == 0) return RectangleF.Empty;
        transform ??= static point => point;
        var first = transform(Vertices[0].Point);
        var left = first.X;
        var right = first.X;
        var top = first.Y;
        var bottom = first.Y;
        for (var index = 1; index < Vertices.Length; index++)
        {
            var point = transform(Vertices[index].Point);
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    internal MixingBrushRegionData? CreateTriangleSubset(IReadOnlyCollection<int> triangleOrdinals)
    {
        if (triangleOrdinals.Count == 0 || !HasSafeTriangleLayout()) return null;
        var indices = new List<int>(triangleOrdinals.Count * 3);
        foreach (var triangle in triangleOrdinals.OrderBy(value => value))
        {
            var offset = triangle * 3;
            if (offset < 0 || offset + 2 >= TriangleIndices.Length) return null;
            indices.Add(TriangleIndices[offset]);
            indices.Add(TriangleIndices[offset + 1]);
            indices.Add(TriangleIndices[offset + 2]);
        }
        return TryNormalize(Vertices, indices, out var subset) ? subset : null;
    }

    private TriangleComponentIndex BuildTriangleComponentIndex()
    {
        if (!HasSafeTriangleLayout()) return new TriangleComponentIndex(false, [], []);
        var triangleCount = TriangleIndices.Length / 3;
        var included = Enumerable.Repeat(true, triangleCount).ToArray();
        var ordinals = BuildConnectedComponentTriangleOrdinals(included);
        var triangleToPartIndex = Enumerable.Repeat(-1, triangleCount).ToArray();
        var components = new MixingBrushRegionData[ordinals.Count];
        for (var partIndex = 0; partIndex < ordinals.Count; partIndex++)
        {
            var component = CreateTriangleSubset(ordinals[partIndex]);
            if (component is null) return new TriangleComponentIndex(false, [], []);
            components[partIndex] = component;
            foreach (var triangle in ordinals[partIndex]) triangleToPartIndex[triangle] = partIndex;
        }
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            if (TriangleCarriesPaint(triangle) && triangleToPartIndex[triangle] < 0)
            {
                return new TriangleComponentIndex(false, [], []);
            }
        }
        return new TriangleComponentIndex(true, triangleToPartIndex, components);
    }

    private List<int[]> BuildConnectedComponentTriangleOrdinals(IReadOnlyList<bool> includedTriangles)
    {
        var trianglesByEdge = new Dictionary<(int Low, int High), List<int>>();
        var eligible = new bool[includedTriangles.Count];
        for (var triangle = 0; triangle < includedTriangles.Count; triangle++)
        {
            if (!includedTriangles[triangle] || !TriangleCarriesPaint(triangle)) continue;
            eligible[triangle] = true;
            var offset = triangle * 3;
            AddEdge(TriangleIndices[offset], TriangleIndices[offset + 1], triangle);
            AddEdge(TriangleIndices[offset + 1], TriangleIndices[offset + 2], triangle);
            AddEdge(TriangleIndices[offset + 2], TriangleIndices[offset], triangle);
        }

        var visited = new bool[includedTriangles.Count];
        var result = new List<int[]>();
        var pending = new Queue<int>();
        for (var start = 0; start < includedTriangles.Count; start++)
        {
            if (!eligible[start] || visited[start]) continue;
            var component = new List<int>();
            visited[start] = true;
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var triangle = pending.Dequeue();
                component.Add(triangle);
                var offset = triangle * 3;
                VisitEdge(TriangleIndices[offset], TriangleIndices[offset + 1]);
                VisitEdge(TriangleIndices[offset + 1], TriangleIndices[offset + 2]);
                VisitEdge(TriangleIndices[offset + 2], TriangleIndices[offset]);

                void VisitEdge(int first, int second)
                {
                    if (!EdgeCarriesPaint(first, second)) return;
                    var key = first < second ? (first, second) : (second, first);
                    if (!trianglesByEdge.TryGetValue(key, out var adjacentTriangles)) return;
                    foreach (var adjacent in adjacentTriangles)
                    {
                        if (visited[adjacent]) continue;
                        visited[adjacent] = true;
                        pending.Enqueue(adjacent);
                    }
                }
            }
            result.Add(component.OrderBy(triangle => triangle).ToArray());
        }
        return result;

        void AddEdge(int first, int second, int triangle)
        {
            if (!EdgeCarriesPaint(first, second)) return;
            var key = first < second ? (first, second) : (second, first);
            if (!trianglesByEdge.TryGetValue(key, out var triangles))
            {
                triangles = [];
                trianglesByEdge[key] = triangles;
            }
            triangles.Add(triangle);
        }
    }

    private bool TriangleCarriesPaint(int triangleOrdinal)
    {
        var offset = triangleOrdinal * 3;
        return ((uint)Vertices[TriangleIndices[offset]].Argb >> 24) > 0
            || ((uint)Vertices[TriangleIndices[offset + 1]].Argb >> 24) > 0
            || ((uint)Vertices[TriangleIndices[offset + 2]].Argb >> 24) > 0;
    }

    private bool EdgeCarriesPaint(int firstVertex, int secondVertex) =>
        ((uint)Vertices[firstVertex].Argb >> 24) > 0
        || ((uint)Vertices[secondVertex].Argb >> 24) > 0;

    private PointF[][] BuildBoundaryContours()
    {
        if (!HasSafeTriangleLayout()) return [];
        var edgeCounts = new Dictionary<(int Low, int High), (int Count, int Start, int End)>();
        for (var offset = 0; offset < TriangleIndices.Length; offset += 3)
        {
            AddEdge(TriangleIndices[offset], TriangleIndices[offset + 1]);
            AddEdge(TriangleIndices[offset + 1], TriangleIndices[offset + 2]);
            AddEdge(TriangleIndices[offset + 2], TriangleIndices[offset]);
        }

        var boundaryEdges = edgeCounts.Values
            .Where(edge => edge.Count == 1)
            .Select(edge => (edge.Start, edge.End))
            .OrderBy(edge => edge.Start)
            .ThenBy(edge => edge.End)
            .ToArray();
        var outgoing = boundaryEdges
            .GroupBy(edge => edge.Start)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.End).ToArray());
        var unused = boundaryEdges.ToHashSet();
        var contours = new List<PointF[]>();
        foreach (var firstEdge in boundaryEdges)
        {
            if (!unused.Remove(firstEdge)) continue;
            var start = firstEdge.Start;
            var current = firstEdge.End;
            var vertices = new List<int> { start };
            while (current != start)
            {
                vertices.Add(current);
                if (!outgoing.TryGetValue(current, out var candidates)) break;
                var found = false;
                foreach (var candidate in candidates)
                {
                    if (!unused.Remove(candidate)) continue;
                    current = candidate.End;
                    found = true;
                    break;
                }
                if (!found) break;
            }
            if (current == start && vertices.Count >= 3)
            {
                contours.Add(vertices.Select(vertex => Vertices[vertex].Point).ToArray());
            }
        }
        return contours.ToArray();

        void AddEdge(int start, int end)
        {
            var key = start < end ? (start, end) : (end, start);
            if (edgeCounts.TryGetValue(key, out var edge))
            {
                edgeCounts[key] = (edge.Count + 1, edge.Start, edge.End);
            }
            else
            {
                edgeCounts[key] = (1, start, end);
            }
        }
    }

    public static Color InterpolatePremultipliedLinear(
        int firstArgb,
        int secondArgb,
        int thirdArgb,
        PointF barycentricWeights)
    {
        var first = Color.FromArgb(firstArgb);
        var second = Color.FromArgb(secondArgb);
        var third = Color.FromArgb(thirdArgb);
        var firstAlpha = first.A / 255f;
        var secondAlpha = second.A / 255f;
        var thirdAlpha = third.A / 255f;
        var alpha = firstAlpha * barycentricWeights.X
            + secondAlpha * barycentricWeights.Y
            + thirdAlpha * (1f - barycentricWeights.X - barycentricWeights.Y);
        if (alpha <= 0.5f / 255f) return Color.Transparent;

        float Channel(byte firstChannel, byte secondChannel, byte thirdChannel)
        {
            var premultiplied = SrgbToLinear(firstChannel) * firstAlpha * barycentricWeights.X
                + SrgbToLinear(secondChannel) * secondAlpha * barycentricWeights.Y
                + SrgbToLinear(thirdChannel) * thirdAlpha * (1f - barycentricWeights.X - barycentricWeights.Y);
            return LinearToSrgb(premultiplied / alpha);
        }

        return Color.FromArgb(
            ToByte(alpha),
            ToByte(Channel(first.R, second.R, third.R)),
            ToByte(Channel(first.G, second.G, third.G)),
            ToByte(Channel(first.B, second.B, third.B)));
    }

    private static bool PaintedEdgeWithin(
        PointF point,
        float toleranceSquared,
        MixingBrushRegionVertex start,
        MixingBrushRegionVertex end)
    {
        var dx = end.Point.X - start.Point.X;
        var dy = end.Point.Y - start.Point.Y;
        var lengthSquared = dx * dx + dy * dy;
        var amount = lengthSquared <= GeometryTolerance
            ? 0f
            : Math.Clamp(
                ((point.X - start.Point.X) * dx + (point.Y - start.Point.Y) * dy) / lengthSquared,
                0f,
                1f);
        var nearestX = start.Point.X + dx * amount;
        var nearestY = start.Point.Y + dy * amount;
        var distanceX = point.X - nearestX;
        var distanceY = point.Y - nearestY;
        if (distanceX * distanceX + distanceY * distanceY > toleranceSquared) return false;
        return LerpAlpha(start.Argb, end.Argb, amount) > 0;
    }

    private static float PaintedEdgeDistanceSquared(
        PointF point,
        MixingBrushRegionVertex start,
        MixingBrushRegionVertex end)
    {
        var dx = end.Point.X - start.Point.X;
        var dy = end.Point.Y - start.Point.Y;
        var lengthSquared = dx * dx + dy * dy;
        var amount = lengthSquared <= GeometryTolerance
            ? 0f
            : Math.Clamp(
                ((point.X - start.Point.X) * dx + (point.Y - start.Point.Y) * dy) / lengthSquared,
                0f,
                1f);
        if (LerpAlpha(start.Argb, end.Argb, amount) <= 0) return float.MaxValue;
        var nearestX = start.Point.X + dx * amount;
        var nearestY = start.Point.Y + dy * amount;
        var distanceX = point.X - nearestX;
        var distanceY = point.Y - nearestY;
        return distanceX * distanceX + distanceY * distanceY;
    }

    private static bool PaintedTriangleEdgeIntersectsBounds(
        PointF start,
        int startArgb,
        PointF end,
        int endArgb,
        RectangleF bounds)
    {
        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };
        for (var index = 0; index < corners.Length; index++)
        {
            if (!TrySegmentIntersection(start, end, corners[index], corners[(index + 1) % corners.Length], out var amount)) continue;
            if (LerpAlpha(startArgb, endArgb, amount) > 0) return true;
        }
        return false;
    }

    private static bool TryBarycentric(
        PointF point,
        PointF first,
        PointF second,
        PointF third,
        out PointF weights)
    {
        var denominator = (double)(second.Y - third.Y) * (first.X - third.X)
            + (double)(third.X - second.X) * (first.Y - third.Y);
        if (Math.Abs(denominator) <= GeometryTolerance)
        {
            weights = PointF.Empty;
            return false;
        }

        var firstWeight = ((double)(second.Y - third.Y) * (point.X - third.X)
            + (double)(third.X - second.X) * (point.Y - third.Y)) / denominator;
        var secondWeight = ((double)(third.Y - first.Y) * (point.X - third.X)
            + (double)(first.X - third.X) * (point.Y - third.Y)) / denominator;
        var thirdWeight = 1d - firstWeight - secondWeight;
        if (firstWeight < -GeometryTolerance
            || secondWeight < -GeometryTolerance
            || thirdWeight < -GeometryTolerance)
        {
            weights = PointF.Empty;
            return false;
        }

        weights = new PointF((float)firstWeight, (float)secondWeight);
        return true;
    }

    private bool HasSafeTriangleLayout()
    {
        if (Vertices is null
            || TriangleIndices is null
            || Vertices.Length < 3
            || TriangleIndices.Length < 3
            || TriangleIndices.Length % 3 != 0)
        {
            return false;
        }

        foreach (var vertexIndex in TriangleIndices)
        {
            if ((uint)vertexIndex >= Vertices.Length) return false;
        }
        return true;
    }

    private static bool HasOverlappingTriangleInteriors(
        IReadOnlyList<MixingBrushRegionVertex> vertices,
        IReadOnlyList<int> indices)
    {
        const int maximumCellsPerTriangle = 4096;
        var triangleCount = indices.Count / 3;
        var bounds = new TriangleBounds[triangleCount];
        var buckets = new Dictionary<(long X, long Y), List<int>>();
        var largeTriangles = new List<int>();
        var candidates = new HashSet<int>();
        var cellSize = MinimumVertexSpacing * 4f;
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var offset = triangle * 3;
            var first = vertices[indices[offset]].Point;
            var second = vertices[indices[offset + 1]].Point;
            var third = vertices[indices[offset + 2]].Point;
            var triangleBounds = new TriangleBounds(
                Math.Min(first.X, Math.Min(second.X, third.X)),
                Math.Min(first.Y, Math.Min(second.Y, third.Y)),
                Math.Max(first.X, Math.Max(second.X, third.X)),
                Math.Max(first.Y, Math.Max(second.Y, third.Y)));
            bounds[triangle] = triangleBounds;

            var hasCellRange = TryGetTriangleCellRange(
                triangleBounds,
                cellSize,
                maximumCellsPerTriangle,
                out var minimumCellX,
                out var maximumCellX,
                out var minimumCellY,
                out var maximumCellY);
            candidates.Clear();
            if (hasCellRange)
            {
                foreach (var largeTriangle in largeTriangles) candidates.Add(largeTriangle);
                for (var y = minimumCellY; y <= maximumCellY; y++)
                {
                    for (var x = minimumCellX; x <= maximumCellX; x++)
                    {
                        if (!buckets.TryGetValue((x, y), out var bucket)) continue;
                        foreach (var candidate in bucket) candidates.Add(candidate);
                    }
                }
            }
            else
            {
                for (var candidate = 0; candidate < triangle; candidate++) candidates.Add(candidate);
            }

            foreach (var candidate in candidates)
            {
                if (TriangleBoundsOverlap(triangleBounds, bounds[candidate])
                    && TriangleInteriorsOverlap(vertices, indices, offset, candidate * 3))
                {
                    return true;
                }
            }

            if (!hasCellRange)
            {
                largeTriangles.Add(triangle);
                continue;
            }

            for (var y = minimumCellY; y <= maximumCellY; y++)
            {
                for (var x = minimumCellX; x <= maximumCellX; x++)
                {
                    if (!buckets.TryGetValue((x, y), out var bucket))
                    {
                        bucket = [];
                        buckets[(x, y)] = bucket;
                    }
                    bucket.Add(triangle);
                }
            }
        }
        return false;
    }

    private static bool TryGetTriangleCellRange(
        TriangleBounds bounds,
        float cellSize,
        int maximumCellCount,
        out long minimumX,
        out long maximumX,
        out long minimumY,
        out long maximumY)
    {
        var minimumXValue = Math.Floor(bounds.Left / cellSize);
        var maximumXValue = Math.Floor(bounds.Right / cellSize);
        var minimumYValue = Math.Floor(bounds.Top / cellSize);
        var maximumYValue = Math.Floor(bounds.Bottom / cellSize);
        if (minimumXValue <= long.MinValue
            || maximumXValue >= long.MaxValue
            || minimumYValue <= long.MinValue
            || maximumYValue >= long.MaxValue)
        {
            minimumX = maximumX = minimumY = maximumY = 0;
            return false;
        }

        minimumX = (long)minimumXValue;
        maximumX = (long)maximumXValue;
        minimumY = (long)minimumYValue;
        maximumY = (long)maximumYValue;
        var width = (double)maximumX - minimumX + 1d;
        var height = (double)maximumY - minimumY + 1d;
        return width > 0d && height > 0d && width * height <= maximumCellCount;
    }

    private static bool TriangleBoundsOverlap(TriangleBounds first, TriangleBounds second) =>
        first.Right >= second.Left - GeometryTolerance
        && first.Left <= second.Right + GeometryTolerance
        && first.Bottom >= second.Top - GeometryTolerance
        && first.Top <= second.Bottom + GeometryTolerance;

    private static bool TriangleInteriorsOverlap(
        IReadOnlyList<MixingBrushRegionVertex> vertices,
        IReadOnlyList<int> indices,
        int firstOffset,
        int secondOffset)
    {
        var first0 = vertices[indices[firstOffset]].Point;
        var first1 = vertices[indices[firstOffset + 1]].Point;
        var first2 = vertices[indices[firstOffset + 2]].Point;
        var second0 = vertices[indices[secondOffset]].Point;
        var second1 = vertices[indices[secondOffset + 1]].Point;
        var second2 = vertices[indices[secondOffset + 2]].Point;
        if (PointStrictlyInsideTriangle(first0, second0, second1, second2)
            || PointStrictlyInsideTriangle(first1, second0, second1, second2)
            || PointStrictlyInsideTriangle(first2, second0, second1, second2)
            || PointStrictlyInsideTriangle(second0, first0, first1, first2)
            || PointStrictlyInsideTriangle(second1, first0, first1, first2)
            || PointStrictlyInsideTriangle(second2, first0, first1, first2))
        {
            return true;
        }

        var firstCentroid = new PointF(
            (first0.X + first1.X + first2.X) / 3f,
            (first0.Y + first1.Y + first2.Y) / 3f);
        var secondCentroid = new PointF(
            (second0.X + second1.X + second2.X) / 3f,
            (second0.Y + second1.Y + second2.Y) / 3f);
        if (PointStrictlyInsideTriangle(firstCentroid, second0, second1, second2)
            || PointStrictlyInsideTriangle(secondCentroid, first0, first1, first2))
        {
            return true;
        }

        for (var firstEdge = 0; firstEdge < 3; firstEdge++)
        {
            var firstStart = TrianglePoint(firstEdge, first0, first1, first2);
            var firstEnd = TrianglePoint((firstEdge + 1) % 3, first0, first1, first2);
            for (var secondEdge = 0; secondEdge < 3; secondEdge++)
            {
                var secondStart = TrianglePoint(secondEdge, second0, second1, second2);
                var secondEnd = TrianglePoint((secondEdge + 1) % 3, second0, second1, second2);
                if (SegmentsProperlyIntersect(firstStart, firstEnd, secondStart, secondEnd)) return true;
            }
        }
        return false;
    }

    private static bool PointStrictlyInsideTriangle(
        PointF point,
        PointF first,
        PointF second,
        PointF third)
    {
        if (!TryBarycentric(point, first, second, third, out var weights)) return false;
        var thirdWeight = 1f - weights.X - weights.Y;
        return weights.X > GeometryTolerance
            && weights.Y > GeometryTolerance
            && thirdWeight > GeometryTolerance;
    }

    private static PointF TrianglePoint(int index, PointF first, PointF second, PointF third) =>
        index switch
        {
            0 => first,
            1 => second,
            _ => third
        };

    private readonly record struct TriangleBounds(float Left, float Top, float Right, float Bottom);

    private static bool SegmentsProperlyIntersect(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd)
    {
        var firstSide = SignedDoubleArea(firstStart, firstEnd, secondStart);
        var secondSide = SignedDoubleArea(firstStart, firstEnd, secondEnd);
        var thirdSide = SignedDoubleArea(secondStart, secondEnd, firstStart);
        var fourthSide = SignedDoubleArea(secondStart, secondEnd, firstEnd);
        return ((firstSide > GeometryTolerance && secondSide < -GeometryTolerance)
                || (firstSide < -GeometryTolerance && secondSide > GeometryTolerance))
            && ((thirdSide > GeometryTolerance && fourthSide < -GeometryTolerance)
                || (thirdSide < -GeometryTolerance && fourthSide > GeometryTolerance));
    }

    private static bool TrySegmentIntersection(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd,
        out float firstAmount)
    {
        var firstX = (double)firstEnd.X - firstStart.X;
        var firstY = (double)firstEnd.Y - firstStart.Y;
        var secondX = (double)secondEnd.X - secondStart.X;
        var secondY = (double)secondEnd.Y - secondStart.Y;
        var denominator = firstX * secondY - firstY * secondX;
        if (Math.Abs(denominator) <= GeometryTolerance)
        {
            firstAmount = 0;
            return false;
        }

        var offsetX = (double)secondStart.X - firstStart.X;
        var offsetY = (double)secondStart.Y - firstStart.Y;
        var first = (offsetX * secondY - offsetY * secondX) / denominator;
        var second = (offsetX * firstY - offsetY * firstX) / denominator;
        if (first < -GeometryTolerance
            || first > 1 + GeometryTolerance
            || second < -GeometryTolerance
            || second > 1 + GeometryTolerance)
        {
            firstAmount = 0;
            return false;
        }

        firstAmount = (float)Math.Clamp(first, 0d, 1d);
        return true;
    }

    private static bool PointInRectangle(PointF point, RectangleF bounds) =>
        point.X >= bounds.Left - GeometryTolerance
        && point.X <= bounds.Right + GeometryTolerance
        && point.Y >= bounds.Top - GeometryTolerance
        && point.Y <= bounds.Bottom + GeometryTolerance;

    private static bool TryLong(double value, out long result)
    {
        if (!double.IsFinite(value)
            || value <= long.MinValue + 1d
            || value >= long.MaxValue - 1d)
        {
            result = 0;
            return false;
        }

        result = (long)value;
        return true;
    }

    private static double SignedDoubleArea(PointF first, PointF second, PointF third) =>
        ((double)second.X - first.X) * (third.Y - first.Y)
        - ((double)second.Y - first.Y) * (third.X - first.X);

    private static int InterpolateAlpha(int firstArgb, int secondArgb, int thirdArgb, PointF weights)
    {
        var thirdWeight = 1f - weights.X - weights.Y;
        return Math.Clamp((int)MathF.Round(
            ((uint)firstArgb >> 24) * weights.X
            + ((uint)secondArgb >> 24) * weights.Y
            + ((uint)thirdArgb >> 24) * thirdWeight), 0, 255);
    }

    private static int LerpAlpha(int startArgb, int endArgb, float amount)
    {
        var startAlpha = (int)((uint)startArgb >> 24);
        var endAlpha = (int)((uint)endArgb >> 24);
        return Math.Clamp((int)MathF.Round(startAlpha + (endAlpha - startAlpha) * amount), 0, 255);
    }

    private static float SrgbToLinear(byte channel)
    {
        var value = channel / 255f;
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static int ToByte(float value) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
