namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    /// <summary>
    /// Covers the re-importable <c>.V2DSymbol</c> package: nested symbol closure with
    /// timeline state, library folder path, tag names and tag colours, plus the
    /// identity rules that keep repeated imports independent.
    /// </summary>
    private static void RunSymbolPackageRegression()
    {
        var project = VectorProject.CreateEmpty();
        var root = project.DrawingObjects[0];
        root.Name = "Package Root";

        // Root geometry: two layers so layer metadata must travel with the file.
        root.Scene.CreateEmpty(2, 12);
        root.Scene.AddObject(0, new PointF(-60, 0), new SizeF(90, 60), 0, 8, Color.Coral, 0, ShapeKind.Rectangle);
        root.Scene.AddObject(1, new PointF(70, 20), new SizeF(50, 40), 0, 6, Color.Teal, 0, ShapeKind.Ellipse);

        var child = project.AddDrawingObject("Package Child");
        child.Scene.CreateEmpty(1, 12);
        child.Scene.AddObject(0, PointF.Empty, new SizeF(64, 48), 0, 6, Color.Gold, 0, ShapeKind.Rectangle);

        // The child animates, so the timeline must survive the round trip.
        child.Scene.Timeline.InsertKeyframe(child.Scene.LayerIds[0], 0);
        var childFrame = Math.Min(6, child.Scene.FrameCount - 1);
        child.Scene.Timeline.InsertKeyframe(child.Scene.LayerIds[0], childFrame);

        // Nested instance relationship: root contains child.
        if (!project.TryAddDrawingObjectInstance(root.Id, child.Id, new PointF(40, 24), out var rootToChild)
            || rootToChild is null)
        {
            throw new InvalidOperationException("Symbol package regression could not nest its child symbol.");
        }

        rootToChild.RotationZ = 32;
        rootToChild.ScaleX = 1.5f;
        rootToChild.Alpha = 0.75f;
        rootToChild.Name = "Nested Child Instance";

        // Library metadata: a two-level folder chain plus a coloured tag.
        if (!project.TryAddAssetFolder("Characters", "", out var characters)
            || characters is null
            || !project.TryAddAssetFolder("Heroes", characters.Id, out var heroes)
            || heroes is null
            || !project.TryMoveDrawingObjectToAssetFolder(root.Id, heroes.Id)
            || !project.TryMoveDrawingObjectToAssetFolder(child.Id, characters.Id))
        {
            throw new InvalidOperationException("Symbol package regression could not create its library folders.");
        }

        var tagColor = Color.FromArgb(255, 233, 90, 120).ToArgb();
        var tagId = Guid.NewGuid().ToString("N");
        if (!project.TryApplyAssetTagEdit(
                root.Id,
                [new ProjectAssetTagData(tagId, "Hero", tagColor)],
                [tagId]))
        {
            throw new InvalidOperationException("Symbol package regression could not create its asset tag.");
        }

        var temporaryRoot = CreateTemporaryDirectory("symbol-package");
        try
        {
            var packagePath = Path.Combine(
                temporaryRoot,
                "Package Root" + DrawingObjectSymbolPackage.FileExtension);
            DrawingObjectSymbolPackageService.Export(project, root.Id, packagePath);
            AssertTimeline(File.Exists(packagePath), "Symbol package export did not create a file.");
            RunSymbolPackageDropRegression(packagePath);

            var package = DrawingObjectSymbolPackage.Read(packagePath);
            AssertTimeline(
                package.Symbols.Length == 2 && package.RootSymbolId == root.Id,
                "Symbol package did not capture the complete nested symbol closure.");
            AssertTimeline(
                package.Symbols.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(["Package Child", "Package Root"]),
                "Symbol package did not preserve symbol names.");
            AssertTimeline(
                package.Folders.Select(folder => folder.Path).OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(["Characters", "Characters/Heroes"]),
                "Symbol package did not preserve the library folder path chain.");
            AssertTimeline(
                package.Tags.Length == 1
                && package.Tags[0].Name == "Hero"
                && package.Tags[0].ColorArgb == tagColor,
                "Symbol package did not preserve the tag name and colour.");

            var packagedRoot = package.Symbols.Single(item => item.Id == root.Id);
            var packagedChild = package.Symbols.Single(item => item.Id == child.Id);
            AssertTimeline(
                packagedRoot.TagNames.SequenceEqual(["Hero"])
                && packagedRoot.FolderPath == "Characters/Heroes"
                && packagedChild.FolderPath == "Characters",
                "Symbol package did not preserve per-symbol tag assignment or folder path.");
            AssertTimeline(
                packagedRoot.Instances.Length == 1
                && packagedRoot.Instances[0].DrawingObjectId == child.Id
                && Math.Abs(packagedRoot.Instances[0].RotationZ - 32) < 0.001f
                && packagedRoot.Instances[0].Name == "Nested Child Instance",
                "Symbol package did not preserve the nested instance transform and reference.");

            // Import into a fresh project that has never seen these symbols.
            var target = VectorProject.CreateEmpty();
            var imported = DrawingObjectSymbolPackageService.Import(target, package);
            AssertTimeline(
                target.DrawingObjects.Count == 3,
                "Symbol package import did not create the complete symbol closure.");

            // Imported symbols must receive fresh identifiers and be independent.
            AssertTimeline(
                !string.Equals(imported.Id, root.Id, StringComparison.Ordinal)
                && target.DrawingObjects.All(item => item.Id != root.Id && item.Id != child.Id),
                "Symbol package import reused packaged identifiers.");

            AssertTimeline(
                imported.Name == "Package Root" && imported.Instances.Count == 1,
                "Symbol package import did not restore the root symbol and its nested instance.");

            var importedChild = target.DrawingObjects.Single(item =>
                string.Equals(item.Id, imported.Instances[0].DrawingObjectId, StringComparison.Ordinal));
            AssertTimeline(
                importedChild.Name == "Package Child" && importedChild.Scene.ObjectCount == 1,
                "Symbol package import did not restore the nested child geometry.");
            AssertTimeline(
                Math.Abs(imported.Instances[0].RotationZ - 32) < 0.001f
                && Math.Abs(imported.Instances[0].ScaleX - 1.5f) < 0.001f
                && Math.Abs(imported.Instances[0].Alpha - 0.75f) < 0.001f,
                "Symbol package import did not restore the nested instance transform.");

            // Layer structure and cel ownership must match the source symbol. The live
            // scene keeps over-allocated backing arrays, so compare against its snapshot.
            var sourceSnapshot = root.Scene.CreateSnapshot();
            AssertTimeline(
                imported.Scene.LayerCount == root.Scene.LayerCount
                && imported.Scene.ObjectCount == root.Scene.ObjectCount
                && imported.Scene.CreateSnapshot().ObjectLayer.SequenceEqual(sourceSnapshot.ObjectLayer)
                && imported.Scene.CreateSnapshot().ObjectKeyframeFrame.SequenceEqual(sourceSnapshot.ObjectKeyframeFrame),
                "Symbol package import did not restore layer and cel ownership.");

            // The child's animation timeline must survive.
            AssertTimeline(
                importedChild.Scene.FrameCount == child.Scene.FrameCount
                && importedChild.Scene.Timeline.Tracks.Count > 0,
                "Symbol package import did not restore the nested symbol timeline.");

            // Library metadata merges by meaning: the folder chain is rebuilt and the
            // tag is matched by name rather than duplicated.
            var importedFolder = target.AssetFolders.SingleOrDefault(folder => folder.Name == "Heroes");
            AssertTimeline(
                importedFolder is not null
                && target.AssetFolders.Any(folder => string.Equals(folder.Id, importedFolder.ParentFolderId, StringComparison.Ordinal)),
                "Symbol package import did not rebuild the library folder path chain.");
            AssertTimeline(
                imported.AssetFolderId == importedFolder!.Id,
                "Symbol package import did not place the root symbol in its library folder.");
            AssertTimeline(
                target.AssetTags.Count == 1
                && target.AssetTags[0].Name == "Hero"
                && target.AssetTags[0].ColorArgb == tagColor
                && imported.AssetTagIds.SequenceEqual([target.AssetTags[0].Id]),
                "Symbol package import did not merge the asset tag by name and colour.");

            // Importing again must produce a second, independent copy that reuses the
            // existing folder and tag instead of multiplying library entries.
            var second = DrawingObjectSymbolPackageService.Import(target, package);
            AssertTimeline(
                !string.Equals(second.Id, imported.Id, StringComparison.Ordinal)
                && target.DrawingObjects.Count == 5,
                "Importing the same package twice did not produce an independent copy.");
            AssertTimeline(
                second.Name == "Package Root 2",
                "Importing a duplicate symbol name did not disambiguate the new symbol name.");
            AssertTimeline(
                target.AssetFolders.Count == 2 && target.AssetTags.Count == 1,
                "Repeated symbol package import duplicated library folders or tags.");

            // Validation must reject tampered packages before any project mutation.
            var mismatched = CopyPackage(package, symbols:
            [
                CopySymbol(package.Symbols[0], geometry: CopyGeometry(package.Symbols[0].Geometry, objectLayer: [99])),
                package.Symbols[1]
            ]);
            ExpectInvalidData(
                () => DrawingObjectSymbolPackageService.Import(VectorProject.CreateEmpty(), mismatched),
                "Symbol package import accepted mismatched geometry and timeline metadata.");

            var dangling = CopyPackage(package, symbols:
            [
                CopySymbol(
                    package.Symbols.Single(item => item.Id == root.Id),
                    instances:
                    [
                        CopyInstance(
                            packagedRoot.Instances[0],
                            drawingObjectId: "missing-symbol")
                    ]),
                package.Symbols[1]
            ]);
            ExpectInvalidData(
                () => DrawingObjectSymbolPackage.Read(
                    WriteTempPackage(temporaryRoot, "dangling", dangling)),
                "Symbol package read accepted a dangling nested symbol reference.");

            // A self-referential package would recurse forever during composition.
            var selfReferential = CopyPackage(package, symbols:
            [
                CopySymbol(
                    package.Symbols.Single(item => item.Id == root.Id),
                    instances:
                    [
                        CopyInstance(packagedRoot.Instances[0], drawingObjectId: packagedRoot.Id)
                    ]),
                package.Symbols[1]
            ]);
            ExpectInvalidData(
                () => DrawingObjectSymbolPackage.Read(
                    WriteTempPackage(temporaryRoot, "self", selfReferential)),
                "Symbol package read accepted a self-referential symbol relationship.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static void RunSymbolPackageDropRegression(string packagePath)
    {
        var secondPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "second.V2DSYMBOL");
        File.Copy(packagePath, secondPath);
        var directoryPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "folder.V2DSymbol");
        Directory.CreateDirectory(directoryPath);
        var unrelatedPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "unrelated.svg");
        File.WriteAllText(unrelatedPath, "<svg />");

        static DataObject FileDrop(params string[] paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            return data;
        }

        var multiple = FileDrop(packagePath, secondPath, packagePath.ToUpperInvariant());
        AssertTimeline(
            LibraryVaultPanel.TryResolveDroppedSymbolFiles(multiple, out var resolved)
            && resolved.SequenceEqual([packagePath, secondPath]),
            "Symbol file drop must accept case-insensitive extensions and preserve distinct file order.");
        AssertTimeline(
            MainForm.TryResolveDroppedSvgFile(FileDrop(unrelatedPath), out var svgPath) && svgPath == unrelatedPath,
            "Symbol file drop changed the existing SVG file-drop recognition.");

        var dragEnter = RequireMethod(typeof(Control), "OnDragEnter", [typeof(DragEventArgs)]);
        var dragOver = RequireMethod(typeof(Control), "OnDragOver", [typeof(DragEventArgs)]);
        var dragDrop = RequireMethod(typeof(Control), "OnDragDrop", [typeof(DragEventArgs)]);
        static DragEventArgs Drag(IDataObject? data, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move) =>
            new(data, 0, 0, 0, allowed, DragDropEffects.None);

        using (var unbound = new LibraryVaultPanel())
        {
            var unboundTree = (TreeView)RequireField(typeof(LibraryVaultPanel), "_projectObjects").GetValue(unbound)!;
            var unboundDrag = Drag(multiple);
            var requested = false;
            unbound.DrawingObjectSymbolImportRequested += (_, _) => requested = true;
            dragEnter.Invoke(unboundTree, [unboundDrag]);
            dragDrop.Invoke(unboundTree, [unboundDrag]);
            AssertTimeline(unboundDrag.Effect == DragDropEffects.None && !requested,
                "An unbound asset library accepted a symbol file drop.");
        }

        // Exercise the real panel event and MainForm import subscription without
        // opening a file picker or running an interactive OLE drag loop.
        using var form = new MainForm();
        var panel = (LibraryVaultPanel)RequireField(typeof(MainForm), "_libraryVaultPanel").GetValue(form)!;
        var tree = (TreeView)RequireField(typeof(LibraryVaultPanel), "_projectObjects").GetValue(panel)!;
        var target = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var originalIds = target.DrawingObjects.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);
        var importRequests = 0;
        IReadOnlyList<string> requestedFiles = [];
        panel.DrawingObjectSymbolImportRequested += (_, e) =>
        {
            importRequests++;
            requestedFiles = e.FileNames;
        };

        foreach (var invalid in new IDataObject?[]
        {
            null, new DataObject(DataFormats.Text, packagePath), FileDrop(), FileDrop(""),
            FileDrop(directoryPath), FileDrop(packagePath, unrelatedPath),
            FileDrop(Path.Combine(Path.GetDirectoryName(packagePath)!, "missing.V2DSymbol"))
        })
        {
            var rejected = Drag(invalid);
            dragEnter.Invoke(tree, [rejected]);
            dragOver.Invoke(tree, [rejected]);
            dragDrop.Invoke(tree, [rejected]);
            AssertTimeline(rejected.Effect == DragDropEffects.None && importRequests == 0,
                "The asset library accepted an unrelated, mixed, missing or non-file drop.");
        }

        // A lone SVG is an external-SVG link drop handled by its own importer; the symbol
        // importer must not claim it.
        AssertTimeline(
            LibraryVaultPanel.TryResolveDroppedAssetFiles(FileDrop(unrelatedPath), out var svgDrop)
            && svgDrop.SvgFiles.SequenceEqual([unrelatedPath])
            && svgDrop.SymbolFiles.Length == 0
            && !LibraryVaultPanel.TryResolveDroppedSymbolFiles(FileDrop(unrelatedPath), out _),
            "A lone SVG drop must be classified as an external-SVG link, not a symbol package.");

        var moveOnly = Drag(multiple, DragDropEffects.Move);
        dragEnter.Invoke(tree, [moveOnly]);
        dragDrop.Invoke(tree, [moveOnly]);
        AssertTimeline(moveOnly.Effect == DragDropEffects.None && importRequests == 0,
            "Symbol files must be copied into the project, never moved from disk.");

        var single = Drag(FileDrop(packagePath));
        dragEnter.Invoke(tree, [single]);
        dragOver.Invoke(tree, [single]);
        AssertTimeline(single.Effect == DragDropEffects.Copy && target.DrawingObjects.Count == originalIds.Count,
            "Symbol drag feedback must advertise Copy without importing during hover.");
        dragDrop.Invoke(tree, [single]);
        var firstRoot = (DrawingObjectDefinition)RequireMethod(typeof(MainForm), "ActiveDrawingObject").Invoke(form, null)!;
        AssertTimeline(importRequests == 1 && requestedFiles.SequenceEqual([packagePath])
            && target.DrawingObjects.Count == originalIds.Count + 2
            && !originalIds.Contains(firstRoot.Id) && firstRoot.Instances.Count == 1,
            "Dropping a symbol file did not import its dependency closure and open its root.");

        var batch = Drag(multiple);
        dragEnter.Invoke(tree, [batch]);
        dragDrop.Invoke(tree, [batch]);
        var lastRoot = (DrawingObjectDefinition)RequireMethod(typeof(MainForm), "ActiveDrawingObject").Invoke(form, null)!;
        AssertTimeline(importRequests == 2 && requestedFiles.SequenceEqual([packagePath, secondPath])
            && target.DrawingObjects.Count == originalIds.Count + 6
            && lastRoot.Id != firstRoot.Id && lastRoot.Instances.Count == 1
            && File.Exists(packagePath) && File.Exists(secondPath),
            "A multi-file drop did not import independent copies and select the last root.");

        // Existing project-local moves still dispatch their original typed events.
        ProjectAssetMoveRequestedEventArgs? movedSymbol = null;
        panel.DrawingObjectMoveRequested += (_, e) => movedSymbol = e;
        var internalSymbol = new DataObject();
        internalSymbol.SetData(typeof(DrawingObjectDragData), new DrawingObjectDragData(target.Id, firstRoot.Id));
        var symbolMove = Drag(internalSymbol);
        dragEnter.Invoke(tree, [symbolMove]);
        dragDrop.Invoke(tree, [symbolMove]);
        AssertTimeline(symbolMove.Effect == DragDropEffects.Move && movedSymbol?.AssetId == firstRoot.Id
            && importRequests == 2, "Symbol file drop broke project-local symbol moves.");

        ProjectAssetMoveRequestedEventArgs? movedFolder = null;
        panel.AssetFolderMoveRequested += (_, e) => movedFolder = e;
        var folder = target.AssetFolders.Single(item => item.Name == "Heroes");
        var internalFolder = new DataObject();
        internalFolder.SetData(typeof(ProjectAssetFolderDragData), new ProjectAssetFolderDragData(target.Id, folder.Id));
        var folderMove = Drag(internalFolder);
        dragEnter.Invoke(tree, [folderMove]);
        dragDrop.Invoke(tree, [folderMove]);
        AssertTimeline(folderMove.Effect == DragDropEffects.Move && movedFolder?.AssetId == folder.Id
            && importRequests == 2, "Symbol file drop broke project-local folder moves.");
        Console.WriteLine("symbol_package_vault_drop_regression=passed");
    }

    private static string WriteTempPackage(
        string directory,
        string name,
        DrawingObjectSymbolPackage package)
    {
        var path = Path.Combine(directory, $"{name}{DrawingObjectSymbolPackage.FileExtension}");
        package.Write(path);
        return path;
    }

    private static DrawingObjectSymbolPackage CopyPackage(
        DrawingObjectSymbolPackage source,
        DrawingObjectSymbolPackage.SymbolPackageEntry[] symbols) => new()
    {
        FormatVersion = source.FormatVersion,
        RootSymbolName = source.RootSymbolName,
        RootSymbolId = source.RootSymbolId,
        Symbols = symbols,
        Folders = source.Folders,
        Tags = source.Tags
    };

    private static DrawingObjectSymbolPackage.SymbolPackageEntry CopySymbol(
        DrawingObjectSymbolPackage.SymbolPackageEntry source,
        DrawingObjectSymbolPackage.GeometrySnapshot? geometry = null,
        DrawingObjectSymbolPackage.InstancePackageEntry[]? instances = null) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Kind = source.Kind,
        Detail = source.Detail,
        AnchorX = source.AnchorX,
        AnchorY = source.AnchorY,
        TagNames = source.TagNames,
        FolderPath = source.FolderPath,
        Geometry = geometry ?? source.Geometry,
        Timeline = source.Timeline,
        Instances = instances ?? source.Instances
    };

    private static DrawingObjectSymbolPackage.GeometrySnapshot CopyGeometry(
        DrawingObjectSymbolPackage.GeometrySnapshot source,
        ushort[] objectLayer) => new()
    {
        Svg = source.Svg,
        Layers = source.Layers,
        ActiveLayer = source.ActiveLayer,
        OnionSkinEnabled = source.OnionSkinEnabled,
        OnionSkinPreviousFrames = source.OnionSkinPreviousFrames,
        OnionSkinNextFrames = source.OnionSkinNextFrames,
        ObjectLayer = objectLayer,
        ObjectKeyframeFrame = source.ObjectKeyframeFrame
    };

    private static DrawingObjectSymbolPackage.InstancePackageEntry CopyInstance(
        DrawingObjectSymbolPackage.InstancePackageEntry source,
        string drawingObjectId) => new()
    {
        Id = source.Id,
        DrawingObjectId = drawingObjectId,
        SceneLayerId = source.SceneLayerId,
        Name = source.Name,
        Visible = source.Visible,
        X = source.X,
        Y = source.Y,
        Z = source.Z,
        RotationX = source.RotationX,
        RotationY = source.RotationY,
        RotationZ = source.RotationZ,
        SkewX = source.SkewX,
        SkewY = source.SkewY,
        ScaleX = source.ScaleX,
        ScaleY = source.ScaleY,
        ScaleZ = source.ScaleZ,
        RotationPivot = source.RotationPivot,
        ScalePivot = source.ScalePivot,
        Distortion = source.Distortion,
        Alpha = source.Alpha,
        TintArgb = source.TintArgb,
        OpticalMaterialOverride = source.OpticalMaterialOverride,
        PlaybackFps = source.PlaybackFps,
        PlaybackMode = source.PlaybackMode,
        HoldFrame = source.HoldFrame,
        StateKeyframes = source.StateKeyframes
    };
}
