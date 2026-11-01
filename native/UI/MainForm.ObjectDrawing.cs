namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool _objectDrawingEnabled;
    private SvgIconButton? _objectDrawingButton;

    private sealed record DrawingToolCommit(
        VectorSceneSnapshot Snapshot,
        bool ObjectDrawing,
        DrawingObjectDefinition? Container,
        SceneDefinition? SceneContainer,
        SceneLayerSnapshot? Layers,
        DrawingObjectInstanceDefinition[] Instances,
        int Frame,
        bool WasDirty);

    private bool ObjectDrawingActive => _objectDrawingEnabled && CanUseObjectDrawing();

    private bool CanUseObjectDrawing()
    {
        if (!IsEditableDrawingContext() || IsSceneMaskEditing() || DrawingToolsBlocked()
            || _scene.GetLayerKind(_scene.ActiveLayer) != DrawingLayerKind.Drawing
            || _scene.IsLayerEffectivelyLocked(_scene.ActiveLayer)) return false;
        if (!(IsShapeTool(_tool) || IsLineTool(_tool) || IsBrushTool(_tool))) return false;
        return IsSceneDrawingContext() || IsDrawingWorkspaceContext && !IsOpenDrawingScope;
    }

    private void AddObjectDrawingToggle(FlowLayoutPanel tools)
    {
        var button = new SvgIconButton(SvgIconKind.Objects)
        {
            Margin = new Padding(0, 4, 0, 4),
            AccessibleName = "Object Drawing",
            AccessibleDescription = "Draw each gesture as an independent editable symbol",
            AccessibleRole = AccessibleRole.CheckButton
        };
        _objectDrawingButton = button;
        UpdateToolbarTogglePresentation(button, active: false, name: "Object Drawing");
        button.MouseEnter += (_, _) => _toolTip.ShowFor(button,
            IsOpenDrawingScope
                ? "Object Drawing: switch to Current scope"
                : "Draw each gesture as an independent editable symbol");
        button.MouseLeave += (_, _) => _toolTip.HideTip();
        button.Click += (_, _) =>
        {
            FinishPointerInteractionForFrameChange();
            if (IsOpenDrawingScope && IsDrawingWorkspaceContext)
            {
                SwitchDrawingScope(current: true);
                if (IsOpenDrawingScope) return;
                _objectDrawingEnabled = true;
            }
            else _objectDrawingEnabled = !_objectDrawingEnabled;
            RefreshObjectDrawingToggle();
            _stage.Focus();
        };
        tools.Controls.Add(button);
    }

    private void RefreshObjectDrawingToggle()
    {
        if (_objectDrawingButton is not { } button) return;
        button.Visible = IsDrawingWorkspaceContext || IsSceneDrawingAvailable();
        button.Enabled = !DrawingToolsBlocked() && !IsSceneMaskEditing()
            && (CanUseObjectDrawing() || IsDrawingWorkspaceContext && IsOpenDrawingScope);
        UpdateToolbarTogglePresentation(button, ObjectDrawingActive, "Object Drawing");
        button.AccessibleDescription = UiLocalization.T(ObjectDrawingActive ? "On" : "Off");
    }

    private DrawingToolCommit BeginDrawingToolCommit()
    {
        var objectDrawing = ObjectDrawingActive;
        var sceneContainer = objectDrawing && IsSceneDrawingContext() ? ActiveScene() : null;
        var container = objectDrawing && sceneContainer is null ? ActiveDrawingObject() : null;
        var layers = sceneContainer?.CreateLayerSnapshot();
        var instances = container?.CreateInstanceSnapshot() ?? sceneContainer?.CreateInstanceSnapshot() ?? [];
        var wasDirty = _projectDirty;
        var snapshot = CreateCanvasMutationSnapshot(affectedLayers: [_scene.ActiveLayer]);
        return new DrawingToolCommit(snapshot, objectDrawing, container, sceneContainer,
            layers, instances, _frame, wasDirty);
    }

    private bool CompleteObjectDrawingCommit(DrawingToolCommit commit, IReadOnlyList<int> objects)
    {
        if (!commit.ObjectDrawing) return false;
        var targets = objects.Distinct().Where(index => (uint)index < _scene.ObjectCount).ToArray();
        DrawingObjectDefinition? symbol = null;
        DrawingObjectInstanceDefinition? instance = null;
        if (targets.Length == 0 || commit.Frame != _frame
            || !_project.TryConvertDrawingObjectsToSymbol(
                commit.Container?.Id ?? commit.SceneContainer!.Id, targets, commit.Frame,
                out symbol, out instance)
            || symbol is null || instance is null)
        {
            if (commit.Container is not null) commit.Container.RestoreInstanceSnapshot(commit.Instances);
            if (commit.SceneContainer is not null)
            {
                if (commit.Layers is not null) commit.SceneContainer.RestoreLayerSnapshot(commit.Layers);
                commit.SceneContainer.RestoreInstanceSnapshot(commit.Instances);
            }
            RestoreCanvasMutationSnapshot(commit.Snapshot);
            SetProjectDirty(commit.WasDirty);
            ClearSelection();
            _hierarchyPanel.RefreshScene();
            UpdateInspector();
            _stage.Invalidate();
            if (targets.Length > 0) AppLog.Warn("Object Drawing commit failed; the drawing gesture was restored.");
            return true;
        }

        if (commit.Container is not null)
        {
            while (commit.Container.MoveInstancesInLayer([instance.Id], direction: 1)) { }
            PushUndoSnapshot(commit.Snapshot, commit.Container, commit.Instances,
                createdDrawingObjectId: symbol.Id);
        }
        else
        {
            while (commit.SceneContainer!.MoveInstancesInLayer([instance.Id], direction: 1)) { }
            PushSceneTimelineUndo(commit.SceneContainer, commit.Snapshot.Timeline!, commit.Layers,
                commit.Instances, playheadFrame: commit.Frame, createdDrawingObjectId: symbol.Id);
        }
        RebuildDrawingObjectUnderlay();
        SetSceneInstanceSelection(instance);
        _timeline.RefreshTimeline();
        _timeline.SelectModelActiveTrack();
        _hierarchyPanel.RefreshScene();
        RefreshDrawingObjectAssetPresentation();
        UpdateInspector();
        _stage.Invalidate();
        return true;
    }
}
