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
            X[i] = (float)(col * cellW - StageWidth * 0.5 + jitterX);
            Y[i] = (float)(row * cellH - StageHeight * 0.5 + jitterY);
            Width[i] = (float)(8 + sizeBias * (24 + rng.NextDouble() * 96));
            Height[i] = (float)(8 + sizeBias * (18 + rng.NextDouble() * 72));
            Angle[i] = (float)((rng.NextDouble() - 0.5) * 0.55);
            Stroke[i] = rng.NextDouble() > 0.28 ? (float)(1 + rng.NextDouble() * 3) : 0;
            ShapeKind[i] = RandomShapeKind(rng);
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
        X[index] = center.X;
        Y[index] = center.Y;
        Width[index] = Math.Max(1, size.Width);
        Height[index] = Math.Max(1, size.Height);
        Angle[index] = angle;
        Stroke[index] = Math.Max(0, stroke);
        ShapeKind[index] = shapeKind ?? InferShapeKind(size, atoms);
        CurveControlX[index] = center.X;
        CurveControlY[index] = center.Y;
        AtomCount[index] = Math.Max(3, atoms);
        Argb[index] = color.ToArgb();
        StrokeArgb[index] = strokeColor.ToArgb();
        MaxHalfExtent = Math.Max(MaxHalfExtent, Math.Max(Width[index], Height[index]) * 0.5f);
        RebuildSpatialIndex();
        return index;
    }

    public bool RemoveObjectAt(int index)
    {
        if ((uint)index >= ObjectCount) return false;

        VirtualAtomCount = Math.Max(0, VirtualAtomCount - AtomCount[index]);
        var last = ObjectCount - 1;
        if (index != last)
        {
            ObjectLayer[index] = ObjectLayer[last];
            X[index] = X[last];
            Y[index] = Y[last];
            Width[index] = Width[last];
            Height[index] = Height[last];
            Angle[index] = Angle[last];
            Stroke[index] = Stroke[last];
            CurveControlX[index] = CurveControlX[last];
            CurveControlY[index] = CurveControlY[last];
            ShapeKind[index] = ShapeKind[last];
            AtomCount[index] = AtomCount[last];
            Argb[index] = Argb[last];
            StrokeArgb[index] = StrokeArgb[last];
        }

        ObjectCount--;
        ResizeObjectArrays();
        RebuildGeometryIndex();
        RebuildSummaries();
        return true;
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
        GetHitTestRange(world, toleranceWorld, out var minX, out var maxX, out var minY, out var maxY);
        var best = -1;

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
                    if (i <= best) continue;
                    var layer = ObjectLayer[i];
                    if (!IsLayerActive(layer, frame)) continue;
                    if (HitObject(world, i, toleranceWorld)) best = i;
                }
            }
        }

        return best;
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
