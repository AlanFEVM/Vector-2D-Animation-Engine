using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

// Values intentionally match the portable SVG names, independently of native endpoint tools.
internal enum SvgNativeCap { Butt, Round, Square }
internal enum SvgNativeJoin { Miter, Round, Bevel }
internal readonly record struct SvgNativeStrokeStyle(SvgNativeCap Cap, SvgNativeJoin Join, float MiterLimit);

internal sealed partial class VectorScene
{
    private readonly Dictionary<int, SvgNativeStrokeStyle> _svgStrokeStyles = new();
    internal SvgNativeStrokeStyle? GetSvgStrokeStyle(int index) => _svgStrokeStyles.TryGetValue(index, out var style) ? style : null;
    internal void SetSvgStrokeStyle(int index, SvgNativeStrokeStyle style)
    {
        if ((uint)index >= ObjectCount || !Enum.IsDefined(style.Cap) || !Enum.IsDefined(style.Join)
            || !float.IsFinite(style.MiterLimit) || style.MiterLimit < 1)
            throw new InvalidDataException("Invalid SVG stroke style.");
        _svgStrokeStyles[index] = style;
    }
    internal void ApplySvgStrokeStyle(int index, Pen pen)
    {
        if (GetSvgStrokeStyle(index) is not { } style) return;
        pen.StartCap = pen.EndCap = style.Cap switch
        { SvgNativeCap.Round => LineCap.Round, SvgNativeCap.Square => LineCap.Square, _ => LineCap.Flat };
        pen.LineJoin = style.Join switch
        { SvgNativeJoin.Round => LineJoin.Round, SvgNativeJoin.Bevel => LineJoin.Bevel, _ => LineJoin.Miter };
        pen.MiterLimit = style.MiterLimit;
    }
}
