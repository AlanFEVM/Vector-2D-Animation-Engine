namespace VectorAnimationEngine;

internal sealed class VectorScene
{
    private const int TileColumns = 384;
    private const int TileRows = 224;
    private const int OverviewColumns = 120;
    private const int OverviewRows = 70;
    private const int IndexColumns = 256;
    private const int IndexRows = 160;

    public int LayerCount { get; private set; }
    public int ObjectCount { get; private set; }
    public long VirtualAtomCount { get; private set; }
    public int ActiveLayer { get; set; }
    public int FrameCount { get; } = 240;
    public float StageWidth { get; } = 48000;
    public float StageHeight { get; } = 28000;
    public float MaxHalfExtent { get; private set; } = 128;

    public string[] LayerNames { get; private set; } = [];
    public bool[] LayerVisible { get; private set; } = [];
    public float[] LayerOpacity { get; private set; } = [];
    public int[] LayerStart { get; private set; } = [];
    public int[] LayerEnd { get; private set; } = [];

    public ushort[] ObjectLayer { get; private set; } = [];
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
    private readonly Dictionary<int, PointF[]> _pathLocalPoints = new();

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
        ActiveLayer = 0;
        MaxHalfExtent = 128;

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
            LayerEnd[i] = FrameCount - 1;
        }

        ObjectLayer = [];
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
        _pathLocalPoints.Clear();
        ClearSummaries();
        RebuildSpatialIndex();
    }

    public void Generate(int layers, int objects, long atoms)
    {
        LayerCount = Math.Clamp(layers, 1, ushort.MaxValue);
        ObjectCount = Math.Clamp(objects, 1, 1_000_000);
        VirtualAtomCount = Math.Max(atoms, ObjectCount * 3L);
        ActiveLayer = 0;
        MaxHalfExtent = 128;

        var rng = new Random(0x2D0A2026);
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
        _pathLocalPoints.Clear();

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
        var index = ObjectCount;
        ObjectCount++;
        VirtualAtomCount += atoms;
        ResizeObjectArrays();

        ObjectLayer[index] = (ushort)Math.Clamp(layer, 0, LayerCount - 1);
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
        RebuildSpatialIndex();
        return index;
    }

    public int AddLineSegment(int layer, PointF start, PointF end, float stroke, Color color, Color strokeColor, uint atoms)
    {
        return AddCurveSegment(layer, start, Midpoint(start, end), end, stroke, color, strokeColor, atoms);
    }

    public int AddCurveSegment(int layer, PointF start, PointF control, PointF end, float stroke, Color color, Color strokeColor, uint atoms)
    {
        var center = Midpoint(start, end);
        var width = Math.Max(DrawingTopologyRules.MinStrokeSegmentUnits, Distance(start, end));
        var height = Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2));
        var index = AddObject(layer, center, new SizeF(width, height), MathF.Atan2(end.Y - start.Y, end.X - start.X), stroke, color, strokeColor, atoms, VectorAnimationEngine.ShapeKind.Line);
        CurveControlX[index] = VectorUnits.Quantize(control.X);
        CurveControlY[index] = VectorUnits.Quantize(control.Y);
        return index;
    }

    public int AddPathObject(int layer, IReadOnlyList<PointF> worldPoints, float stroke, Color color, Color strokeColor, uint atoms)
    {
        if (worldPoints.Count < 3) return -1;

        var left = worldPoints[0].X;
        var right = worldPoints[0].X;
        var top = worldPoints[0].Y;
        var bottom = worldPoints[0].Y;
        for (var i = 1; i < worldPoints.Count; i++)
        {
            left = Math.Min(left, worldPoints[i].X);
            right = Math.Max(right, worldPoints[i].X);
            top = Math.Min(top, worldPoints[i].Y);
            bottom = Math.Max(bottom, worldPoints[i].Y);
        }

        var center = new PointF((left + right) * 0.5f, (top + bottom) * 0.5f);
        var index = AddObject(layer, center, new SizeF(Math.Max(1, right - left), Math.Max(1, bottom - top)), 0, stroke, color, strokeColor, atoms, VectorAnimationEngine.ShapeKind.Path);
        var local = new PointF[worldPoints.Count];
        for (var i = 0; i < worldPoints.Count; i++)
        {
            local[i] = new PointF(VectorUnits.Quantize(worldPoints[i].X - center.X), VectorUnits.Quantize(worldPoints[i].Y - center.Y));
        }

        _pathLocalPoints[index] = local;
        RebuildGeometryIndex();
        RebuildSummaries();
        return index;
    }

    public bool RemoveObjectAt(int index)
    {
        if ((uint)index >= ObjectCount) return false;

        VirtualAtomCount = Math.Max(0, VirtualAtomCount - AtomCount[index]);
        var last = ObjectCount - 1;
        if (index != last) CopyObjectData(last, index);

        ObjectCount--;
        ResizeObjectArrays();
        RemovePathDataOutsideObjectCount();
        RebuildGeometryIndex();
        RebuildSummaries();
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

        long removedAtoms = 0;
        var write = 0;
        for (var read = 0; read < ObjectCount; read++)
        {
            if (remove[read])
            {
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
        RebuildGeometryIndex();
        RebuildSummaries();
        return removeCount;
    }

    public void RebuildGeometryIndex()
    {
        MaxHalfExtent = 128;
        for (var i = 0; i < ObjectCount; i++)
        {
            MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[i], Height[i]) * 0.5f);
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
            ActiveLayer = ActiveLayer,
            MaxHalfExtent = MaxHalfExtent,
            LayerNames = LayerNames.ToArray(),
            LayerVisible = LayerVisible.ToArray(),
            LayerOpacity = LayerOpacity.ToArray(),
            LayerStart = LayerStart.ToArray(),
            LayerEnd = LayerEnd.ToArray(),
            ObjectLayer = ObjectLayer.ToArray(),
            X = X.ToArray(),
            Y = Y.ToArray(),
            Width = Width.ToArray(),
            Height = Height.ToArray(),
            Angle = Angle.ToArray(),
            Stroke = Stroke.ToArray(),
            CurveControlX = CurveControlX.ToArray(),
            CurveControlY = CurveControlY.ToArray(),
            ShapeKind = ShapeKind.ToArray(),
            AtomCount = AtomCount.ToArray(),
            Argb = Argb.ToArray(),
            StrokeArgb = StrokeArgb.ToArray(),
            PathLocalPoints = _pathLocalPoints.ToDictionary(item => item.Key, item => item.Value.ToArray())
        };
    }

    public void RestoreSnapshot(VectorSceneSnapshot snapshot)
    {
        LayerCount = snapshot.LayerCount;
        ObjectCount = snapshot.ObjectCount;
        VirtualAtomCount = snapshot.VirtualAtomCount;
        ActiveLayer = Math.Clamp(snapshot.ActiveLayer, 0, Math.Max(0, snapshot.LayerCount - 1));
        MaxHalfExtent = snapshot.MaxHalfExtent;
        LayerNames = snapshot.LayerNames.ToArray();
        LayerVisible = snapshot.LayerVisible.ToArray();
        LayerOpacity = snapshot.LayerOpacity.ToArray();
        LayerStart = snapshot.LayerStart.ToArray();
        LayerEnd = snapshot.LayerEnd.ToArray();
        ObjectLayer = snapshot.ObjectLayer.ToArray();
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
        _pathLocalPoints.Clear();
        foreach (var item in snapshot.PathLocalPoints)
        {
            if ((uint)item.Key >= ObjectCount) continue;
            _pathLocalPoints[item.Key] = item.Value.ToArray();
        }

        RebuildGeometryIndex();
        RebuildSummaries();
    }

    public bool IsLayerActive(int layer, int frame)
    {
        return layer >= 0 && layer < LayerCount && LayerVisible[layer] && frame >= LayerStart[layer] && frame <= LayerEnd[layer];
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
            var hit = HitElement(world, i, candidates, toleranceWorld);
            if (!hit.IsValid) continue;
            if (IsBetterHit(hit, best)) best = hit;
        }

        return best;
    }

    private static bool IsBetterHit(DrawingElementHit hit, DrawingElementHit best)
    {
        if (!best.IsValid) return true;

        var hitIsFill = hit.Key.Kind == DrawingElementKind.Fill;
        var bestIsFill = best.Key.Kind == DrawingElementKind.Fill;
        if (hitIsFill != bestIsFill) return !hitIsFill;
        if (hitIsFill) return hit.Key.ObjectIndex > best.Key.ObjectIndex;

        if (hit.Distance < best.Distance - 0.001f) return true;
        if (hit.Distance > best.Distance + 0.001f) return false;
        return hit.Key.ObjectIndex > best.Key.ObjectIndex;
    }

    public PointF[] GetShapeBoundary(int objectIndex)
    {
        return (uint)objectIndex < ObjectCount ? ShapeBoundary(objectIndex) : Array.Empty<PointF>();
    }

    public bool TryGetPathWorldPoints(int objectIndex, out PointF[] points)
    {
        if ((uint)objectIndex >= ObjectCount || !_pathLocalPoints.TryGetValue(objectIndex, out var local))
        {
            points = Array.Empty<PointF>();
            return false;
        }

        points = new PointF[local.Length];
        for (var i = 0; i < local.Length; i++) points[i] = LocalToWorld(objectIndex, local[i].X, local[i].Y);
        return true;
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
        var candidates = CollectActiveCandidates(frame);
        return hit.Key.Kind switch
        {
            DrawingElementKind.Stroke => DetachStrokePart(hit, candidates),
            DrawingElementKind.BoundaryStroke => DetachBoundaryStrokePart(hit, candidates),
            DrawingElementKind.Fill => DetachFillPart(hit, candidates),
            _ => hit
        };
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
            if ((uint)index >= ObjectCount || !IsLayerActive(ObjectLayer[index], frame)) continue;
            var shape = ShapeKind.Length > index ? ShapeKind[index] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                AddLineMarqueeParts(index, bounds, additions, remove);
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

    public int MergeSameColorFillsAround(int objectIndex)
    {
        if ((uint)objectIndex >= ObjectCount) return objectIndex;
        if (ShapeKind[objectIndex] == VectorAnimationEngine.ShapeKind.Line) return objectIndex;

        var current = objectIndex;
        var merged = true;
        while (merged && (uint)current < ObjectCount)
        {
            merged = false;
            for (var other = 0; other < ObjectCount; other++)
            {
                if (other == current) continue;
                if (!CanMergeSameColorFills(current, other, out var mergedBounds)) continue;

                var layer = ObjectLayer[current];
                var fillColor = Color.FromArgb(Argb[current]);
                var strokeColor = Color.FromArgb(StrokeArgb[current]);
                var atoms = Math.Max(3u, AtomCount[current] + AtomCount[other]);
                RemoveObjects(new[] { current, other });
                current = AddPathObject(layer, RectPoints(mergedBounds), 0, fillColor, strokeColor, atoms);
                merged = current >= 0;
                break;
            }
        }

        return current;
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

    private bool CanMergeSameColorFills(int a, int b, out RectangleF mergedBounds)
    {
        mergedBounds = RectangleF.Empty;
        if ((uint)a >= ObjectCount || (uint)b >= ObjectCount) return false;
        if (ObjectLayer[a] != ObjectLayer[b] || Argb[a] != Argb[b]) return false;
        if (ShapeKind[a] == VectorAnimationEngine.ShapeKind.Line || ShapeKind[b] == VectorAnimationEngine.ShapeKind.Line) return false;
        if (!TryGetAxisAlignedFillBounds(a, out var boundsA, out var areaA)) return false;
        if (!TryGetAxisAlignedFillBounds(b, out var boundsB, out var areaB)) return false;
        if (!RectsTouchOrOverlap(boundsA, boundsB)) return false;

        mergedBounds = RectangleF.FromLTRB(
            Math.Min(boundsA.Left, boundsB.Left),
            Math.Min(boundsA.Top, boundsB.Top),
            Math.Max(boundsA.Right, boundsB.Right),
            Math.Max(boundsA.Bottom, boundsB.Bottom));

        var intersection = RectangleF.Intersect(boundsA, boundsB);
        var intersectionArea = intersection.Width > 0 && intersection.Height > 0 ? intersection.Width * intersection.Height : 0;
        var unionArea = areaA + areaB - intersectionArea;
        var mergedArea = mergedBounds.Width * mergedBounds.Height;
        return Math.Abs(unionArea - mergedArea) <= 1.0f;
    }

    private bool TryGetAxisAlignedFillBounds(int index, out RectangleF bounds, out float area)
    {
        bounds = RectangleF.Empty;
        area = 0;
        var polygon = OpenPolygon(ShapeBoundary(index));
        if (polygon.Length < 3) return false;

        var left = polygon[0].X;
        var right = polygon[0].X;
        var top = polygon[0].Y;
        var bottom = polygon[0].Y;
        foreach (var point in polygon)
        {
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y);
            bottom = Math.Max(bottom, point.Y);
        }

        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        area = Math.Abs(PolygonArea(polygon));
        if (bounds.Width < 1 || bounds.Height < 1) return false;
        if (Math.Abs(area - bounds.Width * bounds.Height) > 1.0f) return false;

        foreach (var point in polygon)
        {
            var onVertical = Math.Abs(point.X - bounds.Left) <= 0.001f || Math.Abs(point.X - bounds.Right) <= 0.001f;
            var onHorizontal = Math.Abs(point.Y - bounds.Top) <= 0.001f || Math.Abs(point.Y - bounds.Bottom) <= 0.001f;
            if (!onVertical && !onHorizontal) return false;
        }

        return true;
    }

    private static bool RectsTouchOrOverlap(RectangleF a, RectangleF b)
    {
        return a.Left <= b.Right + 0.001f
            && a.Right + 0.001f >= b.Left
            && a.Top <= b.Bottom + 0.001f
            && a.Bottom + 0.001f >= b.Top;
    }

    private static PointF[] RectPoints(RectangleF bounds)
    {
        return
        [
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top),
            new PointF(bounds.Right, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom)
        ];
    }

    private List<int> CollectActiveCandidates(int frame)
    {
        var result = new List<int>(ObjectCount);
        for (var i = 0; i < ObjectCount; i++)
        {
            if (IsLayerActive(ObjectLayer[i], frame)) result.Add(i);
        }

        return result;
    }

    private DrawingElementHit DetachStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || ShapeKind[source] != VectorAnimationEngine.ShapeKind.Line) return hit;

        var curve = LineCurve(source);
        var splits = StrokeSplitParameters(source, candidates);
        if (splits.Count <= 2) return hit;

        var layer = ObjectLayer[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;
        var segments = BuildCurveParts(curve.Start, curve.Control, curve.End, splits);

        RemoveObjectAt(source);
        var selectedIndex = -1;
        foreach (var segment in segments)
        {
            var index = AddCurveSegment(layer, segment.Start, segment.Control, segment.End, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
            if (segment.PartIndex == selectedPart) selectedIndex = index;
        }

        if (selectedIndex < 0 && ObjectCount > 0) selectedIndex = ObjectCount - 1;
        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private DrawingElementHit DetachBoundaryStrokePart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount || Stroke[source] <= 0) return hit;

        var boundary = ShapeBoundary(source);
        var splits = BoundarySplitParameters(source, candidates);
        if (splits.Count <= 1) return hit;

        var layer = ObjectLayer[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        var selectedPart = hit.Key.PartIndex;
        var segments = BuildPolylineParts(boundary, splits);

        Stroke[source] = 0;
        var selectedIndex = -1;
        foreach (var segment in segments)
        {
            var index = AddLineSegment(layer, segment.Start, segment.End, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
            if (segment.PartIndex == selectedPart) selectedIndex = index;
        }

        RebuildGeometryIndex();
        RebuildSummaries();
        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Stroke, 0), -1, 0, 1)
            : hit;
    }

    private DrawingElementHit DetachFillPart(DrawingElementHit hit, IReadOnlyList<int> candidates)
    {
        var source = hit.Key.ObjectIndex;
        if ((uint)source >= ObjectCount) return hit;
        if (!TryFindPrimaryFillCutter(source, candidates, out var lineIndex, out var cutterStart, out var cutterEnd)) return hit;

        var polygon = ShapeBoundary(source);
        if (polygon.Length > 1 && SameDrawingUnit(polygon[0], polygon[^1])) polygon = polygon[..^1];
        var positive = ClipPolygonByLine(polygon, cutterStart, cutterEnd, keepPositive: true);
        var negative = ClipPolygonByLine(polygon, cutterStart, cutterEnd, keepPositive: false);
        if (positive.Count < 3 || negative.Count < 3) return hit;

        var layer = ObjectLayer[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var stroke = Stroke[source];
        var atoms = AtomCount[source];
        var selectedPositive = (hit.Key.PartIndex & 1) != 0;

        if (stroke > 0) MaterializeBoundaryStrokes(source, candidates);
        RemoveObjectAt(source);

        var negativeIndex = AddPathObject(layer, negative, 0, fillColor, strokeColor, Math.Max(3u, atoms / 2));
        var positiveIndex = AddPathObject(layer, positive, 0, fillColor, strokeColor, Math.Max(3u, atoms / 2));
        var selectedIndex = selectedPositive ? positiveIndex : negativeIndex;
        _ = lineIndex;
        return selectedIndex >= 0
            ? new DrawingElementHit(new DrawingElementKey(selectedIndex, DrawingElementKind.Fill, 0), -1, 0, 1)
            : DrawingElementHit.None;
    }

    private void MaterializeBoundaryStrokes(int source, IReadOnlyList<int> candidates)
    {
        var boundary = ShapeBoundary(source);
        var splits = BoundarySplitParameters(source, candidates);
        var segments = BuildPolylineParts(boundary, splits);
        var layer = ObjectLayer[source];
        var stroke = Stroke[source];
        var fillColor = Color.FromArgb(Argb[source]);
        var strokeColor = Color.FromArgb(StrokeArgb[source]);
        var atoms = AtomCount[source];
        foreach (var segment in segments)
        {
            AddLineSegment(layer, segment.Start, segment.End, stroke, fillColor, strokeColor, Math.Max(3u, atoms / (uint)Math.Max(1, segments.Count)));
        }
    }

    private bool TryFindPrimaryFillCutter(int fillIndex, IReadOnlyList<int> candidates, out int lineIndex, out PointF start, out PointF end)
    {
        var boundary = ShapeBoundary(fillIndex);
        foreach (var candidate in candidates)
        {
            if (candidate == fillIndex) continue;
            var shape = ShapeKind.Length > candidate ? ShapeKind[candidate] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape != VectorAnimationEngine.ShapeKind.Line) continue;

            var intersections = CollectCurvePolylineIntersections(CurveSamples(candidate), boundary);
            var distinct = 0;
            PointF? previous = null;
            foreach (var split in intersections.OrderBy(split => split.T))
            {
                var point = VectorUnits.Quantize(split.Point);
                if (previous is { } previousPoint && SameDrawingUnit(previousPoint, point)) continue;
                previous = point;
                distinct++;
            }

            if (distinct < 2) continue;
            var halfW = Width[candidate] * 0.5f;
            start = LocalToWorld(candidate, -halfW, 0);
            end = LocalToWorld(candidate, halfW, 0);
            lineIndex = candidate;
            return true;
        }

        lineIndex = -1;
        start = PointF.Empty;
        end = PointF.Empty;
        return false;
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
                    var layer = ObjectLayer[i];
                    if (!IsLayerActive(layer, frame)) continue;
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
                    var layer = ObjectLayer[i];
                    if (!IsLayerActive(layer, frame)) continue;
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
            var color = Color.FromArgb(Argb[i]);
            AddObjectToTileSummary(i, color, tileR, tileG, tileB);
            AddObjectToOverviewSummary(i, color, overviewR, overviewG, overviewB);
        }

        FinalizeTileSummary(tileR, tileG, tileB);
        FinalizeOverviewSummary(overviewR, overviewG, overviewB);
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

        if (shape == VectorAnimationEngine.ShapeKind.Path)
        {
            return TryGetPathWorldPoints(i, out var points) && PointInPolygon(world, points);
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

    private DrawingElementHit HitElement(PointF world, int i, IReadOnlyList<int> candidates, float toleranceWorld)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            return HitStrokeElement(world, i, candidates, toleranceWorld);
        }

        var boundaryHit = HitBoundaryStrokeElement(world, i, candidates, toleranceWorld);
        if (boundaryHit.IsValid) return boundaryHit;

        if (!HitObject(world, i, toleranceWorld)) return DrawingElementHit.None;
        if (!OwnsFillUnit(world, i, candidates)) return DrawingElementHit.None;
        var part = FillPartIndex(world, i, candidates);
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
            if (shape == VectorAnimationEngine.ShapeKind.Line) continue;
            if (!HitObject(sample, candidate, 0.5f)) continue;
            if (candidate > owner) owner = candidate;
        }

        return owner == fillIndex;
    }

    private DrawingElementHit HitStrokeElement(PointF world, int lineIndex, IReadOnlyList<int> candidates, float toleranceWorld)
    {
        var polyline = CurveSamples(lineIndex);
        var hitRadius = Math.Max(Height[lineIndex] * 0.5f, 1) + toleranceWorld;
        return HitCurvePart(world, lineIndex, DrawingElementKind.Stroke, polyline, StrokeSplitParameters(lineIndex, candidates), hitRadius);
    }

    private DrawingElementHit HitBoundaryStrokeElement(PointF world, int objectIndex, IReadOnlyList<int> candidates, float toleranceWorld)
    {
        if (Stroke[objectIndex] <= 0) return DrawingElementHit.None;
        var boundary = ShapeBoundary(objectIndex);
        var hitRadius = Math.Max(Stroke[objectIndex] * 0.5f, 1) + toleranceWorld;
        return HitPolylinePart(world, objectIndex, DrawingElementKind.BoundaryStroke, boundary, BoundarySplitParameters(objectIndex, candidates), hitRadius);
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

    private List<float> StrokeSplitParameters(int lineIndex, IReadOnlyList<int> candidates)
    {
        var splits = new List<DrawingTopologySplit>();
        var line = CurveSamples(lineIndex);
        splits.Add(new DrawingTopologySplit(0, line[0].Point));
        splits.Add(new DrawingTopologySplit(1, line[^1].Point));
        foreach (var other in candidates)
        {
            if (other == lineIndex) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape == VectorAnimationEngine.ShapeKind.Line)
            {
                AddCurveCurveIntersections(splits, line, CurveSamples(other), includeSourceEndpoints: false);
            }
            else
            {
                AddCurvePolylineIntersections(splits, line, ShapeBoundary(other), includeSourceEndpoints: false);
            }
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private List<float> BoundarySplitParameters(int objectIndex, IReadOnlyList<int> candidates)
    {
        var boundary = ShapeBoundary(objectIndex);
        var splits = new List<DrawingTopologySplit>();
        var segmentCount = Math.Max(1, boundary.Length - 1);
        for (var i = 0; i < boundary.Length; i++)
        {
            splits.Add(new DrawingTopologySplit(Math.Clamp(i / (float)segmentCount, 0, 1), boundary[i]));
        }

        foreach (var other in candidates)
        {
            if (other == objectIndex) continue;
            var shape = ShapeKind.Length > other ? ShapeKind[other] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape != VectorAnimationEngine.ShapeKind.Line) continue;
            AddPolylineCurveIntersections(splits, boundary, CurveSamples(other), includeSourceEndpoints: true);
        }

        return NormalizeStrokeSplits(splits).Select(split => split.T).ToList();
    }

    private List<DrawingTopologySplit> NormalizeStrokeSplits(List<DrawingTopologySplit> splits)
    {
        splits.Sort((a, b) => a.T.CompareTo(b.T));
        var unique = new List<DrawingTopologySplit>(splits.Count);
        foreach (var split in splits)
        {
            var quantized = VectorUnits.Quantize(split.Point);
            if (unique.Count > 0 && SameDrawingUnit(unique[^1].Point, quantized)) continue;
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
        var boundary = ShapeBoundary(fillIndex);
        var part = 0;
        var bit = 1;
        foreach (var lineIndex in candidates)
        {
            if (lineIndex == fillIndex) continue;
            var shape = ShapeKind.Length > lineIndex ? ShapeKind[lineIndex] : VectorAnimationEngine.ShapeKind.Rectangle;
            if (shape != VectorAnimationEngine.ShapeKind.Line) continue;

            var intersections = CollectCurvePolylineIntersections(CurveSamples(lineIndex), boundary);
            intersections.Sort((a, b) => a.T.CompareTo(b.T));
            var distinct = 0;
            PointF? previous = null;
            foreach (var split in intersections)
            {
                var point = VectorUnits.Quantize(split.Point);
                if (previous is { } previousPoint && SameDrawingUnit(previousPoint, point)) continue;
                previous = point;
                distinct++;
            }

            if (distinct < 2) continue;

            var halfW = Width[lineIndex] * 0.5f;
            var a = LocalToWorld(lineIndex, -halfW, 0);
            var b = LocalToWorld(lineIndex, halfW, 0);
            if (SignedSide(world, a, b) >= 0) part |= bit;
            bit <<= 1;
            if (bit >= 1 << 20) break;
        }

        return part;
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
                if (!TrySegmentIntersection(source[i].Point, source[i + 1].Point, cutter[j].Point, cutter[j + 1].Point, out var t)) continue;
                var globalT = source[i].T + (source[i + 1].T - source[i].T) * t;
                if (!includeSourceEndpoints && (globalT <= 0.0001f || globalT >= 0.9999f)) continue;
                var point = Lerp(source[i].Point, source[i + 1].Point, t);
                result.Add(new DrawingTopologySplit(globalT, point));
            }
        }
    }

    private void AddCurvePolylineIntersections(List<DrawingTopologySplit> result, CurveSample[] source, PointF[] cutter, bool includeSourceEndpoints)
    {
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                if (!TrySegmentIntersection(source[i].Point, source[i + 1].Point, cutter[j], cutter[j + 1], out var t)) continue;
                var globalT = source[i].T + (source[i + 1].T - source[i].T) * t;
                if (!includeSourceEndpoints && (globalT <= 0.0001f || globalT >= 0.9999f)) continue;
                var point = Lerp(source[i].Point, source[i + 1].Point, t);
                result.Add(new DrawingTopologySplit(globalT, point));
            }
        }
    }

    private void AddPolylineCurveIntersections(List<DrawingTopologySplit> result, PointF[] source, CurveSample[] cutter, bool includeSourceEndpoints)
    {
        var sourceSegments = Math.Max(1, source.Length - 1);
        for (var i = 0; i < source.Length - 1; i++)
        {
            for (var j = 0; j < cutter.Length - 1; j++)
            {
                if (!TrySegmentIntersection(source[i], source[i + 1], cutter[j].Point, cutter[j + 1].Point, out var t)) continue;
                var globalT = Math.Clamp((i + t) / sourceSegments, 0, 1);
                if (!includeSourceEndpoints && (globalT <= 0.0001f || globalT >= 0.9999f)) continue;
                var point = Lerp(source[i], source[i + 1], t);
                result.Add(new DrawingTopologySplit(globalT, point));
            }
        }
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
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldPoints(i, out var pathPoints))
        {
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
        t = 0;
        var rX = b.X - a.X;
        var rY = b.Y - a.Y;
        var sX = d.X - c.X;
        var sY = d.Y - c.Y;
        var denominator = rX * sY - rY * sX;
        if (Math.Abs(denominator) < 0.0001f) return false;

        var cax = c.X - a.X;
        var cay = c.Y - a.Y;
        t = (cax * sY - cay * sX) / denominator;
        var u = (cax * rY - cay * rX) / denominator;
        var intersects = t >= -DrawingTopologyRules.UnitIntersectionTolerance
            && t <= 1 + DrawingTopologyRules.UnitIntersectionTolerance
            && u >= -DrawingTopologyRules.UnitIntersectionTolerance
            && u <= 1 + DrawingTopologyRules.UnitIntersectionTolerance;
        if (intersects) t = Math.Clamp(t, 0, 1);
        return intersects;
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
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area * 0.5f;
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

    private RectangleF GetObjectWorldBounds(int i)
    {
        var shape = ShapeKind.Length > i ? ShapeKind[i] : VectorAnimationEngine.ShapeKind.Rectangle;
        var margin = Math.Max(Stroke[i] * 0.5f, 1);
        if (shape == VectorAnimationEngine.ShapeKind.Path && TryGetPathWorldPoints(i, out var pathPoints) && pathPoints.Length > 0)
        {
            var left = pathPoints[0].X;
            var right = pathPoints[0].X;
            var top = pathPoints[0].Y;
            var bottom = pathPoints[0].Y;
            for (var p = 1; p < pathPoints.Length; p++)
            {
                left = Math.Min(left, pathPoints[p].X);
                right = Math.Max(right, pathPoints[p].X);
                top = Math.Min(top, pathPoints[p].Y);
                bottom = Math.Max(bottom, pathPoints[p].Y);
            }

            return RectangleF.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
        }

        if (shape == VectorAnimationEngine.ShapeKind.Line)
        {
            var halfW = Width[i] * 0.5f;
            var start = LocalToWorld(i, -halfW, 0);
            var end = LocalToWorld(i, halfW, 0);
            var control = new PointF(CurveControlX[i], CurveControlY[i]);
            var left = Math.Min(Math.Min(start.X, end.X), control.X) - margin;
            var right = Math.Max(Math.Max(start.X, end.X), control.X) + margin;
            var top = Math.Min(Math.Min(start.Y, end.Y), control.Y) - margin;
            var bottom = Math.Max(Math.Max(start.Y, end.Y), control.Y) + margin;
            return RectangleF.FromLTRB(left, top, right, bottom);
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

    private static PointF QuadraticPoint(PointF start, PointF control, PointF end, float t)
    {
        var inv = 1 - t;
        return new PointF(
            inv * inv * start.X + 2 * inv * t * control.X + t * t * end.X,
            inv * inv * start.Y + 2 * inv * t * control.Y + t * t * end.Y);
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

    private void CopyObjectData(int from, int to)
    {
        ObjectLayer[to] = ObjectLayer[from];
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
        if (_pathLocalPoints.TryGetValue(from, out var points))
        {
            _pathLocalPoints[to] = points.ToArray();
        }
        else
        {
            _pathLocalPoints.Remove(to);
        }
    }

    private void RemovePathDataOutsideObjectCount()
    {
        foreach (var index in _pathLocalPoints.Keys.Where(index => index >= ObjectCount).ToArray())
        {
            _pathLocalPoints.Remove(index);
        }
    }

    private void ResizeObjectArrays()
    {
        var objectLayer = ObjectLayer;
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
