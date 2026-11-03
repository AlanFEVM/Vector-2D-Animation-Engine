namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private void DrawSmartSnapLabel(StageControl stage, SmartSnapLine line)
    {
        var font = (stage.Font.FontFamily.Name, stage.Font.SizeInPoints * stage.DeviceDpi / 72f);
        if (_shotLabelFormat is null || _shotLabelFont != font)
        {
            _shotLabelFormat?.Dispose();
            _shotLabelFactory ??= Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>();
            _shotLabelFormat = _shotLabelFactory.CreateTextFormat(font.Item1, font.Item2);
            _shotLabelFormat.WordWrapping = Vortice.DirectWrite.WordWrapping.NoWrap;
            _shotLabelFont = font;
        }
        _target!.DrawText(line.Label, _shotLabelFormat, Rect((line.Start.X + line.End.X) / 2 + 4,
            (line.Start.Y + line.End.Y) / 2 + 4, 160, font.Item2 * 2), BrushFor(Theme.Accent.ToArgb()));
    }
}
