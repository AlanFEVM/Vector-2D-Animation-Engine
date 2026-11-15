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

    // Normalized image coordinates; null means the whole image, empty means erased.
    public PointF[][]? VisibleContours { get; init; }

    // Editable texture fill: marquee/lasso may detach a clipped region while
    // retaining the original asset and its pixel-to-world mapping.
    public bool IsBrokenApart { get; init; }

    public bool IsValid =>
        (VisibleContours is null || (VisibleContours.Length <= 65536
            && VisibleContours.All(contour => contour is { Length: >= 3 }
                && contour.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)))))
        &&
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
        VisibleContours = VisibleContours,
        IsBrokenApart = IsBrokenApart,
        PlacedSize = placedSize
    };
}
