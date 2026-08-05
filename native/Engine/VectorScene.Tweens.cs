using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private sealed record ShapeTweenGeometryPlan(
        bool Closed,
        PointF[][] SourceContours,
        PointF[][] TargetContours,
        int[] ContourDepths);

    private sealed record ShapeTweenContourDescriptor(
        int OriginalIndex,
        PointF[] Points,
        PointF Center,
        PointF NormalizedCenter,
        float AreaRatio,
        float PerimeterRatio,
        int Depth);

    private sealed record ShapeTweenContourMatch(
        ShapeTweenContourDescriptor? Source,
        ShapeTweenContourDescriptor? Target);

    private sealed record ShapeTweenObjectDescriptor(
        int OriginalIndex,
        int ObjectIndex,
        bool Closed,
        PointF NormalizedCenter,
        float MeasureRatio,
        float PerimeterRatio);

    private sealed record ShapeTweenObjectMatch(
        ShapeTweenObjectDescriptor? Source,
        ShapeTweenObjectDescriptor? Target);

    private sealed record ShapeTweenObjectPlan(
        int? Source,
        int? Target,
        ShapeTweenGeometryPlan Geometry,
        long ObjectOrder,
        double ObjectSubOrder);

    /// <summary>
    /// Creates a timeline tween and materializes its interpolated cels.  The
    /// timeline keeps the span metadata while the cels keep the editable scene
    /// compatible with the existing renderer, hit testing, and undo model.
    /// </summary>
    internal bool TryCreateTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out string error)
    {
        if (!TryPrepareTimelineTween(
                layer,
                startFrame,
                endFrame,
                kind,
                out var track,
                out var sourceObjects,
                out var targetObjects,
                out var shapePlans,
                out error))
        {
            return false;
        }
        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            using var batch = Timeline.BeginBatchUpdate();
            for (var frame = startFrame + 1; frame < endFrame; frame++)
            {
                if (!Timeline.InsertKeyframe(track.Id, frame))
                {
                    throw new InvalidOperationException("The tween span could not create an intermediate keyframe.");
                }

                AppendTimelineTweenFrame(
                    layer,
                    frame,
                    kind,
                    sourceObjects,
                    targetObjects,
                    shapePlans,
                    TweenProgress(startFrame, endFrame, frame));
            }

            if (!Timeline.TryCreateTween(track.Id, startFrame, endFrame, kind, out var validation))
            {
                throw new InvalidOperationException($"The tween span is invalid ({validation}).");
            }

            RefreshLegacyExposureBounds(layer);
            RebuildGeometryIndex();
            RebuildSummaries();
            return true;
        }
        catch (Exception exception)
        {
            RestoreSnapshot(snapshot);
            error = exception.Message;
            return false;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    internal bool CanCreateTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out string error)
    {
        return TryPrepareTimelineTween(
            layer,
            startFrame,
            endFrame,
            kind,
            out _,
            out _,
            out _,
            out _,
            out error);
    }

    private bool TryPrepareTimelineTween(
        int layer,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out AnimationTimelineTrack track,
        out int[] sourceObjects,
        out int[] targetObjects,
        out ShapeTweenObjectPlan[]? shapePlans,
        out string error)
    {
        track = null!;
        sourceObjects = [];
        targetObjects = [];
        shapePlans = null;
        error = string.Empty;
        if ((uint)layer >= LayerCount)
        {
            error = "Select a drawing layer.";
            return false;
        }

        SynchronizeTimelineTracks();
        var resolvedTrack = Timeline.FindTrackByTargetId(LayerIds[layer]);
        if (resolvedTrack is null)
        {
            error = "The selected layer has no timeline track.";
            return false;
        }
        track = resolvedTrack;

        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);
        if (endFrame <= startFrame)
        {
            error = "Select a span containing a start and end frame.";
            return false;
        }

        var startExposure = track.EvaluateExposure(startFrame);
        var endExposure = track.EvaluateExposure(endFrame);
        if (!startExposure.IsKeyframe || !endExposure.IsKeyframe
            || !startExposure.HasContent || !endExposure.HasContent)
        {
            error = "Both ends of the span must be populated keyframes.";
            return false;
        }

        if (track.Keyframes.Any(keyframe =>
                keyframe.Frame > startFrame
                && keyframe.Frame < endFrame))
        {
            error = "Remove intermediate keyframes before creating a tween.";
            return false;
        }

        sourceObjects = ObjectsForKeyframe(layer, startExposure.SourceKeyframeFrame);
        targetObjects = ObjectsForKeyframe(layer, endExposure.SourceKeyframeFrame);
        if (kind == TimelineTweenKind.Classic
            && (sourceObjects.Length != 1 || targetObjects.Length != 1))
        {
            error = "Classic tweens require one drawing object at both endpoints.";
            return false;
        }
        if (kind == TimelineTweenKind.Shape
            && (sourceObjects.Length == 0 || targetObjects.Length == 0))
        {
            error = "Shape tweens require vector shapes at both endpoints.";
            return false;
        }

        if (kind == TimelineTweenKind.Classic)
        {
            for (var objectIndex = 0; objectIndex < sourceObjects.Length; objectIndex++)
            {
                if (!CanInterpolateGeometry(
                        sourceObjects[objectIndex],
                        targetObjects[objectIndex],
                        kind,
                        out var geometryError))
                {
                    error = geometryError;
                    return false;
                }
            }
        }

        if (kind == TimelineTweenKind.Shape)
        {
            if (!TryCreateShapeTweenObjectPlans(sourceObjects, targetObjects, out shapePlans, out error))
            {
                return false;
            }
        }

        if (!track.CanCreateTween(startFrame, endFrame, kind, out var validation))
        {
            error = $"The tween span is invalid ({validation}).";
            return false;
        }

        return true;
    }

    internal int TimelineObjectCountForKeyframe(int layer, int keyframe)
    {
        if ((uint)layer >= LayerCount || keyframe < 0) return 0;
        var count = 0;
        for (var index = 0; index < ObjectCount; index++)
        {
            if (ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframe) count++;
        }
        return count;
    }

    private int[] ObjectsForKeyframe(int layer, int keyframe)
    {
        return Enumerable.Range(0, ObjectCount)
            .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframe)
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
    }

    private void AppendTimelineTweenFrame(
        int layer,
        int frame,
        TimelineTweenKind kind,
        int[] sourceObjects,
        int[] targetObjects,
        ShapeTweenObjectPlan[]? shapePlans,
        float progress,
        int startObjectIndex = 0)
    {
        var materializedObjectCount = shapePlans?.Length ?? sourceObjects.Length;
        startObjectIndex = Math.Clamp(startObjectIndex, 0, materializedObjectCount);
        EnsureObjectCapacity(ObjectCount + materializedObjectCount - startObjectIndex);
        for (var objectIndex = startObjectIndex; objectIndex < materializedObjectCount; objectIndex++)
        {
            var shapePlan = shapePlans?[objectIndex];
            int? source = shapePlan is null ? sourceObjects[objectIndex] : shapePlan.Source;
            int? target = shapePlan is null ? targetObjects[objectIndex] : shapePlan.Target;
            var template = source ?? target
                ?? throw new InvalidOperationException("A tween object plan has no endpoint geometry.");
            var destination = ObjectCount++;
            CopyObjectData(template, destination);
            ObjectLayer[destination] = (ushort)layer;
            ObjectKeyframeFrame[destination] = frame;
            ObjectOrder[destination] = shapePlan?.ObjectOrder ?? ObjectOrder[source!.Value];
            ObjectSubOrder[destination] = shapePlan?.ObjectSubOrder ?? ObjectSubOrder[source!.Value];
            VirtualAtomCount += AtomCount[destination];
            if (shapePlan is not null)
            {
                InterpolateShapeObject(shapePlan, destination, progress);
            }
            else
            {
                InterpolateObject(source!.Value, target!.Value, destination, progress, kind);
            }
        }
    }

    private void RefreshTimelineTweenObject(
        int layer,
        int frame,
        TimelineTweenKind kind,
        int[] sourceObjects,
        int[] targetObjects,
        ShapeTweenObjectPlan[]? shapePlans,
        int objectIndex,
        int destination,
        float progress)
    {
        var shapePlan = shapePlans?[objectIndex];
        int? source = shapePlan is null ? sourceObjects[objectIndex] : shapePlan.Source;
        int? target = shapePlan is null ? targetObjects[objectIndex] : shapePlan.Target;
        var template = source ?? target
            ?? throw new InvalidOperationException("A tween object plan has no endpoint geometry.");
        var previousAtoms = AtomCount[destination];
        CopyObjectData(template, destination);
        ObjectLayer[destination] = (ushort)layer;
        ObjectKeyframeFrame[destination] = frame;
        ObjectOrder[destination] = shapePlan?.ObjectOrder ?? ObjectOrder[source!.Value];
        ObjectSubOrder[destination] = shapePlan?.ObjectSubOrder ?? ObjectSubOrder[source!.Value];
        VirtualAtomCount = Math.Max(0, VirtualAtomCount + AtomCount[destination] - previousAtoms);
        if (shapePlan is not null)
        {
            InterpolateShapeObject(shapePlan, destination, progress);
        }
        else
        {
            InterpolateObject(source!.Value, target!.Value, destination, progress, kind);
        }
    }

    private void HideTimelineTweenObject(int destination)
    {
        ScaleTweenObjectAlpha(destination, destination, 0);
        Stroke[destination] = 0;
    }

    private bool TryCreateShapeTweenObjectPlans(
        int[] sourceObjects,
        int[] targetObjects,
        out ShapeTweenObjectPlan[] plans,
        out string error)
    {
        plans = [];
        error = string.Empty;
        if (!TryDescribeShapeTweenObjects(sourceObjects, out var source, out error)
            || !TryDescribeShapeTweenObjects(targetObjects, out var target, out error))
        {
            return false;
        }

        var matches = MatchShapeTweenObjects(source, target);
        if (!matches.Any(match => match.Source is not null && match.Target is not null))
        {
            error = "Shape tween endpoints must share a compatible closed shape or open stroke.";
            return false;
        }

        var prepared = new List<ShapeTweenObjectPlan>(matches.Length);
        foreach (var match in matches)
        {
            var sourceObject = match.Source?.ObjectIndex;
            var targetObject = match.Target?.ObjectIndex;
            if (sourceObject.HasValue
                && targetObject.HasValue
                && !CanInterpolateGeometry(
                    sourceObject.Value,
                    targetObject.Value,
                    TimelineTweenKind.Shape,
                    out error))
            {
                return false;
            }

            if (!TryCreateShapeTweenGeometryPlan(sourceObject, targetObject, out var geometry))
            {
                error = "Shape tween endpoints could not be normalized into compatible edges.";
                return false;
            }

            var template = sourceObject ?? targetObject
                ?? throw new InvalidOperationException("A shape tween match has no endpoint object.");
            prepared.Add(new ShapeTweenObjectPlan(
                sourceObject,
                targetObject,
                geometry,
                ObjectOrder[template],
                ObjectSubOrder[template]));
        }

        plans = prepared
            .OrderBy(plan => plan.ObjectOrder)
            .ThenBy(plan => plan.ObjectSubOrder)
            .ThenBy(plan => plan.Source ?? int.MaxValue)
            .ThenBy(plan => plan.Target ?? int.MaxValue)
            .ToArray();
        return plans.Length > 0;
    }

    private bool TryDescribeShapeTweenObjects(
        int[] objectIndices,
        out ShapeTweenObjectDescriptor[] descriptors,
        out string error)
    {
        descriptors = [];
        error = string.Empty;
        var raw = new List<(
            int OriginalIndex,
            int ObjectIndex,
            bool Closed,
            PointF[][] Contours,
            PointF Center,
            float Measure,
            float Perimeter)>(objectIndices.Length);
        for (var originalIndex = 0; originalIndex < objectIndices.Length; originalIndex++)
        {
            var objectIndex = objectIndices[originalIndex];
            if (!TryGetShapeTweenContours(objectIndex, out var contours, out var closed)
                || contours.Length == 0)
            {
                error = "Shape tweens require editable vector shapes or strokes.";
                return false;
            }

            var points = contours.SelectMany(contour => contour).ToArray();
            if (points.Length < (closed ? 3 : 2))
            {
                error = "Shape tween endpoints require compatible vector contours.";
                return false;
            }

            var center = new PointF(
                points.Average(point => point.X),
                points.Average(point => point.Y));
            var perimeter = contours.Sum(TweenContourPerimeter);
            var measure = closed
                ? contours.Sum(contour => Math.Abs(TweenSignedArea(contour)))
                : perimeter;
            raw.Add((
                originalIndex,
                objectIndex,
                closed,
                contours,
                center,
                Math.Max(0.001f, measure),
                Math.Max(0.001f, perimeter)));
        }

        if (raw.Count == 0) return false;
        var bounds = ContourBounds(raw.SelectMany(item => item.Contours).ToArray());
        descriptors = raw.Select(item =>
        {
            var totalMeasure = Math.Max(
                0.001f,
                raw.Where(candidate => candidate.Closed == item.Closed).Sum(candidate => candidate.Measure));
            var totalPerimeter = Math.Max(
                0.001f,
                raw.Where(candidate => candidate.Closed == item.Closed).Sum(candidate => candidate.Perimeter));
            return new ShapeTweenObjectDescriptor(
                item.OriginalIndex,
                item.ObjectIndex,
                item.Closed,
                NormalizeTweenPoint(item.Center, bounds),
                item.Measure / totalMeasure,
                item.Perimeter / totalPerimeter);
        }).ToArray();
        return true;
    }

    private static ShapeTweenObjectMatch[] MatchShapeTweenObjects(
        ShapeTweenObjectDescriptor[] source,
        ShapeTweenObjectDescriptor[] target)
    {
        var matches = new List<ShapeTweenObjectMatch>(Math.Max(source.Length, target.Length));
        foreach (var closed in new[] { true, false })
        {
            var sourceGroup = source
                .Where(item => item.Closed == closed)
                .OrderBy(item => item.OriginalIndex)
                .ToArray();
            var targetGroup = target
                .Where(item => item.Closed == closed)
                .OrderBy(item => item.OriginalIndex)
                .ToArray();
            MatchShapeTweenObjectGroup(sourceGroup, targetGroup, matches);
        }
        return matches.ToArray();
    }

    private static void MatchShapeTweenObjectGroup(
        ShapeTweenObjectDescriptor[] source,
        ShapeTweenObjectDescriptor[] target,
        List<ShapeTweenObjectMatch> matches)
    {
        if (source.Length == 0)
        {
            matches.AddRange(target.Select(item => new ShapeTweenObjectMatch(null, item)));
            return;
        }
        if (target.Length == 0)
        {
            matches.AddRange(source.Select(item => new ShapeTweenObjectMatch(item, null)));
            return;
        }

        var size = Math.Max(source.Length, target.Length);
        var costs = new double[size, size];
        const double unmatchedCost = 1_000_000d;
        for (var sourceIndex = 0; sourceIndex < size; sourceIndex++)
        {
            for (var targetIndex = 0; targetIndex < size; targetIndex++)
            {
                costs[sourceIndex, targetIndex] = sourceIndex < source.Length && targetIndex < target.Length
                    ? ShapeTweenObjectMatchCost(source[sourceIndex], target[targetIndex])
                    : sourceIndex >= source.Length && targetIndex >= target.Length ? 0 : unmatchedCost;
            }
        }

        var assignment = MinimumCostTweenAssignment(costs);
        var matchedTargets = new bool[target.Length];
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            var targetIndex = assignment[sourceIndex];
            if ((uint)targetIndex < target.Length)
            {
                matchedTargets[targetIndex] = true;
                matches.Add(new ShapeTweenObjectMatch(source[sourceIndex], target[targetIndex]));
            }
            else
            {
                matches.Add(new ShapeTweenObjectMatch(source[sourceIndex], null));
            }
        }

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            if (!matchedTargets[targetIndex]) matches.Add(new ShapeTweenObjectMatch(null, target[targetIndex]));
        }
    }

    private static double ShapeTweenObjectMatchCost(
        ShapeTweenObjectDescriptor source,
        ShapeTweenObjectDescriptor target)
    {
        var dx = source.NormalizedCenter.X - target.NormalizedCenter.X;
        var dy = source.NormalizedCenter.Y - target.NormalizedCenter.Y;
        var positionCost = dx * dx + dy * dy;
        var measureCost = Math.Abs(Math.Log((source.MeasureRatio + 0.0001f) / (target.MeasureRatio + 0.0001f)));
        var perimeterCost = Math.Abs(Math.Log((source.PerimeterRatio + 0.0001f) / (target.PerimeterRatio + 0.0001f)));
        return positionCost * 4d
            + measureCost * 0.45d
            + perimeterCost * 0.2d
            + target.OriginalIndex * 0.000001d;
    }

    internal bool RefreshTimelineTweenMaterializationsAtEndpointFrame(int frame)
    {
        if (frame < 0 || LayerCount == 0) return false;
        var affectedLayers = new bool[LayerCount];
        var hasAffectedLayer = false;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is null || !track.Tweens.Any(tween =>
                    tween.StartFrame == frame || tween.EndFrame == frame))
            {
                continue;
            }

            affectedLayers[layer] = true;
            hasAffectedLayer = true;
        }

        if (!hasAffectedLayer) return false;
        RefreshTimelineTweenMaterializations(affectedLayers);
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    internal bool ReplaceTimelineTweenCurve(
        int layer,
        int startFrame,
        int endFrame,
        IEnumerable<TweenCurveAnchor> anchors)
    {
        if ((uint)layer >= LayerCount) return false;
        SynchronizeTimelineTracks();
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        if (track is null || !Timeline.ReplaceTweenCurve(track.Id, startFrame, endFrame, anchors))
        {
            return false;
        }

        var affectedLayers = new bool[LayerCount];
        affectedLayers[layer] = true;
        RefreshTimelineTweenMaterializations(affectedLayers);
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    internal bool RemoveTimelineTween(int layer, int startFrame, int endFrame)
    {
        if ((uint)layer >= LayerCount) return false;
        SynchronizeTimelineTracks();
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        return track is not null && Timeline.RemoveTween(track.Id, startFrame, endFrame);
    }

    private void RefreshTimelineTweenMaterializations(bool[]? affectedLayers = null)
    {
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (affectedLayers is not null
                && ((uint)layer >= affectedLayers.Length || !affectedLayers[layer]))
            {
                continue;
            }

            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is null || track.Tweens.Count == 0) continue;

            foreach (var tween in track.Tweens)
            {
                var sourceObjects = ObjectsForKeyframe(layer, tween.StartFrame);
                var targetObjects = ObjectsForKeyframe(layer, tween.EndFrame);
                if (sourceObjects.Length == 0 || targetObjects.Length == 0) continue;

                ShapeTweenObjectPlan[]? shapePlans = null;
                if (tween.Kind == TimelineTweenKind.Shape)
                {
                    if (!TryCreateShapeTweenObjectPlans(
                            sourceObjects,
                            targetObjects,
                            out shapePlans,
                            out _)) continue;
                }
                else if (sourceObjects.Length != targetObjects.Length)
                {
                    continue;
                }

                var frames = Enumerable.Range(
                    tween.StartFrame + 1,
                    Math.Max(0, tween.EndFrame - tween.StartFrame - 1)).ToArray();
                var materializedObjectCount = shapePlans?.Length ?? sourceObjects.Length;
                var destinationsByFrame = frames
                    .Select(frame => ObjectsForKeyframe(layer, frame))
                    .ToArray();
                if (tween.Kind == TimelineTweenKind.Shape)
                {
                    for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                    {
                        var destinations = destinationsByFrame[frameIndex];
                        if (destinations.Length >= materializedObjectCount) continue;
                        var frame = frames[frameIndex];
                        AppendTimelineTweenFrame(
                            layer,
                            frame,
                            tween.Kind,
                            sourceObjects,
                            targetObjects,
                            shapePlans,
                            tween.ProgressAt(frame),
                            destinations.Length);
                        SynchronizeKeyframeContentKind(layer, frame);
                        destinationsByFrame[frameIndex] = ObjectsForKeyframe(layer, frame);
                    }
                }

                for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    var destinations = destinationsByFrame[frameIndex];
                    if (destinations.Length < materializedObjectCount) continue;
                    var progress = tween.ProgressAt(frame);
                    for (var objectIndex = 0; objectIndex < materializedObjectCount; objectIndex++)
                    {
                        RefreshTimelineTweenObject(
                            layer,
                            frame,
                            tween.Kind,
                            sourceObjects,
                            targetObjects,
                            shapePlans,
                            objectIndex,
                            destinations[objectIndex],
                            progress);
                    }

                    for (var objectIndex = materializedObjectCount; objectIndex < destinations.Length; objectIndex++)
                    {
                        HideTimelineTweenObject(destinations[objectIndex]);
                    }
                }
            }
        }
    }

    private bool CanInterpolateGeometry(
        int source,
        int target,
        TimelineTweenKind kind,
        out string error)
    {
        error = string.Empty;
        var shape = ShapeKind[source];
        var targetShape = ShapeKind[target];
        if (kind == TimelineTweenKind.Shape)
        {
            if (!TryGetShapeTweenContours(source, out var sourceContours, out var sourceClosed)
                || !TryGetShapeTweenContours(target, out var targetContours, out var targetClosed))
            {
                error = "Shape tweens require editable vector shapes or strokes.";
                return false;
            }

            if (sourceClosed != targetClosed)
            {
                error = "Shape tween endpoints must both be closed shapes or both be open vector strokes.";
                return false;
            }

            if (sourceContours.Length == 0
                || targetContours.Length == 0
                || sourceContours.Any(contour => contour.Length < (sourceClosed ? 3 : 2))
                || targetContours.Any(contour => contour.Length < (targetClosed ? 3 : 2)))
            {
                error = "Shape tween endpoints require compatible vector contours.";
                return false;
            }

            return true;
        }

        if (shape != targetShape)
        {
            error = "Classic tween endpoints must use the same shape type.";
            return false;
        }

        if (shape is VectorAnimationEngine.ShapeKind.ImportedSvg or VectorAnimationEngine.ShapeKind.Text)
        {
            error = "This shape type cannot be interpolated.";
            return false;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path)
        {
            var sourceHasBezier = TryGetPathBezierWorldContours(source, out var sourceBezier);
            var targetHasBezier = TryGetPathBezierWorldContours(target, out var targetBezier);
            if (sourceHasBezier || targetHasBezier)
            {
                if (!SameBezierTopology(sourceBezier, targetBezier))
                {
                    error = "Path tweens require matching contours and control points.";
                    return false;
                }
            }
            else if (!TryGetPathWorldContours(source, out var sourceContours)
                || !TryGetPathWorldContours(target, out var targetContours)
                || !SamePointTopology(sourceContours, targetContours))
            {
                error = "Path tweens require matching contour point counts.";
                return false;
            }
        }
        else if (shape == VectorAnimationEngine.ShapeKind.Freeform)
        {
            var sourceHasNodes = TryGetFreehandBezierWorldNodes(source, out var sourceNodes);
            var targetHasNodes = TryGetFreehandBezierWorldNodes(target, out var targetNodes);
            if (sourceHasNodes || targetHasNodes)
            {
                if (sourceNodes.Length != targetNodes.Length)
                {
                    error = "Freeform tweens require matching control point counts.";
                    return false;
                }
            }
            else if (!TryGetFreehandWorldPoints(source, out var sourcePoints)
                || !TryGetFreehandWorldPoints(target, out var targetPoints)
                || sourcePoints.Length != targetPoints.Length)
            {
                error = "Freeform tweens require matching point counts.";
                return false;
            }
        }
        else if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            if (!TryGetMixingBrushWorldRegion(source, out var sourceRegion)
                || !TryGetMixingBrushWorldRegion(target, out var targetRegion)
                || sourceRegion.Vertices.Length != targetRegion.Vertices.Length
                || !sourceRegion.TriangleIndices.SequenceEqual(targetRegion.TriangleIndices))
            {
                error = "Mixing brush tweens require matching region topology.";
                return false;
            }
        }

        return true;
    }

    private bool TryGetShapeTweenContours(
        int objectIndex,
        out PointF[][] contours,
        out bool closed)
    {
        contours = [];
        closed = false;
        if ((uint)objectIndex >= ObjectCount) return false;

        var shape = ShapeKind[objectIndex];
        if (IsFillShape(shape))
        {
            contours = GetObjectBoundaryContours(objectIndex);
            closed = true;
            return contours.Length > 0;
        }

        if (shape != VectorAnimationEngine.ShapeKind.Line && !IsFreehandShape(shape))
        {
            return false;
        }

        var points = StrokeSamples(objectIndex)
            .Select(sample => sample.Point)
            .ToArray();
        if (points.Length < 2) return false;
        contours = [points];
        return true;
    }

    private void InterpolateObject(
        int source,
        int target,
        int destination,
        float t,
        TimelineTweenKind kind,
        ShapeTweenGeometryPlan? shapePlan = null)
    {
        X[destination] = TweenLerp(X[source], X[target], t);
        Y[destination] = TweenLerp(Y[source], Y[target], t);
        Width[destination] = Math.Max(1, TweenLerp(Width[source], Width[target], t));
        Height[destination] = Math.Max(1, TweenLerp(Height[source], Height[target], t));
        Angle[destination] = TweenLerpAngle(Angle[source], Angle[target], t);
        Stroke[destination] = Math.Max(0, TweenLerp(Stroke[source], Stroke[target], t));
        Argb[destination] = TweenArgb(Argb[source], Argb[target], t);
        StrokeArgb[destination] = TweenArgb(StrokeArgb[source], StrokeArgb[target], t);
        InterpolateGradientPaint(source, target, destination, t);

        if (kind == TimelineTweenKind.Shape)
        {
            if (shapePlan is not null) InterpolateShapeGeometry(destination, t, shapePlan);
            return;
        }

        var shape = ShapeKind[source];
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var sourceCurve = LineCurve(source);
            var targetCurve = LineCurve(target);
            SetLineCurve(
                destination,
                TweenPoint(sourceCurve.Start, targetCurve.Start, t),
                TweenPoint(sourceCurve.Control1, targetCurve.Control1, t),
                TweenPoint(sourceCurve.Control2, targetCurve.Control2, t),
                TweenPoint(sourceCurve.End, targetCurve.End, t));
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path)
        {
            if (TryGetPathBezierWorldContours(source, out var sourceBezier)
                && TryGetPathBezierWorldContours(target, out var targetBezier))
            {
                var contours = new PathBezierNode[sourceBezier.Length][];
                for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
                {
                    contours[contourIndex] = new PathBezierNode[sourceBezier[contourIndex].Length];
                    for (var nodeIndex = 0; nodeIndex < contours[contourIndex].Length; nodeIndex++)
                    {
                        var a = sourceBezier[contourIndex][nodeIndex];
                        var b = targetBezier[contourIndex][nodeIndex];
                        contours[contourIndex][nodeIndex] = new PathBezierNode(
                            TweenPoint(a.Anchor, b.Anchor, t),
                            TweenPoint(a.IncomingControl, b.IncomingControl, t),
                            TweenPoint(a.OutgoingControl, b.OutgoingControl, t));
                    }
                }
                SetPathBezierContoursCore(destination, contours);
                return;
            }

            if (TryGetPathWorldContours(source, out var sourceContours)
                && TryGetPathWorldContours(target, out var targetContours))
            {
                SetPathContours(
                    destination,
                    sourceContours
                        .Select((contour, contourIndex) => contour
                            .Select((point, pointIndex) => TweenPoint(
                                point,
                                targetContours[contourIndex][pointIndex],
                                t))
                            .ToArray())
                        .ToArray());
                return;
            }
        }

        if (shape == VectorAnimationEngine.ShapeKind.Freeform)
        {
            if (TryGetFreehandBezierWorldNodes(source, out var sourceNodes)
                && TryGetFreehandBezierWorldNodes(target, out var targetNodes))
            {
                SetFreehandBezierNodesCore(
                    destination,
                    sourceNodes.Select((node, index) => new PathBezierNode(
                        TweenPoint(node.Anchor, targetNodes[index].Anchor, t),
                        TweenPoint(node.IncomingControl, targetNodes[index].IncomingControl, t),
                        TweenPoint(node.OutgoingControl, targetNodes[index].OutgoingControl, t))).ToArray());
                return;
            }

            if (TryGetFreehandWorldPoints(source, out var sourcePoints)
                && TryGetFreehandWorldPoints(target, out var targetPoints))
            {
                SetFreehandPoints(
                    destination,
                    sourcePoints.Select((point, index) => TweenPoint(point, targetPoints[index], t)).ToArray());
                return;
            }
        }

        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke
            && TryGetMixingBrushWorldRegion(source, out var sourceRegion)
            && TryGetMixingBrushWorldRegion(target, out var targetRegion))
        {
            var vertices = sourceRegion.Vertices
                .Select((vertex, index) => vertex with
                {
                    Point = TweenPoint(vertex.Point, targetRegion.Vertices[index].Point, t),
                    Argb = TweenArgb(vertex.Argb, targetRegion.Vertices[index].Argb, t)
                })
                .ToArray();
            SetMixingBrushRegion(destination, new MixingBrushRegionData(vertices, sourceRegion.TriangleIndices));
        }
    }

    private void InterpolateShapeObject(
        ShapeTweenObjectPlan plan,
        int destination,
        float t)
    {
        if (plan.Source.HasValue && plan.Target.HasValue)
        {
            InterpolateObject(
                plan.Source.Value,
                plan.Target.Value,
                destination,
                t,
                TimelineTweenKind.Shape,
                plan.Geometry);
            return;
        }

        var endpoint = plan.Source ?? plan.Target
            ?? throw new InvalidOperationException("A shape tween object plan has no endpoint.");
        ScaleTweenObjectAlpha(destination, endpoint, plan.Source.HasValue ? 1f - t : t);
        InterpolateShapeGeometry(destination, t, plan.Geometry);
    }

    private void ScaleTweenObjectAlpha(int destination, int endpoint, float opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        Argb[destination] = ScaleTweenArgbAlpha(Argb[endpoint], opacity);
        StrokeArgb[destination] = ScaleTweenArgbAlpha(StrokeArgb[endpoint], opacity);
        GradientStartArgb[destination] = ScaleTweenArgbAlpha(GradientStartArgb[endpoint], opacity);
        GradientEndArgb[destination] = ScaleTweenArgbAlpha(GradientEndArgb[endpoint], opacity);
        if (_gradientStops.TryGetValue(endpoint, out var stops))
        {
            _gradientStops[destination] = stops
                .Select(stop => new GradientStop(stop.Position, ScaleTweenArgbAlpha(stop.Argb, opacity)))
                .ToArray();
        }
    }

    private static int ScaleTweenArgbAlpha(int argb, float opacity)
    {
        var color = Color.FromArgb(argb);
        return Color.FromArgb(
            (int)MathF.Round(color.A * Math.Clamp(opacity, 0, 1)),
            color.R,
            color.G,
            color.B).ToArgb();
    }

    private void InterpolateGradientPaint(int source, int target, int destination, float t)
    {
        var sourceHasGradient = HasGradient(source);
        var targetHasGradient = HasGradient(target);
        if (!sourceHasGradient && !targetHasGradient)
        {
            DisableLinearGradient(destination);
            return;
        }

        var sourceKind = sourceHasGradient ? GradientKinds[source] : GradientKind.Solid;
        var targetKind = targetHasGradient ? GradientKinds[target] : GradientKind.Solid;
        var destinationKind = sourceHasGradient && targetHasGradient
            ? (sourceKind == targetKind || t < 0.5f ? sourceKind : targetKind)
            : sourceHasGradient ? sourceKind : targetKind;
        var sourceStops = sourceHasGradient
            ? GetGradientStops(source)
            : SolidTweenStops(Argb[source]);
        var targetStops = targetHasGradient
            ? GetGradientStops(target)
            : SolidTweenStops(Argb[target]);
        var stopPositions = sourceStops
            .Select(stop => stop.Position)
            .Concat(targetStops.Select(stop => stop.Position))
            .Append(0f)
            .Append(1f)
            .Distinct()
            .OrderBy(position => position)
            .ToArray();
        var interpolatedStops = stopPositions
            .Select(position => new GradientStop(
                position,
                TweenArgb(
                    GradientPaintUtilities.SampleColor(sourceStops, position).ToArgb(),
                    GradientPaintUtilities.SampleColor(targetStops, position).ToArgb(),
                    t)))
            .ToArray();

        LinearGradientEnabled[destination] = true;
        GradientKinds[destination] = destinationKind;
        _gradientStops[destination] = interpolatedStops;
        GradientStartArgb[destination] = interpolatedStops[0].Argb;
        GradientEndArgb[destination] = interpolatedStops[^1].Argb;
        InterpolateGradientEndpoints(
            source,
            target,
            destination,
            t,
            sourceHasGradient,
            targetHasGradient);
        InterpolateGradientAuxiliaryData(
            source,
            target,
            destination,
            t,
            sourceHasGradient,
            targetHasGradient,
            sourceKind,
            targetKind,
            destinationKind);
    }

    private static GradientStop[] SolidTweenStops(int argb) =>
        [new GradientStop(0, argb), new GradientStop(1, argb)];

    private void InterpolateGradientEndpoints(
        int source,
        int target,
        int destination,
        float t,
        bool sourceHasGradient,
        bool targetHasGradient)
    {
        if (sourceHasGradient && targetHasGradient)
        {
            GradientStartX[destination] = TweenLerp(GradientStartX[source], GradientStartX[target], t);
            GradientStartY[destination] = TweenLerp(GradientStartY[source], GradientStartY[target], t);
            GradientEndX[destination] = TweenLerp(GradientEndX[source], GradientEndX[target], t);
            GradientEndY[destination] = TweenLerp(GradientEndY[source], GradientEndY[target], t);
            return;
        }

        var gradientEndpoint = sourceHasGradient ? source : target;
        GradientStartX[destination] = GradientStartX[gradientEndpoint];
        GradientStartY[destination] = GradientStartY[gradientEndpoint];
        GradientEndX[destination] = GradientEndX[gradientEndpoint];
        GradientEndY[destination] = GradientEndY[gradientEndpoint];
    }

    private void InterpolateGradientAuxiliaryData(
        int source,
        int target,
        int destination,
        float t,
        bool sourceHasGradient,
        bool targetHasGradient,
        GradientKind sourceKind,
        GradientKind targetKind,
        GradientKind destinationKind)
    {
        if (destinationKind == GradientKind.Linear)
        {
            InterpolateGradientPath(
                source,
                target,
                destination,
                t,
                sourceHasGradient,
                targetHasGradient,
                sourceKind,
                targetKind);
        }
        else
        {
            _gradientPathLocalPoints.Remove(destination);
        }

        if (destinationKind == GradientKind.ShapeRadial)
        {
            InterpolateShapeGradientMapping(
                source,
                target,
                destination,
                t,
                sourceHasGradient,
                targetHasGradient,
                sourceKind,
                targetKind);
        }
        else
        {
            _shapeGradientMappingLocalContours.Remove(destination);
        }
    }

    private void InterpolateGradientPath(
        int source,
        int target,
        int destination,
        float t,
        bool sourceHasGradient,
        bool targetHasGradient,
        GradientKind sourceKind,
        GradientKind targetKind)
    {
        PointF[] sourcePath = [];
        PointF[] targetPath = [];
        var sourceHasPath = sourceKind == GradientKind.Linear
            && TryGetGradientPathLocalPoints(source, out sourcePath);
        var targetHasPath = targetKind == GradientKind.Linear
            && TryGetGradientPathLocalPoints(target, out targetPath);
        if (sourceHasPath && targetHasPath && sourcePath.Length == targetPath.Length)
        {
            _gradientPathLocalPoints[destination] = sourcePath
                .Select((point, index) => TweenPoint(point, targetPath[index], t))
                .ToArray();
            return;
        }

        if (sourceHasPath
            && (!targetHasGradient || sourceKind != targetKind || t < 0.5f))
        {
            _gradientPathLocalPoints[destination] = sourcePath.ToArray();
            return;
        }

        if (targetHasPath
            && (!sourceHasGradient || sourceKind != targetKind || t >= 0.5f))
        {
            _gradientPathLocalPoints[destination] = targetPath.ToArray();
            return;
        }

        _gradientPathLocalPoints.Remove(destination);
    }

    private void InterpolateShapeGradientMapping(
        int source,
        int target,
        int destination,
        float t,
        bool sourceHasGradient,
        bool targetHasGradient,
        GradientKind sourceKind,
        GradientKind targetKind)
    {
        PointF[][] sourceMapping = [];
        PointF[][] targetMapping = [];
        var sourceHasMapping = sourceKind == GradientKind.ShapeRadial
            && TryGetShapeGradientMappingLocalContours(source, out sourceMapping);
        var targetHasMapping = targetKind == GradientKind.ShapeRadial
            && TryGetShapeGradientMappingLocalContours(target, out targetMapping);
        if (sourceHasMapping && targetHasMapping && SamePointTopology(sourceMapping, targetMapping))
        {
            _shapeGradientMappingLocalContours[destination] = sourceMapping
                .Select((contour, contourIndex) => contour
                    .Select((point, pointIndex) => TweenPoint(
                        point,
                        targetMapping[contourIndex][pointIndex],
                        t))
                    .ToArray())
                .ToArray();
            return;
        }

        if (sourceHasMapping
            && (!targetHasGradient || sourceKind != targetKind || t < 0.5f))
        {
            _shapeGradientMappingLocalContours[destination] = CloneContours(sourceMapping);
            return;
        }

        if (targetHasMapping
            && (!sourceHasGradient || sourceKind != targetKind || t >= 0.5f))
        {
            _shapeGradientMappingLocalContours[destination] = CloneContours(targetMapping);
            return;
        }

        _shapeGradientMappingLocalContours.Remove(destination);
    }

    private bool TryCreateShapeTweenGeometryPlan(
        int? source,
        int? target,
        out ShapeTweenGeometryPlan plan)
    {
        plan = null!;
        PointF[][] sourceContours = [];
        PointF[][] targetContours = [];
        var sourceClosed = false;
        var targetClosed = false;
        if (source.HasValue
            && !TryGetShapeTweenContours(source.Value, out sourceContours, out sourceClosed))
        {
            return false;
        }
        if (target.HasValue
            && !TryGetShapeTweenContours(target.Value, out targetContours, out targetClosed))
        {
            return false;
        }
        if (!source.HasValue && !target.HasValue)
        {
            return false;
        }
        if (source.HasValue && target.HasValue && sourceClosed != targetClosed)
        {
            return false;
        }

        var closed = source.HasValue ? sourceClosed : targetClosed;
        if (!closed)
        {
            var sourceContour = source.HasValue ? sourceContours[0] : [];
            var targetContour = target.HasValue ? targetContours[0] : [];
            if (!source.HasValue)
            {
                var anchor = TweenContourAverage(targetContour);
                sourceContour = Enumerable.Repeat(anchor, targetContour.Length).ToArray();
            }
            if (!target.HasValue)
            {
                var anchor = TweenContourAverage(sourceContour);
                targetContour = Enumerable.Repeat(anchor, sourceContour.Length).ToArray();
            }
            var sampleCount = Math.Clamp(
                Math.Max(sourceContour.Length, targetContour.Length),
                2,
                1024);
            var sampledSource = ResampleOpenContour(sourceContour, sampleCount);
            var sampledTarget = ResampleOpenContour(targetContour, sampleCount);
            AlignOpenContourDirection(sampledSource, sampledTarget);
            plan = new ShapeTweenGeometryPlan(
                false,
                [sampledSource],
                [sampledTarget],
                [0]);
            return true;
        }

        var normalizedSource = sourceContours
            .Select(NormalizeTweenContour)
            .Where(contour => contour.Length >= 3)
            .ToArray();
        var normalizedTarget = targetContours
            .Select(NormalizeTweenContour)
            .Where(contour => contour.Length >= 3)
            .ToArray();
        if (source.HasValue && normalizedSource.Length == 0
            || target.HasValue && normalizedTarget.Length == 0)
        {
            return false;
        }

        if (!source.HasValue || !target.HasValue)
        {
            var reference = source.HasValue ? normalizedSource : normalizedTarget;
            var unmatchedPreparedSource = new PointF[reference.Length][];
            var unmatchedPreparedTarget = new PointF[reference.Length][];
            for (var contourIndex = 0; contourIndex < reference.Length; contourIndex++)
            {
                var contour = reference[contourIndex];
                var anchor = TweenContourCentroid(contour);
                var degenerate = Enumerable.Repeat(anchor, contour.Length).ToArray();
                if (source.HasValue)
                {
                    EqualizeTweenContours(
                        contour,
                        degenerate,
                        out unmatchedPreparedSource[contourIndex],
                        out unmatchedPreparedTarget[contourIndex]);
                }
                else
                {
                    EqualizeTweenContours(
                        degenerate,
                        contour,
                        out unmatchedPreparedSource[contourIndex],
                        out unmatchedPreparedTarget[contourIndex]);
                }
            }

            plan = new ShapeTweenGeometryPlan(
                true,
                unmatchedPreparedSource,
                unmatchedPreparedTarget,
                DetermineReferenceContourDepths(reference));
            return true;
        }

        var sourceBounds = ContourBounds(normalizedSource);
        var targetBounds = ContourBounds(normalizedTarget);
        var sourceDescriptors = DescribeTweenContours(normalizedSource, sourceBounds);
        var targetDescriptors = DescribeTweenContours(normalizedTarget, targetBounds);
        var matches = MatchTweenContours(sourceDescriptors, targetDescriptors);
        if (matches.Length == 0) return false;

        var preparedSource = new PointF[matches.Length][];
        var preparedTarget = new PointF[matches.Length][];
        var depths = new int[matches.Length];
        for (var matchIndex = 0; matchIndex < matches.Length; matchIndex++)
        {
            var match = matches[matchIndex];
            var sourceContour = match.Source?.Points;
            var targetContour = match.Target?.Points;
            var directTopology = match.Source is not null
                && match.Target is not null
                && ShapeKind[source.Value] == ShapeKind[target.Value]
                && ShapeKind[source.Value] != VectorAnimationEngine.ShapeKind.Path
                && match.Source.OriginalIndex == match.Target.OriginalIndex
                && sourceContour!.Length == targetContour!.Length
                && TweenSignedArea(sourceContour) * TweenSignedArea(targetContour) >= 0;
            if (directTopology)
            {
                preparedSource[matchIndex] = sourceContour!.ToArray();
                preparedTarget[matchIndex] = targetContour!.ToArray();
            }
            else
            {
                if (sourceContour is null)
                {
                    var point = MapTweenNormalizedPoint(match.Target!.NormalizedCenter, sourceBounds);
                    sourceContour = Enumerable.Repeat(point, match.Target.Points.Length).ToArray();
                }
                if (targetContour is null)
                {
                    var point = MapTweenNormalizedPoint(match.Source!.NormalizedCenter, targetBounds);
                    targetContour = Enumerable.Repeat(point, match.Source.Points.Length).ToArray();
                }

                EqualizeTweenContours(
                    sourceContour,
                    targetContour,
                    out preparedSource[matchIndex],
                    out preparedTarget[matchIndex]);
            }

            depths[matchIndex] = match.Source?.Depth ?? match.Target!.Depth;
        }

        plan = new ShapeTweenGeometryPlan(true, preparedSource, preparedTarget, depths);
        return true;
    }

    private void InterpolateShapeGeometry(
        int destination,
        float t,
        ShapeTweenGeometryPlan plan)
    {
        if (!plan.Closed)
        {
            ShapeKind[destination] = VectorAnimationEngine.ShapeKind.Freeform;
            ShapeVertexCounts[destination] = 0;
            SetFreehandPoints(
                destination,
                plan.SourceContours[0]
                    .Select((point, pointIndex) => TweenPoint(
                        point,
                        plan.TargetContours[0][pointIndex],
                        t))
                    .ToArray());
            return;
        }

        var interpolated = new PointF[plan.SourceContours.Length][];
        for (var contourIndex = 0; contourIndex < interpolated.Length; contourIndex++)
        {
            interpolated[contourIndex] = plan.SourceContours[contourIndex]
                .Select((point, pointIndex) => TweenPoint(
                    point,
                    plan.TargetContours[contourIndex][pointIndex],
                    t))
                .ToArray();
        }

        interpolated = CanonicalizeTweenContours(interpolated, plan.ContourDepths);
        ShapeKind[destination] = VectorAnimationEngine.ShapeKind.Path;
        ShapeVertexCounts[destination] = 0;
        SetPathContours(destination, interpolated);
    }

    private static ShapeTweenContourDescriptor[] DescribeTweenContours(
        PointF[][] contours,
        RectangleF bounds)
    {
        var depths = DetermineReferenceContourDepths(contours);
        var areas = contours.Select(contour => Math.Abs(TweenSignedArea(contour))).ToArray();
        var perimeters = contours.Select(TweenContourPerimeter).ToArray();
        var totalArea = Math.Max(0.001f, areas.Sum());
        var totalPerimeter = Math.Max(0.001f, perimeters.Sum());
        return contours
            .Select((contour, index) =>
            {
                var center = TweenContourCentroid(contour);
                return new ShapeTweenContourDescriptor(
                    index,
                    contour,
                    center,
                    NormalizeTweenPoint(center, bounds),
                    areas[index] / totalArea,
                    perimeters[index] / totalPerimeter,
                    depths[index]);
            })
            .ToArray();
    }

    private static ShapeTweenContourMatch[] MatchTweenContours(
        ShapeTweenContourDescriptor[] source,
        ShapeTweenContourDescriptor[] target)
    {
        var matches = new List<ShapeTweenContourMatch>(Math.Max(source.Length, target.Length));
        for (var parity = 0; parity <= 1; parity++)
        {
            var sourceGroup = source
                .Where(item => (item.Depth & 1) == parity)
                .OrderBy(item => item.OriginalIndex)
                .ToArray();
            var targetGroup = target
                .Where(item => (item.Depth & 1) == parity)
                .OrderBy(item => item.OriginalIndex)
                .ToArray();
            MatchTweenContourGroup(sourceGroup, targetGroup, matches);
        }

        return matches.ToArray();
    }

    private static void MatchTweenContourGroup(
        ShapeTweenContourDescriptor[] source,
        ShapeTweenContourDescriptor[] target,
        List<ShapeTweenContourMatch> matches)
    {
        if (source.Length == 0)
        {
            matches.AddRange(target.Select(item => new ShapeTweenContourMatch(null, item)));
            return;
        }
        if (target.Length == 0)
        {
            matches.AddRange(source.Select(item => new ShapeTweenContourMatch(item, null)));
            return;
        }

        // Compile a stable one-to-one edge plan. Dummy rows/columns represent
        // contours that collapse or emerge when endpoint topology differs.
        var size = Math.Max(source.Length, target.Length);
        var costs = new double[size, size];
        const double unmatchedCost = 1_000_000d;
        for (var sourceIndex = 0; sourceIndex < size; sourceIndex++)
        {
            for (var targetIndex = 0; targetIndex < size; targetIndex++)
            {
                costs[sourceIndex, targetIndex] = sourceIndex < source.Length && targetIndex < target.Length
                    ? TweenContourMatchCost(source[sourceIndex], target[targetIndex])
                    : sourceIndex >= source.Length && targetIndex >= target.Length ? 0 : unmatchedCost;
            }
        }

        var assignment = MinimumCostTweenAssignment(costs);
        var matchedTargets = new bool[target.Length];
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            var targetIndex = assignment[sourceIndex];
            if ((uint)targetIndex < target.Length)
            {
                matchedTargets[targetIndex] = true;
                matches.Add(new ShapeTweenContourMatch(source[sourceIndex], target[targetIndex]));
            }
            else
            {
                matches.Add(new ShapeTweenContourMatch(source[sourceIndex], null));
            }
        }

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            if (!matchedTargets[targetIndex]) matches.Add(new ShapeTweenContourMatch(null, target[targetIndex]));
        }
    }

    private static double TweenContourMatchCost(
        ShapeTweenContourDescriptor source,
        ShapeTweenContourDescriptor target)
    {
        var dx = source.NormalizedCenter.X - target.NormalizedCenter.X;
        var dy = source.NormalizedCenter.Y - target.NormalizedCenter.Y;
        var positionCost = dx * dx + dy * dy;
        var areaCost = Math.Abs(Math.Log((source.AreaRatio + 0.0001f) / (target.AreaRatio + 0.0001f)));
        var perimeterCost = Math.Abs(Math.Log((source.PerimeterRatio + 0.0001f) / (target.PerimeterRatio + 0.0001f)));
        var depthCost = Math.Abs(source.Depth - target.Depth);
        return positionCost * 4d
            + areaCost * 0.45d
            + perimeterCost * 0.2d
            + depthCost * 0.75d
            + target.OriginalIndex * 0.000001d;
    }

    private static int[] MinimumCostTweenAssignment(double[,] costs)
    {
        var size = costs.GetLength(0);
        var rowPotential = new double[size + 1];
        var columnPotential = new double[size + 1];
        var matchedRow = new int[size + 1];
        var previousColumn = new int[size + 1];
        for (var row = 1; row <= size; row++)
        {
            matchedRow[0] = row;
            var minimum = Enumerable.Repeat(double.PositiveInfinity, size + 1).ToArray();
            var used = new bool[size + 1];
            var column = 0;
            do
            {
                used[column] = true;
                var currentRow = matchedRow[column];
                var delta = double.PositiveInfinity;
                var nextColumn = 0;
                for (var candidate = 1; candidate <= size; candidate++)
                {
                    if (used[candidate]) continue;
                    var reduced = costs[currentRow - 1, candidate - 1]
                        - rowPotential[currentRow]
                        - columnPotential[candidate];
                    if (reduced < minimum[candidate])
                    {
                        minimum[candidate] = reduced;
                        previousColumn[candidate] = column;
                    }
                    if (minimum[candidate] < delta)
                    {
                        delta = minimum[candidate];
                        nextColumn = candidate;
                    }
                }

                for (var candidate = 0; candidate <= size; candidate++)
                {
                    if (used[candidate])
                    {
                        rowPotential[matchedRow[candidate]] += delta;
                        columnPotential[candidate] -= delta;
                    }
                    else
                    {
                        minimum[candidate] -= delta;
                    }
                }
                column = nextColumn;
            }
            while (matchedRow[column] != 0);

            do
            {
                var previous = previousColumn[column];
                matchedRow[column] = matchedRow[previous];
                column = previous;
            }
            while (column != 0);
        }

        var assignment = Enumerable.Repeat(-1, size).ToArray();
        for (var column = 1; column <= size; column++)
        {
            if (matchedRow[column] > 0) assignment[matchedRow[column] - 1] = column - 1;
        }
        return assignment;
    }

    private static void EqualizeTweenContours(
        PointF[] source,
        PointF[] target,
        out PointF[] preparedSource,
        out PointF[] preparedTarget)
    {
        var probeCount = Math.Clamp(Math.Max(source.Length, target.Length), 16, 64);
        var sourceProbe = ResampleClosedContour(source, probeCount);
        var targetProbe = ResampleClosedContour(target, probeCount);
        FindClosedContourAlignment(sourceProbe, targetProbe, out var offset, out var reversed);
        var phase = offset / (float)probeCount;

        var sourceLengths = TweenContourLengths(source, out var sourcePerimeter);
        var targetLengths = TweenContourLengths(target, out var targetPerimeter);
        const int positionScale = 1_000_000;
        var positions = new SortedSet<int>();
        AddTweenContourBreakpoints(positions, sourceLengths, sourcePerimeter, 0, false, positionScale);
        AddTweenContourBreakpoints(positions, targetLengths, targetPerimeter, phase, reversed, positionScale);
        for (var sample = 0; sample < 16; sample++)
        {
            positions.Add((int)Math.Round(sample / 16d * positionScale));
        }

        if (positions.Count > 1024)
        {
            positions.Clear();
            for (var sample = 0; sample < 1024; sample++)
            {
                positions.Add((int)Math.Round(sample / 1024d * positionScale));
            }
        }

        preparedSource = new PointF[positions.Count];
        preparedTarget = new PointF[positions.Count];
        var pointIndex = 0;
        foreach (var positionKey in positions)
        {
            var position = positionKey / (float)positionScale;
            var targetPosition = TweenWrapPosition(phase + (reversed ? -position : position));
            preparedSource[pointIndex] = SampleTweenContour(source, sourceLengths, sourcePerimeter, position);
            preparedTarget[pointIndex] = SampleTweenContour(target, targetLengths, targetPerimeter, targetPosition);
            pointIndex++;
        }
    }

    private static void AddTweenContourBreakpoints(
        SortedSet<int> positions,
        float[] lengths,
        float perimeter,
        float phase,
        bool reversed,
        int scale)
    {
        if (perimeter <= 0.001f) return;
        for (var index = 0; index < lengths.Length - 1; index++)
        {
            var position = lengths[index] / perimeter;
            if (phase != 0 || reversed)
            {
                position = reversed
                    ? TweenWrapPosition(phase - position)
                    : TweenWrapPosition(position - phase);
            }
            positions.Add((int)Math.Round(position * scale));
        }
    }

    private static float[] TweenContourLengths(IReadOnlyList<PointF> contour, out float perimeter)
    {
        var lengths = new float[contour.Count + 1];
        perimeter = 0;
        for (var index = 0; index < contour.Count; index++)
        {
            lengths[index] = perimeter;
            perimeter += TweenDistance(contour[index], contour[(index + 1) % contour.Count]);
        }
        lengths[^1] = perimeter;
        return lengths;
    }

    private static PointF SampleTweenContour(
        IReadOnlyList<PointF> contour,
        IReadOnlyList<float> lengths,
        float perimeter,
        float position)
    {
        if (contour.Count == 0) return PointF.Empty;
        if (perimeter <= 0.001f) return contour[0];
        var distance = TweenWrapPosition(position) * perimeter;
        var segment = 0;
        while (segment < contour.Count - 1 && distance > lengths[segment + 1]) segment++;
        var segmentLength = Math.Max(0.001f, lengths[segment + 1] - lengths[segment]);
        return TweenPoint(
            contour[segment],
            contour[(segment + 1) % contour.Count],
            Math.Clamp((distance - lengths[segment]) / segmentLength, 0, 1));
    }

    private static float TweenWrapPosition(float position)
    {
        position %= 1f;
        return position < 0 ? position + 1f : position;
    }

    private static void FindClosedContourAlignment(
        IReadOnlyList<PointF> source,
        IReadOnlyList<PointF> target,
        out int bestOffset,
        out bool bestReverse)
    {
        bestOffset = 0;
        bestReverse = false;
        var bestCost = double.MaxValue;
        if (source.Count == 0 || source.Count != target.Count) return;
        for (var reverse = 0; reverse <= 1; reverse++)
        {
            for (var offset = 0; offset < target.Count; offset++)
            {
                var cost = 0d;
                for (var index = 0; index < source.Count; index++)
                {
                    var targetIndex = reverse == 0
                        ? (offset + index) % target.Count
                        : (offset - index + target.Count) % target.Count;
                    var dx = source[index].X - target[targetIndex].X;
                    var dy = source[index].Y - target[targetIndex].Y;
                    cost += dx * dx + dy * dy;
                }
                if (cost >= bestCost) continue;
                bestCost = cost;
                bestOffset = offset;
                bestReverse = reverse != 0;
            }
        }
    }

    private static PointF[][] CanonicalizeTweenContours(PointF[][] contours, int[] depths)
    {
        if (contours.Length <= 1 || contours.Length != depths.Length) return contours;
        try
        {
            // Rebuild the intended compound fill by containment depth so two
            // morphing outer rings never cancel into an even-odd overlap hole.
            var desired = new Paths64();
            for (var depth = 0; depth <= depths.Max(); depth++)
            {
                var levelPaths = new Paths64();
                for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
                {
                    if (depths[contourIndex] != depth) continue;
                    var path = ToClipperPath(contours[contourIndex], closed: true);
                    if (path.Count < 3
                        || Math.Abs(Clipper.Area(path)) < 0.5d * ClipperCoordinateScale * ClipperCoordinateScale)
                    {
                        continue;
                    }
                    if (!Clipper.IsPositive(path)) path.Reverse();
                    levelPaths.Add(path);
                }

                if (levelPaths.Count == 0) continue;
                var levelRegion = Clipper.Union(levelPaths, FillRule.NonZero);
                if (levelRegion.Count == 0) continue;
                if (desired.Count == 0)
                {
                    if ((depth & 1) == 0) desired = levelRegion;
                    continue;
                }

                var result = new Paths64();
                var clipper = new Clipper64 { PreserveCollinear = true };
                clipper.AddSubject(desired);
                clipper.AddClip(levelRegion);
                if (!clipper.Execute(
                        (depth & 1) == 0 ? ClipType.Union : ClipType.Difference,
                        FillRule.NonZero,
                        result))
                {
                    return contours;
                }
                desired = result;
            }

            var normalized = desired.Count > 0 ? FromClipperPaths(desired) : Array.Empty<PointF[]>();
            return normalized.Length > 0 ? normalized : contours;
        }
        catch (Exception exception) when (exception is ClipperLibException or OverflowException)
        {
            return contours;
        }
    }

    private static PointF TweenContourCentroid(IReadOnlyList<PointF> contour)
    {
        double crossSum = 0;
        double xSum = 0;
        double ySum = 0;
        for (var index = 0; index < contour.Count; index++)
        {
            var current = contour[index];
            var next = contour[(index + 1) % contour.Count];
            var cross = current.X * next.Y - next.X * current.Y;
            crossSum += cross;
            xSum += (current.X + next.X) * cross;
            ySum += (current.Y + next.Y) * cross;
        }
        if (Math.Abs(crossSum) <= 0.0001d)
        {
            return new PointF(
                contour.Average(point => point.X),
                contour.Average(point => point.Y));
        }
        return new PointF(
            (float)(xSum / (3d * crossSum)),
            (float)(ySum / (3d * crossSum)));
    }

    private static PointF TweenContourAverage(IReadOnlyList<PointF> contour)
    {
        if (contour.Count == 0) return PointF.Empty;
        return new PointF(
            contour.Average(point => point.X),
            contour.Average(point => point.Y));
    }

    private static float TweenContourPerimeter(IReadOnlyList<PointF> contour)
    {
        var perimeter = 0f;
        for (var index = 0; index < contour.Count; index++)
        {
            perimeter += TweenDistance(contour[index], contour[(index + 1) % contour.Count]);
        }
        return perimeter;
    }

    private static PointF NormalizeTweenPoint(PointF point, RectangleF bounds) => new(
        bounds.Width <= 0.001f ? 0.5f : (point.X - bounds.Left) / bounds.Width,
        bounds.Height <= 0.001f ? 0.5f : (point.Y - bounds.Top) / bounds.Height);

    private static PointF MapTweenNormalizedPoint(PointF point, RectangleF bounds) => new(
        bounds.Left + point.X * bounds.Width,
        bounds.Top + point.Y * bounds.Height);

    private static PointF[] ResampleOpenContour(IReadOnlyList<PointF> contour, int sampleCount)
    {
        if (contour.Count == 0 || sampleCount <= 0) return [];
        if (contour.Count == 1) return Enumerable.Repeat(contour[0], sampleCount).ToArray();

        var lengths = new float[contour.Count - 1];
        var totalLength = 0f;
        for (var index = 0; index < contour.Count - 1; index++)
        {
            totalLength += TweenDistance(contour[index], contour[index + 1]);
            lengths[index] = totalLength;
        }
        if (totalLength <= 0.001f) return Enumerable.Repeat(contour[0], sampleCount).ToArray();

        var result = new PointF[sampleCount];
        var segment = 0;
        var segmentStartDistance = 0f;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var distance = sampleCount == 1
                ? 0
                : totalLength * sample / (sampleCount - 1);
            while (segment < lengths.Length - 1 && distance > lengths[segment])
            {
                segmentStartDistance = lengths[segment];
                segment++;
            }

            var segmentLength = Math.Max(0.001f, lengths[segment] - segmentStartDistance);
            result[sample] = TweenPoint(
                contour[segment],
                contour[segment + 1],
                Math.Clamp((distance - segmentStartDistance) / segmentLength, 0, 1));
        }

        return result;
    }

    private static void AlignOpenContourDirection(IReadOnlyList<PointF> source, PointF[] target)
    {
        if (source.Count < 2 || target.Length < 2) return;
        var forward = TweenDistance(source[0], target[0])
            + TweenDistance(source[^1], target[^1]);
        var reverse = TweenDistance(source[0], target[^1])
            + TweenDistance(source[^1], target[0]);
        if (reverse < forward) Array.Reverse(target);
    }

    private static PointF[] NormalizeTweenContour(IReadOnlyList<PointF> contour)
    {
        var count = contour.Count;
        if (count > 1 && contour[0] == contour[^1]) count--;
        return contour.Take(count).ToArray();
    }

    private static PointF[] ResampleClosedContour(IReadOnlyList<PointF> contour, int sampleCount)
    {
        if (contour.Count == 0 || sampleCount <= 0) return [];
        if (contour.Count == 1) return Enumerable.Repeat(contour[0], sampleCount).ToArray();

        var lengths = new float[contour.Count];
        var perimeter = 0f;
        for (var index = 0; index < contour.Count; index++)
        {
            perimeter += TweenDistance(contour[index], contour[(index + 1) % contour.Count]);
            lengths[index] = perimeter;
        }
        if (perimeter <= 0.001f) return Enumerable.Repeat(contour[0], sampleCount).ToArray();

        var result = new PointF[sampleCount];
        var segment = 0;
        var segmentStartDistance = 0f;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var distance = perimeter * sample / sampleCount;
            while (segment < lengths.Length - 1 && distance > lengths[segment])
            {
                segmentStartDistance = lengths[segment];
                segment++;
            }

            var segmentLength = Math.Max(0.001f, lengths[segment] - segmentStartDistance);
            var amount = Math.Clamp((distance - segmentStartDistance) / segmentLength, 0, 1);
            result[sample] = TweenPoint(
                contour[segment],
                contour[(segment + 1) % contour.Count],
                amount);
        }

        return result;
    }

    private static float TweenSignedArea(IReadOnlyList<PointF> contour)
    {
        double area = 0;
        for (var index = 0; index < contour.Count; index++)
        {
            var current = contour[index];
            var next = contour[(index + 1) % contour.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return (float)(area * 0.5);
    }

    private static float TweenDistance(PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool SamePointTopology(PointF[][] a, PointF[][] b)
    {
        return a.Length == b.Length
            && a.Select((contour, index) => contour.Length == b[index].Length).All(value => value);
    }

    private static bool SameBezierTopology(PathBezierNode[][] a, PathBezierNode[][] b)
    {
        return a.Length == b.Length
            && a.Select((contour, index) => contour.Length == b[index].Length).All(value => value);
    }

    private static float TweenProgress(int start, int end, int frame)
        => end <= start ? 0 : Math.Clamp((frame - start) / (float)(end - start), 0, 1);

    private static float TweenLerp(float a, float b, float t) => a + (b - a) * t;

    private static PointF TweenPoint(PointF a, PointF b, float t)
        => new(TweenLerp(a.X, b.X, t), TweenLerp(a.Y, b.Y, t));

    private static float TweenLerpAngle(float a, float b, float t)
    {
        var delta = b - a;
        while (delta > MathF.PI) delta -= MathF.Tau;
        while (delta < -MathF.PI) delta += MathF.Tau;
        return a + delta * t;
    }

    private static int TweenArgb(int a, int b, float t)
    {
        var left = Color.FromArgb(a);
        var right = Color.FromArgb(b);
        return Color.FromArgb(
            (int)MathF.Round(TweenLerp(left.A, right.A, t)),
            (int)MathF.Round(TweenLerp(left.R, right.R, t)),
            (int)MathF.Round(TweenLerp(left.G, right.G, t)),
            (int)MathF.Round(TweenLerp(left.B, right.B, t))).ToArgb();
    }
}
