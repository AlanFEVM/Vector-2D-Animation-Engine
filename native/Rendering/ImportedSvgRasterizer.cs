using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Svg;

namespace VectorAnimationEngine;

internal readonly record struct ImportedSvgLoadResult(string Source, SizeF IntrinsicSize);

internal readonly record struct ImportedSvgRasterKey(string ContentSha256, int PixelWidth, int PixelHeight);

internal sealed class ImportedSvgRaster
{
    public ImportedSvgRaster(ImportedSvgRasterKey key)
    {
        Key = key;
        Stride = checked(key.PixelWidth * 4);
        Pixels = new byte[checked(Stride * key.PixelHeight)];
    }

    public ImportedSvgRasterKey Key { get; }
    public int PixelWidth => Key.PixelWidth;
    public int PixelHeight => Key.PixelHeight;
    public int Stride { get; }
    public byte[] Pixels { get; }

    internal ImportedSvgBitmapLease AcquireBitmap() => new(this);
}

internal sealed class ImportedSvgBitmapLease : IDisposable
{
    private GCHandle _pixelsHandle;

    internal ImportedSvgBitmapLease(ImportedSvgRaster raster)
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

internal static class ImportedSvgRasterizer
{
    internal const long MaxImportSourceBytes = 8L * 1024 * 1024;
    private const long MaxRasterSourceBytes = 16L * 1024 * 1024;
    internal const int MaxRasterDimension = 8192;
    internal const int MaxRasterPixels = 16 * 1024 * 1024;
    private const int MaxXmlDepth = 256;
    private const string InstanceAppearanceNamespace = "urn:vector-animation-engine:instance-appearance";
    // Animated assets often have hundreds of small content/size variants. Retain those
    // within the byte budget instead of evicting them while most of it is still unused.
    private const int MaxRasterCacheEntries = 1024;
    private const long MaxRasterCacheBytes = 512L * 1024 * 1024;
    private const int RasterDimensionQuantum = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ConditionalWeakTable<string, SourceDescriptor> SourceDescriptors = new();
    private static readonly Dictionary<ImportedSvgRasterKey, CacheEntry> RasterCache = new();
    private static readonly object CacheSync = new();
    private static long _rasterCacheBytes;
    private static long _useSequence;
    private static long _descriptorLookupCount;
    private static long _descriptorCreationCount;
    private static long _rasterizeCallCount;
    private static long _rasterCacheHitCount;
    private static long _rasterCacheMissCount;
    private static long _rasterCacheEvictionCount;
    private static long _rasterCacheSameContentDifferentSizeMissCount;
    private static long _rasterCacheNewContentMissCount;
    private static long _rasterCacheLargerHitCount;
    private static long _rasterizeElapsedTicks;

    internal static long DescriptorLookupCount =>
        Interlocked.Read(ref _descriptorLookupCount);

    internal static long DescriptorCreationCount =>
        Interlocked.Read(ref _descriptorCreationCount);

    internal static long RasterizeCallCount =>
        Interlocked.Read(ref _rasterizeCallCount);

    internal static long RasterCacheHitCount =>
        Interlocked.Read(ref _rasterCacheHitCount);

    internal static long RasterCacheMissCount =>
        Interlocked.Read(ref _rasterCacheMissCount);

    internal static long RasterCacheEvictionCount =>
        Interlocked.Read(ref _rasterCacheEvictionCount);

    internal static long RasterCacheSameContentDifferentSizeMissCount =>
        Interlocked.Read(ref _rasterCacheSameContentDifferentSizeMissCount);

    internal static long RasterCacheNewContentMissCount =>
        Interlocked.Read(ref _rasterCacheNewContentMissCount);

    internal static long RasterCacheLargerHitCount =>
        Interlocked.Read(ref _rasterCacheLargerHitCount);

    internal static int RasterCacheEntryCount
    {
        get
        {
            lock (CacheSync)
            {
                return RasterCache.Count;
            }
        }
    }

    internal static long RasterCacheBytes
    {
        get
        {
            lock (CacheSync)
            {
                return _rasterCacheBytes;
            }
        }
    }

