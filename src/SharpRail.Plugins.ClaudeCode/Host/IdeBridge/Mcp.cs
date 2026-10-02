using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.IdeBridge;

internal sealed record JsonRpcRequest(string Method, JsonNode? Id, JsonNode? Params);

/// <summary>
/// The MCP/JSON-RPC shapes a <c>claude</c> CLI expects from an IDE, read off the CLI itself and Anthropic's VS Code
/// and JetBrains extensions: undocumented, and therefore pinned by the checks rather than trusted to stay stable.
/// </summary>
internal static class IdeMcp
{
    public const string ProtocolVersion = "2024-11-05";
    public const string AuthHeader = "x-claude-code-ide-authorization";
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;

    public static JsonRpcRequest? Parse(string raw)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
        if (parsed is not JsonObject message || Json(message["jsonrpc"]) != "2.0" || Json(message["method"]) is not { } method) return null;
        var id = message["id"];
        if (id is not null && id.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number)) return null;
        return new(method, id?.DeepClone(), message["params"]?.DeepClone());
    }

    private static string? Json(JsonNode? value) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : null;

    public static string Success(JsonNode id, JsonNode? result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString();

    public static string Failure(JsonNode id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    /// <summary>A server-to-client MCP notification: no id, never answered.</summary>
    public static string Notification(string method, JsonNode? parameters) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToJsonString();

    /// <summary>A tool's reply as MCP text items: JSON for the tools that describe state, a bare word the CLI compares for the rest.</summary>
    public static JsonObject ToolContent(IReadOnlyList<string> texts) =>
        new() { ["content"] = new JsonArray([.. texts.Select(text => (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = text })]) };

    private static JsonObject Schema(params (string Name, string Type)[] properties) => Schema(properties, []);

    private static JsonObject Schema((string Name, string Type)[] properties, string[] required)
    {
        var map = new JsonObject();
        foreach (var (name, type) in properties) map[name] = new JsonObject { ["type"] = type };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = map };
        if (required.Length > 0) schema["required"] = new JsonArray([.. required.Select(name => (JsonNode?)name)]);
        return schema;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) =>
        new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };

    public static JsonArray Tools() =>
    [
        Tool("openFile", "Open a file in the editor, optionally selecting a range matched by text.",
            Schema([("filePath", "string"), ("preview", "boolean"), ("startText", "string"), ("endText", "string"),
                ("selectToEndOfLine", "boolean"), ("makeFrontmost", "boolean")], ["filePath"])),
        Tool("openDiff", "Show a diff between a file on disk and proposed contents.",
            Schema(("old_file_path", "string"), ("new_file_path", "string"), ("new_file_contents", "string"), ("tab_name", "string"))),
        Tool("getCurrentSelection", "The active editor's current selection.", Schema()),
        Tool("getLatestSelection", "The most recent selection, even if the editor no longer has focus.", Schema()),
        Tool("getOpenEditors", "The list of open editor tabs.", Schema()),
        Tool("getWorkspaceFolders", "The workspace folders this IDE window has open.", Schema()),
        Tool("checkDocumentDirty", "Whether a file has unsaved changes.", Schema([("filePath", "string")], ["filePath"])),
        Tool("saveDocument", "Save a file's unsaved changes.", Schema([("filePath", "string")], ["filePath"])),
        Tool("close_tab", "Close a tab by the name given to openDiff, or by its file's absolute path.", Schema([("tab_name", "string")], ["tab_name"])),
        Tool("closeAllDiffTabs", "Close every open diff tab.", Schema())
    ];
}