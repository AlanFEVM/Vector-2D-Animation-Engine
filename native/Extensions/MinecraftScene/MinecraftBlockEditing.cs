namespace VectorAnimationEngine;

internal static class MinecraftBlockEditing
{
    private readonly record struct Stamp(SceneBlockGrid.Cell Cell, string? AssetId, uint ShadowCorners = 0, uint Warmth = 0,
        int? BiomeTintArgb = null, bool BiomeTintGrassSide = false)
    {
        internal bool HasSameAppearance(Stamp other) => AssetId == other.AssetId
            && BiomeTintArgb == other.BiomeTintArgb && BiomeTintGrassSide == other.BiomeTintGrassSide;
    }

    internal static void Append(VectorScene drawing, SceneBlockGrid grid, SceneBlockGrid.Cell cell, int layer, uint shadowCorners, uint warmth) =>
        Add(drawing, grid, layer, grid.Encode(cell), BlockStamp(cell, grid.Palette[grid.Get(cell)], shadowCorners, warmth));

    private static Stamp BlockStamp(SceneBlockGrid.Cell cell, BlockGridMaterial material, uint shadows, uint warmth) =>
        new(cell, material.AssetId, shadows, warmth, material.BiomeTintArgb, material.BiomeTintGrassSide);

    internal static bool Apply(VectorProject project, SceneDefinition scene, SceneBlockGrid.Cell cell, int material)
    {
        if (scene.BlockGrid is not { } grid || !grid.Contains(cell.X, cell.Y) || !grid.Layers.ContainsKey(cell.Depth)
            || material < -1 || material >= grid.Palette.Count) return false;
        var original = grid.Get(cell);
        if (original == material) return false;
        if (original < 0 && grid.Count >= 1_000_000) throw Error("The scene block limit has been reached.");

        var from = original < 0 ? null : grid.Palette[original];
        var to = material < 0 ? null : grid.Palette[material];
        var opacityChanged = (from?.Occludes ?? false) != (to?.Occludes ?? false);
        var transmissionChanged = opacityChanged || (from?.LightFilter ?? 0) != (to?.LightFilter ?? 0);
        var emissionChanged = (from?.LightEmission ?? 0) != (to?.LightEmission ?? 0);
        var affected = grid.LightDepth == 0 ? SceneBlockLighting.AffectedColumns(grid, cell, opacityChanged)
            : AffectedColumns(grid, cell, transmissionChanged, emissionChanged);
        return ApplyChange(project, scene, grid, affected, () => grid.Set(cell, material), () => grid.Set(cell, original));
    }

    internal static bool ApplyLight(VectorProject project, SceneDefinition scene, SceneBlockGrid.Cell cell, int level, bool restoring = false)
    {
        if (scene.BlockGrid is not { LightDepth: > 0 } grid || !grid.Contains(cell.X, cell.Y)
            || !grid.Layers.ContainsKey(cell.Depth) || level is < 0 or > SceneBlockLightVolume.MaximumLevel) return false;
        var original = grid.GetLight(cell);
        if (original == level) return false;
        if (!restoring && level > 0 && grid.IsOpaque(cell)) throw Error("Place light points in air or on a non-solid block.");
        if (original == 0 && grid.LightPointCount >= 16_384) throw Error("The scene light point limit has been reached.");
        return ApplyChange(project, scene, grid, AffectedColumns(grid, cell, transmission: false, emission: true),
            () => grid.SetLight(cell, level), () => grid.SetLight(cell, original));
    }

    private static IEnumerable<(int X, int Y)> AffectedColumns(SceneBlockGrid grid, SceneBlockGrid.Cell cell,
        bool transmission, bool emission)
    {
        if (!transmission && !emission) { yield return (cell.X, cell.Y); yield break; }
        var sampleReach = transmission ? grid.LightQuality switch
        {
            SceneBlockLightQuality.High => SceneBlockLightVolume.HighFaceSampleReach,
            SceneBlockLightQuality.HighDetailed => Math.Max(SceneBlockLightVolume.HighFaceSampleReach, SceneBlockLightVolume.DetailedOcclusionReach),
            _ => 1
        } : 1;
        var radius = SceneBlockLightVolume.Reach + sampleReach;
        for (var y = transmission ? 0 : Math.Max(0, cell.Y - radius);
             y <= (transmission ? grid.Rows - 1 : Math.Min(grid.Rows - 1, cell.Y + radius)); y++)
            for (var x = Math.Max(0, cell.X - radius); x <= Math.Min(grid.Columns - 1, cell.X + radius); x++)
                yield return (x, y);
    }

    private static bool ApplyChange(VectorProject project, SceneDefinition scene, SceneBlockGrid grid,
        IEnumerable<(int X, int Y)> affected, Action apply, Action revert)
    {
        var volume = grid.LightDepth > 0 ? grid.Lighting() : null;
        Func<SceneBlockGrid.Cell, bool> opaque = grid.IsOpaque;
        var before = ProjectNeighbourhood();
        try
        {
            apply();
            ApplyProjection(project, scene, grid, before, ProjectNeighbourhood());
        }
        catch
        {
            revert();
            throw;
        }
        return true;

        Dictionary<long, Stamp> ProjectNeighbourhood()
        {
            var result = new Dictionary<long, Stamp>();
            foreach (var (x, y) in affected)
                foreach (var (candidate, candidateMaterial) in grid.Column(x, y, visibleOnly: true))
                {
                    var key = grid.Encode(candidate);
                    var face = volume?.Shade(candidate) ?? new SceneBlockLightVolume.Face(
                        grid.ProceduralLighting ? SceneBlockLighting.Calculate(candidate, opaque) : 0, 0);
                    result.Add(key, BlockStamp(candidate, grid.Palette[candidateMaterial], face.Shadows, face.Warmth));
                    if (grid.Shadow(candidate, candidateMaterial) is { } shadow) result.Add(~key, new(candidate, shadow));
                }
            return result;
        }
    }

