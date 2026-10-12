using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal sealed record FlashImportDiagnostic(string Code, string Location, string Message);
internal sealed record FlashDocumentImportResult(
    VectorProject Project, string Name, decimal Fps, int FrameCount, float Width, float Height,
    IReadOnlyList<FlashImportDiagnostic> Diagnostics, IReadOnlyList<string>? TemporaryFiles = null);

internal static partial class FlashDocumentImporter
{
    private const int MaximumFrameCount = 10_000;
    private const int MaximumSymbols = 16384;
    private const int MaximumElements = 100_000;
    private const int MaximumStateKeys = 500_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private sealed record TimelineSpec(string Name, XElement Xml, int Duration, string SymbolType = "");

    internal static FlashDocumentImportResult Load(string path, bool editableGeometry = true)
    {
        var temporaryFiles = new List<string>();
        try { return LoadCore(path, temporaryFiles, editableGeometry); }
        catch { DeleteTemporaryImages(temporaryFiles); throw; }
    }

    private static FlashDocumentImportResult LoadCore(string path, List<string> temporaryFiles, bool editableGeometry)
    {
        using var source = new XflSource(path);
        var document = source.ReadXml("DOMDocument.xml");
        if (document.Name.LocalName != "DOMDocument") throw Failure("document", "Expected DOMDocument root.");
        var diagnostics = new List<FlashImportDiagnostic>();
        RejectVisualEffects(document, "document");
        var width = Number(document, "width", 550, 1, 100_000);
        var height = Number(document, "height", 400, 1, 100_000);
        var fps = (decimal)Number(document, "frameRate", 24, 1, 120);
        DiagnoseNonFrameScripts(document, "document", diagnostics);
        var timelines = Children(Child(document, "timelines"), "DOMTimeline")
            .Select((xml, index) => ParseTimeline(xml, Text(xml, "name", $"Scene {index + 1}"))).ToArray();
        if (timelines.Length is 0 or > 128) throw Failure("document", "Expected 1 to 128 scene timelines.");
        var library = new Dictionary<string, TimelineSpec>(StringComparer.Ordinal);
        foreach (var include in Children(Child(document, "symbols"), "Include"))
        {
            if (library.Count >= MaximumSymbols) throw Failure("library", "Too many library symbols.");
            var href = XflSource.SafeRelativePath(Required(include, "href"));
            var symbolXml = source.ReadXml("LIBRARY/" + href);
            if (symbolXml.Name.LocalName != "DOMSymbolItem") throw Failure(href, "Expected DOMSymbolItem root.");
            var name = Required(symbolXml, "name");
            var symbolType = Text(symbolXml, "symbolType", "movie clip");
            if (symbolType == "button") throw Failure(name, "Interactive button symbols require conversion through Adobe Animate.");
            if (symbolType is not ("graphic" or "movie clip")) throw Failure(name, "Unknown symbol type: " + symbolType);
            RejectVisualEffects(symbolXml, name);
            DiagnoseNonFrameScripts(symbolXml, name, diagnostics);
            if (symbolXml.Attribute("scale9Grid") is not null || Child(symbolXml, "scale9Grid") is not null)
                throw Failure(name, "Scale-nine symbols require Adobe visual conversion.");
            var timeline = Child(Child(symbolXml, "timeline"), "DOMTimeline") ?? throw Failure(name, "Missing symbol timeline.");
            if (!library.TryAdd(name, ParseTimeline(timeline, name) with { SymbolType = symbolType })) throw Failure(name, "Duplicate library symbol name.");
        }
        ValidateReferences(library, timelines);
        foreach (var spec in library.Values.Concat(timelines)) PreflightVisualEffects(spec);
        foreach (var item in document.Descendants().Where(node => node.Name.LocalName is "DOMSoundItem" or "DOMVideoItem"))
            diagnostics.Add(new("media-not-converted", "library/" + Text(item, "name", item.Name.LocalName),
                "Sound/video library data is not converted; placed video content is rejected."));
        var project = VectorProject.CreateEmpty();
        project.Name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
        var folders = ImportLibraryFolders(project, document, library.Keys.Concat(
            document.Descendants().Where(node => node.Name.LocalName == "DOMBitmapItem").Select(item => Required(item, "name"))), diagnostics);
        var symbols = new Dictionary<string, DrawingObjectDefinition>(StringComparer.Ordinal);
        foreach (var (name, spec) in library)
        {
            var symbol = symbols.Count == 0 ? project.DrawingObjects[0] : project.AddDrawingObject(name);
            symbol.Name = name.Split('/')[^1];
            symbol.Detail = $"Imported XFL {spec.SymbolType} library symbol: {name}";
            var separator = name.LastIndexOf('/');
            if (separator >= 0 && !project.TryMoveDrawingObjectToAssetFolder(symbol.Id, folders[name[..separator]]))
                throw Failure(name, "Could not assign the native library folder.");
            symbol.Scene.CreateEmpty(frameCount: spec.Duration); // All durations exist before any forward reference is sampled.
            symbols.Add(name, symbol);
        }
        var bitmaps = ImportBitmaps(project, document, source, folders, temporaryFiles);
        var builder = new ImportBuilder(project, symbols, bitmaps, fps, diagnostics, editableGeometry);
        foreach (var (name, spec) in library) builder.Build(symbols[name], spec);
        for (var index = 0; index < timelines.Length; index++)
        {
            var spec = timelines[index];
            var wrapper = library.Count == 0 && index == 0 ? project.DrawingObjects[0] : project.AddDrawingObject(spec.Name);
            wrapper.Name = spec.Name + " Timeline";
            builder.Build(wrapper, spec);
            var scene = index == 0 ? project.Scenes[0] : project.AddScene(spec.Name);
            scene.Name = spec.Name;
            RestoreNativeScene(project, scene, wrapper, fps, diagnostics);
        }
        var frameCount = timelines.Max(item => item.Duration);
        project.TrySetPlaybackSettings(fps, true, 0, frameCount - 1);
        return new(project, project.Name, fps, frameCount, width, height, diagnostics.AsReadOnly(), temporaryFiles.AsReadOnly());
    }

