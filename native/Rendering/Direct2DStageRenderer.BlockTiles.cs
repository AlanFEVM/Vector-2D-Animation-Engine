using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const int BlockChunkCells = SceneBlockChunk.CellCount;
    private const int MinimumBatchedBlocks = 64;
    private const long MaxBlockChunkBytes = 64L * 1024 * 1024;
    private static readonly long BlockSourceProbeInterval = Math.Max(1, Stopwatch.Frequency / 16);
    private BlockTileCache? _blockTiles;
    private BlockTileKey? _rejectedBlockTiles;
    internal long BlockTileCacheBuilds { get; private set; }
    internal long BlockTileCacheDraws { get; private set; }
    internal long BlockTileBitmapUploads { get; private set; }
    internal double BlockTileSourceProbeMilliseconds { get; private set; }
    internal bool DrewBlockTilesThisFrame { get; private set; }

    private readonly record struct BlockTileKey(StageControl Stage, VectorScene Scene, long Geometry,
        long ActiveContent, long Summary, long Presentation, int Frame, object? ReferenceView);
    private readonly record struct BlockView(float X, float Y, float Scale);
    private readonly record struct BlockTile(int X, int Y, BitmapObjectData Data);
    private readonly record struct BlockPixel(int X, int Y, BitmapImageRasterKey Appearance, uint Atoms);
    private sealed record BlockChunk(ID2D1Bitmap Bitmap, float X, float Y, int[] CellCounts,
        long[] CellAtoms, int Count, long Atoms, BlockPixel[] Pixels);

    private sealed class BlockTileCache(BlockTileKey key, float cellSize,
        Dictionary<string, BitmapImageRasterKey> sources) : IDisposable
    {
        public BlockTileKey Key { get; } = key;
        public float CellSize { get; } = cellSize;
        public Dictionary<string, BitmapImageRasterKey> Sources { get; } = sources;
        public Dictionary<SceneBlockChunk, BlockChunk> Chunks { get; } = [];
        public BlockView LastView;
        public long LastSourceProbe;

        public void Dispose()
        {
            foreach (var chunk in Chunks.Values) chunk.Bitmap.Dispose();
        }
    }

    private BlockTileCache? PrepareBlockTiles(StageControl stage)
    {
        var scene = stage.Scene;
        var reference = stage.RendersReferenceProjection;
        if (_target is not ID2D1DeviceContext || scene.ObjectCount < MinimumBatchedBlocks
            || stage.PlanarCanvasRotation != 0 || stage.PlaybackActive || stage.BlockChunkIsolationActive
            || stage.UnderlayScene is { ObjectCount: > 0 }
            || stage.OnionSkinScene is not null || stage.DragPreviewScene is not null
            || stage.InstanceMovePreviewObjects is not null || stage.SelectionMoveFeedbackActive
            || stage.SelectionDragPreviewActive || stage.RequiresObjectRendererForDragPreview(scene)
            || stage.SceneHasDistortionsForRendering(scene) || stage.HasSceneCompositionMaskClips(scene)
            || stage.CompositionMasksFor(scene) is not null
            || reference && (!stage.UsesSpatialFrontProjection || !stage.CanRetainReferenceNavigation)
            || Environment.GetEnvironmentVariable("VECTOR_DISABLE_BLOCK_TILE_CACHE") == "1") return null;

        var key = new BlockTileKey(stage, scene, scene.GeometryRevision, scene.ActiveContentRevision,
            scene.SummaryRevision, scene.LayerPresentationRevision, stage.Frame,
            reference ? stage.CreateReferenceNavigationContentKey() : null);
        var now = Stopwatch.GetTimestamp();
        var view = reference
            ? new BlockView(stage.ReferenceTargetX, stage.ReferenceTargetY, stage.ReferenceZoomScale)
            : new BlockView(stage.CameraX, stage.CameraY, stage.Zoom);
        if (_rejectedBlockTiles == key) return null;
        if (_blockTiles is { } retained && retained.Key == key)
        {
            var cameraMoved = retained.LastView != view;
            retained.LastView = view;
            // A content or presentation change misses the key immediately.
            // On pure camera motion, bound filesystem probes and still check
            // on a stationary paint so external texture edits stay visible.
            if (cameraMoved && now - retained.LastSourceProbe < BlockSourceProbeInterval) return retained;
            var probeStarted = Stopwatch.GetTimestamp();
            var sourcesMatch = true;
            foreach (var (asset, source) in retained.Sources)
            {
                if (stage.BitmapImageSampling(asset) == BitmapSampling.Point
                    && TryResolveBlockSourceKey(stage, asset, out var current) && source == current) continue;
                sourcesMatch = false;
                break;
            }
            BlockTileSourceProbeMilliseconds = Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;
            if (sourcesMatch)
            {
                retained.LastSourceProbe = now;
                return retained;
            }
        }
        var previous = _blockTiles;
        if (previous is not null && (previous.Key.Stage != stage || previous.Key.Scene != scene))
        {
            ClearBlockTiles();
            previous = null;
        }
        if (scene.HasDisplayLayerEffects)
        {
            _rejectedBlockTiles = key;
            return null;
        }

        var tiles = new Dictionary<int, BlockTile>();
        var untaggedSources = new HashSet<string>(StringComparer.Ordinal);
        var grid = stage.BlockChunkGrid;
        var cellSize = 0f;
        PointF origin = default;
        foreach (var index in scene.GetActiveObjectIndices(stage.Frame))
        {
            if (!scene.ShouldRenderLayerContent(scene.ObjectLayer[index])) continue;
            if (scene.ShapeKind[index] != ShapeKind.Bitmap || (uint)scene.Argb[index] >> 24 != 255
                || scene.Angle[index] != 0 || scene.Width[index] != scene.Height[index]
                || !scene.TryGetBitmapObjectData(index, out var data)
                || data.BlockGridKey is < 0 || data.VisibleContours is not null || data.IsBrokenApart
                || reference && !HasPlanarBlockPose(stage, index))
                return Reject();
            if (tiles.Count == 0)
            {
                cellSize = scene.Width[index];
                if (!float.IsFinite(cellSize) || cellSize < 1) return Reject();
                origin = grid is not null && grid.CellSize == cellSize
                    ? new PointF(-grid.Columns * cellSize * .5f, -grid.Rows * cellSize * .5f)
                    : new PointF(scene.X[index] - cellSize * .5f, scene.Y[index] - cellSize * .5f);
            }
            if (scene.Width[index] != cellSize
                || !TryGetBlockCell((scene.X[index] - cellSize * .5f - origin.X) / cellSize, out var x)
                || !TryGetBlockCell((scene.Y[index] - cellSize * .5f - origin.Y) / cellSize, out var y))
                return Reject();
            tiles.Add(index, new BlockTile(x, y, data));
            if (data.BlockGridKey is null) untaggedSources.Add(data.ImageAssetId);
        }
        if (tiles.Count < MinimumBatchedBlocks) return Reject();

        var sources = new Dictionary<string, BitmapImageRasterKey>(StringComparer.Ordinal);
        var texelWidth = 1;
        var texelHeight = 1;
        foreach (var tile in tiles.Values)
        {
            var asset = tile.Data.ImageAssetId;
            if (sources.ContainsKey(asset)) continue;
            if (stage.BitmapImageSampling(asset) != BitmapSampling.Point
                || !TryResolveBlockSourceKey(stage, asset, out var source)
                // Older imports have no block metadata. Admit only their regular
                // 16x16 point textures after proving the shared grid above.
                || untaggedSources.Contains(asset) && (source.PixelWidth != 16 || source.PixelHeight != 16)
                || !TryGetCommonTexelSize(texelWidth, source.PixelWidth, out texelWidth)
                || !TryGetCommonTexelSize(texelHeight, source.PixelHeight, out texelHeight)) return null;
            sources.Add(asset, source);
        }

        // Use the same painter order even when transparent foliage overlaps a
        // block on another depth layer. The reference plan is built unculled once.
        int[] order;
        if (reference)
        {
            var wasRecording = stage.ReferenceNavigationRecording;
            Reference3DRenderItem[] items;
            stage.ReferenceNavigationRecording = true;
            try { items = stage.GetReference3DSceneRenderItems(); }
            finally { stage.ReferenceNavigationRecording = wasRecording; }
            if (items.Length != tiles.Count) return Reject();
            order = new int[items.Length];
            var seen = new HashSet<int>();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item.Kind != Reference3DRenderKind.FrontFill || item.MaterialOpacity != 1
                    || item.FragmentClip is not null || item.SecondaryObjectIndex >= 0
                    || !tiles.ContainsKey(item.ObjectIndex) || !seen.Add(item.ObjectIndex)) return Reject();
                order[i] = item.ObjectIndex;
            }
        }
        else
        {
            order = tiles.Keys.OrderByDescending(index => scene.ObjectLayer[index])
                .ThenBy(index => scene.ObjectOrder[index]).ThenBy(index => scene.ObjectSubOrder[index])
                .ThenBy(index => index).ToArray();
        }

        var groups = order.ToLookup(index => SceneBlockChunk.FromCell(tiles[index].X, tiles[index].Y));
        var width = BlockChunkCells * texelWidth;
        var height = BlockChunkCells * texelHeight;
        if ((long)groups.Count * width * height * 4 > MaxBlockChunkBytes) return null;
        var cache = new BlockTileCache(key, cellSize, sources) { LastView = view, LastSourceProbe = now };
        var reused = new HashSet<SceneBlockChunk>();
        try
        {
            var pixels = new byte[width * height * 4];
            var decoded = new Dictionary<string, BitmapImageRaster>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var chunkX = group.Key.X * BlockChunkCells;
                var chunkY = group.Key.Y * BlockChunkCells;
                var worldX = origin.X + chunkX * cellSize;
                var worldY = origin.Y + chunkY * cellSize;
                var entries = new BlockPixel[group.Count()];
                var entryIndex = 0;
                foreach (var index in group)
                {
                    var tile = tiles[index];
                    entries[entryIndex++] = new(tile.X - chunkX, tile.Y - chunkY,
                        BitmapImageRasterizer.ObjectAppearanceKey(sources[tile.Data.ImageAssetId], tile.Data), scene.AtomCount[index]);
                }
                if (previous is not null && previous.CellSize == cellSize
                    && previous.Chunks.TryGetValue(group.Key, out var unchanged)
                    && unchanged.X == worldX && unchanged.Y == worldY
                    && unchanged.Bitmap.PixelSize.Width == width && unchanged.Bitmap.PixelSize.Height == height
                    && unchanged.Pixels.AsSpan().SequenceEqual(entries))
                {
                    cache.Chunks.Add(group.Key, unchanged);
                    reused.Add(group.Key);
                    continue;
                }

                Array.Clear(pixels);
                var cellCounts = new int[BlockChunkCells * BlockChunkCells];
                var cellAtoms = new long[cellCounts.Length];
                var count = 0;
                long atoms = 0;
                foreach (var index in group)
                {
                    var tile = tiles[index];
                    var asset = tile.Data.ImageAssetId;
                    if (!decoded.TryGetValue(asset, out var raster))
                    {
                        if (!stage.TryDecodeBitmapImage(asset, out raster) || raster.Key != sources[asset]) return null;
                        decoded.Add(asset, raster);
                    }
                    var entry = entries[count];
                    raster = BitmapImageRasterizer.ApplyObjectAppearance(raster, entry.Appearance);
                    var x = entry.X;
                    var y = entry.Y;
                    CompositeBlockTile(pixels, width * 4, x * texelWidth, y * texelHeight, texelWidth, texelHeight, raster);
                    cellCounts[y * BlockChunkCells + x]++;
                    cellAtoms[y * BlockChunkCells + x] += entry.Atoms;
                    count++;
                    atoms += entry.Atoms;
                }
                cache.Chunks.Add(group.Key, new BlockChunk(UploadBlockChunk(pixels, width, height),
                    worldX, worldY, cellCounts, cellAtoms, count, atoms, entries));
                BlockTileBitmapUploads++;
            }
            if (previous is not null)
            {
                foreach (var coordinate in reused) previous.Chunks.Remove(coordinate);
                previous.Dispose();
            }
            _blockTiles = cache;
            _rejectedBlockTiles = null;
            BlockTileCacheBuilds++;
            return cache;
        }
        finally
        {
            if (!ReferenceEquals(_blockTiles, cache))
            {
                foreach (var coordinate in reused) cache.Chunks.Remove(coordinate);
                cache.Dispose();
            }
        }

        BlockTileCache? Reject()
        {
            _rejectedBlockTiles = key;
            return null;
        }
    }

    private static bool HasPlanarBlockPose(StageControl stage, int index)
    {
        if (stage.SceneCompositionResult?.TryGetPose(index, out var pose) != true) return true;
        var matrix = pose.FlatToScene;
        matrix.M43 = 0;
        return matrix == Matrix4x4.Identity && pose.ExtrusionVector == Vector3.Zero;
    }

    private static bool TryResolveBlockSourceKey(StageControl stage, string asset, out BitmapImageRasterKey key)
    {
        if (stage.TryGetBitmapImageKey(asset, out key)) return true;
        // A newly saved project can remap assets to another managed path.
        // A cold source stamp is not a missing image, and the decoded bytes
        // can still match the chunks already retained by this renderer.
        if (!stage.TryDecodeBitmapImage(asset, out var raster)) return false;
        key = raster.Key;
        return true;
    }

    private static bool TryGetBlockCell(float position, out int cell)
    {
        cell = (int)MathF.Round(position);
        return MathF.Abs(position) < 1_000_000 && MathF.Abs(position - cell) <= .0001f;
    }

    private static bool TryGetCommonTexelSize(int current, int size, out int result)
    {
        result = current;
        if (size is <= 0 or > 64) return false;
        while (result % size != 0 && result <= 64) result += current;
        return result <= 64;
    }

    private static void CompositeBlockTile(byte[] pixels, int stride, int x, int y, int width, int height, BitmapImageRaster raster)
    {
        var scaleX = width / raster.PixelWidth;
        var scaleY = height / raster.PixelHeight;
        for (var row = 0; row < height; row++)
        {
            var source = row / scaleY * raster.Stride;
            var destination = (y + row) * stride + x * 4;
            for (var col = 0; col < width; col++, destination += 4)
            {
                var offset = source + col / scaleX * 4;
                var alpha = raster.Pixels[offset + 3];
                if (alpha == 0) continue;
                var inverse = 255 - alpha;
                for (var c = 0; c < 4; c++)
                    pixels[destination + c] = (byte)(raster.Pixels[offset + c]
                        + (pixels[destination + c] * inverse + 127) / 255);
            }
        }
    }

    private ID2D1Bitmap UploadBlockChunk(byte[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var format = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            return _target!.CreateBitmap(new SizeI(width, height), handle.AddrOfPinnedObject(), (uint)(width * 4),
                new BitmapProperties(format, 96, 96));
        }
        finally { handle.Free(); }
    }

    private bool TryGetBlockTileView(StageControl stage, BlockTileCache cache, out PointF origin, out float scale)
    {
        if (stage.RendersReferenceProjection)
        {
            if (!stage.TryProjectScenePosition(Vector3.Zero, out origin, out _)
                || !stage.TryProjectScenePosition(new Vector3(1024, 0, 0), out var right, out _))
            { scale = 0; return false; }
            scale = (right.X - origin.X) / 1024;
        }
        else
        {
            origin = stage.WorldToScreen(0, 0);
            scale = stage.WorldLengthToScreen(1);
        }
        return scale * cache.CellSize >= 1;
    }

    private RenderStats DrawBlockTiles(StageControl stage, BlockTileCache cache, PointF origin, float scale)
    {
        var visible = 0;
        var draws = 0;
        long atoms = 0;
        var cell = cache.CellSize * scale;
        var extent = BlockChunkCells * cache.CellSize;
        foreach (var chunk in cache.Chunks.Values)
        {
            var x = origin.X + chunk.X * scale;
            var y = origin.Y + chunk.Y * scale;
            var left = Snap(x);
            var top = Snap(y);
            var right = Snap(origin.X + (chunk.X + extent) * scale);
            var bottom = Snap(origin.Y + (chunk.Y + extent) * scale);
            if (right <= 0 || bottom <= 0 || left >= stage.Width || top >= stage.Height) continue;
            if (left >= 0 && top >= 0 && right <= stage.Width && bottom <= stage.Height)
            {
                visible += chunk.Count;
                atoms += chunk.Atoms;
            }
            else
            {
                // Only partially visible chunks need per-cell accounting.
                var minX = Math.Clamp((int)MathF.Floor(-x / cell), 0, BlockChunkCells);
                var minY = Math.Clamp((int)MathF.Floor(-y / cell), 0, BlockChunkCells);
                var maxX = Math.Clamp((int)MathF.Ceiling((stage.Width - x) / cell), 0, BlockChunkCells);
                var maxY = Math.Clamp((int)MathF.Ceiling((stage.Height - y) / cell), 0, BlockChunkCells);
                var partialCount = 0;
                long partialAtoms = 0;
                for (var row = minY; row < maxY; row++)
                    for (var col = minX; col < maxX; col++)
                    {
                        var index = row * BlockChunkCells + col;
                        partialCount += chunk.CellCounts[index];
                        partialAtoms += chunk.CellAtoms[index];
                    }
                if (partialCount == 0) continue;
                visible += partialCount;
                atoms += partialAtoms;
            }
            _target!.DrawBitmap(chunk.Bitmap, Rect(left, top, right - left, bottom - top), 1,
                BitmapInterpolationMode.NearestNeighbor, Rect(0, 0, chunk.Bitmap.PixelSize.Width, chunk.Bitmap.PixelSize.Height));
            draws++;
        }
        BlockTileCacheDraws++;
        DrewBlockTilesThisFrame = true;
        return new RenderStats(visible, visible, atoms, draws, 0, false);

        static float Snap(float coordinate) => MathF.Floor(coordinate + .5f);
    }

    private void ClearBlockTiles()
    {
        _blockTiles?.Dispose();
        _blockTiles = null;
        _rejectedBlockTiles = null;
    }
}
