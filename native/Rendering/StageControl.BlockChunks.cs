using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private RectangleF? _blockChunkOutline;
    private SceneBlockChunk? _blockChunkFocus;
    private (SceneBlockGrid Grid, string LayerId, int? Depth, SceneBlockChunk? Chunk)? _blockLayerHighlight;
    private (VectorScene Scene, int Frame, long Geometry, long Content, long Presentation)? _blockLayerHighlightKey;
    private readonly List<RectangleF> _blockLayerHighlightRuns = [];
    internal Color BlockLayerHighlightColor { get; private set; }
    internal static readonly Color BlockChunkEditingVeil = Color.FromArgb(180, Color.White);
    internal SceneBlockGrid? BlockChunkGrid => _referenceSceneDefinition?.BlockGrid;
    internal bool BlockChunkEditing { get; private set; }
    internal bool BlockChunkIsolationActive => _blockChunkFocus.HasValue && !RenderingCanvasOutput;

    internal void SetBlockChunkFocus(SceneBlockChunk? chunk)
    {
        if (_blockChunkFocus == chunk) return;
        _blockChunkFocus = chunk;
        Invalidate();
    }

    private bool IsOtherSlabInFocusedChunk(VectorScene scene, int objectIndex)
    {
        if (!BlockChunkIsolationActive || _blockChunkFocus is not { } chunk || BlockChunkGrid is not { } grid
            || !scene.TryGetBitmapObjectData(objectIndex, out var bitmap) || bitmap.BlockGridKey is not >= 0) return false;
        var cell = grid.Decode(bitmap.BlockGridKey.Value);
        var placed = SceneBlockChunk.FromCell(cell.X, cell.Y, cell.Depth);
        return placed.X == chunk.X && placed.Y == chunk.Y && placed.Z != chunk.Z;
    }

    internal void SetBlockChunkOutline(RectangleF? bounds, bool editing = false)
    {
        if (_blockChunkOutline == bounds && BlockChunkEditing == editing) return;
        _blockChunkOutline = bounds;
        BlockChunkEditing = editing;
        InvalidateOverlay();
    }

    internal bool TryGetBlockChunkEditingMask(Span<PointF> points) =>
        BlockChunkEditing && !RenderingCanvasOutput && TryGetBlockChunkOutline(points);

    internal bool TryGetBlockChunkOutline(Span<PointF> points)
        => _blockChunkOutline is { } bounds && TryProjectBlockBounds(bounds, points);

    internal bool TryProjectBlockBounds(RectangleF bounds, Span<PointF> points)
    {
        if (UsesReferenceProjection && Reference2DViewDirection != ReferenceViewDirection.Front) return false;
        points[0] = new(bounds.Left, bounds.Top);
        points[1] = new(bounds.Right, bounds.Top);
        points[2] = new(bounds.Right, bounds.Bottom);
        points[3] = new(bounds.Left, bounds.Bottom);
        for (var i = 0; i < 4; i++)
        {
            var point = points[i];
            if (UsesReferenceProjection)
            {
                if (!TryProjectScenePosition(new Vector3(point.X, point.Y, 0), out points[i], out _))
                    return false;
            }
            else points[i] = WorldToScreen(point.X, point.Y);
        }
        return true;
    }

    internal void SetBlockLayerHighlight(SceneBlockGrid grid, string layerId, int? depth, SceneBlockChunk? chunk, Color color)
    {
        var next = (grid, layerId, depth, chunk);
        var tint = Color.FromArgb(88, color);
        if (_blockLayerHighlight == next && BlockLayerHighlightColor == tint) return;
        ClearLayerContentHighlight();
        _blockLayerHighlight = next;
        BlockLayerHighlightColor = tint;
        InvalidateOverlay();
    }

    private void ClearBlockLayerHighlight()
    {
        if (_blockLayerHighlight is null) return;
        _blockLayerHighlight = null;
        _blockLayerHighlightKey = null;
        _blockLayerHighlightRuns.Clear();
        InvalidateOverlay();
    }

    internal IReadOnlyList<RectangleF> GetBlockLayerHighlightRuns()
    {
        if (RenderingCanvasOutput || PlaybackActive || !BlockLayerHighlightInFrontView
            || _blockLayerHighlight is not { } target || !ReferenceEquals(BlockChunkGrid, target.Grid))
            return [];

        var key = (Scene, Frame, Scene.GeometryRevision, Scene.ActiveContentRevision, Scene.LayerPresentationRevision);
        if (_blockLayerHighlightKey == key) return _blockLayerHighlightRuns;
        _blockLayerHighlightKey = key;
        _blockLayerHighlightRuns.Clear();
        var grid = target.Grid;
        var cells = new HashSet<int>();
        foreach (var index in Scene.GetActiveObjectIndices(Frame))
        {
            if (!Scene.ShouldRenderLayerContent(Scene.ObjectLayer[index])
                || !Scene.TryGetBitmapObjectData(index, out var bitmap) || bitmap.BlockGridKey is not >= 0) continue;
            var cell = grid.Decode(bitmap.BlockGridKey.Value);
            if (!grid.Layers.TryGetValue(cell.Depth, out var layerId) || layerId != target.LayerId
                || target.Depth is { } depth && depth != cell.Depth
                || target.Chunk is { } chunk && SceneBlockChunk.FromCell(cell.X, cell.Y, cell.Depth) != chunk) continue;
            cells.Add(cell.Y * grid.Columns + cell.X);
        }

        // Merge adjacent cells into runs so the overlay stays cheap during pan and zoom.
        var ordered = cells.ToArray();
        Array.Sort(ordered);
        for (var i = 0; i < ordered.Length; i++)
        {
            var x = ordered[i] % grid.Columns;
            var y = ordered[i] / grid.Columns;
            var width = 1;
            while (i + 1 < ordered.Length && x + width < grid.Columns && ordered[i + 1] == ordered[i] + 1)
            {
                i++;
                width++;
            }
            _blockLayerHighlightRuns.Add(new((x - grid.Columns * .5f) * grid.CellSize,
                (y - grid.Rows * .5f) * grid.CellSize, width * grid.CellSize, grid.CellSize));
        }
        return _blockLayerHighlightRuns;
    }

    private bool BlockLayerHighlightInFrontView =>
        ReferenceDimension == SceneDimension.TwoD
        && (!UsesReferenceProjection || Reference2DViewDirection == ReferenceViewDirection.Front);
}
