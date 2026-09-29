namespace VectorAnimationEngine;

internal static class LayerFilterBounds
{
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
