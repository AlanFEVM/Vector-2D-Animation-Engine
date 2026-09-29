namespace VectorAnimationEngine;

// Independent camera management for the one scene. Cameras are timeline targets, not owners of
// scene layers. The legacy layer and sequence helpers remain as compatibility shims for older
// callers and documents but intentionally return no ownership information.
internal sealed partial class SceneDefinition
{
    internal const int MaximumShots = 512;

    private readonly List<SceneShotDefinition> _shots = [];
    private readonly IReadOnlyList<SceneShotDefinition> _shotView;

    public IReadOnlyList<SceneShotDefinition> Shots => _shotView;

    public int ShotCount => _shots.Count;

    /// <summary>The scene remains one continuous timeline; cameras do not partition it into shots.</summary>
    public int ShotSequenceLength => FrameCount;

    public SceneShotDefinition? FindShot(string? shotId)
    {
        if (string.IsNullOrWhiteSpace(shotId)) return null;
        foreach (var shot in _shots)
        {
            if (string.Equals(shot.Id, shotId, StringComparison.Ordinal)) return shot;
        }

        return null;
    }

    public int FindShotIndex(string? shotId)
    {
        if (string.IsNullOrWhiteSpace(shotId)) return -1;
        for (var index = 0; index < _shots.Count; index++)
        {
            if (string.Equals(_shots[index].Id, shotId, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    /// <summary>Cameras never claim scene layers.</summary>
    public string FindShotIdForLayer(string? layerId) => "";

    public bool IsLayerInShot(string? layerId) => false;

    /// <summary>A continuous scene frame has no camera ownership or sequence slot.</summary>
    public string FindShotIdForFrame(int frame) => "";

    public int FindShotIndexForFrame(int frame) => -1;

    public SceneShotRange GetShotRange(string? shotId)
    {
        var index = FindShotIndex(shotId);
        return GetShotRangeAt(index);
    }

    public SceneShotRange GetShotRangeAt(int index)
    {
        if (index < 0 || index >= _shots.Count) return new SceneShotRange("", "", -1, -1);
        return new SceneShotRange(
            _shots[index].Id,
            _shots[index].Name,
            0,
            Math.Max(0, FrameCount - 1));
    }

    /// <summary>All camera rows cover the same scene range; this is not an animation-layer filter.</summary>
    public SceneShotRange[] GetShotRanges() => _shots
        .Select((shot, index) => GetShotRangeAt(index))
        .ToArray();

    /// <summary>Cameras never own scene layers; retained only for old callers that ask for claims.</summary>
    public string[] ResolveShotLayerIds(string? shotId) => [];

    public SceneShotDefinition AddShot(
        string? name,
        int durationFrames,
        string? detail = null,
        IEnumerable<string>? layerIds = null)
    {
        NormalizeShots();
        if (_shots.Count >= MaximumShots) return _shots[^1];

        var shot = new SceneShotDefinition
        {
            Name = NormalizeShotName(name, _shots.Count + 1),
            Detail = NormalizeShotDetail(detail),
            DurationFrames = Math.Clamp(
                durationFrames <= 0 ? FrameCount : durationFrames,
                SceneShotDefinition.MinimumDurationFrames,
                SceneShotDefinition.MaximumDurationFrames),
            ColorArgb = ColorForShotOrdinal(_shots.Count)
        };
        _shots.Add(shot);
        // layerIds deliberately ignored: a camera observes the whole scene.
        SynchronizeTimelineTracks();
        return shot;
    }

    public bool TryRenameShot(string? shotId, string? name)
    {
        var shot = FindShot(shotId);
        if (shot is null) return false;
        var next = NormalizeShotName(name, FindShotIndex(shotId) + 1);
        if (string.Equals(shot.Name, next, StringComparison.Ordinal)) return false;
        shot.Name = next;
        MarkShotsChanged();
        return true;
    }

    public bool TrySetShotDetail(string? shotId, string? detail)
    {
        var shot = FindShot(shotId);
        if (shot is null) return false;
        var next = NormalizeShotDetail(detail);
        if (string.Equals(shot.Detail, next, StringComparison.Ordinal)) return false;
        shot.Detail = next;
        MarkShotsChanged();
        return true;
    }

    // Legacy sequence metadata is kept readable, but it no longer controls the camera timeline.
    public bool TrySetShotDuration(string? shotId, int durationFrames)
    {
        var shot = FindShot(shotId);
        if (shot is null) return false;
        var next = Math.Clamp(
            durationFrames <= 0 ? FrameCount : durationFrames,
            SceneShotDefinition.MinimumDurationFrames,
            SceneShotDefinition.MaximumDurationFrames);
        if (shot.DurationFrames == next) return false;
        shot.DurationFrames = next;
        return true;
    }

    public bool TryMoveShot(string? shotId, int destinationIndex)
    {
        var index = FindShotIndex(shotId);
        if (index < 0) return false;
        var target = Math.Clamp(destinationIndex, 0, _shots.Count - 1);
        if (target == index) return false;
        var shot = _shots[index];
        _shots.RemoveAt(index);
        _shots.Insert(target, shot);
        MarkShotsChanged();
        return true;
    }

    public bool TryRemoveShot(string? shotId)
    {
        var index = FindShotIndex(shotId);
        if (index < 0) return false;
        _shots.RemoveAt(index);
        NormalizeShots();
        SynchronizeTimelineTracks();
        MarkShotsChanged();
        return true;
    }

    // Compatibility shims. They intentionally do not alter scene-layer ownership.
    public bool TryAssignLayersToShot(string? shotId, IEnumerable<string>? layerIds, bool exclusive = true) => false;

    public bool TryRemoveLayerFromShot(string? shotId, string? layerId) => false;

    public bool TryClearLayerShot(string? layerId) => false;

    /// <summary>Kept for callers of the former shot-sequence command; the scene is already continuous.</summary>
    internal bool TryExtendTimelineToShotSequence() => false;

    internal SceneShotSnapshot[] CreateShotSnapshot()
    {
        return _shots
            .Select(shot => new SceneShotSnapshot
            {
                Id = shot.Id,
                Name = shot.Name,
                Detail = shot.Detail,
                DurationFrames = shot.DurationFrames,
                ColorArgb = shot.ColorArgb,
                LayerIds = [],
                Settings = shot.Settings,
                StateKeyframes = shot.StateKeyframes.ToArray()
            })
            .ToArray();
    }

    internal void RestoreShotSnapshot(IEnumerable<SceneShotSnapshot>? shots)
    {
        _shots.Clear();
        if (shots is not null)
        {
            foreach (var item in shots)
            {
                if (item is null) continue;
                var shot = new SceneShotDefinition
                {
                    Id = item.Id,
                    Name = item.Name,
                    Detail = item.Detail,
                    DurationFrames = item.DurationFrames,
                    ColorArgb = item.ColorArgb
                };
                // Legacy LayerIds are intentionally not restored.
                if (item.Settings.IsValid) shot.SetSettingsAtFrame(0, item.Settings);
                shot.RestoreStateKeyframes(item.StateKeyframes);
                _shots.Add(shot);
            }
        }

        NormalizeShots();
    }

    internal static int ColorForShotOrdinal(int ordinal)
    {
        var palette = ShotColorPalette;
        var index = ((ordinal % palette.Length) + palette.Length) % palette.Length;
        return palette[index];
    }

    /// <summary>Old layer-removal code may still call this; cameras have no layer references.</summary>
    internal void PruneShotLayerReferences(IEnumerable<string> removedLayerIds)
    {
        foreach (var shot in _shots) shot.LayerIds.Clear();
    }

    // Camera timeline -------------------------------------------------------------

    internal long ShotsRevision { get; private set; }

    private void MarkShotsChanged() => ShotsRevision++;

    internal AnimationTimelineTrack? FindShotTrack(string shotId)
    {
        if (FindShot(shotId) is null) return null;
        SynchronizeTimelineTracks();
        return _timeline.FindTrackByTargetId(shotId);
    }

    public bool TryEvaluateShotSettings(string shotId, int frame, out SceneShotSettings settings)
    {
        settings = SceneShotSettings.Default;
        var shot = FindShot(shotId);
        if (shot is null || frame < 0) return false;

        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null)
        {
            SynchronizeTimelineTracks();
            track = _timeline.FindTrackByTargetId(shot.Id);
        }

        var exposure = track?.EvaluateExposure(frame) ?? TimelineExposure.None(frame);
        if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0) return false;

        var tween = track?.EvaluateTween(frame);
        settings = tween is { Kind: TimelineTweenKind.Classic }
            ? SceneShotSettings.Interpolate(
                shot.EvaluateSettings(tween.Value.StartFrame),
                shot.EvaluateSettings(tween.Value.EndFrame),
                tween.Value.ProgressAt(frame))
            : shot.EvaluateSettings(exposure.SourceKeyframeFrame);
        return true;
    }

    internal bool UpdateShotAtFrame(string shotId, int frame, SceneShotSettings settings) =>
        UpdateShotAtFrameCore(shotId, frame, settings, refreshTweenMaterializations: true);

    internal bool PreviewShotAtFrame(string shotId, int frame, SceneShotSettings settings) =>
        UpdateShotAtFrameCore(shotId, frame, settings, refreshTweenMaterializations: false);

    internal bool RefreshShotTimelineTweenMaterializationsAtEndpoint(string shotId, int endpointFrame)
    {
        if (string.IsNullOrWhiteSpace(shotId) || endpointFrame < 0) return false;
        SynchronizeTimelineTracks();
        var changed = RefreshShotTimelineTweenMaterializations(shotId, endpointFrame);
        if (changed) MarkShotsChanged();
        return changed;
    }

    private bool UpdateShotAtFrameCore(
        string shotId,
        int frame,
        SceneShotSettings settings,
        bool refreshTweenMaterializations)
    {
        if (frame < 0 || frame == int.MaxValue || !settings.IsValid) return false;
        var shot = FindShot(shotId);
        if (shot is null) return false;

        var normalized = settings.Normalized();
        var settingsChanged = shot.EvaluateSettings(frame) != normalized;
        var holdsChanged = shot.Settings != normalized;
        if (!settingsChanged && !(frame == 0 && holdsChanged)) return false;

        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null) return false;
        using var batch = _timeline.BeginBatchUpdate();
        if (settingsChanged) _timeline.InsertKeyframe(track.Id, frame);
        shot.SetSettingsAtFrame(frame, normalized);
        MarkShotsChanged();
        if (settingsChanged && refreshTweenMaterializations)
        {
            RefreshShotTimelineTweenMaterializations(shot.Id, frame);
        }

        return true;
    }

    internal bool InsertShotTimelineFrame(string shotId, int frame, int count = 1)
    {
        var shot = FindShot(shotId);
        if (shot is null || frame < 0 || count <= 0) return false;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null || !_timeline.InsertFrame(track.Id, frame, count)) return false;
        shot.InsertStateFrames(frame, count);
        RefreshShotTimelineTweenMaterializations(shot.Id, -1);
        MarkShotsChanged();
        return true;
    }

