namespace VectorAnimationEngine;

// Director integration: the director selects independent camera objects while the scene remains a
// single continuous animation timeline. Camera state is stored on the camera track; the director
// workspace filters the presentation to those tracks without changing scene ownership.
internal sealed partial class MainForm
{
    private const int ShotDirectorPreferredWidth = 560;
    private const int ShotDirectorMinimumWidth = 360;
    private const int ShotDirectorMaximumWidth = 760;

    private readonly ShotDirectorPanel _shotDirectorPanel = new();
    private readonly ShotPreviewOverlay _shotPreviewOverlay = new();
    private Size _shotPreviewPreferredSize = new(ShotPreviewOverlay.OverlayWidth, ShotPreviewOverlay.OverlayHeight);
    private bool _layingOutShotPreview;
    private readonly Dictionary<string, string> _activeShotBySceneId = new(StringComparer.Ordinal);
    private string _activeShotId = "";
    private int _shotDirectorFingerprint = int.MinValue;
    private bool _shotDirectorInitialized;
    private bool _shotDirectorVisible;
    private SceneShotSnapshot[]? _sceneShotEditSnapshot;
    private AnimationTimelineSnapshot? _sceneShotEditTimelineSnapshot;
    private SceneDefinition? _sceneShotEditScene;
    private int _sceneShotEditFrame;
    private bool _sceneShotEditNeedsTimelineRefresh;
    private SceneShotSettings? _shotDirectorFramingPushed;
    private string _shotPreviewModeCameraId = "";

    /// <summary>
    /// The selected workspace presents the scene stage. Shots &amp; Directing shares the scene
    /// presentation with Scene & Animation and replaces the right inspector with the camera surface.
    /// </summary>
    private bool IsSceneWorkspaceSelected =>
        !IsSymbolEditing && _workspaceTabs.SelectedView is WorkspaceView.SceneEditor or WorkspaceView.ShotDirector;

    private void InitializeShotDirector()
    {
        _shotDirectorPanel.Dock = DockStyle.Fill;
        _shotDirectorPanel.Visible = false;
        _shotDirectorPanel.NewShotRequested += (_, _) => AddSceneShot();
        _shotDirectorPanel.CameraPreviewRequested += (_, _) => ToggleShotPreview();
        _shotDirectorPanel.PreviewModeChanged += (_, _) =>
        {
            if (HasActiveCanvasPointerInteraction())
            {
                _shotDirectorPanel.SetPreviewMode(_stage.ShotPreviewMode, _stage.CanPreviewPlanarShot);
                return;
            }
            CompleteSceneShotEdit();
            _stage.SetShotWorkspacePreviewMode(_shotDirectorPanel.PreviewMode);
            _shotDirectorPanel.SetPreviewMode(_stage.ShotPreviewMode, _stage.CanPreviewPlanarShot);
            ClearSelection();
            _stage.Focus();
        };
        _shotDirectorPanel.SetSimpleCameraStyle(_applicationSettings.SimpleCameraStyleEnabled);
        _shotDirectorPanel.CameraStyleChanged += (_, _) =>
        {
            FinishPointerInteractionForContextChange();
            _applicationSettings = _applicationSettings with
            {
                SimpleCameraStyleEnabled = _shotDirectorPanel.SimpleCameraStyleEnabled
            };
            if (!ApplicationSettingsStore.TrySave(_applicationSettings))
                AppLog.Warn("The camera style could not be saved; the current session will keep the selected style.");
            UpdateShotFramingGizmo();
        };
        _shotDirectorPanel.ShotActivated += (_, e) => ActivateSceneShot(e.ShotId);
        _shotDirectorPanel.ShotLocateRequested += (_, e) => LocateSceneShot(e.ShotId);
        _shotDirectorPanel.ShotRemoveRequested += (_, e) => RemoveSceneShot(e.ShotId);
        _shotDirectorPanel.ShotMoveRequested += (_, e) => MoveSceneShot(e.ShotId, e.TargetIndex);
        _shotDirectorPanel.ShotPropertiesCommitted += (_, e) => ApplySceneShotProperties(e);
        _shotDirectorPanel.ShotEditStarted += (_, _) => BeginSceneShotEdit(ActiveScene());
        _shotDirectorPanel.ShotEditCompleted += (_, _) => CompleteSceneShotEdit();
        _shotDirectorPanel.ShotFramingCommitted += (_, e) => ApplySceneShotFraming(e);
        _shotDirectorPanel.ShotFramingResetRequested += (_, e) => ResetSceneShotFraming(e.ShotId);
        InitializeCameraParallax();

        // The director is an inspector page, not another stage dock. Keeping it inside the existing
        // right host makes the stage reclaim the old left-side width and guarantees that the ordinary
        // scene inspector is replaced only while this workspace is active.
        _inspectorHost.Controls.Add(_shotDirectorPanel);
        _shotDirectorPanel.BringToFront();

        _shotDirectorInitialized = true;
    }

