namespace VectorAnimationEngine;

/// <summary>
/// The interpolation mode assigned to a timeline span.
/// </summary>
internal enum TimelineTweenKind : byte
{
    None = 0,
    Classic = 1,
    Shape = 2
}

/// <summary>
/// Reasons a tween span cannot be created on a timeline track.
/// </summary>
internal enum TimelineTweenValidationError : byte
{
    None = 0,
    InvalidKind,
    InvalidRange,
    OutOfRange,
    MissingStartKeyframe,
    MissingEndKeyframe,
    BlankEndpoint,
    OverlapsExisting,
    AlreadyExists
}

/// <summary>
/// A normalized point on a tween's time-to-progress easing curve.
/// </summary>
internal readonly record struct TweenCurveAnchor(float Time, float Value);

/// <summary>
/// An inclusive frame interval on which the target is interpolated.
/// Start and end are populated keyframes; the scene may materialize
/// intermediate editable cels while retaining this span metadata.
/// </summary>
internal readonly record struct TimelineTween(int StartFrame, int EndFrame, TimelineTweenKind Kind)
{
    internal const int MaxCurveAnchorCount = 64;

    private static readonly TweenCurveAnchor[] LinearCurveAnchors =
    [
        new(0, 0),
        new(1, 1)
    ];

    private readonly TweenCurveAnchor[]? _curveAnchors;

    /// <summary>
    /// Gets a detached copy of the curve. Assigning through an object
    /// initializer also clones the supplied array for snapshot deserialization.
    /// </summary>
    public TweenCurveAnchor[] CurveAnchors
    {
        get => (_curveAnchors ?? LinearCurveAnchors).ToArray();
        init => _curveAnchors = value is null or { Length: 0 } ? null : value.ToArray();
    }

    public bool IsValid => StartFrame >= 0
        && EndFrame > StartFrame
        && Kind is TimelineTweenKind.Classic or TimelineTweenKind.Shape
        && HasValidCurve;

    public bool HasValidCurve => IsValidCurve(_curveAnchors);

    public bool IsLinearCurve => _curveAnchors is null
        || _curveAnchors.Length == 0
        || _curveAnchors.Length == 2
            && _curveAnchors[0] == LinearCurveAnchors[0]
            && _curveAnchors[1] == LinearCurveAnchors[1];

    public int Length => IsValid ? EndFrame - StartFrame + 1 : 0;

    public bool Contains(int frame) => IsValid && frame >= StartFrame && frame <= EndFrame;

    public float ProgressAt(int frame)
    {
        if (!IsValid || frame <= StartFrame) return 0;
        if (frame >= EndFrame) return 1;
        var linearProgress = (float)(frame - StartFrame) / (EndFrame - StartFrame);
        return ProgressAtNormalized(linearProgress);
    }

    public float ProgressAtNormalized(float time)
    {
        if (!float.IsFinite(time)) return 0;
        return EvaluateCurve(Math.Clamp(time, 0, 1));
    }

    internal TimelineTween WithCurveAnchors(TweenCurveAnchor[] normalizedAnchors) =>
        new(StartFrame, EndFrame, Kind) { CurveAnchors = normalizedAnchors };

    internal bool CurveEquals(IReadOnlyList<TweenCurveAnchor> normalizedAnchors)
    {
        var current = _curveAnchors ?? LinearCurveAnchors;
        if (current.Length != normalizedAnchors.Count) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (current[index] != normalizedAnchors[index]) return false;
        }

        return true;
    }

    internal static bool TryNormalizeCurveAnchors(
        IEnumerable<TweenCurveAnchor>? anchors,
        out TweenCurveAnchor[] normalized)
    {
        normalized = [];
        if (anchors is null) return false;

        var collected = new List<TweenCurveAnchor>();
        foreach (var anchor in anchors)
        {
            if (collected.Count == MaxCurveAnchorCount) return false;
            collected.Add(anchor);
        }

        if (collected.Count == 0)
        {
            normalized = LinearCurveAnchors.ToArray();
            return true;
        }

        collected.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        if (!IsValidCurve(collected)) return false;
        normalized = collected.ToArray();
        return true;
    }

    private float EvaluateCurve(float time)
    {
        var anchors = _curveAnchors;
        if (anchors is null || anchors.Length <= 2) return time;

        var low = 0;
        var high = anchors.Length - 1;
        while (low + 1 < high)
        {
            var middle = low + (high - low) / 2;
            if (anchors[middle].Time <= time) low = middle;
            else high = middle;
        }

        var left = anchors[low];
        var right = anchors[low + 1];
        var interval = right.Time - left.Time;
        var localTime = (time - left.Time) / interval;
        var leftTangent = CurveTangent(anchors, low);
        var rightTangent = CurveTangent(anchors, low + 1);
        var localTime2 = localTime * localTime;
        var localTime3 = localTime2 * localTime;
        var value = (2 * localTime3 - 3 * localTime2 + 1) * left.Value
            + (localTime3 - 2 * localTime2 + localTime) * interval * leftTangent
            + (-2 * localTime3 + 3 * localTime2) * right.Value
            + (localTime3 - localTime2) * interval * rightTangent;
        return Math.Clamp(value, left.Value, right.Value);
    }

    private static float CurveTangent(IReadOnlyList<TweenCurveAnchor> anchors, int index)
    {
        if (index <= 0) return CurveSlope(anchors[0], anchors[1]);
        if (index >= anchors.Count - 1) return CurveSlope(anchors[^2], anchors[^1]);

        var previousInterval = anchors[index].Time - anchors[index - 1].Time;
        var nextInterval = anchors[index + 1].Time - anchors[index].Time;
        var previousSlope = (anchors[index].Value - anchors[index - 1].Value) / previousInterval;
        var nextSlope = (anchors[index + 1].Value - anchors[index].Value) / nextInterval;
        if (previousSlope <= 0 || nextSlope <= 0) return 0;

        // Weighted harmonic tangents are shape preserving and cannot create
        // progress overshoot between monotone anchors.
        var previousWeight = 2 * nextInterval + previousInterval;
        var nextWeight = nextInterval + 2 * previousInterval;
        return (previousWeight + nextWeight)
            / (previousWeight / previousSlope + nextWeight / nextSlope);
    }

    private static float CurveSlope(TweenCurveAnchor left, TweenCurveAnchor right) =>
        (right.Value - left.Value) / (right.Time - left.Time);

    private static bool IsValidCurve(IReadOnlyList<TweenCurveAnchor>? anchors)
    {
        if (anchors is null || anchors.Count == 0) return true;
        if (anchors.Count is < 2 or > MaxCurveAnchorCount
            || anchors[0] != LinearCurveAnchors[0]
            || anchors[^1] != LinearCurveAnchors[1])
        {
            return false;
        }

        var previous = anchors[0];
        for (var index = 1; index < anchors.Count; index++)
        {
            var current = anchors[index];
            if (!float.IsFinite(current.Time)
                || !float.IsFinite(current.Value)
                || current.Time <= previous.Time
                || current.Time is < 0 or > 1
                || current.Value < previous.Value
                || current.Value is < 0 or > 1)
            {
                return false;
            }

            previous = current;
        }

        return true;
    }
}
