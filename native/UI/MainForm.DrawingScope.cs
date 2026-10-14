namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private string _currentScopeDrawingObjectId = "";

    private bool IsOpenDrawingScope => ActiveDrawingObject()?.IsOpenCanvas == true;

    private void SwitchDrawingScope(bool current)
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing || !CommitTextEdit())
        {
            BuildDrawingObjectTabs();
            return;
        }
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        DrawingObjectDefinition? target;
        if (current)
        {
            target = _drawingObjects.FirstOrDefault(item => !item.IsOpenCanvas
                && item.Id == _currentScopeDrawingObjectId)
                ?? _drawingObjects.FirstOrDefault(item => !item.IsOpenCanvas);
        }
        else
        {
            if (ActiveDrawingObject() is { IsOpenCanvas: false } active)
                _currentScopeDrawingObjectId = active.Id;
            target = _project.GetOrCreateOpenCanvas();
        }
        if (target is null) return;
        _activeDrawingObjectIndex = _drawingObjects.ToList().IndexOf(target);
        BindActiveDrawingObjectScene(resetView: false);
        BuildDrawingObjectTabs();
        _stage.Focus();
    }

    private bool AddOpenCanvasSelectionToSymbol()
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.BasicDrawing
            || !IsOpenDrawingScope || DrawingToolsBlocked() || !CommitTextEdit()) return false;
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        var targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length == 0 || SelectedSceneInstances().Count > 0)
        {
            ShowOpenCanvasSelectionMessage();
            return false;
        }
        var snapshot = _scene.CreateSnapshot();
        var isolated = new VectorScene();
        isolated.RestoreSnapshot(snapshot);
        isolated.EditFrame = _frame;
        if (_selectedElements.Count > 0)
        {
            var materialized = isolated.MaterializeSelectedParts(
                _selectedElements.Select(hit => hit.Key).ToArray(), _frame);
            if (!materialized.Success || materialized.Parts.Length == 0)
            {
                ShowOpenCanvasSelectionMessage();
                return false;
            }
            targets = materialized.Parts.Select(part => part.Result.ObjectIndex).Distinct().ToArray();
        }
        VectorScene selection;
        try
        {
            selection = isolated.CreateStaticSelectionScene(targets, _frame);
        }
        catch (InvalidOperationException)
        {
            ShowOpenCanvasSelectionMessage();
            return false;
        }
        if (!TryAskNewSymbolName(out var name)) return false;
        var symbol = _project.AddSymbolFromOpenCanvasSelection(selection, name);
        PushUndoSnapshot(snapshot, createdDrawingObjectId: symbol.Id);
        _currentScopeDrawingObjectId = symbol.Id;
        RefreshDrawingObjectAssetPresentation();
        _stage.Focus();
        return true;
    }

    private bool TryAskNewSymbolName(out string name) => DrawingObjectNameDialog.TryAsk(
        this, "Add Symbol", "Symbol name", _project.GetNextSymbolName(), out name, "Add Symbol");

    private void ShowOpenCanvasSelectionMessage()
    {
        ModernMessageDialog.Show(this,
            UiLocalization.T("Select drawing shapes from one unlocked drawing layer to add a symbol. Nested instances are not supported."),
            UiLocalization.T("Add Symbol"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
