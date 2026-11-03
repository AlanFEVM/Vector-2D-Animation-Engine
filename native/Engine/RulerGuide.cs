namespace VectorAnimationEngine;

internal sealed record RulerGuide(bool Horizontal, float Position);

internal static class RulerGuideValidation
{
    internal static RulerGuide[] Copy(IEnumerable<RulerGuide> guides)
    {
        var result = guides.Take(4097).ToArray();
        if (result.Length > 4096 || result.Any(item => item is null || !float.IsFinite(item.Position) || Math.Abs(item.Position) > 100000000))
            throw new InvalidDataException("Invalid ruler guides.");
        return result;
    }
}
