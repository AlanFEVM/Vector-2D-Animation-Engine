using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSceneLightingBrdfRegression()
    {
        const float inversePi = 1f / MathF.PI;
        var material = SpatialOpticalMaterial.Default;
        var normal = SceneLightingBrdf.Evaluate(material, 1f, 1f, 1f, 1f);
        var expectedNormalDiffuse = (1f - SceneLightingBrdf.DielectricF0(material)) * inversePi;
        var expectedNormalSpecular = 4f * inversePi;
        AssertNear(normal.Diffuse, expectedNormalDiffuse, 0.00001f, "normal diffuse");
        AssertNear(normal.Specular, expectedNormalSpecular, 0.00001f, "normal GGX visibility");
        AssertNear(normal.Fresnel, 0f, 0.000001f, "normal Schlick grazing term");
        var smoothLimit = SceneLightingBrdf.Evaluate(
            material with { Roughness = 0f }, 1f, 1f, 1f, 1f);
        AssertNear(smoothLimit.Specular, 1f / (4f * MathF.PI * 0.0025f * 0.0025f),
            0.01f, "finite smooth-surface peak");

        var sharp = SceneLightingBrdf.Evaluate(
            material with { Roughness = 0.15f },
            1f,
            1f,
            1f,
            1f);
        var broad = SceneLightingBrdf.Evaluate(
            material with { Roughness = 0.8f },
            1f,
            1f,
            1f,
            1f);
        var sharpTail = SceneLightingBrdf.Evaluate(
            material with { Roughness = 0.15f },
            0.8f,
            0.8f,
            0.5f,
            0.8f);
        var broadTail = SceneLightingBrdf.Evaluate(
            material with { Roughness = 0.8f },
            0.8f,
            0.8f,
            0.5f,
            0.8f);
        AssertTimeline(
            sharp.Specular > broad.Specular
            && broadTail.Specular > sharpTail.Specular,
            "GGX roughness did not move energy from the peak into the tail.");

        var backside = SceneLightingBrdf.Evaluate(material, 0f, 1f, 1f, 1f);
        var viewBackside = SceneLightingBrdf.Evaluate(material, 1f, -0.01f, 1f, 1f);
        AssertZero(backside, "light backside");
        AssertZero(viewBackside, "view backside");

        var grazing = SceneLightingBrdf.Evaluate(material, 0.000001f, 0.35f, 0.5f, 0.1f);
        AssertTimeline(
            float.IsFinite(grazing.Diffuse)
            && float.IsFinite(grazing.Specular)
            && float.IsFinite(grazing.Fresnel)
            && grazing.Fresnel > grazing.Specular,
            "GGX grazing evaluation was non-finite or did not increase Schlick Fresnel.");

        AssertNear(
            SceneLightingBrdf.Evaluate(material with { Metallic = 1f }, 1f, 1f, 1f, 1f).Diffuse,
            0f,
            0.000001f,
            "metal diffuse");
        AssertNear(
            SceneLightingBrdf.Evaluate(material with { Transmission = 1f }, 1f, 1f, 1f, 1f).Diffuse,
            0f,
            0.000001f,
            "transmission diffuse");

        AssertZero(
            SceneLightingBrdf.Evaluate(material, float.NaN, 1f, 1f, 1f),
            "non-finite dot product");
        AssertZero(
            SceneLightingBrdf.Evaluate(material with { Roughness = float.NaN }, 1f, 1f, 1f, 1f),
            "non-finite material");
        AssertZero(
            SceneLightingBrdf.Evaluate(material, 1f, 1f, 1.01f, 1f),
            "out-of-domain half vector");

        AssertNear(SceneLightingBrdf.DielectricF0(material), 0.04f, 0.000001f, "dielectric IOR F0");
        AssertNear(
            SceneLightingBrdf.DielectricF0(material with { Reflectivity = 0.32f }),
            0.32f,
            0.000001f,
            "dielectric reflectivity F0");

        // The nonzero roughness keeps deterministic sampling from missing a narrow
        // zero-roughness peak while approximating a white-furnace hemisphere.
        var furnaceMaterial = material with { Roughness = 0.45f };
        var furnaceEnergy = IntegrateWhiteFurnace(furnaceMaterial, nDotV: 0.65f);
        AssertTimeline(
            float.IsFinite(furnaceEnergy)
            && furnaceEnergy > 0.9f
            && furnaceEnergy <= 1.06f,
            $"White-furnace BRDF energy escaped its expected bound: {furnaceEnergy:0.######}.");

        Console.WriteLine(
            $"scene_lighting_brdf=ok,normal_specular={normal.Specular:0.######},furnace={furnaceEnergy:0.######}");

        static void AssertNear(float actual, float expected, float tolerance, string label)
        {
            if (!float.IsFinite(actual) || MathF.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(
                    $"Scene lighting BRDF {label} was {actual:0.########}; expected {expected:0.########}.");
            }
        }

        static void AssertZero(SceneLightingBrdfResponse response, string label)
        {
            AssertNear(response.Diffuse, 0f, 0f, label + " diffuse");
            AssertNear(response.Specular, 0f, 0f, label + " specular");
            AssertNear(response.Fresnel, 0f, 0f, label + " fresnel");
        }

        static float IntegrateWhiteFurnace(SpatialOpticalMaterial material, float nDotV)
        {
            const int muSamples = 96;
            const int azimuthSamples = 192;
            var view = new Vector3(MathF.Sqrt(1f - nDotV * nDotV), 0f, nDotV);
            var dielectricF0 = SceneLightingBrdf.DielectricF0(material);
            var sum = 0d;
            var sampleWeight = 2d * Math.PI / (muSamples * azimuthSamples);
            for (var muIndex = 0; muIndex < muSamples; muIndex++)
            {
                var nDotL = (muIndex + 0.5f) / muSamples;
                var sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - nDotL * nDotL));
                for (var azimuthIndex = 0; azimuthIndex < azimuthSamples; azimuthIndex++)
                {
                    var azimuth = (azimuthIndex + 0.5f) * (2f * MathF.PI / azimuthSamples);
                    var light = new Vector3(
                        sinTheta * MathF.Cos(azimuth),
                        sinTheta * MathF.Sin(azimuth),
                        nDotL);
                    var half = light + view;
                    var halfLengthSquared = half.LengthSquared();
                    if (!float.IsFinite(halfLengthSquared) || halfLengthSquared <= 0f) continue;
                    half /= MathF.Sqrt(halfLengthSquared);
                    var response = SceneLightingBrdf.Evaluate(
                        material,
                        nDotL,
                        nDotV,
                        half.Z,
                        Vector3.Dot(view, half));
                    sum += (response.Diffuse
                        + response.Specular * dielectricF0
                        + response.Fresnel) * sampleWeight;
                }
            }

            return (float)sum;
        }
    }
}
