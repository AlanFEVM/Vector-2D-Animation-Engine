using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal readonly record struct LineAnchorSplitResult(
    int FirstObjectIndex,
    int SecondObjectIndex,
    PointF Anchor);

internal readonly record struct ConnectedLineBranchResult(
    int BranchObjectIndex,
    int[] SourceObjectIndices,
    PointF Anchor);

internal sealed record MarqueeMaterializationResult(
    bool Success,
    bool Changed,
    int[] SelectedObjects,
    int[] OldToNewObjectIndex);

internal sealed partial class VectorScene : ITimelineContext
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
    private const float BezierCoincidenceToleranceUnits = 0.75f;
    private const float BooleanBezierMaximumErrorUnits = 2f;
    private const float BooleanBezierSimplificationToleranceUnits = 0.75f;
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
    private const int MaximumTopologyQueryCacheEntries = 64;
    private const int MaximumGradientPathCoverageSamples = 192;
    private const int MaximumObjectDistortions = 256;
    public const int DefaultOnionSkinPreviousFrames = 2;

    /// <summary>
    /// Virtual atom cost charged for a placed bitmap object. Matches the imported-SVG
    /// cost so dense-scene LOD accounting treats raster objects comparably.
    /// </summary>
    public const uint BitmapObjectAtomCount = 64;
    public const int DefaultOnionSkinNextFrames = 2;
    public const int MaximumOnionSkinFrames = 8;
    private const int OnionSkinPreviousTintArgb = unchecked((int)0xffe0867e);
    private const int OnionSkinNextTintArgb = unchecked((int)0xff6fc3da);

    public int LayerCount { get; private set; }
    public int ObjectCount { get; private set; }
    public long VirtualAtomCount { get; private set; }
    public long GeometryRevision { get; private set; }
    public long SummaryRevision { get; private set; }
    internal long ActiveContentRevision { get; private set; }
    internal long GeometryIndexBuildCount { get; private set; }
    internal double LastFillMergePlanMilliseconds { get; private set; }
    internal double LastFillMergeSnapshotMilliseconds { get; private set; }
    internal double LastFillMergeMutationMilliseconds { get; private set; }
    internal double LastFillMergeSpatialMilliseconds { get; private set; }
    internal double LastFillMergeSummaryMilliseconds { get; private set; }
    internal double LastFillMergeTotalMilliseconds { get; private set; }
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
    public LayerBlendMode[] LayerBlendModes { get; private set; } = [];
    public int[] LayerColorArgb { get; private set; } = [];
    public bool[] LayerOutline { get; private set; } = [];
    public bool OnionSkinEnabled { get; private set; }
    public int OnionSkinPreviousFrames { get; private set; } = DefaultOnionSkinPreviousFrames;
    public int OnionSkinNextFrames { get; private set; } = DefaultOnionSkinNextFrames;
    public bool HasOnionSkinPreviewEnabled
    {
        get
        {
            return OnionSkinEnabled
                && (OnionSkinPreviousFrames > 0 || OnionSkinNextFrames > 0)
                && HasEligibleOnionSkinLayer();
        }
    }
    public int[] LayerStart { get; private set; } = [];
    public int[] LayerEnd { get; private set; } = [];
    public bool HasLayerEffects => LayerKinds.Any(kind => kind != DrawingLayerKind.Drawing)
        || LayerMaskIds.Any(id => !string.IsNullOrWhiteSpace(id))
        || HasNonNormalLayerBlendModes;
    public bool HasNonNormalLayerBlendModes => LayerBlendModes.Any(mode => mode != LayerBlendMode.Normal);
    public bool HasLayerOutline => LayerOutline.Any(outline => outline);
    public bool HasDisplayLayerEffects => HasLayerEffects || HasLayerOutline;

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
    public float[] CurveControl2X { get; private set; } = [];
    public float[] CurveControl2Y { get; private set; } = [];
    public LineEndpointStyle[] LineEndpointStyles { get; private set; } = [];
    public LineEndpointStyle[] LineEndEndpointStyles { get; private set; } = [];
    public ShapeKind[] ShapeKind { get; private set; } = [];
    public int[] ShapeVertexCounts { get; private set; } = [];
    public uint[] AtomCount { get; private set; } = [];
    public int[] Argb { get; private set; } = [];
    public int[] StrokeArgb { get; private set; } = [];
    public bool[] FillAutoMergeProtected { get; private set; } = [];
    public bool[] FillBoundaryLinkDetached { get; private set; } = [];
    public bool[] QuadraticLineEditing { get; private set; } = [];
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
    private readonly Dictionary<int, PathBezierNode[][]> _pathBezierLocalContours = new();
    // Object indices whose bezier path contours are rendered as open (unclosed) figures,
    // e.g. a segment that was pulled out of a fill boundary. Closed-only consumers (boolean
    // geometry, topology links) still treat the contour data as closed; only rendering honors this.
    private readonly HashSet<int> _openPathObjects = new();
    // Boundary segments (global bezier contour part indices) whose stroke is suppressed
    // because the segment was pulled out of a closed fill boundary. The contour itself
    // stays closed and untouched so the fill keeps the pre-detach outline (including any
    // bend) and any number of gaps can coexist; only stroke rendering honors this.
    private readonly Dictionary<int, HashSet<int>> _hiddenBoundaryStrokeParts = new();
    private readonly Dictionary<int, PointF[]> _freehandLocalPoints = new();
    private readonly Dictionary<int, PathBezierNode[]> _freehandBezierLocalNodes = new();
    private readonly Dictionary<int, (PointF[] SourcePoints, PathBezierNode[] Nodes)> _legacyFreehandBezierNodeCache = new();
    private readonly Dictionary<int, MixingBrushTrajectorySample[]> _mixingStrokeLocalSamples = new();
    private readonly Dictionary<int, MixingBrushRegionData> _mixingStrokeLocalRegions = new();
    private readonly Dictionary<int, string> _importedSvgSources = new();
    private readonly Dictionary<int, string> _importedSvgNames = new();
    // Placed bitmap objects point at a managed image asset; the pixels are stored once
    // per asset rather than once per placed object.
    private readonly Dictionary<int, BitmapObjectData> _bitmapObjects = new();
    private readonly Dictionary<int, TextObjectData> _textObjects = new();
    // Distortion is an object-level display transform. Keep source geometry intact and
    // apply this ordered stack only when producing display/query coordinates.
    private readonly Dictionary<int, DistortWarp[]> _objectDistortions = new();
    private readonly Dictionary<(int ObjectIndex, int Frame), FillPartition> _fillPartitionCache = new();
    private readonly Dictionary<
        (int ObjectIndex, int Frame, int FillPartIndex, bool IncludeCoincidentStrokes),
        FillBezierSegmentPiece[]> _exposedFillBezierCache = new();
    private readonly Dictionary<(int ObjectIndex, int Frame), int[]> _topologyCandidateCache = new();
    private readonly Dictionary<(int ObjectIndex, int Frame), StrokeSplitCacheEntry> _hitStrokeSplitCache = new();
    private readonly Dictionary<(int ObjectIndex, int Frame), BoundaryStrokePart[]> _hitBoundaryPartCache = new();
    private long _fillPartitionCacheRevision = -1;
    private long _exposedFillBezierCacheRevision = -1;
    private long _interactiveQueryCacheRevision = -1;

    internal bool IsPathOpen(int objectIndex) => _openPathObjects.Contains(objectIndex);
    internal void SetPathOpen(int objectIndex) => _openPathObjects.Add(objectIndex);
    internal void ClearPathOpen(int objectIndex) => _openPathObjects.Remove(objectIndex);

    internal bool IsBoundaryStrokePartHidden(int objectIndex, int partIndex)
    {
        // Stroke-suppression view: hidden-segment bookkeeping only means something while
        // the object still has a stroke to suppress. On a stroke-less fill it must not
        // lock the segment out of fill-boundary editing (bending, overlay handles).
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return false;
        return _hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var parts)
            && parts.Contains(partIndex);
    }

    /// <summary>
    /// Detached-segment bookkeeping, independent of the current stroke state: a boundary
    /// segment that was pulled out stays detached forever, so it can never be detached
    /// twice (which would spawn duplicate lines). Used to guard the detach entries.
    /// </summary>
    internal bool IsBoundarySegmentDetached(int objectIndex, int partIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return false;
        return _hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var parts)
            && parts.Contains(partIndex);
    }

    internal void HideBoundaryStrokePart(int objectIndex, int partIndex)
    {
        if (!_hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var parts))
        {
            parts = new HashSet<int>();
            _hiddenBoundaryStrokeParts[objectIndex] = parts;
        }

        parts.Add(partIndex);
    }

    internal IReadOnlySet<int>? GetHiddenBoundaryStrokeParts(int objectIndex)
    {
        // Same rule as IsBoundaryStrokePartHidden: a stroke-less object has nothing to
        // hide, so callers (renderers, materialization, editing) must see no hidden parts.
        if ((uint)objectIndex >= ObjectCount || !HasStroke(objectIndex)) return null;
        return _hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var parts) && parts.Count > 0
            ? parts
            : null;
    }

    internal void ClearHiddenBoundaryStrokeParts(int objectIndex)
    {
        _hiddenBoundaryStrokeParts.Remove(objectIndex);
    }

    private void ClearHiddenBoundaryStrokePartsBelow(int objectCount)
    {
        foreach (var index in _hiddenBoundaryStrokeParts.Keys.Where(index => index >= objectCount).ToArray())
        {
            _hiddenBoundaryStrokeParts.Remove(index);
        }
    }

    /// <summary>
    /// Maps a hit-test boundary part (whose PartIndex is a split-sequence number) back to
    /// its global bezier contour segment index and reports whether that segment is hidden.
    /// </summary>
    private bool IsBoundaryPartHitHidden(int objectIndex, BoundaryStrokePart part)
    {
        if (!_hiddenBoundaryStrokeParts.TryGetValue(objectIndex, out var hidden) || hidden.Count == 0) return false;
        if (!TryGetPathBezierWorldContours(objectIndex, out var contours)) return false;

        var offset = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contourLength = contours[contourIndex].Length;
            if (contourIndex == part.ContourIndex)
            {
                if (contourLength == 0) return false;
                var segmentIndex = Math.Min(
                    (int)MathF.Floor((part.StartT * contourLength) + 0.001f),
                    contourLength - 1);
                return hidden.Contains(offset + segmentIndex);
            }

            offset += contourLength;
        }

        return false;
    }

    /// <summary>
    /// Resolves a global bezier contour segment index back to a selectable BoundaryStroke
    /// hit (whose PartIndex is a split-sequence number) by matching the split parts that
    /// cover that segment's global parameter range.
    /// </summary>
    internal bool TryGetBoundaryStrokeHitForPart(
        int objectIndex,
        int partIndex,
        int frame,
        out DrawingElementHit hit)
    {
        hit = DrawingElementHit.None;
        if ((uint)objectIndex >= ObjectCount
            || !TryGetPathBezierWorldContours(objectIndex, out var contours))
        {
            return false;
        }

        var offset = 0;
        var targetContourIndex = -1;
        var targetSegmentIndex = -1;
        var targetContourLength = 0;
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contourLength = contours[contourIndex].Length;
            if (partIndex >= offset && partIndex < offset + contourLength)
            {
                targetContourIndex = contourIndex;
                targetSegmentIndex = partIndex - offset;
                targetContourLength = contourLength;
                break;
            }

            offset += contourLength;
        }

        if (targetContourIndex < 0 || targetContourLength < 2) return false;

        var startT = (float)targetSegmentIndex / targetContourLength;
        var endT = (float)(targetSegmentIndex + 1) / targetContourLength;
        const float rangeEpsilon = 1e-4f;
        var candidates = CollectTopologyCandidates(objectIndex, frame);
        foreach (var part in BuildBoundaryStrokeParts(objectIndex, candidates))
        {
            if (part.ContourIndex != targetContourIndex) continue;
            if (part.StartT < startT - rangeEpsilon || part.EndT > endT + rangeEpsilon) continue;
            hit = new DrawingElementHit(
                new DrawingElementKey(objectIndex, DrawingElementKind.BoundaryStroke, part.PartIndex),
                0f,
                part.StartT,
                part.EndT);
            return true;
        }

        return false;
    }

    internal sealed class TransformSession
    {
        internal TransformSession(
            VectorScene owner,
            VectorScene source,
            int[] objectIndices,
            FillBoundaryLineLink[] linkedBoundaries)
        {
            Owner = owner;
            Source = source;
            ObjectIndices = objectIndices;
            LinkedBoundaries = linkedBoundaries;
        }

        internal VectorScene Owner { get; }
        internal VectorScene Source { get; }
        internal int[] ObjectIndices { get; }
        internal FillBoundaryLineLink[] LinkedBoundaries { get; }
    }

    private readonly record struct FillRegion(PointF[][] Contours, RectangleF Bounds, float Area);

    private readonly record struct FillPartition(
        List<FillRegion> Regions,
        PreparedBezierCurve[] Curves);

    private readonly record struct StrokeSplitCacheEntry(
        DrawingTopologySplit[] Splits,
        float[] Parameters);

    private readonly record struct PolylinePart(int PartIndex, float StartT, float EndT, PointF[] Points);

    private readonly record struct OpenBezierSample(
        int SegmentIndex,
        float LocalT,
        PointF Point);

    private readonly record struct OpenBezierCut(
        int SegmentIndex,
        float LocalT,
        PointF Point)
    {
        public float PathT => SegmentIndex + LocalT;
    }

    private readonly record struct OpenBezierRun(
        int PartIndex,
        OpenBezierCut Start,
        OpenBezierCut End,
        PathBezierNode[] Nodes);

    private readonly record struct MarqueeOpenBezierInterval(
        OpenBezierCut Start,
        OpenBezierCut End,
        bool Selected);

    private readonly record struct BezierContourSegment(
        CubicBoundarySegment Curve,
        bool PreservesSourceCurve);

    private readonly record struct MarqueeBezierCurvePiece(
        CubicBoundarySegment Curve,
        PointF[] Samples);

    private readonly record struct PreparedBezierCurve(
        CubicBoundarySegment Curve,
        CurveSample[] Samples,
        RectangleF Bounds);

    private readonly record struct BoundaryStrokePart(
        int PartIndex,
        int ContourIndex,
        float StartT,
        float EndT,
        PointF[] Points,
        CubicBoundarySegment? Curve = null);

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
        bool QuadraticEditing,
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
        LineEndpointStyle EndEndpointStyle,
        bool QuadraticEditing);

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
        PointF Control1,
        PointF Control2,
        PointF End,
        PointF[] Points,
        PointF[][] Contours)
    {
        public GradientPaintData? GradientPaint { get; init; }
        public bool QuadraticEditing { get; init; }
        public bool FillAutoMergeProtected { get; init; }
        public PathBezierNode[][] BezierContours { get; init; } = [];
        public PathBezierNode[] OpenBezierNodes { get; init; } = [];
    }

    private readonly record struct MixingMaterializedPartAddition(
        DrawingElementKey SourceKey,
        int Layer,
        int KeyframeFrame,
        long Order,
        double SubOrder,
        uint Atoms,
        MixingBrushRegionData Region);

    private readonly record struct MarqueeMaterializedAddition(
        MaterializedPartAddition Addition,
        int KeyframeFrame,
        bool Selected);

    private readonly record struct MarqueeMixingMaterializedAddition(
        int Layer,
        int KeyframeFrame,
        long Order,
        double SubOrder,
        uint Atoms,
        MixingBrushRegionData Region,
        bool Selected);

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
        IReadOnlyList<MaterializedPartAddition> Replacements)
    {
        public IReadOnlyList<MixingStrokeEraserReplacement> MixingStrokeReplacements { get; init; } = [];
    }

    private readonly record struct MixingStrokeEraserReplacement(
        int Layer,
        long Order,
        double SubOrder,
        uint Atoms,
        MixingBrushTrajectorySample[] Samples)
    {
        public MixingBrushRegionData? Region { get; init; }
    }

    private sealed record FillOverwritePlan(
        int Source,
        int Layer,
        int KeyframeFrame,
        long Order,
        int FillArgb,
        bool FillAutoMergeProtected,
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
}
