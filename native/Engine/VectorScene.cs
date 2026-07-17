using System.Buffers;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal readonly record struct LineAnchorSplitResult(
    int FirstObjectIndex,
    int SecondObjectIndex,
    PointF Anchor);

internal sealed class VectorScene : ITimelineContext
{
    private const float FillMergeDistanceUnits = 10f;
    private const float LineMergeDistanceUnits = 5f;
    private const float LineMergeDirectionTolerance = 0.0001f;
    private const float LineMergeCollinearityToleranceUnits = DrawingTopologyRules.UnitIntersectionTolerance;
    private static readonly int LineMergeDirectionBucketCount = Math.Max(
        1,
        (int)MathF.Floor(MathF.PI / MathF.Asin(LineMergeDirectionTolerance)));
    private static readonly float LineMergeDirectionBucketRadians = MathF.PI / LineMergeDirectionBucketCount;
    private const float ConnectedStrokeEndpointToleranceUnits = 1.5f;
    private const double ClipperCoordinateScale = 1000d;
    private const double TopologyCutterHalfWidthClipper = 2d;
    private const int TileColumns = 384;
    private const int TileRows = 224;
    private const int OverviewColumns = 120;
    private const int OverviewRows = 70;
    private const int IndexColumns = 256;
    private const int IndexRows = 160;
    private const int DeferredKeyframeUnset = int.MinValue;
    private const int SpatialIndexAppendRebuildThreshold = 4_096;
    public const int DefaultOnionSkinPreviousFrames = 2;
    public const int DefaultOnionSkinNextFrames = 2;
    public const int MaximumOnionSkinFrames = 8;
    private const int OnionSkinPreviousTintArgb = unchecked((int)0xffe0867e);
    private const int OnionSkinNextTintArgb = unchecked((int)0xff6fc3da);

    public int LayerCount { get; private set; }
    public int ObjectCount { get; private set; }
    public long VirtualAtomCount { get; private set; }
    public long GeometryRevision { get; private set; }
    public long SummaryRevision { get; private set; }
    private long _nextObjectOrder;
    private bool _synchronizingKeyframeContent;
    private Func<IReadOnlyList<string>>? _additionalTimelineTargets;
    private Func<string, int, bool>? _externalLayerKeyframeContent;
    private int[]? _deferredAppendKeyframes;
    private int[] _queryActiveKeyframes = [];
    private int _queryActiveFrame = int.MinValue;
    private Dictionary<int, List<int>>? _spatialAppendCells;
    private int _spatialBaseObjectCount;
    private int _spatialPendingObjectCount;
    public int ActiveLayer { get; set; }
    public int EditFrame { get; set; }
    public int FrameCount => Timeline.Tracks.Count == 0 ? AnimationTimeline.DefaultDuration : Timeline.Duration;
    public float StageWidth { get; } = 48000;
    public float StageHeight { get; } = 28000;
    public float MaxHalfExtent { get; private set; } = 128;

    public AnimationTimeline Timeline { get; } = new();
    public IReadOnlyList<string> TimelineTargetIds
    {
        get
        {
            var additionalTargets = AdditionalTimelineTargetIds();
            return additionalTargets.Length == 0
                ? LayerIds
                : LayerIds.Concat(additionalTargets).ToArray();
        }
    }
    public string[] LayerIds { get; private set; } = [];
    public string[] LayerNames { get; private set; } = [];
    public DrawingLayerKind[] LayerKinds { get; private set; } = [];
    public string[] LayerParentIds { get; private set; } = [];
    public string[] LayerMaskIds { get; private set; } = [];
    public bool[] LayerLocked { get; private set; } = [];
    public bool[] LayerVisible { get; private set; } = [];
    public float[] LayerOpacity { get; private set; } = [];
    public int[] LayerColorArgb { get; private set; } = [];
    public bool[] LayerOnionSkin { get; private set; } = [];
    public int OnionSkinPreviousFrames { get; private set; } = DefaultOnionSkinPreviousFrames;
    public int OnionSkinNextFrames { get; private set; } = DefaultOnionSkinNextFrames;
    public bool HasOnionSkinPreviewEnabled
    {
        get
        {
            if (OnionSkinPreviousFrames <= 0 && OnionSkinNextFrames <= 0) return false;
            for (var layer = 0; layer < LayerCount; layer++)
            {
                if (LayerVisible[layer] && LayerOnionSkin[layer]) return true;
            }

            return false;
        }
    }
    public int[] LayerStart { get; private set; } = [];
    public int[] LayerEnd { get; private set; } = [];
    public bool HasLayerEffects => LayerKinds.Any(kind => kind != DrawingLayerKind.Drawing)
        || LayerMaskIds.Any(id => !string.IsNullOrWhiteSpace(id));

    public ushort[] ObjectLayer { get; private set; } = [];
    public int[] ObjectKeyframeFrame { get; private set; } = [];
    public long[] ObjectOrder { get; private set; } = [];
    public double[] ObjectSubOrder { get; private set; } = [];
    public float[] X { get; private set; } = [];
    public float[] Y { get; private set; } = [];
    public float[] Width { get; private set; } = [];
    public float[] Height { get; private set; } = [];
    public float[] Angle { get; private set; } = [];
    public float[] Stroke { get; private set; } = [];
    public float[] CurveControlX { get; private set; } = [];
    public float[] CurveControlY { get; private set; } = [];
    public LineEndpointStyle[] LineEndpointStyles { get; private set; } = [];
    public LineEndpointStyle[] LineEndEndpointStyles { get; private set; } = [];
    public ShapeKind[] ShapeKind { get; private set; } = [];
    public int[] ShapeVertexCounts { get; private set; } = [];
    public uint[] AtomCount { get; private set; } = [];
    public int[] Argb { get; private set; } = [];
    public int[] StrokeArgb { get; private set; } = [];
    public bool[] LinearGradientEnabled { get; private set; } = [];
    public GradientKind[] GradientKinds { get; private set; } = [];
    public int[] GradientStartArgb { get; private set; } = [];
    public int[] GradientEndArgb { get; private set; } = [];
    public float[] GradientStartX { get; private set; } = [];
    public float[] GradientStartY { get; private set; } = [];
    public float[] GradientEndX { get; private set; } = [];
    public float[] GradientEndY { get; private set; } = [];
    private readonly Dictionary<int, GradientStop[]> _gradientStops = new();
    private readonly Dictionary<int, PointF[]> _gradientPathLocalPoints = new();
    private readonly Dictionary<int, PointF[][]> _shapeGradientMappingLocalContours = new();
    private readonly Dictionary<int, PointF[][]> _pathLocalContours = new();
    private readonly Dictionary<int, PointF[]> _freehandLocalPoints = new();

    private readonly record struct MarqueePartAddition(
        bool IsLine,
        int Layer,
        PointF[] Points,
        PointF[][] Contours,
        PointF Start,
        PointF Control,
        PointF End,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle,
        bool Selected)
    {
        public GradientPaintData? GradientPaint { get; init; }
    }

    private readonly record struct FillRegion(PointF[][] Contours, RectangleF Bounds, float Area);

    private readonly record struct PolylinePart(int PartIndex, float StartT, float EndT, PointF[] Points);

    private readonly record struct BoundaryStrokePart(int PartIndex, int ContourIndex, float StartT, float EndT, PointF[] Points);

    private readonly record struct ConnectedStrokePart(DrawingElementHit Hit, PointF Start, PointF End);

    private readonly record struct ClosedFillGraphEdge(int From, int To, PointF[] Points, int ObjectIndex);

    private readonly record struct ClosedFillRawSegment(PointF Start, PointF End, int ObjectIndex);

    private readonly record struct ClosedStrokeFillRegion(PointF[] Contour, int[] BoundaryObjects);

    private readonly record struct LineMergeEndpoint(int CandidateIndex, PointF Point);

    private readonly record struct LineMergeBucketKey(
        ushort Layer,
        int KeyframeFrame,
        int FillArgb,
        int StrokeArgb,
        int StrokeBits,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle,
        int DirectionBucket,
        int CellX,
        int CellY);

    private readonly record struct LineMergePlan(
        int[] Sources,
        int Layer,
        int KeyframeFrame,
        PointF Start,
        PointF End,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        long Order,
        double SubOrder,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle);

    private readonly record struct TopologyCutterPath(Path64 Path, bool Closed);

    private enum MaterializedPartGeometry
    {
        Curve,
        Freehand,
        Polyline,
        Fill
    }

    internal void ConfigureAdditionalTimelineTargets(Func<IReadOnlyList<string>>? provider)
    {
        _additionalTimelineTargets = provider;
        SynchronizeTimelineTracks();
    }

    internal void ConfigureExternalLayerKeyframeContent(Func<string, int, bool>? provider)
    {
        _externalLayerKeyframeContent = provider;
    }

    internal void SynchronizeExternalLayerKeyframeContent()
    {
        SynchronizeAllKeyframeContentKinds();
    }

    private readonly record struct MaterializedPartAddition(
        MaterializedPartGeometry Geometry,
        DrawingElementKey SourceKey,
        int Layer,
        long Order,
        double SubOrder,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        LineEndpointStyle StartEndpointStyle,
        LineEndpointStyle EndEndpointStyle,
        PointF Start,
        PointF Control,
        PointF End,
        PointF[] Points,
        PointF[][] Contours)
    {
        public GradientPaintData? GradientPaint { get; init; }
    }

    private readonly record struct GradientPaintData(
        GradientKind Kind,
        GradientStop[] Stops,
        PointF Start,
        PointF End,
        PointF[] Path,
        PointF[][] ShapeMappingContours);

    private sealed record BrushEraserPlan(
        int Source,
        int KeyframeFrame,
        IReadOnlyList<MaterializedPartAddition> Replacements);

    private sealed record FillOverwritePlan(
        int Source,
        int Layer,
        int KeyframeFrame,
        long Order,
        int FillArgb,
        bool HasGradient,
        GradientKind GradientKind,
        GradientStop[] GradientStops,
        PointF GradientStart,
        PointF GradientEnd,
        PointF[][] ShapeMappingContours,
        IReadOnlyList<FillRegion> RemainingRegions,
        IReadOnlyList<MaterializedPartAddition> BoundaryAdditions,
        IReadOnlyList<double> ReplacementSubOrders,
        uint AtomsPerReplacement);

    private sealed record FillOverwriteCutter(int ObjectIndex, RectangleF Bounds, Paths64 Paths);


    public int TileColumnCount => TileColumns;
    public int TileRowCount => TileRows;
    public int[] TileCount { get; } = new int[TileColumns * TileRows];
    public long[] TileAtoms { get; } = new long[TileColumns * TileRows];
    public int[] TileArgb { get; } = new int[TileColumns * TileRows];
    public int OverviewColumnCount => OverviewColumns;
    public int OverviewRowCount => OverviewRows;
    public int[] OverviewCount { get; } = new int[OverviewColumns * OverviewRows];
    public long[] OverviewAtoms { get; } = new long[OverviewColumns * OverviewRows];
    public int[] OverviewArgb { get; } = new int[OverviewColumns * OverviewRows];

    public int[] CellStart { get; private set; } = new int[IndexColumns * IndexRows + 1];
    public int[] CellObjects { get; private set; } = [];
    public int IndexColumnCount => IndexColumns;
    public int IndexRowCount => IndexRows;

    public VectorScene()
    {
        Timeline.Changed += (_, _) => InvalidateQueryActiveKeyframes();
    }

    public void CreateEmpty(int layers = 1, int frameCount = AnimationTimeline.DefaultDuration)
    {
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
        LayerColorArgb = new int[LayerCount];
        LayerOnionSkin = new bool[LayerCount];
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
        LineEndpointStyles = [];
        LineEndEndpointStyles = [];
        ShapeKind = [];
        ShapeVertexCounts = [];
        AtomCount = [];
        Argb = [];
        StrokeArgb = [];
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
        _freehandLocalPoints.Clear();
        InitializeTimelineFromLayerExposure(frameCount);
        ClearSummaries();
        RebuildSpatialIndex();
    }

