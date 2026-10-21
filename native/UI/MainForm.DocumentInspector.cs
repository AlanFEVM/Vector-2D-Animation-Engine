namespace VectorAnimationEngine;

/// <summary>
/// Host glue for the document summary that replaces the inspector body while
/// nothing is selected. Values come from the project/scene; the panel stays dumb.
/// </summary>
internal sealed partial class MainForm
{
    private readonly DocumentInfoPanel _documentInfoPanel = new();

    private void BuildDocumentInfoPanel()
    {
        _documentInfoPanel.Dock = DockStyle.Top;
        _documentInfoPanel.Height = _documentInfoPanel.PreferredHeight;
        _documentInfoPanel.Visible = false;
        _inspectorHost.Controls.Add(_documentInfoPanel);
        _documentInfoPanel.RenameCommitted += (_, name) => ApplyDocumentInfoRename(name);
    }

    private void UpdateDocumentInfoPanel()
    {
        var noSelection = _selectedObjects.Count == 0 && !_selectedElement.IsValid;
        var visible = noSelection
            && !_shotDirectorVisible
            && _inspectorCategory == InspectorCategory.None;
        // Only touch the flag on change: a redundant Visible write re-lays the host
        // twice per selection change.
        if (_documentInfoPanel.Visible != visible) _documentInfoPanel.Visible = visible;
        if (!visible) return;

        var scene = ActiveScene();
        var sceneSummary = scene is null
            ? "-"
            : $"{scene.Name} · "
                + $"{(scene.Dimension == SceneDimension.ThreeD ? "3D" : "2D")} · "
                + $"{CompactFormat.Number(scene.Layers.Count)} {UiLocalization.T("Layer")}";
        var location = !string.IsNullOrWhiteSpace(_projectPackagePath)
            ? _projectPackagePath
            : string.IsNullOrWhiteSpace(_projectManifestPath)
                ? null
                : _projectManifestPath;
        _documentInfoPanel.SetDocument(new DocumentInfo(
            _project.Name,
            location,
            _project.Scenes.Count,
            _project.DrawingObjects.Count,
            _project.ExternalSvgAssets.Count,
            _project.ImageAssets.Count,
            _project.AssetTags.Count,
            sceneSummary,
            _scene.FrameCount,
            _project.PlaybackFps,
            _scene.ObjectCount));
    }

    private void ApplyDocumentInfoRename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, _project.Name, StringComparison.Ordinal)) return;
        _project.Name = name;
        SetProjectDirty(true);
        UpdateProjectTitle();
        AppLog.Info($"Renamed project: {_project.Name}");
        UpdateDocumentInfoPanel();
    }
}
