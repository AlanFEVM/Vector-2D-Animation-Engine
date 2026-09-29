namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal const int ShotCameraAxisGlyphPointCapacity = 6;

    internal static float GetShotCameraAxisGlyphStrokeWidth(float size) => Math.Max(1.4f, size * 0.23f);

    // Each pair describes one stroke. Both renderers use the same endpoints instead of relying on
    // small text metrics; the glyph stays inside the existing badge/ring and its hit radius.
    internal static int GetShotCameraAxisGlyphPoints(
        ShotFramingHandleKind kind,
        PointF center,
        float radius,
        Span<PointF> points)
    {
        var halfWidth = radius * 0.38f;
        var halfHeight = radius * 0.5f;
        var topLeft = new PointF(center.X - halfWidth, center.Y - halfHeight);
        var topRight = new PointF(center.X + halfWidth, center.Y - halfHeight);
        var bottomLeft = new PointF(center.X - halfWidth, center.Y + halfHeight);
        var bottomRight = new PointF(center.X + halfWidth, center.Y + halfHeight);
        switch (kind)
        {
            case ShotFramingHandleKind.CameraPositionX:
            case ShotFramingHandleKind.CameraRotateX:
                points[0] = topLeft;
                points[1] = bottomRight;
                points[2] = topRight;
                points[3] = bottomLeft;
                return 4;
            case ShotFramingHandleKind.CameraPositionY:
            case ShotFramingHandleKind.CameraRotateY:
                points[0] = topLeft;
                points[1] = center;
                points[2] = topRight;
                points[3] = center;
                points[4] = center;
                points[5] = new PointF(center.X, center.Y + halfHeight);
                return 6;
            case ShotFramingHandleKind.CameraPositionZ:
            case ShotFramingHandleKind.CameraRotateZ:
                points[0] = topLeft;
                points[1] = topRight;
                points[2] = topRight;
                points[3] = bottomLeft;
                points[4] = bottomLeft;
                points[5] = bottomRight;
                return 6;
            default:
                return 0;
        }
    }
}
