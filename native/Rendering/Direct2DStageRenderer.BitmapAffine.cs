using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly List<(int Index, TransformOverlayFrame Frame, IReadOnlyList<DistortWarp>? Warps)> _affineBitmapLayer = [];
    private readonly List<RectangleF> _opaqueBitmapCoverage = [];
    private readonly HashSet<int> _coveredBitmaps = [];
    private readonly Dictionary<int, List<RectangleF>> _exposedBitmapRegions = [];
    private List<RectangleF> _bitmapCoverageRemaining = [];
    private List<RectangleF> _bitmapCoverageScratch = [];
    private readonly List<List<RectangleF>> _bitmapExposedRegionPool = [];
    internal double DiagnosticBitmapCoverageMilliseconds { get; private set; }
    internal long BitmapDrawCalls { get; private set; }
    internal long BitmapOcclusionCulls { get; private set; }
    // A bitmap-only layer has no stroke pass. Keep its target transform scoped
    // to the layer rather than querying/restoring COM state around every image.
    private bool TryDrawAffineBitmapLayer(StageControl stage, VectorScene scene,
        IReadOnlyList<int> objects, int start, bool allowGpuWarp = false)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER") == "1"
            || start != 0 || objects.Count == 0 || stage.UsesReferenceProjection
            || stage.HasSceneCompositionMaskClips(scene)) return false;
        _affineBitmapLayer.Clear();
        var gpuWarp = allowGpuWarp && _bitmapWarp is not null && !_bitmapWarpUnavailable
            && !_capturingShotPreview && !stage.SelectionMoveFeedbackActive && IsBitmapWarpEditing(stage);
        foreach (var index in objects)
        {
            if (scene.ShapeKind[index] != ShapeKind.Bitmap) return false;
            // Selected objects have no occluder fading. Unselected artwork still
            // needs the ordinary masked pass while movement feedback is active.
            if (ReferenceEquals(scene, stage.Scene) && stage.SelectionMoveFeedbackActive
                && !stage.SelectionMoveOrigins.ContainsKey(index)) return false;
            if (!stage.TryGetBitmapAffineScreenFrame(scene, index, out var frame))
            {
                if (stage.TryGetObjectDistortionsForRendering(scene, index, out var warps))
                {
                    if (!gpuWarp) return false;
                    if (!stage.IsObjectHiddenForRendering(scene, index)) _affineBitmapLayer.Add((index, default, warps));
                    continue;
                }
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
            if (!stage.IsObjectHiddenForRendering(scene, index))
            {
                if (scene.TryGetBitmapObjectData(index, out var data) && data.BlockGridKey is not null)
                    StageControl.TrySnapBlockBitmapFrame(data, frame, stage.BitmapImageSampling(data.ImageAssetId), out frame);
                _affineBitmapLayer.Add((index, frame, null));
            }
        }
        var saved = _target!.Transform;
        try
        {
            var coverageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            FindCoveredBitmaps(stage, scene);
            DiagnosticBitmapCoverageMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(coverageStarted).TotalMilliseconds;
            PrepareBitmapRegionRetention(stage, scene);
            foreach (var (index, frame, warps) in _affineBitmapLayer)
            {
                if (warps is not null)
                {
                    DrawGpuBitmapWarp(stage, scene, index, warps);
                    continue;
                }
                if (_coveredBitmaps.Contains(index)) { BitmapOcclusionCulls++; continue; }
                if (!scene.TryGetBitmapObjectData(index, out var data)) continue;
                if (_exposedBitmapRegions.TryGetValue(index, out var regions)
                    && TryDrawBitmapRegions(stage, scene, index, data, frame, regions)) continue;
                if (TryDrawCachedAffineBitmap(stage, scene, index, data, frame)) continue;
                if (!stage.TryDecodeBitmapImage(data, out var raster)) continue;
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
        _exposedBitmapRegions.Clear();
        if (stage.BitmapImageKeyResolver is null
            || Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_OCCLUSION") == "1") return;
        var regionSlot = 0;
        for (var entry = _affineBitmapLayer.Count - 1; entry >= 0; entry--)
        {
            var (index, frame, warps) = _affineBitmapLayer[entry];
            // A curved image has no proven opaque rectangular footprint.
            if (warps is not null) continue;
            var bounds = frame.Bounds;
            var projectionMargin = _capturingShotPreview ? 1 : 0;
            var footprint = RectangleF.FromLTRB(MathF.Floor(bounds.Left) - projectionMargin, MathF.Floor(bounds.Top) - projectionMargin,
                MathF.Ceiling(bounds.Right) + projectionMargin, MathF.Ceiling(bounds.Bottom) + projectionMargin);
            // Visibility is a property of this render target. Outside portions
            // must not fragment the exposed set or prevent a visible occluder
            // from covering a photograph whose full bounds extend off screen.
            // Shot command lists receive a later projection, so retain their
            // original conservative bounds until that projection is applied.
            if (!_capturingShotPreview)
            {
                footprint = RectangleF.Intersect(footprint, new RectangleF(0, 0, stage.Width, stage.Height));
                if (footprint.Width <= 0 || footprint.Height <= 0)
                { _coveredBitmaps.Add(index); continue; }
            }
            if (_opaqueBitmapCoverage.Any(coverage => coverage.Contains(footprint)))
            {
                _coveredBitmaps.Add(index);
                continue;
            }
            var exposed = _bitmapCoverageRemaining;
            var remaining = _bitmapCoverageScratch;
            exposed.Clear(); exposed.Add(footprint);
            foreach (var coverage in _opaqueBitmapCoverage)
            {
                // Most prior rectangles miss this image. Keep the exact same
                // subtraction without allocating/copying a list for every miss.
                if (!coverage.IntersectsWith(footprint)) continue;
                remaining.Clear();
                foreach (var rectangle in exposed)
                {
                    var overlap = RectangleF.Intersect(rectangle, coverage);
                    if (overlap.Width <= 0 || overlap.Height <= 0) { remaining.Add(rectangle); continue; }
                    if (overlap.Top > rectangle.Top) remaining.Add(RectangleF.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, overlap.Top));
                    if (overlap.Bottom < rectangle.Bottom) remaining.Add(RectangleF.FromLTRB(rectangle.Left, overlap.Bottom, rectangle.Right, rectangle.Bottom));
                    if (overlap.Left > rectangle.Left) remaining.Add(RectangleF.FromLTRB(rectangle.Left, overlap.Top, overlap.Left, overlap.Bottom));
                    if (overlap.Right < rectangle.Right) remaining.Add(RectangleF.FromLTRB(overlap.Right, overlap.Top, rectangle.Right, overlap.Bottom));
                }
                (exposed, remaining) = (remaining, exposed);
                // Retain the complete draw if a pathological stack would create
                // too many clips. This limit never drops visible pixels.
                if (exposed.Count > 64) { exposed.Clear(); exposed.Add(footprint); break; }
                if (exposed.Count == 0) break;
            }
            if (exposed.Count == 0)
            {
                _coveredBitmaps.Add(index);
                continue;
            }
            // Small exposed strips should not retain/upload a whole photograph.
            // Integer screen clips and padded source regions preserve sampling.
            if (frame.AxisX.Y == 0 && frame.AxisY.X == 0 && exposed.Count <= 64)
            {
                if (regionSlot == _bitmapExposedRegionPool.Count) _bitmapExposedRegionPool.Add([]);
                var regions = _bitmapExposedRegionPool[regionSlot++];
                regions.Clear(); regions.AddRange(exposed);
                _exposedBitmapRegions[index] = regions;
            }
            // Only a proven fully opaque, unclipped, axis-aligned image can hide
            // an earlier object. Keep a conservative pixel margin for filtering.
            if (frame.AxisX.Y != 0 || frame.AxisY.X != 0 || bounds.Width <= 2 || bounds.Height <= 2
                || System.Drawing.Color.FromArgb(scene.Argb[index]).A != 255
                || !scene.TryGetBitmapObjectData(index, out var data)
                || !stage.TryGetBitmapImageKey(data.ImageAssetId, out var key)
                || BitmapImageRasterizer.ObjectClipKey(key, data) != key
                || !stage.IsBitmapImageKnownOpaque(data.ImageAssetId, key)) continue;
            // Whole device pixels inside an opaque level-zero rectangle are
            // fully covered. Bicubic mip edges may contain fractional alpha,
            // so those retain the original conservative two-pixel margin.
            var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
            var margin = !_capturingShotPreview && (nearest || MinificationLevel(key.PixelWidth, key.PixelHeight,
                Math.Abs(frame.AxisX.X), Math.Abs(frame.AxisY.Y)) == 0) ? 0 : 2;
            var coverageBounds = RectangleF.FromLTRB(MathF.Ceiling(bounds.Left + margin), MathF.Ceiling(bounds.Top + margin),
                MathF.Floor(bounds.Right - margin), MathF.Floor(bounds.Bottom - margin));
            if (coverageBounds.Width > 0 && coverageBounds.Height > 0) _opaqueBitmapCoverage.Add(coverageBounds);
        }
    }

    // GPU textures remain useful after their full-sized CPU source is evicted.
    // Validate the source stamp/settings each paint, then reuse the exact same
    // clip and mip level without decoding pixels merely to rediscover its key.
    private bool TryDrawCachedAffineBitmap(StageControl stage, VectorScene scene, int index,
        BitmapObjectData data, TransformOverlayFrame frame)
    {
        if (!stage.TryGetBitmapImageKey(data, out var source)) return false;
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        if (data.BlockGridKey is not null)
            StageControl.TrySnapBlockBitmapFrame(data, frame, nearest ? BitmapSampling.Point : BitmapSampling.Linear, out frame);
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
            BitmapDrawCalls++;
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
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        if (data.BlockGridKey is not null)
            StageControl.TrySnapBlockBitmapFrame(data, frame, nearest ? BitmapSampling.Point : BitmapSampling.Linear, out frame);
        var sampled = SampleAffineBitmap(stage, data, raster, frame, out var level);
        var bitmap = BitmapObjectBitmap(sampled, level);
        _target!.Transform = new Matrix3x2(frame.AxisX.X / sampled.PixelWidth,
            frame.AxisX.Y / sampled.PixelWidth, frame.AxisY.X / sampled.PixelHeight,
            frame.AxisY.Y / sampled.PixelHeight, frame.Origin.X, frame.Origin.Y);
        _target.DrawBitmap(bitmap, Rect(0, 0, sampled.PixelWidth, sampled.PixelHeight),
            System.Drawing.Color.FromArgb(scene.Argb[index]).A / 255f,
            nearest ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear,
            Rect(0, 0, sampled.PixelWidth, sampled.PixelHeight));
        BitmapDrawCalls++;
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
