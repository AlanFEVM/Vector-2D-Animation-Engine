using System.Diagnostics;
using System.Runtime.InteropServices;

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
        if (shape is ShapeKind.ImportedSvg or ShapeKind.Text or ShapeKind.Bitmap) return true;
        return shape is not ShapeKind.Line and not ShapeKind.Freeform;
    }

    public static bool HasStroke(ShapeKind shape, float stroke)
    {
        if (shape is ShapeKind.ImportedSvg or ShapeKind.Text or ShapeKind.Bitmap) return false;
        return stroke > 0 && shape is not ShapeKind.BrushStroke and not ShapeKind.MixingStroke;
    }

    public static bool RequiresObjectRenderer(VectorScene scene)
    {
        if (scene.HasLayerOutline) return true;
        for (var index = 0; index < scene.ObjectCount; index++)
        {
            // Raster payloads cannot be drawn by the packed/LOD tile path, so their
            // presence forces the full per-object renderer.
            if (scene.ShapeKind[index] is ShapeKind.ImportedSvg
                or ShapeKind.Text
                or ShapeKind.MixingStroke
                or ShapeKind.Bitmap)
            {
                return true;
            }
        }
        return false;
    }

    public static bool ShouldUseDenseObjectLod(
        VectorScene scene,
        float pixelZoom,
        RectangleF visibleBounds,
        SceneRenderOrderBuffer renderOrder)
    {
        if (scene.HasDisplayLayerEffects
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

    public static void DrawObjectPasses(
        VectorScene scene,
        IReadOnlyList<int> objects,
        int start,
        IReadOnlySet<int>? deferredFillObjects,
        Action<int> drawFill,
        Action<int> drawStroke)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(drawFill);
        ArgumentNullException.ThrowIfNull(drawStroke);

        start = Math.Clamp(start, 0, objects.Count);
        for (var index = start; index < objects.Count; index++)
        {
            var objectIndex = objects[index];
            if (HasFill(scene.ShapeKind[objectIndex])
                && deferredFillObjects?.Contains(objectIndex) != true)
            {
                drawFill(objectIndex);
            }
        }

        for (var index = start; index < objects.Count; index++)
        {
            var objectIndex = objects[index];
            if (HasStroke(scene.ShapeKind[objectIndex], scene.Stroke[objectIndex])) drawStroke(objectIndex);
        }

        if (deferredFillObjects is null || deferredFillObjects.Count == 0) return;
        for (var index = 0; index < objects.Count; index++)
        {
            var objectIndex = objects[index];
            if (HasFill(scene.ShapeKind[objectIndex]) && deferredFillObjects.Contains(objectIndex))
            {
                drawFill(objectIndex);
            }
        }
    }
}

internal sealed class SceneRenderOrderBuffer
{
    private const int ParallelCollectThreshold = 8192;
    private List<int>?[] _layers = [];
    private int[] _activeKeyframes = [];
    private CollectBatchBuffer[] _collectBatches = [];
    private VectorScene? _summaryMatchScene;
    private int _summaryMatchFrame = int.MinValue;
    private long _summaryMatchGeometryRevision = -1;
    private long _summaryMatchSummaryRevision = -1;
    private int[] _summaryMatchKeyframes = [];
    private bool _summaryMatchValue;
    private float[] _worldBoundsLeft = [];
    private float[] _worldBoundsTop = [];
    private float[] _worldBoundsRight = [];
    private float[] _worldBoundsBottom = [];
    private VectorScene? _worldBoundsScene;
    private long _worldBoundsGeometryRevision = -1;
    private long _worldBoundsContentRevision = -1;
    private int _worldBoundsFrame = int.MinValue;
    private int _worldBoundsObjectCount = -1;
    private long[] _layerSortKeys = [];
    private int[] _layerSortItems = [];
    private long[] _radixKeys = [];
    private int[] _radixItems = [];
    private int[]? _radixCounts;

