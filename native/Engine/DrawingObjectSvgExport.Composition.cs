using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Svg;

namespace VectorAnimationEngine;

internal static partial class SceneCompositionBuilder
{
    // Export the authored coordinate system and exact requested root frame. A
    // drag preview would apply the symbol's registration transform and opacity.
    internal static SceneCompositionResult BuildDrawingObjectForExport(VectorScene destination,
        DrawingObjectDefinition drawingObject, IReadOnlyList<DrawingObjectDefinition> definitions,
        int frame, decimal fps)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObject);
        if (!definitions.Any(item => ReferenceEquals(item, drawingObject)))
            throw new ArgumentException("The exported symbol must belong to the supplied definitions.", nameof(drawingObject));
        var root = new DrawingObjectInstanceDefinition
        {
            DrawingObjectId = drawingObject.Id,
            Name = drawingObject.Name,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1)),
            PlaybackFps = fps
        };
        var layers = new List<CompositionLayer>();
        CollectDrawingObjectLayers(drawingObject, root, Matrix3x2.Identity, Matrix4x4.Identity,
            true, Vector3.Zero, frame, fps, DefinitionsById(definitions), layers,
            new HashSet<string>(StringComparer.Ordinal), drawingObject.Name, root.Id);
        return BuildLayers(destination, layers, drawingObject.FrameCount);
    }
}

internal static partial class DrawingObjectSvgExport
{
    internal static int FirstContentFrame(DrawingObjectDefinition drawingObject)
    {
        var scene = drawingObject.Scene;
        for (var frame = 0; frame < Math.Max(1, scene.FrameCount); frame++)
        {
            if (scene.GetActiveObjectIndices(frame).Length > 0) return frame;
            foreach (var instance in drawingObject.Instances)
            {
                var layer = Array.IndexOf(scene.LayerIds, instance.SceneLayerId);
                if (!instance.EditorHidden && instance.EvaluateState(frame).Visible && layer >= 0
                    && scene.IsLayerEffectivelyVisible(layer)
                    && drawingObject.Timeline.EvaluateTargetExposure(instance.SceneLayerId, frame).HasContent)
                    return frame;
            }
        }
        return 0;
    }

    internal static XDocument CreateSymbolDocument(DrawingObjectDefinition drawingObject,
        IReadOnlyList<DrawingObjectDefinition> definitions, int frame, decimal fps)
    {
        var composed = new VectorScene();
        var result = SceneCompositionBuilder.BuildDrawingObjectForExport(composed, drawingObject, definitions, frame, fps);
        if (result.ObjectPoses.Any(pose => pose.FlatToScene != Matrix4x4.Identity))
            throw new InvalidOperationException("Portable SVG export cannot preserve a spatial symbol. Save the V2D project to retain the appearance.");
        var document = CreateDocument(drawingObject.Id, drawingObject.Name, composed, 0, result.LayerMasks);
        document.Root!.SetAttributeValue("data-v2d-frame", frame.ToString(CultureInfo.InvariantCulture));
        return document;
    }

    internal static void Write(string path, DrawingObjectDefinition drawingObject,
        IReadOnlyList<DrawingObjectDefinition> definitions, int frame, decimal fps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WriteDocument(path, CreateSymbolDocument(drawingObject, definitions, frame, fps));
    }

    private sealed partial class ExportLayout
    {
        private readonly SceneCompositionMaskPlan? _compositionMasks;
        private readonly int[][] _coverageObjectsByLayer;
        private readonly Dictionary<int, bool> _compositionCoverage = [];

        private bool IsCompositionLayerTransparent(int layer)
        {
            for (var hop = 0; hop < _snapshot.LayerCount; hop++)
            {
                if (_snapshot.LayerOpacity[layer] <= 0) return true;
                var parent = LayerParentIndex(layer);
                if (parent < 0) return false;
                layer = parent;
            }
            throw new InvalidDataException("Cyclic composed layer parents.");
        }

        private void AppendCompositionLayers(XElement root)
        {
            var children = Enumerable.Range(0, _snapshot.LayerCount)
                .GroupBy(LayerParentIndex).ToDictionary(group => group.Key, group => group.ToArray());
            Append(root, -1, []);

            void Append(XElement parent, int parentLayer, HashSet<int> visiting)
            {
                if (!children.TryGetValue(parentLayer, out var siblings)) return;
                foreach (var layer in siblings.Reverse())
                {
                    if (!visiting.Add(layer)) throw new InvalidDataException("Cyclic composed layer parents.");
                    var group = new XElement(Svg + "g", new XAttribute("id", $"v2d-layer-{layer}"),
                        new XAttribute("data-v2d-layer-name", LayerName(layer)));
                    if (_snapshot.LayerOpacity[layer] < 1f)
                        group.SetAttributeValue("opacity", Number(_snapshot.LayerOpacity[layer]));
                    if (BlendModeAttribute(layer) is { } blend) group.SetAttributeValue("style", blend);
                    var paint = AppendCompositionClips(group, layer);
                    if (LayerKind(layer) == DrawingLayerKind.Folder)
                        Append(paint, layer, visiting);
                    else if (_objectsByLayer[layer].Length > 0
                        && CompositionClips(layer).All(mask => HasCompositionMaskCoverage(mask)))
                        AppendObjects(paint, _objectsByLayer[layer]);
                    if (paint.HasElements) parent.Add(group);
                    visiting.Remove(layer);
                }
            }
        }

