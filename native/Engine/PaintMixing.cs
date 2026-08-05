namespace VectorAnimationEngine;

public enum BrushMixingMode : byte
{
    Optical,
    Pigment
}

public readonly record struct MixingBrushSettings(
    BrushMixingMode Mode,
    float Strength,
    float Viscosity,
    float PaintLoad,
    float Influence)
{
    public MixingBrushSettings(
        BrushMixingMode Mode,
        float Strength,
        float Viscosity,
        float PaintLoad)
        : this(Mode, Strength, Viscosity, PaintLoad, 0f)
    {
    }

    public BrushMixingMode Mode { get; init; } = Enum.IsDefined(Mode)
        ? Mode
        : BrushMixingMode.Optical;
    public float Strength { get; init; } = Clamp01(Strength);
    public float Viscosity { get; init; } = Clamp01(Viscosity);
    public float PaintLoad { get; init; } = Clamp01(PaintLoad);
    public float Influence { get; init; } = Clamp01(Influence);

    private static float Clamp01(float value) => float.IsFinite(value)
        ? Math.Clamp(value, 0f, 1f)
        : 0f;
}

public readonly record struct MixingBrushColorSample(
    PointF Point,
    float NormalizedOffset,
    int Argb);

public readonly record struct MixingBrushTrajectorySample(
    PointF Point,
    float Diameter,
    int Argb);

internal readonly record struct PigmentMixState(
    float Band0,
    float Band1,
    float Band2,
    float Band3,
    float Band4,
    float Band5,
    float Band6,
    float Neutral)
{
    public static PigmentMixState Blend(PigmentMixState carried, PigmentMixState surface, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new PigmentMixState(
            Lerp(carried.Band0, surface.Band0, amount),
            Lerp(carried.Band1, surface.Band1, amount),
            Lerp(carried.Band2, surface.Band2, amount),
            Lerp(carried.Band3, surface.Band3, amount),
            Lerp(carried.Band4, surface.Band4, amount),
            Lerp(carried.Band5, surface.Band5, amount),
            Lerp(carried.Band6, surface.Band6, amount),
            Lerp(carried.Neutral, surface.Neutral, amount));
    }

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;
}

public static class PaintColorMixer
{
    private const float MinimumReflectance = 0.015f;

    // Inverse response of the RGB observer below to the fixed red, green, and
    // blue reflectance curves. This removes display-channel cross-talk without
    // changing the spectra used by the pigment mixture itself.
    private const float RedFromRed = 1.10399115f;
    private const float RedFromGreen = -0.19968441f;
    private const float RedFromBlue = 0.01460042f;
    private const float GreenFromRed = -0.16230571f;
    private const float GreenFromGreen = 1.16029861f;
    private const float GreenFromBlue = -0.22479682f;
    private const float BlueFromRed = -0.02406724f;
    private const float BlueFromGreen = -0.08104696f;
    private const float BlueFromBlue = 1.09524322f;

    // Fixed reflectance samples from violet to deep red. They are deliberately
    // broad so complementary paint colors retain their overlapping wavelength
    // band instead of collapsing to an RGB-channel average.
    private static readonly float[] RedReflectance = [0.03f, 0.03f, 0.04f, 0.08f, 0.72f, 1f, 0.92f];
    private static readonly float[] GreenReflectance = [0.02f, 0.06f, 0.62f, 1f, 0.74f, 0.08f, 0.03f];
    private static readonly float[] BlueReflectance = [0.88f, 1f, 0.62f, 0.08f, 0.025f, 0.02f, 0.02f];

    public static Color Mix(Color carried, Color surface, float amount, BrushMixingMode mode)
    {
        amount = Clamp01(amount);
        if (amount <= 0f || surface.A == 0) return carried;
        if (amount >= 1f) return surface;

        var alpha = LerpByte(carried.A, surface.A, amount);
        return mode == BrushMixingMode.Pigment
            ? MixPigment(carried, surface, amount, alpha)
            : MixOptical(carried, surface, amount, alpha);
    }

