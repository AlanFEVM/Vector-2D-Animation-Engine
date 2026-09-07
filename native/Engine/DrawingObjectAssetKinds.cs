namespace VectorAnimationEngine;

internal static class DrawingObjectAssetKinds
{
    public const string Basic = "Symbol";
    public const string ThreeDimensional = "3D Symbol";

    public static bool IsThreeDimensional(string? kind) =>
        string.Equals(kind, ThreeDimensional, StringComparison.Ordinal);
}
