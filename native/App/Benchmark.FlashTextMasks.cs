using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void CheckFlashCompositionTextMaskCoverage()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        scene.LayerKinds[0] = DrawingLayerKind.Mask;
        scene.LayerLocked[0] = true;
        var text = scene.AddTextObject(0, PointF.Empty, new TextObjectData("O",
            TextGeometry.FallbackFontFamilyName, 36, TextFontStyle.Regular,
            TextHorizontalAlignment.Center, new SizeF(2000, 1800)), Color.White);
        scene.AddObject(1, PointF.Empty, new SizeF(5000, 5000), 0, 0, Color.Teal, 4, ShapeKind.Rectangle);
        var plan = new SceneCompositionMaskPlan(new() { [1] = new[] { 0 } },
            new() { [0] = new[] { 0 } }, new());
        using var stage = new StageControl(scene) { Size = new Size(256, 256) };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 2 });
        stage.SetSceneCompositionResult(new SceneCompositionResult { LayerMasks = plan }, scene, requestPaint: false);
        AssertTimeline(scene.TryGetTextWorldContours(text, out var contours) && contours.Length >= 2,
            "Text mask fixture did not produce a glyph and its counter.");
        using var expected = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in contours)
            if (contour.Length >= 3) expected.AddPolygon(contour.Select(point => stage.WorldToScreen(point.X, point.Y)).ToArray());
        using var actual = stage.CreateCompositionMaskObjectPath(scene, text);
        using var gdi = RenderGdiMaskObjects(stage);
        using var direct = RenderDirect2DObjects(stage);
        var bounds = Rectangle.Ceiling(expected.GetBounds());
        var inside = 0;
        var counter = 0;
        // Sample away from glyph edges so antialiasing does not decide coverage.
        for (var y = bounds.Top + 2; y < bounds.Bottom - 2; y += 2)
        for (var x = bounds.Left + 2; x < bounds.Right - 2; x += 2)
        {
            var visible = expected.IsVisible(x, y);
            if (Enumerable.Range(-3, 7).Any(dy => Enumerable.Range(-3, 7)
                .Any(dx => expected.IsVisible(x + dx, y + dy) != visible))) continue;
            AssertTimeline(actual.IsVisible(x, y) == visible
                && (gdi.GetPixel(x, y).A > 0) == visible
                && (direct.GetPixel(x, y).ToArgb() != Color.Fuchsia.ToArgb()) == visible,
                $"Text composition mask coverage differs: point={x},{y}, expected={visible}, "
                    + $"path={actual.IsVisible(x, y)}, gdi={gdi.GetPixel(x, y)}, direct={direct.GetPixel(x, y)}.");
            if (visible) inside++; else counter++;
        }
        AssertTimeline(inside > 0 && counter > 0, "Text mask test did not sample both glyph ink and empty coverage.");
        AssertTimeline(scene.TryGetTextObjectData(text, out var data)
            && scene.UpdateTextObjectData(text, data with { Content = " " }), "Could not blank the text mask glyph.");
        using var empty = stage.CreateCompositionMaskObjectPath(scene, text);
        AssertTimeline(empty.PointCount == 0, "Whitespace text masks fell back to their layout rectangle.");
        using var emptyGdi = RenderGdiMaskObjects(stage);
        using var emptyDirect = RenderDirect2DObjects(stage);
        AssertTimeline(emptyGdi.GetPixel(128, 128).A == 0
            && emptyDirect.GetPixel(128, 128).ToArgb() == Color.Fuchsia.ToArgb(),
            "Whitespace text masks exposed masked content.");
        Console.WriteLine("flash_stage_text_mask_glyph_counter_gdi_direct2d=ok");
    }
}