    /// <summary>
    /// Hosts the floating shot monitor on the Stage. It is pinned to the lower-left of the stage
    /// area, above the timeline, instead of occupying the top of the right director inspector.
    /// The operator can resize it from its lower-right grip; that size is kept as the new preferred
    /// size and only shrunk when the stage itself becomes too small to hold it.
    /// </summary>
    private void AttachShotPreviewOverlay(Panel stagePanel, Control metrics)
    {
        _shotPreviewOverlay.Visible = false;
        _shotPreviewOverlay.Size = new Size(ShotPreviewOverlay.OverlayWidth, ShotPreviewOverlay.OverlayHeight);
        _shotPreviewOverlay.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        stagePanel.Controls.Add(_shotPreviewOverlay);
        _shotPreviewOverlay.BringToFront();
        _shotPreviewOverlay.PreviewToggleRequested += (_, _) => ToggleShotPreview();
        _shotPreviewOverlay.SizeChanged += (_, _) =>
        {
            if (_shotPreviewOverlay.IsResizing) LayoutShotPreviewOverlay();
        };
        _shotPreviewOverlay.VisibleChanged += (_, _) => UpdateShotPreviewScheduler();
        VisibleChanged += (_, _) => UpdateShotPreviewScheduler();
        Resize += (_, _) => UpdateShotPreviewScheduler();
        HandleCreated += (_, _) => UpdateShotPreviewScheduler();
        HandleDestroyed += (_, _) => StopShotPreviewScheduler();
        // A completed user resize changes the preferred monitor size and needs a re-capture at the
        // new pixel size, otherwise the frame would be scaled from the previous resolution.
        _shotPreviewOverlay.PreviewResized += (_, _) =>
        {
            _shotPreviewPreferredSize = _shotPreviewOverlay.Size;
            LayoutShotPreviewOverlay();
            RefreshShotPreview(force: true);
        };

        stagePanel.Resize += (_, _) => LayoutShotPreviewOverlay();
        _stage.LocationChanged += (_, _) => LayoutShotPreviewOverlay();
        _stage.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        _toolPalette.LocationChanged += (_, _) => LayoutShotPreviewOverlay();
        _toolPalette.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        _toolPalette.VisibleChanged += (_, _) => LayoutShotPreviewOverlay();
        _toolPalette.ParentChanged += (_, _) => LayoutShotPreviewOverlay();
        if (_vaultDrawer is { } drawer)
        {
            drawer.VisibleChanged += (_, _) => LayoutShotPreviewOverlay();
            drawer.RegionChanged += (_, _) => LayoutShotPreviewOverlay();
            drawer.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
            drawer.LocationChanged += (_, _) => LayoutShotPreviewOverlay();
        }
        _timeline.LocationChanged += (_, _) => LayoutShotPreviewOverlay();
        _timeline.VisibleChanged += (_, _) => LayoutShotPreviewOverlay();
        metrics.LocationChanged += (_, _) => LayoutShotPreviewOverlay();
        metrics.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        metrics.VisibleChanged += (_, _) => LayoutShotPreviewOverlay();
        _inspectorHost.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        LayoutShotPreviewOverlay();
    }

    private void LayoutShotPreviewOverlay()
    {
        if (_layingOutShotPreview || _shotPreviewOverlay.Parent is not Control stagePanel) return;
        _layingOutShotPreview = true;
        try
        {
            var margin = Math.Max(8, (int)MathF.Round(ShotPreviewOverlay.OverlayMargin * stagePanel.DeviceDpi / 96f));
            var available = Rectangle.Intersect(stagePanel.ClientRectangle, _stage.Bounds);
            available.Inflate(-margin, -margin);
            if (_stageMetrics.Visible)
                available = Rectangle.FromLTRB(available.Left, Math.Max(available.Top, _stageMetrics.Bottom + margin), available.Right, available.Bottom);
            if (_toolPalette.Visible && ReferenceEquals(_toolPalette.Parent, stagePanel))
            {
                var left = Math.Max(available.Left, _toolPalette.Right + margin);
                available = Rectangle.FromLTRB(left, available.Top, available.Right, available.Bottom);
            }
            if (_vaultDrawer is { Visible: true } drawer && ReferenceEquals(drawer.Parent, stagePanel.Parent))
            {
                var drawerWidth = Math.Clamp(_vaultDrawerVisibleWidth, 0, drawer.Width);
                var left = Math.Max(available.Left, drawer.Left + drawerWidth - stagePanel.Left + margin);
                available = Rectangle.FromLTRB(left, available.Top, available.Right, available.Bottom);
            }
            if (_timeline.Visible && ReferenceEquals(_timeline.Parent, this))
            {
                var timelineTop = _timeline.Top - (stagePanel.Parent?.Top ?? 0) - stagePanel.Top - margin;
                available = Rectangle.FromLTRB(available.Left, available.Top, available.Right, Math.Min(available.Bottom, timelineTop));
            }

            var visible = _shotDirectorVisible && SelectedTimelineShot() is not null
                && available.Width >= ShotPreviewOverlay.MinimumOverlayWidth
                && available.Height >= ShotPreviewOverlay.MinimumOverlayHeight;
            var wasVisible = _shotPreviewOverlay.Visible;
            _shotPreviewOverlay.Visible = visible;
            if (!visible) return;
            var preferred = _shotPreviewOverlay.Expanded ? available.Size
                : _shotPreviewOverlay.IsResizing ? _shotPreviewOverlay.Size : _shotPreviewPreferredSize;
            var size = new Size(Math.Min(preferred.Width, available.Width), Math.Min(preferred.Height, available.Height));
            var resized = _shotPreviewOverlay.Size != size;
            _shotPreviewOverlay.Bounds = new Rectangle(new Point(available.Left, available.Bottom - size.Height), size);
            _shotPreviewOverlay.BringToFront();
            if ((resized || !wasVisible) && !_shotPreviewOverlay.IsResizing) RefreshShotPreview(force: true);
        }
        finally
        {
            _layingOutShotPreview = false;
        }
    }

