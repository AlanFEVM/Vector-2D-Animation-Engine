namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private SmartLayoutReference _smartLayoutReference;
    private void BuildSmartLayoutMenu()
    {
        var root = new ToolStripMenuItem("Align and distribute") { DropDown = new AnimatedContextMenuStrip() };
        var references = new[] { "Relative to selection", "Relative to key object", "Relative to canvas" };
        for (var i = 0; i < references.Length; i++)
        {
            var reference = (SmartLayoutReference)i;
            var item = new ToolStripMenuItem(references[i]);
            item.Click += (_, _) => _smartLayoutReference = reference;
            root.DropDown.Opening += (_, _) => item.Checked = _smartLayoutReference == reference;
            root.DropDownItems.Add(item);
        }
        root.DropDownItems.Add(new ToolStripSeparator());
        var labels = new[] { "Align left edges", "Align horizontal centers", "Align right edges",
            "Align top edges", "Align vertical centers", "Align bottom edges",
            "Distribute horizontal gaps", "Distribute vertical gaps" };
        for (var i = 0; i < labels.Length; i++)
        {
            var action = (SmartLayoutAction)i;
            var item = new ToolStripMenuItem(labels[i]);
            item.Click += (_, _) => ApplySmartLayout(action);
            root.DropDown.Opening += (_, _) => item.Enabled = CanApplySmartLayout(action);
            root.DropDownItems.Add(item);
        }
        _stageContextMenu.Items.Add(root);
        _stageContextMenu.Opening += (_, _) => root.Visible = !_playing && !IsShotDirectorContext()
            && !IsScene3DView() && (SelectedSceneInstances().Count > 0 || SelectedActiveDrawingObjectIndices().Length > 0);
    }

    private bool CanApplySmartLayout(SmartLayoutAction action)
    {
        if (_playing || IsShotDirectorContext() || IsScene3DView()) return false;
        var instances = SelectedSceneInstances();
        if (instances.Count > 0 && instances.Any(instance => !instance.EvaluateState(_frame).Visible)) return false;
        if (instances.Count > 0 && _selectedObjects.Count > 0 && !IsSceneCompositionContext()) return false;
        var count = instances.Count > 0 ? instances.Count : SelectedActiveDrawingObjectIndices().Length;
        if (instances.Count == 0 && (DrawingToolsBlocked()
            || SelectedActiveDrawingObjectIndices().Any(i => !_scene.IsObjectSelectable(i, _frame)))) return false;
        if (action is SmartLayoutAction.GapsX or SmartLayoutAction.GapsY)
            return count >= 3 && _smartLayoutReference != SmartLayoutReference.KeyObject
                && (_smartLayoutReference != SmartLayoutReference.Canvas || _stage.CanvasPresentation is not null);
        return count >= (_smartLayoutReference == SmartLayoutReference.Canvas ? 1 : 2)
            && (_smartLayoutReference != SmartLayoutReference.Canvas || _stage.CanvasPresentation is not null)
            && (instances.Count > 0 || !DrawingToolsBlocked());
    }

    private void ApplySmartLayout(SmartLayoutAction action)
    {
        if (!CanApplySmartLayout(action)) return;
        FinishPointerInteractionForContextChange();
        var canvas = _stage.CanvasPresentation is not null
            ? new RectangleF(-_stage.CanvasWorldWidth / 2, -_stage.CanvasWorldHeight / 2, _stage.CanvasWorldWidth, _stage.CanvasWorldHeight)
            : (RectangleF?)null;
        var instances = SelectedSceneInstances();
        if (instances.Count > 0)
        {
            var byId = SmartInstanceBounds();
            if (instances.Any(instance => !byId.ContainsKey(instance.Id))) return;
            var bounds = instances.Select(instance => byId[instance.Id]).ToArray();
            var key = instances.ToList().FindIndex(instance => instance.Id == _selectedSceneInstanceId);
            var delta = SmartLayout.Plan(bounds, action, _smartLayoutReference, Math.Max(0, key), canvas);
            if (delta.All(d => Math.Abs(d.X) < 0.001f && Math.Abs(d.Y) < 0.001f)) return;
            BeginSpatialTransformEdit(allowScene2D: true);
            var spatial = _spatialTransformEditSession;
            if (spatial is null)
            {
                if (IsSceneCompositionContext() || ActiveDrawingObject() is not { } container) return;
                PushUndoSnapshot(_scene.CreateSnapshot(), container, container.CreateInstanceSnapshot());
            }
            ClearInstanceTimelineEditTracking();
            for (var i = 0; i < instances.Count; i++)
            {
                if (Math.Abs(delta[i].X) < 0.001f && Math.Abs(delta[i].Y) < 0.001f) continue;
                var instance = instances[i];
                var frame = PrepareInstanceStateTimelineEdit(instance);
                var start = instance.EvaluateState(frame);
                if (instance.SetStateAtFrame(frame, start with
                    { X = VectorUnits.Quantize(start.X + delta[i].X), Y = VectorUnits.Quantize(start.Y + delta[i].Y) }))
                {
                    if (spatial is not null) spatial.Changed = true;
                    _sceneInstanceTimelineDirty = true;
                }
            }
            if (spatial is not null) CompleteSpatialTransformEdit();
            else
            {
                RefreshEditedInstanceTimelineTweens();
                ClearInstanceTimelineEditTracking();
                _sceneInstanceTimelineDirty = false;
                _timeline.RefreshTimeline();
                RebuildDrawingObjectUnderlay();
                SetSceneInstanceSelection(instances, instances[Math.Max(0, key)]);
                UpdateInspector();
                _stage.Invalidate();
            }
            return;
        }
        var snapshot = CreateCanvasMutationSnapshot();
        if (!TryPrepareSelectedDrawingObjectsForCommand(snapshot, out var targets)) return;
        if (targets.Any(i => !_scene.IsObjectSelectable(i, _frame))) { RestoreCanvasMutationSnapshot(snapshot); return; }
        var targetBounds = targets.Select(i => SmartObjectBounds(_scene, i)).ToArray();
        var keyIndex = Array.IndexOf(targets, _selectedObject);
        var offsets = SmartLayout.Plan(targetBounds, action, _smartLayoutReference, Math.Max(0, keyIndex), canvas);
        if (offsets.All(d => Math.Abs(d.X) < 0.001f && Math.Abs(d.Y) < 0.001f))
        { RestoreCanvasMutationSnapshot(snapshot); return; }
        for (var i = 0; i < targets.Length; i++)
            _scene.TranslateObjectsForPreview([targets[i]], offsets[i].X, offsets[i].Y);
        _scene.RebuildGeometryIndex();
        SetSelection(targets);
        ApplySelectedFillOverwriteAfterGeometryEdit();
        MergeSelectedFillsAfterGeometryEdit(connectNearby: true);
        MergeCompatibleLinesAfterDrawingOperation(LocalLineMergeScope(_selectedObjects));
        MaterializeEditedStrokeIntersections(_selectedObjects.ToArray());
        PushUndoSnapshot(snapshot);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        ResetSmartSnapping();
        _stage.Invalidate();
    }
}
