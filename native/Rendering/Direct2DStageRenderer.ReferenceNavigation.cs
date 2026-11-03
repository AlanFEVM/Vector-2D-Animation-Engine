using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly List<(ID2D1CommandList? Commands, Reference3DRenderItem Raster, RectangleF Bounds)> _referenceNavigation = [];
    private ID2D1StrokeStyle1? _referenceNavigationFixedEdgeStyle;

    private bool TryRecordReferenceNavigationFillEdge(ID2D1Geometry geometry, ID2D1Brush brush, float width)
    {
        if (!_recordingReferenceNavigation) return false;
        _referenceNavigationFixedEdgeStyle ??= _factory!.CreateStrokeStyle(new StrokeStyleProperties1
        { TransformType = StrokeTransformType.Fixed, MiterLimit = 10 });
        _target!.DrawGeometry(geometry, brush, width, _referenceNavigationFixedEdgeStyle);
        return true;
    }
    private readonly List<ID2D1GeometryRealization> _referenceNavigationRealizations = [];
    private readonly Dictionary<(IntPtr Geometry, bool Fill, float Width, IntPtr Style, float Tolerance),
        (ID2D1Geometry Source, ID2D1GeometryRealization Realization)> _retainedNavigationRealizations = [];
    private object? _navigationRealizationContentKey;
    private readonly Dictionary<int, ID2D1PathGeometry?> _instanceMoveSelectionGeometry = [];
    private bool _recordingReferenceNavigation;

    private bool TryRecordReferenceNavigationGeometry(ID2D1Geometry geometry, ID2D1Brush brush,
        bool fill, float width = 0, ID2D1StrokeStyle? style = null)
    {
        if (!_recordingReferenceNavigation || _deviceContext2 is null) return false;
        // Retain device-side tessellation at a tolerance sufficient for the
        // maximum supported navigation zoom, rather than re-tessellating paths
        // every time a command list is replayed at a new scale.
        var transform = _target!.Transform;
        var transformScale = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12
            + transform.M21 * transform.M21 + transform.M22 * transform.M22);
        var tolerance = 0.2f / Math.Max(1, transformScale * 8 / _referenceNavigationZoom);
        var key = (geometry.NativePointer, fill, width, style?.NativePointer ?? IntPtr.Zero, tolerance);
        if (_retainedNavigationRealizations.TryGetValue(key, out var cached))
        {
            _deviceContext2.DrawGeometryRealization(cached.Realization, brush);
            return true;
        }
        var realization = fill
            ? _deviceContext2.CreateFilledGeometryRealization(geometry, tolerance)
            : _deviceContext2.CreateStrokedGeometryRealization(geometry, tolerance, width, style);
        if (_retainedNavigationRealizations.Count < 8192)
        {
            // Hold a COM reference to immutable source geometry so its pointer
            // cannot be recycled for different geometry while this entry lives.
            try { _retainedNavigationRealizations.Add(key, (geometry.QueryInterface<ID2D1Geometry>(), realization)); }
            catch { realization.Dispose(); throw; }
        }
        else _referenceNavigationRealizations.Add(realization);
        _deviceContext2.DrawGeometryRealization(realization, brush);
        return true;
    }
    private object? _referenceNavigationKey;
    private object? _referenceNavigationRejectedKey;
    private PointF _referenceNavigationOrigin;
    private float _referenceNavigationZoom;
    private RenderStats _referenceNavigationStats;
    private float _referenceNavigationFilterZoom;
    private VectorScene? _navigationFilterScene;
    private long _navigationFilterRevision = -1;
    private bool _navigationHasRasterFilters;
    internal long ReferenceNavigationBuilds { get; private set; }
    internal long ReferenceNavigationReuses { get; private set; }
    internal string? ReferenceNavigationFallbackReason { get; private set; }

    private void ClearReferenceNavigationCommands(bool preserveRealizations = false)
    {
        ClearReferenceNavigationPreview();
        _referenceColorRoot?.Dispose();
        _referenceColorRoot = null;
        _referenceColorContentKey = null;
        _instanceMoveSelectionImage?.Dispose();
        _instanceMoveSelectionImage = null;
        foreach (var geometry in _instanceMoveSelectionGeometry.Values) geometry?.Dispose();
        _instanceMoveSelectionGeometry.Clear();
        foreach (var entry in _referenceNavigation) entry.Commands?.Dispose();
        foreach (var realization in _referenceNavigationRealizations) realization.Dispose();
        _referenceNavigationRealizations.Clear();
        if (!preserveRealizations)
        {
            foreach (var entry in _retainedNavigationRealizations.Values)
            { entry.Realization.Dispose(); entry.Source.Dispose(); }
            _retainedNavigationRealizations.Clear();
            _navigationRealizationContentKey = null;
        }
        _referenceNavigationFixedEdgeStyle?.Dispose();
        _referenceNavigationFixedEdgeStyle = null;
        _referenceNavigation.Clear();
        _referenceNavigationKey = null;
        _referenceNavigationRejectedKey = null;
    }

    private bool TryDrawReferenceNavigation(StageControl stage, out RenderStats stats)
    {
        stats = default;
        if (_target is not ID2D1DeviceContext context || !stage.CanRetainReferenceNavigation
            || stage.PlaybackActive || stage.OnionSkinScene is not null || stage.UnderlayScene is not null
            || stage.DragPreviewScene is not null || stage.Scene.RequiresIsolatedLayerCompositing && !CanDrawReferenceColorGroups(stage)
            || stage.CompositionMasksFor(stage.Scene) is not null
            || stage.SceneCompositionMaskClips.Count > 0
            || !stage.TryProjectScenePosition(Vector3.Zero, out var origin, out _))
        {
            ClearReferenceNavigationCommands();
            ReferenceNavigationFallbackReason = "view_or_compositing";
            return false;
        }
        object key = stage.InstanceMovePreviewObjects is not null
            ? (stage, stage.Scene, stage.BasePresentationRevision, stage.InstanceMovePreviewEpoch, stage.Scene.GeometryRevision,
                stage.Scene.SummaryRevision, stage.Scene.ActiveContentRevision, stage.Frame, stage.ClientSize)
            : (stage, stage.CreateReferenceNavigationContentKey(), stage.Scene.ActiveContentRevision, stage.PlanarCanvasRotation, stage.InstanceMovePreviewEpoch);
        if (!ReferenceEquals(_navigationFilterScene, stage.Scene) || _navigationFilterRevision != stage.Scene.GeometryRevision)
        {
            _navigationFilterScene = stage.Scene;
            _navigationFilterRevision = stage.Scene.GeometryRevision;
            _navigationHasRasterFilters = Enumerable.Range(0, stage.Scene.LayerCount).Any(layer =>
            {
                var filters = stage.Scene.GetLayerSymbolFilters(layer);
                var color = filters.ColorTransform;
                return (filters with { ColorTransform = default }).HasEnabled
                    || color.Enabled && (color.RedOffset != 0 || color.GreenOffset != 0 || color.BlueOffset != 0
                        || color.RedMultiplier < 0 || color.GreenMultiplier < 0 || color.BlueMultiplier < 0);
            });
        }
        if (_navigationHasRasterFilters)
        {
            // Keep filtered snapshots through navigation input; rebuild their
            // native-resolution kernels when the viewport settles at a new zoom.
            if (!stage.ReferenceNavigationPreviewActive || _referenceNavigationKey is null)
                _referenceNavigationFilterZoom = stage.ReferenceZoomScale;
            key = (key, _referenceNavigationFilterZoom);
        }
        _deviceContext2 ??= TryQueryDeviceContext2(context);
        if (_deviceContext2 is null) return false;
        if (Equals(key, _referenceNavigationRejectedKey)) return false;
        if (!Equals(key, _referenceNavigationKey))
        {
            object colorContentKey = (stage.CreateReferenceNavigationContentKey(), stage.Scene.ActiveContentRevision,
                stage.PlanarCanvasRotation);
            // A drag only changes the partition and translation. Keep immutable
            // vector commands and exact filter results prepared for this view.
            if (Equals(colorContentKey, _referenceColorContentKey)
                && _referenceNavigationZoom == stage.ReferenceZoomScale && origin == _referenceNavigationOrigin
                && TryRepartitionReferenceColorGroups(stage))
            {
                ClearReferenceNavigationPreview();
                _referenceNavigationKey = key;
            }
            else
            {
                object realizationKey = (stage.Scene, stage.Scene.GeometryRevision, stage.Scene.SummaryRevision,
                    stage.Scene.ActiveContentRevision, stage.Frame, stage.ClientSize, stage.SceneCompositionResult);
                ClearReferenceNavigationCommands(preserveRealizations: Equals(realizationKey, _navigationRealizationContentKey));
                _navigationRealizationContentKey = realizationKey;
                var scene = stage.Scene;
                // Retain vector commands only. SVGs stay in the ordered stream and
                // are resolved at the current zoom, so their raster quality never
                // depends on the zoom at which the command list was recorded.
                for (var index = 0; index < scene.ObjectCount; index++)
                    if (scene.ShapeKind[index] is not (ShapeKind.Freeform or ShapeKind.Path or ShapeKind.ImportedSvg or ShapeKind.Text)
                        || scene.HasGradient(index))
                    { ReferenceNavigationFallbackReason = $"shape_or_gradient:{index}:{scene.ShapeKind[index]}"; _referenceNavigationRejectedKey = key; return false; }
                if (stage.SceneCompositionResult is { } composition
                    && composition.ObjectPoses.Any(p => p.FlatToScene != Matrix4x4.Identity
                        || p.ExtrusionVector.X != 0 || p.ExtrusionVector.Y != 0))
                { ReferenceNavigationFallbackReason = "spatial_pose"; _referenceNavigationRejectedKey = key; return false; }
                Reference3DRenderItem[] items;
                stage.ReferenceNavigationRecording = true;
                try { items = stage.GetReference3DSceneRenderItems(); }
                finally { stage.ReferenceNavigationRecording = false; }
                if (items.Any(item => item.Kind is not (Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke or Reference3DRenderKind.Back)
                    || item.FragmentClip is not null || item.SecondaryObjectIndex >= 0
                    || item.MaterialOpacity < 0.999999f || item.OpticalSurface is not null))
                { ReferenceNavigationFallbackReason = "render_item"; _referenceNavigationRejectedKey = key; return false; }

                using var previousTarget = context.Target;
                var previousTransform = context.Transform;
                context.EndDraw().CheckError();
                try
                {
                    _referenceNavigationZoom = stage.ReferenceZoomScale;
                    _recordingReferenceNavigation = true;
                    if (stage.NavigationPreviewEnabled || stage.Scene.RequiresIsolatedLayerCompositing || stage.InstanceMovePreviewObjects is not null)
                    {
                        _referenceColorRoot = RecordReferenceColorGroups(stage, context);
                        _referenceColorContentKey = colorContentKey;
                    }
                    else
                    for (var start = 0; start < items.Length;)
                    {
                        if (IsRaster(items[start]))
                        {
                            _referenceNavigation.Add((null, items[start++], default));
                            continue;
                        }
                        var end = start + 1;
                        var batchSize = stage.NavigationPreviewEnabled ? 8 : 32;
                        while (end < items.Length && end - start < batchSize && !IsRaster(items[end])) end++;
                        var bounds = RectangleF.Empty;
                        for (var index = start; index < end; index++)
                            if (StageControl.TryGetReference3DProjectedBounds(items[index].Contours,
                                Math.Max(2, stage.GetReference3DStrokeWidth(items[index].ObjectIndex,
                                    scene.Stroke[items[index].ObjectIndex]) * 8), out var itemBounds))
                                bounds = bounds.IsEmpty ? itemBounds : RectangleF.Union(bounds, itemBounds);
                        var commands = context.CreateCommandList();
                        try
                        {
                            context.Target = commands;
                            context.Transform = Matrix3x2.Identity;
                            context.BeginDraw();
                            try
                            {
                                for (var index = start; index < end; index++)
                                    DrawReference3DSceneItemUnmasked(stage, items[index]);
                            }
                            finally { context.EndDraw().CheckError(); }
                            commands.Close().CheckError();
                            _referenceNavigation.Add((commands, default, bounds));
                        }
                        catch { commands.Dispose(); throw; }
                        start = end;
                    }
                    _referenceNavigationKey = key;
                    stage.TryProjectScenePosition(Vector3.Zero, out _referenceNavigationOrigin, out _);
                    _referenceNavigationZoom = stage.ReferenceZoomScale;
                    var indices = items.Select(item => item.ObjectIndex).Distinct().ToArray();
                    _referenceNavigationStats = new RenderStats(indices.Length, indices.Length,
                        indices.Sum(index => (long)scene.AtomCount[index]), 0, scene.ObjectCount, false);
                    ReferenceNavigationBuilds++;
                    ReferenceNavigationFallbackReason = null;

                    bool IsRaster(Reference3DRenderItem item) => scene.ShapeKind[item.ObjectIndex] == ShapeKind.ImportedSvg
                        && item.Kind is Reference3DRenderKind.FrontFill or Reference3DRenderKind.FrontStroke;
                }
                catch { ClearReferenceNavigationCommands(); throw; }
                finally
                {
                    _recordingReferenceNavigation = false;
                    context.Target = previousTarget;
                    context.Transform = previousTransform;
                    context.BeginDraw();
                }
            }
        }
        else ReferenceNavigationReuses++;

        var scale = stage.ReferenceZoomScale / _referenceNavigationZoom;
        var navigation = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(
            origin.X - _referenceNavigationOrigin.X * scale,
            origin.Y - _referenceNavigationOrigin.Y * scale);
        using var colorFrame = _referenceColorRoot is null || stage.InstanceMovePreviewObjects is null && CanReuseReferenceNavigationPreviewImage(stage, origin)
            ? null : PrepareReferenceColorFrame(stage, context, navigation);
        void DrawStream(RectangleF viewport)
        {
            var transform = context.Transform;
            try
            {
                if (colorFrame is not null)
                {
                    context.Transform = Matrix3x2.Identity;
                    context.DrawImage(colorFrame);
                    return;
                }
                foreach (var entry in _referenceNavigation)
                {
                    if (entry.Commands is { } commands)
                    {
                        var min = Vector2.Transform(new Vector2(entry.Bounds.Left, entry.Bounds.Top), navigation);
                        var max = Vector2.Transform(new Vector2(entry.Bounds.Right, entry.Bounds.Bottom), navigation);
                        if (!entry.Bounds.IsEmpty && (max.X < viewport.Left - 2 || max.Y < viewport.Top - 2 || min.X > viewport.Right + 2 || min.Y > viewport.Bottom + 2))
                            continue;
                        context.Transform = navigation;
                        context.DrawImage(commands);
                    }
                    else
                    {
                        context.Transform = Matrix3x2.Identity;
                        // The retained stream includes offscreen objects. Resolve
                        // SVG textures only when their current projected quad can
                        // contribute pixels to the viewport.
                        var contours = stage.GetReference3DProjectedContours(entry.Raster.ObjectIndex);
                        if (StageControl.TryGetReference3DProjectedBounds(contours, 2, out var bounds)
                            && (bounds.Right < viewport.Left || bounds.Bottom < viewport.Top || bounds.Left > viewport.Right || bounds.Top > viewport.Bottom))
                            continue;
                        DrawReference3DSceneItemUnmasked(stage, entry.Raster);
                    }
                }
            }
            finally { context.Transform = transform; }
        }
        RectangleF? GetContentBounds()
        {
            if (_referenceColorRoot is { } root)
            {
                var min = Vector2.Transform(new Vector2(root.ContentBounds.Left, root.ContentBounds.Top), navigation);
                var max = Vector2.Transform(new Vector2(root.ContentBounds.Right, root.ContentBounds.Bottom), navigation);
                return root.ContentBounds.IsEmpty ? null : RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y);
            }
            var bounds = RectangleF.Empty;
            foreach (var entry in _referenceNavigation)
            {
                RectangleF current;
                if (entry.Commands is not null)
                {
                    if (entry.Bounds.IsEmpty) return null;
                    var min = Vector2.Transform(new Vector2(entry.Bounds.Left, entry.Bounds.Top), navigation);
                    var max = Vector2.Transform(new Vector2(entry.Bounds.Right, entry.Bounds.Bottom), navigation);
                    current = RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y);
                }
                else
                {
                    var contours = stage.GetReference3DProjectedContours(entry.Raster.ObjectIndex);
                    if (!StageControl.TryGetReference3DProjectedBounds(contours, 2, out current)) return null;
                }
                bounds = bounds.IsEmpty ? current : RectangleF.Union(bounds, current);
            }
            return bounds.IsEmpty ? null : bounds;
        }
        if (stage.InstanceMovePreviewObjects is not null || !TryDrawReferenceNavigationPreview(stage, context, origin, DrawStream, GetContentBounds))
            DrawStream(new RectangleF(0, 0, stage.Width, stage.Height));
        stage.RecordEditableScenePass();
        stats = _referenceNavigationStats;
        return true;
    }
}