    private void ToggleShotPreview()
    {
        if (!_shotDirectorVisible || SelectedTimelineShot() is null || HasActiveCanvasPointerInteraction()) return;
        CompleteSceneShotEdit();
        _shotPreviewOverlay.Expanded = !_shotPreviewOverlay.Expanded;
        LayoutShotPreviewOverlay();
        _stage.Focus();
    }

    private bool HandleShotWorkspaceShortcut(Keys keyData)
    {
        if (keyData is not (Keys.Enter or Keys.Escape)) return false;
        if (!_stage.ContainsFocus && !_timeline.ContainsFocus && !_shotDirectorPanel.CameraListContainsFocus
            && !_shotPreviewOverlay.ContainsFocus) return false;
        if (ContainsFocusedEditor(this)) return false;
        if (keyData == Keys.Escape && _shotPreviewOverlay.Expanded)
        {
            ToggleShotPreview();
            return true;
        }
        if (keyData != Keys.Enter || !_shotDirectorVisible
            || ContainsFocusedButton(this) || HasActiveCanvasPointerInteraction()) return false;
        CompleteSceneShotEdit();
        TogglePlayback();
        return true;
    }

    /// <summary>Shows the director content only inside the shot workspace and keeps its width responsive.</summary>
    private void UpdateShotDirectorWorkspace(WorkspaceView? selectedView = null)
    {
        if (!_shotDirectorInitialized) return;
        var view = selectedView ?? _workspaceTabs.SelectedView;
        var visible = view == WorkspaceView.ShotDirector || view == WorkspaceView.SceneEditor && IsShotDirectorContext();
        // Control.Visible reports effective visibility, so track the workspace intent explicitly.
        if (_shotDirectorVisible != visible)
        {
            _shotDirectorVisible = visible;
            _shotDirectorPanel.Visible = _inspectorCategory == InspectorCategory.Camera && IsSceneWorkspaceSelected;
            // Joining or leaving the docked stack changes the stage area; re-run the body layout so the
            // stage preview reclaims or releases the panel width immediately.
            _shotDirectorPanel.Parent?.PerformLayout(_shotDirectorPanel, "Visible");
        }
        LayoutInspectorCategoryRail();

        if (visible) RefreshShotDirectorWidth();
        if (!visible || SelectedTimelineShot() is null) _shotPreviewOverlay.Expanded = false;
        LayoutShotPreviewOverlay();
        UpdateShotFramingGizmo();
    }

