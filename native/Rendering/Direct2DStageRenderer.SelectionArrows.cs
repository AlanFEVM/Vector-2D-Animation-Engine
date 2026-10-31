using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawObjectArrowSelection(StageControl stage)
    {
        if (stage.SelectionFeedbackSuppressed) return;
        stage.Scene.PrepareSelectionOutlineBatch(stage.SelectedObjects, stage.Frame);
        var outline = BrushFor(Theme.Accent.ToArgb());
        var width = stage.SelectionArrows.OutlineWidth * stage.DeviceDpi / 96f;
        using var stipple = _target!.CreateBitmapBrush(BitmapObjectBitmap(StageControl.ObjectSelectionPattern));
        stipple.ExtendModeX = ExtendMode.Wrap;
        stipple.ExtendModeY = ExtendMode.Wrap;
        stipple.InterpolationMode = BitmapInterpolationMode.NearestNeighbor;
        var transform = _target.Transform;
        var offset = _recordingSelectionCommands ? PointF.Empty : stage.SelectionDragPreviewOffset;
        try
        {
            _target.Transform = transform * Matrix3x2.CreateTranslation(
                stage.WorldLengthToScreen(1) * offset.X, stage.WorldLengthToScreen(1) * offset.Y);
            foreach (var fill in stage.ObjectArrowSelectedFills())
            {
                stipple.Transform = Matrix3x2.CreateTranslation(WorldToVector(stage, fill.PatternOrigin));
                using var path = _factory!.CreatePathGeometry();
                using (var sink = path.Open())
                {
                    sink.SetFillMode(FillMode.Alternate);
                    foreach (var contour in fill.Contours)
                    {
                        if (contour.Length < 3) continue;
                        sink.BeginFigure(WorldToVector(stage, contour[0]), FigureBegin.Filled);
                        foreach (var point in contour.Skip(1)) sink.AddLine(WorldToVector(stage, point));
                        sink.EndFigure(FigureEnd.Closed);
                    }
                    sink.Close();
                }
                _target.FillGeometry(path, stipple);
            }
            foreach (var stroke in stage.ObjectArrowSelectedStrokes())
            {
                stipple.Transform = Matrix3x2.CreateTranslation(WorldToVector(stage, stroke.PatternOrigin));
                var strokeWidth = Math.Max(1, stage.WorldLengthToScreen(stage.Scene.Stroke[stroke.ObjectIndex]));
                foreach (var contour in stroke.Contours)
                {
                    if (contour.Length > 1)
                    {
                        using var path = BuildSelectionPolylinePath(contour.Select(point => WorldToVector(stage, point)).ToArray());
                        _target.DrawGeometry(path, stipple, strokeWidth, RoundStrokeStyle());
                    }
                    else if (contour.Length == 1)
                        _target.FillEllipse(new Ellipse(WorldToVector(stage, contour[0]), strokeWidth / 2, strokeWidth / 2), stipple);
                }
            }
        }
        finally { _target.Transform = transform; }
        foreach (var objectIndex in stage.SelectedObjects)
        {
            if ((uint)objectIndex >= stage.Scene.ObjectCount || !stage.Scene.IsObjectActive(objectIndex, stage.Frame)) continue;
            if (!stage.ObjectArrowUsesBounds(objectIndex)) continue;
            foreach (var contour in stage.GetObjectArrowSelectionContours(objectIndex))
            {
                if (contour.Length > 1)
                {
                    using var path = BuildSelectionPolylinePath(contour.Select(point => WorldToVector(stage, point)).ToArray());
                    _target!.DrawGeometry(path, outline, width);
                }
                else if (contour.Length == 1)
                    _target!.FillEllipse(new Ellipse(WorldToVector(stage, contour[0]), 2, 2), outline);
            }
        }
        if (!_recordingSelectionCommands) DrawObjectArrowAdornments(stage);
    }

    private void DrawObjectArrowAdornments(StageControl stage)
    {
        if (!stage.TryGetObjectArrowSelectionBounds(out var bounds)) return;
        var topLeft = WorldToVector(stage, bounds.Location);
        var bottomRight = WorldToVector(stage, new PointF(bounds.Right, bounds.Bottom));
        var screen = RectangleF.FromLTRB(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y));
        var outline = BrushFor(Theme.Accent.ToArgb());
        var width = stage.SelectionArrows.OutlineWidth * stage.DeviceDpi / 96f;
        if (stage.DrawingObjectSelectionVisible)
            _target!.DrawRectangle(Rect(screen.X, screen.Y, screen.Width, screen.Height), outline, width);
        if (stage.SelectionArrows.ShowCornerMarks)
        {
            foreach (var mark in StageControl.ObjectArrowCornerMarks(screen, stage.DeviceDpi / 96f))
            {
                _target!.DrawLine(new Vector2(mark.Start.X, mark.Start.Y), new Vector2(mark.Corner.X, mark.Corner.Y), outline, width);
                _target.DrawLine(new Vector2(mark.Corner.X, mark.Corner.Y), new Vector2(mark.End.X, mark.End.Y), outline, width);
            }
        }
        if (!stage.SelectionArrows.ShowSelectionLabel) return;
        var font = (stage.Font.FontFamily.Name, stage.Font.SizeInPoints * stage.DeviceDpi / 72f);
        if (_shotLabelFormat is null || _shotLabelFont != font)
        {
            _shotLabelFormat?.Dispose();
            _shotLabelFormat = null;
            _shotLabelFactory ??= Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>();
            _shotLabelFormat = _shotLabelFactory.CreateTextFormat(font.Item1, font.Item2);
            _shotLabelFormat.WordWrapping = Vortice.DirectWrite.WordWrapping.NoWrap;
            _shotLabelFont = font;
        }
        var text = stage.ObjectArrowSelectionLabel;
        using var layout = _shotLabelFactory!.CreateTextLayout(text, _shotLabelFormat,
            Math.Max(1, stage.Width - 20), font.Item2 * 2);
        var labelWidth = Math.Min(layout.Metrics.Width + 10, Math.Max(1, stage.Width - 4));
        var labelHeight = layout.Metrics.Height + 4;
        var left = Math.Clamp(screen.Left, 2, Math.Max(2, stage.Width - labelWidth - 2));
        var top = Math.Max(2, screen.Top - labelHeight - 9);
        _target!.FillRectangle(Rect(left, top, labelWidth, labelHeight), BrushFor(Theme.Panel.ToArgb()));
        _target.DrawText(text, _shotLabelFormat, Rect(left + 5, top + 2, Math.Max(1, labelWidth - 10), labelHeight - 4),
            outline, DrawTextOptions.Clip);
    }
}
