namespace VectorAnimationEngine;

internal sealed partial class VectorScene
{
    internal void AddChunkBlockBitmap(int layer, SceneBlockGrid grid, SceneBlockGrid.Cell cell, BitmapObjectData data)
    {
        var reserved = (long)grid.Columns * grid.Rows * grid.SectionDepth;
        _nextObjectOrder = Math.Max(_nextObjectOrder, reserved);
        var index = AddBitmapObject(layer, grid.Center(cell), data);
        // Keep translucent front cells above the cells behind them, including after edits.
        ObjectOrder[index] = reserved - grid.Encode(cell);
    }

    internal void UpdateBlockLighting(IEnumerable<(int Index, uint Shadows, uint Warmth)> edits)
    {
        foreach (var (index, shadows, warmth) in edits)
        {
            var prior = _bitmapObjects[index];
            _bitmapObjects[index] = new()
            {
                ImageAssetId = prior.ImageAssetId,
                PlacedSize = prior.PlacedSize,
                BlockGridKey = prior.BlockGridKey,
                ShadowCorners = shadows,
                WarmLightCorners = warmth,
                BiomeTintArgb = prior.BiomeTintArgb,
                BiomeTintGrassSide = prior.BiomeTintGrassSide
            };
        }
        GeometryRevision++;
    }
}
