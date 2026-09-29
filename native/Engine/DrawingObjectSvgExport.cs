using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Svg;

namespace VectorAnimationEngine;

/// <summary>
/// Writes a single drawing object (symbol) to a standalone, portable SVG document.
///
/// Unlike <see cref="DrawingObjectSvgCodec"/>, which persists the whole-object
/// metadata envelope owned by the managed project store, this emitter produces a
/// plain SVG meant to be used outside the application. Two contracts matter:
///
/// 1. Layer order. The stage draws layers from the highest index down to zero, so a
///    higher layer index sits underneath and layer 0 paints on top. SVG paints in
///    document order, therefore groups are emitted highest-layer-first and, within a
///    layer, in the engine's canonical stack order (ObjectOrder, then ObjectSubOrder,
///    then index) - the same bottom-to-top order Break Apart uses when flattening.
/// 2. Visible range. Only what the stage would actually show at the requested frame
///    is exported. A layer is included only when it and every ancestor folder are
///    visible, folder layers never contribute geometry, and masked content is
///    clipped through an SVG mask built from the visible mask-layer objects.
/// </summary>
internal static class DrawingObjectSvgExport
{
    private const string SvgVersion = "1.1";
    private const string SvgNamespaceUri = "http://www.w3.org/2000/svg";
    private const string ContentGroupId = "v2d-export";
    private const int MaxExportedObjects = 250_000;

    private static readonly XNamespace Svg = SvgNamespaceUri;

