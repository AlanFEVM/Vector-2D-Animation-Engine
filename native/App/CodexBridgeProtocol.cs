using System.Text.Json;
using System.Text.Json.Serialization;

namespace VectorAnimationEngine;

internal sealed record CodexToolDefinition(string Name, string Description, JsonElement InputSchema, bool ReadOnly);

internal static partial class CodexBridgeProtocol
{
    internal static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    internal static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
    internal static JsonElement Schema(string properties = "", string required = "")
    {
        using var document = JsonDocument.Parse($$"""{"type":"object","properties":{ {{properties}} },"required":[{{required}}],"additionalProperties":false}""");
        return document.RootElement.Clone();
    }

    internal static readonly JsonElement SettingsSchema = Schema("""
        "language":{"type":"string","enum":["English","SimplifiedChinese"]},
        "colorTheme":{"type":"string","enum":["Dark","White"]},
        "themeHueDegrees":{"type":"integer","minimum":0,"maximum":359},
        "themeSaturationPercent":{"type":"integer","minimum":0,"maximum":200},
        "themeBrightnessPercent":{"type":"integer","minimum":50,"maximum":150},
        "accentHueDegrees":{"type":"integer","minimum":0,"maximum":359},
        "accentSaturationPercent":{"type":"integer","minimum":0,"maximum":200},
        "accentBrightnessPercent":{"type":"integer","minimum":50,"maximum":150},
        "timelineFrameWidth":{"type":"integer","minimum":8,"maximum":32},
        "timelineFrameHeight":{"type":"string","enum":["Low","Medium","High"]},
        "timelineAutoKeyframeEnabled":{"type":"boolean"},
        "workspaceColorArgb":{"type":"integer","minimum":-2147483648,"maximum":2147483647}
        """);

    internal static readonly CodexToolDefinition[] Tools =
    [
        new("editor_get_state", "Read the active workspace, tool, frame, selection and busy state.", Schema(), true),
        new("project_get_info", "Read project identity, dirty state, path, scenes and drawing objects.", Schema(), true),
        new("tools_get", "List drawing tools with availability in the current workspace and current drawing settings.", Schema(), true),
        new("tool_set_active", "Activate an available drawing tool by its exact name from tools_get.",
            Schema("\"tool\":{\"type\":\"string\"}", "\"tool\""), false),
        new("timeline_set_frame", "Move to a zero-based frame inside the active playback range.",
            Schema("\"frame\":{\"type\":\"integer\",\"minimum\":0}", "\"frame\""), false),
        new("timeline_set_playback", "Start or stop playback.",
            Schema("\"playing\":{\"type\":\"boolean\"}", "\"playing\""), false),
        new("editor_undo", "Undo the last edit using the editor's existing undo history.", Schema(), false),
        new("settings_get", "Read application settings and their writable schema. Authentication secrets are omitted.", Schema(), true),
        new("settings_update", "Persist and apply a partial application-settings update. Connection permissions must be changed in the Settings UI.",
            SettingsSchema, false),
        new("project_open", "Open an absolute .v2dProject path. Rejects unsaved work unless discardUnsaved is explicitly true.",
            Schema("\"path\":{\"type\":\"string\"},\"discardUnsaved\":{\"type\":\"boolean\"}", "\"path\""), false),
        new("project_save", "Save to the current project path or an absolute .v2dProject path. A new target requires a dedicated project directory.",
            Schema("\"path\":{\"type\":\"string\"}"), false),
        new("project_new", "Create an empty project. Rejects unsaved work unless discardUnsaved is explicitly true.",
            Schema("\"discardUnsaved\":{\"type\":\"boolean\"}"), false),
        ..CreateDocumentTools(),
        ..CreateSpatialTools(),
        ..CreateAnimationTools()
    ];

    public static object Error(object? id, int code, string message)
        => new { jsonrpc = "2.0", id, error = new { code, message } };

