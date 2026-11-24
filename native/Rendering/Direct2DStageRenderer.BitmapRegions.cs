using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly record struct BitmapRegionKey(BitmapImageRasterKey Source, int Level, Rectangle Pixels);
    private readonly Dictionary<BitmapRegionKey, CachedImportedSvgBitmap> _bitmapRegionCache = [];
    private readonly HashSet<BitmapRegionKey> _protectedBitmapRegions = [];
    private long _bitmapRegionPersistentLimit = MaxBitmapObjectCacheBytes;
    internal long BitmapRegionDrawCalls { get; private set; }

    private bool TryDrawBitmapRegions(StageControl stage, VectorScene scene, int index,
        BitmapObjectData data, TransformOverlayFrame frame, IReadOnlyList<RectangleF> regions)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_REGIONS") == "1"
            || frame.AxisX.X == 0 || frame.AxisY.Y == 0
            || !stage.TryGetBitmapImageKey(data, out var source)) return false;
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        var level = nearest ? 0 : MinificationLevel(source.PixelWidth, source.PixelHeight,
            Math.Abs(frame.AxisX.X), Math.Abs(frame.AxisY.Y));
        var key = BitmapImageRasterizer.ObjectClipKey(source, data);
        var width = key.PixelWidth;
        var height = key.PixelHeight;
        for (var step = 0; step < level && (width > 1 || height > 1); step++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        key = key with { PixelWidth = width, PixelHeight = height };
        var transform = new Matrix3x2(frame.AxisX.X / width, 0, 0, frame.AxisY.Y / height,
            frame.Origin.X, frame.Origin.Y);
        var previousTransform = _target!.Transform;
        BitmapImageRaster? sampled = null;
        var plan = new List<(RectangleF Screen, Rectangle Pixels)>();
        var visibleRegions = regions.Select(region => RectangleF.Intersect(region,
            new RectangleF(0, 0, stage.Width, stage.Height))).Where(region => region.Width > 0 && region.Height > 0).ToArray();
        if (visibleRegions.Length == 0) return true;
        // One enclosing rectangle keeps bitmap edge coverage and translucent
        // composition identical to the original single draw.
        foreach (var region in new[] { visibleRegions.Aggregate(RectangleF.Union) })
        {
            var screen = RectangleF.Intersect(region, new RectangleF(0, 0, stage.Width, stage.Height));
            if (screen.Width <= 0 || screen.Height <= 0) continue;
            var x1 = (screen.Left - frame.Origin.X) / transform.M11;
            var x2 = (screen.Right - frame.Origin.X) / transform.M11;
            var y1 = (screen.Top - frame.Origin.Y) / transform.M22;
            var y2 = (screen.Bottom - frame.Origin.Y) / transform.M22;
            // Quantization provides headroom for fractional pointer movement.
            // Two source pixels retain all bilinear taps at the screen clip.
            const int block = 16;
            var pixels = Rectangle.FromLTRB(
                Math.Clamp((int)MathF.Floor((Math.Min(x1, x2) - 2) / block) * block, 0, width),
                Math.Clamp((int)MathF.Floor((Math.Min(y1, y2) - 2) / block) * block, 0, height),
                Math.Clamp((int)MathF.Ceiling((Math.Max(x1, x2) + 2) / block) * block, 0, width),
                Math.Clamp((int)MathF.Ceiling((Math.Max(y1, y2) + 2) / block) * block, 0, height));
            if (pixels.Width <= 0 || pixels.Height <= 0) continue;
            plan.Add((screen, pixels));
        }
        if (plan.Any(item => !_bitmapRegionCache.ContainsKey(new(key, level, item.Pixels))
            && BitmapImageRasterizer.TryGetRegionRaster(key, item.Pixels) is null))
        {
            if (!stage.TryDecodeBitmapImage(data, out var raster)) return true;
            raster = BitmapImageRasterizer.ApplyObjectClip(raster, data);
            sampled = SampleAffineBitmap(stage, data, raster, frame, out var actualLevel);
            // Decide fallback before submitting any part of a translucent object.
            if (actualLevel != level || sampled.Key != key) return false;
        }
        try
        {
            foreach (var (screen, pixels) in plan)
            {
                var regionKey = new BitmapRegionKey(key, level, pixels);
                var transient = false;
                if (!_bitmapRegionCache.TryGetValue(regionKey, out var texture))
                {
                    var regionRaster = BitmapImageRasterizer.TryGetRegionRaster(key, pixels);
                    if (sampled is null && regionRaster is null)
                    {
                        // A preceding region upload may have evicted a hit that
                        // was present during preflight. The same validated key
                        // still identifies the source needed for this upload.
                        if (!stage.TryDecodeBitmapImage(data, out var raster)) return true;
                        raster = BitmapImageRasterizer.ApplyObjectClip(raster, data);
                        sampled = SampleAffineBitmap(stage, data, raster, frame, out _);
                    }
                    var bytes = (long)pixels.Width * pixels.Height * 4;
                    transient = !TryRetainBitmapRegion(bytes);
                    if (regionRaster is null)
                        regionRaster = BitmapImageRasterizer.CacheRegionRaster(sampled!, pixels);
                    var pin = GCHandle.Alloc(regionRaster.Pixels, GCHandleType.Pinned);
                    ID2D1Bitmap bitmap;
                    try
                    {
                        bitmap = _target.CreateBitmap(new SizeI(pixels.Width, pixels.Height),
                            pin.AddrOfPinnedObject(),
                            (uint)regionRaster.Stride, new BitmapProperties(new Vortice.DCommon.PixelFormat(
                                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
                    }
                    finally { pin.Free(); }
                    texture = new(bytes, bitmap);
                    if (!transient)
                    {
                        _bitmapRegionCache.Add(regionKey, texture);
                        _bitmapObjectCacheBytes += bytes;
                    }
                }
                texture.LastUse = ++_bitmapObjectCacheSequence;
                try
                {
                    // Rectangle subtraction produces disjoint integer screen
                    // regions. Aliased clips give the same union coverage as a
                    // geometry mask without allocating an offscreen layer per
                    // photograph. Disjoint clips never blend alpha twice.
                    var clipped = visibleRegions.Length > 1;
                    for (var clip = 0; clip < (clipped ? visibleRegions.Length : 1); clip++)
                    {
                        if (clipped)
                        {
                            var rectangle = visibleRegions[clip];
                            _target.Transform = Matrix3x2.Identity;
                            _target.PushAxisAlignedClip(Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), AntialiasMode.Aliased);
                        }
                        _target.Transform = transform;
                        try
                        {
                            _target.DrawBitmap(texture.Bitmap, Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height),
                                System.Drawing.Color.FromArgb(scene.Argb[index]).A / 255f,
                                nearest ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Linear,
                                Rect(0, 0, pixels.Width, pixels.Height));
                            BitmapDrawCalls++; BitmapRegionDrawCalls++;
                        }
                        finally { if (clipped) _target.PopAxisAlignedClip(); }
                    }
                }
                finally { if (transient) texture.Dispose(); }
            }
        }
        finally { _target.Transform = previousTransform; }
        return true;
    }

    // Reserve one streaming upload, and protect textures needed later in the
    // same layer. A sequential scan larger than the budget must not evict every
    // reusable texture merely to retain the last photograph in that scan.
    private void PrepareBitmapRegionRetention(StageControl stage, VectorScene scene)
    {
        _protectedBitmapRegions.Clear();
        var largest = 0L;
        foreach (var (index, frame, warps) in _affineBitmapLayer)
        {
            if (warps is not null || _coveredBitmaps.Contains(index) || !_exposedBitmapRegions.TryGetValue(index, out var regions)
                || !scene.TryGetBitmapObjectData(index, out var data)
                || !stage.TryGetBitmapImageKey(data, out var source)) continue;
            var visible = regions.Select(region => RectangleF.Intersect(region,
                new RectangleF(0, 0, stage.Width, stage.Height))).Where(region => region.Width > 0 && region.Height > 0).ToArray();
            if (visible.Length == 0) continue;
            var level = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point ? 0
                : MinificationLevel(source.PixelWidth, source.PixelHeight, Math.Abs(frame.AxisX.X), Math.Abs(frame.AxisY.Y));
            var key = BitmapImageRasterizer.ObjectClipKey(source, data);
            var width = key.PixelWidth; var height = key.PixelHeight;
            for (var step = 0; step < level && (width > 1 || height > 1); step++)
            { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); }
            key = key with { PixelWidth = width, PixelHeight = height };
            var bounds = visible.Aggregate(RectangleF.Union);
            var x1 = (bounds.Left - frame.Origin.X) / (frame.AxisX.X / width);
            var x2 = (bounds.Right - frame.Origin.X) / (frame.AxisX.X / width);
            var y1 = (bounds.Top - frame.Origin.Y) / (frame.AxisY.Y / height);
            var y2 = (bounds.Bottom - frame.Origin.Y) / (frame.AxisY.Y / height);
            const int block = 16;
            var pixels = Rectangle.FromLTRB(
                Math.Clamp((int)MathF.Floor((Math.Min(x1, x2) - 2) / block) * block, 0, width),
                Math.Clamp((int)MathF.Floor((Math.Min(y1, y2) - 2) / block) * block, 0, height),
                Math.Clamp((int)MathF.Ceiling((Math.Max(x1, x2) + 2) / block) * block, 0, width),
                Math.Clamp((int)MathF.Ceiling((Math.Max(y1, y2) + 2) / block) * block, 0, height));
            if (pixels.Width <= 0 || pixels.Height <= 0) continue;
            _protectedBitmapRegions.Add(new(key, level, pixels));
            largest = Math.Max(largest, (long)pixels.Width * pixels.Height * 4);
        }
        largest = Math.Min(largest, MaxBitmapObjectCacheBytes - _bitmapOriginBytes);
        EvictBitmapTextures(largest);
        _bitmapRegionPersistentLimit = MaxBitmapObjectCacheBytes - _bitmapOriginBytes - largest;
    }

    private bool TryRetainBitmapRegion(long bytes)
    {
        while (_bitmapObjectCacheBytes > _bitmapRegionPersistentLimit - bytes)
        {
            var full = _bitmapObjectCache.OrderBy(entry => entry.Value.LastUse).FirstOrDefault();
            var region = _bitmapRegionCache.Where(entry => !_protectedBitmapRegions.Contains(entry.Key))
                .OrderBy(entry => entry.Value.LastUse).FirstOrDefault();
            if (full.Value is null && region.Value is null) return false;
            var evictFull = full.Value is not null
                && (region.Value is null || full.Value.LastUse <= region.Value.LastUse);
            var victim = evictFull ? full.Value! : region.Value!;
            if (evictFull) _bitmapObjectCache.Remove(full.Key);
            else _bitmapRegionCache.Remove(region.Key);
            _bitmapObjectCacheBytes -= victim.PixelBytes; victim.Dispose();
        }
        return true;
    }

    private void EvictBitmapTextures(long incomingBytes)
    {
        while (_bitmapObjectCacheBytes + _bitmapOriginBytes > MaxBitmapObjectCacheBytes - incomingBytes
            && (_bitmapObjectCache.Count > 0 || _bitmapRegionCache.Count > 0))
        {
            var full = _bitmapObjectCache.Count > 0 ? _bitmapObjectCache.MinBy(entry => entry.Value.LastUse) : default;
            var region = _bitmapRegionCache.Count > 0 ? _bitmapRegionCache.MinBy(entry => entry.Value.LastUse) : default;
            var evictFull = full.Value is not null
                && (region.Value is null || full.Value.LastUse <= region.Value.LastUse);
            var victim = evictFull ? full.Value! : region.Value!;
            if (evictFull) _bitmapObjectCache.Remove(full.Key);
            else _bitmapRegionCache.Remove(region.Key);
            _bitmapObjectCacheBytes -= victim.PixelBytes;
            victim.Dispose();
        }
    }
}
