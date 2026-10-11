using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private static Dictionary<string, DrawingObjectDefinition> ImportBitmaps(
        VectorProject project, XElement document, XflSource source, Dictionary<string, string> folders, List<string> temporaryFiles)
    {
        var result = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var item in document.Descendants().Where(node => node.Name.LocalName == "DOMBitmapItem"))
        {
            var name = Required(item, "name");
            if (result.ContainsKey(name)) throw Failure(name, "Duplicate library bitmap name.");
            var reference = XflSource.SafeRelativePath(Required(item, "bitmapDataHRef"));
            var bytes = source.ReadBytes(reference.StartsWith("bin/", StringComparison.Ordinal) ? reference : "bin/" + reference, 128L * 1024 * 1024);
            totalBytes += bytes.Length;
            if (totalBytes > 512L * 1024 * 1024) throw Failure(name, "Bitmap data exceeds the import budget.");
            var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            var file = Path.Combine(Path.GetTempPath(), "v2d-flash-" + Guid.NewGuid().ToString("N") + (jpeg ? ".jpg" : ".png"));
            temporaryFiles.Add(file);
            if (jpeg) File.WriteAllBytes(file, bytes);
            else WriteLosslessBitmap(bytes, item, source, file, name);
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
            var symbol = project.AddDrawingObject(name.Split('/')[^1]);
            symbol.Detail = "Imported XFL bitmap library item: " + name;
            var separator = name.LastIndexOf('/');
            if (separator >= 0 && !project.TryMoveDrawingObjectToAssetFolder(symbol.Id, folders[name[..separator]]))
                throw Failure(name, "Could not assign the native bitmap library folder.");
            symbol.Scene.CreateEmpty(frameCount: 1);
            symbol.Scene.AddBitmapObject(0, new PointF(VectorUnits.FromPixels(width / 2f), VectorUnits.FromPixels(height / 2f)),
                new BitmapObjectData { ImageAssetId = asset.Id, PlacedSize = new SizeF(VectorUnits.FromPixels(width), VectorUnits.FromPixels(height)) });
            result.Add(name, symbol);
        }
        return result;
    }

    private static void WriteLosslessBitmap(byte[] bytes, XElement item, XflSource source, string file, string name)
    {
        // XFL 03/05 stores ARGB rows in a length-prefixed zlib stream. Accept only
        // this verified variant; other layouts must not be guessed.
        if (bytes.Length < 28 || bytes[0] != 3 || bytes[1] != 5 || bytes[25] != 1 || (bytes[24] & ~1) != 0)
            throw Failure(name, "Unsupported XFL lossless bitmap encoding.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        if (!BitmapImageFormats.TryValidateDecodedBudget(width, height, out var error)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)) != width * 4)
            throw Failure(name, "Invalid lossless bitmap dimensions or row stride: " + error);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) != width * 20u
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4)) != height * 20u)
            throw Failure(name, "Nonstandard lossless bitmap bounds are not supported.");
        using var compressed = new MemoryStream();
        var offset = 26;
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
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            inflater.ReadExactly(pixels);
            if (inflater.ReadByte() != -1) throw Failure(name, "Oversized decoded bitmap data.");
        }
        var hasAlpha = (bytes[24] & 1) != 0;
        // A stored PNG can retain straight-alpha colours that Flash's /256
        // premultiplication quantized. Verify it matches the embedded data.
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
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var color = image.GetPixel(x, y);
                    var i = (y * width + x) * 4;
                    var alpha = hasAlpha ? color.A : 255;
                    var storedAlpha = alpha is > 0 and < 255 ? alpha + 1 : alpha;
                    if (pixels[i] != storedAlpha
                        || pixels[i + 1] != (alpha == 255 ? color.R : color.R * alpha / 256)
                        || pixels[i + 2] != (alpha == 255 ? color.G : color.G * alpha / 256)
                        || pixels[i + 3] != (alpha == 255 ? color.B : color.B * alpha / 256))
                        throw Failure(name, "Original PNG does not match the Flash bitmap data.");
                }
            File.WriteAllBytes(file, original);
            return;
        }
        var bgra = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = hasAlpha ? pixels[i] : 255;
            if (hasAlpha && alpha != 0) // Stored alpha 255 is also ambiguous with original alpha 254.
                throw Failure(name, "This bitmap has quantized partial transparency and no matching original PNG; exact restoration cannot be guaranteed.");
            bgra[i] = pixels[i + 3];
            bgra[i + 1] = pixels[i + 2];
            bgra[i + 2] = pixels[i + 1];
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
