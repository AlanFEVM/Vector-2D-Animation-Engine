namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private VectorScene.ObjectAppearance? _appearanceClipboard;
    private InstanceFrameState? _instanceAppearanceClipboard;

    private bool CopyStageAppearance()
    {
        if (SelectedSceneInstance() is { } instance)
        { _instanceAppearanceClipboard = instance.EvaluateState(_frame); _appearanceClipboard = null; return true; }
        if ((uint)_selectedObject >= _scene.ObjectCount || IsSceneCompositionContext()) return false;
        _appearanceClipboard = _scene.CaptureObjectAppearance(_selectedObject);
        _instanceAppearanceClipboard = null;
        return true;
    }

    private bool PasteStageAppearance()
    {
        if (_appearanceClipboard is { } appearance && !IsSceneCompositionContext())
            return EditStageSelection(targets => { foreach (var target in targets) _scene.ApplyObjectAppearance(target, appearance); }, null);
        if (_instanceAppearanceClipboard is not { } copied || SelectedSceneInstances().Count == 0) return false;
        return EditStageSelection(null, (instance, frame) =>
        {
            var state = instance.EvaluateState(frame);
            instance.SetStateAtFrame(frame, state with { Alpha = copied.Alpha, TintArgb = copied.TintArgb,
                Filters = copied.Filters, BlendMode = copied.BlendMode });
        }, rebuildTopology: false);
    }

    private void ShowSelectSameMenu()
    {
        var root = _stageContextMenu.Items.OfType<ToolStripMenuItem>().First(item => Equals(item.Tag, "stage.select-same-menu"));
        root.DropDown.Show(_stage, _stage.PointToClient(Cursor.Position));
    }

    private bool SelectSameStageObjects(int kind)
    {
        if (kind == 2)
        {
            var source = SelectedSceneInstance();
            var definition = _drawingObjects.FirstOrDefault(d => d.Id == source?.DrawingObjectId);
            if (definition is null || definition.AssetTagIds.Count == 0) return false;
            var tags = definition.AssetTagIds.ToHashSet(StringComparer.Ordinal);
            var selected = FindVisibleCompositionInstances(ActiveInstanceCompositionResult(), ActiveEditableInstances())
                .Where(instance => _drawingObjects.FirstOrDefault(d => d.Id == instance.DrawingObjectId) is { } d
                    && tags.SetEquals(d.AssetTagIds)).ToArray();
            SetSceneInstanceSelection(selected, source);
            UpdateInspector();
            return true;
        }
        if (IsSceneCompositionContext() || (uint)_selectedObject >= _scene.ObjectCount) return false;
        var sourceIndex = _selectedObject;
        var selectedObjects = FindSelectableObjectIndices(_scene, _frame).Where(index => kind == 0
            ? _scene.SameFillAppearance(sourceIndex, index) : _scene.SameStrokeAppearance(sourceIndex, index)).ToArray();
        SetSelection(selectedObjects);
        UpdateInspector();
        return true;
    }

    private bool EnterSelectedStageSymbol()
    {
        if (SelectedSceneInstance() is not { } instance) return false;
        var owner = new SceneCompositionObjectOwner(instance.Id, instance.DrawingObjectId, instance.Id);
        if (BeginSceneSymbolEditing(owner)) return true;
        var parent = ActiveDrawingObject();
        if (!OpenDrawingObjectEditor(instance.DrawingObjectId)) return false;
        if (parent is not null) _basicSymbolParents.Push((parent.Id, instance.DrawingObjectId));
        return true;
    }

    private readonly Stack<(string ParentId, string ChildId)> _basicSymbolParents = new();
    private bool ReturnToStageParentSymbol()
    {
        if (IsSymbolEditing) return ExitParentSymbolEditing();
        if (!_basicSymbolParents.TryPeek(out var parent)) return false;
        if (ActiveDrawingObject()?.Id != parent.ChildId) { _basicSymbolParents.Clear(); return false; }
        _basicSymbolParents.Pop();
        return OpenDrawingObjectEditor(parent.ParentId);
    }
}
