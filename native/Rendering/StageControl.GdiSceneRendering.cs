using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl : Control
{
    private GraphicsPath? _collisionTerrainOverlayPath;
    private VectorScene? _collisionTerrainOverlayScene;
    private long _collisionTerrainOverlayRevision = -1;
    private int _collisionTerrainOverlayLayer = -1;
    private int _collisionTerrainOverlayFrame = -1;
    private float _collisionTerrainOverlayCameraX;
    private float _collisionTerrainOverlayCameraY;
    private float _collisionTerrainOverlayZoom;
    private int _collisionTerrainOverlayWidth;
    private int _collisionTerrainOverlayHeight;
    private int[] _collisionTerrainOverlayObjects = [];

    private RenderStats DrawOverviewTiles(Graphics g)
    {
        var scene = Scene;
        var tileW = scene.StageWidth / scene.OverviewColumnCount;
        var tileH = scene.StageHeight / scene.OverviewRowCount;
        var viewport = VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.OverviewColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.OverviewRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.OverviewColumnCount + tx;
                var count = scene.OverviewCount[tile];
                if (count <= 0) continue;
                atoms += scene.OverviewAtoms[tile];
                var wx = tx * tileW - scene.StageWidth * 0.5f;
                var wy = ty * tileH - scene.StageHeight * 0.5f;
                var screen = WorldToScreen(wx, wy);
                g.FillRectangle(BrushFor(scene.OverviewArgb[tile]), screen.X, screen.Y, Math.Max(1, WorldLengthToScreen(tileW) + 1), Math.Max(1, WorldLengthToScreen(tileH) + 1));
                draws++;
            }
        }

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawTiles(Graphics g)
    {
        var scene = Scene;
        var tileW = scene.StageWidth / scene.TileColumnCount;
        var tileH = scene.StageHeight / scene.TileRowCount;
        var viewport = VisibleWorldBounds();
        var left = viewport.Left;
        var right = viewport.Right;
        var top = viewport.Top;
        var bottom = viewport.Bottom;
        var minX = (int)Math.Clamp((left + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var maxX = (int)Math.Clamp((right + scene.StageWidth * 0.5f) / tileW, 0, scene.TileColumnCount - 1);
        var minY = (int)Math.Clamp((top + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var maxY = (int)Math.Clamp((bottom + scene.StageHeight * 0.5f) / tileH, 0, scene.TileRowCount - 1);
        var draws = 0;
        long atoms = 0;

        for (var ty = minY; ty <= maxY; ty++)
        {
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = ty * scene.TileColumnCount + tx;
                var count = scene.TileCount[tile];
                if (count <= 0) continue;
                atoms += scene.TileAtoms[tile];
                var wx = tx * tileW - scene.StageWidth * 0.5f;
                var wy = ty * tileH - scene.StageHeight * 0.5f;
                var screen = WorldToScreen(wx, wy);
                g.FillRectangle(BrushFor(scene.TileArgb[tile]), screen.X, screen.Y, Math.Max(1, WorldLengthToScreen(tileW) + 1), Math.Max(1, WorldLengthToScreen(tileH) + 1));
                draws++;
            }
        }

        return new RenderStats(0, 0, atoms, draws, 0, true);
    }

    private RenderStats DrawObjects(Graphics g, int drawLimit)
    {
        var scene = Scene;
        var bounds = VisibleWorldBounds();
        if (scene.HasSymbolFilters)
        {
            var padding = LayerFilterBounds.GetPadding(scene, ActiveViewZoom);
            bounds.Inflate(
                Math.Max(padding.Left, padding.Right) / WorldLengthToScreen(1),
                Math.Max(padding.Top, padding.Bottom) / WorldLengthToScreen(1));
            drawLimit = int.MaxValue;
        }
        _renderOrder.Collect(scene, bounds, Frame);

        return DrawCollectedObjects(g, drawLimit);
    }

    private RenderStats DrawCollectedObjects(Graphics g, int drawLimit)
    {
        var scene = Scene;
        if (scene.RequiresIsolatedLayerCompositing)
        {
            return DrawCollectedObjectsComposited(g, scene, drawLimit);
        }

        var drawn = _renderOrder.DrawLayers(
            scene,
            drawLimit,
            (layer, objects, start) => DrawLayerObjects(g, scene, layer, objects, start));
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private RenderStats DrawCollectedObjectsComposited(Graphics graphics, VectorScene scene, int drawLimit)
    {
        var starts = new int[scene.LayerCount];
        var skip = Math.Max(0, _renderOrder.VisibleCount - drawLimit);
        var drawn = 0;
        for (var layer = scene.LayerCount - 1; layer >= 0; layer--)
        {
            var objects = _renderOrder.GetLayerObjects(layer);
            var start = Math.Min(skip, objects.Count);
            starts[layer] = start;
            skip -= start;
            drawn += objects.Count - start;
        }

        var compositor = LayerCompositor();
        compositor.CompositeTo(graphics, scene, (layerGraphics, layer) =>
        {
            DrawLayerObjects(
                layerGraphics,
                scene,
                layer,
                _renderOrder.GetLayerObjects(layer),
                starts[layer]);
        });
        return new RenderStats(_renderOrder.VisibleCount, drawn, _renderOrder.VisibleAtoms, 0, _renderOrder.ScannedCount, false);
    }

    private LayerBlendCompositor LayerCompositor()
    {
        var size = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        var padding = LayerFilterBounds.GetPadding(Scene, ActiveViewZoom);
        if (_layerBlendCompositor is not null && _layerBlendCompositor.Size == size
            && _layerBlendCompositor.Padding == padding
            && _layerBlendCompositor.FilterPixelScale == ActiveViewZoom) return _layerBlendCompositor;
        _layerBlendCompositor?.Dispose();
        _layerBlendCompositor = new LayerBlendCompositor(size, padding, ActiveViewZoom);
        return _layerBlendCompositor;
    }

    private void DrawLodDetailObjects(Graphics graphics)
    {
        foreach (var objectIndex in GetLodDetailObjectIndices())
        {
            if (!Scene.IsObjectActive(objectIndex, Frame)
                || !Scene.ShouldRenderLayerContent(Scene.ObjectLayer[objectIndex]))
            {
                continue;
            }

            if (Scene.IsLayerEffectivelyOutlined(Scene.ObjectLayer[objectIndex]))
            {
                DrawWithSceneCompositionMaskClips(
                    graphics,
                    objectIndex,
                    () => DrawObjectOutline(graphics, objectIndex),
                    reference3D: false);
            }
            else
            {
                if (SceneRenderOrder.HasFill(Scene.ShapeKind[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Fill);
                if (SceneRenderOrder.HasStroke(Scene.ShapeKind[objectIndex], Scene.Stroke[objectIndex])) DrawObject(graphics, objectIndex, SceneRenderPass.Stroke);
            }
        }
    }

    internal bool TryGetActiveMaskLayer(out int layer)
    {
        layer = Scene.ActiveLayer;
        return (uint)layer < Scene.LayerCount
            && Scene.GetLayerKind(layer) == DrawingLayerKind.Mask
            && Scene.IsLayerEffectivelyVisible(layer)
            && !Scene.IsLayerEffectivelyLocked(layer);
    }

    private void DrawActiveMaskOutline(Graphics graphics)
    {
        if (!TryGetActiveMaskLayer(out var maskLayer)) return;
        if (Scene.IsCollisionTerrainLayer(maskLayer)) return;
        var activeObjects = Scene.GetActiveObjectIndices(Frame);
        var maskObjects = new int[activeObjects.Length];
        var maskObjectCount = 0;
        for (var activeIndex = 0; activeIndex < activeObjects.Length; activeIndex++)
        {
            var objectIndex = activeObjects[activeIndex];
            if (Scene.ObjectLayer[objectIndex] != maskLayer
                || !SceneRenderOrder.HasFill(Scene.ShapeKind[objectIndex]))
            {
                continue;
            }

            maskObjects[maskObjectCount++] = objectIndex;
        }

        if (maskObjectCount == 0) return;
        Array.Sort(maskObjects, 0, maskObjectCount);
        if (maskObjectCount != maskObjects.Length)
        {
            Array.Resize(ref maskObjects, maskObjectCount);
        }

        using var path = CreateMaskPath(Scene, maskObjects);
        if (path.PointCount == 0) return;
        using var fill = new SolidBrush(Color.FromArgb(48, 112, 205, 209));
        using var glow = new Pen(Color.FromArgb(110, 104, 231, 232), 4f);
        using var outline = new Pen(Color.FromArgb(244, 159, 242, 242), 1.4f) { DashStyle = DashStyle.Dash };
        graphics.FillPath(fill, path);
        graphics.DrawPath(glow, path);
        graphics.DrawPath(outline, path);
    }

    private void DrawCollisionTerrainOverlay(Graphics graphics, VectorScene scene)
    {
        var terrainFrame = DragPreviewScene?.RandomFracturePreviewTerrainFrame ?? Frame;
        var terrainLayers = scene.GetCollisionTerrainLayers()
            .Where(scene.IsLayerEffectivelyVisible)
            .ToArray();
        if (terrainLayers.Length == 0)
        {
            ClearCollisionTerrainOverlayPath();
            return;
        }

        // Active object indices are already cached by frame and revisions.
        // Filter that span instead of rescanning every object and allocating
        // LINQ/HashSet helpers on every paint.
        var activeObjects = scene.GetActiveObjectIndices(terrainFrame);
        var terrainObjects = new int[activeObjects.Length];
        var terrainObjectCount = 0;
        for (var activeIndex = 0; activeIndex < activeObjects.Length; activeIndex++)
        {
            var objectIndex = activeObjects[activeIndex];
            if (Array.IndexOf(terrainLayers, scene.ObjectLayer[objectIndex]) < 0
                || !SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex]))
            {
                continue;
            }

            terrainObjects[terrainObjectCount++] = objectIndex;
        }

        if (terrainObjectCount == 0)
        {
            ClearCollisionTerrainOverlayPath();
            return;
        }

        // Preserve the previous object-index order for stable geometry/cache
        // keys even though active indices are grouped by layer/keyframe.
        Array.Sort(terrainObjects, 0, terrainObjectCount);
        if (terrainObjectCount != terrainObjects.Length)
        {
            Array.Resize(ref terrainObjects, terrainObjectCount);
        }

        var path = GetCollisionTerrainOverlayPath(
            scene,
            terrainLayers[0],
            terrainFrame,
            terrainObjects);
        if (path is null || path.PointCount == 0) return;
        using var fill = new SolidBrush(Color.FromArgb(62, 236, 181, 72));
        using var glow = new Pen(Color.FromArgb(190, 255, 205, 92), 3f);
        using var outline = new Pen(Color.FromArgb(244, 255, 239, 185), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(glow, path);
        graphics.DrawPath(outline, path);
    }

    private GraphicsPath? GetCollisionTerrainOverlayPath(
        VectorScene scene,
        int terrainLayer,
        int terrainFrame,
        int[] terrainObjects)
    {
        if (_collisionTerrainOverlayPath is not null
            && ReferenceEquals(_collisionTerrainOverlayScene, scene)
            && _collisionTerrainOverlayRevision == scene.GeometryRevision
            && _collisionTerrainOverlayLayer == terrainLayer
            && _collisionTerrainOverlayFrame == terrainFrame
            && _collisionTerrainOverlayCameraX == CameraX
            && _collisionTerrainOverlayCameraY == CameraY
            && _collisionTerrainOverlayZoom == Zoom
            && _collisionTerrainOverlayWidth == Width
            && _collisionTerrainOverlayHeight == Height
            && _collisionTerrainOverlayObjects.SequenceEqual(terrainObjects))
        {
            return _collisionTerrainOverlayPath;
        }

        ClearCollisionTerrainOverlayPath();
        var path = CreateMaskPath(scene, terrainObjects);
        if (path.PointCount == 0)
        {
            path.Dispose();
            return null;
        }

        _collisionTerrainOverlayPath = path;
        _collisionTerrainOverlayScene = scene;
        _collisionTerrainOverlayRevision = scene.GeometryRevision;
        _collisionTerrainOverlayLayer = terrainLayer;
        _collisionTerrainOverlayFrame = terrainFrame;
        _collisionTerrainOverlayCameraX = CameraX;
        _collisionTerrainOverlayCameraY = CameraY;
        _collisionTerrainOverlayZoom = Zoom;
        _collisionTerrainOverlayWidth = Width;
        _collisionTerrainOverlayHeight = Height;
        _collisionTerrainOverlayObjects = terrainObjects.ToArray();
        return path;
    }

    private void ClearCollisionTerrainOverlayPath()
    {
        _collisionTerrainOverlayPath?.Dispose();
        _collisionTerrainOverlayPath = null;
        _collisionTerrainOverlayScene = null;
        _collisionTerrainOverlayRevision = -1;
        _collisionTerrainOverlayLayer = -1;
        _collisionTerrainOverlayFrame = -1;
        _collisionTerrainOverlayObjects = [];
    }

    private void DrawLayerObjects(Graphics graphics, VectorScene scene, int layer, IReadOnlyList<int> objects, int start)
    {
        var layerKind = scene.GetLayerKind(layer);
        if (!scene.ShouldRenderLayerContent(layer)) return;
        if (layerKind == DrawingLayerKind.Mask)
        {
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
            return;
        }
        if (!scene.TryGetMaskLayerIndex(layer, out var maskLayer))
        {
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
            return;
        }

        if (!scene.IsLayerEffectivelyVisible(maskLayer)) return;
        using var maskPath = CreateMaskPath(scene, _renderOrder.GetLayerObjects(maskLayer));
        if (maskPath.PointCount == 0) return;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(maskPath, CombineMode.Intersect);
            DrawLayerObjectsUnmasked(graphics, scene, layer, objects, start);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawLayerObjectsUnmasked(
        Graphics graphics,
        VectorScene scene,
        int layer,
        IReadOnlyList<int> objects,
        int start)
    {
        var outlineLayerColor = scene.GetEffectiveLayerOutlineColor(layer);
        if (!outlineLayerColor.IsEmpty)
        {
            for (var index = start; index < objects.Count; index++)
            {
                var objectIndex = objects[index];
                DrawWithSceneCompositionMaskClips(
                    graphics,
                    objectIndex,
                    () => DrawObjectOutline(graphics, objectIndex, outlineLayerColor),
                    reference3D: false);
            }
            return;
        }

        SceneRenderOrder.DrawObjectPasses(
            scene,
            objects,
            start,
            SelectionFillDragFrontObjectsFor(scene),
            objectIndex => DrawObject(graphics, objectIndex, SceneRenderPass.Fill),
            objectIndex => DrawObject(graphics, objectIndex, SceneRenderPass.Stroke));
    }

    private void DrawObjectOutline(Graphics graphics, int objectIndex)
    {
        var scene = Scene;
        var layer = (uint)objectIndex < scene.ObjectLayer.Length ? scene.ObjectLayer[objectIndex] : -1;
        DrawObjectOutline(graphics, objectIndex, scene.GetEffectiveLayerOutlineColor(layer));
    }

    private void DrawObjectOutline(Graphics graphics, int objectIndex, Color layerColor)
    {
        var scene = Scene;
        if (IsObjectHiddenForRendering(scene, objectIndex)) return;
        var color = OutlineColor(scene, objectIndex, layerColor);
        if (color.A == 0) return;

        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        using var pen = new Pen(color, 1f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var oldMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions))
            {
                if (TryGetDistortedVectorGeometryForRendering(scene, objectIndex, distortions, out var geometry))
                {
                    using var distortedPath = new GraphicsPath(FillMode.Alternate);
                    AppendDistortedVectorGeometry(distortedPath, geometry);
                    if (distortedPath.PointCount > 0) graphics.DrawPath(pen, distortedPath);
                    return;
                }
                foreach (var contour in GetObjectBoundaryContoursForRendering(scene, objectIndex))
                {
                    if (shape != ShapeKind.Line && contour.Length >= 3)
                    {
                        graphics.DrawPolygon(pen, contour.Select(WorldToScreen).ToArray());
                    }
                    else DrawOutlinePolyline(graphics, pen, contour.Select(WorldToScreen).ToArray());
                }
                return;
            }

            if (shape == ShapeKind.Line)
            {
                using var lineBrush = new SolidBrush(color);
                DrawBezierLine(graphics, objectIndex, lineBrush, 1f);
                return;
            }

            if (shape == ShapeKind.Freeform && scene.TryGetFreehandWorldPoints(objectIndex, out var centerline))
            {
                DrawOutlinePolyline(graphics, pen, centerline.Select(WorldToScreen).ToArray());
                return;
            }

            if (shape == ShapeKind.BrushStroke && scene.TryGetFreehandWorldPoints(objectIndex, out var brushCenterline))
            {
                using var brushPath = new GraphicsPath(FillMode.Alternate);
                AppendPolygonContours(
                    brushPath,
                    FreehandStrokeProcessor.CreateBrushOutlines(brushCenterline, scene.Stroke[objectIndex]));
                if (brushPath.PointCount > 0) graphics.DrawPath(pen, brushPath);
                return;
            }

            if (shape == ShapeKind.MixingStroke)
            {
                foreach (var contour in GetMixingStrokeScreenContours(scene, objectIndex))
                {
                    DrawOutlinePolyline(graphics, pen, contour);
                }
                return;
            }

            if (shape == ShapeKind.Text && scene.TryGetTextWorldContours(objectIndex, out var textContours))
            {
                using var textPath = new GraphicsPath(FillMode.Alternate);
                AppendPolygonContours(textPath, textContours);
                if (textPath.PointCount > 0) graphics.DrawPath(pen, textPath);
                return;
            }

            if (shape is ShapeKind.ImportedSvg or ShapeKind.Bitmap)
            {
                var screen = WorldToScreen(scene.X[objectIndex], scene.Y[objectIndex]);
                var width = Math.Max(0.75f, WorldLengthToScreen(scene.Width[objectIndex]));
                var height = Math.Max(0.75f, WorldLengthToScreen(scene.Height[objectIndex]));
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(screen.X, screen.Y);
                    graphics.RotateTransform(scene.Angle[objectIndex] * 57.29578f);
                    graphics.DrawRectangle(pen, -width * 0.5f, -height * 0.5f, width, height);
                }
                finally
                {
                    graphics.Restore(state);
                }
                return;
            }

            using var path = CreateObjectBoundaryPath(scene, objectIndex);
            if (path.PointCount > 0) graphics.DrawPath(pen, path);
        }
        finally
        {
            graphics.SmoothingMode = oldMode;
        }
    }

    private static void DrawOutlinePolyline(Graphics graphics, Pen pen, IReadOnlyList<PointF> points)
    {
        if (points.Count == 1)
        {
            graphics.DrawEllipse(pen, points[0].X - 0.5f, points[0].Y - 0.5f, 1f, 1f);
        }
        else if (points.Count > 1)
        {
            graphics.DrawLines(pen, points.ToArray());
        }
    }

    private static Color OutlineColor(VectorScene scene, int objectIndex, Color layerColor)
    {
        if (layerColor.IsEmpty) return Color.Empty;
        var shape = scene.ShapeKind.Length > objectIndex ? scene.ShapeKind[objectIndex] : ShapeKind.Rectangle;
        var fillAlpha = SceneRenderOrder.HasFill(shape) && (uint)objectIndex < scene.Argb.Length
            ? Color.FromArgb(scene.Argb[objectIndex]).A
            : 0;
        var strokeAlpha = SceneRenderOrder.HasStroke(shape, scene.Stroke[objectIndex])
            && (uint)objectIndex < scene.StrokeArgb.Length
                ? Color.FromArgb(scene.StrokeArgb[objectIndex]).A
                : 0;
        var materialAlpha = Math.Max(fillAlpha, strokeAlpha);
        var alpha = (layerColor.A * materialAlpha + 127) / 255;
        return Color.FromArgb(alpha, layerColor.R, layerColor.G, layerColor.B);
    }

    private GraphicsPath CreateMaskPath(VectorScene scene, IReadOnlyList<int> maskObjects)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        foreach (var objectIndex in maskObjects)
        {
            if (!SceneRenderOrder.HasFill(scene.ShapeKind[objectIndex])) continue;
            if (TryGetObjectDistortionsForRendering(scene, objectIndex, out var distortions))
            {
                if (TryGetDistortedVectorGeometryForRendering(scene, objectIndex, distortions, out var geometry))
                {
                    AppendDistortedVectorGeometry(path, geometry);
                }
                else
                {
                    AppendPolygonContours(path, GetObjectBoundaryContoursForRendering(scene, objectIndex));
                }
            }
            else
            {
                AppendObjectBoundaryPath(path, scene, objectIndex);
            }
        }

        return path;
    }

    private void DrawObject(Graphics g, int i, SceneRenderPass pass)
    {
        DrawWithSceneCompositionMaskClips(
            g,
            i,
            () => DrawObjectUnclipped(g, i, pass),
            reference3D: false);
    }

    private void DrawObjectUnclipped(Graphics g, int i, SceneRenderPass pass)
    {
        if (TryGetObjectDistortionsForRendering(Scene, i, out var distortions))
        {
            DrawDistortedObject(g, Scene, i, pass, distortions);
            return;
        }

        DrawObjectRaw(g, i, pass);
    }

    private void DrawObjectRaw(Graphics g, int i, SceneRenderPass pass)
    {
        var scene = Scene;
        if (IsObjectHiddenForRendering(scene, i)) return;
        var screen = WorldToScreen(scene.X[i], scene.Y[i]);
        var w = Math.Max(0.75f, WorldLengthToScreen(scene.Width[i]));
        var h = Math.Max(0.75f, WorldLengthToScreen(scene.Height[i]));
        var rect = new RectangleF(screen.X - w * 0.5f, screen.Y - h * 0.5f, w, h);
        var shape = scene.ShapeKind.Length > i ? scene.ShapeKind[i] : ShapeKind.Rectangle;
        if (shape == ShapeKind.MixingStroke)
        {
            if (pass == SceneRenderPass.Fill) DrawMixingStroke(g, scene, i);
            return;
        }

        var brush = BrushFor(scene.Argb[i]);
        var shapeVertexCount = scene.GetShapeVertexCount(i);
        var strokeColor = StrokeColorFor(i);
        var screenStroke = Math.Max(0.1f, WorldLengthToScreen(scene.Stroke[i]));

        if (shape == ShapeKind.ImportedSvg)
        {
            if (pass == SceneRenderPass.Fill
                && scene.TryGetImportedSvgSource(i, out var source)
                && !string.IsNullOrWhiteSpace(source))
            {
                DrawImportedSvg(g, source, screen, w, h, scene.Angle[i], Color.FromArgb(scene.Argb[i]).A / 255f);
            }
            return;
        }

        if (shape == ShapeKind.Bitmap)
        {
            if (pass == SceneRenderPass.Fill)
            {
                DrawBitmapObject(g, scene, i, screen, w, h);
            }
            return;
        }

        if (shape == ShapeKind.Text)
        {
            if (pass == SceneRenderPass.Fill && scene.TryGetTextWorldContours(i, out var textContours))
            {
                DrawPathObject(g, i, textContours, brush, Color.Transparent, 0, 0, SceneRenderPass.Fill);
            }
            return;
        }

        if (pass == SceneRenderPass.Fill && shape != ShapeKind.Line && scene.HasGradient(i))
        {
            if (scene.GetGradientKind(i) == GradientKind.Linear && scene.HasGradientPath(i))
            {
                DrawPathGradientFill(g, scene, i);
                return;
            }

            DrawGradientFill(g, scene, i);
            return;
        }

        if (shape == ShapeKind.Path)
        {
            using var path = CreateObjectBoundaryPath(scene, i);
            if (pass == SceneRenderPass.Fill)
            {
                DrawPathObject(g, path, brush, strokeColor, scene.Stroke[i], screenStroke, pass);
                return;
            }

            if (scene.GetHiddenBoundaryStrokeParts(i) is { } hiddenParts)
            {
                // The stroke pass draws only the still-visible segments so detached
                // edges stay exposed as gaps; the fill already used the closed boundary.
                if (scene.Stroke[i] > 0)
                {
                    using var strokePath = CreateVisibleBoundaryStrokePath(scene, i, hiddenParts);
                    if (strokePath.PointCount > 0)
                    {
                        using var pen = StrokePen(strokeColor, screenStroke);
                        var oldMode = g.SmoothingMode;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawPath(pen, strokePath);
                        g.SmoothingMode = oldMode;
                    }
                }

                return;
            }

            DrawPathObject(g, path, brush, strokeColor, scene.Stroke[i], screenStroke, pass);
            return;
        }

        if (IsFreehandShape(shape) && scene.TryGetFreehandLocalPoints(i, out var freehandPoints))
        {
            var color = shape == ShapeKind.BrushStroke ? Color.FromArgb(scene.Argb[i]) : strokeColor;
            DrawFreehandStroke(g, i, freehandPoints, color, scene.Stroke[i]);
            return;
        }

        if (shape == ShapeKind.Line)
        {
            if (scene.Stroke[i] <= 0) return;
            if (pass == SceneRenderPass.Stroke && scene.HasGradient(i))
            {
                DrawGradientBezierLine(g, scene, i, screenStroke);
            }
            else if (pass == SceneRenderPass.Stroke)
            {
                DrawBezierLine(g, i, BrushFor(strokeColor.ToArgb()), screenStroke);
            }
            return;
        }

        if (Math.Abs(scene.Angle[i]) > 0.01f || shape is not ShapeKind.Rectangle)
        {
            var state = g.Save();
            g.TranslateTransform(screen.X, screen.Y);
            g.RotateTransform(scene.Angle[i] * 57.29578f);
            DrawLocalShape(g, shape, shapeVertexCount, brush, strokeColor, scene.Stroke[i], screenStroke, w, h, pass);
            g.Restore(state);
            return;
        }

        if (pass == SceneRenderPass.Fill)
        {
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
            g.FillRectangle(brush, rect);
        }
        else if (scene.Stroke[i] > 0 && w > 4 && h > 4)
        {
            using var pen = StrokePen(strokeColor, screenStroke);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void DrawDistortedObject(
        Graphics target,
        VectorScene scene,
        int objectIndex,
        SceneRenderPass pass,
        IReadOnlyList<DistortWarp> distortions)
    {
        if (TryGetDistortedVectorGeometryForRendering(scene, objectIndex, distortions, out var geometry))
        {
            DrawDistortedVectorObject(target, scene, objectIndex, pass, distortions, geometry);
            return;
        }

        DrawRasterDistortedObject(target, scene, objectIndex, pass, distortions);
    }

    private void DrawDistortedVectorObject(
        Graphics target,
        VectorScene scene,
        int objectIndex,
        SceneRenderPass pass,
        IReadOnlyList<DistortWarp> distortions,
        DistortedVectorGeometry geometry)
    {
        if (IsObjectHiddenForRendering(scene, objectIndex)) return;
        using var path = new GraphicsPath(FillMode.Alternate);
        AppendDistortedVectorGeometry(path, geometry);
        if (path.PointCount == 0) return;

        var shape = scene.ShapeKind[objectIndex];
        if (pass == SceneRenderPass.Fill)
        {
            if (!geometry.HasClosedContours) return;
            if (scene.HasGradient(objectIndex))
            {
                DrawRasterDistortedObjectClipped(target, scene, objectIndex, pass, distortions, path, null);
                return;
            }

            FillPathAntialiased(target, path, BrushFor(scene.Argb[objectIndex]));
            return;
        }

        if (scene.Stroke[objectIndex] <= 0) return;
        var screenStroke = Math.Max(0.1f, WorldLengthToScreen(scene.Stroke[objectIndex]));
        var strokeColor = StrokeColorFor(objectIndex);
        if (shape == ShapeKind.Line && scene.HasGradient(objectIndex))
        {
            using var clipPen = CreateDistortedStrokePen(scene, objectIndex, strokeColor, screenStroke);
            DrawRasterDistortedObjectClipped(target, scene, objectIndex, pass, distortions, path, clipPen);
            return;
        }

        using var pen = shape == ShapeKind.Line
            ? CreateDistortedStrokePen(scene, objectIndex, strokeColor, screenStroke)
            : StrokePen(strokeColor, screenStroke);
        var oldMode = target.SmoothingMode;
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.DrawPath(pen, path);
        target.SmoothingMode = oldMode;
    }

    private void DrawRasterDistortedObjectClipped(
        Graphics target,
        VectorScene scene,
        int objectIndex,
        SceneRenderPass pass,
        IReadOnlyList<DistortWarp> distortions,
        GraphicsPath vectorPath,
        Pen? strokePen)
    {
        var state = target.Save();
        var restored = false;
        try
        {
            if (strokePen is null)
            {
                target.SetClip(vectorPath, CombineMode.Intersect);
            }
            else
            {
                using var strokeOutline = (GraphicsPath)vectorPath.Clone();
                strokeOutline.Widen(strokePen);
                target.SetClip(strokeOutline, CombineMode.Intersect);
            }
            DrawRasterDistortedObject(target, scene, objectIndex, pass, distortions);
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException)
        {
            target.Restore(state);
            restored = true;
            DrawRasterDistortedObject(target, scene, objectIndex, pass, distortions);
        }
        finally
        {
            if (!restored) target.Restore(state);
        }
    }

    private static Pen CreateDistortedStrokePen(
        VectorScene scene,
        int objectIndex,
        Color color,
        float width)
    {
        var startStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: true);
        var endStyle = scene.GetLineEndpointStyle(objectIndex, startEndpoint: false);
        return new Pen(color, Math.Max(1.5f, width))
        {
            StartCap = LineCapForEndpoint(startStyle),
            EndCap = LineCapForEndpoint(endStyle),
            LineJoin = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp
                ? LineJoin.Miter
                : LineJoin.Round,
            MiterLimit = startStyle == LineEndpointStyle.Sharp || endStyle == LineEndpointStyle.Sharp ? 8 : 1
        };
    }

    private void DrawRasterDistortedObject(
        Graphics target,
        VectorScene scene,
        int objectIndex,
        SceneRenderPass pass,
        IReadOnlyList<DistortWarp> distortions)
    {
        if (distortions.Count == 0)
        {
            DrawObjectRaw(target, objectIndex, pass);
            return;
        }

        if (IsObjectHiddenForRendering(scene, objectIndex)) return;
        var sourceFrame = distortions[0].Source;
        if (!sourceFrame.IsValid)
        {
            DrawObjectRaw(target, objectIndex, pass);
            return;
        }

        var rawBounds = scene.GetRawObjectWorldBounds(objectIndex);
        if (rawBounds.Width <= 0.001f || rawBounds.Height <= 0.001f)
        {
            DrawObjectRaw(target, objectIndex, pass);
            return;
        }

        var sourceCorners = new[]
        {
            sourceFrame.TopLeft,
            sourceFrame.TopRight,
            sourceFrame.BottomLeft,
            sourceFrame.BottomRight
        };
        var rawCorners = new[]
        {
            new PointF(rawBounds.Left, rawBounds.Top),
            new PointF(rawBounds.Right, rawBounds.Top),
            new PointF(rawBounds.Left, rawBounds.Bottom),
            new PointF(rawBounds.Right, rawBounds.Bottom)
        };
        var uMin = 0f;
        var uMax = 1f;
        var vMin = 0f;
        var vMax = 1f;
        foreach (var point in rawCorners)
        {
            if (!TryGetFrameCoordinates(sourceFrame, point, out var u, out var v)) continue;
            uMin = Math.Min(uMin, u);
            uMax = Math.Max(uMax, u);
            vMin = Math.Min(vMin, v);
            vMax = Math.Max(vMax, v);
        }

        var paddingPixels = Math.Max(3f, WorldLengthToScreen(scene.Stroke[objectIndex]) * 0.75f);
        var sourceAxisXLength = Distance(sourceFrame.TopLeft, sourceFrame.TopRight);
        var sourceAxisYLength = Distance(sourceFrame.TopLeft, sourceFrame.BottomLeft);
        var paddingWorld = ScreenLengthToWorld(paddingPixels);
        var uPadding = sourceAxisXLength > 0.001f ? paddingWorld / sourceAxisXLength : 0.05f;
        var vPadding = sourceAxisYLength > 0.001f ? paddingWorld / sourceAxisYLength : 0.05f;
        uMin = Math.Clamp(uMin - uPadding, -0.5f, 1.5f);
        uMax = Math.Clamp(uMax + uPadding, -0.5f, 1.5f);
        vMin = Math.Clamp(vMin - vPadding, -0.5f, 1.5f);
        vMax = Math.Clamp(vMax + vPadding, -0.5f, 1.5f);
        if (uMax - uMin < 0.0001f || vMax - vMin < 0.0001f)
        {
            DrawObjectRaw(target, objectIndex, pass);
            return;
        }

        var sourceWorldBounds = new List<PointF>(sourceCorners.Length + rawCorners.Length + 4);
        sourceWorldBounds.AddRange(sourceCorners);
        sourceWorldBounds.AddRange(rawCorners);
        sourceWorldBounds.Add(SourceFramePoint(sourceFrame, uMin, vMin));
        sourceWorldBounds.Add(SourceFramePoint(sourceFrame, uMax, vMin));
        sourceWorldBounds.Add(SourceFramePoint(sourceFrame, uMin, vMax));
        sourceWorldBounds.Add(SourceFramePoint(sourceFrame, uMax, vMax));
        var sourceScreenBounds = ScreenBounds(sourceWorldBounds.Select(WorldToScreen));
        sourceScreenBounds = RectangleF.Inflate(sourceScreenBounds, paddingPixels, paddingPixels);
        if (sourceScreenBounds.Width <= 0.5f || sourceScreenBounds.Height <= 0.5f)
        {
            DrawObjectRaw(target, objectIndex, pass);
            return;
        }

        var interactivePreview = HasDistortPreview;
        var rasterMaximumDimension = interactivePreview
            ? DistortPreviewRasterMaximumDimension
            : DistortRasterMaximumDimension;
        var rasterScale = Math.Min(
            1f,
            Math.Min(
                rasterMaximumDimension / sourceScreenBounds.Width,
                rasterMaximumDimension / sourceScreenBounds.Height));
        if (!float.IsFinite(rasterScale) || rasterScale <= 0) rasterScale = 1;
        var bitmapWidth = Math.Clamp(
            (int)MathF.Ceiling(sourceScreenBounds.Width * rasterScale),
            2,
            rasterMaximumDimension);
        var bitmapHeight = Math.Clamp(
            (int)MathF.Ceiling(sourceScreenBounds.Height * rasterScale),
            2,
            rasterMaximumDimension);

        Bitmap? sourceBitmap = null;
        var disposeSourceBitmap = false;
        try
        {
            var cacheKey = new DistortRasterCacheKey(
                scene,
                objectIndex,
                pass,
                scene.GeometryRevision,
                scene.SummaryRevision,
                interactivePreview ? 0 : BasePresentationRevision,
                Frame,
                CameraX,
                CameraY,
                Zoom,
                ClientSize.Width,
                ClientSize.Height,
                sourceScreenBounds,
                bitmapWidth,
                bitmapHeight);
            if (_distortRasterCache.TryGetValue(cacheKey, out var cachedBitmap))
            {
                sourceBitmap = cachedBitmap;
                LastGdiDistortRasterReuses++;
                if (interactivePreview) DistortPreviewRasterReuseCount++;
            }
            else
            {
                sourceBitmap = new Bitmap(bitmapWidth, bitmapHeight, PixelFormat.Format32bppPArgb);
                using (var sourceGraphics = Graphics.FromImage(sourceBitmap))
                using (var transform = new Matrix(
                    rasterScale,
                    0,
                    0,
                    rasterScale,
                    -sourceScreenBounds.Left * rasterScale,
                    -sourceScreenBounds.Top * rasterScale))
                {
                    sourceGraphics.Clear(Color.Transparent);
                    sourceGraphics.CompositingMode = CompositingMode.SourceOver;
                    sourceGraphics.CompositingQuality = interactivePreview
                        ? CompositingQuality.AssumeLinear
                        : CompositingQuality.HighQuality;
                    sourceGraphics.InterpolationMode = interactivePreview
                        ? InterpolationMode.Bilinear
                        : InterpolationMode.HighQualityBicubic;
                    sourceGraphics.PixelOffsetMode = interactivePreview
                        ? PixelOffsetMode.Half
                        : PixelOffsetMode.HighQuality;
                    sourceGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                    sourceGraphics.Transform = transform;
                    DrawObjectRaw(sourceGraphics, objectIndex, pass);
                }

                if (TryCacheDistortRaster(cacheKey, sourceBitmap))
                {
                    LastGdiDistortRasterBuilds++;
                    if (interactivePreview) DistortPreviewRasterBuildCount++;
                }
                else
                {
                    disposeSourceBitmap = true;
                }
            }

            var longestSide = Math.Max(sourceScreenBounds.Width, sourceScreenBounds.Height);
            var segmentCount = distortions.Sum(distortion =>
                Enum.GetValues<DistortSide>().Sum(side => distortion.Envelope.GetSegments(side).Length));
            var maximumDivisions = interactivePreview
                ? DistortPreviewMeshMaximumDivisions
                : DistortMeshMaximumDivisions;
            var minimumDivisions = interactivePreview
                ? DistortPreviewMeshMinimumDivisions
                : DistortMeshMinimumDivisions;
            var columns = Math.Clamp(
                Math.Max(minimumDivisions, (int)MathF.Ceiling(longestSide / DistortMeshCellPixels)),
                minimumDivisions,
                maximumDivisions);
            columns = Math.Clamp(
                Math.Max(columns, segmentCount * (interactivePreview ? 1 : 2)),
                minimumDivisions,
                maximumDivisions);
            var rows = columns;
            var rasterPoints = new PointF[rows + 1, columns + 1];
            var destinationPoints = new PointF[rows + 1, columns + 1];
            for (var row = 0; row <= rows; row++)
            {
                var v = vMin + (vMax - vMin) * row / rows;
                for (var column = 0; column <= columns; column++)
                {
                    var u = uMin + (uMax - uMin) * column / columns;
                    var source = SourceFramePoint(sourceFrame, u, v);
                    rasterPoints[row, column] = ToRasterPoint(
                        WorldToScreen(source),
                        sourceScreenBounds,
                        rasterScale);
                    destinationPoints[row, column] = WorldToScreen(
                        MapObjectPointForRendering(scene, objectIndex, source));
                }
            }

            using var baseTransform = target.Transform;
            try
            {
                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < columns; column++)
                    {
                        var raster00 = rasterPoints[row, column];
                        var raster10 = rasterPoints[row, column + 1];
                        var raster01 = rasterPoints[row + 1, column];
                        var raster11 = rasterPoints[row + 1, column + 1];
                        var destination00 = destinationPoints[row, column];
                        var destination10 = destinationPoints[row, column + 1];
                        var destination01 = destinationPoints[row + 1, column];
                        var destination11 = destinationPoints[row + 1, column + 1];
                        if (!IsFinite(destination00)
                            || !IsFinite(destination10)
                            || !IsFinite(destination01)
                            || !IsFinite(destination11)) continue;

                        DrawDistortedTriangle(
                            target,
                            sourceBitmap!,
                            raster00,
                            raster10,
                            raster01,
                            destination00,
                            destination10,
                            destination01,
                            interactivePreview);
                        DrawDistortedTriangle(
                            target,
                            sourceBitmap!,
                            raster10,
                            raster11,
                            raster01,
                            destination10,
                            destination11,
                            destination01,
                            interactivePreview);
                    }
                }
            }
            finally
            {
                target.Transform = baseTransform;
            }
        }
        catch (ArgumentException)
        {
            DrawObjectRaw(target, objectIndex, pass);
        }
        catch (OutOfMemoryException)
        {
            DrawObjectRaw(target, objectIndex, pass);
        }
        finally
        {
            if (disposeSourceBitmap) sourceBitmap?.Dispose();
        }
    }

    private static void DrawDistortedTriangle(
        Graphics target,
        Bitmap source,
        PointF sourceFirst,
        PointF sourceSecond,
        PointF sourceThird,
        PointF first,
        PointF second,
        PointF third,
        bool interactivePreview)
    {
        if (Math.Abs(Cross(first, second, third)) < 0.01f) return;
        using var clip = new GraphicsPath(FillMode.Alternate);
        clip.AddPolygon([first, second, third]);
        var state = target.Save();
        try
        {
            target.SetClip(clip, CombineMode.Intersect);
            target.CompositingMode = CompositingMode.SourceOver;
            target.CompositingQuality = interactivePreview
                ? CompositingQuality.AssumeLinear
                : CompositingQuality.HighQuality;
            target.InterpolationMode = interactivePreview
                ? InterpolationMode.Bilinear
                : InterpolationMode.HighQualityBicubic;
            target.PixelOffsetMode = interactivePreview
                ? PixelOffsetMode.Half
                : PixelOffsetMode.HighQuality;
            using var transform = CreateAffineTransform(
                sourceFirst,
                sourceSecond,
                sourceThird,
                first,
                second,
                third);
            if (transform is null) return;
            using var targetTransform = target.Transform;
            transform.Multiply(targetTransform, MatrixOrder.Append);
            target.Transform = transform;
            target.DrawImage(source, 0, 0, source.Width, source.Height);
        }
        finally
        {
            target.Restore(state);
        }
    }

    private static Matrix? CreateAffineTransform(
        PointF sourceFirst,
        PointF sourceSecond,
        PointF sourceThird,
        PointF destinationFirst,
        PointF destinationSecond,
        PointF destinationThird)
    {
        var sourceX1 = sourceSecond.X - sourceFirst.X;
        var sourceY1 = sourceSecond.Y - sourceFirst.Y;
        var sourceX2 = sourceThird.X - sourceFirst.X;
        var sourceY2 = sourceThird.Y - sourceFirst.Y;
        var determinant = sourceX1 * sourceY2 - sourceX2 * sourceY1;
        if (Math.Abs(determinant) < 0.000001f) return null;
        var destinationX1 = destinationSecond.X - destinationFirst.X;
        var destinationY1 = destinationSecond.Y - destinationFirst.Y;
        var destinationX2 = destinationThird.X - destinationFirst.X;
        var destinationY2 = destinationThird.Y - destinationFirst.Y;
        var m11 = (destinationX1 * sourceY2 - destinationX2 * sourceY1) / determinant;
        var m21 = (sourceX1 * destinationX2 - sourceX2 * destinationX1) / determinant;
        var m12 = (destinationY1 * sourceY2 - destinationY2 * sourceY1) / determinant;
        var m22 = (sourceX1 * destinationY2 - sourceX2 * destinationY1) / determinant;
        var offsetX = destinationFirst.X - m11 * sourceFirst.X - m21 * sourceFirst.Y;
        var offsetY = destinationFirst.Y - m12 * sourceFirst.X - m22 * sourceFirst.Y;
        if (!float.IsFinite(m11)
            || !float.IsFinite(m12)
            || !float.IsFinite(m21)
            || !float.IsFinite(m22)
            || !float.IsFinite(offsetX)
            || !float.IsFinite(offsetY))
        {
            return null;
        }

        return new Matrix(m11, m12, m21, m22, offsetX, offsetY);
    }

    private static PointF ToRasterPoint(PointF point, RectangleF bounds, float scale) => new(
        (point.X - bounds.Left) * scale,
        (point.Y - bounds.Top) * scale);

    private static RectangleF ScreenBounds(IEnumerable<PointF> points)
    {
        var hasPoint = false;
        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;
        foreach (var point in points)
        {
            if (!IsFinite(point)) continue;
            hasPoint = true;
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return hasPoint ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
    }

    private static PointF SourceFramePoint(TransformOverlayFrame frame, float u, float v) => new(
        frame.Origin.X + frame.AxisX.X * u + frame.AxisY.X * v,
        frame.Origin.Y + frame.AxisX.Y * u + frame.AxisY.Y * v);

    private static bool TryGetFrameCoordinates(
        TransformOverlayFrame frame,
        PointF point,
        out float u,
        out float v)
    {
        var determinant = frame.AxisX.X * frame.AxisY.Y - frame.AxisX.Y * frame.AxisY.X;
        if (Math.Abs(determinant) <= 0.000001f)
        {
            u = v = 0;
            return false;
        }

        var offsetX = point.X - frame.Origin.X;
        var offsetY = point.Y - frame.Origin.Y;
        u = (offsetX * frame.AxisY.Y - offsetY * frame.AxisY.X) / determinant;
        v = (frame.AxisX.X * offsetY - frame.AxisX.Y * offsetX) / determinant;
        return float.IsFinite(u) && float.IsFinite(v);
    }

    private static float Cross(PointF a, PointF b, PointF c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static void DrawImportedSvg(
        Graphics graphics,
        string source,
        PointF screenCenter,
        float screenWidth,
        float screenHeight,
        float angleRadians,
        float opacity)
    {
        var raster = ImportedSvgRasterizer.Rasterize(source, screenWidth, screenHeight);
        using var bitmap = raster.AcquireBitmap();
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TranslateTransform(screenCenter.X, screenCenter.Y);
            graphics.RotateTransform(angleRadians * 57.29578f);
            var destination = new RectangleF(
                -screenWidth * 0.5f,
                -screenHeight * 0.5f,
                screenWidth,
                screenHeight);
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(bitmap.Bitmap, destination);
                return;
            }

            using var attributes = new ImageAttributes();
            var colorMatrix = new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) };
            attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            var destinationPoints = new[]
            {
                new PointF(destination.Left, destination.Top),
                new PointF(destination.Right, destination.Top),
                new PointF(destination.Left, destination.Bottom)
            };
            graphics.DrawImage(
                bitmap.Bitmap,
                destinationPoints,
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>
    /// Draws one placed bitmap object from its decoded managed asset. The interpolation
    /// mode follows the asset's import filter mode, so a Point-filtered image keeps hard
    /// pixel edges instead of being smoothed by the GDI fallback.
    /// </summary>
    private void DrawBitmapObject(
        Graphics graphics,
        VectorScene scene,
        int objectIndex,
        PointF screenCenter,
        float screenWidth,
        float screenHeight)
    {
        if (!scene.TryGetBitmapObjectData(objectIndex, out var data)) return;
        if (!TryDecodeBitmapImage(data.ImageAssetId, out var raster)) return;

        using var bitmap = raster.AcquireBitmap();
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.TranslateTransform(screenCenter.X, screenCenter.Y);
            graphics.RotateTransform(scene.Angle[objectIndex] * 57.29578f);
            var destination = new RectangleF(
                -screenWidth * 0.5f,
                -screenHeight * 0.5f,
                screenWidth,
                screenHeight);
            var opacity = Color.FromArgb(scene.Argb[objectIndex]).A / 255f;
            if (opacity >= 0.999f)
            {
                graphics.DrawImage(bitmap.Bitmap, destination);
                return;
            }

            using var attributes = new ImageAttributes();
            var colorMatrix = new ColorMatrix { Matrix33 = Math.Clamp(opacity, 0f, 1f) };
            attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            var destinationPoints = new[]
            {
                new PointF(destination.Left, destination.Top),
                new PointF(destination.Right, destination.Top),
                new PointF(destination.Left, destination.Bottom)
            };
            graphics.DrawImage(
                bitmap.Bitmap,
                destinationPoints,
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void DrawPathObject(Graphics g, int i, PointF[][] worldContours, Brush brush, Color strokeColor, float stroke, float screenStroke, SceneRenderPass pass)
    {
        if (worldContours.Length == 0) return;
        using var path = new GraphicsPath(FillMode.Alternate);
        AppendPolygonContours(path, worldContours);
        DrawPathObject(g, path, brush, strokeColor, stroke, screenStroke, pass);
    }

    private static void DrawPathObject(
        Graphics graphics,
        GraphicsPath path,
        Brush brush,
        Color strokeColor,
        float stroke,
        float screenStroke,
        SceneRenderPass pass)
    {
        if (path.PointCount == 0) return;
        if (pass == SceneRenderPass.Fill)
        {
            FillPathAntialiased(graphics, path, brush);
            return;
        }

        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        graphics.DrawPath(pen, path);
    }

    private GraphicsPath CreateVisibleBoundaryStrokePath(
        VectorScene scene,
        int objectIndex,
        IReadOnlySet<int> hiddenParts)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        if (!scene.TryGetPathBezierWorldContours(objectIndex, out var contours)) return path;

        var partIndex = 0;
        foreach (var contour in contours)
        {
            if (contour.Length < 2)
            {
                partIndex += Math.Max(contour.Length, 0);
                continue;
            }

            var startIndex = 0;
            while (startIndex < contour.Length)
            {
                if (hiddenParts.Contains(partIndex + startIndex))
                {
                    startIndex++;
                    continue;
                }

                var endIndex = startIndex;
                while (endIndex + 1 < contour.Length
                    && !hiddenParts.Contains(partIndex + endIndex + 1))
                {
                    endIndex++;
                }

                path.StartFigure();
                for (var nodeIndex = startIndex; nodeIndex <= endIndex; nodeIndex++)
                {
                    var current = contour[nodeIndex];
                    var next = contour[(nodeIndex + 1) % contour.Length];
                    path.AddBezier(
                        WorldToScreen(current.Anchor),
                        WorldToScreen(current.OutgoingControl),
                        WorldToScreen(next.IncomingControl),
                        WorldToScreen(next.Anchor));
                }

                startIndex = endIndex + 1;
            }

            partIndex += contour.Length;
        }

        return path;
    }

    private GraphicsPath CreateObjectBoundaryPath(VectorScene scene, int objectIndex)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        AppendObjectBoundaryPath(path, scene, objectIndex);
        return path;
    }

    private void AppendObjectBoundaryPath(GraphicsPath path, VectorScene scene, int objectIndex)
    {
        if (scene.TryGetPathBezierWorldContours(objectIndex, out var bezierContours))
        {
            AppendBezierContours(path, bezierContours);
            return;
        }

        AppendPolygonContours(path, scene.GetObjectBoundaryContours(objectIndex));
    }

    private void AppendBezierContours(GraphicsPath path, IReadOnlyList<PathBezierNode[]> contours)
    {
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            path.StartFigure();
            for (var nodeIndex = 0; nodeIndex < contour.Length; nodeIndex++)
            {
                var current = contour[nodeIndex];
                var next = contour[(nodeIndex + 1) % contour.Length];
                path.AddBezier(
                    WorldToScreen(current.Anchor),
                    WorldToScreen(current.OutgoingControl),
                    WorldToScreen(next.IncomingControl),
                    WorldToScreen(next.Anchor));
            }
            path.CloseFigure();
        }
    }

    private void AppendDistortedVectorGeometry(GraphicsPath path, DistortedVectorGeometry geometry)
    {
        AppendBezierContours(path, geometry.ClosedContours);
        if (!geometry.HasOpenStroke) return;
        path.StartFigure();
        foreach (var segment in geometry.OpenStrokeSegments)
        {
            path.AddBezier(
                WorldToScreen(segment.Start),
                WorldToScreen(segment.Control1),
                WorldToScreen(segment.Control2),
                WorldToScreen(segment.End));
        }
    }

    private void AppendPolygonContours(GraphicsPath path, IReadOnlyList<PointF[]> contours)
    {
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            path.AddPolygon(contour.Select(WorldToScreen).ToArray());
        }
    }

    private Brush CreateGradientFillBrush(VectorScene scene, int objectIndex)
    {
        var start = WorldToScreen(scene.GetGradientStart(objectIndex));
        var end = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var stops = scene.GetGradientStops(objectIndex);
        var endColor = Color.FromArgb(stops[^1].Argb);
        if (Distance(start, end) < 0.5f) return new SolidBrush(endColor);
        var brush = new LinearGradientBrush(
            start,
            end,
            Color.FromArgb(stops[0].Argb),
            endColor);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
            Positions = stops.Select(stop => stop.Position).ToArray()
        };
        return brush;
    }

    private void DrawGradientFill(Graphics g, VectorScene scene, int objectIndex)
    {
        using var path = CreateObjectBoundaryPath(scene, objectIndex);

        if (path.PointCount == 0) return;
        var gradientStops = scene.GetGradientStops(objectIndex);
        if (scene.GetGradientKind(objectIndex) == GradientKind.Radial)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, gradientStops);
            return;
        }

        if (scene.GetGradientKind(objectIndex) == GradientKind.ShapeRadial)
        {
            DrawShapeRadialGradientFill(g, path, scene, objectIndex, gradientStops);
            return;
        }

        using var brush = CreateGradientFillBrush(scene, objectIndex);
        FillPathAntialiased(g, path, brush);
    }

    internal static void FillPathAntialiased(Graphics g, GraphicsPath path, Brush brush)
    {
        var smoothingMode = g.SmoothingMode;
        var pixelOffsetMode = g.PixelOffsetMode;
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawPath(edge, path);
            g.FillPath(brush, path);
        }
        finally
        {
            g.SmoothingMode = smoothingMode;
            g.PixelOffsetMode = pixelOffsetMode;
        }
    }

    private void DrawPathGradientFill(Graphics g, VectorScene scene, int objectIndex)
    {
        var estimatedWidth = scene.EstimateGradientPathStrokeWidth(objectIndex);
        if (!scene.TryGetGradientPathWorldPoints(objectIndex, out var pathPoints)
            || pathPoints.Length < 2
            || estimatedWidth is not > 0)
        {
            return;
        }

        var screenLength = 0f;
        for (var index = 1; index < pathPoints.Length; index++)
        {
            screenLength += Distance(WorldToScreen(pathPoints[index - 1]), WorldToScreen(pathPoints[index]));
        }

        var segments = GradientPaintUtilities.CreatePathGradientSegments(
            pathPoints,
            scene.GetGradientStops(objectIndex),
            Math.Clamp((int)MathF.Ceiling(screenLength / 4f), 8, 256));
        if (segments.Length == 0) return;

        using var mask = CreateObjectBoundaryPath(scene, objectIndex);

        if (mask.PointCount == 0) return;
        var width = Math.Max(1f, WorldLengthToScreen(estimatedWidth * 1.12f));
        var smoothingMode = g.SmoothingMode;
        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(mask, CombineMode.Intersect);
            foreach (var segment in segments)
            {
                var start = WorldToScreen(segment.Start);
                var end = WorldToScreen(segment.End);
                if (Distance(start, end) < 0.1f) continue;
                using var brush = new LinearGradientBrush(
                    start,
                    end,
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
                g.DrawLine(pen, start, end);
            }
        }
        finally
        {
            g.Restore(state);
            g.SmoothingMode = smoothingMode;
        }

        using var edgeBrush = CreateGradientFillBrush(scene, objectIndex);
        ReinforcePathEdge(g, mask, edgeBrush);
    }

    private void DrawRadialGradientFill(
        Graphics g,
        GraphicsPath path,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        var center = WorldToScreen(scene.GetGradientStart(objectIndex));
        var radiusPoint = WorldToScreen(scene.GetGradientEnd(objectIndex));
        var radius = Math.Max(1f, Distance(center, radiusPoint));
        using var outer = new SolidBrush(Color.FromArgb(stops[^1].Argb));
        g.FillPath(outer, path);
        var state = g.Save();
        try
        {
            g.SetClip(path, CombineMode.Intersect);
            const int rings = 64;
            for (var ring = rings - 1; ring >= 0; ring--)
            {
                var position = ring / (float)(rings - 1);
                using var fill = new SolidBrush(GradientColorAt(stops, position));
                var ringRadius = Math.Max(0.5f, radius * position);
                g.FillEllipse(fill, center.X - ringRadius, center.Y - ringRadius, ringRadius * 2, ringRadius * 2);
            }
        }
        finally
        {
            g.Restore(state);
        }
        using var edge = new SolidBrush(Color.FromArgb(stops[^1].Argb));
        ReinforcePathEdge(g, path, edge);
    }

    private static void ReinforcePathEdge(Graphics graphics, GraphicsPath path, Brush brush)
    {
        var smoothingMode = graphics.SmoothingMode;
        var pixelOffsetMode = graphics.PixelOffsetMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawPath(edge, path);
        }
        finally
        {
            graphics.SmoothingMode = smoothingMode;
            graphics.PixelOffsetMode = pixelOffsetMode;
        }
    }

    private void DrawShapeRadialGradientFill(
        Graphics g,
        GraphicsPath path,
        VectorScene scene,
        int objectIndex,
        IReadOnlyList<GradientStop> stops)
    {
        try
        {
            using var mappingPath = new GraphicsPath(FillMode.Alternate);
            foreach (var contour in scene.GetShapeGradientMappingContours(objectIndex))
            {
                if (contour.Length >= 3) mappingPath.AddPolygon(contour.Select(WorldToScreen).ToArray());
            }
            if (mappingPath.PointCount == 0)
            {
                DrawRadialGradientFill(g, path, scene, objectIndex, stops);
                return;
            }

            using var brush = new PathGradientBrush(mappingPath)
            {
                CenterPoint = WorldToScreen(scene.GetGradientStart(objectIndex)),
                InterpolationColors = new ColorBlend
                {
                    Colors = stops.Select(stop => Color.FromArgb(stop.Argb)).ToArray(),
                    Positions = stops.Select(stop => stop.Position).ToArray()
                }
            };
            FillPathAntialiased(g, path, brush);
        }
        catch (ArgumentException)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, stops);
        }
        catch (OutOfMemoryException)
        {
            DrawRadialGradientFill(g, path, scene, objectIndex, stops);
        }
    }

    private static Color GradientColorAt(IReadOnlyList<GradientStop> stops, float position)
    {
        position = Math.Clamp(position, 0f, 1f);
        var previous = stops[0];
        foreach (var current in stops.Skip(1))
        {
            if (position <= current.Position)
            {
                var length = Math.Max(0.0001f, current.Position - previous.Position);
                var amount = Math.Clamp((position - previous.Position) / length, 0f, 1f);
                var from = Color.FromArgb(previous.Argb);
                var to = Color.FromArgb(current.Argb);
                return Color.FromArgb(
                    (int)MathF.Round(from.A + (to.A - from.A) * amount),
                    (int)MathF.Round(from.R + (to.R - from.R) * amount),
                    (int)MathF.Round(from.G + (to.G - from.G) * amount),
                    (int)MathF.Round(from.B + (to.B - from.B) * amount));
            }

            previous = current;
        }

        return Color.FromArgb(stops[^1].Argb);
    }

    private void DrawFreehandStroke(Graphics g, int i, PointF[] localPoints, Color color, float stroke)
    {
        if (localPoints.Length == 0) return;
        var screenWidth = Math.Max(0.75f, WorldLengthToScreen(stroke));
        if (localPoints.Length == 1)
        {
            var point = WorldToScreen(LocalToWorld(i, localPoints[0]));
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, point.X - screenWidth * 0.5f, point.Y - screenWidth * 0.5f, screenWidth, screenWidth);
            return;
        }

        using var pen = StrokePen(color, screenWidth);
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var previous = WorldToScreen(LocalToWorld(i, localPoints[0]));
        for (var p = 1; p < localPoints.Length; p++)
        {
            var current = WorldToScreen(LocalToWorld(i, localPoints[p]));
            g.DrawLine(pen, previous, current);
            previous = current;
        }

        g.SmoothingMode = oldMode;
    }

    private void DrawMixingStroke(Graphics graphics, VectorScene scene, int objectIndex)
    {
        var raster = MixingBrushRasterFor(scene, objectIndex);
        if (raster is null) return;

        var bounds = raster.LocalBounds;
        var destination = new[]
        {
            WorldToScreen(ObjectLocalToWorld(scene, objectIndex, new PointF(bounds.Left, bounds.Top))),
            WorldToScreen(ObjectLocalToWorld(scene, objectIndex, new PointF(bounds.Right, bounds.Top))),
            WorldToScreen(ObjectLocalToWorld(scene, objectIndex, new PointF(bounds.Left, bounds.Bottom)))
        };
        var state = graphics.Save();
        try
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                raster.Bitmap,
                destination,
                new RectangleF(0, 0, raster.PixelWidth, raster.PixelHeight),
                GraphicsUnit.Pixel);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    internal MixingBrushRegionRaster? MixingBrushRasterFor(
        VectorScene scene,
        int objectIndex)
    {
        if ((uint)objectIndex >= scene.ObjectCount
            || scene.ShapeKind[objectIndex] != ShapeKind.MixingStroke)
        {
            return null;
        }

        var scaleBucket = MixingBrushRegionRasterizer.ResolveScaleBucket(WorldLengthToScreen(1));
        return _mixingBrushRasterCache.GetOrCreate(
            scene,
            objectIndex,
            scene.GeometryRevision,
            scaleBucket,
            scale =>
            {
                if (scene.TryGetMixingBrushLocalRegion(objectIndex, out var region))
                {
                    var vertices = region.Vertices
                        .Select(vertex => new MixingBrushRasterVertex(vertex.Point, vertex.Argb))
                        .ToArray();
                    return MixingBrushRegionRasterizer.RasterizeMesh(
                        vertices,
                        region.TriangleIndices,
                        scale);
                }

                if (!scene.TryGetMixingStrokeLocalSamples(objectIndex, out var samples)
                    || samples.Length == 0)
                {
                    return null;
                }
                return MixingBrushRegionRasterizer.RasterizeTrajectory(
                    MixingStrokeCoverageBuilder.GetLocalCells(samples),
                    scale);
            });
    }

    private void DrawLocalShape(
        Graphics g,
        ShapeKind shape,
        int shapeVertexCount,
        Brush brush,
        Color strokeColor,
        float stroke,
        float screenStroke,
        float w,
        float h,
        SceneRenderPass pass)
    {
        var rect = new RectangleF(-w * 0.5f, -h * 0.5f, w, h);
        switch (shape)
        {
            case ShapeKind.Ellipse:
                if (pass == SceneRenderPass.Fill)
                {
                    using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
                    g.DrawEllipse(edge, rect);
                    g.FillEllipse(brush, rect);
                }
                else if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawEllipse(pen, rect);
                }
                break;
            case ShapeKind.Line:
                if (pass == SceneRenderPass.Stroke)
                {
                    using var linePen = StrokePen(strokeColor, screenStroke);
                    g.DrawLine(linePen, -w * 0.5f, 0, w * 0.5f, 0);
                }
                break;
            case ShapeKind.Triangle:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(3, w, h, -MathF.PI / 2), pass);
                break;
            case ShapeKind.Polygon:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, RegularPolygonPoints(PolygonVertexCount(shapeVertexCount), w, h, -MathF.PI / 2), pass);
                break;
            case ShapeKind.Star:
                DrawPolygonShape(g, brush, strokeColor, stroke, screenStroke, StarPoints(StarVertexCount(shapeVertexCount), w, h, -MathF.PI / 2), pass);
                break;
            default:
                if (pass == SceneRenderPass.Fill)
                {
                    using var edge = new Pen(brush, FillEdgeCoverageWidthPixels);
                    g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
                    g.FillRectangle(brush, rect);
                }
                else if (stroke > 0 && w > 4 && h > 4)
                {
                    using var pen = StrokePen(strokeColor, screenStroke);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                break;
        }
    }

    private static void DrawPolygonShape(Graphics g, Brush brush, Color strokeColor, float stroke, float screenStroke, PointF[] points, SceneRenderPass pass)
    {
        if (pass == SceneRenderPass.Fill)
        {
            using var edge = new Pen(brush, FillEdgeCoverageWidthPixels)
            {
                LineJoin = LineJoin.Round
            };
            g.DrawPolygon(edge, points);
            g.FillPolygon(brush, points);
            return;
        }

        if (stroke <= 0) return;
        using var pen = StrokePen(strokeColor, screenStroke);
        g.DrawPolygon(pen, points);
    }

    private Color StrokeColorFor(int objectIndex)
    {
        return Scene.StrokeArgb.Length > objectIndex
            ? Color.FromArgb(Scene.StrokeArgb[objectIndex])
            : Color.FromArgb(238, 242, 241);
    }

    private static Pen StrokePen(Color color, float width)
    {
        return new Pen(color, Math.Max(1.5f, width))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
    }

    private static Pen SelectionPen(Color color, float width)
    {
        return new Pen(color, width)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
    }

    private static PointF[] RegularPolygonPoints(int sides, float w, float h, float startAngle)
    {
        var points = new PointF[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * MathF.Tau / sides;
            points[i] = new PointF(MathF.Cos(angle) * w * 0.5f, MathF.Sin(angle) * h * 0.5f);
        }

        return points;
    }

    private static PointF[] StarPoints(int points, float w, float h, float startAngle)
    {
        var result = new PointF[points * 2];
        for (var i = 0; i < result.Length; i++)
        {
            var radius = i % 2 == 0 ? 0.5f : 0.23f;
            var angle = startAngle + i * MathF.Tau / result.Length;
            result[i] = new PointF(MathF.Cos(angle) * w * radius, MathF.Sin(angle) * h * radius);
        }

        return result;
    }

    private static int PolygonVertexCount(int value) => Math.Clamp(value == 0 ? 6 : value, 3, 64);

    private static int StarVertexCount(int value) => Math.Clamp(value == 0 ? 5 : value, 3, 32);

    private void DrawGrid(Graphics g)
    {
        if (_worldGridOpacity <= 0.001f) return;
        if (RendersReferenceProjection)
        {
            if (PlanarWorldGridOpacity > 0.001f) DrawPlanarGrid(g);
            if (ReferenceWorldGridOpacity > 0.001f) Draw3DReferenceGrid(g);
            return;
        }
        DrawPlanarGrid(g);
    }

    private void DrawPlanarGrid(Graphics g)
    {
        if (PlanarWorldGridOpacity <= 0.001f) return;
        if (_worldGridType == VectorAnimationEngine.WorldGridType.GoldenSpiral)
        {
            DrawGoldenSpiralGrid(g);
            return;
        }
        if (_worldGridType == VectorAnimationEngine.WorldGridType.Polar)
        {
            DrawPolarGrid(g);
            return;
        }

        var scale = CurrentWorldGridScale;
        var bounds = VisibleWorldBounds();
        var origin = WorldToScreen(0, 0);
        DrawWorldGridLines(g, scale, bounds.Left, bounds.Right, vertical: true);
        DrawWorldGridLines(g, scale, bounds.Top, bounds.Bottom, vertical: false);

        if (origin.Y >= 0 && origin.Y <= Height)
        {
            _gridPen.Color = Color.FromArgb(PlanarGridAlpha(205), 214, 82, 82);
            _gridPen.Width = 1.6f;
            g.DrawLine(_gridPen, 0, origin.Y, Width, origin.Y);
        }
        if (origin.X >= 0 && origin.X <= Width)
        {
            _gridPen.Color = Color.FromArgb(PlanarGridAlpha(205), 82, 190, 122);
            _gridPen.Width = 1.6f;
            g.DrawLine(_gridPen, origin.X, 0, origin.X, Height);
        }
        if (origin.X >= 0 && origin.X <= Width && origin.Y >= 0 && origin.Y <= Height)
        {
            var previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(BrushFor(Color.FromArgb(PlanarGridAlpha(230), 224, 232, 234).ToArgb()), origin.X - 3, origin.Y - 3, 6, 6);
            _gridPen.Color = Color.FromArgb(PlanarGridAlpha(235), 22, 26, 29);
            _gridPen.Width = 1.2f;
            g.DrawEllipse(_gridPen, origin.X - 4.5f, origin.Y - 4.5f, 9, 9);
            g.SmoothingMode = previousSmoothing;
        }
    }

    private void DrawGoldenSpiralGrid(Graphics graphics)
    {
        var geometry = ResolveGoldenSpiralGrid();
        if (geometry.SpiralPoints.Length < 2) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var guidePen = new Pen(Color.FromArgb(PlanarGridAlpha(82), 118, 128, 134), 1f);
        using var spiralPen = new Pen(Color.FromArgb(PlanarGridAlpha(205), 232, 194, 86), 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        foreach (var segment in geometry.GuideSegments)
        {
            graphics.DrawLine(
                guidePen,
                WorldToScreen(segment.Start.X, segment.Start.Y),
                WorldToScreen(segment.End.X, segment.End.Y));
        }
        var spiral = new PointF[geometry.SpiralPoints.Length];
        for (var index = 0; index < spiral.Length; index++)
        {
            var point = geometry.SpiralPoints[index];
            spiral[index] = WorldToScreen(point.X, point.Y);
        }
        graphics.DrawLines(spiralPen, spiral);
        graphics.SmoothingMode = previousSmoothing;
    }

    private void DrawPolarGrid(Graphics graphics)
    {
        var geometry = ResolvePolarGrid();
        if (geometry.Circles.Length == 0) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var origin = WorldToScreen(geometry.Origin.X, geometry.Origin.Y);
        var scale = CurrentWorldGridScale;
        var visibleBounds = VisibleWorldBounds();
        var arcOverscan = ScreenLengthToWorld(2f);
        visibleBounds.Inflate(arcOverscan, arcOverscan);
        var maximumFastEllipseRadius = Math.Max(Width, Height) * 2f;
        Span<PolarGridArc> visibleArcs = stackalloc PolarGridArc[PolarGridLayout.MaximumVisibleArcsPerCircle];
        foreach (var circle in geometry.Circles)
        {
            var style = WorldGridLayout.ResolveLineStyle(circle.Index, scale, PlanarWorldGridOpacity);
            if (style.Color.A == 0) continue;
            var radius = WorldLengthToScreen(circle.Radius);
            _gridPen.Color = style.Color;
            _gridPen.Width = style.Width;
            if (radius <= maximumFastEllipseRadius)
            {
                graphics.DrawEllipse(_gridPen, origin.X - radius, origin.Y - radius, radius * 2, radius * 2);
                continue;
            }

            var arcCount = PolarGridLayout.ResolveVisibleArcs(visibleBounds, circle.Radius, visibleArcs);
            if (arcCount == 0) continue;
            using var path = new GraphicsPath();
            for (var arcIndex = 0; arcIndex < arcCount; arcIndex++)
            {
                var arc = visibleArcs[arcIndex];
                var segmentCount = PolarGridLayout.ResolveArcSegmentCount(radius, arc.SweepAngle);
                var previous = PolarArcScreenPoint(origin, radius, arc.StartAngle);
                path.StartFigure();
                for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
                {
                    var angle = arc.StartAngle + arc.SweepAngle * segmentIndex / segmentCount;
                    var current = PolarArcScreenPoint(origin, radius, angle);
                    path.AddLine(previous, current);
                    previous = current;
                }
            }
            graphics.DrawPath(_gridPen, path);
        }

        for (var index = 0; index < geometry.DiameterSegments.Length; index++)
        {
            if (!PolarGridLayout.TryClipSegment(visibleBounds, geometry.DiameterSegments[index], out var segment)) continue;
            _gridPen.Color = index switch
            {
                0 => Color.FromArgb(PlanarGridAlpha(205), 214, 82, 82),
                PolarGridLayout.DiameterCount / 2 => Color.FromArgb(PlanarGridAlpha(205), 82, 190, 122),
                _ => Color.FromArgb(PlanarGridAlpha(index % 3 == 0 ? 112 : 72), 92, 104, 112)
            };
            _gridPen.Width = index is 0 or PolarGridLayout.DiameterCount / 2
                ? 1.6f
                : index % 3 == 0 ? 1.15f : 0.8f;
            graphics.DrawLine(
                _gridPen,
                WorldToScreen(segment.Start.X, segment.Start.Y),
                WorldToScreen(segment.End.X, segment.End.Y));
        }

        if (origin.X >= 0 && origin.X <= Width && origin.Y >= 0 && origin.Y <= Height)
        {
            graphics.FillEllipse(BrushFor(Color.FromArgb(PlanarGridAlpha(230), 224, 232, 234).ToArgb()), origin.X - 3, origin.Y - 3, 6, 6);
            _gridPen.Color = Color.FromArgb(PlanarGridAlpha(235), 22, 26, 29);
            _gridPen.Width = 1.2f;
            graphics.DrawEllipse(_gridPen, origin.X - 4.5f, origin.Y - 4.5f, 9, 9);
        }
        graphics.SmoothingMode = previousSmoothing;
    }

    private static PointF PolarArcScreenPoint(PointF origin, float radius, float angle)
    {
        return new PointF(
            (float)(origin.X + Math.Cos(angle) * radius),
            (float)(origin.Y + Math.Sin(angle) * radius));
    }

    private void DrawWorldGridLines(
        Graphics graphics,
        WorldGridScale scale,
        float minimumWorld,
        float maximumWorld,
        bool vertical)
    {
        var (first, last) = WorldGridLayout.VisibleIndexRange(minimumWorld, maximumWorld, scale.StepWorld);
        for (var index = first; index <= last; index++)
        {
            if (index == 0) continue;
            var style = WorldGridLayout.ResolveLineStyle(index, scale, PlanarWorldGridOpacity);
            if (style.Color.A == 0) continue;
            var world = index * scale.StepWorld;
            var position = vertical ? WorldToScreen((float)world, 0).X : WorldToScreen(0, (float)world).Y;
            if (vertical && (position < -style.Width || position > Width + style.Width)
                || !vertical && (position < -style.Width || position > Height + style.Width))
            {
                continue;
            }

            _gridPen.Color = style.Color;
            _gridPen.Width = style.Width;
            if (vertical) graphics.DrawLine(_gridPen, position, 0, position, Height);
            else graphics.DrawLine(_gridPen, 0, position, Width, position);
        }
    }

    private void Draw3DReferenceGrid(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var horizonPen = new Pen(Color.FromArgb(ReferenceGridAlpha(40), 112, 204, 255), 1);
        using var gridPen = new Pen(Color.FromArgb(ReferenceGridAlpha(70), 72, 84, 92), 1);
        using var centerPen = new Pen(Color.FromArgb(ReferenceGridAlpha(110), 150, 164, 174), 1.3f);
        using var xPen = new Pen(Color.FromArgb(ReferenceGridAlpha(210), 255, 92, 92), 2);
        using var yPen = new Pen(Color.FromArgb(ReferenceGridAlpha(210), 122, 224, 92), 2);
        using var zPen = new Pen(Color.FromArgb(ReferenceGridAlpha(210), 92, 172, 255), 2);

        var horizonY = Height * 0.42f;
        g.DrawLine(horizonPen, 0, horizonY, Width, horizonY);

        var step = InfiniteGridStep();
        var lineRadius = InfiniteGridLineRadius();
        var centerX = SnapToGrid(_referenceTargetX, step);
        var centerY = SnapToGrid(_referenceTargetY, step);
        var minX = centerX - lineRadius * step;
        var maxX = centerX + lineRadius * step;
        var minY = centerY - lineRadius * step;
        var maxY = centerY + lineRadius * step;

        for (var offset = -lineRadius; offset <= lineRadius; offset++)
        {
            var y = centerY + offset * step;
            var x = centerX + offset * step;
            DrawProjectedLine(g, new Point3(minX, y, 0), new Point3(maxX, y, 0), Math.Abs(y) < 0.001f ? centerPen : gridPen);
            DrawProjectedLine(g, new Point3(x, minY, 0), new Point3(x, maxY, 0), Math.Abs(x) < 0.001f ? centerPen : gridPen);
        }

        DrawProjectedLine(g, new Point3(minX, 0, 0), new Point3(maxX, 0, 0), xPen);
        DrawProjectedLine(g, new Point3(0, minY, 0), new Point3(0, maxY, 0), yPen);
        DrawProjectedLine(g, new Point3(0, 0, -5000), new Point3(0, 0, 5000), zPen);
        DrawAxisLabel(g, "X", new Point3(maxX, 0, 0), xPen.Color);
        DrawAxisLabel(g, "Y", new Point3(0, minY, 0), yPen.Color);
        DrawAxisLabel(g, "Z", new Point3(0, 0, 5000), zPen.Color);
    }

    private void DrawProjectedLine(Graphics g, Point3 a, Point3 b, Pen pen)
    {
        var ca = CameraSpacePoint(a);
        var cb = CameraSpacePoint(b);
        if (!ClipNear(ref ca, ref cb)) return;
        var pa = ProjectCameraPoint(ca);
        var pb = ProjectCameraPoint(cb);
        if (!LineMayTouchViewport(pa, pb)) return;
        g.DrawLine(pen, pa, pb);
    }

    private void DrawAxisLabel(Graphics g, string text, Point3 point, Color color)
    {
        var projected = ProjectReferencePoint(point);
        if (projected is null) return;
        using var brush = new SolidBrush(color);
        g.DrawString(text, Font, brush, projected.Value.X + 6, projected.Value.Y - 16);
    }

    private PointF? ProjectReferencePoint(Point3 point)
    {
        var cameraPoint = CameraSpacePoint(point);
        if (cameraPoint.Z < 120) return null;
        return ProjectCameraPoint(cameraPoint);
    }

    private Point3 CameraSpacePoint(Point3 point)
    {
        var yawCos = MathF.Cos(EffectiveReferenceYaw);
        var yawSin = MathF.Sin(EffectiveReferenceYaw);
        var pitchCos = MathF.Cos(EffectiveReferencePitch);
        var pitchSin = MathF.Sin(EffectiveReferencePitch);

        var worldX = point.X - _referenceTargetX;
        var worldY = point.Y - _referenceTargetY;
        var worldZ = point.Z - _referenceTargetZ;
        var x = worldX * yawCos - worldZ * yawSin;
        var z = worldX * yawSin + worldZ * yawCos;
        var y = worldY;
        var py = y * pitchCos - z * pitchSin;
        var pz = y * pitchSin + z * pitchCos + _referenceDistance;
        return new Point3(x, py, pz);
    }

    private PointF ProjectCameraPoint(Point3 point)
    {
        var scale = 0.035f * _referenceZoomScale;
        var perspective = ReferencePerspectiveScale(point.Z);
        return new PointF(
            Width * 0.5f + point.X * scale * perspective,
            Height * 0.58f - point.Y * scale * perspective);
    }

    private static bool ClipNear(ref Point3 a, ref Point3 b)
    {
        const float near = 120f;
        var aInside = a.Z >= near;
        var bInside = b.Z >= near;
        if (!aInside && !bInside) return false;
        if (aInside && bInside) return true;
        var t = Math.Clamp((near - a.Z) / (b.Z - a.Z), 0, 1);
        var clipped = new Point3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);
        if (aInside) b = clipped;
        else a = clipped;
        return true;
    }

    private bool LineMayTouchViewport(PointF a, PointF b)
    {
        const float margin = 320;
        var minX = MathF.Min(a.X, b.X);
        var maxX = MathF.Max(a.X, b.X);
        var minY = MathF.Min(a.Y, b.Y);
        var maxY = MathF.Max(a.Y, b.Y);
        return maxX >= -margin && minX <= Width + margin && maxY >= -margin && minY <= Height + margin;
    }

    private int InfiniteGridLineRadius()
    {
        return Math.Clamp((int)MathF.Ceiling(Math.Max(Width, Height) / 14f), 64, 180);
    }

    private float InfiniteGridStep()
    {
        var raw = _referenceDistance / Math.Max(0.35f, _referenceZoomScale) / 10f;
        if (raw <= 750) return 500;
        if (raw <= 1500) return 1000;
        if (raw <= 3500) return 2000;
        if (raw <= 7000) return 5000;
        return 10000;
    }

    private static float SnapToGrid(float value, float step) => MathF.Round(value / step) * step;

    private int PlanarGridAlpha(int alpha) =>
        (int)MathF.Round(Math.Clamp(alpha * PlanarWorldGridOpacity, 0f, 255f));

    private int ReferenceGridAlpha(int alpha) =>
        (int)MathF.Round(Math.Clamp(alpha * ReferenceWorldGridOpacity, 0f, 255f));

    private readonly record struct Point3(float X, float Y, float Z);
}
