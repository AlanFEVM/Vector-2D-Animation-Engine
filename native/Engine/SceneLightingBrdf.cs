namespace VectorAnimationEngine;

internal readonly record struct SceneLightingBrdfResponse(
    float Diffuse,
    float Specular,
    float Fresnel)
{
    // Weight aliases keep the pixel-shading contract explicit at call sites.
    public float DiffuseWeight => Diffuse;
    public float SpecularF0Weight => Specular;
    public float SpecularGrazingWeight => Fresnel;
}

internal static class SceneLightingBrdf
{
    private const float Pi = MathF.PI;
    private const float MinimumAlpha = 0.0025f;

    // The material is normalized by the owning render pass before entering its
    // pixel loop. Keeping that contract here avoids a per-pixel Normalize call.
    internal static SceneLightingBrdfResponse Evaluate(
        SpatialOpticalMaterial material,
        float nDotL,
        float nDotV,
        float nDotH,
        float vDotH)
    {
        if (!material.IsValid
            || !float.IsFinite(nDotL)
            || !float.IsFinite(nDotV)
            || !float.IsFinite(nDotH)
            || !float.IsFinite(vDotH)
            || nDotL <= 0f
            || nDotV <= 0f
            || nDotL > 1f
            || nDotV > 1f
            || nDotH < 0f
            || nDotH > 1f
            || vDotH < 0f
            || vDotH > 1f)
        {
            return default;
        }

        var alpha = MathF.Max(material.Roughness * material.Roughness, MinimumAlpha);
        var alphaSquared = alpha * alpha;

        // Trowbridge-Reitz (isotropic GGX) normal distribution.
        var nDotHSquared = nDotH * nDotH;
        var distributionDenominator = (1f - nDotHSquared) + nDotHSquared * alphaSquared;
        var distribution = alphaSquared
            / (Pi * distributionDenominator * distributionDenominator);

        // Height-correlated Smith GGX visibility (G2 / (4 NoL NoV)).
        var viewRoot = MathF.Sqrt(nDotV * nDotV * (1f - alphaSquared) + alphaSquared);
        var lightRoot = MathF.Sqrt(nDotL * nDotL * (1f - alphaSquared) + alphaSquared);
        var visibilityDenominator = nDotL * viewRoot + nDotV * lightRoot;
        if (!float.IsFinite(distribution)
            || !float.IsFinite(visibilityDenominator)
            || visibilityDenominator <= 0f)
        {
            return default;
        }

        var visibility = 0.5f / visibilityDenominator;
        var baseWeight = distribution * visibility * nDotL;
        var fresnelWeight = MathF.Pow(1f - vDotH, 5f);
        var diffuse = EvaluateDiffuse(material, nDotL);
        var specular = baseWeight * (1f - fresnelWeight);
        var fresnel = baseWeight * fresnelWeight;
        if (!float.IsFinite(diffuse)
            || !float.IsFinite(specular)
            || !float.IsFinite(fresnel))
        {
            return default;
        }

        return new SceneLightingBrdfResponse(diffuse, specular, fresnel);
    }

    internal static float DielectricF0(SpatialOpticalMaterial material)
    {
        return material.IsValid ? CalculateDielectricF0(material) : 0f;
    }

    internal static float EvaluateDiffuse(SpatialOpticalMaterial material, float nDotL)
    {
        if (!material.IsValid || !float.IsFinite(nDotL) || nDotL is < 0f or > 1f) return 0f;
        return ((1f - material.Metallic) * (1f - material.Transmission)
            * (1f - CalculateDielectricF0(material)) / Pi) * nDotL;
    }

    private static float CalculateDielectricF0(SpatialOpticalMaterial material)
    {
        var iorRatio = (material.IndexOfRefraction - 1f)
            / (material.IndexOfRefraction + 1f);
        return Math.Clamp(
            Math.Max(material.Reflectivity, iorRatio * iorRatio),
            0f,
            1f);
    }
}
