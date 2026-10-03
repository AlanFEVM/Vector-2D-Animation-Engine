using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private Point _reference3DGdiViewportOffset;
    // GDI+ has no opacity layer, so translucent physical items are composed here first.
    private sealed class Reference3DGdiOpacitySurface(Size size) : IDisposable
    {
        private readonly Size _size = new(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height));
        private Bitmap? _bitmap;
        private Graphics? _graphics;
        private ImageAttributes? _attributes;
        private ColorMatrix? _opacityMatrix;

        public Graphics Begin(Graphics template)
        {
            EnsureSurface();
            _graphics!.ResetTransform();
            _graphics.ResetClip();
            _graphics.CompositingMode = CompositingMode.SourceCopy;
            _graphics.Clear(Color.Transparent);
            _graphics.CompositingMode = CompositingMode.SourceOver;
            _graphics.CompositingQuality = template.CompositingQuality;
            _graphics.SmoothingMode = template.SmoothingMode;
            _graphics.PixelOffsetMode = template.PixelOffsetMode;
            _graphics.InterpolationMode = template.InterpolationMode;
            using var transform = template.Transform;
            _graphics.Transform = transform;
            return _graphics;
        }

        public void CompositeTo(Graphics destination, float opacity)
        {
            if (_bitmap is null) return;
            opacity = float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f;
            if (opacity <= 0f) return;

            _attributes ??= new ImageAttributes();
            _opacityMatrix ??= new ColorMatrix();
            _opacityMatrix.Matrix33 = opacity;
            _attributes.SetColorMatrix(
                _opacityMatrix,
                ColorMatrixFlag.Default,
                ColorAdjustType.Bitmap);
            var state = destination.Save();
            try
            {
                destination.ResetTransform();
                destination.CompositingMode = CompositingMode.SourceOver;
                destination.InterpolationMode = InterpolationMode.NearestNeighbor;
                destination.DrawImage(
                    _bitmap,
                    new Rectangle(0, 0, _size.Width, _size.Height),
                    0,
                    0,
                    _size.Width,
                    _size.Height,
                    GraphicsUnit.Pixel,
                    _attributes);
            }
            finally
            {
                destination.Restore(state);
            }
        }

        public void Dispose()
        {
            _attributes?.Dispose();
            _graphics?.Dispose();
            _bitmap?.Dispose();
        }

        private void EnsureSurface()
        {
            if (_bitmap is not null) return;
            _bitmap = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppPArgb);
            _graphics = Graphics.FromImage(_bitmap);
        }
    }

    private RenderStats DrawReference3DScene(Graphics graphics)
    {
        Reference3DGpuOpticsEnabled = false;
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

    internal void DrawGdiReference3DPlaybackBackground(Graphics graphics)
    {
        ResetLastGdiFrameTelemetry();
        graphics.ResetTransform();
        graphics.ResetClip();
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.None;
        DrawGrid(graphics);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        BeginScenePassOrder();
        RecordEditableScenePass();
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
        using var opacitySurface = new Reference3DGdiOpacitySurface(
            Scene.HasSymbolFilters ? LayerCompositor().SurfaceSize : ClientSize);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (Scene.RequiresIsolatedLayerCompositing)
            {
                var maskPaths = new Dictionary<int, GraphicsPath?>();
                var drawnObjects = new HashSet<int>();
                try
                {
                    var compositor = LayerCompositor();
                    compositor.CompositeBatchesTo(
                        graphics,
                        Scene,
                        layer => objectsByLayer[layer].Length > 0,
                        (layerGraphics, layers) =>
                        {
                            var previousOffset = _reference3DGdiViewportOffset;
                            _reference3DGdiViewportOffset = new Point(compositor.Padding.Left, compositor.Padding.Top);
                            try
                            {
                                foreach (var item in GetReference3DCompositeLayerRenderItems(layers))
                                {
                                    if (DrawReference3DSceneItem(
                                            layerGraphics, item, maskPaths, opacitySurface))
                                    {
                                        drawnObjects.Add(item.ObjectIndex);
                                    }
                                }
                            }
                            finally { _reference3DGdiViewportOffset = previousOffset; }
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
                        if (DrawReference3DSceneItem(
                                graphics,
                                item,
                                maskPaths,
                                opacitySurface))
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
        IDictionary<int, GraphicsPath?> maskPaths,
        Reference3DGdiOpacitySurface opacitySurface)
    {
        if ((uint)item.ObjectIndex >= Scene.ObjectCount
            || IsObjectHiddenForRendering(Scene, item.ObjectIndex))
        {
            return false;
        }

        var opacity = float.IsFinite(item.MaterialOpacity)
            ? Math.Clamp(item.MaterialOpacity, 0f, 1f)
            : 1f;
        var physicalSurface = item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke;
        if (physicalSurface && opacity < 0.999999f)
        {
            if (opacity <= 0f) return false;
            var surfaceGraphics = opacitySurface.Begin(graphics);
            var drawn = DrawReference3DSceneItemClipped(
                surfaceGraphics,
                item with { MaterialOpacity = 1f },
                maskPaths);
            if (drawn) opacitySurface.CompositeTo(graphics, opacity);
            return drawn;
        }

        return DrawReference3DSceneItemClipped(graphics, item, maskPaths);
    }

    private bool DrawReference3DSceneItemClipped(
        Graphics graphics,
        Reference3DRenderItem item,
        IDictionary<int, GraphicsPath?> maskPaths)
    {
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
        if (item.OpticalSurface is { } opticalSurface
            && item.Kind is Reference3DRenderKind.Back
                or Reference3DRenderKind.Side
                or Reference3DRenderKind.FrontFill
                or Reference3DRenderKind.FrontStroke)
        {
            var surfaceDrawn = false;
            DrawWithSceneCompositionMaskClips(
                graphics,
                item.ObjectIndex,
                () =>
                {
                    DrawReference3DOpticalSurface(graphics, opticalSurface);
                    surfaceDrawn = true;
                },
                reference3D: true);
            return surfaceDrawn;
        }

        switch (item.Kind)
        {
            case Reference3DRenderKind.FrontFill:
                DrawReference3DObject(graphics, item, SceneRenderPass.Fill);
                return true;
            case Reference3DRenderKind.FrontStroke:
                DrawReference3DObject(graphics, item, SceneRenderPass.Stroke);
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
                        outlineColor,
                        item.MaterialOpacity),
                    reference3D: true);
                return objectDrawn;
            default:
                return false;
        }
    }

    private void DrawReference3DOpticalSurface(
        Graphics graphics,
        Reference3DOpticalSurface surface)
    {
        var state = graphics.Save();
        try
        {
            ResetReference3DGdiScreenTransform(graphics);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.InterpolationMode = surface.PixelWidth == surface.Bounds.Width
                && surface.PixelHeight == surface.Bounds.Height
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(
                surface.Bitmap,
                surface.Bounds,
                0,
                0,
                surface.PixelWidth,
                surface.PixelHeight,
                GraphicsUnit.Pixel);
        }
        finally
        {
            graphics.Restore(state);
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
                    DrawReference3DObject(graphics, item, SceneRenderPass.Fill);
                    break;
                case Reference3DRenderKind.FrontStroke:
                    DrawReference3DObject(graphics, item, SceneRenderPass.Stroke);
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
                using var fill = new SolidBrush(ApplyReference3DMaterialOpacity(
                    Color.FromArgb(item.VectorLightingArgb
                        ?? GetReference3DExtrusionSurfaceColor(item).ToArgb()),
                    item.MaterialOpacity));
                FillPathAntialiased(graphics, path, fill);
                if (item.VectorLightingArgb is null)
                {
                    DrawReference3DOpticalFinish(graphics, item, path);
                }
            },
            reference3D: true);
    }

    private void DrawReference3DOpticalFinish(
        Graphics graphics,
        Reference3DRenderItem item,
        GraphicsPath? surfacePath = null)
    {
        if (item.VectorLightingArgb is not null) return;
        var ownsSurfacePath = surfacePath is null;
        var resolvedSurfacePath = surfacePath
            ?? CreateReference3DPath(item.Contours, fillOnly: true);
        if (resolvedSurfacePath.PointCount == 0)
        {
            if (ownsSurfacePath) resolvedSurfacePath.Dispose();
            return;
        }
        try
        {
            var shade = ApplyReference3DMaterialOpacity(
                Color.FromArgb(item.OpticalResponse.ShadeArgb),
                item.MaterialOpacity);
            if (shade.A > 0)
            {
                using var shadeBrush = new SolidBrush(shade);
                FillPathAntialiased(graphics, resolvedSurfacePath, shadeBrush);
            }
            if (item.LocalLightLayers is { Length: > 0 }
                || item.ShadowLayers is { Length: > 0 })
            {
                var opticalClipState = graphics.Save();
                try
                {
                    graphics.SetClip(resolvedSurfacePath, CombineMode.Intersect);
                    if (item.LocalLightLayers is { Length: > 0 } localLightLayers)
                    {
                        foreach (var layer in localLightLayers)
                        {
                            using var lightPath = CreateReference3DPath(
                                layer.Contours,
                                fillOnly: true);
                            if (lightPath.PointCount == 0) continue;
                            if (layer.DiffuseStops.Any(stop => Color.FromArgb(stop.Argb).A > 0))
                            {
                                DrawReference3DLocalLightGradient(
                                    graphics,
                                    lightPath,
                                    layer.GradientTransform,
                                    layer.DiffuseStops,
                                    item.MaterialOpacity);
                            }
                            if (layer.SpecularStops.Any(stop => Color.FromArgb(stop.Argb).A > 0))
                            {
                                DrawReference3DLocalLightGradient(
                                    graphics,
                                    lightPath,
                                    layer.GradientTransform,
                                    layer.SpecularStops,
                                    item.MaterialOpacity);
                            }
                        }
                    }
                    if (item.ShadowLayers is { Length: > 0 } shadowLayers)
                    {
                        foreach (var shadow in shadowLayers)
                        {
                            using var shadowPath = CreateReference3DPath(
                                shadow.Contours,
                                fillOnly: true);
                            if (shadowPath.PointCount == 0) continue;
                            var shadowColor = ApplyReference3DMaterialOpacity(
                                Color.FromArgb(shadow.Argb),
                                item.MaterialOpacity);
                            if (shadowColor.A == 0) continue;
                            using var shadowBrush = new SolidBrush(shadowColor);
                            FillPathAntialiased(graphics, shadowPath, shadowBrush);
                        }
                    }
                }
                finally
                {
                    graphics.Restore(opticalClipState);
                }
            }
            var highlight = ApplyReference3DMaterialOpacity(
                Color.FromArgb(item.OpticalResponse.HighlightArgb),
                item.MaterialOpacity);
            if (highlight.A > 0)
            {
                using var highlightBrush = new SolidBrush(highlight);
                FillPathAntialiased(graphics, resolvedSurfacePath, highlightBrush);
            }
        }
        finally
        {
            if (ownsSurfacePath) resolvedSurfacePath.Dispose();
        }
    }

    private void DrawReference3DLocalLightGradient(
        Graphics graphics,
        GraphicsPath lightPath,
        System.Numerics.Matrix3x2 transform,
        IReadOnlyList<GradientStop> stops,
        float materialOpacity,
        bool reinforceEdge = true)
    {
        if (stops is not GradientStop[] stopArray)
        {
            stopArray = stops.ToArray();
        }

        var brush = Reference3DGdiLocalLightBrush(stopArray, materialOpacity);
        using var brushTransform = new Matrix(
            transform.M11,
            transform.M12,
            transform.M21,
            transform.M22,
            transform.M31,
            transform.M32);
        brush.Transform = brushTransform;
        if (reinforceEdge)
        {
            FillPathAntialiased(graphics, lightPath, brush);
        }
        else
        {
            FillReference3DOpticalOverlay(graphics, lightPath, brush);
        }
    }

    private static void FillReference3DOpticalOverlay(
        Graphics graphics,
        GraphicsPath path,
        Brush brush)
    {
        var smoothingMode = graphics.SmoothingMode;
        var pixelOffsetMode = graphics.PixelOffsetMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.SmoothingMode = smoothingMode;
            graphics.PixelOffsetMode = pixelOffsetMode;
        }
    }

    private PathGradientBrush Reference3DGdiLocalLightBrush(
        GradientStop[] stops,
        float materialOpacity)
    {
        materialOpacity = float.IsFinite(materialOpacity)
            ? Math.Clamp(materialOpacity, 0f, 1f)
            : 1f;
        var key = new Reference3DGdiLocalLightBrushKey(
            stops,
            BitConverter.SingleToInt32Bits(materialOpacity));
        if (_reference3DGdiLocalLightBrushes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (_reference3DGdiLocalLightBrushes.Count >= MaximumReference3DGdiLocalLightBrushProfiles)
        {
            ClearReference3DGdiLocalLightBrushCache();
        }

        using var unitPath = new GraphicsPath(FillMode.Winding);
        const int segments = 48;
        var boundary = new PointF[segments];
        for (var index = 0; index < boundary.Length; index++)
        {
            var angle = MathF.Tau * index / boundary.Length;
            boundary[index] = new PointF(MathF.Cos(angle), MathF.Sin(angle));
        }
        unitPath.AddPolygon(boundary);
        cached = new PathGradientBrush(unitPath)
        {
            CenterPoint = PointF.Empty,
            InterpolationColors = Reference3DColorBlend(stops, materialOpacity)
        };
        _reference3DGdiLocalLightBrushes.Add(key, cached);
        return cached;
    }

    private void ClearReference3DGdiLocalLightBrushCache()
    {
        foreach (var brush in _reference3DGdiLocalLightBrushes.Values) brush.Dispose();
        _reference3DGdiLocalLightBrushes.Clear();
    }

    private bool DrawReference3DIntersectionEdge(
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
        var edgeColor = Color.FromArgb(
            item.SolidStrokeOpticalBaseArgb ?? item.EdgeArgb);
        using var pen = new Pen(
            ApplyReference3DMaterialOpacity(edgeColor, item.MaterialOpacity),
            item.EdgeWidth)
        {
            LineJoin = LineJoin.Round,
            StartCap = item.EdgeStartCap ? LineCap.Round : LineCap.Flat,
            EndCap = item.EdgeEndCap ? LineCap.Round : LineCap.Flat
        };
        graphics.DrawPath(pen, path);
        if (item.OpticalSurfaceContours is { Length: > 0 } opticalContours)
        {
            using var opticalPath = CreateReference3DPath(opticalContours, fillOnly: true);
            if (opticalPath.PointCount > 0)
            {
                DrawReference3DStrokeOpticalLayers(
                    graphics,
                    item,
                    opticalPath);
            }
        }
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

    private void DrawReference3DObject(
        Graphics graphics,
        Reference3DRenderItem item,
        SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            graphics,
            item.ObjectIndex,
            () =>
            {
                GraphicsPath? sharedFillPath = null;
                try
                {
                    if (pass == SceneRenderPass.Fill
                        && item.OpticalSurface is null
                        && CanReuseReference3DVectorFillPath(item))
                    {
                        sharedFillPath = CreateReference3DPath(item.Contours, fillOnly: true);
                    }

                    DrawReference3DObjectUnclipped(
                        graphics,
                        item,
                        pass,
                        sharedFillPath);
                    if (pass == SceneRenderPass.Fill)
                    {
                        DrawReference3DOpticalFinish(graphics, item, sharedFillPath);
                    }
                    else if (item.OpticalSurfaceContours is { Length: > 0 } opticalContours)
                    {
                        using var opticalPath = CreateReference3DPath(
                            opticalContours,
                            fillOnly: true);
                        if (opticalPath.PointCount > 0)
                        {
                            DrawReference3DStrokeOpticalLayers(
                                graphics,
                                item,
                                opticalPath);
                        }
                    }
                }
                finally
                {
                    sharedFillPath?.Dispose();
                }
            },
            reference3D: true);
    }

    private bool CanReuseReference3DVectorFillPath(Reference3DRenderItem item)
    {
        if (item.Kind != Reference3DRenderKind.FrontFill
            || item.Contours.Length == 0
            || (uint)item.ObjectIndex >= Scene.ObjectCount)
        {
            return false;
        }
        var shape = Scene.ShapeKind[item.ObjectIndex];
        return shape is not (ShapeKind.ImportedSvg or ShapeKind.MixingStroke or ShapeKind.Bitmap)
            && !Scene.HasGradient(item.ObjectIndex);
    }

    private void DrawReference3DStrokeOpticalLayers(
        Graphics graphics,
        Reference3DRenderItem item,
        GraphicsPath surfacePath)
    {
        var opticalClipState = graphics.Save();
        try
        {
            graphics.SetClip(surfacePath, CombineMode.Intersect);
            foreach (var layer in item.LocalLightLayers ?? [])
            {
                using var lightPath = CreateReference3DPath(layer.Contours, fillOnly: true);
                if (lightPath.PointCount == 0) continue;
                if (layer.SolidStrokeStops is { Length: > 0 } solidStrokeStops)
                {
                    DrawReference3DLocalLightGradient(
                        graphics,
                        lightPath,
                        layer.GradientTransform,
                        solidStrokeStops,
                        item.MaterialOpacity,
                        reinforceEdge: false);
                }
                else
                {
                    if (layer.DiffuseStops.Any(stop => Color.FromArgb(stop.Argb).A > 0))
                    {
                        DrawReference3DLocalLightGradient(
                            graphics,
                            lightPath,
                            layer.GradientTransform,
                            layer.DiffuseStops,
                            item.MaterialOpacity,
                            reinforceEdge: false);
                    }
                    if (layer.SpecularStops.Any(stop => Color.FromArgb(stop.Argb).A > 0))
                    {
                        DrawReference3DLocalLightGradient(
                            graphics,
                            lightPath,
                            layer.GradientTransform,
                            layer.SpecularStops,
                            item.MaterialOpacity,
                            reinforceEdge: false);
                    }
                }
            }
            foreach (var shadow in item.ShadowLayers ?? [])
            {
                using var shadowPath = CreateReference3DPath(shadow.Contours, fillOnly: true);
                if (shadowPath.PointCount == 0) continue;
                using var shadowBrush = new SolidBrush(ApplyReference3DMaterialOpacity(
                    Color.FromArgb(shadow.Argb),
                    item.MaterialOpacity));
                FillReference3DOpticalOverlay(graphics, shadowPath, shadowBrush);
            }
        }
        finally
        {
            graphics.Restore(opticalClipState);
        }
    }

    private void DrawReference3DObjectUnclipped(
        Graphics graphics,
        Reference3DRenderItem item,
        SceneRenderPass pass,
        GraphicsPath? fillPath = null)
    {
        var objectIndex = item.ObjectIndex;
        if (IsObjectHiddenForRendering(Scene, objectIndex)) return;
        var shape = Scene.ShapeKind[objectIndex];
        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill)
            {
                DrawReference3DImportedSvg(graphics, objectIndex, item.MaterialOpacity);
            }
            return;
        }
        if (shape == ShapeKind.Bitmap)
        {
            if (pass == SceneRenderPass.Fill)
            {
                DrawReference3DBitmap(graphics, item, objectIndex, item.MaterialOpacity);
            }
            return;
        }
        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill)
            {
                DrawReference3DMixingStroke(graphics, objectIndex, item.MaterialOpacity);
            }
            return;
        }
        if (Scene.HasGradient(objectIndex)
            && TryDrawReference3DFrontGradient(
                graphics,
                item,
                shape,
                pass,
                item.MaterialOpacity))
        {
            if (pass == SceneRenderPass.Stroke && shape == ShapeKind.Line)
            {
                var gradientWidth = GetReference3DStrokeWidth(
                    objectIndex,
                    Scene.Stroke[objectIndex]);
                DrawReference3DLineEndpointJoins(
                    graphics,
                    item,
                    gradientWidth,
                    Reference3DLineEndpointGradientColor(
                        objectIndex,
                        startEndpoint: true,
                        item.MaterialOpacity,
                        item.OpticalStrokeGradientStops),
                    Reference3DLineEndpointGradientColor(
                        objectIndex,
                        startEndpoint: false,
                        item.MaterialOpacity,
                        item.OpticalStrokeGradientStops));
            }
            return;
        }

        if (item.Contours.Length == 0) return;
        var ownsPath = fillPath is null;
        var path = fillPath ?? CreateReference3DPath(
            item.Contours,
            fillOnly: pass == SceneRenderPass.Fill);
        try
        {
            if (path.PointCount == 0) return;
            if (pass == SceneRenderPass.Fill)
            {
                using var fill = new SolidBrush(ApplyReference3DMaterialOpacity(
                    Color.FromArgb(item.VectorLightingArgb ?? Scene.Argb[objectIndex]),
                    item.MaterialOpacity));
                FillPathAntialiased(graphics, path, fill);
                return;
            }

            var width = GetReference3DStrokeWidth(objectIndex, Scene.Stroke[objectIndex]);
            if (width <= 0) return;
            var color = Scene.StrokeArgb.Length > objectIndex
                ? Color.FromArgb(Scene.StrokeArgb[objectIndex])
                : Color.FromArgb(238, 242, 241);
            if (item.SolidStrokeOpticalBaseArgb is int solidStrokeArgb)
            {
                color = Color.FromArgb(solidStrokeArgb);
            }
            color = ApplyReference3DMaterialOpacity(color, item.MaterialOpacity);
            if (shape == ShapeKind.Line)
            {
                DrawReference3DLineStroke(graphics, item, color, width);
                DrawReference3DLineEndpointJoins(graphics, item, width, color, color);
                return;
            }
            using var pen = new Pen(color, width)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawPath(pen, path);
        }
        finally
        {
            if (ownsPath) path.Dispose();
        }
    }

    private void DrawReference3DLineStroke(
        Graphics graphics,
        Reference3DRenderItem item,
        Color color,
        float width)
    {
        var startStyle = Scene.GetLineEndpointStyle(item.ObjectIndex, startEndpoint: true);
        var endStyle = Scene.GetLineEndpointStyle(item.ObjectIndex, startEndpoint: false);
        var lineJoin = startStyle == LineEndpointStyle.Sharp
            || endStyle == LineEndpointStyle.Sharp
                ? LineJoin.Miter
                : LineJoin.Round;
        foreach (var contour in item.Contours)
        {
            if (contour.Closed || contour.Points.Length < 2) continue;
            using var path = CreateReference3DPath([contour], fillOnly: false);
            if (path.PointCount == 0) continue;
            using var pen = new Pen(color, width)
            {
                LineJoin = lineJoin,
                MiterLimit = 8,
                StartCap = contour.HasSourceStart
                    ? LineCapForEndpoint(startStyle)
                    : LineCap.Flat,
                EndCap = contour.HasSourceEnd
                    ? LineCapForEndpoint(endStyle)
                    : LineCap.Flat
            };
            graphics.DrawPath(pen, path);
        }
    }

    private void DrawReference3DLineEndpointJoins(
        Graphics graphics,
        Reference3DRenderItem item,
        float width,
        Color startColor,
        Color endColor)
    {
        DrawEndpoint(startEndpoint: true, startColor);
        DrawEndpoint(startEndpoint: false, endColor);

        void DrawEndpoint(bool startEndpoint, Color color)
        {
            if (!TryGetReference3DLineEndpointJoin(
                    item.ObjectIndex,
                    startEndpoint,
                    item.Contours,
                    width,
                    out var joint,
                    out var miters,
                    out _))
            {
                return;
            }
            using var brush = new SolidBrush(color);
            foreach (var miter in miters)
            {
                graphics.FillPolygon(
                    brush,
                    [joint, miter.OuterFirstOffset, miter.OuterMiter, miter.OuterSecondOffset]);
                graphics.FillPolygon(
                    brush,
                    [joint, miter.InnerFirstOffset, miter.InnerMiter, miter.InnerSecondOffset]);
            }
        }
    }

    private static Color ApplyReference3DMaterialOpacity(Color color, float opacity)
    {
        if (color.A == 0) return color;
        var scale = float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f;
        if (scale >= 0.999999f) return color;
        var alpha = (int)Math.Clamp(Math.Round(color.A * scale), 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private Color Reference3DLineEndpointGradientColor(
        int objectIndex,
        bool startEndpoint,
        float materialOpacity,
        IReadOnlyList<GradientStop>? opticalStops = null)
    {
        var sourceKind = Scene.GetGradientKind(objectIndex);
        // GDI's affine Line path treats ShapeRadial as linear; its projective path uses the mapping.
        var renderedKind = sourceKind switch
        {
            GradientKind.Radial => GradientKind.Radial,
            GradientKind.ShapeRadial when !TryGetReference3DFlatToScreenTransform(
                objectIndex,
                out _) => GradientKind.ShapeRadial,
            _ => GradientKind.Linear
        };
        var color = SampleReference3DLineEndpointGradient(
            Scene,
            objectIndex,
            startEndpoint,
            renderedKind,
            opticalStops);
        return ApplyReference3DMaterialOpacity(color, materialOpacity);
    }

    internal static Color SampleReference3DLineEndpointGradient(
        VectorScene scene,
        int objectIndex,
        bool startEndpoint,
        GradientKind renderedKind,
        IReadOnlyList<GradientStop>? opticalStops = null)
    {
        var fallback = (uint)objectIndex < scene.StrokeArgb.Length
            ? Color.FromArgb(scene.StrokeArgb[objectIndex])
            : Color.FromArgb(238, 242, 241);
        if ((uint)objectIndex >= scene.ObjectCount
            || !scene.HasGradient(objectIndex)
            || !scene.TryGetLineEndpoint(objectIndex, startEndpoint, out var endpoint))
        {
            return fallback;
        }

        var stops = opticalStops is { Count: > 0 }
            ? opticalStops
            : scene.GetGradientStops(objectIndex);
        if (stops.Count == 0) return fallback;
        var start = scene.GetGradientStart(objectIndex);
        var end = scene.GetGradientEnd(objectIndex);
        var axisX = end.X - start.X;
        var axisY = end.Y - start.Y;
        var axisLengthSquared = axisX * axisX + axisY * axisY;
        if (!float.IsFinite(axisLengthSquared) || axisLengthSquared <= 0.0001f)
        {
            return Color.FromArgb(stops[^1].Argb);
        }

        float position;
        if (renderedKind == GradientKind.ShapeRadial)
        {
            position = GradientPaintUtilities.ShapeRadialPosition(
                scene.GetShapeGradientMappingContours(objectIndex),
                start,
                endpoint);
        }
        else if (renderedKind == GradientKind.Radial)
        {
            var pointX = endpoint.X - start.X;
            var pointY = endpoint.Y - start.Y;
            var pointDistanceSquared = pointX * pointX + pointY * pointY;
            position = float.IsFinite(pointDistanceSquared)
                ? MathF.Sqrt(Math.Max(0, pointDistanceSquared)) / MathF.Sqrt(axisLengthSquared)
                : 1f;
        }
        else
        {
            position = ((endpoint.X - start.X) * axisX + (endpoint.Y - start.Y) * axisY)
                / axisLengthSquared;
        }
        if (!float.IsFinite(position)) position = 1f;
        return GradientPaintUtilities.SampleColor(stops, Math.Clamp(position, 0f, 1f));
    }

    private bool TryDrawReference3DFrontGradient(
        Graphics graphics,
        Reference3DRenderItem item,
        ShapeKind shape,
        SceneRenderPass pass,
        float materialOpacity)
    {
        var objectIndex = item.ObjectIndex;
        var gradientPass = pass == SceneRenderPass.Fill && shape != ShapeKind.Line
            || pass == SceneRenderPass.Stroke && shape == ShapeKind.Line;
        if (!gradientPass)
        {
            return false;
        }
        if (!TryGetReference3DFlatToScreenTransform(objectIndex, out var transform))
        {
            return TryDrawReference3DProjectiveFrontGradient(
                graphics,
                item,
                pass,
                materialOpacity);
        }

        using var path = CreateReference3DSourcePath(
            GetReference3DSourceContours(objectIndex),
            fillOnly: pass == SceneRenderPass.Fill);
        if (path.PointCount == 0) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DPath(
                item.Contours,
                fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask!.PointCount == 0) return false;

        using var matrix = Reference3DGdiScreenMatrix(transform);
        var state = graphics.Save();
        try
        {
            if (screenMask is not null) graphics.SetClip(screenMask, CombineMode.Intersect);
            graphics.Transform = matrix;
            var sourceStops = pass == SceneRenderPass.Stroke
                && item.OpticalStrokeGradientStops is { Length: > 0 } opticalStrokeStops
                ? opticalStrokeStops
                : pass == SceneRenderPass.Fill
                    && item.OpticalGradientStops is { Length: > 0 } opticalGradientStops
                    ? opticalGradientStops
                : Scene.GetGradientStops(objectIndex);
            var stops = GradientPaintUtilities.ScaleStopAlpha(sourceStops, materialOpacity);
            if (pass == SceneRenderPass.Stroke)
            {
                var startStyle = Scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
                var endStyle = Scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
                using var stroke = CreateReference3DSourceGradientBrush(objectIndex, stops);
                using var pen = new Pen(
                    stroke,
                    GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]))
                {
                    LineJoin = startStyle == LineEndpointStyle.Sharp
                        || endStyle == LineEndpointStyle.Sharp
                            ? LineJoin.Miter
                            : LineJoin.Round,
                    MiterLimit = 8,
                    StartCap = LineCapForEndpoint(startStyle),
                    EndCap = LineCapForEndpoint(endStyle)
                };
                graphics.DrawPath(pen, path);
                return true;
            }

            if (Scene.GetGradientKind(objectIndex) == GradientKind.Linear
                && DrawReference3DSourcePathGradientFill(graphics, path, objectIndex, stops))
            {
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.Radial)
            {
                DrawReference3DSourceRadialGradientFill(graphics, path, objectIndex, stops);
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial
                && DrawReference3DSourceShapeGradientFill(graphics, path, objectIndex, stops))
            {
                return true;
            }
            if (Scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial)
            {
                DrawReference3DSourceRadialGradientFill(graphics, path, objectIndex, stops);
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
        Reference3DRenderItem item,
        SceneRenderPass pass,
        float materialOpacity)
    {
        var objectIndex = item.ObjectIndex;
        var sourceContours = GetReference3DSourceContours(objectIndex);
        if (!TryGetReference3DProjectiveMesh(
                objectIndex,
                sourceContours,
                usePrimaryContourQuad: false,
                out var triangles)
            || triangles.Length == 0)
        {
            return false;
        }

        using var path = CreateReference3DSourcePath(
            sourceContours,
            fillOnly: pass == SceneRenderPass.Fill);
        if (path.PointCount == 0) return false;
        using var screenMask = pass == SceneRenderPass.Fill
            ? CreateReference3DPath(item.Contours, fillOnly: true)
            : null;
        if (pass == SceneRenderPass.Fill && screenMask!.PointCount == 0) return false;

        var sourceStops = pass == SceneRenderPass.Stroke
            && item.OpticalStrokeGradientStops is { Length: > 0 } opticalStrokeStops
            ? opticalStrokeStops
            : pass == SceneRenderPass.Fill
                && item.OpticalGradientStops is { Length: > 0 } opticalGradientStops
                ? opticalGradientStops
            : Scene.GetGradientStops(objectIndex);
        var stops = GradientPaintUtilities.ScaleStopAlpha(sourceStops, materialOpacity);
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
        Brush? radialOuter = null;
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
            else if (pass == SceneRenderPass.Fill
                     && Scene.GetGradientKind(objectIndex) == GradientKind.Radial)
            {
                radialOuter = new SolidBrush(Color.FromArgb(stops[^1].Argb));
                gradient = CreateReference3DSourceProjectiveRadialGradientBrush(objectIndex, stops);
            }
            else
            {
                gradient = CreateReference3DSourceGradientBrush(objectIndex, stops);
            }

            var outerState = graphics.Save();
            try
            {
                ResetReference3DGdiScreenTransform(graphics);
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
                        ResetReference3DGdiScreenTransform(graphics);
                        graphics.SetClip(trianglePath, CombineMode.Intersect);
                        using var matrix = Reference3DGdiScreenMatrix(transform);
                        graphics.Transform = matrix;
                        if (pass == SceneRenderPass.Stroke)
                        {
                            var startStyle = Scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
                            var endStyle = Scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
                            using var pen = new Pen(
                                gradient,
                                GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]))
                            {
                                LineJoin = startStyle == LineEndpointStyle.Sharp
                                    || endStyle == LineEndpointStyle.Sharp
                                        ? LineJoin.Miter
                                        : LineJoin.Round,
                                MiterLimit = 8,
                                StartCap = LineCapForEndpoint(startStyle),
                                EndCap = LineCapForEndpoint(endStyle)
                            };
                            graphics.DrawPath(pen, path);
                        }
                        else if (hasPathGradient)
                        {
                            DrawReference3DSourcePathGradientSegments(
                                graphics,
                                pathGradientSegments,
                                pathGradientWidth,
                                triangle);
                        }
                        else if (Scene.GetGradientKind(objectIndex) == GradientKind.Radial)
                        {
                            FillReference3DSourcePath(graphics, path, radialOuter!, transform);
                            graphics.FillPath(gradient!, path);
                        }
                        else
                        {
                            FillReference3DSourcePath(graphics, path, gradient!, transform);
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
            radialOuter?.Dispose();
            gradient?.Dispose();
            mappingPath?.Dispose();
        }
    }

    private Brush CreateReference3DSourceProjectiveRadialGradientBrush(
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var start = Scene.GetGradientStart(objectIndex);
        var endColor = Color.FromArgb(stops[^1].Argb);
        var radius = Reference3DScreenDistance(start, Scene.GetGradientEnd(objectIndex));
        if (!float.IsFinite(radius) || radius <= 0.0001f) return new SolidBrush(endColor);

        try
        {
            using var circle = new GraphicsPath();
            circle.AddEllipse(start.X - radius, start.Y - radius, radius * 2f, radius * 2f);
            return new PathGradientBrush(circle)
            {
                CenterPoint = start,
                InterpolationColors = Reference3DProjectiveRadialColorBlend(stops)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            return new SolidBrush(endColor);
        }
    }

    private Brush CreateReference3DSourceGradientBrush(
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var start = Scene.GetGradientStart(objectIndex);
        var end = Scene.GetGradientEnd(objectIndex);
        var endColor = Color.FromArgb(stops[^1].Argb);
        var axisX = end.X - start.X;
        var axisY = end.Y - start.Y;
        var axisLengthSquared = axisX * axisX + axisY * axisY;
        if (!float.IsFinite(axisLengthSquared) || axisLengthSquared <= 0.00000001f)
        {
            return new SolidBrush(endColor);
        }

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

        var minimum = 0f;
        var maximum = 1f;
        foreach (var contour in GetReference3DSourceContours(objectIndex))
        {
            foreach (var point in contour.Points)
            {
                var position = ((point.X - start.X) * axisX + (point.Y - start.Y) * axisY)
                    / axisLengthSquared;
                if (!float.IsFinite(position)) continue;
                minimum = Math.Min(minimum, position);
                maximum = Math.Max(maximum, position);
            }
        }

        var axisLength = MathF.Sqrt(axisLengthSquared);
        var sourcePadding = Scene.ShapeKind[objectIndex] == ShapeKind.Line
            ? GetReference3DSourceStrokeWidth(objectIndex, Scene.Stroke[objectIndex]) * 0.5f
            : 0f;
        var paddingPosition = Math.Max(0.0001f, sourcePadding / axisLength);
        minimum -= paddingPosition;
        maximum += paddingPosition;
        var span = maximum - minimum;
        var extendedStart = new PointF(start.X + axisX * minimum, start.Y + axisY * minimum);
        var extendedEnd = new PointF(start.X + axisX * maximum, start.Y + axisY * maximum);
        var colors = new List<Color>(stops.Count + 2)
        {
            Color.FromArgb(stops[0].Argb)
        };
        var positions = new List<float>(stops.Count + 2) { 0f };
        foreach (var stop in stops)
        {
            var position = Math.Clamp((stop.Position - minimum) / span, 0f, 1f);
            if (position <= positions[^1] + 0.000001f)
            {
                colors[^1] = Color.FromArgb(stop.Argb);
                continue;
            }
            if (position >= 0.999999f) break;
            colors.Add(Color.FromArgb(stop.Argb));
            positions.Add(position);
        }
        colors.Add(endColor);
        positions.Add(1f);

        var brush = new LinearGradientBrush(
            extendedStart,
            extendedEnd,
            Color.FromArgb(stops[0].Argb),
            endColor)
        {
            WrapMode = WrapMode.TileFlipXY,
            InterpolationColors = new ColorBlend
            {
                Colors = colors.ToArray(),
                Positions = positions.ToArray()
            }
        };
        return brush;
    }

    private bool DrawReference3DSourcePathGradientFill(
        Graphics graphics,
        GraphicsPath mask,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
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
        IReadOnlyList<GradientStop> stops)
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
    }

    private bool DrawReference3DSourceShapeGradientFill(
        Graphics graphics,
        GraphicsPath path,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
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
            graphics.FillPath(brush, path);
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

    private static float Reference3DSourcePixelWidth(System.Numerics.Matrix3x2 transform)
    {
        var scaleX = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = MathF.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        return 1f / Math.Max(0.0001f, Math.Max(scaleX, scaleY));
    }

    private static ColorBlend Reference3DColorBlend(
        IReadOnlyList<GradientStop> stops,
        float alphaScale = 1f)
    {
        alphaScale = Math.Clamp(alphaScale, 0f, 1f);
        return new ColorBlend
        {
            Colors = stops.Select(stop =>
            {
                var color = Color.FromArgb(stop.Argb);
                return Color.FromArgb(
                    (int)MathF.Round(color.A * alphaScale),
                    color.R,
                    color.G,
                    color.B);
            }).ToArray(),
            Positions = stops.Select(stop => stop.Position).ToArray()
        };
    }

    private static ColorBlend Reference3DProjectiveRadialColorBlend(
        IReadOnlyList<GradientStop> stops)
    {
        var reversed = stops.Reverse().ToArray();
        return new ColorBlend
        {
            Colors = reversed.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
            Positions = reversed.Select(stop => 1f - stop.Position).ToArray()
        };
    }

    private bool DrawReference3DObjectOutline(
        Graphics graphics,
        int objectIndex,
        Color layerColor,
        float materialOpacity = 1f)
    {
        if (IsObjectHiddenForRendering(Scene, objectIndex)) return false;
        var color = ApplyReference3DMaterialOpacity(
            Reference3DOutlineColor(objectIndex, layerColor),
            materialOpacity);
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

    private void DrawReference3DImportedSvg(
        Graphics graphics,
        int objectIndex,
        float materialOpacity)
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
            rasterSize = EstimateReference3DProjectiveSvgTextureSize(triangles);
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
        var opacity = Color.FromArgb(Scene.Argb[objectIndex]).A / 255f
            * Math.Clamp(materialOpacity, 0f, 1f);
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

        using var bitmap = raster.AcquireBitmap();
        var destination = new[] { topLeft, topRight, bottomLeft };
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(
                    bitmap.Bitmap,
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
                bitmap.Bitmap,
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
        float opacity) => TryDrawBoundedReference3DProjectiveSvg(
            graphics, objectIndex, raster, triangles, opacity);

    /// <summary>
    /// Reference-3D equivalent of the placed-bitmap draw. Projected bitmaps follow the same
    /// projective/affine split as imported SVG so a texture on a tilted surface stays
    /// perspective-correct instead of being smeared by a single affine blit.
    /// </summary>
    private void DrawReference3DBitmap(
        Graphics graphics,
        Reference3DRenderItem item,
        int objectIndex,
        float materialOpacity)
    {
        if (!Scene.TryGetBitmapObjectData(objectIndex, out var data)) return;
        if (!TryDecodeBitmapImage(data.ImageAssetId, out var raster)) return;
        var contour = GetReference3DProjectedContours(objectIndex)
            .FirstOrDefault(candidate => candidate.Closed && candidate.Points.Length >= 3);
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
        if (!projective)
        {
            if (!hasAffineTransform || contour.Points.Length < 4) return;
            topLeft = contour.Points[0];
            topRight = contour.Points[1];
            bottomLeft = contour.Points[^1];
        }

        var opacity = Color.FromArgb(Scene.Argb[objectIndex]).A / 255f
            * Math.Clamp(materialOpacity, 0f, 1f);
        if (projective)
        {
            _ = TryDrawBoundedReference3DProjectiveRaster(
                graphics,
                objectIndex,
                raster.Pixels,
                raster.Stride,
                raster.PixelWidth,
                raster.PixelHeight,
                triangles,
                opacity);
            return;
        }

        using var bitmap = raster.AcquireBitmap();
        var destination = new[] { topLeft, topRight, bottomLeft };
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(
                    bitmap.Bitmap,
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
                bitmap.Bitmap,
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

    private void DrawReference3DMixingStroke(
        Graphics graphics,
        int objectIndex,
        float materialOpacity)
    {
        if (!Scene.TryGetMixingBrushWorldRegion(objectIndex, out var region))
        {
            var contours = GetReference3DProjectedContours(objectIndex);
            using var path = CreateReference3DPath(contours, fillOnly: true);
            if (path.PointCount == 0) return;
            using var fallback = new SolidBrush(ApplyReference3DMaterialOpacity(
                Color.FromArgb(Scene.Argb[objectIndex]),
                materialOpacity));
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
            using var fill = new SolidBrush(ApplyReference3DMaterialOpacity(
                AverageColor(
                    region.Vertices[a].Argb,
                    region.Vertices[b].Argb,
                    region.Vertices[c].Argb),
                materialOpacity));
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
        if (!TryGetSpatialGizmoRenderGeometry(
                out var geometry,
                out var opacity,
                out var renderScale)
            || opacity <= 0.001f)
        {
            return;
        }
        var dpiScale = SpatialGizmoDpiScale * renderScale;
        var highlight = ApplySpatialGizmoOpacity(Color.FromArgb(246, 255, 196, 56), opacity);
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
                    var highlighted = IsSpatialTransformHandleHighlighted(
                        (SpatialTransformAxis)(axis + 1));
                    if (highlighted)
                    {
                        using var halo = new Pen(highlight, 6.2f * dpiScale)
                        {
                            LineJoin = LineJoin.Round,
                            StartCap = LineCap.Round,
                            EndCap = LineCap.Round
                        };
                        graphics.DrawPolygon(halo, ring);
                    }
                    using var pen = new Pen(
                        ApplySpatialGizmoOpacity(SpatialAxisColor(axis), opacity),
                        (highlighted ? 3.4f : 2.2f) * dpiScale)
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
                    var planeAxis = plane switch
                    {
                        0 => SpatialTransformAxis.XY,
                        1 => SpatialTransformAxis.XZ,
                        _ => SpatialTransformAxis.YZ
                    };
                    var highlighted = IsSpatialTransformHandleHighlighted(planeAxis);
                    using var fill = new SolidBrush(ApplySpatialGizmoOpacity(
                        Color.FromArgb(highlighted ? 126 : 68, color),
                        opacity));
                    using var outline = new Pen(
                        ApplySpatialGizmoOpacity(
                            Color.FromArgb(highlighted ? 255 : 205, color),
                            opacity),
                        (highlighted ? 2.1f : 1.3f) * dpiScale)
                    {
                        LineJoin = LineJoin.Round
                    };
                    graphics.FillPolygon(fill, polygon);
                    if (highlighted)
                    {
                        using var halo = new Pen(highlight, 4.6f * dpiScale)
                        {
                            LineJoin = LineJoin.Round
                        };
                        graphics.DrawPolygon(halo, polygon);
                    }
                    graphics.DrawPolygon(outline, polygon);
                }
            }

            for (var axis = 0; axis < 3; axis++)
            {
                var axisKind = (SpatialTransformAxis)(axis + 1);
                var highlighted = IsSpatialTransformHandleHighlighted(axisKind);
                var color = ApplySpatialGizmoOpacity(SpatialAxisColor(axis), opacity);
                if (highlighted)
                {
                    using var halo = new Pen(highlight, 6.4f * dpiScale)
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    graphics.DrawLine(halo, geometry.Origin, geometry.Endpoints[axis]);
                }
                using var pen = new Pen(color, (highlighted ? 3.5f : 2.4f) * dpiScale)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(pen, geometry.Origin, geometry.Endpoints[axis]);
                if (_spatialTransformGizmoMode == SpatialTransformMode.Scale)
                {
                    var halfSize = 4f * dpiScale;
                    using var fill = new SolidBrush(color);
                    var endpoint = geometry.Endpoints[axis];
                    if (highlighted)
                    {
                        var haloHalfSize = 6.5f * dpiScale;
                        using var haloFill = new SolidBrush(highlight);
                        graphics.FillRectangle(
                            haloFill,
                            endpoint.X - haloHalfSize,
                            endpoint.Y - haloHalfSize,
                            haloHalfSize * 2,
                            haloHalfSize * 2);
                    }
                    graphics.FillRectangle(
                        fill,
                        endpoint.X - halfSize,
                        endpoint.Y - halfSize,
                        halfSize * 2,
                        halfSize * 2);
                }
                else
                {
                    DrawSpatialArrowHead(
                        graphics,
                        geometry.Origin,
                        geometry.Endpoints[axis],
                        color,
                        dpiScale,
                        highlighted ? highlight : null);
                }
            }

            if (_spatialTransformGizmoMode == SpatialTransformMode.Scale)
            {
                var highlighted = IsSpatialTransformHandleHighlighted(SpatialTransformAxis.Uniform);
                var halfSize = 4f * dpiScale;
                if (highlighted)
                {
                    var haloHalfSize = 6.5f * dpiScale;
                    using var halo = new SolidBrush(highlight);
                    graphics.FillRectangle(
                        halo,
                        geometry.Origin.X - haloHalfSize,
                        geometry.Origin.Y - haloHalfSize,
                        haloHalfSize * 2,
                        haloHalfSize * 2);
                }
                using var center = new SolidBrush(ApplySpatialGizmoOpacity(
                    Color.FromArgb(238, 235, 241, 242),
                    opacity));
                graphics.FillRectangle(
                    center,
                    geometry.Origin.X - halfSize,
                    geometry.Origin.Y - halfSize,
                    halfSize * 2,
                    halfSize * 2);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
        }
    }

    private static void DrawSpatialArrowHead(
        Graphics graphics,
        PointF origin,
        PointF endpoint,
        Color color,
        float dpiScale,
        Color? highlight)
    {
        var dx = endpoint.X - origin.X;
        var dy = endpoint.Y - origin.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f) return;
        dx /= length;
        dy /= length;
        var perpendicularX = -dy;
        var perpendicularY = dx;
        var basePoint = new PointF(
            endpoint.X - dx * 10f * dpiScale,
            endpoint.Y - dy * 10f * dpiScale);
        PointF[] points =
        [
            endpoint,
            new PointF(
                basePoint.X + perpendicularX * 4.5f * dpiScale,
                basePoint.Y + perpendicularY * 4.5f * dpiScale),
            new PointF(
                basePoint.X - perpendicularX * 4.5f * dpiScale,
                basePoint.Y - perpendicularY * 4.5f * dpiScale)
        ];
        if (highlight is { } haloColor)
        {
            using var halo = new Pen(haloColor, 4f * dpiScale)
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPolygon(halo, points);
        }
        using var fill = new SolidBrush(color);
        graphics.FillPolygon(fill, points);
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

    private static Color ApplySpatialGizmoOpacity(Color color, float opacity)
    {
        var alpha = (int)MathF.Round(color.A * Math.Clamp(opacity, 0f, 1f));
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

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
