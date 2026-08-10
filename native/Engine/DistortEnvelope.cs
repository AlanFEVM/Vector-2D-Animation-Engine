using System.Numerics;
using System.Text.Json.Serialization;

namespace VectorAnimationEngine;

internal enum DistortSide : byte
{
    Top,
    Right,
    Bottom,
    Left
}

internal enum DistortHandleKind : byte
{
    Anchor,
    IncomingControl,
    OutgoingControl
}

internal readonly record struct DistortHandleRef(
    DistortSide Side,
    Guid AnchorId,
    DistortHandleKind Kind);

internal readonly record struct DistortVisualHandle(
    DistortHandleRef Reference,
    PointF Position);

internal readonly record struct DistortBoundaryHit(
    DistortSide Side,
    float SourceT,
    PointF Point,
    float Distance);

internal readonly record struct DistortBezierSegment(
    DistortSide Side,
    Guid StartAnchorId,
    Guid EndAnchorId,
    float StartSourceT,
    float EndSourceT,
    PointF Start,
    PointF Control1,
    PointF Control2,
    PointF End);

internal readonly record struct DistortBezierAnchor(
    Guid Id,
    float SourceT,
    PointF Anchor,
    PointF IncomingControl,
    PointF OutgoingControl);

internal readonly struct DistortWarp : IEquatable<DistortWarp>
{
    [JsonConstructor]
    public DistortWarp(TransformOverlayFrame source, DistortEnvelope envelope)
    {
        Source = source;
        Envelope = envelope;
    }

    public TransformOverlayFrame Source { get; }
    public DistortEnvelope Envelope { get; }
    [JsonIgnore] public bool IsValid => Source.IsValid && Envelope.IsValid;
    [JsonIgnore] public RectangleF Bounds => IsValid ? Envelope.Bounds : RectangleF.Empty;

    public static DistortWarp FromBounds(RectangleF bounds)
    {
        var source = TransformOverlayFrame.FromBounds(bounds);
        return new DistortWarp(source, DistortEnvelope.FromBounds(bounds));
    }

    public PointF Map(PointF point) => IsValid ? Envelope.Map(Source, point) : point;

    public bool TryInverseMap(PointF mappedPoint, out PointF sourcePoint)
    {
        sourcePoint = mappedPoint;
        return IsValid && Envelope.TryInverseMap(Source, mappedPoint, out sourcePoint);
    }

    public DistortWarp WithEnvelope(DistortEnvelope envelope)
    {
        var candidate = new DistortWarp(Source, envelope);
        return candidate.IsValid ? candidate : this;
    }

    public DistortWarp DeepClone() => IsValid
        ? new DistortWarp(Source, Envelope.DeepClone())
        : default;

    public DistortWarp AffineTransform(Matrix3x2 transform)
    {
        if (!IsValid || !IsFinite(transform)) return this;
        var source = TransformFrame(Source, point => TransformPoint(point, transform));
        var candidate = new DistortWarp(source, Envelope.AffineTransform(transform));
        return candidate.IsValid ? candidate : this;
    }

    public DistortWarp Transform(Func<PointF, PointF> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!IsValid) return this;
        try
        {
            var source = TransformFrame(Source, transform);
            var candidate = new DistortWarp(source, Envelope.Transform(transform));
            return candidate.IsValid ? candidate : this;
        }
        catch (Exception exception) when (exception is ArithmeticException or InvalidOperationException)
        {
            return this;
        }
    }

    public bool Equals(DistortWarp other) => Source == other.Source && Envelope == other.Envelope;

    public override bool Equals(object? obj) => obj is DistortWarp other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Source, Envelope);

    public static bool operator ==(DistortWarp left, DistortWarp right) => left.Equals(right);
    public static bool operator !=(DistortWarp left, DistortWarp right) => !left.Equals(right);

    private static TransformOverlayFrame TransformFrame(
        TransformOverlayFrame source,
        Func<PointF, PointF> transform)
    {
        var origin = transform(source.Origin);
        var topRight = transform(source.TopRight);
        var bottomLeft = transform(source.BottomLeft);
        if (!IsFinite(origin) || !IsFinite(topRight) || !IsFinite(bottomLeft)) return default;
        return new TransformOverlayFrame(
            origin,
            Subtract(topRight, origin),
            Subtract(bottomLeft, origin));
    }

    private static PointF TransformPoint(PointF point, Matrix3x2 transform)
    {
        var value = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new PointF(value.X, value.Y);
    }

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11)
        && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M21)
        && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M31)
        && float.IsFinite(matrix.M32);

    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static PointF Subtract(PointF left, PointF right) => new(left.X - right.X, left.Y - right.Y);
}

