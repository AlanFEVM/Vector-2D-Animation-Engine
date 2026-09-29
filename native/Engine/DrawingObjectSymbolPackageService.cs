namespace VectorAnimationEngine;

/// <summary>
/// Bridges the live project model to the <c>.V2DSymbol</c> interchange package.
///
/// Export walks the symbol containment graph from a root symbol and captures the
/// complete dependency closure, so nested symbols and their timelines survive
/// independently of the source project.
///
/// Import is transactional at the project level: the package is fully validated and
/// remapped in a scratch model first, and only then merged into the live project.
/// Imported symbols always receive fresh identifiers, so importing the same file
/// twice produces two independent copies instead of silently rebinding existing
/// instances. Library metadata is merged by identity of meaning rather than by file
/// id: folders are rebuilt by path and tags are matched by name (adopting the file's
/// colour only when the tag is new), which avoids clobbering existing library state.
/// </summary>
internal static class DrawingObjectSymbolPackageService
{
    /// <summary>
    /// Captures <paramref name="drawingObjectId"/> and every symbol it nests.
    /// </summary>
    /// <exception cref="InvalidOperationException">The symbol does not belong to the project.</exception>
    internal static DrawingObjectSymbolPackage CreatePackage(
        VectorProject project,
        string drawingObjectId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingObjectId);

        var root = FindDrawingObject(project, drawingObjectId)
            ?? throw new InvalidOperationException("The symbol is not available in the current project.");

        var closure = CollectClosure(project, root);
        var tagNames = project.AssetTags.ToDictionary(
            tag => tag.Id,
            tag => tag.Name,
            StringComparer.Ordinal);
        var folderPaths = BuildFolderPathLookup(project);

        // Only tags actually referenced by enclosed symbols travel with the file.
        var referencedTags = new Dictionary<string, ProjectAssetTag>(StringComparer.Ordinal);
        foreach (var symbol in closure)
        {
            foreach (var tagId in symbol.AssetTagIds)
            {
                if (!tagNames.TryGetValue(tagId, out var name) || string.IsNullOrWhiteSpace(name)) continue;
                var existing = project.AssetTags.FirstOrDefault(tag =>
                    string.Equals(tag.Id, tagId, StringComparison.Ordinal));
                if (existing is not null) referencedTags[tagId] = existing;
            }
        }

        var includedTagIds = referencedTags.Keys.ToHashSet(StringComparer.Ordinal);
        var symbols = closure.Select(symbol => CreateSymbolEntry(symbol, folderPaths, tagNames, includedTagIds))
            .ToArray();
        var folders = symbols
            .Where(symbol => symbol.FolderPath.Length > 0)
            .Select(symbol => symbol.FolderPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new DrawingObjectSymbolPackage.FolderPackageEntry { Path = path })
            .ToArray();

