namespace VectorAnimationEngine;

// Tool defaults, independent of the ordinary brush's imported/traditional tip.
internal readonly record struct PressureBrushOptions(
    int Stabilizer = 0, int Smoothing = 100, int Roundness = 100,
    int Angle = 90, int Taper = 0, int Velocity = 0, int Pressure = 100)
{
    public static PressureBrushOptions Default => new(0, 100, 100, 90, 0, 0, 100);

    public PressureBrushOptions Clamped() => new(
        Math.Clamp(Stabilizer, 0, 100), Math.Clamp(Smoothing, 0, 100),
        Math.Clamp(Roundness, 1, 100), Math.Clamp(Angle, 0, 180),
        Math.Clamp(Taper, 0, 100), Math.Clamp(Velocity, -100, 100), Math.Clamp(Pressure, 0, 100));

    public float Diameter(float size, PressureBrushSample sample, float travelled)
    {
        var options = Clamped();
        var pressure = float.IsFinite(sample.TabletPressure) ? Math.Clamp(sample.TabletPressure, 0, 1) : 1;
        var pressureScale = 1 - options.Pressure / 100f * (1 - pressure);
        var speed = Math.Clamp(sample.SpeedPixelsPerSecond / 1800f, 0, 1);
        var velocityScale = 1 - options.Velocity / 100f * speed;
        // A distance-based leading taper keeps previously drawn samples stable as the stroke grows.
        var taperScale = options.Taper == 0 ? 1 : Math.Clamp(
            travelled / Math.Max(VectorUnits.MinimumStrokeUnits, size * options.Taper / 25f), 0, 1);
        return Math.Max(VectorUnits.MinimumStrokeUnits * 0.6f, size * pressureScale * velocityScale * taperScale);
    }

    public PointF Stabilize(PointF previous, PointF target, float worldPerPixel)
    {
        var radius = Clamped().Stabilizer * Math.Max(0.0001f, worldPerPixel) * 0.25f;
        var dx = target.X - previous.X;
        var dy = target.Y - previous.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var amount = distance > radius ? (distance - radius) / distance : 0;
        return radius == 0 ? target : new(previous.X + dx * amount, previous.Y + dy * amount);
    }

    public int ApplyEndTaper(PressureBrushPoint[] profile, float size)
    {
        if (Taper <= 0 || profile.Length < 2) return profile.Length;
        var range = Math.Max(VectorUnits.MinimumStrokeUnits, size * Math.Clamp(Taper, 0, 100) / 25f);
        var remaining = 0f;
        var first = profile.Length;
        for (var index = profile.Length - 1; index >= 0; index--)
        {
            if (index < profile.Length - 1)
            {
                var dx = profile[index + 1].Point.X - profile[index].Point.X;
                var dy = profile[index + 1].Point.Y - profile[index].Point.Y;
                remaining += MathF.Sqrt(dx * dx + dy * dy);
            }
            if (remaining >= range) break;
            first = index;
            profile[index] = profile[index] with { Diameter = VectorUnits.Quantize(Math.Max(
                VectorUnits.MinimumStrokeUnits * 0.6f, profile[index].Diameter * remaining / range)) };
        }
        return first;
    }
}
