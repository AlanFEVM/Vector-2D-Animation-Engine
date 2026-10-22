using Vortice.Direct2D1;
using D2DRect = Vortice.Mathematics.Rect;
using GdiColor = System.Drawing.Color;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawCanvasBackground(StageControl stage)
    {
        if (_target is null || stage.CanvasPresentation is not { } canvas) return;
        var bounds = stage.CanvasScreenBounds();
        var visible = System.Drawing.RectangleF.Intersect(bounds, stage.ClientRectangle);
        if (visible.Width <= 0 || visible.Height <= 0) return;
        _target.PushAxisAlignedClip(CanvasRect(visible), AntialiasMode.Aliased);
        try
        {
            if (canvas.Settings.BackgroundKind == SceneBackgroundKind.None
                || GdiColor.FromArgb(canvas.Settings.ColorArgb).A < 255)
            {
                using var checker = _target.CreateBitmapBrush(BitmapObjectBitmap(SceneCanvasCheckerboard.Raster));
                checker.ExtendModeX = ExtendMode.Wrap;
                checker.ExtendModeY = ExtendMode.Wrap;
                checker.InterpolationMode = BitmapInterpolationMode.NearestNeighbor;
                _target.FillRectangle(CanvasRect(visible), checker);
            }
            if (canvas.Settings.BackgroundKind != SceneBackgroundKind.None)
                _target.FillRectangle(CanvasRect(bounds), BrushFor(canvas.Settings.ColorArgb));
            if (canvas.Settings.BackgroundKind == SceneBackgroundKind.Image && canvas.Image is { } image)
            {
                var destination = SceneCanvasLayout.ResolveImageBounds(canvas.Settings, bounds, canvas.ImagePixelSize);
                var level = canvas.PointSampling ? 0 : MinificationLevel(image, destination.Width, destination.Height);
                var raster = level > 0 ? BitmapImageRasterizer.TryGetMinifiedRaster(image, level) ?? image : image;
                var bitmap = BitmapObjectBitmap(raster);
                _target.DrawBitmap(bitmap, CanvasRect(destination), canvas.Settings.ImageOpacity,
                    canvas.PointSampling ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear,
                    new D2DRect(0, 0, raster.PixelWidth, raster.PixelHeight));
            }
        }
        finally
        {
            _target.PopAxisAlignedClip();
        }
    }

    private void DrawCanvasFrame(StageControl stage)
    {
        if (_target is null || stage.CanvasPresentation is not { } canvas) return;
        var bounds = stage.CanvasScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (canvas.Settings.DimOutside)
        {
            var shade = BrushFor(GdiColor.FromArgb(105, 0, 0, 0).ToArgb());
            var visible = System.Drawing.RectangleF.Intersect(bounds, stage.ClientRectangle);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                _target.FillRectangle(new D2DRect(0, 0, stage.Width, stage.Height), shade);
            }
            else
            {
                FillOutside(0, 0, stage.Width, visible.Top);
                FillOutside(0, visible.Bottom, stage.Width, stage.Height - visible.Bottom);
                FillOutside(0, visible.Top, visible.Left, visible.Height);
                FillOutside(visible.Right, visible.Top, stage.Width - visible.Right, visible.Height);
            }

            void FillOutside(float left, float top, float width, float height)
            {
                if (width > 0 && height > 0) _target.FillRectangle(new D2DRect(left, top, width, height), shade);
            }
        }
        _target.DrawRectangle(CanvasRect(bounds), BrushFor(GdiColor.FromArgb(165, Theme.Muted).ToArgb()),
            Math.Max(1f, stage.DeviceDpi / 96f));
    }

    private static D2DRect CanvasRect(System.Drawing.RectangleF bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