    internal bool RemoveShotTimelineFrame(string shotId, int frame, int count = 1)
    {
        var shot = FindShot(shotId);
        if (shot is null || frame < 0 || count <= 0) return false;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null) return false;
        var previousDuration = track.Duration;
        if (!_timeline.RemoveFrame(track.Id, frame, count)) return false;
        shot.RemoveStateFrames(frame, previousDuration - track.Duration);
        RefreshShotTimelineTweenMaterializations(shot.Id, -1);
        MarkShotsChanged();
        return true;
    }

    internal bool InsertShotTimelineKeyframe(string shotId, int frame)
    {
        var shot = FindShot(shotId);
        if (shot is null || frame < 0 || frame == int.MaxValue) return false;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null) return false;

        var settings = TryEvaluateShotSettings(shot.Id, frame, out var evaluated)
            ? evaluated
            : shot.EvaluateSettings(frame);
        using var batch = _timeline.BeginBatchUpdate();
        var changed = RemoveShotTimelineTweenCrossing(track, frame);
        changed |= _timeline.InsertKeyframe(track.Id, frame);
        changed |= shot.SetSettingsAtFrame(frame, settings);
        if (changed) MarkShotsChanged();
        return changed;
    }

    internal bool InsertShotTimelineBlankKeyframe(string shotId, int frame)
    {
        var shot = FindShot(shotId);
        if (shot is null || frame < 0 || frame == int.MaxValue) return false;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null) return false;

        using var batch = _timeline.BeginBatchUpdate();
        var changed = RemoveShotTimelineTweenCrossing(track, frame);
        changed |= _timeline.InsertBlankKeyframe(track.Id, frame);
        changed |= shot.RemoveStateKeyframe(frame);
        if (changed) MarkShotsChanged();
        return changed;
    }

    internal bool ClearShotTimelineKeyframe(string shotId, int frame)
    {
        var shot = FindShot(shotId);
        if (shot is null || frame < 0) return false;
        SynchronizeTimelineTracks();
        var track = _timeline.FindTrackByTargetId(shot.Id);
        if (track is null) return false;

        using var batch = _timeline.BeginBatchUpdate();
        var changed = RemoveShotTimelineTweenCrossing(track, frame);
        changed |= _timeline.ClearKeyframe(track.Id, frame);
        changed |= shot.RemoveStateKeyframe(frame);
        if (changed) MarkShotsChanged();
        return changed;
    }

    private bool RemoveShotTimelineTweenCrossing(AnimationTimelineTrack track, int frame)
    {
        var tween = track.EvaluateTween(frame);
        return tween is { } span
            && frame > span.StartFrame
            && frame < span.EndFrame
            && _timeline.RemoveTween(track.Id, span.StartFrame, span.EndFrame);
    }

    internal bool TryCreateShotTimelineTween(
        string shotId,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out string error)
    {
        if (!TryResolveShotTimelineTween(shotId, startFrame, endFrame, kind, out var track, out var shot, out error))
        {
            return false;
        }

        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);
        var source = shot.EvaluateSettings(startFrame);
        var target = shot.EvaluateSettings(endFrame);
        var timelineSnapshot = _timeline.CreateSnapshot();
        var shotSnapshot = shot.Clone();
        try
        {
            using var batch = _timeline.BeginBatchUpdate();
            for (var frame = startFrame + 1; frame < endFrame; frame++)
            {
                if (!_timeline.InsertKeyframe(track.Id, frame))
                {
                    throw new InvalidOperationException("The camera tween could not create an intermediate keyframe.");
                }

                shot.SetSettingsAtFrame(
                    frame,
                    SceneShotSettings.Interpolate(
                        source,
                        target,
                        (float)(frame - startFrame) / (endFrame - startFrame)));
            }

            if (!_timeline.TryCreateTween(track.Id, startFrame, endFrame, kind, out var validation))
            {
                throw new InvalidOperationException($"The camera tween is invalid ({validation}).");
            }

            MarkShotsChanged();
            return true;
        }
        catch (Exception exception)
        {
            shot.RestoreFrom(shotSnapshot);
            _timeline.RestoreSnapshot(timelineSnapshot);
            SynchronizeTimelineTracks();
            error = exception.Message;
            return false;
        }
    }

    private bool TryResolveShotTimelineTween(
        string shotId,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out AnimationTimelineTrack track,
        out SceneShotDefinition shot,
        out string error)
    {
        track = null!;
        shot = null!;
        error = string.Empty;
        var resolvedShot = FindShot(shotId);
        if (resolvedShot is null)
        {
            error = "Select a camera track.";
            return false;
        }

        if (kind != TimelineTweenKind.Classic)
        {
            error = "Camera tracks support Classic tweens only.";
            return false;
        }

        SynchronizeTimelineTracks();
        var resolvedTrack = _timeline.FindTrackByTargetId(resolvedShot.Id);
        if (resolvedTrack is null)
        {
            error = "The selected camera has no timeline track.";
            return false;
        }

        track = resolvedTrack;
        shot = resolvedShot;
        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Duration - 1, endFrame);
        if (endFrame <= startFrame)
        {
            error = "Select a span containing a start and end frame.";
            return false;
        }

        var startExposure = track.EvaluateExposure(startFrame);
        var endExposure = track.EvaluateExposure(endFrame);
        if (!startExposure.IsKeyframe || !endExposure.IsKeyframe
            || !startExposure.HasContent || !endExposure.HasContent)
        {
            error = "Both ends of the span must be populated camera keyframes.";
            return false;
        }

        if (!track.CanCreateTween(startFrame, endFrame, kind, out var validation))
        {
            error = $"The camera tween is invalid ({validation}).";
            return false;
        }

        return true;
    }

    private bool RefreshShotTimelineTweenMaterializations(string shotFilter, int endpointFrame = -1)
    {
        var changed = false;
        foreach (var track in _timeline.Tracks.ToArray())
        {
            var shot = FindShot(track.TargetId);
            if (shot is null || !string.Equals(shot.Id, shotFilter, StringComparison.Ordinal)) continue;
            foreach (var tween in track.Tweens.ToArray())
            {
                if (tween.Kind != TimelineTweenKind.Classic) continue;
                if (endpointFrame >= 0 && tween.StartFrame != endpointFrame && tween.EndFrame != endpointFrame) continue;
                var exposedFrame = endpointFrame >= 0 ? endpointFrame : tween.EndFrame;
                var exposure = track.EvaluateExposure(exposedFrame);
                if (!exposure.IsKeyframe || !exposure.HasContent) continue;
                var source = shot.EvaluateSettings(tween.StartFrame);
                var target = shot.EvaluateSettings(tween.EndFrame);
                changed |= shot.SetSettingsAtFrame(
                    exposedFrame,
                    SceneShotSettings.Interpolate(source, target, tween.ProgressAt(exposedFrame)));
            }
        }

        return changed;
    }

    internal void NormalizeShots()
    {
        var seenShotIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = _shots.Count - 1; index >= 0; index--)
        {
            var shot = _shots[index];
            if (string.IsNullOrWhiteSpace(shot.Id) || !seenShotIds.Add(shot.Id))
            {
                _shots.RemoveAt(index);
            }
        }

        for (var index = 0; index < _shots.Count; index++)
        {
            var shot = _shots[index];
            shot.Name = NormalizeShotName(shot.Name, index + 1);
            shot.Detail = NormalizeShotDetail(shot.Detail);
            shot.DurationFrames = Math.Clamp(
                shot.DurationFrames <= 0 ? FrameCount : shot.DurationFrames,
                SceneShotDefinition.MinimumDurationFrames,
                SceneShotDefinition.MaximumDurationFrames);
            if (!shot.Settings.IsValid) shot.SetSettingsAtFrame(0, SceneShotSettings.Default);
            shot.LayerIds.Clear();
            var color = Color.FromArgb(shot.ColorArgb);
            shot.ColorArgb = color.A == 0
                ? ColorForShotOrdinal(index)
                : Color.FromArgb(255, color.R, color.G, color.B).ToArgb();
        }

        if (_shots.Count > MaximumShots) _shots.RemoveRange(MaximumShots, _shots.Count - MaximumShots);
    }

    private static readonly int[] ShotColorPalette =
    [
        Color.FromArgb(255, 244, 152, 62).ToArgb(),
        Color.FromArgb(255, 96, 180, 240).ToArgb(),
        Color.FromArgb(255, 136, 206, 122).ToArgb(),
        Color.FromArgb(255, 226, 118, 176).ToArgb(),
        Color.FromArgb(255, 196, 162, 96).ToArgb(),
        Color.FromArgb(255, 154, 142, 240).ToArgb()
    ];

    private static string NormalizeShotName(string? name, int ordinal)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) trimmed = $"Camera {ordinal:000}";
        return trimmed.Length <= SceneShotDefinition.MaximumNameLength
            ? trimmed
            : trimmed[..SceneShotDefinition.MaximumNameLength];
    }

    private static string NormalizeShotDetail(string? detail)
    {
        var trimmed = (detail ?? "").Trim();
        return trimmed.Length <= SceneShotDefinition.MaximumDetailLength
            ? trimmed
            : trimmed[..SceneShotDefinition.MaximumDetailLength];
    }
}