        private IReadOnlyList<int> CompositionClips(int layer) =>
            _compositionMasks!.ClipsByLayer.TryGetValue(layer, out var masks) ? masks : [];

        private void ValidateCompositionMasks()
        {
            if (_compositionMasks is null) return;
            foreach (var pair in _compositionMasks.ContentsByMaskLayer)
            {
                if ((uint)pair.Key >= _snapshot.LayerCount || LayerKind(pair.Key) != DrawingLayerKind.Mask
                    || pair.Value.Any(layer => (uint)layer >= _snapshot.LayerCount))
                    throw new InvalidDataException("Invalid composed mask coverage indices.");
            }
            foreach (var pair in _compositionMasks.ClipsByLayer.Concat(_compositionMasks.ContainersByLayer))
            {
                if ((uint)pair.Key >= _snapshot.LayerCount
                    || pair.Value.Any(mask => !_compositionMasks.ContentsByMaskLayer.ContainsKey(mask)))
                    throw new InvalidDataException("Invalid composed mask reference.");
            }
        }

        private bool HasCompositionMaskCoverage(int mask, HashSet<int>? visiting = null)
        {
            if (_compositionCoverage.TryGetValue(mask, out var value)) return value;
            visiting ??= [];
            if (!visiting.Add(mask)) throw new InvalidDataException("Cyclic composed mask coverage.");
            try
            {
                value = _compositionMasks!.ContentsByMaskLayer[mask].Any(layer =>
                    _coverageObjectsByLayer[layer].Length > 0
                    && CompositionClips(layer).All(clip => HasCompositionMaskCoverage(clip, visiting)));
                _compositionCoverage.Add(mask, value);
                return value;
            }
            finally { visiting.Remove(mask); }
        }

        private XElement AppendCompositionClips(XElement parent, int layer)
        {
            foreach (var mask in CompositionClips(layer))
            {
                var clip = new XElement(Svg + "g", new XAttribute("mask", $"url(#v2d-mask-{mask})"));
                parent.Add(clip);
                parent = clip;
            }
            return parent;
        }

        private void AddCompositionMaskDefinitions(XElement root)
        {
            var definitions = root.Element(Svg + "defs")!;
            foreach (var pair in _compositionMasks!.ContentsByMaskLayer)
            {
                var mask = new XElement(Svg + "mask", new XAttribute("id", $"v2d-mask-{pair.Key}"),
                    new XAttribute("data-v2d-geometric-mask", "1"),
                    new XAttribute("maskUnits", "userSpaceOnUse"), new XAttribute("maskContentUnits", "userSpaceOnUse"));
                if (HasCompositionMaskCoverage(pair.Key))
                {
                    foreach (var layer in pair.Value)
                    {
                        if (CompositionClips(layer).Any(clip => !HasCompositionMaskCoverage(clip))) continue;
                        var source = new XElement(Svg + "g",
                            new XAttribute("id", $"v2d-mask-{pair.Key}-source-{layer}"));
                        var paint = AppendCompositionClips(source, layer);
                        foreach (var index in _coverageObjectsByLayer[layer])
                        {
                            if (DrawingObjectSvgCodec.CreateExportElement(_snapshot, index) is not { } element) continue;
                            element.SetAttributeValue("id", null);
                            OverrideToWhite(element);
                            paint.Add(element);
                        }
                        mask.Add(source);
                    }
                }
                definitions.Add(mask);
            }
        }

        private RectangleF? ClipCompositionBounds(int layer, RectangleF? bounds, SvgDocument document,
            ISvgRenderer renderer, Dictionary<int, RectangleF?> cache, HashSet<int> visiting)
        {
            foreach (var mask in CompositionClips(layer))
            {
                if (bounds is null) return null;
                var clip = MeasureCompositionMaskBounds(mask, document, renderer, cache, visiting);
                bounds = clip is { } rectangle ? RectangleF.Intersect(bounds.Value, rectangle) : null;
            }
            return bounds;
        }

        private RectangleF? MeasureCompositionMaskBounds(int mask, SvgDocument document,
            ISvgRenderer renderer, Dictionary<int, RectangleF?> cache, HashSet<int> visiting)
        {
            if (cache.TryGetValue(mask, out var found)) return found;
            if (!visiting.Add(mask)) throw new InvalidDataException("Cyclic composed mask bounds.");
            try
            {
                RectangleF? result = null;
                foreach (var layer in _compositionMasks!.ContentsByMaskLayer[mask])
                {
                    if (document.GetElementById($"v2d-mask-{mask}-source-{layer}") is not { } source) continue;
                    var bounds = ClipCompositionBounds(layer, MeasurePaintBounds(source, renderer),
                        document, renderer, cache, visiting);
                    if (bounds is { Width: > 0, Height: > 0 } visible) result = UnionBounds(result, visible);
                }
                cache.Add(mask, result);
                return result;
            }
            finally { visiting.Remove(mask); }
        }
    }
}