    private static Color MixOptical(Color carried, Color surface, float amount, int alpha)
    {
        var red = LinearToSrgb(Lerp(SrgbToLinear(carried.R), SrgbToLinear(surface.R), amount));
        var green = LinearToSrgb(Lerp(SrgbToLinear(carried.G), SrgbToLinear(surface.G), amount));
        var blue = LinearToSrgb(Lerp(SrgbToLinear(carried.B), SrgbToLinear(surface.B), amount));
        return Color.FromArgb(alpha, ToByte(red), ToByte(green), ToByte(blue));
    }

    private static Color MixPigment(Color carried, Color surface, float amount, int alpha)
    {
        var mixed = PigmentMixState.Blend(
            CreatePigmentState(carried),
            CreatePigmentState(surface),
            amount);
        return PigmentStateToColor(mixed, alpha);
    }

    internal static PigmentMixState CreatePigmentState(Color color)
    {
        var red = SrgbToLinear(color.R);
        var green = SrgbToLinear(color.G);
        var blue = SrgbToLinear(color.B);
        var white = MathF.Min(red, MathF.Min(green, blue));
        var chromaScale = Math.Max(0.0001f, 1f - white);
        var chromaticRed = Math.Clamp((red - white) / chromaScale, 0f, 1f);
        var chromaticGreen = Math.Clamp((green - white) / chromaScale, 0f, 1f);
        var chromaticBlue = Math.Clamp((blue - white) / chromaScale, 0f, 1f);

        float AbsorptionAt(int band)
        {
            var chromaticReflectance = MathF.Max(
                chromaticRed * RedReflectance[band],
                MathF.Max(
                    chromaticGreen * GreenReflectance[band],
                    chromaticBlue * BlueReflectance[band]));
            var reflectance = white + (1f - white) * chromaticReflectance;
            reflectance = MinimumReflectance + (1f - MinimumReflectance) * reflectance;
            return ReflectanceToAbsorptionRatio(reflectance);
        }

        return new PigmentMixState(
            AbsorptionAt(0),
            AbsorptionAt(1),
            AbsorptionAt(2),
            AbsorptionAt(3),
            AbsorptionAt(4),
            AbsorptionAt(5),
            AbsorptionAt(6),
            white);
    }

    internal static Color PigmentStateToColor(PigmentMixState state, int alpha)
    {
        var band0 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band0));
        var band1 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band1));
        var band2 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band2));
        var band3 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band3));
        var band4 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band4));
        var band5 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band5));
        var band6 = RemoveReflectanceFloor(AbsorptionRatioToReflectance(state.Band6));
        var observedRed = band4 * 0.15f + band5 * 0.5f + band6 * 0.35f;
        var observedGreen = band2 * 0.2f + band3 * 0.7f + band4 * 0.1f;
        var observedBlue = band0 * 0.45f + band1 * 0.5f + band2 * 0.05f;

        var neutral = Math.Clamp(state.Neutral, 0f, 1f);
        var chromaScale = 1f - neutral;
        var chromaticRed = chromaScale > 0.0001f
            ? (observedRed - neutral) / chromaScale
            : 0f;
        var chromaticGreen = chromaScale > 0.0001f
            ? (observedGreen - neutral) / chromaScale
            : 0f;
        var chromaticBlue = chromaScale > 0.0001f
            ? (observedBlue - neutral) / chromaScale
            : 0f;
        var linearRed = CalibratedChannel(
            neutral,
            chromaScale,
            chromaticRed * RedFromRed
                + chromaticGreen * RedFromGreen
                + chromaticBlue * RedFromBlue);
        var linearGreen = CalibratedChannel(
            neutral,
            chromaScale,
            chromaticRed * GreenFromRed
                + chromaticGreen * GreenFromGreen
                + chromaticBlue * GreenFromBlue);
        var linearBlue = CalibratedChannel(
            neutral,
            chromaScale,
            chromaticRed * BlueFromRed
                + chromaticGreen * BlueFromGreen
                + chromaticBlue * BlueFromBlue);
        return Color.FromArgb(
            Math.Clamp(alpha, 0, 255),
            ToByte(LinearToSrgb(linearRed)),
            ToByte(LinearToSrgb(linearGreen)),
            ToByte(LinearToSrgb(linearBlue)));
    }

    private static float ReflectanceToAbsorptionRatio(float reflectance)
    {
        reflectance = Math.Clamp(reflectance, MinimumReflectance, 1f);
        var loss = 1f - reflectance;
        return loss * loss / (2f * reflectance);
    }

    private static float AbsorptionRatioToReflectance(float ratio)
    {
        ratio = Math.Max(0f, ratio);
        return Math.Clamp(1f + ratio - MathF.Sqrt(ratio * ratio + 2f * ratio), 0f, 1f);
    }

    private static float RemoveReflectanceFloor(float reflectance) =>
        Math.Clamp((reflectance - MinimumReflectance) / (1f - MinimumReflectance), 0f, 1f);

    private static float CalibratedChannel(float neutral, float chromaScale, float chromatic) =>
        Math.Clamp(neutral + chromaScale * chromatic, 0f, 1f);

    public static Color CompositeSourceOver(Color backdrop, Color source)
    {
        var sourceAlpha = source.A / 255f;
        var backdropAlpha = backdrop.A / 255f;
        var outputAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
        if (outputAlpha <= 0.0001f) return Color.Transparent;

        var backdropWeight = backdropAlpha * (1f - sourceAlpha);
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round(outputAlpha * 255f), 0, 255),
            CompositeChannel(source.R, sourceAlpha, backdrop.R, backdropWeight, outputAlpha),
            CompositeChannel(source.G, sourceAlpha, backdrop.G, backdropWeight, outputAlpha),
            CompositeChannel(source.B, sourceAlpha, backdrop.B, backdropWeight, outputAlpha));
    }

    private static int CompositeChannel(
        byte source,
        float sourceWeight,
        byte backdrop,
        float backdropWeight,
        float outputAlpha) =>
        Math.Clamp(
            (int)MathF.Round((source * sourceWeight + backdrop * backdropWeight) / outputAlpha),
            0,
            255);

    private static float SrgbToLinear(byte channel)
    {
        var value = channel / 255f;
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;

    private static int LerpByte(byte start, byte end, float amount) =>
        Math.Clamp((int)MathF.Round(Lerp(start, end, amount)), 0, 255);

    private static int ToByte(float value) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static float Clamp01(float value) => float.IsFinite(value)
        ? Math.Clamp(value, 0f, 1f)
        : 0f;
}

