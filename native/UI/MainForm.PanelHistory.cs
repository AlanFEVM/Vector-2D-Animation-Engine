namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record PanelCommandHistory(VectorProject Project, Action Undo, Action Redo, long MemoryBytes = 0);
    private readonly Stack<PanelCommandHistory> _panelCommandUndo = new();

    private void PushPanelHistory(PanelCommandHistory entry)
    {
        _panelCommandUndo.Push(entry);
        RecordUndoSequence(entry);
        long bytes = 0;
        var retained = _panelCommandUndo.TakeWhile((item, index) =>
        {
            bytes += item.MemoryBytes;
            return index == 0 || index < MaxUndoSnapshots && bytes <= MaxUndoSnapshotBytes;
        }).Reverse().ToArray();
        _panelCommandUndo.Clear();
        foreach (var item in retained) _panelCommandUndo.Push(item);
        PruneUndoSequenceEntries();
        MarkProjectDirty();
    }

    private bool TryUndoPanelCommand(long latestSequence)
    {
        if (!_panelCommandUndo.TryPeek(out var entry) || !ReferenceEquals(entry.Project, _project)
            || UndoSequence(entry) < latestSequence) return false;
        _panelCommandUndo.Pop();
        _undoSequenceByEntry.Remove(entry);
        entry.Undo();
        CaptureDocumentRedo(entry);
        return true;
    }

    private void ExecuteLibraryPanelCommand(Action command, bool includeContent = false)
    {
        var project = _project;
        var before = project.CaptureLibraryHistory(includeContent);
        try
        {
            command();
            var after = project.CaptureLibraryHistory(includeContent);
            void Restore(VectorProject.LibraryHistoryState state)
            {
                project.RestoreLibraryHistory(state);
                _activeDrawingObjectIndex = Math.Clamp(_activeDrawingObjectIndex, 0, _drawingObjects.Count - 1);
                if (IsSceneWorkspaceSelected) BindSceneEditStageCore(resetView: false, preserveHistory: true);
                else BindActiveDrawingObjectSceneCore(resetView: false, preserveHistory: true);
                BuildDrawingObjectTabs();
                RefreshSharedAssets();
            }
            PushPanelHistory(new(project, () => Restore(before), () => Restore(after),
                before.Content.Sum(item => item.Snapshot.EstimateMemoryBytes())
                    + after.Content.Sum(item => item.Snapshot.EstimateMemoryBytes())));
            _libraryVaultPanel.RefreshProjectObjects();
            _hierarchyPanel.RefreshScene(force: true);
            UpdateInspector();
        }
        catch (Exception exception)
        {
            project.RestoreLibraryHistory(before);
            RefreshSharedAssets();
            ShowProjectFileError("The library asset could not be changed.", exception);
        }
    }
}
