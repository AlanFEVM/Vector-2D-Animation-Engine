using System.Diagnostics;
using System.Numerics;
using Clipper2Lib;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneOpticsRenderRegression()
    {
        RunSceneLightingBrdfRegression();
        RunSceneLightingVisualRegression();
        RunSceneOpticsPanelLayoutRegression();
        var scene = new VectorScene();
        scene.CreateEmpty();
        var receiver = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(2400, 1800),
            angle: 0,
            stroke: 0,
            color: Color.FromArgb(255, 112, 156, 202),
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);
        var caster = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(900, 700),
            angle: 0,
            stroke: 0,
            color: Color.FromArgb(255, 220, 96, 72),
            strokeColor: Color.Transparent,
            atoms: 12,
            shapeKind: ShapeKind.Rectangle);

        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        var owners = new[]
        {
            new SceneCompositionObjectOwner("receiver", "fixture"),
            new SceneCompositionObjectOwner("caster", "fixture")
        };
        var poses = new[]
        {
            new SceneCompositionObjectPose(Matrix4x4.Identity),
            new SceneCompositionObjectPose(Matrix4x4.CreateTranslation(0, 0, 600))
        };
        var receiverMaterial = SpatialOpticalMaterial.Default with
        {
            Transmission = 0.4f,
            Reflectivity = 0.3f,
            Roughness = 0.2f
        };
        var casterMaterial = SpatialOpticalMaterial.Default;
        var composition = new SceneCompositionResult(
            owners,
            poses,
            [receiverMaterial, casterMaterial]);

        using var stage = new StageControl(scene)
        {
            ClientSize = new Size(640, 480),
            WorldGridOpacity = 0
        };
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        stage.SetSceneCompositionResult(composition, scene);
        RunSceneLightGizmoRegression(stage);

        var initial = stage.GetReference3DSceneRenderItems();
        var receiverItem = initial.FirstOrDefault(item =>
            item.ObjectIndex == receiver
            && item.Kind == Reference3DRenderKind.FrontFill);
        if (receiverItem.ObjectIndex != receiver
            || receiverItem.OpticalResponse.OpacityScale >= 0.999f
            || stage.Reference3DOpticsPlanBuildCount != 1
            || stage.LastReference3DOpticalLightEvaluations <= 0)
        {
            throw new InvalidOperationException(
                "The reference-3D optics plan did not evaluate default lighting and transmission.");
        }

        var cachedBuilds = stage.Reference3DOpticsPlanBuildCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < 64; sample++)
        {
            if (!ReferenceEquals(initial, stage.GetReference3DSceneRenderItems()))
            {
                throw new InvalidOperationException(
                    "A stable optics frame did not reuse the cached reference render plan.");
            }
        }
        var stableAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (stage.Reference3DOpticsPlanBuildCount != cachedBuilds || stableAllocated > 4096)
        {
            throw new InvalidOperationException(
                $"Stable optics-plan reuse regressed: builds="
                + $"{stage.Reference3DOpticsPlanBuildCount - cachedBuilds}, allocated={stableAllocated}.");
        }

        var directional = definition.Lights.First(light => light.Kind == SceneLightKind.Directional);
        var priorPlanBuilds = stage.Reference3DRenderPlanBuildCount;
        if (!definition.UpdateLight(
                directional.Id,
                directional.Name,
                directional.Settings with { Intensity = 1.5f }))
        {
            throw new InvalidOperationException("The directional-light optics fixture could not change intensity.");
        }
        var changed = stage.GetReference3DSceneRenderItems();
        if (ReferenceEquals(initial, changed)
            || stage.Reference3DRenderPlanBuildCount != priorPlanBuilds + 1
            || stage.Reference3DOpticsPlanBuildCount != cachedBuilds + 1)
        {
            throw new InvalidOperationException(
                "A light edit did not lazily rebuild exactly one shared optics render plan.");
        }

        AssertOpticalInteractionPreviewLod();

        AssertLightKindEvaluates(SceneLightKind.Ambient, new SceneLightSettings(
            true,
            Color.White.ToArgb(),
            0.5f,
            0,
            Vector3.Zero,
            Vector3.Zero,
            Vector2.Zero,
            false,
            0,
            0));
        AssertLightKindEvaluates(SceneLightKind.Directional, new SceneLightSettings(
            true,
            Color.White.ToArgb(),
            1,
            0,
            Vector3.Zero,
            Vector3.Zero,
            Vector2.Zero,
            false,
            0,
            0));
        AssertLightKindEvaluates(SceneLightKind.Point, new SceneLightSettings(
            true,
            Color.White.ToArgb(),
            3,
            10_000,
            new Vector3(0, 0, -3000),
            Vector3.Zero,
            Vector2.Zero,
            false,
            0,
            0));
        AssertLightKindEvaluates(SceneLightKind.Area, new SceneLightSettings(
            true,
            Color.White.ToArgb(),
            3,
            10_000,
            new Vector3(0, 0, -3000),
            Vector3.Zero,
            new Vector2(1200, 800),
            false,
            0,
            0.5f));

        AssertSharedDirectLightResponse();
        AssertAreaLightSamplingIndependentOfShadowSoftness();
        AssertFiniteLightLocality(SceneLightKind.Point, Vector2.Zero);
        AssertFiniteLightLocality(SceneLightKind.Area, new Vector2(400, 320));
        AssertFiniteLightSurvivesPerspectiveDolly(SceneLightKind.Point, Vector2.Zero);
        AssertFiniteLightSurvivesPerspectiveDolly(SceneLightKind.Area, new Vector2(400, 320));
        AssertZeroThicknessVisibleSidePointLight();
        AssertFiniteLightOcclusion(SceneLightKind.Point, Vector2.Zero);
        AssertFiniteLightOcclusion(SceneLightKind.Area, new Vector2(400, 320));
        AssertCrossingFragmentLightOcclusion();
        AssertDirectionalShadowIsolation();
        AssertFrontStrokeReceivesLighting();
        AssertThinFrontStrokePreviewContinuity();
        AssertLinearMaterialLighting();
        AssertOpticalRasterBudgetAndFallback();
        AssertShared2D3DFrameOptics();
        RunSceneOpticsDenseLightingRegression();

        Console.WriteLine("scene_optics_render_plan=ok");
        Console.WriteLine($"scene_optics_stable_allocated_bytes={stableAllocated}");

        void AssertOpticalInteractionPreviewLod()
        {
            stage.BeginReference3DOpticalInteractionPreview();
            var previewItems = stage.GetReference3DSceneRenderItems();
            var previewSurface = previewItems
                .Where(item => item.ObjectIndex == receiver
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .Select(item => item.OpticalSurface)
                .OfType<Reference3DOpticalSurface>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The optical interaction preview did not build a receiver surface.");
            if (!stage.Reference3DOpticalInteractionPreviewActive
                || stage.LastReference3DOpticalRasterLod != 2
                || previewSurface.PixelWidth != (int)Math.Ceiling(previewSurface.Bounds.Width / 4d)
                || previewSurface.PixelHeight != (int)Math.Ceiling(previewSurface.Bounds.Height / 4d))
            {
                throw new InvalidOperationException(
                    "The optical interaction preview did not use the bounded quarter-resolution raster.");
            }

            stage.EndReference3DOpticalInteractionPreview();
            var finalItems = stage.GetReference3DSceneRenderItems();
            var finalSurface = finalItems
                .Where(item => item.ObjectIndex == receiver
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .Select(item => item.OpticalSurface)
                .OfType<Reference3DOpticalSurface>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Completing the optical interaction did not build a final receiver surface.");
            if (stage.Reference3DOpticalInteractionPreviewActive
                || stage.LastReference3DOpticalRasterLod != 0
                || finalSurface.PixelWidth != finalSurface.Bounds.Width
                || finalSurface.PixelHeight != finalSurface.Bounds.Height
                || ReferenceEquals(previewItems, finalItems))
            {
                throw new InvalidOperationException(
                    "Completing optical interaction did not restore a distinct full-resolution render plan.");
            }
        }

        void AssertLightKindEvaluates(SceneLightKind kind, SceneLightSettings settings)
        {
            definition.RestoreLights(
                [new SceneLightDefinition(Guid.NewGuid().ToString("N"), kind.ToString(), kind, settings)],
                lightsWerePresent: true);
            var before = stage.Reference3DOpticsPlanBuildCount;
            _ = stage.GetReference3DSceneRenderItems();
            if (stage.Reference3DOpticsPlanBuildCount != before + 1
                || stage.LastReference3DOpticalLightEvaluations <= 0)
            {
                throw new InvalidOperationException($"{kind} light did not reach the shared optics evaluator.");
            }
        }

        void AssertSharedDirectLightResponse()
        {
            var sharedSettings = new SceneLightSettings(
                true,
                Color.FromArgb(255, 244, 196, 132).ToArgb(),
                2.4f,
                SceneLightSettings.MaximumRange,
                new Vector3(0, 0, 1000),
                Vector3.Zero,
                Vector2.Zero,
                false,
                0f,
                0f);
            var isolatedPoses = new[]
            {
                poses[0],
                new SceneCompositionObjectPose(Matrix4x4.CreateTranslation(10_000, 0, 600))
            };
            stage.SetSceneCompositionResult(
                new SceneCompositionResult(owners, isolatedPoses, [receiverMaterial, casterMaterial]),
                scene);
            stage.SetReferenceCameraOrientation(MathF.PI, 0);

            var point = Capture(SceneLightKind.Point, sharedSettings);
            var area = Capture(
                SceneLightKind.Area,
                sharedSettings with { AreaSize = new Vector2(0.01f, 0.01f) });
            var directional = Capture(
                SceneLightKind.Directional,
                sharedSettings with
                {
                    Range = 0f,
                    Position = Vector3.Zero,
                    AreaSize = Vector2.Zero
                });
            if (Luminance(point.Diffuse) <= 0f || Luminance(point.Specular) <= 0f)
            {
                throw new InvalidOperationException(
                    "The shared direct-light fixture did not exercise both diffuse and specular response.");
            }
            AssertNear(point.Diffuse, area.Diffuse, 0.000001f, "Point and point-sized area diffuse");
            AssertNear(point.Specular, area.Specular, 0.000001f, "Point and point-sized area specular");
            AssertNear(point.Fresnel, area.Fresnel, 0.000001f, "Point and point-sized area Fresnel");
            AssertNear(point.Diffuse, directional.Diffuse, 0.000001f, "Point and directional diffuse");
            AssertNear(point.Specular, directional.Specular, 0.000001f, "Point and directional specular");
            AssertNear(point.Fresnel, directional.Fresnel, 0.000001f, "Point and directional Fresnel");
            stage.SetSceneCompositionResult(composition, scene);
            stage.SetReferenceCameraOrientation(0, 0);

            (Vector3 Diffuse, Vector3 Specular, Vector3 Fresnel) Capture(
                SceneLightKind kind,
                SceneLightSettings settings)
            {
                definition.RestoreLights(
                    [new SceneLightDefinition(Guid.NewGuid().ToString("N"), kind.ToString(), kind, settings)],
                    lightsWerePresent: true);
                var localLayers = stage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == receiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .ToArray();
                if (localLayers.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"The shared {kind} fixture produced {localLayers.Length} direct-light layers instead of one.");
                }
                var sample = localLayers[0].LinearStops[0];
                return (sample.DiffuseIrradiance, sample.SpecularRadiance, sample.FresnelRadiance);
            }

            static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string label)
            {
                if (Vector3.Distance(expected, actual) > tolerance * Math.Max(1f, expected.Length()))
                {
                    throw new InvalidOperationException(
                        $"{label} responses diverged: expected={expected}, actual={actual}.");
                }
            }
        }

        void AssertAreaLightSamplingIndependentOfShadowSoftness()
        {
            var settings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                3f,
                1500f,
                new Vector3(0, 0, 1000),
                Vector3.Zero,
                new Vector2(400, 320),
                false,
                0f,
                0f);
            stage.SetReferenceCameraOrientation(MathF.PI, 0);
            var sharp = Capture(settings);
            var soft = Capture(settings with { ShadowSoftness = 1f });
            var mismatchIndex = -1;
            if (sharp.Length == soft.Length)
            {
                for (var index = 0; index < sharp.Length; index++)
                {
                    if (sharp[index].Transform == soft[index].Transform
                        && sharp[index].Linear.AsSpan().SequenceEqual(soft[index].Linear))
                    {
                        continue;
                    }
                    mismatchIndex = index;
                    break;
                }
            }
            if (sharp.Length == 0
                || sharp.Length % 9 != 0
                || soft.Length != sharp.Length
                || mismatchIndex >= 0)
            {
                throw new InvalidOperationException(
                    "Area-light shadow softness changed unobstructed direct-light sampling: "
                    + $"sharp={sharp.Length}, soft={soft.Length}, mismatch={mismatchIndex}.");
            }
            stage.SetReferenceCameraOrientation(0, 0);

            (Matrix3x2 Transform, Reference3DLinearLightStop[] Linear)[] Capture(
                SceneLightSettings value)
            {
                definition.RestoreLights(
                    [new SceneLightDefinition(
                        Guid.NewGuid().ToString("N"),
                        "Area sampling fixture",
                        SceneLightKind.Area,
                        value)],
                    lightsWerePresent: true);
                return stage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == receiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .Select(layer => (layer.GradientTransform, layer.LinearStops))
                    .ToArray();
            }
        }

        void AssertFiniteLightLocality(SceneLightKind kind, Vector2 areaSize)
        {
            stage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    owners,
                    poses,
                    [receiverMaterial with { Reflectivity = 0f }, casterMaterial]),
                scene);
            var settings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                3f,
                1500f,
                new Vector3(0, 0, 1000),
                Vector3.Zero,
                areaSize,
                false,
                0,
                kind == SceneLightKind.Area ? 0.35f : 0f);
            definition.RestoreLights(
                [new SceneLightDefinition(Guid.NewGuid().ToString("N"), kind.ToString(), kind, settings)],
                lightsWerePresent: true);
            stage.SetReferenceCameraOrientation(MathF.PI, 0);
            var localReceivers = stage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == receiver
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .ToArray();
            var localLayers = localReceivers
                .SelectMany(item => item.LocalLightLayers ?? [])
                .ToArray();
            if (localLayers.Length == 0 || stage.LastReference3DLocalLightLayerCount <= 0)
            {
                throw new InvalidOperationException(
                    $"The {kind} light did not create a receiver-clipped local footprint.");
            }
            foreach (var localReceiver in localReceivers)
            {
                var receiverDomain = VisibleSurfacePaths(localReceiver);
                foreach (var layer in localReceiver.LocalLightLayers ?? [])
                {
                    var outside = Clipper.Difference(
                        Paths(layer.Contours),
                        receiverDomain,
                        FillRule.EvenOdd,
                        precision: 4);
                    if (Area(outside) > 0.01d)
                    {
                        throw new InvalidOperationException(
                            $"The {kind} local footprint escaped its physical receiver surface.");
                    }
                }
            }

            var receiverBounds = Bounds(localReceivers.SelectMany(item =>
                item.FragmentClip ?? item.Contours));
            var footprintBounds = Bounds(localLayers.SelectMany(layer => layer.Contours));
            var coversWholeReceiver = footprintBounds.Left <= receiverBounds.Left + 1f
                && footprintBounds.Top <= receiverBounds.Top + 1f
                && footprintBounds.Right >= receiverBounds.Right - 1f
                && footprintBounds.Bottom >= receiverBounds.Bottom - 1f;
            if (coversWholeReceiver)
            {
                throw new InvalidOperationException(
                    $"The finite {kind} light still recolored the entire receiver surface.");
            }

            var stableDiffuse = localLayers
                .SelectMany(layer => layer.LinearStops)
                .Select(stop => stop with { SpecularRadiance = Vector3.Zero, FresnelRadiance = Vector3.Zero })
                .Distinct()
                .OrderBy(stop => stop.Position)
                .ThenBy(stop => stop.DiffuseIrradiance.X)
                .ThenBy(stop => stop.DiffuseIrradiance.Y)
                .ThenBy(stop => stop.DiffuseIrradiance.Z)
                .ToArray();
            if (localLayers.Any(layer =>
                    layer.LinearStops.Length < 9
                    || layer.LinearStops[0].Position != 0f
                    || layer.LinearStops[^1].Position != 1f
                    || Luminance(layer.LinearStops[^1].DiffuseIrradiance) > 0.000001f
                    || !StrictlyIncreasing(layer.LinearStops.Select(stop => stop.Position))
                    || !NonIncreasing(layer.LinearStops.Select(stop =>
                        Luminance(stop.DiffuseIrradiance)))))
            {
                throw new InvalidOperationException(
                    $"The {kind} light did not expose a continuous, monotonic radial profile.");
            }
            if (kind == SceneLightKind.Point)
            {
                var transform = localLayers[0].GradientTransform;
                using var bitmap = RenderOpticsGdi(stage);
                var albedoSample = new PointF(
                    transform.M21 * 0.58f + transform.M31,
                    transform.M22 * 0.58f + transform.M32);
                var albedoPixel = SampleOpticalPixel(localReceivers, albedoSample);
                var albedoAlpha = (albedoPixel >>> 24) & 0xff;
                var albedoRed = Unpremultiply((albedoPixel >>> 16) & 0xff, albedoAlpha);
                var albedoBlue = Unpremultiply(albedoPixel & 0xff, albedoAlpha);
                if (albedoAlpha == 0 || albedoBlue <= albedoRed + 20)
                {
                    throw new InvalidOperationException(
                        "A white point light washed the blue receiver toward the light color instead of preserving albedo: "
                        + $"pixel={albedoPixel:X8}, alpha={albedoAlpha}, red={albedoRed}, blue={albedoBlue}.");
                }

                var radialColors = new HashSet<int>();
                for (var index = 0; index <= 48; index++)
                {
                    var radius = 0.4f + index / 48f * 0.3f;
                    var sample = Point.Round(new PointF(
                        transform.M21 * radius + transform.M31,
                        transform.M22 * radius + transform.M32));
                    if ((uint)sample.X < bitmap.Width && (uint)sample.Y < bitmap.Height)
                    {
                        radialColors.Add(bitmap.GetPixel(sample.X, sample.Y).ToArgb());
                    }
                }
                if (radialColors.Count < 12)
                {
                    throw new InvalidOperationException(
                        $"The GDI point-light radius retained visible color bands: unique={radialColors.Count}.");
                }
            }
            stage.SetReferenceCameraOrientation(MathF.PI - 0.42f, -0.24f);
            var roamedDiffuse = stage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == receiver
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .SelectMany(item => item.LocalLightLayers ?? [])
                .SelectMany(layer => layer.LinearStops)
                .Select(stop => stop with { SpecularRadiance = Vector3.Zero, FresnelRadiance = Vector3.Zero })
                .Distinct()
                .OrderBy(stop => stop.Position)
                .ThenBy(stop => stop.DiffuseIrradiance.X)
                .ThenBy(stop => stop.DiffuseIrradiance.Y)
                .ThenBy(stop => stop.DiffuseIrradiance.Z)
                .ToArray();
            if (!stableDiffuse.AsSpan().SequenceEqual(roamedDiffuse))
            {
                throw new InvalidOperationException(
                    $"Camera roaming changed {kind} diffuse energy at fixed world distances.");
            }

            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    kind.ToString(),
                    kind,
                    settings with
                    {
                        Position = new Vector3(0, 0, 3000),
                        Range = 400
                    })],
                lightsWerePresent: true);
            if (stage.GetReference3DSceneRenderItems().Any(item =>
                    item.ObjectIndex == receiver
                    && item.Kind == Reference3DRenderKind.FrontFill
                    && item.LocalLightLayers is { Length: > 0 }))
            {
                throw new InvalidOperationException(
                    $"The out-of-range {kind} light still affected the receiver.");
            }
            stage.SetReferenceCameraOrientation(0, 0);
            stage.SetSceneCompositionResult(composition, scene);

            static bool StrictlyIncreasing(IEnumerable<float> values)
            {
                var prior = float.NegativeInfinity;
                foreach (var value in values)
                {
                    if (!float.IsFinite(value) || value <= prior) return false;
                    prior = value;
                }
                return true;
            }

            static bool NonIncreasing(IEnumerable<float> values)
            {
                var prior = float.PositiveInfinity;
                foreach (var value in values)
                {
                    if (!float.IsFinite(value) || value > prior + 0.000001f) return false;
                    prior = value;
                }
                return true;
            }

            static int SampleOpticalPixel(
                IEnumerable<Reference3DRenderItem> items,
                PointF point)
            {
                var x = (int)MathF.Round(point.X);
                var y = (int)MathF.Round(point.Y);
                var bestPixel = 0;
                var bestAlpha = 0;
                foreach (var surface in items
                             .Select(item => item.OpticalSurface)
                             .OfType<Reference3DOpticalSurface>())
                {
                    if (!surface.TryGetPremultipliedPixelAtScreenPoint(x, y, out var pixel)) continue;
                    var alpha = (pixel >>> 24) & 0xff;
                    if (alpha <= bestAlpha) continue;
                    bestPixel = pixel;
                    bestAlpha = alpha;
                    if (alpha == 0xff) break;
                }
                if (bestAlpha > 0) return bestPixel;
                throw new InvalidOperationException(
                    $"The local-light albedo sample ({x}, {y}) missed its optical surface.");
            }

            static int Unpremultiply(int value, int alpha) =>
                alpha == 0 ? 0 : Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);
        }

        void AssertOpticalRasterBudgetAndFallback()
        {
            using var boundsStage = new StageControl(new VectorScene())
            {
                ClientSize = new Size(1_000, 1_000)
            };
            var materialContour = ClosedContour(100, 100, 300, 250);
            var fragmentContour = ClosedContour(250, 200, 900, 900);
            var boundedItem = new Reference3DRenderItem(
                0,
                0,
                Reference3DRenderKind.FrontFill,
                [materialContour],
                0,
                default,
                0,
                0)
            {
                FragmentClip = [fragmentContour]
            };
            if (!boundsStage.TryGetReference3DOpticalSurfaceBounds(
                    boundedItem,
                    out var clippedBounds)
                || clippedBounds != Rectangle.FromLTRB(248, 198, 303, 253))
            {
                throw new InvalidOperationException(
                    $"The optical raster did not intersect its material and fragment bounds: {clippedBounds}.");
            }
            if (boundsStage.TryGetReference3DOpticalSurfaceBounds(
                    boundedItem with { FragmentClip = [ClosedContour(600, 600, 800, 800)] },
                    out _))
            {
                throw new InvalidOperationException(
                    "A disjoint fragment allocated an optical raster outside its material surface.");
            }

            var budgetBounds = new[]
            {
                new Rectangle(0, 0, 5_000, 5_000),
                new Rectangle(0, 0, 5_000, 5_000)
            };
            var present = new[] { true, true };
            if (StageControl.TryCreateReference3DOpticalRasterLayouts(
                    budgetBounds,
                    present,
                    lod: 0,
                    out _)
                || !StageControl.TryCreateReference3DOpticalRasterLayouts(
                    budgetBounds,
                    present,
                    lod: 1,
                    out var reducedLayouts)
                || reducedLayouts.Any(layout => layout.PixelWidth != 2_500
                    || layout.PixelHeight != 2_500))
            {
                throw new InvalidOperationException(
                    "The shared optical raster budget did not choose a deterministic reduced layout.");
            }

            var rectangularBounds = new[] { new Rectangle(0, 0, 6_000, 2_000) };
            if (StageControl.TryCreateReference3DOpticalRasterLayouts(
                    rectangularBounds,
                    [true],
                    lod: 0,
                    out _)
                || !StageControl.TryCreateReference3DOpticalRasterLayouts(
                    rectangularBounds,
                    [true],
                    lod: 1,
                    out var rectangularLayouts)
                || rectangularLayouts[0].PixelWidth != 3_000
                || rectangularLayouts[0].PixelHeight != 1_000
                || Math.Abs(rectangularLayouts[0].ScaleX - 0.5f) > 0.000001f
                || Math.Abs(rectangularLayouts[0].ScaleY - 0.5f) > 0.000001f)
            {
                throw new InvalidOperationException(
                    "A non-square optical surface did not use the same LOD divisor on both axes.");
            }

            var aggregateBounds = Enumerable.Repeat(
                    new Rectangle(0, 0, 2_048, 2_048),
                    5)
                .ToArray();
            if (!StageControl.TryCreateReference3DOpticalRasterLayouts(
                    aggregateBounds,
                    [true, true, true, true, false],
                    lod: 0,
                    out _)
                || StageControl.TryCreateReference3DOpticalRasterLayouts(
                    aggregateBounds,
                    [true, true, true, true, true],
                    lod: 0,
                    out _))
            {
                throw new InvalidOperationException(
                    "The optical raster planner did not enforce its 128 MiB aggregate budget.");
            }

            var response = new Reference3DOpticalResponse(
                Color.FromArgb(96, 0, 255, 255).ToArgb(),
                Color.FromArgb(96, 255, 0, 255).ToArgb(),
                0.72f,
                new Vector3(0f, 1f, 1f));
            var fallbackItems = new[]
            {
                boundedItem with
                {
                    MaterialOpacity = 0.37f,
                    OpticalResponse = response,
                    LocalLightLayers =
                    [
                        new Reference3DLocalLightLayer(
                            [materialContour],
                            Matrix3x2.Identity,
                            [new GradientStop(0f, Color.Cyan)],
                            [new GradientStop(0f, Color.Magenta)],
                            [new Reference3DLinearLightStop(0f, Vector3.One, Vector3.One)])
                    ]
                }
            };
            StageControl.DisableReference3DOpticsWithoutSurface(
                fallbackItems,
                [true],
                hasSurface: null,
                sourceMaterialOpacities: [0.37f]);
            var fallback = fallbackItems[0];
            if (fallback.MaterialOpacity != 0.37f
                || fallback.OpticalSurface is not null
                || fallback.OpticalResponse != Reference3DOpticalResponse.Identity
                || fallback.LocalLightLayers is not { Length: 0 }
                || fallback.ShadowLayers is not { Length: 0 })
            {
                throw new InvalidOperationException(
                    "An unavailable optical raster retained the legacy colored-light overlay state.");
            }

            AssertTransactionalOpticalSurfaceFallback();
            AssertOpticalSurfaceGdiScaling();

            var largeScene = new VectorScene();
            largeScene.CreateEmpty();
            var largeObject = largeScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(100_000, 100_000),
                angle: 0,
                stroke: 0,
                color: Color.White,
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            largeScene.SetGradientPaint(
                largeObject,
                GradientKind.Linear,
                [
                    new GradientStop(0f, Color.FromArgb(72, 224, 48, 64)),
                    new GradientStop(0.45f, Color.FromArgb(0, 40, 232, 88)),
                    new GradientStop(0.55f, Color.FromArgb(0, 224, 48, 192)),
                    new GradientStop(1f, Color.FromArgb(184, 48, 160, 224))
                ],
                new PointF(-30_000, 0),
                new PointF(30_000, 0));
            var largeDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            largeDefinition.Camera.Projection = CameraProjection.Orthographic;
            largeDefinition.RestoreLights(
                [SceneLightDefinition.CreateDefaultAmbient()],
                lightsWerePresent: true);
            using var largeStage = new StageControl(largeScene)
            {
                ClientSize = new Size(2_897, 2_897),
                WorldGridOpacity = 0
            };
            largeStage.ConfigureReferenceView(largeDefinition, SceneDimension.ThreeD);
            largeStage.ResetReferenceCameraView();
            largeStage.SetReferenceCameraOrientation(0, 0);
            largeStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    [new SceneCompositionObjectOwner("large-gradient", "fixture")],
                    [new SceneCompositionObjectPose(Matrix4x4.Identity)],
                    [SpatialOpticalMaterial.Default with { Reflectivity = 0f }]),
                largeScene);
            var largeSurface = largeStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == largeObject
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .Select(item => item.OpticalSurface)
                .OfType<Reference3DOpticalSurface>()
                .SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "An oversized cross-hue gradient did not build a reduced optical surface.");
            if (largeSurface.PixelWidth >= largeSurface.Bounds.Width
                || largeSurface.PixelHeight >= largeSurface.Bounds.Height
                || (long)largeSurface.PixelWidth * largeSurface.PixelHeight > 8_388_608
                || largeSurface.PremultipliedPixels.Length
                    != largeSurface.PixelWidth * largeSurface.PixelHeight)
            {
                throw new InvalidOperationException(
                    "The oversized optical surface exceeded its raster budget or retained 1:1 dimensions.");
            }
            if (!largeStage.TryProjectScenePoint(
                    largeObject,
                    new PointF(-30_000, 0),
                    out var largeLeft,
                    out _)
                || !largeStage.TryProjectScenePoint(
                    largeObject,
                    PointF.Empty,
                    out var largeMiddle,
                    out _)
                || !largeStage.TryProjectScenePoint(
                    largeObject,
                    new PointF(30_000, 0),
                    out var largeRight,
                    out _)
                || !largeSurface.TryGetPremultipliedPixelAtScreenPoint(
                    largeLeft.X,
                    largeLeft.Y,
                    out var largeLeftPixel)
                || !largeSurface.TryGetPremultipliedPixelAtScreenPoint(
                    largeMiddle.X,
                    largeMiddle.Y,
                    out var largeMiddlePixel)
                || !largeSurface.TryGetPremultipliedPixelAtScreenPoint(
                    largeRight.X,
                    largeRight.Y,
                    out var largeRightPixel))
            {
                throw new InvalidOperationException(
                    "The reduced optical surface could not map screen samples back to raster pixels.");
            }
            var largeLeftColor = UnpremultiplyColor(largeLeftPixel);
            var largeRightColor = UnpremultiplyColor(largeRightPixel);
            if (Math.Abs(largeLeftColor.A - 72) > 4
                || largeMiddlePixel != 0
                || Math.Abs(largeRightColor.A - 184) > 4
                || largeLeftColor.R <= largeLeftColor.B + 24
                || largeRightColor.B <= largeRightColor.R + 24)
            {
                throw new InvalidOperationException(
                    "Reduced optical rasterization changed a cross-hue gradient's color or alpha: "
                    + $"left={largeLeftPixel:X8}, middle={largeMiddlePixel:X8}, "
                    + $"right={largeRightPixel:X8}.");
            }

            void AssertTransactionalOpticalSurfaceFallback()
            {
                var retryScene = new VectorScene();
                retryScene.CreateEmpty();
                var retryLeft = retryScene.AddObject(
                    0,
                    new PointF(-850, 0),
                    new SizeF(1_200, 1_000),
                    angle: 0,
                    stroke: 0,
                    color: Color.FromArgb(255, 214, 64, 76),
                    strokeColor: Color.Transparent,
                    atoms: 12,
                    shapeKind: ShapeKind.Rectangle);
                var retryRight = retryScene.AddObject(
                    0,
                    new PointF(850, 0),
                    new SizeF(1_200, 1_000),
                    angle: 0,
                    stroke: 0,
                    color: Color.FromArgb(255, 48, 138, 224),
                    strokeColor: Color.Transparent,
                    atoms: 12,
                    shapeKind: ShapeKind.Rectangle);
                var retryDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
                retryDefinition.Camera.Projection = CameraProjection.Orthographic;
                retryDefinition.RestoreLights(
                    [SceneLightDefinition.CreateDefaultAmbient()],
                    lightsWerePresent: true);
                using var retryStage = new StageControl(retryScene)
                {
                    ClientSize = new Size(360, 240),
                    WorldGridOpacity = 0
                };
                retryStage.ConfigureReferenceView(retryDefinition, SceneDimension.ThreeD);
                retryStage.ResetReferenceCameraView();
                retryStage.SetReferenceCameraOrientation(0, 0);
                retryStage.SetSceneCompositionResult(
                    new SceneCompositionResult(
                        [
                            new SceneCompositionObjectOwner("retry-left", "fixture"),
                            new SceneCompositionObjectOwner("retry-right", "fixture")
                        ],
                        [
                            new SceneCompositionObjectPose(Matrix4x4.Identity),
                            new SceneCompositionObjectPose(Matrix4x4.Identity)
                        ],
                        [SpatialOpticalMaterial.Default, SpatialOpticalMaterial.Default]),
                    retryScene);
                retryStage.Reference3DOpticalSurfaceBuildFailureCountdownForTesting = 2;
                Reference3DRenderItem[] retryItems;
                try
                {
                    retryItems = retryStage.GetReference3DSceneRenderItems();
                }
                finally
                {
                    retryStage.Reference3DOpticalSurfaceBuildFailureCountdownForTesting = 0;
                }

                var retrySurfaces = retryItems
                    .Where(item => item.Kind == Reference3DRenderKind.FrontFill
                        && (item.ObjectIndex == retryLeft || item.ObjectIndex == retryRight))
                    .Select(item => item.OpticalSurface)
                    .OfType<Reference3DOpticalSurface>()
                    .ToArray();
                if (retrySurfaces.Length != 2
                    || retrySurfaces.Any(surface =>
                        surface.PixelWidth != (int)Math.Ceiling(surface.Bounds.Width / 2d)
                        || surface.PixelHeight != (int)Math.Ceiling(surface.Bounds.Height / 2d)))
                {
                    throw new InvalidOperationException(
                        "A partial optical-surface build failure did not retry the whole plan at one LOD.");
                }

                var fallbackScene = new VectorScene();
                fallbackScene.CreateEmpty();
                fallbackScene.LayerOpacity[0] = 0.5f;
                var fallbackObject = fallbackScene.AddObject(
                    0,
                    PointF.Empty,
                    new SizeF(1_800, 1_200),
                    angle: 0,
                    stroke: 0,
                    color: Color.FromArgb(255, 120, 168, 216),
                    strokeColor: Color.Transparent,
                    atoms: 12,
                    shapeKind: ShapeKind.Rectangle);
                var fallbackDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
                fallbackDefinition.Camera.Projection = CameraProjection.Orthographic;
                fallbackDefinition.RestoreLights(
                    [SceneLightDefinition.CreateDefaultAmbient()],
                    lightsWerePresent: true);
                using var fallbackStage = new StageControl(fallbackScene)
                {
                    ClientSize = new Size(320, 240),
                    BackColor = Color.Black,
                    WorldGridOpacity = 0
                };
                fallbackStage.ConfigureReferenceView(fallbackDefinition, SceneDimension.ThreeD);
                fallbackStage.ResetReferenceCameraView();
                fallbackStage.SetReferenceCameraOrientation(0, 0);
                fallbackStage.SetSceneCompositionResult(
                    new SceneCompositionResult(
                        [new SceneCompositionObjectOwner("fallback", "fixture")],
                        [new SceneCompositionObjectPose(Matrix4x4.Identity)],
                        [SpatialOpticalMaterial.Default with { Transmission = 0.4f }]),
                    fallbackScene);
                fallbackStage.Reference3DOpticalSurfaceBuildFailureCountdownForTesting = -1;
                Reference3DRenderItem fallbackItem;
                try
                {
                    fallbackItem = fallbackStage.GetReference3DSceneRenderItems()
                        .Single(item => item.ObjectIndex == fallbackObject
                            && item.Kind == Reference3DRenderKind.FrontFill);
                }
                finally
                {
                    fallbackStage.Reference3DOpticalSurfaceBuildFailureCountdownForTesting = 0;
                }

                if (!fallbackStage.TryGetReference3DOpticalSurfaceBounds(fallbackItem, out _)
                    || fallbackStage.Reference3DOpticsPlanBuildCount != 1
                    || fallbackStage.LastReference3DOpticalLightEvaluations <= 0
                    || Math.Abs(fallbackItem.MaterialOpacity - 0.5f) > 0.000001f
                    || fallbackItem.OpticalSurface is not null
                    || fallbackItem.OpticalResponse != Reference3DOpticalResponse.Identity
                    || fallbackItem.LocalLightLayers is not { Length: 0 }
                    || fallbackItem.ShadowLayers is not { Length: 0 })
                {
                    throw new InvalidOperationException(
                        "A real optical build failure did not restore the original vector material state.");
                }
                if (!fallbackStage.TryProjectScenePoint(
                        fallbackObject,
                        PointF.Empty,
                        out var fallbackSample,
                        out _))
                {
                    throw new InvalidOperationException(
                        "The optical fallback sample could not be projected.");
                }
                using var fallbackBitmap = RenderOpticsGdi(fallbackStage);
                var fallbackColor = SampleBitmap(fallbackBitmap, fallbackSample);
                var expectedFallbackColor = Color.FromArgb(255, 60, 84, 108);
                if (Math.Abs(fallbackColor.R - expectedFallbackColor.R) > 3
                    || Math.Abs(fallbackColor.G - expectedFallbackColor.G) > 3
                    || Math.Abs(fallbackColor.B - expectedFallbackColor.B) > 3)
                {
                    throw new InvalidOperationException(
                        "Optical fallback did not render the original material: "
                        + $"actual={fallbackColor.ToArgb():X8}, "
                        + $"expected={expectedFallbackColor.ToArgb():X8}.");
                }
            }

            static void AssertOpticalSurfaceGdiScaling()
            {
                var colors = new[]
                {
                    Color.Red,
                    Color.Lime,
                    Color.Blue,
                    Color.Yellow
                };
                var source = new Bitmap(2, 2);
                source.SetPixel(0, 0, colors[0]);
                source.SetPixel(1, 0, colors[1]);
                source.SetPixel(0, 1, colors[2]);
                source.SetPixel(1, 1, colors[3]);
                var bounds = new Rectangle(3, 2, 2, 2);
                using var surface = new Reference3DOpticalSurface(
                    bounds,
                    source,
                    colors.Select(color => color.ToArgb()).ToArray());
                var sentinel = Color.FromArgb(255, 13, 17, 23);
                var drawSurface = typeof(StageControl).GetMethod(
                    "DrawReference3DOpticalSurface",
                    System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "The optical-surface GDI draw entry point could not be located.");
                using var target = new Bitmap(15, 12);
                using (var graphics = Graphics.FromImage(target))
                {
                    graphics.Clear(sentinel);
                    drawSurface.Invoke(null, [graphics, surface]);
                }

                AssertPixel(bounds.Left, bounds.Top, colors[0], "top-left");
                AssertPixel(bounds.Right - 1, bounds.Top, colors[1], "top-right");
                AssertPixel(bounds.Left, bounds.Bottom - 1, colors[2], "bottom-left");
                AssertPixel(bounds.Right - 1, bounds.Bottom - 1, colors[3], "bottom-right edge");
                AssertPixel(bounds.Left - 1, bounds.Top, sentinel, "left exterior");
                AssertPixel(bounds.Right, bounds.Bottom - 1, sentinel, "right exterior");

                AssertReducedLineSurfaceAntialiasing(drawSurface);

                using var invalidBitmap = new Bitmap(2, 2);
                var rejectedMismatchedPixels = false;
                try
                {
                    _ = new Reference3DOpticalSurface(bounds, invalidBitmap, new int[3]);
                }
                catch (ArgumentException)
                {
                    rejectedMismatchedPixels = true;
                }
                if (!rejectedMismatchedPixels)
                {
                    throw new InvalidOperationException(
                        "An optical surface accepted a pixel buffer that did not match its bitmap.");
                }

                void AssertPixel(int x, int y, Color expected, string label)
                {
                    var actual = target.GetPixel(x, y);
                    if (actual.ToArgb() == expected.ToArgb()) return;
                    throw new InvalidOperationException(
                        $"The optical GDI scaling {label} pixel used the wrong source or destination: "
                        + $"actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
                }

                static void AssertReducedLineSurfaceAntialiasing(
                    System.Reflection.MethodInfo drawSurface)
                {
                    var lineColor = Color.FromArgb(255, 36, 210, 176);
                    using var lineSource = new Bitmap(
                        3,
                        3,
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    for (var y = 0; y < lineSource.Height; y++)
                    {
                        lineSource.SetPixel(1, y, lineColor);
                    }

                    var lineBounds = new Rectangle(3, 2, 12, 12);
                    var linePixels = new int[lineSource.Width * lineSource.Height];
                    for (var y = 0; y < lineSource.Height; y++)
                    {
                        for (var x = 0; x < lineSource.Width; x++)
                        {
                            linePixels[y * lineSource.Width + x] = lineSource.GetPixel(x, y).ToArgb();
                        }
                    }
                    using var lineSurface = new Reference3DOpticalSurface(
                        lineBounds,
                        lineSource,
                        linePixels);
                    using var lineTarget = new Bitmap(18, 16);
                    using (var graphics = Graphics.FromImage(lineTarget))
                    {
                        graphics.Clear(Color.White);
                        drawSurface.Invoke(null, [graphics, lineSurface]);
                    }

                    var hasLineColor = false;
                    var hasTransition = false;
                    var sampleY = lineBounds.Top + lineBounds.Height / 2;
                    for (var x = lineBounds.Left; x < lineBounds.Right; x++)
                    {
                        var actual = lineTarget.GetPixel(x, sampleY);
                        hasLineColor |= actual.G >= actual.R + 72
                            && actual.G >= actual.B + 24;
                        hasTransition |= actual.R > lineColor.R + 12 && actual.R < 243
                            && actual.G > lineColor.G + 3 && actual.G < 252
                            && actual.B > lineColor.B + 3 && actual.B < 250;
                        if (actual.R < lineColor.R - 3
                            || actual.G < lineColor.G - 3
                            || actual.B < lineColor.B - 3)
                        {
                            throw new InvalidOperationException(
                                "Reduced optical line scaling introduced a dark fringe: "
                                + $"x={x}, actual={actual.ToArgb():X8}.");
                        }
                    }
                    if (!hasLineColor || !hasTransition)
                    {
                        throw new InvalidOperationException(
                            "Reduced optical line scaling did not preserve its color with an antialiased edge: "
                            + $"lineColor={hasLineColor}, transition={hasTransition}.");
                    }
                }
            }

            static Reference3DProjectedContour ClosedContour(
                float left,
                float top,
                float right,
                float bottom) => new(
                [
                    new PointF(left, top),
                    new PointF(right, top),
                    new PointF(right, bottom),
                    new PointF(left, bottom)
                ],
                true,
                0f);

            static Color UnpremultiplyColor(int premultipliedArgb)
            {
                var alpha = (premultipliedArgb >>> 24) & 0xff;
                if (alpha == 0) return Color.Transparent;
                return Color.FromArgb(
                    alpha,
                    Unpremultiply((premultipliedArgb >>> 16) & 0xff),
                    Unpremultiply((premultipliedArgb >>> 8) & 0xff),
                    Unpremultiply(premultipliedArgb & 0xff));

                int Unpremultiply(int channel) =>
                    Math.Clamp((channel * 255 + alpha / 2) / alpha, 0, 255);
            }
        }

        void AssertFiniteLightOcclusion(SceneLightKind kind, Vector2 areaSize)
        {
            var settings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                3f,
                2200f,
                new Vector3(0, 0, 1400),
                Vector3.Zero,
                areaSize,
                true,
                1f,
                0.15f);
            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Occlusion fixture",
                    kind,
                    settings)],
                lightsWerePresent: true);
            stage.SetReferenceCameraOrientation(MathF.PI, 0);
            stage.SetSceneCompositionResult(composition, scene);
            var sharpEnergy = ReceiverLocalLightEnergy();

            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Soft occlusion fixture",
                    kind,
                    settings with { ShadowSoftness = 1f })],
                lightsWerePresent: true);
            var softEnergy = ReceiverLocalLightEnergy();
            if (softEnergy <= sharpEnergy + 0.0001d)
            {
                throw new InvalidOperationException(
                    $"Increasing shadow softness did not retain more occluded {kind}-light energy: "
                    + $"sharp={sharpEnergy:F6}, soft={softEnergy:F6}.");
            }

            stage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    owners,
                    poses,
                    [receiverMaterial, casterMaterial with { CastsShadows = false }]),
                scene);
            var unshadowedEnergy = ReceiverLocalLightEnergy();
            if (unshadowedEnergy <= softEnergy + 0.0001d)
            {
                throw new InvalidOperationException(
                    $"An opaque caster did not attenuate direct {kind}-light energy.");
            }

            stage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    owners,
                    poses,
                    [receiverMaterial with { ReceivesShadows = false }, casterMaterial]),
                scene);
            var nonReceivingEnergy = ReceiverLocalLightEnergy();
            if (Math.Abs(nonReceivingEnergy - unshadowedEnergy) > 0.0001d)
            {
                throw new InvalidOperationException(
                    $"A surface with Receive Shadows disabled still lost direct {kind}-light energy.");
            }

            stage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    owners,
                    poses,
                    [receiverMaterial, casterMaterial with { Transmission = 1f }]),
                scene);
            var transmittedEnergy = ReceiverLocalLightEnergy();
            if (Math.Abs(transmittedEnergy - unshadowedEnergy) > 0.0001d)
            {
                throw new InvalidOperationException(
                    $"A fully transmissive caster still blocked direct {kind}-light energy.");
            }
            stage.SetSceneCompositionResult(composition, scene);
            stage.SetReferenceCameraOrientation(0, 0);

            double ReceiverLocalLightEnergy()
            {
                return stage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == receiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .Sum(LocalLightEnergy);
            }
        }

        void AssertDirectionalShadowIsolation()
        {
            var directionalSettings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                1.25f,
                0f,
                Vector3.Zero,
                Vector3.Zero,
                Vector2.Zero,
                true,
                1f,
                0f);
            var pointSettings = new SceneLightSettings(
                true,
                Color.FromArgb(255, 64, 220, 255).ToArgb(),
                2.5f,
                2200f,
                new Vector3(0, 0, 1400),
                Vector3.Zero,
                Vector2.Zero,
                false,
                0f,
                0f);
            stage.SetReferenceCameraOrientation(MathF.PI, 0);
            stage.SetSceneCompositionResult(composition, scene);

            var sharp = Capture(directionalSettings);
            var soft = Capture(directionalSettings with { ShadowSoftness = 1f });
            var unshadowed = Capture(directionalSettings with { CastsShadows = false });
            if (sharp.ShadowProjectionCount <= 0 || soft.ShadowProjectionCount <= 0)
            {
                throw new InvalidOperationException(
                    "The directional-light isolation fixture did not project an opaque caster.");
            }
            if (soft.DirectionalEnergy <= sharp.DirectionalEnergy + 0.0001d
                || unshadowed.DirectionalEnergy <= soft.DirectionalEnergy + 0.0001d)
            {
                throw new InvalidOperationException(
                    "Directional-light softness/shadow state did not progressively restore only its occluded energy: "
                    + $"sharp={sharp.DirectionalEnergy:F6}, soft={soft.DirectionalEnergy:F6}, "
                    + $"unshadowed={unshadowed.DirectionalEnergy:F6}.");
            }
            if (!SameLocalLightLayers(sharp.PointLayers, soft.PointLayers)
                || !SameLocalLightLayers(sharp.PointLayers, unshadowed.PointLayers))
            {
                throw new InvalidOperationException(
                    "Changing directional-light shadow state altered the coexisting point-light contribution.");
            }
            if (sharp.OpticalResponse != soft.OpticalResponse
                || sharp.OpticalResponse != unshadowed.OpticalResponse
                || sharp.HasGlobalShadowLayers
                || soft.HasGlobalShadowLayers
                || unshadowed.HasGlobalShadowLayers)
            {
                throw new InvalidOperationException(
                    "Directional-light occlusion changed base illumination or retained a global black shadow layer.");
            }

            stage.SetSceneCompositionResult(composition, scene);
            stage.SetReferenceCameraOrientation(0, 0);

            (Reference3DLocalLightLayer[] PointLayers,
                double DirectionalEnergy,
                Reference3DOpticalResponse OpticalResponse,
                int ShadowProjectionCount,
                bool HasGlobalShadowLayers) Capture(SceneLightSettings settings)
            {
                definition.RestoreLights(
                    [
                        new SceneLightDefinition(
                            "directional-isolation",
                            "Directional isolation",
                            SceneLightKind.Directional,
                            settings),
                        new SceneLightDefinition(
                            "point-isolation",
                            "Point isolation",
                            SceneLightKind.Point,
                            pointSettings),
                        new SceneLightDefinition(
                            "ambient-isolation",
                            "Ambient isolation",
                            SceneLightKind.Ambient,
                            new SceneLightSettings(
                                true,
                                Color.White.ToArgb(),
                                0.5f,
                                0f,
                                Vector3.Zero,
                                Vector3.Zero,
                                Vector2.Zero,
                                false,
                                0f,
                                0f))
                    ],
                    lightsWerePresent: true);
                var renderItems = stage.GetReference3DSceneRenderItems();
                var receiverItems = renderItems
                    .Where(item => item.ObjectIndex == receiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .ToArray();
                var layers = receiverItems.SelectMany(item => item.LocalLightLayers ?? []).ToArray();
                var pointLayers = layers.Where(layer =>
                    layer.LinearStops.Length == 13).ToArray();
                var directionalLayers = layers.Where(layer =>
                    layer.LinearStops.Length == 2).ToArray();
                if (receiverItems.Length == 0 || pointLayers.Length == 0 || directionalLayers.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The directional-shadow isolation fixture could not identify its direct-light layers: "
                        + $"receivers={receiverItems.Length}, point={pointLayers.Length}, "
                        + $"directional={directionalLayers.Length}.");
                }
                return (
                    pointLayers,
                    directionalLayers.Sum(LocalLightEnergy),
                    receiverItems[0].OpticalResponse,
                    stage.LastReference3DShadowProjectionCount,
                    renderItems.Any(item => item.ShadowLayers is { Length: > 0 }));
            }

            static bool SameLocalLightLayers(
                IReadOnlyList<Reference3DLocalLightLayer> left,
                IReadOnlyList<Reference3DLocalLightLayer> right)
            {
                if (left.Count != right.Count)
                {
                    return false;
                }
                for (var layerIndex = 0; layerIndex < left.Count; layerIndex++)
                {
                    var leftLayer = left[layerIndex];
                    var rightLayer = right[layerIndex];
                    if (leftLayer.GradientTransform != rightLayer.GradientTransform
                        || !leftLayer.LinearStops.AsSpan().SequenceEqual(rightLayer.LinearStops)
                        || leftLayer.Contours.Length != rightLayer.Contours.Length)
                    {
                        return false;
                    }
                    for (var contourIndex = 0;
                         contourIndex < leftLayer.Contours.Length;
                         contourIndex++)
                    {
                        var leftContour = leftLayer.Contours[contourIndex];
                        var rightContour = rightLayer.Contours[contourIndex];
                        if (leftContour.Closed != rightContour.Closed
                            || leftContour.AverageDepth != rightContour.AverageDepth
                            || leftContour.HasSourceStart != rightContour.HasSourceStart
                            || leftContour.HasSourceEnd != rightContour.HasSourceEnd
                            || !leftContour.Points.AsSpan().SequenceEqual(rightContour.Points))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        void AssertCrossingFragmentLightOcclusion()
        {
            var crossingScene = new VectorScene();
            crossingScene.CreateEmpty();
            var receiver = crossingScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(2_400, 1_800),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 112, 156, 202),
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            var blocker = crossingScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(2_400, 1_800),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 220, 96, 72),
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            definition.Camera.Projection = CameraProjection.Perspective;
            var owners = new[]
            {
                new SceneCompositionObjectOwner("crossing-receiver", "optics"),
                new SceneCompositionObjectOwner("crossing-blocker", "optics")
            };
            var poses = new[]
            {
                new SceneCompositionObjectPose(Matrix4x4.Identity),
                new SceneCompositionObjectPose(Matrix4x4.CreateRotationY(0.62f))
            };
            var ambient = new SceneLightDefinition(
                "crossing-ambient",
                "Crossing ambient",
                SceneLightKind.Ambient,
                new SceneLightSettings(
                    true,
                    Color.White.ToArgb(),
                    0.2f,
                    0f,
                    Vector3.Zero,
                    Vector3.Zero,
                    Vector2.Zero,
                    false,
                    0f,
                    0f));
            var pointSettings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                3f,
                3_000f,
                new Vector3(400, 0, -1_400),
                Vector3.Zero,
                Vector2.Zero,
                true,
                1f,
                0f);
            using var stage = new StageControl(crossingScene)
            {
                ClientSize = new Size(640, 480),
                WorldGridOpacity = 0
            };
            stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
            stage.ResetReferenceCameraView();
            stage.SetReferenceCameraOrientation(0.43f, -0.24f);
            if (!stage.TryProjectScenePosition(
                    new Vector3(400, 0, 0),
                    out var blockedScreen,
                    out _)
                || !stage.TryProjectScenePosition(
                    new Vector3(-900, 0, 0),
                    out var openScreen,
                    out _))
            {
                throw new InvalidOperationException(
                    "The crossing-fragment optics fixture could not project its receiver samples.");
            }

            var materials = new[]
            {
                SpatialOpticalMaterial.Default with { Reflectivity = 0f },
                SpatialOpticalMaterial.Default with { Reflectivity = 0f }
            };
            var results = new Dictionary<SceneLightKind, (float Blocked, float Open)>();
            foreach (var kind in new[]
                     {
                         SceneLightKind.Point,
                         SceneLightKind.Area,
                         SceneLightKind.Directional
                     })
            {
                var settings = kind switch
                {
                    SceneLightKind.Directional => pointSettings with
                    {
                        Range = 0f,
                        Position = Vector3.Zero,
                        RotationDegrees = new Vector3(0, 180, 0),
                        AreaSize = Vector2.Zero
                    },
                    SceneLightKind.Area => pointSettings with
                    {
                        AreaSize = new Vector2(240f, 240f)
                    },
                    _ => pointSettings
                };
                var samples = new (
                    float Transmission,
                    float Blocked,
                    float Open,
                    int ShadowProjections)[3];
                for (var transmissionIndex = 0; transmissionIndex < 3; transmissionIndex++)
                {
                    var transmission = transmissionIndex * 0.5f;
                    definition.RestoreLights(
                        [
                            new SceneLightDefinition(
                                $"crossing-{kind}",
                                $"Crossing {kind}",
                                kind,
                                settings),
                            ambient
                        ],
                        lightsWerePresent: true);
                    materials[1] = SpatialOpticalMaterial.Default with
                    {
                        Reflectivity = 0f,
                        Transmission = transmission
                    };
                    stage.SetSceneCompositionResult(
                        new SceneCompositionResult(owners, poses, materials),
                        crossingScene);
                    var renderItems = stage.GetReference3DSceneRenderItems();
                    var receiverItems = renderItems
                        .Where(item => item.ObjectIndex == receiver
                            && item.Kind == Reference3DRenderKind.FrontFill)
                        .ToArray();
                    if (receiverItems.Length < 2
                        || !renderItems.Any(item => item.ObjectIndex == blocker
                            && item.Kind == Reference3DRenderKind.FrontFill
                            && item.FragmentClip is { Length: > 0 }))
                    {
                        throw new InvalidOperationException(
                            $"The crossing {kind} fixture did not preserve physical intersection fragments.");
                    }

                    samples[transmissionIndex] = (
                        transmission,
                        SampleReceiverDirectLight(
                            receiverItems,
                            blockedScreen),
                        SampleReceiverDirectLight(
                            receiverItems,
                            openScreen),
                        stage.LastReference3DShadowProjectionCount);
                }

                var opaque = samples[0];
                var half = samples[1];
                var clear = samples[2];
                if (opaque.Blocked >= opaque.Open - 0.01f
                    || half.Blocked <= opaque.Blocked + 0.005f
                    || clear.Blocked <= half.Blocked + 0.005f)
                {
                    throw new InvalidOperationException(
                        $"The crossing {kind} blocker did not attenuate direct light by transmission: "
                        + $"opaque=({opaque.Blocked:F4},{opaque.Open:F4}), "
                        + $"half=({half.Blocked:F4},{half.Open:F4}), "
                        + $"clear=({clear.Blocked:F4},{clear.Open:F4}), "
                        + $"projections=({opaque.ShadowProjections},{half.ShadowProjections},"
                        + $"{clear.ShadowProjections}).");
                }
                results[kind] = (opaque.Blocked, opaque.Open);
            }

            if (results.Count != 3)
            {
                throw new InvalidOperationException(
                    "The crossing-fragment optics fixture did not exercise all direct-light paths.");
            }

            static float SampleReceiverDirectLight(
                IReadOnlyList<Reference3DRenderItem> receiverItems,
                PointF screenPoint)
            {
                var point = new Vector2(screenPoint.X, screenPoint.Y);
                var energy = Vector3.Zero;
                var foundReceiver = false;
                foreach (var item in receiverItems)
                {
                    if (!ContainsPoint(VisibleSurfacePaths(item), point)) continue;
                    foundReceiver = true;
                    foreach (var layer in item.LocalLightLayers ?? [])
                    {
                        if (layer.LinearStops.Length == 0
                            || !ContainsPoint(Paths(layer.Contours), point)
                            || !Matrix3x2.Invert(layer.GradientTransform, out var inverse))
                        {
                            continue;
                        }

                        var gradientPoint = Vector2.Transform(point, inverse);
                        var position = Math.Clamp(gradientPoint.Length(), 0f, 1f);
                        var stop = SampleStops(layer.LinearStops, position);
                        energy += stop.DiffuseIrradiance + stop.SpecularRadiance + stop.FresnelRadiance;
                    }
                }
                if (!foundReceiver)
                {
                    throw new InvalidOperationException(
                        $"The crossing-fragment sample ({screenPoint.X:F2},{screenPoint.Y:F2}) missed its receiver.");
                }
                return Luminance(energy);
            }

            static Reference3DLinearLightStop SampleStops(
                IReadOnlyList<Reference3DLinearLightStop> stops,
                float position)
            {
                position = float.IsFinite(position) ? Math.Clamp(position, 0f, 1f) : 1f;
                if (stops.Count == 1 || position <= stops[0].Position)
                {
                    return stops[0] with { Position = position };
                }
                if (position >= stops[^1].Position) return stops[^1] with { Position = position };

                var upper = 1;
                while (upper < stops.Count && position > stops[upper].Position) upper++;
                var current = stops[upper];
                var previous = stops[upper - 1];
                var distance = current.Position - previous.Position;
                var amount = distance <= 0.000001f
                    ? 0f
                    : Math.Clamp((position - previous.Position) / distance, 0f, 1f);
                return new Reference3DLinearLightStop(
                    position,
                    Vector3.Lerp(previous.DiffuseIrradiance, current.DiffuseIrradiance, amount),
                    Vector3.Lerp(previous.SpecularRadiance, current.SpecularRadiance, amount),
                    Vector3.Lerp(previous.FresnelRadiance, current.FresnelRadiance, amount));
            }

            static bool ContainsPoint(IReadOnlyList<PathD> paths, Vector2 point)
            {
                var inside = false;
                foreach (var path in paths)
                {
                    if (path.Count < 3) continue;
                    var pathContainsPoint = false;
                    var previous = path[^1];
                    foreach (var current in path)
                    {
                        if ((current.y > point.Y) != (previous.y > point.Y))
                        {
                            var crossX = previous.x
                                + (point.Y - previous.y) * (current.x - previous.x)
                                / (current.y - previous.y);
                            if (point.X < crossX) pathContainsPoint = !pathContainsPoint;
                        }
                        previous = current;
                    }
                    if (pathContainsPoint) inside = !inside;
                }
                return inside;
            }
        }

        void AssertFiniteLightSurvivesPerspectiveDolly(
            SceneLightKind kind,
            Vector2 areaSize)
        {
            var dollyScene = new VectorScene();
            dollyScene.CreateEmpty();
            var dollyReceiver = dollyScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(800, 600),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 112, 156, 202),
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            var dollyDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            dollyDefinition.Camera.Projection = CameraProjection.Perspective;
            const float range = 4000f;
            const float normalDistance = 1000f;
            dollyDefinition.RestoreLights(
                [new SceneLightDefinition(
                    $"{kind}-perspective-dolly",
                    $"{kind} perspective dolly",
                    kind,
                    new SceneLightSettings(
                        true,
                        Color.White.ToArgb(),
                        3f,
                        range,
                        new Vector3(0, 0, normalDistance),
                        Vector3.Zero,
                        areaSize,
                        false,
                        0f,
                        kind == SceneLightKind.Area ? 0.35f : 0f))],
                lightsWerePresent: true);
            var dollyComposition = new SceneCompositionResult(
                [new SceneCompositionObjectOwner("dolly-receiver", "fixture")],
                [new SceneCompositionObjectPose(Matrix4x4.Identity)],
                [SpatialOpticalMaterial.Default]);
            using var dollyStage = new StageControl(dollyScene)
            {
                ClientSize = new Size(640, 480),
                WorldGridOpacity = 0
            };
            dollyStage.ConfigureReferenceView(dollyDefinition, SceneDimension.ThreeD);
            dollyStage.ResetReferenceCameraView();
            dollyStage.SetReferenceCameraOrientation(MathF.PI, 0.6f);
            dollyStage.SetSceneCompositionResult(dollyComposition, dollyScene);

            var footprintRadius = MathF.Sqrt(
                range * range - normalDistance * normalDistance);
            var clippedFootprintPoint = new Vector3(0, footprintRadius, 0);
            if (!dollyStage.TryProjectScenePosition(
                    clippedFootprintPoint,
                    out _,
                    out var farFootprintDepth)
                || farFootprintDepth <= StageControl.ReferenceNearPlane)
            {
                throw new InvalidOperationException(
                    $"The distant {kind} dolly fixture did not begin in front of the near plane.");
            }
            var distant = CaptureDollyLight();

            for (var step = 0;
                 step < 32 && dollyStage.ReferenceDistance > 2000.001f;
                 step++)
            {
                dollyStage.DollyReferenceCamera(+120);
            }
            if (Math.Abs(dollyStage.ReferenceDistance - 2000f) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"The {kind} dolly fixture did not reach the nearest reference-camera distance.");
            }
            foreach (var corner in new[]
                     {
                         new Vector3(-400, -300, 0),
                         new Vector3(400, -300, 0),
                         new Vector3(400, 300, 0),
                         new Vector3(-400, 300, 0)
                     })
            {
                if (!dollyStage.TryProjectScenePosition(corner, out _, out var cornerDepth)
                    || cornerDepth <= StageControl.ReferenceNearPlane)
                {
                    throw new InvalidOperationException(
                        $"The near {kind} dolly fixture clipped its receiver instead of only the light footprint.");
                }
            }
            if (dollyStage.TryProjectScenePosition(
                    clippedFootprintPoint,
                    out _,
                    out var nearFootprintDepth)
                || nearFootprintDepth >= StageControl.ReferenceNearPlane)
            {
                throw new InvalidOperationException(
                    $"The near {kind} dolly fixture did not cross the camera near plane.");
            }

            var nearby = CaptureDollyLight();
            if (distant.Length != nearby.Length)
            {
                throw new InvalidOperationException(
                    $"Camera dolly changed the {kind} world-space light sample count: "
                    + $"far={distant.Length}, near={nearby.Length}.");
            }
            for (var layerIndex = 0; layerIndex < distant.Length; layerIndex++)
            {
                if (!distant[layerIndex].AsSpan().SequenceEqual(nearby[layerIndex]))
                {
                    throw new InvalidOperationException(
                        $"Camera dolly changed {kind} diffuse energy at fixed world positions.");
                }
            }

            Reference3DLinearLightStop[][] CaptureDollyLight()
            {
                var receiverItems = dollyStage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == dollyReceiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .ToArray();
                var layers = receiverItems
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .ToArray();
                var expectedLayers = kind == SceneLightKind.Area ? 9 : 1;
                if (receiverItems.Length == 0 || layers.Length != expectedLayers)
                {
                    throw new InvalidOperationException(
                        $"The {kind} dolly fixture lost direct-light layers: "
                        + $"receivers={receiverItems.Length}, layers={layers.Length}, "
                        + $"expected={expectedLayers}.");
                }
                var receiverDomain = new PathsD();
                foreach (var item in receiverItems)
                {
                    receiverDomain.AddRange(VisibleSurfacePaths(item));
                }
                foreach (var layer in layers)
                {
                    var paths = Paths(layer.Contours);
                    var outside = Clipper.Difference(
                        paths,
                        receiverDomain,
                        FillRule.EvenOdd,
                        precision: 4);
                    var footprintArea = Area(paths);
                    var outsideArea = Area(outside);
                    var outsideTolerance = Math.Max(0.25d, footprintArea * 0.00001d);
                    var hasInvalidPoint = layer.Contours
                        .SelectMany(contour => contour.Points)
                        .Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y));
                    if (footprintArea <= 0.01d
                        || outsideArea > outsideTolerance
                        || hasInvalidPoint)
                    {
                        throw new InvalidOperationException(
                            $"The {kind} dolly fixture produced an invalid receiver-clipped light footprint: "
                            + $"area={footprintArea:F6}, outside={outsideArea:F6}, "
                            + $"invalidPoint={hasInvalidPoint}.");
                    }
                }
                return layers
                    .Select(layer => layer.LinearStops
                        .Select(stop => stop with { SpecularRadiance = Vector3.Zero, FresnelRadiance = Vector3.Zero })
                        .ToArray())
                    .ToArray();
            }
        }

        static double LocalLightEnergy(Reference3DLocalLightLayer layer)
        {
            var area = Area(Paths(layer.Contours));
            var stops = layer.LinearStops;
            if (stops.Length == 0 || area <= 0d) return 0d;
            var weighted = 0d;
            for (var index = 0; index + 1 < stops.Length; index++)
            {
                var start = stops[index];
                var end = stops[index + 1];
                var annulus = Math.Max(0d,
                    end.Position * end.Position - start.Position * start.Position);
                weighted += annulus * 0.5d * (
                    Luminance(start.DiffuseIrradiance) + Luminance(start.SpecularRadiance)
                    + Luminance(end.DiffuseIrradiance) + Luminance(end.SpecularRadiance));
            }
            return area * weighted;
        }

        static float Luminance(Vector3 value)
        {
            if (!float.IsFinite(value.X)
                || !float.IsFinite(value.Y)
                || !float.IsFinite(value.Z))
            {
                return 0f;
            }
            return Math.Max(0f, value.X) * 0.2126f
                + Math.Max(0f, value.Y) * 0.7152f
                + Math.Max(0f, value.Z) * 0.0722f;
        }

        void AssertZeroThicknessVisibleSidePointLight()
        {
            var settings = new SceneLightSettings(
                true,
                Color.White.ToArgb(),
                60.742f,
                513.1f,
                new Vector3(-316f, 1f, -240f),
                Vector3.Zero,
                Vector2.Zero,
                true,
                0.8f,
                0.35f);
            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Backside zero-thickness fixture",
                    SceneLightKind.Point,
                    settings)],
                lightsWerePresent: true);
            var isolatedPoses = new[]
            {
                poses[0],
                new SceneCompositionObjectPose(
                    Matrix4x4.CreateTranslation(10_000, 0, 600))
            };
            var isolatedComposition = new SceneCompositionResult(
                owners,
                isolatedPoses,
                [receiverMaterial, casterMaterial]);
            stage.SetReferenceCameraOrientation(0, 0);
            stage.SetSceneCompositionResult(isolatedComposition, scene);

            var visibleSideStops = ReceiverDiffuseStops();
            if (visibleSideStops.Length == 0
                || visibleSideStops.All(stop => Luminance(stop.DiffuseIrradiance) <= 0f))
            {
                throw new InvalidOperationException(
                    "A negative-Z point light in range did not illuminate a zero-thickness receiver.");
            }

            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Frontside zero-thickness fixture",
                    SceneLightKind.Point,
                    settings with { Position = new Vector3(-316f, 1f, 240f) })],
                lightsWerePresent: true);
            if (ReceiverDiffuseStops().Length != 0)
            {
                throw new InvalidOperationException(
                    "A point light behind the visible side leaked through a zero-thickness receiver.");
            }

            stage.SetReferenceCameraOrientation(MathF.PI, 0);
            var oppositeVisibleSideStops = ReceiverDiffuseStops();
            if (!visibleSideStops.AsSpan().SequenceEqual(oppositeVisibleSideStops))
            {
                throw new InvalidOperationException(
                    "Turning the camera to the opposite side did not restore symmetric point-light energy.");
            }

            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Backside zero-thickness fixture",
                    SceneLightKind.Point,
                    settings)],
                lightsWerePresent: true);
            stage.SetReferenceCameraOrientation(0.42f, -0.24f);
            var roamedStops = ReceiverDiffuseStops();
            if (!visibleSideStops.AsSpan().SequenceEqual(roamedStops))
            {
                throw new InvalidOperationException(
                    "Camera roaming changed backside point-light energy at fixed world distances.");
            }

            var extrudedPoses = new[]
            {
                new SceneCompositionObjectPose(
                    Matrix4x4.Identity,
                    new Vector3(0, 0, 400)),
                isolatedPoses[1]
            };
            stage.SetReferenceCameraOrientation(0, 0);
            stage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    owners,
                    extrudedPoses,
                    [receiverMaterial, casterMaterial]),
                scene);
            var extrudedItems = stage.GetReference3DSceneRenderItems();
            if (!ReceivesPointLight(extrudedItems, Reference3DRenderKind.FrontFill)
                || ReceivesPointLight(extrudedItems, Reference3DRenderKind.Back))
            {
                throw new InvalidOperationException(
                    "A point light on the front side of an extruded receiver illuminated the back face instead.");
            }

            definition.RestoreLights(
                [new SceneLightDefinition(
                    Guid.NewGuid().ToString("N"),
                    "Backside extruded fixture",
                    SceneLightKind.Point,
                    settings with { Position = new Vector3(-316f, 1f, 240f) })],
                lightsWerePresent: true);
            extrudedItems = stage.GetReference3DSceneRenderItems();
            if (ReceivesPointLight(extrudedItems, Reference3DRenderKind.FrontFill)
                || !ReceivesPointLight(extrudedItems, Reference3DRenderKind.Back))
            {
                throw new InvalidOperationException(
                    "A point light on the back side of an extruded receiver illuminated the front face instead.");
            }
            stage.SetSceneCompositionResult(composition, scene);

            bool ReceivesPointLight(
                IEnumerable<Reference3DRenderItem> items,
                Reference3DRenderKind kind)
            {
                return items.Any(item => item.ObjectIndex == receiver
                    && item.Kind == kind
                    && item.LocalLightLayers is { Length: > 0 });
            }

            Reference3DLinearLightStop[] ReceiverDiffuseStops()
            {
                return stage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == receiver
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .SelectMany(layer => layer.LinearStops)
                    .Select(stop => stop with { SpecularRadiance = Vector3.Zero, FresnelRadiance = Vector3.Zero })
                    .Distinct()
                    .OrderBy(stop => stop.Position)
                    .ThenBy(stop => stop.DiffuseIrradiance.X)
                    .ThenBy(stop => stop.DiffuseIrradiance.Y)
                    .ThenBy(stop => stop.DiffuseIrradiance.Z)
                    .ToArray();
            }
        }

        void AssertFrontStrokeReceivesLighting()
        {
            var strokeScene = new VectorScene();
            strokeScene.CreateEmpty();
            var background = Color.FromArgb(255, 236, 239, 244);
            var strokeObject = strokeScene.AddLineSegment(
                0,
                new PointF(-1_200, 0),
                new PointF(1_200, 0),
                stroke: 360,
                color: Color.Transparent,
                strokeColor: Color.FromArgb(255, 48, 112, 204),
                atoms: 24);
            var strokeDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            strokeDefinition.Camera.Projection = CameraProjection.Orthographic;
            var directional = new SceneLightDefinition(
                Guid.NewGuid().ToString("N"),
                "Stroke key",
                SceneLightKind.Directional,
                new SceneLightSettings(
                    true,
                    Color.FromArgb(255, 255, 226, 166).ToArgb(),
                    2.4f,
                    0,
                    Vector3.Zero,
                    Vector3.Zero,
                    Vector2.Zero,
                    false,
                    0,
                    0));
            strokeDefinition.RestoreLights([directional], lightsWerePresent: true);
            var strokeMaterial = SpatialOpticalMaterial.Default with
            {
                Transmission = 0.3f,
                Reflectivity = 0.24f,
                Roughness = 0.3f
            };
            var strokeComposition = new SceneCompositionResult(
                [new SceneCompositionObjectOwner("stroke", "fixture")],
                [new SceneCompositionObjectPose(Matrix4x4.Identity)],
                [strokeMaterial]);
            using var strokeStage = new StageControl(strokeScene)
            {
                ClientSize = new Size(640, 480),
                BackColor = background,
                WorldGridOpacity = 0
            };
            strokeStage.ConfigureReferenceView(strokeDefinition, SceneDimension.ThreeD);
            strokeStage.ResetReferenceCameraView();
            strokeStage.SetReferenceCameraOrientation(MathF.PI, 0);
            strokeStage.SetSceneCompositionResult(strokeComposition, strokeScene);

            var strokeItems = strokeStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == strokeObject
                    && item.Kind == Reference3DRenderKind.FrontStroke)
                .ToArray();
            if (strokeItems.Length == 0
                || strokeItems.Any(item => item.OpticalSurfaceContours is not { Length: > 0 })
                || strokeItems.Any(item => item.LocalLightLayers is not { Length: > 0 })
                || strokeItems.Any(item => item.SolidStrokeOpticalBaseArgb is null)
                || strokeItems.Any(item => item.LocalLightLayers!
                    .Any(layer => layer.SolidStrokeStops is not { Length: > 0 }))
                || strokeItems.Any(item => item.OpticalResponse == Reference3DOpticalResponse.Identity))
            {
                throw new InvalidOperationException(
                    "A physical front stroke did not receive the scene optical response and direct light.");
            }
            foreach (var item in strokeItems)
            {
                var strokeDomain = VisibleSurfacePaths(item);
                foreach (var layer in item.LocalLightLayers ?? [])
                {
                    var outside = Clipper.Difference(
                        Paths(layer.Contours),
                        strokeDomain,
                        FillRule.EvenOdd,
                        precision: 4);
                    if (Area(outside) > 0.01d)
                    {
                        throw new InvalidOperationException(
                            "A front-stroke light layer escaped the actual projected stroke footprint.");
                    }
                }
            }

            if (!strokeStage.TryProjectScenePoint(
                    strokeObject,
                    PointF.Empty,
                    out var strokeSample,
                    out _)
                || !strokeStage.TryProjectScenePoint(
                    strokeObject,
                    new PointF(0, 600),
                    out var outsideSample,
                    out _)
                || !strokeStage.TryProjectScenePoint(
                    strokeObject,
                    new PointF(0, 179),
                    out var edgeSample,
                    out _))
            {
                throw new InvalidOperationException(
                    "The front-stroke lighting fixture could not project its center, outside, and edge samples.");
            }
            Color litStroke;
            using (var litBitmap = RenderOpticsGdi(strokeStage))
            {
                litStroke = SampleBitmap(litBitmap, strokeSample);
                AssertStroke(litStroke, "Lighting did not render the line center");
                AssertBackground(litBitmap, outsideSample, "Lighting rendered outside the line footprint");
                AssertNoHardBlackEdge(litBitmap, edgeSample);
            }

            strokeDefinition.RestoreLights([], lightsWerePresent: true);
            using (var unlitBitmap = RenderOpticsGdi(strokeStage))
            {
                var unlitStroke = SampleBitmap(unlitBitmap, strokeSample);
                if (RgbDistance(litStroke, unlitStroke) <= 18)
                {
                    throw new InvalidOperationException(
                        "Enabling scene lighting did not change the rendered front-stroke pixels.");
                }
                AssertStroke(unlitStroke, "Unlit rendering did not preserve the line center");
                AssertBackground(unlitBitmap, outsideSample, "Unlit rendering rendered outside the line footprint");
                AssertNoHardBlackEdge(unlitBitmap, edgeSample);
            }

            strokeScene.SetGradientPaint(
                strokeObject,
                GradientKind.Linear,
                [
                    new GradientStop(0f, Color.FromArgb(255, 236, 36, 28)),
                    new GradientStop(1f, Color.FromArgb(255, 196, 24, 42))
                ],
                new PointF(-1_200, 0),
                new PointF(1_200, 0));
            strokeStage.Invalidate();
            strokeDefinition.RestoreLights([directional], lightsWerePresent: true);
            var gradientStrokeItems = strokeStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == strokeObject
                    && item.Kind == Reference3DRenderKind.FrontStroke)
                .ToArray();
            if (!strokeScene.HasGradient(strokeObject)
                || gradientStrokeItems.Length == 0
                || gradientStrokeItems.Any(item => item.OpticalSurface is not null)
                || gradientStrokeItems.Any(item =>
                    item.OpticalSurfaceContours is not { Length: > 0 }
                    || item.LocalLightLayers is not { Length: > 0 }
                    || item.OpticalResponse == Reference3DOpticalResponse.Identity))
            {
                throw new InvalidOperationException(
                    "A non-Line front stroke did not retain its vector optical response after adding a fill gradient.");
            }
            using (var gradientBitmap = RenderOpticsGdi(strokeStage))
            {
                var gradientStroke = SampleBitmap(gradientBitmap, strokeSample);
                if (gradientStroke.R <= gradientStroke.B + 30)
                {
                    throw new InvalidOperationException(
                        "A Line front stroke did not use its red stroke gradient after the gradient paint was applied: "
                        + $"gradient={gradientStroke.ToArgb():X8}, lit={litStroke.ToArgb():X8}.");
                }
                AssertStroke(gradientStroke, "A lit fill gradient lost the line center");
                AssertBackground(
                    gradientBitmap,
                    outsideSample,
                    "A lit fill gradient rendered outside the line footprint");
                AssertNoHardBlackEdge(gradientBitmap, edgeSample);
            }

            strokeDefinition.RestoreLights(
                [new SceneLightDefinition(
                    "stroke-gradient-ambient",
                    "Stroke gradient environment",
                    SceneLightKind.Ambient,
                    new SceneLightSettings(
                        true,
                        Color.White.ToArgb(),
                        0.7f,
                        0f,
                        Vector3.Zero,
                        Vector3.Zero,
                        Vector2.Zero,
                        false,
                        0f,
                        0f))],
                lightsWerePresent: true);
            strokeStage.Invalidate();
            Color ambientGradientStroke;
            using (var ambientGradientBitmap = RenderOpticsGdi(strokeStage))
            {
                ambientGradientStroke = SampleBitmap(ambientGradientBitmap, strokeSample);
                AssertStroke(
                    ambientGradientStroke,
                    "Ambient lighting did not render the gradient line center");
                AssertNoHardBlackEdge(ambientGradientBitmap, edgeSample);
            }

            strokeDefinition.RestoreLights([], lightsWerePresent: true);
            strokeStage.Invalidate();
            using (var unlitGradientBitmap = RenderOpticsGdi(strokeStage))
            {
                var unlitGradientStroke = SampleBitmap(unlitGradientBitmap, strokeSample);
                if (RgbDistance(ambientGradientStroke, unlitGradientStroke) <= 18)
                {
                    throw new InvalidOperationException(
                        "Ambient lighting did not change the rendered gradient front-stroke pixels.");
                }
                AssertStroke(
                    unlitGradientStroke,
                    "Unlit rendering did not preserve the gradient line center");
                AssertNoHardBlackEdge(unlitGradientBitmap, edgeSample);
            }

            void AssertStroke(Color actual, string message)
            {
                if (RgbDistance(actual, background) <= 20)
                {
                    throw new InvalidOperationException(
                        $"{message}: actual={actual.ToArgb():X8}.");
                }
            }

            void AssertNoHardBlackEdge(Bitmap bitmap, PointF point)
            {
                var centerX = (int)MathF.Round(point.X);
                var centerY = (int)MathF.Round(point.Y);
                for (var y = Math.Max(0, centerY - 3);
                     y <= Math.Min(bitmap.Height - 1, centerY + 3);
                     y++)
                {
                    for (var x = Math.Max(0, centerX - 3);
                         x <= Math.Min(bitmap.Width - 1, centerX + 3);
                         x++)
                    {
                        var actual = bitmap.GetPixel(x, y);
                        if (actual.R <= 24 && actual.G <= 24 && actual.B <= 24)
                        {
                            throw new InvalidOperationException(
                                $"The front-stroke edge rendered a hard black pixel: "
                                + $"at=({x},{y}), actual={actual.ToArgb():X8}.");
                        }
                    }
                }
            }

            void AssertBackground(Bitmap bitmap, PointF point, string message)
            {
                var actual = SampleBitmap(bitmap, point);
                if (RgbDistance(actual, background) > 3)
                {
                    throw new InvalidOperationException(
                        $"{message}: actual={actual.ToArgb():X8}, expected={background.ToArgb():X8}.");
                }
            }

            static int RgbDistance(Color left, Color right) =>
                Math.Abs(left.R - right.R)
                + Math.Abs(left.G - right.G)
                + Math.Abs(left.B - right.B);
        }

        void AssertThinFrontStrokePreviewContinuity()
        {
            var thinScene = new VectorScene();
            thinScene.CreateEmpty();
            var background = Color.FromArgb(255, 91, 93, 96);
            var strokeColor = Color.FromArgb(255, 24, 28, 32);
            var thinObject = thinScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(7_200, 5_400),
                angle: 0,
                stroke: VectorUnits.MinimumStrokeUnits,
                color: Color.FromArgb(255, 210, 216, 216),
                strokeColor: strokeColor,
                atoms: 24,
                shapeKind: ShapeKind.Rectangle);
            var thinDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            thinDefinition.Camera.Projection = CameraProjection.Perspective;
            thinDefinition.RestoreLights(
                [new SceneLightDefinition(
                    "thin-stroke-light",
                    "Thin stroke light",
                    SceneLightKind.Point,
                    new SceneLightSettings(
                        true,
                        Color.FromArgb(255, 88, 220, 190).ToArgb(),
                        3f,
                        12_000,
                        new Vector3(0, 0, 2_000),
                        Vector3.Zero,
                        Vector2.Zero,
                        false,
                        0f,
                        0f))],
                lightsWerePresent: true);
            using var thinStage = new StageControl(thinScene)
            {
                ClientSize = new Size(640, 480),
                BackColor = background,
                WorldGridOpacity = 0
            };
            thinStage.ConfigureReferenceView(thinDefinition, SceneDimension.ThreeD);
            thinStage.ResetReferenceCameraView();
            thinStage.SetReferenceCameraOrientation(0.16f, -0.1f);
            thinStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    [new SceneCompositionObjectOwner("thin-stroke", "fixture")],
                    [new SceneCompositionObjectPose(
                        Matrix4x4.Identity,
                        new Vector3(0, 0, 450))],
                    [SpatialOpticalMaterial.Default with { Reflectivity = 0.2f }]),
                thinScene);

            thinStage.BeginReference3DOpticalInteractionPreview();
            try
            {
                var strokeItem = thinStage.GetReference3DSceneRenderItems().Single(item =>
                    item.ObjectIndex == thinObject
                    && item.Kind == Reference3DRenderKind.FrontStroke);
                var fillItem = thinStage.GetReference3DSceneRenderItems().Single(item =>
                    item.ObjectIndex == thinObject
                    && item.Kind == Reference3DRenderKind.FrontFill);
                if (thinStage.LastReference3DOpticalRasterLod != 2
                    || fillItem.OpticalSurface is null
                    || strokeItem.OpticalSurface is not null
                    || strokeItem.OpticalSurfaceContours is not { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        "The thin-stroke continuity fixture did not keep the lit stroke vector-based "
                        + "while the filled surface used the reduced optical preview.");
                }

                using var bitmap = RenderOpticsGdi(thinStage);
                foreach (var contour in strokeItem.Contours.Where(contour => contour.Closed))
                {
                    var maximumGap = 0;
                    var currentGap = 0;
                    for (var pointIndex = 0; pointIndex < contour.Points.Length; pointIndex++)
                    {
                        var start = contour.Points[pointIndex];
                        var end = contour.Points[(pointIndex + 1) % contour.Points.Length];
                        var dx = end.X - start.X;
                        var dy = end.Y - start.Y;
                        var length = MathF.Sqrt(dx * dx + dy * dy);
                        var samples = Math.Max(2, (int)MathF.Ceiling(length));
                        for (var sampleIndex = 0; sampleIndex <= samples; sampleIndex++)
                        {
                            var amount = sampleIndex / (float)samples;
                            var point = new PointF(
                                start.X + (end.X - start.X) * amount,
                                start.Y + (end.Y - start.Y) * amount);
                            var foundStroke = false;
                            var centerX = (int)MathF.Round(point.X);
                            var centerY = (int)MathF.Round(point.Y);
                            for (var y = Math.Max(0, centerY - 1);
                                 y <= Math.Min(bitmap.Height - 1, centerY + 1) && !foundStroke;
                                 y++)
                            {
                                for (var x = Math.Max(0, centerX - 1);
                                     x <= Math.Min(bitmap.Width - 1, centerX + 1);
                                     x++)
                                {
                                    var pixel = bitmap.GetPixel(x, y);
                                    if (RgbDistance(pixel, strokeColor) <= 90)
                                    {
                                        foundStroke = true;
                                        break;
                                    }
                                }
                            }
                            if (foundStroke)
                            {
                                currentGap = 0;
                            }
                            else
                            {
                                currentGap++;
                                maximumGap = Math.Max(maximumGap, currentGap);
                            }
                        }
                    }
                    if (maximumGap > 3)
                    {
                        throw new InvalidOperationException(
                            "The reduced optical preview broke a thin 3D stroke into hard segments: "
                            + $"maximum_gap={maximumGap}px.");
                    }
                }
            }
            finally
            {
                thinStage.EndReference3DOpticalInteractionPreview();
            }

            static int RgbDistance(Color left, Color right) =>
                Math.Abs(left.R - right.R)
                + Math.Abs(left.G - right.G)
                + Math.Abs(left.B - right.B);
        }

        void AssertLinearMaterialLighting()
        {
            AssertLighting(
                Color.FromArgb(177, 128, 128, 128),
                new Vector3(0.5f),
                Vector3.Zero,
                Color.FromArgb(177, 92, 92, 92),
                "Linear gray attenuation");
            AssertLighting(
                Color.Red,
                new Vector3(0f, 1f, 1f),
                Vector3.Zero,
                Color.Black,
                "Red material under cyan light");
            AssertLighting(
                Color.Blue,
                new Vector3(0f, 1f, 1f),
                Vector3.Zero,
                Color.Blue,
                "Blue material under cyan light");
            AssertLighting(
                Color.FromArgb(255, 128, 64, 32),
                new Vector3(0.051269f, 0.215861f, 1f),
                Vector3.Zero,
                Color.FromArgb(255, 27, 27, 32),
                "Cross-hue material response");
            AssertLighting(
                Color.FromArgb(0, 255, 48, 96),
                new Vector3(4f, 2f, 3f),
                new Vector3(2f, 3f, 4f),
                Color.Transparent,
                "Transparent material remains transparent");

            AssertColorNear(
                Color.FromArgb(StageControl.EvaluateReference3DLinearLightingArgb(
                    Color.FromArgb(177, 255, 0, 0).ToArgb(),
                    Vector3.Zero, new Vector3(0.25f), 0f, Vector3.Zero, 0.04f)),
                Color.FromArgb(177, 25, 25, 25), 1, "Dielectric F0 reflection");
            AssertColorNear(
                Color.FromArgb(StageControl.EvaluateReference3DLinearLightingArgb(
                    Color.FromArgb(177, 255, 0, 0).ToArgb(),
                    Vector3.Zero, new Vector3(0.25f), 1f, Vector3.Zero, 0.04f)),
                Color.FromArgb(177, 137, 0, 0), 1, "Metal base-color reflection");
            AssertColorNear(
                Color.FromArgb(StageControl.EvaluateReference3DLinearLightingArgb(
                    Color.FromArgb(177, 255, 0, 0).ToArgb(),
                    Vector3.Zero, new Vector3(0.25f), 1f, new Vector3(0.25f), 0.04f)),
                Color.FromArgb(177, 188, 137, 137), 1, "Neutral grazing reflection preserves alpha");

            var gradientScene = new VectorScene();
            gradientScene.CreateEmpty();
            var gradientObject = gradientScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(4_000, 1_800),
                angle: 0,
                stroke: 0,
                color: Color.White,
                strokeColor: Color.Transparent,
                atoms: 16,
                shapeKind: ShapeKind.Rectangle);
            gradientScene.SetGradientPaint(
                gradientObject,
                GradientKind.Linear,
                [
                    new GradientStop(0f, Color.FromArgb(255, 224, 48, 64)),
                    new GradientStop(1f, Color.FromArgb(255, 48, 160, 224))
                ],
                new PointF(-900, 0),
                new PointF(900, 0));
            var gradientDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            gradientDefinition.Camera.Projection = CameraProjection.Orthographic;
            gradientDefinition.RestoreLights(
                [new SceneLightDefinition(
                    "gradient-ambient",
                    "Gradient ambient",
                    SceneLightKind.Ambient,
                    new SceneLightSettings(
                        true,
                        Color.FromArgb(255, 137, 231, 255).ToArgb(),
                        1f,
                        0f,
                        Vector3.Zero,
                        Vector3.Zero,
                        Vector2.Zero,
                        false,
                        0f,
                        0f))],
                lightsWerePresent: true);
            using var gradientStage = new StageControl(gradientScene)
            {
                ClientSize = new Size(640, 480),
                BackColor = Color.FromArgb(255, 17, 21, 27),
                WorldGridOpacity = 0
            };
            gradientStage.ConfigureReferenceView(gradientDefinition, SceneDimension.ThreeD);
            gradientStage.ResetReferenceCameraView();
            gradientStage.SetReferenceCameraOrientation(0, 0);
            gradientStage.SetSceneCompositionResult(
                new SceneCompositionResult(
                    [new SceneCompositionObjectOwner("gradient", "fixture")],
                    [new SceneCompositionObjectPose(Matrix4x4.Identity)],
                    [SpatialOpticalMaterial.Default with { Reflectivity = 0f }]),
                gradientScene);
            var opticalItems = gradientStage.GetReference3DSceneRenderItems()
                .Where(item => item.ObjectIndex == gradientObject
                    && item.Kind == Reference3DRenderKind.FrontFill)
                .ToArray();
            if (opticalItems.Length == 0 || opticalItems.Any(item => item.OpticalSurface is null))
            {
                throw new InvalidOperationException(
                    "The cross-hue gradient did not build a shared optical surface.");
            }
            if (!gradientStage.TryProjectScenePoint(
                    gradientObject,
                    new PointF(-1_400, 0),
                    out var leftSample,
                    out _)
                || !gradientStage.TryProjectScenePoint(
                    gradientObject,
                    PointF.Empty,
                    out var middleSample,
                    out _)
                || !gradientStage.TryProjectScenePoint(
                    gradientObject,
                    new PointF(1_400, 0),
                    out var rightSample,
                    out _))
            {
                throw new InvalidOperationException(
                    "The cross-hue gradient samples could not be projected.");
            }
            using var bitmap = RenderOpticsGdi(gradientStage);
            var left = SampleBitmap(bitmap, leftSample);
            var middle = SampleBitmap(bitmap, middleSample);
            var right = SampleBitmap(bitmap, rightSample);
            AssertColorNear(left, Color.FromArgb(255, 118, 42, 64), 6, "Lit gradient left hue");
            AssertColorNear(middle, Color.FromArgb(255, 66, 93, 144), 10, "Lit gradient middle hue");
            AssertColorNear(right, Color.FromArgb(255, 21, 144, 224), 6, "Lit gradient right hue");
            if (left.R <= left.B || right.B <= right.R || middle.ToArgb() == left.ToArgb()
                || middle.ToArgb() == right.ToArgb())
            {
                throw new InvalidOperationException(
                    "Colored lighting collapsed a cross-hue gradient into one surface color.");
            }

            gradientScene.SetGradientPaint(
                gradientObject,
                GradientKind.Linear,
                [
                    new GradientStop(0f, Color.FromArgb(72, 224, 48, 64)),
                    new GradientStop(0.45f, Color.FromArgb(0, 40, 232, 88)),
                    new GradientStop(0.55f, Color.FromArgb(0, 224, 48, 192)),
                    new GradientStop(1f, Color.FromArgb(184, 48, 160, 224))
                ],
                new PointF(-900, 0),
                new PointF(900, 0));
            gradientStage.Invalidate();
            var cyanLitPixels = CaptureOpticalPixels();
            gradientDefinition.RestoreLights(
                [new SceneLightDefinition(
                    "gradient-ambient-magenta",
                    "Gradient ambient magenta",
                    SceneLightKind.Ambient,
                    new SceneLightSettings(
                        true,
                        Color.FromArgb(255, 255, 96, 224).ToArgb(),
                        2f,
                        0f,
                        Vector3.Zero,
                        Vector3.Zero,
                        Vector2.Zero,
                        false,
                        0f,
                        0f))],
                lightsWerePresent: true);
            gradientStage.Invalidate();
            var magentaLitPixels = CaptureOpticalPixels();
            var expectedAlpha = new[] { 72, 0, 184 };
            for (var index = 0; index < cyanLitPixels.Length; index++)
            {
                var cyanAlpha = (cyanLitPixels[index] >>> 24) & 0xff;
                var magentaAlpha = (magentaLitPixels[index] >>> 24) & 0xff;
                if (Math.Abs(cyanAlpha - expectedAlpha[index]) > 3
                    || cyanAlpha != magentaAlpha)
                {
                    throw new InvalidOperationException(
                        "Lighting changed a gradient pixel's material alpha: "
                        + $"sample={index}, cyan={cyanAlpha}, magenta={magentaAlpha}, "
                        + $"expected={expectedAlpha[index]}.");
                }
            }
            if (cyanLitPixels[1] != 0 || magentaLitPixels[1] != 0)
            {
                throw new InvalidOperationException(
                    "Specular lighting recolored a fully transparent gradient stop.");
            }

            int[] CaptureOpticalPixels()
            {
                var surfaces = gradientStage.GetReference3DSceneRenderItems()
                    .Where(item => item.ObjectIndex == gradientObject
                        && item.Kind == Reference3DRenderKind.FrontFill)
                    .Select(item => item.OpticalSurface)
                    .OfType<Reference3DOpticalSurface>()
                    .ToArray();
                if (surfaces.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The transparent gradient did not build a shared optical surface.");
                }
                return new[] { leftSample, middleSample, rightSample }
                    .Select(point =>
                    {
                        var x = (int)MathF.Round(point.X);
                        var y = (int)MathF.Round(point.Y);
                        foreach (var surface in surfaces)
                        {
                            if (surface.TryGetPremultipliedPixelAtScreenPoint(
                                    x,
                                    y,
                                    out var pixel))
                            {
                                return pixel;
                            }
                        }
                        throw new InvalidOperationException(
                            $"The transparent gradient sample ({x}, {y}) missed its optical surface.");
                    })
                    .ToArray();
            }

            static void AssertLighting(
                Color source,
                Vector3 diffuse,
                Vector3 specular,
                Color expected,
                string label)
            {
                var actual = Color.FromArgb(StageControl.EvaluateReference3DLinearLightingArgb(
                    source.ToArgb(),
                    diffuse,
                    specular));
                AssertColorNear(actual, expected, 1, label);
            }

            static void AssertColorNear(Color actual, Color expected, int tolerance, string label)
            {
                if (Math.Abs(actual.A - expected.A) <= tolerance
                    && Math.Abs(actual.R - expected.R) <= tolerance
                    && Math.Abs(actual.G - expected.G) <= tolerance
                    && Math.Abs(actual.B - expected.B) <= tolerance)
                {
                    return;
                }
                throw new InvalidOperationException(
                    $"{label} diverged: actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
            }
        }

        void AssertShared2D3DFrameOptics()
        {
            var sharedScene = new VectorScene();
            sharedScene.CreateEmpty();
            var receiverObject = sharedScene.AddObject(
                0,
                PointF.Empty,
                new SizeF(2_600, 1_900),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 68, 138, 208),
                strokeColor: Color.Transparent,
                atoms: 16,
                shapeKind: ShapeKind.Rectangle);
            var casterObject = sharedScene.AddObject(
                0,
                new PointF(250, 0),
                new SizeF(900, 700),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 218, 92, 70),
                strokeColor: Color.Transparent,
                atoms: 16,
                shapeKind: ShapeKind.Rectangle);
            var sharedDefinition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
            sharedDefinition.Camera.Projection = CameraProjection.Orthographic;
            var keyStart = new SceneLightSettings(
                true,
                Color.FromArgb(255, 255, 226, 184).ToArgb(),
                0.8f,
                0,
                Vector3.Zero,
                new Vector3(0, 150, 0),
                Vector2.Zero,
                true,
                0.85f,
                0.2f);
            var keyEnd = keyStart with
            {
                ColorArgb = Color.FromArgb(255, 186, 224, 255).ToArgb(),
                Intensity = 2.2f,
                RotationDegrees = new Vector3(0, 165, 0)
            };
            var key = new SceneLightDefinition(
                Guid.NewGuid().ToString("N"),
                "Animated shared key",
                SceneLightKind.Directional,
                keyStart);
            var ambient = SceneLightDefinition.CreateDefaultAmbient();
            _ = ambient.TryApply(
                ambient.Name,
                ambient.Settings with { Intensity = 0.25f });
            sharedDefinition.RestoreLights([key, ambient], lightsWerePresent: true);
            var tweenError = string.Empty;
            if (!sharedDefinition.InsertLightTimelineKeyframe(key.Id, 2)
                || !sharedDefinition.UpdateLightAtFrame(key.Id, key.Name, keyEnd, 2)
                || !sharedDefinition.TryCreateTimelineTween(
                    key.Id,
                    0,
                    2,
                    TimelineTweenKind.Classic,
                    out tweenError))
            {
                throw new InvalidOperationException(
                    $"The shared render-light tween could not be created: {tweenError}");
            }
            var keyTrack = sharedDefinition.Timeline.FindTrackByTargetId(key.Id)
                ?? throw new InvalidOperationException("The shared render-light track was not created.");
            var keyTween = keyTrack.EvaluateTween(1);
            var expectedFrameSettings = SceneLightSettings.Interpolate(
                SceneLightKind.Directional,
                keyStart,
                keyEnd,
                keyTween?.ProgressAt(1) ?? float.NaN);
            if (keyTween is not { Kind: TimelineTweenKind.Classic }
                || !sharedDefinition.TryEvaluateLightSettings(key.Id, 1, out var evaluatedFrameSettings)
                || evaluatedFrameSettings != expectedFrameSettings)
            {
                throw new InvalidOperationException(
                    "The shared optics fixture did not evaluate its light tween at the rendered frame.");
            }

            var owners = new[]
            {
                new SceneCompositionObjectOwner("shared-receiver", "fixture"),
                new SceneCompositionObjectOwner("shared-caster", "fixture")
            };
            var sharedComposition = new SceneCompositionResult(
                owners,
                [
                    new SceneCompositionObjectPose(Matrix4x4.Identity),
                    new SceneCompositionObjectPose(Matrix4x4.CreateTranslation(0, 0, -600))
                ],
                [
                    SpatialOpticalMaterial.Default with
                    {
                        Reflectivity = 0.28f,
                        Roughness = 0.3f
                    },
                    SpatialOpticalMaterial.Default
                ]);
            using var sharedStage = new StageControl(sharedScene)
            {
                ClientSize = new Size(640, 480),
                BackColor = Color.FromArgb(255, 17, 21, 27),
                WorldGridOpacity = 0
            };
            sharedStage.ConfigureReferenceView(sharedDefinition, SceneDimension.TwoD);
            sharedStage.ResetReferenceCameraView();
            sharedStage.SetSceneCompositionResult(sharedComposition, sharedScene);

            sharedStage.Frame = 0;
            var frameZeroItems = sharedStage.GetReference3DSceneRenderItems();
            var frameZeroSignature = DiffuseSignature(frameZeroItems, receiverObject);
            var frameZeroBuilds = sharedStage.Reference3DRenderPlanBuildCount;
            sharedStage.Frame = 1;
            var twoDItems = sharedStage.GetReference3DSceneRenderItems();
            var twoDState = sharedStage.CreateReference3DBaseFrameRenderState();
            var twoDShadowProjections = sharedStage.LastReference3DShadowProjectionCount;
            if (!sharedStage.UsesSceneOpticsProjection
                || sharedStage.EffectiveReferenceProjection != CameraProjection.Orthographic
                || Math.Abs(sharedStage.EffectiveReferenceYaw) > 0.0001f
                || Math.Abs(sharedStage.EffectiveReferencePitch) > 0.0001f
                || sharedStage.Reference3DRenderPlanBuildCount != frameZeroBuilds + 1
                || ReferenceEquals(frameZeroItems, twoDItems)
                || frameZeroSignature.AsSpan().SequenceEqual(
                    DiffuseSignature(twoDItems, receiverObject)))
            {
                throw new InvalidOperationException(
                    "The 2D Scene Building optics plan did not consume the frame-evaluated light state.");
            }
            var twoDBuilds = sharedStage.Reference3DRenderPlanBuildCount;
            if (!ReferenceEquals(twoDItems, sharedStage.GetReference3DSceneRenderItems())
                || sharedStage.Reference3DRenderPlanBuildCount != twoDBuilds)
            {
                throw new InvalidOperationException(
                    "The stable frame-evaluated 2D optics plan was not reused.");
            }
            if (!sharedStage.TryProjectScenePoint(
                    receiverObject,
                    new PointF(-900, 600),
                    out var twoDReceiverSample,
                    out _)
                || !sharedStage.TryProjectScenePoint(
                    casterObject,
                    new PointF(250, 0),
                    out var twoDCasterSample,
                    out _))
            {
                throw new InvalidOperationException(
                    "The shared 2D optics samples could not be projected.");
            }
            Color twoDReceiverPixel;
            Color twoDCasterPixel;
            using (var twoDBitmap = RenderOpticsGdi(sharedStage))
            {
                twoDReceiverPixel = SampleBitmap(twoDBitmap, twoDReceiverSample);
                twoDCasterPixel = SampleBitmap(twoDBitmap, twoDCasterSample);
            }

            sharedStage.ConfigureReferenceView(sharedDefinition, SceneDimension.ThreeD);
            sharedStage.SetReferenceCameraOrientation(0, 0);
            sharedStage.SetSceneCompositionResult(sharedComposition, sharedScene);
            var threeDItems = sharedStage.GetReference3DSceneRenderItems();
            var threeDState = sharedStage.CreateReference3DBaseFrameRenderState();
            var threeDShadowProjections = sharedStage.LastReference3DShadowProjectionCount;
            if (!sharedStage.UsesSceneOpticsProjection
                || sharedStage.EffectiveReferenceProjection != CameraProjection.Orthographic
                || Math.Abs(sharedStage.EffectiveReferenceYaw) > 0.0001f
                || Math.Abs(sharedStage.EffectiveReferencePitch) > 0.0001f
                || twoDState.OpticsState != threeDState.OpticsState
                || twoDShadowProjections <= 0
                || threeDShadowProjections <= 0)
            {
                throw new InvalidOperationException(
                    "The 2D and 3D front views did not share frame lighting, projection, and shadow evaluation.");
            }
            AssertEquivalentPlans(twoDItems, threeDItems);

            if (!sharedStage.TryProjectScenePoint(
                    receiverObject,
                    new PointF(-900, 600),
                    out var threeDReceiverSample,
                    out _)
                || !sharedStage.TryProjectScenePoint(
                    casterObject,
                    new PointF(250, 0),
                    out var threeDCasterSample,
                    out _)
                || Distance(twoDReceiverSample, threeDReceiverSample) > 0.001f
                || Distance(twoDCasterSample, threeDCasterSample) > 0.001f)
            {
                throw new InvalidOperationException(
                    "The equivalent 2D and 3D front cameras projected different optical samples.");
            }
            using (var threeDBitmap = RenderOpticsGdi(sharedStage))
            {
                AssertPixelNear(
                    SampleBitmap(threeDBitmap, threeDReceiverSample),
                    twoDReceiverPixel,
                    "The 2D and 3D front views rendered different receiver lighting");
                AssertPixelNear(
                    SampleBitmap(threeDBitmap, threeDCasterSample),
                    twoDCasterPixel,
                    "The 2D and 3D front views rendered different caster lighting");
            }

            static Reference3DLinearLightStop[] DiffuseSignature(
                IEnumerable<Reference3DRenderItem> items,
                int objectIndex)
            {
                return items
                    .Where(item => item.ObjectIndex == objectIndex)
                    .SelectMany(item => item.LocalLightLayers ?? [])
                    .SelectMany(layer => layer.LinearStops)
                    .Select(stop => stop with { SpecularRadiance = Vector3.Zero, FresnelRadiance = Vector3.Zero })
                    .ToArray();
            }

            static void AssertEquivalentPlans(
                IEnumerable<Reference3DRenderItem> expectedItems,
                IEnumerable<Reference3DRenderItem> actualItems)
            {
                var expected = PhysicalItems(expectedItems);
                var actual = PhysicalItems(actualItems);
                if (expected.Length != actual.Length)
                {
                    throw new InvalidOperationException(
                        $"The shared 2D/3D optics plans had different item counts: {expected.Length}/{actual.Length}.");
                }
                for (var itemIndex = 0; itemIndex < expected.Length; itemIndex++)
                {
                    var left = expected[itemIndex];
                    var right = actual[itemIndex];
                    if (left.ObjectIndex != right.ObjectIndex
                        || left.Kind != right.Kind
                        || left.SurfaceSlot != right.SurfaceSlot
                        || left.FragmentSlot != right.FragmentSlot
                        || left.StableFragmentIdentity != right.StableFragmentIdentity
                        || left.PlaneKey != right.PlaneKey
                        || Math.Abs(left.MaterialOpacity - right.MaterialOpacity) > 0.000001f
                        || left.OpticalResponse != right.OpticalResponse)
                    {
                        throw new InvalidOperationException(
                            $"The shared 2D/3D optical item {itemIndex} changed identity, material, or response.");
                    }
                    AssertContours(left.Contours, right.Contours, $"item {itemIndex} surface");
                    AssertContours(
                        left.FragmentClip ?? [],
                        right.FragmentClip ?? [],
                        $"item {itemIndex} fragment");
                    AssertContours(
                        left.OpticalSurfaceContours ?? [],
                        right.OpticalSurfaceContours ?? [],
                        $"item {itemIndex} optical surface");
                    var leftLayers = left.LocalLightLayers ?? [];
                    var rightLayers = right.LocalLightLayers ?? [];
                    if (leftLayers.Length != rightLayers.Length)
                    {
                        throw new InvalidOperationException(
                            $"The shared 2D/3D optical item {itemIndex} had different direct-light layer counts.");
                    }
                    for (var layerIndex = 0; layerIndex < leftLayers.Length; layerIndex++)
                    {
                        var leftLayer = leftLayers[layerIndex];
                        var rightLayer = rightLayers[layerIndex];
                        if (!MatrixNear(leftLayer.GradientTransform, rightLayer.GradientTransform)
                            || !leftLayer.LinearStops.AsSpan().SequenceEqual(rightLayer.LinearStops))
                        {
                            throw new InvalidOperationException(
                                $"The shared 2D/3D optical item {itemIndex} changed direct-light layer {layerIndex}.");
                        }
                        AssertContours(
                            leftLayer.Contours,
                            rightLayer.Contours,
                            $"item {itemIndex} direct-light layer {layerIndex}");
                    }
                }

                static Reference3DRenderItem[] PhysicalItems(
                    IEnumerable<Reference3DRenderItem> items)
                {
                    return items
                        .Where(item => item.Kind is Reference3DRenderKind.Back
                            or Reference3DRenderKind.Side
                            or Reference3DRenderKind.FrontFill
                            or Reference3DRenderKind.FrontStroke)
                        .OrderBy(item => item.ObjectIndex)
                        .ThenBy(item => item.Kind)
                        .ThenBy(item => item.SurfaceSlot)
                        .ThenBy(item => item.FragmentSlot)
                        .ThenBy(item => item.StableFragmentIdentity)
                        .ToArray();
                }

                static bool MatrixNear(Matrix3x2 left, Matrix3x2 right) =>
                    Near(left.M11, right.M11)
                    && Near(left.M12, right.M12)
                    && Near(left.M21, right.M21)
                    && Near(left.M22, right.M22)
                    && Near(left.M31, right.M31)
                    && Near(left.M32, right.M32);

                static void AssertContours(
                    IReadOnlyList<Reference3DProjectedContour> left,
                    IReadOnlyList<Reference3DProjectedContour> right,
                    string label)
                {
                    if (left.Count != right.Count)
                    {
                        throw new InvalidOperationException(
                            $"The shared 2D/3D {label} contour count changed.");
                    }
                    for (var contourIndex = 0; contourIndex < left.Count; contourIndex++)
                    {
                        var leftContour = left[contourIndex];
                        var rightContour = right[contourIndex];
                        if (leftContour.Closed != rightContour.Closed
                            || leftContour.HasSourceStart != rightContour.HasSourceStart
                            || leftContour.HasSourceEnd != rightContour.HasSourceEnd
                            || leftContour.Points.Length != rightContour.Points.Length
                            || !Near(leftContour.AverageDepth, rightContour.AverageDepth))
                        {
                            throw new InvalidOperationException(
                                $"The shared 2D/3D {label} contour {contourIndex} changed metadata.");
                        }
                        for (var pointIndex = 0;
                             pointIndex < leftContour.Points.Length;
                             pointIndex++)
                        {
                            if (Near(leftContour.Points[pointIndex].X, rightContour.Points[pointIndex].X)
                                && Near(leftContour.Points[pointIndex].Y, rightContour.Points[pointIndex].Y))
                            {
                                continue;
                            }
                            throw new InvalidOperationException(
                                $"The shared 2D/3D {label} contour {contourIndex} changed point {pointIndex}.");
                        }
                    }
                }

                static bool Near(float left, float right) => Math.Abs(left - right) <= 0.001f;
            }

            static float Distance(PointF left, PointF right)
            {
                var dx = left.X - right.X;
                var dy = left.Y - right.Y;
                return MathF.Sqrt(dx * dx + dy * dy);
            }

            static void AssertPixelNear(Color actual, Color expected, string message)
            {
                if (Math.Abs(actual.R - expected.R) <= 1
                    && Math.Abs(actual.G - expected.G) <= 1
                    && Math.Abs(actual.B - expected.B) <= 1)
                {
                    return;
                }
                throw new InvalidOperationException(
                    $"{message}: actual={actual.ToArgb():X8}, expected={expected.ToArgb():X8}.");
            }
        }

        static PathsD VisibleSurfacePaths(Reference3DRenderItem item)
        {
            var surface = Paths(item.Kind == Reference3DRenderKind.FrontStroke
                ? item.OpticalSurfaceContours ?? []
                : item.Contours);
            return item.FragmentClip is { Length: > 0 } fragmentClip
                ? Clipper.Intersect(
                    surface,
                    Paths(fragmentClip),
                    FillRule.EvenOdd,
                    precision: 4)
                : surface;
        }

        static Bitmap RenderOpticsGdi(StageControl source)
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "The optics GDI regression entry point could not be located.");
            var bitmap = new Bitmap(source.ClientSize.Width, source.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            drawGdi.Invoke(source, [graphics]);
            return bitmap;
        }

        static PathsD Paths(IEnumerable<Reference3DProjectedContour> contours)
        {
            return new PathsD(contours
                .Where(contour => contour.Closed && contour.Points.Length >= 3)
                .Select(contour => new PathD(contour.Points.Select(point =>
                    new PointD(point.X, point.Y)))));
        }

        static double Area(PathsD paths) => Math.Abs(paths.Sum(path => Clipper.Area(path)));

        static RectangleF Bounds(IEnumerable<Reference3DProjectedContour> contours)
        {
            var points = contours.SelectMany(contour => contour.Points).ToArray();
            if (points.Length == 0) return RectangleF.Empty;
            return RectangleF.FromLTRB(
                points.Min(point => point.X),
                points.Min(point => point.Y),
                points.Max(point => point.X),
                points.Max(point => point.Y));
        }
    }

    private static void RunSceneLightGizmoRegression(StageControl stage)
    {
        var point = SceneLightDefinition.CreateDefault(SceneLightKind.Point);
        point.TryApply(
            point.Name,
            point.Settings with
            {
                Position = Vector3.Zero,
                Range = 2400f,
                Intensity = 1.25f,
                ShadowStrength = 0.7f,
                ShadowSoftness = 0.3f
            });
        stage.SetSceneLightGizmo(point.Kind, point.Settings);
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out var pointGeometry)
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.Origin)).Kind
                != SceneLightGizmoHandleKind.Position
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.PositionXHandle)).Kind
                != SceneLightGizmoHandleKind.PositionX
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.PositionYHandle)).Kind
                != SceneLightGizmoHandleKind.PositionY
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.PositionZHandle)).Kind
                != SceneLightGizmoHandleKind.PositionZ
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.RangeHandle)).Kind
                != SceneLightGizmoHandleKind.Range
            || stage.HitTestSceneLightGizmo(Point.Round(pointGeometry.IntensityHandle)).Kind
                != SceneLightGizmoHandleKind.Intensity)
        {
            throw new InvalidOperationException(
                "The point-light Gizmo did not expose stable position, range, and intensity handles.");
        }

        var area = SceneLightDefinition.CreateDefault(SceneLightKind.Area);
        stage.SetSceneLightGizmo(area.Kind, area.Settings);
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out var areaGeometry)
            || stage.HitTestSceneLightGizmo(Point.Round(areaGeometry.AreaWidthHandle)).Kind
                != SceneLightGizmoHandleKind.AreaWidth
            || stage.HitTestSceneLightGizmo(Point.Round(areaGeometry.AreaHeightHandle)).Kind
                != SceneLightGizmoHandleKind.AreaHeight
            || stage.HitTestSceneLightGizmo(Point.Round(areaGeometry.DirectionHandle)).Kind
                != SceneLightGizmoHandleKind.Direction)
        {
            throw new InvalidOperationException(
                "The area-light Gizmo did not expose stable size and direction handles.");
        }

        var directional = SceneLightDefinition.CreateDefaultDirectional();
        stage.SetSceneLightGizmo(directional.Kind, directional.Settings);
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out var directionalGeometry)
            || stage.HitTestSceneLightGizmo(Point.Round(directionalGeometry.DirectionHandle)).Kind
                != SceneLightGizmoHandleKind.Direction
            || stage.HitTestSceneLightGizmo(Point.Round(directionalGeometry.Origin)).Kind
                != SceneLightGizmoHandleKind.Position
            || stage.HitTestSceneLightGizmo(Point.Round(directionalGeometry.PositionXHandle)).Kind
                != SceneLightGizmoHandleKind.PositionX
            || !stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Position)
            || stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Range))
        {
            throw new InvalidOperationException(
                "The directional-light Gizmo exposed the wrong handle set.");
        }

        var ambient = SceneLightDefinition.CreateDefaultAmbient();
        stage.SetSceneLightGizmo(ambient.Kind, ambient.Settings);
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out var ambientGeometry)
            || stage.HitTestSceneLightGizmo(Point.Round(ambientGeometry.IntensityHandle)).Kind
                != SceneLightGizmoHandleKind.Intensity
            || stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.Direction)
            || stage.SupportsSceneLightGizmoHandle(SceneLightGizmoHandleKind.ShadowStrength))
        {
            throw new InvalidOperationException(
                "The ambient-light Gizmo exposed controls beyond intensity.");
        }

        var markerEntries = new[]
        {
            new SceneLightGizmoEntry(
                "directional-marker",
                directional.Kind,
                directional.Settings with { Position = new Vector3(-900, -480, 0) },
                Selected: false),
            new SceneLightGizmoEntry(
                "point-marker",
                point.Kind,
                point.Settings with { Position = new Vector3(0, 0, 0) },
                Selected: true),
            new SceneLightGizmoEntry(
                "area-marker",
                area.Kind,
                area.Settings with { Position = new Vector3(900, 480, 0) },
                Selected: false),
            new SceneLightGizmoEntry(
                "ambient-marker",
                ambient.Kind,
                ambient.Settings,
                Selected: false)
        };
        stage.SetSceneLightGizmos(markerEntries);
        if (stage.SceneLightMarkerCount != markerEntries.Length)
        {
            throw new InvalidOperationException("The Stage did not retain all scene-light markers.");
        }
        for (var index = 0; index < markerEntries.Length; index++)
        {
            if (!stage.TryGetSceneLightMarkerScreenGeometry(index, out var marker)
                || marker.LightId != markerEntries[index].LightId
                || marker.Kind != markerEntries[index].Kind
                || stage.HitTestSceneLightMarker(Point.Round(marker.Origin)) != marker.LightId)
            {
                throw new InvalidOperationException(
                    "A scene-light marker lost its stable ID, type, position, or hit target.");
            }
        }
        AssertSceneBindingClearsLightMarkers(markerEntries);

        var adjustedIntensity = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.Intensity,
            new Vector2(12, 0),
            12,
            20);
        var adjustedRange = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.Range,
            new Vector2(10, 0),
            10,
            20);
        var adjustedDirection = MainForm.ApplySceneLightGizmoDelta(
            directional.Kind,
            directional.Settings,
            SceneLightGizmoHandleKind.Direction,
            new Vector2(20, -10),
            20,
            20);
        var adjustedArea = MainForm.ApplySceneLightGizmoDelta(
            area.Kind,
            area.Settings,
            SceneLightGizmoHandleKind.AreaWidth,
            new Vector2(10, 0),
            10,
            20);
        var adjustedShadow = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.ShadowSoftness,
            new Vector2(18, 0),
            18,
            20);
        var adjustedX = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.PositionX,
            new Vector2(10, 0),
            10,
            20);
        var adjustedY = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.PositionY,
            new Vector2(10, 0),
            10,
            20);
        var adjustedZ = MainForm.ApplySceneLightGizmoDelta(
            point.Kind,
            point.Settings,
            SceneLightGizmoHandleKind.PositionZ,
            new Vector2(10, 0),
            10,
            20);
        if (adjustedIntensity.Intensity <= point.Settings.Intensity
            || adjustedRange.Range != point.Settings.Range + 200
            || adjustedDirection.RotationDegrees.X != directional.Settings.RotationDegrees.X - 5
            || adjustedDirection.RotationDegrees.Y != directional.Settings.RotationDegrees.Y + 10
            || adjustedArea.AreaSize.X != area.Settings.AreaSize.X + 400
            || adjustedShadow.ShadowSoftness <= point.Settings.ShadowSoftness
            || adjustedX.Position != point.Settings.Position + new Vector3(200, 0, 0)
            || adjustedY.Position != point.Settings.Position + new Vector3(0, 200, 0)
            || adjustedZ.Position != point.Settings.Position + new Vector3(0, 0, 200))
        {
            throw new InvalidOperationException(
                "A light-Gizmo drag did not map to the expected optical setting.");
        }

        stage.SetSceneLightGizmo(point.Kind, point.Settings);
        _ = stage.TryGetSceneLightGizmoScreenGeometry(out pointGeometry);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < 256; sample++)
        {
            if (!stage.TryGetSceneLightGizmoScreenGeometry(out _))
                throw new InvalidOperationException("Stable light-Gizmo geometry became unavailable.");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (allocated != 0)
        {
            throw new InvalidOperationException(
                $"Stable light-Gizmo geometry allocated {allocated} bytes.");
        }

        using var bitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var drawGdi = typeof(StageControl).GetMethod(
                "DrawGdi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "The GDI light-Gizmo fallback entry point could not be located.");
            drawGdi.Invoke(stage, [graphics]);
        }
        if (!HasSceneLightGizmoPixel(
                bitmap,
                pointGeometry.IntensityHandle,
                StageControl.SceneLightGizmoIntensityColor,
                radius: 5,
                tolerance: 18)
            || !HasSceneLightGizmoPixel(
                bitmap,
                pointGeometry.RangeHandle,
                StageControl.SceneLightGizmoRangeColor,
                radius: 5,
                tolerance: 18)
            || !HasSceneLightGizmoPixel(
                bitmap,
                pointGeometry.PositionXHandle,
                StageControl.SceneLightGizmoPositionXColor,
                radius: 6,
                tolerance: 18)
            || !HasSceneLightGizmoPixel(
                bitmap,
                pointGeometry.PositionYHandle,
                StageControl.SceneLightGizmoPositionYColor,
                radius: 6,
                tolerance: 18)
            || !HasSceneLightGizmoPixel(
                bitmap,
                pointGeometry.PositionZHandle,
                StageControl.SceneLightGizmoPositionZColor,
                radius: 6,
                tolerance: 18))
        {
            throw new InvalidOperationException(
                "The GDI reference-3D overlay did not paint the light-Gizmo handles.");
        }
        stage.ClearSceneLightGizmo();
        RunSceneLightGizmoInteractionRegression();
        Console.WriteLine("scene_light_gizmo=ok");
        Console.WriteLine("scene_light_gizmo_stable_allocated_bytes=0");
        Console.WriteLine("scene_light_gizmo_gdi_pixels=ok");
    }

    private static void AssertSceneBindingClearsLightMarkers(
        IReadOnlyList<SceneLightGizmoEntry> markerEntries)
    {
        var source = new VectorScene();
        source.CreateEmpty();
        using var bindingStage = new StageControl(source)
        {
            ClientSize = new Size(640, 480)
        };
        bindingStage.ConfigureReferenceView(
            new SceneDefinition { Dimension = SceneDimension.ThreeD },
            SceneDimension.ThreeD);
        bindingStage.SetSceneLightGizmos(markerEntries);
        if (!bindingStage.TryGetSceneLightMarkerScreenGeometry(1, out var selectedMarker))
        {
            throw new InvalidOperationException(
                "The scene-binding light-marker regression could not project its selected fixture.");
        }

        var replacement = new VectorScene();
        replacement.CreateEmpty();
        bindingStage.BindScene(replacement);

        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var selectedIndexField = typeof(StageControl).GetField(
            "_sceneLightGizmoSelectedIndex",
            privateInstance)
            ?? throw new InvalidOperationException(
                "The scene-binding light-marker regression could not inspect selection state.");
        if (bindingStage.SceneLightMarkerCount != 0
            || bindingStage.SceneLightGizmoVisible
            || selectedIndexField.GetValue(bindingStage) is not int selectedIndex
            || selectedIndex != -1
            || bindingStage.TryGetSceneLightMarkerScreenGeometry(0, out _)
            || bindingStage.TryGetSceneLightGizmoScreenGeometry(out _)
            || bindingStage.HitTestSceneLightMarker(Point.Round(selectedMarker.Origin)) is not null)
        {
            throw new InvalidOperationException(
                "Binding a replacement scene retained a renderable, hit-testable, or selected light marker.");
        }
        Console.WriteLine("scene_light_markers_cleared_on_binding=ok");
    }

    private static void RunSceneLightGizmoInteractionRegression()
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find workspace tabs.");
        var dimensionButtonField = typeof(MainForm).GetField("_sceneDimensionButton", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find the dimension button.");
        var playbackSettingsField = typeof(MainForm).GetField("_playbackSettings", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find playback settings.");
        var projectField = typeof(MainForm).GetField("_project", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find the project.");
        var stageField = typeof(MainForm).GetField("_stage", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find the Stage.");
        var undoStackField = typeof(MainForm).GetField("_sceneTimelineUndoStack", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not inspect Scene undo state.");
        var beginPointer = typeof(MainForm).GetMethod("TryBeginProjectedScenePointer", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find pointer routing.");
        var movePointer = typeof(MainForm).GetMethod("StageMouseMove", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find pointer movement.");
        var upPointer = typeof(MainForm).GetMethod("StageMouseUp", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find pointer completion.");
        var cancelPointer = typeof(MainForm).GetMethod("CancelSceneLightGizmoPointer", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find pointer cancellation.");
        var selectLight = typeof(MainForm).GetMethod("SelectSceneOpticsLight", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not select a light.");
        var setFrame = typeof(MainForm).GetMethod("SetFrame", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not change frames.");
        var undo = typeof(MainForm).GetMethod("UndoLastEdit", privateInstance)
            ?? throw new InvalidOperationException("Light-Gizmo regression could not find undo.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
            ?? throw new InvalidOperationException("Light-Gizmo regression lost workspace tabs.");
        var dimensionButton = dimensionButtonField.GetValue(form) as Button
            ?? throw new InvalidOperationException("Light-Gizmo regression lost the dimension button.");
        var playbackSettings = playbackSettingsField.GetValue(form) as PlaybackSettingsPanel
            ?? throw new InvalidOperationException("Light-Gizmo regression lost playback settings.");
        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Light-Gizmo regression lost the project.");
        var stage = stageField.GetValue(form) as StageControl
            ?? throw new InvalidOperationException("Light-Gizmo regression lost the Stage.");

        int UndoCount()
        {
            var stack = undoStackField.GetValue(form)
                ?? throw new InvalidOperationException("Light-Gizmo regression lost the Scene undo stack.");
            return stack.GetType().GetProperty("Count")?.GetValue(stack) is int count
                ? count
                : throw new InvalidOperationException("Light-Gizmo regression could not read Scene undo state.");
        }

        form.Show();
        Application.DoEvents();
        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        RunScene3DLightContextMenuRegression(form, project, stage, dimensionButton);
        var scene = project.Scenes[0];
        var light = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        selectLight.Invoke(form, [light.Id]);
        Application.DoEvents();
        if (stage.ReferenceDimension != SceneDimension.ThreeD
            || !stage.TryGetSceneLightGizmoScreenGeometry(out var geometry))
        {
            throw new InvalidOperationException("Light-Gizmo regression could not enter the editable 3D view.");
        }

        var startIntensity = light.Settings.Intensity;
        var undoBefore = UndoCount();
        var handle = Point.Round(geometry.IntensityHandle);
        var drag = new Point(handle.X + 12, handle.Y);
        if (beginPointer.Invoke(
                form,
                [new MouseEventArgs(MouseButtons.Left, 1, handle.X, handle.Y, 0)]) is not true)
        {
            throw new InvalidOperationException("The light handle did not take projected-pointer priority.");
        }
        movePointer.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, drag.X, drag.Y, 0)]);
        upPointer.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, drag.X, drag.Y, 0)]);
        light = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        if (light.Settings.Intensity <= startIntensity || UndoCount() != undoBefore + 1)
        {
            throw new InvalidOperationException(
                "A completed light-Gizmo drag did not update intensity with one undo entry.");
        }

        if (undo.Invoke(form, null) is not true
            || scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional)
                .Settings.Intensity != startIntensity)
        {
            throw new InvalidOperationException("Undo did not restore a completed light-Gizmo drag.");
        }

        const int animatedFrame = 8;
        light = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        var animatedStart = light.Settings with { Intensity = startIntensity + 0.4f };
        if (!scene.InsertLightTimelineKeyframe(light.Id, animatedFrame)
            || !project.TryUpdateSceneLightAtFrame(
                scene.Id,
                light.Id,
                light.Name,
                animatedStart,
                animatedFrame)
            || !scene.TryCreateTimelineTween(
                light.Id,
                0,
                animatedFrame,
                TimelineTweenKind.Classic,
                out var animatedTweenError)
            || !string.IsNullOrEmpty(animatedTweenError))
        {
            throw new InvalidOperationException("Light-Gizmo regression could not create an animated light tween.");
        }
        const int animatedMidpointFrame = animatedFrame / 2;
        var animatedMaterializedBefore = light.EvaluateSettings(animatedMidpointFrame);
        if (!scene.TryEvaluateLightSettings(light.Id, animatedMidpointFrame, out var animatedEvaluatedBefore)
            || animatedEvaluatedBefore != animatedMaterializedBefore)
        {
            throw new InvalidOperationException("The animated light tween was not materialized before interaction.");
        }
        playbackSettings.SetFrameRange(0, animatedFrame, notifyChanged: false);
        if (setFrame.Invoke(form, [animatedFrame, true, true, false]) is not true
            || stage.Frame != animatedFrame)
        {
            throw new InvalidOperationException(
                $"Light-Gizmo regression could not move the playhead to frame {animatedFrame}; "
                + $"Stage remained at {stage.Frame}.");
        }
        selectLight.Invoke(form, [light.Id]);
        Application.DoEvents();
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out geometry))
            throw new InvalidOperationException("The animated light key had no editable Gizmo.");
        var animatedUndoBefore = UndoCount();
        handle = Point.Round(geometry.IntensityHandle);
        drag = new Point(handle.X + 10, handle.Y);
        if (beginPointer.Invoke(
                form,
                [new MouseEventArgs(MouseButtons.Left, 1, handle.X, handle.Y, 0)]) is not true)
        {
            throw new InvalidOperationException("The animated light-Gizmo drag could not begin.");
        }
        movePointer.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, drag.X, drag.Y, 0)]);
        light = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        var animatedMaterializedDuringPreview = light.EvaluateSettings(animatedMidpointFrame);
        if (!scene.TryEvaluateLightSettings(light.Id, animatedMidpointFrame, out var animatedEvaluatedDuringPreview)
            || animatedMaterializedDuringPreview != animatedMaterializedBefore
            || animatedEvaluatedDuringPreview == animatedMaterializedBefore)
        {
            throw new InvalidOperationException(
                "A light-Gizmo preview eagerly rematerialized its tween instead of evaluating live endpoints.");
        }
        upPointer.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, drag.X, drag.Y, 0)]);
        light = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        var animatedEdited = light.EvaluateSettings(animatedFrame);
        var animatedMaterializedAfter = light.EvaluateSettings(animatedMidpointFrame);
        var animatedUndoAfter = UndoCount();
        var animatedUndoSucceeded = animatedUndoAfter == animatedUndoBefore + 1
            && undo.Invoke(form, null) is true;
        var restoredAnimatedLight = scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional);
        var animatedRestored = restoredAnimatedLight.EvaluateSettings(animatedFrame);
        if (animatedEdited.Intensity <= animatedStart.Intensity
            || animatedMaterializedAfter != animatedEvaluatedDuringPreview
            || !animatedUndoSucceeded
            || animatedRestored != animatedStart
            || restoredAnimatedLight.EvaluateSettings(animatedMidpointFrame) != animatedMaterializedBefore)
        {
            throw new InvalidOperationException(
                "A nonzero-frame light edit did not defer tween materialization, create one undo unit, or restore its state key: "
                + $"edited={animatedEdited.Intensity:0.###}, start={animatedStart.Intensity:0.###}, "
                + $"undo={animatedUndoBefore}->{animatedUndoAfter}/{animatedUndoSucceeded}, "
                + $"restored={animatedRestored.Intensity:0.###}.");
        }

        Application.DoEvents();
        if (!stage.TryGetSceneLightGizmoScreenGeometry(out geometry))
            throw new InvalidOperationException("Undo did not restore light-Gizmo presentation.");
        var cancelUndoBefore = UndoCount();
        handle = Point.Round(geometry.IntensityHandle);
        drag = new Point(handle.X + 18, handle.Y);
        if (beginPointer.Invoke(
                form,
                [new MouseEventArgs(MouseButtons.Left, 1, handle.X, handle.Y, 0)]) is not true)
        {
            throw new InvalidOperationException("The second light-Gizmo drag could not begin.");
        }
        movePointer.Invoke(
            form,
            [stage, new MouseEventArgs(MouseButtons.Left, 0, drag.X, drag.Y, 0)]);
        cancelPointer.Invoke(form, null);
        if (scene.Lights.First(candidate => candidate.Kind == SceneLightKind.Directional)
                .EvaluateSettings(animatedFrame) != animatedStart
            || UndoCount() != cancelUndoBefore)
        {
            throw new InvalidOperationException(
                "Canceling a light-Gizmo drag did not restore the value without an undo entry.");
        }
        Console.WriteLine("scene_light_gizmo_pointer_commit_cancel_undo=ok");
    }

    private static void RunScene3DLightContextMenuRegression(
        MainForm form,
        VectorProject project,
        StageControl stage,
        Button dimensionButton)
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var contextMenu = typeof(MainForm).GetField("_scene3DContextMenu", privateInstance)?.GetValue(form)
            as AnimatedContextMenuStrip
            ?? throw new InvalidOperationException("3D light-menu regression could not find the context menu.");
        var addItem = typeof(MainForm).GetField("_addSceneLightContextMenuItem", privateInstance)?.GetValue(form)
            as ToolStripMenuItem
            ?? throw new InvalidOperationException("3D light-menu regression could not find Add light.");
        var menuIcons = typeof(MainForm).GetField("_scene3DLightMenuIcons", privateInstance)?.GetValue(form)
            as Dictionary<SceneLightKind, Bitmap>
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect type icons.");
        var contextPositionField = typeof(MainForm).GetField("_scene3DContextLightPosition", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect its placement.");
        var playingField = typeof(MainForm).GetField("_playing", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect playback state.");
        var selectedLightField = typeof(MainForm).GetField("_selectedSceneLightId", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect light selection.");
        var selectedInstanceField = typeof(MainForm).GetField("_selectedSceneInstanceId", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect instance selection.");
        var selectedInstancesField = typeof(MainForm).GetField("_selectedSceneInstanceIds", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect multi-selection.");
        var timeline = typeof(MainForm).GetField("_timeline", privateInstance)?.GetValue(form) as TimelineStrip
            ?? throw new InvalidOperationException("3D light-menu regression could not inspect the timeline.");
        var prepareMenu = typeof(MainForm).GetMethod("PrepareScene3DContextMenu", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not prepare a placement.");
        var tryGetBasePlanePoint = typeof(MainForm).GetMethod("TryGetSceneBasePlanePoint", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not resolve the base plane.");
        var selectLight = typeof(MainForm).GetMethod("SelectSceneOpticsLight", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not select a light.");
        var selectInstance = typeof(MainForm).GetMethod(
            "SetSceneInstanceSelection",
            privateInstance,
            binder: null,
            types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("3D light-menu regression could not select an instance.");
        var onOpening = typeof(AnimatedContextMenuStrip).GetMethod("OnOpening", privateInstance)
            ?? throw new InvalidOperationException("3D light-menu regression could not raise Opening.");

        System.ComponentModel.CancelEventArgs RaiseOpening()
        {
            var args = new System.ComponentModel.CancelEventArgs();
            onOpening.Invoke(contextMenu, [args]);
            return args;
        }

        if (stage.ReferenceDimension != SceneDimension.TwoD)
            throw new InvalidOperationException("3D light-menu regression did not start in the 2D Scene view.");
        var twoDOpening = RaiseOpening();
        if (!twoDOpening.Cancel || addItem.Enabled)
        {
            throw new InvalidOperationException(
                "The dedicated light menu remained available outside the 3D Scene view.");
        }

        dimensionButton.PerformClick();
        Application.DoEvents();
        stage.CompleteReferenceCameraTransition();
        Application.DoEvents();
        var threeDOpening = RaiseOpening();
        if (stage.ReferenceDimension != SceneDimension.ThreeD
            || threeDOpening.Cancel
            || !addItem.Enabled
            || !contextMenu.ShowImageMargin)
        {
            throw new InvalidOperationException(
                "The dedicated light menu did not become available with its icon margin in 3D Scene view.");
        }

        var expectedKinds = new[]
        {
            SceneLightKind.Directional,
            SceneLightKind.Point,
            SceneLightKind.Area,
            SceneLightKind.Ambient
        };
        if (contextMenu.Items.Count != 1
            || !ReferenceEquals(contextMenu.Items[0], addItem)
            || addItem.Image is null
            || addItem.DropDownItems.Count != expectedKinds.Length)
        {
            throw new InvalidOperationException("The 3D light menu did not expose one typed Add light command.");
        }

        static string KindLabel(SceneLightKind kind) => kind switch
        {
            SceneLightKind.Directional => "Directional Light",
            SceneLightKind.Point => "Point Light",
            SceneLightKind.Area => "Area Light",
            _ => "Ambient Light"
        };

        void AssertLanguage(UiLanguage language)
        {
            if (addItem.Text != UiLocalization.T("Add light", language)
                || addItem.AccessibleName != UiLocalization.T("Add light", language))
            {
                throw new InvalidOperationException($"The Add light command was not localized for {language}.");
            }
            for (var index = 0; index < expectedKinds.Length; index++)
            {
                var kind = expectedKinds[index];
                var child = addItem.DropDownItems[index] as ToolStripMenuItem;
                var label = UiLocalization.T(KindLabel(kind), language);
                if (child is null
                    || child.Tag is not SceneLightKind taggedKind
                    || taggedKind != kind
                    || child.Text != label
                    || child.AccessibleName != label
                    || child.Image is null
                    || !menuIcons.TryGetValue(kind, out var icon)
                    || !ReferenceEquals(child.Image, icon))
                {
                    throw new InvalidOperationException(
                        $"The {kind} light command lost its type, vector icon, accessibility name, or localization.");
                }
            }
        }

        var previousLanguage = UiLocalization.CurrentLanguage;
        try
        {
            UiLocalization.SetLanguage(UiLanguage.English);
            AssertLanguage(UiLanguage.English);
            UiLocalization.SetLanguage(UiLanguage.SimplifiedChinese);
            AssertLanguage(UiLanguage.SimplifiedChinese);
            UiLocalization.SetLanguage(UiLanguage.English);
            AssertLanguage(UiLanguage.English);
        }
        finally
        {
            UiLocalization.SetLanguage(previousLanguage);
        }

        try
        {
            playingField.SetValue(form, true);
            var playingOpening = RaiseOpening();
            if (playingOpening.Cancel || addItem.Enabled)
                throw new InvalidOperationException("Playback did not disable 3D light creation.");
        }
        finally
        {
            playingField.SetValue(form, false);
        }
        if (RaiseOpening().Cancel || !addItem.Enabled)
            throw new InvalidOperationException("Stopping playback did not restore 3D light creation.");

        var contextPoint = new Point(
            Math.Max(1, stage.ClientSize.Width * 2 / 3),
            Math.Max(1, stage.ClientSize.Height * 2 / 3));
        object?[] basePlaneArguments = [contextPoint, PointF.Empty];
        if (tryGetBasePlanePoint.Invoke(form, basePlaneArguments) is not true
            || basePlaneArguments[1] is not PointF basePlanePoint)
        {
            throw new InvalidOperationException("The 3D light menu could not project a valid Stage position.");
        }
        prepareMenu.Invoke(form, [contextPoint]);
        var expectedPosition = new Vector3(basePlanePoint.X, basePlanePoint.Y, 0f);
        if (contextPositionField.GetValue(form) is not Vector3 contextPosition
            || contextPosition != expectedPosition)
        {
            throw new InvalidOperationException("The 3D light menu did not retain its right-click base-plane position.");
        }

        var scene = project.Scenes[0];
        if (!project.TryAddSceneInstance(
                scene.Id,
                project.DrawingObjects[0].Id,
                new PointF(140, -90),
                0,
                out var instance)
            || instance is null)
        {
            throw new InvalidOperationException("3D light-menu regression could not create a selectable instance.");
        }
        selectInstance.Invoke(form, [instance, false]);
        var selectedLight = scene.Lights.First();
        selectLight.Invoke(form, [selectedLight.Id]);
        var selectedInstanceIds = selectedInstancesField.GetValue(form) as HashSet<string>;
        if (!string.IsNullOrWhiteSpace(selectedInstanceField.GetValue(form) as string)
            || selectedInstanceIds is not { Count: 0 }
            || !string.Equals(selectedLightField.GetValue(form) as string, selectedLight.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Selecting a light did not clear the selected Scene instance.");
        }
        selectInstance.Invoke(form, [instance, false]);
        if (!string.IsNullOrWhiteSpace(selectedLightField.GetValue(form) as string)
            || !string.Equals(selectedInstanceField.GetValue(form) as string, instance.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Selecting a Scene instance did not clear the selected light.");
        }

        var knownLightIds = scene.Lights.Select(light => light.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in expectedKinds)
        {
            var child = addItem.DropDownItems.Cast<ToolStripItem>()
                .Single(item => item.Tag is SceneLightKind taggedKind && taggedKind == kind);
            var countBefore = scene.Lights.Count;
            child.PerformClick();
            Application.DoEvents();
            var added = scene.Lights.Single(light => !knownLightIds.Contains(light.Id));
            knownLightIds.Add(added.Id);
            var expectedAddedPosition = kind == SceneLightKind.Ambient
                ? SceneLightDefinition.CreateDefault(kind).Settings.Position
                : expectedPosition;
            var track = scene.Timeline.FindTrackByTargetId(added.Id);
            if (scene.Lights.Count != countBefore + 1
                || added.Kind != kind
                || added.Settings.Position != expectedAddedPosition
                || track is null
                || scene.Timeline.Tracks.Count(candidate =>
                    string.Equals(candidate.TargetId, added.Id, StringComparison.Ordinal)) != 1
                || !string.Equals(selectedLightField.GetValue(form) as string, added.Id, StringComparison.Ordinal)
                || !string.Equals(timeline.ActiveTrackId, track.Id, StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(selectedInstanceField.GetValue(form) as string))
            {
                throw new InvalidOperationException(
                    $"The {kind} context command did not create one positioned, selected light timeline track.");
            }
        }

        var savedLights = scene.Lights.Select(light => light.Clone()).ToArray();
        var savedTimeline = scene.Timeline.CreateSnapshot();
        var selectedLightId = selectedLightField.GetValue(form) as string ?? string.Empty;
        try
        {
            var maximumLights = Enumerable.Range(0, SceneDefinition.MaximumLights)
                .Select(index => SceneLightDefinition.CreateDefault(expectedKinds[index % expectedKinds.Length]))
                .ToArray();
            scene.RestoreLights(maximumLights, lightsWerePresent: true);
            var maximumOpening = RaiseOpening();
            if (maximumOpening.Cancel || addItem.Enabled)
                throw new InvalidOperationException("The 3D light menu ignored the scene light limit.");
        }
        finally
        {
            scene.RestoreLights(savedLights, lightsWerePresent: true);
            scene.Timeline.RestoreSnapshot(savedTimeline);
            scene.SynchronizeTimelineTracks();
            timeline.RefreshTimeline();
            selectLight.Invoke(form, [selectedLightId]);
        }

        Console.WriteLine("scene_3d_light_context_menu=ok");
        Console.WriteLine("scene_light_instance_selection_exclusive=ok");
    }

    private static bool HasSceneLightGizmoPixel(
        Bitmap bitmap,
        PointF center,
        Color expected,
        int radius,
        int tolerance)
    {
        var origin = Point.Round(center);
        for (var y = Math.Max(0, origin.Y - radius); y <= Math.Min(bitmap.Height - 1, origin.Y + radius); y++)
        {
            for (var x = Math.Max(0, origin.X - radius); x <= Math.Min(bitmap.Width - 1, origin.X + radius); x++)
            {
                var actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.R - expected.R) <= tolerance
                    && Math.Abs(actual.G - expected.G) <= tolerance
                    && Math.Abs(actual.B - expected.B) <= tolerance)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void RunSceneOpticsPanelLayoutRegression()
    {
        var previousLanguage = UiLocalization.CurrentLanguage;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
            {
                UiLocalization.SetLanguage(language);
                foreach (var width in new[] { 240, 296 })
                {
                    using var lighting = new SceneLightingPanel
                    {
                        Size = new Size(width, 424)
                    };
                    using var material = new SpatialMaterialPanel
                    {
                        Size = new Size(width, 286)
                    };
                    LayoutTree(lighting);
                    LayoutTree(material);
                    AssertContained(lighting, width, language);
                    AssertContained(material, width, language);
                }
            }
        }
        finally
        {
            UiLocalization.SetLanguage(previousLanguage);
        }

        Console.WriteLine("scene_optics_panel_layout=ok");

        static void LayoutTree(Control root)
        {
            root.CreateControl();
            root.PerformLayout();
            foreach (Control child in root.Controls) LayoutTree(child);
        }

        static void AssertContained(Control root, int requestedWidth, UiLanguage language)
        {
            if (root.Width != requestedWidth)
            {
                throw new InvalidOperationException(
                    $"The optics panel minimum width exceeded {requestedWidth}px in {language}.");
            }
            AssertDescendants(root);

            void AssertDescendants(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if (child.Visible
                        && (child.Left < 0
                            || child.Right > parent.ClientSize.Width + 1))
                    {
                        throw new InvalidOperationException(
                            $"The {language} optics control '{child.AccessibleName ?? child.GetType().Name}' "
                            + $"overflowed its {requestedWidth}px parent: bounds={child.Bounds}, parent={parent.ClientSize}.");
                    }
                    AssertDescendants(child);
                }
            }
        }
    }

    private static void RunSceneOpticsDenseLightingRegression()
    {
        const int objectCount = 192;
        const int columns = 16;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var owners = new SceneCompositionObjectOwner[objectCount];
        var materials = new SpatialOpticalMaterial[objectCount];
        for (var index = 0; index < objectCount; index++)
        {
            var column = index % columns;
            var row = index / columns;
            scene.AddObject(
                0,
                new PointF(-900 + column * 165, -600 + row * 165),
                new SizeF(120, 120),
                angle: 0,
                stroke: 0,
                color: Color.FromArgb(255, 64 + index % 5 * 24, 128, 192),
                strokeColor: Color.Transparent,
                atoms: 12,
                shapeKind: ShapeKind.Rectangle);
            owners[index] = new SceneCompositionObjectOwner(
                $"dense-{index}",
                "dense lighting fixture");
            materials[index] = SpatialOpticalMaterial.Default;
        }

        var definition = new SceneDefinition { Dimension = SceneDimension.ThreeD };
        using var stage = new StageControl(scene)
        {
            ClientSize = new Size(640, 480),
            WorldGridOpacity = 0
        };
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD);
        stage.ResetReferenceCameraView();
        stage.SetReferenceCameraOrientation(0, 0);
        var drawGdi = typeof(StageControl).GetMethod(
            "DrawGdi",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The dense optics fixture could not locate the GDI renderer.");

        var stopwatch = Stopwatch.StartNew();
        var lastItems = Array.Empty<Reference3DRenderItem>();
        for (var frame = 0; frame < 6; frame++)
        {
            var poses = Enumerable.Range(0, objectCount)
                .Select(index => new SceneCompositionObjectPose(
                    Matrix4x4.CreateTranslation(frame * 3f, 0, index % 3 * 0.5f)))
                .ToArray();
            stage.SetSceneCompositionResult(
                new SceneCompositionResult(owners, poses, materials),
                scene);
            lastItems = stage.GetReference3DSceneRenderItems();
            if (lastItems.Length < Reference3DVectorLightingRegressionMinimumItems)
            {
                throw new InvalidOperationException(
                    $"The dense optics fixture produced too few render items: {lastItems.Length}.");
            }

            var filledItems = lastItems.Where(item => item.Kind == Reference3DRenderKind.FrontFill).ToArray();
            if (filledItems.Length < Reference3DVectorLightingRegressionMinimumItems
                || filledItems.Any(item => item.OpticalSurface is not null
                    || item.VectorLightingArgb is null
                    || item.LocalLightLayers is not { Length: 0 }
                    || item.ShadowLayers is not { Length: 0 })
                || stage.LastReference3DLocalLightLayerCount != 0
                || stage.LastReference3DShadowProjectionCount != 0)
            {
                throw new InvalidOperationException(
                    "Dense solid lighting did not use the merged vector-lighting path.");
            }

            using var bitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            drawGdi.Invoke(stage, [graphics]);
        }
        stopwatch.Stop();
        Console.WriteLine(
            $"scene_optics_dense_lighting=frames=6,items={lastItems.Length},elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:0.###}");
    }

    private const int Reference3DVectorLightingRegressionMinimumItems = 128;
}
