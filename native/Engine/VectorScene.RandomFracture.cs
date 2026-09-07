using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private readonly record struct RandomFractureStackKey(
        int KeyframeFrame,
        long Order,
        double SubOrder);

    private readonly record struct RandomFractureTerrainExposureKey(
        string LayerId,
        bool HasContent,
        int SourceKeyframeFrame,
        TimelineKeyframeKind? SourceKind);

    private sealed record RandomFracturePlan(
        int SourceObject,
        int SourceLayer,
        int SourceKeyframeFrame,
        int StartFrame,
        long SourceOrder,
        double SourceSubOrder,
        RandomFractureOptions Options,
        RandomFractureFragment[] Fragments,
        RandomFractureMotion[][] Motions,
        RectangleF SourceBounds,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        GradientPaintData? GradientPaint,
        PointF[][] CollisionTerrainContours);

    private sealed record RandomFracturePlanCacheEntry(
        long GeometryRevision,
        long ActiveContentRevision,
        int ObjectIndex,
        int Frame,
        RandomFractureOptions Options,
        RandomFracturePlan Plan);

    private sealed record RandomFractureTerrainCacheEntry(
        long GeometryRevision,
        long ActiveContentRevision,
        string[] TerrainLayerIds,
        int Frame,
        PointF[][] Contours);

    private readonly object _randomFractureCacheGate = new();
    private RandomFracturePlanCacheEntry? _randomFracturePlanCache;
    private RandomFractureTerrainCacheEntry? _randomFractureTerrainCache;

    // Preview-only metadata consumed by the stage overlay. The preview scene
    // contains fragments only, so the source scene remains the terrain owner.
    internal int? RandomFracturePreviewTerrainFrame { get; private set; }

    internal bool CanRandomFracture(int objectIndex, int frame, out string error)
    {
        error = string.Empty;
        if ((uint)objectIndex >= ObjectCount)
        {
            error = "Select a drawing object to fracture.";
            return false;
        }

        if (!IsObjectActive(objectIndex, frame))
        {
            error = "The selected object is not visible at the current frame.";
            return false;
        }

        var layer = ObjectLayer[objectIndex];
        if (GetLayerKind(layer) != DrawingLayerKind.Drawing || IsLayerEffectivelyLocked(layer))
        {
            error = "Random Fracture requires an unlocked drawing layer.";
            return false;
        }

        if (!IsRandomFractureShape(ShapeKind[objectIndex]) || !HasFill(objectIndex))
        {
            error = "Random Fracture supports closed filled shapes and paths only.";
            return false;
        }

        var contours = GetDistortedObjectBoundaryContours(objectIndex)
            .Where(contour => contour is { Length: >= 3 })
            .ToArray();
        if (contours.Length == 0)
        {
            error = "The selected object has no closed fill boundary.";
            return false;
        }

        return true;
    }

    internal bool TryCreateRandomFracturePreview(
        int objectIndex,
        int frame,
        RandomFractureOptions options,
        out VectorScene previewScene,
        out int[] hiddenSourceObjects,
        out string error,
        int previewFrame = -1,
        CancellationToken cancellationToken = default)
    {
        var normalized = options.Normalize();
        var defaultPreviewFrame = normalized.GenerateAnimation ? normalized.AnimationFrames : 0;
        return TryCreateRandomFracturePreview(
            objectIndex,
            frame,
            previewFrame < 0 ? defaultPreviewFrame : previewFrame,
            normalized,
            out previewScene,
            out hiddenSourceObjects,
            out error,
            cancellationToken);
    }

    internal bool TryCreateRandomFracturePreview(
        int objectIndex,
        int frame,
        int previewFrame,
        RandomFractureOptions options,
        out VectorScene previewScene,
        out int[] hiddenSourceObjects,
        out string error,
        CancellationToken cancellationToken = default)
    {
        previewScene = null!;
        hiddenSourceObjects = [];
        if (!TryBuildRandomFracturePlan(
                objectIndex,
                frame,
                options,
                out var plan,
                out error,
                cancellationToken))
        {
            return false;
        }

        previewScene = BuildRandomFracturePreviewScene(plan, previewFrame, cancellationToken);
        hiddenSourceObjects = [objectIndex];
        return previewScene.ObjectCount > 0;
    }

    internal bool TryCreateRandomFracturePreview(
        int objectIndex,
        int frame,
        RandomFractureOptions options,
        int previewFrame,
        out VectorScene previewScene,
        out int[] hiddenSourceObjects,
        out string error)
    {
        return TryCreateRandomFracturePreview(
            objectIndex,
            frame,
            previewFrame,
            options,
            out previewScene,
            out hiddenSourceObjects,
            out error);
    }

    internal bool TryApplyRandomFracture(
        int objectIndex,
        int frame,
        RandomFractureOptions options,
        out int[] producedObjects,
        out string error)
    {
        producedObjects = [];
        error = string.Empty;
        var rollback = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            if ((uint)objectIndex >= ObjectCount)
            {
                error = "The selected object is no longer available.";
                return false;
            }

            var sourceKey = new RandomFractureStackKey(
                ObjectKeyframeFrame[objectIndex],
                ObjectOrder[objectIndex],
                ObjectSubOrder[objectIndex]);
            var layer = ObjectLayer[objectIndex];
            EditFrame = Math.Max(0, frame);
            if (sourceKey.KeyframeFrame != EditFrame)
            {
                MaterializeAutoKeyframeInPlace(layer, EditFrame);
                objectIndex = FindObjectByRandomFractureStackKey(
                    layer,
                    EditFrame,
                    sourceKey.Order,
                    sourceKey.SubOrder);
                if (objectIndex < 0)
                {
                    error = "The selected object could not be materialized at the current frame.";
                    RestoreSnapshot(rollback);
                    return false;
                }
            }

            if (!TryBuildRandomFracturePlan(objectIndex, frame, options, out var plan, out error))
            {
                RestoreSnapshot(rollback);
                return false;
            }

            var sourceKeyframe = plan.StartFrame;
            var sourceCopies = Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == plan.SourceLayer
                    && ObjectOrder[index] == plan.SourceOrder
                    && ObjectSubOrder[index].Equals(plan.SourceSubOrder))
                .ToArray();
            if (sourceCopies.Length == 0)
            {
                throw new InvalidOperationException("Random Fracture could not find the source object stack key.");
            }

            RemoveObjects(sourceCopies);
            var fragmentLayers = CreateRandomFractureLayers(plan.SourceLayer, plan.Fragments.Length);
            var startObjects = AppendRandomFractureObjects(
                plan,
                fragmentLayers,
                sourceKeyframe,
                plan.Motions[0]);
            if (startObjects.Length == 0)
            {
                throw new InvalidOperationException("Random Fracture produced no drawable fragments.");
            }

            if (plan.Options.GenerateAnimation && plan.Motions.Length > 1)
            {
                WriteRandomFractureAnimation(
                    plan,
                    fragmentLayers,
                    startObjects);
            }

            SynchronizeAllKeyframeContentKinds();
            RebuildGeometryIndex();
            RebuildSummaries();
            producedObjects = startObjects;
            return true;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ArgumentException or OverflowException)
        {
            RestoreSnapshot(rollback);
            producedObjects = [];
            error = $"The fracture could not be applied: {exception.Message}";
            return false;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    private bool TryBuildRandomFracturePlan(
        int objectIndex,
        int frame,
        RandomFractureOptions options,
        out RandomFracturePlan plan,
        out string error,
        CancellationToken cancellationToken = default)
    {
        plan = null!;
        if (!CanRandomFracture(objectIndex, frame, out error)) return false;

        options = options.Normalize();
        lock (_randomFractureCacheGate)
        {
            var cached = _randomFracturePlanCache;
            if (cached is not null
                && cached.GeometryRevision == GeometryRevision
                && cached.ActiveContentRevision == ActiveContentRevision
                && cached.ObjectIndex == objectIndex
                && cached.Frame == frame
                && cached.Options.Equals(options))
            {
                plan = cached.Plan;
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var contours = GetDistortedObjectBoundaryContours(objectIndex)
            .Where(contour => contour is { Length: >= 3 })
            .ToArray();
        if (!RandomFractureGenerator.TryGenerate(
                contours,
                options,
                out var fragments,
                out var sourceBounds,
                out error,
                cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PointF[][]? collisionTerrainContours = null;
        IReadOnlyList<PointF[][]?>? collisionTerrainContoursByFrame = null;
        if (options.GenerateAnimation)
        {
            collisionTerrainContoursByFrame = GetCollisionTerrainContoursByFrame(
                frame,
                checked(options.AnimationFrames + 1),
                cancellationToken);
            collisionTerrainContours = collisionTerrainContoursByFrame is { Count: > 0 }
                ? collisionTerrainContoursByFrame[0]
                : null;
        }

        var motions = RandomFractureGenerator.Simulate(
            fragments,
            sourceBounds,
            options,
            collisionTerrainContours,
            collisionTerrainContoursByFrame,
            cancellationToken);
        var preserveStroke = options.PreserveStroke && HasStroke(objectIndex);
        var generatedPlan = new RandomFracturePlan(
            objectIndex,
            ObjectLayer[objectIndex],
            ObjectKeyframeFrame[objectIndex],
            Math.Max(0, frame),
            ObjectOrder[objectIndex],
            ObjectSubOrder[objectIndex],
            options,
            fragments,
            motions,
            sourceBounds,
            preserveStroke ? Stroke[objectIndex] : 0,
            Color.FromArgb(Argb[objectIndex]),
            preserveStroke ? Color.FromArgb(StrokeArgb[objectIndex]) : Color.Transparent,
            Math.Max(3u, AtomCount[objectIndex]),
            CaptureGradientPaint(objectIndex),
            collisionTerrainContours ?? []);
        lock (_randomFractureCacheGate)
        {
            _randomFracturePlanCache = new RandomFracturePlanCacheEntry(
                GeometryRevision,
                ActiveContentRevision,
                objectIndex,
                frame,
                options,
                generatedPlan);
        }

        plan = generatedPlan;
        return true;
    }

    private VectorScene BuildRandomFracturePreviewScene(
        RandomFracturePlan plan,
        int previewFrame,
        CancellationToken cancellationToken = default)
    {
        var preview = new VectorScene();
        var motionFrame = plan.Motions.Length == 0
            ? 0
            : Math.Clamp(previewFrame, 0, plan.Motions.Length - 1);
        preview.RandomFracturePreviewTerrainFrame = plan.Options.GenerateAnimation
            ? checked(plan.StartFrame + motionFrame)
            : null;
        var previewDuration = Math.Max(
            Math.Max(1, FrameCount),
            motionFrame == int.MaxValue ? int.MaxValue : motionFrame + 1);
        preview.CreateEmpty(Math.Max(1, LayerCount), previewDuration);
        preview.ActiveLayer = plan.SourceLayer;
        preview.EditFrame = 0;
        var previewLayerIds = preview.LayerIds.ToArray();
        var sourceLayerIndexById = LayerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var layer = 0; layer < LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            preview.LayerNames[layer] = LayerNames[layer];
            preview.LayerKinds[layer] = LayerKinds[layer];
            preview.LayerVisible[layer] = LayerVisible[layer];
            preview.LayerLocked[layer] = LayerLocked[layer];
            preview.LayerOpacity[layer] = LayerOpacity[layer];
            preview.LayerBlendModes[layer] = LayerBlendModes[layer];
            preview.LayerColorArgb[layer] = LayerColorArgb[layer];
            preview.LayerOutline[layer] = LayerOutline[layer];
            preview.LayerStart[layer] = LayerStart[layer];
            preview.LayerEnd[layer] = LayerEnd[layer];
            if (layer < LayerParentIds.Length
                && sourceLayerIndexById.TryGetValue(LayerParentIds[layer], out var parentIndex))
            {
                preview.LayerParentIds[layer] = previewLayerIds[parentIndex];
            }

            if (layer < LayerMaskIds.Length
                && sourceLayerIndexById.TryGetValue(LayerMaskIds[layer], out var maskIndex))
            {
                preview.LayerMaskIds[layer] = previewLayerIds[maskIndex];
            }
        }

        var folderLayer = preview.InsertLayer(
            DrawingLayerKind.Folder,
            preview.LayerCount,
            "Random Fracture");
        var folderId = preview.LayerIds[folderLayer];
        if (plan.SourceLayer < preview.LayerParentIds.Length)
        {
            preview.LayerParentIds[folderLayer] = preview.LayerParentIds[plan.SourceLayer];
        }

        var fragmentLayers = new int[plan.Fragments.Length];
        for (var index = 0; index < fragmentLayers.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fragmentLayer = preview.InsertLayer(
                DrawingLayerKind.Drawing,
                folderLayer + 1 + index,
                $"Fragment {index + 1:0000}");
            preview.LayerParentIds[fragmentLayer] = folderId;
            fragmentLayers[index] = fragmentLayer;
        }

        var motion = plan.Motions[motionFrame];
        for (var index = 0; index < plan.Fragments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendRandomFractureObject(
                preview,
                plan,
                fragmentLayers[index],
                plan.Fragments[index],
                keyframeFrame: 0,
                subOrder: index == 0 ? plan.SourceSubOrder : index,
                motion[index]);
        }

        preview.SynchronizeAllKeyframeContentKinds();
        preview.RebuildGeometryIndex();
        preview.RebuildSummaries();
        return preview;
    }

    private int[] AppendRandomFractureObjects(
        RandomFracturePlan plan,
        IReadOnlyList<int> fragmentLayers,
        int keyframeFrame,
        IReadOnlyList<RandomFractureMotion> motions)
    {
        if (fragmentLayers.Count != plan.Fragments.Length || motions.Count != plan.Fragments.Length)
        {
            throw new ArgumentException("Random Fracture replacement metadata is incomplete.");
        }

        var created = new int[plan.Fragments.Length];
        for (var index = 0; index < plan.Fragments.Length; index++)
        {
            created[index] = AppendRandomFractureObject(
                this,
                plan,
                fragmentLayers[index],
                plan.Fragments[index],
                keyframeFrame,
                index == 0 ? plan.SourceSubOrder : index,
                motions[index]);
            if (created[index] < 0)
            {
                throw new InvalidOperationException("Random Fracture produced invalid fragment geometry.");
            }
        }

        CompleteIncrementalObjectAppends(created);
        return created;
    }

    private static int AppendRandomFractureObject(
        VectorScene destination,
        RandomFracturePlan plan,
        int targetLayer,
        RandomFractureFragment fragment,
        int keyframeFrame,
        double subOrder,
        RandomFractureMotion motion)
    {
        var previousEditFrame = destination.EditFrame;
        destination.EditFrame = Math.Max(0, keyframeFrame);
        try
        {
            EnsureRandomFractureKeyframe(destination, targetLayer, keyframeFrame);
            var created = destination.AppendPathObjectContours(
                targetLayer,
                fragment.Contours,
                plan.Stroke,
                plan.FillColor,
                plan.StrokeColor,
                FragmentAtoms(plan.Atoms, fragment));
            if (created < 0) return -1;

            destination.ObjectKeyframeFrame[created] = keyframeFrame;
            destination.ObjectOrder[created] = plan.SourceOrder;
            destination.ObjectSubOrder[created] = subOrder;
            destination.X[created] = VectorUnits.Quantize(fragment.Center.X + motion.Offset.X);
            destination.Y[created] = VectorUnits.Quantize(fragment.Center.Y + motion.Offset.Y);
            destination.Angle[created] = motion.Rotation;
            // Modifier output must stay as independent stage objects. Protecting
            // the fill also keeps a later automatic merge from hiding fragments.
            destination.FillAutoMergeProtected[created] = true;

            ApplyRandomFractureGradient(destination, created, plan, fragment, motion);

            return created;
        }
        finally
        {
            destination.EditFrame = previousEditFrame;
        }
    }

    private void WriteRandomFractureAnimation(
        RandomFracturePlan plan,
        IReadOnlyList<int> fragmentLayers,
        IReadOnlyList<int> startObjects)
    {
        if (fragmentLayers.Count != plan.Fragments.Length
            || startObjects.Count != plan.Fragments.Length)
        {
            throw new InvalidOperationException("Random Fracture could not capture the fragment animation base frame.");
        }

        for (var motionFrame = 1; motionFrame < plan.Motions.Length; motionFrame++)
        {
            var targetFrame = checked(plan.StartFrame + motionFrame);
            var created = new int[fragmentLayers.Count];
            for (var fragmentIndex = 0; fragmentIndex < fragmentLayers.Count; fragmentIndex++)
            {
                created[fragmentIndex] = OverwriteRandomFractureKeyframe(
                    plan,
                    fragmentLayers[fragmentIndex],
                    targetFrame,
                    fragmentIndex,
                    plan.Motions[motionFrame][fragmentIndex]);
            }

            CompleteIncrementalObjectAppends(created);
        }
    }

    private int OverwriteRandomFractureKeyframe(
        RandomFracturePlan plan,
        int layer,
        int frame,
        int fragmentIndex,
        RandomFractureMotion motion)
    {
        if ((uint)fragmentIndex >= plan.Fragments.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentIndex));
        }
        RemoveObjectsForKeyframe(layer, frame);
        var created = AppendRandomFractureObject(
            this,
            plan,
            layer,
            plan.Fragments[fragmentIndex],
            frame,
            fragmentIndex == 0 ? plan.SourceSubOrder : fragmentIndex,
            motion);
        if (created < 0)
        {
            throw new InvalidOperationException("The fracture animation keyframe could not be created.");
        }

        return created;
    }

    private int[] CreateRandomFractureLayers(int sourceLayer, int fragmentCount)
    {
        if ((uint)sourceLayer >= LayerCount || fragmentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentCount));
        }

        var parentId = LayerParentIds[sourceLayer];
        var folderLayer = InsertLayer(
            DrawingLayerKind.Folder,
            sourceLayer + 1,
            "Random Fracture");
        var folderId = LayerIds[folderLayer];
        LayerParentIds[folderLayer] = parentId;

        var fragmentLayers = new int[fragmentCount];
        for (var index = 0; index < fragmentCount; index++)
        {
            var fragmentLayer = InsertLayer(
                DrawingLayerKind.Drawing,
                folderLayer + 1 + index,
                $"Fragment {index + 1:0000}");
            LayerParentIds[fragmentLayer] = folderId;
            fragmentLayers[index] = fragmentLayer;
        }

        ActiveLayer = folderLayer;
        InvalidateQueryActiveKeyframes();
        return fragmentLayers;
    }

    private static void EnsureRandomFractureKeyframe(VectorScene destination, int layer, int frame)
    {
        var track = destination.TimelineTrackForLayer(layer)
            ?? throw new InvalidOperationException("The fracture layer has no timeline track.");
        frame = Math.Max(0, frame);
        if (frame >= track.Duration) destination.Timeline.SetTrackDuration(track.Id, frame + 1);

        var exposure = track.EvaluateExposure(frame);
        if (exposure.IsKeyframe && exposure.SourceKind == TimelineKeyframeKind.Populated) return;
        if (!destination.Timeline.InsertKeyframe(track.Id, frame)
            && (!track.EvaluateExposure(frame).IsKeyframe
                || track.EvaluateExposure(frame).SourceKind != TimelineKeyframeKind.Populated))
        {
            throw new InvalidOperationException("The fracture animation keyframe could not be created.");
        }
    }

    private PointF[][] GetCollisionTerrainContours(
        int frame,
        CancellationToken cancellationToken = default)
    {
        var terrainLayers = GetCollisionTerrainLayers();
        if (terrainLayers.Length == 0) return [];
        var terrainLayerIds = terrainLayers
            .Select(layer => LayerIds[layer])
            .ToArray();

        lock (_randomFractureCacheGate)
        {
            var cached = _randomFractureTerrainCache;
            if (cached is not null
                && cached.GeometryRevision == GeometryRevision
                && cached.ActiveContentRevision == ActiveContentRevision
                && cached.TerrainLayerIds.SequenceEqual(terrainLayerIds)
                && cached.Frame == frame)
            {
                return cached.Contours;
            }
        }

        var terrainPaths = new Paths64();
        var fallbackContours = new List<PointF[]>();
        foreach (var objectIndex in Enumerable.Range(0, ObjectCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCollisionTerrainLayer(ObjectLayer[objectIndex])
                || !IsObjectActive(objectIndex, frame)
                || !IsFillShape(ShapeKind[objectIndex]))
            {
                continue;
            }

            var objectContours = GetDistortedObjectBoundaryContours(objectIndex)
                .Select(NormalizeCollisionTerrainContour)
                .Where(contour => contour.Length >= 3)
                .ToArray();
            fallbackContours.AddRange(objectContours);
            var objectPaths = ToClipperPaths(objectContours);
            if (objectPaths.Count == 0) continue;

            try
            {
                // Each terrain object uses the same even-odd fill semantics as
                // the editor. Normalize it first, then merge separate terrain
                // objects with non-zero union semantics so overlaps stay solid.
                var objectRegion = Clipper.Union(objectPaths, FillRule.EvenOdd);
                terrainPaths.AddRange(objectRegion);
            }
            catch (Exception exception) when (exception is
                ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
            {
                // Keep valid fallback contours available if one legacy object is
                // malformed; the other terrain objects remain usable.
            }
        }

        PointF[][] contours;
        if (terrainPaths.Count == 0)
        {
            contours = fallbackContours.ToArray();
        }
        else
        {
            try
            {
                var union = Clipper.Union(terrainPaths, FillRule.NonZero);
                var normalized = FromClipperPaths(union);
                contours = normalized.Length > 0 ? normalized : fallbackContours.ToArray();
            }
            catch (Exception exception) when (exception is
                ClipperLibException or OverflowException or ArgumentException or InvalidOperationException)
            {
                contours = fallbackContours.ToArray();
            }
        }

        try
        {
            lock (_randomFractureCacheGate)
            {
                _randomFractureTerrainCache = new RandomFractureTerrainCacheEntry(
                    GeometryRevision,
                    ActiveContentRevision,
                    terrainLayerIds,
                    frame,
                    contours);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            // The cache is an optimization; the extracted contours remain valid.
        }

        return contours;
    }

    private PointF[][]?[]? GetCollisionTerrainContoursByFrame(
        int startFrame,
        int frameCount,
        CancellationToken cancellationToken = default)
    {
        var terrainLayers = GetCollisionTerrainLayers();
        if (terrainLayers.Length == 0) return null;

        frameCount = Math.Max(1, frameCount);
        var contoursByFrame = new PointF[][]?[frameCount];
        RandomFractureTerrainExposureKey[]? previousExposureKeys = null;
        PointF[][]? previousContours = null;
        for (var relativeFrame = 0; relativeFrame < frameCount; relativeFrame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteFrame = checked(Math.Max(0, startFrame) + relativeFrame);

            var exposureKeys = terrainLayers
                .Select(layer =>
                {
                    var exposure = Timeline.EvaluateTargetExposure(
                        LayerIds[layer],
                        absoluteFrame);
                    return new RandomFractureTerrainExposureKey(
                        LayerIds[layer],
                        exposure.HasContent,
                        exposure.SourceKeyframeFrame,
                        exposure.SourceKind);
                })
                .ToArray();
            var held = previousExposureKeys is not null
                && TerrainExposureKeysEqual(previousExposureKeys, exposureKeys);
            if (held && previousContours is not null)
            {
                contoursByFrame[relativeFrame] = previousContours;
                continue;
            }

            var contours = GetCollisionTerrainContours(absoluteFrame, cancellationToken);
            contoursByFrame[relativeFrame] = contours;
            previousExposureKeys = exposureKeys;
            previousContours = contours;
        }

        return contoursByFrame;
    }

    private static bool TerrainExposureKeysEqual(
        IReadOnlyList<RandomFractureTerrainExposureKey> first,
        IReadOnlyList<RandomFractureTerrainExposureKey> second)
    {
        if (first.Count != second.Count) return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index]) return false;
        }

        return true;
    }

    private static PointF[] NormalizeCollisionTerrainContour(IReadOnlyList<PointF> contour)
    {
        if (contour is null || contour.Count < 3) return [];

        var points = new List<PointF>(contour.Count);
        foreach (var point in contour)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            if (points.Count == 0
                || DistanceSquared(points[^1], point) > 0.000001f)
            {
                points.Add(point);
            }
        }

        if (points.Count > 1 && DistanceSquared(points[0], points[^1]) <= 0.000001f)
        {
            points.RemoveAt(points.Count - 1);
        }

        return points.Count >= 3 ? points.ToArray() : [];
    }

    private int FindObjectByRandomFractureStackKey(
        int layer,
        int keyframeFrame,
        long order,
        double subOrder)
    {
        for (var index = 0; index < ObjectCount; index++)
        {
            if (ObjectLayer[index] == layer
                && ObjectKeyframeFrame[index] == keyframeFrame
                && ObjectOrder[index] == order
                && ObjectSubOrder[index].Equals(subOrder))
            {
                return index;
            }
        }

        return -1;
    }

    private static uint FragmentAtoms(uint sourceAtoms, RandomFractureFragment fragment)
    {
        var scaled = sourceAtoms * Math.Clamp(fragment.Area, 0.02d, 1d);
        return Math.Max(3u, (uint)Math.Clamp(Math.Round(scaled), 3d, uint.MaxValue));
    }

    private static bool IsRandomFractureShape(VectorAnimationEngine.ShapeKind shape)
        => shape is VectorAnimationEngine.ShapeKind.Rectangle
            or VectorAnimationEngine.ShapeKind.Ellipse
            or VectorAnimationEngine.ShapeKind.Triangle
            or VectorAnimationEngine.ShapeKind.Polygon
            or VectorAnimationEngine.ShapeKind.Star
            or VectorAnimationEngine.ShapeKind.Path;

    private static void ApplyRandomFractureGradient(
        VectorScene destination,
        int objectIndex,
        RandomFracturePlan plan,
        RandomFractureFragment fragment,
        RandomFractureMotion motion)
    {
        if (plan.GradientPaint is not { } paint) return;

        destination.SetGradientPaint(
            objectIndex,
            paint.Kind,
            paint.Stops,
            TransformPoint(paint.Start, fragment.Center, motion),
            TransformPoint(paint.End, fragment.Center, motion));
        if (paint.Kind == GradientKind.ShapeRadial)
        {
            var mappingContours = paint.ShapeMappingContours.Length > 0
                ? paint.ShapeMappingContours
                : fragment.Contours;
            destination.SetShapeGradientMapping(
                objectIndex,
                TransformContours(mappingContours, fragment.Center, motion));
        }
        else if (paint.Path.Length > 1)
        {
            destination.SetOrderedGradientPath(
                objectIndex,
                paint.Path.Select(point => TransformPoint(point, fragment.Center, motion)).ToArray());
        }
    }

    private static PointF TransformPoint(PointF point, PointF center, RandomFractureMotion motion)
    {
        var sine = MathF.Sin(motion.Rotation);
        var cosine = MathF.Cos(motion.Rotation);
        var localX = point.X - center.X;
        var localY = point.Y - center.Y;
        return new PointF(
            center.X + motion.Offset.X + localX * cosine - localY * sine,
            center.Y + motion.Offset.Y + localX * sine + localY * cosine);
    }

    private static PointF[][] TransformContours(
        IReadOnlyList<PointF[]> contours,
        PointF center,
        RandomFractureMotion motion)
        => contours
            .Select(contour => contour
                .Select(point => TransformPoint(point, center, motion))
                .ToArray())
            .ToArray();
}
