namespace VectorAnimationEngine;

// Value types keep timeline keys, undo snapshots and duplicated instances independent.
// A missing field in older documents is the all-disabled default. Sizes are stage pixels
// at 100% zoom. Missing Order retains the legacy built-in sequence.
internal readonly record struct SymbolFilters
{
    public SymbolFilterOrder? Order { get; init; }
    public SymbolFlashColorTransform ColorTransform { get; init; }
    public SymbolBlurFilter Blur { get; init; }
    // Imported Flash blur filters run in source order after Blur. The immutable
    // value object preserves snapshot independence and structural cache equality.
    public SymbolBlurStack? AdditionalBlurs { get; init; }
    public SymbolFlashFilterStack? FlashFilters { get; init; }
    public SymbolGlowFilter Glow { get; init; }
    public SymbolShadowFilter Shadow { get; init; }
    public SymbolEdgeFilter Bevel { get; init; }
    public SymbolEdgeFilter GradientBevel { get; init; }
    public SymbolEdgeFilter GradientGlow { get; init; }
    public bool HasEdgeEffects => Bevel.Enabled || GradientBevel.Enabled || GradientGlow.Enabled;
    public bool HasEnabled => ColorTransform.Enabled || Blur.Enabled || AdditionalBlurs?.HasEnabled == true || FlashFilters?.HasEnabled == true || Glow.Enabled || Shadow.Enabled || HasEdgeEffects;
    public bool IsValid => ColorTransform.IsValid && Blur.IsValid && Glow.IsValid && Shadow.IsValid
        && (AdditionalBlurs?.IsValid ?? true)
        && (FlashFilters?.IsValid ?? true)
        && Bevel.IsValid && GradientBevel.IsValid && GradientGlow.IsValid;
}

internal enum SymbolFilterKind { Blur, Glow, Shadow, Bevel, GradientBevel, GradientGlow }

internal sealed class SymbolFilterOrder : IEquatable<SymbolFilterOrder>
{
    private readonly SymbolFilterKind[] _items;
    internal static SymbolFilterOrder Default { get; } = new(Enum.GetValues<SymbolFilterKind>());
    [System.Text.Json.Serialization.JsonConstructor]
    public SymbolFilterOrder(SymbolFilterKind[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length != 6 || items.Distinct().Count() != 6 || items.Any(item => !Enum.IsDefined(item)))
            throw new ArgumentException("A filter order must contain each of the six built-in filters exactly once.", nameof(items));
        _items = (SymbolFilterKind[])items.Clone();
    }
    public SymbolFilterKind[] Items => (SymbolFilterKind[])_items.Clone();
    internal SymbolFilterKind this[int index] => _items[index];
    public bool Equals(SymbolFilterOrder? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? other) => other is SymbolFilterOrder order && Equals(order);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var item in _items) hash.Add(item); return hash.ToHashCode(); }
}

internal readonly record struct SymbolGradientStop(float Offset, int ColorArgb);

