using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.IO.Compression;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashBitmapFillImportRegression(string root)
    {
        var folder = Path.Combine(root, "BitmapFillSource");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        var header = new byte[26];
        header[0] = 3; header[1] = 5; header[25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 40);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(new byte[] { 255, 255, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255, 0, 0, 255 });
        using (var encoded = File.Create(Path.Combine(folder, "bin", "Tile.dat")))
        {
            encoded.Write(header);
            encoded.Write(BitConverter.GetBytes((ushort)compressed.Length));
            encoded.Write(compressed.ToArray());
            encoded.Write(new byte[] { 0, 0 });
        }
        var xml = $"""
            <DOMDocument xmlns="{FlashFixtureNamespace}" width="32" height="16">
            <media><DOMBitmapItem name="Tile" bitmapDataHRef="Tile.dat" allowSmoothing="false"/></media>
            <timelines><DOMTimeline name="Fill"><layers><DOMLayer><frames><DOMFrame><elements>
            <DOMShape><fills><FillStyle index="1"><BitmapFill bitmapPath="Tile"><matrix><Matrix a="160" d="160"/></matrix></BitmapFill></FillStyle></fills>
            <edges><Edge fillStyle1="1" edges="!0 0|640 0|640 320|0 320|0 0"/></edges></DOMShape>
            </elements></DOMFrame></frames></DOMLayer></layers></DOMTimeline></timelines></DOMDocument>
            """;
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"), xml);
        var imported = FlashDocumentImporter.Load(folder);
        try
        {
            AssertTimeline(imported.Diagnostics.Any(item => item.Code == "bitmap-fill-svg-retained"), "Bitmap paint representation was not reported.");
            var manifest = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "BitmapFillSaved", "Fill.v2dProject"));
            FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles);
            var reopened = ProjectVaultStore.Load(manifest);
            var composed = new VectorScene();
            SceneCompositionBuilder.Build(composed, reopened.Scenes[0], reopened.DrawingObjects, 0, imported.Fps);
            AssertTimeline(composed.ObjectCount == 1 && composed.TryGetImportedSvgSource(0, out _), "Bitmap-painted vector shape was lost on Save/Open.");
            composed.TryGetImportedSvgSource(0, out var svg);
            var raster = ImportedSvgRasterizer.Rasterize(svg, 32, 16);
            AssertTimeline(raster.Pixels[4 * raster.Stride + 2 * 4 + 2] == 255
                && raster.Pixels[4 * raster.Stride + 10 * 4] == 255,
                "Bitmap fill matrix, embedded pixels or point sampling changed after Save/Open.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(imported.TemporaryFiles); }
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"), xml.Replace("bitmapPath=\"Tile\"", "bitmapPath=\"Missing\"", StringComparison.Ordinal));
        ExpectInvalidData(() => FlashDocumentImporter.Load(folder), "A missing bitmap paint reference was silently accepted.");
        File.WriteAllText(Path.Combine(folder, "DOMDocument.xml"), xml.Replace("bitmapPath=\"Tile\"", "bitmapPath=\"Tile\" bitmapIsClipped=\"true\"", StringComparison.Ordinal));
        var clampedImport = FlashDocumentImporter.Load(folder);
        try
        {
            AssertTimeline(clampedImport.Project.DrawingObjects.Any(item => Enumerable.Range(0, item.Scene.ObjectCount)
                .Any(index => item.Scene.TryGetImportedSvgSource(index, out var source) && source.Contains("point-clamp", StringComparison.Ordinal))),
                "Clipped bitmap fill was interpreted as tiled.");
        }
        finally { FlashDocumentImporter.DeleteTemporaryImages(clampedImport.TemporaryFiles); }
        Console.WriteLine("flash_bitmap_fill_import_save_reopen_sampling=ok");
    }

    private static void RunFlashBitmapSamplingRegression()
    {
        using var tile = new Bitmap(2, 2);
        tile.SetPixel(0, 0, Color.Red); tile.SetPixel(0, 1, Color.Red);
        tile.SetPixel(1, 0, Color.Blue); tile.SetPixel(1, 1, Color.Blue);
        using var bytes = new MemoryStream();
        tile.Save(bytes, ImageFormat.Png);
        var uri = "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
        string Svg(string mode, string matrix, string opacity = "1") => $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="32" height="16" viewBox="0 0 32 16">
            <defs><pattern id="tile" patternUnits="userSpaceOnUse" width="2" height="2" patternTransform="matrix({matrix})" data-v2d-bitmap-sampling="{mode}">
            <image width="2" height="2" xlink:href="{uri}"/></pattern></defs>
            <path d="M0 0H32V16H0Z" fill="url(#tile)" fill-opacity="{opacity}"/>
            </svg>
            """;
        var nearest = ImportedSvgRasterizer.Rasterize(Svg("point-tile", "8 0 0 8 0 0"), 32, 16);
        for (var y = 1; y < 15; y++)
            for (var x = 1; x < 31; x++)
            {
                var i = y * nearest.Stride + x * 4;
                var red = (x / 8) % 2 == 0;
                AssertTimeline(nearest.Pixels[i + 3] == 255 && nearest.Pixels[i + 2] == (red ? 255 : 0)
                    && nearest.Pixels[i] == (red ? 0 : 255), "Point bitmap fill blended adjacent texels or failed to tile.");
            }
        var reflected = ImportedSvgRasterizer.Rasterize(Svg("point-tile", "-8 0 0 8 16 0"), 32, 16);
        AssertTimeline(reflected.Pixels[4 * reflected.Stride + 2 * 4] == 255
            && reflected.Pixels[4 * reflected.Stride + 10 * 4 + 2] == 255,
            "A reflected bitmap fill lost its affine transform.");
        var clamped = ImportedSvgRasterizer.Rasterize(Svg("point-clamp", "4 0 0 4 12 4"), 32, 16);
        AssertTimeline(clamped.Pixels[8 * clamped.Stride + 2 * 4 + 2] == 255
            && clamped.Pixels[8 * clamped.Stride + 26 * 4] == 255,
            "Clipped bitmap fill repeated pixels instead of extending its edge colours.");
        var clampLinear = ImportedSvgRasterizer.Rasterize(Svg("bilinear-clamp", "4 0 0 4 12 4"), 32, 16);
        AssertTimeline(clampLinear.Pixels[8 * clampLinear.Stride + 2 * 4 + 2] == 255
            && clampLinear.Pixels[8 * clampLinear.Stride + 26 * 4] == 255,
            "Clipped bilinear sampling mixed the opposite edge of the texture.");
        var linear = ImportedSvgRasterizer.Rasterize(Svg("bilinear-tile", "8 0 0 8 0 0"), 32, 16);
        var middle = 4 * linear.Stride + 7 * 4;
        AssertTimeline(linear.Pixels[middle + 2] == 143 && linear.Pixels[middle] == 112 && linear.Pixels[middle + 3] == 255,
            "Bilinear bitmap fill weights or texel centres differ.");
        var faded = ImportedSvgRasterizer.Rasterize(Svg("point-tile", "8 0 0 8 0 0", "0.5"), 32, 16);
        var opaque = 4 * faded.Stride + 2 * 4;
        AssertTimeline(faded.Pixels[opaque + 2] == 128 && faded.Pixels[opaque + 3] == 128,
            "Bitmap fill opacity was omitted or multiplied twice.");
        var clippedSource = Svg("point-tile", "8 0 0 8 0 0").Replace("</defs>",
            "<clipPath id='clip'><rect x='9' y='0' width='7' height='16'/></clipPath></defs>", StringComparison.Ordinal)
            .Replace("<path ", "<path clip-path='url(#clip)' ", StringComparison.Ordinal);
        var clipped = ImportedSvgRasterizer.Rasterize(clippedSource, 32, 16);
        AssertTimeline(clipped.Pixels[4 * clipped.Stride + 4 * 4 + 3] == 0
            && clipped.Pixels[4 * clipped.Stride + 11 * 4] == 255
            && clipped.Pixels[4 * clipped.Stride + 20 * 4 + 3] == 0,
            "Vector clipping shifted the bitmap's sample origin or was omitted.");
        tile.SetPixel(0, 0, Color.FromArgb(128, 1, 0, 0));
        tile.SetPixel(0, 1, Color.FromArgb(128, 1, 0, 0));
        bytes.SetLength(0);
        tile.Save(bytes, ImageFormat.Png);
        uri = "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
        var partial = ImportedSvgRasterizer.Rasterize(Svg("point-tile", "8 0 0 8 0 0"), 32, 16);
        AssertTimeline(partial.Pixels[opaque + 2] == 1 && partial.Pixels[opaque + 3] == 128,
            "Point sampling changed stored alpha or rounded premultiplied channels.");
        var reusedSource = Svg("point-tile", "8 0 0 8 0 0").Replace("<path d=", "<path id='painted' d=", StringComparison.Ordinal)
            .Replace("</svg>", "<use xlink:href='#painted' transform='translate(16 0)'/></svg>", StringComparison.Ordinal);
        var reused = ImportedSvgRasterizer.Rasterize(reusedSource, 32, 16);
        AssertTimeline(reused.Pixels[4 * reused.Stride + 18 * 4 + 2] > 0
            && reused.Pixels[4 * reused.Stride + 18 * 4 + 3] > 128,
            "Reusing a sampled bitmap path lost its decoded pixels or sampling type.");
        Console.WriteLine("flash_bitmap_point_bilinear_tile_reflection_opacity=ok");
    }
}
