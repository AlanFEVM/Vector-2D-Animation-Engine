using System.Diagnostics;

namespace VectorAnimationEngine;

internal enum ReferenceCameraMotion
{
    Immediate,
    Animated
}

internal readonly record struct ReferenceCameraFrame(
    float Yaw,
    float Pitch,
    float Distance,
    float ZoomScale,
    float TargetX,
    float TargetY,
    float TargetZ,
    float ProjectionBlend);

internal sealed partial class StageControl
{
    private const double ReferenceDirectionTransitionMilliseconds = 180;
    private const double ReferenceCameraTransitionMilliseconds = 220;
    private const float ReferenceProjectionScale = 0.035f;
    private readonly System.Windows.Forms.Timer _referenceCameraTransitionTimer = new() { Interval = 16 };
    private ReferenceCameraFrame _referenceCameraTransitionFrom;
    private ReferenceCameraFrame _referenceCameraTransitionTo;
    private ReferenceCameraFrame _referenceCameraTransitionStoredTarget;
    private double _referenceCameraTransitionElapsedMilliseconds;
    private double _referenceCameraTransitionDurationMilliseconds;
    private long _referenceCameraTransitionLastTick;
    private float _referenceCameraTransitionProgress;
    private float _referenceProjectionBlend;
    private bool _referenceCameraTransitionActive;
    private bool _referenceCameraTransitionSourceRendersReference;
    private bool _referenceCameraTransitionTargetRendersReference;

    internal bool ReferenceCameraTransitionActive => _referenceCameraTransitionActive;
    internal bool RendersReferenceProjection => UsesReferenceProjection || _referenceCameraTransitionActive;
    internal float ReferenceProjectionBlend => _referenceProjectionBlend;
    internal float PlanarWorldGridOpacity => _worldGridOpacity * (1 - ReferenceGridTransitionBlend);
    internal float ReferenceWorldGridOpacity => _worldGridOpacity * ReferenceGridTransitionBlend;

    private float ReferenceGridTransitionBlend
    {
        get
        {
            if (!_referenceCameraTransitionActive) return UsesReferenceProjection ? 1 : 0;
            var from = _referenceCameraTransitionSourceRendersReference ? 1f : 0f;
            var to = _referenceCameraTransitionTargetRendersReference ? 1f : 0f;
            return Lerp(from, to, SmoothTransitionProgress(_referenceCameraTransitionProgress));
        }
    }

    private void InitializeReferenceCameraTransitions()
    {
        _referenceCameraTransitionTimer.Tick += (_, _) => TickReferenceCameraTransition();
        SynchronizeReferenceProjectionBlend();
    }

    private void DisposeReferenceCameraTransitions()
    {
        _referenceCameraTransitionTimer.Stop();
        _referenceCameraTransitionTimer.Dispose();
        _referenceCameraTransitionActive = false;
    }

    private void ConfigureReferenceViewCore(
        SceneDefinition? scene,
        SceneDimension nextDimension,
        ReferenceCameraMotion motion)
    {
        var seed = PrepareReferenceCameraTransition();
        var nextProjection = scene?.Camera.Projection ?? CameraProjection.Orthographic;
        var nextDirection = ReferenceDimension != nextDimension && nextDimension == SceneDimension.TwoD
            ? ReferenceViewDirection.Front
            : _reference2DViewDirection;

        ReferenceDimension = nextDimension;
        ReferenceProjection = nextProjection;
        _reference2DViewDirection = nextDirection;

        var storedTarget = seed.LogicalTarget with
        {
            ProjectionBlend = ProjectionBlendFor(nextDimension, nextProjection)
        };
        var renderedTarget = RenderedTargetForCurrentContext(storedTarget);
        StartReferenceCameraTransition(
            seed,
            renderedTarget,
            storedTarget,
            motion,
            ReferenceCameraTransitionMilliseconds);
    }

    private void SetReferenceViewDirectionCore(
        ReferenceViewDirection direction,
        ReferenceCameraMotion motion)
    {
        var seed = PrepareReferenceCameraTransition();
        var (yaw, pitch) = ReferenceOrientation(direction, seed.LogicalTarget.Yaw, seed.LogicalTarget.Pitch);
        if (ReferenceDimension == SceneDimension.TwoD)
        {
            _reference2DViewDirection = direction;
        }
        var storedTarget = seed.LogicalTarget with
        {
            Yaw = NormalizeRadians(yaw),
            Pitch = Math.Clamp(pitch, -ReferenceMaximumPitch, ReferenceMaximumPitch),
            ProjectionBlend = ProjectionBlendFor(ReferenceDimension, ReferenceProjection)
        };
        var renderedTarget = RenderedTargetForCurrentContext(storedTarget);
        StartReferenceCameraTransition(
            seed,
            renderedTarget,
            storedTarget,
            motion,
            ReferenceDirectionTransitionMilliseconds);
    }

