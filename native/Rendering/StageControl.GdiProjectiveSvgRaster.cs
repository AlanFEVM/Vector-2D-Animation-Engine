using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const double ProjectiveSvgRasterEdgeEpsilon = 0.0001d;

    private bool TryDrawBoundedReference3DProjectiveSvg(
        Graphics graphics,
        int objectIndex,
        ImportedSvgRaster raster,
        IReadOnlyList<Reference3DProjectiveTriangle> triangles,
        float opacity) => TryDrawBoundedReference3DProjectiveRaster(
            graphics,
            objectIndex,
            raster.Pixels,
            raster.Stride,
            raster.PixelWidth,
            raster.PixelHeight,
            triangles,
            opacity);

    /// <summary>
    /// Generic projective texture submission. Shared by imported SVG and placed bitmaps so
    /// both keep one perspective-correct sampling implementation; the only per-kind inputs
    /// are the pixel buffer geometry.
    /// </summary>
    private bool TryDrawBoundedReference3DProjectiveRaster(
        Graphics graphics,
        int objectIndex,
        byte[] pixels,
        int stride,
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<Reference3DProjectiveTriangle> triangles,
        float opacity)
    {
        if (triangles.Count == 0
            || pixelWidth <= 0
            || pixelHeight <= 0
            || pixels is null
            || !float.IsFinite(opacity))
        {
            return false;
        }

        using var screenMask = CreateReference3DPath(
            GetReference3DProjectedContours(objectIndex),
            fillOnly: true);
        if (screenMask.PointCount == 0) return false;

        var state = graphics.Save();
        try
        {
            // Keep all sampling in screen coordinates. The existing optical layout
            // transform is restored before the single final bitmap submission.
            ResetReference3DGdiScreenTransform(graphics);
            graphics.SetClip(screenMask, CombineMode.Intersect);
            if (!TryGetBoundedProjectiveSvgOutput(
                    graphics,
                    out var outputBounds,
                    out var outputWidth,
                    out var outputHeight,
                    out var screenUnitsPerPixelX,
                    out var screenUnitsPerPixelY))
            {
                return false;
            }

            var outputStride = checked(outputWidth * sizeof(int));
            var outputPixels = new byte[checked(outputStride * outputHeight)];
            var sampledPixel = false;
            var opacityScale = Math.Clamp(opacity, 0f, 1f);

            foreach (var triangle in triangles)
            {
                if (!TryGetFiniteTriangleBounds(triangle, out var triangleBounds))
                {
                    return false;
                }

                var visibleTriangle = RectangleF.Intersect(
                    triangleBounds,
                    new RectangleF(
                        outputBounds.Left,
                        outputBounds.Top,
                        outputBounds.Width,
                        outputBounds.Height));
                if (visibleTriangle.Width <= 0f || visibleTriangle.Height <= 0f) continue;

                if (!TryGetReference3DTextureToScreenTransform(
                        triangle,
                        pixelWidth,
                        pixelHeight,
                        out var textureToScreen)
                    || !Matrix3x2.Invert(textureToScreen, out var screenToTexture)
                    || !IsFinite(screenToTexture))
                {
                    return false;
                }

                var area = Edge(triangle.A.Screen, triangle.B.Screen, triangle.C.Screen);
                if (!double.IsFinite(area)
                    || Math.Abs(area) <= ProjectiveSvgRasterEdgeEpsilon)
                {
                    return false;
                }

                var left = Math.Max(
                    0,
                    (int)MathF.Floor(
                        (visibleTriangle.Left - outputBounds.Left) / screenUnitsPerPixelX)
                    - 1);
                var top = Math.Max(
                    0,
                    (int)MathF.Floor(
                        (visibleTriangle.Top - outputBounds.Top) / screenUnitsPerPixelY)
                    - 1);
                var right = Math.Min(
                    outputWidth - 1,
                    (int)MathF.Ceiling(
                        (visibleTriangle.Right - outputBounds.Left) / screenUnitsPerPixelX)
                    + 1);
                var bottom = Math.Min(
                    outputHeight - 1,
                    (int)MathF.Ceiling(
                        (visibleTriangle.Bottom - outputBounds.Top) / screenUnitsPerPixelY)
                    + 1);
                if (right < left || bottom < top) continue;

                for (var y = top; y <= bottom; y++)
                {
                    var screenY = outputBounds.Top
                        + ((double)y + 0.5d) * screenUnitsPerPixelY;
                    if (!TryGetTriangleScanlineXBounds(
                            triangle,
                            screenY,
                            out var scanlineLeft,
                            out var scanlineRight))
                    {
                        continue;
                    }

                    // Clamp the intersections before converting to pixel indices so
                    // distant projective vertices cannot overflow an integer cast.
                    scanlineLeft = Math.Max(
                        (double)outputBounds.Left,
                        Math.Max((double)visibleTriangle.Left, scanlineLeft));
                    scanlineRight = Math.Min(
                        (double)outputBounds.Right,
                        Math.Min((double)visibleTriangle.Right, scanlineRight));
                    if (!double.IsFinite(scanlineLeft)
                        || !double.IsFinite(scanlineRight)
                        || scanlineRight < scanlineLeft)
                    {
                        continue;
                    }

                    var rowLeft = Math.Max(
                        left,
                        (int)Math.Floor(
                            (scanlineLeft - outputBounds.Left) / screenUnitsPerPixelX)
                        - 1);
                    var rowRight = Math.Min(
                        right,
                        (int)Math.Ceiling(
                            (scanlineRight - outputBounds.Left) / screenUnitsPerPixelX)
                        + 1);
                    if (rowRight < rowLeft) continue;

                    for (var x = rowLeft; x <= rowRight; x++)
                    {
                        var screenPoint = new PointF(
                            (float)(outputBounds.Left
                                + (x + 0.5d) * screenUnitsPerPixelX),
                            (float)screenY);
                        if (!IsTopLeftTrianglePixel(triangle, area, screenPoint)) continue;

                        var texturePoint = Vector2.Transform(
                            new Vector2(screenPoint.X, screenPoint.Y),
                            screenToTexture);
                        if (!TrySamplePremultipliedBilinear(
                                pixels,
                                stride,
                                pixelWidth,
                                pixelHeight,
                                texturePoint,
                                opacityScale,
                                outputPixels,
                                outputStride,
                                x,
                                y))
                        {
                            return false;
                        }
                        sampledPixel = true;
                    }
                }
            }

            if (!sampledPixel) return true;

            using var bitmap = new Bitmap(
                outputWidth,
                outputHeight,
                PixelFormat.Format32bppPArgb);
            if (!TryWritePremultipliedPixels(bitmap, outputPixels, outputWidth, outputHeight))
            {
                return false;
            }

            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(
                bitmap,
                outputBounds,
                0,
                0,
                outputWidth,
                outputHeight,
                GraphicsUnit.Pixel);
            return true;
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private bool TryGetBoundedProjectiveSvgOutput(
        Graphics graphics,
        out Rectangle outputBounds,
        out int outputWidth,
        out int outputHeight,
        out float screenUnitsPerPixelX,
        out float screenUnitsPerPixelY)
    {
        outputBounds = Rectangle.Empty;
        outputWidth = 0;
        outputHeight = 0;
        screenUnitsPerPixelX = 0f;
        screenUnitsPerPixelY = 0f;

        var clientWidth = ClientSize.Width;
        var clientHeight = ClientSize.Height;
        if (clientWidth <= 0 || clientHeight <= 0) return false;
        var filterPadding = LayerFilterBounds.GetPadding(Scene, ActiveViewZoom);
        var viewport = Rectangle.FromLTRB(-filterPadding.Left, -filterPadding.Top,
            clientWidth + filterPadding.Right, clientHeight + filterPadding.Bottom);

        RectangleF visible;
        try
        {
            visible = graphics.VisibleClipBounds;
        }
        catch (ExternalException)
        {
            return false;
        }

        if (!IsFinite(visible)) return false;
        visible = RectangleF.Intersect(
            visible,
            viewport);
        if (_reference3DGdiRasterLayout is { } layout)
        {
            if (layout.Bounds.Width <= 0
                || layout.Bounds.Height <= 0
                || layout.PixelWidth <= 0
                || layout.PixelHeight <= 0
                || layout.PixelCount > MaximumReference3DOpticalSurfacePixels
                || !float.IsFinite(layout.ScaleX)
                || !float.IsFinite(layout.ScaleY)
                || layout.ScaleX <= 0f
                || layout.ScaleY <= 0f)
            {
                return false;
            }

            visible = RectangleF.Intersect(
                visible,
                new RectangleF(
                    layout.Bounds.Left,
                    layout.Bounds.Top,
                    layout.Bounds.Width,
                    layout.Bounds.Height));
            screenUnitsPerPixelX = layout.Bounds.Width / (float)layout.PixelWidth;
            screenUnitsPerPixelY = layout.Bounds.Height / (float)layout.PixelHeight;
        }
        else
        {
            screenUnitsPerPixelX = 1f;
            screenUnitsPerPixelY = 1f;
        }

        if (!IsFinite(visible)
            || visible.Width <= 0f
            || visible.Height <= 0f
            || !float.IsFinite(screenUnitsPerPixelX)
            || !float.IsFinite(screenUnitsPerPixelY)
            || screenUnitsPerPixelX <= 0f
            || screenUnitsPerPixelY <= 0f)
        {
            return false;
        }

        var left = Math.Clamp((int)MathF.Floor(visible.Left), viewport.Left, viewport.Right);
        var top = Math.Clamp((int)MathF.Floor(visible.Top), viewport.Top, viewport.Bottom);
        var right = Math.Clamp((int)MathF.Ceiling(visible.Right), viewport.Left, viewport.Right);
        var bottom = Math.Clamp((int)MathF.Ceiling(visible.Bottom), viewport.Top, viewport.Bottom);
        if (right <= left || bottom <= top) return false;
        outputBounds = Rectangle.FromLTRB(left, top, right, bottom);

        var width = Math.Ceiling(outputBounds.Width / (double)screenUnitsPerPixelX);
        var height = Math.Ceiling(outputBounds.Height / (double)screenUnitsPerPixelY);
        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0d
            || height <= 0d
            || width > int.MaxValue
            || height > int.MaxValue)
        {
            return false;
        }

        var pixelCount = width * height;
        if (!double.IsFinite(pixelCount) || pixelCount <= 0d)
        {
            return false;
        }

        if (pixelCount > MaximumReference3DOpticalSurfacePixels)
        {
            var scale = Math.Sqrt(MaximumReference3DOpticalSurfacePixels / pixelCount);
            width = Math.Max(1d, Math.Floor(width * scale));
            height = Math.Max(1d, Math.Floor(height * scale));
            if (!double.IsFinite(width)
                || !double.IsFinite(height)
                || width <= 0d
                || height <= 0d)
            {
                return false;
            }

            var maximumPixels = (double)MaximumReference3DOpticalSurfacePixels;
            if (width > maximumPixels / height)
            {
                width = Math.Max(1d, Math.Floor(maximumPixels / height));
            }
            if (height > maximumPixels / width)
            {
                height = Math.Max(1d, Math.Floor(maximumPixels / width));
            }
        }

        if (width * height > MaximumReference3DOpticalSurfacePixels)
        {
            return false;
        }

        outputWidth = Math.Max(1, (int)width);
        outputHeight = Math.Max(1, (int)height);
        screenUnitsPerPixelX = outputBounds.Width / (float)outputWidth;
        screenUnitsPerPixelY = outputBounds.Height / (float)outputHeight;
        return float.IsFinite(screenUnitsPerPixelX)
            && float.IsFinite(screenUnitsPerPixelY)
            && screenUnitsPerPixelX > 0f
            && screenUnitsPerPixelY > 0f;
    }

    private static bool TryGetFiniteTriangleBounds(
        Reference3DProjectiveTriangle triangle,
        out RectangleF bounds)
    {
        var left = MathF.Min(
            triangle.A.Screen.X,
            MathF.Min(triangle.B.Screen.X, triangle.C.Screen.X));
        var top = MathF.Min(
            triangle.A.Screen.Y,
            MathF.Min(triangle.B.Screen.Y, triangle.C.Screen.Y));
        var right = MathF.Max(
            triangle.A.Screen.X,
            MathF.Max(triangle.B.Screen.X, triangle.C.Screen.X));
        var bottom = MathF.Max(
            triangle.A.Screen.Y,
            MathF.Max(triangle.B.Screen.Y, triangle.C.Screen.Y));
        bounds = RectangleF.FromLTRB(left, top, right, bottom);
        return IsFinite(bounds) && bounds.Width > 0f && bounds.Height > 0f;
    }

    private static bool TryGetTriangleScanlineXBounds(
        Reference3DProjectiveTriangle triangle,
        double screenY,
        out double left,
        out double right)
    {
        var minimumX = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        if (!double.IsFinite(screenY))
        {
            left = 0d;
            right = 0d;
            return false;
        }

        AddIntersection(triangle.A.Screen, triangle.B.Screen);
        AddIntersection(triangle.B.Screen, triangle.C.Screen);
        AddIntersection(triangle.C.Screen, triangle.A.Screen);
        left = minimumX;
        right = maximumX;
        return double.IsFinite(left) && double.IsFinite(right) && right >= left;

        void AddIntersection(PointF start, PointF end)
        {
            var startY = (double)start.Y;
            var endY = (double)end.Y;
            var deltaY = endY - startY;
            if (!double.IsFinite(startY)
                || !double.IsFinite(endY)
                || deltaY == 0d)
            {
                return;
            }

            var minimumY = Math.Min(startY, endY);
            var maximumY = Math.Max(startY, endY);
            if (screenY < minimumY - ProjectiveSvgRasterEdgeEpsilon
                || screenY > maximumY + ProjectiveSvgRasterEdgeEpsilon)
            {
                return;
            }

            var amount = Math.Clamp((screenY - startY) / deltaY, 0d, 1d);
            var x = (double)start.X + ((double)end.X - start.X) * amount;
            if (!double.IsFinite(x)) return;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
        }
    }

    private static bool IsTopLeftTrianglePixel(
        Reference3DProjectiveTriangle triangle,
        double area,
        PointF point)
    {
        var areaIsPositive = area > 0d;
        return IsTriangleEdgeInside(
                Edge(triangle.B.Screen, triangle.C.Screen, point),
                triangle.B.Screen,
                triangle.C.Screen,
                areaIsPositive)
            && IsTriangleEdgeInside(
                Edge(triangle.C.Screen, triangle.A.Screen, point),
                triangle.C.Screen,
                triangle.A.Screen,
                areaIsPositive)
            && IsTriangleEdgeInside(
                Edge(triangle.A.Screen, triangle.B.Screen, point),
                triangle.A.Screen,
                triangle.B.Screen,
                areaIsPositive);
    }

    private static bool IsTriangleEdgeInside(
        double edge,
        PointF start,
        PointF end,
        bool areaIsPositive)
    {
        if (areaIsPositive)
        {
            if (edge > ProjectiveSvgRasterEdgeEpsilon) return true;
            if (edge < -ProjectiveSvgRasterEdgeEpsilon) return false;
            return IsTopLeftEdge(start, end);
        }

        if (edge < -ProjectiveSvgRasterEdgeEpsilon) return true;
        if (edge > ProjectiveSvgRasterEdgeEpsilon) return false;
        return IsTopLeftEdge(end, start);
    }

    private static bool IsTopLeftEdge(PointF start, PointF end)
    {
        var dy = (double)end.Y - start.Y;
        var dx = (double)end.X - start.X;
        return dy < -ProjectiveSvgRasterEdgeEpsilon
            || Math.Abs(dy) <= ProjectiveSvgRasterEdgeEpsilon
                && dx > ProjectiveSvgRasterEdgeEpsilon;
    }

    private static double Edge(PointF a, PointF b, PointF point) =>
        ((double)b.X - a.X) * ((double)point.Y - a.Y)
        - ((double)b.Y - a.Y) * ((double)point.X - a.X);

    private static bool TrySamplePremultipliedBilinear(
        byte[] pixels,
        int stride,
        int pixelWidth,
        int pixelHeight,
        Vector2 texturePoint,
        float opacity,
        byte[] destination,
        int destinationStride,
        int destinationX,
        int destinationY)
    {
        if (!float.IsFinite(texturePoint.X)
            || !float.IsFinite(texturePoint.Y))
        {
            return false;
        }

        var sourceX = Math.Clamp(texturePoint.X - 0.5f, 0f, pixelWidth - 1f);
        var sourceY = Math.Clamp(texturePoint.Y - 0.5f, 0f, pixelHeight - 1f);
        var left = (int)MathF.Floor(sourceX);
        var top = (int)MathF.Floor(sourceY);
        var right = Math.Min(pixelWidth - 1, left + 1);
        var bottom = Math.Min(pixelHeight - 1, top + 1);
        var xAmount = sourceX - left;
        var yAmount = sourceY - top;

        var topLeft = PixelAt(pixels, stride, left, top);
        var topRight = PixelAt(pixels, stride, right, top);
        var bottomLeft = PixelAt(pixels, stride, left, bottom);
        var bottomRight = PixelAt(pixels, stride, right, bottom);
        var blue = Bilinear(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B, xAmount, yAmount);
        var green = Bilinear(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G, xAmount, yAmount);
        var red = Bilinear(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R, xAmount, yAmount);
        var alpha = Bilinear(topLeft.A, topRight.A, bottomLeft.A, bottomRight.A, xAmount, yAmount);
        var destinationOffset = checked(destinationY * destinationStride + destinationX * sizeof(int));
        destination[destinationOffset] = ScalePremultipliedChannel(blue, opacity);
        destination[destinationOffset + 1] = ScalePremultipliedChannel(green, opacity);
        destination[destinationOffset + 2] = ScalePremultipliedChannel(red, opacity);
        destination[destinationOffset + 3] = ScalePremultipliedChannel(alpha, opacity);
        return true;
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(
        byte[] pixels,
        int stride,
        int x,
        int y)
    {
        var offset = checked(y * stride + x * sizeof(int));
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static float Bilinear(
        byte topLeft,
        byte topRight,
        byte bottomLeft,
        byte bottomRight,
        float xAmount,
        float yAmount)
    {
        var top = topLeft + (topRight - topLeft) * xAmount;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * xAmount;
        return top + (bottom - top) * yAmount;
    }

    private static byte ScalePremultipliedChannel(float value, float opacity)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * opacity), 0, 255);
    }

    private static bool TryWritePremultipliedPixels(
        Bitmap bitmap,
        byte[] pixels,
        int width,
        int height)
    {
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    pixels,
                    row * width * sizeof(int),
                    IntPtr.Add(data.Scan0, row * data.Stride),
                    width * sizeof(int));
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
        }
    }

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11)
        && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M21)
        && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M31)
        && float.IsFinite(matrix.M32);

    private static bool IsFinite(RectangleF rectangle) =>
        float.IsFinite(rectangle.Left)
        && float.IsFinite(rectangle.Top)
        && float.IsFinite(rectangle.Right)
        && float.IsFinite(rectangle.Bottom);
}
