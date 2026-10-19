using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunAssetLibraryCategoryRegression()
    {
        RunExternalSvgAssetPersistenceRegression();
        RunSharedSvgAssetReferenceRegression();
        RunImageAssetPersistenceRegression();
        RunSpatialComponentModelRegression();
        Console.WriteLine("asset_library_category_regression=ok");
    }

    private static void RunExternalSvgAssetPersistenceRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("external-svg-asset-regression");
        var assetDirectory = Path.Combine(temporaryRoot, "assets");
        var sourcePath = Path.Combine(assetDirectory, "linked-logo.svg");
        var manifestPath = Path.Combine(temporaryRoot, "ExternalLinks.v2dProject");
        Directory.CreateDirectory(assetDirectory);
        File.WriteAllText(sourcePath, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 12\"><rect width=\"16\" height=\"12\"/></svg>");
        try
        {
            var project = VectorProject.CreateEmpty();
            var sha256 = BitmapImageRasterizer.ComputeSha256(sourcePath);
            AssertTimeline(
                project.TryAddExternalSvgAsset(
                    "Linked Logo",
                    sourcePath,
                    "assets/linked-logo.svg",
                    sha256,
                    out var linked)
                && linked is not null
                && project.ExternalSvgAssets.Count == 1
                && !project.TryAddExternalSvgAsset(
                    "Unsafe",
                    sourcePath,
                    "../linked-logo.svg",
                    sha256,
                    out _),
                "External SVG asset links did not validate their stable metadata and relative path.");

            var restartRestored = VectorProject.RestoreRestartSnapshot(
                EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
            var restartLink = restartRestored.ExternalSvgAssets.Single();
            AssertTimeline(
                restartLink.Id == linked!.Id
                && restartLink.Name == "Linked Logo"
                && File.Exists(restartLink.SourcePath)
                && restartLink.SourcePath != Path.GetFullPath(sourcePath)
                && restartLink.ProjectRelativePath == $".Vault/ExternalSvg/{linked.Id}.svg"
                && restartLink.LastKnownSha256 == sha256,
                "Editor restart JSON did not preserve the external SVG asset link.");

            AssertTimeline(restartLink.OriginalSourcePath == Path.GetFullPath(sourcePath),
                "Restart lost the original path used for duplicate import detection.");
            File.Delete(sourcePath);
            ProjectVaultStore.Save(project, manifestPath);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                ?? throw new InvalidOperationException("External SVG regression could not parse the project manifest.");
            AssertTimeline(
                manifest["formatVersion"]?.GetValue<int>() == ProjectVaultStore.CurrentManifestFormatVersion
                && manifest["externalSvgAssets"] is JsonArray { Count: 1 }
                && Directory.GetFiles(Path.Combine(temporaryRoot, ".Vault"), "*.svg").Length
                    == project.DrawingObjects.Count,
                "Project Vault did not save SVG links in manifest v4 or copied an external file into managed assets.");

            File.Delete(sourcePath);
            var vaultRestored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                vaultRestored.ExternalSvgAssets.Single().Id == linked.Id
                && File.Exists(vaultRestored.ExternalSvgAssets.Single().SourcePath),
                "A deleted original SVG broke the managed project resource.");
            var secondRoot = Path.Combine(temporaryRoot, "save-as");
            var secondManifest = Path.Combine(secondRoot, "Renamed.v2dProject");
            ProjectVaultStore.Save(project, secondManifest);
            ProjectVaultStore.Save(project, secondManifest);
            AssertTimeline(project.ExternalSvgAssets.Single().SourcePath.StartsWith(secondRoot),
                "Save As did not rebind SVG resources.");
            var movedRoot = Path.Combine(temporaryRoot, "moved");
            Directory.Move(secondRoot, movedRoot);
            var movedManifest = Path.Combine(movedRoot, "Renamed.v2dProject");
            var moved = ProjectVaultStore.Load(movedManifest);
            AssertTimeline(File.Exists(moved.ExternalSvgAssets.Single().SourcePath), "Moving the package broke SVG resources.");
            var originalManifestBytes = File.ReadAllBytes(movedManifest);
            File.AppendAllText(moved.ExternalSvgAssets.Single().SourcePath, " ");
            AssertTimeline(ThrowsInvalidData(() => ProjectVaultStore.Save(moved, movedManifest))
                && File.ReadAllBytes(movedManifest).SequenceEqual(originalManifestBytes),
                "A checksum failure modified the committed manifest.");

            var legacyRoot = Path.Combine(temporaryRoot, "legacy");
            var legacyManifestPath = Path.Combine(legacyRoot, "Legacy.v2dProject");
            Directory.CreateDirectory(legacyRoot);
            ProjectVaultStore.Save(VectorProject.CreateEmpty(), legacyManifestPath);
            ConvertProjectCompressionFixtureToLegacy(legacyManifestPath, 1);
            var legacyManifest = JsonNode.Parse(File.ReadAllText(legacyManifestPath))!.AsObject();
            legacyManifest["formatVersion"] = 1;
            legacyManifest.Remove("externalSvgAssets");
            File.WriteAllText(
                legacyManifestPath,
                legacyManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AssertTimeline(
                ProjectVaultStore.Load(legacyManifestPath).ExternalSvgAssets.Count == 0,
                "Manifest v1 did not load with an empty external SVG link collection.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static void RunSharedSvgAssetReferenceRegression()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 12"><rect width="16" height="12" fill="#18a060"/></svg>""";
        const string updated = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 8"><ellipse cx="16" cy="4" rx="16" ry="4" fill="#f08020"/></svg>""";
        var directory = CreateTemporaryDirectory("shared-svg-assets");
        try
        {
            var path = Path.Combine(directory, "tree.svg");
            var updatedPath = Path.Combine(directory, "autumn.svg");
            File.WriteAllText(path, source);
            File.WriteAllText(updatedPath, updated);
            var project = VectorProject.CreateEmpty();
            AssertTimeline(project.TryAddExternalSvgAsset("Tree", path, "",
                BitmapImageRasterizer.ComputeSha256(path), out var asset) && asset is not null,
                "Shared SVG fixture could not create its managed library resource.");
            var assetId = asset!.Id;
            var drawing = project.DrawingObjects[0];
            drawing.Scene.CreateEmpty(1, 12);
            for (var index = 0; index < 3; index++)
            {
                var placed = drawing.Scene.AddImportedSvgObject(0, new PointF(30 + index * 80, index * 10),
                    new SizeF(40 + index * 7, 30 + index * 3), index * 0.2f, source, "Tree");
                AssertTimeline(drawing.Scene.TrySetImportedSvgAsset(placed, assetId),
                    "A whole SVG placement could not reference its library resource.");
            }
            AssertTimeline(drawing.Scene.FindImportedSvgObjectsUsingAsset(assetId).SequenceEqual([0, 1, 2]),
                "Three shared SVG placements did not use the same stable asset identifier.");

            // Convert-to-symbol selection cloning must retain the library reference.
            var selection = drawing.Scene.CreateStaticSelectionScene([1], 0);
            var nested = project.AddDrawingObject("Shared Tree Copy");
            nested.Scene.RestoreSnapshot(selection.CreateSnapshot());
            var roundTrip = new VectorScene();
            roundTrip.RestoreSnapshot(drawing.Scene.CreateSnapshot());
            AssertTimeline(nested.Scene.TryGetImportedSvgAssetId(0, out var copiedId) && copiedId == assetId
                && roundTrip.FindImportedSvgObjectsUsingAsset(assetId).SequenceEqual([0, 1, 2]),
                "Selection cloning or snapshot restore detached a shared SVG asset reference.");
            var preview = VectorScene.CreateObjectPreviewScene(drawing.Scene, 2);
            AssertTimeline(preview is not null && preview.TryGetImportedSvgAssetId(0, out var previewId)
                && previewId == assetId, "SVG object preview cloning lost its shared resource reference.");

            var outer = project.Scenes[0];
            outer.Timeline.SetTrackDuration(outer.Timeline.Tracks[0].Id, 12);
            AssertTimeline(project.TryAddSceneInstance(outer.Id, drawing.Id, new PointF(60, 90), 0, out var instance)
                && instance is not null, "Shared SVG fixture could not create its animated instance.");
            instance!.ScaleX = 1.5f;
            instance.ScaleY = 0.8f;
            instance.SkewX = 17;
            instance.RotationZ = 23;
            instance.SetStateAtFrame(7, instance.EvaluateState(7) with { X = 170, Y = -25, RotationZ = 61 });
            var baseState = instance.EvaluateState(0);
            var keyState = instance.EvaluateState(7);
            var geometryBefore = drawing.Scene.CreateSnapshot();
            var timelineBefore = JsonSerializer.Serialize(drawing.Timeline.CreateSnapshot());
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, outer, project.DrawingObjects, 0);
            AssertTimeline(composed.ObjectCount == 3
                && composed.FindImportedSvgObjectsUsingAsset(assetId).Length == 3
                && composed.TryGetImportedSvgSource(0, out var wrappedSource)
                && VectorScene.ResolveImportedSvgAssetSource(wrappedSource) == source,
                "SVG composition lost the shared reference or its wrapped leaf source.");
            var wrappedBefore = composed.CreateSnapshot();
            AssertTimeline(composed.UpdateImportedSvgAssetSource(assetId, updated) == 3
                && composed.TryGetImportedSvgSource(0, out var wrappedUpdated)
                && VectorScene.ResolveImportedSvgAssetSource(wrappedUpdated) == updated
                && composed.CreateSnapshot().X.SequenceEqual(wrappedBefore.X)
                && composed.CreateSnapshot().Width.SequenceEqual(wrappedBefore.Width),
                "Updating a composed SVG changed its wrapper transform or failed to replace its leaf source.");

            var contentLayer = outer.Layers.First(layer => layer.Kind == SceneLayerKind.Content);
            var mask = outer.AddMaskLayer(project, contentLayer.Id, "Shared SVG Mask");
            AssertTimeline(mask?.MaskScene is not null, "Shared SVG fixture could not create a mask scene.");
            var maskIndex = mask!.MaskScene!.AddImportedSvgObject(0, PointF.Empty, new SizeF(40, 30), source, "Mask Tree");
            AssertTimeline(mask.MaskScene.TrySetImportedSvgAsset(maskIndex, assetId),
                "A mask SVG could not reference its library resource.");
            outer.SynchronizeMaskTimelineContent(mask.Id, 0);
            var maskTimelineBefore = JsonSerializer.Serialize(outer.Timeline.CreateSnapshot());
            AssertTimeline(project.TryRelocateExternalSvgAsset(assetId, updatedPath, "",
                BitmapImageRasterizer.ComputeSha256(updatedPath)), "Shared SVG source update failed.");
            foreach (var (_, ownedScene) in project.EnumerateAssetScenes())
                foreach (var index in ownedScene.FindImportedSvgObjectsUsingAsset(assetId))
                    AssertTimeline(ownedScene.TryGetImportedSvgSource(index, out var actual) && actual == updated,
                        "A shared SVG source update missed a drawing object or scene mask.");
            var geometryAfter = drawing.Scene.CreateSnapshot();
            AssertTimeline(geometryAfter.X.SequenceEqual(geometryBefore.X)
                && geometryAfter.Y.SequenceEqual(geometryBefore.Y)
                && geometryAfter.Width.SequenceEqual(geometryBefore.Width)
                && geometryAfter.Height.SequenceEqual(geometryBefore.Height)
                && geometryAfter.Angle.SequenceEqual(geometryBefore.Angle)
                && geometryAfter.ObjectKeyframeFrame.SequenceEqual(geometryBefore.ObjectKeyframeFrame)
                && JsonSerializer.Serialize(drawing.Timeline.CreateSnapshot()) == timelineBefore
                && JsonSerializer.Serialize(outer.Timeline.CreateSnapshot()) == maskTimelineBefore
                && instance.EvaluateState(0) == baseState && instance.EvaluateState(7) == keyState,
                "Updating a shared SVG reset placement transforms, cel ownership, timeline or instance keyframes.");
            var stableSnapshot = JsonSerializer.Serialize(drawing.Scene.CreateSnapshot());
            AssertTimeline(!project.TryRelocateExternalSvgAsset(assetId, Path.Combine(directory, "missing.svg"), "", "")
                && JsonSerializer.Serialize(drawing.Scene.CreateSnapshot()) == stableSnapshot,
                "A failed SVG source update changed the last valid placed content.");

            File.Delete(path);
            File.Delete(updatedPath);
            var manifestPath = Path.Combine(directory, "Saved", "Shared.v2dProject");
            ProjectVaultStore.Save(project, manifestPath);
            var restored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(restored.ExternalSvgAssets.Single().Id == assetId
                && File.Exists(restored.ExternalSvgAssets.Single().SourcePath)
                && restored.EnumerateAssetScenes().Sum(item => item.Scene.FindImportedSvgObjectsUsingAsset(assetId).Length) == 5
                && restored.Scenes[0].Instances.Single().EvaluateState(7) == keyState,
                "Saving and reopening after source deletion lost shared SVG references, masks or animation.");

            var packagePath = Path.Combine(directory, "Tree" + DrawingObjectSymbolPackage.FileExtension);
            DrawingObjectSymbolPackageService.Export(restored, drawing.Id, packagePath);
            var package = DrawingObjectSymbolPackage.Read(packagePath);
            AssertTimeline(package.ExternalSvgAssets.Length == 1 && package.ExternalSvgAssets[0].Id == assetId,
                "Symbol package did not capture the shared SVG dependency once.");
            var target = VectorProject.CreateEmpty();
            var first = DrawingObjectSymbolPackageService.Import(target, package);
            var second = DrawingObjectSymbolPackageService.Import(target, package);
            var hasFirstId = first.Scene.TryGetImportedSvgAssetId(0, out var firstId);
            var hasSecondId = second.Scene.TryGetImportedSvgAssetId(0, out var secondId);
            AssertTimeline(hasFirstId && hasSecondId
                && firstId != secondId && firstId != assetId && secondId != assetId
                && target.ExternalSvgAssets.Count == 2
                && first.Scene.FindImportedSvgObjectsUsingAsset(firstId).Length == 3
                && second.Scene.FindImportedSvgObjectsUsingAsset(secondId).Length == 3,
                "Repeated symbol package imports reused SVG asset identifiers instead of isolating each import.");
            AssertTimeline(first.Scene.UpdateImportedSvgAssetSource(firstId, source) == 3
                && second.Scene.TryGetImportedSvgSource(0, out var secondSource) && secondSource == updated,
                "Updating the first imported SVG copy changed the second independent package import.");
            var danglingPackage = new DrawingObjectSymbolPackage
            {
                FormatVersion = package.FormatVersion, RootSymbolId = package.RootSymbolId,
                RootSymbolName = package.RootSymbolName, Symbols = package.Symbols,
                Folders = package.Folders, Tags = package.Tags, ExternalSvgAssets = []
            };
            var untouched = VectorProject.CreateEmpty();
            ExpectInvalidData(() => DrawingObjectSymbolPackageService.Import(untouched, danglingPackage),
                "Symbol package import accepted a dangling SVG resource reference.");
            AssertTimeline(untouched.DrawingObjects.Count == 1 && untouched.ExternalSvgAssets.Count == 0,
                "Rejecting a dangling SVG package partially changed the target project.");
            var badRestart = restored.CreateRestartSnapshot();
            badRestart.DrawingObjects[0].Scene.ImportedSvgAssetIds[0] = Guid.NewGuid().ToString("N");
            var rejected = false;
            try { VectorProject.RestoreRestartSnapshot(badRestart); }
            catch (InvalidOperationException) { rejected = true; }
            AssertTimeline(rejected, "Restart restoration accepted a dangling shared SVG resource reference.");
            Console.WriteLine("shared_svg_asset_reference_regression=ok");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static void RunSpatialComponentModelRegression()
    {
        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.ThreeD;
        scene.Timeline.SetTrackDuration(scene.Timeline.Tracks[0].Id, 12);
        var source = project.DrawingObjects[0];
        source.SetAnchor(new PointF(24, -12));
        source.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(80, 50),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            6,
            ShapeKind.Rectangle);

        DrawingObjectInstanceDefinition? before = null;
        DrawingObjectInstanceDefinition? first = null;
        DrawingObjectInstanceDefinition? second = null;
        DrawingObjectInstanceDefinition? after = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, source.Id, new PointF(-240, 30), -20, out before)
            && project.TryAddSceneInstance(scene.Id, source.Id, new PointF(80, 50), 30, out first)
            && project.TryAddSceneInstance(scene.Id, source.Id, new PointF(320, 170), 150, out second)
            && project.TryAddSceneInstance(scene.Id, source.Id, new PointF(620, 240), 260, out after)
            && before is not null && first is not null && second is not null && after is not null,
            "Spatial component regression could not create its ordered scene instances.");
        first!.RotationX = 18;
        first.RotationY = -27;
        first.RotationZ = 32;
        first.ScaleX = 1.25f;
        first.ScaleY = 0.75f;
        first.ScaleZ = 2.5f;
        second!.RotationX = -12;
        second.RotationY = 41;
        second.RotationZ = -9;
        second.ScaleX = 0.8f;
        second.ScaleY = 1.4f;
        second.ScaleZ = 3.25f;
        first.SetStateAtFrame(7, first.EvaluateState(7) with { X = 140, Y = 90, Z = 75, RotationY = 63 });
        second.SetStateAtFrame(7, second.EvaluateState(7) with { X = 410, Y = 210, Z = 220, ScaleZ = 4 });
        var firstBase = first.EvaluateState(0);
        var secondBase = second.EvaluateState(0);
        var firstKey = first.EvaluateState(7);
        var secondKey = second.EvaluateState(7);
        var timelineBefore = JsonSerializer.Serialize(scene.Timeline.CreateSnapshot());

        AssertTimeline(
            project.TryConvertSceneInstancesToSpatialComponent(
                scene.Id,
                [first.Id, second.Id],
                0,
                out var component,
                out var replacement)
            && component is not null
            && replacement is not null,
            "Two contiguous 3D scene instances could not be converted to a spatial component.");
        var members = component!.Instances.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var firstMemberBase = members[first.Id].EvaluateState(0);
        var secondMemberBase = members[second.Id].EvaluateState(0);
        var firstMemberKey = members[first.Id].EvaluateState(7);
        var secondMemberKey = members[second.Id].EvaluateState(7);
        AssertTimeline(
            DrawingObjectAssetKinds.IsThreeDimensional(component.Kind)
            && component.FrameCount == scene.FrameCount
            && members.Count == 2
            && members.Keys.ToHashSet(StringComparer.Ordinal).SetEquals([first.Id, second.Id])
            && scene.Instances.Select(instance => instance.Id)
                .SequenceEqual([before!.Id, replacement!.Id, after!.Id], StringComparer.Ordinal)
            && NearlyEqual(replacement.ScaleX, 1)
            && NearlyEqual(replacement.ScaleY, 1)
            && NearlyEqual(replacement.ScaleZ, 1)
            && replacement.PlaybackFps == project.PlaybackFps
            && StatePositionRecomposes(firstBase, firstMemberBase, replacement)
            && StatePositionRecomposes(secondBase, secondMemberBase, replacement)
            && StatePositionRecomposes(firstKey, firstMemberKey, replacement)
            && StatePositionRecomposes(secondKey, secondMemberKey, replacement)
            && NearlyEqual(firstMemberBase.RotationY, firstBase.RotationY)
            && NearlyEqual(secondMemberKey.ScaleZ, secondKey.ScaleZ)
            && JsonSerializer.Serialize(scene.Timeline.CreateSnapshot()) == timelineBefore,
            "Spatial component conversion changed member identity, animation, ordering, root scale, or outer timeline.");

        var rejectionProject = VectorProject.CreateEmpty();
        var rejectionScene = rejectionProject.Scenes[0];
        rejectionScene.Dimension = SceneDimension.ThreeD;
        var rejectionSource = rejectionProject.DrawingObjects[0];
        DrawingObjectInstanceDefinition? left = null;
        DrawingObjectInstanceDefinition? middle = null;
        DrawingObjectInstanceDefinition? right = null;
        AssertTimeline(
            rejectionProject.TryAddSceneInstance(rejectionScene.Id, rejectionSource.Id, PointF.Empty, 0, out left)
            && rejectionProject.TryAddSceneInstance(rejectionScene.Id, rejectionSource.Id, new PointF(100, 0), 0, out middle)
            && rejectionProject.TryAddSceneInstance(rejectionScene.Id, rejectionSource.Id, new PointF(200, 0), 0, out right)
            && left is not null && middle is not null && right is not null,
            "Spatial component rejection regression could not create non-contiguous instances.");
        var rejectionSnapshot = rejectionScene.CreateInstanceSnapshot();
        AssertTimeline(
            !rejectionProject.TryConvertSceneInstancesToSpatialComponent(
                rejectionScene.Id,
                [left!.Id, right!.Id],
                0,
                out _,
                out _)
            && rejectionProject.DrawingObjects.Count == 1
            && rejectionScene.Instances.Select(instance => instance.Id)
                .SequenceEqual(rejectionSnapshot.Select(instance => instance.Id), StringComparer.Ordinal),
            "Spatial component conversion accepted a non-contiguous selection or partially mutated the project.");

        var rejectionTrack = rejectionScene.Timeline.FindTrackByTargetId(left.SceneLayerId);
        AssertTimeline(
            rejectionTrack is not null
            && rejectionScene.Timeline.InsertBlankKeyframe(rejectionTrack.Id, 1)
            && !rejectionProject.TryConvertSceneInstancesToSpatialComponent(
                rejectionScene.Id,
                [left.Id, middle!.Id],
                1,
                out _,
                out _)
            && rejectionProject.DrawingObjects.Count == 1
            && rejectionScene.Instances.Select(instance => instance.Id)
                .SequenceEqual(rejectionSnapshot.Select(instance => instance.Id), StringComparer.Ordinal),
            "Spatial component conversion accepted instances on a blank layer exposure or partially mutated the project.");
    }

    private static bool StatePositionRecomposes(
        InstanceFrameState original,
        InstanceFrameState local,
        DrawingObjectInstanceDefinition root)
    {
        return NearlyEqual(local.X + root.X, original.X)
            && NearlyEqual(local.Y + root.Y, original.Y)
            && NearlyEqual(local.Z + root.Z, original.Z);
    }
}
