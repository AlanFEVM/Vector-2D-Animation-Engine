using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneOpticsModelRegression()
    {
        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        AssertTimeline(
            scene.Lights.Count == 0
            && project.TrySetSceneDimension(scene.Id, SceneDimension.ThreeD)
            && scene.Lights.Count == 2,
            "A 3D scene did not create its default lighting exactly once.");

        var directional = scene.Lights.Single(light => light.Kind == SceneLightKind.Directional);
        var ambient = scene.Lights.Single(light => light.Kind == SceneLightKind.Ambient);
        AssertTimeline(
            directional.Name == "Key Light"
            && directional.Settings.ColorArgb == Color.White.ToArgb()
            && NearlyEqual(directional.Settings.Intensity, 1f)
            && directional.Settings.CastsShadows
            && ambient.Name == "Environment Light"
            && ambient.Settings.ColorArgb == Color.White.ToArgb()
            && NearlyEqual(ambient.Settings.Intensity, 0.5f)
            && !ambient.Settings.CastsShadows,
            "The default key or environment light changed its optical defaults.");

        SceneLightDefinition? point = null;
        SceneLightDefinition? area = null;
        AssertTimeline(
            project.TryAddSceneLight(scene.Id, SceneLightKind.Point, out point)
            && point is not null
            && project.TryAddSceneLight(scene.Id, SceneLightKind.Area, out area)
            && area is not null,
            "A 3D scene rejected a point or area light.");
        var pointSettings = point!.Settings with
        {
            ColorArgb = Color.Cyan.ToArgb(),
            Intensity = 2.5f,
            Range = 9000f,
            Position = new Vector3(120, -80, 400),
            ShadowSoftness = 0.35f
        };
        AssertTimeline(
            project.TryUpdateSceneLight(scene.Id, point.Id, "Rim Point", pointSettings)
            && point.Name == "Rim Point"
            && point.Settings == pointSettings,
            "A point light did not retain its edited color, range, transform, intensity, or shadow settings.");

        var lightTargetIds = scene.Lights.Select(light => light.Id).ToHashSet(StringComparer.Ordinal);
        AssertTimeline(
            scene.Timeline.Tracks.Count(track => lightTargetIds.Contains(track.TargetId)) == scene.Lights.Count
            && scene.Lights.All(light => scene.Timeline.FindTrackByTargetId(light.Id) is not null),
            "A scene light did not own exactly one stable timeline track.");

        const int tweenEndFrame = 12;
        var tweenEndSettings = pointSettings with
        {
            Intensity = 5.5f,
            Position = new Vector3(960, 360, -240),
            RotationDegrees = new Vector3(18, 210, -24),
            Range = 14_000f
        };
        var easing = new TweenCurveAnchor[]
        {
            new(0, 0),
            new(0.5f, 0.2f),
            new(1, 1)
        };
        AssertTimeline(
            scene.InsertLightTimelineKeyframe(point.Id, tweenEndFrame)
            && project.TryUpdateSceneLightAtFrame(
                scene.Id,
                point.Id,
                point.Name,
                tweenEndSettings,
                tweenEndFrame)
            && scene.TryCreateTimelineTween(
                point.Id,
                0,
                tweenEndFrame,
                TimelineTweenKind.Classic,
                out var tweenError)
            && string.IsNullOrEmpty(tweenError)
            && scene.ReplaceTimelineTweenCurve(point.Id, 0, tweenEndFrame, easing),
            "A light track could not create a Classic tween with easing.");
        var pointTrack = scene.Timeline.FindTrackByTargetId(point.Id)!;
        var midpointTween = pointTrack.EvaluateTween(6);
        var expectedMidpoint = SceneLightSettings.Interpolate(
            point.Kind,
            pointSettings,
            tweenEndSettings,
            midpointTween?.ProgressAt(6) ?? float.NaN);
        AssertTimeline(
            midpointTween is { Kind: TimelineTweenKind.Classic }
            && scene.TryEvaluateLightSettings(point.Id, 6, out var evaluatedMidpoint)
            && evaluatedMidpoint == expectedMidpoint,
            "A light tween did not evaluate its eased color, intensity, range, position, or rotation.");

        var previewStartSettings = pointSettings with
        {
            Intensity = pointSettings.Intensity + 0.75f,
            Position = pointSettings.Position + new Vector3(240, -120, 80)
        };
        var previewMidpoint = SceneLightSettings.Interpolate(
            point.Kind,
            previewStartSettings,
            tweenEndSettings,
            midpointTween?.ProgressAt(6) ?? float.NaN);
        AssertTimeline(
            point.EvaluateSettings(6) == expectedMidpoint
            && project.TryPreviewSceneLightAtFrame(
                scene.Id,
                point.Id,
                point.Name,
                previewStartSettings,
                0)
            && point.EvaluateSettings(6) == expectedMidpoint
            && scene.TryEvaluateLightSettings(point.Id, 6, out var evaluatedPreviewMidpoint)
            && evaluatedPreviewMidpoint == previewMidpoint,
            "A preview light edit eagerly rewrote its tween span or failed to evaluate the live endpoints.");
        AssertTimeline(
            scene.RefreshLightTimelineTweenMaterializationsAtEndpoint(point.Id, 0)
            && point.EvaluateSettings(6) == previewMidpoint
            && project.TryUpdateSceneLightAtFrame(
                scene.Id,
                point.Id,
                point.Name,
                pointSettings,
                0)
            && point.EvaluateSettings(6) == expectedMidpoint,
            "Committing a preview light edit did not materialize the tween once at its endpoint.");
        Console.WriteLine("scene_light_tween_deferred_materialization=ok");

        AssertTimeline(
            project.TryAddSceneLight(scene.Id, SceneLightKind.Point, out var animatedCopy)
            && animatedCopy is not null
            && project.TryUpdateSceneLight(scene.Id, animatedCopy.Id, "Rim Point Copy", point.Settings)
            && scene.CopyLightTimeline(point.Id, animatedCopy.Id)
            && animatedCopy.StateKeyframes.SequenceEqual(point.StateKeyframes)
            && scene.Timeline.FindTrackByTargetId(animatedCopy.Id)?.EvaluateTween(6) is { } copiedTween
            && copiedTween.CurveAnchors.SequenceEqual(easing)
            && scene.TryEvaluateLightSettings(animatedCopy.Id, 6, out var copiedMidpoint)
            && copiedMidpoint == expectedMidpoint,
            "Duplicating a light did not preserve its keyframes, Classic tween, or easing curve.");

        AssertTimeline(
            scene.InsertLightTimelineKeyframe(point.Id, 6)
            && scene.Timeline.FindTrackByTargetId(point.Id)?.EvaluateTween(6) is null
            && scene.TryEvaluateLightSettings(point.Id, 6, out var bakedMidpoint)
            && bakedMidpoint == expectedMidpoint,
            "Inserting a real light key inside a tween did not bake the visible value and break the covering tween.");
        AssertTimeline(
            scene.InsertLightTimelineBlankKeyframe(point.Id, 6)
            && !scene.TryEvaluateLightSettings(point.Id, 6, out _)
            && point.StateKeyframes.All(keyframe => keyframe.Frame != 6),
            "A blank light key remained visible or retained an orphan light state.");

        AssertTimeline(
            scene.InsertLightTimelineBlankKeyframe(area!.Id, 0)
            && scene.Timeline.FindTrackByTargetId(area.Id)?.Keyframes[0]
                == new TimelineKeyframe(0, TimelineKeyframeKind.Blank)
            && !scene.TryEvaluateLightSettings(area.Id, 0, out _),
            "A light track could not retain a blank key at frame zero.");
        scene.SynchronizeTimelineTracks();
        AssertTimeline(
            scene.Timeline.FindTrackByTargetId(area.Id)?.Keyframes[0]
                == new TimelineKeyframe(0, TimelineKeyframeKind.Blank)
            && scene.InsertLightTimelineKeyframe(area.Id, 0)
            && scene.TryEvaluateLightSettings(area.Id, 0, out _),
            "Timeline synchronization overwrote a frame-zero blank light key.");

        var source = project.DrawingObjects[0];
        source.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            0,
            Color.SteelBlue,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var container = project.AddDrawingObject("Optical container");
        DrawingObjectInstanceDefinition? child = null;
        DrawingObjectInstanceDefinition? root = null;
        AssertTimeline(
            project.TryAddDrawingObjectInstance(container.Id, source.Id, PointF.Empty, out child)
            && child is not null
            && project.TryAddSceneInstance(scene.Id, container.Id, PointF.Empty, 40, out root)
            && root is not null,
            "Optical material composition could not create its nested instances.");

        var rootMaterial = SpatialOpticalMaterial.Default with
        {
            Reflectivity = 0.7f,
            Metallic = 0.4f,
            Roughness = 0.25f
        };
        var childMaterial = SpatialOpticalMaterial.Default with
        {
            Transmission = 0.65f,
            Reflectivity = 0.2f,
            Roughness = 0.08f,
            IndexOfRefraction = 1.46f,
            CastsShadows = false
        };
        AssertTimeline(
            project.TrySetSceneInstanceOpticalMaterial(scene.Id, root!.Id, rootMaterial)
            && project.TrySetDrawingObjectInstanceOpticalMaterial(container.Id, child!.Id, childMaterial)
            && !project.TrySetSceneInstanceOpticalMaterial(
                scene.Id,
                root.Id,
                SpatialOpticalMaterial.Default with { Transmission = float.NaN }),
            "Optical material mutation did not accept valid values or reject a non-finite value.");

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            destination.ObjectCount == 1
            && result.ObjectOwners.Count == destination.ObjectCount
            && result.ObjectPoses.Count == destination.ObjectCount
            && result.ObjectMaterials.Count == destination.ObjectCount
            && result.TryGetMaterial(0, out var effective)
            && effective == childMaterial,
            "Nested optical composition lost nearest-override inheritance or metadata alignment.");

        var restored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        var restoredScene = restored.Scenes.Single(item => item.Id == scene.Id);
        var restoredContainer = restored.DrawingObjects.Single(item => item.Id == container.Id);
        AssertTimeline(
            restoredScene.Lights.Select(light => light.Id)
                .SequenceEqual(scene.Lights.Select(light => light.Id), StringComparer.Ordinal)
            && restoredScene.Lights.Select(light => light.Settings).SequenceEqual(scene.Lights.Select(light => light.Settings))
            && restoredScene.Instances.Single(instance => instance.Id == root.Id).OpticalMaterialOverride == rootMaterial
            && restoredContainer.Instances.Single(instance => instance.Id == child!.Id).OpticalMaterialOverride == childMaterial,
            "Editor restart JSON lost stable lights or per-instance optical materials.");

        foreach (var lightId in scene.Lights.Select(light => light.Id).ToArray())
        {
            AssertTimeline(project.TryRemoveSceneLight(scene.Id, lightId), "A scene light could not be removed.");
        }
        var explicitlyUnlit = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        AssertTimeline(
            explicitlyUnlit.Scenes.Single(item => item.Id == scene.Id).Lights.Count == 0,
            "An explicit empty light list was replaced by legacy default lighting.");
        Console.WriteLine("scene_optics_model_regression=ok");
    }

    private static void RunSceneOpticsPersistenceRegression()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"scene-optics-regression-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(temporaryRoot, "SceneOptics.v2dProject");
        try
        {
            var project = VectorProject.CreateEmpty();
            var scene = project.Scenes[0];
            AssertTimeline(
                project.TrySetSceneDimension(scene.Id, SceneDimension.ThreeD),
                "Scene optics persistence could not create a 3D scene.");
            var savedLightTrackIds = scene.Lights.ToDictionary(
                light => light.Id,
                light => scene.Timeline.FindTrackByTargetId(light.Id)?.Id ?? "",
                StringComparer.Ordinal);
            ProjectVaultStore.Save(project, manifestPath);
            var restored = ProjectVaultStore.Load(manifestPath);
            var restoredScene = restored.Scenes[0];
            AssertTimeline(
                restoredScene.Lights.Count == 2
                && restoredScene.Lights[0].Id == scene.Lights[0].Id
                && restoredScene.Lights.All(light =>
                    savedLightTrackIds.TryGetValue(light.Id, out var trackId)
                    && string.Equals(
                        restoredScene.Timeline.FindTrackByTargetId(light.Id)?.Id,
                        trackId,
                        StringComparison.Ordinal)),
                "Project Vault did not preserve stable scene lighting or light-track identities.");

            var timelinePath = Path.Combine(temporaryRoot, ".TimeLine", "Scenes", $"{scene.Id}.json.br");
            var savedTimeline = JsonNode.Parse(ProjectPayloadCompression.Decompress(
                File.ReadAllBytes(timelinePath), 128 * 1024 * 1024))!.AsObject();
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

            void WriteTimelineFixture(JsonObject fixture)
            {
                File.WriteAllBytes(
                    timelinePath,
                    ProjectPayloadCompression.Compress(JsonSerializer.SerializeToUtf8Bytes(fixture)));
                var timelineSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(timelinePath))).ToLowerInvariant();
                manifest["scenes"]!.AsArray()[0]!.AsObject()["timelineSha256"] = timelineSha256;
                File.WriteAllText(
                    manifestPath,
                    manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var currentWithoutLightTrack = savedTimeline.DeepClone().AsObject();
            var currentTracks = currentWithoutLightTrack["timeline"]?["tracks"]?.AsArray()
                ?? throw new InvalidOperationException("The scene timeline fixture has no track array.");
            var missingLightTrackId = scene.Lights[0].Id;
            var removedLightTrack = false;
            for (var index = currentTracks.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(
                        currentTracks[index]?["targetId"]?.GetValue<string>(),
                        missingLightTrackId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                currentTracks.RemoveAt(index);
                removedLightTrack = true;
                break;
            }
            AssertTimeline(removedLightTrack, "The current scene fixture had no light track to remove.");
            WriteTimelineFixture(currentWithoutLightTrack);
            var rejectedCurrentLightWithoutTrack = false;
            try
            {
                _ = ProjectVaultStore.Load(manifestPath);
            }
            catch (InvalidDataException)
            {
                rejectedCurrentLightWithoutTrack = true;
            }
            AssertTimeline(
                rejectedCurrentLightWithoutTrack,
                "A current scene light without its timeline track was accepted as valid.");
            Console.WriteLine("scene_light_timeline_persistence_invariant=ok");

            var malformed = savedTimeline.DeepClone().AsObject();
            malformed.Remove("lights");
            WriteTimelineFixture(malformed);
            var rejectedDanglingLightTrack = false;
            try
            {
                _ = ProjectVaultStore.Load(manifestPath);
            }
            catch (InvalidDataException)
            {
                rejectedDanglingLightTrack = true;
            }
            AssertTimeline(
                rejectedDanglingLightTrack,
                "A legacy scene with a dangling light track was accepted as valid.");

            var legacy = savedTimeline.DeepClone().AsObject();
            legacy.Remove("lights");
            var legacyTracks = legacy["timeline"]?["tracks"]?.AsArray()
                ?? throw new InvalidOperationException("The scene timeline fixture has no track array.");
            var savedLightIds = scene.Lights.Select(light => light.Id).ToHashSet(StringComparer.Ordinal);
            for (var index = legacyTracks.Count - 1; index >= 0; index--)
            {
                var targetId = legacyTracks[index]?["targetId"]?.GetValue<string>();
                if (targetId is not null && savedLightIds.Contains(targetId)) legacyTracks.RemoveAt(index);
            }
            WriteTimelineFixture(legacy);
            var legacyRestored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                legacyRestored.Scenes[0].Lights.Count == 2
                && legacyRestored.Scenes[0].Lights.Any(light =>
                    light.Kind == SceneLightKind.Ambient
                    && NearlyEqual(light.Settings.Intensity, 0.5f)),
                "A legacy 3D scene without a Lights field did not receive compatible defaults.");
            Console.WriteLine("scene_optics_persistence_regression=ok");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Each regression run uses an isolated temporary directory.
            }
        }
    }
}
