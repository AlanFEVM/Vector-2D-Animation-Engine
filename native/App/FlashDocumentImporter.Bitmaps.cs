using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    [Flags]
    private enum BitmapDecodeResult { None = 0, PartialAlpha = 1, OriginalIgnored = 2 }

    private static Dictionary<string, DrawingObjectDefinition> ImportBitmaps(
        VectorProject project, XElement document, XflSource source, Dictionary<string, string> folders, List<string> temporaryFiles,
        List<FlashImportDiagnostic> diagnostics, IProgress<FlashParseProgress>? progress = null, bool editableGeometry = false)
    {
        var result = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        long totalBytes = 0;
        var items = document.Descendants().Where(node => node.Name.LocalName == "DOMBitmapItem").ToArray();
        foreach (var item in items)
        {
            var name = Required(item, "name");
            progress?.Report(new("Reading Flash bitmaps", result.Count, items.Length, name));
            if (result.ContainsKey(name)) throw Failure(name, "Duplicate library bitmap name.");
            var reference = XflSource.SafeRelativePath(Required(item, "bitmapDataHRef"));
            var bytes = source.ReadBytes(reference.StartsWith("bin/", StringComparison.Ordinal) ? reference : "bin/" + reference, 128L * 1024 * 1024);
            totalBytes += bytes.Length;
            if (totalBytes > 512L * 1024 * 1024) throw Failure(name, "Bitmap data exceeds the import budget.");
            var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            var file = Path.Combine(Path.GetTempPath(), "v2d-flash-" + Guid.NewGuid().ToString("N") + (jpeg ? ".jpg" : ".png"));
            temporaryFiles.Add(file);
            if (jpeg) File.WriteAllBytes(file, bytes);
            else
            {
                var decoded = WriteLosslessBitmap(bytes, item, source, file, name);
                if (decoded.HasFlag(BitmapDecodeResult.PartialAlpha))
                    diagnostics.Add(new("bitmap-premultiplied-decoded", "library/" + name,
                        "Embedded premultiplied bitmap pixels were decoded with their stored alpha unchanged. Straight RGB uses canonical integer rounding because original straight colours are unavailable; re-premultiplication preserves the embedded 8-bit pixels."));
                if (decoded.HasFlag(BitmapDecodeResult.OriginalIgnored))
                    diagnostics.Add(new("bitmap-original-mismatch", "library/" + name,
                        "The library PNG differs from the embedded bitmap. The embedded pixels used by Animate were decoded instead."));
            }
            if (!BitmapImageRasterizer.TryProbe(file, out var width, out var height, out var density, out var error))
                throw Failure(name, "Invalid bitmap: " + error);
            var settings = BitmapImageImportSettings.Default with
            {
                Compression = jpeg ? ImageCompression.Jpeg : ImageCompression.LosslessPng,
                FilterMode = Boolean(item, "allowSmoothing", false) ? ImageFilterMode.Bilinear : ImageFilterMode.Point,
                Mipmaps = false
            };
            if (!project.TryAddImageAssetFromSource(name.Split('/')[^1], file, BitmapImageRasterizer.ComputeSha256(file),
                    width, height, density, settings, out var asset) || asset is null)
                throw Failure(name, "Could not add the native bitmap asset.");
            var symbol = editableGeometry ? new DrawingObjectDefinition { Name = name.Split('/')[^1] } : project.AddDrawingObject(name.Split('/')[^1]);
            symbol.Detail = "Imported XFL bitmap library item: " + name;
            var separator = name.LastIndexOf('/');
            if (!editableGeometry && separator >= 0 && !project.TryMoveDrawingObjectToAssetFolder(symbol.Id, folders[name[..separator]]))
                throw Failure(name, "Could not assign the native bitmap library folder.");
            symbol.Scene.CreateEmpty(frameCount: 1);
            symbol.Scene.AddBitmapObject(0, new PointF(VectorUnits.FromPixels(width / 2f), VectorUnits.FromPixels(height / 2f)),
                new BitmapObjectData { ImageAssetId = asset.Id, PlacedSize = new SizeF(VectorUnits.FromPixels(width), VectorUnits.FromPixels(height)) });
            result.Add(name, symbol);
            progress?.Report(new("Reading Flash bitmaps", result.Count, items.Length, name));
        }
        return result;
    }

    private static BitmapDecodeResult WriteLosslessBitmap(byte[] bytes, XElement item, XflSource source, string file, string name)
    {
        // Verified XFL variants: 03/05 ARGB, and 03/03 indexed rows with an
        // uncompressed ARGB palette. Both use length-prefixed zlib chunks.
        if (bytes.Length < 28 || bytes[0] != 3 || bytes[1] is not (3 or 5) || (bytes[24] & ~1) != 0)
            throw Failure(name, "Unsupported XFL lossless bitmap encoding.");
        var indexed = bytes[1] == 3;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        var rowStride = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
        if (!BitmapImageFormats.TryValidateDecodedBudget(width, height, out var error)
            || rowStride != (indexed ? (width + 3) & ~3 : width * 4))
            throw Failure(name, "Invalid lossless bitmap dimensions or row stride: " + error);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) != width * 20u
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4)) != height * 20u)
            throw Failure(name, "Nonstandard lossless bitmap bounds are not supported.");
        var paletteCount = 0;
        var compressionOffset = 25;
        if (indexed)
        {
            paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(25, 2));
            if (paletteCount is < 1 or > 256) throw Failure(name, "Invalid indexed bitmap palette size.");
            compressionOffset = 27 + paletteCount * 4;
        }
        if (compressionOffset >= bytes.Length || bytes[compressionOffset] != 1)
            throw Failure(name, "Unsupported or truncated lossless bitmap compression/palette data.");
        using var compressed = new MemoryStream();
        var offset = compressionOffset + 1;
        while (true)
        {
            if (offset + 2 > bytes.Length) throw Failure(name, "Truncated bitmap chunk length.");
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            offset += 2;
            if (count == 0) break;
            if (count > bytes.Length - offset) throw Failure(name, "Truncated bitmap chunk.");
            compressed.Write(bytes, offset, count);
            offset += count;
        }
        if (offset != bytes.Length) throw Failure(name, "Unexpected trailing bitmap data.");
        compressed.Position = 0;
        var pixels = new byte[checked(width * height * 4)];
        try
        {
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            if (indexed)
            {
                var row = new byte[rowStride];
                for (var y = 0; y < height; y++)
                {
                    inflater.ReadExactly(row);
                    for (var x = 0; x < width; x++)
                    {
                        var index = row[x];
                        if (index >= paletteCount) throw Failure(name, "Indexed bitmap pixel exceeds its palette size.");
                        bytes.AsSpan(27 + index * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
                    }
                }
            }
            else inflater.ReadExactly(pixels);
            if (inflater.ReadByte() != -1) throw Failure(name, "Oversized decoded bitmap data.");
        }
        catch (EndOfStreamException) { throw Failure(name, "Truncated decoded bitmap data."); }
        var hasAlpha = (bytes[24] & 1) != 0;
        // Library source PNGs may be stale. Retain one only when its alpha and
        // rounded premultiplied colours match the authoritative embedded pixels.
        var result = BitmapDecodeResult.None;
        var href = Text(item, "href", "");
        if (href.Length > 0 && source.HasEntry("LIBRARY/" + XflSource.SafeRelativePath(href))
            && Path.GetExtension(href).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            var original = source.ReadBytes("LIBRARY/" + href, 128L * 1024 * 1024);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (original.Length < 33 || !original.AsSpan(0, 8).SequenceEqual(signature)
                || !original.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(16, 4)) != width
                || BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(20, 4)) != height)
                throw Failure(name, "Original PNG header does not match the Flash bitmap.");
            using var stream = new MemoryStream(original);
            using var image = new Bitmap(stream);
            if (image.Width != width || image.Height != height) throw Failure(name, "Original PNG dimensions differ from the Flash bitmap.");
            var matches = true;
            for (var y = 0; y < height && matches; y++)
                for (var x = 0; x < width; x++)
                {
                    var color = image.GetPixel(x, y);
                    var i = (y * width + x) * 4;
                    var alpha = hasAlpha ? pixels[i] : 255;
                    if (color.A != alpha
                        || pixels[i + 1] != (color.R * alpha + 127) / 255
                        || pixels[i + 2] != (color.G * alpha + 127) / 255
                        || pixels[i + 3] != (color.B * alpha + 127) / 255)
                    {
                        matches = false;
                        break;
                    }
                }
            if (matches)
            {
                File.WriteAllBytes(file, original);
                return result;
            }
            result |= BitmapDecodeResult.OriginalIgnored;
        }
        var bgra = new byte[pixels.Length];
        var partialAlpha = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = hasAlpha ? pixels[i] : 255;
            // Embedded pixels/palette entries are premultiplied ARGB, not an inverse of the original
            // author's PNG. Keep alpha, and choose straight bytes whose rounded
            // re-premultiplication reproduces every embedded channel exactly.
            if (hasAlpha && (pixels[i + 1] > alpha || pixels[i + 2] > alpha || pixels[i + 3] > alpha))
                throw Failure(name, "Invalid premultiplied bitmap: a colour channel exceeds alpha.");
            partialAlpha |= alpha is > 0 and < 255;
            bgra[i] = Straight(pixels[i + 3], alpha);
            bgra[i + 1] = Straight(pixels[i + 2], alpha);
            bgra[i + 2] = Straight(pixels[i + 1], alpha);
            bgra[i + 3] = (byte)alpha;
        }
        using var decoded = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = decoded.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(bgra, y * width * 4, locked.Scan0 + y * locked.Stride, width * 4);
        }
        finally { decoded.UnlockBits(locked); }
        decoded.Save(file, ImageFormat.Png);
        return partialAlpha ? result | BitmapDecodeResult.PartialAlpha : result;

        static byte Straight(byte channel, int alpha) => alpha == 0 ? (byte)0
            : (byte)((channel * 255 + alpha / 2) / alpha);
    }

    internal static void DeleteTemporaryImages(IReadOnlyList<string>? files)
    {
        if (files is null) return;
        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
