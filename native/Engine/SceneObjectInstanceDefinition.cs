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
    int PlaybackFps,
    DrawingObjectPlaybackMode PlaybackMode,
    int HoldFrame)
{
    public PointF Position => new(X, Y);
}

internal readonly record struct InstanceStateKeyframe(int Frame, InstanceFrameState State);

internal class DrawingObjectInstanceDefinition
{
    private readonly List<InstanceStateKeyframe> _stateKeyframes = [];
    private readonly IReadOnlyList<InstanceStateKeyframe> _stateKeyframeView;
    private int _playbackFps = 30;
    private int _holdFrame;
    private DrawingObjectPlaybackMode _playbackMode = DrawingObjectPlaybackMode.PlayOnce;

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
    public int PlaybackFps
    {
        get => _playbackFps;
        set => _playbackFps = Math.Clamp(value, 1, 120);
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

    internal int ResolvePlaybackFrame(int parentFrame, int parentFps, int sourceFrameCount)
    {
        return ResolvePlaybackFrame(parentFrame, parentFps, sourceFrameCount, EvaluateState(parentFrame));
    }

    internal static int ResolvePlaybackFrame(
        int parentFrame,
        int parentFps,
        int sourceFrameCount,
        InstanceFrameState state)
    {
        var frameCount = Math.Max(1, sourceFrameCount);
        if (state.PlaybackMode == DrawingObjectPlaybackMode.HoldFrame)
        {
            return Math.Min(state.HoldFrame, frameCount - 1);
        }

        var scaledFrame = (int)Math.Min(
            int.MaxValue,
            Math.Max(0L, parentFrame) * state.PlaybackFps / Math.Max(1, parentFps));
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
            HoldFrame);
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
            || !float.IsFinite(state.ScaleZ))
        {
            return false;
        }

        normalized = state with
        {
            PlaybackFps = Math.Clamp(state.PlaybackFps, 1, 120),
            PlaybackMode = Enum.IsDefined(state.PlaybackMode) ? state.PlaybackMode : DrawingObjectPlaybackMode.PlayOnce,
            HoldFrame = Math.Max(0, state.HoldFrame)
        };
        return true;
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
