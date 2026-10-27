using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunImageAssetPersistenceRegression()
    {
        RunImageImportDialogRegression();
        RunImageDisplayDetectionRegression();
        RunImageBatchPreviewRegression();
        RunImageCropRegression();
        var temporaryRoot = CreateTemporaryDirectory("image-asset-regression");
        var importDirectory = Path.Combine(temporaryRoot, "imports");
        var sourcePath = Path.Combine(importDirectory, "swatch.png");
        var manifestPath = Path.Combine(temporaryRoot, "Images.v2dProject");
        Directory.CreateDirectory(importDirectory);
        WriteFixturePng(sourcePath, 64, 48);
        try
        {
            var project = VectorProject.CreateEmpty();
            var settings = BitmapImageImportSettings.Default with
            {
                FilterMode = ImageFilterMode.Point,
                PixelsPerUnit = 200,
                MaxSize = 32,
                Compression = ImageCompression.LosslessPng,
                PivotX = 0.25f,
                PivotY = 0.75f
            };
            var sha256 = BitmapImageRasterizer.ComputeSha256(sourcePath);
            AssertTimeline(
                project.TryAddImageAssetFromSource(
                    "Swatch",
                    sourcePath,
                    sha256,
                    64,
                    48,
                    96f,
                    settings,
                    out var asset)
                && asset is not null
                && project.ImageAssets.Count == 1
                && project.ImageAssets[0].PixelWidth == 64
                && project.ImageAssets[0].PixelHeight == 48
                && project.ImageAssets[0].ImportSettings.FilterMode == ImageFilterMode.Point
                && project.ImageAssets[0].ImportSettings.PixelsPerUnit == 200
                && project.ImageAssets[0].ImportSettings.PivotX == 0.25f
                // The Vault store enforces a canonical managed path derived from the asset
                // id, so creation must never invent one from the content hash.
                && project.ImageAssets[0].ProjectRelativePath
                    == VectorProject.ImageAssetManagedRelativePath(asset.Id, ".png"),
                "An image asset did not retain its validated metadata and import settings.");

            // MaxSize downscales proportionally and never upscales. The power-of-two rule is
            // a separate opt-in: with it off a 64x48 source under MaxSize 32 keeps its aspect
            // ratio, and with it on each axis snaps down to a power of two.
            var stored = settings.ResolveStoredPixelSize(new SizeF(64, 48));
            AssertTimeline(
                (int)stored.Width == 32
                && (int)stored.Height == 24
                && settings.ResolveStoredPixelSize(new SizeF(8, 8)).Width == 8
                && (int)(settings with { NonPowerOfTwoScale = true })
                    .ResolveStoredPixelSize(new SizeF(64, 48)).Width == 32
                && (int)(settings with { NonPowerOfTwoScale = true })
                    .ResolveStoredPixelSize(new SizeF(64, 48)).Height == 16,
                "Image import settings did not resolve the stored pixel size without upscaling.");

            AssertTimeline(
                !project.TryAddImageAsset(
                    "Traversal",
                    sourcePath,
                    "../escaped.png",
                    sha256,
                    64,
                    48,
                    96f,
                    settings,
                    out _)
                && !project.TryAddImageAsset(
                    "Unsupported",
                    sourcePath,
                    ".Vault/Images/asset.exe",
                    sha256,
                    64,
                    48,
                    96f,
                    settings,
                    out _)
                && !project.TryAddImageAsset(
                    "BadHash",
                    sourcePath,
                    ".Vault/Images/asset.png",
                    "not-a-sha256",
                    64,
                    48,
                    96f,
                    settings,
                    out _),
                "Image asset library accepted an unsafe relative path, an unsupported format, or a malformed hash.");

            // The decoded raster must honour the filter mode and the stored size: MaxSize 32
            // scales the 64x48 fixture by 0.5, keeping the aspect ratio.
            var raster = BitmapImageRasterizer.Decode(sourcePath, settings);
            AssertTimeline(
                raster.PixelWidth == 32
                && raster.PixelHeight == 24
                && raster.Pixels.LongLength == raster.Stride * (long)raster.PixelHeight,
                "The image rasterizer did not decode to the resolved stored size.");

            var drawing = project.DrawingObjects.FirstOrDefault() ?? project.AddDrawingObject();
            var imageScene = drawing.Scene;
            var placed = imageScene.AddBitmapObject(0, PointF.Empty, new BitmapObjectData
            { ImageAssetId = asset!.Id, PlacedSize = new SizeF(400, 300) });
            var beforeErase = imageScene.CreateSnapshot();
            AssertTimeline(imageScene.EraseWithBrushStroke(0, [PointF.Empty], 100,
                BrushShape.CreateSoftRound(), eraseLines: false, eraseFills: true), "Image erasing did not change the image.");
            AssertTimeline(imageScene.TryGetBitmapObjectData(placed, out var clipped)
                && clipped.VisibleContours is { Length: > 0 }
                && imageScene.ShapeKind[placed] == ShapeKind.Bitmap
                && !imageScene.FillContainsPoint(placed, PointF.Empty), "Erasing replaced the image or failed to remove the center.");
            var pixels = BitmapImageRasterizer.ApplyObjectClip(raster, clipped);
            var centerOffset = (pixels.PixelHeight / 2) * pixels.Stride + (pixels.PixelWidth / 2) * 4;
            AssertTimeline(pixels.Pixels[centerOffset + 3] == 0
                && pixels.Pixels.Take(4).SequenceEqual(raster.Pixels.Take(4))
                && !ReferenceEquals(raster, pixels), "Image clipping lost colors or modified shared source pixels.");
            var afterErase = imageScene.CreateSnapshot();
            imageScene.RestoreSnapshot(beforeErase);
            AssertTimeline(imageScene.TryGetBitmapObjectData(placed, out var uncut) && uncut.VisibleContours is null,
                "Undo did not restore the uncut image.");
            imageScene.RestoreSnapshot(afterErase);
            File.Delete(sourcePath);
            ProjectVaultStore.Save(project, manifestPath);
            // The managed file is named after the asset id, not the content hash.
            var managedPath = Path.Combine(temporaryRoot, ".Vault", "Images", $"{asset!.Id}.png");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                ?? throw new InvalidOperationException("Image regression could not parse the project manifest.");
            AssertTimeline(
                manifest["formatVersion"]?.GetValue<int>() == ProjectVaultStore.CurrentManifestFormatVersion
                && manifest["imageAssets"] is JsonArray { Count: 1 }
                && File.Exists(managedPath),
                "Project Vault did not save the image asset in the current manifest format with a managed copy.");

            // The managed copy is authoritative: deleting the original must not break load.
            File.Delete(sourcePath);
            var vaultRestored = ProjectVaultStore.Load(manifestPath);
            var restored = vaultRestored.ImageAssets.Single();
            AssertTimeline(vaultRestored.DrawingObjects.Single(item => item.Id == drawing.Id).Scene
                .TryGetBitmapObjectData(placed, out var loadedClip) && loadedClip.VisibleContours is { Length: > 0 },
                "Saving and reopening lost the image erasure.");
            AssertTimeline(
                restored.Id == asset.Id
                && restored.ImportSettings == settings
                && restored.PixelWidth == 64
                && restored.PixelHeight == 48
                && restored.SourcePath == Path.GetFullPath(managedPath),
                "Reloading a project did not restore the image asset from its managed copy.");

            // A manifest that claims image assets under an older format version must be
            // rejected rather than silently read with the field missing.
            var legacyManifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            legacyManifest["formatVersion"] = 4;
            var legacyPath = Path.Combine(temporaryRoot, "LegacyImages.v2dProject");
            File.WriteAllText(
                legacyPath,
                legacyManifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            AssertTimeline(
                ThrowsInvalidData(() => ProjectVaultStore.Load(legacyPath)),
                "A pre-image project format was allowed to carry image assets.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static void RunBitmapObjectRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var data = new BitmapObjectData
        {
            ImageAssetId = "image-0001",
            PlacedSize = new SizeF(120, 80)
        };
        AssertTimeline(data.IsValid, "A well-formed bitmap payload was reported as invalid.");

        var index = scene.AddBitmapObject(0, new PointF(40, 30), data, 0.5f);
        AssertTimeline(
            index >= 0
            && scene.ShapeKind[index] == ShapeKind.Bitmap
            && scene.TryGetBitmapObjectData(index, out var stored)
            && stored.ImageAssetId == "image-0001"
            && stored.PlacedSize == new SizeF(120, 80)
            && scene.Width[index] == 120
            && scene.Height[index] == 80,
            "A bitmap object did not store its image reference and placed size.");

        // A bitmap carries no stroke or fill paint of its own; leaving either set would make
        // the renderers draw a spurious outline around the image.
        AssertTimeline(
            scene.Stroke[index] == 0
            && Color.FromArgb(scene.StrokeArgb[index]).A == 0,
            "A bitmap object kept a stroke or stroke colour.");

        AssertTimeline(
            scene.FindBitmapObjectsUsingImageAsset("image-0001").Length == 1
            && scene.FindBitmapObjectsUsingImageAsset("missing").Length == 0,
            "Bitmap objects were not indexed by image asset id.");

        // The tint/opacity channel is the object's Argb, so HasFill must be true regardless
        // of that value; otherwise a tinted bitmap would be treated as an empty shape and a
        // scene mask would drop the whole layer.
        scene.Argb[index] = 0;
        AssertTimeline(
            scene.HasFill(index) && SceneRenderOrder.HasFill(ShapeKind.Bitmap),
            "A transparent-tinted bitmap was reported as having no fill.");

        AssertTimeline(
            SceneRenderOrder.HasStroke(ShapeKind.Bitmap, 4f) == false
            && SceneRenderOrder.RequiresObjectRenderer(scene),
            "A bitmap did not force the per-object renderer or wrongly reported a stroke.");

        // Snapshot round-trip: the sparse payload must survive with the object and be dropped
        // when the object is removed.
        var snapshot = scene.CreateSnapshot();
        AssertTimeline(
            snapshot.BitmapObjects.TryGetValue(index, out var snapshotPayload)
            && snapshotPayload.ImageAssetId == "image-0001",
            "The scene snapshot did not carry the bitmap payload.");

        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        AssertTimeline(
            restored.TryGetBitmapObjectData(index, out var restoredPayload)
            && restoredPayload.PlacedSize == new SizeF(120, 80),
            "Restoring a scene snapshot did not restore the bitmap payload.");

        // Updating the placed size must resynchronise the object's width and height.
        AssertTimeline(
            restored.TryUpdateBitmapObject(index, restoredPayload.WithPlacedSize(new SizeF(200, 150)))
            && restored.Width[index] == 200
            && restored.Height[index] == 150,
            "Updating a bitmap object did not resynchronise its placed size.");

        // Deletion is modelled by a predicate that reports whether the asset still exists,
        // so returning false for our id means it was removed from the library.
        var removedCount = restored.RemoveBitmapObjectsWithoutImageAsset(id => id != "image-0001");
        AssertTimeline(
            removedCount == 1
            && restored.ObjectCount == 0
            && restored.FindBitmapObjectsUsingImageAsset("image-0001").Length == 0,
            "Removing a bitmap by missing image asset did not delete the object.");

        // A still-existing asset must keep its placements.
        var keeper = new VectorScene();
        keeper.CreateEmpty();
        keeper.AddBitmapObject(0, PointF.Empty, data);
        AssertTimeline(
            keeper.RemoveBitmapObjectsWithoutImageAsset(id => id == "image-0001") == 0
            && keeper.ObjectCount == 1,
            "A bitmap whose image asset still exists was wrongly removed.");

        AssertTimeline(
            ThrowsMissingBitmapPayload(() =>
            {
                var empty = new VectorScene();
                empty.CreateEmpty();
                empty.AppendPackedObject(
                    0,
                    PointF.Empty,
                    new SizeF(10, 10),
                    0,
                    0,
                    Color.Red.ToArgb(),
                    Color.Transparent.ToArgb(),
                    1,
                    ShapeKind.Bitmap,
                    PointF.Empty);
            }),
            "The packed batch path accepted a bitmap without its image payload.");
    }

    private static void RunImageImportDialogRegression()
    {
        var routing = HotReloadModuleResolver.Resolve([typeof(ImageImportSettingsDialog), typeof(ImageCropDialog),
            typeof(ImageBatchFile), typeof(ImageBatchImportSelection), typeof(ImageDisplayDetector)]);
        AssertTimeline(routing.Modules == HotReloadModule.Workspace && routing.RequiresProcessRestart,
            "Image import types lost their workspace hot-reload route or structural restart policy.");
        var legacy = BitmapImageImportSettings.Default with
        {
            FilterMode = ImageFilterMode.Trilinear, PixelsPerUnit = 321, MaxSize = 128,
            NonPowerOfTwoScale = true, Compression = ImageCompression.Jpeg, CompressionQuality = 73,
            ReadWriteEnabled = false, Mipmaps = false, GenerateMipmapsForAlpha = true,
            PivotX = 0.123456f, PivotY = 0.654321f
        };
        using var dialog = new ImageImportSettingsDialog("Image Import Settings", "Legacy", 400, 200, 96, legacy);
        AssertTimeline(dialog.Settings == legacy, "Opening image settings changed precise or hidden legacy metadata.");
        var toggle = (Button)RequireField(typeof(ImageImportSettingsDialog), "_advancedToggle").GetValue(dialog)!;
        toggle.PerformClick();
        toggle.PerformClick();
        AssertTimeline(dialog.Settings == legacy, "Collapsing image settings changed legacy metadata.");
        var resolution = (ModernNumericUpDown)RequireField(typeof(ImageImportSettingsDialog), "_maxSize").GetValue(dialog)!;
        resolution.Value = 64;
        AssertTimeline(dialog.Settings == legacy with { MaxSize = 64 }, "Resolution editing replaced unrelated image settings.");
        var scale = (ModernNumericUpDown)RequireField(typeof(ImageImportSettingsDialog), "_placementScale").GetValue(dialog)!;
        scale.Value = 200;
        AssertTimeline(dialog.Settings.PixelsPerUnit == 642, "Placement percentage did not map to the existing size model.");

        using var form = new MainForm();
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var original = BitmapImageImportSettings.Default;
        var resolutionRoot = CreateTemporaryDirectory("image-import-dialog-regression");
        var resolutionPath = Path.Combine(resolutionRoot, "resolution.png");
        WriteFixturePng(resolutionPath, 400, 200);
        AssertTimeline(project.TryAddImageAssetFromSource("Resolution", resolutionPath,
            BitmapImageRasterizer.ComputeSha256(resolutionPath), 400, 200, 96, original, out var asset),
            "Could not prepare image resolution fixture.");
        var index = scene.AddBitmapObject(scene.ActiveLayer, PointF.Empty,
            new BitmapObjectData { ImageAssetId = asset!.Id, PlacedSize = new SizeF(600, 300) });
        AssertTimeline(index >= 0, "Could not prepare placed image fixture.");
        var apply = RequireMethod(typeof(MainForm), "ApplyImageImportSettingsToPlacements",
            [typeof(ImageAssetDefinition), typeof(BitmapImageImportSettings)]);
        project.TryApplyImageImportSettings(asset.Id, original with { MaxSize = 32 }, 400, 200, asset.Sha256);
        AssertTimeline(apply.Invoke(form, [asset, original]) is true
            && scene.TryGetBitmapObjectData(index, out var unchanged) && unchanged.PlacedSize == new SizeF(600, 300),
            "Reducing image resolution changed an existing object's size.");
        var previous = asset.ImportSettings;
        project.TryApplyImageImportSettings(asset.Id, previous with { PixelsPerUnit = 200 }, 400, 200, asset.Sha256);
        AssertTimeline(apply.Invoke(form, [asset, previous]) is true
            && scene.TryGetBitmapObjectData(index, out var scaled) && scaled.PlacedSize == new SizeF(1200, 600),
            "Placement scale replaced rather than proportionally scaled a custom object size.");
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("resolution", 32, 16));
        var resolved = (SizeF)RequireMethod(typeof(MainForm), "ScalePlacedSizeForDecode",
            [typeof(SizeF), typeof(ImageAssetDefinition), typeof(BitmapImageRaster)],
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, [new SizeF(600, 300), asset, raster])!;
        AssertTimeline(resolved == new SizeF(600, 300), "New placement size still depends on decoded resolution.");
        previous = asset.ImportSettings;
        project.TryApplyImageImportSettings(asset.Id, previous with { CropWidth = 0.5f }, 400, 200, asset.Sha256);
        AssertTimeline(apply.Invoke(form, [asset, previous]) is true
            && scene.TryGetBitmapObjectData(index, out var cropped) && cropped.PlacedSize == new SizeF(600, 600),
            "Cropping stretched an existing image instead of updating its aspect ratio.");
        previous = asset.ImportSettings;
        project.TryApplyImageImportSettings(asset.Id, previous with { CropWidth = 1 }, 400, 200, asset.Sha256);
        AssertTimeline(apply.Invoke(form, [asset, previous]) is true
            && scene.TryGetBitmapObjectData(index, out var restoredSize) && restoredSize.PlacedSize == new SizeF(1200, 600),
            "Restoring the full image did not restore the existing placement size.");
        DeleteTemporaryDirectory(resolutionRoot);
        Console.WriteLine("image_import_dialog_regression=passed");
    }

    private static void RunImageDisplayDetectionRegression()
    {
        using var sprite = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(sprite))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.OrangeRed);
            graphics.FillRectangle(brush, 4, 4, 24, 24);
        }
        AssertTimeline(ImageDisplayDetector.Detect(sprite) == ImageFilterMode.Point,
            "A hard-edged sprite with binary alpha was not detected as pixel art.");
        using var lowContrast = new Bitmap(16, 16);
        Color[] mutedPalette = [Color.FromArgb(81, 85, 81), Color.FromArgb(109, 114, 107), Color.FromArgb(122, 128, 119)];
        string[] sourceRows =
        [
            "..0..2..0.00.0..",
            "..0.2...0.0..00.",
            "..000...001.2.0.",
            "...10.2..0122..0",
            "..01020..00....0",
            "..1.0....110...1",
            "..1..0...101..11",
            "..1..01.01.1..1.",
            "..20.2120.11.01.",
            "..21..220.1..1..",
            "2.10.0010.1.01..",
            ".22100.0..0.10.2",
            "...10...0.1.102.",
            "..211.2.1.1.1...",
            ".2..12..2.1.0...",
            "..000..02.0101.."
        ];
        for (var row = 0; row < sourceRows.Length; row++)
            for (var column = 0; column < sourceRows[row].Length; column++)
                if (sourceRows[row][column] != '.')
                    lowContrast.SetPixel(column, row, mutedPalette[sourceRows[row][column] - '0']);
        AssertTimeline(ImageDisplayDetector.Detect(lowContrast) == ImageFilterMode.Point,
            "A low-contrast 16x16 sprite was treated as a gradient solely because its palette colors are close.");
        using var enlarged = new Bitmap(128, 128);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(lowContrast, new Rectangle(Point.Empty, enlarged.Size),
                new Rectangle(Point.Empty, lowContrast.Size), GraphicsUnit.Pixel);
        }
        AssertTimeline(ImageDisplayDetector.Detect(enlarged) == ImageFilterMode.Point,
            "Integer enlargement changed the classification of a low-contrast sprite.");
        using var screenshot = new Bitmap(1143, 971);
        using (var graphics = Graphics.FromImage(screenshot))
        {
            graphics.Clear(Color.White);
            using var panelBrush = new SolidBrush(Color.LightGray);
            using var actionBrush = new SolidBrush(Color.DarkOrange);
            graphics.FillRectangle(panelBrush, 100, 100, 900, 700);
            graphics.FillRectangle(actionBrush, 700, 700, 300, 60);
        }
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
                screenshot.SetPixel(column, row, Color.FromArgb(column * 13, row * 13, (column + row) * 7));
        AssertTimeline(ImageDisplayDetector.Detect(screenshot) == ImageFilterMode.Bilinear,
            "A large flat UI screenshot lost its fine color detail during sampling and was classified as pixel art.");
        using var largeSprite = new Bitmap(1024, 1024);
        using (var graphics = Graphics.FromImage(largeSprite))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(lowContrast, new Rectangle(Point.Empty, largeSprite.Size),
                new Rectangle(Point.Empty, lowContrast.Size), GraphicsUnit.Pixel);
        }
        AssertTimeline(ImageDisplayDetector.Detect(largeSprite) == ImageFilterMode.Bilinear,
            "The automatic size limit did not apply to a large integer-scaled pixel-art image.");
        foreach (var size in new[] { new Size(256, 256), new Size(257, 256), new Size(256, 257),
            new Size(257, 32), new Size(32, 257) })
        {
            using var boundarySprite = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(boundarySprite))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(lowContrast, new Rectangle(Point.Empty, size),
                    new Rectangle(Point.Empty, lowContrast.Size), GraphicsUnit.Pixel);
            }
            var expectedFilter = Math.Max(size.Width, size.Height) > 256
                ? ImageFilterMode.Bilinear : ImageFilterMode.Point;
            AssertTimeline(ImageDisplayDetector.Detect(boundarySprite) == expectedFilter,
                $"The automatic size limit was not applied consistently at {size.Width}x{size.Height}.");
        }
        using var dithered = new Bitmap(16, 16);
        using var smallGradient = new Bitmap(16, 16);
        using var uniqueColors = new Bitmap(8, 8);
        using var translucentSprite = new Bitmap(16, 16);
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
            {
                dithered.SetPixel(column, row, mutedPalette[(column + row) % 2]);
                smallGradient.SetPixel(column, row, Color.FromArgb(column * 8, column * 8, column * 8));
                translucentSprite.SetPixel(column, row, Color.FromArgb(128, mutedPalette[(column / 2 + row / 2) % 3]));
                if (row < 8 && column < 8)
                    uniqueColors.SetPixel(column, row, Color.FromArgb(column * 29, row * 29, (column * 43 + row * 17) % 256));
            }
        AssertTimeline(ImageDisplayDetector.Detect(dithered) == ImageFilterMode.Point,
            "Low-contrast dithering was mistaken for a continuous gradient.");
        AssertTimeline(ImageDisplayDetector.Detect(smallGradient) == ImageFilterMode.Bilinear,
            "A 16x16 continuous gradient was classified as pixel art merely because it is small.");
        AssertTimeline(ImageDisplayDetector.Detect(uniqueColors) == ImageFilterMode.Bilinear,
            "A small image with mostly unique colors was incorrectly treated as a coherent pixel-art palette.");
        AssertTimeline(ImageDisplayDetector.Detect(translucentSprite) == ImageFilterMode.Bilinear,
            "Low-contrast detection dropped the existing soft-alpha safeguard.");
        using var gradient = new Bitmap(64, 64);
        for (var row = 0; row < gradient.Height; row++)
            for (var column = 0; column < gradient.Width; column++)
                gradient.SetPixel(column, row, Color.FromArgb(column * 4, column * 4, column * 4));
        AssertTimeline(ImageDisplayDetector.Detect(gradient) == ImageFilterMode.Bilinear,
            "A smooth low-palette gradient was misclassified as pixel art.");
        using var transparent = new Bitmap(32, 32);
        AssertTimeline(ImageDisplayDetector.Detect(transparent) == ImageFilterMode.Bilinear,
            "An empty image was incorrectly assigned a pixel-art display mode.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { ImageDisplayDetector.Detect(sprite, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertTimeline(cancelled, "Image display detection ignored cancellation.");
        Console.WriteLine("image_display_detection_regression=passed");
    }

    private static void RunImageBatchPreviewRegression()
    {
        var root = CreateTemporaryDirectory("image-batch-preview");
        var previousContext = SynchronizationContext.Current;
        var previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        WindowsFormsSynchronizationContext.AutoInstall = false;
        var context = previousContext as WindowsFormsSynchronizationContext
            ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var files = new List<ImageBatchFile>();
            for (var index = 0; index < 80; index++)
            {
                var path = Path.Combine(root, $"sprite-{index:D3}.png");
                WriteFixturePng(path, 32, 24);
                files.Add(new ImageBatchFile(path, Path.GetFileName(path), 32, 24, 96, ImageFilterMode.Point));
            }
            var invalidPath = Path.Combine(root, "corrupt.png");
            File.WriteAllText(invalidPath, "invalid image");
            files.Insert(10, new ImageBatchFile(invalidPath, "corrupt.png", 32, 24, 96));
            var type = typeof(ImageImportSettingsDialog);
            using var dialog = new ImageImportSettingsDialog("Import", files[0].Name, 32, 24, 96,
                BitmapImageImportSettings.Default, files[0].Path, batchCount: files.Count, batchFiles: files);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-20000, -20000);
            dialog.Show();
            Application.DoEvents();
            var setFile = RequireMethod(type, "SetBatchFile", [typeof(int)]);
            var setStrip = RequireMethod(type, "SetStripMode", [typeof(bool)]);
            var loadStrip = RequireMethod(type, "LoadStripThumbnailsAsync", Type.EmptyTypes);
            var remove = RequireMethod(type, "RemoveBatchFiles", [typeof(IReadOnlyList<int>)]);
            var finish = RequireMethod(type, "FinishStripFill", Type.EmptyTypes);
            var items = (System.Collections.IList)RequireField(type, "_thumbItems").GetValue(dialog)!;
            var scroll = (ThemedScrollPanel)RequireField(type, "_stripScroll").GetValue(dialog)!;
            var outerScroll = (ThemedScrollPanel)RequireField(type, "_scroll").GetValue(dialog)!;
            var mode = (ComboBox)RequireField(type, "_displayMode").GetValue(dialog)!;
            AssertTimeline(dialog.AutomaticDisplay && dialog.Settings.FilterMode == ImageFilterMode.Point,
                "A new batch did not use its per-image automatic display suggestions.");
            setFile.Invoke(dialog, [50]);
            setStrip.Invoke(dialog, [true]);
            WaitForLoad((Task)loadStrip.Invoke(dialog, null)!);
            AssertTimeline(items.Cast<Control>().All(item =>
                item.GetType().GetProperty("Loading")!.GetValue(item) is false),
                "A corrupt thumbnail left this or later files permanently loading.");
            AssertTimeline(items[0]!.GetType().GetProperty("Thumbnail")!.GetValue(items[0]) is Bitmap,
                "The batch grid did not display a decoded thumbnail.");
            var survivor = items[2];
            scroll.RestoreScrollPosition(100);
            remove.Invoke(dialog, [new[] { 0, 1 }]);
            finish.Invoke(dialog, null);
            AssertTimeline(ReferenceEquals(items[0], survivor) && items.Count == files.Count - 2
                && (string?)RequireField(type, "_sourcePath").GetValue(dialog) == files[50].Path,
                "Deleting earlier thumbnails rebuilt surviving controls or switched the active file.");
            var position = scroll.ScrollPosition;
            var outerPosition = outerScroll.ScrollPosition;
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120);
            RequireMethod(typeof(Control), "OnMouseWheel", [typeof(MouseEventArgs)]).Invoke(scroll, [wheel]);
            AssertTimeline(wheel.Handled && scroll.ScrollPosition > position && outerScroll.ScrollPosition == outerPosition,
                "The thumbnail grid lost wheel routing after deletion or scrolled the outer settings panel.");
            RequireMethod(type, "SetAdvancedExpanded", [typeof(bool)]).Invoke(dialog, [true]);
            var bounds = mode.Bounds;
            var windowSize = dialog.Size;
            mode.SelectedIndex = 0;
            dialog.PerformLayout();
            mode.SelectedIndex = 1;
            dialog.PerformLayout();
            AssertTimeline(mode.Bounds == bounds && dialog.Size == windowSize,
                "Changing image display mode moved the selector or resized the settings dialog.");
            var pending = (Task)loadStrip.Invoke(dialog, null)!;
            dialog.Dispose();
            WaitForLoad(pending);
            using var single = new ImageImportSettingsDialog("Import", files[0].Name, 32, 24, 96,
                BitmapImageImportSettings.Default, files[0].Path, batchFiles: [files[0]]);
            AssertTimeline(single.RemainingBatchFiles.SequenceEqual(new[] { files[0].Path }),
                "The single-image import dialog dropped the only pending file.");
            var popupType = typeof(LibraryVaultPanel).GetNestedType("VaultPreviewForm", BindingFlags.NonPublic)!;
            using var popup = (Form)Activator.CreateInstance(popupType, true)!;
            var project = VectorProject.CreateEmpty();
            var pointSettings = BitmapImageImportSettings.Default with { FilterMode = ImageFilterMode.Point };
            AssertTimeline(project.TryAddImageAssetFromSource("Pixel sprite", files[0].Path,
                BitmapImageRasterizer.ComputeSha256(files[0].Path), 32, 24, 96, pointSettings, out var asset),
                "Could not prepare a pixel-art popup fixture.");
            var showImage = RequireMethod(popupType, "ShowImageAsset",
                [typeof(ImageAssetDefinition), typeof(BitmapImageRaster)], BindingFlags.Instance | BindingFlags.Public);
            var raster = BitmapImageRasterizer.Decode(files[0].Path, pointSettings);
            showImage.Invoke(popup, [asset, raster]);
            var picture = (PictureBox)RequireField(popupType, "_imagePreview").GetValue(popup)!;
            var filterProperty = picture.GetType().GetProperty("PointFilter")!;
            AssertTimeline(filterProperty.GetValue(picture) is true, "The Vault popup ignored the pixel-art display mode.");
            project.TryApplyImageImportSettings(asset!.Id, pointSettings with { FilterMode = ImageFilterMode.Bilinear },
                32, 24, asset.Sha256);
            showImage.Invoke(popup, [asset, raster]);
            AssertTimeline(filterProperty.GetValue(picture) is false, "The Vault popup retained the previous image's pixel-art filter.");
            RequireMethod(popupType, "HideImagePreview", Type.EmptyTypes).Invoke(popup, null);
            Console.WriteLine("image_batch_preview_regression=passed");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
            DeleteTemporaryDirectory(root);
        }

        static void WaitForLoad(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            AssertTimeline(task.IsCompleted, "Batch preview loading did not finish within 30 seconds.");
            task.GetAwaiter().GetResult();
        }
    }

    private static void RunImageCropRegression()
    {
        var root = CreateTemporaryDirectory("image-crop-regression");
        try
        {
            var path = Path.Combine(root, "crop.png");
            WriteFixturePng(path, 64, 48);
            var originalHash = BitmapImageRasterizer.ComputeSha256(path);
            var settings = BitmapImageImportSettings.Default with { FilterMode = ImageFilterMode.Point, CropWidth = 0.5f, CropHeight = 0.5f };
            AssertTimeline(settings.ResolveCropPixelBounds(64, 48) == new Rectangle(0, 0, 32, 24)
                && settings.ResolveStoredPixelSize(new SizeF(64, 48)) == new SizeF(32, 24)
                && !(settings with { CropWidth = 0 }).IsValid
                && !(settings with { CropX = 0.75f }).IsValid
                && !(settings with { CropY = float.NaN }).IsValid, "Crop bounds or validation are incorrect.");
            var left = BitmapImageRasterizer.Decode(path, settings);
            var right = BitmapImageRasterizer.Decode(path, settings with { CropX = 0.5f });
            using (var leftBitmap = left.AcquireBitmap())
            using (var rightBitmap = right.AcquireBitmap())
                AssertTimeline(left.PixelWidth == 32 && left.PixelHeight == 24 && !ReferenceEquals(left, right)
                    && leftBitmap.Bitmap.GetPixel(16, 12).ToArgb() == Color.OrangeRed.ToArgb()
                    && rightBitmap.Bitmap.GetPixel(16, 12).ToArgb() == Color.CornflowerBlue.ToArgb(),
                    "Cropping decoded the wrong pixels or reused another selection's cache.");
            var fullSettings = settings with { CropWidth = 1, CropHeight = 1 };
            var horizontal = BitmapImageRasterizer.Decode(path, fullSettings with { FlipHorizontal = true });
            var vertical = BitmapImageRasterizer.Decode(path, fullSettings with { FlipVertical = true });
            var both = BitmapImageRasterizer.Decode(path, fullSettings with { FlipHorizontal = true, FlipVertical = true });
            using (var horizontalBitmap = horizontal.AcquireBitmap())
            using (var verticalBitmap = vertical.AcquireBitmap())
            using (var bothBitmap = both.AcquireBitmap())
                AssertTimeline(!ReferenceEquals(horizontal, vertical) && !ReferenceEquals(vertical, both)
                    && horizontal.PixelWidth == 64 && horizontal.PixelHeight == 48
                    && horizontalBitmap.Bitmap.GetPixel(56, 8).ToArgb() == Color.OrangeRed.ToArgb()
                    && horizontalBitmap.Bitmap.GetPixel(8, 8).ToArgb() == Color.CornflowerBlue.ToArgb()
                    && verticalBitmap.Bitmap.GetPixel(8, 40).ToArgb() == Color.OrangeRed.ToArgb()
                    && bothBitmap.Bitmap.GetPixel(56, 40).ToArgb() == Color.OrangeRed.ToArgb(),
                    "Image flips used the wrong axis, changed dimensions, or shared a different orientation's raster cache.");
            var project = VectorProject.CreateEmpty();
            var persistedSettings = settings with { FlipHorizontal = true, FlipVertical = true };
            AssertTimeline(project.TryAddImageAssetFromSource("Crop", path, originalHash, 64, 48, 96, persistedSettings, out var asset),
                "Could not prepare cropped asset.");
            var manifestPath = Path.Combine(root, "Crop.v2dProject");
            ProjectVaultStore.Save(project, manifestPath);
            File.Delete(path);
            var restored = ProjectVaultStore.Load(manifestPath).ImageAssets.Single();
            AssertTimeline(restored.ImportSettings == persistedSettings && restored.PixelWidth == 64 && restored.PixelHeight == 48
                && BitmapImageRasterizer.ComputeSha256(restored.SourcePath) == originalHash
                && BitmapImageRasterizer.Decode(restored.SourcePath, settings with { CropWidth = 1, CropHeight = 1 }).PixelWidth == 64,
                "Saving cropped images lost the crop or modified the original managed file.");
            var legacyJson = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(BitmapImageImportSettings.Default))!.AsObject();
            foreach (var member in new[] { "CropX", "CropY", "CropWidth", "CropHeight", "FlipHorizontal", "FlipVertical" }) legacyJson.Remove(member);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<BitmapImageImportSettings>(legacyJson.ToJsonString());
            AssertTimeline(legacy.IsValid && !legacy.FlipHorizontal && !legacy.FlipVertical
                && legacy.ResolveCropPixelBounds(64, 48) == new Rectangle(0, 0, 64, 48),
                "Legacy image settings without crop fields no longer restore the full image.");
            using var image = new Bitmap(64, 48);
            using var parent = new ImageImportSettingsDialog("Import", "Crop", 64, 48, 96, settings);
            using var editor = new ImageCropDialog(image, new Size(64, 48), settings, false);
            var fields = (ModernNumericUpDown[])RequireField(typeof(ImageCropDialog), "_values").GetValue(editor)!;
            fields[2].Value = 10;
            fields[0].Value = 54;
            fields[2].Value = 64;
            AssertTimeline(editor.Selection == new Rectangle(54, 0, 10, 24) && parent.Settings == settings,
                "Precise crop fields escaped source bounds or mutated the parent before confirmation.");
            var selectionBeforeFlip = editor.Selection;
            var toggleFlip = RequireMethod(typeof(ImageCropDialog), "ToggleFlip", [typeof(bool)]);
            toggleFlip.Invoke(editor, [true]);
            toggleFlip.Invoke(editor, [false]);
            AssertTimeline(editor.FlipHorizontal && editor.FlipVertical && editor.Selection == selectionBeforeFlip,
                "Flipping changed the selected source pixels instead of mirroring the cropped result.");
            toggleFlip.Invoke(editor, [true]);
            toggleFlip.Invoke(editor, [false]);
            AssertTimeline(!editor.FlipHorizontal && !editor.FlipVertical && editor.Selection == selectionBeforeFlip
                && !typeof(Form).IsAssignableFrom(typeof(ImageCropDialog)),
                "Repeated flips did not restore the crop, or the image editor still creates a separate window.");
            var thumbnail = (Bitmap?)RequireMethod(typeof(ImageImportSettingsDialog), "LoadThumbnail",
                [typeof(string), typeof(BitmapImageImportSettings), typeof(CancellationToken)], BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, [restored.SourcePath, settings, CancellationToken.None]);
            using (thumbnail) AssertTimeline(thumbnail is { Width: 32, Height: 24 }, "Import preview did not reflect the actual crop.");
            RunImageCropInteractionRegression();
            Console.WriteLine("image_crop_regression=passed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunImageCropInteractionRegression()
    {
        using var image = new Bitmap(320, 180);
        using var editor = new ImageCropDialog(image, new Size(320, 180), BitmapImageImportSettings.Default, false);
        var type = typeof(ImageCropDialog);
        editor.ClientSize = new Size(920, 640);
        editor.PerformLayout();
        var canvas = (Panel)RequireField(type, "_canvas").GetValue(editor)!;
        canvas.Size = new Size(700, 400);
        var getStyle = typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AssertTimeline((bool)getStyle.Invoke(canvas, [ControlStyles.OptimizedDoubleBuffer])! &&
            (bool)getStyle.Invoke(canvas, [ControlStyles.AllPaintingInWmPaint])! &&
            (bool)getStyle.Invoke(canvas, [ControlStyles.Opaque])!,
            "The crop canvas exposes background clearing instead of presenting a buffered frame.");
        var set = RequireMethod(type, "SetSelection", [typeof(Rectangle)]);
        var begin = RequireMethod(type, "BeginDrag", [typeof(object), typeof(MouseEventArgs)]);
        var move = RequireMethod(type, "ContinueDrag", [typeof(object), typeof(MouseEventArgs)]);
        var end = RequireMethod(type, "EndDrag", [typeof(bool)]);
        RectangleF Frame() => (RectangleF)RequireField(type, "_frame").GetValue(editor)!;
        void Drag(Point start, Point target, bool cancel = false)
        {
            begin.Invoke(editor, [canvas, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
            move.Invoke(editor, [canvas, new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0)]);
            end.Invoke(editor, [cancel]);
        }
        set.Invoke(editor, [new Rectangle(0, 0, 320, 180)]);
        var frame = Frame();
        var imageBounds = (RectangleF)RequireField(type, "_imageBounds").GetValue(editor)!;
        var canvasSize = canvas.Size;
        var start = Point.Round(new PointF(frame.Left, frame.Top + frame.Height / 2));
        begin.Invoke(editor, [canvas, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
        for (var i = 0; i < 120; i++)
        {
            var x = start.X + (i % 40);
            move.Invoke(editor, [canvas, new MouseEventArgs(MouseButtons.Left, 1, x, start.Y, 0)]);
            AssertTimeline((RectangleF)RequireField(type, "_imageBounds").GetValue(editor)! == imageBounds &&
                canvas.Size == canvasSize && (int)RequireField(type, "_dragMode").GetValue(editor)! == 2,
                "Continuous crop edge movement moved/refitted the image or interrupted pointer capture.");
        }
        end.Invoke(editor, [true]);
        AssertTimeline(editor.Selection == new Rectangle(0, 0, 320, 180),
            "Canceling continuous crop resizing failed to restore the source selection.");
        Drag(Point.Round(new PointF(frame.Left, frame.Top + frame.Height / 2)),
            Point.Round(new PointF(frame.Left + frame.Width / 4, frame.Top + frame.Height / 2)));
        AssertTimeline(editor.Selection.Width is >= 239 and <= 241 && editor.Selection.Height == 180,
            "Dragging an edge did not crop one axis.");
        var before = editor.Selection; frame = Frame();
        Drag(Point.Round(new PointF(frame.Left, frame.Top)), new Point(10000, 10000), true);
        AssertTimeline(editor.Selection == before, "Canceling a crop gesture did not restore its selection.");
        Drag(Point.Round(new PointF(frame.Left, frame.Top)), new Point(10000, 10000));
        AssertTimeline(editor.Selection.Width > 0 && editor.Selection.Height > 0 && editor.Selection.Right <= 320 && editor.Selection.Bottom <= 180,
            "Dragging across the opposite corner inverted or escaped the crop.");
        set.Invoke(editor, [new Rectangle(80, 45, 160, 90)]); frame = Frame();
        Drag(Point.Round(new PointF(frame.X + frame.Width / 2, frame.Y + frame.Height / 2)), new Point(10000, 10000));
        AssertTimeline(editor.Selection.Location == Point.Empty && editor.Selection.Size == new Size(160, 90),
            "Image panning exposed empty space or resized the crop. "
            + $"(selection={editor.Selection}, frame={frame}, imageBounds={(RectangleF)RequireField(type, "_imageBounds").GetValue(editor)!}, "
            + $"dragMode={RequireField(type, "_dragMode").GetValue(editor)}, canvas={canvas.Size}, dpi={canvas.DeviceDpi}).");
        var ratio = RequireMethod(type, "SetRatio", [typeof(int)]);
        set.Invoke(editor, [new Rectangle(0, 0, 320, 180)]); ratio.Invoke(editor, [2]);
        AssertTimeline(editor.Selection.Width == editor.Selection.Height, "Square ratio did not create a square crop.");
        frame = Frame();
        Drag(Point.Round(new PointF(frame.Right, frame.Top + frame.Height / 2)),
            Point.Round(new PointF(frame.Right - frame.Width / 4, frame.Top + frame.Height / 2)));
        AssertTimeline(Math.Abs(editor.Selection.Width - editor.Selection.Height) <= 1, "Edge resizing lost the locked ratio.");
        var zoom = RequireMethod(type, "Zoom", [typeof(float)]); before = editor.Selection;
        zoom.Invoke(editor, [2f]);
        AssertTimeline(editor.Selection.Width < before.Width && Math.Abs(editor.Selection.Width - editor.Selection.Height) <= 1,
            "Zoom did not crop around the frame center or lost the ratio.");
        zoom.Invoke(editor, [0.00001f]);
        AssertTimeline(editor.Selection.Right <= 320 && editor.Selection.Bottom <= 180, "Zooming out exposed pixels outside the source.");
        ratio.Invoke(editor, [0]); set.Invoke(editor, [new Rectangle(0, 0, 320, 180)]);
        var values = (ModernNumericUpDown[])RequireField(type, "_values").GetValue(editor)!;
        values[0].Value = 300;
        AssertTimeline(editor.Selection == new Rectangle(300, 0, 20, 180), "Precise position input was blocked by the old crop width.");
        AssertTimeline(!((Control)RequireField(type, "_fields").GetValue(editor)!).Visible, "Precise fields should start collapsed.");
        Console.WriteLine("image_crop_interaction_regression=passed");
    }

    private static void WriteFixturePng(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
            using var brush = new SolidBrush(Color.OrangeRed);
            graphics.FillRectangle(brush, 0, 0, width / 2, height / 2);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static bool ThrowsMissingBitmapPayload(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentNullException exception) when (exception.ParamName == "bitmapObjectData")
        {
            return true;
        }
    }
}
