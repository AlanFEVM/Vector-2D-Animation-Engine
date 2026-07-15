namespace VectorAnimationEngine;

internal enum SceneRenderPass : byte
{
    Fill,
    Stroke
}

internal static class SceneRenderOrder
{
    internal const int DenseObjectLodMinimumVisibleObjects = 320;
    private const long DenseObjectLodMinimumVisibleAtoms = 1024;
    private const double DenseObjectLodCoverageMultiplier = 8d;

    public static bool HasFill(ShapeKind shape)
    {
        return shape is not ShapeKind.Line and not ShapeKind.Freeform;
    }

    public static bool HasStroke(ShapeKind shape, float stroke)
    {
        return stroke > 0 && shape != ShapeKind.BrushStroke;
    }

    public static bool ShouldUseDenseObjectLod(
        VectorScene scene,
        float pixelZoom,
        RectangleF visibleBounds,
        SceneRenderOrderBuffer renderOrder)
    {
        if (scene.HasLayerEffects
            || !renderOrder.SummaryMatchesActiveContent
            || pixelZoom >= 0.18f
            || renderOrder.VisibleCount < DenseObjectLodMinimumVisibleObjects
            || renderOrder.VisibleAtoms < DenseObjectLodMinimumVisibleAtoms)
        {
            return false;
        }

        var viewportArea = Math.Max(1d, visibleBounds.Width * visibleBounds.Height);
        return renderOrder.VisibleBoundsArea >= viewportArea * DenseObjectLodCoverageMultiplier;
    }
}

internal sealed class SceneRenderOrderBuffer
{
    private const int ParallelCollectThreshold = 8192;
    private List<int>?[] _layers = [];
    private int[] _activeKeyframes = [];
    private CollectBatchBuffer[] _collectBatches = [];

    public int VisibleCount { get; private set; }
    public int ScannedCount { get; private set; }
    public long VisibleAtoms { get; private set; }
    public double VisibleBoundsArea { get; private set; }
    public bool SummaryMatchesActiveContent { get; private set; }
    public int LastCollectBatchCount { get; private set; } = 1;

    public void Collect(VectorScene scene, RectangleF bounds, int frame)
    {
        EnsureLayerCapacity(scene.LayerCount);
        foreach (var layer in _layers) layer?.Clear();
        if (_activeKeyframes.Length < scene.LayerCount) Array.Resize(ref _activeKeyframes, scene.LayerCount);
        scene.PopulateActiveKeyframeFrames(frame, _activeKeyframes);
        SummaryMatchesActiveContent = HasSummaryMatchingActiveContent(scene);

        VisibleCount = 0;
        ScannedCount = 0;
        VisibleAtoms = 0;
        VisibleBoundsArea = 0;
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top;
        var bottom = bounds.Bottom;
        scene.GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);
        var columns = maxX - minX + 1;
        var rows = maxY - minY + 1;
        var cellSlots = Math.Max(0, columns * rows);
        for (var slot = 0; slot < cellSlots; slot++)
        {
            var cell = CellForSlot(scene, slot, minX, minY, columns);
            ScannedCount += scene.CellStart[cell + 1] - scene.CellStart[cell];
        }

        var workers = Math.Min(cellSlots, ParallelBatch.WorkerCount(ScannedCount, ParallelCollectThreshold));
        if (workers <= 1)
        {
            LastCollectBatchCount = 1;
            CollectSequential(scene, minX, minY, columns, cellSlots, left, right, top, bottom);
            SortLayers(scene, parallel: false, workers: 1);
            return;
        }

        LastCollectBatchCount = workers;
        EnsureCollectBatchCapacity(workers, scene.LayerCount);
        Parallel.For(0, workers, worker =>
        {
            var batch = _collectBatches[worker];
            batch.Reset(scene.LayerCount);
            var start = (int)((long)cellSlots * worker / workers);
            var end = (int)((long)cellSlots * (worker + 1) / workers);
            for (var slot = start; slot < end; slot++)
            {
                var cell = CellForSlot(scene, slot, minX, minY, columns);
                CollectCell(scene, cell, left, right, top, bottom, batch);
            }
        });

        for (var worker = 0; worker < workers; worker++)
        {
            VisibleCount += _collectBatches[worker].VisibleCount;
            VisibleAtoms += _collectBatches[worker].VisibleAtoms;
            VisibleBoundsArea += _collectBatches[worker].VisibleBoundsArea;
        }

