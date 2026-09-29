using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private StageGpuOpticalShader? _gpuOpticalShader;
    private readonly Dictionary<GpuOpticalCacheKey, GpuOpticalCacheEntry> _gpuOpticalCache = [];
    private readonly Dictionary<GpuOpticalCacheKey, StageGpuOpticalShader.Surface> _gpuOpticalSurfaces = [];
    internal double LastGpuOpticsMilliseconds { get; private set; }
    internal double LastGpuOpticsPlanMilliseconds { get; private set; }
    internal double LastGpuOpticsSignatureMilliseconds { get; private set; }
    internal double LastGpuOpticsBaseMilliseconds { get; private set; }
    internal double LastGpuOpticsShadeMilliseconds { get; private set; }
    internal int LastGpuOpticsBypassedSurfaces { get; private set; }
    internal string? LastGpuOpticsFailure { get; set; }
    internal int LastGpuOpticalSurfaceCount => _gpuOpticalSurfaces.Count;

    private readonly record struct GpuOpticalCacheKey(
        int ObjectIndex,
        int LayerIndex,
        Reference3DRenderKind Kind,
        int SurfaceSlot,
        int FragmentSlot,
        ulong StableFragmentIdentity,
        int SecondaryObjectIndex);

    private sealed class GpuOpticalCacheEntry(
        System.Drawing.Size size,
        Rectangle bounds,
        ulong signature,
        StageGpuOpticalShader.Surface surface)
    {
        public System.Drawing.Size Size { get; } = size;
        public Rectangle Bounds { get; set; } = bounds;
        public ulong Signature { get; set; } = signature;
        public StageGpuOpticalShader.Surface Surface { get; } = surface;
    }

    private void PrepareGpuOpticalSurfaces(StageControl stage)
    {
        LastGpuOpticsMilliseconds = 0;
        LastGpuOpticsPlanMilliseconds = 0;
        LastGpuOpticsSignatureMilliseconds = 0;
        LastGpuOpticsBaseMilliseconds = 0;
        LastGpuOpticsShadeMilliseconds = 0;
        LastGpuOpticsBypassedSurfaces = 0;
        LastGpuOpticsFailure = null;
        stage.Reference3DGpuOpticsEnabled = _gpuDevice is not null
            && _gpuDevice.Device.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_11_0
            && stage.RendersReferenceProjection && stage.Reference3DPlaybackActive
            && stage.UnderlayScene is null && stage.OnionSkinScene is null
            && stage.DragPreviewScene is null;
        if (!stage.Reference3DGpuOpticsEnabled) return;
        var started = Stopwatch.GetTimestamp();
        _gpuOpticalShader ??= new StageGpuOpticalShader(_gpuDevice!);
        var planStarted = Stopwatch.GetTimestamp();
        var items = stage.GetReference3DSceneRenderItems();
        var requiredBytes = 0L;
        foreach (var item in items)
        {
            if (item.GpuOpticalSurface is not { } optical) continue;
            if (item.LocalLightLayers is not { Length: > 0 })
            {
                LastGpuOpticsBypassedSurfaces++;
                continue;
            }
            requiredBytes += (long)((optical.Bounds.Width + 63) / 64 * 64)
                * ((optical.Bounds.Height + 63) / 64 * 64) * 8;
        }
        LastGpuOpticsPlanMilliseconds = Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
        if (requiredBytes > 256L * 1024 * 1024)
        {
            ClearGpuOpticalSurfaces();
            stage.Reference3DGpuOpticsEnabled = false;
            return;
        }
        _gpuOpticalSurfaces.Clear();
        var context = _gpuDevice!.Context;
        // GetTarget returns an owning COM reference; release it after restoring the target.
        using var previousImage = context.Target;
        var previousTransform = context.Transform;
        var usedKeys = new HashSet<GpuOpticalCacheKey>();
        var pendingShades = new List<(Reference3DRenderItem Item, StageGpuOpticalShader.Surface Surface, Rectangle Bounds, SpatialOpticalMaterial Material)>();
        var signatureMilliseconds = 0d;
        var baseMilliseconds = 0d;
        var shadeMilliseconds = 0d;
        try
        {
            foreach (var item in items)
            {
                if (item.GpuOpticalSurface is not { } optical) continue;
                if (item.LocalLightLayers is not { Length: > 0 }) continue;
                var bounds = optical.Bounds;
                // Retain modestly padded allocations while animated silhouettes change size.
                var size = new System.Drawing.Size((bounds.Width + 63) / 64 * 64, (bounds.Height + 63) / 64 * 64);
                var key = CreateGpuOpticalCacheKey(item);
                usedKeys.Add(key);
                var signatureStarted = Stopwatch.GetTimestamp();
                var signature = ComputeGpuOpticalSignature(stage, item, optical.Material, bounds);
                signatureMilliseconds += Stopwatch.GetElapsedTime(signatureStarted).TotalMilliseconds;
                if (_gpuOpticalCache.TryGetValue(key, out var cached)
                    && cached.Size == size
                    && cached.Bounds == bounds
                    && cached.Signature == signature)
                {
                    _gpuOpticalSurfaces[key] = cached.Surface;
                    continue;
                }

                StageGpuOpticalShader.Surface surface;
                if (cached is not null && cached.Size == size)
                {
                    surface = cached.Surface;
                    cached.Bounds = bounds;
                    cached.Signature = signature;
                }
                else
                {
                    cached?.Surface.Dispose();
                    surface = _gpuOpticalShader.CreateSurface(size);
                    _gpuOpticalCache[key] = new GpuOpticalCacheEntry(
                        size,
                        bounds,
                        signature,
                        surface);
                }

                var baseStarted = Stopwatch.GetTimestamp();
                try
                {
                    // Record in stage coordinates so all existing geometry, gradient and
                    // projective drawing helpers retain their coordinate contract.
                    using var commands = context.CreateCommandList();
                    context.Target = commands;
                    context.BeginDraw();
                    try
                    {
                        DrawGpuOpticalBase(stage, item);
                    }
                    finally { context.EndDraw().CheckError(); }
                    commands.Close().CheckError();

                    context.Target = surface.BaseBitmap;
                    context.BeginDraw();
                    try
                    {
                        context.Transform = Matrix3x2.Identity;
                        context.Clear(new Color4(0, 0, 0, 0));
                        context.DrawImage(commands, new Vector2(-bounds.Left, -bounds.Top));
                    }
                    finally { context.EndDraw().CheckError(); }
                    context.Target = null;
                }
                finally { baseMilliseconds += Stopwatch.GetElapsedTime(baseStarted).TotalMilliseconds; }
                _gpuOpticalSurfaces[key] = surface;
                if (item.LocalLightLayers is { Length: > 0 })
                    pendingShades.Add((item, surface, bounds, optical.Material));
            }

            // Keep the D2D recording pass separate from the compute pass. Switching
            // the shared device between D2D and D3D for every item forces an
            // implicit synchronization on some drivers; dispatch all optical
            // surfaces after their base images are complete.
            var shadeStarted = Stopwatch.GetTimestamp();
            try
            {
                foreach (var pending in pendingShades)
                {
                    _gpuOpticalShader.Shade(
                        pending.Surface,
                        pending.Item,
                        pending.Material,
                        pending.Bounds);
                }
            }
            finally { shadeMilliseconds += Stopwatch.GetElapsedTime(shadeStarted).TotalMilliseconds; }
            // A large scene must not keep allocations after receivers disappear.
            foreach (var stale in _gpuOpticalCache.Keys.Where(key => !usedKeys.Contains(key)).ToArray())
            {
                _gpuOpticalCache[stale].Surface.Dispose();
                _gpuOpticalCache.Remove(stale);
            }
        }
        finally
        {
            context.Target = previousImage;
            context.Transform = previousTransform;
            LastGpuOpticsSignatureMilliseconds = signatureMilliseconds;
            LastGpuOpticsBaseMilliseconds = baseMilliseconds;
            LastGpuOpticsShadeMilliseconds = shadeMilliseconds;
            LastGpuOpticsMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private bool TryDrawGpuOpticalSurface(StageControl stage, Reference3DRenderItem item)
    {
        if (item.GpuOpticalSurface is not { } optical) return false;
        if (!stage.Reference3DGpuOpticsEnabled) return false;
        if (item.LocalLightLayers is not { Length: > 0 })
        {
            // The GPU path does not shade a surface without local light layers;
            // its BaseBitmap is exactly the raw base draw below. Keep the outer
            // material opacity and composition/fragment mask wrappers unchanged.
            var drawn = false;
            DrawWithSceneCompositionMaskClips(
                stage,
                item.ObjectIndex,
                () => drawn = DrawGpuOpticalBase(stage, item),
                reference3D: true);
            return drawn;
        }
        if (!_gpuOpticalSurfaces.TryGetValue(CreateGpuOpticalCacheKey(item), out var surface)) return false;
        DrawWithSceneCompositionMaskClips(stage, item.ObjectIndex, () =>
        {
            var bounds = optical.Bounds;
            var previous = _target!.Transform;
            try
            {
                _target.Transform = Matrix3x2.Identity;
                var bitmap = item.LocalLightLayers is { Length: > 0 }
                    ? surface.OutputBitmap
                    : surface.BaseBitmap;
                _target.DrawBitmap(bitmap,
                    Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), 1f,
                    BitmapInterpolationMode.NearestNeighbor, Rect(0, 0, bounds.Width, bounds.Height));
            }
            finally { _target.Transform = previous; }
        }, reference3D: true);
        return true;
    }

    private bool DrawGpuOpticalBase(StageControl stage, Reference3DRenderItem item)
    {
        if (_target is not { } target) return false;
        var previousTransform = target.Transform;
        var previousAntialiasMode = target.AntialiasMode;
        try
        {
            target.Transform = Matrix3x2.Identity;
            target.AntialiasMode = AntialiasMode.PerPrimitive;
            var materialItem = item with
            {
                MaterialOpacity = 1f,
                OpticalSurface = null,
                GpuOpticalSurface = null
            };
            using var clip = item.FragmentClip is { Length: > 0 } fragment
                ? CreateReference3DGeometry(fragment, fillOnly: true)
                : null;
            using var layer = clip is null ? null : target.CreateLayer();
            return DrawReference3DSceneItemWithGeometricClip(clip, layer, () =>
            {
                if (item.Kind is Reference3DRenderKind.Back or Reference3DRenderKind.Side)
                {
                    using var geometry = CreateReference3DGeometry(item.Contours, fillOnly: true);
                    if (geometry is not null)
                    {
                        FillAntialiasedGeometry(
                            geometry,
                            BrushFor(stage.GetReference3DExtrusionSurfaceColor(materialItem).ToArgb()));
                    }
                }
                else
                {
                    DrawReference3DObjectUnclipped(
                        stage,
                        materialItem,
                        item.Kind == Reference3DRenderKind.FrontStroke
                            ? SceneRenderPass.Stroke
                            : SceneRenderPass.Fill);
                }
                return true;
            });
        }
        finally
        {
            target.AntialiasMode = previousAntialiasMode;
            target.Transform = previousTransform;
        }
    }

    private void ClearGpuOpticalSurfaces()
    {
        _gpuOpticalSurfaces.Clear();
        foreach (var entry in _gpuOpticalCache.Values) entry.Surface.Dispose();
        _gpuOpticalCache.Clear();
    }

    private static GpuOpticalCacheKey CreateGpuOpticalCacheKey(Reference3DRenderItem item) =>
        new(
            item.ObjectIndex,
            item.LayerIndex,
            item.Kind,
            item.SurfaceSlot,
            item.FragmentSlot,
            item.StableFragmentIdentity,
            item.SecondaryObjectIndex);

    private static ulong ComputeGpuOpticalSignature(
        StageControl stage,
        Reference3DRenderItem item,
        SpatialOpticalMaterial material,
        Rectangle bounds)
    {
        var hash = 1469598103934665603UL;
        Mix(ref hash, bounds.X);
        Mix(ref hash, bounds.Y);
        Mix(ref hash, bounds.Width);
        Mix(ref hash, bounds.Height);
        Mix(ref hash, item.ObjectIndex);
        Mix(ref hash, item.LayerIndex);
        Mix(ref hash, (int)item.Kind);
        Mix(ref hash, item.SurfaceSlot);
        Mix(ref hash, item.FragmentSlot);
        Mix(ref hash, item.StableFragmentIdentity);
        Mix(ref hash, item.SecondaryObjectIndex);
        Mix(ref hash, item.MaterialOpacity);
        Mix(ref hash, item.EdgeArgb);
        Mix(ref hash, item.EdgeWidth);
        Mix(ref hash, item.EdgeStartCap);
        Mix(ref hash, item.EdgeEndCap);
        Mix(ref hash, item.VectorLightingArgb.GetValueOrDefault());
        Mix(ref hash, item.VectorLightingArgb.HasValue);
        Mix(ref hash, item.SolidStrokeOpticalBaseArgb.GetValueOrDefault());
        Mix(ref hash, item.SolidStrokeOpticalBaseArgb.HasValue);
        MixMaterial(ref hash, material);
        Mix(ref hash, item.OpticalResponse.ShadeArgb);
        Mix(ref hash, item.OpticalResponse.HighlightArgb);
        Mix(ref hash, item.OpticalResponse.OpacityScale);
        MixVector(ref hash, item.OpticalResponse.AmbientIrradiance);
        AddContours(ref hash, item.Contours);
        AddContours(ref hash, item.FragmentClip);
        AddContours(ref hash, item.OcclusionContours);
        AddContours(ref hash, item.OpticalSurfaceContours);
        AddLightLayers(ref hash, item.LocalLightLayers);
        if ((uint)item.ObjectIndex < stage.Scene.Argb.Length)
            Mix(ref hash, stage.Scene.Argb[item.ObjectIndex]);
        Mix(ref hash, stage.GetReference3DExtrusionSurfaceColor(item).ToArgb());
        return hash;
    }

    private static void AddContours(ref ulong hash, IReadOnlyList<Reference3DProjectedContour>? contours)
    {
        if (contours is null)
        {
            Mix(ref hash, -1);
            return;
        }

        Mix(ref hash, contours.Count);
        foreach (var contour in contours)
        {
            Mix(ref hash, contour.Closed);
            Mix(ref hash, contour.AverageDepth);
            Mix(ref hash, contour.Points.Length);
            foreach (var point in contour.Points)
            {
                Mix(ref hash, point.X);
                Mix(ref hash, point.Y);
            }
        }
    }

    private static void AddLightLayers(
        ref ulong hash,
        IReadOnlyList<Reference3DLocalLightLayer>? layers)
    {
        if (layers is null)
        {
            Mix(ref hash, -1);
            return;
        }

        Mix(ref hash, layers.Count);
        foreach (var layer in layers)
        {
            Mix(ref hash, layer.GradientTransform.M11);
            Mix(ref hash, layer.GradientTransform.M12);
            Mix(ref hash, layer.GradientTransform.M21);
            Mix(ref hash, layer.GradientTransform.M22);
            Mix(ref hash, layer.GradientTransform.M31);
            Mix(ref hash, layer.GradientTransform.M32);
            AddContours(ref hash, layer.Contours);
            Mix(ref hash, layer.LinearStops.Length);
            foreach (var stop in layer.LinearStops)
            {
                Mix(ref hash, stop.Position);
                MixVector(ref hash, stop.DiffuseIrradiance);
                MixVector(ref hash, stop.SpecularRadiance);
                MixVector(ref hash, stop.FresnelRadiance);
            }
        }
    }

    private static void MixMaterial(ref ulong hash, SpatialOpticalMaterial material)
    {
        Mix(ref hash, material.Transmission);
        Mix(ref hash, material.Reflectivity);
        Mix(ref hash, material.Metallic);
        Mix(ref hash, material.Roughness);
        Mix(ref hash, material.IndexOfRefraction);
        Mix(ref hash, material.CastsShadows);
        Mix(ref hash, material.ReceivesShadows);
    }

    private static void MixVector(ref ulong hash, Vector3 value)
    {
        Mix(ref hash, value.X);
        Mix(ref hash, value.Y);
        Mix(ref hash, value.Z);
    }

    private static void Mix(ref ulong hash, bool value) => Mix(ref hash, value ? 1 : 0);

    private static void Mix(ref ulong hash, int value)
    {
        hash ^= unchecked((uint)value);
        hash *= 1099511628211UL;
    }

    private static void Mix(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private static void Mix(ref ulong hash, float value) =>
        Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static void Mix(ref ulong hash, int? value)
    {
        Mix(ref hash, value.HasValue);
        Mix(ref hash, value.GetValueOrDefault());
    }
}
