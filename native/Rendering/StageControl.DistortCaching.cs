using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const long DistortResultCacheBudgetBytes = 64L * 1024 * 1024;
    private const int DistortResultCacheMaximumEntries = 32;
    private const int DistortBoundaryCacheMaximumEntries = 512;
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), DistortResultCacheEntry> _distortResultCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), DistortBoundaryCacheEntry> _distortBoundaryCache = new();
    private long _distortResultCacheBytes;
    private long _distortResultCacheSequence;

    internal int LastGdiDistortResultBuilds { get; private set; }
    internal int LastGdiDistortResultReuses { get; private set; }
    internal long DistortMeshBuildCount { get; private set; }
    internal long DistortResultBakeAttemptCount { get; private set; }
    internal long DistortBoundaryBuildCount { get; private set; }

    // Actual object inputs, rather than scene/UI revisions, let unrelated edits and
    // camera navigation keep an unchanged image's native-pixel distortion result.
    private readonly record struct DistortObjectGeometryState(
        ShapeKind Shape, long Revision, BitmapObjectData? BitmapData,
        float X, float Y, float Width, float Height, float Angle, float Stroke,
        IReadOnlyList<DistortWarp> Distortions);

    private readonly record struct DistortResultState(
        DistortObjectGeometryState Geometry, BitmapImageRasterKey Image, BitmapSampling Sampling);

    private sealed record DistortResultCacheEntry(DistortResultState State, BitmapDistortRaster? Result)
    {
        internal long LastUse { get; set; }
    }

    private sealed record DistortBoundaryCacheEntry(DistortObjectGeometryState State, PointF[][] Contours);

    private static DistortObjectGeometryState GetDistortObjectGeometryState(
        VectorScene scene, int objectIndex, IReadOnlyList<DistortWarp> distortions)
    {
        var bitmap = scene.TryGetBitmapObjectData(objectIndex, out var data) ? data : null;
        return new DistortObjectGeometryState(
            scene.ShapeKind[objectIndex], bitmap is null ? scene.GeometryRevision : 0, bitmap,
            scene.X[objectIndex], scene.Y[objectIndex], scene.Width[objectIndex], scene.Height[objectIndex],
            scene.Angle[objectIndex], scene.Stroke[objectIndex], distortions);
    }

    private bool TryDrawCachedBitmapDistortion(
        Graphics target, VectorScene scene, int objectIndex, SceneRenderPass pass,
        IReadOnlyList<DistortWarp> distortions)
    {
        if (pass != SceneRenderPass.Fill || scene.ShapeKind[objectIndex] != ShapeKind.Bitmap
            || IsObjectHiddenForRendering(scene, objectIndex)) return false;
        // Only actively edited envelopes use the established lightweight preview.
        // Capture and wheel-zoom state must never bypass committed native results.
        if (ReferenceEquals(scene, _distortPreviewScene) && _distortPreviewOverrides.ContainsKey(objectIndex)) return false;
        if (!scene.TryGetBitmapObjectData(objectIndex, out var data)
            || !TryDecodeBitmapImage(data.ImageAssetId, out var source)) return false;
        var sampling = BitmapImageSampling(data.ImageAssetId);
        var key = (scene, objectIndex);
        var state = new DistortResultState(GetDistortObjectGeometryState(scene, objectIndex, distortions), source.Key, sampling);
        BitmapDistortRaster? result;
        if (_distortResultCache.TryGetValue(key, out var cached) && cached.State == state)
        {
            cached.LastUse = ++_distortResultCacheSequence;
            result = cached.Result;
            if (result is null) return false;
            LastGdiDistortResultReuses++;
        }
        else
        {
            try
            {
                source = BitmapImageRasterizer.ApplyObjectClip(source, data);
                DistortResultBakeAttemptCount++;
                result = BitmapDistortRasterizer.Rasterize(source, scene, objectIndex, distortions, sampling,
                    DistortResultCacheBudgetBytes);
                // Remember deterministic size/precision rejection as well, so camera
                // navigation does not retry an expensive native bake before fallback.
                StoreDistortResult(key, state, result);
                if (result is null) return false;
            }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException)
            {
                // Allocation/budget failures leave the original renderer available.
                return false;
            }
            DistortMeshBuildCount++;
            LastGdiDistortResultBuilds++;
        }
        // No camera, viewport, backdrop, mask or pointer-quality state participates
        // in the bake. All of those are handled by this ordinary image draw.
        DrawBitmapDistortResult(target, result, sampling, Color.FromArgb(scene.Argb[objectIndex]).A / 255f);
        return true;
    }

    private void DrawBitmapDistortResult(Graphics target, BitmapDistortRaster result, BitmapSampling sampling, float opacity)
    {
        var bounds = result.WorldBounds;
        var first = WorldToScreen(bounds.Left, bounds.Top);
        var last = WorldToScreen(bounds.Right, bounds.Bottom);
        var destination = new RectangleF(first.X, first.Y, last.X - first.X, last.Y - first.Y);
        using var bitmap = result.Raster.AcquireBitmap();
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.Clamp);
        if (opacity < 0.999f) attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0, 1) });
        var saved = target.Save();
        try
        {
            target.CompositingMode = CompositingMode.SourceOver;
            target.CompositingQuality = CompositingQuality.HighQuality;
            target.PixelOffsetMode = PixelOffsetMode.HighQuality;
            target.InterpolationMode = sampling == BitmapSampling.Point
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            target.DrawImage(bitmap.Bitmap,
                [new PointF(destination.Left, destination.Top), new PointF(destination.Right, destination.Top),
                    new PointF(destination.Left, destination.Bottom)],
                new RectangleF(0, 0, result.Raster.PixelWidth, result.Raster.PixelHeight), GraphicsUnit.Pixel, attributes);
        }
        finally { target.Restore(saved); }
    }

    private void StoreDistortResult(
        (VectorScene Scene, int ObjectIndex) key, DistortResultState state, BitmapDistortRaster? result)
    {
        var bytes = result?.Raster.Pixels.LongLength ?? 0;
        if (bytes > DistortResultCacheBudgetBytes) throw new ArgumentException("Distort result exceeds its cache budget.");
        if (_distortResultCache.Remove(key, out var previous)) _distortResultCacheBytes -= previous.Result?.Raster.Pixels.LongLength ?? 0;
        while (_distortResultCache.Count > 0
            && (_distortResultCacheBytes + bytes > DistortResultCacheBudgetBytes
                || _distortResultCache.Count >= DistortResultCacheMaximumEntries))
        {
            var oldest = _distortResultCache.MinBy(entry => entry.Value.LastUse);
            _distortResultCache.Remove(oldest.Key);
            _distortResultCacheBytes -= oldest.Value.Result?.Raster.Pixels.LongLength ?? 0;
        }
        _distortResultCache[key] = new DistortResultCacheEntry(state, result) { LastUse = ++_distortResultCacheSequence };
        _distortResultCacheBytes += bytes;
    }

    private void ClearDistortResultCache()
    {
        _distortResultCache.Clear();
        _distortResultCacheBytes = 0;
        _distortBoundaryCache.Clear();
    }

    private PointF[][] GetCachedDistortedBoundaryContours(
        VectorScene scene, int objectIndex, IReadOnlyList<DistortWarp> distortions)
    {
        var key = (scene, objectIndex);
        var state = GetDistortObjectGeometryState(scene, objectIndex, distortions);
        if (_distortBoundaryCache.TryGetValue(key, out var cached) && cached.State == state) return cached.Contours;
        var contours = scene.GetDistortedObjectBoundaryContours(objectIndex, distortions);
        DistortBoundaryBuildCount++;
        if (_distortBoundaryCache.Count >= DistortBoundaryCacheMaximumEntries) _distortBoundaryCache.Clear();
        _distortBoundaryCache[key] = new DistortBoundaryCacheEntry(state, contours);
        return contours;
    }
}
