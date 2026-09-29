namespace VectorAnimationEngine;

internal static partial class CodexBridgeProtocol
{
    private static CodexToolDefinition[] CreateAnimationTools() =>
    [
        new("animation_import_bundle",
            "Build a new native project from a version 1 JSON bundle of contained SVG frames, nested symbols and instance animation. Rejects unsaved work unless discardUnsaved is true. Save the imported project with project_save.",
            Schema("\"path\":{\"type\":\"string\"},\"discardUnsaved\":{\"type\":\"boolean\"}", "\"path\""), false)
    ];
}
