using System.Numerics;

namespace VectorAnimationEngine;

internal static class DrawingObjectSnapPointMath
{
    internal static Matrix4x4 CreateInstanceTransform(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state)
    {
        ArgumentNullException.ThrowIfNull(drawingObject);
        return Matrix4x4.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y, 0)
            * DrawingObjectInstanceDefinition.CreateSpatialTransform(state);
    }

    internal static Vector3 Transform(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state,
        DrawingObjectSnapPointDefinition snapPoint)
    {
        return Vector3.Transform(
            new Vector3(snapPoint.X, snapPoint.Y, snapPoint.Z),
            CreateInstanceTransform(drawingObject, state));
    }
}
