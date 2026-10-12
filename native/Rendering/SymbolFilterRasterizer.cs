using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal readonly record struct SymbolFilterPadding(int Left, int Top, int Right, int Bottom);

// The caller draws the isolated symbol into a padded, transparent PArgb surface.
// All work is local to this invocation; no bitmap, device, or Stage state is retained.
internal static class SymbolFilterRasterizer
{
    private const int BoxPasses = 3;

    // Preserve the original per-pass kernel (radius BlurX/Y * pixelScale / 3).
    // Quality controls repetitions; missing legacy fields retain three passes.
    // Padding covers every pass, including fractional kernels and effect chains.
    internal static SymbolFilterPadding GetPadding(SymbolFilters filters, float pixelScale = 1f)
    {
        Validate(filters, pixelScale);
        int horizontal = filters.Blur.Enabled ? BlurSupport(filters.Blur, filters.Blur.BlurX, pixelScale) : 0;
        int vertical = filters.Blur.Enabled ? BlurSupport(filters.Blur, filters.Blur.BlurY, pixelScale) : 0;
        if (filters.AdditionalBlurs is { } stack)
            for (int i = 0; i < stack.Count; i++)
            {
                var item = stack[i];
                if (!item.Enabled) continue;
                horizontal = checked(horizontal + BlurSupport(item, item.BlurX, pixelScale));
                vertical = checked(vertical + BlurSupport(item, item.BlurY, pixelScale));
            }
        if (Visible(filters.Glow.Enabled, filters.Glow.Strength, filters.Glow.Opacity, filters.Glow.ColorArgb))
        {
            horizontal = checked(horizontal + Support(filters.Glow.BlurX, pixelScale, filters.Glow.Quality));
            vertical = checked(vertical + Support(filters.Glow.BlurY, pixelScale, filters.Glow.Quality));
        }

        int left = horizontal, right = horizontal, top = vertical, bottom = vertical;
        if (Visible(filters.Shadow.Enabled, filters.Shadow.Strength, filters.Shadow.Opacity, filters.Shadow.ColorArgb))
        {
            (double dx, double dy) = ShadowOffset(filters.Shadow, pixelScale);
            int spreadX = Support(filters.Shadow.BlurX, pixelScale, filters.Shadow.Quality);
            int spreadY = Support(filters.Shadow.BlurY, pixelScale, filters.Shadow.Quality);
            // Cover the unshifted blurred mask as well as its final translation:
            // cropping an intermediate tail would also remove that tail after
            // it moves back inside the image. Bilinear translation reaches both
            // floor(offset) and ceil(offset).
            left = checked(left + spreadX + (int)Math.Max(0, -Math.Floor(dx)));
            right = checked(right + spreadX + (int)Math.Max(0, Math.Ceiling(dx)));
            top = checked(top + spreadY + (int)Math.Max(0, -Math.Floor(dy)));
            bottom = checked(bottom + spreadY + (int)Math.Max(0, Math.Ceiling(dy)));
        }
        foreach (var edge in new[] { filters.Bevel, filters.GradientBevel, filters.GradientGlow })
        {
            if (!edge.Enabled) continue;
            int offset = checked((int)Math.Ceiling((double)edge.Distance * pixelScale));
            int x = checked(Support(edge.BlurX, pixelScale, edge.Quality) + offset);
            int y = checked(Support(edge.BlurY, pixelScale, edge.Quality) + offset);
            left = checked(left + x); right = checked(right + x);
            top = checked(top + y); bottom = checked(bottom + y);
        }
        if (filters.FlashFilters is { } flash)
            for (int i = 0; i < flash.Count; i++)
            {
                var item = flash[i];
                if (!item.Enabled) continue;
                int x = BlurSupport(item.Blur, item.BlurX, pixelScale);
                int y = BlurSupport(item.Blur, item.BlurY, pixelScale);
                double angle = item.Angle * Math.PI / 180;
                double dx = item.Kind == SymbolFlashFilterKind.Shadow ? Math.Cos(angle) * item.Distance * pixelScale : 0;
                double dy = item.Kind == SymbolFlashFilterKind.Shadow ? Math.Sin(angle) * item.Distance * pixelScale : 0;
                left = checked(left + x + (int)Math.Max(0, -Math.Floor(dx)));
                right = checked(right + x + (int)Math.Max(0, Math.Ceiling(dx)));
                top = checked(top + y + (int)Math.Max(0, -Math.Floor(dy)));
                bottom = checked(bottom + y + (int)Math.Max(0, Math.Ceiling(dy)));
            }
        return new(left, top, right, bottom);
    }

