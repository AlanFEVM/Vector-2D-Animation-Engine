namespace VectorAnimationEngine;

internal sealed record TimelineTabGroup(string Id, string Name, int? ColorArgb = null);

internal sealed partial class AnimationTimeline
{
    public const string DefaultTabGroupId = "layers";
    public const string TerrainTabGroupId = "terrain";
    public const string AllTabGroupId = "all";

    private const int MaximumTabGroupNameLength = 80;
    private readonly List<TimelineTabGroup> _tabGroups = CreateDefaultTabGroups();
    private string _activeTabGroupId = DefaultTabGroupId;

    public event EventHandler? TabGroupsChanged;

    public IReadOnlyList<TimelineTabGroup> TabGroups => _tabGroups;

    public string ActiveTabGroupId => _activeTabGroupId;

    public string CreateTabGroup(string name)
    {
        var normalized = NormalizeTabGroupName(name);
        if (normalized.Length == 0) return string.Empty;

        var id = NewTabGroupId();
        _tabGroups.Add(new TimelineTabGroup(id, normalized));
        OnTabGroupsChanged();
        return id;
    }

    public bool RenameTabGroup(string id, string name)
    {
        if (string.Equals(id, AllTabGroupId, StringComparison.Ordinal)) return false;
        var index = FindTabGroupIndex(id);
        var normalized = NormalizeTabGroupName(name);
        if (index < 0 || normalized.Length == 0) return false;
        if (string.Equals(_tabGroups[index].Name, normalized, StringComparison.Ordinal)) return false;

        _tabGroups[index] = _tabGroups[index] with { Name = normalized };
        OnTabGroupsChanged();
        return true;
    }

    public bool SetTabGroupColor(string id, int? colorArgb)
    {
        var index = FindTabGroupIndex(id);
        var normalized = NormalizeTabGroupColor(colorArgb);
        if (index < 0 || _tabGroups[index].ColorArgb == normalized) return false;

        _tabGroups[index] = _tabGroups[index] with { ColorArgb = normalized };
        OnTabGroupsChanged();
        return true;
    }

    private static int? NormalizeTabGroupColor(int? colorArgb) =>
        colorArgb is { } value ? value | unchecked((int)0xff000000) : null;

    public bool RemoveTabGroup(string id)
    {
        if (string.Equals(id, DefaultTabGroupId, StringComparison.Ordinal)
            || string.Equals(id, TerrainTabGroupId, StringComparison.Ordinal)
            || string.Equals(id, AllTabGroupId, StringComparison.Ordinal))
        {
            return false;
        }

        var index = FindTabGroupIndex(id);
        if (index < 0) return false;
        foreach (var track in _tracks)
        {
            if (string.Equals(track.TabGroupId, id, StringComparison.Ordinal))
            {
                track.TabGroupId = DefaultTabGroupId;
            }
        }

        _tabGroups.RemoveAt(index);
        if (string.Equals(_activeTabGroupId, id, StringComparison.Ordinal))
        {
            _activeTabGroupId = DefaultTabGroupId;
        }

        OnTabGroupsChanged();
        return true;
    }

    public bool SetTrackTabGroup(string trackId, string groupId)
    {
        if (string.Equals(groupId, AllTabGroupId, StringComparison.Ordinal)
            || !IsKnownTabGroupId(groupId))
        {
            return false;
        }

        var track = FindTrack(trackId);
        if (track is null || string.Equals(track.TabGroupId, groupId, StringComparison.Ordinal)) return false;
        track.TabGroupId = groupId;
        OnTabGroupsChanged();
        return true;
    }

    public bool SetActiveTabGroup(string groupId)
    {
        if (!string.Equals(groupId, AllTabGroupId, StringComparison.Ordinal)
            && !IsKnownTabGroupId(groupId))
        {
            return false;
        }
        if (string.Equals(_activeTabGroupId, groupId, StringComparison.Ordinal)) return false;

        _activeTabGroupId = groupId;
        OnTabGroupsChanged();
        return true;
    }

