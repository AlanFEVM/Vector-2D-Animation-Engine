using System.Drawing.Drawing2D;
using Svg;

namespace VectorAnimationEngine;

internal static partial class ImportedSvgRasterizer
{
    // SVG.NET 3.4.7 parses masks but does not render them. Only our explicitly
    // geometric, opaque-white masks can be represented by regions; arbitrary
    // imported luminance/alpha masks must not be reinterpreted this way.
    private static void PrepareGeometryMasks(SvgElement parent)
    {
        foreach (var child in parent.Children.ToArray())
        {
            PrepareGeometryMasks(child);
            if (child is not SvgGroup group || GeometryMask(group) is null) continue;
            var replacement = group.DeepCopy<GeometryMaskedGroup>();
            var index = parent.Children.IndexOf(child);
            parent.Children.RemoveAt(index);
            if (index == parent.Children.Count) parent.Children.Add(replacement);
            else parent.Children.Insert(index, replacement);
        }
    }

    private static SvgMask? GeometryMask(SvgElement element)
    {
        if (!element.TryGetAttribute("mask", out var reference)
            || !reference.StartsWith("url(#", StringComparison.Ordinal) || !reference.EndsWith(')')) return null;
        var mask = element.OwnerDocument.GetElementById(reference[5..^1]) as SvgMask;
        return mask is not null && mask.CustomAttributes.TryGetValue("data-v2d-geometric-mask", out var marker)
            && marker == "1" ? mask : null;
    }

    private sealed class GeometryMaskedGroup : SvgGroup
    {
        private Region? _savedGeometryClip;
        public GeometryMaskedGroup() { }
        public override SvgElement DeepCopy() => DeepCopy<GeometryMaskedGroup>();

        protected override void SetClip(ISvgRenderer renderer)
        {
            base.SetClip(renderer);
            _savedGeometryClip = renderer.GetClip();
            using var coverage = GeometryCoverage(GeometryMask(this)
                ?? throw new InvalidDataException("Missing geometric SVG mask."), renderer, []);
            renderer.SetClip(coverage, CombineMode.Intersect);
        }

        protected override void ResetClip(ISvgRenderer renderer)
        {
            if (_savedGeometryClip is not null)
            {
                renderer.SetClip(_savedGeometryClip, CombineMode.Replace);
                _savedGeometryClip.Dispose();
                _savedGeometryClip = null;
            }
            base.ResetClip(renderer);
        }
    }

    private static Region GeometryCoverage(SvgElement element, ISvgRenderer renderer, HashSet<SvgElement> visiting)
    {
        if (!visiting.Add(element)) throw new InvalidDataException("Cyclic geometric SVG mask.");
        var result = new Region();
        result.MakeEmpty();
        try
        {
            if (element is SvgVisualElement visual && element is not SvgGroup)
            {
                // Clone cached paths: transforming a source path corrupts later
                // occurrences and subsequent raster sizes of the same document.
                using var path = visual.Path(renderer)?.Clone() as GraphicsPath;
                if (path is not null && path.PointCount > 0)
                {
                    path.FillMode = visual.FillRule == SvgFillRule.NonZero ? FillMode.Winding : FillMode.Alternate;
                    if (visual.Fill != SvgPaintServer.None) result.Union(path);
                    if (visual.Stroke is not null && visual.Stroke != SvgPaintServer.None && visual.StrokeWidth > 0)
                    {
                        using var stroke = (GraphicsPath)path.Clone();
                        using var pen = new Pen(Color.White, visual.StrokeWidth.ToDeviceValue(renderer, UnitRenderingType.Other, visual));
                        pen.StartCap = pen.EndCap = visual.StrokeLineCap switch
                        {
                            SvgStrokeLineCap.Round => LineCap.Round,
                            SvgStrokeLineCap.Square => LineCap.Square,
                            _ => LineCap.Flat
                        };
                        pen.LineJoin = visual.StrokeLineJoin switch
                        {
                            SvgStrokeLineJoin.Round => LineJoin.Round,
                            SvgStrokeLineJoin.Bevel => LineJoin.Bevel,
                            _ => LineJoin.Miter
                        };
                        pen.MiterLimit = visual.StrokeMiterLimit;
                        stroke.Widen(pen);
                        result.Union(stroke);
                    }
                }
            }
            else
                foreach (var child in element.Children)
                {
                    using var coverage = GeometryCoverage(child, renderer, visiting);
                    result.Union(coverage);
                }
            if (GeometryMask(element) is { } mask)
            {
                using var clip = GeometryCoverage(mask, renderer, visiting);
                result.Intersect(clip);
            }
            if (element.Transforms is { Count: > 0 })
            {
                using var transform = element.Transforms.GetMatrix();
                result.Transform(transform);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { visiting.Remove(element); }
    }
}
