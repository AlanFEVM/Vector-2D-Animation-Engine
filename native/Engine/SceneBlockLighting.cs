namespace VectorAnimationEngine;

internal static class SceneBlockLighting
{
    // Sample two short rays towards the sky. One leans towards the viewer
    // to light exposed walls; the straight ray also picks up cave ceilings.
    private static readonly SceneBlockGrid.Cell[] Slanted =
        [new(0, -1, 0), new(0, -2, -1), new(0, -3, -1), new(0, -4, -2), new(0, -5, -2), new(0, -6, -3)];
    private static readonly SceneBlockGrid.Cell[] Vertical =
        [new(0, -1, -1), new(0, -2, -1), new(0, -3, -1), new(0, -4, -1), new(0, -5, -1), new(0, -6, -1)];

    internal static uint Calculate(SceneBlockGrid.Cell cell, Func<SceneBlockGrid.Cell, bool> opaque)
    {
        var sky = 0;
        foreach (var sample in Slanted)
            if (opaque(new(cell.X + sample.X, cell.Y + sample.Y, cell.Depth + sample.Depth))) { sky += 40; break; }
        foreach (var sample in Vertical)
            if (opaque(new(cell.X + sample.X, cell.Y + sample.Y, cell.Depth + sample.Depth))) { sky += 32; break; }

        var x = cell.X;
        var y = cell.Y;
        var front = cell.Depth - 1;
        if (front < 0) return (uint)sky * 0x01010101u;
        var left = opaque(new(x - 1, y, front));
        var right = opaque(new(x + 1, y, front));
        var top = opaque(new(x, y - 1, front));
        var bottom = opaque(new(x, y + 1, front));
        return Corner(left, top, -1, -1) | Corner(right, top, 1, -1) << 8
            | Corner(left, bottom, -1, 1) << 16 | Corner(right, bottom, 1, 1) << 24;

        uint Corner(bool horizontal, bool vertical, int dx, int dy) => (uint)(sky +
            (horizontal && vertical ? 64 : (horizontal ? 24 : 0) + (vertical ? 24 : 0)
                + (opaque(new(x + dx, y + dy, front)) ? 16 : 0)));
    }

    internal static HashSet<(int X, int Y)> AffectedColumns(SceneBlockGrid grid, SceneBlockGrid.Cell edited, bool occlusionChanged)
    {
        var columns = new HashSet<(int X, int Y)> { (edited.X, edited.Y) };
        if (!occlusionChanged || !grid.ProceduralLighting && grid.Shadows.Count == 0) return columns;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                Add(edited.X + dx, edited.Y + dy);
        if (grid.ProceduralLighting)
        {
            // Invert the same sampling offsets used to find casters.
            foreach (var sample in Slanted) Add(edited.X - sample.X, edited.Y - sample.Y);
            foreach (var sample in Vertical) Add(edited.X - sample.X, edited.Y - sample.Y);
        }
        return columns;

        void Add(int x, int y) { if (grid.Contains(x, y)) columns.Add((x, y)); }
    }
}
