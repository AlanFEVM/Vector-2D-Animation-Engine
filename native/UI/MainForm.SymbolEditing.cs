using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private sealed record SymbolEditingSession(string SceneId, string InstanceId, string SymbolId,
        int SceneFrame, string? PreviousSymbolId, StageViewState SceneView,
        Bitmap Reference, Matrix3x2 ReferencePixelsToLocal, SymbolEditingSession? Parent);

    private SymbolEditingSession? _symbolEditingSession;
    private Button? _symbolEditingReturnButton;
    private readonly List<Button> _symbolEditingPathButtons = [];
    private bool IsSymbolEditing => _symbolEditingSession is not null;
    private bool IsDrawingWorkspaceContext => IsSymbolEditing
        || _workspaceTabs.SelectedView == WorkspaceView.BasicDrawing;
    private WorkspaceView EditingWorkspace => IsSymbolEditing
        ? WorkspaceView.BasicDrawing : _workspaceTabs.SelectedView;

    private bool BeginSceneSymbolEditing(SceneCompositionObjectOwner owner)
    {
        if ((!IsSymbolEditing && (!IsSceneCompositionContext() || !IsScene2DFrontView()))
            || !CommitTextEdit()) return false;
        var scene = ActiveScene();
        var parent = _symbolEditingSession;
        var instances = parent is null ? scene?.Instances : ActiveDrawingObject()?.Instances;
        var instance = instances?.FirstOrDefault(candidate => candidate.Id == owner.RootInstanceId
            || candidate.Id == owner.InstanceId);
        var symbol = _drawingObjects.FirstOrDefault(candidate => candidate.Id == instance?.DrawingObjectId);
        if (scene is null || instance is null || symbol is null) return false;
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.SymbolId == symbol.Id) return false;
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
        var image = _stage.CaptureSymbolEditingReference(rootId,
            parent is null ? null : _drawingObjectUnderlayResult);
        var sceneView = _stage.CaptureViewState();
        var sceneFrame = _frame;
        var parentFps = _playbackSettings.Fps;
        _symbolEditingSession = new(scene.Id, rootId, symbol.Id, sceneFrame,
            ActiveDrawingObject()?.Id, sceneView, image, screenToLocal, parent);
        _activeDrawingObjectIndex = _drawingObjects.ToList().IndexOf(symbol);
        ShowWorkspace(WorkspaceView.SceneEditor);
        SetFrame(instance.ResolvePlaybackFrame(sceneFrame, parentFps, symbol.FrameCount));
        var center = Vector2.Transform(new Vector2(_stage.Width / 2f, _stage.Height / 2f), screenToLocal);
        var zoom = MathF.Sqrt(MathF.Abs(localToScreen.GetDeterminant())) / VectorUnits.ToPixels(1);
        _stage.RestoreViewState(sceneView with { CameraX = center.X, CameraY = center.Y, Zoom = zoom });
        ApplySymbolEditingReference();
        BuildDrawingObjectTabs();
        _drawingObjectTabs.ScrollControlIntoView(_symbolEditingPathButtons.Last(button => button.Visible));
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
        if (_symbolEditingSession is not { } session) return false;
        while (session.Parent is { } parent) session = parent;
        return ReturnFromSymbolEditing(session);
    }

    private bool ExitParentSymbolEditing()
    {
        return _symbolEditingSession is { } session && ReturnFromSymbolEditing(session);
    }

    private bool ReturnFromSymbolEditing(SymbolEditingSession session)
    {
        var ancestor = _symbolEditingSession;
        while (ancestor is not null && !ReferenceEquals(ancestor, session)) ancestor = ancestor.Parent;
        if (ancestor is null) return false;
        if (!CommitTextEdit()) return false;
        StopPlayback();
        FinishPointerInteractionForContextChange();
        // Detach the bitmap from both renderers before disposing any exited levels.
        _stage.SetSymbolEditingReference(null);
        while (_symbolEditingSession is { } current && !ReferenceEquals(current, session.Parent))
        {
            _symbolEditingSession = current.Parent;
            current.Reference.Dispose();
        }
        var sceneIndex = _scenes.ToList().FindIndex(scene => scene.Id == session.SceneId);
        if (sceneIndex >= 0) _activeSceneIndex = sceneIndex;
        var previousIndex = _drawingObjects.ToList().FindIndex(symbol => symbol.Id == session.PreviousSymbolId);
        _activeDrawingObjectIndex = Math.Max(0, previousIndex);
        _frame = session.SceneFrame;
        InvalidateSceneCompositionCache();
        ShowWorkspace(WorkspaceView.SceneEditor);
        _stage.RestoreViewState(session.SceneView);
        BuildDrawingObjectTabs();
        if (IsSymbolEditing)
            _drawingObjectTabs.ScrollControlIntoView(_symbolEditingPathButtons.Last(button => button.Visible));
        var instances = IsSymbolEditing ? ActiveDrawingObject()?.Instances : ActiveScene()?.Instances;
        var instance = instances?.FirstOrDefault(candidate => candidate.Id == session.InstanceId);
        if (instance is not null) SetSceneInstanceSelection([instance], instance);
        _stage.Focus();
        return true;
    }

    private void ClearSymbolEditingSession()
    {
        _stage.SetSymbolEditingReference(null);
        while (_symbolEditingSession is { } session)
        {
            _symbolEditingSession = session.Parent;
            session.Reference.Dispose();
        }
        UpdateSymbolEditingBreadcrumb();
    }

    private void UpdateSymbolEditingBreadcrumb()
    {
        if (IsSymbolEditing)
        {
            if (_symbolEditingReturnButton is null)
            {
                _symbolEditingReturnButton = new Button
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Height = ScaleDrawingObjectTabMetric(28),
                    UseMnemonic = false,
                    Margin = new Padding(0, 0, ScaleDrawingObjectTabMetric(4), 0)
                };
                Theme.StyleButton(_symbolEditingReturnButton);
                _symbolEditingReturnButton.Click += (_, _) => ExitSceneSymbolEditing();
                _drawingObjectTabs.Controls.Add(_symbolEditingReturnButton);
            }
            var path = new List<SymbolEditingSession>();
            for (var session = _symbolEditingSession; session is not null; session = session.Parent)
                path.Add(session);
            path.Reverse();
            var sceneName = _scenes.FirstOrDefault(scene => scene.Id == path[0].SceneId)?.Name
                ?? UiLocalization.T("Scene");
            _symbolEditingReturnButton.Text = sceneName;
            _symbolEditingReturnButton.AccessibleName = $"{UiLocalization.T("Back to scene")}: {sceneName}";
            _symbolEditingReturnButton.Visible = true;
            _drawingObjectTabs.Controls.SetChildIndex(_symbolEditingReturnButton, 0);
            for (var index = 0; index < path.Count; index++)
            {
                if (index == _symbolEditingPathButtons.Count)
                {
                    var button = new Button
                    {
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Height = ScaleDrawingObjectTabMetric(28),
                        UseMnemonic = false,
                        Margin = new Padding(0, 0, ScaleDrawingObjectTabMetric(4), 0)
                    };
                    Theme.StyleButton(button);
                    button.Click += (_, _) =>
                    {
                        if (button.Tag is SymbolEditingSession target) ReturnFromSymbolEditing(target);
                    };
                    _symbolEditingPathButtons.Add(button);
                    _drawingObjectTabs.Controls.Add(button);
                }
                var segment = _symbolEditingPathButtons[index];
                var name = _drawingObjects.FirstOrDefault(symbol => symbol.Id == path[index].SymbolId)?.Name ?? "?";
                segment.Text = $"› {name}";
                segment.AccessibleName = name;
                segment.Tag = index + 1 < path.Count ? path[index + 1] : null;
                if (index + 1 == path.Count) Theme.StyleActiveButton(segment);
                else Theme.StyleButton(segment);
                segment.Visible = true;
                _drawingObjectTabs.Controls.SetChildIndex(segment, index + 1);
            }
            for (var index = path.Count; index < _symbolEditingPathButtons.Count; index++)
            {
                _symbolEditingPathButtons[index].Visible = false;
                _symbolEditingPathButtons[index].Tag = null;
            }
        }
        else
        {
            if (_symbolEditingReturnButton is not null) _symbolEditingReturnButton.Visible = false;
            foreach (var button in _symbolEditingPathButtons)
            {
                button.Visible = false;
                button.Tag = null;
            }
        }
        // Reserve a scrollbar row so arbitrarily long paths retain every ancestor name.
        _drawingObjectTabs.AutoScroll = IsSymbolEditing;
        _drawingObjectRow.Height = ScaleDrawingObjectTabMetric(IsSymbolEditing ? 54 : 36);
        _workspaceHeader.Height = _workspaceTabs.Height + _drawingObjectRow.Height;
        if (_activeDrawingObjectTabButton is { } symbolButton) symbolButton.Visible = !IsSymbolEditing;
        if (_addDrawingObjectTabButton is { } addButton) addButton.Visible = !IsSymbolEditing;
    }
}
