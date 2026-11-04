namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashNativeLibraryRegression()
    {
        var root = CreateTemporaryDirectory("flash-native-library");
        try
        {
            var holder = $"""
                <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Holder" symbolType="graphic">
                  <timeline><DOMTimeline name="Holder"><layers>
                    <DOMLayer name="Mixed"><frames><DOMFrame index="0" duration="2"><elements>
                      {FlashSquareFixture("#00FF00")}
                      <DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"/>
                      {FlashSquareFixture("#0000FF")}
                    </elements></DOMFrame></frames></DOMLayer>
                  </layers></DOMTimeline></timeline>
                </DOMSymbolItem>
                """;
            var document = FlashAnimationFixture().Replace("<Include href=\"Leaf.xml\"/>",
                "<Include href=\"Leaf.xml\"/><Include href=\"Holder.xml\"/>");
            var file = Path.Combine(root, "Native.fla");
            WriteFlashZipFixture(file, ("DOMDocument.xml", document),
                ("LIBRARY/Leaf.xml", FlashSymbolFixture()), ("LIBRARY/Holder.xml", holder));
            var imported = FlashDocumentImporter.Load(file);
            Check(imported.Project);
            var manifest = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "Saved", "Native.v2dProject"));
            Check(ProjectVaultStore.Load(manifest));
            Console.WriteLine("flash_native_library_count_layers_editability_roundtrip=passed");
            var maskedDocument = $"""
                <DOMDocument xmlns="{FlashFixtureNamespace}"><symbols><Include href="Leaf.xml"/></symbols>
                  <timelines><DOMTimeline name="Masked"><layers>
                    <DOMLayer name="Mask" layerType="mask"><frames><DOMFrame><elements>
                      <DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"/>
                    </elements></DOMFrame></frames></DOMLayer>
                    <DOMLayer name="Blue" parentLayerIndex="0"><frames><DOMFrame><elements>{FlashSquareFixture("#0000FF")}</elements></DOMFrame></frames></DOMLayer>
                    <DOMLayer name="Green" parentLayerIndex="0"><frames><DOMFrame><elements>{FlashSquareFixture("#00FF00")}</elements></DOMFrame></frames></DOMLayer>
                  </layers></DOMTimeline></timelines>
                </DOMDocument>
                """;
            var maskedPath = Path.Combine(root, "Masked.fla");
            WriteFlashZipFixture(maskedPath, ("DOMDocument.xml", maskedDocument), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
            var masked = FlashDocumentImporter.Load(maskedPath).Project;
            CheckMasks(masked);
            CheckMasks(ProjectVaultStore.Load(ProjectVaultStore.Save(masked, Path.Combine(root, "MaskSaved", "Masked.v2dProject"))));
            Console.WriteLine("flash_native_scene_masks_no_helper_symbols_roundtrip=passed");

            void CheckMasks(VectorProject project)
            {
                var scene = project.Scenes[0];
                var contents = scene.Layers.Where(layer => layer.Kind == SceneLayerKind.Content).ToArray();
                AssertTimeline(project.DrawingObjects.Count == 1 && contents.Length == 2
                    && contents.All(layer => layer.MaskLayerId.Length > 0)
                    && contents.Select(layer => layer.MaskLayerId).Distinct().Count() == 2
                    && scene.Layers.Where(layer => layer.Kind == SceneLayerKind.Mask)
                        .All(layer => layer.MaskScene is { ObjectCount: > 0 })
                    && scene.Instances.Count == 0,
                    "Scene masks lost native geometry/shared consumers or created helper symbols.");
            }

            void Check(VectorProject project)
            {
                AssertFlashAnimationProject(project, requireEditableGeometry: true);
                AssertTimeline(project.DrawingObjects.Count == 2
                    && project.DrawingObjects.Select(item => item.Name).Order().SequenceEqual(new[] { "Holder", "Leaf" })
                    && project.DrawingObjects.All(item => Enumerable.Range(0, item.Scene.ObjectCount)
                        .All(index => item.Scene.ShapeKind[index] != ShapeKind.ImportedSvg)),
                    "FLA import created extra helper assets or retained opaque SVG leaves.");
                var nested = project.DrawingObjects.Single(item => item.Name == "Holder");
                AssertTimeline(nested.Scene.ObjectCount == 2 && nested.Instances.Count == 1
                    && nested.Scene.LayerNames.Contains("Mixed")
                    && nested.Instances[0].DrawingObjectId == project.DrawingObjects.Single(item => item.Name == "Leaf").Id,
                    "Native import lost editable drawing, source layers or the reusable nested reference.");
                var composed = new VectorScene();
                SceneCompositionBuilder.BuildDrawingObjectPreview(composed, nested, project.DrawingObjects,
                    PointF.Empty, 0, opacity: 1);
                AssertTimeline(composed.ObjectCount == 3, "Mixed native drawing and nested symbol lost content.");
                AssertFlashFramePixel(composed, Color.Blue);
                AssertTimeline(project.Scenes[0].DrawingScene.ObjectCount == 1,
                    "Scene drawing remained in an auxiliary library symbol.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
