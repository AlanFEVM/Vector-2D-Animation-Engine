namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record DocumentRedoEntry(VectorProject Project, object Scope, object Before, object After,
        (DrawingObjectDefinition Definition, int Index)[] CreatedDefinitions);
    private readonly Stack<DocumentRedoEntry> _documentRedo = new();
    private bool _replayingDocumentHistory;
    private bool _applyingDocumentRedo;

    private void CaptureDocumentRedo(object before)
    {
        if (_applyingDocumentRedo) return;
        object after;
        object scope = _scene;
        var createdIds = new List<string>();
        if (before is DrawingUndoEntry drawing)
        {
            var owner = drawing.DrawingObject ?? ActiveDrawingObject();
            after = new DrawingUndoEntry(_scene.CreateSnapshot(), owner,
                drawing.InstanceSnapshot is not null ? owner?.CreateInstanceSnapshot() : null,
                drawing.SnapPointSnapshot is not null
                    ? (drawing.SnapPointOwner ?? (IEditableSnapPointOwner?)owner)?.CreateSnapPointSnapshot() : null,
                _frame, _timeline.CaptureSelectionSnapshot(), drawing.DrawingObjectName is not null ? owner?.Name : null,
                SnapPointOwner: drawing.SnapPointOwner ?? owner);
            if (drawing.CreatedDrawingObjectId is { } drawingId) createdIds.Add(drawingId);
            createdIds.AddRange(drawing.CreatedDrawingObjectIds);
        }
        else if (before is SceneTimelineUndoEntry timeline)
        {
            var scene = timeline.Scene;
            scope = scene;
            after = new SceneTimelineUndoEntry(scene, scene.Timeline.CreateSnapshot(),
                timeline.LayerSnapshot is not null ? scene.CreateLayerSnapshot() : null,
                timeline.InstanceSnapshot is not null ? scene.CreateInstanceSnapshot() : null,
                _frame, _timeline.CaptureSelectionSnapshot(),
                LightSnapshot: timeline.LightSnapshot is not null ? scene.Lights.Select(light => light.Clone()).ToArray() : null,
                SelectedLightId: _selectedSceneLightId,
                OnionSkinState: timeline.OnionSkinState is not null ? scene.CreateOnionSkinState() : null,
                ShotSnapshot: timeline.ShotSnapshot is not null ? scene.CreateShotSnapshot() : null);
            if (timeline.CreatedDrawingObjectId is { } drawingId) createdIds.Add(drawingId);
            createdIds.AddRange(timeline.CreatedDrawingObjectIds);
        }
        else if (before is SharedAssetUndoEntry asset)
        {
            FreezeSharedAssetUndoIds(asset);
            scope = _project;
            after = new SharedAssetUndoEntry(_project, _project.CreateExternalSvgAssetRestartSnapshots(),
                _project.CreateImageAssetRestartSnapshots(), asset.Scenes.Select(item =>
                {
                    var scene = item.DrawingId.Length > 0
                        ? _project.DrawingObjects.First(drawing => drawing.Id == item.DrawingId).Scene
                        : _project.Scenes.First(owner => owner.Id == item.SceneId).FindMaskScene(item.LayerId)!;
                    return item with { Snapshot = scene.CreateSnapshot(), OuterTimeline = item.OuterTimeline is not null
                        ? _project.Scenes.First(owner => owner.Id == item.SceneId).Timeline.CreateSnapshot() : null };
                }).ToArray(), asset.TargetAssetId)
                { SvgIds = asset.SvgIds!.ToHashSet(StringComparer.Ordinal), ImageIds = asset.ImageIds!.ToHashSet(StringComparer.Ordinal) };
        }
        else if (before is CanvasSettingsUndo canvas) { scope = canvas.Scene; after = canvas; }
        else return;
        var definitions = _project.DrawingObjects.Select((definition, index) => (Definition: definition, Index: index))
            .Where(item => createdIds.Contains(item.Definition.Id)).ToArray();
        _documentRedo.Push(new(_project, scope, before, after, definitions));
        long bytes = 0;
        var retained = _documentRedo.TakeWhile((entry, index) =>
        {
            if (entry.After is DrawingUndoEntry drawing) bytes += drawing.Snapshot.EstimateMemoryBytes();
            if (entry.After is SceneTimelineUndoEntry timeline && timeline.LayerSnapshot is not null) bytes += 4096;
            return index == 0 || index < MaxUndoSnapshots && bytes <= MaxUndoSnapshotBytes;
        }).Reverse().ToArray();
        _documentRedo.Clear();
        foreach (var entry in retained) _documentRedo.Push(entry);
    }

    private bool UndoLastEdit()
    {
        _duplicateSelectionKey = null;
        var previous = _replayingDocumentHistory;
        _replayingDocumentHistory = true;
        try { return UndoLastEditCore(); }
        finally { _replayingDocumentHistory = previous; }
    }

    private bool RedoLastEdit()
    {
        if (HasActiveCanvasPointerInteraction() || !_documentRedo.TryPeek(out var redo)
            || !ReferenceEquals(redo.Project, _project)) return false;
        if (redo.Before is DrawingUndoEntry && (IsSceneCompositionContext() || !ReferenceEquals(redo.Scope, _scene))) return false;
        if (redo.Before is SceneTimelineUndoEntry or CanvasSettingsUndo && !ReferenceEquals(redo.Scope, ActiveScene())) return false;
        var previous = _replayingDocumentHistory;
        _replayingDocumentHistory = _applyingDocumentRedo = true;
        try
        {
            foreach (var (definition, index) in redo.CreatedDefinitions)
                if (!_project.TryRestoreHistoryDrawingObject(definition, index)) return false;
            if (redo.Before is CanvasSettingsUndo)
            {
                if (!RedoCanvasSettings()) return false;
            }
            else
            {
                // The existing undo restore paths also restore the captured post-edit state.
                PushHistoryEntry(redo.After);
                if (!UndoLastEditCore()) return false;
                PushHistoryEntry(redo.Before);
            }
            _documentRedo.Pop();
            RefreshDrawingObjectAssetPresentation();
            return true;
        }
        finally { _replayingDocumentHistory = previous; _applyingDocumentRedo = false; }
    }

    private void PushHistoryEntry(object entry)
    {
        if (entry is DrawingUndoEntry drawing) _undoStack.Push(drawing);
        else if (entry is SceneTimelineUndoEntry timeline) _sceneTimelineUndoStack.Push(timeline);
        else if (entry is SharedAssetUndoEntry asset) _sharedAssetUndoStack.Push(asset);
        RecordUndoSequence(entry);
    }
}