internal sealed class SymbolGradientStops : IEquatable<SymbolGradientStops>
{
    internal const int MaximumCount = 16;
    private readonly SymbolGradientStop[] _items;
    [System.Text.Json.Serialization.JsonConstructor]
    public SymbolGradientStops(SymbolGradientStop[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length is < 2 or > MaximumCount || items.Any(item => !float.IsFinite(item.Offset) || item.Offset is < 0 or > 1))
            throw new ArgumentException("A gradient needs 2 to 16 finite color stops between zero and one.", nameof(items));
        _items = items.OrderBy(item => item.Offset).ToArray();
        if (_items[0].Offset != 0 || _items[^1].Offset != 1)
            throw new ArgumentException("A filter gradient must include stops at zero and one.", nameof(items));
        if (_items.Zip(_items.Skip(1), (left, right) => left.Offset >= right.Offset).Any(invalid => invalid))
            throw new ArgumentException("Filter gradient stop positions must be distinct.", nameof(items));
    }
    public SymbolGradientStop[] Items => (SymbolGradientStop[])_items.Clone();
    internal int Count => _items.Length;
    internal SymbolGradientStop this[int index] => _items[index];
    internal System.Numerics.Vector4 Sample(float position)
    {
        if (position < _items[0].Offset) return Straight(_items[0].ColorArgb);
        for (var i = 1; i < _items.Length; i++)
        {
            if (position >= _items[i].Offset) continue;
            var amount = Math.Clamp((position - _items[i - 1].Offset) / Math.Max(1e-10f, _items[i].Offset - _items[i - 1].Offset), 0, 1);
            var a = LinearPremultiplied(_items[i - 1].ColorArgb);
            var b = LinearPremultiplied(_items[i].ColorArgb);
            var value = System.Numerics.Vector4.Lerp(a, b, amount);
            if (value.W <= 0) return default;
            return new(Encode(value.X / value.W), Encode(value.Y / value.W), Encode(value.Z / value.W), value.W);
        }
        return Straight(_items[^1].ColorArgb);
    }
    internal static System.Numerics.Vector4 LinearPremultiplied(int argb)
    {
        var value = Straight(argb);
        return new(Decode(value.X) * value.W, Decode(value.Y) * value.W, Decode(value.Z) * value.W, value.W);
    }
    private static System.Numerics.Vector4 Straight(int argb)
    {
        var color = (uint)argb;
        return new((color & 255) / 255f, ((color >> 8) & 255) / 255f, ((color >> 16) & 255) / 255f, (color >> 24) / 255f);
    }
    private static float Decode(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static float Encode(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    public bool Equals(SymbolGradientStops? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? other) => other is SymbolGradientStops stops && Equals(stops);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var item in _items) hash.Add(item); return hash.ToHashCode(); }
}

// Straight RGB affine transform, applied to the isolated source before its filters.
// Offsets are normalized channel values; zero-alpha pixels remain transparent.
internal readonly record struct SymbolFlashColorTransform
{
    public bool Enabled { get; init; }
    public float? AlphaMultiplier { get; init; }
    public float RedMultiplier { get; init; }
    public float GreenMultiplier { get; init; }
    public float BlueMultiplier { get; init; }
    public float RedOffset { get; init; }
    public float GreenOffset { get; init; }
    public float BlueOffset { get; init; }
    public bool IsValid => !Enabled || SymbolFilterValidation.Opacity(AlphaMultiplier ?? 1) && Valid(RedMultiplier, RedOffset)
        && Valid(GreenMultiplier, GreenOffset) && Valid(BlueMultiplier, BlueOffset);
    private static bool Valid(float multiplier, float offset) => float.IsFinite(multiplier)
        && multiplier is >= -1 and <= 1 && float.IsFinite(offset) && offset is >= -1 and <= 1;
}

internal enum SymbolFlashFilterKind { Blur, Glow, Shadow }

