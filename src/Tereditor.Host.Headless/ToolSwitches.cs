using System.Diagnostics;
using System.Text.Json.Nodes;

using Tereditor.Host.Core;

namespace Tereditor.Host.Headless;

/// <summary>
/// The one decorator over the tool table. It drops the tools the configuration switches off, so <c>tools/list</c> omits them and a
/// call to one is the ordinary unknown-tool error. It narrows the operations of the language tools in their schema and at call
/// time, and it feeds the call log when that is on. With nothing configured it returns the table untouched.
/// </summary>
internal sealed class ToolSwitches(McpConfig config, CallLog? log)
{
    internal IReadOnlyList<McpServer.McpTool> Apply(IReadOnlyList<McpServer.McpTool> tools)
    {
        var result = new List<McpServer.McpTool>();
        foreach (var tool in tools.Where(tool => config.Offers(tool.Name)))
        {
            string[]? allowed = config.Rules.TryGetValue(tool.Name, out var rule) && rule.Operations is { } wanted
                ? [.. Enum(tool).Where(wanted.Contains)] : null;
            if (allowed is null && log is null) { result.Add(tool); continue; }
            var original = Default(tool);
            var fallback = allowed is null ? original : allowed.Contains(original) ? original : config.Rules[tool.Name].Operations!.FirstOrDefault(allowed.Contains);
            var schema = tool.InputSchema;
            if (allowed is not null && tool.InputSchema.DeepClone() is JsonObject narrowed && narrowed["properties"]?["operation"] is JsonObject operation)
            {
                operation["enum"] = new JsonArray([.. allowed.Select(name => (JsonNode?)JsonValue.Create(name))]);
                if (fallback is not null) operation["default"] = fallback;
                schema = narrowed;
            }
            var language = tool.Name.StartsWith("lsp_", StringComparison.Ordinal);
            var call = tool.Call;
            result.Add(tool with
            {
                InputSchema = schema,
                Call = async (arguments, token) =>
                {
                    var timer = Stopwatch.StartNew();
                    var seq = log?.NextSequence() ?? 0;
                    var requested = arguments["operation"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                    var effective = language ? requested ?? fallback : null;
                    (string Text, bool Error) reply;
                    try
                    {
                        if (allowed is not null && effective is not null && !allowed.Contains(effective))
                            reply = ($"Operation \"{effective}\" is not available. Use one of: {string.Join(", ", allowed)}.", true);
                        else
                        {
                            if (allowed is not null && requested is null && fallback is not null) arguments["operation"] = fallback;
                            reply = await call(arguments, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        log?.Record(seq, tool.Name, effective, timer.Elapsed, true, null, "cancelled", arguments);
                        throw;
                    }
                    catch (Exception error)
                    {
                        log?.Record(seq, tool.Name, effective, timer.Elapsed, true, null, error.Message, arguments);
                        throw;
                    }
                    log?.Record(seq, tool.Name, effective, timer.Elapsed, reply.Error, reply.Text, reply.Error ? reply.Text : null, arguments);
                    return reply;
                }
            });
        }
        return result;
    }

    private static string[] Enum(McpServer.McpTool tool) =>
        tool.InputSchema["properties"]?["operation"]?["enum"] is JsonArray values ? [.. values.Select(value => value!.GetValue<string>())] : [];

    private static string? Default(McpServer.McpTool tool) => tool.InputSchema["properties"]?["operation"]?["default"] is JsonValue value ? value.GetValue<string>() : null;

    /// <summary>The initialize text, generated from the tools that exist, so an arm never hears about a tool it does not have.</summary>
    internal static string Instructions(IReadOnlyList<McpServer.McpTool> offered, string languages = "", string terms = "", IReadOnlyList<string>? enabled = null, string root = "")
    {
        var names = offered.Select(tool => tool.Name).ToHashSet();
        if (names.Count == 0) return "tereditor with every tool switched off for this run.";
        List<string> areas = [];
        if (names.Any(name => name.StartsWith("lsp_", StringComparison.Ordinal))) areas.Add("language");
        if (names.Any(name => name.StartsWith("spec_", StringComparison.Ordinal))) areas.Add("spec");
        if (names.Contains("blueprint_check")) areas.Add("Blueprint");
        var text = $"tereditor's {(areas.Count < 2 ? string.Concat(areas) : string.Join(", ", areas[..^1]) + " and " + areas[^1])} tools for the one workspace this server runs in" + (root.Length > 0 ? $" (workspace root: {root})" : "") + ". Paths are relative to its root or absolute inside it. The optional workspace argument is ignored.";
        var navigate = names.Contains("lsp_navigate");
        var diagnostics = names.Contains("lsp_diagnostics");
        if (navigate) text += " Use lsp_navigate instead of grep to find symbols, implementations and references" + (diagnostics ? ", and lsp_diagnostics to check edited files." : ".");
        else if (diagnostics) text += " Use lsp_diagnostics to check edited files.";
        var changing = new[] { "lsp_code_actions", "lsp_rename", "lsp_format" }.Where(names.Contains).ToArray();
        if (changing.Length > 0) text += $" For semantic changes (quick fixes, rename, format) use {string.Join(", ", changing)} instead of hand-editing; they preview before they write.";
        if ((navigate || diagnostics) && languages.Length > 0)
            text += $" Language servers here: {languages}. Each downloads on first use, so a call may answer initializing: wait and call again, never treat it as no results. Other file types have no server: use grep and Read.";
        if ((navigate || diagnostics) && terms.Length > 0 && names.Contains("accept_terms")) text += " " + terms;
        if (enabled is not null && new[] { "spec", "blueprint" }.Where(group => !enabled.Contains(group)).ToArray() is { Length: > 0 } off)
            text += $" The {string.Join(" and ", off)} tools are off by default: start the server with TEREDITOR_TOOLS_ENABLE={string.Join(",", off)} to offer them.";
        return text;
    }
}