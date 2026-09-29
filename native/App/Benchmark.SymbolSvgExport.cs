using System.Globalization;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    /// <summary>
    /// Covers the single-symbol SVG export: document order must follow the stage's
    /// layer stacking (layer 0 on top), and the exported range must contain only
    /// content the stage would visibly draw at the requested frame.
    /// </summary>
    private static void RunDrawingObjectSvgExportRegression()
    {
        RunDrawingObjectSvgViewportRegression();
        var scene = new VectorScene();
        scene.CreateEmpty(3, 8);

        // Bottom layer holds two stacked objects so intra-layer order is exercised.
        var lower = scene.AddObject(
            0,
            new PointF(-100, 0),
            new SizeF(80, 60),
            0,
            0,
            Color.Coral,
            4,
            ShapeKind.Rectangle);
        var upperInLayer = scene.AddObject(
            0,
            new PointF(-20, 0),
            new SizeF(80, 60),
            0,
            0,
            Color.Teal,
            4,
            ShapeKind.Rectangle);
        var top = scene.AddObject(
            2,
            new PointF(120, 0),
            new SizeF(80, 60),
            0,
            0,
            Color.Gold,
            4,
            ShapeKind.Rectangle);

        // Expected order is the engine's canonical bottom-to-top paint order, which is
        // exactly how Break Apart flattens a symbol: descending layer index first, then
        // ascending ObjectOrder, then ObjectSubOrder. A higher layer index therefore
        // sits underneath, so object 2 (layer 2) is the bottom-most and layer 0 paints
        // on top of it, with objects 0 then 1 stacked in ascending ObjectOrder.
        var ordered = DrawingObjectSvgExport.VisibleObjectsInPaintOrder(scene, 0);
        AssertTimeline(
            ordered.SequenceEqual([top, lower, upperInLayer]),
            "Symbol SVG export did not order objects by descending layer and ascending stack order.");

        // Emitted document order must agree with the resolved paint order.
        var document = DrawingObjectSvgExport.CreateDocument("export-regression", "Export", scene, 0);
        var emitted = document
            .Descendants()
            .Where(element => element.Attribute("data-v2d-object-index") is not null)
            .Select(element => int.Parse(
                element.Attribute("data-v2d-object-index")!.Value,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        AssertTimeline(
            emitted.SequenceEqual(ordered),
            "Symbol SVG export emitted document order that disagrees with the resolved paint order.");

        // Document order is paint order, so the bottom-most layer (highest index) is
        // emitted first and layer 0 last.
        var layerGroups = document
            .Descendants()
            .Where(element => element.Name.LocalName == "g"
                && element.Attribute("id")?.Value.StartsWith("v2d-layer-", StringComparison.Ordinal) == true)
            .Select(element => element.Attribute("id")!.Value)
            .ToArray();
        AssertTimeline(
            layerGroups.SequenceEqual(["v2d-layer-2", "v2d-layer-0"]),
            "Symbol SVG export did not emit the bottom-most layer first.");

        // Hiding the bottom-most layer must remove it and its group entirely.
        AssertTimeline(scene.SetLayerVisible(2, false), "Hiding the bottom layer had no effect.");
        var hiddenBottom = DrawingObjectSvgExport.VisibleObjectsInPaintOrder(scene, 0);
        AssertTimeline(
            hiddenBottom.SequenceEqual([lower, upperInLayer]),
            "Symbol SVG export did not exclude a hidden layer from the visible range.");
        AssertTimeline(scene.SetLayerVisible(2, true), "Restoring the bottom layer had no effect.");

        // A hidden parent folder must hide its child layer even when the child itself
        // is still marked visible.
        var parentFolder = scene.AddFolderLayer("Folder");
        var childLayer = scene.AddLayer("Child");
        AssertTimeline(
            scene.SetLayerParent(childLayer, parentFolder),
            "Reparenting a layer under a folder failed.");
        var childObject = scene.AddObject(
            childLayer,
            new PointF(0, 200),
            new SizeF(60, 60),
            0,
            0,
            Color.Violet,
            4,
            ShapeKind.Ellipse);
        AssertTimeline(
            DrawingObjectSvgExport.VisibleObjectsInPaintOrder(scene, 0).Contains(childObject),
            "Visible child layer was excluded from the export range.");

        AssertTimeline(
            scene.SetLayerVisible(parentFolder, false),
            "Hiding the parent folder had no effect.");
        AssertTimeline(
            !DrawingObjectSvgExport.VisibleObjectsInPaintOrder(scene, 0).Contains(childObject),
            "Symbol SVG export did not exclude a layer whose parent folder is hidden.");
        AssertTimeline(
            scene.SetLayerVisible(parentFolder, true),
            "Restoring the parent folder had no effect.");

        // Folder layers never contribute geometry, so no emitted group may describe a
        // folder layer even though the folder itself is visible.
        AssertTimeline(
            !DrawingObjectSvgExport.VisibleObjectsInPaintOrder(scene, 0)
                .Any(index => scene.GetLayerKind(scene.ObjectLayer[index]) == DrawingLayerKind.Folder),
            "Symbol SVG export treated a folder layer as renderable content.");

        // Mask layers clip their content layer and must not be emitted as content.
        // AddMaskLayer inserts the mask immediately before the content layer, so
        // resolve both layer indices after the insertion instead of assuming them.
        var maskScene = new VectorScene();
        maskScene.CreateEmpty(1, 8);
        var maskedObject = maskScene.AddObject(
            0,
            new PointF(0, 0),
            new SizeF(80, 60),
            0,
            0,
            Color.Coral,
            4,
            ShapeKind.Rectangle);
        var maskLayer = maskScene.AddMaskLayer("Mask");
        var contentLayer = maskScene.ObjectLayer[maskedObject];
        var maskObject = maskScene.AddObject(
            maskLayer,
            new PointF(0, 0),
            new SizeF(100, 100),
            0,
            0,
            Color.White,
            4,
            ShapeKind.Rectangle);
        AssertTimeline(
            maskScene.GetLayerKind(maskLayer) == DrawingLayerKind.Mask
            && maskScene.GetLayerKind(contentLayer) == DrawingLayerKind.Drawing
            && maskLayer != contentLayer,
            "The mask regression scene did not build a mask over a drawing layer.");

        var maskedOrder = DrawingObjectSvgExport.VisibleObjectsInPaintOrder(maskScene, 0);
        AssertTimeline(
            maskedOrder.Contains(maskedObject) && !maskedOrder.Contains(maskObject),
            "Symbol SVG export did not keep masked content while excluding the mask layer itself.");

        var maskedDocument = DrawingObjectSvgExport.CreateDocument("mask-regression", "Mask", maskScene, 0);
        AssertTimeline(
            maskedDocument.Descendants().Any(element => element.Name.LocalName == "mask")
            && maskedDocument.Descendants().Any(element =>
                element.Attribute("mask")?.Value.Contains("url(#v2d-mask-", StringComparison.Ordinal) == true),
            "Symbol SVG export did not emit an SVG mask for a masked content layer.");

        // A stroke-only mask source must stay unfilled: forcing white on an open path
        // would clip to its interior instead of its visible stroke coverage.
        var lineMaskScene = new VectorScene();
        lineMaskScene.CreateEmpty(1, 8);
        lineMaskScene.AddObject(
            0,
            new PointF(0, 0),
            new SizeF(80, 20),
            0,
            6,
            Color.Coral,
            Color.Coral,
            4,
            ShapeKind.Line);
        var lineMaskLayer = lineMaskScene.AddMaskLayer("Mask");
        lineMaskScene.AddObject(
            lineMaskLayer,
            new PointF(0, 0),
            new SizeF(90, 30),
            0,
            8,
            Color.White,
            Color.White,
            4,
            ShapeKind.Line);
        var lineMaskDocument = DrawingObjectSvgExport.CreateDocument("line-mask-regression", "Line", lineMaskScene, 0);
        var maskedStrokes = lineMaskDocument
            .Descendants()
            .Where(element => element.Parent?.Name.LocalName == "g"
                && element.Parent.Attribute("fill")?.Value == "#FFFFFF"
                && element.Attribute("stroke") is not null)
            .ToArray();
        AssertTimeline(
            maskedStrokes.Length > 0
            && maskedStrokes.All(element => element.Attribute("fill")?.Value == "none")
            && maskedStrokes.All(element => element.Attribute("stroke")?.Value == "#FFFFFF"),
            "Symbol SVG export filled a stroke-only mask source instead of preserving its stroke coverage.");

        var temporaryRoot = CreateTemporaryDirectory("symbol-svg-export");
        try
        {
            var svgPath = Path.Combine(temporaryRoot, "Symbol.svg");
            DrawingObjectSvgExport.Write(svgPath, "export-regression", "Export", scene, 0);
            AssertTimeline(File.Exists(svgPath), "Symbol SVG export did not create a file.");

            // The written document must be well-formed XML with the expected root.
            var reloaded = XDocument.Load(svgPath);
            AssertTimeline(
                reloaded.Root?.Name.LocalName == "svg"
                && string.Equals(
                    (string?)reloaded.Root.Attribute("data-v2d-drawing-object-id"),
                    "export-regression",
                    StringComparison.Ordinal),
                "Symbol SVG export wrote an unexpected document root.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }
    private static void RunDrawingObjectSvgViewportRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(3, 8);
        scene.AddObject(0, new PointF(-500, -300), new SizeF(200, 100),
            0, 20, Color.Teal, Color.White, 4, ShapeKind.Rectangle);
        scene.AddObject(1, new PointF(20000, 10000), new SizeF(800, 600),
            0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        scene.SetLayerVisible(1, false);
        scene.AddObject(2, new PointF(-20000, -10000), new SizeF(800, 600),
            0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        scene.LayerOpacity[2] = 0;
        var document = DrawingObjectSvgExport.CreateDocument("viewport-regression", "Viewport", scene, 0);
        AssertSvgExportViewport(document, new RectangleF(-610, -360, 220, 120),
            "Exported bounds must fit the visible negative-coordinate shape including its stroke.");

        var visibleLayerId = scene.LayerIds[0];
        // AddFolderLayer wraps the active drawing layer. Wrap the invisible
        // fixture, not the visible rectangle whose bounds we are checking.
        scene.ActiveLayer = 2;
        var parent = scene.AddFolderLayer("Hidden folder");
        var child = scene.AddLayer("Hidden child");
        scene.SetLayerParent(child, parent);
        scene.AddObject(child, new PointF(12000, -12000), new SizeF(500, 500),
            0, 0, Color.Gold, 4, ShapeKind.Rectangle);
        scene.SetLayerVisible(parent, false);
        AssertSvgExportViewport(DrawingObjectSvgExport.CreateDocument("hidden-parent", "Hidden", scene, 0),
            new RectangleF(-610, -360, 220, 120),
            "A hidden ancestor must not enlarge the SVG viewport.");

        // Keep a viewport that fits the image, and preserve vu/px units through the
        // same loader and placement calculation used by Import SVG in the workbench.
        var temporaryRoot = CreateTemporaryDirectory("symbol-svg-viewport");
        try
        {
            var file = Path.Combine(temporaryRoot, "Viewport.svg");
            document.Save(file);
            var imported = ImportedSvgRasterizer.Load(file);
            AssertTimeline(Math.Abs(imported.IntrinsicSize.Width * VectorUnits.UnitsPerPixel - 220) < 0.01f
                && Math.Abs(imported.IntrinsicSize.Height * VectorUnits.UnitsPerPixel - 120) < 0.01f,
                "SVG round trip changed the authored size or retained the full stage viewport.");
            var initialSize = (SizeF)typeof(MainForm).GetMethod("ImportedSvgInitialSize",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [imported.IntrinsicSize, new RectangleF(-10000, -10000, 20000, 20000)])!;
            AssertTimeline(Math.Abs(initialSize.Width - 220) < 0.01f
                && Math.Abs(initialSize.Height - 120) < 0.01f,
                "Import SVG placement must use the tight authored dimensions.");
            var raster = ImportedSvgRasterizer.Rasterize(imported.Source, 220, 120);
            var paintedPixels = 0;
            for (var pixel = 3; pixel < raster.Pixels.Length; pixel += 4)
            {
                if (raster.Pixels[pixel] > 0) paintedPixels++;
            }
            AssertTimeline(paintedPixels > raster.PixelWidth * raster.PixelHeight * 0.85f,
                "Reimported SVG content must fill its viewport instead of becoming a tiny central dot.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }

        var rotated = new VectorScene();
        rotated.CreateEmpty();
        rotated.AddObject(0, new PointF(300, -200), new SizeF(200, 100),
            MathF.PI / 2, 20, Color.Teal, Color.White, 4, ShapeKind.Rectangle);
        AssertSvgExportViewport(DrawingObjectSvgExport.CreateDocument("rotated", "Rotated", rotated, 0),
            new RectangleF(240, -310, 120, 220),
            "SVG viewport must include rotated geometry and its stroke.");

        var line = new VectorScene();
        line.CreateEmpty();
        line.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0),
            20, Color.Transparent, Color.White, 4);
        AssertSvgExportViewport(DrawingObjectSvgExport.CreateDocument("flat-line", "Line", line, 0),
            new RectangleF(-110, -10, 220, 20),
            "A horizontal stroke needs its full thickness and round end caps.");

        var masked = new VectorScene();
        masked.CreateEmpty();
        masked.AddObject(0, new PointF(0, 0), new SizeF(2000, 1000),
            0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        var maskLayer = masked.AddMaskLayer("Mask");
        masked.AddObject(maskLayer, new PointF(-100, 0), new SizeF(100, 60),
            0, 0, Color.White, 4, ShapeKind.Rectangle);
        var maskedDocument = DrawingObjectSvgExport.CreateDocument("masked", "Masked", masked, 0);
        AssertSvgExportViewport(maskedDocument,
            new RectangleF(-150, -30, 100, 60),
            "Masked-out content must not enlarge the SVG viewport.");
        var maskedRaster = ImportedSvgRasterizer.Rasterize(maskedDocument.ToString(), 200, 120);
        var maskPixels = 0;
        for (var pixel = 3; pixel < maskedRaster.Pixels.Length; pixel += 4)
        {
            if (maskedRaster.Pixels[pixel] > 0) maskPixels++;
        }
        AssertTimeline(maskPixels > maskedRaster.PixelWidth * maskedRaster.PixelHeight * 0.85f,
            "An offset mask must remain visible after the SVG canvas is tightened.");

        var empty = new VectorScene();
        empty.CreateEmpty();
        AssertSvgExportViewport(DrawingObjectSvgExport.CreateDocument("empty", "Empty", empty, 0),
            new RectangleF(0, 0, 1, 1), "An empty SVG needs a finite positive viewport.");
        scene.Timeline.InsertBlankKeyframe(scene.Timeline.Tracks.First(track =>
            track.TargetId == visibleLayerId).Id, 4);
        AssertSvgExportViewport(DrawingObjectSvgExport.CreateDocument("blank-frame", "Blank", scene, 4),
            new RectangleF(0, 0, 1, 1),
            "An inactive exposure must not contribute to SVG bounds.");
        Console.WriteLine("symbol_svg_viewport_roundtrip_passed=true");
    }

    private static void AssertSvgExportViewport(XDocument document, RectangleF expected, string message)
    {
        var root = document.Root!;
        var values = root.Attribute("viewBox")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        AssertTimeline(values.Length == 4 && values.All(float.IsFinite)
            && Math.Abs(values[0] - expected.X) < 0.02f
            && Math.Abs(values[1] - expected.Y) < 0.02f
            && Math.Abs(values[2] - expected.Width) < 0.02f
            && Math.Abs(values[3] - expected.Height) < 0.02f,
            $"{message} Actual viewBox: {root.Attribute("viewBox")!.Value}");
        var width = float.Parse(root.Attribute("width")!.Value, CultureInfo.InvariantCulture);
        var height = float.Parse(root.Attribute("height")!.Value, CultureInfo.InvariantCulture);
        AssertTimeline(width > 0 && height > 0 && float.IsFinite(width) && float.IsFinite(height)
            && Math.Abs(width * VectorUnits.UnitsPerPixel - values[2]) < 0.02f
            && Math.Abs(height * VectorUnits.UnitsPerPixel - values[3]) < 0.02f,
            "SVG dimensions must explicitly convert vector units to pixels.");
    }
}
