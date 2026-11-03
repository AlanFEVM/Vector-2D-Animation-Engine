namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashRepeatedIncludeRegression(string root)
    {
        var document = FlashAnimationFixture().Replace("<Include href=\"Leaf.xml\"/>",
            "<Include href=\"Leaf.xml\" itemID=\"obsolete-id\"/><Include href=\"Leaf.xml\" itemID=\"current-id\"/>",
            StringComparison.Ordinal);
        AssertTimeline(document.Contains("obsolete-id", StringComparison.Ordinal), "Repeated Include fixture was not applied.");
        var symbol = FlashSymbolFixture().Replace("name=\"Leaf\"", "name=\"Leaf\" itemID=\"current-id\"", StringComparison.Ordinal);
        var path = Path.Combine(root, "RepeatedLibraryInclude.fla");
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Leaf.xml", symbol));
        var original = File.ReadAllBytes(path);
        var imported = FlashDocumentImporter.Load(path);
        AssertFlashAnimationProject(imported.Project, requireEditableGeometry: true);
        AssertTimeline(imported.Project.DrawingObjects.Count(item => item.Name == "Leaf") == 1
            && imported.Diagnostics.Count(item => item.Code == "library-repeated-include") == 1
            && imported.Diagnostics.Single(item => item.Code == "library-repeated-include").Message.Contains("current-id", StringComparison.Ordinal)
            && imported.Diagnostics.Single(item => item.Code == "library-repeated-include").Message.Contains("obsolete-id", StringComparison.Ordinal),
            "A repeated Include duplicated the library symbol or lost its identity diagnostic.");
        var ids = imported.Project.DrawingObjects.Select(item => item.Id).ToArray();
        var manifest = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "RepeatedIncludeSaved", "Repeated.v2dProject"));
        var reopened = ProjectVaultStore.Load(manifest);
        AssertFlashAnimationProject(reopened, requireEditableGeometry: true);
        AssertTimeline(ids.SequenceEqual(reopened.DrawingObjects.Select(item => item.Id))
            && File.ReadAllBytes(path).SequenceEqual(original), "Repeated Include Save/Open changed stable IDs or source bytes.");

        var conflict = Path.Combine(root, "ConflictingLibraryName.fla");
        var distinctPaths = document.Replace("href=\"Leaf.xml\" itemID=\"current-id\"", "href=\"Other.xml\" itemID=\"current-id\"", StringComparison.Ordinal);
        WriteFlashZipFixture(conflict, ("DOMDocument.xml", distinctPaths), ("LIBRARY/Leaf.xml", symbol), ("LIBRARY/Other.xml", symbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(conflict), "Different library paths with duplicate names were silently merged.");
        WriteFlashZipFixture(conflict, ("DOMDocument.xml", distinctPaths), ("LIBRARY/Leaf.xml", symbol),
            ("LIBRARY/Other.xml", symbol.Replace("#FF0000", "#0000FF", StringComparison.Ordinal)));
        ExpectInvalidData(() => FlashDocumentImporter.Load(conflict), "Conflicting named library artwork was silently selected.");
        Console.WriteLine("flash_repeated_include_identity_save_reopen_distinct_path_rejection=ok");
    }
}
