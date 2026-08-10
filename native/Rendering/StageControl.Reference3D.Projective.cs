using System.Numerics;

namespace VectorAnimationEngine;

internal readonly record struct Reference3DProjectiveVertex(
    PointF Flat,
    PointF Screen,
    float U,
    float V,
    Vector3 Camera);

internal readonly record struct Reference3DProjectiveTriangle(
    Reference3DProjectiveVertex A,
    Reference3DProjectiveVertex B,
    Reference3DProjectiveVertex C);

internal sealed partial class StageControl
{
    private const int Reference3DProjectiveMeshMaximumDepth = 4;
    private const float Reference3DProjectiveMeshErrorPixels = 0.3f;
    private const float Reference3DProjectiveTriangleAreaEpsilon = 0.01f;

    internal bool TryGetReference3DProjectiveMesh(
        int objectIndex,
        bool usePrimaryContourQuad,
        out Reference3DProjectiveTriangle[] triangles)
    {
        triangles = [];
        if ((uint)objectIndex >= Scene.ObjectCount) return false;
        var contours = GetReference3DSourceContours(objectIndex);
        if (Scene.ShapeKind[objectIndex] == ShapeKind.Line && Scene.Stroke[objectIndex] > 0)
        {
            var strokeContours = GetReference3DStrokeOutlineSourceContours(objectIndex);
            if (strokeContours.Length > 0)
            {
                var points = strokeContours.SelectMany(contour => contour.Points).ToArray();
                var padding = Math.Max(
                    1f,
                    GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]) * 0.08f);
                var left = points.Min(point => point.X) - padding;
                var top = points.Min(point => point.Y) - padding;
                var right = points.Max(point => point.X) + padding;
                var bottom = points.Max(point => point.Y) + padding;
                contours =
                [
                    .. strokeContours,
                    new Reference3DSourceContour(
                    [
                        new PointF(left, top),
                        new PointF(right, top),
                        new PointF(right, bottom),
                        new PointF(left, bottom),
                        new PointF(left, top)
                    ],
                    true)
                ];
            }
        }
        if (!TryGetReference3DProjectiveDomain(
                contours,
                usePrimaryContourQuad,
                out var topLeft,
                out var topRight,
                out var bottomRight,
                out var bottomLeft))
        {
            return false;
        }

        var gridSize = 1 << Reference3DProjectiveMeshMaximumDepth;
        var frontOffset = GetReference3DExtrusionOffset(objectIndex, front: true);
        var vertices = new Dictionary<(int X, int Y), Reference3DProjectiveVertex>();
        bool TryGetVertex(int x, int y, out Reference3DProjectiveVertex vertex)
        {
            if (vertices.TryGetValue((x, y), out vertex)) return true;
            var u = x / (float)gridSize;
            var v = y / (float)gridSize;
            var top = LerpReference3DPoint(topLeft, topRight, u);
            var bottom = LerpReference3DPoint(bottomLeft, bottomRight, u);
            var flat = LerpReference3DPoint(top, bottom, v);
            if (!TryTransformFlatPointToCamera(objectIndex, flat, frontOffset, out var camera)) return false;
            var screen = camera.Z >= ReferenceNearPlane
                ? ProjectCameraVector(camera)
                : PointF.Empty;
            if (camera.Z >= ReferenceNearPlane
                && (!float.IsFinite(screen.X) || !float.IsFinite(screen.Y)))
            {
                return false;
            }
            vertex = new Reference3DProjectiveVertex(flat, screen, u, v, camera);
            vertices.Add((x, y), vertex);
            return true;
        }

        var result = new List<Reference3DProjectiveTriangle>();
        bool AppendCell(int left, int top, int right, int bottom, int depth)
        {
            var middleX = (left + right) / 2;
            var middleY = (top + bottom) / 2;
            if (!TryGetVertex(left, top, out var topLeftVertex)
                || !TryGetVertex(right, top, out var topRightVertex)
                || !TryGetVertex(right, bottom, out var bottomRightVertex)
                || !TryGetVertex(left, bottom, out var bottomLeftVertex)
                || !TryGetVertex(middleX, top, out var topMiddleVertex)
                || !TryGetVertex(right, middleY, out var rightMiddleVertex)
                || !TryGetVertex(middleX, bottom, out var bottomMiddleVertex)
                || !TryGetVertex(left, middleY, out var leftMiddleVertex)
                || !TryGetVertex(middleX, middleY, out var centerVertex))
            {
                return false;
            }

            var insideCorners = (topLeftVertex.Camera.Z >= ReferenceNearPlane ? 1 : 0)
                + (topRightVertex.Camera.Z >= ReferenceNearPlane ? 1 : 0)
                + (bottomRightVertex.Camera.Z >= ReferenceNearPlane ? 1 : 0)
                + (bottomLeftVertex.Camera.Z >= ReferenceNearPlane ? 1 : 0);
            if (insideCorners == 0) return true;
            var canSubdivide = depth < Reference3DProjectiveMeshMaximumDepth
                && right - left > 1
                && bottom - top > 1;
            if (insideCorners < 4)
            {
                if (canSubdivide)
                {
                    return AppendCell(left, top, middleX, middleY, depth + 1)
                        && AppendCell(middleX, top, right, middleY, depth + 1)
                        && AppendCell(middleX, middleY, right, bottom, depth + 1)
                        && AppendCell(left, middleY, middleX, bottom, depth + 1);
                }

                AppendClippedReference3DProjectiveTriangle(
                    result,
                    topLeftVertex,
                    topRightVertex,
                    bottomRightVertex);
                AppendClippedReference3DProjectiveTriangle(
                    result,
                    topLeftVertex,
                    bottomRightVertex,
                    bottomLeftVertex);
                return true;
            }

            var error = Math.Max(
                Reference3DProjectiveMidpointError(
                    topLeftVertex.Screen,
                    topRightVertex.Screen,
                    topMiddleVertex.Screen),
                Math.Max(
                    Reference3DProjectiveMidpointError(
                        topRightVertex.Screen,
                        bottomRightVertex.Screen,
                        rightMiddleVertex.Screen),
                    Math.Max(
                        Reference3DProjectiveMidpointError(
                            bottomLeftVertex.Screen,
                            bottomRightVertex.Screen,
                            bottomMiddleVertex.Screen),
                        Math.Max(
                            Reference3DProjectiveMidpointError(
                                topLeftVertex.Screen,
                                bottomLeftVertex.Screen,
                                leftMiddleVertex.Screen),
                            Reference3DProjectiveCenterError(
                                topLeftVertex.Screen,
                                topRightVertex.Screen,
                                bottomRightVertex.Screen,
                                bottomLeftVertex.Screen,
                                centerVertex.Screen)))));
            if (canSubdivide && error > Reference3DProjectiveMeshErrorPixels)
            {
                return AppendCell(left, top, middleX, middleY, depth + 1)
                    && AppendCell(middleX, top, right, middleY, depth + 1)
                    && AppendCell(middleX, middleY, right, bottom, depth + 1)
                    && AppendCell(left, middleY, middleX, bottom, depth + 1);
            }

            AppendReference3DProjectiveTriangle(
                result,
                topLeftVertex,
                topRightVertex,
                bottomRightVertex);
            AppendReference3DProjectiveTriangle(
                result,
                topLeftVertex,
                bottomRightVertex,
                bottomLeftVertex);
            return true;
        }

        if (!AppendCell(0, 0, gridSize, gridSize, 0) || result.Count == 0) return false;
        triangles = result.ToArray();
        return true;
    }

    internal static bool TryGetReference3DFlatToScreenTransform(
        Reference3DProjectiveTriangle triangle,
        out Matrix3x2 transform)
    {
        return TryGetReference3DAffineTransform(
            triangle.A.Flat,
            triangle.B.Flat,
            triangle.C.Flat,
            triangle.A.Screen,
            triangle.B.Screen,
            triangle.C.Screen,
            out transform);
    }

    internal static bool TryGetReference3DTextureToScreenTransform(
        Reference3DProjectiveTriangle triangle,
        float pixelWidth,
        float pixelHeight,
        out Matrix3x2 transform)
    {
        return TryGetReference3DAffineTransform(
            new PointF(triangle.A.U * pixelWidth, triangle.A.V * pixelHeight),
            new PointF(triangle.B.U * pixelWidth, triangle.B.V * pixelHeight),
            new PointF(triangle.C.U * pixelWidth, triangle.C.V * pixelHeight),
            triangle.A.Screen,
            triangle.B.Screen,
            triangle.C.Screen,
            out transform);
    }

    internal static SizeF EstimateReference3DProjectiveTextureSize(
        IReadOnlyList<Reference3DProjectiveTriangle> triangles)
    {
        var width = 0d;
        var height = 0d;
        foreach (var triangle in triangles)
        {
            if (!TryGetReference3DAffineTransform(
                    new PointF(triangle.A.U, triangle.A.V),
                    new PointF(triangle.B.U, triangle.B.V),
                    new PointF(triangle.C.U, triangle.C.V),
                    triangle.A.Screen,
                    triangle.B.Screen,
                    triangle.C.Screen,
                    out var transform))
            {
                continue;
            }
            width = Math.Max(width, Math.Sqrt(
                (double)transform.M11 * transform.M11
                + (double)transform.M12 * transform.M12));
            height = Math.Max(height, Math.Sqrt(
                (double)transform.M21 * transform.M21
                + (double)transform.M22 * transform.M22));
        }

        return new SizeF(
            Reference3DProjectiveTextureDimension(width),
            Reference3DProjectiveTextureDimension(height));
    }

    internal static bool Reference3DProjectiveMeshCoversFullDomain(
        IReadOnlyList<Reference3DProjectiveTriangle> triangles)
    {
        var corners = 0;
        foreach (var triangle in triangles)
        {
            AddCorner(triangle.A);
            AddCorner(triangle.B);
            AddCorner(triangle.C);
            if (corners == 0b1111) return true;
        }
        return false;

        void AddCorner(Reference3DProjectiveVertex vertex)
        {
            const float epsilon = 0.00001f;
            if (Math.Abs(vertex.U) <= epsilon && Math.Abs(vertex.V) <= epsilon) corners |= 0b0001;
            else if (Math.Abs(vertex.U - 1f) <= epsilon && Math.Abs(vertex.V) <= epsilon) corners |= 0b0010;
            else if (Math.Abs(vertex.U - 1f) <= epsilon && Math.Abs(vertex.V - 1f) <= epsilon) corners |= 0b0100;
            else if (Math.Abs(vertex.U) <= epsilon && Math.Abs(vertex.V - 1f) <= epsilon) corners |= 0b1000;
        }
    }

    internal static Rectangle GetReference3DProjectiveTextureBounds(
        Reference3DProjectiveTriangle triangle,
        int pixelWidth,
        int pixelHeight)
    {
        var left = Math.Clamp(
            (int)MathF.Floor(Math.Min(triangle.A.U, Math.Min(triangle.B.U, triangle.C.U)) * pixelWidth) - 1,
            0,
            pixelWidth);
        var top = Math.Clamp(
            (int)MathF.Floor(Math.Min(triangle.A.V, Math.Min(triangle.B.V, triangle.C.V)) * pixelHeight) - 1,
            0,
            pixelHeight);
        var right = Math.Clamp(
            (int)MathF.Ceiling(Math.Max(triangle.A.U, Math.Max(triangle.B.U, triangle.C.U)) * pixelWidth) + 1,
            left,
            pixelWidth);
        var bottom = Math.Clamp(
            (int)MathF.Ceiling(Math.Max(triangle.A.V, Math.Max(triangle.B.V, triangle.C.V)) * pixelHeight) + 1,
            top,
            pixelHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool TryGetReference3DProjectiveDomain(
        IReadOnlyList<Reference3DSourceContour> contours,
        bool usePrimaryContourQuad,
        out PointF topLeft,
        out PointF topRight,
        out PointF bottomRight,
        out PointF bottomLeft)
    {
        topLeft = PointF.Empty;
        topRight = PointF.Empty;
        bottomRight = PointF.Empty;
        bottomLeft = PointF.Empty;
        if (usePrimaryContourQuad)
        {
            foreach (var primary in contours)
            {
                if (!primary.Closed
                    || EffectiveReference3DPointCount(primary.Points, primary.Closed) != 4)
                {
                    continue;
                }
                topLeft = primary.Points[0];
                topRight = primary.Points[1];
                bottomRight = primary.Points[2];
                bottomLeft = primary.Points[3];
                if (Reference3DProjectiveDomainIsValid(topLeft, topRight, bottomRight, bottomLeft))
                {
                    return true;
                }
            }
            return false;
        }

        var points = contours.SelectMany(contour => contour.Points).ToArray();
        if (points.Length < 3) return false;
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        if (!float.IsFinite(left)
            || !float.IsFinite(top)
            || !float.IsFinite(right)
            || !float.IsFinite(bottom)
            || right - left <= 0.0001f
            || bottom - top <= 0.0001f)
        {
            return false;
        }

        topLeft = new PointF(left, top);
        topRight = new PointF(right, top);
        bottomRight = new PointF(right, bottom);
        bottomLeft = new PointF(left, bottom);
        return true;
    }

    private static bool Reference3DProjectiveDomainIsValid(
        PointF topLeft,
        PointF topRight,
        PointF bottomRight,
        PointF bottomLeft)
    {
        var firstArea = Reference2DCross(topLeft, topRight, bottomRight);
        var secondArea = Reference2DCross(topLeft, bottomRight, bottomLeft);
        return float.IsFinite(topLeft.X)
            && float.IsFinite(topLeft.Y)
            && float.IsFinite(topRight.X)
            && float.IsFinite(topRight.Y)
            && float.IsFinite(bottomRight.X)
            && float.IsFinite(bottomRight.Y)
            && float.IsFinite(bottomLeft.X)
            && float.IsFinite(bottomLeft.Y)
            && double.IsFinite(firstArea)
            && double.IsFinite(secondArea)
            && Math.Abs(firstArea) > 0.0001d
            && Math.Abs(secondArea) > 0.0001d
            && Math.Sign(firstArea) == Math.Sign(secondArea);
    }

    private static bool TryGetReference3DAffineTransform(
        PointF sourceA,
        PointF sourceB,
        PointF sourceC,
        PointF destinationA,
        PointF destinationB,
        PointF destinationC,
        out Matrix3x2 transform)
    {
        transform = default;
        var sourceX1 = (double)sourceB.X - sourceA.X;
        var sourceY1 = (double)sourceB.Y - sourceA.Y;
        var sourceX2 = (double)sourceC.X - sourceA.X;
        var sourceY2 = (double)sourceC.Y - sourceA.Y;
        var sourceScale = Math.Max(
            Math.Max(Math.Abs(sourceX1), Math.Abs(sourceY1)),
            Math.Max(Math.Abs(sourceX2), Math.Abs(sourceY2)));
        var determinant = sourceX1 * sourceY2 - sourceX2 * sourceY1;
        if (!double.IsFinite(determinant)
            || !double.IsFinite(sourceScale)
            || sourceScale <= 0
            || Math.Abs(determinant) <= sourceScale * sourceScale * 0.000000000001d)
        {
            return false;
        }

        var destinationX1 = (double)destinationB.X - destinationA.X;
        var destinationY1 = (double)destinationB.Y - destinationA.Y;
        var destinationX2 = (double)destinationC.X - destinationA.X;
        var destinationY2 = (double)destinationC.Y - destinationA.Y;
        var m11 = (destinationX1 * sourceY2 - destinationX2 * sourceY1) / determinant;
        var m21 = (sourceX1 * destinationX2 - sourceX2 * destinationX1) / determinant;
        var m12 = (destinationY1 * sourceY2 - destinationY2 * sourceY1) / determinant;
        var m22 = (sourceX1 * destinationY2 - sourceX2 * destinationY1) / determinant;
        var m31 = destinationA.X - sourceA.X * m11 - sourceA.Y * m21;
        var m32 = destinationA.Y - sourceA.X * m12 - sourceA.Y * m22;
        if (!Reference3DProjectiveMatrixElementIsFinite(m11)
            || !Reference3DProjectiveMatrixElementIsFinite(m12)
            || !Reference3DProjectiveMatrixElementIsFinite(m21)
            || !Reference3DProjectiveMatrixElementIsFinite(m22)
            || !Reference3DProjectiveMatrixElementIsFinite(m31)
            || !Reference3DProjectiveMatrixElementIsFinite(m32))
        {
            return false;
        }
        transform = new Matrix3x2(
            (float)m11,
            (float)m12,
            (float)m21,
            (float)m22,
            (float)m31,
            (float)m32);
        return true;
    }

    private void AppendClippedReference3DProjectiveTriangle(
        ICollection<Reference3DProjectiveTriangle> destination,
        Reference3DProjectiveVertex a,
        Reference3DProjectiveVertex b,
        Reference3DProjectiveVertex c)
    {
        var source = new[] { a, b, c };
        var clipped = new List<Reference3DProjectiveVertex>(4);
        var previous = source[^1];
        var previousInside = previous.Camera.Z >= ReferenceNearPlane;
        foreach (var current in source)
        {
            var currentInside = current.Camera.Z >= ReferenceNearPlane;
            if (currentInside != previousInside)
            {
                if (!TryIntersectReference3DProjectiveNearPlane(previous, current, out var intersection)) return;
                clipped.Add(intersection);
            }
            if (currentInside) clipped.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        if (clipped.Count < 3) return;
        for (var index = 1; index < clipped.Count - 1; index++)
        {
            AppendReference3DProjectiveTriangle(
                destination,
                clipped[0],
                clipped[index],
                clipped[index + 1]);
        }
    }

    private bool TryIntersectReference3DProjectiveNearPlane(
        Reference3DProjectiveVertex start,
        Reference3DProjectiveVertex end,
        out Reference3DProjectiveVertex intersection)
    {
        intersection = default;
        var denominator = (double)end.Camera.Z - start.Camera.Z;
        if (!double.IsFinite(denominator) || denominator == 0) return false;
        var amount = (float)Math.Clamp(
            (ReferenceNearPlane - (double)start.Camera.Z) / denominator,
            0d,
            1d);
        var camera = Vector3.Lerp(start.Camera, end.Camera, amount);
        camera.Z = ReferenceNearPlane;
        intersection = new Reference3DProjectiveVertex(
            LerpReference3DPoint(start.Flat, end.Flat, amount),
            ProjectCameraVector(camera),
            start.U + (end.U - start.U) * amount,
            start.V + (end.V - start.V) * amount,
            camera);
        return Reference3DProjectiveVertexIsFinite(intersection);
    }

    private static void AppendReference3DProjectiveTriangle(
        ICollection<Reference3DProjectiveTriangle> destination,
        Reference3DProjectiveVertex a,
        Reference3DProjectiveVertex b,
        Reference3DProjectiveVertex c)
    {
        if (!Reference3DProjectiveVertexIsFinite(a)
            || !Reference3DProjectiveVertexIsFinite(b)
            || !Reference3DProjectiveVertexIsFinite(c)
            || a.Camera.Z < ReferenceNearPlane
            || b.Camera.Z < ReferenceNearPlane
            || c.Camera.Z < ReferenceNearPlane)
        {
            return;
        }
        var area = Reference2DCross(a.Screen, b.Screen, c.Screen);
        if (!double.IsFinite(area)
            || Math.Abs(area) <= Reference3DProjectiveTriangleAreaEpsilon)
        {
            return;
        }
        destination.Add(new Reference3DProjectiveTriangle(a, b, c));
    }

    private static bool Reference3DProjectiveVertexIsFinite(Reference3DProjectiveVertex vertex)
    {
        return float.IsFinite(vertex.Flat.X)
            && float.IsFinite(vertex.Flat.Y)
            && float.IsFinite(vertex.Screen.X)
            && float.IsFinite(vertex.Screen.Y)
            && float.IsFinite(vertex.U)
            && float.IsFinite(vertex.V)
            && float.IsFinite(vertex.Camera.X)
            && float.IsFinite(vertex.Camera.Y)
            && float.IsFinite(vertex.Camera.Z);
    }

    private static bool Reference3DProjectiveMatrixElementIsFinite(double value)
    {
        return double.IsFinite(value) && value >= -float.MaxValue && value <= float.MaxValue;
    }

    private static float Reference3DProjectiveTextureDimension(double value)
    {
        if (!double.IsFinite(value)) return float.MaxValue;
        return (float)Math.Clamp(value, 1d, float.MaxValue);
    }

    private static float Reference3DProjectiveMidpointError(
        PointF start,
        PointF end,
        PointF actual)
    {
        return ReferencePointDistance(
            new PointF((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f),
            actual);
    }

    private static float Reference3DProjectiveCenterError(
        PointF topLeft,
        PointF topRight,
        PointF bottomRight,
        PointF bottomLeft,
        PointF actual)
    {
        return ReferencePointDistance(
            new PointF(
                (topLeft.X + topRight.X + bottomRight.X + bottomLeft.X) * 0.25f,
                (topLeft.Y + topRight.Y + bottomRight.Y + bottomLeft.Y) * 0.25f),
            actual);
    }

    private static PointF LerpReference3DPoint(PointF start, PointF end, float amount)
    {
        return new PointF(
            start.X + (end.X - start.X) * amount,
            start.Y + (end.Y - start.Y) * amount);
    }
}
