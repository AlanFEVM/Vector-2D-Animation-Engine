namespace VectorAnimationEngine;

internal enum SmartLayoutAction { Left, CenterX, Right, Top, CenterY, Bottom, GapsX, GapsY }
internal enum SmartLayoutReference { Selection, KeyObject, Canvas }

internal static class SmartLayout
{
    internal static PointF[] Plan(IReadOnlyList<RectangleF> bounds, SmartLayoutAction action,
        SmartLayoutReference reference, int keyIndex, RectangleF? canvas)
    {
        var result = new PointF[bounds.Count];
        if (bounds.Count == 0 || bounds.Any(b => !SmartSnapIndex.Finite(b))) return result;
        var union = bounds[0];
        foreach (var b in bounds.Skip(1)) union = RectangleF.Union(union, b);
        var anchor = reference switch
        {
            SmartLayoutReference.KeyObject => bounds[Math.Clamp(keyIndex, 0, bounds.Count - 1)],
            SmartLayoutReference.Canvas when canvas is { } page => page,
            _ => union
        };
        if (action is SmartLayoutAction.GapsX or SmartLayoutAction.GapsY)
        {
            if (bounds.Count < 3) return result;
            var horizontal = action == SmartLayoutAction.GapsX;
            var ordered = Enumerable.Range(0, bounds.Count).OrderBy(i => horizontal ? bounds[i].Left : bounds[i].Top).ThenBy(i => i).ToArray();
            var low = horizontal ? anchor.Left : anchor.Top;
            var high = horizontal ? anchor.Right : anchor.Bottom;
            var total = bounds.Sum(b => horizontal ? b.Width : b.Height);
            var gap = (high - low - total) / (bounds.Count - 1);
            // Distribution uses the selection span, or the canvas. A key object
            // remains an alignment reference, not an ambiguous distribution span.
            if (reference == SmartLayoutReference.KeyObject) return result;
            var cursor = low;
            foreach (var index in ordered)
            {
                var b = bounds[index];
                result[index] = horizontal ? new(cursor - b.Left, 0) : new(0, cursor - b.Top);
                cursor += (horizontal ? b.Width : b.Height) + gap;
            }
            return result;
        }
        for (var i = 0; i < bounds.Count; i++)
        {
            if (reference == SmartLayoutReference.KeyObject && i == keyIndex) continue;
            var b = bounds[i];
            result[i] = action switch
            {
                SmartLayoutAction.Left => new(anchor.Left - b.Left, 0),
                SmartLayoutAction.CenterX => new(anchor.Left + anchor.Width / 2 - b.Left - b.Width / 2, 0),
                SmartLayoutAction.Right => new(anchor.Right - b.Right, 0),
                SmartLayoutAction.Top => new(0, anchor.Top - b.Top),
                SmartLayoutAction.CenterY => new(0, anchor.Top + anchor.Height / 2 - b.Top - b.Height / 2),
                SmartLayoutAction.Bottom => new(0, anchor.Bottom - b.Bottom),
                _ => PointF.Empty
            };
        }
        return result;
    }
}
