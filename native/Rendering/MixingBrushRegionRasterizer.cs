using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal readonly record struct MixingBrushRasterVertex(PointF Point, int Argb);

internal readonly record struct MixingBrushBoundaryEdge(int StartVertex, int EndVertex);

internal sealed class MixingBrushRegionRaster : IDisposable
{
    private GCHandle _pixelHandle;
    private bool _disposed;

    internal MixingBrushRegionRaster(
        RectangleF localBounds,
        int pixelWidth,
        int pixelHeight,
        float pixelsPerLocalUnit,
        int[] pixels)
    {
        LocalBounds = localBounds;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PixelsPerLocalUnit = pixelsPerLocalUnit;
        Pixels = pixels;
        _pixelHandle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        try
        {
            Bitmap = new Bitmap(
                PixelWidth,
                PixelHeight,
                Stride,
                PixelFormat.Format32bppPArgb,
                _pixelHandle.AddrOfPinnedObject());
        }
        catch
        {
            _pixelHandle.Free();
            throw;
        }
    }

    internal RectangleF LocalBounds { get; }
    internal int PixelWidth { get; }
    internal int PixelHeight { get; }
    internal int PixelCount => PixelWidth * PixelHeight;
    internal int Stride => PixelWidth * sizeof(int);
    internal float PixelsPerLocalUnit { get; }
    internal int[] Pixels { get; }
    internal Bitmap Bitmap { get; }
    internal IntPtr PixelData => _pixelHandle.IsAllocated ? _pixelHandle.AddrOfPinnedObject() : IntPtr.Zero;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bitmap.Dispose();
        if (_pixelHandle.IsAllocated) _pixelHandle.Free();
    }
}

internal sealed class MixingBrushRegionRasterCache : IDisposable
{
    private const int MaximumEntries = 64;
    private const long MaximumPixels = 8_388_608;

    private readonly Dictionary<CacheKey, CacheEntry> _entries = new();
    private readonly LinkedList<CacheKey> _leastRecentlyUsed = new();
    private long _pixelCount;

    private readonly record struct CacheKey(
        VectorScene Scene,
        int ObjectIndex,
        long GeometryRevision,
        int ScaleBucket);

    private sealed record CacheEntry(
        MixingBrushRegionRaster Raster,
        LinkedListNode<CacheKey> Node);

    internal MixingBrushRegionRaster? GetOrCreate(
        VectorScene scene,
        int objectIndex,
        long geometryRevision,
        int scaleBucket,
        Func<float, MixingBrushRegionRaster?> create)
    {
        var key = new CacheKey(scene, objectIndex, geometryRevision, scaleBucket);
        if (_entries.TryGetValue(key, out var cached))
        {
            _leastRecentlyUsed.Remove(cached.Node);
            _leastRecentlyUsed.AddLast(cached.Node);
            return cached.Raster;
        }

        RemoveObsoleteRevisions(scene, objectIndex, geometryRevision);
        var raster = create(MixingBrushRegionRasterizer.ScaleForBucket(scaleBucket));
        if (raster is null) return null;

        var node = _leastRecentlyUsed.AddLast(key);
        _entries.Add(key, new CacheEntry(raster, node));
        _pixelCount += raster.PixelCount;
        Trim();
        return raster;
    }

    internal void Clear()
    {
        foreach (var entry in _entries.Values) entry.Raster.Dispose();
        _entries.Clear();
        _leastRecentlyUsed.Clear();
        _pixelCount = 0;
    }

    public void Dispose() => Clear();

    private void RemoveObsoleteRevisions(
        VectorScene scene,
        int objectIndex,
        long geometryRevision)
    {
        List<CacheKey>? obsolete = null;
        foreach (var key in _entries.Keys)
        {
            if (ReferenceEquals(key.Scene, scene)
                && key.ObjectIndex == objectIndex
                && key.GeometryRevision != geometryRevision)
            {
                (obsolete ??= []).Add(key);
            }
        }

        if (obsolete is null) return;
        foreach (var key in obsolete) Remove(key);
    }

    private void Trim()
    {
        while ((_entries.Count > MaximumEntries || _pixelCount > MaximumPixels)
            && _leastRecentlyUsed.First is { } oldest)
        {
            Remove(oldest.Value);
        }
    }