    private SceneShotDefinition? SelectedTimelineShot()
    {
        if (!IsSceneCompositionContext() || ActiveScene() is not { } scene
            || _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId ?? "") is not { } track
            || scene.FindShot(track.TargetId) is not { } shot || !IsSceneShotAllowed(shot)) return null;
        return shot;
    }

    private void RefreshIntegratedShotPresentation()
    {
        if (!IsSceneWorkspaceSelected) return;
        FinishPointerInteractionForContextChange();
        RefreshShotDirector(force: true);
        if (IsShotDirectorContext() && _tool is not (ToolMode.Select or ToolMode.Transform or ToolMode.Hand))
            _tool = ToolMode.Select;
        RefreshToolButtons();
        ApplyToolCursor();
        UpdateTransformOverlay();
        UpdateSceneInstanceSelectionOverlay();
        _stage.Invalidate();
    }

    private void HandleShotTabGroupChanged()
    {
        if (IsSceneWorkspaceSelected && IsSceneCompositionContext()
            && _timeline.Context.Timeline.ActiveTabGroupId == AnimationTimeline.ShotTabGroupId
            && SelectedTimelineShot() is null && ActiveScene() is { } scene)
        {
            var rememberedShotId = _activeShotBySceneId.GetValueOrDefault(scene.Id, "");
            var shot = scene.Shots.FirstOrDefault(candidate => candidate.Id == rememberedShotId && IsSceneShotAllowed(candidate))
                ?? scene.Shots.FirstOrDefault(IsSceneShotAllowed);
            if (shot is not null) _timeline.SelectSingleLayerTarget(shot.Id, notifyActiveLayerChanged: false);
        }
        RefreshIntegratedShotPresentation();
    }

    /// <summary>Keeps the right inspector host laid out after the director page is toggled.</summary>
    private void RefreshShotDirectorWidth()
    {
        if (!_shotDirectorInitialized) return;
        _shotDirectorPanel.Parent?.PerformLayout(_shotDirectorPanel, "Dock");
    }

    /// <summary>Director panel width for the available workbench width. Used by the responsive layout.</summary>
    internal static int ResolveShotDirectorWidth(int availableWidth)
    {
        var preferred = availableWidth * 2 / 5;
        return Math.Clamp(preferred, ShotDirectorMinimumWidth, ShotDirectorMaximumWidth);
    }

    /// <summary>Rebuilds the camera list and applies the director-only camera track presentation.</summary>
    private void RefreshShotDirector(bool force = false)
    {
        if (!_shotDirectorInitialized) return;
        _shotDirectorPanel.SetExperimental3DEnabled(_applicationSettings.Experimental3DEnabled);
        _shotDirectorPanel.SetSimpleCameraStyle(_applicationSettings.SimpleCameraStyleEnabled);
        var scene = IsSceneBuildingContext() ? ActiveScene() : null;
        NormalizeActiveShot(scene);
        ApplyShotTimelineFilter(scene);
        SynchronizeActiveShotStateFromTimeline(scene);
        UpdateShotDirectorWorkspace();
        var fingerprint = ComputeShotDirectorFingerprint(scene);
        if (!force && fingerprint == _shotDirectorFingerprint)
        {
            _shotDirectorPanel.SetPlayhead(_frame);
            UpdateShotFramingGizmo();
            RefreshShotPreview();
            return;
        }

        _shotDirectorFingerprint = fingerprint;
        _shotDirectorPanel.Bind(BuildShotDirectorState(scene));
        _shotDirectorFramingPushed = null;
        PushShotDirectorFraming();
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
    }

    /// <summary>Keeps the framed-viewport editors aligned with the framing evaluated at the playhead.</summary>
    private void PushShotDirectorFraming()
    {
        if (!_shotDirectorInitialized) return;
        var scene = IsSceneBuildingContext() ? ActiveScene() : null;
        var settings = SceneShotSettings.Default;
        var hasFraming = scene is not null
            && _activeShotId.Length > 0
            && scene.TryEvaluateShotSettings(_activeShotId, Math.Max(0, _frame), out settings);
        if (!hasFraming) settings = SceneShotSettings.Default;
        if (_shotDirectorFramingPushed == settings) return;
        _shotDirectorFramingPushed = settings;
        _shotDirectorPanel.SetSelectedShotFraming(
            settings,
            hasFraming && IsShotFramingEditable(scene!, _activeShotId));
    }

    private bool IsShotFramingEditable(SceneDefinition scene, string shotId)
    {
        if (scene.FindShot(shotId) is not { } shot || !IsSceneShotAllowed(shot)) return false;
        if (_timeline.AutoKeyframeEnabled) return true;
        var track = scene.FindShotTrack(shotId);
        if (track is null) return false;
        var exposure = track.EvaluateExposure(Math.Max(0, _frame));
        return exposure.HasContent && exposure.SourceKeyframeFrame >= 0;
    }

    /// <summary>
    /// Resolves the frame a framing edit applies to: the playhead with Auto Key on, otherwise the
    /// owning tween start or the held source keyframe, mirroring the scene-light edit semantics.
    /// </summary>
    private bool TryResolveSceneShotEditFrame(SceneDefinition scene, string shotId, out int editFrame)
    {
        editFrame = Math.Max(0, _frame);
        if (scene.FindShot(shotId) is not { } shot || !IsSceneShotAllowed(shot)) return false;
        if (_timeline.AutoKeyframeEnabled) return true;
        var track = scene.FindShotTrack(shotId);
        if (track is null) return false;
        var tween = track.EvaluateTween(editFrame);
        if (tween is { } span && editFrame > span.StartFrame && editFrame < span.EndFrame)
        {
            editFrame = span.StartFrame;
            return true;
        }

        var exposure = track.EvaluateExposure(editFrame);
        if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0) return false;
        editFrame = exposure.SourceKeyframeFrame;
        return true;
    }

    private void ApplySceneShotFraming(ShotDirectorFramingEventArgs e)
    {
        if (!_applicationSettings.Experimental3DEnabled && !e.Settings.PinsPlanarRotation) return;
        var scene = ActiveScene();
        if (scene is null || scene.FindShot(e.ShotId) is null) return;
        if (!TryResolveSceneShotEditFrame(scene, e.ShotId, out var editFrame))
        {
            PushShotDirectorFraming();
            return;
        }

        var continuousEdit = ReferenceEquals(_sceneShotEditScene, scene) && _sceneShotEditSnapshot is not null;
        ApplySceneShotEdit(scene, () =>
        {
            if (_timeline.AutoKeyframeEnabled && editFrame == Math.Max(0, _frame))
            {
                scene.InsertShotTimelineKeyframe(e.ShotId, editFrame);
            }

            return scene.UpdateShotAtFrame(e.ShotId, editFrame, e.Settings);
        }, refreshTimelineOnComplete: true);
        if (!continuousEdit)
        {
            _shotDirectorFramingPushed = null;
            PushShotDirectorFraming();
        }
        UpdateShotFramingGizmo();
        if (continuousEdit) return;

        // A one-shot edit completes synchronously in ApplySceneShotEdit. The complete path already
        // performs the expensive panel/timeline/preview synchronization, so only the composition
        // surface needs a normal invalidation here.
        _stage.Invalidate();
    }

    private void ResetSceneShotFraming(string shotId)
    {
        var scene = ActiveScene();
        if (scene is null || scene.FindShot(shotId) is null) return;
        if (!TryResolveSceneShotEditFrame(scene, shotId, out var editFrame)) return;
        ApplySceneShotEdit(scene, () =>
        {
            if (_timeline.AutoKeyframeEnabled && editFrame == Math.Max(0, _frame))
            {
                scene.InsertShotTimelineKeyframe(shotId, editFrame);
            }

            var settings = _applicationSettings.Experimental3DEnabled ? SceneShotSettings.Default
                : (SceneShotSettings.Default with { Kind = SceneShotCameraKind.TwoD }).EnforceKind();
            return scene.UpdateShotAtFrame(shotId, editFrame, settings);
        });
        _shotDirectorFramingPushed = null;
        PushShotDirectorFraming();
        _timeline.RefreshTimeline();
        _stage.Invalidate();
    }

    private void NormalizeActiveShot(SceneDefinition? scene)
    {
        if (scene is null)
        {
            _activeShotId = "";
            return;
        }

        _activeShotId = _activeShotBySceneId.GetValueOrDefault(scene.Id, "");
        if (_activeShotId.Length == 0) return;
        if (scene.FindShot(_activeShotId) is not null) return;
        _activeShotId = "";
        _activeShotBySceneId[scene.Id] = "";
    }

    private void SetActiveShotState(SceneDefinition scene, string shotId)
    {
        _activeShotId = shotId;
        _activeShotBySceneId[scene.Id] = shotId;
    }

    private int ComputeShotDirectorFingerprint(SceneDefinition? scene)
    {
        var hash = new HashCode();
        hash.Add(_activeShotId, StringComparer.Ordinal);
        hash.Add(_playbackSettings.Fps);
        hash.Add(_project.PlaybackEndFrame);
        hash.Add(scene is null);
        if (scene is null) return hash.ToHashCode();

        hash.Add(scene.Id, StringComparer.Ordinal);
        hash.Add(scene.FrameCount);
        hash.Add(scene.ShotCount);
        foreach (var layer in scene.Layers)
        {
            hash.Add(layer.Id);
            hash.Add(layer.Name);
            hash.Add(layer.ColorArgb);
            hash.Add(layer.ParallaxDepth);
            hash.Add(layer.ParallaxEnabled);
        }
        foreach (var shot in scene.Shots)
        {
            hash.Add(shot.Id, StringComparer.Ordinal);
            hash.Add(shot.Name, StringComparer.Ordinal);
            hash.Add(shot.Detail, StringComparer.Ordinal);
            hash.Add(shot.Settings);
            foreach (var keyframe in shot.StateKeyframes)
            {
                hash.Add(keyframe.Frame);
                hash.Add(keyframe.Settings);
            }

            if (scene.FindShotTrack(shot.Id) is { } track)
            {
                hash.Add(track.Duration);
                foreach (var keyframe in track.Keyframes)
                {
                    hash.Add(keyframe.Frame);
                    hash.Add((int)keyframe.Kind);
                }
                foreach (var tween in track.Tweens)
                {
                    hash.Add(tween.StartFrame);
                    hash.Add(tween.EndFrame);
                    hash.Add((int)tween.Kind);
                }
            }
        }

        return hash.ToHashCode();
    }

    private ShotDirectorState BuildShotDirectorState(SceneDefinition? scene)
    {
        if (scene is null)
        {
            return new ShotDirectorState
            {
                IsAvailable = false,
                ActiveShotId = "",
                CurrentFrame = _frame,
                FrameCount = Math.Max(1, _timeline.Context.FrameCount),
                PlaybackFps = _playbackSettings.Fps
            };
        }

        var frameCount = Math.Max(1, scene.FrameCount);
        var ranges = scene.GetShotRanges();
        var shots = new ShotDirectorItem[ranges.Length];
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            var shot = scene.FindShot(range.ShotId);
            var framing = shot is not null
                && scene.TryEvaluateShotSettings(range.ShotId, Math.Max(0, _frame), out var evaluatedFraming)
                    ? evaluatedFraming
                    : shot?.Settings ?? SceneShotSettings.Default;
            var track = shot is null ? null : scene.FindShotTrack(shot.Id);
            shots[index] = new ShotDirectorItem
            {
                Id = range.ShotId,
                Name = range.Name,
                Detail = shot?.Detail ?? "",
                DurationFrames = frameCount,
                StartFrame = range.StartFrame,
                EndFrame = range.EndFrame,
                ColorArgb = shot?.ColorArgb ?? SceneShotDefinition.DefaultColorArgb,
                IsActive = string.Equals(range.ShotId, _activeShotId, StringComparison.Ordinal),
                Framing = framing,
                FramingEditable = shot is not null && IsShotFramingEditable(scene, range.ShotId),
                KeyframeCount = track?.Keyframes.Count(keyframe => keyframe.Kind == TimelineKeyframeKind.Populated) ?? 0,
                TweenCount = track?.Tweens.Count(tween => tween.Kind == TimelineTweenKind.Classic) ?? 0
            };
        }

        return new ShotDirectorState
        {
            IsAvailable = true,
            ActiveShotId = _activeShotId,
            CurrentFrame = _frame,
            FrameCount = frameCount,
            PlaybackFps = _playbackSettings.Fps,
            ShotSequenceLength = scene.ShotSequenceLength,
            Shots = shots,
            ParallaxLayers = scene.Layers.Where(layer => layer.Kind == SceneLayerKind.Content)
                .Select(layer => new CameraParallaxLayer(layer.Id, layer.Name, layer.ColorArgb,
                    layer.ParallaxDepth, layer.ParallaxEnabled)).ToArray()
        };
    }

    /// <summary>Merges consecutive keyframes with the same kind into exposure segments clipped to the shot span.</summary>
    internal static ShotDirectorExposure[] BuildShotExposures(
        IReadOnlyList<TimelineKeyframe> keys,
        SceneShotRange range)
    {
        if (range.FrameCount <= 0) return [];
        var segments = new List<ShotDirectorExposure>();
        var index = 0;
        while (index < keys.Count && keys[index].Frame <= range.StartFrame) index++;
        var kind = index > 0 ? keys[index - 1].Kind : (TimelineKeyframeKind?)null;
        var frame = range.StartFrame;
        while (frame <= range.EndFrame)
        {
            var nextKeyFrame = index < keys.Count ? keys[index].Frame : int.MaxValue;
            var end = Math.Min(range.EndFrame, nextKeyFrame - 1);
            if (end < frame) end = frame;
            var hasContent = kind == TimelineKeyframeKind.Populated;
            if (segments.Count > 0 && segments[^1].HasContent == hasContent)
            {
                segments[^1] = segments[^1] with { EndFrame = end };
            }
            else
            {
                segments.Add(new ShotDirectorExposure(frame, end, hasContent));
            }

            frame = end + 1;
            if (index >= keys.Count) break;
            kind = keys[index].Kind;
            index++;
        }

        return segments.ToArray();
    }

    private void ApplyShotTimelineFilter(SceneDefinition? scene)
    {
        if (_workspaceTabs.SelectedView != WorkspaceView.ShotDirector || scene is null)
        {
            _timeline.ApplyShotFilter(
                null,
                null,
                null,
                hideShotTracks: false);
            return;
        }

        var activeCameraId = scene.FindShot(_activeShotId) is null ? null : _activeShotId;
        var cameraTrackIds = scene.Shots
            .Where(IsSceneShotAllowed)
            .Select(shot => shot.Id)
            .ToArray();
        _timeline.ApplyShotFilter(
            activeCameraId,
            cameraTrackIds,
            scene.GetShotRanges(),
            hideShotTracks: false);
        if (activeCameraId is not null)
        {
            _timeline.SelectSingleLayerTarget(activeCameraId, notifyActiveLayerChanged: false);
        }
    }

    /// <summary>
    /// The shot filter can choose the first visible camera without raising ActiveLayerChanged. Keep
    /// the director selection aligned with that stable timeline selection before binding the panel.
    /// </summary>
    private void SynchronizeActiveShotStateFromTimeline(SceneDefinition? scene)
    {
        if (_workspaceTabs.SelectedView == WorkspaceView.SceneEditor && scene is not null)
        {
            if (SelectedTimelineShot() is { } selectedShot) SetActiveShotState(scene, selectedShot.Id);
            else _activeShotId = "";
            return;
        }
        if (_workspaceTabs.SelectedView != WorkspaceView.ShotDirector
            || scene is null
            || string.IsNullOrWhiteSpace(_timeline.ActiveTrackId)
            || _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId) is not { } track
            || scene.FindShot(track.TargetId) is not { } shot)
        {
            return;
        }

        SetActiveShotState(scene, shot.Id);
    }

    // Shot editing ---------------------------------------------------------------

    private void BeginSceneShotEdit(SceneDefinition? scene)
    {
        if (scene is null)
        {
            _sceneShotEditScene = null;
            _sceneShotEditSnapshot = null;
            _sceneShotEditLayerSnapshot = null;
            _sceneShotEditNeedsTimelineRefresh = false;
            return;
        }

        if (ReferenceEquals(_sceneShotEditScene, scene) && _sceneShotEditSnapshot is not null) return;
        _sceneShotEditLayerSnapshot = null;
        _sceneShotEditScene = scene;
        _sceneShotEditSnapshot = scene.CreateShotSnapshot();
        _sceneShotEditTimelineSnapshot = scene.Timeline.CreateSnapshot();
        _sceneShotEditFrame = _frame;
        _sceneShotEditNeedsTimelineRefresh = false;
    }

    /// <summary>Aborts the running shot edit (gesture cancel) by restoring the captured snapshots.</summary>
    private void CancelSceneShotEdit()
    {
        var scene = _sceneShotEditScene;
        var shotSnapshot = _sceneShotEditSnapshot;
        var timelineSnapshot = _sceneShotEditTimelineSnapshot;
        var layerSnapshot = _sceneShotEditLayerSnapshot;
        _sceneShotEditLayerSnapshot = null;
        _sceneShotEditScene = null;
        _sceneShotEditSnapshot = null;
        _sceneShotEditTimelineSnapshot = null;
        _sceneShotEditNeedsTimelineRefresh = false;
        if (scene is null) return;
        if (timelineSnapshot is not null)
        {
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
        }

        if (shotSnapshot is not null) scene.RestoreShotSnapshot(shotSnapshot);
        if (layerSnapshot is not null) scene.RestoreLayerSnapshot(layerSnapshot);
        _shotDirectorFingerprint = int.MinValue;
        RefreshShotDirector(force: true);
        _timeline.RefreshTimeline();
        _stage.Invalidate();
    }

    private void CompleteSceneShotEdit()
    {
        var scene = _sceneShotEditScene;
        var before = _sceneShotEditSnapshot;
        var timelineBefore = _sceneShotEditTimelineSnapshot;
        var layersBefore = _sceneShotEditLayerSnapshot;
        _sceneShotEditLayerSnapshot = null;
        var frame = _sceneShotEditFrame;
        var needsTimelineRefresh = _sceneShotEditNeedsTimelineRefresh;
        _sceneShotEditScene = null;
        _sceneShotEditSnapshot = null;
        _sceneShotEditTimelineSnapshot = null;
        _sceneShotEditNeedsTimelineRefresh = false;
        if (scene is null || before is null || timelineBefore is null) return;
        if (ShotSnapshotsEqual(before, scene) && ParallaxLayersEqual(layersBefore, scene)) return;
        PushSceneTimelineUndo(
            scene,
            timelineBefore,
            layerSnapshot: layersBefore,
            playheadFrame: frame,
            shotSnapshot: before);
        RefreshShotDirector(force: true);
        if (needsTimelineRefresh) _timeline.RefreshTimeline();
    }

    /// <summary>Runs one shot mutation inside an edit session so a gesture produces a single undo entry.</summary>
    private void ApplySceneShotEdit(
        SceneDefinition scene,
        Func<bool> mutate,
        bool refreshTimelineOnComplete = false)
    {
        var ownsSession = !ReferenceEquals(_sceneShotEditScene, scene) || _sceneShotEditSnapshot is null;
        if (ownsSession) BeginSceneShotEdit(scene);
        if (refreshTimelineOnComplete) _sceneShotEditNeedsTimelineRefresh = true;
        if (!mutate())
        {
            if (ownsSession)
            {
                _sceneShotEditScene = null;
                _sceneShotEditSnapshot = null;
                _sceneShotEditTimelineSnapshot = null;
                _sceneShotEditNeedsTimelineRefresh = false;
            }

            return;
        }

        MarkProjectDirty();
        if (ownsSession)
        {
            CompleteSceneShotEdit();
            return;
        }
    }

    private static bool ShotSnapshotsEqual(SceneShotSnapshot[] before, SceneDefinition scene)
    {
        var after = scene.CreateShotSnapshot();
        if (before.Length != after.Length) return false;
        for (var index = 0; index < before.Length; index++)
        {
            var left = before[index];
            var right = after[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Detail, right.Detail, StringComparison.Ordinal)
                || left.DurationFrames != right.DurationFrames
                || left.Settings != right.Settings
                || !left.StateKeyframes.SequenceEqual(right.StateKeyframes))
            {
                return false;
            }
        }

        return true;
    }

    private void AddSceneShot()
    {
        var scene = ActiveScene();
        if (!CommitTextEdit() || scene is null) return;
        if (scene.ShotCount >= SceneDefinition.MaximumShots) return;
        StartSceneShotTimelineEdit(scene);
        var shot = scene.AddShot(null, ResolveDefaultShotDuration(scene), detail: null);
        if (!_applicationSettings.Experimental3DEnabled)
            scene.UpdateShotAtFrame(shot.Id, 0, (SceneShotSettings.Default with { Kind = SceneShotCameraKind.TwoD }).EnforceKind());
        SetActiveShotState(scene, shot.Id);
        _timeline.RefreshTimeline();
        _timeline.SelectSingleLayerTarget(shot.Id, notifyActiveLayerChanged: false);
        FinishSceneShotTimelineEdit(scene);
    }

    private void ActivateSceneShot(string shotId)
    {
        var scene = ActiveScene();
        if (scene is null || !CommitTextEdit()) return;
        if (shotId.Length > 0 && (scene.FindShot(shotId) is not { } shot || !IsSceneShotAllowed(shot))) return;
        if (string.Equals(_activeShotId, shotId, StringComparison.Ordinal))
        {
            RefreshShotDirector();
            return;
        }

        SetActiveShotState(scene, shotId);
        if (shotId.Length > 0) _timeline.SelectSingleLayerTarget(shotId, notifyActiveLayerChanged: false);
        RefreshIntegratedShotPresentation();
        _timeline.RefreshTimeline();
    }

    /// <summary>
    /// Keeps a camera timeline row independent from SceneDefinition.ActiveLayerId. Camera rows are
    /// selectable timeline targets, but a camera observes the complete scene and must not switch the
    /// editable scene layer when the row is clicked.
    /// </summary>
    private bool SynchronizeSceneShotSelectionFromTimeline()
    {
        if (!IsSceneCompositionContext()
            || ActiveScene() is not { } scene
            || string.IsNullOrWhiteSpace(_timeline.ActiveTrackId)
            || _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId) is not { } track
            || scene.FindShot(track.TargetId) is not { } shot
            || !IsSceneShotAllowed(shot))
        {
            return false;
        }

        ClearSceneOpticsLightSelection();
        var changed = !string.Equals(_activeShotId, shot.Id, StringComparison.Ordinal);
        SetActiveShotState(scene, shot.Id);
        RefreshShotDirector(force: changed);
        if (changed) _timeline.RefreshTimeline();
        else UpdateShotFramingGizmo();
        return true;
    }

    private void LocateSceneShot(string shotId)
    {
        var scene = ActiveScene();
        if (scene?.FindShot(shotId) is not { } shot || !IsSceneShotAllowed(shot)) return;
        if (!string.Equals(_activeShotId, shotId, StringComparison.Ordinal))
        {
            SetActiveShotState(scene, shotId);
            RefreshShotDirector(force: true);
        }

        _timeline.SelectSingleLayerTarget(shotId, notifyActiveLayerChanged: false);
        RefreshIntegratedShotPresentation();
        _timeline.ScrollFrameIntoView(_frame);
        _timeline.RefreshTimeline();
        _stage.Invalidate();
    }

    private void EnsurePlayheadInsideShot(SceneDefinition scene, SceneShotRange range, bool forceLocate)
    {
        if (range.FrameCount <= 0) return;
        TryExtendPlaybackRangeToShot(scene, range);
        if (!forceLocate && _frame >= range.StartFrame && _frame <= range.EndFrame)
        {
            _timeline.ScrollFrameIntoView(_frame);
            return;
        }

        SetFrame(range.StartFrame);
        _timeline.ScrollFrameIntoView(range.StartFrame);
        _timeline.EnsureCurrentFrameVisible();
        _stage.Invalidate();
    }

    private bool TryExtendPlaybackRangeToShot(SceneDefinition scene, SceneShotRange range)
    {
        if (range.FrameCount <= 0) return false;
        var lastFrame = Math.Max(0, _timeline.Context.FrameCount - 1);
        var start = Math.Max(0, Math.Min(_playbackSettings.StartFrame, range.StartFrame));
        var end = Math.Clamp(Math.Max(_playbackSettings.EndFrame, range.EndFrame), start, lastFrame);
        if (start == _playbackSettings.StartFrame && end == _playbackSettings.EndFrame) return false;
        _playbackSettings.SetFrameRange(start, end, notifyChanged: false);
        SyncTimelineFrameRange();
        PersistProjectPlaybackRange();
        return true;
    }

    private void RemoveSceneShot(string shotId)
    {
        var scene = ActiveScene();
        if (scene is null || scene.FindShot(shotId) is null) return;
        if (!IsSceneShotAllowed(scene.FindShot(shotId)!)) return;
        StartSceneShotTimelineEdit(scene);
        if (!scene.TryRemoveShot(shotId))
        {
            _sceneShotEditScene = null;
            _sceneShotEditSnapshot = null;
            _sceneShotEditNeedsTimelineRefresh = false;
            return;
        }

        if (string.Equals(_activeShotId, shotId, StringComparison.Ordinal)) SetActiveShotState(scene, "");
        FinishSceneShotTimelineEdit(scene);
    }

    private void MoveSceneShot(string shotId, int targetIndex)
    {
        var scene = ActiveScene();
        if (scene is null || scene.FindShot(shotId) is null) return;
        if (!IsSceneShotAllowed(scene.FindShot(shotId)!)) return;
        StartSceneShotTimelineEdit(scene);
        if (!scene.TryMoveShot(shotId, targetIndex))
        {
            _sceneShotEditScene = null;
            _sceneShotEditSnapshot = null;
            _sceneShotEditNeedsTimelineRefresh = false;
            return;
        }

        FinishSceneShotTimelineEdit(scene);
    }

    private void ApplySceneShotProperties(ShotDirectorPropertiesEventArgs e)
    {
        var scene = ActiveScene();
        if (scene is null || scene.FindShot(e.ShotId) is null) return;
        if (!IsSceneShotAllowed(scene.FindShot(e.ShotId)!)) return;
        ApplySceneShotEdit(scene, () =>
        {
            var changed = scene.TryRenameShot(e.ShotId, e.Name);
            changed |= scene.TrySetShotDetail(e.ShotId, e.Detail);
            changed |= scene.TrySetShotDuration(e.ShotId, e.DurationFrames);
            return changed;
        });
    }

    private void FitTimelineToShotSequence()
    {
        var scene = ActiveScene();
        if (scene is null) return;
        if (scene.ShotSequenceLength <= scene.FrameCount) return;
        var previousLastFrame = Math.Max(0, scene.FrameCount - 1);
        StartSceneShotTimelineEdit(scene);
        if (!scene.TryExtendTimelineToShotSequence())
        {
            _sceneShotEditScene = null;
            _sceneShotEditSnapshot = null;
            _sceneShotEditNeedsTimelineRefresh = false;
            return;
        }

        FinishSceneShotTimelineEdit(scene, previousLastFrame);
        if (scene.ShotCount > 0)
        {
            TryExtendPlaybackRangeToShot(scene, scene.GetShotRangeAt(scene.ShotCount - 1));
        }
    }

    private void StartSceneShotTimelineEdit(SceneDefinition scene)
    {
        BeginSceneShotEdit(scene);
        _sceneShotEditFrame = _frame;
    }

    private void FinishSceneShotTimelineEdit(SceneDefinition scene, int previousLastFrame = -1)
    {
        CompleteSceneShotEdit();
        _timeline.RefreshTimeline();
        if (previousLastFrame >= 0) ApplyBoundTimelineDuration(previousLastFrame, refreshClampedFrame: false);
        ApplyShotTimelineFilter(scene);
        RebuildSceneComposition();
        _shotDirectorPanel.RefreshSummary();
        UpdateInspector();
        _stage.Invalidate();
    }

    private bool AssignActiveLayerToActiveShot(SceneDefinition scene)
    {
        // Cameras observe the complete scene; adding a scene layer never changes camera ownership.
        return false;
    }

    internal static int ResolveDefaultShotDuration(SceneDefinition scene)
    {
        return Math.Clamp(
            Math.Max(1, scene.FrameCount),
            SceneShotDefinition.MinimumDurationFrames,
            SceneShotDefinition.MaximumDurationFrames);
    }
}
