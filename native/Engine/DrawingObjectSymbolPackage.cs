using System.Text;
using System.Text.Json;

namespace VectorAnimationEngine;

/// <summary>
/// A self-contained, re-importable snapshot of one or more symbols.
///
/// A <c>.V2DSymbol</c> file carries the requested symbol plus the complete closure
/// of symbols it nests, so nested timeline relationships survive a round trip even
/// when the receiving project has never seen the dependencies. Library metadata
/// travels with it: the asset-tag definitions (name and colour), the asset-folder
/// chain the symbol lived in, and the per-symbol tag assignments.
///
/// The file is Brotli-compressed UTF-8 JSON. The geometry payload deliberately reuses
/// <see cref="VectorSceneSnapshot"/> and <see cref="AnimationTimelineSnapshot"/> - the
/// same DTOs the managed project store round-trips - so a symbol written here and read
/// back is byte-for-byte the same model the project format would have produced.
/// </summary>
internal sealed class DrawingObjectSymbolPackage
{
    internal const string FileExtension = ".V2DSymbol";
    internal const int CurrentFormatVersion = 1;
    internal const int MinimumReadableFormatVersion = 1;

    internal const int MaxSymbolCount = 10_000;
    internal const int MaxFolderCount = 10_000;
    internal const int MaxTagCount = VectorProject.MaxAssetTagCount;
    internal const int MaxSymbolNameLength = 512;
    internal const int MaxFolderNameLength = 512;
    internal const int MaxTagNameLength = VectorProject.MaxAssetTagNameLength;
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    internal const long MaxDecodedBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        MaxDepth = 256,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Format version of the file that was read.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Display name recorded at export time, used as the import default.</summary>
    public string RootSymbolName { get; init; } = "";

    /// <summary>Stable id of the symbol the user exported, within this package.</summary>
    public string RootSymbolId { get; init; } = "";

    /// <summary>Every symbol in the dependency closure, ordered so dependencies precede dependents.</summary>
    public SymbolPackageEntry[] Symbols { get; init; } = [];

    /// <summary>
    /// Library folders referenced by <see cref="SymbolPackageEntry.FolderPath"/>, as
    /// slash-separated paths rooted at the library top level.
    /// </summary>
    public FolderPackageEntry[] Folders { get; init; } = [];

    /// <summary>Asset-tag definitions (name and colour) used by the enclosed symbols.</summary>
    public TagPackageEntry[] Tags { get; init; } = [];

