namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashSharedMaskModelRegression(string root)
    {
        var project = VectorProject.CreateEmpty();
        var scene = project.DrawingObjects[0].Scene;
        scene.CreateEmpty(3, 4);
        var mask = scene.AddMaskLayer("Shared mask");
        AssertTimeline(scene.SetSharedLayerMask(2, mask),
            "Shared mask bindings were not created.");
        Check(scene);
        var restored = new VectorScene();
        restored.RestoreSnapshot(scene.CreateSnapshot());
        Check(restored);
        var manifest = ProjectVaultStore.Save(project, Path.Combine(root, "SharedMaskModel", "Shared.v2dProject"));
        Check(ProjectVaultStore.Load(manifest).DrawingObjects[0].Scene);
        restored.SetLayerMask(1, mask);
        AssertTimeline(!restored.TryGetMaskLayerIndex(2, out _), "Exclusive UI mask binding changed its existing behavior.");
        Console.WriteLine("flash_shared_mask_model_snapshot_save_reopen_display_order=ok");
        CheckSceneMapping();

        void CheckSceneMapping()
        {
            var mappedProject = VectorProject.CreateEmpty();
            var staging = mappedProject.DrawingObjects[0];
            staging.Scene.RestoreSnapshot(scene.CreateSnapshot());
            // Distinct leaves make duplicate scene placements observable even when
            // the staging scene contains only referenced symbols, as FLA imports do.
            for (var layer = 0; layer < staging.Scene.LayerCount; layer++)
            {
                var leaf = mappedProject.AddDrawingObject("Mask mapping leaf " + layer);
                AssertTimeline(mappedProject.TryAddDrawingObjectInstance(staging.Id, leaf.Id,
                    System.Drawing.PointF.Empty, staging.Scene.LayerIds[layer], out _),
                    "Could not prepare a referenced mask mapping leaf.");
            }
            var method = typeof(FlashDocumentImporter).GetMethod("RestoreNativeScene",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            method.Invoke(null, [mappedProject, mappedProject.Scenes[0], staging, 24m, new List<FlashImportDiagnostic>()]);
            CheckMapped(mappedProject);
            var path = ProjectVaultStore.Save(mappedProject,
                Path.Combine(root, "SharedMaskMapping", "Shared.v2dProject"));
            CheckMapped(ProjectVaultStore.Load(path));
            Console.WriteLine("flash_shared_mask_scene_mapping_unique_placements_save_reopen=ok");

            void CheckMapped(VectorProject candidate)
            {
                var native = candidate.Scenes[0];
                AssertTimeline(native.Layers.Count == 2 && native.Instances.Count == 2,
                    "A masked content layer was mapped twice or the ordinary layer was lost.");
                var group = candidate.DrawingObjects.Single(item => item.Scene.LayerCount == 3);
                AssertTimeline(group.Instances.Count == 3
                    && group.Scene.TryGetMaskLayerIndex(1, out var firstMask)
                    && group.Scene.TryGetMaskLayerIndex(2, out var secondMask)
                    && firstMask == 0 && secondMask == 0,
                    "Scene mapping lost shared mask references or referenced leaves.");
                var placed = native.Instances.Select(item => item.DrawingObjectId).ToHashSet(StringComparer.Ordinal);
                AssertTimeline(placed.Contains(group.Id)
                    && !placed.Contains(group.Instances.First(item => item.SceneLayerId == group.Scene.LayerIds[1]).DrawingObjectId)
                    && !placed.Contains(group.Instances.First(item => item.SceneLayerId == group.Scene.LayerIds[2]).DrawingObjectId),
                    "A masked leaf escaped its group as an unmasked scene placement.");
            }
        }

        void Check(VectorScene candidate)
        {
            AssertTimeline(candidate.TryGetMaskLayerIndex(1, out var first) && first == mask
                && candidate.TryGetMaskLayerIndex(2, out var second) && second == mask,
                "Shared mask bindings were lost during snapshot or Save/Open.");
            var order = candidate.GetLayerDisplayOrder().ToArray();
            var position = Array.IndexOf(order, mask);
            AssertTimeline(order[position + 1] == 1 && order[position + 2] == 2,
                "Shared masked layers were separated in display order.");
        }
    }
}

