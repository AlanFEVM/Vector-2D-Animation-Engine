namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record SymbolReferenceHistory(
        ITimelineContext Owner, DrawingObjectInstanceDefinition[] Instances, AnimationTimelineSnapshot Timeline);

    private sealed record SymbolDeletionHistory(
        VectorProject Project, DrawingObjectDefinition Symbol, int Index, bool Present,
        SymbolReferenceHistory[] References, string ActiveSymbolId, bool SceneContext, PointF? Anchor = null);

    private readonly Stack<SymbolDeletionHistory> _symbolDeletionUndo = new();

    private void PushSymbolDeletionHistory(SymbolDeletionHistory state)
    {
        _symbolDeletionUndo.Push(state);
        RecordUndoSequence(state);
        var retained = _symbolDeletionUndo.Take(MaxUndoSnapshots).Reverse().ToArray();
        _symbolDeletionUndo.Clear();
        foreach (var item in retained) _symbolDeletionUndo.Push(item);
        PruneUndoSequenceEntries();
    }

    private SymbolDeletionHistory CaptureSymbolDeletionHistory(DrawingObjectDefinition symbol, int index,
        IEnumerable<ITimelineContext>? owners = null, bool captureAnchor = false)
    {
        owners ??= _project.Scenes.Where(scene => scene.Instances.Any(instance => instance.DrawingObjectId == symbol.Id))
            .Cast<ITimelineContext>().Concat(_drawingObjects.Where(drawing =>
                drawing.Instances.Any(instance => instance.DrawingObjectId == symbol.Id)));
        var references = owners.Select(owner => new SymbolReferenceHistory(owner,
            owner is SceneDefinition scene ? scene.CreateInstanceSnapshot()
                : ((DrawingObjectDefinition)owner).CreateInstanceSnapshot(), owner.Timeline.CreateSnapshot())).ToArray();
        return new(_project, symbol, index, _drawingObjects.Contains(symbol), references,
            ActiveDrawingObject()?.Id ?? "", IsSceneBuildingContext(), captureAnchor ? symbol.Anchor : null);
    }

    private void RestoreSymbolDeletionHistory(SymbolDeletionHistory state)
    {
        if (state.Present)
        {
            if (!_project.TryRestoreHistoryDrawingObject(state.Symbol, state.Index))
                throw new InvalidOperationException("The deleted symbol could not be restored.");
        }
        else if (_drawingObjects.Contains(state.Symbol)
            && !_project.TryRemoveDrawingObject(state.Symbol.Id, out _))
            throw new InvalidOperationException("The symbol could not be deleted again.");
        if (state.Anchor is { } anchor && !_project.TrySetDrawingObjectAnchor(state.Symbol.Id, anchor))
            throw new InvalidOperationException("The shared symbol anchor could not be restored.");
        foreach (var reference in state.References)
        {
            if (reference.Owner is SceneDefinition scene) scene.RestoreInstanceSnapshot(reference.Instances);
            else ((DrawingObjectDefinition)reference.Owner).RestoreInstanceSnapshot(reference.Instances);
            reference.Owner.Timeline.RestoreSnapshot(reference.Timeline);
            reference.Owner.SynchronizeTimelineTracks();
        }
        var activeIndex = Array.FindIndex(_drawingObjects.ToArray(), drawing => drawing.Id == state.ActiveSymbolId);
        _activeDrawingObjectIndex = activeIndex >= 0 ? activeIndex : Math.Clamp(state.Index, 0, _drawingObjects.Count - 1);
        if (state.SceneContext) BindSceneEditStageCore(resetView: false, preserveHistory: true);
        else BindActiveDrawingObjectSceneCore(resetView: false, preserveHistory: true);
        BuildDrawingObjectTabs();
        RefreshDrawingObjectAssetPresentation();
        MarkProjectDirty();
    }
}
