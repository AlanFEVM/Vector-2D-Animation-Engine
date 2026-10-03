namespace VectorAnimationEngine;

/// <summary>
/// A bitmap image imported into the project. Unlike <see cref="ExternalSvgAssetDefinition"/>
/// the pixel data is copied into the managed <c>.Vault/Images</c> directory, so the asset
/// travels with the project and its checksum can be verified on every load.
/// </summary>
internal sealed class ImageAssetDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; internal set; } = "Image";

    /// <summary>Original file the image was imported from. Retained for diagnostics and Relink.</summary>
    public string SourcePath { get; internal set; } = "";
    /// <summary>Managed copy relative to the project root, for example <c>.Vault/Images/&lt;id&gt;.png</c>.</summary>
    public string ProjectRelativePath { get; internal set; } = "";
    /// <summary>SHA-256 of the managed file, used to detect tampering and reuse cached rasters.</summary>
    public string Sha256 { get; internal set; } = "";

    public int PixelWidth { get; internal set; }
    public int PixelHeight { get; internal set; }
    /// <summary>Sampling density detected from the source metadata, or 96 for formats that do not store one.</summary>
    public float NaturalPixelsPerUnit { get; internal set; } = 96f;

    public BitmapImageImportSettings ImportSettings { get; internal set; } = BitmapImageImportSettings.Default;
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public SizeF PixelSize => new(PixelWidth, PixelHeight);

    /// <summary>Placed object size in vector units for the current import settings.</summary>
    public SizeF ResolvePlacedSize() => ImportSettings.ResolvePlacedSize(PixelSize, NaturalPixelsPerUnit);
}
