using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private bool TryDrawGpuProjectiveBitmap(StageControl stage, ID2D1Bitmap bitmap,
        Reference3DProjectiveTriangle[] triangles, ID2D1Geometry screenMask,
        int width, int height, float opacity)
    {
        if (_target is not ID2D1DeviceContext context
            || stage.ReferenceProjectionBlend < 0.999999f
            || !TryGetGpuProjectiveBitmapTransform(triangles, width, height, out var transform)) return false;
        var previous = context.Transform;
        try
        {
            context.Transform = Matrix3x2.Identity;
            var rectangle = new Vortice.RawRectF(0, 0, width, height);
            if (stage.Reference3DPlaybackActive)
            {
                // The validated projective mesh covers the complete source
                // domain. During playback the transparent SVG texture can be
                // submitted directly, avoiding a per-frame D2D mask layer.
                context.DrawBitmap(bitmap, rectangle, opacity, InterpolationMode.Linear, rectangle, transform);
            }
            else
            {
                using var layer = context.CreateLayer();
                context.PushLayer(new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f
                }, layer);
                try
                {
                    context.DrawBitmap(bitmap, rectangle, opacity, InterpolationMode.Linear, rectangle, transform);
                }
                finally { context.PopLayer(); }
            }
        }
        finally { context.Transform = previous; }
        return true;
    }

    // Screen*cameraDepth and cameraDepth are affine over a planar UV domain.
    // Recover their homogeneous map once instead of submitting a D2D layer
    // for every adaptive-mesh triangle. Validate all vertices before using it.
    internal static bool TryGetGpuProjectiveBitmapTransform(
        IReadOnlyList<Reference3DProjectiveTriangle> triangles, int width, int height,
        out Matrix4x4 transform)
    {
        transform = default;
        if (triangles.Count == 0 || width <= 0 || height <= 0) return false;
        var best = triangles[0];
        double largest = 0;
        foreach (var candidate in triangles)
        {
            var area = Math.Abs(Area(candidate));
            if (area > largest) { largest = area; best = candidate; }
        }
        if (largest < 1e-10) return false;
        var a = best.A; var b = best.B; var c = best.C;
        var normalization = Math.Max(a.Camera.Z, Math.Max(b.Camera.Z, c.Camera.Z));
        if (!float.IsFinite(normalization) || normalization <= 0) return false;
        var x = Coefficients(a.Screen.X * (double)a.Camera.Z / normalization,
            b.Screen.X * (double)b.Camera.Z / normalization, c.Screen.X * (double)c.Camera.Z / normalization);
        var y = Coefficients(a.Screen.Y * (double)a.Camera.Z / normalization,
            b.Screen.Y * (double)b.Camera.Z / normalization, c.Screen.Y * (double)c.Camera.Z / normalization);
        var w = Coefficients(a.Camera.Z / (double)normalization,
            b.Camera.Z / (double)normalization, c.Camera.Z / (double)normalization);
        transform = new Matrix4x4(
            (float)(x.U / width), (float)(y.U / width), 0, (float)(w.U / width),
            (float)(x.V / height), (float)(y.V / height), 0, (float)(w.V / height),
            0, 0, 1, 0,
            (float)x.C, (float)y.C, 0, (float)w.C);
        foreach (var triangle in triangles)
            if (!Matches(triangle.A) || !Matches(triangle.B) || !Matches(triangle.C)) return false;
        return true;

        bool Matches(Reference3DProjectiveVertex vertex)
        {
            var divisor = w.U * vertex.U + w.V * vertex.V + w.C;
            if (!double.IsFinite(divisor) || divisor <= 0) return false;
            var dx = (x.U * vertex.U + x.V * vertex.V + x.C) / divisor - vertex.Screen.X;
            var dy = (y.U * vertex.U + y.V * vertex.V + y.C) / divisor - vertex.Screen.Y;
            return double.IsFinite(dx) && double.IsFinite(dy) && dx * dx + dy * dy <= 0.25;
        }

        (double U, double V, double C) Coefficients(double av, double bv, double cv)
        {
            var determinant = Area(best);
            var u = ((bv - av) * (c.V - a.V) - (cv - av) * (b.V - a.V)) / determinant;
            var v = ((b.U - a.U) * (cv - av) - (c.U - a.U) * (bv - av)) / determinant;
            return (u, v, av - u * a.U - v * a.V);
        }

        static double Area(Reference3DProjectiveTriangle t) =>
            (t.B.U - (double)t.A.U) * (t.C.V - (double)t.A.V)
            - (t.C.U - (double)t.A.U) * (t.B.V - (double)t.A.V);
    }
}
