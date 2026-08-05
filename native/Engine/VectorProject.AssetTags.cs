namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    internal const int MaxAssetTagCount = 128;
    internal const int MaxAssetTagNameLength = 64;

    private readonly List<ProjectAssetTag> _assetTags = [];
    private readonly IReadOnlyList<ProjectAssetTag> _assetTagView;

    public IReadOnlyList<ProjectAssetTag> AssetTags => _assetTagView;

    public bool TryApplyAssetTagEdit(
        string drawingObjectId,
        IEnumerable<ProjectAssetTagData> tags,
        IEnumerable<string> assignedTagIds)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(assignedTagIds);
        var drawingObject = FindDrawingObject(drawingObjectId);
        if (drawingObject is null) return false;

        var normalized = NormalizeAssetTags(tags);
        if (normalized is null) return false;
        var validIds = normalized.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        var requestedAssignments = assignedTagIds
            .Where(validIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var normalizedAssignments = normalized
            .Where(tag => requestedAssignments.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToArray();

        var definitionsChanged = _assetTags.Count != normalized.Length
            || _assetTags.Where((tag, index) => !AssetTagEquals(tag, normalized[index])).Any();
        var assignmentsChanged = false;
        foreach (var candidate in _drawingObjects)
        {
            var next = ReferenceEquals(candidate, drawingObject)
                ? normalizedAssignments
                : candidate.AssetTagIds.Where(validIds.Contains).ToArray();
            if (candidate.AssetTagIds.SequenceEqual(next, StringComparer.Ordinal)) continue;
            assignmentsChanged = true;
            candidate.ReplaceAssetTagIds(next);
        }

        if (!definitionsChanged && !assignmentsChanged) return true;
        if (definitionsChanged)
        {
            _assetTags.Clear();
            _assetTags.AddRange(normalized);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void RestoreAssetTags(IEnumerable<ProjectAssetTagRestartSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var normalized = NormalizeAssetTags(snapshots.Select(snapshot => new ProjectAssetTagData(
            snapshot.Id,
            snapshot.Name,
            snapshot.ColorArgb)));
        _assetTags.Clear();
        if (normalized is not null) _assetTags.AddRange(normalized);
    }

    private static ProjectAssetTag[]? NormalizeAssetTags(IEnumerable<ProjectAssetTagData> tags)
    {
        var result = new List<ProjectAssetTag>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in tags)
        {
            if (result.Count >= MaxAssetTagCount) return null;
            var id = source.Id?.Trim() ?? "";
            var name = source.Name?.Trim() ?? "";
            if (!IsValidRestartId(id)
                || name.Length is 0 or > MaxAssetTagNameLength
                || !ids.Add(id)
                || !names.Add(name))
            {
                return null;
            }

            var color = Color.FromArgb(source.ColorArgb);
            result.Add(new ProjectAssetTag
            {
                Id = id,
                Name = name,
                ColorArgb = Color.FromArgb(255, color.R, color.G, color.B).ToArgb()
            });
        }
        return result.ToArray();
    }

    private static bool AssetTagEquals(ProjectAssetTag left, ProjectAssetTag right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && left.ColorArgb == right.ColorArgb;
    }
}
