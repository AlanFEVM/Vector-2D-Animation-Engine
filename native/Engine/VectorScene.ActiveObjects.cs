namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private readonly record struct ActiveObjectKeyframeEntry(
        ushort Layer,
        int KeyframeFrame,
        int ObjectIndex);

    private readonly record struct ActiveObjectKeyframeRange(
        ushort Layer,
        int KeyframeFrame,
        int Start,
        int Count);

    private static readonly IComparer<ActiveObjectKeyframeEntry> ActiveObjectKeyframeEntryComparer =
        Comparer<ActiveObjectKeyframeEntry>.Create(static (left, right) =>
        {
            var comparison = left.Layer.CompareTo(right.Layer);
            if (comparison != 0) return comparison;
            comparison = left.KeyframeFrame.CompareTo(right.KeyframeFrame);
            return comparison != 0
                ? comparison
                : left.ObjectIndex.CompareTo(right.ObjectIndex);
        });

    private ActiveObjectKeyframeEntry[] _activeObjectKeyframeEntries = [];
    private ActiveObjectKeyframeRange[] _activeObjectKeyframeRanges = [];
    private int _activeObjectKeyframeRangeCount;
    private int[] _activeObjectIndices = [];
    private int _activeObjectIndexCount;
    private int[] _activeObjectKeyframes = [];
    private VectorScene? _activeObjectIndexScene;
    private long _activeObjectIndexGeometryRevision = -1;
    private long _activeObjectIndexContentRevision = -1;
    private int _activeObjectIndexObjectCount = -1;
    private int _activeObjectIndexLayerCount = -1;
    private int _activeObjectIndexFrame = int.MinValue;

    internal ReadOnlySpan<int> GetActiveObjectIndices(int frame)
    {
        EnsureActiveObjectIndex(frame);
        return _activeObjectIndices.AsSpan(0, _activeObjectIndexCount);
    }

    private void EnsureActiveObjectIndex(int frame)
    {
        if (ReferenceEquals(_activeObjectIndexScene, this)
            && _activeObjectIndexFrame == frame
            && _activeObjectIndexGeometryRevision == GeometryRevision
            && _activeObjectIndexContentRevision == ActiveContentRevision
            && _activeObjectIndexObjectCount == ObjectCount
            && _activeObjectIndexLayerCount == LayerCount)
        {
            return;
        }

        if (!ReferenceEquals(_activeObjectIndexScene, this)
            || _activeObjectIndexGeometryRevision != GeometryRevision
            || _activeObjectIndexContentRevision != ActiveContentRevision
            || _activeObjectIndexObjectCount != ObjectCount
            || _activeObjectIndexLayerCount != LayerCount)
        {
            RebuildActiveObjectKeyframeIndex();
        }

        if (_activeObjectKeyframes.Length < LayerCount)
        {
            Array.Resize(ref _activeObjectKeyframes, LayerCount);
        }
        PopulateActiveKeyframeFrames(frame, _activeObjectKeyframes);

        if (_activeObjectIndices.Length < ObjectCount)
        {
            _activeObjectIndices = new int[ObjectCount];
        }

        var count = 0;
        for (var layer = 0; layer < LayerCount; layer++)
        {
            var keyframe = _activeObjectKeyframes[layer];
            if (keyframe == int.MinValue
                || !TryGetActiveObjectKeyframeRange(layer, keyframe, out var range))
            {
                continue;
            }

            for (var entryIndex = range.Start; entryIndex < range.Start + range.Count; entryIndex++)
            {
                _activeObjectIndices[count++] = _activeObjectKeyframeEntries[entryIndex].ObjectIndex;
            }
        }

        _activeObjectIndexCount = count;
        _activeObjectIndexScene = this;
        _activeObjectIndexGeometryRevision = GeometryRevision;
        _activeObjectIndexContentRevision = ActiveContentRevision;
        _activeObjectIndexObjectCount = ObjectCount;
        _activeObjectIndexLayerCount = LayerCount;
        _activeObjectIndexFrame = frame;
    }

    private void RebuildActiveObjectKeyframeIndex()
    {
        if (ObjectCount == 0)
        {
            _activeObjectKeyframeEntries = [];
            _activeObjectKeyframeRanges = [];
            _activeObjectKeyframeRangeCount = 0;
            return;
        }

        var entries = new ActiveObjectKeyframeEntry[ObjectCount];
        var entryCount = 0;
        for (var objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
        {
            var layer = ObjectLayer[objectIndex];
            if ((uint)layer >= LayerCount) continue;
            entries[entryCount++] = new ActiveObjectKeyframeEntry(
                layer,
                ObjectKeyframeFrame[objectIndex],
                objectIndex);
        }

        if (entryCount != entries.Length) Array.Resize(ref entries, entryCount);
        Array.Sort(entries, ActiveObjectKeyframeEntryComparer);
        _activeObjectKeyframeEntries = entries;

        var ranges = new ActiveObjectKeyframeRange[entries.Length];
        var rangeCount = 0;
        var start = 0;
        while (start < entries.Length)
        {
            var end = start + 1;
            while (end < entries.Length
                && entries[end].Layer == entries[start].Layer
                && entries[end].KeyframeFrame == entries[start].KeyframeFrame)
            {
                end++;
            }

            ranges[rangeCount++] = new ActiveObjectKeyframeRange(
                entries[start].Layer,
                entries[start].KeyframeFrame,
                start,
                end - start);
            start = end;
        }

        if (rangeCount != ranges.Length) Array.Resize(ref ranges, rangeCount);
        _activeObjectKeyframeRanges = ranges;
        _activeObjectKeyframeRangeCount = rangeCount;
    }

    private bool TryGetActiveObjectKeyframeRange(
        int layer,
        int keyframeFrame,
        out ActiveObjectKeyframeRange range)
    {
        var low = 0;
        var high = _activeObjectKeyframeRangeCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _activeObjectKeyframeRanges[middle];
            var comparison = candidate.Layer.CompareTo((ushort)layer);
            if (comparison == 0) comparison = candidate.KeyframeFrame.CompareTo(keyframeFrame);
            if (comparison == 0)
            {
                range = candidate;
                return true;
            }

            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }

        range = default;
        return false;
    }
}
