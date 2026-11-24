using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal IReadOnlyList<PathBezierNode[][]> PressureInkPieces { get; private set; } = [];
    internal Color PressureInkColor { get; private set; }

    internal void SetPressureInkPreview(IReadOnlyList<PathBezierNode[][]> pieces, Color color)
    {
        PressureInkPieces = pieces;
        PressureInkColor = color;
        FreehandPreviewPoints = [];
        FreehandPreviewDiameters = [];
        PressurePreviewLayers = [];
        FreehandPreviewVisible = pieces.Count > 0;
        InvalidateOverlay();
    }

    private bool DrawPressureInkPreview(Graphics graphics)
    {
        if (PressureInkPieces.Count == 0) return false;
        using var path = new GraphicsPath(FillMode.Winding);
        foreach (var piece in PressureInkPieces)
            foreach (var contour in piece)
            {
                if (contour.Length < 3) continue;
                path.StartFigure();
                for (var index = 0; index < contour.Length; index++)
                {
                    var current = contour[index];
                    var next = contour[(index + 1) % contour.Length];
                    path.AddBezier(WorldToScreen(current.Anchor), WorldToScreen(current.OutgoingControl),
                        WorldToScreen(next.IncomingControl), WorldToScreen(next.Anchor));
                }
                path.CloseFigure();
            }
        var mode = graphics.SmoothingMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(PressureInkColor);
            graphics.FillPath(brush, path);
        }
        finally { graphics.SmoothingMode = mode; }
        return true;
    }
}
