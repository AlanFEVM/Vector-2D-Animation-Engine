namespace VectorAnimationEngine;

// Value types keep timeline keys, undo snapshots and duplicated instances independent.
// A missing field in older documents is the all-disabled default. Sizes are stage pixels
// at 100% zoom. Effects run in the fixed order blur, glow, shadow, bevel, gradient bevel, gradient glow.
internal readonly record struct SymbolFilters
{
    public SymbolBlurFilter Blur { get; init; }
    public SymbolGlowFilter Glow { get; init; }
    public SymbolShadowFilter Shadow { get; init; }
    public SymbolEdgeFilter Bevel { get; init; }
    public SymbolEdgeFilter GradientBevel { get; init; }
    public SymbolEdgeFilter GradientGlow { get; init; }
    public bool HasEdgeEffects => Bevel.Enabled || GradientBevel.Enabled || GradientGlow.Enabled;
    public bool HasEnabled => Blur.Enabled || Glow.Enabled || Shadow.Enabled || HasEdgeEffects;
    public bool IsValid => Blur.IsValid && Glow.IsValid && Shadow.IsValid
        && Bevel.IsValid && GradientBevel.IsValid && GradientGlow.IsValid;
}

internal readonly record struct SymbolEdgeFilter
{
    public bool Enabled { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public float Strength { get; init; }
    public float Opacity { get; init; }
    public float Angle { get; init; }
    public float Distance { get; init; }
    public int StartColorArgb { get; init; }
    public int EndColorArgb { get; init; }
    public static SymbolEdgeFilter Default => new()
    {
        Enabled = true, BlurX = 6, BlurY = 6, Strength = 1, Opacity = 1,
        Angle = 45, Distance = 4, StartColorArgb = unchecked((int)0xff202040),
        EndColorArgb = unchecked((int)0xffffffff)
    };
    public bool IsValid => SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity)
        && float.IsFinite(Angle) && Angle >= -360 && Angle <= 360
        && float.IsFinite(Distance) && Distance >= 0 && Distance <= 256;
}

internal readonly record struct SymbolBlurFilter
{
    public bool Enabled { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public static SymbolBlurFilter Default => new() { Enabled = true, BlurX = 6, BlurY = 6 };
    public bool IsValid => SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY);
}

internal readonly record struct SymbolGlowFilter
{
    public bool Enabled { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public float Strength { get; init; }
    public float Opacity { get; init; }
    public int ColorArgb { get; init; }
    public static SymbolGlowFilter Default => new()
    { Enabled = true, BlurX = 8, BlurY = 8, Strength = 1, Opacity = 1, ColorArgb = unchecked((int)0xffffcc33) };
    public bool IsValid => SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity);
}

internal readonly record struct SymbolShadowFilter
{
    public bool Enabled { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public float Strength { get; init; }
    public float Opacity { get; init; }
    public int ColorArgb { get; init; }
    public float Angle { get; init; }
    public float Distance { get; init; }
    public static SymbolShadowFilter Default => new()
    { Enabled = true, BlurX = 6, BlurY = 6, Strength = 1, Opacity = 0.7f,
      ColorArgb = unchecked((int)0xff000000), Angle = 45, Distance = 8 };
    public bool IsValid => SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity)
        && float.IsFinite(Angle) && Angle >= -360 && Angle <= 360
        && float.IsFinite(Distance) && Distance >= 0 && Distance <= 256;
}

internal static class SymbolFilterValidation
{
    internal static bool Radius(float value) => float.IsFinite(value) && value >= 0 && value <= 128;
    internal static bool Strength(float value) => float.IsFinite(value) && value >= 0 && value <= 10;
    internal static bool Opacity(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
}
