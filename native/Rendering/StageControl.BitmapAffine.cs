using System.Numerics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal bool SceneRequiresSoftwareDistortion(VectorScene? scene)
    {
        if (scene is null) return false;
        if (UsesReferenceProjection) return SceneHasDistortionsForRendering(scene);
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
        foreach (var warp in warps)
        {
            if (!warp.TryGetAffineTransform(out var next)) return false;
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
            || !TryDecodeBitmapImage(data.ImageAssetId, out var raster)) return true;
        raster = BitmapImageRasterizer.ApplyObjectClip(raster, data);
        using var bitmap = raster.AcquireBitmap();
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.Clamp);
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Color.FromArgb(scene.Argb[index]).A / 255f });
        var saved = target.Save();
        try
        {
            target.InterpolationMode = BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            target.DrawImage(bitmap.Bitmap, new[] { frame.TopLeft, frame.TopRight, frame.BottomLeft },
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight), GraphicsUnit.Pixel, attributes);
        }
        finally { target.Restore(saved); }
        return true;
    }
}
