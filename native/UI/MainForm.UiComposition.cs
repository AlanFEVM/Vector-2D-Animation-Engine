namespace VectorAnimationEngine;

// Composition root: leaf controls know nothing about these hosts or their siblings.
internal sealed partial class MainForm
{
    private void BuildInspectorPages(Control inspector)
    {
        _basicInspectorPage.Dock = DockStyle.Fill;
        _basicInspectorPage.BackColor = Theme.Panel;
        _basicInspectorPage.ContentPadding = new Padding(0, 0, 2, 0);
        UiComposition.MountVertical(_basicInspectorPage.Content,
        [
            new(_objectInspector, _objectInspector.PreferredHeight),
            new(_brushTipPanel, _brushTipPanel.PreferredHeight),
            new(_drawingObjectInstancePanel, _drawingObjectInstancePanel.PreferredPanelHeight),
            new(_textSettingsPanel, _textSettingsPanel.PreferredHeight),
            new(_materialEditor),
            new(_mixingBrushSettingsPanel, _mixingBrushSettingsPanel.PreferredPanelHeight),
            new(_drawSettingsPanel, _drawSettingsPanel.PreferredHeight),
            new(_shapeSettingsPanel, _shapeSettingsPanel.PreferredHeight),
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
            new(_playbackSettings, 188)
        ], _hierarchyPanel);
        BuildSceneOpticsInspectorPanels();

        _animationPage.Dock = DockStyle.Fill;
        _animationPage.BackColor = Theme.Panel;
        inspector.Controls.Add(_animationPage);
        inspector.Controls.Add(_sceneEditPage);
        inspector.Controls.Add(_basicInspectorPage);
        _layerBlendModePanel.Dock = DockStyle.Top;
        _layerBlendModePanel.Height = _layerBlendModePanel.PreferredPanelHeight;
        inspector.Controls.Add(_layerBlendModePanel);
        _layerBlendModePanel.SendToBack();
        ShowWorkspace(WorkspaceView.BasicDrawing);
    }

    private void PlaceDrawingObjectInstancePanel(bool sceneEdit)
    {
        var target = sceneEdit ? _sceneEditPage.Content : _basicInspectorPage.Content;
        UiComposition.MountVertical(target, [new(_drawingObjectInstancePanel)]);
        if (sceneEdit) ArrangeSceneInspectorSections();
        else ArrangeBasicDrawingInspectorPanels();
    }

    private bool PlaceDrawingParameterPanels()
    {
        var sceneMask = IsSceneMaskDrawingInspectorContext();
        var target = sceneMask ? _sceneEditPage.Content : _basicInspectorPage.Content;
        UiSection[] sections =
        [
            new(_materialEditor), new(_brushTipPanel), new(_mixingBrushSettingsPanel),
            new(_textSettingsPanel), new(_drawSettingsPanel), new(_shapeSettingsPanel), new(_imageInspector)
        ];
        var moved = sections.Any(section => !ReferenceEquals(section.Control.Parent, target));
        if (moved)
        {
            UiComposition.MountVertical(target, sections);
            if (sceneMask) ArrangeSceneMaskInspectorPanels();
            else
            {
                ArrangeBasicDrawingInspectorPanels();
                ArrangeSceneInspectorSections();
            }
        }
        return moved;
    }

    private void ArrangeBasicDrawingInspectorPanels() => UiComposition.OrderVertical(
        _basicInspectorPage.Content,
        [
            _objectInspector, _brushTipPanel, _drawingObjectInstancePanel, _textSettingsPanel,
            _materialEditor, _mixingBrushSettingsPanel, _drawSettingsPanel,
            _shapeSettingsPanel, _tweenCurveEditorPanel, _imageInspector
        ]);

    private void ArrangeSceneMaskInspectorPanels() => UiComposition.OrderVertical(
        _sceneEditPage.Content,
        [
            _imageInspector, _textSettingsPanel, _materialEditor, _brushTipPanel,
            _mixingBrushSettingsPanel, _drawSettingsPanel, _shapeSettingsPanel,
            _drawingObjectInstancePanel, _sceneEditorPanel, _playbackSettings
        ], _hierarchyPanel);

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
            _tweenCurveEditorPanel, _drawingObjectInstancePanel, _spatialMaterialPanel,
            _sceneLightingPanel, _sceneEditorPanel, _playbackSettings
        ], _hierarchyPanel);
    }
}
