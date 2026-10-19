using System.Text;
using System.Xml;

namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    internal const int MaxExternalSvgAssetCount = 4_096;
    internal const int MaxExternalSvgAssetNameLength = 80;
    internal const int MaxExternalSvgAssetPathLength = 4_096;

    private readonly List<ExternalSvgAssetDefinition> _externalSvgAssets = [];
    private IReadOnlyList<ExternalSvgAssetDefinition>? _externalSvgAssetView;

    public IReadOnlyList<ExternalSvgAssetDefinition> ExternalSvgAssets =>
        _externalSvgAssetView ??= _externalSvgAssets.AsReadOnly();

    public bool TryAddExternalSvgAsset(
        string? name,
        string? sourcePath,
        string? projectRelativePath,
        string? lastKnownSha256,
        out ExternalSvgAssetDefinition? asset)
    {
        asset = null;
        if (_externalSvgAssets.Count >= MaxExternalSvgAssetCount
            || !TryNormalizeExternalSvgAssetName(name, out var normalizedName)
            || !TryNormalizeExternalSvgSourcePath(sourcePath, out var normalizedSourcePath)
            || !TryNormalizeExternalSvgRelativePath(projectRelativePath, out var normalizedRelativePath)
            || !TryNormalizeExternalSvgSha256(lastKnownSha256, out var normalizedSha256))
        {
            return false;
        }

        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (FindDrawingObject(id) is not null
               || _externalSvgAssets.Any(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)));

        var originalSourcePath = normalizedSourcePath;
        if (!ProjectTemporaryResources.TryCopy(Id, normalizedSourcePath,
                normalizedSha256, 128L * 1024 * 1024, out normalizedSourcePath)) return false;
        normalizedRelativePath = $".Vault/ExternalSvg/{id}.svg";
        asset = new ExternalSvgAssetDefinition
        {
            OriginalSourcePath = originalSourcePath,
            Id = id,
            Name = normalizedName,
            SourcePath = normalizedSourcePath,
            ProjectRelativePath = normalizedRelativePath,
            LastKnownSha256 = normalizedSha256
        };
        _externalSvgAssets.Add(asset);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveExternalSvgAsset(string externalSvgAssetId, out ExternalSvgAssetDefinition? removed)
    {
        removed = FindExternalSvgAsset(externalSvgAssetId);
        if (removed is null || !_externalSvgAssets.Remove(removed))
        {
            removed = null;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRelocateExternalSvgAsset(
        string externalSvgAssetId,
        string? sourcePath,
        string? projectRelativePath,
        string? lastKnownSha256)
    {
        var asset = FindExternalSvgAsset(externalSvgAssetId);
        if (asset is null
            || !TryNormalizeExternalSvgSourcePath(sourcePath, out var normalizedSourcePath)
            || !TryNormalizeExternalSvgRelativePath(projectRelativePath, out var normalizedRelativePath)
            || !TryNormalizeExternalSvgSha256(lastKnownSha256, out var normalizedSha256))
        {
            return false;
        }

        if (string.Equals(asset.SourcePath, normalizedSourcePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(asset.ProjectRelativePath, normalizedRelativePath, StringComparison.Ordinal)
            && string.Equals(asset.LastKnownSha256, normalizedSha256, StringComparison.Ordinal))
        {
            if (!TryReadManagedSvgSource(normalizedSourcePath, out var unchangedSource)) return false;
            if (!TryPrepareSvgAssetSourceUpdates(asset.Id, unchangedSource, out var unchangedUpdates)) return false;
            foreach (var (scene, updates) in unchangedUpdates) scene.ApplyImportedSvgAssetSourceUpdate(updates);
            if (unchangedUpdates.Any(item => item.Updates.Count > 0)) Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var originalSourcePath = normalizedSourcePath;
        if (!ProjectTemporaryResources.TryCopy(Id, normalizedSourcePath,
                normalizedSha256, 128L * 1024 * 1024, out normalizedSourcePath)) return false;
        // Read the managed copy before changing either metadata or any existing placement.
        // The UI validates the SVG before entering this transaction.
        if (!TryReadManagedSvgSource(normalizedSourcePath, out var source)) return false;
        if (!TryPrepareSvgAssetSourceUpdates(asset.Id, source, out var preparedUpdates)) return false;
        normalizedRelativePath = $".Vault/ExternalSvg/{asset.Id}.svg";
        asset.OriginalSourcePath = originalSourcePath;
        asset.SourcePath = normalizedSourcePath;
        asset.ProjectRelativePath = normalizedRelativePath;
        asset.LastKnownSha256 = normalizedSha256;
        foreach (var (scene, updates) in preparedUpdates) scene.ApplyImportedSvgAssetSourceUpdate(updates);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal ExternalSvgAssetRestartSnapshot[] CreateExternalSvgAssetRestartSnapshots()
    {
        return _externalSvgAssets.Select(asset => new ExternalSvgAssetRestartSnapshot
        {
            Id = asset.Id,
            Name = asset.Name,
            OriginalSourcePath = asset.OriginalSourcePath,
            SourcePath = ProjectTemporaryResources.SnapshotSource(Id, asset.SourcePath, asset.LastKnownSha256, 128L * 1024 * 1024),
            ProjectRelativePath = asset.ProjectRelativePath,
            LastKnownSha256 = asset.LastKnownSha256,
            CreatedAt = asset.CreatedAt
        }).ToArray();
    }

    internal void RestoreExternalSvgAssets(IEnumerable<ExternalSvgAssetRestartSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var restored = new List<ExternalSvgAssetDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null
                || restored.Count >= MaxExternalSvgAssetCount
                || !IsValidRestartId(snapshot.Id)
                || FindDrawingObject(snapshot.Id) is not null
                || !ids.Add(snapshot.Id)
                || !TryNormalizeExternalSvgAssetName(snapshot.Name, out var normalizedName)
                || !TryNormalizeExternalSvgSourcePath(snapshot.SourcePath, out var normalizedSourcePath)
                || !TryNormalizeExternalSvgRelativePath(snapshot.ProjectRelativePath, out var normalizedRelativePath)
                || !TryNormalizeExternalSvgSha256(snapshot.LastKnownSha256, out var normalizedSha256))
            {
                throw new InvalidOperationException("The external SVG asset snapshot is invalid.");
            }

            restored.Add(new ExternalSvgAssetDefinition
            {
                Id = snapshot.Id,
                Name = normalizedName,
                OriginalSourcePath = snapshot.OriginalSourcePath,
                SourcePath = normalizedSourcePath,
                ProjectRelativePath = normalizedRelativePath,
                LastKnownSha256 = normalizedSha256,
                CreatedAt = snapshot.CreatedAt
            });
        }

        _externalSvgAssets.Clear();
        _externalSvgAssets.AddRange(restored);
    }

    internal ExternalSvgAssetDefinition? FindExternalSvgAsset(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ExternalSvgAssetDefinition? result = null;
        foreach (var asset in _externalSvgAssets)
        {
            if (!string.Equals(asset.Id, id, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = asset;
        }
        return result;
    }

    private static bool TryReadManagedSvgSource(string path, out string source)
    {
        source = "";
        try
        {
            const int maximumCharacters = 8 * 1024 * 1024;
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var buffer = new char[maximumCharacters + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > maximumCharacters) return false;
            source = VectorScene.NormalizeImportedSvgAssetSource(new string(buffer, 0, length));
            return !string.IsNullOrWhiteSpace(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryPrepareSvgAssetSourceUpdates(string assetId, string source,
        out List<(VectorScene Scene, Dictionary<int, string> Updates)> prepared)
    {
        prepared = [];
        try
        {
            foreach (var (_, scene) in EnumerateAssetScenes())
                prepared.Add((scene, scene.PrepareImportedSvgAssetSourceUpdate(assetId, source)));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or ArgumentException)
        {
            prepared.Clear();
            return false;
        }
    }

    private static bool TryNormalizeExternalSvgAssetName(string? name, out string normalized)
    {
        normalized = name?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaxExternalSvgAssetNameLength;
    }

    private static bool TryNormalizeExternalSvgSourcePath(string? sourcePath, out string normalized)
    {
        normalized = "";
        var candidate = sourcePath?.Trim() ?? "";
        if (candidate.Length is 0 or > MaxExternalSvgAssetPathLength
            || candidate.IndexOf('\0') >= 0
            || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return normalized.Length <= MaxExternalSvgAssetPathLength
            && string.Equals(Path.GetExtension(normalized), ".svg", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryNormalizeExternalSvgRelativePath(string? relativePath, out string normalized)
    {
        normalized = "";
        var candidate = relativePath?.Trim() ?? "";
        if (candidate.Length == 0) return true;
        if (candidate.Length > MaxExternalSvgAssetPathLength
            || candidate.IndexOf('\0') >= 0
            || Path.IsPathRooted(candidate))
        {
            return false;
        }

        var segments = candidate.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) return false;
        normalized = string.Join('/', segments);
        return string.Equals(Path.GetExtension(normalized), ".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeExternalSvgSha256(string? sha256, out string normalized)
    {
        normalized = sha256?.Trim().ToLowerInvariant() ?? "";
        return normalized.Length == 0
            || normalized.Length == 64 && normalized.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
