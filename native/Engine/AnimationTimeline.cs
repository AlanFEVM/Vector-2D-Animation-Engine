namespace VectorAnimationEngine;

internal interface ITimelineContext
{
    AnimationTimeline Timeline { get; }
    int FrameCount { get; }
    IReadOnlyList<string> TimelineTargetIds { get; }

    void SynchronizeTimelineTracks();
}

internal enum TimelineKeyframeKind : byte
{
    Populated,
    Blank
}

internal readonly record struct TimelineKeyframe(int Frame, TimelineKeyframeKind Kind)
{
    public bool HasContent => Kind == TimelineKeyframeKind.Populated;
}

internal readonly record struct TimelineExposure(
    int Frame,
    bool HasContent,
    int SourceKeyframeFrame,
    int EndFrame,
    TimelineKeyframeKind? SourceKind)
{
    public bool IsKeyframe => SourceKeyframeFrame == Frame;

    public static TimelineExposure None(int frame) => new(frame, false, -1, frame, null);
}

internal readonly record struct TimelineTrackTarget(string TargetId, int Duration);

internal sealed class AnimationTimelineTrack
{
    private readonly List<TimelineKeyframe> _keyframes = [];
    private readonly List<TimelineTween> _tweens = [];

    internal AnimationTimelineTrack(
        string id,
        string targetId,
        int duration,
        IEnumerable<TimelineKeyframe>? keyframes = null,
        IEnumerable<TimelineTween>? tweens = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        TargetId = targetId;
        Duration = Math.Max(1, duration);
        if (keyframes is not null) RestoreKeyframes(keyframes);
        if (tweens is not null) RestoreTweens(tweens);
    }

    public string Id { get; }
    public string TargetId { get; }
    public int Duration { get; private set; }
    public IReadOnlyList<TimelineKeyframe> Keyframes => _keyframes;
    public IReadOnlyList<TimelineTween> Tweens => _tweens;

    public TimelineExposure EvaluateExposure(int frame)
    {
        if (frame < 0 || frame >= Duration || _keyframes.Count == 0) return TimelineExposure.None(frame);

        var insertionIndex = LowerBound(frame);
        var keyframeIndex = insertionIndex < _keyframes.Count && _keyframes[insertionIndex].Frame == frame
            ? insertionIndex
            : insertionIndex - 1;
        if (keyframeIndex < 0) return TimelineExposure.None(frame);

        var source = _keyframes[keyframeIndex];
        var endFrame = keyframeIndex + 1 < _keyframes.Count
            ? _keyframes[keyframeIndex + 1].Frame - 1
            : Duration - 1;
        return new TimelineExposure(frame, source.HasContent, source.Frame, endFrame, source.Kind);
    }

    internal bool InsertFrame(int frame, int count)
    {
        if (count <= 0 || frame < 0 || frame > Duration || Duration > int.MaxValue - count) return false;

        for (var i = _keyframes.Count - 1; i >= 0; i--)
        {
            var keyframe = _keyframes[i];
            if (keyframe.Frame <= frame) break;
            _keyframes[i] = keyframe with { Frame = keyframe.Frame + count };
        }

        Duration += count;
        AdjustTweensAfterInsert(frame, count);
        return true;
    }

    internal bool RemoveFrame(int frame, int count)
    {
        if (count <= 0 || frame < 0 || frame >= Duration || Duration <= 1) return false;

        var removeCount = Math.Min(count, Duration - frame);
        removeCount = Math.Min(removeCount, Duration - 1);
        if (removeCount <= 0) return false;

        var removeEnd = frame + removeCount;
        var continuation = removeEnd < Duration ? EvaluateExposure(removeEnd) : TimelineExposure.None(removeEnd);
        var preserveRemovedSource = continuation.SourceKeyframeFrame >= frame
            && continuation.SourceKeyframeFrame < removeEnd
            && continuation.SourceKind is not null;
        for (var i = _keyframes.Count - 1; i >= 0; i--)
        {
            var keyframe = _keyframes[i];
            if (keyframe.Frame >= removeEnd)
            {
                _keyframes[i] = keyframe with { Frame = keyframe.Frame - removeCount };
            }
            else if (keyframe.Frame >= frame)
            {
                _keyframes.RemoveAt(i);
            }
        }

        Duration -= removeCount;
        AdjustTweensAfterRemove(frame, removeCount);
        if (preserveRemovedSource)
        {
            var index = LowerBound(frame);
            if (index >= _keyframes.Count || _keyframes[index].Frame != frame)
            {
                _keyframes.Insert(index, new TimelineKeyframe(frame, continuation.SourceKind!.Value));
            }
        }

        if (_keyframes.Count == 0 || _keyframes[0].Frame > 0)
        {
            _keyframes.Insert(0, new TimelineKeyframe(0, TimelineKeyframeKind.Blank));
        }

        return true;
    }