    private void Remove(CacheKey key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _leastRecentlyUsed.Remove(entry.Node);
        _pixelCount -= entry.Raster.PixelCount;
        entry.Raster.Dispose();
    }
}

internal static class MixingBrushRegionRasterizer
{
    private const int MaximumRasterDimension = 2_048;
    private const int MaximumRasterPixels = 1_048_576;
    private const float LegacyEdgeSoftness = 0.14f;
    private const double GeometryEpsilon = 0.000001;
    private const int LinearSrgbTableResolution = 4_096;
    private static readonly float[] SrgbLinearTable = BuildSrgbLinearTable();
    private static readonly float[] LinearSrgbTable = BuildLinearSrgbTable();

    private readonly record struct RasterLayout(
        RectangleF Bounds,
        int PixelWidth,
        int PixelHeight,
        float Scale);

    internal static int ResolveScaleBucket(float screenPixelsPerLocalUnit)
    {
        var scale = float.IsFinite(screenPixelsPerLocalUnit)
            ? Math.Max(0.000001f, screenPixelsPerLocalUnit)
            : 1f;
        return Math.Clamp((int)MathF.Round(MathF.Log2(scale) * 4f), -96, 96);
    }

    internal static float ScaleForBucket(int scaleBucket) =>
        MathF.Pow(2f, Math.Clamp(scaleBucket, -96, 96) / 4f);

    internal static MixingBrushRegionRaster? RasterizeMesh(
        IReadOnlyList<MixingBrushRasterVertex> vertices,
        IReadOnlyList<int> triangleIndices,
        float requestedPixelsPerLocalUnit)
    {
        if (vertices is null
            || triangleIndices is null
            || vertices.Count < 3
            || triangleIndices.Count < 3
            || triangleIndices.Count % 3 != 0
            || !TryGetVertexBounds(vertices, out var geometryBounds)
            || !TryCreateLayout(geometryBounds, requestedPixelsPerLocalUnit, out var layout))
        {
            return null;
        }

        var accumulators = new Vector4[layout.PixelWidth * layout.PixelHeight];
        var renderedTriangle = false;
        for (var offset = 0; offset < triangleIndices.Count; offset += 3)
        {
            var firstIndex = triangleIndices[offset];
            var secondIndex = triangleIndices[offset + 1];
            var thirdIndex = triangleIndices[offset + 2];
            if ((uint)firstIndex >= vertices.Count
                || (uint)secondIndex >= vertices.Count
                || (uint)thirdIndex >= vertices.Count)
            {
                continue;
            }

            var first = vertices[firstIndex];
            var second = vertices[secondIndex];
            var third = vertices[thirdIndex];
            if (!IsFinite(first.Point) || !IsFinite(second.Point) || !IsFinite(third.Point)) continue;

            var area = Edge(first.Point, second.Point, third.Point);
            if (Math.Abs(area) <= GeometryEpsilon) continue;
            if (area < 0)
            {
                (second, third) = (third, second);
                area = -area;
            }

            renderedTriangle = true;
            if ((((uint)first.Argb | (uint)second.Argb | (uint)third.Argb) >> 24) == 0)
            {
                continue;
            }

            RasterizeTriangle(accumulators, layout, first, second, third, area);
        }

        return renderedTriangle ? CreateRaster(layout, accumulators) : null;
    }

    internal static MixingBrushRegionRaster? RasterizeTrajectory(
        IReadOnlyList<MixingStrokeCoverageCell> cells,
        float requestedPixelsPerLocalUnit)
    {
        if (cells is null
            || cells.Count == 0
            || !TryGetCellBounds(cells, out var geometryBounds)
            || !TryCreateLayout(geometryBounds, requestedPixelsPerLocalUnit, out var layout))
        {
            return null;
        }

        var accumulators = new Vector4[layout.PixelWidth * layout.PixelHeight];
        foreach (var cell in cells)
        {
            var color = Color.FromArgb(cell.Argb);
            if (color.A == 0 || cell.Radius <= 0 || !float.IsFinite(cell.Radius)) continue;

            PixelRange(cell.Bounds, layout, out var left, out var top, out var right, out var bottom);
            for (var y = top; y <= bottom; y++)
            {
                var localY = layout.Bounds.Top + (y + 0.5f) / layout.Scale;
                var row = y * layout.PixelWidth;
                for (var x = left; x <= right; x++)
                {
                    var point = new PointF(
                        layout.Bounds.Left + (x + 0.5f) / layout.Scale,
                        localY);
                    if (!cell.Contains(point) || IsOwnedByNextCell(cell, point)) continue;

                    var normalizedDistance = NormalizedDistance(cell, point);
                    var edge = Math.Clamp((1f - normalizedDistance) / LegacyEdgeSoftness, 0f, 1f);
                    var coverage = edge * edge * (3f - 2f * edge);
                    var alpha = color.A / 255f * coverage;
                    if (alpha <= 0) continue;

                    Composite(accumulators, row + x, LinearPremultiplied(color, alpha));
                }
            }
        }

        return CreateRaster(layout, accumulators);
    }

