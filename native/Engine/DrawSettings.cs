namespace VectorAnimationEngine;

internal sealed class DrawSettings
{
    private float _gridSize = 128;
    private float _angleSnapDegrees = 15;
    private int _polygonSides = 6;
    private int _starPoints = 5;

    public event EventHandler? Changed;

    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public bool SnapEnabled { get; set; }
    public bool SnapToGrid { get; set; }
    public bool SnapToObjects { get; set; }
    public bool AdhesionEnabled { get; set; }
    public bool AlignmentEnabled { get; set; }
    public bool AngleSnapEnabled { get; set; }
    public bool KeepAspectRatio { get; set; }

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

    public PointF SnapPoint(PointF point)
    {
        if (!SnapEnabled || !SnapToGrid) return point;
        return new PointF(SnapValue(point.X, GridSize), SnapValue(point.Y, GridSize));
    }

    public SizeF ApplyAspectRatio(SizeF size)
    {
        if (!KeepAspectRatio) return size;
        var side = Math.Max(Math.Abs(size.Width), Math.Abs(size.Height));
        return new SizeF(MathF.CopySign(side, size.Width), MathF.CopySign(side, size.Height));
    }

    public float SnapAngle(float radians)
    {
        if (!SnapEnabled || !AngleSnapEnabled) return radians;
        var degrees = radians * 57.29578f;
        return SnapValue(degrees, AngleSnapDegrees) * 0.017453292f;
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static float SnapValue(float value, float step)
    {
        if (step <= 0) return value;
        return MathF.Round(value / step) * step;
    }
}
