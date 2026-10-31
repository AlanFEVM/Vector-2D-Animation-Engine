namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int MaximumClipboardResourceCount = 32;
    private static readonly string[] ClipboardImageFormats =
    [
        "PNG", "image/png", "image/x-png", "JPEG", "JFIF", "JPG", "image/jpeg",
        "GIF", "image/gif", "TIFF", DataFormats.Tiff, "image/tiff", "image/x-tiff",
        "BMP", "image/bmp", "image/x-ms-bmp"
    ];

    internal static Bitmap? ReadClipboardImage(IDataObject? clipboard)
    {
        if (clipboard is null) return null;
        Exception? failure = null;
        foreach (var format in ClipboardImageFormats)
        {
            try
            {
                if (!clipboard.GetDataPresent(format, autoConvert: false)) continue;
                var payload = clipboard.GetData(format, autoConvert: false);
                if (payload is Image image) return CloneClipboardImage(image);
                if (payload is byte[] bytes)
                {
                    using var source = new MemoryStream(bytes, writable: false);
                    return DecodeClipboardImage(source);
                }
                if (payload is Stream stream) return DecodeClipboardImage(stream);
                throw new InvalidDataException(UiLocalization.T("The clipboard image format is unsupported or invalid."));
            }
            catch (Exception exception) when (IsClipboardImageReadFailure(exception))
            {
                failure ??= exception;
            }
        }
        try
        {
            if (clipboard.GetData(DataFormats.Bitmap) is Image bitmap) return CloneClipboardImage(bitmap);
        }
        catch (Exception exception) when (IsClipboardImageReadFailure(exception))
        {
            failure ??= exception;
        }
        if (failure is not null)
            throw new InvalidDataException(failure.Message, failure);
        return null;
    }

    private static bool IsClipboardImageReadFailure(Exception exception) => exception is
        ArgumentException or IOException or InvalidDataException or NotSupportedException
        or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException
        or System.Runtime.InteropServices.ExternalException;

    private static Bitmap DecodeClipboardImage(Stream source)
    {
        var position = source.CanSeek ? source.Position : 0;
        try
        {
            if (source.CanSeek)
            {
                if (source.Length < 1 || source.Length > BitmapImageFormats.MaximumSourceBytes)
                    throw new InvalidDataException(UiLocalization.T("The image is missing, empty, or exceeds the image file size limit."));
                source.Position = 0;
            }
            using var encoded = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (encoded.Length > BitmapImageFormats.MaximumSourceBytes - count)
                    throw new InvalidDataException(UiLocalization.T("The image is missing, empty, or exceeds the image file size limit."));
                encoded.Write(buffer, 0, count);
            }
            if (encoded.Length == 0)
                throw new InvalidDataException(UiLocalization.T("The image has no positive pixel size."));
            encoded.Position = 0;
            using var decoded = Image.FromStream(encoded, useEmbeddedColorManagement: true, validateImageData: false);
            var format = decoded.RawFormat.Guid;
            if (format != System.Drawing.Imaging.ImageFormat.Png.Guid
                && format != System.Drawing.Imaging.ImageFormat.Jpeg.Guid
                && format != System.Drawing.Imaging.ImageFormat.Gif.Guid
                && format != System.Drawing.Imaging.ImageFormat.Tiff.Guid
                && format != System.Drawing.Imaging.ImageFormat.Bmp.Guid)
                throw new InvalidDataException(UiLocalization.T("The clipboard image format is unsupported or invalid."));
            return CloneClipboardImage(decoded);
        }
        finally
        {
            if (source.CanSeek) source.Position = position;
        }
    }

    private bool TryPasteClipboardFiles(IDataObject? clipboard)
    {
        if (clipboard?.GetDataPresent(DataFormats.FileDrop, autoConvert: false) != true) return false;
        if (clipboard.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] files || files.Length == 0)
            throw new InvalidDataException(UiLocalization.T("The image could not be imported."));
        if (files.Length > MaximumClipboardResourceCount)
            throw new InvalidDataException(string.Format(UiLocalization.T("Paste up to {0} resource files at a time."), MaximumClipboardResourceCount));
        var paths = files.Select(path => Path.GetFullPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long sourceBytes = 0;
        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path);
            var svg = string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase);
            if (!svg && !BitmapImageFormats.IsSupportedExtension(extension))
                throw new InvalidDataException($"{UiLocalization.T("The image could not be imported.")} {Path.GetFileName(path)}\r\n{string.Join(", ", BitmapImageFormats.Extensions)}, .svg");
            ProjectTemporaryResources.RejectReparsePath(path);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException(UiLocalization.T("The image is missing."), path);
            var limit = svg ? ImportedSvgRasterizer.MaxImportSourceBytes : BitmapImageFormats.MaximumSourceBytes;
            if (file.Length < 1 || file.Length > limit)
                throw new InvalidDataException($"{Path.GetFileName(path)}: {UiLocalization.T("The image is missing, empty, or exceeds the image file size limit.")}");
            sourceBytes += file.Length;
            if (sourceBytes > BitmapImageFormats.MaximumSourceBytes)
                throw new InvalidDataException(UiLocalization.T("Clipboard resources exceed the supported size limit."));
        }
        if (!EnsureImagePlacementAllowed()) return true;
        foreach (var path in paths)
        {
            if (!CanPlaceImage()) break;
            if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                ImportSvgFile(path, center: null);
                continue;
            }
            if (!PasteClipboardImageFile(path)) break;
        }
        return true;
    }

    private bool PasteClipboardImageFile(string path)
    {
        var prepared = PrepareImageImport(path);
        var existing = _project.ImageAssets.FirstOrDefault(candidate =>
            string.Equals(candidate.Sha256, prepared.Sha256, StringComparison.OrdinalIgnoreCase)
            && ResolveImageAssetPath(candidate) is not null);
        if (existing is not null) return PlaceImageAsset(existing.Id);
        var asset = RegisterImageAsset(prepared.Probe, BitmapImageImportSettings.Default,
            prepared.Sha256, deferRefresh: true);
        if (asset is null) return false;
        var placed = false;
        try
        {
            placed = PlaceImageAsset(asset.Id);
            return placed;
        }
        finally
        {
            if (!placed) _project.TryRemoveImageAsset(asset.Id, out _);
            _libraryVaultPanel.RefreshProjectObjects();
        }
    }

    private static void DeleteClipboardStagingFile(string path)
    {
        try
        {
            ProjectTemporaryResources.RejectReparsePath(path);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn($"Unable to clean up clipboard staging file: {exception.Message}");
        }
    }
}
