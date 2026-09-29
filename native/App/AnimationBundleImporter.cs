using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace VectorAnimationEngine;

internal sealed record AnimationBundleImportResult(
    VectorProject Project,
    string Name,
    decimal Fps,
    int FrameCount,
    float Width,
    float Height,
    IReadOnlyDictionary<string, string> AliasToId,
    int NestedInstanceCount);

internal static class AnimationBundleImporter
{
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const int MaximumSymbols = 1024;
    private const int MaximumInstancesPerContainer = 4096;
    private const int MaximumSvgFramesPerSymbol = 10_000;
    private const int MaximumTotalSvgFrames = 100_000;
    private const int MaximumTotalInstanceKeys = 500_000;
    private const int MaximumFrameCount = 10_000;
    private const int MaximumNameLength = 160;
    private const int MaximumPathLength = 1024;
    private const float MaximumCoordinate = 5_000_000f;
    private const float MaximumScale = 1_000f;
    private const float MaximumRotation = 360_000f;
    private const int MaximumXmlDepth = 256;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = MaximumXmlDepth
    };

    internal static AnimationBundleImportResult Load(string manifestPath)
    {
        var fullManifestPath = ValidateManifestPath(manifestPath);
        var bundleDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The animation bundle has no containing directory.");
        EnsureDirectoryIsSafe(bundleDirectory);

        var manifest = ReadManifest(fullManifestPath);
        var svgSources = ValidateManifest(manifest, bundleDirectory);
        var project = BuildProject(manifest, bundleDirectory, svgSources);
        var sceneInstanceCount = project.Scenes.Sum(scene => scene.Instances.Count);
        var nestedInstanceCount = project.DrawingObjects.Sum(symbol => symbol.Instances.Count)
            + sceneInstanceCount;

        return new AnimationBundleImportResult(
            project,
            RequireText(manifest.Name, "name", MaximumNameLength),
            (decimal)manifest.Fps,
            manifest.FrameCount,
            manifest.Width,
            manifest.Height,
            project.DrawingObjects
                .Select((symbol, index) => (Alias: manifest.Symbols![index].Key!, symbol.Id))
                .ToDictionary(item => item.Alias, item => item.Id, StringComparer.Ordinal),
            nestedInstanceCount);
    }

    private static string ValidateManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("path must be an absolute .json animation bundle manifest path.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The animation bundle manifest does not exist.", fullPath);
        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException($"The animation bundle manifest must be between 1 byte and {MaximumManifestBytes} bytes.");
        }

        EnsureFileIsSafe(fullPath);
        return fullPath;
    }

    private static AnimationBundleManifest ReadManifest(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The animation bundle manifest could not be read.", exception);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<AnimationBundleManifest>(bytes, ManifestJsonOptions);
            return manifest ?? throw new InvalidDataException("The animation bundle manifest cannot be JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The animation bundle manifest is not valid JSON for the supported schema.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("The animation bundle manifest contains an unsupported JSON value.", exception);
        }
    }

    private static Dictionary<string, string> ValidateManifest(
        AnimationBundleManifest manifest,
        string bundleDirectory)
    {
        if (manifest.Version != 1) throw new InvalidDataException("Only animation bundle version 1 is supported.");
        RequireText(manifest.Name, "name", MaximumNameLength);
        ValidateNumber(manifest.Fps, "fps", 1, 120);
        ValidatePositive(manifest.FrameCount, "frameCount", MaximumFrameCount);
        ValidateNumber(manifest.Width, "width", 1, MaximumCoordinate);
        ValidateNumber(manifest.Height, "height", 1, MaximumCoordinate);

        var symbols = manifest.Symbols ?? throw new InvalidDataException("symbols is required.");
        if (symbols.Length is < 1 or > MaximumSymbols)
        {
            throw new InvalidDataException($"symbols must contain 1 to {MaximumSymbols} entries.");
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var frameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var svgSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalSvgFrames = 0;
        var totalInstanceKeys = 0;

        for (var symbolIndex = 0; symbolIndex < symbols.Length; symbolIndex++)
        {
            var symbol = symbols[symbolIndex] ?? throw new InvalidDataException($"symbols[{symbolIndex}] cannot be null.");
            var alias = RequireText(symbol.Key, $"symbols[{symbolIndex}].key", MaximumNameLength);
            RequireText(symbol.Name, $"symbols[{symbolIndex}].name", MaximumNameLength);
            if (aliases.Contains(alias)) throw new InvalidDataException($"Duplicate symbol alias: {alias}.");

            ValidatePositive(symbol.FrameCount, $"symbols[{symbolIndex}].frameCount", MaximumFrameCount);

            var frames = symbol.Frames ?? [];
            if (frames.Length > MaximumSvgFramesPerSymbol)
            {
                throw new InvalidDataException($"symbols[{symbolIndex}].frames contains too many SVG frames.");
            }
            totalSvgFrames = checked(totalSvgFrames + frames.Length);
            if (totalSvgFrames > MaximumTotalSvgFrames)
            {
                throw new InvalidDataException("The animation bundle contains too many SVG frames.");
            }

            var frameNumbers = new HashSet<int>();
            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var frame = frames[frameIndex]
                    ?? throw new InvalidDataException($"symbols[{symbolIndex}].frames[{frameIndex}] cannot be null.");
                var frameNumber = RequireFrame(frame.Frame, $"symbols[{symbolIndex}].frames[{frameIndex}].frame", symbol.FrameCount);
                if (!frameNumbers.Add(frameNumber))
                {
                    throw new InvalidDataException($"Duplicate SVG frame {frameNumber} in symbol '{alias}'.");
                }

                RequireText(frame.Path, $"symbols[{symbolIndex}].frames[{frameIndex}].path", MaximumPathLength);
                ValidateNumber(frame.Width, $"symbols[{symbolIndex}].frames[{frameIndex}].width", 1, MaximumCoordinate);
                ValidateNumber(frame.Height, $"symbols[{symbolIndex}].frames[{frameIndex}].height", 1, MaximumCoordinate);
                ValidateCoordinate(frame.X, $"symbols[{symbolIndex}].frames[{frameIndex}].x");
                ValidateCoordinate(frame.Y, $"symbols[{symbolIndex}].frames[{frameIndex}].y");

                var svgPath = ResolveSvgPath(bundleDirectory, frame.Path!);
                if (!svgSources.ContainsKey(svgPath))
                {
                    var imported = ImportedSvgRasterizer.Load(svgPath);
                    ValidateSvgSource(imported.Source, frame.Path!);
                    svgSources.Add(svgPath, imported.Source);
                }
            }

            var instances = symbol.Instances ?? [];
            ValidateInstanceCount(instances, $"symbols[{symbolIndex}].instances");
            for (var instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
            {
                var instance = instances[instanceIndex]
                    ?? throw new InvalidDataException($"symbols[{symbolIndex}].instances[{instanceIndex}] cannot be null.");
                ValidateInstance(
                    instance,
                    aliases,
                    frameCounts,
                    symbol.FrameCount,
                    $"symbols[{symbolIndex}].instances[{instanceIndex}]",
                    ref totalInstanceKeys);
            }

            aliases.Add(alias);
            frameCounts.Add(alias, symbol.FrameCount);
        }

        var scene = manifest.Scene ?? throw new InvalidDataException("scene is required.");
        RequireText(scene.Name, "scene.name", MaximumNameLength);
        var sceneInstances = scene.Instances ?? throw new InvalidDataException("scene.instances is required.");
        ValidateInstanceCount(sceneInstances, "scene.instances");
        for (var instanceIndex = 0; instanceIndex < sceneInstances.Length; instanceIndex++)
        {
            var instance = sceneInstances[instanceIndex]
                ?? throw new InvalidDataException($"scene.instances[{instanceIndex}] cannot be null.");
            ValidateInstance(
                instance,
                aliases,
                frameCounts,
                manifest.FrameCount,
                $"scene.instances[{instanceIndex}]",
                ref totalInstanceKeys);
        }

        return svgSources;
    }

    private static void ValidateInstance(
        AnimationBundleInstance instance,
        IReadOnlySet<string> knownAliases,
        IReadOnlyDictionary<string, int> frameCounts,
        int parentFrameCount,
        string path,
        ref int totalInstanceKeys)
    {
        RequireText(instance.Name, $"{path}.name", MaximumNameLength);
        var alias = RequireText(instance.Symbol, $"{path}.symbol", MaximumNameLength);
        if (!knownAliases.Contains(alias)) throw new InvalidDataException($"{path}.symbol references an unknown symbol alias '{alias}'.");
        if (instance.Layer is not null) RequireText(instance.Layer, $"{path}.layer", MaximumNameLength);
        ValidateCoordinate(instance.X, $"{path}.x");
        ValidateCoordinate(instance.Y, $"{path}.y");
        ValidateRotation(instance.RotationZ, $"{path}.rotationZ");
        ValidateScale(instance.ScaleX, $"{path}.scaleX");
        ValidateScale(instance.ScaleY, $"{path}.scaleY");
        if (instance.HoldFrame is < 0) throw new InvalidDataException($"{path}.holdFrame must be non-negative.");

        var playbackMode = NormalizePlaybackMode(instance.PlaybackMode, $"{path}.playbackMode");
        if (playbackMode == DrawingObjectPlaybackMode.HoldFrame
            && instance.HoldFrame.GetValueOrDefault() >= frameCounts[alias])
        {
            throw new InvalidDataException($"{path}.holdFrame is outside the referenced symbol's frame range.");
        }

        var keys = instance.Keys ?? [];
        totalInstanceKeys = checked(totalInstanceKeys + keys.Length);
        if (totalInstanceKeys > MaximumTotalInstanceKeys)
        {
            throw new InvalidDataException("The animation bundle contains too many instance transform keys.");
        }

        var keyFrames = new HashSet<int>();
        foreach (var key in keys)
        {
            if (key is null) throw new InvalidDataException($"{path}.keys cannot contain null.");
            var frame = RequireFrame(key.Frame, $"{path}.keys.frame", parentFrameCount);
            if (!keyFrames.Add(frame)) throw new InvalidDataException($"{path}.keys contains duplicate frame {frame}.");
            ValidateCoordinate(key.X, $"{path}.keys[{frame}].x");
            ValidateCoordinate(key.Y, $"{path}.keys[{frame}].y");
            ValidateRotation(key.RotationZ, $"{path}.keys[{frame}].rotationZ");
            ValidateScale(key.ScaleX, $"{path}.keys[{frame}].scaleX");
            ValidateScale(key.ScaleY, $"{path}.keys[{frame}].scaleY");
        }
    }

    private static VectorProject BuildProject(
        AnimationBundleManifest manifest,
        string bundleDirectory,
        IReadOnlyDictionary<string, string> svgSources)
    {
        var project = VectorProject.CreateEmpty();
        project.Name = RequireText(manifest.Name, "name", MaximumNameLength);
        project.TrySetPlaybackSettings(
            (decimal)manifest.Fps,
            loopPlayback: true,
            playbackStartFrame: 0,
            playbackEndFrame: manifest.FrameCount - 1);

        var symbolsByAlias = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        var symbolFrameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Symbols!.Length; index++)
        {
            var specification = manifest.Symbols[index];
            var symbol = index == 0
                ? project.DrawingObjects[0]
                : project.AddDrawingObject(specification.Name);
            symbol.Name = RequireText(specification.Name, $"symbols[{index}].name", MaximumNameLength);
            symbol.Scene.CreateEmpty(
                layers: Math.Max(1, (specification.Frames?.Length > 0 ? 1 : 0) + (specification.Instances?.Length ?? 0)),
                frameCount: specification.FrameCount);
            symbolsByAlias.Add(specification.Key!, symbol);
            symbolFrameCounts.Add(specification.Key!, specification.FrameCount);
            BuildSymbol(
                project,
                symbol,
                specification,
                symbolsByAlias,
                symbolFrameCounts,
                manifest.Fps,
                bundleDirectory,
                svgSources);
        }

        var sceneSpecification = manifest.Scene!;
        var scene = project.Scenes[0];
        scene.Name = RequireText(sceneSpecification.Name, "scene.name", MaximumNameLength);
        SetSceneDuration(scene, manifest.FrameCount);
        BuildRootScene(
            project,
            scene,
            sceneSpecification,
            symbolsByAlias,
            manifest.Fps);
        return project;
    }

    private static void BuildSymbol(
        VectorProject project,
        DrawingObjectDefinition symbol,
        AnimationBundleSymbol specification,
        IReadOnlyDictionary<string, DrawingObjectDefinition> symbolsByAlias,
        IReadOnlyDictionary<string, int> symbolFrameCounts,
        double playbackFps,
        string bundleDirectory,
        IReadOnlyDictionary<string, string> svgSources)
    {
        var scene = symbol.Scene;
        var hasFrames = specification.Frames is { Length: > 0 };
        var instances = specification.Instances ?? [];
        if (hasFrames)
        {
            var frameLayer = instances.Length;
            scene.LayerNames[frameLayer] = $"{symbol.Name} Frames";
            foreach (var frame in specification.Frames!.OrderBy(item => item!.Frame!.Value))
            {
                var svgPath = ResolveSvgPath(bundleDirectory, frame.Path!);
                if (!svgSources.TryGetValue(svgPath, out var source))
                {
                    throw new InvalidDataException($"The prevalidated SVG source disappeared: {frame.Path}");
                }

                var frameNumber = frame.Frame!.Value;
                scene.EditFrame = frameNumber;
                scene.InsertTimelineBlankKeyframe(frameLayer, frameNumber);
                scene.AddImportedSvgObject(
                    frameLayer,
                    new PointF(frame.X ?? 0, frame.Y ?? 0),
                    new SizeF(frame.Width, frame.Height),
                    source,
                    Path.GetFileNameWithoutExtension(frame.Path));
            }
        }

        for (var index = 0; index < instances.Length; index++)
        {
            var specificationInstance = instances[index];
            // Native layer zero is drawn last. Reverse the declared bottom-to-top order.
            var layer = instances.Length - 1 - index;
            scene.LayerNames[layer] = (specificationInstance.Layer ?? specificationInstance.Name)!.Trim();
            var child = symbolsByAlias[specificationInstance.Symbol!];
            var layerId = scene.LayerIds[layer];
            if (!project.TryAddDrawingObjectInstance(
                    symbol.Id,
                    child.Id,
                    new PointF(specificationInstance.X ?? 0, specificationInstance.Y ?? 0),
                    layerId,
                    out var instance)
                || instance is null)
            {
                throw new InvalidDataException(
                    $"Could not add nested instance '{specificationInstance.Name}' to symbol '{symbol.Name}'.");
            }

            instance.Name = specificationInstance.Name!.Trim();
            ApplyInstanceState(
                instance,
                specificationInstance,
                (decimal)playbackFps,
                symbol.FrameCount,
                symbolFrameCounts[specificationInstance.Symbol!]);
        }

        scene.EditFrame = 0;
        scene.SynchronizeTimelineTracks();
    }

    private static void BuildRootScene(
        VectorProject project,
        SceneDefinition scene,
        AnimationBundleScene specification,
        IReadOnlyDictionary<string, DrawingObjectDefinition> symbolsByAlias,
        double playbackFps)
    {
        var instances = specification.Instances!;
        for (var index = instances.Length - 1; index >= 0; index--)
        {
            var specificationInstance = instances[index];
            SceneLayerDefinition? layer;
            if (index == instances.Length - 1)
            {
                layer = scene.Layers[0];
                layer.Name = specificationInstance.Name!.Trim();
            }
            else if (!project.TryAddSceneLayer(scene.Id, specificationInstance.Name, out layer)
                     || layer is null)
            {
                throw new InvalidDataException("The animation bundle could not create a scene content layer.");
            }

            var symbol = symbolsByAlias[specificationInstance.Symbol!];
            if (!project.TryAddSceneInstance(
                    scene.Id,
                    symbol.Id,
                    new PointF(specificationInstance.X ?? 0, specificationInstance.Y ?? 0),
                    z: 0,
                    layer.Id,
                    out var instance)
                || instance is null)
            {
                throw new InvalidDataException(
                    $"Could not add scene instance '{specificationInstance.Name}'.");
            }

            instance.Name = specificationInstance.Name!.Trim();
            ApplyInstanceState(
                instance,
                specificationInstance,
                (decimal)playbackFps,
                scene.FrameCount,
                symbol.FrameCount);
        }

        scene.SynchronizeTimelineTracks();
    }

    private static void ApplyInstanceState(
        DrawingObjectInstanceDefinition instance,
        AnimationBundleInstance specification,
        decimal playbackFps,
        int parentFrameCount,
        int childFrameCount)
    {
        var baseState = instance.EvaluateState(0) with
        {
            Visible = specification.Visible ?? true,
            X = specification.X ?? 0,
            Y = specification.Y ?? 0,
            RotationZ = specification.RotationZ ?? 0,
            ScaleX = specification.ScaleX ?? 1,
            ScaleY = specification.ScaleY ?? 1,
            PlaybackFps = playbackFps,
            PlaybackMode = NormalizePlaybackMode(specification.PlaybackMode, "playbackMode"),
            HoldFrame = specification.HoldFrame ?? 0
        };
        // Setting frame zero is deliberately allowed to be a no-op. The base state is
        // already valid, and the caller may still have supplied an explicit frame-zero key.
        instance.SetStateAtFrame(0, baseState);

        foreach (var key in (specification.Keys ?? []).OrderBy(item => item!.Frame!.Value))
        {
            var frame = key.Frame!.Value;
            if (frame >= parentFrameCount)
            {
                throw new InvalidDataException($"Instance key frame {frame} exceeds its parent duration.");
            }

            var state = instance.EvaluateState(frame) with
            {
                X = key.X ?? instance.EvaluateState(frame).X,
                Y = key.Y ?? instance.EvaluateState(frame).Y,
                RotationZ = key.RotationZ ?? instance.EvaluateState(frame).RotationZ,
                ScaleX = key.ScaleX ?? instance.EvaluateState(frame).ScaleX,
                ScaleY = key.ScaleY ?? instance.EvaluateState(frame).ScaleY,
                Visible = key.Visible ?? instance.EvaluateState(frame).Visible
            };
            instance.SetStateAtFrame(frame, state);
        }

        if (instance.PlaybackMode == DrawingObjectPlaybackMode.HoldFrame)
        {
            instance.HoldFrame = Math.Min(instance.HoldFrame, Math.Max(0, childFrameCount - 1));
        }
    }

    private static void SetSceneDuration(SceneDefinition scene, int frameCount)
    {
        scene.SynchronizeTimelineTracks();
        foreach (var track in scene.Timeline.Tracks.ToArray())
        {
            scene.Timeline.SetTrackDuration(track.Id, frameCount);
        }
    }

    private static string ResolveSvgPath(string bundleDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Length > MaximumPathLength)
        {
            throw new InvalidDataException("SVG frame paths must be short relative paths inside the bundle directory.");
        }

        var root = Path.GetFullPath(bundleDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(bundleDirectory, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SVG frame path escapes the animation bundle directory: {relativePath}");
        }
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The SVG frame does not exist.", fullPath);
        EnsureFileIsSafe(fullPath);
        return fullPath;
    }

    private static void EnsureDirectoryIsSafe(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Animation bundle directories cannot use filesystem reparse points.");
        }
    }

    private static void EnsureFileIsSafe(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Animation bundle files cannot use filesystem reparse points.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrWhiteSpace(root))
        {
            EnsureDirectoryIsSafe(root);
            var parent = Directory.GetParent(root)?.FullName;
            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) break;
            root = parent;
        }
    }

    private static void ValidateSvgSource(string source, string displayPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ImportedSvgRasterizer.MaxImportSourceBytes,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false
        };

        try
        {
            using var text = new StringReader(source);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.Depth > MaximumXmlDepth)
                {
                    throw new InvalidDataException($"SVG XML depth exceeds {MaximumXmlDepth}: {displayPath}");
                }
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                {
                    throw new InvalidDataException($"SVG processing instructions are not supported: {displayPath}");
                }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (string.Equals(reader.LocalName, "script", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(reader.LocalName, "foreignObject", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SVG scripts and foreign objects are not supported: {displayPath}");
                }

                if (!reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                {
                    if (reader.NamespaceURI == "http://www.w3.org/2000/xmlns/") continue;
                    var name = reader.LocalName;
                    var value = reader.Value;
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                        || (name.Equals("href", StringComparison.OrdinalIgnoreCase)
                            && !value.TrimStart().StartsWith('#'))
                        || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                        || ContainsExternalReference(value))
                    {
                        throw new InvalidDataException($"SVG event handlers and external references are not supported: {displayPath}");
                    }
                }
                reader.MoveToElement();
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            throw new InvalidDataException($"SVG XML validation failed: {displayPath}", exception);
        }
    }

    private static bool ContainsExternalReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        if (normalized.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var lower = normalized.ToLowerInvariant();
        return lower.Contains("url(http:", StringComparison.Ordinal)
            || lower.Contains("url(https:", StringComparison.Ordinal)
            || lower.Contains("url(file:", StringComparison.Ordinal)
            || lower.Contains("url(//", StringComparison.Ordinal);
    }

    private static DrawingObjectPlaybackMode NormalizePlaybackMode(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) return DrawingObjectPlaybackMode.Loop;
        return value switch
        {
            nameof(DrawingObjectPlaybackMode.Loop) => DrawingObjectPlaybackMode.Loop,
            nameof(DrawingObjectPlaybackMode.PlayOnce) => DrawingObjectPlaybackMode.PlayOnce,
            nameof(DrawingObjectPlaybackMode.HoldFrame) => DrawingObjectPlaybackMode.HoldFrame,
            _ => throw new InvalidDataException($"{path} must be Loop, PlayOnce, or HoldFrame.")
        };
    }

    private static void ValidateInstanceCount(AnimationBundleInstance[] instances, string path)
    {
        if (instances.Length > MaximumInstancesPerContainer)
        {
            throw new InvalidDataException($"{path} contains too many instances.");
        }
    }

    private static int RequireFrame(int? value, string path, int frameCount)
    {
        if (value is null || value.Value < 0 || value.Value >= frameCount)
        {
            throw new InvalidDataException($"{path} must be between 0 and {Math.Max(0, frameCount - 1)}.");
        }
        return value.Value;
    }

    private static string RequireText(string? value, string path, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            throw new InvalidDataException($"{path} must be between 1 and {maximumLength} characters.");
        }
        return normalized;
    }

    private static void ValidatePositive(int value, string path, int maximum)
    {
        if (value < 1 || value > maximum) throw new InvalidDataException($"{path} must be between 1 and {maximum}.");
    }

    private static void ValidateNumber(float value, string path, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{path} must be finite and between {minimum} and {maximum}.");
        }
    }

    private static void ValidateCoordinate(float? value, string path)
    {
        if (value is { } coordinate) ValidateNumber(coordinate, path, -MaximumCoordinate, MaximumCoordinate);
    }

    private static void ValidateRotation(float? value, string path)
    {
        if (value is { } rotation) ValidateNumber(rotation, path, -MaximumRotation, MaximumRotation);
    }

    private static void ValidateScale(float? value, string path)
    {
        if (value is { } scale) ValidateNumber(scale, path, -MaximumScale, MaximumScale);
    }

    private sealed class AnimationBundleManifest
    {
        public int Version { get; init; }
        public string? Name { get; init; }
        public float Fps { get; init; }
        public int FrameCount { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public AnimationBundleSymbol[]? Symbols { get; init; }
        public AnimationBundleScene? Scene { get; init; }
    }

    private sealed class AnimationBundleSymbol
    {
        public string? Key { get; init; }
        public string? Name { get; init; }
        public int FrameCount { get; init; }
        public AnimationBundleFrame[]? Frames { get; init; }
        public AnimationBundleInstance[]? Instances { get; init; }
    }

    private sealed class AnimationBundleScene
    {
        public string? Name { get; init; }
        public AnimationBundleInstance[]? Instances { get; init; }
    }

    private sealed class AnimationBundleFrame
    {
        public int? Frame { get; init; }
        public string? Path { get; init; }
        public float? X { get; init; }
        public float? Y { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
    }

    private sealed class AnimationBundleInstance
    {
        public string? Name { get; init; }
        public string? Symbol { get; init; }
        public string? Layer { get; init; }
        public float? X { get; init; }
        public float? Y { get; init; }
        public float? RotationZ { get; init; }
        public float? ScaleX { get; init; }
        public float? ScaleY { get; init; }
        public string? PlaybackMode { get; init; }
        public int? HoldFrame { get; init; }
        public AnimationBundleKey[]? Keys { get; init; }
        public bool? Visible { get; init; }
    }

    private sealed class AnimationBundleKey
    {
        public int? Frame { get; init; }
        public float? X { get; init; }
        public float? Y { get; init; }
        public float? RotationZ { get; init; }
        public float? ScaleX { get; init; }
        public float? ScaleY { get; init; }
        public bool? Visible { get; init; }
    }
}
