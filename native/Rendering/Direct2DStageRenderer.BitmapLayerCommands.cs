using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly Dictionary<(VectorScene Scene, int Layer), BitmapLayerCommands> _bitmapLayerCommands = [];
    private long _bitmapLayerCommandSequence;
    private long _bitmapLayerCommandBytes;
    internal long BitmapLayerCommandBuilds { get; private set; }
    internal long BitmapLayerCommandReuses { get; private set; }

    private sealed class BitmapLayerCommands : IDisposable
    {
        public required ID2D1CommandList Commands;
        public required StageControl Stage;
        public required long Geometry;
        public required long Content;
        public required long Summary;
        public required long Presentation;
        public required int Frame;
        public required float Rotation;
        public required int[] Objects;
        public required (string Asset, BitmapImageRasterKey Source)[] Sources;
        public required PointF Origin;
        public required float Scale;
        public required (float X, float Y, float Zoom, Size Size) View;
        public required float MinimumExtent;
        public required bool HasBlockBitmaps;
        public long LastUse;
        public long SourceBytes;
        public void Dispose() => Commands.Dispose();
    }

    private bool TryDrawBitmapLayerCommands(StageControl stage, VectorScene scene, int layer,
        IReadOnlyList<int> objects, int start)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_BITMAP_LAYER_COMMANDS") == "1"
            || _target is not ID2D1DeviceContext context || _capturingShotPreview
            || start != 0 || objects.Count == 0 || stage.UsesReferenceProjection || scene.HasDisplayLayerEffects
            || stage.SelectionMoveFeedbackActive || stage.SelectionDragPreviewActive
            || stage.RequiresObjectRendererForDragPreview(scene)
            || stage.SceneHasDistortionsForRendering(scene)
            || stage.HasSceneCompositionMaskClips(scene)) return false;

        var key = (scene, layer);
        var scale = stage.WorldLengthToScreen(1);
        var view = (stage.CameraX, stage.CameraY, stage.Zoom, stage.ClientSize);
        // Rescaling recorded device coordinates can move a nearest-neighbor
        // sample across a texel boundary. Re-record at the settled view.
        var zoomPreview = stage.NavigationPreviewEnabled && stage.ZoomLodPreviewActive;
        var valid = _bitmapLayerCommands.TryGetValue(key, out var cached)
            && ReferenceEquals(cached.Stage, stage) && cached.Geometry == scene.GeometryRevision
            && cached.Content == scene.ActiveContentRevision && cached.Summary == scene.SummaryRevision
            && cached.Presentation == scene.LayerPresentationRevision
            && cached.Frame == stage.Frame && cached.Rotation == stage.PlanarCanvasRotation
            && (cached.View == view || zoomPreview && !cached.HasBlockBitmaps)
            && (zoomPreview ? cached.Objects.Length >= objects.Count : cached.Objects.Length == objects.Count);
        if (valid)
        {
            // At zoom-in, the visible list may be a subsequence of the recorded
            // layer. Its extra draws fall outside the viewport and are clipped
            // by the target. Missing or reordered visible objects still rebuild.
            var visibleIndex = 0;
            foreach (var index in cached!.Objects)
            {
                if (stage.IsObjectHiddenForRendering(scene, index)) { valid = false; break; }
                if (visibleIndex < objects.Count && objects[visibleIndex] == index) visibleIndex++;
            }
            valid &= visibleIndex == objects.Count;
            if (valid)
                foreach (var (asset, source) in cached!.Sources)
                    if (!stage.TryGetBitmapImageKey(asset, out var current) || current != source
                        || stage.BitmapImageSampling(asset) != BitmapSampling.Point)
                    { valid = false; break; }
        }
        if (!valid)
        {
            if (_bitmapLayerCommands.Remove(key, out var stale)) ReleaseBitmapLayerCommands(stale);
            var sources = new Dictionary<string, BitmapImageRasterKey>(StringComparer.Ordinal);
            var textures = new HashSet<BitmapImageRasterKey>();
            var minimumExtent = float.MaxValue;
            var hasBlockBitmaps = false;
            foreach (var index in objects)
            {
                if (scene.ShapeKind[index] != ShapeKind.Bitmap || stage.IsObjectHiddenForRendering(scene, index)
                    || !scene.TryGetBitmapObjectData(index, out var data) || data.VisibleContours is not null
                    || stage.BitmapImageSampling(data.ImageAssetId) != BitmapSampling.Point
                    || !stage.TryGetBitmapImageKey(data.ImageAssetId, out var source)) return false;
                if (source.PixelWidth > 64 || source.PixelHeight > 64) return false;
                hasBlockBitmaps |= data.BlockGridKey is not null;
                minimumExtent = Math.Min(minimumExtent, Math.Min(scene.Width[index], scene.Height[index]));
                sources[data.ImageAssetId] = source;
                textures.Add(BitmapImageRasterizer.ObjectAppearanceKey(source, data));
            }
            // A cached command list recorded on the old pixel grid must not be
            // rescaled for tiles. During a gesture the live bitmap pass aligns them.
            if (zoomPreview && hasBlockBitmaps) return false;
            // The ordinary path clamps tiny objects to 0.75 device pixels.
            // Replaying an affine command must never scale that clamp.
            if (minimumExtent * scale < 0.75f) return false;
            var sourceBytes = textures.Sum(source => (long)source.PixelWidth * source.PixelHeight * 4);
            if (sourceBytes > 32L * 1024 * 1024) return false;
            using var previousTarget = context.Target;
            var previousTransform = context.Transform;
            context.EndDraw().CheckError();
            var commands = context.CreateCommandList();
            try
            {
                context.Target = commands;
                context.Transform = Matrix3x2.Identity;
                context.BeginDraw();
                try
                {
                    foreach (var index in objects)
                    {
                        if (!scene.TryGetBitmapObjectData(index, out var data)
                            || !stage.TryDecodeBitmapImage(data, out var raster)) return false;
                        var center = stage.WorldToScreen(scene.X[index], scene.Y[index]);
                        var angle = scene.Angle[index] + stage.PlanarCanvasRotation;
                        var width = stage.WorldLengthToScreen(scene.Width[index]);
                        var height = stage.WorldLengthToScreen(scene.Height[index]);
                        var x = new PointF(width * MathF.Cos(angle), width * MathF.Sin(angle));
                        var y = new PointF(-height * MathF.Sin(angle), height * MathF.Cos(angle));
                        var frame = new TransformOverlayFrame(
                            new PointF(center.X - (x.X + y.X) * .5f, center.Y - (x.Y + y.Y) * .5f), x, y);
                        DrawAffineBitmapCore(stage, scene, index, data, raster, frame);
                    }
                }
                finally { context.EndDraw().CheckError(); }
                commands.Close().CheckError();
                cached = new BitmapLayerCommands
                {
                    Commands = commands, Stage = stage, Geometry = scene.GeometryRevision,
                    Content = scene.ActiveContentRevision, Summary = scene.SummaryRevision,
                    Presentation = scene.LayerPresentationRevision, Frame = stage.Frame,
                    Rotation = stage.PlanarCanvasRotation, Objects = objects.ToArray(),
                    Sources = sources.Select(pair => (pair.Key, pair.Value)).ToArray(),
                    Origin = stage.WorldToScreen(0, 0), Scale = scale, View = view, MinimumExtent = minimumExtent,
                    HasBlockBitmaps = hasBlockBitmaps
                };
                _bitmapLayerCommands.Add(key, cached);
                cached.SourceBytes = sourceBytes;
                _bitmapLayerCommandBytes += cached.SourceBytes;
                BitmapLayerCommandBuilds++;
            }
            finally
            {
                context.Target = previousTarget;
                context.Transform = previousTransform;
                context.BeginDraw();
                if (!_bitmapLayerCommands.ContainsKey(key)) commands.Dispose();
            }
            // Command lists retain GPU images independently of the raster cache.
            cached!.LastUse = ++_bitmapLayerCommandSequence;
            while (_bitmapLayerCommands.Count > 128 || _bitmapLayerCommandBytes > 32L * 1024 * 1024)
            {
                var victim = _bitmapLayerCommands.MinBy(pair => pair.Value.LastUse);
                _bitmapLayerCommands.Remove(victim.Key);
                ReleaseBitmapLayerCommands(victim.Value);
            }
        }
        else BitmapLayerCommandReuses++;
        if (cached!.MinimumExtent * scale < 0.75f) return false;
        cached.LastUse = ++_bitmapLayerCommandSequence;
        if (cached.View == view)
        {
            context.DrawImage(cached.Commands, Vortice.Direct2D1.InterpolationMode.NearestNeighbor);
            return true;
        }
        var ratio = scale / cached.Scale;
        var origin = stage.WorldToScreen(0, 0);
        var saved = context.Transform;
        try
        {
            context.Transform = Matrix3x2.CreateScale(ratio)
                * Matrix3x2.CreateTranslation(origin.X - cached.Origin.X * ratio, origin.Y - cached.Origin.Y * ratio)
                * saved;
            context.DrawImage(cached.Commands, Vortice.Direct2D1.InterpolationMode.NearestNeighbor);
        }
        finally { context.Transform = saved; }
        return true;
    }

    private void ClearBitmapLayerCommands()
    {
        foreach (var cached in _bitmapLayerCommands.Values) cached.Dispose();
        _bitmapLayerCommands.Clear();
        _bitmapLayerCommandBytes = 0;
    }

    private void ReleaseBitmapLayerCommands(BitmapLayerCommands cached)
    {
        _bitmapLayerCommandBytes -= cached.SourceBytes;
        cached.Dispose();
    }
}