    internal bool InsertKeyframe(int frame, TimelineKeyframeKind kind)
    {
        if (frame < 0 || frame == int.MaxValue) return false;

        var changed = false;
        if (frame >= Duration)
        {
            Duration = frame + 1;
            changed = true;
        }

        var index = LowerBound(frame);
        if (index < _keyframes.Count && _keyframes[index].Frame == frame)
        {
            if (_keyframes[index].Kind == kind) return changed;
            _keyframes[index] = new TimelineKeyframe(frame, kind);
            PruneInvalidTweens();
            return true;
        }

        _keyframes.Insert(index, new TimelineKeyframe(frame, kind));
        PruneTweensCrossing(frame);
        return true;
    }

    internal bool ClearKeyframe(int frame)
    {
        if (frame < 0 || _keyframes.Count == 0) return false;
        var index = LowerBound(frame);
        if (index >= _keyframes.Count || _keyframes[index].Frame != frame) return false;
        if (frame == 0)
        {
            if (_keyframes[index].Kind == TimelineKeyframeKind.Blank) return false;
            _keyframes[index] = new TimelineKeyframe(0, TimelineKeyframeKind.Blank);
            PruneInvalidTweens();
            return true;
        }
        _keyframes.RemoveAt(index);
        PruneInvalidTweens();
        return true;
    }

    internal bool SetDuration(int duration)
    {
        var next = Math.Max(1, duration);
        if (next == Duration) return false;

        Duration = next;
        var firstOutOfRange = LowerBound(Duration);
        if (firstOutOfRange < _keyframes.Count)
        {
            _keyframes.RemoveRange(firstOutOfRange, _keyframes.Count - firstOutOfRange);
        }

        PruneInvalidTweens();

        return true;
    }

    /// <summary>
    /// Creates a tween only when both endpoints are populated keyframes and
    /// the requested span does not overlap another span.
    /// Object-count and geometry compatibility are intentionally validated by
    /// the owning scene; this method enforces timeline invariants only.
    /// </summary>
    internal bool TryCreateTween(
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out TimelineTweenValidationError error)
    {
        error = ValidateTween(startFrame, endFrame, kind);
        if (error != TimelineTweenValidationError.None) return false;

        var tween = new TimelineTween(startFrame, endFrame, kind);
        _tweens.Add(tween);
        _tweens.Sort(static (left, right) => left.StartFrame.CompareTo(right.StartFrame));
        return true;
    }

    internal bool CanCreateTween(
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out TimelineTweenValidationError error)
    {
        error = ValidateTween(startFrame, endFrame, kind);
        return error == TimelineTweenValidationError.None;
    }

    /// <summary>
    /// Resolves a frame selection to the adjacent populated keyframes that
    /// bound it. This matches Animate's workflow where the tween command may
    /// be invoked from any frame inside the intended span.
    /// </summary>
    internal bool TryResolveTweenSpan(
        int firstFrame,
        int lastFrame,
        out int startFrame,
        out int endFrame)
    {
        startFrame = -1;
        endFrame = -1;
        if (firstFrame < 0 || lastFrame < firstFrame || firstFrame >= Duration)
        {
            return false;
        }

        lastFrame = Math.Min(lastFrame, Duration - 1);
        var exposure = EvaluateExposure(firstFrame);
        if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0)
        {
            return false;
        }

        var startIndex = LowerBound(exposure.SourceKeyframeFrame);
        if (startIndex >= _keyframes.Count
            || _keyframes[startIndex].Frame != exposure.SourceKeyframeFrame)
        {
            return false;
        }

        if (TryResolveForward(startIndex, lastFrame, out startFrame, out endFrame))
        {
            return true;
        }

        // A single click on the final keyframe of a span resolves backward
        // when there is no valid populated keyframe to its right.
        if (firstFrame != lastFrame || !exposure.IsKeyframe || startIndex <= 0)
        {
            return false;
        }