    internal static MixingBrushBoundaryEdge[] ExtractBoundaryEdges(
        IReadOnlyList<int> triangleIndices)
    {
        if (triangleIndices is null || triangleIndices.Count < 3) return [];

        var edges = new Dictionary<(int Low, int High), (int Count, MixingBrushBoundaryEdge Edge)>();
        for (var offset = 0; offset + 2 < triangleIndices.Count; offset += 3)
        {
            AddEdge(triangleIndices[offset], triangleIndices[offset + 1]);
            AddEdge(triangleIndices[offset + 1], triangleIndices[offset + 2]);
            AddEdge(triangleIndices[offset + 2], triangleIndices[offset]);
        }
        return edges.Values
            .Where(value => value.Count == 1)
            .Select(value => value.Edge)
            .ToArray();

        void AddEdge(int start, int end)
        {
            if (start < 0 || end < 0 || start == end) return;
            var key = start < end ? (start, end) : (end, start);
            if (edges.TryGetValue(key, out var current))
            {
                edges[key] = (current.Count + 1, current.Edge);
            }
            else
            {
                edges[key] = (1, new MixingBrushBoundaryEdge(start, end));
            }
        }
    }

    private static void RasterizeTriangle(
        Vector4[] accumulators,
        RasterLayout layout,
        MixingBrushRasterVertex first,
        MixingBrushRasterVertex second,
        MixingBrushRasterVertex third,
        double area)
    {
        var bounds = RectangleF.FromLTRB(
            Math.Min(first.Point.X, Math.Min(second.Point.X, third.Point.X)),
            Math.Min(first.Point.Y, Math.Min(second.Point.Y, third.Point.Y)),
            Math.Max(first.Point.X, Math.Max(second.Point.X, third.Point.X)),
            Math.Max(first.Point.Y, Math.Max(second.Point.Y, third.Point.Y)));
        PixelRange(bounds, layout, out var left, out var top, out var right, out var bottom);

        var firstColor = LinearPremultiplied(first.Argb);
        var secondColor = LinearPremultiplied(second.Argb);
        var thirdColor = LinearPremultiplied(third.Argb);
        var inverseArea = 1d / area;
        var localPixelStep = 1f / layout.Scale;
        var localLeft = layout.Bounds.Left + (left + 0.5f) / layout.Scale;

        var firstStepX = -((double)third.Point.Y - second.Point.Y) * localPixelStep;
        var secondStepX = -((double)first.Point.Y - third.Point.Y) * localPixelStep;
        var thirdStepX = -((double)second.Point.Y - first.Point.Y) * localPixelStep;
        var firstTopLeft = IsTopLeft(second.Point, third.Point);
        var secondTopLeft = IsTopLeft(third.Point, first.Point);
        var thirdTopLeft = IsTopLeft(first.Point, second.Point);
        for (var y = top; y <= bottom; y++)
        {
            var localY = layout.Bounds.Top + (y + 0.5f) / layout.Scale;
            var row = y * layout.PixelWidth;
            var rowStart = new PointF(localLeft, localY);
            var firstEdge = Edge(second.Point, third.Point, rowStart);
            var secondEdge = Edge(third.Point, first.Point, rowStart);
            var thirdEdge = Edge(first.Point, second.Point, rowStart);
            for (var x = left; x <= right; x++)
            {
                if (InsideEdge(firstEdge, firstTopLeft)
                    && InsideEdge(secondEdge, secondTopLeft)
                    && InsideEdge(thirdEdge, thirdTopLeft))
                {
                    var firstWeight = (float)(firstEdge * inverseArea);
                    var secondWeight = (float)(secondEdge * inverseArea);
                    var thirdWeight = (float)(thirdEdge * inverseArea);
                    var source = firstColor * firstWeight
                        + secondColor * secondWeight
                        + thirdColor * thirdWeight;
                    source = Vector4.Clamp(source, Vector4.Zero, Vector4.One);
                    if (source.W > 0) Composite(accumulators, row + x, source);
                }

                firstEdge += firstStepX;
                secondEdge += secondStepX;
                thirdEdge += thirdStepX;
            }
        }
    }

