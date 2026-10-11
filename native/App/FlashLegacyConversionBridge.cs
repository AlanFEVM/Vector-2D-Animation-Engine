using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace VectorAnimationEngine;

internal sealed record FlashLegacyConversionPlan(
    string WorkingSourcePath,
    string ScriptPath,
    string ExpectedFlaPath,
    string CompletionPath);

/// <summary>
/// Prepares a one-way migration through an installed Adobe authoring application.
/// This is not a binary FLA decoder, and preparing a plan never launches Adobe.
/// </summary>
internal static class FlashLegacyConversionBridge
{
    private const long MaximumFlaBytes = 512L * 1024 * 1024;
    private const long MaximumDocumentBytes = 32L * 1024 * 1024;
    private const long MaximumStatusBytes = 64L * 1024;
    private const string SourceName = "legacy-source.fla";
    private const string ScriptName = "convert.jsfl";
    private const string OutputName = "converted.fla";
    private const string StatusName = "conversion-status.txt";
    private const string XflNamespace = "http://ns.adobe.com/xfl/2008/";
    private static readonly byte[] OleSignature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsLegacyBinaryFla(string path)
    {
        try
        {
            var fullPath = RequireAbsolutePath(path, nameof(path));
            if (!string.Equals(Path.GetExtension(fullPath), ".fla", StringComparison.OrdinalIgnoreCase)) return false;
            EnsureSafePath(fullPath);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 8 or > MaximumFlaBytes) return false;
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            return signature.SequenceEqual(OleSignature);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static FlashLegacyConversionPlan Prepare(string sourceFlaPath, string outputDirectory)
    {
        var source = RequireAbsolutePath(sourceFlaPath, nameof(sourceFlaPath));
        if (!string.Equals(Path.GetExtension(source), ".fla", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The legacy source must be a .fla file.", nameof(sourceFlaPath));
        }
        EnsureSafePath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("The legacy FLA source does not exist.", source);

        // Keep the source open without write sharing until the verified bytes have been copied.
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (sourceStream.Length is < 512 or > MaximumFlaBytes)
        {
            throw new InvalidDataException($"The binary FLA source must be between 512 and {MaximumFlaBytes} bytes.");
        }
        Span<byte> signature = stackalloc byte[8];
        sourceStream.ReadExactly(signature);
        if (!signature.SequenceEqual(OleSignature))
        {
            throw new InvalidDataException("Only legacy OLE compound-document FLA files can use this conversion bridge.");
        }
        sourceStream.Position = 0;

        var directory = RequireAbsolutePath(outputDirectory, nameof(outputDirectory));
        EnsureSafePath(directory);
        if (File.Exists(directory)) throw new IOException("The conversion destination must be a directory.");
        Directory.CreateDirectory(directory);
        EnsureSafePath(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("The conversion destination must be empty; existing files will not be overwritten.");
        }

        var plan = new FlashLegacyConversionPlan(
            Path.Combine(directory, SourceName),
            Path.Combine(directory, ScriptName),
            Path.Combine(directory, OutputName),
            Path.Combine(directory, StatusName));
        using (var copy = new FileStream(plan.WorkingSourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            sourceStream.CopyTo(copy);
            copy.Flush(true);
        }
        WriteNewText(plan.CompletionPath, "PENDING\nRun convert.jsfl using Commands > Run Command in Flash CS5 or later / Adobe Animate.\n");
        WriteNewText(plan.ScriptPath, CreateScript(plan));
        return plan;
    }

    internal static bool TryGetCompletedPath(FlashLegacyConversionPlan plan, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        try
        {
            ArgumentNullException.ThrowIfNull(plan);
            ValidatePlan(plan);
            if (!File.Exists(plan.CompletionPath))
            {
                error = "The Adobe conversion has not reported a result. Run convert.jsfl using Commands > Run Command.";
                return false;
            }
            var statusInfo = new FileInfo(plan.CompletionPath);
            if (statusInfo.Length is <= 0 or > MaximumStatusBytes)
            {
                throw new InvalidDataException("The Adobe conversion status file is empty or too large.");
            }
            var status = File.ReadAllText(plan.CompletionPath, StrictUtf8).TrimStart('\ufeff');
            var firstLineEnd = status.IndexOf('\n');
            var state = (firstLineEnd < 0 ? status : status[..firstLineEnd]).TrimEnd('\r');
            if (!string.Equals(state, "SUCCEEDED", StringComparison.Ordinal))
            {
                error = state switch
                {
                    "PENDING" => "The Adobe conversion is pending. Run convert.jsfl in Flash CS5 or later / Adobe Animate.",
                    "RUNNING" => "Adobe has started conversion but has not completed it. Check Adobe for an open-file error, dialog, or cancelled script.",
                    "FAILED" => "Adobe conversion failed: " + (firstLineEnd < 0 ? "No details were reported." : status[(firstLineEnd + 1)..].Trim()),
                    _ => "The Adobe conversion reported an unrecognized status."
                };
                return false;
            }
            ValidateModernFla(plan.ExpectedFlaPath);
            path = plan.ExpectedFlaPath;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or XmlException or DecoderFallbackException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string CreateScript(FlashLegacyConversionPlan plan)
    {
        // JSON literals are also valid ES3 strings: quotes, backslashes, control
        // characters and Unicode separators cannot become executable JSFL code.
        var source = JsonSerializer.Serialize(plan.WorkingSourcePath);
        var output = JsonSerializer.Serialize(plan.ExpectedFlaPath);
        var completion = JsonSerializer.Serialize(plan.CompletionPath);
        return $$"""
            // Vector 2D: migrate only the isolated legacy source copy.
            // Run with Commands > Run Command in Flash CS5+ or Adobe Animate.
            // This command does not publish, test a movie, or evaluate ActionScript.
            (function () {
                var sourceURI = FLfile.platformPathToURI({{source}});
                var outputURI = FLfile.platformPathToURI({{output}});
                var statusURI = FLfile.platformPathToURI({{completion}});
                var doc = null;
                var previous = fl.getDocumentDOM();
                var version = String(fl.version);
                function report(state, message) {
                    var text = state + "\n" + message + "\nAdobe: " + version + "\n";
                    if (!FLfile.write(statusURI, text)) {
                        throw new Error("Cannot write conversion-status.txt; check directory permissions.");
                    }
                    fl.trace(text);
                }
                try {
                    report("RUNNING", "Opening the isolated legacy FLA copy.");
                    var major = version.match(/(\d+)[,.]/);
                    if (!major || Number(major[1]) < 11) {
                        throw new Error("Flash CS5 (version 11) or later / Adobe Animate is required for ZIP-based FLA output.");
                    }
                    if (FLfile.exists(outputURI)) {
                        throw new Error("converted.fla already exists. Prepare a new empty conversion folder; no existing output will be overwritten.");
                    }
                    if (!FLfile.exists(sourceURI)) {
                        throw new Error("The isolated legacy-source.fla copy is missing.");
                    }
                    doc = fl.openDocument(sourceURI);
                    if (!doc) throw new Error("Adobe could not open the legacy FLA copy.");
                    doc.livePreview = false;
                    if (!fl.saveDocument(doc, outputURI) || !FLfile.exists(outputURI)) {
                        throw new Error("Adobe did not save converted.fla successfully.");
                    }
                    fl.closeDocument(doc, false);
                    doc = null;
                    if (previous) fl.setActiveWindow(previous);
                    report("SUCCEEDED", "Saved converted.fla. Import this file in Vector 2D and review the migration report. Adobe compatibility changes may affect older features.");
                } catch (failure) {
                    var detail = String(failure);
                    try { if (doc) fl.closeDocument(doc, false); } catch (closeFailure) { detail += "\nClose: " + String(closeFailure); }
                    try { if (previous) fl.setActiveWindow(previous); } catch (restoreFailure) { detail += "\nRestore: " + String(restoreFailure); }
                    try { report("FAILED", detail); } catch (statusFailure) { fl.trace(detail + "\n" + String(statusFailure)); }
                    alert("Legacy FLA migration failed.\n" + detail + "\nCheck conversion-status.txt and Adobe's Output panel.");
                }
            }());
            """;
    }

    private static void ValidateModernFla(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Adobe reported success but converted.fla is missing.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 4 or > MaximumFlaBytes)
        {
            throw new InvalidDataException("The converted FLA is empty or exceeds the conversion size limit.");
        }
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 }))
        {
            throw new InvalidDataException("Adobe output is not a modern ZIP-based FLA. Use Flash CS5 or later / Adobe Animate.");
        }
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 20_000) throw new InvalidDataException("The converted FLA contains too many ZIP entries.");
        var documents = archive.Entries.Where(entry => string.Equals(entry.FullName, "DOMDocument.xml", StringComparison.Ordinal)).ToArray();
        if (documents.Length != 1 || documents[0].Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The converted FLA must contain one bounded root DOMDocument.xml.");
        }
        using var xmlStream = documents[0].Open();
        using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentBytes
        });
        reader.MoveToContent();
        if (reader.LocalName != "DOMDocument" || reader.NamespaceURI != XflNamespace)
        {
            throw new InvalidDataException("The converted FLA does not contain an Adobe XFL document.");
        }
        while (reader.Read())
        {
            if (reader.Depth > 256) throw new InvalidDataException("The converted XFL document is nested too deeply.");
        }
        // The importer separately validates the full archive and feature support.
    }

    private static void ValidatePlan(FlashLegacyConversionPlan plan)
    {
        var paths = new[] { plan.WorkingSourcePath, plan.ScriptPath, plan.ExpectedFlaPath, plan.CompletionPath };
        var names = new[] { SourceName, ScriptName, OutputName, StatusName };
        var directory = Path.GetDirectoryName(RequireAbsolutePath(paths[0], nameof(plan)));
        for (var index = 0; index < paths.Length; index++)
        {
            var fullPath = RequireAbsolutePath(paths[index], nameof(plan));
            if (!string.Equals(Path.GetDirectoryName(fullPath), directory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullPath), names[index], StringComparison.Ordinal))
            {
                throw new ArgumentException("Conversion plan files must use the prepared names in one directory.", nameof(plan));
            }
            EnsureSafePath(fullPath);
        }
    }

    private static string RequireAbsolutePath(string path, string parameter)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 1024)
        {
            throw new ArgumentException("Conversion paths must be absolute and at most 1024 characters long.", parameter);
        }
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 1024) throw new ArgumentException("The normalized conversion path is too long.", parameter);
        return fullPath;
    }

    private static void EnsureSafePath(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Conversion paths cannot contain symbolic links or reparse points.");
                }
            }
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static void WriteNewText(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = StrictUtf8.GetBytes(text);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
