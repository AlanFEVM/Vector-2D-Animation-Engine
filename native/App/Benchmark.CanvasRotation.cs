namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunCanvasRotationShortcutRegression()
    {
        using var form = CreateOffscreenSnapPointForm();
        var stage = (StageControl)RequireField(typeof(MainForm), "_stage").GetValue(form)!;
        var scene = (VectorScene)RequireField(typeof(MainForm), "_scene").GetValue(form)!;
        form.Show();
        form.Location = Point.Empty;
        PositionWorkspaceCaptureWindow(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
        Application.DoEvents();
        scene.CreateEmpty();
        var index = scene.AddObject(0, new PointF(1500, 0), new SizeF(900, 400), 0, 0,
            Color.Coral, Color.Transparent, 4, ShapeKind.Rectangle);
        stage.BindScene(scene);
        stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 1, CanvasRotation = 0 });
        var revision = scene.GeometryRevision;
        var draw = RequireMethod(typeof(StageControl), "DrawGdi", [typeof(Graphics)]);
        try
        {
            foreach (var angle in new[] { 0f, 0.5f, MathF.PI / 2, -MathF.PI * 0.75f })
            {
                stage.SetCanvasRotation(angle);
                var screen = stage.WorldToScreen(scene.X[index], scene.Y[index]);
                var roundtrip = stage.ScreenToWorld(Point.Round(screen));
                if (Math.Abs(roundtrip.X - scene.X[index]) > 20 || Math.Abs(roundtrip.Y - scene.Y[index]) > 20)
                    throw new InvalidOperationException("Rotated Stage coordinate conversion did not round trip.");
                if (!stage.VisibleWorldBounds().Contains(roundtrip))
                    throw new InvalidOperationException("Rotated visible bounds omitted a visible point.");
                using var bitmap = new Bitmap(stage.Width, stage.Height);
                using (var graphics = Graphics.FromImage(bitmap)) draw.Invoke(stage, [graphics]);
                var sample = bitmap.GetPixel((int)screen.X, (int)screen.Y);
                if (Math.Abs(sample.R - Color.Coral.R) > 3 || Math.Abs(sample.G - Color.Coral.G) > 3)
                    throw new InvalidOperationException($"GDI rotated rectangle missed its projected center at {angle}.");
                // A point beyond the short side distinguishes a rotated primitive from a rectangle
                // whose center alone was rotated.
                var inside = stage.WorldToScreen(scene.X[index] + 350, scene.Y[index]);
                sample = bitmap.GetPixel((int)inside.X, (int)inside.Y);
                if (Math.Abs(sample.R - Color.Coral.R) > 3)
                    throw new InvalidOperationException("GDI primitive orientation did not follow canvas rotation.");
                stage.Refresh();
                Application.DoEvents();
                if (!stage.LastFrameUsedDirect2D) throw new InvalidOperationException("Rotated Stage unexpectedly lost Direct2D rendering.");
                FlushWorkspaceCapture();
                using (var presented = new Bitmap(stage.Width, stage.Height))
                {
                    using (var capture = Graphics.FromImage(presented))
                        capture.CopyFromScreen(stage.PointToScreen(Point.Empty), Point.Empty, stage.Size);
                    var color = presented.GetPixel((int)inside.X, (int)inside.Y);
                    if (Math.Abs(color.R - Color.Coral.R) > 6 || Math.Abs(color.G - Color.Coral.G) > 6)
                        throw new InvalidOperationException($"Direct2D presented pixels missed the rotated primitive at {angle}: {color}.");
                }
                var before = stage.WorldToScreen(0, 0);
                stage.Pan(13, -9);
                var after = stage.WorldToScreen(0, 0);
                if (Math.Abs(after.X - before.X - 13) > 0.01f || Math.Abs(after.Y - before.Y + 9) > 0.01f)
                    throw new InvalidOperationException("Rotated Stage pan did not follow screen axes.");
                var anchor = new Point(stage.Width / 3, stage.Height / 3);
                var world = stage.ScreenToWorld(anchor);
                stage.ZoomAt(anchor, 1.2f);
                var anchored = stage.ScreenToWorld(anchor);
                if (Math.Abs(world.X - anchored.X) > 0.01f || Math.Abs(world.Y - anchored.Y) > 0.01f)
                    throw new InvalidOperationException("Rotated zoom moved the pointer's world anchor.");
                stage.RestoreViewState(stage.CaptureViewState() with { CameraX = 0, CameraY = 0, Zoom = 1 });
            }
            var view = stage.CaptureViewState();
            stage.SetCanvasRotation(0);
            stage.RestoreViewState(view);
            if (stage.CanvasRotation != view.CanvasRotation || scene.GeometryRevision != revision)
                throw new InvalidOperationException("Canvas rotation changed geometry or failed view-state restore.");
            stage.Focus();
            if (RequireMethod(typeof(MainForm), "HoldCanvasRotation").Invoke(form, null) is not true)
                throw new InvalidOperationException("Canvas rotation shortcut did not start.");
            var start = new Point(stage.Width / 2 + 80, stage.Height / 2);
            RequireMethod(typeof(MainForm), "StageMouseDown").Invoke(form, [stage,
                new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
            RequireMethod(typeof(MainForm), "StageMouseMove").Invoke(form, [stage,
                new MouseEventArgs(MouseButtons.Left, 0, stage.Width / 2, stage.Height / 2 + 80, 0)]);
            RequireMethod(typeof(MainForm), "EndCanvasRotation").Invoke(form, [true]);
            if (stage.Capture || stage.CanvasRotation != view.CanvasRotation)
                throw new InvalidOperationException("Cancelling canvas rotation retained capture or a changed angle.");
            Console.WriteLine("canvas_rotation_shortcut_regression=ok,gdi=ok,direct2d=ok,coordinates=ok,pan=ok,zoom=ok,cancel=ok");
        }
        finally { RequireField(typeof(MainForm), "_projectDirty").SetValue(form, false); }
    }
}