internal readonly record struct SymbolFlashFilter
{
    public SymbolFlashFilterKind Kind { get; init; }
    public bool Enabled { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public int Quality { get; init; }
    public int ColorArgb { get; init; }
    public float Opacity { get; init; }
    public float Strength { get; init; }
    public float Angle { get; init; }
    public float Distance { get; init; }
    public bool Inner { get; init; }
    public bool Knockout { get; init; }
    public bool HideObject { get; init; }
    internal SymbolBlurFilter Blur => new() { Enabled = Enabled, FlashCompatible = true, BlurX = BlurX, BlurY = BlurY, Quality = Quality };
    internal bool IsValid => Enum.IsDefined(Kind) && Blur.IsValid
        && float.IsFinite(Strength) && Strength is >= 0 and <= 255
        && SymbolFilterValidation.Opacity(Opacity) && float.IsFinite(Angle)
        && float.IsFinite(Distance) && Distance is >= -256 and <= 256;
}

// No mutable array can escape into an instance key, undo snapshot or GPU cache.
internal sealed class SymbolFlashFilterStack : IEquatable<SymbolFlashFilterStack>
{
    private readonly SymbolFlashFilter[] _items;
    [System.Text.Json.Serialization.JsonConstructor]
    public SymbolFlashFilterStack(SymbolFlashFilter[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length is < 1 or > 32) throw new ArgumentException("A Flash filter stack must contain 1 to 32 filters.", nameof(items));
        _items = (SymbolFlashFilter[])items.Clone();
    }
    public SymbolFlashFilter[] Items => (SymbolFlashFilter[])_items.Clone();
    internal int Count => _items.Length;
    internal SymbolFlashFilter this[int index] => _items[index];
    internal bool HasEnabled => _items.Any(item => item.Enabled);
    internal bool IsValid => _items.All(item => item.IsValid);
    public bool Equals(SymbolFlashFilterStack? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? other) => other is SymbolFlashFilterStack stack && Equals(stack);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}

internal sealed class SymbolBlurStack : IEquatable<SymbolBlurStack>
{
    internal const int MaximumAdditionalFilters = 31;
    private readonly SymbolBlurFilter[] _items;
    [System.Text.Json.Serialization.JsonConstructor]
    public SymbolBlurStack(SymbolBlurFilter[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length is < 1 or > MaximumAdditionalFilters)
            throw new ArgumentException("A blur stack must contain 1 to 31 additional filters.", nameof(items));
        _items = (SymbolBlurFilter[])items.Clone();
    }
    public SymbolBlurFilter[] Items => (SymbolBlurFilter[])_items.Clone();
    internal int Count => _items.Length;
    internal SymbolBlurFilter this[int index] => _items[index];
    internal bool HasEnabled => _items.Any(item => item.Enabled);
    internal bool IsValid => _items.All(item => item.IsValid);
    public bool Equals(SymbolBlurStack? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? other) => other is SymbolBlurStack stack && Equals(stack);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}

internal readonly record struct SymbolEdgeFilter
{
    public SymbolGradientStops? GradientStops { get; init; }
    public bool Enabled { get; init; }
    public int? Quality { get; init; }
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
    public bool IsValid => SymbolFilterValidation.Quality(Quality) && SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity)
        && float.IsFinite(Angle) && Angle >= -360 && Angle <= 360
        && float.IsFinite(Distance) && Distance >= 0 && Distance <= 256;
}

internal readonly record struct SymbolBlurFilter
{
    public bool Enabled { get; init; }
    public int? Quality { get; init; }
    // Absent in legacy V2D documents: retain their original floating-point kernel.
    // Flash stores full kernel widths and quantizes after every axis/pass.
    public bool FlashCompatible { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public static SymbolBlurFilter Default => new() { Enabled = true, BlurX = 6, BlurY = 6 };
    public bool IsValid => SymbolFilterValidation.Quality(Quality)
        && float.IsFinite(BlurX) && BlurX >= 0 && BlurX <= (FlashCompatible ? 255 : 128)
        && float.IsFinite(BlurY) && BlurY >= 0 && BlurY <= (FlashCompatible ? 255 : 128);
}

internal readonly record struct SymbolGlowFilter
{
    public bool Enabled { get; init; }
    public int? Quality { get; init; }
    public float BlurX { get; init; }
    public float BlurY { get; init; }
    public float Strength { get; init; }
    public float Opacity { get; init; }
    public int ColorArgb { get; init; }
    public static SymbolGlowFilter Default => new()
    { Enabled = true, BlurX = 8, BlurY = 8, Strength = 1, Opacity = 1, ColorArgb = unchecked((int)0xffffcc33) };
    public bool IsValid => SymbolFilterValidation.Quality(Quality) && SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity);
}

internal readonly record struct SymbolShadowFilter
{
    public bool Enabled { get; init; }
    public int? Quality { get; init; }
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
    public bool IsValid => SymbolFilterValidation.Quality(Quality) && SymbolFilterValidation.Radius(BlurX) && SymbolFilterValidation.Radius(BlurY)
        && SymbolFilterValidation.Strength(Strength) && SymbolFilterValidation.Opacity(Opacity)
        && float.IsFinite(Angle) && Angle >= -360 && Angle <= 360
        && float.IsFinite(Distance) && Distance >= 0 && Distance <= 256;
}

internal static class SymbolFilterValidation
{
    // Nullable so absent legacy fields preserve the original three-pass rendering.
    // Explicit zero/out-of-range values are invalid rather than silently defaulted.
    internal const int LegacyQuality = 3;
    internal static bool Quality(int? value) => value is null or >= 1 and <= 15;
    internal static int Passes(int? value) => value ?? LegacyQuality;
    internal static bool Radius(float value) => float.IsFinite(value) && value >= 0 && value <= 128;
    internal static bool Strength(float value) => float.IsFinite(value) && value >= 0 && value <= 10;
    internal static bool Opacity(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
}
