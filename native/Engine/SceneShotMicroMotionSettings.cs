using System.Text.Json.Serialization;

namespace VectorAnimationEngine;

internal readonly record struct SceneShotMicroMotionSettings
{
    [JsonConstructor]
    public SceneShotMicroMotionSettings() { }

    public bool Enabled { get; init; }
    public float HorizontalPercent { get; init; } = 0.25f;
    public float VerticalPercent { get; init; } = 0.25f;
    public float RotationDegrees { get; init; } = 0.15f;
    public float Speed { get; init; } = 0.8f;
    public int Seed { get; init; } = 1;
    public bool HandheldDefaultsInitialized { get; init; }

    internal bool IsValid => InRange(HorizontalPercent, 0, 5)
        && InRange(VerticalPercent, 0, 5) && InRange(RotationDegrees, 0, 5)
        && InRange(Speed, 0.01f, 20) && Seed is >= 0 and <= 999999;

    internal SceneShotMicroMotionSettings Normalized() => this with
    {
        HorizontalPercent = Normalize(HorizontalPercent, 0.25f, 0, 5),
        VerticalPercent = Normalize(VerticalPercent, 0.25f, 0, 5),
        RotationDegrees = Normalize(RotationDegrees, 0.15f, 0, 5),
        Speed = Normalize(Speed, 0.8f, 0.01f, 20),
        Seed = Math.Clamp(Seed, 0, 999999)
    };

    internal SceneShotMicroMotionSettings WithHandheldDefaults(SceneShotSettings initialCamera,
        float canvasWidth, float canvasHeight, bool force = false)
    {
        if (!force && (HandheldDefaultsInitialized
            || (this with { Enabled = false }) != new SceneShotMicroMotionSettings())) return this;
        canvasWidth = float.IsFinite(canvasWidth) && canvasWidth > 0 ? canvasWidth : 48000;
        canvasHeight = float.IsFinite(canvasHeight) && canvasHeight > 0 ? canvasHeight : 28000;
        var frame = initialCamera.ResolveWorldFrame(canvasWidth, canvasHeight);
        var frameWidth = frame.HalfWidth * 2;
        var frameHeight = frame.HalfHeight * 2;
        var shortEdge = Math.Min(frameWidth, frameHeight);
        var framingScale = Math.Clamp(MathF.Sqrt(Math.Min(canvasWidth, canvasHeight) / shortEdge), 0.7f, 1.6f);
        var drift = shortEdge * 0.008f * framingScale;
        return (this with
        {
            HorizontalPercent = MathF.Round(Math.Clamp(drift / frameWidth * 100, 0.1f, 1.5f), 2),
            VerticalPercent = MathF.Round(Math.Clamp(drift * 0.75f / frameHeight * 100, 0.1f, 1.5f), 2),
            RotationDegrees = MathF.Round(0.16f * framingScale, 2),
            Speed = 0.65f,
            HandheldDefaultsInitialized = true
        }).Normalized();
    }

    internal SceneShotSettings ApplyTo(SceneShotSettings camera, double timeSeconds,
        float canvasWidth, float canvasHeight)
    {
        if (!Enabled || !double.IsFinite(timeSeconds)
            || HorizontalPercent == 0 && VerticalPercent == 0 && RotationDegrees == 0) return camera;
        var motion = Normalized();
        var time = Math.Clamp(timeSeconds, 0, int.MaxValue) * motion.Speed;
        var frame = camera.ResolveWorldFrame(canvasWidth, canvasHeight);
        var horizontal = Sample(time, motion.Seed, 0) * frame.HalfWidth * 2 * motion.HorizontalPercent / 100;
        var vertical = Sample(time, motion.Seed, 1) * frame.HalfHeight * 2 * motion.VerticalPercent / 100;
        var position = camera.Position + frame.Right * horizontal - frame.Up * vertical;
        return (camera with
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            RotationDegrees = camera.RotationDegrees + Sample(time, motion.Seed, 2) * motion.RotationDegrees
        }).Normalized();
    }

    private static float Sample(double time, int seed, int channel) =>
        (float)(Noise(time, seed, channel) * 0.7 + Noise(time * 2.17 + 11.3, seed, channel + 3) * 0.3);

    private static double Noise(double time, int seed, int channel)
    {
        var cell = (long)Math.Floor(time);
        var fraction = time - cell;
        var blend = fraction * fraction * fraction * (fraction * (fraction * 6 - 15) + 10);
        var source = Hash(cell, seed, channel);
        return source + (Hash(cell + 1, seed, channel) - source) * blend;
    }

    private static double Hash(long cell, int seed, int channel)
    {
        unchecked
        {
            var value = (uint)cell ^ ((uint)seed + 1) * 0x9e3779b9u ^ ((uint)channel + 1) * 0x85ebca6bu;
            value = (value ^ (value >> 16)) * 0x7feb352du;
            value = (value ^ (value >> 15)) * 0x846ca68bu;
            value ^= value >> 16;
            return value / (double)uint.MaxValue * 2 - 1;
        }
    }

    private static bool InRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static float Normalize(float value, float fallback, float minimum, float maximum) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);
}
