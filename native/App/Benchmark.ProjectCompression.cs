using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const int ProjectCompressionMaximumTimelineBytes = 128 * 1024 * 1024;
    private const int ProjectCompressionMaximumMetadataBytes = 96 * 1024 * 1024;
    private static readonly UTF8Encoding ProjectCompressionUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static void VerifyProjectCompressionRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("project-compression-regression");
        var manifestPath = Path.Combine(temporaryRoot, "Compression.v2dProject");
        try
        {
            var project = CreateProjectCompressionFixture();
            ProjectVaultStore.Save(project, manifestPath);

            var drawingId = project.DrawingObjects[0].Id;
            var sceneId = project.Scenes[0].Id;
            var svgPath = Path.Combine(
                temporaryRoot,
                ".Vault",
                $"{drawingId}.svg");
            var drawingTimelinePath = Path.Combine(
                temporaryRoot,
                ".TimeLine",
                "Drawings",
                $"{drawingId}.json.br");
            var sceneTimelinePath = Path.Combine(
                temporaryRoot,
                ".TimeLine",
                "Scenes",
                $"{sceneId}.json.br");
            var manifest = ReadProjectCompressionManifest(manifestPath);
            var drawingManifest = FindManifestEntry(manifest, "drawingObjects", drawingId);
            var sceneManifest = FindManifestEntry(manifest, "scenes", sceneId);
            var svgDocument = XDocument.Load(svgPath, LoadOptions.None);
            var svgMetadata = svgDocument.Root?
                .Elements(XNamespace.Get("http://www.w3.org/2000/svg") + "metadata")
                .SingleOrDefault(element => string.Equals(
                    (string?)element.Attribute("id"),
                    "v2d-metadata",
                    StringComparison.Ordinal));
            AssertTimeline(
                manifest["formatVersion"]?.GetValue<int>() == ProjectVaultStore.CurrentManifestFormatVersion
                && string.Equals(
                    (string?)svgMetadata?.Attribute("data-encoding"),
                    "base64-brotli-json",
                    StringComparison.Ordinal)
                && drawingManifest["timelinePath"]?.GetValue<string>()?.EndsWith(
                    ".json.br",
                    StringComparison.OrdinalIgnoreCase) == true
                && sceneManifest["timelinePath"]?.GetValue<string>()?.EndsWith(
                    ".json.br",
                    StringComparison.OrdinalIgnoreCase) == true
                && File.Exists(drawingTimelinePath)
                && File.Exists(sceneTimelinePath)
                && !File.Exists(drawingTimelinePath[..^3])
                && !File.Exists(sceneTimelinePath[..^3])
                && string.Equals(
                    drawingManifest["svgSha256"]?.GetValue<string>(),
                    ComputeProjectCompressionSha256(svgPath),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    drawingManifest["timelineSha256"]?.GetValue<string>(),
                    ComputeProjectCompressionSha256(drawingTimelinePath),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    sceneManifest["timelineSha256"]?.GetValue<string>(),
                    ComputeProjectCompressionSha256(sceneTimelinePath),
                    StringComparison.OrdinalIgnoreCase),
                "Project Vault did not write compressed v4 timeline payloads with hashes of the stored bytes.");

            var restored = ProjectVaultStore.Load(manifestPath);
            AssertTimeline(
                restored.DrawingObjects.Count == project.DrawingObjects.Count
                && restored.DrawingObjects[0].Scene.ObjectCount == project.DrawingObjects[0].Scene.ObjectCount
                && restored.DrawingObjects[0].Scene.ShapeKind[0] == ShapeKind.Rectangle
                && restored.Scenes.Count == project.Scenes.Count,
                "Compressed project timelines did not round-trip the fixture graph.");

            VerifyProjectCompressionPayloadFailures();
            VerifyProjectCompressionCorruptedLoad(
                manifestPath,
                drawingId,
                drawingTimelinePath,
                restored);

            foreach (var (legacyVersion, svgVersion) in new[]
            {
                (ManifestVersion: 1, SvgVersion: 1),
                (ManifestVersion: 2, SvgVersion: 2),
                (ManifestVersion: 3, SvgVersion: 3),
                (ManifestVersion: 3, SvgVersion: 4)
            })
            {
                var legacyRoot = Path.Combine(
                    temporaryRoot,
                    $"legacy-v{legacyVersion}-svg-v{svgVersion}");
                var legacyManifestPath = Path.Combine(
                    legacyRoot,
                    $"LegacyV{legacyVersion}SvgV{svgVersion}.v2dProject");
                ProjectVaultStore.Save(project, legacyManifestPath);
                ConvertProjectCompressionFixtureToLegacy(
                    legacyManifestPath,
                    legacyVersion,
                    svgVersion);

                var legacyManifest = ReadProjectCompressionManifest(legacyManifestPath);
                var legacyDrawing = FindManifestEntry(legacyManifest, "drawingObjects", drawingId);
                var legacyScene = FindManifestEntry(legacyManifest, "scenes", sceneId);
                var legacyDrawingTimelinePath = Path.Combine(
                    legacyRoot,
                    ".TimeLine",
                    "Drawings",
                    $"{drawingId}.json");
                var legacySceneTimelinePath = Path.Combine(
                    legacyRoot,
                    ".TimeLine",
                    "Scenes",
                    $"{sceneId}.json");
                AssertTimeline(
                    legacyManifest["formatVersion"]?.GetValue<int>() == legacyVersion
                    && legacyDrawing["timelinePath"]?.GetValue<string>()?.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase) == true
                    && legacyScene["timelinePath"]?.GetValue<string>()?.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase) == true
                    && File.Exists(legacyDrawingTimelinePath)
                    && File.Exists(legacySceneTimelinePath)
                    && !File.Exists(legacyDrawingTimelinePath + ".br")
                    && !File.Exists(legacySceneTimelinePath + ".br")
                    && string.Equals(
                        legacyDrawing["timelineSha256"]?.GetValue<string>(),
                        ComputeProjectCompressionSha256(legacyDrawingTimelinePath),
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        legacyScene["timelineSha256"]?.GetValue<string>(),
                        ComputeProjectCompressionSha256(legacySceneTimelinePath),
                        StringComparison.OrdinalIgnoreCase),
                    $"Legacy manifest v{legacyVersion} with SVG v{svgVersion} was not converted to raw JSON timelines with synchronized hashes.");

                var legacy = ProjectVaultStore.Load(legacyManifestPath);
                AssertTimeline(
                    legacy.DrawingObjects[0].Scene.ObjectCount == project.DrawingObjects[0].Scene.ObjectCount
                    && legacy.Scenes.Count == project.Scenes.Count,
                    $"Legacy manifest v{legacyVersion} with SVG v{svgVersion} metadata did not load.");

                ProjectVaultStore.Save(legacy, legacyManifestPath);
                var migratedManifest = ReadProjectCompressionManifest(legacyManifestPath);
                AssertTimeline(
                    migratedManifest["formatVersion"]?.GetValue<int>() == ProjectVaultStore.CurrentManifestFormatVersion
                    && File.Exists(Path.Combine(
                        legacyRoot,
                        ".TimeLine",
                        "Drawings",
                        $"{drawingId}.json.br"))
                    && File.Exists(Path.Combine(
                        legacyRoot,
                        ".TimeLine",
                        "Scenes",
                        $"{sceneId}.json.br"))
                    && !File.Exists(legacyDrawingTimelinePath)
                    && !File.Exists(legacySceneTimelinePath),
                    $"Saving legacy manifest v{legacyVersion} with SVG v{svgVersion} did not migrate timelines back to compressed v4 storage.");
            }
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }

        Console.WriteLine("project_compression_regression=ok");
    }

    private static VectorProject CreateProjectCompressionFixture()
    {
        var project = VectorProject.CreateEmpty();
        project.Name = "Project compression fixture";
        var drawing = project.DrawingObjects[0];
        drawing.Name = "Compressed symbol";
        drawing.Scene.CreateEmpty(1, 4);
        drawing.Scene.AddObject(
            0,
            new PointF(24, 18),
            new SizeF(80, 48),
            0,
            0,
            Color.Coral,
            8,
            ShapeKind.Rectangle);
        return project;
    }

    private static void VerifyProjectCompressionPayloadFailures()
    {
        var payload = ProjectCompressionUtf8.GetBytes("{\"payload\":\"compression\",\"items\":[1,2,3]}");
        var compressed = ProjectPayloadCompression.Compress(payload);
        var restored = ProjectPayloadCompression.Decompress(compressed, payload.Length);
        AssertTimeline(
            restored.SequenceEqual(payload),
            "Project payload compression did not round-trip a valid payload.");

        var largePayload = new byte[128 * 1024 + 17];
        for (var index = 0; index < largePayload.Length; index++)
        {
            largePayload[index] = (byte)(index % 251);
        }
        var largeCompressed = ProjectPayloadCompression.Compress(largePayload);
        AssertTimeline(
            ProjectPayloadCompression.Decompress(largeCompressed, largePayload.Length).SequenceEqual(largePayload)
            && ProjectPayloadCompression.Decompress(ProjectPayloadCompression.Compress([]), 0).Length == 0,
            "Project payload compression did not preserve chunk boundaries or an empty payload.");
        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(largeCompressed, largePayload.Length - 1),
            "Project payload compression exceeded its size limit across output chunks.");
        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(compressed.Concat(compressed).ToArray(), payload.Length * 2),
            "Project payload compression accepted concatenated Brotli streams.");

        var truncated = compressed[..^1];
        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(truncated, payload.Length),
            "Project payload compression accepted a truncated Brotli stream.");

        var trailing = compressed.Concat(new byte[] { 0 }).ToArray();
        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(trailing, payload.Length),
            "Project payload compression accepted trailing data after a Brotli stream.");

        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(compressed, payload.Length - 1),
            "Project payload compression accepted a payload above its decoded size limit.");

        ExpectInvalidData(
            () => ProjectPayloadCompression.Decompress(new byte[] { 0xff, 0xff, 0xff, 0xff }, payload.Length),
            "Project payload compression accepted invalid Brotli bytes.");
    }

    private static void VerifyProjectCompressionCorruptedLoad(
        string manifestPath,
        string drawingId,
        string drawingTimelinePath,
        VectorProject restored)
    {
        File.WriteAllBytes(drawingTimelinePath, [0xff, 0xff, 0xff, 0xff]);
        var manifest = ReadProjectCompressionManifest(manifestPath);
        var drawingManifest = FindManifestEntry(manifest, "drawingObjects", drawingId);
        drawingManifest["timelineSha256"] = ComputeProjectCompressionSha256(drawingTimelinePath);
        WriteProjectCompressionManifest(manifestPath, manifest);

        ExpectInvalidData(
            () => ProjectVaultStore.Load(manifestPath),
            "Project Vault accepted an invalid Brotli payload after its manifest hash was updated.");

        ProjectVaultStore.Save(restored, manifestPath);
        var repaired = ProjectVaultStore.Load(manifestPath);
        AssertTimeline(
            repaired.DrawingObjects.Count == restored.DrawingObjects.Count
            && repaired.DrawingObjects[0].Scene.ObjectCount == restored.DrawingObjects[0].Scene.ObjectCount
            && repaired.DrawingObjects[0].Scene.ShapeKind[0] == restored.DrawingObjects[0].Scene.ShapeKind[0]
            && repaired.Scenes.Count == restored.Scenes.Count,
            "Repairing a corrupted project timeline changed the restored fixture graph.");
    }

    private static void ConvertProjectCompressionFixtureToLegacy(
        string manifestPath,
        int manifestVersion,
        int svgVersion = 4)
    {
        if (manifestVersion is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(manifestVersion));
        }
        if (svgVersion is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(svgVersion));
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var projectRoot = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidOperationException("Legacy fixture manifest has no project root.");
        var manifest = ReadProjectCompressionManifest(fullManifestPath);

        foreach (var collectionName in new[] { "drawingObjects", "scenes" })
        {
            var entries = manifest[collectionName]?.AsArray()
                ?? throw new InvalidOperationException($"Legacy fixture has no {collectionName} array.");
            foreach (var node in entries)
            {
                var entry = node?.AsObject()
                    ?? throw new InvalidOperationException("Legacy fixture has a null manifest entry.");
                ConvertProjectCompressionTimelineEntry(projectRoot, entry);
            }
        }

        var drawings = manifest["drawingObjects"]?.AsArray()
            ?? throw new InvalidOperationException("Legacy fixture has no drawing array.");
        foreach (var node in drawings)
        {
            var entry = node?.AsObject()
                ?? throw new InvalidOperationException("Legacy fixture has a null drawing entry.");
            var drawingId = entry["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Legacy fixture drawing has no stable ID.");
            var svgRelativePath = entry["svgPath"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Legacy fixture drawing has no SVG path.");
            var svgPath = ResolveProjectCompressionPath(projectRoot, svgRelativePath);
            ConvertProjectCompressionSvgToLegacy(svgPath, drawingId, svgVersion);
            entry["svgSha256"] = ComputeProjectCompressionSha256(svgPath);
        }

        manifest["formatVersion"] = manifestVersion;
        WriteProjectCompressionManifest(fullManifestPath, manifest);
    }

    private static void ConvertProjectCompressionTimelineEntry(string projectRoot, JsonObject entry)
    {
        var compressedRelativePath = entry["timelinePath"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Compressed timeline entry has no path.");
        if (!compressedRelativePath.EndsWith(".json.br", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Compressed timeline entry does not use the .json.br extension.");
        }

        var compressedPath = ResolveProjectCompressionPath(projectRoot, compressedRelativePath);
        var legacyRelativePath = compressedRelativePath[..^3];
        var legacyPath = ResolveProjectCompressionPath(projectRoot, legacyRelativePath);
        var decoded = ProjectPayloadCompression.Decompress(
            File.ReadAllBytes(compressedPath),
            ProjectCompressionMaximumTimelineBytes);
        File.WriteAllBytes(legacyPath, decoded);
        File.Delete(compressedPath);
        entry["timelinePath"] = legacyRelativePath;
        entry["timelineSha256"] = ComputeProjectCompressionSha256(legacyPath);
    }

    private static void ConvertProjectCompressionSvgToLegacy(
        string svgPath,
        string drawingId,
        int svgVersion)
    {
        var svgNamespace = XNamespace.Get("http://www.w3.org/2000/svg");
        var document = XDocument.Load(svgPath, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("Compressed SVG fixture has no root element.");
        var metadata = root
            .Elements(svgNamespace + "metadata")
            .SingleOrDefault(element => string.Equals(
                (string?)element.Attribute("id"),
                "v2d-metadata",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Compressed SVG fixture has no V2D metadata element.");
        var encoded = metadata.Value.Trim();
        var compressed = Convert.FromBase64String(encoded);
        var json = ProjectPayloadCompression.Decompress(
            compressed,
            ProjectCompressionMaximumMetadataBytes);
        var envelope = JsonNode.Parse(ProjectCompressionUtf8.GetString(json))?.AsObject()
            ?? throw new InvalidOperationException("Compressed SVG fixture metadata is not a JSON object.");
        var versionProperty = envelope.Select(property => property.Key).SingleOrDefault(key =>
            string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase));
        if (versionProperty is null)
        {
            throw new InvalidOperationException("Compressed SVG fixture metadata has no envelope version.");
        }

        envelope[versionProperty] = svgVersion;
        metadata.Value = Convert.ToBase64String(ProjectCompressionUtf8.GetBytes(
            envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false })));
        root.SetAttributeValue("data-v2d-format-version", svgVersion.ToString());
        metadata.SetAttributeValue("data-v2d-format-version", svgVersion.ToString());
        metadata.SetAttributeValue("data-encoding", "base64-json");
        root.SetAttributeValue("data-v2d-drawing-object-id", drawingId);

        var settings = new XmlWriterSettings
        {
            Encoding = ProjectCompressionUtf8,
            Indent = true,
            NewLineHandling = NewLineHandling.None
        };
        using var writer = XmlWriter.Create(svgPath, settings);
        document.Save(writer);
    }

    private static JsonObject ReadProjectCompressionManifest(string manifestPath)
    {
        return JsonNode.Parse(File.ReadAllText(manifestPath, ProjectCompressionUtf8))?.AsObject()
            ?? throw new InvalidOperationException("Project compression fixture manifest is not a JSON object.");
    }

    private static JsonObject FindManifestEntry(
        JsonObject manifest,
        string collectionName,
        string id)
    {
        var entries = manifest[collectionName]?.AsArray()
            ?? throw new InvalidOperationException($"Manifest has no {collectionName} array.");
        return entries
            .Select(node => node?.AsObject())
            .Single(entry => string.Equals(
                entry?["id"]?.GetValue<string>(),
                id,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Manifest has no {collectionName} entry for '{id}'.");
    }

    private static void WriteProjectCompressionManifest(string manifestPath, JsonObject manifest)
    {
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ProjectCompressionUtf8);
    }

    private static string ResolveProjectCompressionPath(string projectRoot, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(projectRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Legacy fixture path escaped its project root.");
        }

        return fullPath;
    }

    private static string ComputeProjectCompressionSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

}
