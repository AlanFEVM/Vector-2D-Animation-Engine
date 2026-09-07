using System.Reflection;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private const BindingFlags PrivateInstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static FieldInfo RequireField(
        Type type,
        string name,
        BindingFlags flags = PrivateInstanceFlags)
    {
        return type.GetField(name, flags)
            ?? throw new InvalidOperationException(
                $"Required field '{type.FullName ?? type.Name}.{name}' was not found.");
    }

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        BindingFlags flags = PrivateInstanceFlags)
    {
        return type.GetMethod(name, flags)
            ?? throw new InvalidOperationException(
                $"Required method '{type.FullName ?? type.Name}.{name}(...)' was not found.");
    }

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        Type[] parameterTypes,
        BindingFlags flags = PrivateInstanceFlags)
    {
        return type.GetMethod(name, flags, binder: null, parameterTypes, modifiers: null)
            ?? throw new InvalidOperationException(
                $"Required method '{type.FullName ?? type.Name}.{name}({string.Join(", ", parameterTypes.Select(parameterType => parameterType.FullName ?? parameterType.Name))})' was not found.");
    }

    private static PropertyInfo RequireProperty(
        Type type,
        string name,
        BindingFlags flags = PrivateInstanceFlags)
    {
        return type.GetProperty(name, flags)
            ?? throw new InvalidOperationException(
                $"Required property '{type.FullName ?? type.Name}.{name}' was not found.");
    }

    private static void ForceFullCollectionForBenchmark()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static Color SampleBitmap(Bitmap bitmap, PointF point)
    {
        var x = Math.Clamp((int)MathF.Round(point.X), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)MathF.Round(point.Y), 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y);
    }

    private static void AssertTimeline(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A later regression run uses a unique directory.
        }
    }

    private static int ApplyInstanceAppearanceForRegression(int argb, float alpha, int tintArgb)
    {
        static int Multiply(int first, int second) => (first * second + 127) / 255;
        var resultAlpha = (int)Math.Clamp(((argb >>> 24) & 0xff) * alpha, 0f, 255f);
        return (resultAlpha << 24)
            | (Multiply((argb >>> 16) & 0xff, (tintArgb >>> 16) & 0xff) << 16)
            | (Multiply((argb >>> 8) & 0xff, (tintArgb >>> 8) & 0xff) << 8)
            | Multiply(argb & 0xff, tintArgb & 0xff);
    }

    private static bool NearlyEqual(float actual, float expected) => Math.Abs(actual - expected) <= 0.001f;

    private static bool PointsNear(PointF actual, PointF expected)
    {
        return Math.Abs(actual.X - expected.X) <= 0.1f && Math.Abs(actual.Y - expected.Y) <= 0.1f;
    }

    private static bool PointsWithin(PointF actual, PointF expected, float tolerance)
    {
        var dx = actual.X - expected.X;
        var dy = actual.Y - expected.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static bool ContoursNear(IReadOnlyList<PointF[]> actual, IReadOnlyList<PointF[]> expected)
    {
        if (actual.Count != expected.Count) return false;
        for (var contourIndex = 0; contourIndex < actual.Count; contourIndex++)
        {
            var actualContour = actual[contourIndex];
            var expectedContour = expected[contourIndex];
            if (actualContour.Length != expectedContour.Length) return false;
            for (var pointIndex = 0; pointIndex < actualContour.Length; pointIndex++)
            {
                if (!PointsNear(actualContour[pointIndex], expectedContour[pointIndex])) return false;
            }
        }

        return true;
    }

    private static void AssertBrushCoversCenterline(VectorScene scene, int objectIndex, IReadOnlyList<PointF> centerline)
    {
        if ((uint)objectIndex >= scene.ObjectCount
            || scene.ShapeKind[objectIndex] != ShapeKind.Path
            || scene.Stroke[objectIndex] != 0)
        {
            throw new InvalidOperationException("Brush did not commit as an unstroked Path fill.");
        }

        const int samplesPerSegment = 8;
        for (var segment = 0; segment < centerline.Count - 1; segment++)
        {
            var start = centerline[segment];
            var end = centerline[segment + 1];
            for (var sample = 0; sample <= samplesPerSegment; sample++)
            {
                var t = sample / (float)samplesPerSegment;
                var point = new PointF(
                    start.X + (end.X - start.X) * t,
                    start.Y + (end.Y - start.Y) * t);
                if (!scene.FillContainsPoint(objectIndex, point))
                {
                    throw new InvalidOperationException($"Brush fill contains a false hole on centerline segment {segment} at t={t:0.###}.");
                }
            }
        }
    }
}
