namespace VectorAnimationEngine;

internal readonly record struct RenderStats(
    int VisibleObjects,
    int DrawnObjects,
    long VisibleAtoms,
    int TileDraws,
    int ScannedObjects,
    bool TileLod);