public sealed class MixingBrushRuntime
{
    private const double MinimumSegmentLength = 0.000001d;

    private readonly Func<PointF, Color?>? _surfaceSampler;
    private readonly List<double>? _sampleDistances;
    private readonly PigmentMixState _loadedPigment;
    private Color _carriedColor;
    private PigmentMixState _carriedPigment;
    private PigmentMixState _cachedSurfacePigment;
    private int _cachedSurfaceArgb;
    private PointF _lastInputPoint;
    private float _lastInputDiameter;
    private double _totalDistance;
    private double _nextSampleDistance;
    private double _lastOutputDistance;
    private bool _hasInput;
    private bool _hasOutput;

    public MixingBrushRuntime(
        Color loadedColor,
        MixingBrushSettings settings,
        float samplingDistance,
        Func<PointF, Color?>? surfaceSampler,
        bool trackSampleDistances = false)
    {
        LoadedColor = loadedColor;
        Settings = new MixingBrushSettings(
            settings.Mode,
            settings.Strength,
            settings.Viscosity,
            settings.PaintLoad,
            settings.Influence);
        SamplingDistance = float.IsFinite(samplingDistance) && samplingDistance > 0.0001f
            ? samplingDistance
            : 1f;
        _surfaceSampler = surfaceSampler;
        _carriedColor = loadedColor;
        _loadedPigment = PaintColorMixer.CreatePigmentState(loadedColor);
        _carriedPigment = _loadedPigment;
        _sampleDistances = trackSampleDistances ? [] : null;
        _nextSampleDistance = SamplingDistance;
    }

    public Color LoadedColor { get; }
    public MixingBrushSettings Settings { get; }
    public float SamplingDistance { get; }
    public Color CarriedColor => _carriedColor;
    public bool IsComplete { get; private set; }

    internal IReadOnlyList<double> SampleDistances => _sampleDistances is not null
        ? _sampleDistances
        : Array.Empty<double>();
    internal double TotalDistance => _totalDistance;

    public MixingBrushTrajectorySample[] Append(PointF point, float diameter)
    {
        var emitted = new List<MixingBrushTrajectorySample>();
        AppendTo(point, diameter, emitted);
        return emitted.ToArray();
    }

