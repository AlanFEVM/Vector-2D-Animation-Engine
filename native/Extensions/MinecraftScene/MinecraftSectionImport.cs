using System.Text.Json;

namespace VectorAnimationEngine;

internal static class MinecraftSectionImport
{
    private const int MaxBlocks = 1_000_000;
    private const int MaxSectionJsonBytes = 256 * 1024 * 1024;
    private const int MaxVisible = 20_000;
    private const int MaxChunkFaces = 200_000;
    private const int MaxDepthLayers = 128;
    private const int MaxTextures = 1024;
    private const int MaxTextureBytes = 4 * 1024 * 1024;
    private const long MaxCombinedTextureBytes = 128L * 1024 * 1024;
    private const string Descriptor = "minecraft-scene.v2dextension";
    internal const int DefaultCellSize = 16;
    internal sealed record Texture(string Path, string Hash, int Width, int Height);
    internal sealed record Tile(string Id, int X, int Y, int Depth, bool Occludes, string TextureSuffix = "")
    {
        internal string TextureKey => Id + TextureSuffix;
        internal SceneBlockGrid.Cell Cell => new(X, Y, Depth);
        internal uint ShadowCorners { get; init; }
        internal uint WarmLightCorners { get; init; }
        internal int Emission { get; init; }
        internal int LightFilter { get; init; }
        internal int? BiomeTintArgb { get; init; }
        internal bool BiomeTintGrassSide { get; init; }
    }
    internal sealed record Plan(string Name, int Width, int Height, int CellSize, int SourceCount,
        Tile[] Tiles, Dictionary<string, Texture> Textures, string[] Missing, int MissingCount,
        Tile[]? GridTiles = null, bool ProceduralLighting = false, int MissingHiddenCount = 0, int LightDepth = 0,
        SceneBlockLightQuality LightQuality = SceneBlockLightQuality.Low, int SectionDepth = 0, Tile[]? ChunkTiles = null,
        bool NightVision = false);

    internal static string DefaultTextureDirectory(string baseDirectory)
    {
        var directory = Path.Combine(baseDirectory, "Extensions", "MinecraftScene", "Textures");
        return Directory.Exists(directory) ? directory : string.Empty;
    }

    internal static bool IsAvailable(string baseDirectory)
    {
        try
        {
            var path = Path.Combine(baseDirectory, "Extensions", "MinecraftScene", Descriptor);
            using var document = ReadJson(path, 4096);
            var root = document.RootElement;
            return root.GetProperty("formatVersion").GetInt32() == 1
                && root.GetProperty("id").GetString() == "minecraft-scene"
                && root.GetProperty("workflow").GetString() == "minecraft-scene";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException)
        {
            return false;
        }
    }

