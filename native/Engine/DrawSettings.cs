namespace VectorAnimationEngine;

internal sealed class DrawSettings
{
    private float _gridSize = 128;
    private float _angleSnapDegrees = 15;
    private int _polygonSides = 6;
    private int _starPoints = 5;
    private int _pencilSmoothing = 52;
    private int _pressureBrushSmoothing = 70;
    private int _brushFrequency = 8;
    private MixingBrushSettings _mixingBrushSettings = new(
        BrushMixingMode.Optical,
        Strength: 0.65f,
        Viscosity: 0.45f,
        PaintLoad: 0.35f,
        Influence: 0.4f);

    public event EventHandler? Changed;

    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public bool SnapEnabled { get; set; }
    public bool SnapToGrid { get; set; }
    public bool SnapToObjects { get; set; }
    public bool AdhesionEnabled { get; set; }
    public bool AlignmentEnabled { get; set; }
    public bool SnapToGuides { get; set; } = true;
    public bool SnapToCanvas { get; set; } = true;
    public bool SnapToGaps { get; set; } = true;
    private float _snapTolerancePixels = 8;
    public float SnapTolerancePixels
    {
        get => _snapTolerancePixels;
        set => _snapTolerancePixels = float.IsFinite(value) ? Math.Clamp(value, 2, 24) : 8;
    }
    public bool AngleSnapEnabled { get; set; }
    public bool KeepAspectRatio { get; set; }
    public bool EraseLines { get; set; } = true;
    public bool EraseFills { get; set; } = true;
    public bool BrushContinuous { get; set; } = true;

    public float GridSize
    {
        get => _gridSize;
        set => _gridSize = Math.Clamp(VectorUnits.Quantize(value), 1, 10000);
    }

    public float AngleSnapDegrees
    {
        get => _angleSnapDegrees;
        set => _angleSnapDegrees = Math.Clamp(value, 0.1f, 90);
    }

    public int PolygonSides
    {
        get => _polygonSides;
        set => _polygonSides = Math.Clamp(value, 3, 64);
    }

    public int StarPoints
    {
        get => _starPoints;
        set => _starPoints = Math.Clamp(value, 3, 32);
    }

    public int PencilSmoothing
    {
        get => _pencilSmoothing;
        set => _pencilSmoothing = Math.Clamp(value, 0, 100);
    }

    public int PressureBrushSmoothing
    {
        get => _pressureBrushSmoothing;
        set => _pressureBrushSmoothing = Math.Clamp(value, 0, 100);
    }

    public int BrushFrequency
    {
        get => _brushFrequency;
        set => _brushFrequency = Math.Clamp(value, 1, 24);
    }

    public MixingBrushSettings MixingBrushSettings
    {
        get => _mixingBrushSettings;
        set => _mixingBrushSettings = new MixingBrushSettings(
            value.Mode,
            value.Strength,
            value.Viscosity,
            value.PaintLoad,
            value.Influence);
    }

    public PointF SnapPoint(PointF point, float? gridStep = null)
    {
        if (!SnapEnabled || !SnapToGrid) return point;
        var step = gridStep is { } adaptiveStep && float.IsFinite(adaptiveStep) && adaptiveStep > 0
            ? adaptiveStep
            : GridSize;
        return new PointF(SnapValue(point.X, step), SnapValue(point.Y, step));
    }

    public PointF ResolvePointSnap(
        PointF point,
        PointF? objectCandidate = null,
        bool temporarilySnapToObjects = false,
        float? gridStep = null)
    {
        if (objectCandidate is { } candidate
            && (temporarilySnapToObjects || SnapEnabled && SnapToObjects))
        {
            return candidate;
        }

        return VectorUnits.Quantize(SnapPoint(point, gridStep));
    }

    public SizeF ApplyAspectRatio(SizeF size)
    {
        if (!KeepAspectRatio) return size;
        var side = Math.Max(Math.Abs(size.Width), Math.Abs(size.Height));
        return new SizeF(MathF.CopySign(side, size.Width), MathF.CopySign(side, size.Height));
    }

    public float SnapAngle(float radians, bool temporarilyEnabled = false)
    {
        if (!temporarilyEnabled && (!SnapEnabled || !AngleSnapEnabled)) return radians;
        var degrees = radians * 57.29578f;
        return SnapValue(degrees, AngleSnapDegrees) * 0.017453292f;
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    internal float SnapTightFitAngle(float radians, float length, float tolerance)
    {
        if (!SnapEnabled || !AdhesionEnabled || !float.IsFinite(radians)
            || !float.IsFinite(length) || length <= 0 || !float.IsFinite(tolerance) || tolerance <= 0) return radians;
        var degrees = radians * 180f / MathF.PI;
        var sign = MathF.CopySign(1, degrees);
        var absolute = Math.Abs(degrees);
        ReadOnlySpan<float> preferred = [0, 15, 30, 45, 60, 90, 120, 135, 150, 165, 180];
        var best = absolute;
        var distance = float.PositiveInfinity;
        foreach (var angle in preferred)
        {
            var difference = Math.Abs(absolute - angle);
            if (difference >= distance) continue;
            best = angle;
            distance = difference;
        }
        var delta = distance * MathF.PI / 180f;
        // Both tests are needed: short segments must not acquire a wide angular
        // capture cone, and long segments must not jump many screen pixels.
        return distance <= 4 && 2 * length * MathF.Sin(delta / 2) <= tolerance
            ? sign * best * MathF.PI / 180f : radians;
    }

    private static float SnapValue(float value, float step)
    {
        if (step <= 0) return value;
        return MathF.Round(value / step) * step;
    }
}
