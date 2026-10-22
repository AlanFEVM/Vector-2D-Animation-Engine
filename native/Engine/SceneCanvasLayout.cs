namespace VectorAnimationEngine;

internal static class SceneCanvasLayout
{
    public static RectangleF ResolveImageBounds(
        SceneCanvasSettings settings, RectangleF canvas, SizeF imagePixelSize)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0
            || imagePixelSize.Width <= 0 || imagePixelSize.Height <= 0) return RectangleF.Empty;
        var pixelsToScreen = canvas.Width / settings.Width;
        var fit = settings.ImageFit switch
        {
            CanvasImageFit.Cover => Math.Max(canvas.Width / imagePixelSize.Width, canvas.Height / imagePixelSize.Height),
            CanvasImageFit.Original => pixelsToScreen,
            _ => Math.Min(canvas.Width / imagePixelSize.Width, canvas.Height / imagePixelSize.Height)
        };
        var width = imagePixelSize.Width * fit * settings.ImageScale;
        var height = imagePixelSize.Height * fit * settings.ImageScale;
        return new RectangleF(
            canvas.X + (canvas.Width - width) * 0.5f + settings.ImageOffsetX * pixelsToScreen,
            canvas.Y + (canvas.Height - height) * 0.5f + settings.ImageOffsetY * pixelsToScreen,
            width, height);
    }
}
