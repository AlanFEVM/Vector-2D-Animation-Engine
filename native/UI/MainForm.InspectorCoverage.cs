namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly ObjectGeometryPanel _objectGeometryPanel = new();

    private void BindInspectorCoverageEvents()
    {
        _toolContextPanel.OptionChanged += (kind, value) =>
        {
            if (!CanActivateTool(_tool)) return;
            var enabled = value != 0;
            switch (kind)
            {
                case ToolOptionKind.KeepRatio: _drawSettings.KeepAspectRatio = enabled; break;
                case ToolOptionKind.EraseStroke: _drawSettings.EraseLines = enabled; break;
                case ToolOptionKind.EraseFill: _drawSettings.EraseFills = enabled; break;
                case ToolOptionKind.Snap: _drawSettings.SnapEnabled = enabled; break;
                case ToolOptionKind.Grid: _drawSettings.SnapToGrid = enabled; break;
                case ToolOptionKind.Objects: _drawSettings.SnapToObjects = enabled; break;
                case ToolOptionKind.Angle: _drawSettings.AngleSnapEnabled = enabled; break;
                case ToolOptionKind.AngleStep: _drawSettings.AngleSnapDegrees = (float)value; break;
            }
            _drawSettings.NotifyChanged();
        };
        _toolContextPanel.RelatedPageRequested += (_, _) => RevealInspectorCategory(
            ToolContextPanel.UsesObjectPage(_tool) ? InspectorCategory.None : InspectorCategory.Color,
            "explicit tool panel navigation");
        _toolContextPanel.ZoomRequested += ZoomActiveView;
        _objectGeometryPanel.AppearanceRequested += (_, _) =>
            RevealInspectorCategory(InspectorCategory.Color, "explicit geometry panel navigation");
        _objectGeometryPanel.ValueCommitted += ApplyInspectorObjectTransform;
    }

    private void UpdateObjectGeometryPanel(int[] selection)
    {
        var instances = SelectedSceneInstances();
        var hasGeometry = TryGetInspectorObjectGeometry(selection, out var geometry, out var rotation);
        var show = (IsDrawingInspectorContext() && selection.Length > 0 || instances.Count > 0)
            && hasGeometry;
        _objectGeometryPanel.Visible = show;
        if (!show) return;
        var imported = selection.Any(index => _scene.ShapeKind[index] is ShapeKind.ImportedSvg or ShapeKind.Bitmap);
        var source = instances.Count > 0 ? string.Join(" / ", instances.Select(instance => instance.Name))
            : selection.Length == 1 && _scene.TryGetImportedSvgName(selection[0], out var svgName)
            ? svgName
            : string.Join(" / ", selection.Select(index => _scene.ShapeKind[index]).Distinct()
                .Select(kind => UiLocalization.T(kind.ToString())));
        var token = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_scene)}:{_frame}:"
            + (instances.Count > 0 ? string.Join(",", instances.Select(instance => instance.Id)) : string.Join(",", selection));
        _objectGeometryPanel.SetGeometry(geometry, source, appearanceAvailable: !imported && instances.Count == 0,
            rotationDegrees: rotation, editable: CanEditInspectorObjectTransform(selection),
            group: instances.Count > 0 ? instances.Count > 1 : selection.Length > 1, bindingToken: token,
            relativeRotation: instances.Count == 0 && selection.Length == 1 && InspectorUsesBakedGeometry(selection[0]),
            heightEditable: instances.Count > 0 || selection.Length != 1 || _scene.ShapeKind[selection[0]] != ShapeKind.Line,
            mixedSelection: instances.Count > 0 && selection.Length > 0);
    }
}
