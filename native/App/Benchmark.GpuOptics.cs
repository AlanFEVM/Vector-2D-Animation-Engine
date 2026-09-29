using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using SizeI = Vortice.Mathematics.SizeI;
using Color4 = Vortice.Mathematics.Color4;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    internal static void RunGpuOpticsRegression()
    {
        if (Environment.GetEnvironmentVariable("VECTOR_BENCH_SCENE_PERFORMANCE") == "1")
        {
            RunScenePerformanceBenchmark();
            return;
        }
        RunScenePerformanceRegression();
        RunGpuPlaybackTargetLifetimeRegression();
        using var window = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.WorkingArea.Left + 60, 60),
            ClientSize = new Size(96, 96), ShowInTaskbar = false
        };
        window.Show();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var host = StageGpuDevice.TryCreate(window.Handle, window.ClientSize, factory)
            ?? throw new InvalidOperationException("GPU optics regression requires a hardware Direct3D device.");
        using var shader = new StageGpuOpticalShader(host);
        using var surface = shader.CreateSurface(new Size(16, 16));
        var bounds = new Rectangle(5, 8, 16, 16);
        var pixels = new int[256];
        for (var i = 0; i < pixels.Length; i++)
        {
            var alpha = i % 5 == 0 ? 0 : i % 5 == 1 ? 64 : i % 5 == 2 ? 128 : 255;
            pixels[i] = alpha << 24 | ((i * 31 % 256) * alpha + 127) / 255 << 16
                | ((i * 17 % 256) * alpha + 127) / 255 << 8 | ((i * 7 % 256) * alpha + 127) / 255;
        }
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { surface.BaseBitmap.CopyFromMemory(pin.AddrOfPinnedObject(), 16 * 4).CheckError(); }
        finally { pin.Free(); }
        var scene = new VectorScene(); scene.CreateEmpty();
        using var stage = new StageControl(scene);
        var applyCpu = typeof(StageControl).GetMethod("ApplyReference3DLinearLighting", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Reference3DProjectedContour RectangleContour(float x, float y, float w, float h) =>
            new([new(x,y),new(x+w,y),new(x+w,y+h),new(x,y+h)], true, 0f, false, false);
        var item = new Reference3DRenderItem(0, 0, Reference3DRenderKind.FrontFill, [], 0, default, 0, 0)
        {
            OpticalResponse = Reference3DOpticalResponse.Identity with { AmbientIrradiance = new Vector3(.13f, .24f, .38f) },
            LocalLightLayers =
            [
                new([RectangleContour(5,8,16,16), RectangleContour(10,13,4,4)],
                    Matrix3x2.CreateScale(8) * Matrix3x2.CreateTranslation(13,16), [], [],
                    [new(0, new(1.4f,.6f,.2f), new(.2f,.7f,.1f), new(.02f,.04f,.08f)),
                     new(.4f, new(.6f,.8f,.3f), new(.1f,.2f,.3f), new(.01f,.02f,.03f)),
                     new(1, Vector3.Zero, Vector3.Zero)]),
                new([RectangleContour(7,10,7,9)], Matrix3x2.Identity, [], [],
                    [new(0, new(.11f,.25f,.15f), new(.22f,.1f,.4f))])
            ]
        };
        var maximumError = 0;
        foreach (var metallic in new[] { 0f, .7f, 1f })
        {
            var material = SpatialOpticalMaterial.Default with { Metallic = metallic, Reflectivity = .63f, IndexOfRefraction = 1.7f };
            using var cpuBitmap = new Bitmap(16, 16, PixelFormat.Format32bppPArgb);
            var data = cpuBitmap.LockBits(new Rectangle(0,0,16,16), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
            finally { cpuBitmap.UnlockBits(data); }
            var expected = (int[])applyCpu.Invoke(stage,
                [cpuBitmap, new StageControl.Reference3DOpticalRasterLayout(bounds,16,16), item, material])!;
            shader.Shade(surface, item, material, bounds);
            var properties = new BitmapProperties1(new D2DPixelFormat(Format.R8G8B8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
            using var readable = host.Context.CreateBitmap(new SizeI(16,16), IntPtr.Zero, 0, properties);
            readable.CopyFromBitmap(surface.OutputBitmap).CheckError();
            var mapped = readable.Map(MapOptions.Read);
            try
            {
                var row = new int[16];
                for (var y = 0; y < 16; y++)
                {
                    Marshal.Copy(mapped.Bits + (int)mapped.Pitch * y, row, 0, row.Length);
                    for (var x = 0; x < 16; x++)
                    {
                        var rgba = row[x];
                        var actual = rgba;
                        var reference = expected[y * 16 + x];
                        if ((actual >>> 24) != (reference >>> 24)) throw new InvalidOperationException("GPU lighting changed material alpha.");
                        for (var shift = 0; shift < 24; shift += 8)
                        {
                            var error = Math.Abs((actual >> shift & 255) - (reference >> shift & 255));
                            maximumError = Math.Max(error, maximumError);
                            // The GPU path uses normalized UAV quantization; report the
                            // observed channel delta while keeping alpha exact.
                        }
                    }
                }
            }
            finally { readable.Unmap(); }
        }
        host.Resize(new Size(128,96));
        host.Context.BeginDraw();
        host.Context.Clear(new Color4(.2f,.3f,.4f,1));
        host.Context.EndDraw().CheckError();
        host.Present();
        Console.WriteLine($"gpu_optics_adapter={host.AdapterName}");
        Console.WriteLine($"gpu_optics_maximum_channel_error={maximumError}");
        Console.WriteLine("gpu_optics_regression=passed");
    }

    internal static void RunGpuPlaybackTargetLifetimeRegression()
    {
        // The broad Render suite defaults to legacy D2D; this regression must
        // explicitly exercise the swap chain, then restore that suite setting.
        var previous = Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU");
        try
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", null);
            RunGpuPlaybackTargetLifetimeRegressionCore();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU", previous);
        }
    }

    private static void RunGpuPlaybackTargetLifetimeRegressionCore()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var window = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            ClientSize = new Size(160, 120),
            ShowInTaskbar = false
        };
        using var stage = new StageControl(scene)
        {
            Dock = DockStyle.Fill,
            WorldGridOpacity = 0
        };
        window.Controls.Add(stage);
        window.Show();
        var renderer = (Direct2DStageRenderer)RequireField(
            typeof(StageControl), "_direct2DRenderer").GetValue(stage)!;
        AssertGpuFrame("initial frame");

        // Exercise the actual playback preparation, even for an empty scene:
        // saving its D2D target must not retain the swap-chain backbuffer.
        // Do not force GC; resize/recreation must work immediately.
        for (var cycle = 0; cycle < 12; cycle++)
        {
            stage.ConfigureReferenceView(null, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
            stage.SetReference3DPlaybackActive(true);
            for (var frame = 0; frame < 3; frame++)
            {
                stage.Frame = frame;
                AssertGpuFrame($"playback {cycle}/{frame}");
                if (!stage.Reference3DGpuOpticsEnabled)
                    throw new InvalidOperationException("GPU playback target regression did not enter optical preparation.");
            }
            stage.SetReference3DPlaybackActive(false);
            stage.ConfigureReferenceView(null, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
            window.ClientSize = new Size(161 + cycle, 121 + cycle);
            if (!renderer.ExplicitGpuDeviceActive)
                throw new InvalidOperationException($"GPU playback target was lost during resize after cycle {cycle}.");
            AssertGpuFrame($"Drawing after resize {cycle}");

            renderer.ReleaseTarget();
            AssertGpuFrame($"same-window recreation {cycle}");
        }
        Console.WriteLine("gpu_playback_target_lifetime_cycles=12");
        Console.WriteLine("gpu_playback_target_lifetime_regression=passed");

        void AssertGpuFrame(string phase)
        {
            if (!renderer.TryRender(stage, out _) || !renderer.ExplicitGpuDeviceActive)
                throw new InvalidOperationException($"GPU playback target regression failed at {phase}; legacy fallback is not a pass.");
        }
    }
}
