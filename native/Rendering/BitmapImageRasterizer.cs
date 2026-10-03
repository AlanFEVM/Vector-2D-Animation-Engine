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
internal readonly record struct BitmapImageRasterKey(string ContentSha256, int PixelWidth, int PixelHeight);

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
    private static readonly object CacheSync = new();
    private static long _cacheBytes;
    private static long _useSequence;
    private static long _decodeCallCount;
    private static long _cacheHitCount;
    private static long _cacheMissCount;
    private static long _cacheEvictionCount;

    internal static long DecodeCallCount => Interlocked.Read(ref _decodeCallCount);
    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);
    internal static long CacheMissCount => Interlocked.Read(ref _cacheMissCount);
    internal static long CacheEvictionCount => Interlocked.Read(ref _cacheEvictionCount);

    private sealed class CacheEntry(BitmapImageRaster raster, long lastUse)
    {
        public BitmapImageRaster Raster { get; } = raster;
        public long LastUse { get; set; } = lastUse;
    }

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
    internal static BitmapImageRaster Decode(string fullPath, BitmapImageImportSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!settings.IsValid)
        {
            throw new InvalidDataException("The image import settings are invalid.");
        }

        Interlocked.Increment(ref _decodeCallCount);
        ValidateSourceFile(fullPath);
        var storedSize = ResolveDecodeSize(fullPath, settings);
        var contentSha256 = ComputeSha256(fullPath);
        var key = new BitmapImageRasterKey(contentSha256, storedSize.Width, storedSize.Height);

        lock (CacheSync)
        {
            if (RasterCache.TryGetValue(key, out var cached))
            {
                Interlocked.Increment(ref _cacheHitCount);
                cached.LastUse = ++_useSequence;
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

            var cacheBytes = raster.Pixels.LongLength;
            EvictFor(cacheBytes);
            RasterCache[key] = new CacheEntry(raster, ++_useSequence);
            _cacheBytes += cacheBytes;
            return raster;
        }
    }

    internal static void ClearCache()
    {
        lock (CacheSync)
        {
            RasterCache.Clear();
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

    private static void ValidateSourceFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException("The image file no longer exists.");
        }
        if (!BitmapImageFormats.IsSupportedExtension(Path.GetExtension(fullPath)))
        {
            throw new InvalidDataException("The image file format is not supported.");
        }
        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > MaxSourceBytes)
        {
            throw new InvalidDataException(
                $"Image files must be between 1 byte and {MaxSourceBytes} bytes.");
        }
    }

    /// <summary>
    /// Loads through an explicit stream so the file is not left locked; the caller owns the
    /// returned image. <paramref name="validateOnly"/> still decodes the header, which is what
    /// rejects truncated or mislabelled files before the import is committed.
    /// </summary>
    private static Bitmap LoadImage(string fullPath, bool validateOnly)
    {
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
        Bitmap source,
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
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, size.Width, size.Height),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
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
        if (RasterCache.Count < MaxCacheEntries && _cacheBytes <= MaxCacheBytes - incomingBytes) return;

        // Oldest-first eviction keeps the small working set that a timeline actually
        // references, matching the SVG raster cache's bounded-memory guarantee.
        var ordered = RasterCache
            .OrderBy(item => item.Value.LastUse)
            .ToArray();
        foreach (var item in ordered)
        {
            if (RasterCache.Count < MaxCacheEntries && _cacheBytes <= MaxCacheBytes - incomingBytes) break;
            if (!RasterCache.Remove(item.Key)) continue;
            _cacheBytes -= item.Value.Raster.Pixels.LongLength;
            Interlocked.Increment(ref _cacheEvictionCount);
        }
    }
}