    public void AppendTo(
        PointF point,
        float diameter,
        List<MixingBrushTrajectorySample> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (IsComplete)
        {
            throw new InvalidOperationException("Cannot append to a completed mixing-brush runtime.");
        }

        if (!IsFinite(point)) return;
        diameter = NormalizeDiameter(diameter, _hasInput ? _lastInputDiameter : 1f);

        if (!_hasInput)
        {
            _hasInput = true;
            _lastInputPoint = point;
            _lastInputDiameter = diameter;
            destination.Add(Emit(point, diameter, 0d, SamplingDistance));
            return;
        }

        var startPoint = _lastInputPoint;
        var startDiameter = _lastInputDiameter;
        var segmentLength = Distance(startPoint, point);
        _lastInputPoint = point;
        _lastInputDiameter = diameter;
        if (!double.IsFinite(segmentLength) || segmentLength <= MinimumSegmentLength) return;

        var segmentStartDistance = _totalDistance;
        var segmentEndDistance = segmentStartDistance + segmentLength;
        var tolerance = DistanceTolerance();
        while (_nextSampleDistance <= segmentEndDistance + tolerance)
        {
            var sampleDistance = Math.Min(_nextSampleDistance, segmentEndDistance);
            var amount = Math.Clamp(
                (_nextSampleDistance - segmentStartDistance) / segmentLength,
                0d,
                1d);
            var samplePoint = Lerp(startPoint, point, amount);
            var sampleDiameter = Lerp(startDiameter, diameter, amount);
            destination.Add(Emit(samplePoint, sampleDiameter, sampleDistance, SamplingDistance));
            _nextSampleDistance += SamplingDistance;
        }

        _totalDistance = segmentEndDistance;
    }

    public MixingBrushTrajectorySample[] Complete()
    {
        var emitted = new List<MixingBrushTrajectorySample>(1);
        CompleteTo(emitted);
        return emitted.ToArray();
    }

    public void CompleteTo(List<MixingBrushTrajectorySample> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (IsComplete) return;
        IsComplete = true;
        if (!_hasInput || !_hasOutput) return;

        var remainingDistance = _totalDistance - _lastOutputDistance;
        if (remainingDistance <= 0d) return;

        destination.Add(Emit(
            _lastInputPoint,
            _lastInputDiameter,
            _totalDistance,
            remainingDistance));
    }

    private MixingBrushTrajectorySample Emit(
        PointF point,
        float diameter,
        double distance,
        double travelledDistance)
    {
        AdvanceColor(point, travelledDistance);
        _hasOutput = true;
        _lastOutputDistance = distance;
        _sampleDistances?.Add(distance);
        return new MixingBrushTrajectorySample(point, diameter, DepositColor().ToArgb());
    }

    private void AdvanceColor(PointF point, double travelledDistance)
    {
        if (travelledDistance <= 0d) return;

        var surface = _surfaceSampler?.Invoke(point);
        if (surface is { A: > 0 } sampledSurface)
        {
            if (Settings.Strength <= 0f) return;

            var pickupRate = Settings.Strength
                * Lerp(4f, 0.35f, Settings.Viscosity)
                * Lerp(1f, 0.1f, Settings.PaintLoad);
            var pickupAmount = ExponentialResponse(travelledDistance, pickupRate)
                * (sampledSurface.A / 255f);
            if (Settings.Mode == BrushMixingMode.Pigment)
            {
                var surfaceArgb = sampledSurface.ToArgb();
                if (surfaceArgb != _cachedSurfaceArgb)
                {
                    _cachedSurfaceArgb = surfaceArgb;
                    _cachedSurfacePigment = PaintColorMixer.CreatePigmentState(sampledSurface);
                }
                _carriedPigment = PigmentMixState.Blend(
                    _carriedPigment,
                    _cachedSurfacePigment,
                    pickupAmount);
                _carriedColor = PaintColorMixer.PigmentStateToColor(_carriedPigment, _carriedColor.A);
            }
            else
            {
                var opaqueSurface = Color.FromArgb(
                    _carriedColor.A,
                    sampledSurface.R,
                    sampledSurface.G,
                    sampledSurface.B);
                _carriedColor = PaintColorMixer.Mix(
                    _carriedColor,
                    opaqueSurface,
                    pickupAmount,
                    BrushMixingMode.Optical);
            }
            return;
        }

        if (Settings.Influence <= 0f) return;

        var recoveryRate = Settings.Influence
            * Lerp(0.25f, 1.5f, Settings.PaintLoad)
            * Lerp(1f, 0.35f, Settings.Viscosity);
        var recoveryAmount = ExponentialResponse(travelledDistance, recoveryRate);
        if (Settings.Mode == BrushMixingMode.Pigment)
        {
            _carriedPigment = PigmentMixState.Blend(_carriedPigment, _loadedPigment, recoveryAmount);
            _carriedColor = PaintColorMixer.PigmentStateToColor(_carriedPigment, _carriedColor.A);
        }
        else
        {
            _carriedColor = PaintColorMixer.Mix(
                _carriedColor,
                LoadedColor,
                recoveryAmount,
                BrushMixingMode.Optical);
        }
    }

