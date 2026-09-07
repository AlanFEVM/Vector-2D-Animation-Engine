using System.Numerics;

namespace VectorAnimationEngine;

internal enum SceneLightKind : byte
{
    Directional,
    Point,
    Area,
    Ambient
}

internal readonly record struct SceneLightSettings(
    bool Enabled,
    int ColorArgb,
    float Intensity,
    float Range,
    Vector3 Position,
    Vector3 RotationDegrees,
    Vector2 AreaSize,
    bool CastsShadows,
    float ShadowStrength,
    float ShadowSoftness)
{
    internal const float MaximumIntensity = 1000f;
    internal const float MaximumRange = 5_000_000f;

    public bool IsValid(SceneLightKind kind)
    {
        if (!Enum.IsDefined(kind)
            || (uint)ColorArgb >> 24 != 0xff
            || !float.IsFinite(Intensity)
            || Intensity is < 0f or > MaximumIntensity
            || !Finite(Position)
            || !Finite(RotationDegrees)
            || !Finite(AreaSize)
            || !UnitInterval(ShadowStrength)
            || !UnitInterval(ShadowSoftness)
            || kind == SceneLightKind.Ambient && CastsShadows)
        {
            return false;
        }

        if (kind is SceneLightKind.Point or SceneLightKind.Area)
        {
            if (!float.IsFinite(Range) || Range is <= 0f or > MaximumRange) return false;
        }
        else if (Range != 0f)
        {
            return false;
        }

        return kind != SceneLightKind.Area
            || AreaSize.X is > 0f and <= MaximumRange
            && AreaSize.Y is > 0f and <= MaximumRange;
    }

    internal static SceneLightSettings Interpolate(
        SceneLightKind kind,
        SceneLightSettings source,
        SceneLightSettings target,
        float progress)
    {
        if (!source.IsValid(kind) || !target.IsValid(kind))
            throw new ArgumentOutOfRangeException(nameof(source));

        progress = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 0f;
        if (progress <= 0f) return source;
        if (progress >= 1f) return target;

        return new SceneLightSettings(
            source.Enabled,
            InterpolateArgb(source.ColorArgb, target.ColorArgb, progress),
            Lerp(source.Intensity, target.Intensity, progress),
            kind is SceneLightKind.Point or SceneLightKind.Area
                ? Lerp(source.Range, target.Range, progress)
                : 0f,
            Lerp(source.Position, target.Position, progress),
            LerpRotation(source.RotationDegrees, target.RotationDegrees, progress),
            kind == SceneLightKind.Area
                ? Lerp(source.AreaSize, target.AreaSize, progress)
                : Vector2.Zero,
            source.CastsShadows,
            Lerp(source.ShadowStrength, target.ShadowStrength, progress),
            Lerp(source.ShadowSoftness, target.ShadowSoftness, progress));
    }

    private static bool UnitInterval(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 1f;

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float Lerp(float source, float target, float progress) =>
        (float)((double)source + ((double)target - source) * progress);

    private static Vector2 Lerp(Vector2 source, Vector2 target, float progress) => new(
        Lerp(source.X, target.X, progress),
        Lerp(source.Y, target.Y, progress));

    private static Vector3 Lerp(Vector3 source, Vector3 target, float progress) => new(
        Lerp(source.X, target.X, progress),
        Lerp(source.Y, target.Y, progress),
        Lerp(source.Z, target.Z, progress));

    private static Vector3 LerpRotation(Vector3 source, Vector3 target, float progress) => new(
        LerpAngle(source.X, target.X, progress),
        LerpAngle(source.Y, target.Y, progress),
        LerpAngle(source.Z, target.Z, progress));

    private static float LerpAngle(float source, float target, float progress)
    {
        var delta = ((double)target - source) % 360d;
        if (delta > 180d) delta -= 360d;
        else if (delta < -180d) delta += 360d;
        return (float)((double)source + delta * progress);
    }

    private static int InterpolateArgb(int source, int target, float progress)
    {
        static int Channel(int value, int shift) => (value >> shift) & 0xff;
        static int Blend(int left, int right, float amount) =>
            Math.Clamp((int)MathF.Round(left + (right - left) * amount), 0, 255);

        var red = Blend(Channel(source, 16), Channel(target, 16), progress);
        var green = Blend(Channel(source, 8), Channel(target, 8), progress);
        var blue = Blend(Channel(source, 0), Channel(target, 0), progress);
        return unchecked((int)0xff000000) | red << 16 | green << 8 | blue;
    }
}

internal readonly record struct SceneLightStateKeyframe(int Frame, SceneLightSettings Settings);

internal sealed class SceneLightDefinition
{
    internal const int MaximumNameLength = 80;

    private readonly List<SceneLightStateKeyframe> _stateKeyframes = [];
    private readonly IReadOnlyList<SceneLightStateKeyframe> _stateKeyframeView;

    internal SceneLightDefinition(
        string id,
        string name,
        SceneLightKind kind,
        SceneLightSettings settings)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A light ID is required.", nameof(id));
        if (!TryNormalizeName(name, out var normalizedName)) throw new ArgumentException("A light name is required.", nameof(name));
        if (!Enum.IsDefined(kind) || !settings.IsValid(kind)) throw new ArgumentOutOfRangeException(nameof(settings));
        Id = id;
        Name = normalizedName;
        Kind = kind;
        Settings = settings;
        _stateKeyframeView = _stateKeyframes.AsReadOnly();
    }

    public string Id { get; }
    public string Name { get; private set; }
    public SceneLightKind Kind { get; }
    public SceneLightSettings Settings { get; private set; }
    public IReadOnlyList<SceneLightStateKeyframe> StateKeyframes => _stateKeyframeView;

    internal SceneLightDefinition Clone()
    {
        var clone = new SceneLightDefinition(Id, Name, Kind, Settings);
        clone.RestoreStateKeyframes(StateKeyframes);
        return clone;
    }

    internal bool TryApply(string? name, SceneLightSettings settings) =>
        TryApplyAtFrame(name, settings, 0);

    internal bool TryApplyAtFrame(string? name, SceneLightSettings settings, int frame)
    {
        if (frame < 0
            || !TryNormalizeName(name, out var normalizedName)
            || !settings.IsValid(Kind))
        {
            return false;
        }

        var nameChanged = !string.Equals(Name, normalizedName, StringComparison.Ordinal);
        var settingsChanged = EvaluateSettings(frame) != settings;
        if (!nameChanged && !settingsChanged) return false;
        Name = normalizedName;
        if (settingsChanged) SetSettingsAtFrame(frame, settings);
        return true;
    }

    public SceneLightSettings EvaluateSettings(int frame)
    {
        frame = Math.Max(0, frame);
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) index--;
        return index >= 0 ? _stateKeyframes[index].Settings : Settings;
    }

    internal bool SetSettingsAtFrame(int frame, SceneLightSettings settings)
    {
        if (frame < 0 || !settings.IsValid(Kind)) return false;
        if (frame == 0)
        {
            if (Settings == settings) return false;
            Settings = settings;
            return true;
        }

        var index = LowerBoundStateKeyframe(frame);
        var keyframe = new SceneLightStateKeyframe(frame, settings);
        if (index < _stateKeyframes.Count && _stateKeyframes[index].Frame == frame)
        {
            if (_stateKeyframes[index] == keyframe) return false;
            _stateKeyframes[index] = keyframe;
            return true;
        }

        _stateKeyframes.Insert(index, keyframe);
        return true;
    }

    internal bool RemoveStateKeyframe(int frame)
    {
        if (frame <= 0) return false;
        var index = LowerBoundStateKeyframe(frame);
        if (index >= _stateKeyframes.Count || _stateKeyframes[index].Frame != frame) return false;
        _stateKeyframes.RemoveAt(index);
        return true;
    }

    internal void InsertStateFrames(int frame, int count)
    {
        if (frame < 0 || count <= 0) return;
        for (var index = _stateKeyframes.Count - 1; index >= 0; index--)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame <= frame) break;
            _stateKeyframes[index] = keyframe with { Frame = keyframe.Frame + count };
        }
    }

    internal void RemoveStateFrames(int frame, int count)
    {
        if (frame < 0 || count <= 0 || _stateKeyframes.Count == 0) return;
        var removeEnd = frame + count;
        var continuationIndex = LowerBoundStateKeyframe(removeEnd + 1) - 1;
        var preserveContinuation = continuationIndex >= 0
            && _stateKeyframes[continuationIndex].Frame >= frame
            && _stateKeyframes[continuationIndex].Frame < removeEnd;
        var continuation = preserveContinuation ? _stateKeyframes[continuationIndex].Settings : default;

        for (var index = _stateKeyframes.Count - 1; index >= 0; index--)
        {
            var keyframe = _stateKeyframes[index];
            if (keyframe.Frame >= removeEnd)
            {
                _stateKeyframes[index] = keyframe with { Frame = keyframe.Frame - count };
            }
            else if (keyframe.Frame >= frame)
            {
                _stateKeyframes.RemoveAt(index);
            }
        }

        if (_stateKeyframes.Count > 0 && _stateKeyframes[0].Frame == 0)
        {
            Settings = _stateKeyframes[0].Settings;
            _stateKeyframes.RemoveAt(0);
        }

        if (preserveContinuation) SetSettingsAtFrame(frame, continuation);
    }

    internal void RestoreStateKeyframes(IEnumerable<SceneLightStateKeyframe>? keyframes)
    {
        _stateKeyframes.Clear();
        if (keyframes is null) return;
        foreach (var keyframe in keyframes
                     .Where(item => item.Frame >= 0 && item.Settings.IsValid(Kind))
                     .GroupBy(item => item.Frame)
                     .Select(group => group.Last())
                     .OrderBy(item => item.Frame))
        {
            if (keyframe.Frame == 0) Settings = keyframe.Settings;
            else _stateKeyframes.Add(keyframe);
        }
    }

    internal void RestoreFrom(SceneLightDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Id, source.Id, StringComparison.Ordinal) || Kind != source.Kind)
            throw new InvalidOperationException("The light snapshot does not match its target.");
        Name = source.Name;
        Settings = source.Settings;
        RestoreStateKeyframes(source.StateKeyframes);
    }

    internal static SceneLightDefinition CreateDefaultDirectional() => new(
        Guid.NewGuid().ToString("N"),
        "Key Light",
        SceneLightKind.Directional,
        new SceneLightSettings(
            Enabled: true,
            ColorArgb: unchecked((int)0xffffffff),
            Intensity: 1f,
            Range: 0f,
            Position: Vector3.Zero,
            RotationDegrees: new Vector3(-35f, -35f, 0f),
            AreaSize: Vector2.Zero,
            CastsShadows: true,
            ShadowStrength: 1f,
            ShadowSoftness: 0.15f));

    internal static SceneLightDefinition CreateDefaultAmbient() => new(
        Guid.NewGuid().ToString("N"),
        "Environment Light",
        SceneLightKind.Ambient,
        new SceneLightSettings(
            Enabled: true,
            ColorArgb: unchecked((int)0xffffffff),
            Intensity: 0.5f,
            Range: 0f,
            Position: Vector3.Zero,
            RotationDegrees: Vector3.Zero,
            AreaSize: Vector2.Zero,
            CastsShadows: false,
            ShadowStrength: 0f,
            ShadowSoftness: 0f));

    internal static SceneLightDefinition CreateDefault(SceneLightKind kind)
    {
        return kind switch
        {
            SceneLightKind.Directional => CreateDefaultDirectional(),
            SceneLightKind.Ambient => CreateDefaultAmbient(),
            SceneLightKind.Point => new SceneLightDefinition(
                Guid.NewGuid().ToString("N"),
                "Point Light",
                kind,
                new SceneLightSettings(
                    true,
                    unchecked((int)0xffffffff),
                    1f,
                    4000f,
                    Vector3.Zero,
                    Vector3.Zero,
                    Vector2.Zero,
                    true,
                    1f,
                    0.15f)),
            SceneLightKind.Area => new SceneLightDefinition(
                Guid.NewGuid().ToString("N"),
                "Area Light",
                kind,
                new SceneLightSettings(
                    true,
                    unchecked((int)0xffffffff),
                    1f,
                    6000f,
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector2(1200f, 1200f),
                    true,
                    1f,
                    0.5f)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    internal static bool TryNormalizeName(string? name, out string normalized)
    {
        normalized = name?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaximumNameLength;
    }

    private int LowerBoundStateKeyframe(int frame)
    {
        var low = 0;
        var high = _stateKeyframes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_stateKeyframes[middle].Frame < frame) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}
