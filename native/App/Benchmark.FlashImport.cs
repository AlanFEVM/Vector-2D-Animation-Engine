using System.IO.Compression;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const string FlashFixtureNamespace = "http://ns.adobe.com/xfl/2008/";

    private static void RunFlashImportRegression()
    {
        var root = CreateTemporaryDirectory("flash-import");
        try
        {
            var document = FlashAnimationFixture();
            var symbol = FlashSymbolFixture();
            var xflDirectory = Path.Combine(root, "Uncompressed");
            Directory.CreateDirectory(Path.Combine(xflDirectory, "LIBRARY"));
            File.WriteAllText(Path.Combine(xflDirectory, "DOMDocument.xml"), document);
            File.WriteAllText(Path.Combine(xflDirectory, "LIBRARY", "Leaf.xml"), symbol);
            var marker = Path.Combine(xflDirectory, "Animation.xfl");
            File.WriteAllText(marker, "PROXY-CS5");

            var fla = Path.Combine(root, "Animation.fla");
            WriteFlashZipFixture(fla, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
            AssertTimeline(FlashDocumentImporter.IsZipContainer(fla)
                && !FlashDocumentImporter.IsZipContainer(marker),
                "Flash container detection confused a ZIP FLA with an uncompressed XFL marker.");

            foreach (var source in new[] { fla, marker, xflDirectory, Path.Combine(xflDirectory, "DOMDocument.xml") })
            {
                var imported = FlashDocumentImporter.Load(source);
                AssertTimeline(imported.Width == 160 && imported.Height == 120
                    && imported.Fps == 23.976m && imported.FrameCount == 4
                    && imported.Project.PlaybackFps == 23.976m
                    && imported.Project.PlaybackEndFrame == 3,
                    "Flash import lost stage dimensions, fractional FPS, or the final exposure frame.");
                AssertFlashAnimationProject(imported.Project, requireEditableGeometry: true);
            }

            var project = FlashDocumentImporter.Load(fla).Project;
            var automatic = FlashDocumentImporter.Load(fla);
            var automaticDirectory = Path.Combine(root, "Automatic");
            var automaticManifest = MainForm.SaveImportedFlashProject(automatic, automaticDirectory);
            AssertTimeline(File.Exists(automaticManifest), "Flash conversion did not automatically create its manifest.");
            AssertFlashAnimationProject(ProjectVaultStore.Load(automaticManifest), requireEditableGeometry: true);
            var manifestBefore = File.ReadAllBytes(automaticManifest);
            ExpectInvalidData(() => MainForm.SaveImportedFlashProject(automatic, automaticDirectory), "Flash conversion reused an occupied project directory.");
            AssertTimeline(File.ReadAllBytes(automaticManifest).SequenceEqual(manifestBefore), "Rejected Flash destination changed the existing project.");
            ExpectInvalidData(() => MainForm.ValidateFlashProjectDirectory(xflDirectory), "Flash conversion accepted its occupied source directory.");
            using (var savingDialog = new FlashImportProgressDialog())
            {
                savingDialog.BeginSaving();
                AssertTimeline(savingDialog.CancelButton is Button { Enabled: false } && !savingDialog.CancellationRequested,
                    "Flash saving allowed cancellation while its transaction was running.");
                savingDialog.Complete();
            }
            var ids = project.DrawingObjects.Select(item => item.Id).ToArray();
            var savePath = Path.Combine(root, "Saved", "Animation.v2dProject");
            ProjectVaultStore.Save(project, savePath);
            var reopened = ProjectVaultStore.Load(savePath);
            AssertTimeline(reopened.DrawingObjects.Select(item => item.Id).SequenceEqual(ids),
                "Saving and reopening an imported Flash project replaced stable symbol identifiers.");
            AssertFlashAnimationProject(reopened, requireEditableGeometry: true);

            RunFlashLayerOrderRegression(root);
            RunFlashDirectZipCompatibilityRegression(root);
            RunFlashBitmapRegression(root);
            RunFlashNativeStructureRegression(root);
            RunFlashFolderWhitespaceRegression(root);
            RunFlashFolderExposureRegression(root);
            RunFlashGeometryPixelRegression(root);
            RunFlashSolidPaintRegression();
            RunFlashDenseShapeRegression(root);
            RunFlashBlendRegression(root);
            RunFlashEmbeddedImageRegression(root);
            RunFlashSymbolPlaybackRegression(root);
            RunFlashNestedPlaybackRegression(root);
            RunFlashDiagnosticRegression(root, document, symbol);
            RunFlashRejectedInputRegression(root, document, symbol);
            RunFlashLegacyBridgeRegression(root, document, symbol);
            RunFlashVisualBridgeRegression(root, document, symbol);
            Console.WriteLine("flash_import_regression=ok");
        }
        finally { DeleteTemporaryDirectory(root); }
    }

    private static void RunFlashBlendRegression(string root)
    {
        var modes = new Dictionary<string, LayerBlendMode>
        {
            ["normal"] = LayerBlendMode.Normal, ["layer"] = LayerBlendMode.Isolate,
            ["multiply"] = LayerBlendMode.Multiply, ["screen"] = LayerBlendMode.Screen,
            ["lighten"] = LayerBlendMode.Lighten, ["darken"] = LayerBlendMode.Darken,
            ["difference"] = LayerBlendMode.Difference, ["add"] = LayerBlendMode.LinearDodge,
            ["subtract"] = LayerBlendMode.Subtract, ["invert"] = LayerBlendMode.Invert,
            ["overlay"] = LayerBlendMode.Overlay, ["hardlight"] = LayerBlendMode.HardLight
        };
        foreach (var (name, mode) in modes)
        {
            var path = Path.Combine(root, "Blend-" + name + ".fla");
            var document = FlashAnimationFixture().Replace("libraryItemName=\"Leaf\"", $"libraryItemName=\"Leaf\" blendMode=\"{name}\"", StringComparison.Ordinal);
            WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
            var imported = FlashDocumentImporter.Load(path);
            Check(imported.Project);
            var manifest = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "BlendSaved-" + name, "Converted.v2dProject"));
            Check(ProjectVaultStore.Load(manifest));
            void Check(VectorProject project)
            {
                AssertTimeline(project.Scenes[0].Instances.Where(instance => instance.EvaluateState(0).Visible && instance.Name == "Leaf").Single().EvaluateState(0).BlendMode == mode,
                    "Flash instance blend mode was lost during import or saving.");
                var scene = new VectorScene();
                SceneCompositionBuilder.Build(scene, project.Scenes[0], project.DrawingObjects, 0, imported.Fps);
                AssertTimeline(mode == LayerBlendMode.Normal || scene.LayerBlendModes.Contains(mode),
                    "Imported Flash blend mode did not reach composed rendering layers.");
            }
        }
        foreach (var name in new[] { "alpha", "erase", "shader", "3", "unknown" })
        {
            var path = Path.Combine(root, "RejectedBlend-" + name + ".fla");
            WriteFlashZipFixture(path, ("DOMDocument.xml", FlashAnimationFixture().Replace("libraryItemName=\"Leaf\"", $"libraryItemName=\"Leaf\" blendMode=\"{name}\"", StringComparison.Ordinal)), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
            ExpectInvalidData(() => FlashDocumentImporter.Load(path), "Unsupported Flash blend was silently accepted.");
        }
        // Invalid geometry comes first: effect preflight must reject the later filter
        // before beginning any expensive geometry conversion.
        var filtered = Path.Combine(root, "FilterPreflight.fla");
        var xml = FlashAnimationFixture().Replace("<DOMSymbolInstance ", "<DOMShape><edges><Edge edges='invalid'/></edges></DOMShape><DOMSymbolInstance ", StringComparison.Ordinal)
            .Replace("libraryItemName=\"Leaf\"", "libraryItemName=\"Leaf\" blendMode=\"add\"", StringComparison.Ordinal)
            .Replace("<matrix>", "<filters><GlowFilter blurX='20' blurY='20' quality='3'/></filters><matrix>", StringComparison.Ordinal);
        WriteFlashZipFixture(filtered, ("DOMDocument.xml", xml), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
        var before = File.ReadAllBytes(filtered);
        try { FlashDocumentImporter.Load(filtered); throw new InvalidOperationException("Unsupported Flash filter was omitted."); }
        catch (InvalidDataException exception)
        {
            AssertTimeline(exception.Message.Contains("GlowFilter", StringComparison.Ordinal)
                && exception.Message.Contains("quality=3", StringComparison.Ordinal)
                && exception.Message.Contains("frame 1", StringComparison.Ordinal),
                "Effect preflight did not identify the exact filter and frame before geometry conversion.");
        }
        AssertTimeline(File.ReadAllBytes(filtered).SequenceEqual(before), "Effect preflight changed the source FLA.");
    }

    private static void RunFlashBitmapRegression(string root)
    {
        var folder = Path.Combine(root, "BitmapXfl");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        var xml = $"<DOMDocument xmlns='{FlashFixtureNamespace}'><media><DOMBitmapItem name='Picture' bitmapDataHRef='M 1.dat' isJPEG='true'/></media><timelines><DOMTimeline name='Bitmap Scene'><layers><DOMLayer name='Images'><frames><DOMFrame index='0' duration='3'><elements><DOMBitmapInstance libraryItemName='Picture'><matrix><Matrix a='2' d='3' tx='10' ty='20'/></matrix></DOMBitmapInstance><DOMBitmapInstance libraryItemName='Picture'><matrix><Matrix tx='40' ty='50'/></matrix></DOMBitmapInstance></elements></DOMFrame></frames></DOMLayer></layers></DOMTimeline></timelines></DOMDocument>";
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"), xml);
        var source = Path.Combine(folder, "bin", "M 1.dat");
        using (var image = new Bitmap(4, 3))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Red);
            image.Save(source, ImageFormat.Jpeg);
        }
        var original = File.ReadAllBytes(source);
        var imported = FlashDocumentImporter.Load(folder);
        try
        {
            AssertTimeline(imported.Project.ImageAssets.Count == 1, "Bitmap instances did not share a native image asset.");
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, imported.Project.Scenes[0], imported.Project.DrawingObjects, 2, imported.Fps);
            AssertTimeline(composed.ObjectCount == 2 && composed.ShapeKind.Take(2).All(kind => kind == ShapeKind.Bitmap),
                "Flash bitmap placements or held exposure were lost.");
            AssertTimeline(composed.Width.Take(2).Any(width => MathF.Abs(width - VectorUnits.FromPixels(8)) < 0.01f)
                && composed.Height.Take(2).Any(height => MathF.Abs(height - VectorUnits.FromPixels(9)) < 0.01f),
                "Flash bitmap placement lost matrix scale.");
            var manifest = MainForm.SaveImportedFlashProject(imported, Path.Combine(root, "BitmapSaved"));
            var reopened = ProjectVaultStore.Load(manifest);
            FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles);
            AssertTimeline(File.ReadAllBytes(reopened.ImageAssets.Single().SourcePath).SequenceEqual(original),
                "Flash JPEG was re-encoded or its managed copy was lost.");
            ProjectVaultStore.Save(reopened, manifest);
            AssertTimeline(ProjectVaultStore.Load(manifest).ImageAssets.Count == 1, "Re-saving lost the imported bitmap.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }
        byte[] Lossless(byte alpha, byte red, byte green, byte blue)
        {
            var header = new byte[26];
            header[0] = 3; header[1] = 5; header[24] = alpha == 255 ? (byte)0 : (byte)1; header[25] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 80);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 60);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                for (var i = 0; i < 12; i++) zlib.Write([alpha, red, green, blue]);
            var payload = compressed.ToArray();
            using var result = new MemoryStream();
            result.Write(header);
            result.Write(BitConverter.GetBytes((ushort)2)); result.Write(payload, 0, 2);
            result.Write(BitConverter.GetBytes((ushort)(payload.Length - 2))); result.Write(payload, 2, payload.Length - 2);
            result.Write([0, 0]);
            return result.ToArray();
        }
        File.WriteAllBytes(source, Lossless(255, 19, 73, 201));
        imported = FlashDocumentImporter.Load(folder);
        try
        {
            using var decoded = new Bitmap(imported.Project.ImageAssets.Single().SourcePath);
            AssertTimeline(decoded.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 19, 73, 201).ToArgb(),
                "Lossless XFL decoder changed colour channels.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }
        File.WriteAllBytes(source, Lossless(129, 10, 20, 30));
        ExpectInvalidData(() => FlashDocumentImporter.Load(folder), "Quantized transparency was silently accepted without its original PNG.");
        Directory.CreateDirectory(Path.Combine(folder, "LIBRARY"));
        using (var originalImage = new Bitmap(4, 3))
        {
            for (var y = 0; y < 3; y++) for (var x = 0; x < 4; x++) originalImage.SetPixel(x, y, Color.FromArgb(128, 20, 40, 60));
            originalImage.Save(Path.Combine(folder, "LIBRARY", "Picture.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"), xml.Replace("name='Picture' bitmapDataHRef", "name='Picture' href='Picture.png' bitmapDataHRef"));
        imported = FlashDocumentImporter.Load(folder);
        try
        {
            AssertTimeline(File.ReadAllBytes(imported.Project.ImageAssets.Single().SourcePath).SequenceEqual(
                File.ReadAllBytes(Path.Combine(folder, "LIBRARY", "Picture.png"))), "Original transparent PNG was not preserved.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }
        File.WriteAllBytes(source, [3, 5, 1, 2]);
        ExpectInvalidData(() => FlashDocumentImporter.Load(folder), "Unknown Flash bitmap encoding was silently accepted.");
    }

    private static void RunFlashDirectZipCompatibilityRegression(string root)
    {
        var zip = Path.Combine(root, "AnimateDirectory.fla");
        WriteFlashZipFixture(zip, ("mimetype", "application/vnd.adobe.xfl"), ("DOMDocument.xml", FlashAnimationFixture()), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
        var original = File.ReadAllBytes(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(4, 2), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(6, 2), 0x0800);
        var eocd = original.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(eocd + 16, 4));
        var orphaned = original[..central].Concat(original[(central + 54)..]).ToArray();
        eocd -= 54;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(orphaned.AsSpan(eocd + 10, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(orphaned.AsSpan(eocd + 8, 2), (ushort)(count - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(orphaned.AsSpan(eocd + 10, 2), (ushort)(count - 1));
        File.WriteAllBytes(zip, orphaned);
        AssertFlashAnimationProject(FlashDocumentImporter.Load(zip).Project);
        AssertTimeline(File.ReadAllBytes(zip).SequenceEqual(orphaned), "ZIP compatibility rewrote the source FLA.");
        orphaned[38] ^= 1;
        File.WriteAllBytes(zip, orphaned);
        ExpectInvalidData(() => FlashDocumentImporter.Load(zip), "ZIP compatibility accepted an unknown orphan payload.");

        var held = $"<DOMDocument xmlns='{FlashFixtureNamespace}'><symbols><Include href='Leaf.xml'/></symbols><timelines><DOMTimeline name='Held'><layers><DOMLayer name='Held'><frames><DOMFrame index='0' duration='10000'><elements><DOMSymbolInstance libraryItemName='Leaf' symbolType='graphic' loop='single frame'/></elements></DOMFrame></frames></DOMLayer></layers></DOMTimeline></timelines></DOMDocument>";
        WriteFlashZipFixture(zip, ("DOMDocument.xml", held), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
        var imported = FlashDocumentImporter.Load(zip);
        AssertTimeline(imported.Project.Scenes[0].Instances.Single().StateKeyframes.Count <= 1, "Static Flash exposure stored redundant frame states.");
        var composed = new VectorScene();
        SceneCompositionBuilder.Build(composed, imported.Project.Scenes[0], imported.Project.DrawingObjects, 9999, imported.Fps);
        AssertTimeline(composed.ObjectCount > 0, "Compressed exposure disappeared at its last frame.");
    }

    private static void AssertFlashAnimationProject(VectorProject project, bool requireEditableGeometry = false)
    {
        AssertTimeline(project.Scenes.Count == 1 && project.Scenes[0].FrameCount == 4
            && project.DrawingObjects.Any(item => item.Name == "Leaf")
            && project.DrawingObjects.Sum(item => item.Instances.Count) > 0,
            "Flash import did not preserve its scene, reusable library symbol, or nested references.");
        var scene = project.Scenes[0];
        AssertTimeline(scene.Layers.Select(layer => layer.Name).SequenceEqual(new[] { "Animated", "Hidden" }),
            "Flash scene layers were flattened into a synthetic timeline symbol.");
        var layer = scene.Layers.Single(item => item.Name == "Animated");
        var track = scene.Timeline.FindTrackByTargetId(layer.Id)!;
        AssertTimeline(track.Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0, 2, 3 }),
            "Flash held exposures were expanded into extra native timeline keyframes.");
        AssertTimeline(track.EvaluateExposure(0).HasContent && track.EvaluateExposure(1).HasContent
            && !track.EvaluateExposure(2).HasContent && track.EvaluateExposure(3).HasContent,
            "Flash populated, held, blank, and resumed exposures do not match the source frames.");
        var hidden = scene.Layers.Single(item => item.Name == "Hidden");
        AssertTimeline(!hidden.Visible,
            "Flash import lost the hidden source layer's visibility.");
        var leaf = project.DrawingObjects.Single(item => item.Name == "Leaf");
        if (requireEditableGeometry)
        {
            var kinds = project.DrawingObjects.SelectMany(item => item.Scene.ShapeKind.Take(item.Scene.ObjectCount)).ToArray();
            AssertTimeline(kinds.Length > 0 && !kinds.Contains(ShapeKind.ImportedSvg)
                && kinds.Any(kind => kind is ShapeKind.Path or ShapeKind.Freeform),
                "Automatic Flash import left a vector leaf as an SVG object instead of editable native geometry.");
        }
        AssertTimeline(scene.InstancesInLayer(layer.Id).Count == 2
            && scene.InstancesInLayer(layer.Id).All(instance => instance.DrawingObjectId == leaf.Id)
            && !project.DrawingObjects.Any(item => item.Scene.LayerNames.Contains("Animated")),
            "Flash scene instances do not directly reuse the native library symbol or retained a staging wrapper.");

        var destination = new VectorScene();
        for (var frame = 0; frame < 4; frame++)
        {
            var composition = SceneCompositionBuilder.Build(destination, project.Scenes[0],
                project.DrawingObjects, frame, project.PlaybackFps);
            if (frame == 2)
            {
                AssertTimeline(destination.ObjectCount == 0,
                    "Flash blank frame retained the preceding cel or leaked hidden layer content.");
                continue;
            }

            AssertTimeline(destination.ObjectCount == 1 && composition.ObjectOwners.Count == 1,
                "Flash frame did not compose exactly its one visible symbol with matching provenance.");
            // The second populated key rotates a 10px square and scales its axes by 2 and 3.
            // These expected bounds come from the source matrix, independently of native decomposition.
            var expected = frame == 3 ? new RectangleF(30, 80, 30, 20) : new RectangleF(20, 30, 10, 10);
            AssertFlashBounds(destination, 0, expected);
            AssertFlashFramePixel(destination, Color.Red);
        }
    }

    private static void AssertFlashBounds(VectorScene scene, int objectIndex, RectangleF expectedPixels)
    {
        // The spatial-query bounds include a minimum 1vu hit-test margin even on SVG
        // objects without a native stroke. Remove that documented margin before
        // comparing source content geometry; keep the affine tolerance unchanged.
        var actual = scene.GetObjectWorldBounds(objectIndex);
        var margin = Math.Max(scene.Stroke[objectIndex] * 0.5f, 1);
        actual.Inflate(-margin, -margin);
        var scale = VectorUnits.UnitsPerPixel;
        AssertTimeline(Math.Abs(actual.X - expectedPixels.X * scale) < 0.1f
            && Math.Abs(actual.Y - expectedPixels.Y * scale) < 0.1f
            && Math.Abs(actual.Width - expectedPixels.Width * scale) < 0.1f
            && Math.Abs(actual.Height - expectedPixels.Height * scale) < 0.1f,
            $"Flash twip/matrix transform changed visible bounds: actual={actual}, expectedPixels={expectedPixels}.");
    }

    private static void AssertFlashFramePixel(VectorScene scene, Color expected)
    {
        var document = DrawingObjectSvgExport.CreateDocument("flash-frame", "Flash frame", scene, 0);
        var raster = ImportedSvgRasterizer.Rasterize(document.ToString(), 64, 64);
        using var bitmap = raster.AcquireBitmap();
        var center = bitmap.Bitmap.GetPixel(32, 32);
        AssertTimeline(center.A >= 250 && Math.Abs(center.R - expected.R) <= 2
            && Math.Abs(center.G - expected.G) <= 2 && Math.Abs(center.B - expected.B) <= 2,
            $"Flash frame has the wrong visible fill/stacking colour: actual={center}, expected={expected}.");
    }

    private static void RunFlashLayerOrderRegression(string root)
    {
        var path = Path.Combine(root, "LayerOrder.fla");
        var document = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="40" height="40" frameRate="24">
              <timelines><DOMTimeline name="Stacking"><layers>
                <DOMLayer name="Top"><frames><DOMFrame index="0"><elements>{FlashSquareFixture("#FF0000")}</elements></DOMFrame></frames></DOMLayer>
                <DOMLayer name="Bottom"><frames><DOMFrame index="0"><elements>{FlashSquareFixture("#0000FF")}</elements></DOMFrame></frames></DOMLayer>
              </layers></DOMTimeline></timelines>
            </DOMDocument>
            """;
        WriteFlashZipFixture(path, ("DOMDocument.xml", document));
        var imported = FlashDocumentImporter.Load(path);
        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, imported.Project.Scenes[0], imported.Project.DrawingObjects, 0, imported.Fps);
        AssertFlashFramePixel(destination, Color.Red);

        // Adobe element order within one layer is back-to-front as well.
        var sameLayer = Path.Combine(root, "SameLayerOrder.fla");
        WriteFlashZipFixture(sameLayer, ("DOMDocument.xml", FlashShapeDocument(
            FlashSquareFixture("#0000FF") + FlashSquareFixture("#FF0000"))));
        var sameLayerImported = FlashDocumentImporter.Load(sameLayer);
        SceneCompositionBuilder.Build(destination, sameLayerImported.Project.Scenes[0], sameLayerImported.Project.DrawingObjects, 0);
        AssertFlashFramePixel(destination, Color.Red);
    }

    private static void RunFlashNativeStructureRegression(string root)
    {
        var document = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="100" height="40" frameRate="24">
              <folders><DOMFolderItem name="Assets"/><DOMFolderItem name="Assets/Unused/Empty"/></folders>
              <symbols><Include href="Assets/Shapes/Leaf.xml"/><Include href="Other/Leaf.xml"/></symbols>
              <timelines>
                <DOMTimeline name="First"><layers>
                  <DOMLayer name="Foreground" color="#112233"><frames><DOMFrame index="0" duration="3"><elements>
                    <DOMSymbolInstance libraryItemName="Assets/Shapes/Leaf" name="Left" symbolType="graphic" loop="single frame"/>
                    <DOMSymbolInstance libraryItemName="Assets/Shapes/Leaf" name="Right" symbolType="graphic" loop="single frame"><matrix><Matrix tx="20"/></matrix></DOMSymbolInstance>
                  </elements></DOMFrame></frames></DOMLayer>
                  <DOMLayer name="Hidden" visible="false" color="#445566" outline="true"><frames><DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Other/Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame></frames></DOMLayer>
                  <DOMLayer name="Group" layerType="folder" locked="true" color="#667788" outline="true"/>
                  <DOMLayer name="Locked Child" parentLayerIndex="2" locked="true" color="#778899"><frames><DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Assets/Shapes/Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="40"/></matrix></DOMSymbolInstance></elements></DOMFrame></frames></DOMLayer>
                  <DOMLayer name="Nested Folder" layerType="folder" parentLayerIndex="2" visible="false" color="#8899AA"/>
                  <DOMLayer name="Nested Hidden" parentLayerIndex="4" outline="true" color="#99AABB"><frames><DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Other/Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame></frames></DOMLayer>
                  <DOMLayer name="Second Child" parentLayerIndex="2" color="#22AA44"><frames><DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Other/Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="60"/></matrix></DOMSymbolInstance></elements></DOMFrame></frames></DOMLayer>
                  <DOMLayer name="Locked Root" locked="true" visible="false" color="#ABCDEF" outline="true"><frames><DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Other/Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame></frames></DOMLayer>
                </layers></DOMTimeline>
                <DOMTimeline name="Second"><layers><DOMLayer name="Blue" color="#2468AC"><frames>
                  <DOMFrame index="0" duration="2"><elements><DOMSymbolInstance libraryItemName="Other/Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="60"/></matrix></DOMSymbolInstance></elements></DOMFrame>
                </frames></DOMLayer></layers></DOMTimeline>
              </timelines>
            </DOMDocument>
            """;
        var leaf = $"""
            <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Assets/Shapes/Leaf" symbolType="graphic">
              <timeline><DOMTimeline name="Leaf"><layers>
                <DOMLayer name="Library Group" layerType="folder" locked="true" color="#AA4422"/>
                <DOMLayer name="Fill" parentLayerIndex="0" color="#102030"><frames><DOMFrame index="0"><elements>{FlashSquareFixture("#FF0000")}</elements></DOMFrame></frames></DOMLayer>
                <DOMLayer name="Sketch" parentLayerIndex="0" visible="false" locked="true" outline="true" color="#305070"><frames><DOMFrame index="0"><elements/></DOMFrame></frames></DOMLayer>
              </layers></DOMTimeline></timeline>
            </DOMSymbolItem>
            """;
        var blueLeaf = FlashSymbolFixture().Replace("name=\"Leaf\"", "name=\"Other/Leaf\"", StringComparison.Ordinal)
            .Replace("#FF0000", "#0000FF", StringComparison.Ordinal);
        var path = Path.Combine(root, "NativeStructure.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", document),
            ("LIBRARY/Assets/Shapes/Leaf.xml", leaf), ("LIBRARY/Other/Leaf.xml", blueLeaf));
        var project = FlashDocumentImporter.Load(path).Project;
        AssertFlashNativeStructure(project);
        var structure = FlashNativeStructureSignature(project);
        var saved = Path.Combine(root, "NativeStructureSaved", "NativeStructure.v2dProject");
        ProjectVaultStore.Save(project, saved);
        var reopened = ProjectVaultStore.Load(saved);
        AssertTimeline(FlashNativeStructureSignature(reopened) == structure,
            "Flash native folders, scene/layer/symbol/instance/track identifiers or state keys changed after Save/Open.");
        AssertFlashNativeStructure(reopened);
    }

    private static void RunFlashFolderWhitespaceRegression(string root)
    {
        var path = Path.Combine(root, "FolderWhitespace.fla");
        var document = FlashAnimationFixture().Replace("libraryItemName=\"Leaf\"", "libraryItemName=\"Sprites /Leaf\"", StringComparison.Ordinal);
        var symbol = FlashSymbolFixture().Replace("name=\"Leaf\"", "name=\"Sprites /Leaf\"", StringComparison.Ordinal);
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
        var result = FlashDocumentImporter.Load(path);
        var project = result.Project;
        AssertTimeline(project.AssetFolders.Single().Name == "Sprites"
            && project.DrawingObjects.Single(item => item.Name == "Leaf").AssetFolderId == project.AssetFolders.Single().Id,
            "Flash library folder whitespace normalization lost the native folder identity.");
        AssertTimeline(result.Diagnostics.Any(item => item.Code == "library-folder-name-normalized"),
            "Flash library folder whitespace was silently trimmed.");
        var saved = Path.Combine(root, "FolderWhitespaceSaved", "FolderWhitespace.v2dProject");
        ProjectVaultStore.Save(project, saved);
        var reopened = ProjectVaultStore.Load(saved);
        AssertTimeline(reopened.AssetFolders.Single().Name == "Sprites", "Flash normalized folder name did not survive Save/Open.");
    }

    private static void AssertFlashNativeStructure(VectorProject project)
    {
        AssertTimeline(project.Scenes.Select(scene => scene.Name).SequenceEqual(new[] { "First", "Second" })
            && project.Scenes.Select(scene => scene.FrameCount).SequenceEqual(new[] { 3, 2 }),
            "Flash import merged source scenes or changed their individual durations.");
        var paths = project.AssetFolders.ToDictionary(folder => folder.Id,
            folder => FlashAssetFolderPath(project, folder.Id), StringComparer.Ordinal);
        AssertTimeline(paths.Values.OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(
                new[] { "Assets", "Assets/Shapes", "Assets/Unused", "Assets/Unused/Empty", "Other" }),
            "Flash import did not retain slash-separated library folders and explicitly empty folders.");
        var leaves = project.DrawingObjects.Where(symbol => symbol.Name == "Leaf").ToArray();
        AssertTimeline(leaves.Length == 2, "Distinct Flash library paths with the same leaf name were merged or duplicated.");
        var red = leaves.Single(symbol => paths.GetValueOrDefault(symbol.AssetFolderId) == "Assets/Shapes");
        var blue = leaves.Single(symbol => paths.GetValueOrDefault(symbol.AssetFolderId) == "Other");
        AssertTimeline(red.Id != blue.Id, "Flash library identity was reduced to the display basename.");
        var library = red.Scene;
        AssertTimeline(library.LayerNames.SequenceEqual(new[] { "Library Group", "Fill", "Sketch" })
            && library.LayerKinds[0] == DrawingLayerKind.Folder
            && library.LayerParentIds[1] == library.LayerIds[0]
            && library.LayerParentIds[2] == library.LayerIds[0]
            && library.LayerLocked[0] && !library.LayerLocked[1] && library.LayerLocked[2]
            && library.LayerVisible[0] && library.LayerVisible[1] && !library.LayerVisible[2]
            && library.LayerColorArgb.SequenceEqual(new[] { Color.FromArgb(0xAA, 0x44, 0x22).ToArgb(),
                Color.FromArgb(0x10, 0x20, 0x30).ToArgb(), Color.FromArgb(0x30, 0x50, 0x70).ToArgb() })
            && library.LayerOutline.SequenceEqual(new[] { false, false, true }),
            "Flash library symbol lost native layer hierarchy, visibility, lock, color or outline metadata.");

        var first = project.Scenes[0];
        AssertTimeline(first.Layers.Select(layer => layer.Name).SequenceEqual(new[] { "Foreground", "Hidden", "Group", "Locked Root" }),
            "Flash root layers were not reconstructed as native scene layers.");
        var foreground = first.Layers[0];
        var hidden = first.Layers[1];
        AssertTimeline(foreground.Visible && !foreground.Outline
            && foreground.ColorArgb == Color.FromArgb(0x11, 0x22, 0x33).ToArgb()
            && !hidden.Visible && hidden.Outline && hidden.ColorArgb == Color.FromArgb(0x44, 0x55, 0x66).ToArgb(),
            "Flash ordinary scene layers lost native visibility, color or outline metadata.");
        var references = first.InstancesInLayer(foreground.Id);
        AssertTimeline(references.Count == 2 && references.All(instance => instance.DrawingObjectId == red.Id)
            && references.Select(instance => instance.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "Left", "Right" })
            && first.InstancesInLayer(hidden.Id).Single().DrawingObjectId == blue.Id,
            "Flash scene placements cloned reusable library definitions or lost their names/identities.");
        var group = project.DrawingObjects.Single(symbol => symbol.Id == first.InstancesInLayer(first.Layers[2].Id).Single().DrawingObjectId);
        var nativeGroup = group.Scene;
        AssertTimeline(nativeGroup.LayerNames.SequenceEqual(new[] { "Group", "Locked Child", "Nested Folder", "Nested Hidden", "Second Child" })
            && nativeGroup.LayerKinds[0] == DrawingLayerKind.Folder
            && nativeGroup.LayerKinds[2] == DrawingLayerKind.Folder
            && nativeGroup.LayerParentIds[1] == nativeGroup.LayerIds[0]
            && nativeGroup.LayerParentIds[2] == nativeGroup.LayerIds[0]
            && nativeGroup.LayerParentIds[3] == nativeGroup.LayerIds[2]
            && nativeGroup.LayerParentIds[4] == nativeGroup.LayerIds[0]
            && nativeGroup.LayerLocked[0] && nativeGroup.LayerLocked[1]
            && !nativeGroup.LayerVisible[2] && nativeGroup.LayerOutline[3] && nativeGroup.LayerOutline[0]
            && !first.Layers[2].Outline
            && nativeGroup.LayerColorArgb.SequenceEqual(new[] { Color.FromArgb(0x66, 0x77, 0x88).ToArgb(),
                Color.FromArgb(0x77, 0x88, 0x99).ToArgb(), Color.FromArgb(0x88, 0x99, 0xAA).ToArgb(),
                Color.FromArgb(0x99, 0xAA, 0xBB).ToArgb(), Color.FromArgb(0x22, 0xAA, 0x44).ToArgb() }),
            "Flash scene-folder projection lost its editable source hierarchy or layer metadata.");
        var locked = project.DrawingObjects.Single(symbol => symbol.Id == first.InstancesInLayer(first.Layers[3].Id).Single().DrawingObjectId);
        AssertTimeline(locked.Scene.LayerCount == 1 && locked.Scene.LayerNames[0] == "Locked Root"
            && locked.Scene.LayerLocked[0]
            && locked.Scene.LayerColorArgb[0] == Color.FromArgb(0xAB, 0xCD, 0xEF).ToArgb()
            && !first.Layers[3].Visible && first.Layers[3].Outline,
            "Flash locked root layer did not retain its lock/visibility/outline in an editable native group.");
        AssertTimeline(!project.DrawingObjects.Any(symbol => symbol.Name is "First Timeline" or "Second Timeline")
            && !project.DrawingObjects.Any(symbol => symbol.Scene.LayerNames.Contains("Foreground")),
            "Flash scene projection retained a duplicate entire-timeline staging symbol.");
        foreach (var scene in project.Scenes)
        foreach (var track in scene.Timeline.Tracks)
            AssertTimeline(track.Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0 }),
                "Flash scene held exposures gained extra timeline keyframes during native projection.");

        var destination = new VectorScene();
        for (var frame = 0; frame < first.FrameCount; frame++)
        {
            SceneCompositionBuilder.Build(destination, first, project.DrawingObjects, frame, project.PlaybackFps);
            AssertTimeline(destination.ObjectCount == 4, "Flash scene reconstruction leaked hidden content or lost group content.");
            var positions = Enumerable.Range(0, destination.ObjectCount)
                .OrderBy(index => destination.GetObjectWorldBounds(index).Left).ToArray();
            for (var index = 0; index < positions.Length; index++)
            {
                AssertFlashBounds(destination, positions[index], new RectangleF(index * 20, 0, 10, 10));
                if (index < 2) AssertFlashObjectPixel(destination, positions[index], Color.Red);
                else
                {
                    var composedLayer = destination.ObjectLayer[positions[index]];
                    var expectedOutline = index == 2 ? Color.FromArgb(0x77, 0x88, 0x99) : Color.FromArgb(0x22, 0xAA, 0x44);
                    AssertTimeline(destination.LayerOutline[composedLayer]
                        && destination.LayerColorArgb[composedLayer] == expectedOutline.ToArgb(),
                        "Flash folder outline replaced distinct child layer colors with the folder color.");
                }
            }
        }
        for (var frame = 0; frame < project.Scenes[1].FrameCount; frame++)
        {
            SceneCompositionBuilder.Build(destination, project.Scenes[1], project.DrawingObjects, frame, project.PlaybackFps);
            AssertTimeline(destination.ObjectCount == 1, "Flash second scene contains unexpected geometry.");
            AssertFlashBounds(destination, 0, new RectangleF(60, 0, 10, 10));
            AssertFlashFramePixel(destination, Color.Blue);
        }
    }

    private static string FlashAssetFolderPath(VectorProject project, string folderId)
    {
        var folder = project.AssetFolders.Single(item => item.Id == folderId);
        return string.IsNullOrEmpty(folder.ParentFolderId) ? folder.Name
            : FlashAssetFolderPath(project, folder.ParentFolderId) + "/" + folder.Name;
    }

    private static string FlashNativeStructureSignature(VectorProject project) => JsonSerializer.Serialize(new
    {
        project.Id,
        Folders = project.AssetFolders.Select(folder => new { folder.Id, folder.Name, folder.ParentFolderId }),
        Symbols = project.DrawingObjects.Select(symbol => new
        {
            symbol.Id, symbol.Name, symbol.AssetFolderId,
            symbol.Scene.LayerIds, symbol.Scene.LayerNames, symbol.Scene.LayerKinds, symbol.Scene.LayerParentIds,
            symbol.Scene.LayerVisible, symbol.Scene.LayerLocked, symbol.Scene.LayerColorArgb, symbol.Scene.LayerOutline,
            Tracks = symbol.Timeline.Tracks.Select(track => new { track.Id, track.TargetId, track.Duration, track.Keyframes }),
            Instances = symbol.Instances.Select(instance => new
                { instance.Id, instance.Name, instance.DrawingObjectId, instance.SceneLayerId, instance.StateKeyframes })
        }),
        Scenes = project.Scenes.Select(scene => new
        {
            scene.Id, scene.Name, scene.FrameCount,
            Layers = scene.Layers.Select(layer => new { layer.Id, layer.Name, layer.Visible, layer.ColorArgb, layer.Outline }),
            Tracks = scene.Timeline.Tracks.Select(track => new { track.Id, track.TargetId, track.Duration, track.Keyframes }),
            Instances = scene.Instances.Select(instance => new
                { instance.Id, instance.Name, instance.DrawingObjectId, instance.SceneLayerId, instance.StateKeyframes })
        })
    });

    private static void AssertFlashObjectPixel(VectorScene scene, int objectIndex, Color expected)
    {
        var editable = new VectorScene();
        editable.RestoreSnapshot(scene.CreateSnapshot());
        Array.Fill(editable.LayerLocked, false);
        var isolated = editable.CreateStaticSelectionScene([objectIndex], 0);
        AssertFlashFramePixel(isolated, expected);
    }

    private static void RunFlashFolderExposureRegression(string root)
    {
        var document = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="40" height="40" frameRate="24">
              <symbols><Include href="Leaf.xml"/></symbols>
              <timelines><DOMTimeline name="Folder Exposures"><layers>
                <DOMLayer name="Folder" layerType="folder"/>
                <DOMLayer name="A" parentLayerIndex="0"><frames>
                  <DOMFrame index="0" duration="2"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame>
                  <DOMFrame index="2" duration="2"><elements/></DOMFrame>
                  <DOMFrame index="4"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame>
                </frames></DOMLayer>
                <DOMLayer name="B" parentLayerIndex="0"><frames>
                  <DOMFrame index="1" duration="2"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="20"/></matrix></DOMSymbolInstance></elements></DOMFrame>
                  <DOMFrame index="3" duration="2"><elements/></DOMFrame>
                </frames></DOMLayer>
              </layers></DOMTimeline></timelines>
            </DOMDocument>
            """;
        var path = Path.Combine(root, "FolderExposures.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
        var project = FlashDocumentImporter.Load(path).Project;
        AssertFlashFolderExposures(project);
        var structure = FlashNativeStructureSignature(project);
        var saved = Path.Combine(root, "FolderExposureSaved", "FolderExposures.v2dProject");
        ProjectVaultStore.Save(project, saved);
        var reopened = ProjectVaultStore.Load(saved);
        AssertTimeline(FlashNativeStructureSignature(reopened) == structure,
            "Flash folder exposure union changed its native structure or identifiers after Save/Open.");
        AssertFlashFolderExposures(reopened);
    }

    private static void AssertFlashFolderExposures(VectorProject project)
    {
        var scene = project.Scenes.Single();
        AssertTimeline(scene.Layers.Count == 1 && scene.FrameCount == 5,
            "Flash folder exposures were split into unrelated scene layers or truncated.");
        var track = scene.Timeline.FindTrackByTargetId(scene.Layers[0].Id)!;
        AssertTimeline(track.Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0, 1, 2, 3, 4 })
            && Enumerable.Range(0, 5).Select(frame => track.EvaluateExposure(frame).HasContent)
                .SequenceEqual(new[] { true, true, true, false, true }),
            "Flash folder scene exposure does not equal the union of its staggered child exposures.");
        var group = project.DrawingObjects.Single(symbol => symbol.Id == scene.Instances.Single().DrawingObjectId);
        var a = group.Timeline.FindTrackByTargetId(group.Scene.LayerIds[1])!;
        var b = group.Timeline.FindTrackByTargetId(group.Scene.LayerIds[2])!;
        AssertTimeline(a.Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0, 2, 4 })
            && b.Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0, 1, 3 }),
            "Flash folder projection expanded child holds or lost the leading blank exposure.");
        var destination = new VectorScene();
        var counts = new[] { 1, 2, 1, 0, 1 };
        for (var frame = 0; frame < 5; frame++)
        {
            SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, frame, project.PlaybackFps);
            AssertTimeline(destination.ObjectCount == counts[frame],
                $"Flash staggered folder child exposure composed the wrong object count at frame {frame}.");
            if (frame == 3) continue;
            var positions = Enumerable.Range(0, destination.ObjectCount)
                .OrderBy(index => destination.GetObjectWorldBounds(index).Left).ToArray();
            for (var index = 0; index < positions.Length; index++)
            {
                var x = frame == 2 ? 20 : index * 20;
                AssertFlashBounds(destination, positions[index], new RectangleF(x, 0, 10, 10));
                AssertFlashObjectPixel(destination, positions[index], Color.Red);
            }
        }
    }

    private static void RunFlashGeometryPixelRegression(string root)
    {
        // Disconnected and reordered edge records form a ring; its hole must remain transparent.
        var ring = """
            <DOMShape><fills><FillStyle index="1"><SolidColor color="#FF0000"/></FillStyle></fills><edges>
              <Edge fillStyle1="1" edges="!400 400|0 400"/>
              <Edge fillStyle1="1" edges="!0 0|400 0"/>
              <Edge fillStyle1="1" edges="!0 400|0 0"/>
              <Edge fillStyle1="1" edges="!400 0|400 400"/>
              <Edge fillStyle0="1" edges="!100 100|300 100|300 300|100 300|100 100"/>
            </edges></DOMShape>
            """;
        using (var raster = RasterizeFlashShape(root, "Ring", ring))
        {
            AssertTimeline(raster.Bitmap.GetPixel(32, 32).A == 0
                && raster.Bitmap.GetPixel(8, 32).R >= 250 && raster.Bitmap.GetPixel(8, 32).A >= 250,
                "Flash disconnected fill edges did not preserve the ring and its transparent hole.");
        }

        // The left edge is negative signed 24.8 hexadecimal. A quadratic arch fills below its curve.
        var quadratic = """
            <DOMShape><fills><FillStyle index="1"><SolidColor color="#FF0000"/></FillStyle></fills><edges>
              <Edge fillStyle1="1" edges="!#FFFF38.00 0[0 -200 #C8.00 0|#C8.00 200|#FFFF38.00 200|#FFFF38.00 0"/>
            </edges></DOMShape>
            """;
        using (var raster = RasterizeFlashShape(root, "HexQuadratic", quadratic))
        {
            AssertTimeline(raster.Bitmap.GetPixel(32, 48).R >= 250 && raster.Bitmap.GetPixel(32, 48).A >= 250
                && raster.Bitmap.GetPixel(32, 4).A == 0,
                "Flash signed hexadecimal coordinates or quadratic edges changed the painted arch.");
        }

        var gradientStroke = """
            <DOMShape><fills><FillStyle index="1"><LinearGradient>
              <matrix><Matrix a="0.006103515625" d="0.006103515625" tx="5"/></matrix>
              <GradientEntry ratio="0" color="#FF0000"/><GradientEntry ratio="1" color="#0000FF"/>
            </LinearGradient></FillStyle></fills>
            <strokes><StrokeStyle index="1"><SolidStroke weight="2" caps="none" joints="round"><fill><SolidColor color="#00FF00"/></fill></SolidStroke></StrokeStyle></strokes>
            <edges><Edge fillStyle1="1" strokeStyle="1" edges="!0 0|200 0|200 200|0 200|0 0"/></edges></DOMShape>
            """;
        using (var raster = RasterizeFlashShape(root, "GradientStroke", gradientStroke))
        {
            var left = raster.Bitmap.GetPixel(20, 32);
            var right = raster.Bitmap.GetPixel(44, 32);
            var stroke = raster.Bitmap.GetPixel(6, 32);
            AssertTimeline(left.R > left.B + 50 && right.B > right.R + 50
                && stroke.G > 240 && stroke.R < 10 && stroke.B < 10,
                "Flash gradient direction or solid stroke colour was lost in the rendered result.");
        }
    }

    private static void RunFlashSolidPaintRegression()
    {
        const string shape = """
            <DOMShape><fills><FillStyle index="1"><SolidColor color="#FF0000" alpha="0.5"/></FillStyle></fills>
            <strokes><StrokeStyle index="1"><SolidStroke weight="10" caps="square" joints="round"><fill><SolidColor color="#0000FF" alpha="0.25"/></fill></SolidStroke></StrokeStyle></strokes>
            <edges><Edge fillStyle1="1" edges="!800 0|1200 0|1200 400|800 400|800 0"/><Edge strokeStyle="1" edges="!0 0|200 200"/></edges></DOMShape>
            """;
        var root = CreateTemporaryDirectory("flash-solid-paint");
        try
        {
            var source = Path.Combine(root, "Solid.fla");
            WriteFlashZipFixture(source, ("DOMDocument.xml", FlashShapeDocument(shape)));
            var project = FlashDocumentImporter.Load(source, editableGeometry: false).Project;
            var manifest = Path.Combine(root, "Saved", "Solid.v2dProject");
            for (var roundTrip = 0; roundTrip < 2; roundTrip++)
            {
                var drawing = project.DrawingObjects.Single(item => item.Scene.ObjectCount > 0);
                AssertTimeline(drawing.Scene.TryGetImportedSvgSource(0, out var svg), "Flash solid paint lost its SVG source.");
                var document = System.Xml.Linq.XDocument.Parse(svg);
                var viewBox = document.Root!.Attribute("viewBox")!.Value.Split(' ')
                    .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                AssertTimeline(viewBox[0] <= -7.071f && viewBox[1] <= -7.071f
                    && !document.Descendants().Any(element => element.Name.LocalName == "linearGradient"),
                    "Flash square caps were clipped or transparent solid paint became a gradient.");
                var extracted = ImportedSvgBreakApart.Extract(svg);
                AssertTimeline(extracted.Approximations == ImportedSvgBreakApproximation.None
                    && extracted.Parts.OfType<ImportedSvgBreakFill>().Any(fill => fill.BezierContours.Length > 0
                        && fill.Paint.Color.A is >= 127 and <= 128)
                    && extracted.Parts.OfType<ImportedSvgBreakStroke>().Any(stroke => stroke.BezierNodes.Length == 2
                        && stroke.Style.Cap == SvgNativeCap.Square && stroke.Paint.Color.A is >= 63 and <= 64),
                    "Flash transparent solid fill/stroke lost editable native curves or opacity.");
                using var raster = ImportedSvgRasterizer.Rasterize(svg, 256, 128).AcquireBitmap();
                var fillPixel = raster.Bitmap.GetPixel(220, 64);
                AssertTimeline(fillPixel.R >= 250 && fillPixel.A is >= 127 and <= 128,
                    "Flash transparent solid fill changed its rendered colour or alpha.");
                ProjectVaultStore.Save(project, manifest);
                project = ProjectVaultStore.Load(manifest);
            }
        }
        finally { DeleteTemporaryDirectory(root); }
        Console.WriteLine("flash_solid_paint=ok,square_caps=ok,fill_alpha=ok,stroke_alpha=ok,editable_curves=ok,save_reopen=ok");
    }

    private static void RunFlashDenseShapeRegression(string root)
    {
        const int perSide = 25_001;
        var edge = new StringBuilder("!0 0");
        for (var i = 1; i <= perSide; i++) edge.Append(FormattableString.Invariant($"|{i} 0"));
        for (var i = 1; i <= perSide; i++) edge.Append(FormattableString.Invariant($"|{perSide} {i}"));
        for (var i = perSide - 1; i >= 0; i--) edge.Append(FormattableString.Invariant($"|{i} {perSide}"));
        for (var i = perSide - 1; i >= 0; i--) edge.Append(FormattableString.Invariant($"|0 {i}"));
        var shape = $"<DOMShape><fills><FillStyle index='1'><SolidColor color='#FF0000'/></FillStyle></fills><edges><Edge fillStyle1='1' edges='{edge}'/></edges></DOMShape>";
        var path = Path.Combine(root, "DenseShape.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", FlashShapeDocument(shape)));
        var imported = FlashDocumentImporter.Load(path, editableGeometry: false);
        Check(imported.Project);
        var manifest = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "DenseSaved", "Dense.v2dProject"));
        Check(ProjectVaultStore.Load(manifest));
        var splitEdges = new StringBuilder();
        const int splitSide = 100_001;
        for (var side = 0; side < 4; side++)
        {
            var stream = new StringBuilder();
            for (var i = 0; i <= splitSide; i++)
            {
                var x = side switch { 0 => i, 1 => splitSide, 2 => splitSide - i, _ => 0 };
                var y = side switch { 0 => 0, 1 => i, 2 => splitSide, _ => splitSide - i };
                stream.Append(FormattableString.Invariant($"{(i == 0 ? '!' : '|')}{x} {y}"));
            }
            splitEdges.Append($"<Edge fillStyle0='2' fillStyle1='1' edges='{stream}'/>");
        }
        var splitShape = $"<DOMShape><fills><FillStyle index='1'><SolidColor color='#FF0000'/></FillStyle><FillStyle index='2'><SolidColor color='#0000FF'/></FillStyle></fills><edges>{splitEdges}</edges></DOMShape>";
        var splitPath = Path.Combine(root, "SplitDense.fla");
        WriteFlashZipFixture(splitPath, ("DOMDocument.xml", FlashShapeDocument(splitShape)));
        var split = FlashDocumentImporter.Load(splitPath, editableGeometry: false);
        CheckSplit(split.Project);
        var splitManifest = ProjectVaultStore.Save(split.Project, Path.Combine(root, "SplitSaved", "Split.v2dProject"));
        CheckSplit(ProjectVaultStore.Load(splitManifest));
        ExpectInvalidData(() => FlashDocumentImporter.Load(WriteInvalid()), "Streaming edge parser accepted malformed trailing data.");

        void CheckSplit(VectorProject project)
        {
            var scene = new VectorScene();
            SceneCompositionBuilder.Build(scene, project.Scenes[0], project.DrawingObjects, 0, split.Fps);
            AssertTimeline(scene.ObjectCount == 2, "Dense independent fill paths were not partitioned into bounded SVG objects.");
            var lines = 0;
            for (var index = 0; index < scene.ObjectCount; index++)
            {
                scene.TryGetImportedSvgSource(index, out var source);
                AssertTimeline(Encoding.UTF8.GetByteCount(source) <= ImportedSvgRasterizer.MaxImportSourceBytes, "Dense SVG chunk exceeded the existing per-object limit.");
                lines += source.Count(character => character == 'L');
            }
            AssertTimeline(lines == splitSide * 8 && scene.ObjectOrder[0] < scene.ObjectOrder[1], "Dense SVG partition lost geometry or drawing order.");
            scene.TryGetImportedSvgSource(0, out var top);
            AssertTimeline(top.Contains("#0000FF", StringComparison.Ordinal) && !top.Contains("#FF0000", StringComparison.Ordinal),
                "Dense SVG chunks reversed the original fill stacking.");
        }

        void Check(VectorProject project)
        {
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, project.Scenes[0], project.DrawingObjects, 0, imported.Fps);
            AssertTimeline(composed.ObjectCount == 1 && composed.TryGetImportedSvgSource(0, out _), "Dense Flash shape was dropped.");
            composed.TryGetImportedSvgSource(0, out var svg);
            AssertTimeline(svg.Count(character => character == 'L') == perSide * 4, "Dense Flash shape lost line segments.");
            using var raster = ImportedSvgRasterizer.Rasterize(svg, 64, 64).AcquireBitmap();
            AssertTimeline(raster.Bitmap.GetPixel(32, 32).R > 250 && raster.Bitmap.GetPixel(32, 32).A > 250,
                "Dense closed contour did not render its fill.");
        }
        string WriteInvalid()
        {
            var invalid = Path.Combine(root, "DenseInvalid.fla");
            WriteFlashZipFixture(invalid, ("DOMDocument.xml", FlashShapeDocument(shape.Replace(edge.ToString(), edge + "?"))));
            return invalid;
        }
    }

    private static ImportedSvgBitmapLease RasterizeFlashShape(string root, string name, string shape)
    {
        var path = Path.Combine(root, name + ".fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", FlashShapeDocument(shape)));
        var imported = FlashDocumentImporter.Load(path, editableGeometry: false);
        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, imported.Project.Scenes[0], imported.Project.DrawingObjects, 0, imported.Fps);
        var svg = DrawingObjectSvgExport.CreateDocument("flash-geometry", name, destination, 0);
        return ImportedSvgRasterizer.Rasterize(svg.ToString(), 64, 64).AcquireBitmap();
    }

    private static string FlashShapeDocument(string elements) => $"""
        <DOMDocument xmlns="{FlashFixtureNamespace}" width="40" height="40" frameRate="24">
          <timelines><DOMTimeline name="Geometry"><layers><DOMLayer name="Artwork"><frames>
            <DOMFrame index="0"><elements>{elements}</elements></DOMFrame>
          </frames></DOMLayer></layers></DOMTimeline></timelines>
        </DOMDocument>
        """;

    private static void RunFlashEmbeddedImageRegression(string root)
    {
        var directory = Path.Combine(root, "VisualBundle");
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "animation.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            version = 1, name = "Embedded Flash image", fps = 24, frameCount = 1, width = 250, height = 250,
            symbols = new[] { new { key = "visual", name = "Visual", frameCount = 1,
                frames = new[] { new { frame = 0, path = "frame.svg", x = 125, y = 125, width = 250, height = 250 } } } },
            scene = new { name = "Visual", instances = new[] { new { name = "Frame", symbol = "visual", playbackMode = "HoldFrame", holdFrame = 0 } } }
        }));
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg })
        {
            using var image = new Bitmap(10, 10);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
            using var encoded = new MemoryStream();
            image.Save(encoded, format);
            var data = "data:image/" + (format.Equals(ImageFormat.Png) ? "png" : "jpeg")
                + ";base64," + Convert.ToBase64String(encoded.ToArray());
            AssertTimeline(FlashEmbeddedImageValidator.IsValidDataImage(data),
                "A valid bounded embedded PNG/JPEG was rejected.");
            File.WriteAllText(Path.Combine(directory, "frame.svg"), FlashEmbeddedImageSvg(data));
            var imported = AnimationBundleImporter.Load(manifest);
            var destination = new VectorScene();
            SceneCompositionBuilder.Build(destination, imported.Project.Scenes[0], imported.Project.DrawingObjects, 0, imported.Fps);
            AssertFlashFramePixel(destination, Color.Red);
            var savePath = Path.Combine(root, "ImageSaved" + format.Guid.ToString("N"), "Image.v2dProject");
            ProjectVaultStore.Save(imported.Project, savePath);
            var reopened = ProjectVaultStore.Load(savePath);
            SceneCompositionBuilder.Build(destination, reopened.Scenes[0], reopened.DrawingObjects, 0, reopened.PlaybackFps);
            AssertFlashFramePixel(destination, Color.Red);
        }

        var oversized = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(oversized, 0);
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(oversized.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16, 4), int.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(20, 4), int.MaxValue);
        var oversizedData = "data:image/png;base64," + Convert.ToBase64String(oversized);
        AssertTimeline(!FlashEmbeddedImageValidator.IsValidDataImage(oversizedData),
            "Embedded image decoding accepted an oversized PNG header.");
        foreach (var rejected in new[] { "https://example.invalid/image.png", "file:///C:/image.png",
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg/>")), oversizedData })
        {
            File.WriteAllText(Path.Combine(directory, "frame.svg"), FlashEmbeddedImageSvg(rejected));
            ExpectInvalidData(() => AnimationBundleImporter.Load(manifest),
                "Animation bundle accepted an external, active, or oversized SVG image resource.");
        }
        File.WriteAllText(Path.Combine(directory, "frame.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"data:image/png;base64,AA==\"/></svg>");
        ExpectInvalidData(() => AnimationBundleImporter.Load(manifest),
            "Animation bundle allowed a data URI on a non-image SVG element.");
    }

    private static string FlashEmbeddedImageSvg(string href) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\" viewBox=\"0 0 10 10\"><image width=\"10\" height=\"10\" href=\"{href}\"/></svg>";

    private static void RunFlashRejectedInputRegression(string root, string document, string symbol)
    {
        var traversal = Path.Combine(root, "Traversal.fla");
        var escaped = "flash-escape-" + Guid.NewGuid().ToString("N") + ".xml";
        WriteFlashZipFixture(traversal, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol),
            ("../" + escaped, "unsafe"));
        ExpectInvalidData(() => FlashDocumentImporter.Load(traversal), "Flash ZIP traversal was accepted.");
        AssertTimeline(!File.Exists(Path.Combine(Path.GetDirectoryName(root)!, escaped)),
            "Flash ZIP traversal wrote content outside the import directory.");

        var duplicate = Path.Combine(root, "Duplicate.fla");
        WriteFlashZipFixture(duplicate, ("DOMDocument.xml", document), ("DOMDocument.xml", document));
        ExpectInvalidData(() => FlashDocumentImporter.Load(duplicate), "Duplicate Flash archive paths were accepted.");
        var dtd = Path.Combine(root, "Dtd.fla");
        WriteFlashZipFixture(dtd, ("DOMDocument.xml", "<!DOCTYPE DOMDocument [<!ENTITY injected 'unsafe'>]>" + document));
        ExpectInvalidData(() => FlashDocumentImporter.Load(dtd), "Flash XML with a DTD was accepted.");
        var missing = Path.Combine(root, "MissingSymbol.fla");
        WriteFlashZipFixture(missing, ("DOMDocument.xml", document));
        ExpectInvalidData(() => FlashDocumentImporter.Load(missing), "Flash missing symbol references were accepted.");
        var cycle = Path.Combine(root, "Cycle.fla");
        var cyclic = $"""
            <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Leaf" symbolType="graphic">
              <timeline><DOMTimeline name="Leaf"><layers><DOMLayer name="Fill"><frames>
                <DOMFrame index="0"><elements><DOMSymbolInstance libraryItemName="Leaf"/></elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timeline>
            </DOMSymbolItem>
            """;
        WriteFlashZipFixture(cycle, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", cyclic));
        ExpectInvalidData(() => FlashDocumentImporter.Load(cycle), "Flash recursive symbol containment was accepted.");
        var old = Path.Combine(root, "Old.fla");
        File.WriteAllBytes(old, FlashOleFixture());
        try
        {
            FlashDocumentImporter.Load(old);
            throw new InvalidOperationException("A legacy binary FLA was treated as modern XFL.");
        }
        catch (InvalidDataException exception)
        {
            AssertTimeline(exception.Message.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("Animate", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("旧", StringComparison.Ordinal),
                "Legacy FLA rejection did not explain its conversion requirement.");
        }
    }

    private static void RunFlashSymbolPlaybackRegression(string root)
    {
        var symbol = $"""
            <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Leaf" symbolType="graphic">
              <timeline><DOMTimeline name="Leaf"><layers><DOMLayer name="Fill"><frames>
                <DOMFrame index="0"><elements>{FlashSquareFixture("#FF0000")}</elements></DOMFrame>
                <DOMFrame index="1"><elements>{FlashSquareFixture("#0000FF")}</elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timeline>
            </DOMSymbolItem>
            """;
        var document = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="40" height="40" frameRate="24">
              <symbols><Include href="Leaf.xml"/></symbols>
              <timelines><DOMTimeline name="Playback"><layers><DOMLayer name="Animated"><frames>
                <DOMFrame index="0" duration="4"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="loop" firstFrame="1"/></elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timelines>
            </DOMDocument>
            """;
        var path = Path.Combine(root, "Playback.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
        var imported = FlashDocumentImporter.Load(path, editableGeometry: false);
        var destination = new VectorScene();
        for (var frame = 0; frame < 4; frame++)
        {
            SceneCompositionBuilder.Build(destination, imported.Project.Scenes[0], imported.Project.DrawingObjects, frame, imported.Fps);
            AssertFlashFramePixel(destination, frame % 2 == 0 ? Color.Blue : Color.Red);
        }

        // A simple classic tween must visibly move between endpoints, rather than holding frame zero.
        var tweenDocument = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="40" height="40" frameRate="24">
              <symbols><Include href="Leaf.xml"/></symbols>
              <timelines><DOMTimeline name="Tween"><layers><DOMLayer name="Animated"><frames>
                <DOMFrame index="0" duration="3" tweenType="motion"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"/></elements></DOMFrame>
                <DOMFrame index="3"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="30"/></matrix></DOMSymbolInstance></elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timelines>
            </DOMDocument>
            """;
        var tweenPath = Path.Combine(root, "Tween.fla");
        WriteFlashZipFixture(tweenPath, ("DOMDocument.xml", tweenDocument), ("LIBRARY/Leaf.xml", FlashSymbolFixture()));
        var tween = FlashDocumentImporter.Load(tweenPath);
        AssertTimeline(tween.Diagnostics.Any(item => item.Code == "tween-baked"),
            "Flash import did not disclose its classic tween conversion.");
        for (var frame = 0; frame < 4; frame++)
        {
            SceneCompositionBuilder.Build(destination, tween.Project.Scenes[0], tween.Project.DrawingObjects, frame, tween.Fps);
            AssertTimeline(destination.ObjectCount == 1, "Flash classic tween lost its visible symbol.");
            AssertFlashBounds(destination, 0, new RectangleF(frame * 10, 0, 10, 10));
        }
    }

    private static void RunFlashNestedPlaybackRegression(string root)
    {
        var document = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="100" height="40" frameRate="24">
              <symbols><Include href="Actor.xml"/><Include href="Leaf.xml"/></symbols>
              <timelines><DOMTimeline name="Nested"><layers><DOMLayer name="Actor"><frames>
                <DOMFrame index="0" duration="5"><elements><DOMSymbolInstance libraryItemName="Actor" symbolType="graphic" loop="loop" firstFrame="1"><matrix><Matrix tx="20"/></matrix></DOMSymbolInstance></elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timelines>
            </DOMDocument>
            """;
        var actor = $"""
            <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Actor" symbolType="graphic">
              <timeline><DOMTimeline name="Actor"><layers><DOMLayer name="Child"><frames>
                <DOMFrame index="0" duration="3"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="loop" firstFrame="1"><matrix><Matrix tx="5"/></matrix></DOMSymbolInstance></elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timeline>
            </DOMSymbolItem>
            """;
        var leaf = $"""
            <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Leaf" symbolType="graphic">
              <timeline><DOMTimeline name="Leaf"><layers><DOMLayer name="Fill"><frames>
                <DOMFrame index="0"><elements>{FlashSquareFixture("#FF0000")}</elements></DOMFrame>
                <DOMFrame index="1"><elements>{FlashSquareFixture("#0000FF")}</elements></DOMFrame>
              </frames></DOMLayer></layers></DOMTimeline></timeline>
            </DOMSymbolItem>
            """;
        var path = Path.Combine(root, "NestedPlayback.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Actor.xml", actor), ("LIBRARY/Leaf.xml", leaf));
        var project = FlashDocumentImporter.Load(path).Project;
        AssertFlashNestedPlayback(project);
        var structure = FlashNativeStructureSignature(project);
        var saved = Path.Combine(root, "NestedPlaybackSaved", "NestedPlayback.v2dProject");
        ProjectVaultStore.Save(project, saved);
        var reopened = ProjectVaultStore.Load(saved);
        AssertTimeline(FlashNativeStructureSignature(reopened) == structure,
            "Flash nested animation references or baked instance states changed after Save/Open.");
        AssertFlashNestedPlayback(reopened);
    }

    private static void AssertFlashNestedPlayback(VectorProject project)
    {
        var scene = project.Scenes.Single();
        var actor = project.DrawingObjects.Single(symbol => symbol.Name == "Actor");
        var leaf = project.DrawingObjects.Single(symbol => symbol.Name == "Leaf");
        AssertTimeline(scene.Instances.Single().DrawingObjectId == actor.Id
            && actor.Instances.Single().DrawingObjectId == leaf.Id
            && scene.Timeline.Tracks.Single().Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0 })
            && actor.Timeline.Tracks.Single().Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0 })
            && leaf.Timeline.Tracks.Single().Keyframes.Select(key => key.Frame).SequenceEqual(new[] { 0, 1 }),
            "Flash forward symbol reference or nested held exposures were flattened/cloned into extra source keys.");
        var destination = new VectorScene();
        var expected = new[] { Color.Red, Color.Blue, Color.Blue, Color.Red, Color.Blue };
        for (var frame = 0; frame < expected.Length; frame++)
        {
            SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, frame, project.PlaybackFps);
            AssertTimeline(destination.ObjectCount == 1, "Flash nested animation lost or duplicated its visible leaf.");
            AssertFlashBounds(destination, 0, new RectangleF(25, 0, 10, 10));
            AssertFlashFramePixel(destination, expected[frame]);
        }
    }

    private static void RunFlashDiagnosticRegression(string root, string document, string symbol)
    {
        var reported = document.Replace("<DOMFrame index=\"0\" duration=\"2\">",
                "<DOMFrame index=\"0\" duration=\"2\" name=\"entry\" soundName=\"voice\">", StringComparison.Ordinal)
            .Replace("<elements><DOMSymbolInstance", "<Actionscript><script><![CDATA[stop();]]></script></Actionscript><elements><DOMSymbolInstance", StringComparison.Ordinal);
        var path = Path.Combine(root, "Diagnostics.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", reported), ("LIBRARY/Leaf.xml", symbol));
        var imported = FlashDocumentImporter.Load(path);
        AssertTimeline(new[] { "script-not-converted", "label-not-converted", "sound-not-converted" }
                .All(code => imported.Diagnostics.Any(item => item.Code == code
                    && !string.IsNullOrWhiteSpace(item.Location) && !string.IsNullOrWhiteSpace(item.Message))),
            "Flash import silently dropped scripts, frame labels, or sound without an actionable report.");
        AssertFlashAnimationProject(imported.Project);

        var unsupported = Path.Combine(root, "UnsupportedMask.fla");
        WriteFlashZipFixture(unsupported,
            ("DOMDocument.xml", document.Replace("name=\"Animated\"", "name=\"Animated\" layerType=\"mask\"", StringComparison.Ordinal)),
            ("LIBRARY/Leaf.xml", symbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(unsupported),
            "Flash import silently approximated an unsupported mask layer.");
    }

    private static void RunFlashLegacyBridgeRegression(string root, string document, string symbol)
    {
        var source = Path.Combine(root, "旧版 'quoted'.fla");
        var sourceBytes = FlashOleFixture();
        File.WriteAllBytes(source, sourceBytes);
        var output = Path.Combine(root, "conversion '中文'");
        var plan = FlashLegacyConversionBridge.Prepare(source, output);
        AssertTimeline(File.ReadAllBytes(source).SequenceEqual(sourceBytes)
            && File.ReadAllBytes(plan.WorkingSourcePath).SequenceEqual(sourceBytes)
            && !string.Equals(source, plan.WorkingSourcePath, StringComparison.OrdinalIgnoreCase),
            "Legacy Flash conversion modified its original file or failed to use an independent copy.");
        var script = File.ReadAllText(plan.ScriptPath);
        AssertTimeline(script.Contains("FLfile.platformPathToURI", StringComparison.Ordinal)
            && script.Contains(JsonSerializer.Serialize(plan.WorkingSourcePath), StringComparison.Ordinal)
            && script.Contains(JsonSerializer.Serialize(plan.ExpectedFlaPath), StringComparison.Ordinal),
            "Legacy conversion JSFL did not safely encode Unicode, apostrophes, and Windows paths.");
        AssertTimeline(!FlashLegacyConversionBridge.TryGetCompletedPath(plan, out _, out var pendingError)
            && !string.IsNullOrWhiteSpace(pendingError),
            "Legacy conversion adopted an unfinished output without explaining its pending state.");
        try
        {
            FlashLegacyConversionBridge.Prepare(source, output);
            throw new InvalidOperationException("Legacy conversion overwrote a nonempty output directory.");
        }
        catch (IOException) { }
        File.WriteAllText(plan.CompletionPath, "SUCCEEDED");
        File.WriteAllBytes(plan.ExpectedFlaPath, sourceBytes);
        AssertTimeline(!FlashLegacyConversionBridge.TryGetCompletedPath(plan, out _, out _),
            "Legacy conversion accepted an OLE file merely because its completion marker claimed success.");
        WriteFlashZipFixture(plan.ExpectedFlaPath, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
        File.WriteAllText(plan.CompletionPath, "FAILED\nAdobe save failed");
        AssertTimeline(!FlashLegacyConversionBridge.TryGetCompletedPath(plan, out _, out _),
            "Legacy conversion accepted output from a failed conversion.");
        File.WriteAllText(plan.CompletionPath, "SUCCEEDED\nSaved as current Animate document");
        AssertTimeline(FlashLegacyConversionBridge.TryGetCompletedPath(plan, out var completed, out var error)
            && completed == plan.ExpectedFlaPath && string.IsNullOrEmpty(error),
            "Legacy conversion did not accept its validated completed modern FLA.");
        AssertFlashAnimationProject(FlashDocumentImporter.Load(completed).Project);
        AssertTimeline(File.ReadAllBytes(source).SequenceEqual(sourceBytes),
            "Legacy conversion completion altered the original binary FLA.");

        var invalidSource = Path.Combine(root, "NotLegacy.fla");
        File.WriteAllText(invalidSource, "not an OLE document");
        var rejectedDirectory = Path.Combine(root, "rejected-conversion");
        ExpectInvalidData(() => FlashLegacyConversionBridge.Prepare(invalidSource, rejectedDirectory),
            "Legacy conversion accepted an unsigned non-Flash source.");
        AssertTimeline(!Directory.Exists(rejectedDirectory),
            "Rejected legacy source left conversion output behind.");
    }

    private static void RunFlashVisualBridgeRegression(string root, string document, string symbol)
    {
        var modernSource = Path.Combine(root, "视觉 'modern'.fla");
        WriteFlashZipFixture(modernSource, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
        var legacySource = Path.Combine(root, "视觉 'legacy'.fla");
        File.WriteAllBytes(legacySource, FlashOleFixture());
        foreach (var source in new[] { modernSource, legacySource })
        {
            var before = File.ReadAllBytes(source);
            var output = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + " output");
            var plan = FlashVisualConversionBridge.Prepare(source, output);
            AssertTimeline(File.ReadAllBytes(source).SequenceEqual(before)
                && File.ReadAllBytes(plan.WorkingSourcePath).SequenceEqual(before)
                && plan.WorkingSourcePath != source,
                "Flash visual bake did not isolate its source from the original document.");
            var script = File.ReadAllText(plan.ScriptPath);
            AssertTimeline(script.Contains(JsonSerializer.Serialize(plan.WorkingSourcePath), StringComparison.Ordinal)
                && script.Contains(JsonSerializer.Serialize(plan.ExpectedManifestPath), StringComparison.Ordinal)
                && script.Contains("FLfile.platformPathToURI", StringComparison.Ordinal),
                "Flash visual JSFL did not safely encode its source and manifest paths.");
            AssertTimeline(!FlashVisualConversionBridge.TryGetCompletedPath(plan, out _, out var pending)
                && !string.IsNullOrWhiteSpace(pending),
                "Flash visual bake adopted a pending or absent manifest.");
            try
            {
                FlashVisualConversionBridge.Prepare(source, output);
                throw new InvalidOperationException("Flash visual bake overwrote an occupied output directory.");
            }
            catch (IOException) { }

            File.WriteAllText(plan.CompletionPath, "SUCCEEDED\nSimulated fixture; Adobe was not run");
            File.WriteAllText(plan.ExpectedManifestPath, FlashVisualManifestFixture());
            AssertTimeline(!FlashVisualConversionBridge.TryGetCompletedPath(plan, out _, out _),
                "Flash visual bake accepted a manifest before its frame existed.");
            Directory.CreateDirectory(Path.Combine(output, "frames"));
            var frame = Path.Combine(output, "frames", "frame-00000.svg");
            File.WriteAllText(frame, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>");
            AssertTimeline(FlashVisualConversionBridge.TryGetCompletedPath(plan, out var completed, out var error)
                && completed == plan.ExpectedManifestPath && string.IsNullOrEmpty(error),
                "Flash visual bake did not validate a complete full-stage animation-bundle fixture.");
            var imported = AnimationBundleImporter.Load(completed);
            var destination = new VectorScene();
            SceneCompositionBuilder.Build(destination, imported.Project.Scenes[0], imported.Project.DrawingObjects, 0, imported.Fps);
            AssertFlashFramePixel(destination, Color.Red);
            File.WriteAllText(frame, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><text>font dependency</text></svg>");
            AssertTimeline(!FlashVisualConversionBridge.TryGetCompletedPath(plan, out _, out _),
                "Flash visual bake accepted text that still depends on installed fonts.");
            File.WriteAllText(frame, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 5 5\"><rect width=\"5\" height=\"5\"/></svg>");
            AssertTimeline(!FlashVisualConversionBridge.TryGetCompletedPath(plan, out _, out _),
                "Flash visual bake accepted cropped content as a full-stage frame.");
            AssertTimeline(File.ReadAllBytes(source).SequenceEqual(before),
                "Flash visual result checking modified the source document.");
        }

        var nonFla = Path.Combine(root, "NotFlash.txt");
        File.WriteAllBytes(nonFla, FlashOleFixture());
        try
        {
            FlashVisualConversionBridge.Prepare(nonFla, Path.Combine(root, "invalid-visual-extension"));
            throw new InvalidOperationException("Flash visual bake accepted a non-FLA source extension.");
        }
        catch (ArgumentException) { }
        var unsigned = Path.Combine(root, "Unsigned.fla");
        File.WriteAllBytes(unsigned, new byte[512]);
        ExpectInvalidData(() => FlashVisualConversionBridge.Prepare(unsigned, Path.Combine(root, "unsigned-visual")),
            "Flash visual bake accepted an unsigned binary source.");
    }

    private static string FlashVisualManifestFixture() => """
        {"version":1,"name":"Simulated Adobe visual bake","fps":24,"frameCount":1,"width":250,"height":250,
         "symbols":[{"key":"visual-bake","name":"Visual Frames","frameCount":1,
          "frames":[{"frame":0,"path":"frames/frame-00000.svg","x":0,"y":0,"width":250,"height":250}],"instances":[]}],
         "scene":{"name":"Visual","instances":[{"name":"Visual Playback","symbol":"visual-bake","playbackMode":"Loop"}]}}
        """;

    private static string FlashAnimationFixture() => $"""
        <DOMDocument xmlns="{FlashFixtureNamespace}" width="160" height="120" frameRate="23.976">
          <symbols><Include href="Leaf.xml"/></symbols>
          <timelines><DOMTimeline name="Animation"><layers>
            <DOMLayer name="Animated"><frames>
              <DOMFrame index="0" duration="2"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix tx="20" ty="30"/></matrix></DOMSymbolInstance></elements></DOMFrame>
              <DOMFrame index="2" duration="1"><elements/></DOMFrame>
              <DOMFrame index="3" duration="1"><elements><DOMSymbolInstance libraryItemName="Leaf" symbolType="graphic" loop="single frame"><matrix><Matrix a="0" b="2" c="-3" d="0" tx="60" ty="80"/></matrix></DOMSymbolInstance></elements></DOMFrame>
            </frames></DOMLayer>
            <DOMLayer name="Hidden" visible="false"><frames><DOMFrame index="0" duration="4"><elements>{FlashSquareFixture("#0000FF")}</elements></DOMFrame></frames></DOMLayer>
          </layers></DOMTimeline></timelines>
        </DOMDocument>
        """;

    private static string FlashSymbolFixture() => $"""
        <DOMSymbolItem xmlns="{FlashFixtureNamespace}" name="Leaf" symbolType="graphic">
          <timeline><DOMTimeline name="Leaf"><layers><DOMLayer name="Fill"><frames>
            <DOMFrame index="0"><elements>{FlashSquareFixture("#FF0000")}</elements></DOMFrame>
          </frames></DOMLayer></layers></DOMTimeline></timeline>
        </DOMSymbolItem>
        """;

    private static string FlashSquareFixture(string color) => $"""
        <DOMShape><fills><FillStyle index="1"><SolidColor color="{color}"/></FillStyle></fills>
          <edges><Edge fillStyle1="1" edges="!0 0|200 0|200 200|0 200|0 0"/></edges></DOMShape>
        """;

    private static byte[] FlashOleFixture()
    {
        var bytes = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static void WriteFlashZipFixture(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name, entry.Name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }
    }
}
