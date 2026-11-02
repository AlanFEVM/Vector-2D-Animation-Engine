using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private sealed record SvgMaskExtraction(ImportedSvgBreakResult? Result);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, SvgMaskExtraction> SvgMaskExtractions = new();
    internal IReadOnlyList<GraphicsPath> CreateCompositionMaskObjectPaths(VectorScene scene, int index)
    {
        if (scene.ShapeKind[index] == ShapeKind.ImportedSvg
            && !TryGetObjectDistortionsForRendering(scene, index, out _)
            && scene.TryGetImportedSvgSource(index, out var source)
            && TryCreateExactSvgMaskPaths(scene, index, source, out var paths)) return paths;
        return new[] { CreateCompositionMaskObjectPath(scene, index) };
    }

    private bool TryCreateExactSvgMaskPaths(VectorScene scene, int index, string source,
        out IReadOnlyList<GraphicsPath> paths)
    {
        paths = [];
        var extracted = SvgMaskExtractions.GetValue(source, static value =>
        {
            try { return new(ImportedSvgBreakApart.Extract(value, forceStrokeOutlines: false, requireExactGeometry: true)); }
            catch (InvalidDataException) { return new(null); }
        }).Result;
        if (extracted is null) return false;
        // Stroke outlines and raster content need separate mask semantics.
        // Keep the existing route until those representations are supported.
        if (extracted.Parts.Any(part => part is not ImportedSvgBreakFill)) return false;
        var result = new List<GraphicsPath>();
        try
        {
            var center = WorldToScreen(scene.X[index], scene.Y[index]);
            var angle = scene.Angle[index] + PlanarCanvasRotation;
            var sx = scene.Width[index] / extracted.IntrinsicSize.Width * Zoom / VectorUnits.UnitsPerPixel;
            var sy = scene.Height[index] / extracted.IntrinsicSize.Height * Zoom / VectorUnits.UnitsPerPixel;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            using var transform = new Matrix(sx * cos, sx * sin, -sy * sin, sy * cos,
                center.X - scene.Width[index] * Zoom / VectorUnits.UnitsPerPixel * cos * .5f
                    + scene.Height[index] * Zoom / VectorUnits.UnitsPerPixel * sin * .5f,
                center.Y - scene.Width[index] * Zoom / VectorUnits.UnitsPerPixel * sin * .5f
                    - scene.Height[index] * Zoom / VectorUnits.UnitsPerPixel * cos * .5f);
            foreach (var fill in extracted.Parts.Cast<ImportedSvgBreakFill>())
            {
                var path = new GraphicsPath(fill.UsesEvenOddFillRule ? FillMode.Alternate : FillMode.Winding);
                result.Add(path);
                foreach (var contour in fill.BezierContours)
                {
                    path.StartFigure();
                    for (var node = 0; node < contour.Length; node++)
                    {
                        var current = contour[node];
                        var next = contour[(node + 1) % contour.Length];
                        path.AddBezier(current.Anchor, current.OutgoingControl, next.IncomingControl, next.Anchor);
                    }
                    path.CloseFigure();
                }
                path.Transform(transform);
            }
            paths = result;
            return true;
        }
        catch { foreach (var path in result) path.Dispose(); throw; }
    }

    internal SceneCompositionMaskPlan? CompositionMasksFor(VectorScene scene)
    {
        if (ReferenceEquals(OnionSkinScene, scene)
            && _onionSkinMaskPlan is { ContentsByMaskLayer.Count: > 0 } onionPlan) return onionPlan;
        if (ReferenceEquals(DragPreviewScene, scene)
            && _dragPreviewCompositionResult?.LayerMasks is { ContentsByMaskLayer.Count: > 0 } dragPlan) return dragPlan;
        var result = ReferenceEquals(UnderlayScene, scene) && _underlayCompositionResult is not null
            ? _underlayCompositionResult
            : ReferenceEquals(_sceneCompositionResultScene, scene) ? _sceneCompositionResult : null;
        return result?.LayerMasks is { ContentsByMaskLayer.Count: > 0 } plan ? plan : null;
    }

    internal static bool IsCompositionMaskArtworkHidden(VectorScene scene, SceneCompositionMaskPlan plan, int layer)
        => plan.ContainersByLayer.TryGetValue(layer, out var masks)
            && masks.Any(scene.IsLayerEffectivelyLocked);

    internal GraphicsPath CreateProjectedCompositionMaskObjectPath(int index)
        => ProjectCompositionMaskPath(index, CreateCompositionMaskObjectPath(Scene, index));

    internal IReadOnlyList<GraphicsPath> CreateProjectedCompositionMaskObjectPaths(int index)
    {
        if (EffectiveReferenceProjection != CameraProjection.Orthographic)
            return new[] { CreateProjectedCompositionMaskObjectPath(index) };
        var paths = CreateCompositionMaskObjectPaths(Scene, index);
        var result = new List<GraphicsPath>();
        var consumed = 0;
        try
        {
            foreach (var path in paths)
            {
                consumed++;
                result.Add(ProjectCompositionMaskPath(index, path));
            }
            return result;
        }
        catch
        {
            foreach (var path in result) path.Dispose();
            for (var indexToDispose = consumed; indexToDispose < paths.Count; indexToDispose++) paths[indexToDispose].Dispose();
            throw;
        }
    }

    private GraphicsPath ProjectCompositionMaskPath(int index, GraphicsPath path)
    {
        // Orthographic projection of an object's plane is affine. Transform the
        // same curved fill path used by the 2D pass, without sampling the curve.
        // Perspective retains the renderer's existing near-clipped contours.
        if (EffectiveReferenceProjection != CameraProjection.Orthographic)
        { path.Dispose(); return CreateReference3DMaskPath(new[] { index }); }
        const float basisLength = 100f;
        var origin = new PointF(Scene.X[index], Scene.Y[index]);
        var right = new PointF(origin.X + basisLength, origin.Y);
        var down = new PointF(origin.X, origin.Y + basisLength);
        if (!TryProjectScenePoint(index, origin, out var projectedOrigin, out _)
            || !TryProjectScenePoint(index, right, out var projectedRight, out _)
            || !TryProjectScenePoint(index, down, out var projectedDown, out _))
        { path.Dispose(); return CreateReference3DMaskPath(new[] { index }); }
        var sourceOrigin = WorldToScreen(origin);
        var sourceRight = WorldToScreen(right);
        var sourceDown = WorldToScreen(down);
        using var source = new Matrix(
            (sourceRight.X - sourceOrigin.X) / basisLength, (sourceRight.Y - sourceOrigin.Y) / basisLength,
            (sourceDown.X - sourceOrigin.X) / basisLength, (sourceDown.Y - sourceOrigin.Y) / basisLength,
            sourceOrigin.X, sourceOrigin.Y);
        if (!source.IsInvertible) { path.Dispose(); return CreateReference3DMaskPath(new[] { index }); }
        source.Invert();
        using var destination = new Matrix(
            (projectedRight.X - projectedOrigin.X) / basisLength, (projectedRight.Y - projectedOrigin.Y) / basisLength,
            (projectedDown.X - projectedOrigin.X) / basisLength, (projectedDown.Y - projectedOrigin.Y) / basisLength,
            projectedOrigin.X, projectedOrigin.Y);
        try
        {
            path.Transform(source);
            // An orthographic camera can still cut through a tilted object.
            // The affine shortcut is valid only when the entire control hull
            // stays in front of the near plane; otherwise use near clipping.
            if (path.PointCount > 0 && path.PathPoints.Any(point => !TryProjectScenePoint(index,
                new PointF(point.X + origin.X, point.Y + origin.Y), out _, out _)))
            {
                path.Reset();
                AppendReference3DContours(path, GetReference3DProjectedContours(index), fillOnly: true);
                return path;
            }
            path.Transform(destination);
            return path;
        }
        catch { path.Dispose(); throw; }
    }

    internal GraphicsPath CreateCompositionMaskObjectPath(VectorScene scene, int index)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        try
        {
            if (!SceneRenderOrder.HasFill(scene.ShapeKind[index])) return path;
            if (scene.ShapeKind[index] == ShapeKind.Text
                && !TryGetObjectDistortionsForRendering(scene, index, out _))
            {
                if (scene.TryGetTextWorldContours(index, out var textContours))
                    AppendPolygonContours(path, textContours);
                return path;
            }
            if (TryGetObjectDistortionsForRendering(scene, index, out var distortions))
            {
                if (TryGetDistortedVectorGeometryForRendering(scene, index, distortions, out var geometry))
                    AppendDistortedVectorGeometry(path, geometry);
                else AppendPolygonContours(path, GetObjectBoundaryContoursForRendering(scene, index));
            }
            else if (scene.ShapeKind[index] == ShapeKind.Ellipse)
            {
                // A native ellipse's boundary query is sampled for spatial work;
                // clipping must use the same curved primitive as its GDI paint.
                path.AddEllipse(-scene.Width[index] * .5f, -scene.Height[index] * .5f,
                    scene.Width[index], scene.Height[index]);
                var center = WorldToScreen(scene.X[index], scene.Y[index]);
                var angle = scene.Angle[index] + PlanarCanvasRotation;
                var scale = Zoom / VectorUnits.UnitsPerPixel;
                var cos = MathF.Cos(angle) * scale;
                var sin = MathF.Sin(angle) * scale;
                using var transform = new Matrix(cos, sin, -sin, cos, center.X, center.Y);
                path.Transform(transform);
            }
            else AppendObjectBoundaryPath(path, scene, index);
            return path;
        }
        catch { path.Dispose(); throw; }
    }
}
