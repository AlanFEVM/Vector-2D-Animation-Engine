using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private sealed class ReferenceColorNode : IDisposable
    {
        public ID2D1CommandList? Commands;
        public Reference3DRenderItem? Raster;
        public SymbolFlashColorTransform Color;
        public SymbolFilters RasterFilters;
        public ID2D1Bitmap1? FilteredImage;
        public RectangleF FilteredBounds;
        public bool Moving;
        public bool ContainsMoving;
        public bool Partitioned;
        public bool StaticBand;
        public int[] ObjectIndices = [];
        public ID2D1Bitmap1? StaticImage;
        public RectangleF ImageBounds;
        public RectangleF ContentBounds;
        public List<ReferenceColorNode> Children = [];
        public void Dispose() { FilteredImage?.Dispose(); StaticImage?.Dispose(); Commands?.Dispose(); foreach (var child in Children) child.Dispose(); }
    }
    private ReferenceColorNode? _referenceColorRoot;
    private object? _referenceColorContentKey;
    private sealed class ReferenceColorEligibility
    {
        public object? Key;
        public bool Allowed;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StageControl, ReferenceColorEligibility>
        ReferenceColorEligibilityCache = new();

    private bool TryRepartitionReferenceColorGroups(StageControl stage)
    {
        if (_referenceColorRoot is null || !CanPartition(_referenceColorRoot)) return false;
        Reset(_referenceColorRoot);
        return true;

        bool Selected(int index) => stage.InstanceMovePreviewObjects?.Contains(index) == true;
        bool CanPartition(ReferenceColorNode node) =>
            (node.ObjectIndices.Length == 0 || node.ObjectIndices.All(index => Selected(index) == Selected(node.ObjectIndices[0])))
            && node.Children.All(CanPartition);
        void Reset(ReferenceColorNode node)
        {
            node.StaticImage?.Dispose(); node.StaticImage = null;
            node.Moving = node.ObjectIndices.Length > 0 && Selected(node.ObjectIndices[0]);
            node.ContainsMoving = node.Moving;
            node.Partitioned = false;
            var children = new List<ReferenceColorNode>();
            foreach (var child in node.Children)
            {
                Reset(child);
                if (child.StaticBand)
                {
                    children.AddRange(child.Children);
                    child.Children = [];
                    child.Dispose();
                }
                else children.Add(child);
            }
            node.Children = children;
        }
    }
    // A zero-offset RGB multiplier commutes with premultiplication. Keep the
    // group boundary: applying it to individual overlapping paths is incorrect.
    internal static bool CanDrawReferenceColorGroups(StageControl stage)
    {
        var scene = stage.Scene;
        if (!stage.RendersReferenceProjection || !stage.CanRetainReferenceNavigation
            || stage.PlaybackActive || stage.UnderlayScene is not null || stage.OnionSkinScene is not null
            || stage.DragPreviewScene is not null || stage.CompositionMasksFor(scene) is not null
            || stage.SceneCompositionMaskClips.Count > 0) return false;
        if (stage.InstanceMovePreviewObjects is not null && stage.InstanceMovePreviewContentValid) return true;
        var entry = ReferenceColorEligibilityCache.GetValue(stage, _ => new());
        object key = (stage.CreateReferenceNavigationContentKey(), scene.ActiveContentRevision);
        if (Equals(key, entry.Key)) return entry.Allowed;
        entry.Allowed = Evaluate();
        entry.Key = key;
        return entry.Allowed;

        bool Evaluate()
        {
            for (var layer = 0; layer < scene.LayerCount; layer++)
            {
                if (scene.LayerBlendModes[layer] is not (LayerBlendMode.Normal or LayerBlendMode.Isolate)
                    || scene.LayerOpacity[layer] != 1 || scene.GetLayerKind(layer) == DrawingLayerKind.Mask
                    || scene.TryGetMaskLayerIndex(layer, out _)) return false;
                var filters = scene.GetLayerSymbolFilters(layer);
                var color = filters.ColorTransform;
                if (!filters.IsValid) return false;
            }
            for (var index = 0; index < scene.ObjectCount; index++)
                if (scene.ShapeKind[index] is not (ShapeKind.Freeform or ShapeKind.Path or ShapeKind.ImportedSvg or ShapeKind.Text) || scene.HasGradient(index)) return false;
            return stage.SceneCompositionResult is not { } composition
                || !composition.ObjectPoses.Any(p => p.FlatToScene != Matrix4x4.Identity
                    || p.ExtrusionVector.X != 0 || p.ExtrusionVector.Y != 0);
        }
    }

    private ReferenceColorNode RecordReferenceColorGroups(StageControl stage, ID2D1DeviceContext context)
    {
        var scene = stage.Scene;
        var children = new List<int>[scene.LayerCount + 1];
        for (var i = 0; i < children.Length; i++) children[i] = [];
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < scene.LayerCount; i++) ids[scene.LayerIds[i]] = i;
        for (var i = 0; i < scene.LayerCount; i++)
        {
            var parent = ids.TryGetValue(scene.LayerParentIds[i], out var candidate)
                && candidate != i && scene.GetLayerKind(candidate) == DrawingLayerKind.Folder ? candidate : scene.LayerCount;
            children[parent].Add(i);
        }
        return RecordGroup(scene.LayerCount);

        ID2D1CommandList Record(Action draw)
        {
            var commands = context.CreateCommandList();
            try
            {
                context.Target = commands;
                context.Transform = Matrix3x2.Identity;
                context.BeginDraw();
                try { draw(); }
                finally { context.EndDraw().CheckError(); }
                commands.Close().CheckError();
                return commands;
            }
            catch { commands.Dispose(); throw; }
        }

        ReferenceColorNode RecordGroup(int parent)
        {
            var node = new ReferenceColorNode();
            var pending = new List<int>();
            try
            {
                AppendChildren(parent);
                Flush();
                return node;
            }
            catch { node.Dispose(); throw; }

            void AppendChildren(int folder)
            {
                foreach (var layer in children[folder].AsEnumerable().Reverse())
                {
                    if (!scene.LayerVisible[layer]) continue;
                    if (scene.GetLayerKind(layer) == DrawingLayerKind.Folder)
                    {
                        if (scene.LayerBlendModes[layer] == LayerBlendMode.Normal
                            && !scene.GetLayerSymbolFilters(layer).HasEnabled)
                        {
                            AppendChildren(layer);
                            continue;
                        }
                        Flush();
                        var group = RecordGroup(layer);
                        SetFilters(group, scene.GetLayerSymbolFilters(layer));
                        node.Children.Add(group);
                    }
                    else if (scene.ShouldRenderLayerContent(layer))
                    {
                        if (scene.GetLayerSymbolFilters(layer).HasEnabled)
                        {
                            Flush();
                            pending.Add(layer);
                            Flush(layer);
                        }
                        else pending.Add(layer);
                    }
                }
            }

            void Flush(int layer = -1)
            {
                if (pending.Count == 0) return;
                Reference3DRenderItem[] items;
                stage.ReferenceNavigationRecording = true;
                try { items = stage.GetReference3DCompositeLayerRenderItems(pending); }
                finally { stage.ReferenceNavigationRecording = false; }
                if (items.Any(item => item.FragmentClip is not null || item.SecondaryObjectIndex >= 0
                    || item.MaterialOpacity < 0.999999f || item.OpticalSurface is not null))
                    throw new InvalidOperationException("Color group requires software spatial compositing.");
                var batch = new ReferenceColorNode();
                if (layer >= 0) SetFilters(batch, scene.GetLayerSymbolFilters(layer));
                node.Children.Add(batch);
                for (var start = 0; start < items.Length;)
                {
                    if (scene.ShapeKind[items[start].ObjectIndex] == ShapeKind.ImportedSvg)
                    {
                        var raster = items[start++];
                        var moving = stage.InstanceMovePreviewObjects?.Contains(raster.ObjectIndex) == true;
                        batch.Children.Add(new ReferenceColorNode { Raster = raster, ObjectIndices = [raster.ObjectIndex], Moving = moving, ContainsMoving = moving });
                        if (StageControl.TryGetReference3DProjectedBounds(raster.Contours,
                            16 + stage.GetReference3DStrokeWidth(raster.ObjectIndex, scene.Stroke[raster.ObjectIndex]), out var rasterBounds))
                            batch.Children[^1].ContentBounds = rasterBounds;
                        continue;
                    }
                    var first = start++;
                    var move = stage.InstanceMovePreviewObjects?.Contains(items[first].ObjectIndex) == true;
                    while (start < items.Length && scene.ShapeKind[items[start].ObjectIndex] != ShapeKind.ImportedSvg
                        && SameInstance(items[first].ObjectIndex, items[start].ObjectIndex)
                        && (stage.InstanceMovePreviewObjects?.Contains(items[start].ObjectIndex) == true) == move) start++;
                    var end = start;
                    batch.Children.Add(new ReferenceColorNode { ObjectIndices = items[first..end].Select(item => item.ObjectIndex).Distinct().ToArray(), Moving = move, ContainsMoving = move, Commands = Record(() =>
                    {
                        for (var index = first; index < end; index++) DrawReference3DSceneItemUnmasked(stage, items[index]);
                    }) });
                    for (var index = first; index < end; index++)
                        if (StageControl.TryGetReference3DProjectedBounds(items[index].Contours,
                            16 + stage.GetReference3DStrokeWidth(items[index].ObjectIndex, scene.Stroke[items[index].ObjectIndex]), out var bounds))
                            batch.Children[^1].ContentBounds = batch.Children[^1].ContentBounds.IsEmpty
                                ? bounds : RectangleF.Union(batch.Children[^1].ContentBounds, bounds);
                }
                pending.Clear();
            }

            bool SameInstance(int first, int next)
            {
                if (first == next) return true;
                if (stage.SceneCompositionResult is not { } composition
                    || !composition.TryGetOwner(first, out var a) || !composition.TryGetOwner(next, out var b)) return false;
                var aRoot = string.IsNullOrEmpty(a.RootInstanceId) ? a.InstanceId : a.RootInstanceId;
                var bRoot = string.IsNullOrEmpty(b.RootInstanceId) ? b.InstanceId : b.RootInstanceId;
                return !string.IsNullOrEmpty(aRoot) && string.Equals(aRoot, bRoot, StringComparison.Ordinal);
            }
        }

        static void SetFilters(ReferenceColorNode node, SymbolFilters filters)
        {
            var color = filters.ColorTransform;
            if ((filters with { ColorTransform = default }).HasEnabled
                || color.Enabled && (color.RedOffset != 0 || color.GreenOffset != 0 || color.BlueOffset != 0
                    || color.RedMultiplier < 0 || color.GreenMultiplier < 0 || color.BlueMultiplier < 0))
                node.RasterFilters = filters;
            else node.Color = color;
        }
    }

    private ID2D1CommandList PrepareReferenceColorFrame(StageControl stage, ID2D1DeviceContext context, Matrix3x2 navigation)
    {
        var preparingImage = false;
        var preparationViewport = RectangleF.Empty;
        using var previousTarget = context.Target;
        var previousTransform = context.Transform;
        context.EndDraw().CheckError();
        try
        {
            Mark(_referenceColorRoot!);
            PrepareFilters(_referenceColorRoot!);
            if (stage.InstanceMovePreviewObjects is not null)
            {
                PrepareStatic(_referenceColorRoot!);
            }
            return RecordNode(_referenceColorRoot!);
        }
        finally { context.Target = previousTarget; context.Transform = previousTransform; context.BeginDraw(); }

        bool Mark(ReferenceColorNode node)
        {
            var moving = node.Moving;
            foreach (var child in node.Children) moving |= Mark(child);
            if (node.Children.Count > 0 && node.Children.All(child => child.Moving)) node.Moving = true;
            if (moving && !node.Partitioned)
            {
                // Collapse each consecutive static run without crossing a
                // colour/isolation boundary. A sprite sheet may contain many
                // SVGs, but needs only the static bands around the selection.
                var bands = new List<ReferenceColorNode>();
                ReferenceColorNode? band = null;
                foreach (var child in node.Children)
                {
                    if (child.ContainsMoving)
                    { band = null; bands.Add(child); }
                    else
                    {
                        if (band is null) { band = new ReferenceColorNode { Partitioned = true, StaticBand = true }; bands.Add(band); }
                        band.Children.Add(child);
                    }
                }
                node.Children = bands;
                node.Partitioned = true;
            }
            return node.ContainsMoving = moving;
        }

        void PrepareFilters(ReferenceColorNode node)
        {
            foreach (var child in node.Children)
            {
                PrepareFilters(child);
                node.ContentBounds = node.ContentBounds.IsEmpty ? child.ContentBounds
                    : child.ContentBounds.IsEmpty ? node.ContentBounds : RectangleF.Union(node.ContentBounds, child.ContentBounds);
            }
            if (!node.RasterFilters.HasEnabled || node.FilteredImage is not null || node.ContentBounds.IsEmpty) return;
            var padding = SymbolFilterRasterizer.GetPadding(node.RasterFilters, _referenceNavigationZoom);
            var bounds = node.ContentBounds;
            bounds = RectangleF.FromLTRB(bounds.Left - padding.Left, bounds.Top - padding.Top,
                bounds.Right + padding.Right, bounds.Bottom + padding.Bottom);
            // Rasterize at the retained stream's original projection. Navigation
            // transforms the resulting group exactly once; moving a whole group
            // never changes its filter input or crops its offscreen contents.
            preparingImage = true;
            preparationViewport = bounds;
            stage.InstanceMovePreviewRasterizing = true;
            ID2D1CommandList commands;
            try { commands = RecordNode(node); }
            finally { preparingImage = false; stage.InstanceMovePreviewRasterizing = false; }
            using (commands)
                node.FilteredImage = CreateFilteredNavigationImage(context, commands, Rectangle.Ceiling(bounds),
                    node.RasterFilters, _referenceNavigationZoom);
            node.FilteredBounds = Rectangle.Ceiling(bounds);
            node.ContentBounds = node.FilteredBounds;
        }

        void PrepareStatic(ReferenceColorNode node)
        {
            if (node.ContainsMoving && !node.Moving)
            {
                foreach (var child in node.Children) PrepareStatic(child);
                return;
            }
            if (node.StaticImage is not null) return;
            var imageBounds = node.Moving ? Rectangle.Ceiling(node.ContentBounds)
                : new Rectangle(0, 0, stage.Width, stage.Height);
            if (imageBounds.Width <= 0 || imageBounds.Height <= 0
                || imageBounds.Width > context.MaximumBitmapSize || imageBounds.Height > context.MaximumBitmapSize
                || (long)imageBounds.Width * imageBounds.Height > 16_777_216) return;
            preparingImage = true;
            preparationViewport = imageBounds;
            stage.InstanceMovePreviewRasterizing = true;
            ID2D1CommandList commands;
            try { commands = RecordNode(node); }
            finally { preparingImage = false; stage.InstanceMovePreviewRasterizing = false; }
            using var recorded = commands;
            var bitmap = context.CreateBitmap(new SizeI(imageBounds.Width, imageBounds.Height), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            try
            {
                context.Target = bitmap; context.Transform = Matrix3x2.CreateTranslation(-imageBounds.X, -imageBounds.Y); context.BeginDraw();
                try { context.Clear(new Color4(0, 0, 0, 0)); context.DrawImage(commands); }
                finally { context.EndDraw().CheckError(); }
                node.StaticImage = bitmap;
                node.ImageBounds = imageBounds;
            }
            catch { bitmap.Dispose(); throw; }
        }

        ID2D1CommandList RecordNode(ReferenceColorNode node)
        {
            var groups = new Dictionary<ReferenceColorNode, ID2D1CommandList>();
            ID2D1CommandList? commands = null;
            try
            {
                GatherGroups(node);
                commands = context.CreateCommandList();
                context.Target = commands; context.Transform = Matrix3x2.Identity; context.BeginDraw();
                try { DrawNode(node); }
                finally { context.EndDraw().CheckError(); }
                commands.Close().CheckError();
                return commands;
            }
            catch { commands?.Dispose(); throw; }
            finally { foreach (var group in groups.Values) group.Dispose(); }

            void GatherGroups(ReferenceColorNode current)
            {
                if (OutsidePreparation(current)) return;
                if (current.StaticImage is not null) return;
                foreach (var child in current.Children)
                    if (OutsidePreparation(child)) continue;
                    else if (child.Color.Enabled) groups.Add(child, RecordNode(child));
                    else GatherGroups(child);
            }

            void DrawNode(ReferenceColorNode current)
            {
                if (OutsidePreparation(current)) return;
                if (current.FilteredImage is { } filtered)
                {
                    var move = current.Moving && !preparingImage ? stage.InstanceMovePreviewOffset : PointF.Empty;
                    context.Transform = Matrix3x2.CreateTranslation(current.FilteredBounds.Location.X,
                        current.FilteredBounds.Location.Y) * navigation * Matrix3x2.CreateTranslation(move.X, move.Y);
                    context.DrawBitmap(filtered);
                    return;
                }
                if (current.StaticImage is { } image)
                {
                    var imageOffset = current.Moving ? stage.InstanceMovePreviewOffset : PointF.Empty;
                    context.Transform = Matrix3x2.CreateTranslation(current.ImageBounds.X + imageOffset.X,
                        current.ImageBounds.Y + imageOffset.Y);
                    context.DrawBitmap(image); return;
                }
                var offset = current.Moving && !preparingImage ? stage.InstanceMovePreviewOffset : PointF.Empty;
                var translation = Matrix3x2.CreateTranslation(offset.X, offset.Y);
                if (current.Commands is { } vectors)
                { context.Transform = navigation * translation; context.DrawImage(vectors); }
                else if (current.Raster is { } raster)
                { context.Transform = translation; DrawReference3DSceneItemUnmasked(stage, raster); }
                foreach (var child in current.Children)
                {
                    if (OutsidePreparation(child)) continue;
                    if (!child.Color.Enabled) { DrawNode(child); continue; }
                    context.Transform = Matrix3x2.Identity;
                    using var effect = new ID2D1Effect(context.CreateEffect(EffectGuids.ColorMatrix));
                    var color = child.Color;
                    var alpha = color.AlphaMultiplier ?? 1;
                    effect.SetValue((int)ColorMatrixProperties.ColorMatrix,
                        new Matrix5x4(color.RedMultiplier, 0, 0, 0, 0, color.GreenMultiplier, 0, 0,
                            0, 0, color.BlueMultiplier, 0, 0, 0, 0, alpha, 0, 0, 0, 0));
                    effect.SetValue((int)ColorMatrixProperties.AlphaMode, ColorMatrixAlphaMode.Premultiplied);
                    effect.SetInput(0, groups[child], true);
                    context.DrawImage(effect);
                }
            }
        }

        bool OutsidePreparation(ReferenceColorNode node)
        {
            if (!preparingImage || node.ContentBounds.IsEmpty) return false;
            var min = Vector2.Transform(new Vector2(node.ContentBounds.Left, node.ContentBounds.Top), navigation);
            var max = Vector2.Transform(new Vector2(node.ContentBounds.Right, node.ContentBounds.Bottom), navigation);
            // Filter preparation uses the entire padded group. Static drag
            // images only need the viewport; avoid submitting offscreen paths.
            return max.X < preparationViewport.Left - 2 || max.Y < preparationViewport.Top - 2
                || min.X > preparationViewport.Right + 2 || min.Y > preparationViewport.Bottom + 2;
        }
    }
}
