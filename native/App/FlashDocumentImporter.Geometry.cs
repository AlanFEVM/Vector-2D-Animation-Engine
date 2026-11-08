using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private sealed record ConvertedShape(IReadOnlyList<string> Sources, RectangleF Bounds, int RepairedGaps = 0, float MaximumGap = 0, bool ApproximateHairline = false, bool ApproximatePixelHinting = false);
    // Even the shortest SVG line command occupies six bytes ("L 0 0 ").
    // Use the existing SVG payload budget as a bounded parsing allowance;
    // generated SVG is separately limited per path, per chunk and in total.
    private const int MaximumShapeSegments = (int)(ImportedSvgRasterizer.MaxImportSourceBytes / 6);
    private sealed record EdgeSegment(PointF Start, PointF End, PointF? Control)
    {
        internal EdgeSegment Reverse() => new(End, Start, Control);
        internal string Command => Control is { } control
            ? $"Q {Format(control.X)} {Format(control.Y)} {Format(End.X)} {Format(End.Y)} "
            : $"L {Format(End.X)} {Format(End.Y)} ";
    }
    private sealed record EdgeRun(List<EdgeSegment> Segments)
    {
        internal PointF Start => Segments[0].Start;
        internal PointF End => Segments[^1].End;
        internal EdgeRun Reverse() => new(Segments.AsEnumerable().Reverse().Select(segment => segment.Reverse()).ToList());
    }
    // XFL edge syntax follows Adobe twips and signed 24.8 hexadecimal fixed point.
    // Reference investigated: xfl2svg/shape/edge.py (PluieElectrique); this is an independent implementation.
    private static readonly Regex EdgeTokens = new(@"[!|/\[\]]|S[1-7]|\#[a-fA-F0-9]{1,6}\.[a-fA-F0-9]{1,2}|[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private sealed record BitmapFillSource(string DataUri, int Width, int Height, bool Smoothing);

    private static ConvertedShape ConvertShape(XElement shape, string location, Func<string, BitmapFillSource>? bitmapResolver = null)
    {
        if (Child(shape, "morphShape") is not null)
            throw Failure(location, "Morph/advanced shape geometry requires Adobe visual conversion.");
        if (shape.Attributes().Any(attribute => attribute.Name.LocalName is "winding" or "fillRule" or "windingRule"))
            throw Failure(location, "Explicit authoring winding rules require Adobe visual conversion; their interpretation is not approximated.");
        var svgNs = (XNamespace)"http://www.w3.org/2000/svg";
        var defs = new XElement(svgNs + "defs");
        var fills = new Dictionary<int, ConvertedPaint>();
        var strokes = new Dictionary<int, XElement>();
        var filled = new Dictionary<int, List<EdgeRun>>();
        var stroked = new Dictionary<int, List<EdgeRun>>();
        foreach (var style in Children(Child(shape, "fills"), "FillStyle"))
        {
            var id = Integer(style, "index", 0, 1, 65535);
            var value = style.Elements().SingleOrDefault() ?? throw Failure(location, "Empty fill style.");
            if (!fills.TryAdd(id, Paint(value, defs, "fill" + id, location, bitmapResolver))) throw Failure(location, "Duplicate fill style index.");
        }
        var maximumStroke = 0f;
        var approximatePixelHinting = false;
        foreach (var style in Children(Child(shape, "strokes"), "StrokeStyle"))
        {
            var id = Integer(style, "index", 0, 1, 65535);
            var stroke = Child(style, "SolidStroke") ?? throw Failure(location, "Only solid vector stroke styles are supported.");
            if (Text(stroke, "scaleMode", "normal") != "normal" || Child(stroke, "widths") is not null)
                throw Failure(location, "Non-scaling or variable-width strokes require Adobe visual conversion.");
            // Preserve authored geometry; Flash screen alignment has no native equivalent.
            approximatePixelHinting |= Boolean(stroke, "pixelHinting", false);
            var solidStyle = Text(stroke, "solidStyle", "solid");
            if (solidStyle is not ("solid" or "hairline")) throw Failure(location, "Unsupported stroke pattern.");
            var weight = solidStyle == "hairline" ? 1f : Number(stroke, "weight", 1, 0.001f, 10000);
            var cap = Text(stroke, "caps", "round") switch { "round" => "round", "square" => "square", "none" => "butt", _ => throw Failure(location, "Unsupported stroke cap.") };
            var join = Text(stroke, "joints", "round");
            if (join is not ("round" or "bevel" or "miter")) throw Failure(location, "Unsupported stroke join.");
            var paint = Children(Child(stroke, "fill")).SingleOrDefault() ?? throw Failure(location, "Missing stroke paint.");
            var strokePaint = Paint(paint, defs, "stroke" + id, location, bitmapResolver);
            var attributes = new XElement(svgNs + "path", new XAttribute("fill", "none"),
                new XAttribute("stroke", strokePaint.Value), new XAttribute("stroke-opacity", Format(strokePaint.Opacity)),
                new XAttribute("stroke-width", Format(weight)), new XAttribute("stroke-linecap", cap),
                new XAttribute("stroke-linejoin", join), new XAttribute("stroke-miterlimit", Format(Number(stroke, "miterLimit", 3, 1, 100))));
            if (!strokes.TryAdd(id, attributes)) throw Failure(location, "Duplicate stroke style index.");
            var expansion = MathF.Max(cap == "square" ? MathF.Sqrt(2) : 1,
                join == "miter" ? Number(stroke, "miterLimit", 3, 1, 100) : 1);
            maximumStroke = MathF.Max(maximumStroke, weight * expansion);
        }
        var minimumX = float.PositiveInfinity;
        var minimumY = float.PositiveInfinity;
        var maximumX = float.NegativeInfinity;
        var maximumY = float.NegativeInfinity;
        var segmentCount = 0;
        foreach (var edge in Children(Child(shape, "edges"), "Edge"))
        {
            if (edge.Attribute("edges") is null)
            {
                // Animate often stores separate cubic authoring hints alongside the actual
                // quadratic edges. These hints do not own fill/stroke styles or rendered geometry.
                if (edge.Attribute("fillStyle0") is not null || edge.Attribute("fillStyle1") is not null || edge.Attribute("strokeStyle") is not null)
                    throw Failure(location, "Styled XFL edge is missing its rendered edge stream.");
                continue;
            }
            var runs = ParseEdge(edge.Attribute("edges")!.Value, location, ref segmentCount);
            var left = Integer(edge, "fillStyle0", 0, 0, 65535);
            var right = Integer(edge, "fillStyle1", 0, 0, 65535);
            var stroke = Integer(edge, "strokeStyle", 0, 0, 65535);
            if (left != 0 && !fills.ContainsKey(left) || right != 0 && !fills.ContainsKey(right)
                || stroke != 0 && !strokes.ContainsKey(stroke)) throw Failure(location, "XFL edge references a missing style.");
            // Unpainted edges are authoring geometry, not visible artwork.
            if (left == right && stroke == 0) continue;
            foreach (var run in runs)
            {
                if (left != 0 && left != right) Add(filled, left, run);
                if (right != 0 && left != right) Add(filled, right, run.Reverse());
                if (stroke != 0) Add(stroked, stroke, run);
                foreach (var segment in run.Segments)
                {
                    Include(segment.Start); Include(segment.End);
                    if (segment.Control is { } control) Include(control);
                }
            }
        }
        if (filled.Count + stroked.Count == 0) return new(Array.Empty<string>(), RectangleF.Empty);
        var margin = maximumStroke / 2;
        var bounds = RectangleF.FromLTRB(minimumX - margin, minimumY - margin, maximumX + margin, maximumY + margin);
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 200_000 || bounds.Height > 200_000)
            throw Failure(location, "Shape bounds are empty or exceed supported limits.");
        var template = new XElement(svgNs + "svg", new XAttribute("width", Format(bounds.Width)), new XAttribute("height", Format(bounds.Height)),
            new XAttribute("viewBox", $"{Format(bounds.X)} {Format(bounds.Y)} {Format(bounds.Width)} {Format(bounds.Height)}"));
        if (defs.HasElements) template.Add(defs);
        var repairedGaps = 0;
        var maximumGap = 0f;
        var sources = new List<string>();
        var svg = new XElement(template);
        var baseBytes = Encoding.UTF8.GetByteCount(template.ToString(SaveOptions.DisableFormatting)) + 16;
        long chunkBytes = baseBytes;
        long totalBytes = 0;
        var chunkPaths = 0;
        foreach (var (id, runs) in filled.OrderBy(item => item.Key))
        {
            long pathBytes = 0;
            AppendPath(new XElement(svgNs + "path", new XAttribute("d", JoinRuns(runs, true, location, ref pathBytes, ref repairedGaps, ref maximumGap)),
                new XAttribute("fill", fills[id].Value), new XAttribute("fill-opacity", Format(fills[id].Opacity)),
                new XAttribute("fill-rule", "nonzero"), new XAttribute("stroke", "none")));
        }
        foreach (var (id, runs) in stroked.OrderBy(item => item.Key))
        {
            long pathBytes = 0;
            var node = new XElement(strokes[id]);
            node.Add(new XAttribute("d", JoinRuns(runs, false, location, ref pathBytes, ref repairedGaps, ref maximumGap)));
            AppendPath(node);
        }
        FinishChunk();
        return new(sources.AsReadOnly(), bounds, repairedGaps, maximumGap, shape.Descendants().Any(node => node.Name.LocalName == "SolidStroke" && Text(node, "solidStyle", "solid") == "hairline"), approximatePixelHinting);

        void AppendPath(XElement node)
        {
            var nodeBytes = Encoding.UTF8.GetByteCount(node.ToString(SaveOptions.DisableFormatting));
            if (baseBytes + nodeBytes > ImportedSvgRasterizer.MaxImportSourceBytes)
                throw Failure(location, "A single fill/stroke path exceeds the SVG import size limit; it cannot be split without changing contour semantics.");
            if (chunkPaths > 0 && chunkBytes + nodeBytes > ImportedSvgRasterizer.MaxImportSourceBytes) FinishChunk();
            svg.Add(node);
            chunkBytes += nodeBytes;
            chunkPaths++;
        }

        void FinishChunk()
        {
            if (chunkPaths == 0) return;
            var source = svg.ToString(SaveOptions.DisableFormatting);
            var length = Encoding.UTF8.GetByteCount(source);
            totalBytes += length;
            if (length > ImportedSvgRasterizer.MaxImportSourceBytes || totalBytes > 64L * 1024 * 1024)
                throw Failure(location, "Generated shape SVG exceeds the bounded import budget.");
            sources.Add(source);
            svg = new XElement(template);
            chunkBytes = baseBytes;
            chunkPaths = 0;
        }

        void Include(PointF point)
        {
            minimumX = MathF.Min(minimumX, point.X); minimumY = MathF.Min(minimumY, point.Y);
            maximumX = MathF.Max(maximumX, point.X); maximumY = MathF.Max(maximumY, point.Y);
        }
    }

    private static void Add(Dictionary<int, List<EdgeRun>> collection, int id, EdgeRun run)
    {
        if (!collection.TryGetValue(id, out var values)) collection[id] = values = [];
        values.Add(run);
    }

    private static List<EdgeRun> ParseEdge(string source, string location, ref int segmentCount)
    {
        if (source.Length > 4 * 1024 * 1024) throw Failure(location, "Edge stream exceeds size limit.");
        var match = EdgeTokens.Match(source);
        var previous = 0;
        string Token()
        {
            if (!match.Success) throw Failure(location, "Incomplete XFL edge coordinate.");
            if (!IsWhitespace(source.AsSpan(previous, match.Index - previous)))
                throw Failure(location, "Unknown XFL edge token.");
            var token = match.Value;
            previous = match.Index + match.Length;
            match = match.NextMatch();
            return token;
        }
        var result = new List<EdgeRun>();
        var segments = new List<EdgeSegment>();
        var position = PointF.Empty;
        var hasPosition = false;
        PointF Point()
        {
            return new(EdgeNumber(Token(), location), EdgeNumber(Token(), location));
        }
        while (match.Success)
        {
            var command = Token();
            if (command.StartsWith('S')) continue; // Authoring selection hints; never geometry/style switches.
            if (command == "!")
            {
                var next = Point();
                if (hasPosition && next == position) continue;
                if (segments.Count != 0) result.Add(new(segments));
                segments = []; position = next; hasPosition = true;
            }
            else
            {
                if (!hasPosition) throw Failure(location, "XFL edge must begin with move-to.");
                PointF? control = null;
                if (command is "[" or "]") control = Point();
                else if (command is not ("|" or "/")) throw Failure(location, "Unknown XFL edge command.");
                var next = Point();
                if (++segmentCount > MaximumShapeSegments)
                    throw Failure(location, "XFL shape geometry exceeds the bounded parsing budget.");
                segments.Add(new(position, next, control)); position = next;
            }
        }
        if (!IsWhitespace(source.AsSpan(previous))) throw Failure(location, "Invalid trailing XFL edge data.");
        if (segments.Count != 0) result.Add(new(segments));
        return result;
    }

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text) if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static float EdgeNumber(string token, string location)
    {
        float number;
        if (token.StartsWith('#'))
        {
            var parts = token[1..].Split('.');
            if (parts.Length != 2 || !uint.TryParse(parts[0].PadLeft(6, '0') + parts[1].PadRight(2, '0'), NumberStyles.HexNumber, Invariant, out var raw))
                throw Failure(location, "Invalid XFL hexadecimal coordinate.");
            number = unchecked((int)raw) / 256f / 20;
        }
        else if (!float.TryParse(token, NumberStyles.Float, Invariant, out number)) throw Failure(location, "Invalid XFL edge coordinate.");
        else number /= 20;
        if (!float.IsFinite(number) || MathF.Abs(number) > 100_000) throw Failure(location, "XFL edge coordinate exceeds limits.");
        return number;
    }

    private static string JoinRuns(List<EdgeRun> runs, bool requireClosed, string location, ref long pathBytes, ref int repairedGaps, ref float maximumGap)
    {
        // Join directed contour boundaries by exact coordinate identity. A branched Euler contour
        // is followed deterministically; every segment is consumed once, including shared vertices.
        // Fill junctions can land inside an XFL run. Index each directed segment
        // so those junctions remain available without changing curve geometry.
        if (requireClosed)
            runs = runs.SelectMany(run => run.Segments.Select(segment => new EdgeRun([segment]))).ToList();
        if (requireClosed)
        {
            // Repair bounded gaps between degree deficits without moving existing curves.
            var degree = new Dictionary<PointF, int>();
            foreach (var run in runs)
            {
                degree[run.Start] = degree.GetValueOrDefault(run.Start) + 1;
                degree[run.End] = degree.GetValueOrDefault(run.End) - 1;
            }
            var starts = degree.Where(pair => pair.Value > 0).ToArray();
            var ends = degree.Where(pair => pair.Value < 0).ToArray();
            if (starts.Length > 2048 || ends.Length > 2048)
                throw Failure(location, "Too many open fill boundary endpoints.");
            // Pair shortest gaps first, with stable coordinate tie-breaking.
            // Four source pixels is the explicit tolerance for legacy artwork.
            var candidates = (from end in ends from start in starts
                let dx = end.Key.X - start.Key.X
                let dy = end.Key.Y - start.Key.Y
                let distance = dx * dx + dy * dy
                where distance <= 16f
                orderby distance, end.Key.X, end.Key.Y, start.Key.X, start.Key.Y
                select (End: end.Key, Start: start.Key, Distance: distance)).ToArray();
            foreach (var pair in candidates)
            {
                while (degree[pair.End] < 0 && degree[pair.Start] > 0)
                {
                    runs.Add(new EdgeRun([new EdgeSegment(pair.End, pair.Start, null)]));
                    degree[pair.End]++;
                    degree[pair.Start]--;
                    repairedGaps++;
                    maximumGap = MathF.Max(maximumGap, MathF.Sqrt(pair.Distance));
                }
            }
            if (degree.Values.Any(value => value != 0))
                throw Failure(location, "Open fill boundary exceeds the four-pixel repair tolerance.");
        }
        var byStart = runs.GroupBy(run => run.Start).ToDictionary(group => group.Key, group => new Queue<EdgeRun>(group));
        var result = new StringBuilder();
        var bytes = pathBytes;
        foreach (var initial in runs)
        {
            if (!byStart.TryGetValue(initial.Start, out var queue) || queue.Count == 0) continue;
            while (queue.Count != 0)
            {
                var first = queue.Dequeue();
                Append($"M {Format(first.Start.X)} {Format(first.Start.Y)} ");
                foreach (var segment in first.Segments) Append(segment.Command);
                var end = first.End;
                var hops = 0;
                while (end != first.Start && byStart.TryGetValue(end, out var next) && next.Count != 0)
                {
                    var follow = next.Dequeue();
                    foreach (var segment in follow.Segments) Append(segment.Command);
                    end = follow.End;
                    if (++hops > runs.Count) throw Failure(location, "Invalid contour traversal.");
                }
                if (end == first.Start) Append("Z ");
                else if (requireClosed) throw Failure(location, "Open fill boundary cannot be imported safely.");
            }
        }
        pathBytes = bytes;
        return result.ToString();

        void Append(string command)
        {
            bytes += Encoding.UTF8.GetByteCount(command);
            if (bytes > ImportedSvgRasterizer.MaxImportSourceBytes)
                throw Failure(location, "Generated shape SVG exceeds the import size limit.");
            result.Append(command);
        }
    }

    private readonly record struct ConvertedPaint(string Value, float Opacity = 1);

    private static ConvertedPaint Paint(XElement paint, XElement defs, string id, string location, Func<string, BitmapFillSource>? bitmapResolver = null)
    {
        var svgNs = defs.Name.Namespace;
        if (paint.Name.LocalName == "SolidColor")
        {
            var color = ColorHex(paint, location);
            var alpha = Number(paint, "alpha", 1, 0, 1);
            // Keep solid alpha as solid paint so editable SVG extraction retains curves.
            return new ConvertedPaint(color, alpha);
        }
        if (paint.Name.LocalName == "BitmapFill")
        {
            EnsureChildNames(paint, location, "matrix");
            if (paint.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration
                && attribute.Name.LocalName is not ("bitmapPath" or "bitmapIsClipped")))
                throw Failure(location, "Unsupported bitmap fill attributes.");
            var clipped = Boolean(paint, "bitmapIsClipped", false);
            var bitmap = bitmapResolver?.Invoke(Required(paint, "bitmapPath"))
                ?? throw Failure(location, "Bitmap fill references an unavailable library image.");
            if (Child(Child(paint, "matrix"), "Matrix") is null)
                throw Failure(location, "A bitmap fill needs a verified mapping matrix.");
            var bitmapMatrix = ReadMatrix(paint);
            bitmapMatrix.M11 /= 20; bitmapMatrix.M12 /= 20; bitmapMatrix.M21 /= 20; bitmapMatrix.M22 /= 20;
            if (MathF.Abs(bitmapMatrix.GetDeterminant()) < 1e-12f) throw Failure(location, "Singular bitmap fill matrix.");
            defs.Add(new XElement(svgNs + "pattern", new XAttribute("id", id),
                new XAttribute("patternUnits", "userSpaceOnUse"), new XAttribute("patternContentUnits", "userSpaceOnUse"),
                new XAttribute("width", bitmap.Width), new XAttribute("height", bitmap.Height),
                new XAttribute(ImportedSvgRasterizer.BitmapSamplingAttribute, (bitmap.Smoothing ? "bilinear-" : "point-") + (clipped ? "clamp" : "tile")),
                new XAttribute("patternTransform", $"matrix({Format(bitmapMatrix.M11)} {Format(bitmapMatrix.M12)} {Format(bitmapMatrix.M21)} {Format(bitmapMatrix.M22)} {Format(bitmapMatrix.M31)} {Format(bitmapMatrix.M32)})"),
                new XElement(svgNs + "image", new XAttribute("width", bitmap.Width), new XAttribute("height", bitmap.Height),
                    new XAttribute("preserveAspectRatio", "none"),
                    new XAttribute(XName.Get("href", "http://www.w3.org/1999/xlink"), bitmap.DataUri))));
            return new ConvertedPaint("url(#" + id + ")");
        }
        if (paint.Name.LocalName is not ("LinearGradient" or "RadialGradient"))
            throw Failure(location, $"Paint type '{paint.Name.LocalName}' requires Adobe visual conversion.");
        var linear = paint.Name.LocalName == "LinearGradient";
        var spread = Text(paint, "spreadMethod", "extend") switch { "extend" or "pad" => "pad", "reflect" => "reflect", "repeat" => "repeat", _ => throw Failure(location, "Unsupported gradient spread.") };
        var interpolation = Text(paint, "interpolationMethod", "rgb") switch { "rgb" => "sRGB", "linearRGB" => "linearRGB", _ => throw Failure(location, "Unsupported gradient interpolation.") };
        var matrix = ReadMatrix(paint);
        var gradient = new XElement(svgNs + (linear ? "linearGradient" : "radialGradient"), new XAttribute("id", id),
            new XAttribute("gradientUnits", "userSpaceOnUse"), new XAttribute("spreadMethod", spread), new XAttribute("color-interpolation", interpolation),
            new XAttribute("gradientTransform", $"matrix({Format(matrix.M11)} {Format(matrix.M12)} {Format(matrix.M21)} {Format(matrix.M22)} {Format(matrix.M31)} {Format(matrix.M32)})"));
        // Flash gradient coordinate space is -16384..16384 twips = -819.2..819.2 px.
        if (linear) gradient.Add(new XAttribute("x1", "-819.2"), new XAttribute("x2", "819.2"), new XAttribute("y1", "0"), new XAttribute("y2", "0"));
        else gradient.Add(new XAttribute("cx", "0"), new XAttribute("cy", "0"), new XAttribute("r", "819.2"),
            new XAttribute("fx", Format(Number(paint, "focalPointRatio", 0, -0.999f, 0.999f) * 819.2f)), new XAttribute("fy", "0"));
        var previous = -1f;
        var entries = Children(paint, "GradientEntry").ToArray();
        if (entries.Length is < 2 or > 256) throw Failure(location, "Gradient requires 2 to 256 stops.");
        foreach (var entry in entries)
        {
            var ratio = Number(entry, "ratio", 0, 0, 1);
            if (ratio < previous) throw Failure(location, "Unordered gradient stops.");
            previous = ratio;
            gradient.Add(new XElement(svgNs + "stop", new XAttribute("offset", Format(ratio)), new XAttribute("stop-color", ColorHex(entry, location)),
                new XAttribute("stop-opacity", Format(Number(entry, "alpha", 1, 0, 1)))));
        }
        defs.Add(gradient);
        return new ConvertedPaint("url(#" + id + ")");
    }

    private static string ColorHex(XElement element, string location)
    {
        var value = Text(element, "color", "#000000");
        if (value.Length != 7 || value[0] != '#' || !uint.TryParse(value[1..], NumberStyles.HexNumber, Invariant, out _))
            throw Failure(location, "Invalid XFL RGB color.");
        return value;
    }
}
