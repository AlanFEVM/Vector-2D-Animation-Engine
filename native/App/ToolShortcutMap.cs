namespace VectorAnimationEngine;

internal readonly record struct ToolShortcutDisplay(string Tool, string Shortcut);

internal static class ToolShortcutMap
{
    private static readonly ToolShortcutDisplay[] TraditionalFlashBindings =
    [
        new("Select", "V"),
        new("Free Transform", "Q"),
        new("Hand", "H"),
        new("Rectangle", "R"),
        new("Ellipse", "O"),
        new("Line", "N"),
        new("Pen", "P"),
        new("Pencil", "Y"),
        new("Brush", "B"),
        new("Fill", "K"),
        new("Ink Bottle", "S"),
        new("Eyedropper", "I"),
        new("Gradient", "G"),
        new("Eraser", "E")
    ];

    private static readonly ToolShortcutDisplay[] NumberKeyBindings =
    [
        new("Selection group", "1"),
        new("Shape group", "2"),
        new("Line group", "3"),
        new("Brush group", "4"),
        new("Fill group", "5"),
        new("Gradient", "6"),
        new("Eraser", "7"),
        new("Eyedropper", "8"),
        new("Vault", "9")
    ];

    public static IReadOnlyList<ToolShortcutDisplay> DisplayBindings(ToolShortcutPreset preset)
    {
        return preset == ToolShortcutPreset.NumberKeys ? NumberKeyBindings : TraditionalFlashBindings;
    }

    public static ToolMode? ResolveTool(
        ToolShortcutPreset preset,
        Keys keyData,
        ToolMode activeSelectionTool,
        ToolMode activeShapeTool,
        ToolMode activeLineTool,
        ToolMode activeBrushTool,
        ToolMode activePaintTool)
    {
        return preset == ToolShortcutPreset.NumberKeys
            ? ResolveNumberKeyTool(
                keyData,
                activeSelectionTool,
                activeShapeTool,
                activeLineTool,
                activeBrushTool,
                activePaintTool)
            : ResolveTraditionalFlashTool(keyData);
    }

    public static bool IsVaultShortcut(ToolShortcutPreset preset, Keys keyData)
    {
        return preset == ToolShortcutPreset.NumberKeys && keyData is Keys.D9 or Keys.NumPad9;
    }

    internal static ToolMode? ResolveTraditionalFlashTool(Keys keyData)
    {
        return keyData switch
        {
            Keys.V => ToolMode.Select,
            Keys.Q => ToolMode.Transform,
            Keys.H => ToolMode.Hand,
            Keys.R => ToolMode.Rectangle,
            Keys.O => ToolMode.Ellipse,
            Keys.N => ToolMode.Line,
            Keys.P => ToolMode.Pen,
            Keys.Y => ToolMode.Pencil,
            Keys.B => ToolMode.Brush,
            Keys.K => ToolMode.Fill,
            Keys.S => ToolMode.InkBottle,
            Keys.I => ToolMode.Eyedropper,
            Keys.G => ToolMode.Gradient,
            Keys.E => ToolMode.Eraser,
            _ => null
        };
    }

    internal static ToolMode? ResolveNumberKeyTool(
        Keys keyData,
        ToolMode activeSelectionTool,
        ToolMode activeShapeTool,
        ToolMode activeLineTool,
        ToolMode activeBrushTool,
        ToolMode activePaintTool)
    {
        return keyData switch
        {
            Keys.D1 or Keys.NumPad1 => activeSelectionTool,
            Keys.D2 or Keys.NumPad2 => activeShapeTool,
            Keys.D3 or Keys.NumPad3 => activeLineTool,
            Keys.D4 or Keys.NumPad4 => activeBrushTool,
            Keys.D5 or Keys.NumPad5 => activePaintTool,
            Keys.D6 or Keys.NumPad6 => ToolMode.Gradient,
            Keys.D7 or Keys.NumPad7 => ToolMode.Eraser,
            Keys.D8 or Keys.NumPad8 => ToolMode.Eyedropper,
            _ => null
        };
    }
}
