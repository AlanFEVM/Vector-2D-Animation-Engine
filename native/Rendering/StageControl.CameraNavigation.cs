using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal const float ReferenceWheelDollyBase = 0.9f;
    private const float ReferencePixelDollyExponent = 0.012f;

    private float ReferenceWorldUnitsPerPixel()
    {
        var screenScale = ReferenceProjectionScale * _referenceZoomScale;
        var projectionScale = ReferencePerspectiveScale(_referenceDistance);
        return 1f / Math.Max(0.000001f, screenScale * projectionScale);
    }

    internal void TranslateReferenceCameraLocalByPixels(float rightPixels, float forwardPixels)
    {
        if (ReferenceDimension != SceneDimension.ThreeD
            || !float.IsFinite(rightPixels)
            || !float.IsFinite(forwardPixels)
            || rightPixels == 0 && forwardPixels == 0)
        {
            return;
        }

        CompleteReferenceCameraTransitionForDirectInput();
        var right = Vector3.Normalize(CameraToReference(Vector3.UnitX, direction: true));
        var forward = Vector3.Normalize(CameraToReference(Vector3.UnitZ, direction: true));
        var worldPerPixel = ReferenceWorldUnitsPerPixel();
        var translation = (right * rightPixels + forward * forwardPixels) * worldPerPixel;

        _referenceTargetX = Math.Clamp(_referenceTargetX + translation.X, -5_000_000f, 5_000_000f);
        _referenceTargetY = Math.Clamp(_referenceTargetY + translation.Y, -5_000_000f, 5_000_000f);
        _referenceTargetZ = Math.Clamp(_referenceTargetZ + translation.Z, -5_000_000f, 5_000_000f);
        Invalidate();
    }

    internal void LookAroundReferenceCamera(float dx, float dy)
    {
        if (ReferenceDimension != SceneDimension.ThreeD
            || !float.IsFinite(dx)
            || !float.IsFinite(dy)
            || dx == 0 && dy == 0)
        {
            return;
        }

        CompleteReferenceCameraTransitionForDirectInput();
        var eye = CameraToReference(Vector3.Zero, direction: false);
        var nextYaw = NormalizeRadians(_referenceYaw + dx * 0.01f % MathF.Tau);
        var nextPitch = Math.Clamp(
            _referencePitch - dy * 0.01f,
            -ReferenceMaximumPitch,
            ReferenceMaximumPitch);
        var pitchCos = MathF.Cos(nextPitch);
        var forward = Vector3.Normalize(new Vector3(
            pitchCos * MathF.Sin(nextYaw),
            MathF.Sin(nextPitch),
            pitchCos * MathF.Cos(nextYaw)));
        var target = eye + forward * _referenceDistance;
        const float referenceTargetLimit = 5_000_000f;
        if (!Finite(eye)
            || !Finite(target)
            || MathF.Abs(target.X) > referenceTargetLimit
            || MathF.Abs(target.Y) > referenceTargetLimit
            || MathF.Abs(target.Z) > referenceTargetLimit)
        {
            return;
        }

        _referenceYaw = nextYaw;
        _referencePitch = nextPitch;
        _referenceTargetX = target.X;
        _referenceTargetY = target.Y;
        _referenceTargetZ = target.Z;
        Invalidate();
    }
}
