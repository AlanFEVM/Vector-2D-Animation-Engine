using System.Diagnostics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private const float SpatialGizmoSpringStiffness = 260f;
    private const float SpatialGizmoSpringDamping = 22f;
    private const float SpatialGizmoPositionEpsilon = 0.0005f;
    private const float SpatialGizmoVelocityEpsilon = 0.004f;
    private const float SpatialGizmoHiddenScale = 0.68f;
    private const float SpatialGizmoMaximumRenderScale = 1.08f;

    private readonly System.Windows.Forms.Timer _spatialTransformGizmoMotionTimer = new() { Interval = 16 };
    private bool _spatialTransformGizmoPresented;
    private float _spatialTransformGizmoMotionPosition;
    private float _spatialTransformGizmoMotionVelocity;
    private long _spatialTransformGizmoMotionLastTick;
    private int _spatialTransformGizmoMotionGeneration;

    internal bool SpatialTransformGizmoPresented => _spatialTransformGizmoPresented;
    internal bool SpatialTransformGizmoMotionActive => _spatialTransformGizmoMotionTimer.Enabled;
    internal float SpatialTransformGizmoMotionPosition => _spatialTransformGizmoMotionPosition;
    internal float SpatialTransformGizmoRenderOpacity => Math.Clamp(
        _spatialTransformGizmoMotionPosition,
        0f,
        1f);
    internal float SpatialTransformGizmoRenderScale => Math.Clamp(
        SpatialGizmoHiddenScale
            + (1f - SpatialGizmoHiddenScale) * _spatialTransformGizmoMotionPosition,
        SpatialGizmoHiddenScale,
        SpatialGizmoMaximumRenderScale);
    internal int SpatialTransformGizmoMotionGeneration => _spatialTransformGizmoMotionGeneration;

    private void InitializeSpatialTransformGizmoMotion()
    {
        _spatialTransformGizmoMotionTimer.Tick += (_, _) => TickSpatialTransformGizmoMotion();
    }

    private void DisposeSpatialTransformGizmoMotion()
    {
        ResetSpatialTransformGizmoMotionState();
        _spatialTransformGizmoMotionTimer.Dispose();
    }

    private void RetargetSpatialTransformGizmoMotion(SpatialGizmoMotion motion)
    {
        var target = _spatialTransformGizmoVisible ? 1f : 0f;
        if (_spatialTransformGizmoVisible) _spatialTransformGizmoPresented = true;
        if (motion != SpatialGizmoMotion.Animated || !UiMotion.AnimationsEnabled)
        {
            CompleteSpatialTransformGizmoMotion();
            return;
        }

        if (SpatialTransformGizmoMotionSettled(target))
        {
            CompleteSpatialTransformGizmoMotion();
            return;
        }

        if (!_spatialTransformGizmoMotionTimer.Enabled)
        {
            _spatialTransformGizmoMotionLastTick = Stopwatch.GetTimestamp();
            _spatialTransformGizmoMotionTimer.Start();
        }
        InvalidateOverlay();
    }

    private void TickSpatialTransformGizmoMotion()
    {
        if (!_spatialTransformGizmoMotionTimer.Enabled) return;
        if (!UiMotion.AnimationsEnabled)
        {
            CompleteSpatialTransformGizmoMotion();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _spatialTransformGizmoMotionLastTick == 0
            ? _spatialTransformGizmoMotionTimer.Interval
            : Stopwatch.GetElapsedTime(_spatialTransformGizmoMotionLastTick, now).TotalMilliseconds;
        _spatialTransformGizmoMotionLastTick = now;
        AdvanceSpatialTransformGizmoMotionCore(elapsedMilliseconds, invalidate: true);
    }

    internal bool AdvanceSpatialTransformGizmoMotion(double elapsedMilliseconds)
    {
        if (_spatialTransformGizmoMotionTimer.Enabled)
        {
            _spatialTransformGizmoMotionLastTick = Stopwatch.GetTimestamp();
        }
        return AdvanceSpatialTransformGizmoMotionCore(elapsedMilliseconds, invalidate: true);
    }

    private bool AdvanceSpatialTransformGizmoMotionCore(double elapsedMilliseconds, bool invalidate)
    {
        if (!_spatialTransformGizmoMotionTimer.Enabled) return false;
        if (!UiMotion.AnimationsEnabled)
        {
            CompleteSpatialTransformGizmoMotion(invalidate);
            return false;
        }

        var target = _spatialTransformGizmoVisible ? 1f : 0f;
        var settled = StepSpatialTransformGizmoSpring(
            ref _spatialTransformGizmoMotionPosition,
            ref _spatialTransformGizmoMotionVelocity,
            target,
            (float)Math.Max(0, elapsedMilliseconds / 1000d));
        if (settled) CompleteSpatialTransformGizmoMotion(invalidate: false);
        if (invalidate) InvalidateOverlay();
        return _spatialTransformGizmoMotionTimer.Enabled;
    }

    internal void CompleteSpatialTransformGizmoMotion(bool invalidate = true)
    {
        var target = _spatialTransformGizmoVisible ? 1f : 0f;
        var changed = _spatialTransformGizmoMotionTimer.Enabled
            || _spatialTransformGizmoMotionPosition != target
            || _spatialTransformGizmoMotionVelocity != 0
            || _spatialTransformGizmoPresented != _spatialTransformGizmoVisible;
        _spatialTransformGizmoMotionTimer.Stop();
        _spatialTransformGizmoMotionLastTick = 0;
        _spatialTransformGizmoMotionPosition = target;
        _spatialTransformGizmoMotionVelocity = 0;
        _spatialTransformGizmoPresented = _spatialTransformGizmoVisible;
        if (!_spatialTransformGizmoPresented) ClearSpatialTransformGizmoSnapshot();
        if (invalidate && changed) InvalidateOverlay();
    }

    private void ResetSpatialTransformGizmoMotionState()
    {
        _spatialTransformGizmoMotionTimer.Stop();
        _spatialTransformGizmoMotionLastTick = 0;
        _spatialTransformGizmoMotionPosition = 0;
        _spatialTransformGizmoMotionVelocity = 0;
        _spatialTransformGizmoPresented = false;
        _spatialTransformGizmoVisible = false;
        _spatialTransformHoveredHandle = SpatialTransformHandleHit.None;
        _spatialTransformActiveHandle = SpatialTransformHandleHit.None;
        ClearSpatialTransformGizmoSnapshot();
    }

    private void ClearSpatialTransformGizmoSnapshot()
    {
        _spatialTransformGizmoOrigin = default;
        _spatialTransformGizmoMode = SpatialTransformMode.Move;
        _spatialTransformGizmoBasis = SpatialGizmoBasis.Identity;
    }

    private void RecordSpatialTransformGizmoMotionTargetChange()
    {
        unchecked
        {
            _spatialTransformGizmoMotionGeneration++;
        }
    }

    private bool SpatialTransformGizmoMotionSettled(float target) =>
        Math.Abs(_spatialTransformGizmoMotionPosition - target) <= SpatialGizmoPositionEpsilon
        && Math.Abs(_spatialTransformGizmoMotionVelocity) <= SpatialGizmoVelocityEpsilon;

    internal static bool StepSpatialTransformGizmoSpring(
        ref float position,
        ref float velocity,
        float target,
        float deltaSeconds)
    {
        var dt = Math.Clamp(deltaSeconds, 0.008f, 0.034f);
        velocity += ((target - position) * SpatialGizmoSpringStiffness
            - velocity * SpatialGizmoSpringDamping) * dt;
        position += velocity * dt;
        if (!float.IsFinite(position) || !float.IsFinite(velocity))
        {
            position = target;
            velocity = 0;
            return true;
        }

        if (Math.Abs(position - target) > SpatialGizmoPositionEpsilon
            || Math.Abs(velocity) > SpatialGizmoVelocityEpsilon)
        {
            return false;
        }

        position = target;
        velocity = 0;
        return true;
    }
}
