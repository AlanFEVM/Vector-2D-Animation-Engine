namespace VectorAnimationEngine;

internal readonly record struct MixingBrushRegionMergeResult(
    int ObjectIndex,
    int MergedObjectCount)
{
    internal static MixingBrushRegionMergeResult Failed => new(-1, 0);
    public bool Success => ObjectIndex >= 0;
    public bool Merged => MergedObjectCount > 0;
}

internal sealed record PreparedMixingBrushGridRegion(
    MixingBrushRegionData Region,
    IReadOnlyDictionary<MixingBrushGridPoint, int> VertexArgb,
    IReadOnlyCollection<MixingBrushGridCell> Cells);

internal readonly record struct MixingBrushGridPoint(long X, long Y);

internal readonly record struct MixingBrushGridCell(long X, long Y);

internal static class MixingBrushRegionMerger
{
    private const double GridTolerance = 0.0001d;

    internal static bool TryPrepare(
        MixingBrushRegionData region,
        out PreparedMixingBrushGridRegion prepared)
    {
        prepared = null!;
        if (region is null
            || !MixingBrushRegionData.TryNormalize(
                region.Vertices,
                region.TriangleIndices,
                out var normalized))
        {
            return false;
        }

        return TryPrepareNormalized(normalized, out prepared);
    }

    internal static bool TryPrepareNormalized(
        MixingBrushRegionData normalized,
        out PreparedMixingBrushGridRegion prepared)
    {
        prepared = null!;
        if (normalized is null
            || normalized.Vertices is not { Length: >= 3 }
            || normalized.TriangleIndices is not { Length: >= 3 }
            || normalized.TriangleIndices.Length % 3 != 0
            || normalized.TriangleIndices.Any(index => (uint)index >= normalized.Vertices.Length))
        {
            return false;
        }

        var vertexKeys = new MixingBrushGridPoint[normalized.Vertices.Length];
        var colors = new Dictionary<MixingBrushGridPoint, int>(normalized.Vertices.Length);
        for (var index = 0; index < normalized.Vertices.Length; index++)
        {
            var vertex = normalized.Vertices[index];
            if (!TryGridPoint(vertex.Point, out var key)
                || !colors.TryAdd(key, vertex.Argb))
            {
                return false;
            }
            vertexKeys[index] = key;
        }

        var cellMasks = new Dictionary<MixingBrushGridCell, byte>();
        for (var offset = 0; offset < normalized.TriangleIndices.Length; offset += 3)
        {
            var first = vertexKeys[normalized.TriangleIndices[offset]];
            var second = vertexKeys[normalized.TriangleIndices[offset + 1]];
            var third = vertexKeys[normalized.TriangleIndices[offset + 2]];
            if (!TryGridTriangle(first, second, third, out var cell, out var mask)) return false;

            cellMasks.TryGetValue(cell, out var existingMask);
            if ((existingMask & mask) != 0) return false;
            cellMasks[cell] = (byte)(existingMask | mask);
        }

        if (cellMasks.Count == 0 || cellMasks.Values.Any(mask => mask != 0b11)) return false;
        prepared = new PreparedMixingBrushGridRegion(
            normalized,
            colors,
            cellMasks.Keys.ToArray());
        return true;
    }

    internal static bool TryMerge(
        IReadOnlyList<PreparedMixingBrushGridRegion> regionsInPaintOrder,
        out MixingBrushRegionData merged)
    {
        merged = new MixingBrushRegionData([], []);
        if (regionsInPaintOrder is null || regionsInPaintOrder.Count == 0) return false;

        var colors = new Dictionary<MixingBrushGridPoint, int>();
        var cells = new HashSet<MixingBrushGridCell>();
        foreach (var region in regionsInPaintOrder)
        {
            if (region is null || region.Cells.Count == 0) return false;
            foreach (var (point, sourceArgb) in region.VertexArgb)
            {
                colors[point] = colors.TryGetValue(point, out var backdropArgb)
                    ? CompositeLinearSourceOver(backdropArgb, sourceArgb)
                    : sourceArgb;
            }
            cells.UnionWith(region.Cells);
        }
        if (cells.Count > int.MaxValue / 6) return false;

        var vertices = new List<MixingBrushRegionVertex>(colors.Count);
        var indices = new List<int>(cells.Count * 6);
        var vertexIndices = new Dictionary<MixingBrushGridPoint, int>(colors.Count);

        int VertexIndex(MixingBrushGridPoint point)
        {
            if (vertexIndices.TryGetValue(point, out var index)) return index;
            index = vertices.Count;
            vertexIndices[point] = index;
            vertices.Add(new MixingBrushRegionVertex(WorldPoint(point), colors[point]));
            return index;
        }

        foreach (var cell in cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            var topLeft = VertexIndex(new MixingBrushGridPoint(cell.X, cell.Y));
            var topRight = VertexIndex(new MixingBrushGridPoint(cell.X + 1, cell.Y));
            var bottomRight = VertexIndex(new MixingBrushGridPoint(cell.X + 1, cell.Y + 1));
            var bottomLeft = VertexIndex(new MixingBrushGridPoint(cell.X, cell.Y + 1));
            indices.Add(topLeft);
            indices.Add(topRight);
            indices.Add(bottomRight);
            indices.Add(topLeft);
            indices.Add(bottomRight);
            indices.Add(bottomLeft);
        }

        return MixingBrushRegionData.TryNormalize(vertices, indices, out merged);
    }

