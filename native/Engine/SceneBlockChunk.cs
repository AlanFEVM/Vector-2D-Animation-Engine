namespace VectorAnimationEngine;

internal readonly record struct SceneBlockChunk(int X, int Y, int Z = 0)
{
    internal const int CellCount = 16;
    internal const int MaxSectionDepth = 128 * CellCount;
    internal const int MaxChunks = 16_384;
    internal const string SceneLayerGroupName = "MC Scene Layers";

    internal static SceneBlockChunk FromCell(int x, int y, int depth = 0) => new(Bucket(x), Bucket(y), Bucket(depth));

    internal static int Bucket(int cell) => cell >= 0 ? cell / CellCount : (cell + 1) / CellCount - 1;
    internal static int CountFor(int cells) => cells == 0 ? 1 : Bucket(cells - 1) + 1;
    internal static string DepthLabel(int depth) => $"MC Z {depth}";
    internal static string OuterLayerName(int slab) => $"MC Z {slab * CellCount}\u2013{(slab + 1) * CellCount - 1}";

    internal static IEnumerable<SceneBlockChunk> Enumerate(SceneBlockGrid grid)
    {
        for (var z = 0; z < CountFor(grid.SectionDepth); z++)
            for (var y = 0; y < CountFor(grid.Rows); y++)
                for (var x = 0; x < CountFor(grid.Columns); x++)
                    yield return new(x, y, z);
    }

    internal bool ExistsIn(SceneBlockGrid grid) =>
        (uint)X < CountFor(grid.Columns) && (uint)Y < CountFor(grid.Rows) && (uint)Z < CountFor(grid.SectionDepth);

    internal bool Contains(SceneBlockGrid grid, SceneBlockGrid.Cell cell) =>
        this == FromCell(cell.X, cell.Y, grid.SectionDepth > 0 ? cell.Depth : 0);

    internal RectangleF Bounds(SceneBlockGrid grid)
    {
        var x = X * CellCount;
        var y = Y * CellCount;
        return new((x - grid.Columns * 0.5f) * grid.CellSize, (y - grid.Rows * 0.5f) * grid.CellSize,
            Math.Min(CellCount, grid.Columns - x) * grid.CellSize, Math.Min(CellCount, grid.Rows - y) * grid.CellSize);
    }
}

internal sealed class SceneBlockChunkTimeline : ITimelineContext
{
    private readonly Dictionary<string, int> _depths = new(StringComparer.Ordinal);

    internal SceneBlockChunkTimeline(SceneDefinition scene, SceneBlockGrid grid, SceneBlockChunk chunk)
    {
        Scene = scene;
        Grid = grid;
        Chunk = chunk;
        var depths = grid.SectionDepth == 0 ? grid.Layers.Keys.OrderDescending().ToArray()
            : Enumerable.Range(chunk.Z * SceneBlockChunk.CellCount, SceneBlockChunk.CellCount).Reverse().ToArray();
        foreach (var depth in depths)
        {
            _depths.Add(TargetId(depth), depth);
        }
        TimelineTargetIds = _depths.Keys.ToArray();
    }

    internal SceneDefinition Scene { get; }
    internal SceneBlockGrid Grid { get; }
    internal SceneBlockChunk Chunk { get; }
    public AnimationTimeline Timeline { get; } = new();
    public int FrameCount => Math.Max(1, Scene.FrameCount);
    public IReadOnlyList<string> TimelineTargetIds { get; }

    public void SynchronizeTimelineTracks() => Timeline.SynchronizeTracks(TimelineTargetIds, defaultDuration: 1);

    internal bool TryGetDepth(string targetId, out int depth) => _depths.TryGetValue(targetId, out depth);
    internal string LayerName(string targetId) => TryGetDepth(targetId, out var depth)
        ? SceneBlockChunk.DepthLabel(depth) : "";
    internal static string TargetId(int depth) => $"mc-depth-{depth}";
}
