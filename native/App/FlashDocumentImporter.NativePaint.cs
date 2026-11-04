using System.Drawing;
using System.Numerics;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private static void AppendNativeShapeSource(VectorScene scene, string source, RectangleF bounds,
        string location, List<FlashImportDiagnostic> diagnostics)
    {
        var xml = XElement.Parse(source);
        foreach (var path in xml.Elements().Where(node => node.Name.LocalName == "path"))
        {
            var root = new XElement(xml.Name, xml.Attributes(), xml.Elements().Where(node => node.Name.LocalName == "defs"));
            var node = new XElement(path);
            var gradients = new List<(XElement Definition, float Opacity)>();
            float solidAlpha = 1;
            foreach (var channel in new[] { "fill", "stroke" })
            {
                var value = (string?)node.Attribute(channel);
                if (value is null or "none") continue;
                var alpha = Number(node, channel + "-opacity", 1, 0, 1);
                if (value.StartsWith("url(#", StringComparison.Ordinal))
                {
                    var id = value[5..^1];
                    var definition = xml.Descendants().Single(item => (string?)item.Attribute("id") == id);
                    if (definition.Name.LocalName is not ("linearGradient" or "radialGradient"))
                        throw Failure(location, "Native bitmap pattern editing is not yet supported.");
                    gradients.Add((definition, alpha));
                    node.SetAttributeValue(channel, "#FFFFFF");
                }
                else solidAlpha = alpha;
                node.SetAttributeValue(channel + "-opacity", "1");
            }
            root.Add(node);
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting))))
            using (var bitmap = new Bitmap(1, 1))
            using (var renderer = Svg.SvgRenderer.FromImage(bitmap))
            {
                var document = Svg.SvgDocument.Open<Svg.SvgDocument>(stream, new Svg.SvgOptions());
                var geometry = document.Children.OfType<Svg.SvgVisualElement>().Single().Path(renderer);
                var local = geometry.GetBounds();
                var strokeWidth = (string?)node.Attribute("stroke") is null or "none" ? 0
                    : Number(node, "stroke-width", 1, 0, 10000);
                if (strokeWidth == 0 && (local.Width == 0 || local.Height == 0))
                {
                    diagnostics.Add(new("zero-area-fill", location, "A zero-area fill boundary paints no pixels and was omitted."));
                    continue;
                }
                local.Inflate(strokeWidth * 2, strokeWidth * 2);
                bounds = local;
                root.SetAttributeValue("width", Format(bounds.Width));
                root.SetAttributeValue("height", Format(bounds.Height));
                root.SetAttributeValue("viewBox", $"{Format(bounds.X)} {Format(bounds.Y)} {Format(bounds.Width)} {Format(bounds.Height)}");
            }
            var width = Math.Max(2, VectorUnits.FromPixels(bounds.Width));
            var height = Math.Max(2, VectorUnits.FromPixels(bounds.Height));
            var center = new PointF(VectorUnits.FromPixels(bounds.Left + bounds.Width / 2),
                VectorUnits.FromPixels(bounds.Top + bounds.Height / 2));
            var svg = scene.AddImportedSvgObject(0, center, new SizeF(width, height), root.ToString(SaveOptions.DisableFormatting));
            (int[] ProducedObjects, ImportedSvgBreakApproximation Approximations) result;
            try { result = scene.BreakApartImportedSvgObjects([svg], createDrawingLayers: false); }
            catch (InvalidDataException exception) when (width <= 25 && height <= 25
                && exception.Message.Contains("no visible geometry", StringComparison.Ordinal))
            {
                scene.Width[svg] *= 2;
                scene.Height[svg] *= 2;
                width = scene.Width[svg]; height = scene.Height[svg];
                result = scene.BreakApartImportedSvgObjects([svg], createDrawingLayers: false);
                diagnostics.Add(new("tiny-shape-native-precision", location,
                    "A sub-pixel contour was enlarged twofold to survive native coordinate quantization."));
            }
            foreach (var index in result.ProducedObjects)
            {
                if (gradients.Count == 0)
                {
                    scene.Argb[index] = WithAlpha(scene.Argb[index], solidAlpha);
                    scene.StrokeArgb[index] = WithAlpha(scene.StrokeArgb[index], solidAlpha);
                    continue;
                }
                var (gradient, opacity) = gradients.Single();
                var stops = gradient.Elements().Where(item => item.Name.LocalName == "stop").Select(stop =>
                    new GradientStop(Number(stop, "offset", 0, 0, 1), Color.FromArgb(
                        (int)MathF.Round(255 * opacity * Number(stop, "stop-opacity", 1, 0, 1)),
                        ColorTranslator.FromHtml((string)stop.Attribute("stop-color")!)))).ToArray();
                var transformText = (string)gradient.Attribute("gradientTransform")!;
                var values = transformText[7..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => float.Parse(value, Invariant)).ToArray();
                var matrix = new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5]);
                var linear = gradient.Name.LocalName == "linearGradient";
                Vector2 start;
                Vector2 end;
                if (linear)
                {
                    // Inverse-transpose keeps the gradient's isocolor lines correct under shear.
                    if (!Matrix3x2.Invert(matrix, out var inverse)) throw Failure(location, "Singular gradient mapping.");
                    var normal = new Vector2(inverse.M11, inverse.M21);
                    var axis = normal * (1638.4f / normal.LengthSquared());
                    var middle = new Vector2(matrix.M31, matrix.M32);
                    start = middle - axis / 2; end = middle + axis / 2;
                }
                else
                {
                    start = Vector2.Transform(Vector2.Zero, matrix);
                    end = Vector2.Transform(new Vector2(819.2f, 0), matrix);
                    diagnostics.Add(new("radial-gradient-native-approximation", location,
                        "Radial paint uses a native circular gradient; elliptical mapping and focal offset may differ."));
                }
                PointF Map(Vector2 point) => new(center.X + (point.X - bounds.Left - bounds.Width / 2) * width / bounds.Width,
                    center.Y + (point.Y - bounds.Top - bounds.Height / 2) * height / bounds.Height);
                scene.SetGradientPaint(index, linear ? GradientKind.Linear : GradientKind.Radial, stops, Map(start), Map(end));
                if ((string?)gradient.Attribute("spreadMethod") != "pad"
                    || (string?)gradient.Attribute("color-interpolation") != "sRGB")
                    diagnostics.Add(new("gradient-native-sampling-approximation", location,
                        "Native gradients use extended endpoints and RGB interpolation."));
            }
            if (VectorUnits.FromPixels(bounds.Width) < 2 || VectorUnits.FromPixels(bounds.Height) < 2)
                diagnostics.Add(new("tiny-shape-native-precision", location,
                    "Sub-unit artwork expanded to a minimum two-vector-unit editable extent."));
        }
        static int WithAlpha(int argb, float alpha) => Color.FromArgb(
            (int)MathF.Round(Color.FromArgb(argb).A * alpha), Color.FromArgb(argb)).ToArgb();
    }
}
