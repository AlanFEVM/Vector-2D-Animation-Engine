using System.Drawing;
using System.Numerics;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private sealed record GuideSample(double Distance, int Segment, float T);

    private sealed class GuideMotion(EdgeSegment[] segments, List<GuideSample> samples, Vector2 anchor, bool orient, string location)
    {
        internal InstanceFrameState Apply(InstanceFrameState state, float progress, float initialRotation)
        {
            var (point, tangent) = Evaluate(progress);
            if (orient)
            {
                var (_, initialTangent) = Evaluate(0);
                state = state with { RotationZ = initialRotation + Angle(tangent) - Angle(initialTangent) };
            }
            var offset = Vector2.TransformNormal(anchor, DrawingObjectInstanceDefinition.CreateLinearTransform(state));
            return state with { X = VectorUnits.FromPixels(point.X - offset.X), Y = VectorUnits.FromPixels(point.Y - offset.Y) };
        }

        internal (Vector2 Point, Vector2 Tangent) Evaluate(float progress)
        {
            var distance = Math.Clamp(progress, 0, 1) * samples[^1].Distance;
            var low = 0; var high = samples.Count - 1;
            while (low + 1 < high)
            {
                var middle = (low + high) / 2;
                if (samples[middle].Distance < distance) low = middle; else high = middle;
            }
            var a = samples[low]; var b = samples[high];
            var startT = a.Segment == b.Segment ? a.T : 0;
            var amount = (float)((distance - a.Distance) / (b.Distance - a.Distance));
            var t = startT + (b.T - startT) * amount;
            var segment = segments[b.Segment];
            var start = ToVector(segment.Start); var end = ToVector(segment.End);
            var control = segment.Control is { } c ? ToVector(c) : (start + end) / 2;
            var tangent = 2 * ((1 - t) * (control - start) + t * (end - control));
            if (tangent.LengthSquared() < 1e-12f) tangent = end - start;
            if (tangent.LengthSquared() < 1e-12f) throw Failure(location, "Guide tangent is undefined at a sampled frame.");
            return ((1 - t) * (1 - t) * start + 2 * (1 - t) * t * control + t * t * end, tangent);
        }
    }

    private static Vector2 ToVector(PointF point) => new(point.X, point.Y);
    private static float Angle(Vector2 vector) => MathF.Atan2(vector.Y, vector.X) * 180 / MathF.PI;

    private static GuideMotion ReadGuideMotion(XElement guide, XElement tween, XElement startElement, XElement endElement, int start, int end, string location)
    {
        var exposures = Children(Child(guide, "frames"), "DOMFrame").Where(frame =>
            Integer(frame, "index", 0, 0, MaximumFrameCount - 1) <= start
            && Integer(frame, "index", 0, 0, MaximumFrameCount - 1) + Integer(frame, "duration", 1, 1, MaximumFrameCount) >= end).ToArray();
        if (exposures.Length != 1 || Text(exposures[0], "tweenType", "none") is not ("none" or "")
            || Child(exposures[0], "motionObjectXML") is not null || Child(exposures[0], "morphShape") is not null)
            throw Failure(location, "Guide geometry must remain static throughout the classic tween exposure.");
        var parsed = new List<EdgeSegment>(); var count = 0;
        foreach (var element in Children(Child(exposures[0], "elements"))) Read(element, Matrix3x2.Identity);
        if (parsed.Count == 0) throw Failure(location, "Bound guide has no supported stroke path.");
        var adjacency = new Dictionary<PointF, List<int>>();
        for (var i = 0; i < parsed.Count; i++)
        {
            if (parsed[i].Start == parsed[i].End) throw Failure(location, "Closed or degenerate guide segments are not supported.");
            Attach(parsed[i].Start, i); Attach(parsed[i].End, i);
        }
        var endpoints = adjacency.Where(pair => pair.Value.Count == 1).Select(pair => pair.Key).ToArray();
        if (endpoints.Length != 2 || adjacency.Any(pair => pair.Value.Count > 2))
            throw Failure(location, "Guide must be one unbranched open path; closed, disconnected or branching paths are ambiguous.");
        var anchor = ReadAnchor(startElement);
        if (Vector2.Distance(anchor, ReadAnchor(endElement)) > 0.001f) throw Failure(location, "Changing guide transformation points are not supported.");
        var from = Vector2.Transform(anchor, ReadMatrix(startElement));
        var to = Vector2.Transform(anchor, ReadMatrix(endElement));
        const float snapTolerance = 0.25f;
        var first = endpoints[0]; var last = endpoints[1];
        if (Vector2.Distance(from, ToVector(last)) <= snapTolerance && Vector2.Distance(to, ToVector(first)) <= snapTolerance)
            (first, last) = (last, first);
        if (Vector2.Distance(from, ToVector(first)) > snapTolerance || Vector2.Distance(to, ToVector(last)) > snapTolerance)
            throw Failure(location, "Guide binding could not be determined: transformation points must match the two path endpoints within 0.25 stage pixel.");
        var ordered = new List<EdgeSegment>(); var current = first; var used = new HashSet<int>();
        while (current != last)
        {
            var next = adjacency[current].Where(index => !used.Contains(index)).ToArray();
            if (next.Length != 1) throw Failure(location, "Guide path has an ambiguous connection.");
            var index = next[0]; used.Add(index);
            var segment = parsed[index].Start == current ? parsed[index] : parsed[index].Reverse();
            ordered.Add(segment); current = segment.End;
        }
        if (used.Count != parsed.Count) throw Failure(location, "Guide contains disconnected stroke paths.");
        var samples = new List<GuideSample> { new(0, 0, 0) }; double length = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var segment = ordered[i]; var a = ToVector(segment.Start); var b = ToVector(segment.End);
            Subdivide(a, segment.Control is { } control ? ToVector(control) : (a + b) / 2, b, 0, 1, 0.01 / ordered.Count, 0, i);
        }
        if (!double.IsFinite(length) || length <= 0) throw Failure(location, "Guide length is invalid.");
        var result = new GuideMotion(ordered.ToArray(), samples, anchor, Boolean(tween, "motionTweenOrientToPath", false), location);
        if (Boolean(tween, "motionTweenOrientToPath", false))
        {
            var initial = StateFromMatrix(new DrawingObjectInstanceDefinition().EvaluateState(0), ReadMatrix(startElement), location);
            var final = StateFromMatrix(initial, ReadMatrix(endElement), location);
            var expected = result.Apply(final, 1, initial.RotationZ).RotationZ;
            var difference = MathF.IEEERemainder(expected - final.RotationZ, 360);
            if (MathF.Abs(difference) > 0.1f) throw Failure(location, "Guide orientation and endpoint rotation disagree; custom rotation requires visual conversion.");
        }
        return result;

        void Attach(PointF point, int index)
        {
            if (!adjacency.TryGetValue(point, out var list)) adjacency[point] = list = [];
            list.Add(index);
        }
        Vector2 ReadAnchor(XElement element)
        {
            var point = Child(Child(element, "transformationPoint"), "Point");
            return point is null ? Vector2.Zero : new(Number(point, "x", 0), Number(point, "y", 0));
        }
        void Read(XElement element, Matrix3x2 inherited)
        {
            var matrix = ReadMatrix(element) * inherited;
            if (element.Name.LocalName == "DOMGroup")
            {
                foreach (var member in Children(Child(element, "members"))) Read(member, matrix);
                return;
            }
            if (element.Name.LocalName != "DOMShape") throw Failure(location, "Bound guide must contain only stroke shapes or groups.");
            foreach (var edge in Children(Child(element, "edges"), "Edge"))
            {
                if (Integer(edge, "strokeStyle", 0, 0, 65535) == 0) continue;
                if (edge.Attribute("edges") is null) throw Failure(location, "Guide stroke has no supported edge geometry.");
                foreach (var run in ParseEdge(Required(edge, "edges"), location, ref count))
                    foreach (var segment in run.Segments)
                    {
                        PointF Transform(PointF point) { var value = Vector2.Transform(ToVector(point), matrix); return new(value.X, value.Y); }
                        parsed.Add(new(Transform(segment.Start), Transform(segment.End), segment.Control is { } c ? Transform(c) : null));
                    }
            }
        }
        void Subdivide(Vector2 a, Vector2 c, Vector2 b, float t0, float t1, double tolerance, int depth, int index)
        {
            var chord = Vector2.Distance(a, b); var polygon = Vector2.Distance(a, c) + Vector2.Distance(c, b);
            // Both geometric flatness and total arc-length error are bounded. A
            // collinear quadratic with uneven speed still needs parameter subdivision.
            var midpoint = (a + 2 * c + b) / 4;
            if (polygon - chord <= tolerance && Vector2.Distance(midpoint, (a + b) / 2) <= 0.0025f)
            {
                if (samples.Count >= 200_000) throw Failure(location, "Guide sampling exceeds the bounded point budget.");
                if (chord > 0) { length += chord; samples.Add(new(length, index, t1)); }
                return;
            }
            if (depth >= 24) throw Failure(location, "Guide cannot be sampled within the arc-length tolerance.");
            var left = (a + c) / 2; var right = (c + b) / 2; var middle = (left + right) / 2; var tm = (t0 + t1) / 2;
            Subdivide(a, left, middle, t0, tm, tolerance / 2, depth + 1, index);
            Subdivide(middle, right, b, tm, t1, tolerance / 2, depth + 1, index);
        }
    }
}
