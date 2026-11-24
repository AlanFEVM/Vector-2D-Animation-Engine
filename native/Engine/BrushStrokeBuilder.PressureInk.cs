using Clipper2Lib;

namespace VectorAnimationEngine;

internal static partial class BrushStrokeBuilder
{
    // Immutable completed pieces plus a replaceable tail. No whole-stroke union
    // or editable scene construction occurs on pointer updates.
    internal sealed class PressureInk(BrushShape shape)
    {
        private const int Span = 64;
        private readonly List<PathBezierNode[][]> _completed = [];
        private readonly List<PathBezierNode[][]> _pieces = [];
        private int _sealedCount;
        private int _smoothing = -1;
        private float _scale;
        private PressureBrushPoint[] _last = [];
        internal IReadOnlyList<PathBezierNode[][]> Pieces => _pieces;
        internal long SweptSampleCount { get; private set; }
        internal int SealedCount => _sealedCount;

        internal void Update(IReadOnlyList<PressureBrushPoint> profile, int stableCount, int smoothing, float scale)
        {
            if (profile.Count == 0) return;
            scale = NormalizeWorldPerPixel(scale);
            if (_scale == scale && _smoothing == smoothing && profile.SequenceEqual(_last)) return;
            if (_scale != scale || _smoothing != smoothing || _sealedCount > Math.Max(0, stableCount - 1))
            {
                _completed.Clear();
                _sealedCount = 0;
            }
            _scale = scale;
            _smoothing = smoothing;
            // Use one fixed error per gesture/zoom, independent of future pressure minima.
            var error = Math.Max(1, scale * 0.05f);
            while (stableCount - 1 - _sealedCount >= Span)
            {
                _completed.Add(BuildPiece(Math.Max(0, _sealedCount - 1), _sealedCount + Span));
                _sealedCount += Span;
            }
            _pieces.Clear();
            _pieces.AddRange(_completed);
            _pieces.Add(BuildPiece(Math.Max(0, _sealedCount - 1), profile.Count));
            _last = profile.ToArray();

            PathBezierNode[][] BuildPiece(int first, int last)
            {
                SweptSampleCount += last - first;
                var samples = InterpolatePressureRange(profile, first, last, smoothing, error * 0.5f);
                var tip = shape.NormalizedContour(shape.Layers[0].Threshold);
                var polygons = Union(SweepPressureSamples(samples, tip, shape), FillRule.NonZero);
                return CreatePressureContours(polygons, error, smoothing > 0);
            }
        }

        internal int[] Commit(VectorScene scene, int layer, Color color)
        {
            // Sample exactly the displayed pieces before merging. Keep final curves
            // within the same small screen-space tolerance as the live ink.
            var polygons = Union(new Paths64(_pieces.SelectMany(piece => piece)
                .Select(contour => ToPath(SampleClosed(contour)))), FillRule.NonZero);
            var error = Math.Max(1, _scale * 0.05f);
            var contours = CreatePressureContours(polygons, error, _smoothing > 0);
            if (contours.Length == 0) return [];
            return AppendRegions(scene, layer, [new Region(contours, polygons, color)]);
        }
    }
}
