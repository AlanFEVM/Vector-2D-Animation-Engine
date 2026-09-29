using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private Bitmap? _softwareFrameSurface;
    private ID2D1Bitmap? _softwareFrameBitmap;

    // Flip-model HWNDs retain their last presented frame over subsequent GDI
    // window painting, even after releasing the swap chain. Keep software
    // composition at full resolution and upload only its final presentation.
    internal bool TryPresentSoftwareFrame(StageControl stage, Action<Graphics> drawFrame)
    {
        if (_disabled || stage.Width <= 0 || stage.Height <= 0
            || (!RequiresSoftwareLayerCompositing(stage) && !RequiresSoftwareDistortion(stage))) return false;
        var drawing = false;
        try
        {
            EnsureTarget(stage);
            if (_target is null) return false;
            if (_softwareFrameSurface?.Size != stage.ClientSize)
            {
                ClearSoftwareFramePresentation();
                _softwareFrameSurface = new Bitmap(stage.Width, stage.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            }
            var commandStarted = Stopwatch.GetTimestamp();
            using (var graphics = Graphics.FromImage(_softwareFrameSurface)) drawFrame(graphics);
            var data = _softwareFrameSurface.LockBits(stage.ClientRectangle, ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var pitch = checked((uint)data.Stride);
                if (_softwareFrameBitmap is null)
                    _softwareFrameBitmap = _target.CreateBitmap(new SizeI(stage.Width, stage.Height),
                        data.Scan0, pitch, new BitmapProperties(
                            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96));
                else if (_softwareFrameBitmap.CopyFromMemory(data.Scan0, pitch).Failure)
                    throw new InvalidOperationException("Software frame upload failed.");
            }
            finally { _softwareFrameSurface.UnlockBits(data); }
            _target.BeginDraw();
            drawing = true;
            _target.Transform = Matrix3x2.Identity;
            _target.DrawBitmap(_softwareFrameBitmap, 1f, BitmapInterpolationMode.NearestNeighbor);
            LastCommandMilliseconds = Stopwatch.GetElapsedTime(commandStarted).TotalMilliseconds;
            var presentStarted = Stopwatch.GetTimestamp();
            var result = _target.EndDraw();
            drawing = false;
            if (result.Failure) throw new InvalidOperationException("Software frame presentation failed.");
            _gpuDevice?.Present();
            LastPresentMilliseconds = Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds;
            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception exception)
        {
            if (drawing)
            {
                try { _target?.EndDraw(); } catch { }
            }
            AppLog.Warn($"Software frame presentation skipped; using GDI: {exception.Message}");
            RecordFailure();
            ResetTarget();
            return false;
        }
    }

    private void ClearSoftwareFramePresentation()
    {
        _softwareFrameBitmap?.Dispose();
        _softwareFrameBitmap = null;
        _softwareFrameSurface?.Dispose();
        _softwareFrameSurface = null;
    }
}