    private void SetReferenceCameraOrientationCore(
        float yaw,
        float pitch,
        ReferenceCameraMotion motion)
    {
        var seed = PrepareReferenceCameraTransition();
        var storedTarget = seed.LogicalTarget with
        {
            Yaw = NormalizeRadians(yaw),
            Pitch = Math.Clamp(pitch, -ReferenceMaximumPitch, ReferenceMaximumPitch),
            ProjectionBlend = ProjectionBlendFor(ReferenceDimension, ReferenceProjection)
        };
        var renderedTarget = RenderedTargetForCurrentContext(storedTarget);
        StartReferenceCameraTransition(
            seed,
            renderedTarget,
            storedTarget,
            motion,
            ReferenceDirectionTransitionMilliseconds);
    }

    private void ResetReferenceCameraViewCore(ReferenceCameraMotion motion)
    {
        var seed = PrepareReferenceCameraTransition();
        var storedTarget = new ReferenceCameraFrame(
            -0.72f,
            0.76f,
            12_000,
            1,
            0,
            0,
            0,
            ProjectionBlendFor(ReferenceDimension, ReferenceProjection));
        var renderedTarget = RenderedTargetForCurrentContext(storedTarget);
        StartReferenceCameraTransition(
            seed,
            renderedTarget,
            storedTarget,
            motion,
            ReferenceCameraTransitionMilliseconds);
    }

    private ReferenceCameraTransitionSeed PrepareReferenceCameraTransition()
    {
        AdvanceReferenceCameraTransitionToNow(invalidate: false);
        var sourceRendersReference = RendersReferenceProjection;
        var presented = CapturePresentedReferenceCameraFrame(sourceRendersReference);
        var logicalTarget = _referenceCameraTransitionActive
            ? _referenceCameraTransitionStoredTarget
            : CaptureCurrentReferenceCameraFrame();
        StopReferenceCameraTransition();
        return new ReferenceCameraTransitionSeed(presented, logicalTarget, sourceRendersReference);
    }

    private void StartReferenceCameraTransition(
        ReferenceCameraTransitionSeed seed,
        ReferenceCameraFrame renderedTarget,
        ReferenceCameraFrame storedTarget,
        ReferenceCameraMotion motion,
        double durationMilliseconds)
    {
        var targetRendersReference = UsesReferenceProjection;
        var shouldAnimate = motion == ReferenceCameraMotion.Animated
            && UiMotion.AnimationsEnabled
            && (!ReferenceCameraFramesNearlyEqual(seed.Presented, renderedTarget)
                || seed.SourceRendersReference != targetRendersReference);
        if (!shouldAnimate)
        {
            ApplyReferenceCameraFrame(storedTarget);
            Invalidate();
            return;
        }

        _referenceCameraTransitionFrom = seed.Presented;
        _referenceCameraTransitionTo = renderedTarget;
        _referenceCameraTransitionStoredTarget = storedTarget;
        _referenceCameraTransitionElapsedMilliseconds = 0;
        _referenceCameraTransitionDurationMilliseconds = Math.Max(1, durationMilliseconds);
        _referenceCameraTransitionLastTick = Stopwatch.GetTimestamp();
        _referenceCameraTransitionProgress = 0;
        _referenceCameraTransitionSourceRendersReference = seed.SourceRendersReference;
        _referenceCameraTransitionTargetRendersReference = targetRendersReference;
        _referenceCameraTransitionActive = true;
        ApplyReferenceCameraFrame(seed.Presented);
        _referenceCameraTransitionTimer.Start();
        Invalidate();
    }

    private ReferenceCameraFrame RenderedTargetForCurrentContext(ReferenceCameraFrame logicalTarget)
    {
        if (ReferenceDimension != SceneDimension.TwoD
            || _reference2DViewDirection != ReferenceViewDirection.Front)
        {
            return logicalTarget;
        }

        if (UsesReferenceProjection)
        {
            return logicalTarget with
            {
                Yaw = 0,
                Pitch = 0,
                ProjectionBlend = 0
            };
        }

        return Ordinary2DReferenceCameraFrame(logicalTarget);
    }

