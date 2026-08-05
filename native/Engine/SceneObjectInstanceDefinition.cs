using System.Numerics;

namespace VectorAnimationEngine;

internal readonly record struct InstancePositionKeyframe(int Frame, float X, float Y);

internal enum DrawingObjectPlaybackMode
{
    PlayOnce,
    Loop,
    HoldFrame
}

internal readonly record struct InstanceFrameState(
    bool Visible,
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ,
    float SkewX,
    float SkewY,
    float ScaleX,
    float ScaleY,
    float ScaleZ,
    decimal PlaybackFps,
    DrawingObjectPlaybackMode PlaybackMode,
    int HoldFrame)
{
    private readonly bool _hasStoredAlpha;
    private readonly float _storedAlpha;
    private readonly bool _hasStoredTintArgb;
    private readonly int _storedTintArgb;

    public PointF Position => new(X, Y);

    // Zero-initialized structs come from project files written before appearance fields existed.
    public float Alpha
    {
        get => _hasStoredAlpha ? _storedAlpha : 1f;
        init
        {
            _storedAlpha = value;
            _hasStoredAlpha = true;
        }
    }

    public int TintArgb
    {
        get => _hasStoredTintArgb ? _storedTintArgb | unchecked((int)0xff000000) : unchecked((int)0xffffffff);
        init
        {
            _storedTintArgb = value | unchecked((int)0xff000000);
            _hasStoredTintArgb = true;
        }
    }
}

internal readonly record struct InstanceStateKeyframe(int Frame, InstanceFrameState State);

internal readonly record struct InstanceAnchorCompensation(
    InstanceFrameState BaseState,
    InstanceStateKeyframe[] StateKeyframes);

internal class DrawingObjectInstanceDefinition
{
    private readonly List<InstanceStateKeyframe> _stateKeyframes = [];
    private readonly IReadOnlyList<InstanceStateKeyframe> _stateKeyframeView;
    private decimal _playbackFps = 30m;
    private int _holdFrame;
    private DrawingObjectPlaybackMode _playbackMode = DrawingObjectPlaybackMode.PlayOnce;
    private float _alpha = 1f;
    private int _tintArgb = unchecked((int)0xffffffff);

    public DrawingObjectInstanceDefinition()
    {
        _stateKeyframeView = _stateKeyframes.AsReadOnly();
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DrawingObjectId { get; init; } = "";
    public string SceneLayerId { get; set; } = "";
    public string Name { get; set; } = "Instance";
    public bool Visible { get; set; } = true;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float SkewX { get; set; }
    public float SkewY { get; set; }
    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;
    public float ScaleZ { get; set; } = 1;
    public float Alpha
    {
        get => _alpha;
        set => _alpha = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }
    public int TintArgb
    {
        get => _tintArgb;
        set => _tintArgb = value | unchecked((int)0xff000000);
    }
    public decimal PlaybackFps
    {
        get => _playbackFps;
        set => _playbackFps = Math.Clamp(value, 1m, 120m);
    }
    public DrawingObjectPlaybackMode PlaybackMode
    {
        get => _playbackMode;
        set => _playbackMode = Enum.IsDefined(value) ? value : DrawingObjectPlaybackMode.PlayOnce;
    }
    public int HoldFrame
    {
        get => _holdFrame;
        set => _holdFrame = Math.Max(0, value);
    }
    public IReadOnlyList<InstanceStateKeyframe> StateKeyframes => _stateKeyframeView;
    public IReadOnlyList<InstancePositionKeyframe> PositionKeyframes => _stateKeyframes
        .Select(keyframe => new InstancePositionKeyframe(keyframe.Frame, keyframe.State.X, keyframe.State.Y))
        .ToArray();

    internal DrawingObjectInstanceDefinition Clone()
    {
        var clone = new DrawingObjectInstanceDefinition
        {
            Id = Id,
            DrawingObjectId = DrawingObjectId,
            SceneLayerId = SceneLayerId,
            Name = Name,
            Visible = Visible,
            X = X,
            Y = Y,
            Z = Z,
            RotationX = RotationX,
            RotationY = RotationY,
            RotationZ = RotationZ,
            SkewX = SkewX,
            SkewY = SkewY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            ScaleZ = ScaleZ,
            Alpha = Alpha,
            TintArgb = TintArgb,
            PlaybackFps = PlaybackFps,
            PlaybackMode = PlaybackMode,
            HoldFrame = HoldFrame
        };
        clone.RestoreStateKeyframes(StateKeyframes);
        return clone;
    }

    internal int ResolvePlaybackFrame(int parentFrame, decimal parentFps, int sourceFrameCount)
    {
        return ResolvePlaybackFrame(parentFrame, parentFps, sourceFrameCount, EvaluateState(parentFrame));
    }

    internal static int ResolvePlaybackFrame(
        int parentFrame,
        decimal parentFps,
        int sourceFrameCount,
        InstanceFrameState state)
    {
        var frameCount = Math.Max(1, sourceFrameCount);
        if (state.PlaybackMode == DrawingObjectPlaybackMode.HoldFrame)
        {
            return Math.Min(state.HoldFrame, frameCount - 1);
        }

        var playbackFps = Math.Clamp(state.PlaybackFps, 1m, 120m);
        var scaledFrame = (int)Math.Min(
            int.MaxValue,
            Math.Floor(Math.Max(0, parentFrame) * playbackFps / Math.Max(1m, parentFps)));
        return state.PlaybackMode == DrawingObjectPlaybackMode.Loop
            ? scaledFrame % frameCount
            : Math.Min(scaledFrame, frameCount - 1);
    }

    public InstanceFrameState EvaluateState(int frame)
    {
        frame = Math.Max(0, frame);
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) index--;
        return index >= 0 ? _stateKeyframes[index].State : BaseState();
    }

