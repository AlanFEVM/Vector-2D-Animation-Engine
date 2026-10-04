using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    public void CreateEmpty(int layers = 1, int frameCount = AnimationTimeline.DefaultDuration)
    {
        _layerSymbolFilters.Clear();
        InvalidateQueryActiveKeyframes();
        _deferredAppendKeyframes = null;
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        frameCount = Math.Max(1, frameCount);
        ObjectCount = 0;
        VirtualAtomCount = 0;
        _nextObjectOrder = 0;
        ActiveLayer = 0;
        EditFrame = 0;
        MaxHalfExtent = 128;
        OnionSkinEnabled = false;
        OnionSkinPreviousFrames = DefaultOnionSkinPreviousFrames;
        OnionSkinNextFrames = DefaultOnionSkinNextFrames;

        LayerIds = CreateStableIds(LayerCount, AdditionalTimelineTargetIds());
        LayerNames = new string[LayerCount];
        LayerKinds = new DrawingLayerKind[LayerCount];
        LayerParentIds = new string[LayerCount];
        LayerMaskIds = new string[LayerCount];
        Array.Fill(LayerParentIds, string.Empty);
        Array.Fill(LayerMaskIds, string.Empty);
        LayerLocked = new bool[LayerCount];
        LayerVisible = new bool[LayerCount];
        LayerOpacity = new float[LayerCount];
        LayerBlendModes = new LayerBlendMode[LayerCount];
        LayerColorArgb = new int[LayerCount];
        LayerOutline = new bool[LayerCount];
        LayerStart = new int[LayerCount];
        LayerEnd = new int[LayerCount];
        for (var i = 0; i < LayerCount; i++)
        {
            LayerNames[i] = $"Layer {i:0000}";
            LayerVisible[i] = true;
            LayerOpacity[i] = 1.0f;
            LayerColorArgb[i] = DefaultLayerColor(i).ToArgb();
            LayerStart[i] = 0;
            LayerEnd[i] = frameCount - 1;
        }

        ObjectLayer = [];
        ObjectKeyframeFrame = [];
        ObjectOrder = [];
        ObjectSubOrder = [];
        X = [];
        Y = [];
        Width = [];
        Height = [];
        Angle = [];
        Stroke = [];
        CurveControlX = [];
        CurveControlY = [];
        CurveControl2X = [];
        CurveControl2Y = [];
        LineEndpointStyles = [];
        LineEndEndpointStyles = [];
        ShapeKind = [];
        ShapeVertexCounts = [];
        AtomCount = [];
        Argb = [];
        StrokeArgb = [];
        FillAutoMergeProtected = [];
        LinearGradientEnabled = [];
        GradientKinds = [];
        GradientStartArgb = [];
        GradientEndArgb = [];
        GradientStartX = [];
        GradientStartY = [];
        GradientEndX = [];
        GradientEndY = [];
        _gradientStops.Clear();
        _gradientPathLocalPoints.Clear();
        _shapeGradientMappingLocalContours.Clear();
        _pathLocalContours.Clear();
        _pathBezierLocalContours.Clear();
        _freehandLocalPoints.Clear();
        _freehandBezierLocalNodes.Clear();
        _legacyFreehandBezierNodeCache.Clear();
        _mixingStrokeLocalSamples.Clear();
        _mixingStrokeLocalRegions.Clear();
        _importedSvgSources.Clear();
        _importedSvgNames.Clear();
        _bitmapObjects.Clear();
        _textObjects.Clear();
        _objectDistortions.Clear();
        InitializeTimelineFromLayerExposure(frameCount);
        ClearSummaries();
        RebuildSpatialIndex();
    }

    public void Generate(int layers, int objects, long atoms)
    {
        _layerSymbolFilters.Clear();
        InvalidateQueryActiveKeyframes();
        _deferredAppendKeyframes = null;
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        ObjectCount = Math.Clamp(objects, 1, 1_000_000);
        VirtualAtomCount = Math.Max(atoms, ObjectCount * 3L);
        _nextObjectOrder = ObjectCount;
        ActiveLayer = 0;
        EditFrame = 0;
        MaxHalfExtent = 128;
        OnionSkinEnabled = false;
        OnionSkinPreviousFrames = DefaultOnionSkinPreviousFrames;
        OnionSkinNextFrames = DefaultOnionSkinNextFrames;

        var layerRng = new Random(0x2D0A2026);
        LayerIds = CreateStableIds(LayerCount, AdditionalTimelineTargetIds());
        LayerNames = new string[LayerCount];
        LayerKinds = new DrawingLayerKind[LayerCount];
        LayerParentIds = new string[LayerCount];
        LayerMaskIds = new string[LayerCount];
        Array.Fill(LayerParentIds, string.Empty);
        Array.Fill(LayerMaskIds, string.Empty);
        LayerLocked = new bool[LayerCount];
        LayerVisible = new bool[LayerCount];
        LayerOpacity = new float[LayerCount];
        LayerBlendModes = new LayerBlendMode[LayerCount];
        LayerColorArgb = new int[LayerCount];
        LayerOutline = new bool[LayerCount];
        LayerStart = new int[LayerCount];
        LayerEnd = new int[LayerCount];
        Array.Fill(LayerVisible, true);

        for (var i = 0; i < LayerCount; i++)
        {
            LayerNames[i] = $"Layer {i:0000}";
            LayerOpacity[i] = (float)(0.42 + layerRng.NextDouble() * 0.58);
            LayerColorArgb[i] = DefaultLayerColor(i).ToArgb();
            LayerStart[i] = layerRng.Next(0, 18);
            LayerEnd[i] = 180 + layerRng.Next(0, 60);
        }

        ObjectLayer = GC.AllocateUninitializedArray<ushort>(ObjectCount);
        ObjectKeyframeFrame = GC.AllocateUninitializedArray<int>(ObjectCount);
        ObjectOrder = GC.AllocateUninitializedArray<long>(ObjectCount);
        ObjectSubOrder = GC.AllocateUninitializedArray<double>(ObjectCount);
        X = GC.AllocateUninitializedArray<float>(ObjectCount);
        Y = GC.AllocateUninitializedArray<float>(ObjectCount);
        Width = GC.AllocateUninitializedArray<float>(ObjectCount);
        Height = GC.AllocateUninitializedArray<float>(ObjectCount);
        Angle = GC.AllocateUninitializedArray<float>(ObjectCount);
        Stroke = GC.AllocateUninitializedArray<float>(ObjectCount);
        CurveControlX = GC.AllocateUninitializedArray<float>(ObjectCount);
        CurveControlY = GC.AllocateUninitializedArray<float>(ObjectCount);
        CurveControl2X = GC.AllocateUninitializedArray<float>(ObjectCount);
        CurveControl2Y = GC.AllocateUninitializedArray<float>(ObjectCount);
        LineEndpointStyles = new LineEndpointStyle[ObjectCount];
        LineEndEndpointStyles = new LineEndpointStyle[ObjectCount];
        ShapeKind = GC.AllocateUninitializedArray<ShapeKind>(ObjectCount);
        ShapeVertexCounts = GC.AllocateUninitializedArray<int>(ObjectCount);
        AtomCount = GC.AllocateUninitializedArray<uint>(ObjectCount);
        Argb = GC.AllocateUninitializedArray<int>(ObjectCount);
        StrokeArgb = GC.AllocateUninitializedArray<int>(ObjectCount);
        FillAutoMergeProtected = new bool[ObjectCount];
        LinearGradientEnabled = new bool[ObjectCount];
        GradientKinds = new GradientKind[ObjectCount];
        GradientStartArgb = GC.AllocateUninitializedArray<int>(ObjectCount);
        GradientEndArgb = GC.AllocateUninitializedArray<int>(ObjectCount);
        GradientStartX = GC.AllocateUninitializedArray<float>(ObjectCount);
        GradientStartY = GC.AllocateUninitializedArray<float>(ObjectCount);
        GradientEndX = GC.AllocateUninitializedArray<float>(ObjectCount);
        GradientEndY = GC.AllocateUninitializedArray<float>(ObjectCount);
        _gradientStops.Clear();
        _gradientPathLocalPoints.Clear();
        _shapeGradientMappingLocalContours.Clear();
        _pathLocalContours.Clear();
        _pathBezierLocalContours.Clear();
        _freehandLocalPoints.Clear();
        _freehandBezierLocalNodes.Clear();
        _legacyFreehandBezierNodeCache.Clear();
        _mixingStrokeLocalSamples.Clear();
        _mixingStrokeLocalRegions.Clear();
        _importedSvgSources.Clear();
        _importedSvgNames.Clear();
        _bitmapObjects.Clear();
        _textObjects.Clear();
        _objectDistortions.Clear();

        var avgAtoms = (double)VirtualAtomCount / ObjectCount;
        var columns = (int)Math.Ceiling(Math.Sqrt(ObjectCount * 1.7));
        var rows = (int)Math.Ceiling((double)ObjectCount / columns);
        var cellW = StageWidth / columns;
        var cellH = StageHeight / rows;

        var workers = ParallelBatch.WorkerCount(ObjectCount);
        var batchMaxHalfExtents = new float[workers];
        var defaultStrokeArgb = Color.FromArgb(235, 238, 242, 241).ToArgb();
        ParallelBatch.For(ObjectCount, ParallelBatch.DefaultMinItemsPerWorker, (worker, start, end) =>
        {
            var maxHalfExtent = 128f;
            for (var i = start; i < end; i++)
            {
                var rng = new StressRandom(i);
                var layer = i % LayerCount;
                var col = i % columns;
                var row = i / columns;
                var jitterX = (rng.NextDouble() - 0.5) * cellW * 0.72;
                var jitterY = (rng.NextDouble() - 0.5) * cellH * 0.72;
                var sizeBias = 0.35 + rng.NextDouble() * rng.NextDouble() * 1.8;
                var hue = (layer / (double)Math.Max(1, LayerCount) + rng.NextDouble() * 0.08) % 1;
                var color = ColorFromHsl(hue, 0.46 + rng.NextDouble() * 0.22, 0.42 + rng.NextDouble() * 0.24);

                ObjectLayer[i] = (ushort)layer;
                ObjectKeyframeFrame[i] = LayerStart[layer];
                ObjectOrder[i] = i + 1L;
                ObjectSubOrder[i] = 0;
                X[i] = VectorUnits.Quantize((float)(col * cellW - StageWidth * 0.5 + jitterX));
                Y[i] = VectorUnits.Quantize((float)(row * cellH - StageHeight * 0.5 + jitterY));
                Width[i] = VectorUnits.Quantize(VectorUnits.FromPixels((float)(8 + sizeBias * (24 + rng.NextDouble() * 96))));
                Height[i] = VectorUnits.Quantize(VectorUnits.FromPixels((float)(8 + sizeBias * (18 + rng.NextDouble() * 72))));
                Angle[i] = (float)((rng.NextDouble() - 0.5) * 0.55);
                Stroke[i] = rng.NextDouble() > 0.28 ? VectorUnits.StrokePointsToUnits((float)(1 + rng.NextDouble() * 3)) : 0;
                ShapeKind[i] = RandomShapeKind(ref rng);
                ShapeVertexCounts[i] = DefaultShapeVertexCount(ShapeKind[i]);
                if (ShapeKind[i] == VectorAnimationEngine.ShapeKind.Line)
                {
                    Height[i] = Math.Max(VectorUnits.FromPixels(3), Stroke[i] + VectorUnits.FromPixels(2));
                }

                if (ShapeKind[i] == VectorAnimationEngine.ShapeKind.Line)
                {
                    var halfWidth = Width[i] * 0.5f;
                    var curveStart = LocalToWorld(i, -halfWidth, 0);
                    var curveEnd = LocalToWorld(i, halfWidth, 0);
                    var control1 = Lerp(curveStart, curveEnd, 1f / 3f);
                    var control2 = Lerp(curveStart, curveEnd, 2f / 3f);
                    CurveControlX[i] = VectorUnits.Quantize(control1.X);
                    CurveControlY[i] = VectorUnits.Quantize(control1.Y);
                    CurveControl2X[i] = VectorUnits.Quantize(control2.X);
                    CurveControl2Y[i] = VectorUnits.Quantize(control2.Y);
                }
                else
                {
                    CurveControlX[i] = X[i];
                    CurveControlY[i] = Y[i];
                    CurveControl2X[i] = X[i];
                    CurveControl2Y[i] = Y[i];
                }
                AtomCount[i] = (uint)Math.Max(3, Math.Floor(avgAtoms * (0.18 + rng.NextDouble() * rng.NextDouble() * 2.35)));
                Argb[i] = color.ToArgb();
                StrokeArgb[i] = defaultStrokeArgb;
                GradientStartArgb[i] = Argb[i];
                GradientEndArgb[i] = Argb[i];
                GradientStartX[i] = X[i] - Width[i] * 0.5f;
                GradientStartY[i] = Y[i];
                GradientEndX[i] = X[i] + Width[i] * 0.5f;
                GradientEndY[i] = Y[i];
                maxHalfExtent = Math.Max(maxHalfExtent, Math.Max(Width[i], Height[i]) * 0.5f);
            }

            batchMaxHalfExtents[worker] = maxHalfExtent;
        });
        MaxHalfExtent = batchMaxHalfExtents.Max();

        InitializeTimelineFromLayerExposure();
        RebuildSummaries(useFillColorForStrokeShapes: true);
        RebuildSpatialIndex();
    }

    public int AddObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        float stroke,
        Color color,
        uint atoms,
        ShapeKind? shapeKind = null,
        int shapeVertexCount = 0)
    {
        return AddObject(
            layer,
            center,
            size,
            angle,
            stroke,
            color,
            Color.FromArgb(238, 242, 241),
            atoms,
            shapeKind,
            shapeVertexCount);
    }

    public int AddObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms,
        ShapeKind? shapeKind = null,
        int shapeVertexCount = 0)
    {
        var index = AppendObject(layer, center, size, angle, stroke, color, strokeColor, atoms, shapeKind, shapeVertexCount);
        AppendObjectToSpatialIndex(index);
        var summaryColor = ShapeKind[index] is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform
            ? strokeColor
            : color;
        AddObjectToSummariesIncremental(index, summaryColor);
        return index;
    }

    public int AddImportedSvgObject(
        int layer,
        PointF center,
        SizeF size,
        string source,
        string? name = null)
    {
        return AddImportedSvgObject(layer, center, size, angle: 0, source, name);
    }

    public int AddImportedSvgObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        string source,
        string? name = null)
    {
        var index = AppendImportedSvgObject(layer, center, size, angle, source, name);
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, Color.FromArgb(128, 128, 128));
        return index;
    }

    public bool TryGetImportedSvgSource(int objectIndex, out string source)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.ImportedSvg
            && _importedSvgSources.TryGetValue(objectIndex, out source!)
            && !string.IsNullOrWhiteSpace(source))
        {
            return true;
        }

        source = string.Empty;
        return false;
    }

    public bool TryGetImportedSvgName(int objectIndex, out string name)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.ImportedSvg
            && _importedSvgNames.TryGetValue(objectIndex, out name!)
            && !string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// Places an imported image asset as one whole bitmap object. The payload stores only
    /// the asset reference and the placed size, so the same image can be placed many times
    /// without duplicating pixel data.
    /// </summary>
    public int AddBitmapObject(int layer, PointF center, BitmapObjectData data, float angle = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        var placedSize = data.PlacedSize;
        var index = AppendPackedObject(
            layer,
            center,
            placedSize,
            angle,
            stroke: 0,
            colorArgb: unchecked((int)0xffffffff),
            strokeColorArgb: Color.Transparent.ToArgb(),
            atoms: BitmapObjectAtomCount,
            shapeKind: VectorAnimationEngine.ShapeKind.Bitmap,
            curveControl: center,
            bitmapObjectData: data);
        // Keep the spatial index and overview summaries in sync so the placed image can
        // be hit-tested and selected. The packed append only stores the object arrays;
        // every other Add* variant performs this step (see AddImportedSvgObject).
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, Color.FromArgb(unchecked((int)0xffffffff)));
        return index;
    }

    public bool TryGetBitmapObjectData(int objectIndex, out BitmapObjectData data)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Bitmap
            && _bitmapObjects.TryGetValue(objectIndex, out data!)
            && data.IsValid)
        {
            return true;
        }

        data = null!;
        return false;
    }

    /// <summary>
    /// Re-points a placed bitmap at new import settings. Returns false when the object is
    /// not a bitmap, so callers can treat "nothing changed" and "wrong kind" distinctly.
    /// </summary>
    public bool TryUpdateBitmapObject(int objectIndex, BitmapObjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Bitmap
            || !data.IsValid)
        {
            return false;
        }

        _bitmapObjects[objectIndex] = data;
        Width[objectIndex] = Math.Max(1, VectorUnits.Quantize(data.PlacedSize.Width));
        Height[objectIndex] = Math.Max(1, VectorUnits.Quantize(data.PlacedSize.Height));
        MaxHalfExtent = Math.Max(
            MaxHalfExtent,
            Math.Max(Width[objectIndex], Height[objectIndex]) * 0.5f);
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    /// <summary>Object indices whose bitmap payload references the given image asset.</summary>
    public int[] FindBitmapObjectsUsingImageAsset(string imageAssetId)
    {
        if (string.IsNullOrWhiteSpace(imageAssetId)) return [];
        var matches = new List<int>();
        foreach (var (index, data) in _bitmapObjects)
        {
            if ((uint)index < ObjectCount
                && string.Equals(data.ImageAssetId, imageAssetId, StringComparison.Ordinal))
            {
                matches.Add(index);
            }
        }
        matches.Sort();
        return matches.ToArray();
    }

    /// <summary>
    /// Drops placed bitmap objects whose image asset no longer exists. Used when an image
    /// is removed from the library so the scene never keeps a dangling reference.
    /// </summary>
    public int RemoveBitmapObjectsWithoutImageAsset(Func<string, bool> imageAssetExists)
    {
        ArgumentNullException.ThrowIfNull(imageAssetExists);
        var orphaned = _bitmapObjects
            .Where(item => (uint)item.Key < ObjectCount
                && !imageAssetExists(item.Value.ImageAssetId))
            .Select(item => item.Key)
            .OrderDescending()
            .ToArray();
        if (orphaned.Length == 0) return 0;
        foreach (var index in orphaned) RemoveObjectAt(index);
        return orphaned.Length;
    }

    public int AddTextObject(int layer, PointF center, TextObjectData data, Color color)
    {
        var normalized = TextGeometry.NormalizeForAuthoring(data);
        var index = AppendTextObject(
            layer,
            center,
            normalized.LayoutSize,
            angle: 0,
            data: normalized,
            colorArgb: color.ToArgb());
        return index;
    }

    public bool TryGetTextObjectData(int objectIndex, out TextObjectData data)
    {
        if ((uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Text
            && _textObjects.TryGetValue(objectIndex, out data!)
            && TextGeometry.IsValidStoredData(data))
        {
            return true;
        }

        data = null!;
        return false;
    }

    public bool UpdateTextObjectData(int objectIndex, TextObjectData data)
    {
        if (!TryGetTextObjectData(objectIndex, out var previous)) return false;
        var normalized = TextGeometry.NormalizeForAuthoring(data);
        if (normalized == previous) return true;

        var scaleX = Width[objectIndex] / previous.LayoutSize.Width;
        var scaleY = Height[objectIndex] / previous.LayoutSize.Height;
        var displayWidth = normalized.LayoutSize.Width * scaleX;
        var displayHeight = normalized.LayoutSize.Height * scaleY;
        if (!float.IsFinite(displayWidth)
            || !float.IsFinite(displayHeight)
            || displayWidth <= 0
            || displayHeight <= 0)
        {
            throw new InvalidOperationException("Updating the text would produce an invalid display transform.");
        }

        var previousAtoms = AtomCount[objectIndex];
        var nextAtoms = TextGeometry.EstimateAtomCount(normalized);
        _textObjects[objectIndex] = normalized;
        Width[objectIndex] = Math.Max(1, VectorUnits.Quantize(displayWidth));
        Height[objectIndex] = Math.Max(1, VectorUnits.Quantize(displayHeight));
        AtomCount[objectIndex] = nextAtoms;
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - previousAtoms + nextAtoms);
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    public bool TryGetTextWorldContours(int objectIndex, out PointF[][] contours)
    {
        if (!TryGetTextObjectData(objectIndex, out var data))
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }

        return TextGeometry.TryCreateWorldContours(
            data,
            new PointF(X[objectIndex], Y[objectIndex]),
            new SizeF(Width[objectIndex], Height[objectIndex]),
            Angle[objectIndex],
            out contours);
    }

    internal int[] BreakApartTextObjects(IEnumerable<int> objectIndices)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var targets = objectIndices
            .Where(index => (uint)index < ObjectCount && ShapeKind[index] == VectorAnimationEngine.ShapeKind.Text)
            .Distinct()
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
        if (targets.Length == 0) return [];

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        var producedKeys = new List<(ushort Layer, int Keyframe, long Order, double SubOrder)>(targets.Length);
        try
        {
            foreach (var sourceObject in targets)
            {
                if (!TryGetTextObjectData(sourceObject, out _))
                {
                    throw new InvalidDataException("The text object is missing its editable payload.");
                }
                if (!TryGetTextWorldContours(sourceObject, out var contours) || contours.Length == 0)
                {
                    throw new InvalidOperationException("Text without drawable glyphs cannot be broken apart.");
                }

                var layer = ObjectLayer[sourceObject];
                var keyframe = ObjectKeyframeFrame[sourceObject];
                var order = ObjectOrder[sourceObject];
                var subOrder = ObjectSubOrder[sourceObject];
                EditFrame = keyframe;
                var created = AppendPathObjectContours(
                    layer,
                    contours,
                    0,
                    Color.FromArgb(Argb[sourceObject]),
                    Color.Transparent,
                    AtomCount[sourceObject]);
                if (created < 0)
                {
                    throw new InvalidOperationException("Text break-apart produced invalid outline geometry.");
                }

                ObjectKeyframeFrame[created] = keyframe;
                ObjectOrder[created] = order;
                ObjectSubOrder[created] = subOrder;
                producedKeys.Add((layer, keyframe, order, subOrder));
            }

            RemoveObjects(targets);
            var produced = new int[producedKeys.Count];
            for (var keyIndex = 0; keyIndex < producedKeys.Count; keyIndex++)
            {
                var key = producedKeys[keyIndex];
                var matches = Enumerable.Range(0, ObjectCount)
                    .Where(index => ObjectLayer[index] == key.Layer
                        && ObjectKeyframeFrame[index] == key.Keyframe
                        && ObjectOrder[index] == key.Order
                        && ObjectSubOrder[index].Equals(key.SubOrder)
                        && ShapeKind[index] == VectorAnimationEngine.ShapeKind.Path)
                    .ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException("Text break-apart lost replacement object identity.");
                }
                produced[keyIndex] = matches[0];
            }
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

    internal (int[] ProducedObjects, ImportedSvgBreakApproximation Approximations) BreakApartImportedSvgObjects(
        IEnumerable<int> objectIndices,
        bool createDrawingLayers = true)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var targets = objectIndices
            .Where(index => (uint)index < ObjectCount && ShapeKind[index] == VectorAnimationEngine.ShapeKind.ImportedSvg)
            .Distinct()
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
        if (targets.Length == 0) return ([], ImportedSvgBreakApproximation.None);

        var snapshot = CreateSnapshot();
        var previousEditFrame = EditFrame;
        var producedKeys = new List<(long Order, double SubOrder)>();
        var approximations = ImportedSvgBreakApproximation.None;
        try
        {
            foreach (var sourceObject in targets)
            {
                if (!TryGetImportedSvgSource(sourceObject, out var source))
                {
                    throw new InvalidDataException("The imported SVG object is missing its source payload.");
                }
                var importedSvgName = TryGetImportedSvgName(sourceObject, out var storedName)
                    ? storedName
                    : "SVG";

                var extracted = ImportedSvgBreakApart.Extract(source);
                approximations |= extracted.Approximations;
                var keyframe = ObjectKeyframeFrame[sourceObject];
                var order = ObjectOrder[sourceObject];
                var subOrder = ObjectSubOrder[sourceObject];
                var occupiedSubOrders = Enumerable.Range(0, ObjectCount)
                    .Where(index => index != sourceObject && ObjectOrder[index] == order)
                    .Select(index => ObjectSubOrder[index])
                    .ToHashSet();
                var center = new PointF(X[sourceObject], Y[sourceObject]);
                var size = new SizeF(Width[sourceObject], Height[sourceObject]);
                var angle = Angle[sourceObject];
                var scaleX = size.Width / extracted.IntrinsicSize.Width;
                var scaleY = size.Height / extracted.IntrinsicSize.Height;
                var strokeScale = MathF.Sqrt(Math.Abs(scaleX * scaleY));
                var cos = MathF.Cos(angle);
                var sin = MathF.Sin(angle);
                var producedBeforeSource = producedKeys.Count;
                var preparedParts = new List<(ImportedSvgBreakPart Part, PointF[][] Contours, PointF[] Points)>(extracted.Parts.Length);
                foreach (var part in extracted.Parts.OrderBy(part => part.Order))
                {
                    switch (part)
                    {
                        case ImportedSvgBreakFill fill:
                            var contours = NormalizePathContours(fill.Contours
                                .Select(contour => contour.Select(ToWorld).ToArray())
                                .ToArray());
                            if (contours.Length == 0)
                            {
                                approximations |= ImportedSvgBreakApproximation.SkippedInvalidGeometry;
                                continue;
                            }
                            preparedParts.Add((part, contours, []));
                            break;
                        case ImportedSvgBreakStroke stroke:
                            var points = NormalizeFreehandPoints(stroke.Points.Select(ToWorld).ToArray());
                            if (points.Length == 0)
                            {
                                approximations |= ImportedSvgBreakApproximation.SkippedInvalidGeometry;
                                continue;
                            }
                            preparedParts.Add((part, [], points));
                            break;
                        default:
                            throw new InvalidDataException("The SVG break-apart result contains an unknown geometry part.");
                    }
                }

                if (preparedParts.Count == 0)
                {
                    throw new InvalidDataException("The SVG break-apart result contains no visible geometry at the current object size.");
                }

                var usedLayerKeys = preparedParts.Select(item => item.Part.LayerKey).ToHashSet();
                var sourceLayers = extracted.Layers
                    .Where(layer => usedLayerKeys.Contains(layer.Key))
                    .OrderBy(layer => layer.Order)
                    .ToArray();
                if (sourceLayers.Length == 0
                    || sourceLayers.Select(layer => layer.Key).Distinct().Count() != sourceLayers.Length
                    || usedLayerKeys.Except(sourceLayers.Select(layer => layer.Key)).Any())
                {
                    throw new InvalidDataException("The SVG break-apart result contains invalid layer metadata.");
                }
                if (createDrawingLayers && sourceLayers.Length + 1 > ushort.MaxValue - LayerCount)
                {
                    throw new InvalidOperationException("The SVG break-apart result exceeds the supported drawing-layer count.");
                }

                var hostLayer = ObjectLayer[sourceObject];
                var destinationLayers = new Dictionary<int, int>(sourceLayers.Length);
                if (createDrawingLayers)
                {
                    var hostLayerColor = LayerColorArgb[hostLayer];
                    var hostLayerParentId = LayerParentIds[hostLayer];
                    var hostLayerVisible = LayerVisible[hostLayer];
                    var hostLayerOpacity = LayerOpacity[hostLayer];
                    var hostLayerBlendMode = LayerBlendModes[hostLayer];
                    var hostLayerOutline = LayerOutline[hostLayer];
                    var insertionIndex = hostLayer + 1;
                    var folderLayer = InsertLayer(
                        DrawingLayerKind.Folder,
                        insertionIndex++,
                        importedSvgName);
                    LayerParentIds[folderLayer] = hostLayerParentId;
                    LayerColorArgb[folderLayer] = hostLayerColor;
                    var folderId = LayerIds[folderLayer];
                    foreach (var sourceLayer in sourceLayers.OrderByDescending(layer => layer.Order))
                    {
                        var destinationLayer = InsertLayer(DrawingLayerKind.Drawing, insertionIndex++, sourceLayer.Name);
                        LayerParentIds[destinationLayer] = folderId;
                        LayerVisible[destinationLayer] = hostLayerVisible;
                        LayerOpacity[destinationLayer] = hostLayerOpacity;
                        LayerBlendModes[destinationLayer] = hostLayerBlendMode;
                        LayerColorArgb[destinationLayer] = hostLayerColor;
                        LayerOutline[destinationLayer] = hostLayerOutline;
                        PrepareImportedSvgBreakLayerFrame(destinationLayer, keyframe);
                        destinationLayers.Add(sourceLayer.Key, destinationLayer);
                    }
                    ActiveLayer = folderLayer + 1;
                }
                else
                {
                    foreach (var sourceLayer in sourceLayers)
                    {
                        destinationLayers.Add(sourceLayer.Key, hostLayer);
                    }
                }
                EditFrame = keyframe;

                foreach (var prepared in preparedParts)
                {
                    var part = prepared.Part;
                    var layer = destinationLayers[part.LayerKey];
                    int created;
                    switch (part)
                    {
                        case ImportedSvgBreakFill fill:
                            created = AppendPathObjectContours(
                                layer,
                                prepared.Contours,
                                0,
                                fill.Paint.Color,
                                Color.Transparent,
                                (uint)Math.Max(3, prepared.Contours.Sum(contour => contour.Length)));
                            break;
                        case ImportedSvgBreakStroke stroke:
                            created = prepared.Points.Length == 2 && !stroke.Closed
                                ? AddLineSegment(
                                    layer,
                                    prepared.Points[0],
                                    prepared.Points[1],
                                    stroke.Width * strokeScale,
                                    Color.Transparent,
                                    stroke.Paint.Color,
                                    (uint)Math.Max(3, prepared.Points.Length),
                                    stroke.StartEndpointStyle,
                                    stroke.EndEndpointStyle)
                                : AppendFreehandStroke(
                                    layer,
                                    prepared.Points,
                                    stroke.Width * strokeScale,
                                    stroke.Paint.Color,
                                    (uint)Math.Max(3, prepared.Points.Length));
                            break;
                        default:
                            throw new InvalidDataException("The SVG break-apart result contains an unknown geometry part.");
                    }

                    if (created < 0)
                    {
                        approximations |= ImportedSvgBreakApproximation.SkippedInvalidGeometry;
                        continue;
                    }
                    if (part is ImportedSvgBreakFill) FillAutoMergeProtected[created] = true;
                    if (ObjectKeyframeFrame[created] != keyframe)
                    {
                        throw new InvalidOperationException("SVG break-apart wrote geometry into the wrong keyframe.");
                    }
                    do
                    {
                        subOrder = Math.BitIncrement(subOrder);
                    }
                    while (!occupiedSubOrders.Add(subOrder));
                    ObjectKeyframeFrame[created] = keyframe;
                    ObjectOrder[created] = order;
                    ObjectSubOrder[created] = subOrder;
                    producedKeys.Add((order, subOrder));
                }

                if (producedKeys.Count == producedBeforeSource)
                {
                    throw new InvalidDataException("The SVG break-apart result contains no visible geometry at the current object size.");
                }

                PointF ToWorld(PointF point)
                {
                    var localX = point.X * scaleX - size.Width * 0.5f;
                    var localY = point.Y * scaleY - size.Height * 0.5f;
                    return VectorUnits.Quantize(new PointF(
                        center.X + localX * cos - localY * sin,
                        center.Y + localX * sin + localY * cos));
                }
            }

            RemoveObjects(targets);
            var produced = producedKeys
                .Select(key => FindObjectByStackKey(key.Order, key.SubOrder))
                .Where(index => index >= 0)
                .ToArray();
            if (produced.Length != producedKeys.Count)
            {
                throw new InvalidOperationException("SVG break-apart lost replacement object identity.");
            }
            return (produced, approximations);
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

    private void PrepareImportedSvgBreakLayerFrame(int layer, int keyframe)
    {
        var track = Timeline.FindTrackByTargetId(LayerIds[layer])
            ?? throw new InvalidOperationException("SVG break-apart could not create a timeline track for its layer.");
        keyframe = Math.Max(0, keyframe);
        if (keyframe >= track.Duration) Timeline.SetTrackDuration(track.Id, keyframe + 1);
        Timeline.InsertKeyframe(track.Id, keyframe);
        RefreshLegacyExposureBounds(layer);
    }

    internal int AppendImportedSvgObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        string source,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return AppendPackedObject(
            layer: layer,
            center: center,
            size: size,
            angle: angle,
            stroke: 0,
            colorArgb: Color.FromArgb(128, 128, 128).ToArgb(),
            strokeColorArgb: Color.Transparent.ToArgb(),
            atoms: 3,
            shapeKind: VectorAnimationEngine.ShapeKind.ImportedSvg,
            curveControl: center,
            importedSvgSource: source,
            importedSvgName: name);
    }

    private static string NormalizeImportedSvgName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    internal int AppendTextObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        TextObjectData data,
        int colorArgb)
    {
        if (!float.IsFinite(size.Width)
            || !float.IsFinite(size.Height)
            || size.Width <= 0
            || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Text display size must be finite and positive.");
        }

        var normalized = TextGeometry.NormalizeStoredData(data);
        var deferred = _deferredAppendKeyframes is not null;
        var index = AppendPackedObject(
            layer: layer,
            center: center,
            size: size,
            angle: angle,
            stroke: 0,
            colorArgb: colorArgb,
            strokeColorArgb: Color.Transparent.ToArgb(),
            atoms: TextGeometry.EstimateAtomCount(normalized),
            shapeKind: VectorAnimationEngine.ShapeKind.Text,
            curveControl: center,
            textObjectData: normalized);
        if (!deferred)
        {
            AppendObjectToSpatialIndex(index);
            AddObjectToSummariesIncremental(index, Color.FromArgb(colorArgb));
        }
        return index;
    }

    internal int AppendObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms,
        ShapeKind? shapeKind = null,
        int shapeVertexCount = 0)
    {
        var shape = shapeKind ?? InferShapeKind(size, atoms);
        return AppendPackedObject(
            layer,
            center,
            size,
            angle,
            stroke,
            color.ToArgb(),
            strokeColor.ToArgb(),
            atoms,
            shape,
            center,
            shapeVertexCount: shapeVertexCount);
    }

    internal int AppendPackedObject(
        int layer,
        PointF center,
        SizeF size,
        float angle,
        float stroke,
        int colorArgb,
        int strokeColorArgb,
        uint atoms,
        ShapeKind shapeKind,
        PointF curveControl,
        LineEndpointStyle startEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle endEndpointStyle = LineEndpointStyle.Round,
        int shapeVertexCount = 0,
        PointF? curveControl2 = null,
        string? importedSvgSource = null,
        string? importedSvgName = null,
        TextObjectData? textObjectData = null,
        BitmapObjectData? bitmapObjectData = null)
    {
        TextObjectData? normalizedTextData = null;
        BitmapObjectData? normalizedBitmapData = null;
        if (shapeKind == VectorAnimationEngine.ShapeKind.ImportedSvg)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(importedSvgSource);
            if (textObjectData is not null)
            {
                throw new ArgumentException("Imported SVG objects cannot carry text payload.", nameof(textObjectData));
            }
            if (bitmapObjectData is not null)
            {
                throw new ArgumentException("Imported SVG objects cannot carry bitmap payload.", nameof(bitmapObjectData));
            }
        }
        else if (shapeKind == VectorAnimationEngine.ShapeKind.Bitmap)
        {
            if (importedSvgSource is not null || importedSvgName is not null || textObjectData is not null)
            {
                throw new ArgumentException("Bitmap objects cannot carry SVG or text payload.", nameof(importedSvgSource));
            }
            normalizedBitmapData = bitmapObjectData
                ?? throw new ArgumentNullException(nameof(bitmapObjectData), "Bitmap objects require an image asset reference.");
            if (!normalizedBitmapData.IsValid)
            {
                throw new ArgumentException("The bitmap object payload is invalid.", nameof(bitmapObjectData));
            }
            // A bitmap is drawn from its own pixels; the shape fill and stroke play no part.
            stroke = 0;
            strokeColorArgb = Color.Transparent.ToArgb();
        }
        else if (shapeKind == VectorAnimationEngine.ShapeKind.Text)
        {
            if (importedSvgSource is not null || importedSvgName is not null || bitmapObjectData is not null)
            {
                throw new ArgumentException("Text objects cannot carry imported SVG source payload.", nameof(importedSvgSource));
            }
            normalizedTextData = TextGeometry.NormalizeStoredData(
                textObjectData ?? throw new ArgumentNullException(nameof(textObjectData), "Text objects require editable text payload."));
            stroke = 0;
            strokeColorArgb = Color.Transparent.ToArgb();
        }
        else if (importedSvgSource is not null
            || importedSvgName is not null
            || textObjectData is not null
            || bitmapObjectData is not null)
        {
            throw new ArgumentException("Only sparse object kinds can carry sparse object payload.");
        }

        var targetLayer = ResolveObjectLayer(layer);
        if (IsCollisionTerrainLayer(targetLayer))
        {
            colorArgb = NormalizeCollisionTerrainObjectArgb(colorArgb);
        }
        var keyframeFrame = _deferredAppendKeyframes is { } deferred
            ? deferred[targetLayer]
            : EnsureWritableKeyframe(targetLayer, EditFrame);
        if (keyframeFrame == DeferredKeyframeUnset)
        {
            throw new InvalidOperationException("Deferred append received an object for a layer that was not marked as populated.");
        }

        var index = ObjectCount;
        ObjectCount++;
        VirtualAtomCount += atoms;
        EnsureObjectCapacity(ObjectCount);

        ObjectLayer[index] = (ushort)targetLayer;
        ObjectKeyframeFrame[index] = keyframeFrame;
        ObjectOrder[index] = ++_nextObjectOrder;
        ObjectSubOrder[index] = 0;
        X[index] = VectorUnits.Quantize(center.X);
        Y[index] = VectorUnits.Quantize(center.Y);
        Width[index] = Math.Max(1, VectorUnits.Quantize(size.Width));
        Height[index] = Math.Max(1, VectorUnits.Quantize(size.Height));
        Angle[index] = angle;
        Stroke[index] = Math.Max(0, stroke);
        ShapeKind[index] = shapeKind;
        ShapeVertexCounts[index] = NormalizeShapeVertexCount(shapeKind, shapeVertexCount);
        var firstControl = curveControl;
        var secondControl = curveControl2 ?? curveControl;
        if (shapeKind == VectorAnimationEngine.ShapeKind.Line && curveControl2 is null)
        {
            var halfWidth = Width[index] * 0.5f;
            var start = LocalToWorld(index, -halfWidth, 0);
            var end = LocalToWorld(index, halfWidth, 0);
            firstControl = Lerp(start, curveControl, 2f / 3f);
            secondControl = Lerp(end, curveControl, 2f / 3f);
        }
        CurveControlX[index] = VectorUnits.Quantize(firstControl.X);
        CurveControlY[index] = VectorUnits.Quantize(firstControl.Y);
        CurveControl2X[index] = VectorUnits.Quantize(secondControl.X);
        CurveControl2Y[index] = VectorUnits.Quantize(secondControl.Y);
        LineEndpointStyles[index] = NormalizeLineEndpointStyle(startEndpointStyle);
        LineEndEndpointStyles[index] = NormalizeLineEndpointStyle(endEndpointStyle);
        AtomCount[index] = Math.Max(3, atoms);
        Argb[index] = colorArgb;
        StrokeArgb[index] = strokeColorArgb;
        FillAutoMergeProtected[index] = IsCollisionTerrainLayer(targetLayer);
        LinearGradientEnabled[index] = false;
        GradientKinds[index] = GradientKind.Solid;
        GradientStartArgb[index] = colorArgb;
        GradientEndArgb[index] = colorArgb;
        GradientStartX[index] = X[index] - Width[index] * 0.5f;
        GradientStartY[index] = Y[index];
        GradientEndX[index] = X[index] + Width[index] * 0.5f;
        GradientEndY[index] = Y[index];
        if (importedSvgSource is not null) _importedSvgSources[index] = importedSvgSource;
        var normalizedImportedSvgName = NormalizeImportedSvgName(importedSvgName);
        if (normalizedImportedSvgName.Length > 0) _importedSvgNames[index] = normalizedImportedSvgName;
        if (normalizedBitmapData is not null) _bitmapObjects[index] = normalizedBitmapData;
        if (normalizedTextData is not null) _textObjects[index] = normalizedTextData;
        MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[index], Height[index]) * 0.5f);
        return index;
    }

    internal int AppendPackedObjects(PackedSceneObject[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0) return ObjectCount;
        if (objects.Any(item => item.Shape is VectorAnimationEngine.ShapeKind.ImportedSvg
                or VectorAnimationEngine.ShapeKind.Text
                or VectorAnimationEngine.ShapeKind.MixingStroke
                or VectorAnimationEngine.ShapeKind.Bitmap))
        {
            throw new InvalidOperationException("Sparse-payload objects cannot be appended through a packed batch.");
        }
        var deferredKeyframes = _deferredAppendKeyframes
            ?? throw new InvalidOperationException("Packed batches require an active deferred append.");
        var firstObject = ObjectCount;
        var firstOrder = _nextObjectOrder;
        EnsureObjectCapacity(ObjectCount + objects.Length);
        var workers = ParallelBatch.WorkerCount(objects.Length, 4096);
        var batchAtoms = new long[workers];
        var batchMaxHalfExtents = new float[workers];
        ParallelBatch.For(objects.Length, 4096, (worker, start, end) =>
        {
            long atoms = 0;
            var maxHalfExtent = 128f;
            for (var offset = start; offset < end; offset++)
            {
                var item = objects[offset];
                var index = firstObject + offset;
                var layer = ResolveObjectLayer(item.Layer);
                var objectArgb = IsCollisionTerrainLayer(layer)
                    ? NormalizeCollisionTerrainObjectArgb(item.Argb)
                    : item.Argb;
                ObjectLayer[index] = (ushort)layer;
                ObjectKeyframeFrame[index] = deferredKeyframes[layer];
                ObjectOrder[index] = firstOrder + offset + 1L;
                ObjectSubOrder[index] = 0;
                X[index] = VectorUnits.Quantize(item.Center.X);
                Y[index] = VectorUnits.Quantize(item.Center.Y);
                Width[index] = Math.Max(1, VectorUnits.Quantize(item.Size.Width));
                Height[index] = Math.Max(1, VectorUnits.Quantize(item.Size.Height));
                Angle[index] = item.Angle;
                Stroke[index] = Math.Max(0, item.Stroke);
                CurveControlX[index] = VectorUnits.Quantize(item.CurveControl.X);
                CurveControlY[index] = VectorUnits.Quantize(item.CurveControl.Y);
                CurveControl2X[index] = VectorUnits.Quantize(item.CurveControl2.X);
                CurveControl2Y[index] = VectorUnits.Quantize(item.CurveControl2.Y);
                LineEndpointStyles[index] = NormalizeLineEndpointStyle(item.StartEndpointStyle);
                LineEndEndpointStyles[index] = NormalizeLineEndpointStyle(item.EndEndpointStyle);
                ShapeKind[index] = item.Shape;
                ShapeVertexCounts[index] = NormalizeShapeVertexCount(item.Shape, item.ShapeVertexCount);
                AtomCount[index] = Math.Max(3, item.Atoms);
                Argb[index] = objectArgb;
                StrokeArgb[index] = item.StrokeArgb;
                FillAutoMergeProtected[index] = IsCollisionTerrainLayer(layer);
                var gradientKind = item.GradientKind == GradientKind.Solid && item.LinearGradientEnabled
                    ? GradientKind.Linear
                    : item.GradientKind;
                LinearGradientEnabled[index] = gradientKind != GradientKind.Solid && SupportsGradient(item.Shape);
                GradientKinds[index] = LinearGradientEnabled[index] ? gradientKind : GradientKind.Solid;
                GradientStartArgb[index] = LinearGradientEnabled[index] ? item.GradientStartArgb : objectArgb;
                GradientEndArgb[index] = LinearGradientEnabled[index] ? item.GradientEndArgb : objectArgb;
                GradientStartX[index] = LinearGradientEnabled[index] ? VectorUnits.Quantize(item.GradientStart.X) : X[index] - Width[index] * 0.5f;
                GradientStartY[index] = LinearGradientEnabled[index] ? VectorUnits.Quantize(item.GradientStart.Y) : Y[index];
                GradientEndX[index] = LinearGradientEnabled[index] ? VectorUnits.Quantize(item.GradientEnd.X) : X[index] + Width[index] * 0.5f;
                GradientEndY[index] = LinearGradientEnabled[index] ? VectorUnits.Quantize(item.GradientEnd.Y) : Y[index];
                if (LinearGradientEnabled[index] && item.GradientStops is { Length: >= 2 })
                {
                    _gradientStops[index] = NormalizeGradientStops(item.GradientStops);
                }
                atoms += item.Atoms;
                maxHalfExtent = Math.Max(maxHalfExtent, Math.Max(Width[index], Height[index]) * 0.5f);
            }

            batchAtoms[worker] = atoms;
            batchMaxHalfExtents[worker] = maxHalfExtent;
        }, workers);

        ObjectCount += objects.Length;
        _nextObjectOrder += objects.Length;
        VirtualAtomCount += batchAtoms.Sum();
        MaxHalfExtent = Math.Max(MaxHalfExtent, batchMaxHalfExtents.Max());
        return firstObject;
    }

    public int GetShapeVertexCount(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return 0;
        var stored = ShapeVertexCounts.Length > objectIndex ? ShapeVertexCounts[objectIndex] : 0;
        return NormalizeShapeVertexCount(ShapeKind[objectIndex], stored);
    }

    public int AddLineSegment(
        int layer,
        PointF start,
        PointF end,
        float stroke,
        Color color,
        Color strokeColor,
        uint atoms,
        LineEndpointStyle startEndpointStyle = LineEndpointStyle.Round,
        LineEndpointStyle? endEndpointStyle = null)
    {
        return AddCubicCurveSegment(
            layer,
            start,
            Lerp(start, end, 1f / 3f),
            Lerp(start, end, 2f / 3f),
            end,
            stroke,
            color,
            strokeColor,
            atoms,
            startEndpointStyle,
            endEndpointStyle);
    }

    public int AddCurveSegment(
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
        var control1 = Lerp(start, control, 2f / 3f);
        var control2 = Lerp(end, control, 2f / 3f);
        return AddCubicCurveSegment(
            layer,
            start,
            control1,
            control2,
            end,
            stroke,
            color,
            strokeColor,
            atoms,
            startEndpointStyle,
            endEndpointStyle);
    }

    public int AddCubicCurveSegment(
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
        var index = AppendCubicCurveSegment(
            layer,
            start,
            control1,
            control2,
            end,
            stroke,
            color,
            strokeColor,
            atoms,
            startEndpointStyle,
            endEndpointStyle);
        AppendObjectToSpatialIndex(index);
        AddObjectToSummariesIncremental(index, strokeColor);
        return index;
    }

    public bool RemoveObjectAt(int index)
    {
        if ((uint)index >= ObjectCount) return false;

        var removedLayer = ObjectLayer[index];
        var removedKeyframeFrame = ObjectKeyframeFrame[index];
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - AtomCount[index]);
        var last = ObjectCount - 1;
        if (index != last) CopyObjectData(last, index);

        ObjectCount--;
        ResizeObjectArrays();
        RemovePathDataOutsideObjectCount();
        RemoveFreehandDataOutsideObjectCount();
        RemoveMixingStrokeDataOutsideObjectCount();
        RemoveImportedSvgDataOutsideObjectCount();
        RemoveTextDataOutsideObjectCount();
        RemoveGradientDataOutsideObjectCount();
        RemoveObjectDistortionsOutsideObjectCount();
        RebuildGeometryIndex();
        RebuildSummaries();
        SynchronizeKeyframeContentKind(removedLayer, removedKeyframeFrame);
        return true;
    }

    public int RemoveObjects(IEnumerable<int> indices)
    {
        if (ObjectCount <= 0) return 0;
        var remove = new bool[ObjectCount];
        var removeCount = 0;
        foreach (var index in indices)
        {
            if ((uint)index >= ObjectCount || remove[index]) continue;
            remove[index] = true;
            removeCount++;
        }

        if (removeCount == 0) return 0;

        var affectedKeyframes = new HashSet<(int Layer, int Frame)>();
        long removedAtoms = 0;
        var write = 0;
        for (var read = 0; read < ObjectCount; read++)
        {
            if (remove[read])
            {
                affectedKeyframes.Add((ObjectLayer[read], ObjectKeyframeFrame[read]));
                removedAtoms += AtomCount[read];
                continue;
            }

            if (write != read) MoveObjectData(read, write);
            write++;
        }

        ObjectCount = write;
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - removedAtoms);
        ResizeObjectArrays();
        RemovePathDataOutsideObjectCount();
        RemoveFreehandDataOutsideObjectCount();
        RemoveMixingStrokeDataOutsideObjectCount();
        RemoveImportedSvgDataOutsideObjectCount();
        RemoveTextDataOutsideObjectCount();
        RemoveGradientDataOutsideObjectCount();
        RemoveObjectDistortionsOutsideObjectCount();
        RebuildGeometryIndex();
        RebuildSummaries();
        foreach (var (layer, frame) in affectedKeyframes) SynchronizeKeyframeContentKind(layer, frame);
        return removeCount;
    }

    public void RebuildGeometryIndex()
    {
        GeometryIndexBuildCount++;
        var workers = ParallelBatch.WorkerCount(ObjectCount, 8192);
        if (workers == 1)
        {
            MaxHalfExtent = 128;
            for (var i = 0; i < ObjectCount; i++) MaxHalfExtent = Math.Max(MaxHalfExtent, ObjectHalfExtent(i));
        }
        else
        {
            var maxima = new float[workers];
            ParallelBatch.For(ObjectCount, 8192, (worker, start, end) =>
            {
                var maximum = 128f;
                for (var i = start; i < end; i++) maximum = Math.Max(maximum, ObjectHalfExtent(i));
                maxima[worker] = maximum;
            }, workers);
            MaxHalfExtent = maxima.Max();
        }

        RebuildSpatialIndex();
    }

    private float ObjectHalfExtent(int objectIndex)
    {
        var bounds = GetObjectWorldBounds(objectIndex);
        return Math.Max(
            Math.Max(Math.Abs(bounds.Left - X[objectIndex]), Math.Abs(bounds.Right - X[objectIndex])),
            Math.Max(Math.Abs(bounds.Top - Y[objectIndex]), Math.Abs(bounds.Bottom - Y[objectIndex])));
    }

    internal void CompleteDeferredBuild()
    {
        RebuildGeometryIndex();
        RebuildSummaries();
    }

    internal VectorScene CreateStaticSelectionScene(
        IReadOnlyCollection<int> objectIndices,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var targets = objectIndices
            .Distinct()
            .Where(index => (uint)index < ObjectCount && IsObjectActive(index, frame))
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("Convert to Symbol requires selected drawing objects.");
        }

        var sourceLayer = ObjectLayer[targets[0]];
        if (targets.Any(index => ObjectLayer[index] != sourceLayer)
            || GetLayerKind(sourceLayer) != DrawingLayerKind.Drawing
            || IsLayerEffectivelyLocked(sourceLayer))
        {
            throw new InvalidOperationException("Convert to Symbol requires objects from one unlocked drawing layer.");
        }

        var destination = new VectorScene();
        destination.CreateEmpty(1, 1);
        destination.LayerNames[0] = LayerNames[sourceLayer];
        destination.LayerColorArgb[0] = LayerColorArgb[sourceLayer];
        destination.EnsureObjectCapacity(targets.Length);
        foreach (var sourceObject in targets)
        {
            var destinationObject = destination.ObjectCount++;
            destination.CopyObjectDataFrom(this, sourceObject, destinationObject);
            destination.ObjectLayer[destinationObject] = 0;
            destination.ObjectKeyframeFrame[destinationObject] = 0;
            destination.ObjectOrder[destinationObject] = ++destination._nextObjectOrder;
            destination.VirtualAtomCount += destination.AtomCount[destinationObject];
        }

        destination.SynchronizeAllKeyframeContentKinds();
        destination.RebuildGeometryIndex();
        destination.RebuildSummaries();
        return destination;
    }

    /// <summary>
    /// Clones one object into a standalone single-layer scene for a drop preview. The
    /// clone carries the same sparse payload (bitmap reference, imported SVG source, text,
    /// distortions) so the preview renders exactly what a committed drop would insert,
    /// but it owns its own arrays and is never referenced by the project.
    /// </summary>
    internal static VectorScene? CreateObjectPreviewScene(VectorScene source, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)objectIndex >= source.ObjectCount) return null;

        var preview = new VectorScene();
        preview.CreateEmpty(1, 1);
        var sourceLayer = source.ObjectLayer[objectIndex];
        if ((uint)sourceLayer < source.LayerCount)
        {
            preview.LayerNames[0] = source.LayerNames[sourceLayer];
            preview.LayerColorArgb[0] = source.LayerColorArgb[sourceLayer];
        }

        // A preview scene has its own single layer; mirror whether the object's own layer
        // was visible so a drop on a hidden layer does not look like a successful commit.
        preview.LayerVisible[0] = (uint)sourceLayer < source.LayerCount
            && source.LayerVisible[sourceLayer];

        preview.EnsureObjectCapacity(1);
        preview.ObjectCount = 1;
        preview.CopyObjectDataFrom(source, objectIndex, 0);
        preview.ObjectLayer[0] = 0;
        preview.ObjectKeyframeFrame[0] = 0;
        preview.ObjectOrder[0] = ++preview._nextObjectOrder;
        preview.ObjectSubOrder[0] = 0;
        preview.VirtualAtomCount = preview.AtomCount[0];
        preview.MaxHalfExtent = Math.Max(128f, Math.Max(preview.Width[0], preview.Height[0]) * 0.5f);
        preview.SynchronizeAllKeyframeContentKinds();
        preview.RebuildGeometryIndex();
        preview.RebuildSummaries();
        return preview;
    }

    internal void InvalidateDeferredTopologyQueries()
    {
        _fillPartitionCache.Clear();
        _exposedFillBezierCache.Clear();
        _fillPartitionCacheRevision = -1;
        _exposedFillBezierCacheRevision = -1;
        InvalidateInteractiveQueryCaches();
    }

    internal void BeginDeferredAppend(int expectedObjectCount, IReadOnlyList<int> populatedLayers)
    {
        ArgumentNullException.ThrowIfNull(populatedLayers);
        if (_deferredAppendKeyframes is not null) throw new InvalidOperationException("A deferred append is already active.");
        using var batchUpdate = Timeline.BeginBatchUpdate();
        SynchronizeTimelineTracks();
        var keyframes = new int[LayerCount];
        Array.Fill(keyframes, DeferredKeyframeUnset);
        foreach (var layer in populatedLayers)
        {
            if ((uint)layer >= LayerCount || keyframes[layer] != DeferredKeyframeUnset) continue;
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            keyframes[layer] = track is null ? 0 : EnsureWritableKeyframe(track, layer, EditFrame);
        }

        EnsureObjectCapacity(ObjectCount + Math.Max(0, expectedObjectCount));
        _deferredAppendKeyframes = keyframes;
    }

    internal void EndDeferredAppend()
    {
        var keyframes = _deferredAppendKeyframes;
        if (keyframes is null) return;
        try
        {
            var occupied = new bool[LayerCount];
            for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
            {
                var layer = ObjectLayer[objectIndex];
                if (keyframes[layer] == DeferredKeyframeUnset
                    || ObjectKeyframeFrame[objectIndex] != keyframes[layer])
                {
                    continue;
                }

                occupied[layer] = true;
            }

            using var batchUpdate = Timeline.BeginBatchUpdate();
            for (var layer = 0; layer < LayerCount; layer++)
            {
                if (keyframes[layer] == DeferredKeyframeUnset || occupied[layer]) continue;
                var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
                if (track is null) continue;
                Timeline.InsertBlankKeyframe(track.Id, keyframes[layer]);
                RefreshLegacyExposureBounds(layer);
            }
        }
        finally
        {
            _deferredAppendKeyframes = null;
        }
    }

    private void CopyObjectData(int from, int to)
    {
        CopyObjectDataFrom(this, from, to);
    }

    private void CopyObjectDataFrom(VectorScene source, int from, int to)
    {
        CopyObjectScalarDataFrom(source, from, to);
        if (source._gradientStops.TryGetValue(from, out var gradientStops))
        {
            _gradientStops[to] = gradientStops.ToArray();
        }
        else
        {
            _gradientStops.Remove(to);
        }
        if (source._gradientPathLocalPoints.TryGetValue(from, out var gradientPath))
        {
            _gradientPathLocalPoints[to] = gradientPath.ToArray();
        }
        else
        {
            _gradientPathLocalPoints.Remove(to);
        }
        if (source._shapeGradientMappingLocalContours.TryGetValue(from, out var shapeGradientMapping))
        {
            _shapeGradientMappingLocalContours[to] = CloneContours(shapeGradientMapping);
        }
        else
        {
            _shapeGradientMappingLocalContours.Remove(to);
        }
        if (source._pathLocalContours.TryGetValue(from, out var contours))
        {
            _pathLocalContours[to] = CloneContours(contours);
        }
        else
        {
            _pathLocalContours.Remove(to);
        }
        if (source._pathBezierLocalContours.TryGetValue(from, out var bezierContours))
        {
            _pathBezierLocalContours[to] = CloneBezierContours(bezierContours);
        }
        else
        {
            _pathBezierLocalContours.Remove(to);
        }

        if (source._freehandLocalPoints.TryGetValue(from, out var freehandPoints))
        {
            _freehandLocalPoints[to] = freehandPoints.ToArray();
        }
        else
        {
            _freehandLocalPoints.Remove(to);
        }

        if (source._freehandBezierLocalNodes.TryGetValue(from, out var freehandBezierNodes))
        {
            _freehandBezierLocalNodes[to] = freehandBezierNodes.ToArray();
        }
        else
        {
            _freehandBezierLocalNodes.Remove(to);
        }
        _legacyFreehandBezierNodeCache.Remove(to);

        if (source._mixingStrokeLocalSamples.TryGetValue(from, out var mixingSamples))
        {
            _mixingStrokeLocalSamples[to] = mixingSamples.ToArray();
        }
        else
        {
            _mixingStrokeLocalSamples.Remove(to);
        }

        if (source._mixingStrokeLocalRegions.TryGetValue(from, out var mixingRegion))
        {
            _mixingStrokeLocalRegions[to] = mixingRegion.DeepClone();
        }
        else
        {
            _mixingStrokeLocalRegions.Remove(to);
        }

        if (source._importedSvgSources.TryGetValue(from, out var importedSvgSource))
        {
            _importedSvgSources[to] = importedSvgSource;
        }
        else
        {
            _importedSvgSources.Remove(to);
        }

        if (source._importedSvgNames.TryGetValue(from, out var importedSvgName))
        {
            _importedSvgNames[to] = importedSvgName;
        }
        else
        {
            _importedSvgNames.Remove(to);
        }

        if (source._bitmapObjects.TryGetValue(from, out var bitmapObject))
        {
            _bitmapObjects[to] = bitmapObject;
        }
        else
        {
            _bitmapObjects.Remove(to);
        }

        if (source._textObjects.TryGetValue(from, out var textObjectData))
        {
            _textObjects[to] = textObjectData;
        }
        else
        {
            _textObjects.Remove(to);
        }

        if (source._objectDistortions.TryGetValue(from, out var distortions))
        {
            _objectDistortions[to] = CloneDistortions(distortions);
        }
        else
        {
            _objectDistortions.Remove(to);
        }
    }

    private void MoveObjectData(int from, int to)
    {
        CopyObjectScalarDataFrom(this, from, to);
        MoveObjectDictionaryEntry(_gradientStops, from, to);
        MoveObjectDictionaryEntry(_gradientPathLocalPoints, from, to);
        MoveObjectDictionaryEntry(_shapeGradientMappingLocalContours, from, to);
        MoveObjectDictionaryEntry(_pathLocalContours, from, to);
        MoveObjectDictionaryEntry(_pathBezierLocalContours, from, to);
        MoveObjectDictionaryEntry(_freehandLocalPoints, from, to);
        MoveObjectDictionaryEntry(_freehandBezierLocalNodes, from, to);
        MoveObjectDictionaryEntry(_legacyFreehandBezierNodeCache, from, to);
        MoveObjectDictionaryEntry(_mixingStrokeLocalSamples, from, to);
        MoveObjectDictionaryEntry(_mixingStrokeLocalRegions, from, to);
        MoveObjectDictionaryEntry(_importedSvgSources, from, to);
        MoveObjectDictionaryEntry(_importedSvgNames, from, to);
        MoveObjectDictionaryEntry(_bitmapObjects, from, to);
        MoveObjectDictionaryEntry(_textObjects, from, to);
        MoveObjectDictionaryEntry(_objectDistortions, from, to);
    }

    private void RemapObjectDictionariesAfterCompaction(IReadOnlyList<int> oldToNew)
    {
        RemapObjectDictionary(_gradientStops, oldToNew);
        RemapObjectDictionary(_gradientPathLocalPoints, oldToNew);
        RemapObjectDictionary(_shapeGradientMappingLocalContours, oldToNew);
        RemapObjectDictionary(_pathLocalContours, oldToNew);
        RemapObjectDictionary(_pathBezierLocalContours, oldToNew);
        RemapObjectDictionary(_freehandLocalPoints, oldToNew);
        RemapObjectDictionary(_freehandBezierLocalNodes, oldToNew);
        RemapObjectDictionary(_legacyFreehandBezierNodeCache, oldToNew);
        RemapObjectDictionary(_mixingStrokeLocalSamples, oldToNew);
        RemapObjectDictionary(_mixingStrokeLocalRegions, oldToNew);
        RemapObjectDictionary(_importedSvgSources, oldToNew);
        RemapObjectDictionary(_importedSvgNames, oldToNew);
        RemapObjectDictionary(_bitmapObjects, oldToNew);
        RemapObjectDictionary(_textObjects, oldToNew);
        RemapObjectDictionary(_objectDistortions, oldToNew);
    }

    private static void RemapObjectDictionary<T>(Dictionary<int, T> dictionary, IReadOnlyList<int> oldToNew)
    {
        if (dictionary.Count == 0) return;
        var entries = dictionary.ToArray();
        dictionary.Clear();
        foreach (var (oldIndex, value) in entries)
        {
            if ((uint)oldIndex >= oldToNew.Count) continue;
            var newIndex = oldToNew[oldIndex];
            if (newIndex >= 0) dictionary[newIndex] = value;
        }
    }

    private static void MoveObjectDictionaryEntry<T>(Dictionary<int, T> dictionary, int from, int to)
    {
        if (dictionary.TryGetValue(from, out var value)) dictionary[to] = value;
        else dictionary.Remove(to);
    }

    private void CopyObjectScalarDataFrom(VectorScene source, int from, int to)
    {
        ObjectLayer[to] = source.ObjectLayer[from];
        ObjectKeyframeFrame[to] = source.ObjectKeyframeFrame[from];
        ObjectOrder[to] = source.ObjectOrder[from];
        ObjectSubOrder[to] = source.ObjectSubOrder[from];
        X[to] = source.X[from];
        Y[to] = source.Y[from];
        Width[to] = source.Width[from];
        Height[to] = source.Height[from];
        Angle[to] = source.Angle[from];
        Stroke[to] = source.Stroke[from];
        CurveControlX[to] = source.CurveControlX[from];
        CurveControlY[to] = source.CurveControlY[from];
        CurveControl2X[to] = source.CurveControl2X[from];
        CurveControl2Y[to] = source.CurveControl2Y[from];
        LineEndpointStyles[to] = source.LineEndpointStyles[from];
        LineEndEndpointStyles[to] = source.LineEndEndpointStyles[from];
        ShapeKind[to] = source.ShapeKind[from];
        ShapeVertexCounts[to] = source.GetShapeVertexCount(from);
        AtomCount[to] = source.AtomCount[from];
        Argb[to] = source.Argb[from];
        StrokeArgb[to] = source.StrokeArgb[from];
        FillAutoMergeProtected[to] = source.FillAutoMergeProtected[from];
        LinearGradientEnabled[to] = source.LinearGradientEnabled[from];
        GradientStartArgb[to] = source.GradientStartArgb[from];
        GradientEndArgb[to] = source.GradientEndArgb[from];
        GradientStartX[to] = source.GradientStartX[from];
        GradientStartY[to] = source.GradientStartY[from];
        GradientEndX[to] = source.GradientEndX[from];
        GradientEndY[to] = source.GradientEndY[from];
        GradientKinds[to] = source.GradientKinds[from];
    }

    private void RemovePathDataOutsideObjectCount()
    {
        foreach (var index in _pathLocalContours.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _pathLocalContours.Remove(index);
        }
        foreach (var index in _pathBezierLocalContours.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _pathBezierLocalContours.Remove(index);
        }
    }

    private void RemoveFreehandDataOutsideObjectCount()
    {
        foreach (var index in _freehandLocalPoints.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _freehandLocalPoints.Remove(index);
        }
        foreach (var index in _freehandBezierLocalNodes.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _freehandBezierLocalNodes.Remove(index);
        }
        foreach (var index in _legacyFreehandBezierNodeCache.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _legacyFreehandBezierNodeCache.Remove(index);
        }
    }

    private void RemoveMixingStrokeDataOutsideObjectCount()
    {
        foreach (var index in _mixingStrokeLocalSamples.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _mixingStrokeLocalSamples.Remove(index);
        }
        foreach (var index in _mixingStrokeLocalRegions.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _mixingStrokeLocalRegions.Remove(index);
        }
    }

    private void RemoveImportedSvgDataOutsideObjectCount()
    {
        foreach (var index in _importedSvgSources.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _importedSvgSources.Remove(index);
        }
        foreach (var index in _importedSvgNames.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _importedSvgNames.Remove(index);
        }
        foreach (var index in _bitmapObjects.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _bitmapObjects.Remove(index);
        }
    }

    private void RemoveTextDataOutsideObjectCount()
    {
        foreach (var index in _textObjects.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _textObjects.Remove(index);
        }
    }

    private void RemoveGradientDataOutsideObjectCount()
    {
        foreach (var index in _gradientStops.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _gradientStops.Remove(index);
        }

        foreach (var index in _gradientPathLocalPoints.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _gradientPathLocalPoints.Remove(index);
        }

        foreach (var index in _shapeGradientMappingLocalContours.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _shapeGradientMappingLocalContours.Remove(index);
        }
    }

    private void RemoveObjectDistortionsOutsideObjectCount()
    {
        foreach (var index in _objectDistortions.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _objectDistortions.Remove(index);
        }
    }

    private void ResizeObjectArrays()
    {
        // Packed arrays retain capacity after removals; ObjectCount is the live range.
    }

    private void TrimObjectArrays()
    {
        var objectLayer = ObjectLayer;
        var objectKeyframeFrame = ObjectKeyframeFrame;
        var objectOrder = ObjectOrder;
        var objectSubOrder = ObjectSubOrder;
        var x = X;
        var y = Y;
        var width = Width;
        var height = Height;
        var angleArray = Angle;
        var strokeArray = Stroke;
        var curveControlX = CurveControlX;
        var curveControlY = CurveControlY;
        var curveControl2X = CurveControl2X;
        var curveControl2Y = CurveControl2Y;
        var lineEndpointStyles = LineEndpointStyles;
        var lineEndEndpointStyles = LineEndEndpointStyles;
        var shapeKind = ShapeKind;
        var shapeVertexCounts = ShapeVertexCounts;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
        var fillAutoMergeProtected = FillAutoMergeProtected;
        var linearGradientEnabled = LinearGradientEnabled;
        var gradientKinds = GradientKinds;
        var gradientStartArgb = GradientStartArgb;
        var gradientEndArgb = GradientEndArgb;
        var gradientStartX = GradientStartX;
        var gradientStartY = GradientStartY;
        var gradientEndX = GradientEndX;
        var gradientEndY = GradientEndY;
        Array.Resize(ref objectLayer, ObjectCount);
        Array.Resize(ref objectKeyframeFrame, ObjectCount);
        Array.Resize(ref objectOrder, ObjectCount);
        Array.Resize(ref objectSubOrder, ObjectCount);
        Array.Resize(ref x, ObjectCount);
        Array.Resize(ref y, ObjectCount);
        Array.Resize(ref width, ObjectCount);
        Array.Resize(ref height, ObjectCount);
        Array.Resize(ref angleArray, ObjectCount);
        Array.Resize(ref strokeArray, ObjectCount);
        Array.Resize(ref curveControlX, ObjectCount);
        Array.Resize(ref curveControlY, ObjectCount);
        Array.Resize(ref curveControl2X, ObjectCount);
        Array.Resize(ref curveControl2Y, ObjectCount);
        Array.Resize(ref lineEndpointStyles, ObjectCount);
        Array.Resize(ref lineEndEndpointStyles, ObjectCount);
        Array.Resize(ref shapeKind, ObjectCount);
        Array.Resize(ref shapeVertexCounts, ObjectCount);
        Array.Resize(ref atomCount, ObjectCount);
        Array.Resize(ref argb, ObjectCount);
        Array.Resize(ref strokeArgb, ObjectCount);
        Array.Resize(ref fillAutoMergeProtected, ObjectCount);
        Array.Resize(ref linearGradientEnabled, ObjectCount);
        Array.Resize(ref gradientKinds, ObjectCount);
        Array.Resize(ref gradientStartArgb, ObjectCount);
        Array.Resize(ref gradientEndArgb, ObjectCount);
        Array.Resize(ref gradientStartX, ObjectCount);
        Array.Resize(ref gradientStartY, ObjectCount);
        Array.Resize(ref gradientEndX, ObjectCount);
        Array.Resize(ref gradientEndY, ObjectCount);
        ObjectLayer = objectLayer;
        ObjectKeyframeFrame = objectKeyframeFrame;
        ObjectOrder = objectOrder;
        ObjectSubOrder = objectSubOrder;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Angle = angleArray;
        Stroke = strokeArray;
        CurveControlX = curveControlX;
        CurveControlY = curveControlY;
        CurveControl2X = curveControl2X;
        CurveControl2Y = curveControl2Y;
        LineEndpointStyles = lineEndpointStyles;
        LineEndEndpointStyles = lineEndEndpointStyles;
        ShapeKind = shapeKind;
        ShapeVertexCounts = shapeVertexCounts;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
        FillAutoMergeProtected = fillAutoMergeProtected;
        LinearGradientEnabled = linearGradientEnabled;
        GradientKinds = gradientKinds;
        GradientStartArgb = gradientStartArgb;
        GradientEndArgb = gradientEndArgb;
        GradientStartX = gradientStartX;
        GradientStartY = gradientStartY;
        GradientEndX = gradientEndX;
        GradientEndY = gradientEndY;
    }

    private void EnsureObjectCapacity(int required)
    {
        if (ObjectLayer.Length >= required) return;
        var capacity = Math.Max(required, ObjectLayer.Length == 0 ? 16 : ObjectLayer.Length * 2);
        var objectLayer = ObjectLayer;
        var objectKeyframeFrame = ObjectKeyframeFrame;
        var objectOrder = ObjectOrder;
        var objectSubOrder = ObjectSubOrder;
        var x = X;
        var y = Y;
        var width = Width;
        var height = Height;
        var angleArray = Angle;
        var strokeArray = Stroke;
        var curveControlX = CurveControlX;
        var curveControlY = CurveControlY;
        var curveControl2X = CurveControl2X;
        var curveControl2Y = CurveControl2Y;
        var lineEndpointStyles = LineEndpointStyles;
        var lineEndEndpointStyles = LineEndEndpointStyles;
        var shapeKind = ShapeKind;
        var shapeVertexCounts = ShapeVertexCounts;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
        var fillAutoMergeProtected = FillAutoMergeProtected;
        var linearGradientEnabled = LinearGradientEnabled;
        var gradientKinds = GradientKinds;
        var gradientStartArgb = GradientStartArgb;
        var gradientEndArgb = GradientEndArgb;
        var gradientStartX = GradientStartX;
        var gradientStartY = GradientStartY;
        var gradientEndX = GradientEndX;
        var gradientEndY = GradientEndY;
        Array.Resize(ref objectLayer, capacity);
        Array.Resize(ref objectKeyframeFrame, capacity);
        Array.Resize(ref objectOrder, capacity);
        Array.Resize(ref objectSubOrder, capacity);
        Array.Resize(ref x, capacity);
        Array.Resize(ref y, capacity);
        Array.Resize(ref width, capacity);
        Array.Resize(ref height, capacity);
        Array.Resize(ref angleArray, capacity);
        Array.Resize(ref strokeArray, capacity);
        Array.Resize(ref curveControlX, capacity);
        Array.Resize(ref curveControlY, capacity);
        Array.Resize(ref curveControl2X, capacity);
        Array.Resize(ref curveControl2Y, capacity);
        Array.Resize(ref lineEndpointStyles, capacity);
        Array.Resize(ref lineEndEndpointStyles, capacity);
        Array.Resize(ref shapeKind, capacity);
        Array.Resize(ref shapeVertexCounts, capacity);
        Array.Resize(ref atomCount, capacity);
        Array.Resize(ref argb, capacity);
        Array.Resize(ref strokeArgb, capacity);
        Array.Resize(ref fillAutoMergeProtected, capacity);
        Array.Resize(ref linearGradientEnabled, capacity);
        Array.Resize(ref gradientKinds, capacity);
        Array.Resize(ref gradientStartArgb, capacity);
        Array.Resize(ref gradientEndArgb, capacity);
        Array.Resize(ref gradientStartX, capacity);
        Array.Resize(ref gradientStartY, capacity);
        Array.Resize(ref gradientEndX, capacity);
        Array.Resize(ref gradientEndY, capacity);
        ObjectLayer = objectLayer;
        ObjectKeyframeFrame = objectKeyframeFrame;
        ObjectOrder = objectOrder;
        ObjectSubOrder = objectSubOrder;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Angle = angleArray;
        Stroke = strokeArray;
        CurveControlX = curveControlX;
        CurveControlY = curveControlY;
        CurveControl2X = curveControl2X;
        CurveControl2Y = curveControl2Y;
        LineEndpointStyles = lineEndpointStyles;
        LineEndEndpointStyles = lineEndEndpointStyles;
        ShapeKind = shapeKind;
        ShapeVertexCounts = shapeVertexCounts;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
        FillAutoMergeProtected = fillAutoMergeProtected;
        LinearGradientEnabled = linearGradientEnabled;
        GradientKinds = gradientKinds;
        GradientStartArgb = gradientStartArgb;
        GradientEndArgb = gradientEndArgb;
        GradientStartX = gradientStartX;
        GradientStartY = gradientStartY;
        GradientEndX = gradientEndX;
        GradientEndY = gradientEndY;
    }

    private static LineEndpointStyle NormalizeLineEndpointStyle(LineEndpointStyle endpointStyle)
    {
        return endpointStyle == LineEndpointStyle.Sharp
            ? LineEndpointStyle.Sharp
            : LineEndpointStyle.Round;
    }

    private static ShapeKind InferShapeKind(SizeF size, uint atoms)
    {
        if (atoms <= 8 || Math.Min(size.Width, size.Height) <= 4) return VectorAnimationEngine.ShapeKind.Line;
        return VectorAnimationEngine.ShapeKind.Rectangle;
    }

    private static int DefaultShapeVertexCount(ShapeKind shape)
    {
        return shape switch
        {
            VectorAnimationEngine.ShapeKind.Triangle => 3,
            VectorAnimationEngine.ShapeKind.Polygon => 6,
            VectorAnimationEngine.ShapeKind.Star => 5,
            _ => 0
        };
    }

    private static int NormalizeShapeVertexCount(ShapeKind shape, int value)
    {
        return shape switch
        {
            VectorAnimationEngine.ShapeKind.Triangle => 3,
            VectorAnimationEngine.ShapeKind.Polygon => Math.Clamp(value == 0 ? 6 : value, 3, 64),
            VectorAnimationEngine.ShapeKind.Star => Math.Clamp(value == 0 ? 5 : value, 3, 32),
            _ => 0
        };
    }

    private static ShapeKind RandomShapeKind(ref StressRandom rng)
    {
        return rng.Next(0, 6) switch
        {
            1 => VectorAnimationEngine.ShapeKind.Ellipse,
            2 => VectorAnimationEngine.ShapeKind.Line,
            3 => VectorAnimationEngine.ShapeKind.Triangle,
            4 => VectorAnimationEngine.ShapeKind.Polygon,
            5 => VectorAnimationEngine.ShapeKind.Star,
            _ => VectorAnimationEngine.ShapeKind.Rectangle
        };
    }

    private struct StressRandom(int objectIndex)
    {
        private ulong _state = 0x2D0A2026D1B54A32UL ^ ((ulong)(uint)objectIndex * 0x9E3779B97F4A7C15UL);

        public double NextDouble()
        {
            var value = NextUInt64();
            return (value >> 11) * (1.0 / (1UL << 53));
        }

        public int Next(int minValue, int maxValue)
        {
            if (maxValue <= minValue) return minValue;
            return minValue + (int)(NextUInt64() % (uint)(maxValue - minValue));
        }

        private ulong NextUInt64()
        {
            var value = _state += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

}