    internal static Plan Prepare(string jsonPath, string textureDirectory, string mappingPath,
        int cellSize, bool importLighting, CancellationToken cancellationToken, bool editable = false,
        SceneBlockLightQuality quality = SceneBlockLightQuality.Low, bool nightVision = false)
    {
        if (cellSize is < 1 or > 512) throw new InvalidDataException("Invalid block size.");
        using var document = ReadJson(jsonPath, MaxSectionJsonBytes);
        using var mapping = string.IsNullOrEmpty(mappingPath) ? null : ReadJson(mappingPath, 4 * 1024 * 1024);
        var map = mapping?.RootElement;
        if (map is { ValueKind: not JsonValueKind.Object }) throw new InvalidDataException("Invalid texture map.");
        var root = document.RootElement;
        var bounds = root.GetProperty("bounds");
        var (minX, minY, minZ) = (Coordinate(bounds, "minX"), Coordinate(bounds, "minY"), Coordinate(bounds, "minZ"));
        var (maxX, maxY, maxZ) = (Coordinate(bounds, "maxX"), Coordinate(bounds, "maxY"), Coordinate(bounds, "maxZ"));
        if (maxX < minX || maxY < minY || maxZ < minZ) throw new InvalidDataException("Invalid section bounds.");
        var front = root.GetProperty("front").GetString();
        if (front is not ("minX" or "maxX" or "minZ" or "maxZ")) throw new InvalidDataException("Invalid section front.");
        var alongX = front is "minZ" or "maxZ";
        var width = ((long)(alongX ? maxX - minX : maxZ - minZ) + 1) * cellSize;
        var height = ((long)maxY - minY + 1) * cellSize;
        if (width > 32768 || height > 32768) throw new InvalidDataException("The generated canvas would exceed 32768 pixels.");
        var sectionDepth = (alongX ? maxZ - minZ : maxX - minX) + 1;
        var lightDepth = importLighting ? sectionDepth : 0;
        var columns = (int)width / cellSize;
        var rows = (int)height / cellSize;
        if (editable && (sectionDepth > SceneBlockChunk.MaxSectionDepth
            || (long)SceneBlockChunk.CountFor(columns) * SceneBlockChunk.CountFor(rows)
                * SceneBlockChunk.CountFor(sectionDepth) > SceneBlockChunk.MaxChunks))
            throw new InvalidDataException(UiLocalization.T("Too many MC chunks. Export a smaller section."));
        if (importLighting && !SceneBlockLightVolume.CanFit(columns, rows, lightDepth))
            throw new InvalidDataException(UiLocalization.T("The section is too large for block lighting. Export a smaller section."));
        var blocks = root.GetProperty("blocks");
        if (blocks.GetArrayLength() is < 1 or > MaxBlocks)
            throw new InvalidDataException(string.Format(UiLocalization.T("Expected 1 to {0:N0} source blocks."), MaxBlocks));
        var positions = new HashSet<(int, int, int)>();
        var candidates = new List<Tile>(blocks.GetArrayLength());
        foreach (var block in blocks.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y, z) = (Coordinate(block, "x"), Coordinate(block, "y"), Coordinate(block, "z"));
            if (x < minX || x > maxX || y < minY || y > maxY || z < minZ || z > maxZ || !positions.Add((x, y, z)))
                throw new InvalidDataException("A block is outside the section or duplicated.");
            var id = block.GetProperty("id").GetString();
            if (id is null || id.Length is < 1 or > 160 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or ':' or '.')))
                throw new InvalidDataException("Invalid block ID.");
            if (id is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air") continue;
            var (screenX, depth) = front switch
            {
                "minZ" => (maxX - x, z - minZ),
                "maxZ" => (x - minX, maxZ - z),
                "minX" => (z - minZ, x - minX),
                _ => (maxZ - z, maxX - x)
            };
            var screenY = maxY - y;
            var (emission, filter) = importLighting ? MinecraftLightMaterials.Read(id, block) : default;
            var suffix = TextureSuffix(id, block);
            var occludes = id is not ("minecraft:torch" or "minecraft:wall_torch")
                && (!block.TryGetProperty("occlude", out var opacity) || opacity.GetBoolean());
            candidates.Add(new(id, screenX, screenY, depth, occludes, suffix)
                { Emission = emission, LightFilter = filter, BiomeTintArgb = MinecraftLightMaterials.ReadBiomeTint(id, suffix, block) });
        }

        var tiles = new List<Tile>();
        var chunkTiles = editable ? new List<Tile>() : null;
        var blocked = new HashSet<(int, int)>();
        var blockedChunks = new HashSet<(int, int, int)>();
        foreach (var tile in candidates.OrderBy(tile => tile.Depth))
        {
            var key = (tile.X, tile.Y);
            if (!blocked.Contains(key))
            {
                tiles.Add(tile);
                if (tile.Occludes) blocked.Add(key);
            }
            if (chunkTiles is null) continue;
            var chunkKey = (tile.X, tile.Y, SceneBlockChunk.Bucket(tile.Depth));
            if (blockedChunks.Contains(chunkKey)) continue;
            chunkTiles.Add(tile);
            if (tile.Occludes) blockedChunks.Add(chunkKey);
        }
        var depthTiles = editable ? candidates : tiles;
        if (tiles.Count is < 1 or > MaxVisible || !editable && depthTiles.Select(tile => tile.Depth).Distinct().Count() > MaxDepthLayers)
            throw new InvalidDataException("Expected 1 to 20000 visible blocks across at most 128 depth layers.");
        if (chunkTiles is { Count: > MaxChunkFaces })
            throw new InvalidDataException(UiLocalization.T("Too many projected MC block faces. Export a smaller section."));

        if (importLighting)
        {
            var volume = new SceneBlockLightVolume(columns, rows, lightDepth, quality, nightVision);
            foreach (var tile in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                volume.Set(tile.Cell, tile.Occludes, tile.LightFilter, tile.Emission, 0);
            }
            volume.Build(cancellationToken);
            var shaded = new Dictionary<SceneBlockGrid.Cell, Tile>();
            Shade(chunkTiles ?? tiles);
            if (chunkTiles is not null)
                for (var i = 0; i < tiles.Count; i++)
                    tiles[i] = shaded[tiles[i].Cell];

            void Shade(List<Tile> faces)
            {
                for (var i = 0; i < faces.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tile = faces[i];
                    var face = volume.Shade(tile.Cell);
                    if ((face.Shadows | face.Warmth) != 0)
                        faces[i] = tile with { ShadowCorners = face.Shadows, WarmLightCorners = face.Warmth };
                    if (chunkTiles is not null) shaded.Add(tile.Cell, faces[i]);
                }
            }
        }
        var textures = new Dictionary<string, Texture>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var basePath = Path.GetFullPath(textureDirectory);
        if (!Directory.Exists(basePath)) throw new DirectoryNotFoundException(basePath);
        long textureBytes = 0;
        var visibleKeys = (chunkTiles ?? tiles).Select(tile => tile.TextureKey).ToHashSet(StringComparer.Ordinal);
        var missingHidden = 0;
        foreach (var tile in depthTiles.DistinctBy(tile => tile.TextureKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = tile.Id;
            var name = id[(id.IndexOf(':') + 1)..];
            var mapped = tile.TextureSuffix.Length == 0 ? null
                : ResolveTextureName(map, tile.TextureKey, name + tile.TextureSuffix);
            var path = new[] { mapped, tile.TextureSuffix.Length == 0 ? null : name + tile.TextureSuffix,
                    ResolveTextureName(map, id, name), name,
                    name is "water" or "lava" ? name + "_still" : null, name == "wall_torch" ? "torch" : null,
                    name + "_side", name + "_front" }
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Select(candidate => TexturePath(basePath, candidate!)).FirstOrDefault(File.Exists);
            if (path is null)
            {
                if (visibleKeys.Contains(tile.TextureKey)) missing.Add(tile.TextureKey);
                else missingHidden++;
                continue;
            }
            textures.Add(tile.TextureKey, ReadTexture(path));
        }
        if (editable && importLighting && !textures.ContainsKey(MinecraftLightMaterials.Torch))
        {
            var torch = TexturePath(basePath, "torch");
            var bundled = DefaultTextureDirectory(AppContext.BaseDirectory);
            if (!File.Exists(torch) && bundled.Length != 0) torch = TexturePath(bundled, "torch");
            if (File.Exists(torch)) textures.Add(MinecraftLightMaterials.Torch, ReadTexture(torch));
        }
        var sceneName = Path.GetFileNameWithoutExtension(jsonPath);
        return new(sceneName[..Math.Min(80, sceneName.Length)], (int)width, (int)height, cellSize,
            blocks.GetArrayLength(), tiles.Select(ResolveGrassFace).ToArray(), textures, missing.Take(12).ToArray(), missing.Count,
            editable ? candidates.Select(ResolveGrassFace).ToArray() : null, importLighting, missingHidden, lightDepth,
            importLighting ? quality : SceneBlockLightQuality.Low, editable ? sectionDepth : 0,
            chunkTiles?.Select(ResolveGrassFace).ToArray(), importLighting && nightVision);

        Tile ResolveGrassFace(Tile tile)
        {
            if (tile.BiomeTintArgb is null) return tile;
            var name = MinecraftLightMaterials.VanillaBlockName(tile.Id);
            if (name is not ("grass_block" or "grass_block_side" or "grass_block_top")) return tile;
            if (!textures.TryGetValue(tile.TextureKey, out var texture)) return tile with { BiomeTintArgb = null };
            var file = Path.GetFileNameWithoutExtension(texture.Path.AsSpan());
            if (file.EndsWith("_snow", StringComparison.OrdinalIgnoreCase) || file.EndsWith("_\u96ea\u8986\u76d6", StringComparison.Ordinal))
                return tile with { BiomeTintArgb = null };
            // The section uses a side face unless a top texture was explicitly selected.
            if (file.EndsWith("_top", StringComparison.OrdinalIgnoreCase) || file.EndsWith("_\u9876\u90e8", StringComparison.Ordinal))
                return tile;
            if (file.Equals("grass_block_side".AsSpan(), StringComparison.OrdinalIgnoreCase)
                && texture.Width >= 16 && texture.Width == texture.Height)
                return tile with { BiomeTintGrassSide = true };
            // Unknown texture layouts must not tint the dirt or cut into a custom grass edge.
            return tile with { BiomeTintArgb = null };
        }

        Texture ReadTexture(string path)
        {
            if (!byPath.TryGetValue(path, out var texture))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 or > MaxTextureBytes
                    || byPath.Count >= MaxTextures || (textureBytes += info.Length) > MaxCombinedTextureBytes)
                    throw new InvalidDataException($"Texture limit exceeded or linked texture: {info.Name}");
                using var stream = OpenBounded(path, MaxTextureBytes);
                // Read the PNG size before decoding; oversized images never enter GDI+.
                Span<byte> header = new byte[24];
                stream.ReadExactly(header);
                var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]);
                var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]);
                if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    || !header[12..16].SequenceEqual("IHDR"u8) || w is < 1 or > 2048 || h is < 1 or > 2048)
                    throw new InvalidDataException($"Unsupported PNG texture: {info.Name}");
                stream.Position = 0;
                using (var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true))
                    if (image.Width != w || image.Height != h) throw new InvalidDataException($"Invalid PNG texture: {info.Name}");
                stream.Position = 0;
                texture = new(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), w, h);
                byPath.Add(path, texture);
            }
            return texture;
        }
    }

    private static string TextureSuffix(string id, JsonElement block)
    {
        if (!block.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) return "";
        if (MinecraftLightMaterials.VanillaBlockName(id) == "grass_block"
            && props.TryGetProperty("snowy", out var snowy) && (snowy.ValueKind == JsonValueKind.True
                || snowy.ValueKind == JsonValueKind.String && snowy.GetString() == "true"))
            return "_snow";
        if (id == "minecraft:pointed_dripstone"
            && props.TryGetProperty("vertical_direction", out var direction) && direction.ValueKind == JsonValueKind.String
            && props.TryGetProperty("thickness", out var thickness) && thickness.ValueKind == JsonValueKind.String
            && direction.GetString() is "up" or "down"
            && thickness.GetString() is "tip" or "tip_merge" or "frustum" or "middle" or "base")
            return "_" + direction.GetString() + "_" + thickness.GetString();
        if (!props.TryGetProperty("half", out var half) || half.ValueKind != JsonValueKind.String) return "";
        return half.GetString() switch
        {
            "upper" => "_top",
            "lower" => "_bottom",
            _ => ""
        };
    }

    private static string? ResolveTextureName(JsonElement? map, string id, string name)
    {
        if (map is not { } lookup || (!lookup.TryGetProperty(id, out var entry) && !lookup.TryGetProperty(name, out entry))) return null;
        if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("side", out var side)
            && side.ValueKind == JsonValueKind.String) return side.GetString();
        return null;
    }

    internal static (SceneDefinition Scene, ImageAssetRestartSnapshot[] Assets) CreateScene(VectorProject project, Plan plan)
    {
        if (plan.MissingCount != 0) throw new InvalidDataException("Resolve missing textures before generating.");
        var uniqueTextures = plan.Textures.Values.Distinct().ToArray();
        if (project.ImageAssets.Count + uniqueTextures.Length > VectorProject.MaxImageAssetCount)
            throw new InvalidDataException("This project does not have enough room for the textures.");

        var before = project.Scenes.ToArray();
        var assets = new Dictionary<Texture, ImageAssetDefinition>();
        var cellSize = VectorUnits.FromPixels(plan.CellSize);
        var halfWidth = VectorUnits.FromPixels(plan.Width) / 2f;
        var halfHeight = VectorUnits.FromPixels(plan.Height) / 2f;
        try
        {
            foreach (var texture in uniqueTextures)
            {
                var name = Path.GetFileNameWithoutExtension(texture.Path);
                var firstFrameHeight = texture.Height > texture.Width && texture.Height % texture.Width == 0
                    ? (float)texture.Width / texture.Height : 1f;
                var settings = BitmapImageImportSettings.Default with
                {
                    FilterMode = ImageFilterMode.Point, Mipmaps = false, GenerateMipmapsForAlpha = false,
                    Compression = ImageCompression.Raw, CropHeight = firstFrameHeight
                };
                if (!project.TryAddImageAssetFromSource(name[..Math.Min(name.Length, VectorProject.MaxImageAssetNameLength)],
                        texture.Path, texture.Hash, texture.Width, texture.Height, 100, settings, out var asset) || asset is null)
                    throw new InvalidDataException($"Could not copy texture: {Path.GetFileName(texture.Path)}");
                assets.Add(texture, asset);
            }

            var scene = project.AddScene(plan.Name);
            if (!project.TrySetSceneCanvas(scene.Id, new() { Width = plan.Width, Height = plan.Height }))
                throw new InvalidOperationException("Could not create the scene canvas.");
            var grouped = plan.SectionDepth > 0;
            var projection = plan.ChunkTiles ?? plan.Tiles;
            var groups = projection.ToLookup(tile => grouped ? SceneBlockChunk.Bucket(tile.Depth) : tile.Depth);
            var depths = grouped ? Enumerable.Range(0, SceneBlockChunk.CountFor(plan.SectionDepth)).ToArray()
                : (plan.GridTiles ?? plan.Tiles).Select(tile => tile.Depth).Distinct().OrderDescending().ToArray();
            var depthStep = Math.Min(cellSize, (SceneShotSettings.MaximumPosition - 1001f)
                / Math.Max(1, depths.Max() * (grouped ? SceneBlockChunk.CellCount : 1)));
            var layerIds = new string[depths.Length];
            using (scene.Timeline.BeginBatchUpdate())
            {
                for (var i = 0; i < depths.Length; i++)
                {
                    var depth = depths[i];
                    var label = grouped ? SceneBlockChunk.OuterLayerName(depth) : SceneBlockChunk.DepthLabel(depth);
                    if (i == 0)
                    {
                        layerIds[i] = scene.Layers[0].Id;
                        project.TryRenameSceneLayer(scene.Id, layerIds[i], label);
                    }
                    else if (project.TryAddSceneLayer(scene.Id, label, out var layer) && layer is not null)
                        layerIds[i] = layer.Id;
                    else throw new InvalidOperationException("Could not add a scene layer.");
                    scene.SetLayerParallax(layerIds[i], 1000f + depth * depthStep * (grouped ? SceneBlockChunk.CellCount : 1), enabled: true);
                }
            }

            var drawing = scene.DrawingScene;
            int[] ResolveLayerIndices()
            {
                var indices = layerIds.Select(id => Array.IndexOf(drawing.LayerIds, id)).ToArray();
                if (indices.Any(index => index < 0)) throw new InvalidOperationException("Could not resolve a scene layer.");
                return indices;
            }
            var size = new SizeF(cellSize, cellSize);
            if (plan.GridTiles is { } gridTiles)
            {
                var columns = plan.Width / plan.CellSize;
                var rows = plan.Height / plan.CellSize;
                var paletteKeys = gridTiles.Select(MaterialKey).Distinct().ToList();
                var torch = (MinecraftLightMaterials.Torch, false, MinecraftLightMaterials.TorchEmission, 0, (int?)null, false);
                if (plan.ProceduralLighting && plan.Textures.ContainsKey(torch.Item1) && !paletteKeys.Contains(torch)) paletteKeys.Add(torch);
                var paletteIndices = paletteKeys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index);
                var positions = gridTiles.Select(tile => (long)tile.Depth * columns * rows + tile.Y * columns + tile.X).ToArray();
                var materials = gridTiles.Select(tile => paletteIndices[MaterialKey(tile)]).ToArray();
                Array.Sort(positions, materials);
                var grid = new SceneBlockGrid(new()
                {
                    Columns = columns, Rows = rows, CellPixels = plan.CellSize, SectionDepth = plan.SectionDepth,
                    Layers = grouped ? Enumerable.Range(0, plan.SectionDepth).ToDictionary(depth => depth,
                        depth => layerIds[SceneBlockChunk.Bucket(depth)])
                        : depths.Select((depth, index) => (depth, index)).ToDictionary(item => item.depth, item => layerIds[item.index]),
                    Palette = paletteKeys.Select(key => new BlockGridMaterial(key.TextureKey,
                        plan.Textures.TryGetValue(key.TextureKey, out var texture) ? assets[texture].Id : null, key.Occludes)
                        {
                            LightEmission = key.Emission, LightFilter = key.LightFilter,
                            BiomeTintArgb = key.BiomeTintArgb, BiomeTintGrassSide = key.BiomeTintGrassSide
                        }).ToArray(),
                    ProceduralLighting = plan.ProceduralLighting,
                    LightDepth = plan.LightDepth,
                    LightQuality = plan.LightQuality,
                    NightVision = plan.NightVision,
                    Positions = positions, Materials = materials
                });
                scene.BlockGrid = grid;
                var tab = scene.Timeline.CreateTabGroup(SceneBlockChunk.SceneLayerGroupName);
                if (tab.Length > 0)
                {
                    foreach (var id in layerIds)
                        if (scene.Timeline.FindTrackByTargetId(id) is { } track)
                            scene.Timeline.SetTrackTabGroup(track.Id, tab);
                    scene.Timeline.SetActiveTabGroup(AnimationTimeline.AllTabGroupId);
                }
                if (grouped) scene.SetActiveLayer(layerIds[0]);
                var layerIndices = ResolveLayerIndices();
                var populatedLayers = depths.Select((depth, index) => (depth, index)).Where(item => groups.Contains(item.depth))
                    .Select(item => layerIndices[item.index]).ToArray();
                using (drawing.BeginBulkObjectAppend(projection.Length, populatedLayers))
                    for (var i = 0; i < depths.Length; i++)
                        foreach (var tile in groups[depths[i]])
                            MinecraftBlockEditing.Append(drawing, grid, tile.Cell, layerIndices[i], tile.ShadowCorners, tile.WarmLightCorners);
            }
            else
            {
                var layerIndices = ResolveLayerIndices();
                using (drawing.BeginBulkObjectAppend(plan.Tiles.Length, layerIndices))
                    for (var i = 0; i < depths.Length; i++)
                        foreach (var tile in groups[depths[i]])
                            drawing.AddBitmapObject(layerIndices[i],
                                new PointF((tile.X + .5f) * cellSize - halfWidth, (tile.Y + .5f) * cellSize - halfHeight),
                                new BitmapObjectData { ImageAssetId = assets[plan.Textures[tile.TextureKey]].Id,
                                    PlacedSize = size, ShadowCorners = tile.ShadowCorners, WarmLightCorners = tile.WarmLightCorners,
                                    BiomeTintArgb = tile.BiomeTintArgb, BiomeTintGrassSide = tile.BiomeTintGrassSide,
                                    BlockGridKey = ((long)tile.Depth * (plan.Height / plan.CellSize) + tile.Y)
                                        * (plan.Width / plan.CellSize) + tile.X });
            }

            static (string TextureKey, bool Occludes, int Emission, int LightFilter, int? BiomeTintArgb, bool BiomeTintGrassSide)
                MaterialKey(Tile tile) => (tile.TextureKey, tile.Occludes, tile.Emission, tile.LightFilter, tile.BiomeTintArgb, tile.BiomeTintGrassSide);

            var ids = assets.Values.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
            return (scene, project.CreateImageAssetRestartSnapshots().Where(asset => ids.Contains(asset.Id)).ToArray());
        }
        catch
        {
            if (project.Scenes.Count != before.Length) project.RestoreSceneListHistory(before);
            foreach (var asset in assets.Values) project.TryRemoveImageAsset(asset.Id, out _);
            throw;
        }
    }

    private static string TexturePath(string directory, string name)
    {
        if (name.Length is < 1 or > 180 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The texture map must use PNG file names, not paths.");
        return Path.Combine(directory, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png");
    }

    private static int Coordinate(JsonElement element, string property)
    {
        var coordinate = element.GetProperty(property).GetInt32();
        if (coordinate is < -32_000_000 or > 32_000_000) throw new InvalidDataException("Coordinate outside the supported range.");
        return coordinate;
    }

    private static JsonDocument ReadJson(string path, int limit)
    {
        using var stream = OpenBounded(path, limit);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 24 });
    }

    private static FileStream OpenBounded(string path, int limit)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is > 0 && stream.Length <= limit) return stream;
        var length = stream.Length;
        stream.Dispose();
        throw new InvalidDataException(length == 0
            ? string.Format(UiLocalization.T("Input file is empty: {0}"), Path.GetFileName(path))
            : string.Format(UiLocalization.T("Input exceeds {0:F1} MiB ({1:F1} MiB): {2}"),
                limit / (1024d * 1024), length / (1024d * 1024), Path.GetFileName(path)));
    }
}
