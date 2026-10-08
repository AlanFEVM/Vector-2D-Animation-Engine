using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunImageAssetPersistenceRegression()
    {
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
            ThrowsInvalidOperation(() =>
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

    private static bool ThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
