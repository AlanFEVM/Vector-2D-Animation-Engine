using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneCanvasRenderingRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(frameCount: 1);
        var maskLayer = scene.AddMaskLayer("Empty canvas mask");
        AssertTimeline(
            scene.GetLayerKind(maskLayer) == DrawingLayerKind.Mask
            && scene.TryGetMaskLayerIndex(maskLayer + 1, out var assignedMask)
            && assignedMask == maskLayer
            && scene.ObjectCount == 0,
            "Canvas rendering regression could not prepare an empty masked scene.");
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        var geometryBefore = JsonSerializer.Serialize(scene.CreateSnapshot(), jsonOptions);
        var stageWidth = scene.StageWidth;
        var stageHeight = scene.StageHeight;
        using var stage = new StageControl(scene) { ClientSize = new Size(256, 192), WorldGridOpacity = 0 };
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 1 });
        var sentinel = Color.Magenta;
        var settings = new SceneCanvasSettings { Width = 128, Height = 96, ColorArgb = Color.Red.ToArgb() };
        var none = new SceneCanvasPresentation(settings, null, SizeF.Empty, true);

        Bitmap Render(SceneCanvasPresentation? presentation)
        {
            stage.SetCanvasPresentation(presentation);
            var bitmap = new Bitmap(stage.Width, stage.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(sentinel);
            stage.DrawCanvasBackgroundGdi(graphics);
            return bitmap;
        }

        void AssertPixel(Bitmap bitmap, int screenX, int screenY, Color expected, string context)
        {
            var actual = bitmap.GetPixel(screenX, screenY);
            AssertTimeline(
                Math.Abs(actual.A - expected.A) <= 2
                && Math.Abs(actual.R - expected.R) <= 2
                && Math.Abs(actual.G - expected.G) <= 2
                && Math.Abs(actual.B - expected.B) <= 2,
                $"{context}: pixel ({screenX}, {screenY}) was {actual}, expected {expected}.");
        }

        void AssertUntouched(Bitmap bitmap, string context)
        {
            for (var screenY = 0; screenY < bitmap.Height; screenY++)
                for (var screenX = 0; screenX < bitmap.Width; screenX++)
                    AssertTimeline(bitmap.GetPixel(screenX, screenY).ToArgb() == sentinel.ToArgb(), context);
        }

        static Color Blend(Color foreground, Color background)
        {
            var alpha = foreground.A;
            return Color.FromArgb(
                255,
                (foreground.R * alpha + background.R * (255 - alpha) + 127) / 255,
                (foreground.G * alpha + background.G * (255 - alpha) + 127) / 255,
                (foreground.B * alpha + background.B * (255 - alpha) + 127) / 255);
        }

        using (var legacy = Render(null))
        {
            AssertUntouched(legacy, "An unset legacy canvas painted pixels into the background.");
            AssertTimeline(
                stage.CanvasScreenBounds() == RectangleF.Empty
                && stage.CanvasWorldWidth == stageWidth
                && stage.CanvasWorldHeight == stageHeight,
                "An unset legacy canvas changed its bounds or fallback world dimensions.");
        }
        using var checker = Render(none);
        AssertTimeline(
            stage.CanvasScreenBounds() == new RectangleF(64, 48, 128, 96)
            && stage.CanvasWorldWidth == VectorUnits.FromPixels(128)
            && stage.CanvasWorldHeight == VectorUnits.FromPixels(96),
            "Canvas presentation did not resolve its centered screen bounds and pixel-to-world dimensions.");
        AssertPixel(checker, 80, 80, Color.FromArgb(205, 208, 212), "None checkerboard light tile");
        AssertPixel(checker, 88, 80, Color.FromArgb(175, 178, 182), "None checkerboard dark tile");
        AssertPixel(checker, 32, 32, sentinel, "Canvas clipping outside bounds");
        AssertPixel(checker, 200, 152, sentinel, "Canvas clipping below and right of bounds");

        var solidColor = Color.FromArgb(128, 224, 32, 48);
        var solid = none with { Settings = settings with { BackgroundKind = SceneBackgroundKind.Solid, ColorArgb = solidColor.ToArgb() } };
        using (var translucent = Render(solid))
        {
            AssertPixel(translucent, 80, 80, Blend(solidColor, checker.GetPixel(80, 80)), "Solid alpha over light checker");
            AssertPixel(translucent, 88, 80, Blend(solidColor, checker.GetPixel(88, 80)), "Solid alpha over dark checker");
            AssertPixel(translucent, 32, 32, sentinel, "Solid alpha outside canvas");
        }

        var red = Color.FromArgb(224, 32, 48);
        var blue = Color.FromArgb(32, 64, 224);
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("scene-canvas-render-regression", 32, 16));
        for (var imageY = 0; imageY < raster.PixelHeight; imageY++)
            for (var imageX = 0; imageX < raster.PixelWidth; imageX++)
            {
                var color = imageX < raster.PixelWidth / 2 ? red : blue;
                var offset = imageY * raster.Stride + imageX * 4;
                raster.Pixels[offset] = color.B;
                raster.Pixels[offset + 1] = color.G;
                raster.Pixels[offset + 2] = color.R;
                raster.Pixels[offset + 3] = 255;
            }
        var imageSettings = settings with
        {
            BackgroundKind = SceneBackgroundKind.Image,
            ColorArgb = Color.Transparent.ToArgb(),
            ImageAssetId = "render-image",
            ImageFit = CanvasImageFit.Contain
        };
        var image = new SceneCanvasPresentation(imageSettings, raster, new SizeF(32, 16), true);
        using (var contained = Render(image))
        {
            AssertPixel(contained, 80, 96, red, "Contained image left stripe");
            AssertPixel(contained, 176, 96, blue, "Contained image right stripe");
            AssertPixel(contained, 80, 54, checker.GetPixel(80, 54), "Contained image top letterbox");
            AssertPixel(contained, 80, 138, checker.GetPixel(80, 138), "Contained image bottom letterbox");
        }
        var cover = image with { Settings = imageSettings with { ImageFit = CanvasImageFit.Cover } };
        using (var covered = Render(cover))
        {
            AssertPixel(covered, 80, 54, red, "Covered image fills top letterbox");
            AssertPixel(covered, 176, 138, blue, "Covered image fills bottom letterbox");
            AssertPixel(covered, 48, 96, sentinel, "Covered image clips left overflow");
            AssertPixel(covered, 208, 96, sentinel, "Covered image clips right overflow");
        }
        var original = image with { Settings = imageSettings with { ImageFit = CanvasImageFit.Original } };
        using (var natural = Render(original))
        {
            AssertPixel(natural, 116, 92, red, "Original image left stripe");
            AssertPixel(natural, 140, 92, blue, "Original image right stripe");
            AssertPixel(natural, 80, 96, checker.GetPixel(80, 96), "Original image retains natural size");
        }
        var shifted = original with { Settings = original.Settings with { ImageOffsetX = 16, ImageOffsetY = -8 } };
        using (var offsetImage = Render(shifted))
        {
            AssertPixel(offsetImage, 132, 84, red, "Offset image left stripe");
            AssertPixel(offsetImage, 156, 84, blue, "Offset image right stripe");
            AssertPixel(offsetImage, 116, 92, checker.GetPixel(116, 92), "Offset image vacates original position");
        }
        var scaled = shifted with { Settings = shifted.Settings with { ImageScale = 2 } };
        using (var scaledImage = Render(scaled))
        {
            AssertTimeline(
                SceneCanvasLayout.ResolveImageBounds(scaled.Settings, stage.CanvasScreenBounds(), scaled.ImagePixelSize)
                    == new RectangleF(112, 72, 64, 32),
                "Image scaling changed its aspect ratio or multiplied its pixel offsets.");
            AssertPixel(scaledImage, 120, 80, red, "Uniformly scaled image left stripe");
            AssertPixel(scaledImage, 168, 80, blue, "Uniformly scaled image right stripe");
            AssertPixel(scaledImage, 120, 112, checker.GetPixel(120, 112), "Uniformly scaled image bottom extent");
        }
        using (var transparentImage = Render(cover with { Settings = cover.Settings with { ImageOpacity = 0 } }))
            AssertPixel(transparentImage, 80, 80, checker.GetPixel(80, 80), "Zero image opacity preserves checkerboard");
        using (var translucentImage = Render(cover with { Settings = cover.Settings with { ImageOpacity = 0.5f } }))
            AssertPixel(translucentImage, 80, 80, Blend(Color.FromArgb(128, red), checker.GetPixel(80, 80)), "Half image opacity over checkerboard");

        stage.SetSelection([0], primaryObject: 0);
        using (var unselectableBackground = Render(cover))
        {
            AssertTimeline(
                stage.SelectedObjects.Count == 0
                && stage.SelectedObject == -1
                && scene.ObjectCount == 0
                && scene.HitTest(PointF.Empty, 0) == -1
                && !scene.HitTestElement(PointF.Empty, 0).IsValid
                && !scene.HasSelectableObjectAt(PointF.Empty, 0),
                "Canvas presentation added selectable geometry or entered empty-mask hit testing.");
            AssertPixel(unselectableBackground, 80, 80, red, "Empty mask and rejected selection do not clip the background");
        }

        stage.SetCanvasPresentation(none with { Settings = settings with { Width = 256, Height = 192 } });
        AssertTimeline(
            stage.CanvasWorldWidth == VectorUnits.FromPixels(256)
            && stage.CanvasWorldHeight == VectorUnits.FromPixels(192)
            && stage.CanvasScreenBounds() == new RectangleF(0, 0, 256, 192),
            "Changing presentation dimensions did not update canvas bounds independently of scene geometry.");
        stage.SetCanvasPresentation(none);
        stage.Pan(20, -10);
        stage.FitCanvas();
        var fittedBounds = stage.CanvasScreenBounds();
        AssertTimeline(
            stage.CameraX == 0
            && stage.CameraY == 0
            && Math.Abs(stage.Zoom - 1.7f) < 0.001f
            && Math.Abs(fittedBounds.Width - 217.6f) < 0.01f
            && Math.Abs(fittedBounds.Height - 163.2f) < 0.01f
            && Math.Abs(fittedBounds.Left + fittedBounds.Width * 0.5f - stage.Width * 0.5f) < 0.01f
            && Math.Abs(fittedBounds.Top + fittedBounds.Height * 0.5f - stage.Height * 0.5f) < 0.01f,
            "FitCanvas did not center and fit the configured canvas with the expected margin.");

        stage.SetCanvasPresentation(scaled);
        var state = stage.CaptureReference3DPlaybackPreparationState();
        AssertTimeline(
            ReferenceEquals(state.CanvasPresentation, scaled),
            "Playback preparation capture dropped or replaced the immutable canvas presentation.");
        using (var preparationStage = new StageControl(new VectorScene()) { ClientSize = stage.ClientSize })
        {
            preparationStage.ConfigureReference3DPlaybackPreparation(scene, SceneCompositionResult.Empty, 0, state);
            AssertTimeline(
                ReferenceEquals(preparationStage.CanvasPresentation, scaled)
                && preparationStage.CanvasWorldWidth == stage.CanvasWorldWidth
                && preparationStage.CanvasWorldHeight == stage.CanvasWorldHeight,
                "Playback preparation configuration lost the canvas image or world dimensions.");
            preparationStage.ConfigureReference3DPlaybackPreparation(
                scene, SceneCompositionResult.Empty, 0, state with { CanvasPresentation = null });
            AssertTimeline(
                preparationStage.CanvasPresentation is null
                && preparationStage.CanvasScreenBounds() == RectangleF.Empty
                && preparationStage.CanvasWorldWidth == scene.StageWidth
                && preparationStage.CanvasWorldHeight == scene.StageHeight,
                "Legacy playback preparation retained a stale canvas presentation.");
        }
        using (var cleared = Render(null))
            AssertUntouched(cleared, "Clearing canvas presentation left stale background pixels.");
        AssertTimeline(
            state.CanvasPresentation == scaled
            && scene.StageWidth == stageWidth
            && scene.StageHeight == stageHeight
            && scene.ObjectCount == 0
            && JsonSerializer.Serialize(scene.CreateSnapshot(), jsonOptions) == geometryBefore,
            "Canvas presentation, drawing, fitting, selection, or playback preparation mutated world geometry.");
        Console.WriteLine("scene_canvas_rendering_regression=ok");
    }

    private static void RunSceneCanvasModelRegression()
    {
        RunEnterPlaybackViewSettingsRegression();
        var defaults = new SceneCanvasSettings();
        var validationScene = new SceneDefinition();
        AssertTimeline(
            validationScene.Canvas is null
            && !validationScene.CanvasWelcomeDismissed
            && !validationScene.TrySetCanvas(null)
            && defaults.IsValid
            && defaults.Width == 1920
            && defaults.Height == 1080
            && defaults.BackgroundKind == SceneBackgroundKind.None
            && defaults.ColorArgb == Color.White.ToArgb()
            && defaults.ImageAssetId == ""
            && defaults.ImageFit == CanvasImageFit.Contain
            && defaults.ImageScale == 1f
            && defaults.ImageOffsetX == 0f
            && defaults.ImageOffsetY == 0f
            && defaults.ImageOpacity == 1f
            && defaults.DimOutside,
            "A new scene or canvas settings did not retain the no-canvas defaults.");

        SceneCanvasSettings[] validSettings =
        [
            defaults,
            defaults with { Width = 1, Height = 32768, ImageScale = 0.001f, ImageOpacity = 0f },
            defaults with { Width = 32768, Height = 1, ImageScale = 100f, ImageOpacity = 1f },
            defaults with { Width = 32768, Height = 32768, ImageOffsetX = -5_000_000f, ImageOffsetY = 5_000_000f },
            defaults with { ImageOffsetX = 5_000_000f, ImageOffsetY = -5_000_000f },
            defaults with { Width = 1, Height = 1, BackgroundKind = SceneBackgroundKind.Solid, ImageFit = CanvasImageFit.Cover },
            defaults with { BackgroundKind = SceneBackgroundKind.Image, ImageAssetId = "image", ImageFit = CanvasImageFit.Original }
        ];
        foreach (var settings in validSettings)
        {
            AssertTimeline(
                settings.IsValid && validationScene.TrySetCanvas(settings) && validationScene.Canvas == settings,
                "A scene rejected valid canvas dimensions, enum values, or inclusive numeric bounds.");
        }

        validationScene.RestoreCanvas(defaults, welcomeDismissed: true);
        SceneCanvasSettings[] invalidSettings =
        [
            defaults with { Width = 0 },
            defaults with { Width = -1 },
            defaults with { Width = 32769 },
            defaults with { Height = 0 },
            defaults with { Height = -1 },
            defaults with { Height = 32769 },
            defaults with { BackgroundKind = (SceneBackgroundKind)(-1) },
            defaults with { BackgroundKind = (SceneBackgroundKind)3 },
            defaults with { ImageFit = (CanvasImageFit)(-1) },
            defaults with { ImageFit = (CanvasImageFit)3 },
            defaults with { ImageScale = float.NaN },
            defaults with { ImageScale = float.PositiveInfinity },
            defaults with { ImageScale = float.NegativeInfinity },
            defaults with { ImageScale = 0f },
            defaults with { ImageScale = -1f },
            defaults with { ImageScale = 0.0009f },
            defaults with { ImageScale = 100.01f },
            defaults with { ImageOffsetX = float.NaN },
            defaults with { ImageOffsetX = float.PositiveInfinity },
            defaults with { ImageOffsetX = float.NegativeInfinity },
            defaults with { ImageOffsetX = MathF.BitDecrement(-5_000_000f) },
            defaults with { ImageOffsetX = MathF.BitIncrement(5_000_000f) },
            defaults with { ImageOffsetX = float.MinValue },
            defaults with { ImageOffsetX = float.MaxValue },
            defaults with { ImageOffsetY = float.NaN },
            defaults with { ImageOffsetY = float.PositiveInfinity },
            defaults with { ImageOffsetY = float.NegativeInfinity },
            defaults with { ImageOffsetY = MathF.BitDecrement(-5_000_000f) },
            defaults with { ImageOffsetY = MathF.BitIncrement(5_000_000f) },
            defaults with { ImageOffsetY = float.MinValue },
            defaults with { ImageOffsetY = float.MaxValue },
            defaults with { ImageOpacity = float.NaN },
            defaults with { ImageOpacity = float.PositiveInfinity },
            defaults with { ImageOpacity = float.NegativeInfinity },
            defaults with { ImageOpacity = -0.01f },
            defaults with { ImageOpacity = 1.01f },
            defaults with { ImageAssetId = null! },
            defaults with { BackgroundKind = SceneBackgroundKind.Image },
            defaults with { BackgroundKind = SceneBackgroundKind.Image, ImageAssetId = " " }
        ];
        foreach (var settings in invalidSettings)
        {
            AssertTimeline(
                !settings.IsValid && !validationScene.TrySetCanvas(settings),
                "A scene accepted invalid canvas dimensions, enum values, image IDs, or out-of-range numeric settings.");
            ExpectInvalidData(
                () => validationScene.RestoreCanvas(settings, welcomeDismissed: false),
                "Canvas restore accepted invalid settings instead of throwing InvalidDataException.");
            AssertTimeline(
                validationScene.Canvas == defaults && validationScene.CanvasWelcomeDismissed,
                "Rejecting invalid canvas settings partially changed the canvas or welcome state.");
        }

        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        var foreignProject = VectorProject.CreateEmpty();
        var missingId = Guid.NewGuid().ToString("N");
        var changed = 0;
        var foreignChanged = 0;
        project.Changed += (_, _) => changed++;
        foreignProject.Changed += (_, _) => foreignChanged++;
        AssertTimeline(
            scene.Canvas is null
            && !scene.CanvasWelcomeDismissed
            && !project.TrySetSceneCanvas(scene.Id, null)
            && !project.TrySetSceneCanvas(foreignProject.Scenes[0].Id, defaults)
            && !foreignProject.TrySetSceneCanvas(scene.Id, defaults)
            && !project.TrySetSceneCanvas(missingId, defaults)
            && !project.TrySetSceneCanvas(scene.Id, defaults with
            {
                BackgroundKind = SceneBackgroundKind.Image,
                ImageAssetId = missingId
            })
            && !project.TrySetSceneCanvas(scene.Id, defaults with { ImageAssetId = missingId })
            && changed == 0
            && foreignChanged == 0
            && scene.Canvas is null
            && foreignProject.Scenes[0].Canvas is null,
            "Canvas mutation accepted a foreign scene or missing image reference, or notified on rejection.");
        foreach (var settings in invalidSettings)
        {
            AssertTimeline(
                !project.TrySetSceneCanvas(scene.Id, settings) && changed == 0 && scene.Canvas is null,
                "The project accepted invalid canvas settings or notified on rejection.");
        }

        var solid = defaults with
        {
            Width = 2048,
            Height = 1536,
            BackgroundKind = SceneBackgroundKind.Solid,
            ColorArgb = Color.FromArgb(137, 32, 64, 96).ToArgb(),
            ImageFit = CanvasImageFit.Original,
            ImageScale = 1.25f,
            ImageOffsetX = -12.5f,
            ImageOffsetY = 33.75f,
            ImageOpacity = 0.65f,
            DimOutside = false
        };
        AssertTimeline(
            project.TrySetSceneCanvas(scene.Id, solid)
            && changed == 1
            && scene.Canvas == solid
            && !project.TrySetSceneCanvas(scene.Id, solid with { })
            && !scene.TrySetCanvas(solid with { })
            && changed == 1,
            "A real canvas change did not notify exactly once, or an equal immutable record notified again.");
        AssertTimeline(
            project.TrySetSceneCanvas(scene.Id, null)
            && scene.Canvas is null
            && changed == 2
            && !project.TrySetSceneCanvas(scene.Id, null)
            && changed == 2
            && project.TrySetSceneCanvas(scene.Id, solid)
            && changed == 3,
            "Clearing or restoring a canvas did not preserve no-op notification semantics.");

        RunSceneCanvasResizeRegression(project, scene, solid);
        scene.DismissCanvasWelcome();
        scene.DismissCanvasWelcome();
        var dismissedScene = project.AddScene("Dismissed without canvas");
        dismissedScene.DismissCanvasWelcome();
        var untouchedScene = project.AddScene("Untouched canvas");
        var snapshot = project.CreateRestartSnapshot();
        var restored = VectorProject.RestoreRestartSnapshot(EditorRestartStore.RoundTripProjectSnapshot(snapshot));
        AssertTimeline(
            restored.Scenes.Single(item => item.Id == scene.Id).Canvas == scene.Canvas
            && restored.Scenes.Single(item => item.Id == scene.Id).CanvasWelcomeDismissed
            && restored.Scenes.Single(item => item.Id == dismissedScene.Id).Canvas is null
            && restored.Scenes.Single(item => item.Id == dismissedScene.Id).CanvasWelcomeDismissed
            && restored.Scenes.Single(item => item.Id == untouchedScene.Id).Canvas is null
            && !restored.Scenes.Single(item => item.Id == untouchedScene.Id).CanvasWelcomeDismissed,
            "Restart JSON lost canvas values or conflated missing canvases with dismissed welcome state.");
        var snapshotCanvas = snapshot.Scenes.Single(item => item.Id == scene.Id).Canvas;
        AssertTimeline(
            project.TrySetSceneCanvas(scene.Id, solid)
            && snapshotCanvas != solid
            && snapshot.Scenes.Single(item => item.Id == scene.Id).Canvas == snapshotCanvas
            && restored.Scenes.Single(item => item.Id == scene.Id).Canvas == snapshotCanvas,
            "Later canvas edits mutated the saved undo value, restart snapshot, or restored project.");

        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        var snapshotJson = JsonSerializer.SerializeToNode(snapshot, jsonOptions)!.AsObject();
        var invalidSnapshotJson = snapshotJson.DeepClone().AsObject();
        invalidSnapshotJson["Scenes"]!.AsArray()[0]!["Canvas"]!["Width"] = 0;
        ExpectInvalidData(
            () => VectorProject.RestoreRestartSnapshot(
                JsonSerializer.Deserialize<ProjectRestartSnapshot>(invalidSnapshotJson.ToJsonString(), jsonOptions)!),
            "Project restart accepted an invalid canvas width.");
        var missingImageJson = snapshotJson.DeepClone().AsObject();
        missingImageJson["Scenes"]!.AsArray()[0]!["Canvas"]!["BackgroundKind"] = (int)SceneBackgroundKind.Image;
        missingImageJson["Scenes"]!.AsArray()[0]!["Canvas"]!["ImageAssetId"] = missingId;
        ExpectInvalidData(
            () => VectorProject.RestoreRestartSnapshot(
                JsonSerializer.Deserialize<ProjectRestartSnapshot>(missingImageJson.ToJsonString(), jsonOptions)!),
            "Project restart accepted a missing canvas image reference.");
        foreach (var sceneNode in snapshotJson["Scenes"]!.AsArray())
        {
            var sceneJson = sceneNode!.AsObject();
            AssertTimeline(
                sceneJson.Remove("Canvas") && sceneJson.Remove("CanvasWelcomeDismissed"),
                "The legacy restart fixture did not remove the canvas fields.");
        }
        var legacy = VectorProject.RestoreRestartSnapshot(
            JsonSerializer.Deserialize<ProjectRestartSnapshot>(snapshotJson.ToJsonString(), jsonOptions)!);
        AssertTimeline(
            legacy.Scenes.All(item => item.Canvas is null && !item.CanvasWelcomeDismissed),
            "A legacy restart without canvas fields acquired a canvas or dismissed welcome state.");

        RunSceneCanvasLayoutRegression();
        RunSceneCanvasPersistenceRegression();
        Console.WriteLine("scene_canvas_model_regression=ok");
    }

    private static void RunSceneCanvasLayoutRegression()
    {
        var settings = new SceneCanvasSettings
        {
            Width = 800,
            Height = 600,
            BackgroundKind = SceneBackgroundKind.Image,
            ImageAssetId = "layout-image"
        };
        var canvas = new RectangleF(10, 20, 400, 300);
        var wideImage = new SizeF(200, 100);
        var tallImage = new SizeF(100, 200);
        (CanvasImageFit Fit, RectangleF WideBounds, RectangleF TallBounds)[] fixtures =
        [
            (CanvasImageFit.Contain, new RectangleF(10, 70, 400, 200), new RectangleF(135, 20, 150, 300)),
            (CanvasImageFit.Cover, new RectangleF(-90, 20, 600, 300), new RectangleF(10, -230, 400, 800)),
            (CanvasImageFit.Original, new RectangleF(160, 145, 100, 50), new RectangleF(185, 120, 50, 100))
        ];
        foreach (var fixture in fixtures)
        {
            var fittedSettings = settings with { ImageFit = fixture.Fit };
            AssertTimeline(
                SceneCanvasLayout.ResolveImageBounds(fittedSettings, canvas, wideImage) == fixture.WideBounds
                && SceneCanvasLayout.ResolveImageBounds(fittedSettings, canvas, tallImage) == fixture.TallBounds,
                $"Canvas {fixture.Fit} did not preserve the expected centered wide and tall image bounds.");
            foreach (var imageSize in new[] { wideImage, tallImage })
            {
                var fitted = SceneCanvasLayout.ResolveImageBounds(fittedSettings, canvas, imageSize);
                var offsetSettings = fittedSettings with { ImageOffsetX = 40, ImageOffsetY = -30 };
                var offset = SceneCanvasLayout.ResolveImageBounds(offsetSettings, canvas, imageSize);
                AssertTimeline(
                    offset == new RectangleF(fitted.X + 20, fitted.Y - 15, fitted.Width, fitted.Height),
                    $"Canvas {fixture.Fit} offsets did not use canvas pixels at the current screen scale.");
                var scaledSettings = offsetSettings with { ImageScale = 1.5f };
                var scaled = SceneCanvasLayout.ResolveImageBounds(scaledSettings, canvas, imageSize);
                AssertTimeline(
                    scaled == new RectangleF(
                        offset.X - fitted.Width * 0.25f,
                        offset.Y - fitted.Height * 0.25f,
                        fitted.Width * 1.5f,
                        fitted.Height * 1.5f)
                    && scaled.Width / imageSize.Width == scaled.Height / imageSize.Height,
                    $"Canvas {fixture.Fit} image scaling stretched the aspect ratio, moved the center, or scaled the offset.");
                var doubledCanvas = new RectangleF(20, 40, 800, 600);
                var zoomed = SceneCanvasLayout.ResolveImageBounds(scaledSettings, doubledCanvas, imageSize);
                AssertTimeline(
                    zoomed == new RectangleF(scaled.X * 2, scaled.Y * 2, scaled.Width * 2, scaled.Height * 2),
                    $"Canvas {fixture.Fit} screen zoom did not uniformly scale image bounds and pixel offsets.");
                var translated = SceneCanvasLayout.ResolveImageBounds(
                    scaledSettings, new RectangleF(110, -80, 400, 300), imageSize);
                AssertTimeline(
                    translated == new RectangleF(scaled.X + 100, scaled.Y - 100, scaled.Width, scaled.Height),
                    $"Canvas {fixture.Fit} ignored the canvas origin or changed image size during translation.");
            }
        }

        AssertTimeline(
            SceneCanvasLayout.ResolveImageBounds(settings, RectangleF.Empty, wideImage) == RectangleF.Empty
            && SceneCanvasLayout.ResolveImageBounds(settings, new RectangleF(0, 0, 400, 0), wideImage) == RectangleF.Empty
            && SceneCanvasLayout.ResolveImageBounds(settings, canvas, SizeF.Empty) == RectangleF.Empty
            && SceneCanvasLayout.ResolveImageBounds(settings, canvas, new SizeF(200, 0)) == RectangleF.Empty,
            "Canvas image layout returned drawable bounds for an empty canvas or natural image size.");
    }

    private static void RunSceneCanvasResizeRegression(
        VectorProject project,
        SceneDefinition scene,
        SceneCanvasSettings original)
    {
        var drawing = project.DrawingObjects[0];
        drawing.Scene.AddObject(0, new PointF(13, -27), new SizeF(120, 80), 17, 2, Color.SteelBlue, 8, ShapeKind.Rectangle);
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawing.Id, new PointF(31, 47), 19, out _),
            "Canvas resize regression could not prepare a scene instance.");
        scene.Camera.X = 123;
        scene.Camera.Y = -456;
        scene.Camera.Z = -2500;
        scene.Camera.Depth = 2500;
        scene.Camera.FieldOfViewDegrees = 53;
        var shot = scene.AddShot("Canvas resize camera", 24);
        AssertTimeline(
            scene.UpdateShotAtFrame(shot.Id, 0, SceneShotSettings.Default with { X = 150, Y = -200, FocalLength = 70 })
            && scene.UpdateShotAtFrame(shot.Id, 12, SceneShotSettings.Default with { X = 300, Y = 75, FocalLength = 35 }),
            "Canvas resize regression could not prepare shot framing keyframes.");
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        var geometryBefore = JsonSerializer.Serialize(drawing.Scene.CreateSnapshot(), jsonOptions);
        var cameraBefore = JsonSerializer.Serialize(scene.Camera, jsonOptions);
        var shotsBefore = JsonSerializer.Serialize(scene.CreateShotSnapshot(), jsonOptions);
        var sceneBefore = project.CreateRestartSnapshot().Scenes.Single(item => item.Id == scene.Id);
        var instancesBefore = JsonSerializer.Serialize(sceneBefore.Instances, jsonOptions);
        var timelineBefore = JsonSerializer.Serialize(sceneBefore.Timeline, jsonOptions);
        var stageWidth = drawing.Scene.StageWidth;
        var stageHeight = drawing.Scene.StageHeight;
        var resized = original with { Width = 1, Height = 32768 };
        AssertTimeline(
            project.TrySetSceneCanvas(scene.Id, resized)
            && scene.Canvas == resized
            && original.Width == 2048
            && original.Height == 1536
            && drawing.Scene.StageWidth == stageWidth
            && drawing.Scene.StageHeight == stageHeight
            && JsonSerializer.Serialize(drawing.Scene.CreateSnapshot(), jsonOptions) == geometryBefore
            && JsonSerializer.Serialize(scene.Camera, jsonOptions) == cameraBefore
            && JsonSerializer.Serialize(scene.CreateShotSnapshot(), jsonOptions) == shotsBefore,
            "Canvas resizing mutated immutable undo settings, internal stage dimensions, geometry, or camera framing.");
        var sceneAfter = project.CreateRestartSnapshot().Scenes.Single(item => item.Id == scene.Id);
        AssertTimeline(
            JsonSerializer.Serialize(sceneAfter.Instances, jsonOptions) == instancesBefore
            && JsonSerializer.Serialize(sceneAfter.Timeline, jsonOptions) == timelineBefore,
            "Canvas resizing changed scene instance transforms or shot timeline data.");
    }

    private static void RunSceneCanvasPersistenceRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("scene-canvas-regression");
        var projectRoot = Path.Combine(temporaryRoot, "Project");
        var manifestPath = Path.Combine(projectRoot, "SceneCanvas.v2dProject");
        var sourcePath = Path.Combine(temporaryRoot, "canvas-background.png");
        try
        {
            var project = VectorProject.CreateEmpty();
            var scene = project.Scenes[0];
            ProjectVaultStore.Save(project, manifestPath);
            var emptyRestored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                emptyRestored.Scenes[0].Canvas is null && !emptyRestored.Scenes[0].CanvasWelcomeDismissed,
                "Saving and reopening a new project created a canvas or dismissed its welcome state.");
            WriteFixturePng(sourcePath, 64, 48);
            AssertTimeline(
                project.TryAddImageAssetFromSource(
                    "Canvas background", sourcePath, BitmapImageRasterizer.ComputeSha256(sourcePath),
                    64, 48, 96f, BitmapImageImportSettings.Default, out var imported),
                "Canvas persistence regression could not import its PNG fixture.");
            var asset = imported ?? throw new InvalidOperationException("Canvas persistence regression lost its imported image.");
            var settings = new SceneCanvasSettings
            {
                Width = 2560,
                Height = 1440,
                BackgroundKind = SceneBackgroundKind.Image,
                ColorArgb = Color.FromArgb(173, 12, 34, 56).ToArgb(),
                ImageAssetId = asset.Id,
                ImageFit = CanvasImageFit.Cover,
                ImageScale = 1.75f,
                ImageOffsetX = -43.5f,
                ImageOffsetY = 81.25f,
                ImageOpacity = 0.45f,
                DimOutside = false
            };
            var secondScene = project.AddScene("Shared canvas image");
            var dismissedScene = project.AddScene("Dismissed canvas welcome");
            dismissedScene.DismissCanvasWelcome();
            scene.DismissCanvasWelcome();
            AssertTimeline(
                project.TrySetSceneCanvas(scene.Id, settings)
                && project.TrySetSceneCanvas(secondScene.Id, settings with { ImageFit = CanvasImageFit.Original })
                && !project.TryRemoveImageAsset(asset.Id, out var rejectedRemoval)
                && rejectedRemoval is null,
                "A canvas image reference could not be assigned or did not prevent asset deletion.");
            var restart = VectorProject.RestoreRestartSnapshot(
                EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
            AssertTimeline(
                restart.Scenes.Single(item => item.Id == scene.Id).Canvas == settings
                && restart.Scenes.Single(item => item.Id == scene.Id).CanvasWelcomeDismissed
                && !restart.TryRemoveImageAsset(asset.Id, out _),
                "Restart JSON lost a canvas image reference, welcome flag, or asset deletion protection.");
            ProjectVaultStore.Save(project, manifestPath);
            File.Delete(sourcePath);
            var managedPath = Path.Combine(projectRoot, ".Vault", "Images", $"{asset.Id}.png");
            var loaded = ProjectVaultStore.Load(manifestPath);
            var loadedScene = loaded.Scenes.Single(item => item.Id == scene.Id);
            var loadedAsset = loaded.ImageAssets.Single(item => item.Id == asset.Id);
            AssertTimeline(
                File.Exists(managedPath)
                && !File.Exists(sourcePath)
                && loadedScene.Canvas == settings
                && loadedScene.CanvasWelcomeDismissed
                && loadedAsset.SourcePath == Path.GetFullPath(managedPath)
                && BitmapImageRasterizer.ComputeSha256(managedPath) == asset.Sha256
                && loaded.Scenes.Single(item => item.Id == secondScene.Id).Canvas == (settings with { ImageFit = CanvasImageFit.Original })
                && !loaded.Scenes.Single(item => item.Id == secondScene.Id).CanvasWelcomeDismissed
                && loaded.Scenes.Single(item => item.Id == dismissedScene.Id).Canvas is null
                && loaded.Scenes.Single(item => item.Id == dismissedScene.Id).CanvasWelcomeDismissed,
                "Managed save/reopen lost canvas settings, shared image IDs, welcome flags, or the authoritative PNG copy.");
            var changed = 0;
            loaded.Changed += (_, _) => changed++;
            AssertTimeline(
                !loaded.TrySetSceneCanvas(scene.Id, settings with { })
                && !loaded.TryRemoveImageAsset(asset.Id, out var removed)
                && removed is null
                && changed == 0
                && loaded.TrySetSceneCanvas(scene.Id, null)
                && changed == 1
                && !loaded.TryRemoveImageAsset(asset.Id, out _)
                && changed == 1
                && loaded.TrySetSceneCanvas(secondScene.Id, null)
                && loaded.TryRemoveImageAsset(asset.Id, out removed)
                && removed?.Id == asset.Id
                && loaded.ImageAssets.Count == 0
                && loadedScene.CanvasWelcomeDismissed
                && changed == 3,
                "Shared canvas image deletion did not wait for all references to clear or notified on a rejected mutation.");

            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var entry = manifest["scenes"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == scene.Id)!.AsObject();
            var timelinePath = Path.Combine(projectRoot, entry["timelinePath"]!.GetValue<string>());
            var savedTimeline = JsonNode.Parse(ProjectPayloadCompression.Decompress(
                File.ReadAllBytes(timelinePath), 128 * 1024 * 1024))!.AsObject();
            AssertTimeline(
                manifest["formatVersion"]!.GetValue<int>() == ProjectVaultStore.CurrentManifestFormatVersion
                && savedTimeline["canvas"]?["imageAssetId"]?.GetValue<string>() == asset.Id
                && savedTimeline["canvasWelcomeDismissed"]?.GetValue<bool>() == true,
                "The managed scene timeline did not serialize the canvas and welcome state in the current project format.");

            void WriteCanvasTimelineFixture(JsonObject fixture)
            {
                var compressed = ProjectPayloadCompression.Compress(JsonSerializer.SerializeToUtf8Bytes(fixture));
                File.WriteAllBytes(timelinePath, compressed);
                entry["timelineSha256"] = Convert.ToHexString(SHA256.HashData(compressed)).ToLowerInvariant();
                File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var invalidTimeline = savedTimeline.DeepClone().AsObject();
            invalidTimeline["canvas"]!["width"] = 0;
            WriteCanvasTimelineFixture(invalidTimeline);
            ExpectInvalidData(
                () => ProjectVaultStore.Load(manifestPath),
                "Managed load accepted an invalid canvas width despite a matching timeline checksum.");
            var missingImageTimeline = savedTimeline.DeepClone().AsObject();
            missingImageTimeline["canvas"]!["imageAssetId"] = Guid.NewGuid().ToString("N");
            WriteCanvasTimelineFixture(missingImageTimeline);
            ExpectInvalidData(
                () => ProjectVaultStore.Load(manifestPath),
                "Managed load accepted a missing canvas image reference despite a matching timeline checksum.");
            var legacyTimeline = savedTimeline.DeepClone().AsObject();
            AssertTimeline(
                legacyTimeline.Remove("canvas") && legacyTimeline.Remove("canvasWelcomeDismissed"),
                "The legacy managed timeline fixture did not remove the canvas fields.");
            WriteCanvasTimelineFixture(legacyTimeline);
            var legacy = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                legacy.Scenes.Single(item => item.Id == scene.Id).Canvas is null
                && !legacy.Scenes.Single(item => item.Id == scene.Id).CanvasWelcomeDismissed
                && legacy.Scenes.Single(item => item.Id == secondScene.Id).Canvas?.ImageAssetId == asset.Id,
                "A legacy managed timeline without canvas fields acquired state or erased another scene's image reference.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }
}
