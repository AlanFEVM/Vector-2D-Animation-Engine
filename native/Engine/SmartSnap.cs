namespace VectorAnimationEngine;

internal enum SmartSnapKind { Edge, Center, Guide, Canvas, Gap, Point, Grid }
internal readonly record struct SmartSnapLine(PointF Start, PointF End, string Label = "");
internal readonly record struct SmartSnapAxis(float Correction, float Coordinate, float SourceOffset,
    SmartSnapKind Kind, int Target, SmartSnapLine Line, SmartSnapLine? Comparison = null);
internal readonly record struct SmartSnapResult(PointF Correction, SmartSnapAxis? X, SmartSnapAxis? Y)
{
    internal bool Snapped => X.HasValue || Y.HasValue;
}

// Immutable candidates; coordinates are world units, tolerance is supplied by the view.
// Axis arrays are sorted once per gesture, not once per pointer message.
internal sealed class SmartSnapIndex
{
    private readonly record struct Axis(float Coordinate, float Min, float Max, SmartSnapKind Kind, int Target);
    private readonly Axis[] _x;
    private readonly Axis[] _y;
    private readonly RectangleF[] _bounds;
    private readonly RangeNode? _rows;
    private readonly RangeNode? _columns;
    private readonly PointF[] _points;

    internal SmartSnapIndex(IEnumerable<RectangleF> bounds, IEnumerable<RulerGuide>? guides = null,
        RectangleF? canvas = null, IEnumerable<PointF>? points = null)
    {
        _bounds = bounds.Where(Finite).ToArray();
        _rows = RangeNode.Build(_bounds.OrderBy(b => b.Top).ToArray(), false);
        _columns = RangeNode.Build(_bounds.OrderBy(b => b.Left).ToArray(), true);
        _points = (points ?? []).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y))
            .Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var x = new List<Axis>();
        var y = new List<Axis>();
        for (var i = 0; i < _bounds.Length; i++) Add(_bounds[i], i, false);
        if (canvas is { } page && Finite(page)) Add(page, -1, true);
        var guideId = -2;
        foreach (var guide in guides ?? [])
        {
            if (!float.IsFinite(guide.Position)) continue;
            (guide.Horizontal ? y : x).Add(new Axis(guide.Position, float.NegativeInfinity,
                float.PositiveInfinity, SmartSnapKind.Guide, guideId--));
        }
        _x = x.OrderBy(a => a.Coordinate).ThenBy(a => a.Kind).ThenBy(a => a.Target).ToArray();
        _y = y.OrderBy(a => a.Coordinate).ThenBy(a => a.Kind).ThenBy(a => a.Target).ToArray();

        void Add(RectangleF b, int id, bool page)
        {
            x.Add(new(b.Left, b.Top, b.Bottom, page ? SmartSnapKind.Canvas : SmartSnapKind.Edge, id));
            x.Add(new(b.Right, b.Top, b.Bottom, page ? SmartSnapKind.Canvas : SmartSnapKind.Edge, id));
            x.Add(new(b.Left + b.Width / 2, b.Top, b.Bottom, page ? SmartSnapKind.Canvas : SmartSnapKind.Center, id));
            y.Add(new(b.Top, b.Left, b.Right, page ? SmartSnapKind.Canvas : SmartSnapKind.Edge, id));
            y.Add(new(b.Bottom, b.Left, b.Right, page ? SmartSnapKind.Canvas : SmartSnapKind.Edge, id));
            y.Add(new(b.Top + b.Height / 2, b.Left, b.Right, page ? SmartSnapKind.Canvas : SmartSnapKind.Center, id));
        }
    }

    internal SmartSnapResult Resolve(RectangleF moving, float tolerance, bool align, bool tight, bool guides,
        bool gaps, SmartSnapResult previous = default, float? gridStep = null)
    {
        if (!Finite(moving) || !float.IsFinite(tolerance) || tolerance <= 0) return default;
        var x = ResolveAxis(_x, moving.Left, moving.Width, moving.Top, moving.Bottom, tolerance,
            align, tight, guides, previous.X, true);
        var y = ResolveAxis(_y, moving.Top, moving.Height, moving.Left, moving.Right, tolerance,
            align, tight, guides, previous.Y, false);
        if (gaps && moving.Width > 0 && moving.Height > 0)
        {
            x = ResolveGap(moving, tolerance, true, x, previous.X);
            y = ResolveGap(moving, tolerance, false, y, previous.Y);
        }
        if (gridStep is > 0 && float.IsFinite(gridStep.Value))
        {
            x ??= Grid(moving.Left, gridStep.Value);
            y ??= Grid(moving.Top, gridStep.Value);
        }
        return new(new PointF(x?.Correction ?? 0, y?.Correction ?? 0), x, y);
    }

    internal bool TryPoint(PointF point, float tolerance, out PointF snapped)
    {
        snapped = point;
        var found = false;
        var best = tolerance * tolerance;
        var lo = 0;
        var hi = _points.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (_points[mid].X < point.X - tolerance) lo = mid + 1; else hi = mid; }
        for (var i = lo; i < _points.Length && _points[i].X <= point.X + tolerance; i++)
        {
            var p = _points[i];
            var d = (p.X - point.X) * (p.X - point.X) + (p.Y - point.Y) * (p.Y - point.Y);
            if (d > best) continue;
            if (found && d == best) continue;
            best = d; snapped = p; found = true;
        }
        return found;
    }

    private static SmartSnapAxis? ResolveAxis(Axis[] entries, float start, float size, float min, float max,
        float tolerance, bool align, bool tight, bool guides, SmartSnapAxis? previous, bool vertical)
    {
        SmartSnapAxis? best = null;
        var bestDistance = float.PositiveInfinity;
        for (var source = 0; source < 3; source++)
        {
            var offset = source * size / 2;
            var value = start + offset;
            var radius = tolerance * 1.5f;
            var lo = 0; var hi = entries.Length;
            while (lo < hi) { var mid = (lo + hi) / 2; if (entries[mid].Coordinate < value - radius) lo = mid + 1; else hi = mid; }
            for (var i = lo; i < entries.Length && entries[i].Coordinate <= value + radius; i++)
            {
                var a = entries[i];
                if (a.Kind is SmartSnapKind.Guide or SmartSnapKind.Canvas) { if (!guides) continue; }
                else if (!align && !(tight && source != 1 && a.Kind == SmartSnapKind.Edge && a.Max >= min && a.Min <= max)) continue;
                var retained = previous is { } p && p.Target == a.Target && p.Kind == a.Kind && p.SourceOffset == offset && p.Coordinate == a.Coordinate;
                var distance = Math.Abs(a.Coordinate - value);
                if (distance > (retained ? radius : tolerance)) continue;
                // A retained target wins until the release radius is exceeded.
                var score = retained ? -1 : distance;
                if (score >= bestDistance) continue;
                bestDistance = score;
                var from = float.IsFinite(a.Min) ? Math.Min(min, a.Min) : min - tolerance * 4;
                var to = float.IsFinite(a.Max) ? Math.Max(max, a.Max) : max + tolerance * 4;
                var line = vertical
                    ? new SmartSnapLine(new(a.Coordinate, from), new(a.Coordinate, to))
                    : new SmartSnapLine(new(from, a.Coordinate), new(to, a.Coordinate));
                best = new(a.Coordinate - value, a.Coordinate, offset, a.Kind, a.Target, line);
            }
        }
        return best;
    }

    private SmartSnapAxis? ResolveGap(RectangleF moving, float tolerance, bool horizontal, SmartSnapAxis? current, SmartSnapAxis? previous)
    {
        RectangleF? before = null, before2 = null, after = null, after2 = null;
        var start = horizontal ? moving.Left : moving.Top;
        var end = horizontal ? moving.Right : moving.Bottom;
        var tree = horizontal ? _rows : _columns;
        if (tree is null) return current;
        foreach (var b in tree.Query(horizontal ? moving.Top : moving.Left, horizontal ? moving.Bottom : moving.Right))
        {
            var low = horizontal ? b.Left : b.Top;
            var high = horizontal ? b.Right : b.Bottom;
            if (high <= start)
            {
                if (before is not { } p || high > High(p)) { before2 = before; before = b; }
                else if (before2 is not { } p2 || high > High(p2)) before2 = b;
            }
            if (low >= end)
            {
                if (after is not { } n || low < Low(n)) { after2 = after; after = b; }
                else if (after2 is not { } n2 || low < Low(n2)) after2 = b;
            }
        }
        if (before is { } left && after is { } right)
        {
            var gap = (Low(right) - High(left) - (end - start)) / 2;
            Consider(High(left) + gap - start, gap, High(left), High(left) + gap, Low(right) - gap, Low(right), 0);
        }
        if (before is { } p1 && before2 is { } p0)
        {
            var gap = Low(p1) - High(p0);
            Consider(High(p1) + gap - start, gap, High(p0), Low(p1), High(p1), High(p1) + gap, 1);
        }
        if (after is { } n1 && after2 is { } n0)
        {
            var gap = Low(n0) - High(n1);
            Consider(Low(n1) - gap - end, gap, Low(n1) - gap, Low(n1), High(n1), Low(n0), 2);
        }
        return current;

        float Low(RectangleF b) => horizontal ? b.Left : b.Top;
        float High(RectangleF b) => horizontal ? b.Right : b.Bottom;
        void Consider(float correction, float gap, float a, float b, float c, float d, int mode)
        {
            var retained = previous is { Kind: SmartSnapKind.Gap } old && old.Target == mode && Math.Abs(old.Coordinate - (start + correction)) < 0.001f;
            if (gap < 0 || Math.Abs(correction) > tolerance * (retained ? 1.5f : 1)) return;
            if (current is { } hit && (hit.Kind != SmartSnapKind.Gap && previous is { } prior && prior.Kind == hit.Kind && prior.Coordinate == hit.Coordinate
                || !retained && Math.Abs(hit.Correction) <= Math.Abs(correction))) return;
            var cross = horizontal ? moving.Top + moving.Height / 2 : moving.Left + moving.Width / 2;
            var label = $"= {VectorUnits.ToPixels(gap):0.##} px";
            SmartSnapLine Line(float first, float last, string text) => horizontal
                ? new(new(first, cross), new(last, cross), text) : new(new(cross, first), new(cross, last), text);
            current = new(correction, start + correction, 0, SmartSnapKind.Gap, mode, Line(a, b, label), Line(c, d, label));
        }
    }

    private static SmartSnapAxis Grid(float value, float step)
    {
        var target = MathF.Round(value / step) * step;
        return new(target - value, target, 0, SmartSnapKind.Grid, -1, default);
    }
    internal static bool Finite(RectangleF b) => float.IsFinite(b.Left) && float.IsFinite(b.Top)
        && float.IsFinite(b.Right) && float.IsFinite(b.Bottom) && b.Width >= 0 && b.Height >= 0;

    // Interval tree filters equal-spacing candidates to the affected row/column.
    private sealed class RangeNode(RectangleF bounds, bool horizontal)
    {
        private readonly RectangleF _value = bounds;
        private readonly bool _horizontal = horizontal;
        private RangeNode? _left, _right;
        private float _maximum = horizontal ? bounds.Right : bounds.Bottom;
        internal static RangeNode? Build(RectangleF[] sorted, bool horizontal)
        {
            return BuildRange(0, sorted.Length);
            RangeNode? BuildRange(int first, int count)
            {
                if (count == 0) return null;
                var middle = first + count / 2;
                var node = new RangeNode(sorted[middle], horizontal)
                {
                    _left = BuildRange(first, middle - first),
                    _right = BuildRange(middle + 1, first + count - middle - 1)
                };
                node._maximum = Math.Max(node._maximum, Math.Max(node._left?._maximum ?? float.NegativeInfinity, node._right?._maximum ?? float.NegativeInfinity));
                return node;
            }
        }
        internal IEnumerable<RectangleF> Query(float min, float max)
        {
            if (_maximum <= min) yield break;
            if (_left is not null) foreach (var b in _left.Query(min, max)) yield return b;
            var low = _horizontal ? _value.Left : _value.Top;
            var high = _horizontal ? _value.Right : _value.Bottom;
            if (low >= max) yield break;
            if (high > min) yield return _value;
            if (_right is not null) foreach (var b in _right.Query(min, max)) yield return b;
        }
    }
}
