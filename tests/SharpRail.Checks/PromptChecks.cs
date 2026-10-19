using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.Host;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Visualize;

namespace SharpRail.Checks;

/// <summary>
/// The instructions agent plugins give sessions follow the enabled plugins and the Code Intelligence setting: a disabled
/// plugin leaves no trace, a plugin enabled alone adds exactly its own text, and the moved wording is unchanged (H20).
/// </summary>
internal static class PromptChecks
{
    private static readonly string[] Optional =
        [SpecDialectManifest.Manifest.Id, BlueprintContract.Manifest.Id, VisualizeContract.Manifest.Id];

    private static readonly string[] Traces = ["spec_", "blueprint_check", "BLUEPRINT.md", "visualize", "context_", "Air Context", "jbcontext"];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static (string Claude, string Codex) Prompts(PluginRuntime runtime) =>
    (
        SystemPrompt.Compose(runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList), runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section)),
        CodexSystemPrompt.Compose(runtime.PromptTexts(CodexManifest.Id, PromptPlacement.ToolList), runtime.PromptTexts(CodexManifest.Id, PromptPlacement.Section)));

    internal static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var state = new HostStateStore(directory);
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = BuiltinPlugins.All });
        await runtime.Start();
        async Task Set(params (string Id, bool On)[] changes)
        {
            await state.ChangeAsync([.. changes.Select(change => HostStateChange.PluginEnabled(change.Id, change.On))]);
            await runtime.Schedule();
        }
        async Task Only(params string[] ids) => await Set([.. Optional.Select(id => (id, ids.Contains(id)))]);

        // Everything optional off: neither agent's instructions name any plugin's tools.
        await Set([.. Optional.Append(AirContextContract.Id).Select(id => (id, false))]);
        var bare = Prompts(runtime);
        foreach (var prompt in new[] { bare.Claude, bare.Codex })
        {
            foreach (var trace in Traces) Require(!prompt.Contains(trace, StringComparison.Ordinal), $"With every optional plugin off the instructions mention \"{trace}\".");
            Require(!prompt.Contains("Prefer available SharpRail MCP tools", StringComparison.Ordinal), "An empty tool list left its introduction behind.");
            Require(prompt.Contains("`set_title`", StringComparison.Ordinal) && prompt.Contains("`workspace_create`", StringComparison.Ordinal) && prompt.Contains("`lsp_navigate`", StringComparison.Ordinal),
                "The host's own text is missing.");
        }

        // Each plugin alone adds exactly its own text.
        var texts = new Dictionary<string, string>
        {
            [SpecDialectManifest.Manifest.Id] = "If this project uses a spec graph and the `spec_*` tools are available, you MUST use them (`spec_graph`, `spec_get`, `spec_grep`) to read the affected specs before changing governed behavior, and maintain and validate (`spec_validate`) them in the same change. Projects without specs need no spec workflow; do not create specs unless asked.",
            [BlueprintContract.Manifest.Id] = "`blueprint_check`: you MUST call it to verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.",
            [VisualizeContract.Manifest.Id] = "`visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view.",
        };
        foreach (var (id, text) in texts)
        {
            // Blueprint depends on the spec dialect, so it never runs without its item.
            var items = id == BlueprintContract.Manifest.Id ? new[] { texts[SpecDialectManifest.Manifest.Id], text } : [text];
            await Only(id == BlueprintContract.Manifest.Id ? [id, SpecDialectManifest.Manifest.Id] : [id]);
            Require(runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList).SequenceEqual(items), $"{id} alone did not contribute exactly its own item.");
            var alone = Prompts(runtime);
            Require(alone.Claude == SystemPrompt.Compose(items, runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section)) && alone.Codex == CodexSystemPrompt.Compose(items, runtime.PromptTexts(CodexManifest.Id, PromptPlacement.Section)),
                $"{id} alone changed the instructions beyond its own item.");
            foreach (var other in texts.Where(other => !items.Contains(other.Value)))
                Require(!alone.Claude.Contains(other.Value, StringComparison.Ordinal), $"{id} alone brought {other.Key}'s text.");
        }

        // All enabled: the old items in the old order, the Code Intelligence text unchanged, after the host's text.
        await Only(Optional);
        var all = Prompts(runtime);
        string[] expected = [texts[VisualizeContract.Manifest.Id], texts[SpecDialectManifest.Manifest.Id], texts[BlueprintContract.Manifest.Id]];
        Require(runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList).SequenceEqual(expected), "The order hints did not keep the old order.");
        Require(all.Claude.Contains("Prefer available SharpRail MCP tools; disabled plugins may hide tools:\n\n- " + string.Join("\n- ", expected) + "\n\nCall `set_title`", StringComparison.Ordinal),
            "The list is not the old wording.");
        Require(all.Claude.EndsWith("\n" + CorePrompt.Text(state.Current.Settings.LanguageServers) + "\n", StringComparison.Ordinal) && all.Claude == Prompts(runtime).Claude, "The Code Intelligence text moved or is unstable.");

        // The strict Code Intelligence, server-start and delegation rules ride on the feature, in both agents.
        foreach (var prompt in new[] { all.Claude, all.Codex })
            foreach (var rule in new[] { "You MUST use `lsp_navigate`", "ONLY for literals", "MUST run `lsp_diagnostics` with `paths`", "is NOT a substitute", "You CAN and MUST enable any missing or stopped language server yourself", "without asking the user", "`operation: \"catalog\"`", "NEVER fall back silently to grep", "is NEVER a reason to skip LSP", "Only a setting the user disabled", "to EVERY `lsp_*` call", "the brief MUST contain", "until the state is ready", "NEVER treat empty results from an initializing server", "the ids of the language servers its task needs", "NEVER abandon LSP because of a malformed call", "its final report MUST state how many `lsp_navigate` and `lsp_diagnostics` calls", "reject a report with none", "NEVER push unless the user asks", "NEVER bypass or disable commit signing" })
                Require(prompt.Contains(rule, StringComparison.Ordinal), $"The instructions lack the rule \"{rule}\".");
        Require(!bare.Claude.Contains("context_search", StringComparison.Ordinal) && !all.Claude.Contains("You MUST call `context_search` first", StringComparison.Ordinal), "Air Context rules appear without the plugin.");

        // The list of servers is the catalog's: every id with its file types, and a server the user disabled is marked, not dropped.
        foreach (var server in LanguageServers.Catalog)
            Require(all.Claude.Contains($"- `{server.Id}`:", StringComparison.Ordinal) && all.Codex.Contains($"- `{server.Id}`:", StringComparison.Ordinal) && all.Claude.Contains(string.Join(" ", server.Extensions), StringComparison.Ordinal), $"The instructions do not list {server.Id}.");
        Require(!all.Claude.Contains("DISABLED by the user", StringComparison.Ordinal) && all.Claude.Contains("\"operation\": \"retry\", \"server\": \"<id>\", \"workspace\"", StringComparison.Ordinal), "The server list or the call example is wrong.");
        await state.ChangeAsync([HostStateChange.Setting("language-server:csharp", "{\"disabled\":true}")]);
        var marked = Prompts(runtime).Codex;
        Require(marked.Contains("`csharp`:", StringComparison.Ordinal) && marked.Split('\n').Single(line => line.StartsWith("- `csharp`:", StringComparison.Ordinal)).Contains("DISABLED by the user", StringComparison.Ordinal)
            && !marked.Split('\n').Single(line => line.StartsWith("- `go`:", StringComparison.Ordinal)).Contains("DISABLED", StringComparison.Ordinal), "A disabled server is not marked in the list.");
        await state.ChangeAsync([HostStateChange.Setting("language-server:csharp", "{}")]);
        Require(Prompts(runtime).Claude == all.Claude, "Enabling the server again did not restore the list.");

        // Language services off: no LSP guidance, and no trace of the tools.
        await state.ChangeAsync([HostStateChange.Setting("lsp-enabled", "false")]);
        foreach (var prompt in new[] { Prompts(runtime).Claude, Prompts(runtime).Codex })
            Require(!prompt.Contains("lsp_", StringComparison.Ordinal) && !prompt.Contains("language.toml", StringComparison.Ordinal) && !prompt.Contains("LSP", StringComparison.Ordinal),
                "With language services off the instructions advertise them.");
        await state.ChangeAsync([HostStateChange.Setting("lsp-enabled", "true")]);
        Require(Prompts(runtime).Claude == all.Claude, "Turning language services back on did not restore the text.");

        // The files are rewritten from the current set for the next terminal.
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", Path.Combine(directory, "files"));
        try
        {
            SystemPrompt.Write(runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList), runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section));
            Require(File.ReadAllText(SystemPrompt.FilePath()) == all.Claude, "The Claude file is not the composed text.");
            await Only();
            SystemPrompt.Write(runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList), runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section));
            Require(File.ReadAllText(SystemPrompt.FilePath()) == bare.Claude, "Disabling the plugins left a stale Claude file.");
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", previous); }
        Console.WriteLine("PASS prompt contributions: disabled plugins leave no trace, each adds only its own text, old wording and order kept, language services gate their guidance, files follow the current set");
    }
}