    private ReferenceCameraFrame CapturePresentedReferenceCameraFrame(bool rendersReference)
    {
        var current = CaptureCurrentReferenceCameraFrame();
        if (!rendersReference) return Ordinary2DReferenceCameraFrame(current);
        if (_referenceCameraTransitionActive) return current;
        return UsesSpatialFrontView
            ? current with { Yaw = 0, Pitch = 0, ProjectionBlend = 0 }
            : current;
    }

    private ReferenceCameraFrame Ordinary2DReferenceCameraFrame(ReferenceCameraFrame template)
    {
        var screenScale = Math.Max(0.000001f, VectorUnits.PixelsPerUnit * Zoom);
        return template with
        {
            Yaw = 0,
            Pitch = 0,
            Distance = ReferencePerspectiveFocalLength,
            ZoomScale = screenScale / ReferenceProjectionScale,
            TargetX = CameraX,
            TargetY = -CameraY - Height * 0.08f / screenScale,
            TargetZ = 0,
            ProjectionBlend = 0
        };
    }

    private ReferenceCameraFrame CaptureCurrentReferenceCameraFrame()
    {
        return new ReferenceCameraFrame(
            _referenceYaw,
            _referencePitch,
            _referenceDistance,
            _referenceZoomScale,
            _referenceTargetX,
            _referenceTargetY,
            _referenceTargetZ,
            _referenceProjectionBlend);
    }

    internal ReferenceCameraFrame CaptureReferenceCameraFrameForPersistence()
    {
        return _referenceCameraTransitionActive
            ? _referenceCameraTransitionStoredTarget
            : CaptureCurrentReferenceCameraFrame();
    }

    private void ApplyReferenceCameraFrame(ReferenceCameraFrame frame)
    {
        _referenceYaw = NormalizeRadians(frame.Yaw);
        _referencePitch = Math.Clamp(frame.Pitch, -ReferenceMaximumPitch, ReferenceMaximumPitch);
        _referenceDistance = Math.Max(0.0001f, frame.Distance);
        _referenceZoomScale = Math.Max(0.0001f, frame.ZoomScale);
        _referenceTargetX = frame.TargetX;
        _referenceTargetY = frame.TargetY;
        _referenceTargetZ = frame.TargetZ;
        _referenceProjectionBlend = Math.Clamp(frame.ProjectionBlend, 0, 1);
    }