    // Null means no filter ran; an empty rectangle is a fully transparent group.
    // Otherwise the returned region contains every nontransparent output pixel.
    internal static Rectangle? Apply(Bitmap surface, SymbolFilters filters, float pixelScale = 1f,
        SymbolFilterGpuRasterizer? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var padding = GetPadding(filters, pixelScale); // Also validates scaled support/offset overflow.
        if (surface.PixelFormat != PixelFormat.Format32bppPArgb)
            throw new ArgumentException(
                "Symbol filters require a Format32bppPArgb bitmap.", nameof(surface));
        bool blur = filters.Blur.Enabled && (filters.Blur.BlurX > 0 || filters.Blur.BlurY > 0);
        blur |= filters.AdditionalBlurs?.HasEnabled == true;
        bool glow = Visible(filters.Glow.Enabled, filters.Glow.Strength, filters.Glow.Opacity, filters.Glow.ColorArgb);
        bool shadow = Visible(filters.Shadow.Enabled, filters.Shadow.Strength, filters.Shadow.Opacity, filters.Shadow.ColorArgb);
        if (!filters.ColorTransform.Enabled && !blur && !glow && !shadow && !filters.HasEdgeEffects && filters.FlashFilters?.HasEnabled != true) return null;

        // Read alpha without allocating a viewport-sized pixel array. Every
        // nonzero alpha (including 1/255) contributes to the exact content bounds.
        var content = FindAlphaBounds(surface);
        if (content.IsEmpty) return Rectangle.Empty;
        // GetPadding covers every intermediate blur as well as the translated
        // shadow. Outside this region the input and all finite-support outputs
        // are transparent. Clipping only to the original bitmap preserves its
        // original zero-boundary behavior, including content touching an edge.
        var bounds = Rectangle.FromLTRB(
            (int)Math.Max(0L, (long)content.Left - padding.Left),
            (int)Math.Max(0L, (long)content.Top - padding.Top),
            (int)Math.Min(surface.Width, (long)content.Right + padding.Right),
            (int)Math.Min(surface.Height, (long)content.Bottom + padding.Bottom));
        int width = bounds.Width, height = bounds.Height;
        int count = checked(width * height);
        int components = checked(count * 4);
        int rowBytes = checked(width * 4);
        var bytes = ArrayPool<byte>.Shared.Rent(components);
        try
        {
        CopyPixels(surface, bounds, bytes, rowBytes, write: false);
        if (accelerator?.TryApply(bytes, width, height, filters, pixelScale) == true)
        {
            CopyPixels(surface, bounds, bytes, rowBytes, write: true);
            return bounds;
        }
        // A failed readback may have copied some rows into bytes. CPU fallback
        // must start from the original, still untouched bitmap.
        if (accelerator?.LastFailure is not null) CopyPixels(surface, bounds, bytes, rowBytes, write: false);

        // Retain floating-point premultiplied channels throughout the pipeline:
        // quantizing an intermediate alpha mask would erase low-alpha tails
        // that a subsequent glow or shadow strength can make visible again.
        var pixels = ArrayPool<float>.Shared.Rent(components);
        var plane = ArrayPool<float>.Shared.Rent(count);
        var scratch = ArrayPool<float>.Shared.Rent(count);
        try
        {
        for (int p = 0; p < components; p += 4)
        {
            float alpha = bytes[p + 3] / 255f;
            pixels[p] = Math.Min(bytes[p] / 255f, alpha);
            pixels[p + 1] = Math.Min(bytes[p + 1] / 255f, alpha);
            pixels[p + 2] = Math.Min(bytes[p + 2] / 255f, alpha);
            pixels[p + 3] = alpha;
            if (filters.ColorTransform is { Enabled: true } transform && alpha > 0)
            {
                pixels[p] = Math.Clamp(pixels[p] / alpha * transform.BlueMultiplier + transform.BlueOffset, 0, 1) * alpha;
                pixels[p + 1] = Math.Clamp(pixels[p + 1] / alpha * transform.GreenMultiplier + transform.GreenOffset, 0, 1) * alpha;
                pixels[p + 2] = Math.Clamp(pixels[p + 2] / alpha * transform.RedMultiplier + transform.RedOffset, 0, 1) * alpha;
                float opacity = transform.AlphaMultiplier ?? 1;
                for (int channel = 0; channel < 4; channel++) pixels[p + channel] *= opacity;
            }
        }

        if (blur)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                for (int i = 0; i < count; i++) plane[i] = pixels[i * 4 + channel];
                ApplyBlur(filters.Blur);
                if (filters.AdditionalBlurs is { } chain)
                    for (int i = 0; i < chain.Count; i++) ApplyBlur(chain[i]);
                for (int i = 0; i < count; i++) pixels[i * 4 + channel] = plane[i];
            }
        }
        void ApplyBlur(SymbolBlurFilter item)
        {
            if (!item.Enabled) return;
            if (item.FlashCompatible) FlashBlur(ref plane, ref scratch, width, height, item, pixelScale);
            else Blur(ref plane, ref scratch, width, height, item.BlurX, item.BlurY, pixelScale, item.Quality);
        }

        if (glow)
        {
            ExtractAlpha(pixels, plane, count);
            Blur(ref plane, ref scratch, width, height,
                filters.Glow.BlurX, filters.Glow.BlurY, pixelScale, filters.Glow.Quality);
            CompositeBehind(pixels, plane, width, height, filters.Glow.ColorArgb,
                filters.Glow.Strength, filters.Glow.Opacity, 0, 0);
        }

        if (shadow)
        {
            // The shadow belongs to the preceding blur-plus-glow result.
            ExtractAlpha(pixels, plane, count);
            Blur(ref plane, ref scratch, width, height,
                filters.Shadow.BlurX, filters.Shadow.BlurY, pixelScale, filters.Shadow.Quality);
            (double dx, double dy) = ShadowOffset(filters.Shadow, pixelScale);
            CompositeBehind(pixels, plane, width, height, filters.Shadow.ColorArgb,
                filters.Shadow.Strength, filters.Shadow.Opacity, dx, dy);
        }

        ApplyEdge(filters.Bevel, false, false);
        ApplyEdge(filters.GradientBevel, true, false);
        ApplyEdge(filters.GradientGlow, true, true);
        if (filters.FlashFilters is { } flash)
            for (int i = 0; i < flash.Count; i++)
            {
                var item = flash[i];
                if (!item.Enabled) continue;
                if (item.Kind == SymbolFlashFilterKind.Blur)
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        for (int p = 0; p < count; p++) plane[p] = pixels[p * 4 + channel];
                        ApplyBlur(item.Blur);
                        for (int p = 0; p < count; p++) pixels[p * 4 + channel] = plane[p];
                    }
                    continue;
                }
                ExtractAlpha(pixels, plane, count);
                ApplyBlur(item.Blur);
                double angle = item.Angle * Math.PI / 180;
                double dx = item.Kind == SymbolFlashFilterKind.Shadow ? Math.Cos(angle) * item.Distance * pixelScale : 0;
                double dy = item.Kind == SymbolFlashFilterKind.Shadow ? Math.Sin(angle) * item.Distance * pixelScale : 0;
                uint color = (uint)item.ColorArgb;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 4;
                    float originalAlpha = pixels[p + 3];
                    float mask = Sample(plane, width, height, x - dx, y - dy);
                    float coverage = Math.Clamp((item.Inner ? 1 - mask : mask) * item.Strength * item.Opacity * (color >> 24) / 255f, 0, 1);
                    float alpha = coverage * (item.Inner ? originalAlpha : item.HideObject ? 1 : 1 - originalAlpha);
                    float keep = item.Knockout || item.HideObject ? 0 : item.Inner ? 1 - coverage : 1;
                    pixels[p] = ToByte(pixels[p] * keep + (color & 255) / 255f * alpha) / 255f;
                    pixels[p + 1] = ToByte(pixels[p + 1] * keep + ((color >> 8) & 255) / 255f * alpha) / 255f;
                    pixels[p + 2] = ToByte(pixels[p + 2] * keep + ((color >> 16) & 255) / 255f * alpha) / 255f;
                    pixels[p + 3] = ToByte(originalAlpha * keep + alpha) / 255f;
                }
            }

        void ApplyEdge(SymbolEdgeFilter effect, bool gradient, bool glowEffect)
        {
            if (!effect.Enabled || effect.Strength == 0 || effect.Opacity == 0) return;
            ExtractAlpha(pixels, plane, count);
            Blur(ref plane, ref scratch, width, height, effect.BlurX, effect.BlurY, pixelScale, effect.Quality);
            double angle = effect.Angle * Math.PI / 180;
            double dx = Math.Cos(angle) * effect.Distance * pixelScale;
            double dy = Math.Sin(angle) * effect.Distance * pixelScale;
            var start = Color.FromArgb(effect.StartColorArgb);
            var end = Color.FromArgb(effect.EndColorArgb);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, p = i * 4;
                float signal = glowEffect ? plane[i] :
                    Sample(plane, width, height, x + dx, y + dy) - Sample(plane, width, height, x - dx, y - dy);
                float coverage = Math.Clamp(Math.Abs(signal) * effect.Strength, 0, 1);
                float t = glowEffect ? Math.Clamp(signal, 0, 1) : gradient ? (signal + 1) * 0.5f : signal >= 0 ? 1 : 0;
                float alpha = coverage * effect.Opacity * (start.A + (end.A - start.A) * t) / 255f;
                // Glow is behind the silhouette; bevel shades its interior without changing alpha.
                alpha *= glowEffect ? 1 - pixels[p + 3] : pixels[p + 3];
                float keep = glowEffect ? 1 : 1 - alpha / Math.Max(pixels[p + 3], 1e-10f);
                pixels[p] = pixels[p] * keep + (start.B + (end.B - start.B) * t) / 255f * alpha;
                pixels[p + 1] = pixels[p + 1] * keep + (start.G + (end.G - start.G) * t) / 255f * alpha;
                pixels[p + 2] = pixels[p + 2] * keep + (start.R + (end.R - start.R) * t) / 255f * alpha;
                if (glowEffect) pixels[p + 3] += alpha;
            }
        }

        for (int p = 0; p < components; p += 4)
        {
            byte alpha = ToByte(pixels[p + 3]);
            bytes[p] = Math.Min(ToByte(pixels[p]), alpha);
            bytes[p + 1] = Math.Min(ToByte(pixels[p + 1]), alpha);
            bytes[p + 2] = Math.Min(ToByte(pixels[p + 2]), alpha);
            bytes[p + 3] = alpha;
        }
        // Allocation, parameter and computation failures leave the input intact.
        CopyPixels(surface, bounds, bytes, rowBytes, write: true);
        return bounds;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pixels);
            ArrayPool<float>.Shared.Return(plane);
            ArrayPool<float>.Shared.Return(scratch);
        }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    private static void Validate(SymbolFilters filters, float pixelScale)
    {
        if (!filters.IsValid) throw new ArgumentOutOfRangeException(nameof(filters));
        if (!float.IsFinite(pixelScale) || pixelScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelScale));
    }

    private static bool Visible(bool enabled, float strength, float opacity, int colorArgb)
        => enabled && strength > 0 && opacity > 0 && ((uint)colorArgb >> 24) != 0;

    private static int Support(float blur, float pixelScale, int? quality)
        => checked(SymbolFilterValidation.Passes(quality) * (int)Math.Ceiling((double)blur * pixelScale / BoxPasses));

    private static int BlurSupport(SymbolBlurFilter filter, float blur, float scale)
        => filter.FlashCompatible
            ? SymbolFilterValidation.Passes(filter.Quality) * (int)Math.Ceiling((FlashKernelWidth(blur, scale) - 1) / 2)
            : Support(blur, scale, filter.Quality);

    // Stage zoom scales the distance between kernel samples; instance transforms
    // have already been applied to the geometry and must not scale the filter again.
    internal static double FlashKernelWidth(float blur, float scale)
        => Math.Clamp((double)(blur - 1) * scale + 1, 1, 255);

    private static void FlashBlur(ref float[] plane, ref float[] scratch,
        int width, int height, SymbolBlurFilter filter, float scale)
    {
        double x = FlashKernelWidth(filter.BlurX, scale), y = FlashKernelWidth(filter.BlurY, scale);
        for (int pass = 0; pass < SymbolFilterValidation.Passes(filter.Quality); pass++)
        {
            if (x > 1) { FlashAxis(plane, scratch, width, height, x, true); (plane, scratch) = (scratch, plane); }
            if (y > 1) { FlashAxis(plane, scratch, width, height, y, false); (plane, scratch) = (scratch, plane); }
        }
    }

    // Fixed byte samples keep sums exact and avoid accumulating roundoff across
    // quality passes. The fractional edge weight is an 8-bit fixed-point value;
    // the full (unrounded) width remains the normalization divisor.
    private static void FlashAxis(float[] input, float[] output, int width, int height, double kernelWidth, bool horizontal)
    {
        double radius = (kernelWidth - 1) / 2;
        int whole = (int)Math.Ceiling(radius) - 1;
        int weight = (int)Math.Floor((radius - whole) * 255);
        int length = horizontal ? width : height, lines = horizontal ? height : width;
        int step = horizontal ? 1 : width;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width : line;
            long sum = 0;
            for (int i = 0; i <= Math.Min(whole, length - 1); i++) sum += ToByte(input[start + i * step]);
            for (int position = 0; position < length; position++)
            {
                long before = (long)position - whole - 1, after = (long)position + whole + 1;
                long weighted = sum * 255;
                if (before >= 0) weighted += weight * ToByte(input[start + (int)before * step]);
                if (after < length) weighted += weight * ToByte(input[start + (int)after * step]);
                output[start + position * step] = (float)Math.Clamp(Math.Floor(weighted / (255 * kernelWidth)), 0, 255) / 255f;
                long leaving = (long)position - whole;
                if (leaving >= 0) sum -= ToByte(input[start + (int)leaving * step]);
                if (after < length) sum += ToByte(input[start + (int)after * step]);
            }
        }
    }

    private static (double X, double Y) ShadowOffset(SymbolShadowFilter filter, float scale)
    {
        double angle = filter.Angle * (Math.PI / 180);
        double distance = (double)filter.Distance * scale;
        // Positive angles rotate clockwise in screen coordinates (positive Y down).
        return (Math.Cos(angle) * distance, Math.Sin(angle) * distance);
    }

    private static void ExtractAlpha(float[] pixels, float[] plane, int count)
    {
        for (int i = 0; i < count; i++) plane[i] = pixels[i * 4 + 3];
    }

    private static void Blur(ref float[] plane, ref float[] scratch,
        int width, int height, float blurX, float blurY, float scale, int? quality)
    {
        double radiusX = (double)blurX * scale / BoxPasses;
        double radiusY = (double)blurY * scale / BoxPasses;
        for (int pass = 0; pass < SymbolFilterValidation.Passes(quality); pass++)
        {
            if (radiusX > 0)
            {
                BoxBlur(plane, scratch, width, height, radiusX, horizontal: true);
                (plane, scratch) = (scratch, plane);
            }
            if (radiusY > 0)
            {
                BoxBlur(plane, scratch, width, height, radiusY, horizontal: false);
                (plane, scratch) = (scratch, plane);
            }
        }
    }

    // The full normalization divisor is retained at image edges: samples outside
    // the surface are transparent, never clamped to the nearest opaque pixel.
    // Sliding sums bound the work by O(width * height), even for huge radii.
    private static void BoxBlur(float[] input, float[] output, int width, int height,
        double radius, bool horizontal)
    {
        int whole = checked((int)Math.Floor(radius));
        double fraction = radius - whole;
        double inverse = 1 / (2 * radius + 1);
        int length = horizontal ? width : height;
        int lines = horizontal ? height : width;
        int step = horizontal ? 1 : width;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width : line;
            double sum = 0;
            int initialEnd = Math.Min(whole, length - 1);
            for (int i = 0; i <= initialEnd; i++) sum += input[start + i * step];
            for (int position = 0; position < length; position++)
            {
                double value = sum;
                long before = (long)position - whole - 1;
                long after = (long)position + whole + 1;
                if (fraction > 0)
                {
                    if (before >= 0) value += fraction * input[start + (int)before * step];
                    if (after < length) value += fraction * input[start + (int)after * step];
                }
                output[start + position * step] = (float)Math.Clamp(value * inverse, 0, 1);
                long leaving = (long)position - whole;
                if (leaving >= 0) sum -= input[start + (int)leaving * step];
                if (after < length) sum += input[start + (int)after * step];
            }
        }
    }

    private static void CompositeBehind(float[] pixels, float[] mask, int width, int height,
        int colorArgb, float strength, float opacity, double offsetX, double offsetY)
    {
        uint argb = (uint)colorArgb;
        float colorBlue = (argb & 255) / 255f;
        float colorGreen = ((argb >> 8) & 255) / 255f;
        float colorRed = ((argb >> 16) & 255) / 255f;
        float effectOpacity = opacity * ((argb >> 24) / 255f);
        bool shifted = offsetX != 0 || offsetY != 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int p = index * 4;
                float alpha = shifted
                    ? Sample(mask, width, height, x - offsetX, y - offsetY)
                    : mask[index];
                // Strength changes the mask coverage; opacity remains a separate
                // multiplier even when strength has saturated that coverage.
                float behind = Math.Clamp(alpha * strength, 0, 1) * effectOpacity
                    * (1 - pixels[p + 3]);
                pixels[p] += colorBlue * behind;
                pixels[p + 1] += colorGreen * behind;
                pixels[p + 2] += colorRed * behind;
                pixels[p + 3] += behind;
            }
        }
    }

    private static float Sample(float[] mask, int width, int height, double x, double y)
    {
        if (x <= -1 || x >= width || y <= -1 || y >= height) return 0;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = At(mask, width, height, x0, y0);
        float b = At(mask, width, height, x0 + 1, y0);
        float c = At(mask, width, height, x0, y0 + 1);
        float d = At(mask, width, height, x0 + 1, y0 + 1);
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

    private static float At(float[] mask, int width, int height, int x, int y)
        => (uint)x < (uint)width && (uint)y < (uint)height ? mask[y * width + x] : 0;

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);

    private static Rectangle FindAlphaBounds(Bitmap surface)
    {
        var bounds = new Rectangle(0, 0, surface.Width, surface.Height);
        int rowBytes = checked(bounds.Width * 4);
        var row = new byte[rowBytes];
        int left = bounds.Width, top = bounds.Height, right = 0, bottom = 0;
        BitmapData data = surface.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < bounds.Height; y++)
            {
                Marshal.Copy(RowAddress(data, y), row, 0, rowBytes);
                // Once the two row endpoints are found, the interior cannot
                // enlarge this row's bounds and does not need another scan.
                int first = 0;
                while (first < bounds.Width && row[first * 4 + 3] == 0) first++;
                if (first == bounds.Width) continue;
                int last = bounds.Width - 1;
                while (last > first && row[last * 4 + 3] == 0) last--;
                left = Math.Min(left, first);
                right = Math.Max(right, last + 1);
                top = Math.Min(top, y);
                bottom = y + 1;
            }
        }
        finally
        {
            surface.UnlockBits(data);
        }
        return right > left ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }

    private static void CopyPixels(Bitmap surface, Rectangle bounds, byte[] bytes, int rowBytes, bool write)
    {
        BitmapData data = surface.LockBits(bounds,
            write ? ImageLockMode.WriteOnly : ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < bounds.Height; y++)
            {
                IntPtr row = RowAddress(data, y);
                if (write) Marshal.Copy(bytes, y * rowBytes, row, rowBytes);
                else Marshal.Copy(row, bytes, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            surface.UnlockBits(data);
        }
    }

    private static IntPtr RowAddress(BitmapData data, int y)
        => new(checked(data.Scan0.ToInt64() + (long)y * data.Stride));
}
