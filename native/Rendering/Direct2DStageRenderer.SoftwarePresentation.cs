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
    private ID2D1DCRenderTarget? _rasterZoomTarget;
    private ID2D1Bitmap? _rasterZoomBitmap;
    private SizeI _rasterZoomTargetSize;
    private long _rasterZoomRevision = -1;
    private bool _rasterZoomUnavailable;

    // Gesture zoom preview: scale the cached GDI base frame with the Direct2D software
    // rasterizer instead of the GDI+ bilinear copy. The exact frame after the gesture
    // still goes through the unchanged GDI path; any D2D failure disables only this
    // preview and leaves the previous GDI copy in place.
    internal bool TryDrawRasterZoomPreview(
        Graphics graphics,
        Bitmap source,
        long sourceRevision,
        float offsetX,
        float offsetY,
        float scale,
        System.Drawing.Size viewport,
        out double copyMilliseconds)
    {
        copyMilliseconds = 0;
        if (_disabled || _rasterZoomUnavailable || scale <= 0f
            || viewport.Width <= 0 || viewport.Height <= 0) return false;
        var drawing = false;
        try
        {
            EnsureRasterZoomTarget(viewport);
            EnsureRasterZoomBitmap(source, sourceRevision);
            if (_rasterZoomTarget is null || _rasterZoomBitmap is null) return false;

            var started = Stopwatch.GetTimestamp();
            var hdc = graphics.GetHdc();
            try
            {
                _rasterZoomTarget.BindDC(hdc, new Vortice.RawRect(0, 0, viewport.Width, viewport.Height));
                _rasterZoomTarget.BeginDraw();
                drawing = true;
                _rasterZoomTarget.Transform = Matrix3x2.Identity;
                _rasterZoomTarget.AntialiasMode = AntialiasMode.Aliased;
                // Vortice.Mathematics.Rect takes (x, y, width, height), not left/top/right/bottom.
                _rasterZoomTarget.DrawBitmap(
                    _rasterZoomBitmap,
                    new Rect(offsetX, offsetY, source.Width * scale, source.Height * scale),
                    1f,
                    BitmapInterpolationMode.Linear,
                    new Rect(0, 0, source.Width, source.Height));
                var result = _rasterZoomTarget.EndDraw();
                drawing = false;
                if (result.Failure) throw new InvalidOperationException("Raster zoom preview draw failed.");
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
            copyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return true;
        }
        catch (Exception exception)
        {
            if (drawing)
            {
                try { _rasterZoomTarget?.EndDraw(); } catch { }
            }
            AppLog.Warn($"Raster zoom preview skipped; using GDI: {exception.Message}");
            ClearRasterZoomPreview();
            _rasterZoomUnavailable = true;
            copyMilliseconds = 0;
            return false;
        }
    }

    private void EnsureRasterZoomTarget(System.Drawing.Size viewport)
    {
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        var size = new SizeI(Math.Max(1, viewport.Width), Math.Max(1, viewport.Height));
        if (_rasterZoomTarget is not null && _rasterZoomTargetSize == size) return;
        ClearRasterZoomPreview();
        _rasterZoomTarget = _factory.CreateDCRenderTarget(new RenderTargetProperties(
            RenderTargetType.Software,
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore),
            96,
            96,
            RenderTargetUsage.None,
            FeatureLevel.Default));
        _rasterZoomTargetSize = size;
    }

    private void EnsureRasterZoomBitmap(Bitmap source, long sourceRevision)
    {
        if (_rasterZoomBitmap is not null && _rasterZoomRevision == sourceRevision) return;
        _rasterZoomBitmap?.Dispose();
        _rasterZoomBitmap = null;
        _rasterZoomRevision = -1;
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            _rasterZoomBitmap = _rasterZoomTarget!.CreateBitmap(
                new SizeI(source.Width, source.Height),
                data.Scan0,
                checked((uint)data.Stride),
                new BitmapProperties(
                    new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96));
        }
        finally
        {
            source.UnlockBits(data);
        }
        _rasterZoomRevision = sourceRevision;
    }

    private void ClearRasterZoomPreview()
    {
        _rasterZoomTarget?.Dispose();
        _rasterZoomTarget = null;
        _rasterZoomBitmap?.Dispose();
        _rasterZoomBitmap = null;
        _rasterZoomTargetSize = default;
        _rasterZoomRevision = -1;
    }

    // Flip-model HWNDs retain their last presented frame over subsequent GDI
    // window painting, even after releasing the swap chain. Keep software
    // composition at full resolution and upload only its final presentation.
    internal bool TryPresentSoftwareFrame(StageControl stage, Action<Graphics> drawFrame, bool preparedFrame = false)
    {
        if (_disabled || stage.Width <= 0 || stage.Height <= 0
            || (!preparedFrame && !stage.ShotWorkspacePreviewActive && !RequiresSoftwareLayerCompositing(stage) && !RequiresSoftwareDistortion(stage))) return false;
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
