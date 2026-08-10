using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public int AddPathObject(int layer, IReadOnlyList<PointF> worldPoints, float stroke, Color color, Color strokeColor, uint atoms)
    {
        return AddPathObjectContours(layer, new[] { worldPoints.ToArray() }, stroke, color, strokeColor, atoms);
    }

    public int AddPathObjectContours(int layer, IReadOnlyList<PointF[]> worldContours, float stroke, Color color, Color strokeColor, uint atoms)
    {
        var index = AppendPathObjectContours(layer, worldContours, stroke, color, strokeColor, atoms);
        if (index < 0) return -1;
        RebuildGeometryIndex();
        RebuildSummaries();
        return index;
    }

    public bool CanConvertLineToFill(int objectIndex, int frame)
    {
        return (uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line
            && IsObjectActive(objectIndex, frame)
            && HasStroke(objectIndex);
    }

    public bool TryConvertLineToFill(int objectIndex, int frame, out int fillIndex)
    {
        fillIndex = -1;
        if (!CanConvertLineToFill(objectIndex, frame)) return false;

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            var contours = StrokeOutlineContours(objectIndex);
            if (contours.Length == 0) return false;

            var layer = ObjectLayer[objectIndex];
            var keyframeFrame = ObjectKeyframeFrame[objectIndex];
            var order = ObjectOrder[objectIndex];
            var subOrder = ObjectSubOrder[objectIndex];
            var atoms = AtomCount[objectIndex];
            var fillColor = Color.FromArgb(StrokeArgb[objectIndex]);

            EditFrame = Math.Max(0, frame);
            var appendedFill = AppendPathObjectContours(layer, contours, 0, fillColor, Color.Transparent, atoms);
            if (appendedFill < 0) throw new InvalidOperationException("Line-to-fill conversion produced invalid outline geometry.");

            ObjectKeyframeFrame[appendedFill] = keyframeFrame;
            ObjectOrder[appendedFill] = order;
            ObjectSubOrder[appendedFill] = subOrder;
            if (!RemoveObjectAt(objectIndex)) throw new InvalidOperationException("Line-to-fill conversion could not remove its source line.");

            // The appended fill is compacted into the removed source slot.
            fillIndex = objectIndex;
            return true;
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return false;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    internal int AppendPathObjectContours(int layer, IReadOnlyList<PointF[]> worldContours, float stroke, Color color, Color strokeColor, uint atoms)
    {
        var contours = NormalizePathContours(worldContours);
        if (contours.Length == 0) return -1;

        var first = contours[0][0];
        var left = first.X;
        var right = first.X;
        var top = first.Y;
        var bottom = first.Y;
        foreach (var contour in contours)
        {
            foreach (var point in contour)
            {
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        var center = VectorUnits.Quantize(new PointF((left + right) * 0.5f, (top + bottom) * 0.5f));
        var index = AppendObject(layer, center, new SizeF(Math.Max(1, right - left), Math.Max(1, bottom - top)), 0, stroke, color, strokeColor, atoms, VectorAnimationEngine.ShapeKind.Path);
        var localContours = new PointF[contours.Length][];
        for (var c = 0; c < contours.Length; c++)
        {
            var contour = contours[c];
            var local = new PointF[contour.Length];
            for (var i = 0; i < contour.Length; i++)
            {
                local[i] = new PointF(VectorUnits.Quantize(contour[i].X - center.X), VectorUnits.Quantize(contour[i].Y - center.Y));
            }

            localContours[c] = local;
        }

        _pathLocalContours[index] = localContours;
        _pathBezierLocalContours.Remove(index);
        return index;
    }

    internal int AppendPathBezierObjectContours(
        int layer,
        IReadOnlyList<PathBezierNode[]> worldContours,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms)
    {
        if (!TryPreparePathBezierContours(worldContours, out var exactContours, out var sampledContours, out var bounds))
        {
            return -1;
        }

        var center = VectorUnits.Quantize(new PointF(
            bounds.Left + bounds.Width * 0.5f,
            bounds.Top + bounds.Height * 0.5f));
        var index = AppendObject(
            layer,
            center,
            new SizeF(
                Math.Max(1, VectorUnits.Quantize(bounds.Width)),
                Math.Max(1, VectorUnits.Quantize(bounds.Height))),
            0,
            stroke,
            color,
            strokeColor,
            atoms,
            VectorAnimationEngine.ShapeKind.Path);
        StorePreparedPathBezierContours(index, exactContours, sampledContours, bounds, center);
        return index;
    }

    public int AddFreehandStroke(int layer, IReadOnlyList<PointF> worldPoints, float stroke, Color color, bool brushStroke, uint atoms)
    {
        var points = NormalizeFreehandPoints(worldPoints);
        if (points.Length == 0) return -1;

        if (brushStroke)
        {
            var outlines = FreehandStrokeProcessor.CreateBrushOutlines(points, stroke);
            var outlineAtoms = outlines.Sum(contour => contour.Length);
            return outlines.Length > 0
                ? AddPathObjectContours(layer, outlines, 0, color, Color.Transparent, Math.Max(atoms, (uint)Math.Max(3, outlineAtoms)))
                : -1;
        }

        var index = AppendFreehandStroke(layer, points, stroke, color, atoms);
        if (index < 0) return -1;
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, color);
        return index;
    }

    public int AddFreehandBezierStroke(
        int layer,
        IReadOnlyList<PathBezierNode> worldNodes,
        float stroke,
        Color color,
        uint atoms)
    {
        var index = AppendFreehandBezierStroke(layer, worldNodes, stroke, color, atoms);
        if (index < 0) return -1;
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, color);
        return index;
    }

    public int[] AddSoftBrushStroke(
        int layer,
        IReadOnlyList<PointF> worldPoints,
        float diameter,
        Color color,
        BrushShape brushShape,
        uint atoms,
        int frequency = 8,
        bool continuous = true)
    {
        var points = NormalizeFreehandPoints(worldPoints);
        if (points.Length == 0) return Array.Empty<int>();

        var added = new List<int>();
        foreach (var layerDefinition in brushShape.Layers)
        {
            var normalizedContour = brushShape.NormalizedContour(layerDefinition.Threshold);
            var contours = continuous && brushShape.IsRadiallySymmetric
                ? FreehandStrokeProcessor.CreateBrushOutlines(
                    points,
                    diameter * ContourRadiusScale(normalizedContour))
                : CreateBrushSweepContours(
                    points,
                    diameter,
                    normalizedContour,
                    frequency,
                    continuous,
                    brushShape.StampSpacingScale);
            if (contours.Length == 0) continue;
            var alpha = (int)Math.Clamp(Math.Round(color.A * layerDefinition.Opacity), 1, 255);
            var layerColor = Color.FromArgb(alpha, color.R, color.G, color.B);
            var layerAtoms = Math.Max(atoms, (uint)Math.Max(3, contours.Sum(contour => contour.Length)));
            var index = AppendPathObjectContours(layer, contours, 0, layerColor, Color.Transparent, layerAtoms);
            if (index >= 0) added.Add(index);
        }

        if (added.Count > 0)
        {
            CompleteIncrementalObjectAppends(added);
        }
        return added.ToArray();
    }

    public int AddMixingBrushStroke(
        int layer,
        IReadOnlyList<MixingBrushTrajectorySample> worldSamples,
        uint atoms)
    {
        var index = AppendMixingBrushStroke(layer, worldSamples, atoms);
        if (index < 0) return -1;
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, Color.FromArgb(Argb[index]));
        return index;
    }

    public int AddMixingBrushRegion(
        int layer,
        MixingBrushRegionData worldRegion,
        uint atoms)
    {
        var index = AppendMixingBrushRegion(layer, worldRegion, atoms);
        if (index < 0) return -1;
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, Color.FromArgb(Argb[index]));
        return index;
    }

    internal MixingBrushRegionMergeResult AddOrMergeMixingBrushRegion(
        int layer,
        int frame,
        MixingBrushRegionData worldRegion,
        uint atoms)
    {
        if (worldRegion is null
            || !MixingBrushRegionData.TryNormalize(
                worldRegion.Vertices,
                worldRegion.TriangleIndices,
                out var normalizedNewRegion))
        {
            return MixingBrushRegionMergeResult.Failed;
        }
        var canMergeNewRegion = MixingBrushRegionMerger.TryPrepareNormalized(
            normalizedNewRegion,
            out var newRegion);

        var targetLayer = ResolveObjectLayer(layer);
        var previousEditFrame = EditFrame;
        try
        {
            EditFrame = Math.Max(0, frame);
            var targetKeyframe = EnsureWritableKeyframe(targetLayer, EditFrame);
            if (!canMergeNewRegion)
            {
                var independent = AppendNormalizedMixingBrushRegion(targetLayer, normalizedNewRegion, atoms);
                if (independent < 0) return MixingBrushRegionMergeResult.Failed;
                ObjectKeyframeFrame[independent] = targetKeyframe;
                AppendObjectToSpatialIndex(independent);
                AddObjectToSummariesIncremental(independent, Color.FromArgb(Argb[independent]));
                return new MixingBrushRegionMergeResult(independent, 0);
            }

            var candidateObjects = new List<int>();
            var preparedCandidates = new List<PreparedMixingBrushGridRegion>();
            var orderedObjects = Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == targetLayer
                    && ObjectKeyframeFrame[index] == targetKeyframe)
                .OrderByDescending(index => ObjectOrder[index])
                .ThenByDescending(index => ObjectSubOrder[index])
                .ThenByDescending(index => index);
            foreach (var candidate in orderedObjects)
            {
                if (ShapeKind[candidate] != VectorAnimationEngine.ShapeKind.MixingStroke
                    || !TryGetMixingBrushWorldRegion(candidate, out var candidateRegion)
                    || !MixingBrushRegionMerger.TryPrepareNormalized(candidateRegion, out var preparedCandidate))
                {
                    break;
                }

                candidateObjects.Add(candidate);
                preparedCandidates.Add(preparedCandidate);
            }

            if (candidateObjects.Count == 0)
            {
                var independent = AppendNormalizedMixingBrushRegion(targetLayer, newRegion.Region, atoms);
                if (independent < 0) return MixingBrushRegionMergeResult.Failed;
                ObjectKeyframeFrame[independent] = targetKeyframe;
                AppendObjectToSpatialIndex(independent);
                AddObjectToSummariesIncremental(independent, Color.FromArgb(Argb[independent]));
                return new MixingBrushRegionMergeResult(independent, 0);
            }

            preparedCandidates.Reverse();
            preparedCandidates.Add(newRegion);
            if (!MixingBrushRegionMerger.TryMerge(preparedCandidates, out var mergedRegion))
            {
                var independent = AppendNormalizedMixingBrushRegion(targetLayer, newRegion.Region, atoms);
                if (independent < 0) return MixingBrushRegionMergeResult.Failed;
                ObjectKeyframeFrame[independent] = targetKeyframe;
                AppendObjectToSpatialIndex(independent);
                AddObjectToSummariesIncremental(independent, Color.FromArgb(Argb[independent]));
                return new MixingBrushRegionMergeResult(independent, 0);
            }

            var mergedComplexity = (ulong)mergedRegion.Vertices.Length
                + (ulong)(mergedRegion.TriangleIndices.Length / 3);
            var mergedAtoms = (uint)Math.Clamp(mergedComplexity, 3ul, uint.MaxValue);
            var appended = AppendNormalizedMixingBrushRegion(targetLayer, mergedRegion, mergedAtoms);
            if (appended < 0) return MixingBrushRegionMergeResult.Failed;
            ObjectKeyframeFrame[appended] = targetKeyframe;
            var mergedOrder = ObjectOrder[appended];

            var removed = RemoveObjects(candidateObjects);
            var resultIndex = appended - removed;
            if ((uint)resultIndex >= ObjectCount || ObjectOrder[resultIndex] != mergedOrder)
            {
                resultIndex = Enumerable.Range(0, ObjectCount)
                    .FirstOrDefault(index => ObjectOrder[index] == mergedOrder, -1);
            }
            return resultIndex >= 0
                ? new MixingBrushRegionMergeResult(resultIndex, removed)
                : MixingBrushRegionMergeResult.Failed;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    public int[] AddPressureBrushStroke(
        int layer,
        IReadOnlyList<PressureBrushSample> samples,
        float baseDiameter,
        Color color,
        BrushShape brushShape,
        uint atoms,
        int smoothing,
        float simplifyTolerance,
        int frequency = 8,
        bool continuous = true)
    {
        var profile = continuous && brushShape.IsRadiallySymmetric
            ? FreehandStrokeProcessor.CreatePressurePreview(samples, baseDiameter, smoothing)
            : FreehandStrokeProcessor.ProcessPressure(samples, baseDiameter, smoothing, simplifyTolerance);
        return AddPressureBrushProfile(layer, profile, color, brushShape, atoms, frequency, continuous);
    }

    internal int[] AddPressureBrushProfile(
        int layer,
        IReadOnlyList<PressureBrushPoint> profile,
        Color color,
        BrushShape brushShape,
        uint atoms,
        int frequency = 8,
        bool continuous = true)
    {
        if (profile.Count == 0) return Array.Empty<int>();

        var added = new List<int>();
        foreach (var layerDefinition in brushShape.Layers)
        {
            var normalizedContour = brushShape.NormalizedContour(layerDefinition.Threshold);
            var contours = continuous && brushShape.IsRadiallySymmetric
                ? FreehandStrokeProcessor.CreateVariableWidthBrushOutlines(
                    profile,
                    ContourRadiusScale(normalizedContour))
                : CreatePressureBrushSweepContours(
                    profile,
                    normalizedContour,
                    frequency,
                    continuous,
                    brushShape.StampSpacingScale);
            if (contours.Length == 0) continue;

            var alpha = (int)Math.Clamp(Math.Round(color.A * layerDefinition.Opacity), 1, 255);
            var layerColor = Color.FromArgb(alpha, color.R, color.G, color.B);
            var layerAtoms = Math.Max(atoms, (uint)Math.Max(3, contours.Sum(contour => contour.Length)));
            var index = AppendPathObjectContours(layer, contours, 0, layerColor, Color.Transparent, layerAtoms);
            if (index >= 0) added.Add(index);
        }

        if (added.Count > 0)
        {
            CompleteIncrementalObjectAppends(added);
        }
        return added.ToArray();
    }

    private void CompleteIncrementalObjectAppends(IReadOnlyList<int> objectIndices)
    {
        if (objectIndices.Count == 0) return;
        var firstIndex = objectIndices[0];
        var contiguousTail = firstIndex >= 0 && firstIndex + objectIndices.Count == ObjectCount;
        for (var index = 1; contiguousTail && index < objectIndices.Count; index++)
        {
            contiguousTail = objectIndices[index] == firstIndex + index;
        }
        if (!contiguousTail)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
            return;
        }

        CompleteIncrementalObjectAppends(firstIndex, objectIndices.Count);
    }

    private void CompleteIncrementalObjectAppends(int firstIndex, int count)
    {
        if (count <= 0) return;
        if (!TryAppendObjectRangeToSpatialIndex(firstIndex, count))
        {
            RebuildGeometryIndex();
            RebuildSummaries();
            return;
        }

        var geometryRevision = GeometryRevision;
        var summaryRevision = SummaryRevision;
        for (var objectIndex = firstIndex; objectIndex < firstIndex + count; objectIndex++)
        {
            var summaryArgb = ShapeKind[objectIndex] is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform
                ? StrokeArgb[objectIndex]
                : Argb[objectIndex];
            AddObjectToSummariesIncremental(objectIndex, Color.FromArgb(summaryArgb));
        }

        GeometryRevision = geometryRevision + 1;
        SummaryRevision = summaryRevision + 1;
    }

    private static float ContourRadiusScale(IReadOnlyList<PointF> contour)
    {
        if (contour.Count == 0) return 1;
        var total = 0f;
        foreach (var point in contour) total += MathF.Sqrt(point.X * point.X + point.Y * point.Y);
        return Math.Max(0.04f, total / contour.Count);
    }

    public bool EraseWithBrushStroke(
        int frame,
        IReadOnlyList<PointF> worldPoints,
        float diameter,
        BrushShape brushShape,
        bool eraseLines,
        bool eraseFills,
        int frequency = 8,
        bool continuous = true,
        bool materializeAutoKeyframes = false)
    {
        if ((!eraseLines && !eraseFills) || worldPoints.Count == 0) return false;

        var centerline = NormalizeFreehandPoints(worldPoints);
        if (centerline.Length == 0) return false;
        var contour = brushShape.NormalizedContour(0.10f);
        var sweepContours = CreateBrushSweepContours(
            centerline,
            diameter,
            contour,
            frequency,
            continuous,
            brushShape.StampSpacingScale,
            quantizeStampPoints: false);
        var cutter = ToClipperPaths(sweepContours, minimumAreaUnitsSquared: 0.001d);
        if (cutter.Count == 0) return false;

        var plans = new List<BrushEraserPlan>();
        for (var index = 0; index < ObjectCount; index++)
        {
            if (!IsObjectActive(index, frame)) continue;
            if (TryBuildBrushEraserPlan(
                    index,
                    cutter,
                    sweepContours,
                    eraseLines,
                    eraseFills,
                    out var plan))
            {
                plans.Add(plan);
            }
        }

        if (plans.Count == 0) return false;

        if (materializeAutoKeyframes)
        {
            var planLayers = plans
                .Select(plan => (int)ObjectLayer[plan.Source])
                .Distinct()
                .ToArray();
            var materializedLayers = new HashSet<int>();
            using (Timeline.BeginBatchUpdate())
            {
                foreach (var layer in planLayers)
                {
                    if (MaterializeAutoKeyframeInPlace(layer, frame)) materializedLayers.Add(layer);
                }
            }

            if (materializedLayers.Count > 0)
            {
                for (var index = 0; index < plans.Count; index++)
                {
                    var plan = plans[index];
                    if (materializedLayers.Contains(ObjectLayer[plan.Source]))
                    {
                        plans[index] = plan with { KeyframeFrame = frame };
                    }
                }
            }
        }

        RemoveObjects(plans.Select(plan => plan.Source));
        foreach (var plan in plans)
        {
            foreach (var replacement in plan.Replacements)
            {
                var index = AppendMaterializedPart(replacement);
                if (index < 0) continue;
                ObjectKeyframeFrame[index] = plan.KeyframeFrame;
            }

            foreach (var replacement in plan.MixingStrokeReplacements)
            {
                var index = replacement.Region is { } region
                    ? AppendMixingBrushRegion(
                        replacement.Layer,
                        region,
                        replacement.Atoms)
                    : AppendMixingBrushStroke(
                        replacement.Layer,
                        replacement.Samples,
                        replacement.Atoms);
                if (index < 0) continue;
                ObjectOrder[index] = replacement.Order;
                ObjectSubOrder[index] = replacement.SubOrder;
                ObjectKeyframeFrame[index] = plan.KeyframeFrame;
            }
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    public bool IsBrushEraserStrokePath(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && Stroke[objectIndex] <= 0
            && Argb[objectIndex] == StrokeArgb[objectIndex]
            && Color.FromArgb(Argb[objectIndex]).A > 0;
    }

    private bool TryBuildBrushEraserPlan(
        int source,
        Paths64 cutter,
        PointF[][] cutterContours,
        bool eraseLines,
        bool eraseFills,
        out BrushEraserPlan plan)
    {
        plan = null!;
        if (ShapeKind[source] == VectorAnimationEngine.ShapeKind.MixingStroke)
        {
            return eraseFills
                && TryBuildMixingStrokeEraserPlan(source, cutterContours, out plan);
        }

        var eraserStrokePath = IsBrushEraserStrokePath(source);
        var hasFill = HasFill(source) && !eraserStrokePath;
        var hasStroke = HasStroke(source) || eraserStrokePath;
        if (!hasFill && !hasStroke) return false;

        var changedFill = false;
        var changedStroke = false;
        var fillRegions = new List<FillRegion>();
        var strokeRegions = new List<FillRegion>();
        if (eraseFills && hasFill)
        {
            changedFill = TryDifferenceFillRegions(FillWorldContours(source), cutter, out fillRegions);
        }

        if (eraseLines && hasStroke)
        {
            changedStroke = TryDifferenceFillRegions(
                eraserStrokePath ? FillWorldContours(source) : StrokeOutlineContours(source),
                cutter,
                out strokeRegions);
        }

        if (!changedFill && !changedStroke) return false;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var gradientPaint = CaptureGradientPaint(source);
        var shape = ShapeKind[source];
        var editableStrokeReplacement = changedStroke
            && !eraserStrokePath
            && (shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform
                || IsFillShape(shape));
        var additions = new List<MaterializedPartAddition>();

        if (hasFill)
        {
            var unchangedBezierContours = !changedFill
                && TryGetEditableFillBezierContours(source, out var editableBezierContours)
                    ? editableBezierContours
                    : Array.Empty<PathBezierNode[]>();
            var regions = changedFill
                ? fillRegions
                : ExecuteFillRegions(ToClipperPaths(FillWorldContours(source)), new Paths64());
            foreach (var region in regions)
            {
                var replacementGradientPaint = gradientPaint;
                if (changedFill && gradientPaint is { Kind: GradientKind.ShapeRadial } shapeGradient)
                {
                    // Shape-gradient positions are derived from the owning fill boundary.
                    // An erased fragment therefore needs its own center and mapping contours.
                    var center = ShapeGradientCenter(region.Contours);
                    replacementGradientPaint = shapeGradient with
                    {
                        Start = center,
                        End = ShapeGradientEnd(region.Contours, center),
                        ShapeMappingContours = region.Contours
                    };
                }

                additions.Add(FillMaterialization(
                    DrawingElementKey.None,
                    layer,
                    order,
                    0,
                    fillColor,
                    Color.Transparent,
                    0,
                    region.Contours,
                    replacementGradientPaint,
                    fillAutoMergeProtected: FillAutoMergeProtected[source],
                    bezierContours: regions.Count == 1
                        ? unchangedBezierContours
                        : Array.Empty<PathBezierNode[]>()));
            }
        }

        if (hasStroke)
        {
            if (changedStroke)
            {
                if (editableStrokeReplacement)
                {
                    if (!TryAddErasedEditableStrokeMaterializations(source, cutter, additions)) return false;
                }
                else
                {
                    foreach (var region in strokeRegions)
                    {
                        // Matching fill and stroke colors marks a path that originated as a line.
                        additions.Add(FillMaterialization(
                            DrawingElementKey.None,
                            layer,
                            order,
                            0,
                            strokeColor,
                            strokeColor,
                            0,
                            region.Contours));
                    }
                }
            }
            else if (!eraserStrokePath)
            {
                AddUnchangedStrokeMaterializations(source, additions);
            }
        }

        if (additions.Count == 0)
        {
            plan = new BrushEraserPlan(source, ObjectKeyframeFrame[source], additions);
            return true;
        }

        var subOrders = new double[additions.Count];
        if (editableStrokeReplacement)
        {
            subOrders[0] = ObjectSubOrder[source];
            if (additions.Count > 1)
            {
                ReplacementSubOrders(source, additions.Count - 1, preserveSourceSubOrder: true)
                    .CopyTo(subOrders, 1);
            }
        }
        else
        {
            subOrders = additions.Count == 1
                ? new[] { ObjectSubOrder[source] }
                : ReplacementSubOrders(source, additions.Count);
        }

        var sourceAtoms = AtomCount[source];
        var atoms = Math.Max(3u, sourceAtoms / (uint)Math.Max(1, additions.Count));
        var additionCount = (uint)additions.Count;
        var atomRemainder = editableStrokeReplacement && sourceAtoms >= 3u * additionCount
            ? sourceAtoms % (uint)additions.Count
            : 0;
        for (var index = 0; index < additions.Count; index++)
        {
            additions[index] = additions[index] with
            {
                Atoms = atoms + ((uint)index < atomRemainder ? 1u : 0u),
                SubOrder = subOrders[index]
            };
        }

        plan = new BrushEraserPlan(source, ObjectKeyframeFrame[source], additions);
        return true;
    }

    private bool TryBuildMixingStrokeEraserPlan(
        int source,
        PointF[][] cutterContours,
        out BrushEraserPlan plan)
    {
        plan = null!;
        if (TryGetMixingBrushWorldRegion(source, out var region))
        {
            return TryBuildMixingBrushRegionEraserPlan(source, region, cutterContours, out plan);
        }
        if (cutterContours.Length == 0
            || !TryGetMixingStrokeWorldSamples(source, out var samples)
            || samples.Length == 0)
        {
            return false;
        }

        var cutterBounds = ContourBounds(cutterContours);
        var retainedRuns = new List<MixingBrushTrajectorySample[]>();
        var currentRun = new List<MixingBrushTrajectorySample>(samples.Length);
        var changed = false;

        void CompleteRun()
        {
            if (currentRun.Count == 0) return;
            retainedRuns.Add(currentRun.ToArray());
            currentRun.Clear();
        }

        foreach (var sample in samples)
        {
            var radius = sample.Diameter * 0.5f;
            if (sample.Point.X + radius < cutterBounds.Left
                || sample.Point.X - radius > cutterBounds.Right
                || sample.Point.Y + radius < cutterBounds.Top
                || sample.Point.Y - radius > cutterBounds.Bottom)
            {
                currentRun.Add(sample);
                continue;
            }

            if (PointInCompoundPolygonOrOnBoundary(sample.Point, cutterContours))
            {
                changed = true;
                CompleteRun();
                continue;
            }

            var distance = DistanceToCompoundPolygonBoundary(sample.Point, cutterContours);
            if (distance >= radius - DrawingTopologyRules.UnitIntersectionTolerance)
            {
                currentRun.Add(sample);
                continue;
            }

            changed = true;
            var retainedDiameter = MathF.Floor(Math.Max(0, distance * 2f));
            if (retainedDiameter < VectorUnits.MinimumStrokeUnits)
            {
                CompleteRun();
                continue;
            }

            currentRun.Add(sample with { Diameter = retainedDiameter });
        }

        CompleteRun();
        if (!changed) return false;

        var replacements = new MixingStrokeEraserReplacement[retainedRuns.Count];
        if (retainedRuns.Count > 0)
        {
            var subOrders = retainedRuns.Count == 1
                ? [ObjectSubOrder[source]]
                : ReplacementSubOrders(source, retainedRuns.Count);
            var sourceAtoms = AtomCount[source];
            var atoms = Math.Max(3u, sourceAtoms / (uint)retainedRuns.Count);
            var atomRemainder = sourceAtoms >= 3u * (uint)retainedRuns.Count
                ? sourceAtoms % (uint)retainedRuns.Count
                : 0;
            for (var index = 0; index < retainedRuns.Count; index++)
            {
                replacements[index] = new MixingStrokeEraserReplacement(
                    ObjectLayer[source],
                    ObjectOrder[source],
                    subOrders[index],
                    atoms + ((uint)index < atomRemainder ? 1u : 0u),
                    retainedRuns[index]);
            }
        }

        plan = new BrushEraserPlan(
            source,
            ObjectKeyframeFrame[source],
            Array.Empty<MaterializedPartAddition>())
        {
            MixingStrokeReplacements = replacements
        };
        return true;
    }

    private bool TryBuildMixingBrushRegionEraserPlan(
        int source,
        MixingBrushRegionData region,
        PointF[][] cutterContours,
        out BrushEraserPlan plan)
    {
        plan = null!;
        if (cutterContours.Length == 0) return false;

        var cutterBounds = ContourBounds(cutterContours);
        var triangleCount = region.TriangleIndices.Length / 3;
        var retainedTriangles = new bool[triangleCount];
        var changed = false;
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var offset = triangle * 3;
            var first = region.Vertices[region.TriangleIndices[offset]];
            var second = region.Vertices[region.TriangleIndices[offset + 1]];
            var third = region.Vertices[region.TriangleIndices[offset + 2]];
            var intersects = TriangleIntersectsMixingCutter(
                first,
                second,
                third,
                cutterContours,
                cutterBounds);
            retainedTriangles[triangle] = !intersects;
            changed |= intersects;
        }
        if (!changed) return false;

        var components = region.CreateConnectedComponents(retainedTriangles);
        var replacements = new MixingStrokeEraserReplacement[components.Length];
        if (components.Length > 0)
        {
            var subOrders = components.Length == 1
                ? [ObjectSubOrder[source]]
                : ReplacementSubOrders(source, components.Length);
            var sourceAtoms = AtomCount[source];
            var atoms = Math.Max(3u, sourceAtoms / (uint)components.Length);
            var atomRemainder = sourceAtoms >= 3u * (uint)components.Length
                ? sourceAtoms % (uint)components.Length
                : 0;
            for (var index = 0; index < components.Length; index++)
            {
                replacements[index] = new MixingStrokeEraserReplacement(
                    ObjectLayer[source],
                    ObjectOrder[source],
                    subOrders[index],
                    atoms + ((uint)index < atomRemainder ? 1u : 0u),
                    [])
                {
                    Region = components[index]
                };
            }
        }

        plan = new BrushEraserPlan(
            source,
            ObjectKeyframeFrame[source],
            Array.Empty<MaterializedPartAddition>())
        {
            MixingStrokeReplacements = replacements
        };
        return true;
    }

    private static bool TriangleIntersectsMixingCutter(
        MixingBrushRegionVertex first,
        MixingBrushRegionVertex second,
        MixingBrushRegionVertex third,
        PointF[][] cutterContours,
        RectangleF cutterBounds)
    {
        if (((uint)first.Argb >> 24) == 0
            && ((uint)second.Argb >> 24) == 0
            && ((uint)third.Argb >> 24) == 0)
        {
            return false;
        }

        var triangleLeft = Math.Min(first.Point.X, Math.Min(second.Point.X, third.Point.X));
        var triangleRight = Math.Max(first.Point.X, Math.Max(second.Point.X, third.Point.X));
        var triangleTop = Math.Min(first.Point.Y, Math.Min(second.Point.Y, third.Point.Y));
        var triangleBottom = Math.Max(first.Point.Y, Math.Max(second.Point.Y, third.Point.Y));
        if (triangleRight < cutterBounds.Left
            || triangleLeft > cutterBounds.Right
            || triangleBottom < cutterBounds.Top
            || triangleTop > cutterBounds.Bottom)
        {
            return false;
        }

        if (PointInCompoundPolygonOrOnBoundary(first.Point, cutterContours)
            || PointInCompoundPolygonOrOnBoundary(second.Point, cutterContours)
            || PointInCompoundPolygonOrOnBoundary(third.Point, cutterContours))
        {
            return true;
        }

        foreach (var contour in cutterContours)
        {
            if (contour.Length == 0) continue;
            if (PointInMixingTriangle(contour[0], first.Point, second.Point, third.Point)) return true;
            for (var index = 0; index < contour.Length; index++)
            {
                var cutterStart = contour[index];
                var cutterEnd = contour[(index + 1) % contour.Length];
                if (TrySegmentIntersection(first.Point, second.Point, cutterStart, cutterEnd, out _)
                    || TrySegmentIntersection(second.Point, third.Point, cutterStart, cutterEnd, out _)
                    || TrySegmentIntersection(third.Point, first.Point, cutterStart, cutterEnd, out _))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool PointInMixingTriangle(
        PointF point,
        PointF first,
        PointF second,
        PointF third)
    {
        var firstSide = Cross(first, second, point);
        var secondSide = Cross(second, third, point);
        var thirdSide = Cross(third, first, point);
        var tolerance = DrawingTopologyRules.UnitIntersectionTolerance;
        var hasNegative = firstSide < -tolerance || secondSide < -tolerance || thirdSide < -tolerance;
        var hasPositive = firstSide > tolerance || secondSide > tolerance || thirdSide > tolerance;
        return !(hasNegative && hasPositive);
    }

    private static double Cross(PointF start, PointF end, PointF point) =>
        ((double)end.X - start.X) * (point.Y - start.Y)
        - ((double)end.Y - start.Y) * (point.X - start.X);

    private bool TryAddErasedEditableStrokeMaterializations(
        int source,
        Paths64 cutter,
        List<MaterializedPartAddition> additions)
    {
        PointF[][] expandedContours;
        try
        {
            var expandedCutter = OffsetClosedPaths(cutter, Stroke[source] * 0.5d);
            if (expandedCutter.Count == 0) return false;
            expandedContours = FromClipperPaths(expandedCutter);
            if (expandedContours.Length == 0) return false;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }

        var shape = ShapeKind[source];
        if (IsFillShape(shape))
        {
            return TryAddErasedFillBoundaryStrokeMaterializations(source, expandedContours, additions);
        }

        var samples = StrokeSamples(source);
        if (samples.Length == 0) return false;
        if (samples.Length == 1)
        {
            if (shape == VectorAnimationEngine.ShapeKind.Freeform
                && !PointInCompoundPolygonOrOnBoundary(samples[0].Point, expandedContours)
                && TryGetFreehandWorldPoints(source, out var singlePointStroke))
            {
                additions.Add(FreehandMaterialization(
                    DrawingElementKey.None,
                    ObjectLayer[source],
                    ObjectOrder[source],
                    0,
                    Stroke[source],
                    Color.FromArgb(Argb[source]),
                    Color.FromArgb(StrokeArgb[source]),
                    0,
                    singlePointStroke));
            }

            return true;
        }

        var splits = new List<DrawingTopologySplit>
        {
            new(0, samples[0].Point),
            new(1, samples[^1].Point)
        };
        foreach (var contour in expandedContours)
        {
            if (contour.Length < 3) continue;
            var closedContour = new PointF[contour.Length + 1];
            contour.CopyTo(closedContour, 0);
            closedContour[^1] = contour[0];
            AddCurvePolylineIntersections(splits, samples, closedContour, includeSourceEndpoints: false);
        }

        var normalizedSplits = NormalizeStrokeSplits(splits);
        if (normalizedSplits.Count < 2) return false;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var gradientPaint = CaptureGradientPaint(source);
        var curve = shape == VectorAnimationEngine.ShapeKind.Line ? LineCurve(source) : default;
        var freehandPoints = Array.Empty<PointF>();
        if (shape == VectorAnimationEngine.ShapeKind.Freeform
            && !TryGetFreehandWorldPoints(source, out freehandPoints))
        {
            return false;
        }

        for (var index = 0; index < normalizedSplits.Count - 1; index++)
        {
            var startT = normalizedSplits[index].T;
            var endT = normalizedSplits[index + 1].T;
            if (endT - startT <= 0.0001f) continue;

            var middleT = (startT + endT) * 0.5f;
            var middle = shape == VectorAnimationEngine.ShapeKind.Line
                ? CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, middleT)
                : PolylinePointAt(freehandPoints, middleT);
            if (PointInCompoundPolygonOrOnBoundary(middle, expandedContours)) continue;

            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                var segment = CubicSubcurve(
                    curve.Start,
                    curve.Control1,
                    curve.Control2,
                    curve.End,
                    startT,
                    endT);
                var segmentCurve = new CubicBoundarySegment(
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End);
                if (PolylineLength(SampleCubicSegment(segmentCurve)) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
                additions.Add(CurveMaterialization(
                    DrawingElementKey.None,
                    layer,
                    order,
                    0,
                    stroke,
                    fillColor,
                    strokeColor,
                    0,
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End,
                    startT <= 0.0001f ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                    endT >= 0.9999f ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round)
                    with { GradientPaint = gradientPaint });
                continue;
            }

            var points = PolylineSlice(freehandPoints, startT, endT);
            if (PolylineLength(points) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            additions.Add(FreehandMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                0,
                stroke,
                fillColor,
                strokeColor,
                0,
                points));
        }

        return true;
    }

    private bool TryAddErasedFillBoundaryStrokeMaterializations(
        int source,
        PointF[][] expandedContours,
        List<MaterializedPartAddition> additions)
    {
        var parts = GetEditableFillBezierSegmentParts(source);
        if (parts.Length == 0) return false;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        foreach (var part in parts)
        {
            var curve = new CubicBoundarySegment(
                part.Start,
                part.Control1,
                part.Control2,
                part.End);
            var samples = SampleCubicSegmentWithParameters(curve);
            var splits = new List<DrawingTopologySplit>
            {
                new(0, curve.Start),
                new(1, curve.End)
            };
            foreach (var contour in expandedContours)
            {
                if (contour.Length < 3) continue;
                var closedContour = new PointF[contour.Length + 1];
                contour.CopyTo(closedContour, 0);
                closedContour[^1] = contour[0];
                AddCurvePolylineIntersections(splits, samples, closedContour, includeSourceEndpoints: false);
            }

            var normalizedSplits = NormalizeStrokeSplits(splits);
            if (normalizedSplits.Count < 2) return false;
            for (var splitIndex = 0; splitIndex < normalizedSplits.Count - 1; splitIndex++)
            {
                var startT = normalizedSplits[splitIndex].T;
                var endT = normalizedSplits[splitIndex + 1].T;
                if (endT - startT <= 0.0001f) continue;

                var middle = CubicPoint(
                    curve.Start,
                    curve.Control1,
                    curve.Control2,
                    curve.End,
                    (startT + endT) * 0.5f);
                if (PointInCompoundPolygonOrOnBoundary(middle, expandedContours)) continue;

                var segment = CubicSubcurve(
                    curve.Start,
                    curve.Control1,
                    curve.Control2,
                    curve.End,
                    startT,
                    endT);
                var segmentCurve = new CubicBoundarySegment(
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End);
                if (PolylineLength(SampleCubicSegment(segmentCurve)) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;

                additions.Add(CurveMaterialization(
                    DrawingElementKey.None,
                    layer,
                    order,
                    0,
                    stroke,
                    Color.Transparent,
                    strokeColor,
                    0,
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End,
                    startT <= 0.0001f ? LineEndpointStyle.Sharp : LineEndpointStyle.Round,
                    endT >= 0.9999f ? LineEndpointStyle.Sharp : LineEndpointStyle.Round));
            }
        }

        return true;
    }

    private void AddUnchangedStrokeMaterializations(int source, List<MaterializedPartAddition> additions)
    {
        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var shape = ShapeKind[source];
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(source);
            additions.Add(CurveMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                0,
                stroke,
                fillColor,
                strokeColor,
                0,
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End,
                GetLineEndpointStyle(source, startEndpoint: true),
                GetLineEndpointStyle(source, startEndpoint: false)));
            return;
        }

        if (IsFreehandShape(shape) && TryGetFreehandWorldPoints(source, out var points))
        {
            additions.Add(FreehandMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                0,
                stroke,
                fillColor,
                strokeColor,
                0,
                points));
            return;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            foreach (var part in BuildEllipseBoundaryStrokeParts(source, Array.Empty<int>()))
            {
                additions.Add(BoundaryMaterialization(
                    part,
                    DrawingElementKey.None,
                    layer,
                    order,
                    0,
                    stroke,
                    Color.Transparent,
                    strokeColor,
                    0));
            }

            return;
        }

        foreach (var contour in ShapeBoundaryContours(source))
        {
            if (contour.Length < 2) continue;
            additions.Add(PolylineMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                0,
                stroke,
                Color.Transparent,
                strokeColor,
                0,
                contour));
        }
    }

    private PointF[][] StrokeOutlineContours(int objectIndex)
    {
        var shape = ShapeKind[objectIndex];
        if (IsTopologyStrokeShape(shape))
        {
            return FreehandStrokeProcessor.CreateBrushOutlines(
                StrokeSamples(objectIndex).Select(sample => sample.Point).ToArray(),
                Stroke[objectIndex]);
        }

        var boundary = ToClipperPaths(ShapeBoundaryContours(objectIndex));
        if (boundary.Count == 0) return Array.Empty<PointF[]>();

        try
        {
            var radius = Math.Max(VectorUnits.MinimumStrokeUnits, Stroke[objectIndex]) * 0.5d;
            var outer = OffsetClosedPaths(boundary, radius);
            if (outer.Count == 0) return Array.Empty<PointF[]>();
            var inner = OffsetClosedPaths(boundary, -radius);
            if (inner.Count == 0) return FromClipperPaths(outer);

            var outlined = new Paths64();
            var clipper = new Clipper64 { PreserveCollinear = true };
            clipper.AddSubject(outer);
            clipper.AddClip(inner);
            return clipper.Execute(ClipType.Difference, FillRule.EvenOdd, outlined)
                ? FromClipperPaths(outlined)
                : Array.Empty<PointF[]>();
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    internal PointF[][] GetStrokeOutlineContours(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount && Stroke[objectIndex] > 0
            ? StrokeOutlineContours(objectIndex)
            : [];
    }

    private static Paths64 OffsetClosedPaths(Paths64 source, double delta)
    {
        var offset = new ClipperOffset(2d, 0.5d * ClipperCoordinateScale, preserveCollinear: true, reverseSolution: false);
        foreach (var path in source) offset.AddPath(path, JoinType.Round, EndType.Polygon);
        var result = new Paths64();
        offset.Execute(delta * ClipperCoordinateScale, result);
        return result;
    }

    private static bool TryDifferenceFillRegions(
        PointF[][] sourceContours,
        Paths64 cutter,
        out List<FillRegion> regions)
    {
        regions = new List<FillRegion>();
        var source = ToClipperPaths(sourceContours);
        if (source.Count == 0 || cutter.Count == 0) return false;

        try
        {
            var intersection = new Paths64();
            var intersectionClipper = new Clipper64 { PreserveCollinear = true };
            intersectionClipper.AddSubject(source);
            intersectionClipper.AddClip(cutter);
            if (!intersectionClipper.Execute(ClipType.Intersection, FillRule.EvenOdd, intersection) || intersection.Count == 0)
            {
                return false;
            }

            regions = ExecuteFillRegions(source, cutter);
            return true;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }
    }

    private static PointF[][] CreateBrushSweepContours(
        IReadOnlyList<PointF> centerline,
        float diameter,
        IReadOnlyList<PointF> normalizedContour,
        int frequency,
        bool continuous,
        float stampSpacingScale = 1f,
        bool quantizeStampPoints = true)
    {
        if (centerline.Count == 0 || normalizedContour.Count < 3) return Array.Empty<PointF[]>();
        try
        {
            var stamps = new Paths64();
            var radius = Math.Max(VectorUnits.MinimumStrokeUnits, diameter) * 0.5f;
            frequency = Math.Clamp(frequency, 1, 24);
            stampSpacingScale = Math.Clamp(stampSpacingScale, 0.1f, 1f);
            var spacing = Math.Max(
                DrawingTopologyRules.MinStrokeSegmentUnits,
                diameter / frequency * stampSpacingScale);
            var lastStamp = PointF.Empty;
            var hasLastStamp = false;

            void AddStamp(PointF center)
            {
                if (hasLastStamp && Distance(center, lastStamp) < 0.001f) return;

                var stamp = new PointF[normalizedContour.Count];
                if (quantizeStampPoints)
                {
                    for (var pointIndex = 0; pointIndex < stamp.Length; pointIndex++)
                    {
                        stamp[pointIndex] = VectorUnits.Quantize(new PointF(
                            center.X + normalizedContour[pointIndex].X * radius,
                            center.Y + normalizedContour[pointIndex].Y * radius));
                    }
                }
                else
                {
                    for (var pointIndex = 0; pointIndex < stamp.Length; pointIndex++)
                    {
                        stamp[pointIndex] = new PointF(
                            center.X + normalizedContour[pointIndex].X * radius,
                            center.Y + normalizedContour[pointIndex].Y * radius);
                    }
                }

                var path = ToClipperPath(stamp, closed: true);
                if (path.Count >= 3) stamps.Add(path);
                lastStamp = center;
                hasLastStamp = true;
            }

            AddStamp(centerline[0]);
            if (continuous)
            {
                var distanceSinceStamp = 0f;
                for (var index = 1; index < centerline.Count; index++)
                {
                    var start = centerline[index - 1];
                    var end = centerline[index];
                    var segmentLength = Distance(start, end);
                    if (segmentLength < 0.001f) continue;

                    var consumed = 0f;
                    while (distanceSinceStamp + segmentLength - consumed >= spacing)
                    {
                        var required = Math.Max(0.0001f, spacing - distanceSinceStamp);
                        consumed += required;
                        if (consumed > segmentLength + 0.0001f) break;
                        AddStamp(Lerp(start, end, Math.Clamp(consumed / segmentLength, 0, 1)));
                        distanceSinceStamp = 0;
                    }

                    distanceSinceStamp += Math.Max(0, segmentLength - consumed);
                }
            }
            else
            {
                for (var index = 1; index < centerline.Count; index++)
                {
                    var center = centerline[index];
                    if (index != centerline.Count - 1 && Distance(center, lastStamp) < spacing) continue;
                    AddStamp(center);
                }
            }

            AddStamp(centerline[^1]);

            return stamps.Count == 0
                ? Array.Empty<PointF[]>()
                : FromClipperPaths(
                    Clipper.Union(stamps, FillRule.NonZero),
                    normalizeContours: quantizeStampPoints);
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    private static PointF[][] CreatePressureBrushSweepContours(
        IReadOnlyList<PressureBrushPoint> profile,
        IReadOnlyList<PointF> normalizedContour,
        int frequency,
        bool continuous,
        float stampSpacingScale = 1f)
    {
        if (profile.Count == 0 || normalizedContour.Count < 3) return Array.Empty<PointF[]>();
        try
        {
            var stamps = new Paths64();
            frequency = Math.Clamp(frequency, 1, 24);
            stampSpacingScale = Math.Clamp(stampSpacingScale, 0.1f, 1f);
            var lastStamp = PointF.Empty;
            var hasLastStamp = false;

            void AddStamp(PressureBrushPoint sample)
            {
                if (hasLastStamp && Distance(sample.Point, lastStamp) < 0.001f) return;

                var radius = Math.Max(VectorUnits.MinimumStrokeUnits * 0.6f, sample.Diameter) * 0.5f;
                var stamp = new PointF[normalizedContour.Count];
                for (var pointIndex = 0; pointIndex < stamp.Length; pointIndex++)
                {
                    stamp[pointIndex] = VectorUnits.Quantize(new PointF(
                        sample.Point.X + normalizedContour[pointIndex].X * radius,
                        sample.Point.Y + normalizedContour[pointIndex].Y * radius));
                }

                var path = ToClipperPath(stamp, closed: true);
                if (path.Count >= 3) stamps.Add(path);
                lastStamp = sample.Point;
                hasLastStamp = true;
            }

            AddStamp(profile[0]);
            if (continuous)
            {
                for (var index = 1; index < profile.Count; index++)
                {
                    var start = profile[index - 1];
                    var end = profile[index];
                    var distance = Distance(start.Point, end.Point);
                    var spacing = Math.Max(
                        DrawingTopologyRules.MinStrokeSegmentUnits,
                        Math.Min(start.Diameter, end.Diameter) / frequency * stampSpacingScale);
                    var steps = Math.Max(1, (int)MathF.Ceiling(distance / spacing));
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = step / (float)steps;
                        AddStamp(new PressureBrushPoint(
                            Lerp(start.Point, end.Point, t),
                            start.Diameter + (end.Diameter - start.Diameter) * t));
                    }
                }
            }
            else
            {
                for (var index = 1; index < profile.Count; index++) AddStamp(profile[index]);
            }

            return stamps.Count == 0 ? Array.Empty<PointF[]>() : FromClipperPaths(Clipper.Union(stamps, FillRule.NonZero));
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    internal int AppendMixingBrushStroke(
        int layer,
        IReadOnlyList<MixingBrushTrajectorySample> worldSamples,
        uint atoms)
    {
        var samples = NormalizeMixingStrokeSamples(worldSamples);
        if (samples.Length == 0) return -1;

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
        var summaryColor = Color.FromArgb(samples[^1].Argb);
        var objectIndex = AppendObject(
            layer,
            center,
            new SizeF(
                Math.Max(VectorUnits.MinimumStrokeUnits, VectorUnits.Quantize(right - left)),
                Math.Max(VectorUnits.MinimumStrokeUnits, VectorUnits.Quantize(bottom - top))),
            0,
            0,
            summaryColor,
            Color.Transparent,
            Math.Max(atoms, (uint)Math.Max(3, samples.Length)),
            VectorAnimationEngine.ShapeKind.MixingStroke);
        _mixingStrokeLocalSamples[objectIndex] = samples
            .Select(sample => sample with
            {
                Point = new PointF(
                    VectorUnits.Quantize(sample.Point.X - center.X),
                    VectorUnits.Quantize(sample.Point.Y - center.Y))
            })
            .ToArray();
        _mixingStrokeLocalRegions.Remove(objectIndex);
        return objectIndex;
    }

    internal int AppendMixingBrushRegion(
        int layer,
        MixingBrushRegionData worldRegion,
        uint atoms)
    {
        if (worldRegion is null
            || !MixingBrushRegionData.TryNormalize(
                worldRegion.Vertices,
                worldRegion.TriangleIndices,
                out var normalized))
        {
            return -1;
        }

        return AppendNormalizedMixingBrushRegion(layer, normalized, atoms);
    }

    internal int AppendMixingBrushPreviewRegion(
        int layer,
        MixingBrushRegionData worldRegion,
        uint atoms)
    {
        if (worldRegion is null
            || worldRegion.Vertices.Length < 3
            || worldRegion.TriangleIndices.Length < 3
            || worldRegion.TriangleIndices.Length % 3 != 0)
        {
            return -1;
        }

        for (var index = 0; index < worldRegion.TriangleIndices.Length; index++)
        {
            if ((uint)worldRegion.TriangleIndices[index] >= worldRegion.Vertices.Length)
            {
                return -1;
            }
        }

        var previewIndex = AppendNormalizedMixingBrushRegion(layer, worldRegion, atoms);
        if (previewIndex < 0) return -1;
        AppendObjectToSpatialIndex(previewIndex);
        AddObjectToSummariesIncremental(previewIndex, Color.FromArgb(Argb[previewIndex]));
        return previewIndex;
    }

    private int AppendNormalizedMixingBrushRegion(
        int layer,
        MixingBrushRegionData normalized,
        uint atoms)
    {
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

        var objectIndex = AppendObject(
            layer,
            center,
            new SizeF(
                Math.Max(VectorUnits.MinimumStrokeUnits, VectorUnits.Quantize(bounds.Width)),
                Math.Max(VectorUnits.MinimumStrokeUnits, VectorUnits.Quantize(bounds.Height))),
            0,
            0,
            Color.FromArgb(MixingBrushRegionSummaryArgb(localRegion)),
            Color.Transparent,
            Math.Max(atoms, (uint)Math.Max(3, localRegion.Vertices.Length)),
            VectorAnimationEngine.ShapeKind.MixingStroke);
        _mixingStrokeLocalRegions[objectIndex] = localRegion;
        _mixingStrokeLocalSamples.Remove(objectIndex);
        return objectIndex;
    }

    public bool TryGetMixingBrushLocalRegion(
        int objectIndex,
        out MixingBrushRegionData region)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke
            || !_mixingStrokeLocalRegions.TryGetValue(objectIndex, out region!))
        {
            region = new MixingBrushRegionData([], []);
            return false;
        }

        return region.Vertices is { Length: >= 3 }
            && region.TriangleIndices is { Length: >= 3 };
    }

    public bool TryGetMixingBrushWorldRegion(
        int objectIndex,
        out MixingBrushRegionData region)
    {
        if (!TryGetMixingBrushLocalRegion(objectIndex, out var localRegion))
        {
            region = new MixingBrushRegionData([], []);
            return false;
        }

        var vertices = new MixingBrushRegionVertex[localRegion.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var vertex = localRegion.Vertices[index];
            vertices[index] = vertex with
            {
                Point = LocalToWorld(objectIndex, vertex.Point.X, vertex.Point.Y)
            };
        }
        region = new MixingBrushRegionData(vertices, localRegion.TriangleIndices.ToArray());
        return true;
    }

    public int GetMixingBrushRegionPartCount(int objectIndex)
    {
        return TryGetMixingBrushLocalRegion(objectIndex, out var region)
            ? region.ConnectedComponentCount
            : 0;
    }

    public bool TryGetMixingBrushLocalRegionPart(
        int objectIndex,
        int partIndex,
        out MixingBrushRegionData region)
    {
        if (!TryGetMixingBrushLocalRegion(objectIndex, out var localRegion)
            || !localRegion.TryGetConnectedComponent(partIndex, out region))
        {
            region = new MixingBrushRegionData([], []);
            return false;
        }
        return true;
    }

    public bool TryGetMixingBrushWorldRegionPart(
        int objectIndex,
        int partIndex,
        out MixingBrushRegionData region)
    {
        if (!TryGetMixingBrushLocalRegionPart(objectIndex, partIndex, out var localRegion))
        {
            region = new MixingBrushRegionData([], []);
            return false;
        }

        var vertices = new MixingBrushRegionVertex[localRegion.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var vertex = localRegion.Vertices[index];
            vertices[index] = vertex with
            {
                Point = LocalToWorld(objectIndex, vertex.Point.X, vertex.Point.Y)
            };
        }
        region = new MixingBrushRegionData(vertices, localRegion.TriangleIndices.ToArray());
        return true;
    }

    public bool TryGetMixingStrokeLocalSamples(
        int objectIndex,
        out MixingBrushTrajectorySample[] samples)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke
            || !_mixingStrokeLocalSamples.TryGetValue(objectIndex, out samples!))
        {
            samples = Array.Empty<MixingBrushTrajectorySample>();
            return false;
        }

        return samples.Length > 0;
    }

    public bool TryGetMixingStrokeWorldSamples(
        int objectIndex,
        out MixingBrushTrajectorySample[] samples)
    {
        if (!TryGetMixingStrokeLocalSamples(objectIndex, out var localSamples))
        {
            samples = Array.Empty<MixingBrushTrajectorySample>();
            return false;
        }

        samples = new MixingBrushTrajectorySample[localSamples.Length];
        for (var index = 0; index < localSamples.Length; index++)
        {
            var sample = localSamples[index];
            samples[index] = sample with
            {
                Point = LocalToWorld(objectIndex, sample.Point.X, sample.Point.Y)
            };
        }
        return true;
    }

    internal int AppendFreehandStroke(int layer, PointF[] points, float stroke, Color color, uint atoms)
    {
        points = NormalizeFreehandPoints(points);
        if (points.Length == 0) return -1;
        if (TryCreateOpenFreehandBezierNodes(points, out var nodes))
        {
            return AppendFreehandBezierStroke(layer, nodes, stroke, color, atoms);
        }

        var left = points[0].X;
        var right = points[0].X;
        var top = points[0].Y;
        var bottom = points[0].Y;
        for (var i = 1; i < points.Length; i++)
        {
            left = Math.Min(left, points[i].X);
            right = Math.Max(right, points[i].X);
            top = Math.Min(top, points[i].Y);
            bottom = Math.Max(bottom, points[i].Y);
        }

        stroke = Math.Max(VectorUnits.MinimumStrokeUnits, stroke);
        var center = new PointF((left + right) * 0.5f, (top + bottom) * 0.5f);
        var shape = VectorAnimationEngine.ShapeKind.Freeform;
        var transparent = Color.FromArgb(0, color);
        var index = AppendObject(
            layer,
            center,
            new SizeF(Math.Max(stroke, right - left + stroke), Math.Max(stroke, bottom - top + stroke)),
            0,
            stroke,
            transparent,
            color,
            Math.Max(3u, atoms),
            shape);

        var local = new PointF[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            local[i] = new PointF(
                VectorUnits.Quantize(points[i].X - center.X),
                VectorUnits.Quantize(points[i].Y - center.Y));
        }

        _freehandLocalPoints[index] = local;
        return index;
    }

    internal int AppendFreehandBezierStroke(
        int layer,
        IReadOnlyList<PathBezierNode> worldNodes,
        float stroke,
        Color color,
        uint atoms)
    {
        if (!TryPrepareFreehandBezierNodes(worldNodes, out var exactNodes, out var sampledPoints, out var bounds))
        {
            return -1;
        }

        stroke = Math.Max(VectorUnits.MinimumStrokeUnits, stroke);
        var center = VectorUnits.Quantize(new PointF(
            bounds.Left + bounds.Width * 0.5f,
            bounds.Top + bounds.Height * 0.5f));
        var transparent = Color.FromArgb(0, color);
        var index = AppendObject(
            layer,
            center,
            new SizeF(
                Math.Max(stroke, VectorUnits.Quantize(bounds.Width + stroke)),
                Math.Max(stroke, VectorUnits.Quantize(bounds.Height + stroke))),
            0,
            stroke,
            transparent,
            color,
            Math.Max(3u, atoms),
            VectorAnimationEngine.ShapeKind.Freeform);
        StorePreparedFreehandBezierNodes(index, exactNodes, sampledPoints, bounds, center);
        return index;
    }

    public bool TryGetFreehandLocalPoints(int objectIndex, out PointF[] points)
    {
        if ((uint)objectIndex >= ObjectCount || !_freehandLocalPoints.TryGetValue(objectIndex, out points!))
        {
            points = Array.Empty<PointF>();
            return false;
        }

        return points.Length > 0;
    }

    public bool TryGetFreehandWorldPoints(int objectIndex, out PointF[] points)
    {
        if (!TryGetFreehandLocalPoints(objectIndex, out var local))
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = new PointF[local.Length];
        for (var i = 0; i < local.Length; i++) points[i] = LocalToWorld(objectIndex, local[i].X, local[i].Y);
        return true;
    }

    internal bool TryGetFreehandBezierLocalNodes(int objectIndex, out PathBezierNode[] nodes)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Freeform)
        {
            nodes = Array.Empty<PathBezierNode>();
            return false;
        }

        if (_freehandBezierLocalNodes.TryGetValue(objectIndex, out nodes!)) return nodes.Length >= 2;
        if (!_freehandLocalPoints.TryGetValue(objectIndex, out var points))
        {
            nodes = Array.Empty<PathBezierNode>();
            return false;
        }

        if (_legacyFreehandBezierNodeCache.TryGetValue(objectIndex, out var cached)
            && ReferenceEquals(cached.SourcePoints, points))
        {
            nodes = cached.Nodes;
            return nodes.Length >= 2;
        }

        if (!TryCreateOpenFreehandBezierNodes(points, out nodes))
        {
            nodes = Array.Empty<PathBezierNode>();
            return false;
        }

        _legacyFreehandBezierNodeCache[objectIndex] = (points, nodes);

        return true;
    }

    public bool TryGetFreehandBezierWorldNodes(int objectIndex, out PathBezierNode[] nodes)
    {
        if (!TryGetFreehandBezierLocalNodes(objectIndex, out var localNodes))
        {
            nodes = Array.Empty<PathBezierNode>();
            return false;
        }

        nodes = new PathBezierNode[localNodes.Length];
        for (var nodeIndex = 0; nodeIndex < localNodes.Length; nodeIndex++)
        {
            var node = localNodes[nodeIndex];
            nodes[nodeIndex] = new PathBezierNode(
                LocalToWorld(objectIndex, node.Anchor.X, node.Anchor.Y),
                LocalToWorld(objectIndex, node.IncomingControl.X, node.IncomingControl.Y),
                LocalToWorld(objectIndex, node.OutgoingControl.X, node.OutgoingControl.Y));
        }

        return true;
    }

    public bool TryGetFreehandBezierSegment(
        int objectIndex,
        int segmentIndex,
        out PathBezierSegmentPart segment)
    {
        segment = default;
        if (segmentIndex < 0
            || !TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || segmentIndex >= nodes.Length - 1)
        {
            return false;
        }

        var current = nodes[segmentIndex];
        var next = nodes[segmentIndex + 1];
        var curve = new CubicBoundarySegment(
            current.Anchor,
            current.OutgoingControl,
            next.IncomingControl,
            next.Anchor);
        segment = new PathBezierSegmentPart(
            segmentIndex,
            0,
            segmentIndex,
            curve,
            SampleCubicSegment(curve));
        return true;
    }

    public bool TryFindClosestFreehandBezierSegment(
        int objectIndex,
        PointF world,
        float maximumDistance,
        out int segmentIndex,
        out PathBezierSegmentPart segment)
    {
        segmentIndex = -1;
        segment = default;
        if (!Finite(world)
            || !float.IsFinite(maximumDistance)
            || maximumDistance < 0
            || !TryGetFreehandBezierWorldNodes(objectIndex, out var nodes))
        {
            return false;
        }

        var bestDistance = maximumDistance;
        for (var candidateIndex = 0; candidateIndex < nodes.Length - 1; candidateIndex++)
        {
            var current = nodes[candidateIndex];
            var next = nodes[candidateIndex + 1];
            var curve = new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            var samples = SampleCubicSegment(curve);
            var distance = DistanceToPolyline(world, samples);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            segmentIndex = candidateIndex;
            segment = new PathBezierSegmentPart(candidateIndex, 0, candidateIndex, curve, samples);
        }

        return segmentIndex >= 0;
    }

    internal bool SetFreehandBezierSegmentForPreview(
        int objectIndex,
        int segmentIndex,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        bool rebuildGeometryIndex = false)
    {
        if (segmentIndex < 0
            || !TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || segmentIndex >= nodes.Length - 1)
        {
            return false;
        }

        start = VectorUnits.Quantize(start);
        control1 = VectorUnits.Quantize(control1);
        control2 = VectorUnits.Quantize(control2);
        end = VectorUnits.Quantize(end);
        var current = nodes[segmentIndex];
        var next = nodes[segmentIndex + 1];
        var startDelta = new PointF(start.X - current.Anchor.X, start.Y - current.Anchor.Y);
        var endDelta = new PointF(end.X - next.Anchor.X, end.Y - next.Anchor.Y);
        nodes[segmentIndex] = current with
        {
            Anchor = start,
            IncomingControl = VectorUnits.Quantize(new PointF(
                current.IncomingControl.X + startDelta.X,
                current.IncomingControl.Y + startDelta.Y)),
            OutgoingControl = control1
        };
        nodes[segmentIndex + 1] = next with
        {
            Anchor = end,
            IncomingControl = control2,
            OutgoingControl = VectorUnits.Quantize(new PointF(
                next.OutgoingControl.X + endDelta.X,
                next.OutgoingControl.Y + endDelta.Y))
        };
        if (!SetFreehandBezierNodesCore(objectIndex, nodes)) return false;
        CompleteFreehandBezierMutation(rebuildGeometryIndex);
        return true;
    }

    internal bool CompleteFreehandBezierPreview(int objectIndex, bool rebuildGeometryIndex = true)
    {
        if (!TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            || !SetFreehandBezierNodesCore(objectIndex, nodes))
        {
            return false;
        }

        CompleteFreehandBezierMutation(rebuildGeometryIndex);
        return true;
    }

    public void UpdateFreehandStrokeWidth(int objectIndex, float stroke, bool rebuildGeometryIndex = true)
    {
        if ((uint)objectIndex >= ObjectCount || !IsFreehandShape(ShapeKind[objectIndex]) || !TryGetFreehandLocalPoints(objectIndex, out var points)) return;
        stroke = Math.Max(VectorUnits.MinimumStrokeUnits, stroke);
        Stroke[objectIndex] = stroke;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Freeform
            && TryGetFreehandBezierWorldNodes(objectIndex, out var nodes)
            && SetFreehandBezierNodesCore(objectIndex, nodes))
        {
            if (rebuildGeometryIndex) RebuildGeometryIndex();
            return;
        }

        var left = points[0].X;
        var right = points[0].X;
        var top = points[0].Y;
        var bottom = points[0].Y;
        for (var i = 1; i < points.Length; i++)
        {
            left = Math.Min(left, points[i].X);
            right = Math.Max(right, points[i].X);
            top = Math.Min(top, points[i].Y);
            bottom = Math.Max(bottom, points[i].Y);
        }

        Width[objectIndex] = Math.Max(stroke, right - left + stroke);
        Height[objectIndex] = Math.Max(stroke, bottom - top + stroke);
        if (rebuildGeometryIndex) RebuildGeometryIndex();
    }

}
