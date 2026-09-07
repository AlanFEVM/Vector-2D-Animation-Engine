using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunColorHarmonyRegression()
    {
        RunLabColorRegression();
        RunTraditionalColorPickerRegression();

        var red = Color.FromArgb(255, 255, 0, 0);
        var complementary = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Complementary);
        var analogous = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Analogous);
        var triadic = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Triadic);
        var splitComplementary = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.SplitComplementary);
        var tetradic = HarmonyColorWheel.CreateHarmonyColors(red, ColorHarmonyMode.Tetradic);
        if (complementary.Length != 2
            || complementary[0].ToArgb() != red.ToArgb()
            || complementary[1].R > 2 || complementary[1].G < 253 || complementary[1].B < 253
            || analogous.Length != 3
            || triadic.Length != 3 || triadic[1].G < 253 || triadic[2].B < 253
            || splitComplementary.Length != 3
            || tetradic.Length != 4)
        {
            throw new InvalidOperationException("Color harmony rules did not generate the expected complementary, analogous, triadic, split-complementary, and tetradic palettes.");
        }

        var wheelBounds = new Rectangle(4, 4, 88, 88);
        var center = new Point(48, 48);
        if (!HarmonyColorWheel.TryResolvePointerHue(new Point(48, 5), wheelBounds, requireHotZone: true, out var topHue)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(66, 48), wheelBounds, requireHotZone: true, out _)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(95, 48), wheelBounds, requireHotZone: true, out _)
            || HarmonyColorWheel.TryResolvePointerHue(center, wheelBounds, requireHotZone: true, out _)
            || HarmonyColorWheel.TryResolvePointerHue(new Point(140, 48), wheelBounds, requireHotZone: true, out _)
            || !HarmonyColorWheel.TryResolvePointerHue(new Point(140, 48), wheelBounds, requireHotZone: false, out var capturedHue)
            || Math.Abs(topHue) > 2f
            || Math.Abs(capturedHue - 90f) > 2f)
        {
            throw new InvalidOperationException("Color harmony wheel pointer hot zones or captured drag hue resolution were invalid.");
        }

        const float wheelRadius = 44f;
        var peakRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 30f, 30f);
        var shoulderRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 42f, 30f);
        var edgeRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 48f, 30f);
        var wrappedRadius = HarmonyColorWheel.ResolvePointerSurfaceRadius(wheelRadius, 359f, 1f);
        if (HarmonyColorWheel.InteractionRefreshIntervalMilliseconds > 8
            || Math.Abs(peakRadius - 47f) > 0.001f
            || shoulderRadius <= wheelRadius || shoulderRadius >= peakRadius
            || Math.Abs(edgeRadius - wheelRadius) > 0.001f
            || wrappedRadius <= shoulderRadius)
        {
            throw new InvalidOperationException("Color harmony wheel pointer surface shader did not peak, decay, or wrap hue angles correctly.");
        }

        var sliderPeak = ColorComponentSlider.ResolvePointerSurfaceOffset(48f, 48f);
        var sliderShoulder = ColorComponentSlider.ResolvePointerSurfaceOffset(55f, 48f);
        var sliderEdge = ColorComponentSlider.ResolvePointerSurfaceOffset(62f, 48f);
        if (ColorComponentSlider.InteractionRefreshIntervalMilliseconds > 8
            || Math.Abs(sliderPeak - 3f) > 0.001f
            || sliderShoulder <= 0f || sliderShoulder >= sliderPeak
            || Math.Abs(sliderEdge) > 0.001f)
        {
            throw new InvalidOperationException("Color component slider pointer surface did not peak, decay, or refresh at interactive frequency.");
        }

        Console.WriteLine("color_harmony_regression=ok");
    }

    private static void RunLabColorRegression()
    {
        var references = new[]
        {
            (Color: Color.Black, Lightness: 0d, GreenRed: 0d, BlueYellow: 0d),
            (Color: Color.White, Lightness: 100d, GreenRed: 0d, BlueYellow: 0d),
            (Color: Color.Red, Lightness: 53.2408d, GreenRed: 80.0925d, BlueYellow: 67.2032d),
            (Color: Color.Lime, Lightness: 87.7347d, GreenRed: -86.1827d, BlueYellow: 83.1793d),
            (Color: Color.Blue, Lightness: 32.297d, GreenRed: 79.1875d, BlueYellow: -107.8602d)
        };
        foreach (var reference in references)
        {
            MaterialEditorPanel.RgbToLab(reference.Color, out var lightness, out var greenRed, out var blueYellow);
            if (Math.Abs(lightness - reference.Lightness) > 0.0005
                || Math.Abs(greenRed - reference.GreenRed) > 0.0005
                || Math.Abs(blueYellow - reference.BlueYellow) > 0.0005)
            {
                throw new InvalidOperationException(
                    $"sRGB to CIELAB D65 conversion drifted for {reference.Color}: " +
                    $"L*={lightness:F4}, a*={greenRed:F4}, b*={blueYellow:F4}.");
            }
        }

        var roundTripColors = new[]
        {
            Color.FromArgb(17, 12, 34, 56),
            Color.FromArgb(255, 79, 179, 162),
            Color.FromArgb(128, 238, 242, 241),
            Color.FromArgb(73, 255, 0, 255)
        };
        foreach (var source in roundTripColors)
        {
            AssertLabRoundTrip(source);
        }
        for (var red = 0; red <= 255; red += 17)
        for (var green = 0; green <= 255; green += 17)
        for (var blue = 0; blue <= 255; blue += 17)
        {
            AssertLabRoundTrip(Color.FromArgb((red + green + blue) & 0xFF, red, green, blue));
        }

        var neutral = MaterialEditorPanel.ColorFromLab(50, 0, 0, 91);
        var violet = MaterialEditorPanel.ColorFromLab(75, 20, -30, 201);
        var clippedWarm = MaterialEditorPanel.ColorFromLab(50, 127, 127, 99);
        var clippedCool = MaterialEditorPanel.ColorFromLab(50, -128, -128, 99);
        if (neutral.A != 91
            || neutral.R is < 118 or > 120
            || Math.Abs(neutral.G - neutral.R) > 1
            || Math.Abs(neutral.B - neutral.R) > 1
            || violet.A != 201 || violet.R != 194 || violet.G != 175 || violet.B != 240
            || clippedWarm.A != 99 || clippedWarm.R != 255 || clippedWarm.G != 0 || clippedWarm.B != 0
            || clippedCool.A != 99 || clippedCool.R != 0 || clippedCool.G != 169 || clippedCool.B != 255)
        {
            throw new InvalidOperationException("CIELAB inverse conversion, sRGB gamut clipping, or alpha preservation was invalid.");
        }

        Console.WriteLine("lab_color_regression=ok");
    }

    private static void RunTraditionalColorPickerRegression()
    {
        var planeSamples = 0;
        var planeValueChanges = 0;
        var planeStarted = 0;
        var planeCompleted = 0;
        var planeCanceled = 0;
        using var plane = new TraditionalColorPlane
        {
            Size = new Size(104, 108),
            HorizontalAxisName = "Horizontal",
            VerticalAxisName = "Vertical",
            ColorAt = (x, y) =>
            {
                planeSamples++;
                return Color.FromArgb(
                    255,
                    (int)MathF.Round(x * 255),
                    (int)MathF.Round(y * 255),
                    0);
            }
        };
        plane.AccessibleName = "Localized color field";
        plane.ValueChanged += (_, _) => planeValueChanges++;
        plane.InteractionStarted += (_, _) => planeStarted++;
        plane.InteractionCompleted += (_, _) => planeCompleted++;
        plane.InteractionCanceled += (_, _) => planeCanceled++;
        plane.CreateControl();
        using var planeBitmap = new Bitmap(plane.Width, plane.Height);
        plane.DrawToBitmap(planeBitmap, plane.ClientRectangle);
        var initialPlaneSamples = planeSamples;
        var highCorner = planeBitmap.GetPixel(plane.Width - 8, 7);
        var lowCorner = planeBitmap.GetPixel(7, plane.Height - 8);
        plane.SetValues(0.2f, 0.8f);
        plane.DrawToBitmap(planeBitmap, plane.ClientRectangle);
        if (initialPlaneSamples <= 0
            || highCorner.R < 240 || highCorner.G < 240
            || lowCorner.R > 15 || lowCorner.G > 15
            || planeSamples != initialPlaneSamples
            || planeValueChanges != 0
            || plane.AccessibleName != "Localized color field")
        {
            throw new InvalidOperationException("Traditional color plane orientation, marker-only repaint caching, or accessibility state was invalid.");
        }

        plane.RefreshGradient();
        plane.DrawToBitmap(planeBitmap, plane.ClientRectangle);
        if (planeSamples <= initialPlaneSamples)
        {
            throw new InvalidOperationException("Traditional color plane did not rebuild its cached bitmap after gradient invalidation.");
        }

        SendControlKey(plane, "OnKeyDown", Keys.Right);
        SendControlKey(plane, "OnKeyUp", Keys.Right);
        var cancelPlaneY = plane.YValue;
        SendControlKey(plane, "OnKeyDown", Keys.Down);
        SendControlKey(plane, "OnKeyDown", Keys.Escape);
        if (planeStarted != 2
            || planeCompleted != 1
            || planeCanceled != 1
            || Math.Abs(plane.YValue - cancelPlaneY) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional color plane keyboard completion or Escape cancellation lifecycle was invalid.");
        }

        var componentSamples = 0;
        var componentValueChanges = 0;
        var componentStarted = 0;
        var componentCompleted = 0;
        var componentCanceled = 0;
        using var component = new VerticalColorComponentSlider
        {
            Size = new Size(24, 108),
            AxisName = "Primary",
            GradientColor = amount =>
            {
                componentSamples++;
                return Color.FromArgb(255, (int)MathF.Round(amount * 255), 0, 0);
            }
        };
        component.AccessibleName = "Localized primary component";
        component.ValueChanged += (_, _) => componentValueChanges++;
        component.InteractionStarted += (_, _) => componentStarted++;
        component.InteractionCompleted += (_, _) => componentCompleted++;
        component.InteractionCanceled += (_, _) => componentCanceled++;
        component.CreateControl();
        using var componentBitmap = new Bitmap(component.Width, component.Height);
        component.DrawToBitmap(componentBitmap, component.ClientRectangle);
        var initialComponentSamples = componentSamples;
        var highAtTop = componentBitmap.GetPixel(12, 5).R;
        var lowAtBottom = componentBitmap.GetPixel(12, 102).R;
        component.SetValue(0.35f);
        component.DrawToBitmap(componentBitmap, component.ClientRectangle);
        if (initialComponentSamples <= 0
            || highAtTop < 240 || lowAtBottom > 15
            || componentSamples != initialComponentSamples
            || componentValueChanges != 0
            || component.AccessibleName != "Localized primary component")
        {
            throw new InvalidOperationException("Traditional primary-component orientation, marker-only repaint caching, or accessibility state was invalid.");
        }

        component.RefreshGradient();
        component.DrawToBitmap(componentBitmap, component.ClientRectangle);
        var refreshedComponentSamples = componentSamples;
        component.HighAtTop = false;
        component.DrawToBitmap(componentBitmap, component.ClientRectangle);
        var lowAtTop = componentBitmap.GetPixel(12, 5).R;
        var highAtBottom = componentBitmap.GetPixel(12, 102).R;
        if (refreshedComponentSamples <= initialComponentSamples
            || componentSamples <= refreshedComponentSamples
            || lowAtTop > 15 || highAtBottom < 240)
        {
            throw new InvalidOperationException("Traditional primary-component gradient invalidation or low-at-top direction was invalid.");
        }

        component.SetValue(0.4f);
        SendControlKey(component, "OnKeyDown", Keys.Up);
        SendControlKey(component, "OnKeyUp", Keys.Up);
        var cancelComponentValue = component.Value;
        SendControlKey(component, "OnKeyDown", Keys.Down);
        SendControlKey(component, "OnKeyDown", Keys.Escape);
        if (componentStarted != 2
            || componentCompleted != 1
            || componentCanceled != 1
            || Math.Abs(component.Value - cancelComponentValue) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional primary-component keyboard completion or Escape cancellation lifecycle was invalid.");
        }

        using var material = new MaterialEditorPanel();
        var materialChanges = 0;
        var materialInteractionsStarted = 0;
        var materialInteractionsCompleted = 0;
        var materialInteractionsCanceled = 0;
        material.MaterialChanged += (_, _) => materialChanges++;
        material.ContinuousEditStarted += (_, _) => materialInteractionsStarted++;
        material.ContinuousEditCompleted += (_, _) => materialInteractionsCompleted++;
        material.ContinuousEditCanceled += (_, _) => materialInteractionsCanceled++;
        var setMode = typeof(MaterialEditorPanel).GetMethod(
            "SetColorMode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var planeField = typeof(MaterialEditorPanel).GetField(
            "_traditionalPlane",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var componentField = typeof(MaterialEditorPanel).GetField(
            "_primaryComponent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (setMode is null
            || planeField?.GetValue(material) is not TraditionalColorPlane materialPlane
            || componentField?.GetValue(material) is not VerticalColorComponentSlider materialComponent)
        {
            throw new InvalidOperationException("Traditional material color-picker controls were not connected.");
        }

        var modeType = setMode.GetParameters()[0].ParameterType;
        foreach (var (name, source, highAtTopExpected) in new[]
                 {
                     ("Rgb", Color.FromArgb(73, 10, 20, 30), true),
                     ("Hsv", Color.FromArgb(73, 128, 0, 0), false),
                     ("Hsl", Color.FromArgb(73, 128, 0, 0), false),
                     ("Lab", Color.FromArgb(73, 128, 128, 128), true)
                 })
        {
            material.SetMaterial(source, Color.White, 2f, source.A / 255f);
            var changesBeforeMode = materialChanges;
            var interactionsBeforeMode = materialInteractionsStarted;
            setMode.Invoke(material, [Enum.Parse(modeType, name)]);
            if (material.Fill.ToArgb() != source.ToArgb()
                || materialChanges != changesBeforeMode
                || materialComponent.HighAtTop != highAtTopExpected)
            {
                throw new InvalidOperationException($"Traditional color-picker mode {name} changed material color, raised an edit, or used the wrong primary direction.");
            }

            var expectedAfterPlane = name switch
            {
                "Rgb" => Color.FromArgb(source.A, source.R, 255, 255),
                "Hsv" => Color.FromArgb(source.A, 255, 0, 0),
                "Hsl" => Color.FromArgb(source.A, 255, 255, 255),
                _ => ExpectedLabPlaneMaximum(source)
            };
            SendControlKey(materialPlane, "OnKeyDown", Keys.End);
            SendControlKey(materialPlane, "OnKeyUp", Keys.End);
            if (material.Fill.ToArgb() != expectedAfterPlane.ToArgb())
            {
                throw new InvalidOperationException(
                    $"Traditional color-picker mode {name} mapped its two-dimensional maximum to {material.Fill.ToArgb():X8} instead of {expectedAfterPlane.ToArgb():X8}.");
            }

            var expectedInteractions = 1;
            if (name == "Rgb")
            {
                SendControlKey(materialComponent, "OnKeyDown", Keys.End);
                SendControlKey(materialComponent, "OnKeyUp", Keys.End);
                expectedInteractions++;
                if (material.Fill.ToArgb() != Color.FromArgb(source.A, 255, 255, 255).ToArgb())
                {
                    throw new InvalidOperationException("Traditional RGB primary-component input did not map R to its maximum.");
                }
            }

            if (material.Fill.A != source.A
                || materialChanges <= changesBeforeMode
                || materialInteractionsStarted - interactionsBeforeMode != expectedInteractions
                || materialInteractionsCompleted != materialInteractionsStarted
                || materialInteractionsCanceled != 0)
            {
                throw new InvalidOperationException($"Traditional color-picker mode {name} did not preserve alpha or emit one completed continuous lifecycle per gesture.");
            }
        }

        Console.WriteLine("traditional_color_picker_regression=ok");
    }

    private static Color ExpectedLabPlaneMaximum(Color source)
    {
        MaterialEditorPanel.RgbToLab(source, out var lightness, out _, out _);
        var roundedLightness = Math.Round(lightness * 10, MidpointRounding.AwayFromZero) / 10;
        return MaterialEditorPanel.ColorFromLab(roundedLightness, 127, 127, source.A);
    }

    private static void SendControlKey(Control control, string methodName, Keys key)
    {
        var method = control.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method is null) throw new InvalidOperationException($"Missing {control.GetType().Name}.{methodName} regression hook.");
        method.Invoke(control, [new KeyEventArgs(key)]);
    }

    private static void AssertLabRoundTrip(Color source)
    {
        MaterialEditorPanel.RgbToLab(source, out var lightness, out var greenRed, out var blueYellow);
        var roundTrip = MaterialEditorPanel.ColorFromLab(lightness, greenRed, blueYellow, source.A);
        if (roundTrip.A != source.A
            || Math.Abs(roundTrip.R - source.R) > 1
            || Math.Abs(roundTrip.G - source.G) > 1
            || Math.Abs(roundTrip.B - source.B) > 1)
        {
            throw new InvalidOperationException(
                $"CIELAB round trip changed ARGB {source.ToArgb():X8} to {roundTrip.ToArgb():X8}.");
        }
    }

    private static void RunGradientPresetRegression()
    {
        var presets = MaterialEditorPanel.BuiltInGradientPresets;
        var midpoint = GradientPreviewRenderer.Sample(
            [new GradientStop(0, Color.Black), new GradientStop(1, Color.White)],
            0.5f);
        var savedGradient = new GradientPreset(
            "Saved radial",
            GradientKind.Radial,
            [new GradientStop(0, Color.Coral), new GradientStop(0.4f, Color.Gold), new GradientStop(1, Color.RoyalBlue)],
            isUserSaved: true);
        var restoredSavedGradients = MaterialPaletteStore.Deserialize(MaterialPaletteStore.Serialize([savedGradient]));
        var persistenceRoundTrip = restoredSavedGradients.Length == 1
            && restoredSavedGradients[0].IsUserSaved
            && restoredSavedGradients[0].Name == savedGradient.Name
            && restoredSavedGradients[0].Kind == savedGradient.Kind
            && restoredSavedGradients[0].Stops.SequenceEqual(savedGradient.Stops);
        if (presets.Count != 6
            || presets.Count(preset => preset.Kind == GradientKind.Linear) != 4
            || presets.Count(preset => preset.Kind == GradientKind.Radial) != 2
            || presets.Any(preset => preset.Stops.Length < 2 || preset.Stops[0].Position != 0f || preset.Stops[^1].Position != 1f)
            || midpoint.R is < 126 or > 129 || midpoint.G != midpoint.R || midpoint.B != midpoint.R
            || !persistenceRoundTrip)
        {
            throw new InvalidOperationException("Gradient presets, persistence, or multi-stop preview sampling were invalid.");
        }

        Console.WriteLine("gradient_preset_regression=ok");
    }

    private static void RunSoftBrushRegression()
    {
        RunTraditionalBrushContourRegression();
        RunBrushGradientRegression();

        var scene = new VectorScene();
        scene.CreateEmpty();
        var centerline = new[] { new PointF(-180, 0), new PointF(0, 0), new PointF(180, 0) };
        var objects = scene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32);
        if (objects.Length != 3
            || objects.Any(index => (uint)index >= scene.ObjectCount || scene.ShapeKind[index] != ShapeKind.Path)
            || objects.Any(index => !scene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("RGB soft brush did not create layered Path fills over its centerline.");
        }

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        var traditional = BrushShape.CreateTraditionalBrush();
        var first = mergeScene.AddSoftBrushStroke(0, centerline, VectorUnits.StrokePointsToUnits(16), Color.Coral, traditional, 32);
        var second = mergeScene.AddSoftBrushStroke(
            0,
            centerline.Select(point => new PointF(point.X, point.Y + 8)).ToArray(),
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            traditional,
            32);
        var merged = mergeScene.MergeSameColorFillsAroundNewObjects(second, connectNearby: true);
        if (first.Length != 1
            || second.Length != 1
            || mergeScene.ObjectCount != 1
            || merged.Length != 1
            || !mergeScene.FillContainsPoint(merged[0], PointF.Empty))
        {
            throw new InvalidOperationException("A newly committed brush fill did not immediately merge with the matching prior brush fill.");
        }

        Console.WriteLine("soft_brush_regression=ok");
    }

    private static void RunBrushGradientRegression()
    {
        var color = Color.FromArgb(200, Color.Coral);
        var stops = new[]
        {
            new GradientStop(0, Color.FromArgb(240, Color.Coral)),
            new GradientStop(0.45f, Color.FromArgb(160, Color.Gold)),
            new GradientStop(1, Color.FromArgb(80, Color.RoyalBlue))
        };
        if (!GradientPaintUtilities.AreStopsOpaque([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)])
            || GradientPaintUtilities.AreStopsOpaque(stops))
        {
            throw new InvalidOperationException("Gradient opacity detection did not distinguish seam-safe opaque fills from translucent brush layers.");
        }
        var irregularPath = new[]
        {
            new PointF(-180, 10),
            new PointF(-120, -150),
            new PointF(-10, -20),
            new PointF(80, 145),
            new PointF(180, -50)
        };
        var gradientSegments = GradientPaintUtilities.CreatePathGradientSegments(irregularPath, stops, 24);
        if (gradientSegments.Length != 24
            || gradientSegments[0].StartArgb != stops[0].Argb
            || gradientSegments[^1].EndArgb != stops[^1].Argb
            || !gradientSegments.Any(segment => segment.StartArgb != segment.EndArgb)
            || gradientSegments.Zip(gradientSegments.Skip(1)).Any(pair => pair.First.EndArgb != pair.Second.StartArgb)
            || GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(irregularPath, out _))
        {
            throw new InvalidOperationException("Curved brush gradient segments did not retain continuous endpoint colors along the accumulated path length.");
        }

        var selfCrossingPath = new[]
        {
            new PointF(-180, -120),
            new PointF(180, 120),
            new PointF(-180, 120),
            new PointF(180, -120)
        };
        if (!GradientPaintUtilities.TryCreateSelfIntersectionFallbackAxis(selfCrossingPath, out var fallbackAxis)
            || fallbackAxis.Start.X >= fallbackAxis.End.X
            || Math.Abs(fallbackAxis.End.X - fallbackAxis.Start.X) < 300
            || Math.Abs(fallbackAxis.End.Y - fallbackAxis.Start.Y) > 1)
        {
            throw new InvalidOperationException("A self-crossing brush path did not resolve to a stable, start-oriented spatial gradient axis.");
        }

        var selfCrossingScene = new VectorScene();
        selfCrossingScene.CreateEmpty();
        var selfCrossingObjects = selfCrossingScene.AddSoftBrushStroke(
            0,
            selfCrossingPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(
            selfCrossingScene,
            selfCrossingObjects,
            color,
            GradientKind.Linear,
            stops,
            selfCrossingPath[0],
            selfCrossingPath[^1]);
        foreach (var objectIndex in selfCrossingObjects) selfCrossingScene.SetGradientPath(objectIndex, selfCrossingPath);
        if (selfCrossingObjects.Length != 1
            || selfCrossingScene.HasGradientPath(selfCrossingObjects[0])
            || !PointsNear(selfCrossingScene.GetGradientStart(selfCrossingObjects[0]), fallbackAxis.Start)
            || !PointsNear(selfCrossingScene.GetGradientEnd(selfCrossingObjects[0]), fallbackAxis.End))
        {
            throw new InvalidOperationException("A self-crossing gradient brush retained conflicting trajectory colors at its intersection.");
        }

        var centerline = new[] { new PointF(-180, 0), PointF.Empty, new PointF(180, 0) };
        var softScene = new VectorScene();
        softScene.CreateEmpty();
        var softObjects = softScene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateSoftRound(),
            32);
        ApplyBrushGradient(softScene, softObjects, color, GradientKind.Linear, stops, centerline[0], centerline[^1]);
        foreach (var objectIndex in softObjects) softScene.SetGradientPath(objectIndex, centerline);
        if (softObjects.Length < 2
            || !softObjects.All(index => softScene.HasGradient(index)
                && softScene.GetGradientKind(index) == GradientKind.Linear
                && softScene.GetGradientStops(index).Length == stops.Length
                && softScene.GetGradientStart(index) == centerline[0]
                && softScene.GetGradientEnd(index) == centerline[^1]
                && softScene.TryGetGradientPathWorldPoints(index, out var gradientPath)
                && gradientPath.SequenceEqual(centerline))
            || Color.FromArgb(softScene.GetGradientStops(softObjects[0])[0].Argb).A
                >= Color.FromArgb(softScene.GetGradientStops(softObjects[^1])[0].Argb).A)
        {
            throw new InvalidOperationException("Soft brush gradient material did not preserve its layered alpha falloff.");
        }

        var gradientPathSnapshot = softScene.CreateSnapshot();
        softScene.TransformObjects([softObjects[0]], point => new PointF(point.X + 37, point.Y - 19));
        if (!softScene.TryGetGradientPathWorldPoints(softObjects[0], out var transformedGradientPath)
            || !transformedGradientPath.SequenceEqual(centerline.Select(point => new PointF(point.X + 37, point.Y - 19))))
        {
            throw new InvalidOperationException("Transforming a brush fill did not keep its path-gradient trajectory aligned.");
        }
        softScene.RestoreSnapshot(gradientPathSnapshot);

        var shapeBrushScene = new VectorScene();
        shapeBrushScene.CreateEmpty();
        var loopingPath = new[]
        {
            new PointF(-190, 0),
            new PointF(-90, -120),
            new PointF(90, -120),
            new PointF(180, 0),
            new PointF(90, 110),
            new PointF(-80, 95),
            new PointF(-130, 10),
            new PointF(80, -10)
        };
        var shapeBrushObjects = shapeBrushScene.AddSoftBrushStroke(
            0,
            loopingPath,
            VectorUnits.StrokePointsToUnits(20),
            color,
            BrushShape.CreateSoftRound(),
            48);
        var sharedShapeMapping = shapeBrushObjects.Length > 0
            ? shapeBrushScene.GetObjectBoundaryContours(shapeBrushObjects[0])
            : Array.Empty<PointF[]>();
        var sharedShapePoints = sharedShapeMapping.SelectMany(contour => contour).ToArray();
        var sharedShapeCenter = sharedShapePoints.Length > 0
            ? VectorUnits.Quantize(new PointF(
                (sharedShapePoints.Min(point => point.X) + sharedShapePoints.Max(point => point.X)) * 0.5f,
                (sharedShapePoints.Min(point => point.Y) + sharedShapePoints.Max(point => point.Y)) * 0.5f))
            : PointF.Empty;
        GradientPaintUtilities.TryFindShapeBoundaryPoint(
            sharedShapeMapping,
            sharedShapeCenter,
            new PointF(1, 0),
            out var sharedShapeEnd);
        foreach (var objectIndex in shapeBrushObjects)
        {
            var layerAlpha = Color.FromArgb(shapeBrushScene.Argb[objectIndex]).A;
            shapeBrushScene.SetGradientPaint(
                objectIndex,
                GradientKind.ShapeRadial,
                GradientPaintUtilities.ScaleStopAlpha(stops, layerAlpha / (float)Math.Max(1, (int)color.A)),
                sharedShapeCenter,
                sharedShapeEnd);
            shapeBrushScene.SetShapeGradientMapping(objectIndex, sharedShapeMapping);
        }
        PointF[][] normalizedSharedShapeMapping = [];
        var hasNormalizedSharedShapeMapping = shapeBrushObjects.Length > 0
            && shapeBrushScene.TryGetShapeGradientMappingWorldContours(
                shapeBrushObjects[0],
                out normalizedSharedShapeMapping);
        var normalizedSharedShapePoints = hasNormalizedSharedShapeMapping
            ? normalizedSharedShapeMapping.SelectMany(contour => contour).ToArray()
            : Array.Empty<PointF>();
        var shapeBrushSnapshot = shapeBrushScene.CreateSnapshot();
        var shapeBrushMappingShared = shapeBrushObjects.Length == 3
            && hasNormalizedSharedShapeMapping
            && shapeBrushObjects.All(index =>
                shapeBrushScene.GetGradientKind(index) == GradientKind.ShapeRadial
                && shapeBrushScene.GetGradientStart(index) == sharedShapeCenter
                && shapeBrushScene.TryGetShapeGradientMappingWorldContours(index, out var mapping)
                && mapping.SelectMany(contour => contour).SequenceEqual(normalizedSharedShapePoints));
        shapeBrushScene.TransformObjects([shapeBrushObjects[0]], point => new PointF(point.X + 31, point.Y - 17));
        var shapeBrushMappingTransformed = shapeBrushScene.TryGetShapeGradientMappingWorldContours(
                shapeBrushObjects[0],
                out var transformedShapeMapping)
            && transformedShapeMapping.SelectMany(contour => contour).SequenceEqual(
                normalizedSharedShapePoints.Select(point => new PointF(point.X + 31, point.Y - 17)));
        shapeBrushScene.RestoreSnapshot(shapeBrushSnapshot);
        if (!shapeBrushMappingShared || !shapeBrushMappingTransformed)
        {
            throw new InvalidOperationException(
                $"Layered irregular brush shape gradients did not share or transform one outer mapping: shared={shapeBrushMappingShared}, transformed={shapeBrushMappingTransformed}.");
        }

        var matchingShapeScene = new VectorScene();
        matchingShapeScene.CreateEmpty();
        var firstShapePath = new[] { new PointF(-220, 0), new PointF(-120, 0), new PointF(-20, 0) };
        var secondShapePath = new[] { new PointF(-60, 0), new PointF(50, 0), new PointF(160, 0) };
        var firstShapeBrush = matchingShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(matchingShapeScene, firstShapeBrush, color, stops);
        var firstShapeCenter = matchingShapeScene.GetGradientStart(firstShapeBrush[0]);

        var shapePreviewScene = new VectorScene();
        shapePreviewScene.CreateEmpty();
        var previewShapeBrush = shapePreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(shapePreviewScene, previewShapeBrush, color, stops);
        var matchingPreviewSources = matchingShapeScene.FindIntersectingMatchingShapeGradientFills(
            shapePreviewScene,
            previewShapeBrush,
            0);

        var secondShapeBrush = matchingShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(matchingShapeScene, secondShapeBrush, color, stops);
        var mergedShapeBrush = matchingShapeScene.ApplyFillOverwriteToNewObjects(secondShapeBrush, 0);
        var mergedShapeMapping = mergedShapeBrush.Length == 1
            && matchingShapeScene.TryGetShapeGradientMappingWorldContours(mergedShapeBrush[0], out var combinedMapping)
                ? combinedMapping
                : Array.Empty<PointF[]>();
        var mergedShapeCenter = mergedShapeBrush.Length == 1
            ? matchingShapeScene.GetGradientStart(mergedShapeBrush[0])
            : PointF.Empty;
        if (firstShapeBrush.Length != 1
            || previewShapeBrush.Length != 1
            || matchingPreviewSources.Length != 1
            || mergedShapeBrush.Length != 1
            || matchingShapeScene.ObjectCount != 1
            || matchingShapeScene.GetGradientKind(mergedShapeBrush[0]) != GradientKind.ShapeRadial
            || !matchingShapeScene.GetGradientStops(mergedShapeBrush[0]).SequenceEqual(stops)
            || mergedShapeCenter == firstShapeCenter
            || !matchingShapeScene.FillContainsPoint(mergedShapeBrush[0], firstShapePath[1])
            || !matchingShapeScene.FillContainsPoint(mergedShapeBrush[0], secondShapePath[1])
            || mergedShapeMapping.SelectMany(contour => contour).Count() < 3)
        {
            throw new InvalidOperationException(
                $"Matching shape-gradient brush strokes did not preview, union, and recalculate one shared gradient: " +
                $"previewMatches={matchingPreviewSources.Length}, merged={mergedShapeBrush.Length}, " +
                $"objects={matchingShapeScene.ObjectCount}, center={mergedShapeCenter}.");
        }

        var budgetShapeScene = new VectorScene();
        budgetShapeScene.CreateEmpty();
        var softShapeBrush = BrushShape.CreateSoftRound();
        var budgetFirstShapeBrush = budgetShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            softShapeBrush,
            1_024);
        ApplyShapeBrushGradient(budgetShapeScene, budgetFirstShapeBrush, color, stops);
        var budgetFirstRetained = budgetShapeScene.NormalizePaintForInteractiveCommit(
            budgetFirstShapeBrush,
            connectNearby: true,
            frame: 0,
            enforceComplexityBudget: true);
        var budgetSecondShapeBrush = budgetShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            softShapeBrush,
            1_024);
        ApplyShapeBrushGradient(budgetShapeScene, budgetSecondShapeBrush, color, stops);
        var rejectedByGeneralInteractiveBudget = !budgetShapeScene.CanNormalizePaintInteractively(
            budgetSecondShapeBrush,
            0);
        var budgetMergedShapeBrush = budgetShapeScene.NormalizePaintForInteractiveCommit(
            budgetSecondShapeBrush,
            connectNearby: true,
            frame: 0,
            enforceComplexityBudget: true);
        var budgetMergedLayerStates = budgetMergedShapeBrush.Select(index =>
        {
            var hasMapping = budgetShapeScene.TryGetShapeGradientMappingWorldContours(index, out var mapping);
            return (
                Index: index,
                Kind: budgetShapeScene.GetGradientKind(index),
                ContainsFirst: budgetShapeScene.FillContainsPoint(index, firstShapePath[1]),
                ContainsSecond: budgetShapeScene.FillContainsPoint(index, secondShapePath[1]),
                MappingPoints: hasMapping ? mapping.SelectMany(contour => contour).Count() : 0);
        }).ToArray();
        var budgetMergedLayersValid = budgetMergedLayerStates.All(state =>
            state.Kind == GradientKind.ShapeRadial
            && state.ContainsFirst
            && state.ContainsSecond
            && state.MappingPoints >= 3);
        if (budgetFirstShapeBrush.Length != 3
            || budgetFirstRetained.Length != 3
            || budgetSecondShapeBrush.Length != 3
            || !rejectedByGeneralInteractiveBudget
            || budgetMergedShapeBrush.Length != 3
            || budgetShapeScene.ObjectCount != 3
            || !budgetMergedLayersValid)
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brush fills stopped participating in union after exceeding the general interactive normalization budget: "
                + $"first={budgetFirstShapeBrush.Length}/{budgetFirstRetained.Length}, "
                + $"second={budgetSecondShapeBrush.Length}, rejected={rejectedByGeneralInteractiveBudget}, "
                + $"merged={budgetMergedShapeBrush.Length}, objects={budgetShapeScene.ObjectCount}, "
                + $"layersValid={budgetMergedLayersValid}, states=[{string.Join(';', budgetMergedLayerStates)}].");
        }

        var hiddenFillScene = new VectorScene();
        hiddenFillScene.CreateEmpty();
        var hiddenFirstColor = Color.FromArgb(color.A, 24, 96, 180);
        var hiddenSecondColor = Color.FromArgb(color.A, 210, 72, 36);
        var hiddenFirst = hiddenFillScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenFirstColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenFillScene, hiddenFirst, hiddenFirstColor, stops);

        var hiddenPreviewScene = new VectorScene();
        hiddenPreviewScene.CreateEmpty();
        var hiddenPreview = hiddenPreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenPreviewScene, hiddenPreview, hiddenSecondColor, stops);
        var hiddenPreviewMatches = hiddenFillScene.FindIntersectingMatchingShapeGradientFills(
            hiddenPreviewScene,
            hiddenPreview,
            0);

        var hiddenSecond = hiddenFillScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            hiddenSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(hiddenFillScene, hiddenSecond, hiddenSecondColor, stops);
        var hiddenMerged = hiddenFillScene.NormalizePaintForInteractiveCommit(hiddenSecond, frame: 0);
        if (hiddenPreviewMatches.Length != 1
            || hiddenMerged.Length != 1
            || hiddenFillScene.ObjectCount != 1
            || !hiddenFillScene.GetGradientStops(hiddenMerged[0]).SequenceEqual(stops)
            || !hiddenFillScene.FillContainsPoint(hiddenMerged[0], firstShapePath[1])
            || !hiddenFillScene.FillContainsPoint(hiddenMerged[0], secondShapePath[1]))
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brushes depended on hidden fallback fill RGB: "
                + $"preview={hiddenPreviewMatches.Length}, merged={hiddenMerged.Length}, objects={hiddenFillScene.ObjectCount}.");
        }

        var alphaScene = new VectorScene();
        alphaScene.CreateEmpty();
        var alphaFirstColor = Color.FromArgb(220, 24, 96, 180);
        var alphaSecondColor = Color.FromArgb(160, 210, 72, 36);
        var alphaFirst = alphaScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            alphaFirstColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(alphaScene, alphaFirst, alphaFirstColor, stops);
        var alphaPreviewScene = new VectorScene();
        alphaPreviewScene.CreateEmpty();
        var alphaPreview = alphaPreviewScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            alphaSecondColor,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(alphaPreviewScene, alphaPreview, alphaSecondColor, stops);
        var alphaMatches = alphaScene.FindIntersectingMatchingShapeGradientFills(
            alphaPreviewScene,
            alphaPreview,
            0);
        if (alphaMatches.Length != 0)
        {
            throw new InvalidOperationException("Shape-gradient brushes with different layer alpha incorrectly matched.");
        }

        var crossLayerShapeScene = new VectorScene();
        crossLayerShapeScene.CreateEmpty(2);
        var crossLayerFirst = crossLayerShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(crossLayerShapeScene, crossLayerFirst, color, stops);
        var crossLayerSecond = crossLayerShapeScene.AddSoftBrushStroke(
            1,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(crossLayerShapeScene, crossLayerSecond, color, stops);
        crossLayerShapeScene.ObjectOrder[crossLayerSecond[0]] = crossLayerShapeScene.ObjectOrder[crossLayerFirst[0]];
        crossLayerShapeScene.ObjectSubOrder[crossLayerSecond[0]] = crossLayerShapeScene.ObjectSubOrder[crossLayerFirst[0]];
        var crossLayerRetained = crossLayerShapeScene.NormalizePaintForInteractiveCommit(crossLayerSecond, frame: 0);
        var crossLayerValid = crossLayerShapeScene.ObjectCount == 2
            && crossLayerRetained.Length == 1
            && crossLayerShapeScene.ObjectLayer[crossLayerRetained[0]] == 1
            && crossLayerShapeScene.ObjectLayer[crossLayerFirst[0]] == 0;
        if (!crossLayerValid)
        {
            throw new InvalidOperationException(
                "Matching shape-gradient brushes crossed a layer or resolved a colliding stack key incorrectly: "
                + $"retained={crossLayerRetained.Length}, objects={crossLayerShapeScene.ObjectCount}.");
        }

        var mismatchedShapeScene = new VectorScene();
        mismatchedShapeScene.CreateEmpty();
        var mismatchedFirst = mismatchedShapeScene.AddSoftBrushStroke(
            0,
            firstShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(mismatchedShapeScene, mismatchedFirst, color, stops);
        var changedShapeStops = stops.ToArray();
        changedShapeStops[1] = new GradientStop(changedShapeStops[1].Position, Color.FromArgb(160, Color.LimeGreen));
        var mismatchedSecond = mismatchedShapeScene.AddSoftBrushStroke(
            0,
            secondShapePath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            24);
        ApplyShapeBrushGradient(mismatchedShapeScene, mismatchedSecond, color, changedShapeStops);
        var mismatchedRetained = mismatchedShapeScene.ApplyFillOverwriteToNewObjects(mismatchedSecond, 0);
        if (mismatchedRetained.Length != 1
            || mismatchedShapeScene.ObjectCount <= 1
            || !mismatchedShapeScene.GetGradientStops(mismatchedRetained[0]).SequenceEqual(changedShapeStops)
            || Enumerable.Range(0, mismatchedShapeScene.ObjectCount)
                .Count(index => mismatchedShapeScene.FillContainsPoint(index, PointF.Empty)) != 1)
        {
            throw new InvalidOperationException("Different shape-gradient brush materials incorrectly entered the matching-gradient union path.");
        }

        var gradientOverlapScene = new VectorScene();
        gradientOverlapScene.CreateEmpty();
        var horizontalPath = new[] { new PointF(-200, 0), PointF.Empty, new PointF(200, 0) };
        var verticalPath = new[] { new PointF(0, -200), PointF.Empty, new PointF(0, 200) };
        var firstGradientBrush = gradientOverlapScene.AddSoftBrushStroke(
            0,
            horizontalPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(gradientOverlapScene, firstGradientBrush, color, GradientKind.Linear, stops, horizontalPath[0], horizontalPath[^1]);
        foreach (var objectIndex in firstGradientBrush) gradientOverlapScene.SetGradientPath(objectIndex, horizontalPath);

        var secondGradientBrush = gradientOverlapScene.AddSoftBrushStroke(
            0,
            verticalPath,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32);
        ApplyBrushGradient(gradientOverlapScene, secondGradientBrush, color, GradientKind.Linear, stops, verticalPath[0], verticalPath[^1]);
        foreach (var objectIndex in secondGradientBrush) gradientOverlapScene.SetGradientPath(objectIndex, verticalPath);

        var retainedGradientBrush = gradientOverlapScene.ApplyFillOverwriteToNewObjects(secondGradientBrush);
        if (firstGradientBrush.Length != 1
            || secondGradientBrush.Length != 1
            || retainedGradientBrush.Length != 1
            || gradientOverlapScene.ObjectCount != 3
            || !gradientOverlapScene.TryGetGradientPathWorldPoints(retainedGradientBrush[0], out var retainedSecondPath)
            || !retainedSecondPath.SequenceEqual(verticalPath)
            || !gradientOverlapScene.FillContainsPoint(retainedGradientBrush[0], PointF.Empty)
            || Enumerable.Range(0, gradientOverlapScene.ObjectCount)
                .Count(index => gradientOverlapScene.FillContainsPoint(index, PointF.Empty)) != 1
            || Enumerable.Range(0, gradientOverlapScene.ObjectCount)
                .Where(index => index != retainedGradientBrush[0])
                .Any(index => !gradientOverlapScene.HasGradient(index)
                    || gradientOverlapScene.HasGradientPath(index)
                    || !gradientOverlapScene.GetGradientStops(index).SequenceEqual(stops)))
        {
            throw new InvalidOperationException("Overlapping gradient brush strokes did not remove the covered fill or safely materialize the remaining gradient fragments.");
        }

        var pressureScene = new VectorScene();
        pressureScene.CreateEmpty();
        var pressureSamples = new[]
        {
            new PressureBrushSample(new PointF(-160, 0), 200, 0),
            new PressureBrushSample(PointF.Empty, 160, 0.2f),
            new PressureBrushSample(new PointF(160, 0), 220, 0.4f)
        };
        var pressureObjects = pressureScene.AddPressureBrushStroke(
            0,
            pressureSamples,
            VectorUnits.StrokePointsToUnits(16),
            color,
            BrushShape.CreateTraditionalBrush(),
            32,
            smoothing: 58,
            simplifyTolerance: 1);
        ApplyBrushGradient(
            pressureScene,
            pressureObjects,
            color,
            GradientKind.Radial,
            stops,
            pressureSamples[0].Point,
            pressureSamples[^1].Point);
        if (pressureObjects.Length != 1
            || !pressureScene.HasGradient(pressureObjects[0])
            || pressureScene.GetGradientKind(pressureObjects[0]) != GradientKind.Radial
            || !pressureScene.GetGradientStops(pressureObjects[0]).SequenceEqual(stops)
            || pressureScene.GetGradientStart(pressureObjects[0]) != pressureSamples[0].Point
            || pressureScene.GetGradientEnd(pressureObjects[0]) != pressureSamples[^1].Point)
        {
            throw new InvalidOperationException("Pressure brush did not retain the active gradient material.");
        }

        Console.WriteLine("brush_gradient_regression=ok");

        static void ApplyBrushGradient(
            VectorScene scene,
            IReadOnlyList<int> objects,
            Color sourceColor,
            GradientKind kind,
            IReadOnlyList<GradientStop> gradientStops,
            PointF start,
            PointF end)
        {
            var sourceAlpha = Math.Max(1, (int)sourceColor.A);
            foreach (var objectIndex in objects)
            {
                var layerAlpha = Color.FromArgb(scene.Argb[objectIndex]).A;
                scene.SetGradientPaint(
                    objectIndex,
                    kind,
                    GradientPaintUtilities.ScaleStopAlpha(gradientStops, layerAlpha / (float)sourceAlpha),
                    start,
                    end);
            }
        }

        static void ApplyShapeBrushGradient(
            VectorScene scene,
            IReadOnlyList<int> objects,
            Color sourceColor,
            IReadOnlyList<GradientStop> gradientStops)
        {
            if (objects.Count == 0) return;
            var mapping = scene.GetObjectBoundaryContours(objects[0]);
            var points = mapping.SelectMany(contour => contour).ToArray();
            if (points.Length == 0) return;
            var center = VectorUnits.Quantize(new PointF(
                (points.Min(point => point.X) + points.Max(point => point.X)) * 0.5f,
                (points.Min(point => point.Y) + points.Max(point => point.Y)) * 0.5f));
            var end = GradientPaintUtilities.TryFindShapeBoundaryPoint(mapping, center, new PointF(1, 0), out var boundary)
                ? boundary
                : new PointF(points.Max(point => point.X), center.Y);
            ApplyBrushGradient(scene, objects, sourceColor, GradientKind.ShapeRadial, gradientStops, center, end);
            foreach (var objectIndex in objects) scene.SetShapeGradientMapping(objectIndex, mapping);
        }
    }

    private static void RunBrushColorPaletteRegression()
    {
        using var material = new MaterialEditorPanel();
        material.Fill = Color.FromArgb(96, 20, 30, 40);
        var materialChanged = false;
        material.MaterialChanged += (_, _) => materialChanged = true;
        material.SetFillColorForBrush(Color.Coral);
        if (materialChanged
            || material.Fill.A != 96
            || material.Fill.R != Color.Coral.R
            || material.Fill.G != Color.Coral.G
            || material.Fill.B != Color.Coral.B
            || material.RecentColors.Count == 0)
        {
            throw new InvalidOperationException("Brush palette color selection altered scene material state or did not preserve the brush alpha.");
        }

        material.SetGradient(GradientKind.Linear, [new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]);
        material.SetGradientPreviewTarget(strokeTarget: true);
        var retainedFillGradient = material.GetGradientPaintForTarget(strokeTarget: false);
        if (!material.GradientPreviewOnStroke
            || !material.EditingStroke
            || retainedFillGradient.Kind != GradientKind.Linear
            || !retainedFillGradient.Stops.SequenceEqual([new GradientStop(0, Color.Coral), new GradientStop(1, Color.RoyalBlue)]))
        {
            throw new InvalidOperationException("Line gradient material controls did not preserve the Fill gradient while selecting the Stroke target.");
        }
        material.SetGradientPreviewTarget(strokeTarget: false);
        if (material.GradientPreviewOnStroke || material.EditingStroke)
        {
            throw new InvalidOperationException("Fill gradient material controls did not restore the fill target.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(640, 420) };
        var colors = new[] { Color.Coral, Color.Teal, Color.Gold, Color.MediumPurple };
        stage.SetBrushColorPalette(new Point(320, 210), colors);
        if (!stage.BrushColorPaletteVisible || stage.BrushColorPaletteColors.Count != colors.Length)
        {
            throw new InvalidOperationException("Brush recent-color palette did not become visible with its requested colors.");
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var bounds = stage.BrushColorPaletteSwatchBounds(index);
            if (bounds.IsEmpty || stage.HitTestBrushColorPalette(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)) != index)
            {
                throw new InvalidOperationException("Brush recent-color palette swatch layout or hit testing was invalid.");
            }
        }

        stage.SetBrushColorPalette(new Point(320, 210), colors, hoveredIndex: 2);
        if (stage.BrushColorPaletteHoveredIndex != 2)
        {
            throw new InvalidOperationException("Brush recent-color palette did not retain its hovered swatch.");
        }

        stage.ClearBrushColorPalette();
        if (stage.BrushColorPaletteVisible)
        {
            throw new InvalidOperationException("Brush recent-color palette did not clear after its Shift interaction ended.");
        }

        Console.WriteLine("brush_color_palette_regression=ok");
    }

    private static void RunTraditionalBrushContourRegression()
    {
        var traditionalContour = BrushShape.CreateTraditionalBrush().NormalizedContour(0.5f);
        var radii = traditionalContour.Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y)).ToArray();
        var traditionalRadiusSpread = radii.Max() - radii.Min();
        if (traditionalRadiusSpread > 0.0001f)
        {
            throw new InvalidOperationException("Traditional brush contour introduced radial spikes instead of a circular hard edge.");
        }

        var squareContour = BrushShape.CreateTraditionalBrush(
                TraditionalBrushTipKind.Square,
                widthPercent: 50,
                directionDegrees: 0)
            .NormalizedContour(0.5f);
        var squareWidth = squareContour.Max(point => point.X) - squareContour.Min(point => point.X);
        var squareHeight = squareContour.Max(point => point.Y) - squareContour.Min(point => point.Y);
        if (squareContour.Length != 4
            || Math.Abs(squareWidth - 1f) > 0.0001f
            || Math.Abs(squareHeight - 2f) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional square brush did not retain its requested shape width.");
        }

        var rotatedSquare = BrushShape.CreateTraditionalBrush(
                TraditionalBrushTipKind.Square,
                widthPercent: 50,
                directionDegrees: 90)
            .NormalizedContour(0.5f);
        var rotatedWidth = rotatedSquare.Max(point => point.X) - rotatedSquare.Min(point => point.X);
        var rotatedHeight = rotatedSquare.Max(point => point.Y) - rotatedSquare.Min(point => point.Y);
        if (Math.Abs(rotatedWidth - 2f) > 0.0001f
            || Math.Abs(rotatedHeight - 1f) > 0.0001f)
        {
            throw new InvalidOperationException("Traditional brush direction did not rotate the square tip.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var diameter = VectorUnits.StrokePointsToUnits(16);
        var objects = scene.AddSoftBrushStroke(
            0,
            [new PointF(-180, 0), new PointF(0, 0), new PointF(180, 0)],
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            32,
            frequency: 8,
            continuous: true);
        if (objects.Length != 1
            || scene.ShapeKind[objects[0]] != ShapeKind.Path
            || !scene.FillContainsPoint(objects[0], PointF.Empty))
        {
            throw new InvalidOperationException("Traditional brush did not create one continuous rounded stroke contour.");
        }

        var squareScene = new VectorScene();
        squareScene.CreateEmpty();
        var squareObjects = squareScene.AddSoftBrushStroke(
            0,
            [PointF.Empty],
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(TraditionalBrushTipKind.Square, widthPercent: 50),
            8,
            frequency: 8,
            continuous: true);
        if (squareObjects.Length != 1
            || !squareScene.FillContainsPoint(squareObjects[0], new PointF(0, diameter * 0.35f))
            || squareScene.FillContainsPoint(squareObjects[0], new PointF(diameter * 0.32f, 0)))
        {
            throw new InvalidOperationException("Traditional square brush geometry did not use its configured width.");
        }
    }

    private static void RunBrushFrequencyRegression()
    {
        var diameter = VectorUnits.StrokePointsToUnits(24);
        var centerline = new[] { new PointF(-diameter * 4, 0), new PointF(diameter * 4, 0) };
        var continuous = new VectorScene();
        continuous.CreateEmpty();
        var continuousObjects = continuous.AddSoftBrushStroke(
            0,
            centerline,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32,
            frequency: 8,
            continuous: true);
        if (continuousObjects.Length == 0 || !continuousObjects.Any(index => continuous.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Continuous brush spacing left a gap between distant pointer samples.");
        }

        var stamped = new VectorScene();
        stamped.CreateEmpty();
        var stampedObjects = stamped.AddSoftBrushStroke(
            0,
            centerline,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            32,
            frequency: 8,
            continuous: false);
        if (stampedObjects.Length == 0 || stampedObjects.Any(index => stamped.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Non-continuous brush mode did not retain stamp spacing.");
        }

        var softRadius = BrushShape.CreateSoftRound(0).NormalizedContour(0.70f)
            .Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y))
            .Average();
        var hardRadius = BrushShape.CreateSoftRound(80).NormalizedContour(0.70f)
            .Select(point => MathF.Sqrt(point.X * point.X + point.Y * point.Y))
            .Average();
        if (hardRadius <= softRadius + 0.1f)
        {
            throw new InvalidOperationException("Default soft round brush hardness did not widen its opaque core.");
        }

        Console.WriteLine("brush_frequency_regression=ok");
    }

    public static void RunPressureBrushRegression()
    {
        RunSoftBrushRegression();
        RunMixingBrushRegression();
        RunBrushColorPaletteRegression();

        var onsetSamples = new[]
        {
            new PressureBrushSample(new PointF(0, 0), 0, 0),
            new PressureBrushSample(new PointF(20, 0), 0, 0.02f),
            new PressureBrushSample(new PointF(1000, 0), 0, 0.8f)
        };
        var onsetDiameter = VectorUnits.StrokePointsToUnits(18);
        var onsetPreview = FreehandStrokeProcessor.CreatePressurePreview(onsetSamples, onsetDiameter);
        var onsetCommit = FreehandStrokeProcessor.ProcessPressure(onsetSamples, onsetDiameter, smoothing: 0, simplifyTolerance: 0.25f);
        var minimumDiameter = VectorUnits.MinimumStrokeUnits;
        var quantizedMinimumOnset = VectorUnits.Quantize(minimumDiameter * 0.6f);
        if (onsetPreview.Length != onsetSamples.Length
            || onsetPreview[0].Diameter != quantizedMinimumOnset
            || onsetPreview[1].Diameter <= onsetPreview[0].Diameter
            || onsetPreview[^1].Diameter <= onsetPreview[1].Diameter * 2f
            || onsetCommit.Length < 2
            || onsetCommit[0].Diameter != quantizedMinimumOnset
            || onsetCommit[^1].Diameter <= onsetCommit[0].Diameter * 8f)
        {
            throw new InvalidOperationException("Pressure brush onset did not diffuse from a minimal initial deposit.");
        }

        var tabletBaseDiameter = VectorUnits.StrokePointsToUnits(20);
        var tabletMinimumVisibleDiameter = minimumDiameter * 0.6f;
        var tabletMappingCases = new[]
        {
            (Pressure: -0.5f, ExpectedPressure: 0f),
            (Pressure: 0f, ExpectedPressure: 0f),
            (Pressure: 0.5f, ExpectedPressure: 0.5f),
            (Pressure: 1f, ExpectedPressure: 1f),
            (Pressure: 1.5f, ExpectedPressure: 1f)
        };
        foreach (var mapping in tabletMappingCases)
        {
            var tabletSample = new PressureBrushSample(PointF.Empty, 5000, 0, mapping.Pressure);
            var tabletPreview = FreehandStrokeProcessor.CreatePressurePreview([tabletSample], tabletBaseDiameter);
            var tabletProcess = FreehandStrokeProcessor.ProcessPressure(
                [tabletSample],
                tabletBaseDiameter,
                smoothing: 0,
                simplifyTolerance: 0.25f);
            var incrementalTabletProfile = new List<PressureBrushPoint>();
            var incrementalTabletDistances = new List<float>();
            FreehandStrokeProcessor.UpdatePressurePreviewProfile(
                [tabletSample],
                tabletBaseDiameter,
                incrementalTabletProfile,
                incrementalTabletDistances);
            var expectedDiameter = VectorUnits.Quantize(Math.Clamp(
                tabletBaseDiameter * mapping.ExpectedPressure,
                tabletMinimumVisibleDiameter,
                tabletBaseDiameter * 1.2f));
            if (tabletPreview.Length != 1
                || tabletProcess.Length != 1
                || incrementalTabletProfile.Count != 1
                || tabletPreview[0].Diameter != expectedDiameter
                || tabletProcess[0].Diameter != expectedDiameter
                || incrementalTabletProfile[0].Diameter != expectedDiameter)
            {
                throw new InvalidOperationException(
                    "Tablet pressure did not map and clamp consistently across pressure-brush profiles: "
                    + $"pressure={mapping.Pressure}, expected={expectedDiameter}, "
                    + $"preview={tabletPreview.FirstOrDefault().Diameter}, "
                    + $"process={tabletProcess.FirstOrDefault().Diameter}, "
                    + $"incremental={incrementalTabletProfile.FirstOrDefault().Diameter}.");
            }
        }

        var tabletPrecedenceDiameters = new[]
        {
            new PressureBrushSample(PointF.Empty, 0, 12, 0.5f),
            new PressureBrushSample(PointF.Empty, 5000, 0, 0.5f)
        }.Select(sample => FreehandStrokeProcessor.CreatePressurePreview([sample], tabletBaseDiameter)[0].Diameter)
            .ToArray();
        if (tabletPrecedenceDiameters[0] != tabletPrecedenceDiameters[1]
            || tabletPrecedenceDiameters[0] != VectorUnits.Quantize(tabletBaseDiameter * 0.5f))
        {
            throw new InvalidOperationException("Tablet pressure did not take precedence over speed and hold simulation.");
        }

        const float legacySpeed = 720f;
        const float legacyHeldSeconds = 0.42f;
        var implicitLegacySample = new PressureBrushSample(PointF.Empty, legacySpeed, legacyHeldSeconds);
        var explicitLegacySample = implicitLegacySample with { TabletPressure = float.NaN };
        var implicitLegacyPreview = FreehandStrokeProcessor.CreatePressurePreview(
            [implicitLegacySample],
            tabletBaseDiameter);
        var explicitLegacyPreview = FreehandStrokeProcessor.CreatePressurePreview(
            [explicitLegacySample],
            tabletBaseDiameter);
        var implicitLegacyProcess = FreehandStrokeProcessor.ProcessPressure(
            [implicitLegacySample],
            tabletBaseDiameter,
            smoothing: 0,
            simplifyTolerance: 0.25f);
        var legacyNormalizedSpeed = Math.Clamp(legacySpeed / 1800f, 0f, 1f);
        var legacySpeedPressure = 0.38f + 0.62f * MathF.Pow(1f - legacyNormalizedSpeed, 0.65f);
        var legacyHoldPressure = 0.55f + 0.45f * (1f - MathF.Exp(-legacyHeldSeconds / 0.15f));
        var legacyTargetDiameter = Math.Clamp(
            tabletBaseDiameter * legacySpeedPressure * legacyHoldPressure,
            tabletMinimumVisibleDiameter,
            tabletBaseDiameter * 1.2f);
        var legacyOnset = 1f - MathF.Exp(-legacyHeldSeconds / 0.18f);
        var expectedLegacyDiameter = VectorUnits.Quantize(
            tabletMinimumVisibleDiameter
                + (legacyTargetDiameter - tabletMinimumVisibleDiameter) * legacyOnset);
        if (!float.IsNaN(implicitLegacySample.TabletPressure)
            || implicitLegacyPreview.Length != 1
            || explicitLegacyPreview.Length != 1
            || implicitLegacyProcess.Length != 1
            || implicitLegacyPreview[0].Diameter != expectedLegacyDiameter
            || explicitLegacyPreview[0].Diameter != expectedLegacyDiameter
            || implicitLegacyProcess[0].Diameter != expectedLegacyDiameter)
        {
            throw new InvalidOperationException("NaN tablet pressure did not preserve the legacy speed and hold fallback.");
        }

        var tabletProfileSamples = new[]
        {
            new PressureBrushSample(new PointF(0, 0), 5000, 0, 0.2f),
            new PressureBrushSample(new PointF(100, 80), 0, 8, 0.8f),
            new PressureBrushSample(new PointF(200, -80), 5000, 0, 0.35f),
            new PressureBrushSample(new PointF(300, 80), 0, 8, 1f),
            new PressureBrushSample(new PointF(400, 0), 5000, 0, 0.5f)
        };
        var tabletPreviewProfile = FreehandStrokeProcessor.CreatePressurePreview(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 0);
        var tabletProcessProfile = FreehandStrokeProcessor.ProcessPressure(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 0,
            simplifyTolerance: 0.25f);
        var incrementalTabletPreview = new List<PressureBrushPoint>();
        var incrementalTabletTravel = new List<float>();
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            tabletProfileSamples,
            tabletBaseDiameter,
            incrementalTabletPreview,
            incrementalTabletTravel);
        var smoothedTabletPreview = FreehandStrokeProcessor.CreatePressurePreview(
            tabletProfileSamples,
            tabletBaseDiameter,
            smoothing: 70);
        var smoothedIncrementalTabletPreview = FreehandStrokeProcessor.SmoothPressureProfile(
            incrementalTabletPreview,
            smoothing: 70);
        var previewProcessMismatch = tabletPreviewProfile.Length != tabletProcessProfile.Length
            || tabletPreviewProfile.Zip(tabletProcessProfile).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 1);
        var previewIncrementalMismatch = tabletPreviewProfile.Length != incrementalTabletPreview.Count
            || tabletPreviewProfile.Zip(incrementalTabletPreview).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 2);
        var smoothedPreviewMismatch = smoothedTabletPreview.Length != smoothedIncrementalTabletPreview.Length
            || smoothedTabletPreview.Zip(smoothedIncrementalTabletPreview).Any(pair =>
                pair.First.Point != pair.Second.Point
                || MathF.Abs(pair.First.Diameter - pair.Second.Diameter) > 2);
        if (previewProcessMismatch
            || previewIncrementalMismatch
            || smoothedPreviewMismatch
            || tabletPreviewProfile[1].Diameter >= VectorUnits.Quantize(tabletBaseDiameter * 0.8f))
        {
            throw new InvalidOperationException(
                "Tablet pressure diverged between preview, incremental preview, and processed profiles or bypassed smoothing.");
        }

        var samples = new PressureBrushSample[513];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = -640 + i * 2.5f;
            samples[i] = new PressureBrushSample(
                new PointF(x, MathF.Sin(i * 0.08f) * 18),
                120 + i % 9 * 40,
                i / 120f);
        }

        var diameter = VectorUnits.StrokePointsToUnits(18);
        var incrementalPressureSamples = new List<PressureBrushSample>();
        var incrementalPressureProfile = new List<PressureBrushPoint>();
        var incrementalPressureDistances = new List<float>();
        for (var start = 0; start < samples.Length; start += 37)
        {
            incrementalPressureSamples.AddRange(samples.Skip(start).Take(Math.Min(37, samples.Length - start)));
            FreehandStrokeProcessor.UpdatePressurePreviewProfile(
                incrementalPressureSamples,
                diameter,
                incrementalPressureProfile,
                incrementalPressureDistances);
        }
        var completePressureProfile = FreehandStrokeProcessor.CreatePressurePreview(samples, diameter, smoothing: 0);
        var maximumIncrementalDiameterError = incrementalPressureProfile
            .Zip(completePressureProfile)
            .Max(pair => MathF.Abs(pair.First.Diameter - pair.Second.Diameter));
        incrementalPressureSamples[^1] = incrementalPressureSamples[^1] with
        {
            HeldSeconds = incrementalPressureSamples[^1].HeldSeconds + 0.5f
        };
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            incrementalPressureSamples,
            diameter,
            incrementalPressureProfile,
            incrementalPressureDistances);
        var heldPressureProfile = FreehandStrokeProcessor.CreatePressurePreview(
            incrementalPressureSamples,
            diameter,
            smoothing: 0);
        if (incrementalPressureProfile.Count != completePressureProfile.Length
            || incrementalPressureDistances.Count != incrementalPressureProfile.Count
            || maximumIncrementalDiameterError > 2
            || MathF.Abs(incrementalPressureProfile[^1].Diameter - heldPressureProfile[^1].Diameter) > 2)
        {
            throw new InvalidOperationException(
                "Incremental pressure preview diverged from the complete pressure profile: "
                + $"points={incrementalPressureProfile.Count}, diameter_error={maximumIncrementalDiameterError:0.00}.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        var stopwatch = Stopwatch.StartNew();
        var softObjects = scene.AddPressureBrushStroke(
            0,
            samples,
            diameter,
            Color.Coral,
            BrushShape.CreateSoftRound(),
            (uint)samples.Length,
            smoothing: 58,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        stopwatch.Stop();
        const double pressureBrushCommitBudgetMilliseconds = 10;
        var pressureBrushCommitBudgetMet = stopwatch.Elapsed.TotalMilliseconds <= pressureBrushCommitBudgetMilliseconds;
        if (softObjects.Length != 3
            || softObjects.Any(index => scene.ShapeKind[index] != ShapeKind.Path)
            || !softObjects.All(index => scene.FillContainsPoint(index, samples[samples.Length / 2].Point))
            || !pressureBrushCommitBudgetMet)
        {
            throw new InvalidOperationException(
                $"Continuous pressure brush exceeded its interaction budget or lost layered Path fills: "
                + $"elapsed={stopwatch.Elapsed.TotalMilliseconds:0.00} ms.");
        }

        var traditional = new VectorScene();
        traditional.CreateEmpty();
        var traditionalObjects = traditional.AddPressureBrushStroke(
            0,
            samples,
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            (uint)samples.Length,
            smoothing: 58,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        if (traditionalObjects.Length != 1
            || traditional.ShapeKind[traditionalObjects[0]] != ShapeKind.Path
            || !traditional.FillContainsPoint(traditionalObjects[0], samples[samples.Length / 2].Point))
        {
            throw new InvalidOperationException("Traditional pressure brush did not commit a solid Path fill.");
        }

        var cornerSamples = new[]
        {
            new PressureBrushSample(new PointF(-120, -80), 240, 0),
            new PressureBrushSample(new PointF(0, 80), 160, 0.3f),
            new PressureBrushSample(new PointF(120, -80), 240, 0.6f)
        };
        var cornerScene = new VectorScene();
        cornerScene.CreateEmpty();
        var cornerObjects = cornerScene.AddPressureBrushStroke(
            0,
            cornerSamples,
            diameter,
            Color.Coral,
            BrushShape.CreateTraditionalBrush(),
            12,
            smoothing: 70,
            simplifyTolerance: 1,
            frequency: 8,
            continuous: true);
        if (cornerObjects.Length != 1 || !cornerScene.FillContainsPoint(cornerObjects[0], cornerSamples[1].Point))
        {
            throw new InvalidOperationException("Pressure brush left an unfilled gap at a sharp corner.");
        }

        var longPreviewSamples = new PressureBrushSample[16_384];
        for (var index = 0; index < longPreviewSamples.Length; index++)
        {
            longPreviewSamples[index] = new PressureBrushSample(
                new PointF(index * 2, MathF.Sin(index * 0.03f) * 80),
                180 + index % 17 * 20,
                index / 240f);
        }
        const int previewIterations = 64;
        var previewWatch = Stopwatch.StartNew();
        IReadOnlyList<PressureBrushSample> boundedPreview = [];
        PressureBrushPoint[] previewProfile = [];
        for (var iteration = 0; iteration < previewIterations; iteration++)
        {
            boundedPreview = MainForm.LimitDrawingPreview(longPreviewSamples, 256);
            previewProfile = FreehandStrokeProcessor.CreatePressurePreview(boundedPreview, diameter, smoothing: 58);
        }
        previewWatch.Stop();
        var pressurePreviewAverageMilliseconds = previewWatch.Elapsed.TotalMilliseconds / previewIterations;
        if (boundedPreview.Count != 256
            || previewProfile.Length != 256
            || boundedPreview[0].Point != longPreviewSamples[0].Point
            || boundedPreview[^1].Point != longPreviewSamples[^1].Point
            || pressurePreviewAverageMilliseconds > pressureBrushCommitBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Long pressure-brush preview exceeded its interaction budget or lost trajectory endpoints: "
                + $"points={boundedPreview.Count}, profile={previewProfile.Length}, avg={pressurePreviewAverageMilliseconds:0.000} ms.");
        }

        var longTrajectory = new PointF[32_768];
        for (var index = 0; index < longTrajectory.Length; index++)
        {
            longTrajectory[index] = new PointF(
                index * 2,
                MathF.Sin(index * 0.011f) * 160 + MathF.Sin(index * 0.037f) * 35);
        }
        var processedLongTrajectory = FreehandStrokeProcessor.Process(longTrajectory, smoothing: 30, simplifyTolerance: 1);
        var trajectoryStart = processedLongTrajectory[0];
        var trajectoryEnd = processedLongTrajectory[^1];
        var trajectoryDx = trajectoryEnd.X - trajectoryStart.X;
        var trajectoryDy = trajectoryEnd.Y - trajectoryStart.Y;
        var trajectoryLength = Math.Max(0.0001f, MathF.Sqrt(trajectoryDx * trajectoryDx + trajectoryDy * trajectoryDy));
        var maximumCurveDistance = processedLongTrajectory.Max(point => MathF.Abs(
            trajectoryDy * point.X - trajectoryDx * point.Y
            + trajectoryEnd.X * trajectoryStart.Y - trajectoryEnd.Y * trajectoryStart.X) / trajectoryLength);
        if (processedLongTrajectory.Length < 64 || maximumCurveDistance < 120)
        {
            throw new InvalidOperationException(
                "Long brush processing collapsed a curved trajectory into a straight line: "
                + $"source={longTrajectory.Length}, processed={processedLongTrajectory.Length}, curve={maximumCurveDistance:0.00}.");
        }

        var incrementalPreviewPath = new PointF[769];
        for (var index = 0; index < incrementalPreviewPath.Length; index++)
        {
            incrementalPreviewPath[index] = new PointF(
                index * 12,
                MathF.Sin(index * 0.075f) * 150 + MathF.Sin(index * 0.19f) * 28);
        }
        var incrementalPreviewScene = new VectorScene();
        incrementalPreviewScene.CreateEmpty();
        var incrementalBrush = BrushShape.CreateSoftRound();
        var stableCoverage = new List<PointF>();
        var processedPreviewSamples = 0;
        while (processedPreviewSamples < incrementalPreviewPath.Length)
        {
            var nextSample = Math.Min(incrementalPreviewPath.Length, processedPreviewSamples + 32);
            var segmentStart = processedPreviewSamples == 0 ? 0 : processedPreviewSamples - 1;
            var additions = incrementalPreviewScene.AddSoftBrushStroke(
                0,
                incrementalPreviewPath[segmentStart..nextSample],
                diameter,
                Color.Teal,
                incrementalBrush,
                (uint)Math.Max(3, nextSample - segmentStart),
                frequency: 8,
                continuous: true);
            incrementalPreviewScene.MergeSameColorFillsAroundNewObjects(additions, connectNearby: false, frame: 0);
            processedPreviewSamples = nextSample;

            bool PreviewContains(PointF point) => Enumerable.Range(0, incrementalPreviewScene.ObjectCount)
                .Any(objectIndex => incrementalPreviewScene.FillContainsPoint(objectIndex, point));

            if (stableCoverage.Count == 0)
            {
                var bounds = incrementalPreviewScene.GetObjectWorldBounds(0);
                var gridStep = Math.Max(8, diameter / 6);
                for (var y = bounds.Top + gridStep * 0.5f; y < bounds.Bottom; y += gridStep)
                {
                    for (var x = bounds.Left + gridStep * 0.5f; x < bounds.Right; x += gridStep)
                    {
                        var probe = new PointF(x, y);
                        if (PreviewContains(probe)) stableCoverage.Add(probe);
                    }
                }
            }
            else if (stableCoverage.Any(point => !PreviewContains(point)))
            {
                throw new InvalidOperationException("Incremental brush preview changed an already covered area.");
            }
        }

        if (stableCoverage.Count < 12
            || incrementalPreviewScene.ObjectCount != incrementalBrush.Layers.Count
            || incrementalPreviewPath.Where((_, index) => index % 48 == 0)
                .Any(point => !Enumerable.Range(0, incrementalPreviewScene.ObjectCount)
                    .Any(objectIndex => incrementalPreviewScene.FillContainsPoint(objectIndex, point))))
        {
            throw new InvalidOperationException("Incremental brush preview did not preserve and extend its accumulated area.");
        }

        Console.WriteLine($"pressure_brush_commit_ms={stopwatch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"pressure_brush_commit_budget_ms={pressureBrushCommitBudgetMilliseconds:0.00}");
        Console.WriteLine($"pressure_brush_commit_budget_met={pressureBrushCommitBudgetMet.ToString().ToLowerInvariant()}");
        Console.WriteLine($"pressure_brush_preview_source_points={longPreviewSamples.Length}");
        Console.WriteLine($"pressure_brush_preview_points={boundedPreview.Count}");
        Console.WriteLine($"pressure_brush_preview_avg_ms={pressurePreviewAverageMilliseconds:0.000}");
        Console.WriteLine($"pressure_brush_preview_budget_met={(pressurePreviewAverageMilliseconds <= pressureBrushCommitBudgetMilliseconds).ToString().ToLowerInvariant()}");
    }

    private static void RunBrushWorldScaleRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene) { Size = new Size(960, 540) };
        stage.SetVisibleWorldWidth(VectorUnits.DefaultVisibleWorldWidth);
        var diameter = VectorUnits.StrokePointsToUnits(18);
        var initialScreenDiameter = stage.WorldLengthToScreen(diameter);
        stage.SetBrushTipCursor(new Point(stage.Width / 2, stage.Height / 2), BrushShape.CreateSoftRound(), diameter, eraser: false);
        var initialCursorRadius = stage.BrushTipCursorRadiusPixels;
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 2);
        var zoomedScreenDiameter = stage.WorldLengthToScreen(diameter);
        var zoomedCursorRadius = stage.BrushTipCursorRadiusPixels;
        var restoredWorldDiameter = stage.ScreenLengthToWorld(zoomedScreenDiameter);
        if (Math.Abs(zoomedScreenDiameter - initialScreenDiameter * 2) > 0.001f
            || Math.Abs(restoredWorldDiameter - diameter) > 0.001f
            || Math.Abs(zoomedCursorRadius - initialCursorRadius * 2) > 0.001f)
        {
            throw new InvalidOperationException("Brush size did not retain its world-unit diameter across stage zoom changes.");
        }

        Console.WriteLine("brush_world_scale_regression=ok");
    }

    private static void RunBrushEraserRegression()
    {
        var shape = BrushShape.CreateSoftRound();
        var diameter = VectorUnits.StrokePointsToUnits(24);
        var center = new[] { PointF.Empty };

        static bool LineTouchesPoint(VectorScene scene, PointF point)
        {
            for (var index = 0; index < scene.ObjectCount; index++)
            {
                if (scene.ShapeKind[index] != ShapeKind.Line
                    || !scene.TryGetClosestPointOnLine(index, point, out _, out _, out var distance))
                {
                    continue;
                }

                if (distance <= scene.Stroke[index] * 0.5f + DrawingTopologyRules.UnitIntersectionTolerance) return true;
            }

            return false;
        }

        static PointF EvaluateCubic(PointF start, PointF control1, PointF control2, PointF end, float amount)
        {
            var inverse = 1f - amount;
            var inverseSquared = inverse * inverse;
            var amountSquared = amount * amount;
            return new PointF(
                inverseSquared * inverse * start.X
                    + 3f * inverseSquared * amount * control1.X
                    + 3f * inverse * amountSquared * control2.X
                    + amountSquared * amount * end.X,
                inverseSquared * inverse * start.Y
                    + 3f * inverseSquared * amount * control1.Y
                    + 3f * inverse * amountSquared * control2.Y
                    + amountSquared * amount * end.Y);
        }

        static VectorScene CreateTargetOptionScene(float stroke)
        {
            var scene = new VectorScene();
            scene.CreateEmpty();
            scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(600, 400),
                0,
                0,
                Color.Coral,
                Color.Transparent,
                12,
                ShapeKind.Rectangle);
            scene.AddLineSegment(
                0,
                new PointF(-300, 0),
                new PointF(300, 0),
                stroke,
                Color.Transparent,
                Color.Aqua,
                12);
            return scene;
        }

        var fillScene = new VectorScene();
        fillScene.CreateEmpty();
        fillScene.AddObject(0, PointF.Empty, new SizeF(400, 240), 0, 0, Color.Coral, Color.Transparent, 12, ShapeKind.Rectangle);
        if (!fillScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: true)
            || Enumerable.Range(0, fillScene.ObjectCount).Any(index => fillScene.FillContainsPoint(index, PointF.Empty)))
        {
            throw new InvalidOperationException("Brush eraser did not punch a local hole through a fill.");
        }

        var shapeGradientScene = new VectorScene();
        shapeGradientScene.CreateEmpty();
        var shapeGradient = shapeGradientScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(400, 240),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var shapeGradientStops = new[]
        {
            new GradientStop(0, Color.Gold),
            new GradientStop(0.5f, Color.Coral),
            new GradientStop(1, Color.RoyalBlue)
        };
        var originalShapeMapping = shapeGradientScene.GetObjectBoundaryContours(shapeGradient);
        shapeGradientScene.SetGradientPaint(
            shapeGradient,
            GradientKind.ShapeRadial,
            shapeGradientStops,
            PointF.Empty,
            new PointF(200, 0));
        shapeGradientScene.SetShapeGradientMapping(shapeGradient, originalShapeMapping);
        var erasedShapeGradient = shapeGradientScene.EraseWithBrushStroke(
            0,
            center,
            diameter,
            shape,
            eraseLines: false,
            eraseFills: true);
        var shapeGradientFragments = Enumerable.Range(0, shapeGradientScene.ObjectCount)
            .Where(index => shapeGradientScene.HasGradient(index)
                && shapeGradientScene.GetGradientKind(index) == GradientKind.ShapeRadial)
            .ToArray();
        var shapeGradientStopsPreserved = shapeGradientFragments.All(index =>
            shapeGradientScene.GetGradientStops(index).SequenceEqual(shapeGradientStops));
        var shapeGradientCentersMoved = shapeGradientFragments.All(index =>
            shapeGradientScene.GetGradientStart(index) != PointF.Empty);
        var shapeGradientCentersInside = shapeGradientFragments.All(index =>
            shapeGradientScene.FillContainsPoint(index, shapeGradientScene.GetGradientStart(index)));
        var originalShapeMappingPoints = originalShapeMapping
            .SelectMany(contour => contour)
            .ToHashSet();
        var shapeGradientMappingsUpdated = shapeGradientFragments.All(index =>
        {
            if (!shapeGradientScene.TryGetShapeGradientMappingWorldContours(index, out var mapping)) return false;
            var mappingPoints = mapping.SelectMany(contour => contour).ToHashSet();
            var boundaryPoints = shapeGradientScene.GetObjectBoundaryContours(index)
                .SelectMany(contour => contour)
                .ToHashSet();
            return mappingPoints.SetEquals(boundaryPoints)
                && !mappingPoints.SetEquals(originalShapeMappingPoints);
        });
        var updatedShapeGradients = shapeGradientFragments.Length > 0
            && shapeGradientStopsPreserved
            && shapeGradientCentersMoved
            && shapeGradientCentersInside
            && shapeGradientMappingsUpdated;
        if (!erasedShapeGradient || !updatedShapeGradients)
        {
            throw new InvalidOperationException(
                $"Erasing a shape-gradient fill did not recalculate each remaining gradient: erased={erasedShapeGradient}, "
                + $"fragments={shapeGradientFragments.Length}, stops={shapeGradientStopsPreserved}, moved={shapeGradientCentersMoved}, "
                + $"inside={shapeGradientCentersInside}, mappings={shapeGradientMappingsUpdated}.");
        }

        var lineScene = new VectorScene();
        lineScene.CreateEmpty(2);
        var lineStroke = VectorUnits.StrokePointsToUnits(10);
        var lineStops = new[]
        {
            new GradientStop(0, Color.Aqua),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.Coral)
        };
        var lineGradientStart = new PointF(-240, 0);
        var lineGradientEnd = new PointF(240, 0);
        var lineSource = lineScene.AddCubicCurveSegment(
            1,
            new PointF(-240, 0),
            new PointF(-160, -180),
            new PointF(160, 180),
            new PointF(240, 0),
            lineStroke,
            Color.Transparent,
            Color.Aqua,
            17,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Sharp);
        lineScene.SetGradientPaint(
            lineSource,
            GradientKind.Linear,
            lineStops,
            lineGradientStart,
            lineGradientEnd);
        var lineOrder = lineScene.ObjectOrder[lineSource];
        var lineKeyframe = lineScene.ObjectKeyframeFrame[lineSource];
        if (!lineScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted portion of a curved line.");
        }

        var lineFragments = Enumerable.Range(0, lineScene.ObjectCount)
            .Where(index => lineScene.ShapeKind[index] == ShapeKind.Line)
            .OrderBy(index => lineScene.ObjectSubOrder[index])
            .ToArray();
        var lineTopologyPreserved = lineFragments.Length == 2
            && lineFragments.Length == lineScene.ObjectCount
            && !LineTouchesPoint(lineScene, PointF.Empty)
            && lineFragments.All(index => lineScene.ObjectLayer[index] == 1
                && lineScene.Stroke[index] == lineStroke
                && lineScene.StrokeArgb[index] == Color.Aqua.ToArgb()
                && lineScene.ObjectOrder[index] == lineOrder
                && lineScene.ObjectKeyframeFrame[index] == lineKeyframe
                && lineScene.HasGradient(index)
                && lineScene.GetGradientKind(index) == GradientKind.Linear
                && lineScene.GetGradientStops(index).SequenceEqual(lineStops)
                && lineScene.GetGradientStart(index) == lineGradientStart
                && lineScene.GetGradientEnd(index) == lineGradientEnd)
            && lineScene.GetLineEndpointStyle(lineFragments[0], startEndpoint: true) == LineEndpointStyle.Sharp
            && lineScene.GetLineEndpointStyle(lineFragments[0], startEndpoint: false) == LineEndpointStyle.Round
            && lineScene.GetLineEndpointStyle(lineFragments[1], startEndpoint: true) == LineEndpointStyle.Round
            && lineScene.GetLineEndpointStyle(lineFragments[1], startEndpoint: false) == LineEndpointStyle.Sharp
            && lineScene.ObjectSubOrder[lineFragments[1]] > lineScene.ObjectSubOrder[lineFragments[0]]
            && lineFragments.Sum(index => (long)lineScene.AtomCount[index]) == 17;
        if (!lineTopologyPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser converted or lost curved-line topology, material, endpoints, ownership, order, or atoms.");
        }

        lineScene.TryGetLineBezierPart(
            lineFragments[0],
            0,
            1,
            out var firstFragmentStart,
            out var firstFragmentControl1,
            out var firstFragmentControl2,
            out var firstFragmentEnd);
        var secondContactPoint = EvaluateCubic(
            firstFragmentStart,
            firstFragmentControl1,
            firstFragmentControl2,
            firstFragmentEnd,
            0.5f);
        var secondContact = new[] { secondContactPoint };
        if (!lineScene.EraseWithBrushStroke(0, secondContact, diameter, shape, eraseLines: true, eraseFills: false)
            || Enumerable.Range(0, lineScene.ObjectCount).Any(index => lineScene.ShapeKind[index] != ShapeKind.Line)
            || LineTouchesPoint(lineScene, secondContactPoint))
        {
            throw new InvalidOperationException("A previously erased line could not be erased again while remaining a line.");
        }

        var freeformScene = new VectorScene();
        freeformScene.CreateEmpty();
        var freeformStroke = VectorUnits.StrokePointsToUnits(4);
        freeformScene.AddFreehandStroke(
            0,
            [
                new PointF(-420, -80),
                new PointF(-260, 70),
                new PointF(-120, -60),
                PointF.Empty,
                new PointF(120, 60),
                new PointF(260, -70),
                new PointF(420, 80)
            ],
            freeformStroke,
            Color.MediumPurple,
            brushStroke: false,
            23);
        if (!freeformScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false)
            || freeformScene.ObjectCount != 2
            || Enumerable.Range(0, freeformScene.ObjectCount).Any(index =>
                freeformScene.ShapeKind[index] != ShapeKind.Freeform
                || freeformScene.Stroke[index] != freeformStroke
                || freeformScene.StrokeArgb[index] != Color.MediumPurple.ToArgb())
            || freeformScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 0.1f).IsValid)
        {
            throw new InvalidOperationException("Brush eraser converted or lost editable freeform stroke topology.");
        }

        var mixingScene = new VectorScene();
        mixingScene.CreateEmpty(2);
        if (!mixingScene.InsertTimelineBlankKeyframe(1, 7))
        {
            throw new InvalidOperationException("Mixing-brush eraser regression could not create its target keyframe.");
        }
        mixingScene.EditFrame = 7;
        var mixingSamples = Enumerable.Range(-4, 9)
            .Select(index => new MixingBrushTrajectorySample(
                new PointF(index * 40, 0),
                50,
                Color.FromArgb(255, 80 + (index + 4) * 12, 120, 220 - (index + 4) * 10).ToArgb()))
            .ToArray();
        var mixingSource = mixingScene.AddMixingBrushStroke(1, mixingSamples, 17);
        mixingScene.ObjectSubOrder[mixingSource] = 0.375;
        var mixingOrder = mixingScene.ObjectOrder[mixingSource];
        if (!mixingScene.EraseWithBrushStroke(
                7,
                center,
                50,
                shape,
                eraseLines: false,
                eraseFills: true))
        {
            throw new InvalidOperationException("Brush eraser did not modify the contacted mixing-brush trajectory.");
        }

        var mixingFragments = Enumerable.Range(0, mixingScene.ObjectCount)
            .OrderBy(index => mixingScene.ObjectSubOrder[index])
            .ToArray();
        var mixingFragmentSamples = mixingFragments
            .Select(index => mixingScene.TryGetMixingStrokeWorldSamples(index, out var fragmentSamples)
                ? fragmentSamples
                : Array.Empty<MixingBrushTrajectorySample>())
            .ToArray();
        var mixingTrajectoryPreserved = mixingFragments.Length == 2
            && mixingFragments.All(index => mixingScene.ShapeKind[index] == ShapeKind.MixingStroke
                && mixingScene.ObjectLayer[index] == 1
                && mixingScene.ObjectKeyframeFrame[index] == 7
                && mixingScene.ObjectOrder[index] == mixingOrder)
            && mixingScene.ObjectSubOrder[mixingFragments[0]] < mixingScene.ObjectSubOrder[mixingFragments[1]]
            && mixingFragmentSamples.All(samples => samples.Length == 4)
            && mixingFragmentSamples.SelectMany(samples => samples).All(sample => sample.Point != PointF.Empty)
            && mixingFragmentSamples.SelectMany(samples => samples).Select(sample => sample.Argb)
                .SequenceEqual(mixingSamples.Where(sample => sample.Point != PointF.Empty).Select(sample => sample.Argb))
            && mixingFragmentSamples.SelectMany(samples => samples).Any(sample => sample.Diameter < 50)
            && !mixingFragments.Any(index => mixingScene.TrySampleMixingStrokeColor(index, PointF.Empty, out _))
            && mixingFragments.Sum(index => (long)mixingScene.AtomCount[index]) == 17;
        if (!mixingTrajectoryPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser did not split, trim, or preserve the ordering and metadata of a mixing-brush trajectory.");
        }

        var mixingStrokeOnlyScene = new VectorScene();
        mixingStrokeOnlyScene.CreateEmpty();
        mixingStrokeOnlyScene.AddMixingBrushStroke(0, mixingSamples, 17);
        if (mixingStrokeOnlyScene.EraseWithBrushStroke(
                0,
                center,
                50,
                shape,
                eraseLines: true,
                eraseFills: false)
            || mixingStrokeOnlyScene.ObjectCount != 1)
        {
            throw new InvalidOperationException("Stroke-only erasing modified a mixing-brush fill trajectory.");
        }

        var outlinedRectangleScene = new VectorScene();
        outlinedRectangleScene.CreateEmpty(3);
        if (!outlinedRectangleScene.InsertTimelineBlankKeyframe(2, 9))
        {
            throw new InvalidOperationException("Brush eraser regression could not create an outlined-shape keyframe.");
        }

        outlinedRectangleScene.EditFrame = 9;
        var boundaryStroke = VectorUnits.StrokePointsToUnits(6);
        var boundaryEraserDiameter = VectorUnits.StrokePointsToUnits(4);
        var boundarySource = outlinedRectangleScene.AddObject(
            2,
            PointF.Empty,
            new SizeF(600, 400),
            0,
            boundaryStroke,
            Color.Coral,
            Color.Teal,
            60,
            ShapeKind.Rectangle);
        outlinedRectangleScene.ObjectSubOrder[boundarySource] = 0.375;
        var boundaryOrder = outlinedRectangleScene.ObjectOrder[boundarySource];
        var boundaryContact = new PointF(0, -200);
        if (!outlinedRectangleScene.EraseWithBrushStroke(
                9,
                [boundaryContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted outlined-rectangle boundary.");
        }

        var rectangleFill = Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
            .SingleOrDefault(index => outlinedRectangleScene.HasFill(index));
        var rectangleLines = Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
            .Where(index => outlinedRectangleScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var rectangleRoundEndpoints = rectangleLines.Sum(index =>
            (outlinedRectangleScene.GetLineEndpointStyle(index, startEndpoint: true) == LineEndpointStyle.Round ? 1 : 0)
            + (outlinedRectangleScene.GetLineEndpointStyle(index, startEndpoint: false) == LineEndpointStyle.Round ? 1 : 0));
        var rectangleBoundaryPreserved = rectangleFill >= 0
            && outlinedRectangleScene.ObjectCount == 6
            && rectangleLines.Length == 5
            && outlinedRectangleScene.FillContainsPoint(rectangleFill, PointF.Empty)
            && outlinedRectangleScene.Argb[rectangleFill] == Color.Coral.ToArgb()
            && outlinedRectangleScene.Stroke[rectangleFill] == 0
            && outlinedRectangleScene.ObjectLayer[rectangleFill] == 2
            && outlinedRectangleScene.ObjectKeyframeFrame[rectangleFill] == 9
            && outlinedRectangleScene.ObjectOrder[rectangleFill] == boundaryOrder
            && outlinedRectangleScene.ObjectSubOrder[rectangleFill] == 0.375
            && rectangleLines.All(index => outlinedRectangleScene.ObjectLayer[index] == 2
                && outlinedRectangleScene.ObjectKeyframeFrame[index] == 9
                && outlinedRectangleScene.ObjectOrder[index] == boundaryOrder
                && outlinedRectangleScene.ObjectSubOrder[index] > outlinedRectangleScene.ObjectSubOrder[rectangleFill]
                && outlinedRectangleScene.Stroke[index] == boundaryStroke
                && outlinedRectangleScene.StrokeArgb[index] == Color.Teal.ToArgb())
            && rectangleRoundEndpoints == 2
            && !LineTouchesPoint(outlinedRectangleScene, boundaryContact)
            && LineTouchesPoint(outlinedRectangleScene, new PointF(0, 200))
            && !Enumerable.Range(0, outlinedRectangleScene.ObjectCount).Any(index =>
                outlinedRectangleScene.HasFill(index)
                && outlinedRectangleScene.Argb[index] == Color.Teal.ToArgb())
            && Enumerable.Range(0, outlinedRectangleScene.ObjectCount)
                .Sum(index => (long)outlinedRectangleScene.AtomCount[index]) == 60;
        if (!rectangleBoundaryPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser converted an outlined-rectangle boundary to fill or lost its fill, style, cel, order, endpoints, or atoms.");
        }

        var secondBoundaryContact = new PointF(0, 200);
        if (!outlinedRectangleScene.EraseWithBrushStroke(
                9,
                [secondBoundaryContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false)
            || Enumerable.Range(0, outlinedRectangleScene.ObjectCount).Any(index =>
                !outlinedRectangleScene.HasFill(index)
                && outlinedRectangleScene.ShapeKind[index] != ShapeKind.Line)
            || LineTouchesPoint(outlinedRectangleScene, secondBoundaryContact))
        {
            throw new InvalidOperationException(
                "A materialized outlined-shape boundary could not be erased again while remaining editable lines.");
        }

        var outlinedEllipseScene = new VectorScene();
        outlinedEllipseScene.CreateEmpty(2);
        var ellipseStroke = VectorUnits.StrokePointsToUnits(5);
        var ellipseSource = outlinedEllipseScene.AddObject(
            1,
            PointF.Empty,
            new SizeF(640, 360),
            0,
            ellipseStroke,
            Color.Gold,
            Color.RoyalBlue,
            72,
            ShapeKind.Ellipse);
        outlinedEllipseScene.ObjectSubOrder[ellipseSource] = 0.625;
        var ellipseOrder = outlinedEllipseScene.ObjectOrder[ellipseSource];
        var ellipseParts = outlinedEllipseScene.GetEditableFillBezierSegmentParts(ellipseSource);
        var ellipseContact = EvaluateCubic(
            ellipseParts[0].Start,
            ellipseParts[0].Control1,
            ellipseParts[0].Control2,
            ellipseParts[0].End,
            0.5f);
        if (!outlinedEllipseScene.EraseWithBrushStroke(
                0,
                [ellipseContact],
                boundaryEraserDiameter,
                shape,
                eraseLines: true,
                eraseFills: false))
        {
            throw new InvalidOperationException("Brush eraser did not remove the contacted curved boundary.");
        }

        var ellipseFill = Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
            .SingleOrDefault(index => outlinedEllipseScene.HasFill(index));
        var ellipseLines = Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
            .Where(index => outlinedEllipseScene.ShapeKind[index] == ShapeKind.Line)
            .ToArray();
        var ellipseBoundaryPreserved = ellipseFill >= 0
            && outlinedEllipseScene.ObjectCount == 6
            && ellipseLines.Length == 5
            && outlinedEllipseScene.FillContainsPoint(ellipseFill, PointF.Empty)
            && outlinedEllipseScene.TryGetPathBezierWorldContours(ellipseFill, out var ellipseContours)
            && ellipseContours.Length == 1
            && ellipseContours[0].Length == 4
            && ellipseLines.Any(index => !outlinedEllipseScene.IsLineStraight(index))
            && ellipseLines.All(index => outlinedEllipseScene.ObjectLayer[index] == 1
                && outlinedEllipseScene.ObjectKeyframeFrame[index] == 0
                && outlinedEllipseScene.ObjectOrder[index] == ellipseOrder
                && outlinedEllipseScene.ObjectSubOrder[index] > outlinedEllipseScene.ObjectSubOrder[ellipseFill]
                && outlinedEllipseScene.Stroke[index] == ellipseStroke
                && outlinedEllipseScene.StrokeArgb[index] == Color.RoyalBlue.ToArgb())
            && !LineTouchesPoint(outlinedEllipseScene, ellipseContact)
            && !Enumerable.Range(0, outlinedEllipseScene.ObjectCount).Any(index =>
                outlinedEllipseScene.HasFill(index)
                && outlinedEllipseScene.Argb[index] == Color.RoyalBlue.ToArgb())
            && Enumerable.Range(0, outlinedEllipseScene.ObjectCount)
                .Sum(index => (long)outlinedEllipseScene.AtomCount[index]) == 72;
        if (!ellipseBoundaryPreserved)
        {
            throw new InvalidOperationException(
                "Brush eraser flattened or converted an outlined ellipse boundary, or lost its exact fill and metadata.");
        }

        var targetStroke = VectorUnits.StrokePointsToUnits(8);
        var neitherScene = CreateTargetOptionScene(targetStroke);
        if (neitherScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: false)
            || neitherScene.ObjectCount != 2
            || !neitherScene.FillContainsPoint(0, PointF.Empty)
            || !LineTouchesPoint(neitherScene, PointF.Empty))
        {
            throw new InvalidOperationException("Disabling both eraser targets modified the scene.");
        }

        var fillOnlyScene = CreateTargetOptionScene(targetStroke);
        if (!fillOnlyScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: false, eraseFills: true)
            || Enumerable.Range(0, fillOnlyScene.ObjectCount).Any(index => fillOnlyScene.FillContainsPoint(index, PointF.Empty))
            || !LineTouchesPoint(fillOnlyScene, PointF.Empty))
        {
            throw new InvalidOperationException("Fill-only erasing did not preserve the contacted stroke.");
        }

        var strokeOnlyScene = CreateTargetOptionScene(targetStroke);
        if (!strokeOnlyScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: false)
            || !Enumerable.Range(0, strokeOnlyScene.ObjectCount).Any(index => strokeOnlyScene.FillContainsPoint(index, PointF.Empty))
            || LineTouchesPoint(strokeOnlyScene, PointF.Empty)
            || Enumerable.Range(0, strokeOnlyScene.ObjectCount).Any(index =>
                strokeOnlyScene.Stroke[index] > 0 && strokeOnlyScene.ShapeKind[index] is not ShapeKind.Line))
        {
            throw new InvalidOperationException("Stroke-only erasing did not preserve the contacted fill or editable line type.");
        }

        var bothScene = CreateTargetOptionScene(targetStroke);
        if (!bothScene.EraseWithBrushStroke(0, center, diameter, shape, eraseLines: true, eraseFills: true)
            || Enumerable.Range(0, bothScene.ObjectCount).Any(index => bothScene.FillContainsPoint(index, PointF.Empty))
            || LineTouchesPoint(bothScene, PointF.Empty))
        {
            throw new InvalidOperationException("Combined stroke-and-fill erasing did not erase both selected targets.");
        }

        if (VectorUnits.MinimumStrokePoints != 0.1f
            || VectorUnits.MinimumStrokeUnits != VectorUnits.StrokePointsToUnits(0.1f))
        {
            throw new InvalidOperationException("The shared minimum stroke width is not 0.1 pt.");
        }

        using (var material = new MaterialEditorPanel())
        using (var brushTip = new BrushTipPanel())
        {
            var materialWidth = typeof(MaterialEditorPanel).GetField(
                "_strokeWidth",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(material)
                as ModernNumericUpDown;
            var brushSize = typeof(BrushTipPanel).GetField(
                "_size",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(brushTip)
                as ModernNumericUpDown;
            material.StrokeWidth = 0;
            brushTip.SetBrushStrokeSettings(8, continuous: true, sizePoints: 0, pressureSmoothing: 70);
            if (materialWidth is null
                || brushSize is null
                || materialWidth.Minimum != 0.1m
                || materialWidth.Increment != 0.1m
                || brushSize.Minimum != 0.1m
                || brushSize.Increment != 0.1m
                || material.StrokeWidth != 0.1f
                || brushTip.SizePoints != 0.1f)
            {
                throw new InvalidOperationException("Stroke-width controls did not clamp or step at 0.1 pt.");
            }
        }

        var minimumScene = new VectorScene();
        minimumScene.CreateEmpty();
        var minimumFreeform = minimumScene.AddFreehandStroke(
            0,
            [new PointF(-20, 0), new PointF(20, 0)],
            0,
            Color.White,
            brushStroke: false,
            4);
        using (var previewStage = new StageControl(minimumScene))
        {
            previewStage.SetFreehandPreview([PointF.Empty], Color.White, 0);
            if (minimumFreeform < 0
                || minimumScene.Stroke[minimumFreeform] != VectorUnits.MinimumStrokeUnits
                || previewStage.FreehandPreviewStroke != VectorUnits.MinimumStrokeUnits)
            {
                throw new InvalidOperationException("Freeform storage or preview did not preserve the 0.1 pt minimum stroke.");
            }
        }

        var minimumEraserScene = new VectorScene();
        minimumEraserScene.CreateEmpty();
        minimumEraserScene.AddLineSegment(
            0,
            new PointF(-20, 0),
            new PointF(20, 0),
            VectorUnits.MinimumStrokeUnits,
            Color.Transparent,
            Color.White,
            6);
        var minimumErased = minimumEraserScene.EraseWithBrushStroke(
            0,
            center,
            VectorUnits.MinimumStrokeUnits,
            shape,
            eraseLines: true,
            eraseFills: false);
        var minimumFragmentsValid = Enumerable.Range(0, minimumEraserScene.ObjectCount).All(index =>
            minimumEraserScene.ShapeKind[index] == ShapeKind.Line
            && minimumEraserScene.Stroke[index] == VectorUnits.MinimumStrokeUnits);
        var minimumCenterTouched = LineTouchesPoint(minimumEraserScene, PointF.Empty);
        if (!minimumErased
            || minimumEraserScene.ObjectCount != 2
            || !minimumFragmentsValid
            || minimumCenterTouched)
        {
            var minimumFragments = string.Join(",", Enumerable.Range(0, minimumEraserScene.ObjectCount)
                .Select(index => $"{minimumEraserScene.ShapeKind[index]}:{minimumEraserScene.Stroke[index]:0.###}"));
            throw new InvalidOperationException(
                "The 0.1 pt eraser path was discontinuous or converted thin line fragments: "
                + $"erased={minimumErased}, count={minimumEraserScene.ObjectCount}, fragments={minimumFragments}, "
                + $"centerTouched={minimumCenterTouched}.");
        }

        Console.WriteLine("brush_eraser_regression=ok");
    }

}
