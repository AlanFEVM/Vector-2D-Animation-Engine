namespace VectorAnimationEngine;

internal readonly record struct SpatialOpticalMaterial(
    float Transmission,
    float Reflectivity,
    float Metallic,
    float Roughness,
    float IndexOfRefraction,
    bool CastsShadows,
    bool ReceivesShadows)
{
    public static SpatialOpticalMaterial Default { get; } = new(
        Transmission: 0f,
        Reflectivity: 0.04f,
        Metallic: 0f,
        Roughness: 0.5f,
        IndexOfRefraction: 1.5f,
        CastsShadows: true,
        ReceivesShadows: true);

    public bool IsValid =>
        UnitInterval(Transmission)
        && UnitInterval(Reflectivity)
        && UnitInterval(Metallic)
        && UnitInterval(Roughness)
        && float.IsFinite(IndexOfRefraction)
        && IndexOfRefraction is >= 1f and <= 4f;

    public SpatialOpticalMaterial Normalize()
    {
        return new SpatialOpticalMaterial(
            NormalizeUnit(Transmission, Default.Transmission),
            NormalizeUnit(Reflectivity, Default.Reflectivity),
            NormalizeUnit(Metallic, Default.Metallic),
            NormalizeUnit(Roughness, Default.Roughness),
            float.IsFinite(IndexOfRefraction)
                ? Math.Clamp(IndexOfRefraction, 1f, 4f)
                : Default.IndexOfRefraction,
            CastsShadows,
            ReceivesShadows);
    }

    private static bool UnitInterval(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 1f;

    private static float NormalizeUnit(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
}
