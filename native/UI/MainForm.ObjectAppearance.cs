namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void PrepareObjectMaterialBinding(bool bind)
    {
        if (bind) _drawingToolPaint ??= _materialEditor.CapturePaintState();
        else RestoreDrawingToolPaint();
    }

    private void RestoreDrawingToolPaint()
    {
        if (_drawingToolPaint is not { } paint) return;
        _drawingToolPaint = null;
        _materialEditor.SetTextObjectMode(false);
        _materialEditor.RestorePaintState(paint);
    }

    private void UpdateObjectAppearancePanel(int[] selection, bool bound)
    {
        _objectAppearancePanel.Visible = bound;
        if (!bound) return;
        var objectIndex = selection.Contains(_selectedObject) ? _selectedObject : selection[0];
        BindObjectMaterial(objectIndex);
        _objectAppearancePanel.Enabled = CanEditInspectorObjectTransform(selection);
        var elementSelection = _selectedElements.Count > 0;
        var fillAvailable = selection.Any(index => IsFillShape(_scene.ShapeKind[index])
                || _scene.ShapeKind[index] == ShapeKind.BrushStroke)
            && (!elementSelection || _selectedElements.Any(hit => hit.Key.Kind == DrawingElementKind.Fill));
        var strokeAvailable = selection.Any(index => _scene.ShapeKind[index] is not (ShapeKind.BrushStroke or ShapeKind.Text))
            && (!elementSelection || _selectedElements.Any(hit =>
                hit.Key.Kind is DrawingElementKind.Stroke or DrawingElementKind.BoundaryStroke));
        var mixed = selection.Any(index => _scene.Argb[index] != _scene.Argb[objectIndex]
            || _scene.StrokeArgb[index] != _scene.StrokeArgb[objectIndex]
            || Math.Abs(_scene.Stroke[index] - _scene.Stroke[objectIndex]) > 0.001f
            || _scene.GetGradientKind(index) != _scene.GetGradientKind(objectIndex));
        _objectAppearancePanel.SetMaterial(_materialEditor, fillAvailable, strokeAvailable, mixed);
    }

    private void BindObjectMaterial(int objectIndex)
    {
        var fill = Color.FromArgb(_scene.Argb[objectIndex]);
        var stroke = _scene.StrokeArgb.Length > objectIndex ? Color.FromArgb(_scene.StrokeArgb[objectIndex]) : ActiveStrokeColor();
        var strokePoints = VectorUnits.UnitsToStrokePoints(_scene.Stroke[objectIndex]);
        var shape = _scene.ShapeKind[objectIndex];
        var selectedKind = _selectedElement.IsValid && _selectedElement.Key.ObjectIndex == objectIndex
            ? _selectedElement.Key.Kind
            : DrawingElementKind.None;
        ApplyMaterialTargetPreference(selectedKind);
        var strokeMaterialTarget = _materialEditor.EditingStroke;
        var gradientAppliesToTarget = CanEditGradientForMaterialTarget(shape, strokeMaterialTarget);

        var gradientStops = gradientAppliesToTarget && _scene.HasGradient(objectIndex)
            ? _scene.GetGradientStops(objectIndex)
            : [];
        var materialOpacity = gradientStops.Length > 0
            ? Color.FromArgb(gradientStops[0].Argb).A / 255f
            : (strokeMaterialTarget ? stroke : fill).A / 255f;
        if (shape is ShapeKind.Freeform or ShapeKind.Line)
        {
            _materialEditor.SetMaterial(_materialEditor.Fill, stroke, strokePoints, materialOpacity);
        }
        else if (shape == ShapeKind.BrushStroke)
        {
            _materialEditor.SetMaterial(fill, _materialEditor.Stroke, strokePoints, materialOpacity);
        }
        else
        {
            _materialEditor.SetMaterial(fill, stroke, strokePoints, materialOpacity);
        }

        foreach (var strokeTarget in new[] { false, true })
        {
            var hasGradient = CanEditGradientForMaterialTarget(shape, strokeTarget) && _scene.HasGradient(objectIndex);
            var targetColor = strokeTarget ? stroke : fill;
            _materialEditor.SetObjectGradient(strokeTarget,
                hasGradient ? _scene.GetGradientKind(objectIndex) : GradientKind.Solid,
                hasGradient ? _scene.GetGradientStops(objectIndex)
                    : [new GradientStop(0, targetColor), new GradientStop(1, targetColor)]);
        }
    }
}