    private static TimelineSpec ParseTimeline(XElement xml, string name)
    {
        var duration = 1;
        var layers = Children(Child(xml, "layers"), "DOMLayer").ToArray();
        if (layers.Length is 0 or > 4096) throw Failure(name, "Expected 1 to 4096 layers.");
        foreach (var layer in layers)
        {
            var end = 0;
            foreach (var frame in Children(Child(layer, "frames"), "DOMFrame"))
            {
                var index = Integer(frame, "index", 0, 0, MaximumFrameCount - 1);
                var count = Integer(frame, "duration", 1, 1, MaximumFrameCount);
                if (index < end || index + count > MaximumFrameCount)
                    throw Failure(name, "Overlapping, unordered, or oversized frame exposures.");
                end = index + count;
                duration = Math.Max(duration, end);
            }
        }
        return new(name, xml, duration);
    }

    private static void ValidateReferences(Dictionary<string, TimelineSpec> library, TimelineSpec[] scenes)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var heights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in library.Keys) Visit(name, 0);
        foreach (var scene in scenes) Check(scene);
        void Check(TimelineSpec spec)
        {
            foreach (var instance in spec.Xml.Descendants().Where(node => node.Name.LocalName == "DOMSymbolInstance"))
                if (!library.ContainsKey(Required(instance, "libraryItemName")))
                    throw Failure(spec.Name, "Missing library symbol: " + Required(instance, "libraryItemName"));
        }
        int Visit(string name, int depth)
        {
            if (depth > 64) throw Failure(name, "Symbol nesting exceeds 64 levels.");
            if (state.TryGetValue(name, out var previous))
            {
                if (previous == 1) throw Failure(name, "Recursive library symbol reference.");
                if (depth + heights[name] > 64) throw Failure(name, "Symbol nesting exceeds 64 levels.");
                return heights[name];
            }
            state[name] = 1;
            Check(library[name]);
            var height = 0;
            foreach (var child in library[name].Xml.Descendants().Where(node => node.Name.LocalName == "DOMSymbolInstance"))
                height = Math.Max(height, 1 + Visit(Required(child, "libraryItemName"), depth + 1));
            if (height > 64) throw Failure(name, "Symbol nesting exceeds 64 levels.");
            heights[name] = height;
            state[name] = 2;
            return height;
        }
    }

    private sealed class ImportBuilder(VectorProject project,
        Dictionary<string, DrawingObjectDefinition> symbols, Dictionary<string, DrawingObjectDefinition> bitmaps,
        decimal fps, List<FlashImportDiagnostic> diagnostics, bool editableGeometry)
    {
        private int _elements;
        private int _stateKeys;

        internal void Build(DrawingObjectDefinition target, TimelineSpec spec)
        {
            RejectVisualEffects(spec.Xml, spec.Name);
            var layers = Children(Child(spec.Xml, "layers"), "DOMLayer").ToArray();
            var native = target.Scene;
            var exported = Enumerable.Range(0, layers.Length).Where(index => Text(layers[index], "layerType", "normal") != "guide").ToArray();
            var nativeIndices = exported.Select((source, index) => (source, index)).ToDictionary(pair => pair.source, pair => pair.index);
            native.CreateEmpty(Math.Max(1, exported.Length), spec.Duration);
            foreach (var guide in layers.Where(layer => Text(layer, "layerType", "normal") == "guide"))
                diagnostics.Add(new("guide-layer-omitted", spec.Name + "/" + Text(guide, "name", "Guide"),
                    "Non-exporting guide artwork was omitted; bound classic motion is baked on the guided layer."));
            foreach (var sourceIndex in exported)
            {
                var layerIndex = nativeIndices[sourceIndex];
                var layer = layers[sourceIndex];
                XElement? guideLayer = null;
                var location = spec.Name + "/" + Text(layer, "name", $"Layer {layerIndex + 1}");
                native.LayerNames[layerIndex] = Text(layer, "name", $"Layer {layerIndex + 1}");
                native.LayerVisible[layerIndex] = Boolean(layer, "visible", true);
                native.LayerLocked[layerIndex] = Boolean(layer, "locked", false);
                native.LayerOutline[layerIndex] = Boolean(layer, "outline", false);
                if (layer.Attribute("color") is not null)
                    native.LayerColorArgb[layerIndex] = ReadLayerColor(layer, location).ToArgb();
                var kind = Text(layer, "layerType", "normal");
                if (kind == "folder") native.LayerKinds[layerIndex] = DrawingLayerKind.Folder;
                else if (kind is not ("normal" or "guided")) throw Failure(location, $"Layer type '{kind}' requires visual conversion (mask/rig layers are not approximated).");
                if (layer.Attribute("parentLayerIndex") is not null)
                {
                    var parent = Integer(layer, "parentLayerIndex", -1, 0, layers.Length - 1);
                    if (parent >= sourceIndex || Text(layers[parent], "layerType", "normal") is not ("folder" or "guide"))
                        throw Failure(location, "Invalid or unsupported layer parent relationship.");
                    if (Text(layers[parent], "layerType", "normal") == "guide")
                    {
                        guideLayer = layers[parent];
                        if (guideLayer.Attribute("parentLayerIndex") is not null)
                        {
                            var folder = Integer(guideLayer, "parentLayerIndex", -1, 0, layers.Length - 1);
                            if (folder >= parent || Text(layers[folder], "layerType", "normal") != "folder")
                                throw Failure(location, "Invalid guide folder relationship.");
                            native.LayerParentIds[layerIndex] = native.LayerIds[nativeIndices[folder]];
                        }
                    }
                    else native.LayerParentIds[layerIndex] = native.LayerIds[nativeIndices[parent]];
                }
                if (kind == "guided" && guideLayer is null) throw Failure(location, "Guided layer has no guide parent.");
                RejectVisualEffects(layer, location);
                var frames = Children(Child(layer, "frames"), "DOMFrame").ToArray();
                if (kind == "folder")
                {
                    if (frames.Any(frame => Children(Child(frame, "elements")).Any()))
                        throw Failure(location, "Folder layers cannot own visual content.");
                    continue;
                }
                var track = native.Timeline.FindTrackByTargetId(native.LayerIds[layerIndex])!;
                for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    var start = Integer(frame, "index", 0, 0, spec.Duration - 1);
                    var duration = Integer(frame, "duration", 1, 1, MaximumFrameCount);
                    var end = start + duration;
                    var frameLocation = location + $"/frame {start + 1}";
                    RejectVisualEffects(frame, frameLocation);
                    EnsureChildNames(frame, frameLocation, "elements", "Actionscript", "script", "tweens", "motionObjectXML", "morphShape", "persistentData");
                    if (frame.Descendants().Any(node => node.Name.LocalName is "Actionscript" or "script"))
                        diagnostics.Add(new("script-not-converted", frameLocation, "Frame scripts are not executed or converted; they may change playback behavior."));
                    if (!string.IsNullOrEmpty(frame.Attribute("soundName")?.Value))
                        diagnostics.Add(new("sound-not-converted", frameLocation, "Timeline sound is not converted."));
                    if (!string.IsNullOrEmpty(frame.Attribute("name")?.Value))
                        diagnostics.Add(new("label-not-converted", frameLocation, "Frame labels are not stored in the V2D timeline."));
                    var tween = Text(frame, "tweenType", "none");
                    if (Child(frame, "motionObjectXML") is not null || Child(frame, "morphShape") is not null)
                        throw Failure(frameLocation, "Object/shape tween geometry requires Adobe visual conversion.");
                    var elements = Children(Child(frame, "elements")).ToArray();
                    XElement? endpoint = null;
                    if (tween is not ("none" or "") && !(tween == "motion" && frameIndex + 1 == frames.Length))
                    {
                        if (tween != "motion" || Child(frame, "motionObjectXML") is not null
                            || elements.Length != 1 || elements[0].Name.LocalName != "DOMSymbolInstance"
                            || frameIndex + 1 >= frames.Length)
                            throw Failure(frameLocation, "This shape/object/complex motion tween requires Adobe visual conversion.");
                        var next = frames[frameIndex + 1];
                        var nextElements = Children(Child(next, "elements")).ToArray();
                        if (Integer(next, "index", 0, 0, spec.Duration - 1) != end || nextElements.Length != 1
                            || nextElements[0].Name.LocalName != "DOMSymbolInstance"
                            || Required(nextElements[0], "libraryItemName") != Required(elements[0], "libraryItemName")
                            || Integer(frame, "motionTweenRotateTimes", 0, 0, 1000) != 0
                            || Text(frame, "motionTweenRotate", "none") is not ("none" or "auto")
                            || (guideLayer is null && Boolean(frame, "motionTweenOrientToPath", false))
                            || !Boolean(frame, "motionTweenScale", true)
                            || Children(Child(frame, "tweens")).Any(ease => ease.Name.LocalName is not ("CustomEase" or "Ease")))
                            throw Failure(frameLocation, "The classic tween endpoints or custom rotation/easing cannot be mapped exactly.");
                        endpoint = nextElements[0];
                        diagnostics.Add(new("tween-baked", frameLocation, "Classic instance tween was baked to editable frame states."));
                    }
                    native.EditFrame = start;
                    var guideMotion = endpoint is not null && guideLayer is not null
                        ? ReadGuideMotion(guideLayer, frame, elements[0], endpoint, start, end, frameLocation) : null;
                    if (guideMotion is not null) diagnostics.Add(new("guide-motion-baked", frameLocation,
                        "Guide motion was baked to ordinary editable keys using arc length, easing and the transformation point. Numerical arc-length tolerance is 0.01 stage pixel; Adobe visual parity has not been verified."));
                    native.Timeline.InsertBlankKeyframe(track.Id, start);
                    if (native.GetLayerKind(layerIndex) == DrawingLayerKind.Folder && elements.Length != 0)
                        throw Failure(frameLocation, "Folder layers cannot own visual content.");
                    // DOM elements are stored back-to-front; native composition layer zero is frontmost.
                    foreach (var element in elements.Reverse())
                        AddElement(target, layerIndex, element, Matrix3x2.Identity, start, end, frameLocation, endpoint,
                            Number(frame, "acceleration", Number(frame, "tweenEasing", 0, -100, 100), -100, 100),
                            endpoint is null ? null : ReadCustomEase(frame, frameLocation),
                            endpoint is null ? "none" : ReadEaseMethod(frame, frameLocation), guideMotion);
                    if (endpoint is not null)
                    {
                        // Only baked tweens need per-frame keys. Ordinary Flash exposures remain
                        // held native Cels; nested symbol playback samples the actual parent frame.
                        for (var sample = start; sample < end; sample++) native.Timeline.InsertKeyframe(track.Id, sample);
                    }
                    if (end < spec.Duration) native.Timeline.InsertBlankKeyframe(track.Id, end);
                }
            }
            native.EditFrame = 0;
            target.SynchronizeTimelineTracks();
        }

        private void AddElement(DrawingObjectDefinition target, int layer, XElement element, Matrix3x2 inherited,
            int start, int end, string location, XElement? endpoint, float acceleration, Vector2[]? customEase = null, string easeMethod = "none", GuideMotion? guideMotion = null)
        {
            if (++_elements > MaximumElements) throw Failure(location, "Too many imported visual elements.");
            RejectVisualEffects(element, location);
            var matrix = ReadMatrix(element) * inherited;
            if (element.Name.LocalName == "DOMGroup")
            {
                EnsureChildNames(element, location, "matrix", "transformationPoint", "members", "persistentData");
                if (Child(element, "color") is not null) throw Failure(location, "Group color transforms require Adobe visual conversion.");
                foreach (var child in Children(Child(element, "members")).Reverse())
                    AddElement(target, layer, child, matrix, start, end, location, null, 0);
                return;
            }
            DrawingObjectDefinition childSymbol;
            if (element.Name.LocalName == "DOMSymbolInstance")
            {
                EnsureChildNames(element, location, "matrix", "transformationPoint", "color", "filters", "persistentData");
                childSymbol = symbols[Required(element, "libraryItemName")];
            }
            else if (element.Name.LocalName == "DOMBitmapInstance")
            {
                EnsureChildNames(element, location, "matrix", "transformationPoint", "color", "filters", "persistentData");
                var bitmapName = Required(element, "libraryItemName");
                if (!bitmaps.TryGetValue(bitmapName, out childSymbol!))
                    throw Failure(location, "Missing library bitmap: " + bitmapName);
            }
            else if (element.Name.LocalName == "DOMStaticText")
            {
                EnsureChildNames(element, location, "matrix", "textRuns", "persistentData");
                var runs = Children(Child(element, "textRuns"), "DOMTextRun").ToArray();
                if (runs.Length != 1) throw Failure(location, "Multi-style static text is not mapped.");
                var attrs = Child(Child(runs[0], "textAttrs"), "DOMTextAttrs") ?? throw Failure(location, "Missing static text style.");
                var allowed = new[] { "aliasText", "lineHeight", "size", "bitmapSize", "face", "fillColor", "alpha", "bold", "italic", "alignment" };
                if (attrs.HasElements || attrs.Attributes().Any(a => !a.IsNamespaceDeclaration && !allowed.Contains(a.Name.LocalName, StringComparer.Ordinal)))
                    throw Failure(location, "Static text contains unsupported layout attributes.");
                var alignment = Text(attrs, "alignment", "left") switch
                { "left" => TextHorizontalAlignment.Left, "center" => TextHorizontalAlignment.Center, "right" => TextHorizontalAlignment.Right,
                  _ => throw Failure(location, "Static text alignment is not mapped.") };
                var family = Required(attrs, "face");
                var width = Number(element, "width", 1, 1, 100000); var height = Number(element, "height", 1, 1, 100000);
                var data = new TextObjectData(Child(runs[0], "characters")?.Value ?? "", family,
                    Number(attrs, "size", 12, TextGeometry.MinimumFontSizePoints / 0.75f, TextGeometry.MaximumFontSizePoints / 0.75f) * 0.75f,
                    Boolean(attrs, "bold", false) ? Boolean(attrs, "italic", false) ? TextFontStyle.BoldItalic : TextFontStyle.Bold
                        : Boolean(attrs, "italic", false) ? TextFontStyle.Italic : TextFontStyle.Regular, alignment,
                    new SizeF(VectorUnits.FromPixels(width), VectorUnits.FromPixels(height)));
                var paint = Text(attrs, "fillColor", "#000000");
                if (paint.Length != 7 || paint[0] != '#' || !uint.TryParse(paint[1..], NumberStyles.HexNumber, Invariant, out var rgb))
                    throw Failure(location, "Invalid static text fill color.");
                childSymbol = project.AddDrawingObject("XFL Text");
                childSymbol.Scene.CreateEmpty(frameCount: 1);
                childSymbol.Scene.AppendTextObject(0, new PointF(VectorUnits.FromPixels(width / 2), VectorUnits.FromPixels(height / 2)),
                    data.LayoutSize, 0, TextGeometry.NormalizeStoredData(data),
                    Color.FromArgb((int)MathF.Round(Number(attrs, "alpha", 1, 0, 1) * 255), (int)(rgb >> 16), (int)((rgb >> 8) & 255), (int)(rgb & 255)).ToArgb());
                using var fonts = new System.Drawing.Text.InstalledFontCollection();
                if (!fonts.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase)))
                    diagnostics.Add(new("text-font-missing", location, $"Font '{family}' is unavailable. Its name and text remain editable; display uses the V2D fallback font until a matching installed family is available."));
                diagnostics.Add(new("text-layout-mapped", location, "Static text mapped to editable V2D text. Flash glyph metrics, line height and wrapping have not been visually matched."));
            }
            else if (element.Name.LocalName == "DOMShape")
            {
                EnsureChildNames(element, location, "matrix", "transformationPoint", "fills", "strokes", "edges", "persistentData");
                var shape = ConvertShape(element, location);
                childSymbol = project.AddDrawingObject("XFL Shape");
                childSymbol.Scene.CreateEmpty(frameCount: 1);
                foreach (var source in shape.Sources.Reverse())
                {
                    var part = shape.Sources.Count == 1 ? childSymbol : project.AddDrawingObject("XFL Shape Part");
                    if (part != childSymbol) part.Scene.CreateEmpty(frameCount: 1);
                    var svgObject = part.Scene.AddImportedSvgObject(0,
                        new PointF(VectorUnits.FromPixels(shape.Bounds.Left + shape.Bounds.Width / 2), VectorUnits.FromPixels(shape.Bounds.Top + shape.Bounds.Height / 2)),
                        new SizeF(VectorUnits.FromPixels(shape.Bounds.Width), VectorUnits.FromPixels(shape.Bounds.Height)), source, "XFL Shape");
                    // Gradient paints retain their original vector source until native
                    // gradient extraction can preserve every stop and affine mapping.
                    bool vectorPaint = element.Descendants().Any(node => node.Name.LocalName is "LinearGradient" or "RadialGradient");
                    if (editableGeometry && vectorPaint)
                        diagnostics.Add(new("gradient-vector-retained", location,
                            "Gradient shape retained as a whole SVG vector object; its original paths, paint stops and transforms remain in the project. Native node editing requires Break Apart, which may change the gradient representation."));
                    if (editableGeometry && !vectorPaint)
                    {
                        try
                        {
                            part.Scene.BreakApartImportedSvgObjects([svgObject], requireExactGeometry: true);
                        }
                        catch (InvalidDataException exception) when (exception.Message == "The SVG requires flattened, omitted, or approximated geometry; exact editable conversion stopped."
                            || exception.Message == "Non-uniform SVG placement requires a flattened stroke outline; exact editable conversion stopped."
                            || exception.Message == "An SVG fill contour cannot be preserved as an editable native curve."
                            || exception.Message == "An SVG part cannot be preserved as editable native geometry."
                            || exception.Message == "SVG break-apart output must not exceed 500000 points.")
                        {
                            // Break Apart restores its snapshot on failure. Keep the complete
                            // generated vector object rather than flattening or omitting paths.
                            diagnostics.Add(new("shape-vector-retained", location,
                                "Shape retained as a whole SVG vector object because exact native node conversion is unavailable or exceeds the Break Apart point budget. Original vector paths remain in the project."));
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            throw Failure(location, "Could not preserve this shape as editable native geometry: " + exception.Message);
                        }
                    }
                    if (part != childSymbol && !project.TryAddDrawingObjectInstance(childSymbol.Id, part.Id, PointF.Empty,
                            childSymbol.Scene.LayerIds[0], out _))
                        throw Failure(location, "Could not attach the bounded shape part.");
                }
            }
            else throw Failure(location, $"Visual element '{element.Name.LocalName}' was not mapped.");
            // Validate appearance and playback before attaching an instance, so an omitted
            // unsupported element never leaves a partially initialized visible placement.
            var basis = StateFromMatrix(new DrawingObjectInstanceDefinition().EvaluateState(0), matrix, location);
            basis = ApplyAppearance(basis, element, location) with { PlaybackFps = fps, PlaybackMode = DrawingObjectPlaybackMode.HoldFrame, Visible = false };
            var firstFrame = Integer(element, "firstFrame", 0, 0, MaximumFrameCount - 1);
            var loop = Text(element, "loop", "loop");
            var symbolType = Text(element, "symbolType", "movie clip");
            if (symbolType == "button") throw Failure(location, "Button instances are not converted.");
            if (symbolType is not ("graphic" or "movie clip")) throw Failure(location, "Unknown symbol instance type: " + symbolType);
            if (loop is not ("loop" or "play once" or "single frame")) throw Failure(location, $"Unknown graphic playback mode: {loop}.");
            if (firstFrame >= childSymbol.FrameCount) throw Failure(location, "Symbol firstFrame exceeds its duration.");
            if (symbolType == "movie clip" && childSymbol.FrameCount > 1)
                diagnostics.Add(new("movie-clip-sampled", location, "Movie clip playback is sampled from placement; independent scripted playback is not converted."));
            var endpointState = endpoint is null ? basis : ApplyAppearance(StateFromMatrix(basis, ReadMatrix(endpoint) * inherited, location), endpoint, location);
            if (endpoint is not null && basis.Filters != endpointState.Filters)
            {
                InterpolateImportedFilters(basis.Filters, endpointState.Filters, 0.5f, location);
                diagnostics.Add(new("filter-tween-baked", location,
                    "Blur widths and RGB color transforms are baked per frame. Discrete blur quality remains at the start value until the endpoint; Adobe pixel parity has not been verified."));
            }
            if (!project.TryAddDrawingObjectInstance(target.Id, childSymbol.Id, PointF.Empty, target.Scene.LayerIds[layer], out var instance)
                || instance is null) throw Failure(location, "Could not add symbol instance.");
            instance.Name = Text(element, "name", childSymbol.Name);
            instance.SetStateAtFrame(0, basis);
            var constant = endpoint is null && (loop == "single frame" || childSymbol.FrameCount == 1);
            var sampleEnd = constant ? start + 1 : end;
            for (var frame = start; frame < sampleEnd; frame++)
            {
                if (++_stateKeys > MaximumStateKeys) throw Failure(location, "Too many baked instance frame states.");
                var progress = (float)(frame - start) / (end - start);
                progress = acceleration < 0
                    ? progress * (1 + acceleration / 100) + progress * progress * (-acceleration / 100)
                    : progress * (1 - acceleration / 100) + (1 - (1 - progress) * (1 - progress)) * acceleration / 100;
                if (customEase is not null) progress = EvaluateCustomEase(customEase, (float)(frame - start) / (end - start), location);
                else if (easeMethod != "none") progress = EvaluateEaseMethod(easeMethod, (float)(frame - start) / (end - start));
                var state = endpoint is null ? basis : DrawingObjectInstanceDefinition.InterpolateState(basis, endpointState, progress);
                if (endpoint is not null) state = state with { Filters = InterpolateImportedFilters(basis.Filters, endpointState.Filters, progress, location) };
                if (guideMotion is not null) state = guideMotion.Apply(state, progress, basis.RotationZ);
                var local = loop == "single frame" ? firstFrame : firstFrame + frame - start;
                local = loop == "loop" ? local % childSymbol.FrameCount : Math.Min(local, childSymbol.FrameCount - 1);
                instance.SetStateAtFrame(frame, state with { Visible = Boolean(element, "visible", true), HoldFrame = local });
            }
            if (end < target.FrameCount) instance.SetStateAtFrame(end, basis with { Visible = false });
        }
    }

    private static InstanceFrameState StateFromMatrix(InstanceFrameState state, Matrix3x2 matrix, string location)
    {
        // QR decomposition represents the exact affine matrix as Scale * SkewX * Rotation.
        var sx = MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        if (sx < 0.000001f) throw Failure(location, "Singular affine transforms are not supported.");
        var rotation = MathF.Atan2(matrix.M12, matrix.M11);
        var sy = matrix.GetDeterminant() / sx;
        if (MathF.Abs(sy) < 0.000001f) throw Failure(location, "Singular affine transforms are not supported.");
        var shear = (matrix.M21 * matrix.M11 + matrix.M22 * matrix.M12) / sx / sy;
        if (sx > 1000 || MathF.Abs(sy) > 1000 || MathF.Abs(shear) > 10000)
            throw Failure(location, "Affine transform exceeds supported scale/skew limits.");
        return state with { X = VectorUnits.FromPixels(matrix.M31), Y = VectorUnits.FromPixels(matrix.M32),
            ScaleX = sx, ScaleY = sy, RotationZ = rotation * 180 / MathF.PI,
            SkewX = MathF.Atan(shear) * 180 / MathF.PI, SkewY = 0 };
    }

    private static string ReadEaseMethod(XElement frame, string location)
    {
        var curves = Children(Child(frame, "tweens"), "Ease").ToArray();
        if (curves.Length == 0) return "none";
        if (curves.Length != 1 || Children(Child(frame, "tweens"), "CustomEase").Any()
            || Text(curves[0], "target", "all") != "all" || curves[0].HasElements)
            throw Failure(location, "Only one Ease curve targeting all properties is supported.");
        var method = Text(curves[0], "method", "none");
        if (method is not ("none" or "cubicIn" or "cubicOut" or "cubicInOut"))
            throw Failure(location, $"Ease method '{method}' is not mapped.");
        return method;
    }

    private static float EvaluateEaseMethod(string method, float t) => method switch
    {
        "cubicIn" => t * t * t,
        "cubicOut" => 1 - (1 - t) * (1 - t) * (1 - t),
        "cubicInOut" => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2,
        _ => t
    };

    private static Vector2[]? ReadCustomEase(XElement frame, string location)
    {
        var curves = Children(Child(frame, "tweens"), "CustomEase").ToArray();
        if (curves.Length == 0) return null;
        if (curves.Length != 1 || Text(curves[0], "target", "all") != "all")
            throw Failure(location, "Only one CustomEase curve targeting all properties is supported.");
        EnsureChildNames(curves[0], location, "Point");
        var points = Children(curves[0], "Point").Select(p => new Vector2(Number(p, "x", 0, 0, 1), Number(p, "y", 0, -10, 10))).ToArray();
        if (points.Length < 4 || points.Length > 3073 || (points.Length - 1) % 3 != 0
            || points[0] != Vector2.Zero || points[^1] != Vector2.One)
            throw Failure(location, "Invalid CustomEase cubic curve endpoints or control point count.");
        return points;
    }

    private static float EvaluateCustomEase(Vector2[] points, float x, string location)
    {
        if (x <= 0 || x >= 1) return x;
        var roots = new List<double>();
        for (int i = 0; i + 3 < points.Length; i += 3)
        {
            var p0 = points[i]; var p1 = points[i + 1]; var p2 = points[i + 2]; var p3 = points[i + 3];
            double a = -p0.X + 3d * p1.X - 3d * p2.X + p3.X;
            double b = 3d * p0.X - 6d * p1.X + 3d * p2.X;
            double c = -3d * p0.X + 3d * p1.X;
            double At(double t) => ((a * t + b) * t + c) * t + p0.X;
            var cuts = new List<double> { 0, 1 };
            void Cut(double t) { if (t > 0 && t < 1) cuts.Add(t); }
            if (Math.Abs(a) < 1e-12) { if (Math.Abs(b) > 1e-12) Cut(-c / (2 * b)); }
            else
            {
                var discriminant = 4 * b * b - 12 * a * c;
                if (discriminant >= 0) { Cut((-2 * b - Math.Sqrt(discriminant)) / (6 * a)); Cut((-2 * b + Math.Sqrt(discriminant)) / (6 * a)); }
            }
            cuts.Sort();
            for (int k = 1; k < cuts.Count; k++)
            {
                double lo = cuts[k - 1], hi = cuts[k], left = At(lo), right = At(hi);
                if (x < Math.Min(left, right) - 1e-9 || x > Math.Max(left, right) + 1e-9) continue;
                if (Math.Abs(right - left) < 1e-12) throw Failure(location, "CustomEase has an ambiguous vertical interval.");
                bool increasing = right > left;
                for (int step = 0; step < 48; step++) { double mid = (lo + hi) / 2; if ((At(mid) < x) == increasing) lo = mid; else hi = mid; }
                double t = (lo + hi) / 2, u = 1 - t;
                double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
                if (!roots.Any(existing => Math.Abs(existing - y) < 1e-7)) roots.Add(y);
            }
        }
        if (roots.Count != 1) throw Failure(location, "CustomEase does not have a unique progress value at this sampled frame.");
        if (roots[0] < 0 || roots[0] > 1) throw Failure(location, "CustomEase overshoot at this sampled frame requires extrapolation that is not mapped.");
        return (float)roots[0];
    }

    private static SymbolFilters InterpolateImportedFilters(SymbolFilters source, SymbolFilters target, float progress, string location)
    {
        if (source == target || progress <= 0) return source;
        if (progress >= 1) return target;
        if (source with { Blur = default, AdditionalBlurs = null, ColorTransform = default }
            != target with { Blur = default, AdditionalBlurs = null, ColorTransform = default }
            || (source.AdditionalBlurs?.Count ?? 0) != (target.AdditionalBlurs?.Count ?? 0))
            throw Failure(location, "Tween changes unsupported filter types, order or parameters.");
        float Mix(float a, float b) => a + (b - a) * progress;
        SymbolBlurFilter MixBlur(SymbolBlurFilter a, SymbolBlurFilter b)
        {
            if (a == default && b.Enabled && b.FlashCompatible) a = b with { BlurX = 0, BlurY = 0 };
            if (b == default && a.Enabled && a.FlashCompatible) b = a with { BlurX = 0, BlurY = 0 };
            if (a.Enabled != b.Enabled || a.FlashCompatible != b.FlashCompatible)
                throw Failure(location, "Tween changes blur enable state or algorithm.");
            return a with { BlurX = Mix(a.BlurX, b.BlurX), BlurY = Mix(a.BlurY, b.BlurY) };
        }
        var identity = new SymbolFlashColorTransform { Enabled = true, RedMultiplier = 1, GreenMultiplier = 1, BlueMultiplier = 1 };
        var a = source.ColorTransform.Enabled ? source.ColorTransform : identity;
        var b = target.ColorTransform.Enabled ? target.ColorTransform : identity;
        SymbolBlurStack? stack = null;
        if (source.AdditionalBlurs is { } items)
        {
            var mixed = new SymbolBlurFilter[items.Count];
            for (int i = 0; i < mixed.Length; i++) mixed[i] = MixBlur(items[i], target.AdditionalBlurs![i]);
            stack = new SymbolBlurStack(mixed);
        }
        return source with { Blur = MixBlur(source.Blur, target.Blur), AdditionalBlurs = stack,
            ColorTransform = source.ColorTransform.Enabled || target.ColorTransform.Enabled ? new SymbolFlashColorTransform
            { Enabled = true, AlphaMultiplier = Mix(a.AlphaMultiplier ?? 1, b.AlphaMultiplier ?? 1), RedMultiplier = Mix(a.RedMultiplier, b.RedMultiplier), GreenMultiplier = Mix(a.GreenMultiplier, b.GreenMultiplier),
              BlueMultiplier = Mix(a.BlueMultiplier, b.BlueMultiplier), RedOffset = Mix(a.RedOffset, b.RedOffset),
              GreenOffset = Mix(a.GreenOffset, b.GreenOffset), BlueOffset = Mix(a.BlueOffset, b.BlueOffset) } : default };
    }

    private static InstanceFrameState ApplyAppearance(InstanceFrameState state, XElement element, string location)
    {
        state = state with { BlendMode = ReadBlendMode(element, location), Filters = ReadFilters(element, location),
            Alpha = 1, TintArgb = unchecked((int)0xffffffff) };
        var color = Child(Child(element, "color"), "Color");
        if (color is null) return state;
        var allowed = new[] { "brightness", "tintMultiplier", "tintColor", "redMultiplier", "greenMultiplier",
            "blueMultiplier", "alphaMultiplier", "redOffset", "greenOffset", "blueOffset", "alphaOffset" };
        if (color.HasElements || color.Attributes().Any(a => !a.IsNamespaceDeclaration && !allowed.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            throw Failure(location, "Color transform contains unsupported data.");
        if (Number(color, "alphaOffset", 0, -255, 255) != 0)
            throw Failure(location, "A nonzero alphaOffset cannot be represented without changing transparent instance bounds.");
        var alpha = Number(color, "alphaMultiplier", 1, 0, 1);
        var transform = new SymbolFlashColorTransform
        {
            Enabled = true, AlphaMultiplier = alpha, RedMultiplier = Number(color, "redMultiplier", 1, -1, 1),
            GreenMultiplier = Number(color, "greenMultiplier", 1, -1, 1), BlueMultiplier = Number(color, "blueMultiplier", 1, -1, 1),
            RedOffset = Number(color, "redOffset", 0, -255, 255) / 255f,
            GreenOffset = Number(color, "greenOffset", 0, -255, 255) / 255f, BlueOffset = Number(color, "blueOffset", 0, -255, 255) / 255f
        };
        bool brightness = color.Attribute("brightness") is not null;
        bool tint = color.Attribute("tintMultiplier") is not null || color.Attribute("tintColor") is not null;
        if ((brightness && tint) || ((brightness || tint) && color.Attributes().Any(a => a.Name.LocalName is
            "redMultiplier" or "greenMultiplier" or "blueMultiplier" or "redOffset" or "greenOffset" or "blueOffset")))
            throw Failure(location, "Conflicting brightness, tint or advanced RGB color transform modes.");
        if (brightness)
        {
            var value = Number(color, "brightness", 0, -1, 1);
            var multiplier = 1 - MathF.Abs(value);
            var offset = Math.Max(0, value);
            transform = transform with { RedMultiplier = multiplier, GreenMultiplier = multiplier, BlueMultiplier = multiplier,
                RedOffset = offset, GreenOffset = offset, BlueOffset = offset };
        }
        if (tint)
        {
            var amount = Number(color, "tintMultiplier", 0, 0, 1);
            var text = Text(color, "tintColor", "#000000");
            if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text[1..], NumberStyles.HexNumber, Invariant, out var rgb))
                throw Failure(location, $"Invalid tintColor: {text}.");
            transform = transform with { RedMultiplier = 1 - amount, GreenMultiplier = 1 - amount, BlueMultiplier = 1 - amount,
                RedOffset = ((rgb >> 16) & 255) / 255f * amount, GreenOffset = ((rgb >> 8) & 255) / 255f * amount,
                BlueOffset = (rgb & 255) / 255f * amount };
        }
        return state with { Alpha = 1, TintArgb = unchecked((int)0xffffffff), Filters = state.Filters with { ColorTransform = transform } };
    }

    private static void RejectVisualEffects(XElement xml, string location)
    {
        var blend = ReadBlendMode(xml, location);
        if (blend != LayerBlendMode.Normal && xml.Name.LocalName is not ("DOMSymbolInstance" or "DOMBitmapInstance"))
            throw Failure(location, $"Blend mode '{Text(xml, "blendMode", "normal")}' on {xml.Name.LocalName} is not mapped.");
        ReadFilters(xml, location);
        if (Child(xml, "matrix3D") is not null || Child(xml, "Matrix3D") is not null
            || Child(xml, "transformationPoint")?.Descendants().Any(node => node.Name.LocalName == "Matrix3D") == true)
            throw Failure(location, "3D instance transform is not mapped.");
        foreach (var attribute in new[] { "isCamera", "useAdvancedLayers", "cacheAsBitmap" })
            if (Boolean(xml, attribute, false)) throw Failure(location, $"Unsupported visual attribute: {attribute}=true.");
    }

    private static SymbolFilters ReadFilters(XElement xml, string location)
    {
        var filters = Children(Child(xml, "filters")).ToArray();
        if (filters.Length == 0) return default;
        if (filters.Length > 32)
            throw Failure(location, "A Flash filter stack exceeds the supported limit of 32 filters.");
        if (filters.Any(item => item.Name.LocalName != "BlurFilter"))
        {
            if (xml.Name.LocalName is not ("DOMSymbolInstance" or "DOMBitmapInstance"))
                throw Failure(location, "Filters on this element cannot be represented as an isolated instance.");
            return new SymbolFilters { FlashFilters = new SymbolFlashFilterStack(filters.Select(item => ReadFlashFilter(item, location)).ToArray()) };
        }
        var filter = filters[0];
        if (xml.Name.LocalName is not ("DOMSymbolInstance" or "DOMBitmapInstance")
            || filters.Any(item => item.Name.LocalName != "BlurFilter"))
            throw Failure(location, $"Filter '{filter.Name.LocalName}' on {xml.Name.LocalName} is not mapped exactly (blurX={Text(filter, "blurX", "default")}, blurY={Text(filter, "blurY", "default")}, quality={Text(filter, "quality", "default")}, filters={filters.Length}, attrs={string.Join(",", filter.Attributes().Select(attribute => attribute.Name.LocalName))}, children={string.Join(",", filter.Elements().Select(child => child.Name.LocalName))}). Conversion stopped without omitting the effect.");
        if (filters.Any(item => item.HasElements || item.Attributes().Any(attribute =>
            !attribute.IsNamespaceDeclaration
            && attribute.Name.LocalName is not ("blurX" or "blurY" or "quality" or "isEnabled"))))
            throw Failure(location, "BlurFilter has unsupported attributes or child data; conversion stopped.");
        try
        {
            var mapped = filters.Select(item => new SymbolBlurFilter
            {
                Enabled = Boolean(item, "isEnabled", true), FlashCompatible = true,
                BlurX = Number(item, "blurX", 4, 0, 255), BlurY = Number(item, "blurY", 4, 0, 255),
                Quality = Integer(item, "quality", 1, 1, 15)
            }).ToArray();
            return new SymbolFilters { Blur = mapped[0],
                AdditionalBlurs = mapped.Length > 1 ? new SymbolBlurStack(mapped[1..]) : null };
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith("XFL [" + location + "]: ", StringComparison.Ordinal))
        { throw Failure(location, exception.Message); }
    }

    private static SymbolFlashFilter ReadFlashFilter(XElement item, string location)
    {
        var name = item.Name.LocalName;
        var allowed = name switch
        {
            "BlurFilter" => new[] { "blurX", "blurY", "quality", "isEnabled" },
            "GlowFilter" => new[] { "blurX", "blurY", "quality", "isEnabled", "color", "alpha", "strength", "inner", "knockout" },
            "DropShadowFilter" => new[] { "blurX", "blurY", "quality", "isEnabled", "color", "alpha", "strength", "inner", "knockout", "hideObject", "angle", "distance" },
            _ => throw Failure(location, $"Filter '{name}' is not mapped (quality={Text(item, "quality", "default")}). Conversion stopped without omitting the effect.")
        };
        if (item.HasElements || item.Attributes().Any(a => !a.IsNamespaceDeclaration && !allowed.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            throw Failure(location, $"{name} has unsupported attributes or child data.");
        try
        {
            var colorText = Text(item, "color", name == "GlowFilter" ? "#FF0000" : "#000000");
            if (colorText.Length != 7 || colorText[0] != '#' || !uint.TryParse(colorText[1..], NumberStyles.HexNumber, Invariant, out var rgb))
                throw Failure(location, $"Invalid {name} color: {colorText}.");
            return new SymbolFlashFilter
            {
                Kind = name == "BlurFilter" ? SymbolFlashFilterKind.Blur : name == "GlowFilter" ? SymbolFlashFilterKind.Glow : SymbolFlashFilterKind.Shadow,
                Enabled = Boolean(item, "isEnabled", true),
                BlurX = Number(item, "blurX", 4, 0, 255), BlurY = Number(item, "blurY", 4, 0, 255),
                Quality = Integer(item, "quality", 1, 1, 15), ColorArgb = unchecked((int)(0xff000000u | rgb)),
                Opacity = Number(item, "alpha", 1, 0, 1), Strength = Number(item, "strength", 1, 0, 255),
                Angle = Number(item, "angle", 45, -360, 360), Distance = Number(item, "distance", 4, -256, 256),
                Inner = Boolean(item, "inner", false), Knockout = Boolean(item, "knockout", false), HideObject = Boolean(item, "hideObject", false)
            };
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith("XFL [" + location + "]: ", StringComparison.Ordinal))
        { throw Failure(location, exception.Message); }
    }

    private static LayerBlendMode ReadBlendMode(XElement xml, string location) => Text(xml, "blendMode", "normal") switch
    {
        "normal" or "0" => LayerBlendMode.Normal,
        "layer" => LayerBlendMode.Isolate,
        "multiply" => LayerBlendMode.Multiply,
        "screen" => LayerBlendMode.Screen,
        "lighten" => LayerBlendMode.Lighten,
        "darken" => LayerBlendMode.Darken,
        "difference" => LayerBlendMode.Difference,
        "add" => LayerBlendMode.LinearDodge,
        "subtract" => LayerBlendMode.Subtract,
        "invert" => LayerBlendMode.Invert,
        "overlay" => LayerBlendMode.Overlay,
        "hardlight" => LayerBlendMode.HardLight,
        var mode => throw Failure(location, $"Blend mode '{mode}' is not mapped exactly; conversion stopped.")
    };

    private static void PreflightVisualEffects(TimelineSpec spec)
    {
        RejectVisualEffects(spec.Xml, spec.Name);
        foreach (var layer in Children(Child(spec.Xml, "layers"), "DOMLayer"))
        {
            if (Text(layer, "layerType", "normal") == "guide") continue;
            var location = spec.Name + "/" + Text(layer, "name", "Layer");
            RejectVisualEffects(layer, location);
            foreach (var frame in Children(Child(layer, "frames"), "DOMFrame"))
            {
                var frameLocation = location + $"/frame {Integer(frame, "index", 0, 0, MaximumFrameCount - 1) + 1}";
                RejectVisualEffects(frame, frameLocation);
                foreach (var element in Children(Child(frame, "elements"))) CheckElement(element);
                void CheckElement(XElement element)
                {
                    RejectVisualEffects(element, frameLocation);
                    if (element.Name.LocalName == "DOMGroup")
                        foreach (var member in Children(Child(element, "members"))) CheckElement(member);
                }
            }
        }
    }

    private static void EnsureChildNames(XElement xml, string location, params string[] names)
    {
        var unknown = xml.Elements().FirstOrDefault(child => !names.Contains(child.Name.LocalName, StringComparer.Ordinal));
        if (unknown is not null) throw Failure(location, "Unsupported visual data: " + unknown.Name.LocalName);
    }

    private static void DiagnoseNonFrameScripts(XElement xml, string location, List<FlashImportDiagnostic> diagnostics)
    {
        if (xml.Descendants().Any(node => (node.Name.LocalName is "Actionscript" or "script")
            && !node.Ancestors().Any(parent => parent.Name.LocalName == "DOMFrame")))
            diagnostics.Add(new("script-not-converted", location, "Document/symbol scripts are not executed or converted; interactive behavior may differ."));
        if (xml.Attributes().Any(attribute => (attribute.Name.LocalName is "linkageClassName" or "linkageExportForAS") && attribute.Value is not ("" or "false")))
            diagnostics.Add(new("linkage-not-converted", location, "Script linkage/export metadata is not converted."));
    }

    private static Matrix3x2 ReadMatrix(XElement element)
    {
        var xml = Child(Child(element, "matrix"), "Matrix");
        if (xml is null) return Matrix3x2.Identity;
        return new(Number(xml, "a", 1), Number(xml, "b", 0), Number(xml, "c", 0), Number(xml, "d", 1),
            Number(xml, "tx", 0), Number(xml, "ty", 0));
    }
    private static XElement? Child(XElement? element, string name)
    {
        var matches = element?.Elements().Where(child => child.Name.LocalName == name).Take(2).ToArray();
        if (matches is { Length: > 1 }) throw Failure(element!.Name.LocalName, "Duplicate XML child: " + name);
        return matches?.FirstOrDefault();
    }
    private static IEnumerable<XElement> Children(XElement? element, string? name = null) => element?.Elements().Where(child => name is null || child.Name.LocalName == name) ?? [];
    private static string Text(XElement element, string attribute, string fallback) => element.Attribute(attribute)?.Value ?? fallback;
    private static string Required(XElement element, string attribute)
    {
        var value = Text(element, attribute, "");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) throw Failure(element.Name.LocalName, $"Missing/invalid {attribute}.");
        return value;
    }
    private static float Number(XElement element, string attribute, float fallback, float minimum = -100_000, float maximum = 100_000)
    {
        var value = element.Attribute(attribute)?.Value;
        if (value is null) return fallback;
        if (!float.TryParse(value, NumberStyles.Float, Invariant, out var number) || !float.IsFinite(number) || number < minimum || number > maximum)
            throw Failure(element.Name.LocalName, $"Invalid {attribute}: {value}.");
        return number;
    }
    private static int Integer(XElement element, string attribute, int fallback, int minimum, int maximum)
    {
        var value = element.Attribute(attribute)?.Value;
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, Invariant, out var number) || number < minimum || number > maximum)
            throw Failure(element.Name.LocalName, $"Invalid {attribute}: {value}.");
        return number;
    }
    private static bool Boolean(XElement element, string attribute, bool fallback)
    {
        var value = element.Attribute(attribute)?.Value;
        return value switch { null => fallback, "true" or "1" => true, "false" or "0" => false,
            _ => throw Failure(element.Name.LocalName, $"Invalid {attribute}: {value}.") };
    }
    private static InvalidDataException Failure(string location, string message) => new($"XFL [{location}]: {message}");
    private static string Format(float value) => value.ToString("R", Invariant);
}
