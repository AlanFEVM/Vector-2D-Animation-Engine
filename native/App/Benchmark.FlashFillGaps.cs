namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashFillGapToleranceRegression(string root)
    {
        var path = Path.Combine(root, "FillGapTolerance.fla");
        foreach (var gap in new[] { 0, 10, 80, 81 })
        {
            var symbol = FlashSymbolFixture().Replace("|0 200|0 0", $"|0 200|0 {gap}", StringComparison.Ordinal);
            WriteFlashZipFixture(path, ("DOMDocument.xml", FlashAnimationFixture()), ("LIBRARY/Leaf.xml", symbol));
            if (gap > 80)
                ExpectInvalidData(() => FlashDocumentImporter.Load(path), "Over-tolerance fill gap was accepted.");
            else
            {
                var result = FlashDocumentImporter.Load(path);
                AssertTimeline(result.Diagnostics.Any(item => item.Code == "fill-subpixel-gaps-repaired") == (gap != 0),
                    "Fill repair diagnostic was missing or emitted for a closed contour.");
            }
        }
        Console.WriteLine("flash_fill_gap_exact_subpixel_limit_rejection=ok");
    }
}
