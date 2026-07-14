using System.Diagnostics;
using System.Numerics;

namespace VectorAnimationEngine;

internal readonly record struct SceneCompositionObjectOwner(
    string InstanceId,
    string DrawingObjectId,
    string RootInstanceId = "");

internal readonly record struct SceneCompositionBuildMetrics(
    double BucketMilliseconds,
    double SetupMilliseconds,
    double AppendMilliseconds,
    double FinalizeMilliseconds);

internal sealed class SceneCompositionResult
{
    private readonly SceneCompositionObjectOwner[] _owners;
    private IReadOnlyDictionary<int, SceneCompositionObjectOwner>? _ownerView;

    public SceneCompositionResult(SceneCompositionObjectOwner[]? owners = null)
    {
        _owners = owners ?? [];
    }

    public static SceneCompositionResult Empty { get; } = new();
    public IReadOnlyDictionary<int, SceneCompositionObjectOwner> ObjectOwners => _ownerView ??= new IndexedOwnerMap(_owners);

    public bool TryGetOwner(int objectIndex, out SceneCompositionObjectOwner owner)
    {
        if ((uint)objectIndex < _owners.Length)
        {
            owner = _owners[objectIndex];
            return true;
        }

        owner = default;
        return false;
    }

    private sealed class IndexedOwnerMap(SceneCompositionObjectOwner[] owners) : IReadOnlyDictionary<int, SceneCompositionObjectOwner>
    {
        public int Count => owners.Length;
        public IEnumerable<int> Keys => Enumerable.Range(0, owners.Length);
        public IEnumerable<SceneCompositionObjectOwner> Values => owners;
        public SceneCompositionObjectOwner this[int key] => (uint)key < owners.Length ? owners[key] : throw new KeyNotFoundException();

        public bool ContainsKey(int key) => (uint)key < owners.Length;

        public bool TryGetValue(int key, out SceneCompositionObjectOwner value)
        {
            if ((uint)key < owners.Length)
            {
                value = owners[key];
                return true;
            }

            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<int, SceneCompositionObjectOwner>> GetEnumerator()
        {
            for (var index = 0; index < owners.Length; index++) yield return new KeyValuePair<int, SceneCompositionObjectOwner>(index, owners[index]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class SceneCompositionBuilder
{
    private const int PreparedChunkSize = 2048;

    public static SceneCompositionBuildMetrics LastBuildMetrics { get; private set; }

    private readonly record struct CompositionLayer(
        VectorScene Source,
        int SourceLayer,
        string Name,
        Matrix3x2 Transform,
        SceneCompositionObjectOwner Owner);

    private readonly record struct SourceFrameKey(VectorScene Source, int Frame);

    private readonly record struct CompositionWorkItem(
        VectorScene Source,
        int SourceObject,
        int DestinationLayer,
        Matrix3x2 Transform,
        SceneCompositionObjectOwner Owner);

    private enum PreparedCompositionKind : byte
    {
        Primitive,
        Path,
        Freehand,
        Curve
    }

    private readonly record struct PreparedCompositionObject(
        PreparedCompositionKind Kind,
        int DestinationLayer,
        ShapeKind Shape,
        PointF Center,
        SizeF Size,
        float Angle,
        float Stroke,
        int FillArgb,
        int StrokeArgb,
        uint Atoms,
        PointF Start,
        PointF Control,
        PointF End,
        PointF[] Points,
        PointF[][] Contours,
        LineEndpointStyle StartEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle EndEndpointStyle = LineEndpointStyle.Round)
    {
        public bool LinearGradientEnabled { get; init; }
        public GradientKind GradientKind { get; init; }
        public int GradientStartArgb { get; init; }
        public int GradientEndArgb { get; init; }
        public PointF GradientStart { get; init; }
        public PointF GradientEnd { get; init; }
        public GradientStop[] GradientStops { get; init; } = [];
    }

    public static SceneCompositionResult Build(
        VectorScene destination,
        SceneDefinition? sceneDefinition,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);

        if (sceneDefinition is null)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        sceneDefinition.SynchronizeTimelineTracks();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, sceneDefinition.FrameCount - 1));
        foreach (var sceneLayer in sceneDefinition.Layers)
        {
            if (!sceneLayer.Visible
                || !sceneDefinition.Timeline.EvaluateTargetExposure(sceneLayer.Id, localFrame).HasContent)
            {
                continue;
            }

            foreach (var instance in sceneDefinition.InstancesInLayer(sceneLayer.Id))
            {
                if (!instance.Visible
                    || !definitionsById.TryGetValue(instance.DrawingObjectId, out var drawingObject))
                {
                    continue;
                }

                CollectDrawingObjectLayers(
                    drawingObject,
                    instance,
                    InstanceMatrix(instance),
                    localFrame,
                    definitionsById,
                    layers,
                    new HashSet<string>(StringComparer.Ordinal),
                    $"{instance.Name} / {sceneLayer.Name}",
                    instance.Id);
            }
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, sceneDefinition.FrameCount), localFrame);
    }

