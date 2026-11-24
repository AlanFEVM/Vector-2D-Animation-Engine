using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VectorAnimationEngine;

/// <summary>
/// Cache key for a decoded bitmap. The content hash makes an edited or replaced asset
/// produce a new key instead of resurrecting a stale decode.
/// </summary>
internal readonly record struct BitmapImageRasterKey(string ContentSha256, int PixelWidth, int PixelHeight)
{
    public uint ShadowCorners { get; init; }
    public uint WarmLightCorners { get; init; }
    public int? BiomeTintArgb { get; init; }
    public bool BiomeTintGrassSide { get; init; }
}

/// <summary>
/// Fully decoded, premultiplied 32bpp image ready for upload to either renderer.
/// </summary>
internal sealed class BitmapImageRaster
{
    public BitmapImageRaster(BitmapImageRasterKey key)
    {
        Key = key;
        Stride = checked(key.PixelWidth * 4);
        Pixels = new byte[checked(Stride * key.PixelHeight)];
    }

    public BitmapImageRasterKey Key { get; }
    public int PixelWidth => Key.PixelWidth;
    public int PixelHeight => Key.PixelHeight;
    public int Stride { get; }
    /// <summary>Premultiplied BGRA (Format32bppPArgb) pixel bytes.</summary>
    public byte[] Pixels { get; }

    internal BitmapImageBitmapLease AcquireBitmap() => new(this);
}

/// <summary>
/// Wraps the decoded pixel buffer as a GDI+ bitmap without copying, for the GDI renderer.
/// </summary>
internal sealed class BitmapImageBitmapLease : IDisposable
{
    private GCHandle _pixelsHandle;

    internal BitmapImageBitmapLease(BitmapImageRaster raster)
    {
        _pixelsHandle = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        try
        {
            Bitmap = new Bitmap(
                raster.PixelWidth,
                raster.PixelHeight,
                raster.Stride,
                PixelFormat.Format32bppPArgb,
                _pixelsHandle.AddrOfPinnedObject());
        }
        catch
        {
            _pixelsHandle.Free();
            throw;
        }
    }

    internal Bitmap Bitmap { get; }

    public void Dispose()
    {
        try
        {
            Bitmap.Dispose();
        }
        finally
        {
            if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
        }
    }
}

/// <summary>
/// Decodes managed image assets and applies the import filter mode. Mirrors
/// <see cref="ImportedSvgRasterizer"/>'s cache policy so bitmap and SVG objects share the
/// same memory behaviour: bounded entry and byte budgets, content-hash keys, and reuse of
/// a decoded raster for the same content at any size.
/// </summary>
internal static class BitmapImageRasterizer
{
    private const int MaxCacheEntries = 512;
    private const long MaxCacheBytes = 512L * 1024 * 1024;
    private const int MaxSourceBytes = (int)BitmapImageFormats.MaximumSourceBytes;

    private static readonly Dictionary<BitmapImageRasterKey, CacheEntry> RasterCache = new();
    private static readonly Dictionary<SourceCacheKey, SourceCacheEntry> SourceCache = new();
    // Keep only small validation records with their live project asset. Pixel
    // eviction must not discard the identity of an uploaded GPU texture, and
    // closing/replacing a project must not keep its asset definitions alive.
    private static readonly ConditionalWeakTable<ImageAssetDefinition, AssetSourceProof> AssetSources = new();
    private static readonly object CacheSync = new();
    private static long _cacheBytes;
    private static long _useSequence;
    private static long _decodeCallCount;
    private static long _sourceReadCount;
    private static long _cacheHitCount;
    private static long _cacheMissCount;
    private static long _cacheEvictionCount;
    private static readonly ConditionalWeakTable<BitmapObjectData, string> ClipKeys = new();
    private static readonly HashSet<BitmapImageRasterKey> OpaqueSources = [];

    internal static bool IsKnownOpaqueSource(BitmapImageRasterKey key, ImageAssetDefinition? asset = null)
    {
        lock (CacheSync)
            return asset is not null && AssetSources.TryGetValue(asset, out var proof)
                ? proof.Entry.RasterKey == key && proof.Entry.Opaque
                : OpaqueSources.Contains(key);
    }

