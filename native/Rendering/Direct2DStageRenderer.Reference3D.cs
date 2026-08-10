using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using D2DRect = Vortice.Mathematics.Rect;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;
using GdiSizeF = System.Drawing.SizeF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private RenderStats DrawReference3DScene(StageControl stage)
    {
        var editableScene = stage.Scene;
        var onionSkinStats = default(RenderStats);
        var underlayStats = default(RenderStats);
        try
        {
            if (stage.OnionSkinScene is { } onionSkin)
            {
                stage.Scene = onionSkin;
                stage.RecordOnionSkinScenePass();
                onionSkinStats = DrawReference3DCurrentScene(stage);
            }

            if (stage.UnderlayScene is { } underlay)
            {
                stage.Scene = underlay;
                stage.RecordUnderlayScenePass();
                underlayStats = DrawReference3DCurrentScene(stage);
            }

            stage.Scene = editableScene;
            stage.RecordEditableScenePass();
            var editableStats = DrawReference3DCurrentScene(stage);
            return RenderStats.Combine(
                RenderStats.Combine(onionSkinStats, underlayStats),
                editableStats);
        }
        finally
        {
            stage.Scene = editableScene;
        }
    }

    private RenderStats DrawReference3DCurrentScene(StageControl stage)
    {
        var scene = stage.Scene;
        var visible = 0;
        long atoms = 0;
        for (var layer = scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!scene.ShouldRenderLayerContent(layer)) continue;
            var objects = stage.GetReference3DLayerObjects(layer);
            if (objects.Length == 0) continue;
            visible += objects.Length;
            foreach (var objectIndex in objects) atoms += scene.AtomCount[objectIndex];
        }

        var maskGeometries = new Dictionary<int, ID2D1PathGeometry?>();
        var drawnObjects = new HashSet<int>();
        var renderItems = stage.GetReference3DSceneRenderItems();
        using var maskTargetLayer = _target!.CreateLayer();
        using var secondaryMaskTargetLayer = renderItems.Any(item =>
                item.SecondaryObjectIndex >= 0
                && item.SecondaryObjectIndex != item.ObjectIndex)
            ? _target.CreateLayer()
            : null;
        using var fragmentTargetLayer = renderItems.Any(item => item.FragmentClip is not null)
            ? _target.CreateLayer()
            : null;
        try
        {
            foreach (var item in renderItems)
            {
                if (DrawReference3DSceneItem(
                        stage,
                        item,
                        maskGeometries,
                        maskTargetLayer,
                        secondaryMaskTargetLayer,
                        fragmentTargetLayer))
                {
                    drawnObjects.Add(item.ObjectIndex);
                }
            }
        }
        finally
        {
            foreach (var geometry in maskGeometries.Values) geometry?.Dispose();
        }
        var drawn = drawnObjects.Count;
        return new RenderStats(visible, drawn, atoms, 0, scene.ObjectCount, false);
    }

    private bool DrawReference3DSceneItem(
        StageControl stage,
        Reference3DRenderItem item,
        IDictionary<int, ID2D1PathGeometry?> maskGeometries,
        ID2D1Layer maskTargetLayer,
        ID2D1Layer? secondaryMaskTargetLayer,
        ID2D1Layer? fragmentTargetLayer)
    {
        var scene = stage.Scene;
        if ((uint)item.ObjectIndex >= scene.ObjectCount
            || stage.IsObjectHiddenForRendering(scene, item.ObjectIndex))
        {
            return false;
        }

        if (!TryGetReference3DLayerMaskGeometry(
                stage,
                item.LayerIndex,
                maskGeometries,
                out var maskGeometry))
        {
            return false;
        }
        ID2D1PathGeometry? secondaryMaskGeometry = null;
        if (item.SecondaryObjectIndex >= 0
            && item.SecondaryObjectIndex != item.ObjectIndex)
        {
            if ((uint)item.SecondaryObjectIndex >= scene.ObjectCount
                || stage.IsObjectHiddenForRendering(scene, item.SecondaryObjectIndex)
                || !TryGetReference3DLayerMaskGeometry(
                    stage,
                    scene.ObjectLayer[item.SecondaryObjectIndex],
                    maskGeometries,
                    out secondaryMaskGeometry))
            {
                return false;
            }
            if (ReferenceEquals(secondaryMaskGeometry, maskGeometry)) secondaryMaskGeometry = null;
        }
        var fragmentClip = item.FragmentClip;
        if (maskGeometry is null && secondaryMaskGeometry is null && fragmentClip is null)
        {
            return DrawReference3DSceneItemUnmasked(stage, item);
        }

        using var fragmentGeometry = fragmentClip is null
            ? null
            : CreateReference3DGeometry(fragmentClip, fillOnly: true);
        if (fragmentClip is not null && fragmentGeometry is null) return false;
        return DrawReference3DSceneItemWithGeometricClip(
            maskGeometry,
            maskTargetLayer,
            () => DrawReference3DSceneItemWithGeometricClip(
                secondaryMaskGeometry,
                secondaryMaskTargetLayer,
                () => DrawReference3DSceneItemWithGeometricClip(
                    fragmentGeometry,
                    fragmentTargetLayer,
                    () => DrawReference3DSceneItemUnmasked(stage, item),
                    AntialiasMode.Aliased)));
    }

    private bool DrawReference3DSceneItemWithGeometricClip(
        ID2D1PathGeometry? geometry,
        ID2D1Layer? targetLayer,
        Func<bool> draw,
        AntialiasMode maskAntialiasMode = AntialiasMode.PerPrimitive)
    {
        if (geometry is null) return draw();
        if (targetLayer is null) return false;

        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = geometry,
            MaskAntialiasMode = maskAntialiasMode,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target!.PushLayer(parameters, targetLayer);
        try
        {
            return draw();
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private bool TryGetReference3DLayerMaskGeometry(
        StageControl stage,
        int layer,
        IDictionary<int, ID2D1PathGeometry?> maskGeometries,
        out ID2D1PathGeometry? maskGeometry)
    {
        var scene = stage.Scene;
        maskGeometry = null;
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            || !scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            return true;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return false;

        if (!maskGeometries.TryGetValue(layer, out maskGeometry))
        {
            maskGeometry = CreateReference3DMaskGeometry(
                stage,
                stage.GetReference3DLayerObjects(maskLayer));
            maskGeometries[layer] = maskGeometry;
        }
        return maskGeometry is not null;
    }

    private bool DrawReference3DSceneItemUnmasked(
        StageControl stage,
        Reference3DRenderItem item)
    {
        switch (item.Kind)
        {
            case Reference3DRenderKind.FrontFill:
                DrawReference3DObject(stage, item.ObjectIndex, SceneRenderPass.Fill);
                return true;
            case Reference3DRenderKind.FrontStroke:
                DrawReference3DObject(stage, item.ObjectIndex, SceneRenderPass.Stroke);
                return true;
            case Reference3DRenderKind.Back:
            case Reference3DRenderKind.Side:
                DrawReference3DExtrusionSurface(stage, item);
                return true;
            case Reference3DRenderKind.IntersectionEdge:
                return DrawReference3DIntersectionEdgeWithCompositionMasks(stage, item);
            case Reference3DRenderKind.Outline:
                var objectDrawn = false;
                var outlineColor = stage.Scene.GetEffectiveLayerOutlineColor(item.LayerIndex);
                DrawWithSceneCompositionMaskClips(
                    stage,
                    item.ObjectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(
                        stage,
                        item.ObjectIndex,
                        outlineColor),
                    reference3D: true);
                return objectDrawn;
            default:
                return false;
        }
    }

    private void DrawReference3DLayer(
        StageControl stage,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var scene = stage.Scene;
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask)
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
            return;
        }
        if (!scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
            return;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var mask = CreateReference3DMaskGeometry(stage, stage.GetReference3DLayerObjects(maskLayer));
        if (mask is null) return;
        using var targetLayer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target.PushLayer(parameters, targetLayer);
        try
        {
            DrawReference3DLayerUnmasked(stage, layer, objects, ref drawn);
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private void DrawReference3DLayerUnmasked(
        StageControl stage,
        int layer,
        IReadOnlyList<int> objects,
        ref int drawn)
    {
        var scene = stage.Scene;
        var outlineColor = scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineColor.IsEmpty)
        {
            foreach (var objectIndex in objects)
            {
                var objectDrawn = false;
                DrawWithSceneCompositionMaskClips(
                    stage,
                    objectIndex,
                    () => objectDrawn = DrawReference3DObjectOutline(stage, objectIndex, outlineColor),
                    reference3D: true);
                if (objectDrawn) drawn++;
            }
            return;
        }
        foreach (var item in stage.GetReference3DLayerRenderItems(objects))
        {
            switch (item.Kind)
            {
                case Reference3DRenderKind.FrontFill:
                    DrawReference3DObject(stage, item.ObjectIndex, SceneRenderPass.Fill);
                    break;
                case Reference3DRenderKind.FrontStroke:
                    DrawReference3DObject(stage, item.ObjectIndex, SceneRenderPass.Stroke);
                    break;
                default:
                    DrawReference3DExtrusionSurface(stage, item);
                    break;
            }
        }
        foreach (var objectIndex in objects)
        {
            if (!stage.IsObjectHiddenForRendering(scene, objectIndex)) drawn++;
        }
    }

    private void DrawReference3DExtrusionSurface(
        StageControl stage,
        Reference3DRenderItem item)
    {
        DrawWithSceneCompositionMaskClips(
            stage,
            item.ObjectIndex,
            () =>
            {
                using var geometry = CreateReference3DGeometry(item.Contours, fillOnly: true);
                if (geometry is null) return;
                _target!.FillGeometry(
                    geometry,
                    BrushFor(stage.GetReference3DExtrusionColor(item.ObjectIndex).ToArgb()));
            },
            reference3D: true);
    }

    private bool DrawReference3DIntersectionEdge(Reference3DRenderItem item)
    {
        if (!float.IsFinite(item.EdgeWidth)
            || item.EdgeWidth <= 0
            || GdiColor.FromArgb(item.EdgeArgb).A == 0)
        {
            return false;
        }
        using var geometry = CreateReference3DGeometry(item.Contours, fillOnly: false);
        if (geometry is null) return false;
        _target!.DrawGeometry(
            geometry,
            BrushFor(item.EdgeArgb),
            item.EdgeWidth,
            RoundStrokeStyle());
        return true;
    }

    private bool DrawReference3DIntersectionEdgeWithCompositionMasks(
        StageControl stage,
        Reference3DRenderItem item)
    {
        var edgeDrawn = false;
        void DrawEdge() => edgeDrawn = DrawReference3DIntersectionEdge(item);

        DrawWithSceneCompositionMaskClips(
            stage,
            item.ObjectIndex,
            () =>
            {
                if (item.SecondaryObjectIndex >= 0
                    && item.SecondaryObjectIndex != item.ObjectIndex)
                {
                    DrawWithSceneCompositionMaskClips(
                        stage,
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

    private void DrawReference3DObject(StageControl stage, int objectIndex, SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            stage,
            objectIndex,
            () => DrawReference3DObjectUnclipped(stage, objectIndex, pass),
            reference3D: true);
    }

    private void DrawReference3DObjectUnclipped(StageControl stage, int objectIndex, SceneRenderPass pass)
    {
        var scene = stage.Scene;
        if (stage.IsObjectHiddenForRendering(scene, objectIndex)) return;
        var shape = scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DImportedSvg(stage, objectIndex);
            return;
        }
        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill) DrawReference3DMixingStroke(stage, objectIndex);
            return;
        }
        if (scene.HasGradient(objectIndex)
            && TryDrawReference3DFrontGradient(stage, objectIndex, shape, pass))
        {
            return;
        }

        var contours = stage.GetReference3DProjectedContours(objectIndex);
        using var geometry = CreateReference3DGeometry(contours, fillOnly: pass == SceneRenderPass.Fill);
        if (geometry is null) return;
        if (pass == SceneRenderPass.Fill)
        {
            _target!.FillGeometry(geometry, BrushFor(scene.Argb[objectIndex]));
            return;
        }
        var width = stage.GetReference3DStrokeWidth(objectIndex, scene.Stroke[objectIndex]);
        if (width <= 0) return;
        var color = scene.StrokeArgb.Length > objectIndex
            ? scene.StrokeArgb[objectIndex]
            : GdiColor.FromArgb(238, 242, 241).ToArgb();
        _target!.DrawGeometry(geometry, BrushFor(color), width, RoundStrokeStyle());
    }

    private bool TryDrawReference3DFrontGradient(
        StageControl stage,
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
        if (!stage.TryGetReference3DFlatToScreenTransform(objectIndex, out var transform))
        {
            return TryDrawReference3DProjectiveFrontGradient(stage, objectIndex, pass);
        }

        using var geometry = CreateReference3DSourceGeometry(
            stage.GetReference3DSourceContours(objectIndex),
            fillOnly: pass == SceneRenderPass.Fill);
        if (geometry is null) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask is null) return false;

        var scene = stage.Scene;
        var stops = scene.GetGradientStops(objectIndex);
        if (pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.Linear
            && DrawReference3DSourcePathGradientFill(
                stage,
                screenMask!,
                objectIndex,
                stops,
                transform))
        {
            return true;
        }
        if (pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            && DrawReference3DSourceShapeGradientFill(
                stage,
                screenMask!,
                objectIndex,
                stops,
                transform))
        {
            return true;
        }

        var gradientKind = scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientKind.Radial
            : scene.GetGradientKind(objectIndex);
        var cached = GradientBrush(scene, objectIndex, gradientKind, stops);
        cached.SetAxis(
            ToVector(scene.GetGradientStart(objectIndex)),
            ToVector(scene.GetGradientEnd(objectIndex)));
        var old = _target!.Transform;
        try
        {
            if (pass == SceneRenderPass.Fill)
            {
                var parameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                using var layer = _target.CreateLayer();
                _target.Transform = Matrix3x2.Identity;
                _target.PushLayer(parameters, layer);
                try
                {
                    _target.Transform = transform;
                    FillAntialiasedGeometry(
                        geometry,
                        cached.Brush,
                        Reference3DSourcePixelWidth(transform));
                }
                finally
                {
                    _target.Transform = Matrix3x2.Identity;
                    _target.PopLayer();
                }
            }
            else
            {
                _target.Transform = transform;
                _target.DrawGeometry(
                    geometry,
                    cached.Brush,
                    stage.GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex]),
                    RoundStrokeStyle());
            }
        }
        finally
        {
            _target.Transform = old;
        }
        return true;
    }

    private bool TryDrawReference3DProjectiveFrontGradient(
        StageControl stage,
        int objectIndex,
        SceneRenderPass pass)
    {
        if (!stage.TryGetReference3DProjectiveMesh(
                objectIndex,
                usePrimaryContourQuad: false,
                out var triangles)
            || triangles.Length == 0)
        {
            return false;
        }

        using var geometry = CreateReference3DSourceGeometry(
            stage.GetReference3DSourceContours(objectIndex),
            fillOnly: pass == SceneRenderPass.Fill);
        if (geometry is null) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask is null) return false;

        var scene = stage.Scene;
        var stops = scene.GetGradientStops(objectIndex);
        GradientPathGradientSegment[] pathSegments = [];
        CachedPathGradientBrushes? pathBrushes = null;
        var pathWidth = 0f;
        if (pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.Linear)
        {
            var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
            if (scene.TryGetGradientPathWorldPoints(objectIndex, out var path)
                && path.Length >= 2
                && estimatedWidth is > 0)
            {
                var screenLength = 0f;
                for (var index = 1; index < path.Length; index++)
                {
                    if (stage.TryProjectReference3DFrontPoint(objectIndex, path[index - 1], out var start, out _)
                        && stage.TryProjectReference3DFrontPoint(objectIndex, path[index], out var end, out _))
                    {
                        screenLength += Reference3DDistance(start, end);
                    }
                }
                pathSegments = GradientPaintUtilities.CreatePathGradientSegments(
                    path,
                    stops,
                    Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
                if (pathSegments.Length > 0)
                {
                    pathBrushes = PathGradientBrushes(scene, objectIndex, stops, pathSegments);
                    pathWidth = Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f);
                }
            }
        }

        var shapeGradient = pass == SceneRenderPass.Fill
            && scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? ShapeGradientBitmap(stage, scene, objectIndex, stops)
            : null;
        var gradientKind = scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
            ? GradientKind.Radial
            : scene.GetGradientKind(objectIndex);
        var cached = GradientBrush(scene, objectIndex, gradientKind, stops);
        cached.SetAxis(
            ToVector(scene.GetGradientStart(objectIndex)),
            ToVector(scene.GetGradientEnd(objectIndex)));

        var drewMaterial = DrawReference3DProjectiveTriangles(
            triangles,
            screenMask,
            triangle =>
            {
                if (!StageControl.TryGetReference3DFlatToScreenTransform(triangle, out var transform))
                {
                    return false;
                }

                _target!.Transform = transform;
                if (pathBrushes is not null)
                {
                    DrawReference3DSourcePathGradientSegments(
                        pathSegments,
                        pathBrushes,
                        pathWidth,
                        triangle);
                    return true;
                }
                if (shapeGradient is not null)
                {
                    var destination = Rect(
                        shapeGradient.WorldBounds.Left,
                        shapeGradient.WorldBounds.Top,
                        shapeGradient.WorldBounds.Width,
                        shapeGradient.WorldBounds.Height);
                    var source = Rect(0, 0, shapeGradient.PixelWidth, shapeGradient.PixelHeight);
                    _target.DrawBitmap(
                        shapeGradient.Bitmap,
                        destination,
                        1f,
                        BitmapInterpolationMode.Linear,
                        source);
                    return true;
                }
                if (pass == SceneRenderPass.Fill)
                {
                    FillAntialiasedGeometry(
                        geometry,
                        cached.Brush,
                        Reference3DSourcePixelWidth(transform));
                }
                else
                {
                    _target.DrawGeometry(
                        geometry,
                        cached.Brush,
                        stage.GetReference3DSourceStrokeWidth(objectIndex, scene.Stroke[objectIndex]),
                        RoundStrokeStyle());
                }
                return true;
            });
        if (drewMaterial
            && screenMask is not null
            && (pathBrushes is not null || shapeGradient is not null))
        {
            var old = _target!.Transform;
            try
            {
                _target.Transform = Matrix3x2.Identity;
                _target.DrawGeometry(
                    screenMask,
                    BrushFor(stops[^1].Argb),
                    FillEdgeCoverageWidthPixels);
            }
            finally
            {
                _target.Transform = old;
            }
        }
        return drewMaterial;
    }

    private void DrawReference3DSourcePathGradientSegments(
        IReadOnlyList<GradientPathGradientSegment> segments,
        CachedPathGradientBrushes cachedBrushes,
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
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (Math.Max(segment.Start.X, segment.End.X) < clipLeft
                || Math.Min(segment.Start.X, segment.End.X) > clipRight
                || Math.Max(segment.Start.Y, segment.End.Y) < clipTop
                || Math.Min(segment.Start.Y, segment.End.Y) > clipBottom)
            {
                continue;
            }
            var start = ToVector(segment.Start);
            var end = ToVector(segment.End);
            var direction = end - start;
            var segmentLength = direction.Length();
            var positionSpan = segment.EndPosition - segment.StartPosition;
            if (segmentLength <= 0.001f || positionSpan <= 0.0001f) continue;

            direction /= segmentLength;
            var gradientAxisLength = segmentLength / positionSpan;
            var gradientAxisStart = start - direction * (gradientAxisLength * segment.StartPosition);
            var brush = cachedBrushes.Brushes[index];
            brush.StartPoint = gradientAxisStart;
            brush.EndPoint = gradientAxisStart + direction * gradientAxisLength;
            _target!.DrawLine(start, end, brush, width, RoundStrokeStyle());
        }
    }

    private bool DrawReference3DProjectiveTriangles(
        IReadOnlyList<Reference3DProjectiveTriangle> triangles,
        ID2D1Geometry? screenMask,
        Func<Reference3DProjectiveTriangle, bool> drawTriangle)
    {
        using var outerLayer = screenMask is null ? null : _target!.CreateLayer();
        using var triangleLayer = _target!.CreateLayer();
        var old = _target.Transform;
        var outerPushed = false;
        try
        {
            _target.Transform = Matrix3x2.Identity;
            if (screenMask is not null)
            {
                var outerParameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = screenMask,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.PushLayer(outerParameters, outerLayer!);
                outerPushed = true;
            }

            var drewTriangle = false;
            foreach (var triangle in triangles)
            {
                using var triangleGeometry = CreateReference3DTriangle(
                    triangle.A.Screen,
                    triangle.B.Screen,
                    triangle.C.Screen);
                var triangleParameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = triangleGeometry,
                    MaskAntialiasMode = AntialiasMode.Aliased,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.Transform = Matrix3x2.Identity;
                _target.PushLayer(triangleParameters, triangleLayer);
                try
                {
                    drewTriangle |= drawTriangle(triangle);
                }
                finally
                {
                    _target.Transform = Matrix3x2.Identity;
                    _target.PopLayer();
                }
            }
            return drewTriangle;
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            if (outerPushed) _target.PopLayer();
            _target.Transform = old;
        }
    }

    private bool DrawReference3DSourcePathGradientFill(
        StageControl stage,
        ID2D1Geometry mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        Matrix3x2 transform)
    {
        var scene = stage.Scene;
        var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
        if (!scene.TryGetGradientPathWorldPoints(objectIndex, out var path)
            || path.Length < 2
            || estimatedWidth is not > 0)
        {
            return false;
        }

        var screenLength = 0f;
        for (var index = 1; index < path.Length; index++)
        {
            screenLength += Vector2.Distance(
                Vector2.Transform(ToVector(path[index - 1]), transform),
                Vector2.Transform(ToVector(path[index]), transform));
        }
        var segments = GradientPaintUtilities.CreatePathGradientSegments(
            path,
            stops,
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        if (segments.Length == 0) return false;

        using var layer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var old = _target.Transform;
        _target.Transform = Matrix3x2.Identity;
        _target.PushLayer(parameters, layer);
        try
        {
            _target.Transform = transform;
            var cachedBrushes = PathGradientBrushes(scene, objectIndex, stops, segments);
            var width = Math.Max(VectorUnits.MinimumStrokeUnits, estimatedWidth * 1.12f);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var start = ToVector(segment.Start);
                var end = ToVector(segment.End);
                var direction = end - start;
                var segmentLength = direction.Length();
                var positionSpan = segment.EndPosition - segment.StartPosition;
                if (segmentLength <= 0.001f || positionSpan <= 0.0001f) continue;

                direction /= segmentLength;
                var gradientAxisLength = segmentLength / positionSpan;
                var gradientAxisStart = start - direction * (gradientAxisLength * segment.StartPosition);
                var brush = cachedBrushes.Brushes[index];
                brush.StartPoint = gradientAxisStart;
                brush.EndPoint = gradientAxisStart + direction * gradientAxisLength;
                _target.DrawLine(start, end, brush, width, RoundStrokeStyle());
            }
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            _target.PopLayer();
            _target.Transform = old;
        }

        try
        {
            _target.Transform = Matrix3x2.Identity;
            _target.DrawGeometry(mask, BrushFor(stops[^1].Argb), FillEdgeCoverageWidthPixels);
        }
        finally
        {
            _target.Transform = old;
        }
        return true;
    }

    private bool DrawReference3DSourceShapeGradientFill(
        StageControl stage,
        ID2D1Geometry mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops,
        Matrix3x2 transform)
    {
        var cached = ShapeGradientBitmap(stage, stage.Scene, objectIndex, stops);
        if (cached is null) return false;

        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        var destination = Rect(
            cached.WorldBounds.Left,
            cached.WorldBounds.Top,
            cached.WorldBounds.Width,
            cached.WorldBounds.Height);
        var source = Rect(0, 0, cached.PixelWidth, cached.PixelHeight);
        var old = _target!.Transform;
        _target.Transform = Matrix3x2.Identity;
        _target.PushLayer(parameters, ShapeGradientMaskLayer());
        try
        {
            _target.Transform = transform;
            _target.DrawBitmap(cached.Bitmap, destination, 1f, BitmapInterpolationMode.Linear, source);
        }
        finally
        {
            _target.Transform = Matrix3x2.Identity;
            _target.PopLayer();
            _target.Transform = old;
        }

        var edge = BrushFor(stops.Count > 0 ? stops[^1].Argb : stage.Scene.GradientEndArgb[objectIndex]);
        try
        {
            _target.Transform = Matrix3x2.Identity;
            _target.DrawGeometry(mask, edge, FillEdgeCoverageWidthPixels);
        }
        finally
        {
            _target.Transform = old;
        }
        return true;
    }

    private static float Reference3DSourcePixelWidth(Matrix3x2 transform)
    {
        var scaleX = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = MathF.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        return 1f / Math.Max(0.0001f, Math.Max(scaleX, scaleY));
    }

    private bool DrawReference3DObjectOutline(StageControl stage, int objectIndex, GdiColor layerColor)
    {
        var scene = stage.Scene;
        if (stage.IsObjectHiddenForRendering(scene, objectIndex)) return false;
        var color = Reference3DOutlineColor(scene, objectIndex, layerColor);
        if (color.A == 0) return false;
        using var geometry = CreateReference3DGeometry(
            stage.GetReference3DProjectedSolid(objectIndex).SelectionEdges,
            fillOnly: false);
        if (geometry is null) return false;
        _target!.DrawGeometry(geometry, BrushFor(color.ToArgb()), 1f, RoundStrokeStyle());
        return true;
    }

    private ID2D1PathGeometry? CreateReference3DMaskGeometry(
        StageControl stage,
        IReadOnlyList<int> maskObjects)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var objectIndex in maskObjects)
            {
                if (!SceneRenderOrder.HasFill(stage.Scene.ShapeKind[objectIndex])
                    || stage.IsObjectHiddenForRendering(stage.Scene, objectIndex))
                {
                    continue;
                }
                hasGeometry |= AppendReference3DGeometry(
                    sink,
                    stage.GetReference3DProjectedContours(objectIndex),
                    fillOnly: true);
            }
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }

    private ID2D1PathGeometry? CreateReference3DGeometry(
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            hasGeometry = AppendReference3DGeometry(sink, contours, fillOnly);
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }

    private ID2D1PathGeometry? CreateReference3DSourceGeometry(
        IReadOnlyList<Reference3DSourceContour> contours,
        bool fillOnly)
    {
        var path = _factory!.CreatePathGeometry();
        var hasGeometry = false;
        using (var sink = path.Open())
        {
            sink.SetFillMode(FillMode.Alternate);
            foreach (var contour in contours)
            {
                if (fillOnly && !contour.Closed) continue;
                if (contour.Points.Length < (contour.Closed ? 3 : 2)) continue;
                sink.BeginFigure(
                    ToVector(contour.Points[0]),
                    contour.Closed ? FigureBegin.Filled : FigureBegin.Hollow);
                for (var index = 1; index < contour.Points.Length; index++)
                {
                    sink.AddLine(ToVector(contour.Points[index]));
                }
                sink.EndFigure(contour.Closed ? FigureEnd.Closed : FigureEnd.Open);
                hasGeometry = true;
            }
            sink.Close();
        }
        if (hasGeometry) return path;
        path.Dispose();
        return null;
    }

    private static bool AppendReference3DGeometry(
        ID2D1GeometrySink sink,
        IReadOnlyList<Reference3DProjectedContour> contours,
        bool fillOnly)
    {
        var hasGeometry = false;
        foreach (var contour in contours)
        {
            if (fillOnly && !contour.Closed) continue;
            if (contour.Points.Length < (contour.Closed ? 3 : 2)) continue;
            sink.BeginFigure(
                ToVector(contour.Points[0]),
                contour.Closed ? FigureBegin.Filled : FigureBegin.Hollow);
            for (var index = 1; index < contour.Points.Length; index++)
            {
                sink.AddLine(ToVector(contour.Points[index]));
            }
            sink.EndFigure(contour.Closed ? FigureEnd.Closed : FigureEnd.Open);
            hasGeometry = true;
        }
        return hasGeometry;
    }

    private void DrawReference3DImportedSvg(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (!scene.TryGetImportedSvgSource(objectIndex, out var source)
            || string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        var contour = stage.GetReference3DProjectedContours(objectIndex)
            .FirstOrDefault(item => item.Closed && item.Points.Length >= 3);
        if (contour.Points is not { Length: >= 3 }) return;

        var hasAffineTransform = stage.TryGetReference3DFlatToScreenTransform(objectIndex, out _);
        Reference3DProjectiveTriangle[] triangles = [];
        var hasProjectiveMesh = stage.TryGetReference3DProjectiveMesh(
            objectIndex,
            usePrimaryContourQuad: true,
            out triangles)
            && triangles.Length > 0;
        var projective = hasProjectiveMesh
            && (!hasAffineTransform
                || !StageControl.Reference3DProjectiveMeshCoversFullDomain(triangles));

        GdiPointF topLeft = default;
        GdiPointF topRight = default;
        GdiPointF bottomLeft = default;
        GdiSizeF rasterSize;
        if (projective)
        {
            rasterSize = StageControl.EstimateReference3DProjectiveTextureSize(triangles);
        }
        else
        {
            if (!hasAffineTransform || contour.Points.Length < 4) return;
            topLeft = contour.Points[0];
            topRight = contour.Points[1];
            bottomLeft = contour.Points[^1];
            rasterSize = new GdiSizeF(
                Math.Max(1f, Reference3DDistance(topLeft, topRight)),
                Math.Max(1f, Reference3DDistance(topLeft, bottomLeft)));
        }
        var raster = ImportedSvgRasterizer.Rasterize(source, rasterSize.Width, rasterSize.Height);
        var bitmap = ImportedSvgBitmap(raster);
        var opacity = GdiColor.FromArgb(scene.Argb[objectIndex]).A / 255f;
        if (projective)
        {
            using var screenMask = CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true);
            if (screenMask is not null)
            {
                DrawReference3DProjectiveTriangles(
                    triangles,
                    screenMask,
                    triangle =>
                    {
                        if (!StageControl.TryGetReference3DTextureToScreenTransform(
                                triangle,
                                raster.PixelWidth,
                                raster.PixelHeight,
                                out var projectiveTransform))
                        {
                            return false;
                        }
                        _target!.Transform = projectiveTransform;
                        var textureBounds = StageControl.GetReference3DProjectiveTextureBounds(
                            triangle,
                            raster.PixelWidth,
                            raster.PixelHeight);
                        if (textureBounds.Width <= 0 || textureBounds.Height <= 0) return false;
                        var rectangle = Rect(
                            textureBounds.X,
                            textureBounds.Y,
                            textureBounds.Width,
                            textureBounds.Height);
                        _target.DrawBitmap(
                            bitmap,
                            rectangle,
                            opacity,
                            BitmapInterpolationMode.Linear,
                            rectangle);
                        return true;
                    });
            }
            return;
        }

        var transform = new Matrix3x2(
            (topRight.X - topLeft.X) / raster.PixelWidth,
            (topRight.Y - topLeft.Y) / raster.PixelWidth,
            (bottomLeft.X - topLeft.X) / raster.PixelHeight,
            (bottomLeft.Y - topLeft.Y) / raster.PixelHeight,
            topLeft.X,
            topLeft.Y);
        var old = _target!.Transform;
        try
        {
            _target.Transform = transform;
            var destination = Rect(0, 0, raster.PixelWidth, raster.PixelHeight);
            var sourceRect = Rect(0, 0, raster.PixelWidth, raster.PixelHeight);
            _target.DrawBitmap(
                bitmap,
                destination,
                opacity,
                BitmapInterpolationMode.Linear,
                sourceRect);
        }
        finally
        {
            _target.Transform = old;
        }
    }

    private void DrawReference3DMixingStroke(StageControl stage, int objectIndex)
    {
        var scene = stage.Scene;
        if (!scene.TryGetMixingBrushWorldRegion(objectIndex, out var region))
        {
            using var fallback = CreateReference3DGeometry(
                stage.GetReference3DProjectedContours(objectIndex),
                fillOnly: true);
            if (fallback is not null) _target!.FillGeometry(fallback, BrushFor(scene.Argb[objectIndex]));
            return;
        }
        var vertices = new GdiPointF[region.Vertices.Length];
        var visible = new bool[vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            visible[index] = stage.TryProjectReference3DFrontPoint(
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
            using var geometry = CreateReference3DTriangle(vertices[a], vertices[b], vertices[c]);
            _target!.FillGeometry(
                geometry,
                BrushFor(AverageArgb(
                    region.Vertices[a].Argb,
                    region.Vertices[b].Argb,
                    region.Vertices[c].Argb)));
        }
    }

    private ID2D1PathGeometry CreateReference3DTriangle(GdiPointF a, GdiPointF b, GdiPointF c)
    {
        var path = _factory!.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(ToVector(a), FigureBegin.Filled);
        sink.AddLine(ToVector(b));
        sink.AddLine(ToVector(c));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return path;
    }

    private void DrawReference3DSelection(StageControl stage)
    {
        foreach (var objectIndex in stage.Reference3DSelectedObjects)
        {
            using var geometry = CreateReference3DGeometry(
                stage.GetReference3DProjectedSolid(objectIndex).SelectionEdges,
                fillOnly: false);
            if (geometry is null) continue;
            DrawReference3DSelectionWithMasks(stage, objectIndex, () =>
            {
                _target!.DrawGeometry(
                    geometry,
                    BrushFor(StageControl.Reference3DSelectionHaloColor.ToArgb()),
                    5.5f,
                    RoundStrokeStyle());
                _target.DrawGeometry(
                    geometry,
                    BrushFor(StageControl.Reference3DSelectionLineColor.ToArgb()),
                    2f,
                    RoundStrokeStyle());
            });
        }
    }

    private void DrawReference3DSelectionWithMasks(
        StageControl stage,
        int objectIndex,
        Action draw)
    {
        var scene = stage.Scene;
        var layer = scene.ObjectLayer[objectIndex];
        if (scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            || !scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawWithSceneCompositionMaskClips(stage, objectIndex, draw, reference3D: true);
            return;
        }
        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;

        using var mask = CreateReference3DMaskGeometry(stage, stage.GetReference3DLayerObjects(maskLayer));
        if (mask is null) return;
        using var targetLayer = _target!.CreateLayer();
        var parameters = new LayerParameters
        {
            ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None
        };
        _target.PushLayer(parameters, targetLayer);
        try
        {
            DrawWithSceneCompositionMaskClips(stage, objectIndex, draw, reference3D: true);
        }
        finally
        {
            _target.PopLayer();
        }
    }

    private void DrawSpatialTransformGizmo(StageControl stage)
    {
        if (!stage.TryGetSpatialGizmoScreenGeometry(out var geometry)) return;
        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Rotate)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var ring = geometry.Rings[axis];
                if (ring.Length < 3) continue;
                using var path = CreateReference3DGeometry(
                    [new Reference3DProjectedContour(ring, true, 0)],
                    fillOnly: false);
                if (path is not null)
                {
                    _target!.DrawGeometry(path, BrushFor(SpatialAxisArgb(axis)), 2.2f, RoundStrokeStyle());
                }
            }
            return;
        }

        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Move)
        {
            for (var plane = 0; plane < geometry.PlaneHandles.Length; plane++)
            {
                var polygon = geometry.PlaneHandles[plane];
                if (polygon.Length != 4) continue;
                using var path = CreateReference3DGeometry(
                    [new Reference3DProjectedContour(polygon, true, 0)],
                    fillOnly: true);
                if (path is null) continue;
                _target!.FillGeometry(path, BrushFor(SpatialPlaneArgb(plane, 68)));
                _target.DrawGeometry(path, BrushFor(SpatialPlaneArgb(plane, 205)), 1.3f, RoundStrokeStyle());
            }
        }

        for (var axis = 0; axis < 3; axis++)
        {
            var color = BrushFor(SpatialAxisArgb(axis));
            _target!.DrawLine(ToVector(geometry.Origin), ToVector(geometry.Endpoints[axis]), color, 2.4f, RoundStrokeStyle());
            if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Scale)
            {
                var endpoint = geometry.Endpoints[axis];
                var square = Rect(endpoint.X - 4, endpoint.Y - 4, 8, 8);
                _target.FillRectangle(square, color);
            }
            else
            {
                using var arrow = CreateSpatialArrowGeometry(geometry.Origin, geometry.Endpoints[axis]);
                if (arrow is not null) _target.FillGeometry(arrow, color);
            }
        }
        if (stage.SpatialTransformGizmoMode == SpatialTransformMode.Scale)
        {
            var origin = geometry.Origin;
            var square = Rect(origin.X - 4, origin.Y - 4, 8, 8);
            _target!.FillRectangle(square, BrushFor(GdiColor.FromArgb(238, 235, 241, 242).ToArgb()));
        }
    }

    private ID2D1PathGeometry? CreateSpatialArrowGeometry(GdiPointF origin, GdiPointF endpoint)
    {
        var dx = endpoint.X - origin.X;
        var dy = endpoint.Y - origin.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f) return null;
        dx /= length;
        dy /= length;
        var perpendicularX = -dy;
        var perpendicularY = dx;
        var basePoint = new GdiPointF(endpoint.X - dx * 10f, endpoint.Y - dy * 10f);
        return CreateReference3DTriangle(
            endpoint,
            new GdiPointF(basePoint.X + perpendicularX * 4.5f, basePoint.Y + perpendicularY * 4.5f),
            new GdiPointF(basePoint.X - perpendicularX * 4.5f, basePoint.Y - perpendicularY * 4.5f));
    }

    private static GdiColor Reference3DOutlineColor(
        VectorScene scene,
        int objectIndex,
        GdiColor layerColor)
    {
        if (layerColor.IsEmpty) return GdiColor.Empty;
        var shape = scene.ShapeKind[objectIndex];
        var fillAlpha = SceneRenderOrder.HasFill(shape) ? GdiColor.FromArgb(scene.Argb[objectIndex]).A : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            ? GdiColor.FromArgb(scene.StrokeArgb[objectIndex]).A
            : 0;
        var alpha = (layerColor.A * Math.Max(fillAlpha, strokeAlpha) + 127) / 255;
        return GdiColor.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private static int AverageArgb(int first, int second, int third)
    {
        var a = GdiColor.FromArgb(first);
        var b = GdiColor.FromArgb(second);
        var c = GdiColor.FromArgb(third);
        return GdiColor.FromArgb(
            (a.A + b.A + c.A) / 3,
            (a.R + b.R + c.R) / 3,
            (a.G + b.G + c.G) / 3,
            (a.B + b.B + c.B) / 3).ToArgb();
    }

    private static int SpatialAxisArgb(int axis) => axis switch
    {
        0 => GdiColor.FromArgb(245, 235, 82, 82).ToArgb(),
        1 => GdiColor.FromArgb(245, 82, 205, 122).ToArgb(),
        _ => GdiColor.FromArgb(245, 82, 155, 245).ToArgb()
    };

    private static int SpatialPlaneArgb(int plane, int alpha)
    {
        var color = GdiColor.FromArgb(SpatialAxisArgb(plane switch
        {
            0 => 2,
            1 => 1,
            _ => 0
        }));
        return GdiColor.FromArgb(alpha, color.R, color.G, color.B).ToArgb();
    }

    private static float Reference3DDistance(GdiPointF left, GdiPointF right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void DrawWithSceneCompositionMaskClips(
        StageControl stage,
        int objectIndex,
        Action draw,
        bool reference3D)
    {
        var clips = stage.GetSceneCompositionMaskClips(objectIndex);
        if (clips.Count == 0)
        {
            draw();
            return;
        }

        var layers = new List<ID2D1Layer>(clips.Count);
        var geometries = new List<ID2D1PathGeometry>(clips.Count);
        try
        {
            foreach (var clip in clips)
            {
                var geometry = reference3D
                    ? CreateReference3DGeometry(stage.GetReference3DProjectedMaskContours(clip), fillOnly: true)
                    : CreateSceneCompositionMaskGeometry2D(stage, clip);
                if (geometry is null) return;
                var layer = _target!.CreateLayer();
                var parameters = new LayerParameters
                {
                    ContentBounds = Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    GeometricMask = geometry,
                    MaskAntialiasMode = AntialiasMode.PerPrimitive,
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = 1f,
                    OpacityBrush = null,
                    LayerOptions = LayerOptions.None
                };
                _target.PushLayer(parameters, layer);
                geometries.Add(geometry);
                layers.Add(layer);
            }
            draw();
        }
        finally
        {
            for (var index = layers.Count - 1; index >= 0; index--)
            {
                _target!.PopLayer();
                layers[index].Dispose();
                geometries[index].Dispose();
            }
        }
    }

    private ID2D1PathGeometry? CreateSceneCompositionMaskGeometry2D(
        StageControl stage,
        SceneCompositionMaskClip clip)
    {
        var projected = stage.GetSceneCompositionMaskWorldContours(clip)
            .Where(contour => contour.Length >= 3)
            .Select(contour => new Reference3DProjectedContour(
                contour.Select(point => stage.WorldToScreen(point.X, point.Y)).ToArray(),
                true,
                0))
            .ToArray();
        return CreateReference3DGeometry(projected, fillOnly: true);
    }
}