        var previous = _keyframes[startIndex - 1];
        if (!previous.HasContent) return false;
        startFrame = previous.Frame;
        endFrame = exposure.SourceKeyframeFrame;
        return true;
    }

    private bool TryResolveForward(
        int startIndex,
        int selectedEndFrame,
        out int startFrame,
        out int endFrame)
    {
        startFrame = -1;
        endFrame = -1;
        if (startIndex + 1 >= _keyframes.Count) return false;

        var start = _keyframes[startIndex];
        var end = _keyframes[startIndex + 1];
        if (!start.HasContent || !end.HasContent || selectedEndFrame > end.Frame)
        {
            return false;
        }

        startFrame = start.Frame;
        endFrame = end.Frame;
        return true;
    }

    internal bool RemoveTween(int startFrame, int endFrame)
    {
        var index = _tweens.FindIndex(tween => tween.StartFrame == startFrame && tween.EndFrame == endFrame);
        if (index < 0) return false;
        _tweens.RemoveAt(index);
        return true;
    }

    internal bool ReplaceTweenCurve(
        int startFrame,
        int endFrame,
        IEnumerable<TweenCurveAnchor> anchors)
    {
        var index = _tweens.FindIndex(tween => tween.StartFrame == startFrame && tween.EndFrame == endFrame);
        if (index < 0
            || !TimelineTween.TryNormalizeCurveAnchors(anchors, out var normalized)
            || _tweens[index].CurveEquals(normalized))
        {
            return false;
        }

        _tweens[index] = _tweens[index].WithCurveAnchors(normalized);
        return true;
    }

    public TimelineTween? EvaluateTween(int frame)
    {
        if (frame < 0 || _tweens.Count == 0) return null;

        var index = LowerBoundTween(frame);
        if (index < _tweens.Count && _tweens[index].StartFrame == frame) return _tweens[index];
        index--;
        return index >= 0 && _tweens[index].Contains(frame) ? _tweens[index] : null;
    }

    internal AnimationTimelineTrackSnapshot CreateSnapshot()
    {
        return new AnimationTimelineTrackSnapshot
        {
            Id = Id,
            TargetId = TargetId,
            Duration = Duration,
            Keyframes = _keyframes.ToArray(),
            Tweens = _tweens.ToArray()
        };
    }

    private void RestoreKeyframes(IEnumerable<TimelineKeyframe> keyframes)
    {
        _keyframes.Clear();
        foreach (var keyframe in keyframes
                     .Where(item => item.Frame >= 0 && item.Frame < Duration)
                     .OrderBy(item => item.Frame))
        {
            if (_keyframes.Count > 0 && _keyframes[^1].Frame == keyframe.Frame)
            {
                _keyframes[^1] = keyframe;
            }
            else
            {
                _keyframes.Add(keyframe);
            }
        }
    }

    private void RestoreTweens(IEnumerable<TimelineTween> tweens)
    {
        _tweens.Clear();
        foreach (var tween in tweens
                     .Where(item => item.IsValid && item.StartFrame >= 0 && item.EndFrame < Duration)
                     .OrderBy(item => item.StartFrame))
        {
            if (ValidateTween(tween.StartFrame, tween.EndFrame, tween.Kind) != TimelineTweenValidationError.None)
            {
                continue;
            }

            _tweens.Add(tween);
        }
    }

    private TimelineTweenValidationError ValidateTween(int startFrame, int endFrame, TimelineTweenKind kind)
    {
        if (kind is not (TimelineTweenKind.Classic or TimelineTweenKind.Shape))
        {
            return TimelineTweenValidationError.InvalidKind;
        }

        if (startFrame < 0 || endFrame <= startFrame)
        {
            return TimelineTweenValidationError.InvalidRange;
        }

        if (endFrame >= Duration)
        {
            return TimelineTweenValidationError.OutOfRange;
        }

        var startIndex = LowerBound(startFrame);
        var endIndex = LowerBound(endFrame);
        if (startIndex >= _keyframes.Count || _keyframes[startIndex].Frame != startFrame)
        {
            return TimelineTweenValidationError.MissingStartKeyframe;
        }

        if (endIndex >= _keyframes.Count || _keyframes[endIndex].Frame != endFrame)
        {
            return TimelineTweenValidationError.MissingEndKeyframe;
        }

        if (!_keyframes[startIndex].HasContent || !_keyframes[endIndex].HasContent)
        {
            return TimelineTweenValidationError.BlankEndpoint;
        }

        foreach (var existing in _tweens)
        {
            if (existing.StartFrame == startFrame && existing.EndFrame == endFrame)
            {
                return existing.Kind == kind
                    ? TimelineTweenValidationError.AlreadyExists
                    : TimelineTweenValidationError.OverlapsExisting;
            }

            // Adjacent spans may share a destination/start keyframe. Their
            // interiors must remain disjoint.
            if (startFrame < existing.EndFrame && endFrame > existing.StartFrame)
            {
                return TimelineTweenValidationError.OverlapsExisting;
            }
        }

        return TimelineTweenValidationError.None;
    }

    private void PruneTweensCrossing(int frame)
    {
        _tweens.RemoveAll(tween => frame > tween.StartFrame && frame < tween.EndFrame);
    }

    private void PruneInvalidTweens()
    {
        _tweens.RemoveAll(tween =>
            ValidateTweenEndpoints(tween.StartFrame, tween.EndFrame) != TimelineTweenValidationError.None);
    }

    private TimelineTweenValidationError ValidateTweenEndpoints(int startFrame, int endFrame)
    {
        if (startFrame < 0 || endFrame <= startFrame || endFrame >= Duration)
        {
            return TimelineTweenValidationError.InvalidRange;
        }

        var startIndex = LowerBound(startFrame);
        var endIndex = LowerBound(endFrame);
        if (startIndex >= _keyframes.Count || _keyframes[startIndex].Frame != startFrame)
        {
            return TimelineTweenValidationError.MissingStartKeyframe;
        }

        if (endIndex >= _keyframes.Count || _keyframes[endIndex].Frame != endFrame)
        {
            return TimelineTweenValidationError.MissingEndKeyframe;
        }

        return _keyframes[startIndex].HasContent && _keyframes[endIndex].HasContent
            ? TimelineTweenValidationError.None
            : TimelineTweenValidationError.BlankEndpoint;
    }

    private void AdjustTweensAfterInsert(int frame, int count)
    {
        for (var i = 0; i < _tweens.Count; i++)
        {
            var tween = _tweens[i];
            // Timeline insertion preserves the keyframe at the insertion
            // frame; only frames strictly after it move forward.
            var start = tween.StartFrame > frame ? tween.StartFrame + count : tween.StartFrame;
            var end = tween.EndFrame > frame ? tween.EndFrame + count : tween.EndFrame;
            _tweens[i] = tween with { StartFrame = start, EndFrame = end };
        }
    }

    private void AdjustTweensAfterRemove(int frame, int count)
    {
        var removeEnd = frame + count;
        var adjusted = new List<TimelineTween>(_tweens.Count);
        foreach (var tween in _tweens)
        {
            if (tween.EndFrame < frame)
            {
                adjusted.Add(tween);
                continue;
            }

            if (tween.StartFrame >= removeEnd)
            {
                adjusted.Add(tween with
                {
                    StartFrame = tween.StartFrame - count,
                    EndFrame = tween.EndFrame - count
                });
                continue;
            }

            var hasPrefix = tween.StartFrame < frame;
            var hasSuffix = tween.EndFrame >= removeEnd;
            if (!hasPrefix && !hasSuffix) continue;

            var start = hasPrefix ? tween.StartFrame : frame;
            var end = hasSuffix ? tween.EndFrame - count : frame - 1;
            if (end > start)
            {
                adjusted.Add(tween with { StartFrame = start, EndFrame = end });
            }
        }

        _tweens.Clear();
        _tweens.AddRange(adjusted);
        PruneInvalidTweens();
    }

    private int LowerBoundTween(int frame)
    {
        var low = 0;
        var high = _tweens.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_tweens[middle].StartFrame < frame) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private int LowerBound(int frame)
    {
        var low = 0;
        var high = _keyframes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_keyframes[middle].Frame < frame) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}

