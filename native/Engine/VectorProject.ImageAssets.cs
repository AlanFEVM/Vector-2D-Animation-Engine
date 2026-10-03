namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    internal const int MaxImageAssetCount = 4_096;
    internal const int MaxImageAssetNameLength = 80;
    internal const int MaxImageAssetPathLength = 4_096;

    private readonly List<ImageAssetDefinition> _imageAssets = [];
    private IReadOnlyList<ImageAssetDefinition>? _imageAssetView;

    public IReadOnlyList<ImageAssetDefinition> ImageAssets =>
        _imageAssetView ??= _imageAssets.AsReadOnly();

    /// <summary>
    /// The canonical managed path for an image asset. The Vault store requires the managed
    /// file to be named after the asset id, so callers that do not yet have an id must use
    /// <see cref="TryAddImageAssetFromSource"/> instead of inventing a path.
    /// </summary>
    public static string ImageAssetManagedRelativePath(string imageAssetId, string extension) =>
        $".Vault/Images/{imageAssetId}{extension}";

    /// <summary>Allocates an image asset id that collides with no existing symbol or asset.</summary>
    internal string CreateImageAssetId()
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (FindDrawingObject(id) is not null
               || _imageAssets.Any(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)));

        return id;
    }

    /// <summary>
    /// Allocates the asset id, derives the canonical managed path from it, and adds the
    /// entry. This is the supported way to create an image asset: the managed path must
    /// match the id or the Vault store rejects the project on save.
    /// </summary>
    public bool TryAddImageAssetFromSource(
        string? name,
        string? sourcePath,
        string? sha256,
        int pixelWidth,
        int pixelHeight,
        float naturalPixelsPerUnit,
        BitmapImageImportSettings importSettings,
        out ImageAssetDefinition? asset)
    {
        var id = CreateImageAssetId();
        return TryAddImageAsset(
            id,
            name,
            sourcePath,
            ImageAssetManagedRelativePath(id, BitmapImageFormats.ExtensionFor(importSettings.Compression)),
            sha256,
            pixelWidth,
            pixelHeight,
            naturalPixelsPerUnit,
            importSettings,
            out asset);
    }

    public bool TryAddImageAsset(
        string? name,
        string? sourcePath,
        string? projectRelativePath,
        string? sha256,
        int pixelWidth,
        int pixelHeight,
        float naturalPixelsPerUnit,
        BitmapImageImportSettings importSettings,
        out ImageAssetDefinition? asset) =>
        TryAddImageAsset(
            CreateImageAssetId(),
            name,
            sourcePath,
            projectRelativePath,
            sha256,
            pixelWidth,
            pixelHeight,
            naturalPixelsPerUnit,
            importSettings,
            out asset);

    /// <summary>
    /// Adds an image asset under a known id. The managed path is validated against that id
    /// by the Vault store, so callers deriving a path must derive it from this same id.
    /// </summary>
    internal bool TryAddImageAsset(
        string id,
        string? name,
        string? sourcePath,
        string? projectRelativePath,
        string? sha256,
        int pixelWidth,
        int pixelHeight,
        float naturalPixelsPerUnit,
        BitmapImageImportSettings importSettings,
        out ImageAssetDefinition? asset)
    {
        asset = null;
        if (_imageAssets.Count >= MaxImageAssetCount
            || string.IsNullOrWhiteSpace(id)
            || id.Length > MaxImageAssetPathLength
            || id.IndexOf('\0') >= 0
            || FindDrawingObject(id) is not null
            || _imageAssets.Any(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            || !TryNormalizeImageAssetName(name, out var normalizedName)
            || !TryNormalizeImageSourcePath(sourcePath, out var normalizedSourcePath)
            || !TryNormalizeImageRelativePath(projectRelativePath, out var normalizedRelativePath)
            || !TryNormalizeImageSha256(sha256, out var normalizedSha256)
            || !IsValidImageDimensions(pixelWidth, pixelHeight)
            || !TryNormalizeNaturalPixelsPerUnit(naturalPixelsPerUnit, out var normalizedPixelsPerUnit)
            || !importSettings.IsValid)
        {
            return false;
        }

        asset = new ImageAssetDefinition
        {
            Id = id,
            Name = normalizedName,
            SourcePath = normalizedSourcePath,
            ProjectRelativePath = normalizedRelativePath,
            Sha256 = normalizedSha256,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            NaturalPixelsPerUnit = normalizedPixelsPerUnit,
            ImportSettings = importSettings
        };
        _imageAssets.Add(asset);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemoveImageAsset(string imageAssetId, out ImageAssetDefinition? removed)
    {
        removed = FindImageAsset(imageAssetId);
        if (removed is null || !_imageAssets.Remove(removed))
        {
            removed = null;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRenameImageAsset(string imageAssetId, string? name)
    {
        var asset = FindImageAsset(imageAssetId);
        if (asset is null || !TryNormalizeImageAssetName(name, out var normalizedName))
        {
            return false;
        }
        if (string.Equals(asset.Name, normalizedName, StringComparison.Ordinal)) return true;

        asset.Name = normalizedName;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Replaces the import settings of an existing image. Pixel-affecting settings must be
    /// paired with a re-encode, so the caller supplies the resulting managed file metadata.
    /// </summary>
    public bool TryApplyImageImportSettings(
        string imageAssetId,
        BitmapImageImportSettings importSettings,
        int pixelWidth,
        int pixelHeight,
        string? sha256)
    {
        var asset = FindImageAsset(imageAssetId);
        if (asset is null
            || !importSettings.IsValid
            || !IsValidImageDimensions(pixelWidth, pixelHeight)
            || !TryNormalizeImageSha256(sha256, out var normalizedSha256))
        {
            return false;
        }

        if (asset.ImportSettings == importSettings
            && asset.PixelWidth == pixelWidth
            && asset.PixelHeight == pixelHeight
            && string.Equals(asset.Sha256, normalizedSha256, StringComparison.Ordinal))
        {
            return true;
        }

        asset.ImportSettings = importSettings;
        asset.PixelWidth = pixelWidth;
        asset.PixelHeight = pixelHeight;
        asset.Sha256 = normalizedSha256;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Records a relocated managed file after Relink without touching import settings.</summary>
    public bool TryRelinkImageAsset(
        string imageAssetId,
        string? sourcePath,
        string? projectRelativePath,
        string? sha256,
        int pixelWidth,
        int pixelHeight,
        float naturalPixelsPerUnit)
    {
        var asset = FindImageAsset(imageAssetId);
        if (asset is null
            || !TryNormalizeImageSourcePath(sourcePath, out var normalizedSourcePath)
            || !TryNormalizeImageRelativePath(projectRelativePath, out var normalizedRelativePath)
            || !TryNormalizeImageSha256(sha256, out var normalizedSha256)
            || !IsValidImageDimensions(pixelWidth, pixelHeight)
            || !TryNormalizeNaturalPixelsPerUnit(naturalPixelsPerUnit, out var normalizedPixelsPerUnit))
        {
            return false;
        }

        asset.SourcePath = normalizedSourcePath;
        asset.ProjectRelativePath = normalizedRelativePath;
        asset.Sha256 = normalizedSha256;
        asset.PixelWidth = pixelWidth;
        asset.PixelHeight = pixelHeight;
        asset.NaturalPixelsPerUnit = normalizedPixelsPerUnit;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal ImageAssetRestartSnapshot[] CreateImageAssetRestartSnapshots()
    {
        return _imageAssets.Select(asset => new ImageAssetRestartSnapshot
        {
            Id = asset.Id,
            Name = asset.Name,
            SourcePath = asset.SourcePath,
            ProjectRelativePath = asset.ProjectRelativePath,
            Sha256 = asset.Sha256,
            PixelWidth = asset.PixelWidth,
            PixelHeight = asset.PixelHeight,
            NaturalPixelsPerUnit = asset.NaturalPixelsPerUnit,
            ImportSettings = asset.ImportSettings,
            CreatedAt = asset.CreatedAt
        }).ToArray();
    }

    internal void RestoreImageAssets(IEnumerable<ImageAssetRestartSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var restored = new List<ImageAssetDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null
                || restored.Count >= MaxImageAssetCount
                || !IsValidRestartId(snapshot.Id)
                || FindDrawingObject(snapshot.Id) is not null
                || !ids.Add(snapshot.Id)
                || !TryNormalizeImageAssetName(snapshot.Name, out var normalizedName)
                || !TryNormalizeImageSourcePath(snapshot.SourcePath, out var normalizedSourcePath)
                || !TryNormalizeImageRelativePath(snapshot.ProjectRelativePath, out var normalizedRelativePath)
                || !TryNormalizeImageSha256(snapshot.Sha256, out var normalizedSha256)
                || !IsValidImageDimensions(snapshot.PixelWidth, snapshot.PixelHeight)
                || !TryNormalizeNaturalPixelsPerUnit(snapshot.NaturalPixelsPerUnit, out var normalizedPixelsPerUnit)
                || !snapshot.ImportSettings.IsValid)
            {
                throw new InvalidOperationException("The image asset snapshot is invalid.");
            }

            restored.Add(new ImageAssetDefinition
            {
                Id = snapshot.Id,
                Name = normalizedName,
                SourcePath = normalizedSourcePath,
                ProjectRelativePath = normalizedRelativePath,
                Sha256 = normalizedSha256,
                PixelWidth = snapshot.PixelWidth,
                PixelHeight = snapshot.PixelHeight,
                NaturalPixelsPerUnit = normalizedPixelsPerUnit,
                ImportSettings = snapshot.ImportSettings,
                CreatedAt = snapshot.CreatedAt
            });
        }

        _imageAssets.Clear();
        _imageAssets.AddRange(restored);
    }

    internal ImageAssetDefinition? FindImageAsset(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ImageAssetDefinition? result = null;
        foreach (var asset in _imageAssets)
        {
            if (!string.Equals(asset.Id, id, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = asset;
        }
        return result;
    }

    internal static bool TryNormalizeImageAssetName(string? name, out string normalized)
    {
        normalized = name?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaxImageAssetNameLength;
    }

    private static bool TryNormalizeImageSourcePath(string? sourcePath, out string normalized)
    {
        normalized = sourcePath?.Trim() ?? "";
        if (normalized.Length == 0) return true;
        if (normalized.Length > MaxImageAssetPathLength
            || normalized.IndexOf('\0') >= 0
            || !Path.IsPathFullyQualified(normalized))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return normalized.Length <= MaxImageAssetPathLength
            && BitmapImageFormats.IsSupportedExtension(Path.GetExtension(normalized));
    }

    private static bool TryNormalizeImageRelativePath(string? relativePath, out string normalized)
    {
        normalized = relativePath?.Trim() ?? "";
        if (normalized.Length == 0) return true;
        if (normalized.Length > MaxImageAssetPathLength
            || normalized.IndexOf('\0') >= 0
            || Path.IsPathRooted(normalized))
        {
            return false;
        }

        var segments = normalized.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) return false;
        normalized = string.Join('/', segments);
        return BitmapImageFormats.IsSupportedExtension(Path.GetExtension(normalized));
    }

    private static bool TryNormalizeImageSha256(string? sha256, out string normalized)
    {
        normalized = sha256?.Trim().ToLowerInvariant() ?? "";
        return normalized.Length == 0
            || normalized.Length == 64 && normalized.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsValidImageDimensions(int pixelWidth, int pixelHeight) =>
        pixelWidth is >= 1 and <= BitmapImageFormats.MaximumPixelDimension
        && pixelHeight is >= 1 and <= BitmapImageFormats.MaximumPixelDimension;

    private static bool TryNormalizeNaturalPixelsPerUnit(float value, out float normalized)
    {
        normalized = value;
        if (!float.IsFinite(value) || value <= 0) return false;
        normalized = Math.Clamp(
            value,
            BitmapImageImportSettings.MinimumPixelsPerUnit,
            BitmapImageImportSettings.MaximumPixelsPerUnit);
        return true;
    }
}
