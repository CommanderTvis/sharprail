using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Host.Core;

/// <summary>A minimal, stateless Model Context Protocol server over single JSON-RPC requests, serving the
/// spec tools to agents in a terminal. A tool failure is an <c>isError</c> result, never a protocol error.</summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private const int DefaultGrepLimit = 200;
    private static readonly HashSet<string> KnownProtocols = ["2024-11-05", "2025-03-26", LatestProtocol];
    private const string Instructions =
        "SharpRail's project tools for the workspace this session runs in. The spec_* tools read the project's spec-graph — its living design docs; reach for spec_grep/spec_get before exploring code.";

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, string, CancellationToken, Task<(string Text, bool Error)>> Call);

    private static readonly Tool[] Tools =
    [
        new("spec_grep",
            "Search the project's spec-graph: regex or substring match within spec files, optionally narrowed by type or parent. Returns path:line matches with a snippet. Read a matched file's body with the normal read tool.",
            Schema(["pattern"],
                ("pattern", "string", "Regex or substring to search for within spec files."),
                ("regex", "boolean", "Treat pattern as a regular expression (default: substring)."),
                ("ignoreCase", "boolean", "Case-insensitive match (default: true)."),
                ("type", "string", "Only search specs with this frontmatter type."),
                ("parent", "string", "Only search specs whose parent is this id."),
                ("limit", "number", $"Max matches to return (default: {DefaultGrepLimit}).")),
            GrepAsync),
        new("spec_get",
            "Get one spec node by id: its type, title, path and parent links in both directions. Returns no prose body — read the file at the returned path with the read tool.",
            Schema(["id"], ("id", "string", "The spec id to look up.")),
            GetAsync),
    ];

    public static async Task<(int Status, JsonNode? Body)> HandleAsync(JsonNode? message, string cwd, CancellationToken cancellationToken = default)
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
                    ["tools"] = new JsonArray([.. Tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.Schema.DeepClone()
                    })])
                });
            case "tools/call":
                var name = Text(parameters["name"]);
                if (Tools.FirstOrDefault(tool => tool.Name == name) is not { } called) return Error(id, -32602, $"Unknown tool: {name}");
                var arguments = parameters["arguments"] as JsonObject ?? [];
                (string Text, bool Error) reply;
                try
                {
                    reply = Invalid(called, arguments) is { } problem
                        ? ($"Invalid arguments for {called.Name} — {problem}", true)
                        : await called.Call(arguments, cwd, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException) { reply = ("Tool failed: " + error.Message, true); }
                var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text }) };
                if (reply.Error) result["isError"] = true;
                return Result(id, result);
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static async Task<(string, bool)> GrepAsync(JsonObject arguments, string cwd, CancellationToken cancellationToken)
    {
        var pattern = Text(arguments["pattern"])!;
        var ignoreCase = arguments["ignoreCase"]?.GetValue<bool>() ?? true;
        Func<string, bool> matches;
        if (arguments["regex"]?.GetValue<bool>() == true)
        {
            Regex expression;
            try { expression = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException error) { return ("Error: Invalid search pattern: " + error.Message, true); }
            matches = expression.IsMatch;
        }
        else matches = line => line.Contains(pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var requested = (int)Math.Truncate(arguments["limit"]?.GetValue<double>() ?? DefaultGrepLimit);
        var limit = requested > 0 ? requested : DefaultGrepLimit;
        var type = Text(arguments["type"]); var parent = Text(arguments["parent"]);
        var found = new List<string>();
        var truncated = false;
        foreach (var spec in (await SpecCatalog.ReadAsync(cwd, cancellationToken)).OrderBy(spec => spec.Path, StringComparer.Ordinal))
        {
            if ((type is not null && spec.Type != type) || (parent is not null && spec.Parent != parent)) continue;
            var lines = (await File.ReadAllTextAsync(Path.Combine(cwd, spec.Path), cancellationToken)).TrimStart('﻿').Split('\n');
            for (var index = 0; index < lines.Length && !truncated; index++)
            {
                var line = lines[index].TrimEnd('\r');
                if (!matches(line)) continue;
                if (found.Count >= limit) truncated = true;
                else found.Add($"{spec.Path}:{index + 1}: {line.Trim()}");
            }
            if (truncated) break;
        }
        var header = found.Count == 0 ? "No matches." : $"{found.Count} match(es){(truncated ? " (truncated)" : "")}:";
        return ((header + "\n" + string.Join('\n', found)).TrimEnd(), false);
    }

    private static async Task<(string, bool)> GetAsync(JsonObject arguments, string cwd, CancellationToken cancellationToken)
    {
        var id = Text(arguments["id"])!;
        var specs = await SpecCatalog.ReadAsync(cwd, cancellationToken);
        if (specs.FirstOrDefault(spec => spec.Id == id) is not { } node) return ($"Error: No spec with id \"{id}\".", true);
        string Link(string target) => $"  parent -> {target}" + (specs.FirstOrDefault(spec => spec.Id == target) is { } found ? $" ({found.Path})" : " (missing)");
        var links = node.Parent.Length > 0 ? [Link(node.Parent)] : Array.Empty<string>();
        var children = specs.Where(spec => spec.Parent == id).Select(spec => $"  parent -> {spec.Id} ({spec.Path})").ToArray();
        return (string.Join('\n',
            $"{node.Id} [{node.Type}] — {node.Title}",
            $"path: {node.Path}",
            links.Length > 0 ? "links:\n" + string.Join('\n', links) : "links: (none)",
            children.Length > 0 ? "referenced by:\n" + string.Join('\n', children) : "referenced by: (none)"), false);
    }

    // The published schema is the enforced contract: required properties and each property's JSON type.
    private static string? Invalid(Tool tool, JsonObject arguments)
    {
        foreach (var required in tool.Schema["required"]!.AsArray())
            if (!arguments.ContainsKey(required!.GetValue<string>())) return $"arguments: missing required property '{required}'";
        var properties = tool.Schema["properties"]!.AsObject();
        foreach (var (key, value) in arguments)
        {
            if (properties[key] is not JsonObject property) return $"/{key}: unexpected property";
            var kind = value?.GetValueKind();
            var expected = property["type"]!.GetValue<string>();
            var ok = expected switch
            {
                "string" => kind == System.Text.Json.JsonValueKind.String,
                "number" => kind == System.Text.Json.JsonValueKind.Number,
                _ => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            };
            if (!ok) return $"/{key}: expected {expected}";
        }
        return null;
    }

    private static JsonObject Schema(string[] required, params (string Name, string Type, string Description)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(property => KeyValuePair.Create(property.Name,
            (JsonNode?)new JsonObject { ["type"] = property.Type, ["description"] = property.Description }))),
        ["required"] = new JsonArray([.. required.Select(name => (JsonNode)name)]),
        ["additionalProperties"] = false
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    private static (int, JsonNode?) Result(JsonNode? id, JsonNode result) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static (int, JsonNode?) Error(JsonNode? id, int code, string message) =>
        (200, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}
