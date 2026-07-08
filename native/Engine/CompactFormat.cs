namespace VectorAnimationEngine;

internal static class CompactFormat
{
    public static string Number(double value)
    {
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000:0.##}B";
        if (value >= 1_000_000) return $"{value / 1_000_000:0.##}M";
        if (value >= 1_000) return $"{value / 1_000:0.#}K";
        return $"{value:0}";
    }
}
