namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void ImportFlashProjectFromDialog()
    {
        if (!CommitTextEdit()) return;
        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Import Flash Project"),
            Filter = "Flash / Animate (*.fla;*.xfl;DOMDocument.xml)|*.fla;*.xfl;DOMDocument.xml|Flash visual bridge (manifest.json)|manifest.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        FlashDocumentImportResult? imported = null;
        string? destinationDirectory = null;
        var failureStage = "Checking the Flash source";
        Cursor = Cursors.WaitCursor;
        try
        {
            if (FlashLegacyConversionBridge.IsLegacyBinaryFla(dialog.FileName))
            {
                Cursor = Cursors.Default;
                failureStage = "Preparing the Adobe conversion bridge";
                PrepareLegacyFlashBridge(dialog.FileName);
                return;
            }
            using var destination = new FolderBrowserDialog
            {
                Description = UiLocalization.T("Choose or create an empty folder for the converted V2D project."),
                UseDescriptionForTitle = true, ShowNewFolderButton = true,
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            Cursor = Cursors.Default;
            if (destination.ShowDialog(this) != DialogResult.OK) return;
            failureStage = "Validating the destination folder";
            destinationDirectory = ValidateFlashProjectDirectory(destination.SelectedPath);

            // Parse the complete source before asking to replace or touching the live document.
            Cursor = Cursors.Default;
            Exception? importError = null;
            failureStage = "Reading and converting the Flash document";
            using (var progress = new FlashImportProgressDialog())
            {
                var sourcePath = Path.GetFullPath(dialog.FileName);
                progress.Shown += async (_, _) =>
                {
                    try { imported = await Task.Run(() => LoadFlashImportSource(sourcePath)); }
                    catch (Exception exception) { importError = exception; }
                    finally { progress.Complete(); }
                };
                progress.ShowDialog(this);
                if (progress.CancellationRequested) return;
            }
            if (importError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(importError).Throw();
            if (imported is null) throw new InvalidDataException("The Flash import did not produce a project.");
            if (imported.Diagnostics.Count > 0)
            {
                var groups = imported.Diagnostics.GroupBy(item => item.Code).ToArray();
                var details = string.Join("\r\n\r\n", groups.Select(group =>
                {
                    var item = group.First();
                    return $"{item.Code} (×{group.Count()}): {item.Location}\r\n{item.Message}";
                }));
                if (ModernMessageDialog.Show(this,
                    UiLocalization.T("Review the Flash conversion report before replacing the current project.")
                    + "\r\n\r\n" + details,
                    UiLocalization.T("Flash Import"), MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
            }
            if (!TryContinueAfterUnsavedChanges()) return;
            failureStage = "Saving the converted V2D project";
            string? manifestPath = null;
            Exception? saveError = null;
            using (var saving = new FlashImportProgressDialog())
            {
                saving.BeginSaving();
                saving.Shown += async (_, _) =>
                {
                    try { manifestPath = await Task.Run(() => SaveImportedFlashProject(imported, destinationDirectory)); }
                    catch (Exception exception) { saveError = exception; }
                    finally { saving.Complete(); }
                };
                saving.ShowDialog(this);
            }
            if (saveError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(saveError).Throw();
            if (manifestPath is null) throw new IOException("The converted project was not saved.");

            StopPlayback();
            FinishPointerInteractionForFrameChange();
            failureStage = "Opening the converted V2D project";
            LoadProjectDocument(ProjectVaultStore.Load(manifestPath), manifestPath);
            _workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            ApplyProjectPlaybackSettingsToCurrentContext();
            SetFrame(0);
            var aspect = _stage.Width / (float)Math.Max(1, _stage.Height);
            var visibleWidth = VectorUnits.FromPixels(Math.Max(imported.Width, imported.Height * aspect)) * 1.04f;
            _stage.SetVisibleWorldWidth(visibleWidth);
            var referenceScale = Math.Clamp(_stage.Width / (visibleWidth * 0.035f), 0.25f, 8f);
            var sourceAtStageOrigin = !Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase);
            _stage.RestoreViewState(_stage.CaptureViewState() with
            {
                ReferenceZoomScale = referenceScale,
                ReferenceTargetX = sourceAtStageOrigin ? VectorUnits.FromPixels(imported.Width / 2) : 0,
                ReferenceTargetY = (sourceAtStageOrigin ? -VectorUnits.FromPixels(imported.Height / 2) : 0)
                    - _stage.Height * 0.08f / (referenceScale * 0.035f),
                ReferenceTargetZ = 0
            });
            SetProjectDirty(false);
            AppLog.Info($"Converted Flash project: {dialog.FileName}; destination={manifestPath}; diagnostics={imported.Diagnostics.Count}");
        }
        catch (Exception exception)
        {
            AppLog.Error($"Unable to import Flash project: {dialog.FileName}", exception);
            using var failure = new FlashImportErrorDialog(exception, dialog.FileName, destinationDirectory, failureStage);
            Cursor = Cursors.Default;
            failure.ShowDialog(this);
        }
        finally
        {
            FlashDocumentImporter.DeleteTemporaryImages(imported?.TemporaryFiles);
            Cursor = Cursors.Default;
        }
    }

    internal static string ValidateFlashProjectDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException(UiLocalization.T("Choose an empty folder for the new V2D project."));
        directory = Path.GetFullPath(directory);
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The V2D project folder cannot cross a reparse point.");
        if (File.Exists(directory) || (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
            throw new InvalidDataException(UiLocalization.T("Choose an empty folder for the new V2D project."));
        return directory;
    }

    internal static string SaveImportedFlashProject(FlashDocumentImportResult imported, string directory)
    {
        // Recheck after parsing and dialogs: never adopt an existing managed project.
        directory = ValidateFlashProjectDirectory(directory);
        return ProjectVaultStore.Save(imported.Project,
            Path.Combine(directory, SafeProjectFileName(imported.Name).TrimEnd('.', ' ') + ProjectVaultStore.ProjectExtension));
    }

    private static FlashDocumentImportResult LoadFlashImportSource(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        if (Path.GetFileName(sourcePath).Equals("converted.fla", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directory, "conversion-status.txt")))
        {
            var plan = new FlashLegacyConversionPlan(Path.Combine(directory, "legacy-source.fla"),
                Path.Combine(directory, "convert.jsfl"), sourcePath, Path.Combine(directory, "conversion-status.txt"));
            if (!FlashLegacyConversionBridge.TryGetCompletedPath(plan, out _, out var error))
                throw new InvalidDataException(error);
        }
        if (!Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return FlashDocumentImporter.Load(sourcePath, editableGeometry: true);
        if (File.Exists(Path.Combine(directory, "visual-conversion-status.txt")))
        {
            var plan = new FlashVisualConversionPlan(Path.Combine(directory, "source-copy.fla"),
                Path.Combine(directory, "export-frames.jsfl"), sourcePath, Path.Combine(directory, "visual-conversion-status.txt"));
            if (!FlashVisualConversionBridge.TryGetCompletedPath(plan, out _, out var error))
                throw new InvalidDataException(error);
        }
        var bundle = AnimationBundleImporter.Load(sourcePath);
        return new(bundle.Project, bundle.Name, bundle.Fps, bundle.FrameCount,
            bundle.Width / VectorUnits.UnitsPerPixel, bundle.Height / VectorUnits.UnitsPerPixel,
            [new FlashImportDiagnostic("visual-animation-baked", bundle.Name,
                UiLocalization.T("Visual frames are imported as whole SVG objects. Original library structure, editable tweens, audio, and scripts are not retained."))]);
    }

    private void PrepareFlashVisualBridgeFromDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Create Flash Visual Bridge"),
            Filter = "Flash / Animate (*.fla)|*.fla", CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (ModernMessageDialog.Show(this,
            UiLocalization.T("This bridge requires Adobe Animate 2020 or newer. It exports static SVG frames to preserve visual animation; library structure, editable tweens, audio and scripts are not transferred. Adobe may alter deprecated features. Review the exported frames before using the V2D project."),
            UiLocalization.T("Flash Import"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        using var folder = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Choose an empty folder for the Flash conversion bridge."),
            UseDescriptionForTitle = true, ShowNewFolderButton = true
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            var plan = FlashVisualConversionBridge.Prepare(Path.GetFullPath(dialog.FileName), folder.SelectedPath);
            ModernMessageDialog.Show(this,
                UiLocalization.T("In Adobe Animate, run the JSFL script using Commands > Run Command. After success, import manifest.json here and save as a V2D project.")
                + $"\r\n\r\n{plan.ScriptPath}\r\n\r\n{plan.ExpectedManifestPath}",
                UiLocalization.T("Flash Import"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Unable to prepare Flash visual bridge: {dialog.FileName}", exception);
            using var failure = new FlashImportErrorDialog(exception, dialog.FileName, folder.SelectedPath, "Preparing the Adobe conversion bridge");
            Cursor = Cursors.Default;
            failure.ShowDialog(this);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void PrepareLegacyFlashBridge(string sourcePath)
    {
        if (ModernMessageDialog.Show(this,
            UiLocalization.T("Flash 8 / CS3 / CS4 binary FLA requires an installed Flash CS5 or newer / Animate. Create a conversion bridge, run its JSFL script in Adobe, then import converted.fla here. The original file and current project are preserved."),
            UiLocalization.T("Flash Import"), MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information) != DialogResult.OK) return;
        using var folder = new FolderBrowserDialog
        {
            Description = UiLocalization.T("Choose an empty folder for the Flash conversion bridge."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        var plan = FlashLegacyConversionBridge.Prepare(Path.GetFullPath(sourcePath), folder.SelectedPath);
        ModernMessageDialog.Show(this,
            UiLocalization.T("In Flash / Animate, choose Commands > Run Command and run the script below. After a successful conversion, use Import Flash Project to open the converted FLA, then Save Project As to create the V2D project.")
            + $"\r\n\r\n{plan.ScriptPath}\r\n\r\n{plan.ExpectedFlaPath}",
            UiLocalization.T("Flash Import"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
