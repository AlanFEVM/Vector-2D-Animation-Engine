using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashIndexedBitmapRegression(string root)
    {
        var folder = Path.Combine(root, "IndexedBitmapXfl");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"),
            $"<DOMDocument xmlns='{FlashFixtureNamespace}'><media><DOMBitmapItem name='Indexed' bitmapDataHRef='Indexed.dat'/></media>"
            + "<timelines><DOMTimeline><layers><DOMLayer><frames><DOMFrame duration='2'><elements>"
            + "<DOMBitmapInstance libraryItemName='Indexed'/></elements></DOMFrame></frames></DOMLayer></layers></DOMTimeline></timelines></DOMDocument>");
        var source = Path.Combine(folder, "bin", "Indexed.dat");
        byte[] palette = [0, 0, 0, 0, 128, 64, 32, 16, 255, 5, 17, 211];
        // 3 visible pixels, followed by an unused padding index outside the palette.
        byte[] indices = [0, 1, 2, 255, 2, 1, 0, 255];
        var encoded = FlashIndexedBitmapFixture(3, 2, palette, indices, true);
        File.WriteAllBytes(source, encoded);
        var imported = FlashDocumentImporter.Load(folder);
        try
        {
            CheckPixels(imported.Project);
            AssertTimeline(imported.Diagnostics.Any(item => item.Code == "bitmap-premultiplied-decoded"),
                "Partial-alpha indexed colours were decoded without the existing rounding disclosure.");
            var saved = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "IndexedBitmapSaved", "Indexed.v2dProject"));
            FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles);
            var reopened = ProjectVaultStore.Load(saved);
            CheckPixels(reopened);
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, reopened.Scenes[0], reopened.DrawingObjects, 1, imported.Fps);
            AssertTimeline(composed.ObjectCount == 1 && composed.ShapeKind[0] == ShapeKind.Bitmap,
                "An indexed bitmap lost its held exposure after Save/Open/composition.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }

        byte[] fullPalette = new byte[256 * 4];
        for (var index = 0; index < 256; index++) fullPalette[index * 4] = 255;
        fullPalette[255 * 4 + 1] = 12; fullPalette[255 * 4 + 2] = 34; fullPalette[255 * 4 + 3] = 56;
        File.WriteAllBytes(source, FlashIndexedBitmapFixture(1, 1, fullPalette, [255, 0, 0, 0], true));
        imported = FlashDocumentImporter.Load(folder);
        try
        {
            using var image = new Bitmap(imported.Project.ImageAssets.Single().SourcePath);
            AssertTimeline(image.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 12, 34, 56).ToArgb(),
                "A 256-entry palette was treated as a byte-sized count or lost index 255.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }
        File.WriteAllBytes(source, FlashIndexedBitmapFixture(1, 1, [0, 19, 73, 201], [0, 255, 255, 255], false));
        imported = FlashDocumentImporter.Load(folder);
        try
        {
            using var image = new Bitmap(imported.Project.ImageAssets.Single().SourcePath);
            AssertTimeline(image.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 19, 73, 201).ToArgb(),
                "An opaque indexed bitmap used its ignored palette alpha byte.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }

        Reject(FlashIndexedBitmapFixture(3, 2, palette, [3, 1, 2, 0, 2, 1, 0, 0], true), "Out-of-range visible palette index was accepted.");
        Reject(FlashIndexedBitmapFixture(3, 2, palette, indices[..^1], true), "Truncated indexed rows were accepted.");
        Reject(FlashIndexedBitmapFixture(3, 2, palette, [.. indices, 0], true), "Extra decoded indexed pixels were accepted.");
        Reject(FlashIndexedBitmapFixture(1, 1, [17, 18, 0, 0], [0, 0, 0, 0], true), "Invalid premultiplied palette channels were accepted.");
        var invalid = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(2), 3);
        Reject(invalid, "Unaligned indexed bitmap row stride was accepted.");
        invalid = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(25), 0);
        Reject(invalid, "An empty indexed palette was accepted.");
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(25), 257);
        Reject(invalid, "An oversized indexed palette was accepted.");
        Reject(encoded[..(27 + palette.Length - 1)], "A truncated palette was accepted.");
        invalid = (byte[])encoded.Clone(); invalid[27 + palette.Length] = 0;
        Reject(invalid, "Unverified indexed compression mode was accepted.");
        Console.WriteLine("flash_indexed_bitmap_palette_padding_alpha_256_save_reopen_rejection=ok");

        void Reject(byte[] bytes, string message)
        {
            File.WriteAllBytes(source, bytes);
            ExpectInvalidData(() => FlashDocumentImporter.Load(folder), message);
        }

        static void CheckPixels(VectorProject project)
        {
            using var image = new Bitmap(project.ImageAssets.Single().SourcePath);
            AssertTimeline(image.Size == new Size(3, 2) && image.GetPixel(0, 0).ToArgb() == 0
                && image.GetPixel(2, 1).ToArgb() == 0
                && image.GetPixel(2, 0).ToArgb() == Color.FromArgb(255, 5, 17, 211).ToArgb()
                && image.GetPixel(0, 1).ToArgb() == Color.FromArgb(255, 5, 17, 211).ToArgb(),
                "Indexed row padding, palette channel order or row order changed.");
            var partial = image.GetPixel(1, 0);
            AssertTimeline(partial.A == 128 && (partial.R * partial.A + 127) / 255 == 64
                && (partial.G * partial.A + 127) / 255 == 32 && (partial.B * partial.A + 127) / 255 == 16
                && image.GetPixel(1, 1).ToArgb() == partial.ToArgb(),
                "Indexed alpha or premultiplied colour changed after decoding/Save/Open.");
        }
    }

    private static byte[] FlashIndexedBitmapFixture(int width, int height, byte[] palette, byte[] indices, bool hasAlpha)
    {
        var header = new byte[27];
        header[0] = 3; header[1] = 3; header[24] = hasAlpha ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)((width + 3) & ~3));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(width * 20));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(height * 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(25), (ushort)(palette.Length / 4));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(indices);
        var payload = compressed.ToArray();
        using var result = new MemoryStream();
        result.Write(header); result.Write(palette); result.WriteByte(1);
        result.Write(BitConverter.GetBytes((ushort)2)); result.Write(payload, 0, 2);
        result.Write(BitConverter.GetBytes((ushort)(payload.Length - 2))); result.Write(payload, 2, payload.Length - 2);
        result.Write([0, 0]);
        return result.ToArray();
    }
}