internal readonly struct DistortEnvelope : IEquatable<DistortEnvelope>
{
    private const int MaximumAnchorsPerSide = 64;
    private const int MaximumFlattenDepth = 12;
    private const float SourceParameterTolerance = 0.00001f;
    private const float BoundaryFlatnessUnits = 0.25f;

    private readonly DistortBezierAnchor[]? _top;
    private readonly DistortBezierAnchor[]? _right;
    private readonly DistortBezierAnchor[]? _bottom;
    private readonly DistortBezierAnchor[]? _left;
    private readonly bool _isValid;
    private readonly RectangleF _bounds;

    [JsonConstructor]
    public DistortEnvelope(
        DistortBezierAnchor[] top,
        DistortBezierAnchor[] right,
        DistortBezierAnchor[] bottom,
        DistortBezierAnchor[] left)
    {
        _top = CloneAnchors(top);
        _right = CloneAnchors(right);
        _bottom = CloneAnchors(bottom);
        _left = CloneAnchors(left);
        var analysis = Analyze(_top, _right, _bottom, _left);
        _isValid = analysis.IsValid;
        _bounds = analysis.Bounds;
    }

    public DistortEnvelope(
        PointF topLeft,
        PointF topRight,
        PointF bottomRight,
        PointF bottomLeft)
        : this(CreateStraightSides(topLeft, topRight, bottomRight, bottomLeft))
    {
    }

    private DistortEnvelope((
        DistortBezierAnchor[] Top,
        DistortBezierAnchor[] Right,
        DistortBezierAnchor[] Bottom,
        DistortBezierAnchor[] Left) sides)
        : this(sides.Top, sides.Right, sides.Bottom, sides.Left)
    {
    }

    public DistortBezierAnchor[] Top => CloneAnchors(_top);
    public DistortBezierAnchor[] Right => CloneAnchors(_right);
    public DistortBezierAnchor[] Bottom => CloneAnchors(_bottom);
    public DistortBezierAnchor[] Left => CloneAnchors(_left);

    [JsonIgnore] public PointF TopLeft => FirstAnchor(_top);
    [JsonIgnore] public PointF TopRight => LastAnchor(_top);
    [JsonIgnore] public PointF BottomRight => LastAnchor(_bottom);
    [JsonIgnore] public PointF BottomLeft => FirstAnchor(_bottom);
    [JsonIgnore] public bool IsValid => _isValid;
    [JsonIgnore] public RectangleF Bounds => _isValid ? _bounds : RectangleF.Empty;

    public static DistortEnvelope FromBounds(RectangleF bounds) => new(
        new PointF(bounds.Left, bounds.Top),
        new PointF(bounds.Right, bounds.Top),
        new PointF(bounds.Right, bounds.Bottom),
        new PointF(bounds.Left, bounds.Bottom));

    public DistortEnvelope DeepClone() => IsValid
        ? new DistortEnvelope(_top!, _right!, _bottom!, _left!)
        : default;

    public DistortBezierAnchor[] GetAnchors(DistortSide side) => CloneAnchors(Side(side));

    public DistortBezierSegment[] GetSegments(DistortSide side)
    {
        if (!IsValid) return [];
        var anchors = Side(side);
        var segments = new DistortBezierSegment[anchors.Length - 1];
        for (var index = 0; index < segments.Length; index++)
        {
            var start = anchors[index];
            var end = anchors[index + 1];
            segments[index] = new DistortBezierSegment(
                side,
                start.Id,
                end.Id,
                start.SourceT,
                end.SourceT,
                start.Anchor,
                start.OutgoingControl,
                end.IncomingControl,
                end.Anchor);
        }
        return segments;
    }

    public PointF[] GetBoundaryPolyline(DistortSide side) => IsValid
        ? FlattenBoundary(Side(side))
        : [];

    public PointF[] GetBoundaryContour()
    {
        if (!IsValid) return [];
        var perimeter = BuildPerimeter(_top!, _right!, _bottom!, _left!);
        if (perimeter.Length == 0) return perimeter;
        var closed = new PointF[perimeter.Length + 1];
        Array.Copy(perimeter, closed, perimeter.Length);
        closed[^1] = closed[0];
        return closed;
    }

    public DistortVisualHandle[] GetVisualHandles()
    {
        if (!IsValid) return [];
        var handles = new List<DistortVisualHandle>();
        var anchorIds = new HashSet<Guid>();
        foreach (var side in Enum.GetValues<DistortSide>())
        {
            var anchors = Side(side);
            for (var index = 0; index < anchors.Length; index++)
            {
                var anchor = anchors[index];
                if (anchorIds.Add(anchor.Id))
                {
                    handles.Add(new DistortVisualHandle(
                        new DistortHandleRef(side, anchor.Id, DistortHandleKind.Anchor),
                        anchor.Anchor));
                }
                if (index > 0)
                {
                    handles.Add(new DistortVisualHandle(
                        new DistortHandleRef(side, anchor.Id, DistortHandleKind.IncomingControl),
                        anchor.IncomingControl));
                }
                if (index + 1 < anchors.Length)
                {
                    handles.Add(new DistortVisualHandle(
                        new DistortHandleRef(side, anchor.Id, DistortHandleKind.OutgoingControl),
                        anchor.OutgoingControl));
                }
            }
        }
        return handles.ToArray();
    }

    public bool TryGetHandlePosition(DistortHandleRef handle, out PointF position)
    {
        position = PointF.Empty;
        if (!IsValid || handle.AnchorId == Guid.Empty) return false;
        var anchors = Side(handle.Side);
        var index = Array.FindIndex(anchors, item => item.Id == handle.AnchorId);
        if (index < 0) return false;
        var anchor = anchors[index];
        if (handle.Kind == DistortHandleKind.IncomingControl && index == 0
            || handle.Kind == DistortHandleKind.OutgoingControl && index == anchors.Length - 1)
        {
            return false;
        }
        position = handle.Kind switch
        {
            DistortHandleKind.Anchor => anchor.Anchor,
            DistortHandleKind.IncomingControl => anchor.IncomingControl,
            DistortHandleKind.OutgoingControl => anchor.OutgoingControl,
            _ => PointF.Empty
        };
        return Enum.IsDefined(handle.Kind) && IsFinite(position);
    }

    public DistortEnvelope WithHandle(
        DistortHandleRef handle,
        PointF startPointer,
        PointF currentPointer,
        bool preserveSmoothTangent = false)
    {
        if (!TryGetHandlePosition(handle, out _)
            || !IsFinite(startPointer)
            || !IsFinite(currentPointer))
        {
            return this;
        }

        var delta = Subtract(currentPointer, startPointer);
        if (!IsFinite(delta) || IsZero(delta)) return this;
        var sides = CloneSides();
        if (handle.Kind == DistortHandleKind.Anchor)
        {
            MoveAnchorById(sides.Top, handle.AnchorId, delta);
            MoveAnchorById(sides.Right, handle.AnchorId, delta);
            MoveAnchorById(sides.Bottom, handle.AnchorId, delta);
            MoveAnchorById(sides.Left, handle.AnchorId, delta);
        }
        else
        {
            var anchors = Side(sides, handle.Side);
            var index = Array.FindIndex(anchors, item => item.Id == handle.AnchorId);
            if (index < 0) return this;
            var anchor = anchors[index];
            if (handle.Kind == DistortHandleKind.IncomingControl)
            {
                var next = anchor with { IncomingControl = Add(anchor.IncomingControl, delta) };
                if (preserveSmoothTangent)
                {
                    next = next with
                    {
                        OutgoingControl = MirroredControl(
                            next.Anchor,
                            next.IncomingControl,
                            anchor.OutgoingControl)
                    };
                }
                anchors[index] = next;
            }
            else if (handle.Kind == DistortHandleKind.OutgoingControl)
            {
                var next = anchor with { OutgoingControl = Add(anchor.OutgoingControl, delta) };
                if (preserveSmoothTangent)
                {
                    next = next with
                    {
                        IncomingControl = MirroredControl(
                            next.Anchor,
                            next.OutgoingControl,
                            anchor.IncomingControl)
                    };
                }
                anchors[index] = next;
            }
            else
            {
                return this;
            }
        }

        return ValidCandidateOrCurrent(sides);
    }

    public DistortEnvelope WithHandle(
        TransformHandleKind handle,
        PointF startPointer,
        PointF currentPointer)
    {
        if (!IsValid || !IsFinite(startPointer) || !IsFinite(currentPointer)) return this;
        var delta = Subtract(currentPointer, startPointer);
        if (!IsFinite(delta) || IsZero(delta)) return this;
        var sides = CloneSides();
        switch (handle)
        {
            case TransformHandleKind.TopLeft:
                MoveAnchorById(sides, _top![0].Id, delta);
                break;
            case TransformHandleKind.Top:
                TranslateSideByAnchorIds(sides, DistortSide.Top, delta);
                break;
            case TransformHandleKind.TopRight:
                MoveAnchorById(sides, _top![^1].Id, delta);
                break;
            case TransformHandleKind.Right:
                TranslateSideByAnchorIds(sides, DistortSide.Right, delta);
                break;
            case TransformHandleKind.BottomRight:
                MoveAnchorById(sides, _bottom![^1].Id, delta);
                break;
            case TransformHandleKind.Bottom:
                TranslateSideByAnchorIds(sides, DistortSide.Bottom, delta);
                break;
            case TransformHandleKind.BottomLeft:
                MoveAnchorById(sides, _bottom![0].Id, delta);
                break;
            case TransformHandleKind.Left:
                TranslateSideByAnchorIds(sides, DistortSide.Left, delta);
                break;
            case TransformHandleKind.Move:
                TranslateSide(sides.Top, delta);
                TranslateSide(sides.Right, delta);
                TranslateSide(sides.Bottom, delta);
                TranslateSide(sides.Left, delta);
                break;
            default:
                return this;
        }
        return ValidCandidateOrCurrent(sides);
    }

    public bool TryInsertAnchor(
        DistortSide side,
        float sourceT,
        out DistortEnvelope envelope,
        out DistortHandleRef handle)
    {
        return TryInsertAnchor(side, sourceT, Guid.NewGuid(), out envelope, out handle);
    }

    internal bool TryInsertAnchor(
        DistortSide side,
        float sourceT,
        Guid anchorId,
        out DistortEnvelope envelope,
        out DistortHandleRef handle)
    {
        envelope = this;
        handle = default;
        if (!IsValid
            || !float.IsFinite(sourceT)
            || sourceT <= SourceParameterTolerance
            || sourceT >= 1f - SourceParameterTolerance
            || anchorId == Guid.Empty
            || ContainsAnchorId(anchorId))
        {
            return false;
        }

        var sides = CloneSides();
        var anchors = Side(sides, side);
        if (anchors.Length >= MaximumAnchorsPerSide) return false;
        var rightIndex = UpperBound(anchors, sourceT);
        if (rightIndex <= 0 || rightIndex >= anchors.Length) return false;
        var leftIndex = rightIndex - 1;
        var left = anchors[leftIndex];
        var right = anchors[rightIndex];
        if (sourceT - left.SourceT <= SourceParameterTolerance
            || right.SourceT - sourceT <= SourceParameterTolerance)
        {
            return false;
        }

        var localT = (sourceT - left.SourceT) / (right.SourceT - left.SourceT);
        SplitCubic(
            left.Anchor,
            left.OutgoingControl,
            right.IncomingControl,
            right.Anchor,
            localT,
            out var leftOutgoing,
            out var incoming,
            out var inserted,
            out var outgoing,
            out var rightIncoming);
        var expanded = new DistortBezierAnchor[anchors.Length + 1];
        Array.Copy(anchors, 0, expanded, 0, rightIndex);
        expanded[leftIndex] = left with { OutgoingControl = leftOutgoing };
        expanded[rightIndex] = new DistortBezierAnchor(
            anchorId,
            sourceT,
            inserted,
            incoming,
            outgoing);
        expanded[rightIndex + 1] = right with { IncomingControl = rightIncoming };
        if (rightIndex + 1 < anchors.Length)
        {
            Array.Copy(
                anchors,
                rightIndex + 1,
                expanded,
                rightIndex + 2,
                anchors.Length - rightIndex - 1);
        }
        SetSide(sides, side, expanded);

        var candidate = new DistortEnvelope(sides.Top, sides.Right, sides.Bottom, sides.Left);
        if (!candidate.IsValid) return false;
        envelope = candidate;
        handle = new DistortHandleRef(side, anchorId, DistortHandleKind.Anchor);
        return true;
    }

    public bool TryFindNearestBoundary(
        PointF point,
        float maximumDistance,
        out DistortBoundaryHit hit)
    {
        hit = default;
        if (!IsValid || !IsFinite(point) || !float.IsFinite(maximumDistance) || maximumDistance < 0)
        {
            return false;
        }

        var bestDistance = maximumDistance;
        var found = false;
        foreach (var side in Enum.GetValues<DistortSide>())
        {
            var anchors = Side(side);
            for (var segment = 0; segment < anchors.Length - 1; segment++)
            {
                var start = anchors[segment];
                var end = anchors[segment + 1];
                const int samples = 24;
                var previous = start.Anchor;
                for (var sample = 1; sample <= samples; sample++)
                {
                    var localEnd = sample / (float)samples;
                    var current = CubicPoint(
                        start.Anchor,
                        start.OutgoingControl,
                        end.IncomingControl,
                        end.Anchor,
                        localEnd);
                    var localStart = (sample - 1) / (float)samples;
                    var projected = SegmentProjection(point, previous, current);
                    var localT = Lerp(localStart, localEnd, projected);
                    var closest = CubicPoint(
                        start.Anchor,
                        start.OutgoingControl,
                        end.IncomingControl,
                        end.Anchor,
                        localT);
                    var distance = Distance(point, closest);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        var sourceT = Lerp(start.SourceT, end.SourceT, localT);
                        hit = new DistortBoundaryHit(side, sourceT, closest, distance);
                        found = true;
                    }
                    previous = current;
                }
            }
        }
        return found;
    }

    public PointF BoundaryPoint(DistortSide side, float sourceT)
    {
        if (!IsValid || !float.IsFinite(sourceT)) return PointF.Empty;
        return EvaluateBoundary(Side(side), sourceT).Point;
    }

    public PointF Map(TransformOverlayFrame source, PointF point)
    {
        if (!IsValid || !source.IsValid || !IsFinite(point)
            || !TryGetSourceCoordinates(source, point, out var u, out var v))
        {
            return point;
        }
        var mapped = MapCoordinates(u, v).Point;
        return IsFinite(mapped) ? mapped : point;
    }

    public bool TryInverseMap(
        TransformOverlayFrame source,
        PointF mappedPoint,
        out PointF sourcePoint)
    {
        sourcePoint = mappedPoint;
        if (!IsValid || !source.IsValid || !IsFinite(mappedPoint)) return false;

        const int coarseSteps = 10;
        const int seedCount = 8;
        Span<float> seedU = stackalloc float[seedCount];
        Span<float> seedV = stackalloc float[seedCount];
        Span<double> seedDistanceSquared = stackalloc double[seedCount];
        seedDistanceSquared.Fill(double.MaxValue);
        for (var y = 0; y <= coarseSteps; y++)
        {
            var v = y / (float)coarseSteps;
            for (var x = 0; x <= coarseSteps; x++)
            {
                var u = x / (float)coarseSteps;
                var sample = MapCoordinates(u, v).Point;
                var distanceSquared = DistanceSquared(sample, mappedPoint);
                for (var seedIndex = 0; seedIndex < seedCount; seedIndex++)
                {
                    if (distanceSquared >= seedDistanceSquared[seedIndex]) continue;
                    for (var shift = seedCount - 1; shift > seedIndex; shift--)
                    {
                        seedDistanceSquared[shift] = seedDistanceSquared[shift - 1];
                        seedU[shift] = seedU[shift - 1];
                        seedV[shift] = seedV[shift - 1];
                    }
                    seedDistanceSquared[seedIndex] = distanceSquared;
                    seedU[seedIndex] = u;
                    seedV[seedIndex] = v;
                    break;
                }
            }
        }

        var bestU = seedU[0];
        var bestV = seedV[0];
        var bestDistanceSquared = seedDistanceSquared[0];
        for (var seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            RefineInverseCandidate(
                mappedPoint,
                seedU[seedIndex],
                seedV[seedIndex],
                out var u,
                out var v,
                out var distanceSquared);
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            bestU = u;
            bestV = v;
        }

        var diagonal = Math.Max(1f, MathF.Sqrt(_bounds.Width * _bounds.Width + _bounds.Height * _bounds.Height));
        var tolerance = Math.Max(0.05f, diagonal * 0.0001f);
        if (bestDistanceSquared > tolerance * tolerance) return false;
        sourcePoint = new PointF(
            source.Origin.X + source.AxisX.X * bestU + source.AxisY.X * bestV,
            source.Origin.Y + source.AxisX.Y * bestU + source.AxisY.Y * bestV);
        return IsFinite(sourcePoint);
    }

    private void RefineInverseCandidate(
        PointF target,
        float seedU,
        float seedV,
        out float resolvedU,
        out float resolvedV,
        out double resolvedDistanceSquared)
    {
        var u = Math.Clamp(seedU, 0, 1);
        var v = Math.Clamp(seedV, 0, 1);
        var distanceSquared = DistanceSquared(MapCoordinates(u, v).Point, target);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var sample = MapCoordinates(u, v);
            var error = Subtract(sample.Point, target);
            var duLengthSquared = Dot(sample.Du, sample.Du);
            var dvLengthSquared = Dot(sample.Dv, sample.Dv);
            var duDv = Dot(sample.Du, sample.Dv);
            var damping = Math.Max(0.000000001d, (duLengthSquared + dvLengthSquared + 1) * 0.00000001d);
            var a = duLengthSquared + damping;
            var b = duDv;
            var c = dvLengthSquared + damping;
            var determinant = a * c - b * b;
            if (!double.IsFinite(determinant) || determinant <= 0.000000000001d) break;

            var errorDu = Dot(error, sample.Du);
            var errorDv = Dot(error, sample.Dv);
            var deltaU = (c * errorDu - b * errorDv) / determinant;
            var deltaV = (a * errorDv - b * errorDu) / determinant;
            var largestStep = Math.Max(Math.Abs(deltaU), Math.Abs(deltaV));
            if (largestStep > 0.25d)
            {
                var scale = 0.25d / largestStep;
                deltaU *= scale;
                deltaV *= scale;
            }

            var improved = false;
            var step = 1f;
            for (var lineSearch = 0; lineSearch < 8; lineSearch++)
            {
                var candidateU = Math.Clamp(u - (float)deltaU * step, 0, 1);
                var candidateV = Math.Clamp(v - (float)deltaV * step, 0, 1);
                var candidateDistanceSquared = DistanceSquared(
                    MapCoordinates(candidateU, candidateV).Point,
                    target);
                if (candidateDistanceSquared < distanceSquared)
                {
                    u = candidateU;
                    v = candidateV;
                    improved = true;
                    if (distanceSquared - candidateDistanceSquared <= 0.000000000001d)
                    {
                        distanceSquared = candidateDistanceSquared;
                        iteration = 32;
                    }
                    else
                    {
                        distanceSquared = candidateDistanceSquared;
                    }
                    break;
                }
                step *= 0.5f;
            }

            if (!improved || distanceSquared <= 0.000000000001d) break;
        }

        resolvedU = u;
        resolvedV = v;
        resolvedDistanceSquared = distanceSquared;
    }

    public DistortEnvelope AffineTransform(Matrix3x2 transform)
    {
        if (!IsValid || !IsFinite(transform)) return this;
        var transformed = new DistortEnvelope(
            TransformAnchors(_top!, transform),
            TransformAnchors(_right!, transform),
            TransformAnchors(_bottom!, transform),
            TransformAnchors(_left!, transform));
        return transformed.IsValid ? transformed : this;
    }

    public DistortEnvelope Transform(Func<PointF, PointF> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!IsValid) return this;
        try
        {
            var transformed = new DistortEnvelope(
                TransformAnchors(_top!, transform),
                TransformAnchors(_right!, transform),
                TransformAnchors(_bottom!, transform),
                TransformAnchors(_left!, transform));
            return transformed.IsValid ? transformed : this;
        }
        catch (Exception exception) when (exception is ArithmeticException or InvalidOperationException)
        {
            return this;
        }
    }

    public bool Equals(DistortEnvelope other)
    {
        return AnchorsEqual(_top, other._top)
            && AnchorsEqual(_right, other._right)
            && AnchorsEqual(_bottom, other._bottom)
            && AnchorsEqual(_left, other._left);
    }

    public override bool Equals(object? obj) => obj is DistortEnvelope other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        AddAnchorsToHash(ref hash, _top);
        AddAnchorsToHash(ref hash, _right);
        AddAnchorsToHash(ref hash, _bottom);
        AddAnchorsToHash(ref hash, _left);
        return hash.ToHashCode();
    }

    public static bool operator ==(DistortEnvelope left, DistortEnvelope right) => left.Equals(right);
    public static bool operator !=(DistortEnvelope left, DistortEnvelope right) => !left.Equals(right);

    private DistortEnvelope ValidCandidateOrCurrent(MutableSides sides)
    {
        var candidate = new DistortEnvelope(sides.Top, sides.Right, sides.Bottom, sides.Left);
        return candidate.IsValid ? candidate : this;
    }

    private MutableSides CloneSides() => new(
        CloneAnchors(_top),
        CloneAnchors(_right),
        CloneAnchors(_bottom),
        CloneAnchors(_left));

    private DistortBezierAnchor[] Side(DistortSide side)
    {
        return side switch
        {
            DistortSide.Top => _top ?? [],
            DistortSide.Right => _right ?? [],
            DistortSide.Bottom => _bottom ?? [],
            DistortSide.Left => _left ?? [],
            _ => []
        };
    }

    private static DistortBezierAnchor[] Side(MutableSides sides, DistortSide side)
    {
        return side switch
        {
            DistortSide.Top => sides.Top,
            DistortSide.Right => sides.Right,
            DistortSide.Bottom => sides.Bottom,
            DistortSide.Left => sides.Left,
            _ => []
        };
    }

    private static void SetSide(MutableSides sides, DistortSide side, DistortBezierAnchor[] anchors)
    {
        switch (side)
        {
            case DistortSide.Top:
                sides.Top = anchors;
                break;
            case DistortSide.Right:
                sides.Right = anchors;
                break;
            case DistortSide.Bottom:
                sides.Bottom = anchors;
                break;
            case DistortSide.Left:
                sides.Left = anchors;
                break;
        }
    }

    private bool ContainsAnchorId(Guid id)
    {
        return _top!.Any(item => item.Id == id)
            || _right!.Any(item => item.Id == id)
            || _bottom!.Any(item => item.Id == id)
            || _left!.Any(item => item.Id == id);
    }

    private static void MoveAnchorById(MutableSides sides, Guid anchorId, PointF delta)
    {
        MoveAnchorById(sides.Top, anchorId, delta);
        MoveAnchorById(sides.Right, anchorId, delta);
        MoveAnchorById(sides.Bottom, anchorId, delta);
        MoveAnchorById(sides.Left, anchorId, delta);
    }

    private static void MoveAnchorById(DistortBezierAnchor[] anchors, Guid anchorId, PointF delta)
    {
        for (var index = 0; index < anchors.Length; index++)
        {
            if (anchors[index].Id != anchorId) continue;
            var anchor = anchors[index];
            anchors[index] = anchor with
            {
                Anchor = Add(anchor.Anchor, delta),
                IncomingControl = Add(anchor.IncomingControl, delta),
                OutgoingControl = Add(anchor.OutgoingControl, delta)
            };
        }
    }

    private static void TranslateSide(DistortBezierAnchor[] anchors, PointF delta)
    {
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            anchors[index] = anchor with
            {
                Anchor = Add(anchor.Anchor, delta),
                IncomingControl = Add(anchor.IncomingControl, delta),
                OutgoingControl = Add(anchor.OutgoingControl, delta)
            };
        }
    }

    private static void TranslateSideByAnchorIds(MutableSides sides, DistortSide side, PointF delta)
    {
        var anchorIds = Side(sides, side).Select(anchor => anchor.Id).ToArray();
        foreach (var anchorId in anchorIds) MoveAnchorById(sides, anchorId, delta);
    }

    private static PointF MirroredControl(PointF anchor, PointF movedControl, PointF oldOpposite)
    {
        var direction = Subtract(anchor, movedControl);
        var directionLength = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        var opposite = Subtract(oldOpposite, anchor);
        var oppositeLength = MathF.Sqrt(opposite.X * opposite.X + opposite.Y * opposite.Y);
        if (directionLength <= float.Epsilon) return anchor;
        if (oppositeLength <= float.Epsilon) oppositeLength = directionLength;
        var scale = oppositeLength / directionLength;
        return new PointF(anchor.X + direction.X * scale, anchor.Y + direction.Y * scale);
    }

    private static void SplitCubic(
        PointF p0,
        PointF p1,
        PointF p2,
        PointF p3,
        float t,
        out PointF leftOutgoing,
        out PointF incoming,
        out PointF anchor,
        out PointF outgoing,
        out PointF rightIncoming)
    {
        var p01 = Lerp(p0, p1, t);
        var p12 = Lerp(p1, p2, t);
        var p23 = Lerp(p2, p3, t);
        var p012 = Lerp(p01, p12, t);
        var p123 = Lerp(p12, p23, t);
        leftOutgoing = p01;
        incoming = p012;
        anchor = Lerp(p012, p123, t);
        outgoing = p123;
        rightIncoming = p23;
    }

    private static int UpperBound(IReadOnlyList<DistortBezierAnchor> anchors, float sourceT)
    {
        var low = 0;
        var high = anchors.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (anchors[middle].SourceT <= sourceT) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool TryGetSourceCoordinates(
        TransformOverlayFrame source,
        PointF point,
        out float u,
        out float v)
    {
        var determinant = Cross(source.AxisX, source.AxisY);
        if (Math.Abs(determinant) <= double.Epsilon)
        {
            u = v = 0;
            return false;
        }
        var offset = Subtract(point, source.Origin);
        u = (float)(Cross(offset, source.AxisY) / determinant);
        v = (float)(Cross(source.AxisX, offset) / determinant);
        return float.IsFinite(u) && float.IsFinite(v);
    }

    private CoonsSample MapCoordinates(float u, float v)
    {
        var top = EvaluateBoundary(_top!, u);
        var right = EvaluateBoundary(_right!, v);
        var bottom = EvaluateBoundary(_bottom!, u);
        var left = EvaluateBoundary(_left!, v);
        var topLeft = _top![0].Anchor;
        var topRight = _top[^1].Anchor;
        var bottomLeft = _bottom![0].Anchor;
        var bottomRight = _bottom[^1].Anchor;
        var bilinear = Bilinear(topLeft, topRight, bottomLeft, bottomRight, u, v);
        var point = Subtract(
            Add(
                Add(Scale(top.Point, 1f - v), Scale(bottom.Point, v)),
                Add(Scale(left.Point, 1f - u), Scale(right.Point, u))),
            bilinear);
        var bilinearDu = Add(
            Scale(Subtract(topRight, topLeft), 1f - v),
            Scale(Subtract(bottomRight, bottomLeft), v));
        var bilinearDv = Add(
            Scale(Subtract(bottomLeft, topLeft), 1f - u),
            Scale(Subtract(bottomRight, topRight), u));
        var du = Subtract(
            Add(
                Add(Scale(top.Derivative, 1f - v), Scale(bottom.Derivative, v)),
                Subtract(right.Point, left.Point)),
            bilinearDu);
        var dv = Subtract(
            Add(
                Subtract(bottom.Point, top.Point),
                Add(Scale(left.Derivative, 1f - u), Scale(right.Derivative, u))),
            bilinearDv);
        return new CoonsSample(point, du, dv);
    }

    private static BoundarySample EvaluateBoundary(DistortBezierAnchor[] anchors, float sourceT)
    {
        var segment = sourceT <= anchors[0].SourceT
            ? 0
            : sourceT >= anchors[^1].SourceT
                ? anchors.Length - 2
                : Math.Clamp(UpperBound(anchors, sourceT) - 1, 0, anchors.Length - 2);
        var start = anchors[segment];
        var end = anchors[segment + 1];
        var span = end.SourceT - start.SourceT;
        var localT = span <= SourceParameterTolerance ? 0f : (sourceT - start.SourceT) / span;
        var point = CubicPoint(
            start.Anchor,
            start.OutgoingControl,
            end.IncomingControl,
            end.Anchor,
            localT);
        var derivative = Scale(
            CubicDerivative(
                start.Anchor,
                start.OutgoingControl,
                end.IncomingControl,
                end.Anchor,
                localT),
            1f / span);
        return new BoundarySample(point, derivative);
    }

    private static EnvelopeAnalysis Analyze(
        DistortBezierAnchor[] top,
        DistortBezierAnchor[] right,
        DistortBezierAnchor[] bottom,
        DistortBezierAnchor[] left)
    {
        if (!ValidateSide(top)
            || !ValidateSide(right)
            || !ValidateSide(bottom)
            || !ValidateSide(left)
            || !ValidateCorner(top[0], left[0])
            || !ValidateCorner(top[^1], right[0])
            || !ValidateCorner(bottom[0], left[^1])
            || !ValidateCorner(bottom[^1], right[^1])
            || !ValidateAnchorIds(top, right, bottom, left))
        {
            return default;
        }

        var bounds = CubicBounds(top, right, bottom, left);
        if (!IsFinite(bounds)
            || bounds.Width <= 0.000001f && bounds.Height <= 0.000001f)
        {
            return default;
        }

        var temporary = new DistortEnvelope(top, right, bottom, left, skipAnalysis: true);
        var uParameters = ValidationParameters(top, bottom);
        var vParameters = ValidationParameters(left, right);
        foreach (var v in vParameters)
        {
            foreach (var u in uParameters)
            {
                var sample = temporary.MapCoordinates(u, v);
                if (!IsFinite(sample.Point) || !IsFinite(sample.Du) || !IsFinite(sample.Dv)) return default;
            }
        }

        return new EnvelopeAnalysis(true, bounds);
    }

    private DistortEnvelope(
        DistortBezierAnchor[] top,
        DistortBezierAnchor[] right,
        DistortBezierAnchor[] bottom,
        DistortBezierAnchor[] left,
        bool skipAnalysis)
    {
        _top = top;
        _right = right;
        _bottom = bottom;
        _left = left;
        _isValid = skipAnalysis;
        _bounds = RectangleF.Empty;
    }

    private static bool ValidateSide(DistortBezierAnchor[] anchors)
    {
        if (anchors.Length is < 2 or > MaximumAnchorsPerSide) return false;
        if (Math.Abs(anchors[0].SourceT) > SourceParameterTolerance
            || Math.Abs(anchors[^1].SourceT - 1f) > SourceParameterTolerance)
        {
            return false;
        }
        var ids = new HashSet<Guid>();
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            if (anchor.Id == Guid.Empty
                || !ids.Add(anchor.Id)
                || !float.IsFinite(anchor.SourceT)
                || !IsFinite(anchor.Anchor)
                || !IsFinite(anchor.IncomingControl)
                || !IsFinite(anchor.OutgoingControl)
                || index > 0 && anchor.SourceT - anchors[index - 1].SourceT <= SourceParameterTolerance)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateCorner(DistortBezierAnchor first, DistortBezierAnchor second)
    {
        return first.Id == second.Id && first.Anchor == second.Anchor;
    }

    private static bool ValidateAnchorIds(params DistortBezierAnchor[][] sides)
    {
        var occurrences = new Dictionary<Guid, int>();
        foreach (var side in sides)
        {
            foreach (var anchor in side)
            {
                occurrences[anchor.Id] = occurrences.GetValueOrDefault(anchor.Id) + 1;
            }
        }
        return occurrences.Values.All(count => count is 1 or 2)
            && occurrences.Values.Count(count => count == 2) == 4;
    }

    private static float[] ValidationParameters(
        DistortBezierAnchor[] first,
        DistortBezierAnchor[] second)
    {
        var knots = first.Select(item => item.SourceT)
            .Concat(second.Select(item => item.SourceT))
            .Append(0f)
            .Append(1f)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var values = new SortedSet<float>();
        const int uniformSegments = 16;
        for (var index = 0; index <= uniformSegments; index++) values.Add(index / (float)uniformSegments);
        for (var index = 0; index < knots.Length - 1; index++)
        {
            var start = knots[index];
            var end = knots[index + 1];
            values.Add(start);
            values.Add(Lerp(start, end, 0.25f));
            values.Add(Lerp(start, end, 0.5f));
            values.Add(Lerp(start, end, 0.75f));
            values.Add(end);
        }
        return values.ToArray();
    }

    private static PointF[] BuildPerimeter(
        DistortBezierAnchor[] top,
        DistortBezierAnchor[] right,
        DistortBezierAnchor[] bottom,
        DistortBezierAnchor[] left)
    {
        var points = new List<PointF>();
        AppendBoundary(points, FlattenBoundary(top), reverse: false);
        AppendBoundary(points, FlattenBoundary(right), reverse: false);
        AppendBoundary(points, FlattenBoundary(bottom), reverse: true);
        AppendBoundary(points, FlattenBoundary(left), reverse: true);
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        return points.ToArray();
    }

    private static void AppendBoundary(List<PointF> destination, PointF[] source, bool reverse)
    {
        var values = reverse ? source.Reverse() : source;
        foreach (var point in values)
        {
            if (destination.Count == 0 || destination[^1] != point) destination.Add(point);
        }
    }

    private static PointF[] FlattenBoundary(DistortBezierAnchor[] anchors)
    {
        var points = new List<PointF> { anchors[0].Anchor };
        for (var index = 0; index < anchors.Length - 1; index++)
        {
            var start = anchors[index];
            var end = anchors[index + 1];
            FlattenCubic(
                points,
                start.Anchor,
                start.OutgoingControl,
                end.IncomingControl,
                end.Anchor,
                0);
        }
        return points.ToArray();
    }

    private static void FlattenCubic(
        List<PointF> points,
        PointF p0,
        PointF p1,
        PointF p2,
        PointF p3,
        int depth)
    {
        if (depth >= MaximumFlattenDepth
            || Math.Max(DistanceToLine(p1, p0, p3), DistanceToLine(p2, p0, p3)) <= BoundaryFlatnessUnits)
        {
            if (points[^1] != p3) points.Add(p3);
            return;
        }
        SplitCubic(
            p0,
            p1,
            p2,
            p3,
            0.5f,
            out var leftOutgoing,
            out var incoming,
            out var middle,
            out var outgoing,
            out var rightIncoming);
        FlattenCubic(points, p0, leftOutgoing, incoming, middle, depth + 1);
        FlattenCubic(points, middle, outgoing, rightIncoming, p3, depth + 1);
    }

    private static RectangleF CubicBounds(params DistortBezierAnchor[][] sides)
    {
        var hasPoint = false;
        var left = 0f;
        var right = 0f;
        var top = 0f;
        var bottom = 0f;
        foreach (var side in sides)
        {
            for (var index = 0; index < side.Length - 1; index++)
            {
                var start = side[index];
                var end = side[index + 1];
                AddCubicBounds(
                    start.Anchor,
                    start.OutgoingControl,
                    end.IncomingControl,
                    end.Anchor,
                    ref hasPoint,
                    ref left,
                    ref top,
                    ref right,
                    ref bottom);
            }
        }
        return hasPoint ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
    }

    private static void AddCubicBounds(
        PointF p0,
        PointF p1,
        PointF p2,
        PointF p3,
        ref bool hasPoint,
        ref float left,
        ref float top,
        ref float right,
        ref float bottom)
    {
        AddBoundsPoint(p0, ref hasPoint, ref left, ref top, ref right, ref bottom);
        AddBoundsPoint(p3, ref hasPoint, ref left, ref top, ref right, ref bottom);
        foreach (var t in CubicExtrema(p0.X, p1.X, p2.X, p3.X))
        {
            AddBoundsPoint(CubicPoint(p0, p1, p2, p3, t), ref hasPoint, ref left, ref top, ref right, ref bottom);
        }
        foreach (var t in CubicExtrema(p0.Y, p1.Y, p2.Y, p3.Y))
        {
            AddBoundsPoint(CubicPoint(p0, p1, p2, p3, t), ref hasPoint, ref left, ref top, ref right, ref bottom);
        }
    }

    private static IEnumerable<float> CubicExtrema(float p0, float p1, float p2, float p3)
    {
        var a = -p0 + 3f * p1 - 3f * p2 + p3;
        var b = 2f * (p0 - 2f * p1 + p2);
        var c = p1 - p0;
        if (Math.Abs(a) <= 0.0000001f)
        {
            if (Math.Abs(b) <= 0.0000001f) yield break;
            var root = -c / b;
            if (root is > 0f and < 1f) yield return root;
            yield break;
        }
        var discriminant = b * b - 4f * a * c;
        if (discriminant < 0) yield break;
        var squareRoot = MathF.Sqrt(discriminant);
        var first = (-b + squareRoot) / (2f * a);
        var second = (-b - squareRoot) / (2f * a);
        if (first is > 0f and < 1f) yield return first;
        if (second is > 0f and < 1f && Math.Abs(second - first) > 0.000001f) yield return second;
    }

    private static void AddBoundsPoint(
        PointF point,
        ref bool hasPoint,
        ref float left,
        ref float top,
        ref float right,
        ref float bottom)
    {
        if (!hasPoint)
        {
            left = right = point.X;
            top = bottom = point.Y;
            hasPoint = true;
            return;
        }
        left = Math.Min(left, point.X);
        top = Math.Min(top, point.Y);
        right = Math.Max(right, point.X);
        bottom = Math.Max(bottom, point.Y);
    }

    private static DistortBezierAnchor[] TransformAnchors(
        DistortBezierAnchor[] anchors,
        Matrix3x2 transform)
    {
        var result = new DistortBezierAnchor[anchors.Length];
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            result[index] = anchor with
            {
                Anchor = Transform(anchor.Anchor, transform),
                IncomingControl = Transform(anchor.IncomingControl, transform),
                OutgoingControl = Transform(anchor.OutgoingControl, transform)
            };
        }
        return result;
    }

    private static DistortBezierAnchor[] TransformAnchors(
        DistortBezierAnchor[] anchors,
        Func<PointF, PointF> transform)
    {
        var result = new DistortBezierAnchor[anchors.Length];
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            var transformedAnchor = transform(anchor.Anchor);
            var transformedIncoming = transform(anchor.IncomingControl);
            var transformedOutgoing = transform(anchor.OutgoingControl);
            if (!IsFinite(transformedAnchor)
                || !IsFinite(transformedIncoming)
                || !IsFinite(transformedOutgoing))
            {
                return [];
            }
            result[index] = anchor with
            {
                Anchor = transformedAnchor,
                IncomingControl = transformedIncoming,
                OutgoingControl = transformedOutgoing
            };
        }
        return result;
    }


    private static (
        DistortBezierAnchor[] Top,
        DistortBezierAnchor[] Right,
        DistortBezierAnchor[] Bottom,
        DistortBezierAnchor[] Left) CreateStraightSides(
        PointF topLeft,
        PointF topRight,
        PointF bottomRight,
        PointF bottomLeft)
    {
        var topLeftId = Guid.NewGuid();
        var topRightId = Guid.NewGuid();
        var bottomRightId = Guid.NewGuid();
        var bottomLeftId = Guid.NewGuid();
        return (
            StraightBoundary(topLeft, topRight, topLeftId, topRightId),
            StraightBoundary(topRight, bottomRight, topRightId, bottomRightId),
            StraightBoundary(bottomLeft, bottomRight, bottomLeftId, bottomRightId),
            StraightBoundary(topLeft, bottomLeft, topLeftId, bottomLeftId));
    }

    private static DistortBezierAnchor[] StraightBoundary(
        PointF start,
        PointF end,
        Guid startId,
        Guid endId)
    {
        return
        [
            new DistortBezierAnchor(
                startId,
                0f,
                start,
                start,
                Lerp(start, end, 1f / 3f)),
            new DistortBezierAnchor(
                endId,
                1f,
                end,
                Lerp(start, end, 2f / 3f),
                end)
        ];
    }

    private static DistortBezierAnchor[] CloneAnchors(DistortBezierAnchor[]? anchors) =>
        anchors?.ToArray() ?? [];

    private static PointF FirstAnchor(DistortBezierAnchor[]? anchors) =>
        anchors is { Length: > 0 } ? anchors[0].Anchor : PointF.Empty;

    private static PointF LastAnchor(DistortBezierAnchor[]? anchors) =>
        anchors is { Length: > 0 } ? anchors[^1].Anchor : PointF.Empty;

    private static bool AnchorsEqual(DistortBezierAnchor[]? left, DistortBezierAnchor[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        return left.AsSpan().SequenceEqual(right);
    }

    private static void AddAnchorsToHash(ref HashCode hash, DistortBezierAnchor[]? anchors)
    {
        hash.Add(anchors?.Length ?? -1);
        if (anchors is null) return;
        foreach (var anchor in anchors) hash.Add(anchor);
    }

    private static PointF CubicPoint(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        var inverse = 1f - t;
        var inverseSquared = inverse * inverse;
        var tSquared = t * t;
        return new PointF(
            inverseSquared * inverse * p0.X
                + 3f * inverseSquared * t * p1.X
                + 3f * inverse * tSquared * p2.X
                + tSquared * t * p3.X,
            inverseSquared * inverse * p0.Y
                + 3f * inverseSquared * t * p1.Y
                + 3f * inverse * tSquared * p2.Y
                + tSquared * t * p3.Y);
    }

    private static PointF CubicDerivative(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        var inverse = 1f - t;
        return new PointF(
            3f * inverse * inverse * (p1.X - p0.X)
                + 6f * inverse * t * (p2.X - p1.X)
                + 3f * t * t * (p3.X - p2.X),
            3f * inverse * inverse * (p1.Y - p0.Y)
                + 6f * inverse * t * (p2.Y - p1.Y)
                + 3f * t * t * (p3.Y - p2.Y));
    }

    private static PointF Bilinear(
        PointF topLeft,
        PointF topRight,
        PointF bottomLeft,
        PointF bottomRight,
        float u,
        float v)
    {
        return Add(
            Add(Scale(topLeft, (1f - u) * (1f - v)), Scale(topRight, u * (1f - v))),
            Add(Scale(bottomLeft, (1f - u) * v), Scale(bottomRight, u * v)));
    }

    private static PointF Transform(PointF point, Matrix3x2 transform)
    {
        var value = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return new PointF(value.X, value.Y);
    }

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11)
        && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M21)
        && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M31)
        && float.IsFinite(matrix.M32);

    private static bool IsFinite(RectangleF bounds) =>
        float.IsFinite(bounds.X)
        && float.IsFinite(bounds.Y)
        && float.IsFinite(bounds.Width)
        && float.IsFinite(bounds.Height);

    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsZero(PointF point) => point.X == 0f && point.Y == 0f;

    private static PointF Add(PointF left, PointF right) => new(left.X + right.X, left.Y + right.Y);

    private static PointF Subtract(PointF left, PointF right) => new(left.X - right.X, left.Y - right.Y);

    private static PointF Scale(PointF point, float amount) => new(point.X * amount, point.Y * amount);

    private static PointF Lerp(PointF start, PointF end, float amount) => new(
        start.X + (end.X - start.X) * amount,
        start.Y + (end.Y - start.Y) * amount);

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;

    private static float Distance(PointF left, PointF right) => MathF.Sqrt((float)DistanceSquared(left, right));

    private static double DistanceSquared(PointF left, PointF right)
    {
        var dx = (double)right.X - left.X;
        var dy = (double)right.Y - left.Y;
        return dx * dx + dy * dy;
    }

    private static float DistanceToLine(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon) return Distance(point, start);
        return MathF.Abs((point.X - start.X) * dy - (point.Y - start.Y) * dx)
            / MathF.Sqrt(lengthSquared);
    }

    private static float SegmentProjection(PointF point, PointF start, PointF end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= float.Epsilon) return 0f;
        return Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0f, 1f);
    }

    private static double Cross(PointF left, PointF right) =>
        (double)left.X * right.Y - (double)left.Y * right.X;

    private static double Dot(PointF left, PointF right) =>
        (double)left.X * right.X + (double)left.Y * right.Y;

    private sealed class MutableSides(
        DistortBezierAnchor[] top,
        DistortBezierAnchor[] right,
        DistortBezierAnchor[] bottom,
        DistortBezierAnchor[] left)
    {
        public DistortBezierAnchor[] Top { get; set; } = top;
        public DistortBezierAnchor[] Right { get; set; } = right;
        public DistortBezierAnchor[] Bottom { get; set; } = bottom;
        public DistortBezierAnchor[] Left { get; set; } = left;
    }

    private readonly record struct BoundarySample(PointF Point, PointF Derivative);

    private readonly record struct CoonsSample(PointF Point, PointF Du, PointF Dv);

    private readonly record struct EnvelopeAnalysis(bool IsValid, RectangleF Bounds);
}
