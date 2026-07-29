namespace VectorAnimationEngine;

internal readonly record struct ToolShortcutDisplay(string Tool, string Shortcut);

internal static class ToolShortcutMap
{
    public static IReadOnlyList<ToolShortcutDisplay> DisplayBindings(ToolShortcutPreset preset)
    {
        return DisplayBindings(ShortcutProfiles.GetBuiltInProfile(preset));
    }

    public static IReadOnlyList<ToolShortcutDisplay> DisplayBindings(ShortcutProfileRecord profile)
    {
        var result = new List<ToolShortcutDisplay>();
        foreach (var binding in profile.Bindings)
        {
            if (!ShortcutProfiles.TryGetCommand(binding.CommandId, out var command)) continue;
            result.Add(new ToolShortcutDisplay(command.DisplayName, ShortcutProfiles.FormatGestures(binding.Gestures)));
        }

        return result;
    }

    public static IReadOnlyList<ToolShortcutDisplay> DisplayCatalogBindings(ShortcutProfileRecord profile)
    {
        var bindings = profile.Bindings.ToDictionary(binding => binding.CommandId, StringComparer.Ordinal);
        return ShortcutProfiles.Commands
            .Select(command => new ToolShortcutDisplay(
                command.DisplayName,
                bindings.TryGetValue(command.Id, out var binding)
                    ? ShortcutProfiles.FormatGestures(binding.Gestures)
                    : ""))
            .ToArray();
    }

    public static string? ResolveCommandId(ShortcutProfileRecord profile, Keys keyData)
    {
        if (!ShortcutProfiles.TryCreateGesture(keyData, out var expected)) return null;
        foreach (var binding in profile.Bindings)
        {
            if (!ShortcutProfiles.TryGetCommand(binding.CommandId, out _)) continue;
            if (binding.Gestures.Any(gesture => GestureEquals(gesture, expected))) return binding.CommandId;
        }

        return null;
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
        return ResolveTool(
            ShortcutProfiles.GetBuiltInProfile(preset),
            keyData,
            activeSelectionTool,
            activeShapeTool,
            activeLineTool,
            activeBrushTool,
            activePaintTool);
    }

    public static ToolMode? ResolveTool(
        ShortcutProfileRecord profile,
        Keys keyData,
        ToolMode activeSelectionTool,
        ToolMode activeShapeTool,
        ToolMode activeLineTool,
        ToolMode activeBrushTool,
        ToolMode activePaintTool)
    {
        var commandId = ResolveCommandId(profile, keyData);
        if (!ShortcutProfiles.TryGetCommand(commandId, out var command)) return null;
        return command.Kind switch
        {
            ShortcutCommandKind.Tool => command.Tool,
            ShortcutCommandKind.SelectionGroup => activeSelectionTool,
            ShortcutCommandKind.ShapeGroup => activeShapeTool,
            ShortcutCommandKind.LineGroup => activeLineTool,
            ShortcutCommandKind.BrushGroup => activeBrushTool,
            ShortcutCommandKind.PaintGroup => activePaintTool,
            _ => null
        };
    }

    public static bool IsVaultShortcut(ToolShortcutPreset preset, Keys keyData)
    {
        return IsVaultShortcut(ShortcutProfiles.GetBuiltInProfile(preset), keyData);
    }

    public static bool IsVaultShortcut(ShortcutProfileRecord profile, Keys keyData)
    {
        return string.Equals(
            ResolveCommandId(profile, keyData),
            ShortcutCommandIds.ToggleVault,
            StringComparison.Ordinal);
    }

    internal static ToolMode? ResolveTraditionalFlashTool(Keys keyData)
    {
        return ResolveTool(
            ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.TraditionalFlash),
            keyData,
            ToolMode.Select,
            ToolMode.Rectangle,
            ToolMode.Line,
            ToolMode.Brush,
            ToolMode.Fill);
    }

    internal static ToolMode? ResolveNumberKeyTool(
        Keys keyData,
        ToolMode activeSelectionTool,
        ToolMode activeShapeTool,
        ToolMode activeLineTool,
        ToolMode activeBrushTool,
        ToolMode activePaintTool)
    {
        return ResolveTool(
            ShortcutProfiles.GetBuiltInProfile(ToolShortcutPreset.NumberKeys),
            keyData,
            activeSelectionTool,
            activeShapeTool,
            activeLineTool,
            activeBrushTool,
            activePaintTool);
    }

    private static bool GestureEquals(ShortcutGestureRecord left, ShortcutGestureRecord right)
    {
        return ShortcutProfiles.TryGetKeyData(left, out var leftKeyData)
            && ShortcutProfiles.TryGetKeyData(right, out var rightKeyData)
            && leftKeyData == rightKeyData;
    }
}