    public PointF EvaluatePosition(int frame) => EvaluateState(frame).Position;

    internal bool SetStateAtFrame(int frame, InstanceFrameState state)
    {
        frame = Math.Max(0, frame);
        if (!TryNormalizeState(state, out var normalized)) return false;
        if (frame == 0)
        {
            var changed = BaseState() != normalized;
            ApplyBaseState(normalized);
            return changed;
        }

        var index = LowerBoundStateKeyframe(frame);
        var next = new InstanceStateKeyframe(frame, normalized);
        if (index < _stateKeyframes.Count && _stateKeyframes[index].Frame == frame)
        {
            if (_stateKeyframes[index] == next) return false;
            _stateKeyframes[index] = next;
            return true;
        }

        _stateKeyframes.Insert(index, next);
        return true;
    }

    internal bool SetPositionAtFrame(int frame, PointF position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return false;
        return SetStateAtFrame(frame, EvaluateState(frame) with { X = position.X, Y = position.Y });
    }

    internal bool RemoveStateKeyframe(int frame)
    {
        if (frame <= 0) return false;
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) return false;
        _stateKeyframes.RemoveAt(index);
        return true;
    }

    internal bool RemovePositionKeyframe(int frame) => RemoveStateKeyframe(frame);

    internal void InsertStateFrames(int frame, int count)
    {
        if (frame < 0 || count <= 0) return;
        for (var index = _stateKeyframes.Count - 1; index >= 0; index--)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame <= frame) break;
            _stateKeyframes[index] = keyframe with { Frame = keyframe.Frame + count };
        }
    }

    internal void InsertPositionFrames(int frame, int count) => InsertStateFrames(frame, count);

    internal void RemoveStateFrames(int frame, int count)
    {
        if (frame < 0 || count <= 0 || _stateKeyframes.Count == 0) return;
        var removeEnd = frame + count;
        var continuationIndex = LowerBoundStateKeyframe(removeEnd + 1) - 1;
        var preserveContinuation = continuationIndex >= 0
            && _stateKeyframes[continuationIndex].Frame >= frame
            && _stateKeyframes[continuationIndex].Frame < removeEnd;
        var continuation = preserveContinuation ? _stateKeyframes[continuationIndex].State : default;

        for (var index = _stateKeyframes.Count - 1; index >= 0; index--)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame >= removeEnd)
            {
                _stateKeyframes[index] = keyframe with { Frame = keyframe.Frame - count };
            }
            else if (keyframe.Frame >= frame)
            {
                _stateKeyframes.RemoveAt(index);
            }
        }

        if (_stateKeyframes.Count > 0 && _stateKeyframes[0].Frame == 0)
        {
            ApplyBaseState(_stateKeyframes[0].State);
            _stateKeyframes.RemoveAt(0);
        }

        if (preserveContinuation) SetStateAtFrame(frame, continuation);
    }

    internal void RemovePositionFrames(int frame, int count) => RemoveStateFrames(frame, count);

    internal void RestoreStateKeyframes(IEnumerable<InstanceStateKeyframe>? keyframes)
    {
        _stateKeyframes.Clear();
        if (keyframes is null) return;
        foreach (var keyframe in keyframes
                     .Where(item => item.Frame >= 0 && TryNormalizeState(item.State, out _))
                     .GroupBy(item => item.Frame)
                     .Select(group => group.Last())
                     .OrderBy(item => item.Frame))
        {
            TryNormalizeState(keyframe.State, out var state);
            if (keyframe.Frame == 0) ApplyBaseState(state);
            else _stateKeyframes.Add(new InstanceStateKeyframe(keyframe.Frame, state));
        }
    }

    internal void RestorePositionKeyframes(IEnumerable<InstancePositionKeyframe>? keyframes)
    {
        if (keyframes is null) return;
        foreach (var keyframe in keyframes
                     .Where(item => item.Frame >= 0 && float.IsFinite(item.X) && float.IsFinite(item.Y))
                     .GroupBy(item => item.Frame)
                     .Select(group => group.Last())
                     .OrderBy(item => item.Frame))
        {
            SetPositionAtFrame(keyframe.Frame, new PointF(keyframe.X, keyframe.Y));
        }
    }

    internal bool TryPlanAnchorCompensation(PointF sourceDelta, out InstanceAnchorCompensation compensation)
    {
        compensation = default;
        if (!TryCompensateState(BaseState(), sourceDelta, out var baseState)) return false;

        var keyframes = new InstanceStateKeyframe[_stateKeyframes.Count];
        for (var index = 0; index < _stateKeyframes.Count; index++)
        {
            var keyframe = _stateKeyframes[index];
            if (!TryCompensateState(keyframe.State, sourceDelta, out var state)) return false;
            keyframes[index] = keyframe with { State = state };
        }

        compensation = new InstanceAnchorCompensation(baseState, keyframes);
        return true;
    }

    internal void ApplyAnchorCompensation(InstanceAnchorCompensation compensation)
    {
        ApplyBaseState(compensation.BaseState);
        _stateKeyframes.Clear();
        _stateKeyframes.AddRange(compensation.StateKeyframes);
    }

    internal static Matrix3x2 CreateLinearTransform(InstanceFrameState state)
    {
        return Matrix3x2.CreateScale(state.ScaleX, state.ScaleY)
            * Matrix3x2.CreateSkew(state.SkewX * MathF.PI / 180f, state.SkewY * MathF.PI / 180f)
            * Matrix3x2.CreateRotation(state.RotationZ * MathF.PI / 180f);
    }

    internal static InstanceFrameState InterpolateState(
        InstanceFrameState source,
        InstanceFrameState target,
        float progress)
    {
        if (!float.IsFinite(progress) || progress <= 0) return source;
        if (progress >= 1) return target;

        return source with
        {
            X = Lerp(source.X, target.X, progress),
            Y = Lerp(source.Y, target.Y, progress),
            Z = Lerp(source.Z, target.Z, progress),
            RotationX = LerpAngle(source.RotationX, target.RotationX, progress),
            RotationY = LerpAngle(source.RotationY, target.RotationY, progress),
            RotationZ = LerpAngle(source.RotationZ, target.RotationZ, progress),
            SkewX = Lerp(source.SkewX, target.SkewX, progress),
            SkewY = Lerp(source.SkewY, target.SkewY, progress),
            ScaleX = Lerp(source.ScaleX, target.ScaleX, progress),
            ScaleY = Lerp(source.ScaleY, target.ScaleY, progress),
            ScaleZ = Lerp(source.ScaleZ, target.ScaleZ, progress),
            Alpha = Lerp(source.Alpha, target.Alpha, progress),
            TintArgb = LerpArgb(source.TintArgb, target.TintArgb, progress)
        };
    }

    private static float Lerp(float source, float target, float progress) =>
        source + (target - source) * progress;

    private static float LerpAngle(float source, float target, float progress)
    {
        var delta = (target - source) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return source + delta * progress;
    }

    private static int LerpArgb(int sourceArgb, int targetArgb, float progress)
    {
        var source = Color.FromArgb(sourceArgb);
        var target = Color.FromArgb(targetArgb);
        return Color.FromArgb(
            255,
            (int)MathF.Round(Lerp(source.R, target.R, progress)),
            (int)MathF.Round(Lerp(source.G, target.G, progress)),
            (int)MathF.Round(Lerp(source.B, target.B, progress))).ToArgb();
    }

    private InstanceFrameState BaseState()
    {
        return new InstanceFrameState(
            Visible,
            X,
            Y,
            Z,
            RotationX,
            RotationY,
            RotationZ,
            SkewX,
            SkewY,
            ScaleX,
            ScaleY,
            ScaleZ,
            PlaybackFps,
            PlaybackMode,
            HoldFrame)
        {
            Alpha = Alpha,
            TintArgb = TintArgb
        };
    }

    private void ApplyBaseState(InstanceFrameState state)
    {
        Visible = state.Visible;
        X = state.X;
        Y = state.Y;
        Z = state.Z;
        RotationX = state.RotationX;
        RotationY = state.RotationY;
        RotationZ = state.RotationZ;
        SkewX = state.SkewX;
        SkewY = state.SkewY;
        ScaleX = state.ScaleX;
        ScaleY = state.ScaleY;
        ScaleZ = state.ScaleZ;
        Alpha = state.Alpha;
        TintArgb = state.TintArgb;
        PlaybackFps = state.PlaybackFps;
        PlaybackMode = state.PlaybackMode;
        HoldFrame = state.HoldFrame;
    }

    private static bool TryNormalizeState(InstanceFrameState state, out InstanceFrameState normalized)
    {
        normalized = state;
        if (!float.IsFinite(state.X)
            || !float.IsFinite(state.Y)
            || !float.IsFinite(state.Z)
            || !float.IsFinite(state.RotationX)
            || !float.IsFinite(state.RotationY)
            || !float.IsFinite(state.RotationZ)
            || !float.IsFinite(state.SkewX)
            || !float.IsFinite(state.SkewY)
            || !float.IsFinite(state.ScaleX)
            || !float.IsFinite(state.ScaleY)
            || !float.IsFinite(state.ScaleZ)
            || !float.IsFinite(state.Alpha))
        {
            return false;
        }

        normalized = state with
        {
            Alpha = Math.Clamp(state.Alpha, 0f, 1f),
            TintArgb = state.TintArgb | unchecked((int)0xff000000),
            PlaybackFps = Math.Clamp(state.PlaybackFps, 1m, 120m),
            PlaybackMode = Enum.IsDefined(state.PlaybackMode) ? state.PlaybackMode : DrawingObjectPlaybackMode.PlayOnce,
            HoldFrame = Math.Max(0, state.HoldFrame)
        };
        return true;
    }

    private static bool TryCompensateState(
        InstanceFrameState state,
        PointF sourceDelta,
        out InstanceFrameState compensated)
    {
        var offset = Vector2.Transform(
            new Vector2(sourceDelta.X, sourceDelta.Y),
            CreateLinearTransform(state));
        var x = state.X + offset.X;
        var y = state.Y + offset.Y;
        compensated = state with { X = x, Y = y };
        return float.IsFinite(x) && float.IsFinite(y);
    }

    private int LowerBoundStateKeyframe(int frame)
    {
        var low = 0;
        var high = _stateKeyframes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_stateKeyframes[middle].Frame < frame) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}

internal sealed class SceneObjectInstanceDefinition : DrawingObjectInstanceDefinition;
