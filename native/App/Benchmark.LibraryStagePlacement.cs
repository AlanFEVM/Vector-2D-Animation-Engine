using System.Reflection;
using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunLibraryStagePlacementRegression()
    {
        var root = CreateTemporaryDirectory("library-stage-placement");
        try
        {
            var defaults = new ApplicationSettings();
            AssertTimeline(defaults.ImageImportAutoPlaceLimit == 50
                && ApplicationSettingsStore.Normalize(defaults with { ImageImportAutoPlaceLimit = -5 }).ImageImportAutoPlaceLimit == 0
                && ApplicationSettingsStore.Normalize(defaults with { ImageImportAutoPlaceLimit = 5000 }).ImageImportAutoPlaceLimit == 1000
                && JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(defaults with
                    { ImageImportAutoPlaceLimit = 17 }))!.ImageImportAutoPlaceLimit == 17,
                "The image placement threshold lost its default, bounds or serialization.");
            using var form = new MainForm();
            var type = typeof(MainForm);
            var project = (VectorProject)RequireField(type, "_project").GetValue(form)!;
            var stage = (StageControl)RequireField(type, "_stage").GetValue(form)!;
            var scene = (VectorScene)RequireField(type, "_scene").GetValue(form)!;
            var settingsField = RequireField(type, "_applicationSettings");
            var settings = (ApplicationSettings)settingsField.GetValue(form)!;
            settingsField.SetValue(form, settings with { ImageImportAutoPlaceLimit = 2 });
            var imageIds = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                var path = Path.Combine(root, $"image-{index}.png");
                WriteFixturePng(path, 32, 24);
                AssertTimeline(project.TryAddImageAssetFromSource($"image-{index}", path,
                    BitmapImageRasterizer.ComputeSha256(path), 32, 24, 96, BitmapImageImportSettings.Default, out var asset),
                    "Could not create library image fixtures.");
                imageIds.Add(asset!.Id);
            }
            var complete = RequireMethod(type, "CompleteImageBatchPlacement", [typeof(IReadOnlyList<string>), typeof(int)]);
            var place = RequireMethod(type, "PlaceLibraryAssets", [typeof(VaultStageDragData), typeof(PointF?)]);
            var undo = RequireMethod(type, "UndoLastEdit", Type.EmptyTypes);
            var originalCount = scene.ObjectCount;
            AssertTimeline(complete.Invoke(form, [imageIds, 3]) is false && scene.ObjectCount == originalCount
                && project.ImageAssets.Count == 3, "An over-limit import was added to the stage or lost its library assets.");
            AssertTimeline(complete.Invoke(form, [imageIds.Take(1).ToArray(), 3]) is false && scene.ObjectCount == originalCount,
                "A partially cancelled large batch unexpectedly auto-placed its completed images.");
            AssertTimeline(complete.Invoke(form, [imageIds.Take(2).ToArray(), 2]) is true
                && scene.ObjectCount == originalCount + 2 && stage.SelectedObjects.Count == 2,
                "An import exactly at the threshold was not placed/selected as one batch.");
            AssertTimeline(undo.Invoke(form, null) is true && scene.ObjectCount == originalCount && project.ImageAssets.Count == 3,
                "One undo did not remove the whole image batch while preserving its library assets.");
            settingsField.SetValue(form, settings with { ImageImportAutoPlaceLimit = 0 });
            AssertTimeline(complete.Invoke(form, [imageIds.Take(1).ToArray(), 1]) is false && scene.ObjectCount == originalCount,
                "Threshold zero did not disable automatic placement.");
            var batch = new VaultStageDragData(project.Id, [], imageIds.ToArray(), []);
            AssertTimeline(place.Invoke(form, [batch, new PointF(1234, -567)]) is true
                && scene.ObjectCount == originalCount + 3 && stage.SelectedObjects.Count == 3
                && Enumerable.Range(originalCount, 3).All(index => scene.X[index] == 1234 && scene.Y[index] == -567),
                "Multi-image placement lost a selected asset or missed the drop position.");
            AssertTimeline(undo.Invoke(form, null) is true && scene.ObjectCount == originalCount,
                "Multi-image drop required more than one undo.");
            var canPlace = RequireMethod(type, "CanPlaceLibraryAssets", [typeof(VaultStageDragData)]);
            AssertTimeline(canPlace.Invoke(form, [batch with { ProjectId = "another-project" }]) is false
                && canPlace.Invoke(form, [batch with { ImageIds = ["missing"] }]) is false,
                "A foreign-project or missing library reference was accepted.");

            var panel = (LibraryVaultPanel)RequireField(type, "_libraryVaultPanel").GetValue(form)!;
            panel.BindProject(project);
            var category = typeof(LibraryVaultPanel).GetNestedType("AssetCategory", BindingFlags.NonPublic)!;
            RequireMethod(typeof(LibraryVaultPanel), "SelectAssetCategory", [category, typeof(bool)])
                .Invoke(panel, [Enum.Parse(category, "Images"), true]);
            var tree = (TreeView)RequireField(typeof(LibraryVaultPanel), "_projectObjects").GetValue(panel)!;
            var nodes = tree.Nodes.Cast<TreeNode>().Where(node => node.Tag is not null).ToArray();
            RequireMethod(typeof(LibraryVaultPanel), "SetMultiSelection", [typeof(IEnumerable<TreeNode>)])
                .Invoke(panel, [nodes.Reverse()]);
            var payload = (VaultStageDragData?)RequireMethod(typeof(LibraryVaultPanel), "SelectedStageAssets", Type.EmptyTypes).Invoke(panel, null);
            AssertTimeline(payload is { Count: 3 } && payload.ImageIds.SequenceEqual(imageIds),
                "The library drag payload lost multi-selection or changed visual stacking order.");
            var data = new DataObject(); data.SetData(typeof(VaultStageDragData), payload!);
            var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            RequireMethod(type, "StageDragEnter", [typeof(object), typeof(DragEventArgs)]).Invoke(form, [stage, args]);
            AssertTimeline(args.Effect == DragDropEffects.Copy && scene.ObjectCount == originalCount,
                "Batch hover mutated the scene or rejected a valid selection.");
            var dropScreen = stage.PointToScreen(new Point(stage.Width / 2, stage.Height / 2));
            var drop = new DragEventArgs(data, 0, dropScreen.X, dropScreen.Y, DragDropEffects.Copy, DragDropEffects.None);
            RequireMethod(type, "StageDragDrop", [typeof(object), typeof(DragEventArgs)]).Invoke(form, [stage, drop]);
            AssertTimeline(drop.Effect == DragDropEffects.Copy && scene.ObjectCount == originalCount + 3 && stage.SelectedObjects.Count == 3,
                "The Stage drop handler did not place the complete library drag payload.");
            AssertTimeline(undo.Invoke(form, null) is true && scene.ObjectCount == originalCount,
                "The library drag/drop did not form one undo operation.");
            scene.SetLayerLocked(scene.ActiveLayer, true);
            AssertTimeline(canPlace.Invoke(form, [batch]) is false, "Batch image placement ignored the locked destination layer.");
            scene.SetLayerLocked(scene.ActiveLayer, false);
            var placedEvent = false;
            panel.AssetBatchActionRequested += (_, request) => placedEvent = request.Action == "Place" && request.ImageIds.Length == 3;
            RequireMethod(typeof(LibraryVaultPanel), "PlaceSelectedStageAssets", Type.EmptyTypes).Invoke(panel, null);
            AssertTimeline(placedEvent && scene.ObjectCount == originalCount + 3,
                "The multi-selection Place on Stage command did not place every selected image.");
            AssertTimeline(undo.Invoke(form, null) is true && scene.ObjectCount == originalCount,
                "The Place on Stage command did not form one undo operation.");
            var svgPath = Path.Combine(root, "shape.svg");
            File.WriteAllText(svgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\"/></svg>");
            AssertTimeline(project.TryAddExternalSvgAsset("SVG", svgPath, "", BitmapImageRasterizer.ComputeSha256(svgPath), out var svg),
                "Could not create the shared SVG fixture.");
            var mixedFiles = new VaultStageDragData(project.Id, [], [imageIds[0]], [svg!.Id]);
            AssertTimeline(place.Invoke(form, [mixedFiles, new PointF(250, 350)]) is true
                && scene.ObjectCount == originalCount + 2 && scene.TryGetImportedSvgAssetId(scene.ObjectCount - 1, out var svgId)
                && svgId == svg.Id, "Batch placement did not preserve SVG identity alongside images.");
            AssertTimeline(undo.Invoke(form, null) is true && scene.ObjectCount == originalCount && project.ExternalSvgAssets.Count == 1,
                "Mixed image/SVG placement was not undone as a whole.");
            var container = project.DrawingObjects[0];
            var childA = project.AddDrawingObject("Batch A");
            var childB = project.AddDrawingObject("Batch B");
            var nestedBefore = container.Instances.Count;
            var symbols = new VaultStageDragData(project.Id, [childA.Id, childB.Id], [], []);
            AssertTimeline(place.Invoke(form, [symbols, new PointF(400, 500)]) is true
                && container.Instances.Count == nestedBefore + 2
                && container.Instances.TakeLast(2).All(instance => instance.X == 400 && instance.Y == 500),
                "Multi-symbol placement lost an instance or missed the drop position.");
            AssertTimeline(undo.Invoke(form, null) is true && container.Instances.Count == nestedBefore,
                "Multi-symbol placement was not undone as a whole.");
            AssertTimeline(canPlace.Invoke(form, [symbols with { SymbolIds = [container.Id, childA.Id] }]) is false,
                "A multi-symbol drop accepted self-containment.");
            Console.WriteLine("library_stage_placement=passed,threshold=50,limit_boundary=ok,zero=ok,cancel=ok,images/svg/symbols=ok,selection=ok,undo=ok,drag_payload=ok,hover=nonmutating,context_command=ok,cycles=rejected");
        }
        finally { DeleteTemporaryDirectory(root); }
    }
}