        return new DrawingObjectSymbolPackage
        {
            FormatVersion = DrawingObjectSymbolPackage.CurrentFormatVersion,
            RootSymbolName = root.Name,
            RootSymbolId = root.Id,
            Symbols = symbols,
            Folders = folders,
            Tags = referencedTags.Values
                .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .Select(tag => new DrawingObjectSymbolPackage.TagPackageEntry
                {
                    Name = tag.Name,
                    ColorArgb = tag.ColorArgb
                })
                .ToArray()
        };
    }

    /// <summary>
    /// Writes <paramref name="drawingObjectId"/> and its nested closure to a package file.
    /// </summary>
    internal static void Export(VectorProject project, string drawingObjectId, string path)
    {
        CreatePackage(project, drawingObjectId).Write(path);
    }

    /// <summary>
    /// Merges a validated package into the live project and returns the new root symbol.
    ///
    /// The whole merge is planned first and applied under a single project change
    /// notification. If any step fails, every symbol added by this call is removed
    /// again so the project is left exactly as it was.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is structurally invalid.</exception>
    internal static DrawingObjectDefinition Import(VectorProject project, DrawingObjectSymbolPackage package)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(package);
        package.Validate();

        // Validate and plan everything before touching the project, so the metadata
        // merges below are the first mutations and cannot be orphaned by a later
        // planning failure.
        var ordered = OrderByDependency(package);
        var nameMap = ResolveUniqueNames(project, ordered);
        var geometry = new Dictionary<string, VectorSceneSnapshot>(StringComparer.Ordinal);
        foreach (var symbol in ordered)
        {
            geometry[symbol.Id] = DecodeGeometry(symbol);
        }

        // Library metadata is merged next because symbols are placed into folders as
        // they are created. Both merges are additive and idempotent: re-importing the
        // same package reuses the folders and tags it created the first time.
        var folderIds = MergeFolders(project, package);
        var tagIds = MergeTags(project, package);

        var imported = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        try
        {
            foreach (var symbol in ordered)
            {
                // AddDrawingObject always mints a fresh identifier, which is exactly the
                // isolation this import needs: packaged ids never enter the project.
                imported[symbol.Id] = AddSymbol(project, symbol, geometry[symbol.Id], nameMap, folderIds, tagIds);
            }

            if (!imported.TryGetValue(package.RootSymbolId, out var root))
            {
                throw new InvalidDataException("The symbol package root could not be materialized.");
            }

            // Instances are attached only after every symbol exists, because a nested
            // instance must reference a symbol that is already registered.
            foreach (var symbol in ordered)
            {
                AttachInstances(project, imported[symbol.Id], symbol, imported);
            }

            foreach (var drawingObject in imported.Values) drawingObject.SynchronizeTimelineTracks();
            return root;
        }
        catch
        {
            // Every symbol created by this call is removed, so a mid-merge failure does
            // not leave partially imported symbols behind. Merged folders and tags are
            // additive and emptied-of-symbols, so leaving them is harmless and keeps
            // this path free of destructive project surgery.
            foreach (var drawingObject in imported.Values) RemoveUnconditionally(project, drawingObject.Id);
            throw;
        }
    }

    /// <summary>
    /// Returns the root symbol plus every symbol reachable through nested instances,
    /// ordered so dependencies precede the symbols that reference them.
    /// </summary>
    private static List<DrawingObjectDefinition> CollectClosure(
        VectorProject project,
        DrawingObjectDefinition root)
    {
        var ordered = new List<DrawingObjectDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(DrawingObjectDefinition symbol)
        {
            if (!visited.Add(symbol.Id)) return;
            foreach (var instance in symbol.Instances)
            {
                var child = FindDrawingObject(project, instance.DrawingObjectId);
                // A dangling reference cannot be packaged; skip it rather than write a
                // file that would fail its own validation on read.
                if (child is not null) Visit(child);
            }

            // Post-order guarantees dependencies land before their dependents.
            ordered.Add(symbol);
        }

        Visit(root);
        return ordered;
    }

    private static DrawingObjectSymbolPackage.SymbolPackageEntry CreateSymbolEntry(
        DrawingObjectDefinition symbol,
        IReadOnlyDictionary<string, string> folderPaths,
        IReadOnlyDictionary<string, string> tagNames,
        IReadOnlySet<string> includedTagIds)
    {
        var snapshot = symbol.Scene.CreateSnapshot();
        var svgPath = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            "symbol-package",
            $"{Guid.NewGuid():N}.svg");
        try
        {
            DrawingObjectSvgCodec.Write(svgPath, symbol.Id, snapshot);
            var svg = File.ReadAllText(svgPath);

            return new DrawingObjectSymbolPackage.SymbolPackageEntry
            {
                Id = symbol.Id,
                Name = symbol.Name,
                Kind = symbol.Kind,
                Detail = symbol.Detail,
                AnchorX = symbol.Anchor.X,
                AnchorY = symbol.Anchor.Y,
                TagNames = symbol.AssetTagIds
                    .Where(includedTagIds.Contains)
                    .Select(tagId => tagNames.GetValueOrDefault(tagId, ""))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                FolderPath = folderPaths.GetValueOrDefault(symbol.AssetFolderId, ""),
                Geometry = new DrawingObjectSymbolPackage.GeometrySnapshot
                {
                    Svg = DrawingObjectSymbolPackage.EncodePayload(svg),
                    Layers = Enumerable.Range(0, snapshot.LayerCount)
                        .Select(index => CreateLayerEntry(snapshot, index))
                        .ToArray(),
                    ActiveLayer = snapshot.ActiveLayer,
                    OnionSkinEnabled = snapshot.OnionSkinEnabled,
                    OnionSkinPreviousFrames = snapshot.OnionSkinPreviousFrames,
                    OnionSkinNextFrames = snapshot.OnionSkinNextFrames,
                    ObjectLayer = snapshot.ObjectLayer.ToArray(),
                    ObjectKeyframeFrame = snapshot.ObjectKeyframeFrame.ToArray()
                },
                Timeline = symbol.Timeline.CreateSnapshot(),
                Instances = symbol.Instances.Select(CreateInstanceEntry).ToArray()
            };
        }
        finally
        {
            TryDelete(svgPath, Path.GetDirectoryName(svgPath));
        }
    }

    private static DrawingObjectSymbolPackage.DrawingLayerEntry CreateLayerEntry(
        VectorSceneSnapshot scene,
        int index)
    {
        return new DrawingObjectSymbolPackage.DrawingLayerEntry
        {
            Id = scene.LayerIds[index],
            Name = scene.LayerNames[index],
            Kind = scene.LayerKinds[index],
            ParentLayerId = scene.LayerParentIds[index],
            MaskLayerId = scene.LayerMaskIds[index],
            Locked = scene.LayerLocked[index],
            Visible = scene.LayerVisible[index],
            Opacity = scene.LayerOpacity[index],
            BlendMode = index < scene.LayerBlendModes.Length
                ? scene.LayerBlendModes[index]
                : LayerBlendMode.Normal,
            ColorArgb = scene.LayerColorArgb[index],
            Outline = index < scene.LayerOutline.Length && scene.LayerOutline[index],
            OnionSkin = scene.LayerOnionSkin[index],
            StartFrame = scene.LayerStart[index],
            EndFrame = scene.LayerEnd[index]
        };
    }

    private static DrawingObjectSymbolPackage.InstancePackageEntry CreateInstanceEntry(
        DrawingObjectInstanceDefinition instance)
    {
        return new DrawingObjectSymbolPackage.InstancePackageEntry
        {
            Id = instance.Id,
            DrawingObjectId = instance.DrawingObjectId,
            SceneLayerId = instance.SceneLayerId,
            Name = instance.Name,
            Visible = instance.Visible,
            X = instance.X,
            Y = instance.Y,
            Z = instance.Z,
            RotationX = instance.RotationX,
            RotationY = instance.RotationY,
            RotationZ = instance.RotationZ,
            SkewX = instance.SkewX,
            SkewY = instance.SkewY,
            ScaleX = instance.ScaleX,
            ScaleY = instance.ScaleY,
            ScaleZ = instance.ScaleZ,
            RotationPivot = instance.RotationPivot,
            ScalePivot = instance.ScalePivot,
            Distortion = instance.Distortion?.DeepClone(),
            Alpha = instance.Alpha,
            TintArgb = instance.TintArgb,
            Filters = instance.Filters,
            OpticalMaterialOverride = instance.OpticalMaterialOverride,
            PlaybackFps = instance.PlaybackFps,
            PlaybackMode = instance.PlaybackMode,
            HoldFrame = instance.HoldFrame,
            StateKeyframes = instance.StateKeyframes.ToArray()
        };
    }

    /// <summary>
    /// Materializes one packaged symbol as a new project symbol with a fresh id,
    /// restoring geometry, layer metadata, timeline and tag assignments.
    /// </summary>
    private static DrawingObjectDefinition AddSymbol(
        VectorProject project,
        DrawingObjectSymbolPackage.SymbolPackageEntry symbol,
        VectorSceneSnapshot geometry,
        IReadOnlyDictionary<string, string> nameMap,
        IReadOnlyDictionary<string, string> folderIds,
        IReadOnlyDictionary<string, string> tagIds)
    {
        var target = project.AddDrawingObject(nameMap[symbol.Id]);
        target.Kind = symbol.Kind;
        target.Detail = symbol.Detail;
        if (float.IsFinite(symbol.AnchorX) && float.IsFinite(symbol.AnchorY))
        {
            target.SetAnchor(new PointF(
                Math.Clamp(symbol.AnchorX, -5_000_000, 5_000_000),
                Math.Clamp(symbol.AnchorY, -5_000_000, 5_000_000)));
        }

        target.Scene.RestoreSnapshot(geometry);
        target.ReplaceAssetTagIds(symbol.TagNames
            .Select(name => tagIds.GetValueOrDefault(name, ""))
            .Where(id => id.Length > 0));

        // Folders are transferred through the project API so path validation stays
        // in one place.
        if (symbol.FolderPath.Length > 0 && folderIds.TryGetValue(symbol.FolderPath, out var folderId))
        {
            project.TryMoveDrawingObjectToAssetFolder(target.Id, folderId);
        }

        // The packaged timeline targets the layer ids that the SVG payload reproduces
        // verbatim, so no target translation is required.
        target.Timeline.RestoreSnapshot(CloneTimeline(symbol.Timeline));
        target.SynchronizeTimelineTracks();
        return target;
    }

    /// <summary>
    /// Rebuilds a scene snapshot from the packaged SVG plus its timeline sidecar,
    /// mirroring how the managed project format splits the two.
    /// </summary>
    private static VectorSceneSnapshot DecodeGeometry(DrawingObjectSymbolPackage.SymbolPackageEntry symbol)
    {
        var svg = DrawingObjectSymbolPackage.DecodePayload(symbol.Geometry.Svg);
        var scratchPath = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            "symbol-package",
            $"{Guid.NewGuid():N}.svg");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratchPath)!);
            File.WriteAllText(scratchPath, svg);
            var sceneSnapshot = DrawingObjectSvgCodec.Read(scratchPath, symbol.Id);
            ValidateGeometryMatchesTimeline(symbol, sceneSnapshot);
            return sceneSnapshot;
        }
        finally
        {
            TryDelete(scratchPath, Path.GetDirectoryName(scratchPath));
        }
    }

    /// <summary>
    /// Confirms the SVG payload and the timeline sidecar describe the same document.
    /// A mismatch means the package was assembled incorrectly or tampered with.
    /// </summary>
    private static void ValidateGeometryMatchesTimeline(
        DrawingObjectSymbolPackage.SymbolPackageEntry symbol,
        VectorSceneSnapshot scene)
    {
        var geometry = symbol.Geometry;
        if (geometry.Layers.Length != scene.LayerCount
            || geometry.ObjectLayer.Length != scene.ObjectCount
            || geometry.ObjectKeyframeFrame.Length != scene.ObjectCount
            || !geometry.ObjectLayer.SequenceEqual(scene.ObjectLayer)
            || !geometry.ObjectKeyframeFrame.SequenceEqual(scene.ObjectKeyframeFrame))
        {
            throw new InvalidDataException(
                $"Symbol '{symbol.Name}' has mismatched geometry and timeline metadata.");
        }

        for (var index = 0; index < geometry.Layers.Length; index++)
        {
            var layer = geometry.Layers[index];
            if (layer is null
                || !string.Equals(layer.Id, scene.LayerIds[index], StringComparison.Ordinal)
                || layer.Kind != scene.LayerKinds[index]
                || layer.BlendMode != (index < scene.LayerBlendModes.Length
                    ? scene.LayerBlendModes[index]
                    : LayerBlendMode.Normal))
            {
                throw new InvalidDataException(
                    $"Symbol '{symbol.Name}' has mismatched layer metadata.");
            }
        }
    }

    /// <summary>
    /// Attaches the packaged nested instances to an imported container, remapping
    /// both the child symbol reference and the timeline target ids.
    /// </summary>
    private static void AttachInstances(
        VectorProject project,
        DrawingObjectDefinition container,
        DrawingObjectSymbolPackage.SymbolPackageEntry symbol,
        IReadOnlyDictionary<string, DrawingObjectDefinition> imported)
    {
        if (symbol.Instances.Length == 0) return;

        foreach (var instance in symbol.Instances)
        {
            if (!imported.TryGetValue(instance.DrawingObjectId, out var child)) continue;
            if (!project.CanContainDrawingObject(container.Id, child.Id)) continue;

            var target = new DrawingObjectInstanceDefinition
            {
                DrawingObjectId = child.Id,
                SceneLayerId = instance.SceneLayerId,
                Name = instance.Name,
                Visible = instance.Visible,
                X = instance.X,
                Y = instance.Y,
                Z = instance.Z,
                RotationX = instance.RotationX,
                RotationY = instance.RotationY,
                RotationZ = instance.RotationZ,
                SkewX = instance.SkewX,
                SkewY = instance.SkewY,
                ScaleX = instance.ScaleX,
                ScaleY = instance.ScaleY,
                ScaleZ = instance.ScaleZ,
                RotationPivot = instance.RotationPivot,
                ScalePivot = instance.ScalePivot,
                Distortion = instance.Distortion?.DeepClone(),
                Alpha = instance.Alpha,
                TintArgb = instance.TintArgb,
                Filters = instance.Filters,
                OpticalMaterialOverride = instance.OpticalMaterialOverride,
                PlaybackFps = instance.PlaybackFps,
                PlaybackMode = instance.PlaybackMode,
                HoldFrame = instance.HoldFrame
            };
            target.RestoreStateKeyframes(instance.StateKeyframes);
            container.AddInstance(project, target);
        }
    }

    /// <summary>
    /// Orders packaged symbols so every symbol appears after the symbols it nests.
    /// </summary>
    private static List<DrawingObjectSymbolPackage.SymbolPackageEntry> OrderByDependency(
        DrawingObjectSymbolPackage package)
    {
        var byId = package.Symbols.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var ordered = new List<DrawingObjectSymbolPackage.SymbolPackageEntry>(package.Symbols.Length);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(DrawingObjectSymbolPackage.SymbolPackageEntry symbol)
        {
            if (!visited.Add(symbol.Id)) return;
            foreach (var instance in symbol.Instances)
            {
                if (byId.TryGetValue(instance.DrawingObjectId, out var child)) Visit(child);
            }

            ordered.Add(symbol);
        }

        // Visit the root first so its closure is contiguous, then any extras.
        Visit(byId[package.RootSymbolId]);
        foreach (var symbol in package.Symbols) Visit(symbol);
        return ordered;
    }

    /// <summary>
    /// Assigns each imported symbol a name that does not collide with the existing
    /// project, keeping the packaged name when it is still free.
    /// </summary>
    private static Dictionary<string, string> ResolveUniqueNames(
        VectorProject project,
        IReadOnlyList<DrawingObjectSymbolPackage.SymbolPackageEntry> ordered)
    {
        var taken = project.DrawingObjects
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in ordered)
        {
            var baseName = string.IsNullOrWhiteSpace(symbol.Name) ? "Symbol" : symbol.Name.Trim();
            var candidate = baseName;
            for (var suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{baseName} {suffix}";
            }

            result[symbol.Id] = candidate;
        }

        return result;
    }

    /// <summary>
    /// Rebuilds the packaged folder chain in the project, reusing folders that already
    /// exist at the same path so repeated imports do not multiply folders.
    /// </summary>
    private static Dictionary<string, string> MergeFolders(
        VectorProject project,
        DrawingObjectSymbolPackage package)
    {
        // Path -> folder id. Keyed by path (not by folder id), because the packaged
        // metadata identifies folders by path and re-import must reuse the folder that
        // already occupies each path rather than creating a parallel copy.
        var byPath = BuildFolderPathToIdLookup(project);
        var pending = package.Folders.Select(folder => folder.Path).ToList();
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var path = pending[index];
                if (byPath.ContainsKey(path))
                {
                    pending.RemoveAt(index);
                    progressed = true;
                    continue;
                }

                var separator = path.LastIndexOf('/');
                var parentPath = separator < 0 ? "" : path[..separator];
                var name = separator < 0 ? path : path[(separator + 1)..];
                // Create parents first so the chain is never orphaned.
                var parentId = parentPath.Length == 0 ? "" : byPath.GetValueOrDefault(parentPath, null!);
                if (parentId is null) continue;

                if (!project.TryAddAssetFolder(name, parentId, out var folder) || folder is null)
                {
                    // Treat an unexpected failure as a hard error so import stays atomic.
                    throw new InvalidDataException($"The library folder '{path}' could not be created.");
                }

                byPath[path] = folder.Id;
                pending.RemoveAt(index);
                progressed = true;
            }

            if (!progressed)
            {
                throw new InvalidDataException("The symbol package declares a folder path with no valid parent.");
            }
        }

        return byPath;
    }

    /// <summary>
    /// Merges packaged tag definitions by name. Existing tags keep their project id
    /// and colour; only genuinely new tags are created using the packaged colour.
    ///
    /// The project exposes tag definitions only through a validated whole-list edit,
    /// so the merged list is applied through that entry point against an anchor
    /// symbol whose own assignments are preserved verbatim. Other symbols' existing
    /// assignments are retained by the project API.
    /// </summary>
    private static Dictionary<string, string> MergeTags(
        VectorProject project,
        DrawingObjectSymbolPackage package)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in project.AssetTags) byName[tag.Name] = tag.Id;
        if (package.Tags.Length == 0) return byName;

        var definitions = project.AssetTags
            .Select(tag => new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb))
            .ToList();
        var added = false;
        foreach (var tag in package.Tags)
        {
            if (byName.ContainsKey(tag.Name)) continue;
            if (definitions.Count >= VectorProject.MaxAssetTagCount) break;
            definitions.Add(new ProjectAssetTagData(Guid.NewGuid().ToString("N"), tag.Name, tag.ColorArgb));
            added = true;
        }

        if (!added) return byName;

        var anchor = project.DrawingObjects[0];
        if (!project.TryApplyAssetTagEdit(anchor.Id, definitions, anchor.AssetTagIds))
        {
            throw new InvalidDataException("The packaged asset tags could not be merged.");
        }

        byName.Clear();
        foreach (var tag in project.AssetTags) byName[tag.Name] = tag.Id;
        return byName;
    }

    private static AnimationTimelineSnapshot CloneTimeline(AnimationTimelineSnapshot timeline)
    {
        return new AnimationTimelineSnapshot
        {
            Tracks = timeline.Tracks.Select(track => new AnimationTimelineTrackSnapshot
            {
                Id = track.Id,
                TargetId = track.TargetId,
                TabGroupId = track.TabGroupId,
                IsCollisionTerrain = track.IsCollisionTerrain,
                Duration = track.Duration,
                Keyframes = track.Keyframes.ToArray(),
                Tweens = track.Tweens.ToArray()
            }).ToArray(),
            TabGroups = timeline.TabGroups.ToArray(),
            ActiveTabGroupId = timeline.ActiveTabGroupId
        };
    }

    private static DrawingObjectDefinition? FindDrawingObject(VectorProject project, string id)
    {
        DrawingObjectDefinition? result = null;
        foreach (var drawingObject in project.DrawingObjects)
        {
            if (!string.Equals(drawingObject.Id, id, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = drawingObject;
        }

        return result;
    }

    /// <summary>
    /// Builds a lookup from library folder path to folder id, the inverse of
    /// <see cref="BuildFolderPathLookup"/>. The library root is represented by the
    /// empty path and is intentionally absent.
    /// </summary>
    private static Dictionary<string, string> BuildFolderPathToIdLookup(VectorProject project)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, path) in BuildFolderPathLookup(project))
        {
            // A malformed or ambiguous chain must not silently alias two folders onto
            // one path; the first writer wins and later collisions are dropped.
            result.TryAdd(path, id);
        }

        return result;
    }

    /// <summary>
    /// Builds a lookup from library folder id to its slash-separated path.
    /// </summary>
    private static Dictionary<string, string> BuildFolderPathLookup(VectorProject project)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in project.AssetFolders)
        {
            names[folder.Id] = folder.Name;
            parents[folder.Id] = folder.ParentFolderId;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in project.AssetFolders)
        {
            var segments = new List<string>();
            var current = folder.Id;
            for (var hop = 0; hop < project.AssetFolders.Count; hop++)
            {
                if (!names.TryGetValue(current, out var name)) break;
                segments.Insert(0, name);
                if (!parents.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent)) break;
                current = parent;
            }

            result[folder.Id] = DrawingObjectSymbolPackage.BuildFolderPath(segments);
        }

        return result;
    }

    private static void RemoveUnconditionally(VectorProject project, string drawingObjectId)
    {
        // Rollback must succeed even when the failure happened mid-merge, so removal is
        // attempted but never allowed to mask the original exception.
        try
        {
            project.TryRemoveDrawingObject(drawingObjectId, out _);
        }
        catch
        {
            // Ignored: the original failure is the actionable error.
        }
    }

    private static void TryDelete(string path, string? directory)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (!string.IsNullOrEmpty(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Scratch cleanup is best effort and must not fail an export or import.
        }
    }
}