internal sealed class AnimationTimeline
{
    public const int DefaultDuration = 240;

    private readonly List<AnimationTimelineTrack> _tracks = [];
    private readonly Dictionary<string, AnimationTimelineTrack> _tracksById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationTimelineTrack> _tracksByTargetId = new(StringComparer.Ordinal);
    private int _batchUpdateDepth;
    private bool _batchChanged;

    public event EventHandler? Changed;

    public IReadOnlyList<AnimationTimelineTrack> Tracks => _tracks;
    public int Duration => _tracks.Count == 0 ? 0 : _tracks.Max(track => track.Duration);

    internal IDisposable BeginBatchUpdate()
    {
        _batchUpdateDepth++;
        return new BatchUpdateScope(this);
    }

    public AnimationTimelineTrack? FindTrack(string trackId)
    {
        return _tracksById.GetValueOrDefault(trackId);
    }

    public AnimationTimelineTrack? FindTrackByTargetId(string targetId)
    {
        return _tracksByTargetId.GetValueOrDefault(targetId);
    }

    public TimelineExposure EvaluateExposure(string trackId, int frame)
    {
        return FindTrack(trackId)?.EvaluateExposure(frame) ?? TimelineExposure.None(frame);
    }

    public TimelineExposure EvaluateTargetExposure(string targetId, int frame)
    {
        return FindTrackByTargetId(targetId)?.EvaluateExposure(frame) ?? TimelineExposure.None(frame);
    }

