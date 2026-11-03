namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal IReadOnlyList<SmartSnapLine> SmartSnapLines { get; private set; } = [];
    internal void SetSmartSnapLines(IEnumerable<SmartSnapLine> lines)
    {
        var next = lines.ToArray();
        if (SmartSnapLines.SequenceEqual(next)) return;
        SmartSnapLines = next;
        Invalidate();
    }
    internal void ClearSmartSnapLines()
    {
        if (SmartSnapLines.Count == 0) return;
        SmartSnapLines = [];
        Invalidate();
    }
    internal IEnumerable<SmartSnapLine> SmartSnapScreenLines()
    {
        if (PlaybackActive) yield break;
        foreach (var line in SmartSnapLines)
        {
            var a = WorldToScreen(line.Start);
            var b = WorldToScreen(line.End);
            if (RendersReferenceProjection)
            {
                if (!TryProjectScenePosition(new(line.Start.X, line.Start.Y, 0), out a, out _)
                    || !TryProjectScenePosition(new(line.End.X, line.End.Y, 0), out b, out _)) continue;
            }
            yield return new(a, b, line.Label);
        }
    }
    private void DrawSmartSnapGdi(Graphics graphics)
    {
        using var pen = new Pen(Theme.Accent, Math.Max(1, DeviceDpi / 96f));
        foreach (var line in SmartSnapScreenLines())
        {
            graphics.DrawLine(pen, line.Start, line.End);
            if (!string.IsNullOrEmpty(line.Label))
                TextRenderer.DrawText(graphics, line.Label, Font,
                    new Point((int)((line.Start.X + line.End.X) / 2) + 4, (int)((line.Start.Y + line.End.Y) / 2) + 4),
                    Theme.Accent, Theme.Top, TextFormatFlags.NoPadding);
        }
    }
}
