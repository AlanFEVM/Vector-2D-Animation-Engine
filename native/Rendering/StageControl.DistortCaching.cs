﻿using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const long DistortResultCacheBudgetBytes = 64L * 1024 * 1024;
    private const int DistortResultCacheMaximumEntries = 4096;
    private const int DistortBoundaryCacheMaximumEntries = 4096;
    private const long DistortBoundaryCacheBudgetBytes = 16L * 1024 * 1024;
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), DistortResultCacheEntry> _distortResultCache = new();
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), DistortBoundaryCacheEntry> _distortBoundaryCache = new();
    private long _distortResultCacheBytes;
    private long _distortResultCacheSequence;
    private long _distortBoundaryCacheBytes;
    private readonly Dictionary<(VectorScene Scene, int ObjectIndex), BitmapPreviewRasterEntry> _bitmapPreviewRasters = new();
    private long _bitmapPreviewRasterBytes;
    private PendingDistortBake? _pendingDistortBake;
    private System.Windows.Forms.Timer? _distortBakeTimer;
    private long _distortBakeGeneration;

    private sealed record PendingDistortBake((VectorScene Scene, int ObjectIndex) Key,
        DistortResultState State, long Generation, CancellationTokenSource Cancellation)
    {
        internal Task<(BitmapDistortRaster? Result, bool Completed)>? Task { get; set; }
    }

    internal int LastGdiDistortResultBuilds { get; private set; }
    internal int LastGdiDistortResultReuses { get; private set; }
    internal long DistortMeshBuildCount { get; private set; }
    internal long DistortResultBakeAttemptCount { get; private set; }
    internal long DistortResultBakeCompletedCount { get; private set; }
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

    private sealed record DistortBoundaryCacheEntry(DistortObjectGeometryState State, PointF[][] Contours, long Bytes)
    {
        internal long LastUse { get; set; }
    }

    private sealed record BitmapPreviewRasterEntry(DistortResultState State, float Density, RectangleF View,
        BitmapDistortRaster Result)
    {
        internal long LastUse { get; set; }
        internal long Bytes => Result.Raster.Pixels.LongLength;
    }

    private bool TryDrawBitmapDistortionPreview(Graphics target, VectorScene scene, int index,
        SceneRenderPass pass, IReadOnlyList<DistortWarp> distortions)
    {
        if (scene.ShapeKind[index] != ShapeKind.Bitmap || pass != SceneRenderPass.Fill) return false;
        if (IsObjectHiddenForRendering(scene, index)) return true;
        if (!scene.TryGetBitmapObjectData(index, out var data)
            || !TryDecodeBitmapImage(data, out var source)) return false;
        var density = WorldLengthToScreen(1);
        var view = VisibleWorldBounds();
        var sampling = BitmapImageSampling(data.ImageAssetId);
        var state = new DistortResultState(GetDistortObjectGeometryState(scene, index, distortions), source.Key, sampling);
        var key = (scene, index);
        if (!_bitmapPreviewRasters.TryGetValue(key, out var entry) || entry.State != state || entry.Density != density
            || entry.View != view)
        {
            // This UI-only buffer is never uploaded as a shared asset or handed
            // to native/background bakes. Remove its old state before mutation.
            var reusable = entry?.Result.Raster;
            if (_bitmapPreviewRasters.Remove(key, out var previous)) _bitmapPreviewRasterBytes -= previous.Bytes;
            // Map original pixels directly at current screen precision. Shared-edge
            // coverage is resolved once, without thousands of GDI triangle clips.
            source = BitmapImageRasterizer.ApplyObjectClip(source, data);
            var result = BitmapDistortRasterizer.Rasterize(source, scene.Width[index], scene.Height[index],
                scene.X[index], scene.Y[index], scene.Angle[index], distortions, sampling,
                DistortBoundaryCacheBudgetBytes, viewportDensity: density, viewportBounds: view,
                reusablePreviewRaster: reusable);
            if (result is null) return false;
            entry = new BitmapPreviewRasterEntry(state, density, view, result);
            while (_bitmapPreviewRasters.Count > 0 && (_bitmapPreviewRasters.Count >= DistortBoundaryCacheMaximumEntries
                || _bitmapPreviewRasterBytes + entry.Bytes > DistortBoundaryCacheBudgetBytes))
            {
                var oldest = _bitmapPreviewRasters.MinBy(item => item.Value.LastUse);
                _bitmapPreviewRasters.Remove(oldest.Key);
                _bitmapPreviewRasterBytes -= oldest.Value.Bytes;
            }
            _bitmapPreviewRasters[key] = entry;
            _bitmapPreviewRasterBytes += entry.Bytes;
            DistortMeshBuildCount++;
        }
        entry.LastUse = ++_distortResultCacheSequence;
        DrawBitmapDistortResult(target, entry.Result, sampling, Color.FromArgb(scene.Argb[index]).A / 255f);
        return true;
    }

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
        if (ReferenceEquals(scene, _distortPreviewScene) && _distortPreviewOverrides.ContainsKey(objectIndex)) return false;
        if (!scene.TryGetBitmapObjectData(objectIndex, out var data)
            || !TryDecodeBitmapImage(data, out var source)) return false;
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
        else if (Capture && !PlaybackActive)
        {
            // A captured pointer is editing this image's geometry (skew, move, scale).
            // Native-pixel baking costs far more than a frame, so let the viewport
            // envelope preview draw the in-progress state; pointer release restores
            // the committed native result. Camera navigation keeps hitting the cache
            // above because it never changes this geometry state.
            return false;
        }
        else
        {
            // Large native-pixel warps must not freeze the first UI frame after
            // release. Offscreen/export draws retain synchronous final output.
            if (_paintInProgress && !_renderingCanvasOutput && !PlaybackActive && IsHandleCreated && Parent is not null
                && (long)source.PixelWidth * source.PixelHeight >= 256 * 256)
            {
                QueueBitmapDistortion(key, state, source, data);
                return false;
            }
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
            DistortResultBakeCompletedCount++;
            LastGdiDistortResultBuilds++;
        }
        // No camera, viewport, backdrop, mask or pointer-quality state participates
        // in the bake. All of those are handled by this ordinary image draw.
        DrawBitmapDistortResult(target, result, sampling, Color.FromArgb(scene.Argb[objectIndex]).A / 255f);
        return true;
    }

    private void QueueBitmapDistortion((VectorScene Scene, int ObjectIndex) key,
        DistortResultState state, BitmapImageRaster source, BitmapObjectData data)
    {
        if (_pendingDistortBake is { } pending)
        {
            if (pending.Key == key && pending.State != state) pending.Cancellation.Cancel();
            return;
        }
        var work = new PendingDistortBake(key, state, _distortBakeGeneration, new CancellationTokenSource());
        _pendingDistortBake = work;
        DistortResultBakeAttemptCount++;
        work.Task = Task.Run(() =>
        {
            BitmapDistortRaster? result = null;
            var completed = false;
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                var geometry = state.Geometry;
                var clipped = BitmapImageRasterizer.ApplyObjectClip(source, data);
                result = BitmapDistortRasterizer.Rasterize(clipped, geometry.Width, geometry.Height,
                    geometry.X, geometry.Y, geometry.Angle, geometry.Distortions, state.Sampling,
                    DistortResultCacheBudgetBytes, work.Cancellation.Token);
                completed = !work.Cancellation.IsCancellationRequested;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException)
            {
                AppLog.Info($"Background bitmap distortion could not allocate its result: {error.Message}");
                completed = !work.Cancellation.IsCancellationRequested;
            }
            return (result, completed);
        });
        // Poll completion through the UI message loop: no worker depends on a
        // mutable SynchronizationContext or the Stage's recreatable HWND.
        if (_distortBakeTimer is null)
        {
            _distortBakeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _distortBakeTimer.Tick += (_, _) => PollBitmapDistortion();
        }
        _distortBakeTimer.Start();
    }

    private void PollBitmapDistortion()
    {
        if (_pendingDistortBake is not { Task.IsCompleted: true } work) return;
        _distortBakeTimer?.Stop();
        if (work.Task.IsCompletedSuccessfully)
        {
            var result = work.Task.Result;
            CompleteBitmapDistortion(work, result.Result, result.Completed);
        }
        else
        {
            AppLog.Error("Background bitmap distortion failed", work.Task.Exception!);
            CompleteBitmapDistortion(work, null, completed: true);
        }
    }

    private void CompleteBitmapDistortion(PendingDistortBake work, BitmapDistortRaster? result, bool completed)
    {
        if (!ReferenceEquals(_pendingDistortBake, work)) { work.Cancellation.Dispose(); return; }
        _pendingDistortBake = null;
        var current = completed && !work.Cancellation.IsCancellationRequested && !_disposingResources
            && work.Generation == _distortBakeGeneration;
        work.Cancellation.Dispose();
        var (scene, index) = work.Key;
        if (current && scene.TryGetObjectDistortionsView(index, out var distortions)
            && GetDistortObjectGeometryState(scene, index, distortions) == work.State.Geometry
            && scene.TryGetBitmapObjectData(index, out var data)
            && TryDecodeBitmapImage(data, out var source) && source.Key == work.State.Image
            && BitmapImageSampling(data.ImageAssetId) == work.State.Sampling)
        {
            StoreDistortResult(work.Key, work.State, result);
            if (result is not null)
            {
                DistortMeshBuildCount++;
                DistortResultBakeCompletedCount++;
            }
        }
        if (!_disposingResources && !IsDisposed) RequestStageFrame(basePresentationChanged: true);
    }

    private void DrawBitmapDistortResult(Graphics target, BitmapDistortRaster result, BitmapSampling sampling, float opacity)
    {
        var bounds = result.WorldBounds;
        var destination = new[] { WorldToScreen(bounds.Left, bounds.Top), WorldToScreen(bounds.Right, bounds.Top),
            WorldToScreen(bounds.Left, bounds.Bottom) };
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
                destination,
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
        _distortBakeGeneration++;
        _pendingDistortBake?.Cancellation.Cancel();
        if (_disposingResources)
        {
            _distortBakeTimer?.Dispose();
            _distortBakeTimer = null;
            if (_pendingDistortBake is { } retired)
            {
                _pendingDistortBake = null;
                _ = retired.Task!.ContinueWith(task =>
                {
                    _ = task.Exception;
                    retired.Cancellation.Dispose();
                }, TaskScheduler.Default);
            }
        }
        _distortResultCache.Clear();
        _distortResultCacheBytes = 0;
        _distortBoundaryCache.Clear();
        _distortBoundaryCacheBytes = 0;
        _bitmapPreviewRasters.Clear();
        _bitmapPreviewRasterBytes = 0;
    }

    private PointF[][] GetCachedDistortedBoundaryContours(
        VectorScene scene, int objectIndex, IReadOnlyList<DistortWarp> distortions)
    {
        var key = (scene, objectIndex);
        var state = GetDistortObjectGeometryState(scene, objectIndex, distortions);
        if (_distortBoundaryCache.TryGetValue(key, out var cached) && cached.State == state)
        {
            cached.LastUse = ++_distortResultCacheSequence;
            return cached.Contours;
        }
        var contours = scene.GetDistortedObjectBoundaryContours(objectIndex, distortions);
        DistortBoundaryBuildCount++;
        var bytes = contours.Sum(contour => (long)contour.Length * 8 + 32) + 128;
        if (bytes > DistortBoundaryCacheBudgetBytes) return contours;
        if (_distortBoundaryCache.Remove(key, out var previous)) _distortBoundaryCacheBytes -= previous.Bytes;
        while (_distortBoundaryCache.Count > 0
            && (_distortBoundaryCache.Count >= DistortBoundaryCacheMaximumEntries
                || _distortBoundaryCacheBytes + bytes > DistortBoundaryCacheBudgetBytes))
        {
            var oldest = _distortBoundaryCache.MinBy(entry => entry.Value.LastUse);
            _distortBoundaryCache.Remove(oldest.Key);
            _distortBoundaryCacheBytes -= oldest.Value.Bytes;
        }
        _distortBoundaryCache[key] = new DistortBoundaryCacheEntry(state, contours, bytes)
        { LastUse = ++_distortResultCacheSequence };
        _distortBoundaryCacheBytes += bytes;
        return contours;
    }
}
