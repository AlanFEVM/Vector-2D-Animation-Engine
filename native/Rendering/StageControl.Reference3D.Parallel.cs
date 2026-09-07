using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const int Reference3DParallelRenderPlanMinimumObjects = 32;

    internal int LastReference3DParallelRenderPlanWorkers { get; private set; }

    internal int LastReference3DParallelRenderPlanLayers { get; private set; }

    internal int LastReference3DParallelRenderPlanObjects { get; private set; }

    internal double LastReference3DParallelRenderPlanMilliseconds { get; private set; }

    private Reference3DRenderItem[] BuildReference3DLayerRenderItemsParallel(
        IReadOnlyList<int> layers,
        bool substituteOutlineItems)
    {
        if (layers.Count == 0)
        {
            LastReference3DParallelRenderPlanWorkers = 1;
            LastReference3DParallelRenderPlanLayers = 0;
            LastReference3DParallelRenderPlanObjects = 0;
            LastReference3DParallelRenderPlanMilliseconds = 0;
            return [];
        }

        var layerObjects = new int[layers.Count][];
        var renderableLayers = 0;
        var renderableObjects = 0;
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if ((uint)layer >= Scene.LayerCount || !Scene.ShouldRenderLayerContent(layer))
            {
                continue;
            }

            var objects = GetReference3DLayerObjects(layer);
            if (objects.Length == 0) continue;
            layerObjects[index] = objects;
            renderableLayers++;
            renderableObjects += objects.Length;
        }

        var layerResults = new Reference3DRenderItem[layers.Count][];
        var useParallel = renderableLayers > 1
            && renderableObjects >= Reference3DParallelRenderPlanMinimumObjects;
        var workers = useParallel
            ? ParallelBatch.WorkerCount(layers.Count, 1)
            : 1;
        var startedAt = Stopwatch.GetTimestamp();
        if (workers > 1)
        {
            ParallelBatch.For(
                layers.Count,
                1,
                (_, start, end) =>
                {
                    for (var index = start; index < end; index++)
                    {
                        if (layerObjects[index] is { } objects)
                        {
                            layerResults[index] = BuildReference3DLayerRenderBatch(
                                layers[index],
                                objects,
                                substituteOutlineItems);
                        }
                    }
                });
        }
        else
        {
            for (var index = 0; index < layers.Count; index++)
            {
                if (layerObjects[index] is { } objects)
                {
                    layerResults[index] = BuildReference3DLayerRenderBatch(
                        layers[index],
                        objects,
                        substituteOutlineItems);
                }
            }
        }

        var result = new List<Reference3DRenderItem>(renderableObjects * 4);
        foreach (var layerResult in layerResults)
        {
            if (layerResult is { Length: > 0 }) result.AddRange(layerResult);
        }

        LastReference3DParallelRenderPlanWorkers = workers;
        LastReference3DParallelRenderPlanLayers = renderableLayers;
        LastReference3DParallelRenderPlanObjects = renderableObjects;
        LastReference3DParallelRenderPlanMilliseconds =
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        return result.ToArray();
    }

    private Reference3DRenderItem[] BuildReference3DLayerRenderBatch(
        int layer,
        IReadOnlyList<int> objects,
        bool substituteOutlineItems)
    {
        if (substituteOutlineItems && !Scene.GetEffectiveLayerOutlineColor(layer).IsEmpty)
        {
            return BuildReference3DOutlineRenderBatch(layer, objects);
        }

        if (objects is int[] objectArray
            && objectArray.Length >= Reference3DParallelRenderPlanMinimumObjects
            && objectArray.All(objectIndex =>
                (uint)objectIndex < Scene.ObjectCount
                && Scene.ShapeKind[objectIndex] != ShapeKind.Line))
        {
            return BuildReference3DObjectRenderBatch(objectArray);
        }

        return GetReference3DLayerRenderItems(objects);
    }

    private Reference3DRenderItem[] BuildReference3DObjectRenderBatch(int[] objects)
    {
        var workers = ParallelBatch.WorkerCount(objects.Length, 24);
        if (workers <= 1) return GetReference3DLayerRenderItems(objects);

        var chunks = new Reference3DRenderItem[workers][];
        ParallelBatch.For(
            objects.Length,
            24,
            (worker, start, end) =>
            {
                chunks[worker] = GetReference3DLayerRenderItems(
                    new ArraySegment<int>(objects, start, end - start));
            });

        var result = new List<Reference3DRenderItem>(objects.Length * 2);
        foreach (var chunk in chunks)
        {
            if (chunk is { Length: > 0 }) result.AddRange(chunk);
        }
        result.Sort(CompareReference3DRenderItems);
        return result.ToArray();
    }

    private Reference3DRenderItem[] BuildReference3DOutlineRenderBatch(
        int layer,
        IReadOnlyList<int> objects)
    {
        if (objects.Count < Reference3DParallelRenderPlanMinimumObjects
            || objects is not int[] objectArray)
        {
            var result = new List<Reference3DRenderItem>(objects.Count);
            AppendReference3DOutlineItems(result, layer, objects);
            return result.ToArray();
        }

        var workers = ParallelBatch.WorkerCount(objectArray.Length, 24);
        if (workers <= 1)
        {
            var result = new List<Reference3DRenderItem>(objects.Count);
            AppendReference3DOutlineItems(result, layer, objects);
            return result.ToArray();
        }

        var chunks = new Reference3DRenderItem[workers][];
        ParallelBatch.For(
            objectArray.Length,
            24,
            (worker, start, end) =>
            {
                var chunk = new List<Reference3DRenderItem>(end - start);
                AppendReference3DOutlineItems(
                    chunk,
                    layer,
                    new ArraySegment<int>(objectArray, start, end - start));
                chunks[worker] = chunk.ToArray();
            });

        var combined = new List<Reference3DRenderItem>(objects.Count);
        foreach (var chunk in chunks)
        {
            if (chunk is { Length: > 0 }) combined.AddRange(chunk);
        }
        combined.Sort(CompareReference3DRenderItems);
        return combined.ToArray();
    }
}
