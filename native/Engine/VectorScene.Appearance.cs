namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal sealed record ObjectAppearance(int Fill, int StrokeColor, float StrokeWidth, RectangleF Bounds,
        GradientPaintData? Gradient, SvgNativeStrokeStyle? StrokeStyle);

    internal ObjectAppearance CaptureObjectAppearance(int index) => new(Argb[index], StrokeArgb[index], Stroke[index],
        GetObjectWorldBounds(index), CaptureGradientPaint(index), GetSvgStrokeStyle(index));

    internal void ApplyObjectAppearance(int index, ObjectAppearance appearance)
    {
        if ((uint)index >= ObjectCount || ObjectLocked[index]) return;
        Argb[index] = appearance.Fill;
        StrokeArgb[index] = appearance.StrokeColor;
        Stroke[index] = appearance.StrokeWidth;
        _svgStrokeStyles.Remove(index);
        if (appearance.StrokeStyle is { } style) SetSvgStrokeStyle(index, style);
        DisableLinearGradient(index);
        if (appearance.Gradient is not { } gradient) return;
        var target = GetObjectWorldBounds(index);
        PointF Map(PointF p) => new(target.X + (p.X - appearance.Bounds.X) / Math.Max(1, appearance.Bounds.Width) * target.Width,
            target.Y + (p.Y - appearance.Bounds.Y) / Math.Max(1, appearance.Bounds.Height) * target.Height);
        ApplyGradientPaint(index, gradient with
        {
            Start = Map(gradient.Start), End = Map(gradient.End),
            Path = gradient.Path.Select(Map).ToArray(),
            ShapeMappingContours = gradient.ShapeMappingContours.Select(c => c.Select(Map).ToArray()).ToArray()
        });
    }

    internal bool SameFillAppearance(int a, int b)
    {
        if (HasFill(a) != HasFill(b) || Argb[a] != Argb[b] || HasGradient(a) != HasGradient(b)) return false;
        return !HasGradient(a) || GradientKinds[a] == GradientKinds[b] && GetGradientStops(a).SequenceEqual(GetGradientStops(b));
    }

    internal bool SameStrokeAppearance(int a, int b) => HasStroke(a) == HasStroke(b)
        && Stroke[a] == Stroke[b] && StrokeArgb[a] == StrokeArgb[b] && GetSvgStrokeStyle(a) == GetSvgStrokeStyle(b);
}
