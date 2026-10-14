namespace VectorAnimationEngine;

internal sealed partial class VectorProject
{
    internal DrawingObjectDefinition GetOrCreateOpenCanvas()
    {
        var existing = _drawingObjects.FirstOrDefault(item => item.IsOpenCanvas);
        if (existing is not null) return existing;
        var canvas = new DrawingObjectDefinition
        {
            Name = "Open Canvas",
            Kind = "OpenCanvas",
            Detail = "Independent drawing workspace"
        };
        canvas.Scene.CreateEmpty(frameCount: 1);
        _drawingObjects.Add(canvas);
        Changed?.Invoke(this, EventArgs.Empty);
        return canvas;
    }

    internal string GetNextSymbolName()
    {
        var symbols = _drawingObjects.Where(item => !item.IsOpenCanvas).ToArray();
        var initial = symbols.FirstOrDefault();
        var unusedInitial = initial is not null && initial.Name == "Symbol 001"
            && initial.Scene.ObjectCount == 0 && initial.Instances.Count == 0
            && !_scenes.Any(scene => scene.Instances.Any(instance => instance.DrawingObjectId == initial.Id))
            && !symbols.Any(symbol => symbol.Instances.Any(instance => instance.DrawingObjectId == initial.Id));
        var number = symbols.Length + 1 - (unusedInitial ? 1 : 0);
        while (symbols.Any(symbol => symbol.Name == number.ToString("000"))) number++;
        return number.ToString("000");
    }

    internal DrawingObjectDefinition AddSymbolFromOpenCanvasSelection(VectorScene selection, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.ObjectCount == 0) throw new ArgumentException("The selection is empty.", nameof(selection));
        var symbol = new DrawingObjectDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? GetNextSymbolName() : name.Trim()
        };
        symbol.Scene.RestoreSnapshot(selection.CreateSnapshot());
        var bounds = selection.GetObjectWorldBounds(0);
        for (var index = 1; index < selection.ObjectCount; index++)
            bounds = RectangleF.Union(bounds, selection.GetObjectWorldBounds(index));
        symbol.SetAnchor(new PointF(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        _drawingObjects.Add(symbol);
        Changed?.Invoke(this, EventArgs.Empty);
        return symbol;
    }
}
