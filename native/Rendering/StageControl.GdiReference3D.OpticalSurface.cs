using System.Buffers;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const int MaximumReference3DOpticalSurfacePixels = 8_388_608;
    private const int MaximumReference3DOpticalLightingTilePixels = 65_536;
    private const int MaximumReference3DOpticalRasterLod = 2;
    private const int MaximumReference3DGdiLocalLightBrushProfiles = 256;
    private Reference3DOpticalRasterLayout? _reference3DGdiRasterLayout;
    private readonly Dictionary<Reference3DGdiLocalLightBrushKey, PathGradientBrush>
        _reference3DGdiLocalLightBrushes = new(Reference3DGdiLocalLightBrushKeyComparer.Instance);

    private readonly record struct Reference3DGdiLocalLightBrushKey(
        GradientStop[] Stops,
        int MaterialOpacityBits);

    private sealed class Reference3DGradientStopProfileComparer : IEqualityComparer<GradientStop[]>
    {
        public static Reference3DGradientStopProfileComparer Instance { get; } = new();

        public bool Equals(GradientStop[]? left, GradientStop[]? right) =>
            ReferenceEquals(left, right)
            || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(GradientStop[] stops)
        {
            var hash = new HashCode();
            foreach (var stop in stops) hash.Add(stop);
            return hash.ToHashCode();
        }
    }

    private sealed class Reference3DGdiLocalLightBrushKeyComparer
        : IEqualityComparer<Reference3DGdiLocalLightBrushKey>
    {
        public static Reference3DGdiLocalLightBrushKeyComparer Instance { get; } = new();

        public bool Equals(
            Reference3DGdiLocalLightBrushKey left,
            Reference3DGdiLocalLightBrushKey right) =>
            left.MaterialOpacityBits == right.MaterialOpacityBits
            && Reference3DGradientStopProfileComparer.Instance.Equals(left.Stops, right.Stops);

        public int GetHashCode(Reference3DGdiLocalLightBrushKey key)
        {
            var hash = new HashCode();
            hash.Add(key.MaterialOpacityBits);
            foreach (var stop in key.Stops) hash.Add(stop);
            return hash.ToHashCode();
        }
    }

    internal int Reference3DOpticalSurfaceBuildFailureCountdownForTesting { get; set; }

    internal readonly record struct Reference3DOpticalRasterLayout(
        Rectangle Bounds,
        int PixelWidth,
        int PixelHeight)
    {
        public long PixelCount => (long)PixelWidth * PixelHeight;

        public float ScaleX => PixelWidth / (float)Bounds.Width;

        public float ScaleY => PixelHeight / (float)Bounds.Height;

        public float ScreenUnitsPerPixelX => Bounds.Width / (float)PixelWidth;

        public float ScreenUnitsPerPixelY => Bounds.Height / (float)PixelHeight;
    }

    private struct Reference3DLinearLightingPixel
    {
        public Vector3 Diffuse;
        public Vector3 Specular;
        public Vector3 Fresnel;
    }

    private readonly record struct Reference3DPreparedLinearLightLayer(
        Reference3DProjectedContour[] Contours,
        Matrix3x2 InverseGradientTransform,
        Reference3DLinearLightStop[] LinearStops);

    private Reference3DOpticalSurface? BuildReference3DOpticalSurface(
        Reference3DRenderItem item,
        SpatialOpticalMaterial material,
        Reference3DOpticalRasterLayout layout)
    {
        Bitmap? bitmap = null;
        try
        {
            if (layout.PixelCount <= 0
                || layout.PixelCount > MaximumReference3DOpticalSurfacePixels)
            {
                return null;
            }
            if (ShouldFailReference3DOpticalSurfaceBuildForTesting()) return null;
            bitmap = new Bitmap(
                layout.PixelWidth,
                layout.PixelHeight,
                PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                var reduced = layout.PixelWidth < layout.Bounds.Width
                    || layout.PixelHeight < layout.Bounds.Height;
                graphics.CompositingQuality = reduced
                    ? CompositingQuality.HighSpeed
                    : CompositingQuality.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = reduced
                    ? PixelOffsetMode.Half
                    : PixelOffsetMode.HighQuality;
                graphics.InterpolationMode = reduced
                    ? InterpolationMode.Bilinear
                    : InterpolationMode.HighQualityBicubic;
                _reference3DGdiRasterLayout = layout;
                ResetReference3DGdiScreenTransform(graphics);
                try
                {
                    if (item.FragmentClip is { Length: > 0 } fragmentClip)
                    {
                        using var fragmentPath = CreateReference3DPath(fragmentClip, fillOnly: true);
                        graphics.SetClip(fragmentPath, CombineMode.Intersect);
                    }
                    DrawReference3DOpticalBaseMaterial(
                        graphics,
                        item with
                        {
                            MaterialOpacity = 1f,
                            OpticalSurface = null
                        });
                }
                finally
                {
                    _reference3DGdiRasterLayout = null;
                }
            }

            var pixels = ApplyReference3DLinearLighting(bitmap, layout, item, material);
            var surface = new Reference3DOpticalSurface(layout.Bounds, bitmap, pixels);
            bitmap = null;
            return surface;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or ExternalException
                                           or OutOfMemoryException
                                           or OverflowException)
        {
            return null;
        }
        finally
        {
            _reference3DGdiRasterLayout = null;
            bitmap?.Dispose();
        }
    }

    private bool ShouldFailReference3DOpticalSurfaceBuildForTesting()
    {
        if (Reference3DOpticalSurfaceBuildFailureCountdownForTesting < 0) return true;
        if (Reference3DOpticalSurfaceBuildFailureCountdownForTesting == 0) return false;
        Reference3DOpticalSurfaceBuildFailureCountdownForTesting--;
        return Reference3DOpticalSurfaceBuildFailureCountdownForTesting == 0;
    }

    internal bool TryGetReference3DOpticalSurfaceBounds(
        Reference3DRenderItem item,
        out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var surfaceContours = item.Kind is Reference3DRenderKind.FrontStroke
                or Reference3DRenderKind.IntersectionEdge
            ? item.OpticalSurfaceContours ?? item.Contours
            : item.Contours;
        if (!TryGetBounds(surfaceContours, out var surfaceBounds)) return false;
        if (item.FragmentClip is { Length: > 0 } fragmentClip)
        {
            if (!TryGetBounds(fragmentClip, out var fragmentBounds)) return false;
            surfaceBounds = RectangleF.Intersect(surfaceBounds, fragmentBounds);
            if (surfaceBounds.Width <= 0f || surfaceBounds.Height <= 0f) return false;
        }

        const int antialiasPadding = 2;
        var filterPadding = LayerFilterBounds.GetPadding(Scene, ActiveViewZoom);
        var viewport = Rectangle.FromLTRB(-filterPadding.Left, -filterPadding.Top,
            ClientSize.Width + filterPadding.Right, ClientSize.Height + filterPadding.Bottom);
        var requested = Rectangle.FromLTRB(
            (int)Math.Clamp(
                Math.Floor(surfaceBounds.Left) - antialiasPadding,
                viewport.Left,
                viewport.Right),
            (int)Math.Clamp(
                Math.Floor(surfaceBounds.Top) - antialiasPadding,
                viewport.Top,
                viewport.Bottom),
            (int)Math.Clamp(
                Math.Ceiling(surfaceBounds.Right) + antialiasPadding + 1d,
                viewport.Left,
                viewport.Right),
            (int)Math.Clamp(
                Math.Ceiling(surfaceBounds.Bottom) + antialiasPadding + 1d,
                viewport.Top,
                viewport.Bottom));
        bounds = Rectangle.Intersect(requested, viewport);
        return bounds.Width > 0 && bounds.Height > 0;

        static bool TryGetBounds(
            IReadOnlyList<Reference3DProjectedContour> contours,
            out RectangleF result)
        {
            result = RectangleF.Empty;
            var hasPoint = false;
            var left = float.PositiveInfinity;
            var top = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            var bottom = float.NegativeInfinity;
            foreach (var contour in contours)
            {
                foreach (var point in contour.Points)
                {
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
                    hasPoint = true;
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
            }
            if (!hasPoint || right <= left || bottom <= top) return false;
            result = RectangleF.FromLTRB(left, top, right, bottom);
            return true;
        }
    }

    private void DrawReference3DOpticalBaseMaterial(
        Graphics graphics,
        Reference3DRenderItem item)
    {
        if (item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
        {
            using var path = CreateReference3DPath(item.Contours, fillOnly: true);
            if (path.PointCount == 0) return;
            using var fill = new SolidBrush(GetReference3DExtrusionSurfaceColor(item));
            FillPathAntialiased(graphics, path, fill);
            return;
        }

        DrawReference3DObjectUnclipped(
            graphics,
            item,
            item.Kind == Reference3DRenderKind.FrontStroke
                ? SceneRenderPass.Stroke
                : SceneRenderPass.Fill);
    }

    private int[] ApplyReference3DLinearLighting(
        Bitmap bitmap,
        Reference3DOpticalRasterLayout layout,
        Reference3DRenderItem item,
        SpatialOpticalMaterial material)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixelCount = checked(width * height);
        var pixels = new int[pixelCount];
        var rectangle = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            CopyReference3DBitmapPixels(data, pixels, width, height, fromBitmap: true);
            var preparedLayers = PrepareReference3DLinearLightLayers(item.LocalLightLayers);
            var tileCapacity = Math.Min(pixelCount, MaximumReference3DOpticalLightingTilePixels);
            var lighting = ArrayPool<Reference3DLinearLightingPixel>.Shared.Rent(tileCapacity);
            try
            {
                var ambient = Reference3DFiniteOrZero(item.OpticalResponse.AmbientIrradiance);
                var normalizedMaterial = material.Normalize();
                var metallic = normalizedMaterial.Metallic;
                var dielectricF0 = Reference3DDielectricF0(normalizedMaterial);
                var intersections = new List<float>();
                for (var tileX = 0; tileX < width;)
                {
                    var tileWidth = Math.Min(width - tileX, MaximumReference3DOpticalLightingTilePixels);
                    var maximumTileRows = Math.Max(
                        1,
                        MaximumReference3DOpticalLightingTilePixels / tileWidth);
                    for (var tileY = 0; tileY < height; tileY += maximumTileRows)
                    {
                        var tileHeight = Math.Min(height - tileY, maximumTileRows);
                        var tilePixelCount = checked(tileWidth * tileHeight);
                        for (var index = 0; index < tilePixelCount; index++)
                        {
                            lighting[index].Diffuse = ambient;
                            lighting[index].Specular = Vector3.Zero;
                            lighting[index].Fresnel = Vector3.Zero;
                        }

                        foreach (var layer in preparedLayers)
                        {
                            AccumulateReference3DLinearLightLayer(
                                layer,
                                layout,
                                tileX,
                                tileY,
                                tileWidth,
                                tileHeight,
                                lighting,
                                pixels,
                                width,
                                intersections);
                        }

                        ParallelBatch.For(tilePixelCount, 4096, (_, start, end) =>
                        {
                            for (var index = start; index < end; index++)
                            {
                                var localY = index / tileWidth;
                                var localX = index - localY * tileWidth;
                                var pixelIndex = (tileY + localY) * width + tileX + localX;
                                pixels[pixelIndex] = ApplyReference3DLinearLightingToPremultipliedPixel(
                                    pixels[pixelIndex],
                                    lighting[index].Diffuse,
                                    lighting[index].Specular,
                                    metallic,
                                    lighting[index].Fresnel,
                                    dielectricF0);
                            }
                        });
                    }
                    tileX += tileWidth;
                }
            }
            finally
            {
                ArrayPool<Reference3DLinearLightingPixel>.Shared.Return(lighting);
            }
            CopyReference3DBitmapPixels(data, pixels, width, height, fromBitmap: false);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return pixels;
    }

    private static Reference3DPreparedLinearLightLayer[] PrepareReference3DLinearLightLayers(
        IReadOnlyList<Reference3DLocalLightLayer>? layers)
    {
        if (layers is not { Count: > 0 }) return [];
        var result = new List<Reference3DPreparedLinearLightLayer>(layers.Count);
        foreach (var layer in layers)
        {
            if (layer.LinearStops.Length == 0
                || !Matrix3x2.Invert(layer.GradientTransform, out var inverse))
            {
                continue;
            }
            var contours = layer.Contours
                .Where(contour => contour.Closed && contour.Points.Length >= 3)
                .ToArray();
            if (contours.Length == 0) continue;
            result.Add(new Reference3DPreparedLinearLightLayer(
                contours,
                inverse,
                layer.LinearStops));
        }
        return result.ToArray();
    }

    private static void AccumulateReference3DLinearLightLayer(
        Reference3DPreparedLinearLightLayer layer,
        Reference3DOpticalRasterLayout layout,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        Reference3DLinearLightingPixel[] lighting,
        IReadOnlyList<int> basePixels,
        int rasterWidth,
        List<float> intersections)
    {
        var screenUnitsX = layout.ScreenUnitsPerPixelX;
        var screenUnitsY = layout.ScreenUnitsPerPixelY;
        for (var localY = 0; localY < tileHeight; localY++)
        {
            var rasterY = tileY + localY;
            var screenY = layout.Bounds.Top + (rasterY + 0.5f) * screenUnitsY;
            intersections.Clear();
            foreach (var contour in layer.Contours)
            {
                var points = contour.Points;
                var previous = points[^1];
                foreach (var current in points)
                {
                    if ((previous.Y <= screenY && current.Y > screenY)
                        || (current.Y <= screenY && previous.Y > screenY))
                    {
                        var amount = (screenY - previous.Y) / (current.Y - previous.Y);
                        var x = previous.X + (current.X - previous.X) * amount;
                        if (float.IsFinite(x)) intersections.Add(x);
                    }
                    previous = current;
                }
            }
            if (intersections.Count < 2) continue;
            intersections.Sort();
            for (var span = 0; span + 1 < intersections.Count; span += 2)
            {
                var startX = Math.Clamp(
                    (int)MathF.Ceiling(
                        (intersections[span] - layout.Bounds.Left) / screenUnitsX - 0.5f),
                    tileX,
                    tileX + tileWidth);
                var endX = Math.Clamp(
                    (int)MathF.Ceiling(
                        (intersections[span + 1] - layout.Bounds.Left) / screenUnitsX - 0.5f),
                    startX,
                    tileX + tileWidth);
                var row = localY * tileWidth;
                for (var rasterX = startX; rasterX < endX; rasterX++)
                {
                    var pixelIndex = rasterY * rasterWidth + rasterX;
                    if ((basePixels[pixelIndex] >>> 24) == 0) continue;
                    var screen = new Vector2(
                        layout.Bounds.Left + (rasterX + 0.5f) * screenUnitsX,
                        screenY);
                    var gradientPoint = Vector2.Transform(screen, layer.InverseGradientTransform);
                    var position = Math.Clamp(gradientPoint.Length(), 0f, 1f);
                    var sample = SampleReference3DLinearLightStops(layer.LinearStops, position);
                    var index = row + rasterX - tileX;
                    lighting[index].Diffuse += sample.DiffuseIrradiance;
                    lighting[index].Specular += sample.SpecularRadiance;
                    lighting[index].Fresnel += sample.FresnelRadiance;
                }
            }
        }
    }

    private static Reference3DLinearLightStop SampleReference3DLinearLightStops(
        IReadOnlyList<Reference3DLinearLightStop> stops,
        float position)
    {
        if (stops.Count == 0) return default;
        position = float.IsFinite(position) ? Math.Clamp(position, 0f, 1f) : 1f;
        if (stops.Count == 1 || position <= stops[0].Position)
        {
            return stops[0] with { Position = position };
        }
        if (position >= stops[^1].Position) return stops[^1] with { Position = position };

        var low = 1;
        var high = stops.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (position <= stops[middle].Position) high = middle;
            else low = middle + 1;
        }

        var current = stops[low];
        var previous = stops[low - 1];
        var distance = current.Position - previous.Position;
        var amount = distance <= 0.000001f
            ? 0f
            : Math.Clamp((position - previous.Position) / distance, 0f, 1f);
        return new Reference3DLinearLightStop(
            position,
            Vector3.Lerp(previous.DiffuseIrradiance, current.DiffuseIrradiance, amount),
            Vector3.Lerp(previous.SpecularRadiance, current.SpecularRadiance, amount),
            Vector3.Lerp(previous.FresnelRadiance, current.FresnelRadiance, amount));
    }

    internal static int EvaluateReference3DLinearLightingArgb(
        int baseArgb,
        Vector3 diffuseIrradiance,
        Vector3 specularRadiance,
        float metallic = 0f,
        Vector3 fresnelRadiance = default,
        float dielectricF0 = 1f)
    {
        var color = Color.FromArgb(baseArgb);
        if (color.A == 0) return Color.Transparent.ToArgb();
        var baseLinear = Reference3DLinearColor(baseArgb);
        var diffuse = Reference3DFiniteOrZero(diffuseIrradiance);
        var specular = Reference3DFiniteOrZero(specularRadiance);
        var fresnel = Reference3DFiniteOrZero(fresnelRadiance);
        metallic = float.IsFinite(metallic) ? Math.Clamp(metallic, 0f, 1f) : 0f;
        dielectricF0 = float.IsFinite(dielectricF0) ? Math.Clamp(dielectricF0, 0f, 1f) : 1f;
        var specularTint = Vector3.Lerp(
            new Vector3(dielectricF0),
            baseLinear,
            metallic);
        var output = Vector3.Max(
            Vector3.Zero,
            baseLinear * diffuse + specular * specularTint + fresnel);
        return Color.FromArgb(
            color.A,
            Reference3DLinearChannelToSrgbByte(output.X),
            Reference3DLinearChannelToSrgbByte(output.Y),
            Reference3DLinearChannelToSrgbByte(output.Z)).ToArgb();
    }

    private static int ApplyReference3DLinearLightingToPremultipliedPixel(
        int premultipliedArgb,
        Vector3 diffuseIrradiance,
        Vector3 specularRadiance,
        float metallic,
        Vector3 fresnelRadiance,
        float dielectricF0)
    {
        var alpha = (premultipliedArgb >>> 24) & 0xff;
        if (alpha == 0) return 0;
        var straightArgb = alpha << 24
            | UnpremultiplyReference3DChannel((premultipliedArgb >>> 16) & 0xff, alpha) << 16
            | UnpremultiplyReference3DChannel((premultipliedArgb >>> 8) & 0xff, alpha) << 8
            | UnpremultiplyReference3DChannel(premultipliedArgb & 0xff, alpha);
        var litArgb = EvaluateReference3DLinearLightingArgb(
            straightArgb,
            diffuseIrradiance,
            specularRadiance,
            metallic,
            fresnelRadiance,
            dielectricF0);
        return alpha << 24
            | PremultiplyReference3DChannel((litArgb >>> 16) & 0xff, alpha) << 16
            | PremultiplyReference3DChannel((litArgb >>> 8) & 0xff, alpha) << 8
            | PremultiplyReference3DChannel(litArgb & 0xff, alpha);
    }

    private static int UnpremultiplyReference3DChannel(int value, int alpha) =>
        Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);

    private static int PremultiplyReference3DChannel(int value, int alpha) =>
        (value * alpha + 127) / 255;

    private static int Reference3DLinearChannelToSrgbByte(float value) =>
        (int)MathF.Round(Math.Clamp(Reference3DLinearToSrgb(value), 0f, 1f) * 255f);

    private static Vector3 Reference3DFiniteOrZero(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : Vector3.Zero;

    private static void CopyReference3DBitmapPixels(
        BitmapData data,
        int[] pixels,
        int width,
        int height,
        bool fromBitmap)
    {
        var stride = Math.Abs(data.Stride) / sizeof(int);
        if (stride == width && data.Stride > 0)
        {
            if (fromBitmap) Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            else Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            return;
        }

        var row = new int[stride];
        for (var y = 0; y < height; y++)
        {
            var scan = IntPtr.Add(data.Scan0, y * data.Stride);
            if (fromBitmap)
            {
                Marshal.Copy(scan, row, 0, stride);
                Array.Copy(row, 0, pixels, y * width, width);
            }
            else
            {
                Array.Copy(pixels, y * width, row, 0, width);
                Marshal.Copy(row, 0, scan, stride);
            }
        }
    }

    private void ResetReference3DGdiScreenTransform(Graphics graphics)
    {
        if (_reference3DGdiRasterLayout is not { } layout)
        {
            graphics.ResetTransform();
            graphics.TranslateTransform(_reference3DGdiViewportOffset.X, _reference3DGdiViewportOffset.Y);
            return;
        }
        using var transform = new Matrix(
            layout.ScaleX,
            0f,
            0f,
            layout.ScaleY,
            -layout.Bounds.Left * layout.ScaleX,
            -layout.Bounds.Top * layout.ScaleY);
        graphics.Transform = transform;
    }

    private Matrix Reference3DGdiScreenMatrix(Matrix3x2 transform)
    {
        if (_reference3DGdiRasterLayout is not { } layout)
        {
            return new Matrix(
                transform.M11,
                transform.M12,
                transform.M21,
                transform.M22,
                transform.M31 + _reference3DGdiViewportOffset.X,
                transform.M32 + _reference3DGdiViewportOffset.Y);
        }
        return new Matrix(
            transform.M11 * layout.ScaleX,
            transform.M12 * layout.ScaleY,
            transform.M21 * layout.ScaleX,
            transform.M22 * layout.ScaleY,
            (transform.M31 - layout.Bounds.Left) * layout.ScaleX,
            (transform.M32 - layout.Bounds.Top) * layout.ScaleY);
    }
}