    internal static bool TryGetSourceKey(FileInfo file, BitmapImageImportSettings settings,
        out BitmapImageRasterKey key, ImageAssetDefinition? asset = null)
    {
        key = default;
        if (!settings.IsValid || !file.Exists) return false;
        var stamp = new SourceStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        var sourceKey = new SourceCacheKey(file.FullName.ToUpperInvariant(), settings);
        lock (CacheSync)
        {
            if (asset is not null && AssetSources.TryGetValue(asset, out var proof)
                && proof.SourceKey == sourceKey && proof.Entry.Stamp == stamp)
            {
                key = proof.Entry.RasterKey;
                return true;
            }
            if (!SourceCache.TryGetValue(sourceKey, out var source)
                || source.Stamp != stamp) return false;
            if (asset is not null)
            {
                AssetSources.Remove(asset);
                AssetSources.Add(asset, new(sourceKey, source));
            }
            key = source.RasterKey;
            return true;
        }
    }

    internal static BitmapImageRasterKey ObjectClipKey(BitmapImageRasterKey source, BitmapObjectData data)
    {
        if (data.VisibleContours is not { } contours) return source;
        if (contours is [{ Length: 4 } full] && full[0] == new PointF(0, 0)
            && full[1] == new PointF(1, 0) && full[2] == new PointF(1, 1) && full[3] == new PointF(0, 1)) return source;
        var clipKey = ClipKeys.GetValue(data, value => Convert.ToHexString(SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value.VisibleContours,
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true }))));
        return source with { ContentSha256 = source.ContentSha256 + ":clip:" + clipKey };
    }

    internal static BitmapImageRasterKey ObjectAppearanceKey(BitmapImageRasterKey source, BitmapObjectData data) =>
        source with
        {
            ShadowCorners = data.ShadowCorners,
            WarmLightCorners = data.WarmLightCorners,
            BiomeTintArgb = data.BiomeTintArgb,
            BiomeTintGrassSide = data.BiomeTintArgb.HasValue && data.BiomeTintGrassSide
        };

    internal static BitmapImageRaster ApplyShadow(BitmapImageRaster source, uint corners, uint warmth = 0)
    {
        if ((corners | warmth) == 0) return source;
        return ApplyObjectAppearance(source, source.Key with { ShadowCorners = corners, WarmLightCorners = warmth });
    }

    // Derive from the decoded source so the lookup key and rendered pixels describe the same appearance.
    internal static BitmapImageRaster ApplyObjectAppearance(BitmapImageRaster source, BitmapImageRasterKey key)
    {
        if (key == source.Key) return source;
        var corners = key.ShadowCorners;
        var warmth = key.WarmLightCorners;
        lock (CacheSync)
        {
            if (RasterCache.TryGetValue(key, out var cached))
            {
                cached.LastUse = ++_useSequence;
                return cached.Raster;
            }
            var result = new BitmapImageRaster(key);
            var tintRgb = (key.BiomeTintArgb ?? -1) & 0x00ffffff;
            var tintB = tintRgb & 255;
            var tintG = (tintRgb >> 8) & 255;
            var tintR = tintRgb >> 16;
            var maskGrassSide = key.BiomeTintArgb.HasValue && key.BiomeTintGrassSide;
            var hasTint = maskGrassSide || tintRgb != 0x00ffffff;
            // Opaque texels of the bundled 16x16 grass_block_side_overlay, low bit at the left.
            ReadOnlySpan<ushort> grassSideRows = [0xffff, 0xffdf, 0x6f5d, 0x0510];
            var offset = 0;
            for (var y = 0; y < source.PixelHeight; y++)
            {
                var grassRow = maskGrassSide ? GrassSideMaskCoordinate(y, source.PixelHeight) : -1;
                var grassMask = (uint)grassRow < (uint)grassSideRows.Length ? grassSideRows[grassRow] : 0;
                var v = (2 * y + 1) * 128 / source.PixelHeight;
                var (left, right) = InterpolateEdges(corners, v);
                var (warmLeft, warmRight) = InterpolateEdges(warmth, v);
                for (var x = 0; x < source.PixelWidth; x++, offset += 4)
                {
                    var u = (2 * x + 1) * 128 / source.PixelWidth;
                    var shade = 255 - ((left * (256 - u) + right * u + 32768) >> 16);
                    var warm = (warmLeft * (256 - u) + warmRight * u + 32768) >> 16;
                    var b = (int)source.Pixels[offset];
                    var g = (int)source.Pixels[offset + 1];
                    var r = (int)source.Pixels[offset + 2];
                    if (hasTint && (!maskGrassSide || (grassMask != 0
                        && (grassMask & (1 << GrassSideMaskCoordinate(x, source.PixelWidth))) != 0)))
                    {
                        // Remove the baked grass hue without losing its texel shading or premultiplied alpha.
                        if (maskGrassSide) b = g = r = (54 * r + 183 * g + 19 * b + 128) >> 8;
                        // RGB multiplication on premultiplied pixels preserves the source alpha.
                        b = (b * tintB + 127) / 255;
                        g = (g * tintG + 127) / 255;
                        r = (r * tintR + 127) / 255;
                    }
                    result.Pixels[offset] = (byte)((b * (shade - shade * warm / 255) + 127) / 255);
                    result.Pixels[offset + 1] = (byte)((g * (shade - shade * warm / 765) + 127) / 255);
                    result.Pixels[offset + 2] = (byte)((r * Math.Min(255, shade + warm / 2) + 127) / 255);
                    result.Pixels[offset + 3] = source.Pixels[offset + 3];
                }
            }
            EvictFor(result.Pixels.LongLength);
            RasterCache[key] = new CacheEntry(result, ++_useSequence);
            _cacheBytes += result.Pixels.LongLength;
            return result;
        }

        // Sample the stock mask at the center of each decoded texel, including scaled rasters.
        static int GrassSideMaskCoordinate(int pixel, int extent) => (2 * pixel + 1) * 8 / extent;

        static (int Left, int Right) InterpolateEdges(uint value, int v) =>
            ((int)(value & 255) * (256 - v) + (int)(value >> 16 & 255) * v,
             (int)(value >> 8 & 255) * (256 - v) + (int)(value >> 24) * v);
    }

    internal static BitmapImageRaster ApplyObjectClip(BitmapImageRaster source, BitmapObjectData data)
    {
        if (data.VisibleContours is not { } contours) return source;
        if (contours is [{ Length: 4 } full] && full[0] == new PointF(0, 0)
            && full[1] == new PointF(1, 0) && full[2] == new PointF(1, 1) && full[3] == new PointF(0, 1)) return source;
        var key = ObjectClipKey(source.Key, data);
        lock (CacheSync)
        {
            if (RasterCache.TryGetValue(key, out var cached))
            {
                cached.LastUse = ++_useSequence;
                return cached.Raster;
            }
            var result = new BitmapImageRaster(key);
            if (contours.Length > 0)
            {
                using var path = new GraphicsPath(FillMode.Alternate);
                foreach (var contour in contours)
                    path.AddPolygon(contour.Select(point => new PointF(
                        point.X * source.PixelWidth, point.Y * source.PixelHeight)).ToArray());
                using var input = source.AcquireBitmap();
                using var output = result.AcquireBitmap();
                using var graphics = Graphics.FromImage(output.Bitmap);
                graphics.SetClip(path);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(input.Bitmap, 0, 0);
            }
            EvictFor(result.Pixels.LongLength);
            RasterCache[key] = new CacheEntry(result, ++_useSequence);
            _cacheBytes += result.Pixels.LongLength;
            return result;
        }
    }

    internal static long DecodeCallCount => Interlocked.Read(ref _decodeCallCount);
    internal static long SourceReadCount => Interlocked.Read(ref _sourceReadCount);
    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);
    internal static long CacheMissCount => Interlocked.Read(ref _cacheMissCount);
    internal static long CacheEvictionCount => Interlocked.Read(ref _cacheEvictionCount);

    private sealed class CacheEntry(BitmapImageRaster raster, long lastUse)
    {
        public BitmapImageRaster Raster { get; } = raster;
        public long LastUse { get; set; } = lastUse;
    }

    private readonly record struct SourceCacheKey(string FullPath, BitmapImageImportSettings Settings);
    private readonly record struct SourceStamp(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
    private readonly record struct SourceCacheEntry(SourceStamp Stamp, BitmapImageRasterKey RasterKey, bool Opaque);
    private sealed record AssetSourceProof(SourceCacheKey SourceKey, SourceCacheEntry Entry);

    /// <summary>
    /// Reads image metadata without decoding pixels, so the import dialog can show
    /// dimensions and natural PPI before the user commits.
    /// </summary>
    internal static bool TryProbe(
        string fullPath,
        out int pixelWidth,
        out int pixelHeight,
        out float naturalPixelsPerUnit,
        out string error)
    {
        pixelWidth = 0;
        pixelHeight = 0;
        naturalPixelsPerUnit = 96f;
        error = "";
        try
        {
            ValidateSourceFile(fullPath);
            using var image = LoadImage(fullPath, validateOnly: true);
            pixelWidth = image.Width;
            pixelHeight = image.Height;
            naturalPixelsPerUnit = ReadNaturalPixelsPerUnit(image);
            return BitmapImageFormats.TryValidateDecodedBudget(pixelWidth, pixelHeight, out error);
        }
        catch (Exception exception) when (exception is InvalidDataException or OutOfMemoryException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static string ComputeSha256(string fullPath)
    {
        Interlocked.Increment(ref _sourceReadCount);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Decodes the managed file into a premultiplied BGRA raster. The requested settings
    /// can downscale and pad to a power of two; they never upscale, because enlarging an
    /// imported source destroys detail the renderer cannot recover.
    /// </summary>
    internal static BitmapImageRaster Decode(string fullPath, BitmapImageImportSettings settings) =>
        DecodeSource(fullPath, settings, null);

    // A render lookup has just read these attributes to validate the resource
    // path. Reuse that per-lookup metadata, never a stamp from an earlier frame.
    internal static BitmapImageRaster Decode(FileInfo source, BitmapImageImportSettings settings,
        ImageAssetDefinition? asset = null) => DecodeSource(source.FullName, settings, source, asset);

    private static BitmapImageRaster DecodeSource(string fullPath, BitmapImageImportSettings settings,
        FileInfo? sourceMetadata, ImageAssetDefinition? asset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!settings.IsValid)
        {
            throw new InvalidDataException("The image import settings are invalid.");
        }

        Interlocked.Increment(ref _decodeCallCount);
        fullPath = Path.GetFullPath(fullPath);
        var file = ValidateSourceFile(fullPath, sourceMetadata);
        var stamp = new SourceStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        var sourceKey = new SourceCacheKey(fullPath.ToUpperInvariant(), settings);
        lock (CacheSync)
        {
            var known = SourceCache.TryGetValue(sourceKey, out var source);
            if (asset is not null && AssetSources.TryGetValue(asset, out var proof)
                && proof.SourceKey == sourceKey && proof.Entry.Stamp == stamp)
            { source = proof.Entry; known = true; }
            if (known
                && source.Stamp == stamp
                && RasterCache.TryGetValue(source.RasterKey, out var cached))
            {
                Interlocked.Increment(ref _cacheHitCount);
                cached.LastUse = ++_useSequence;
                if (asset is not null)
                {
                    AssetSources.Remove(asset);
                    AssetSources.Add(asset, new(sourceKey, source));
                }
                return cached.Raster;
            }
        }
        var storedSize = ResolveDecodeSize(fullPath, settings);
        var contentSha256 = ComputeSha256(fullPath);
        var key = new BitmapImageRasterKey(
            FormattableString.Invariant($"{contentSha256}:{settings.FilterMode}:{settings.AlphaSource}:{settings.CropX:R}:{settings.CropY:R}:{settings.CropWidth:R}:{settings.CropHeight:R}:{settings.FlipHorizontal}:{settings.FlipVertical}"), storedSize.Width, storedSize.Height);

        lock (CacheSync)
        {
            if (RasterCache.TryGetValue(key, out var cached))
            {
                Interlocked.Increment(ref _cacheHitCount);
                cached.LastUse = ++_useSequence;
                CacheSource(sourceKey, stamp, key, file, IsOpaque(cached.Raster), asset);
                return cached.Raster;
            }

            Interlocked.Increment(ref _cacheMissCount);
            var raster = new BitmapImageRaster(key);
            using (var source = LoadImage(fullPath, validateOnly: false))
            using (var scaled = RenderToSize(source, storedSize, settings))
            using (var bitmap = raster.AcquireBitmap())
            using (var graphics = Graphics.FromImage(bitmap.Bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = settings.FilterMode == ImageFilterMode.Point
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImageUnscaled(scaled, 0, 0);
            }

            ApplyAlphaSource(raster.Pixels, settings.AlphaSource);
            var opaque = IsOpaque(raster);
            if (opaque)
            {
                if (OpaqueSources.Count >= MaxCacheEntries) OpaqueSources.Remove(OpaqueSources.First());
                OpaqueSources.Add(key);
            }

            var cacheBytes = raster.Pixels.LongLength;
            EvictFor(cacheBytes);
            RasterCache[key] = new CacheEntry(raster, ++_useSequence);
            _cacheBytes += cacheBytes;
            CacheSource(sourceKey, stamp, key, file, opaque, asset);
            return raster;
        }
    }

    private static void CacheSource(SourceCacheKey sourceKey, SourceStamp stamp,
        BitmapImageRasterKey rasterKey, FileInfo file, bool opaque, ImageAssetDefinition? asset)
    {
        file.Refresh();
        if (!file.Exists || stamp != new SourceStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc)) return;
        // Retain source stamps for the live pixel working set. Otherwise 1,000
        // small images reread their files even though all pixels fit the budget.
        if (SourceCache.Count >= Math.Max(MaxCacheEntries, RasterCache.Count) && !SourceCache.ContainsKey(sourceKey))
            SourceCache.Remove(SourceCache.Keys.First());
        var entry = new SourceCacheEntry(stamp, rasterKey, opaque);
        SourceCache[sourceKey] = entry;
        if (asset is not null)
        {
            AssetSources.Remove(asset);
            AssetSources.Add(asset, new(sourceKey, entry));
        }
    }

    private static bool IsOpaque(BitmapImageRaster raster)
    {
        for (var offset = 3; offset < raster.Pixels.Length; offset += 4)
            if (raster.Pixels[offset] != 255) return false;
        return true;
    }

    internal static BitmapImageRasterKey RegionRasterKey(BitmapImageRasterKey source, Rectangle pixels)
        => pixels == new Rectangle(0, 0, source.PixelWidth, source.PixelHeight) ? source
            : source with
            {
                ContentSha256 = $"{source.ContentSha256}:region:{source.PixelWidth}:{source.PixelHeight}:{pixels.X}:{pixels.Y}",
                PixelWidth = pixels.Width, PixelHeight = pixels.Height
            };

    internal static BitmapImageRaster? TryGetRegionRaster(BitmapImageRasterKey source, Rectangle pixels)
    {
        lock (CacheSync)
        {
            if (!RasterCache.TryGetValue(RegionRasterKey(source, pixels), out var entry)) return null;
            entry.LastUse = ++_useSequence;
            return entry.Raster;
        }
    }

    internal static BitmapImageRaster CacheRegionRaster(BitmapImageRaster source, Rectangle pixels)
    {
        var key = RegionRasterKey(source.Key, pixels);
        if (key == source.Key) return source;
        lock (CacheSync)
        {
            if (RasterCache.TryGetValue(key, out var existing))
            { existing.LastUse = ++_useSequence; return existing.Raster; }
            var result = new BitmapImageRaster(key);
            for (var row = 0; row < pixels.Height; row++)
                Buffer.BlockCopy(source.Pixels, (pixels.Y + row) * source.Stride + pixels.X * 4,
                    result.Pixels, row * result.Stride, result.Stride);
            EvictFor(result.Pixels.LongLength);
            RasterCache.Add(key, new(result, ++_useSequence));
            _cacheBytes += result.Pixels.LongLength;
            return result;
        }
    }

    internal static void ClearCache()
    {
        lock (CacheSync)
        {
            RasterCache.Clear();
            SourceCache.Clear();
            AssetSources.Clear();
            OpaqueSources.Clear();
            _cacheBytes = 0;
        }
    }

    /// <summary>
    /// Resolves the pixel size a decode must produce, honouring MaxSize and the optional
    /// power-of-two constraint. Kept internal so import settings and the regression can
    /// assert the same numbers the decoder uses.
    /// </summary>
    internal static (int Width, int Height) ResolveDecodeSize(
        string fullPath,
        BitmapImageImportSettings settings)
    {
        using var image = LoadImage(fullPath, validateOnly: true);
        if (!BitmapImageFormats.TryValidateDecodedBudget(image.Width, image.Height, out var error))
        {
            throw new InvalidDataException(error);
        }

        var resolved = settings.ResolveStoredPixelSize(new SizeF(image.Width, image.Height));
        var width = Math.Max(1, (int)resolved.Width);
        var height = Math.Max(1, (int)resolved.Height);
        if (!BitmapImageFormats.TryValidateDecodedBudget(width, height, out var scaledError))
        {
            throw new InvalidDataException(scaledError);
        }
        return (width, height);
    }

    private static FileInfo ValidateSourceFile(string fullPath, FileInfo? source = null)
    {
        var file = source ?? new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new InvalidDataException("The image file no longer exists.");
        }
        if (!BitmapImageFormats.IsSupportedExtension(Path.GetExtension(fullPath)))
        {
            throw new InvalidDataException("The image file format is not supported.");
        }
        var length = file.Length;
        if (length <= 0 || length > MaxSourceBytes)
        {
            throw new InvalidDataException(
                $"Image files must be between 1 byte and {MaxSourceBytes} bytes.");
        }
        return file;
    }

    /// <summary>
    /// Loads through an explicit stream so the file is not left locked; the caller owns the
    /// returned image. <paramref name="validateOnly"/> still decodes the header, which is what
    /// rejects truncated or mislabelled files before the import is committed.
    /// </summary>
    private static Bitmap LoadImage(string fullPath, bool validateOnly)
    {
        Interlocked.Increment(ref _sourceReadCount);
        byte[] bytes;
        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var decoded = Image.FromStream(memory, useEmbeddedColorManagement: true, validateImageData: true);
            if (!BitmapImageFormats.TryValidateDecodedBudget(decoded.Width, decoded.Height, out var error))
            {
                throw new InvalidDataException(error);
            }
            if (validateOnly) return new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
            return new Bitmap(decoded);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("The image file could not be decoded.", exception);
        }
    }

    private static Bitmap RenderToSize(
        Image source,
        (int Width, int Height) size,
        BitmapImageImportSettings settings)
    {
        var target = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(target);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = settings.FilterMode == ImageFilterMode.Point
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TranslateTransform(settings.FlipHorizontal ? size.Width : 0, settings.FlipVertical ? size.Height : 0);
            graphics.ScaleTransform(settings.FlipHorizontal ? -1 : 1, settings.FlipVertical ? -1 : 1);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, size.Width, size.Height),
                settings.ResolveCropPixelBounds(source.Width, source.Height),
                GraphicsUnit.Pixel);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    // Library rows need a few dozen pixels, not a full renderer raster. This path
    // owns its stream/image, never takes CacheSync, and never evicts stage textures.
    internal static Bitmap CreateLibraryThumbnail(string fullPath, BitmapImageImportSettings settings,
        int size, CancellationToken cancellationToken)
    {
        if (!settings.IsValid || size is < 1 or > 512) throw new ArgumentException("Invalid thumbnail settings.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSourceFile(fullPath);
        ProjectTemporaryResources.RejectReparsePath(fullPath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        Interlocked.Increment(ref _sourceReadCount);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
        if (!BitmapImageFormats.TryValidateDecodedBudget(source.Width, source.Height, out var error))
            throw new InvalidDataException(error);
        var stored = settings.ResolveStoredPixelSize(source.Size);
        var scale = Math.Min(size / stored.Width, size / stored.Height);
        var width = Math.Max(1, (int)MathF.Round(stored.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(stored.Height * scale));
        cancellationToken.ThrowIfCancellationRequested();
        using var sampled = RenderToSize(source, (width, height), settings);
        if (settings.AlphaSource == ImageAlphaSource.None)
        {
            var data = sampled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    var pointer = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(pointer, row, 0, row.Length);
                    ApplyAlphaSource(row, settings.AlphaSource);
                    Marshal.Copy(row, 0, pointer, row.Length);
                }
            }
            finally { sampled.UnlockBits(data); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var thumbnail = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(thumbnail);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(sampled, (size - width) / 2, (size - height) / 2);
            return thumbnail;
        }
        catch { thumbnail.Dispose(); throw; }
    }

    /// <summary>
    /// Successively halved, high-quality-filtered copies of a decoded raster.
    /// A placed bitmap is normally drawn far smaller than its source pixels, and sampling
    /// that full-resolution source with plain bilinear filtering aliases badly; the aliasing
    /// reads as jagged edges as soon as the object is rotated. Renderers therefore pick the
    /// level closest to the on-screen size and leave at most a 2x reduction to the filter.
    /// This is what the import "Generate mipmaps" option promises but never produced.
    /// </summary>
    private const long MaxMinifiedCacheBytes = 128L * 1024 * 1024;
    private static readonly Dictionary<(BitmapImageRasterKey Key, int Level), BitmapImageRaster> MinifiedCache = new();
    private static readonly object MinifiedSync = new();
    private static long _minifiedCacheBytes;

    /// <summary>
    /// Returns the raster reduced by <paramref name="level"/> successive halvings, or null
    /// when it cannot be built. Level 0 is the source itself.
    /// </summary>
    internal static BitmapImageRaster? TryGetMinifiedRaster(BitmapImageRaster source, int level)
    {
        if (level <= 0) return source;
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        for (var step = 0; step < level && (width > 1 || height > 1); step++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        if (width >= source.PixelWidth && height >= source.PixelHeight) return source;

        lock (MinifiedSync)
        {
            if (MinifiedCache.TryGetValue((source.Key, level), out var cached)) return cached;
            var minified = CreateMinifiedRaster(source, width, height);
            if (minified is null) return null;
            if (_minifiedCacheBytes + minified.Pixels.LongLength > MaxMinifiedCacheBytes)
            {
                MinifiedCache.Clear();
                _minifiedCacheBytes = 0;
            }
            MinifiedCache[(source.Key, level)] = minified;
            _minifiedCacheBytes += minified.Pixels.LongLength;
            return minified;
        }
    }

    private static BitmapImageRaster? CreateMinifiedRaster(BitmapImageRaster source, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            using var lease = source.AcquireBitmap();
            var raster = new BitmapImageRaster(source.Key with { PixelWidth = width, PixelHeight = height });
            using var target = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    lease.Bitmap,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(0, 0, source.PixelWidth, source.PixelHeight),
                    GraphicsUnit.Pixel);
            }

            var data = target.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, y * data.Stride),
                        raster.Pixels,
                        y * raster.Stride,
                        raster.Stride);
                }
            }
            finally
            {
                target.UnlockBits(data);
            }

            return raster;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Honours the alpha-source setting: pre-multiplied pixels whose alpha is dropped must
    /// have their colour channels un-premultiplied, not left darkened.
    /// </summary>
    private static void ApplyAlphaSource(byte[] pixels, ImageAlphaSource alphaSource)
    {
        if (alphaSource != ImageAlphaSource.None) return;
        for (var offset = 0; offset + 3 < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha is 0 or 255)
            {
                pixels[offset + 3] = 255;
                if (alpha == 0)
                {
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                }
                continue;
            }

            // Un-premultiply against the stored alpha before forcing full opacity.
            pixels[offset] = (byte)Math.Min(255, pixels[offset] * 255 / alpha);
            pixels[offset + 1] = (byte)Math.Min(255, pixels[offset + 1] * 255 / alpha);
            pixels[offset + 2] = (byte)Math.Min(255, pixels[offset + 2] * 255 / alpha);
            pixels[offset + 3] = 255;
        }
    }

    /// <summary>
    /// Reads PPI from the decoded image's resolution metadata. GDI+ reports a default of
    /// 96 DPI for files that store none, which is indistinguishable from a true 96 PPI
    /// image, so the default is the documented fallback rather than a special case.
    /// </summary>
    private static float ReadNaturalPixelsPerUnit(Image image)
    {
        var horizontal = image.HorizontalResolution;
        return float.IsFinite(horizontal) && horizontal > 0
            ? Math.Clamp(
                horizontal,
                BitmapImageImportSettings.MinimumPixelsPerUnit,
                BitmapImageImportSettings.MaximumPixelsPerUnit)
            : 96f;
    }

    private static void EvictFor(long incomingBytes)
    {
        if (RasterCache.Count == 0) return;
        if (_cacheBytes <= MaxCacheBytes - incomingBytes) return;

        // Oldest-first eviction keeps the small working set that a timeline actually
        // references, matching the SVG raster cache's bounded-memory guarantee.
        var ordered = RasterCache
            .OrderBy(item => item.Value.LastUse)
            .ToArray();
        foreach (var item in ordered)
        {
            if (_cacheBytes <= MaxCacheBytes - incomingBytes) break;
            if (!RasterCache.Remove(item.Key)) continue;
            _cacheBytes -= item.Value.Raster.Pixels.LongLength;
            Interlocked.Increment(ref _cacheEvictionCount);
        }
    }
}
