using System.Threading;

namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    private const int MaximumDistortBoundsCacheEntries = 4096;
    private readonly object _distortBoundsCacheGate = new();
    private readonly Dictionary<int, DistortBoundsCacheEntry> _distortBoundsCache = new();
    private readonly Queue<int> _distortBoundsCacheInsertionOrder = new();
    private long _distortBoundsCacheBuildCount;
    private long _distortBoundsCacheReuseCount;

    internal long DistortBoundsCacheBuildCount => Interlocked.Read(ref _distortBoundsCacheBuildCount);
    internal long DistortBoundsCacheReuseCount => Interlocked.Read(ref _distortBoundsCacheReuseCount);
    internal int DistortBoundsCacheEntryCount
    {
        get
        {
            lock (_distortBoundsCacheGate) return _distortBoundsCache.Count;
        }
    }

    private sealed class DistortBoundsCacheEntry(
        RectangleF rawBounds,
        DistortWarp[] distortions)
    {
        private readonly object _buildGate = new();
        private RectangleF _bounds;
        private bool _hasBounds;
        internal RectangleF RawBounds { get; } = rawBounds;
        // Mutations replace the stored warp array. Keep only its identity after
        // computation so a scene reset or undo can release the retired envelopes.
        internal WeakReference<DistortWarp[]> Distortions { get; } = new(distortions);

        internal RectangleF GetBounds(VectorScene owner, DistortWarp[] stack)
        {
            lock (_buildGate)
            {
                if (_hasBounds) return _bounds;
                var bounds = RawBounds;
                foreach (var distortion in stack)
                {
                    bounds = MapBoundsThroughDistortion(bounds, distortion);
                }
                _bounds = bounds;
                _hasBounds = true;
                Interlocked.Increment(ref owner._distortBoundsCacheBuildCount);
                return bounds;
            }
        }
    }

    private RectangleF GetCachedDistortedObjectBounds(
        int objectIndex,
        RectangleF rawBounds,
        DistortWarp[] distortions)
    {
        DistortBoundsCacheEntry entry;
        lock (_distortBoundsCacheGate)
        {
            if (_distortBoundsCache.TryGetValue(objectIndex, out var cached)
                && cached.RawBounds == rawBounds
                && cached.Distortions.TryGetTarget(out var cachedDistortions)
                && ReferenceEquals(cachedDistortions, distortions))
            {
                entry = cached;
                Interlocked.Increment(ref _distortBoundsCacheReuseCount);
            }
            else
            {
                // Bounds are requested during RebuildGeometryIndex, before its
                // revision changes. The actual inputs also cover direct preview
                // edits, snapshot restore, composition rebuilds and packed remaps.
                entry = new DistortBoundsCacheEntry(rawBounds, distortions);
                if (!_distortBoundsCache.ContainsKey(objectIndex))
                {
                    if (_distortBoundsCache.Count == MaximumDistortBoundsCacheEntries)
                    {
                        _distortBoundsCache.Remove(_distortBoundsCacheInsertionOrder.Dequeue());
                    }
                    _distortBoundsCacheInsertionOrder.Enqueue(objectIndex);
                }
                _distortBoundsCache[objectIndex] = entry;
            }
        }

        // Different objects can build in parallel; callers of the same entry
        // share one calculation without holding the scene-wide cache lock.
        return entry.GetBounds(this, distortions);
    }
}
