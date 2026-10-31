using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using SizeI = Vortice.Mathematics.SizeI;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1Bitmap1? _shotPreviewCaptureTarget;
    private ID2D1Bitmap1? _shotPreviewCaptureReadback;
    private Size _shotPreviewCaptureSize;
    private bool _capturingShotPreview;
    private bool _shotPreviewCaptureFailed;

    internal void ReleaseShotPreviewCaptureResources()
    {
        _shotPreviewCaptureReadback?.Dispose();
        _shotPreviewCaptureReadback = null;
        _shotPreviewCaptureTarget?.Dispose();
        _shotPreviewCaptureTarget = null;
        _shotPreviewCaptureSize = Size.Empty;
    }

    internal bool TryCapturePlanarShotPreview(StageControl stage, SceneShotSettings settings, Size size,
        SceneShotSettings? parallaxAnchor, out Bitmap? bitmap)
    {
        bitmap = null;
        if (_disabled || _shotPreviewCaptureFailed || _target is not ID2D1DeviceContext context
            || RequiresSoftwareLayerCompositing(stage) || RequiresSoftwareDistortion(stage)) return false;
        using var previousTarget = context.Target;
        var previousTransform = context.Transform;
        var previousOptics = stage.Reference3DGpuOpticsEnabled;
        var drawing = false;
        try
        {
            if (_shotPreviewCaptureSize != size || _shotPreviewCaptureTarget is null
                || _shotPreviewCaptureReadback is null)
            {
                ReleaseShotPreviewCaptureResources();
                var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied);
                _shotPreviewCaptureTarget = context.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0,
                    new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
                _shotPreviewCaptureReadback = context.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0,
                    new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
                _shotPreviewCaptureSize = size;
            }
            _capturingShotPreview = true;
            stage.Reference3DGpuOpticsEnabled = false;
            using var commands = context.CreateCommandList();
            context.Target = commands;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw();
            drawing = true;
            var transform = Matrix3x2.Identity;
            stage.RenderShotPreviewScene(settings, size, parallaxAnchor, projection =>
            {
                var elements = projection.Elements;
                transform = new Matrix3x2(elements[0], elements[1], elements[2], elements[3], elements[4], elements[5]);
                return DrawShotPreviewScene(stage);
            });
            var result = context.EndDraw();
            drawing = false;
            result.CheckError();
            commands.Close().CheckError();
            context.Target = _shotPreviewCaptureTarget;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw();
            drawing = true;
            var clear = ToD2D(stage.CanvasPresentation is null ? stage.BackColor : Color.Transparent);
            context.Clear(in clear);
            context.Transform = transform;
            context.DrawImage(commands);
            result = context.EndDraw();
            drawing = false;
            result.CheckError();
            context.Target = previousTarget;
            _shotPreviewCaptureReadback.CopyFromBitmap(_shotPreviewCaptureTarget).CheckError();
            var mapped = _shotPreviewCaptureReadback.Map(MapOptions.Read);
            try
            {
                bitmap = new Bitmap(size.Width, size.Height, GdiPixelFormat.Format32bppPArgb);
                var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly,
                    GdiPixelFormat.Format32bppPArgb);
                try
                {
                    var rowBytes = size.Width * 4;
                    var buffer = ArrayPool<byte>.Shared.Rent(rowBytes);
                    try
                    {
                        for (var row = 0; row < size.Height; row++)
                        {
                            Marshal.Copy(IntPtr.Add(mapped.Bits, checked(row * (int)mapped.Pitch)), buffer, 0, rowBytes);
                            Marshal.Copy(buffer, 0, IntPtr.Add(data.Scan0, row * data.Stride), rowBytes);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                finally { bitmap.UnlockBits(data); }
            }
            finally { _shotPreviewCaptureReadback.Unmap(); }
            return true;
        }
        catch (Exception exception)
        {
            bitmap?.Dispose();
            bitmap = null;
            _shotPreviewCaptureFailed = true;
            AppLog.Warn($"GPU shot preview capture failed; using GDI: {exception.Message}");
            return false;
        }
        finally
        {
            if (drawing) context.EndDraw();
            context.Target = previousTarget;
            context.Transform = previousTransform;
            _capturingShotPreview = false;
            stage.Reference3DGpuOpticsEnabled = previousOptics;
            ClearTransientGradientBrushes();
            ClearTransientShapeGradientBitmaps();
            ClearTransientPathGradientBrushes();
        }
    }

    private RenderStats DrawShotPreviewScene(StageControl stage)
    {
        stage.BeginScenePassOrder();
        DrawCanvasBackground(stage);
        if (stage.RendersReferenceProjection) return DrawReference3DScene(stage);
        var editable = stage.Scene;
        var combined = default(RenderStats);
        try
        {
            if (stage.OnionSkinScene is { } onionSkin)
            {
                stage.Scene = onionSkin;
                stage.RecordOnionSkinScenePass();
                combined = DrawScene(stage, int.MaxValue, forceObjectRenderer: true);
            }
            if (stage.UnderlayScene is { } underlay)
            {
                stage.RecordUnderlayScenePass();
                if (stage.IsUnderlayHostScene(editable))
                {
                    _underlayRenderOrder.Collect(underlay, stage.VisibleWorldBounds(), stage.Frame);
                    _underlayDrawLimit = int.MaxValue;
                    _underlayDrawnObjects = 0;
                    combined = RenderStats.Combine(combined, new RenderStats(
                        _underlayRenderOrder.VisibleCount, _underlayRenderOrder.VisibleCount,
                        _underlayRenderOrder.VisibleAtoms, 0, _underlayRenderOrder.ScannedCount, false));
                }
                else
                {
                    stage.Scene = underlay;
                    combined = RenderStats.Combine(combined, DrawScene(stage, int.MaxValue, forceObjectRenderer: true));
                }
            }
            stage.Scene = editable;
            stage.RecordEditableScenePass();
            return RenderStats.Combine(combined, DrawScene(stage, int.MaxValue, forceObjectRenderer: true));
        }
        finally { stage.Scene = editable; }
    }
}