    public bool InsertFrame(string trackId, int frame, int count = 1)
    {
        var track = FindTrack(trackId);
        if (track is null || frame < 0 || count <= 0) return false;
        var changed = frame > track.Duration && track.SetDuration(frame);
        changed |= track.InsertFrame(frame, count);
        if (changed) OnChanged();
        return changed;
    }

    public bool RemoveFrame(string trackId, int frame, int count = 1)
    {
        var changed = FindTrack(trackId)?.RemoveFrame(frame, count) == true;
        if (changed) OnChanged();
        return changed;
    }

    public bool InsertKeyframe(string trackId, int frame)
    {
        return InsertKeyframe(trackId, frame, TimelineKeyframeKind.Populated);
    }

    public bool InsertBlankKeyframe(string trackId, int frame)
    {
        return InsertKeyframe(trackId, frame, TimelineKeyframeKind.Blank);
    }

    public bool ClearKeyframe(string trackId, int frame)
    {
        var changed = FindTrack(trackId)?.ClearKeyframe(frame) == true;
        if (changed) OnChanged();
        return changed;
    }

    public bool SetTrackDuration(string trackId, int duration)
    {
        var changed = FindTrack(trackId)?.SetDuration(duration) == true;
        if (changed) OnChanged();
        return changed;
    }

    public void Clear()
    {
        if (_tracks.Count == 0) return;
        _tracks.Clear();
        _tracksById.Clear();
        _tracksByTargetId.Clear();
        OnChanged();
    }

