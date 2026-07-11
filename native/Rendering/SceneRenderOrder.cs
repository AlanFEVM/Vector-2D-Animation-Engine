namespace VectorAnimationEngine;

internal enum SceneRenderPass : byte
{
    Fill,
    Stroke
}

internal static class SceneRenderOrder
{
    public static bool HasFill(ShapeKind shape)
    {
        return shape is not ShapeKind.Line and not ShapeKind.Freeform;
    }

    public static bool HasStroke(ShapeKind shape, float stroke)
    {
        return stroke > 0 && shape != ShapeKind.BrushStroke;
    }
}

internal sealed class SceneRenderOrderBuffer
{
    private List<int>?[] _layers = [];

    public int VisibleCount { get; private set; }
    public int ScannedCount { get; private set; }
    public long VisibleAtoms { get; private set; }

    public void Collect(VectorScene scene, RectangleF bounds, int frame)
    {
        EnsureLayerCapacity(scene.LayerCount);
        foreach (var layer in _layers) layer?.Clear();

        VisibleCount = 0;
        ScannedCount = 0;
        VisibleAtoms = 0;
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top;
        var bottom = bounds.Bottom;
        scene.GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = scene.CellIndex(cx, cy);
                var start = scene.CellStart[cell];
                var end = scene.CellStart[cell + 1];
                ScannedCount += end - start;
                for (var p = start; p < end; p++)
                {
                    var index = scene.CellObjects[p];
                    var layer = scene.ObjectLayer[index];
                    if (!scene.IsObjectActive(index, frame)) continue;
                    var objectBounds = scene.GetObjectWorldBounds(index);
                    if (objectBounds.Right < left
                        || objectBounds.Left > right
                        || objectBounds.Bottom < top
                        || objectBounds.Top > bottom)
                    {
                        continue;
                    }

                    (_layers[layer] ??= new List<int>(64)).Add(index);
                    VisibleCount++;
                    VisibleAtoms += scene.AtomCount[index];
                }
            }
        }

        foreach (var layer in _layers)
        {
            if (layer is not { Count: > 1 }) continue;
            layer.Sort((a, b) =>
            {
                var comparison = scene.ObjectOrder[a].CompareTo(scene.ObjectOrder[b]);
                if (comparison != 0) return comparison;
                comparison = scene.ObjectSubOrder[a].CompareTo(scene.ObjectSubOrder[b]);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
        }
    }

    public int Draw(VectorScene scene, int drawLimit, Action<int> drawFill, Action<int> drawStroke)
    {
        if (VisibleCount == 0 || drawLimit <= 0) return 0;

        var skip = Math.Max(0, VisibleCount - drawLimit);
        var drawn = 0;
        for (var layerIndex = scene.LayerCount - 1; layerIndex >= 0; layerIndex--)
        {
            var layer = _layers[layerIndex];
            if (layer is null || layer.Count == 0) continue;

            var start = 0;
            if (skip > 0)
            {
                start = Math.Min(skip, layer.Count);
                skip -= start;
            }

            for (var i = start; i < layer.Count; i++)
            {
                var index = layer[i];
                if (SceneRenderOrder.HasFill(scene.ShapeKind[index])) drawFill(index);
            }

            for (var i = start; i < layer.Count; i++)
            {
                var index = layer[i];
                if (SceneRenderOrder.HasStroke(scene.ShapeKind[index], scene.Stroke[index])) drawStroke(index);
            }

            drawn += layer.Count - start;
        }

        return drawn;
    }

    private void EnsureLayerCapacity(int layerCount)
    {
        if (_layers.Length == layerCount) return;
        Array.Resize(ref _layers, layerCount);
    }
}
