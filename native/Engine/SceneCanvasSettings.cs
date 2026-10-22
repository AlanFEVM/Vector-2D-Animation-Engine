using System.Text.Json.Serialization;

namespace VectorAnimationEngine;

internal enum SceneBackgroundKind
{
    None,
    Solid,
    Image
}

internal enum CanvasImageFit
{
    Contain,
    Cover,
    Original
}

internal sealed record SceneCanvasSettings
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public SceneBackgroundKind BackgroundKind { get; init; } = SceneBackgroundKind.None;
    public int ColorArgb { get; init; } = Color.White.ToArgb();
    public string ImageAssetId { get; init; } = "";
    public CanvasImageFit ImageFit { get; init; } = CanvasImageFit.Contain;
    public float ImageScale { get; init; } = 1f;
    public float ImageOffsetX { get; init; }
    public float ImageOffsetY { get; init; }
    public float ImageOpacity { get; init; } = 1f;
    public bool DimOutside { get; init; } = true;

    [JsonIgnore]
    public bool IsValid => Width is >= 1 and <= 32768
        && Height is >= 1 and <= 32768
        && Enum.IsDefined(BackgroundKind)
        && Enum.IsDefined(ImageFit)
        && ImageAssetId is not null
        && (BackgroundKind != SceneBackgroundKind.Image || !string.IsNullOrWhiteSpace(ImageAssetId))
        && float.IsFinite(ImageScale) && ImageScale is >= 0.001f and <= 100f
        && float.IsFinite(ImageOffsetX) && ImageOffsetX is >= -5_000_000f and <= 5_000_000f
        && float.IsFinite(ImageOffsetY) && ImageOffsetY is >= -5_000_000f and <= 5_000_000f
        && float.IsFinite(ImageOpacity) && ImageOpacity is >= 0f and <= 1f;
}
