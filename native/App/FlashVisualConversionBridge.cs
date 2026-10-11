using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace VectorAnimationEngine;

internal sealed record FlashVisualConversionPlan(
    string WorkingSourcePath,
    string ScriptPath,
    string ExpectedManifestPath,
    string CompletionPath);

/// <summary>Creates an Adobe-operated visual bake, not a lossless document conversion.</summary>
internal static class FlashVisualConversionBridge
{
    private const long MaximumFlaBytes = 512L * 1024 * 1024;
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const long MaximumSvgBytes = 8L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] OleSignature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];

    internal static FlashVisualConversionPlan Prepare(string sourceFlaPath, string outputDirectory)
    {
        var source = Absolute(sourceFlaPath);
        if (!Path.GetExtension(source).Equals(".fla", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The visual bridge source must be a .fla file.", nameof(sourceFlaPath));
        SafePath(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 512 or > MaximumFlaBytes) throw new InvalidDataException("FLA source must be between 512 bytes and 512 MiB.");
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual(OleSignature))
        {
            if (!signature[..4].SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 }))
                throw new InvalidDataException("The visual bridge source must be an OLE binary FLA or an XFL ZIP FLA.");
            input.Position = 0;
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 20_000) throw new InvalidDataException("The FLA archive contains too many entries.");
            var documents = archive.Entries.Where(entry => entry.FullName == "DOMDocument.xml").ToArray();
            if (documents.Length != 1 || documents[0].Length is <= 0 or > 32L * 1024 * 1024)
                throw new InvalidDataException("The FLA ZIP must contain one bounded root DOMDocument.xml.");
            using var xml = documents[0].Open();
            using var reader = XmlReader.Create(xml, XmlSettings(32L * 1024 * 1024));
            reader.MoveToContent();
            if (reader.LocalName != "DOMDocument" || reader.NamespaceURI != "http://ns.adobe.com/xfl/2008/")
                throw new InvalidDataException("The FLA ZIP does not contain an Adobe XFL document.");
            while (reader.Read()) if (reader.Depth > 256) throw new InvalidDataException("XFL XML depth exceeds 256.");
        }
        input.Position = 0;

        var directory = Absolute(outputDirectory);
        SafePath(directory);
        if (File.Exists(directory)) throw new IOException("The visual bridge destination must be a directory.");
        Directory.CreateDirectory(directory);
        SafePath(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("The visual bridge destination must be empty; existing files will not be overwritten.");
        var plan = new FlashVisualConversionPlan(
            Path.Combine(directory, "source-copy.fla"),
            Path.Combine(directory, "export-frames.jsfl"),
            Path.Combine(directory, "manifest.json"),
            Path.Combine(directory, "visual-conversion-status.txt"));
        using (var copy = new FileStream(plan.WorkingSourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(copy);
            copy.Flush(true);
        }
        WriteNew(plan.CompletionPath, "PENDING\nRun export-frames.jsfl through Commands > Run Command in Adobe Animate 2020 or later.\n");
        WriteNew(plan.ScriptPath, Script(plan));
        return plan;
    }

    internal static bool TryGetCompletedPath(FlashVisualConversionPlan plan, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        try
        {
            ArgumentNullException.ThrowIfNull(plan);
            var paths = new[] { plan.WorkingSourcePath, plan.ScriptPath, plan.ExpectedManifestPath, plan.CompletionPath };
            var names = new[] { "source-copy.fla", "export-frames.jsfl", "manifest.json", "visual-conversion-status.txt" };
            var directory = Path.GetDirectoryName(Absolute(paths[0]));
            for (var index = 0; index < paths.Length; index++)
            {
                var fullPath = Absolute(paths[index]);
                if (Path.GetFileName(fullPath) != names[index]
                    || !string.Equals(Path.GetDirectoryName(fullPath), directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Visual bridge files must use the prepared names in one directory.");
                SafePath(fullPath);
            }
            if (!File.Exists(plan.CompletionPath)) throw new InvalidDataException("Adobe has not reported a visual export result.");
            if (new FileInfo(plan.CompletionPath).Length is <= 0 or > 64 * 1024)
                throw new InvalidDataException("The visual export status file is empty or too large.");
            var status = File.ReadAllText(plan.CompletionPath, StrictUtf8).TrimStart('\ufeff');
            var newline = status.IndexOf('\n');
            var state = (newline < 0 ? status : status[..newline]).TrimEnd('\r');
            if (state != "SUCCEEDED")
            {
                error = state switch
                {
                    "PENDING" => "Visual export is pending. Run export-frames.jsfl in Animate 2020 or later.",
                    "RUNNING" => "Visual export started but did not finish. Check Adobe's dialogs and Output panel.",
                    "FAILED" => "Adobe visual export failed: " + (newline < 0 ? "No details." : status[(newline + 1)..].Trim()),
                    _ => "Adobe reported an unrecognized visual export status."
                };
                return false;
            }
            ValidateManifest(plan.ExpectedManifestPath);
            path = plan.ExpectedManifestPath;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or XmlException or JsonException or DecoderFallbackException or NotSupportedException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string Script(FlashVisualConversionPlan plan)
    {
        var source = JsonSerializer.Serialize(plan.WorkingSourcePath);
        var manifest = JsonSerializer.Serialize(plan.ExpectedManifestPath);
        var completion = JsonSerializer.Serialize(plan.CompletionPath);
        return $$$"""
            // Visual bake: original layers, symbols and tweens are flattened into SVG frames.
            // Requires Adobe Animate 2020+. Does not publish, test, or evaluate ActionScript.
            // Before running, configure SVG export to full Stage bounds, embedded PNG/JPEG,
            // hidden layers excluded, text as outlines. Modify > Break Apart twice converts
            // Classic text to shapes if needed; edit only source-copy.fla, never the original.
            // Audio, interactions, scripts and independently playing movie clips are not baked.
            (function () {
                var sourceURI = FLfile.platformPathToURI({{{source}}});
                var manifestURI = FLfile.platformPathToURI({{{manifest}}});
                var statusURI = FLfile.platformPathToURI({{{completion}}});
                var rootURI = manifestURI.substring(0, manifestURI.lastIndexOf("/") + 1);
                var doc = null, previous = fl.getDocumentDOM();
                var version = String(fl.version), totalBytes = 0, ownsManifest = false;
                function report(state, detail) {
                    if (!FLfile.write(statusURI, state + "\n" + detail + "\nAdobe: " + version + "\n"))
                        throw new Error("Cannot write visual-conversion-status.txt.");
                    fl.trace(state + ": " + detail);
                }
                function quote(value) {
                    var str = String(value), out = '"', i, ch, code;
                    for (i = 0; i < str.length; i++) {
                        ch = str.charAt(i); code = str.charCodeAt(i);
                        if (ch == '"' || ch == '\\') out += '\\' + ch;
                        else if (code < 32 || code > 126) out += '\\u' + ('0000' + code.toString(16)).slice(-4);
                        else out += ch;
                    }
                    return out + '"';
                }
                function json(value) {
                    if (value === null) return "null";
                    if (typeof value == "string") return quote(value);
                    if (typeof value == "number") { if (!isFinite(value)) throw new Error("Nonfinite manifest number."); return String(value); }
                    if (typeof value == "boolean") return value ? "true" : "false";
                    var parts = [], i, key;
                    if (value instanceof Array) { for (i = 0; i < value.length; i++) parts.push(json(value[i])); return "[" + parts.join(",") + "]"; }
                    for (key in value) if (value.hasOwnProperty(key)) parts.push(quote(key) + ":" + json(value[key]));
                    return "{" + parts.join(",") + "}";
                }
                function closeCopy() {
                    if (doc) { fl.closeDocument(doc, false); doc = null; }
                    if (previous) fl.setActiveWindow(previous);
                }
                function stageSvg(rawURI, width, height) {
                    if (FLfile.getSize(rawURI) <= 0 || FLfile.getSize(rawURI) > 8388608)
                        throw new Error("An exported SVG is empty or larger than 8 MiB.");
                    var text = FLfile.read(rawURI);
                    if (!text) throw new Error("Cannot read exported SVG.");
                    // Adobe SVG 1.1 exports can include a standard external DOCTYPE.
                    // Remove it without resolving any resource; reject internal subsets.
                    text = text.replace(/^\uFEFF/, "").replace(/^\s*<\?xml[^?]*\?>\s*/, "");
                    text = text.replace(/<!DOCTYPE\s+svg\s+(?:PUBLIC\s+"[^"]*"\s+"[^"]*"|SYSTEM\s+"[^"]*")\s*>/i, "");
                    if (/<!DOCTYPE|<!ENTITY|<\?/i.test(text)) throw new Error("SVG contains an unsupported DTD, entity or processing instruction.");
                    var svg = new XML(text);
                    if (String(svg.localName()) != "svg" || String(svg.namespace().uri) != "http://www.w3.org/2000/svg")
                        throw new Error("Adobe did not export an SVG document.");
                    var view = String(svg.@viewBox).replace(/^\s+|\s+$/g, "").split(/[\s,]+/);
                    if (view.length != 4 || !isFinite(Number(view[0])) || !isFinite(Number(view[1])) || !isFinite(Number(view[2])) || !isFinite(Number(view[3]))
                        || Math.abs(Number(view[0])) > 0.001 || Math.abs(Number(view[1])) > 0.001
                        || Math.abs(Number(view[2]) - width) > 0.001 || Math.abs(Number(view[3]) - height) > 0.001)
                        throw new Error("SVG does not use full Stage bounds. Configure full Stage SVG export; tight bounds are not stretched.");
                    var nodes = svg.descendants(), node, attrs, attr, name, value, i, j;
                    for (i = 0; i < nodes.length(); i++) {
                        node = nodes[i]; if (node.nodeKind() != "element") continue;
                        name = String(node.localName()).toLowerCase();
                        if (name == "text" || name == "tspan") throw new Error("SVG still contains text. Convert text to outlines on the source copy before export.");
                        if (name == "script" || name == "foreignobject") throw new Error("SVG contains executable or foreign content.");
                        attrs = node.attributes();
                        for (j = 0; j < attrs.length(); j++) {
                            attr = attrs[j]; value = String(attr); name = String(attr.localName()).toLowerCase();
                            if (/^on/.test(name) || /javascript:/i.test(value)) throw new Error("SVG contains an event handler.");
                            if (name == "href" && value.charAt(0) != "#") {
                                if (String(node.localName()).toLowerCase() != "image" || !/^data:image\/(png|jpeg);base64,[A-Za-z0-9+\/=\s]+$/i.test(value))
                                    throw new Error("SVG uses linked images or external resources. Configure SVG export to Embed PNG/JPEG bitmaps.");
                            }
                        }
                    }
                    // Preserve full-stage coordinate mapping; only normalize physical units.
                    svg.@width = String(width) + "px"; svg.@height = String(height) + "px";
                    return svg.toXMLString();
                }
                try {
                    report("RUNNING", "Opening the isolated FLA source copy.");
                    var major = version.match(/(\d+)[,.]/);
                    if (!major || Number(major[1]) < 20) throw new Error("Adobe Animate 2020 (version 20) or later is required.");
                    if (FLfile.exists(manifestURI) || FLfile.exists(rootURI + "frames") || FLfile.exists(rootURI + "raw"))
                        throw new Error("Visual export output already exists. Prepare a new empty folder; no existing output will be overwritten.");
                    doc = fl.openDocument(sourceURI);
                    if (!doc || typeof doc.exportSVG != "function") throw new Error("Adobe cannot open this FLA or does not provide exportSVG().");
                    doc.livePreview = false;
                    var width = Number(doc.width), height = Number(doc.height), fps = Number(doc.frameRate);
                    if (!isFinite(width) || !isFinite(height) || width < 1 || height < 1 || width * 25 > 5000000 || height * 25 > 5000000
                        || !isFinite(fps) || fps < 1 || fps > 120) throw new Error("Stage dimensions or FPS exceed animation bundle limits.");
                    var scenes = doc.timelines, total = 0, i, frame;
                    if (!scenes || scenes.length == 0) throw new Error("The document has no scenes.");
                    for (i = 0; i < scenes.length; i++) {
                        var count = Number(scenes[i].frameCount);
                        if (!isFinite(count) || count < 1 || Math.floor(count) != count) throw new Error("A scene has invalid frame count.");
                        total += count;
                    }
                    if (total > 10000) throw new Error("Concatenated scenes exceed 10000 frames.");
                    if (!FLfile.createFolder(rootURI + "frames") || !FLfile.createFolder(rootURI + "raw")) throw new Error("Cannot create export directories.");
                    var frames = [], absoluteFrame = 0;
                    for (i = 0; i < scenes.length; i++) {
                        doc.editScene(i);
                        var timeline = doc.getTimeline(), frameCount = timeline.frameCount;
                        for (frame = 0; frame < frameCount; frame++) {
                            timeline.currentFrame = frame;
                            var id = "frame-" + ("00000" + absoluteFrame).slice(-5);
                            var rawDirectory = rootURI + "raw/" + id;
                            if (!FLfile.createFolder(rawDirectory)) throw new Error("Cannot create frame export directory.");
                            doc.exportSVG(rawDirectory + "/export", true, true);
                            var files = FLfile.listFolder(rawDirectory + "/*.svg", "files");
                            if (!files || files.length != 1) throw new Error("Adobe did not produce exactly one SVG for scene " + (i + 1) + ", frame " + (frame + 1) + ".");
                            var rawURI = rawDirectory + "/" + files[0], relative = "frames/" + id + ".svg";
                            var normalized = stageSvg(rawURI, width, height);
                            if (FLfile.exists(rootURI + relative) || !FLfile.write(rootURI + relative, normalized)) throw new Error("Cannot write isolated SVG frame.");
                            var bytes = Number(FLfile.getSize(rootURI + relative));
                            totalBytes += bytes;
                            if (!isFinite(bytes) || bytes <= 0 || bytes > 8388608 || totalBytes > 536870912) throw new Error("SVG frame or total bundle size exceeds the export budget.");
                            frames.push({frame:absoluteFrame, path:relative, x:0, y:0, width:width*25, height:height*25});
                            absoluteFrame++;
                            report("RUNNING", "Exported " + absoluteFrame + " / " + total + " frames.");
                        }
                    }
                    var bundle = {version:1, name:"Flash Visual Bake", fps:fps, frameCount:total, width:width*25, height:height*25,
                        symbols:[{key:"visual-bake", name:"Flash Visual Frames", frameCount:total, frames:frames, instances:[]}],
                        scene:{name:"Flash Scenes (Visual Bake)", instances:[{name:"Flash Visual Playback", symbol:"visual-bake", layer:"Visual Bake", x:0, y:0, playbackMode:"Loop"}]}};
                    var serialized = json(bundle);
                    if (serialized.length > 16777216) throw new Error("Manifest exceeds 16 MiB.");
                    closeCopy();
                    if (FLfile.exists(manifestURI)) throw new Error("manifest.json already exists; existing output will not be overwritten.");
                    ownsManifest = true;
                    if (!FLfile.write(manifestURI, serialized)) throw new Error("Cannot write final manifest.json.");
                    report("SUCCEEDED", "Exported all scenes in order to manifest.json. Import the animation bundle in Vector 2D. This bake flattens structure; audio, scripts, interactions and independent movie-clip playback are not preserved. SVG filter output can differ from Adobe.");
                } catch (failure) {
                    var detail = String(failure);
                    try { closeCopy(); } catch (closeFailure) { detail += "\nClose: " + String(closeFailure); }
                    try { if (ownsManifest && FLfile.exists(manifestURI)) FLfile.remove(manifestURI); } catch (removeFailure) { detail += "\nCleanup: " + String(removeFailure); }
                    try { report("FAILED", detail); } catch (statusFailure) { fl.trace(detail + "\n" + String(statusFailure)); }
                    alert("Flash visual export failed.\n" + detail + "\nCheck visual-conversion-status.txt and Adobe's Output panel.");
                }
            }());
            """;
    }

    private static void ValidateManifest(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("The completed visual manifest is missing, empty or larger than 16 MiB.");
        using var json = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8), new JsonDocumentOptions { MaxDepth = 256 });
        var manifest = json.RootElement;
        if (manifest.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported visual manifest version.");
        var count = manifest.GetProperty("frameCount").GetInt32();
        var width = manifest.GetProperty("width").GetDouble();
        var height = manifest.GetProperty("height").GetDouble();
        var fps = manifest.GetProperty("fps").GetDouble();
        if (count is < 1 or > 10000 || !double.IsFinite(width) || !double.IsFinite(height)
            || width < 25 || height < 25 || width > 5000000 || height > 5000000 || !double.IsFinite(fps) || fps < 1 || fps > 120)
            throw new InvalidDataException("The visual manifest dimensions, FPS or frame count exceed export limits.");
        var symbols = manifest.GetProperty("symbols");
        if (symbols.GetArrayLength() != 1) throw new InvalidDataException("Visual export must contain one baked symbol.");
        var symbol = symbols[0];
        if (symbol.GetProperty("key").GetString() != "visual-bake" || symbol.GetProperty("frameCount").GetInt32() != count)
            throw new InvalidDataException("The visual symbol does not match the manifest duration.");
        var frames = symbol.GetProperty("frames");
        if (frames.GetArrayLength() != count) throw new InvalidDataException("The visual bake does not contain every frame.");
        var directory = Path.GetDirectoryName(path)!;
        long totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            var frame = frames[index];
            var relative = "frames/frame-" + index.ToString("D5", CultureInfo.InvariantCulture) + ".svg";
            if (frame.GetProperty("frame").GetInt32() != index || frame.GetProperty("path").GetString() != relative
                || frame.GetProperty("x").GetDouble() != 0 || frame.GetProperty("y").GetDouble() != 0
                || frame.GetProperty("width").GetDouble() != width || frame.GetProperty("height").GetDouble() != height)
                throw new InvalidDataException("Visual frame metadata does not match the full-stage bake.");
            var svgPath = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            SafePath(svgPath);
            if (!File.Exists(svgPath)) throw new FileNotFoundException("A baked SVG frame is missing.", svgPath);
            var bytes = new FileInfo(svgPath).Length;
            if (bytes is <= 0 or > MaximumSvgBytes || (totalBytes += bytes) > MaximumFlaBytes)
                throw new InvalidDataException("Baked SVG frame or total bundle exceeds the size limit.");
            using var reader = XmlReader.Create(svgPath, XmlSettings(MaximumSvgBytes));
            reader.MoveToContent();
            if (reader.LocalName != "svg" || reader.NamespaceURI != "http://www.w3.org/2000/svg")
                throw new InvalidDataException("A baked frame is not SVG.");
            var view = (reader.GetAttribute("viewBox") ?? "").Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
            if (view.Length != 4 || !Nearly(view[0], 0) || !Nearly(view[1], 0) || !Nearly(view[2], width / 25) || !Nearly(view[3], height / 25))
                throw new InvalidDataException("A baked SVG frame does not use full Stage bounds.");
            while (reader.Read())
            {
                if (reader.Depth > 256) throw new InvalidDataException("Baked SVG depth exceeds 256.");
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "text" or "tspan")
                    throw new InvalidDataException("A baked SVG still contains text; convert it to outlines in Adobe.");
            }
        }
        // Full schema, embedded-image and external-reference validation belongs to
        // AnimationBundleImporter.Load before a live project can be replaced.
    }

    private static bool Nearly(string value, double expected) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var actual) && double.IsFinite(actual) && Math.Abs(actual - expected) <= 0.001;

    private static XmlReaderSettings XmlSettings(long maximum) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum
    };

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 1024)
            throw new ArgumentException("Visual bridge paths must be absolute and at most 1024 characters long.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 1024) throw new ArgumentException("The normalized visual bridge path is too long.", nameof(path));
        return fullPath;
    }

    private static void SafePath(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Visual bridge paths cannot contain symbolic links or reparse points.");
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static void WriteNew(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(StrictUtf8.GetBytes(value));
        stream.Flush(true);
    }
}