    private static void ApplyProjection(VectorProject project, SceneDefinition scene, SceneBlockGrid grid,
        Dictionary<long, Stamp> before, Dictionary<long, Stamp> after)
    {
        var remove = before.Where(pair => !after.TryGetValue(pair.Key, out var next) || next != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var add = after.Where(pair => !before.TryGetValue(pair.Key, out var previous) || previous != pair.Value).ToArray();
        var drawing = scene.DrawingScene;
        var layers = new Dictionary<int, int>();
        foreach (var stamp in remove.Values.Concat(add.Select(pair => pair.Value)))
        {
            if (layers.ContainsKey(stamp.Cell.Depth)) continue;
            var index = Array.IndexOf(drawing.LayerIds, grid.Layers[stamp.Cell.Depth]);
            if (index < 0 || drawing.GetLayerKind(index) != DrawingLayerKind.Drawing)
                throw Error("A scene block layer is missing or has changed.");
            layers.Add(stamp.Cell.Depth, index);
        }

        foreach (var (_, stamp) in add)
            if (project.FindImageAsset(stamp.AssetId) is null)
                throw Error("This edit would reveal a block or shadow with a missing texture.");

        if (remove.Count != 0 || add.Length != 0)
        {
            var indices = new Dictionary<long, int>();
            var additions = add.Select(pair => pair.Key).ToHashSet();
            for (var index = 0; index < drawing.ObjectCount; index++)
            {
                if (drawing.ObjectKeyframeFrame[index] != 0 || !drawing.TryGetBitmapObjectData(index, out var bitmap)
                    || bitmap.BlockGridKey is not { } key) continue;
                if (!remove.TryGetValue(key, out var stamp))
                {
                    if (additions.Contains(key)) throw EditConflict();
                    continue;
                }
                var center = grid.Center(stamp.Cell);
                if (drawing.ObjectLayer[index] != layers[stamp.Cell.Depth] || drawing.Angle[index] != 0
                    || drawing.X[index] != VectorUnits.Quantize(center.X) || drawing.Y[index] != VectorUnits.Quantize(center.Y)
                    || bitmap.ImageAssetId != stamp.AssetId || bitmap.VisibleContours is not null || bitmap.IsBrokenApart
                    || bitmap.ShadowCorners != stamp.ShadowCorners || bitmap.WarmLightCorners != stamp.Warmth
                    || bitmap.BiomeTintArgb != stamp.BiomeTintArgb || bitmap.BiomeTintGrassSide != stamp.BiomeTintGrassSide
                    || bitmap.PlacedSize.Width != grid.CellSize || bitmap.PlacedSize.Height != grid.CellSize
                    || !indices.TryAdd(key, index)) throw EditConflict();
            }
            if (indices.Count != remove.Count) throw EditConflict();
            var editFrame = drawing.EditFrame;
            drawing.EditFrame = 0;
            try
            {
                // Retain order and object identity for blocks that only changed lighting.
                var relit = add.Where(pair => remove.TryGetValue(pair.Key, out var old) && old.HasSameAppearance(pair.Value)).ToArray();
                if (relit.Length > 0)
                    drawing.UpdateBlockLighting(relit.Select(pair => (indices[pair.Key], pair.Value.ShadowCorners, pair.Value.Warmth)));
                foreach (var (key, _) in relit) indices.Remove(key);
                var relitKeys = relit.Select(pair => pair.Key).ToHashSet();
                add = add.Where(pair => !relitKeys.Contains(pair.Key)).ToArray();
                if (indices.Count != 0) drawing.RemoveObjects(indices.Values);
                if (add.Length != 0)
                {
                    using var batch = drawing.BeginBulkObjectAppend(add.Length,
                        add.Select(pair => layers[pair.Value.Cell.Depth]).Distinct().ToArray());
                    foreach (var (key, stamp) in add) Add(drawing, grid, layers[stamp.Cell.Depth], key, stamp);
                }
            }
            finally { drawing.EditFrame = editFrame; }
        }
    }

    private static void Add(VectorScene drawing, SceneBlockGrid grid, int layer, long key, Stamp stamp)
    {
        var bitmap = new BitmapObjectData
        {
            BlockGridKey = key,
            ShadowCorners = stamp.ShadowCorners,
            WarmLightCorners = stamp.Warmth,
            BiomeTintArgb = stamp.BiomeTintArgb,
            BiomeTintGrassSide = stamp.BiomeTintGrassSide,
            ImageAssetId = stamp.AssetId ?? throw EditConflict(),
            PlacedSize = new(grid.CellSize, grid.CellSize)
        };
        if (grid.SectionDepth > 0)
            drawing.AddChunkBlockBitmap(layer, grid, stamp.Cell, bitmap);
        else drawing.AddBitmapObject(layer, grid.Center(stamp.Cell), bitmap);
    }

    private static InvalidOperationException EditConflict() => Error("These scene blocks were changed outside the block editor.");
    private static InvalidOperationException Error(string text) => new(UiLocalization.T(text));
}