    private static bool TryCreateLayout(
        RectangleF geometryBounds,
        float requestedPixelsPerLocalUnit,
        out RasterLayout layout)
    {
        layout = default;
        if (!IsFinite(geometryBounds)
            || geometryBounds.Width <= 0
            || geometryBounds.Height <= 0)
        {
            return false;
        }

        var scale = float.IsFinite(requestedPixelsPerLocalUnit)
            ? Math.Max(0.000001f, requestedPixelsPerLocalUnit)
            : 1f;
        scale = Math.Min(scale, (MaximumRasterDimension - 2f) / geometryBounds.Width);
        scale = Math.Min(scale, (MaximumRasterDimension - 2f) / geometryBounds.Height);
        scale = Math.Min(scale, MathF.Sqrt(
            (MaximumRasterPixels - 4f)
            / Math.Max(float.Epsilon, geometryBounds.Width * geometryBounds.Height)));
        scale = Math.Max(0.000001f, scale);

        var pixelWidth = 0;
        var pixelHeight = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            pixelWidth = Math.Max(3, (int)MathF.Ceiling(geometryBounds.Width * scale) + 2);
            pixelHeight = Math.Max(3, (int)MathF.Ceiling(geometryBounds.Height * scale) + 2);
            if (pixelWidth <= MaximumRasterDimension
                && pixelHeight <= MaximumRasterDimension
                && (long)pixelWidth * pixelHeight <= MaximumRasterPixels)
            {
                break;
            }

            var dimensionFactor = Math.Min(
                (MaximumRasterDimension - 1f) / pixelWidth,
                (MaximumRasterDimension - 1f) / pixelHeight);
            var pixelFactor = MathF.Sqrt(MaximumRasterPixels / (float)((long)pixelWidth * pixelHeight));
            scale *= Math.Min(dimensionFactor, pixelFactor) * 0.995f;
        }

        pixelWidth = Math.Max(3, (int)MathF.Ceiling(geometryBounds.Width * scale) + 2);
        pixelHeight = Math.Max(3, (int)MathF.Ceiling(geometryBounds.Height * scale) + 2);
        if (pixelWidth > MaximumRasterDimension
            || pixelHeight > MaximumRasterDimension
            || (long)pixelWidth * pixelHeight > MaximumRasterPixels)
        {
            return false;
        }

