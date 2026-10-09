namespace VectorAnimationEngine;

/// <summary>A view-independent image and the world rectangle occupied by its pixels.</summary>
internal sealed record BitmapDistortRaster(BitmapImageRaster Raster, RectangleF WorldBounds);

/// <summary>
/// Bakes a placed bitmap's warp stack at the original image's pixel density. Camera,
/// viewport, object opacity and target clipping are applied later by the renderer. The
/// incoming source must already have had its BitmapObjectData clip applied.
/// </summary>
internal static class BitmapDistortRasterizer
{
    private const double MaximumMeshErrorPixels = 0.25;
    private const int MaximumMeshVertices = 1_100_000;
    private const int MaximumRasterDimension = 32_768;
    private const int StripRows = 32;
    private const int CoverageScale = 2;
    private const long SubpixelScale = 256;

    // The center and both triangle interiors supplement the four edge midpoints.
    // A regular mesh keeps shared edges identical when a curved cell is subdivided.
    private static readonly PointF[] ErrorSamples =
    [
        new(0.5f, 0), new(1, 0.5f), new(0.5f, 1), new(0, 0.5f),
        new(0.5f, 0.5f), new(1f / 3f, 1f / 3f), new(2f / 3f, 2f / 3f),
        new(0.125f, 0.125f), new(0.125f, 0.375f), new(0.125f, 0.625f), new(0.125f, 0.875f),
        new(0.375f, 0.125f), new(0.375f, 0.375f), new(0.375f, 0.625f), new(0.375f, 0.875f),
        new(0.625f, 0.125f), new(0.625f, 0.375f), new(0.625f, 0.625f), new(0.625f, 0.875f),
        new(0.875f, 0.125f), new(0.875f, 0.375f), new(0.875f, 0.625f), new(0.875f, 0.875f)
    ];