    internal static double RasterizeMilliseconds =>
        Interlocked.Read(ref _rasterizeElapsedTicks) * 1000d / Stopwatch.Frequency;

    static ImportedSvgRasterizer()
    {
        // SVG.NET defaults to resolving local and remote image/element references.
        // Imported assets are self-contained and never receive filesystem or network access.
        SvgDocument.DisableDtdProcessing = true;
        SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
        SvgDocument.ResolveExternalImages = ExternalType.None;
        SvgDocument.ResolveExternalElements = ExternalType.None;
    }

    internal static ImportedSvgLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An SVG file path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The SVG file does not exist.", fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Imported SVG files cannot use filesystem reparse points.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > MaxImportSourceBytes)
        {
            throw new InvalidDataException($"Imported SVG files must be between 1 byte and {MaxImportSourceBytes} bytes.");
        }

        byte[] bytes;
        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
        }

        string source;
        try
        {
            source = NormalizeSource(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The imported SVG is not valid UTF-8.", exception);
        }

        var descriptor = DescribeSource(source);
        return new ImportedSvgLoadResult(source, descriptor.IntrinsicSize);
    }

    internal static ImportedSvgRaster Rasterize(string source, float requestedPixelWidth, float requestedPixelHeight)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException("The imported SVG source is empty.");
        }
        if (!float.IsFinite(requestedPixelWidth)
            || !float.IsFinite(requestedPixelHeight)
            || requestedPixelWidth <= 0
            || requestedPixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPixelWidth), "SVG raster dimensions must be finite and positive.");
        }

        Interlocked.Increment(ref _rasterizeCallCount);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var descriptor = DescribeSource(source);
            var size = QuantizeRasterSize(requestedPixelWidth, requestedPixelHeight);
            var key = new ImportedSvgRasterKey(descriptor.ContentSha256, size.Width, size.Height);
            lock (CacheSync)
            {
                if (RasterCache.TryGetValue(key, out var cached))
                {
                    Interlocked.Increment(ref _rasterCacheHitCount);
                    cached.LastUse = ++_useSequence;
                    return cached.Raster;
                }

                // A larger raster is safe to reuse because every bitmap caller uses the
                // returned raster's actual dimensions for source rectangles and UV transforms.
                CacheEntry? larger = null;
                var sameContent = false;
                foreach (var candidate in RasterCache.Values)
                {
                    if (!string.Equals(
                            candidate.Raster.Key.ContentSha256,
                            key.ContentSha256,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sameContent = true;
                    if (candidate.Raster.PixelWidth < key.PixelWidth
                        || candidate.Raster.PixelHeight < key.PixelHeight
                        || !HasCompatibleRasterAspect(
                            requestedPixelWidth,
                            requestedPixelHeight,
                            candidate.Raster.Key))
                    {
                        continue;
                    }

                    if (larger is null
                        || IsSmallerRaster(candidate.Raster, larger.Raster))
                    {
                        larger = candidate;
                    }
                }

                if (larger is not null)
                {
                    Interlocked.Increment(ref _rasterCacheHitCount);
                    Interlocked.Increment(ref _rasterCacheLargerHitCount);
                    larger.LastUse = ++_useSequence;
                    return larger.Raster;
                }

                Interlocked.Increment(ref _rasterCacheMissCount);
                if (sameContent)
                {
                    Interlocked.Increment(ref _rasterCacheSameContentDifferentSizeMissCount);
                }
                else
                {
                    Interlocked.Increment(ref _rasterCacheNewContentMissCount);
                }

                var raster = new ImportedSvgRaster(key);
                if (!descriptor.Document.TryGetTarget(out var document))
                {
                    document = ParseDocument(StrictUtf8.GetBytes(source));
                    descriptor.Document.SetTarget(document);
                }
                using (var rendered = document.Draw(raster.PixelWidth, raster.PixelHeight))
                using (var bitmap = raster.AcquireBitmap())
                using (var graphics = Graphics.FromImage(bitmap.Bitmap))
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(rendered, 0, 0);
                }
                ApplyMultiplyTint(raster.Pixels, descriptor.MultiplyTintArgb);

                var cacheBytes = raster.Pixels.LongLength;
                EvictFor(cacheBytes);
                RasterCache[key] = new CacheEntry(raster, ++_useSequence);
                _rasterCacheBytes += cacheBytes;
                return raster;
            }
        }
        finally
        {
            Interlocked.Add(
                ref _rasterizeElapsedTicks,
                Stopwatch.GetTimestamp() - started);
        }
    }

    internal static void ClearCache()
    {
        lock (CacheSync)
        {
            RasterCache.Clear();
            _rasterCacheBytes = 0;
        }
    }

    private static SourceDescriptor DescribeSource(string source)
    {
        Interlocked.Increment(ref _descriptorLookupCount);
        try
        {
            return SourceDescriptors.GetValue(source, CreateSourceDescriptor);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or OverflowException)
        {
            throw new InvalidDataException("The imported SVG could not be parsed.", exception);
        }
    }

    private static SourceDescriptor CreateSourceDescriptor(string source)
    {
        Interlocked.Increment(ref _descriptorCreationCount);
        var sourceBytes = StrictUtf8.GetBytes(source);
        if (sourceBytes.LongLength <= 0 || sourceBytes.LongLength > MaxRasterSourceBytes)
        {
            throw new InvalidDataException($"Imported SVG source must not exceed {MaxRasterSourceBytes} bytes.");
        }

        var multiplyTintArgb = ValidateXml(source);
        var document = ParseDocument(sourceBytes);

        var intrinsicSize = document.GetDimensions();
        if (!float.IsFinite(intrinsicSize.Width)
            || !float.IsFinite(intrinsicSize.Height)
            || intrinsicSize.Width <= 0
            || intrinsicSize.Height <= 0)
        {
            throw new InvalidDataException("The imported SVG has no finite positive intrinsic size.");
        }

        var contentSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        return new SourceDescriptor(new WeakReference<SvgDocument>(document), intrinsicSize, contentSha256, multiplyTintArgb);
    }

    private static SvgDocument ParseDocument(byte[] sourceBytes)
    {
        try
        {
            using var stream = new MemoryStream(sourceBytes, writable: false);
            return SvgDocument.Open<SvgDocument>(stream, new SvgOptions())
                ?? throw new InvalidDataException("The imported SVG document is empty.");
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("SVG.NET could not parse the imported SVG.", exception);
        }
    }

    private static int ValidateXml(string source)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRasterSourceBytes,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false
        };
        using var text = new StringReader(source);
        using var reader = XmlReader.Create(text, settings);
        var rootFound = false;
        var multiplyTintArgb = unchecked((int)0xffffffff);
        while (reader.Read())
        {
            if (reader.Depth > MaxXmlDepth)
            {
                throw new InvalidDataException($"Imported SVG XML depth must not exceed {MaxXmlDepth}.");
            }
            if (reader.NodeType == XmlNodeType.ProcessingInstruction)
            {
                throw new InvalidDataException("Imported SVG processing instructions are not supported.");
            }
            if (rootFound || reader.NodeType != XmlNodeType.Element) continue;
            rootFound = true;
            if (!string.Equals(reader.LocalName, "svg", StringComparison.Ordinal)
                || !string.Equals(reader.NamespaceURI, "http://www.w3.org/2000/svg", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The imported document root is not an SVG element.");
            }

            var multiplyTint = reader.GetAttribute("multiply-tint", InstanceAppearanceNamespace);
            var rgb = 0;
            if (multiplyTint is not null
                && (multiplyTint.Length != 6
                    || !int.TryParse(
                        multiplyTint,
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out rgb)))
            {
                throw new InvalidDataException("The imported SVG instance tint marker is invalid.");
            }
            if (multiplyTint is not null) multiplyTintArgb = unchecked((int)0xff000000) | rgb;
        }

        if (!rootFound) throw new InvalidDataException("The imported SVG document is empty.");
        return multiplyTintArgb;
    }

    private static void ApplyMultiplyTint(byte[] pixels, int tintArgb)
    {
        var red = (tintArgb >>> 16) & 0xff;
        var green = (tintArgb >>> 8) & 0xff;
        var blue = tintArgb & 0xff;
        if (red == 0xff && green == 0xff && blue == 0xff) return;

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)((pixels[offset] * blue + 127) / 255);
            pixels[offset + 1] = (byte)((pixels[offset + 1] * green + 127) / 255);
            pixels[offset + 2] = (byte)((pixels[offset + 2] * red + 127) / 255);
        }
    }

    private static string NormalizeSource(string source)
    {
        if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];
        return source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static Size QuantizeRasterSize(float requestedWidth, float requestedHeight)
    {
        var requestedPixels = (double)requestedWidth * requestedHeight;
        var boundedScale = Math.Min(
            1d,
            Math.Min(
                MaxRasterDimension / (double)Math.Max(requestedWidth, requestedHeight),
                Math.Sqrt(MaxRasterPixels / requestedPixels)));

        // Quantize one scale rather than both axes independently. Independent rounding can turn
        // a small non-square target into a square raster, so SVG preserveAspectRatio no longer
        // matches the object's selection bounds while zooming.
        var requestedLongEdge = Math.Max(requestedWidth, requestedHeight);
        var boundedLongEdge = Math.Max(1, (int)Math.Ceiling(requestedLongEdge * boundedScale));
        var rasterLongEdge = Math.Min(
            MaxRasterDimension,
            RoundUp(boundedLongEdge, RasterDimensionQuantum));
        var rasterScale = rasterLongEdge / (double)requestedLongEdge;
        var width = Math.Max(1, (int)Math.Round(requestedWidth * rasterScale));
        var height = Math.Max(1, (int)Math.Round(requestedHeight * rasterScale));

        if ((long)width * height > MaxRasterPixels)
        {
            var pixelScale = Math.Sqrt(MaxRasterPixels / (double)((long)width * height));
            width = Math.Max(1, (int)Math.Floor(width * pixelScale));
            height = Math.Max(1, (int)Math.Floor(height * pixelScale));
        }
        return new Size(width, height);
    }

    private static int RoundUp(int value, int quantum)
    {
        return checked((value + quantum - 1) / quantum * quantum);
    }

    private static bool HasCompatibleRasterAspect(
        float requestedPixelWidth,
        float requestedPixelHeight,
        ImportedSvgRasterKey candidate)
    {
        var crossProductError = Math.Abs(
            candidate.PixelWidth * (double)requestedPixelHeight
            - candidate.PixelHeight * (double)requestedPixelWidth);
        return crossProductError <= Math.Max(requestedPixelWidth, requestedPixelHeight);
    }

    private static bool IsSmallerRaster(
        ImportedSvgRaster candidate,
        ImportedSvgRaster current)
    {
        var candidatePixels = (long)candidate.PixelWidth * candidate.PixelHeight;
        var currentPixels = (long)current.PixelWidth * current.PixelHeight;
        return candidatePixels < currentPixels
            || candidatePixels == currentPixels
                && (candidate.PixelWidth < current.PixelWidth
                    || candidate.PixelWidth == current.PixelWidth
                        && candidate.PixelHeight < current.PixelHeight);
    }

    private static void EvictFor(long requiredBytes)
    {
        while (RasterCache.Count >= MaxRasterCacheEntries
               || _rasterCacheBytes > MaxRasterCacheBytes - requiredBytes)
        {
            var oldest = RasterCache.MinBy(item => item.Value.LastUse);
            if (oldest.Value is null) break;
            RasterCache.Remove(oldest.Key);
            _rasterCacheBytes -= oldest.Value.Raster.Pixels.LongLength;
            Interlocked.Increment(ref _rasterCacheEvictionCount);
        }
    }

    private sealed record SourceDescriptor(
        WeakReference<SvgDocument> Document,
        SizeF IntrinsicSize,
        string ContentSha256,
        int MultiplyTintArgb);

    private sealed class CacheEntry(ImportedSvgRaster raster, long lastUse)
    {
        public ImportedSvgRaster Raster { get; } = raster;
        public long LastUse { get; set; } = lastUse;
    }
}
