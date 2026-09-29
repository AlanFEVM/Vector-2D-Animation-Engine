using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class CodexBridgeProtocol
{
    internal static CodexToolDefinition[] CreateDocumentTools()
    {
        return
        [
            new("workspace_set_active", "Switch the active editor workspace.",
                Schema("\"workspace\":{\"type\":\"string\",\"enum\":[\"BasicDrawing\",\"SceneEditor\"]}", "\"workspace\""), false),
            new("layers_get", "Read the layers in the active workspace context.", Schema(), true),
            new("layer_create", "Create a drawing or scene layer in the active workspace context.",
                Schema("\"name\":{\"type\":\"string\",\"maxLength\":80}"), false),
            new("layer_update", "Update the name, visibility, lock state, or opacity of one layer.",
                Schema("\"layerId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"maxLength\":80},\"visible\":{\"type\":\"boolean\"},\"locked\":{\"type\":\"boolean\"},\"opacity\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}", "\"layerId\""), false),
            new("layer_reorder", "Move one layer to an exact zero-based layer index.",
                Schema("\"layerId\":{\"type\":\"string\"},\"index\":{\"type\":\"integer\",\"minimum\":0}", "\"layerId\",\"index\""), false),
            new("layer_remove", "Remove one layer and its layer content from the active workspace context.",
                Schema("\"layerId\":{\"type\":\"string\"}", "\"layerId\""), false),
            new("layer_set_active", "Select one layer as the active timeline layer.",
                Schema("\"layerId\":{\"type\":\"string\"}", "\"layerId\""), false),
            new("symbols_get", "Read all reusable symbols in the current project.", Schema(), true),
            new("symbol_create", "Create and activate a reusable symbol.",
                Schema("\"name\":{\"type\":\"string\",\"maxLength\":80}"), false),
            new("symbol_duplicate", "Duplicate a reusable symbol and activate the copy.",
                Schema("\"symbolId\":{\"type\":\"string\"}", "\"symbolId\""), false),
            new("symbol_rename", "Rename a reusable symbol.",
                Schema("\"symbolId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"maxLength\":80}", "\"symbolId\",\"name\""), false),
            new("symbol_remove", "Remove a reusable symbol and references to it.",
                Schema("\"symbolId\":{\"type\":\"string\"}", "\"symbolId\""), false),
            new("symbol_set_active", "Select a reusable symbol in the current editor context.",
                Schema("\"symbolId\":{\"type\":\"string\"}", "\"symbolId\""), false),
            new("scene_create", "Create and activate a scene.",
                Schema("\"name\":{\"type\":\"string\",\"maxLength\":80}"), false),
            new("scene_rename", "Rename a scene.",
                Schema("\"sceneId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"maxLength\":80}", "\"sceneId\",\"name\""), false),
            new("scene_remove", "Remove a scene from the current project.",
                Schema("\"sceneId\":{\"type\":\"string\"}", "\"sceneId\""), false),
            new("scene_set_active", "Select a scene in the Scene Editor.",
                Schema("\"sceneId\":{\"type\":\"string\"}", "\"sceneId\""), false),
            new("scene_instances_get", "Read all symbol instances in the active scene.", Schema(), true),
            new("scene_instance_create", "Create a symbol instance in the active scene.",
                Schema("\"symbolId\":{\"type\":\"string\"},\"layerId\":{\"type\":\"string\"},\"x\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000},\"y\":{\"type\":\"number\",\"minimum\":-5000000,\"maximum\":5000000}", "\"symbolId\""), false),
            new("scene_instance_remove", "Remove one symbol instance from the active scene.",
                Schema("\"instanceId\":{\"type\":\"string\"}", "\"instanceId\""), false)
        ];
    }
}