    public int VisibleCount { get; private set; }
    public int ScannedCount { get; private set; }
    public long VisibleAtoms { get; private set; }
    public double VisibleBoundsArea { get; private set; }
    public bool SummaryMatchesActiveContent { get; private set; }
    public int LastCollectBatchCount { get; private set; } = 1;

    internal static double DiagnosticBoundsMs;
    internal static double DiagnosticCollectPhaseMs;
    internal static double DiagnosticSortPhaseMs;

    public void Collect(VectorScene scene, RectangleF bounds, int frame)
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        EnsureLayerCapacity(scene.LayerCount);
        EnsureWorldBounds(scene, frame);
        DiagnosticBoundsMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        phaseStarted = Stopwatch.GetTimestamp();
        foreach (var layer in _layers) layer?.Clear();
        if (_activeKeyframes.Length < scene.LayerCount) Array.Resize(ref _activeKeyframes, scene.LayerCount);
        scene.PopulateActiveKeyframeFrames(frame, _activeKeyframes);
        SummaryMatchesActiveContent = HasSummaryMatchingActiveContent(scene, frame);

        var activeObjects = scene.GetActiveObjectIndices(frame);
        if (ShouldUseActiveObjectIndex(scene, activeObjects.Length))
        {
            CollectActiveObjects(scene, activeObjects, bounds);
            LastCollectBatchCount = 1;
            DiagnosticCollectPhaseMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
            var singleSortStarted = Stopwatch.GetTimestamp();
            SortLayers(scene, parallel: false, workers: 1);
            DiagnosticSortPhaseMs = Stopwatch.GetElapsedTime(singleSortStarted).TotalMilliseconds;
            return;
        }

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
            ScannedCount += scene.GetSpatialCellObjectCount(cell);
        }

        var workers = Math.Min(cellSlots, ParallelBatch.WorkerCount(ScannedCount, ParallelCollectThreshold));
        if (workers <= 1)
        {
            LastCollectBatchCount = 1;
            CollectSequential(scene, minX, minY, columns, cellSlots, left, right, top, bottom);
            DiagnosticCollectPhaseMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
            var sequentialSortStarted = Stopwatch.GetTimestamp();
            SortLayers(scene, parallel: false, workers: 1);
            DiagnosticSortPhaseMs = Stopwatch.GetElapsedTime(sequentialSortStarted).TotalMilliseconds;
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

        DiagnosticCollectPhaseMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        var parallelSortStarted = Stopwatch.GetTimestamp();
        SortLayers(scene, parallel: true, workers);
        DiagnosticSortPhaseMs = Stopwatch.GetElapsedTime(parallelSortStarted).TotalMilliseconds;
    }

    private void EnsureWorldBounds(VectorScene scene, int frame)
    {
        // World bounds depend only on the geometry revision, the materialized
        // active-content revision and the frame, never on the camera. Camera
        // panning therefore keeps the cached boxes for the whole frame.
        if (ReferenceEquals(_worldBoundsScene, scene)
            && _worldBoundsGeometryRevision == scene.GeometryRevision
            && _worldBoundsContentRevision == scene.ActiveContentRevision
            && _worldBoundsFrame == frame
            && _worldBoundsObjectCount == scene.ObjectCount)
        {
            return;
        }

        var count = scene.ObjectCount;
        if (_worldBoundsLeft.Length < count)
        {
            Array.Resize(ref _worldBoundsLeft, count);
            Array.Resize(ref _worldBoundsTop, count);
            Array.Resize(ref _worldBoundsRight, count);
            Array.Resize(ref _worldBoundsBottom, count);
        }

        for (var index = 0; index < count; index++)
        {
            var bounds = scene.GetObjectWorldBounds(index);
            _worldBoundsLeft[index] = bounds.Left;
            _worldBoundsTop[index] = bounds.Top;
            _worldBoundsRight[index] = bounds.Right;
            _worldBoundsBottom[index] = bounds.Bottom;
        }

        _worldBoundsScene = scene;
        _worldBoundsGeometryRevision = scene.GeometryRevision;
        _worldBoundsContentRevision = scene.ActiveContentRevision;
        _worldBoundsFrame = frame;
        _worldBoundsObjectCount = count;
    }

    private RectangleF CachedWorldBounds(int index)
    {
        return RectangleF.FromLTRB(
            _worldBoundsLeft[index],
            _worldBoundsTop[index],
            _worldBoundsRight[index],
            _worldBoundsBottom[index]);
    }

    private static bool ShouldUseActiveObjectIndex(VectorScene scene, int activeObjectCount)
    {
        if (activeObjectCount <= 4_096) return true;
        return activeObjectCount * 4L <= Math.Max(1, scene.ObjectCount) * 3L;
    }

    private void CollectActiveObjects(
        VectorScene scene,
        ReadOnlySpan<int> activeObjects,
        RectangleF bounds)
    {
        VisibleCount = 0;
        ScannedCount = activeObjects.Length;
        VisibleAtoms = 0;
        VisibleBoundsArea = 0;
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top;
        var bottom = bounds.Bottom;
        for (var activeIndex = 0; activeIndex < activeObjects.Length; activeIndex++)
        {
            var index = activeObjects[activeIndex];
            var layer = scene.ObjectLayer[index];
            if (!scene.ShouldRenderLayerContent(layer)) continue;
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
                if ((uint)index >= scene.ObjectCount) continue;
                var layer = scene.ObjectLayer[index];
                if (!scene.ShouldRenderLayerContent(layer)) continue;
                if (scene.ObjectKeyframeFrame[index] != _activeKeyframes[layer]) continue;
                var objectBounds = CachedWorldBounds(index);
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

            foreach (var index in scene.GetPendingSpatialCellObjects(cell))
            {
                if ((uint)index >= scene.ObjectCount) continue;
                var layer = scene.ObjectLayer[index];
                if (!scene.ShouldRenderLayerContent(layer)) continue;
                if (scene.ObjectKeyframeFrame[index] != _activeKeyframes[layer]) continue;
                var objectBounds = CachedWorldBounds(index);
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
            if ((uint)index >= scene.ObjectCount) continue;
            var layer = scene.ObjectLayer[index];
            if (!scene.ShouldRenderLayerContent(layer)) continue;
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

        foreach (var index in scene.GetPendingSpatialCellObjects(cell))
        {
            if ((uint)index >= scene.ObjectCount) continue;
            var layer = scene.ObjectLayer[index];
            if (!scene.ShouldRenderLayerContent(layer)) continue;
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

    private bool HasSummaryMatchingActiveContent(VectorScene scene, int frame)
    {
        if (ReferenceEquals(_summaryMatchScene, scene)
            && _summaryMatchFrame == frame
            && _summaryMatchGeometryRevision == scene.GeometryRevision
            && _summaryMatchSummaryRevision == scene.SummaryRevision
            && ActiveKeyframesMatch())
        {
            return _summaryMatchValue;
        }

        var matches = ComputeSummaryMatchingActiveContent(scene);
        _summaryMatchScene = scene;
        _summaryMatchFrame = frame;
        _summaryMatchGeometryRevision = scene.GeometryRevision;
        _summaryMatchSummaryRevision = scene.SummaryRevision;
        if (_summaryMatchKeyframes.Length != _activeKeyframes.Length) Array.Resize(ref _summaryMatchKeyframes, _activeKeyframes.Length);
        Array.Copy(_activeKeyframes, _summaryMatchKeyframes, _activeKeyframes.Length);
        _summaryMatchValue = matches;
        return matches;
    }

    private bool ActiveKeyframesMatch()
    {
        if (_summaryMatchKeyframes.Length != _activeKeyframes.Length) return false;
        for (var layer = 0; layer < _activeKeyframes.Length; layer++)
        {
            if (_summaryMatchKeyframes[layer] != _activeKeyframes[layer]) return false;
        }

        return true;
    }

    private bool ComputeSummaryMatchingActiveContent(VectorScene scene)
    {
        if (scene.HasDisplayLayerEffects) return false;
        for (var layer = 0; layer < scene.LayerCount; layer++)
        {
            if (!scene.IsLayerEffectivelyVisible(layer)) return false;
        }

        for (var objectIndex = 0; objectIndex < scene.ObjectCount; objectIndex++)
        {
            var layer = scene.ObjectLayer[objectIndex];
            if (!scene.ShouldRenderLayerContent(layer)) continue;
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
        // A single renderable layer makes the parallel fan-out costlier than the
        // merge itself, so the merge and the sort stay on the calling thread.
        if (!parallel || scene.LayerCount <= 1)
        {
            for (var layerIndex = 0; layerIndex < scene.LayerCount; layerIndex++)
            {
                var target = parallel
                    ? MergeCollectBatches(layerIndex, workers)
                    : (_layers.Length > layerIndex ? _layers[layerIndex] : null);
                SortLayerInPlace(scene, target, useSharedSortBuffers: true);
            }

            return;
        }

        // Layers sort on parallel workers, so they cannot share the reusable
        // sort buffers and allocate their own instead.
        Parallel.For(0, scene.LayerCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, layerIndex =>
        {
            SortLayerInPlace(scene, MergeCollectBatches(layerIndex, workers), useSharedSortBuffers: false);
        });
    }

    private List<int>? MergeCollectBatches(int layerIndex, int workers)
    {
        List<int>? target = null;
        for (var worker = 0; worker < workers; worker++)
        {
            var source = _collectBatches[worker].Layers[layerIndex];
            if (source is not { Count: > 0 }) continue;
            if (target is null)
            {
                // Reserve the merged size up front; growing while appending every
                // worker batch otherwise re-copies the whole layer several times.
                var mergedCount = 0;
                for (var remaining = worker; remaining < workers; remaining++)
                {
                    mergedCount += _collectBatches[remaining].Layers[layerIndex]?.Count ?? 0;
                }

                target = _layers[layerIndex] ??= new List<int>(Math.Max(64, mergedCount));
            }

            target.AddRange(source);
        }

        return target;
    }

    private void SortLayerInPlace(VectorScene scene, List<int>? layer, bool useSharedSortBuffers)
    {
        if (layer is not { Count: > 1 }) return;
        // Collect() walks the spatial grid, which already yields render order for
        // documents that were never re-stacked in the frame. Verify that in one
        // linear pass so the O(n log n) comparison sort (and its delegate calls)
        // only runs when the order actually changed.
        if (IsOrderedForRendering(scene, layer)) return;
        if (TrySortLayerByOrderKey(scene, layer, useSharedSortBuffers)) return;
        layer.Sort((a, b) => CompareObjects(scene, a, b));
    }

    private bool TrySortLayerByOrderKey(VectorScene scene, List<int> layer, bool useSharedSortBuffers)
    {
        var count = layer.Count;
        var objects = CollectionsMarshal.AsSpan(layer);
        var keys = scene.ObjectOrder;
        long[] sortKeys;
        int[] sortItems;
        if (useSharedSortBuffers)
        {
            if (_layerSortKeys.Length < count) Array.Resize(ref _layerSortKeys, count);
            if (_layerSortItems.Length < count) Array.Resize(ref _layerSortItems, count);
            sortKeys = _layerSortKeys;
            sortItems = _layerSortItems;
        }
        else
        {
            sortKeys = new long[count];
            sortItems = new int[count];
        }

        for (var index = 0; index < count; index++)
        {
            var objectIndex = objects[index];
            sortItems[index] = objectIndex;
            sortKeys[index] = keys[objectIndex];
        }

        // ObjectOrder is allocated from a monotonic counter, so unique keys make
        // the ordering exactly equal to the ObjectOrder/ObjectSubOrder/index
        // comparison. Duplicate keys fall back to the comparing sort, which keeps
        // the original sub-order and index tie-breaking.
        var maximumKey = 0L;
        var minimumKey = long.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var key = sortKeys[index];
            if (key < minimumKey) minimumKey = key;
            if (key > maximumKey) maximumKey = key;
        }

        if (useSharedSortBuffers && minimumKey >= 0 && maximumKey <= uint.MaxValue)
        {
            RadixSortLayerKeys(sortKeys, sortItems, count);
        }
        else
        {
            Array.Sort(sortKeys, sortItems, 0, count);
        }

        for (var index = 1; index < count; index++)
        {
            if (sortKeys[index - 1] == sortKeys[index]) return false;
        }

        for (var index = 0; index < count; index++) objects[index] = sortItems[index];
        return true;
    }

    /// <summary>
    /// Two-pass 16-bit least-significant-digit radix sort. Object order keys stay
    /// inside the non-negative 32-bit range, so this orders a layer in linear time
    /// without the delegate and comparison overhead of a comparison sort.
    /// </summary>
    private void RadixSortLayerKeys(long[] keys, int[] items, int count)
    {
        if (_radixKeys.Length < count) Array.Resize(ref _radixKeys, count);
        if (_radixItems.Length < count) Array.Resize(ref _radixItems, count);
        // The bucket table is only needed once a layer actually reaches the radix
        // path, so it is allocated lazily instead of reserving 256 KB per buffer.
        var counts = _radixCounts ??= new int[65536];
        var sourceKeys = keys;
        var sourceItems = items;
        var targetKeys = _radixKeys;
        var targetItems = _radixItems;
        for (var pass = 0; pass < 2; pass++)
        {
            var shift = pass * 16;
            Array.Clear(counts);
            for (var index = 0; index < count; index++)
            {
                counts[(int)((sourceKeys[index] >>> shift) & 0xFFFF)]++;
            }

            var total = 0;
            for (var bucket = 0; bucket < counts.Length; bucket++)
            {
                var bucketCount = counts[bucket];
                counts[bucket] = total;
                total += bucketCount;
            }

            for (var index = 0; index < count; index++)
            {
                var bucket = (int)((sourceKeys[index] >>> shift) & 0xFFFF);
                var destination = counts[bucket]++;
                targetKeys[destination] = sourceKeys[index];
                targetItems[destination] = sourceItems[index];
            }

            (sourceKeys, targetKeys) = (targetKeys, sourceKeys);
            (sourceItems, targetItems) = (targetItems, sourceItems);
        }

        if (!ReferenceEquals(sourceKeys, keys))
        {
            Array.Copy(sourceKeys, keys, count);
            Array.Copy(sourceItems, items, count);
        }
    }

    private static bool IsOrderedForRendering(VectorScene scene, List<int> layer)
    {
        var objects = CollectionsMarshal.AsSpan(layer);
        var orders = scene.ObjectOrder;
        var subOrders = scene.ObjectSubOrder;
        var previous = objects[0];
        for (var index = 1; index < objects.Length; index++)
        {
            var current = objects[index];
            var comparison = orders[previous].CompareTo(orders[current]);
            if (comparison == 0) comparison = subOrders[previous].CompareTo(subOrders[current]);
            if (comparison == 0) comparison = previous.CompareTo(current);
            if (comparison > 0) return false;
            previous = current;
        }

        return true;
    }

    internal static int CompareObjects(VectorScene scene, int a, int b)
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
            SceneRenderOrder.DrawObjectPasses(
                scene,
                objects,
                start,
                deferredFillObjects: null,
                drawFill,
                drawStroke);
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
            // Collect() partitions the spatial cells across workers, so a single
            // worker batch can hold thousands of objects per layer. Start from a
            // reasonable capacity so appending does not repeatedly re-copy it.
            (Layers[layer] ??= new List<int>(256)).Add(objectIndex);
            VisibleCount++;
            VisibleAtoms += atoms;
            VisibleBoundsArea += visibleBoundsArea;
        }
    }
}
