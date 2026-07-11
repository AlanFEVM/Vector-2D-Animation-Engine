namespace VectorAnimationEngine;

internal readonly record struct RenderStats(
    int VisibleObjects,
    int DrawnObjects,
    long VisibleAtoms,
    int TileDraws,
    int ScannedObjects,
    bool TileLod)
{
    public static RenderStats Combine(RenderStats first, RenderStats second)
    {
        return new RenderStats(
            first.VisibleObjects + second.VisibleObjects,
            first.DrawnObjects + second.DrawnObjects,
            first.VisibleAtoms + second.VisibleAtoms,
            first.TileDraws + second.TileDraws,
            first.ScannedObjects + second.ScannedObjects,
            first.TileLod || second.TileLod);
    }
}