    private static bool TryGridPoint(PointF point, out MixingBrushGridPoint gridPoint)
    {
        var spacing = MixingBrushRegionData.MinimumVertexSpacing;
        var scaledX = point.X / spacing;
        var scaledY = point.Y / spacing;
        var roundedX = Math.Round(scaledX);
        var roundedY = Math.Round(scaledY);
        if (!double.IsFinite(roundedX)
            || !double.IsFinite(roundedY)
            || Math.Abs(scaledX - roundedX) > GridTolerance
            || Math.Abs(scaledY - roundedY) > GridTolerance
            || roundedX <= int.MinValue
            || roundedX >= int.MaxValue
            || roundedY <= int.MinValue
            || roundedY >= int.MaxValue)
        {
            gridPoint = default;
            return false;
        }

        gridPoint = new MixingBrushGridPoint((long)roundedX, (long)roundedY);
        return true;
    }

    private static bool TryGridTriangle(
        MixingBrushGridPoint first,
        MixingBrushGridPoint second,
        MixingBrushGridPoint third,
        out MixingBrushGridCell cell,
        out byte mask)
    {
        var minimumX = Math.Min(first.X, Math.Min(second.X, third.X));
        var maximumX = Math.Max(first.X, Math.Max(second.X, third.X));
        var minimumY = Math.Min(first.Y, Math.Min(second.Y, third.Y));
        var maximumY = Math.Max(first.Y, Math.Max(second.Y, third.Y));
        cell = new MixingBrushGridCell(minimumX, minimumY);
        mask = 0;
        if (maximumX - minimumX != 1 || maximumY - minimumY != 1) return false;

        var topLeft = new MixingBrushGridPoint(minimumX, minimumY);
        var topRight = new MixingBrushGridPoint(maximumX, minimumY);
        var bottomRight = new MixingBrushGridPoint(maximumX, maximumY);
        var bottomLeft = new MixingBrushGridPoint(minimumX, maximumY);
        if (SameTriangle(first, second, third, topLeft, topRight, bottomRight))
        {
            mask = 0b01;
            return true;
        }
        if (SameTriangle(first, second, third, topLeft, bottomRight, bottomLeft))
        {
            mask = 0b10;
            return true;
        }
        return false;
    }

    private static bool SameTriangle(
        MixingBrushGridPoint first,
        MixingBrushGridPoint second,
        MixingBrushGridPoint third,
        MixingBrushGridPoint expectedFirst,
        MixingBrushGridPoint expectedSecond,
        MixingBrushGridPoint expectedThird) =>
        Contains(first, second, third, expectedFirst)
        && Contains(first, second, third, expectedSecond)
        && Contains(first, second, third, expectedThird);

    private static bool Contains(
        MixingBrushGridPoint first,
        MixingBrushGridPoint second,
        MixingBrushGridPoint third,
        MixingBrushGridPoint value) =>
        first == value || second == value || third == value;

    private static PointF WorldPoint(MixingBrushGridPoint point)
    {
        var spacing = MixingBrushRegionData.MinimumVertexSpacing;
        return VectorUnits.Quantize(new PointF(
            (float)(point.X * (double)spacing),
            (float)(point.Y * (double)spacing)));
    }

    private static int CompositeLinearSourceOver(int backdropArgb, int sourceArgb)
    {
        var backdrop = Color.FromArgb(backdropArgb);
        var source = Color.FromArgb(sourceArgb);
        if (source.A == 0) return backdropArgb;
        if (backdrop.A == 0) return sourceArgb;

        var sourceAlpha = source.A / 255f;
        var backdropAlpha = backdrop.A / 255f;
        var backdropWeight = backdropAlpha * (1f - sourceAlpha);
        var outputAlpha = sourceAlpha + backdropWeight;
        if (outputAlpha <= 0.5f / 255f) return Color.Transparent.ToArgb();

        float Channel(byte backdropChannel, byte sourceChannel) => LinearToSrgb(
            (SrgbToLinear(sourceChannel) * sourceAlpha
                + SrgbToLinear(backdropChannel) * backdropWeight) / outputAlpha);

        return Color.FromArgb(
            ToByte(outputAlpha),
            ToByte(Channel(backdrop.R, source.R)),
            ToByte(Channel(backdrop.G, source.G)),
            ToByte(Channel(backdrop.B, source.B))).ToArgb();
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
