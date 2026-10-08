using System.Drawing;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    /// <summary>
    /// Covers dragging image files in from the shell onto the asset library and the Stage:
    /// extension routing, reject rules, library-side batch import, and the Stage drop
    /// preview that must never mutate the project while hovering.
    /// </summary>
    private static void RunImageDropRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("image-drop-regression");
        var sourceDirectory = Path.Combine(temporaryRoot, "source");
        var imagePath = Path.Combine(sourceDirectory, "sprite.png");
        var secondImagePath = Path.Combine(sourceDirectory, "sprite-2.png");
        var uppercaseImagePath = Path.Combine(sourceDirectory, "SPRITE-UPPER.PNG");
        var svgPath = Path.Combine(sourceDirectory, "icon.svg");
        var textPath = Path.Combine(sourceDirectory, "notes.txt");
        var directoryPath = Path.Combine(sourceDirectory, "folder.png");
        Directory.CreateDirectory(sourceDirectory);
        WriteFixturePng(imagePath, 40, 24);
        WriteFixturePng(secondImagePath, 16, 16);
        WriteFixturePng(uppercaseImagePath, 12, 12);
        File.WriteAllText(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\"/></svg>");
        File.WriteAllText(textPath, "not an image");
        Directory.CreateDirectory(directoryPath);
        try
        {
            RunDroppedAssetFileClassificationRegression(
                imagePath,
                secondImagePath,
                uppercaseImagePath,
                svgPath,
                textPath,
                directoryPath);
            RunLibraryImageDropRegression(imagePath, secondImagePath, svgPath, textPath);
            using var form = new MainForm();
            RunStageImageDropPreviewRegression(form, imagePath);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }

        Console.WriteLine("image_drop_regression=passed");
    }

    private static DataObject FileDrop(params string[] paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return data;
    }

    private static void RunDroppedAssetFileClassificationRegression(
        string imagePath,
        string secondImagePath,
        string uppercaseImagePath,
        string svgPath,
        string textPath,
        string directoryPath)
    {
        // Each recognized asset kind routes to its own importer.
        AssertTimeline(
            LibraryVaultPanel.TryResolveDroppedAssetFiles(FileDrop(imagePath), out var imageOnly)
            && imageOnly.ImageFiles.SequenceEqual([imagePath])
            && imageOnly.SvgFiles.Length == 0
            && imageOnly.SymbolFiles.Length == 0
            && LibraryVaultPanel.TryResolveDroppedAssetFiles(FileDrop(svgPath), out var svgOnly)
            && svgOnly.SvgFiles.SequenceEqual([svgPath])
            && svgOnly.ImageFiles.Length == 0,
            "Image and SVG drops were not classified into their own importers.");

        // Extensions are matched case-insensitively and duplicates collapse.
        AssertTimeline(
            LibraryVaultPanel.TryResolveDroppedAssetFiles(
                FileDrop(uppercaseImagePath, uppercaseImagePath),
                out var upper)
            && upper.ImageFiles.Length == 1
            && upper.ImageFiles[0] == uppercaseImagePath,
            "Image drop routing did not accept a case-insensitive extension or collapsed duplicates.");

        AssertTimeline(
            LibraryVaultPanel.TryResolveDroppedAssetFiles(
                FileDrop(imagePath, secondImagePath),
                out var ordered)
            && ordered.ImageFiles.SequenceEqual([imagePath, secondImagePath]),
            "A multi-image drop did not preserve distinct file order.");

        // Unsupported, missing, directory, non-file and mixed-kind payloads must stay rejected.
        foreach (var invalid in new IDataObject?[]
                 {
                     null,
                     new DataObject(DataFormats.Text, imagePath),
                     FileDrop(),
                     FileDrop(""),
                     FileDrop(textPath),
                     FileDrop(directoryPath),
                     FileDrop(Path.Combine(Path.GetDirectoryName(imagePath)!, "missing.png")),
                     FileDrop(imagePath, textPath),
                     FileDrop(imagePath, svgPath)
                 })
        {
            AssertTimeline(
                !LibraryVaultPanel.TryResolveDroppedAssetFiles(invalid, out _),
                "The asset library accepted an unsupported, mixed-kind, missing or directory drop.");
        }

        // The symbol-package helper must keep its strict single-kind behaviour now that it
        // shares the generic classifier, or existing package drops would regress.
        AssertTimeline(
            !LibraryVaultPanel.TryResolveDroppedSymbolFiles(FileDrop(imagePath), out _)
            && !LibraryVaultPanel.TryResolveDroppedSymbolFiles(FileDrop(svgPath), out _),
            "Symbol package drop recognition started accepting non-package files.");
    }

    /// <summary>
    /// Drop routing is exercised on a standalone panel. A real <see cref="MainForm"/>
    /// subscribes to these events with importers that open a modal import-settings dialog,
    /// which cannot complete in a headless run, so the panel is bound to an empty project
    /// and only its own event contract is asserted here.
    /// </summary>
    private static void RunLibraryImageDropRegression(
        string imagePath,
        string secondImagePath,
        string svgPath,
        string textPath)
    {
        using var panel = new LibraryVaultPanel();
        panel.BindProject(VectorProject.CreateEmpty(), null);
        var tree = (TreeView)RequireField(typeof(LibraryVaultPanel), "_projectObjects").GetValue(panel)!;

        var dragEnter = RequireMethod(typeof(Control), "OnDragEnter", [typeof(DragEventArgs)]);
        var dragOver = RequireMethod(typeof(Control), "OnDragOver", [typeof(DragEventArgs)]);
        var dragDrop = RequireMethod(typeof(Control), "OnDragDrop", [typeof(DragEventArgs)]);
        static DragEventArgs Drag(IDataObject? data, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move) =>
            new(data, 0, 0, 0, allowed, DragDropEffects.None);

        var imageDrops = 0;
        IReadOnlyList<string> droppedImageFiles = [];
        var svgDrops = 0;
        IReadOnlyList<string> droppedSvgFiles = [];
        var symbolDrops = 0;
        panel.ImageAssetFilesDropped += (_, e) =>
        {
            imageDrops++;
            droppedImageFiles = e.FileNames;
        };
        panel.ExternalSvgAssetFilesDropped += (_, e) =>
        {
            svgDrops++;
            droppedSvgFiles = e.FileNames;
        };
        panel.DrawingObjectSymbolImportRequested += (_, _) => symbolDrops++;

        // Hovering must advertise Copy without importing anything.
        var hover = Drag(FileDrop(imagePath));
        dragEnter.Invoke(tree, [hover]);
        dragOver.Invoke(tree, [hover]);
        AssertTimeline(
            hover.Effect == DragDropEffects.Copy && imageDrops == 0,
            "Image drag feedback must advertise Copy without importing during hover.");

        // Images are copied into the project, so a Move-only drag is refused.
        var moveOnly = Drag(FileDrop(imagePath), DragDropEffects.Move);
        dragEnter.Invoke(tree, [moveOnly]);
        dragDrop.Invoke(tree, [moveOnly]);
        AssertTimeline(
            moveOnly.Effect == DragDropEffects.None && imageDrops == 0,
            "Image files must be copied into the library, never moved from disk.");

        // A rejected payload must not reach any importer.
        var rejected = Drag(FileDrop(textPath));
        dragEnter.Invoke(tree, [rejected]);
        dragDrop.Invoke(tree, [rejected]);
        AssertTimeline(
            rejected.Effect == DragDropEffects.None && imageDrops == 0 && svgDrops == 0,
            "The library accepted a non-image file as an image drop.");

        dragDrop.Invoke(tree, [hover]);
        AssertTimeline(
            imageDrops == 1 && droppedImageFiles.SequenceEqual([imagePath]),
            "Dropping an image did not route to the image importer.");

        // A multi-image drop stays one batch so the importer can share one settings choice.
        var batch = Drag(FileDrop(imagePath, secondImagePath));
        dragDrop.Invoke(tree, [batch]);
        AssertTimeline(
            imageDrops == 2
            && droppedImageFiles.SequenceEqual([imagePath, secondImagePath])
            && svgDrops == 0
            && symbolDrops == 0,
            "A multi-image drop was not routed to the image importer as one batch.");

        // A lone SVG routes to the external-SVG importer, not the image or symbol one.
        var svgOnly = Drag(FileDrop(svgPath));
        dragEnter.Invoke(tree, [svgOnly]);
        dragDrop.Invoke(tree, [svgOnly]);
        AssertTimeline(
            svgOnly.Effect == DragDropEffects.Copy
            && svgDrops == 1
            && droppedSvgFiles.SequenceEqual([svgPath])
            && imageDrops == 2
            && symbolDrops == 0,
            "A lone SVG drop was not routed to the external-SVG importer.");

        // Mixed kinds are rejected before reaching any importer.
        var mixedKinds = Drag(FileDrop(imagePath, svgPath));
        dragEnter.Invoke(tree, [mixedKinds]);
        dragDrop.Invoke(tree, [mixedKinds]);
        AssertTimeline(
            mixedKinds.Effect == DragDropEffects.None && imageDrops == 2 && svgDrops == 1,
            "A mixed-kind drop reached an importer instead of being rejected.");
    }

    /// <summary>
    /// The Stage must show what a drop would insert without touching the project: the
    /// preview is a throwaway clone of a real placement, and the source scene keeps its
    /// object count for the whole hover.
    /// </summary>
    private static void RunStageImageDropPreviewRegression(MainForm form, string imagePath)
    {
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;

        // Register the image exactly the way the drop path does so the preview can resolve it.
        var importFromPath = RequireMethod(
            typeof(MainForm),
            "ImportImageAssetFromPath",
            [typeof(string), typeof(bool)]);
        var asset = (ImageAssetDefinition?)importFromPath.Invoke(form, [imagePath, false]);
        AssertTimeline(asset is not null, "The image import path did not register a valid fixture image.");

        var buildPreview = RequireMethod(
            typeof(MainForm),
            "BuildImageDropPreviewScene",
            [typeof(ImageAssetDefinition), typeof(string)]);
        var objectCountBefore = scene.ObjectCount;
        var preview = (VectorScene?)buildPreview.Invoke(form, [asset!, imagePath]);
        AssertTimeline(
            preview is { ObjectCount: 1 } && scene.ObjectCount == objectCountBefore,
            "Building an image drop preview mutated the edited scene.");

        // The preview clone must render the same thing a real placement would: a bitmap
        // object pointing at the same asset with the same decode-aware quad size.
        AssertTimeline(
            preview!.TryGetBitmapObjectData(0, out var previewData)
            && previewData.ImageAssetId == asset!.Id
            && previewData.IsValid,
            "The image drop preview did not carry a valid bitmap payload.");

        var loadedAsset = asset!;
        var placedSize = ScalePlacedSizeForDecodeProbe(loadedAsset, imagePath);
        AssertTimeline(
            Math.Abs(previewData.PlacedSize.Width - placedSize.Width) <= 0.01f
            && Math.Abs(previewData.PlacedSize.Height - placedSize.Height) <= 0.01f,
            "The image drop preview size did not match the committed placement size.");

        // Translating the preview must not touch the edited scene's geometry revision.
        var revisionBefore = scene.GeometryRevision;
        preview.TranslateAllObjectsForPreview(25, -10);
        AssertTimeline(
            scene.GeometryRevision == revisionBefore
            && Math.Abs(preview.X[0] - 25) <= 0.01f
            && Math.Abs(preview.Y[0] + 10) <= 0.01f,
            "Translating the drop preview wrote through to the edited scene.");

        // A defective payload produces no preview rather than a broken object.
        AssertTimeline(
            VectorScene.CreateObjectPreviewScene(scene, -1) is null
            && VectorScene.CreateObjectPreviewScene(scene, scene.ObjectCount) is null,
            "The object preview clone accepted an out-of-range object index.");

        var image = scene.AddBitmapObject(scene.ActiveLayer, PointF.Empty,
            new BitmapObjectData { ImageAssetId = loadedAsset.Id, PlacedSize = new SizeF(600, 400) });
        scene.Width[image] = 900;
        scene.Height[image] = 600;
        var select = RequireMethod(typeof(MainForm), "SetSelection", [typeof(DrawingElementHit), typeof(bool)]);
        void SelectImage(int index) => select.Invoke(form, [new DrawingElementHit(
            new DrawingElementKey(index, DrawingElementKind.Fill, 0), 0, 0, 1), false]);
        SelectImage(image);
        AssertTimeline((bool)RequireMethod(typeof(MainForm), "CopySelectedObjects").Invoke(form, null)!
            && (bool)RequireMethod(typeof(MainForm), "PasteCopiedObjects").Invoke(form, null)!,
            "Copy/paste rejected a clicked bitmap selection.");
        var copy = scene.ObjectCount - 1;
        AssertTimeline(scene.TryGetBitmapObjectData(copy, out var copied) && copied.ImageAssetId == loadedAsset.Id
            && scene.Width[copy] == 900 && scene.Height[copy] == 600, "Image paste lost its payload or current scale.");
        SelectImage(copy);
        AssertTimeline((bool)RequireMethod(typeof(MainForm), "MoveSelectedDrawingObjectsInStack").Invoke(form, [-1])!,
            "Image stack ordering rejected a clicked bitmap selection.");
        var inspector = (ImageInspectorPanel)RequireField(typeof(MainForm), "_imageInspector").GetValue(form)!;
        var summary = (Label)RequireField(typeof(ImageInspectorPanel), "_summary").GetValue(inspector)!;
        AssertTimeline(summary.Text.Contains(loadedAsset.Name) && summary.Text.Contains(loadedAsset.SourcePath),
            "Selecting an image did not show its import information.");
        var other = project.TryAddImageAssetFromSource("Other image", imagePath, loadedAsset.Sha256,
            40, 24, 96, loadedAsset.ImportSettings with { PixelsPerUnit = 321 }, out var otherAsset);
        AssertTimeline(other && otherAsset is not null, "Could not prepare an independent image import.");
        var otherIndex = scene.AddBitmapObject(scene.ActiveLayer, new PointF(1000, 0),
            new BitmapObjectData { ImageAssetId = otherAsset!.Id, PlacedSize = new SizeF(300, 200) });
        SelectImage(otherIndex);
        RequireMethod(typeof(MainForm), "UpdateInspector", [typeof(bool)]).Invoke(form, [true]);
        AssertTimeline(summary.Text.Contains("Other image") && summary.Text.Contains("321"),
            "The inspector reused another image import settings.");
    }

    private static SizeF ScalePlacedSizeForDecodeProbe(ImageAssetDefinition asset, string path)
    {
        var raster = BitmapImageRasterizer.Decode(path, asset.ImportSettings);
        var placed = asset.ResolvePlacedSize();
        if (asset.PixelWidth <= 0 || asset.PixelHeight <= 0) return placed;
        var scale = Math.Min(
            raster.PixelWidth / (float)asset.PixelWidth,
            raster.PixelHeight / (float)asset.PixelHeight);
        if (scale <= 0 || !float.IsFinite(scale) || scale >= 0.9999f) return placed;
        return new SizeF(
            Math.Max(1, VectorUnits.Quantize(placed.Width * scale)),
            Math.Max(1, VectorUnits.Quantize(placed.Height * scale)));
    }
}