        var rasterWidth = pixelWidth / scale;
        var rasterHeight = pixelHeight / scale;
        var centerX = geometryBounds.Left + geometryBounds.Width * 0.5f;
        var centerY = geometryBounds.Top + geometryBounds.Height * 0.5f;
        layout = new RasterLayout(
            new RectangleF(
                centerX - rasterWidth * 0.5f,
                centerY - rasterHeight * 0.5f,
                rasterWidth,
                rasterHeight),
            pixelWidth,
            pixelHeight,
            scale);
        return true;
    }

    private static MixingBrushRegionRaster CreateRaster(
        RasterLayout layout,
        Vector4[] accumulators)
    {
        var pixels = new int[accumulators.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = ToPremultipliedSrgb(accumulators[index]);
        }
        return new MixingBrushRegionRaster(
            layout.Bounds,
            layout.PixelWidth,
            layout.PixelHeight,
            layout.Scale,
            pixels);
    }

    private static bool TryGetVertexBounds(
        IReadOnlyList<MixingBrushRasterVertex> vertices,
        out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var initialized = false;
        var left = 0f;
        var top = 0f;
        var right = 0f;
        var bottom = 0f;
        foreach (var vertex in vertices)
        {
            if (!IsFinite(vertex.Point)) return false;
            if (!initialized)
            {
                left = right = vertex.Point.X;
                top = bottom = vertex.Point.Y;
                initialized = true;
                continue;
            }
            left = Math.Min(left, vertex.Point.X);
            top = Math.Min(top, vertex.Point.Y);
            right = Math.Max(right, vertex.Point.X);
            bottom = Math.Max(bottom, vertex.Point.Y);
        }
        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        return initialized;
    }

    private static bool TryGetCellBounds(
        IReadOnlyList<MixingStrokeCoverageCell> cells,
        out RectangleF bounds)
    {
        bounds = RectangleF.Empty;
        var initialized = false;
        foreach (var cell in cells)
        {
            if (!IsFinite(cell.Bounds) || cell.Bounds.Width <= 0 || cell.Bounds.Height <= 0) continue;
            bounds = initialized ? RectangleF.Union(bounds, cell.Bounds) : cell.Bounds;
            initialized = true;
        }
        return initialized;
    }

    private static void PixelRange(
        RectangleF localBounds,
        RasterLayout layout,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = Math.Clamp(
            (int)MathF.Floor((localBounds.Left - layout.Bounds.Left) * layout.Scale - 0.5f),
            0,
            layout.PixelWidth - 1);
        top = Math.Clamp(
            (int)MathF.Floor((localBounds.Top - layout.Bounds.Top) * layout.Scale - 0.5f),
            0,
            layout.PixelHeight - 1);
        right = Math.Clamp(
            (int)MathF.Ceiling((localBounds.Right - layout.Bounds.Left) * layout.Scale - 0.5f),
            0,
            layout.PixelWidth - 1);
        bottom = Math.Clamp(
            (int)MathF.Ceiling((localBounds.Bottom - layout.Bounds.Top) * layout.Scale - 0.5f),
            0,
            layout.PixelHeight - 1);
    }

    private static bool IsOwnedByNextCell(MixingStrokeCoverageCell cell, PointF point)
    {
        if (cell.Kind is MixingStrokeCoverageCellKind.Circle or MixingStrokeCoverageCellKind.End)
        {
            return false;
        }

        var midpoint = Midpoint(cell.NextLeft, cell.NextRight);
        var directionX = midpoint.X - cell.Center.X;
        var directionY = midpoint.Y - cell.Center.Y;
        if (directionX * directionX + directionY * directionY <= 0.000001f)
        {
            directionX = cell.Tangent.X;
            directionY = cell.Tangent.Y;
        }
        return (point.X - midpoint.X) * directionX + (point.Y - midpoint.Y) * directionY >= 0;
    }

    private static float NormalizedDistance(MixingStrokeCoverageCell cell, PointF point)
    {
        if (cell.Kind == MixingStrokeCoverageCellKind.Circle)
        {
            return Distance(point, cell.Center) / Math.Max(0.000001f, cell.Radius);
        }

        var tangentDistance = (point.X - cell.Center.X) * cell.Tangent.X
            + (point.Y - cell.Center.Y) * cell.Tangent.Y;
        if (cell.Kind == MixingStrokeCoverageCellKind.Start && tangentDistance <= 0
            || cell.Kind == MixingStrokeCoverageCellKind.End && tangentDistance >= 0)
        {
            return Distance(point, cell.Center) / Math.Max(0.000001f, cell.Radius);
        }

        var distance = float.PositiveInfinity;
        if (cell.Kind != MixingStrokeCoverageCellKind.Start)
        {
            var previousCenter = Midpoint(cell.PreviousLeft, cell.PreviousRight);
            var previousRadius = Distance(cell.PreviousLeft, cell.PreviousRight) * 0.5f;
            distance = NormalizedDistanceToTaperedSegment(
                point,
                previousCenter,
                cell.Center,
                previousRadius,
                cell.Radius);
        }
        if (cell.Kind != MixingStrokeCoverageCellKind.End)
        {
            var nextCenter = Midpoint(cell.NextLeft, cell.NextRight);
            var nextRadius = Distance(cell.NextLeft, cell.NextRight) * 0.5f;
            distance = Math.Min(
                distance,
                NormalizedDistanceToTaperedSegment(
                    point,
                    cell.Center,
                    nextCenter,
                    cell.Radius,
                    nextRadius));
        }
        return distance;
    }

    private static float NormalizedDistanceToTaperedSegment(
        PointF point,
        PointF start,
        PointF end,
        float startRadius,
        float endRadius)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        var amount = lengthSquared <= 0.000001f
            ? 0f
            : Math.Clamp(
                ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
                0f,
                1f);
        var nearest = new PointF(start.X + dx * amount, start.Y + dy * amount);
        var radius = startRadius + (endRadius - startRadius) * amount;
        return Distance(point, nearest) / Math.Max(0.000001f, radius);
    }

    private static void Composite(Vector4[] accumulators, int index, Vector4 source)
    {
        var destination = accumulators[index];
        var inverseAlpha = 1f - source.W;
        accumulators[index] = new Vector4(
            source.X + destination.X * inverseAlpha,
            source.Y + destination.Y * inverseAlpha,
            source.Z + destination.Z * inverseAlpha,
            source.W + destination.W * inverseAlpha);
    }

    private static Vector4 LinearPremultiplied(Color color) =>
        LinearPremultiplied(color, color.A / 255f);

    private static Vector4 LinearPremultiplied(int argb)
    {
        var packed = (uint)argb;
        var alpha = (packed >> 24) / 255f;
        return new Vector4(
            SrgbLinearTable[(packed >> 16) & 0xff] * alpha,
            SrgbLinearTable[(packed >> 8) & 0xff] * alpha,
            SrgbLinearTable[packed & 0xff] * alpha,
            alpha);
    }

    private static Vector4 LinearPremultiplied(Color color, float alpha) => new(
        SrgbLinearTable[color.R] * alpha,
        SrgbLinearTable[color.G] * alpha,
        SrgbLinearTable[color.B] * alpha,
        alpha);

    private static int ToPremultipliedSrgb(Vector4 color)
    {
        var alpha = Math.Clamp(color.W, 0f, 1f);
        var alphaByte = ToByte(alpha);
        if (alphaByte == 0) return 0;
        var inverseAlpha = 1f / Math.Max(0.000001f, alpha);
        var red = ToByte(LinearToSrgb(color.X * inverseAlpha) * alpha);
        var green = ToByte(LinearToSrgb(color.Y * inverseAlpha) * alpha);
        var blue = ToByte(LinearToSrgb(color.Z * inverseAlpha) * alpha);
        return (alphaByte << 24) | (red << 16) | (green << 8) | blue;
    }

    private static bool InsideEdge(double value, bool isTopLeft) =>
        value > GeometryEpsilon
        || value >= -GeometryEpsilon && isTopLeft;

    private static bool IsTopLeft(PointF start, PointF end) =>
        start.Y < end.Y
        || Math.Abs(start.Y - end.Y) <= GeometryEpsilon && start.X > end.X;

    private static double Edge(PointF start, PointF end, PointF point) =>
        ((double)end.X - start.X) * (point.Y - start.Y)
        - ((double)end.Y - start.Y) * (point.X - start.X);

    private static PointF Midpoint(PointF first, PointF second) => new(
        (first.X + second.X) * 0.5f,
        (first.Y + second.Y) * 0.5f);

    private static float Distance(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsFinite(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsFinite(RectangleF bounds) =>
        float.IsFinite(bounds.X)
        && float.IsFinite(bounds.Y)
        && float.IsFinite(bounds.Width)
        && float.IsFinite(bounds.Height);

    private static float[] BuildSrgbLinearTable()
    {
        var result = new float[256];
        for (var index = 0; index < result.Length; index++)
        {
            var value = index / 255f;
            result[index] = value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        }
        return result;
    }

    private static float[] BuildLinearSrgbTable()
    {
        var result = new float[LinearSrgbTableResolution + 1];
        for (var index = 0; index < result.Length; index++)
        {
            var value = index / (float)LinearSrgbTableResolution;
            result[index] = value <= 0.0031308f
                ? value * 12.92f
                : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        }
        return result;
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        var position = value * LinearSrgbTableResolution;
        var lower = Math.Min((int)position, LinearSrgbTableResolution - 1);
        var amount = position - lower;
        return LinearSrgbTable[lower]
            + (LinearSrgbTable[lower + 1] - LinearSrgbTable[lower]) * amount;
    }

    private static int ToByte(float value) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
