namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void ApplySelectedDrawingObjectBlendMode(LayerBlendMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        CompleteInstanceAppearanceEdit();
        BeginInstanceAppearanceEdit();
        var session = _instanceAppearanceEditSession;
        if (session is null) return;
        foreach (var instance in ActiveEditableInstances().Where(instance =>
                     session.SelectedInstanceIds.Contains(instance.Id, StringComparer.Ordinal)))
        {
            var state = instance.EvaluateState(ResolveInstanceStateEditFrameForCurrentContext(instance));
            if (state.BlendMode == mode) continue;
            var editFrame = PrepareInstanceStateTimelineEdit(instance);
            session.Changed |= instance.SetStateAtFrame(editFrame, state with { BlendMode = mode });
        }
        if (session.Changed)
        {
            _sceneInstanceTimelineDirty = true;
            QueueInstanceAppearancePreview();
        }
        CompleteInstanceAppearanceEdit();
    }

    private void ApplySelectedDrawingObjectFilters(SymbolFiltersChangedEventArgs change)
    {
        BeginInstanceAppearanceEdit();
        var session = _instanceAppearanceEditSession;
        if (session is null) return;

        var changed = false;
        foreach (var instance in ActiveEditableInstances().Where(instance =>
                     session.SelectedInstanceIds.Contains(instance.Id, StringComparer.Ordinal)))
        {
            var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
            var state = instance.EvaluateState(editFrame);
            var filters = change.Update(state.Filters);
            if (!filters.IsValid || filters == state.Filters) continue;
            editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed |= instance.SetStateAtFrame(editFrame, state with { Filters = filters });
        }
        if (!changed) return;
        session.Changed = true;
        _sceneInstanceTimelineDirty = true;
        QueueInstanceAppearancePreview();
    }
}
