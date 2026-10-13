namespace VectorAnimationEngine;

internal sealed record TimelineMotionParentLink(string ChildTargetId, string ParentTargetId);

internal sealed partial class AnimationTimeline
{
    private readonly Dictionary<string, string> _motionParents = new(StringComparer.Ordinal);

    internal HashSet<string> MotionDescendantTargets(IEnumerable<string> roots)
    {
        var rootSet = roots.ToHashSet(StringComparer.Ordinal);
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in _motionParents.Keys)
        {
            var current = child;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current) && _motionParents.TryGetValue(current, out var parent))
            {
                if (rootSet.Contains(parent))
                {
                    if (!rootSet.Contains(child)) descendants.Add(child);
                    break;
                }
                current = parent;
            }
        }
        return descendants;
    }

    public string GetMotionParentTargetId(string targetId)
    {
        return string.IsNullOrWhiteSpace(targetId)
            ? string.Empty
            : _motionParents.GetValueOrDefault(targetId, string.Empty);
    }

    public bool CanSetMotionParent(string childTargetId, string parentTargetId)
    {
        if (string.IsNullOrWhiteSpace(childTargetId)
            || !_tracksByTargetId.ContainsKey(childTargetId)
            || parentTargetId is null) return false;
        if (parentTargetId.Length == 0) return true;
        if (!_tracksByTargetId.TryGetValue(parentTargetId, out var parentTrack)
            || !string.Equals(_tracksByTargetId[childTargetId].TabGroupId, parentTrack.TabGroupId, StringComparison.Ordinal))
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = parentTargetId;
        while (current.Length > 0)
        {
            if (string.Equals(current, childTargetId, StringComparison.Ordinal)
                || !visited.Add(current)) return false;
            current = GetMotionParentTargetId(current);
        }

        return true;
    }

    public bool SetMotionParent(string childTargetId, string parentTargetId)
    {
        if (!CanSetMotionParent(childTargetId, parentTargetId)
            || string.Equals(GetMotionParentTargetId(childTargetId), parentTargetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (parentTargetId.Length == 0) _motionParents.Remove(childTargetId);
        else _motionParents[childTargetId] = parentTargetId;
        OnChanged();
        return true;
    }

    internal static void ValidateMotionParents(AnimationTimelineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var targetIds = (snapshot.Tracks ?? [])
            .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.TargetId))
            .Select(track => track.TargetId)
            .ToHashSet(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in snapshot.MotionParents ?? [])
        {
            if (link is null
                || string.IsNullOrWhiteSpace(link.ChildTargetId)
                || string.IsNullOrWhiteSpace(link.ParentTargetId)
                || !targetIds.Contains(link.ChildTargetId)
                || !targetIds.Contains(link.ParentTargetId)
                || !parents.TryAdd(link.ChildTargetId, link.ParentTargetId))
            {
                throw new InvalidDataException("Invalid timeline motion parent link.");
            }
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in parents.Keys)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var current = child;
            while (!resolved.Contains(current) && parents.TryGetValue(current, out var parent))
            {
                if (!path.Add(current))
                {
                    throw new InvalidDataException("Timeline motion parents contain a cycle.");
                }

                current = parent;
            }

            resolved.UnionWith(path);
        }
    }

    private TimelineMotionParentLink[] CreateMotionParentSnapshot()
    {
        return _tracks
            .Where(track => _motionParents.ContainsKey(track.TargetId))
            .Select(track => new TimelineMotionParentLink(track.TargetId, _motionParents[track.TargetId]))
            .ToArray();
    }

    private void RestoreMotionParents(AnimationTimelineSnapshot snapshot)
    {
        _motionParents.Clear();
        foreach (var link in snapshot.MotionParents ?? [])
        {
            _motionParents.Add(link.ChildTargetId, link.ParentTargetId);
        }
    }

    private void SynchronizeMotionParents()
    {
        foreach (var child in _motionParents
                     .Where(link => !_tracksByTargetId.ContainsKey(link.Key)
                         || !_tracksByTargetId.ContainsKey(link.Value))
                     .Select(link => link.Key)
                     .ToArray())
        {
            _motionParents.Remove(child);
        }
    }
}
