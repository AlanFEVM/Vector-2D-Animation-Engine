namespace VectorAnimationEngine;

/// <summary>
/// The inspector suggests which material target (fill or stroke) a selection is about,
/// but a suggestion must never undo the target the user picked by hand: clicking "Stroke"
/// and then any refresh used to snap the editor back to fill. The suggestion is re-applied
/// only when the selection identity actually changes.
/// </summary>
internal sealed partial class MainForm
{
    private bool _materialTargetChosenByUser;
    private (int SelectionCount, int ElementObject, DrawingElementKind ElementKind, int SelectedObject, ToolMode Tool)
        _materialTargetSignature;

    private void ApplyMaterialTargetPreference(DrawingElementKind kind)
    {
        var signature = (
            _selectedObjects.Count,
            _selectedElement.Key.ObjectIndex,
            _selectedElement.Key.Kind,
            _selectedObject,
            _tool);
        if (signature != _materialTargetSignature)
        {
            _materialTargetSignature = signature;
            _materialTargetChosenByUser = false;
        }
        if (_materialTargetChosenByUser) return;
        var objectIndex = _selectedElement.IsValid ? _selectedElement.Key.ObjectIndex : _selectedObject;
        if ((uint)objectIndex >= (uint)_scene.ObjectCount) return;
        _materialEditor.SetGradientPreviewTarget(
            ShouldPreferStrokeMaterial(kind, _scene.ShapeKind[objectIndex]));
    }
}
