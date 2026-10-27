using System.Numerics;
using System.Text.Json.Serialization;

namespace VectorAnimationEngine;

internal readonly record struct SceneShotParallaxSettings
{
    [JsonConstructor]
    public SceneShotParallaxSettings() { }

    internal const float NearDistance = 10f;
    public bool Enabled { get; init; }
    public float Strength { get; init; } = 1f;
    public float ReferenceZ { get; init; }
    public bool KeepSize { get; init; } = true;
    public float HorizontalResponse { get; init; } = 1f;
    public float VerticalResponse { get; init; } = 1f;
    public bool DollyScaling { get; init; } = true;
    public float SizeAnchorZ { get; init; } = -1000f;

    internal bool IsValid => InRange(Strength, 0, 2)
        && InRange(HorizontalResponse, 0, 2) && InRange(VerticalResponse, 0, 2)
        && InRange(ReferenceZ, -SceneShotSettings.MaximumPosition, SceneShotSettings.MaximumPosition)
        && InRange(SizeAnchorZ, -SceneShotSettings.MaximumPosition, SceneShotSettings.MaximumPosition);

    internal SceneShotParallaxSettings Normalized() => this with
    {
        Strength = Normalize(Strength, 1, 0, 2),
        HorizontalResponse = Normalize(HorizontalResponse, 1, 0, 2),
        VerticalResponse = Normalize(VerticalResponse, 1, 0, 2),
        ReferenceZ = Normalize(ReferenceZ, 0, -SceneShotSettings.MaximumPosition, SceneShotSettings.MaximumPosition),
        SizeAnchorZ = Normalize(SizeAnchorZ, -1000, -SceneShotSettings.MaximumPosition, SceneShotSettings.MaximumPosition)
    };

    internal static SceneShotParallaxSettings Interpolate(SceneShotParallaxSettings source,
        SceneShotParallaxSettings target, float progress) => source with
    {
        Strength = float.Lerp(source.Strength, target.Strength, progress),
        ReferenceZ = float.Lerp(source.ReferenceZ, target.ReferenceZ, progress),
        HorizontalResponse = float.Lerp(source.HorizontalResponse, target.HorizontalResponse, progress),
        VerticalResponse = float.Lerp(source.VerticalResponse, target.VerticalResponse, progress),
        SizeAnchorZ = float.Lerp(source.SizeAnchorZ, target.SizeAnchorZ, progress)
    };

    internal Matrix4x4 ResolveTransform(SceneShotSettings camera, float depth)
        => ResolveTransform(camera, depth, camera with { X = 0, Y = 0, Z = SizeAnchorZ });

    internal Matrix4x4 ResolveTransform(SceneShotSettings camera, float depth, SceneShotSettings anchor)
    {
        if (!Enabled || camera.Kind != SceneShotCameraKind.TwoD || Strength <= 0) return Matrix4x4.Identity;
        var referenceDistance = Math.Max(NearDistance, ReferenceZ - camera.Z);
        var layerDistance = Math.Max(NearDistance, depth - camera.Z);
        var response = Math.Clamp(referenceDistance / layerDistance, 0.01f, 100f);
        var anchorReference = Math.Max(NearDistance, ReferenceZ - anchor.Z);
        var anchorLayer = Math.Max(NearDistance, depth - anchor.Z);
        var anchorResponse = Math.Clamp(anchorReference / anchorLayer, 0.01f, 100f);
        var baselineScale = KeepSize ? 1f : anchorResponse;
        var scale = DollyScaling ? baselineScale * response / anchorResponse : baselineScale;
        scale = Math.Clamp(float.Lerp(1f, scale, Strength), 0.01f, 100f);
        return Matrix4x4.CreateScale(scale, scale, 1f)
            * Matrix4x4.CreateTranslation(anchor.X * (1f - scale)
                + (camera.X - anchor.X) * (1f - response) * Strength * HorizontalResponse,
                anchor.Y * (1f - scale)
                + (camera.Y - anchor.Y) * (1f - response) * Strength * VerticalResponse, 0);
    }

    private static bool InRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static float Normalize(float value, float fallback, float minimum, float maximum) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);
}