    /// <summary>One symbol inside the package.</summary>
    internal sealed class SymbolPackageEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "Symbol";
        public string Kind { get; init; } = "Symbol";
        public string Detail { get; init; } = "Reusable symbol";
        public float AnchorX { get; init; }
        public float AnchorY { get; init; }
        public string[] TagNames { get; init; } = [];
        public string FolderPath { get; init; } = "";
        public GeometrySnapshot Geometry { get; init; } = new();
        public AnimationTimelineSnapshot Timeline { get; init; } = new();
        public InstancePackageEntry[] Instances { get; init; } = [];
    }

    /// <summary>
    /// Geometry payload for one symbol. The SVG document holds the packed scene and
    /// the timeline document holds layers, cel ownership and exposure state, matching
    /// the split the managed project format uses.
    /// </summary>
    internal sealed class GeometrySnapshot
    {
        public string Svg { get; init; } = "";
        public DrawingLayerEntry[] Layers { get; init; } = [];
        public int ActiveLayer { get; init; }
        public bool? OnionSkinEnabled { get; init; }
        public int? OnionSkinPreviousFrames { get; init; }
        public int? OnionSkinNextFrames { get; init; }
        public ushort[] ObjectLayer { get; init; } = [];
        public int[] ObjectKeyframeFrame { get; init; } = [];
    }

    internal sealed class DrawingLayerEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public DrawingLayerKind Kind { get; init; }
        public string ParentLayerId { get; init; } = "";
        public string MaskLayerId { get; init; } = "";
        public bool Locked { get; init; }
        public bool Visible { get; init; }
        public float Opacity { get; init; }
        public LayerBlendMode BlendMode { get; init; } = LayerBlendMode.Normal;
        public int ColorArgb { get; init; }
        public bool Outline { get; init; }
        public bool OnionSkin { get; init; }
        public int StartFrame { get; init; }
        public int EndFrame { get; init; }
    }

    /// <summary>
    /// A nested symbol instance. <see cref="DrawingObjectId"/> refers to another
    /// symbol in the same package; it is remapped on import.
    /// </summary>
    internal sealed class InstancePackageEntry
    {
        public string Id { get; init; } = "";
        public string DrawingObjectId { get; init; } = "";
        public string SceneLayerId { get; init; } = "";
        public string Name { get; init; } = "Instance";
        public bool Visible { get; init; } = true;
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
        public float RotationX { get; init; }
        public float RotationY { get; init; }
        public float RotationZ { get; init; }
        public float SkewX { get; init; }
        public float SkewY { get; init; }
        public float ScaleX { get; init; } = 1;
        public float ScaleY { get; init; } = 1;
        public float ScaleZ { get; init; } = 1;
        public System.Numerics.Vector3 RotationPivot { get; init; }
        public System.Numerics.Vector3 ScalePivot { get; init; }
        public DistortWarp? Distortion { get; init; }
        public float Alpha { get; init; } = 1;
        public int TintArgb { get; init; } = unchecked((int)0xffffffff);
        public SymbolFilters Filters { get; init; }
        public SpatialOpticalMaterial? OpticalMaterialOverride { get; init; }
        public decimal PlaybackFps { get; init; } = 30m;
        public DrawingObjectPlaybackMode PlaybackMode { get; init; } = DrawingObjectPlaybackMode.PlayOnce;
        public int HoldFrame { get; init; }
        public InstanceStateKeyframe[] StateKeyframes { get; init; } = [];
    }

    internal sealed class FolderPackageEntry
    {
        public string Path { get; init; } = "";
    }

    internal sealed class TagPackageEntry
    {
        public string Name { get; init; } = "Tag";
        public int ColorArgb { get; init; } = Color.FromArgb(66, 165, 245).ToArgb();
    }

    /// <summary>
    /// Serializes this package to a compressed <c>.V2DSymbol</c> file, creating the
    /// target directory when needed.
    /// </summary>
    internal void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (json.LongLength > MaxDecodedBytes)
        {
            throw new InvalidDataException("The symbol package exceeds the supported decoded size limit.");
        }

        var compressed = ProjectPayloadCompression.Compress(json);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, compressed);
    }

    /// <summary>
    /// Reads and structurally validates a <c>.V2DSymbol</c> file. Structural checks
    /// happen before the caller is allowed to mutate a live project.
    /// </summary>
    internal static DrawingObjectSymbolPackage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The symbol package does not exist.", path);
        if (file.Length > MaxPackageBytes)
        {
            throw new InvalidDataException("The symbol package exceeds the supported file size limit.");
        }

        var decoded = ProjectPayloadCompression.Decompress(
            File.ReadAllBytes(path),
            checked((int)MaxDecodedBytes));
        DrawingObjectSymbolPackage package;
        try
        {
            package = JsonSerializer.Deserialize<DrawingObjectSymbolPackage>(decoded, JsonOptions)
                ?? throw new InvalidDataException("The symbol package is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The symbol package contains invalid JSON.", exception);
        }

        package.Validate();
        return package;
    }

    /// <summary>
    /// Rejects structurally invalid or unsafe packages before any project mutation:
    /// unsupported versions, duplicate or dangling symbol references, cycles,
    /// malformed layer/instance metadata and traversal-shaped folder paths.
    /// </summary>
    internal void Validate()
    {
        if (FormatVersion < MinimumReadableFormatVersion || FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException("The symbol package format version is unsupported.");
        }
        if (Symbols is null || Symbols.Length == 0 || Symbols.Length > MaxSymbolCount)
        {
            throw new InvalidDataException("The symbol package contains no symbols.");
        }
        if (Folders is null || Tags is null)
        {
            throw new InvalidDataException("The symbol package library metadata is missing.");
        }
        if (!IsSafeId(RootSymbolId))
        {
            throw new InvalidDataException("The symbol package root identifier is invalid.");
        }

        var byId = new Dictionary<string, SymbolPackageEntry>(StringComparer.Ordinal);
        foreach (var symbol in Symbols)
        {
            if (symbol is null
                || !IsSafeId(symbol.Id)
                || !byId.TryAdd(symbol.Id, symbol)
                || string.IsNullOrWhiteSpace(symbol.Name)
                || symbol.Name.Length > MaxSymbolNameLength
                || symbol.Geometry is null
                || symbol.Timeline is null
                || symbol.Instances is null
                || symbol.TagNames is null
                || symbol.FolderPath is null)
            {
                throw new InvalidDataException("The symbol package contains an invalid symbol entry.");
            }
        }

        if (!byId.ContainsKey(RootSymbolId))
        {
            throw new InvalidDataException("The symbol package does not contain its declared root symbol.");
        }

        // Instances must resolve inside the package, and the containment graph must be
        // acyclic so import can materialize it in any order.
        foreach (var symbol in Symbols)
        {
            foreach (var instance in symbol.Instances)
            {
                if (instance is null
                    || !IsSafeId(instance.Id)
                    || !IsSafeId(instance.DrawingObjectId)
                    || !byId.ContainsKey(instance.DrawingObjectId)
                    || string.Equals(instance.DrawingObjectId, symbol.Id, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(instance.Name)
                    || instance.Name.Length > MaxSymbolNameLength
                    || !float.IsFinite(instance.X)
                    || !float.IsFinite(instance.Y)
                    || !float.IsFinite(instance.Z)
                    || !float.IsFinite(instance.Alpha)
                    || instance.Alpha < 0f
                    || instance.Alpha > 1f
                    || !Enum.IsDefined(instance.PlaybackMode)
                    || instance.StateKeyframes is null
                    || !instance.Filters.IsValid
                    || instance.StateKeyframes.Any(key => !key.State.Filters.IsValid))
                {
                    throw new InvalidDataException("The symbol package contains an invalid nested instance.");
                }
            }

            var instanceIds = symbol.Instances.Select(item => item.Id).ToArray();
            if (instanceIds.Distinct(StringComparer.Ordinal).Count() != instanceIds.Length)
            {
                throw new InvalidDataException("The symbol package contains duplicate instance identifiers.");
            }
        }

        RejectContainmentCycles(byId);
        ValidateFolders();
        ValidateTags();
    }

    /// <summary>
    /// Detects self-referential containment chains, which would make the imported
    /// graph impossible to order and would recurse forever during composition.
    /// </summary>
    private void RejectContainmentCycles(IReadOnlyDictionary<string, SymbolPackageEntry> byId)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var symbol in Symbols)
        {
            if (HasCycle(symbol.Id, byId, state))
            {
                throw new InvalidDataException("The symbol package contains a recursive symbol relationship.");
            }
        }
    }

    private static bool HasCycle(
        string id,
        IReadOnlyDictionary<string, SymbolPackageEntry> byId,
        IDictionary<string, int> state)
    {
        if (state.TryGetValue(id, out var existing)) return existing == 1;
        state[id] = 1;
        foreach (var instance in byId[id].Instances)
        {
            if (HasCycle(instance.DrawingObjectId, byId, state)) return true;
        }

        state[id] = 2;
        return false;
    }

    private void ValidateFolders()
    {
        if (Folders.Length > MaxFolderCount)
        {
            throw new InvalidDataException("The symbol package declares too many library folders.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in Folders)
        {
            if (folder is null || !IsSafeFolderPath(folder.Path) || !seen.Add(folder.Path))
            {
                throw new InvalidDataException("The symbol package contains an invalid library folder path.");
            }
        }

        foreach (var symbol in Symbols)
        {
            if (symbol.FolderPath.Length > 0
                && (!IsSafeFolderPath(symbol.FolderPath) || !seen.Contains(symbol.FolderPath)))
            {
                throw new InvalidDataException("A symbol references an undeclared library folder path.");
            }
        }
    }

    private void ValidateTags()
    {
        if (Tags.Length > MaxTagCount)
        {
            throw new InvalidDataException("The symbol package declares too many asset tags.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in Tags)
        {
            if (tag is null
                || string.IsNullOrWhiteSpace(tag.Name)
                || tag.Name.Length > MaxTagNameLength
                || !names.Add(tag.Name))
            {
                throw new InvalidDataException("The symbol package contains an invalid asset tag.");
            }
        }

        // Tag assignments are resolved by name, so every referenced name must exist.
        foreach (var symbol in Symbols)
        {
            if (symbol.TagNames.Any(name =>
                    string.IsNullOrWhiteSpace(name)
                    || name.Length > MaxTagNameLength
                    || !names.Contains(name)))
            {
                throw new InvalidDataException("A symbol references an undeclared asset tag.");
            }

            var distinct = symbol.TagNames.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct != symbol.TagNames.Length)
            {
                throw new InvalidDataException("A symbol references the same asset tag more than once.");
            }
        }
    }

    /// <summary>
    /// True for identifiers safe to round-trip through JSON and file names.
    /// </summary>
    internal static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        foreach (var character in id)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_')) return false;
        }

        return id is not "." and not "..";
    }

    /// <summary>
    /// True for slash-separated relative folder paths that cannot escape the library
    /// root or address a reparse point.
    /// </summary>
    internal static bool IsSafeFolderPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Length > 1024 || path.StartsWith('/') || path.EndsWith('/')) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length is 0 or > MaxFolderNameLength) return false;
            if (segment is "." or "..") return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (segment.Contains(Path.DirectorySeparatorChar)
                || segment.Contains(Path.AltDirectorySeparatorChar))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the path of a library folder chain from leaf to root, excluding the
    /// library root itself which is represented by an empty path.
    /// </summary>
    internal static string BuildFolderPath(IEnumerable<string> namesFromRoot)
    {
        var segments = namesFromRoot
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToArray();
        return segments.Length == 0 ? "" : string.Join('/', segments);
    }

    /// <summary>
    /// Encodes an arbitrary string (the geometry SVG payload) for JSON transport.
    /// </summary>
    internal static string EncodePayload(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>Decodes a payload produced by <see cref="EncodePayload"/>.</summary>
    internal static string DecodePayload(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidDataException("The symbol package contains an empty geometry payload.");
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The symbol package geometry payload is malformed.", exception);
        }
    }
}
