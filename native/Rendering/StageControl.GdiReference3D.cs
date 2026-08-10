using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private RenderStats DrawReference3DScene(Graphics graphics)
    {
        var editableScene = Scene;
        var onionSkinStats = default(RenderStats);
        var underlayStats = default(RenderStats);
        try
        {
            if (OnionSkinScene is { } onionSkin)
            {
                Scene = onionSkin;
                RecordOnionSkinScenePass();
                onionSkinStats = DrawReference3DCurrentScene(graphics);
            }

            if (UnderlayScene is { } underlay)
            {
                Scene = underlay;
                RecordUnderlayScenePass();
                underlayStats = DrawReference3DCurrentScene(graphics);
            }

            Scene = editableScene;
            RecordEditableScenePass();
            var editableStats = DrawReference3DCurrentScene(graphics);
            return RenderStats.Combine(
                RenderStats.Combine(onionSkinStats, underlayStats),
                editableStats);
        }
        finally
        {
            Scene = editableScene;
        }
    }

    private RenderStats DrawReference3DCurrentScene(Graphics graphics)
    {
        var visible = 0;
        var drawn = 0;
        long atoms = 0;
        var objectsByLayer = new int[Scene.LayerCount][];
        for (var layer = Scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!Scene.ShouldRenderLayerContent(layer))
            {
                objectsByLayer[layer] = [];
                continue;
            }
            var objects = GetReference3DLayerObjects(layer);
            objectsByLayer[layer] = objects;
            visible += objects.Length;
            foreach (var objectIndex in objects) atoms += Scene.AtomCount[objectIndex];
        }

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (Scene.HasNonNormalLayerBlendModes)
            {
                var maskPaths = new Dictionary<int, GraphicsPath?>();
                var drawnObjects = new HashSet<int>();
                try
                {
                    LayerCompositor().CompositeBatchesTo(
                        graphics,
                        Scene,
                        layer => objectsByLayer[layer].Length > 0,
                        (layerGraphics, layers) =>
                        {
                            foreach (var item in GetReference3DCompositeLayerRenderItems(layers))
                            {
                                if (DrawReference3DSceneItem(layerGraphics, item, maskPaths))
                                {
                                    drawnObjects.Add(item.ObjectIndex);
                                }
                            }
                        });
                    drawn = drawnObjects.Count;
                }
                finally
                {
                    foreach (var path in maskPaths.Values) path?.Dispose();
                }
            }
            else
            {
                var maskPaths = new Dictionary<int, GraphicsPath?>();
                var drawnObjects = new HashSet<int>();
                try
                {
                    foreach (var item in GetReference3DSceneRenderItems())
                    {
                        if (DrawReference3DSceneItem(graphics, item, maskPaths))
                        {
                            drawnObjects.Add(item.ObjectIndex);
                        }
                    }
                    drawn = drawnObjects.Count;
                }
                finally
                {
                    foreach (var path in maskPaths.Values) path?.Dispose();
                }
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
        }

        return new RenderStats(visible, drawn, atoms, 0, Scene.ObjectCount, false);
    }

    private bool DrawReference3DSceneItem(
        Graphics graphics,
        Reference3DRenderItem item,
        IDictionary<int, GraphicsPath?> maskPaths)
    {
        if ((uint)item.ObjectIndex >= Scene.ObjectCount
            || IsObjectHiddenForRendering(Scene, item.ObjectIndex))
        {
            return false;
        }

        if (!TryGetReference3DLayerMaskPath(item.LayerIndex, maskPaths, out var maskPath))
        {
            return false;
        }
        GraphicsPath? secondaryMaskPath = null;
        if (item.SecondaryObjectIndex >= 0
            && item.SecondaryObjectIndex != item.ObjectIndex)
        {
            if ((uint)item.SecondaryObjectIndex >= Scene.ObjectCount
                || IsObjectHiddenForRendering(Scene, item.SecondaryObjectIndex)
                || !TryGetReference3DLayerMaskPath(
                    Scene.ObjectLayer[item.SecondaryObjectIndex],
                    maskPaths,
                    out secondaryMaskPath))
            {
                return false;
            }
        }
        var fragmentClip = item.FragmentClip;
        if (maskPath is null && secondaryMaskPath is null && fragmentClip is null)
        {
            return DrawReference3DSceneItemUnmasked(graphics, item);
        }

        using var fragmentPath = fragmentClip is null
            ? null
            : CreateReference3DPath(fragmentClip, fillOnly: true);
        if (fragmentPath is { PointCount: 0 }) return false;

        var state = graphics.Save();
        try
        {
            if (maskPath is not null) graphics.SetClip(maskPath, CombineMode.Intersect);
            if (secondaryMaskPath is not null
                && !ReferenceEquals(secondaryMaskPath, maskPath))
            {
                graphics.SetClip(secondaryMaskPath, CombineMode.Intersect);
            }
            if (fragmentPath is not null) graphics.SetClip(fragmentPath, CombineMode.Intersect);
            return DrawReference3DSceneItemUnmasked(graphics, item);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private bool TryGetReference3DLayerMaskPath(
        int layer,
        IDictionary<int, GraphicsPath?> maskPaths,
        out GraphicsPath? maskPath)
    {
        maskPath = null;
        if (Scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            || !Scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            return true;
        }
        if (!Scene.IsLayerEffectivelyVisible(maskLayer)) return false;

        if (!maskPaths.TryGetValue(layer, out maskPath))
        {
            maskPath = CreateReference3DMaskPath(GetReference3DLayerObjects(maskLayer));
            maskPaths[layer] = maskPath;
        }
        return maskPath is { PointCount: > 0 };
    }

    private bool DrawReference3DSceneItemUnmasked(
        Graphics graphics,
        Reference3DRenderItem item)
    {
        switch (item.Kind)
        {
            case Reference3DRenderKind.FrontFill:
                DrawReference3DObject(graphics, item.ObjectIndex, SceneRenderPass.Fill);
                return true;
            case Reference3DRenderKind.FrontStroke:
                DrawReference3DObject(graphics, item.ObjectIndex, SceneRenderPass.Stroke);
                return true;
            case Reference3DRenderKind.Back:
            case Reference3DRenderKind.Side:
                DrawReference3DExtrusionSurface(graphics, item);
                return true;
            case Reference3DRenderKind.IntersectionEdge:
                return DrawReference3DIntersectionEdgeWithCompositionMasks(graphics, item);
            case Reference3DRenderKind.Outline:
                var objectDrawn = false;
                var outlineColor = Scene.GetEffectiveLayerOutlineColor(item.LayerIndex);
                DrawWithSceneCompositionMaskClips(
                    graphics,
                    item.ObjectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(
                        graphics,
                        item.ObjectIndex,
                        outlineColor),
                    reference3D: true);
                return objectDrawn;
            default:
                return false;
        }
    }

    private void DrawReference3DLayer(
        Graphics graphics,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var layerKind = Scene.GetLayerKind(layer);
        if (layerKind == DrawingLayerKind.Mask)
        {
            DrawReference3DLayerUnmasked(graphics, layer, objects, ref drawn);
            return;
        }

        if (!Scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawReference3DLayerUnmasked(graphics, layer, objects, ref drawn);
            return;
        }

        if (!Scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var maskPath = CreateReference3DMaskPath(GetReference3DLayerObjects(maskLayer));
        if (maskPath.PointCount == 0) return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(maskPath, CombineMode.Intersect);
            DrawReference3DLayerUnmasked(graphics, layer, objects, ref drawn);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawReference3DLayerUnmasked(
        Graphics graphics,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var outlineColor = Scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineColor.IsEmpty)
        {
            foreach (var objectIndex in objects)
            {
                var objectDrawn = false;
                DrawWithSceneCompositionMaskClips(
                    graphics,
                    objectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(graphics, objectIndex, outlineColor),
                    reference3D: true);
                if (objectDrawn) drawn++;
            }
            return;
        }

        foreach (var item in GetReference3DLayerRenderItems(objects))
        {
            switch (item.Kind)
            {
                case Reference3DRenderKind.FrontFill:
                    DrawReference3DObject(graphics, item.ObjectIndex, SceneRenderPass.Fill);
                    break;
                case Reference3DRenderKind.FrontStroke:
                    DrawReference3DObject(graphics, item.ObjectIndex, SceneRenderPass.Stroke);
                    break;
                default:
                    DrawReference3DExtrusionSurface(graphics, item);
                    break;
            }
        }
        foreach (var objectIndex in objects)
        {
            if (!IsObjectHiddenForRendering(Scene, objectIndex)) drawn++;
        }
    }

    private void DrawReference3DExtrusionSurface(
        Graphics graphics,
        Reference3DRenderItem item)
    {
        DrawWithSceneCompositionMaskClips(
            graphics,
            item.ObjectIndex,
            () =>
            {
                using var path = CreateReference3DPath(item.Contours, fillOnly: true);
                if (path.PointCount == 0) return;
                using var fill = new SolidBrush(GetReference3DExtrusionColor(item.ObjectIndex));
                FillPathAntialiased(graphics, path, fill);
            },
            reference3D: true);
    }

    private static bool DrawReference3DIntersectionEdge(
        Graphics graphics,
        Reference3DRenderItem item)
    {
        if (!float.IsFinite(item.EdgeWidth)
            || item.EdgeWidth <= 0
            || Color.FromArgb(item.EdgeArgb).A == 0)
        {
            return false;
        }
        using var path = CreateReference3DPath(item.Contours, fillOnly: false);
        if (path.PointCount == 0) return false;
        using var pen = new Pen(Color.FromArgb(item.EdgeArgb), item.EdgeWidth)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(pen, path);
        return true;
    }

    private bool DrawReference3DIntersectionEdgeWithCompositionMasks(
        Graphics graphics,
        Reference3DRenderItem item)
    {
        var edgeDrawn = false;
        void DrawEdge() => edgeDrawn = DrawReference3DIntersectionEdge(graphics, item);

        DrawWithSceneCompositionMaskClips(
            graphics,
            item.ObjectIndex,
            () =>
            {
                if (item.SecondaryObjectIndex >= 0
                    && item.SecondaryObjectIndex != item.ObjectIndex)
                {
                    DrawWithSceneCompositionMaskClips(
                        graphics,
                        item.SecondaryObjectIndex,
                        DrawEdge,
                        reference3D: true);
                    return;
                }
                DrawEdge();
            },
            reference3D: true);
        return edgeDrawn;
    }

    private void DrawReference3DObject(Graphics graphics, int objectIndex, SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            graphics,
            objectIndex,
            () => DrawReference3DObjectUnclipped(graphics, objectIndex, pass),
            reference3D: true);
    }

    private void DrawReference3DObjectUnclipped(Graphics graphics, int objectIndex, SceneRenderPass pass)
    {
        if (IsObjectHiddenForRendering(Scene, objectIndex)) return;
        var shape = Scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DImportedSvg(graphics, objectIndex);
            return;
        }
        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DMixingStroke(graphics, objectIndex);
            return;
        }
        if (Scene.HasGradient(objectIndex)
            && TryDrawReference3DFrontGradient(graphics, objectIndex, shape, pass))
        {
            return;
        }

        var contours = GetReference3DProjectedContours(objectIndex);
        if (contours.Length == 0) return;
        using var path = CreateReference3DPath(contours, fillOnly: pass == SceneRenderPass.Fill);
        if (path.PointCount == 0) return;
        if (pass == SceneRenderPass.Fill)
        {
            using var fill = new SolidBrush(Color.FromArgb(Scene.Argb[objectIndex]));
            FillPathAntialiased(graphics, path, fill);
            return;
        }

        var width = GetReference3DStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
        if (width <= 0) return;
        var color = Scene.StrokeArgb.Length > objectIndex
            ? Color.FromArgb(Scene.StrokeArgb[objectIndex])
            : Color.FromArgb(238, 242, 241);
        using var pen = StrokePen(color, width);
        graphics.DrawPath(pen, path);
    }

    private bool TryDrawReference3DFrontGradient(
        Graphics graphics,
        int objectIndex,
        ShapeKind shape,
        SceneRenderPass pass)
    {
        var gradientPass = pass == SceneRenderPass.Fill && shape != ShapeKind.Line
            || pass == SceneRenderPass.Stroke && shape == ShapeKind.Line;
        if (!gradientPass)
        {
            return false;
        }
        if (!TryGetReference3DFlatToScreenTransform(objectIndex, out var transform))
        {
            return TryDrawReference3DProjectiveFrontGradient(graphics, objectIndex, pass);
        }

        using var path = CreateReference3DSourcePath(
            GetReference3DSourceContours(objectIndex),
            fillOnly: pass == SceneRenderPass.Fill);
        if (path.PointCount == 0) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DPath(
                GetReference3DProjectedContours(objectIndex),
                fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask!.PointCount == 0) return false;

        using var matrix = new Matrix(
            transform.M11,
            transform.M12,
            transform.M21,
            transform.M22,
            transform.M31,
            transform.M32);
        var state = graphics.Save();
        try
        {
            if (screenMask is not null) graphics.SetClip(screenMask, CombineMode.Intersect);
            graphics.Transform = matrix;
            var stops = Scene.GetGradientStops(objectIndex);
            if (pass == SceneRenderPass.Stroke)
            {
                using var stroke = CreateReference3DSourceGradientBrush(objectIndex, stops);
                using var pen = new Pen(
                    stroke,
                    GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]))
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawPath(pen, path);
                return true;
            }

            if (Scene.GetGradientKind(objectIndex) == GradientKind.Linear
                && DrawReference3DSourcePathGradientFill(graphics, path, objectIndex, stops, transform))
            {
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.Radial)
            {
                DrawReference3DSourceRadialGradientFill(graphics, path, objectIndex, stops, transform);
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
                && DrawReference3DSourceShapeGradientFill(graphics, path, objectIndex, stops, transform))
            {
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial)
            {
                DrawReference3DSourceRadialGradientFill(graphics, path, objectIndex, stops, transform);
                return true;
            }

            using var fill = CreateReference3DSourceGradientBrush(objectIndex, stops);
            FillReference3DSourcePath(graphics, path, fill, transform);
            return true;
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private bool TryDrawReference3DProjectiveFrontGradient(
        Graphics graphics,
        int objectIndex,
        SceneRenderPass pass)
    {
        if (!TryGetReference3DProjectiveMesh(
                objectIndex,
                usePrimaryContourQuad: false,
                out var triangles)
            || triangles.Length == 0)
        {
            return false;
        }

        using var path = CreateReference3DSourcePath(
            GetReference3DSourceContours(objectIndex),
            fillOnly: pass == SceneRenderPass.Fill);
        if (path.PointCount == 0) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DPath(GetReference3DProjectedContours(objectIndex), fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask!.PointCount == 0) return false;

        var stops = Scene.GetGradientStops(objectIndex);
        GradientPathGradientSegment[] pathGradientSegments = [];
        var pathGradientWidth = 0f;
        var hasPathGradient = pass == SceneRenderPass.Fill
            && Scene.GetGradientKind(objectIndex) == GradientKind.Linear
            && TryPrepareReference3DSourcePathGradient(
                objectIndex,
                stops,
                out pathGradientSegments,
                out pathGradientWidth);
        GraphicsPath? mappingPath = null;
        Brush? gradient = null;
        try
        {
            if (Scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial)
            {
                mappingPath = new GraphicsPath(FillMode.Alternate);
                foreach (var contour in Scene.GetShapeGradientMappingContours(objectIndex))
                {
                    if (contour.Length >= 3) mappingPath.AddPolygon(contour);
                }
                if (mappingPath.PointCount == 0) return false;
                gradient = new PathGradientBrush(mappingPath)
                {
                    CenterPoint = Scene.GetGradientStart(objectIndex),
                    InterpolationColors = Reference3DColorBlend(stops)
                };
            }
            else
            {
                gradient = CreateReference3DSourceGradientBrush(objectIndex, stops);
            }

            var outerState = graphics.Save();
            try
            {
                graphics.ResetTransform();
                if (screenMask is not null) graphics.SetClip(screenMask, CombineMode.Intersect);
                var drewTriangle = false;
                foreach (var triangle in triangles)
                {
                    if (!TryGetReference3DFlatToScreenTransform(triangle, out var transform)) continue;
                    using var trianglePath = new GraphicsPath();
                    trianglePath.AddPolygon(
                    [
                        triangle.A.Screen,
                        triangle.B.Screen,
                        triangle.C.Screen
                    ]);
                    var triangleState = graphics.Save();
                    try
                    {
                        graphics.ResetTransform();
                        graphics.SetClip(trianglePath, CombineMode.Intersect);
                        using var matrix = new Matrix(
                            transform.M11,
                            transform.M12,
                            transform.M21,
                            transform.M22,
                            transform.M31,
                            transform.M32);
                        graphics.Transform = matrix;
                        if (pass == SceneRenderPass.Stroke)
                        {
                            using var pen = new Pen(
                                gradient,
                                GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]))
                            {
                                LineJoin = LineJoin.Round,
                                StartCap = LineCap.Round,
                                EndCap = LineCap.Round
                            };
                            graphics.DrawPath(pen, path);
                        }
                        else
                        {
                            if (hasPathGradient)
                            {
                                DrawReference3DSourcePathGradientSegments(
                                    graphics,
                                    pathGradientSegments,
                                    pathGradientWidth,
                                    triangle);
                            }
                            else
                            {
                                FillReference3DSourcePath(graphics, path, gradient, transform);
                            }
                        }
                        drewTriangle = true;
                    }
                    finally
                    {
                        graphics.Restore(triangleState);
                    }
                }
                return drewTriangle;
            }
            finally
            {
                graphics.Restore(outerState);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            gradient?.Dispose();
            mappingPath?.Dispose();
        }
    }

    private Brush CreateReference3DSourceGradientBrush(
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var start = Scene.GetGradientStart(objectIndex);
        var end = Scene.GetGradientEnd(objectIndex);
        var endColor = Color.FromArgb(stops[^1].Argb);
        if (Reference3DScreenDistance(start, end) <= 0.0001f) return new SolidBrush(endColor);

        if (Scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            try
            {
                var radius = Reference3DScreenDistance(start, end);
                using var circle = new GraphicsPath();
                circle.AddEllipse(start.X - radius, start.Y - radius, radius * 2f, radius * 2f);
                return new PathGradientBrush(circle)
                {
                    CenterPoint = start,
                    InterpolationColors = Reference3DColorBlend(stops)
                };
            }
            catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
            {
                return new SolidBrush(endColor);
            }
        }

        var brush = new LinearGradientBrush(
            start,
            end,
            Color.FromArgb(stops[0].Argb),
            endColor)
        {
            WrapMode = WrapMode.TileFlipXY,
            InterpolationColors = Reference3DColorBlend(stops)
        };
        return brush;
    }

    private bool DrawReference3DSourcePathGradientFill(
        Graphics graphics,
        GraphicsPath mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        System.Numerics.Matrix3x2 transform)
    {
        if (!TryPrepareReference3DSourcePathGradient(
                objectIndex,
                stops,
                out var segments,
                out var width))
        {
            return false;
        }

        var state = graphics.Save();
        try
        {
            graphics.SetClip(mask, CombineMode.Intersect);
            DrawReference3DSourcePathGradientSegments(graphics, segments, width);
        }
        finally
        {
            graphics.Restore(state);
        }

        using var edge = CreateReference3DSourceGradientBrush(objectIndex, stops);
        ReinforceReference3DSourcePathEdge(graphics, mask, edge, transform);
        return true;
    }

    private bool TryPrepareReference3DSourcePathGradient(
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        out GradientPathGradientSegment[] segments,
        out float width)
    {
        segments = [];
        width = 0;
        var estimatedWidth = Scene.EstimateGradientPathStrokeWidth(objectIndex);
        if (!Scene.TryGetGradientPathWorldPoints(objectIndex, out var pathPoints)
            || pathPoints.Length < 2
            || estimatedWidth is not > 0)
        {
            return false;
        }

        var screenLength = 0f;
        for (var index = 1; index < pathPoints.Length; index++)
        {
            if (TryProjectReference3DFrontPoint(objectIndex, pathPoints[index - 1], out var start, out _)
                && TryProjectReference3DFrontPoint(objectIndex, pathPoints[index], out var end, out _))
            {
                screenLength += Reference3DScreenDistance(start, end);
            }
        }
        segments = GradientPaintUtilities.CreatePathGradientSegments(
            pathPoints,
            stops,
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        width = Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f);
        return segments.Length > 0;
    }

    private static void DrawReference3DSourcePathGradientSegments(
        Graphics graphics,
        IReadOnlyList<GradientPathGradientSegment> segments,
        float width,
        Reference3DProjectiveTriangle? clipTriangle = null)
    {
        var clipLeft = float.NegativeInfinity;
        var clipTop = float.NegativeInfinity;
        var clipRight = float.PositiveInfinity;
        var clipBottom = float.PositiveInfinity;
        if (clipTriangle is { } triangle)
        {
            var padding = width * 0.5f;
            clipLeft = Math.Min(triangle.A.Flat.X, Math.Min(triangle.B.Flat.X, triangle.C.Flat.X)) - padding;
            clipTop = Math.Min(triangle.A.Flat.Y, Math.Min(triangle.B.Flat.Y, triangle.C.Flat.Y)) - padding;
            clipRight = Math.Max(triangle.A.Flat.X, Math.Max(triangle.B.Flat.X, triangle.C.Flat.X)) + padding;
            clipBottom = Math.Max(triangle.A.Flat.Y, Math.Max(triangle.B.Flat.Y, triangle.C.Flat.Y)) + padding;
        }
        foreach (var segment in segments)
        {
            if (Math.Max(segment.Start.X, segment.End.X) < clipLeft
                || Math.Min(segment.Start.X, segment.End.X) > clipRight
                || Math.Max(segment.Start.Y, segment.End.Y) < clipTop
                || Math.Min(segment.Start.Y, segment.End.Y) > clipBottom)
            {
                continue;
            }
            if (Reference3DScreenDistance(segment.Start, segment.End) <= 0.0001f) continue;
            using var brush = new LinearGradientBrush(
                segment.Start,
                segment.End,
                Color.FromArgb(segment.StartArgb),
                Color.FromArgb(segment.EndArgb))
            {
                WrapMode = WrapMode.TileFlipXY
            };
            using var pen = new Pen(brush, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLine(pen, segment.Start, segment.End);
        }
    }

    private void DrawReference3DSourceRadialGradientFill(
        Graphics graphics,
        GraphicsPath path,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        System.Numerics.Matrix3x2 transform)
    {
        var center = Scene.GetGradientStart(objectIndex);
        var radius = Math.Max(0.001f, Reference3DScreenDistance(center, Scene.GetGradientEnd(objectIndex)));
        using var outer = new SolidBrush(Color.FromArgb(stops[^1].Argb));
        graphics.FillPath(outer, path);
        var state = graphics.Save();
        try
        {
            graphics.SetClip(path, CombineMode.Intersect);
            const int rings = 64;
            for (var ring = rings - 1; ring >= 0; ring--)
            {
                var position = ring / (float)(rings - 1);
                using var fill = new SolidBrush(GradientColorAt(stops, position));
                var ringRadius = Math.Max(0.001f, radius * position);
                graphics.FillEllipse(
                    fill,
                    center.X - ringRadius,
                    center.Y - ringRadius,
                    ringRadius * 2f,
                    ringRadius * 2f);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
        ReinforceReference3DSourcePathEdge(graphics, path, outer, transform);
    }

    private bool DrawReference3DSourceShapeGradientFill(
        Graphics graphics,
        GraphicsPath path,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        System.Numerics.Matrix3x2 transform)
    {
        try
        {
            using var mappingPath = new GraphicsPath(FillMode.Alternate);
            foreach (var contour in Scene.GetShapeGradientMappingContours(objectIndex))
            {
                if (contour.Length >= 3) mappingPath.AddPolygon(contour);
            }
            if (mappingPath.PointCount == 0) return false;

            using var brush = new PathGradientBrush(mappingPath)
            {
                CenterPoint = Scene.GetGradientStart(objectIndex),
                InterpolationColors = Reference3DColorBlend(stops)
            };
            FillReference3DSourcePath(graphics, path, brush, transform);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            return false;
        }
    }

    private static void FillReference3DSourcePath(
        Graphics graphics,
        GraphicsPath path,
        Brush brush,
        System.Numerics.Matrix3x2 transform)
    {
        using var edge = new Pen(brush, Reference3DSourcePixelWidth(transform))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(edge, path);
        graphics.FillPath(brush, path);
    }

    private static void ReinforceReference3DSourcePathEdge(
        Graphics graphics,
        GraphicsPath path,
        Brush brush,
        System.Numerics.Matrix3x2 transform)
    {
        using var edge = new Pen(brush, Reference3DSourcePixelWidth(transform))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(edge, path);
    }

    private static float Reference3DSourcePixelWidth(System.Numerics.Matrix3x2 transform)
    {
        var scaleX = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = MathF.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        return 1f / Math.Max(0.0001f, Math.Max(scaleX, scaleY));
    }

    private static ColorBlend Reference3DColorBlend(IReadOnlyList<GradientStop> stops)
    {
        return new ColorBlend
        {
            Colors = stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
            Positions = stops.Select(stop => stop.Position).ToArray()
        };
    }

    private bool DrawReference3DObjectOutline(Graphics graphics, int objectIndex, Color layerColor)
    {
        if (IsObjectHiddenForRendering(Scene, objectIndex)) return false;
        var color = Reference3DOutlineColor(objectIndex, layerColor);
        if (color.A == 0) return false;
        var contours = GetReference3DProjectedSolid(objectIndex).SelectionEdges;
        if (contours.Length == 0) return false;
        using var path = CreateReference3DPath(contours, fillOnly: false);
        if (path.PointCount == 0) return false;
        using var pen = new Pen(color, 1f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(pen, path);
        return true;
    }

    private GraphicsPath CreateReference3DMaskPath(IReadOnlyList<int> maskObjects)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var objectIndex in maskObjects)
        {
            if (!SceneRenderOrder.HasFill(Scene.ShapeKind[objectIndex])
                || IsObjectHiddenForRendering(Scene, objectIndex))
            {
                continue;
            }
            AppendReference3DContours(path, GetReference3DProjectedContours(objectIndex), fillOnly: true);
        }
        return path;
    }

    private static GraphicsPath CreateReference3DPath(
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        AppendReference3DContours(path, contours, fillOnly);
        return path;
    }

    private static GraphicsPath CreateReference3DSourcePath(
        IReadOnlyList<Reference3DSourceContour> contours,
        bool fillOnly)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var contour in contours)
        {
            if (fillOnly && !contour.Closed) continue;
            if (contour.Closed && contour.Points.Length >= 3)
            {
                path.AddPolygon(contour.Points);
            }
            else if (!fillOnly && contour.Points.Length >= 2)
            {
                path.StartFigure();
                path.AddLines(contour.Points);
            }
        }
        return path;
    }

    private static void AppendReference3DContours(
        GraphicsPath path,
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        foreach (var contour in contours)
        {
            if (fillOnly && !contour.Closed) continue;
            if (contour.Closed && contour.Points.Length >= 3)
            {
                path.AddPolygon(contour.Points);
            }
            else if (!fillOnly && contour.Points.Length >= 2)
            {
                path.StartFigure();
                path.AddLines(contour.Points);
            }
        }
    }

    private void DrawReference3DImportedSvg(Graphics graphics, int objectIndex)
    {
        if (!Scene.TryGetImportedSvgSource(objectIndex, out var source)
            || string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        var contour = GetReference3DProjectedContours(objectIndex)
            .FirstOrDefault(item => item.Closed && item.Points.Length >= 3);
        if (contour.Points is not { Length: >= 3 }) return;

        var hasAffineTransform = TryGetReference3DFlatToScreenTransform(objectIndex, out _);
        Reference3DProjectiveTriangle[] triangles = [];
        var hasProjectiveMesh = TryGetReference3DProjectiveMesh(
            objectIndex,
            usePrimaryContourQuad: true,
            out triangles)
            && triangles.Length > 0;
        var projective = hasProjectiveMesh
            && (!hasAffineTransform || !Reference3DProjectiveMeshCoversFullDomain(triangles));

        PointF topLeft = default;
        PointF topRight = default;
        PointF bottomLeft = default;
        SizeF rasterSize;
        if (projective)
        {
            rasterSize = EstimateReference3DProjectiveTextureSize(triangles);
        }
        else
        {
            if (!hasAffineTransform || contour.Points.Length < 4) return;
            topLeft = contour.Points[0];
            topRight = contour.Points[1];
            bottomLeft = contour.Points[^1];
            rasterSize = new SizeF(
                Math.Max(1f, Reference3DScreenDistance(topLeft, topRight)),
                Math.Max(1f, Reference3DScreenDistance(topLeft, bottomLeft)));
        }
        var raster = ImportedSvgRasterizer.Rasterize(source, rasterSize.Width, rasterSize.Height);
        var opacity = Color.FromArgb(Scene.Argb[objectIndex]).A / 255f;
        if (projective)
        {
            TryDrawReference3DProjectiveImportedSvg(
                graphics,
                objectIndex,
                raster,
                triangles,
                opacity);
            return;
        }

        var destination = new[] { topLeft, topRight, bottomLeft };
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(
                    raster.Bitmap,
                    destination,
                    new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                    GraphicsUnit.Pixel);
                return;
            }

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(
                new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) },
                ColorMatrixFlag.Default,
                ColorAdjustType.Bitmap);
            graphics.DrawImage(
                raster.Bitmap,
                destination,
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private bool TryDrawReference3DProjectiveImportedSvg(
        Graphics graphics,
        int objectIndex,
        ImportedSvgRaster raster,
        IReadOnlyList<Reference3DProjectiveTriangle> triangles,
        float opacity)
    {
        using var screenMask = CreateReference3DPath(
            GetReference3DProjectedContours(objectIndex),
            fillOnly: true);
        if (screenMask.PointCount == 0) return false;

        using var attributes = opacity < 0.999f ? new ImageAttributes() : null;
        attributes?.SetColorMatrix(
            new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) },
            ColorMatrixFlag.Default,
            ColorAdjustType.Bitmap);
        var outerState = graphics.Save();
        try
        {
            graphics.ResetTransform();
            graphics.SetClip(screenMask, CombineMode.Intersect);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var drewTriangle = false;
            foreach (var triangle in triangles)
            {
                if (!TryGetReference3DTextureToScreenTransform(
                        triangle,
                        raster.PixelWidth,
                        raster.PixelHeight,
                        out var transform))
                {
                    continue;
                }

                using var trianglePath = new GraphicsPath();
                trianglePath.AddPolygon(
                [
                    triangle.A.Screen,
                    triangle.B.Screen,
                    triangle.C.Screen
                ]);
                var triangleState = graphics.Save();
                try
                {
                    graphics.ResetTransform();
                    graphics.SetClip(trianglePath, CombineMode.Intersect);
                    using var matrix = new Matrix(
                        transform.M11,
                        transform.M12,
                        transform.M21,
                        transform.M22,
                        transform.M31,
                        transform.M32);
                    graphics.Transform = matrix;
                    var textureBounds = GetReference3DProjectiveTextureBounds(
                        triangle,
                        raster.PixelWidth,
                        raster.PixelHeight);
                    if (textureBounds.Width <= 0 || textureBounds.Height <= 0) continue;
                    graphics.DrawImage(
                        raster.Bitmap,
                        textureBounds,
                        textureBounds.X,
                        textureBounds.Y,
                        textureBounds.Width,
                        textureBounds.Height,
                        GraphicsUnit.Pixel,
                        attributes);
                    drewTriangle = true;
                }
                finally
                {
                    graphics.Restore(triangleState);
                }
            }
            return drewTriangle;
        }
        finally
        {
            graphics.Restore(outerState);
        }
    }

    private void DrawReference3DMixingStroke(Graphics graphics, int objectIndex)
    {
        if (!Scene.TryGetMixingBrushWorldRegion(objectIndex, out var region))
        {
            var contours = GetReference3DProjectedContours(objectIndex);
            using var path = CreateReference3DPath(contours, fillOnly: true);
            if (path.PointCount == 0) return;
            using var fallback = new SolidBrush(Color.FromArgb(Scene.Argb[objectIndex]));
            FillPathAntialiased(graphics, path, fallback);
            return;
        }

        var vertices = new PointF[region.Vertices.Length];
        var visible = new bool[region.Vertices.Length];
        for (var index = 0; index < region.Vertices.Length; index++)
        {
            visible[index] = TryProjectReference3DFrontPoint(
                objectIndex,
                region.Vertices[index].Point,
                out vertices[index],
                out _);
        }
        for (var triangle = 0; triangle + 2 < region.TriangleIndices.Length; triangle += 3)
        {
            var a = region.TriangleIndices[triangle];
            var b = region.TriangleIndices[triangle + 1];
            var c = region.TriangleIndices[triangle + 2];
            if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length
                || !visible[a] || !visible[b] || !visible[c])
            {
                continue;
            }
            using var fill = new SolidBrush(AverageColor(
                region.Vertices[a].Argb,
                region.Vertices[b].Argb,
                region.Vertices[c].Argb));
            graphics.FillPolygon(fill, [vertices[a], vertices[b], vertices[c]]);
        }
    }

    private void DrawReference3DSelection(Graphics graphics)
    {
        if (_reference3DSelectedObjects.Length == 0) return;
        using var glow = new Pen(Reference3DSelectionHaloColor, 5.5f)
        {
            DashStyle = DashStyle.Solid,
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var outline = new Pen(Reference3DSelectionLineColor, 2f)
        {
            DashStyle = DashStyle.Solid,
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        foreach (var objectIndex in _reference3DSelectedObjects)
        {
            var contours = GetReference3DProjectedSolid(objectIndex).SelectionEdges;
            using var path = CreateReference3DPath(contours, fillOnly: false);
            if (path.PointCount == 0) continue;
            DrawReference3DSelectionWithMasks(graphics, objectIndex, () =>
            {
                graphics.DrawPath(glow, path);
                graphics.DrawPath(outline, path);
            });
        }
    }

    private void DrawReference3DSelectionWithMasks(
        Graphics graphics,
        int objectIndex,
        Action draw)
    {
        var state = graphics.Save();
        try
        {
            var layer = Scene.ObjectLayer[objectIndex];
            if (Scene.GetLayerKind(layer) != DrawingLayerKind.Mask
                && Scene.TryGetMaskLayerIndex(layer, out var maskLayer))
            {
                if (!Scene.IsLayerEffectivelyVisible(maskLayer)) return;
                using var maskPath = CreateReference3DMaskPath(GetReference3DLayerObjects(maskLayer));
                if (maskPath.PointCount == 0) return;
                graphics.SetClip(maskPath, CombineMode.Intersect);
            }

            DrawWithSceneCompositionMaskClips(
                graphics,
                objectIndex,
                draw,
                reference3D: true);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawSpatialTransformGizmoGdi(Graphics graphics)
    {
        if (!TryGetSpatialGizmoScreenGeometry(out var geometry)) return;
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (_spatialTransformGizmoMode == SpatialTransformMode.Rotate)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    var ring = geometry.Rings[axis];
                    if (ring.Length < 3) continue;
                    using var pen = new Pen(SpatialAxisColor(axis), 2.2f)
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    graphics.DrawPolygon(pen, ring);
                }
                return;
            }

            if (_spatialTransformGizmoMode == SpatialTransformMode.Move)
            {
                for (var plane = 0; plane < geometry.PlaneHandles.Length; plane++)
                {
                    var polygon = geometry.PlaneHandles[plane];
                    if (polygon.Length != 4) continue;
                    var color = SpatialPlaneColor(plane);
                    using var fill = new SolidBrush(Color.FromArgb(68, color));
                    using var outline = new Pen(Color.FromArgb(205, color), 1.3f)
                    {
                        LineJoin = LineJoin.Round
                    };
                    graphics.FillPolygon(fill, polygon);
                    graphics.DrawPolygon(outline, polygon);
                }
            }

            for (var axis = 0; axis < 3; axis++)
            {
                var color = SpatialAxisColor(axis);
                using var pen = new Pen(color, 2.4f)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(pen, geometry.Origin, geometry.Endpoints[axis]);
                if (_spatialTransformGizmoMode == SpatialTransformMode.Scale)
                {
                    using var fill = new SolidBrush(color);
                    var endpoint = geometry.Endpoints[axis];
                    graphics.FillRectangle(fill, endpoint.X - 4, endpoint.Y - 4, 8, 8);
                }
                else
                {
                    DrawSpatialArrowHead(graphics, geometry.Origin, geometry.Endpoints[axis], color);
                }
            }

            if (_spatialTransformGizmoMode == SpatialTransformMode.Scale)
            {
                using var center = new SolidBrush(Color.FromArgb(238, 235, 241, 242));
                graphics.FillRectangle(center, geometry.Origin.X - 4, geometry.Origin.Y - 4, 8, 8);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
        }
    }

    private static void DrawSpatialArrowHead(Graphics graphics, PointF origin, PointF endpoint, Color color)
    {
        var dx = endpoint.X - origin.X;
        var dy = endpoint.Y - origin.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f) return;
        dx /= length;
        dy /= length;
        var perpendicularX = -dy;
        var perpendicularY = dx;
        var basePoint = new PointF(endpoint.X - dx * 10f, endpoint.Y - dy * 10f);
        using var fill = new SolidBrush(color);
        graphics.FillPolygon(fill,
        [
            endpoint,
            new PointF(basePoint.X + perpendicularX * 4.5f, basePoint.Y + perpendicularY * 4.5f),
            new PointF(basePoint.X - perpendicularX * 4.5f, basePoint.Y - perpendicularY * 4.5f)
        ]);
    }

    private Color Reference3DOutlineColor(int objectIndex, Color layerColor)
    {
        if (layerColor.IsEmpty) return Color.Empty;
        var shape = Scene.ShapeKind[objectIndex];
        var fillAlpha = SceneRenderOrder.HasFill(shape) ? Color.FromArgb(Scene.Argb[objectIndex]).A : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, Scene.Stroke[objectIndex])
            ? Color.FromArgb(Scene.StrokeArgb[objectIndex]).A
            : 0;
        var alpha = (layerColor.A * Math.Max(fillAlpha, strokeAlpha) + 127) / 255;
        return Color.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private static Color AverageColor(int first, int second, int third)
    {
        var a = Color.FromArgb(first);
        var b = Color.FromArgb(second);
        var c = Color.FromArgb(third);
        return Color.FromArgb(
            (a.A + b.A + c.A) / 3,
            (a.R + b.R + c.R) / 3,
            (a.G + b.G + c.G) / 3,
            (a.B + b.B + c.B) / 3);
    }

    private static Color SpatialAxisColor(int axis) => axis switch
    {
        0 => Color.FromArgb(245, 235, 82, 82),
        1 => Color.FromArgb(245, 82, 205, 122),
        _ => Color.FromArgb(245, 82, 155, 245)
    };

    private static Color SpatialPlaneColor(int plane) => SpatialAxisColor(plane switch
    {
        0 => 2,
        1 => 1,
        _ => 0
    });

    private static float Reference3DScreenDistance(PointF left, PointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void DrawWithSceneCompositionMaskClips(
        Graphics graphics,
        int objectIndex,
        Action draw,
        bool reference3D)
    {
        var clips = GetSceneCompositionMaskClips(objectIndex);
        if (clips.Count == 0)
        {
            draw();
            return;
        }

        var state = graphics.Save();
        try
        {
            foreach (var clip in clips)
            {
                using var path = reference3D
                    ? CreateReference3DPath(GetReference3DProjectedMaskContours(clip), fillOnly: true)
                    : CreateSceneCompositionMaskPath2D(clip);
                if (path.PointCount == 0) return;
                graphics.SetClip(path, CombineMode.Intersect);
            }
            draw();
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private GraphicsPath CreateSceneCompositionMaskPath2D(SceneCompositionMaskClip clip)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        AppendPolygonContours(path, GetSceneCompositionMaskWorldContours(clip));
        return path;
    }
}
