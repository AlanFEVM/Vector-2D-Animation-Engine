using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm : Form
{
    private const float SpatialThicknessLimit = 5_000_000f;
    private const float MinimumSpatialScreenAxisLength = 4f;
    private const float FallbackSpatialScreenAxisLength = 72f;
    private const float MinimumSpatialAxisPlaneRayDot = 0.1f;

    private readonly ReferenceViewPad _referenceViewPad = new();
    private readonly SpatialTransformPanel _spatialTransformPanel = new();
    private SpatialTransformMode _spatialTransformMode;
    private SpatialTransformSpace _spatialTransformSpace;
    private SpatialPivotKind _spatialPivotKind;
    private SpatialTransformEditSession? _spatialTransformEditSession;
    private SpatialTransformPointerSession? _spatialTransformPointerSession;
    private Point? _pendingSpatialTransformScreen;
    private bool _spatialTransformKeyboardActive;
    private SpatialTransformAxis _spatialTransformKeyboardAxis;
    private Vector3? _projectedSceneMoveStartRayOrigin;
    private bool _switchingSceneLayerEditingContext;

    private sealed class SpatialTransformEditSession
    {
        public required SceneDefinition Scene { get; init; }
        public required AnimationTimelineSnapshot TimelineSnapshot { get; init; }
        public required DrawingObjectInstanceDefinition[] InstanceSnapshot { get; init; }
        public required TimelineSelectionSnapshot TimelineSelection { get; init; }
        public required string[] SelectedInstanceIds { get; init; }
        public required string PrimaryInstanceId { get; init; }
        public required Dictionary<string, int> EditFrames { get; init; }
        public required Dictionary<string, InstanceFrameState> StartStates { get; init; }
        public bool Changed { get; set; }
    }

    private sealed class SpatialTransformPointerSession
    {
        public required SpatialTransformHandleHit Handle { get; init; }
        public required SpatialPivotKind PivotKind { get; init; }
        public required Point StartScreen { get; init; }
        public required Vector3 Origin { get; init; }
        public required SpatialGizmoBasis Basis { get; init; }
        public required Vector3 Axis { get; init; }
        public required Vector2 ScreenAxis { get; init; }
        public required float ScreenAxisLength { get; init; }
        public required float AxisStartParameter { get; init; }
        public required float AxisWorldLength { get; init; }
        public required bool ScreenAxisProjectionOnly { get; init; }
        public Vector3? AxisDragPlaneNormal { get; init; }
        public required Vector3 PlaneNormal { get; init; }
        public Vector3? PlaneStartPoint { get; init; }
        public PointF[] RotationRing { get; init; } = [];
        public Vector3? RotationPreviousVector { get; set; }
        public float RotationPreviousScreenParameter { get; set; }
        public bool RotationScreenParameterValid { get; set; }
        public bool RotationUsedScreenFallback { get; set; }
        public float RotationAccumulatedRadians { get; set; }
        public float PreviousRawRotationRadians;
        public float EffectiveRotationRadians;
        public Vector3 PreviousRawMoveDelta;
        public Vector3 EffectiveMoveDelta;
        public float PreviousRawScaleFactor = 1f;
        public float EffectiveScaleFactor = 1f;
        public float PreviousRawThicknessDelta;
        public float EffectiveThicknessDelta;
        public bool KeyboardDriven { get; init; }
        public bool PointerUpdateApplied { get; set; }
    }

    private bool IsSceneReferenceView()
    {
        return IsSceneCompositionContext() && _stage.UsesReferenceProjection;
    }

    private bool IsScene2DFrontView()
    {
        return IsSceneCompositionContext()
            && ActiveSceneViewDimension() == SceneDimension.TwoD
            && _stage.Reference2DViewDirection == ReferenceViewDirection.Front;
    }

    private bool IsProjectedScene2DTransformView()
    {
        return IsSceneReferenceView() && IsScene2DFrontView();
    }

    private void AttachSceneSpatialControls(Panel stagePanel, Control metrics)
    {
        _spatialTransformPanel.Dock = DockStyle.None;
        _spatialTransformPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _spatialTransformPanel.Size = new Size(300, _spatialTransformPanel.PreferredPanelHeight);
        _spatialTransformPanel.Visible = false;
        PaintFullBorder(_spatialTransformPanel);
        stagePanel.Controls.Add(_spatialTransformPanel);

        void LayoutSpatialTransformPanel()
        {
            var margin = Math.Max(8, (int)MathF.Round(12f * stagePanel.DeviceDpi / 96f));
            var stageBounds = _stage.Bounds;
            var topBoundary = metrics.Visible ? metrics.Bottom : stageBounds.Top;
            var location = new Point(
                Math.Max(stageBounds.Left + margin, stageBounds.Right - _spatialTransformPanel.Width - margin),
                Math.Max(stageBounds.Top + margin, topBoundary + margin));
            _spatialTransformPanel.SetFloatingTargetLocation(location);
        }

        stagePanel.Resize += (_, _) => LayoutSpatialTransformPanel();
        _stage.LocationChanged += (_, _) => LayoutSpatialTransformPanel();
        _stage.SizeChanged += (_, _) => LayoutSpatialTransformPanel();
        metrics.LocationChanged += (_, _) => LayoutSpatialTransformPanel();
        metrics.SizeChanged += (_, _) => LayoutSpatialTransformPanel();
        metrics.VisibleChanged += (_, _) => LayoutSpatialTransformPanel();

        _referenceViewPad.Visible = false;
        _referenceViewPad.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        stagePanel.Controls.Add(_referenceViewPad);

        void LayoutReferenceViewPad()
        {
            _referenceViewPad.Left = Math.Max(8, stagePanel.ClientSize.Width - _referenceViewPad.Width - 12);
            _referenceViewPad.Top = Math.Max(42, stagePanel.ClientSize.Height - _referenceViewPad.Height - 12);
        }

        stagePanel.Resize += (_, _) => LayoutReferenceViewPad();
        _referenceViewPad.ViewRequested += (_, e) =>
        {
            if (!IsSceneCompositionContext() || ActiveSceneViewDimension() != SceneDimension.TwoD) return;
            FinishPointerInteractionForContextChange();
            _stage.SetReferenceViewDirection(e.Direction, ReferenceCameraMotion.Animated);
            if (e.Direction != ReferenceViewDirection.Front
                && _tool is not (ToolMode.Select or ToolMode.Hand))
            {
                ActivateTool(ToolMode.Select);
            }
            UpdateSceneInstanceSelectionOverlay();
            UpdateTransformOverlay();
            RefreshToolButtons();
            ApplyToolCursor();
            UpdateStatusBar();
        };
        _spatialTransformPanel.ModeChanged += (_, e) => SetSpatialTransformMode(e.Mode, activateTool: true);
        _spatialTransformPanel.SpaceChanged += (_, e) => SetSpatialTransformSpace(e.Space);
        _spatialTransformPanel.PivotKindChanged += (_, e) => SetSpatialPivotKind(e.Kind, activateTool: true);
        _spatialTransformPanel.InteractionStarted += (_, _) =>
        {
            if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
            BeginSpatialTransformEdit();
        };
        _spatialTransformPanel.ValuesChanged += (_, e) => ApplySpatialTransformValues(e.Values, e.Group);
        _spatialTransformPanel.InteractionCompleted += (_, _) => CompleteSpatialTransformEdit();
        _spatialTransformPanel.InteractionCanceled += (_, _) => CancelSpatialTransformEdit();

        LayoutSpatialTransformPanel();
        LayoutReferenceViewPad();
        _spatialTransformPanel.BringToFront();
        _referenceViewPad.BringToFront();
    }

    private void UpdateSceneSpatialControlsVisibility()
    {
        var sceneWorkspace = _workspaceTabs.SelectedView == WorkspaceView.SceneEditor;
        _referenceViewPad.Visible = sceneWorkspace
            && IsSceneCompositionContext()
            && ActiveSceneViewDimension() == SceneDimension.TwoD;
        if (_referenceViewPad.Visible) _referenceViewPad.BringToFront();
    }

    private void UpdateSpatialTransformPanelState()
    {
        var spatialContextAvailable = _workspaceTabs.SelectedView == WorkspaceView.SceneEditor
            && IsScene3DView()
            && !IsSceneMaskEditing();
        var instances = spatialContextAvailable ? SelectedSceneInstances() : [];
        var floatingVisible = spatialContextAvailable && instances.Count > 0;
        _spatialTransformPanel.SetMode(_spatialTransformMode);
        _spatialTransformPanel.SetPivotKind(_spatialPivotKind);
        _spatialTransformPanel.SetSpace(
            _spatialTransformSpace,
            enabled: _spatialPivotKind == SpatialPivotKind.None
                && _spatialTransformMode != SpatialTransformMode.Scale);
        var state = instances.Count == 1 ? instances[0].EvaluateState(_frame) : (InstanceFrameState?)null;
        _spatialTransformPanel.SetState(
            state,
            enabled: floatingVisible && instances.Count == 1 && !_playing);
        _spatialTransformPanel.SetFloatingVisibility(floatingVisible, animate: spatialContextAvailable);
        ArrangeSceneInspectorSections();
    }

    private void ArrangeSceneInspectorSections()
    {
        var controls = _sceneEditPage.Content.Controls;
        if (!controls.Contains(_hierarchyPanel)
            || !controls.Contains(_sceneWorkflowControls))
        {
            return;
        }

        // Docking is evaluated from back to front, ending with the selection panels at the top.
        Control[] frontToBack =
        [
            _hierarchyPanel,
            _sceneWorkflowControls,
            _sceneLightingPanel,
            _spatialMaterialPanel,
            _drawingObjectInstancePanel,
            _tweenCurveEditorPanel
        ];
        var childIndex = 0;
        foreach (var control in frontToBack)
        {
            if (!ReferenceEquals(control.Parent, _sceneEditPage.Content)) continue;
            controls.SetChildIndex(control, childIndex++);
        }
    }

    private void HandleSceneLayerEditingContextChanged()
    {
        if (_switchingSceneLayerEditingContext) return;
        _switchingSceneLayerEditingContext = true;
        try
        {
            FinishPointerInteractionForContextChange();
            BindSceneEditStage(resetView: false);
            EnsureToolValidForSceneContext();
            UpdateSceneDimensionButton();
            UpdateSpatialTransformPanelState();
            RefreshToolButtons();
            ApplyToolCursor();
        }
        finally
        {
            _switchingSceneLayerEditingContext = false;
        }
    }

    private void EnsureToolValidForSceneContext()
    {
        if (CanActivateTool(_tool)) return;
        _tool = ToolMode.Select;
        _selectionToolGroup.ActiveTool = _tool;
        HideToolFlyouts();
        _stage.ClearDrawingPreview();
    }

    private IReadOnlyList<SceneCompositionMaskClip> BuildSceneCompositionMaskClips(
        SceneDefinition? sceneDefinition)
    {
        if (sceneDefinition is null || _sceneCompositionResult.ObjectOwners.Count == 0) return [];

        var clips = new List<SceneCompositionMaskClip>();
        foreach (var contentLayer in sceneDefinition.Layers)
        {
            if (contentLayer.Kind != SceneLayerKind.Content
                || string.IsNullOrWhiteSpace(contentLayer.MaskLayerId)
                || sceneDefinition.FindLayer(contentLayer.MaskLayerId) is not { Kind: SceneLayerKind.Mask } maskLayer
                || sceneDefinition.FindMaskScene(contentLayer.MaskLayerId) is not { } maskScene)
            {
                continue;
            }

            var exposure = sceneDefinition.Timeline.EvaluateTargetExposure(maskLayer.Id, _frame);
            var maskFrame = maskLayer.Visible && exposure.HasContent ? _frame : -1;

            var instanceIds = sceneDefinition.InstancesInLayer(contentLayer.Id)
                .Select(instance => instance.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (instanceIds.Count == 0) continue;

            var objectIndices = new HashSet<int>();
            for (var objectIndex = 0; objectIndex < _sceneEditStage.ObjectCount; objectIndex++)
            {
                if (!_sceneCompositionResult.TryGetOwner(objectIndex, out var owner)) continue;
                var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
                    ? owner.InstanceId
                    : owner.RootInstanceId;
                if (instanceIds.Contains(rootInstanceId)) objectIndices.Add(objectIndex);
            }
            if (objectIndices.Count > 0)
            {
                clips.Add(new SceneCompositionMaskClip(
                    _sceneEditStage,
                    maskScene,
                    maskFrame,
                    objectIndices));
            }
        }
        return clips;
    }

    private bool SynchronizeActiveSceneMaskTimelineContent(bool refreshTimeline = true)
    {
        var scene = ActiveScene();
        if (!IsSceneMaskEditing() || scene is null || string.IsNullOrWhiteSpace(scene.ActiveLayerId)) return false;
        var changed = _project.TrySynchronizeSceneMaskTimelineContent(scene.Id, scene.ActiveLayerId, _frame);
        if (changed && refreshTimeline) _timeline.RefreshTimeline();
        return changed;
    }

    private void SetSpatialTransformMode(SpatialTransformMode mode, bool activateTool)
    {
        if (!Enum.IsDefined(mode)) return;
        if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        else if (_spatialTransformPointerSession is not null) CompleteSpatialTransformPointer();
        else ClearPendingSpatialTransformPointer();
        _spatialPivotKind = SpatialPivotKind.None;
        _spatialTransformMode = mode;
        _spatialTransformPanel.SetMode(mode);
        _spatialTransformPanel.SetPivotKind(SpatialPivotKind.None);
        _spatialTransformPanel.SetSpace(
            _spatialTransformSpace,
            enabled: mode != SpatialTransformMode.Scale);
        if (activateTool && IsScene3DView() && _tool != ToolMode.Transform3D)
        {
            ActivateTool(ToolMode.Transform3D);
        }
        UpdateSpatialTransformPresentation();
    }

    private void SetSpatialPivotKind(SpatialPivotKind kind, bool activateTool)
    {
        if (!Enum.IsDefined(kind)) return;
        if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        else if (_spatialTransformPointerSession is not null) CompleteSpatialTransformPointer();
        else ClearPendingSpatialTransformPointer();
        _spatialPivotKind = kind;
        _spatialTransformPanel.SetPivotKind(kind);
        _spatialTransformPanel.SetSpace(
            _spatialTransformSpace,
            enabled: kind == SpatialPivotKind.None
                && _spatialTransformMode != SpatialTransformMode.Scale);
        if (activateTool && kind != SpatialPivotKind.None && IsScene3DView() && _tool != ToolMode.Transform3D)
        {
            ActivateTool(ToolMode.Transform3D);
        }
        UpdateSpatialTransformPresentation();
    }

    private void SetSpatialTransformSpace(SpatialTransformSpace space)
    {
        if (!Enum.IsDefined(space)
            || _spatialTransformMode == SpatialTransformMode.Scale
            || _spatialPivotKind != SpatialPivotKind.None) return;
        if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        else if (_spatialTransformPointerSession is not null) CompleteSpatialTransformPointer();
        else ClearPendingSpatialTransformPointer();
        _spatialTransformSpace = space;
        _spatialTransformPanel.SetSpace(space, enabled: true);
        UpdateSpatialTransformPresentation();
    }

    private void BeginSpatialTransformEdit(bool allowScene2D = false)
    {
        if (_playing
            || _spatialTransformEditSession is not null
            || !IsScene3DView()
                && !(allowScene2D
                    && IsSceneCompositionContext()
                    && ActiveSceneViewDimension() == SceneDimension.TwoD)
            || ActiveScene() is not { } scene)
        {
            return;
        }
        var instances = SelectedSceneInstances();
        if (instances.Count == 0) return;

        ClearInstanceTimelineEditTracking();
        var editFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        var startStates = new Dictionary<string, InstanceFrameState>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
            editFrames[instance.Id] = editFrame;
            startStates[instance.Id] = instance.EvaluateState(editFrame);
        }

        ClearSceneSnapQueryCache();
        _spatialTransformEditSession = new SpatialTransformEditSession
        {
            Scene = scene,
            TimelineSnapshot = scene.Timeline.CreateSnapshot(),
            InstanceSnapshot = scene.CreateInstanceSnapshot(),
            TimelineSelection = _timeline.CaptureSelectionSnapshot(),
            SelectedInstanceIds = instances.Select(instance => instance.Id).ToArray(),
            PrimaryInstanceId = _selectedSceneInstanceId,
            EditFrames = editFrames,
            StartStates = startStates
        };
    }

    private void ApplySpatialTransformValues(
        SpatialTransformValues values,
        SpatialTransformValueGroup group)
    {
        var session = _spatialTransformEditSession;
        var instance = SelectedSceneInstance();
        if (session is null
            || instance is null
            || session.SelectedInstanceIds.Length != 1
            || !string.Equals(session.SelectedInstanceIds[0], instance.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (!session.EditFrames.TryGetValue(instance.Id, out var requestedEditFrame)) return;
        var state = instance.EvaluateState(requestedEditFrame);
        var next = group switch
        {
            SpatialTransformValueGroup.Position => state with
            {
                X = VectorUnits.Quantize(values.X),
                Y = VectorUnits.Quantize(values.Y),
                Z = VectorUnits.Quantize(values.Z)
            },
            SpatialTransformValueGroup.Rotation => state with
            {
                RotationX = NormalizeDegrees(values.RotationX),
                RotationY = NormalizeDegrees(values.RotationY),
                RotationZ = NormalizeDegrees(values.RotationZ)
            },
            SpatialTransformValueGroup.Scale => state with
            {
                ScaleX = Math.Clamp(values.ScaleX, 0.01f, 1000f),
                ScaleY = Math.Clamp(values.ScaleY, 0.01f, 1000f),
                ScaleZ = Math.Clamp(VectorUnits.Quantize(values.ScaleZ), 0f, SpatialThicknessLimit)
            },
            SpatialTransformValueGroup.RotationPivot =>
                DrawingObjectInstanceDefinition.PreserveSpatialTransformForPivotChange(
                    state,
                    state with { RotationPivot = QuantizeSpatialPivot(values.RotationPivot) }),
            SpatialTransformValueGroup.ScalePivot =>
                DrawingObjectInstanceDefinition.PreserveSpatialTransformForPivotChange(
                    state,
                    state with { ScalePivot = QuantizeSpatialPivot(values.ScalePivot) }),
            _ => state
        };
        if (next.Equals(state)) return;
        var editFrame = PrepareInstanceStateTimelineEdit(instance);
        if (!instance.SetStateAtFrame(editFrame, next)) return;

        session.Changed = true;
        _sceneInstanceTimelineDirty = true;
        RefreshSpatialTransformPreview();
    }

    private void CompleteSpatialTransformEdit()
    {
        ClearPendingSpatialTransformPointer();
        ClearSpatialTransformKeyboardTracking();
        var session = _spatialTransformEditSession;
        _spatialTransformEditSession = null;
        ClearSceneSnapQueryCache();
        if (session is null) return;

        var changed = session.Changed || _sceneInstanceTimelineDirty;
        if (changed) RefreshEditedInstanceTimelineTweens();
        _sceneInstanceTimelineDirty = false;
        ClearInstanceTimelineEditTracking();
        if (!changed)
        {
            UpdateSpatialTransformPanelState();
            return;
        }
        if (!SpatialTransformEditHasNetStateChange(session))
        {
            RestoreSpatialTransformEditSession(session);
            return;
        }

        PushSceneTimelineUndo(
            session.Scene,
            session.TimelineSnapshot,
            instanceSnapshot: session.InstanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: session.TimelineSelection);
        _timeline.RefreshTimeline();
        InvalidateSceneCompositionCache();
        RebuildSceneComposition();
        RestoreTimelineInstanceSelection(session.SelectedInstanceIds, session.PrimaryInstanceId);
        UpdateInspector();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
    }

    private void CancelSpatialTransformEdit()
    {
        ClearPendingSpatialTransformPointer();
        ClearSpatialTransformKeyboardTracking();
        var session = _spatialTransformEditSession;
        _spatialTransformEditSession = null;
        ClearSceneSnapQueryCache();
        if (session is null) return;

        RestoreSpatialTransformEditSession(session);
    }

    private static bool SpatialTransformEditHasNetStateChange(SpatialTransformEditSession session)
    {
        foreach (var instanceId in session.SelectedInstanceIds)
        {
            var instance = session.Scene.Instances.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
            if (instance is null
                || !session.StartStates.TryGetValue(instanceId, out var start)
                || !session.EditFrames.TryGetValue(instanceId, out var editFrame)
                || !instance.EvaluateState(editFrame).Equals(start))
            {
                return true;
            }
        }
        return false;
    }

    private void RestoreSpatialTransformEditSession(SpatialTransformEditSession session)
    {
        session.Scene.RestoreInstanceSnapshot(session.InstanceSnapshot);
        session.Scene.Timeline.RestoreSnapshot(session.TimelineSnapshot);
        session.Scene.SynchronizeTimelineTracks();
        _sceneInstanceTimelineDirty = false;
        ClearInstanceTimelineEditTracking();
        _timeline.RefreshTimeline();
        _timeline.RestoreSelectionSnapshot(session.TimelineSelection);
        InvalidateSceneCompositionCache();
        RebuildSceneComposition();
        RestoreTimelineInstanceSelection(session.SelectedInstanceIds, session.PrimaryInstanceId);
        UpdateInspector();
    }

    private bool TryBeginProjectedScenePointer(MouseEventArgs e)
    {
        if (!IsSceneReferenceView() || e.Button != MouseButtons.Left) return false;
        if (TryBeginSceneLightGizmoPointer(e.Location)) return true;
        if (_tool == ToolMode.Transform3D && IsScene3DView())
        {
            var handle = _stage.HitTestSpatialTransformGizmo(e.Location);
            if (handle.IsValid && TryBeginSpatialTransformPointer(e.Location, handle)) return true;
        }
        if (_stage.HitTestSceneLightMarker(e.Location) is { Length: > 0 } lightId)
        {
            SelectSceneOpticsLight(lightId);
            UpdateInspector();
            return true;
        }

        if (_tool is ToolMode.Transform or ToolMode.Distort
            && IsProjectedScene2DTransformView())
        {
            BeginSceneCompositionPointer(e);
            return true;
        }

        if (_tool is not (ToolMode.Select or ToolMode.Transform3D)) return true;
        DrawingObjectInstanceDefinition? hitInstance = null;
        if (TryResolveProjectedSceneInstance(e.Location, out var instance))
        {
            hitInstance = instance;
            SetSceneInstanceSelection(instance, additive: IsShiftPressed());
            if (IsScene3DView() && _tool == ToolMode.Select)
            {
                ActivateTool(ToolMode.Transform3D);
            }
        }
        else if (!IsShiftPressed())
        {
            ClearSelection();
            ClearSceneOpticsLightSelection();
        }

        if (hitInstance is not null
            && _tool == ToolMode.Select
            && TryBeginProjectedSceneMovePointer(e.Location))
        {
            UpdateInspector();
            UpdateSpatialTransformPresentation();
            _stage.Invalidate();
            return true;
        }

        UpdateInspector();
        UpdateSpatialTransformPresentation();
        _stage.Invalidate();
        return true;
    }

    private bool CanMoveProjectedSceneInstances()
    {
        return IsSceneCompositionContext()
            && ActiveSceneViewDimension() == SceneDimension.TwoD
            && _stage.UsesReferenceProjection;
    }

    private bool TryBeginProjectedSceneMovePointer(Point screen)
    {
        if (!CanMoveProjectedSceneInstances()
            || !_stage.TryGetReferenceRay(screen, out var ray))
        {
            return false;
        }

        BeginSpatialTransformEdit(allowScene2D: true);
        if (_spatialTransformEditSession is null) return false;

        _projectedSceneMoveStartRayOrigin = ray.Origin;
        _sceneInstanceMoveActive = true;
        _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = null;
        _activeTransformHandle = TransformHandleKind.None;
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private bool UpdateProjectedSceneMovePointer(MouseEventArgs e)
    {
        if (_projectedSceneMoveStartRayOrigin is not { } startOrigin) return false;
        if (e.Button != MouseButtons.Left || !PointerDragExceeded(e.Location)) return true;
        if (!_stage.TryGetReferenceRay(e.Location, out var ray)) return true;

        var delta = ConstrainProjectedSceneMoveDelta(
            _stage.Reference2DViewDirection,
            ray.Origin - startOrigin);
        delta = ResolveSceneSnapDelta(
            delta,
            correction => ConstrainProjectedSceneMoveDelta(
                _stage.Reference2DViewDirection,
                correction));
        var identity = delta.LengthSquared() <= 0.000001f;

        ApplySpatialPointerStates((start, _) => start with
        {
            X = identity || !HasSpatialComponentDelta(delta.X)
                ? start.X
                : VectorUnits.Quantize(start.X + delta.X),
            Y = identity || !HasSpatialComponentDelta(delta.Y)
                ? start.Y
                : VectorUnits.Quantize(start.Y + delta.Y),
            Z = identity || !HasSpatialComponentDelta(delta.Z)
                ? start.Z
                : VectorUnits.Quantize(start.Z + delta.Z)
        });
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    internal static Vector3 ConstrainProjectedSceneMoveDelta(
        ReferenceViewDirection direction,
        Vector3 delta)
    {
        return direction switch
        {
            ReferenceViewDirection.Front or ReferenceViewDirection.Back => new Vector3(delta.X, delta.Y, 0),
            ReferenceViewDirection.Left or ReferenceViewDirection.Right => new Vector3(0, delta.Y, delta.Z),
            ReferenceViewDirection.Top or ReferenceViewDirection.Bottom => new Vector3(delta.X, 0, delta.Z),
            _ => Vector3.Zero
        };
    }

    private void CompleteProjectedSceneMovePointer()
    {
        if (_projectedSceneMoveStartRayOrigin is null) return;
        _projectedSceneMoveStartRayOrigin = null;
        _sceneInstanceMoveActive = false;
        _stage.ClearSceneSnapIndicator();
        CompleteSpatialTransformEdit();
        _lastMouse = null;
        _startScreen = null;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void CancelProjectedSceneMovePointer()
    {
        if (_projectedSceneMoveStartRayOrigin is null) return;
        _projectedSceneMoveStartRayOrigin = null;
        _sceneInstanceMoveActive = false;
        _stage.ClearSceneSnapIndicator();
        CancelSpatialTransformEdit();
        _lastMouse = null;
        _startScreen = null;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private bool TryResolveProjectedSceneInstance(Point screen, out DrawingObjectInstanceDefinition instance)
    {
        instance = null!;
        if (!_stage.TryHitTestProjectedObject(screen, 7f, out var objectIndex)
            || !_sceneCompositionResult.TryGetOwner(objectIndex, out var owner)
            || ActiveScene() is not { } scene)
        {
            return false;
        }

        var rootInstanceId = string.IsNullOrWhiteSpace(owner.RootInstanceId)
            ? owner.InstanceId
            : owner.RootInstanceId;
        instance = scene.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, rootInstanceId, StringComparison.Ordinal))!;
        return instance is not null;
    }

    private bool TryGetProjectedSceneTransformOverlay(out Matrix4x4 transform)
    {
        transform = Matrix4x4.Identity;
        if (!IsProjectedScene2DTransformView() || SelectedSceneInstances().Count == 0) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        foreach (var objectIndex in _selectedSceneInstanceObjectIndices)
        {
            if (_stage.TryGetReference3DFlatToSceneTransform(objectIndex, out transform)) return true;
        }
        return false;
    }

    private Matrix4x4? ProjectedSceneTransformOverlay()
    {
        return TryGetProjectedSceneTransformOverlay(out var transform) ? transform : null;
    }

    private bool TryBeginSpatialTransformPointer(
        Point screen,
        SpatialTransformHandleHit handle,
        bool keyboardDriven = false)
    {
        var selectedCount = SelectedSceneInstances().Count;
        if (!handle.IsValid
            || selectedCount == 0
            || _spatialPivotKind != SpatialPivotKind.None && selectedCount != 1) return false;
        _stage.CompleteSpatialTransformGizmoMotion();
        ClearPendingSpatialTransformPointer();
        BeginSpatialTransformEdit();
        if (_spatialTransformEditSession is null
            || !_stage.TryGetSpatialGizmoScreenGeometry(out var geometry))
        {
            return false;
        }

        var origin = _stage.SpatialTransformGizmoOrigin;
        var axis = geometry.Basis.Axis(handle.Axis);
        var planeNormal = geometry.Basis.PlaneNormal(handle.Axis);
        Vector3? planeStartPoint = null;
        if (IsSpatialPlaneAxis(handle.Axis))
        {
            if (!TryGetSpatialPlanePoint(screen, origin, planeNormal, out var startPoint))
            {
                CancelSpatialTransformEdit();
                return false;
            }
            planeStartPoint = startPoint;
        }
        var screenAxis = Vector2.Zero;
        var screenAxisLength = 100f;
        var axisStartParameter = 0f;
        var axisWorldLength = geometry.WorldLength;
        var screenAxisProjectionOnly = false;
        Vector3? axisDragPlaneNormal = null;
        if (handle.Axis is SpatialTransformAxis.X or SpatialTransformAxis.Y or SpatialTransformAxis.Z)
        {
            var endpoint = geometry.Endpoints[(int)handle.Axis - 1];
            var projectedAxis = new Vector2(endpoint.X - geometry.Origin.X, endpoint.Y - geometry.Origin.Y);
            var projectedAxisLength = projectedAxis.Length();
            if (projectedAxisLength > 0.001f)
            {
                screenAxis = projectedAxis / projectedAxisLength;
            }
            else
            {
                screenAxis = new Vector2(0, -1);
            }
            screenAxisLength = projectedAxisLength >= MinimumSpatialScreenAxisLength
                ? projectedAxisLength
                : FallbackSpatialScreenAxisLength;
            if (handle.Mode == SpatialTransformMode.Move
                && projectedAxisLength >= MinimumSpatialScreenAxisLength
                && TryCreateSpatialAxisDragPlane(screen, axis, out var dragPlaneNormal)
                && TryGetAxisPlaneParameter(
                    screen,
                    origin,
                    axis,
                    dragPlaneNormal,
                    out axisStartParameter))
            {
                axisDragPlaneNormal = dragPlaneNormal;
            }
            else if (handle.Mode == SpatialTransformMode.Move)
            {
                screenAxisProjectionOnly = true;
            }
        }

        var rotationStartVector = handle.Mode == SpatialTransformMode.Rotate
            && TryGetRotationPlaneVector(screen, origin, axis, out var startVector)
            ? startVector
            : (Vector3?)null;
        var rotationRing = handle.Mode == SpatialTransformMode.Rotate
            && handle.Axis is SpatialTransformAxis.X or SpatialTransformAxis.Y or SpatialTransformAxis.Z
                ? geometry.Rings[(int)handle.Axis - 1]
                : [];
        var rotationScreenParameterValid = TryResolveSpatialRingParameter(
            rotationRing,
            screen,
            out var rotationScreenParameter);
        if (!rotationScreenParameterValid)
        {
            rotationScreenParameter = ScreenAngle(screen, geometry.Origin);
            rotationScreenParameterValid = handle.Mode == SpatialTransformMode.Rotate;
        }
        _spatialTransformPointerSession = new SpatialTransformPointerSession
        {
            Handle = handle,
            PivotKind = _spatialPivotKind,
            StartScreen = screen,
            Origin = origin,
            Basis = geometry.Basis,
            Axis = axis,
            ScreenAxis = screenAxis,
            ScreenAxisLength = screenAxisLength,
            AxisStartParameter = axisStartParameter,
            AxisWorldLength = axisWorldLength,
            ScreenAxisProjectionOnly = screenAxisProjectionOnly,
            AxisDragPlaneNormal = axisDragPlaneNormal,
            PlaneNormal = planeNormal,
            PlaneStartPoint = planeStartPoint,
            RotationRing = rotationRing,
            RotationPreviousVector = rotationStartVector,
            RotationPreviousScreenParameter = rotationScreenParameter,
            RotationScreenParameterValid = rotationScreenParameterValid,
            KeyboardDriven = keyboardDriven
        };
        _stage.SetSpatialTransformActive(handle);
        _stage.BeginReference3DOpticalInteractionPreview();
        if (!keyboardDriven) _stage.Capture = true;
        _lastMouse = screen;
        _startScreen = screen;
        _startWorld = null;
        _stage.Cursor = Cursors.SizeAll;
        return true;
    }

    private void QueueSpatialTransformPointer(Point screen)
    {
        var pointer = _spatialTransformPointerSession;
        if (pointer is null || pointer.KeyboardDriven)
        {
            return;
        }

        _pendingSpatialTransformScreen = screen;
        if (!pointer.PointerUpdateApplied) ApplyPendingSpatialTransformPointer();
        _lineDragPreviewTimer.Start();
    }

    private bool ApplyPendingSpatialTransformPointer()
    {
        if (_pendingSpatialTransformScreen is not { } screen) return false;
        _pendingSpatialTransformScreen = null;
        var pointer = _spatialTransformPointerSession;
        if (pointer is null || pointer.KeyboardDriven)
        {
            return false;
        }

        pointer.PointerUpdateApplied = true;
        UpdateSpatialTransformPointer(screen);
        return true;
    }

    private void ClearPendingSpatialTransformPointer()
    {
        _pendingSpatialTransformScreen = null;
        if (!HasPendingFrameCoalescedWork()) _lineDragPreviewTimer.Stop();
    }

    private void UpdateSpatialTransformPointer(Point screen)
    {
        var pointer = _spatialTransformPointerSession;
        var edit = _spatialTransformEditSession;
        if (pointer is null || edit is null) return;

        switch (pointer.Handle.Mode)
        {
            case SpatialTransformMode.Move:
            {
                if (pointer.PlaneStartPoint is not null)
                {
                    if (!TryResolveSpatialPlaneDelta(pointer, screen, out var planeDelta))
                    {
                        return;
                    }
                    planeDelta = ApplySpatialPrecisionDelta(
                        ref pointer.PreviousRawMoveDelta,
                        ref pointer.EffectiveMoveDelta,
                        planeDelta,
                        IsShiftPressed());
                    if (pointer.PivotKind == SpatialPivotKind.None)
                    {
                        planeDelta = ResolveSceneSnapDelta(
                            planeDelta,
                            correction => correction
                                - pointer.PlaneNormal * Vector3.Dot(correction, pointer.PlaneNormal),
                            snapRequested: IsControlPressed());
                    }
                    else
                    {
                        _stage.ClearSceneSnapIndicator();
                    }
                    ApplySpatialMoveDelta(pointer, planeDelta);
                    break;
                }

                if (!TryResolveSpatialAxisDelta(pointer, screen, out var delta)) return;
                var axisDelta = pointer.Axis * delta;
                axisDelta = ApplySpatialPrecisionDelta(
                    ref pointer.PreviousRawMoveDelta,
                    ref pointer.EffectiveMoveDelta,
                    axisDelta,
                    IsShiftPressed());
                if (pointer.PivotKind == SpatialPivotKind.None)
                {
                    axisDelta = ResolveSceneSnapDelta(
                        axisDelta,
                        correction => pointer.Axis * Vector3.Dot(correction, pointer.Axis),
                        snapRequested: IsControlPressed());
                }
                else
                {
                    _stage.ClearSceneSnapIndicator();
                }
                ApplySpatialMoveDelta(pointer, axisDelta);
                break;
            }
            case SpatialTransformMode.Rotate:
            {
                var rawRadians = ResolveSpatialRotation(pointer, screen);
                var radians = ApplySpatialPrecisionDelta(
                    ref pointer.PreviousRawRotationRadians,
                    ref pointer.EffectiveRotationRadians,
                    rawRadians,
                    IsShiftPressed());
                if (Math.Abs(radians) <= 0.0001f)
                {
                    ApplySpatialPointerStates((start, _) => start);
                    return;
                }
                var rotation = Matrix4x4.CreateFromAxisAngle(pointer.Axis, radians);
                ApplySpatialPointerStates((start, _) =>
                {
                    var targetRotation = SpatialTransformMath.CreateRotation(start) * rotation;
                    if (!SpatialTransformMath.TryDecomposeRotation(
                            targetRotation,
                            new Vector3(start.RotationX, start.RotationY, start.RotationZ),
                            out var euler))
                    {
                        return start;
                    }
                    var operationPivot = DrawingObjectInstanceDefinition.RotationPivotScenePosition(start);
                    var targetPivot = pointer.Origin
                        + Vector3.Transform(operationPivot - pointer.Origin, rotation);
                    var positionDelta = targetPivot - operationPivot;
                    return start with
                    {
                        X = !HasSpatialComponentDelta(positionDelta.X)
                            ? start.X
                            : VectorUnits.Quantize(start.X + positionDelta.X),
                        Y = !HasSpatialComponentDelta(positionDelta.Y)
                            ? start.Y
                            : VectorUnits.Quantize(start.Y + positionDelta.Y),
                        Z = !HasSpatialComponentDelta(positionDelta.Z)
                            ? start.Z
                            : VectorUnits.Quantize(start.Z + positionDelta.Z),
                        RotationX = NormalizeDegrees(euler.X),
                        RotationY = NormalizeDegrees(euler.Y),
                        RotationZ = NormalizeDegrees(euler.Z)
                    };
                });
                break;
            }
            case SpatialTransformMode.Scale:
            {
                if (pointer.Handle.Axis == SpatialTransformAxis.Z)
                {
                    var rawThicknessDelta = ResolveSpatialScreenAxisDelta(pointer, screen);
                    var thicknessDelta = ApplySpatialPrecisionDelta(
                        ref pointer.PreviousRawThicknessDelta,
                        ref pointer.EffectiveThicknessDelta,
                        rawThicknessDelta,
                        IsShiftPressed());
                    var identity = Math.Abs(thicknessDelta) <= 0.0001f;
                    ApplySpatialPointerStates((start, _) => start with
                    {
                        ScaleZ = identity
                            ? start.ScaleZ
                            : ApplySpatialThicknessDelta(start.ScaleZ, thicknessDelta)
                    });
                    break;
                }

                var rawFactor = ResolveSpatialScale(pointer, screen);
                var factor = ApplySpatialPrecisionDelta(
                    ref pointer.PreviousRawScaleFactor,
                    ref pointer.EffectiveScaleFactor,
                    rawFactor,
                    IsShiftPressed());
                var identityFactor = Math.Abs(factor - 1f) <= 0.0001f;
                ApplySpatialPointerStates((start, _) => identityFactor
                    ? start
                    : ScaleSpatialState(start, pointer, factor));
                break;
            }
        }
    }

    private void ApplySpatialMoveDelta(SpatialTransformPointerSession pointer, Vector3 worldDelta)
    {
        var identity = worldDelta.LengthSquared() <= 0.000001f;
        if (pointer.PivotKind == SpatialPivotKind.None)
        {
            ApplySpatialPointerStates((start, _) => start with
            {
                X = identity || !HasSpatialComponentDelta(worldDelta.X)
                    ? start.X
                    : VectorUnits.Quantize(start.X + worldDelta.X),
                Y = identity || !HasSpatialComponentDelta(worldDelta.Y)
                    ? start.Y
                    : VectorUnits.Quantize(start.Y + worldDelta.Y),
                Z = identity || !HasSpatialComponentDelta(worldDelta.Z)
                    ? start.Z
                    : VectorUnits.Quantize(start.Z + worldDelta.Z)
            });
            return;
        }

        ApplySpatialPointerStates((start, _) =>
            identity || !TryMoveSpatialPivot(start, pointer.PivotKind, worldDelta, out var next)
                ? start
                : next);
    }

    internal static bool TryMoveSpatialPivot(
        InstanceFrameState start,
        SpatialPivotKind kind,
        Vector3 worldDelta,
        out InstanceFrameState next)
    {
        next = start;
        if (kind == SpatialPivotKind.None || !Finite(worldDelta)) return false;

        Matrix4x4 mapping;
        if (kind == SpatialPivotKind.Rotation)
        {
            mapping = SpatialTransformMath.CreateRotation(start);
        }
        else
        {
            mapping = DrawingObjectInstanceDefinition.CreateSpatialLinearTransform(start);
            if (!Matrix4x4.Invert(mapping, out _))
            {
                mapping = DrawingObjectInstanceDefinition.CreateSpatialLinearTransform(
                    start with { ScaleZ = 1f });
            }
        }
        if (!Matrix4x4.Invert(mapping, out var inverse)) return false;

        var localDelta = Vector3.TransformNormal(worldDelta, inverse);
        if (kind == SpatialPivotKind.Scale && Math.Abs(start.ScaleZ) <= 0.000001f)
        {
            localDelta.Z = 0;
        }
        if (!Finite(localDelta) || localDelta.LengthSquared() <= 0.000001f) return false;

        var candidate = kind == SpatialPivotKind.Rotation
            ? start with { RotationPivot = QuantizeSpatialPivot(start.RotationPivot + localDelta) }
            : start with { ScalePivot = QuantizeSpatialPivot(start.ScalePivot + localDelta) };
        next = DrawingObjectInstanceDefinition.PreserveSpatialTransformForPivotChange(start, candidate);
        return next != start;
    }

    private void ApplySpatialPointerStates(Func<InstanceFrameState, string, InstanceFrameState> transform)
    {
        var session = _spatialTransformEditSession;
        if (session is null) return;
        var changed = false;
        foreach (var instanceId in session.SelectedInstanceIds)
        {
            var instance = session.Scene.Instances.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
            if (instance is null || !session.StartStates.TryGetValue(instanceId, out var start)) continue;
            var next = transform(start, instanceId);
            if (session.EditFrames.TryGetValue(instanceId, out var currentFrame)
                && instance.EvaluateState(currentFrame).Equals(next))
            {
                continue;
            }
            var editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed |= instance.SetStateAtFrame(editFrame, next);
        }
        if (!changed) return;

        session.Changed = true;
        _sceneInstanceTimelineDirty = true;
        RefreshSpatialTransformPreview();
    }

    private static InstanceFrameState ScaleSpatialState(
        InstanceFrameState start,
        SpatialTransformPointerSession pointer,
        float factor)
    {
        factor = Math.Clamp(factor, 0.01f, 1000f);
        var uniform = pointer.Handle.Axis == SpatialTransformAxis.Uniform;
        var operationPivot = DrawingObjectInstanceDefinition.ScalePivotScenePosition(start);
        var offset = operationPivot - pointer.Origin;
        var targetPivot = uniform
            ? pointer.Origin + offset * factor
            : operationPivot + pointer.Axis * Vector3.Dot(offset, pointer.Axis) * (factor - 1f);
        var positionDelta = targetPivot - operationPivot;
        return start with
        {
            X = !HasSpatialComponentDelta(positionDelta.X)
                ? start.X
                : VectorUnits.Quantize(start.X + positionDelta.X),
            Y = !HasSpatialComponentDelta(positionDelta.Y)
                ? start.Y
                : VectorUnits.Quantize(start.Y + positionDelta.Y),
            Z = !HasSpatialComponentDelta(positionDelta.Z)
                ? start.Z
                : VectorUnits.Quantize(start.Z + positionDelta.Z),
            ScaleX = !uniform && pointer.Handle.Axis != SpatialTransformAxis.X
                ? start.ScaleX
                : Math.Clamp(start.ScaleX * factor, 0.01f, 1000f),
            ScaleY = !uniform && pointer.Handle.Axis != SpatialTransformAxis.Y
                ? start.ScaleY
                : Math.Clamp(start.ScaleY * factor, 0.01f, 1000f),
            ScaleZ = !uniform && pointer.Handle.Axis != SpatialTransformAxis.Z
                ? start.ScaleZ
                : Math.Clamp(VectorUnits.Quantize(start.ScaleZ * factor), 0f, SpatialThicknessLimit)
        };
    }

    internal static float ApplySpatialThicknessDelta(float startThickness, float worldDelta)
    {
        if (!float.IsFinite(startThickness) || !float.IsFinite(worldDelta)) return 0;
        return Math.Clamp(
            VectorUnits.Quantize(Math.Max(0, startThickness) + worldDelta),
            0f,
            SpatialThicknessLimit);
    }

    private bool TryResolveSpatialAxisDelta(
        SpatialTransformPointerSession pointer,
        Point screen,
        out float delta)
    {
        delta = 0;
        if (pointer.ScreenAxisProjectionOnly)
        {
            delta = ResolveSpatialScreenAxisDelta(pointer, screen);
            return float.IsFinite(delta);
        }

        if (pointer.AxisDragPlaneNormal is not { } planeNormal
            || !TryGetAxisPlaneParameter(
                screen,
                pointer.Origin,
                pointer.Axis,
                planeNormal,
                out var parameter))
        {
            return false;
        }
        delta = parameter - pointer.AxisStartParameter;
        return float.IsFinite(delta);
    }

    private static float ResolveSpatialScreenAxisDelta(SpatialTransformPointerSession pointer, Point screen)
    {
        var screenDelta = new Vector2(screen.X - pointer.StartScreen.X, screen.Y - pointer.StartScreen.Y);
        return Vector2.Dot(screenDelta, pointer.ScreenAxis)
            / Math.Max(1f, pointer.ScreenAxisLength)
            * pointer.AxisWorldLength;
    }

    private float ResolveSpatialRotation(SpatialTransformPointerSession pointer, Point screen)
    {
        if (TryGetRotationPlaneVector(screen, pointer.Origin, pointer.Axis, out var current))
        {
            if (pointer.RotationPreviousVector is { } previous
                && !pointer.RotationUsedScreenFallback)
            {
                pointer.RotationAccumulatedRadians += MathF.Atan2(
                    Vector3.Dot(pointer.Axis, Vector3.Cross(previous, current)),
                    Vector3.Dot(previous, current));
            }
            else if (pointer.RotationPreviousVector is null
                && pointer.RotationScreenParameterValid
                && TryResolveSpatialRingParameter(pointer.RotationRing, screen, out var initialParameter))
            {
                pointer.RotationAccumulatedRadians += NormalizeAngle(
                    initialParameter - pointer.RotationPreviousScreenParameter);
            }
            pointer.RotationPreviousVector = current;
            pointer.RotationUsedScreenFallback = false;
            if (TryResolveSpatialRingParameter(pointer.RotationRing, screen, out var parameter))
            {
                pointer.RotationPreviousScreenParameter = parameter;
                pointer.RotationScreenParameterValid = true;
            }
            return pointer.RotationAccumulatedRadians;
        }

        float screenParameter;
        if (!TryResolveSpatialRingParameter(pointer.RotationRing, screen, out screenParameter))
        {
            if (!_stage.TryProjectScenePosition(pointer.Origin, out var origin, out _))
            {
                return pointer.RotationAccumulatedRadians;
            }
            screenParameter = ScreenAngle(screen, origin);
        }
        if (pointer.RotationScreenParameterValid)
        {
            pointer.RotationAccumulatedRadians += NormalizeAngle(
                screenParameter - pointer.RotationPreviousScreenParameter);
        }
        pointer.RotationPreviousScreenParameter = screenParameter;
        pointer.RotationScreenParameterValid = true;
        pointer.RotationUsedScreenFallback = true;
        return pointer.RotationAccumulatedRadians;
    }

    private static bool TryResolveSpatialRingParameter(
        IReadOnlyList<PointF> ring,
        Point screen,
        out float parameter)
    {
        parameter = 0;
        if (ring.Count < 3) return false;

        var bestDistanceSquared = float.MaxValue;
        var bestSegment = 0;
        var bestAmount = 0f;
        for (var index = 0; index < ring.Count; index++)
        {
            var start = ring[index];
            var end = ring[(index + 1) % ring.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var amount = lengthSquared <= 0.000001f
                ? 0f
                : Math.Clamp(
                    ((screen.X - start.X) * dx + (screen.Y - start.Y) * dy) / lengthSquared,
                    0f,
                    1f);
            var projectedX = start.X + dx * amount;
            var projectedY = start.Y + dy * amount;
            var distanceX = screen.X - projectedX;
            var distanceY = screen.Y - projectedY;
            var distanceSquared = distanceX * distanceX + distanceY * distanceY;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            bestSegment = index;
            bestAmount = amount;
        }

        parameter = (bestSegment + bestAmount) * MathF.Tau / ring.Count;
        return float.IsFinite(parameter);
    }

    private static float ResolveSpatialScale(SpatialTransformPointerSession pointer, Point screen)
    {
        var delta = new Vector2(screen.X - pointer.StartScreen.X, screen.Y - pointer.StartScreen.Y);
        var pixels = pointer.Handle.Axis == SpatialTransformAxis.Uniform
            ? delta.X - delta.Y
            : Vector2.Dot(delta, pointer.ScreenAxis);
        return 1f + pixels / Math.Max(24f, pointer.ScreenAxisLength);
    }

    private bool TryResolveSpatialPlaneDelta(
        SpatialTransformPointerSession pointer,
        Point screen,
        out Vector3 delta)
    {
        delta = default;
        if (pointer.PlaneStartPoint is not { } start
            || !TryGetSpatialPlanePoint(
                screen,
                pointer.Origin,
                pointer.PlaneNormal,
                out var current))
        {
            return false;
        }

        var raw = current - start;
        delta = raw - pointer.PlaneNormal * Vector3.Dot(raw, pointer.PlaneNormal);
        return float.IsFinite(delta.X) && float.IsFinite(delta.Y) && float.IsFinite(delta.Z);
    }

    private bool TryCreateSpatialAxisDragPlane(
        Point screen,
        Vector3 axis,
        out Vector3 normal)
    {
        normal = default;
        if (!_stage.TryGetReferenceRay(screen, out var ray)) return false;
        var perpendicular = ray.Direction - axis * Vector3.Dot(axis, ray.Direction);
        if (perpendicular.LengthSquared()
            < MinimumSpatialAxisPlaneRayDot * MinimumSpatialAxisPlaneRayDot)
        {
            return false;
        }
        normal = Vector3.Normalize(perpendicular);
        return float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z);
    }

    private bool TryGetAxisPlaneParameter(
        Point screen,
        Vector3 origin,
        Vector3 axis,
        Vector3 planeNormal,
        out float parameter)
    {
        parameter = 0;
        if (!TryGetSpatialPlanePoint(screen, origin, planeNormal, out var point)) return false;
        parameter = Vector3.Dot(point - origin, axis);
        return float.IsFinite(parameter);
    }

    private bool TryGetSpatialPlanePoint(
        Point screen,
        Vector3 origin,
        Vector3 normal,
        out Vector3 point)
    {
        point = default;
        if (!_stage.TryGetReferenceRay(screen, out var ray)) return false;
        var denominator = Vector3.Dot(ray.Direction, normal);
        if (Math.Abs(denominator) < StageControl.SpatialGizmoMinimumPlaneRayDot) return false;
        var distance = Vector3.Dot(origin - ray.Origin, normal) / denominator;
        if (!float.IsFinite(distance) || distance < 0) return false;
        point = ray.Origin + ray.Direction * distance;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    private bool TryGetRotationPlaneVector(
        Point screen,
        Vector3 origin,
        Vector3 axis,
        out Vector3 vector)
    {
        vector = default;
        if (!_stage.TryGetReferenceRay(screen, out var ray)) return false;
        var denominator = Vector3.Dot(ray.Direction, axis);
        if (Math.Abs(denominator) < StageControl.SpatialGizmoMinimumPlaneRayDot) return false;
        var distance = Vector3.Dot(origin - ray.Origin, axis) / denominator;
        var intersection = ray.Origin + ray.Direction * distance;
        var offset = intersection - origin;
        if (offset.LengthSquared() <= 0.000001f) return false;
        vector = Vector3.Normalize(offset);
        return true;
    }

    private static bool IsSpatialPlaneAxis(SpatialTransformAxis axis)
    {
        return axis is SpatialTransformAxis.XY or SpatialTransformAxis.XZ or SpatialTransformAxis.YZ;
    }

    private static float ScreenAngle(Point point, PointF origin)
    {
        return MathF.Atan2(point.Y - origin.Y, point.X - origin.X);
    }

    internal static Vector3 ApplySpatialPrecisionDelta(
        ref Vector3 previousRaw,
        ref Vector3 effective,
        Vector3 raw,
        bool precision)
    {
        if (!Finite(raw)) return effective;
        var increment = raw - previousRaw;
        var offset = effective - previousRaw;
        previousRaw = raw;
        if (!Finite(increment)) return effective;
        // Preserve the raw anchor on ordinary drags, even after large excursions.
        effective = precision ? effective + increment * 0.1f : raw + offset;
        if (!Finite(effective)) effective = raw;
        return effective;
    }

    internal static float ApplySpatialPrecisionDelta(
        ref float previousRaw,
        ref float effective,
        float raw,
        bool precision)
    {
        if (!float.IsFinite(raw)) return effective;
        var increment = raw - previousRaw;
        var offset = effective - previousRaw;
        previousRaw = raw;
        if (!float.IsFinite(increment)) return effective;
        effective = precision ? effective + increment * 0.1f : raw + offset;
        if (!float.IsFinite(effective)) effective = raw;
        return effective;
    }

    private void RefreshSpatialTransformPreview()
    {
        _sceneInstancePreviewDirty = true;
        InvalidateSceneCompositionCache();
        RebuildSceneComposition(refreshScenePanels: false);
        UpdateInspector();
    }

    private void ClearSpatialTransformKeyboardTracking()
    {
        var keyboardTracked = _spatialTransformKeyboardActive
            || _spatialTransformPointerSession?.KeyboardDriven == true;
        _spatialTransformKeyboardActive = false;
        _spatialTransformKeyboardAxis = SpatialTransformAxis.None;
        if (!keyboardTracked) return;

        if (_spatialTransformPointerSession?.KeyboardDriven == true)
        {
            _spatialTransformPointerSession = null;
        }
        _stage.SetSpatialTransformActive(SpatialTransformHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _lastMouse = null;
        _startScreen = null;
    }

    private bool BeginSpatialTransformKeyboard(SpatialTransformMode mode)
    {
        SetSpatialTransformMode(mode, activateTool: true);
        if (SelectedSceneInstances().Count == 0) return true;

        BeginSpatialTransformEdit();
        if (_spatialTransformEditSession is null) return true;

        _spatialTransformKeyboardActive = true;
        _spatialTransformKeyboardAxis = SpatialTransformAxis.None;
        _lastMouse = null;
        _startScreen = null;
        _stage.Focus();
        RefreshInteractionCursorAtPointer();
        return true;
    }

    private void CompleteSpatialTransformKeyboard()
    {
        EndSpatialTransformKeyboard(commit: true);
    }

    private void CancelSpatialTransformKeyboard()
    {
        EndSpatialTransformKeyboard(commit: false);
    }

    private void EndSpatialTransformKeyboard(bool commit)
    {
        if (!_spatialTransformKeyboardActive) return;
        _spatialTransformKeyboardActive = false;
        _spatialTransformKeyboardAxis = SpatialTransformAxis.None;
        if (_spatialTransformPointerSession?.KeyboardDriven == true)
        {
            _spatialTransformPointerSession = null;
        }
        _stage.SetSpatialTransformActive(SpatialTransformHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _lastMouse = null;
        _startScreen = null;
        if (commit) CompleteSpatialTransformEdit();
        else CancelSpatialTransformEdit();
        RefreshInteractionCursorAtPointer();
    }

    private bool SelectSpatialTransformKeyboardAxis(SpatialTransformAxis axis)
    {
        if (!_spatialTransformKeyboardActive
            || axis is not (SpatialTransformAxis.X or SpatialTransformAxis.Y or SpatialTransformAxis.Z))
        {
            return false;
        }
        if (_spatialTransformKeyboardAxis == axis) return true;

        if (_spatialTransformKeyboardAxis != SpatialTransformAxis.None)
        {
            var mode = _spatialTransformMode;
            CancelSpatialTransformKeyboard();
            BeginSpatialTransformKeyboard(mode);
            if (!_spatialTransformKeyboardActive) return true;
        }

        _spatialTransformKeyboardAxis = axis;
        var pointer = _stage.PointToClient(Cursor.Position);
        if (_stage.ClientRectangle.Contains(pointer))
        {
            UpdateSpatialTransformKeyboardPointer(pointer);
        }
        return true;
    }

    private bool UpdateSpatialTransformKeyboardPointer(Point screen)
    {
        if (!_spatialTransformKeyboardActive) return false;
        if (_spatialTransformKeyboardAxis == SpatialTransformAxis.None)
        {
            UpdateSpatialTransformCursor(screen);
            return true;
        }

        if (_spatialTransformPointerSession is null)
        {
            TryBeginSpatialTransformPointer(
                screen,
                new SpatialTransformHandleHit(_spatialTransformMode, _spatialTransformKeyboardAxis),
                keyboardDriven: true);
            return true;
        }
        if (!_spatialTransformPointerSession.KeyboardDriven) return false;

        _lastMouse = screen;
        UpdateSpatialTransformPointer(screen);
        UpdateSpatialTransformCursor(screen);
        return true;
    }

    private bool HandleSpatialTransformKeyboardMouseDown(MouseEventArgs e)
    {
        if (!_spatialTransformKeyboardActive) return false;
        if (e.Button == MouseButtons.Right) CancelSpatialTransformKeyboard();
        return true;
    }

    private bool HandleSpatialTransformKeyboardMouseUp(MouseEventArgs e)
    {
        if (!_spatialTransformKeyboardActive) return false;
        if (e.Button == MouseButtons.Left)
        {
            UpdateSpatialTransformKeyboardPointer(e.Location);
            CompleteSpatialTransformKeyboard();
        }
        else if (e.Button == MouseButtons.Right)
        {
            CancelSpatialTransformKeyboard();
        }
        return true;
    }

    private void CompleteSpatialTransformPointer()
    {
        var pointer = _spatialTransformPointerSession;
        if (pointer is null)
        {
            ClearPendingSpatialTransformPointer();
            return;
        }
        if (pointer.KeyboardDriven)
        {
            ClearPendingSpatialTransformPointer();
            CompleteSpatialTransformKeyboard();
            return;
        }
        ApplyPendingSpatialTransformPointer();
        ClearPendingSpatialTransformPointer();
        _spatialTransformPointerSession = null;
        _stage.SetSpatialTransformActive(SpatialTransformHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _stage.ClearSceneSnapIndicator();
        CompleteSpatialTransformEdit();
        _lastMouse = null;
        _startScreen = null;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void CancelSpatialTransformPointer()
    {
        ClearPendingSpatialTransformPointer();
        var pointer = _spatialTransformPointerSession;
        if (pointer is null) return;
        if (pointer.KeyboardDriven)
        {
            CancelSpatialTransformKeyboard();
            return;
        }
        _spatialTransformPointerSession = null;
        _stage.SetSpatialTransformActive(SpatialTransformHandleHit.None);
        _stage.EndReference3DOpticalInteractionPreview();
        _stage.ClearSceneSnapIndicator();
        CancelSpatialTransformEdit();
        _lastMouse = null;
        _startScreen = null;
        if (_stage.Capture) _stage.Capture = false;
        RefreshInteractionCursorAtPointer();
    }

    private void UpdateSpatialTransformPresentation()
    {
        CancelSpatialTransformKeyboardIfSelectionChanged();
        var transformContextAvailable = IsScene3DView() && _tool == ToolMode.Transform3D;
        var instances = SelectedSceneInstances();
        if (!transformContextAvailable || instances.Count == 0)
        {
            _stage.ClearSpatialTransformGizmo(
                transformContextAvailable
                    ? SpatialGizmoMotion.Animated
                    : SpatialGizmoMotion.Immediate);
            if (!IsSceneReferenceView()) _stage.ClearReference3DSelection();
            return;
        }

        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        var states = instances.Select(instance => instance.EvaluateState(_frame)).ToArray();
        if (states.Length == 0)
        {
            _stage.ClearSpatialTransformGizmo(SpatialGizmoMotion.Animated);
            return;
        }
        if (_spatialPivotKind != SpatialPivotKind.None && states.Length != 1)
        {
            _stage.ClearSpatialTransformGizmo(SpatialGizmoMotion.Immediate);
            return;
        }
        var displayMode = _spatialPivotKind == SpatialPivotKind.None
            ? _spatialTransformMode
            : SpatialTransformMode.Move;
        var origins = states.Select(state => _spatialPivotKind switch
        {
            SpatialPivotKind.Rotation => DrawingObjectInstanceDefinition.RotationPivotScenePosition(state),
            SpatialPivotKind.Scale => DrawingObjectInstanceDefinition.ScalePivotScenePosition(state),
            _ when _spatialTransformMode == SpatialTransformMode.Rotate =>
                DrawingObjectInstanceDefinition.RotationPivotScenePosition(state),
            _ when _spatialTransformMode == SpatialTransformMode.Scale =>
                DrawingObjectInstanceDefinition.ScalePivotScenePosition(state),
            _ => new Vector3(state.X, state.Y, state.Z)
        }).ToArray();
        var origin = new Vector3(
            origins.Average(point => point.X),
            origins.Average(point => point.Y),
            origins.Average(point => point.Z));
        var effectiveSpace = _spatialPivotKind != SpatialPivotKind.None
            || _spatialTransformMode == SpatialTransformMode.Scale
            ? SpatialTransformSpace.Local
            : _spatialTransformSpace;
        var primary = SelectedSceneInstance();
        var basis = _spatialTransformPointerSession?.Basis
            ?? (effectiveSpace == SpatialTransformSpace.Local && primary is not null
                ? SpatialTransformMath.CreateBasis(primary.EvaluateState(_frame))
                : SpatialGizmoBasis.Identity);
        _stage.SetSpatialTransformGizmo(
            origin,
            displayMode,
            _selectedSceneInstanceObjectIndices,
            basis,
            SpatialGizmoMotion.Animated);
    }

    private static Vector3 QuantizeSpatialPivot(Vector3 value)
    {
        return new Vector3(
            VectorUnits.Quantize(value.X),
            VectorUnits.Quantize(value.Y),
            VectorUnits.Quantize(value.Z));
    }

    private static bool HasSpatialComponentDelta(float value)
    {
        return Math.Abs(value) > 0.000001f;
    }

    private static bool Finite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private void CancelSpatialTransformKeyboardIfSelectionChanged()
    {
        var session = _spatialTransformEditSession;
        if (!_spatialTransformKeyboardActive || session is null) return;

        var selected = SelectedSceneInstances();
        var selectedIds = selected.Select(instance => instance.Id).ToArray();
        if (selectedIds.Length == session.SelectedInstanceIds.Length
            && selectedIds.All(id => session.SelectedInstanceIds.Contains(id, StringComparer.Ordinal))
            && string.Equals(_selectedSceneInstanceId, session.PrimaryInstanceId, StringComparison.Ordinal))
        {
            return;
        }

        var primaryId = _selectedSceneInstanceId;
        CancelSpatialTransformKeyboard();
        var resolved = ResolveTimelineInstanceSelection(ActiveEditableInstances(), selectedIds, primaryId);
        if (resolved.Instances.Length > 0) SetSceneInstanceSelection(resolved.Instances, resolved.Primary);
        else ClearSelection();
        UpdateInspector();
    }

    private bool UpdateProjectedSceneSelectionPresentation()
    {
        if (!IsSceneReferenceView()) return false;
        _stage.SetDrawingObjectSelectionOverlay(RectangleF.Empty);
        _stage.SetReference3DSelection(_selectedSceneInstanceObjectIndices);
        UpdateSpatialTransformPresentation();
        return true;
    }

    private bool UpdateSpatialTransformCursor(Point screen)
    {
        if (CanMoveProjectedSceneInstances() && _tool == ToolMode.Select)
        {
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            _stage.ClearBrushTipCursor();
            _stage.ClearFillToolCursor();
            ClearFillHoverPreview();
            _stage.Cursor = _projectedSceneMoveStartRayOrigin is not null
                || TryResolveProjectedSceneInstance(screen, out _)
                    ? Cursors.SizeAll
                    : Cursors.Default;
            return true;
        }

        if (!IsScene3DView() || _tool != ToolMode.Transform3D)
        {
            _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
            return false;
        }
        _stage.ClearBrushTipCursor();
        _stage.ClearFillToolCursor();
        ClearFillHoverPreview();
        var hit = _stage.HitTestSpatialTransformGizmo(screen);
        if (_spatialTransformPointerSession is null) _stage.SetSpatialTransformHover(hit);
        _stage.Cursor = _spatialTransformKeyboardActive
            || _spatialTransformPointerSession is not null
            || hit.IsValid
                ? Cursors.SizeAll
                : Cursors.Default;
        return true;
    }

    private bool FocusSelectedSceneInstance()
    {
        if (!IsScene3DView() || SelectedSceneInstances().Count == 0) return false;
        if (_selectedSceneInstanceObjectIndices.Length == 0) RefreshSelectedSceneInstanceObjectIndices();
        if (!_stage.TryGetReference3DFocusPoints(
                _selectedSceneInstanceObjectIndices,
                out var scenePoints))
        {
            return false;
        }

        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        return _stage.FocusReferenceCamera(scenePoints, ReferenceCameraMotion.Animated);
    }

    private bool HandleSpatialTransformShortcut(Keys keyData)
    {
        if (!IsScene3DView()) return false;
        if ((keyData & Keys.Modifiers) != Keys.None) return false;
        var key = keyData & Keys.KeyCode;
        var mode = key switch
        {
            Keys.G => SpatialTransformMode.Move,
            Keys.R => SpatialTransformMode.Rotate,
            Keys.S => SpatialTransformMode.Scale,
            _ => (SpatialTransformMode?)null
        };
        if (_spatialTransformKeyboardActive)
        {
            if (mode is { } activeMode)
            {
                if (_spatialTransformMode == activeMode) return true;
                CancelSpatialTransformKeyboard();
                return BeginSpatialTransformKeyboard(activeMode);
            }

            var axis = key switch
            {
                Keys.X => SpatialTransformAxis.X,
                Keys.Y => SpatialTransformAxis.Y,
                Keys.Z => SpatialTransformAxis.Z,
                _ => SpatialTransformAxis.None
            };
            if (axis != SpatialTransformAxis.None) return SelectSpatialTransformKeyboardAxis(axis);
            if (key == Keys.Enter)
            {
                CompleteSpatialTransformKeyboard();
                return true;
            }
            if (key == Keys.Escape)
            {
                CancelSpatialTransformKeyboard();
                return true;
            }
            if (key == Keys.Space) return true;
            return false;
        }

        return mode is { } nextMode && BeginSpatialTransformKeyboard(nextMode);
    }

    private bool TryGetSceneBasePlanePoint(Point screen, out PointF world)
    {
        world = PointF.Empty;
        if (!IsSceneReferenceView())
        {
            world = _stage.ScreenToWorld(screen);
            return float.IsFinite(world.X) && float.IsFinite(world.Y);
        }

        if (!_stage.TryGetReferenceRay(screen, out var ray)
            || Math.Abs(ray.Direction.Z) <= 0.00001f)
        {
            return false;
        }

        var distance = -ray.Origin.Z / ray.Direction.Z;
        if (!float.IsFinite(distance) || distance < 0) return false;
        var point = ray.Origin + ray.Direction * distance;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        world = VectorUnits.Quantize(new PointF(point.X, point.Y));
        return true;
    }
}
