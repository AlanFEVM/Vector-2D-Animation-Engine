using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly List<(int Index, TransformOverlayFrame Frame)> _affineBitmapLayer = [];
    private readonly List<RectangleF> _opaqueBitmapCoverage = [];
    private readonly HashSet<int> _coveredBitmaps = [];
    // A bitmap-only layer has no stroke pass. Keep its target transform scoped
    // to the layer rather than querying/restoring COM state around every image.
    private bool TryDrawAffineBitmapLayer(StageControl stage, VectorScene scene,
        IReadOnlyList<int> objects, int start)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER") == "1"
            || start != 0 || objects.Count == 0 || stage.UsesReferenceProjection
            || stage.HasSceneCompositionMaskClips(scene)) return false;
        _affineBitmapLayer.Clear();
        foreach (var index in objects)
        {
            if (scene.ShapeKind[index] != ShapeKind.Bitmap) return false;
            // Selected objects have no occluder fading. Unselected artwork still
            // needs the ordinary masked pass while movement feedback is active.
            if (ReferenceEquals(scene, stage.Scene) && stage.SelectionMoveFeedbackActive
                && !stage.SelectionMoveOrigins.ContainsKey(index)) return false;
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
            FindCoveredBitmaps(stage, scene);
            foreach (var (index, frame) in _affineBitmapLayer)
            {
                if (_coveredBitmaps.Contains(index)) continue;
                if (!scene.TryGetBitmapObjectData(index, out var data)) continue;
                if (TryDrawCachedAffineBitmap(stage, scene, index, data, frame)) continue;
                if (!stage.TryDecodeBitmapImage(data.ImageAssetId, out var raster)) continue;
                DrawAffineBitmapCore(stage, scene, index, data,
                    BitmapImageRasterizer.ApplyObjectClip(raster, data), frame);
            }
        }
        finally { _target.Transform = saved; }
        return true;
    }

    private void FindCoveredBitmaps(StageControl stage, VectorScene scene)
    {
        _opaqueBitmapCoverage.Clear();
        _coveredBitmaps.Clear();
        if (stage.BitmapImageKeyResolver is null
            || Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION") == "1") return;
        for (var entry = _affineBitmapLayer.Count - 1; entry >= 0; entry--)
        {
            var (index, frame) = _affineBitmapLayer[entry];
            var corners = new[] { frame.Origin, new PointF(frame.Origin.X + frame.AxisX.X, frame.Origin.Y + frame.AxisX.Y),
                new PointF(frame.Origin.X + frame.AxisY.X, frame.Origin.Y + frame.AxisY.Y),
                new PointF(frame.Origin.X + frame.AxisX.X + frame.AxisY.X, frame.Origin.Y + frame.AxisX.Y + frame.AxisY.Y) };
            var bounds = RectangleF.FromLTRB(corners.Min(p => p.X), corners.Min(p => p.Y),
                corners.Max(p => p.X), corners.Max(p => p.Y));
            var footprint = RectangleF.FromLTRB(MathF.Floor(bounds.Left) - 1, MathF.Floor(bounds.Top) - 1,
                MathF.Ceiling(bounds.Right) + 1, MathF.Ceiling(bounds.Bottom) + 1);
            if (_opaqueBitmapCoverage.Any(coverage => coverage.Contains(footprint)))
            {
                _coveredBitmaps.Add(index);
                continue;
            }
            var exposed = new List<RectangleF> { footprint };
            foreach (var coverage in _opaqueBitmapCoverage)
            {
                var remaining = new List<RectangleF>();
                foreach (var rectangle in exposed)
                {
                    var overlap = RectangleF.Intersect(rectangle, coverage);
                    if (overlap.Width <= 0 || overlap.Height <= 0) { remaining.Add(rectangle); continue; }
                    if (overlap.Top > rectangle.Top) remaining.Add(RectangleF.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, overlap.Top));
                    if (overlap.Bottom < rectangle.Bottom) remaining.Add(RectangleF.FromLTRB(rectangle.Left, overlap.Bottom, rectangle.Right, rectangle.Bottom));
                    if (overlap.Left > rectangle.Left) remaining.Add(RectangleF.FromLTRB(rectangle.Left, overlap.Top, overlap.Left, overlap.Bottom));
                    if (overlap.Right < rectangle.Right) remaining.Add(RectangleF.FromLTRB(overlap.Right, overlap.Top, rectangle.Right, overlap.Bottom));
                }
                exposed = remaining;
                // Retain the complete draw if a pathological stack would create
                // too many clips. This limit never drops visible pixels.
                if (exposed.Count > 64) { exposed = [footprint]; break; }
                if (exposed.Count == 0) break;
            }
            if (exposed.Count == 0)
            {
                _coveredBitmaps.Add(index);
                continue;
            }
            // Only a proven fully opaque, unclipped, axis-aligned image can hide
            // an earlier object. Keep a conservative pixel margin for filtering.
            if (frame.AxisX.Y != 0 || frame.AxisY.X != 0 || bounds.Width <= 2 || bounds.Height <= 2
                || System.Drawing.Color.FromArgb(scene.Argb[index]).A != 255
                || !scene.TryGetBitmapObjectData(index, out var data)
                || !stage.TryGetBitmapImageKey(data.ImageAssetId, out var key)
                || BitmapImageRasterizer.ObjectClipKey(key, data) != key
                || !BitmapImageRasterizer.IsKnownOpaqueSource(key)) continue;
            var coverageBounds = RectangleF.FromLTRB(MathF.Ceiling(bounds.Left + 2), MathF.Ceiling(bounds.Top + 2),
                MathF.Floor(bounds.Right - 2), MathF.Floor(bounds.Bottom - 2));
            if (coverageBounds.Width > 0 && coverageBounds.Height > 0) _opaqueBitmapCoverage.Add(coverageBounds);
        }
    }

    // GPU textures remain useful after their full-sized CPU source is evicted.
    // Validate the source stamp/settings each paint, then reuse the exact same
    // clip and mip level without decoding pixels merely to rediscover its key.
    private bool TryDrawCachedAffineBitmap(StageControl stage, VectorScene scene, int index,
        BitmapObjectData data, TransformOverlayFrame frame)
    {
        if (!stage.TryGetBitmapImageKey(data.ImageAssetId, out var source)) return false;
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        var width = MathF.Sqrt(frame.AxisX.X * frame.AxisX.X + frame.AxisX.Y * frame.AxisX.Y);
        var height = MathF.Sqrt(frame.AxisY.X * frame.AxisY.X + frame.AxisY.Y * frame.AxisY.Y);
        var level = nearest ? 0 : MinificationLevel(source.PixelWidth, source.PixelHeight, width, height);
        var key = BitmapImageRasterizer.ObjectClipKey(source, data);
        var pixelWidth = key.PixelWidth;
        var pixelHeight = key.PixelHeight;
        for (var step = 0; step < level && (pixelWidth > 1 || pixelHeight > 1); step++)
        {
            pixelWidth = Math.Max(1, pixelWidth / 2);
            pixelHeight = Math.Max(1, pixelHeight / 2);
        }
        key = key with { PixelWidth = pixelWidth, PixelHeight = pixelHeight };
        if (!_bitmapObjectCache.TryGetValue((key, level), out var cached)) return false;
        cached.LastUse = ++_bitmapObjectCacheSequence;
        var saved = _target!.Transform;
        try
        {
            _target.Transform = new Matrix3x2(frame.AxisX.X / pixelWidth, frame.AxisX.Y / pixelWidth,
                frame.AxisY.X / pixelHeight, frame.AxisY.Y / pixelHeight, frame.Origin.X, frame.Origin.Y);
            _target.DrawBitmap(cached.Bitmap, Rect(0, 0, pixelWidth, pixelHeight),
                System.Drawing.Color.FromArgb(scene.Argb[index]).A / 255f,
                nearest ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear,
                Rect(0, 0, pixelWidth, pixelHeight));
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
