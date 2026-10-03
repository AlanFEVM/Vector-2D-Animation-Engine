namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void AddExternalSvgAssetLink()
    {
        using var dialog = CreateExternalSvgAssetDialog("Add SVG Link...");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryValidateExternalSvgAssetFile(dialog.FileName, out var path, out var sha256, out var error))
        {
            ShowExternalSvgAssetError("The SVG link could not be added.", error);
            return;
        }

        if (!_project.TryAddExternalSvgAsset(
                ImportedSvgDisplayName(path),
                path,
                ProjectRelativeExternalSvgPath(path),
                sha256,
                out var asset)
            || asset is null)
        {
            ShowExternalSvgAssetError("The SVG link could not be added.", "The link metadata is invalid or the library is full.");
            return;
        }

        _libraryVaultPanel.RefreshProjectObjects();
        AppLog.Info($"Added external SVG asset link: {asset.Name} -> {asset.SourcePath}");
    }

    /// <summary>
    /// Adds SVG links from files dropped onto the asset library. Each file is validated
    /// before it is registered, so one unreadable SVG does not abort the rest, and files
    /// already linked by the same source path are skipped instead of duplicated. Failures
    /// are reported once at the end rather than prompting per file.
    /// </summary>
    private void AddExternalSvgAssetLinksFromFiles(IReadOnlyList<string> fileNames)
    {
        var added = 0;
        var skipped = 0;
        var failure = "";
        foreach (var fileName in fileNames)
        {
            string path;
            try
            {
                path = Path.GetFullPath(fileName);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failure = exception.Message;
                continue;
            }

            if (_project.ExternalSvgAssets.Any(asset =>
                    string.Equals(asset.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            if (!TryValidateExternalSvgAssetFile(path, out var validated, out var sha256, out var error))
            {
                failure = error;
                continue;
            }

            if (_project.TryAddExternalSvgAsset(
                    ImportedSvgDisplayName(validated),
                    validated,
                    ProjectRelativeExternalSvgPath(validated),
                    sha256,
                    out var asset)
                && asset is not null)
            {
                added++;
                AppLog.Info($"Added external SVG asset link: {asset.Name} -> {asset.SourcePath}");
            }
        }

        if (added > 0) _libraryVaultPanel.RefreshProjectObjects();
        if (failure.Length == 0) return;
        AppLog.Warn($"Skipped {fileNames.Count - added - skipped} SVG link(s) during a library drop: {failure}");
        // A drop can carry several files; report once and only when there is a host window,
        // so a background or non-interactive caller is never blocked by a modal dialog.
        if (IsHandleCreated && Visible) ShowExternalSvgAssetError("The SVG link could not be added.", failure);
    }

    private void UseExternalSvgAssetLink(string assetId, PointF? center = null)
    {
        var asset = FindExternalSvgAsset(assetId);
        var path = asset is null ? null : ResolveExternalSvgAssetPath(asset);
        if (asset is null || path is null)
        {
            ShowExternalSvgAssetError("The SVG link is missing.", asset?.SourcePath ?? assetId);
            return;
        }
        if (!CanImportSvg())
        {
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("Open a Basic Drawing or scene mask drawing layer before using an SVG link."),
                UiLocalization.T("External SVG"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ImportSvgFile(path, center);
    }

    private void RelocateExternalSvgAssetLink(string assetId)
    {
        var asset = FindExternalSvgAsset(assetId);
        if (asset is null) return;

        using var dialog = CreateExternalSvgAssetDialog("Relocate SVG Link...");
        var currentDirectory = Path.GetDirectoryName(ResolveExternalSvgAssetPath(asset) ?? asset.SourcePath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryValidateExternalSvgAssetFile(dialog.FileName, out var path, out var sha256, out var error))
        {
            ShowExternalSvgAssetError("The SVG link could not be relocated.", error);
            return;
        }
        if (!_project.TryRelocateExternalSvgAsset(
                asset.Id,
                path,
                ProjectRelativeExternalSvgPath(path),
                sha256))
        {
            ShowExternalSvgAssetError("The SVG link could not be relocated.", "The selected path is invalid.");
            return;
        }

        _libraryVaultPanel.RefreshProjectObjects();
        AppLog.Info($"Relocated external SVG asset link: {asset.Name} -> {path}");
    }

    private void DeleteExternalSvgAssetLink(string assetId)
    {
        var asset = FindExternalSvgAsset(assetId);
        if (asset is null) return;
        if (ModernMessageDialog.Show(
                this,
                string.Format(UiLocalization.T("Remove SVG link \"{0}\" from the library?"), asset.Name),
                UiLocalization.T("External SVG"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes
            || !_project.TryRemoveExternalSvgAsset(asset.Id, out _))
        {
            return;
        }

        _libraryVaultPanel.RefreshProjectObjects();
        AppLog.Info($"Removed external SVG asset link: {asset.Name}");
    }

    private ExternalSvgAssetDefinition? FindExternalSvgAsset(string assetId) =>
        _project.ExternalSvgAssets.FirstOrDefault(asset =>
            string.Equals(asset.Id, assetId, StringComparison.Ordinal));

    private string? ResolveExternalSvgAssetPath(ExternalSvgAssetDefinition asset)
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
                    && string.Equals(Path.GetExtension(candidate), ".svg", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AppLog.Error($"Invalid project-relative SVG asset path: {asset.ProjectRelativePath}", exception);
            }
        }

        return File.Exists(asset.SourcePath) ? asset.SourcePath : null;
    }

    private string ProjectRelativeExternalSvgPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(_projectManifestPath)) return "";
        try
        {
            var projectRoot = Path.GetDirectoryName(Path.GetFullPath(_projectManifestPath))!;
            var relative = Path.GetRelativePath(projectRoot, fullPath);
            return VectorProject.TryNormalizeExternalSvgRelativePath(relative, out var normalized)
                ? normalized
                : "";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = exception;
            return "";
        }
    }

    private bool TryResolveDroppedExternalSvgAsset(
        IDataObject? data,
        out ExternalSvgAssetDefinition asset,
        out string path)
    {
        asset = null!;
        path = "";
        if (data?.GetData(typeof(ExternalSvgAssetDragData)) is not ExternalSvgAssetDragData reference
            || !string.Equals(reference.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            return false;
        }

        asset = FindExternalSvgAsset(reference.AssetId)!;
        var resolved = asset is null ? null : ResolveExternalSvgAssetPath(asset);
        if (resolved is null) return false;
        path = resolved;
        return true;
    }

    /// <summary>Preview scene for a Vault external-SVG link hovered over the stage.</summary>
    private VectorScene? BuildExternalSvgDropPreviewScene(string path) =>
        BuildSvgFileDropPreviewScene(path);

    /// <summary>
    /// Preview scene for a dragged <c>.svg</c> file. The source is parsed but nothing is
    /// written to the project; the drop performs the real import.
    /// </summary>
    private VectorScene? BuildSvgFileDropPreviewScene(string path)
    {
        try
        {
            var imported = ImportedSvgRasterizer.Load(path);
            var viewport = _stage.VisibleWorldBounds();
            var size = ImportedSvgInitialSize(imported.IntrinsicSize, viewport);
            var data = new ImportedSvgObjectPreview(imported.Source, size);
            var preview = new VectorScene();
            preview.CreateEmpty(1, 1);
            preview.AddImportedSvgObject(
                0,
                PointF.Empty,
                data.Size,
                data.Source,
                ImportedSvgDisplayName(path));
            return preview.ObjectCount > 0 ? preview : null;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Unable to build SVG drop preview: {path}", exception);
            return null;
        }
    }

    private readonly record struct ImportedSvgObjectPreview(string Source, SizeF Size);

    private static OpenFileDialog CreateExternalSvgAssetDialog(string title) => new()
    {
        Title = UiLocalization.T(title),
        Filter = $"{UiLocalization.T("SVG files")} (*.svg)|*.svg|{UiLocalization.T("All files")} (*.*)|*.*",
        CheckFileExists = true,
        Multiselect = false
    };

    private static bool TryValidateExternalSvgAssetFile(
        string fileName,
        out string fullPath,
        out string sha256,
        out string error)
    {
        fullPath = "";
        sha256 = "";
        error = "";
        try
        {
            fullPath = Path.GetFullPath(fileName);
            var imported = ImportedSvgRasterizer.Load(fullPath);
            var validationScale = Math.Min(
                1f,
                512f / Math.Max(imported.IntrinsicSize.Width, imported.IntrinsicSize.Height));
            _ = ImportedSvgRasterizer.Rasterize(
                imported.Source,
                Math.Max(1, imported.IntrinsicSize.Width * validationScale),
                Math.Max(1, imported.IntrinsicSize.Height * validationScale));
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(imported.Source)))
                .ToLowerInvariant();
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void ShowExternalSvgAssetError(string message, string detail)
    {
        ModernMessageDialog.Show(
            this,
            $"{UiLocalization.T(message)}\r\n\r\n{detail}",
            UiLocalization.T("External SVG"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
