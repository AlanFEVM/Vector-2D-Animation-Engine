using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

/// <summary>Only bounded PNG/JPEG raster data may cross the SVG bundle resource boundary.</summary>
internal static class FlashEmbeddedImageValidator
{
    internal static bool IsValidDataImage(string value)
    {
        const string png = "data:image/png;base64,";
        const string jpeg = "data:image/jpeg;base64,";
        var prefix = value.StartsWith(png, StringComparison.Ordinal) ? png
            : value.StartsWith(jpeg, StringComparison.Ordinal) ? jpeg : null;
        if (prefix is null || value.Length > 16 * 1024 * 1024) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value[prefix.Length..]); }
        catch (FormatException) { return false; }
        int width, height;
        if (prefix == png)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature)
                || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)) != 13) return false;
            width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        }
        else
        {
            if (!TryReadJpegSize(bytes, out width, out height)) return false;
        }
        if (!BitmapImageFormats.TryValidateDecodedBudget(width, height, out _)) return false;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return image.Width == width && image.Height == height;
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException or ExternalException)
        {
            return false;
        }
    }

    private static bool TryReadJpegSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position++] != 0xff) return false;
            while (position < bytes.Length && bytes[position] == 0xff) position++;
            if (position >= bytes.Length) return false;
            var marker = bytes[position++];
            if (marker is 0xda or 0xd9) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            if (position + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
            if (length < 2 || position + length > bytes.Length) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 5, 2));
                return true;
            }
            position += length;
        }
        return false;
    }
}
