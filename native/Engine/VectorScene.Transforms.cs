using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public void TransformObjects(IEnumerable<int> objectIndices, Func<PointF, PointF> transform, bool rebuildGeometryIndex = true)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        ArgumentNullException.ThrowIfNull(transform);
        var targets = objectIndices
            .Distinct()
            .Where(objectIndex => (uint)objectIndex < ObjectCount)
            .ToArray();
        var linkedBoundaries = CaptureLinkedFillBoundariesForTransform(targets);
        TransformObjectsCore(targets, linkedBoundaries, transform, convertPrimitivesToPaths: false, rebuildGeometryIndex);
    }

    internal TransformSession BeginTransformSession(IEnumerable<int> objectIndices)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var targets = objectIndices
            .Distinct()
            .Where(objectIndex => (uint)objectIndex < ObjectCount)
            .ToArray();
        var source = new VectorScene();
        source.CreateEmpty();
        source.EnsureObjectCapacity(targets.Length);
        for (var sourceObject = 0; sourceObject < targets.Length; sourceObject++)
        {
            source.ObjectCount++;
            source.CopyObjectDataFrom(this, targets[sourceObject], sourceObject);
        }

        return new TransformSession(
            this,
            source,
            targets,
            CaptureLinkedFillBoundariesForTransform(targets));
    }

    internal void ApplyTransformSession(
        TransformSession session,
        Func<PointF, PointF> transform,
        bool convertPrimitivesToPaths,
        bool rebuildGeometryIndex = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transform);
        if (!ReferenceEquals(session.Owner, this))
        {
            throw new ArgumentException("The transform session belongs to another scene.", nameof(session));
        }

        for (var sourceObject = 0; sourceObject < session.ObjectIndices.Length; sourceObject++)
        {
            var targetObject = session.ObjectIndices[sourceObject];
            if ((uint)targetObject >= ObjectCount)
            {
                throw new InvalidOperationException("The scene structure changed during the transform session.");
            }

            CopyObjectDataFrom(session.Source, sourceObject, targetObject);
        }

        TransformObjectsCore(
            session.ObjectIndices,
            session.LinkedBoundaries,
            transform,
            convertPrimitivesToPaths,
            rebuildGeometryIndex);
    }

    internal bool ApplyTranslationSessionForPreview(
        TransformSession session,
        float dx,
        float dy)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Owner, this))
        {
            throw new ArgumentException("The transform session belongs to another scene.", nameof(session));
        }
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) return false;

        var source = session.Source;
        var changed = false;
        for (var sourceObject = 0; sourceObject < session.ObjectIndices.Length; sourceObject++)
        {
            var targetObject = session.ObjectIndices[sourceObject];
            if ((uint)targetObject >= ObjectCount)
            {
                throw new InvalidOperationException("The scene structure changed during the transform session.");
            }

            var nextX = VectorUnits.Quantize(source.X[sourceObject] + dx);
            var nextY = VectorUnits.Quantize(source.Y[sourceObject] + dy);
            var nextControlX = VectorUnits.Quantize(source.CurveControlX[sourceObject] + dx);
            var nextControlY = VectorUnits.Quantize(source.CurveControlY[sourceObject] + dy);
            var nextControl2X = VectorUnits.Quantize(source.CurveControl2X[sourceObject] + dx);
            var nextControl2Y = VectorUnits.Quantize(source.CurveControl2Y[sourceObject] + dy);
            var hasGradient = source.HasGradient(sourceObject);
            var nextGradientStartX = hasGradient
                ? VectorUnits.Quantize(source.GradientStartX[sourceObject] + dx)
                : GradientStartX[targetObject];
            var nextGradientStartY = hasGradient
                ? VectorUnits.Quantize(source.GradientStartY[sourceObject] + dy)
                : GradientStartY[targetObject];
            var nextGradientEndX = hasGradient
                ? VectorUnits.Quantize(source.GradientEndX[sourceObject] + dx)
                : GradientEndX[targetObject];
            var nextGradientEndY = hasGradient
                ? VectorUnits.Quantize(source.GradientEndY[sourceObject] + dy)
                : GradientEndY[targetObject];
            var translationX = nextX - source.X[sourceObject];
            var translationY = nextY - source.Y[sourceObject];
            var nextDistortions = source._objectDistortions.TryGetValue(sourceObject, out var sourceDistortions)
                ? sourceDistortions
                    .Select(distortion => distortion.Transform(point => new PointF(
                        point.X + translationX,
                        point.Y + translationY)))
                    .ToArray()
                : [];
            var distortionsChanged = _objectDistortions.TryGetValue(targetObject, out var targetDistortions)
                ? !DistortionsEqual(targetDistortions, nextDistortions)
                : nextDistortions.Length > 0;
            if (X[targetObject] == nextX
                && Y[targetObject] == nextY
                && CurveControlX[targetObject] == nextControlX
                && CurveControlY[targetObject] == nextControlY
                && CurveControl2X[targetObject] == nextControl2X
                && CurveControl2Y[targetObject] == nextControl2Y
                && GradientStartX[targetObject] == nextGradientStartX
                && GradientStartY[targetObject] == nextGradientStartY
                && GradientEndX[targetObject] == nextGradientEndX
                && GradientEndY[targetObject] == nextGradientEndY
                && !distortionsChanged)
            {
                continue;
            }

            X[targetObject] = nextX;
            Y[targetObject] = nextY;
            CurveControlX[targetObject] = nextControlX;
            CurveControlY[targetObject] = nextControlY;
            CurveControl2X[targetObject] = nextControl2X;
            CurveControl2Y[targetObject] = nextControl2Y;
            if (hasGradient)
            {
                GradientStartX[targetObject] = nextGradientStartX;
                GradientStartY[targetObject] = nextGradientStartY;
                GradientEndX[targetObject] = nextGradientEndX;
                GradientEndY[targetObject] = nextGradientEndY;
            }
            if (nextDistortions.Length == 0) _objectDistortions.Remove(targetObject);
            else _objectDistortions[targetObject] = CloneDistortions(nextDistortions);
            changed = true;
        }

        if (!changed) return false;
        var linkedBoundariesChanged = UpdateFillBoundaryLineLinks(
            session.LinkedBoundaries,
            rebuildGeometryIndex: false);
        if (changed && !linkedBoundariesChanged) GeometryRevision++;
        return true;
    }

    private void TransformObjectsCore(
        IReadOnlyList<int> targets,
        IReadOnlyList<FillBoundaryLineLink> linkedBoundaries,
        Func<PointF, PointF> transform,
        bool convertPrimitivesToPaths,
        bool rebuildGeometryIndex)
    {
        foreach (var objectIndex in targets)
        {
            var shape = ShapeKind[objectIndex];
            if (convertPrimitivesToPaths
                && shape is VectorAnimationEngine.ShapeKind.Rectangle
                    or VectorAnimationEngine.ShapeKind.Ellipse
                    or VectorAnimationEngine.ShapeKind.Triangle
                    or VectorAnimationEngine.ShapeKind.Polygon
                    or VectorAnimationEngine.ShapeKind.Star)
            {
                ConvertPrimitiveToTransformedPath(objectIndex, transform);
            }
            else if (!convertPrimitivesToPaths
                || shape != VectorAnimationEngine.ShapeKind.Text
                || !TryConvertTextToTransformedPath(objectIndex, transform))
            {
                TransformObject(objectIndex, transform);
            }
            TransformObjectDistortions(objectIndex, transform);
        }

        UpdateFillBoundaryLineLinks(linkedBoundaries, rebuildGeometryIndex: false);
        if (!rebuildGeometryIndex)
        {
            if (targets.Count > 0) GeometryRevision++;
            return;
        }
        RebuildGeometryIndex();
        RebuildSummaries();
    }

    public bool FlipObjects(IEnumerable<int> objectIndices, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var targets = objectIndices
            .Distinct()
            .Where(objectIndex => (uint)objectIndex < ObjectCount)
            .ToArray();
        if (targets.Length == 0) return false;

        var bounds = GetObjectWorldBounds(targets[0]);
        for (var index = 1; index < targets.Length; index++)
        {
            bounds = RectangleF.Union(bounds, GetObjectWorldBounds(targets[index]));
        }

        var centerX = bounds.Left + bounds.Width * 0.5f;
        var centerY = bounds.Top + bounds.Height * 0.5f;
        ShearObjects(
            targets,
            horizontal
                ? point => new PointF(centerX * 2 - point.X, point.Y)
                : point => new PointF(point.X, centerY * 2 - point.Y));
        return true;
    }

    public bool CanMoveObjectsInLayerStack(IEnumerable<int> objectIndices, int direction, int frame)
    {
        return CreateObjectStackMovePlan(objectIndices, direction, frame).Count > 0;
    }

    public bool MoveObjectsInLayerStack(IEnumerable<int> objectIndices, int direction, int frame)
    {
        var plan = CreateObjectStackMovePlan(objectIndices, direction, frame);
        if (plan.Count == 0) return false;

        for (var index = 0; index < ObjectCount; index++)
        {
            var key = (ObjectLayer[index], ObjectKeyframeFrame[index], ObjectOrder[index]);
            if (plan.TryGetValue(key, out var nextOrder)) ObjectOrder[index] = nextOrder;
        }

        GeometryRevision++;
        RebuildSummaries();
        return true;
    }

    private Dictionary<(int Layer, int Keyframe, long Order), long> CreateObjectStackMovePlan(
        IEnumerable<int> objectIndices,
        int direction,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        if (direction == 0 || ObjectCount == 0) return [];

        var targets = objectIndices
            .Distinct()
            .Where(index => (uint)index < ObjectCount && IsObjectActive(index, frame))
            .ToArray();
        if (targets.Length == 0) return [];

        var plan = new Dictionary<(int Layer, int Keyframe, long Order), long>();
        foreach (var scope in targets.GroupBy(index => (ObjectLayer[index], ObjectKeyframeFrame[index])))
        {
            var selectedOrders = scope.Select(index => ObjectOrder[index]).ToHashSet();
            var orderSlots = Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == scope.Key.Item1
                    && ObjectKeyframeFrame[index] == scope.Key.Item2
                    && IsObjectActive(index, frame))
                .Select(index => ObjectOrder[index])
                .Distinct()
                .OrderBy(order => order)
                .ToArray();
            if (orderSlots.Length < 2) continue;

            var arranged = orderSlots
                .Select(order => (SourceOrder: order, Selected: selectedOrders.Contains(order)))
                .ToArray();
            var moved = false;
            if (direction > 0)
            {
                for (var index = arranged.Length - 2; index >= 0; index--)
                {
                    if (!arranged[index].Selected || arranged[index + 1].Selected) continue;
                    (arranged[index], arranged[index + 1]) = (arranged[index + 1], arranged[index]);
                    moved = true;
                }
            }
            else
            {
                for (var index = 1; index < arranged.Length; index++)
                {
                    if (!arranged[index].Selected || arranged[index - 1].Selected) continue;
                    (arranged[index], arranged[index - 1]) = (arranged[index - 1], arranged[index]);
                    moved = true;
                }
            }

            if (!moved) continue;
            for (var index = 0; index < arranged.Length; index++)
            {
                if (arranged[index].SourceOrder == orderSlots[index]) continue;
                plan[(scope.Key.Item1, scope.Key.Item2, arranged[index].SourceOrder)] = orderSlots[index];
            }
        }

        return plan;
    }

    // Composition previews are rebuilt on commit, so keep local geometry arrays
    // stable during translation and avoid spatial-index/cache churn per pointer sample.
    internal void TranslateObjectsForPreview(ReadOnlySpan<int> objectIndices, float dx, float dy)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy)
            || (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f))
        {
            return;
        }

        var changed = false;
        foreach (var objectIndex in objectIndices)
        {
            if ((uint)objectIndex >= ObjectCount) continue;
            TranslateObjectForPreview(objectIndex, dx, dy);
            changed = true;
        }

        if (changed) GeometryRevision++;
    }

    internal void TranslateAllObjectsForPreview(float dx, float dy)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy)
            || ObjectCount == 0
            || (Math.Abs(dx) <= 0.0001f && Math.Abs(dy) <= 0.0001f))
        {
            return;
        }

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            TranslateObjectForPreview(objectIndex, dx, dy);
        }

        GeometryRevision++;
    }

    private void TranslateObjectForPreview(int objectIndex, float dx, float dy)
    {
        X[objectIndex] = VectorUnits.Quantize(X[objectIndex] + dx);
        Y[objectIndex] = VectorUnits.Quantize(Y[objectIndex] + dy);
        CurveControlX[objectIndex] = VectorUnits.Quantize(CurveControlX[objectIndex] + dx);
        CurveControlY[objectIndex] = VectorUnits.Quantize(CurveControlY[objectIndex] + dy);
        CurveControl2X[objectIndex] = VectorUnits.Quantize(CurveControl2X[objectIndex] + dx);
        CurveControl2Y[objectIndex] = VectorUnits.Quantize(CurveControl2Y[objectIndex] + dy);
        TranslateObjectDistortionsForPreview(objectIndex, dx, dy);
        if (!HasGradient(objectIndex)) return;
        GradientStartX[objectIndex] = VectorUnits.Quantize(GradientStartX[objectIndex] + dx);
        GradientStartY[objectIndex] = VectorUnits.Quantize(GradientStartY[objectIndex] + dy);
        GradientEndX[objectIndex] = VectorUnits.Quantize(GradientEndX[objectIndex] + dx);
        GradientEndY[objectIndex] = VectorUnits.Quantize(GradientEndY[objectIndex] + dy);
    }

    private bool TransformObjectDistortions(int objectIndex, Func<PointF, PointF> transform)
    {
        if (!_objectDistortions.TryGetValue(objectIndex, out var current)) return false;
        var transformed = current.Select(distortion => distortion.Transform(transform)).ToArray();
        if (DistortionsEqual(current, transformed)) return false;
        _objectDistortions[objectIndex] = transformed;
        return true;
    }

    private bool TranslateObjectDistortionsForPreview(int objectIndex, float dx, float dy)
    {
        if (!_objectDistortions.TryGetValue(objectIndex, out var current)) return false;
        var transform = System.Numerics.Matrix3x2.CreateTranslation(dx, dy);
        var transformed = new DistortWarp[current.Length];
        for (var index = 0; index < current.Length; index++)
        {
            transformed[index] = current[index].AffineTransform(transform);
        }

        if (DistortionsEqual(current, transformed)) return false;
        _objectDistortions[objectIndex] = transformed;
        return true;
    }

    public void ShearObjects(
        IEnumerable<int> objectIndices,
        Func<PointF, PointF> transform,
        bool rebuildGeometryIndex = true)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        ArgumentNullException.ThrowIfNull(transform);
        var targets = objectIndices
            .Distinct()
            .Where(objectIndex => (uint)objectIndex < ObjectCount)
            .ToArray();
        var linkedBoundaries = CaptureLinkedFillBoundariesForTransform(targets);
        TransformObjectsCore(targets, linkedBoundaries, transform, convertPrimitivesToPaths: true, rebuildGeometryIndex);
    }

    internal void ApplyOpacity(float opacity)
    {
        var factor = Math.Clamp(opacity, 0, 1);
        for (var index = 0; index < ObjectCount; index++)
        {
            Argb[index] = ApplyOpacity(Argb[index], factor);
            StrokeArgb[index] = ApplyOpacity(StrokeArgb[index], factor);
            GradientStartArgb[index] = ApplyOpacity(GradientStartArgb[index], factor);
            GradientEndArgb[index] = ApplyOpacity(GradientEndArgb[index], factor);
            if (_gradientStops.TryGetValue(index, out var stops))
            {
                _gradientStops[index] = stops
                    .Select(stop => new GradientStop(stop.Position, ApplyOpacity(stop.Argb, factor)))
                    .ToArray();
            }
            if (_mixingStrokeLocalSamples.TryGetValue(index, out var mixingSamples))
            {
                _mixingStrokeLocalSamples[index] = mixingSamples
                    .Select(sample => sample with { Argb = ApplyOpacity(sample.Argb, factor) })
                    .ToArray();
            }
            if (_mixingStrokeLocalRegions.TryGetValue(index, out var mixingRegion))
            {
                _mixingStrokeLocalRegions[index] = ApplyMixingRegionColor(
                    mixingRegion,
                    argb => ApplyOpacity(argb, factor));
            }
        }

        RebuildSummaries();
    }

    internal int[] AppendFlattenedSceneToLayer(VectorScene source, int destinationLayer, int frame)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)destinationLayer >= LayerCount
            || GetLayerKind(destinationLayer) != DrawingLayerKind.Drawing
            || IsLayerEffectivelyLocked(destinationLayer))
        {
            throw new InvalidOperationException("Break Apart requires an unlocked drawing layer.");
        }
        if (source.HasLayerEffects)
        {
            throw new InvalidDataException("Break Apart cannot flatten symbols that use folders, masks, or blend modes.");
        }

        var sourceObjects = Enumerable.Range(0, source.ObjectCount)
            .Where(index => source.IsObjectActive(index, 0)
                && source.IsLayerEffectivelyVisible(source.ObjectLayer[index]))
            .OrderByDescending(index => source.ObjectLayer[index])
            .ThenBy(index => source.ObjectOrder[index])
            .ThenBy(index => source.ObjectSubOrder[index])
            .ToArray();
        if (sourceObjects.Length == 0) throw new InvalidDataException("Break Apart produced no visible geometry.");
        if (sourceObjects.Any(index => source.ShapeKind[index] is VectorAnimationEngine.ShapeKind.ImportedSvg
                or VectorAnimationEngine.ShapeKind.Bitmap))
        {
            throw new InvalidOperationException("Break Apart cannot materialize opaque raster objects.");
        }

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            EditFrame = Math.Max(0, frame);
            var keyframe = EnsureWritableKeyframe(destinationLayer, EditFrame);
            var existingOrders = Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == destinationLayer && ObjectKeyframeFrame[index] == keyframe)
                .Select(index => ObjectOrder[index])
                .ToArray();
            var hasExistingObjects = existingOrders.Length > 0;
            var firstOrder = hasExistingObjects
                ? checked(existingOrders.Min() - sourceObjects.Length)
                : _nextObjectOrder + 1;

            EnsureObjectCapacity(checked(ObjectCount + sourceObjects.Length));
            var produced = new int[sourceObjects.Length];
            for (var offset = 0; offset < sourceObjects.Length; offset++)
            {
                var sourceObject = sourceObjects[offset];
                var destinationObject = ObjectCount++;
                CopyObjectDataFrom(source, sourceObject, destinationObject);
                ObjectLayer[destinationObject] = (ushort)destinationLayer;
                ObjectKeyframeFrame[destinationObject] = keyframe;
                ObjectOrder[destinationObject] = firstOrder + offset;
                ObjectSubOrder[destinationObject] = 0;
                ApplyObjectOpacity(destinationObject, source.LayerOpacity[source.ObjectLayer[sourceObject]]);
                VirtualAtomCount += AtomCount[destinationObject];
                produced[offset] = destinationObject;
            }
            if (!hasExistingObjects) _nextObjectOrder += sourceObjects.Length;

            SynchronizeAllKeyframeContentKinds();
            RebuildGeometryIndex();
            RebuildSummaries();
            return produced;
        }
        catch
        {
            RestoreSnapshot(snapshot);
            throw;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    private void ApplyObjectOpacity(int objectIndex, float opacity)
    {
        var factor = Math.Clamp(opacity, 0, 1);
        Argb[objectIndex] = ApplyOpacity(Argb[objectIndex], factor);
        StrokeArgb[objectIndex] = ApplyOpacity(StrokeArgb[objectIndex], factor);
        GradientStartArgb[objectIndex] = ApplyOpacity(GradientStartArgb[objectIndex], factor);
        GradientEndArgb[objectIndex] = ApplyOpacity(GradientEndArgb[objectIndex], factor);
        if (_gradientStops.TryGetValue(objectIndex, out var stops))
        {
            _gradientStops[objectIndex] = stops
                .Select(stop => new GradientStop(stop.Position, ApplyOpacity(stop.Argb, factor)))
                .ToArray();
        }
        if (_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var mixingSamples))
        {
            _mixingStrokeLocalSamples[objectIndex] = mixingSamples
                .Select(sample => sample with { Argb = ApplyOpacity(sample.Argb, factor) })
                .ToArray();
        }
        if (_mixingStrokeLocalRegions.TryGetValue(objectIndex, out var mixingRegion))
        {
            _mixingStrokeLocalRegions[objectIndex] = ApplyMixingRegionColor(
                mixingRegion,
                argb => ApplyOpacity(argb, factor));
        }
    }

    private void ApplyOnionSkinAppearance(int objectIndex, float opacity, int tintArgb)
    {
        var factor = Math.Clamp(opacity, 0, 1);
        Argb[objectIndex] = ApplyOnionSkinOpacity(Argb[objectIndex], factor, tintArgb);
        StrokeArgb[objectIndex] = ApplyOnionSkinOpacity(StrokeArgb[objectIndex], factor, tintArgb);
        GradientStartArgb[objectIndex] = ApplyOnionSkinOpacity(GradientStartArgb[objectIndex], factor, tintArgb);
        GradientEndArgb[objectIndex] = ApplyOnionSkinOpacity(GradientEndArgb[objectIndex], factor, tintArgb);
        if (_gradientStops.TryGetValue(objectIndex, out var stops))
        {
            _gradientStops[objectIndex] = stops
                .Select(stop => new GradientStop(stop.Position, ApplyOnionSkinOpacity(stop.Argb, factor, tintArgb)))
                .ToArray();
        }
        if (_mixingStrokeLocalSamples.TryGetValue(objectIndex, out var mixingSamples))
        {
            _mixingStrokeLocalSamples[objectIndex] = mixingSamples
                .Select(sample => sample with
                {
                    Argb = ApplyOnionSkinOpacity(sample.Argb, factor, tintArgb)
                })
                .ToArray();
        }
        if (_mixingStrokeLocalRegions.TryGetValue(objectIndex, out var mixingRegion))
        {
            _mixingStrokeLocalRegions[objectIndex] = ApplyMixingRegionColor(
                mixingRegion,
                argb => ApplyOnionSkinOpacity(argb, factor, tintArgb));
        }
    }

    private static int ApplyOnionSkinOpacity(int argb, float factor, int tintArgb)
    {
        var alpha = (int)Math.Clamp(((argb >>> 24) & 0xff) * factor, 0, 255);
        return (tintArgb & 0x00ffffff) | (alpha << 24);
    }

    private static int ApplyOpacity(int argb, float factor)
    {
        var alpha = (int)Math.Clamp(((argb >>> 24) & 0xff) * factor, 0, 255);
        return (argb & 0x00ffffff) | (alpha << 24);
    }

    private static MixingBrushRegionData ApplyMixingRegionColor(
        MixingBrushRegionData region,
        Func<int, int> transform)
    {
        var vertices = region.Vertices
            .Select(vertex => vertex with { Argb = transform(vertex.Argb) })
            .ToArray();
        return new MixingBrushRegionData(vertices, region.TriangleIndices);
    }

    private static int MixingBrushRegionSummaryArgb(MixingBrushRegionData region)
    {
        for (var index = region.Vertices.Length - 1; index >= 0; index--)
        {
            if (((uint)region.Vertices[index].Argb >> 24) > 0) return region.Vertices[index].Argb;
        }
        return region.Vertices.Length > 0 ? region.Vertices[^1].Argb : Color.Transparent.ToArgb();
    }

    private void TransformObject(int objectIndex, Func<PointF, PointF> transform)
    {
        var hasGradient = HasGradient(objectIndex);
        var gradientStart = hasGradient ? GetGradientStart(objectIndex) : PointF.Empty;
        var gradientEnd = hasGradient ? GetGradientEnd(objectIndex) : PointF.Empty;
        var gradientPath = TryGetGradientPathWorldPoints(objectIndex, out var pathGradient) ? pathGradient : Array.Empty<PointF>();
        var shapeGradientMapping = TryGetShapeGradientMappingWorldContours(objectIndex, out var mappingContours)
            ? mappingContours
            : Array.Empty<PointF[]>();
        var shape = ShapeKind[objectIndex];
        if (shape == VectorAnimationEngine.ShapeKind.Text
            && !CanRepresentTextTransform(objectIndex, transform)
            && TryConvertTextToTransformedPath(objectIndex, transform))
        {
            return;
        }
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(objectIndex);
            SetLineCurve(
                objectIndex,
                transform(curve.Start),
                transform(curve.Control1),
                transform(curve.Control2),
                transform(curve.End));
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path
            && TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
        {
            TransformPathBezierContours(bezierContours, transform);
            if (!SetPathBezierContoursCore(objectIndex, bezierContours)) return;
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
            if (shapeGradientMapping.Length > 0)
            {
                SetShapeGradientMapping(
                    objectIndex,
                    shapeGradientMapping.Select(contour => contour.Select(transform).ToArray()).ToArray());
            }
            if (gradientPath.Length > 1) SetGradientPath(objectIndex, gradientPath.Select(transform).ToArray());
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(objectIndex, out var contours))
        {
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++) contour[pointIndex] = transform(contour[pointIndex]);
            }

            SetPathContours(objectIndex, contours);
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
            if (shapeGradientMapping.Length > 0)
            {
                SetShapeGradientMapping(
                    objectIndex,
                    shapeGradientMapping.Select(contour => contour.Select(transform).ToArray()).ToArray());
            }
            if (gradientPath.Length > 1) SetGradientPath(objectIndex, gradientPath.Select(transform).ToArray());
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke
            && TryGetMixingBrushWorldRegion(objectIndex, out var mixingRegion))
        {
            var transformedVertices = mixingRegion.Vertices
                .Select(vertex => vertex with { Point = transform(vertex.Point) })
                .ToArray();
            SetMixingBrushRegion(
                objectIndex,
                new MixingBrushRegionData(transformedVertices, mixingRegion.TriangleIndices));
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.MixingStroke
            && TryGetMixingStrokeWorldSamples(objectIndex, out var mixingSamples))
        {
            for (var sampleIndex = 0; sampleIndex < mixingSamples.Length; sampleIndex++)
            {
                var sample = mixingSamples[sampleIndex];
                var radius = sample.Diameter * 0.5f;
                var transformedPoint = transform(sample.Point);
                var transformedSampleXAxis = transform(new PointF(sample.Point.X + radius, sample.Point.Y));
                var transformedSampleYAxis = transform(new PointF(sample.Point.X, sample.Point.Y + radius));
                var diameterX = Distance(transformedPoint, transformedSampleXAxis) * 2f;
                var diameterY = Distance(transformedPoint, transformedSampleYAxis) * 2f;
                var transformedDiameter = MathF.Sqrt(Math.Max(0f, diameterX * diameterY));
                mixingSamples[sampleIndex] = sample with
                {
                    Point = transformedPoint,
                    Diameter = float.IsFinite(transformedDiameter) && transformedDiameter > 0
                        ? transformedDiameter
                        : sample.Diameter
                };
            }

            SetMixingStrokeSamples(objectIndex, mixingSamples);
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Freeform
            && TryGetFreehandBezierWorldNodes(objectIndex, out var freehandNodes))
        {
            TransformFreehandBezierNodes(freehandNodes, transform);
            if (!SetFreehandBezierNodesCore(objectIndex, freehandNodes)) return;
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
            return;
        }

        if (IsFreehandShape(shape) && TryGetFreehandWorldPoints(objectIndex, out var points))
        {
            for (var pointIndex = 0; pointIndex < points.Length; pointIndex++) points[pointIndex] = transform(points[pointIndex]);
            SetFreehandPoints(objectIndex, points);
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
            return;
        }

        var center = new PointF(X[objectIndex], Y[objectIndex]);
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var xAxis = LocalToWorld(objectIndex, halfWidth, 0);
        var yAxis = LocalToWorld(objectIndex, 0, halfHeight);
        var transformedCenter = transform(center);
        var transformedXAxis = transform(xAxis);
        var transformedYAxis = transform(yAxis);
        var dx = transformedXAxis.X - transformedCenter.X;
        var dy = transformedXAxis.Y - transformedCenter.Y;

        X[objectIndex] = VectorUnits.Quantize(transformedCenter.X);
        Y[objectIndex] = VectorUnits.Quantize(transformedCenter.Y);
        Width[objectIndex] = Math.Max(1, VectorUnits.Quantize(2 * Distance(transformedCenter, transformedXAxis)));
        Height[objectIndex] = Math.Max(1, VectorUnits.Quantize(2 * Distance(transformedCenter, transformedYAxis)));
        Angle[objectIndex] = MathF.Atan2(dy, dx);
        var transformedControl = transform(new PointF(CurveControlX[objectIndex], CurveControlY[objectIndex]));
        CurveControlX[objectIndex] = VectorUnits.Quantize(transformedControl.X);
        CurveControlY[objectIndex] = VectorUnits.Quantize(transformedControl.Y);
        var transformedControl2 = transform(new PointF(CurveControl2X[objectIndex], CurveControl2Y[objectIndex]));
        CurveControl2X[objectIndex] = VectorUnits.Quantize(transformedControl2.X);
        CurveControl2Y[objectIndex] = VectorUnits.Quantize(transformedControl2.Y);
        if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
    }

    private void ConvertPrimitiveToTransformedPath(int objectIndex, Func<PointF, PointF> transform)
    {
        var hasGradient = HasGradient(objectIndex);
        var gradientStart = hasGradient ? GetGradientStart(objectIndex) : PointF.Empty;
        var gradientEnd = hasGradient ? GetGradientEnd(objectIndex) : PointF.Empty;
        var shapeGradientMapping = TryGetShapeGradientMappingWorldContours(objectIndex, out var mappingContours)
            ? mappingContours
            : Array.Empty<PointF[]>();
        PathBezierNode[][] contours;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            contours = [CreateBezierContour(EllipseBoundaryCurves(objectIndex))];
        }
        else
        {
            var boundary = OpenPolygon(ShapeBoundary(objectIndex));
            if (boundary.Length < 3) return;
            contours = [CreateLinearBezierContour(boundary)];
        }

        TransformPathBezierContours(contours, transform);
        if (!SetPathBezierContoursCore(objectIndex, contours)) return;
        if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
        if (shapeGradientMapping.Length > 0)
        {
            SetShapeGradientMapping(
                objectIndex,
                shapeGradientMapping.Select(source => source.Select(transform).ToArray()).ToArray());
        }
    }

    private bool TryConvertTextToTransformedPath(int objectIndex, Func<PointF, PointF> transform)
    {
        if (!TryGetTextWorldContours(objectIndex, out var contours) || contours.Length == 0) return false;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
            {
                contour[pointIndex] = transform(contour[pointIndex]);
            }
        }

        ShapeKind[objectIndex] = VectorAnimationEngine.ShapeKind.Path;
        _textObjects.Remove(objectIndex);
        Stroke[objectIndex] = 0;
        StrokeArgb[objectIndex] = Color.Transparent.ToArgb();
        DisableLinearGradient(objectIndex);
        SetPathContours(objectIndex, contours);
        return true;
    }

    private bool CanRepresentTextTransform(int objectIndex, Func<PointF, PointF> transform)
    {
        var center = new PointF(X[objectIndex], Y[objectIndex]);
        var xAxis = LocalToWorld(objectIndex, Width[objectIndex] * 0.5f, 0);
        var yAxis = LocalToWorld(objectIndex, 0, Height[objectIndex] * 0.5f);
        var transformedCenter = transform(center);
        var transformedXAxis = transform(xAxis);
        var transformedYAxis = transform(yAxis);
        var xVector = new PointF(
            transformedXAxis.X - transformedCenter.X,
            transformedXAxis.Y - transformedCenter.Y);
        var yVector = new PointF(
            transformedYAxis.X - transformedCenter.X,
            transformedYAxis.Y - transformedCenter.Y);
        var xLength = MathF.Sqrt(xVector.X * xVector.X + xVector.Y * xVector.Y);
        var yLength = MathF.Sqrt(yVector.X * yVector.X + yVector.Y * yVector.Y);
        if (!float.IsFinite(xLength) || !float.IsFinite(yLength) || xLength <= 0 || yLength <= 0) return false;

        var determinant = xVector.X * yVector.Y - xVector.Y * yVector.X;
        var axisDot = xVector.X * yVector.X + xVector.Y * yVector.Y;
        return determinant > 0 && Math.Abs(axisDot) <= 0.0001f * xLength * yLength;
    }

    private void SetLineCurve(int objectIndex, PointF start, PointF control1, PointF control2, PointF end)
    {
        var center = Midpoint(start, end);
        X[objectIndex] = VectorUnits.Quantize(center.X);
        Y[objectIndex] = VectorUnits.Quantize(center.Y);
        Width[objectIndex] = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, VectorUnits.Quantize(Distance(start, end)));
        Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), Height[objectIndex]);
        Angle[objectIndex] = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        CurveControlX[objectIndex] = VectorUnits.Quantize(control1.X);
        CurveControlY[objectIndex] = VectorUnits.Quantize(control1.Y);
        CurveControl2X[objectIndex] = VectorUnits.Quantize(control2.X);
        CurveControl2Y[objectIndex] = VectorUnits.Quantize(control2.Y);
        InvalidateDeferredTopologyQueries();
    }

    private void SetPathContours(int objectIndex, PointF[][] contours)
    {
        var gradientPath = TryGetGradientPathWorldPoints(objectIndex, out var existingGradientPath)
            ? existingGradientPath
            : Array.Empty<PointF>();
        var shapeGradientMapping = TryGetShapeGradientMappingWorldContours(objectIndex, out var existingShapeMapping)
            ? existingShapeMapping
            : Array.Empty<PointF[]>();
        var points = contours.SelectMany(contour => contour).ToArray();
        if (points.Length == 0) return;
        var left = points.Min(point => point.X);
        var right = points.Max(point => point.X);
        var top = points.Min(point => point.Y);
        var bottom = points.Max(point => point.Y);
        var center = VectorUnits.Quantize(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(1, VectorUnits.Quantize(right - left));
        Height[objectIndex] = Math.Max(1, VectorUnits.Quantize(bottom - top));
        Angle[objectIndex] = 0;
        _pathLocalContours[objectIndex] = contours
            .Select(contour => contour.Select(point => new PointF(
                VectorUnits.Quantize(point.X - center.X),
                VectorUnits.Quantize(point.Y - center.Y))).ToArray())
            .ToArray();
        _pathBezierLocalContours.Remove(objectIndex);
        if (gradientPath.Length > 1) SetGradientPath(objectIndex, gradientPath);
        if (shapeGradientMapping.Length > 0) SetShapeGradientMapping(objectIndex, shapeGradientMapping);
    }

    private void SetFreehandPoints(int objectIndex, PointF[] points)
    {
        points = NormalizeFreehandPoints(points);
        if (points.Length == 0) return;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Freeform
            && TryCreateOpenFreehandBezierNodes(points, out var nodes)
            && SetFreehandBezierNodesCore(objectIndex, nodes))
        {
            return;
        }

        var left = points.Min(point => point.X);
        var right = points.Max(point => point.X);
        var top = points.Min(point => point.Y);
        var bottom = points.Max(point => point.Y);
        var center = VectorUnits.Quantize(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(Stroke[objectIndex], VectorUnits.Quantize(right - left + Stroke[objectIndex]));
        Height[objectIndex] = Math.Max(Stroke[objectIndex], VectorUnits.Quantize(bottom - top + Stroke[objectIndex]));
        Angle[objectIndex] = 0;
        _freehandLocalPoints[objectIndex] = points
            .Select(point => new PointF(
                VectorUnits.Quantize(point.X - center.X),
                VectorUnits.Quantize(point.Y - center.Y)))
            .ToArray();
        _freehandBezierLocalNodes.Remove(objectIndex);
        _legacyFreehandBezierNodeCache.Remove(objectIndex);
    }

    private void SetMixingStrokeSamples(
        int objectIndex,
        IReadOnlyList<MixingBrushTrajectorySample> worldSamples)
    {
        var samples = NormalizeMixingStrokeSamples(worldSamples);
        if (samples.Length == 0) return;

        var firstRadius = samples[0].Diameter * 0.5f;
        var left = samples[0].Point.X - firstRadius;
        var right = samples[0].Point.X + firstRadius;
        var top = samples[0].Point.Y - firstRadius;
        var bottom = samples[0].Point.Y + firstRadius;
        for (var index = 1; index < samples.Length; index++)
        {
            var radius = samples[index].Diameter * 0.5f;
            left = Math.Min(left, samples[index].Point.X - radius);
            right = Math.Max(right, samples[index].Point.X + radius);
            top = Math.Min(top, samples[index].Point.Y - radius);
            bottom = Math.Max(bottom, samples[index].Point.Y + radius);
        }

        var center = VectorUnits.Quantize(new PointF(
            left + (right - left) * 0.5f,
            top + (bottom - top) * 0.5f));
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(
            VectorUnits.MinimumStrokeUnits,
            VectorUnits.Quantize(right - left));
        Height[objectIndex] = Math.Max(
            VectorUnits.MinimumStrokeUnits,
            VectorUnits.Quantize(bottom - top));
        Angle[objectIndex] = 0;
        Argb[objectIndex] = samples[^1].Argb;
        _mixingStrokeLocalSamples[objectIndex] = samples
            .Select(sample => sample with
            {
                Point = new PointF(
                    VectorUnits.Quantize(sample.Point.X - center.X),
                    VectorUnits.Quantize(sample.Point.Y - center.Y))
            })
            .ToArray();
        _mixingStrokeLocalRegions.Remove(objectIndex);
    }

    private bool SetMixingBrushRegion(int objectIndex, MixingBrushRegionData worldRegion)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke
            || worldRegion is null
            || !MixingBrushRegionData.TryNormalize(
                worldRegion.Vertices,
                worldRegion.TriangleIndices,
                out var normalized))
        {
            return false;
        }

        var bounds = normalized.CalculateBounds();
        var center = VectorUnits.Quantize(new PointF(
            bounds.Left + bounds.Width * 0.5f,
            bounds.Top + bounds.Height * 0.5f));
        var localVertices = normalized.Vertices
            .Select(vertex => vertex with
            {
                Point = new PointF(
                    VectorUnits.Quantize(vertex.Point.X - center.X),
                    VectorUnits.Quantize(vertex.Point.Y - center.Y))
            })
            .ToArray();
        var localRegion = new MixingBrushRegionData(localVertices, normalized.TriangleIndices);

        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(
            VectorUnits.MinimumStrokeUnits,
            VectorUnits.Quantize(bounds.Width));
        Height[objectIndex] = Math.Max(
            VectorUnits.MinimumStrokeUnits,
            VectorUnits.Quantize(bounds.Height));
        Angle[objectIndex] = 0;
        Argb[objectIndex] = MixingBrushRegionSummaryArgb(localRegion);
        _mixingStrokeLocalRegions[objectIndex] = localRegion;
        _mixingStrokeLocalSamples.Remove(objectIndex);
        return true;
    }

    internal int AppendCurveSegment(
        int layer,
        PointF start,
        PointF control,
        PointF end,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms,
        LineEndpointStyle startEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle? endEndpointStyle = null)
    {
        return AppendCubicCurveSegment(
            layer,
            start,
            Lerp(start, control, 2f / 3f),
            Lerp(end, control, 2f / 3f),
            end,
            stroke,
            color,
            strokeColor,
            atoms,
            startEndpointStyle,
            endEndpointStyle);
    }

    internal int AppendCubicCurveSegment(
        int layer,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms,
        LineEndpointStyle startEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle? endEndpointStyle = null)
    {
        var center = Midpoint(start, end);
        var width = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, Distance(start, end));
        var height = Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2));
        var index = AppendPackedObject(
            layer,
            center,
            new SizeF(width, height),
            MathF.Atan2(end.Y - start.Y, end.X - start.X),
            stroke,
            color.ToArgb(),
            strokeColor.ToArgb(),
            atoms,
            VectorAnimationEngine.ShapeKind.Line,
            control1,
            startEndpointStyle,
            endEndpointStyle ?? startEndpointStyle,
            curveControl2: control2);
        var margin = Math.Max(stroke * 0.5f, 1);
        var curveExtent = Math.Max(
            Math.Max(Math.Abs(start.X - X[index]), Math.Abs(end.X - X[index])),
            Math.Max(Math.Abs(start.Y - Y[index]), Math.Abs(end.Y - Y[index])));
        curveExtent = Math.Max(curveExtent, Math.Max(Math.Abs(control1.X - X[index]), Math.Abs(control1.Y - Y[index])));
        curveExtent = Math.Max(curveExtent, Math.Max(Math.Abs(control2.X - X[index]), Math.Abs(control2.Y - Y[index]))) + margin;
        MaxHalfExtent = Math.Max(MaxHalfExtent, curveExtent);
        return index;
    }

}
