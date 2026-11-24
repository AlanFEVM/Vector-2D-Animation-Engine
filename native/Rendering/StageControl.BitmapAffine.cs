using System.Numerics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal static bool TrySnapBlockBitmapFrame(BitmapObjectData data, TransformOverlayFrame frame,
        BitmapSampling sampling, out TransformOverlayFrame snapped)
    {
        snapped = frame;
        if (data.BlockGridKey is null || data.VisibleContours is not null || data.IsBrokenApart
            || sampling != BitmapSampling.Point || frame.AxisX.X < 1f || frame.AxisY.Y < 1f
            || MathF.Abs(frame.AxisX.Y) > .0001f || MathF.Abs(frame.AxisY.X) > .0001f) return false;
        // Round both endpoints, not the width/height. Adjacent point-sampled
        // tiles then meet on one device-pixel edge rather than blending over the canvas.
        var left = MathF.Floor(frame.Origin.X + .5f);
        var top = MathF.Floor(frame.Origin.Y + .5f);
        var right = MathF.Floor(frame.TopRight.X + .5f);
        var bottom = MathF.Floor(frame.BottomLeft.Y + .5f);
        snapped = new(new(left, top), new(right - left, 0), new(0, bottom - top));
        return true;
    }

    private static RectangleF BlockBitmapDestination(TransformOverlayFrame frame) =>
        new(frame.Origin.X, frame.Origin.Y, frame.AxisX.X, frame.AxisY.Y);

    private static void DrawSnappedBlockBitmap(Graphics target, BitmapImageRaster raster, Bitmap bitmap,
        TransformOverlayFrame frame, float opacity)
    {
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.InterpolationMode = InterpolationMode.NearestNeighbor;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        if (opacity < .999f)
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        var destination = Rectangle.Round(BlockBitmapDestination(frame));
        target.DrawImage(bitmap, destination, 0, 0, raster.PixelWidth, raster.PixelHeight,
            GraphicsUnit.Pixel, attributes);
    }

    internal bool SceneRequiresSoftwareDistortion(VectorScene? scene, bool gpuBitmaps = false)
    {
        if (scene is null) return false;
        if (UsesReferenceProjection) return SceneHasDistortionsForRendering(scene);
        if (gpuBitmaps)
        {
            if (scene.HasNonBitmapDistortions) return true;
            if (ReferenceEquals(scene, _distortPreviewScene))
                foreach (var index in _distortPreviewOverrides.Keys)
                    if (scene.ShapeKind[index] != ShapeKind.Bitmap) return true;
            return false;
        }
        // Preview overrides may replace a committed stack with an arbitrary curve.
        if (ReferenceEquals(scene, _distortPreviewScene) && _distortPreviewOverrides.Count > 0) return true;
        return scene.RequiresSoftwareDistortion;
    }

    internal bool TryGetBitmapAffineScreenFrame(VectorScene scene, int index,
        out TransformOverlayFrame frame)
    {
        frame = default;
        if (!TryGetBitmapAffineWorldCorners(scene, index, out var topLeft,
                out var topRight, out var bottomLeft, out _)) return false;
        var origin = WorldToScreen(topLeft.X, topLeft.Y);
        var right = WorldToScreen(topRight.X, topRight.Y);
        var bottom = WorldToScreen(bottomLeft.X, bottomLeft.Y);
        frame = new TransformOverlayFrame(origin,
            new PointF(right.X - origin.X, right.Y - origin.Y),
            new PointF(bottom.X - origin.X, bottom.Y - origin.Y));
        return true;
    }

    internal bool TryGetBitmapAffineWorldCorners(VectorScene scene, int index, out PointF[] corners)
    {
        corners = [];
        if (!TryGetBitmapAffineWorldCorners(scene, index, out var topLeft, out var topRight,
                out var bottomLeft, out var bottomRight)) return false;
        corners = [topLeft, topRight, bottomLeft, bottomRight];
        return true;
    }

    internal bool TryGetBitmapAffineWorldCorners(
        VectorScene scene,
        int index,
        out PointF topLeft,
        out PointF topRight,
        out PointF bottomLeft,
        out PointF bottomRight)
    {
        topLeft = topRight = bottomLeft = bottomRight = PointF.Empty;
        if (scene.ShapeKind[index] != ShapeKind.Bitmap
            || !TryGetObjectDistortionsForRendering(scene, index, out var warps)) return false;
        var transform = Matrix3x2.Identity;
        for (var i = 0; i < warps.Count; i++)
        {
            if (!warps[i].TryGetAffineTransform(out var next)) return false;
            transform *= next;
        }
        var cos = MathF.Cos(scene.Angle[index]);
        var sin = MathF.Sin(scene.Angle[index]);
        PointF Map(float x, float y)
        {
            var world = Vector2.Transform(new Vector2(scene.X[index] + x * cos - y * sin,
                scene.Y[index] + x * sin + y * cos), transform);
            return new PointF(world.X, world.Y);
        }
        topLeft = Map(-scene.Width[index] * 0.5f, -scene.Height[index] * 0.5f);
        topRight = Map(scene.Width[index] * 0.5f, -scene.Height[index] * 0.5f);
        bottomLeft = Map(-scene.Width[index] * 0.5f, scene.Height[index] * 0.5f);
        bottomRight = Map(scene.Width[index] * 0.5f, scene.Height[index] * 0.5f);
        return true;
    }

    private bool TryDrawAffineBitmap(Graphics target, VectorScene scene, int index, SceneRenderPass pass)
    {
        if (!TryGetBitmapAffineScreenFrame(scene, index, out var frame)) return false;
        if (pass != SceneRenderPass.Fill || IsObjectHiddenForRendering(scene, index)) return true;
        if (!scene.TryGetBitmapObjectData(index, out var data)
            || !TryDecodeBitmapImage(data, out var raster)) return true;
        raster = BitmapImageRasterizer.ApplyObjectClip(raster, data);
        using var bitmap = raster.AcquireBitmap();
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.Clamp);
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Color.FromArgb(scene.Argb[index]).A / 255f });
        var saved = target.Save();
        try
        {
            if (TrySnapBlockBitmapFrame(data, frame, BitmapImageSampling(data.ImageAssetId), out var snapped))
            {
                DrawSnappedBlockBitmap(target, raster, bitmap.Bitmap, snapped, Color.FromArgb(scene.Argb[index]).A / 255f);
                return true;
            }
            target.InterpolationMode = BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            target.DrawImage(bitmap.Bitmap, new[] { frame.TopLeft, frame.TopRight, frame.BottomLeft },
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight), GraphicsUnit.Pixel, attributes);
        }
        finally { target.Restore(saved); }
        return true;
    }
}
