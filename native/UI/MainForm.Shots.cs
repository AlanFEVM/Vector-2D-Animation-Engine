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

    /// <summary>
    /// The selected workspace presents the scene stage. Shots &amp; Directing shares the scene
    /// presentation with Scene & Animation and replaces the right inspector with the camera surface.
    /// </summary>
    private bool IsSceneWorkspaceSelected =>
        _workspaceTabs.SelectedView is WorkspaceView.SceneEditor or WorkspaceView.ShotDirector;

    private void InitializeShotDirector()
    {
        _shotDirectorPanel.Dock = DockStyle.Fill;
        _shotDirectorPanel.Visible = false;
        _shotDirectorPanel.NewShotRequested += (_, _) => AddSceneShot();
        _shotDirectorPanel.ShotActivated += (_, e) => ActivateSceneShot(e.ShotId);
        _shotDirectorPanel.ShotLocateRequested += (_, e) => LocateSceneShot(e.ShotId);
        _shotDirectorPanel.ShotRemoveRequested += (_, e) => RemoveSceneShot(e.ShotId);
        _shotDirectorPanel.ShotMoveRequested += (_, e) => MoveSceneShot(e.ShotId, e.TargetIndex);
        _shotDirectorPanel.ShotPropertiesCommitted += (_, e) => ApplySceneShotProperties(e);
        _shotDirectorPanel.ShotEditStarted += (_, _) => BeginSceneShotEdit(ActiveScene());
        _shotDirectorPanel.ShotEditCompleted += (_, _) => CompleteSceneShotEdit();
        _shotDirectorPanel.ShotFramingCommitted += (_, e) => ApplySceneShotFraming(e);
        _shotDirectorPanel.ShotFramingResetRequested += (_, e) => ResetSceneShotFraming(e.ShotId);

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
        // A completed user resize changes the preferred monitor size and needs a re-capture at the
        // new pixel size, otherwise the frame would be scaled from the previous resolution.
        _shotPreviewOverlay.PreviewResized += (_, _) =>
        {
            _shotPreviewPreferredSize = _shotPreviewOverlay.Size;
            RefreshShotPreview(force: true);
        };

        void LayoutShotPreviewOverlay()
        {
            var margin = Math.Max(8, (int)MathF.Round(ShotPreviewOverlay.OverlayMargin * stagePanel.DeviceDpi / 96f));
            // The operator's chosen size wins. Only the physical stage bounds may shrink it.
            var preferred = _shotPreviewPreferredSize;
            var width = Math.Max(
                ShotPreviewOverlay.MinimumOverlayWidth,
                Math.Min(preferred.Width, Math.Max(120, stagePanel.ClientSize.Width - margin * 2)));
            var height = Math.Max(
                ShotPreviewOverlay.MinimumOverlayHeight,
                Math.Min(preferred.Height, Math.Max(90, stagePanel.ClientSize.Height - margin * 2)));
            if (_shotPreviewOverlay.Size != new Size(width, height))
                _shotPreviewOverlay.Size = new Size(width, height);
            _shotPreviewOverlay.Left = margin;
            // The overlay floats in the stage area, whose bottom edge already sits directly above the
            // timeline panel, so a plain bottom margin keeps the monitor above the timeline strip.
            _shotPreviewOverlay.Top = Math.Max(
                metrics.Visible ? metrics.Bottom + margin : margin,
                stagePanel.ClientSize.Height - height - margin);
        }

        stagePanel.Resize += (_, _) => LayoutShotPreviewOverlay();
        metrics.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        metrics.VisibleChanged += (_, _) => LayoutShotPreviewOverlay();
        _inspectorHost.SizeChanged += (_, _) => LayoutShotPreviewOverlay();
        LayoutShotPreviewOverlay();
    }

    /// <summary>Shows the director content only inside the shot workspace and keeps its width responsive.</summary>
    private void UpdateShotDirectorWorkspace(WorkspaceView? selectedView = null)
    {
        if (!_shotDirectorInitialized) return;
        var view = selectedView ?? _workspaceTabs.SelectedView;
        var visible = view == WorkspaceView.ShotDirector;
        // Control.Visible reports effective visibility, so track the workspace intent explicitly.
        if (_shotDirectorVisible != visible)
        {
            _shotDirectorVisible = visible;
            _shotDirectorPanel.Visible = visible;
            // Joining or leaving the docked stack changes the stage area; re-run the body layout so the
            // stage preview reclaims or releases the panel width immediately.
            _shotDirectorPanel.Parent?.PerformLayout(_shotDirectorPanel, "Visible");
        }

        if (visible) RefreshShotDirectorWidth();
        _shotPreviewOverlay.Visible = visible;
        if (visible) _shotPreviewOverlay.BringToFront();
        UpdateShotFramingGizmo();
        RefreshShotPreview(force: true);
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
        var scene = IsSceneBuildingContext() ? ActiveScene() : null;
        NormalizeActiveShot(scene);
        ApplyShotTimelineFilter(scene);
        SynchronizeActiveShotStateFromTimeline(scene);
        NormalizeActiveShot(scene);
        var fingerprint = ComputeShotDirectorFingerprint(scene);
        if (!force && fingerprint == _shotDirectorFingerprint)
        {
            _shotDirectorPanel.SetPlayhead(_frame);
            UpdateShotFramingGizmo();
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

            return scene.UpdateShotAtFrame(shotId, editFrame, SceneShotSettings.Default);
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
            Shots = shots
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
                hideShotTracks: _workspaceTabs.SelectedView == WorkspaceView.SceneEditor
                    && scene is not null);
            return;
        }

        var activeCameraId = scene.FindShot(_activeShotId) is null ? null : _activeShotId;
        var cameraTrackIds = scene.Shots
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
            _sceneShotEditNeedsTimelineRefresh = false;
            return;
        }

        if (ReferenceEquals(_sceneShotEditScene, scene) && _sceneShotEditSnapshot is not null) return;
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
        var frame = _sceneShotEditFrame;
        var needsTimelineRefresh = _sceneShotEditNeedsTimelineRefresh;
        _sceneShotEditScene = null;
        _sceneShotEditSnapshot = null;
        _sceneShotEditTimelineSnapshot = null;
        _sceneShotEditNeedsTimelineRefresh = false;
        if (scene is null || before is null || timelineBefore is null) return;
        if (ShotSnapshotsEqual(before, scene)) return;
        PushSceneTimelineUndo(
            scene,
            timelineBefore,
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
        StartSceneShotTimelineEdit(scene);
        var shot = scene.AddShot(null, ResolveDefaultShotDuration(scene), detail: null);
        SetActiveShotState(scene, shot.Id);
        FinishSceneShotTimelineEdit(scene);
    }

    private void ActivateSceneShot(string shotId)
    {
        var scene = ActiveScene();
        if (scene is null || !CommitTextEdit()) return;
        if (string.Equals(_activeShotId, shotId, StringComparison.Ordinal))
        {
            RefreshShotDirector();
            return;
        }

        SetActiveShotState(scene, shotId);
        RefreshShotDirector(force: true);
        if (shotId.Length == 0) return;
        _timeline.SelectSingleLayerTarget(shotId, notifyActiveLayerChanged: false);
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
            || scene.FindShot(track.TargetId) is not { } shot)
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
        if (scene is null || shotId.Length == 0) return;
        if (!string.Equals(_activeShotId, shotId, StringComparison.Ordinal))
        {
            SetActiveShotState(scene, shotId);
            RefreshShotDirector(force: true);
        }

        _timeline.SelectSingleLayerTarget(shotId, notifyActiveLayerChanged: false);
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