        SortLayers(scene, parallel: true, workers);
    }

    private void CollectSequential(
        VectorScene scene,
        int minX,
        int minY,
        int columns,
        int cellSlots,
        float left,
        float right,
        float top,
        float bottom)
    {
        for (var slot = 0; slot < cellSlots; slot++)
        {
            var cell = CellForSlot(scene, slot, minX, minY, columns);
            var start = scene.CellStart[cell];
            var end = scene.CellStart[cell + 1];
            for (var p = start; p < end; p++)
            {
                var index = scene.CellObjects[p];
                var layer = scene.ObjectLayer[index];
                if (scene.ObjectKeyframeFrame[index] != _activeKeyframes[layer]) continue;
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
                VisibleBoundsArea += VisibleArea(objectBounds, left, right, top, bottom);
            }
        }
    }

    private void CollectCell(
        VectorScene scene,
        int cell,
        float left,
        float right,
        float top,
        float bottom,
        CollectBatchBuffer batch)
    {
        var start = scene.CellStart[cell];
        var end = scene.CellStart[cell + 1];
        for (var p = start; p < end; p++)
        {
            var index = scene.CellObjects[p];
            var layer = scene.ObjectLayer[index];
            if (scene.ObjectKeyframeFrame[index] != _activeKeyframes[layer]) continue;
            var objectBounds = scene.GetObjectWorldBounds(index);
            if (objectBounds.Right < left
                || objectBounds.Left > right
                || objectBounds.Bottom < top
                || objectBounds.Top > bottom)
            {
                continue;
            }

            batch.Add(layer, index, scene.AtomCount[index], VisibleArea(objectBounds, left, right, top, bottom));
        }
    }

    private static double VisibleArea(RectangleF bounds, float left, float right, float top, float bottom)
    {
        var width = Math.Max(0f, Math.Min(bounds.Right, right) - Math.Max(bounds.Left, left));
        var height = Math.Max(0f, Math.Min(bounds.Bottom, bottom) - Math.Max(bounds.Top, top));
        return width * (double)height;
    }

    private bool HasSummaryMatchingActiveContent(VectorScene scene)
    {
        if (scene.HasLayerEffects) return false;
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            if (!scene.IsLayerEffectivelyVisible(layer)) return false;
        }

        for (var objectIndex = 0; objectIndex < scene.ObjectCount; objectIndex++)
        {
            var layer = scene.ObjectLayer[objectIndex];
            if ((uint)layer >= _activeKeyframes.Length
                || scene.ObjectKeyframeFrame[objectIndex] != _activeKeyframes[layer])
            {
                return false;
            }
        }

        return true;
    }

    private void SortLayers(VectorScene scene, bool parallel, int workers)
    {
        if (!parallel)
        {
            foreach (var layer in _layers)
            {
                if (layer is { Count: > 1 }) layer.Sort((a, b) => CompareObjects(scene, a, b));
            }
            return;
        }

        Parallel.For(0, scene.LayerCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, layerIndex =>
        {
            List<int>? target = null;
            for (var worker = 0; worker < workers; worker++)
            {
                var source = _collectBatches[worker].Layers[layerIndex];
                if (source is not { Count: > 0 }) continue;
                target ??= _layers[layerIndex] ??= new List<int>(Math.Max(64, source.Count));
                target.AddRange(source);
            }

            if (target is { Count: > 1 }) target.Sort((a, b) => CompareObjects(scene, a, b));
        });
    }

    private static int CompareObjects(VectorScene scene, int a, int b)
    {
        var comparison = scene.ObjectOrder[a].CompareTo(scene.ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = scene.ObjectSubOrder[a].CompareTo(scene.ObjectSubOrder[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    private static int CellForSlot(VectorScene scene, int slot, int minX, int minY, int columns)
    {
        var y = minY + slot / columns;
        var x = minX + slot % columns;
        return scene.CellIndex(x, y);
    }

    public int Draw(VectorScene scene, int drawLimit, Action<int> drawFill, Action<int> drawStroke)
    {
        ArgumentNullException.ThrowIfNull(drawFill);
        ArgumentNullException.ThrowIfNull(drawStroke);
        return DrawLayers(scene, drawLimit, (_, objects, start) =>
        {
            for (var index = start; index < objects.Count; index++)
            {
                var objectIndex = objects[index];
                if (SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) drawFill(objectIndex);
            }

            for (var index = start; index < objects.Count; index++)
            {
                var objectIndex = objects[index];
                if (SceneRenderOrder.HasStroke(scene.ShapeKind[objectIndex], scene.Stroke[objectIndex])) drawStroke(objectIndex);
            }
        });
    }

    public IReadOnlyList<int> GetLayerObjects(int layer)
    {
        return (uint)layer < _layers.Length && _layers[layer] is { } objects
            ? objects
            : Array.Empty<int>();
    }

    public int DrawLayers(VectorScene scene, int drawLimit, Action<int, IReadOnlyList<int>, int> drawLayer)
    {
        ArgumentNullException.ThrowIfNull(drawLayer);
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

            drawLayer(layerIndex, layer, start);

            drawn += layer.Count - start;
        }

        return drawn;
    }

    private void EnsureLayerCapacity(int layerCount)
    {
        if (_layers.Length == layerCount) return;
        Array.Resize(ref _layers, layerCount);
    }

    private void EnsureCollectBatchCapacity(int workers, int layerCount)
    {
        if (_collectBatches.Length < workers) Array.Resize(ref _collectBatches, workers);
        for (var worker = 0; worker < workers; worker++)
        {
            _collectBatches[worker] ??= new CollectBatchBuffer();
            _collectBatches[worker].EnsureLayerCapacity(layerCount);
        }
    }

    private sealed class CollectBatchBuffer
    {
        public List<int>?[] Layers = [];
        public int VisibleCount { get; private set; }
        public long VisibleAtoms { get; private set; }
        public double VisibleBoundsArea { get; private set; }

        public void EnsureLayerCapacity(int layerCount)
        {
            if (Layers.Length != layerCount) Array.Resize(ref Layers, layerCount);
        }

        public void Reset(int layerCount)
        {
            EnsureLayerCapacity(layerCount);
            foreach (var layer in Layers) layer?.Clear();
            VisibleCount = 0;
            VisibleAtoms = 0;
            VisibleBoundsArea = 0;
        }

        public void Add(int layer, int objectIndex, uint atoms, double visibleBoundsArea)
        {
            (Layers[layer] ??= new List<int>(32)).Add(objectIndex);
            VisibleCount++;
            VisibleAtoms += atoms;
            VisibleBoundsArea += visibleBoundsArea;
        }
    }
}