    internal static async Task<object?> HandleAsync(JsonElement request,
        Func<string, JsonElement, CancellationToken, Task<object>> execute, CancellationToken cancellation)
    {
        // The March 2025 protocol permits batches. Bound the batch and keep
        // command order deterministic, while omitting notification replies.
        if (request.ValueKind == JsonValueKind.Array)
        {
            if (request.GetArrayLength() is 0 or > 32) return Error(null, -32600, "A batch must contain 1 to 32 messages.");
            var responses = new List<object>();
            foreach (var item in request.EnumerateArray())
            {
                var response = item.ValueKind == JsonValueKind.Array
                    ? Error(null, -32600, "Nested batches are invalid.")
                    : await HandleAsync(item, execute, cancellation).ConfigureAwait(false);
                if (response is not null) responses.Add(response);
            }
            return responses.Count == 0 ? null : responses.ToArray();
        }
        if (request.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Expected a JSON-RPC request object.");
        var hasId = request.TryGetProperty("id", out var id);
        if (!request.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0"
            || !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String
            || (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))
            return Error(null, -32600, "Invalid JSON-RPC envelope.");
        // Notifications never execute tools, and never receive a JSON-RPC reply.
        if (!hasId) return null;
        object responseId = id.Clone();
        var parameters = request.TryGetProperty("params", out var p) ? p : EmptyArguments;
        if (parameters.ValueKind != JsonValueKind.Object) return Error(responseId, -32602, "params must be an object.");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            object result;
            switch (method.GetString())
            {
                case "initialize":
                    if (!parameters.TryGetProperty("protocolVersion", out var requested) || requested.ValueKind != JsonValueKind.String)
                        return Error(responseId, -32602, "protocolVersion is required.");
                    result = new
                    {
                        protocolVersion = ProtocolVersions.Contains(requested.GetString()) ? requested.GetString() : ProtocolVersions[0],
                        capabilities = new { tools = new { listChanged = false }, resources = new { subscribe = false, listChanged = false } },
                        serverInfo = new { name = "vector2d-editor", version = ReleaseNotesCatalog.CurrentVersion.ToString() },
                        instructions = "This server controls the running Vector 2D editor. Read editor_get_state and tools_get before changing context. Frames are zero-based. Busy editors reject mutations. Write access is controlled in Settings > Codex / MCP. Project open/new preserve unsaved work unless discardUnsaved is explicitly true."
                    };
                    break;
                case "ping": result = new { }; break;
                case "tools/list":
                    result = new { tools = Tools.Select(t => new
                    {
                        name = t.Name, description = t.Description, inputSchema = t.InputSchema,
                        annotations = new { readOnlyHint = t.ReadOnly, destructiveHint = !t.ReadOnly, openWorldHint = false }
                    }) };
                    break;
                case "tools/call":
                    if (!parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                        return Error(responseId, -32602, "A tool name is required.");
                    var tool = Tools.FirstOrDefault(t => t.Name == name.GetString());
                    if (tool is null) return Error(responseId, -32602, "Unknown tool.");
                    var arguments = parameters.TryGetProperty("arguments", out var a) ? a : EmptyArguments;
                    try
                    {
                        ValidateArguments(arguments, tool.InputSchema);
                        var data = await execute(tool.Name, arguments, cancellation).ConfigureAwait(false);
                        result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonOptions) } },
                            structuredContent = data, isError = false };
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        result = new { content = new[] { new { type = "text", text = ex.Message } }, isError = true };
                    }
                    break;
                case "resources/list":
                    result = new { resources = new[]
                    {
                        new { uri = "vector2d://editor/state", name = "Editor state", mimeType = "application/json" },
                        new { uri = "vector2d://application/settings", name = "Application settings", mimeType = "application/json" }
                    } };
                    break;
                case "resources/templates/list": result = new { resourceTemplates = Array.Empty<object>() }; break;
                case "resources/read":
                    if (!parameters.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String)
                        return Error(responseId, -32602, "A resource URI is required.");
                    var resourceTool = uri.GetString() switch
                    {
                        "vector2d://editor/state" => "editor_get_state",
                        "vector2d://application/settings" => "settings_get",
                        _ => null
                    };
                    if (resourceTool is null) return Error(responseId, -32002, "Unknown resource.");
                    var resourceData = await execute(resourceTool, EmptyArguments, cancellation).ConfigureAwait(false);
                    result = new { contents = new[] { new { uri = uri.GetString(), mimeType = "application/json",
                        text = JsonSerializer.Serialize(resourceData, JsonOptions) } } };
                    break;
                default: return Error(responseId, -32601, "Method not found.");
            }
            return new { jsonrpc = "2.0", id = responseId, result };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("MCP dispatch failed.", ex);
            return Error(responseId, -32603, "The editor could not process the request.");
        }
    }

    internal static void ValidateArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!arguments.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException($"Missing argument: {required.GetString()}.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException($"Duplicate argument: {property.Name}.");
            if (!schema.GetProperty("properties").TryGetProperty(property.Name, out var definition))
                throw new ArgumentException($"Unknown argument: {property.Name}.");
            var value = property.Value;
            var valid = definition.GetProperty("type").GetString() switch
            {
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "string" => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
                _ => false
            };
            if (!valid) throw new ArgumentException($"Invalid value for {property.Name}.");
            if (definition.TryGetProperty("enum", out var choices)
                && !choices.EnumerateArray().Any(choice => choice.ValueKind == value.ValueKind
                    && (value.ValueKind == JsonValueKind.String
                        ? choice.GetString() == value.GetString()
                        : choice.GetRawText() == value.GetRawText())))
                throw new ArgumentException($"Unsupported value for {property.Name}.");
            if ((definition.TryGetProperty("minimum", out var min) && value.GetDouble() < min.GetDouble())
                || (definition.TryGetProperty("maximum", out var max) && value.GetDouble() > max.GetDouble()))
                throw new ArgumentException($"Value is out of range for {property.Name}.");
            if (value.ValueKind == JsonValueKind.String
                && definition.TryGetProperty("maxLength", out var maxLength)
                && value.GetString()!.Length > maxLength.GetInt32())
                throw new ArgumentException($"Value is too long for {property.Name}.");
        }
    }
}