    public void Generate(int layers, int objects, long atoms)
    {
        InvalidateQueryActiveKeyframes();
        _deferredAppendKeyframes = null;
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        ObjectCount = Math.Clamp(objects, 1, 1_000_000);
        VirtualAtomCount = Math.Max(atoms, ObjectCount * 3L);
        _nextObjectOrder = ObjectCount;
        ActiveLayer = 0;
        EditFrame = 0;
        MaxHalfExtent = 128;
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
        LayerColorArgb = new int[LayerCount];
        LayerOnionSkin = new bool[LayerCount];
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
        LineEndpointStyles = new LineEndpointStyle[ObjectCount];
        LineEndEndpointStyles = new LineEndpointStyle[ObjectCount];
        ShapeKind = GC.AllocateUninitializedArray<ShapeKind>(ObjectCount);
        ShapeVertexCounts = GC.AllocateUninitializedArray<int>(ObjectCount);
        AtomCount = GC.AllocateUninitializedArray<uint>(ObjectCount);
        Argb = GC.AllocateUninitializedArray<int>(ObjectCount);
        StrokeArgb = GC.AllocateUninitializedArray<int>(ObjectCount);
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
        _freehandLocalPoints.Clear();

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

                CurveControlX[i] = X[i];
                CurveControlY[i] = Y[i];
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
        int shapeVertexCount = 0)
    {
        var targetLayer = ResolveObjectLayer(layer);
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
        CurveControlX[index] = VectorUnits.Quantize(curveControl.X);
        CurveControlY[index] = VectorUnits.Quantize(curveControl.Y);
        LineEndpointStyles[index] = NormalizeLineEndpointStyle(startEndpointStyle);
        LineEndEndpointStyles[index] = NormalizeLineEndpointStyle(endEndpointStyle);
        AtomCount[index] = Math.Max(3, atoms);
        Argb[index] = colorArgb;
        StrokeArgb[index] = strokeColorArgb;
        LinearGradientEnabled[index] = false;
        GradientKinds[index] = GradientKind.Solid;
        GradientStartArgb[index] = colorArgb;
        GradientEndArgb[index] = colorArgb;
        GradientStartX[index] = X[index] - Width[index] * 0.5f;
        GradientStartY[index] = Y[index];
        GradientEndX[index] = X[index] + Width[index] * 0.5f;
        GradientEndY[index] = Y[index];
        MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[index], Height[index]) * 0.5f);
        return index;
    }

    internal int AppendPackedObjects(PackedSceneObject[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0) return ObjectCount;
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
                LineEndpointStyles[index] = NormalizeLineEndpointStyle(item.StartEndpointStyle);
                LineEndEndpointStyles[index] = NormalizeLineEndpointStyle(item.EndEndpointStyle);
                ShapeKind[index] = item.Shape;
                ShapeVertexCounts[index] = NormalizeShapeVertexCount(item.Shape, item.ShapeVertexCount);
                AtomCount[index] = Math.Max(3, item.Atoms);
                Argb[index] = item.Argb;
                StrokeArgb[index] = item.StrokeArgb;
                var gradientKind = item.GradientKind == GradientKind.Solid && item.LinearGradientEnabled
                    ? GradientKind.Linear
                    : item.GradientKind;
                LinearGradientEnabled[index] = gradientKind != GradientKind.Solid && SupportsGradient(item.Shape);
                GradientKinds[index] = LinearGradientEnabled[index] ? gradientKind : GradientKind.Solid;
                GradientStartArgb[index] = LinearGradientEnabled[index] ? item.GradientStartArgb : item.Argb;
                GradientEndArgb[index] = LinearGradientEnabled[index] ? item.GradientEndArgb : item.Argb;
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
        return AddCurveSegment(
            layer,
            start,
            Midpoint(start, end),
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
        var index = AppendCurveSegment(
            layer,
            start,
            control,
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

    public void TransformObjects(IEnumerable<int> objectIndices, Func<PointF, PointF> transform, bool rebuildGeometryIndex = true)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        ArgumentNullException.ThrowIfNull(transform);
        var targets = objectIndices
            .Distinct()
            .Where(objectIndex => (uint)objectIndex < ObjectCount)
            .ToArray();
        var linkedBoundaries = CaptureLinkedFillBoundariesForTransform(targets);
        foreach (var objectIndex in targets)
        {
            TransformObject(objectIndex, transform);
        }

        UpdateFillBoundaryLineLinks(linkedBoundaries, rebuildGeometryIndex: false);
        if (!rebuildGeometryIndex)
        {
            if (targets.Length > 0) GeometryRevision++;
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
        if (!HasGradient(objectIndex)) return;
        GradientStartX[objectIndex] = VectorUnits.Quantize(GradientStartX[objectIndex] + dx);
        GradientStartY[objectIndex] = VectorUnits.Quantize(GradientStartY[objectIndex] + dy);
        GradientEndX[objectIndex] = VectorUnits.Quantize(GradientEndX[objectIndex] + dx);
        GradientEndY[objectIndex] = VectorUnits.Quantize(GradientEndY[objectIndex] + dy);
    }

    public bool HasGradient(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && LinearGradientEnabled.Length > objectIndex
            && LinearGradientEnabled[objectIndex]
            && GradientKinds.Length > objectIndex
            && GradientKinds[objectIndex] is GradientKind.Linear or GradientKind.Radial or GradientKind.ShapeRadial
            && (GradientKinds[objectIndex] != GradientKind.ShapeRadial || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line)
            && SupportsGradient(ShapeKind[objectIndex]);
    }

    public bool HasLinearGradient(int objectIndex) => HasGradient(objectIndex) && GradientKinds[objectIndex] == GradientKind.Linear;

    public GradientKind GetGradientKind(int objectIndex) => HasGradient(objectIndex) ? GradientKinds[objectIndex] : GradientKind.Solid;

    public PointF GetGradientStart(int objectIndex) => new(GradientStartX[objectIndex], GradientStartY[objectIndex]);

    public PointF GetGradientEnd(int objectIndex) => new(GradientEndX[objectIndex], GradientEndY[objectIndex]);

    public GradientStop[] GetGradientStops(int objectIndex)
    {
        if (!HasGradient(objectIndex)) return [];
        return _gradientStops.TryGetValue(objectIndex, out var stops)
            ? stops.ToArray()
            : [new GradientStop(0, GradientStartArgb[objectIndex]), new GradientStop(1, GradientEndArgb[objectIndex])];
    }

    public bool HasGradientPath(int objectIndex) => HasGradient(objectIndex)
        && GradientKinds[objectIndex] == GradientKind.Linear
        && _gradientPathLocalPoints.TryGetValue(objectIndex, out var points)
        && points.Length >= 2;

    public bool TryGetGradientPathWorldPoints(int objectIndex, out PointF[] points)
    {
        if (!HasGradientPath(objectIndex) || !_gradientPathLocalPoints.TryGetValue(objectIndex, out var localPoints))
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = new PointF[localPoints.Length];
        for (var index = 0; index < localPoints.Length; index++)
        {
            points[index] = new PointF(X[objectIndex] + localPoints[index].X, Y[objectIndex] + localPoints[index].Y);
        }

        return true;
    }

    public void SetGradientPath(int objectIndex, IReadOnlyList<PointF> worldPoints)
    {
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Path
            || !HasGradient(objectIndex)
            || GradientKinds[objectIndex] != GradientKind.Linear)
        {
            ClearGradientPath(objectIndex);
            return;
        }

        var points = NormalizeFreehandPoints(worldPoints);
        if (points.Length < 2)
        {
            ClearGradientPath(objectIndex);
            return;
        }

        if (GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(points, out var fallbackAxis))
        {
            GradientStartX[objectIndex] = VectorUnits.Quantize(fallbackAxis.Start.X);
            GradientStartY[objectIndex] = VectorUnits.Quantize(fallbackAxis.Start.Y);
            GradientEndX[objectIndex] = VectorUnits.Quantize(fallbackAxis.End.X);
            GradientEndY[objectIndex] = VectorUnits.Quantize(fallbackAxis.End.Y);
            ClearGradientPath(objectIndex);
            return;
        }

        _gradientPathLocalPoints[objectIndex] = points
            .Select(point => new PointF(
                VectorUnits.Quantize(point.X - X[objectIndex]),
                VectorUnits.Quantize(point.Y - Y[objectIndex])))
            .ToArray();
    }

    public void ClearGradientPath(int objectIndex) => _gradientPathLocalPoints.Remove(objectIndex);

    public bool TryGetShapeGradientMappingWorldContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex >= ObjectCount
            || GetGradientKind(objectIndex) != GradientKind.ShapeRadial
            || !_shapeGradientMappingLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }

        contours = new PointF[localContours.Length][];
        for (var contourIndex = 0; contourIndex < localContours.Length; contourIndex++)
        {
            var local = localContours[contourIndex];
            var world = new PointF[local.Length];
            for (var pointIndex = 0; pointIndex < local.Length; pointIndex++)
            {
                world[pointIndex] = LocalToWorld(objectIndex, local[pointIndex].X, local[pointIndex].Y);
            }
            contours[contourIndex] = world;
        }

        return contours.Length > 0;
    }

    internal bool TryGetShapeGradientMappingLocalContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex < ObjectCount
            && GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            && _shapeGradientMappingLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = localContours;
            return contours.Length > 0;
        }

        contours = Array.Empty<PointF[]>();
        return false;
    }

    public PointF[][] GetShapeGradientMappingContours(int objectIndex)
    {
        return TryGetShapeGradientMappingWorldContours(objectIndex, out var contours)
            ? contours
            : GetObjectBoundaryContours(objectIndex);
    }

    public void SetShapeGradientMapping(int objectIndex, IReadOnlyList<PointF[]> worldContours)
    {
        if ((uint)objectIndex >= ObjectCount || GetGradientKind(objectIndex) != GradientKind.ShapeRadial)
        {
            ClearShapeGradientMapping(objectIndex);
            return;
        }

        var contours = NormalizePathContours(worldContours);
        if (contours.Length == 0)
        {
            ClearShapeGradientMapping(objectIndex);
            return;
        }

        _shapeGradientMappingLocalContours[objectIndex] = contours
            .Select(contour => contour
                .Select(point => VectorUnits.Quantize(WorldToLocal(objectIndex, point)))
                .ToArray())
            .ToArray();
    }

    public void ClearShapeGradientMapping(int objectIndex) => _shapeGradientMappingLocalContours.Remove(objectIndex);

    public float EstimateGradientPathStrokeWidth(int objectIndex)
    {
        if (!TryGetGradientPathWorldPoints(objectIndex, out var points)) return 0;
        var length = 0f;
        for (var index = 1; index < points.Length; index++) length += Distance(points[index - 1], points[index]);
        if (length <= 0.001f) return 0;

        var area = Math.Abs(CompoundContourArea(FillWorldContours(objectIndex)));
        return Math.Max(VectorUnits.FromPixels(0.5f), area / length);
    }

    private GradientPaintData? CaptureGradientPaint(int objectIndex)
    {
        return HasGradient(objectIndex)
            ? new GradientPaintData(
                GradientKinds[objectIndex],
                GetGradientStops(objectIndex),
                GetGradientStart(objectIndex),
                GetGradientEnd(objectIndex),
                TryGetGradientPathWorldPoints(objectIndex, out var path) ? path : Array.Empty<PointF>(),
                TryGetShapeGradientMappingWorldContours(objectIndex, out var mappingContours)
                    ? mappingContours
                    : Array.Empty<PointF[]>())
            : null;
    }

    private void ApplyGradientPaint(int objectIndex, GradientPaintData? gradientPaint)
    {
        if (gradientPaint is not { } paint) return;
        SetGradientPaint(objectIndex, paint.Kind, paint.Stops, paint.Start, paint.End);
        if (paint.Path.Length > 1) SetGradientPath(objectIndex, paint.Path);
        if (paint.ShapeMappingContours.Length > 0) SetShapeGradientMapping(objectIndex, paint.ShapeMappingContours);
    }

    public void SetLinearGradient(int objectIndex, Color startColor, Color endColor, PointF? start = null, PointF? end = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.Linear,
            [new GradientStop(0, startColor), new GradientStop(1, endColor)],
            start,
            end);
    }

    public void SetRadialGradient(int objectIndex, Color centerColor, Color edgeColor, PointF? center = null, PointF? radiusPoint = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.Radial,
            [new GradientStop(0, centerColor), new GradientStop(1, edgeColor)],
            center,
            radiusPoint);
    }

    public void SetShapeRadialGradient(int objectIndex, Color centerColor, Color edgeColor, PointF? center = null)
    {
        SetGradientPaint(
            objectIndex,
            GradientKind.ShapeRadial,
            [new GradientStop(0, centerColor), new GradientStop(1, edgeColor)],
            center);
    }

    public void SetGradientPaint(
        int objectIndex,
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        PointF? start = null,
        PointF? end = null)
    {
        if ((uint)objectIndex >= ObjectCount
            || !SupportsGradient(ShapeKind[objectIndex])
            || kind == GradientKind.ShapeRadial && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            return;
        }
        if (kind == GradientKind.Solid)
        {
            DisableLinearGradient(objectIndex);
            return;
        }

        var normalizedStops = NormalizeGradientStops(stops);
        PointF defaultStart;
        PointF defaultEnd;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            // A line's width/angle describe its bounds, not the direction of a
            // curved stroke. Use its real endpoints so vertical and diagonal
            // lines do not collapse a default linear gradient into one color.
            var curve = LineCurve(objectIndex);
            if (kind == GradientKind.Radial)
            {
                defaultStart = QuadraticPoint(curve.Start, curve.Control, curve.End, 0.5f);
                defaultEnd = curve.End;
            }
            else
            {
                defaultStart = curve.Start;
                defaultEnd = curve.End;
            }
        }
        else if (kind is GradientKind.Radial or GradientKind.ShapeRadial)
        {
            defaultStart = new PointF(X[objectIndex], Y[objectIndex]);
            defaultEnd = new PointF(X[objectIndex] + Math.Max(Width[objectIndex], Height[objectIndex]) * 0.5f, Y[objectIndex]);
        }
        else
        {
            defaultStart = new PointF(X[objectIndex] - Width[objectIndex] * 0.5f, Y[objectIndex]);
            defaultEnd = new PointF(X[objectIndex] + Width[objectIndex] * 0.5f, Y[objectIndex]);
        }

        var resolvedStart = start ?? defaultStart;
        var resolvedEnd = end ?? defaultEnd;
        if (kind == GradientKind.ShapeRadial
            && end is null
            && GradientPaintUtilities.TryFindShapeBoundaryPoint(
                GetObjectBoundaryContours(objectIndex),
                resolvedStart,
                new PointF(1f, 0f),
                out var shapeBoundary))
        {
            resolvedEnd = shapeBoundary;
        }

        LinearGradientEnabled[objectIndex] = true;
        GradientKinds[objectIndex] = kind;
        if (kind != GradientKind.Linear) ClearGradientPath(objectIndex);
        if (kind != GradientKind.ShapeRadial) ClearShapeGradientMapping(objectIndex);
        _gradientStops[objectIndex] = normalizedStops;
        GradientStartArgb[objectIndex] = normalizedStops[0].Argb;
        GradientEndArgb[objectIndex] = normalizedStops[^1].Argb;
        SetLinearGradientEndpoints(objectIndex, resolvedStart, resolvedEnd);
    }

    public void SetLinearGradientColors(int objectIndex, Color startColor, Color endColor)
    {
        if (!HasGradient(objectIndex)) return;
        var stops = GetGradientStops(objectIndex);
        stops[0] = new GradientStop(stops[0].Position, startColor);
        stops[^1] = new GradientStop(stops[^1].Position, endColor);
        SetGradientStops(objectIndex, stops);
    }

    public void SetGradientStops(int objectIndex, IReadOnlyList<GradientStop> stops)
    {
        if (!HasGradient(objectIndex)) return;
        var normalizedStops = NormalizeGradientStops(stops);
        _gradientStops[objectIndex] = normalizedStops;
        GradientStartArgb[objectIndex] = normalizedStops[0].Argb;
        GradientEndArgb[objectIndex] = normalizedStops[^1].Argb;
    }

    public void SetLinearGradientEndpoints(int objectIndex, PointF start, PointF end)
    {
        if ((uint)objectIndex >= ObjectCount) return;
        GradientStartX[objectIndex] = VectorUnits.Quantize(start.X);
        GradientStartY[objectIndex] = VectorUnits.Quantize(start.Y);
        GradientEndX[objectIndex] = VectorUnits.Quantize(end.X);
        GradientEndY[objectIndex] = VectorUnits.Quantize(end.Y);
    }

    public void DisableLinearGradient(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return;
        LinearGradientEnabled[objectIndex] = false;
        GradientKinds[objectIndex] = GradientKind.Solid;
        _gradientStops.Remove(objectIndex);
        ClearGradientPath(objectIndex);
        ClearShapeGradientMapping(objectIndex);
    }

    private static GradientStop[] NormalizeGradientStops(IReadOnlyList<GradientStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var normalized = stops
            .Select(stop => new GradientStop(Math.Clamp(stop.Position, 0f, 1f), stop.Argb))
            .OrderBy(stop => stop.Position)
            .GroupBy(stop => stop.Position)
            .Select(group => group.Last())
            .ToList();
        if (normalized.Count == 0) normalized.Add(new GradientStop(0, Color.White));
        if (normalized.Count == 1) normalized.Add(new GradientStop(1, normalized[0].Argb));
        if (normalized[0].Position > 0) normalized.Insert(0, new GradientStop(0, normalized[0].Argb));
        if (normalized[^1].Position < 1) normalized.Add(new GradientStop(1, normalized[^1].Argb));
        return normalized.ToArray();
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
        foreach (var objectIndex in targets)
        {
            var shape = ShapeKind[objectIndex];
            if (shape is VectorAnimationEngine.ShapeKind.Rectangle
                or VectorAnimationEngine.ShapeKind.Ellipse
                or VectorAnimationEngine.ShapeKind.Triangle
                or VectorAnimationEngine.ShapeKind.Polygon
                or VectorAnimationEngine.ShapeKind.Star)
            {
                ConvertPrimitiveToTransformedPath(objectIndex, transform);
            }
            else
            {
                TransformObject(objectIndex, transform);
            }
        }

        UpdateFillBoundaryLineLinks(linkedBoundaries, rebuildGeometryIndex: false);
        if (!rebuildGeometryIndex)
        {
            if (targets.Length > 0) GeometryRevision++;
            return;
        }
        RebuildGeometryIndex();
        RebuildSummaries();
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
        }

        RebuildSummaries();
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
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(objectIndex);
            SetLineCurve(objectIndex, transform(curve.Start), transform(curve.Control), transform(curve.End));
            if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
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
        var boundary = ShapeBoundary(objectIndex);
        var count = boundary.Length > 1 && Distance(boundary[0], boundary[^1]) <= DrawingTopologyRules.MinStrokeSegmentUnits
            ? boundary.Length - 1
            : boundary.Length;
        if (count < 3) return;

        var contour = new PointF[count];
        for (var index = 0; index < count; index++) contour[index] = transform(boundary[index]);
        ShapeKind[objectIndex] = VectorAnimationEngine.ShapeKind.Path;
        SetPathContours(objectIndex, new[] { contour });
        if (hasGradient) SetLinearGradientEndpoints(objectIndex, transform(gradientStart), transform(gradientEnd));
        if (shapeGradientMapping.Length > 0)
        {
            SetShapeGradientMapping(
                objectIndex,
                shapeGradientMapping.Select(source => source.Select(transform).ToArray()).ToArray());
        }
    }

    private void SetLineCurve(int objectIndex, PointF start, PointF control, PointF end)
    {
        var center = Midpoint(start, end);
        X[objectIndex] = VectorUnits.Quantize(center.X);
        Y[objectIndex] = VectorUnits.Quantize(center.Y);
        Width[objectIndex] = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, VectorUnits.Quantize(Distance(start, end)));
        Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), Height[objectIndex]);
        Angle[objectIndex] = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        CurveControlX[objectIndex] = VectorUnits.Quantize(control.X);
        CurveControlY[objectIndex] = VectorUnits.Quantize(control.Y);
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
        if (gradientPath.Length > 1) SetGradientPath(objectIndex, gradientPath);
        if (shapeGradientMapping.Length > 0) SetShapeGradientMapping(objectIndex, shapeGradientMapping);
    }

    private void SetFreehandPoints(int objectIndex, PointF[] points)
    {
        if (points.Length == 0) return;
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
            control,
            startEndpointStyle,
            endEndpointStyle ?? startEndpointStyle);
        var margin = Math.Max(stroke * 0.5f, 1);
        var curveExtent = Math.Max(
            Math.Max(Math.Abs(start.X - X[index]), Math.Abs(end.X - X[index])),
            Math.Max(Math.Abs(start.Y - Y[index]), Math.Abs(end.Y - Y[index])));
        curveExtent = Math.Max(curveExtent, Math.Max(Math.Abs(control.X - X[index]), Math.Abs(control.Y - Y[index]))) + margin;
        MaxHalfExtent = Math.Max(MaxHalfExtent, curveExtent);
        return index;
    }

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
            var contours = continuous && brushShape.IsTraditionalBrush && brushShape.IsRadiallySymmetric
                ? FreehandStrokeProcessor.CreateBrushOutlines(points, diameter)
                : CreateBrushSweepContours(
                    points,
                    diameter,
                    brushShape.NormalizedContour(layerDefinition.Threshold),
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
        var previewProfile = FreehandStrokeProcessor.CreatePressurePreview(samples, baseDiameter, smoothing);
        if (previewProfile.Length == 0) return Array.Empty<int>();
        PressureBrushPoint[]? processedProfile = null;

        var added = new List<int>();
        foreach (var layerDefinition in brushShape.Layers)
        {
            var normalizedContour = brushShape.NormalizedContour(layerDefinition.Threshold);
            var contours = continuous && brushShape.IsRadiallySymmetric
                ? FreehandStrokeProcessor.CreateVariableWidthBrushOutlines(
                    previewProfile,
                    ContourRadiusScale(normalizedContour))
                : CreatePressureBrushSweepContours(
                    processedProfile ??= FreehandStrokeProcessor.ProcessPressure(
                        samples,
                        baseDiameter,
                        smoothing,
                        simplifyTolerance),
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
        var geometryRevision = GeometryRevision;
        var summaryRevision = SummaryRevision;
        foreach (var objectIndex in objectIndices)
        {
            AppendObjectToSpatialIndex(objectIndex);
            AddObjectToSummariesIncremental(objectIndex, Color.FromArgb(Argb[objectIndex]));
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
        bool continuous = true)
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
            brushShape.StampSpacingScale);
        var cutter = ToClipperPaths(sweepContours);
        if (cutter.Count == 0) return false;

        var plans = new List<BrushEraserPlan>();
        for (var index = 0; index < ObjectCount; index++)
        {
            if (!IsObjectActive(index, frame)) continue;
            if (TryBuildBrushEraserPlan(index, cutter, eraseLines, eraseFills, out var plan)) plans.Add(plan);
        }

        if (plans.Count == 0) return false;

        RemoveObjects(plans.Select(plan => plan.Source));
        foreach (var plan in plans)
        {
            foreach (var replacement in plan.Replacements)
            {
                var index = AppendMaterializedPart(replacement);
                if (index < 0) continue;
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
        bool eraseLines,
        bool eraseFills,
        out BrushEraserPlan plan)
    {
        plan = null!;
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
        var additions = new List<MaterializedPartAddition>();

        if (hasFill)
        {
            var regions = changedFill
                ? fillRegions
                : ExecuteFillRegions(ToClipperPaths(FillWorldContours(source)), new Paths64());
            foreach (var region in regions)
            {
                additions.Add(FillMaterialization(
                    DrawingElementKey.None,
                    layer,
                    order,
                    0,
                    fillColor,
                    Color.Transparent,
                    0,
                    region.Contours,
                    gradientPaint));
            }
        }

        if (hasStroke)
        {
            if (changedStroke)
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

        var subOrders = additions.Count == 1
            ? new[] { ObjectSubOrder[source] }
            : ReplacementSubOrders(source, additions.Count);
        var atoms = Math.Max(3u, AtomCount[source] / (uint)Math.Max(1, additions.Count));
        for (var index = 0; index < additions.Count; index++)
        {
            additions[index] = additions[index] with { Atoms = atoms, SubOrder = subOrders[index] };
        }

        plan = new BrushEraserPlan(source, ObjectKeyframeFrame[source], additions);
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
                curve.Control,
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
            var radius = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), Stroke[objectIndex]) * 0.5d;
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
        float stampSpacingScale = 1f)
    {
        if (centerline.Count == 0 || normalizedContour.Count < 3) return Array.Empty<PointF[]>();
        try
        {
            var stamps = new Paths64();
            var radius = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), diameter) * 0.5f;
            frequency = Math.Clamp(frequency, 1, 24);
            stampSpacingScale = Math.Clamp(stampSpacingScale, 0.1f, 1f);
            var spacing = Math.Max(
                VectorUnits.StrokePointsToUnits(0.25f),
                diameter / frequency * stampSpacingScale);
            var lastStamp = PointF.Empty;
            var hasLastStamp = false;

            void AddStamp(PointF center)
            {
                if (hasLastStamp && Distance(center, lastStamp) < 0.001f) return;

                var stamp = new PointF[normalizedContour.Count];
                for (var pointIndex = 0; pointIndex < stamp.Length; pointIndex++)
                {
                    stamp[pointIndex] = VectorUnits.Quantize(new PointF(
                        center.X + normalizedContour[pointIndex].X * radius,
                        center.Y + normalizedContour[pointIndex].Y * radius));
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

            return stamps.Count == 0 ? Array.Empty<PointF[]>() : FromClipperPaths(Clipper.Union(stamps, FillRule.NonZero));
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

                var radius = Math.Max(VectorUnits.StrokePointsToUnits(0.5f) * 0.6f, sample.Diameter) * 0.5f;
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
                        VectorUnits.StrokePointsToUnits(0.25f),
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

    internal int AppendFreehandStroke(int layer, PointF[] points, float stroke, Color color, uint atoms)
    {
        if (points.Length == 0) return -1;

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

        stroke = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), stroke);
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

    public void UpdateFreehandStrokeWidth(int objectIndex, float stroke, bool rebuildGeometryIndex = true)
    {
        if ((uint)objectIndex >= ObjectCount || !IsFreehandShape(ShapeKind[objectIndex]) || !TryGetFreehandLocalPoints(objectIndex, out var points)) return;
        stroke = Math.Max(VectorUnits.StrokePointsToUnits(0.5f), stroke);
        Stroke[objectIndex] = stroke;

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
        RemoveGradientDataOutsideObjectCount();
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

            if (write != read) CopyObjectData(read, write);
            write++;
        }

        ObjectCount = write;
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - removedAtoms);
        ResizeObjectArrays();
        RemovePathDataOutsideObjectCount();
        RemoveFreehandDataOutsideObjectCount();
        RemoveGradientDataOutsideObjectCount();
        RebuildGeometryIndex();
        RebuildSummaries();
        foreach (var (layer, frame) in affectedKeyframes) SynchronizeKeyframeContentKind(layer, frame);
        return removeCount;
    }

    public void RebuildGeometryIndex()
    {
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

    public VectorSceneSnapshot CreateSnapshot()
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
            LayerColorArgb = LayerColorArgb.ToArray(),
            LayerOnionSkin = LayerOnionSkin.ToArray(),
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
            LineEndpointStyles = LineEndpointStyles[..ObjectCount],
            LineEndEndpointStyles = LineEndEndpointStyles[..ObjectCount],
            ShapeKind = ShapeKind[..ObjectCount],
            ShapeVertexCounts = ShapeVertexCounts[..ObjectCount],
            AtomCount = AtomCount[..ObjectCount],
            Argb = Argb[..ObjectCount],
            StrokeArgb = StrokeArgb[..ObjectCount],
            LinearGradientEnabled = LinearGradientEnabled[..ObjectCount],
            GradientKinds = GradientKinds[..ObjectCount],
            GradientStartArgb = GradientStartArgb[..ObjectCount],
            GradientEndArgb = GradientEndArgb[..ObjectCount],
            GradientStartX = GradientStartX[..ObjectCount],
            GradientStartY = GradientStartY[..ObjectCount],
            GradientEndX = GradientEndX[..ObjectCount],
            GradientEndY = GradientEndY[..ObjectCount],
            GradientStops = _gradientStops.ToDictionary(item => item.Key, item => item.Value.ToArray()),
            GradientPathLocalPoints = _gradientPathLocalPoints.ToDictionary(item => item.Key, item => item.Value.ToArray()),
            ShapeGradientMappingLocalContours = _shapeGradientMappingLocalContours.ToDictionary(
                item => item.Key,
                item => CloneContours(item.Value)),
            Timeline = Timeline.CreateSnapshot(),
            PathLocalContours = _pathLocalContours.ToDictionary(item => item.Key, item => CloneContours(item.Value)),
            FreehandLocalPoints = _freehandLocalPoints.ToDictionary(item => item.Key, item => item.Value.ToArray())
        };
    }

    public void RestoreSnapshot(VectorSceneSnapshot snapshot)
    {
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
        LayerColorArgb = NormalizeLayerColors(snapshot.LayerColorArgb, LayerCount);
        LayerOnionSkin = NormalizeLayerOnionSkin(snapshot.LayerOnionSkin, LayerCount);
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
        LineEndpointStyles = snapshot.LineEndpointStyles.Length == ObjectCount
            ? snapshot.LineEndpointStyles.Select(NormalizeLineEndpointStyle).ToArray()
            : Enumerable.Repeat(LineEndpointStyle.Round, ObjectCount).ToArray();
        LineEndEndpointStyles = snapshot.LineEndEndpointStyles.Length == ObjectCount
            ? snapshot.LineEndEndpointStyles.Select(NormalizeLineEndpointStyle).ToArray()
            : LineEndpointStyles.ToArray();
        ShapeKind = snapshot.ShapeKind.ToArray();
        ShapeVertexCounts = snapshot.ShapeVertexCounts.Length == ObjectCount
            ? snapshot.ShapeVertexCounts
                .Select((value, index) => NormalizeShapeVertexCount(ShapeKind[index], value))
                .ToArray()
            : ShapeKind.Select(DefaultShapeVertexCount).ToArray();
        AtomCount = snapshot.AtomCount.ToArray();
        Argb = snapshot.Argb.ToArray();
        StrokeArgb = snapshot.StrokeArgb.ToArray();
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

        _freehandLocalPoints.Clear();
        foreach (var item in snapshot.FreehandLocalPoints)
        {
            if ((uint)item.Key >= ObjectCount) continue;
            _freehandLocalPoints[item.Key] = item.Value.ToArray();
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
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    public bool IsLayerActive(int layer, int frame)
    {
        return layer >= 0
            && layer < LayerCount
            && GetLayerKind(layer) != DrawingLayerKind.Folder
            && IsLayerEffectivelyVisible(layer)
            && Timeline.EvaluateTargetExposure(LayerIds[layer], frame).HasContent;
    }

    public DrawingLayerKind GetLayerKind(int layer)
    {
        return (uint)layer < LayerKinds.Length ? LayerKinds[layer] : DrawingLayerKind.Drawing;
    }

    public int GetLayerParentIndex(int layer)
    {
        if ((uint)layer >= LayerCount || layer >= LayerParentIds.Length) return -1;
        var parentId = LayerParentIds[layer];
        if (string.IsNullOrWhiteSpace(parentId)) return -1;
        var parent = Array.IndexOf(LayerIds, parentId);
        return parent >= 0 && GetLayerKind(parent) == DrawingLayerKind.Folder ? parent : -1;
    }

    public int GetLayerDepth(int layer)
    {
        if ((uint)layer >= LayerCount) return 0;
        var depth = 0;
        var current = layer;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            var parent = GetLayerParentIndex(current);
            if (parent < 0) break;
            depth++;
            current = parent;
        }

        return depth;
    }

    public bool IsLayerEffectivelyVisible(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        var current = layer;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            if (!LayerVisible[current]) return false;
            var parent = GetLayerParentIndex(current);
            if (parent < 0) return true;
            current = parent;
        }

        return false;
    }

    public bool IsLayerEffectivelyLocked(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        var current = layer;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            if (current < LayerLocked.Length && LayerLocked[current]) return true;
            var parent = GetLayerParentIndex(current);
            if (parent < 0) return false;
            current = parent;
        }

        return true;
    }

    public bool IsObjectSelectable(int objectIndex, int frame)
    {
        return (uint)objectIndex < ObjectCount
            && IsObjectActive(objectIndex, frame)
            && !IsLayerEffectivelyLocked(ObjectLayer[objectIndex]);
    }

    public bool ShouldRenderLayerContent(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        return GetLayerKind(layer) switch
        {
            DrawingLayerKind.Folder => false,
            DrawingLayerKind.Mask => !IsLayerEffectivelyLocked(layer),
            _ => true
        };
    }

    public bool TryGetMaskLayerIndex(int layer, out int maskLayer)
    {
        maskLayer = -1;
        if ((uint)layer >= LayerCount
            || GetLayerKind(layer) != DrawingLayerKind.Drawing
            || layer >= LayerMaskIds.Length
            || string.IsNullOrWhiteSpace(LayerMaskIds[layer]))
        {
            return false;
        }

        var candidate = Array.IndexOf(LayerIds, LayerMaskIds[layer]);
        if (candidate < 0 || GetLayerKind(candidate) != DrawingLayerKind.Mask) return false;
        maskLayer = candidate;
        return true;
    }

    public int GetMaskContentLayerIndex(int maskLayer)
    {
        if ((uint)maskLayer >= LayerCount || GetLayerKind(maskLayer) != DrawingLayerKind.Mask) return -1;
        var maskId = LayerIds[maskLayer];
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) return layer;
        }

        return -1;
    }

    public bool SetLayerMask(int contentLayer, int maskLayer)
    {
        if ((uint)contentLayer >= LayerCount
            || (uint)maskLayer >= LayerCount
            || GetLayerKind(contentLayer) != DrawingLayerKind.Drawing
            || GetLayerKind(maskLayer) != DrawingLayerKind.Mask)
        {
            return false;
        }

        var maskId = LayerIds[maskLayer];
        var changed = false;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (layer == contentLayer || !string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) continue;
            LayerMaskIds[layer] = string.Empty;
            changed = true;
        }

        if (!string.Equals(LayerMaskIds[contentLayer], maskId, StringComparison.Ordinal))
        {
            LayerMaskIds[contentLayer] = maskId;
            changed = true;
        }

        var parentId = LayerParentIds[maskLayer];
        if (!string.Equals(LayerParentIds[contentLayer], parentId, StringComparison.Ordinal))
        {
            LayerParentIds[contentLayer] = parentId;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool ClearLayerMask(int contentLayer)
    {
        if ((uint)contentLayer >= LayerCount || GetLayerKind(contentLayer) != DrawingLayerKind.Drawing) return false;
        if (string.IsNullOrWhiteSpace(LayerMaskIds[contentLayer])) return false;
        LayerMaskIds[contentLayer] = string.Empty;
        InvalidateQueryActiveKeyframes();
        return true;
    }

    public bool ClearMaskLayerLinks(int maskLayer)
    {
        if ((uint)maskLayer >= LayerCount || GetLayerKind(maskLayer) != DrawingLayerKind.Mask) return false;
        var maskId = LayerIds[maskLayer];
        var changed = false;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (!string.Equals(LayerMaskIds[layer], maskId, StringComparison.Ordinal)) continue;
            LayerMaskIds[layer] = string.Empty;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool CanSetLayerParent(int layer, int parentLayer)
    {
        if ((uint)layer >= LayerCount) return false;
        if (parentLayer < 0) return true;
        if ((uint)parentLayer >= LayerCount || GetLayerKind(parentLayer) != DrawingLayerKind.Folder) return false;
        if (layer == parentLayer) return false;

        var current = parentLayer;
        var visited = new HashSet<int>();
        while (visited.Add(current))
        {
            if (current == layer) return false;
            current = GetLayerParentIndex(current);
            if (current < 0) return true;
        }

        return false;
    }

    public bool SetLayerParent(int layer, int parentLayer)
    {
        if (!CanSetLayerParent(layer, parentLayer)) return false;
        var parentId = parentLayer >= 0 ? LayerIds[parentLayer] : string.Empty;
        var relatedLayers = GetLinkedLayerIndices(layer);
        var changed = false;
        foreach (var relatedLayer in relatedLayers)
        {
            if (string.Equals(LayerParentIds[relatedLayer], parentId, StringComparison.Ordinal)) continue;
            LayerParentIds[relatedLayer] = parentId;
            changed = true;
        }

        if (changed) InvalidateQueryActiveKeyframes();
        return changed;
    }

    public bool RenameLayer(int layer, string? name)
    {
        if ((uint)layer >= LayerCount) return false;
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        normalized = normalized.Length <= 80 ? normalized : normalized[..80];
        if (string.Equals(LayerNames[layer], normalized, StringComparison.Ordinal)) return false;
        LayerNames[layer] = normalized;
        return true;
    }

    public bool ToggleLayerLocked(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        LayerLocked[layer] = !LayerLocked[layer];
        return true;
    }

    public bool SetLayerLocked(int layer, bool locked)
    {
        if ((uint)layer >= LayerCount || LayerLocked[layer] == locked) return false;
        LayerLocked[layer] = locked;
        return true;
    }

    public bool IsLayerDescendantOf(int layer, int ancestor)
    {
        if ((uint)layer >= LayerCount || (uint)ancestor >= LayerCount || layer == ancestor) return false;
        var current = GetLayerParentIndex(layer);
        var visited = new HashSet<int>();
        while (current >= 0 && visited.Add(current))
        {
            if (current == ancestor) return true;
            current = GetLayerParentIndex(current);
        }

        return false;
    }

    public int GetLayerSubtreeEnd(int layer)
    {
        if ((uint)layer >= LayerCount) return -1;
        var last = layer;
        for (var candidate = layer + 1; candidate < LayerCount; candidate++)
        {
            if (IsLayerDescendantOf(candidate, layer)) last = candidate;
        }

        return last;
    }

    public IReadOnlyList<int> GetLayerDisplayOrder()
    {
        if (LayerCount == 0) return [];

        var result = new List<int>(LayerCount);
        var appended = new bool[LayerCount];

        void AppendLayer(int layer)
        {
            if ((uint)layer >= LayerCount || appended[layer]) return;
            appended[layer] = true;
            result.Add(layer);

            if (GetLayerKind(layer) == DrawingLayerKind.Folder)
            {
                for (var candidate = 0; candidate < LayerCount; candidate++)
                {
                    if (GetLayerParentIndex(candidate) != layer) continue;
                    if (TryGetMaskLayerIndex(candidate, out _)) continue;
                    AppendLayer(candidate);
                }

                return;
            }

            if (GetLayerKind(layer) != DrawingLayerKind.Mask) return;
            var contentLayer = GetMaskContentLayerIndex(layer);
            if (contentLayer >= 0) AppendLayer(contentLayer);
        }

        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (GetLayerParentIndex(layer) >= 0 || TryGetMaskLayerIndex(layer, out _)) continue;
            AppendLayer(layer);
        }

        for (var layer = 0; layer < LayerCount; layer++) AppendLayer(layer);
        return result;
    }

    public void SynchronizeTimelineTracks()
    {
        var additionalTargets = AdditionalTimelineTargetIds();
        var targetIds = additionalTargets.Length == 0
            ? LayerIds
            : LayerIds.Concat(additionalTargets).ToArray();
        Timeline.SynchronizeTracks(targetIds, FrameCount, populateNewTracks: false);
        var additionalTargetSet = additionalTargets.ToHashSet(StringComparer.Ordinal);
        foreach (var track in Timeline.Tracks)
        {
            if (track.Keyframes.Count == 0 || track.Keyframes[0].Frame > 0)
            {
                if (additionalTargetSet.Contains(track.TargetId)) Timeline.InsertKeyframe(track.Id, 0);
                else Timeline.InsertBlankKeyframe(track.Id, 0);
            }
        }
    }

    public int AddLayer(string? name = null) => InsertLayer(DrawingLayerKind.Drawing, LayerCount, name);

    public int AddFolderLayer(string? name = null)
    {
        var contentLayer = FindEditableLayer(ActiveLayer, allowMask: false);
        if (contentLayer < 0) return AddLayer(name);

        var parentId = LayerParentIds[contentLayer];
        var folderLayer = InsertLayer(DrawingLayerKind.Folder, contentLayer, name ?? $"Folder {LayerCount:0000}");
        LayerParentIds[folderLayer] = parentId;
        LayerParentIds[contentLayer + 1] = LayerIds[folderLayer];
        ActiveLayer = contentLayer + 1;
        return folderLayer;
    }

    public int AddMaskLayer(string? name = null)
    {
        var contentLayer = FindEditableLayer(ActiveLayer, allowMask: false);
        if (contentLayer < 0) return AddLayer(name);

        var parentId = LayerParentIds[contentLayer];
        var maskLayer = InsertLayer(DrawingLayerKind.Mask, contentLayer, name ?? $"Mask {LayerCount:0000}");
        LayerParentIds[maskLayer] = parentId;
        LayerMaskIds[contentLayer + 1] = LayerIds[maskLayer];
        ActiveLayer = maskLayer;
        return maskLayer;
    }

    private int InsertLayer(DrawingLayerKind kind, int index, string? name)
    {
        if (LayerCount >= ushort.MaxValue) return Math.Max(0, LayerCount - 1);

        var duration = Math.Max(1, FrameCount);
        var previousCount = LayerCount;
        index = Math.Clamp(index, 0, previousCount);
        var knownIds = LayerIds.ToHashSet(StringComparer.Ordinal);
        var layerId = Guid.NewGuid().ToString("N");
        while (!knownIds.Add(layerId)) layerId = Guid.NewGuid().ToString("N");

        LayerIds = InsertLayerValue(LayerIds, index, layerId);
        LayerNames = InsertLayerValue(
            LayerNames,
            index,
            string.IsNullOrWhiteSpace(name)
                ? kind switch
                {
                    DrawingLayerKind.Folder => $"Folder {previousCount:0000}",
                    DrawingLayerKind.Mask => $"Mask {previousCount:0000}",
                    _ => $"Layer {previousCount:0000}"
                }
                : name.Trim());
        LayerKinds = InsertLayerValue(LayerKinds, index, kind);
        LayerParentIds = InsertLayerValue(LayerParentIds, index, string.Empty);
        LayerMaskIds = InsertLayerValue(LayerMaskIds, index, string.Empty);
        LayerLocked = InsertLayerValue(LayerLocked, index, false);
        LayerVisible = InsertLayerValue(LayerVisible, index, true);
        LayerOpacity = InsertLayerValue(LayerOpacity, index, 1f);
        LayerColorArgb = InsertLayerValue(LayerColorArgb, index, DefaultLayerColor(index).ToArgb());
        LayerOnionSkin = InsertLayerValue(LayerOnionSkin, index, false);
        LayerStart = InsertLayerValue(LayerStart, index, 0);
        LayerEnd = InsertLayerValue(LayerEnd, index, -1);
        LayerCount++;

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (ObjectLayer[objectIndex] >= index) ObjectLayer[objectIndex]++;
        }

        ActiveLayer = index;
        using (Timeline.BeginBatchUpdate())
        {
            SynchronizeTimelineTracks();
            var track = Timeline.FindTrackByTargetId(layerId);
            if (track is not null)
            {
                Timeline.SetTrackDuration(track.Id, duration);
                Timeline.InsertBlankKeyframe(track.Id, 0);
            }
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        return index;
    }

    private int ResolveObjectLayer(int requestedLayer)
    {
        var layer = FindEditableLayer(requestedLayer, allowMask: true);
        return layer >= 0 ? layer : Math.Clamp(requestedLayer, 0, LayerCount - 1);
    }

    internal string ResolveInstanceLayerId(string? preferredLayerId)
    {
        if (LayerCount == 0) return string.Empty;
        var preferredLayer = string.IsNullOrWhiteSpace(preferredLayerId)
            ? ActiveLayer
            : Array.IndexOf(LayerIds, preferredLayerId);
        if (preferredLayer < 0) preferredLayer = ActiveLayer;
        var layer = FindEditableLayer(preferredLayer, allowMask: false);
        return LayerIds[layer >= 0 ? layer : 0];
    }

    private int FindEditableLayer(int requestedLayer, bool allowMask)
    {
        if (LayerCount == 0) return -1;
        requestedLayer = Math.Clamp(requestedLayer, 0, LayerCount - 1);
        var requestedKind = GetLayerKind(requestedLayer);
        if (requestedKind == DrawingLayerKind.Drawing || (allowMask && requestedKind == DrawingLayerKind.Mask)) return requestedLayer;
        if (requestedKind != DrawingLayerKind.Folder) return -1;

        var folderId = LayerIds[requestedLayer];
        for (var layer = requestedLayer + 1; layer < LayerCount; layer++)
        {
            if (!string.Equals(LayerParentIds[layer], folderId, StringComparison.Ordinal)) continue;
            var kind = GetLayerKind(layer);
            if (kind == DrawingLayerKind.Drawing || (allowMask && kind == DrawingLayerKind.Mask)) return layer;
        }

        return -1;
    }

    private static T[] InsertLayerValue<T>(IReadOnlyList<T> source, int index, T value)
    {
        var result = new T[source.Count + 1];
        for (var sourceIndex = 0; sourceIndex < index; sourceIndex++) result[sourceIndex] = source[sourceIndex];
        result[index] = value;
        for (var sourceIndex = index; sourceIndex < source.Count; sourceIndex++) result[sourceIndex + 1] = source[sourceIndex];
        return result;
    }

    public Color GetLayerColor(int layer)
    {
        return (uint)layer < LayerColorArgb.Length
            ? Color.FromArgb(LayerColorArgb[layer])
            : DefaultLayerColor(layer);
    }

    public bool SetLayerColor(int layer, Color color)
    {
        if ((uint)layer >= LayerCount) return false;
        var next = color.ToArgb();
        if (LayerColorArgb[layer] == next) return false;
        LayerColorArgb[layer] = next;
        return true;
    }

    public bool ToggleLayerOnionSkin(int layer)
    {
        if ((uint)layer >= LayerCount) return false;
        LayerOnionSkin[layer] = !LayerOnionSkin[layer];
        return true;
    }

    public bool SetOnionSkinRange(int previousFrames, int nextFrames)
    {
        var previous = NormalizeOnionSkinFrames(previousFrames, DefaultOnionSkinPreviousFrames);
        var next = NormalizeOnionSkinFrames(nextFrames, DefaultOnionSkinNextFrames);
        if (OnionSkinPreviousFrames == previous && OnionSkinNextFrames == next) return false;

        OnionSkinPreviousFrames = previous;
        OnionSkinNextFrames = next;
        return true;
    }

    public bool MoveLayer(int from, int to)
    {
        if ((uint)from >= LayerCount || (uint)to >= LayerCount || from == to) return false;

        var moving = GetLayerMoveSet(from);
        if (moving.Contains(to)) return false;
        return MoveLayerToRemainingIndex(from, moving, Math.Clamp(to, 0, LayerCount - moving.Count));
    }

    private bool MoveLayerToRemainingIndex(int from, IReadOnlySet<int> moving, int destinationIndex)
    {
        var ordered = Enumerable.Range(0, LayerCount).ToList();
        var remaining = ordered.Where(layer => !moving.Contains(layer)).ToList();
        var block = ordered.Where(moving.Contains).ToList();
        var insertAt = Math.Clamp(destinationIndex, 0, remaining.Count);
        remaining.InsertRange(insertAt, block);
        if (remaining.SequenceEqual(ordered)) return false;
        var destinationBySource = new int[LayerCount];
        for (var destination = 0; destination < remaining.Count; destination++)
        {
            destinationBySource[remaining[destination]] = destination;
        }

        LayerIds = ReorderLayers(LayerIds, destinationBySource);
        LayerNames = ReorderLayers(LayerNames, destinationBySource);
        LayerKinds = ReorderLayers(LayerKinds, destinationBySource);
        LayerParentIds = ReorderLayers(LayerParentIds, destinationBySource);
        LayerMaskIds = ReorderLayers(LayerMaskIds, destinationBySource);
        LayerLocked = ReorderLayers(LayerLocked, destinationBySource);
        LayerVisible = ReorderLayers(LayerVisible, destinationBySource);
        LayerOpacity = ReorderLayers(LayerOpacity, destinationBySource);
        LayerColorArgb = ReorderLayers(LayerColorArgb, destinationBySource);
        LayerOnionSkin = ReorderLayers(LayerOnionSkin, destinationBySource);
        LayerStart = ReorderLayers(LayerStart, destinationBySource);
        LayerEnd = ReorderLayers(LayerEnd, destinationBySource);
        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            ObjectLayer[objectIndex] = (ushort)destinationBySource[ObjectLayer[objectIndex]];
        }

        ActiveLayer = destinationBySource[Math.Clamp(ActiveLayer, 0, LayerCount - 1)];
        SynchronizeTimelineTracks();
        InvalidateQueryActiveKeyframes();
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
    }

    public bool MoveLayerBefore(int from, int beforeLayer)
    {
        if ((uint)from >= LayerCount || (uint)beforeLayer >= LayerCount || from == beforeLayer) return false;
        if (TryGetMaskLayerIndex(beforeLayer, out var maskLayer)) beforeLayer = maskLayer;
        var moving = GetLayerMoveSet(from);
        if (moving.Contains(beforeLayer)) return false;
        var destination = Enumerable.Range(0, beforeLayer).Count(layer => !moving.Contains(layer));
        return MoveLayerToRemainingIndex(from, moving, destination);
    }

    public bool MoveLayerAfter(int from, int afterLayer)
    {
        if ((uint)from >= LayerCount || (uint)afterLayer >= LayerCount || from == afterLayer) return false;
        var moving = GetLayerMoveSet(from);
        if (moving.Contains(afterLayer)) return false;
        var boundary = GetLayerGroupEnd(afterLayer) + 1;
        var destination = Enumerable.Range(0, boundary).Count(layer => !moving.Contains(layer));
        return MoveLayerToRemainingIndex(from, moving, destination);
    }

    private int GetLayerGroupEnd(int layer)
    {
        var last = layer;
        foreach (var member in GetLayerMoveSet(layer)) last = Math.Max(last, member);
        return last;
    }

    private HashSet<int> GetLayerMoveSet(int rootLayer)
    {
        var result = new HashSet<int> { rootLayer };
        var pending = new Queue<int>();
        pending.Enqueue(rootLayer);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            var currentId = LayerIds[current];
            for (var candidate = 0; candidate < LayerCount; candidate++)
            {
                if (result.Contains(candidate)) continue;
                var isChild = string.Equals(LayerParentIds[candidate], currentId, StringComparison.Ordinal);
                var isLinkedMask = string.Equals(LayerMaskIds[candidate], currentId, StringComparison.Ordinal)
                    || string.Equals(LayerMaskIds[current], LayerIds[candidate], StringComparison.Ordinal);
                if (!isChild && !isLinkedMask) continue;
                result.Add(candidate);
                pending.Enqueue(candidate);
            }
        }

        return result;
    }

    private int[] GetLinkedLayerIndices(int layer)
    {
        var related = new List<int> { layer };
        var layerId = LayerIds[layer];
        for (var candidate = 0; candidate < LayerCount; candidate++)
        {
            if (candidate == layer) continue;
            if (string.Equals(LayerMaskIds[candidate], layerId, StringComparison.Ordinal)
                || string.Equals(LayerMaskIds[layer], LayerIds[candidate], StringComparison.Ordinal))
            {
                related.Add(candidate);
            }
        }

        return related.ToArray();
    }

    public bool CopyTimelineFrameFrom(
        VectorScene source,
        int sourceLayer,
        int sourceFrame,
        int destinationLayer,
        int destinationFrame)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            var snapshotSource = new VectorScene();
            snapshotSource.RestoreSnapshot(CreateSnapshot());
            return CopyTimelineFrameFrom(snapshotSource, sourceLayer, sourceFrame, destinationLayer, destinationFrame);
        }
        if ((uint)sourceLayer >= source.LayerCount
            || (uint)destinationLayer >= LayerCount
            || sourceFrame < 0
            || destinationFrame < 0)
        {
            return false;
        }

        var sourceTrack = source.TimelineTrackForLayer(sourceLayer);
        var destinationTrack = TimelineTrackForLayer(destinationLayer);
        if (sourceTrack is null || destinationTrack is null) return false;

        var sourceExposure = sourceTrack.EvaluateExposure(Math.Min(sourceFrame, sourceTrack.Duration - 1));
        if (!sourceExposure.HasContent)
        {
            return InsertTimelineBlankKeyframe(destinationLayer, destinationFrame);
        }

        var sourceObjects = Enumerable.Range(0, source.ObjectCount)
            .Where(index => source.ObjectLayer[index] == sourceLayer
                && source.ObjectKeyframeFrame[index] == sourceExposure.SourceKeyframeFrame)
            .ToArray();
        if (sourceObjects.Length == 0)
        {
            return InsertTimelineBlankKeyframe(destinationLayer, destinationFrame);
        }

        using var batchUpdate = Timeline.BeginBatchUpdate();
        if (destinationFrame >= destinationTrack.Duration)
        {
            Timeline.SetTrackDuration(destinationTrack.Id, destinationFrame + 1);
        }

        _synchronizingKeyframeContent = true;
        try
        {
            RemoveObjectsForKeyframe(destinationLayer, destinationFrame);
            Timeline.InsertKeyframe(destinationTrack.Id, destinationFrame);
            CloneObjectsFrom(source, sourceObjects, destinationLayer, destinationFrame);
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }

        RefreshLegacyExposureBounds(destinationLayer);
        return true;
    }

    public void BuildOnionSkinPreview(
        VectorScene destination,
        int frame,
        int? previousFrames = null,
        int? nextFrames = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var previousRange = NormalizeOnionSkinFrames(previousFrames, OnionSkinPreviousFrames);
        var nextRange = NormalizeOnionSkinFrames(nextFrames, OnionSkinNextFrames);
        if ((previousRange <= 0 && nextRange <= 0) || !HasVisibleOnionSkinLayer())
        {
            destination.CreateEmpty();
            return;
        }

        var candidates = new List<(int Layer, int Keyframe, float Opacity, bool IsPrevious)>();
        var seen = new HashSet<(int Layer, int Keyframe)>();
        var lastFrame = Math.Max(0, FrameCount - 1);

        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (!LayerVisible[layer] || !LayerOnionSkin[layer]) continue;
            var track = TimelineTrackForLayer(layer);
            if (track is null) continue;
            var currentExposure = track.EvaluateExposure(Math.Clamp(frame, 0, track.Duration - 1));
            var currentKeyframe = currentExposure.HasContent ? currentExposure.SourceKeyframeFrame : -1;

            for (var offset = previousRange; offset >= 1; offset--)
            {
                var previewFrame = frame - offset;
                if (previewFrame < 0) continue;
                var exposure = track.EvaluateExposure(previewFrame);
                if (!exposure.HasContent
                    || exposure.SourceKeyframeFrame == currentKeyframe
                    || !seen.Add((layer, exposure.SourceKeyframeFrame)))
                {
                    continue;
                }
                candidates.Add((layer, exposure.SourceKeyframeFrame, 0.18f + 0.32f / offset, true));
            }

            for (var offset = 1; offset <= nextRange; offset++)
            {
                var previewFrame = frame + offset;
                if (previewFrame > lastFrame || previewFrame >= track.Duration) continue;
                var exposure = track.EvaluateExposure(previewFrame);
                if (!exposure.HasContent
                    || exposure.SourceKeyframeFrame == currentKeyframe
                    || !seen.Add((layer, exposure.SourceKeyframeFrame)))
                {
                    continue;
                }
                candidates.Add((layer, exposure.SourceKeyframeFrame, 0.18f + 0.32f / offset, false));
            }
        }

        var objectsByCel = new Dictionary<(int Layer, int Keyframe), List<int>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            objectsByCel.TryAdd((candidate.Layer, candidate.Keyframe), []);
        }

        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (objectsByCel.TryGetValue((ObjectLayer[objectIndex], ObjectKeyframeFrame[objectIndex]), out var objects))
            {
                objects.Add(objectIndex);
            }
        }

        var populated = new List<((int Layer, int Keyframe, float Opacity, bool IsPrevious) Candidate, List<int> Objects)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var objects = objectsByCel[(candidate.Layer, candidate.Keyframe)];
            if (objects.Count > 0) populated.Add((candidate, objects));
        }

        destination.CreateEmpty(Math.Max(1, populated.Count));
        if (populated.Count == 0) return;

        var expectedCount = populated.Sum(item => item.Objects.Count);
        destination.EnsureObjectCapacity(expectedCount);
        using var batchUpdate = destination.Timeline.BeginBatchUpdate();
        for (var destinationLayer = 0; destinationLayer < populated.Count; destinationLayer++)
        {
            var item = populated[destinationLayer];
            destination.LayerNames[destinationLayer] = $"{LayerNames[item.Candidate.Layer]} onion";
            destination.LayerVisible[destinationLayer] = true;
            destination.LayerOpacity[destinationLayer] = item.Candidate.Opacity;
            destination.LayerColorArgb[destinationLayer] = item.Candidate.IsPrevious
                ? OnionSkinPreviousTintArgb
                : OnionSkinNextTintArgb;
            var track = destination.Timeline.FindTrackByTargetId(destination.LayerIds[destinationLayer]);
            if (track is not null) destination.Timeline.InsertKeyframe(track.Id, 0);

            foreach (var sourceObject in item.Objects)
            {
                var destinationObject = destination.ObjectCount++;
                destination.CopyObjectDataFrom(this, sourceObject, destinationObject);
                destination.ObjectLayer[destinationObject] = (ushort)destinationLayer;
                destination.ObjectKeyframeFrame[destinationObject] = 0;
                destination.ObjectOrder[destinationObject] = ++destination._nextObjectOrder;
                destination.ApplyOnionSkinAppearance(
                    destinationObject,
                    item.Candidate.Opacity,
                    item.Candidate.IsPrevious ? OnionSkinPreviousTintArgb : OnionSkinNextTintArgb);
                destination.VirtualAtomCount += destination.AtomCount[destinationObject];
            }
        }

        destination.SynchronizeAllKeyframeContentKinds();
        destination.RebuildGeometryIndex();
        destination.RebuildSummaries();
    }

    private bool HasVisibleOnionSkinLayer()
    {
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (LayerVisible[layer] && LayerOnionSkin[layer]) return true;
        }

        return false;
    }

    internal void CombineOnionSkinPreviews(
        IReadOnlyList<(VectorScene Scene, float Opacity, bool? IsPrevious)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var populated = sources.Where(source => source.Scene.ObjectCount > 0).ToArray();
        var layerCount = Math.Max(1, populated.Sum(source => source.Scene.LayerCount));
        CreateEmpty(layerCount);
        if (populated.Length == 0) return;

        EnsureObjectCapacity(populated.Sum(source => source.Scene.ObjectCount));
        var destinationLayerOffset = 0;
        foreach (var sourceItem in populated)
        {
            var source = sourceItem.Scene;
            for (var sourceLayer = 0; sourceLayer < source.LayerCount; sourceLayer++)
            {
                var destinationLayer = destinationLayerOffset + sourceLayer;
                LayerNames[destinationLayer] = source.LayerNames[sourceLayer];
                LayerKinds[destinationLayer] = source.LayerKinds[sourceLayer];
                LayerVisible[destinationLayer] = source.LayerVisible[sourceLayer];
                LayerOpacity[destinationLayer] = source.LayerOpacity[sourceLayer] * sourceItem.Opacity;
                LayerColorArgb[destinationLayer] = source.LayerColorArgb[sourceLayer];
                var sourceParent = Array.IndexOf(source.LayerIds, source.LayerParentIds[sourceLayer]);
                if (sourceParent >= 0)
                {
                    LayerParentIds[destinationLayer] = LayerIds[destinationLayerOffset + sourceParent];
                }
                var sourceMask = Array.IndexOf(source.LayerIds, source.LayerMaskIds[sourceLayer]);
                if (sourceMask >= 0)
                {
                    LayerMaskIds[destinationLayer] = LayerIds[destinationLayerOffset + sourceMask];
                }
            }

            for (var sourceObject = 0; sourceObject < source.ObjectCount; sourceObject++)
            {
                var destinationObject = ObjectCount++;
                CopyObjectDataFrom(source, sourceObject, destinationObject);
                ObjectLayer[destinationObject] = (ushort)(destinationLayerOffset + source.ObjectLayer[sourceObject]);
                ObjectKeyframeFrame[destinationObject] = 0;
                ObjectOrder[destinationObject] = ++_nextObjectOrder;
                if (sourceItem.IsPrevious is { } isPrevious)
                {
                    ApplyOnionSkinAppearance(
                        destinationObject,
                        sourceItem.Opacity,
                        isPrevious ? OnionSkinPreviousTintArgb : OnionSkinNextTintArgb);
                }
                VirtualAtomCount += AtomCount[destinationObject];
            }

            destinationLayerOffset += source.LayerCount;
        }

        SynchronizeAllKeyframeContentKinds();
        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private string[] AdditionalTimelineTargetIds()
    {
        var targets = _additionalTimelineTargets?.Invoke();
        if (targets is null || targets.Count == 0) return [];

        var layerIds = LayerIds.ToHashSet(StringComparer.Ordinal);
        return targets
            .Where(targetId => !string.IsNullOrWhiteSpace(targetId) && !layerIds.Contains(targetId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public bool IsObjectActive(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var layer = ObjectLayer[objectIndex];
        if ((uint)layer >= LayerCount || !IsLayerEffectivelyVisible(layer)) return false;

        var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
        return exposure.HasContent && ObjectKeyframeFrame[objectIndex] == exposure.SourceKeyframeFrame;
    }

    internal void PopulateActiveKeyframeFrames(int frame, int[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < LayerCount) throw new ArgumentException("The active-frame buffer is too small.", nameof(destination));

        if (_queryActiveFrame == frame && _queryActiveKeyframes.Length == LayerCount)
        {
            if (!ReferenceEquals(destination, _queryActiveKeyframes))
            {
                Array.Copy(_queryActiveKeyframes, destination, LayerCount);
            }

            return;
        }

        if (_queryActiveKeyframes.Length != LayerCount) Array.Resize(ref _queryActiveKeyframes, LayerCount);
        for (var layer = 0; layer < LayerCount; layer++)
        {
            if (GetLayerKind(layer) == DrawingLayerKind.Folder || !IsLayerEffectivelyVisible(layer))
            {
                _queryActiveKeyframes[layer] = int.MinValue;
                continue;
            }

            var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
            _queryActiveKeyframes[layer] = exposure.HasContent ? exposure.SourceKeyframeFrame : int.MinValue;
        }

        _queryActiveFrame = frame;
        if (!ReferenceEquals(destination, _queryActiveKeyframes))
        {
            Array.Copy(_queryActiveKeyframes, destination, LayerCount);
        }
    }

    public bool InsertTimelineFrame(int layer, int frame, int count = 1)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || count <= 0) return false;
        frame = Math.Max(0, frame);
        if (frame > track.Duration) Timeline.SetTrackDuration(track.Id, frame);
        if (!Timeline.InsertFrame(track.Id, frame, count)) return false;

        for (var i = 0; i < ObjectCount; i++)
        {
            if (ObjectLayer[i] == layer && ObjectKeyframeFrame[i] > frame)
            {
                ObjectKeyframeFrame[i] += count;
            }
        }

        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool RemoveTimelineFrame(int layer, int frame, int count = 1)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || count <= 0 || frame < 0 || frame >= track.Duration || track.Duration <= 1) return false;

        var removeCount = Math.Min(Math.Min(count, track.Duration - frame), track.Duration - 1);
        var removeEnd = frame + removeCount;
        var continuation = removeEnd < track.Duration
            ? track.EvaluateExposure(removeEnd)
            : TimelineExposure.None(removeEnd);
        var preservedSourceFrame = continuation.SourceKeyframeFrame >= frame
            && continuation.SourceKeyframeFrame < removeEnd
            ? continuation.SourceKeyframeFrame
            : -1;
        if (removeCount <= 0 || !Timeline.RemoveFrame(track.Id, frame, removeCount)) return false;

        var removed = new List<int>();
        for (var i = 0; i < ObjectCount; i++)
        {
            if (ObjectLayer[i] != layer) continue;
            var sourceFrame = ObjectKeyframeFrame[i];
            if (sourceFrame == preservedSourceFrame) ObjectKeyframeFrame[i] = frame;
            else if (sourceFrame >= frame && sourceFrame < removeEnd) removed.Add(i);
            else if (sourceFrame >= removeEnd) ObjectKeyframeFrame[i] -= removeCount;
        }

        if (removed.Count > 0) RemoveObjects(removed);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool InsertTimelineKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;

        var current = frame < track.Duration
            ? track.EvaluateExposure(frame)
            : track.EvaluateExposure(track.Duration - 1);
        if (frame < track.Duration
            && current.IsKeyframe
            && current.SourceKind == TimelineKeyframeKind.Populated)
        {
            return false;
        }
        var sourceObjects = current.HasContent
            ? Enumerable.Range(0, ObjectCount)
                .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == current.SourceKeyframeFrame)
                .ToArray()
            : [];
        var carriesExternalContent = current.HasContent
            && HasExternalLayerKeyframeContent(layer, current.SourceKeyframeFrame);

        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);

        var inserted = sourceObjects.Length > 0 || carriesExternalContent
            ? Timeline.InsertKeyframe(track.Id, frame)
            : Timeline.InsertBlankKeyframe(track.Id, frame);
        if (!inserted) return false;
        CloneObjectsIntoKeyframe(sourceObjects, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool InsertTimelineBlankKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;
        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);
        var existing = track.EvaluateExposure(frame);
        if (existing.IsKeyframe && existing.SourceKind == TimelineKeyframeKind.Blank) return false;
        if (!Timeline.InsertBlankKeyframe(track.Id, frame)) return false;

        RemoveObjectsForKeyframe(layer, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    public bool ClearTimelineKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null || frame < 0) return false;
        var existing = track.EvaluateExposure(frame);
        if (!existing.IsKeyframe || !Timeline.ClearKeyframe(track.Id, frame)) return false;

        RemoveObjectsForKeyframe(layer, frame);
        RefreshLegacyExposureBounds(layer);
        return true;
    }

    private AnimationTimelineTrack? TimelineTrackForLayer(int layer)
    {
        if ((uint)layer >= LayerCount) return null;
        SynchronizeTimelineTracks();
        return Timeline.FindTrackByTargetId(LayerIds[layer]);
    }

    private int EnsureWritableKeyframe(int layer, int frame)
    {
        var track = TimelineTrackForLayer(layer);
        if (track is null) return 0;

        return EnsureWritableKeyframe(track, layer, frame);
    }

    private int EnsureWritableKeyframe(AnimationTimelineTrack track, int layer, int frame)
    {

        frame = Math.Max(0, frame);
        if (frame >= track.Duration)
        {
            Timeline.SetTrackDuration(track.Id, frame + 1);
            Timeline.InsertKeyframe(track.Id, frame);
            RefreshLegacyExposureBounds(layer);
            return frame;
        }

        var exposure = track.EvaluateExposure(frame);
        if (exposure.HasContent) return exposure.SourceKeyframeFrame;

        var keyframeFrame = exposure.SourceKeyframeFrame >= 0 ? exposure.SourceKeyframeFrame : frame;
        Timeline.InsertKeyframe(track.Id, keyframeFrame);
        RefreshLegacyExposureBounds(layer);
        return keyframeFrame;
    }

    private void CloneObjectsIntoKeyframe(IReadOnlyList<int> sourceObjects, int keyframeFrame)
    {
        if (sourceObjects.Count == 0) return;

        var sourceCount = ObjectCount;
        EnsureObjectCapacity(ObjectCount + sourceObjects.Count);
        foreach (var source in sourceObjects)
        {
            if ((uint)source >= sourceCount) continue;
            var destination = ObjectCount++;
            CopyObjectData(source, destination);
            ObjectKeyframeFrame[destination] = keyframeFrame;
            VirtualAtomCount += AtomCount[destination];
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private void CloneObjectsFrom(
        VectorScene source,
        IReadOnlyList<int> sourceObjects,
        int destinationLayer,
        int keyframeFrame)
    {
        EnsureObjectCapacity(ObjectCount + sourceObjects.Count);
        foreach (var sourceObject in sourceObjects)
        {
            if ((uint)sourceObject >= source.ObjectCount) continue;
            var destination = ObjectCount++;
            CopyObjectDataFrom(source, sourceObject, destination);
            ObjectLayer[destination] = (ushort)destinationLayer;
            ObjectKeyframeFrame[destination] = keyframeFrame;
            ObjectOrder[destination] = ++_nextObjectOrder;
            VirtualAtomCount += AtomCount[destination];
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    private void RemoveObjectsForKeyframe(int layer, int keyframeFrame)
    {
        var removed = Enumerable.Range(0, ObjectCount)
            .Where(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframeFrame)
            .ToArray();
        if (removed.Length > 0) RemoveObjects(removed);
    }

    private void SynchronizeAllKeyframeContentKinds()
    {
        var occupied = new HashSet<(int Layer, int Frame)>();
        for (var index = 0; index < ObjectCount; index++)
        {
            occupied.Add((ObjectLayer[index], ObjectKeyframeFrame[index]));
        }

        _synchronizingKeyframeContent = true;
        try
        {
            for (var layer = 0; layer < LayerCount; layer++)
            {
                var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
                if (track is null) continue;
                foreach (var keyframe in track.Keyframes.ToArray())
                {
                    var hasContent = occupied.Contains((layer, keyframe.Frame))
                        || (keyframe.HasContent && HasExternalLayerKeyframeContent(layer, keyframe.Frame));
                    if (keyframe.HasContent == hasContent) continue;
                    if (hasContent) Timeline.InsertKeyframe(track.Id, keyframe.Frame);
                    else Timeline.InsertBlankKeyframe(track.Id, keyframe.Frame);
                }

                RefreshLegacyExposureBounds(layer);
            }
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }
    }

    private void SynchronizeKeyframeContentKind(int layer, int keyframeFrame)
    {
        if (_synchronizingKeyframeContent || (uint)layer >= LayerCount) return;
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        var keyframe = track?.Keyframes
            .Where(item => item.Frame == keyframeFrame)
            .Select(item => (TimelineKeyframe?)item)
            .FirstOrDefault();
        if (track is null || keyframe is null) return;

        var hasContent = Enumerable.Range(0, ObjectCount)
                .Any(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframeFrame)
            || (keyframe.Value.HasContent && HasExternalLayerKeyframeContent(layer, keyframeFrame));
        if (keyframe.Value.HasContent == hasContent) return;

        _synchronizingKeyframeContent = true;
        try
        {
            if (hasContent) Timeline.InsertKeyframe(track.Id, keyframeFrame);
            else Timeline.InsertBlankKeyframe(track.Id, keyframeFrame);
            RefreshLegacyExposureBounds(layer);
        }
        finally
        {
            _synchronizingKeyframeContent = false;
        }
    }

    private bool HasExternalLayerKeyframeContent(int layer, int keyframeFrame)
    {
        return (uint)layer < LayerCount
            && _externalLayerKeyframeContent?.Invoke(LayerIds[layer], keyframeFrame) == true;
    }

    private void RefreshLegacyExposureBounds(int layer)
    {
        if ((uint)layer >= LayerCount || layer >= LayerStart.Length || layer >= LayerEnd.Length) return;
        var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
        if (track is null) return;

        var populated = track.Keyframes.Where(keyframe => keyframe.HasContent).ToArray();
        if (populated.Length == 0)
        {
            LayerStart[layer] = 0;
            LayerEnd[layer] = -1;
            return;
        }

        LayerStart[layer] = populated[0].Frame;
        LayerEnd[layer] = populated
            .Select(keyframe => track.EvaluateExposure(keyframe.Frame).EndFrame)
            .Max();
    }

    public void SoloLayer(int layer)
    {
        Array.Fill(LayerVisible, false);
        if (layer >= 0 && layer < LayerCount) LayerVisible[layer] = true;
        InvalidateQueryActiveKeyframes();
    }

    public void ShowAllLayers()
    {
        Array.Fill(LayerVisible, true);
        InvalidateQueryActiveKeyframes();
    }

    public bool SetLayerVisible(int layer, bool visible)
    {
        if ((uint)layer >= LayerCount || LayerVisible[layer] == visible) return false;
        LayerVisible[layer] = visible;
        InvalidateQueryActiveKeyframes();
        return true;
    }

    public void ToggleLayer(int layer)
    {
        if (layer < 0 || layer >= LayerCount) return;
        LayerVisible[layer] = !LayerVisible[layer];
        InvalidateQueryActiveKeyframes();
    }

    public int HitTest(PointF world, int frame, float toleranceWorld = 6)
    {
        var hit = HitTestElement(world, frame, toleranceWorld);
        return hit.IsValid ? hit.Key.ObjectIndex : -1;
    }

    public DrawingElementHit HitTestElement(PointF world, int frame, float toleranceWorld = 6)
    {
        GetHitTestRange(world, toleranceWorld, out var minX, out var maxX, out var minY, out var maxY);
        var candidates = CollectHitCandidates(minX, maxX, minY, maxY, frame);
        var best = DrawingElementHit.None;

        foreach (var i in candidates)
        {
            var hit = HitElement(world, i, candidates, frame, toleranceWorld);
            if (!hit.IsValid) continue;
            if (IsBetterHit(hit, best)) best = hit;
        }

        return best;
    }

    private bool IsBetterHit(DrawingElementHit hit, DrawingElementHit best)
    {
        if (!best.IsValid) return true;

        var hitLayer = ObjectLayer[hit.Key.ObjectIndex];
        var bestLayer = ObjectLayer[best.Key.ObjectIndex];
        if (hitLayer != bestLayer) return hitLayer < bestLayer;

        var hitIsFill = hit.Key.Kind == DrawingElementKind.Fill;
        var bestIsFill = best.Key.Kind == DrawingElementKind.Fill;
        if (hitIsFill != bestIsFill) return !hitIsFill;
        if (hitIsFill) return CompareObjectStack(hit.Key.ObjectIndex, best.Key.ObjectIndex) > 0;

        if (hit.Distance < best.Distance - 0.001f) return true;
        if (hit.Distance > best.Distance + 0.001f) return false;
        return CompareObjectStack(hit.Key.ObjectIndex, best.Key.ObjectIndex) > 0;
    }

    private int CompareObjectStack(int a, int b)
    {
        var comparison = ObjectOrder[a].CompareTo(ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = ObjectSubOrder[a].CompareTo(ObjectSubOrder[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    public PointF[] GetShapeBoundary(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount ? ShapeBoundary(objectIndex) : Array.Empty<PointF>();
    }

    public PointF[][] GetFillPartContours(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Fill || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF[]>();
        }

        foreach (var part in GetFillParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return CloneContours(part.Contours);
        }

        return Array.Empty<PointF[]>();
    }

    public DrawingFillPartGeometry[] GetFillParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount || !HasFill(objectIndex)) return Array.Empty<DrawingFillPartGeometry>();
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var regions = BuildFillRegions(objectIndex, candidates);
        var result = new DrawingFillPartGeometry[regions.Count];
        for (var part = 0; part < regions.Count; part++)
        {
            result[part] = new DrawingFillPartGeometry(part, CloneContours(regions[part].Contours));
        }

        return result;
    }

    public PointF[] GetBoundaryPartPoints(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.BoundaryStroke || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF>();
        }

        foreach (var part in GetBoundaryParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return part.Points.ToArray();
        }

        return Array.Empty<PointF>();
    }

    public DrawingPolylinePartGeometry[] GetBoundaryParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return Array.Empty<DrawingPolylinePartGeometry>();
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        return BuildBoundaryStrokeParts(objectIndex, candidates)
            .Select(part => new DrawingPolylinePartGeometry(part.PartIndex, part.Points.ToArray()))
            .ToArray();
    }

    public PointF[] GetStrokePartPoints(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || hit.Key.Kind != DrawingElementKind.Stroke || (uint)hit.Key.ObjectIndex >= ObjectCount)
        {
            return Array.Empty<PointF>();
        }

        if (ShapeKind[hit.Key.ObjectIndex] == VectorAnimationEngine.ShapeKind.Line
            && TryGetLineBezierPart(
                hit.Key.ObjectIndex,
                hit.StartT,
                hit.EndT,
                out var start,
                out _,
                out var end))
        {
            // Endpoint editing needs the topology-part endpoints, not the owning line's outer endpoints.
            return new[] { start, end };
        }

        foreach (var part in GetStrokeParts(hit.Key.ObjectIndex, frame))
        {
            if (part.PartIndex == hit.Key.PartIndex) return part.Points.ToArray();
        }

        return Array.Empty<PointF>();
    }

    public DrawingPolylinePartGeometry[] GetStrokeParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount
            || !IsFreehandShape(ShapeKind[objectIndex])
            || !TryGetFreehandWorldPoints(objectIndex, out var points))
        {
            return Array.Empty<DrawingPolylinePartGeometry>();
        }

        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var splits = StrokeSplitParameters(objectIndex, candidates);
        return BuildPolylinePathParts(points, splits)
            .Select(part => new DrawingPolylinePartGeometry(part.PartIndex, part.Points.ToArray()))
            .ToArray();
    }

    public DrawingElementHit[] GetConnectedStrokeElements(DrawingElementHit seed, int frame)
    {
        if (!seed.IsValid
            || seed.Key.Kind is not (DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
            || (uint)seed.Key.ObjectIndex >= ObjectCount
            || !IsObjectActive(seed.Key.ObjectIndex, frame))
        {
            return Array.Empty<DrawingElementHit>();
        }

        var layer = ObjectLayer[seed.Key.ObjectIndex];
        var parts = new List<ConnectedStrokePart>();
        var partIndices = new Dictionary<DrawingElementKey, int>();
        var endpointIndex = new Dictionary<(int X, int Y), List<int>>();
        var indexedObjects = new HashSet<int>();

        void AddEndpoint(PointF point, int partIndex)
        {
            var key = ConnectedEndpointKey(point);
            if (!endpointIndex.TryGetValue(key, out var indexed))
            {
                indexed = new List<int>(4);
                endpointIndex[key] = indexed;
            }

            indexed.Add(partIndex);
        }

        void IndexObject(int objectIndex)
        {
            if (!indexedObjects.Add(objectIndex)
                || (uint)objectIndex >= ObjectCount
                || ObjectLayer[objectIndex] != layer
                || !IsObjectActive(objectIndex, frame))
            {
                return;
            }

            foreach (var part in BuildConnectedStrokeParts(objectIndex, frame))
            {
                if (partIndices.ContainsKey(part.Hit.Key)) continue;
                var partIndex = parts.Count;
                parts.Add(part);
                partIndices[part.Hit.Key] = partIndex;
                AddEndpoint(part.Start, partIndex);
                AddEndpoint(part.End, partIndex);
            }
        }

        IndexObject(seed.Key.ObjectIndex);
        if (!partIndices.TryGetValue(seed.Key, out var seedPartIndex)) return Array.Empty<DrawingElementHit>();

        var connected = new HashSet<int>();
        var connectedOrder = new List<int>();
        var endpoints = new Queue<PointF>();
        var visitedEndpoints = new HashSet<(int X, int Y)>();

        void SelectPart(int partIndex)
        {
            if (!connected.Add(partIndex)) return;
            connectedOrder.Add(partIndex);
            endpoints.Enqueue(parts[partIndex].Start);
            endpoints.Enqueue(parts[partIndex].End);
        }

        SelectPart(seedPartIndex);
        while (endpoints.Count > 0)
        {
            var endpoint = endpoints.Dequeue();
            if (!visitedEndpoints.Add(ConnectedEndpointKey(endpoint))) continue;

            var tolerance = ConnectedStrokeEndpointToleranceUnits;
            var queryBounds = RectangleF.FromLTRB(
                endpoint.X - tolerance,
                endpoint.Y - tolerance,
                endpoint.X + tolerance,
                endpoint.Y + tolerance);
            foreach (var objectIndex in QueryObjects(queryBounds, frame)) IndexObject(objectIndex);

            var endpointKey = ConnectedEndpointKey(endpoint);
            for (var y = endpointKey.Y - 2; y <= endpointKey.Y + 2; y++)
            {
                for (var x = endpointKey.X - 2; x <= endpointKey.X + 2; x++)
                {
                    if (!endpointIndex.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var candidateIndex in candidates)
                    {
                        var candidate = parts[candidateIndex];
                        if (Distance(endpoint, candidate.Start) <= tolerance
                            || Distance(endpoint, candidate.End) <= tolerance)
                        {
                            SelectPart(candidateIndex);
                        }
                    }
                }
            }
        }

        return connectedOrder.Select(index => parts[index].Hit).ToArray();
    }

    private ConnectedStrokePart[] BuildConnectedStrokeParts(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return Array.Empty<ConnectedStrokePart>();
        var shape = ShapeKind[objectIndex];
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        var result = new List<ConnectedStrokePart>();

        if (IsTopologyStrokeShape(shape))
        {
            var splits = StrokeSplitParameters(objectIndex, candidates);
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                var curve = LineCurve(objectIndex);
                foreach (var part in BuildCurveParts(curve.Start, curve.Control, curve.End, splits))
                {
                    result.Add(new ConnectedStrokePart(
                        new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            splits[part.PartIndex],
                            splits[part.PartIndex + 1]),
                        part.Start,
                        part.End));
                }
            }
            else if (TryGetFreehandWorldPoints(objectIndex, out var points))
            {
                foreach (var part in BuildPolylinePathParts(points, splits))
                {
                    result.Add(new ConnectedStrokePart(
                        new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            part.StartT,
                            part.EndT),
                        part.Points[0],
                        part.Points[^1]));
                }
            }

            return result.ToArray();
        }

        foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
        {
            result.Add(new ConnectedStrokePart(
                new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                    0,
                    part.StartT,
                    part.EndT),
                part.Points[0],
                part.Points[^1]));
        }

        return result.ToArray();
    }

    public bool TryCreateFillFromClosedStrokeRegion(
        PointF world,
        int frame,
        Color color,
        out int createdObject,
        out PointF[][] animationContours)
    {
        createdObject = -1;
        animationContours = Array.Empty<PointF[]>();
        if (!TryFindClosedStrokeFillRegion(world, frame, out var region)) return false;

        var atoms = (uint)Math.Clamp(region.Contour.Length, 3, 4096);
        var created = AppendPathObjectContours(
            ActiveLayer,
            new[] { region.Contour },
            0,
            color,
            Color.Transparent,
            atoms);
        if (created < 0) return false;

        var firstBoundary = region.BoundaryObjects
            .Where(index => (uint)index < ObjectCount)
            .OrderBy(index => ObjectOrder[index])
            .ThenBy(index => ObjectSubOrder[index])
            .FirstOrDefault(-1);
        if (firstBoundary >= 0)
        {
            // Keep the generated fill beneath every stroke that forms its boundary.
            ObjectOrder[created] = ObjectOrder[firstBoundary];
            ObjectSubOrder[created] = ObjectSubOrder[firstBoundary] - 0.5d;
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        createdObject = created;
        animationContours = new[] { region.Contour.ToArray() };
        return true;
    }

    public bool TryGetClosedStrokeFillRegion(PointF world, int frame, out PointF[][] contours)
    {
        contours = Array.Empty<PointF[]>();
        if (!TryFindClosedStrokeFillRegion(world, frame, out var region)) return false;
        contours = new[] { region.Contour.ToArray() };
        return true;
    }

    private bool TryFindClosedStrokeFillRegion(PointF world, int frame, out ClosedStrokeFillRegion region)
    {
        region = default;
        const int maximumRawSegments = 2048;

        var rawSegments = new List<ClosedFillRawSegment>();
        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            if (ObjectLayer[objectIndex] != ActiveLayer
                || !IsObjectActive(objectIndex, frame)
                || !HasStroke(objectIndex))
            {
                continue;
            }

            foreach (var part in BuildConnectedStrokeParts(objectIndex, frame))
            {
                var points = ClosedFillPartPoints(part, frame);
                if (points.Length < 2) continue;
                for (var pointIndex = 0; pointIndex < points.Length - 1; pointIndex++)
                {
                    if (Distance(points[pointIndex], points[pointIndex + 1]) <= 0.001f) continue;
                    rawSegments.Add(new ClosedFillRawSegment(points[pointIndex], points[pointIndex + 1], objectIndex));
                    if (rawSegments.Count >= maximumRawSegments) break;
                }

                if (rawSegments.Count >= maximumRawSegments) break;
            }

            if (rawSegments.Count >= maximumRawSegments) break;
        }

        if (rawSegments.Count < 3) return false;

        var splitParameters = rawSegments
            .Select(_ => new List<float> { 0f, 1f })
            .ToArray();
        for (var left = 0; left < rawSegments.Count; left++)
        {
            var first = rawSegments[left];
            for (var right = left + 1; right < rawSegments.Count; right++)
            {
                var second = rawSegments[right];
                if (!ClosedFillSegmentBoundsOverlap(first.Start, first.End, second.Start, second.End)) continue;
                if (TryClosedFillSegmentIntersection(first.Start, first.End, second.Start, second.End, out var firstT, out var secondT))
                {
                    splitParameters[left].Add(firstT);
                    splitParameters[right].Add(secondT);
                }

                AddClosedFillEndpointProjection(first.Start, second.Start, second.End, splitParameters[right]);
                AddClosedFillEndpointProjection(first.End, second.Start, second.End, splitParameters[right]);
                AddClosedFillEndpointProjection(second.Start, first.Start, first.End, splitParameters[left]);
                AddClosedFillEndpointProjection(second.End, first.Start, first.End, splitParameters[left]);
            }
        }

        var vertices = new List<PointF>();
        var edges = new List<ClosedFillGraphEdge>();
        for (var segmentIndex = 0; segmentIndex < rawSegments.Count; segmentIndex++)
        {
            var segment = rawSegments[segmentIndex];
            var parameters = splitParameters[segmentIndex]
                .OrderBy(value => value)
                .Aggregate(new List<float>(), (result, value) =>
                {
                    if (result.Count == 0 || value - result[^1] > 0.0001f) result.Add(value);
                    return result;
                });
            for (var parameterIndex = 0; parameterIndex < parameters.Count - 1; parameterIndex++)
            {
                var start = Lerp(segment.Start, segment.End, parameters[parameterIndex]);
                var end = Lerp(segment.Start, segment.End, parameters[parameterIndex + 1]);
                if (Distance(start, end) <= 0.001f) continue;
                var from = FindClosedFillVertex(vertices, start);
                var to = FindClosedFillVertex(vertices, end);
                if (from == to) continue;
                edges.Add(new ClosedFillGraphEdge(from, to, new[] { start, end }, segment.ObjectIndex));
            }
        }

        if (edges.Count < 3 || vertices.Count < 3) return false;

        var halfFrom = new List<int>(edges.Count * 2);
        var halfTo = new List<int>(edges.Count * 2);
        var outgoing = Enumerable.Range(0, vertices.Count).Select(_ => new List<int>()).ToArray();
        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            var edge = edges[edgeIndex];
            var forward = halfFrom.Count;
            halfFrom.Add(edge.From);
            halfTo.Add(edge.To);
            outgoing[edge.From].Add(forward);

            var backward = halfFrom.Count;
            halfFrom.Add(edge.To);
            halfTo.Add(edge.From);
            outgoing[edge.To].Add(backward);
        }

        for (var vertex = 0; vertex < outgoing.Length; vertex++)
        {
            outgoing[vertex].Sort((left, right) =>
            {
                var leftPoint = vertices[halfTo[left]];
                var rightPoint = vertices[halfTo[right]];
                var origin = vertices[vertex];
                var leftAngle = Math.Atan2(leftPoint.Y - origin.Y, leftPoint.X - origin.X);
                var rightAngle = Math.Atan2(rightPoint.Y - origin.Y, rightPoint.X - origin.X);
                return leftAngle.CompareTo(rightAngle);
            });
        }

        var visited = new bool[halfFrom.Count];
        var bestArea = float.MaxValue;
        PointF[]? bestContour = null;
        int[]? bestBoundaryObjects = null;
        for (var start = 0; start < halfFrom.Count; start++)
        {
            if (visited[start]) continue;

            var face = new List<int>();
            var current = start;
            var closed = false;
            for (var step = 0; step <= halfFrom.Count; step++)
            {
                if (current == start && face.Count > 0)
                {
                    closed = true;
                    break;
                }

                if (visited[current]) break;
                visited[current] = true;
                face.Add(current);

                var atVertex = halfTo[current];
                var reverse = current ^ 1;
                var fan = outgoing[atVertex];
                var reversePosition = fan.IndexOf(reverse);
                if (reversePosition < 0 || fan.Count < 2) break;
                current = fan[(reversePosition - 1 + fan.Count) % fan.Count];
            }

            if (!closed || face.Count < 3) continue;
            var facePoints = face.Select(half => vertices[halfFrom[half]]).ToArray();
            var area = PolygonArea(facePoints);
            // The half-edge walk keeps bounded faces counter-clockwise; the outer face is clockwise.
            if (area <= 0.5f || area >= bestArea || !PointInPolygonOrOnBoundary(world, facePoints)) continue;

            var contour = BuildClosedFillContour(face, edges);
            if (contour.Length < 3 || Math.Abs(PolygonArea(contour)) < 0.5f) continue;
            bestArea = area;
            bestContour = contour;
            bestBoundaryObjects = face
                .Select(half => edges[half / 2].ObjectIndex)
                .Distinct()
                .ToArray();
        }

        if (bestContour is null || bestBoundaryObjects is null) return false;
        region = new ClosedStrokeFillRegion(bestContour, bestBoundaryObjects);
        return true;
    }

    private PointF[] ClosedFillPartPoints(ConnectedStrokePart part, int frame)
    {
        var objectIndex = part.Hit.Key.ObjectIndex;
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<PointF>();

        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(objectIndex);
            var segment = QuadraticSubcurve(curve.Start, curve.Control, curve.End, part.Hit.StartT, part.Hit.EndT);
            var samples = new List<CurveSample>(12) { new(0, part.Start) };
            AddAdaptiveQuadraticSamples(samples, segment.Start, segment.Control, segment.End, 0, 1, 0);
            samples.Add(new CurveSample(1, part.End));
            return samples.Select(sample => sample.Point).ToArray();
        }

        if (IsFreehandShape(ShapeKind[objectIndex]) && TryGetFreehandWorldPoints(objectIndex, out var freehand))
        {
            return PolylineSlice(freehand, part.Hit.StartT, part.Hit.EndT);
        }

        var boundary = GetBoundaryPartPoints(part.Hit, frame);
        return boundary.Length >= 2 ? boundary : new[] { part.Start, part.End };
    }

    private static bool ClosedFillSegmentBoundsOverlap(PointF firstStart, PointF firstEnd, PointF secondStart, PointF secondEnd)
    {
        var tolerance = ConnectedStrokeEndpointToleranceUnits;
        return Math.Max(firstStart.X, firstEnd.X) + tolerance >= Math.Min(secondStart.X, secondEnd.X)
            && Math.Max(secondStart.X, secondEnd.X) + tolerance >= Math.Min(firstStart.X, firstEnd.X)
            && Math.Max(firstStart.Y, firstEnd.Y) + tolerance >= Math.Min(secondStart.Y, secondEnd.Y)
            && Math.Max(secondStart.Y, secondEnd.Y) + tolerance >= Math.Min(firstStart.Y, firstEnd.Y);
    }

    private static bool TryClosedFillSegmentIntersection(
        PointF firstStart,
        PointF firstEnd,
        PointF secondStart,
        PointF secondEnd,
        out float firstT,
        out float secondT)
    {
        firstT = 0;
        secondT = 0;
        var firstX = firstEnd.X - firstStart.X;
        var firstY = firstEnd.Y - firstStart.Y;
        var secondX = secondEnd.X - secondStart.X;
        var secondY = secondEnd.Y - secondStart.Y;
        var determinant = firstX * secondY - firstY * secondX;
        if (Math.Abs(determinant) <= 0.000001f) return false;

        var offsetX = secondStart.X - firstStart.X;
        var offsetY = secondStart.Y - firstStart.Y;
        var first = (offsetX * secondY - offsetY * secondX) / determinant;
        var second = (offsetX * firstY - offsetY * firstX) / determinant;
        var firstLength = MathF.Sqrt(firstX * firstX + firstY * firstY);
        var secondLength = MathF.Sqrt(secondX * secondX + secondY * secondY);
        var firstTolerance = ConnectedStrokeEndpointToleranceUnits / Math.Max(0.001f, firstLength);
        var secondTolerance = ConnectedStrokeEndpointToleranceUnits / Math.Max(0.001f, secondLength);
        if (first < -firstTolerance || first > 1 + firstTolerance
            || second < -secondTolerance || second > 1 + secondTolerance)
        {
            return false;
        }

        firstT = Math.Clamp(first, 0, 1);
        secondT = Math.Clamp(second, 0, 1);
        return true;
    }

    private static void AddClosedFillEndpointProjection(PointF point, PointF segmentStart, PointF segmentEnd, List<float> parameters)
    {
        var dx = segmentEnd.X - segmentStart.X;
        var dy = segmentEnd.Y - segmentStart.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.000001f) return;

        var length = MathF.Sqrt(lengthSquared);
        var parameter = ((point.X - segmentStart.X) * dx + (point.Y - segmentStart.Y) * dy) / lengthSquared;
        var tolerance = ConnectedStrokeEndpointToleranceUnits / length;
        if (parameter < -tolerance || parameter > 1 + tolerance) return;
        var clamped = Math.Clamp(parameter, 0, 1);
        var closest = Lerp(segmentStart, segmentEnd, clamped);
        if (Distance(point, closest) <= ConnectedStrokeEndpointToleranceUnits) parameters.Add(clamped);
    }

    private static int FindClosedFillVertex(List<PointF> vertices, PointF point)
    {
        for (var index = 0; index < vertices.Count; index++)
        {
            if (Distance(vertices[index], point) <= ConnectedStrokeEndpointToleranceUnits) return index;
        }

        vertices.Add(VectorUnits.Quantize(point));
        return vertices.Count - 1;
    }

    private static PointF[] BuildClosedFillContour(IReadOnlyList<int> face, IReadOnlyList<ClosedFillGraphEdge> edges)
    {
        var result = new List<PointF>();
        foreach (var half in face)
        {
            var edge = edges[half / 2];
            var forward = (half & 1) == 0;
            var points = edge.Points;
            for (var offset = 0; offset < points.Length - 1; offset++)
            {
                var index = forward ? offset : points.Length - 1 - offset;
                var point = VectorUnits.Quantize(points[index]);
                if (result.Count == 0 || Distance(result[^1], point) > 0.001f) result.Add(point);
            }
        }

        if (result.Count > 1 && Distance(result[0], result[^1]) <= 0.001f) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }

    private static (int X, int Y) ConnectedEndpointKey(PointF point)
    {
        return ((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
    }

    public long EstimateElementAtomCount(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= ObjectCount) return 0;
        var atoms = Math.Max(1u, AtomCount[hit.Key.ObjectIndex]);
        if (hit.Key.Kind == DrawingElementKind.Fill)
        {
            var candidates = CollectTopologyCandidates(hit.Key.ObjectIndex, frame);
            var regions = BuildFillRegions(hit.Key.ObjectIndex, candidates);
            if ((uint)hit.Key.PartIndex >= regions.Count) return 0;
            var total = regions.Sum(region => Math.Max(0.001f, region.Area));
            var fraction = total > 0 ? regions[hit.Key.PartIndex].Area / total : 1;
            return Math.Max(1, (long)Math.Round(atoms * Math.Clamp(fraction, 0, 1)));
        }

        var parts = hit.Key.Kind == DrawingElementKind.BoundaryStroke
            ? GetBoundaryParts(hit.Key.ObjectIndex, frame)
            : GetStrokeParts(hit.Key.ObjectIndex, frame);
        if (parts.Length > 0)
        {
            var total = parts.Sum(part => Math.Max(0.001f, PolylineLength(part.Points)));
            var selected = parts.FirstOrDefault(part => part.PartIndex == hit.Key.PartIndex);
            if (selected.Points is not null)
            {
                var fraction = total > 0 ? PolylineLength(selected.Points) / total : 1;
                return Math.Max(1, (long)Math.Round(atoms * Math.Clamp(fraction, 0, 1)));
            }
        }

        var parameterFraction = Math.Clamp(hit.EndT - hit.StartT, 0, 1);
        return Math.Max(1, (long)Math.Round(atoms * parameterFraction));
    }

    public bool FillContainsPoint(int objectIndex, PointF world)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var shape = ShapeKind.Length > objectIndex ? ShapeKind[objectIndex] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (!IsFillShape(shape) || !HasFill(objectIndex)) return false;
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(objectIndex, out var contours))
        {
            return PointInCompoundPolygonOrOnBoundary(world, contours);
        }

        var polygon = OpenPolygon(ShapeBoundary(objectIndex));
        return polygon.Length >= 3 && PointInPolygonOrOnBoundary(world, polygon);
    }

    public bool TryGetPathWorldPoints(int objectIndex, out PointF[] points)
    {
        if (!TryGetPathWorldContours(objectIndex, out var contours) || contours.Length == 0)
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = contours[0];
        return true;
    }

    public bool TryGetPathWorldContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex >= ObjectCount || !_pathLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = Array.Empty<PointF[]>();
            return false;
        }

        contours = new PointF[localContours.Length][];
        for (var c = 0; c < localContours.Length; c++)
        {
            var local = localContours[c];
            var world = new PointF[local.Length];
            for (var i = 0; i < local.Length; i++) world[i] = LocalToWorld(objectIndex, local[i].X, local[i].Y);
            contours[c] = world;
        }

        return contours.Length > 0;
    }

    internal bool TryGetPathLocalContours(int objectIndex, out PointF[][] contours)
    {
        if ((uint)objectIndex < ObjectCount && _pathLocalContours.TryGetValue(objectIndex, out var localContours))
        {
            contours = localContours;
            return contours.Length > 0;
        }

        contours = Array.Empty<PointF[]>();
        return false;
    }

    public bool TryGetLineEndpoint(int objectIndex, bool startEndpoint, out PointF point)
    {
        point = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var halfW = Width[objectIndex] * 0.5f;
        point = LocalToWorld(objectIndex, startEndpoint ? -halfW : halfW, 0);
        return true;
    }

    public bool TryGetLineJoinNeighbor(
        int objectIndex,
        bool startEndpoint,
        int frame,
        out int neighborObjectIndex,
        out bool neighborStartEndpoint)
    {
        neighborObjectIndex = -1;
        neighborStartEndpoint = false;
        if (!TryGetLineEndpointJunction(objectIndex, startEndpoint, frame, out var junction)
            || junction.NeighborCount != 1)
        {
            return false;
        }

        neighborObjectIndex = junction.FirstNeighborObjectIndex;
        neighborStartEndpoint = junction.FirstNeighborStartEndpoint;
        return true;
    }

    internal bool TryGetLineEndpointJunction(
        int objectIndex,
        bool startEndpoint,
        int frame,
        out LineEndpointJunction junction)
    {
        junction = default;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || !IsObjectActive(objectIndex, frame)
            || !TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint))
        {
            return false;
        }

        var tolerance = ConnectedStrokeEndpointToleranceUnits;
        var bounds = RectangleF.FromLTRB(
            endpoint.X - tolerance,
            endpoint.Y - tolerance,
            endpoint.X + tolerance,
            endpoint.Y + tolerance);
        var neighborCount = 0;
        var ownerObjectIndex = objectIndex;
        var firstNeighborObjectIndex = -1;
        var firstNeighborStartEndpoint = false;
        var allSharp = GetLineEndpointStyle(objectIndex, startEndpoint) == LineEndpointStyle.Sharp;
        List<LineEndpointConnection>? connections = null;
        foreach (var candidate in QueryObjects(bounds, frame))
        {
            if (candidate == objectIndex
                || ShapeKind[candidate] != VectorAnimationEngine.ShapeKind.Line
                || ObjectLayer[candidate] != ObjectLayer[objectIndex]
                || ObjectKeyframeFrame[candidate] != ObjectKeyframeFrame[objectIndex]
                || StrokeArgb[candidate] != StrokeArgb[objectIndex]
                || Math.Abs(Stroke[candidate] - Stroke[objectIndex]) > 0.001f)
            {
                continue;
            }

            var connectedAtStart = TryGetLineEndpoint(candidate, startEndpoint: true, out var candidateStart)
                && Distance(candidateStart, endpoint) <= tolerance;
            var connectedAtEnd = !connectedAtStart
                && TryGetLineEndpoint(candidate, startEndpoint: false, out var candidateEnd)
                && Distance(candidateEnd, endpoint) <= tolerance;
            if (!connectedAtStart && !connectedAtEnd) continue;

            if (firstNeighborObjectIndex < 0)
            {
                firstNeighborObjectIndex = candidate;
                firstNeighborStartEndpoint = connectedAtStart;
            }

            neighborCount++;
            connections ??= new List<LineEndpointConnection>(2);
            connections.Add(new LineEndpointConnection(candidate, connectedAtStart));
            ownerObjectIndex = Math.Min(ownerObjectIndex, candidate);
            allSharp &= GetLineEndpointStyle(candidate, connectedAtStart) == LineEndpointStyle.Sharp;
        }

        if (neighborCount == 0) return false;
        junction = new LineEndpointJunction(
            neighborCount,
            ownerObjectIndex,
            firstNeighborObjectIndex,
            firstNeighborStartEndpoint,
            allSharp,
            ownerObjectIndex == objectIndex ? connections!.ToArray() : Array.Empty<LineEndpointConnection>());
        return true;
    }

    public bool TryGetLineBezierPart(
        int objectIndex,
        float startT,
        float endT,
        out PointF start,
        out PointF control,
        out PointF end)
    {
        start = PointF.Empty;
        control = PointF.Empty;
        end = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;

        var curve = LineCurve(objectIndex);
        (start, control, end) = QuadraticSubcurve(curve.Start, curve.Control, curve.End, startT, endT);
        return true;
    }

    public bool TryGetClosestPointOnLine(
        int objectIndex,
        PointF world,
        out float parameter,
        out PointF point,
        out float distance)
    {
        parameter = 0;
        point = PointF.Empty;
        distance = float.MaxValue;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;

        var curve = LineCurve(objectIndex);
        var samples = CurveSamples(objectIndex);
        if (samples.Length < 2) return false;
        for (var index = 1; index < samples.Length; index++)
        {
            var a = samples[index - 1];
            var b = samples[index];
            var dx = b.Point.X - a.Point.X;
            var dy = b.Point.Y - a.Point.Y;
            var lengthSquared = dx * dx + dy * dy;
            var segmentParameter = lengthSquared <= DrawingTopologyRules.UnitIntersectionTolerance
                ? 0f
                : Math.Clamp(((world.X - a.Point.X) * dx + (world.Y - a.Point.Y) * dy) / lengthSquared, 0f, 1f);
            var candidateParameter = a.T + (b.T - a.T) * segmentParameter;
            var candidate = QuadraticPoint(curve.Start, curve.Control, curve.End, candidateParameter);
            var candidateDistance = Distance(world, candidate);
            if (candidateDistance >= distance) continue;
            parameter = candidateParameter;
            point = candidate;
            distance = candidateDistance;
        }

        var radius = 1f / Math.Max(8, samples.Length - 1);
        var low = Math.Max(0, parameter - radius);
        var high = Math.Min(1, parameter + radius);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var first = low + (high - low) / 3f;
            var second = high - (high - low) / 3f;
            var firstPoint = QuadraticPoint(curve.Start, curve.Control, curve.End, first);
            var secondPoint = QuadraticPoint(curve.Start, curve.Control, curve.End, second);
            if (Distance(world, firstPoint) <= Distance(world, secondPoint)) high = second;
            else low = first;
        }

        parameter = (low + high) * 0.5f;
        point = QuadraticPoint(curve.Start, curve.Control, curve.End, parameter);
        distance = Distance(world, point);
        return true;
    }

    public bool SplitLineAt(int objectIndex, float parameter, out LineAnchorSplitResult result)
    {
        result = default;
        if ((uint)objectIndex >= ObjectCount
            || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line
            || parameter <= 0.001f
            || parameter >= 0.999f)
        {
            return false;
        }

        var curve = LineCurve(objectIndex);
        var firstControl = Lerp(curve.Start, curve.Control, parameter);
        var secondControl = Lerp(curve.Control, curve.End, parameter);
        var anchor = VectorUnits.Quantize(Lerp(firstControl, secondControl, parameter));
        firstControl = VectorUnits.Quantize(firstControl);
        secondControl = VectorUnits.Quantize(secondControl);
        if (Distance(curve.Start, anchor) < DrawingTopologyRules.MinStrokeSegmentUnits
            || Distance(anchor, curve.End) < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return false;
        }

        var layer = ObjectLayer[objectIndex];
        var keyframeFrame = ObjectKeyframeFrame[objectIndex];
        var order = ObjectOrder[objectIndex];
        var subOrder = ObjectSubOrder[objectIndex];
        var stroke = Stroke[objectIndex];
        var fillColor = Color.FromArgb(Argb[objectIndex]);
        var strokeColor = Color.FromArgb(StrokeArgb[objectIndex]);
        var startStyle = GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = GetLineEndpointStyle(objectIndex, startEndpoint: false);
        var gradientPaint = CaptureGradientPaint(objectIndex);
        var sourceAtoms = Math.Max(6u, AtomCount[objectIndex]);
        var firstAtoms = Math.Max(3u, (uint)Math.Round(sourceAtoms * parameter));
        var secondAtoms = Math.Max(3u, sourceAtoms - Math.Min(sourceAtoms - 3u, firstAtoms));
        if (firstAtoms + secondAtoms != sourceAtoms)
        {
            firstAtoms = Math.Max(3u, sourceAtoms / 2u);
            secondAtoms = Math.Max(3u, sourceAtoms - firstAtoms);
        }

        SetLineCurve(objectIndex, curve.Start, firstControl, anchor);
        LineEndpointStyles[objectIndex] = startStyle;
        LineEndEndpointStyles[objectIndex] = LineEndpointStyle.Round;
        VirtualAtomCount -= AtomCount[objectIndex];
        AtomCount[objectIndex] = firstAtoms;
        VirtualAtomCount += firstAtoms;

        var secondObject = AppendCurveSegment(
            layer,
            anchor,
            secondControl,
            curve.End,
            stroke,
            fillColor,
            strokeColor,
            secondAtoms,
            LineEndpointStyle.Round,
            endStyle);
        ObjectLayer[secondObject] = layer;
        ObjectKeyframeFrame[secondObject] = keyframeFrame;
        ObjectOrder[secondObject] = order;
        ObjectSubOrder[secondObject] = Math.BitIncrement(subOrder);
        ApplyGradientPaint(objectIndex, gradientPaint);
        ApplyGradientPaint(secondObject, gradientPaint);
        RebuildGeometryIndex();
        RebuildSummaries();
        result = new LineAnchorSplitResult(objectIndex, secondObject, anchor);
        return true;
    }

    public bool IsLineStraight(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var halfW = Width[objectIndex] * 0.5f;
        var start = LocalToWorld(objectIndex, -halfW, 0);
        var end = LocalToWorld(objectIndex, halfW, 0);
        return DistanceToSegment(new PointF(CurveControlX[objectIndex], CurveControlY[objectIndex]), start, end) <= DrawingTopologyRules.MinStrokeSegmentUnits;
    }

    public LineEndpointStyle GetLineEndpointStyle(int objectIndex, bool startEndpoint)
    {
        return (uint)objectIndex < ObjectCount
            && ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line
            && LineEndpointStyles.Length > objectIndex
            && LineEndEndpointStyles.Length > objectIndex
            ? NormalizeLineEndpointStyle(startEndpoint ? LineEndpointStyles[objectIndex] : LineEndEndpointStyles[objectIndex])
            : LineEndpointStyle.Round;
    }

    public bool SetLineEndpointStyle(int objectIndex, bool startEndpoint, LineEndpointStyle endpointStyle)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var normalized = NormalizeLineEndpointStyle(endpointStyle);
        var styles = startEndpoint ? LineEndpointStyles : LineEndEndpointStyles;
        if (styles[objectIndex] == normalized) return false;
        styles[objectIndex] = normalized;
        return true;
    }

    public LineEndpointStyle GetLineEndpointStyle(int objectIndex) => GetLineEndpointStyle(objectIndex, startEndpoint: true);

    public bool SetLineEndpointStyle(int objectIndex, LineEndpointStyle endpointStyle)
    {
        var startChanged = SetLineEndpointStyle(objectIndex, startEndpoint: true, endpointStyle);
        var endChanged = SetLineEndpointStyle(objectIndex, startEndpoint: false, endpointStyle);
        return startChanged || endChanged;
    }

    public void SetLineEndpoint(int objectIndex, bool startEndpoint, PointF endpoint, PointF oppositeEndpoint, PointF control, bool keepStraight)
    {
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return;
        var start = startEndpoint ? endpoint : oppositeEndpoint;
        var end = startEndpoint ? oppositeEndpoint : endpoint;
        var center = Midpoint(start, end);
        X[objectIndex] = center.X;
        Y[objectIndex] = center.Y;
        Width[objectIndex] = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, Distance(start, end));
        Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), Height[objectIndex]);
        Angle[objectIndex] = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        var newControl = keepStraight ? Midpoint(start, end) : control;
        CurveControlX[objectIndex] = newControl.X;
        CurveControlY[objectIndex] = newControl.Y;
    }

    public DrawingElementHit DetachElementForMove(DrawingElementHit hit, int frame)
    {
        if (!hit.IsValid || (uint)hit.Key.ObjectIndex >= ObjectCount) return hit;
        var materialized = MaterializeSelectedParts(new[] { hit.Key }, frame);
        if (!materialized.Success || materialized.Parts.Length != 1) return DrawingElementHit.None;
        if (!materialized.Changed) return hit;
        return new DrawingElementHit(materialized.Parts[0].Result, -1, 0, 1);
    }

    public FillBoundaryLineLink[] CaptureFillBoundaryLineLinks(int lineObjectIndex, int frame)
    {
        if ((uint)lineObjectIndex >= ObjectCount
            || !IsFillBoundaryLinkedStrokeShape(ShapeKind[lineObjectIndex])
            || !IsObjectActive(lineObjectIndex, frame))
        {
            return Array.Empty<FillBoundaryLineLink>();
        }

        var linePoints = StrokeSamples(lineObjectIndex)
            .Select(sample => VectorUnits.Quantize(sample.Point))
            .ToArray();
        if (linePoints.Length < 2) return Array.Empty<FillBoundaryLineLink>();
        var reverseLinePoints = linePoints.Reverse().ToArray();

        var links = new List<FillBoundaryLineLink>();
        var queryBounds = GetObjectWorldBounds(lineObjectIndex);
        queryBounds.Inflate(ConnectedStrokeEndpointToleranceUnits, ConnectedStrokeEndpointToleranceUnits);
        foreach (var fillObjectIndex in QueryObjects(queryBounds, frame))
        {
            if (ObjectLayer[fillObjectIndex] != ObjectLayer[lineObjectIndex]
                || ObjectKeyframeFrame[fillObjectIndex] != ObjectKeyframeFrame[lineObjectIndex]
                || !HasFill(fillObjectIndex))
            {
                continue;
            }

            var contours = ShapeBoundaryContours(fillObjectIndex);
            PointF[][]? originalContours = null;
            for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
            {
                var contour = contours[contourIndex];
                for (var segmentIndex = 0; segmentIndex < contour.Length - 1; segmentIndex++)
                {
                    var forward = BoundarySequenceMatches(contour, segmentIndex, linePoints);
                    var reverse = !forward
                        && BoundarySequenceMatches(contour, segmentIndex, reverseLinePoints);
                    if (!forward && !reverse) continue;
                    originalContours ??= CloneContours(contours);
                    links.Add(new FillBoundaryLineLink(
                        lineObjectIndex,
                        fillObjectIndex,
                        originalContours,
                        contourIndex,
                        segmentIndex,
                        linePoints.Length - 1,
                        Reversed: reverse));
                }
            }
        }

        return links.ToArray();
    }

    private FillBoundaryLineLink[] CaptureLinkedFillBoundariesForTransform(IReadOnlyCollection<int> targets)
    {
        if (targets.Count == 0) return Array.Empty<FillBoundaryLineLink>();

        var transformed = targets.ToHashSet();
        var links = new List<FillBoundaryLineLink>();
        foreach (var lineObjectIndex in targets)
        {
            if (!IsFillBoundaryLinkedStrokeShape(ShapeKind[lineObjectIndex])) continue;
            foreach (var link in CaptureFillBoundaryLineLinks(lineObjectIndex, EditFrame))
            {
                // A selected fill has already received the same affine transform.
                // Replacing it from a pre-transform contour would undo that transform.
                if (!transformed.Contains(link.FillObjectIndex)) links.Add(link);
            }
        }

        return links.ToArray();
    }

    public bool UpdateFillBoundaryLineLinks(
        IReadOnlyList<FillBoundaryLineLink> links,
        bool rebuildGeometryIndex = true)
    {
        if (links.Count == 0) return false;

        var changed = false;
        foreach (var fillLinks in links
                     .GroupBy(link => link.FillObjectIndex)
                     .OrderBy(group => group.Key))
        {
            var fillObjectIndex = fillLinks.Key;
            if ((uint)fillObjectIndex >= ObjectCount
                || !IsFillShape(ShapeKind[fillObjectIndex]))
            {
                continue;
            }

            var template = fillLinks.First();
            var contours = CloneContours(template.OriginalContours);
            var fillChanged = false;
            foreach (var contourLinks in fillLinks
                         .GroupBy(link => link.ContourIndex)
                         .OrderBy(group => group.Key))
            {
                var contourIndex = contourLinks.Key;
                if (contourIndex < 0 || contourIndex >= contours.Length) continue;

                var contour = contours[contourIndex];
                foreach (var link in contourLinks
                             .OrderByDescending(link => link.SegmentIndex)
                             .ThenBy(link => link.LineObjectIndex))
                {
                    if ((uint)link.LineObjectIndex >= ObjectCount
                        || !IsFillBoundaryLinkedStrokeShape(ShapeKind[link.LineObjectIndex])
                        || link.SegmentIndex < 0
                        || link.SegmentCount <= 0
                        || link.SegmentIndex + link.SegmentCount >= contour.Length)
                    {
                        continue;
                    }

                    var linePoints = StrokeSamples(link.LineObjectIndex)
                        .Select(sample => VectorUnits.Quantize(sample.Point))
                        .ToArray();
                    if (linePoints.Length < 2) continue;

                    var replacement = link.Reversed ? linePoints.Reverse().ToArray() : linePoints;
                    var updatedContour = ReplaceBoundarySegment(
                        contour,
                        link.SegmentIndex,
                        link.SegmentCount,
                        replacement);
                    if (updatedContour.Length < 3) continue;
                    contour = updatedContour;
                    fillChanged = true;
                }

                contours[contourIndex] = contour;
            }

            if (!fillChanged) continue;
            ShapeKind[fillObjectIndex] = VectorAnimationEngine.ShapeKind.Path;
            ShapeVertexCounts[fillObjectIndex] = 0;
            SetPathContours(fillObjectIndex, contours);
            changed = true;
        }

        if (changed && rebuildGeometryIndex)
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }

        return changed;
    }

    public MaterializeSelectedPartsResult MaterializeSelectedParts(IReadOnlyList<DrawingElementKey> selectedParts, int frame)
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
        var mappedResults = new Dictionary<DrawingElementKey, DrawingElementKey>();

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
            var layer = ObjectLayer[source];
            var order = ObjectOrder[source];
            var stroke = Stroke[source];
            var fillColor = Color.FromArgb(Argb[source]);
            var strokeColor = Color.FromArgb(StrokeArgb[source]);
            var gradientPaint = CaptureGradientPaint(source);
            var atoms = AtomCount[source];

            if (IsTopologyStrokeShape(shape))
            {
                if (!HasStroke(source) || sourceKeys.Any(key => key.Kind != DrawingElementKind.Stroke))
                {
                    return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
                }

                var splits = StrokeSplitParameters(source, candidates);
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
                    var segments = BuildCurveParts(curve.Start, curve.Control, curve.End, splits);
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
                            segment.Control,
                            segment.End,
                            segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                            segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round));
                    }
                }
                else
                {
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
            var regions = fillKeys.Length > 0 ? BuildFillRegions(source, candidates) : new List<FillRegion>();
            if (selectedFillParts.Keys.Any(part => part < 0 || part >= regions.Count))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            var boundaryParts = boundaryKeys.Length > 0 || fillKeys.Length > 0 && HasStroke(source)
                ? BuildBoundaryStrokeParts(source, candidates)
                : new List<BoundaryStrokePart>();
            if (selectedBoundaryParts.Keys.Any(part => boundaryParts.All(segment => segment.PartIndex != part)))
            {
                return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
            }

            if (fillKeys.Length > 0)
            {
                var detachBoundary = HasStroke(source);
                if (regions.Count <= 1 && !detachBoundary)
                {
                    foreach (var key in fillKeys) mappedResults[key] = key;
                    continue;
                }

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
                        additions.Add(PolylineMaterialization(
                            selectedBoundaryParts.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                            layer,
                            order,
                            replacementSubOrders[replacementSubOrderIndex++],
                            stroke,
                            fillColor,
                            strokeColor,
                            boundaryAtoms,
                            segment.Points));
                    }
                }

                var fillAtoms = Math.Max(3u, atoms / (uint)Math.Max(1, regions.Count));
                for (var part = 0; part < regions.Count; part++)
                {
                    additions.Add(FillMaterialization(
                        selectedFillParts.GetValueOrDefault(part, DrawingElementKey.None),
                        layer,
                        order,
                        replacementSubOrders[replacementSubOrderIndex++],
                        fillColor,
                        strokeColor,
                        fillAtoms,
                        regions[part].Contours,
                        gradientPaint));
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
                additions.Add(PolylineMaterialization(
                    selectedBoundaryParts.GetValueOrDefault(segment.PartIndex, DrawingElementKey.None),
                    layer,
                    order,
                    boundarySubOrders[boundarySubOrderIndex++],
                    stroke,
                    fillColor,
                    strokeColor,
                    atomsPerBoundary,
                    segment.Points));
            }
        }

        var changed = remove.Any(value => value) || clearStroke.Count > 0 || additions.Count > 0;
        if (!changed)
        {
            var identity = Enumerable.Range(0, oldObjectCount).ToArray();
            var unchanged = keys.Select(key => new MaterializedPartMapping(key, mappedResults[key])).ToArray();
            return new MaterializeSelectedPartsResult(true, false, unchanged, identity);
        }

        var snapshot = CreateSnapshot();
        try
        {
            var oldToNew = CompactObjectsForMaterialization(remove);
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

            foreach (var addition in additions)
            {
                var index = AppendMaterializedPart(addition);
                if (index < 0) throw new InvalidOperationException("Topology materialization produced invalid replacement geometry.");
                if (addition.SourceKey.IsValid)
                {
                    var kind = addition.Geometry == MaterializedPartGeometry.Fill
                        ? DrawingElementKind.Fill
                        : DrawingElementKind.Stroke;
                    mappedResults[addition.SourceKey] = new DrawingElementKey(index, kind, 0);
                }
            }

            RebuildGeometryIndex();
            RebuildSummaries();
            var mappings = keys.Select(key => new MaterializedPartMapping(key, mappedResults[key])).ToArray();
            return new MaterializeSelectedPartsResult(true, true, mappings, oldToNew);
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return new MaterializeSelectedPartsResult(false, false, Array.Empty<MaterializedPartMapping>(), Array.Empty<int>());
        }
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
        PointF control,
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
            control,
            end,
            Array.Empty<PointF>(),
            Array.Empty<PointF[]>());
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
            points,
            Array.Empty<PointF[]>());
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
        GradientPaintData? gradientPaint = null)
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
            Array.Empty<PointF>(),
            contours)
        {
            GradientPaint = gradientPaint
        };
    }

    private int AppendMaterializedPart(MaterializedPartAddition addition)
    {
        var index = addition.Geometry switch
        {
            MaterializedPartGeometry.Curve => AppendCurveSegment(
                addition.Layer,
                addition.Start,
                addition.Control,
                addition.End,
                addition.Stroke,
                addition.FillColor,
                addition.StrokeColor,
                addition.Atoms,
                addition.StartEndpointStyle,
                addition.EndEndpointStyle),
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
            ObjectOrder[index] = addition.Order;
            ObjectSubOrder[index] = addition.SubOrder;
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
            if (write != read) CopyObjectData(read, write);
            write++;
        }

        ObjectCount = write;
        VirtualAtomCount = Math.Max(0, VirtualAtomCount - removedAtoms);
        ResizeObjectArrays();
        RemovePathDataOutsideObjectCount();
        RemoveFreehandDataOutsideObjectCount();
        RemoveGradientDataOutsideObjectCount();
        return oldToNew;
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeParts(RectangleF worldBounds, int frame)
    {
        return MaterializeMarqueeParts(worldBounds, frame, includeLines: true);
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeFillParts(RectangleF worldBounds, int frame)
    {
        return MaterializeMarqueeParts(worldBounds, frame, includeLines: false);
    }

    private (bool Changed, int[] SelectedObjects) MaterializeMarqueeParts(
        RectangleF worldBounds,
        int frame,
        bool includeLines)
    {
        var bounds = NormalizeToDrawingUnits(worldBounds);
        if (bounds.Width < DrawingTopologyRules.MinStrokeSegmentUnits || bounds.Height < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return (false, Array.Empty<int>());
        }

        var candidates = QueryObjects(bounds, frame);
        if (candidates.Length == 0) return (false, Array.Empty<int>());

        var activeCandidates = CollectActiveCandidates(frame);
        var remove = new HashSet<int>();
        var additions = new List<MarqueePartAddition>();

        foreach (var index in candidates)
        {
            if ((uint)index >= ObjectCount || !IsObjectActive(index, frame)) continue;
            var shape = ShapeKind.Length > index ? ShapeKind[index] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape == VectorAnimationEngine.ShapeKind.Line && includeLines)
            {
                AddLineMarqueeParts(index, bounds, additions, remove);
            }
            else if (shape == VectorAnimationEngine.ShapeKind.Line || IsFreehandShape(shape))
            {
                continue;
            }
            else
            {
                AddFillMarqueeParts(index, bounds, activeCandidates, additions, remove);
            }
        }

        if (remove.Count == 0) return (false, Array.Empty<int>());

        RemoveObjects(remove);
        var selected = new List<int>();
        foreach (var addition in additions)
        {
            int newIndex;
            if (addition.IsLine)
            {
                newIndex = AddCurveSegment(
                    addition.Layer,
                    addition.Start,
                    addition.Control,
                    addition.End,
                    addition.Stroke,
                    addition.FillColor,
                    addition.StrokeColor,
                    addition.Atoms,
                    addition.StartEndpointStyle,
                    addition.EndEndpointStyle);
            }
            else
            {
                var contours = addition.Contours.Length > 0 ? addition.Contours : new[] { addition.Points };
                newIndex = AddPathObjectContours(addition.Layer, contours, addition.Stroke, addition.FillColor, addition.StrokeColor, addition.Atoms);
            }

            if (newIndex >= 0) ApplyGradientPaint(newIndex, addition.GradientPaint);
            if (newIndex >= 0 && addition.Selected) selected.Add(newIndex);
        }

        return (true, selected.ToArray());
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeLineParts(RectangleF worldBounds, int frame)
    {
        var bounds = NormalizeToDrawingUnits(worldBounds);
        if (bounds.Width < DrawingTopologyRules.MinStrokeSegmentUnits || bounds.Height < DrawingTopologyRules.MinStrokeSegmentUnits)
        {
            return (false, Array.Empty<int>());
        }

        var candidates = QueryObjects(bounds, frame);
        if (candidates.Length == 0) return (false, Array.Empty<int>());

        var remove = new bool[ObjectCount];
        var additions = new List<(MaterializedPartAddition Addition, bool Selected)>();
        foreach (var source in candidates)
        {
            if ((uint)source >= ObjectCount
                || ShapeKind[source] != VectorAnimationEngine.ShapeKind.Line
                || !IsObjectActive(source, frame))
            {
                continue;
            }

            var curve = LineCurve(source);
            var splits = LineRectSplitParameters(source, bounds);
            if (splits.Count <= 2) continue;

            var segments = BuildCurveParts(curve.Start, curve.Control, curve.End, splits);
            if (segments.Count <= 1) continue;

            var selected = segments
                .Select(segment => QuadraticCurveInsideRectangle(segment.Start, segment.Control, segment.End, bounds))
                .ToArray();
            if (!selected.Any(value => value)) continue;

            remove[source] = true;
            var subOrders = ReplacementSubOrders(source, segments.Count);
            var gradientPaint = CaptureGradientPaint(source);
            for (var part = 0; part < segments.Count; part++)
            {
                var segment = segments[part];
                additions.Add((
                    CurveMaterialization(
                        DrawingElementKey.None,
                        ObjectLayer[source],
                        ObjectOrder[source],
                        subOrders[part],
                        Stroke[source],
                        Color.FromArgb(Argb[source]),
                        Color.FromArgb(StrokeArgb[source]),
                        Math.Max(3u, AtomCount[source] / (uint)segments.Count),
                        segment.Start,
                        segment.Control,
                        segment.End,
                        segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                        segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round)
                    with { GradientPaint = gradientPaint },
                    selected[part]));
            }
        }

        if (!remove.Any(value => value)) return (false, Array.Empty<int>());

        var snapshot = CreateSnapshot();
        var editFrame = EditFrame;
        try
        {
            EditFrame = Math.Max(0, frame);
            CompactObjectsForMaterialization(remove);
            var selectedObjects = new List<int>();
            foreach (var item in additions)
            {
                var index = AppendMaterializedPart(item.Addition);
                if (index < 0) throw new InvalidOperationException("Marquee line materialization produced invalid replacement geometry.");
                if (item.Selected) selectedObjects.Add(index);
            }

            SynchronizeAllKeyframeContentKinds();
            RebuildGeometryIndex();
            RebuildSummaries();
            return (true, selectedObjects.ToArray());
        }
        catch
        {
            RestoreSnapshot(snapshot);
            return (false, Array.Empty<int>());
        }
        finally
        {
            EditFrame = editFrame;
        }
    }

    public int MergeSameColorFillsAround(int objectIndex, bool connectNearby = true, int frame = 0)
    {
        if ((uint)objectIndex >= ObjectCount) return objectIndex;
        if (!IsFillShape(ShapeKind[objectIndex])) return objectIndex;
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
                if (other == current) continue;
                if (ObjectKeyframeFrame[other] != ObjectKeyframeFrame[current]) continue;
                if (!TryBuildSameColorFillMerge(current, other, connectNearby, mergeBounds, out var mergedPath)) continue;

                var layer = ObjectLayer[current];
                var topSource = CompareObjectStack(current, other) >= 0 ? current : other;
                var order = ObjectOrder[topSource];
                var subOrder = ObjectSubOrder[topSource];
                var fillColor = Color.FromArgb(Argb[current]);
                var strokeColor = Color.FromArgb(StrokeArgb[current]);
                var atoms = Math.Max(3u, AtomCount[current] + AtomCount[other]);
                var boundaryAdditions = new List<MaterializedPartAddition>();
                PlanBoundaryMaterializations(current, frame, boundaryAdditions, preserveSourceSubOrder: current == topSource);
                PlanBoundaryMaterializations(other, frame, boundaryAdditions, preserveSourceSubOrder: other == topSource);
                var mergeSource = current;
                var snapshot = CreateSnapshot();
                try
                {
                    var remove = new bool[ObjectCount];
                    remove[current] = true;
                    remove[other] = true;
                    CompactObjectsForMaterialization(remove);
                    current = AppendPathObjectContours(layer, mergedPath, 0, fillColor, strokeColor, atoms);
                    if (current < 0) throw new InvalidOperationException("Merged fill geometry was invalid.");
                    ObjectOrder[current] = order;
                    ObjectSubOrder[current] = subOrder;
                    foreach (var addition in boundaryAdditions)
                    {
                        if (AppendMaterializedPart(addition) < 0)
                        {
                            throw new InvalidOperationException("A fill boundary could not be preserved during merge.");
                        }
                    }

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

    public int MergeMatchingShapeGradientFillsAround(int objectIndex, int frame = 0)
    {
        if ((uint)objectIndex >= ObjectCount
            || !IsMatchingShapeGradientFill(this, objectIndex)
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
                    || ObjectKeyframeFrame[other] != ObjectKeyframeFrame[current]
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
            .Where(index => (uint)index < ObjectCount && IsFillShape(ShapeKind[index]))
            .Select(index => (Order: ObjectOrder[index], SubOrder: ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        if (sourceKeys.Length == 0) return [];

        var results = new List<int>(sourceKeys.Length);
        foreach (var key in sourceKeys)
        {
            var source = FindObjectByStackKey(key.Order, key.SubOrder);
            if (source < 0 || !IsFillShape(ShapeKind[source])) continue;
            var merged = MergeMatchingShapeGradientFillsAround(source, frame);
            if ((uint)merged < ObjectCount) results.Add(merged);
        }

        var distinct = results.Distinct().ToArray();
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
        int frame)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(additionIndices);
        var matches = new HashSet<int>();
        foreach (var addition in additionIndices)
        {
            if (!IsMatchingShapeGradientFill(additions, addition)) continue;
            var bounds = additions.GetObjectWorldBounds(addition);
            var additionPaths = ToClipperPaths(additions.FillWorldContours(addition));
            if (additionPaths.Count == 0) continue;
            foreach (var candidate in QueryObjects(bounds, frame))
            {
                if (!IsObjectActive(candidate, frame)
                    || ObjectLayer[candidate] != additions.ObjectLayer[addition]
                    || !MatchingShapeGradientMaterial(this, candidate, additions, addition))
                {
                    continue;
                }

                var candidatePaths = ToClipperPaths(FillWorldContours(candidate));
                if (candidatePaths.Count > 0 && ClipperPathsIntersect(candidatePaths, additionPaths))
                {
                    matches.Add(candidate);
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
        return IsMatchingShapeGradientFill(a, aIndex)
            && IsMatchingShapeGradientFill(b, bIndex)
            && a.Argb[aIndex] == b.Argb[bIndex]
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
        mergedPath = BuildMergedFillPath(pathsA, pathsB, Array.Empty<PointF>());
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
            .Where(index => (uint)index < ObjectCount && IsFillShape(ShapeKind[index]))
            .Select(index => (Order: ObjectOrder[index], SubOrder: ObjectSubOrder[index]))
            .Distinct()
            .ToArray();
        if (sourceKeys.Length == 0) return [];

        var results = new List<int>(sourceKeys.Length);
        foreach (var key in sourceKeys)
        {
            var source = -1;
            for (var index = 0; index < ObjectCount; index++)
            {
                if (ObjectOrder[index] != key.Order || !ObjectSubOrder[index].Equals(key.SubOrder)) continue;
                source = index;
                break;
            }

            if (source < 0 || !IsFillShape(ShapeKind[source])) continue;
            var merged = MergeSameColorFillsAround(source, connectNearby, frame);
            if ((uint)merged < ObjectCount) results.Add(merged);
        }

        return results.Distinct().ToArray();
    }

    public int[] ApplyFillOverwriteToNewObjects(IReadOnlyList<int> objectIndices, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(objectIndices);
        var mergedShapeGradients = MergeMatchingShapeGradientFillsAroundNewObjects(objectIndices, frame);
        var newObjects = mergedShapeGradients
            .Where(index => (uint)index < ObjectCount && HasFill(index))
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

    private bool TryBuildFillOverwritePlan(
        int source,
        IReadOnlyList<FillOverwriteCutter> cutters,
        int frame,
        out FillOverwritePlan plan)
    {
        plan = null!;
        if ((uint)source >= ObjectCount || !HasFill(source)) return false;

        var sourceBounds = GetObjectWorldBounds(source);
        var remainingContours = FillWorldContours(source);
        var changed = false;
        List<FillRegion>? remainingRegions = null;
        foreach (var cutter in cutters)
        {
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
        for (var index = 0; index < oldObjectCount; index++)
        {
            if (scopedObjects is not null && !scopedObjects.Contains(index)) continue;
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
            additions.Add(PolylineMaterialization(
                DrawingElementKey.None,
                layer,
                order,
                subOrders[subOrderIndex++],
                stroke,
                fillColor,
                strokeColor,
                atoms,
                part.Points));
        }
    }

    private void AddFillMarqueeParts(int index, RectangleF bounds, IReadOnlyList<int> activeCandidates, List<MarqueePartAddition> additions, HashSet<int> remove)
    {
        if (!TrySplitFillByMarquee(index, bounds, out var insideContours, out var outsideContours)) return;

        remove.Add(index);
        var layer = ObjectLayer[index];
        var fillColor = Color.FromArgb(Argb[index]);
        var strokeColor = Color.FromArgb(StrokeArgb[index]);
        var gradientPaint = CaptureGradientPaint(index);
        var atoms = AtomCount[index];
        var atomsPerPart = Math.Max(3u, atoms / 2);

        if (Stroke[index] > 0)
        {
            AddBoundaryMarqueeParts(
                index,
                bounds,
                activeCandidates,
                layer,
                fillColor,
                strokeColor,
                atomsPerPart,
                additions);
        }

        additions.Add(new MarqueePartAddition(false, layer, Array.Empty<PointF>(), outsideContours, PointF.Empty, PointF.Empty, PointF.Empty, 0, fillColor, strokeColor, atomsPerPart, LineEndpointStyle.Round, LineEndpointStyle.Round, false)
        {
            GradientPaint = gradientPaint
        });
        additions.Add(new MarqueePartAddition(false, layer, Array.Empty<PointF>(), insideContours, PointF.Empty, PointF.Empty, PointF.Empty, 0, fillColor, strokeColor, atomsPerPart, LineEndpointStyle.Round, LineEndpointStyle.Round, true)
        {
            GradientPaint = gradientPaint
        });
    }

    private void AddBoundaryMarqueeParts(
        int index,
        RectangleF bounds,
        IReadOnlyList<int> activeCandidates,
        int layer,
        Color fillColor,
        Color strokeColor,
        uint atoms,
        List<MarqueePartAddition> additions)
    {
        foreach (var boundary in BuildPolylineParts(ShapeBoundary(index), BoundarySplitParameters(index, activeCandidates)))
        {
            var splits = SegmentRectSplitParameters(boundary.Start, boundary.End, bounds);
            for (var split = 0; split < splits.Count - 1; split++)
            {
                var start = Lerp(boundary.Start, boundary.End, splits[split]);
                var end = Lerp(boundary.Start, boundary.End, splits[split + 1]);
                if (Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;

                additions.Add(new MarqueePartAddition(
                    true,
                    layer,
                    Array.Empty<PointF>(),
                    Array.Empty<PointF[]>(),
                    start,
                    Midpoint(start, end),
                    end,
                    Stroke[index],
                    fillColor,
                    strokeColor,
                    atoms,
                    LineEndpointStyle.Round,
                    LineEndpointStyle.Round,
                    PointInRectangle(start, bounds) && PointInRectangle(end, bounds)));
            }
        }
    }

    private bool TrySplitFillByMarquee(int index, RectangleF bounds, out PointF[][] insideContours, out PointF[][] outsideContours)
    {
        insideContours = Array.Empty<PointF[]>();
        outsideContours = Array.Empty<PointF[]>();
        try
        {
            var source = ToClipperPaths(FillWorldContours(index));
            var marquee = ToClipperPaths(new[]
            {
                new[]
                {
                    new PointF(bounds.Left, bounds.Top),
                    new PointF(bounds.Right, bounds.Top),
                    new PointF(bounds.Right, bounds.Bottom),
                    new PointF(bounds.Left, bounds.Bottom)
                }
            });
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

    private void AddLineMarqueeParts(int index, RectangleF bounds, List<MarqueePartAddition> additions, HashSet<int> remove)
    {
        var curve = LineCurve(index);
        var splits = LineRectSplitParameters(index, bounds);
        if (splits.Count <= 2) return;

        var segments = BuildCurveParts(curve.Start, curve.Control, curve.End, splits);
        if (segments.Count <= 1) return;

        var layer = ObjectLayer[index];
        var fillColor = Color.FromArgb(Argb[index]);
        var strokeColor = Color.FromArgb(StrokeArgb[index]);
        var gradientPaint = CaptureGradientPaint(index);
        var atomsPerPart = Math.Max(3u, AtomCount[index] / (uint)Math.Max(1, segments.Count));
        var selectedCount = 0;

        foreach (var segment in segments)
        {
            var midpoint = QuadraticPoint(segment.Start, segment.Control, segment.End, 0.5f);
            var selected = bounds.Contains(midpoint);
            if (selected) selectedCount++;
            additions.Add(new MarqueePartAddition(
                true,
                layer,
                Array.Empty<PointF>(),
                Array.Empty<PointF[]>(),
                segment.Start,
                segment.Control,
                segment.End,
                Stroke[index],
                fillColor,
                strokeColor,
                atomsPerPart,
                segment.PartIndex == 0 ? GetLineEndpointStyle(index, startEndpoint: true) : LineEndpointStyle.Round,
                segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(index, startEndpoint: false) : LineEndpointStyle.Round,
                selected)
            {
                GradientPaint = gradientPaint
            });
        }

        if (selectedCount == 0)
        {
            additions.RemoveRange(additions.Count - segments.Count, segments.Count);
            return;
        }

        remove.Add(index);
    }

    private bool TryBuildSameColorFillMerge(
        int a,
        int b,
        bool connectNearby,
        RectangleF mergeBounds,
        out PointF[][] mergedPath)
    {
        mergedPath = Array.Empty<PointF[]>();
        if ((uint)a >= ObjectCount || (uint)b >= ObjectCount) return false;
        if (ObjectLayer[a] != ObjectLayer[b] || Argb[a] != Argb[b]) return false;
        if (!IsFillShape(ShapeKind[a]) || !IsFillShape(ShapeKind[b])) return false;
        if (HasGradient(a) || HasGradient(b)) return false;

        var mergeDistance = connectNearby ? FillMergeDistanceUnits : 0.001f;
        if (!mergeBounds.IntersectsWith(GetObjectWorldBounds(b))) return false;

        var contoursA = FillWorldContours(a);
        var contoursB = FillWorldContours(b);
        if (contoursA.Length == 0 || contoursB.Length == 0) return false;
        var pathsA = ToClipperPaths(contoursA);
        var pathsB = ToClipperPaths(contoursB);
        if (pathsA.Count == 0 || pathsB.Count == 0) return false;
        if (ClipperPathsIntersect(pathsA, pathsB))
        {
            mergedPath = BuildMergedFillPath(pathsA, pathsB, Array.Empty<PointF>());
            return mergedPath.Length > 0;
        }

        var distance = CompoundPolygonDistance(contoursA, contoursB, out var nearestA, out var nearestB);
        if (distance > mergeDistance) return false;

        var connector = connectNearby && distance > 0.001f ? CreateFillConnector(nearestA, nearestB) : Array.Empty<PointF>();
        mergedPath = BuildMergedFillPath(pathsA, pathsB, connector);
        return mergedPath.Length > 0;
    }

    private static bool ClipperPathsIntersect(Paths64 subject, Paths64 clip)
    {
        try
        {
            var intersection = new Paths64();
            var clipper = new Clipper64 { PreserveCollinear = true };
            clipper.AddSubject(subject);
            clipper.AddClip(clip);
            return clipper.Execute(ClipType.Intersection, FillRule.EvenOdd, intersection)
                && intersection.Count > 0;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return false;
        }
    }

    private static PointF[] CreateFillConnector(PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f) return Array.Empty<PointF>();

        const float halfThickness = 1f;
        const float endpointOverlap = 1f;
        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy * halfThickness;
        var ny = ux * halfThickness;
        var start = new PointF(a.X - ux * endpointOverlap, a.Y - uy * endpointOverlap);
        var end = new PointF(b.X + ux * endpointOverlap, b.Y + uy * endpointOverlap);

        return
        [
            new PointF(start.X + nx, start.Y + ny),
            new PointF(end.X + nx, end.Y + ny),
            new PointF(end.X - nx, end.Y - ny),
            new PointF(start.X - nx, start.Y - ny)
        ];
    }

    private static PointF[][] BuildMergedFillPath(PointF[][] contoursA, PointF[][] contoursB, PointF[] connector)
    {
        return BuildMergedFillPath(ToClipperPaths(contoursA), ToClipperPaths(contoursB), connector);
    }

    private static PointF[][] BuildMergedFillPath(Paths64 subject, Paths64 clip, PointF[] connector)
    {
        if (subject.Count == 0 || clip.Count == 0) return Array.Empty<PointF[]>();
        try
        {
            var solution = new Paths64();
            var union = new Clipper64();
            union.AddSubject(subject);
            union.AddClip(clip);
            if (!union.Execute(ClipType.Union, FillRule.EvenOdd, solution) || solution.Count == 0)
            {
                return Array.Empty<PointF[]>();
            }

            if (connector.Length >= 3)
            {
                var connectorPaths = ToClipperPaths(new[] { connector });
                if (connectorPaths.Count == 0) return Array.Empty<PointF[]>();

                var connected = new Paths64();
                var connectorUnion = new Clipper64();
                connectorUnion.AddSubject(solution);
                connectorUnion.AddClip(connectorPaths);
                if (!connectorUnion.Execute(ClipType.Union, FillRule.EvenOdd, connected) || connected.Count == 0)
                {
                    return Array.Empty<PointF[]>();
                }

                solution = connected;
            }

            return FromClipperPaths(solution);
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return Array.Empty<PointF[]>();
        }
    }

    private static Paths64 ToClipperPaths(IReadOnlyList<PointF[]> contours)
    {
        var result = new Paths64(contours.Count);
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var path = new Path64(contour.Length);
            foreach (var point in contour)
            {
                path.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
            }

            path = Clipper.StripDuplicates(path, true);
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= ClipperCoordinateScale * ClipperCoordinateScale * 0.5d)
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static PointF[][] FromClipperPaths(Paths64 paths)
    {
        var contours = new List<PointF[]>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var contour = new PointF[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                contour[i] = new PointF(
                    (float)(path[i].X / ClipperCoordinateScale),
                    (float)(path[i].Y / ClipperCoordinateScale));
            }

            contours.Add(contour);
        }

        return NormalizePathContours(contours);
    }

    private static long ToClipperCoordinate(float value)
    {
        return checked((long)Math.Round(value * ClipperCoordinateScale, MidpointRounding.AwayFromZero));
    }

    private PointF[][] FillWorldContours(int objectIndex)
    {
        var shape = ShapeKind.Length > objectIndex ? ShapeKind[objectIndex] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(objectIndex, out var contours))
        {
            return contours;
        }

        var polygon = OpenPolygon(ShapeBoundary(objectIndex));
        return polygon.Length >= 3 ? new[] { polygon } : Array.Empty<PointF[]>();
    }

    private List<FillRegion> BuildFillRegions(int fillIndex, IReadOnlyList<int> candidates)
    {
        if ((uint)fillIndex >= ObjectCount || !HasFill(fillIndex)) return new List<FillRegion>();

        try
        {
            var source = ToClipperPaths(FillWorldContours(fillIndex));
            if (source.Count == 0) return new List<FillRegion>();

            var baseRegions = ExecuteFillRegions(source, new Paths64());
            var cutterPaths = MergeConnectedOpenCutterPaths(BuildTopologyCutterPaths(fillIndex, candidates));
            if (cutterPaths.Count == 0) return baseRegions;

            var cutRegions = ExecuteFillRegions(source, BuildTopologyCutterAreas(cutterPaths));
            return cutRegions.Count > baseRegions.Count ? cutRegions : baseRegions;
        }
        catch (Exception ex) when (ex is ClipperLibException or OverflowException)
        {
            return FallbackFillRegions(fillIndex);
        }
    }

    private List<TopologyCutterPath> BuildTopologyCutterPaths(int fillIndex, IReadOnlyList<int> candidates)
    {
        var layer = ObjectLayer[fillIndex];
        var result = new List<TopologyCutterPath>();
        foreach (var candidate in candidates)
        {
            if (candidate == fillIndex || ObjectLayer[candidate] != layer || !HasStroke(candidate)) continue;
            var shape = ShapeKind.Length > candidate ? ShapeKind[candidate] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (IsTopologyStrokeShape(shape))
            {
                var samples = StrokeSamples(candidate);
                var closed = samples.Length > 2 && SameDrawingUnit(samples[0].Point, samples[^1].Point);
                var path = ToClipperPath(samples.Select(sample => sample.Point), closed);
                if (path.Count >= 2) result.Add(new TopologyCutterPath(path, closed));
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(candidate))
                {
                    var path = ToClipperPath(contour, closed: true);
                    if (path.Count >= 3) result.Add(new TopologyCutterPath(path, Closed: true));
                }
            }
        }

        return result;
    }

    private static Paths64 BuildTopologyCutterAreas(IReadOnlyList<TopologyCutterPath> paths)
    {
        if (paths.Count == 0) return new Paths64();
        var offset = new ClipperOffset(2d, 0d, preserveCollinear: true, reverseSolution: false)
        {
            MergeGroups = true
        };
        foreach (var path in paths)
        {
            var offsetPath = path.Closed ? path.Path : ExtendOpenClipperPath(path.Path);
            offset.AddPath(offsetPath, JoinType.Miter, path.Closed ? EndType.Joined : EndType.Square);
        }

        var cutterAreas = new Paths64();
        offset.Execute(TopologyCutterHalfWidthClipper, cutterAreas);
        return cutterAreas.Count > 1 ? Clipper.Union(cutterAreas, FillRule.NonZero) : cutterAreas;
    }

    private static Path64 ExtendOpenClipperPath(Path64 source)
    {
        if (source.Count < 2) return source;
        var result = new Path64(source.Count);
        foreach (var point in source) result.Add(point);

        var extension = DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale;
        result[0] = ExtendClipperEndpoint(result[0], result[1], extension);
        result[^1] = ExtendClipperEndpoint(result[^1], result[^2], extension);
        return result;
    }

    private static Point64 ExtendClipperEndpoint(Point64 endpoint, Point64 neighbor, double extension)
    {
        var dx = (double)endpoint.X - neighbor.X;
        var dy = (double)endpoint.Y - neighbor.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001d) return endpoint;
        return new Point64(
            checked(endpoint.X + (long)Math.Round(dx / length * extension, MidpointRounding.AwayFromZero)),
            checked(endpoint.Y + (long)Math.Round(dy / length * extension, MidpointRounding.AwayFromZero)));
    }

    private static List<TopologyCutterPath> MergeConnectedOpenCutterPaths(List<TopologyCutterPath> source)
    {
        var result = source.ToList();
        var endpoints = new List<Point64>();
        foreach (var path in source)
        {
            if (path.Closed || path.Path.Count < 2) continue;
            AddClipperEndpoint(endpoints, path.Path[0]);
            AddClipperEndpoint(endpoints, path.Path[^1]);
        }

        var joinable = endpoints
            .Where(point => source.Count(path => ClipperPathTouchesPoint(path.Path, point, path.Closed)) == 2)
            .ToArray();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var a = 0; a < result.Count && !merged; a++)
            {
                if (result[a].Closed) continue;
                for (var b = a + 1; b < result.Count; b++)
                {
                    if (result[b].Closed || !TryJoinClipperPaths(result[a].Path, result[b].Path, joinable, out var joined)) continue;
                    var closed = joined.Count > 2 && joined[0] == joined[^1];
                    if (closed) joined = Clipper.StripDuplicates(joined, true);
                    result[a] = new TopologyCutterPath(joined, closed);
                    result.RemoveAt(b);
                    merged = true;
                    break;
                }
            }
        }

        return result;
    }

    private static bool TryJoinClipperPaths(Path64 a, Path64 b, IReadOnlyList<Point64> joinable, out Path64 joined)
    {
        joined = new Path64();
        if (a.Count < 2 || b.Count < 2) return false;

        if (ClipperPointsNear(a[^1], b[0]) && IsJoinableClipperEndpoint(a[^1], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: false, b, reverseB: false);
            return true;
        }

        if (ClipperPointsNear(a[^1], b[^1]) && IsJoinableClipperEndpoint(a[^1], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: false, b, reverseB: true);
            return true;
        }

        if (ClipperPointsNear(a[0], b[^1]) && IsJoinableClipperEndpoint(a[0], joinable))
        {
            joined = ConcatClipperPaths(b, reverseA: false, a, reverseB: false);
            return true;
        }

        if (ClipperPointsNear(a[0], b[0]) && IsJoinableClipperEndpoint(a[0], joinable))
        {
            joined = ConcatClipperPaths(a, reverseA: true, b, reverseB: false);
            return true;
        }

        return false;
    }

    private static void AddClipperEndpoint(List<Point64> endpoints, Point64 point)
    {
        foreach (var endpoint in endpoints)
        {
            if (ClipperPointsNear(endpoint, point)) return;
        }

        endpoints.Add(point);
    }

    private static bool IsJoinableClipperEndpoint(Point64 point, IReadOnlyList<Point64> joinable)
    {
        foreach (var candidate in joinable)
        {
            if (ClipperPointsNear(point, candidate)) return true;
        }

        return false;
    }

    private static bool ClipperPathTouchesPoint(Path64 path, Point64 point, bool closed)
    {
        if (path.Count == 0) return false;
        if (path.Count == 1) return ClipperPointsNear(path[0], point);
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (DistanceToClipperSegment(point, path[i], path[i + 1]) <= DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale)
            {
                return true;
            }
        }

        if (closed && DistanceToClipperSegment(point, path[^1], path[0]) <= DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale)
        {
            return true;
        }

        return false;
    }

    private static double DistanceToClipperSegment(Point64 point, Point64 start, Point64 end)
    {
        var vx = (double)end.X - start.X;
        var vy = (double)end.Y - start.Y;
        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= 0.001d)
        {
            var dx = (double)point.X - start.X;
            var dy = (double)point.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        var t = Math.Clamp(((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSquared, 0d, 1d);
        var projectedX = start.X + vx * t;
        var projectedY = start.Y + vy * t;
        var offsetX = point.X - projectedX;
        var offsetY = point.Y - projectedY;
        return Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
    }

    private static bool ClipperPointsNear(Point64 a, Point64 b)
    {
        var tolerance = DrawingTopologyRules.MinStrokeSegmentUnits * ClipperCoordinateScale;
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static Path64 ConcatClipperPaths(Path64 a, bool reverseA, Path64 b, bool reverseB)
    {
        var result = new Path64(a.Count + b.Count - 1);
        AddClipperPath(result, a, reverseA, skipFirst: false);
        AddClipperPath(result, b, reverseB, skipFirst: true);
        return result;
    }

    private static void AddClipperPath(Path64 destination, Path64 source, bool reverse, bool skipFirst)
    {
        for (var i = skipFirst ? 1 : 0; i < source.Count; i++)
        {
            destination.Add(reverse ? source[source.Count - 1 - i] : source[i]);
        }
    }

    private static Path64 ToClipperPath(IEnumerable<PointF> points, bool closed)
    {
        var result = new Path64();
        foreach (var point in points)
        {
            result.Add(new Point64(ToClipperCoordinate(point.X), ToClipperCoordinate(point.Y)));
        }

        return Clipper.StripDuplicates(result, closed);
    }

    private static List<FillRegion> ExecuteFillRegions(Paths64 source, Paths64 cutters)
    {
        var tree = new PolyTree64();
        var clipper = new Clipper64 { PreserveCollinear = true };
        clipper.AddSubject(source);
        if (cutters.Count > 0) clipper.AddClip(cutters);
        var clipType = cutters.Count > 0 ? ClipType.Difference : ClipType.Union;
        if (!clipper.Execute(clipType, FillRule.EvenOdd, tree)) return new List<FillRegion>();

        var regions = new List<FillRegion>();
        for (var i = 0; i < tree.Count; i++) CollectFillRegions(tree[i], regions);
        regions.Sort(CompareFillRegions);
        return regions;
    }

    private static void CollectFillRegions(PolyPath64 node, List<FillRegion> regions)
    {
        if (!node.IsHole)
        {
            var paths = new Paths64 { node.Polygon! };
            for (var i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole) paths.Add(child.Polygon!);
            }

            foreach (var region in NormalizeFillRegionContours(FromClipperPaths(paths))) regions.Add(region);
        }

        for (var i = 0; i < node.Count; i++) CollectFillRegions(node[i], regions);
    }

    private static List<FillRegion> NormalizeFillRegionContours(PointF[][] contours)
    {
        if (contours.Length == 0) return new List<FillRegion>();
        var paths = ToClipperPaths(contours);
        if (paths.Count == 0) return new List<FillRegion>();

        var tree = new PolyTree64();
        var clipper = new Clipper64 { PreserveCollinear = true };
        clipper.AddSubject(paths);
        if (!clipper.Execute(ClipType.Union, FillRule.EvenOdd, tree)) return new List<FillRegion>();

        var regions = new List<FillRegion>();
        for (var i = 0; i < tree.Count; i++) CollectNormalizedFillRegions(tree[i], regions);
        return regions;
    }

    private static void CollectNormalizedFillRegions(PolyPath64 node, List<FillRegion> regions)
    {
        if (!node.IsHole)
        {
            var paths = new Paths64 { node.Polygon! };
            for (var i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole) paths.Add(child.Polygon!);
            }

            var contours = FromClipperPaths(paths);
            if (contours.Length > 0)
            {
                regions.Add(new FillRegion(contours, ContourBounds(contours), CompoundContourArea(contours)));
            }
        }

        for (var i = 0; i < node.Count; i++) CollectNormalizedFillRegions(node[i], regions);
    }

    private List<FillRegion> FallbackFillRegions(int fillIndex)
    {
        var contours = FillWorldContours(fillIndex);
        return contours.Length > 0
            ? new List<FillRegion> { new(contours, ContourBounds(contours), CompoundContourArea(contours)) }
            : new List<FillRegion>();
    }

    private static int CompareFillRegions(FillRegion a, FillRegion b)
    {
        var comparison = a.Bounds.Top.CompareTo(b.Bounds.Top);
        if (comparison != 0) return comparison;
        comparison = a.Bounds.Left.CompareTo(b.Bounds.Left);
        if (comparison != 0) return comparison;
        comparison = b.Area.CompareTo(a.Area);
        if (comparison != 0) return comparison;
        comparison = a.Bounds.Bottom.CompareTo(b.Bounds.Bottom);
        return comparison != 0 ? comparison : a.Bounds.Right.CompareTo(b.Bounds.Right);
    }

    private static RectangleF ContourBounds(PointF[][] contours)
    {
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

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static float CompoundContourArea(PointF[][] contours)
    {
        var area = 0f;
        foreach (var contour in contours) area += Math.Abs(PolygonArea(contour));
        return area;
    }

    private static float CompoundPolygonDistance(PointF[][] a, PointF[][] b, out PointF nearestA, out PointF nearestB)
    {
        nearestA = a[0][0];
        nearestB = b[0][0];
        if (a.Any(contour => contour.Any(point => PointInCompoundPolygon(point, b)))
            || b.Any(contour => contour.Any(point => PointInCompoundPolygon(point, a))))
        {
            nearestA = nearestB = a[0][0];
            return 0;
        }

        var best = float.MaxValue;
        foreach (var contourA in a)
        {
            foreach (var contourB in b)
            {
                var distance = PolygonDistance(contourA, contourB, out var candidateA, out var candidateB);
                if (distance >= best) continue;
                best = distance;
                nearestA = candidateA;
                nearestB = candidateB;
            }
        }

        return best;
    }

    private static float PolygonDistance(PointF[] a, PointF[] b, out PointF nearestA, out PointF nearestB)
    {
        nearestA = a[0];
        nearestB = b[0];
        if (a.Any(point => PointInPolygon(point, b)) || b.Any(point => PointInPolygon(point, a)))
        {
            nearestA = nearestB = a[0];
            return 0;
        }

        var best = float.MaxValue;
        for (var i = 0; i < a.Length; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Length];
            for (var j = 0; j < b.Length; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Length];
                if (TrySegmentIntersection(a0, a1, b0, b1, out _))
                {
                    nearestA = nearestB = a0;
                    return 0;
                }

                TryUpdateNearest(a0, b0, b1, ref best, ref nearestA, ref nearestB);
                TryUpdateNearest(a1, b0, b1, ref best, ref nearestA, ref nearestB);
                TryUpdateNearest(b0, a0, a1, ref best, ref nearestB, ref nearestA);
                TryUpdateNearest(b1, a0, a1, ref best, ref nearestB, ref nearestA);
            }
        }

        return best;
    }

    private static void TryUpdateNearest(PointF point, PointF segmentStart, PointF segmentEnd, ref float best, ref PointF nearestPoint, ref PointF nearestOnSegment)
    {
        var t = SegmentProjectionT(point, segmentStart, segmentEnd);
        var projected = Lerp(segmentStart, segmentEnd, t);
        var distance = Distance(point, projected);
        if (distance >= best) return;
        best = distance;
        nearestPoint = point;
        nearestOnSegment = projected;
    }

    private static bool PointInPolygonOrOnBoundary(PointF point, PointF[] polygon)
    {
        if (PointInPolygon(point, polygon)) return true;
        for (var i = 0; i < polygon.Length; i++)
        {
            if (DistanceToSegment(point, polygon[i], polygon[(i + 1) % polygon.Length]) <= 0.75f) return true;
        }

        return false;
    }

    private List<int> CollectActiveCandidates(int frame)
    {
        var result = new List<int>(ObjectCount);
        for (var i = 0; i < ObjectCount; i++)
        {
            if (IsObjectActive(i, frame)) result.Add(i);
        }

        return result;
    }

    private List<int> CollectTopologyCandidates(int objectIndex, int frame)
    {
        if ((uint)objectIndex >= ObjectCount) return new List<int>();

        var layer = ObjectLayer[objectIndex];
        var bounds = GetObjectWorldBounds(objectIndex);
        bounds.Inflate(DrawingTopologyRules.MinStrokeSegmentUnits, DrawingTopologyRules.MinStrokeSegmentUnits);
        return QueryObjects(bounds, frame)
            .Where(index => ObjectLayer[index] == layer)
            .ToList();
    }

    private DrawingElementHit DetachStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !IsTopologyStrokeShape(ShapeKind[source]) || !HasStroke(source)) return hit;

        var splits = StrokeSplitParameters(source, candidates);
        if ((uint)hit.Key.PartIndex >= (uint)Math.Max(0, splits.Count - 1)) return DrawingElementHit.None;
        if (splits.Count <= 2) return hit;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;
        var selectedIndex = -1;

        if (ShapeKind[source] == VectorAnimationEngine.ShapeKind.Line)
        {
            var curve = LineCurve(source);
            var segments = BuildCurveParts(curve.Start, curve.Control, curve.End, splits);
            RemoveObjectAt(source);
            foreach (var segment in segments)
            {
                var index = AddCurveSegment(
                    layer,
                    segment.Start,
                    segment.Control,
                    segment.End,
                    stroke,
                    fillColor,
                    strokeColor,
                    Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)),
                    segment.PartIndex == 0 ? GetLineEndpointStyle(source, startEndpoint: true) : LineEndpointStyle.Round,
                    segment.PartIndex == segments[^1].PartIndex ? GetLineEndpointStyle(source, startEndpoint: false) : LineEndpointStyle.Round);
                if (index >= 0) ObjectOrder[index] = order;
                if (segment.PartIndex == selectedPart) selectedIndex = index;
            }
        }
        else
        {
            if (!TryGetFreehandWorldPoints(source, out var points)) return hit;
            var segments = BuildPolylinePathParts(points, splits);
            RemoveObjectAt(source);
            foreach (var segment in segments)
            {
                var index = AddFreehandStroke(layer, segment.Points, stroke, strokeColor, brushStroke: false, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
                if (index >= 0) ObjectOrder[index] = order;
                if (segment.PartIndex == selectedPart) selectedIndex = index;
            }
        }

        if (selectedIndex < 0 && ObjectCount > 0) selectedIndex = ObjectCount - 1;
        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private DrawingElementHit DetachBoundaryStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !HasStroke(source)) return hit;

        var segments = BuildBoundaryStrokeParts(source, candidates);
        if (segments.Count == 0) return hit;
        if (!segments.Any(segment => segment.PartIndex == hit.Key.PartIndex)) return DrawingElementHit.None;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;

        Stroke[source] = 0;
        var selectedIndex = -1;
        foreach (var segment in segments)
        {
            var index = AddPolylineStroke(layer, segment.Points, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
            if (index >= 0) ObjectOrder[index] = order;
            if (segment.PartIndex == selectedPart) selectedIndex = index;
        }

        if (!HasFill(source))
        {
            var last = ObjectCount - 1;
            if (selectedIndex == last) selectedIndex = source;
            RemoveObjectAt(source);
        }
        else
        {
            RebuildGeometryIndex();
            RebuildSummaries();
        }

        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : hit;
    }

    private DrawingElementHit DetachFillPart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || !HasFill(source)) return hit;

        var regions = BuildFillRegions(source, candidates);
        if ((uint)hit.Key.PartIndex >= regions.Count) return DrawingElementHit.None;
        var detachBoundary = HasStroke(source);
        if (regions.Count <= 1 && !detachBoundary) return hit;

        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];

        if (detachBoundary) MaterializeBoundaryStrokes(source, candidates);
        RemoveObjectAt(source);

        var selectedIndex = -1;
        var atomsPerPart = Math.Max(3u, atoms / (uint)Math.Max(1, regions.Count));
        for (var part = 0; part < regions.Count; part++)
        {
            var index = AddPathObjectContours(layer, regions[part].Contours, 0, fillColor, strokeColor, atomsPerPart);
            if (index >= 0) ObjectOrder[index] = order;
            if (part == hit.Key.PartIndex) selectedIndex = index;
        }

        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Fill, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private void MaterializeBoundaryStrokes(int source, IReadOnlyList<int> candidates)
    {
        var segments = BuildBoundaryStrokeParts(source, candidates);
        var layer = ObjectLayer[source];
        var order = ObjectOrder[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        foreach (var segment in segments)
        {
            var index = AddPolylineStroke(layer, segment.Points, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
            if (index >= 0) ObjectOrder[index] = order;
        }
    }

    private int AddPolylineStroke(int layer, PointF[] points, float stroke, Color fillColor, Color strokeColor, uint atoms)
    {
        if (points.Length < 2) return -1;
        if (points.Length == 2) return AddLineSegment(layer, points[0], points[1], stroke, fillColor, strokeColor, atoms);
        return AddFreehandStroke(layer, points, stroke, strokeColor, brushStroke: false, atoms);
    }

    private List<(int PartIndex, PointF Start, PointF End)> BuildPolylineParts(PointF[] polyline, IReadOnlyList<float> splits)
    {
        var result = new List<(int PartIndex, PointF Start, PointF End)>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var start = PolylinePointAt(polyline, splits[i]);
            var end = PolylinePointAt(polyline, splits[i + 1]);
            if (Distance(start, end) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add((i, start, end));
        }

        return result;
    }

    private List<PolylinePart> BuildPolylinePathParts(PointF[] polyline, IReadOnlyList<float> splits)
    {
        var result = new List<PolylinePart>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var startT = splits[i];
            var endT = splits[i + 1];
            if (endT - startT <= 0.0001f) continue;

            var points = PolylineSlice(polyline, startT, endT);
            if (PolylineLength(points) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add(new PolylinePart(i, startT, endT, points));
        }

        return result;
    }

    private List<CurveSegmentPart> BuildCurveParts(PointF start, PointF control, PointF end, IReadOnlyList<float> splits)
    {
        var result = new List<CurveSegmentPart>();
        for (var i = 0; i < splits.Count - 1; i++)
        {
            var startT = splits[i];
            var endT = splits[i + 1];
            if (endT - startT <= 0.0001f) continue;
            var segment = QuadraticSubcurve(start, control, end, startT, endT);
            if (Distance(segment.Start, segment.End) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            result.Add(new CurveSegmentPart(i, segment.Start, segment.Control, segment.End));
        }

        return result;
    }

    private List<int> CollectHitCandidates(int minX, int maxX, int minY, int maxY, int frame)
    {
        var result = new List<int>(128);
        PopulateQueryActiveKeyframes(frame);
        // RebuildSpatialIndex assigns every object to exactly one center cell.
        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = CellIndex(cx, cy);
                var start = CellStart[cell];
                var end = CellStart[cell + 1];
                for (var p = start; p < end; p++)
                {
                    var i = CellObjects[p];
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    result.Add(i);
                }

                foreach (var i in GetPendingSpatialCellObjects(cell))
                {
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    result.Add(i);
                }
            }
        }

        result.Sort();
        return result;
    }

    public int[] QueryObjects(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        if (ObjectCount <= 0 || limit <= 0) return Array.Empty<int>();
        var bounds = Normalize(worldBounds);
        if (bounds.Width <= 0.001f || bounds.Height <= 0.001f) return Array.Empty<int>();

        GetIndexRange(bounds, out var minX, out var maxX, out var minY, out var maxY);
        var result = new List<int>(Math.Min(ObjectCount, 1024));
        PopulateQueryActiveKeyframes(frame);

        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var cell = CellIndex(cx, cy);
                var start = CellStart[cell];
                var end = CellStart[cell + 1];
                for (var p = start; p < end; p++)
                {
                    var i = CellObjects[p];
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    if (!ObjectIntersectsBounds(i, bounds)) continue;

                    result.Add(i);
                    if (result.Count >= limit)
                    {
                        result.Sort();
                        return result.ToArray();
                    }
                }

                foreach (var i in GetPendingSpatialCellObjects(cell))
                {
                    if (ObjectKeyframeFrame[i] != _queryActiveKeyframes[ObjectLayer[i]]) continue;
                    if (!ObjectIntersectsBounds(i, bounds)) continue;

                    result.Add(i);
                    if (result.Count >= limit)
                    {
                        result.Sort();
                        return result.ToArray();
                    }
                }
            }
        }

        result.Sort();
        return result.ToArray();
    }

    private void PopulateQueryActiveKeyframes(int frame)
    {
        if (_queryActiveFrame == frame && _queryActiveKeyframes.Length == LayerCount) return;
        if (_queryActiveKeyframes.Length != LayerCount) Array.Resize(ref _queryActiveKeyframes, LayerCount);
        PopulateActiveKeyframeFrames(frame, _queryActiveKeyframes);
        _queryActiveFrame = frame;
    }

    private void InvalidateQueryActiveKeyframes()
    {
        _queryActiveFrame = int.MinValue;
    }

    public int[] QueryDrawingObjects(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        var bounds = Normalize(worldBounds);
        return QueryObjects(bounds, frame, limit)
            .Where(index => ObjectGeometryIntersectsBounds(index, bounds))
            .ToArray();
    }

    public DrawingElementHit[] QueryDrawingElementsInsideBounds(RectangleF worldBounds, int frame, int limit = 100_000)
    {
        var bounds = Normalize(worldBounds);
        var result = new List<DrawingElementHit>();
        foreach (var objectIndex in QueryDrawingObjects(bounds, frame, limit))
        {
            var shape = ShapeKind[objectIndex];
            var candidates = CollectTopologyCandidates(objectIndex, frame);
            if (IsTopologyStrokeShape(shape))
            {
                if (!HasStroke(objectIndex)) continue;
                var splits = StrokeSplitParameters(objectIndex, candidates);
                if (shape == VectorAnimationEngine.ShapeKind.Line)
                {
                    var curve = LineCurve(objectIndex);
                    foreach (var part in BuildCurveParts(curve.Start, curve.Control, curve.End, splits))
                    {
                        if (!QuadraticCurveInsideRectangle(part.Start, part.Control, part.End, bounds)) continue;

                        result.Add(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            splits[part.PartIndex],
                            splits[part.PartIndex + 1]));
                    }
                }
                else if (TryGetFreehandWorldPoints(objectIndex, out var points))
                {
                    foreach (var part in BuildPolylinePathParts(points, splits))
                    {
                        if (!part.Points.All(point => PointInRectangle(point, bounds))) continue;
                        result.Add(new DrawingElementHit(
                            new DrawingElementKey(objectIndex, DrawingElementKind.Stroke, part.PartIndex),
                            0,
                            part.StartT,
                            part.EndT));
                    }
                }

                continue;
            }

            if (HasFill(objectIndex))
            {
                var regions = BuildFillRegions(objectIndex, candidates);
                for (var part = 0; part < regions.Count; part++)
                {
                    if (!regions[part].Contours.All(contour => contour.All(point => PointInRectangle(point, bounds)))) continue;
                    result.Add(new DrawingElementHit(
                        new DrawingElementKey(objectIndex, DrawingElementKind.Fill, part),
                        0,
                        0,
                        1));
                }
            }

            if (!HasStroke(objectIndex)) continue;
            foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
            {
                if (!part.Points.All(point => PointInRectangle(point, bounds))) continue;
                result.Add(new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                    0,
                    part.StartT,
                    part.EndT));
            }
        }

        return result.ToArray();
    }

    public bool IsObjectGeometryInsideBounds(int objectIndex, RectangleF worldBounds)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        var bounds = Normalize(worldBounds);
        var shape = ShapeKind[objectIndex];
        if (IsTopologyStrokeShape(shape))
        {
            var samples = StrokeSamples(objectIndex);
            return samples.Length > 0 && samples.All(sample => PointInRectangle(sample.Point, bounds));
        }

        var contours = ShapeBoundaryContours(objectIndex);
        return contours.Length > 0 && contours.All(contour => contour.All(point => PointInRectangle(point, bounds)));
    }

    private bool ObjectGeometryIntersectsBounds(int objectIndex, RectangleF bounds)
    {
        var shape = ShapeKind[objectIndex];
        if (IsTopologyStrokeShape(shape))
        {
            if (!HasStroke(objectIndex)) return false;
            var expanded = bounds;
            var radius = Math.Max(Stroke[objectIndex] * 0.5f, 1);
            expanded.Inflate(radius, radius);
            return PolylineIntersectsRectangle(StrokeSamples(objectIndex).Select(sample => sample.Point).ToArray(), expanded);
        }

        if (!HasFill(objectIndex) && !HasStroke(objectIndex)) return false;
        var contours = ShapeBoundaryContours(objectIndex);
        var contourBounds = bounds;
        if (HasStroke(objectIndex))
        {
            var radius = Math.Max(Stroke[objectIndex] * 0.5f, 1);
            contourBounds.Inflate(radius, radius);
        }

        foreach (var contour in contours)
        {
            if (PolylineIntersectsRectangle(contour, contourBounds)) return true;
        }

        if (!HasFill(objectIndex)) return false;
        var corners = new[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        };
        return corners.Any(corner => FillContainsPoint(objectIndex, corner));
    }

    private static bool PolylineIntersectsRectangle(IReadOnlyList<PointF> points, RectangleF bounds)
    {
        if (points.Count == 0) return false;
        if (points.Any(point => PointInRectangle(point, bounds))) return true;
        for (var i = 0; i < points.Count - 1; i++)
        {
            if (SegmentIntersectsRectangle(points[i], points[i + 1], bounds)) return true;
        }

        return false;
    }

    private static bool SegmentIntersectsRectangle(PointF start, PointF end, RectangleF bounds)
    {
        var topLeft = new PointF(bounds.Left, bounds.Top);
        var topRight = new PointF(bounds.Right, bounds.Top);
        var bottomRight = new PointF(bounds.Right, bounds.Bottom);
        var bottomLeft = new PointF(bounds.Left, bounds.Bottom);
        return TrySegmentIntersection(start, end, topLeft, topRight, out _)
            || TrySegmentIntersection(start, end, topRight, bottomRight, out _)
            || TrySegmentIntersection(start, end, bottomRight, bottomLeft, out _)
            || TrySegmentIntersection(start, end, bottomLeft, topLeft, out _);
    }

    private static bool PointInRectangle(PointF point, RectangleF bounds)
    {
        return point.X >= bounds.Left - 0.001f
            && point.X <= bounds.Right + 0.001f
            && point.Y >= bounds.Top - 0.001f
            && point.Y <= bounds.Bottom + 0.001f;
    }

    public void GetIndexRange(RectangleF worldBounds, out int minX, out int maxX, out int minY, out int maxY)
    {
        var expanded = MaxHalfExtent + 4;
        var cellW = StageWidth / IndexColumns;
        var cellH = StageHeight / IndexRows;
        minX = (int)Math.Clamp((worldBounds.Left - expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        maxX = (int)Math.Clamp((worldBounds.Right + expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        minY = (int)Math.Clamp((worldBounds.Top - expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
        maxY = (int)Math.Clamp((worldBounds.Bottom + expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
    }

    public int CellIndex(int x, int y) => y * IndexColumns + x;

    internal int GetSpatialCellObjectCount(int cell)
    {
        if ((uint)cell >= IndexColumns * IndexRows) return 0;
        var count = CellStart[cell + 1] - CellStart[cell];
        return _spatialAppendCells is not null && _spatialAppendCells.TryGetValue(cell, out var appended)
            ? count + appended.Count
            : count;
    }

    internal ReadOnlySpan<int> GetPendingSpatialCellObjects(int cell)
    {
        return _spatialAppendCells is not null && _spatialAppendCells.TryGetValue(cell, out var appended)
            ? CollectionsMarshal.AsSpan(appended)
            : ReadOnlySpan<int>.Empty;
    }

    private void AppendObjectToSpatialIndex(int objectIndex)
    {
        var cellCount = IndexColumns * IndexRows;
        if (objectIndex != ObjectCount - 1
            || CellStart.Length != cellCount + 1
            || objectIndex != _spatialBaseObjectCount + _spatialPendingObjectCount)
        {
            RebuildGeometryIndex();
            return;
        }

        MaxHalfExtent = Math.Max(MaxHalfExtent, ObjectHalfExtent(objectIndex));
        var cell = CellForWorld(X[objectIndex], Y[objectIndex]);
        if (cell >= 0)
        {
            _spatialAppendCells ??= new Dictionary<int, List<int>>();
            if (!_spatialAppendCells.TryGetValue(cell, out var appended))
            {
                appended = new List<int>(4);
                _spatialAppendCells.Add(cell, appended);
            }

            appended.Add(objectIndex);
        }

        _spatialPendingObjectCount++;
        GeometryRevision++;
        if (_spatialPendingObjectCount >= SpatialIndexAppendRebuildThreshold) RebuildSpatialIndex();
    }

    private void RebuildSpatialIndex()
    {
        var cellCount = IndexColumns * IndexRows;
        var workers = ParallelBatch.WorkerCount(ObjectCount, 8192);
        if (workers == 1)
        {
            var starts = new int[cellCount + 1];
            var objects = GC.AllocateUninitializedArray<int>(ObjectCount);
            for (var i = 0; i < ObjectCount; i++)
            {
                var cell = CellForWorld(X[i], Y[i]);
                if (cell >= 0) starts[cell + 1]++;
            }

            for (var i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
            var cursor = new int[cellCount];
            Array.Copy(starts, cursor, cellCount);
            for (var i = 0; i < ObjectCount; i++)
            {
                var cell = CellForWorld(X[i], Y[i]);
                if (cell >= 0) objects[cursor[cell]++] = i;
            }

            CellStart = starts;
            CellObjects = objects;
            ResetSpatialAppendBuffer();
            GeometryRevision++;
            return;
        }

        var localCounts = new int[workers][];
        try
        {
            for (var worker = 0; worker < workers; worker++)
            {
                localCounts[worker] = ArrayPool<int>.Shared.Rent(cellCount);
                Array.Clear(localCounts[worker], 0, cellCount);
            }

            ParallelBatch.For(ObjectCount, 8192, (worker, start, end) =>
            {
                var counts = localCounts[worker];
                for (var i = start; i < end; i++)
                {
                    var cell = CellForWorld(X[i], Y[i]);
                    if (cell >= 0) counts[cell]++;
                }
            }, workers);

            var starts = new int[cellCount + 1];
            for (var cell = 0; cell < cellCount; cell++)
            {
                var count = 0;
                for (var worker = 0; worker < workers; worker++) count += localCounts[worker][cell];
                starts[cell + 1] = count;
            }

            for (var cell = 1; cell < starts.Length; cell++) starts[cell] += starts[cell - 1];
            for (var cell = 0; cell < cellCount; cell++)
            {
                var cursor = starts[cell];
                for (var worker = 0; worker < workers; worker++)
                {
                    var count = localCounts[worker][cell];
                    localCounts[worker][cell] = cursor;
                    cursor += count;
                }
            }

            var objects = GC.AllocateUninitializedArray<int>(ObjectCount);
            ParallelBatch.For(ObjectCount, 8192, (worker, start, end) =>
            {
                var cursors = localCounts[worker];
                for (var i = start; i < end; i++)
                {
                    var cell = CellForWorld(X[i], Y[i]);
                    if (cell >= 0) objects[cursors[cell]++] = i;
                }
            }, workers);

            CellStart = starts;
            CellObjects = objects;
            ResetSpatialAppendBuffer();
            GeometryRevision++;
        }
        finally
        {
            foreach (var counts in localCounts)
            {
                if (counts is not null) ArrayPool<int>.Shared.Return(counts);
            }
        }
    }

    private void ResetSpatialAppendBuffer()
    {
        _spatialAppendCells = null;
        _spatialBaseObjectCount = ObjectCount;
        _spatialPendingObjectCount = 0;
    }

    private void ClearSummaries()
    {
        Array.Clear(TileCount);
        Array.Clear(TileAtoms);
        Array.Clear(TileArgb);
        Array.Clear(OverviewCount);
        Array.Clear(OverviewAtoms);
        Array.Clear(OverviewArgb);
        SummaryRevision++;
    }

    private void RebuildSummaries(bool useFillColorForStrokeShapes = false)
    {
        var workers = ParallelBatch.WorkerCount(ObjectCount, 20_000, maxWorkers: 6);
        if (workers > 1)
        {
            RebuildSummariesParallel(workers, useFillColorForStrokeShapes);
            return;
        }

        ClearSummaries();
        var tileR = new long[TileCount.Length];
        var tileG = new long[TileCount.Length];
        var tileB = new long[TileCount.Length];
        var overviewR = new long[OverviewCount.Length];
        var overviewG = new long[OverviewCount.Length];
        var overviewB = new long[OverviewCount.Length];

        for (var i = 0; i < ObjectCount; i++)
        {
            var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
            var color = Color.FromArgb(useFillColorForStrokeShapes
                ? Argb[i]
                : shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform ? StrokeArgb[i] : Argb[i]);
            if (IsFreehandShape(shape))
            {
                AddObjectBoundsToSummary(i, color, TileColumns, TileRows, TileCount, TileAtoms, tileR, tileG, tileB);
                AddObjectBoundsToSummary(i, color, OverviewColumns, OverviewRows, OverviewCount, OverviewAtoms, overviewR, overviewG, overviewB);
            }
            else
            {
                AddObjectToTileSummary(i, color, tileR, tileG, tileB);
                AddObjectToOverviewSummary(i, color, overviewR, overviewG, overviewB);
            }
        }

        FinalizeTileSummary(tileR, tileG, tileB);
        FinalizeOverviewSummary(overviewR, overviewG, overviewB);
    }

    private void RebuildSummariesParallel(int workers, bool useFillColorForStrokeShapes)
    {
        var tileLength = TileCount.Length;
        var overviewLength = OverviewCount.Length;
        var tileCounts = new int[workers][];
        var tileAtoms = new long[workers][];
        var tileR = new long[workers][];
        var tileG = new long[workers][];
        var tileB = new long[workers][];
        var overviewCounts = new int[workers][];
        var overviewAtoms = new long[workers][];
        var overviewR = new long[workers][];
        var overviewG = new long[workers][];
        var overviewB = new long[workers][];

        try
        {
            for (var worker = 0; worker < workers; worker++)
            {
                tileCounts[worker] = RentCleared<int>(tileLength);
                tileAtoms[worker] = RentCleared<long>(tileLength);
                tileR[worker] = RentCleared<long>(tileLength);
                tileG[worker] = RentCleared<long>(tileLength);
                tileB[worker] = RentCleared<long>(tileLength);
                overviewCounts[worker] = RentCleared<int>(overviewLength);
                overviewAtoms[worker] = RentCleared<long>(overviewLength);
                overviewR[worker] = RentCleared<long>(overviewLength);
                overviewG[worker] = RentCleared<long>(overviewLength);
                overviewB[worker] = RentCleared<long>(overviewLength);
            }

            ParallelBatch.For(ObjectCount, 20_000, (worker, start, end) =>
            {
                for (var i = start; i < end; i++)
                {
                    var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
                    var color = Color.FromArgb(useFillColorForStrokeShapes
                        ? Argb[i]
                        : shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform ? StrokeArgb[i] : Argb[i]);
                    if (IsFreehandShape(shape))
                    {
                        AddObjectBoundsToSummary(i, color, TileColumns, TileRows, tileCounts[worker], tileAtoms[worker], tileR[worker], tileG[worker], tileB[worker]);
                        AddObjectBoundsToSummary(i, color, OverviewColumns, OverviewRows, overviewCounts[worker], overviewAtoms[worker], overviewR[worker], overviewG[worker], overviewB[worker]);
                    }
                    else
                    {
                        AddObjectCenterToSummary(i, color, TileColumns, TileRows, tileCounts[worker], tileAtoms[worker], tileR[worker], tileG[worker], tileB[worker]);
                        AddObjectCenterToSummary(i, color, OverviewColumns, OverviewRows, overviewCounts[worker], overviewAtoms[worker], overviewR[worker], overviewG[worker], overviewB[worker]);
                    }
                }
            }, workers);

            MergeSummaryBatches(tileLength, workers, tileCounts, tileAtoms, tileR, tileG, tileB, TileCount, TileAtoms, TileArgb, overview: false);
            MergeSummaryBatches(overviewLength, workers, overviewCounts, overviewAtoms, overviewR, overviewG, overviewB, OverviewCount, OverviewAtoms, OverviewArgb, overview: true);
            SummaryRevision++;
        }
        finally
        {
            ReturnBatches(tileCounts);
            ReturnBatches(tileAtoms);
            ReturnBatches(tileR);
            ReturnBatches(tileG);
            ReturnBatches(tileB);
            ReturnBatches(overviewCounts);
            ReturnBatches(overviewAtoms);
            ReturnBatches(overviewR);
            ReturnBatches(overviewG);
            ReturnBatches(overviewB);
        }
    }

    private static void MergeSummaryBatches(
        int length,
        int workers,
        int[][] batchCounts,
        long[][] batchAtoms,
        long[][] batchR,
        long[][] batchG,
        long[][] batchB,
        int[] counts,
        long[] atoms,
        int[] argb,
        bool overview)
    {
        Parallel.For(0, length, new ParallelOptions { MaxDegreeOfParallelism = workers }, cell =>
        {
            var count = 0;
            long atomCount = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (var worker = 0; worker < workers; worker++)
            {
                count += batchCounts[worker][cell];
                atomCount += batchAtoms[worker][cell];
                red += batchR[worker][cell];
                green += batchG[worker][cell];
                blue += batchB[worker][cell];
            }

            counts[cell] = count;
            atoms[cell] = atomCount;
            if (count == 0)
            {
                argb[cell] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                return;
            }

            var alpha = overview
                ? Math.Clamp(58 + count * 6, 64, 230)
                : Math.Clamp(44 + count * 9, 48, 230);
            argb[cell] = Color.FromArgb(alpha, (int)(red / count), (int)(green / count), (int)(blue / count)).ToArgb();
        });
    }

    private static T[] RentCleared<T>(int length)
    {
        var buffer = ArrayPool<T>.Shared.Rent(length);
        Array.Clear(buffer, 0, length);
        return buffer;
    }

    private static void ReturnBatches<T>(IEnumerable<T[]?> batches)
    {
        foreach (var batch in batches)
        {
            if (batch is not null) ArrayPool<T>.Shared.Return(batch);
        }
    }

    private void AddObjectToSummariesIncremental(int objectIndex, Color color)
    {
        AddObjectToSummaryRange(
            objectIndex,
            color,
            TileColumns,
            TileRows,
            TileCount,
            TileAtoms,
            TileArgb);
        AddObjectToSummaryRange(
            objectIndex,
            color,
            OverviewColumns,
            OverviewRows,
            OverviewCount,
            OverviewAtoms,
            OverviewArgb);
        SummaryRevision++;
    }

    private void AddObjectToSummaryRange(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        int[] colors)
    {
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var cellWidth = StageWidth / columns;
        var cellHeight = StageHeight / rows;
        var minX = (int)Math.Clamp((X[objectIndex] - halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var maxX = (int)Math.Clamp((X[objectIndex] + halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var minY = (int)Math.Clamp((Y[objectIndex] - halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
        var maxY = (int)Math.Clamp((Y[objectIndex] + halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = y * columns + x;
                var oldCount = counts[cell];
                var oldColor = oldCount > 0 ? Color.FromArgb(colors[cell]) : Color.Empty;
                var nextCount = oldCount + 1;
                counts[cell] = nextCount;
                atoms[cell] += AtomCount[objectIndex];
                var red = (oldColor.R * oldCount + color.R) / nextCount;
                var green = (oldColor.G * oldCount + color.G) / nextCount;
                var blue = (oldColor.B * oldCount + color.B) / nextCount;
                var alpha = Math.Clamp(44 + nextCount * 9, 48, 230);
                colors[cell] = Color.FromArgb(alpha, red, green, blue).ToArgb();
            }
        }
    }

    private void AddObjectBoundsToSummary(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        long[] red,
        long[] green,
        long[] blue)
    {
        var halfWidth = Width[objectIndex] * 0.5f;
        var halfHeight = Height[objectIndex] * 0.5f;
        var cellWidth = StageWidth / columns;
        var cellHeight = StageHeight / rows;
        var minX = (int)Math.Clamp((X[objectIndex] - halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var maxX = (int)Math.Clamp((X[objectIndex] + halfWidth + StageWidth * 0.5f) / cellWidth, 0, columns - 1);
        var minY = (int)Math.Clamp((Y[objectIndex] - halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);
        var maxY = (int)Math.Clamp((Y[objectIndex] + halfHeight + StageHeight * 0.5f) / cellHeight, 0, rows - 1);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = y * columns + x;
                counts[cell]++;
                atoms[cell] += AtomCount[objectIndex];
                red[cell] += color.R;
                green[cell] += color.G;
                blue[cell] += color.B;
            }
        }
    }

    private void AddObjectCenterToSummary(
        int objectIndex,
        Color color,
        int columns,
        int rows,
        int[] counts,
        long[] atoms,
        long[] red,
        long[] green,
        long[] blue)
    {
        var x = (int)Math.Clamp((X[objectIndex] + StageWidth * 0.5f) / StageWidth * columns, 0, columns - 1);
        var y = (int)Math.Clamp((Y[objectIndex] + StageHeight * 0.5f) / StageHeight * rows, 0, rows - 1);
        var cell = y * columns + x;
        counts[cell]++;
        atoms[cell] += AtomCount[objectIndex];
        red[cell] += color.R;
        green[cell] += color.G;
        blue[cell] += color.B;
    }

    private int CellForWorld(float x, float y)
    {
        var ix = (int)((x + StageWidth * 0.5f) / StageWidth * IndexColumns);
        var iy = (int)((y + StageHeight * 0.5f) / StageHeight * IndexRows);
        if ((uint)ix >= IndexColumns || (uint)iy >= IndexRows) return -1;
        return iy * IndexColumns + ix;
    }

    private void GetHitTestRange(PointF world, float toleranceWorld, out int minX, out int maxX, out int minY, out int maxY)
    {
        var expanded = MaxHalfExtent + Math.Max(4, toleranceWorld);
        var cellW = StageWidth / IndexColumns;
        var cellH = StageHeight / IndexRows;
        minX = (int)Math.Clamp((world.X - expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        maxX = (int)Math.Clamp((world.X + expanded + StageWidth * 0.5f) / cellW, 0, IndexColumns - 1);
        minY = (int)Math.Clamp((world.Y - expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
        maxY = (int)Math.Clamp((world.Y + expanded + StageHeight * 0.5f) / cellH, 0, IndexRows - 1);
    }

    private bool HitObject(PointF world, int i, float toleranceWorld)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfW = Width[i] * 0.5f;
            var start = LocalToWorld(i, -halfW, 0);
            var end = LocalToWorld(i, halfW, 0);
            var control = new PointF(CurveControlX[i], CurveControlY[i]);
            var hitRadius = Math.Max(Height[i] * 0.5f, 1) + toleranceWorld;
            return DistanceToQuadratic(world, start, control, end) <= hitRadius;
        }

        if (IsFreehandShape(shape))
        {
            if (!TryGetFreehandWorldPoints(i, out var points) || points.Length == 0) return false;
            var hitRadius = Math.Max(Stroke[i] * 0.5f, 1) + toleranceWorld;
            if (points.Length == 1) return Distance(world, points[0]) <= hitRadius;
            for (var p = 0; p < points.Length - 1; p++)
            {
                if (DistanceToSegment(world, points[p], points[p + 1]) <= hitRadius) return true;
            }

            return false;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path)
        {
            return TryGetPathWorldContours(i, out var contours) && PointInCompoundPolygon(world, contours);
        }

        var local = WorldToLocal(i, world);
        var halfWidth = Width[i] * 0.5f + toleranceWorld;
        var halfHeight = Height[i] * 0.5f + toleranceWorld;
        if (Math.Abs(local.X) > halfWidth || Math.Abs(local.Y) > halfHeight) return false;

        if (shape == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            var rx = Math.Max(1, Width[i] * 0.5f + toleranceWorld);
            var ry = Math.Max(1, Height[i] * 0.5f + toleranceWorld);
            var normalized = local.X * local.X / (rx * rx) + local.Y * local.Y / (ry * ry);
            return normalized <= 1;
        }

        return true;
    }

    private DrawingElementHit HitElement(PointF world, int i, IReadOnlyList<int> hitCandidates, int frame, float toleranceWorld)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (IsTopologyStrokeShape(shape))
        {
            if (!HasStroke(i)) return DrawingElementHit.None;
            return HitStrokeElement(world, i, CollectTopologyCandidates(i, frame), toleranceWorld);
        }

        List<int>? topologyCandidates = null;
        if (HasStroke(i))
        {
            topologyCandidates = CollectTopologyCandidates(i, frame);
            var boundaryHit = HitBoundaryStrokeElement(world, i, topologyCandidates, toleranceWorld);
            if (boundaryHit.IsValid) return boundaryHit;
        }

        if (!HasFill(i) || !FillContainsPoint(i, world)) return DrawingElementHit.None;
        if (!OwnsFillUnit(world, i, hitCandidates)) return DrawingElementHit.None;
        topologyCandidates ??= CollectTopologyCandidates(i, frame);
        var part = FillPartIndex(world, i, topologyCandidates);
        if (part < 0) return DrawingElementHit.None;
        return new DrawingElementHit(new DrawingElementKey(i, DrawingElementKind.Fill, part), 0, 0, 1);
    }

    private bool OwnsFillUnit(PointF world, int fillIndex, IReadOnlyList<int> candidates)
    {
        var layer = ObjectLayer[fillIndex];
        var unit = DrawingUnitCell.FromPoint(layer, world);
        var sample = new PointF(unit.X + 0.5f, unit.Y + 0.5f);
        var owner = -1;

        foreach (var candidate in candidates)
        {
            if (ObjectLayer[candidate] != layer) continue;
            var shape = ShapeKind.Length > candidate ? ShapeKind[candidate] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (!IsFillShape(shape) || !HasFill(candidate)) continue;
            if (!FillContainsPoint(candidate, sample)) continue;
            if (owner < 0 || CompareObjectStack(candidate, owner) > 0)
            {
                owner = candidate;
            }
        }

        return owner == fillIndex;
    }

    private DrawingElementHit HitStrokeElement(PointF world, int strokeIndex, IReadOnlyList<int> candidates, float toleranceWorld)
    {
        var samples = StrokeSamples(strokeIndex);
        if (samples.Length == 0) return DrawingElementHit.None;
        var hitRadius = Math.Max(Stroke[strokeIndex] * 0.5f, 1) + toleranceWorld;
        if (samples.Length == 1)
        {
            var distance = Distance(world, samples[0].Point);
            return distance <= hitRadius
                ? new DrawingElementHit(new DrawingElementKey(strokeIndex, DrawingElementKind.Stroke, 0), distance, 0, 1)
                : DrawingElementHit.None;
        }

        return HitCurvePart(world, strokeIndex, DrawingElementKind.Stroke, samples, StrokeSplitParameters(strokeIndex, candidates), hitRadius);
    }

    private DrawingElementHit HitBoundaryStrokeElement(PointF world, int objectIndex, IReadOnlyList<int> candidates, float toleranceWorld)
    {
        if (!HasStroke(objectIndex)) return DrawingElementHit.None;
        var hitRadius = Math.Max(Stroke[objectIndex] * 0.5f, 1) + toleranceWorld;
        var best = DrawingElementHit.None;
        foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
        {
            var distance = DistanceToPolyline(world, part.Points);
            if (distance > hitRadius || (best.IsValid && distance >= best.Distance)) continue;
            best = new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                distance,
                part.StartT,
                part.EndT);
        }

        return best;
    }

    private DrawingElementHit HitPolylinePart(PointF world, int objectIndex, DrawingElementKind kind, PointF[] polyline, List<float> splitPoints, float hitRadius)
    {
        var bestDistance = float.MaxValue;
        var bestT = 0f;
        var segments = Math.Max(1, polyline.Length - 1);

        for (var i = 0; i < polyline.Length - 1; i++)
        {
            var distance = DistanceToSegment(world, polyline[i], polyline[i + 1]);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestT = (i + SegmentProjectionT(world, polyline[i], polyline[i + 1])) / segments;
        }

        if (bestDistance > hitRadius) return DrawingElementHit.None;

        var part = 0;
        for (var i = 0; i < splitPoints.Count - 1; i++)
        {
            if (bestT < splitPoints[i] - 0.0001f || bestT > splitPoints[i + 1] + 0.0001f) continue;
            part = i;
            return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, splitPoints[i], splitPoints[i + 1]);
        }

        return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, 0, 1);
    }

    private DrawingElementHit HitCurvePart(PointF world, int objectIndex, DrawingElementKind kind, CurveSample[] samples, List<float> splitPoints, float hitRadius)
    {
        var bestDistance = float.MaxValue;
        var bestT = 0f;

        for (var i = 0; i < samples.Length - 1; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            var distance = DistanceToSegment(world, a.Point, b.Point);
            if (distance >= bestDistance) continue;
            var localT = SegmentProjectionT(world, a.Point, b.Point);
            bestDistance = distance;
            bestT = a.T + (b.T - a.T) * localT;
        }

        if (bestDistance > hitRadius) return DrawingElementHit.None;

        var part = 0;
        for (var i = 0; i < splitPoints.Count - 1; i++)
        {
            if (bestT < splitPoints[i] - 0.0001f || bestT > splitPoints[i + 1] + 0.0001f) continue;
            part = i;
            return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, splitPoints[i], splitPoints[i + 1]);
        }

        return new DrawingElementHit(new DrawingElementKey(objectIndex, kind, part), bestDistance, 0, 1);
    }

    private List<float> StrokeSplitParameters(int strokeIndex, IReadOnlyList<int> candidates)
    {
        var splits = new List<DrawingTopologySplit>();
        var source = StrokeSamples(strokeIndex);
        if (source.Length == 0) return new List<float>();
        splits.Add(new DrawingTopologySplit(0, source[0].Point));
        splits.Add(new DrawingTopologySplit(1, source[^1].Point));
        var sourceLayer = ObjectLayer[strokeIndex];
        foreach (var other in candidates)
        {
            if (other == strokeIndex || ObjectLayer[other] != sourceLayer) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (IsTopologyStrokeShape(shape) && HasStroke(other))
            {
                AddCurveCurveIntersections(splits, source, StrokeSamples(other), includeSourceEndpoints: false);
            }
            else if (IsFillShape(shape) && (HasFill(other) || HasStroke(other)))
            {
                foreach (var contour in ShapeBoundaryContours(other))
                {
                    AddCurvePolylineIntersections(splits, source, contour, includeSourceEndpoints: false);
                }
            }
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private List<float> BoundarySplitParameters(int objectIndex, IReadOnlyList<int> candidates)
    {
        return BoundarySplitParameters(objectIndex, ShapeBoundary(objectIndex), candidates);
    }

    private List<float> BoundarySplitParameters(int objectIndex, PointF[] boundary, IReadOnlyList<int> candidates)
    {
        var splits = new List<DrawingTopologySplit>();
        var segmentCount = Math.Max(1, boundary.Length - 1);
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Ellipse)
        {
            splits.Add(new DrawingTopologySplit(0, boundary[0]));
            splits.Add(new DrawingTopologySplit(1, boundary[^1]));
        }
        else
        {
            for (var i = 0; i < boundary.Length; i++)
            {
                splits.Add(new DrawingTopologySplit(Math.Clamp(i / (float)segmentCount, 0, 1), boundary[i]));
            }
        }

        var sourceLayer = ObjectLayer[objectIndex];
        foreach (var other in candidates)
        {
            if (other == objectIndex || ObjectLayer[other] != sourceLayer) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (!HasStroke(other)) continue;
            if (IsTopologyStrokeShape(shape))
            {
                AddPolylineCurveIntersections(splits, boundary, StrokeSamples(other), includeSourceEndpoints: true);
            }
            else if (IsFillShape(shape))
            {
                foreach (var contour in ShapeBoundaryContours(other))
                {
                    AddPolylinePolylineIntersections(splits, boundary, contour, includeSourceEndpoints: true);
                }
            }
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private List<BoundaryStrokePart> BuildBoundaryStrokeParts(int objectIndex, IReadOnlyList<int> candidates)
    {
        var result = new List<BoundaryStrokePart>();
        var partIndex = 0;
        var contours = ShapeBoundaryContours(objectIndex);
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            var splits = BoundarySplitParameters(objectIndex, contour, candidates);
            foreach (var part in BuildPolylinePathParts(contour, splits))
            {
                result.Add(new BoundaryStrokePart(partIndex++, contourIndex, part.StartT, part.EndT, part.Points));
            }
        }

        return result;
    }

    private List<DrawingTopologySplit> NormalizeStrokeSplits(List<DrawingTopologySplit> splits)
    {
        splits.Sort((a, b) => a.T.CompareTo(b.T));
        var unique = new List<DrawingTopologySplit>(splits.Count);
        foreach (var split in splits)
        {
            var quantized = VectorUnits.Quantize(split.Point);
            if (unique.Count > 0
                && Math.Abs(unique[^1].T - split.T) <= 0.0001f
                && SameDrawingUnit(unique[^1].Point, quantized))
            {
                continue;
            }

            unique.Add(new DrawingTopologySplit(Math.Clamp(split.T, 0, 1), quantized));
        }

        if (unique.Count <= 2) return unique;

        var filtered = new List<DrawingTopologySplit> { unique[0] };
        for (var i = 1; i < unique.Count - 1; i++)
        {
            var previous = filtered[^1];
            var next = unique[i + 1];
            var current = unique[i];
            if (Distance(previous.Point, current.Point) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            if (Distance(current.Point, next.Point) < DrawingTopologyRules.MinStrokeSegmentUnits) continue;
            filtered.Add(current);
        }

        if (Distance(filtered[^1].Point, unique[^1].Point) >= DrawingTopologyRules.MinStrokeSegmentUnits || filtered.Count == 1)
        {
            filtered.Add(unique[^1]);
        }
        else
        {
            filtered[^1] = unique[^1];
        }

        return filtered;
    }

    private int FillPartIndex(PointF world, int fillIndex, IReadOnlyList<int> candidates)
    {
        var regions = BuildFillRegions(fillIndex, candidates);
        for (var part = 0; part < regions.Count; part++)
        {
            if (PointInCompoundPolygonOrOnBoundary(world, regions[part].Contours)) return part;
        }

        return -1;
    }

    private List<DrawingTopologySplit> CollectCurvePolylineIntersections(CurveSample[] source, PointF[] cutter)
    {
        var result = new List<DrawingTopologySplit>();
        AddCurvePolylineIntersections(result, source, cutter, includeSourceEndpoints: false);
        return result;
    }

    private void AddCurveCurveIntersections(List<DrawingTopologySplit> result, CurveSample[] source, CurveSample[] cutter, bool includeSourceEndpoints)
    {
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, source[i], source[i + 1], cutter[j].Point, cutter[j + 1].Point, includeSourceEndpoints);
            }
        }
    }

    private void AddCurvePolylineIntersections(List<DrawingTopologySplit> result, CurveSample[] source, PointF[] cutter, bool includeSourceEndpoints)
    {
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, source[i], source[i + 1], cutter[j], cutter[j + 1], includeSourceEndpoints);
            }
        }
    }

    private void AddPolylineCurveIntersections(List<DrawingTopologySplit> result, PointF[] source, CurveSample[] cutter, bool includeSourceEndpoints)
    {
        var sourceSegments = Math.Max(1, source.Length - 1);
        for (var i = 0; i < source.Length - 1; i++)
        {
            var sourceStart = new CurveSample(i / (float)sourceSegments, source[i]);
            var sourceEnd = new CurveSample((i + 1) / (float)sourceSegments, source[i + 1]);
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, sourceStart, sourceEnd, cutter[j].Point, cutter[j + 1].Point, includeSourceEndpoints);
            }
        }
    }

    private void AddPolylinePolylineIntersections(List<DrawingTopologySplit> result, PointF[] source, PointF[] cutter, bool includeSourceEndpoints)
    {
        var sourceSegments = Math.Max(1, source.Length - 1);
        for (var i = 0; i < source.Length - 1; i++)
        {
            var sourceStart = new CurveSample(i / (float)sourceSegments, source[i]);
            var sourceEnd = new CurveSample((i + 1) / (float)sourceSegments, source[i + 1]);
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                AddTopologySegmentIntersections(result, sourceStart, sourceEnd, cutter[j], cutter[j + 1], includeSourceEndpoints);
            }
        }
    }

    private void AddTopologySegmentIntersections(
        List<DrawingTopologySplit> result,
        CurveSample sourceStart,
        CurveSample sourceEnd,
        PointF cutterStart,
        PointF cutterEnd,
        bool includeSourceEndpoints)
    {
        var count = SegmentIntersectionParameters(sourceStart.Point, sourceEnd.Point, cutterStart, cutterEnd, out var first, out var second);
        if (count >= 1)
        {
            AddTopologySegmentIntersection(result, sourceStart, sourceEnd, first, includeSourceEndpoints);
            if (count >= 2) AddTopologySegmentIntersection(result, sourceStart, sourceEnd, second, includeSourceEndpoints);
            return;
        }

        // A terminating stroke can meet another stroke without producing a crossing.
        // Keep that contact as a topology node so the endpoint can be selected and edited.
        if (TryEndpointContactParameter(sourceStart.Point, sourceEnd.Point, cutterStart, cutterEnd, out var contact))
        {
            AddTopologySegmentIntersection(result, sourceStart, sourceEnd, contact, includeSourceEndpoints);
        }
    }

    private static bool TryEndpointContactParameter(
        PointF sourceStart,
        PointF sourceEnd,
        PointF cutterStart,
        PointF cutterEnd,
        out float parameter)
    {
        parameter = 0;
        if (TryPointOnSegment(cutterStart, sourceStart, sourceEnd, out parameter)) return true;
        if (TryPointOnSegment(cutterEnd, sourceStart, sourceEnd, out parameter)) return true;
        if (TryPointOnSegment(sourceStart, cutterStart, cutterEnd, out _)) return true;
        if (!TryPointOnSegment(sourceEnd, cutterStart, cutterEnd, out _)) return false;
        parameter = 1;
        return true;
    }

    private static bool TryPointOnSegment(PointF point, PointF start, PointF end, out float parameter)
    {
        parameter = 0;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        var tolerance = DrawingTopologyRules.UnitIntersectionTolerance;
        if (lengthSquared <= tolerance * tolerance)
        {
            return Distance(point, start) <= tolerance;
        }

        var projected = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        var length = MathF.Sqrt(lengthSquared);
        var parameterTolerance = tolerance / length;
        if (projected < -parameterTolerance || projected > 1 + parameterTolerance) return false;

        parameter = Math.Clamp(projected, 0, 1);
        var closest = Lerp(start, end, parameter);
        return Distance(point, closest) <= tolerance;
    }

    private static void AddTopologySegmentIntersection(
        List<DrawingTopologySplit> result,
        CurveSample sourceStart,
        CurveSample sourceEnd,
        float localT,
        bool includeSourceEndpoints)
    {
        var globalT = sourceStart.T + (sourceEnd.T - sourceStart.T) * localT;
        var point = Lerp(sourceStart.Point, sourceEnd.Point, localT);
        if (!includeSourceEndpoints
            && (sourceStart.T <= 0 && Distance(point, sourceStart.Point) <= DrawingTopologyRules.UnitIntersectionTolerance
                || sourceEnd.T >= 1 && Distance(point, sourceEnd.Point) <= DrawingTopologyRules.UnitIntersectionTolerance))
        {
            return;
        }

        result.Add(new DrawingTopologySplit(globalT, point));
    }

    private (PointF Start, PointF Control, PointF End) LineCurve(int i)
    {
        var halfW = Width[i] * 0.5f;
        var start = LocalToWorld(i, -halfW, 0);
        var end = LocalToWorld(i, halfW, 0);
        var control = new PointF(CurveControlX[i], CurveControlY[i]);
        return (start, control, end);
    }

    private CurveSample[] CurveSamples(int i)
    {
        var (start, control, end) = LineCurve(i);
        var samples = new List<CurveSample>(32) { new(0, start) };
        AddAdaptiveQuadraticSamples(samples, start, control, end, 0, 1, 0);
        samples.Add(new CurveSample(1, end));
        return samples.ToArray();
    }

    private CurveSample[] StrokeSamples(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<CurveSample>();
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line) return CurveSamples(objectIndex);
        if (!IsFreehandShape(ShapeKind[objectIndex]) || !TryGetFreehandWorldPoints(objectIndex, out var points) || points.Length == 0)
        {
            return Array.Empty<CurveSample>();
        }

        if (points.Length == 1) return new[] { new CurveSample(0, points[0]) };
        var samples = new CurveSample[points.Length];
        var segmentCount = points.Length - 1;
        for (var i = 0; i < points.Length; i++) samples[i] = new CurveSample(i / (float)segmentCount, points[i]);
        return samples;
    }

    private static void AddAdaptiveQuadraticSamples(List<CurveSample> samples, PointF start, PointF control, PointF end, float startT, float endT, int depth)
    {
        const int maxDepth = 9;
        const float flatnessUnits = 0.35f;
        if (depth >= maxDepth || DistanceToSegment(control, start, end) <= flatnessUnits)
        {
            return;
        }

        var startControl = Midpoint(start, control);
        var controlEnd = Midpoint(control, end);
        var middle = Midpoint(startControl, controlEnd);
        var middleT = (startT + endT) * 0.5f;
        AddAdaptiveQuadraticSamples(samples, start, startControl, middle, startT, middleT, depth + 1);
        samples.Add(new CurveSample(middleT, middle));
        AddAdaptiveQuadraticSamples(samples, middle, controlEnd, end, middleT, endT, depth + 1);
    }

    private PointF[] ShapeBoundary(int i)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (IsFreehandShape(shape) && TryGetFreehandWorldPoints(i, out var freehandPoints))
        {
            return freehandPoints;
        }

        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldContours(i, out var pathContours) && pathContours.Length > 0)
        {
            var pathPoints = pathContours
                .OrderByDescending(contour => Math.Abs(PolygonArea(contour)))
                .First();
            var boundary = new PointF[pathPoints.Length + 1];
            Array.Copy(pathPoints, boundary, pathPoints.Length);
            boundary[^1] = pathPoints[0];
            return boundary;
        }

        var halfW = Width[i] * 0.5f;
        var halfH = Height[i] * 0.5f;
        var local = shape switch
        {
            VectorAnimationEngine.ShapeKind.Ellipse => EllipseBoundary(halfW, halfH),
            VectorAnimationEngine.ShapeKind.Triangle => RegularBoundary(3, halfW, halfH, -MathF.PI / 2),
            VectorAnimationEngine.ShapeKind.Polygon => RegularBoundary(GetShapeVertexCount(i), halfW, halfH, -MathF.PI / 2),
            VectorAnimationEngine.ShapeKind.Star => StarBoundary(GetShapeVertexCount(i), halfW, halfH),
            _ => new[]
            {
                new PointF(-halfW, -halfH),
                new PointF(halfW, -halfH),
                new PointF(halfW, halfH),
                new PointF(-halfW, halfH),
                new PointF(-halfW, -halfH)
            }
        };

        var result = new PointF[local.Length];
        for (var p = 0; p < local.Length; p++) result[p] = LocalToWorld(i, local[p].X, local[p].Y);
        return result;
    }

    private PointF[][] ShapeBoundaryContours(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return Array.Empty<PointF[]>();
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Path
            && TryGetPathWorldContours(objectIndex, out var pathContours))
        {
            var result = new List<PointF[]>(pathContours.Length);
            foreach (var contour in pathContours)
            {
                var closed = ClosePolyline(contour);
                if (closed.Length >= 4) result.Add(closed);
            }

            return result.ToArray();
        }

        var boundary = ShapeBoundary(objectIndex);
        return boundary.Length >= 2 ? new[] { boundary } : Array.Empty<PointF[]>();
    }

    internal PointF[][] GetObjectBoundaryContours(int objectIndex) => ShapeBoundaryContours(objectIndex);

    private static PointF[] EllipseBoundary(float halfW, float halfH)
    {
        const int count = 32;
        var points = new PointF[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var angle = i * MathF.Tau / count;
            points[i] = new PointF(MathF.Cos(angle) * halfW, MathF.Sin(angle) * halfH);
        }

        return points;
    }

    private static PointF[] RegularBoundary(int sides, float halfW, float halfH, float startAngle)
    {
        var points = new PointF[sides + 1];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new PointF(MathF.Cos(angle) * halfW, MathF.Sin(angle) * halfH);
        }

        points[^1] = points[0];
        return points;
    }

    private static PointF[] StarBoundary(int points, float halfW, float halfH)
    {
        var result = new PointF[points * 2 + 1];
        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? 1f : 0.46f;
            var angle = -MathF.PI / 2 + i * MathF.Tau / (points * 2);
            result[i] = new PointF(MathF.Cos(angle) * halfW * radius, MathF.Sin(angle) * halfH * radius);
        }

        result[^1] = result[0];
        return result;
    }

    private static bool TrySegmentIntersection(PointF a, PointF b, PointF c, PointF d, out float t)
    {
        return SegmentIntersectionParameters(a, b, c, d, out t, out _) > 0;
    }

    private static int SegmentIntersectionParameters(
        PointF a,
        PointF b,
        PointF c,
        PointF d,
        out float first,
        out float second)
    {
        first = 0;
        second = 0;
        var tolerance = (double)DrawingTopologyRules.UnitIntersectionTolerance;
        var toleranceSquared = tolerance * tolerance;
        var rX = (double)b.X - a.X;
        var rY = (double)b.Y - a.Y;
        var sX = (double)d.X - c.X;
        var sY = (double)d.Y - c.Y;
        var rLengthSquared = rX * rX + rY * rY;
        var sLengthSquared = sX * sX + sY * sY;

        if (rLengthSquared <= toleranceSquared)
        {
            if (DistanceToSegment(a, c, d) > tolerance) return 0;
            return 1;
        }

        var rLength = Math.Sqrt(rLengthSquared);
        var cax = (double)c.X - a.X;
        var cay = (double)c.Y - a.Y;
        if (sLengthSquared <= toleranceSquared)
        {
            var projected = (cax * rX + cay * rY) / rLengthSquared;
            var parameterTolerance = tolerance / rLength;
            if (projected < -parameterTolerance || projected > 1 + parameterTolerance) return 0;

            var clamped = Math.Clamp(projected, 0, 1);
            var pointX = a.X + rX * clamped;
            var pointY = a.Y + rY * clamped;
            var dx = c.X - pointX;
            var dy = c.Y - pointY;
            if (dx * dx + dy * dy > toleranceSquared) return 0;

            first = second = (float)clamped;
            return 1;
        }

        var sLength = Math.Sqrt(sLengthSquared);
        var dax = (double)d.X - a.X;
        var day = (double)d.Y - a.Y;
        var collinearTolerance = tolerance * rLength;
        if (Math.Abs(cax * rY - cay * rX) <= collinearTolerance
            && Math.Abs(dax * rY - day * rX) <= collinearTolerance)
        {
            var cParameter = (cax * rX + cay * rY) / rLengthSquared;
            var dParameter = (dax * rX + day * rY) / rLengthSquared;
            var overlapStart = Math.Min(cParameter, dParameter);
            var overlapEnd = Math.Max(cParameter, dParameter);
            var overlapTolerance = tolerance / rLength;
            if (overlapEnd < -overlapTolerance || overlapStart > 1 + overlapTolerance) return 0;

            overlapStart = Math.Clamp(overlapStart, 0, 1);
            overlapEnd = Math.Clamp(overlapEnd, 0, 1);
            if ((overlapEnd - overlapStart) * rLength <= tolerance)
            {
                first = second = (float)((overlapStart + overlapEnd) * 0.5);
                return 1;
            }

            first = (float)overlapStart;
            second = (float)overlapEnd;
            return 2;
        }

        var denominator = rX * sY - rY * sX;
        var parallelTolerance = toleranceSquared * tolerance * Math.Max(1, rLength * sLength);
        if (Math.Abs(denominator) <= parallelTolerance) return 0;

        var t = (cax * sY - cay * sX) / denominator;
        var u = (cax * rY - cay * rX) / denominator;
        if (t < -tolerance / rLength
            || t > 1 + tolerance / rLength
            || u < -tolerance / sLength
            || u > 1 + tolerance / sLength)
        {
            return 0;
        }

        first = second = (float)Math.Clamp(t, 0, 1);
        return 1;
    }

    private static bool SameDrawingUnit(PointF a, PointF b)
    {
        return MathF.Floor(a.X) == MathF.Floor(b.X) && MathF.Floor(a.Y) == MathF.Floor(b.Y);
    }

    private static PointF Lerp(PointF a, PointF b, float t)
    {
        return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static PointF Midpoint(PointF a, PointF b)
    {
        return new PointF((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
    }

    private static PointF PolylinePointAt(PointF[] points, float t)
    {
        if (points.Length == 0) return PointF.Empty;
        if (points.Length == 1) return points[0];
        t = Math.Clamp(t, 0, 1);
        var segments = points.Length - 1;
        var scaled = t * segments;
        var index = Math.Min(segments - 1, (int)MathF.Floor(scaled));
        return Lerp(points[index], points[index + 1], scaled - index);
    }

    private static PointF[] PolylineSlice(PointF[] points, float startT, float endT)
    {
        if (points.Length == 0) return Array.Empty<PointF>();
        if (points.Length == 1) return new[] { points[0] };

        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var segmentCount = points.Length - 1;
        var startScaled = startT * segmentCount;
        var endScaled = endT * segmentCount;
        var firstVertex = Math.Clamp((int)MathF.Floor(startScaled) + 1, 1, points.Length - 1);
        var lastVertex = Math.Clamp((int)MathF.Ceiling(endScaled) - 1, 0, points.Length - 2);
        var result = new List<PointF> { VectorUnits.Quantize(PolylinePointAt(points, startT)) };

        for (var i = firstVertex; i <= lastVertex; i++)
        {
            var point = VectorUnits.Quantize(points[i]);
            if (result[^1] != point) result.Add(point);
        }

        var end = VectorUnits.Quantize(PolylinePointAt(points, endT));
        if (result[^1] != end) result.Add(end);
        return result.ToArray();
    }

    private static float PolylineLength(IReadOnlyList<PointF> points)
    {
        var length = 0f;
        for (var i = 0; i < points.Count - 1; i++) length += Distance(points[i], points[i + 1]);
        return length;
    }

    private static float DistanceToPolyline(PointF point, IReadOnlyList<PointF> polyline)
    {
        if (polyline.Count == 0) return float.MaxValue;
        if (polyline.Count == 1) return Distance(point, polyline[0]);
        var distance = float.MaxValue;
        for (var i = 0; i < polyline.Count - 1; i++)
        {
            distance = Math.Min(distance, DistanceToSegment(point, polyline[i], polyline[i + 1]));
        }

        return distance;
    }

    private static PointF[] ClosePolyline(PointF[] points)
    {
        if (points.Length == 0) return Array.Empty<PointF>();
        if (points.Length > 1 && SameDrawingUnit(points[0], points[^1])) return points.ToArray();
        var result = new PointF[points.Length + 1];
        Array.Copy(points, result, points.Length);
        result[^1] = points[0];
        return result;
    }

    private static PointF[] ReplaceBoundarySegment(
        IReadOnlyList<PointF> closedContour,
        int segmentIndex,
        int segmentCount,
        IReadOnlyList<PointF> replacement)
    {
        if (closedContour.Count < 4
            || segmentIndex < 0
            || segmentCount <= 0
            || segmentIndex + segmentCount >= closedContour.Count
            || replacement.Count < 2)
        {
            return Array.Empty<PointF>();
        }

        var result = new List<PointF>(closedContour.Count + replacement.Count);
        void Add(PointF point)
        {
            point = VectorUnits.Quantize(point);
            if (result.Count == 0 || !SameDrawingUnit(result[^1], point)) result.Add(point);
        }

        for (var index = 0; index < segmentIndex; index++) Add(closedContour[index]);
        for (var index = 0; index < replacement.Count; index++) Add(replacement[index]);
        for (var index = segmentIndex + segmentCount + 1; index < closedContour.Count; index++) Add(closedContour[index]);
        if (result.Count > 1 && SameDrawingUnit(result[0], result[^1])) result.RemoveAt(result.Count - 1);
        return result.Count >= 3 ? result.ToArray() : Array.Empty<PointF>();
    }

    private static bool BoundarySequenceMatches(
        IReadOnlyList<PointF> contour,
        int segmentIndex,
        IReadOnlyList<PointF> linePoints)
    {
        if (linePoints.Count < 2 || segmentIndex < 0 || segmentIndex + linePoints.Count > contour.Count) return false;
        for (var pointIndex = 0; pointIndex < linePoints.Count; pointIndex++)
        {
            if (!SameDrawingUnit(contour[segmentIndex + pointIndex], linePoints[pointIndex])) return false;
        }

        return true;
    }

    private List<float> LineRectSplitParameters(int lineIndex, RectangleF bounds)
    {
        var splits = new List<DrawingTopologySplit>();
        var samples = CurveSamples(lineIndex);
        splits.Add(new DrawingTopologySplit(0, samples[0].Point));
        splits.Add(new DrawingTopologySplit(1, samples[^1].Point));

        for (var i = 0; i < samples.Length - 1; i++)
        {
            AddSegmentRectIntersections(splits, samples[i], samples[i + 1], bounds);
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private static List<float> SegmentRectSplitParameters(PointF start, PointF end, RectangleF bounds)
    {
        var splits = new List<float> { 0, 1 };
        if (!TryClipSegmentToRectangle(start, end, bounds, out var enter, out var exit)) return splits;

        if (enter > 0.0001f && enter < 0.9999f) splits.Add(enter);
        if (exit > 0.0001f && exit < 0.9999f) splits.Add(exit);
        splits.Sort();

        var write = 1;
        for (var read = 1; read < splits.Count; read++)
        {
            if (splits[read] - splits[write - 1] <= 0.0001f) continue;
            splits[write++] = splits[read];
        }

        if (write < splits.Count) splits.RemoveRange(write, splits.Count - write);
        return splits;
    }

    private static bool TryClipSegmentToRectangle(PointF start, PointF end, RectangleF bounds, out float enter, out float exit)
    {
        enter = 0;
        exit = 1;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        return Clip(-dx, start.X - bounds.Left, ref enter, ref exit)
            && Clip(dx, bounds.Right - start.X, ref enter, ref exit)
            && Clip(-dy, start.Y - bounds.Top, ref enter, ref exit)
            && Clip(dy, bounds.Bottom - start.Y, ref enter, ref exit);
    }

    private static bool Clip(float direction, float distance, ref float enter, ref float exit)
    {
        if (Math.Abs(direction) <= 0.000001f) return distance >= 0;

        var t = distance / direction;
        if (direction < 0)
        {
            if (t > exit) return false;
            if (t > enter) enter = t;
        }
        else
        {
            if (t < enter) return false;
            if (t < exit) exit = t;
        }

        return true;
    }

    private static void AddSegmentRectIntersections(List<DrawingTopologySplit> splits, CurveSample a, CurveSample b, RectangleF bounds)
    {
        var topLeft = new PointF(bounds.Left, bounds.Top);
        var topRight = new PointF(bounds.Right, bounds.Top);
        var bottomRight = new PointF(bounds.Right, bounds.Bottom);
        var bottomLeft = new PointF(bounds.Left, bounds.Bottom);
        AddSegmentRectIntersection(splits, a, b, topLeft, topRight);
        AddSegmentRectIntersection(splits, a, b, topRight, bottomRight);
        AddSegmentRectIntersection(splits, a, b, bottomRight, bottomLeft);
        AddSegmentRectIntersection(splits, a, b, bottomLeft, topLeft);
    }

    private static void AddSegmentRectIntersection(List<DrawingTopologySplit> splits, CurveSample a, CurveSample b, PointF edgeStart, PointF edgeEnd)
    {
        if (!TrySegmentIntersection(a.Point, b.Point, edgeStart, edgeEnd, out var localT)) return;
        var globalT = a.T + (b.T - a.T) * localT;
        if (globalT <= 0.0001f || globalT >= 0.9999f) return;
        splits.Add(new DrawingTopologySplit(globalT, Lerp(a.Point, b.Point, localT)));
    }

    private static PointF[] OpenPolygon(PointF[] polygon)
    {
        if (polygon.Length > 1 && SameDrawingUnit(polygon[0], polygon[^1])) return polygon[..^1];
        return polygon;
    }

    private static float PolygonArea(IReadOnlyList<PointF> polygon)
    {
        if (polygon.Count < 3) return 0;
        var origin = polygon[0];
        var area = 0d;
        for (var i = 1; i < polygon.Count - 1; i++)
        {
            var ax = (double)polygon[i].X - origin.X;
            var ay = (double)polygon[i].Y - origin.Y;
            var bx = (double)polygon[i + 1].X - origin.X;
            var by = (double)polygon[i + 1].Y - origin.Y;
            area += ax * by - bx * ay;
        }

        return (float)(area * 0.5d);
    }

    private static RectangleF NormalizeToDrawingUnits(RectangleF rect)
    {
        var normalized = Normalize(rect);
        var left = MathF.Floor(normalized.Left);
        var top = MathF.Floor(normalized.Top);
        var right = MathF.Ceiling(normalized.Right);
        var bottom = MathF.Ceiling(normalized.Bottom);
        return RectangleF.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    private static List<PointF> RemoveDuplicatePolygonPoints(List<PointF> points)
    {
        var result = new List<PointF>(points.Count);
        foreach (var point in points)
        {
            if (result.Count > 0 && SameDrawingUnit(result[^1], point)) continue;
            result.Add(point);
        }

        if (result.Count > 1 && SameDrawingUnit(result[0], result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool PointInPolygon(PointF point, PointF[] polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) == (pj.Y > point.Y)) continue;
            var denominator = pj.Y - pi.Y;
            if (Math.Abs(denominator) < 0.0001f) continue;
            var x = (pj.X - pi.X) * (point.Y - pi.Y) / denominator + pi.X;
            if (point.X < x) inside = !inside;
        }

        return inside;
    }

    private static bool PointInCompoundPolygon(PointF point, PointF[][] contours)
    {
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            if (PointInPolygon(point, contour)) inside = !inside;
        }

        return inside;
    }

    private static bool PointInCompoundPolygonOrOnBoundary(PointF point, PointF[][] contours)
    {
        if (PointInCompoundPolygon(point, contours)) return true;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            for (var i = 0; i < contour.Length; i++)
            {
                if (DistanceToSegment(point, contour[i], contour[(i + 1) % contour.Length]) <= 0.75f) return true;
            }
        }

        return false;
    }

    private static PointF[][] NormalizePathContours(IReadOnlyList<PointF[]> contours)
    {
        var result = new List<PointF[]>(contours.Count);
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var cleaned = RemoveDuplicatePolygonPoints(contour.Select(VectorUnits.Quantize).ToList());
            if (cleaned.Count < 3 || Math.Abs(PolygonArea(cleaned)) < 0.5f) continue;
            result.Add(cleaned.ToArray());
        }

        return result
            .OrderByDescending(contour => Math.Abs(PolygonArea(contour)))
            .ToArray();
    }

    private static PointF[] NormalizeFreehandPoints(IReadOnlyList<PointF> points)
    {
        var result = new List<PointF>(points.Count);
        foreach (var source in points)
        {
            var point = VectorUnits.Quantize(source);
            if (result.Count > 0 && result[^1] == point) continue;
            result.Add(point);
        }

        return result.ToArray();
    }

    private static PointF[][] CloneContours(PointF[][] contours)
    {
        var clone = new PointF[contours.Length][];
        for (var i = 0; i < contours.Length; i++) clone[i] = contours[i].ToArray();
        return clone;
    }

    private static float SegmentProjectionT(PointF point, PointF start, PointF end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSq = vx * vx + vy * vy;
        if (lengthSq <= 0.0001f) return 0;
        return Math.Clamp(((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSq, 0, 1);
    }

    private bool ObjectIntersectsBounds(int i, RectangleF bounds)
    {
        var objectBounds = GetObjectWorldBounds(i);
        return objectBounds.Left <= bounds.Right
            && objectBounds.Right >= bounds.Left
            && objectBounds.Top <= bounds.Bottom
            && objectBounds.Bottom >= bounds.Top;
    }

    internal RectangleF GetObjectWorldBounds(int i)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        var margin = Math.Max(Stroke[i] * 0.5f, 1);
        if (shape == VectorAnimationEngine.ShapeKind.Path
            && _pathLocalContours.TryGetValue(i, out var pathContours)
            && pathContours.Length > 0)
        {
            var hasPoint = false;
            var left = 0f;
            var right = 0f;
            var top = 0f;
            var bottom = 0f;
            foreach (var contour in pathContours)
            {
                foreach (var local in contour)
                {
                    var point = LocalToWorld(i, local.X, local.Y);
                    if (!hasPoint)
                    {
                        left = right = point.X;
                        top = bottom = point.Y;
                        hasPoint = true;
                        continue;
                    }

                    left = Math.Min(left, point.X);
                    right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y);
                    bottom = Math.Max(bottom, point.Y);
                }
            }

            if (hasPoint) return RectangleF.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
        }

        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfW = Width[i] * 0.5f;
            var start = LocalToWorld(i, -halfW, 0);
            var end = LocalToWorld(i, halfW, 0);
            var control = new PointF(CurveControlX[i], CurveControlY[i]);
            var bounds = QuadraticCurveBounds(start, control, end);
            bounds.Inflate(margin, margin);
            return bounds;
        }

        if (IsFreehandShape(shape)
            && _freehandLocalPoints.TryGetValue(i, out var freehandPoints)
            && freehandPoints.Length > 0)
        {
            var first = LocalToWorld(i, freehandPoints[0].X, freehandPoints[0].Y);
            var left = first.X;
            var right = first.X;
            var top = first.Y;
            var bottom = first.Y;
            for (var p = 1; p < freehandPoints.Length; p++)
            {
                var point = LocalToWorld(i, freehandPoints[p].X, freehandPoints[p].Y);
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }

            return RectangleF.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
        }

        var halfWShape = Width[i] * 0.5f;
        var halfHShape = Height[i] * 0.5f;
        if (Math.Abs(Angle[i]) < 0.0001f)
        {
            return RectangleF.FromLTRB(X[i] - halfWShape - margin, Y[i] - halfHShape - margin, X[i] + halfWShape + margin, Y[i] + halfHShape + margin);
        }

        var p0 = LocalToWorld(i, -halfWShape, -halfHShape);
        var p1 = LocalToWorld(i, halfWShape, -halfHShape);
        var p2 = LocalToWorld(i, halfWShape, halfHShape);
        var p3 = LocalToWorld(i, -halfWShape, halfHShape);
        var leftRotated = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)) - margin;
        var rightRotated = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)) + margin;
        var topRotated = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)) - margin;
        var bottomRotated = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)) + margin;
        return RectangleF.FromLTRB(leftRotated, topRotated, rightRotated, bottomRotated);
    }

    private static RectangleF Normalize(RectangleF rect)
    {
        return RectangleF.FromLTRB(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Top, rect.Bottom),
            Math.Max(rect.Left, rect.Right),
            Math.Max(rect.Top, rect.Bottom));
    }

    private PointF WorldToLocal(int i, PointF world)
    {
        var dx = world.X - X[i];
        var dy = world.Y - Y[i];
        var angle = Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(dx * cos + dy * sin, -dx * sin + dy * cos);
    }

    private PointF LocalToWorld(int i, float localX, float localY)
    {
        var angle = Angle[i];
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new PointF(X[i] + localX * cos - localY * sin, Y[i] + localX * sin + localY * cos);
    }

    private static float DistanceToQuadratic(PointF point, PointF start, PointF control, PointF end)
    {
        var best = float.MaxValue;
        var previous = start;
        const int segments = 24;
        for (var s = 1; s <= segments; s++)
        {
            var t = s / (float)segments;
            var current = QuadraticPoint(start, control, end, t);
            best = Math.Min(best, DistanceToSegment(point, previous, current));
            previous = current;
        }

        return best;
    }

    private static bool QuadraticCurveInsideRectangle(PointF start, PointF control, PointF end, RectangleF bounds)
    {
        var curveBounds = QuadraticCurveBounds(start, control, end);
        return curveBounds.Left >= bounds.Left - DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Right <= bounds.Right + DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Top >= bounds.Top - DrawingTopologyRules.UnitIntersectionTolerance
            && curveBounds.Bottom <= bounds.Bottom + DrawingTopologyRules.UnitIntersectionTolerance;
    }

    private static RectangleF QuadraticCurveBounds(PointF start, PointF control, PointF end)
    {
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);
        IncludeQuadraticExtremum(start.X, control.X, end.X, ref left, ref right);
        IncludeQuadraticExtremum(start.Y, control.Y, end.Y, ref top, ref bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static void IncludeQuadraticExtremum(float start, float control, float end, ref float minimum, ref float maximum)
    {
        var denominator = start - 2 * control + end;
        if (Math.Abs(denominator) <= 0.000001f) return;
        var t = (start - control) / denominator;
        if (t <= 0 || t >= 1) return;
        var value = QuadraticCoordinate(start, control, end, t);
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
    }

    private static float QuadraticCoordinate(float start, float control, float end, float t)
    {
        var inv = 1 - t;
        return inv * inv * start + 2 * inv * t * control + t * t * end;
    }

    private static PointF QuadraticPoint(PointF start, PointF control, PointF end, float t)
    {
        return new PointF(
            QuadraticCoordinate(start.X, control.X, end.X, t),
            QuadraticCoordinate(start.Y, control.Y, end.Y, t));
    }

    private static (PointF Start, PointF Control, PointF End) QuadraticSubcurve(PointF start, PointF control, PointF end, float startT, float endT)
    {
        startT = Math.Clamp(startT, 0, 1);
        endT = Math.Clamp(endT, startT, 1);
        var p0 = QuadraticPoint(start, control, end, startT);
        var p2 = QuadraticPoint(start, control, end, endT);
        if (endT - startT <= 0.0001f) return (p0, Midpoint(p0, p2), p2);

        var derivativeStart = QuadraticDerivative(start, control, end, startT);
        var derivativeEnd = QuadraticDerivative(start, control, end, endT);
        var duration = endT - startT;
        var c0 = new PointF(p0.X + derivativeStart.X * duration * 0.5f, p0.Y + derivativeStart.Y * duration * 0.5f);
        var c1 = new PointF(p2.X - derivativeEnd.X * duration * 0.5f, p2.Y - derivativeEnd.Y * duration * 0.5f);
        return (p0, Midpoint(c0, c1), p2);
    }

    private static PointF QuadraticDerivative(PointF start, PointF control, PointF end, float t)
    {
        return new PointF(
            2 * ((1 - t) * (control.X - start.X) + t * (end.X - control.X)),
            2 * ((1 - t) * (control.Y - start.Y) + t * (end.Y - control.Y)));
    }

    private static float DistanceToSegment(PointF point, PointF start, PointF end)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSq = vx * vx + vy * vy;
        if (lengthSq <= 0.0001f) return Distance(point, start);

        var t = ((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSq;
        t = Math.Clamp(t, 0, 1);
        return Distance(point, new PointF(start.X + vx * t, start.Y + vy * t));
    }

    private static float Distance(PointF a, PointF b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is VectorAnimationEngine.ShapeKind.Freeform or VectorAnimationEngine.ShapeKind.BrushStroke;
    }

    private static bool IsTopologyStrokeShape(ShapeKind shape)
    {
        return shape == VectorAnimationEngine.ShapeKind.Line || IsFreehandShape(shape);
    }

    private static bool IsFillBoundaryLinkedStrokeShape(ShapeKind shape)
    {
        return shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform;
    }

    private bool HasFill(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && IsFillShape(ShapeKind[objectIndex])
            && Color.FromArgb(Argb[objectIndex]).A > 0;
    }

    private bool HasStroke(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount
            && Stroke[objectIndex] > 0
            && Color.FromArgb(StrokeArgb[objectIndex]).A > 0;
    }

    private static bool IsFillShape(ShapeKind shape)
    {
        return shape is not VectorAnimationEngine.ShapeKind.Line
            and not VectorAnimationEngine.ShapeKind.Freeform
            and not VectorAnimationEngine.ShapeKind.BrushStroke;
    }

    private static bool SupportsGradient(ShapeKind shape) => IsFillShape(shape) || shape == VectorAnimationEngine.ShapeKind.Line;

    private void CopyObjectData(int from, int to)
    {
        CopyObjectDataFrom(this, from, to);
    }

    private void CopyObjectDataFrom(VectorScene source, int from, int to)
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
        LineEndpointStyles[to] = source.LineEndpointStyles[from];
        LineEndEndpointStyles[to] = source.LineEndEndpointStyles[from];
        ShapeKind[to] = source.ShapeKind[from];
        ShapeVertexCounts[to] = source.GetShapeVertexCount(from);
        AtomCount[to] = source.AtomCount[from];
        Argb[to] = source.Argb[from];
        StrokeArgb[to] = source.StrokeArgb[from];
        LinearGradientEnabled[to] = source.LinearGradientEnabled[from];
        GradientStartArgb[to] = source.GradientStartArgb[from];
        GradientEndArgb[to] = source.GradientEndArgb[from];
        GradientStartX[to] = source.GradientStartX[from];
        GradientStartY[to] = source.GradientStartY[from];
        GradientEndX[to] = source.GradientEndX[from];
        GradientEndY[to] = source.GradientEndY[from];
        GradientKinds[to] = source.GradientKinds[from];
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

        if (source._freehandLocalPoints.TryGetValue(from, out var freehandPoints))
        {
            _freehandLocalPoints[to] = freehandPoints.ToArray();
        }
        else
        {
            _freehandLocalPoints.Remove(to);
        }
    }

    private void RemovePathDataOutsideObjectCount()
    {
        foreach (var index in _pathLocalContours.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _pathLocalContours.Remove(index);
        }
    }

    private void RemoveFreehandDataOutsideObjectCount()
    {
        foreach (var index in _freehandLocalPoints.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _freehandLocalPoints.Remove(index);
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

    private void ResizeObjectArrays()
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
        var lineEndpointStyles = LineEndpointStyles;
        var lineEndEndpointStyles = LineEndEndpointStyles;
        var shapeKind = ShapeKind;
        var shapeVertexCounts = ShapeVertexCounts;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
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
        Array.Resize(ref lineEndpointStyles, ObjectCount);
        Array.Resize(ref lineEndEndpointStyles, ObjectCount);
        Array.Resize(ref shapeKind, ObjectCount);
        Array.Resize(ref shapeVertexCounts, ObjectCount);
        Array.Resize(ref atomCount, ObjectCount);
        Array.Resize(ref argb, ObjectCount);
        Array.Resize(ref strokeArgb, ObjectCount);
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
        LineEndpointStyles = lineEndpointStyles;
        LineEndEndpointStyles = lineEndEndpointStyles;
        ShapeKind = shapeKind;
        ShapeVertexCounts = shapeVertexCounts;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
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
        var lineEndpointStyles = LineEndpointStyles;
        var lineEndEndpointStyles = LineEndEndpointStyles;
        var shapeKind = ShapeKind;
        var shapeVertexCounts = ShapeVertexCounts;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
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
        Array.Resize(ref lineEndpointStyles, capacity);
        Array.Resize(ref lineEndEndpointStyles, capacity);
        Array.Resize(ref shapeKind, capacity);
        Array.Resize(ref shapeVertexCounts, capacity);
        Array.Resize(ref atomCount, capacity);
        Array.Resize(ref argb, capacity);
        Array.Resize(ref strokeArgb, capacity);
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
        LineEndpointStyles = lineEndpointStyles;
        LineEndEndpointStyles = lineEndEndpointStyles;
        ShapeKind = shapeKind;
        ShapeVertexCounts = shapeVertexCounts;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
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

    private void AddObjectToTileSummary(int i, Color color, long[] tileR, long[] tileG, long[] tileB)
    {
        var tx = (int)Math.Clamp((X[i] + StageWidth * 0.5f) / StageWidth * TileColumns, 0, TileColumns - 1);
        var ty = (int)Math.Clamp((Y[i] + StageHeight * 0.5f) / StageHeight * TileRows, 0, TileRows - 1);
        var tile = ty * TileColumns + tx;
        TileCount[tile]++;
        TileAtoms[tile] += AtomCount[i];
        tileR[tile] += color.R;
        tileG[tile] += color.G;
        tileB[tile] += color.B;
    }

    private void AddObjectToOverviewSummary(int i, Color color, long[] overviewR, long[] overviewG, long[] overviewB)
    {
        var tx = (int)Math.Clamp((X[i] + StageWidth * 0.5f) / StageWidth * OverviewColumns, 0, OverviewColumns - 1);
        var ty = (int)Math.Clamp((Y[i] + StageHeight * 0.5f) / StageHeight * OverviewRows, 0, OverviewRows - 1);
        var tile = ty * OverviewColumns + tx;
        OverviewCount[tile]++;
        OverviewAtoms[tile] += AtomCount[i];
        overviewR[tile] += color.R;
        overviewG[tile] += color.G;
        overviewB[tile] += color.B;
    }

    private void FinalizeTileSummary(long[] tileR, long[] tileG, long[] tileB)
    {
        for (var i = 0; i < TileCount.Length; i++)
        {
            if (TileCount[i] == 0)
            {
                TileArgb[i] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                continue;
            }

            var count = TileCount[i];
            var alpha = Math.Clamp(44 + count * 9, 48, 230);
            TileArgb[i] = Color.FromArgb(alpha, (int)(tileR[i] / count), (int)(tileG[i] / count), (int)(tileB[i] / count)).ToArgb();
        }
    }

    private void FinalizeOverviewSummary(long[] overviewR, long[] overviewG, long[] overviewB)
    {
        for (var i = 0; i < OverviewCount.Length; i++)
        {
            if (OverviewCount[i] == 0)
            {
                OverviewArgb[i] = Color.FromArgb(24, 36, 40, 42).ToArgb();
                continue;
            }

            var count = OverviewCount[i];
            var alpha = Math.Clamp(58 + count * 6, 64, 230);
            OverviewArgb[i] = Color.FromArgb(alpha, (int)(overviewR[i] / count), (int)(overviewG[i] / count), (int)(overviewB[i] / count)).ToArgb();
        }
    }

    private void InitializeTimelineFromLayerExposure(int defaultDuration = AnimationTimeline.DefaultDuration)
    {
        defaultDuration = Math.Max(1, defaultDuration);
        using var batchUpdate = Timeline.BeginBatchUpdate();
        var additionalTargetIds = AdditionalTimelineTargetIds();
        var additionalTargetSet = additionalTargetIds.ToHashSet(StringComparer.Ordinal);
        var preservedAdditionalTracks = Timeline.CreateSnapshot().Tracks
            .Where(track => additionalTargetSet.Contains(track.TargetId))
            .ToDictionary(track => track.TargetId, track => track, StringComparer.Ordinal);

        Timeline.Clear();
        Timeline.SynchronizeTracks(LayerIds, defaultDuration, populateNewTracks: false);
        var occupied = new HashSet<(int Layer, int Frame)>();
        for (var index = 0; index < ObjectCount; index++)
        {
            occupied.Add((ObjectLayer[index], ObjectKeyframeFrame[index]));
        }

        for (var layer = 0; layer < LayerCount; layer++)
        {
            var track = Timeline.FindTrackByTargetId(LayerIds[layer]);
            if (track is null) continue;

            var startFrame = layer < LayerStart.Length
                ? Math.Clamp(LayerStart[layer], 0, track.Duration - 1)
                : 0;
            var endFrame = layer < LayerEnd.Length
                ? Math.Clamp(LayerEnd[layer], startFrame, track.Duration - 1)
                : track.Duration - 1;
            var hasContent = occupied.Contains((layer, startFrame));
            if (startFrame > 0) Timeline.InsertBlankKeyframe(track.Id, 0);
            if (hasContent) Timeline.InsertKeyframe(track.Id, startFrame);
            else if (startFrame == 0) Timeline.InsertBlankKeyframe(track.Id, startFrame);
            if (endFrame + 1 < track.Duration) Timeline.InsertBlankKeyframe(track.Id, endFrame + 1);
            RefreshLegacyExposureBounds(layer);
        }

        if (additionalTargetIds.Length == 0) return;

        var layerTracks = Timeline.CreateSnapshot().Tracks;
        var additionalTracks = additionalTargetIds
            .Select(targetId => preservedAdditionalTracks.GetValueOrDefault(targetId) ?? new AnimationTimelineTrackSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                TargetId = targetId,
                Duration = defaultDuration,
                Keyframes = [new TimelineKeyframe(0, TimelineKeyframeKind.Populated)]
            });
        Timeline.RestoreSnapshot(new AnimationTimelineSnapshot
        {
            Tracks = layerTracks.Concat(additionalTracks).ToArray()
        });
    }

    private static string[] CreateStableIds(int count, IEnumerable<string>? reservedIds = null)
    {
        var result = new string[Math.Max(0, count)];
        var used = reservedIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : reservedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            string id;
            do
            {
                id = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(id));
            result[i] = id;
        }

        return result;
    }

    private static string[] NormalizeStableIds(IReadOnlyList<string>? source, int count)
    {
        var result = new string[Math.Max(0, count)];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            var candidate = source is not null && i < source.Count ? source[i] : "";
            if (!string.IsNullOrWhiteSpace(candidate) && used.Add(candidate))
            {
                result[i] = candidate;
                continue;
            }

            string id;
            do
            {
                id = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(id));

            result[i] = id;
        }

        return result;
    }

    private static DrawingLayerKind[] NormalizeLayerKinds(IReadOnlyList<DrawingLayerKind>? source, int count)
    {
        var result = new DrawingLayerKind[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++)
        {
            result[index] = Enum.IsDefined(source[index]) ? source[index] : DrawingLayerKind.Drawing;
        }

        return result;
    }

    private static string[] NormalizeLayerParentIds(
        IReadOnlyList<string>? source,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<DrawingLayerKind> layerKinds)
    {
        var result = Enumerable.Repeat(string.Empty, layerIds.Count).ToArray();
        if (source is null) return result;
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var layer = 0; layer < result.Length && layer < source.Count; layer++)
        {
            var parentId = source[layer];
            if (string.IsNullOrWhiteSpace(parentId)
                || !indexes.TryGetValue(parentId, out var parent)
                || parent == layer
                || layerKinds[parent] != DrawingLayerKind.Folder)
            {
                continue;
            }

            var visited = new HashSet<int> { layer };
            var current = parent;
            var valid = true;
            while (true)
            {
                if (!visited.Add(current))
                {
                    valid = false;
                    break;
                }

                if (current >= source.Count || string.IsNullOrWhiteSpace(source[current])) break;
                if (!indexes.TryGetValue(source[current], out current) || layerKinds[current] != DrawingLayerKind.Folder)
                {
                    valid = false;
                    break;
                }
            }

            if (valid) result[layer] = parentId;
        }

        return result;
    }

    private static string[] NormalizeLayerMaskIds(
        IReadOnlyList<string>? source,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<DrawingLayerKind> layerKinds)
    {
        var result = Enumerable.Repeat(string.Empty, layerIds.Count).ToArray();
        if (source is null) return result;
        var claimedMaskIds = new HashSet<string>(StringComparer.Ordinal);
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var layer = 0; layer < result.Length && layer < source.Count; layer++)
        {
            var maskId = source[layer];
            if (layerKinds[layer] != DrawingLayerKind.Drawing
                || string.IsNullOrWhiteSpace(maskId)
                || !indexes.TryGetValue(maskId, out var maskLayer)
                || layerKinds[maskLayer] != DrawingLayerKind.Mask)
            {
                continue;
            }

            if (!claimedMaskIds.Add(maskId)) continue;
            result[layer] = maskId;
        }

        return result;
    }

    private static void NormalizeMaskedLayerParents(
        string[] layerParentIds,
        IReadOnlyList<string> layerIds,
        IReadOnlyList<string> layerMaskIds)
    {
        var indexes = layerIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        for (var contentLayer = 0; contentLayer < layerMaskIds.Count; contentLayer++)
        {
            var maskId = layerMaskIds[contentLayer];
            if (string.IsNullOrWhiteSpace(maskId)) continue;
            if (!indexes.TryGetValue(maskId, out var maskLayer) || maskLayer >= layerParentIds.Length) continue;
            layerParentIds[contentLayer] = layerParentIds[maskLayer];
        }
    }

    private static Color DefaultLayerColor(int index)
    {
        var colors = new[]
        {
            Color.FromArgb(79, 195, 247),
            Color.FromArgb(255, 183, 77),
            Color.FromArgb(129, 199, 132),
            Color.FromArgb(244, 143, 177),
            Color.FromArgb(179, 157, 219),
            Color.FromArgb(128, 203, 196),
            Color.FromArgb(255, 138, 128),
            Color.FromArgb(255, 241, 118)
        };
        return colors[Math.Abs(index) % colors.Length];
    }

    private static int[] NormalizeLayerColors(IReadOnlyList<int>? source, int count)
    {
        var result = new int[Math.Max(0, count)];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = source is not null && index < source.Count
                ? source[index]
                : DefaultLayerColor(index).ToArgb();
        }

        return result;
    }

    private static bool[] NormalizeLayerOnionSkin(IReadOnlyList<bool>? source, int count)
    {
        var result = new bool[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++) result[index] = source[index];
        return result;
    }

    private static bool[] NormalizeLayerLocked(IReadOnlyList<bool>? source, int count)
    {
        var result = new bool[Math.Max(0, count)];
        if (source is null) return result;
        for (var index = 0; index < result.Length && index < source.Count; index++) result[index] = source[index];
        return result;
    }

    private static int NormalizeOnionSkinFrames(int? source, int fallback)
    {
        return Math.Clamp(source ?? fallback, 0, MaximumOnionSkinFrames);
    }

    private static T[] ReorderLayers<T>(IReadOnlyList<T> source, IReadOnlyList<int> destinationBySource)
    {
        var result = new T[destinationBySource.Count];
        for (var sourceIndex = 0; sourceIndex < result.Length; sourceIndex++)
        {
            result[destinationBySource[sourceIndex]] = source[sourceIndex];
        }

        return result;
    }

    private static Color ColorFromHsl(double h, double s, double light)
    {
        var a = s * Math.Min(light, 1 - light);
        int F(double n)
        {
            var k = (n + h * 12) % 12;
            var channel = light - a * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1)));
            return (int)Math.Clamp(channel * 255, 0, 255);
        }

        return Color.FromArgb(F(0), F(8), F(4));
    }
}
