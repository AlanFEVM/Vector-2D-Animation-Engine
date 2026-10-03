using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunMixingBrushRegression()
    {
        var clamped = new MixingBrushSettings(
            (BrushMixingMode)byte.MaxValue,
            Strength: -1f,
            Viscosity: float.NaN,
            PaintLoad: 2f,
            Influence: 2f);
        if (clamped.Mode != BrushMixingMode.Optical
            || clamped.Strength != 0f
            || clamped.Viscosity != 0f
            || clamped.PaintLoad != 1f
            || clamped.Influence != 1f)
        {
            throw new InvalidOperationException("Mixing-brush settings were not normalized to their supported ranges.");
        }

        var opticalMidpoint = PaintColorMixer.Mix(Color.Black, Color.White, 0.5f, BrushMixingMode.Optical);
        var opticalYellowBlue = PaintColorMixer.Mix(Color.Yellow, Color.Blue, 0.5f, BrushMixingMode.Optical);
        var pigmentYellowBlue = PaintColorMixer.Mix(Color.Yellow, Color.Blue, 0.5f, BrushMixingMode.Pigment);
        if (opticalMidpoint.R is < 187 or > 189
            || opticalMidpoint.G != opticalMidpoint.R
            || opticalMidpoint.B != opticalMidpoint.R
            || pigmentYellowBlue.G <= pigmentYellowBlue.R
            || pigmentYellowBlue.G <= pigmentYellowBlue.B
            || ChannelSum(pigmentYellowBlue) >= ChannelSum(opticalYellowBlue))
        {
            throw new InvalidOperationException(
                "Optical or pigment mixing did not preserve its expected color model: "
                + $"optical-mid={opticalMidpoint}, optical-yb={opticalYellowBlue}, pigment-yb={pigmentYellowBlue}.");
        }

        foreach (var primary in new[] { Color.Red, Color.Lime, Color.Blue })
        {
            var roundTrip = PaintColorMixer.PigmentStateToColor(
                PaintColorMixer.CreatePigmentState(primary),
                primary.A);
            var sameColorMix = PaintColorMixer.Mix(
                primary,
                primary,
                0.5f,
                BrushMixingMode.Pigment);
            if (!SameRgb(roundTrip, primary) || !SameRgb(sameColorMix, primary))
            {
                throw new InvalidOperationException(
                    "Pigment primary calibration introduced display-channel contamination: "
                    + $"primary={primary}, round-trip={roundTrip}, same-color={sameColorMix}.");
            }
        }

        var translucentPigmentRed = PaintColorMixer.Mix(
            Color.FromArgb(64, Color.Red),
            Color.FromArgb(192, Color.Red),
            0.5f,
            BrushMixingMode.Pigment);
        if (!SameRgb(translucentPigmentRed, Color.Red) || translucentPigmentRed.A != 128)
        {
            throw new InvalidOperationException(
                $"Pigment calibration changed pure-red alpha semantics: {translucentPigmentRed}.");
        }

        var sparsePath = new[] { PointF.Empty, new PointF(100, 0) };
        var densePath = Enumerable.Range(0, 21).Select(index => new PointF(index * 5, 0)).ToArray();
        Color? SurfaceAt(PointF point) => point.X < 50 ? Color.Gold : Color.RoyalBlue;
        var settings = new MixingBrushSettings(BrushMixingMode.Optical, 0.72f, 0.4f, 0.25f);
        var sparseProfile = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Coral,
            settings,
            SurfaceAt,
            samplingDistance: 10f);
        var denseProfile = MixingBrushProcessor.CreateColorProfile(
            densePath,
            Color.Coral,
            settings,
            SurfaceAt,
            samplingDistance: 10f);
        if (!sparseProfile.SequenceEqual(denseProfile))
        {
            throw new InvalidOperationException("Mixing-brush color pickup depended on pointer event density.");
        }

        var noMix = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Coral,
            settings with { Strength = 0f },
            _ => Color.RoyalBlue,
            samplingDistance: 10f);
        var transparentSurface = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Coral,
            settings,
            _ => Color.Transparent,
            samplingDistance: 10f);
        var lowViscosity = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Red,
            settings with { Viscosity = 0f, PaintLoad = 0f },
            _ => Color.Blue,
            samplingDistance: 10f);
        var highViscosity = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Red,
            settings with { Viscosity = 1f, PaintLoad = 0f },
            _ => Color.Blue,
            samplingDistance: 10f);
        var highPaintLoad = MixingBrushProcessor.CreateColorProfile(
            sparsePath,
            Color.Red,
            settings with { Viscosity = 0f, PaintLoad = 1f },
            _ => Color.Blue,
            samplingDistance: 10f);
        if (noMix.Any(sample => !SameRgb(Color.FromArgb(sample.Argb), Color.Coral)
                || Color.FromArgb(sample.Argb).A is <= 0 or >= 255)
            || !transparentSurface.SequenceEqual(noMix)
            || Color.FromArgb(lowViscosity[^1].Argb).B <= Color.FromArgb(highViscosity[^1].Argb).B
            || Color.FromArgb(lowViscosity[^1].Argb).B <= Color.FromArgb(highPaintLoad[^1].Argb).B)
        {
            throw new InvalidOperationException("Strength, viscosity, paint load, or transparent pickup was not monotonic.");
        }

        var longProfile = MixingBrushProcessor.CreateColorProfile(
            [PointF.Empty, new PointF(100, 0)],
            Color.White,
            settings,
            _ => Color.Black,
            samplingDistance: 1f);
        if (longProfile.Length <= MixingBrushProcessor.MaximumColorSamples
            || longProfile[0].NormalizedOffset != 0f
            || longProfile[^1].NormalizedOffset != 1f
            || longProfile[0].Point != PointF.Empty
            || longProfile[^1].Point != new PointF(100, 0))
        {
            throw new InvalidOperationException("Mixing-brush trajectories truncated stable first or last samples.");
        }

        var lowInfluence = MixingBrushProcessor.CreateColorProfile(
            [PointF.Empty, new PointF(20, 0), new PointF(100, 0)],
            Color.Blue,
            settings with { Influence = 0.1f },
            point => point.X <= 20 ? Color.Red : null,
            samplingDistance: 10f);
        var highInfluence = MixingBrushProcessor.CreateColorProfile(
            [PointF.Empty, new PointF(20, 0), new PointF(100, 0)],
            Color.Blue,
            settings with { Influence = 1f },
            point => point.X <= 20 ? Color.Red : null,
            samplingDistance: 10f);
        if (ColorDistance(Color.FromArgb(highInfluence[^1].Argb), Color.Blue)
            >= ColorDistance(Color.FromArgb(lowInfluence[^1].Argb), Color.Blue))
        {
            throw new InvalidOperationException("Mixing-brush influence did not restore loaded paint monotonically.");
        }

        var pigmentSettings = settings with
        {
            Mode = BrushMixingMode.Pigment,
            Strength = 0.62f,
            Viscosity = 0.58f,
            PaintLoad = 0.2f
        };
        Color? PigmentSurface(PointF point) => point.X < 40 ? Color.Gold : Color.RoyalBlue;
        var pigmentSparse = MixingBrushProcessor.CreateColorProfile(
            [PointF.Empty, new PointF(160, 0)],
            Color.Gold,
            pigmentSettings,
            PigmentSurface,
            samplingDistance: 10f);
        var pigmentDense = MixingBrushProcessor.CreateColorProfile(
            Enumerable.Range(0, 33).Select(index => new PointF(index * 5, 0)).ToArray(),
            Color.Gold,
            pigmentSettings,
            PigmentSurface,
            samplingDistance: 10f);
        var pigmentTransition = pigmentSparse
            .Where(sample => sample.Point.X is >= 40 and <= 100)
            .Select(sample => sample.Argb)
            .ToArray();
        if (!pigmentSparse.SequenceEqual(pigmentDense)
            || pigmentTransition.Distinct().Count() < 4
            || Color.FromArgb(pigmentTransition[0]).ToArgb() == Color.RoyalBlue.ToArgb()
            || ColorDistance(Color.FromArgb(pigmentTransition[^1]), Color.RoyalBlue)
                >= ColorDistance(Color.FromArgb(pigmentTransition[0]), Color.RoyalBlue))
        {
            throw new InvalidOperationException(
                "Pigment mixing did not preserve stable fixed-distance transition colors across a paint boundary.");
        }

        RunMixingBrushPaintSamplingRegression();
        RunMixingBrushRegionRegression();
        RunMixingBrushMarqueeRegression();
        RunMixingBrushUndoRegression();
        using (var panel = new MixingBrushSettingsPanel())
        {
            panel.SetSettings(new MixingBrushSettings(BrushMixingMode.Pigment, 0.61f, 0.72f, 0.83f, 0.47f));
            var panelSettings = panel.Settings;
            if (panelSettings.Mode != BrushMixingMode.Pigment
                || Math.Abs(panelSettings.Strength - 0.61f) > 0.001f
                || Math.Abs(panelSettings.Viscosity - 0.72f) > 0.001f
                || Math.Abs(panelSettings.PaintLoad - 0.83f) > 0.001f
                || Math.Abs(panelSettings.Influence - 0.47f) > 0.001f
                || panel.MinimumSize.Width != 240
                || panel.PreferredPanelHeight != 196)
            {
                throw new InvalidOperationException("The mixing-brush settings panel did not retain its compact model state.");
            }
        }

        Console.WriteLine("mixing_brush_regression=ok");
    }

    private static void RunMixingBrushUndoRegression()
    {
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var sceneField = typeof(MainForm).GetField("_scene", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not inspect the drawing scene.");
        var stageField = typeof(MainForm).GetField("_stage", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not inspect Stage focus.");
        var settingsPanelField = typeof(MainForm).GetField("_mixingBrushSettingsPanel", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not inspect the settings panel.");
        var strengthSliderField = typeof(MixingBrushSettingsPanel).GetField("_strength", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not inspect the strength slider.");
        var activateTool = typeof(MainForm).GetMethod("ActivateTool", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not activate the tool.");
        var beginStroke = typeof(MainForm).GetMethod("BeginFreehandStroke", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not start a stroke.");
        var commitStroke = typeof(MainForm).GetMethod("CommitFreehandStroke", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not commit a stroke.");
        var finishPointer = typeof(MainForm).GetMethod("FinishPointerInteraction", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not finish pointer input.");
        var undo = typeof(MainForm).GetMethod("UndoLastEdit", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not invoke undo.");
        var processShortcut = typeof(MainForm).GetMethod("ProcessCmdKey", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush undo regression could not invoke Ctrl+Z.");
        var setElementSelection = typeof(MainForm).GetMethod(
            "SetSelection",
            instanceFlags,
            binder: null,
            types: [typeof(DrawingElementHit), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("Mixing-brush clipboard regression could not set an element selection.");
        var copySelection = typeof(MainForm).GetMethod("CopySelectedObjects", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush clipboard regression could not copy the selection.");
        var pasteSelection = typeof(MainForm).GetMethod("PasteCopiedObjects", instanceFlags)
            ?? throw new InvalidOperationException("Mixing-brush clipboard regression could not paste the selection.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000)
        };
        form.Show();
        Application.DoEvents();

        var scene = sceneField.GetValue(form) as VectorScene
            ?? throw new InvalidOperationException("Mixing-brush undo regression did not find the drawing scene.");
        var stage = stageField.GetValue(form) as StageControl
            ?? throw new InvalidOperationException("Mixing-brush undo regression did not find the Stage.");
        var settingsPanel = settingsPanelField.GetValue(form) as MixingBrushSettingsPanel
            ?? throw new InvalidOperationException("Mixing-brush undo regression did not find the settings panel.");
        var strengthSlider = strengthSliderField.GetValue(settingsPanel) as ModernSlider
            ?? throw new InvalidOperationException("Mixing-brush undo regression did not find the strength slider.");
        var initialObjectCount = scene.ObjectCount;
        activateTool.Invoke(form, [ToolMode.MixingBrush]);
        Application.DoEvents();

        void CommitStroke(PointF world)
        {
            beginStroke.Invoke(form, [Point.Empty, world, float.NaN]);
            commitStroke.Invoke(form, null);
            finishPointer.Invoke(form, null);
            if (scene.ObjectCount != initialObjectCount + 1
                || scene.ShapeKind[scene.ObjectCount - 1] != ShapeKind.MixingStroke)
            {
                throw new InvalidOperationException("Mixing-brush undo regression did not commit one trajectory object.");
            }
        }

        if (!settingsPanel.Visible || !strengthSlider.Focus() || !strengthSlider.Focused)
        {
            throw new InvalidOperationException("Mixing-brush undo regression could not focus a settings slider.");
        }

        CommitStroke(PointF.Empty);
        if (!stage.ContainsFocus || strengthSlider.ContainsFocus)
        {
            throw new InvalidOperationException("Starting a mixing-brush stroke did not return focus to the Stage.");
        }
        if (undo.Invoke(form, null) is not true || scene.ObjectCount != initialObjectCount)
        {
            throw new InvalidOperationException("UndoLastEdit did not restore the pre-mixing-brush snapshot.");
        }

        if (!strengthSlider.Focus() || !strengthSlider.Focused)
        {
            throw new InvalidOperationException("Mixing-brush undo regression could not restore slider focus.");
        }

        CommitStroke(new PointF(40, 20));
        if (!stage.ContainsFocus || strengthSlider.ContainsFocus)
        {
            throw new InvalidOperationException("A subsequent mixing-brush stroke did not reclaim Stage focus.");
        }
        object?[] shortcutArguments = [default(Message), Keys.Control | Keys.Z];
        if (processShortcut.Invoke(form, shortcutArguments) is not true
            || scene.ObjectCount != initialObjectCount)
        {
            throw new InvalidOperationException("Ctrl+Z did not undo a committed mixing-brush trajectory.");
        }

        CommitStroke(PointF.Empty);
        var mergedObject = scene.ObjectCount - 1;
        if (!scene.TryGetMixingBrushWorldRegion(mergedObject, out var firstRegion))
        {
            throw new InvalidOperationException("The first mergeable mixing stroke did not store a region payload.");
        }
        CommitStroke(new PointF(40, 20));
        if (scene.ObjectCount != initialObjectCount + 1
            || !scene.TryGetMixingBrushWorldRegion(scene.ObjectCount - 1, out var twicePaintedRegion)
            || twicePaintedRegion.Vertices.SequenceEqual(firstRegion.Vertices))
        {
            throw new InvalidOperationException(
                "Two consecutive mixing strokes retained separate objects or skipped their final merge.");
        }
        if (undo.Invoke(form, null) is not true
            || scene.ObjectCount != initialObjectCount + 1
            || !scene.TryGetMixingBrushWorldRegion(scene.ObjectCount - 1, out var restoredFirstRegion)
            || !restoredFirstRegion.Vertices.SequenceEqual(firstRegion.Vertices)
            || !restoredFirstRegion.TriangleIndices.SequenceEqual(firstRegion.TriangleIndices))
        {
            throw new InvalidOperationException(
                "Undoing a merged mixing stroke did not preserve the previous single-region result.");
        }
        if (undo.Invoke(form, null) is not true || scene.ObjectCount != initialObjectCount)
        {
            throw new InvalidOperationException(
                "A second undo did not remove the remaining first mixing stroke.");
        }

        var firstArgb = Color.Crimson.ToArgb();
        var secondArgb = Color.RoyalBlue.ToArgb();
        var clipboardVertices = new[]
        {
            new MixingBrushRegionVertex(new PointF(0, 0), firstArgb),
            new MixingBrushRegionVertex(new PointF(100, 0), firstArgb),
            new MixingBrushRegionVertex(new PointF(100, 100), firstArgb),
            new MixingBrushRegionVertex(new PointF(0, 100), firstArgb),
            new MixingBrushRegionVertex(new PointF(400, 0), secondArgb),
            new MixingBrushRegionVertex(new PointF(500, 0), secondArgb),
            new MixingBrushRegionVertex(new PointF(500, 100), secondArgb),
            new MixingBrushRegionVertex(new PointF(400, 100), secondArgb)
        };
        if (!MixingBrushRegionData.TryNormalize(
                clipboardVertices,
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
                out var clipboardRegion))
        {
            throw new InvalidOperationException("Mixing-brush clipboard regression could not build its disconnected region.");
        }
        var clipboardSource = scene.AddOrMergeMixingBrushRegion(
            0,
            0,
            clipboardRegion,
            (uint)(clipboardRegion.Vertices.Length + clipboardRegion.TriangleIndices.Length / 3));
        var clipboardHit = scene.HitTestElement(new PointF(25, 25), 0, toleranceWorld: 1);
        setElementSelection.Invoke(form, [clipboardHit, false]);
        var copied = copySelection.Invoke(form, null) is true;
        var pasted = copied && pasteSelection.Invoke(form, null) is true;
        var pastedRegion = new MixingBrushRegionData([], []);
        var hasPastedRegion = scene.ObjectCount > 0
            && scene.TryGetMixingBrushWorldRegion(scene.ObjectCount - 1, out pastedRegion);
        var pastedContainsSelectedIsland = hasPastedRegion
            && pastedRegion.ContainsPaint(new PointF(25 + 96, 25 + 96));
        var pastedContainsUnselectedIsland = hasPastedRegion
            && pastedRegion.ContainsPaint(new PointF(425 + 96, 25 + 96));
        if (!clipboardSource.Success
            || !clipboardHit.IsValid
            || !copied
            || !pasted
            || scene.ObjectCount != initialObjectCount + 2
            || !hasPastedRegion
            || !pastedContainsSelectedIsland
            || pastedContainsUnselectedIsland)
        {
            throw new InvalidOperationException(
                "Copying one mixing-region island lost its mesh payload or included an unselected island: "
                + $"source={clipboardSource.Success}, hit={clipboardHit.IsValid}, copied={copied}, pasted={pasted}, "
                + $"objects={scene.ObjectCount}/{initialObjectCount + 2}, region={hasPastedRegion}, "
                + $"selected={pastedContainsSelectedIsland}, unselected={pastedContainsUnselectedIsland}.");
        }
    }

    private static void RunMixingBrushMarqueeRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        if (!scene.InsertTimelineBlankKeyframe(1, 7))
        {
            throw new InvalidOperationException("Mixing-brush marquee regression could not create its target keyframe.");
        }

        scene.EditFrame = 7;
        var trajectory = Enumerable.Range(0, 11)
            .Select(index => new MixingBrushTrajectorySample(
                new PointF(-120, -100 + index * 20),
                36,
                Color.Crimson.ToArgb()))
            .Concat(Enumerable.Range(1, 12).Select(index => new MixingBrushTrajectorySample(
                new PointF(-120 + index * 20, 100),
                36,
                Color.RoyalBlue.ToArgb())))
            .ToArray();
        var source = scene.AddMixingBrushStroke(1, trajectory, 9);
        var middleBounds = new RectangleF(-130, -12, 20, 24);
        var emptyBounds = new RectangleF(30, -20, 30, 30);
        var middleHits = scene.QueryDrawingElementsInsideBounds(middleBounds, 7);
        var emptyHits = scene.QueryDrawingElementsInsideBounds(emptyBounds, 7);
        var inactiveFrameHits = scene.QueryDrawingElementsInsideBounds(middleBounds, 0);
        var materialized = scene.MaterializeMarqueeSelectionParts(middleBounds, 7);
        var thinMaterialized = scene.MaterializeMarqueeSelectionParts(
            new RectangleF(-120.25f, -0.25f, 0.5f, 0.5f),
            7);
        var snapshot = scene.CreateSnapshot();
        if (middleHits.Length != 1
            || middleHits[0].Key.ObjectIndex != source
            || middleHits[0].Key.Kind != DrawingElementKind.Fill
            || emptyHits.Length != 0
            || inactiveFrameHits.Length != 0
            || !materialized.Success
            || materialized.Changed
            || !materialized.SelectedObjects.SequenceEqual([source])
            || !thinMaterialized.Success
            || thinMaterialized.Changed
            || !thinMaterialized.SelectedObjects.SequenceEqual([source])
            || scene.ObjectCount != 1
            || scene.ShapeKind[source] != ShapeKind.MixingStroke
            || scene.ObjectLayer[source] != 1
            || scene.ObjectKeyframeFrame[source] != 7
            || snapshot.PathLocalContours.ContainsKey(source)
            || snapshot.FreehandLocalPoints.ContainsKey(source)
            || !snapshot.MixingStrokeLocalSamples.ContainsKey(source))
        {
            throw new InvalidOperationException(
                "Mixing-brush marquee selection missed painted trajectory coverage, selected empty bounds, or materialized the stroke.");
        }

        var splitScene = new VectorScene();
        splitScene.CreateEmpty();
        var splitSamples = Enumerable.Range(-4, 9)
            .Select(index => new MixingBrushTrajectorySample(
                new PointF(index * 40, 0),
                50,
                Color.MediumPurple.ToArgb()))
            .ToArray();
        splitScene.AddMixingBrushStroke(0, splitSamples, 17);
        if (!splitScene.EraseWithBrushStroke(
                0,
                [PointF.Empty],
                50,
                BrushShape.CreateSoftRound(),
                eraseLines: false,
                eraseFills: true))
        {
            throw new InvalidOperationException("Mixing-brush marquee regression could not create erased trajectory fragments.");
        }

        var fragments = Enumerable.Range(0, splitScene.ObjectCount)
            .Where(index => splitScene.ShapeKind[index] == ShapeKind.MixingStroke)
            .OrderBy(index => splitScene.ObjectSubOrder[index])
            .ToArray();
        var leftHits = splitScene.QueryDrawingElementsInsideBounds(new RectangleF(-130, -8, 20, 16), 0);
        var rightHits = splitScene.QueryDrawingElementsInsideBounds(new RectangleF(110, -8, 20, 16), 0);
        if (fragments.Length != 2
            || leftHits.Length != 1
            || rightHits.Length != 1
            || leftHits[0].Key.ObjectIndex != fragments[0]
            || rightHits[0].Key.ObjectIndex != fragments[1])
        {
            throw new InvalidOperationException("Erased mixing-brush fragments were not independently marquee-selectable.");
        }

        var transparentBreakScene = new VectorScene();
        transparentBreakScene.CreateEmpty();
        transparentBreakScene.AddMixingBrushStroke(
            0,
            [
                new MixingBrushTrajectorySample(new PointF(-100, 0), 20, Color.Red.ToArgb()),
                new MixingBrushTrajectorySample(PointF.Empty, 20, Color.Transparent.ToArgb()),
                new MixingBrushTrajectorySample(new PointF(100, 0), 20, Color.Blue.ToArgb())
            ],
            3);
        if (transparentBreakScene.QueryDrawingElementsInsideBounds(
                new RectangleF(-5, -5, 10, 10),
                0).Length != 0)
        {
            throw new InvalidOperationException("A transparent mixing sample did not break marquee trajectory coverage.");
        }

        var mixedScene = new VectorScene();
        mixedScene.CreateEmpty();
        mixedScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(240, 160),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        mixedScene.AddMixingBrushStroke(
            0,
            Enumerable.Range(-5, 11)
                .Select(index => new MixingBrushTrajectorySample(
                    new PointF(index * 20, 0),
                    24,
                    Color.Gold.ToArgb()))
                .ToArray(),
            5);
        var mixedMaterialization = mixedScene.MaterializeMarqueeSelectionParts(
            new RectangleF(-40, -30, 80, 60),
            0);
        var remappedMixingStroke = mixedMaterialization.SelectedObjects
            .SingleOrDefault(index => (uint)index < mixedScene.ObjectCount
                && mixedScene.ShapeKind[index] == ShapeKind.MixingStroke, -1);
        var mixedSnapshot = mixedScene.CreateSnapshot();
        if (!mixedMaterialization.Success
            || !mixedMaterialization.Changed
            || remappedMixingStroke < 0
            || remappedMixingStroke != 0
            || !mixedSnapshot.MixingStrokeLocalSamples.ContainsKey(remappedMixingStroke)
            || mixedSnapshot.PathLocalContours.ContainsKey(remappedMixingStroke))
        {
            throw new InvalidOperationException(
                "Mixed topology materialization did not preserve or remap the selected mixing-brush trajectory.");
        }
    }

    private static void RunMixingBrushPaintSamplingRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 200),
            0,
            0,
            Color.FromArgb(128, Color.Red),
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 200),
            0,
            0,
            Color.FromArgb(128, Color.Blue),
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var composite = MixingBrushPaintSampler.SampleActiveLayerFill(scene, PointF.Empty, frame: 0, layer: 0);
        if (composite is not { } sampledComposite
            || sampledComposite.A is < 190 or > 193
            || sampledComposite.B <= sampledComposite.R)
        {
            throw new InvalidOperationException($"Layered fill pickup did not alpha-compose in drawing order: {composite}.");
        }

        var gradientScene = new VectorScene();
        gradientScene.CreateEmpty();
        var gradientObject = gradientScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 200),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var stops = new[] { new GradientStop(0, Color.Red), new GradientStop(1, Color.Blue) };
        gradientScene.SetGradientPaint(
            gradientObject,
            GradientKind.Linear,
            stops,
            new PointF(-100, 0),
            new PointF(100, 0));
        var linearMiddle = MixingBrushPaintSampler.SampleObjectFill(gradientScene, gradientObject, PointF.Empty);
        gradientScene.SetGradientPaint(
            gradientObject,
            GradientKind.Radial,
            stops,
            PointF.Empty,
            new PointF(100, 0));
        var radialCenter = MixingBrushPaintSampler.SampleObjectFill(gradientScene, gradientObject, PointF.Empty);
        var radialEdge = MixingBrushPaintSampler.SampleObjectFill(gradientScene, gradientObject, new PointF(100, 0));
        gradientScene.SetGradientPaint(
            gradientObject,
            GradientKind.ShapeRadial,
            stops,
            PointF.Empty,
            new PointF(100, 0));
        var shapeCenter = MixingBrushPaintSampler.SampleObjectFill(gradientScene, gradientObject, PointF.Empty);
        var shapeEdge = MixingBrushPaintSampler.SampleObjectFill(gradientScene, gradientObject, new PointF(100, 0));
        if (linearMiddle.R is < 126 or > 129
            || linearMiddle.B is < 126 or > 129
            || radialCenter.ToArgb() != Color.Red.ToArgb()
            || radialEdge.ToArgb() != Color.Blue.ToArgb()
            || shapeCenter.ToArgb() != Color.Red.ToArgb()
            || shapeEdge.ToArgb() != Color.Blue.ToArgb())
        {
            throw new InvalidOperationException("Solid, linear, radial, or shape-radial fill pickup diverged from rendered paint.");
        }

        var crossingTrajectory = new[]
        {
            new MixingBrushTrajectorySample(new PointF(-80, -80), 36, Color.Red.ToArgb()),
            new MixingBrushTrajectorySample(PointF.Empty, 36, Color.Red.ToArgb()),
            new MixingBrushTrajectorySample(new PointF(80, 80), 36, Color.Red.ToArgb()),
            new MixingBrushTrajectorySample(new PointF(-80, 80), 36, Color.Blue.ToArgb()),
            new MixingBrushTrajectorySample(PointF.Empty, 36, Color.Blue.ToArgb()),
            new MixingBrushTrajectorySample(new PointF(80, -80), 36, Color.Blue.ToArgb())
        };

        var solidScene = new VectorScene();
        solidScene.CreateEmpty();
        solidScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(220, 220),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var geometryRevision = solidScene.GeometryRevision;
        var brushObject = solidScene.AddMixingBrushStroke(
            0,
            crossingTrajectory,
            24);
        var storedSnapshot = solidScene.CreateSnapshot();
        if (brushObject < 0
            || solidScene.ObjectCount != 2
            || solidScene.GeometryRevision != geometryRevision + 1
            || solidScene.ShapeKind[brushObject] != ShapeKind.MixingStroke
            || solidScene.HasGradient(brushObject)
            || !solidScene.TryGetMixingStrokeWorldSamples(brushObject, out var storedTrajectory)
            || !storedTrajectory.SequenceEqual(crossingTrajectory)
            || storedSnapshot.PathLocalContours.ContainsKey(brushObject)
            || storedSnapshot.FreehandLocalPoints.ContainsKey(brushObject)
            || storedSnapshot.GradientStops.ContainsKey(brushObject)
            || !storedSnapshot.MixingStrokeLocalSamples.ContainsKey(brushObject))
        {
            throw new InvalidOperationException("Mixing-brush trajectory was not committed as one sparse non-gradient object.");
        }

        var restoredScene = new VectorScene();
        restoredScene.RestoreSnapshot(storedSnapshot);
        if (!restoredScene.TryGetMixingStrokeWorldSamples(brushObject, out var restoredTrajectory)
            || !restoredTrajectory.SequenceEqual(crossingTrajectory))
        {
            throw new InvalidOperationException("Mixing-brush trajectory did not survive snapshot round trip.");
        }
        restoredScene.RemoveObjectAt(0);
        if (restoredScene.ObjectCount != 1
            || restoredScene.ShapeKind[0] != ShapeKind.MixingStroke
            || !restoredScene.TryGetMixingStrokeWorldSamples(0, out var compactedTrajectory)
            || !compactedTrajectory.SequenceEqual(crossingTrajectory))
        {
            throw new InvalidOperationException("Mixing-brush trajectory payload was lost during object compaction.");
        }

        var svgPath = Path.Combine(Path.GetTempPath(), $"v2d-mixing-{Guid.NewGuid():N}.svg");
        try
        {
            DrawingObjectSvgCodec.Write(svgPath, "mixing-regression", storedSnapshot);
            var svgSnapshot = DrawingObjectSvgCodec.Read(svgPath, "mixing-regression");
            var svgScene = new VectorScene();
            svgScene.RestoreSnapshot(svgSnapshot);
            if (!svgScene.TryGetMixingStrokeWorldSamples(brushObject, out var svgTrajectory)
                || !svgTrajectory.SequenceEqual(crossingTrajectory))
            {
                throw new InvalidOperationException("Mixing-brush trajectory did not survive drawing-object SVG persistence.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(svgPath)) File.Delete(svgPath);
            }
            catch
            {
                // A later regression uses a unique path.
            }
        }

        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.AddMixingBrushStroke(0, crossingTrajectory, 24);
        var sceneDefinition = project.Scenes[0];
        if (!project.TryAddSceneInstance(
                sceneDefinition.Id,
                drawingObject.Id,
                new PointF(120, 80),
                0,
                out var instance)
            || instance is null)
        {
            throw new InvalidOperationException("Mixing-brush composition regression could not create its instance.");
        }
        instance.ScaleX = 2f;
        instance.ScaleY = 0.5f;
        instance.Alpha = 0.5f;
        instance.TintArgb = Color.FromArgb(255, 128, 255, 255).ToArgb();
        var composedScene = new VectorScene();
        var composition = SceneCompositionBuilder.Build(
            composedScene,
            sceneDefinition,
            project.DrawingObjects,
            0);
        if (composedScene.ObjectCount != 1
            || composedScene.ShapeKind[0] != ShapeKind.MixingStroke
            || !composedScene.TryGetMixingStrokeWorldSamples(0, out var composedTrajectory)
            || composedTrajectory.Length != crossingTrajectory.Length
            || Math.Abs(composedTrajectory[0].Diameter - crossingTrajectory[0].Diameter) > 0.001f
            || composedTrajectory[0].Point != new PointF(-40, 40)
            || composedTrajectory[0].Argb != ApplyInstanceAppearanceForRegression(
                crossingTrajectory[0].Argb,
                0.5f,
                instance.TintArgb)
            || !composition.TryGetOwner(0, out _))
        {
            throw new InvalidOperationException("Mixing-brush trajectory composition lost its transform, color, or ownership.");
        }

        if (!solidScene.TrySampleMixingStrokeColor(brushObject, PointF.Empty, out var crossingColor)
            || crossingColor.ToArgb() != Color.Blue.ToArgb())
        {
            throw new InvalidOperationException(
                "The later trajectory sample did not remain above the earlier color at a self-intersection.");
        }

        var opticalSettings = new MixingBrushSettings(
            BrushMixingMode.Optical,
            Strength: 0.7f,
            Viscosity: 0.4f,
            PaintLoad: 0.25f,
            Influence: 0.4f);
        var opticalColor = Color.FromArgb(255, 40, 140, 220);
        var opticalRuntime = MixingBrushProcessor.CreateRuntime(
            opticalColor,
            opticalSettings,
            samplingDistance: 10f,
            surfaceSampler: null);
        var opticalTrajectory = new List<MixingBrushTrajectorySample>();
        opticalRuntime.AppendTo(new PointF(0, 0), 50, opticalTrajectory);
        opticalRuntime.AppendTo(new PointF(100, 0), 50, opticalTrajectory);
        opticalRuntime.CompleteTo(opticalTrajectory);
        var opticalScene = new VectorScene();
        opticalScene.CreateEmpty();
        var firstOpticalStroke = opticalScene.AddMixingBrushStroke(0, opticalTrajectory, 16);
        if (!opticalScene.TrySampleMixingStrokeColor(firstOpticalStroke, new PointF(50, 0), out var firstOpticalLayer)
            || firstOpticalLayer.ToArgb() != opticalTrajectory[5].Argb
            || !SameRgb(firstOpticalLayer, opticalColor))
        {
            throw new InvalidOperationException("A continuous optical pass accumulated overlapping sample opacity.");
        }

        using (var opticalStage = new StageControl(opticalScene)
        {
            Size = new Size(320, 240),
            BackColor = Color.Black,
            WorldGridOpacity = 0
        })
        using (var opticalBitmap = new Bitmap(opticalStage.ClientSize.Width, opticalStage.ClientSize.Height))
        {
            opticalStage.SetVisibleWorldWidth(240);
            using (var opticalGraphics = Graphics.FromImage(opticalBitmap))
            {
                var drawGdi = typeof(StageControl).GetMethod(
                    "DrawGdi",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("The GDI optical-coverage entry point could not be located.");
                drawGdi.Invoke(opticalStage, [opticalGraphics]);
            }

            var centerlineBlue = Enumerable.Range(0, 31)
                .Select(index => opticalStage.WorldToScreen(20 + index * 2, 0))
                .Select(point => opticalBitmap.GetPixel(
                    Math.Clamp((int)MathF.Round(point.X), 0, opticalBitmap.Width - 1),
                    Math.Clamp((int)MathF.Round(point.Y), 0, opticalBitmap.Height - 1)).B)
                .ToArray();
            if (centerlineBlue.Max() - centerlineBlue.Min() > 8)
            {
                throw new InvalidOperationException(
                    "Continuous mixing-brush coverage retained visible opacity bands between adjacent samples.");
            }
        }

        opticalScene.AddMixingBrushStroke(0, opticalTrajectory, 16);
        var stackedOptical = MixingBrushPaintSampler.SampleActiveLayerFill(
            opticalScene,
            new PointF(50, 0),
            frame: 0,
            layer: 0);
        if (stackedOptical is not { } stackedColor
            || stackedColor.A <= firstOpticalLayer.A
            || !SameRgb(stackedColor, opticalColor))
        {
            throw new InvalidOperationException("Repeated optical strokes did not build the same color monotonically.");
        }

        var cachedSampler = MixingBrushPaintSampler.CreateActiveLayerSampler(
            opticalScene,
            frame: 0,
            layer: 0,
            tileSize: 256);
        _ = cachedSampler.Sample(new PointF(50, 0));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var samplingTimer = Stopwatch.StartNew();
        const int cachedSampleCount = 4096;
        for (var sampleIndex = 0; sampleIndex < cachedSampleCount; sampleIndex++)
        {
            _ = cachedSampler.Sample(new PointF(48 + sampleIndex % 5, 0));
        }
        samplingTimer.Stop();
        var cachedSamplingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (cachedSampler.SpatialQueryCount != 1 || cachedSamplingAllocatedBytes > 1024)
        {
            throw new InvalidOperationException(
                "Mixing-brush sampling did not reuse its spatial candidates without per-sample allocation: "
                + $"queries={cachedSampler.SpatialQueryCount}, allocated={cachedSamplingAllocatedBytes}.");
        }
        Console.WriteLine($"mixing_brush_cached_sampling_avg_ms={samplingTimer.Elapsed.TotalMilliseconds / cachedSampleCount:0.000000}");
        Console.WriteLine($"mixing_brush_cached_sampling_allocated_bytes={cachedSamplingAllocatedBytes}");

        using var solidStage = new StageControl(solidScene)
        {
            Size = new Size(320, 240),
            BackColor = Color.Black,
            WorldGridOpacity = 0
        };
        solidStage.SetVisibleWorldWidth(240);
        using var solidBitmap = new Bitmap(solidStage.ClientSize.Width, solidStage.ClientSize.Height);
        using (var solidGraphics = Graphics.FromImage(solidBitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI mixing-brush rendering entry point could not be located.");
            drawGdi.Invoke(solidStage, [solidGraphics]);
        }

        var crossingScreen = solidStage.WorldToScreen(0, 0);
        var crossingPixel = solidBitmap.GetPixel(
            Math.Clamp((int)MathF.Round(crossingScreen.X), 0, solidBitmap.Width - 1),
            Math.Clamp((int)MathF.Round(crossingScreen.Y), 0, solidBitmap.Height - 1));
        if (crossingPixel.B <= crossingPixel.R || crossingPixel.B <= crossingPixel.G)
        {
            throw new InvalidOperationException(
                $"The GDI solid-deposit renderer did not show the later blue paint at the crossing: {crossingPixel}.");
        }
    }

    private static void RunMixingBrushRegionRegression()
    {
        static MixingBrushRegionData BuildRegion(
            IEnumerable<PointF> points,
            BrushMixingMode mode,
            Func<PointF, Color?>? sampler = null,
            Color? loadedColor = null)
        {
            var accumulator = new MixingBrushRegionAccumulator(
                loadedColor ?? Color.Coral,
                new MixingBrushSettings(mode, 0.68f, 0.42f, 0.35f, 0.5f),
                diameter: 100f,
                sampler);
            foreach (var point in points) accumulator.Append(point);
            accumulator.Complete();
            if (!accumulator.TryCreateRegion(out var region))
            {
                throw new InvalidOperationException("Mixing-brush region accumulator produced no painted mesh.");
            }
            return region;
        }

        var sparse = BuildRegion(
            [PointF.Empty, new PointF(150, 0)],
            BrushMixingMode.Optical,
            point => point.X < 75 ? Color.Gold : Color.RoyalBlue);
        var dense = BuildRegion(
            Enumerable.Range(0, 31).Select(index => new PointF(index * 5, 0)),
            BrushMixingMode.Optical,
            point => point.X < 75 ? Color.Gold : Color.RoyalBlue);
        if (!sparse.Vertices.SequenceEqual(dense.Vertices)
            || !sparse.TriangleIndices.SequenceEqual(dense.TriangleIndices)
            || !MixingBrushRegionData.HasMinimumVertexSpacing(sparse.Vertices))
        {
            throw new InvalidOperationException(
                "Mixing-brush region data depended on pointer density or violated the 25 vu vertex spacing contract.");
        }

        var sharedPointArgb = Color.Crimson.ToArgb();
        if (!MixingBrushRegionData.TryNormalize(
                [
                    new MixingBrushRegionVertex(PointF.Empty, sharedPointArgb),
                    new MixingBrushRegionVertex(new PointF(-100, 0), sharedPointArgb),
                    new MixingBrushRegionVertex(new PointF(0, -100), sharedPointArgb),
                    new MixingBrushRegionVertex(new PointF(100, 0), Color.RoyalBlue.ToArgb()),
                    new MixingBrushRegionVertex(new PointF(0, 100), Color.RoyalBlue.ToArgb())
                ],
                [0, 1, 2, 0, 3, 4],
                out var pointTouchRegion)
            || pointTouchRegion.ConnectedComponentCount != 2
            || !pointTouchRegion.TryHitConnectedComponent(new PointF(-25, -25), 0, out var pointTouchFirst)
            || !pointTouchRegion.TryHitConnectedComponent(new PointF(25, 25), 0, out var pointTouchSecond)
            || pointTouchFirst == pointTouchSecond)
        {
            throw new InvalidOperationException("Mixing regions that only touched at one vertex shared a selection part.");
        }

        if (!MixingBrushRegionData.TryNormalize(
                [
                    new MixingBrushRegionVertex(new PointF(-50, 0), Color.Transparent.ToArgb()),
                    new MixingBrushRegionVertex(new PointF(50, 0), Color.Transparent.ToArgb()),
                    new MixingBrushRegionVertex(new PointF(0, -50), Color.Crimson.ToArgb()),
                    new MixingBrushRegionVertex(new PointF(0, 50), Color.RoyalBlue.ToArgb())
                ],
                [0, 1, 2, 1, 0, 3],
                out var transparentBridgeRegion)
            || transparentBridgeRegion.ConnectedComponentCount != 2
            || !transparentBridgeRegion.TryHitConnectedComponent(new PointF(0, -20), 0, out var bridgeFirst)
            || !transparentBridgeRegion.TryHitConnectedComponent(new PointF(0, 20), 0, out var bridgeSecond)
            || bridgeFirst == bridgeSecond)
        {
            throw new InvalidOperationException("A fully transparent shared edge bridged independent mixing selection parts.");
        }

        var lowAtomScene = new VectorScene();
        lowAtomScene.CreateEmpty();
        var lowAtomSource = lowAtomScene.AppendMixingBrushRegion(
            0,
            pointTouchRegion,
            (uint)pointTouchRegion.Vertices.Length);
        var lowAtomHit = new DrawingElementHit(
            new DrawingElementKey(lowAtomSource, DrawingElementKind.Fill, pointTouchFirst),
            0,
            0,
            1);
        var lowAtomMaterialized = lowAtomScene.MaterializeSelectedParts([lowAtomHit.Key], 0);
        var lowAtomTotal = Enumerable.Range(0, lowAtomScene.ObjectCount)
            .Aggregate(0UL, (total, index) => total + lowAtomScene.AtomCount[index]);
        var minimumSplitAtomTotal = (ulong)pointTouchRegion.ConnectedComponentCount * 3;
        if (lowAtomSource < 0
            || !lowAtomHit.IsValid
            || !lowAtomMaterialized.Success
            || !lowAtomMaterialized.Changed
            || lowAtomScene.ObjectCount != 2
            || lowAtomTotal != minimumSplitAtomTotal)
        {
            throw new InvalidOperationException(
                "Materializing low-atom mixing components exceeded the per-object minimum atom total: "
                + $"source={lowAtomSource}, hit={lowAtomHit.IsValid}, success={lowAtomMaterialized.Success}, "
                + $"changed={lowAtomMaterialized.Changed}, objects={lowAtomScene.ObjectCount}, "
                + $"atoms={lowAtomTotal}/{minimumSplitAtomTotal}.");
        }

        var pigmentRedRegion = BuildRegion(
            [PointF.Empty, new PointF(150, 0)],
            BrushMixingMode.Pigment,
            loadedColor: Color.Red);
        if (!pigmentRedRegion.TrySampleColor(new PointF(75, 0), out var pigmentRed)
            || !SameRgb(pigmentRed, Color.Red))
        {
            throw new InvalidOperationException(
                $"A pure-red pigment stroke was contaminated while generating its region: {pigmentRed}.");
        }

        var previewPoints = Enumerable.Range(0, 256)
            .Select(index =>
            {
                var pass = index / 32;
                var step = index % 32;
                var x = (pass & 1) == 0 ? step * 8f : (31 - step) * 8f;
                var y = pass % 4 * 25f;
                return new PointF(x, y);
            })
            .ToArray();
        var previewSettings = new MixingBrushSettings(
            BrushMixingMode.Optical,
            Strength: 0.68f,
            Viscosity: 0.42f,
            PaintLoad: 0.35f,
            Influence: 0.5f);
        var liveAccumulator = new MixingBrushRegionAccumulator(
            Color.Coral,
            previewSettings,
            diameter: 150f,
            committedSampler: point => point.X < 124 ? Color.Gold : Color.RoyalBlue);
        var previewAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var previewTimer = Stopwatch.StartNew();
        foreach (var point in previewPoints)
        {
            liveAccumulator.Append(point);
            _ = liveAccumulator.TryCreatePreviewRegion(out _);
        }
        liveAccumulator.Complete();
        if (!liveAccumulator.TryCreateRegion(out var liveRegion))
        {
            throw new InvalidOperationException("Live mixing preview produced no final region.");
        }
        previewTimer.Stop();
        var previewAllocated = GC.GetAllocatedBytesForCurrentThread() - previewAllocatedBefore;

        var finalAccumulator = new MixingBrushRegionAccumulator(
            Color.Coral,
            previewSettings,
            diameter: 150f,
            committedSampler: point => point.X < 124 ? Color.Gold : Color.RoyalBlue);
        foreach (var point in previewPoints) finalAccumulator.Append(point);
        finalAccumulator.Complete();
        if (!finalAccumulator.TryCreateRegion(out var finalRegion)
            || !liveRegion.Vertices.SequenceEqual(finalRegion.Vertices)
            || !liveRegion.TriangleIndices.SequenceEqual(finalRegion.TriangleIndices)
            || !liveAccumulator.ControlDistances.SequenceEqual(finalAccumulator.ControlDistances)
            || previewTimer.Elapsed.TotalMilliseconds > 650
            || previewAllocated > 24L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "Incremental mixing preview diverged from final output or exceeded its interaction budget: "
                + $"elapsed={previewTimer.Elapsed.TotalMilliseconds:0.000} ms, allocated={previewAllocated}.");
        }
        Console.WriteLine($"mixing_brush_live_preview_avg_ms={previewTimer.Elapsed.TotalMilliseconds / previewPoints.Length:0.000000}");
        Console.WriteLine($"mixing_brush_live_preview_allocated_bytes={previewAllocated}");

        if (MainForm.MixingBrushPreviewMinimumIntervalMilliseconds(1024) != 33
            || MainForm.MixingBrushPreviewMinimumIntervalMilliseconds(20_000) <= 33
            || MainForm.MixingBrushPreviewMinimumIntervalMilliseconds(100_000) > 160)
        {
            throw new InvalidOperationException("Long mixing-brush preview cadence was not bounded adaptively.");
        }

        foreach (var mode in Enum.GetValues<BrushMixingMode>())
        {
            var softRegion = BuildRegion(
                [PointF.Empty, new PointF(100, 0)],
                mode);
            var hasCenter = softRegion.TrySampleColor(new PointF(50, 0), out var center);
            var hasShoulder = softRegion.TrySampleColor(new PointF(50, 50), out var shoulder);
            var hasEdge = softRegion.TrySampleColor(new PointF(50, 65), out var edge);
            if (!hasCenter
                || !hasShoulder
                || !hasEdge
                || center.A <= shoulder.A
                || shoulder.A <= edge.A
                || edge.A <= 0)
            {
                throw new InvalidOperationException(
                    $"{mode} mixing-brush edge alpha was not a monotonic soft falloff: "
                    + $"center={center.A}, shoulder={shoulder.A}, edge={edge.A}.");
            }
        }

        var straight = BuildRegion(
            [PointF.Empty, new PointF(150, 0)],
            BrushMixingMode.Optical,
            _ => Color.RoyalBlue);
        var folded = BuildRegion(
            [PointF.Empty, new PointF(150, 0), PointF.Empty],
            BrushMixingMode.Optical,
            _ => Color.RoyalBlue);
        var hasStraightColor = straight.TrySampleColor(new PointF(75, 0), out var straightColor);
        var hasFoldedColor = folded.TrySampleColor(new PointF(75, 0), out var foldedColor);
        if (!hasStraightColor
            || !hasFoldedColor
            || foldedColor.ToArgb() == straightColor.ToArgb()
            || foldedColor.A <= straightColor.A)
        {
            throw new InvalidOperationException(
                "A same-gesture foldback did not resample and recompute the previously painted region: "
                + $"straight={straightColor}, folded={foldedColor}.");
        }

        var firstMergeRegion = BuildRegion(
            [new PointF(-100, 0), new PointF(100, 0)],
            BrushMixingMode.Optical,
            loadedColor: Color.Red);
        var secondMergeRegion = BuildRegion(
            [new PointF(0, -100), new PointF(0, 100)],
            BrushMixingMode.Optical,
            loadedColor: Color.Blue);
        var disconnectedMergeRegion = BuildRegion(
            [new PointF(400, 0), new PointF(500, 0)],
            BrushMixingMode.Optical,
            loadedColor: Color.LimeGreen);
        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        var firstMerge = mergeScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            firstMergeRegion,
            (uint)firstMergeRegion.Vertices.Length);
        var secondMerge = mergeScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            secondMergeRegion,
            (uint)secondMergeRegion.Vertices.Length);
        var mergedOverlap = Color.Transparent;
        var hasMergedOverlap = secondMerge.Success
            && mergeScene.TrySampleMixingStrokeColor(
                secondMerge.ObjectIndex,
                PointF.Empty,
                out mergedOverlap);
        if (!firstMerge.Success
            || firstMerge.Merged
            || !secondMerge.Success
            || secondMerge.MergedObjectCount != 1
            || mergeScene.ObjectCount != 1
            || !hasMergedOverlap
            || mergedOverlap.A <= 45
            || mergedOverlap.R <= 0
            || mergedOverlap.B <= mergedOverlap.R)
        {
            throw new InvalidOperationException(
                "Consecutive mixing regions did not collapse to one final source-over result: "
                + $"objects={mergeScene.ObjectCount}, merged={secondMerge.MergedObjectCount}, color={mergedOverlap}.");
        }

        var disconnectedMerge = mergeScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            disconnectedMergeRegion,
            (uint)disconnectedMergeRegion.Vertices.Length);
        if (!disconnectedMerge.Success
            || disconnectedMerge.MergedObjectCount != 1
            || mergeScene.ObjectCount != 1
            || !mergeScene.TryGetMixingBrushLocalRegion(disconnectedMerge.ObjectIndex, out var mergedPayload)
            || mergeScene.AtomCount[disconnectedMerge.ObjectIndex]
                != mergedPayload.Vertices.Length + mergedPayload.TriangleIndices.Length / 3
            || mergeScene.QueryDrawingElementsInsideBounds(new RectangleF(-5, -5, 10, 10), 0).Length != 1
            || mergeScene.QueryDrawingElementsInsideBounds(new RectangleF(445, -5, 10, 10), 0).Length != 1)
        {
            throw new InvalidOperationException(
                "Disconnected consecutive mixing regions retained historical objects or atom cost.");
        }

        var connectedHit = mergeScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 1);
        var disconnectedHit = mergeScene.HitTestElement(new PointF(450, 0), 0, toleranceWorld: 1);
        var gapHit = mergeScene.HitTestElement(new PointF(250, 0), 0, toleranceWorld: 1);
        var connectedContours = mergeScene.GetFillPartContours(connectedHit, 0);
        PointF[][] stageConnectedContours;
        using (var selectionStage = new StageControl(mergeScene))
        {
            stageConnectedContours = selectionStage.GetSelectedFillPartContours(connectedHit);
        }
        if (!connectedHit.IsValid
            || !disconnectedHit.IsValid
            || gapHit.IsValid
            || connectedHit.Key.ObjectIndex != disconnectedMerge.ObjectIndex
            || disconnectedHit.Key.ObjectIndex != disconnectedMerge.ObjectIndex
            || connectedHit.Key.Kind != DrawingElementKind.Fill
            || disconnectedHit.Key.Kind != DrawingElementKind.Fill
            || connectedHit.Key.PartIndex == disconnectedHit.Key.PartIndex
            || connectedContours.Length == 0
            || connectedContours.SelectMany(contour => contour).Any(point => point.X > 300)
            || stageConnectedContours.Length == 0
            || stageConnectedContours.SelectMany(contour => contour).Any(point => point.X > 300))
        {
            throw new InvalidOperationException(
                "Disconnected mixing-region islands did not retain independent hit or selection-outline identities.");
        }

        var mergedSnapshot = mergeScene.CreateSnapshot();
        var originalAtomCount = mergeScene.AtomCount[disconnectedMerge.ObjectIndex];
        var materializedIsland = mergeScene.MaterializeSelectedParts(
            [connectedHit.Key],
            0,
            mergedSnapshot);
        var selectedIslandMapping = materializedIsland.Parts
            .SingleOrDefault(part => part.Source == connectedHit.Key);
        var selectedIsland = selectedIslandMapping.Source.IsValid
            ? selectedIslandMapping.Result.ObjectIndex
            : -1;
        var unselectedIsland = Enumerable.Range(0, mergeScene.ObjectCount)
            .SingleOrDefault(index => index != selectedIsland, -1);
        if (!materializedIsland.Success
            || !materializedIsland.Changed
            || materializedIsland.Parts.Length != 1
            || mergeScene.ObjectCount != 2
            || selectedIsland < 0
            || unselectedIsland < 0
            || mergeScene.ShapeKind[selectedIsland] != ShapeKind.MixingStroke
            || mergeScene.ShapeKind[unselectedIsland] != ShapeKind.MixingStroke
            || mergeScene.ObjectLayer[selectedIsland] != 0
            || mergeScene.ObjectLayer[unselectedIsland] != 0
            || mergeScene.ObjectKeyframeFrame[selectedIsland] != 0
            || mergeScene.ObjectKeyframeFrame[unselectedIsland] != 0
            || mergeScene.ObjectOrder[selectedIsland] != mergeScene.ObjectOrder[unselectedIsland]
            || mergeScene.ObjectSubOrder[selectedIsland] == mergeScene.ObjectSubOrder[unselectedIsland]
            || mergeScene.AtomCount[selectedIsland] + mergeScene.AtomCount[unselectedIsland] != originalAtomCount
            || !mergeScene.TryGetMixingBrushWorldRegion(selectedIsland, out var selectedIslandRegion)
            || !selectedIslandRegion.ContainsPaint(PointF.Empty)
            || selectedIslandRegion.ContainsPaint(new PointF(450, 0))
            || !mergeScene.TryGetMixingBrushWorldRegion(unselectedIsland, out var unselectedIslandRegion)
            || !unselectedIslandRegion.ContainsPaint(new PointF(450, 0))
            || unselectedIslandRegion.ContainsPaint(PointF.Empty))
        {
            throw new InvalidOperationException(
                "Materializing one mixing-region island did not preserve the selected and unselected components transactionally.");
        }

        var selectedBoundsBeforeMove = mergeScene.GetObjectWorldBounds(selectedIsland);
        var unselectedBoundsBeforeMove = mergeScene.GetObjectWorldBounds(unselectedIsland);
        var transformSession = mergeScene.BeginTransformSession([selectedIsland]);
        if (!mergeScene.ApplyTranslationSessionForPreview(transformSession, 25, 10))
        {
            throw new InvalidOperationException("The selected mixing-region island could not start a translation session.");
        }
        var selectedBoundsAfterMove = mergeScene.GetObjectWorldBounds(selectedIsland);
        var unselectedBoundsAfterMove = mergeScene.GetObjectWorldBounds(unselectedIsland);
        if (Math.Abs(selectedBoundsAfterMove.X - selectedBoundsBeforeMove.X - 25) > 0.001f
            || Math.Abs(selectedBoundsAfterMove.Y - selectedBoundsBeforeMove.Y - 10) > 0.001f
            || unselectedBoundsAfterMove != unselectedBoundsBeforeMove)
        {
            throw new InvalidOperationException("Moving one mixing-region island also moved an unselected island.");
        }

        mergeScene.RestoreSnapshot(mergedSnapshot);
        var restoredConnectedHit = mergeScene.HitTestElement(PointF.Empty, 0, toleranceWorld: 1);
        var restoredDisconnectedHit = mergeScene.HitTestElement(new PointF(450, 0), 0, toleranceWorld: 1);
        if (mergeScene.ObjectCount != 1
            || !restoredConnectedHit.IsValid
            || !restoredDisconnectedHit.IsValid
            || restoredConnectedHit.Key.ObjectIndex != restoredDisconnectedHit.Key.ObjectIndex
            || restoredConnectedHit.Key.PartIndex == restoredDisconnectedHit.Key.PartIndex)
        {
            throw new InvalidOperationException("Restoring the mixing-region snapshot lost merged island identities.");
        }

        var marqueeMaterializedIsland = mergeScene.MaterializeMarqueeSelectionParts(
            new RectangleF(-10, -10, 20, 20),
            0);
        if (!marqueeMaterializedIsland.Success
            || !marqueeMaterializedIsland.Changed
            || marqueeMaterializedIsland.SelectedObjects.Length != 1
            || mergeScene.ObjectCount != 2
            || !mergeScene.TryGetMixingBrushWorldRegion(
                marqueeMaterializedIsland.SelectedObjects[0],
                out var marqueeSelectedRegion)
            || !marqueeSelectedRegion.ContainsPaint(PointF.Empty)
            || marqueeSelectedRegion.ContainsPaint(new PointF(450, 0)))
        {
            throw new InvalidOperationException("Marquee selection promoted one mixing-region island to the whole merged object.");
        }

        var barrierScene = new VectorScene();
        barrierScene.CreateEmpty();
        _ = barrierScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            firstMergeRegion,
            (uint)firstMergeRegion.Vertices.Length);
        barrierScene.AddObject(
            0,
            PointF.Empty,
            new SizeF(20, 20),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var barrierMerge = barrierScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            secondMergeRegion,
            (uint)secondMergeRegion.Vertices.Length);
        if (!barrierMerge.Success || barrierMerge.Merged || barrierScene.ObjectCount != 3)
        {
            throw new InvalidOperationException(
                "Mixing-region merge crossed a non-mixing paint-order barrier.");
        }

        var scopedMergeScene = new VectorScene();
        scopedMergeScene.CreateEmpty(2);
        var layerZero = scopedMergeScene.AddOrMergeMixingBrushRegion(
            0,
            0,
            firstMergeRegion,
            (uint)firstMergeRegion.Vertices.Length);
        var layerOne = scopedMergeScene.AddOrMergeMixingBrushRegion(
            1,
            0,
            secondMergeRegion,
            (uint)secondMergeRegion.Vertices.Length);
        if (!scopedMergeScene.InsertTimelineBlankKeyframe(0, 5))
        {
            throw new InvalidOperationException("Mixing-region merge regression could not create a second keyframe.");
        }
        var laterFrame = scopedMergeScene.AddOrMergeMixingBrushRegion(
            0,
            5,
            disconnectedMergeRegion,
            (uint)disconnectedMergeRegion.Vertices.Length);
        if (!layerZero.Success
            || !layerOne.Success
            || layerOne.Merged
            || !laterFrame.Success
            || laterFrame.Merged
            || scopedMergeScene.ObjectCount != 3)
        {
            throw new InvalidOperationException(
                "Mixing-region merge crossed a layer or keyframe boundary.");
        }

        const int mergePerformanceStrokeCount = 24;
        var mergePerformanceRegions = Enumerable.Range(0, mergePerformanceStrokeCount)
            .Select(index => BuildRegion(
                [
                    new PointF(-180 + index * 12, -80 + index % 5 * 20),
                    new PointF(180 + index * 12, 80 - index % 5 * 20)
                ],
                BrushMixingMode.Optical,
                loadedColor: index % 2 == 0 ? Color.Coral : Color.RoyalBlue))
            .ToArray();
        var mergePerformanceScene = new VectorScene();
        mergePerformanceScene.CreateEmpty();
        var mergePerformanceTimer = Stopwatch.StartNew();
        foreach (var region in mergePerformanceRegions)
        {
            var result = mergePerformanceScene.AddOrMergeMixingBrushRegion(
                0,
                0,
                region,
                (uint)region.Vertices.Length);
            if (!result.Success)
            {
                throw new InvalidOperationException("Repeated mixing-region merge rejected a valid grid region.");
            }
        }
        mergePerformanceTimer.Stop();
        var mergeAverageMilliseconds = mergePerformanceTimer.Elapsed.TotalMilliseconds
            / mergePerformanceStrokeCount;
        if (mergePerformanceScene.ObjectCount != 1 || mergeAverageMilliseconds > 25)
        {
            throw new InvalidOperationException(
                "Repeated mixing-region merge exceeded its interaction budget: "
                + $"objects={mergePerformanceScene.ObjectCount}, average={mergeAverageMilliseconds:0.000} ms.");
        }
        Console.WriteLine($"mixing_brush_region_merge_avg_ms={mergeAverageMilliseconds:0.000}");

        var longRegion = BuildRegion(
            [PointF.Empty, new PointF(10_000, 0)],
            BrushMixingMode.Optical);
        _ = longRegion.TrySampleColor(new PointF(9_950, 0), out _);
        var samplingAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var samplingTimer = Stopwatch.StartNew();
        const int regionSampleCount = 4096;
        for (var sample = 0; sample < regionSampleCount; sample++)
        {
            _ = longRegion.TrySampleColor(
                new PointF(9_900 + sample % 75, sample % 31 - 15),
                out _);
        }
        samplingTimer.Stop();
        var samplingAllocated = GC.GetAllocatedBytesForCurrentThread() - samplingAllocatedBefore;
        var samplingAverageMilliseconds = samplingTimer.Elapsed.TotalMilliseconds / regionSampleCount;
        if (samplingAllocated > 1024 || samplingAverageMilliseconds > 0.05)
        {
            throw new InvalidOperationException(
                "Long mixing-region color lookup did not reuse its triangle spatial index: "
                + $"average={samplingAverageMilliseconds:0.000000} ms, allocated={samplingAllocated}.");
        }
        Console.WriteLine($"mixing_brush_region_sampling_avg_ms={samplingAverageMilliseconds:0.000000}");
        Console.WriteLine($"mixing_brush_region_sampling_allocated_bytes={samplingAllocated}");

        var rasterVertices = longRegion.Vertices
            .Select(vertex => new MixingBrushRasterVertex(vertex.Point, vertex.Argb))
            .ToArray();
        using var warmRaster = MixingBrushRegionRasterizer.RasterizeMesh(
            rasterVertices,
            longRegion.TriangleIndices,
            requestedPixelsPerLocalUnit: 0.25f)
            ?? throw new InvalidOperationException("Mixing-region raster benchmark produced no warm raster.");
        var expectedRasterHash = PixelHash(warmRaster.Pixels);
        const int rasterIterationCount = 8;
        var rasterTimer = Stopwatch.StartNew();
        for (var iteration = 0; iteration < rasterIterationCount; iteration++)
        {
            using var raster = MixingBrushRegionRasterizer.RasterizeMesh(
                rasterVertices,
                longRegion.TriangleIndices,
                requestedPixelsPerLocalUnit: 0.25f)
                ?? throw new InvalidOperationException("Mixing-region raster benchmark produced no raster.");
            if (PixelHash(raster.Pixels) != expectedRasterHash)
            {
                throw new InvalidOperationException("Mixing-region raster output was not deterministic.");
            }
        }
        rasterTimer.Stop();
        var rasterAverageMilliseconds = rasterTimer.Elapsed.TotalMilliseconds / rasterIterationCount;
        if (rasterAverageMilliseconds > 30)
        {
            throw new InvalidOperationException(
                $"Mixing-region raster exceeded its cache-build budget: {rasterAverageMilliseconds:0.000} ms.");
        }
        Console.WriteLine($"mixing_brush_region_raster_avg_ms={rasterAverageMilliseconds:0.000}");

        var scene = new VectorScene();
        scene.CreateEmpty();
        var objectIndex = scene.AddMixingBrushRegion(0, sparse, (uint)sparse.Vertices.Length);
        var snapshot = scene.CreateSnapshot();
        var restored = new VectorScene();
        restored.RestoreSnapshot(snapshot);
        if (objectIndex < 0
            || snapshot.MixingStrokeLocalSamples.ContainsKey(objectIndex)
            || !snapshot.MixingStrokeLocalRegions.ContainsKey(objectIndex)
            || !restored.TryGetMixingBrushWorldRegion(objectIndex, out var restoredRegion)
            || !restoredRegion.Vertices.SequenceEqual(sparse.Vertices)
            || !restoredRegion.TriangleIndices.SequenceEqual(sparse.TriangleIndices))
        {
            throw new InvalidOperationException("Mixing-brush region payload did not survive snapshot round trip.");
        }

        var paintedHits = scene.QueryDrawingElementsInsideBounds(
            new RectangleF(70, -5, 10, 10),
            frame: 0);
        var transparentHits = scene.QueryDrawingElementsInsideBounds(
            new RectangleF(70, 90, 10, 10),
            frame: 0);
        if (paintedHits.Length != 1
            || paintedHits[0].Key.ObjectIndex != objectIndex
            || transparentHits.Length != 0)
        {
            throw new InvalidOperationException(
                "Mixing-region marquee hit testing selected empty halo space or missed painted coverage.");
        }

        var eraseScene = new VectorScene();
        eraseScene.CreateEmpty();
        var eraseRegion = BuildRegion(
            [new PointF(-250, 0), new PointF(250, 0)],
            BrushMixingMode.Pigment);
        eraseScene.AddMixingBrushRegion(0, eraseRegion, (uint)eraseRegion.Vertices.Length);
        if (!eraseScene.EraseWithBrushStroke(
                0,
                [PointF.Empty],
                200,
                BrushShape.CreateSoftRound(),
                eraseLines: false,
                eraseFills: true))
        {
            throw new InvalidOperationException("The eraser did not remove mixing-region triangles.");
        }
        var eraseFragments = Enumerable.Range(0, eraseScene.ObjectCount)
            .Where(index => eraseScene.ShapeKind[index] == ShapeKind.MixingStroke)
            .ToArray();
        if (eraseFragments.Length != 2
            || eraseFragments.Any(index => !eraseScene.TryGetMixingBrushLocalRegion(index, out _))
            || eraseScene.QueryDrawingElementsInsideBounds(new RectangleF(-210, -5, 10, 10), 0).Length != 1
            || eraseScene.QueryDrawingElementsInsideBounds(new RectangleF(200, -5, 10, 10), 0).Length != 1
            || eraseScene.QueryDrawingElementsInsideBounds(new RectangleF(-5, -5, 10, 10), 0).Length != 0)
        {
            throw new InvalidOperationException(
                "Erasing a mixing region did not preserve two independently selectable mesh components.");
        }

        using (var stage = new StageControl(scene)
        {
            Size = new Size(320, 240),
            BackColor = Color.Black,
            WorldGridOpacity = 0
        })
        using (var bitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            stage.SetVisibleWorldWidth(300);
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The GDI region-rendering entry point could not be located.");
            drawGdi.Invoke(stage, [graphics]);

            Color PixelAt(PointF world)
            {
                var screen = stage.WorldToScreen(world.X, world.Y);
                return bitmap.GetPixel(
                    Math.Clamp((int)MathF.Round(screen.X), 0, bitmap.Width - 1),
                    Math.Clamp((int)MathF.Round(screen.Y), 0, bitmap.Height - 1));
            }

            var centerPixel = PixelAt(new PointF(75, 0));
            var edgePixel = PixelAt(new PointF(75, 55));
            var outsidePixel = PixelAt(new PointF(75, 90));
            if (ChannelSum(centerPixel) <= ChannelSum(edgePixel)
                || ChannelSum(edgePixel) <= ChannelSum(outsidePixel) + 2)
            {
                throw new InvalidOperationException(
                    "The GDI mixing-region raster was blank or lost its soft edge: "
                    + $"center={centerPixel}, edge={edgePixel}, outside={outsidePixel}.");
            }
        }

        var svgPath = Path.Combine(Path.GetTempPath(), $"v2d-mixing-region-{Guid.NewGuid():N}.svg");
        try
        {
            DrawingObjectSvgCodec.Write(svgPath, "mixing-region-regression", snapshot);
            var svgScene = new VectorScene();
            svgScene.RestoreSnapshot(DrawingObjectSvgCodec.Read(svgPath, "mixing-region-regression"));
            if (!svgScene.TryGetMixingBrushWorldRegion(objectIndex, out var svgRegion)
                || !svgRegion.Vertices.SequenceEqual(sparse.Vertices)
                || !svgRegion.TriangleIndices.SequenceEqual(sparse.TriangleIndices))
            {
                throw new InvalidOperationException("Mixing-brush region payload did not survive SVG v2 round trip.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(svgPath)) File.Delete(svgPath);
            }
            catch
            {
                // A later regression uses a unique path.
            }
        }

        // Keep this allocation-heavy persistence check from perturbing later
        // interaction-budget measurements in the same benchmark process.
        ForceFullCollectionForBenchmark();
    }

    private static void RunMixingBrushDirect2DRegression()
    {
        var accumulator = new MixingBrushRegionAccumulator(
            Color.Coral,
            new MixingBrushSettings(BrushMixingMode.Optical, 0f, 0.5f, 0.7f, 0f),
            diameter: 100f,
            committedSampler: null);
        accumulator.Append(PointF.Empty);
        accumulator.Append(new PointF(100, 0));
        accumulator.Complete();
        if (!accumulator.TryCreateRegion(out var region))
        {
            throw new InvalidOperationException("Direct2D mixing-region regression produced no mesh.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty();
        if (scene.AddMixingBrushRegion(0, region, (uint)region.Vertices.Length) < 0)
        {
            throw new InvalidOperationException("Direct2D mixing-region regression could not add its mesh.");
        }

        var screenBounds = SystemInformation.VirtualScreen;
        var captureSentinel = Color.Fuchsia;
        using var form = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(
                Math.Max(screenBounds.Left, screenBounds.Right - 360),
                Math.Max(screenBounds.Top, screenBounds.Bottom - 280)),
            ClientSize = new Size(320, 240),
            BackColor = captureSentinel,
            Padding = new Padding(4)
        };
        using var stage = new StageControl(scene)
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            WorldGridOpacity = 0
        };
        stage.SetVisibleWorldWidth(300);
        form.Controls.Add(stage);
        form.Show();
        form.Activate();
        stage.Invalidate();
        stage.Update();
        Application.DoEvents();

        var visibleDesktop = false;
        for (var attempt = 0; attempt < 8 && !visibleDesktop; attempt++)
        {
            form.BringToFront();
            stage.Invalidate();
            stage.Update();
            Application.DoEvents();
            Thread.Sleep(20);
            Application.DoEvents();

            try
            {
                using var probe = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                using var graphics = Graphics.FromImage(probe);
                graphics.CopyFromScreen(
                    form.PointToScreen(Point.Empty),
                    Point.Empty,
                    form.ClientSize,
                    CopyPixelOperation.SourceCopy);
                visibleDesktop = new[]
                    {
                        new Point(1, 1),
                        new Point(probe.Width - 2, 1),
                        new Point(1, probe.Height - 2),
                        new Point(probe.Width - 2, probe.Height - 2)
                    }
                    .All(point => ColorDistance(probe.GetPixel(point.X, point.Y), captureSentinel) <= 12);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or System.Runtime.InteropServices.ExternalException
                or ArgumentException)
            {
                visibleDesktop = false;
            }
        }
        if (!visibleDesktop)
        {
            form.Close();
            Console.WriteLine("mixing_brush_region_direct2d=skipped_no_visible_desktop");
            return;
        }

        using var capture = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
        using (var graphics = Graphics.FromImage(capture))
        {
            graphics.CopyFromScreen(
                stage.PointToScreen(Point.Empty),
                Point.Empty,
                stage.ClientSize,
                CopyPixelOperation.SourceCopy);
        }

        Color PixelAt(PointF world)
        {
            var point = stage.WorldToScreen(world.X, world.Y);
            return capture.GetPixel(
                Math.Clamp((int)MathF.Round(point.X), 0, capture.Width - 1),
                Math.Clamp((int)MathF.Round(point.Y), 0, capture.Height - 1));
        }

        var center = PixelAt(new PointF(50, 0));
        var edge = PixelAt(new PointF(50, 55));
        var outside = PixelAt(new PointF(50, 90));
        if (!stage.LastFrameUsedDirect2D
            || !stage.GpuAccelerationActive
            || ChannelSum(center) <= ChannelSum(edge)
            || ChannelSum(edge) <= ChannelSum(outside) + 2)
        {
            throw new InvalidOperationException(
                "The Direct2D mixing-region presentation was blank or lost its soft edge: "
                + $"direct2d={stage.LastFrameUsedDirect2D}, center={center}, edge={edge}, outside={outside}.");
        }

        form.Close();
        Console.WriteLine("mixing_brush_region_direct2d=ok");
    }

    private static int ChannelSum(Color color) => color.R + color.G + color.B;

    private static int ColorDistance(Color first, Color second) =>
        Math.Abs(first.R - second.R)
        + Math.Abs(first.G - second.G)
        + Math.Abs(first.B - second.B);

    private static bool SameRgb(Color first, Color second) =>
        first.R == second.R && first.G == second.G && first.B == second.B;

    private static int PixelHash(IReadOnlyList<int> pixels)
    {
        var hash = 17;
        for (var index = 0; index < pixels.Count; index++)
        {
            hash = unchecked(hash * 31 + pixels[index]);
        }
        return hash;
    }
}
