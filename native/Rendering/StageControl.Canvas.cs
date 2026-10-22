using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed record SceneCanvasPresentation(
    SceneCanvasSettings Settings,
    BitmapImageRaster? Image,
    SizeF ImagePixelSize,
    bool PointSampling);

internal static class SceneCanvasCheckerboard
{
    internal static BitmapImageRaster Raster { get; } = Create();

    private static BitmapImageRaster Create()
    {
        var raster = new BitmapImageRaster(new BitmapImageRasterKey("scene-canvas-checkerboard", 16, 16));
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
            {
                var dark = ((row / 8 + column / 8) & 1) != 0;
                var offset = row * raster.Stride + column * 4;
                raster.Pixels[offset] = (byte)(dark ? 182 : 212);
                raster.Pixels[offset + 1] = (byte)(dark ? 178 : 208);
                raster.Pixels[offset + 2] = (byte)(dark ? 175 : 205);
                raster.Pixels[offset + 3] = 255;
            }
        return raster;
    }
}

internal sealed partial class StageControl
{
    internal SceneCanvasPresentation? CanvasPresentation { get; private set; }
    private bool _renderingCanvasOutput;

    internal float CanvasWorldWidth => CanvasPresentation is { } canvas
        ? VectorUnits.FromPixels(canvas.Settings.Width) : Scene.StageWidth;
    internal float CanvasWorldHeight => CanvasPresentation is { } canvas
        ? VectorUnits.FromPixels(canvas.Settings.Height) : Scene.StageHeight;

    internal void SetCanvasPresentation(SceneCanvasPresentation? presentation)
    {
        if (CanvasPresentation == presentation) return;
        CanvasPresentation = presentation;
        ClearGdiBaseFrameCache();
        Invalidate();
    }

    internal RectangleF CanvasScreenBounds()
    {
        if (CanvasPresentation is not { } canvas) return RectangleF.Empty;
        if (!_renderingCanvasOutput && (ReferenceDimension == SceneDimension.ThreeD
            || Reference2DViewDirection != ReferenceViewDirection.Front))
        {
            var available = new RectangleF(24, 24, Math.Max(1, Width - 48), Math.Max(1, Height - 48));
            var scale = Math.Min(available.Width / canvas.Settings.Width, available.Height / canvas.Settings.Height);
            var width = canvas.Settings.Width * scale;
            var height = canvas.Settings.Height * scale;
            return new RectangleF((Width - width) * 0.5f, (Height - height) * 0.5f, width, height);
        }
        var topLeft = WorldToScreen(-CanvasWorldWidth * 0.5f, -CanvasWorldHeight * 0.5f);
        var bottomRight = WorldToScreen(CanvasWorldWidth * 0.5f, CanvasWorldHeight * 0.5f);
        if (RendersReferenceProjection)
        {
            if (!TryProjectScenePosition(new System.Numerics.Vector3(-CanvasWorldWidth * 0.5f, -CanvasWorldHeight * 0.5f, 0),
                    out topLeft, out _)
                || !TryProjectScenePosition(new System.Numerics.Vector3(CanvasWorldWidth * 0.5f, CanvasWorldHeight * 0.5f, 0),
                    out bottomRight, out _)) return RectangleF.Empty;
        }
        return RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    internal void DrawCanvasBackgroundGdi(Graphics graphics, RectangleF? boundsOverride = null)
    {
        if (CanvasPresentation is not { } canvas) return;
        var bounds = boundsOverride ?? CanvasScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.IntersectsWith(ClientRectangle)) return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            if (!_renderingCanvasOutput)
            {
                using var tile = SceneCanvasCheckerboard.Raster.AcquireBitmap();
                using var checker = new TextureBrush(tile.Bitmap, WrapMode.Tile);
                graphics.FillRectangle(checker, bounds);
            }
            if (canvas.Settings.BackgroundKind != SceneBackgroundKind.None)
            {
                using var color = new SolidBrush(Color.FromArgb(canvas.Settings.ColorArgb));
                graphics.FillRectangle(color, bounds);
            }
            if (canvas.Settings.BackgroundKind == SceneBackgroundKind.Image && canvas.Image is { } image)
            {
                var destination = SceneCanvasLayout.ResolveImageBounds(canvas.Settings, bounds, canvas.ImagePixelSize);
                if (!destination.IntersectsWith(bounds)) return;
                graphics.InterpolationMode = canvas.PointSampling
                    ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                using var lease = image.AcquireBitmap();
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = canvas.Settings.ImageOpacity });
                graphics.DrawImage(lease.Bitmap, [
                    new PointF(destination.Left, destination.Top),
                    new PointF(destination.Right, destination.Top),
                    new PointF(destination.Left, destination.Bottom)
                ], new RectangleF(0, 0, image.PixelWidth, image.PixelHeight), GraphicsUnit.Pixel, attributes);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawCanvasFrameGdi(Graphics graphics)
    {
        if (_renderingCanvasOutput || CanvasPresentation is not { } canvas) return;
        var bounds = CanvasScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (canvas.Settings.DimOutside)
        {
            using var outside = new Region(ClientRectangle);
            outside.Exclude(bounds);
            using var shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0));
            graphics.FillRegion(shade, outside);
        }
        using var border = new Pen(Color.FromArgb(165, Theme.Muted), Math.Max(1f, DeviceDpi / 96f));
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    internal void FitCanvas()
    {
        if (Width <= 0 || Height <= 0 || CanvasPresentation is null) return;
        _pendingVisibleWorldWidth = null;
        EndZoomLodPreview(invalidate: false);
        CameraX = 0;
        CameraY = 0;
        Zoom = Math.Clamp(Math.Min(Width / VectorUnits.ToPixels(CanvasWorldWidth),
            Height / VectorUnits.ToPixels(CanvasWorldHeight)) * 0.85f, 0.02f, 64f);
        if (RendersReferenceProjection && ReferenceDimension == SceneDimension.TwoD
            && Reference2DViewDirection == ReferenceViewDirection.Front)
        {
            CompleteReferenceCameraTransitionForDirectInput();
            _referenceZoomScale = Math.Clamp(Math.Min(Width / CanvasWorldWidth,
                Height / CanvasWorldHeight) * 0.85f / 0.035f, 0.25f, 8f);
            _referenceTargetX = 0;
            _referenceTargetY = -Height * 0.08f / (0.035f * _referenceZoomScale);
            _referenceTargetZ = 0;
        }
        RefreshBrushTipCursorScale();
        Invalidate();
        RaiseViewChanged();
    }
}
