namespace VectorAnimationEngine;

internal enum GradientKind : byte
{
    Solid,
    Linear,
    Radial
}

internal readonly record struct GradientStop(float Position, int Argb)
{
    public GradientStop(float position, Color color) : this(position, color.ToArgb())
    {
    }
}
