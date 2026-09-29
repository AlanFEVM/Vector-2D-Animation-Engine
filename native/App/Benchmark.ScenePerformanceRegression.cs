using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    internal static void RunScenePerformanceRegression()
    {
        const string source = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><path fill='#349ed0' d='M0 0H120V80H0Z'/></svg>";
        Verify(0.2f, 1.5f, 1.5f, 0, true);
        Verify(0, 1.5f, 0.8f, 0, true);
        Verify(0.2f, 1.5f, 0.8f, 0, false);
        Verify(0.2f, -1.5f, 0.8f, 0, false);
        Verify(0.2f, 1, 1, 12, false);
        VerifySvgRasterReuse();
        Console.WriteLine("scene_svg_transform_reuse_regression=passed");

        static void Verify(float angle, float scaleX, float scaleY, float skewX, bool reuse)
        {
            var project = VectorProject.CreateEmpty();
            var drawing = project.DrawingObjects[0];
            drawing.Scene.CreateEmpty();
            var original = drawing.Scene.AddImportedSvgObject(
                0, new PointF(20, -10), new SizeF(120, 80), angle, source, "Transform fixture");
            AssertTimeline(project.TryAddSceneInstance(project.Scenes[0].Id, drawing.Id,
                    PointF.Empty, 0, out var instance) && instance is not null,
                "SVG transform fixture did not create its scene instance.");
            for (var frame = 0; frame < 3; frame++)
            {
                var state = instance!.EvaluateState(0) with
                {
                    X = 180 + frame * 17,
                    Y = -75 + frame * 11,
                    ScaleX = scaleX,
                    ScaleY = scaleY,
                    SkewX = skewX,
                    RotationZ = 27 + frame * 13
                };
                instance.SetStateAtFrame(0, state);
                var destination = new VectorScene();
                var composition = SceneCompositionBuilder.Build(
                    destination, project.Scenes[0], project.DrawingObjects, 0);
                AssertTimeline(destination.ObjectCount == 1 && composition.ObjectOwners.Count == 1
                    && destination.TryGetImportedSvgSource(0, out _),
                    "SVG transform composition lost geometry or provenance.");
                destination.TryGetImportedSvgSource(0, out var actualSource);
                AssertTimeline((actualSource == source) == reuse,
                    $"SVG source reuse did not distinguish orthogonal axes from shear/reflection ({angle}, {scaleX}, {scaleY}, {skewX}).");
                if (!reuse)
                {
                    AssertTimeline(actualSource.Contains("matrix(", StringComparison.Ordinal),
                        "SVG shear/reflection lost its exact matrix wrapper.");
                    continue;
                }

                var parent = SceneObjectInstanceDefinition.CreatePlanarTransform(state);
                var originalTransform = ObjectTransform(drawing.Scene, original) * parent;
                var composedTransform = ObjectTransform(destination, 0);
                foreach (var corner in new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One })
                {
                    var expected = Vector2.Transform(corner, originalTransform);
                    var actual = Vector2.Transform(corner, composedTransform);
                    // Center and each size component quantize to one vector unit.
                    AssertTimeline(Vector2.Distance(expected, actual) <= 1.1f,
                        $"SVG source reuse moved a transformed corner: expected={expected}, actual={actual}.");
                }
            }
        }

        static Matrix3x2 ObjectTransform(VectorScene scene, int index) =>
            Matrix3x2.CreateTranslation(-0.5f, -0.5f)
            * Matrix3x2.CreateScale(scene.Width[index], scene.Height[index])
            * Matrix3x2.CreateRotation(scene.Angle[index])
            * Matrix3x2.CreateTranslation(scene.X[index], scene.Y[index]);
    }

    private static void VerifySvgRasterReuse()
    {
        const string source = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><path fill='#1234ab' d='M0 0H120V80H0Z'/></svg>";
        ImportedSvgRasterizer.ClearCache();
        try
        {
            var large = ImportedSvgRasterizer.Rasterize(source, 640, 480);
            var smaller = ImportedSvgRasterizer.Rasterize(source, 320, 240);
            AssertTimeline(ReferenceEquals(large, smaller),
                "A matching higher-resolution SVG raster was rebuilt instead of reused.");
            var differentAspect = ImportedSvgRasterizer.Rasterize(source, 400, 240);
            AssertTimeline(!ReferenceEquals(large, differentAspect)
                && Math.Abs(differentAspect.PixelWidth * 240d - differentAspect.PixelHeight * 400d) <= 400,
                "SVG raster reuse changed the requested aspect ratio.");
            var higher = ImportedSvgRasterizer.Rasterize(source, 1280, 960);
            AssertTimeline(higher.PixelWidth >= 1280 && higher.PixelHeight >= 960,
                "SVG raster reuse returned a texture below the requested resolution.");
            var changed = ImportedSvgRasterizer.Rasterize(source.Replace("#1234ab", "#fe1200"), 320, 240);
            AssertTimeline(changed.Key.ContentSha256 != large.Key.ContentSha256
                && !ReferenceEquals(changed, large), "SVG raster reuse crossed a material/content change.");
            for (var variant = 0; variant < 220; variant++)
                ImportedSvgRasterizer.Rasterize(source.Replace("#1234ab", $"#{variant:x6}"), 16, 16);
            AssertTimeline(ReferenceEquals(large, ImportedSvgRasterizer.Rasterize(source, 640, 480)),
                "A sequence of small animation variants evicted a reusable SVG far below the byte budget.");
        }
        finally { ImportedSvgRasterizer.ClearCache(); }
        Console.WriteLine("scene_svg_larger_raster_reuse_regression=passed");
    }
}
