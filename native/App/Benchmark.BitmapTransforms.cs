using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunBitmapAffineTransformRegression()
    {
        RunBitmapFreeTransformPointerRegression();
        var scene = new VectorScene();
        scene.CreateEmpty();
        var index = scene.AddBitmapObject(0, new PointF(400, 300), new BitmapObjectData
        {
            ImageAssetId = "affine-bitmap",
            PlacedSize = new SizeF(1200, 800),
            VisibleContours = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)]]
        }, 0.35f);
        var bounds = scene.GetRawObjectWorldBounds(index);
        scene.SetObjectDistortions(index, [new DistortWarp(
            TransformOverlayFrame.FromBounds(bounds),
            new DistortEnvelope(
                new PointF(bounds.Left - 100, bounds.Top),
                new PointF(bounds.Right + 200, bounds.Top + 80),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)))]);
        var transforms = new[]
        {
            Matrix3x2.CreateSkew(0.4f, 0),
            Matrix3x2.CreateSkew(0, -0.3f),
            Matrix3x2.CreateScale(1.7f, 0.6f),
            Matrix3x2.CreateScale(-1, 1),
            Matrix3x2.CreateScale(1, -1),
            Matrix3x2.CreateRotation(0.6f) * Matrix3x2.CreateTranslation(100, -40)
        };
        var probes = new[] { new PointF(0.2f, 0.3f), new PointF(0.7f, 0.8f) };
        var initial = probes.Select(probe => BitmapTransformProbe(scene, index, probe)).ToArray();
        var session = scene.BeginTransformSession([index]);
        foreach (var transform in transforms)
        {
            scene.ApplyTransformSession(session, point => TransformBitmapProbe(point, transform),
                convertPrimitivesToPaths: true);
            for (var probeIndex = 0; probeIndex < probes.Length; probeIndex++)
            {
                AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probes[probeIndex]),
                    TransformBitmapProbe(initial[probeIndex], transform), "Bitmap transform session lost shear or reflection.");
            }
            AssertTimeline(scene.TryGetObjectDistortions(index, out var warps) && warps.Length == 1
                && scene.TryGetBitmapObjectData(index, out var data) && data.ImageAssetId == "affine-bitmap"
                && data.VisibleContours is { Length: 1 },
                "Bitmap transform changed its asset/clip or accumulated preview warps.");
        }

        foreach (var transform in transforms)
        {
            var previous = probes.Select(probe => BitmapTransformProbe(scene, index, probe)).ToArray();
            scene.ShearObjects([index], point => TransformBitmapProbe(point, transform));
            for (var probeIndex = 0; probeIndex < probes.Length; probeIndex++)
            {
                var actual = BitmapTransformProbe(scene, index, probes[probeIndex]);
                AssertBitmapTransformPoint(actual, TransformBitmapProbe(previous[probeIndex], transform),
                    "Sequential bitmap transforms lost an existing distortion.");
                AssertTimeline(scene.HitTest(actual, 0, 1) == index,
                    "Transformed bitmap hit testing disagreed with its image mapping.");
            }
        }
        var restored = new VectorScene();
        restored.RestoreSnapshot(scene.CreateSnapshot());
        AssertBitmapTransformPoint(BitmapTransformProbe(restored, index, probes[0]),
            BitmapTransformProbe(scene, index, probes[0]), "Bitmap transform snapshot lost the image mapping.");

        var project = VectorProject.CreateEmpty();
        var symbol = project.DrawingObjects[0];
        symbol.SetAnchor(PointF.Empty);
        symbol.Scene.RestoreSnapshot(scene.CreateSnapshot());
        AssertTimeline(project.TryAddSceneInstance(project.Scenes[0].Id, symbol.Id,
            new PointF(200, 100), 0, out var instance) && instance is not null,
            "Could not create a bitmap composition fixture.");
        foreach (var mirror in new[] { false, true })
        {
            instance!.SkewX = 25;
            instance.ScaleX = mirror ? -1.4f : 1.4f;
            instance.ScaleY = 0.7f;
            instance.RotationZ = 20;
            var destination = new VectorScene();
            SceneCompositionBuilder.Build(destination, project.Scenes[0], project.DrawingObjects, 0);
            var transform = DrawingObjectInstanceDefinition.CreatePlanarTransform(instance.EvaluateState(0));
            AssertTimeline(destination.ObjectCount == 1 && destination.ShapeKind[0] == ShapeKind.Bitmap,
                "Sheared bitmap composition lost or materialized the image object.");
            AssertBitmapTransformPoint(BitmapTransformProbe(destination, 0, probes[0]),
                TransformBitmapProbe(BitmapTransformProbe(symbol.Scene, index, probes[0]), transform),
                "Bitmap composition did not preserve the source warp before its parent transform.");
        }
        Console.WriteLine("bitmap_affine_transform=session,sequential,shear,mirror,clip,snapshot,composition");
    }

    private static void RunBitmapFreeTransformPointerRegression()
    {
        using var form = new MainForm { Size = new Size(1280, 800) };
        form.CreateControl();
        form.PerformLayout();
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        stage.Size = new Size(760, 480);
        stage.CreateControl();
        stage.ZoomAt(new Point(stage.Width / 2, stage.Height / 2), 1f / stage.Zoom);
        var index = scene.AddBitmapObject(scene.ActiveLayer, new PointF(stage.CameraX, stage.CameraY),
            new BitmapObjectData { ImageAssetId = "pointer-bitmap", PlacedSize = new SizeF(3000, 2000) });
        RequireMethod(typeof(MainForm), "SetSelection", [typeof(int), typeof(bool)]).Invoke(form, [index, false]);
        RequireMethod(typeof(MainForm), "ActivateTool", [typeof(ToolMode)]).Invoke(form, [ToolMode.Transform]);
        var handle = stage.GetTransformOverlayScreenGeometry().SkewHandles
            .Single(item => item.Kind == TransformHandleKind.SkewTop);
        var start = Point.Round(handle.Point);
        var end = new Point(start.X + 30, start.Y);
        var startWorld = stage.ScreenToWorld(start);
        var endWorld = stage.ScreenToWorld(end);
        var bounds = scene.GetObjectWorldBounds(index);
        var probe = new PointF(0.3f, 0.4f);
        var original = BitmapTransformProbe(scene, index, probe);
        RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
        RequireMethod(typeof(MainForm), "ApplyTransformFromPointer").Invoke(form, [endWorld]);
        var factor = -(endWorld.X - startWorld.X) / bounds.Height;
        var expected = new PointF(original.X + (original.Y - bounds.Bottom) * factor, original.Y);
        AssertTimeline(scene.TryGetObjectDistortions(index, out _),
            "Free Transform still ignored the bitmap skew handle.");
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), expected,
            "Bitmap skew pointer preview changed the handles without the image mapping.");
        RequireMethod(typeof(MainForm), "StageMouseUp").Invoke(form,
            [stage, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0)]);
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), expected,
            "Committed bitmap skew disagreed with the pointer preview.");
        AssertTimeline(RequireMethod(typeof(MainForm), "UndoLastEdit").Invoke(form, null) is true,
            "Bitmap skew did not create an undoable pointer transaction.");
        AssertBitmapTransformPoint(BitmapTransformProbe(scene, index, probe), original,
            "Undo did not restore the bitmap before skewing.");
    }

    private static PointF TransformBitmapProbe(PointF point, Matrix3x2 transform)
    {
        var result = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new PointF(result.X, result.Y);
    }

    private static PointF BitmapTransformProbe(VectorScene scene, int index, PointF normalized)
    {
        var localX = (normalized.X - 0.5f) * scene.Width[index];
        var localY = (normalized.Y - 0.5f) * scene.Height[index];
        var cosine = MathF.Cos(scene.Angle[index]);
        var sine = MathF.Sin(scene.Angle[index]);
        return scene.MapObjectPoint(index, new PointF(
            scene.X[index] + localX * cosine - localY * sine,
            scene.Y[index] + localX * sine + localY * cosine));
    }

    private static void AssertBitmapTransformPoint(PointF actual, PointF expected, string message)
    {
        AssertTimeline(Math.Abs(actual.X - expected.X) < 2 && Math.Abs(actual.Y - expected.Y) < 2,
            $"{message} actual={actual}, expected={expected}");
    }
}
