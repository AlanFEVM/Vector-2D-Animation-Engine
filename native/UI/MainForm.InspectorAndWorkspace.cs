using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private void UpdateInspector(bool refreshLineEndpointStyles = true)
    {
        RefreshLayerBlendModePanel();
        UpdateSpatialTransformPanelState();
        var basicInspectorScrollY = CaptureInspectorScrollPosition(_basicInspectorPage);
        var sceneInspectorScrollY = CaptureInspectorScrollPosition(_sceneEditPage);
        var drawingObjectPanelWasVisible = _drawingObjectInstancePanel.Visible;
        var drawingObjectPanelParent = _drawingObjectInstancePanel.Parent;
        var materialEditorHeight = _materialEditor.Height;
        var materialEditorWasVisible = _materialEditor.Visible;
        var materialEditorParent = _materialEditor.Parent;
        var textSettingsWasVisible = _textSettingsPanel.Visible;
        var textSettingsParent = _textSettingsPanel.Parent;
        _basicInspectorPage.SuspendContentLayout();
        _sceneEditPage.SuspendContentLayout();
        var drawingParameterPanelsMoved = false;
        try
        {
        drawingParameterPanelsMoved = PlaceDrawingParameterPanels();
        _objectMetric.Text = $"Objects: {CompactFormat.Number(_scene.ObjectCount)}";
        if (refreshLineEndpointStyles) UpdateLineEndpointStyleControl();
        UpdateTextSettingsPanel();
        var validSelection = _selectedObjects
            .Where(index => index >= 0 && index < _scene.ObjectCount)
            .ToArray();
        if (_selectedElement.IsValid && (uint)_selectedElement.Key.ObjectIndex < _scene.ObjectCount)
        {
            _materialEditor.SetGradientPreviewTarget(ShouldPreferStrokeMaterial(
                _selectedElement.Key.Kind,
                _scene.ShapeKind[_selectedElement.Key.ObjectIndex]));
        }
        _materialEditor.SetTextObjectMode(
            IsDrawingInspectorContext()
            && (_tool == ToolMode.Text
                || IsTextEditActive()
                || (validSelection.Length == 1
                    && _scene.ShapeKind[validSelection[0]] == ShapeKind.Text)));
        var selectedSceneInstances = SelectedSceneInstances();
        var sceneInstance = SelectedSceneInstance();
        _drawingObjectInstancePanel.Visible = sceneInstance is not null;
        _materialEditor.Visible = sceneInstance is not null || !validSelection.Any(IsImportedSvgObject);
        if (sceneInstance is not null)
        {
            var sceneInstanceState = sceneInstance.EvaluateState(_frame);
            var selectedSceneInstanceStates = selectedSceneInstances
                .Select(instance => instance.EvaluateState(_frame))
                .ToArray();
            var drawingObject = _drawingObjects.FirstOrDefault(item =>
                string.Equals(item.Id, sceneInstance.DrawingObjectId, StringComparison.Ordinal));
            _drawingObjectInstancePanel.SetInstance(
                sceneInstanceState,
                drawingObject?.FrameCount ?? 1,
                selectedSceneInstanceStates.Any(CanRestoreDrawingObjectOriginalSize),
                drawingObject?.Anchor ?? PointF.Empty,
                drawingObject is not null && selectedSceneInstances.All(instance =>
                    string.Equals(instance.DrawingObjectId, drawingObject.Id, StringComparison.Ordinal)),
                selectedSceneInstanceStates.Any(state => Math.Abs(state.Alpha - sceneInstanceState.Alpha) > 0.0001f),
                selectedSceneInstanceStates.Any(state => state.TintArgb != sceneInstanceState.TintArgb));
            var drawingLayer = Array.IndexOf(_scene.LayerIds, sceneInstance.SceneLayerId);
            var layerName = IsSceneCompositionContext()
                ? ActiveScene()?.FindLayer(sceneInstance.SceneLayerId)?.Name
                : drawingLayer >= 0
                    ? _scene.LayerNames[drawingLayer]
                    : null;
            _selected.Text = selectedSceneInstances.Count > 1
                ? $"Selected: {selectedSceneInstances.Count} Symbol Instances"
                : $"Selected: {sceneInstance.Name}";
            _selectedLayer.Text = $"Layer: {layerName ?? "Missing layer"} / Symbol: {drawingObject?.Name ?? "Missing object"}";
            _selectedAtoms.Text = $"Transform: {sceneInstanceState.ScaleX:0.##}, {sceneInstanceState.ScaleY:0.##} / {sceneInstanceState.RotationZ:0.#} deg / skew {sceneInstanceState.SkewX:0.#}, {sceneInstanceState.SkewY:0.#}";
            return;
        }

        if (_selectedElements.Count > 1)
        {
            var kinds = _selectedElements.Select(hit => hit.Key.Kind).Distinct().ToArray();
            var firstLayer = validSelection.Length > 0 ? _scene.ObjectLayer[validSelection[0]] : -1;
            var mixedLayer = validSelection.Any(index => _scene.ObjectLayer[index] != firstLayer);
            var atoms = _selectedElements.Sum(hit => _scene.EstimateElementAtomCount(hit, _frame));
            var kindLabel = kinds.Length == 1 ? kinds[0].ToString() : "Mixed";
            _selected.Text = $"Selected: {CompactFormat.Number(_selectedElements.Count)} {kindLabel} parts";
            _selectedLayer.Text = mixedLayer ? "Layer: Mixed" : firstLayer >= 0 ? $"Layer: {_scene.LayerNames[firstLayer]}" : "Layer: -";
            _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(atoms)}";
            return;
        }

        if (validSelection.Length > 1)
        {
            var firstLayer = _scene.ObjectLayer[validSelection[0]];
            var mixedLayer = false;
            long atoms = 0;
            foreach (var index in validSelection)
            {
                atoms += _scene.AtomCount[index];
                if (_scene.ObjectLayer[index] != firstLayer) mixedLayer = true;
            }

            _selected.Text = $"Selected: {CompactFormat.Number(validSelection.Length)} objects";
            _selectedLayer.Text = mixedLayer ? "Layer: Mixed" : $"Layer: {_scene.LayerNames[firstLayer]}";
            _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(atoms)}";
            return;
        }

        if (validSelection.Length == 1 && _selectedObject != validSelection[0]) _selectedObject = validSelection[0];
        if (_selectedObject < 0 || _selectedObject >= _scene.ObjectCount)
        {
            _selected.Text = "Selected: None";
            _selectedLayer.Text = _scene.LayerNames.Length > 0 ? $"Layer: {_scene.LayerNames[_scene.ActiveLayer]}" : "Layer: -";
            _selectedAtoms.Text = "Atoms: -";
            return;
        }

        var layer = _scene.ObjectLayer[_selectedObject];
        _selected.Text = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == _selectedObject
            ? $"Selected: #{_selectedObject} {_selectedElement.Key.Kind} part {_selectedElement.Key.PartIndex}"
            : $"Selected: #{_selectedObject}";
        _selectedLayer.Text = $"Layer: {_scene.LayerNames[layer]}";
        var selectedAtoms = _selectedElement.IsValid
            ? _scene.EstimateElementAtomCount(_selectedElement, _frame)
            : _scene.AtomCount[_selectedObject];
        _selectedAtoms.Text = $"Atoms: {CompactFormat.Number(selectedAtoms)}";
        var fill = Color.FromArgb(_scene.Argb[_selectedObject]);
        var stroke = _scene.StrokeArgb.Length > _selectedObject ? Color.FromArgb(_scene.StrokeArgb[_selectedObject]) : ActiveStrokeColor();
        var strokePoints = VectorUnits.UnitsToStrokePoints(_scene.Stroke[_selectedObject]);
        var shape = _scene.ShapeKind[_selectedObject];
        var selectedKind = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == _selectedObject
            ? _selectedElement.Key.Kind
            : DrawingElementKind.None;
        var strokeMaterialTarget = ShouldPreferStrokeMaterial(selectedKind, shape);
        var gradientAppliesToTarget = CanEditGradientForMaterialTarget(shape, strokeMaterialTarget);
        var brushFill = IsBrushTool(_tool) && IsFillShape(shape);
        var inspectorStrokePoints = brushFill ? _brushStrokeWidthPoints : strokePoints;
        var gradientStops = gradientAppliesToTarget && _scene.HasGradient(_selectedObject)
            ? _scene.GetGradientStops(_selectedObject)
            : [];
        var materialOpacity = gradientStops.Length > 0
            ? Color.FromArgb(gradientStops[0].Argb).A / 255f
            : selectedKind != DrawingElementKind.None
                ? (strokeMaterialTarget ? stroke : fill).A / 255f
                : shape is ShapeKind.Freeform or ShapeKind.Line
                    ? stroke.A / 255f
                    : fill.A / 255f;
        if (shape is ShapeKind.Freeform or ShapeKind.Line)
        {
            _materialEditor.SetMaterial(_materialEditor.Fill, stroke, inspectorStrokePoints, materialOpacity);
        }
        else if (shape == ShapeKind.BrushStroke)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, materialOpacity);
        }
        else if (brushFill)
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), _materialEditor.Stroke, inspectorStrokePoints, materialOpacity);
        }
        else
        {
            _materialEditor.SetMaterial(Color.FromArgb(fill.A, fill), stroke, inspectorStrokePoints, materialOpacity);
        }

        _materialEditor.SetGradientPreviewTarget(strokeMaterialTarget);

        if (gradientAppliesToTarget)
        {
            _materialEditor.SetGradient(
                _scene.GetGradientKind(_selectedObject),
                _scene.HasGradient(_selectedObject)
                    ? _scene.GetGradientStops(_selectedObject)
                    : shape == ShapeKind.Line
                        ? [new GradientStop(0, stroke), new GradientStop(1, stroke)]
                        : [new GradientStop(0, fill), new GradientStop(1, stroke)]);
        }
        else
        {
            var targetColor = strokeMaterialTarget ? stroke : fill;
            _materialEditor.SetGradient(
                GradientKind.Solid,
                [new GradientStop(0, targetColor), new GradientStop(1, targetColor)]);
        }
        }
        finally
        {
            var drawingObjectLayoutChanged = drawingObjectPanelWasVisible != _drawingObjectInstancePanel.Visible;
            var materialEditorLayoutChanged = materialEditorHeight != _materialEditor.Height
                || materialEditorWasVisible != _materialEditor.Visible;
            var textSettingsLayoutChanged = textSettingsWasVisible != _textSettingsPanel.Visible;
            var basicLayoutChanged = drawingParameterPanelsMoved
                || (drawingObjectLayoutChanged
                    && ReferenceEquals(drawingObjectPanelParent, _basicInspectorPage.Content))
                || (materialEditorLayoutChanged
                    && ReferenceEquals(materialEditorParent, _basicInspectorPage.Content))
                || (textSettingsLayoutChanged
                    && ReferenceEquals(textSettingsParent, _basicInspectorPage.Content));
            var sceneLayoutChanged = drawingParameterPanelsMoved
                || (drawingObjectLayoutChanged
                    && ReferenceEquals(drawingObjectPanelParent, _sceneEditPage.Content))
                || (materialEditorLayoutChanged
                    && ReferenceEquals(materialEditorParent, _sceneEditPage.Content))
                || (textSettingsLayoutChanged
                    && ReferenceEquals(textSettingsParent, _sceneEditPage.Content));
            _sceneEditPage.ResumeContentLayout(performLayout: sceneLayoutChanged);
            _basicInspectorPage.ResumeContentLayout(performLayout: basicLayoutChanged);
            if (basicLayoutChanged)
            {
                RestoreInspectorScrollPosition(_basicInspectorPage, basicInspectorScrollY);
            }
            if (sceneLayoutChanged)
            {
                RestoreInspectorScrollPosition(_sceneEditPage, sceneInspectorScrollY);
            }
        }
    }

    private void UpdateTextSettingsPanel()
    {
        var hasSelectedText = TryGetSelectedTextObject(out _, out var selectedData);
        _textSettingsPanel.Visible = IsDrawingInspectorContext()
            && (_tool == ToolMode.Text || IsTextEditActive() || hasSelectedText);
        if (!_textSettingsPanel.Visible) return;

        var data = IsTextEditActive() ? _textEditData : hasSelectedText ? selectedData : null;
        if (data is null) return;
        _textSettingsPanel.SetSettings(
            data.FontFamilyName,
            data.FontSizePoints,
            data.FontStyle,
            data.Alignment);
    }

    private static int CaptureInspectorScrollPosition(ThemedScrollPanel page)
    {
        return page.ScrollPosition;
    }

    private void UpdateInspectorForTimelineLayerChange()
    {
        if (_selectedSceneInstanceIds.Count > 0
            || _selectedObjects.Count > 0
            || _selectedElements.Count > 0
            || _selectedObject >= 0)
        {
            return;
        }

        SetLabelText(_selected, "Selected: None");
        SetLabelText(
            _selectedLayer,
            _scene.LayerNames.Length > 0 && _scene.ActiveLayer >= 0 && _scene.ActiveLayer < _scene.LayerNames.Length
                ? $"Layer: {_scene.LayerNames[_scene.ActiveLayer]}"
                : "Layer: -");
        SetLabelText(_selectedAtoms, "Atoms: -");
    }

    private void RestoreInspectorScrollPosition(ThemedScrollPanel page, int verticalPosition)
    {
        if (verticalPosition <= 0 || !page.IsHandleCreated || page.IsDisposed) return;
        void Restore()
        {
            if (page.IsDisposed || !page.IsHandleCreated) return;
            page.RestoreScrollPosition(verticalPosition);
        }

        Restore();
        if (page.ScrollPosition == verticalPosition || IsDisposed || !IsHandleCreated) return;
        BeginInvoke((MethodInvoker)Restore);
    }

    private int[] SelectedLineEndpointStyleTargets()
    {
        if (IsSceneCompositionContext() || _selectedElements.Count > 0) return [];
        var selected = _selectedObjects
            .Where(index => (uint)index < _scene.ObjectCount)
            .Distinct()
            .ToArray();
        if (selected.Length == 0 && (uint)_selectedObject < _scene.ObjectCount)
        {
            selected = [_selectedObject];
        }

        return selected.Length > 0 && selected.All(index => _scene.ShapeKind[index] == ShapeKind.Line)
            ? selected
            : [];
    }

    private DrawingElementHit[] SelectedLineEndpointStyleElements()
    {
        if (IsSceneCompositionContext() || _selectedElements.Count == 0) return [];
        var selected = _selectedElements
            .Where(hit => hit.IsValid)
            .GroupBy(hit => hit.Key)
            .Select(group => group.First())
            .ToArray();
        return selected.Length > 0
            && selected.All(hit => _scene.TryGetLinePartEndpointStyles(hit.Key, _frame, out _, out _))
                ? selected
                : [];
    }

    private void UpdateLineEndpointStyleControl()
    {
        var elements = SelectedLineEndpointStyleElements();
        if (elements.Length > 0)
        {
            var primary = elements.FirstOrDefault(hit => hit.Key == _selectedElement.Key);
            if (!primary.IsValid) primary = elements[0];
            if (_scene.TryGetLinePartEndpointStyles(primary.Key, _frame, out var startStyle, out var endStyle))
            {
                _materialEditor.SetLineEndpointStyles(startStyle, endStyle, visible: true);
                return;
            }
        }

        var targets = SelectedLineEndpointStyleTargets();
        if (targets.Length == 0)
        {
            _materialEditor.SetLineEndpointStyles(LineEndpointStyle.Round, LineEndpointStyle.Round, visible: false);
            return;
        }

        _materialEditor.SetLineEndpointStyles(
            _scene.GetLineEndpointStyle(targets[0], startEndpoint: true),
            _scene.GetLineEndpointStyle(targets[0], startEndpoint: false),
            visible: true);
    }

    private void ApplyLineEndpointStyle(LineEndpointStyle endpointStyle, bool startEndpoint)
    {
        var selectedElements = SelectedLineEndpointStyleElements();
        var seedObjects = selectedElements.Length == 0
            ? SelectedLineEndpointStyleTargets()
            : [];
        if (selectedElements.Length == 0 && seedObjects.Length == 0) return;

        var affectedObjects = selectedElements.Length > 0
            ? selectedElements.Select(hit => hit.Key.ObjectIndex)
            : seedObjects;
        var snapshot = CreateCanvasMutationSnapshot(affectedObjects);
        var hierarchyChanged = false;
        if (selectedElements.Length > 0)
        {
            var selectedKeys = selectedElements.Select(hit => hit.Key).Distinct().ToArray();
            var materialized = _scene.MaterializeSelectedParts(selectedKeys, _frame);
            if (!materialized.Success || materialized.Parts.Length != selectedKeys.Length)
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return;
            }

            seedObjects = materialized.Parts
                .Select(part => part.Result.ObjectIndex)
                .Distinct()
                .ToArray();
            if (seedObjects.Length == 0
                || seedObjects.Any(index => (uint)index >= _scene.ObjectCount || _scene.ShapeKind[index] != ShapeKind.Line))
            {
                RestoreCanvasMutationSnapshot(snapshot);
                return;
            }
            hierarchyChanged = materialized.Changed;
        }

        var targets = EndpointStyleTargets(seedObjects, startEndpoint);
        if (targets.Length == 0)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
        var changed = false;
        foreach (var target in targets)
        {
            changed |= _scene.SetLineEndpointStyle(target.ObjectIndex, target.StartEndpoint, endpointStyle);
        }

        if (!changed)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
        if (selectedElements.Length > 0) SetSelection(seedObjects);
        hierarchyChanged |= MergeCompatibleLinesAfterDrawingOperation();
        PushUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        UpdateInspector();
        _stage.Invalidate();
    }

    private (int ObjectIndex, bool StartEndpoint)[] EndpointStyleTargets(IEnumerable<int> seedObjects, bool startEndpoint)
    {
        var targets = new HashSet<(int ObjectIndex, bool StartEndpoint)>();
        foreach (var objectIndex in seedObjects.Where(index => (uint)index < _scene.ObjectCount))
        {
            if (_scene.ShapeKind[objectIndex] != ShapeKind.Line) continue;
            targets.Add((objectIndex, startEndpoint));
            if (_scene.TryGetLineJoinNeighbor(objectIndex, startEndpoint, _frame, out var neighbor, out var neighborStart))
            {
                targets.Add((neighbor, neighborStart));
            }
        }

        return targets.OrderBy(target => target.ObjectIndex).ThenBy(target => target.StartEndpoint).ToArray();
    }

    private Color ActiveColor()
    {
        return _materialEditor.Fill;
    }

    private void ApplyGradientToSelection(GradientChangedEventArgs gradient)
    {
        if (IsSceneCompositionContext()) return;
        int[] targets;
        if (_selectedElements.Count > 0)
        {
            var strokeTarget = _materialEditor.EditingStroke;
            targets = _selectedElements
                .Where(hit => hit.IsValid
                    && (uint)hit.Key.ObjectIndex < _scene.ObjectCount
                    && SelectionKindMatchesMaterialTarget(hit.Key.Kind, strokeTarget)
                    && CanEditGradientForMaterialTarget(_scene.ShapeKind[hit.Key.ObjectIndex], strokeTarget))
                .Select(hit => hit.Key.ObjectIndex)
                .Distinct()
                .ToArray();
        }
        else
        {
            targets = _selectedObjects
                .Where(index => (uint)index < _scene.ObjectCount && SupportsGradient(_scene.ShapeKind[index]))
                .Distinct()
                .ToArray();
            if (targets.Length == 0
                && _selectedObject >= 0
                && _selectedObject < _scene.ObjectCount
                && SupportsGradient(_scene.ShapeKind[_selectedObject]))
            {
                targets = [_selectedObject];
            }
        }

        if (targets.Length == 0) return;
        var snapshot = CreateMaterialUndoSnapshot();
        var changed = false;
        foreach (var objectIndex in targets)
        {
            if (!gradient.Enabled)
            {
                if (!_scene.HasGradient(objectIndex)) continue;
                _scene.DisableLinearGradient(objectIndex);
                if (_scene.ShapeKind[objectIndex] == ShapeKind.Line)
                {
                    _scene.StrokeArgb[objectIndex] = _materialEditor.Stroke.ToArgb();
                }
                else
                {
                    _scene.Argb[objectIndex] = _materialEditor.Fill.ToArgb();
                }
                changed = true;
                continue;
            }

            if (_scene.HasGradient(objectIndex))
            {
                if (_scene.GetGradientKind(objectIndex) == gradient.Kind
                    && _scene.GetGradientStops(objectIndex).SequenceEqual(gradient.Stops))
                {
                    continue;
                }

                _scene.SetGradientPaint(
                    objectIndex,
                    gradient.Kind,
                    gradient.Stops,
                    _scene.GetGradientStart(objectIndex),
                    _scene.GetGradientEnd(objectIndex));
            }
            else
            {
                _scene.SetGradientPaint(objectIndex, gradient.Kind, gradient.Stops);
            }

            var representativeColor = gradient.Stops.Length > 0
                ? Color.FromArgb(gradient.Stops[0].Argb)
                : _scene.ShapeKind[objectIndex] == ShapeKind.Line
                    ? _materialEditor.Stroke
                    : _materialEditor.Fill;
            if (_scene.ShapeKind[objectIndex] == ShapeKind.Line)
            {
                _scene.StrokeArgb[objectIndex] = representativeColor.ToArgb();
            }
            else
            {
                _scene.Argb[objectIndex] = representativeColor.ToArgb();
            }

            changed = true;
        }

        if (!changed)
        {
            if (_materialEditSession is null) RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
        PushMaterialUndoSnapshot(snapshot);
        UpdateGradientOverlay();
        RefreshInspectorAfterMaterialChange();
        _stage.Invalidate();
    }

    private void ApplyMaterialToSelectedElements(MaterialChangedEventArgs material, float strokeUnits)
    {
        var selected = _selectedElements.ToArray();
        var affected = selected
            .Where(hit => MaterialChangeAffectsSelection(hit.Key.ObjectIndex, hit.Key.Kind, material, strokeUnits))
            .Select(hit => hit.Key)
            .ToHashSet();
        if (affected.Count == 0) return;

        var snapshot = CreateMaterialUndoSnapshot();
        var materialized = _scene.MaterializeSelectedParts(affected.ToArray(), _frame);
        if (!materialized.Success)
        {
            RestoreCanvasMutationSnapshot(snapshot);
            return;
        }

        var geometryChanged = false;
        foreach (var part in materialized.Parts)
        {
            if (!affected.Contains(part.Source)) continue;
            var objectIndex = part.Result.ObjectIndex;
            if (part.Source.Kind == DrawingElementKind.Fill)
            {
                _scene.Argb[objectIndex] = TargetFillArgb(objectIndex, material);
                _scene.DisableLinearGradient(objectIndex);
                if (_scene.Stroke[objectIndex] != 0)
                {
                    _scene.Stroke[objectIndex] = 0;
                    geometryChanged = true;
                }

                continue;
            }

            var color = TargetStrokeColor(objectIndex, material);
            _scene.Argb[objectIndex] = Color.FromArgb(0, color).ToArgb();
            _scene.StrokeArgb[objectIndex] = color.ToArgb();
            if (_scene.ShapeKind[objectIndex] == ShapeKind.Line
                && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged))
            {
                _scene.DisableLinearGradient(objectIndex);
            }
            var targetStrokeUnits = TargetStrokeUnits(objectIndex, material, strokeUnits);
            if ((material.ApplyAll || material.StrokeWidthChanged)
                && Math.Abs(_scene.Stroke[objectIndex] - targetStrokeUnits) > 0.001f)
            {
                if (IsFreehandShape(_scene.ShapeKind[objectIndex]))
                {
                    _scene.UpdateFreehandStrokeWidth(objectIndex, targetStrokeUnits, rebuildGeometryIndex: false);
                }
                else
                {
                    _scene.Stroke[objectIndex] = targetStrokeUnits;
                    _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), targetStrokeUnits + VectorUnits.FromPixels(2));
                }

                geometryChanged = true;
            }
        }

        if (geometryChanged) _scene.RebuildGeometryIndex();
        var selectedObjects = materialized.Parts
            .Where(part => affected.Contains(part.Source))
            .Select(part => part.Result.ObjectIndex)
            .Distinct()
            .ToArray();
        SetSelection(selectedObjects);
        var hierarchyChanged = materialized.Changed;
        if (affected.Any(key => key.Kind == DrawingElementKind.Fill))
        {
            hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
        }

        hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();

        PushMaterialUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        RefreshInspectorAfterMaterialChange();
        _stage.Invalidate();
    }

    private void ApplyMaterialToSelectedObjects(MaterialChangedEventArgs material, float strokeUnits)
    {
        var targets = _selectedObjects.Where(index => (uint)index < _scene.ObjectCount).Distinct().ToArray();
        if (targets.Length < 2) return;
        var snapshot = CreateMaterialUndoSnapshot();
        var changed = false;
        var geometryChanged = false;
        foreach (var objectIndex in targets)
        {
            changed |= ApplyMaterialToWholeObject(objectIndex, material, strokeUnits, out var objectGeometryChanged);
            geometryChanged |= objectGeometryChanged;
        }

        if (!changed)
        {
            if (_materialEditSession is null) RestoreCanvasMutationSnapshot(snapshot);
            return;
        }
        if (geometryChanged) _scene.RebuildGeometryIndex();
        var hierarchyChanged = false;
        var changedFill = material.ApplyAll || material.FillChanged || material.OpacityChanged;
        if (changedFill) hierarchyChanged |= MergeSelectedFillsAfterMaterialChange();
        hierarchyChanged |= MergeCompatibleLinesAfterMaterialChange();
        PushMaterialUndoSnapshot(snapshot);
        RefreshHierarchyAfterMaterialChange(hierarchyChanged);
        RefreshInspectorAfterMaterialChange();
        _stage.Invalidate();
    }

    private bool ApplyMaterialToWholeObject(
        int objectIndex,
        MaterialChangedEventArgs material,
        float requestedStrokeUnits,
        out bool geometryChanged)
    {
        geometryChanged = false;
        var shape = _scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.ImportedSvg) return false;
        var fillCapable = IsFillShape(shape) || shape == ShapeKind.BrushStroke;
        var changed = false;

        if (fillCapable && (material.ApplyAll || material.FillChanged || material.OpacityChanged))
        {
            var fill = TargetFillArgb(objectIndex, material);
            if (_scene.Argb[objectIndex] != fill || _scene.HasGradient(objectIndex))
            {
                _scene.Argb[objectIndex] = fill;
                _scene.DisableLinearGradient(objectIndex);
                changed = true;
            }

            if (shape == ShapeKind.BrushStroke && _scene.StrokeArgb[objectIndex] != fill)
            {
                _scene.StrokeArgb[objectIndex] = fill;
                changed = true;
            }
        }

        if (shape is not (ShapeKind.BrushStroke or ShapeKind.Text)
            && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged))
        {
            var stroke = TargetStrokeColor(objectIndex, material).ToArgb();
            if (_scene.StrokeArgb[objectIndex] != stroke)
            {
                _scene.StrokeArgb[objectIndex] = stroke;
                changed = true;
            }

            if (shape == ShapeKind.Line && _scene.HasGradient(objectIndex))
            {
                _scene.DisableLinearGradient(objectIndex);
                changed = true;
            }
        }

        if (shape != ShapeKind.Text && (material.ApplyAll || material.StrokeWidthChanged))
        {
            var stroke = TargetStrokeUnits(objectIndex, material, requestedStrokeUnits);
            if (Math.Abs(_scene.Stroke[objectIndex] - stroke) > 0.001f)
            {
                if (IsFreehandShape(shape))
                {
                    _scene.UpdateFreehandStrokeWidth(objectIndex, stroke, rebuildGeometryIndex: false);
                }
                else
                {
                    _scene.Stroke[objectIndex] = stroke;
                    if (shape == ShapeKind.Line)
                    {
                        _scene.Height[objectIndex] = Math.Max(VectorUnits.FromPixels(3), stroke + VectorUnits.FromPixels(2));
                    }
                }

                changed = true;
                geometryChanged = true;
            }
        }

        return changed;
    }

    private bool WholeObjectMaterialWouldChange(int objectIndex, MaterialChangedEventArgs material, float requestedStrokeUnits)
    {
        var shape = _scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.ImportedSvg) return false;
        var fillCapable = IsFillShape(shape) || shape == ShapeKind.BrushStroke;
        if (fillCapable
            && (material.ApplyAll || material.FillChanged || material.OpacityChanged)
            && (_scene.Argb[objectIndex] != TargetFillArgb(objectIndex, material) || _scene.HasGradient(objectIndex)))
        {
            return true;
        }

        if (shape == ShapeKind.BrushStroke
            && (material.ApplyAll || material.FillChanged || material.OpacityChanged)
            && _scene.StrokeArgb[objectIndex] != TargetFillArgb(objectIndex, material))
        {
            return true;
        }

        if (shape is not (ShapeKind.BrushStroke or ShapeKind.Text)
            && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged)
            && (_scene.StrokeArgb[objectIndex] != TargetStrokeColor(objectIndex, material).ToArgb()
                || (shape == ShapeKind.Line && _scene.HasGradient(objectIndex))))
        {
            return true;
        }

        return shape != ShapeKind.Text
            && (material.ApplyAll || material.StrokeWidthChanged)
            && Math.Abs(_scene.Stroke[objectIndex] - TargetStrokeUnits(objectIndex, material, requestedStrokeUnits)) > 0.001f;
    }

    private bool MaterialChangeAffectsSelection(int objectIndex, DrawingElementKind selectedKind, MaterialChangedEventArgs material, float strokeUnits)
    {
        if (selectedKind == DrawingElementKind.Fill)
        {
            if (!material.ApplyAll && !material.FillChanged && !material.OpacityChanged) return false;
            return _scene.Argb[objectIndex] != TargetFillArgb(objectIndex, material)
                || _scene.HasGradient(objectIndex);
        }

        if (selectedKind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke)
        {
            if (!material.ApplyAll && !material.StrokeChanged && !material.StrokeWidthChanged && !material.OpacityChanged) return false;
            return _scene.StrokeArgb[objectIndex] != TargetStrokeColor(objectIndex, material).ToArgb()
                || Math.Abs(_scene.Stroke[objectIndex] - TargetStrokeUnits(objectIndex, material, strokeUnits)) > 0.001f
                || (_scene.ShapeKind[objectIndex] == ShapeKind.Line
                    && (material.ApplyAll || material.StrokeChanged || material.OpacityChanged)
                    && _scene.HasGradient(objectIndex));
        }

        return WholeObjectMaterialWouldChange(objectIndex, material, strokeUnits);
    }

    private int TargetFillArgb(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.Argb[objectIndex]);
        var color = material.ApplyAll || material.FillChanged ? material.Fill : current;
        if (material.ApplyAll || material.OpacityChanged)
        {
            color = Color.FromArgb((int)Math.Clamp(material.Opacity * 255, 0, 255), color);
        }

        return color.ToArgb();
    }

    private Color TargetStrokeColor(int objectIndex, MaterialChangedEventArgs material)
    {
        var current = Color.FromArgb(_scene.StrokeArgb[objectIndex]);
        var color = material.ApplyAll || material.StrokeChanged ? material.Stroke : current;
        if (material.ApplyAll || material.OpacityChanged)
        {
            color = Color.FromArgb((int)Math.Clamp(material.Opacity * 255, 0, 255), color);
        }

        return color;
    }

    private float TargetStrokeUnits(int objectIndex, MaterialChangedEventArgs material, float requestedStrokeUnits)
    {
        var stroke = material.ApplyAll || material.StrokeWidthChanged ? requestedStrokeUnits : _scene.Stroke[objectIndex];
        return IsFreehandShape(_scene.ShapeKind[objectIndex])
            ? Math.Max(VectorUnits.MinimumStrokeUnits, stroke)
            : stroke;
    }

    private float ActiveStrokeUnits()
    {
        var points = _tool switch
        {
            ToolMode.Brush => _brushStrokeWidthPoints,
            ToolMode.PressureBrush => _brushStrokeWidthPoints,
            ToolMode.MixingBrush => _brushStrokeWidthPoints,
            ToolMode.Eraser => _eraserStrokeWidthPoints,
            _ when IsStandardStrokeTool(_tool) => _standardStrokeWidthPoints,
            _ => (float)_materialEditor.StrokeWidth
        };
        return VectorUnits.StrokePointsToUnits(points);
    }

    private Color ActiveStrokeColor() => _materialEditor.Stroke;

    internal bool ReloadModulesForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || plan.Modules == HotReloadModule.None) return false;
        if (InvokeRequired)
        {
            BeginInvoke(() => ReloadModulesForHotReload(plan));
            return true;
        }

        var engineChanged = plan.Includes(HotReloadModule.Engine);
        var workspaceChanged = engineChanged || plan.Includes(HotReloadModule.Workspace);
        var timelineChanged = engineChanged || plan.Includes(HotReloadModule.Timeline);
        var inspectorChanged = engineChanged || plan.Includes(HotReloadModule.Inspector);
        SuspendLayout();
        try
        {
            if (engineChanged)
            {
                if (IsSceneCompositionContext()) RebuildSceneComposition();
                else RebuildDrawingObjectUnderlay();
            }

            if (engineChanged || plan.Includes(HotReloadModule.Rendering)) _stage.ReloadRenderingModuleForHotReload();
            if (workspaceChanged) RefreshWorkspaceModulesAfterHotReload();
            if (timelineChanged) _timeline.RefreshTimeline();
            if (inspectorChanged)
            {
                _drawSettings.NotifyChanged();
                SyncBrushTipSettings();
                UpdateEraserOptionsPresentation();
                UpdatePencilSettingsPanelPresentation();
                UpdateShapeSettingsPanelPresentation();
                UpdateInspector();
            }

            if (plan.Includes(HotReloadModule.Shell))
            {
                RefreshToolButtons();
                ApplyToolCursor();
            }

            UpdateStatusBar();
            InvalidateControlTree(this);
            AppLog.Info($"Module reload applied without recreating the workbench: {plan.Modules}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Module reload failed: {plan.Modules}", ex);
            return false;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    internal bool RebuildWorkbenchForHotReload(HotReloadPlan plan)
    {
        if (IsDisposed || _restartRequested) return false;
        if (InvokeRequired)
        {
            BeginInvoke(() => RebuildWorkbenchForHotReload(plan));
            return true;
        }

        try
        {
            var handler = RestartRequested;
            if (handler is null) return false;
            if (!CommitTextEdit()) return false;
            StopPlayback();
            FinishPointerInteractionForFrameChange();
            HideToolFlyouts();
            var state = CaptureEditorRestartState();
            _restartRequested = true;
            AppLog.Info($"Rebuilding workbench after runtime hot reload: {plan.Modules}; types: {plan.UpdatedTypes}");
            handler(this, new EditorRestartRequestedEventArgs(state));
            return true;
        }
        catch (Exception ex)
        {
            _restartRequested = false;
            AppLog.Error($"Workbench rebuild failed during module hot reload: {plan.Modules}", ex);
            return false;
        }
    }

    internal bool RequestProcessRestartForHotReload()
    {
        if (IsDisposed || _restartRequested) return false;
        RequestEditorRestart();
        return _restartRequested;
    }

    internal void SetHotReloadStatus(HotReloadUiState state, long generation)
    {
        if (!IsModuleHotReloadEnabled() || IsDisposed) return;
        var text = state switch
        {
            HotReloadUiState.Applying => "Module Reloading",
            HotReloadUiState.Applied => "Module Reload Applied",
            HotReloadUiState.Recovering => "Module Reload Recovering",
            HotReloadUiState.Failed => "Module Reload Failed",
            _ => "Module Reload On"
        };
        _devReloadStatus.Text = $"{UiLocalization.T(text)} #{generation}";
        _devReloadStatus.ForeColor = state switch
        {
            HotReloadUiState.Applied => Theme.Accent,
            HotReloadUiState.Failed => Theme.Danger,
            HotReloadUiState.Recovering => Theme.Warning,
            _ => Theme.Muted
        };
    }

    private void RefreshWorkspaceModulesAfterHotReload()
    {
        _hierarchyPanel.RefreshScene(force: true);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        UpdateSceneDimensionButton();
        _libraryVaultPanel.BindProject(_project, () => _frame);
        _libraryVaultPanel.BindScene(_scene, () => _selectedObject);
        _libraryVaultPanel.SetActiveDrawingObject(ActiveDrawingObject()?.Id);
        if (_vaultDrawerOpen) _libraryVaultPanel.RefreshProjectObjects();
    }

    private static void InvalidateControlTree(Control control)
    {
        control.Invalidate();
        foreach (Control child in control.Controls) InvalidateControlTree(child);
    }

    private void ShowWorkspace(WorkspaceView view)
    {
        if (view == WorkspaceView.Animation) view = WorkspaceView.SceneEditor;
        if (!CommitTextEdit()) return;
        FinishPointerInteractionForContextChange();
        HideToolFlyouts();
        _toolTip.HideTip();
        if (view != WorkspaceView.BasicDrawing) HideBrushColorPalette();
        var basicDrawing = view == WorkspaceView.BasicDrawing;
        var sceneEdit = view == WorkspaceView.SceneEditor;
        var inspector = _basicInspectorPage.Parent;
        SuspendLayout();
        inspector?.SuspendLayout();
        _basicInspectorPage.SuspendContentLayout();
        _sceneEditPage.SuspendContentLayout();
        try
        {
            PlaceDrawingObjectInstancePanel(sceneEdit);
            _workspaceHeader.Height = basicDrawing ? 74 : 38;
            _drawingObjectRow.Visible = basicDrawing;
            _sceneDimensionButton.Visible = sceneEdit;
            _timeline.Visible = true;
            if (basicDrawing)
            {
                BindActiveDrawingObjectScene(resetView: false);
            }
            else if (sceneEdit)
            {
                BindSceneEditStage(resetView: false);
            }
            UpdateSceneDimensionButton();

            if (!basicDrawing && !IsSceneMaskEditing() && IsBasicDrawingOnlyTool(_tool))
            {
                _tool = ToolMode.Select;
                CancelTraditionalPenPath();
                CancelPenCurve();
                CancelFreehandStroke();
                _stage.ClearDrawingPreview();
            }

            RefreshToolButtons();
            UpdateEraserOptionsPresentation();
            UpdatePencilSettingsPanelPresentation();
            UpdateShapeSettingsPanelPresentation();
            _brushTipPanel.Visible = IsBrushTool(_tool) || _tool == ToolMode.Eraser;
            _mixingBrushSettingsPanel.Visible = _tool == ToolMode.MixingBrush;
            UpdateFillEdgeBezierOverlay();
            ApplyToolCursor();
            RefreshTweenCurveInspector();

            _basicInspectorPage.Visible = basicDrawing;
            _sceneEditPage.Visible = sceneEdit;
            _animationPage.Visible = false;
            UpdateSceneSpatialControlsVisibility();
            UpdateSpatialTransformPanelState();
        }
        finally
        {
            _basicInspectorPage.ResumeContentLayout(performLayout: basicDrawing);
            _sceneEditPage.ResumeContentLayout(performLayout: sceneEdit);
            inspector?.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
        }
    }

    private void PlaceDrawingObjectInstancePanel(bool sceneEdit)
    {
        var target = sceneEdit ? _sceneEditPage.Content : _basicInspectorPage.Content;
        if (!ReferenceEquals(_drawingObjectInstancePanel.Parent, target))
        {
            target.Controls.Add(_drawingObjectInstancePanel);
        }
        _drawingObjectInstancePanel.BringToFront();
        if (!sceneEdit)
        {
            _materialEditor.BringToFront();
            _shapeSettingsPanel.BringToFront();
        }
    }

    private bool IsSceneMaskDrawingInspectorContext()
    {
        return _workspaceTabs.SelectedView == WorkspaceView.SceneEditor && IsSceneMaskEditing();
    }

    private bool IsDrawingInspectorContext()
    {
        return _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            || IsSceneMaskDrawingInspectorContext();
    }

    private bool PlaceDrawingParameterPanels()
    {
        var target = IsSceneMaskDrawingInspectorContext()
            ? _sceneEditPage.Content
            : _basicInspectorPage.Content;
        Control[] panels =
        [
            _materialEditor,
            _brushTipPanel,
            _mixingBrushSettingsPanel,
            _textSettingsPanel,
            _drawSettingsPanel,
            _shapeSettingsPanel
        ];
        var moved = false;
        foreach (var panel in panels)
        {
            if (ReferenceEquals(panel.Parent, target)) continue;
            target.Controls.Add(panel);
            moved = true;
        }

        if (ReferenceEquals(target, _sceneEditPage.Content))
        {
            ArrangeSceneMaskInspectorPanels();
        }
        else if (moved)
        {
            ArrangeBasicDrawingInspectorPanels();
            ArrangeSceneInspectorSections();
        }

        return moved;
    }

    private void ArrangeBasicDrawingInspectorPanels()
    {
        if (ReferenceEquals(_drawingObjectInstancePanel.Parent, _basicInspectorPage.Content))
        {
            _drawingObjectInstancePanel.BringToFront();
        }
        _textSettingsPanel.BringToFront();
        _materialEditor.BringToFront();
        _mixingBrushSettingsPanel.BringToFront();
        _drawSettingsPanel.BringToFront();
        _shapeSettingsPanel.BringToFront();
        _tweenCurveEditorPanel.BringToFront();
    }

    private void ArrangeSceneMaskInspectorPanels()
    {
        var content = _sceneEditPage.Content;
        // Docking is evaluated back-to-front, ending with the text panel at the top.
        Control[] frontToBack =
        [
            _hierarchyPanel,
            _spatialTransformPanel,
            _sceneWorkflowControls,
            _drawingObjectInstancePanel,
            _shapeSettingsPanel,
            _drawSettingsPanel,
            _mixingBrushSettingsPanel,
            _brushTipPanel,
            _materialEditor,
            _textSettingsPanel
        ];
        var childIndex = 0;
        foreach (var control in frontToBack)
        {
            if (!ReferenceEquals(control.Parent, content)) continue;
            if (content.Controls.GetChildIndex(control) != childIndex)
            {
                content.Controls.SetChildIndex(control, childIndex);
            }
            childIndex++;
        }
    }

    private static bool IsDrawingTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen or ToolMode.Pencil or ToolMode.Brush or ToolMode.PressureBrush or ToolMode.MixingBrush;
    }

    private static bool IsBasicDrawingOnlyTool(ToolMode tool)
    {
        return IsDrawingTool(tool)
            || tool is ToolMode.Text or ToolMode.Fill or ToolMode.InkBottle or ToolMode.Eyedropper or ToolMode.Gradient or ToolMode.Eraser;
    }

    internal static bool IsToolVisibleInWorkspace(WorkspaceView view, ToolMode tool)
    {
        if (view == WorkspaceView.Animation) view = WorkspaceView.SceneEditor;
        if (tool == ToolMode.Transform3D) return view == WorkspaceView.SceneEditor;
        return view == WorkspaceView.BasicDrawing || !IsBasicDrawingOnlyTool(tool);
    }

    private bool IsToolVisibleForCurrentContext(ToolMode tool)
    {
        var workspace = _workspaceTabs.SelectedView;
        if (workspace == WorkspaceView.SceneEditor && IsSceneMaskEditing())
        {
            return tool != ToolMode.Transform3D;
        }
        if (!IsToolVisibleInWorkspace(workspace, tool)) return false;
        if (workspace != WorkspaceView.SceneEditor) return true;
        return IsSceneReferenceView()
            ? IsScene3DView()
                ? tool is ToolMode.Select or ToolMode.Transform3D or ToolMode.Hand
                : IsScene2DFrontView()
                    ? tool is ToolMode.Select or ToolMode.Transform or ToolMode.Distort or ToolMode.Hand
                    : tool is ToolMode.Select or ToolMode.Hand
            : tool != ToolMode.Transform3D;
    }

    private bool CanActivateTool(ToolMode tool)
    {
        return IsToolVisibleForCurrentContext(tool)
            && (!DrawingToolsBlocked() || !IsBasicDrawingOnlyTool(tool));
    }

    private static bool IsStandardStrokeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star or ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen;
    }

    private static bool IsFreehandTool(ToolMode tool)
    {
        return tool is ToolMode.Pencil or ToolMode.Brush or ToolMode.PressureBrush or ToolMode.MixingBrush or ToolMode.Eraser;
    }

    private static bool IsBrushTool(ToolMode tool)
    {
        return tool is ToolMode.Brush or ToolMode.PressureBrush or ToolMode.MixingBrush;
    }

    private static bool IsFreehandShape(ShapeKind shape)
    {
        return shape is ShapeKind.Freeform or ShapeKind.BrushStroke;
    }

    internal static bool SupportsMarqueeSelection(ToolMode tool)
    {
        return tool is ToolMode.Select or ToolMode.Transform or ToolMode.Distort;
    }

    internal static bool ShouldShowFillEdgeBezierOverlay(
        ToolMode tool,
        int selectedObjectCount,
        bool hasEditableFillBoundarySelection)
    {
        return tool == ToolMode.Select
            && selectedObjectCount == 1
            && hasEditableFillBoundarySelection;
    }

    internal static bool TryGetSelectedFillPartIndex(
        int selectedObject,
        IReadOnlyList<DrawingElementHit> selectedElements,
        out int partIndex)
    {
        partIndex = -1;
        if (selectedElements.Count != 1
            || selectedElements[0].Key.Kind != DrawingElementKind.Fill
            || selectedElements[0].Key.ObjectIndex != selectedObject)
        {
            return false;
        }

        partIndex = selectedElements[0].Key.PartIndex;
        return partIndex >= 0;
    }

    internal static bool IsWholeFillElementSelection(
        VectorScene scene,
        int frame,
        int selectedObject,
        IReadOnlyList<DrawingElementHit> selectedElements)
    {
        if (!TryGetSelectedFillPartIndex(selectedObject, selectedElements, out var selectedPartIndex))
        {
            return false;
        }

        var parts = scene.GetFillParts(selectedObject, frame);
        return parts.Length == 1
            && parts[0].PartIndex == selectedPartIndex;
    }

    internal static bool TryGetSelectedFillBoundaryBezierPart(
        VectorScene scene,
        int frame,
        int selectedObject,
        IReadOnlyList<DrawingElementHit> selectedElements,
        out int partIndex)
    {
        partIndex = -1;
        if (selectedElements.Count != 1
            || selectedElements[0].Key.Kind != DrawingElementKind.BoundaryStroke
            || selectedElements[0].Key.ObjectIndex != selectedObject
            || !scene.TryGetExactFillBezierSegmentForBoundary(selectedElements[0], frame, out var segment))
        {
            return false;
        }

        partIndex = segment.PartIndex;
        return true;
    }

    internal static bool ShouldBeginTransformMarquee(
        TransformHandleKind handle,
        bool hasSelectableTarget)
    {
        return handle == TransformHandleKind.None && !hasSelectableTarget;
    }

    internal static bool HasActiveDistortHandle(
        TransformHandleKind handle,
        bool visualHandleActive)
    {
        return handle != TransformHandleKind.None || visualHandleActive;
    }

    private static bool IsFillShape(ShapeKind shape)
    {
        return shape is not ShapeKind.Line
            and not ShapeKind.Freeform
            and not ShapeKind.BrushStroke
            and not ShapeKind.ImportedSvg
            and not ShapeKind.MixingStroke;
    }

    private static bool SupportsGradient(ShapeKind shape) => shape != ShapeKind.Text
        && (IsFillShape(shape) || shape == ShapeKind.Line);

    internal static bool ShouldPreferStrokeMaterial(DrawingElementKind selectedKind, ShapeKind shape)
    {
        return selectedKind switch
        {
            DrawingElementKind.Fill => false,
            DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke => true,
            _ => shape == ShapeKind.Line
        };
    }

    internal static bool CanEditGradientForMaterialTarget(ShapeKind shape, bool strokeTarget)
    {
        return strokeTarget ? shape == ShapeKind.Line : SupportsGradient(shape);
    }

    private static bool SelectionKindMatchesMaterialTarget(DrawingElementKind kind, bool strokeTarget)
    {
        return strokeTarget
            ? kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke
            : kind == DrawingElementKind.Fill;
    }

    private static bool IsShapeTool(ToolMode tool)
    {
        return tool is ToolMode.Rectangle or ToolMode.Ellipse or ToolMode.Triangle or ToolMode.Polygon or ToolMode.Star;
    }

    internal static bool ShouldShowShapeSettings(
        WorkspaceView view,
        ToolMode tool,
        bool sceneMaskEditing = false)
    {
        return (view == WorkspaceView.BasicDrawing
                || view == WorkspaceView.SceneEditor && sceneMaskEditing)
            && tool is ToolMode.Polygon or ToolMode.Star;
    }

    internal static bool ShouldShowPencilSettings(
        WorkspaceView view,
        ToolMode tool,
        bool sceneMaskEditing = false)
    {
        return (view == WorkspaceView.BasicDrawing
                || view == WorkspaceView.SceneEditor && sceneMaskEditing)
            && tool == ToolMode.Pencil;
    }

    private void UpdatePencilSettingsPanelPresentation()
    {
        var sceneMaskEditing = IsSceneMaskDrawingInspectorContext();
        _drawSettingsPanel.Visible = ShouldShowPencilSettings(
            _workspaceTabs.SelectedView,
            _tool,
            sceneMaskEditing);
        if (_drawSettingsPanel.Visible && !sceneMaskEditing) _drawSettingsPanel.BringToFront();
        if (sceneMaskEditing) ArrangeSceneMaskInspectorPanels();
    }

    private void UpdateShapeSettingsPanelPresentation()
    {
        if (ToolShapeKind(_tool) is { } shape && ShapeSettingsPanel.SupportsShape(shape))
        {
            _shapeSettingsPanel.SetShape(shape);
        }

        var sceneMaskEditing = IsSceneMaskDrawingInspectorContext();
        _shapeSettingsPanel.Visible = ShouldShowShapeSettings(
            _workspaceTabs.SelectedView,
            _tool,
            sceneMaskEditing);
        if (sceneMaskEditing) ArrangeSceneMaskInspectorPanels();
    }

    private static bool IsLineTool(ToolMode tool)
    {
        return tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen or ToolMode.Pencil;
    }

    private static bool UsesStrokeGradient(ToolMode tool) => tool is ToolMode.Line or ToolMode.Pen or ToolMode.SimplePen;

    private static ShapeKind? ToolShapeKind(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Rectangle => ShapeKind.Rectangle,
            ToolMode.Ellipse => ShapeKind.Ellipse,
            ToolMode.Triangle => ShapeKind.Triangle,
            ToolMode.Polygon => ShapeKind.Polygon,
            ToolMode.Star => ShapeKind.Star,
            ToolMode.Line => ShapeKind.Line,
            _ => null
        };
    }

    private static SvgIconKind ToolIconKind(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Ellipse => SvgIconKind.Ellipse,
            ToolMode.Triangle => SvgIconKind.Triangle,
            ToolMode.Polygon => SvgIconKind.Polygon,
            ToolMode.Star => SvgIconKind.Star,
            ToolMode.Line => SvgIconKind.Line,
            ToolMode.Pen => SvgIconKind.Pen,
            ToolMode.SimplePen => SvgIconKind.SimplePen,
            ToolMode.Pencil => SvgIconKind.Pencil,
            ToolMode.Brush => SvgIconKind.Brush,
            ToolMode.PressureBrush => SvgIconKind.PressureBrush,
            ToolMode.MixingBrush => SvgIconKind.MixingBrush,
            ToolMode.Text => SvgIconKind.Text,
            ToolMode.Fill => SvgIconKind.Fill,
            ToolMode.InkBottle => SvgIconKind.InkBottle,
            ToolMode.Eyedropper => SvgIconKind.Eyedropper,
            ToolMode.Gradient => SvgIconKind.Gradient,
            ToolMode.Eraser => SvgIconKind.Eraser,
            ToolMode.Transform3D => SvgIconKind.Transform3D,
            ToolMode.Transform => SvgIconKind.Transform,
            ToolMode.Distort => SvgIconKind.Distort,
            ToolMode.Hand => SvgIconKind.Pan,
            ToolMode.Select => SvgIconKind.Select,
            _ => SvgIconKind.Rectangle
        };
    }

    private static string ShapeToolName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Ellipse => "Ellipse Tool",
            ToolMode.Triangle => "Triangle Tool",
            ToolMode.Polygon => "Polygon Tool",
            ToolMode.Star => "Star Tool",
            _ => "Rectangle Tool"
        };
    }

    private static string LineToolName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Pen => "Pen Tool",
            ToolMode.SimplePen => "Simple Pen Tool",
            ToolMode.Pencil => "Pencil Tool",
            _ => "Line Tool"
        };
    }

    private static string ToolPairName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.Transform => "Free Transform Tool",
            ToolMode.Transform3D => "3D Transform Tool",
            ToolMode.Distort => "Distort Tool",
            ToolMode.Fill => "Fill Tool",
            ToolMode.InkBottle => "Ink Bottle Tool",
            _ => "Select Tool"
        };
    }

    private sealed class ToolPairGroup(ToolMode[] tools)
    {
        public ToolMode[] Tools { get; } = tools;
        public ToolMode ActiveTool { get; set; } = tools[0];
        public SvgIconButton? ParentButton { get; set; }
        public FlowLayoutPanel? Flyout { get; set; }
        public Dictionary<ToolMode, Button> FlyoutButtons { get; } = new();
        public System.Windows.Forms.Timer HideTimer { get; } = new() { Interval = 100 };
        public DateTime HideAtUtc { get; set; }

        public bool Contains(ToolMode tool) => Tools.Contains(tool);
    }

    private static string BrushToolName(ToolMode tool)
    {
        return tool switch
        {
            ToolMode.PressureBrush => "Pressure Brush Tool",
            ToolMode.MixingBrush => "Mixing Brush Tool",
            _ => "Brush Tool"
        };
    }

    private static ToolMode? ToolModeForShape(ShapeKind shape)
    {
        return shape switch
        {
            ShapeKind.Rectangle => ToolMode.Rectangle,
            ShapeKind.Ellipse => ToolMode.Ellipse,
            ShapeKind.Triangle => ToolMode.Triangle,
            ShapeKind.Polygon => ToolMode.Polygon,
            ShapeKind.Star => ToolMode.Star,
            ShapeKind.Line => ToolMode.Line,
            _ => null
        };
    }

    private void RefreshToolButtons()
    {
        EnsureToolValidForSceneContext();
        var workspace = _workspaceTabs.SelectedView;
        var drawingToolsBlocked = DrawingToolsBlocked();
        _lastDrawingToolsBlocked = drawingToolsBlocked;
        foreach (var (tool, button) in _toolButtons)
        {
            var visible = IsToolVisibleForCurrentContext(tool);
            var enabled = visible && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
            button.Visible = visible;
            button.Enabled = enabled;
            if (tool == _tool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        RefreshToolPairGroup(_selectionToolGroup, workspace, drawingToolsBlocked);
        RefreshToolPairGroup(_paintToolGroup, workspace, drawingToolsBlocked);

        if (_shapeToolButton is not null)
        {
            var visible = _shapeTools.Any(IsToolVisibleForCurrentContext);
            var enabled = _shapeTools.Any(tool =>
                IsToolVisibleForCurrentContext(tool)
                && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool)));
            _shapeToolButton.Visible = visible;
            _shapeToolButton.Enabled = enabled;
            _shapeToolButton.Icon = ToolIconKind(_activeShapeTool);
            _shapeToolButton.Tag = _activeShapeTool;
            _shapeToolButton.AccessibleName = ShapeToolName(_activeShapeTool);
            if (IsShapeTool(_tool)) Theme.StyleActiveButton(_shapeToolButton);
            else Theme.StyleButton(_shapeToolButton);
            if (!enabled) _shapeToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _shapeToolButton.Invalidate();
            if (!visible) HideShapeToolFlyout();
        }

        foreach (var (tool, button) in _shapeFlyoutButtons)
        {
            var visible = IsToolVisibleForCurrentContext(tool);
            var enabled = visible && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
            button.Visible = visible;
            button.Enabled = enabled;
            if (tool == _activeShapeTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_lineToolButton is not null)
        {
            var visible = _lineTools.Any(IsToolVisibleForCurrentContext);
            var enabled = _lineTools.Any(tool =>
                IsToolVisibleForCurrentContext(tool)
                && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool)));
            _lineToolButton.Visible = visible;
            _lineToolButton.Enabled = enabled;
            _lineToolButton.Icon = ToolIconKind(_activeLineTool);
            _lineToolButton.Tag = _activeLineTool;
            _lineToolButton.AccessibleName = LineToolName(_activeLineTool);
            if (IsLineTool(_tool)) Theme.StyleActiveButton(_lineToolButton);
            else Theme.StyleButton(_lineToolButton);
            if (!enabled) _lineToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _lineToolButton.Invalidate();
            if (!visible) HideLineToolFlyout();
        }

        foreach (var (tool, button) in _lineFlyoutButtons)
        {
            var visible = IsToolVisibleForCurrentContext(tool);
            var enabled = visible && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
            button.Visible = visible;
            button.Enabled = enabled;
            if (tool == _activeLineTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        if (_brushToolButton is not null)
        {
            var visible = _brushTools.Any(IsToolVisibleForCurrentContext);
            var enabled = _brushTools.Any(tool =>
                IsToolVisibleForCurrentContext(tool)
                && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool)));
            _brushToolButton.Visible = visible;
            _brushToolButton.Enabled = enabled;
            _brushToolButton.Icon = ToolIconKind(_activeBrushTool);
            _brushToolButton.Tag = _activeBrushTool;
            _brushToolButton.AccessibleName = BrushToolName(_activeBrushTool);
            if (IsBrushTool(_tool)) Theme.StyleActiveButton(_brushToolButton);
            else Theme.StyleButton(_brushToolButton);
            if (!enabled) _brushToolButton.ForeColor = Color.FromArgb(120, Theme.Text);
            _brushToolButton.Invalidate();
            if (!visible) HideBrushToolFlyout();
        }

        foreach (var (tool, button) in _brushFlyoutButtons)
        {
            var visible = IsToolVisibleForCurrentContext(tool);
            var enabled = visible && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
            button.Visible = visible;
            button.Enabled = enabled;
            if (tool == _activeBrushTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }

        var activeDrawingObject = ActiveDrawingObject();
        foreach (var (id, button) in _drawingObjectTabButtons)
        {
            if (activeDrawingObject is not null && id == activeDrawingObject.Id) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
        }

        LayoutToolPalette();
    }

    private void RefreshToolPairGroup(
        ToolPairGroup group,
        WorkspaceView workspace,
        bool drawingToolsBlocked)
    {
        var groupVisible = group.Tools.Any(IsToolVisibleForCurrentContext);
        var groupEnabled = group.Tools.Any(tool =>
            IsToolVisibleForCurrentContext(tool)
            && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool)));
        if (group.ParentButton is not null)
        {
            group.ParentButton.Visible = groupVisible;
            group.ParentButton.Enabled = groupEnabled;
            group.ParentButton.Icon = ToolIconKind(group.ActiveTool);
            group.ParentButton.Tag = group.ActiveTool;
            group.ParentButton.AccessibleName = ToolPairName(group.ActiveTool);
            if (group.Contains(_tool)) Theme.StyleActiveButton(group.ParentButton);
            else Theme.StyleButton(group.ParentButton);
            if (!groupEnabled) group.ParentButton.ForeColor = Color.FromArgb(120, Theme.Text);
            group.ParentButton.Invalidate();
            if (!groupVisible) HideToolPairFlyout(group);
        }

        foreach (var (tool, button) in group.FlyoutButtons)
        {
            var visible = IsToolVisibleForCurrentContext(tool);
            var enabled = visible && (!drawingToolsBlocked || !IsBasicDrawingOnlyTool(tool));
            button.Visible = visible;
            button.Enabled = enabled;
            if (tool == group.ActiveTool) Theme.StyleActiveButton(button);
            else Theme.StyleButton(button);
            if (!enabled) button.ForeColor = Color.FromArgb(120, Theme.Text);
        }
    }

    private void LayoutToolPalette()
    {
        var tools = _toolPalette;
        if (tools.Parent is not Control stagePanel) return;

        var visibleStageHeight = stagePanel.ClientSize.Height;
        if (_timeline.Visible && ReferenceEquals(_timeline.Parent, this))
        {
            var bodyTop = stagePanel.Parent?.Top ?? 0;
            var timelineTopInStage = _timeline.Top - bodyTop - stagePanel.Top;
            visibleStageHeight = Math.Min(visibleStageHeight, Math.Max(0, timelineTopInStage));
        }

        var availableHeight = Math.Max(Theme.IconButtonSize, visibleStageHeight - tools.Top - 6);
        var paletteControls = tools.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        var oneColumnHeight = tools.Padding.Vertical
            + paletteControls.Sum(control => control.Height + control.Margin.Vertical);
        if (oneColumnHeight <= availableHeight)
        {
            tools.AutoSize = true;
            tools.WrapContents = false;
            return;
        }

        var columns = Math.Clamp((int)Math.Ceiling(oneColumnHeight / (double)availableHeight), 2, 3);
        var rows = (int)Math.Ceiling(paletteControls.Length / (double)columns);
        var rowHeight = paletteControls.Length == 0
            ? Theme.IconButtonSize
            : paletteControls.Max(control => control.Height + control.Margin.Vertical);
        tools.AutoSize = false;
        tools.WrapContents = true;
        tools.Height = Math.Min(availableHeight, tools.Padding.Vertical + rows * rowHeight);
        tools.Width = tools.Padding.Horizontal
            + columns * Theme.IconButtonSize
            + (columns - 1) * Theme.GapXs;
    }

    private sealed class DrawingObjectNameDialog : ModernDialogForm
    {
        private readonly TextBox _input = new();

        private DrawingObjectNameDialog(string title, string label, string initialValue)
            : base(title, new Size(440, 224))
        {
            var prompt = new Label
            {
                Text = label,
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            DialogContent.Controls.Add(prompt);

            _input.Dock = DockStyle.Top;
            _input.Height = 30;
            _input.Margin = new Padding(0, 8, 0, 0);
            _input.Text = initialValue;
            _input.SelectAll();
            Theme.StyleTextBox(_input);
            DialogContent.Controls.Add(_input);
            _input.BringToFront();

            var save = AddDialogAction(
                "Rename",
                DialogResult.OK,
                DialogActionStyle.Primary,
                () => !string.IsNullOrWhiteSpace(_input.Text));
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);

            AcceptButton = save;
            CancelButton = cancel;
            Shown += (_, _) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
            UiLocalization.Watch(this);
        }

        public static bool TryAsk(IWin32Window owner, string title, string label, string initialValue, out string value)
        {
            using var dialog = new DrawingObjectNameDialog(title, label, initialValue);
            var accepted = dialog.ShowDialog(owner) == DialogResult.OK;
            value = accepted ? dialog._input.Text.Trim() : string.Empty;
            return accepted;
        }
    }

    private static Label MetricLabel(string text, int width) => new() { Text = text, Left = 8, Top = 6, Width = width, Height = 22, ForeColor = Theme.Muted, BackColor = Theme.Top, Font = Theme.UiFont(8.8f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static CheckBox EraserTargetOption(string text, string accessibleName) => new()
    {
        Text = text,
        AccessibleName = accessibleName,
        AutoSize = true,
        Height = 28,
        ForeColor = Theme.Text,
        BackColor = Theme.Top,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.UiFont(),
        Margin = new Padding(0, 2, 10, 0)
    };
    private static ToolStripStatusLabel StatusLabel(string text) => new() { Text = text, ForeColor = Theme.Muted, Spring = false, Margin = new Padding(0, 0, 10, 0) };
    private static ToolStripStatusLabel StatusSeparator() => new() { Text = "|", ForeColor = Theme.Border, Margin = new Padding(0, 0, 10, 0) };
    private static bool IsModuleHotReloadEnabled() => Environment.GetEnvironmentVariable("V2D_DEV_HOT_RELOAD") == "1";
    private static Label InspectorLabel(string text) => new() { Text = text, Height = 26, ForeColor = Theme.Text, BackColor = Theme.Panel, Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Muted,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(0, 3, 8, 3)
    };
    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool ContainsFocusedEditor(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is TextBoxBase or NumericUpDown or ComboBox) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedEditor(child)) return true;
        }

        return false;
    }

    private static bool ContainsFocusedTextEditor(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is NumericUpDown) return false;
        if (control is TextBoxBase or ComboBox) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedTextEditor(child)) return true;
        }

        return false;
    }

    private static bool ContainsFocusedInteractiveControl(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (IsCanvasShortcutBlockingInteractiveControl(control)) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedInteractiveControl(child)) return true;
        }

        return false;
    }

    internal static bool IsCanvasShortcutBlockingInteractiveControl(Control control)
        => control is ButtonBase or ListControl or TreeView or ListView or ModernSlider;

    private static bool ContainsFocusedButton(Control control)
    {
        if (!control.ContainsFocus) return false;
        if (control is ButtonBase) return true;
        foreach (Control child in control.Controls)
        {
            if (ContainsFocusedButton(child)) return true;
        }

        return false;
    }
}
