using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunPanelUsabilityRegression()
    {
        RunPanelNumericDraftRegression();
        var directory = CreateTemporaryDirectory("panel-usability");
        try
        {
            const string svgSource = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 12"><rect width="16" height="12" fill="#309080"/></svg>""";
            var imagePath = Path.Combine(directory, "panel.png");
            var svgPath = Path.Combine(directory, "panel.svg");
            WriteFixturePng(imagePath, 32, 24);
            File.WriteAllText(svgPath, svgSource);
            var project = VectorProject.CreateEmpty();
            var root = project.DrawingObjects[0];
            root.Scene.CreateEmpty(frameCount: 12);
            AssertTimeline(project.TryAddAssetFolder("Media", "", out var parent) && parent is not null,
                "Panel metadata fixture could not create its parent folder.");
            AssertTimeline(project.TryAddAssetFolder("Resources only", parent!.Id, out var folder) && folder is not null,
                "Panel metadata fixture could not create its resource-only leaf folder.");
            AssertTimeline(project.TryAddImageAssetFromSource("Panel image", imagePath,
                    BitmapImageRasterizer.ComputeSha256(imagePath), 32, 24, 96,
                    BitmapImageImportSettings.Default, out var image) && image is not null,
                "Panel metadata fixture could not import its image.");
            AssertTimeline(project.TryAddExternalSvgAsset("Panel SVG", svgPath, "",
                    BitmapImageRasterizer.ComputeSha256(svgPath), out var svg) && svg is not null,
                "Panel metadata fixture could not import its SVG.");
            var tags = new[]
            {
                new ProjectAssetTagData(Guid.NewGuid().ToString("N"), "Resource tag", Color.Coral.ToArgb()),
                new ProjectAssetTagData(Guid.NewGuid().ToString("N"), "Shared tag", Color.Teal.ToArgb())
            };
            AssertTimeline(project.TryApplyLibraryAssetTagEdit([image!.Id, svg!.Id], tags, tags.Select(tag => tag.Id))
                && project.TryMoveLibraryAssetToFolder(image.Id, folder!.Id)
                && project.TryMoveLibraryAssetToFolder(svg.Id, folder.Id),
                "Mixed image/SVG metadata editing failed.");
            root.Scene.AddBitmapObject(0, new PointF(30, 20), new BitmapObjectData
            {
                ImageAssetId = image.Id, PlacedSize = new SizeF(32, 24),
                VisibleContours = [new[] { PointF.Empty, new PointF(1, 0), new PointF(1, 1) }]
            });
            var svgObject = root.Scene.AddImportedSvgObject(0, new PointF(80, 20), new SizeF(16, 12), 0.25f,
                svgSource, "Panel SVG");
            AssertTimeline(root.Scene.TrySetImportedSvgAsset(svgObject, svg.Id), "Panel fixture lost its SVG resource reference.");
            root.SynchronizeTimelineTracks();

            var beforeInvalidEdit = PanelProjectSignature(project);
            var notifications = 0;
            project.Changed += CountChange;
            void CountChange(object? sender, EventArgs args) => notifications++;
            AssertTimeline(!project.TryApplyLibraryAssetTagEdit([image.Id, "missing-asset"],
                    [tags[0] with { Name = "Must not leak" }], [tags[0].Id])
                && notifications == 0 && PanelProjectSignature(project) == beforeInvalidEdit,
                "A mixed tag edit with a stale selection partially changed the project or published a change.");
            project.Changed -= CountChange;

            var restart = VectorProject.RestoreRestartSnapshot(
                EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
            AssertPanelResourceMetadata(restart, image.Id, svg.Id, folder!.Id, tags.Select(tag => tag.Id).ToArray());

            var packagePath = Path.Combine(directory, "ResourceMetadata.V2DSymbol");
            DrawingObjectSymbolPackageService.Export(project, root.Id, packagePath);
            var package = DrawingObjectSymbolPackage.Read(packagePath);
            AssertTimeline(package.Tags.Length == 2 && package.Symbols[0].TagNames.Length == 0
                && package.ImageAssets.Single().FolderPath == "Media/Resources only"
                && package.ExternalSvgAssets.Single().TagNames.SequenceEqual(tags.Select(tag => tag.Name)),
                "Resource-only tags or nested folders were omitted from the exported symbol package.");
            var target = VectorProject.CreateEmpty();
            var imported = DrawingObjectSymbolPackageService.Import(target, package);
            var importedImage = target.ImageAssets.Single();
            var importedSvg = target.ExternalSvgAssets.Single();
            var importedLeaf = target.AssetFolders.Single(item => item.Name == "Resources only");
            var importedParent = target.AssetFolders.Single(item => item.Name == "Media");
            AssertTimeline(importedLeaf.ParentFolderId == importedParent.Id
                && importedImage.Id != image.Id && importedSvg.Id != svg.Id
                && imported.Scene.FindBitmapObjectsUsingImageAsset(importedImage.Id).Length == 1
                && imported.Scene.FindImportedSvgObjectsUsingAsset(importedSvg.Id).Length == 1,
                "Resource metadata import did not recreate ancestors or remap fresh resource identities.");
            AssertPanelResourceMetadata(target, importedImage.Id, importedSvg.Id, importedLeaf.Id,
                tags.Select(tag => target.AssetTags.Single(item => item.Name == tag.Name).Id).ToArray());

            var manifestPath = ProjectVaultStore.Save(project, Path.Combine(directory, "Project", "Panel.v2dProject"));
            File.Delete(imagePath);
            File.Delete(svgPath);
            var reopened = ProjectVaultStore.Load(manifestPath);
            AssertPanelResourceMetadata(reopened, image.Id, svg.Id, folder!.Id, tags.Select(tag => tag.Id).ToArray());
            AssertTimeline(File.Exists(reopened.ImageAssets.Single().SourcePath)
                && File.Exists(reopened.ExternalSvgAssets.Single().SourcePath),
                "Reopening depended on deleted original resource files.");

            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            foreach (var item in manifest["imageAssets"]!.AsArray().Concat(manifest["externalSvgAssets"]!.AsArray()))
            {
                item!.AsObject().Remove("assetFolderId");
                item.AsObject().Remove("assetTagIds");
            }
            var legacyJson = manifest.ToJsonString();
            File.WriteAllText(manifestPath, legacyJson);
            var legacy = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(legacy.ImageAssets.Single().AssetFolderId == "" && legacy.ImageAssets.Single().AssetTagIds.Count == 0
                && legacy.ExternalSvgAssets.Single().AssetFolderId == "" && legacy.ExternalSvgAssets.Single().AssetTagIds.Count == 0,
                "A project without optional resource metadata did not retain legacy read compatibility.");
            manifest["imageAssets"]![0]!["assetFolderId"] = "missing-folder";
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            AssertTimeline(ThrowsInvalidData(() => ProjectVaultStore.Load(manifestPath)),
                "Loading accepted an image resource pointing to an undeclared asset folder.");
            File.WriteAllText(manifestPath, legacyJson);

            RunPanelLibraryHistoryRoundTrip(project, image.Id, svg.Id, folder.Id, parent.Id);
            RunPanelSceneListIdentityRegression(project);
            Console.WriteLine("panel_usability_regression=ok");
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    private static void AssertPanelResourceMetadata(VectorProject project, string imageId, string svgId,
        string folderId, string[] tagIds)
    {
        var image = project.ImageAssets.Single(item => item.Id == imageId);
        var svg = project.ExternalSvgAssets.Single(item => item.Id == svgId);
        AssertTimeline(image.AssetFolderId == folderId && svg.AssetFolderId == folderId
            && image.AssetTagIds.SequenceEqual(tagIds) && svg.AssetTagIds.SequenceEqual(tagIds),
            "Image/SVG folder or tag metadata did not survive its storage boundary.");
    }

    private static string PanelProjectSignature(VectorProject project)
    {
        var snapshot = JsonSerializer.SerializeToNode(project.CreateRestartSnapshot(),
            new JsonSerializerOptions { IncludeFields = true })!;
        Normalize(snapshot);
        return snapshot.ToJsonString();

        static void Normalize(JsonNode node)
        {
            if (node is JsonObject item)
            {
                // These legacy bounds are derived from the authoritative timeline
                // during restore. Compare the full timeline, not its stale cache.
                if (item.ContainsKey("ObjectKeyframeFrame") && item.ContainsKey("Timeline"))
                {
                    item.Remove("LayerStart");
                    item.Remove("LayerEnd");
                }
                foreach (var child in item.Select(pair => pair.Value).OfType<JsonNode>()) Normalize(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.OfType<JsonNode>()) Normalize(child);
        }
    }

    private static void RunPanelLibraryHistoryRoundTrip(VectorProject project, string imageId, string svgId,
        string folderId, string parentFolderId)
    {
        var keeper = project.DrawingObjects[0];
        var removed = project.AddDrawingObject("Delete in batch");
        removed.Scene.AddObject(0, new PointF(7, 11), new SizeF(13, 17), 0.2f, 0, Color.Gold, 8, ShapeKind.Rectangle);
        var first = project.Scenes[0];
        var second = project.AddScene("Unrelated scene");
        second.DrawingScene.AddObject(0, new PointF(203, 109), new SizeF(27, 19), 0.4f, 0, Color.Blue, 8, ShapeKind.Ellipse);
        AssertTimeline(project.TryAddSceneInstance(first.Id, removed.Id, new PointF(33, 51), 7, out var instance)
            && instance is not null, "History fixture could not create its scene instance.");
        instance!.Name = "Animated reference";
        instance.SetStateAtFrame(6, instance.EvaluateState(0) with { X = 77, RotationZ = 23, Alpha = 0.45f });
        first.Timeline.SetTrackDuration(first.Timeline.Tracks[0].Id, 12);
        AssertTimeline(project.TryAddDrawingObjectInstance(keeper.Id, removed.Id, new PointF(40, 60), out var nested)
            && nested is not null, "History fixture could not create its nested instance.");
        nested!.SetStateAtFrame(5, nested.EvaluateState(0) with { Y = 83, ScaleX = 1.5f });
        var canvas = new SceneCanvasSettings
        {
            Width = 937, Height = 613, BackgroundKind = SceneBackgroundKind.Image, ImageAssetId = imageId,
            ImageFit = CanvasImageFit.Cover, ImageScale = 1.7f, ImageOffsetX = 19, ImageOffsetY = -31, ImageOpacity = 0.6f
        };
        AssertTimeline(project.TrySetSceneCanvas(first.Id, canvas), "History fixture could not assign its image background.");
        first.DismissCanvasWelcome();
        var contentLayerId = first.Layers.First(layer => layer.Kind == SceneLayerKind.Content).Id;
        var mask = first.AddMaskLayer(project, contentLayerId, "Image mask")
            ?? throw new InvalidOperationException("History fixture could not create a scene mask.");
        mask.MaskScene!.AddBitmapObject(0, new PointF(10, 15),
            new BitmapObjectData { ImageAssetId = imageId, PlacedSize = new SizeF(20, 18) });
        first.SynchronizeMaskTimelineContent(mask.Id, 0);
        first.DrawingScene.AddBitmapObject(Array.IndexOf(first.DrawingScene.LayerIds, contentLayerId), new PointF(42, 18),
            new BitmapObjectData { ImageAssetId = imageId, PlacedSize = new SizeF(24, 16) });
        first.SynchronizeTimelineTracks();
        keeper.SynchronizeTimelineTracks();

        var beforeText = PanelProjectSignature(project);
        var before = project.CaptureLibraryHistory(includeContent: true);
        foreach (var (_, scene) in project.EnumerateAssetScenes())
            scene.RemoveObjects(scene.FindBitmapObjectsUsingImageAsset(imageId)
                .Concat(scene.FindImportedSvgObjectsUsingAsset(svgId)).Distinct().ToArray());
        AssertTimeline(project.TrySetSceneCanvas(first.Id, canvas with { BackgroundKind = SceneBackgroundKind.None, ImageAssetId = "" })
            && project.TryRemoveImageAsset(imageId, out _) && project.TryRemoveExternalSvgAsset(svgId, out _)
            && project.TryRemoveDrawingObject(removed.Id, out _) && project.TryRemoveAssetFolder(folderId),
            "History fixture could not apply its mixed destructive operation.");
        first.SynchronizeMaskTimelineContent(mask.Id, 0);
        var after = project.CaptureLibraryHistory(includeContent: true);
        var afterText = PanelProjectSignature(project);
        AssertTimeline(beforeText != afterText && first.Canvas is { Width: 937, Height: 613, ImageAssetId: "" }
            && first.Instances.Count == 0 && keeper.Instances.Count == 0 && project.AssetFolders.Any(item => item.Id == parentFolderId),
            "Batch deletion did not remove references while preserving canvas size and the parent folder.");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            project.RestoreLibraryHistory(before);
            AssertPanelProjectSignature(project, beforeText, "Undo");
            AssertTimeline(ReferenceEquals(project.DrawingObjects.Single(item => item.Id == removed.Id), removed)
                && first.Canvas == canvas && first.CanvasWelcomeDismissed
                && first.Instances.Single().EvaluateState(6).X == 77
                && keeper.Instances.Single().EvaluateState(5).Y == 83,
                "Undo lost object order, masks, metadata, canvas settings, instance keys, timelines or symbol identity.");
            project.RestoreLibraryHistory(after);
            AssertPanelProjectSignature(project, afterText, "Redo");
        }
        project.RestoreLibraryHistory(before);
        var metadataBefore = project.CaptureLibraryHistory();
        AssertTimeline(project.TryRemoveAssetFolder(folderId)
            && project.ImageAssets.Single().AssetFolderId == parentFolderId
            && project.ExternalSvgAssets.Single().AssetFolderId == parentFolderId,
            "Removing a folder failed to promote image/SVG assets to its parent.");
        project.RestoreLibraryHistory(metadataBefore);
        AssertTimeline(PanelProjectSignature(project) == beforeText,
            "Metadata-only undo changed unrelated geometry or failed to restore asset organization.");
    }

    private static void AssertPanelProjectSignature(VectorProject project, string expected, string operation)
    {
        var actual = PanelProjectSignature(project);
        if (actual == expected) return;
        var offset = 0;
        while (offset < Math.Min(actual.Length, expected.Length) && actual[offset] == expected[offset]) offset++;
        var start = Math.Max(0, offset - 100);
        throw new InvalidOperationException($"{operation} changed the captured project at offset {offset}. "
            + $"Expected: {expected.Substring(start, Math.Min(300, expected.Length - start))}; "
            + $"Actual: {actual.Substring(Math.Min(start, actual.Length), Math.Min(300, Math.Max(0, actual.Length - start)))}");
    }

    private static void RunPanelNumericDraftRegression()
    {
        using var numeric = new ModernNumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Value = 42 };
        var editor = numeric.Controls.OfType<TextBox>().Single();
        RequireMethod(typeof(Control), "OnGotFocus").Invoke(editor, [EventArgs.Empty]);
        var changes = 0;
        numeric.ValueChanged += (_, _) => changes++;
        editor.Text = "invalid";
        AssertTimeline(!numeric.TryCommitInput() && numeric.Value == 42 && editor.Text == "invalid" && changes == 0,
            "Invalid numeric input was silently discarded or changed the model.");
        editor.Text = "101";
        AssertTimeline(!numeric.TryCommitInput() && numeric.Value == 42 && editor.Text == "101" && changes == 0,
            "Out-of-range numeric input was silently clamped.");
        RequireMethod(typeof(ModernNumericUpDown), "HandleEditorKeyDown").Invoke(numeric, [editor, new KeyEventArgs(Keys.Escape)]);
        AssertTimeline(numeric.Value == 42 && numeric.TryCommitInput() && changes == 0,
            "Esc did not cancel an invalid numeric draft without creating a model edit.");
        numeric.MixedValue = true;
        AssertTimeline(numeric.TryCommitInput() && numeric.MixedValue && changes == 0,
            "Focusing and leaving a mixed numeric field chose a concrete value.");
        editor.Text = 42m.ToString(System.Globalization.CultureInfo.CurrentCulture);
        AssertTimeline(numeric.TryCommitInput() && !numeric.MixedValue && numeric.Value == 42 && changes == 1,
            "Explicitly typing the primary mixed value failed to apply it to the selection.");
        AssertTimeline(!numeric.MouseWheelChangesValue, "Property numeric fields still change by wheel by default.");
        Console.WriteLine("panel_numeric_invalid_range_escape_mixed=ok");
    }

    private static void RunPanelHistoryWorkflowRegression()
    {
        var project = VectorProject.CreateEmpty();
        project.DrawingObjects[0].Scene.AddObject(0, PointF.Empty, new SizeF(24, 18), 0, 0, Color.Teal, 0, ShapeKind.Rectangle);
        var first = project.Scenes[0];
        var second = project.AddScene("History B");
        using var form = MainForm.CreateForRestart(new EditorRestartState
        {
            Project = project, ActiveSceneIndex = 0, ActiveDrawingObjectIndex = 0,
            Workspace = WorkspaceView.SceneEditor, WindowBounds = new Rectangle(-30_000, -30_000, 1280, 800),
            Frame = 0, SelectedObjects = [], WindowState = FormWindowState.Normal,
            StageView = default(StageViewState) with { Zoom = 1, ReferenceDistance = 1000, ReferenceZoomScale = 1 }
        });
        form.ShowInTaskbar = false;
        RequireMethod(typeof(MainForm), "ResetEditHistory").Invoke(form, null);
        var select = RequireMethod(typeof(MainForm), "SelectScenePreservingHistory");
        var add = RequireMethod(typeof(MainForm), "AddSceneInstanceFromPanel");
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var redo = RequireMethod(typeof(MainForm), "RedoLastEdit");
        add.Invoke(form, null);
        select.Invoke(form, [1]);
        add.Invoke(form, null);
        select.Invoke(form, [0]);
        AssertTimeline(first.Instances.Count == 1 && second.Instances.Count == 1 && undo.Invoke(form, null) is true
            && first.Instances.Count == 1 && second.Instances.Count == 0,
            "Undo after scene navigation did not restore the newest scene operation.");
        AssertTimeline(undo.Invoke(form, null) is true && first.Instances.Count == 0
            && redo.Invoke(form, null) is true && first.Instances.Count == 1
            && redo.Invoke(form, null) is true && second.Instances.Count == 1,
            "Scene navigation lost undo/redo entries or restored the wrong scope.");
        AssertTimeline(undo.Invoke(form, null) is true, "Cross-scene stale redo setup could not undo.");
        select.Invoke(form, [0]);
        add.Invoke(form, null);
        select.Invoke(form, [1]);
        AssertTimeline(redo.Invoke(form, null) is false,
            "Returning to a cached scene revived redo invalidated by another scene edit.");

        RequireMethod(typeof(MainForm), "ResetEditHistory").Invoke(form, null);
        RequireMethod(typeof(MainForm), "ExecuteLibraryPanelCommand").Invoke(form,
            [(Action)(() => project.TryRenameDrawingObject(project.DrawingObjects[0].Id, "History metadata")), true]);
        select.Invoke(form, [0]);
        var countBefore = first.Instances.Count;
        add.Invoke(form, null);
        select.Invoke(form, [1]);
        AssertTimeline(undo.Invoke(form, null) is true && first.Instances.Count == countBefore
            && project.DrawingObjects[0].Name == "History metadata",
            "A project library undo skipped a newer archived scene operation.");
        AssertTimeline(undo.Invoke(form, null) is true && project.DrawingObjects[0].Name != "History metadata"
            && redo.Invoke(form, null) is true && project.DrawingObjects[0].Name == "History metadata"
            && redo.Invoke(form, null) is true && first.Instances.Count == countBefore + 1,
            "A library snapshot crossed another scene edit or failed its redo chain.");
        RequireMethod(typeof(MainForm), "ResetEditHistory").Invoke(form, null);
        RequireMethod(typeof(MainForm), "AddSceneFromPanel").Invoke(form, null);
        var created = project.Scenes[^1];
        AssertTimeline(undo.Invoke(form, null) is true && !project.Scenes.Contains(created)
            && redo.Invoke(form, null) is true && ReferenceEquals(project.Scenes[^1], created),
            "Adding a scene switched or cleared the global redo stack during replay.");
        Console.WriteLine("panel_scene_global_history_order_stale_redo=ok");
    }

    private static void RunPanelSceneListIdentityRegression(VectorProject project)
    {
        var before = project.Scenes.ToArray();
        var removed = before[0];
        var original = PanelProjectSignature(project);
        AssertTimeline(project.TryRemoveScene(removed.Id, out var removedResult) && ReferenceEquals(removedResult, removed),
            "Scene-list history fixture did not remove the original scene identity.");
        var after = project.Scenes.ToArray();
        project.RestoreSceneListHistory(before);
        AssertTimeline(project.Scenes.Select((scene, index) => ReferenceEquals(scene, before[index])).All(value => value)
            && PanelProjectSignature(project) == original,
            "Scene-list undo replaced scene identities or discarded timeline, mask, canvas or instance state.");
        project.RestoreSceneListHistory(after);
        AssertTimeline(project.Scenes.Count == after.Length && !project.Scenes.Any(scene => ReferenceEquals(scene, removed)),
            "Scene-list redo retained the deleted scene.");
        project.RestoreSceneListHistory(before);
        var beforeReject = project.Scenes.ToArray();
        try { project.RestoreSceneListHistory([removed, removed]); }
        catch (InvalidOperationException) { }
        AssertTimeline(project.Scenes.SequenceEqual(beforeReject),
            "Rejecting a duplicated scene identity partially changed the scene collection.");
    }
}