    private void TickReferenceCameraTransition()
    {
        if (!_referenceCameraTransitionActive)
        {
            _referenceCameraTransitionTimer.Stop();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = _referenceCameraTransitionLastTick == 0
            ? _referenceCameraTransitionTimer.Interval
            : Stopwatch.GetElapsedTime(_referenceCameraTransitionLastTick, now).TotalMilliseconds;
        _referenceCameraTransitionLastTick = now;
        AdvanceReferenceCameraTransitionCore(elapsed, invalidate: true);
    }

    private void AdvanceReferenceCameraTransitionToNow(bool invalidate)
    {
        if (!_referenceCameraTransitionActive) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = _referenceCameraTransitionLastTick == 0
            ? 0
            : Stopwatch.GetElapsedTime(_referenceCameraTransitionLastTick, now).TotalMilliseconds;
        _referenceCameraTransitionLastTick = now;
        if (elapsed > 0) AdvanceReferenceCameraTransitionCore(elapsed, invalidate);
    }

    internal bool AdvanceReferenceCameraTransition(double elapsedMilliseconds)
    {
        if (_referenceCameraTransitionActive)
        {
            _referenceCameraTransitionLastTick = Stopwatch.GetTimestamp();
        }
        return AdvanceReferenceCameraTransitionCore(elapsedMilliseconds, invalidate: true);
    }

    private bool AdvanceReferenceCameraTransitionCore(double elapsedMilliseconds, bool invalidate)
    {
        if (!_referenceCameraTransitionActive) return false;
        _referenceCameraTransitionElapsedMilliseconds += Math.Max(0, elapsedMilliseconds);
        var progress = (float)Math.Clamp(
            _referenceCameraTransitionElapsedMilliseconds / _referenceCameraTransitionDurationMilliseconds,
            0,
            1);
        _referenceCameraTransitionProgress = progress;
        ApplyReferenceCameraFrame(ResolveReferenceCameraTransitionFrame(
            _referenceCameraTransitionFrom,
            _referenceCameraTransitionTo,
            progress));
        if (progress >= 1) CompleteReferenceCameraTransitionCore();
        if (invalidate) Invalidate();
        return _referenceCameraTransitionActive;
    }

    internal void CompleteReferenceCameraTransition(bool invalidate = true)
    {
        if (!_referenceCameraTransitionActive) return;
        CompleteReferenceCameraTransitionCore();
        if (invalidate) Invalidate();
    }

    private void CompleteReferenceCameraTransitionCore()
    {
        _referenceCameraTransitionTimer.Stop();
        _referenceCameraTransitionActive = false;
        _referenceCameraTransitionLastTick = 0;
        _referenceCameraTransitionProgress = 1;
        ApplyReferenceCameraFrame(_referenceCameraTransitionStoredTarget);
    }

    private void StopReferenceCameraTransition()
    {
        _referenceCameraTransitionTimer.Stop();
        _referenceCameraTransitionActive = false;
        _referenceCameraTransitionLastTick = 0;
        _referenceCameraTransitionProgress = 0;
    }

    private void CompleteReferenceCameraTransitionForDirectInput()
    {
        CompleteReferenceCameraTransition(invalidate: false);
    }

    private void CancelReferenceCameraTransitionForStateReplacement()
    {
        StopReferenceCameraTransition();
    }

    private void SynchronizeReferenceProjectionBlend()
    {
        _referenceProjectionBlend = ProjectionBlendFor(ReferenceDimension, ReferenceProjection);
    }

    private static float ProjectionBlendFor(SceneDimension dimension, CameraProjection projection)
    {
        return dimension == SceneDimension.ThreeD && projection == CameraProjection.Perspective ? 1 : 0;
    }

    private static (float Yaw, float Pitch) ReferenceOrientation(
        ReferenceViewDirection direction,
        float fallbackYaw,
        float fallbackPitch)
    {
        return direction switch
        {
            ReferenceViewDirection.Front => (0, 0),
            ReferenceViewDirection.Back => (MathF.PI, 0),
            ReferenceViewDirection.Left => (-MathF.PI / 2, 0),
            ReferenceViewDirection.Right => (MathF.PI / 2, 0),
            ReferenceViewDirection.Top => (0, ReferenceMaximumPitch),
            ReferenceViewDirection.Bottom => (0, -ReferenceMaximumPitch),
            ReferenceViewDirection.Isometric => (-0.72f, 0.76f),
            _ => (fallbackYaw, fallbackPitch)
        };
    }

    internal static ReferenceCameraFrame ResolveReferenceCameraTransitionFrame(
        ReferenceCameraFrame from,
        ReferenceCameraFrame to,
        float progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var eased = SmoothTransitionProgress(progress);
        var yawDelta = NormalizeRadians(to.Yaw - from.Yaw);
        return new ReferenceCameraFrame(
            NormalizeRadians(from.Yaw + yawDelta * eased),
            Lerp(from.Pitch, to.Pitch, eased),
            LerpPositive(from.Distance, to.Distance, eased),
            LerpPositive(from.ZoomScale, to.ZoomScale, eased),
            Lerp(from.TargetX, to.TargetX, eased),
            Lerp(from.TargetY, to.TargetY, eased),
            Lerp(from.TargetZ, to.TargetZ, eased),
            Lerp(from.ProjectionBlend, to.ProjectionBlend, eased));
    }

    private static bool ReferenceCameraFramesNearlyEqual(ReferenceCameraFrame a, ReferenceCameraFrame b)
    {
        return Math.Abs(NormalizeRadians(a.Yaw - b.Yaw)) < 0.00001f
            && Math.Abs(a.Pitch - b.Pitch) < 0.00001f
            && Math.Abs(a.Distance - b.Distance) < 0.01f
            && Math.Abs(a.ZoomScale - b.ZoomScale) < 0.00001f
            && Math.Abs(a.TargetX - b.TargetX) < 0.01f
            && Math.Abs(a.TargetY - b.TargetY) < 0.01f
            && Math.Abs(a.TargetZ - b.TargetZ) < 0.01f
            && Math.Abs(a.ProjectionBlend - b.ProjectionBlend) < 0.00001f;
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static float SmoothTransitionProgress(float progress) => progress * progress * (3 - 2 * progress);

    private static float LerpPositive(float from, float to, float amount)
    {
        from = Math.Max(0.0001f, from);
        to = Math.Max(0.0001f, to);
        return MathF.Exp(Lerp(MathF.Log(from), MathF.Log(to), amount));
    }

    private readonly record struct ReferenceCameraTransitionSeed(
        ReferenceCameraFrame Presented,
        ReferenceCameraFrame LogicalTarget,
        bool SourceRendersReference);
}
