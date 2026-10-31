namespace VectorAnimationEngine;

/// <summary>
/// Host glue for the explicitly selected document page.
/// Values come from the project/scene; the panel stays dumb.
/// </summary>
internal sealed partial class MainForm
{
    private readonly DocumentInfoPanel _documentInfoPanel = new();
    private readonly ThemedScrollPanel _documentInspectorPage = new();

    private bool IsDocumentInspectorContext => _inspectorCategory == InspectorCategory.Document;

    private void BuildDocumentInfoPanel()
    {
        _documentInfoPanel.Dock = DockStyle.Top;
        _documentInfoPanel.Height = _documentInfoPanel.PreferredHeight;
        _documentInspectorPage.Dock = DockStyle.Fill;
        _documentInspectorPage.ShowScrollBar = true;
        _documentInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        _documentInspectorPage.Visible = false;
        _documentInspectorPage.Content.Controls.Add(_documentInfoPanel);
        _inspectorHost.Controls.Add(_documentInspectorPage);
        _documentInfoPanel.RenameCommitted += (_, name) => ApplyDocumentInfoRename(name);
        InitializeEnterPlaybackView();
        _documentInfoPanel.LayerPanel.BlendModeChanged += (_, e) => ApplySelectedLayerBlendMode(e.BlendMode);
        InitializeCanvasWorkspace();
    }

    private void UpdateDocumentInfoPanel()
    {
        var visible = IsDocumentInspectorContext;
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
            _scene.ObjectCount,
            scene?.Canvas is { } canvas ? $"{canvas.Width} × {canvas.Height} px" : UiLocalization.T("Not added"),
            scene?.Canvas?.BackgroundKind switch
            {
                SceneBackgroundKind.Solid => UiLocalization.T("Solid color"),
                SceneBackgroundKind.Image => IsSceneWorkspaceSelected && _canvasBackgroundError.Length > 0
                    ? UiLocalization.T("Image unavailable")
                    : _project.FindImageAsset(scene.Canvas.ImageAssetId)?.Name ?? UiLocalization.T("Missing project image"),
                _ => UiLocalization.T("Not added")
            }, _projectDirty, scene?.Canvas is not null));
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
