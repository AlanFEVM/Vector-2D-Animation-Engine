namespace VectorAnimationEngine;

internal static class LayerFilterBounds
{
    // Memory budget for one isolated GDI compositing surface. A surface keeps a
    // 32bpp PArgb bitmap (4 bytes/pixel) plus two managed int copies used by the
    // non-Normal blend modes (8 bytes/pixel), about 12 bytes/pixel together.
    // 10,000,000 pixels caps a surface near 120 MB and also bounds the filter
    // scratch arrays (about 16 bytes/pixel, transient). Without the cap, a 30 px
    // blur at 32x already asks for ~54 million pixels (~650 MB) and at 64x for
    // ~182 million (~2 GB). The value also keeps a plain 4K viewport (8.3 million
    // pixels) inside the budget so 1x rendering on a 4K display is untouched.
    // Keep this near MaximumReference3DOpticalSurfacePixels so both raster paths
    // share one scale.
    internal const int MaximumCompositeSurfacePixels = 10_000_000;

    internal readonly record struct CompositeBudget(SymbolFilterPadding Padding, bool FiltersEnabled,
        float FilterScaleFactor);

    // Degradation order is fixed and predictable:
    //   1. Requested padding fits            -> unchanged appearance.
    //   2. Only a smaller padding fits       -> shrink every side by the same factor
    //                                           and scale the filter kernels to the
    //                                           retained padding. Filters stay visible
    //                                           inside the smaller surface instead of
    //                                           processing tails that would be cropped.
    //   3. Even zero padding exceeds the cap -> drop filters entirely (the viewport
    //                                           itself is larger than the budget) and
    //                                           composite the layers unpadded.
    // Normal zoom levels (1x-8x on a 1600x900 stage) stay in step 1, so their rendering
    // is bit-identical. FilterScaleFactor is 1 whenever padding is untouched.
    internal static CompositeBudget ApplyCompositeBudget(Size viewport, SymbolFilterPadding requested)
    {
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);
        var horizontal = (long)requested.Left + requested.Right;
        var vertical = (long)requested.Top + requested.Bottom;
        if ((width + horizontal) * (height + vertical) <= MaximumCompositeSurfacePixels)
        {
            return new(requested, true, 1f);
        }
        if ((long)width * height > MaximumCompositeSurfacePixels) return new(default, false, 0f);

        var scale = SolvePaddingScale(width, height, horizontal, vertical);
        var padding = new SymbolFilterPadding(
            (int)Math.Floor(requested.Left * scale),
            (int)Math.Floor(requested.Top * scale),
            (int)Math.Floor(requested.Right * scale),
            (int)Math.Floor(requested.Bottom * scale));
        // Rounding and the aspect coupling between axes can leave the area a few
        // pixels above the cap. Trim the widest side first; only degraded zooms
        // reach this loop, and every iteration removes one pixel.
        while (PaddedPixels(width, height, padding) > MaximumCompositeSurfacePixels)
        {
            if (padding.Left >= padding.Right && padding.Left > 0)
                padding = padding with { Left = padding.Left - 1 };
            else if (padding.Right > 0) padding = padding with { Right = padding.Right - 1 };
            else if (padding.Top >= padding.Bottom && padding.Top > 0)
                padding = padding with { Top = padding.Top - 1 };
            else if (padding.Bottom > 0) padding = padding with { Bottom = padding.Bottom - 1 };
            else break;
        }

        return new(padding, true, PaddingScaleFactor(requested, padding));
    }

    // Ratio of the retained padding to the requested padding, used to keep filter
    // kernels sized to what the smaller surface can actually show.
    private static float PaddingScaleFactor(SymbolFilterPadding requested, SymbolFilterPadding retained)
    {
        var factor = 1f;
        var horizontal = requested.Left + requested.Right;
        var vertical = requested.Top + requested.Bottom;
        if (horizontal > 0) factor = Math.Min(factor, (float)(retained.Left + retained.Right) / horizontal);
        if (vertical > 0) factor = Math.Min(factor, (float)(retained.Top + retained.Bottom) / vertical);
        return Math.Clamp(factor, 0f, 1f);
    }

    // Largest uniform padding scale whose padded surface stays inside the budget.
    private static double SolvePaddingScale(int width, int height, long horizontal, long vertical)
    {
        var quadratic = (double)horizontal * vertical;
        var linear = (double)width * vertical + (double)height * horizontal;
        var constant = (double)((long)width * height - MaximumCompositeSurfacePixels);
        if (quadratic <= 0d) return linear <= 0d ? 0d : Math.Clamp(-constant / linear, 0d, 1d);
        var discriminant = linear * linear - 4d * quadratic * constant;
        if (discriminant <= 0d) return 0d;
        return Math.Clamp((-linear + Math.Sqrt(discriminant)) / (2d * quadratic), 0d, 1d);
    }

    private static long PaddedPixels(int width, int height, SymbolFilterPadding padding) =>
        ((long)width + padding.Left + padding.Right) * ((long)height + padding.Top + padding.Bottom);

    // Union sibling extents and add ancestor extents: nested filters accumulate, unrelated
    // instances do not inflate one another's surfaces. All source pixels that can reach the
    // viewport must be rendered before filtering, including shapes just outside the screen.
    internal static SymbolFilterPadding GetPadding(VectorScene scene, float pixelScale)
    {
        if (!scene.HasSymbolFilters) return default;
        var values = new SymbolFilterPadding[scene.LayerCount];
        var resolved = new byte[scene.LayerCount];
        var result = default(SymbolFilterPadding);
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            var padding = Resolve(layer);
            result = new(Math.Max(result.Left, padding.Left), Math.Max(result.Top, padding.Top),
                Math.Max(result.Right, padding.Right), Math.Max(result.Bottom, padding.Bottom));
        }
        // These surfaces surround a viewport, not a symbol's content bounds. A rightward
        // shadow can be sourced by geometry to the LEFT of the viewport as well.
        var horizontal = Math.Max(result.Left, result.Right);
        var vertical = Math.Max(result.Top, result.Bottom);
        return new(horizontal, vertical, horizontal, vertical);

        SymbolFilterPadding Resolve(int layer)
        {
            if (resolved[layer] == 2) return values[layer];
            if (resolved[layer] == 1) throw new InvalidDataException("Cyclic symbol filter groups.");
            resolved[layer] = 1;
            var own = SymbolFilterRasterizer.GetPadding(scene.GetLayerSymbolFilters(layer), pixelScale);
            var parent = scene.GetLayerParentIndex(layer);
            var inherited = parent >= 0 ? Resolve(parent) : default;
            values[layer] = new(checked(own.Left + inherited.Left), checked(own.Top + inherited.Top),
                checked(own.Right + inherited.Right), checked(own.Bottom + inherited.Bottom));
            resolved[layer] = 2;
            return values[layer];
        }
    }
}
