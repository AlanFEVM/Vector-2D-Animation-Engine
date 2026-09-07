using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunAssetLibraryCategoryRegression()
    {
        RunExternalSvgAssetPersistenceRegression();
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
            var sha256 = new string('a', 64);
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
                && restartLink.SourcePath == Path.GetFullPath(sourcePath)
                && restartLink.ProjectRelativePath == "assets/linked-logo.svg"
                && restartLink.LastKnownSha256 == sha256,
                "Editor restart JSON did not preserve the external SVG asset link.");

            ProjectVaultStore.Save(project, manifestPath);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                ?? throw new InvalidOperationException("External SVG regression could not parse the project manifest.");
            AssertTimeline(
                manifest["formatVersion"]?.GetValue<int>() == 4
                && manifest["externalSvgAssets"] is JsonArray { Count: 1 }
                && Directory.GetFiles(Path.Combine(temporaryRoot, ".Vault"), "*.svg").Length
                    == project.DrawingObjects.Count,
                "Project Vault did not save SVG links in manifest v4 or copied an external file into managed assets.");

            File.Delete(sourcePath);
            var vaultRestored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                vaultRestored.ExternalSvgAssets.Single().Id == linked.Id
                && !File.Exists(vaultRestored.ExternalSvgAssets.Single().SourcePath),
                "A missing external SVG source prevented project load or removed its retained link.");

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
