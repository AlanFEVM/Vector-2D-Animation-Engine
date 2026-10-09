namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly ToolStripSeparator _collisionTerrainMenuSeparator = new();
    private readonly ToolStripMenuItem _collisionTerrainMenuItem = new("Add Collision Terrain")
    {
        AccessibleName = "Add Collision Terrain",
        AccessibleDescription = "Create a semi-transparent polygon collision terrain mask"
    };
    private readonly ToolStripSeparator _randomFractureMenuSeparator = new();
    private readonly ToolStripMenuItem _randomFractureMenuItem = new("Random Fracture...")
    {
        AccessibleName = "Random Fracture"
    };
    private VectorScene? _randomFracturePreviewScene;
    private int _randomFractureDialogObject = -1;
    private int _randomFractureDialogFrame = -1;
    private CancellationTokenSource? _randomFracturePreviewCancellation;
    private readonly SemaphoreSlim _randomFracturePreviewGate = new(1, 1);
    private Task? _randomFracturePreviewTask;
    private int _randomFracturePreviewRequestId;

    private void BuildRandomFractureContextMenu()
    {
        _stageContextMenu.Items.Add(_collisionTerrainMenuSeparator);
        _stageContextMenu.Items.Add(_collisionTerrainMenuItem);
        _stageContextMenu.Items.Add(_randomFractureMenuSeparator);
        _stageContextMenu.Items.Add(_randomFractureMenuItem);
        _collisionTerrainMenuItem.Click += (_, _) => AddRandomFractureCollisionTerrain();
        _randomFractureMenuItem.Click += (_, _) => ShowRandomFractureDialog();
        _stageContextMenu.Opening += (_, _) =>
        {
            var canOfferTerrain = CanOfferRandomFractureCollisionTerrain();
            var canOffer = !IsSceneCompositionContext()
                && SelectedSceneInstances().Count == 0
                && _selectedElements.Count == 0;
            var targets = canOffer ? SelectedActiveDrawingObjectIndices() : [];
            var canFracture = targets.Length == 1
                && _scene.CanRandomFracture(targets[0], _frame, out _);
            _collisionTerrainMenuSeparator.Visible = canOfferTerrain;
            _collisionTerrainMenuItem.Visible = canOfferTerrain;
            _collisionTerrainMenuItem.Enabled = canOfferTerrain;
            _randomFractureMenuSeparator.Visible = canOffer;
            _randomFractureMenuItem.Visible = canOffer;
            _randomFractureMenuItem.Enabled = canFracture;
        };
    }

    private bool CanOfferRandomFractureCollisionTerrain()
    {
        return _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing
            && !IsSceneCompositionContext()
            && _timeline.Context is DrawingObjectDefinition or VectorScene
            && _scene.LayerCount > 0;
    }

    private void AddRandomFractureCollisionTerrain()
    {
        if (!CanOfferRandomFractureCollisionTerrain()) return;

        StopPlayback();
        var snapshot = _scene.CreateSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var terrainLayer = _scene.AddCollisionTerrainLayer();
        if ((uint)terrainLayer >= _scene.LayerCount)
        {
            ShowRandomFractureError("The collision terrain layer could not be created.");
            return;
        }

        _scene.ActiveLayer = terrainLayer;
        PushUndoSnapshot(
            snapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        ClearSelection();
        _drawSettings.ShapeKind = ShapeKind.Polygon;
        _drawSettings.PolygonSides = 6;
        _drawSettings.NotifyChanged();
        RefreshLayers();
        _timeline.SelectModelActiveTrack(clearFrameSelection: true);
        _timeline.SetActiveTabGroup(AnimationTimeline.TerrainTabGroupId);
        if (_timeline.Context is DrawingObjectDefinition) RebuildDrawingObjectUnderlay();
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        ActivateTool(ToolMode.Polygon);
        _stage.Invalidate();
        AppLog.Info($"Added Random Fracture collision terrain layer at index {terrainLayer}.");
    }

    private async void ShowRandomFractureDialog()
    {
        if (!TryGetRandomFractureTarget(out var objectIndex, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error)) ShowRandomFractureError(error);
            return;
        }

        StopPlayback();
        var applyFrame = _frame;
        var snapshot = _scene.CreateSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var previousLastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        _randomFractureDialogObject = objectIndex;
        _randomFractureDialogFrame = applyFrame;

        using var dialog = new RandomFractureDialog();
        void PreviewChanged(object? sender, EventArgs args) => UpdateRandomFracturePreview(dialog);
        void PreviewFrameChanged(object? sender, EventArgs args) => UpdateRandomFracturePreview(dialog);
        dialog.PreviewChanged += PreviewChanged;
        dialog.PreviewFrameChanged += PreviewFrameChanged;
        try
        {
            UpdateRandomFracturePreview(dialog);
            var result = dialog.ShowDialog(this);
            ClearRandomFracturePreview();
            await WaitForRandomFracturePreviewAsync();
            if (result != DialogResult.OK) return;

            var options = dialog.Options;
            if (!_scene.TryApplyRandomFracture(
                    objectIndex,
                    applyFrame,
                    options,
                    out var producedObjects,
                    out error))
            {
                RestoreCanvasMutationSnapshot(snapshot);
                ShowRandomFractureError(error);
                return;
            }

            PushUndoSnapshot(
                snapshot,
                playheadFrame: applyFrame,
                timelineSelection: timelineSelection);
            SetSelection(producedObjects);
            _stage.ClearHoveredLineElement();
            _timeline.RefreshTimeline();
            ApplyBoundTimelineDuration(previousLastFrame);
            _hierarchyPanel.RefreshScene();
            RebuildDrawingObjectUnderlay();
            UpdateInspector();
            _stage.Invalidate();
            AppLog.Info(
                $"Applied Random Fracture: {producedObjects.Length} fragment(s), "
                + $"frame={applyFrame}, animated={options.GenerateAnimation}.");
        }
        finally
        {
            dialog.PreviewChanged -= PreviewChanged;
            dialog.PreviewFrameChanged -= PreviewFrameChanged;
            ClearRandomFracturePreview();
            _randomFractureDialogObject = -1;
            _randomFractureDialogFrame = -1;
        }
    }

    private void UpdateRandomFracturePreview(RandomFractureDialog dialog)
    {
        if (_randomFractureDialogObject < 0 || _randomFractureDialogFrame < 0)
        {
            ClearRandomFracturePreview();
            dialog.SetPreviewStatus("Preview unavailable", valid: false);
            return;
        }

        var requestId = unchecked(++_randomFracturePreviewRequestId);
        var cancellation = new CancellationTokenSource();
        var previousCancellation = _randomFracturePreviewCancellation;
        _randomFracturePreviewCancellation = cancellation;
        previousCancellation?.Cancel();

        _stage.BindDragPreviewScene(null);
        _randomFracturePreviewScene = null;
        dialog.SetPreviewStatus("Generating preview...", valid: false);

        var objectIndex = _randomFractureDialogObject;
        var frame = _randomFractureDialogFrame;
        var previewFrame = dialog.PreviewFrame;
        var options = dialog.Options;
        _randomFracturePreviewTask = GenerateRandomFracturePreviewAsync(
            dialog,
            requestId,
            objectIndex,
            frame,
            previewFrame,
            options,
            cancellation);
    }

    private async Task GenerateRandomFracturePreviewAsync(
        RandomFractureDialog dialog,
        int requestId,
        int objectIndex,
        int frame,
        int previewFrame,
        RandomFractureOptions options,
        CancellationTokenSource cancellation)
    {
        var gateEntered = false;
        try
        {
            await _randomFracturePreviewGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            var result = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var success = _scene.TryCreateRandomFracturePreview(
                    objectIndex,
                    frame,
                    options,
                    out var previewScene,
                    out var hiddenSourceObjects,
                    out var error,
                    previewFrame: previewFrame,
                    cancellationToken: cancellation.Token);
                return (Success: success, PreviewScene: previewScene, HiddenSourceObjects: hiddenSourceObjects, Error: error);
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested
                || requestId != _randomFracturePreviewRequestId
                || IsDisposed
                || dialog.IsDisposed)
            {
                return;
            }

            if (result.Success)
            {
                _randomFracturePreviewScene = result.PreviewScene;
                _stage.BindDragPreviewScene(
                    result.PreviewScene,
                    _scene,
                    result.HiddenSourceObjects);
                dialog.SetPreviewStatus("Preview ready", valid: true);
                return;
            }

            _stage.BindDragPreviewScene(null);
            _randomFracturePreviewScene = null;
            dialog.SetPreviewStatus(result.Error, valid: false);
        }
        catch (OperationCanceledException)
        {
            // A newer parameter or frame request owns the preview now.
        }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested
                || requestId != _randomFracturePreviewRequestId
                || IsDisposed
                || dialog.IsDisposed)
            {
                return;
            }

            _stage.BindDragPreviewScene(null);
            _randomFracturePreviewScene = null;
            AppLog.Error("Random Fracture preview failed.", exception);
            dialog.SetPreviewStatus("The fracture preview could not be generated.", valid: false);
        }
        finally
        {
            if (gateEntered) _randomFracturePreviewGate.Release();
            if (ReferenceEquals(_randomFracturePreviewCancellation, cancellation))
            {
                _randomFracturePreviewCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task WaitForRandomFracturePreviewAsync()
    {
        var previewTask = _randomFracturePreviewTask;
        if (previewTask is null) return;

        try
        {
            await previewTask;
        }
        catch (OperationCanceledException)
        {
            // Dialog shutdown owns cancellation; there is no preview result to publish.
        }
        finally
        {
            if (ReferenceEquals(_randomFracturePreviewTask, previewTask))
            {
                _randomFracturePreviewTask = null;
            }
        }

        // A canceled request may have been waiting on the gate while an older
        // request was still inside a non-interruptible native geometry call.
        // Drain the gate before mutating the source scene on Apply.
        await _randomFracturePreviewGate.WaitAsync();
        _randomFracturePreviewGate.Release();
    }

    private void ClearRandomFracturePreview()
    {
        unchecked { _randomFracturePreviewRequestId++; }
        _randomFracturePreviewCancellation?.Cancel();
        _randomFracturePreviewCancellation = null;
        _stage.BindDragPreviewScene(null);
        _randomFracturePreviewScene = null;
    }

    private void CancelRandomFracturePreview()
    {
        unchecked { _randomFracturePreviewRequestId++; }
        _randomFracturePreviewCancellation?.Cancel();
        _randomFracturePreviewCancellation = null;
    }

    private bool TryGetRandomFractureTarget(out int objectIndex, out string error)
    {
        objectIndex = -1;
        error = string.Empty;
        if (IsSceneCompositionContext())
        {
            error = "Random Fracture is unavailable in a scene composition.";
            return false;
        }

        if (SelectedSceneInstances().Count > 0)
        {
            error = "Select a drawing object rather than a nested instance.";
            return false;
        }

        if (_selectedElements.Count > 0)
        {
            error = "Select the whole filled shape before using Random Fracture.";
            return false;
        }

        var targets = SelectedActiveDrawingObjectIndices();
        if (targets.Length != 1)
        {
            error = "Select exactly one active filled shape before using Random Fracture.";
            return false;
        }

        objectIndex = targets[0];
        return _scene.CanRandomFracture(objectIndex, _frame, out error);
    }

    private void ShowRandomFractureError(string error)
    {
        ModernMessageDialog.Show(
            this,
            UiLocalization.T(string.IsNullOrWhiteSpace(error)
                ? "The fracture could not be applied."
                : error),
            UiLocalization.T("Random Fracture"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
}
