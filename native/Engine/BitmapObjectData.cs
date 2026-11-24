namespace VectorAnimationEngine;

/// <summary>
/// Payload of a single placed bitmap object. Kept small: the pixel data lives once in
/// the managed image asset, and every placed object only points at it. That keeps a
/// symbol with the same image used twenty times from storing twenty copies.
/// </summary>
internal sealed class BitmapObjectData
{
    public string ImageAssetId { get; init; } = "";

    // Scene block projection identity; older projects used its complement for shadow objects.
    public long? BlockGridKey { get; init; }

    // Packed top-left, top-right, bottom-left, bottom-right darkness (0 = unchanged).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public uint ShadowCorners { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public uint WarmLightCorners { get; init; }

    // Optional Minecraft grass/foliage tint; null preserves legacy untinted objects.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? BiomeTintArgb { get; init; }

    // Grass block sides tint only the grass overlay, not the dirt in the base texture.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BiomeTintGrassSide { get; init; }

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
        && (BiomeTintArgb is { } tint ? (uint)tint >> 24 == 0xff : !BiomeTintGrassSide)
        && float.IsFinite(PlacedSize.Width)
        && float.IsFinite(PlacedSize.Height)
        && PlacedSize.Width >= 1
        && PlacedSize.Height >= 1;

    public BitmapObjectData WithPlacedSize(SizeF placedSize) => new()
    {
        ImageAssetId = ImageAssetId,
        BlockGridKey = BlockGridKey,
        ShadowCorners = ShadowCorners,
        WarmLightCorners = WarmLightCorners,
        BiomeTintArgb = BiomeTintArgb,
        BiomeTintGrassSide = BiomeTintGrassSide,
        VisibleContours = VisibleContours,
        IsBrokenApart = IsBrokenApart,
        PlacedSize = placedSize
    };
}