    internal static BitmapDistortRaster? Rasterize(
        BitmapImageRaster source,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<DistortWarp> distortions,
        BitmapSampling sampling,
        long maximumBytes)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || maximumBytes <= 0
            || objectIndex < 0 || objectIndex >= scene.ObjectCount
            || !Enum.IsDefined(sampling)) return null;

        var width = scene.Width[objectIndex];
        var height = scene.Height[objectIndex];
        var centerX = scene.X[objectIndex];
        var centerY = scene.Y[objectIndex];
        var angle = scene.Angle[objectIndex];
        if (!float.IsFinite(width) || !float.IsFinite(height)
            || !float.IsFinite(centerX) || !float.IsFinite(centerY) || !float.IsFinite(angle)
            || width == 0 || height == 0) return null;

        var density = Math.Max(source.PixelWidth / Math.Abs((double)width),
            source.PixelHeight / Math.Abs((double)height));
        if (!double.IsFinite(density) || density <= 0) return null;

        try
        {
            var placement = new Placement(centerX, centerY, width, height,
                Math.Cos(angle), Math.Sin(angle), distortions);
            var mesh = BuildMesh(placement, density, source.PixelWidth, source.PixelHeight, maximumBytes);
            if (mesh is null) return null;

            // Two native pixels protect curved-edge coverage and later image filtering.
            var left = mesh.Left - 2 / density;
            var top = mesh.Top - 2 / density;
            var pixelWidthValue = Math.Ceiling((mesh.Right - mesh.Left) * density) + 4;
            var pixelHeightValue = Math.Ceiling((mesh.Bottom - mesh.Top) * density) + 4;
            if (!double.IsFinite(left) || !double.IsFinite(top)
                || !double.IsFinite(pixelWidthValue) || !double.IsFinite(pixelHeightValue)
                || pixelWidthValue < 1 || pixelHeightValue < 1
                || pixelWidthValue > MaximumRasterDimension || pixelHeightValue > MaximumRasterDimension)
                return null;

            var pixelWidth = checked((int)pixelWidthValue);
            var pixelHeight = checked((int)pixelHeightValue);
            var resultBytes = checked((long)pixelWidth * pixelHeight * 4);
            if (resultBytes > maximumBytes || resultBytes > int.MaxValue) return null;

            var worldBounds = new RectangleF((float)left, (float)top,
                (float)(pixelWidth / density), (float)(pixelHeight / density));
            if (!float.IsFinite(worldBounds.X) || !float.IsFinite(worldBounds.Y)
                || !float.IsFinite(worldBounds.Width) || !float.IsFinite(worldBounds.Height)
                || worldBounds.Width <= 0 || worldBounds.Height <= 0) return null;

            var vertices = new RasterVertex[mesh.Points.Length];
            for (var index = 0; index < vertices.Length; index++)
            {
                var point = mesh.Points[index];
                vertices[index] = new RasterVertex(
                    checked((long)Math.Round((point.X - left) * density * CoverageScale * SubpixelScale)),
                    checked((long)Math.Round((point.Y - top) * density * CoverageScale * SubpixelScale)));
            }

            // Store cell indices, retaining source row/column order in every strip. The
            // order matters for genuine folds, where distinct source regions overlap.
            var bins = BuildStripBins(vertices, mesh.Columns, mesh.Rows, pixelHeight, maximumBytes);
            if (bins is null) return null;
            var raster = new BitmapImageRaster(new BitmapImageRasterKey(
                $"{source.Key.ContentSha256}:distort-native-v1:{Guid.NewGuid():N}", pixelWidth, pixelHeight));
            var stripStride = checked(pixelWidth * CoverageScale * 4);
            var stripPixels = new byte[checked(stripStride * Math.Min(pixelHeight, StripRows) * CoverageScale)];

            for (var strip = 0; strip < bins.Length; strip++)
            {
                var firstRow = strip * StripRows;
                var rows = Math.Min(StripRows, pixelHeight - firstRow);
                Array.Clear(stripPixels);
                foreach (var cell in bins[strip])
                {
                    var row = cell / mesh.Columns;
                    var column = cell % mesh.Columns;
                    var topLeft = row * (mesh.Columns + 1) + column;
                    var bottomLeft = topLeft + mesh.Columns + 1;
                    var x0 = column * (double)source.PixelWidth / mesh.Columns;
                    var x1 = (column + 1) * (double)source.PixelWidth / mesh.Columns;
                    var y0 = row * (double)source.PixelHeight / mesh.Rows;
                    var y1 = (row + 1) * (double)source.PixelHeight / mesh.Rows;
                    DrawTriangle(source, sampling, stripPixels, stripStride, pixelWidth,
                        firstRow, rows, vertices[topLeft], vertices[topLeft + 1], vertices[bottomLeft],
                        new TexturePoint(x0, y0), new TexturePoint(x1, y0), new TexturePoint(x0, y1));
                    DrawTriangle(source, sampling, stripPixels, stripStride, pixelWidth,
                        firstRow, rows, vertices[topLeft + 1], vertices[bottomLeft + 1], vertices[bottomLeft],
                        new TexturePoint(x1, y0), new TexturePoint(x1, y1), new TexturePoint(x0, y1));
                }

                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < pixelWidth; column++)
                    {
                        var input = row * CoverageScale * stripStride + column * CoverageScale * 4;
                        var output = (firstRow + row) * raster.Stride + column * 4;
                        for (var channel = 0; channel < 4; channel++)
                            raster.Pixels[output + channel] = (byte)((stripPixels[input + channel]
                                + stripPixels[input + 4 + channel]
                                + stripPixels[input + stripStride + channel]
                                + stripPixels[input + stripStride + 4 + channel] + 2) / 4);
                    }
                }
            }

            return new BitmapDistortRaster(raster, worldBounds);
        }
        catch (Exception exception) when (exception is ArithmeticException or OutOfMemoryException)
        {
            // Keep the normal renderer available; never reduce native pixel density.
            return null;
        }
    }

    private static Mesh? BuildMesh(Placement placement, double density,
        int sourceWidth, int sourceHeight, long maximumBytes)
    {
        var columns = Math.Clamp((sourceWidth + 31) / 32, 8, 128);
        var rows = Math.Clamp((sourceHeight + 31) / 32, 8, 128);
        while (true)
        {
            var vertexCount = checked((long)(columns + 1) * (rows + 1));
            if (vertexCount > MaximumMeshVertices || vertexCount * 8 > maximumBytes)
                return null;
            var points = new PointF[checked((int)vertexCount)];
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            for (var row = 0; row <= rows; row++)
            {
                for (var column = 0; column <= columns; column++)
                {
                    var point = placement.Map(column / (double)columns, row / (double)rows);
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return null;
                    points[row * (columns + 1) + column] = point;
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
            }
            var mesh = new Mesh(points, columns, rows, left, top, right, bottom);
            if (MeshMeetsTolerance(mesh, placement, density)) return mesh;
            columns = checked(columns * 2);
            rows = checked(rows * 2);
        }
    }

    private static bool MeshMeetsTolerance(Mesh mesh, Placement placement, double density)
    {
        var toleranceSquared = Math.Pow(MaximumMeshErrorPixels / density, 2);
        for (var row = 0; row < mesh.Rows; row++)
        {
            for (var column = 0; column < mesh.Columns; column++)
            {
                var index = row * (mesh.Columns + 1) + column;
                var p00 = mesh.Points[index];
                var p10 = mesh.Points[index + 1];
                var p01 = mesh.Points[index + mesh.Columns + 1];
                var p11 = mesh.Points[index + mesh.Columns + 2];
                foreach (var sample in ErrorSamples)
                {
                    var actual = placement.Map((column + (double)sample.X) / mesh.Columns,
                        (row + (double)sample.Y) / mesh.Rows);
                    if (!float.IsFinite(actual.X) || !float.IsFinite(actual.Y)) return false;
                    double x, y;
                    if (sample.X + sample.Y <= 1)
                    {
                        x = p00.X * (1d - sample.X - sample.Y) + p10.X * (double)sample.X + p01.X * (double)sample.Y;
                        y = p00.Y * (1d - sample.X - sample.Y) + p10.Y * (double)sample.X + p01.Y * (double)sample.Y;
                    }
                    else
                    {
                        x = p10.X * (1d - sample.Y) + p11.X * (sample.X + (double)sample.Y - 1) + p01.X * (1d - sample.X);
                        y = p10.Y * (1d - sample.Y) + p11.Y * (sample.X + (double)sample.Y - 1) + p01.Y * (1d - sample.X);
                    }
                    var dx = actual.X - x;
                    var dy = actual.Y - y;
                    if (dx * dx + dy * dy > toleranceSquared) return false;
                }
            }
        }
        return true;
    }

    private static List<int>[]? BuildStripBins(RasterVertex[] vertices,
        int columns, int rows, int pixelHeight, long maximumBytes)
    {
        var bins = Enumerable.Range(0, (pixelHeight + StripRows - 1) / StripRows)
            .Select(_ => new List<int>()).ToArray();
        long entries = 0;
        var maximumEntries = maximumBytes / 8;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * (columns + 1) + column;
                var first = vertices[index];
                var second = vertices[index + 1];
                var third = vertices[index + columns + 1];
                var fourth = vertices[index + columns + 2];
                var minY = Math.Min(Math.Min(first.Y, second.Y), Math.Min(third.Y, fourth.Y));
                var maxY = Math.Max(Math.Max(first.Y, second.Y), Math.Max(third.Y, fourth.Y));
                var firstStrip = Math.Clamp((int)Math.Floor(minY / (double)(CoverageScale * SubpixelScale * StripRows)), 0, bins.Length - 1);
                var lastStrip = Math.Clamp((int)Math.Floor(maxY / (double)(CoverageScale * SubpixelScale * StripRows)), 0, bins.Length - 1);
                entries += lastStrip - firstStrip + 1;
                if (entries > maximumEntries) return null;
                for (var strip = firstStrip; strip <= lastStrip; strip++)
                    bins[strip].Add(row * columns + column);
            }
        }
        return bins;
    }

    private static void DrawTriangle(BitmapImageRaster source, BitmapSampling sampling,
        byte[] pixels, int stride, int pixelWidth, int firstRow, int rows,
        RasterVertex a, RasterVertex b, RasterVertex c, TexturePoint ta, TexturePoint tb, TexturePoint tc)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (area == 0) return;
        // Ownership follows source edges, even if a fold reverses destination
        // winding. A shared edge then cannot composite its alpha twice.
        var edgeAInclusive = IsTopLeft(tb, tc);
        var edgeBInclusive = IsTopLeft(tc, ta);
        var edgeCInclusive = IsTopLeft(ta, tb);
        if (area < 0)
        {
            (b, c) = (c, b);
            (tb, tc) = (tc, tb);
            (edgeBInclusive, edgeCInclusive) = (edgeCInclusive, edgeBInclusive);
            area = -area;
        }
        var left = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / (double)SubpixelScale));
        var right = Math.Min(pixelWidth * CoverageScale,
            (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)) / (double)SubpixelScale));
        var top = Math.Max(firstRow * CoverageScale,
            (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) / (double)SubpixelScale));
        var bottom = Math.Min((firstRow + rows) * CoverageScale,
            (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)) / (double)SubpixelScale));
        for (var y = top; y < bottom; y++)
        {
            var sampleY = y * SubpixelScale + SubpixelScale / 2;
            for (var x = left; x < right; x++)
            {
                var sampleX = x * SubpixelScale + SubpixelScale / 2;
                var wa = Edge(b, c, sampleX, sampleY);
                var wb = Edge(c, a, sampleX, sampleY);
                var wc = Edge(a, b, sampleX, sampleY);
                if (wa < 0 || wa == 0 && !edgeAInclusive
                    || wb < 0 || wb == 0 && !edgeBInclusive
                    || wc < 0 || wc == 0 && !edgeCInclusive) continue;

                // Coverage is supersampled; texture is sampled at the final pixel
                // center so an identity warp does not blur a Linear-filtered image.
                var textureX = (x / CoverageScale * CoverageScale + CoverageScale / 2) * SubpixelScale;
                var textureY = (y / CoverageScale * CoverageScale + CoverageScale / 2) * SubpixelScale;
                var textureWa = Edge(b, c, textureX, textureY) / (double)area;
                var textureWb = Edge(c, a, textureX, textureY) / (double)area;
                var textureWc = 1 - textureWa - textureWb;
                var u = ta.X * textureWa + tb.X * textureWb + tc.X * textureWc;
                var v = ta.Y * textureWa + tb.Y * textureWb + tc.Y * textureWc;
                var value = Sample(source, sampling, u, v);
                var offset = (y - firstRow * CoverageScale) * stride + x * 4;
                SourceOver(pixels, offset, value);
            }
        }
    }

    private static uint Sample(BitmapImageRaster source, BitmapSampling sampling, double x, double y)
    {
        if (sampling == BitmapSampling.Point)
        {
            var column = (int)Math.Floor(Math.Clamp(x, 0, source.PixelWidth - 1));
            var row = (int)Math.Floor(Math.Clamp(y, 0, source.PixelHeight - 1));
            return ReadPixel(source, column, row);
        }
        x = Math.Clamp(x - 0.5, 0, source.PixelWidth - 1);
        y = Math.Clamp(y - 0.5, 0, source.PixelHeight - 1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, source.PixelWidth - 1);
        var y1 = Math.Min(y0 + 1, source.PixelHeight - 1);
        var fx = x - x0;
        var fy = y - y0;
        var p00 = ReadPixel(source, x0, y0);
        var p10 = ReadPixel(source, x1, y0);
        var p01 = ReadPixel(source, x0, y1);
        var p11 = ReadPixel(source, x1, y1);
        uint result = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            var shift = channel * 8;
            var upper = ((p00 >> shift) & 255) * (1 - fx) + ((p10 >> shift) & 255) * fx;
            var lower = ((p01 >> shift) & 255) * (1 - fx) + ((p11 >> shift) & 255) * fx;
            result |= (uint)Math.Clamp((int)Math.Round(upper * (1 - fy) + lower * fy), 0, 255) << shift;
        }
        return result;
    }

    private static uint ReadPixel(BitmapImageRaster source, int column, int row)
    {
        var offset = row * source.Stride + column * 4;
        return source.Pixels[offset] | (uint)source.Pixels[offset + 1] << 8
            | (uint)source.Pixels[offset + 2] << 16 | (uint)source.Pixels[offset + 3] << 24;
    }

    private static void SourceOver(byte[] pixels, int offset, uint value)
    {
        var alpha = (int)(value >> 24);
        if (alpha == 0) return;
        var inverseAlpha = 255 - alpha;
        for (var channel = 0; channel < 4; channel++)
            pixels[offset + channel] = (byte)Math.Min(255,
                ((value >> (channel * 8)) & 255)
                + (pixels[offset + channel] * inverseAlpha + 127) / 255);
    }

    private static long Edge(RasterVertex a, RasterVertex b, long x, long y) =>
        (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private static bool IsTopLeft(TexturePoint a, TexturePoint b) =>
        b.Y < a.Y || b.Y == a.Y && b.X > a.X;

    private readonly record struct RasterVertex(long X, long Y);
    private readonly record struct TexturePoint(double X, double Y);
    private sealed record Mesh(PointF[] Points, int Columns, int Rows,
        double Left, double Top, double Right, double Bottom);

    private sealed record Placement(double X, double Y, double Width, double Height,
        double Cos, double Sin, IReadOnlyList<DistortWarp> Distortions)
    {
        internal PointF Map(double u, double v)
        {
            var localX = (u - 0.5) * Width;
            var localY = (v - 0.5) * Height;
            var point = new PointF((float)(X + localX * Cos - localY * Sin),
                (float)(Y + localX * Sin + localY * Cos));
            foreach (var distortion in Distortions) point = distortion.Map(point);
            return point;
        }
    }
}
