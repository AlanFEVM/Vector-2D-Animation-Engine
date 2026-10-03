namespace VectorAnimationEngine;

/// <summary>
/// Payload of a single placed bitmap object. Kept small: the pixel data lives once in
/// the managed image asset, and every placed object only points at it. That keeps a
/// symbol with the same image used twenty times from storing twenty copies.
/// </summary>
internal sealed class BitmapObjectData
{
    public string ImageAssetId { get; init; } = "";

    /// <summary>
    /// Object size in vector units the image should cover. Derived from the asset import
    /// settings at placement time and re-derived when those settings change.
    /// </summary>
    public SizeF PlacedSize { get; init; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ImageAssetId)
        && ImageAssetId.Length <= 128
        && ImageAssetId.IndexOf('\0') < 0
        && float.IsFinite(PlacedSize.Width)
        && float.IsFinite(PlacedSize.Height)
        && PlacedSize.Width >= 1
        && PlacedSize.Height >= 1;

    public BitmapObjectData WithPlacedSize(SizeF placedSize) => new()
    {
        ImageAssetId = ImageAssetId,
        PlacedSize = placedSize
    };
}
