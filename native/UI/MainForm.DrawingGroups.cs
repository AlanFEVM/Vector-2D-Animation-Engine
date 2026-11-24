namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool CanGroupStageSelection()
    {
        if (IsSceneMaskEditing() || IsOpenDrawingScope || SelectedSceneInstances().Count > 0 || !CanConvertStageSelectionToSymbol()) return false;
        var targets = SelectedActiveDrawingObjectIndices().ToHashSet();
        var first = targets.First();
        var ordered = Enumerable.Range(0, _scene.ObjectCount)
            .Where(index => _scene.ObjectLayer[index] == _scene.ObjectLayer[first]
                && _scene.IsObjectActive(index, _frame))
            .OrderBy(index => _scene.ObjectOrder[index])
            .ThenBy(index => _scene.ObjectSubOrder[index]).ToArray();
        var start = Array.FindIndex(ordered, targets.Contains);
        return start >= 0 && ordered.Skip(start).All(targets.Contains);
    }

    private bool GroupStageSelection()
    {
        if (!CanGroupStageSelection()) return false;
        return ConvertSelectedDrawingObjectsToContainer(asGroup: true);
    }

    private HashSet<string> CapturePrivateGroupIds() => _drawingObjects
        .Where(item => item.IsGroup).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

    private void RecordCreatedPrivateGroups(ISet<string> previous)
    {
        var created = _drawingObjects.Where(item => item.IsGroup && !previous.Contains(item.Id))
            .Select(item => item.Id).ToArray();
        if (created.Length == 0) return;
        if (IsSceneCompositionContext())
        {
            var entry = _sceneTimelineUndoStack.Pop();
            _undoSequenceByEntry.Remove(entry);
            var replacement = entry with { CreatedDrawingObjectIds = entry.CreatedDrawingObjectIds.Concat(created).Distinct().ToArray() };
            _sceneTimelineUndoStack.Push(replacement);
            RecordUndoSequence(replacement);
        }
        else
        {
            var entry = _undoStack.Pop();
            _undoSequenceByEntry.Remove(entry);
            var replacement = entry with { CreatedDrawingObjectIds = entry.CreatedDrawingObjectIds.Concat(created).Distinct().ToArray() };
            _undoStack.Push(replacement);
            RecordUndoSequence(replacement);
        }
    }
}
