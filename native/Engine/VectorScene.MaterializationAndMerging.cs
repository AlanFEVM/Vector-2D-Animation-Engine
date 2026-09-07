using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public MaterializeSelectedPartsResult MaterializeSelectedParts(IReadOnlyList<DrawingElementKey> selectedParts, int frame)
    {
        return MaterializeSelectedParts(selectedParts, frame, strokeIntersectionsOnly: false, rollbackSnapshot: null);
    }

    internal MaterializeSelectedPartsResult MaterializeSelectedParts(
        IReadOnlyList<DrawingElementKey> selectedParts,
        int frame,
        VectorSceneSnapshot rollbackSnapshot)
    {
        ArgumentNullException.ThrowIfNull(rollbackSnapshot);
        return MaterializeSelectedParts(selectedParts, frame, strokeIntersectionsOnly: false, rollbackSnapshot);
    }

    private MaterializeSelectedPartsResult MaterializeSelectedParts(
        IReadOnlyList<DrawingElementKey> selectedParts,
        int frame,
        bool strokeIntersectionsOnly,
        VectorSceneSnapshot? rollbackSnapshot = null)
    {
        if (selectedParts.Count == 0 || selectedParts.Any(key => !key.IsValid))
        {
            return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
        }

        var keys = selectedParts.Distinct().ToArray();
        var oldObjectCount = ObjectCount;
        var remove = new bool[oldObjectCount];
        var clearStroke = new HashSet<int>();
        var additions = new List<MaterializedPartAddition>();
        var mixingAdditions = new List<MixingMaterializedPartAddition>();
        var mappedResults = new Dictionary<DrawingElementKey, DrawingElementKey>();
        var keyframesBySourceStack = new Dictionary<(ushort Layer, long Order), int>();

        foreach (var group in keys.GroupBy(key => key.ObjectIndex).OrderBy(group => group.Key))
        {
            var source = group.Key;
            if ((uint)source >= oldObjectCount || !IsObjectActive(source, frame))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            var sourceKeys = group.ToArray();
            var shape = ShapeKind[source];
            var candidates = CollectTopologyCandidates(source, frame);
            if (strokeIntersectionsOnly)
            {
                candidates = candidates
                    .Where(candidate => (ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Line
                            || ShapeKind[candidate] == VectorAnimationEngine.ShapeKind.Freeform)
                        && HasStroke(candidate))
                    .ToList();
            }
            var layer = ObjectLayer[source];
            var order = ObjectOrder[source];
            keyframesBySourceStack[((ushort)layer, order)] = ObjectKeyframeFrame[source];
            var stroke = Stroke[source];
            var fillColor = Color.FromArgb(Argb[source]);
            var strokeColor = Color.FromArgb(StrokeArgb[source]);
            var gradientPaint = CaptureGradientPaint(source);
            var atoms = AtomCount[source];

            if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
            {
                if (sourceKeys.Any(key => key.Kind != DrawingElementKind.Fill))
                {
                    return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                }

                if (!TryGetMixingBrushLocalRegion(source, out var mixingRegion))
                {
                    if (!_mixingStrokeLocalSamples.ContainsKey(source)
                        || sourceKeys.Any(key => key.PartIndex != 0))
                    {
                        return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                    }
                    foreach (var key in sourceKeys) mappedResults[key] = key;
                    continue;
                }

                var componentCount = mixingRegion.ConnectedComponentCount;
                var selectedByPart = sourceKeys.ToDictionary(key => key.PartIndex);
                if (componentCount <= 0
                    || selectedByPart.Keys.Any(part => part < 0 || part >= componentCount))
                {
                    return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                }
                if (componentCount == 1)
                {
                    foreach (var key in sourceKeys) mappedResults[key] = key;
                    continue;
                }

                var components = new MixingBrushRegionData[componentCount];
                for (var partIndex = 0; partIndex < componentCount; partIndex++)
                {
                    if (!TryGetMixingBrushWorldRegionPart(source, partIndex, out components[partIndex]))
                    {
                        return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                    }
                }
                var componentAtoms = AllocateMixingComponentAtoms(atoms, components);
                var subOrders = ReplacementSubOrders(source, componentCount);
                remove[source] = true;
                for (var partIndex = 0; partIndex < componentCount; partIndex++)
                {
                    mixingAdditions.Add(new MixingMaterializedPartAddition(
                        selectedByPart.GetValueOrDefault(partIndex, DrawingElementKey.None),
                        layer,
                        ObjectKeyframeFrame[source],
                        order,
                        subOrders[partIndex],
                        componentAtoms[partIndex],
                        components[partIndex]));
                }
                continue;
            }

            if (IsTopologyStrokeShape(shape))
            {
                if (!HasStroke(source) || sourceKeys.Any(key => key.Kind != DrawingElementKind.Stroke))
                {
                    return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                }

                var topologySplits = strokeIntersectionsOnly
                    ? StrokeSplits(source, candidates)
                    : CachedStrokeSplits(source, frame, candidates);
                var splits = topologySplits.Select(split => split.T).ToList();
                var selectedByPart = sourceKeys.ToDictionary(key => key.PartIndex);
                if (selectedByPart.Keys.Any(part => part < 0 || part >= Math.Max(0, splits.Count - 1)))
                {
                    return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                }

                if (splits.Count <= 2)
                {
                    foreach (var key in sourceKeys) mappedResults[key] = key;
                    continue;
                }

                remove[source] = true;
                if (shape == VectorAnimationEngine.ShapeKind.Line)
                {
                    var curve = LineCurve(source);
                    var segments = BuildCurveParts(
                        curve.Start,
                        curve.Control1,
                        curve.Control2,
                        curve.End,
                        topologySplits);
                    if (selectedByPart.Keys.Any(part => segments.All(segment => segment.PartIndex != part)))
                    {
                        return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                    }

                    var atomsPerPart = Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count));
                    var subOrders = ReplacementSubOrders(source, segments.Count);
                    var subOrderIndex = 0;
                    foreach (var segment in segments)
                    {
                        additions.Add(CurveMaterialization(
                            selectedByPart.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                            layer,
                            order,
                            subOrders[subOrderIndex++],
                            stroke,
                            fillColor,
                            strokeColor,
                            atomsPerPart,
                            segment.Start,
                            segment.Control1,
                            segment.Control2,
                            segment.End,
                            segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                            segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round) with
                        {
                            GradientPaint = gradientPaint
                        });
                    }
                }
                else
                {
                    if (shape == VectorAnimationEngine.ShapeKind.Freeform
                        && _freehandBezierLocalNodes.ContainsKey(source)
                        && TryGetFreehandBezierWorldNodes(source, out var nodes)
                        && TryBuildOpenBezierRuns(nodes, topologySplits, out var runs))
                    {
                        if (selectedByPart.Keys.Any(part => runs.All(run => run.PartIndex != part)))
                        {
                            return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                        }

                        var atomsPerRun = Math.Max(3u, atoms / (uint)Math.Max(1, runs.Count));
                        var exactSubOrders = ReplacementSubOrders(source, runs.Count);
                        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
                        {
                            var run = runs[runIndex];
                            additions.Add(FreehandBezierMaterialization(
                                selectedByPart.GetValueOrDefault(run.PartIndex, DrawingElementKey.None),
                                layer,
                                order,
                                exactSubOrders[runIndex],
                                stroke,
                                fillColor,
                                strokeColor,
                                atomsPerRun,
                                run.Nodes));
                        }
                        continue;
                    }

                    if (!TryGetFreehandWorldPoints(source, out var points))
                    {
                        return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                    }

                    var segments = BuildPolylinePathParts(points, splits);
                    if (selectedByPart.Keys.Any(part => segments.All(segment => segment.PartIndex != part)))
                    {
                        return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                    }

                    var atomsPerPart = Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count));
                    var subOrders = ReplacementSubOrders(source, segments.Count);
                    var subOrderIndex = 0;
                    foreach (var segment in segments)
                    {
                        additions.Add(FreehandMaterialization(
                            selectedByPart.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                            layer,
                            order,
                            subOrders[subOrderIndex++],
                            stroke,
                            fillColor,
                            strokeColor,
                            atomsPerPart,
                            segment.Points));
                    }
                }

                continue;
            }

            if (!IsFillShape(shape)
                || sourceKeys.Any(key => key.Kind is not DrawingElementKind.Fill and not DrawingElementKind.BoundaryStroke))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            var fillKeys = sourceKeys.Where(key => key.Kind == DrawingElementKind.Fill).ToArray();
            var boundaryKeys = sourceKeys.Where(key => key.Kind == DrawingElementKind.BoundaryStroke).ToArray();
            if (fillKeys.Length > 0 && !HasFill(source) || boundaryKeys.Length > 0 && !HasStroke(source))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            var selectedFillParts = fillKeys.ToDictionary(key => key.PartIndex);
            var selectedBoundaryParts = boundaryKeys.ToDictionary(key => key.PartIndex);
            var fillPartition = fillKeys.Length > 0
                ? BuildFillPartition(source, frame, candidates)
                : new FillPartition([], []);
            var regions = fillPartition.Regions;
            if (selectedFillParts.Keys.Any(part => part < 0 || part >= regions.Count))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            IReadOnlyList<BoundaryStrokePart> boundaryParts = boundaryKeys.Length > 0 || fillKeys.Length > 0 && HasStroke(source)
                ? CachedBoundaryStrokeParts(source, frame, candidates)
                : Array.Empty<BoundaryStrokePart>();
            if (selectedBoundaryParts.Keys.Any(part => boundaryParts.All(segment => segment.PartIndex != part)))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            if (fillKeys.Length > 0)
            {
                var detachBoundary = HasStroke(source);
                if (regions.Count <= 1)
                {
                    foreach (var key in fillKeys) mappedResults[key] = key;
                    if (detachBoundary)
                    {
                        clearStroke.Add(source);
                        var boundaryAtoms = Math.Max(3u, atoms / (uint)Math.Max(1, boundaryParts.Count));
                        var retainedBoundarySubOrders = ReplacementSubOrders(
                            source,
                            boundaryParts.Count,
                            preserveSourceSubOrder: true);
                        var retainedBoundarySubOrderIndex = 0;
                        foreach (var segment in boundaryParts)
                        {
                            additions.Add(BoundaryMaterialization(
                                segment,
                                selectedBoundaryParts.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                                layer,
                                order,
                                retainedBoundarySubOrders[retainedBoundarySubOrderIndex++],
                                stroke,
                                fillColor,
                                strokeColor,
                                boundaryAtoms));
                        }
                    }
                    continue;
                }

                var regionBezierSources = fillPartition.Curves;
                remove[source] = true;
                var replacementSubOrders = ReplacementSubOrders(
                    source,
                    (detachBoundary ? boundaryParts.Count : 0) + regions.Count);
                var replacementSubOrderIndex = 0;
                if (detachBoundary)
                {
                    var boundaryAtoms = Math.Max(3u, atoms / (uint)Math.Max(1, boundaryParts.Count));
                    foreach (var segment in boundaryParts)
                    {
                        additions.Add(BoundaryMaterialization(
                            segment,
                            selectedBoundaryParts.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                            layer,
                            order,
                            replacementSubOrders[replacementSubOrderIndex++],
                            stroke,
                            fillColor,
                            strokeColor,
                            boundaryAtoms));
                    }
                }

                var fillAtoms = Math.Max(3u, atoms / (uint)Math.Max(1, regions.Count));
                for (var part = 0; part < regions.Count; part++)
                {
                    PathBezierNode[][]? regionBezierContours = null;
                    if (regionBezierSources.Length > 0
                        && TryRebuildBezierContoursFromBooleanBoundary(
                            regionBezierSources,
                            regions[part].Contours,
                            out var rebuiltBezierContours))
                    {
                        regionBezierContours = rebuiltBezierContours;
                    }

                    additions.Add(FillMaterialization(
                        selectedFillParts.GetValueOrDefault(part, DrawingElementKey.None),
                        layer,
                        order,
                        replacementSubOrders[replacementSubOrderIndex++],
                        fillColor,
                        strokeColor,
                        fillAtoms,
                        regions[part].Contours,
                        gradientPaint,
                        fillAutoMergeProtected: FillAutoMergeProtected[source],
                        bezierContours: regionBezierContours));
                }

                continue;
            }

            if (HasFill(source)) clearStroke.Add(source);
            else remove[source] = true;
            var atomsPerBoundary = Math.Max(3u, atoms / (uint)Math.Max(1, boundaryParts.Count));
            var boundarySubOrders = ReplacementSubOrders(source, boundaryParts.Count, preserveSourceSubOrder: true);
            var boundarySubOrderIndex = 0;
            foreach (var segment in boundaryParts)
            {
                additions.Add(BoundaryMaterialization(
                    segment,
                    selectedBoundaryParts.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                    layer,
                    order,
                    boundarySubOrders[boundarySubOrderIndex++],
                    stroke,
                    fillColor,
                    strokeColor,
                    atomsPerBoundary));
            }
        }

        var changed = remove.Any(value => value)
            || clearStroke.Count > 0
            || additions.Count > 0
            || mixingAdditions.Count > 0;
        if (!changed)
        {
            var identity = Enumerable.Range(0, oldObjectCount).ToArray();
            var unchanged = keys.Select(key => new MaterializedPartMapping(key, mappedResults[key])).ToArray();
            return new MaterializeSelectedPartsResult(true, false, unchanged, identity);
        }

        var additionKeyframes = new int[additions.Count];
        for (var index = 0; index < additions.Count; index++)
        {
            var addition = additions[index];
            if (!keyframesBySourceStack.TryGetValue(((ushort)addition.Layer, addition.Order), out additionKeyframes[index]))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }
        }

        var affectedTileCells = new HashSet<int>();
        var affectedOverviewCells = new HashSet<int>();
        for (var source = 0; source < oldObjectCount; source++)
        {
            if (!remove[source] && !clearStroke.Contains(source)) continue;
            AddObjectSummaryCells(source, affectedTileCells, TileColumns, TileRows);
            AddObjectSummaryCells(source, affectedOverviewCells, OverviewColumns, OverviewRows);
        }

        var snapshot = rollbackSnapshot ?? CreateSnapshot();
        var editFrame = EditFrame;
        try
        {
            var oldToNew = CompactObjectsForMaterialization(remove);
            var spatialIndexRemapped = TryRemapSpatialIndexAfterCompaction(oldToNew);
            foreach (var source in clearStroke)
            {
                var mapped = oldToNew[source];
                if (mapped >= 0) Stroke[mapped] = 0;
            }

            foreach (var item in mappedResults.ToArray())
            {
                var mappedObject = oldToNew[item.Value.ObjectIndex];
                if (mappedObject < 0) throw new InvalidOperationException("A retained selected object was removed during topology materialization.");
                mappedResults[item.Key] = item.Value with { ObjectIndex = mappedObject };
            }

            for (var additionIndex = 0; additionIndex < additions.Count; additionIndex++)
            {
                var addition = additions[additionIndex];
                EditFrame = additionKeyframes[additionIndex];
                var index = AppendMaterializedPart(addition);
                if (index < 0) throw new InvalidOperationException("Topology materialization produced invalid replacement geometry.");
                ObjectKeyframeFrame[index] = additionKeyframes[additionIndex];
                if (spatialIndexRemapped) AppendObjectToSpatialIndex(index);
                AddObjectSummaryCells(index, affectedTileCells, TileColumns, TileRows);
                AddObjectSummaryCells(index, affectedOverviewCells, OverviewColumns, OverviewRows);
                if (addition.SourceKey.IsValid)
                {
                    var kind = addition.Geometry == MaterializedPartGeometry.Fill
                        ? DrawingElementKind.Fill
                        : DrawingElementKind.Stroke;
                    mappedResults[addition.SourceKey] = new DrawingElementKey(index, kind, 0);
                }
            }

            foreach (var addition in mixingAdditions)
            {
                EditFrame = addition.KeyframeFrame;
                var index = AppendMixingBrushRegion(
                    addition.Layer,
                    addition.Region,
                    addition.Atoms);
                if (index < 0) throw new InvalidOperationException("Topology materialization produced an invalid mixing-brush component.");
                ObjectOrder[index] = addition.Order;
                ObjectSubOrder[index] = addition.SubOrder;
                ObjectKeyframeFrame[index] = addition.KeyframeFrame;
                if (spatialIndexRemapped) AppendObjectToSpatialIndex(index);
                AddObjectSummaryCells(index, affectedTileCells, TileColumns, TileRows);
                AddObjectSummaryCells(index, affectedOverviewCells, OverviewColumns, OverviewRows);
                if (addition.SourceKey.IsValid)
                {
                    mappedResults[addition.SourceKey] = new DrawingElementKey(index, DrawingElementKind.Fill, 0);
                }
            }

            if (spatialIndexRemapped)
            {
                RebuildSummaryCells(affectedTileCells, affectedOverviewCells);
            }
            else
            {
                RebuildGeometryIndex();
                RebuildSummaries();
            }
            var mappings = keys.Select(key => new MaterializedPartMapping(key, mappedResults[key])).ToArray();
            return new MaterializeSelectedPartsResult(true, true, mappings, oldToNew);
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
        }
        finally
        {
            EditFrame = editFrame;
        }
    }

    private static uint[] AllocateMixingComponentAtoms(
        uint sourceAtoms,
        IReadOnlyList<MixingBrushRegionData> components)
    {
        if (components.Count == 0) return [];
        var result = Enumerable.Repeat(3u, components.Count).ToArray();
        var minimumTotal = (ulong)result.Length * 3;
        if (sourceAtoms <= minimumTotal) return result;

        var remaining = (ulong)sourceAtoms - minimumTotal;
        var weights = components
            .Select(component => (ulong)Math.Max(
                1,
                component.Vertices.Length + component.TriangleIndices.Length / 3))
            .ToArray();
        var totalWeight = Math.Max(1UL, weights.Aggregate(0UL, (total, value) => total + value));
        var distributed = 0UL;
        for (var index = 0; index < result.Length; index++)
        {
            var share = remaining * weights[index] / totalWeight;
            result[index] += (uint)share;
            distributed += share;
        }
        var remainder = remaining - distributed;
        for (var index = 0; remainder > 0; index = (index + 1) % result.Length)
        {
            result[index]++;
            remainder--;
        }
        return result;
    }

    private static MaterializedPartAddition CurveMaterialization(
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        float stroke,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        PointF start,
        PointF control1,
        PointF control2,
        PointF end,
        LineEndpointStyle startEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle endEndpointStyle = LineEndpointStyle.Round)
    {
        return new MaterializedPartAddition(
            MaterializedPartGeometry.Curve,
            sourceKey,
            layer,
            order,
            subOrder,
            stroke,
            fillColor,
            strokeColor,
            atoms,
            startEndpointStyle,
            endEndpointStyle,
            start,
            control1,
            control2,
            end,
            Array.Empty<PointF>(),
            Array.Empty<PointF[]>());
    }

    private static MaterializedPartAddition BoundaryMaterialization(
        BoundaryStrokePart part,
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        float stroke,
        Color fillColor,
        Color strokeColor,
        uint atoms)
    {
        if (part.Curve is { } curve)
        {
            return CurveMaterialization(
                sourceKey,
                layer,
                order,
                subOrder,
                stroke,
                fillColor,
                strokeColor,
                atoms,
                curve.Start,
                curve.Control1,
                curve.Control2,
                curve.End);
        }

        return PolylineMaterialization(
            sourceKey,
            layer,
            order,
            subOrder,
            stroke,
            fillColor,
            strokeColor,
            atoms,
            part.Points);
    }

    private static MaterializedPartAddition PolylineMaterialization(
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        float stroke,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        PointF[] points)
    {
        return new MaterializedPartAddition(
            MaterializedPartGeometry.Polyline,
            sourceKey,
            layer,
            order,
            subOrder,
            stroke,
            fillColor,
            strokeColor,
            atoms,
            LineEndpointStyle.Round,
            LineEndpointStyle.Round,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            points,
            Array.Empty<PointF[]>());
    }

    private static MaterializedPartAddition FreehandMaterialization(
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        float stroke,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        PointF[] points)
    {
        return new MaterializedPartAddition(
            MaterializedPartGeometry.Freehand,
            sourceKey,
            layer,
            order,
            subOrder,
            stroke,
            fillColor,
            strokeColor,
            atoms,
            LineEndpointStyle.Round,
            LineEndpointStyle.Round,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            points,
            Array.Empty<PointF[]>());
    }

    private static MaterializedPartAddition FreehandBezierMaterialization(
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        float stroke,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        PathBezierNode[] nodes)
    {
        return FreehandMaterialization(
            sourceKey,
            layer,
            order,
            subOrder,
            stroke,
            fillColor,
            strokeColor,
            atoms,
            Array.Empty<PointF>()) with
        {
            OpenBezierNodes = nodes
        };
    }

    private static MaterializedPartAddition FillMaterialization(
        DrawingElementKey sourceKey,
        int layer,
        long order,
        double subOrder,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        PointF[][] contours,
        GradientPaintData? gradientPaint = null,
        bool fillAutoMergeProtected = false,
        PathBezierNode[][]? bezierContours = null)
    {
        return new MaterializedPartAddition(
            MaterializedPartGeometry.Fill,
            sourceKey,
            layer,
            order,
            subOrder,
            0,
            fillColor,
            strokeColor,
            atoms,
            LineEndpointStyle.Round,
            LineEndpointStyle.Round,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            Array.Empty<PointF>(),
            contours)
        {
            GradientPaint = gradientPaint,
            FillAutoMergeProtected = fillAutoMergeProtected,
            BezierContours = bezierContours ?? []
        };
    }

    private int AppendMaterializedPart(MaterializedPartAddition addition)
    {
        var index = addition.Geometry switch
        {
            MaterializedPartGeometry.Curve => AppendCubicCurveSegment(
                addition.Layer,
                addition.Start,
                addition.Control1,
                addition.Control2,
                addition.End,
                addition.Stroke,
                addition.FillColor,
                addition.StrokeColor,
                addition.Atoms,
                addition.StartEndpointStyle,
                addition.EndEndpointStyle),
            MaterializedPartGeometry.Freehand when addition.OpenBezierNodes.Length >= 2 => AppendFreehandBezierStroke(
                addition.Layer,
                addition.OpenBezierNodes,
                addition.Stroke,
                addition.StrokeColor,
                addition.Atoms),
            MaterializedPartGeometry.Freehand => AppendFreehandStroke(
                addition.Layer,
                addition.Points,
                addition.Stroke,
                addition.StrokeColor,
                addition.Atoms),
            MaterializedPartGeometry.Polyline => AppendPolylineStroke(
                addition.Layer,
                addition.Points,
                addition.Stroke,
                addition.FillColor,
                addition.StrokeColor,
                addition.Atoms),
            MaterializedPartGeometry.Fill when addition.BezierContours.Length > 0 => AppendPathBezierObjectContours(
                addition.Layer,
                addition.BezierContours,
                0,
                addition.FillColor,
                addition.StrokeColor,
                addition.Atoms),
            MaterializedPartGeometry.Fill => AppendPathObjectContours(
                addition.Layer,
                addition.Contours,
                0,
                addition.FillColor,
                addition.StrokeColor,
                addition.Atoms),
            _ => -1
        };
        if (index >= 0)
        {
            if (addition.Geometry == MaterializedPartGeometry.Curve)
            {
                SetLineEndpoint(
                    index,
                    startEndpoint: true,
                    addition.Start,
                    addition.End,
                    addition.Control1,
                    addition.Control2,
                    keepStraight: false);
            }
            ObjectOrder[index] = addition.Order;
            ObjectSubOrder[index] = addition.SubOrder;
            FillAutoMergeProtected[index] = addition.FillAutoMergeProtected;
            ApplyGradientPaint(index, addition.GradientPaint);
        }
        return index;
    }

    private double[] ReplacementSubOrders(int source, int count, bool preserveSourceSubOrder = false)
    {
        if (count <= 0) return Array.Empty<double>();
        var order = ObjectOrder[source];
        var sourceSubOrder = ObjectSubOrder[source];
        double? lower = null;
        double? upper = null;
        for (var index = 0; index < ObjectCount; index++)
        {
            if (index == source || ObjectOrder[index] != order) continue;
            var candidate = ObjectSubOrder[index];
            if (candidate < sourceSubOrder && (!lower.HasValue || candidate > lower.Value)) lower = candidate;
            if (candidate > sourceSubOrder && (!upper.HasValue || candidate < upper.Value)) upper = candidate;
        }

        const double defaultGap = 1024d;
        if (preserveSourceSubOrder)
        {
            var preservedUpperBound = upper ?? sourceSubOrder + defaultGap;
            if (TryAllocateSubOrders(sourceSubOrder, preservedUpperBound, count, out var preserved)) return preserved;

            var preservedLowerBound = lower ?? sourceSubOrder - defaultGap;
            if (TryAllocateSubOrders(preservedLowerBound, sourceSubOrder, count, out preserved)) return preserved;

            throw new InvalidOperationException("No distinct drawing sub-order remained beside the preserved source object.");
        }

        var lowerBound = lower ?? sourceSubOrder - defaultGap;
        var upperBound = upper ?? sourceSubOrder + defaultGap;
        var step = (upperBound - lowerBound) / (count + 1d);
        if (!(step > 0) || double.IsInfinity(step) || double.IsNaN(step))
        {
            lowerBound = sourceSubOrder - defaultGap;
            step = defaultGap * 2d / (count + 1d);
        }

        var result = new double[count];
        for (var index = 0; index < count; index++) result[index] = lowerBound + step * (index + 1d);
        return result;
    }

    private static bool TryAllocateSubOrders(double lowerExclusive, double upperExclusive, int count, out double[] result)
    {
        result = Array.Empty<double>();
        var step = (upperExclusive - lowerExclusive) / (count + 1d);
        if (!(step > 0) || double.IsInfinity(step) || double.IsNaN(step)) return false;

        var allocated = new double[count];
        var previous = lowerExclusive;
        for (var index = 0; index < count; index++)
        {
            var candidate = lowerExclusive + step * (index + 1d);
            if (!(candidate > previous) || !(candidate < upperExclusive)) return false;
            allocated[index] = candidate;
            previous = candidate;
        }

        result = allocated;
        return true;
    }

    private int AppendPolylineStroke(int layer, PointF[] points, float stroke, Color fillColor, Color strokeColor, uint atoms)
    {
        if (points.Length < 2) return -1;
        if (points.Length == 2)
        {
            return AppendCurveSegment(layer, points[0], Midpoint(points[0], points[1]), points[1], stroke, fillColor, strokeColor, atoms);
        }

        return AppendFreehandStroke(layer, points, stroke, strokeColor, atoms);
    }

    private int[] CompactObjectsForMaterialization(bool[] remove)
    {
        var oldCount = ObjectCount;
        var oldToNew = Enumerable.Repeat(-1, oldCount).ToArray();
        long removedAtoms = 0;
        var write = 0;
        for (var read = 0; read < oldCount; read++)
        {
            if (remove[read])
            {
                removedAtoms += AtomCount[read];
                continue;
            }

            oldToNew[read] = write;
            if (write != read) CopyObjectScalarDataFrom(this, read, write);
            write++;
        }

        ObjectCount = write;
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - removedAtoms);
        RemapObjectDictionariesAfterCompaction(oldToNew);
        return oldToNew;
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeParts(RectangleF worldBounds, int frame)
    {
        var result = MaterializeMarqueeParts(
            worldBounds,
            frame,
            includeLines: true,
            includeFills: true,
            includeWholeObjectSelections: false);
        return (result.Changed, result.SelectedObjects);
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeFillParts(RectangleF worldBounds, int frame)
    {
        var result = MaterializeMarqueeParts(
            worldBounds,
            frame,
            includeLines: false,
            includeFills: true,
            includeWholeObjectSelections: false);
        return (result.Changed, result.SelectedObjects);
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeLineParts(RectangleF worldBounds, int frame)
    {
        var result = MaterializeMarqueeParts(
            worldBounds,
            frame,
            includeLines: true,
            includeFills: false,
            includeWholeObjectSelections: false);
        return (result.Changed, result.SelectedObjects);
    }

    public MarqueeMaterializationResult MaterializeMarqueeSelectionParts(RectangleF worldBounds, int frame)
    {
        return MaterializeMarqueeParts(
            worldBounds,
            frame,
            includeLines: true,
            includeFills: true,
            includeWholeObjectSelections: true);
    }

    public MarqueeMaterializationResult MaterializeLassoSelectionParts(
        IReadOnlyList<PointF> worldPolygon,
        int frame)
    {
        var identity = Enumerable.Range(0, ObjectCount).ToArray();
        if (!TryNormalizeLassoPolygon(worldPolygon, out var polygon, out var bounds))
        {
            return new MarqueeMaterializationResult(
                false,
                false,
                Array.Empty<int>(),
                identity);
        }

        return MaterializeMarqueeParts(
            bounds,
            frame,
            includeLines: true,
            includeFills: true,
            includeWholeObjectSelections: true,
            selectionPolygon: polygon);
    }

    private MarqueeMaterializationResult MaterializeMarqueeParts(
        RectangleF worldBounds,
        int frame,
        bool includeLines,
        bool includeFills,
        bool includeWholeObjectSelections,
        PointF[]? selectionPolygon = null)
    {
        var oldObjectCount = ObjectCount;
        var identity = Enumerable.Range(0, oldObjectCount).ToArray();
        var bounds = selectionPolygon is null
            ? NormalizeToDrawingUnits(worldBounds)
            : Normalize(worldBounds);
        var canMaterializeParts = selectionPolygon is not null
            || bounds.Width >= DrawingTopologyRules.MinStrokeSegmentUnits
            && bounds.Height >= DrawingTopologyRules.MinStrokeSegmentUnits;
        if (!canMaterializeParts && !includeWholeObjectSelections)
        {
            return new MarqueeMaterializationResult(
                true,
                false,
                Array.Empty<int>(),
                identity);
        }

        var queryBounds = bounds;
        if (!canMaterializeParts)
        {
            queryBounds.Inflate(
                Math.Max(0, DrawingTopologyRules.MinStrokeSegmentUnits - bounds.Width) * 0.5f,
                Math.Max(0, DrawingTopologyRules.MinStrokeSegmentUnits - bounds.Height) * 0.5f);
        }

        var candidates = QueryObjects(queryBounds, frame);
        if (candidates.Length == 0)
        {
            return new MarqueeMaterializationResult(
                true,
                false,
                Array.Empty<int>(),
                identity);
        }

        var snapshot = CreateSnapshot();
        var editFrame = EditFrame;
        try
        {
            IReadOnlyList<int> activeCandidates = canMaterializeParts && includeFills
                ? CollectActiveCandidates(frame)
                : Array.Empty<int>();
            var remove = new bool[oldObjectCount];
            var additions = new List<MarqueeMaterializedAddition>();
            var mixingAdditions = new List<MarqueeMixingMaterializedAddition>();
            var selectedWholeObjects = new List<int>();
            foreach (var source in candidates)
            {
                if ((uint)source >= oldObjectCount || !IsObjectSelectable(source, frame)) continue;
                var shape = ShapeKind[source];
                if (selectionPolygon is not null && _objectDistortions.ContainsKey(source))
                {
                    if (includeWholeObjectSelections
                        && IsObjectGeometryInsidePolygonCore(source, selectionPolygon))
                    {
                        selectedWholeObjects.Add(source);
                    }
                    continue;
                }
                if (shape == VectorAnimationEngine.ShapeKind.MixingStroke)
                {
                    if (TryGetMixingBrushLocalRegion(source, out var mixingRegion))
                    {
                        var componentCount = mixingRegion.ConnectedComponentCount;
                        var selected = new bool[componentCount];
                        for (var partIndex = 0; partIndex < componentCount; partIndex++)
                        {
                            if (!mixingRegion.TryGetConnectedComponent(partIndex, out var component)
                                || !MixingComponentIntersectsSelection(
                                    source,
                                    component,
                                    bounds,
                                    selectionPolygon)) continue;
                            selected[partIndex] = true;
                        }
                        if (!selected.Any(value => value)) continue;
                        if (componentCount <= 1 || includeWholeObjectSelections && selected.All(value => value))
                        {
                            if (includeWholeObjectSelections) selectedWholeObjects.Add(source);
                            continue;
                        }
                        if (!includeFills && !includeWholeObjectSelections) continue;

                        var components = new MixingBrushRegionData[componentCount];
                        var validComponents = true;
                        for (var partIndex = 0; partIndex < componentCount; partIndex++)
                        {
                            if (TryGetMixingBrushWorldRegionPart(source, partIndex, out components[partIndex])) continue;
                            validComponents = false;
                            break;
                        }
                        if (!validComponents) throw new InvalidOperationException("Marquee materialization could not extract a mixing-brush component.");
                        var componentAtoms = AllocateMixingComponentAtoms(AtomCount[source], components);
                        var subOrders = ReplacementSubOrders(source, componentCount);
                        remove[source] = true;
                        for (var partIndex = 0; partIndex < componentCount; partIndex++)
                        {
                            mixingAdditions.Add(new MarqueeMixingMaterializedAddition(
                                ObjectLayer[source],
                                ObjectKeyframeFrame[source],
                                ObjectOrder[source],
                                subOrders[partIndex],
                                componentAtoms[partIndex],
                                components[partIndex],
                                selected[partIndex]));
                        }
                    }
                    else if (includeWholeObjectSelections
                        && MixingStrokeIntersectsSelection(source, bounds, selectionPolygon))
                    {
                        selectedWholeObjects.Add(source);
                    }
                    continue;
                }
                if (selectionPolygon is not null
                    && IsObjectGeometryInsidePolygonCore(source, selectionPolygon))
                {
                    if (includeWholeObjectSelections) selectedWholeObjects.Add(source);
                    continue;
                }
                if (!canMaterializeParts) continue;
                if (IsWholeObjectShape(shape)) continue;
                if (shape == VectorAnimationEngine.ShapeKind.Line)
                {
                    if (includeLines) AddLineMarqueeParts(source, bounds, additions, remove, selectionPolygon);
                    continue;
                }

                if (shape == VectorAnimationEngine.ShapeKind.Freeform)
                {
                    if (includeLines) TryAddFreehandMarqueeParts(source, bounds, additions, remove, selectionPolygon);
                    continue;
                }

                if (includeFills && !IsFreehandShape(shape))
                {
                    AddFillMarqueeParts(source, bounds, activeCandidates, additions, remove, selectionPolygon);
                }
            }

            if (!remove.Any(value => value))
            {
                return new MarqueeMaterializationResult(
                    true,
                    false,
                    selectedWholeObjects.ToArray(),
                    identity);
            }

            var oldToNew = CompactObjectsForMaterialization(remove);
            var selectedObjects = selectedWholeObjects
                .Select(index => oldToNew[index])
                .Where(index => index >= 0)
                .ToList();
            foreach (var item in additions)
            {
                EditFrame = item.KeyframeFrame;
                var index = AppendMaterializedPart(item.Addition);
                if (index < 0) throw new InvalidOperationException("Marquee materialization produced invalid replacement geometry.");
                ObjectKeyframeFrame[index] = item.KeyframeFrame;
                if (item.Selected) selectedObjects.Add(index);
            }
            foreach (var item in mixingAdditions)
            {
                EditFrame = item.KeyframeFrame;
                var index = AppendMixingBrushRegion(item.Layer, item.Region, item.Atoms);
                if (index < 0) throw new InvalidOperationException("Marquee materialization produced an invalid mixing-brush component.");
                ObjectOrder[index] = item.Order;
                ObjectSubOrder[index] = item.SubOrder;
                ObjectKeyframeFrame[index] = item.KeyframeFrame;
                if (item.Selected) selectedObjects.Add(index);
            }

            SynchronizeAllKeyframeContentKinds();
            RebuildGeometryIndex();
            RebuildSummaries();
            return new MarqueeMaterializationResult(
                true,
                true,
                selectedObjects.ToArray(),
                oldToNew);
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return new MarqueeMaterializationResult(
                false,
                false,
                Array.Empty<int>(),
                identity);
        }
        finally
        {
            EditFrame = editFrame;
        }
    }

    public int MergeSameColorFillsAround(int objectIndex, bool connectNearby = true, int frame = 0)
    {
        var operationWatch = Stopwatch.StartNew();
        LastFillMergePlanMilliseconds = 0;
        LastFillMergeSnapshotMilliseconds = 0;
        LastFillMergeMutationMilliseconds = 0;
        LastFillMergeSpatialMilliseconds = 0;
        LastFillMergeSummaryMilliseconds = 0;
        LastFillMergeTotalMilliseconds = 0;
        if ((uint)objectIndex >= ObjectCount) return objectIndex;
        if (!IsFillShape(ShapeKind[objectIndex])) return objectIndex;
        if (IsCollisionTerrainLayer(ObjectLayer[objectIndex])) return objectIndex;
        if (FillAutoMergeProtected[objectIndex]) return objectIndex;
        if (HasGradient(objectIndex)) return objectIndex;
        if (!IsObjectActive(objectIndex, frame)) return objectIndex;

        var current = objectIndex;
        var merged = true;
        while (merged && (uint)current < ObjectCount)
        {
            merged = false;
            var mergeDistance = connectNearby ? FillMergeDistanceUnits : 0.001f;
            var mergeBounds = GetObjectWorldBounds(current);
            mergeBounds.Inflate(mergeDistance, mergeDistance);
            foreach (var other in QueryObjects(mergeBounds, frame))
            {
                if (other == current || IsCollisionTerrainLayer(ObjectLayer[other])) continue;
                if (ObjectKeyframeFrame[other] != ObjectKeyframeFrame[current]) continue;
                if (!TryBuildSameColorFillMerge(current, other, connectNearby, mergeBounds, out var mergedPath)) continue;
                if (!TryGetEditableFillBezierContours(current, out var currentBezierContours)
                    || !TryGetEditableFillBezierContours(other, out var otherBezierContours))
                {
                    continue;
                }

                var sourceBezierContours = currentBezierContours.Concat(otherBezierContours).ToArray();
                if (!TryRebuildBezierContoursFromBooleanBoundary(
                        sourceBezierContours,
                        mergedPath,
                        out var mergedBezierContours))
                {
                    mergedBezierContours = mergedPath.Select(CreateLinearBezierContour).ToArray();
                }

                var layer = ObjectLayer[current];
                var keyframeFrame = ObjectKeyframeFrame[current];
                var topSource = CompareObjectStack(current, other) >= 0 ? current : other;
                var order = ObjectOrder[topSource];
                var subOrder = ObjectSubOrder[topSource];
                var fillColor = Color.FromArgb(Argb[current]);
                var strokeColor = Color.FromArgb(StrokeArgb[current]);
                var atoms = Math.Max(3u, AtomCount[current] + AtomCount[other]);
                var boundaryAdditions = new List<MaterializedPartAddition>();
                PlanBoundaryMaterializations(current, frame, boundaryAdditions, preserveSourceSubOrder: current == topSource);
                PlanBoundaryMaterializations(other, frame, boundaryAdditions, preserveSourceSubOrder: other == topSource);
                var affectedTileCells = new HashSet<int>
                {
                    SummaryCellForObject(current, TileColumns, TileRows),
                    SummaryCellForObject(other, TileColumns, TileRows)
                };
                var affectedOverviewCells = new HashSet<int>
                {
                    SummaryCellForObject(current, OverviewColumns, OverviewRows),
                    SummaryCellForObject(other, OverviewColumns, OverviewRows)
                };
                var mergeSource = current;
                LastFillMergePlanMilliseconds = operationWatch.Elapsed.TotalMilliseconds;
                var stageWatch = Stopwatch.StartNew();
                var snapshot = CreateSnapshot();
                LastFillMergeSnapshotMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                try
                {
                    stageWatch.Restart();
                    var remove = new bool[ObjectCount];
                    remove[current] = true;
                    remove[other] = true;
                    var oldToNew = CompactObjectsForMaterialization(remove);
                    var spatialIndexRemapped = TryRemapSpatialIndexAfterCompaction(oldToNew);
                    current = AppendPathBezierObjectContours(
                        layer,
                        mergedBezierContours,
                        0,
                        fillColor,
                        strokeColor,
                        atoms);
                    if (current < 0) throw new InvalidOperationException("Merged fill geometry was invalid.");
                    ObjectKeyframeFrame[current] = keyframeFrame;
                    ObjectOrder[current] = order;
                    ObjectSubOrder[current] = subOrder;
                    if (spatialIndexRemapped) AppendObjectToSpatialIndex(current);
                    affectedTileCells.Add(SummaryCellForObject(current, TileColumns, TileRows));
                    affectedOverviewCells.Add(SummaryCellForObject(current, OverviewColumns, OverviewRows));
                    foreach (var addition in boundaryAdditions)
                    {
                        var boundary = AppendMaterializedPart(addition);
                        if (boundary < 0)
                        {
                            throw new InvalidOperationException("A fill boundary could not be preserved during merge.");
                        }
                        ObjectKeyframeFrame[boundary] = keyframeFrame;
                        if (spatialIndexRemapped) AppendObjectToSpatialIndex(boundary);
                        AddObjectSummaryCells(boundary, affectedTileCells, TileColumns, TileRows);
                        AddObjectSummaryCells(boundary, affectedOverviewCells, OverviewColumns, OverviewRows);
                    }

                    LastFillMergeMutationMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                    MaxHalfExtent = Math.Max(MaxHalfExtent, ObjectHalfExtent(current));
                    stageWatch.Restart();
                    if (!spatialIndexRemapped) RebuildSpatialIndex();
                    LastFillMergeSpatialMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                    stageWatch.Restart();
                    RebuildSummaryCells(affectedTileCells, affectedOverviewCells);
                    LastFillMergeSummaryMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                    LastFillMergeTotalMilliseconds = operationWatch.Elapsed.TotalMilliseconds;
                    merged = true;
                }
                catch
                {
                    RestoreSnapshot(snapshot);
                    current = mergeSource;
                    merged = false;
                }

                break;
            }
        }

        return current;
    }

    public int MergeMatchingShapeGradientFillsAround(int objectIndex, int frame = 0)
    {
        if ((uint)objectIndex >= ObjectCount
            || !IsMatchingShapeGradientFill(this, objectIndex)
            || IsCollisionTerrainLayer(ObjectLayer[objectIndex])
            || FillAutoMergeProtected[objectIndex]
            || !IsObjectActive(objectIndex, frame))
        {
            return objectIndex;
        }

        var current = objectIndex;
        var merged = true;
        while (merged && (uint)current < ObjectCount)
        {
            merged = false;
            var mergeBounds = GetObjectWorldBounds(current);
            foreach (var other in QueryObjects(mergeBounds, frame))
            {
                if (other == current
                    || ObjectLayer[other] != ObjectLayer[current]
                    || ObjectKeyframeFrame[other] != ObjectKeyframeFrame[current]
                    || IsCollisionTerrainLayer(ObjectLayer[other])
                    || FillAutoMergeProtected[other]
                    || !MatchingShapeGradientMaterial(this, current, this, other)
                    || !TryBuildIntersectingFillUnion(current, other, mergeBounds, out var mergedPath))
                {
                    continue;
                }

                var topSource = CompareObjectStack(current, other) >= 0 ? current : other;
                var layer = ObjectLayer[current];
                var keyframeFrame = ObjectKeyframeFrame[current];
                var order = ObjectOrder[topSource];
                var subOrder = ObjectSubOrder[topSource];
                var fillColor = Color.FromArgb(Argb[current]);
                var stops = GetGradientStops(current);
                var atoms = Math.Max(3u, AtomCount[current] + AtomCount[other]);
                var mergeSource = current;
                var snapshot = CreateSnapshot();
                try
                {
                    var remove = new bool[ObjectCount];
                    remove[current] = true;
                    remove[other] = true;
                    CompactObjectsForMaterialization(remove);
                    current = AppendPathObjectContours(layer, mergedPath, 0, fillColor, Color.Transparent, atoms);
                    if (current < 0) throw new InvalidOperationException("Merged shape-gradient fill geometry was invalid.");
                    ObjectKeyframeFrame[current] = keyframeFrame;
                    ObjectOrder[current] = order;
                    ObjectSubOrder[current] = subOrder;
                    ApplyRecalculatedShapeGradient(current, stops, mergedPath);
                    RebuildGeometryIndex();
                    RebuildSummaries();
                    merged = true;
                }
                catch
                {
                    RestoreSnapshot(snapshot);
                    current = mergeSource;
                    merged = false;
                }

                break;
            }
        }

        return current;
    }

    public int[] MergeMatchingShapeGradientFillsAroundNewObjects(
        IReadOnlyList<int> objectIndices,
        int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var sourceKeys = objectIndices
            .Where(index => (uint)index < ObjectCount
                && IsFillShape(ShapeKind[index])
                && !IsCollisionTerrainLayer(ObjectLayer[index]))
            .Select(index => (
                Layer: ObjectLayer[index],
                Keyframe: ObjectKeyframeFrame[index],
                Order: ObjectOrder[index],
                SubOrder: ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        if (sourceKeys.Length == 0) return [];

        var resultKeys = new HashSet<(ushort Layer, int Keyframe, long Order, double SubOrder)>();
        foreach (var key in sourceKeys)
        {
            var source = FindObjectByStackKey(key.Layer, key.Keyframe, key.Order, key.SubOrder);
            if (source < 0 || !IsFillShape(ShapeKind[source])) continue;
            var merged = MergeMatchingShapeGradientFillsAround(source, frame);
            if ((uint)merged < ObjectCount)
            {
                resultKeys.Add((
                    ObjectLayer[merged],
                    ObjectKeyframeFrame[merged],
                    ObjectOrder[merged],
                    ObjectSubOrder[merged]));
            }
        }

        var distinct = sourceKeys
            .Concat(resultKeys)
            .Distinct()
            .Select(key => FindObjectByStackKey(key.Layer, key.Keyframe, key.Order, key.SubOrder))
            .Where(index => (uint)index < ObjectCount && IsFillShape(ShapeKind[index]))
            .Distinct()
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
        var shapeGradients = distinct
            .Where(index => IsMatchingShapeGradientFill(this, index))
            .ToArray();
        if (shapeGradients.Length > 0)
        {
            var sharedMapping = FillWorldContours(shapeGradients[0]);
            if (sharedMapping.Length > 0)
            {
                var center = ShapeGradientCenter(sharedMapping);
                var end = ShapeGradientEnd(sharedMapping, center);
                foreach (var index in shapeGradients)
                {
                    var stops = GetGradientStops(index);
                    SetGradientPaint(index, GradientKind.ShapeRadial, stops, center, end);
                    SetShapeGradientMapping(index, sharedMapping);
                }
            }
        }

        return distinct;
    }

    internal int[] FindIntersectingMatchingShapeGradientFills(
        VectorScene additions,
        IReadOnlyList<int> additionIndices,
        int frame,
        int maximumResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(additionIndices);
        var matches = new HashSet<int>();
        foreach (var addition in additionIndices)
        {
            if (!IsMatchingShapeGradientFill(additions, addition)
                || IsCollisionTerrainLayer(additions.ObjectLayer[addition])) continue;
            var bounds = additions.GetObjectWorldBounds(addition);
            var additionPaths = ToClipperPaths(additions.FillWorldContours(addition));
            if (additionPaths.Count == 0) continue;
            foreach (var candidate in QueryObjects(bounds, frame))
            {
                if (!IsObjectActive(candidate, frame)
                    || ObjectLayer[candidate] != additions.ObjectLayer[addition]
                    || IsCollisionTerrainLayer(ObjectLayer[candidate])
                    || !MatchingShapeGradientMaterial(this, candidate, additions, addition))
                {
                    continue;
                }

                var candidatePaths = ToClipperPaths(FillWorldContours(candidate));
                if (candidatePaths.Count > 0 && ClipperPathsIntersect(candidatePaths, additionPaths))
                {
                    matches.Add(candidate);
                    if (matches.Count >= maximumResults)
                    {
                        return matches.OrderBy(index => ObjectOrder[index]).ThenBy(index => ObjectSubOrder[index]).ToArray();
                    }
                }
            }
        }

        return matches.OrderBy(index => ObjectOrder[index]).ThenBy(index => ObjectSubOrder[index]).ToArray();
    }

    private int FindObjectByStackKey(long order, double subOrder)
    {
        for (var index = 0; index < ObjectCount; index++)
        {
            if (ObjectOrder[index] == order && ObjectSubOrder[index].Equals(subOrder)) return index;
        }
        return -1;
    }

    private int FindObjectByStackKey(int layer, int keyframe, long order, double subOrder)
    {
        for (var index = 0; index < ObjectCount; index++)
        {
            if (ObjectLayer[index] == layer
                && ObjectKeyframeFrame[index] == keyframe
                && ObjectOrder[index] == order
                && ObjectSubOrder[index].Equals(subOrder))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsMatchingShapeGradientFill(VectorScene scene, int objectIndex)
    {
        return (uint)objectIndex < scene.ObjectCount
            && scene.ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && scene.Stroke[objectIndex] <= 0
            && scene.HasGradient(objectIndex)
            && scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial;
    }

    private static bool MatchingShapeGradientMaterial(VectorScene a, int aIndex, VectorScene b, int bIndex)
    {
        // Gradient stops define the visible paint. The packed fill RGB is only a
        // fallback color, while its alpha still distinguishes soft-brush layers.
        return IsMatchingShapeGradientFill(a, aIndex)
            && IsMatchingShapeGradientFill(b, bIndex)
            && ((uint)a.Argb[aIndex] >> 24) == ((uint)b.Argb[bIndex] >> 24)
            && a.GetGradientStops(aIndex).SequenceEqual(b.GetGradientStops(bIndex));
    }

    private bool TryBuildIntersectingFillUnion(
        int a,
        int b,
        RectangleF mergeBounds,
        out PointF[][] mergedPath)
    {
        mergedPath = Array.Empty<PointF[]>();
        if (!mergeBounds.IntersectsWith(GetObjectWorldBounds(b))) return false;
        var pathsA = ToClipperPaths(FillWorldContours(a));
        var pathsB = ToClipperPaths(FillWorldContours(b));
        if (pathsA.Count == 0 || pathsB.Count == 0 || !ClipperPathsIntersect(pathsA, pathsB)) return false;
        mergedPath = BuildMergedFillPath(pathsA, pathsB);
        return mergedPath.Length > 0;
    }

    private void ApplyRecalculatedShapeGradient(
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        PointF[][] contours)
    {
        var center = ShapeGradientCenter(contours);
        var end = ShapeGradientEnd(contours, center);
        SetGradientPaint(objectIndex, GradientKind.ShapeRadial, stops, center, end);
        SetShapeGradientMapping(objectIndex, contours);
    }

    private static PointF ShapeGradientCenter(PointF[][] contours)
    {
        var bounds = ContourBounds(contours);
        var desired = new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
        if (PointInCompoundPolygon(desired, contours)) return VectorUnits.Quantize(desired);

        const int grid = 17;
        var candidates = new List<(PointF Point, float Depth, float DistanceSquared)>(grid * grid);
        var maximumDepth = 0f;
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var point = new PointF(
                    bounds.Left + bounds.Width * (x + 0.5f) / grid,
                    bounds.Top + bounds.Height * (y + 0.5f) / grid);
                if (!PointInCompoundPolygon(point, contours)) continue;
                var depth = float.PositiveInfinity;
                foreach (var contour in contours)
                {
                    for (var index = 0; index < contour.Length; index++)
                    {
                        depth = Math.Min(depth, DistanceToSegment(point, contour[index], contour[(index + 1) % contour.Length]));
                    }
                }

                var dx = point.X - desired.X;
                var dy = point.Y - desired.Y;
                candidates.Add((point, depth, dx * dx + dy * dy));
                maximumDepth = Math.Max(maximumDepth, depth);
            }
        }

        var preferredDepth = maximumDepth * 0.65f;
        var resolved = candidates
            .Where(candidate => candidate.Depth >= preferredDepth)
            .OrderBy(candidate => candidate.DistanceSquared)
            .Select(candidate => candidate.Point)
            .FirstOrDefault(desired);
        return VectorUnits.Quantize(resolved);
    }

    private static PointF ShapeGradientEnd(PointF[][] contours, PointF center)
    {
        if (GradientPaintUtilities.TryFindShapeBoundaryPoint(contours, center, new PointF(1f, 0f), out var boundary))
        {
            return VectorUnits.Quantize(boundary);
        }

        var bounds = ContourBounds(contours);
        return VectorUnits.Quantize(new PointF(bounds.Right, center.Y));
    }

    public int[] MergeSameColorFillsAroundNewObjects(
        IReadOnlyList<int> objectIndices,
        bool connectNearby = true,
        int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var sourceKeys = objectIndices
            .Where(index => (uint)index < ObjectCount
                && IsFillShape(ShapeKind[index])
                && !IsCollisionTerrainLayer(ObjectLayer[index]))
            .Select(index => (
                Layer: ObjectLayer[index],
                Keyframe: ObjectKeyframeFrame[index],
                Order: ObjectOrder[index],
                SubOrder: ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        if (sourceKeys.Length == 0) return [];

        var resultKeys = new HashSet<(ushort Layer, int Keyframe, long Order, double SubOrder)>();
        foreach (var key in sourceKeys)
        {
            var source = FindObjectByStackKey(key.Layer, key.Keyframe, key.Order, key.SubOrder);

            if (source < 0 || !IsFillShape(ShapeKind[source])) continue;
            var merged = MergeSameColorFillsAround(source, connectNearby, frame);
            if ((uint)merged < ObjectCount)
            {
                resultKeys.Add((
                    ObjectLayer[merged],
                    ObjectKeyframeFrame[merged],
                    ObjectOrder[merged],
                    ObjectSubOrder[merged]));
            }
        }

        return sourceKeys
            .Concat(resultKeys)
            .Distinct()
            .Select(key => FindObjectByStackKey(key.Layer, key.Keyframe, key.Order, key.SubOrder))
            .Where(index => (uint)index < ObjectCount && IsFillShape(ShapeKind[index]))
            .Distinct()
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
    }

    public int[] ApplyFillOverwriteToNewObjects(IReadOnlyList<int> objectIndices, int frame = 0)
    {
        return ApplyFillOverwriteToNewObjectsCore(objectIndices, frame, mergeMatchingShapeGradients: true);
    }

    private int[] ApplyFillOverwriteToNewObjectsCore(
        IReadOnlyList<int> objectIndices,
        int frame,
        bool mergeMatchingShapeGradients)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var mergedShapeGradients = mergeMatchingShapeGradients
            ? MergeMatchingShapeGradientFillsAroundNewObjects(objectIndices, frame)
            : objectIndices
                .Where(index => (uint)index < ObjectCount
                    && HasFill(index)
                    && !IsCollisionTerrainLayer(ObjectLayer[index]))
                .Distinct()
                .ToArray();
        var newObjects = mergedShapeGradients
            .Where(index => (uint)index < ObjectCount
                && HasFill(index)
                && !IsCollisionTerrainLayer(ObjectLayer[index]))
            .Distinct()
            .ToArray();
        if (newObjects.Length == 0) return Array.Empty<int>();

        var newObjectSet = newObjects.ToHashSet();
        var plans = new List<FillOverwritePlan>();
        try
        {
            foreach (var group in newObjects.GroupBy(index => (Layer: ObjectLayer[index], Keyframe: ObjectKeyframeFrame[index])))
            {
                var cutterObjects = group.ToArray();
                var cutters = cutterObjects
                    .Select(index => new FillOverwriteCutter(
                        index,
                        GetObjectWorldBounds(index),
                        ToClipperPaths(FillWorldContours(index))))
                    .Where(cutter => cutter.Paths.Count > 0)
                    .ToArray();
                if (cutters.Length == 0) continue;
                var bounds = GetObjectWorldBounds(cutterObjects[0]);
                for (var index = 1; index < cutterObjects.Length; index++) bounds = RectangleF.Union(bounds, GetObjectWorldBounds(cutterObjects[index]));

                var candidates = QueryObjects(bounds, frame)
                    .Where(source => !newObjectSet.Contains(source)
                        && ObjectLayer[source] == group.Key.Layer
                        && ObjectKeyframeFrame[source] == group.Key.Keyframe
                        && !IsCollisionTerrainLayer(ObjectLayer[source])
                        && HasFill(source))
                    .ToArray();
                var groupPlans = new FillOverwritePlan?[candidates.Length];
                ParallelBatch.For(candidates.Length, 8, (_, start, end) =>
                {
                    for (var candidateIndex = start; candidateIndex < end; candidateIndex++)
                    {
                        try
                        {
                            if (TryBuildFillOverwritePlan(candidates[candidateIndex], cutters, frame, out var plan))
                            {
                                groupPlans[candidateIndex] = plan;
                            }
                        }
                        catch (Exception ex) when (ex is ClipperLibException or OverflowException or InvalidOperationException)
                        {
                            // Independent invalid geometry does not block other overwrite candidates.
                        }
                    }
                });
                foreach (var plan in groupPlans)
                {
                    if (plan is not null) plans.Add(plan);
                }
            }
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException or InvalidOperationException)
        {
            return newObjects;
        }

        if (plans.Count == 0) return newObjects;

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            var remove = new bool[ObjectCount];
            foreach (var plan in plans) remove[plan.Source] = true;
            var oldToNew = CompactObjectsForMaterialization(remove);
            var result = newObjects
                .Select(index => oldToNew[index])
                .Where(index => index >= 0)
                .ToArray();

            foreach (var plan in plans)
            {
                EditFrame = plan.KeyframeFrame;
                var subOrderIndex = 0;
                foreach (var region in plan.RemainingRegions)
                {
                    var replacement = AppendPathObjectContours(
                        plan.Layer,
                        region.Contours,
                        0,
                        Color.FromArgb(plan.FillArgb),
                        Color.Transparent,
                        plan.AtomsPerReplacement);
                    if (replacement < 0) throw new InvalidOperationException("Fill overwrite produced invalid replacement geometry.");
                    ObjectKeyframeFrame[replacement] = plan.KeyframeFrame;
                    ObjectOrder[replacement] = plan.Order;
                    ObjectSubOrder[replacement] = plan.ReplacementSubOrders[subOrderIndex++];
                    FillAutoMergeProtected[replacement] = plan.FillAutoMergeProtected;
                    if (plan.HasGradient)
                    {
                        // A Boolean fragment no longer owns a contiguous portion
                        // of the original brush trajectory. Keep its paint stops
                        // and global axis, but do not replay the full brush path.
                        SetGradientPaint(
                            replacement,
                            plan.GradientKind,
                            plan.GradientStops,
                            plan.GradientStart,
                            plan.GradientEnd);
                        if (plan.ShapeMappingContours.Length > 0)
                        {
                            SetShapeGradientMapping(replacement, plan.ShapeMappingContours);
                        }
                    }
                }

                foreach (var boundary in plan.BoundaryAdditions)
                {
                    var replacement = AppendMaterializedPart(boundary with
                    {
                        SubOrder = plan.ReplacementSubOrders[subOrderIndex++],
                        Atoms = plan.AtomsPerReplacement
                    });
                    if (replacement < 0) throw new InvalidOperationException("Fill overwrite could not preserve a boundary stroke.");
                    ObjectKeyframeFrame[replacement] = plan.KeyframeFrame;
                }
            }

            SynchronizeAllKeyframeContentKinds();
            RebuildGeometryIndex();
            RebuildSummaries();
            return result;
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return newObjects;
        }
        finally
        {
            EditFrame = previousEditFrame;
        }
    }

    internal int[] NormalizePaintForInteractiveCommit(
        IReadOnlyList<int> objectIndices,
        bool connectNearby = true,
        int frame = 0,
        bool enforceComplexityBudget = false)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var retained = objectIndices
            .Where(index => (uint)index < ObjectCount
                && IsFillShape(ShapeKind[index])
                && !IsCollisionTerrainLayer(ObjectLayer[index]))
            .Distinct()
            .ToArray();
        if (retained.Length == 0) return retained;

        // Matching shape gradients must still union when a long or complex stroke
        // exceeds the broader overwrite budget used for interactive painting.
        if (retained.Any(index => IsMatchingShapeGradientFill(this, index)))
        {
            retained = MergeMatchingShapeGradientFillsAroundNewObjects(retained, frame);
        }

        if (retained.Length == 0) return retained;
        if (enforceComplexityBudget && !CanNormalizePaintInteractively(retained, frame)) return retained;
        retained = ApplyFillOverwriteToNewObjectsCore(retained, frame, mergeMatchingShapeGradients: false);
        return MergeSameColorFillsAroundNewObjects(retained, connectNearby, frame);
    }

    internal bool CanNormalizePaintInteractively(
        IReadOnlyList<int> objectIndices,
        int frame,
        int maximumCandidates = 4,
        uint maximumCombinedAtoms = 512,
        int maximumSceneObjects = 512)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        if (ObjectCount > maximumSceneObjects) return false;
        var additions = objectIndices
            .Where(index => (uint)index < ObjectCount
                && HasFill(index)
                && !IsCollisionTerrainLayer(ObjectLayer[index]))
            .Distinct()
            .ToArray();
        if (additions.Length == 0) return false;

        var additionSet = additions.ToHashSet();
        var candidates = new HashSet<int>();
        ulong combinedAtoms = 0;
        foreach (var addition in additions)
        {
            combinedAtoms += Math.Max(3u, AtomCount[addition]);
            if (combinedAtoms > maximumCombinedAtoms) return false;
            var bounds = GetObjectWorldBounds(addition);
            foreach (var candidate in QueryObjects(bounds, frame))
            {
                if (additionSet.Contains(candidate)
                    || ObjectLayer[candidate] != ObjectLayer[addition]
                    || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[addition]
                    || IsCollisionTerrainLayer(ObjectLayer[candidate])
                    || !HasFill(candidate)
                    || !candidates.Add(candidate))
                {
                    continue;
                }

                if (candidates.Count > maximumCandidates) return false;
                combinedAtoms += Math.Max(3u, AtomCount[candidate]);
                if (combinedAtoms > maximumCombinedAtoms) return false;
            }
        }

        return true;
    }

    private bool TryBuildFillOverwritePlan(
        int source,
        IReadOnlyList<FillOverwriteCutter> cutters,
        int frame,
        out FillOverwritePlan plan)
    {
        plan = null!;
        if ((uint)source >= ObjectCount
            || !HasFill(source)
            || IsCollisionTerrainLayer(ObjectLayer[source])) return false;

        var sourceBounds = GetObjectWorldBounds(source);
        var remainingContours = FillWorldContours(source);
        var changed = false;
        List<FillRegion>? remainingRegions = null;
        foreach (var cutter in cutters)
        {
            if (IsCollisionTerrainLayer(ObjectLayer[cutter.ObjectIndex])) continue;
            if (!sourceBounds.IntersectsWith(cutter.Bounds)) continue;
            // Solid fills with the same material are unioned later. A gradient fill
            // remains independent, so it covers every older fill material.
            if (!HasGradient(cutter.ObjectIndex) && SameFillMaterial(source, cutter.ObjectIndex)) continue;
            if (!TryDifferenceFillRegions(remainingContours, cutter.Paths, out var regions)) continue;
            changed = true;
            remainingRegions = regions;
            remainingContours = regions.SelectMany(region => region.Contours).ToArray();
            if (remainingContours.Length == 0) break;
        }

        if (!changed) return false;

        var boundaries = new List<MaterializedPartAddition>();
        if (HasStroke(source)) AddUnchangedStrokeMaterializations(source, boundaries);
        var replacementCount = (remainingRegions?.Count ?? 0) + boundaries.Count;
        var subOrders = replacementCount switch
        {
            0 => Array.Empty<double>(),
            1 => new[] { ObjectSubOrder[source] },
            _ => ReplacementSubOrders(source, replacementCount)
        };
        var hasGradient = HasGradient(source);
        plan = new FillOverwritePlan(
            source,
            ObjectLayer[source],
            ObjectKeyframeFrame[source],
            ObjectOrder[source],
            Argb[source],
            FillAutoMergeProtected[source],
            hasGradient,
            hasGradient ? GradientKinds[source] : GradientKind.Solid,
            hasGradient ? GetGradientStops(source) : Array.Empty<GradientStop>(),
            hasGradient ? GetGradientStart(source) : PointF.Empty,
            hasGradient ? GetGradientEnd(source) : PointF.Empty,
            hasGradient && TryGetShapeGradientMappingWorldContours(source, out var shapeMappingContours)
                ? shapeMappingContours
                : Array.Empty<PointF[]>(),
            remainingRegions ?? new List<FillRegion>(),
            boundaries,
            subOrders,
            Math.Max(3u, AtomCount[source] / (uint)Math.Max(1, replacementCount)));
        return true;
    }

    private bool SameFillMaterial(int first, int second)
    {
        if (Argb[first] != Argb[second]) return false;
        var firstHasGradient = HasGradient(first);
        if (firstHasGradient != HasGradient(second)) return false;
        if (!firstHasGradient) return true;
        if (GradientKinds[first] != GradientKinds[second]
            || GetGradientStart(first) != GetGradientStart(second)
            || GetGradientEnd(first) != GetGradientEnd(second))
        {
            return false;
        }

        return GetGradientStops(first).SequenceEqual(GetGradientStops(second));
    }

    public LineSegmentMergeResult MergeCompatibleLineSegments(
        int frame,
        IReadOnlyCollection<int>? objectScope = null)
    {
        var oldObjectCount = ObjectCount;
        if (oldObjectCount < 2) return new LineSegmentMergeResult(false, 0, Array.Empty<int>());

        HashSet<int>? scopedObjects = null;
        if (objectScope is not null)
        {
            scopedObjects = objectScope
                .Where(index => (uint)index < oldObjectCount)
                .ToHashSet();
            if (scopedObjects.Count < 2) return new LineSegmentMergeResult(false, 0, Array.Empty<int>());
        }

        var candidateGeometry = new List<(int ObjectIndex, PointF Start, PointF End)>();
        var candidateIndices = scopedObjects is null
            ? Enumerable.Range(0, oldObjectCount)
            : scopedObjects.OrderBy(index => index);
        foreach (var index in candidateIndices)
        {
            if (TryGetMergeableStraightLine(index, frame, out var start, out var end))
            {
                candidateGeometry.Add((index, start, end));
            }
        }

        if (candidateGeometry.Count < 2) return new LineSegmentMergeResult(false, 0, Array.Empty<int>());

        var candidateCount = candidateGeometry.Count;
        var candidates = new int[candidateCount];
        var starts = new PointF[candidateCount];
        var ends = new PointF[candidateCount];
        var directionBuckets = new int[candidateCount];
        var parents = Enumerable.Range(0, candidateCount).ToArray();
        var ranks = new byte[candidateCount];
        for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            var candidate = candidateGeometry[candidateIndex];
            candidates[candidateIndex] = candidate.ObjectIndex;
            starts[candidateIndex] = candidate.Start;
            ends[candidateIndex] = candidate.End;
            directionBuckets[candidateIndex] = LineMergeDirectionBucket(candidate.Start, candidate.End);
        }

        var buckets = new Dictionary<LineMergeBucketKey, List<LineMergeEndpoint>>();
        for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            AddLineMergeEndpoint(candidateIndex, starts[candidateIndex]);
            AddLineMergeEndpoint(candidateIndex, ends[candidateIndex]);
        }

        var groups = Enumerable.Range(0, candidateCount)
            .GroupBy(index => FindLineMergeRoot(parents, index))
            .Select(group => group.ToArray())
            .Where(group => group.Length > 1)
            .ToArray();
        if (groups.Length == 0) return new LineSegmentMergeResult(false, 0, Array.Empty<int>());

        var plans = new List<LineMergePlan>(groups.Length);
        var remove = new bool[oldObjectCount];
        var mergeCount = 0;
        foreach (var group in groups)
        {
            var retainedCandidate = group[0];
            var retainedSource = candidates[retainedCandidate];
            foreach (var candidateIndex in group)
            {
                var source = candidates[candidateIndex];
                if (CompareObjectStack(source, retainedSource) <= 0) continue;
                retainedCandidate = candidateIndex;
                retainedSource = source;
            }

            var baselineStart = starts[retainedCandidate];
            var baselineEnd = ends[retainedCandidate];
            if (!LineMergeGroupSharesBaseline(group, retainedCandidate, starts, ends))
            {
                continue;
            }

            foreach (var candidateIndex in group) remove[candidates[candidateIndex]] = true;

            var directionX = baselineEnd.X - baselineStart.X;
            var directionY = baselineEnd.Y - baselineStart.Y;
            var directionLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
            directionX /= directionLength;
            directionY /= directionLength;

            var minimumProjection = float.MaxValue;
            var maximumProjection = float.MinValue;
            var startEndpointStyle = LineEndpointStyle.Round;
            var endEndpointStyle = LineEndpointStyle.Round;
            foreach (var candidateIndex in group)
            {
                var source = candidates[candidateIndex];
                UpdateLineMergeExtent(starts[candidateIndex], GetLineEndpointStyle(source, startEndpoint: true));
                UpdateLineMergeExtent(ends[candidateIndex], GetLineEndpointStyle(source, startEndpoint: false));
            }

            var mergedStart = new PointF(
                baselineStart.X + minimumProjection * directionX,
                baselineStart.Y + minimumProjection * directionY);
            var mergedEnd = new PointF(
                baselineStart.X + maximumProjection * directionX,
                baselineStart.Y + maximumProjection * directionY);

            ulong atomTotal = 0;
            var sources = new int[group.Length];
            for (var index = 0; index < group.Length; index++)
            {
                var source = candidates[group[index]];
                sources[index] = source;
                atomTotal += AtomCount[source];
            }

            plans.Add(new LineMergePlan(
                sources,
                ObjectLayer[retainedSource],
                ObjectKeyframeFrame[retainedSource],
                mergedStart,
                mergedEnd,
                Stroke[retainedSource],
                Color.FromArgb(Argb[retainedSource]),
                Color.FromArgb(StrokeArgb[retainedSource]),
                (uint)Math.Min(uint.MaxValue, Math.Max(3UL, atomTotal)),
                ObjectOrder[retainedSource],
                ObjectSubOrder[retainedSource],
                startEndpointStyle,
                endEndpointStyle));
            mergeCount += group.Length - 1;

            void UpdateLineMergeExtent(PointF point, LineEndpointStyle endpointStyle)
            {
                var projection = (point.X - baselineStart.X) * directionX
                    + (point.Y - baselineStart.Y) * directionY;
                if (projection < minimumProjection)
                {
                    minimumProjection = projection;
                    startEndpointStyle = endpointStyle;
                }

                if (projection > maximumProjection)
                {
                    maximumProjection = projection;
                    endEndpointStyle = endpointStyle;
                }
            }
        }

        if (plans.Count == 0) return new LineSegmentMergeResult(false, 0, Array.Empty<int>());

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        try
        {
            EditFrame = frame;
            var oldToNew = CompactObjectsForMaterialization(remove);
            foreach (var plan in plans)
            {
                var merged = AppendCurveSegment(
                    plan.Layer,
                    plan.Start,
                    Midpoint(plan.Start, plan.End),
                    plan.End,
                    plan.Stroke,
                    plan.FillColor,
                    plan.StrokeColor,
                    plan.Atoms,
                    plan.StartEndpointStyle,
                    plan.EndEndpointStyle);
                if (merged < 0) throw new InvalidOperationException("Compatible line merge produced invalid geometry.");

                ObjectKeyframeFrame[merged] = plan.KeyframeFrame;
                ObjectOrder[merged] = plan.Order;
                ObjectSubOrder[merged] = plan.SubOrder;
                foreach (var source in plan.Sources) oldToNew[source] = merged;
            }

            RebuildGeometryIndex();
            RebuildSummaries();
            return new LineSegmentMergeResult(true, mergeCount, oldToNew);
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return new LineSegmentMergeResult(false, 0, Array.Empty<int>());
        }
        finally
        {
            EditFrame = previousEditFrame;
        }

        void AddLineMergeEndpoint(int candidateIndex, PointF point)
        {
            var objectIndex = candidates[candidateIndex];
            var directionBucket = directionBuckets[candidateIndex];
            var cellX = (int)MathF.Floor(point.X / LineMergeDistanceUnits);
            var cellY = (int)MathF.Floor(point.Y / LineMergeDistanceUnits);
            for (var directionOffset = -1; directionOffset <= 1; directionOffset++)
            {
                var nearbyDirectionBucket = WrapLineMergeDirectionBucket(directionBucket + directionOffset);
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var nearbyKey = LineMergeKey(
                            objectIndex,
                            nearbyDirectionBucket,
                            cellX + offsetX,
                            cellY + offsetY);
                        if (!buckets.TryGetValue(nearbyKey, out var nearby)) continue;
                        foreach (var endpoint in nearby)
                        {
                            if (endpoint.CandidateIndex == candidateIndex
                                || DistanceSquared(point, endpoint.Point) >= LineMergeDistanceUnits * LineMergeDistanceUnits
                                || !LinesCanMergeGeometrically(
                                    starts[candidateIndex],
                                    ends[candidateIndex],
                                    starts[endpoint.CandidateIndex],
                                    ends[endpoint.CandidateIndex]))
                            {
                                continue;
                            }

                            UnionLineMergeRoots(parents, ranks, candidateIndex, endpoint.CandidateIndex);
                        }
                    }
                }
            }

            var key = LineMergeKey(objectIndex, directionBucket, cellX, cellY);
            if (!buckets.TryGetValue(key, out var endpoints))
            {
                endpoints = new List<LineMergeEndpoint>();
                buckets.Add(key, endpoints);
            }

            endpoints.Add(new LineMergeEndpoint(candidateIndex, point));
        }

        LineMergeBucketKey LineMergeKey(int objectIndex, int directionBucket, int cellX, int cellY)
        {
            return new LineMergeBucketKey(
                ObjectLayer[objectIndex],
                ObjectKeyframeFrame[objectIndex],
                Argb[objectIndex],
                StrokeArgb[objectIndex],
                BitConverter.SingleToInt32Bits(Stroke[objectIndex]),
                GetLineEndpointStyle(objectIndex, startEndpoint: true),
                GetLineEndpointStyle(objectIndex, startEndpoint: false),
                directionBucket,
                cellX,
                cellY);
        }
    }

    private bool TryGetMergeableStraightLine(int objectIndex, int frame, out PointF start, out PointF end)
    {
        start = PointF.Empty;
        end = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || HasGradient(objectIndex)
            || !IsObjectActive(objectIndex, frame)
            || !TryGetLineEndpoint(objectIndex, startEndpoint: true, out start)
            || !TryGetLineEndpoint(objectIndex, startEndpoint: false, out end))
        {
            return false;
        }

        // Controls are stored on the integer vector-unit grid. For odd-length straight
        // segments the stored midpoint may be 0.5 vu from the mathematical midpoint,
        // while the segment still has zero curvature. Measure the curve itself instead
        // of comparing its control point to an unquantized midpoint.
        return IsLineStraight(objectIndex);
    }

    private static int LineMergeDirectionBucket(PointF start, PointF end)
    {
        var angle = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        if (angle < 0) angle += MathF.PI;
        if (angle >= MathF.PI) angle -= MathF.PI;
        return Math.Min(
            LineMergeDirectionBucketCount - 1,
            (int)MathF.Floor(angle / LineMergeDirectionBucketRadians));
    }

    private static int WrapLineMergeDirectionBucket(int bucket)
    {
        if (bucket < 0) return bucket + LineMergeDirectionBucketCount;
        if (bucket >= LineMergeDirectionBucketCount) return bucket - LineMergeDirectionBucketCount;
        return bucket;
    }

    private static bool LinesCanMergeGeometrically(PointF aStart, PointF aEnd, PointF bStart, PointF bEnd)
    {
        var ax = aEnd.X - aStart.X;
        var ay = aEnd.Y - aStart.Y;
        var bx = bEnd.X - bStart.X;
        var by = bEnd.Y - bStart.Y;
        var aLength = MathF.Sqrt(ax * ax + ay * ay);
        var bLength = MathF.Sqrt(bx * bx + by * by);
        if (aLength <= DrawingTopologyRules.UnitIntersectionTolerance
            || bLength <= DrawingTopologyRules.UnitIntersectionTolerance)
        {
            return false;
        }

        var normalizedCross = Math.Abs(ax * by - ay * bx) / (aLength * bLength);
        if (normalizedCross > LineMergeDirectionTolerance) return false;

        var startOffset = Math.Abs((bStart.X - aStart.X) * ay - (bStart.Y - aStart.Y) * ax) / aLength;
        var endOffset = Math.Abs((bEnd.X - aStart.X) * ay - (bEnd.Y - aStart.Y) * ax) / aLength;
        return startOffset <= LineMergeCollinearityToleranceUnits
            && endOffset <= LineMergeCollinearityToleranceUnits;
    }

    private static bool LineMergeGroupSharesBaseline(
        IReadOnlyList<int> group,
        int baselineCandidate,
        IReadOnlyList<PointF> starts,
        IReadOnlyList<PointF> ends)
    {
        var baselineStart = starts[baselineCandidate];
        var baselineEnd = ends[baselineCandidate];
        var directionX = baselineEnd.X - baselineStart.X;
        var directionY = baselineEnd.Y - baselineStart.Y;
        var directionLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
        if (directionLength <= DrawingTopologyRules.UnitIntersectionTolerance) return false;
        directionX /= directionLength;
        directionY /= directionLength;

        var minimumOffset = 0f;
        var maximumOffset = 0f;
        var minimumDirectionAngle = 0f;
        var maximumDirectionAngle = 0f;
        foreach (var candidateIndex in group)
        {
            if (candidateIndex == baselineCandidate) continue;
            var start = starts[candidateIndex];
            var end = ends[candidateIndex];
            if (!LinesCanMergeGeometrically(baselineStart, baselineEnd, start, end)) return false;

            UpdateOffset(start);
            UpdateOffset(end);

            var candidateDirectionX = end.X - start.X;
            var candidateDirectionY = end.Y - start.Y;
            var candidateLength = MathF.Sqrt(
                candidateDirectionX * candidateDirectionX + candidateDirectionY * candidateDirectionY);
            candidateDirectionX /= candidateLength;
            candidateDirectionY /= candidateLength;
            var directionDot = directionX * candidateDirectionX + directionY * candidateDirectionY;
            if (directionDot < 0)
            {
                candidateDirectionX = -candidateDirectionX;
                candidateDirectionY = -candidateDirectionY;
                directionDot = -directionDot;
            }

            var directionCross = directionX * candidateDirectionY - directionY * candidateDirectionX;
            var directionAngle = MathF.Atan2(directionCross, Math.Clamp(directionDot, -1f, 1f));
            minimumDirectionAngle = Math.Min(minimumDirectionAngle, directionAngle);
            maximumDirectionAngle = Math.Max(maximumDirectionAngle, directionAngle);
        }

        return maximumOffset - minimumOffset <= LineMergeCollinearityToleranceUnits
            && maximumDirectionAngle - minimumDirectionAngle <= MathF.Asin(LineMergeDirectionTolerance);

        void UpdateOffset(PointF point)
        {
            var offset = (point.X - baselineStart.X) * directionY
                - (point.Y - baselineStart.Y) * directionX;
            minimumOffset = Math.Min(minimumOffset, offset);
            maximumOffset = Math.Max(maximumOffset, offset);
        }
    }

    private static int FindLineMergeRoot(int[] parents, int objectIndex)
    {
        var root = objectIndex;
        while (parents[root] != root) root = parents[root];
        while (parents[objectIndex] != objectIndex)
        {
            var parent = parents[objectIndex];
            parents[objectIndex] = root;
            objectIndex = parent;
        }

        return root;
    }

    private static void UnionLineMergeRoots(int[] parents, byte[] ranks, int a, int b)
    {
        var aRoot = FindLineMergeRoot(parents, a);
        var bRoot = FindLineMergeRoot(parents, b);
        if (aRoot == bRoot) return;
        if (ranks[aRoot] < ranks[bRoot])
        {
            parents[aRoot] = bRoot;
            return;
        }

        parents[bRoot] = aRoot;
        if (ranks[aRoot] == ranks[bRoot]) ranks[aRoot]++;
    }

    private static float DistanceSquared(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private void PlanBoundaryMaterializations(
        int source,
        int frame,
        List<MaterializedPartAddition> additions,
        bool preserveSourceSubOrder)
    {
        if ((uint)source >= ObjectCount || !HasStroke(source)) return;
        var candidates = CollectTopologyCandidates(source, frame);
        var parts = BuildBoundaryStrokeParts(source, candidates);
        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = Math.Max(3u, AtomCount[source] / (uint)Math.Max(1, parts.Count));
        var subOrders = ReplacementSubOrders(source, parts.Count, preserveSourceSubOrder);
        var subOrderIndex = 0;
        foreach (var part in parts)
        {
            additions.Add(BoundaryMaterialization(
                part,
                DrawingElementKey.None,
                layer,
                order,
                subOrders[subOrderIndex++],
                stroke,
                fillColor,
                strokeColor,
                atoms));
        }
    }

    private void AddFillMarqueeParts(
        int source,
        RectangleF bounds,
        IReadOnlyList<int> activeCandidates,
        List<MarqueeMaterializedAddition> additions,
        bool[] remove,
        PointF[]? selectionPolygon = null)
    {
        if (!TrySplitFillByMarquee(
                source,
                bounds,
                out var insideContours,
                out var outsideContours,
                selectionPolygon)) return;

        var layer = ObjectLayer[source];
        var keyframeFrame = ObjectKeyframeFrame[source];
        var order = ObjectOrder[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var gradientPaint = CaptureGradientPaint(source);
        var atoms = AtomCount[source];
        var hasInsideBezierContours = TryBuildMarqueeBezierContours(
            source,
            bounds,
            insideContours,
            out var insideBezierContours,
            selectionPolygon);
        var hasOutsideBezierContours = TryBuildMarqueeBezierContours(
            source,
            bounds,
            outsideContours,
            out var outsideBezierContours,
            selectionPolygon);
        var boundaryParts = Stroke[source] > 0
            ? BuildMarqueeBoundaryParts(source, bounds, activeCandidates, selectionPolygon)
            : new List<(BoundaryStrokePart Part, bool Selected)>();
        var replacementCount = boundaryParts.Count + 2;
        var subOrders = ReplacementSubOrders(source, replacementCount);
        var subOrderIndex = 0;

        remove[source] = true;
        var boundaryAtoms = Math.Max(3u, atoms / (uint)Math.Max(1, boundaryParts.Count));
        foreach (var boundary in boundaryParts)
        {
            additions.Add(new MarqueeMaterializedAddition(
                BoundaryMaterialization(
                    boundary.Part,
                    DrawingElementKey.None,
                    layer,
                    order,
                    subOrders[subOrderIndex++],
                    Stroke[source],
                    fillColor,
                    strokeColor,
                    boundaryAtoms),
                keyframeFrame,
                boundary.Selected));
        }

        var fillAtoms = Math.Max(3u, atoms / 2);
        additions.Add(new MarqueeMaterializedAddition(
            FillMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                subOrders[subOrderIndex++],
                fillColor,
                strokeColor,
                fillAtoms,
                outsideContours,
                gradientPaint,
                fillAutoMergeProtected: FillAutoMergeProtected[source],
                bezierContours: hasOutsideBezierContours ? outsideBezierContours : null),
            keyframeFrame,
            false));
        additions.Add(new MarqueeMaterializedAddition(
            FillMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                subOrders[subOrderIndex],
                fillColor,
                strokeColor,
                fillAtoms,
                insideContours,
                gradientPaint,
                fillAutoMergeProtected: FillAutoMergeProtected[source],
                bezierContours: hasInsideBezierContours ? insideBezierContours : null),
            keyframeFrame,
            true));
    }

    private List<(BoundaryStrokePart Part, bool Selected)> BuildMarqueeBoundaryParts(
        int source,
        RectangleF bounds,
        IReadOnlyList<int> activeCandidates,
        PointF[]? selectionPolygon = null)
    {
        var result = new List<(BoundaryStrokePart Part, bool Selected)>();
        if (TryGetPathBezierWorldContours(source, out var exactContours))
        {
            var partIndex = 0;
            foreach (var exact in BuildPathBezierSegmentParts(exactContours))
            {
                var curve = new CubicBoundarySegment(
                    exact.Start,
                    exact.Control1,
                    exact.Control2,
                    exact.End);
                var curveSplits = selectionPolygon is null
                    ? CubicRectSplits(curve, bounds)
                    : CubicPolygonSplits(curve, selectionPolygon);
                foreach (var segment in BuildCurveParts(
                             curve.Start,
                             curve.Control1,
                             curve.Control2,
                             curve.End,
                             curveSplits))
                {
                    var segmentCurve = new CubicBoundarySegment(
                        segment.Start,
                        segment.Control1,
                        segment.Control2,
                        segment.End);
                    var midpoint = CubicPoint(
                        segment.Start,
                        segment.Control1,
                        segment.Control2,
                        segment.End,
                        0.5f);
                    result.Add((
                        new BoundaryStrokePart(
                            partIndex++,
                            exact.ContourIndex,
                            curveSplits[segment.PartIndex].T,
                            curveSplits[segment.PartIndex + 1].T,
                            SampleCubicSegment(segmentCurve),
                            segmentCurve),
                        IsSelectionPointInside(midpoint, bounds, selectionPolygon)));
                }
            }

            return result;
        }

        foreach (var boundary in BuildBoundaryStrokeParts(source, activeCandidates))
        {
            if (boundary.Curve is { } curve)
            {
                var curveSplits = selectionPolygon is null
                    ? CubicRectSplits(curve, bounds)
                    : CubicPolygonSplits(curve, selectionPolygon);
                var segments = BuildCurveParts(
                    curve.Start,
                    curve.Control1,
                    curve.Control2,
                    curve.End,
                    curveSplits);
                foreach (var segment in segments)
                {
                    var segmentCurve = new CubicBoundarySegment(
                        segment.Start,
                        segment.Control1,
                        segment.Control2,
                        segment.End);
                    var startT = boundary.StartT
                        + (boundary.EndT - boundary.StartT) * curveSplits[segment.PartIndex].T;
                    var endT = boundary.StartT
                        + (boundary.EndT - boundary.StartT) * curveSplits[segment.PartIndex + 1].T;
                    var part = new BoundaryStrokePart(
                        boundary.PartIndex,
                        boundary.ContourIndex,
                        startT,
                        endT,
                        SampleCubicSegment(segmentCurve),
                        segmentCurve);
                    var midpoint = CubicPoint(
                        segment.Start,
                        segment.Control1,
                        segment.Control2,
                        segment.End,
                        0.5f);
                    result.Add((part, IsSelectionPointInside(midpoint, bounds, selectionPolygon)));
                }

                continue;
            }

            var polylineSplits = selectionPolygon is null
                ? PolylineRectSplitParameters(boundary.Points, bounds)
                : PolylinePolygonSplitParameters(boundary.Points, selectionPolygon);
            foreach (var part in BuildPolylinePathParts(boundary.Points, polylineSplits))
            {
                var midpoint = PolylinePointAt(part.Points, 0.5f);
                result.Add((
                    new BoundaryStrokePart(
                        boundary.PartIndex,
                        boundary.ContourIndex,
                        boundary.StartT + (boundary.EndT - boundary.StartT) * part.StartT,
                        boundary.StartT + (boundary.EndT - boundary.StartT) * part.EndT,
                        part.Points),
                    IsSelectionPointInside(midpoint, bounds, selectionPolygon)));
            }
        }

        return result;
    }

    private bool TryBuildMarqueeBezierContours(
        int source,
        RectangleF bounds,
        PointF[][] clippedContours,
        out PathBezierNode[][] bezierContours,
        PointF[]? selectionPolygon = null)
    {
        bezierContours = [];
        if (!TryGetPathBezierWorldContours(source, out var exactContours) || clippedContours.Length == 0)
        {
            return false;
        }

        var curvePieces = new List<MarqueeBezierCurvePiece>();
        foreach (var exact in BuildPathBezierSegmentParts(exactContours))
        {
            var curve = new CubicBoundarySegment(
                exact.Start,
                exact.Control1,
                exact.Control2,
                exact.End);
            var splits = selectionPolygon is null
                ? CubicRectSplits(curve, bounds)
                : CubicPolygonSplits(curve, selectionPolygon);
            foreach (var part in BuildCurveParts(
                         curve.Start,
                         curve.Control1,
                         curve.Control2,
                         curve.End,
                         splits))
            {
                var piece = new CubicBoundarySegment(
                    part.Start,
                    part.Control1,
                    part.Control2,
                    part.End);
                curvePieces.Add(new MarqueeBezierCurvePiece(piece, SampleCubicSegment(piece)));
            }
        }

        var result = new PathBezierNode[clippedContours.Length][];
        for (var contourIndex = 0; contourIndex < clippedContours.Length; contourIndex++)
        {
            if (!TryBuildMarqueeBezierContour(clippedContours[contourIndex], curvePieces, out result[contourIndex]))
            {
                bezierContours = [];
                return false;
            }
        }

        bezierContours = result;
        return true;
    }

    private static bool TryBuildMarqueeBezierContour(
        PointF[] clippedContour,
        IReadOnlyList<MarqueeBezierCurvePiece> curvePieces,
        out PathBezierNode[] contour)
    {
        contour = [];
        var anchors = OpenPolygon(clippedContour);
        if (anchors.Length < 3) return false;

        const float matchToleranceUnits = 2f;
        var labels = Enumerable.Repeat(-1, anchors.Length).ToArray();
        for (var edgeIndex = 0; edgeIndex < anchors.Length; edgeIndex++)
        {
            var start = anchors[edgeIndex];
            var end = anchors[(edgeIndex + 1) % anchors.Length];
            var midpoint = Midpoint(start, end);
            var bestScore = float.MaxValue;
            for (var curveIndex = 0; curveIndex < curvePieces.Count; curveIndex++)
            {
                var samples = curvePieces[curveIndex].Samples;
                var startDistance = DistanceToPolyline(start, samples);
                var endDistance = DistanceToPolyline(end, samples);
                var middleDistance = DistanceToPolyline(midpoint, samples);
                var maximumDistance = Math.Max(startDistance, Math.Max(endDistance, middleDistance));
                if (maximumDistance > matchToleranceUnits) continue;
                var score = startDistance + endDistance + middleDistance;
                if (score >= bestScore) continue;
                bestScore = score;
                labels[edgeIndex] = curveIndex;
            }
        }

        var startEdge = 0;
        for (var edgeIndex = 0; edgeIndex < labels.Length; edgeIndex++)
        {
            var previous = labels[(edgeIndex - 1 + labels.Length) % labels.Length];
            if (labels[edgeIndex] >= 0 && labels[edgeIndex] == previous) continue;
            startEdge = edgeIndex;
            break;
        }

        var segments = new List<BezierContourSegment>(anchors.Length);
        var processed = 0;
        while (processed < anchors.Length)
        {
            var edgeIndex = (startEdge + processed) % anchors.Length;
            var label = labels[edgeIndex];
            var groupLength = 1;
            if (label >= 0)
            {
                while (processed + groupLength < anchors.Length
                    && labels[(edgeIndex + groupLength) % anchors.Length] == label)
                {
                    groupLength++;
                }
            }

            var start = anchors[edgeIndex];
            var end = anchors[(edgeIndex + groupLength) % anchors.Length];
            if (label >= 0
                && TryMatchMarqueeCurvePiece(
                    start,
                    end,
                    curvePieces[label].Curve,
                    matchToleranceUnits,
                    out var matchedCurve))
            {
                segments.Add(new BezierContourSegment(matchedCurve, true));
            }
            else
            {
                for (var offset = 0; offset < groupLength; offset++)
                {
                    var lineStart = anchors[(edgeIndex + offset) % anchors.Length];
                    var lineEnd = anchors[(edgeIndex + offset + 1) % anchors.Length];
                    segments.Add(new BezierContourSegment(
                        new CubicBoundarySegment(
                            lineStart,
                            Lerp(lineStart, lineEnd, 1f / 3f),
                            Lerp(lineStart, lineEnd, 2f / 3f),
                            lineEnd),
                        false));
                }
            }

            processed += groupLength;
        }

        while (segments.Count < 3)
        {
            var splitIndex = Enumerable.Range(0, segments.Count)
                .OrderByDescending(index => Distance(segments[index].Curve.Start, segments[index].Curve.End))
                .First();
            var source = segments[splitIndex];
            var first = CubicSubcurve(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                0,
                0.5f);
            var second = CubicSubcurve(
                source.Curve.Start,
                source.Curve.Control1,
                source.Curve.Control2,
                source.Curve.End,
                0.5f,
                1);
            segments[splitIndex] = new BezierContourSegment(
                new CubicBoundarySegment(first.Start, first.Control1, first.Control2, first.End),
                source.PreservesSourceCurve);
            segments.Insert(
                splitIndex + 1,
                new BezierContourSegment(
                    new CubicBoundarySegment(second.Start, second.Control1, second.Control2, second.End),
                    source.PreservesSourceCurve));
        }

        var preferredIndex = Enumerable.Range(0, segments.Count)
            .Where(index => segments[index].PreservesSourceCurve)
            .OrderByDescending(index => Math.Max(
                DistanceToSegment(
                    segments[index].Curve.Control1,
                    segments[index].Curve.Start,
                    segments[index].Curve.End),
                DistanceToSegment(
                    segments[index].Curve.Control2,
                    segments[index].Curve.Start,
                    segments[index].Curve.End)))
            .ThenByDescending(index => Distance(segments[index].Curve.Start, segments[index].Curve.End))
            .FirstOrDefault(-1);
        if (preferredIndex > 0)
        {
            segments = segments.Skip(preferredIndex).Concat(segments.Take(preferredIndex)).ToList();
        }

        contour = new PathBezierNode[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var current = segments[index].Curve;
            var previous = segments[(index - 1 + segments.Count) % segments.Count].Curve;
            contour[index] = new PathBezierNode(current.Start, previous.Control2, current.Control1);
        }

        return true;
    }

    private static bool TryMatchMarqueeCurvePiece(
        PointF start,
        PointF end,
        CubicBoundarySegment curve,
        float tolerance,
        out CubicBoundarySegment matched)
    {
        var forward = Math.Max(Distance(start, curve.Start), Distance(end, curve.End));
        var reverse = Math.Max(Distance(start, curve.End), Distance(end, curve.Start));
        if (Math.Min(forward, reverse) > tolerance)
        {
            matched = default;
            return false;
        }

        if (forward <= reverse)
        {
            var startDelta = new PointF(start.X - curve.Start.X, start.Y - curve.Start.Y);
            var endDelta = new PointF(end.X - curve.End.X, end.Y - curve.End.Y);
            matched = new CubicBoundarySegment(
                start,
                new PointF(curve.Control1.X + startDelta.X, curve.Control1.Y + startDelta.Y),
                new PointF(curve.Control2.X + endDelta.X, curve.Control2.Y + endDelta.Y),
                end);
            return true;
        }

        var reverseStartDelta = new PointF(start.X - curve.End.X, start.Y - curve.End.Y);
        var reverseEndDelta = new PointF(end.X - curve.Start.X, end.Y - curve.Start.Y);
        matched = new CubicBoundarySegment(
            start,
            new PointF(curve.Control2.X + reverseStartDelta.X, curve.Control2.Y + reverseStartDelta.Y),
            new PointF(curve.Control1.X + reverseEndDelta.X, curve.Control1.Y + reverseEndDelta.Y),
            end);
        return true;
    }

    private static bool IsSelectionPointInside(
        PointF point,
        RectangleF bounds,
        PointF[]? selectionPolygon)
    {
        return selectionPolygon is null
            ? PointInRectangle(point, bounds)
            : PointInPolygonOrOnBoundary(point, selectionPolygon);
    }

    private List<DrawingTopologySplit> CubicPolygonSplits(
        CubicBoundarySegment curve,
        PointF[] polygon)
    {
        var samples = SampleCubicSegmentWithParameters(curve);
        var splits = new List<DrawingTopologySplit>
        {
            new(0, curve.Start),
            new(1, curve.End)
        };
        for (var sampleIndex = 0; sampleIndex < samples.Length - 1; sampleIndex++)
        {
            var first = samples[sampleIndex];
            var second = samples[sampleIndex + 1];
            for (var edgeIndex = 0; edgeIndex < polygon.Length; edgeIndex++)
            {
                var edgeStart = polygon[edgeIndex];
                var edgeEnd = polygon[(edgeIndex + 1) % polygon.Length];
                var count = SegmentIntersectionParameters(
                    first.Point,
                    second.Point,
                    edgeStart,
                    edgeEnd,
                    out var localT,
                    out var secondLocalT);
                if (count <= 0) continue;
                AddSplit(first, second, localT);
                if (count > 1) AddSplit(first, second, secondLocalT);
            }
        }

        return NormalizeStrokeSplits(splits);

        void AddSplit(CurveSample firstSample, CurveSample secondSample, float localT)
        {
            var clamped = Math.Clamp(localT, 0, 1);
            var globalT = firstSample.T + (secondSample.T - firstSample.T) * clamped;
            if (globalT <= 0.0001f || globalT >= 0.9999f) return;
            splits.Add(new DrawingTopologySplit(globalT, Lerp(firstSample.Point, secondSample.Point, clamped)));
        }
    }

    private static List<float> PolylinePolygonSplitParameters(
        PointF[] points,
        PointF[] polygon)
    {
        var splits = new List<float> { 0, 1 };
        var segmentCount = points.Length - 1;
        if (segmentCount <= 0) return splits;

        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var start = points[segmentIndex];
            var end = points[segmentIndex + 1];
            for (var edgeIndex = 0; edgeIndex < polygon.Length; edgeIndex++)
            {
                var edgeStart = polygon[edgeIndex];
                var edgeEnd = polygon[(edgeIndex + 1) % polygon.Length];
                var count = SegmentIntersectionParameters(
                    start,
                    end,
                    edgeStart,
                    edgeEnd,
                    out var localT,
                    out var secondLocalT);
                if (count <= 0) continue;
                AddSplit(segmentIndex, localT);
                if (count > 1) AddSplit(segmentIndex, secondLocalT);
            }
        }

        splits.Sort();
        var write = 1;
        for (var read = 1; read < splits.Count; read++)
        {
            if (splits[read] - splits[write - 1] <= 0.0001f) continue;
            splits[write++] = splits[read];
        }

        if (write < splits.Count) splits.RemoveRange(write, splits.Count - write);
        return splits;

        void AddSplit(int currentSegment, float segmentParameter)
        {
            var parameter = (currentSegment + Math.Clamp(segmentParameter, 0, 1)) / segmentCount;
            if (parameter <= 0.0001f || parameter >= 0.9999f) return;
            splits.Add(parameter);
        }
    }

    private bool MixingComponentIntersectsSelection(
        int source,
        MixingBrushRegionData component,
        RectangleF bounds,
        PointF[]? selectionPolygon)
    {
        if (selectionPolygon is null)
        {
            return component.IntersectsBounds(
                bounds,
                point => LocalToWorld(source, point.X, point.Y));
        }

        var localContours = component.GetBoundaryContours();
        if (localContours.Length == 0) return false;
        var worldContours = localContours
            .Select(contour => contour
                .Select(point => LocalToWorld(source, point.X, point.Y))
                .ToArray())
            .ToArray();
        return ContoursIntersectPolygon(worldContours, selectionPolygon);
    }

    private bool MixingStrokeIntersectsSelection(
        int source,
        RectangleF bounds,
        PointF[]? selectionPolygon)
    {
        if (selectionPolygon is null) return MixingStrokeIntersectsBounds(source, bounds);
        if (!_mixingStrokeLocalSamples.TryGetValue(source, out var samples)) return false;

        foreach (var sample in samples)
        {
            if (((uint)sample.Argb >> 24) == 0
                || !float.IsFinite(sample.Point.X)
                || !float.IsFinite(sample.Point.Y)
                || !float.IsFinite(sample.Diameter)
                || sample.Diameter <= 0)
            {
                continue;
            }

            var center = LocalToWorld(source, sample.Point.X, sample.Point.Y);
            var radius = sample.Diameter * 0.5f;
            if (PointInPolygonOrOnBoundary(center, selectionPolygon)
                || selectionPolygon.Any(point => Distance(point, center) <= radius)
                || PolygonBoundaryDistance(center, selectionPolygon) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContoursIntersectPolygon(
        PointF[][] contours,
        PointF[] polygon)
    {
        foreach (var contour in contours)
        {
            if (contour.Length == 0) continue;
            if (contour.Any(point => PointInPolygonOrOnBoundary(point, polygon))) return true;
            for (var edgeIndex = 0; edgeIndex < contour.Length; edgeIndex++)
            {
                var start = contour[edgeIndex];
                var end = contour[(edgeIndex + 1) % contour.Length];
                for (var polygonEdge = 0; polygonEdge < polygon.Length; polygonEdge++)
                {
                    if (SegmentIntersectionParameters(
                            start,
                            end,
                            polygon[polygonEdge],
                            polygon[(polygonEdge + 1) % polygon.Length],
                            out _,
                            out _) > 0)
                    {
                        return true;
                    }
                }
            }
        }

        return polygon.Any(point => PointInCompoundPolygon(point, contours));
    }

    private static float PolygonBoundaryDistance(PointF point, PointF[] polygon)
    {
        var distance = float.MaxValue;
        for (var index = 0; index < polygon.Length; index++)
        {
            distance = Math.Min(
                distance,
                DistanceToSegment(point, polygon[index], polygon[(index + 1) % polygon.Length]));
        }

        return distance;
    }

    private bool TrySplitFillByMarquee(
        int index,
        RectangleF bounds,
        out PointF[][] insideContours,
        out PointF[][] outsideContours,
        PointF[]? selectionPolygon = null)
    {
        insideContours = Array.Empty<PointF[]>();
        outsideContours = Array.Empty<PointF[]>();
        try
        {
            var source = ToClipperPaths(FillWorldContours(index));
            var marquee = selectionPolygon is null
                ? ToClipperPaths(new[]
                {
                    new[]
                    {
                        new PointF(bounds.Left, bounds.Top),
                        new PointF(bounds.Right, bounds.Top),
                        new PointF(bounds.Right, bounds.Bottom),
                        new PointF(bounds.Left, bounds.Bottom)
                    }
                })
                : ToClipperPaths(new[] { selectionPolygon });
            if (source.Count == 0 || marquee.Count != 1) return false;

            var intersection = new Clipper64();
            intersection.AddSubject(source);
            intersection.AddClip(marquee);
            var inside = new Paths64();
            if (!intersection.Execute(ClipType.Intersection, FillRule.EvenOdd, inside) || inside.Count == 0) return false;

            var difference = new Clipper64();
            difference.AddSubject(source);
            difference.AddClip(marquee);
            var outside = new Paths64();
            if (!difference.Execute(ClipType.Difference, FillRule.EvenOdd, outside) || outside.Count == 0) return false;

            insideContours = FromClipperPaths(inside);
            outsideContours = FromClipperPaths(outside);
            return insideContours.Length > 0 && outsideContours.Length > 0;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }
    }

    private void AddLineMarqueeParts(
        int source,
        RectangleF bounds,
        List<MarqueeMaterializedAddition> additions,
        bool[] remove,
        PointF[]? selectionPolygon = null)
    {
        var curve = LineCurve(source);
        var splits = selectionPolygon is null
            ? LineRectSplitParameters(source, bounds)
            : CubicPolygonSplits(curve, selectionPolygon).Select(split => split.T).ToList();
        if (splits.Count <= 2) return;

        var segments = BuildCurveParts(curve.Start, curve.Control1, curve.Control2, curve.End, splits);
        if (segments.Count <= 1) return;

        var selected = segments
            .Select(segment => IsSelectionPointInside(
                CubicPoint(segment.Start, segment.Control1, segment.Control2, segment.End, 0.5f),
                bounds,
                selectionPolygon))
            .ToArray();
        if (!selected.Any(value => value)) return;

        remove[source] = true;
        var subOrders = ReplacementSubOrders(source, segments.Count);
        var gradientPaint = CaptureGradientPaint(source);
        var atomsPerPart = Math.Max(3u, AtomCount[source] / (uint)segments.Count);
        for (var part = 0; part < segments.Count; part++)
        {
            var segment = segments[part];
            additions.Add(new MarqueeMaterializedAddition(
                CurveMaterialization(
                    DrawingElementKey.None,
                    ObjectLayer[source],
                    ObjectOrder[source],
                    subOrders[part],
                    Stroke[source],
                    Color.FromArgb(Argb[source]),
                    Color.FromArgb(StrokeArgb[source]),
                    atomsPerPart,
                    segment.Start,
                    segment.Control1,
                    segment.Control2,
                    segment.End,
                    segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                    segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round)
                with { GradientPaint = gradientPaint },
                ObjectKeyframeFrame[source],
                selected[part]));
        }
    }

    private bool TryAddFreehandMarqueeParts(
        int source,
        RectangleF bounds,
        List<MarqueeMaterializedAddition> additions,
        bool[] remove,
        PointF[]? selectionPolygon = null)
    {
        if ((uint)source >= ObjectCount
            || ShapeKind[source] != VectorAnimationEngine.ShapeKind.Freeform
            || !HasStroke(source))
        {
            return false;
        }

        if (!_freehandBezierLocalNodes.ContainsKey(source))
        {
            return TryAddLegacyFreehandMarqueeParts(source, bounds, additions, remove, selectionPolygon);
        }

        if (!TryGetFreehandBezierWorldNodes(source, out var nodes) || nodes.Length < 2) return false;

        var intervals = new List<MarqueeOpenBezierInterval>();
        for (var segmentIndex = 0; segmentIndex < nodes.Length - 1; segmentIndex++)
        {
            var current = nodes[segmentIndex];
            var next = nodes[segmentIndex + 1];
            var curve = new CubicBoundarySegment(
                current.Anchor,
                current.OutgoingControl,
                next.IncomingControl,
                next.Anchor);
            var splits = selectionPolygon is null
                ? CubicRectSplits(curve, bounds)
                : CubicPolygonSplits(curve, selectionPolygon);
            for (var splitIndex = 0; splitIndex < splits.Count - 1; splitIndex++)
            {
                var startT = splits[splitIndex].T;
                var endT = splits[splitIndex + 1].T;
                if (endT - startT <= 0.0001f) continue;
                var start = new OpenBezierCut(
                    segmentIndex,
                    startT,
                    CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, startT));
                var end = new OpenBezierCut(
                    segmentIndex,
                    endT,
                    CubicPoint(curve.Start, curve.Control1, curve.Control2, curve.End, endT));
                var selected = IsSelectionPointInside(
                    CubicPoint(
                        curve.Start,
                        curve.Control1,
                        curve.Control2,
                        curve.End,
                        (startT + endT) * 0.5f),
                    bounds,
                    selectionPolygon);
                if (intervals.Count > 0
                    && intervals[^1].Selected == selected
                    && Math.Abs(intervals[^1].End.PathT - start.PathT) <= 0.0001f)
                {
                    intervals[^1] = intervals[^1] with { End = end };
                }
                else
                {
                    intervals.Add(new MarqueeOpenBezierInterval(start, end, selected));
                }
            }
        }

        if (intervals.Count <= 1
            || !intervals.Any(interval => interval.Selected)
            || !intervals.Any(interval => !interval.Selected))
        {
            return false;
        }

        var planned = new List<MarqueeMaterializedAddition>(intervals.Count);
        var subOrders = ReplacementSubOrders(source, intervals.Count);
        var atomsPerRun = Math.Max(3u, AtomCount[source] / (uint)intervals.Count);
        for (var intervalIndex = 0; intervalIndex < intervals.Count; intervalIndex++)
        {
            var interval = intervals[intervalIndex];
            if (!TryCreateOpenBezierRunNodes(
                    nodes,
                    interval.Start,
                    interval.End,
                    out var runNodes))
            {
                return false;
            }

            planned.Add(new MarqueeMaterializedAddition(
                FreehandBezierMaterialization(
                    DrawingElementKey.None,
                    ObjectLayer[source],
                    ObjectOrder[source],
                    subOrders[intervalIndex],
                    Stroke[source],
                    Color.FromArgb(Argb[source]),
                    Color.FromArgb(StrokeArgb[source]),
                    atomsPerRun,
                    runNodes),
                ObjectKeyframeFrame[source],
                interval.Selected));
        }

        additions.AddRange(planned);
        remove[source] = true;
        return true;
    }

    private bool TryAddLegacyFreehandMarqueeParts(
        int source,
        RectangleF bounds,
        List<MarqueeMaterializedAddition> additions,
        bool[] remove,
        PointF[]? selectionPolygon = null)
    {
        if (!TryGetFreehandWorldPoints(source, out var points) || points.Length < 2) return false;
        var splits = selectionPolygon is null
            ? PolylineRectSplitParameters(points, bounds)
            : PolylinePolygonSplitParameters(points, selectionPolygon);
        if (splits.Count <= 2) return false;

        var runs = BuildPolylinePathParts(points, splits);
        if (runs.Count <= 1) return false;
        var selected = runs
            .Select(run => IsSelectionPointInside(
                PolylinePointAt(run.Points, 0.5f),
                bounds,
                selectionPolygon))
            .ToArray();
        if (!selected.Any(value => value) || !selected.Any(value => !value)) return false;

        var subOrders = ReplacementSubOrders(source, runs.Count);
        var atomsPerRun = Math.Max(3u, AtomCount[source] / (uint)runs.Count);
        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            additions.Add(new MarqueeMaterializedAddition(
                FreehandMaterialization(
                    DrawingElementKey.None,
                    ObjectLayer[source],
                    ObjectOrder[source],
                    subOrders[runIndex],
                    Stroke[source],
                    Color.FromArgb(Argb[source]),
                    Color.FromArgb(StrokeArgb[source]),
                    atomsPerRun,
                    runs[runIndex].Points),
                ObjectKeyframeFrame[source],
                selected[runIndex]));
        }

        remove[source] = true;
        return true;
    }

}
