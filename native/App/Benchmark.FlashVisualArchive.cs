namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFlashVisualArchiveRegression(string root, string document, string symbol)
    {
        var index = 0;
        // Visual baking must accept authoring features that native parsing rejects.
        var foreignText = document.Replace("</DOMDocument>",
            "<metadata><TextFlow xmlns='http://ns.adobe.com/textLayout/2008'><p>Visual text</p></TextFlow></metadata></DOMDocument>",
            StringComparison.Ordinal);
        foreach (var rootName in new[] { "domdocument.XML", "Authoring/DOMDocument.xml" })
        {
            var source = Path.Combine(root, $"VisualArchiveRoot{index}.fla");
            var libraryPath = rootName.StartsWith("Authoring/", StringComparison.Ordinal)
                ? "Authoring/LIBRARY/Leaf.xml" : "LIBRARY/Leaf.xml";
            WriteFlashZipFixture(source, (rootName, foreignText), (libraryPath, symbol));
            var original = File.ReadAllBytes(source);
            var plan = FlashVisualConversionBridge.Prepare(source, Path.Combine(root, $"VisualArchiveRoot{index++}"));
            AssertTimeline(File.ReadAllBytes(source).SequenceEqual(original)
                && File.ReadAllBytes(plan.WorkingSourcePath).SequenceEqual(original)
                && File.Exists(plan.ScriptPath),
                "The visual bridge failed to preserve a discovered XFL root or rejected foreign authoring namespaces.");
        }

        var duplicate = Path.Combine(root, "VisualArchiveDuplicateRoot.fla");
        WriteFlashZipFixture(duplicate, ("DOMDocument.xml", document), ("domdocument.XML", document),
            ("LIBRARY/Leaf.xml", symbol));
        Reject(duplicate, "VisualDuplicateRoot", "The visual bridge accepted ambiguous XFL roots.");
        var duplicatePath = Path.Combine(root, "VisualArchiveDuplicatePath.fla");
        WriteFlashZipFixture(duplicatePath, ("DOMDocument.xml", document),
            ("LIBRARY/Leaf.xml", symbol), ("LIBRARY/Leaf.xml", symbol));
        Reject(duplicatePath, "VisualDuplicatePath", "The visual bridge accepted duplicate archive paths.");
        var dtd = Path.Combine(root, "VisualArchiveDtd.fla");
        WriteFlashZipFixture(dtd, ("DOMDocument.xml", "<!DOCTYPE DOMDocument [<!ENTITY text 'not resolved'>]>" + document),
            ("LIBRARY/Leaf.xml", symbol));
        Reject(dtd, "VisualDtd", "The visual bridge accepted an XFL document containing a DTD.");
        Console.WriteLine("flash_visual_archive_roots_foreign_namespaces_ambiguity_dtd=ok");

        void Reject(string source, string folder, string message)
        {
            var destination = Path.Combine(root, folder);
            ExpectInvalidData(() => FlashVisualConversionBridge.Prepare(source, destination), message);
            AssertTimeline(!Directory.Exists(destination), "A rejected visual archive left bridge output behind.");
        }
    }
}
