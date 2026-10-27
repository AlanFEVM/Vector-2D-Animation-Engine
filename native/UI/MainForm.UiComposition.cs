namespace VectorAnimationEngine;

// Composition root: leaf controls know nothing about these hosts or their siblings.
internal sealed partial class MainForm
{
    private void BuildInspectorPages(Control inspector)
    {
        _basicInspectorPage.ShowScrollBar = true;
        _sceneEditPage.ShowScrollBar = true;
        _basicInspectorPage.Dock = DockStyle.Fill;
        _basicInspectorPage.BackColor = Theme.Panel;
        _basicInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        UiComposition.MountVertical(_basicInspectorPage.Content,
        [
            new(_objectInspector, _objectInspector.PreferredHeight),
            new(_drawingObjectInstancePanel, _drawingObjectInstancePanel.PreferredPanelHeight),
            new(_textSettingsPanel, _textSettingsPanel.PreferredHeight),
            new(_tweenCurveEditorPanel, _tweenCurveEditorPanel.PreferredPanelHeight),
            new(_imageInspector, _imageInspector.PreferredHeight)
        ]);
        foreach (var control in new Control[]
        {
            _objectInspector, _brushTipPanel, _drawingObjectInstancePanel, _textSettingsPanel,
            _mixingBrushSettingsPanel, _drawSettingsPanel, _shapeSettingsPanel,
            _tweenCurveEditorPanel, _imageInspector
        }) control.Visible = false;

        _sceneEditPage.Dock = DockStyle.Fill;
        _sceneEditPage.BackColor = Theme.Panel;
        // Scene and playback are peers, each with its own state and events.
        UiComposition.MountVertical(_sceneEditPage.Content,
        [
            new(_sceneEditorPanel, _sceneEditorPanel.PreferredPanelHeight),
            new(_playbackSettings, _playbackSettings.PreferredPanelHeight)
        ]);
        _hierarchyWindow = new HierarchyWindow(_hierarchyPanel, ProcessCmdKey);
        BuildSceneOpticsInspectorPanels();
        UiComposition.MountVertical(_basicInspectorPage.Content, [new(_spatialMaterialPanel)]);
        BuildInspectorCategoryRail(inspector);

        _animationPage.Dock = DockStyle.Fill;
        _animationPage.BackColor = Theme.Panel;
        inspector.Controls.Add(_animationPage);
        inspector.Controls.Add(_sceneEditPage);
        inspector.Controls.Add(_basicInspectorPage);
        // Added after the pages so it docks before them: the summary owns the top
        // strip of whichever page is active, in every workspace.
        BuildDocumentInfoPanel();
        _layerBlendModePanel.Dock = DockStyle.Top;
        _layerBlendModePanel.Height = _layerBlendModePanel.PreferredPanelHeight;
        inspector.Controls.Add(_layerBlendModePanel);
        _layerBlendModePanel.SendToBack();
        // Complete the module boundary across the host's outer content gutters.
        inspector.Paint += (_, e) =>
        {
            if (_layerBlendModePanel.Visible)
                InspectorModuleStyle.DrawDivider(inspector, e.Graphics, _layerBlendModePanel.Bottom);
        };
        inspector.Layout += (_, _) => inspector.Invalidate();
        _layerBlendModePanel.VisibleChanged += (_, _) => inspector.Invalidate();
        ShowWorkspace(WorkspaceView.BasicDrawing);
    }

    private void PlaceDrawingObjectInstancePanel()
    {
        UiComposition.MountVertical(_basicInspectorPage.Content, [new(_drawingObjectInstancePanel)]);
        ArrangeBasicDrawingInspectorPanels();
    }

    private bool PlaceDrawingParameterPanels()
    {
        var moved = false;
        if (!ReferenceEquals(_materialEditor.Parent, _colorInspectorPage.Content))
            moved |= UiComposition.MountVertical(_colorInspectorPage.Content, [new(_appearanceInspectorSummary), new(_materialEditor)]);
        if (moved)
        {
            _colorInspectorPage.Content.PerformLayout();
        }
        return moved;
    }

    private void ArrangeBasicDrawingInspectorPanels() => UiComposition.OrderVertical(
        _basicInspectorPage.Content,
        [
            _objectInspector, _tweenCurveEditorPanel, _drawingObjectInstancePanel,
            _spatialMaterialPanel, _textSettingsPanel, _imageInspector
        ]);

    private void ArrangeSceneMaskInspectorPanels() => UiComposition.OrderVertical(
        _sceneEditPage.Content,
        [
            _sceneLightingPanel, _sceneEditorPanel, _playbackSettings
        ]);

    private void ArrangeSceneInspectorSections()
    {
        if (!ReferenceEquals(_sceneEditorPanel.Parent, _sceneEditPage.Content)) return;
        if (IsSceneMaskDrawingInspectorContext())
        {
            ArrangeSceneMaskInspectorPanels();
            return;
        }
        UiComposition.OrderVertical(_sceneEditPage.Content,
        [
            _sceneLightingPanel, _sceneEditorPanel, _playbackSettings
        ]);
    }
}
