using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed class VectorScene : ITimelineContext
{
    private const float FillMergeDistanceUnits = 10f;
    private const float ConnectedStrokeEndpointToleranceUnits = 1.5f;
    private const double ClipperCoordinateScale = 1000d;
    private const double TopologyCutterHalfWidthClipper = 2d;
    private const int TileColumns = 384;
    private const int TileRows = 224;
    private const int OverviewColumns = 120;
    private const int OverviewRows = 70;
    private const int IndexColumns = 256;
    private const int IndexRows = 160;

    public int LayerCount { get; private set; }
    public int ObjectCount { get; private set; }
    public long VirtualAtomCount { get; private set; }
    public long GeometryRevision { get; private set; }
    private long _nextObjectOrder;
    private bool _synchronizingKeyframeContent;
    private Func<IReadOnlyList<string>>? _additionalTimelineTargets;
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
    public bool[] LayerVisible { get; private set; } = [];
    public float[] LayerOpacity { get; private set; } = [];
    public int[] LayerStart { get; private set; } = [];
    public int[] LayerEnd { get; private set; } = [];

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
    public ShapeKind[] ShapeKind { get; private set; } = [];
    public uint[] AtomCount { get; private set; } = [];
    public int[] Argb { get; private set; } = [];
    public int[] StrokeArgb { get; private set; } = [];
    private readonly Dictionary<int, PointF[][]> _pathLocalContours = new();
    private readonly Dictionary<int, PointF[]> _freehandLocalPoints = new();

    private readonly record struct MarqueePartAddition(
        bool IsLine,
        int Layer,
        PointF[] Points,
        PointF Start,
        PointF Control,
        PointF End,
        float Stroke,
        Color FillColor,
        Color StrokeColor,
        uint Atoms,
        bool Selected);

    private readonly record struct FillRegion(PointF[][] Contours, RectangleF Bounds, float Area);

    private readonly record struct PolylinePart(int PartIndex, float StartT, float EndT, PointF[] Points);

    private readonly record struct BoundaryStrokePart(int PartIndex, int ContourIndex, float StartT, float EndT, PointF[] Points);

    private readonly record struct ConnectedStrokePart(DrawingElementHit Hit, PointF Start, PointF End);

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
        PointF Start,
        PointF Control,
        PointF End,
        PointF[] Points,
        PointF[][] Contours);


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

    public void CreateEmpty(int layers = 1)
    {
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        ObjectCount = 0;
        VirtualAtomCount = 0;
        _nextObjectOrder = 0;
        ActiveLayer = 0;
        EditFrame = 0;
        MaxHalfExtent = 128;

        LayerIds = CreateStableIds(LayerCount, AdditionalTimelineTargetIds());
        LayerNames = new string[LayerCount];
        LayerVisible = new bool[LayerCount];
        LayerOpacity = new float[LayerCount];
        LayerStart = new int[LayerCount];
        LayerEnd = new int[LayerCount];
        for (var i = 0; i < LayerCount; i++)
        {
            LayerNames[i] = $"Layer {i:0000}";
            LayerVisible[i] = true;
            LayerOpacity[i] = 1.0f;
            LayerStart[i] = 0;
            LayerEnd[i] = AnimationTimeline.DefaultDuration - 1;
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
        ShapeKind = [];
        AtomCount = [];
        Argb = [];
        StrokeArgb = [];
        _pathLocalContours.Clear();
        _freehandLocalPoints.Clear();
        InitializeTimelineFromLayerExposure();
        ClearSummaries();
        RebuildSpatialIndex();
    }

    public void Generate(int layers, int objects, long atoms)
    {
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        ObjectCount = Math.Clamp(objects, 1, 1_000_000);
        VirtualAtomCount = Math.Max(atoms, ObjectCount * 3L);
        _nextObjectOrder = ObjectCount;
        ActiveLayer = 0;
        EditFrame = 0;
        MaxHalfExtent = 128;

        var rng = new Random(0x2D0A2026);
        LayerIds = CreateStableIds(LayerCount, AdditionalTimelineTargetIds());
        LayerNames = new string[LayerCount];
        LayerVisible = new bool[LayerCount];
        LayerOpacity = new float[LayerCount];
        LayerStart = new int[LayerCount];
        LayerEnd = new int[LayerCount];
        Array.Fill(LayerVisible, true);

        for (var i = 0; i < LayerCount; i++)
        {
            LayerNames[i] = $"Layer {i:0000}";
            LayerOpacity[i] = (float)(0.42 + rng.NextDouble() * 0.58);
            LayerStart[i] = rng.Next(0, 18);
            LayerEnd[i] = 180 + rng.Next(0, 60);
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
        ShapeKind = GC.AllocateUninitializedArray<ShapeKind>(ObjectCount);
        AtomCount = GC.AllocateUninitializedArray<uint>(ObjectCount);
        Argb = GC.AllocateUninitializedArray<int>(ObjectCount);
        StrokeArgb = GC.AllocateUninitializedArray<int>(ObjectCount);
        _pathLocalContours.Clear();
        _freehandLocalPoints.Clear();

        var avgAtoms = (double)VirtualAtomCount / ObjectCount;
        var columns = (int)Math.Ceiling(Math.Sqrt(ObjectCount * 1.7));
        var rows = (int)Math.Ceiling((double)ObjectCount / columns);
        var cellW = StageWidth / columns;
        var cellH = StageHeight / rows;

        ClearSummaries();
        var tileR = new long[TileCount.Length];
        var tileG = new long[TileCount.Length];
        var tileB = new long[TileCount.Length];
        var overviewR = new long[OverviewCount.Length];
        var overviewG = new long[OverviewCount.Length];
        var overviewB = new long[OverviewCount.Length];

        for (var i = 0; i < ObjectCount; i++)
        {
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
            ShapeKind[i] = RandomShapeKind(rng);
            if (ShapeKind[i] == VectorAnimationEngine.ShapeKind.Line) Height[i] = Math.Max(VectorUnits.FromPixels(3), Stroke[i] + VectorUnits.FromPixels(2));
            CurveControlX[i] = X[i];
            CurveControlY[i] = Y[i];
            AtomCount[i] = (uint)Math.Max(3, Math.Floor(avgAtoms * (0.18 + rng.NextDouble() * rng.NextDouble() * 2.35)));
            Argb[i] = color.ToArgb();
            StrokeArgb[i] = Color.FromArgb(235, 238, 242, 241).ToArgb();
            MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[i], Height[i]) * 0.5f);

            AddObjectToTileSummary(i, color, tileR, tileG, tileB);
            AddObjectToOverviewSummary(i, color, overviewR, overviewG, overviewB);
        }

        InitializeTimelineFromLayerExposure();
        FinalizeTileSummary(tileR, tileG, tileB);
        FinalizeOverviewSummary(overviewR, overviewG, overviewB);
        RebuildSpatialIndex();
    }

    public int AddObject(int layer, PointF center, SizeF size, float angle, float stroke, Color color, uint atoms, ShapeKind? shapeKind = null)
    {
        return AddObject(layer, center, size, angle, stroke, color, Color.FromArgb(238, 242, 241), atoms, shapeKind);
    }

    public int AddObject(int layer, PointF center, SizeF size, float angle, float stroke, Color color, Color strokeColor, uint atoms, ShapeKind? shapeKind = null)
    {
        var index = AppendObject(layer, center, size, angle, stroke, color, strokeColor, atoms, shapeKind);
        RebuildSpatialIndex();
        var summaryColor = ShapeKind[index] is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform
            ? strokeColor
            : color;
        AddObjectToSummariesIncremental(index, summaryColor);
        return index;
    }

    private int AppendObject(int layer, PointF center, SizeF size, float angle, float stroke, Color color, Color strokeColor, uint atoms, ShapeKind? shapeKind = null)
    {
        var index = ObjectCount;
        ObjectCount++;
        VirtualAtomCount += atoms;
        EnsureObjectCapacity(ObjectCount);

        var targetLayer = Math.Clamp(layer, 0, LayerCount - 1);
        ObjectLayer[index] = (ushort)targetLayer;
        ObjectKeyframeFrame[index] = EnsureWritableKeyframe(targetLayer, EditFrame);
        ObjectOrder[index] = ++_nextObjectOrder;
        ObjectSubOrder[index] = 0;
        X[index] = VectorUnits.Quantize(center.X);
        Y[index] = VectorUnits.Quantize(center.Y);
        Width[index] = Math.Max(1, VectorUnits.Quantize(size.Width));
        Height[index] = Math.Max(1, VectorUnits.Quantize(size.Height));
        Angle[index] = angle;
        Stroke[index] = Math.Max(0, stroke);
        ShapeKind[index] = shapeKind ?? InferShapeKind(size, atoms);
        CurveControlX[index] = X[index];
        CurveControlY[index] = Y[index];
        AtomCount[index] = Math.Max(3, atoms);
        Argb[index] = color.ToArgb();
        StrokeArgb[index] = strokeColor.ToArgb();
        MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[index], Height[index]) * 0.5f);
        return index;
    }

    public int AddLineSegment(int layer, PointF start, PointF end, float stroke, Color color, Color strokeColor, uint atoms)
    {
        return AddCurveSegment(layer, start, Midpoint(start, end), end, stroke, color, strokeColor, atoms);
    }

    public int AddCurveSegment(int layer, PointF start, PointF control, PointF end, float stroke, Color color, Color strokeColor, uint atoms)
    {
        var index = AppendCurveSegment(layer, start, control, end, stroke, color, strokeColor, atoms);
        RebuildSpatialIndex();
        AddObjectToSummariesIncremental(index, strokeColor);
        return index;
    }

    private int AppendCurveSegment(int layer, PointF start, PointF control, PointF end, float stroke, Color color, Color strokeColor, uint atoms)
    {
        var center = Midpoint(start, end);
        var width = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, Distance(start, end));
        var height = Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2));
        var index = AppendObject(layer, center, new SizeF(width, height), MathF.Atan2(end.Y - start.Y, end.X - start.X), stroke, color, strokeColor, atoms, VectorAnimationEngine.ShapeKind.Line);
        CurveControlX[index] = VectorUnits.Quantize(control.X);
        CurveControlY[index] = VectorUnits.Quantize(control.Y);
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

    private int AppendPathObjectContours(int layer, IReadOnlyList<PointF[]> worldContours, float stroke, Color color, Color strokeColor, uint atoms)
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
        RebuildSpatialIndex();
        AddObjectToSummariesIncremental(index, color);
        return index;
    }

    private int AppendFreehandStroke(int layer, PointF[] points, float stroke, Color color, uint atoms)
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

    public void UpdateFreehandStrokeWidth(int objectIndex, float stroke)
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
        RebuildGeometryIndex();
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
        RebuildGeometryIndex();
        RebuildSummaries();
        foreach (var (layer, frame) in affectedKeyframes) SynchronizeKeyframeContentKind(layer, frame);
        return removeCount;
    }

    public void RebuildGeometryIndex()
    {
        MaxHalfExtent = 128;
        for (var i = 0; i < ObjectCount; i++)
        {
            var bounds = GetObjectWorldBounds(i);
            var extent = Math.Max(
                Math.Max(Math.Abs(bounds.Left - X[i]), Math.Abs(bounds.Right - X[i])),
                Math.Max(Math.Abs(bounds.Top - Y[i]), Math.Abs(bounds.Bottom - Y[i])));
            MaxHalfExtent = Math.Max(MaxHalfExtent, extent);
        }

        RebuildSpatialIndex();
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
            LayerVisible = LayerVisible.ToArray(),
            LayerOpacity = LayerOpacity.ToArray(),
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
            ShapeKind = ShapeKind[..ObjectCount],
            AtomCount = AtomCount[..ObjectCount],
            Argb = Argb[..ObjectCount],
            StrokeArgb = StrokeArgb[..ObjectCount],
            Timeline = Timeline.CreateSnapshot(),
            PathLocalContours = _pathLocalContours.ToDictionary(item => item.Key, item => CloneContours(item.Value)),
            FreehandLocalPoints = _freehandLocalPoints.ToDictionary(item => item.Key, item => item.Value.ToArray())
        };
    }

    public void RestoreSnapshot(VectorSceneSnapshot snapshot)
    {
        LayerCount = snapshot.LayerCount;
        ObjectCount = snapshot.ObjectCount;
        VirtualAtomCount = snapshot.VirtualAtomCount;
        _nextObjectOrder = snapshot.NextObjectOrder;
        ActiveLayer = Math.Clamp(snapshot.ActiveLayer, 0, Math.Max(0, snapshot.LayerCount - 1));
        MaxHalfExtent = snapshot.MaxHalfExtent;
        LayerIds = NormalizeStableIds(snapshot.LayerIds, LayerCount);
        LayerNames = snapshot.LayerNames.ToArray();
        LayerVisible = snapshot.LayerVisible.ToArray();
        LayerOpacity = snapshot.LayerOpacity.ToArray();
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
        ShapeKind = snapshot.ShapeKind.ToArray();
        AtomCount = snapshot.AtomCount.ToArray();
        Argb = snapshot.Argb.ToArray();
        StrokeArgb = snapshot.StrokeArgb.ToArray();
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
            && LayerVisible[layer]
            && Timeline.EvaluateTargetExposure(LayerIds[layer], frame).HasContent;
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
        if ((uint)layer >= LayerCount || !LayerVisible[layer]) return false;

        var exposure = Timeline.EvaluateTargetExposure(LayerIds[layer], frame);
        return exposure.HasContent && ObjectKeyframeFrame[objectIndex] == exposure.SourceKeyframeFrame;
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

        if (frame >= track.Duration) Timeline.SetTrackDuration(track.Id, frame + 1);

        var inserted = sourceObjects.Length > 0
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
                    var hasContent = occupied.Contains((layer, keyframe.Frame));
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
            .Any(index => ObjectLayer[index] == layer && ObjectKeyframeFrame[index] == keyframeFrame);
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
    }

    public void ShowAllLayers() => Array.Fill(LayerVisible, true);

    public void ToggleLayer(int layer)
    {
        if (layer >= 0 && layer < LayerCount) LayerVisible[layer] = !LayerVisible[layer];
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

    public bool TryGetLineEndpoint(int objectIndex, bool startEndpoint, out PointF point)
    {
        point = PointF.Empty;
        if ((uint)objectIndex >= ObjectCount || ShapeKind[objectIndex] != VectorAnimationEngine.ShapeKind.Line) return false;
        var halfW = Width[objectIndex] * 0.5f;
        point = LocalToWorld(objectIndex, startEndpoint ? -halfW : halfW, 0);
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
                            segment.End));
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
                        regions[part].Contours));
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
        PointF end)
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
        PointF[][] contours)
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
            PointF.Empty,
            PointF.Empty,
            PointF.Empty,
            Array.Empty<PointF>(),
            contours);
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
        return oldToNew;
    }

    public (bool Changed, int[] SelectedObjects) MaterializeMarqueeParts(RectangleF worldBounds, int frame)
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
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                AddLineMarqueeParts(index, bounds, additions, remove);
            }
            else if (IsFreehandShape(shape))
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
                newIndex = AddCurveSegment(addition.Layer, addition.Start, addition.Control, addition.End, addition.Stroke, addition.FillColor, addition.StrokeColor, addition.Atoms);
            }
            else
            {
                newIndex = AddPathObject(addition.Layer, addition.Points, addition.Stroke, addition.FillColor, addition.StrokeColor, addition.Atoms);
            }

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
                        segment.End),
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
        if (!IsObjectActive(objectIndex, frame)) return objectIndex;

        var current = objectIndex;
        var merged = true;
        while (merged && (uint)current < ObjectCount)
        {
            merged = false;
            for (var other = 0; other < ObjectCount; other++)
            {
                if (other == current) continue;
                if (ObjectKeyframeFrame[other] != ObjectKeyframeFrame[current]) continue;
                if (!TryBuildSameColorFillMerge(current, other, connectNearby, out var mergedPath)) continue;

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
        var polygon = OpenPolygon(ShapeBoundary(index));
        if (polygon.Length < 3) return;

        var pieces = SplitPolygonByRect(polygon, bounds);
        if (pieces.Inside.Count < 3) return;

        var originalArea = Math.Abs(PolygonArea(polygon));
        var insideArea = Math.Abs(PolygonArea(pieces.Inside));
        if (originalArea < 0.5f || insideArea < 0.5f || insideArea >= originalArea - 0.5f) return;
        if (!SplitCoversSourceFill(polygon, pieces.Inside, pieces.Outside)) return;

        remove.Add(index);
        var layer = ObjectLayer[index];
        var fillColor = Color.FromArgb(Argb[index]);
        var strokeColor = Color.FromArgb(StrokeArgb[index]);
        var atoms = AtomCount[index];
        var splitCount = pieces.Outside.Count + 1;
        var atomsPerPart = Math.Max(3u, atoms / (uint)Math.Max(1, splitCount));

        if (Stroke[index] > 0)
        {
            foreach (var segment in BuildPolylineParts(ShapeBoundary(index), BoundarySplitParameters(index, activeCandidates)))
            {
                additions.Add(new MarqueePartAddition(true, layer, Array.Empty<PointF>(), segment.Start, Midpoint(segment.Start, segment.End), segment.End, Stroke[index], fillColor, strokeColor, atomsPerPart, false));
            }
        }

        foreach (var outside in pieces.Outside)
        {
            if (outside.Count < 3 || Math.Abs(PolygonArea(outside)) < 0.5f) continue;
            additions.Add(new MarqueePartAddition(false, layer, outside.ToArray(), PointF.Empty, PointF.Empty, PointF.Empty, 0, fillColor, strokeColor, atomsPerPart, false));
        }

        additions.Add(new MarqueePartAddition(false, layer, pieces.Inside.ToArray(), PointF.Empty, PointF.Empty, PointF.Empty, 0, fillColor, strokeColor, atomsPerPart, true));
    }

    private static bool SplitCoversSourceFill(PointF[] source, IReadOnlyList<PointF> inside, IReadOnlyList<List<PointF>> outside)
    {
        var totalArea = Math.Abs(PolygonArea(inside));
        foreach (var polygon in outside) totalArea += Math.Abs(PolygonArea(polygon));
        var sourceArea = Math.Abs(PolygonArea(source));
        if (Math.Abs(totalArea - sourceArea) > Math.Max(1f, sourceArea * 0.02f)) return false;

        foreach (var point in source)
        {
            if (!PointInSplitPieces(point, inside, outside)) return false;
        }

        for (var i = 0; i < source.Length; i++)
        {
            var midpoint = Midpoint(source[i], source[(i + 1) % source.Length]);
            if (!PointInSplitPieces(midpoint, inside, outside)) return false;
        }

        return true;
    }

    private static bool PointInSplitPieces(PointF point, IReadOnlyList<PointF> inside, IReadOnlyList<List<PointF>> outside)
    {
        if (inside.Count >= 3 && PointInPolygonOrOnBoundary(point, inside.ToArray())) return true;
        foreach (var polygon in outside)
        {
            if (polygon.Count >= 3 && PointInPolygonOrOnBoundary(point, polygon.ToArray())) return true;
        }

        return false;
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
        var atomsPerPart = Math.Max(3u, AtomCount[index] / (uint)Math.Max(1, segments.Count));
        var selectedCount = 0;

        foreach (var segment in segments)
        {
            var midpoint = QuadraticPoint(segment.Start, segment.Control, segment.End, 0.5f);
            var selected = bounds.Contains(midpoint);
            if (selected) selectedCount++;
            additions.Add(new MarqueePartAddition(true, layer, Array.Empty<PointF>(), segment.Start, segment.Control, segment.End, Stroke[index], fillColor, strokeColor, atomsPerPart, selected));
        }

        if (selectedCount == 0)
        {
            additions.RemoveRange(additions.Count - segments.Count, segments.Count);
            return;
        }

        remove.Add(index);
    }

    private bool TryBuildSameColorFillMerge(int a, int b, bool connectNearby, out PointF[][] mergedPath)
    {
        mergedPath = Array.Empty<PointF[]>();
        if ((uint)a >= ObjectCount || (uint)b >= ObjectCount) return false;
        if (ObjectLayer[a] != ObjectLayer[b] || Argb[a] != Argb[b]) return false;
        if (!IsFillShape(ShapeKind[a]) || !IsFillShape(ShapeKind[b])) return false;

        var mergeDistance = connectNearby ? FillMergeDistanceUnits : 0.001f;
        var mergeBounds = GetObjectWorldBounds(a);
        mergeBounds.Inflate(mergeDistance, mergeDistance);
        if (!mergeBounds.IntersectsWith(GetObjectWorldBounds(b))) return false;

        var contoursA = FillWorldContours(a);
        var contoursB = FillWorldContours(b);
        if (contoursA.Length == 0 || contoursB.Length == 0) return false;
        var distance = CompoundPolygonDistance(contoursA, contoursB, out var nearestA, out var nearestB);
        if (distance > mergeDistance) return false;

        var connector = connectNearby && distance > 0.001f ? CreateFillConnector(nearestA, nearestB) : Array.Empty<PointF>();
        mergedPath = BuildMergedFillPath(contoursA, contoursB, connector);
        return mergedPath.Length > 0;
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
        try
        {
            var subject = ToClipperPaths(contoursA);
            var clip = ToClipperPaths(contoursB);
            if (subject.Count == 0 || clip.Count == 0) return Array.Empty<PointF[]>();

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
                var index = AddCurveSegment(layer, segment.Start, segment.Control, segment.End, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
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
        var seen = new HashSet<int>();
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
                    if (!seen.Add(i)) continue;
                    if (!IsObjectActive(i, frame)) continue;
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
        var seen = new HashSet<int>();

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
                    if (!seen.Add(i)) continue;
                    if (!IsObjectActive(i, frame)) continue;
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

    private void RebuildSpatialIndex()
    {
        GeometryRevision++;
        var cellCount = IndexColumns * IndexRows;
        if (CellStart.Length != cellCount + 1) CellStart = new int[cellCount + 1];
        Array.Clear(CellStart);
        CellObjects = GC.AllocateUninitializedArray<int>(ObjectCount);

        for (var i = 0; i < ObjectCount; i++)
        {
            var cell = CellForWorld(X[i], Y[i]);
            if (cell >= 0) CellStart[cell + 1]++;
        }

        for (var i = 1; i < CellStart.Length; i++) CellStart[i] += CellStart[i - 1];

        var cursor = new int[cellCount];
        Array.Copy(CellStart, cursor, cellCount);
        for (var i = 0; i < ObjectCount; i++)
        {
            var cell = CellForWorld(X[i], Y[i]);
            if (cell >= 0) CellObjects[cursor[cell]++] = i;
        }
    }

    private void ClearSummaries()
    {
        Array.Clear(TileCount);
        Array.Clear(TileAtoms);
        Array.Clear(TileArgb);
        Array.Clear(OverviewCount);
        Array.Clear(OverviewAtoms);
        Array.Clear(OverviewArgb);
    }

    private void RebuildSummaries()
    {
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
            var color = Color.FromArgb(shape is VectorAnimationEngine.ShapeKind.Line or VectorAnimationEngine.ShapeKind.Freeform ? StrokeArgb[i] : Argb[i]);
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
        if (count >= 1) AddTopologySegmentIntersection(result, sourceStart, sourceEnd, first, includeSourceEndpoints);
        if (count >= 2) AddTopologySegmentIntersection(result, sourceStart, sourceEnd, second, includeSourceEndpoints);
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
            VectorAnimationEngine.ShapeKind.Polygon => RegularBoundary(6, halfW, halfH, -MathF.PI / 2),
            VectorAnimationEngine.ShapeKind.Star => StarBoundary(halfW, halfH),
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

    private static PointF[] StarBoundary(float halfW, float halfH)
    {
        var points = new PointF[11];
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? 1f : 0.46f;
            var angle = -MathF.PI / 2 + i * MathF.Tau / 10;
            points[i] = new PointF(MathF.Cos(angle) * halfW * radius, MathF.Sin(angle) * halfH * radius);
        }

        points[^1] = points[0];
        return points;
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

    private static List<PointF> ClipPolygonByLine(PointF[] polygon, PointF a, PointF b, bool keepPositive)
    {
        var result = new List<PointF>();
        if (polygon.Length < 3) return result;

        var previous = polygon[^1];
        var previousInside = IsInsideHalfPlane(previous, a, b, keepPositive);
        foreach (var current in polygon)
        {
            var currentInside = IsInsideHalfPlane(current, a, b, keepPositive);
            if (currentInside != previousInside && TryLineIntersection(previous, current, a, b, out var intersection))
            {
                result.Add(VectorUnits.Quantize(intersection));
            }

            if (currentInside) result.Add(VectorUnits.Quantize(current));
            previous = current;
            previousInside = currentInside;
        }

        return RemoveDuplicatePolygonPoints(result);
    }

    private static (List<PointF> Inside, List<List<PointF>> Outside) SplitPolygonByRect(PointF[] polygon, RectangleF bounds)
    {
        var left = ClipPolygonByAxis(polygon, vertical: true, bounds.Left, keepGreaterOrEqual: false);
        var afterLeft = ClipPolygonByAxis(polygon, vertical: true, bounds.Left, keepGreaterOrEqual: true);
        var right = ClipPolygonByAxis(afterLeft, vertical: true, bounds.Right, keepGreaterOrEqual: true);
        var middleX = ClipPolygonByAxis(afterLeft, vertical: true, bounds.Right, keepGreaterOrEqual: false);
        var top = ClipPolygonByAxis(middleX, vertical: false, bounds.Top, keepGreaterOrEqual: false);
        var afterTop = ClipPolygonByAxis(middleX, vertical: false, bounds.Top, keepGreaterOrEqual: true);
        var bottom = ClipPolygonByAxis(afterTop, vertical: false, bounds.Bottom, keepGreaterOrEqual: true);
        var inside = ClipPolygonByAxis(afterTop, vertical: false, bounds.Bottom, keepGreaterOrEqual: false);

        var outside = new List<List<PointF>>(4);
        AddPolygonIfUseful(outside, left);
        AddPolygonIfUseful(outside, right);
        AddPolygonIfUseful(outside, top);
        AddPolygonIfUseful(outside, bottom);
        return (inside, outside);
    }

    private static List<PointF> ClipPolygonByAxis(IReadOnlyList<PointF> polygon, bool vertical, float value, bool keepGreaterOrEqual)
    {
        var result = new List<PointF>();
        if (polygon.Count < 3) return result;

        var previous = polygon[^1];
        var previousInside = IsInsideAxis(previous, vertical, value, keepGreaterOrEqual);
        foreach (var current in polygon)
        {
            var currentInside = IsInsideAxis(current, vertical, value, keepGreaterOrEqual);
            if (currentInside != previousInside && TryAxisIntersection(previous, current, vertical, value, out var intersection))
            {
                result.Add(VectorUnits.Quantize(intersection));
            }

            if (currentInside) result.Add(VectorUnits.Quantize(current));
            previous = current;
            previousInside = currentInside;
        }

        return RemoveDuplicatePolygonPoints(result);
    }

    private static bool IsInsideAxis(PointF point, bool vertical, float value, bool keepGreaterOrEqual)
    {
        var coordinate = vertical ? point.X : point.Y;
        return keepGreaterOrEqual ? coordinate >= value - 0.001f : coordinate <= value + 0.001f;
    }

    private static bool TryAxisIntersection(PointF a, PointF b, bool vertical, float value, out PointF point)
    {
        point = PointF.Empty;
        var delta = vertical ? b.X - a.X : b.Y - a.Y;
        if (Math.Abs(delta) < 0.0001f) return false;
        var t = ((vertical ? value - a.X : value - a.Y) / delta);
        t = Math.Clamp(t, 0, 1);
        point = Lerp(a, b, t);
        return true;
    }

    private static void AddPolygonIfUseful(List<List<PointF>> polygons, List<PointF> polygon)
    {
        if (polygon.Count >= 3 && Math.Abs(PolygonArea(polygon)) >= 0.5f) polygons.Add(polygon);
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

    private static bool IsInsideHalfPlane(PointF point, PointF a, PointF b, bool keepPositive)
    {
        var side = SignedSide(point, a, b);
        return keepPositive ? side >= -0.001f : side <= 0.001f;
    }

    private static bool TryLineIntersection(PointF a, PointF b, PointF c, PointF d, out PointF point)
    {
        point = PointF.Empty;
        var rX = b.X - a.X;
        var rY = b.Y - a.Y;
        var sX = d.X - c.X;
        var sY = d.Y - c.Y;
        var denominator = rX * sY - rY * sX;
        if (Math.Abs(denominator) < 0.0001f) return false;

        var cax = c.X - a.X;
        var cay = c.Y - a.Y;
        var t = (cax * sY - cay * sX) / denominator;
        point = Lerp(a, b, Math.Clamp(t, 0, 1));
        return true;
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

    private static float SignedSide(PointF point, PointF a, PointF b)
    {
        return (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
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

    private void CopyObjectData(int from, int to)
    {
        ObjectLayer[to] = ObjectLayer[from];
        ObjectKeyframeFrame[to] = ObjectKeyframeFrame[from];
        ObjectOrder[to] = ObjectOrder[from];
        ObjectSubOrder[to] = ObjectSubOrder[from];
        X[to] = X[from];
        Y[to] = Y[from];
        Width[to] = Width[from];
        Height[to] = Height[from];
        Angle[to] = Angle[from];
        Stroke[to] = Stroke[from];
        CurveControlX[to] = CurveControlX[from];
        CurveControlY[to] = CurveControlY[from];
        ShapeKind[to] = ShapeKind[from];
        AtomCount[to] = AtomCount[from];
        Argb[to] = Argb[from];
        StrokeArgb[to] = StrokeArgb[from];
        if (_pathLocalContours.TryGetValue(from, out var contours))
        {
            _pathLocalContours[to] = CloneContours(contours);
        }
        else
        {
            _pathLocalContours.Remove(to);
        }

        if (_freehandLocalPoints.TryGetValue(from, out var freehandPoints))
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
        var shapeKind = ShapeKind;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
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
        Array.Resize(ref shapeKind, ObjectCount);
        Array.Resize(ref atomCount, ObjectCount);
        Array.Resize(ref argb, ObjectCount);
        Array.Resize(ref strokeArgb, ObjectCount);
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
        ShapeKind = shapeKind;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
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
        var shapeKind = ShapeKind;
        var atomCount = AtomCount;
        var argb = Argb;
        var strokeArgb = StrokeArgb;
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
        Array.Resize(ref shapeKind, capacity);
        Array.Resize(ref atomCount, capacity);
        Array.Resize(ref argb, capacity);
        Array.Resize(ref strokeArgb, capacity);
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
        ShapeKind = shapeKind;
        AtomCount = atomCount;
        Argb = argb;
        StrokeArgb = strokeArgb;
    }

    private static ShapeKind InferShapeKind(SizeF size, uint atoms)
    {
        if (atoms <= 8 || Math.Min(size.Width, size.Height) <= 4) return VectorAnimationEngine.ShapeKind.Line;
        return VectorAnimationEngine.ShapeKind.Rectangle;
    }

    private static ShapeKind RandomShapeKind(Random rng)
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

    private void InitializeTimelineFromLayerExposure()
    {
        var additionalTargetIds = AdditionalTimelineTargetIds();
        var additionalTargetSet = additionalTargetIds.ToHashSet(StringComparer.Ordinal);
        var preservedAdditionalTracks = Timeline.CreateSnapshot().Tracks
            .Where(track => additionalTargetSet.Contains(track.TargetId))
            .ToDictionary(track => track.TargetId, track => track, StringComparer.Ordinal);

        Timeline.Clear();
        Timeline.SynchronizeTracks(LayerIds, AnimationTimeline.DefaultDuration, populateNewTracks: false);
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
                Duration = AnimationTimeline.DefaultDuration,
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
