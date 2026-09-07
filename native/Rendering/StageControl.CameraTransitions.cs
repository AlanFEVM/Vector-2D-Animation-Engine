using System.Diagnostics;
using System.Numerics;

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
    private const float ReferenceFocusMinimumDistance = 2_000f;
    private const float ReferenceFocusMaximumDistance = 80_000f;
    private const float ReferenceFocusNearMargin = 1f;
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

    internal bool FocusReferenceCamera(
        IReadOnlyList<Vector3>? scenePoints,
        ReferenceCameraMotion motion = ReferenceCameraMotion.Animated)
    {
        if (ReferenceDimension != SceneDimension.ThreeD && !UsesReferenceProjection) return false;
        var logicalTarget = _referenceCameraTransitionActive
            ? _referenceCameraTransitionStoredTarget
            : CaptureCurrentReferenceCameraFrame();
        if (ReferenceDimension == SceneDimension.TwoD
            && Reference2DViewDirection == ReferenceViewDirection.Front)
        {
            logicalTarget = logicalTarget with { Yaw = 0, Pitch = 0 };
        }
        if (!TryResolveReferenceCameraFocusFrame(
                scenePoints,
                ClientSize,
                EffectiveReferenceProjection,
                logicalTarget,
                out var storedTarget))
        {
            return false;
        }

        var seed = PrepareReferenceCameraTransition();
        var renderedTarget = RenderedTargetForCurrentContext(storedTarget);
        StartReferenceCameraTransition(
            seed,
            renderedTarget,
            storedTarget,
            motion,
            ReferenceCameraTransitionMilliseconds);
        return true;
    }

    internal static bool TryResolveReferenceCameraFocusFrame(
        IReadOnlyList<Vector3>? scenePoints,
        Size viewport,
        CameraProjection projection,
        ReferenceCameraFrame logicalTarget,
        out ReferenceCameraFrame frame)
    {
        frame = default;
        if (scenePoints is null
            || scenePoints.Count == 0
            || viewport.Width <= 0
            || viewport.Height <= 0
            || !Enum.IsDefined(projection)
            || !float.IsFinite(logicalTarget.Yaw)
            || !float.IsFinite(logicalTarget.Pitch))
        {
            return false;
        }

        var points = scenePoints.Where(Finite).ToArray();
        if (points.Length == 0) return false;

        var padding = Math.Max(16d, Math.Min(viewport.Width, viewport.Height) * 0.05d);
        var horizontalExtent = viewport.Width * 0.5d - padding;
        var upwardExtent = viewport.Height * 0.58d - padding;
        var downwardExtent = viewport.Height * 0.42d - padding;
        if (horizontalExtent <= 0 || upwardExtent <= 0 || downwardExtent <= 0) return false;

        var minimum = points[0];
        var maximum = points[0];
        for (var index = 1; index < points.Length; index++)
        {
            minimum = Vector3.Min(minimum, points[index]);
            maximum = Vector3.Max(maximum, points[index]);
        }

        var center = new Vector3(
            (float)(((double)minimum.X + maximum.X) * 0.5d),
            (float)(((double)minimum.Y + maximum.Y) * 0.5d),
            (float)(((double)minimum.Z + maximum.Z) * 0.5d));
        if (!Finite(center)) return false;

        var yaw = NormalizeRadians(logicalTarget.Yaw);
        var pitch = Math.Clamp(logicalTarget.Pitch, -ReferenceMaximumPitch, ReferenceMaximumPitch);
        var yawCos = Math.Cos(yaw);
        var yawSin = Math.Sin(yaw);
        var pitchCos = Math.Cos(pitch);
        var pitchSin = Math.Sin(pitch);
        var cameraPoints = new Vector3[points.Length];
        var minimumDepthOffset = double.PositiveInfinity;
        for (var index = 0; index < points.Length; index++)
        {
            var worldX = (double)points[index].X - center.X;
            var worldY = -(double)points[index].Y + center.Y;
            var worldZ = (double)points[index].Z - center.Z;
            var cameraX = worldX * yawCos - worldZ * yawSin;
            var yawDepth = worldX * yawSin + worldZ * yawCos;
            var cameraY = worldY * pitchCos - yawDepth * pitchSin;
            var cameraZ = worldY * pitchSin + yawDepth * pitchCos;
            if (!double.IsFinite(cameraX)
                || !double.IsFinite(cameraY)
                || !double.IsFinite(cameraZ)
                || Math.Abs(cameraX) > float.MaxValue
                || Math.Abs(cameraY) > float.MaxValue
                || Math.Abs(cameraZ) > float.MaxValue)
            {
                return false;
            }
            cameraPoints[index] = new Vector3((float)cameraX, (float)cameraY, (float)cameraZ);
            minimumDepthOffset = Math.Min(minimumDepthOffset, cameraZ);
        }

        var minimumNearDistance = ReferenceNearPlane + ReferenceFocusNearMargin - minimumDepthOffset;
        if (!double.IsFinite(minimumNearDistance)
            || minimumNearDistance > ReferenceFocusMaximumDistance)
        {
            return false;
        }

        double distance;
        double zoomScale;
        if (projection == CameraProjection.Orthographic)
        {
            var fitZoom = 1d;
            foreach (var point in cameraPoints)
            {
                fitZoom = Math.Min(fitZoom, FocusZoomLimit(horizontalExtent, Math.Abs(point.X), 1));
                fitZoom = Math.Min(fitZoom, FocusZoomLimit(
                    point.Y >= 0 ? upwardExtent : downwardExtent,
                    Math.Abs(point.Y),
                    1));
            }
            zoomScale = fitZoom;
            var currentDistance = float.IsFinite(logicalTarget.Distance)
                ? logicalTarget.Distance
                : 12_000f;
            distance = Math.Max(
                Math.Clamp(currentDistance, ReferenceFocusMinimumDistance, ReferenceFocusMaximumDistance),
                minimumNearDistance);
        }
        else
        {
            var requiredDistance = Math.Max(ReferenceFocusMinimumDistance, minimumNearDistance);
            foreach (var point in cameraPoints)
            {
                requiredDistance = Math.Max(requiredDistance, FocusPerspectiveDistance(
                    horizontalExtent,
                    Math.Abs(point.X),
                    point.Z));
                requiredDistance = Math.Max(requiredDistance, FocusPerspectiveDistance(
                    point.Y >= 0 ? upwardExtent : downwardExtent,
                    Math.Abs(point.Y),
                    point.Z));
            }

            if (!double.IsFinite(requiredDistance)) return false;
            distance = Math.Min(requiredDistance, ReferenceFocusMaximumDistance);
            zoomScale = 1d;
            if (requiredDistance > ReferenceFocusMaximumDistance)
            {
                foreach (var point in cameraPoints)
                {
                    var depth = distance + point.Z;
                    if (depth < ReferenceNearPlane) return false;
                    zoomScale = Math.Min(zoomScale, FocusZoomLimit(
                        horizontalExtent,
                        Math.Abs(point.X),
                        depth / ReferencePerspectiveFocalLength));
                    zoomScale = Math.Min(zoomScale, FocusZoomLimit(
                        point.Y >= 0 ? upwardExtent : downwardExtent,
                        Math.Abs(point.Y),
                        depth / ReferencePerspectiveFocalLength));
                }
            }
        }

        if (!double.IsFinite(distance)
            || !double.IsFinite(zoomScale)
            || distance <= 0
            || zoomScale < 0.0001d
            || distance > float.MaxValue
            || zoomScale > float.MaxValue)
        {
            return false;
        }

        frame = logicalTarget with
        {
            Yaw = yaw,
            Pitch = pitch,
            Distance = (float)distance,
            ZoomScale = (float)Math.Min(1d, zoomScale),
            TargetX = center.X,
            TargetY = -center.Y,
            TargetZ = center.Z,
            ProjectionBlend = projection == CameraProjection.Perspective ? 1f : 0f
        };
        return true;
    }

    private static double FocusPerspectiveDistance(double availablePixels, double extent, double depthOffset)
    {
        return extent <= 0
            ? double.NegativeInfinity
            : extent * ReferenceProjectionScale * ReferencePerspectiveFocalLength / availablePixels - depthOffset;
    }

    private static double FocusZoomLimit(double availablePixels, double extent, double depthFactor)
    {
        if (extent <= 0) return double.PositiveInfinity;
        return availablePixels * depthFactor / (ReferenceProjectionScale * extent);
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
