namespace VectorAnimationEngine;

/// <summary>
/// Filter modes mirroring a texture importer: the mode decides which mag filter the
/// renderer uses, and whether the source may be downscaled at import time.
/// </summary>
internal enum ImageFilterMode : byte
{
    Point = 0,
    Bilinear = 1,
    Trilinear = 2
}

/// <summary>
/// Renderer-neutral sampling request derived from <see cref="ImageFilterMode"/>. The Direct2D
/// and GDI paths each translate this into their own interpolation type, so the engine never
/// depends on a renderer-specific enum.
/// </summary>
internal enum BitmapSampling
{
    /// <summary>Nearest-neighbour: hard pixel edges, used by pixel-art imports.</summary>
    Point,
    /// <summary>Smooth sampling, used by bilinear and trilinear imports.</summary>
    Linear
}

internal static class ImageFilterModes
{
    internal static BitmapSampling ToSampling(ImageFilterMode mode) =>
        mode == ImageFilterMode.Point ? BitmapSampling.Point : BitmapSampling.Linear;
}

internal enum ImageCompression : byte
{
    /// <summary>Re-encode as PNG. Keeps alpha, larger files.</summary>
    LosslessPng = 0,
    /// <summary>Re-encode as JPEG at <see cref="BitmapImageImportSettings.CompressionQuality"/>. No alpha.</summary>
    Jpeg = 1,
    /// <summary>Copy the source bytes into the project unchanged.</summary>
    Raw = 2
}

internal enum ImageAlphaSource : byte
{
    /// <summary>Use the image alpha channel when the file has one.</summary>
    FromInput = 0,
    /// <summary>Treat every pixel as fully opaque.</summary>
    None = 1
}

/// <summary>
/// Per-image import settings. Value type with init-only members so a settings edit
/// produces a new value instead of mutating a shared instance.
/// </summary>
internal readonly record struct BitmapImageImportSettings
{
    /// <summary>
    /// Explicit parameterless constructor so the defaulted members below are legal
    /// on a record struct while still allowing <c>with</c> expressions.
    /// </summary>
    public BitmapImageImportSettings()
    {
    }

    internal const int MinimumPixelsPerUnit = 1;
    internal const int MaximumPixelsPerUnit = 4096;
    internal const int MinimumMaxSize = 16;
    internal const int MaximumMaxSize = 8192;
    internal const int MinimumCompressionQuality = 1;
    internal const int MaximumCompressionQuality = 100;

    public ImageFilterMode FilterMode { get; init; } = ImageFilterMode.Bilinear;
    public bool Mipmaps { get; init; } = true;
    /// <summary>
    /// Sampling density. The asset records its own natural PPI; this value drives the
    /// placed object size in vector units the same way Unity derives sprite size.
    /// </summary>
    public int PixelsPerUnit { get; init; } = 100;
    public bool NonPowerOfTwoScale { get; init; }
    public int MaxSize { get; init; } = MaximumMaxSize;
    public ImageCompression Compression { get; init; } = ImageCompression.LosslessPng;
    public int CompressionQuality { get; init; } = 90;
    public bool ReadWriteEnabled { get; init; } = true;
    /// <summary>Sprite pivot in normalized 0..1 image space; 0.5,0.5 is the center.</summary>
    public float PivotX { get; init; } = 0.5f;
    public float PivotY { get; init; } = 0.5f;
    public ImageAlphaSource AlphaSource { get; init; } = ImageAlphaSource.FromInput;
    public bool GenerateMipmapsForAlpha { get; init; } = true;

    public static BitmapImageImportSettings Default => new();

    public bool IsValid =>
        Enum.IsDefined(FilterMode)
        && Enum.IsDefined(Compression)
        && Enum.IsDefined(AlphaSource)
        && PixelsPerUnit is >= MinimumPixelsPerUnit and <= MaximumPixelsPerUnit
        && MaxSize is >= MinimumMaxSize and <= MaximumMaxSize
        && CompressionQuality is >= MinimumCompressionQuality and <= MaximumCompressionQuality
        && float.IsFinite(PivotX) && PivotX is >= 0f and <= 1f
        && float.IsFinite(PivotY) && PivotY is >= 0f and <= 1f;

    /// <summary>
    /// Applies the settings that change stored pixels and reports the pixel size the
    /// managed file will have. Never upscales: MaxSize only downscales.
    /// </summary>
    public SizeF ResolveStoredPixelSize(SizeF sourcePixelSize)
    {
        if (!float.IsFinite(sourcePixelSize.Width)
            || !float.IsFinite(sourcePixelSize.Height)
            || sourcePixelSize.Width < 1
            || sourcePixelSize.Height < 1)
        {
            return SizeF.Empty;
        }

        var scale = Math.Min(1f, MaxSize / Math.Max(sourcePixelSize.Width, sourcePixelSize.Height));
        var width = Math.Max(1, (int)MathF.Round(sourcePixelSize.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(sourcePixelSize.Height * scale));
        if (NonPowerOfTwoScale)
        {
            width = NearestPowerOfTwoAtMost(width);
            height = NearestPowerOfTwoAtMost(height);
        }

        return new SizeF(width, height);
    }

    /// <summary>
    /// Converts a stored pixel size into the placed object size in vector units. The
    /// natural PPI scales the density so a 300 PPI scan imports at its physical size.
    /// </summary>
    public SizeF ResolvePlacedSize(SizeF storedPixelSize, float naturalPixelsPerUnit)
    {
        var density = Math.Clamp(
            float.IsFinite(naturalPixelsPerUnit) && naturalPixelsPerUnit > 0 ? naturalPixelsPerUnit : PixelsPerUnit,
            MinimumPixelsPerUnit,
            MaximumPixelsPerUnit);
        var scale = VectorUnits.UnitsPerPixel * PixelsPerUnit / density;
        return new SizeF(
            Math.Max(1f, (float)Math.Round(storedPixelSize.Width * scale)),
            Math.Max(1f, (float)Math.Round(storedPixelSize.Height * scale)));
    }

    private static int NearestPowerOfTwoAtMost(int value)
    {
        var result = 1;
        while (result * 2 <= value) result *= 2;
        return result;
    }
}
