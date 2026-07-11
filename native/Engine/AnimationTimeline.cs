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

    internal AnimationTimelineTrack(string id, string targetId, int duration, IEnumerable<TimelineKeyframe>? keyframes = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        TargetId = targetId;
        Duration = Math.Max(1, duration);
        if (keyframes is not null) RestoreKeyframes(keyframes);
    }

    public string Id { get; }
    public string TargetId { get; }
    public int Duration { get; private set; }
    public IReadOnlyList<TimelineKeyframe> Keyframes => _keyframes;

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
            return true;
        }

        _keyframes.Insert(index, new TimelineKeyframe(frame, kind));
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
            return true;
        }
        _keyframes.RemoveAt(index);
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

        return true;
    }

    internal AnimationTimelineTrackSnapshot CreateSnapshot()
    {
        return new AnimationTimelineTrackSnapshot
        {
            Id = Id,
            TargetId = TargetId,
            Duration = Duration,
            Keyframes = _keyframes.ToArray()
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

    public event EventHandler? Changed;

    public IReadOnlyList<AnimationTimelineTrack> Tracks => _tracks;
    public int Duration => _tracks.Count == 0 ? 0 : _tracks.Max(track => track.Duration);

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
            _tracks.Add(new AnimationTimelineTrack(id, item.TargetId, item.Duration, item.Keyframes));
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

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
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
}
