using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record SymbolEditingSession(string SceneId, string InstanceId, string SymbolId,
        int SceneFrame, string? PreviousSymbolId, StageViewState SceneView,
        Bitmap Reference, Matrix3x2 ReferencePixelsToLocal);

    private SymbolEditingSession? _symbolEditingSession;
    private Button? _symbolEditingReturnButton;
    private bool IsSymbolEditing => _symbolEditingSession is not null;
    private bool IsDrawingWorkspaceContext => IsSymbolEditing
        || _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing;
    private WorkspaceView EditingWorkspace => IsSymbolEditing
        ? WorkspaceView.BasicDrawing : _workspaceTabs.SelectedView;

    private bool BeginSceneSymbolEditing(SceneCompositionObjectOwner owner)
    {
        if (!IsSceneCompositionContext() || !IsScene2DFrontView() || !CommitTextEdit()) return false;
        var scene = ActiveScene();
        var instance = scene?.Instances.FirstOrDefault(candidate => candidate.Id == owner.RootInstanceId
            || candidate.Id == owner.InstanceId);
        var symbol = _drawingObjects.FirstOrDefault(candidate => candidate.Id == instance?.DrawingObjectId);
        if (scene is null || instance is null || symbol is null) return false;
        var state = instance.EvaluateState(_frame);
        // A warped or spatial placement needs its existing independent symbol editor.
        if (state.Distortion is not null || state.RotationX != 0 || state.RotationY != 0) return false;
        var transform = Matrix3x2.CreateTranslation(-symbol.Anchor.X, -symbol.Anchor.Y)
            * DrawingObjectInstanceDefinition.CreatePlanarTransform(state);
        Matrix3x2 sceneToScreen;
        if (_stage.RendersReferenceProjection)
        {
            if (!_stage.TryProjectScenePosition(Vector3.Zero, out var origin, out _)
                || !_stage.TryProjectScenePosition(new Vector3(1024, 0, 0), out var axisX, out _)
                || !_stage.TryProjectScenePosition(new Vector3(0, 1024, 0), out var axisY, out _)) return false;
            sceneToScreen = new((axisX.X - origin.X) / 1024, (axisX.Y - origin.Y) / 1024,
                (axisY.X - origin.X) / 1024, (axisY.Y - origin.Y) / 1024, origin.X, origin.Y);
        }
        else
        {
            var origin = _stage.WorldToScreen(0, 0);
            var scale = VectorUnits.ToPixels(1) * _stage.Zoom;
            sceneToScreen = new(scale, 0, 0, scale, origin.X, origin.Y);
        }
        var localToScreen = transform * sceneToScreen;
        if (!Matrix3x2.Invert(localToScreen, out var screenToLocal)) return false;
        StopPlayback();
        FinishPointerInteractionForContextChange();
        var rootId = instance.Id;
        var image = _stage.CaptureSymbolEditingReference(rootId);
        var sceneView = _stage.CaptureViewState();
        var sceneFrame = _frame;
        var parentFps = _playbackSettings.Fps;
        _symbolEditingSession = new(scene.Id, rootId, symbol.Id, sceneFrame,
            ActiveDrawingObject()?.Id, sceneView, image, screenToLocal);
        _activeDrawingObjectIndex = _drawingObjects.ToList().IndexOf(symbol);
        _inspectorCategory = InspectorCategory.None;
        ShowWorkspace(WorkspaceView.SceneEditor);
        SetFrame(instance.ResolvePlaybackFrame(sceneFrame, parentFps, symbol.FrameCount));
        var center = Vector2.Transform(new Vector2(_stage.Width / 2f, _stage.Height / 2f), screenToLocal);
        var zoom = MathF.Sqrt(MathF.Abs(localToScreen.GetDeterminant())) / VectorUnits.ToPixels(1);
        _stage.RestoreViewState(sceneView with { CameraX = center.X, CameraY = center.Y, Zoom = zoom });
        ApplySymbolEditingReference();
        BuildDrawingObjectTabs();
        ActivateTool(ToolMode.Select);
        _stage.Focus();
        return true;
    }

    private void ApplySymbolEditingReference()
    {
        if (_symbolEditingSession is { } session)
            _stage.SetSymbolEditingReference(session.Reference, session.ReferencePixelsToLocal);
    }

    private bool ExitSceneSymbolEditing()
    {
        if (_symbolEditingSession is not { } session || !CommitTextEdit()) return false;
        StopPlayback();
        FinishPointerInteractionForContextChange();
        ClearSymbolEditingSession();
        var sceneIndex = _scenes.ToList().FindIndex(scene => scene.Id == session.SceneId);
        if (sceneIndex >= 0) _activeSceneIndex = sceneIndex;
        var previousIndex = _drawingObjects.ToList().FindIndex(symbol => symbol.Id == session.PreviousSymbolId);
        _activeDrawingObjectIndex = Math.Max(0, previousIndex);
        _frame = session.SceneFrame;
        InvalidateSceneCompositionCache();
        ShowWorkspace(WorkspaceView.SceneEditor);
        _stage.RestoreViewState(session.SceneView);
        BuildDrawingObjectTabs();
        var instance = ActiveScene()?.Instances.FirstOrDefault(candidate => candidate.Id == session.InstanceId);
        if (instance is not null) SetSceneInstanceSelection([instance], instance);
        _stage.Focus();
        return true;
    }

    private void ClearSymbolEditingSession()
    {
        var session = _symbolEditingSession;
        _symbolEditingSession = null;
        _stage.SetSymbolEditingReference(null);
        session?.Reference.Dispose();
        if (_symbolEditingReturnButton is not null) _symbolEditingReturnButton.Visible = false;
    }

    private void UpdateSymbolEditingBreadcrumb()
    {
        if (IsSymbolEditing)
        {
            if (_symbolEditingReturnButton is null)
            {
                _symbolEditingReturnButton = new Button
                {
                    AutoSize = true, Height = ScaleDrawingObjectTabMetric(28),
                    AccessibleName = "Back to scene", Text = "Back to scene", Margin = new Padding(0, 0, 6, 0)
                };
                Theme.StyleButton(_symbolEditingReturnButton);
                _symbolEditingReturnButton.Click += (_, _) => ExitSceneSymbolEditing();
                _drawingObjectTabs.Controls.Add(_symbolEditingReturnButton);
                UiLocalization.Watch(_symbolEditingReturnButton);
            }
            _symbolEditingReturnButton.Text = UiLocalization.T("Back to scene");
            _symbolEditingReturnButton.Visible = true;
            _drawingObjectTabs.Controls.SetChildIndex(_symbolEditingReturnButton, 0);
        }
        else if (_symbolEditingReturnButton is not null) _symbolEditingReturnButton.Visible = false;
        if (_activeDrawingObjectTabButton is { } symbolButton) symbolButton.Enabled = !IsSymbolEditing;
        if (_addDrawingObjectTabButton is { } addButton) addButton.Visible = !IsSymbolEditing;
    }
}
