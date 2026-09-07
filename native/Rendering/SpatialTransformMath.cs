using System.Numerics;

namespace VectorAnimationEngine;

internal enum SpatialTransformSpace
{
    Global,
    Local
}

internal readonly record struct SpatialGizmoBasis(Vector3 X, Vector3 Y, Vector3 Z)
{
    private const float UnitTolerance = 0.002f;

    public static SpatialGizmoBasis Identity { get; } = new(
        Vector3.UnitX,
        Vector3.UnitY,
        Vector3.UnitZ);

    public bool IsValid => IsUnit(X)
        && IsUnit(Y)
        && IsUnit(Z)
        && Math.Abs(Vector3.Dot(X, Y)) <= UnitTolerance
        && Math.Abs(Vector3.Dot(X, Z)) <= UnitTolerance
        && Math.Abs(Vector3.Dot(Y, Z)) <= UnitTolerance
        && Vector3.Dot(Vector3.Cross(X, Y), Z) >= 1f - UnitTolerance;

    public Vector3 Axis(SpatialTransformAxis axis) => axis switch
    {
        SpatialTransformAxis.X => X,
        SpatialTransformAxis.Y => Y,
        SpatialTransformAxis.Z => Z,
        _ => Vector3.Zero
    };

    public (Vector3 First, Vector3 Second) PlaneAxes(SpatialTransformAxis axis) => axis switch
    {
        SpatialTransformAxis.XY => (X, Y),
        SpatialTransformAxis.XZ => (X, Z),
        SpatialTransformAxis.YZ => (Y, Z),
        _ => (Vector3.Zero, Vector3.Zero)
    };

    public Vector3 PlaneNormal(SpatialTransformAxis axis) => axis switch
    {
        SpatialTransformAxis.XY => Z,
        SpatialTransformAxis.XZ => Y,
        SpatialTransformAxis.YZ => X,
        _ => Vector3.Zero
    };

    public Vector3 RingOffset(SpatialTransformAxis axis, float angle)
    {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        return axis switch
        {
            SpatialTransformAxis.X => Y * cosine + Z * sine,
            SpatialTransformAxis.Y => Z * cosine + X * sine,
            SpatialTransformAxis.Z => X * cosine + Y * sine,
            _ => Vector3.Zero
        };
    }

    private static bool IsUnit(Vector3 value)
    {
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && Math.Abs(value.LengthSquared() - 1f) <= UnitTolerance;
    }
}

internal static class SpatialTransformMath
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float RadiansToDegrees = 180f / MathF.PI;
    private const float SingularCosineThreshold = 0.0001f;

    internal static Matrix4x4 CreateRotation(InstanceFrameState state)
    {
        return CreateRotation(state.RotationX, state.RotationY, state.RotationZ);
    }

    internal static Matrix4x4 CreateRotation(float rotationX, float rotationY, float rotationZ)
    {
        return Matrix4x4.CreateRotationZ(rotationZ * DegreesToRadians)
            * Matrix4x4.CreateRotationX(rotationX * DegreesToRadians)
            * Matrix4x4.CreateRotationY(rotationY * DegreesToRadians);
    }

    internal static SpatialGizmoBasis CreateBasis(InstanceFrameState state)
    {
        var rotation = CreateRotation(state);
        var x = NormalizeOrFallback(Vector3.TransformNormal(Vector3.UnitX, rotation), Vector3.UnitX);
        var rawY = Vector3.TransformNormal(Vector3.UnitY, rotation);
        var y = NormalizeOrFallback(rawY - x * Vector3.Dot(rawY, x), Vector3.UnitY);
        var z = NormalizeOrFallback(Vector3.Cross(x, y), Vector3.UnitZ);
        var basis = new SpatialGizmoBasis(x, y, z);
        return basis.IsValid ? basis : SpatialGizmoBasis.Identity;
    }

    internal static bool TryDecomposeRotation(
        Matrix4x4 rotation,
        Vector3 referenceDegrees,
        out Vector3 resultDegrees)
    {
        resultDegrees = default;
        if (!Finite(referenceDegrees)) return false;

        var quaternion = Quaternion.CreateFromRotationMatrix(rotation);
        var lengthSquared = quaternion.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0.00000001f) return false;
        rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(quaternion));

        var reference = referenceDegrees * DegreesToRadians;
        var sineX = Math.Clamp(-rotation.M32, -1f, 1f);
        var x = MathF.Asin(sineX);
        var cosineX = MathF.Cos(x);
        Vector3 result;
        if (Math.Abs(cosineX) > SingularCosineThreshold)
        {
            var principal = new Vector3(
                x,
                MathF.Atan2(rotation.M31, rotation.M33),
                MathF.Atan2(rotation.M12, rotation.M22));
            var alternate = new Vector3(
                x >= 0 ? MathF.PI - x : -MathF.PI - x,
                principal.Y + MathF.PI,
                principal.Z + MathF.PI);
            principal = UnwrapNear(principal, reference);
            alternate = UnwrapNear(alternate, reference);
            result = AngularDistanceSquared(principal, reference)
                <= AngularDistanceSquared(alternate, reference)
                    ? principal
                    : alternate;
        }
        else if (sineX >= 0)
        {
            var difference = UnwrapNear(
                MathF.Atan2(rotation.M13, rotation.M11),
                reference.Z - reference.Y);
            var z = (reference.Z + reference.Y + difference) * 0.5f;
            var y = (reference.Z + reference.Y - difference) * 0.5f;
            result = new Vector3(UnwrapNear(MathF.PI * 0.5f, reference.X), y, z);
        }
        else
        {
            var sum = UnwrapNear(
                MathF.Atan2(-rotation.M13, rotation.M11),
                reference.Z + reference.Y);
            var z = (reference.Z - reference.Y + sum) * 0.5f;
            var y = (-reference.Z + reference.Y + sum) * 0.5f;
            result = new Vector3(UnwrapNear(-MathF.PI * 0.5f, reference.X), y, z);
        }

        resultDegrees = result * RadiansToDegrees;
        return Finite(resultDegrees);
    }

    private static Vector3 UnwrapNear(Vector3 value, Vector3 reference)
    {
        return new Vector3(
            UnwrapNear(value.X, reference.X),
            UnwrapNear(value.Y, reference.Y),
            UnwrapNear(value.Z, reference.Z));
    }

    private static float UnwrapNear(float value, float reference)
    {
        return value + MathF.Round((reference - value) / MathF.Tau) * MathF.Tau;
    }

    private static float AngularDistanceSquared(Vector3 value, Vector3 reference)
    {
        var delta = value - reference;
        return delta.LengthSquared();
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 0.00000001f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static bool Finite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
