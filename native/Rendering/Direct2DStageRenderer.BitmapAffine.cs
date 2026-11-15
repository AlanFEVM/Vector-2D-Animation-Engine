using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly List<(int Index, TransformOverlayFrame Frame)> _affineBitmapLayer = [];
    // A bitmap-only layer has no stroke pass. Keep its target transform scoped
    // to the layer rather than querying/restoring COM state around every image.
    private bool TryDrawAffineBitmapLayer(StageControl stage, VectorScene scene,
        IReadOnlyList<int> objects, int start)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER") == "1"
            || start != 0 || objects.Count < 2 || stage.UsesReferenceProjection
            || stage.HasSceneCompositionMaskClips(scene)) return false;
        _affineBitmapLayer.Clear();
        foreach (var index in objects)
        {
            if (scene.ShapeKind[index] != ShapeKind.Bitmap) return false;
            if (!stage.TryGetBitmapAffineScreenFrame(scene, index, out var frame))
            {
                if (scene.TryGetObjectDistortionsView(index, out _)) return false;
                // Match the ordinary bitmap's minimum footprint and rotation.
                var center = stage.WorldToScreen(scene.X[index], scene.Y[index]);
                var width = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Width[index]));
                var height = Math.Max(0.75f, stage.WorldLengthToScreen(scene.Height[index]));
                var angle = scene.Angle[index] + stage.PlanarCanvasRotation;
                var x = new PointF(width * MathF.Cos(angle), width * MathF.Sin(angle));
                var y = new PointF(-height * MathF.Sin(angle), height * MathF.Cos(angle));
                frame = new TransformOverlayFrame(new PointF(center.X - (x.X + y.X) * 0.5f,
                    center.Y - (x.Y + y.Y) * 0.5f), x, y);
            }
            if (!stage.IsObjectHiddenForRendering(scene, index)) _affineBitmapLayer.Add((index, frame));
        }
        var saved = _target!.Transform;
        try
        {
            foreach (var (index, frame) in _affineBitmapLayer)
            {
                if (!scene.TryGetBitmapObjectData(index, out var data)
                    || !stage.TryDecodeBitmapImage(data.ImageAssetId, out var raster)) continue;
                DrawAffineBitmapCore(stage, scene, index, data,
                    BitmapImageRasterizer.ApplyObjectClip(raster, data), frame);
            }
        }
        finally { _target.Transform = saved; }
        return true;
    }

    private void DrawAffineBitmap(StageControl stage, VectorScene scene, int index,
        BitmapObjectData data, BitmapImageRaster raster, TransformOverlayFrame frame)
    {
        var saved = _target!.Transform;
        try { DrawAffineBitmapCore(stage, scene, index, data, raster, frame); }
        finally { _target.Transform = saved; }
    }

    private void DrawAffineBitmapCore(StageControl stage, VectorScene scene, int index,
        BitmapObjectData data, BitmapImageRaster raster, TransformOverlayFrame frame)
    {
        var sampled = SampleAffineBitmap(stage, data, raster, frame, out var level);
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        var bitmap = BitmapObjectBitmap(sampled, level);
        _target!.Transform = new Matrix3x2(frame.AxisX.X / sampled.PixelWidth,
            frame.AxisX.Y / sampled.PixelWidth, frame.AxisY.X / sampled.PixelHeight,
            frame.AxisY.Y / sampled.PixelHeight, frame.Origin.X, frame.Origin.Y);
        _target.DrawBitmap(bitmap, Rect(0, 0, sampled.PixelWidth, sampled.PixelHeight),
            System.Drawing.Color.FromArgb(scene.Argb[index]).A / 255f,
            nearest ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear,
            Rect(0, 0, sampled.PixelWidth, sampled.PixelHeight));
    }

    private static BitmapImageRaster SampleAffineBitmap(StageControl stage, BitmapObjectData data,
        BitmapImageRaster raster, TransformOverlayFrame frame, out int level)
    {
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        var width = MathF.Sqrt(frame.AxisX.X * frame.AxisX.X + frame.AxisX.Y * frame.AxisX.Y);
        var height = MathF.Sqrt(frame.AxisY.X * frame.AxisY.X + frame.AxisY.Y * frame.AxisY.Y);
        level = nearest ? 0 : MinificationLevel(raster, width, height);
        var sampled = level > 0 ? BitmapImageRasterizer.TryGetMinifiedRaster(raster, level) : null;
        if (sampled is null) { sampled = raster; level = 0; }
        return sampled;
    }
}