    private bool ResetTabGroups()
    {
        var changed = !string.Equals(_activeTabGroupId, DefaultTabGroupId, StringComparison.Ordinal)
            || !_tabGroups.SequenceEqual(CreateDefaultTabGroups());
        _tabGroups.Clear();
        _tabGroups.AddRange(CreateDefaultTabGroups());
        _activeTabGroupId = DefaultTabGroupId;
        return changed;
    }

    private void RestoreTabGroups(AnimationTimelineSnapshot snapshot)
    {
        _tabGroups.Clear();
        var supplied = snapshot.TabGroups ?? [];
        var suppliedById = supplied
            .Where(group => group is not null && !string.IsNullOrWhiteSpace(group.Id))
            .GroupBy(group => group.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        AddBuiltInTabGroup(DefaultTabGroupId, "Layers", suppliedById);
        AddBuiltInTabGroup(TerrainTabGroupId, "Terrain", suppliedById);
        var knownIds = _tabGroups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in supplied)
        {
            if (group is null
                || string.IsNullOrWhiteSpace(group.Id)
                || string.Equals(group.Id, AllTabGroupId, StringComparison.Ordinal)
                || !knownIds.Add(group.Id))
            {
                continue;
            }

            var name = NormalizeTabGroupName(group.Name);
            if (name.Length > 0)
            {
                _tabGroups.Add(new TimelineTabGroup(group.Id, name, NormalizeTabGroupColor(group.ColorArgb)));
            }
        }

        var requestedActiveGroupId = snapshot.ActiveTabGroupId ?? DefaultTabGroupId;
        _activeTabGroupId = string.Equals(requestedActiveGroupId, AllTabGroupId, StringComparison.Ordinal)
            || IsKnownTabGroupId(requestedActiveGroupId)
            ? requestedActiveGroupId
            : DefaultTabGroupId;
    }

    private void AddBuiltInTabGroup(
        string id,
        string defaultName,
        IReadOnlyDictionary<string, TimelineTabGroup> suppliedById)
    {
        var name = suppliedById.TryGetValue(id, out var supplied)
            ? NormalizeTabGroupName(supplied.Name)
            : string.Empty;
        _tabGroups.Add(new TimelineTabGroup(
            id, name.Length > 0 ? name : defaultName, NormalizeTabGroupColor(supplied?.ColorArgb)));
    }

    private string NormalizeTrackTabGroupId(string? groupId)
    {
        return !string.IsNullOrWhiteSpace(groupId) && IsKnownTabGroupId(groupId)
            ? groupId
            : DefaultTabGroupId;
    }

    private string NewTrackTabGroupId()
    {
        return _activeTabGroupId is AllTabGroupId or TerrainTabGroupId
            ? DefaultTabGroupId
            : NormalizeTrackTabGroupId(_activeTabGroupId);
    }

    private bool IsKnownTabGroupId(string? id)
    {
        return !string.IsNullOrWhiteSpace(id)
            && _tabGroups.Any(group => string.Equals(group.Id, id, StringComparison.Ordinal));
    }

    private int FindTabGroupIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        return _tabGroups.FindIndex(group => string.Equals(group.Id, id, StringComparison.Ordinal));
    }

    private static List<TimelineTabGroup> CreateDefaultTabGroups() =>
    [
        new TimelineTabGroup(DefaultTabGroupId, "Layers"),
        new TimelineTabGroup(TerrainTabGroupId, "Terrain")
    ];

    private static string NormalizeTabGroupName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length > MaximumTabGroupNameLength)
        {
            normalized = normalized[..MaximumTabGroupNameLength];
        }

        return normalized;
    }

    private string NewTabGroupId()
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (IsKnownTabGroupId(id)
            || string.Equals(id, AllTabGroupId, StringComparison.Ordinal));

        return id;
    }

    private void OnTabGroupsChanged() => TabGroupsChanged?.Invoke(this, EventArgs.Empty);
}
