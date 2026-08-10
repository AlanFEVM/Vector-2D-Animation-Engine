using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public VectorSceneSnapshot CreateSnapshot() => CreateSnapshot(cloneGeometry: true);

    internal VectorSceneSnapshot CreateWholeObjectTranslationSnapshot()
        => CreateSnapshot(cloneGeometry: false);

    private VectorSceneSnapshot CreateSnapshot(bool cloneGeometry)
    {
        return new VectorSceneSnapshot
        {
            LayerCount = LayerCount,
            ObjectCount = ObjectCount,
            VirtualAtomCount = VirtualAtomCount,
            NextObjectOrder = _nextObjectOrder,
            ActiveLayer = ActiveLayer,
            MaxHalfExtent = MaxHalfExtent,
            LayerIds = LayerIds.ToArray(),
            LayerNames = LayerNames.ToArray(),
            LayerKinds = LayerKinds.ToArray(),
            LayerParentIds = LayerParentIds.ToArray(),
            LayerMaskIds = LayerMaskIds.ToArray(),
            LayerLocked = LayerLocked.ToArray(),
            LayerVisible = LayerVisible.ToArray(),
            LayerOpacity = LayerOpacity.ToArray(),
            LayerBlendModes = LayerBlendModes.ToArray(),
            LayerColorArgb = LayerColorArgb.ToArray(),
            LayerOutline = LayerOutline.ToArray(),
            OnionSkinEnabled = OnionSkinEnabled,
            LayerOnionSkin = Enumerable.Repeat(OnionSkinEnabled, LayerCount).ToArray(),
            OnionSkinPreviousFrames = OnionSkinPreviousFrames,
            OnionSkinNextFrames = OnionSkinNextFrames,
            LayerStart = LayerStart.ToArray(),
            LayerEnd = LayerEnd.ToArray(),
            ObjectLayer = ObjectLayer[..ObjectCount],
            ObjectKeyframeFrame = ObjectKeyframeFrame[..ObjectCount],
            ObjectOrder = ObjectOrder[..ObjectCount],
            ObjectSubOrder = ObjectSubOrder[..ObjectCount],
            X = X[..ObjectCount],
            Y = Y[..ObjectCount],
            Width = Width[..ObjectCount],
            Height = Height[..ObjectCount],
            Angle = Angle[..ObjectCount],
            Stroke = Stroke[..ObjectCount],
            CurveControlX = CurveControlX[..ObjectCount],
            CurveControlY = CurveControlY[..ObjectCount],
            CurveControl2X = CurveControl2X[..ObjectCount],
            CurveControl2Y = CurveControl2Y[..ObjectCount],
            LineEndpointStyles = LineEndpointStyles[..ObjectCount],
            LineEndEndpointStyles = LineEndEndpointStyles[..ObjectCount],
            ShapeKind = ShapeKind[..ObjectCount],
            ShapeVertexCounts = ShapeVertexCounts[..ObjectCount],
            AtomCount = AtomCount[..ObjectCount],
            Argb = Argb[..ObjectCount],
            StrokeArgb = StrokeArgb[..ObjectCount],
            FillAutoMergeProtected = FillAutoMergeProtected[..ObjectCount],
            LinearGradientEnabled = LinearGradientEnabled[..ObjectCount],
            GradientKinds = GradientKinds[..ObjectCount],
            GradientStartArgb = GradientStartArgb[..ObjectCount],
            GradientEndArgb = GradientEndArgb[..ObjectCount],
            GradientStartX = GradientStartX[..ObjectCount],
            GradientStartY = GradientStartY[..ObjectCount],
            GradientEndX = GradientEndX[..ObjectCount],
            GradientEndY = GradientEndY[..ObjectCount],
            GradientStops = _gradientStops.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.ToArray() : item.Value),
            GradientPathLocalPoints = _gradientPathLocalPoints.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.ToArray() : item.Value),
            ShapeGradientMappingLocalContours = _shapeGradientMappingLocalContours.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? CloneContours(item.Value) : item.Value),
            Timeline = Timeline.CreateSnapshot(),
            PathLocalContours = _pathLocalContours.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? CloneContours(item.Value) : item.Value),
            PathBezierLocalContours = _pathBezierLocalContours.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? CloneBezierContours(item.Value) : item.Value),
            FreehandLocalPoints = _freehandLocalPoints.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.ToArray() : item.Value),
            FreehandBezierLocalNodes = _freehandBezierLocalNodes.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.ToArray() : item.Value),
            MixingStrokeLocalSamples = _mixingStrokeLocalSamples.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.ToArray() : item.Value),
            MixingStrokeLocalRegions = _mixingStrokeLocalRegions.ToDictionary(
                item => item.Key,
                item => cloneGeometry ? item.Value.DeepClone() : item.Value),
            ImportedSvgSources = _importedSvgSources.ToDictionary(item => item.Key, item => item.Value),
            ImportedSvgNames = _importedSvgNames.ToDictionary(item => item.Key, item => item.Value),
            TextObjects = _textObjects.ToDictionary(item => item.Key, item => item.Value),
            ObjectDistortions = _objectDistortions.ToDictionary(
                item => item.Key,
                item => CloneDistortions(item.Value))
        };
    }

    public void RestoreSnapshot(VectorSceneSnapshot snapshot)
    {
        ValidateImportedSvgSnapshotPayload(snapshot);
        ValidateTextSnapshotPayload(snapshot);
        ValidateMixingStrokeSnapshotPayload(snapshot);
        ValidateObjectDistortionSnapshotPayload(snapshot);
        InvalidateQueryActiveKeyframes();
        LayerCount = snapshot.LayerCount;
        ObjectCount = snapshot.ObjectCount;
        VirtualAtomCount = snapshot.VirtualAtomCount;
        _nextObjectOrder = snapshot.NextObjectOrder;
        ActiveLayer = Math.Clamp(snapshot.ActiveLayer, 0, Math.Max(0, snapshot.LayerCount - 1));
        MaxHalfExtent = snapshot.MaxHalfExtent;
        LayerIds = NormalizeStableIds(snapshot.LayerIds, LayerCount);
        LayerNames = snapshot.LayerNames.ToArray();
        LayerKinds = NormalizeLayerKinds(snapshot.LayerKinds, LayerCount);
        LayerParentIds = NormalizeLayerParentIds(snapshot.LayerParentIds, LayerIds, LayerKinds);
        LayerMaskIds = NormalizeLayerMaskIds(snapshot.LayerMaskIds, LayerIds, LayerKinds);
        NormalizeMaskedLayerParents(LayerParentIds, LayerIds, LayerMaskIds);
        LayerLocked = NormalizeLayerLocked(snapshot.LayerLocked, LayerCount);
        LayerVisible = snapshot.LayerVisible.ToArray();
        LayerOpacity = snapshot.LayerOpacity.ToArray();
        LayerBlendModes = NormalizeLayerBlendModes(snapshot.LayerBlendModes, LayerCount);
        LayerColorArgb = NormalizeLayerColors(snapshot.LayerColorArgb, LayerCount);
        LayerOutline = NormalizeLayerOutline(snapshot.LayerOutline, LayerCount);
        OnionSkinEnabled = snapshot.OnionSkinEnabled
            ?? snapshot.LayerOnionSkin.Any(enabled => enabled);
        OnionSkinPreviousFrames = NormalizeOnionSkinFrames(snapshot.OnionSkinPreviousFrames, DefaultOnionSkinPreviousFrames);
        OnionSkinNextFrames = NormalizeOnionSkinFrames(snapshot.OnionSkinNextFrames, DefaultOnionSkinNextFrames);
        LayerStart = snapshot.LayerStart.ToArray();
        LayerEnd = snapshot.LayerEnd.ToArray();
        ObjectLayer = snapshot.ObjectLayer.ToArray();
        ObjectKeyframeFrame = snapshot.ObjectKeyframeFrame.Length == ObjectCount
            ? snapshot.ObjectKeyframeFrame.ToArray()
            : Enumerable.Range(0, ObjectCount)
                .Select(index => ObjectLayer.Length > index && LayerStart.Length > ObjectLayer[index]
                    ? LayerStart[ObjectLayer[index]]
                    : 0)
                .ToArray();
        ObjectOrder = snapshot.ObjectOrder.ToArray();
        ObjectSubOrder = snapshot.ObjectSubOrder.Length == ObjectCount
            ? snapshot.ObjectSubOrder.ToArray()
            : new double[ObjectCount];
        X = snapshot.X.ToArray();
        Y = snapshot.Y.ToArray();
        Width = snapshot.Width.ToArray();
        Height = snapshot.Height.ToArray();
        Angle = snapshot.Angle.ToArray();
        Stroke = snapshot.Stroke.ToArray();
        CurveControlX = snapshot.CurveControlX.ToArray();
        CurveControlY = snapshot.CurveControlY.ToArray();
        CurveControl2X = snapshot.CurveControl2X.Length == ObjectCount
            ? snapshot.CurveControl2X.ToArray()
            : new float[ObjectCount];
        CurveControl2Y = snapshot.CurveControl2Y.Length == ObjectCount
            ? snapshot.CurveControl2Y.ToArray()
            : new float[ObjectCount];
        LineEndpointStyles = snapshot.LineEndpointStyles.Length == ObjectCount
            ? snapshot.LineEndpointStyles.Select(NormalizeLineEndpointStyle).ToArray()
            : Enumerable.Repeat(LineEndpointStyle.Round, ObjectCount).ToArray();
        LineEndEndpointStyles = snapshot.LineEndEndpointStyles.Length == ObjectCount
            ? snapshot.LineEndEndpointStyles.Select(NormalizeLineEndpointStyle).ToArray()
            : LineEndpointStyles.ToArray();
        ShapeKind = snapshot.ShapeKind.ToArray();
        if (snapshot.CurveControl2X.Length != ObjectCount || snapshot.CurveControl2Y.Length != ObjectCount)
        {
            for (var index = 0; index < ObjectCount; index++)
            {
                if (ShapeKind[index] != VectorAnimationEngine.ShapeKind.Line)
                {
                    CurveControl2X[index] = CurveControlX[index];
                    CurveControl2Y[index] = CurveControlY[index];
                    continue;
                }

                var halfWidth = Width[index] * 0.5f;
                var start = LocalToWorld(index, -halfWidth, 0);
                var end = LocalToWorld(index, halfWidth, 0);
                var quadratic = new PointF(CurveControlX[index], CurveControlY[index]);
                var control1 = Lerp(start, quadratic, 2f / 3f);
                var control2 = Lerp(end, quadratic, 2f / 3f);
                CurveControlX[index] = VectorUnits.Quantize(control1.X);
                CurveControlY[index] = VectorUnits.Quantize(control1.Y);
                CurveControl2X[index] = VectorUnits.Quantize(control2.X);
                CurveControl2Y[index] = VectorUnits.Quantize(control2.Y);
            }
        }
        ShapeVertexCounts = snapshot.ShapeVertexCounts.Length == ObjectCount
            ? snapshot.ShapeVertexCounts
                .Select((value, index) => NormalizeShapeVertexCount(ShapeKind[index], value))
                .ToArray()
            : ShapeKind.Select(DefaultShapeVertexCount).ToArray();
        AtomCount = snapshot.AtomCount.ToArray();
        Argb = snapshot.Argb.ToArray();
        StrokeArgb = snapshot.StrokeArgb.ToArray();
        FillAutoMergeProtected = snapshot.FillAutoMergeProtected.Length == ObjectCount
            ? snapshot.FillAutoMergeProtected.ToArray()
            : new bool[ObjectCount];
        LinearGradientEnabled = snapshot.LinearGradientEnabled.Length == ObjectCount
            ? snapshot.LinearGradientEnabled.ToArray()
            : new bool[ObjectCount];
        GradientKinds = snapshot.GradientKinds.Length == ObjectCount
            ? snapshot.GradientKinds.Select(kind => Enum.IsDefined(kind) ? kind : GradientKind.Solid).ToArray()
            : LinearGradientEnabled.Select(enabled => enabled ? GradientKind.Linear : GradientKind.Solid).ToArray();
        GradientStartArgb = snapshot.GradientStartArgb.Length == ObjectCount
            ? snapshot.GradientStartArgb.ToArray()
            : Argb.ToArray();
        GradientEndArgb = snapshot.GradientEndArgb.Length == ObjectCount
            ? snapshot.GradientEndArgb.ToArray()
            : Argb.ToArray();
        GradientStartX = snapshot.GradientStartX.Length == ObjectCount
            ? snapshot.GradientStartX.ToArray()
            : X.Select((value, index) => value - Width[index] * 0.5f).ToArray();
        GradientStartY = snapshot.GradientStartY.Length == ObjectCount
            ? snapshot.GradientStartY.ToArray()
            : Y.ToArray();
        GradientEndX = snapshot.GradientEndX.Length == ObjectCount
            ? snapshot.GradientEndX.ToArray()
            : X.Select((value, index) => value + Width[index] * 0.5f).ToArray();
        GradientEndY = snapshot.GradientEndY.Length == ObjectCount
            ? snapshot.GradientEndY.ToArray()
            : Y.ToArray();
        _gradientStops.Clear();
        foreach (var item in snapshot.GradientStops)
        {
            if ((uint)item.Key >= ObjectCount || GradientKinds[item.Key] == GradientKind.Solid || item.Value.Length == 0) continue;
            _gradientStops[item.Key] = NormalizeGradientStops(item.Value);
        }
        _gradientPathLocalPoints.Clear();
        foreach (var item in snapshot.GradientPathLocalPoints)
        {
            if ((uint)item.Key >= ObjectCount
                || ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.Path
                || GradientKinds[item.Key] != GradientKind.Linear
                || item.Value.Length < 2)
            {
                continue;
            }

            SetGradientPath(
                item.Key,
                item.Value.Select(point => new PointF(
                    X[item.Key] + point.X,
                    Y[item.Key] + point.Y)).ToArray());
        }
        _shapeGradientMappingLocalContours.Clear();
        foreach (var item in snapshot.ShapeGradientMappingLocalContours)
        {
            if ((uint)item.Key >= ObjectCount
                || GradientKinds[item.Key] != GradientKind.ShapeRadial
                || item.Value.Length == 0)
            {
                continue;
            }

            _shapeGradientMappingLocalContours[item.Key] = CloneContours(item.Value);
        }
        _pathLocalContours.Clear();
        foreach (var item in snapshot.PathLocalContours)
        {
            if ((uint)item.Key >= ObjectCount) continue;
            _pathLocalContours[item.Key] = CloneContours(item.Value);
        }

        _pathBezierLocalContours.Clear();
        foreach (var item in snapshot.PathBezierLocalContours)
        {
            if ((uint)item.Key >= ObjectCount
                || ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.Path
                || !TryNormalizeLocalPathBezierContours(item.Value, out var exactContours, out var sampledContours))
            {
                continue;
            }

            _pathBezierLocalContours[item.Key] = exactContours;
            _pathLocalContours[item.Key] = sampledContours;
        }

        _freehandLocalPoints.Clear();
        foreach (var item in snapshot.FreehandLocalPoints)
        {
            if ((uint)item.Key >= ObjectCount) continue;
            _freehandLocalPoints[item.Key] = item.Value.ToArray();
        }

        _freehandBezierLocalNodes.Clear();
        _legacyFreehandBezierNodeCache.Clear();
        foreach (var item in snapshot.FreehandBezierLocalNodes)
        {
            if ((uint)item.Key >= ObjectCount
                || ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.Freeform
                || !_freehandLocalPoints.ContainsKey(item.Key)
                || !TryPrepareFreehandBezierNodes(item.Value, out var exactNodes, out var sampledPoints, out _))
            {
                continue;
            }

            _freehandBezierLocalNodes[item.Key] = exactNodes;
            _freehandLocalPoints[item.Key] = sampledPoints;
        }

        _mixingStrokeLocalSamples.Clear();
        foreach (var item in snapshot.MixingStrokeLocalSamples)
        {
            if ((uint)item.Key >= ObjectCount
                || ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.MixingStroke)
            {
                continue;
            }

            var samples = NormalizeMixingStrokeSamples(item.Value);
            if (samples.Length > 0) _mixingStrokeLocalSamples[item.Key] = samples;
        }

        _mixingStrokeLocalRegions.Clear();
        foreach (var item in snapshot.MixingStrokeLocalRegions)
        {
            if ((uint)item.Key >= ObjectCount
                || ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.MixingStroke
                || !MixingBrushRegionData.TryNormalize(
                    item.Value.Vertices,
                    item.Value.TriangleIndices,
                    out var region))
            {
                continue;
            }

            _mixingStrokeLocalRegions[item.Key] = region;
        }

        _importedSvgSources.Clear();
        foreach (var item in snapshot.ImportedSvgSources)
        {
            _importedSvgSources[item.Key] = item.Value;
        }

        _importedSvgNames.Clear();
        foreach (var item in snapshot.ImportedSvgNames)
        {
            _importedSvgNames[item.Key] = item.Value;
        }

        _textObjects.Clear();
        foreach (var item in snapshot.TextObjects)
        {
            _textObjects[item.Key] = item.Value;
        }

        _objectDistortions.Clear();
        foreach (var item in snapshot.ObjectDistortions)
        {
            _objectDistortions[item.Key] = CloneDistortions(item.Value);
        }

        if (snapshot.Timeline is null)
        {
            InitializeTimelineFromLayerExposure();
        }
        else
        {
            Timeline.RestoreSnapshot(snapshot.Timeline);
            SynchronizeTimelineTracks();
            SynchronizeAllKeyframeContentKinds();
            RefreshTimelineTweenMaterializations();
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private static void ValidateObjectDistortionSnapshotPayload(VectorSceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ObjectDistortions is null)
        {
            throw new InvalidOperationException("The scene snapshot is missing object distortion metadata.");
        }

        foreach (var (objectIndex, distortions) in snapshot.ObjectDistortions)
        {
            if ((uint)objectIndex >= snapshot.ObjectCount
                || distortions is null
                || distortions.Length is 0 or > MaximumObjectDistortions
                || distortions.Any(distortion => !distortion.IsValid))
            {
                throw new InvalidOperationException("The scene snapshot contains invalid object distortion metadata.");
            }
        }
    }

    private static void ValidateImportedSvgSnapshotPayload(VectorSceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ImportedSvgSources is null || snapshot.ImportedSvgNames is null)
        {
            throw new InvalidOperationException("The scene snapshot is missing imported SVG source metadata.");
        }


        foreach (var item in snapshot.ImportedSvgNames)
        {
            if ((uint)item.Key >= snapshot.ObjectCount
                || item.Key >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.ImportedSvg
                || string.IsNullOrWhiteSpace(item.Value)
                || item.Value.Length > 80)
            {
                throw new InvalidOperationException("The scene snapshot contains invalid imported SVG name metadata.");
            }
        }

        foreach (var item in snapshot.ImportedSvgSources)
        {
            if ((uint)item.Key >= snapshot.ObjectCount
                || item.Key >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.ImportedSvg
                || string.IsNullOrWhiteSpace(item.Value))
            {
                throw new InvalidOperationException("The scene snapshot contains invalid imported SVG payload.");
            }
        }

        for (var index = 0; index < Math.Min(snapshot.ObjectCount, snapshot.ShapeKind.Length); index++)
        {
            if (snapshot.ShapeKind[index] == VectorAnimationEngine.ShapeKind.ImportedSvg
                && !snapshot.ImportedSvgSources.ContainsKey(index))
            {
                throw new InvalidOperationException("The scene snapshot is missing imported SVG payload.");
            }
        }
    }

    private static void ValidateTextSnapshotPayload(VectorSceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TextObjects is null)
        {
            throw new InvalidOperationException("The scene snapshot is missing editable text metadata.");
        }

        foreach (var item in snapshot.TextObjects)
        {
            if ((uint)item.Key >= snapshot.ObjectCount
                || item.Key >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[item.Key] != VectorAnimationEngine.ShapeKind.Text
                || !TextGeometry.IsValidStoredData(item.Value))
            {
                throw new InvalidOperationException("The scene snapshot contains invalid editable text payload.");
            }
        }

        for (var index = 0; index < Math.Min(snapshot.ObjectCount, snapshot.ShapeKind.Length); index++)
        {
            if (snapshot.ShapeKind[index] == VectorAnimationEngine.ShapeKind.Text
                && !snapshot.TextObjects.ContainsKey(index))
            {
                throw new InvalidOperationException("The scene snapshot is missing editable text payload.");
            }
        }
    }

    private static void ValidateMixingStrokeSnapshotPayload(VectorSceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MixingStrokeLocalSamples is null
            || snapshot.MixingStrokeLocalRegions is null)
        {
            throw new InvalidOperationException("The scene snapshot is missing mixing-stroke metadata.");
        }

        foreach (var (objectIndex, samples) in snapshot.MixingStrokeLocalSamples)
        {
            if ((uint)objectIndex >= snapshot.ObjectCount
                || objectIndex >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke
                || samples is null
                || samples.Length == 0
                || samples.Any(sample => !float.IsFinite(sample.Point.X)
                    || !float.IsFinite(sample.Point.Y)
                    || !float.IsFinite(sample.Diameter)
                    || sample.Diameter <= 0))
            {
                throw new InvalidOperationException("The scene snapshot contains invalid mixing-stroke payload.");
            }
        }

        foreach (var (objectIndex, region) in snapshot.MixingStrokeLocalRegions)
        {
            if ((uint)objectIndex >= snapshot.ObjectCount
                || objectIndex >= snapshot.ShapeKind.Length
                || snapshot.ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.MixingStroke
                || region is null
                || !MixingBrushRegionData.TryNormalize(
                    region.Vertices,
                    region.TriangleIndices,
                    out _)
                || snapshot.MixingStrokeLocalSamples.ContainsKey(objectIndex))
            {
                throw new InvalidOperationException("The scene snapshot contains invalid mixing-stroke region payload.");
            }
        }

        for (var index = 0; index < Math.Min(snapshot.ObjectCount, snapshot.ShapeKind.Length); index++)
        {
            if (snapshot.ShapeKind[index] == VectorAnimationEngine.ShapeKind.MixingStroke
                && snapshot.MixingStrokeLocalSamples.ContainsKey(index)
                    == snapshot.MixingStrokeLocalRegions.ContainsKey(index))
            {
                throw new InvalidOperationException("The scene snapshot is missing mixing-stroke payload.");
            }
            if (snapshot.ShapeKind[index] != VectorAnimationEngine.ShapeKind.MixingStroke
                && (snapshot.MixingStrokeLocalSamples.ContainsKey(index)
                    || snapshot.MixingStrokeLocalRegions.ContainsKey(index)))
            {
                throw new InvalidOperationException("A non-mixing object owns mixing-stroke payload.");
            }
        }
    }

}
