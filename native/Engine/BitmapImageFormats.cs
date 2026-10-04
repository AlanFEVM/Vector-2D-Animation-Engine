using System.Drawing.Imaging;

namespace VectorAnimationEngine;

/// <summary>
/// Central allow-list for bitmap image formats the importer accepts and the
/// managed-file codec writes. Kept in the engine so the project validator and the
/// UI file dialog cannot drift apart.
/// </summary>
internal static class BitmapImageFormats
{
    internal const int MaximumPixelDimension = 8192;
    internal const long MaximumDecodedPixels = 64L * 1024 * 1024;
    internal const long MaximumSourceBytes = 256L * 1024 * 1024;
    internal const int MaximumImageAssetNameLength = 80;

    internal const string PngExtension = ".png";
    internal const string JpegExtension = ".jpg";

    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    /// <summary>Extensions offered by the import dialog filter.</summary>
    internal static IReadOnlyList<string> Extensions => SupportedExtensions;

    internal static bool IsSupportedExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        foreach (var supported in SupportedExtensions)
        {
            if (string.Equals(supported, extension, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string ExtensionFor(ImageCompression compression) =>
        compression == ImageCompression.Jpeg ? JpegExtension : PngExtension;

    /// <summary>
    /// The managed encoder for a compression mode, or null when the source bytes are
    /// copied verbatim (<see cref="ImageCompression.Raw"/>).
    /// </summary>
    internal static ImageFormat? ManagedFormat(ImageCompression compression) => compression switch
    {
        ImageCompression.Jpeg => ImageFormat.Jpeg,
        ImageCompression.LosslessPng => ImageFormat.Png,
        _ => null
    };

    internal static string BuildDialogFilter()
    {
        var patterns = string.Join(";", SupportedExtensions.Select(extension => $"*{extension}"));
        return $"Image files ({patterns})|{patterns}|PNG (*.png)|*.png|All files (*.*)|*.*";
    }

    /// <summary>
    /// Rejects files whose declared size cannot be decoded inside the project budget
    /// before any decoder is asked to allocate the bitmap.
    /// </summary>
    internal static bool TryValidateDecodedBudget(int pixelWidth, int pixelHeight, out string error)
    {
        error = "";
        if (pixelWidth < 1 || pixelHeight < 1)
        {
            error = UiLocalization.T("The image has no positive pixel size.");
            return false;
        }
        if (pixelWidth > MaximumPixelDimension || pixelHeight > MaximumPixelDimension)
        {
            error = string.Format(UiLocalization.T("Image dimensions must not exceed {0} pixels per side."), MaximumPixelDimension);
            return false;
        }
        if ((long)pixelWidth * pixelHeight > MaximumDecodedPixels)
        {
            error = string.Format(UiLocalization.T("Image pixel count must not exceed {0} pixels."), MaximumDecodedPixels);
            return false;
        }
        return true;
    }
}