    private Color DepositColor()
    {
        if (Settings.Mode != BrushMixingMode.Optical || _carriedColor.A == 0) return _carriedColor;
        // Coverage remains translucent so repeated trajectory passes can build density.
        var coverage = Lerp(0.12f, 0.28f, Settings.PaintLoad);
        var alpha = Math.Max(1, (int)MathF.Round(_carriedColor.A * coverage));
        return Color.FromArgb(alpha, _carriedColor.R, _carriedColor.G, _carriedColor.B);
    }

    private float ExponentialResponse(double travelledDistance, float rate)
    {
        if (rate <= 0f) return 0f;
        var exponent = -travelledDistance / SamplingDistance * rate;
        return (float)Math.Clamp(1d - Math.Exp(exponent), 0d, 1d);
    }

    private double DistanceTolerance() =>
        Math.Max(0.000000001d, SamplingDistance * 0.0000001d);

    private static bool IsFinite(PointF point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static float NormalizeDiameter(float diameter, float fallback) =>
        float.IsFinite(diameter) ? Math.Max(0f, diameter) : fallback;

    private static double Distance(PointF start, PointF end)
    {
        var dx = (double)end.X - start.X;
        var dy = (double)end.Y - start.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PointF Lerp(PointF start, PointF end, double amount) => new(
        (float)(start.X + (end.X - (double)start.X) * amount),
        (float)(start.Y + (end.Y - (double)start.Y) * amount));

    private static float Lerp(float start, float end, double amount) =>
        (float)(start + (end - (double)start) * amount);

    private static float Lerp(float start, float end, float amount) =>
        start + (end - start) * amount;
}

public static class MixingBrushProcessor
{
    private const double MinimumPathLength = 0.0001d;

    // Kept for source compatibility with legacy callers. Incremental trajectories are unbounded.
    public const int MaximumColorSamples = 48;

    public static MixingBrushRuntime CreateRuntime(
        Color loadedColor,
        MixingBrushSettings settings,
        float samplingDistance,
        Func<PointF, Color?>? surfaceSampler) =>
        new(loadedColor, settings, samplingDistance, surfaceSampler);

    public static MixingBrushColorSample[] CreateColorProfile(
        IReadOnlyList<PointF> path,
        Color loadedColor,
        MixingBrushSettings settings,
        Func<PointF, Color?>? surfaceSampler,
        float samplingDistance = 1f)
    {
        ArgumentNullException.ThrowIfNull(path);
        var runtime = new MixingBrushRuntime(
            loadedColor,
            settings,
            samplingDistance,
            surfaceSampler,
            trackSampleDistances: true);
        var trajectory = new List<MixingBrushTrajectorySample>();
        foreach (var point in path)
        {
            runtime.AppendTo(point, 1f, trajectory);
        }
        runtime.CompleteTo(trajectory);
        if (trajectory.Count == 0) return [];

        var result = new MixingBrushColorSample[trajectory.Count];
        for (var index = 0; index < trajectory.Count; index++)
        {
            var normalizedOffset = runtime.TotalDistance <= MinimumPathLength
                ? 0f
                : (float)Math.Clamp(
                    runtime.SampleDistances[index] / runtime.TotalDistance,
                    0d,
                    1d);
            var sample = trajectory[index];
            result[index] = new MixingBrushColorSample(
                sample.Point,
                normalizedOffset,
                sample.Argb);
        }

        return result;
    }
}
