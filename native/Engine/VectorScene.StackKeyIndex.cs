namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    /// <summary>
    /// Resolves drawing-stack keys (layer, keyframe, order, sub-order) to object indices without
    /// scanning the whole table per key. Batch fill merges rewrite the object table once per merge
    /// and can move any key to a new index, so the resolver caches a sorted index and rebuilds it
    /// only when the object table revision changed. A miss falls back to the previous linear
    /// lookup, which keeps the historical first-match answer for ambiguous stack keys.
    /// </summary>
    internal sealed class StackKeyIndex
    {
        private readonly VectorScene _scene;
        private int[] _sorted = [];
        private int _objectCount = -1;
        private long _geometryRevision = -1;

        public StackKeyIndex(VectorScene scene) => _scene = scene;

        public int Find(int layer, int keyframe, long order, double subOrder)
        {
            if (!TryFind(layer, keyframe, order, subOrder, out var index))
            {
                index = _scene.LookupStackKeyLinearly(layer, keyframe, order, subOrder);
            }

            return index;
        }

        public int FindByOrder(int layer, int keyframe, long order)
        {
            if (!TryFind(layer, keyframe, order, out var index))
            {
                index = _scene.LookupStackKeyLinearly(layer, keyframe, order);
            }

            return index;
        }

        /// <summary>
        /// Resolves a stack key that does not carry a layer or keyframe, matching the previous
        /// first-match scan over the whole table by order and sub-order.
        /// </summary>
        public int Find(long order, double subOrder)
        {
            if (!TryFind(order, subOrder, out var index))
            {
                index = _scene.LookupStackKeyLinearly(order, subOrder);
            }

            return index;
        }

        private bool TryFind(long order, double subOrder, out int index)
        {
            index = -1;
            if (!EnsureBuilt()) return false;
            var (low, high) = Range(0, _sorted.Length, candidate => _scene.ObjectOrder[candidate].CompareTo(order));
            if (low >= high) return false;
            for (var position = low; position < high; position++)
            {
                var candidate = _sorted[position];
                if ((uint)candidate < _scene.ObjectCount && _scene.ObjectSubOrder[candidate].Equals(subOrder))
                {
                    index = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool TryFind(int layer, int keyframe, long order, double subOrder, out int index)
        {
            index = -1;
            if (!EnsureBuilt()) return false;
            var (low, high) = Range(0, _sorted.Length, candidate => _scene.ObjectLayer[candidate].CompareTo(layer));
            (low, high) = Range(low, high, candidate => _scene.ObjectOrder[candidate].CompareTo(order));
            (low, high) = Range(low, high, candidate => _scene.ObjectKeyframeFrame[candidate].CompareTo(keyframe));
            if (low >= high || !IsSubOrderSorted(low, high)) return false;
            var (subLow, subHigh) = Range(low, high, candidate => _scene.ObjectSubOrder[candidate].CompareTo(subOrder));
            if (subLow >= subHigh) return false;
            index = _sorted[subLow];
            return (uint)index < _scene.ObjectCount;
        }

        private bool TryFind(int layer, int keyframe, long order, out int index)
        {
            index = -1;
            if (!EnsureBuilt()) return false;
            var (low, high) = Range(0, _sorted.Length, candidate => _scene.ObjectLayer[candidate].CompareTo(layer));
            (low, high) = Range(low, high, candidate => _scene.ObjectOrder[candidate].CompareTo(order));
            (low, high) = Range(low, high, candidate => _scene.ObjectKeyframeFrame[candidate].CompareTo(keyframe));
            if (low >= high) return false;
            index = _sorted[low];
            return (uint)index < _scene.ObjectCount;
        }

        private bool EnsureBuilt()
        {
            if (_objectCount != _scene.ObjectCount || _geometryRevision != _scene.GeometryRevision) Build();
            return _sorted.Length > 0;
        }

        private void Build()
        {
            var objects = new int[_scene.ObjectCount];
            for (var index = 0; index < objects.Length; index++) objects[index] = index;
            Array.Sort(objects, CompareInStackOrder);
            _sorted = objects;
            _objectCount = _scene.ObjectCount;
            _geometryRevision = _scene.GeometryRevision;
        }

        private int CompareInStackOrder(int left, int right)
        {
            var compare = _scene.ObjectLayer[left].CompareTo(_scene.ObjectLayer[right]);
            if (compare != 0) return compare;
            compare = _scene.ObjectOrder[left].CompareTo(_scene.ObjectOrder[right]);
            if (compare != 0) return compare;
            compare = _scene.ObjectKeyframeFrame[left].CompareTo(_scene.ObjectKeyframeFrame[right]);
            if (compare != 0) return compare;
            compare = _scene.ObjectSubOrder[left].CompareTo(_scene.ObjectSubOrder[right]);
            return compare != 0 ? compare : left.CompareTo(right);
        }

        private (int Low, int High) Range(int low, int high, Func<int, int> compare)
        {
            var start = low;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (compare(_sorted[middle]) < 0) low = middle + 1;
                else high = middle;
            }

            var first = low;
            high = _sorted.Length;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (compare(_sorted[middle]) <= 0) low = middle + 1;
                else high = middle;
            }

            return (first, low);
        }

        private bool IsSubOrderSorted(int low, int high)
        {
            for (var position = low + 1; position < high; position++)
            {
                if (_scene.ObjectSubOrder[_sorted[position - 1]] > _scene.ObjectSubOrder[_sorted[position]]) return false;
            }

            return true;
        }
    }
}
