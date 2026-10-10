namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly ImageInspectorPanel _imageInspector = new() { Visible = false };
    private uint? _internalClipboardSequence;
    private readonly Dictionary<string, string> _imageContentHashes = new(StringComparer.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private bool TryPasteClipboardImage()
    {
        // Preserve application object/Cel copies until the system clipboard changes.
        if (_internalClipboardSequence == GetClipboardSequenceNumber()) return false;
        try
        {
            return PasteClipboardImage(Clipboard.GetDataObject());
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException
            or ArgumentException or System.IO.IOException or InvalidDataException or OutOfMemoryException)
        {
            ShowImageAssetError("The image could not be pasted.", exception.Message);
            return true;
        }
    }

    private bool PasteClipboardImage(IDataObject? clipboard)
    {
        using var image = ReadClipboardImage(clipboard);
        if (image is null) return false;
        if (!EnsureImagePlacementAllowed()) return true;
        if (!BitmapImageFormats.TryValidateDecodedBudget(image.Width, image.Height, out var error))
        {
            ShowImageAssetError("The image could not be pasted.", error);
            return true;
        }

        var contentHash = ClipboardImageContentHash(image);
        if (_imageContentHashes.Count >= VectorProject.MaxImageAssetCount) _imageContentHashes.Clear();
        foreach (var candidate in _project.ImageAssets)
        {
            if (candidate.PixelWidth != image.Width || candidate.PixelHeight != image.Height) continue;
            var path = ResolveImageAssetPath(candidate);
            if (path is null) continue;
            try
            {
                // Key the cache by actual file content, so replacing/relinking an asset
                // cannot reuse a stale identity. Compare source pixels before import settings.
                var fileHash = BitmapImageRasterizer.ComputeSha256(path);
                if (!_imageContentHashes.TryGetValue(fileHash, out var candidateHash))
                {
                    using var decoded = Image.FromFile(path);
                    using var bitmap = CloneClipboardImage(decoded);
                    candidateHash = ClipboardImageContentHash(bitmap);
                    _imageContentHashes[fileHash] = candidateHash;
                }
                if (candidateHash != contentHash) continue;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException
                or ArgumentException or IOException or InvalidDataException or OutOfMemoryException)
            {
                AppLog.Warn($"Unable to compare image asset for clipboard reuse: {candidate.Name}: {exception.Message}");
                continue;
            }
            PlaceImageAsset(candidate.Id);
            return true;
        }

        // Unsaved projects and restart handoff need a durable source until Save copies
        // these pixels into .Vault/Images. Do not use a disposable temporary file.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vector2DAnimationEngine", "ClipboardImages");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        ImageAssetDefinition? asset = null;
        var placed = false;
        try
        {
            image.Save(sourcePath, System.Drawing.Imaging.ImageFormat.Png);
            var settings = BitmapImageImportSettings.Default with { PixelsPerUnit = 96 };
            var sourceHash = BitmapImageRasterizer.ComputeSha256(sourcePath);
            if (!_project.TryAddImageAssetFromSource(UiLocalization.T("Clipboard Image"), sourcePath,
                    sourceHash, image.Width, image.Height, 96,
                    settings, out asset) || asset is null)
            {
                ShowImageAssetError("The image could not be pasted.",
                    UiLocalization.T("The image metadata is invalid or the library is full."));
                return true;
            }
            placed = PlaceImageAsset(asset.Id);
            if (placed)
            {
                _imageContentHashes[sourceHash] = contentHash;
                _libraryVaultPanel.RefreshProjectObjects();
            }
            return true;
        }
        finally
        {
            if (!placed)
            {
                if (asset is not null) _project.TryRemoveImageAsset(asset.Id, out _);
                File.Delete(sourcePath);
            }
        }
    }

    private static string ClipboardImageContentHash(Bitmap image)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(image.Width));
        hash.AppendData(BitConverter.GetBytes(image.Height));
        var pixels = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[checked(image.Width * 4)];
            for (var y = 0; y < image.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, row.Length);
                hash.AppendData(row);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { image.UnlockBits(pixels); }
    }

    internal static Bitmap? ReadClipboardImage(IDataObject? clipboard)
    {
        if (clipboard is null) return null;
        // PNG carries alpha that the Windows Bitmap conversion can discard.
        if (clipboard.GetDataPresent("PNG", autoConvert: false))
        {
            var payload = clipboard.GetData("PNG", autoConvert: false);
            if (payload is byte[] bytes)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var decoded = Image.FromStream(stream);
                return CloneClipboardImage(decoded);
            }
            if (payload is Stream source)
            {
                var position = source.CanSeek ? source.Position : 0;
                try
                {
                    if (source.CanSeek) source.Position = 0;
                    using var decoded = Image.FromStream(source);
                    return CloneClipboardImage(decoded);
                }
                finally { if (source.CanSeek) source.Position = position; }
            }
        }
        return clipboard.GetData(DataFormats.Bitmap) is Image bitmap ? CloneClipboardImage(bitmap) : null;
    }

    private static Bitmap CloneClipboardImage(Image image)
    {
        if (!BitmapImageFormats.TryValidateDecodedBudget(image.Width, image.Height, out var error))
            throw new InvalidDataException(error);
        return new Bitmap(image);
    }

    private void UpdateImageInspector(int[] selection)
    {
        var asset = selection.Length == 1 && _scene.TryGetBitmapObjectData(selection[0], out var bitmap)
            ? FindImageAsset(bitmap.ImageAssetId) : null;
        _imageInspector.Visible = asset is not null;
        if (asset is null)
        {
            _imageInspector.SetState(null);
            return;
        }
        var settings = asset.ImportSettings;
        string Row(string label, object value) => $"{UiLocalization.T(label)}: {value}";
        string Flag(bool value) => UiLocalization.T(value ? "Yes" : "No");
        var summary = string.Join(Environment.NewLine, new[]
        {
            asset.Name, asset.SourcePath, $"{asset.PixelWidth} × {asset.PixelHeight}",
            Row("Filter mode", UiLocalization.T(settings.FilterMode.ToString())),
            Row("Pixels per unit", settings.PixelsPerUnit),
            Row("Max size (pixels)", settings.MaxSize),
            Row("Non power of two", Flag(settings.NonPowerOfTwoScale)),
            Row("Compression", settings.Compression),
            Row("Compression quality", settings.CompressionQuality),
            Row("Alpha source", settings.AlphaSource),
            Row("Generate mipmaps", Flag(settings.Mipmaps)),
            Row("Read/Write enabled", Flag(settings.ReadWriteEnabled)),
            $"{UiLocalization.T("Pivot X")}: {settings.PivotX:0.###} / {UiLocalization.T("Pivot Y")}: {settings.PivotY:0.###}"
        });
        _imageInspector.SetState(new(asset.Id, summary));
    }

    private void ImportImageAsset()
    {
        if (!EnsureImagePlacementAllowed()) return;
        using var dialog = CreateImageAssetDialog("Import Image...");
        dialog.Multiselect = true;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportImageAssetsFromFiles(dialog.FileNames);
    }

    /// <summary>
    /// Imports image files dropped onto the asset library. The library shows the import
    /// settings dialog only for the first file and reuses that configuration for the rest,
    /// so dragging a folder of sprites is one gesture instead of one dialog per file.
    /// Files the project already references by the same source path are skipped.
    /// </summary>
    private void ImportImageAssetsFromFiles(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0) return;
        if (!EnsureImagePlacementAllowed()) return;
        using var progress = new ImageImportProgressDialog(fileNames.Count);
        ImageImportBatchResult? result = null;
        progress.Shown += async (_, _) =>
        {
            try
            {
                result = await ImportImageAssetsBatchAsync(fileNames,
                    (path, width, height, pixelsPerUnit) =>
                    {
                        using var settings = new ImageImportSettingsDialog(
                            UiLocalization.T("Image Import Settings"), Path.GetFileName(path),
                            width, height, pixelsPerUnit, BitmapImageImportSettings.Default);
                        return settings.ShowDialog(progress) == DialogResult.OK ? settings.Settings : null;
                    }, progress.Report, () => progress.CancellationRequested);
            }
            catch (Exception exception)
            {
                AppLog.Error("Unable to finish image batch import.", exception);
                ShowImageAssetError("The image could not be imported.", exception.Message);
            }
            finally
            {
                progress.Complete();
            }
        };
        progress.ShowDialog(this);
        if (result is null) return;
        var summary = string.Format(
            UiLocalization.T("Imported: {0}\nSkipped: {1}\nFailed: {2}\nNot processed: {3}"),
            result.Imported, result.Skipped, result.Failed,
            fileNames.Count - result.Imported - result.Skipped - result.Failed);
        if (result.Cancelled) summary = UiLocalization.T("Image import cancelled.") + "\n\n" + summary;
        if (result.Errors.Count > 0) summary += "\n\n" + string.Join("\n", result.Errors);
        ModernMessageDialog.Show(this, summary, UiLocalization.T("Images"), MessageBoxButtons.OK,
            result.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        AppLog.Info($"Image batch import: {result.Imported} imported, {result.Skipped} skipped, "
            + $"{result.Failed} failed, cancelled={result.Cancelled}.");
    }

    private sealed record ImageImportBatchResult(
        int Imported, int Skipped, int Failed, bool Cancelled, IReadOnlyList<string> Errors);

    private async Task<ImageImportBatchResult> ImportImageAssetsBatchAsync(
        IReadOnlyList<string> fileNames,
        Func<string, int, int, float, BitmapImageImportSettings?> chooseSettings,
        Action<int, string> report,
        Func<bool> cancellationRequested)
    {
        var imported = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;
        var errors = new List<string>();
        BitmapImageImportSettings? sharedSettings = null;
        var existingPaths = new HashSet<string>(_project.ImageAssets.Select(asset => asset.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        _libraryVaultPanel.BeginProjectObjectUpdate();
        try
        {
            for (var index = 0; index < fileNames.Count; index++)
            {
                if (cancellationRequested()) { cancelled = true; break; }
                var fileName = fileNames[index];
                report(index, fileName);
                try
                {
                    var fullPath = Path.GetFullPath(fileName);
                    if (existingPaths.Contains(fullPath))
                    {
                        skipped++;
                    }
                    else
                    {
                        var prepared = await Task.Run(() => PrepareImageImport(fullPath));
                        if (cancellationRequested()) { cancelled = true; break; }
                        if (sharedSettings is null)
                        {
                            var probe = prepared.Probe;
                            sharedSettings = chooseSettings(probe.FullPath, probe.PixelWidth,
                                probe.PixelHeight, probe.NaturalPixelsPerUnit);
                            if (sharedSettings is null) { cancelled = true; break; }
                        }
                        if (cancellationRequested()) { cancelled = true; break; }
                        if (!CanPlaceImage()) throw new InvalidOperationException(
                            UiLocalization.T("The image metadata is invalid or the library is full."));
                        var asset = RegisterImageAsset(prepared.Probe, sharedSettings.Value,
                            prepared.Sha256, deferRefresh: true, showError: detail =>
                                throw new InvalidDataException(detail));
                        if (asset is not null)
                        {
                            existingPaths.Add(fullPath);
                            imported++;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
                    or NotSupportedException or UnauthorizedAccessException
                    or System.Runtime.InteropServices.ExternalException or OutOfMemoryException
                    or InvalidOperationException)
                {
                    failed++;
                    var detail = $"{fileName}: {exception.Message}";
                    if (errors.Count < 5) errors.Add(detail);
                    AppLog.Warn($"Image batch import failed: {detail}");
                }
                report(index + 1, fileName);
                await Task.Yield();
            }
        }
        finally
        {
            _libraryVaultPanel.EndProjectObjectUpdate();
        }
        return new ImageImportBatchResult(imported, skipped, failed, cancelled, errors);
    }

    private static (ImageFileProbe Probe, string Sha256) PrepareImageImport(string fullPath)
    {
        if (!BitmapImageRasterizer.TryProbe(fullPath, out var width, out var height,
                out var pixelsPerUnit, out var error)) throw new InvalidDataException(error);
        return (new ImageFileProbe(fullPath, width, height, pixelsPerUnit),
            BitmapImageRasterizer.ComputeSha256(fullPath));
    }

    /// <summary>
    /// Imports a dragged image file and places it at the drop point. An already-imported
    /// file is reused instead of creating a duplicate library entry, so dragging the same
    /// image twice keeps one asset and simply adds another placement.
    /// </summary>
    private void PlaceDroppedImageFile(
        string fileName,
        ImageAssetDefinition? knownAsset,
        PointF center)
    {
        if (!EnsureImagePlacementAllowed()) return;
        var asset = knownAsset ?? ImportImageAssetFromPath(fileName, showSettingsDialog: true);
        if (asset is null) return;
        PlaceImageAsset(asset.Id, center);
    }

    private void ImportImageAssetFromPath(string fileName) =>
        _ = ImportImageAssetFromPath(fileName, showSettingsDialog: true);

    private ImageAssetDefinition? ImportImageAssetFromPath(string fileName, bool showSettingsDialog)
    {
        if (!CanPlaceImage()) return null;
        if (!TryProbeImageFile(fileName, out var probe)) return null;

        var settings = BitmapImageImportSettings.Default;
        if (showSettingsDialog)
        {
            // Import settings are chosen before the asset is registered, so a cancelled or
            // invalid configuration never leaves a half-created library entry behind.
            using var settingsDialog = new ImageImportSettingsDialog(
                UiLocalization.T("Image Import Settings"),
                Path.GetFileName(probe.FullPath),
                probe.PixelWidth,
                probe.PixelHeight,
                probe.NaturalPixelsPerUnit,
                BitmapImageImportSettings.Default);
            if (settingsDialog.ShowDialog(this) != DialogResult.OK) return null;
            settings = settingsDialog.Settings;
        }

        return RegisterImageAsset(probe, settings);
    }

    private readonly record struct ImageFileProbe(
        string FullPath,
        int PixelWidth,
        int PixelHeight,
        float NaturalPixelsPerUnit);

    private bool TryProbeImageFile(string fileName, out ImageFileProbe probe)
    {
        probe = default;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(fileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowImageAssetError("The image could not be imported.", exception.Message);
            return false;
        }

        if (!BitmapImageRasterizer.TryProbe(
                fullPath,
                out var pixelWidth,
                out var pixelHeight,
                out var naturalPixelsPerUnit,
                out var error))
        {
            ShowImageAssetError("The image could not be imported.", error);
            return false;
        }

        probe = new ImageFileProbe(fullPath, pixelWidth, pixelHeight, naturalPixelsPerUnit);
        return true;
    }

    private ImageAssetDefinition? RegisterImageAsset(
        ImageFileProbe probe,
        BitmapImageImportSettings settings,
        string? preparedSha256 = null,
        bool deferRefresh = false,
        Action<string>? showError = null)
    {
        showError ??= detail => ShowImageAssetError("The image could not be imported.", detail);
        var decodedSize = settings.ResolveStoredPixelSize(new SizeF(probe.PixelWidth, probe.PixelHeight));
        if (!BitmapImageFormats.TryValidateDecodedBudget(
                Math.Max(1, (int)decodedSize.Width),
                Math.Max(1, (int)decodedSize.Height),
                out var error))
        {
            showError(error);
            return null;
        }

        string sha256;
        try
        {
            sha256 = preparedSha256 ?? BitmapImageRasterizer.ComputeSha256(probe.FullPath);
        }
        catch (Exception exception)
        {
            showError(exception.Message);
            return null;
        }

        if (!_project.TryAddImageAssetFromSource(
                ImageAssetDisplayName(probe.FullPath),
                probe.FullPath,
                sha256,
                probe.PixelWidth,
                probe.PixelHeight,
                probe.NaturalPixelsPerUnit,
                settings,
                out var asset)
            || asset is null)
        {
            showError(UiLocalization.T("The image metadata is invalid or the library is full."));
            return null;
        }

        if (!deferRefresh) _libraryVaultPanel.RefreshProjectObjects();
        AppLog.Info(
            $"Imported image asset: {asset.Name} ({asset.PixelWidth}x{asset.PixelHeight}) from {probe.FullPath}");
        return asset;
    }

    private bool PlaceImageAsset(string assetId, PointF? center = null)
    {
        var asset = FindImageAsset(assetId);
        if (asset is null)
        {
            ShowImageAssetError("The image is missing.", assetId);
            return false;
        }
        if (!EnsureImagePlacementAllowed()) return false;

        var path = ResolveImageAssetPath(asset);
        if (path is null)
        {
            ShowImageAssetError("The image is missing.", asset.ProjectRelativePath);
            return false;
        }

        Cursor = Cursors.WaitCursor;
        VectorSceneSnapshot? snapshot = null;
        try
        {
            // Decode before mutating so an unreadable file cannot leave a bitmap object
            // pointing at pixels the renderers can never resolve.
            var raster = BitmapImageRasterizer.Decode(path, asset.ImportSettings);
            if (!CanPlaceImage()) return false;

            var viewport = _stage.VisibleWorldBounds();
            var placement = center ?? new PointF(
                viewport.Left + viewport.Width * 0.5f,
                viewport.Top + viewport.Height * 0.5f);
            var data = new BitmapObjectData
            {
                ImageAssetId = asset.Id,
                // The stored decode can be smaller than the source under MaxSize or the
                // power-of-two rule, so the quad follows the decoded aspect ratio.
                PlacedSize = ScalePlacedSizeForDecode(asset.ResolvePlacedSize(), asset, raster)
            };
            if (!data.IsValid)
            {
                ShowImageAssetError("The image could not be placed.", "The placement metadata is invalid.");
                return false;
            }

            snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
            var objectIndex = _scene.AddBitmapObject(_scene.ActiveLayer, placement, data);
            if (objectIndex < 0)
            {
                RestoreCanvasMutationSnapshot(snapshot);
                ShowImageAssetError("The image could not be placed.", "The active layer rejected the object.");
                return false;
            }

            PushUndoSnapshot(snapshot);
            SetSelection(objectIndex);
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            if (IsSceneMaskEditing()) RebuildDrawingObjectUnderlay();
            AppLog.Info($"Placed image asset: {asset.Name} at {placement.X:0.##},{placement.Y:0.##}");
            return true;
        }
        catch (Exception exception)
        {
            if (snapshot is not null) RestoreCanvasMutationSnapshot(snapshot);
            AppLog.Error($"Unable to place image: {asset.Name}", exception);
            ShowImageAssetError("The image could not be placed.", exception.Message);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Scales a placed quad to the decoded raster's aspect ratio. The stored decode can be
    /// smaller than the source under MaxSize or the power-of-two rule; without this the
    /// image would be stretched to the nominal size and lose its aspect ratio.
    /// </summary>
    private static SizeF ScalePlacedSizeForDecode(
        SizeF placedSize,
        ImageAssetDefinition asset,
        BitmapImageRaster raster)
    {
        if (asset.PixelWidth <= 0 || asset.PixelHeight <= 0) return placedSize;
        var scale = Math.Min(
            raster.PixelWidth / (float)asset.PixelWidth,
            raster.PixelHeight / (float)asset.PixelHeight);
        if (scale <= 0 || !float.IsFinite(scale) || scale >= 0.9999f) return placedSize;
        return new SizeF(
            Math.Max(1, VectorUnits.Quantize(placedSize.Width * scale)),
            Math.Max(1, VectorUnits.Quantize(placedSize.Height * scale)));
    }

    private void EditImageAssetImportSettings(string assetId)
    {
        var asset = FindImageAsset(assetId);
        if (asset is null) return;

        using var dialog = new ImageImportSettingsDialog(
            UiLocalization.T("Image Import Settings"),
            asset.Name,
            asset.PixelWidth,
            asset.PixelHeight,
            asset.NaturalPixelsPerUnit,
            asset.ImportSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var settings = dialog.Settings;

        var storedPixelSize = settings.ResolveStoredPixelSize(new SizeF(asset.PixelWidth, asset.PixelHeight));
        var storedWidth = Math.Max(1, (int)storedPixelSize.Width);
        var storedHeight = Math.Max(1, (int)storedPixelSize.Height);
        if (!BitmapImageFormats.TryValidateDecodedBudget(storedWidth, storedHeight, out var error))
        {
            ShowImageAssetError("The import settings could not be applied.", error);
            return;
        }

        // Capture the previous state so a placement failure can roll the asset back;
        // otherwise the library would advertise settings the canvas does not reflect.
        var previousSettings = asset.ImportSettings;
        var previousWidth = asset.PixelWidth;
        var previousHeight = asset.PixelHeight;
        if (!_project.TryApplyImageImportSettings(
                asset.Id,
                settings,
                storedWidth,
                storedHeight,
                asset.Sha256))
        {
            ShowImageAssetError(
                "The import settings could not be applied.",
                "The settings are invalid or the decoded size exceeds the supported budget.");
            return;
        }

        var refreshed = FindImageAsset(asset.Id)!;
        if (!ApplyImageImportSettingsToPlacements(refreshed))
        {
            _project.TryApplyImageImportSettings(
                asset.Id,
                previousSettings,
                previousWidth,
                previousHeight,
                asset.Sha256);
            BitmapImageRasterizer.ClearCache();
            _libraryVaultPanel.RefreshProjectObjects();
            ShowImageAssetError(
                "The import settings could not be applied.",
                "A placed instance could not be resized to the new settings.");
            return;
        }

        BitmapImageRasterizer.ClearCache();
        _libraryVaultPanel.RefreshProjectObjects();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        MarkProjectDirty();
        AppLog.Info(
            $"Applied image import settings: {asset.Name} ({settings.FilterMode}, {settings.PixelsPerUnit:0.##} PPU)");
    }

    /// <summary>
    /// Re-derives every placed quad for one image from the asset's current settings.
    /// Returns false when any scene rejects the update so the caller can roll back.
    /// </summary>
    private bool ApplyImageImportSettingsToPlacements(ImageAssetDefinition asset)
    {
        var placedSize = asset.ResolvePlacedSize();
        foreach (var scene in EnumerateProjectScenes())
        {
            foreach (var objectIndex in scene.FindBitmapObjectsUsingImageAsset(asset.Id))
            {
                if (!scene.TryGetBitmapObjectData(objectIndex, out var existing)) continue;
                if (existing.PlacedSize.Equals(placedSize)) continue;
                if (!scene.TryUpdateBitmapObject(objectIndex, existing.WithPlacedSize(placedSize)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void RelinkImageAsset(string assetId)
    {
        var asset = FindImageAsset(assetId);
        if (asset is null) return;

        using var dialog = CreateImageAssetDialog("Relink Image...");
        var currentPath = ResolveImageAssetPath(asset);
        var currentDirectory = currentPath is null ? null : Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(dialog.FileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowImageAssetError("The image could not be relinked.", exception.Message);
            return;
        }

        if (!BitmapImageRasterizer.TryProbe(fullPath, out var pixelWidth, out var pixelHeight, out _, out var error))
        {
            ShowImageAssetError("The image could not be relinked.", error);
            return;
        }

        string sha256;
        try
        {
            sha256 = BitmapImageRasterizer.ComputeSha256(fullPath);
        }
        catch (Exception exception)
        {
            ShowImageAssetError("The image could not be relinked.", exception.Message);
            return;
        }

        // Relink re-points the managed copy at a different file while keeping the asset id
        // and every existing placement intact.
        if (!_project.TryRelinkImageAsset(
                asset.Id,
                fullPath,
                VectorProject.ImageAssetManagedRelativePath(
                    asset.Id,
                    BitmapImageFormats.ExtensionFor(asset.ImportSettings.Compression)),
                sha256,
                pixelWidth,
                pixelHeight,
                asset.NaturalPixelsPerUnit))
        {
            ShowImageAssetError("The image could not be relinked.", "The selected file is not a valid image.");
            return;
        }

        BitmapImageRasterizer.ClearCache();
        _libraryVaultPanel.RefreshProjectObjects();
        _stage.Invalidate();
        MarkProjectDirty();
        AppLog.Info($"Relinked image asset: {asset.Name} -> {fullPath}");
    }

    private void DeleteImageAsset(string assetId)
    {
        var asset = FindImageAsset(assetId);
        if (asset is null) return;

        var placements = 0;
        var visited = new HashSet<VectorScene>(ReferenceEqualityComparer.Instance);
        foreach (var scene in EnumerateProjectScenes())
        {
            if (!visited.Add(scene)) continue;
            placements += scene.FindBitmapObjectsUsingImageAsset(assetId).Length;
        }
        var message = placements > 0
            ? string.Format(
                UiLocalization.T("Remove image \"{0}\" from the library and delete {1} placed instance(s)?"),
                asset.Name,
                placements)
            : string.Format(UiLocalization.T("Remove image \"{0}\" from the library?"), asset.Name);
        if (ModernMessageDialog.Show(
                this,
                message,
                UiLocalization.T("Images"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        // Placements must go first: a bitmap object whose asset is gone cannot be drawn or
        // saved, so the objects and the asset are removed as one user-visible action.
        RemoveBitmapPlacementsEverywhere(assetId);
        if (!_project.TryRemoveImageAsset(asset.Id, out _)) return;

        BitmapImageRasterizer.ClearCache();
        _libraryVaultPanel.RefreshProjectObjects();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        MarkProjectDirty();
        AppLog.Info($"Removed image asset: {asset.Name}");
    }

    /// <summary>
    /// Every scene a bitmap object can live in: each symbol's drawing scene plus each
    /// scene definition's own editable scene and its per-layer mask scenes. Bitmap
    /// placements are removed from all of them so delete cannot leave a dangling reference
    /// in a mask or in a symbol that is not currently open.
    /// </summary>
    private IEnumerable<VectorScene> EnumerateProjectScenes()
    {
        foreach (var drawingObject in _project.DrawingObjects) yield return drawingObject.Scene;
        foreach (var sceneDefinition in _project.Scenes)
        {
            yield return _sceneEditStage;
            foreach (var layer in sceneDefinition.Layers)
            {
                var maskScene = sceneDefinition.FindMaskScene(layer.Id);
                if (maskScene is not null) yield return maskScene;
            }
        }
    }

    private void RemoveBitmapPlacementsEverywhere(string assetId)
    {
        var visited = new HashSet<VectorScene>(ReferenceEqualityComparer.Instance);
        foreach (var scene in EnumerateProjectScenes())
        {
            if (!visited.Add(scene)) continue;
            // The predicate reports whether an asset still exists, so the deleted asset must
            // answer false; every other asset keeps its placements.
            scene.RemoveBitmapObjectsWithoutImageAsset(id =>
                !string.Equals(id, assetId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Image placement shares the SVG import rules: an editable drawing surface with a
    /// drawing-kind active layer. Placing onto a mask layer would silently produce a
    /// bitmap the mask path cannot clip with.
    /// </summary>
    /// <summary>
    /// Explains why an image cannot be placed right now. Placement depends on the active
    /// workspace, whether drawing tools are busy, the current selection and the layer kind,
    /// so each unmet requirement reports its own actionable message instead of failing
    /// silently from a bare guard clause.
    /// </summary>
    private string? ImagePlacementBlockedReason()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing && !IsSceneMaskEditing())
        {
            return UiLocalization.T("Open a Basic Drawing or scene mask drawing layer before placing an image.");
        }
        if (DrawingToolsBlocked())
        {
            return UiLocalization.T("Finish the current drawing operation before placing an image.");
        }
        if (!IsSceneMaskEditing() && ActiveDrawingObject() is null)
        {
            return UiLocalization.T("Select an object on the stage first.");
        }
        var layer = _scene.ActiveLayer;
        if ((uint)layer >= _scene.LayerCount || _scene.GetLayerKind(layer) != DrawingLayerKind.Drawing)
        {
            return UiLocalization.T("Open a Basic Drawing or scene mask drawing layer before placing an image.");
        }
        return null;
    }

    private bool CanPlaceImage() => ImagePlacementBlockedReason() is null;

    private bool EnsureImagePlacementAllowed()
    {
        if (ImagePlacementBlockedReason() is not { } reason) return true;
        ModernMessageDialog.Show(
            this,
            reason,
            UiLocalization.T("Images"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }

    private ImageAssetDefinition? FindImageAsset(string assetId) =>
        _project.ImageAssets.FirstOrDefault(asset =>
            string.Equals(asset.Id, assetId, StringComparison.Ordinal));

    /// <summary>
    /// Resolves an image asset id to decoded pixels for the stage renderers. The renderers
    /// only ever see the scene, so asset lookup, file resolution and decoding stay in the
    /// project layer. The rasterizer caches by content hash, so repeated frames are cheap.
    /// </summary>
    private BitmapImageRaster? ResolveBitmapImageRaster(string imageAssetId)
    {
        var asset = FindImageAsset(imageAssetId);
        if (asset is null) return null;
        var path = ResolveImageAssetPath(asset);
        if (path is null) return null;
        try
        {
            return BitmapImageRasterizer.Decode(path, asset.ImportSettings);
        }
        catch (Exception exception) when (exception is InvalidDataException or OutOfMemoryException or ArgumentException)
        {
            AppLog.Error($"Unable to decode image asset for rendering: {asset.Name}", exception);
            return null;
        }
    }

    /// <summary>
    /// Gives a stage the asset lookup it needs to paint bitmap objects. Without it the
    /// renderers resolve nothing and skip every bitmap without a sound, which reads as an
    /// empty canvas instead of a failure.
    /// </summary>
    private BitmapSampling ResolveBitmapImageSampling(string imageAssetId) =>
        FindImageAsset(imageAssetId)?.ImportSettings.FilterMode == ImageFilterMode.Point
            ? BitmapSampling.Point
            : BitmapSampling.Linear;

    private void BindStageBitmapImageResolvers(StageControl stage)
    {
        stage.BitmapImageResolver = ResolveBitmapImageRaster;
        stage.BitmapImageSamplingProvider = ResolveBitmapImageSampling;
    }

    /// <summary>
    /// Resolves the file to decode. The managed copy inside the project is authoritative
    /// once the project has been saved, matching the loader; the original import path is
    /// only a fallback for a project that has never been saved.
    /// </summary>
    private string? ResolveImageAssetPath(ImageAssetDefinition asset)
    {
        if (!string.IsNullOrWhiteSpace(_projectManifestPath)
            && !string.IsNullOrWhiteSpace(asset.ProjectRelativePath))
        {
            try
            {
                var projectRoot = Path.GetDirectoryName(Path.GetFullPath(_projectManifestPath))!;
                var candidate = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    asset.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                var rootPrefix = Path.TrimEndingDirectorySeparator(projectRoot) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AppLog.Error($"Invalid project-relative image path: {asset.ProjectRelativePath}", exception);
            }
        }

        return File.Exists(asset.SourcePath) ? asset.SourcePath : null;
    }

    private bool TryResolveDroppedImageAsset(
        IDataObject? data,
        out ImageAssetDefinition asset,
        out string path)
    {
        asset = null!;
        path = "";
        if (data?.GetData(typeof(ImageAssetDragData)) is not ImageAssetDragData reference
            || !string.Equals(reference.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var resolved = FindImageAsset(reference.AssetId);
        if (resolved is null) return false;
        var resolvedPath = ResolveImageAssetPath(resolved);
        if (resolvedPath is null) return false;
        asset = resolved;
        path = resolvedPath;
        return true;
    }

    /// <summary>
    /// Recognizes an image file dragged in from the shell. The file is not imported during
    /// hover: <paramref name="asset"/> stays null and the first import happens on drop, so a
    /// drag across the stage never creates library entries the user did not ask for.
    /// </summary>
    private bool TryResolveDroppedImageFile(
        IDataObject? data,
        out string fileName,
        out ImageAssetDefinition? asset)
    {
        fileName = "";
        asset = null;
        if (data?.GetDataPresent(DataFormats.FileDrop) != true
            || data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length != 1
            || string.IsNullOrWhiteSpace(files[0])
            || !BitmapImageFormats.IsSupportedExtension(Path.GetExtension(files[0]))
            || !File.Exists(files[0]))
        {
            return false;
        }

        fileName = files[0];
        var dropped = fileName;
        asset = _project.ImageAssets.FirstOrDefault(candidate =>
            string.Equals(candidate.SourcePath, dropped, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// Builds the throwaway scene the stage shows while an image file hovers over it. The
    /// quad uses the same decode-aware size a real placement would take, so the preview
    /// aspect ratio matches the committed object.
    /// </summary>
    private VectorScene? BuildImageDropPreviewScene(ImageAssetDefinition asset, string path)
    {
        try
        {
            var raster = BitmapImageRasterizer.Decode(path, asset.ImportSettings);
            var data = new BitmapObjectData
            {
                ImageAssetId = asset.Id,
                PlacedSize = ScalePlacedSizeForDecode(asset.ResolvePlacedSize(), asset, raster)
            };
            if (!data.IsValid) return null;
            var preview = new VectorScene();
            preview.CreateEmpty(1, 1);
            preview.AddBitmapObject(0, PointF.Empty, data);
            return preview.ObjectCount > 0 ? preview : null;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Unable to build image drop preview: {asset.Name}", exception);
            return null;
        }
    }

    private static string ImageAssetDisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = "Image";
        return name.Length > VectorProject.MaxImageAssetNameLength
            ? name[..VectorProject.MaxImageAssetNameLength]
            : name;
    }

    private static OpenFileDialog CreateImageAssetDialog(string title) => new()
    {
        Title = UiLocalization.T(title),
        Filter = BitmapImageFormats.BuildDialogFilter(),
        CheckFileExists = true,
        Multiselect = false
    };

    private void ShowImageAssetError(string message, string detail)
    {
        ModernMessageDialog.Show(
            this,
            $"{UiLocalization.T(message)}\r\n\r\n{detail}",
            UiLocalization.T("Images"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