    /// <summary>
    /// Builds the SVG document for one symbol at <paramref name="frame"/>.
    /// </summary>
    /// <param name="drawingObjectId">Stable symbol id, recorded for traceability.</param>
    /// <param name="name">Display name used for the document title.</param>
    /// <param name="scene">Source scene; read without mutation.</param>
    /// <param name="frame">Stage frame whose active exposures are exported.</param>
    internal static XDocument CreateDocument(
        string drawingObjectId,
        string name,
        VectorScene scene,
        int frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingObjectId);
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.ObjectCount > MaxExportedObjects)
        {
            throw new InvalidOperationException("The symbol has too many objects to export.");
        }

        // Geometry emission is shared with the stored whole-object preview, which
        // works from a snapshot. Active-set and visibility decisions use the live
        // scene so the export matches what the stage actually shows.
        var snapshot = scene.CreateSnapshot();
        var layout = new ExportLayout(snapshot, index => scene.IsObjectActive(index, frame));
        var root = new XElement(
            Svg + "svg",
            new XAttribute("version", SvgVersion),
            new XAttribute("data-v2d-drawing-object-id", drawingObjectId),
            new XAttribute("data-v2d-frame", frame.ToString(CultureInfo.InvariantCulture)),
            new XElement(Svg + "title", string.IsNullOrWhiteSpace(name) ? drawingObjectId : name),
            new XElement(Svg + "defs"));

        layout.AddGradientDefinitions(root);
        layout.AddMaskDefinitions(root);

        var content = new XElement(Svg + "g", new XAttribute("id", ContentGroupId));
        layout.AppendLayers(content);
        root.Add(content);
        layout.SetContentViewport(root);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>
    /// Writes the SVG document for one symbol to <paramref name="path"/>.
    /// </summary>
    internal static void Write(
        string path,
        string drawingObjectId,
        string name,
        VectorScene scene,
        int frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = CreateDocument(drawingObjectId, name, scene, frame);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineHandling = NewLineHandling.None
        };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    /// <summary>
    /// Resolves the ordered object list the stage would draw at the export frame.
    /// Exposed for regression coverage.
    /// </summary>
    internal static int[] VisibleObjectsInPaintOrder(VectorScene scene, int frame)
    {
        var snapshot = scene.CreateSnapshot();
        return new ExportLayout(snapshot, index => scene.IsObjectActive(index, frame)).OrderedObjects;
    }

    /// <summary>
    /// Precomputed per-export view over a scene snapshot: effective layer
    /// visibility, the frame's active objects, and their paint order.
    /// </summary>
    private sealed class ExportLayout
    {
        private readonly VectorSceneSnapshot _snapshot;
        private readonly int[][] _objectsByLayer;
        private readonly int[][] _maskObjectsByLayer;

        internal ExportLayout(VectorSceneSnapshot snapshot, Func<int, bool> isObjectActive)
        {
            _snapshot = snapshot;
            var active = new List<int>(snapshot.ObjectCount);
            var maskObjects = new List<int>(snapshot.ObjectCount);
            for (var index = 0; index < snapshot.ObjectCount; index++)
            {
                // IsObjectActive applies recursive layer visibility and the frame's
                // active exposure, but it still admits folder and mask layers, which
                // own no directly drawn content here and are filtered explicitly.
                if (!isObjectActive(index)) continue;
                switch (LayerKind(snapshot.ObjectLayer[index]))
                {
                    case DrawingLayerKind.Drawing:
                        active.Add(index);
                        break;
                    // Mask geometry is not drawn directly; it is kept aside so the
                    // owning content layer can be clipped by it.
                    case DrawingLayerKind.Mask:
                        maskObjects.Add(index);
                        break;
                }
            }

            active.Sort((a, b) => ComparePaintOrder(snapshot, a, b));
            OrderedObjects = active.ToArray();
            _maskObjectsByLayer = BucketByLayer(maskObjects, snapshot);
            _objectsByLayer = BucketByLayer(OrderedObjects, snapshot);
        }

        private static int[][] BucketByLayer(IReadOnlyList<int> objects, VectorSceneSnapshot snapshot)
        {
            var result = new int[snapshot.LayerCount][];
            var buckets = new List<int>?[snapshot.LayerCount];
            foreach (var index in objects)
            {
                (buckets[snapshot.ObjectLayer[index]] ??= []).Add(index);
            }

            for (var layer = 0; layer < snapshot.LayerCount; layer++)
            {
                result[layer] = buckets[layer]?.ToArray() ?? [];
            }

            return result;
        }

        internal int[] OrderedObjects { get; }

        /// <summary>
        /// Measures the emitted geometry, rather than the stage or selection bounds.
        /// Keep coordinates in vector units; explicit pixel dimensions preserve the
        /// authored size when the standalone SVG is imported again (25 vu = 1 px).
        /// </summary>
        internal void SetContentViewport(XElement root)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)));
            var document = SvgDocument.Open<SvgDocument>(stream, new SvgOptions())
                ?? throw new InvalidDataException("The exported SVG document could not be measured.");
            using var bitmap = new Bitmap(1, 1);
            using var renderer = SvgRenderer.FromImage(bitmap);
            RectangleF? contentBounds = null;
            var maskBounds = new Dictionary<int, RectangleF?>();
            for (var layer = 0; layer < _snapshot.LayerCount; layer++)
            {
                if (document.GetElementById($"v2d-layer-{layer.ToString(CultureInfo.InvariantCulture)}") is not { } group) continue;
                var bounds = MeasurePaintBounds(group, renderer);
                if (bounds is null) continue;
                if (TryGetUsableMask(layer, out var maskLayer))
                {
                    if (!maskBounds.TryGetValue(maskLayer, out var mask))
                    {
                        var maskElement = document.GetElementById($"v2d-mask-{maskLayer.ToString(CultureInfo.InvariantCulture)}");
                        mask = maskElement is null ? null : MeasurePaintBounds(maskElement, renderer);
                        maskBounds.Add(maskLayer, mask);
                    }
                    // A mask contributes coverage, never an independent canvas extent.
                    bounds = mask is { } clip ? RectangleF.Intersect(bounds.Value, clip) : null;
                }
                if (bounds is { Width: > 0, Height: > 0 } visible)
                {
                    contentBounds = UnionBounds(contentBounds, visible);
                }
            }

            // Empty frames still need a valid, finite SVG viewport.
            var viewport = contentBounds ?? new RectangleF(0, 0, 1, 1);
            root.SetAttributeValue("viewBox", $"{Number(viewport.X)} {Number(viewport.Y)} {Number(viewport.Width)} {Number(viewport.Height)}");
            root.SetAttributeValue("width", Number(VectorUnits.ToPixels(viewport.Width)));
            root.SetAttributeValue("height", Number(VectorUnits.ToPixels(viewport.Height)));
            // Default percentage mask regions are not stable for an offset viewBox.
            // Use the final canvas in user coordinates so tight negative-coordinate
            // exports retain the same coverage as the authored mask.
            foreach (var mask in root.Element(Svg + "defs")!.Elements(Svg + "mask"))
            {
                mask.SetAttributeValue("x", Number(viewport.X));
                mask.SetAttributeValue("y", Number(viewport.Y));
                mask.SetAttributeValue("width", Number(viewport.Width));
                mask.SetAttributeValue("height", Number(viewport.Height));
            }
        }

        private static RectangleF? MeasurePaintBounds(SvgElement element, ISvgRenderer renderer)
        {
            if (element is SvgVisualElement { Opacity: <= 0 }) return null;
            if (element is SvgGroup or SvgMask)
            {
                RectangleF? groupBounds = null;
                foreach (var child in element.Children)
                {
                    if (MeasurePaintBounds(child, renderer) is { } bounds)
                    {
                        groupBounds = UnionBounds(groupBounds, bounds);
                    }
                }
                return groupBounds;
            }
            if (element is not SvgVisualElement visual) return null;
            var sourcePath = visual.Path(renderer);
            if (sourcePath is null || sourcePath.PointCount == 0) return null;
            using var path = (GraphicsPath)sourcePath.Clone();
            using var transform = visual.Transforms?.GetMatrix();
            RectangleF? paintedBounds = null;
            if (visual is SvgImage || HasPaint(visual.Fill, visual.FillOpacity))
            {
                paintedBounds = path.GetBounds(transform);
            }
            if (HasPaint(visual.Stroke, visual.StrokeOpacity) && visual.StrokeWidth.Value > 0)
            {
                // The emitter uses round joins and either round or butt caps.
                using var pen = new Pen(Color.Black, visual.StrokeWidth.Value)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = visual.StrokeLineCap == SvgStrokeLineCap.Round ? LineCap.Round : LineCap.Flat,
                    EndCap = visual.StrokeLineCap == SvgStrokeLineCap.Round ? LineCap.Round : LineCap.Flat
                };
                // GetBounds(matrix, pen) adds a conservative full pen width on
                // each side. Measure the actual widened stroke outline instead.
                using var strokeOutline = (GraphicsPath)path.Clone();
                strokeOutline.Widen(pen, null, 0.01f);
                paintedBounds = UnionBounds(paintedBounds, strokeOutline.GetBounds(transform));
            }
            return paintedBounds;
        }

        private static bool HasPaint(SvgPaintServer? paint, float opacity) =>
            opacity > 0 && paint is not null && paint != SvgPaintServer.None
            && (paint is not SvgColourServer colour || colour.Colour.A > 0);

        private static RectangleF UnionBounds(RectangleF? accumulated, RectangleF bounds)
        {
            if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
                || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom))
            {
                throw new InvalidDataException("The exported SVG has non-finite geometry bounds.");
            }
            return accumulated is { } previous ? RectangleF.Union(previous, bounds) : bounds;
        }

        /// <summary>
        /// Emits one group per visible layer, highest layer index first so the
        /// bottom-most layer lands at the start of the document. Layers the stage
        /// skips (folders, hidden layers, mask layers, locked/absent masks) are
        /// omitted or clipped.
        /// </summary>
        internal void AppendLayers(XElement content)
        {
            for (var layer = _snapshot.LayerCount - 1; layer >= 0; layer--)
            {
                if (_objectsByLayer[layer].Length == 0) continue;

                var group = new XElement(
                    Svg + "g",
                    new XAttribute("id", $"v2d-layer-{layer.ToString(CultureInfo.InvariantCulture)}"),
                    new XAttribute("data-v2d-layer-name", LayerName(layer)));

                var opacity = _snapshot.LayerOpacity.Length > layer ? _snapshot.LayerOpacity[layer] : 1f;
                if (opacity < 1f) group.SetAttributeValue("opacity", Number(opacity));
                if (BlendModeAttribute(layer) is { } blend) group.SetAttributeValue("style", blend);
                if (TryGetUsableMask(layer, out var maskLayer))
                {
                    group.SetAttributeValue(
                        "mask",
                        $"url(#v2d-mask-{maskLayer.ToString(CultureInfo.InvariantCulture)})");
                }

                AppendObjects(group, _objectsByLayer[layer]);
                content.Add(group);
            }
        }

        /// <summary>
        /// Declares an SVG mask for every layer whose mask layer is visibly usable.
        /// A mask is a luminance mask, so mask content is forced to opaque white;
        /// only its coverage survives, matching the renderer's geometric mask.
        /// </summary>
        internal void AddMaskDefinitions(XElement root)
        {
            var definitions = root.Element(Svg + "defs");
            if (definitions is null) return;
            for (var layer = 0; layer < _snapshot.LayerCount; layer++)
            {
                if (_objectsByLayer[layer].Length == 0) continue;
                if (!TryGetUsableMask(layer, out var maskLayer)) continue;

                var maskObjects = _maskObjectsByLayer[maskLayer];
                if (maskObjects.Length == 0) continue;

                var mask = new XElement(
                    Svg + "mask",
                    new XAttribute("id", $"v2d-mask-{maskLayer.ToString(CultureInfo.InvariantCulture)}"),
                    new XAttribute("maskUnits", "userSpaceOnUse"),
                    new XAttribute("maskContentUnits", "userSpaceOnUse"));
                var maskPaint = new XElement(
                    Svg + "g",
                    new XAttribute("fill", "#FFFFFF"),
                    new XAttribute("stroke", "#FFFFFF"),
                    new XAttribute("fill-opacity", "1"),
                    new XAttribute("stroke-opacity", "1"));
                foreach (var index in maskObjects)
                {
                    if (DrawingObjectSvgCodec.CreateExportElement(_snapshot, index) is { } element)
                    {
                        OverrideToWhite(element);
                        maskPaint.Add(element);
                    }
                }

                if (maskPaint.HasElements)
                {
                    mask.Add(maskPaint);
                    definitions.Add(mask);
                }
            }
        }

        internal void AddGradientDefinitions(XElement root)
        {
            var definitions = root.Element(Svg + "defs");
            if (definitions is null) return;
            foreach (var index in OrderedObjects)
            {
                if (DrawingObjectSvgCodec.CreateExportGradient(_snapshot, index) is { } gradient)
                {
                    definitions.Add(gradient);
                }
            }
        }

        private void AppendObjects(XElement parent, IReadOnlyList<int> objects)
        {
            foreach (var index in objects)
            {
                if (DrawingObjectSvgCodec.CreateExportElement(_snapshot, index) is { } element)
                {
                    parent.Add(element);
                }
            }
        }

        /// <summary>
        /// Forces an element subtree to opaque white so a luminance mask keeps the
        /// authored coverage while ignoring the layer's own paint colours. A fill of
        /// <c>none</c> is preserved: stroke-only content such as lines and open
        /// freehand paths must not gain a fill, or the mask would cover more than the
        /// stage actually clips to. Gradients are replaced by flat white because a
        /// mask must not re-use the layer's paint definitions.
        /// </summary>
        private static void OverrideToWhite(XElement element)
        {
            ApplyWhite(element);
            foreach (var descendant in element.Descendants())
            {
                ApplyWhite(descendant);
            }
        }

        private static void ApplyWhite(XElement element)
        {
            if (!IsNone(element.Attribute("fill")))
            {
                element.SetAttributeValue("fill", "#FFFFFF");
                element.SetAttributeValue("fill-opacity", "1");
            }

            if (!IsNone(element.Attribute("stroke")))
            {
                element.SetAttributeValue("stroke", "#FFFFFF");
                element.SetAttributeValue("stroke-opacity", "1");
            }
        }

        private static bool IsNone(XAttribute? attribute) =>
            string.Equals(attribute?.Value, "none", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves a usable mask for a layer: it must exist, be a mask layer, be
        /// effectively visible and unlocked, and hold visible content.
        /// </summary>
        private bool TryGetUsableMask(int layer, out int maskLayer)
        {
            maskLayer = -1;
            if (LayerKind(layer) != DrawingLayerKind.Drawing) return false;
            if (_snapshot.LayerMaskIds.Length <= layer) return false;
            var maskId = _snapshot.LayerMaskIds[layer];
            if (string.IsNullOrWhiteSpace(maskId)) return false;

            var candidate = Array.IndexOf(_snapshot.LayerIds, maskId);
            if (candidate < 0 || LayerKind(candidate) != DrawingLayerKind.Mask) return false;
            if (!IsLayerEffectivelyVisible(candidate)) return false;
            if (_snapshot.LayerLocked.Length > candidate && _snapshot.LayerLocked[candidate]) return false;
            maskLayer = candidate;
            return true;
        }

        /// <summary>
        /// Mirrors <c>VectorScene.IsLayerEffectivelyVisible</c>: a layer is visible
        /// only when it and every ancestor folder chain are visible.
        /// </summary>
        private bool IsLayerEffectivelyVisible(int layer)
        {
            var current = layer;
            for (var hop = 0; hop < _snapshot.LayerCount; hop++)
            {
                if ((uint)current >= _snapshot.LayerCount || !LayerVisible(current)) return false;
                var parent = LayerParentIndex(current);
                if (parent < 0) return true;
                current = parent;
            }

            return false;
        }

        private bool LayerVisible(int layer) =>
            _snapshot.LayerVisible.Length > layer && _snapshot.LayerVisible[layer];

        private int LayerParentIndex(int layer)
        {
            if ((uint)layer >= _snapshot.LayerCount || layer >= _snapshot.LayerParentIds.Length) return -1;
            var parentId = _snapshot.LayerParentIds[layer];
            if (string.IsNullOrWhiteSpace(parentId)) return -1;
            var parent = Array.IndexOf(_snapshot.LayerIds, parentId);
            return parent >= 0 && LayerKind(parent) == DrawingLayerKind.Folder ? parent : -1;
        }

        private string LayerName(int layer) =>
            _snapshot.LayerNames.Length > layer ? _snapshot.LayerNames[layer] : "";

        /// <summary>
        /// Maps an engine blend mode onto its CSS/SVG equivalent. Modes without a
        /// standard equivalent fall back to normal rendering rather than guessing.
        /// </summary>
        private string? BlendModeAttribute(int layer)
        {
            if (_snapshot.LayerBlendModes.Length <= layer) return null;
            var mode = _snapshot.LayerBlendModes[layer];
            if (mode == LayerBlendMode.Normal) return null;
            var css = mode switch
            {
                LayerBlendMode.Multiply => "multiply",
                LayerBlendMode.Screen => "screen",
                LayerBlendMode.Darken => "darken",
                LayerBlendMode.Lighten => "lighten",
                LayerBlendMode.ColorBurn => "color-burn",
                LayerBlendMode.ColorDodge => "color-dodge",
                LayerBlendMode.Overlay => "overlay",
                LayerBlendMode.SoftLight => "soft-light",
                LayerBlendMode.HardLight => "hard-light",
                LayerBlendMode.Difference => "difference",
                LayerBlendMode.Exclusion => "exclusion",
                LayerBlendMode.Hue => "hue",
                LayerBlendMode.Saturation => "saturation",
                LayerBlendMode.Color => "color",
                LayerBlendMode.Luminosity => "luminosity",
                LayerBlendMode.LinearDodge => "plus-lighter",
                _ => null
            };

            return css is null ? null : $"mix-blend-mode:{css}";
        }

        private DrawingLayerKind LayerKind(int layer) =>
            _snapshot.LayerKinds.Length > layer ? _snapshot.LayerKinds[layer] : DrawingLayerKind.Drawing;
    }

    /// <summary>
    /// Canonical render-order comparison: higher layer index first (it paints first,
    /// therefore sits underneath), then the engine's stack key. The result is a
    /// bottom-to-top list suitable for direct SVG document order.
    /// </summary>
    private static int ComparePaintOrder(VectorSceneSnapshot snapshot, int a, int b)
    {
        var comparison = snapshot.ObjectLayer[b].CompareTo(snapshot.ObjectLayer[a]);
        if (comparison != 0) return comparison;

        comparison = snapshot.ObjectOrder[a].CompareTo(snapshot.ObjectOrder[b]);
        if (comparison != 0) return comparison;
        comparison = SubOrder(snapshot, a).CompareTo(SubOrder(snapshot, b));
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    private static double SubOrder(VectorSceneSnapshot snapshot, int index) =>
        index < snapshot.ObjectSubOrder.Length ? snapshot.ObjectSubOrder[index] : 0d;

    /// <summary>
    /// Resolves a layer's parent folder index, tolerating dangling ids.
    /// </summary>
    private static int LayerParentIndex(VectorSceneSnapshot snapshot, int layer)
    {
        if ((uint)layer >= snapshot.LayerCount || layer >= snapshot.LayerParentIds.Length) return -1;
        var parentId = snapshot.LayerParentIds[layer];
        if (string.IsNullOrWhiteSpace(parentId)) return -1;
        var parent = Array.IndexOf(snapshot.LayerIds, parentId);
        return parent >= 0 && LayerKind(snapshot, parent) == DrawingLayerKind.Folder ? parent : -1;
    }

    private static bool LayerVisible(VectorSceneSnapshot snapshot, int layer) =>
        snapshot.LayerVisible.Length > layer && snapshot.LayerVisible[layer];

    private static DrawingLayerKind LayerKind(VectorSceneSnapshot snapshot, int layer) =>
        snapshot.LayerKinds.Length > layer ? snapshot.LayerKinds[layer] : DrawingLayerKind.Drawing;

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