    public static SceneCompositionResult BuildDrawingObjectChildren(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || drawingObject.Instances.Count == 0)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        drawingObject.SynchronizeInstanceTimelineTracks();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        var ancestry = new HashSet<string>(StringComparer.Ordinal) { drawingObject.Id };
        foreach (var instance in drawingObject.Instances)
        {
            if (!instance.Visible
                || !drawingObject.InstanceTimeline.EvaluateTargetExposure(instance.Id, localFrame).HasContent
                || !definitionsById.TryGetValue(instance.DrawingObjectId, out var child))
            {
                continue;
            }

            CollectDrawingObjectLayers(
                child,
                instance,
                InstanceMatrix(instance),
                localFrame,
                definitionsById,
                layers,
                ancestry,
                instance.Name,
                instance.Id);
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawingObject.FrameCount), localFrame);
    }

    public static SceneCompositionResult BuildDrawingObjectPreview(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        PointF position,
        int frame,
        float opacity = 0.48f)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || !drawingObjects.Any(item => string.Equals(item.Id, drawingObject.Id, StringComparison.Ordinal)))
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var previewInstance = new DrawingObjectInstanceDefinition
        {
            Id = $"drag-preview-{drawingObject.Id}",
            DrawingObjectId = drawingObject.Id,
            Name = drawingObject.Name,
            X = position.X,
            Y = position.Y
        };
        var layers = new List<CompositionLayer>();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        CollectDrawingObjectLayers(
            drawingObject,
            previewInstance,
            InstanceMatrix(previewInstance),
            localFrame,
            definitionsById,
            layers,
            new HashSet<string>(StringComparer.Ordinal),
            drawingObject.Name,
            previewInstance.Id);
        var result = BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawingObject.FrameCount), localFrame);
        destination.ApplyOpacity(opacity);
        return result;
    }

    private static Dictionary<string, DrawingObjectDefinition> DefinitionsById(IReadOnlyList<DrawingObjectDefinition> drawingObjects)
    {
        return drawingObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static void CollectDrawingObjectLayers(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        Matrix3x2 transform,
        int frame,
        IReadOnlyDictionary<string, DrawingObjectDefinition> definitionsById,
        ICollection<CompositionLayer> layers,
        ISet<string> ancestry,
        string path,
        string rootInstanceId)
    {
        if (!ancestry.Add(drawingObject.Id)) return;
        try
        {
            var source = drawingObject.Scene;
            source.SynchronizeTimelineTracks();
            for (var sourceLayer = 0; sourceLayer < source.LayerCount; sourceLayer++)
            {
                layers.Add(new CompositionLayer(
                    source,
                    sourceLayer,
                    $"{path} / {source.LayerNames[sourceLayer]}",
                    transform,
                    new SceneCompositionObjectOwner(instance.Id, drawingObject.Id, rootInstanceId)));
            }

            drawingObject.SynchronizeInstanceTimelineTracks();
            var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
            foreach (var childInstance in drawingObject.Instances)
            {
                if (!childInstance.Visible
                    || !drawingObject.InstanceTimeline.EvaluateTargetExposure(childInstance.Id, localFrame).HasContent
                    || !definitionsById.TryGetValue(childInstance.DrawingObjectId, out var child))
                {
                    continue;
                }

                CollectDrawingObjectLayers(
                    child,
                    childInstance,
                    InstanceMatrix(childInstance) * transform,
                    localFrame,
                    definitionsById,
                    layers,
                    ancestry,
                    $"{path} / {childInstance.Name}",
                    rootInstanceId);
            }
        }
        finally
        {
            ancestry.Remove(drawingObject.Id);
        }
    }

    private static SceneCompositionResult BuildLayers(
        VectorScene destination,
        IReadOnlyList<CompositionLayer> layers,
        int duration,
        int frame)
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        var sourceFrames = BuildSourceFrames(layers, frame);
        var sourceObjectsByLayer = BuildSourceObjectBuckets(layers, sourceFrames);
        var expectedObjectCount = 0;
        var populatedDestinationLayers = new List<int>(layers.Count);
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            var sourceFrame = sourceFrames[layer.Source];
            var sourceObjectCount = sourceObjectsByLayer[new SourceFrameKey(layer.Source, sourceFrame)][layer.SourceLayer].Length;
            expectedObjectCount += sourceObjectCount;
            if (sourceObjectCount > 0) populatedDestinationLayers.Add(destinationLayer);
        }
        var bucketMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        destination.CreateEmpty(Math.Max(1, layers.Count));
        destination.EditFrame = 0;
        using (destination.Timeline.BeginBatchUpdate())
        {
            destination.SynchronizeTimelineTracks();
            foreach (var track in destination.Timeline.Tracks)
            {
                destination.Timeline.SetTrackDuration(track.Id, Math.Max(1, duration));
            }
        }

        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            destination.LayerNames[destinationLayer] = layer.Name;
            destination.LayerVisible[destinationLayer] = true;
            destination.LayerOpacity[destinationLayer] = layer.Source.LayerOpacity[layer.SourceLayer];
            destination.LayerColorArgb[destinationLayer] = layer.Source.LayerColorArgb[layer.SourceLayer];
        }
        var setupMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        var owners = new List<SceneCompositionObjectOwner>(expectedObjectCount);
        destination.BeginDeferredAppend(expectedObjectCount, populatedDestinationLayers);
        try
        {
            var packedBatch = expectedObjectCount >= 8192 && CanUsePackedBatch(layers, sourceObjectsByLayer, sourceFrames);
            if (packedBatch)
            {
                var workItems = BuildWorkItems(layers, sourceObjectsByLayer, sourceFrames, expectedObjectCount);
                var packedObjects = new PackedSceneObject[workItems.Length];
                ParallelBatch.For(workItems.Length, 4096, (_, start, end) =>
                {
                    for (var index = start; index < end; index++) packedObjects[index] = PreparePackedObject(workItems[index]);
                });

                var firstObject = destination.AppendPackedObjects(packedObjects);
                if (firstObject != owners.Count) throw new InvalidOperationException("Packed composition owners lost object-index alignment.");
                for (var index = 0; index < workItems.Length; index++) owners.Add(workItems[index].Owner);
            }
            else if (expectedObjectCount >= 8192)
            {
                var workItems = BuildWorkItems(layers, sourceObjectsByLayer, sourceFrames, expectedObjectCount);
                AppendPreparedChunks(destination, workItems, owners);
            }
            else
            {
                for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
                {
                    var layer = layers[destinationLayer];
                    var sourceFrame = sourceFrames[layer.Source];
                    var sourceObjects = sourceObjectsByLayer[new SourceFrameKey(layer.Source, sourceFrame)][layer.SourceLayer];
                    foreach (var sourceObject in sourceObjects)
                    {
                        var item = new CompositionWorkItem(layer.Source, sourceObject, destinationLayer, layer.Transform, layer.Owner);
                        var destinationObject = AppendPreparedObject(destination, PrepareObject(item));
                        if (destinationObject >= 0) AppendOwner(owners, destinationObject, layer.Owner);
                    }
                }
            }
        }
        finally
        {
            destination.EndDeferredAppend();
        }
        var appendMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        destination.CompleteDeferredBuild();
        var finalizeMilliseconds = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
        LastBuildMetrics = new SceneCompositionBuildMetrics(
            bucketMilliseconds,
            setupMilliseconds,
            appendMilliseconds,
            finalizeMilliseconds);
        return new SceneCompositionResult(owners.ToArray());
    }

    private static void AppendPreparedChunks(
        VectorScene destination,
        CompositionWorkItem[] workItems,
        List<SceneCompositionObjectOwner> owners)
    {
        var preparedObjects = new PreparedCompositionObject[Math.Min(PreparedChunkSize, workItems.Length)];
        for (var offset = 0; offset < workItems.Length; offset += preparedObjects.Length)
        {
            var count = Math.Min(preparedObjects.Length, workItems.Length - offset);
            ParallelBatch.For(count, 256, (_, start, end) =>
            {
                for (var index = start; index < end; index++) preparedObjects[index] = PrepareObject(workItems[offset + index]);
            });

            for (var index = 0; index < count; index++)
            {
                var destinationObject = AppendPreparedObject(destination, preparedObjects[index]);
                if (destinationObject >= 0) AppendOwner(owners, destinationObject, workItems[offset + index].Owner);
            }

            Array.Clear(preparedObjects, 0, count);
        }
    }

    private static void AppendOwner(List<SceneCompositionObjectOwner> owners, int objectIndex, SceneCompositionObjectOwner owner)
    {
        if (objectIndex != owners.Count) throw new InvalidOperationException("Composition owners lost object-index alignment.");
        owners.Add(owner);
    }

    private static bool CanUsePackedBatch(
        IReadOnlyList<CompositionLayer> layers,
        IReadOnlyDictionary<SourceFrameKey, int[][]> sourceObjectsByLayer,
        IReadOnlyDictionary<VectorScene, int> sourceFrames)
    {
        foreach (var layer in layers)
        {
            if (HasShear(layer.Transform)) return false;
            var sourceFrame = sourceFrames[layer.Source];
            var sourceObjects = sourceObjectsByLayer[new SourceFrameKey(layer.Source, sourceFrame)][layer.SourceLayer];
            foreach (var sourceObject in sourceObjects)
            {
                if (layer.Source.ShapeKind[sourceObject] is ShapeKind.Path or ShapeKind.Freeform) return false;
            }
        }

        return true;
    }

    private static CompositionWorkItem[] BuildWorkItems(
        IReadOnlyList<CompositionLayer> layers,
        IReadOnlyDictionary<SourceFrameKey, int[][]> sourceObjectsByLayer,
        IReadOnlyDictionary<VectorScene, int> sourceFrames,
        int expectedObjectCount)
    {
        var workItems = new CompositionWorkItem[expectedObjectCount];
        var index = 0;
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            var sourceFrame = sourceFrames[layer.Source];
            var sourceObjects = sourceObjectsByLayer[new SourceFrameKey(layer.Source, sourceFrame)][layer.SourceLayer];
            foreach (var sourceObject in sourceObjects)
            {
                workItems[index++] = new CompositionWorkItem(layer.Source, sourceObject, destinationLayer, layer.Transform, layer.Owner);
            }
        }

        return workItems;
    }

    private static Dictionary<VectorScene, int> BuildSourceFrames(
        IReadOnlyList<CompositionLayer> layers,
        int frame)
    {
        var result = new Dictionary<VectorScene, int>(ReferenceEqualityComparer.Instance);
        foreach (var layer in layers)
        {
            if (!result.ContainsKey(layer.Source))
            {
                result[layer.Source] = Math.Clamp(frame, 0, Math.Max(0, layer.Source.FrameCount - 1));
            }
        }

        return result;
    }

    private static Dictionary<SourceFrameKey, int[][]> BuildSourceObjectBuckets(
        IReadOnlyList<CompositionLayer> layers,
        IReadOnlyDictionary<VectorScene, int> sourceFrames)
    {
        var result = new Dictionary<SourceFrameKey, int[][]>();
        foreach (var layer in layers)
        {
            var sourceFrame = sourceFrames[layer.Source];
            var key = new SourceFrameKey(layer.Source, sourceFrame);
            if (!result.ContainsKey(key)) result[key] = BuildActiveObjectsByLayer(layer.Source, sourceFrame);
        }

        return result;
    }

    private static int[][] BuildActiveObjectsByLayer(VectorScene source, int frame)
    {
        var activeKeyframes = new int[source.LayerCount];
        source.PopulateActiveKeyframeFrames(frame, activeKeyframes);
        var workers = ParallelBatch.WorkerCount(source.ObjectCount, 8192);
        var batches = new List<int>?[workers][];
        for (var worker = 0; worker < workers; worker++) batches[worker] = new List<int>?[source.LayerCount];

        ParallelBatch.For(source.ObjectCount, 8192, (worker, start, end) =>
        {
            var target = batches[worker];
            for (var index = start; index < end; index++)
            {
                var layer = source.ObjectLayer[index];
                if (source.ObjectKeyframeFrame[index] != activeKeyframes[layer]) continue;
                (target[layer] ??= new List<int>(32)).Add(index);
            }
        }, workers);

        var objectsByLayer = new int[source.LayerCount][];
        Parallel.For(0, source.LayerCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, layer =>
        {
            var count = 0;
            for (var worker = 0; worker < workers; worker++) count += batches[worker][layer]?.Count ?? 0;
            if (count == 0)
            {
                objectsByLayer[layer] = [];
                return;
            }

            var objects = new int[count];
            var offset = 0;
            for (var worker = 0; worker < workers; worker++)
            {
                var batch = batches[worker][layer];
                if (batch is not { Count: > 0 }) continue;
                batch.CopyTo(objects, offset);
                offset += batch.Count;
            }

            Array.Sort(objects, (a, b) => CompareSourceObjects(source, a, b));
            objectsByLayer[layer] = objects;
        });
        return objectsByLayer;
    }

    private static int CompareSourceObjects(VectorScene source, int a, int b)
    {
        var comparison = source.ObjectOrder[a].CompareTo(source.ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = source.ObjectSubOrder[a].CompareTo(source.ObjectSubOrder[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    private static PreparedCompositionObject PrepareObject(CompositionWorkItem workItem)
    {
        var source = workItem.Source;
        var sourceObject = workItem.SourceObject;
        var destinationLayer = workItem.DestinationLayer;
        var transform = workItem.Transform;
        var identityTransform = transform.IsIdentity;
        var shape = source.ShapeKind[sourceObject];
        var determinant = identityTransform ? 1f : transform.M11 * transform.M22 - transform.M12 * transform.M21;
        var stroke = source.Stroke[sourceObject] * MathF.Sqrt(Math.Abs(determinant));
        var fillArgb = source.Argb[sourceObject];
        var strokeArgb = source.StrokeArgb[sourceObject];
        var atoms = source.AtomCount[sourceObject];

        if (shape == ShapeKind.Path && source.TryGetPathWorldContours(sourceObject, out var contours))
        {
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                {
                    contour[pointIndex] = TransformPoint(contour[pointIndex], transform, identityTransform);
                }
            }

            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Path,
                destinationLayer,
                shape,
                PointF.Empty,
                SizeF.Empty,
                0,
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                contours), source, sourceObject, transform, identityTransform);
        }

        if (shape == ShapeKind.Freeform && source.TryGetFreehandWorldPoints(sourceObject, out var freehand))
        {
            for (var pointIndex = 0; pointIndex < freehand.Length; pointIndex++)
            {
                freehand[pointIndex] = TransformPoint(freehand[pointIndex], transform, identityTransform);
            }
            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Freehand,
                destinationLayer,
                shape,
                PointF.Empty,
                SizeF.Empty,
                0,
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                freehand,
                []), source, sourceObject, transform, identityTransform);
        }

        if (shape == ShapeKind.Line
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: true, out var start)
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: false, out var end))
        {
            var control = new PointF(source.CurveControlX[sourceObject], source.CurveControlY[sourceObject]);
            var transformedStart = TransformPoint(start, transform, identityTransform);
            var transformedControl = TransformPoint(control, transform, identityTransform);
            var transformedEnd = TransformPoint(end, transform, identityTransform);
            var dx = transformedEnd.X - transformedStart.X;
            var dy = transformedEnd.Y - transformedStart.Y;
            var curveCenter = new PointF((transformedStart.X + transformedEnd.X) * 0.5f, (transformedStart.Y + transformedEnd.Y) * 0.5f);
            var curveSize = new SizeF(
                Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, MathF.Sqrt(dx * dx + dy * dy)),
                Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2)));
            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Curve,
                destinationLayer,
                shape,
                curveCenter,
                curveSize,
                MathF.Atan2(dy, dx),
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                transformedStart,
                transformedControl,
                transformedEnd,
                [],
                [],
                source.GetLineEndpointStyle(sourceObject, startEndpoint: true),
                source.GetLineEndpointStyle(sourceObject, startEndpoint: false)), source, sourceObject, transform, identityTransform);
        }

        if (!identityTransform && HasShear(transform))
        {
            var skewContours = source.GetObjectBoundaryContours(sourceObject);
            for (var contourIndex = 0; contourIndex < skewContours.Length; contourIndex++)
            {
                var contour = skewContours[contourIndex];
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                {
                    contour[pointIndex] = TransformPoint(contour[pointIndex], transform, identityTransform);
                }
            }

            if (skewContours.Length > 0)
            {
                return WithGradient(new PreparedCompositionObject(
                    PreparedCompositionKind.Path,
                    destinationLayer,
                    ShapeKind.Path,
                    PointF.Empty,
                    SizeF.Empty,
                    0,
                    stroke,
                    fillArgb,
                    strokeArgb,
                    atoms,
                    PointF.Empty,
                    PointF.Empty,
                    PointF.Empty,
                    [],
                    skewContours), source, sourceObject, transform, identityTransform);
            }
        }

        if (identityTransform)
        {
            return WithGradient(new PreparedCompositionObject(
                PreparedCompositionKind.Primitive,
                destinationLayer,
                shape,
                new PointF(source.X[sourceObject], source.Y[sourceObject]),
                new SizeF(source.Width[sourceObject], source.Height[sourceObject]),
                source.Angle[sourceObject],
                stroke,
                fillArgb,
                strokeArgb,
                atoms,
                PointF.Empty,
                PointF.Empty,
                PointF.Empty,
                [],
                []), source, sourceObject, transform, identityTransform);
        }

        var center = Transform(new PointF(source.X[sourceObject], source.Y[sourceObject]), transform);
        var sourceAngle = source.Angle[sourceObject];
        var cos = MathF.Cos(sourceAngle);
        var sin = MathF.Sin(sourceAngle);
        var widthAxis = Vector2.TransformNormal(new Vector2(cos * source.Width[sourceObject], sin * source.Width[sourceObject]), transform);
        var heightAxis = Vector2.TransformNormal(new Vector2(-sin * source.Height[sourceObject], cos * source.Height[sourceObject]), transform);
        var size = new SizeF(Math.Max(1, widthAxis.Length()), Math.Max(1, heightAxis.Length()));
        var angle = MathF.Atan2(widthAxis.Y, widthAxis.X);
        return WithGradient(new PreparedCompositionObject(
            PreparedCompositionKind.Primitive,
            destinationLayer,
            shape,
            center,
            size,
            angle,
            stroke,
            fillArgb,
            strokeArgb,
            atoms,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            [],
            []), source, sourceObject, transform, identityTransform);
    }

    private static PackedSceneObject PreparePackedObject(CompositionWorkItem workItem)
    {
        var item = PrepareObject(workItem);
        if (item.Kind is PreparedCompositionKind.Path or PreparedCompositionKind.Freehand)
        {
            throw new InvalidOperationException("Path and freehand geometry cannot be written through the packed batch path.");
        }

        return new PackedSceneObject(
            item.DestinationLayer,
            item.Center,
            item.Size,
            item.Angle,
            item.Stroke,
            item.FillArgb,
            item.StrokeArgb,
            item.Atoms,
            item.Shape,
            item.Kind == PreparedCompositionKind.Curve ? item.Control : item.Center,
            item.StartEndpointStyle,
            item.EndEndpointStyle,
            item.LinearGradientEnabled,
            item.GradientKind,
            item.GradientStartArgb,
            item.GradientEndArgb,
            item.GradientStart,
            item.GradientEnd,
            item.GradientStops);
    }

    private static int AppendPreparedObject(VectorScene destination, PreparedCompositionObject item)
    {
        var index = item.Kind switch
        {
            PreparedCompositionKind.Path => destination.AppendPathObjectContours(
                item.DestinationLayer,
                item.Contours,
                item.Stroke,
                Color.FromArgb(item.FillArgb),
                Color.FromArgb(item.StrokeArgb),
                item.Atoms),
            PreparedCompositionKind.Freehand => destination.AppendFreehandStroke(
                item.DestinationLayer,
                item.Points,
                item.Stroke,
                Color.FromArgb(item.StrokeArgb),
                item.Atoms),
            PreparedCompositionKind.Curve => destination.AppendPackedObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.Stroke,
                item.FillArgb,
                item.StrokeArgb,
                item.Atoms,
                ShapeKind.Line,
                item.Control,
                item.StartEndpointStyle,
                item.EndEndpointStyle),
            _ => destination.AppendPackedObject(
                item.DestinationLayer,
                item.Center,
                item.Size,
                item.Angle,
                item.Stroke,
                item.FillArgb,
                item.StrokeArgb,
                item.Atoms,
                item.Shape,
                item.Center,
                item.StartEndpointStyle,
                item.EndEndpointStyle)
        };
        if (item.LinearGradientEnabled)
        {
            destination.SetGradientPaint(
                index,
                item.GradientKind,
                item.GradientStops,
                item.GradientStart,
                item.GradientEnd);
        }

        return index;
    }

    private static PreparedCompositionObject WithGradient(
        PreparedCompositionObject item,
        VectorScene source,
        int sourceObject,
        Matrix3x2 transform,
        bool identityTransform)
    {
        if (!source.HasGradient(sourceObject)) return item;
        return item with
        {
            LinearGradientEnabled = true,
            GradientKind = source.GetGradientKind(sourceObject),
            GradientStartArgb = source.GradientStartArgb[sourceObject],
            GradientEndArgb = source.GradientEndArgb[sourceObject],
            GradientStart = TransformPoint(source.GetGradientStart(sourceObject), transform, identityTransform),
            GradientEnd = TransformPoint(source.GetGradientEnd(sourceObject), transform, identityTransform),
            GradientStops = source.GetGradientStops(sourceObject)
        };
    }

    private static Matrix3x2 InstanceMatrix(DrawingObjectInstanceDefinition instance)
    {
        return Matrix3x2.CreateScale(instance.ScaleX, instance.ScaleY)
            * Matrix3x2.CreateSkew(instance.SkewX * MathF.PI / 180f, instance.SkewY * MathF.PI / 180f)
            * Matrix3x2.CreateRotation(instance.RotationZ * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(instance.X, instance.Y);
    }

    private static bool HasShear(Matrix3x2 transform)
    {
        var axisDot = transform.M11 * transform.M21 + transform.M12 * transform.M22;
        var scale = Math.Max(1, MathF.Sqrt(
            transform.M11 * transform.M11
            + transform.M12 * transform.M12
            + transform.M21 * transform.M21
            + transform.M22 * transform.M22));
        return Math.Abs(axisDot) > 0.0001f * scale * scale;
    }

    private static PointF Transform(PointF point, Matrix3x2 transform)
    {
        var transformed = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return VectorUnits.Quantize(new PointF(transformed.X, transformed.Y));
    }

    private static PointF TransformPoint(PointF point, Matrix3x2 transform, bool identityTransform)
    {
        return identityTransform ? VectorUnits.Quantize(point) : Transform(point, transform);
    }
}
