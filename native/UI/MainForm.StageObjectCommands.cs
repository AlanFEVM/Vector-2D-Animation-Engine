namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private IReadOnlyList<DrawingObjectInstanceDefinition> StageOwnedInstances() => IsSceneCompositionContext()
        ? ActiveScene()?.Instances ?? [] : ActiveDrawingObject()?.Instances ?? [];

    private bool ChangeStageObjectVisibility(bool showAll = false, bool isolate = false, bool? locked = null)
    {
        if (_playing || IsShotDirectorContext() || HasActiveCanvasPointerInteraction()) return false;
        var targets = IsSceneCompositionContext() ? [] : SelectedActiveDrawingObjectIndices();
        var selectedIds = _selectedSceneInstanceIds.ToHashSet(StringComparer.Ordinal);
        if (!showAll && locked != false && targets.Length == 0 && selectedIds.Count == 0) return false;
        var owner = ActiveDrawingObject();
        var activeScene = ActiveScene();
        var sceneSnapshot = !IsSceneCompositionContext() ? _scene.CreateSnapshot() : null;
        var instanceSnapshot = IsSceneCompositionContext() ? activeScene?.CreateInstanceSnapshot() : owner?.CreateInstanceSnapshot();
        var timelineSnapshot = IsSceneCompositionContext() ? activeScene?.Timeline.CreateSnapshot() : null;
        var changed = false;
        if (!IsSceneCompositionContext())
        {
            var selected = targets.ToHashSet();
            for (var i = 0; i < _scene.ObjectCount; i++)
            {
                if (locked is { } lockValue)
                {
                    if (lockValue && !selected.Contains(i)) continue;
                    changed |= _scene.ObjectLocked[i] != lockValue;
                    _scene.ObjectLocked[i] = lockValue;
                }
                else if (showAll || selected.Contains(i) || isolate && _scene.IsObjectActive(i, _frame))
                {
                    var hidden = !showAll && (!isolate || !selected.Contains(i));
                    changed |= _scene.ObjectHidden[i] != hidden;
                    _scene.ObjectHidden[i] = hidden;
                }
            }
        }
        foreach (var instance in StageOwnedInstances())
        {
            if (locked is { } lockValue)
            {
                if (lockValue && !selectedIds.Contains(instance.Id)) continue;
                changed |= instance.EditorLocked != lockValue;
                instance.EditorLocked = lockValue;
            }
            else if (showAll || isolate || selectedIds.Contains(instance.Id))
            {
                var hidden = !showAll && (!isolate || !selectedIds.Contains(instance.Id));
                changed |= instance.EditorHidden != hidden;
                instance.EditorHidden = hidden;
            }
        }
        if (!changed) return true;
        if (IsSceneCompositionContext() && activeScene is not null && timelineSnapshot is not null)
            PushSceneTimelineUndo(activeScene, timelineSnapshot, instanceSnapshot: instanceSnapshot);
        else if (sceneSnapshot is not null)
        {
            _scene.CompleteDeferredBuild();
            PushUndoSnapshot(sceneSnapshot, owner, instanceSnapshot);
        }
        ClearSelection();
        InvalidateSceneCompositionCache();
        RebuildEditableInstanceComposition();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        UpdateInspector();
        ResetSmartSnapping();
        _stage.Invalidate();
        return true;
    }

    private void BuildStageObjectCommandMenu()
    {
        var root = new ToolStripMenuItem("Object selection") { DropDown = new AnimatedContextMenuStrip() };
        Add("Hide Selected", ShortcutCommandIds.HideSelected, () => ChangeStageObjectVisibility());
        Add("Show All Objects", ShortcutCommandIds.ShowAll, () => ChangeStageObjectVisibility(showAll: true));
        Add("Isolate Selected", ShortcutCommandIds.IsolateSelected, () => ChangeStageObjectVisibility(isolate: true));
        Add("Lock Selected", ShortcutCommandIds.LockSelected, () => ChangeStageObjectVisibility(locked: true));
        Add("Unlock All Objects", ShortcutCommandIds.UnlockAll, () => ChangeStageObjectVisibility(locked: false));
        var same = new ToolStripMenuItem("Select Same") { Tag = "stage.select-same-menu", DropDown = new AnimatedContextMenuStrip() };
        var labels = new[] { "Same Fill", "Same Stroke", "Same Asset Tags" };
        for (var i = 0; i < labels.Length; i++)
        {
            var kind = i;
            var item = new ToolStripMenuItem(labels[i]);
            item.Click += (_, _) => SelectSameStageObjects(kind);
            same.DropDownItems.Add(item);
        }
        _stageContextMenu.Items.Add(same);
        _stageContextMenu.Items.Add(root);
        void Add(string label, string id, Action action)
        {
            var item = new ToolStripMenuItem(label) { Tag = id };
            item.Click += (_, _) => action();
            root.DropDownItems.Add(item);
        }
    }
}
