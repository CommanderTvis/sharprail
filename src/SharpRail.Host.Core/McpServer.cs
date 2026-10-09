using System.Text.Json.Nodes;

namespace SharpRail.Host.Core;

/// <summary>A minimal, stateless Model Context Protocol server over single JSON-RPC requests, serving agents in a
/// terminal exactly the tools its caller hands it. A tool failure is an <c>isError</c> result, never a protocol error.</summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly HashSet<string> KnownProtocols = ["2024-11-05", "2025-03-26", LatestProtocol];
    private const string Instructions =
        "SharpRail's workspace tools and enabled plugins' project tools for the workspace this session runs in. Use those relevant to the task and the project's workflow.";

    /// <summary>A host or active plugin tool on the table; it validates its own arguments.</summary>
    public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, Func<JsonObject, CancellationToken, Task<(string Text, bool Error)>> Call);

    public static async Task<(int Status, JsonNode? Body)> HandleAsync(JsonNode? message, IReadOnlyList<McpTool> tools, CancellationToken cancellationToken = default)
    {
        if (message is not JsonObject frame) return Error(null, -32600, "Expected a single JSON-RPC request object.");
        var id = frame["id"]?.DeepClone();
        if (Text(frame["jsonrpc"]) != "2.0" || Text(frame["method"]) is not { } method) return Error(id, -32600, "Not a JSON-RPC 2.0 request.");
        if (!frame.ContainsKey("id")) return (202, null);
        var parameters = frame["params"] as JsonObject ?? [];
        switch (method)
        {
            case "initialize":
                var requested = Text(parameters["protocolVersion"]);
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested is not null && KnownProtocols.Contains(requested) ? requested : LatestProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "sharprail", ["version"] = "1" },
                    ["instructions"] = Instructions
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["title"] = tool.Title,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone()
                    })])
                });
            case "tools/call":
                var name = Text(parameters["name"]);
                if (tools.FirstOrDefault(tool => tool.Name == name) is not { } called) return Error(id, -32602, $"Unknown tool: {name}");
                var arguments = parameters["arguments"] as JsonObject ?? [];
                (string Text, bool Error) reply;
                try { reply = await called.Call(arguments, cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException) { reply = ("Tool failed: " + error.Message, true); }
                var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text }) };
                if (reply.Error) result["isError"] = true;
                return Result(id, result);
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    private static (int, JsonNode?) Result(JsonNode? id, JsonNode result) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static (int, JsonNode?) Error(JsonNode? id, int code, string message) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}