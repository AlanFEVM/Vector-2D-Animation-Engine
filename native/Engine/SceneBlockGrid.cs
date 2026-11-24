namespace VectorAnimationEngine;

internal sealed record BlockGridMaterial(string Name, string? AssetId, bool Occludes)
{
    public int LightEmission { get; init; }
    public int LightFilter { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? BiomeTintArgb { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BiomeTintGrassSide { get; init; }
    public override string ToString() => Name;
}

internal sealed class SceneBlockGridSnapshot
{
    public int Columns { get; init; }
    public int Rows { get; init; }
    public int CellPixels { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SectionDepth { get; init; }
    public Dictionary<int, string> Layers { get; init; } = [];
    public BlockGridMaterial[] Palette { get; init; } = [];
    public string[] Shadows { get; init; } = [];
    public bool ProceduralLighting { get; init; }
    public int LightDepth { get; init; }
    public SceneBlockLightQuality LightQuality { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool NightVision { get; init; }
    public Dictionary<long, int> LightPoints { get; init; } = [];
    public long[] Positions { get; init; } = [];
    public int[] Materials { get; init; } = [];
}

internal sealed class SceneBlockGrid
{
    private readonly Dictionary<int, SortedList<int, int>> _columns = [];
    private readonly Dictionary<long, int> _lights;
    private SceneBlockLightVolume? _lighting;
    private readonly int _area;
    internal readonly record struct Cell(int X, int Y, int Depth);

    internal SceneBlockGrid(SceneBlockGridSnapshot snapshot)
    {
        if (snapshot.CellPixels is < 1 or > 512 || snapshot.Columns < 1 || snapshot.Rows < 1
            || (long)snapshot.Columns * snapshot.CellPixels > 32768 || (long)snapshot.Rows * snapshot.CellPixels > 32768
            || snapshot.SectionDepth is < 0 or > SceneBlockChunk.MaxSectionDepth
            || snapshot.Layers is not { Count: > 0 and <= SceneBlockChunk.MaxSectionDepth }
            || snapshot.Layers.Any(pair => pair.Key is < 0 or > 64_000_000 || !ValidId(pair.Value))
            || !ValidLayers(snapshot)
            || snapshot.Palette is not { Length: > 0 and <= 32768 }
            || snapshot.Palette.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256
                || item.LightEmission is < 0 or > 15 || item.LightFilter is < 0 or > 7
                || (item.BiomeTintArgb is { } tint ? (uint)tint >> 24 != 0xff : item.BiomeTintGrassSide)
                || item.AssetId is not null && !ValidId(item.AssetId))
            || snapshot.Shadows is not { Length: 0 or 3 } || snapshot.Shadows.Any(id => !ValidId(id))
            || snapshot.ProceduralLighting && snapshot.Shadows.Length != 0
            || snapshot.LightDepth < 0 || (snapshot.LightDepth > 0 && (!snapshot.ProceduralLighting
                || !SceneBlockLightVolume.CanFit(snapshot.Columns, snapshot.Rows, snapshot.LightDepth)
                || snapshot.Layers.Keys.Any(depth => depth >= snapshot.LightDepth)))
            || snapshot.LightPoints is not { Count: <= 16_384 }
            || snapshot.LightQuality is not (SceneBlockLightQuality.Low or SceneBlockLightQuality.Medium
                or SceneBlockLightQuality.High or SceneBlockLightQuality.HighDetailed)
            || (snapshot.LightDepth == 0 && (snapshot.NightVision || snapshot.LightPoints.Count != 0))
            || snapshot.Positions is not { Length: <= 1_000_000 } || snapshot.Materials is null
            || snapshot.Positions.Length != snapshot.Materials.Length)
            throw new InvalidDataException("Invalid scene block grid.");

        Columns = snapshot.Columns;
        Rows = snapshot.Rows;
        CellPixels = snapshot.CellPixels;
        SectionDepth = snapshot.SectionDepth;
        Layers = new Dictionary<int, string>(snapshot.Layers);
        Palette = Array.AsReadOnly((BlockGridMaterial[])snapshot.Palette.Clone());
        Shadows = Array.AsReadOnly((string[])snapshot.Shadows.Clone());
        ProceduralLighting = snapshot.ProceduralLighting;
        LightDepth = snapshot.LightDepth;
        LightQuality = snapshot.LightQuality;
        NightVision = snapshot.NightVision;
        _area = Columns * Rows;
        if (snapshot.LightPoints.Any(pair => pair.Key < 0 || pair.Key / _area >= LightDepth
            || !Layers.ContainsKey((int)(pair.Key / _area)) || pair.Value is < 1 or > 15))
            throw new InvalidDataException("Invalid scene block light point.");
        _lights = new(snapshot.LightPoints);
        for (var i = 0; i < snapshot.Positions.Length; i++)
        {
            var position = snapshot.Positions[i];
            if (position < 0 || position / _area > int.MaxValue || !Layers.ContainsKey((int)(position / _area))
                || (uint)snapshot.Materials[i] >= Palette.Count || (i > 0 && snapshot.Positions[i - 1] >= position))
                throw new InvalidDataException("Invalid scene block grid position or material.");
            var column = (int)(position % _area);
            Set(new(column % Columns, column / Columns, (int)(position / _area)), snapshot.Materials[i]);
        }

        static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && !value.Contains('\0');

        static bool ValidLayers(SceneBlockGridSnapshot data)
        {
            if (data.SectionDepth == 0) return data.Layers.Count <= 128
                && data.Layers.Values.Distinct(StringComparer.Ordinal).Count() == data.Layers.Count;
            if (data.Layers.Count != data.SectionDepth
                || (data.LightDepth != 0 && data.LightDepth != data.SectionDepth)
                || (long)SceneBlockChunk.CountFor(data.Columns) * SceneBlockChunk.CountFor(data.Rows)
                    * SceneBlockChunk.CountFor(data.SectionDepth) > SceneBlockChunk.MaxChunks) return false;
            var groups = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 0; depth < data.SectionDepth; depth++)
            {
                if (!data.Layers.TryGetValue(depth, out var id)) return false;
                if (depth % SceneBlockChunk.CellCount == 0)
                {
                    if (!groups.Add(id)) return false;
                }
                else if (id != data.Layers[depth - 1]) return false;
            }
            return true;
        }
    }

    internal int Columns { get; }
    internal int Rows { get; }
    internal int CellPixels { get; }
    internal int SectionDepth { get; }
    internal int Count { get; private set; }
    internal float CellSize => VectorUnits.FromPixels(CellPixels);
    internal IReadOnlyDictionary<int, string> Layers { get; }
    internal IReadOnlyList<BlockGridMaterial> Palette { get; }
    internal IReadOnlyList<string> Shadows { get; }
    internal bool ProceduralLighting { get; }
    internal int LightDepth { get; }
    internal SceneBlockLightQuality LightQuality { get; }
    internal bool NightVision { get; }
    internal int LightPointCount => _lights.Count;
    internal int GetLight(Cell cell) => _lights.GetValueOrDefault(Encode(cell));

    internal bool Contains(int x, int y) => (uint)x < Columns && (uint)y < Rows;
    internal long Encode(Cell cell) => (long)cell.Depth * _area + cell.Y * Columns + cell.X;
    internal Cell Decode(long key)
    {
        var position = (int)(key % _area);
        return new(position % Columns, position / Columns, (int)(key / _area));
    }
    internal PointF Center(Cell cell) => new((cell.X + .5f - Columns * .5f) * CellSize,
        (cell.Y + .5f - Rows * .5f) * CellSize);

    internal IEnumerable<(Cell Cell, int Material)> Column(int x, int y, bool visibleOnly, int? chunkDepth = null)
    {
        if (!Contains(x, y) || !_columns.TryGetValue(y * Columns + x, out var list)) yield break;
        var blocked = -1;
        foreach (var (depth, material) in list)
        {
            var slab = SectionDepth > 0 ? SceneBlockChunk.Bucket(depth) : 0;
            if (chunkDepth.HasValue && slab != chunkDepth.Value) continue;
            if (visibleOnly && blocked == slab) continue;
            yield return (new(x, y, depth), material);
            if (visibleOnly && Palette[material].Occludes)
            {
                if (SectionDepth == 0 || chunkDepth.HasValue) yield break;
                blocked = slab;
            }
        }
    }

    internal int Get(Cell cell) => Contains(cell.X, cell.Y)
        && _columns.TryGetValue(cell.Y * Columns + cell.X, out var list)
        && list.TryGetValue(cell.Depth, out var material) ? material : -1;

    internal bool IsOpaque(Cell cell) => Get(cell) is var index && index >= 0 && Palette[index].Occludes;

    internal SceneBlockLightVolume Lighting()
    {
        if (_lighting is not null) return _lighting;
        var volume = new SceneBlockLightVolume(Columns, Rows, LightDepth, LightQuality, NightVision);
        foreach (var (column, entries) in _columns)
            foreach (var (depth, material) in entries)
            {
                var cell = new Cell(column % Columns, column / Columns, depth);
                var info = Palette[material];
                volume.Set(cell, info.Occludes, info.LightFilter, info.LightEmission, GetLight(cell));
            }
        foreach (var (position, level) in _lights)
        {
            var column = (int)(position % _area);
            var cell = new Cell(column % Columns, column / Columns, (int)(position / _area));
            if (Get(cell) < 0) volume.Set(cell, false, 0, 0, level);
        }
        volume.Build();
        return _lighting = volume;
    }

    internal void SetLight(Cell cell, int level)
    {
        if (level == 0) _lights.Remove(Encode(cell));
        else _lights[Encode(cell)] = level;
        RefreshLight(cell);
    }

    private void RefreshLight(Cell cell)
    {
        if (_lighting is null) return;
        var index = Get(cell);
        var material = index >= 0 ? Palette[index] : null;
        _lighting.Relight(cell, material?.Occludes ?? false, material?.LightFilter ?? 0,
            material?.LightEmission ?? 0, GetLight(cell));
    }

    internal void Set(Cell cell, int material)
    {
        var key = cell.Y * Columns + cell.X;
        if (!_columns.TryGetValue(key, out var list))
        {
            if (material < 0) return;
            _columns.Add(key, list = []);
        }
        if (material < 0)
        {
            if (list.Remove(cell.Depth)) Count--;
            if (list.Count == 0) _columns.Remove(key);
        }
        else
        {
            if (!list.ContainsKey(cell.Depth)) Count++;
            list[cell.Depth] = material;
        }
        RefreshLight(cell);
    }

    // Legacy saved grids still use their already-imported contact shadow assets.
    internal string? Shadow(Cell cell, int material)
    {
        if (Shadows.Count == 0 || !Palette[material].Occludes || cell.Depth == 0) return null;
        var covered = 0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var other = Get(new(cell.X + dx, cell.Y + dy, cell.Depth - 1));
                if (other >= 0 && Palette[other].Occludes) covered++;
            }
        return covered == 0 ? null : Shadows[Math.Min(2, (covered - 1) / 2)];
    }

    internal SceneBlockGridSnapshot Capture()
    {
        var positions = new long[Count];
        var materials = new int[Count];
        var index = 0;
        foreach (var (column, entries) in _columns)
            foreach (var (depth, material) in entries)
            {
                positions[index] = (long)depth * _area + column;
                materials[index++] = material;
            }
        Array.Sort(positions, materials);
        return new()
        {
            Columns = Columns, Rows = Rows, CellPixels = CellPixels, SectionDepth = SectionDepth, Layers = new(Layers),
            Palette = Palette.ToArray(), Shadows = Shadows.ToArray(), ProceduralLighting = ProceduralLighting,
            LightDepth = LightDepth, LightQuality = LightQuality, NightVision = NightVision, LightPoints = new(_lights),
            Positions = positions, Materials = materials
        };
    }
}