    public void SynchronizeTracks(
        IEnumerable<string> targetIds,
        int defaultDuration = DefaultDuration,
        bool populateNewTracks = true)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        var targets = targetIds
            .Where(targetId => !string.IsNullOrWhiteSpace(targetId))
            .Distinct(StringComparer.Ordinal)
            .Select(targetId => new TimelineTrackTarget(targetId, Math.Max(1, defaultDuration)))
            .ToArray();
        SynchronizeTracksCore(targets, populateNewTracks, updateExistingDurations: false);
    }

    public void SynchronizeTracks(IEnumerable<TimelineTrackTarget> targets, bool populateNewTracks = true)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var normalized = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.TargetId))
            .GroupBy(target => target.TargetId, StringComparer.Ordinal)
            .Select(group => new TimelineTrackTarget(group.Key, Math.Max(1, group.Last().Duration)))
            .ToArray();
        SynchronizeTracksCore(normalized, populateNewTracks, updateExistingDurations: true);
    }

    public AnimationTimelineSnapshot CreateSnapshot()
    {
        return new AnimationTimelineSnapshot
        {
            Tracks = _tracks.Select(track => track.CreateSnapshot()).ToArray()
        };
    }

    public void RestoreSnapshot(AnimationTimelineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _tracks.Clear();

        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Tracks)
        {
            if (string.IsNullOrWhiteSpace(item.TargetId) || !targetIds.Add(item.TargetId)) continue;
            var id = !string.IsNullOrWhiteSpace(item.Id) && trackIds.Add(item.Id)
                ? item.Id
                : NewUniqueId(trackIds);
            _tracks.Add(new AnimationTimelineTrack(id, item.TargetId, item.Duration, item.Keyframes, item.Tweens));
        }

        RebuildLookups();
        OnChanged();
    }

    private bool InsertKeyframe(string trackId, int frame, TimelineKeyframeKind kind)
    {
        var changed = FindTrack(trackId)?.InsertKeyframe(frame, kind) == true;
        if (changed) OnChanged();
        return changed;
    }

    public bool TryCreateTween(
        string trackId,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out TimelineTweenValidationError error)
    {
        var track = FindTrack(trackId);
        if (track is null)
        {
            error = TimelineTweenValidationError.OutOfRange;
            return false;
        }

        var changed = track.TryCreateTween(startFrame, endFrame, kind, out error);
        if (changed) OnChanged();
        return changed;
    }

    public bool CanCreateTween(
        string trackId,
        int startFrame,
        int endFrame,
        TimelineTweenKind kind,
        out TimelineTweenValidationError error)
    {
        var track = FindTrack(trackId);
        if (track is null)
        {
            error = TimelineTweenValidationError.OutOfRange;
            return false;
        }

        return track.CanCreateTween(startFrame, endFrame, kind, out error);
    }

    public bool RemoveTween(string trackId, int startFrame, int endFrame)
    {
        var changed = FindTrack(trackId)?.RemoveTween(startFrame, endFrame) == true;
        if (changed) OnChanged();
        return changed;
    }

    public bool ReplaceTweenCurve(
        string trackId,
        int startFrame,
        int endFrame,
        IEnumerable<TweenCurveAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var changed = FindTrack(trackId)?.ReplaceTweenCurve(startFrame, endFrame, anchors) == true;
        if (changed) OnChanged();
        return changed;
    }

    public TimelineTween? EvaluateTween(string trackId, int frame)
    {
        return FindTrack(trackId)?.EvaluateTween(frame);
    }

    public TimelineTween? EvaluateTargetTween(string targetId, int frame)
    {
        return FindTrackByTargetId(targetId)?.EvaluateTween(frame);
    }

    private void SynchronizeTracksCore(
        IReadOnlyList<TimelineTrackTarget> targets,
        bool populateNewTracks,
        bool updateExistingDurations)
    {
        var existingByTarget = _tracks
            .GroupBy(track => track.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var synchronized = new List<AnimationTimelineTrack>(targets.Count);
        var changed = targets.Count != _tracks.Count;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (existingByTarget.TryGetValue(target.TargetId, out var track))
            {
                if (updateExistingDurations) changed |= track.SetDuration(target.Duration);
                synchronized.Add(track);
                if (!changed && !ReferenceEquals(_tracks[i], track)) changed = true;
                continue;
            }

            track = new AnimationTimelineTrack(Guid.NewGuid().ToString("N"), target.TargetId, target.Duration);
            if (populateNewTracks) track.InsertKeyframe(0, TimelineKeyframeKind.Populated);
            synchronized.Add(track);
            changed = true;
        }

        if (!changed) return;
        _tracks.Clear();
        _tracks.AddRange(synchronized);
        RebuildLookups();
        OnChanged();
    }

    private void RebuildLookups()
    {
        _tracksById.Clear();
        _tracksByTargetId.Clear();
        foreach (var track in _tracks)
        {
            _tracksById[track.Id] = track;
            _tracksByTargetId[track.TargetId] = track;
        }
    }

    private static string NewUniqueId(ISet<string> ids)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!ids.Add(id));

        return id;
    }

    private void EndBatchUpdate()
    {
        if (_batchUpdateDepth <= 0) return;
        _batchUpdateDepth--;
        if (_batchUpdateDepth != 0 || !_batchChanged) return;
        _batchChanged = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChanged()
    {
        if (_batchUpdateDepth > 0)
        {
            _batchChanged = true;
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class BatchUpdateScope(AnimationTimeline timeline) : IDisposable
    {
        private AnimationTimeline? _timeline = timeline;

        public void Dispose()
        {
            Interlocked.Exchange(ref _timeline, null)?.EndBatchUpdate();
        }
    }
}

internal sealed class AnimationTimelineSnapshot
{
    public AnimationTimelineTrackSnapshot[] Tracks { get; init; } = [];
}

internal sealed class AnimationTimelineTrackSnapshot
{
    public string Id { get; init; } = "";
    public string TargetId { get; init; } = "";
    public int Duration { get; init; } = AnimationTimeline.DefaultDuration;
    public TimelineKeyframe[] Keyframes { get; init; } = [];
    public TimelineTween[] Tweens { get; init; } = [];
}
