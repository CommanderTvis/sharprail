using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>Workspace-bound Context tools for SharpRail's authenticated agent namespace; no consent, login or enable operation.</summary>
public static class ContextMcpTools
{
    public static IReadOnlyList<McpServer.McpTool> Create(HostStateStore state, string callingWorkspace)
    {
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(callingWorkspace));
        return [Tool("context_search", "search", "Semantic code discovery in this enabled workspace. Use a descriptive query when you do not know where behavior lives; follow returned file/line references with targeted reads and LSP navigation. Prefer LSP for known symbols. Indexing sends source chunks to JetBrains; only the user can enable it. If disabled, signed out or indexing, report that state and use other tools. Results may lag working-tree changes; LSP/editor buffers remain authoritative for unsaved code."),
            Tool("context_status", "status", "Read this workspace's SharpRail-managed Context state. Never enables indexing, accepts terms or signs in. Use refresh=true for vendor-reported index status."),
            Tool("context_index", "index", "Refresh semantic indexing for this already-enabled workspace. Returns immediately; poll context_status. Source chunks are sent to JetBrains. Cannot enable a workspace or accept terms.")];

        McpServer.McpTool Tool(string name, string operation, string description)
        {
            var schema = JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"properties":{
                  "workspace":{"type":"string","description":"Absolute directory of this SharpRail workspace; cannot target another workspace."}
                }}
                """)!.AsObject();
            var properties = schema["properties"]!.AsObject();
            if (operation == "search")
            {
                properties["query"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 8000, ["description"] = "Detailed natural-language description of the behavior or implementation to locate." };
                properties["path"] = new JsonObject { ["type"] = "string", ["description"] = "Optional workspace-relative path filter. No external directories or repository URLs." };
                properties["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["description"] = "Requested result limit when the native tool supports it; output is always bounded." };
                schema["required"] = new JsonArray("query");
            }
            if (operation == "status") properties["refresh"] = new JsonObject { ["type"] = "boolean", ["description"] = "Ask the vendor CLI for current index status; does not start indexing." };
            return new(name, name.Replace('_', ' '), description, schema, async (args, token) =>
            {
                if (args.Any(item => !properties.ContainsKey(item.Key))) throw new ArgumentException("Unknown Context tool argument.");
                var requested = args["workspace"]?.GetValue<string>() ?? workspace;
                if (!Path.IsPathFullyQualified(requested) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)) != workspace)
                    throw new UnauthorizedAccessException("Context tools are bound to their calling SharpRail workspace.");
                var selectedOperation = operation == "status" && args["refresh"]?.GetValue<bool>() == true ? "refresh" : operation;
                var result = await state.Contexts.ExecuteAsync(new(workspace, selectedOperation, args["query"]?.GetValue<string>() ?? "", args["path"]?.GetValue<string>() ?? "", args["limit"]?.GetValue<int>() ?? 20), token);
                var response = new JsonObject { ["state"] = result.State, ["detail"] = result.Detail, ["enabled"] = result.Enabled };
                if (result.Data.Length > 0)
                {
                    try { response["results"] = JsonNode.Parse(result.Data); }
                    catch (JsonException) { response["results"] = result.Data; }
                }
                return (response.ToJsonString(), result.State is "error" or "requiresAcceptance" or "requiresLogin" or "unavailable");
            });
        }
    }
}