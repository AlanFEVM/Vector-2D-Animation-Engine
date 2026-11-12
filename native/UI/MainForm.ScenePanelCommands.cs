namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record ScenePanelHistory(DrawingUndoEntry[] Drawing, SceneTimelineUndoEntry[] Timeline,
        CanvasSettingsUndo[] Canvas, int Frame)
    {
        public IEnumerable<object> Entries => Drawing.Cast<object>().Concat(Timeline).Concat(Canvas);
    }
    private readonly Dictionary<SceneDefinition, ScenePanelHistory> _scenePanelHistories = new();

    private void SaveScenePanelHistory(SceneDefinition scene)
    {
        _scenePanelHistories[scene] = new(_undoStack.ToArray(), _sceneTimelineUndoStack.ToArray(),
            _canvasSettingsUndo.ToArray(), _frame);
        while (!_replayingDocumentHistory && (_scenePanelHistories.Count > 8 || _scenePanelHistories.Values
            .SelectMany(history => history.Drawing).Sum(entry => entry.MemoryBytes) > MaxUndoSnapshotBytes))
        {
            var oldest = _scenePanelHistories.Keys.First();
            if (ReferenceEquals(oldest, scene) && _scenePanelHistories.Count == 1) break;
            var cutoff = _scenePanelHistories[oldest].Entries.Select(UndoSequence).DefaultIfEmpty(0).Max();
            void DiscardOlderProjectHistory<T>(Stack<T> stack) where T : class
            {
                var retained = stack.Where(entry => UndoSequence(entry) > cutoff).Reverse().ToArray();
                stack.Clear();
                foreach (var entry in retained) stack.Push(entry);
            }
            if (cutoff > 0)
            {
                DiscardOlderProjectHistory(_panelCommandUndo);
                DiscardOlderProjectHistory(_sharedAssetUndoStack);
                DiscardOlderProjectHistory(_symbolDeletionUndo);
                _documentRedo.Clear();
                _canvasSettingsRedo.Clear();
            }
            _scenePanelHistories.Remove(oldest);
        }
    }

    private void LoadScenePanelHistory(SceneDefinition scene)
    {
        static void Restore<T>(Stack<T> stack, IEnumerable<T> items)
        {
            stack.Clear();
            foreach (var item in items.Reverse()) stack.Push(item);
        }
        _scenePanelHistories.Remove(scene, out var history);
        Restore(_undoStack, history?.Drawing ?? []);
        Restore(_sceneTimelineUndoStack, history?.Timeline ?? []);
        Restore(_canvasSettingsUndo, history?.Canvas ?? []);
        _frame = Math.Clamp(history?.Frame ?? 0, 0, Math.Max(0, scene.FrameCount - 1));
    }

    private void SelectScenePreservingHistory(int index)
    {
        if (index < 0 || index >= _scenes.Count || !CommitTextEdit()) return;
        if (IsSymbolEditing && !ExitSceneSymbolEditing()) return;
        FinishPointerInteractionForContextChange();
        CompleteInspectorGeometryEdit();
        CompleteAnchorEdit();
        StopPlayback();
        if (_activeSceneIndex != index)
        {
            if (ActiveScene() is { } previous) SaveScenePanelHistory(previous);
            _activeSceneIndex = index;
            LoadScenePanelHistory(_scenes[index]);
        }
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        _sceneEditorPanel.SetInstanceSource(ActiveDrawingObject()?.Name);
        UpdateSceneDimensionButton();
        if (IsSceneWorkspaceSelected) BindSceneEditStageCore(resetView: false, preserveHistory: true);
        _timeline.RefreshTimeline();
        PruneUndoSequenceEntries();
    }

    private bool ActivateLatestScenePanelHistory()
    {
        var current = _undoStack.Cast<object>().Concat(_sceneTimelineUndoStack).Concat(_canvasSettingsUndo)
            .Concat(_panelCommandUndo).Concat(_sharedAssetUndoStack).Concat(_symbolDeletionUndo)
            .Select(UndoSequence).DefaultIfEmpty(0).Max();
        var candidate = _scenePanelHistories
            .Where(pair => _scenes.Contains(pair.Key))
            .Select(pair => (Scene: pair.Key, Sequence: pair.Value.Entries.Select(UndoSequence).DefaultIfEmpty(0).Max()))
            .OrderByDescending(item => item.Sequence).FirstOrDefault();
        if (candidate.Scene is not null && candidate.Sequence > current)
        {
            if (!IsSceneWorkspaceSelected) return false;
            SelectScenePreservingHistory(_scenes.ToList().IndexOf(candidate.Scene));
            return ReferenceEquals(ActiveScene(), candidate.Scene);
        }
        return true;
    }

    private bool ActivatePanelRedoScope(object scope)
    {
        if (scope is SceneDefinition scene)
        {
            if (ReferenceEquals(ActiveScene(), scene)) return true;
            var index = _scenes.ToList().IndexOf(scene);
            if (index < 0) return false;
            SelectScenePreservingHistory(index);
            return ReferenceEquals(ActiveScene(), scene);
        }
        if (scope is VectorScene drawing && !ReferenceEquals(_scene, drawing))
        {
            var ownerIndex = _drawingObjects.ToList().FindIndex(owner => ReferenceEquals(owner.Scene, drawing));
            if (ownerIndex >= 0)
            {
                _activeDrawingObjectIndex = ownerIndex;
                BindActiveDrawingObjectSceneCore(resetView: false, preserveHistory: true);
                return ReferenceEquals(_scene, drawing);
            }
            var owner = _scenes.FirstOrDefault(item => ReferenceEquals(item.DrawingScene, drawing)
                || item.Layers.Any(layer => ReferenceEquals(layer.MaskScene, drawing)));
            if (owner is null) return false;
            SelectScenePreservingHistory(_scenes.ToList().IndexOf(owner));
            if (owner.Layers.FirstOrDefault(layer => ReferenceEquals(layer.MaskScene, drawing)) is { } maskLayer)
            {
                owner.SetActiveLayer(maskLayer.Id);
                BindSceneEditStageCore(resetView: false, preserveHistory: true);
            }
            if (!ReferenceEquals(_scene, drawing)) return false;
        }
        return true;
    }

    private void ApplySceneListFromHistory(SceneDefinition[] scenes, string selectedId)
    {
        if (ActiveScene() is { } previous) SaveScenePanelHistory(previous);
        _project.RestoreSceneListHistory(scenes);
        _activeSceneIndex = Math.Max(0, Array.FindIndex(scenes, scene => scene.Id == selectedId));
        LoadScenePanelHistory(_scenes[_activeSceneIndex]);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        _sceneEditorPanel.SetInstanceSource(ActiveDrawingObject()?.Name);
        if (IsSceneWorkspaceSelected) BindSceneEditStageCore(resetView: false, preserveHistory: true);
        UpdateSceneDimensionButton();
        MarkProjectDirty();
    }

    private void AddSceneFromPanel()
    {
        if (!CommitTextEdit()) return;
        var before = _scenes.ToArray();
        var previousId = ActiveScene()?.Id ?? before[0].Id;
        var added = _project.AddScene();
        SelectScenePreservingHistory(_scenes.Count - 1);
        var after = _scenes.ToArray();
        PushPanelHistory(new(_project, () => ApplySceneListFromHistory(before, previousId),
            () => ApplySceneListFromHistory(after, added.Id)));
    }

    private void RenameSceneFromPanel(int index)
    {
        if (index < 0 || index >= _scenes.Count) return;
        var scene = _scenes[index];
        if (!DrawingObjectNameDialog.TryAsk(this, "Rename Scene", "Scene name", scene.Name, out var name)) return;
        var before = scene.Name;
        if (name == before || !_project.TryRenameScene(scene.Id, name)) return;
        void Apply(string value)
        {
            _project.TryRenameScene(scene.Id, value);
            _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
            UpdateInspector();
        }
        PushPanelHistory(new(_project, () => Apply(before), () => Apply(name)));
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
    }

    private void DeleteSceneFromPanel(int index)
    {
        if (index < 0 || index >= _scenes.Count || _scenes.Count <= 1 || !CommitTextEdit()) return;
        var scene = _scenes[index];
        if (ModernMessageDialog.Show(this,
            string.Format(UiLocalization.T("Delete scene \"{0}\" and its timeline, cameras and instances? This can be undone."), scene.Name),
            UiLocalization.T("Delete Scene"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var before = _scenes.ToArray();
        var previousId = ActiveScene()?.Id ?? before[0].Id;
        if (ActiveScene() is { } active) SaveScenePanelHistory(active);
        if (!_project.TryRemoveScene(scene.Id, out _)) return;
        _activeSceneIndex = -1;
        var after = _scenes.ToArray();
        var selectedId = previousId == scene.Id ? after[Math.Min(index, after.Length - 1)].Id : previousId;
        ApplySceneListFromHistory(after, selectedId);
        PushPanelHistory(new(_project, () => ApplySceneListFromHistory(before, previousId),
            () => ApplySceneListFromHistory(after, selectedId)));
    }

    private void SelectInstanceFromScenePanel(string instanceId)
    {
        if (ActiveScene()?.Instances.FirstOrDefault(instance => instance.Id == instanceId) is not { } instance) return;
        if (!CommitTextEdit()) return;
        if (!IsSceneCompositionContext()) BindSceneEditStageCore(resetView: false, preserveHistory: true);
        ClearSelection();
        SetSceneInstanceSelection(instance);
        UpdateInspector();
        _stage.Invalidate();
    }

    private void AddSceneInstanceFromPanel()
    {
        var scene = ActiveScene();
        var symbol = ActiveDrawingObject();
        if (scene is null || symbol is null || symbol.IsOpenCanvas || !CommitTextEdit()) return;
        StopPlayback();
        var timeline = scene.Timeline.CreateSnapshot();
        var instances = scene.CreateInstanceSnapshot();
        var center = _stage.ScreenToWorld(new Point(_stage.ClientSize.Width / 2, _stage.ClientSize.Height / 2));
        var position = center;
        var step = Math.Max(16, 24f / Math.Max(0.01f, _stage.Zoom));
        for (var attempt = 0; attempt < 64 && scene.Instances.Any(instance =>
            Math.Abs(instance.X - position.X) < step / 2 && Math.Abs(instance.Y - position.Y) < step / 2); attempt++)
            position = new PointF(center.X + (attempt + 1) * step, center.Y + (attempt + 1) * step);
        if (!_project.TryAddSceneInstance(scene.Id, symbol.Id, position, 0, out var added) || added is null) return;
        PushSceneTimelineUndo(scene, timeline, instanceSnapshot: instances, playheadFrame: _frame);
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        _sceneEditorPanel.SetInstanceSource(symbol.Name);
        _timeline.RefreshTimeline();
        ApplyBoundTimelineDuration();
        RebuildSceneComposition();
        SetSceneInstanceSelection(added);
        UpdateInspector();
        _stage.Invalidate();
    }
}
