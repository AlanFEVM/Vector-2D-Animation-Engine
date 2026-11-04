namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashEmptyLibraryTimelineRegression(string root)
    {
        var path = Path.Combine(root, "EmptyLibraryTimeline.fla");
        var document = $"<DOMDocument xmlns='{FlashFixtureNamespace}'><symbols><Include href='Empty.xml'/></symbols><timelines><DOMTimeline name='Scene'><layers><DOMLayer><frames><DOMFrame><elements><DOMSymbolInstance libraryItemName='Empty'/></elements></DOMFrame></frames></DOMLayer></layers></DOMTimeline></timelines></DOMDocument>";
        var symbol = $"<DOMSymbolItem xmlns='{FlashFixtureNamespace}' name='Empty' symbolType='graphic'><timeline><DOMTimeline name='Empty'><layers/></DOMTimeline></timeline></DOMSymbolItem>";
        WriteFlashZipFixture(path, ("DOMDocument.xml", document), ("LIBRARY/Empty.xml", symbol));
        var imported = FlashDocumentImporter.Load(path);
        var empty = imported.Project.DrawingObjects.Single(item => item.Name == "Empty");
        AssertTimeline(empty.Scene.ObjectCount == 0, "An empty Flash symbol acquired visible geometry.");
        var saved = ProjectVaultStore.Save(imported.Project, Path.Combine(root, "EmptyLibrarySaved", "Empty.v2dProject"));
        var reopened = ProjectVaultStore.Load(saved);
        AssertTimeline(reopened.DrawingObjects.Any(item => item.Id == empty.Id && item.Scene.ObjectCount == 0),
            "An empty Flash library symbol lost its identity after Save/Open.");
        Console.WriteLine("flash_empty_library_timeline_save_reopen=ok");

        foreach (var folder in new[] { "Sprites ", "Misc." })
        {
            var archive = Path.Combine(root, "LogicalPath-" + folder.Trim().TrimEnd('.') + ".fla");
            var logicalDocument = document.Replace("href='Empty.xml'", $"href='{folder}/Empty.xml'", StringComparison.Ordinal);
            WriteFlashZipFixture(archive, ("DOMDocument.xml", logicalDocument),
                ($"LIBRARY/{folder}/Empty.xml", symbol));
            AssertTimeline(FlashDocumentImporter.Load(archive).Project.DrawingObjects.Any(item => item.Name == "Empty"),
                "An exact ZIP library key with a trailing space or dot was rejected.");
            var disk = Path.Combine(root, "DiskPath-" + folder.Trim().TrimEnd('.'));
            Directory.CreateDirectory(disk);
            File.WriteAllText(Path.Combine(disk, "DOMDocument.xml"), logicalDocument);
            ExpectInvalidData(() => FlashDocumentImporter.Load(disk),
                "A Windows-normalized library path was accepted from an uncompressed XFL source.");
        }
        Console.WriteLine("flash_zip_logical_paths_disk_suffix_rejection=ok");

        var casePath = Path.Combine(root, "CaseDistinctLibrary.fla");
        var caseDocument = document.Replace("<Include href='Empty.xml'/>",
            "<Include href='Empty.xml'/><Include href='empty.xml'/>", StringComparison.Ordinal);
        WriteFlashZipFixture(casePath, ("DOMDocument.xml", caseDocument),
            ("LIBRARY/Empty.xml", symbol), ("LIBRARY/empty.xml", symbol.Replace("name='Empty'", "name='empty'", StringComparison.Ordinal)));
        var caseProject = FlashDocumentImporter.Load(casePath).Project;
        AssertTimeline(caseProject.DrawingObjects.Any(item => item.Name == "Empty")
            && caseProject.DrawingObjects.Any(item => item.Name == "empty"),
            "Case-distinct ZIP library paths were merged or rejected.");
        Console.WriteLine("flash_zip_case_distinct_library=ok");
        var lowerRoot = Path.Combine(root, "LowercaseRoot.fla");
        WriteFlashZipFixture(lowerRoot, ("domdocument.xml", document), ("LIBRARY/Empty.xml", symbol));
        AssertTimeline(FlashDocumentImporter.Load(lowerRoot).Project.DrawingObjects.Any(item => item.Name == "Empty"),
            "Case-sensitive library lookup broke case-insensitive root document discovery.");

        const string escapedInclude = "<Include href='A&amp;#038B.xml' itemID='stable-symbol-id'/>";
        var escapedDocument = document.Replace("<Include href='Empty.xml'/>", escapedInclude, StringComparison.Ordinal);
        var identifiedSymbol = symbol.Replace("name='Empty'", "name='Empty' itemID='stable-symbol-id'", StringComparison.Ordinal);
        var escapedPath = Path.Combine(root, "AdobeEscapedLibrary.fla");
        WriteFlashZipFixture(escapedPath, ("DOMDocument.xml", escapedDocument), ("LIBRARY/A&B.xml", identifiedSymbol));
        var escaped = FlashDocumentImporter.Load(escapedPath);
        AssertTimeline(escaped.Project.DrawingObjects.Any(item => item.Name == "Empty")
            && escaped.Diagnostics.Any(item => item.Code == "library-path-escape-resolved"),
            "An Adobe-escaped Include with a matching itemID was not resolved.");
        var escapedSaved = ProjectVaultStore.Save(escaped.Project, Path.Combine(root, "AdobeEscapedSaved", "Escaped.v2dProject"));
        AssertTimeline(ProjectVaultStore.Load(escapedSaved).DrawingObjects.Any(item => item.Name == "Empty"),
            "A resolved Adobe library symbol was lost after Save/Open.");
        var exactPath = Path.Combine(root, "AdobeEscapedExactPrecedence.fla");
        WriteFlashZipFixture(exactPath, ("DOMDocument.xml", escapedDocument), ("LIBRARY/A&#038B.xml", identifiedSymbol),
            ("LIBRARY/A&B.xml", identifiedSymbol.Replace("name='Empty'", "name='Unexpected'", StringComparison.Ordinal)));
        AssertTimeline(!FlashDocumentImporter.Load(exactPath).Diagnostics.Any(item => item.Code == "library-path-escape-resolved"),
            "An exact library path was replaced by a decoded alias.");
        var mismatchedPath = Path.Combine(root, "AdobeEscapedMismatchedId.fla");
        WriteFlashZipFixture(mismatchedPath, ("DOMDocument.xml", escapedDocument),
            ("LIBRARY/A&B.xml", identifiedSymbol.Replace("stable-symbol-id", "different-symbol-id", StringComparison.Ordinal)));
        ExpectInvalidData(() => FlashDocumentImporter.Load(mismatchedPath), "An escaped path adopted a symbol with a different itemID.");
        var unidentifiedPath = Path.Combine(root, "AdobeEscapedMissingId.fla");
        WriteFlashZipFixture(unidentifiedPath, ("DOMDocument.xml", escapedDocument.Replace(" itemID='stable-symbol-id'", "", StringComparison.Ordinal)),
            ("LIBRARY/A&B.xml", identifiedSymbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(unidentifiedPath), "An escaped path was adopted without an Include itemID.");
        var recursivePath = Path.Combine(root, "AdobeEscapedRecursive.fla");
        WriteFlashZipFixture(recursivePath, ("DOMDocument.xml", escapedDocument.Replace("A&amp;#038B", "A&amp;#038#038B", StringComparison.Ordinal)),
            ("LIBRARY/A&B.xml", identifiedSymbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(recursivePath), "A library path was repeatedly decoded until an unrelated key matched.");
        Console.WriteLine("flash_adobe_library_escape_identity_exact_precedence_save_reopen=ok");

        var stalePath = Path.Combine(root, "StaleLibraryInclude.fla");
        var staleDocument = document.Replace("<Include href='Empty.xml'/>",
            "<Include href='Empty.xml'/><Include href='Unavailable.xml' itemID='obsolete-id'/>", StringComparison.Ordinal);
        var visibleSymbol = symbol.Replace("<layers/>", "<layers><DOMLayer><frames><DOMFrame><elements>"
            + FlashSquareFixture("#FF0000") + "</elements></DOMFrame></frames></DOMLayer></layers>", StringComparison.Ordinal);
        WriteFlashZipFixture(stalePath, ("DOMDocument.xml", staleDocument), ("LIBRARY/Empty.xml", visibleSymbol));
        var stale = FlashDocumentImporter.Load(stalePath);
        AssertTimeline(stale.Diagnostics.Any(item => item.Code == "library-stale-include" && item.Location == "Unavailable.xml")
            && !stale.Project.DrawingObjects.Any(item => item.Name == "Unavailable"),
            "An obsolete Include was hidden or replaced with fabricated artwork.");
        var staleSaved = ProjectVaultStore.Save(stale.Project, Path.Combine(root, "StaleIncludeSaved", "Stale.v2dProject"));
        var staleReopened = ProjectVaultStore.Load(staleSaved);
        var staleComposition = new VectorScene();
        SceneCompositionBuilder.Build(staleComposition, staleReopened.Scenes[0], staleReopened.DrawingObjects, 0, stale.Fps);
        AssertTimeline(staleReopened.DrawingObjects.Any(item => item.Name == "Empty") && staleComposition.ObjectCount == 1,
            "Available artwork was lost while importing an obsolete Include and saving/reopening it.");
        var missingReferencedPath = Path.Combine(root, "MissingReferencedLibrary.fla");
        WriteFlashZipFixture(missingReferencedPath, ("DOMDocument.xml", staleDocument.Replace("libraryItemName='Empty'", "libraryItemName='Unavailable'", StringComparison.Ordinal)),
            ("LIBRARY/Empty.xml", symbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(missingReferencedPath), "Missing referenced artwork was silently omitted with an obsolete Include.");
        var missingNestedPath = Path.Combine(root, "MissingUnplacedNestedLibrary.fla");
        var nestedSymbol = symbol.Replace("<layers/>",
            "<layers><DOMLayer><frames><DOMFrame><elements><DOMSymbolInstance libraryItemName='Unavailable'/></elements></DOMFrame></frames></DOMLayer></layers>", StringComparison.Ordinal);
        WriteFlashZipFixture(missingNestedPath, ("DOMDocument.xml", staleDocument.Replace("<DOMSymbolInstance libraryItemName='Empty'/>", "", StringComparison.Ordinal)),
            ("LIBRARY/Empty.xml", nestedSymbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(missingNestedPath), "A missing reference inside an unplaced library symbol was omitted.");
        var unsafeStalePath = Path.Combine(root, "UnsafeStaleInclude.fla");
        WriteFlashZipFixture(unsafeStalePath, ("DOMDocument.xml", staleDocument.Replace("href='Unavailable.xml'", "href='../Unavailable.xml'", StringComparison.Ordinal)),
            ("LIBRARY/Empty.xml", visibleSymbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(unsafeStalePath), "An unsafe missing Include path bypassed path validation.");
        var excessiveStalePath = Path.Combine(root, "ExcessiveStaleIncludes.fla");
        var excessiveStaleDocument = document.Replace("<Include href='Empty.xml'/>", "<Include href='Empty.xml'/>"
            + string.Concat(Enumerable.Repeat("<Include href='Unavailable.xml'/>", 16384)), StringComparison.Ordinal);
        WriteFlashZipFixture(excessiveStalePath, ("DOMDocument.xml", excessiveStaleDocument), ("LIBRARY/Empty.xml", visibleSymbol));
        ExpectInvalidData(() => FlashDocumentImporter.Load(excessiveStalePath), "Unavailable Includes bypassed the symbol-count limit.");
        Console.WriteLine("flash_stale_include_complete_reference_validation_save_reopen=ok");

        var tinyPath = Path.Combine(root, "TinyFill.fla");
        var tinyShape = FlashSquareFixture("#FF0000").Replace("200", "0.2", StringComparison.Ordinal);
        var tinyDocument = document.Replace("<symbols><Include href='Empty.xml'/></symbols>", "", StringComparison.Ordinal)
            .Replace("<DOMSymbolInstance libraryItemName='Empty'/>", tinyShape, StringComparison.Ordinal);
        WriteFlashZipFixture(tinyPath, ("DOMDocument.xml", tinyDocument));
        var tiny = FlashDocumentImporter.Load(tinyPath);
        AssertTimeline(tiny.Diagnostics.Any(item => item.Code == "tiny-shape-native-precision"),
            "A sub-unit fill did not disclose its native coordinate approximation.");
        var tinySaved = ProjectVaultStore.Save(tiny.Project, Path.Combine(root, "TinySaved", "Tiny.v2dProject"));
        var tinyReopened = ProjectVaultStore.Load(tinySaved);
        var composed = new VectorScene();
        SceneCompositionBuilder.Build(composed, tinyReopened.Scenes[0], tinyReopened.DrawingObjects, 0, tiny.Fps);
        AssertTimeline(composed.ObjectCount == 1, "A retained tiny fill disappeared after Save/Open/composition.");
        AssertTimeline(composed.ShapeKind[0] != ShapeKind.ImportedSvg, "Tiny artwork retained an SVG wrapper.");
        Console.WriteLine("flash_tiny_fill_native_precision=ok");

        var transparentPath = Path.Combine(root, "TransparentFill.fla");
        var transparentShape = FlashSquareFixture("#FF0000").Replace("<SolidColor ", "<SolidColor alpha='0' ", StringComparison.Ordinal);
        WriteFlashZipFixture(transparentPath, ("DOMDocument.xml", tinyDocument.Replace(tinyShape, transparentShape, StringComparison.Ordinal)));
        var transparent = FlashDocumentImporter.Load(transparentPath);
        var transparentSaved = ProjectVaultStore.Save(transparent.Project, Path.Combine(root, "TransparentSaved", "Transparent.v2dProject"));
        var transparentReopened = ProjectVaultStore.Load(transparentSaved);
        SceneCompositionBuilder.Build(composed, transparentReopened.Scenes[0], transparentReopened.DrawingObjects, 0, transparent.Fps);
        AssertTimeline(transparentReopened.Scenes[0].DrawingScene.ObjectCount == 1
            && Color.FromArgb(transparentReopened.Scenes[0].DrawingScene.Argb[0]).A == 0
            && transparentReopened.Scenes[0].DrawingScene.ShapeKind[0] != ShapeKind.ImportedSvg,
            "A fully transparent native fill lost its editable geometry or alpha after Save/Open.");
        Console.WriteLine("flash_transparent_shape_native=ok");
    }
}
