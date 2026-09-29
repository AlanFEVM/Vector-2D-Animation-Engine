using System.Globalization;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    /// <summary>
    /// Exports one symbol to a standalone SVG file chosen by the user.
    ///
    /// The exported frame is the symbol's current stage frame, and only content the
    /// stage would visibly draw is written: hidden layers, hidden ancestor folders,
    /// folder layers and mask layers are excluded, while masks, layer blend modes and
    /// object distortions are carried into the SVG as far as the format allows.
    /// </summary>
    private void ExportDrawingObjectSvg(string drawingObjectId)
    {
        if (!CommitTextEdit()) return;
        var drawingObject = _drawingObjects.FirstOrDefault(item =>
            string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null) return;

        var frame = ResolveDrawingObjectExportFrame(drawingObject);
        using var dialog = new SaveFileDialog
        {
            Title = UiLocalization.T("Export Symbol as SVG"),
            Filter = $"{UiLocalization.T("SVG files")} (*.svg)|*.svg|{UiLocalization.T("All files")} (*.*)|*.*",
            DefaultExt = "svg",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SafeSvgFileName(drawingObject.Name) + ".svg",
            InitialDirectory = ExportInitialDirectory()
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            DrawingObjectSvgExport.Write(
                dialog.FileName,
                drawingObject.Id,
                drawingObject.Name,
                drawingObject.Scene,
                frame);
            AppLog.Info($"Exported symbol SVG: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to export symbol SVG: {dialog.FileName}", ex);
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("The symbol could not be exported as SVG."),
                UiLocalization.T("Export Symbol as SVG"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    /// <summary>
    /// Resolves the frame to export for a symbol. The symbol currently bound to the
    /// stage exports its live frame; any other symbol exports its first frame that
    /// has visible content, so the result is never an accidentally empty document.
    /// </summary>
    private int ResolveDrawingObjectExportFrame(DrawingObjectDefinition drawingObject)
    {
        var scene = drawingObject.Scene;
        var lastFrame = Math.Max(0, scene.FrameCount - 1);
        if (ReferenceEquals(scene, _scene) || ReferenceEquals(drawingObject, ActiveDrawingObject()))
        {
            return Math.Clamp(_frame, 0, lastFrame);
        }

        for (var frame = 0; frame <= lastFrame; frame++)
        {
            if (scene.GetActiveObjectIndices(frame).Length > 0) return frame;
        }

        return 0;
    }

    /// <summary>
    /// Default export directory: alongside the project when one is open, otherwise
    /// the user's documents folder.
    /// </summary>
    private string ExportInitialDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_projectManifestPath))
        {
            var directory = Path.GetDirectoryName(_projectManifestPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Builds a filesystem-safe default file name from a symbol name.
    /// </summary>
    private static string SafeSvgFileName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Symbol" : name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var candidate = builder.ToString().Trim().Trim('.');
        if (candidate.Length == 0) return "Symbol";
        return candidate.Length > 120 ? candidate[..120] : candidate;
    }

    /// <summary>
    /// Exports one symbol plus every symbol it nests to a re-importable
    /// <c>.V2DSymbol</c> file, carrying library folders and tag assignments.
    /// </summary>
    private void ExportDrawingObjectSymbolPackage(string drawingObjectId)
    {
        if (!CommitTextEdit()) return;
        var drawingObject = _drawingObjects.FirstOrDefault(item =>
            string.Equals(item.Id, drawingObjectId, StringComparison.Ordinal));
        if (drawingObject is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = UiLocalization.T("Export Symbol File"),
            Filter =
                $"{UiLocalization.T("Symbol files")} (*{DrawingObjectSymbolPackage.FileExtension})"
                + $"|*{DrawingObjectSymbolPackage.FileExtension}|{UiLocalization.T("All files")} (*.*)|*.*",
            DefaultExt = DrawingObjectSymbolPackage.FileExtension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SafeSvgFileName(drawingObject.Name) + DrawingObjectSymbolPackage.FileExtension,
            InitialDirectory = ExportInitialDirectory()
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            DrawingObjectSymbolPackageService.Export(_project, drawingObject.Id, dialog.FileName);
            AppLog.Info($"Exported symbol file: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to export symbol file: {dialog.FileName}", ex);
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("The symbol could not be exported."),
                UiLocalization.T("Export Symbol File"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    /// <summary>
    /// Imports a <c>.V2DSymbol</c> file into the current project. Imported symbols
    /// always receive new identifiers, so importing the same file twice yields two
    /// independent copies.
    /// </summary>
    private void ImportDrawingObjectSymbolPackage()
    {
        if (!CommitTextEdit()) return;

        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Import Symbol File"),
            Filter =
                $"{UiLocalization.T("Symbol files")} (*{DrawingObjectSymbolPackage.FileExtension})"
                + $"|*{DrawingObjectSymbolPackage.FileExtension}|{UiLocalization.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExportInitialDirectory()
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ImportDrawingObjectSymbolPackages([dialog.FileName]);
    }

    private void ImportDrawingObjectSymbolPackages(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0 || !CommitTextEdit()) return;
        foreach (var fileName in fileNames) ImportDrawingObjectSymbolPackage(fileName);
    }

    private void ImportDrawingObjectSymbolPackage(string fileName)
    {
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            var package = DrawingObjectSymbolPackage.Read(fileName);
            var imported = DrawingObjectSymbolPackageService.Import(_project, package);
            RefreshDrawingObjectAssetPresentation();
            var index = -1;
            for (var i = 0; i < _drawingObjects.Count; i++)
            {
                if (!ReferenceEquals(_drawingObjects[i], imported)) continue;
                index = i;
                break;
            }

            if (index >= 0) SelectDrawingObject(index);
            AppLog.Info($"Imported symbol file: {fileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unable to import symbol file: {fileName}", ex);
            ModernMessageDialog.Show(
                this,
                UiLocalization.T("The symbol could not be imported."),
                UiLocalization.T("Import Symbol File"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }
}
