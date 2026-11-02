using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class SceneCompositionBuilder
{
    private static SceneCompositionResult BuildDrawingObjectOnionExposure(VectorScene destination,
        DrawingObjectDefinition drawing, int layer, int frame, decimal fps,
        IReadOnlyDictionary<string, DrawingObjectDefinition> definitions)
    {
        var source = drawing.Scene;
        var required = new HashSet<int>();
        Include(layer);
        var instance = new DrawingObjectInstanceDefinition
        {
            Id = $"onion-source-{drawing.Id}", DrawingObjectId = drawing.Id, Name = drawing.Name,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame, HoldFrame = frame
        };
        var layers = new List<CompositionLayer>();
        // The edited symbol's own coordinates already are Stage world space.
        // Compose its selected exposure and mask dependencies with identity,
        // rather than placing it as a new instance around its registration point.
        CollectDrawingObjectLayers(drawing, instance, Matrix3x2.Identity, Matrix4x4.Identity,
            true, Vector3.UnitZ, frame, fps, definitions, layers, new HashSet<string>(StringComparer.Ordinal),
            drawing.Name, instance.Id, excludeEffectivelyLockedLayers: true, sourceLayerFilter: required);
        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawing.FrameCount));

        void Include(int index)
        {
            if (!required.Add(index)) return;
            var parent = Array.IndexOf(source.LayerIds, source.LayerParentIds[index]);
            if (parent >= 0) Include(parent);
            if (source.TryGetMaskLayerIndex(index, out var mask)) Include(mask);
        }
    }
}
