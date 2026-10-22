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
            [VisualizeContract.Manifest.Id] = "`visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view; `visualize_read` returns what it shows and what the user selected.",
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
        Require(all.Claude.EndsWith("\n" + CorePrompt.Text() + "\n", StringComparison.Ordinal) && all.Claude == Prompts(runtime).Claude, "The Code Intelligence text moved or is unstable.");

        // The strict Code Intelligence, server-start and delegation rules ride on the feature, in both agents.
        foreach (var prompt in new[] { all.Claude, all.Codex })
            foreach (var rule in new[] { "You MUST use `lsp_navigate`", "ONLY for literals", "MUST run `lsp_diagnostics` with `paths`", "is NOT a substitute", "NEVER fall back silently to grep", "Only a setting the user disabled", "to EVERY `lsp_*` call", "the brief MUST contain", "The `lsp_*` tools start the language server a file needs and wait for it up to 60 s", "NEVER treat that as \"no results\"", "languageServers: [", "\"operation\": \"workspaceSymbols\"", "with the server ids its task needs", "Subagents do NOT receive this text", "macOS uses a different terminal renderer", "{\"operation\": \"references\", \"query\": ", "zero-based", "misses implementations, overloads, partial classes", "NEVER abandon LSP because of a malformed call", "its final report MUST state how many `lsp_navigate` and `lsp_diagnostics` calls", "reject a report with none", "NEVER push unless the user asks", "NEVER bypass or disable commit signing" })
                Require(prompt.Contains(rule, StringComparison.Ordinal), $"The instructions lack the rule \"{rule}\".");
        Require(CorePrompt.LanguageServices.Length < 3_600, "The Code Intelligence text outgrew its budget: " + CorePrompt.LanguageServices.Length);
        Require(!bare.Claude.Contains("context_search", StringComparison.Ordinal) && !all.Claude.Contains("You MUST call `context_search` first", StringComparison.Ordinal), "Air Context rules appear without the plugin.");

        // Without a workspace no server is named and the catalog is never listed; `lsp_status servers` is.
        Require(Listed(all.Claude).Length == 0 && Listed(all.Codex).Length == 0 && all.Claude.Contains("`lsp_status {\"operation\": \"servers\"}` lists them", StringComparison.Ordinal),
            "The workspace-independent text lists servers.");
        Require(!all.Claude.Contains("DISABLED by the user", StringComparison.Ordinal) && all.Claude.Contains("\"operation\": \"retry\", \"server\": \"<id>\", \"workspace\"", StringComparison.Ordinal), "The server list or the call example is wrong.");
        await ProjectTexts(directory, state, runtime);
        await state.ChangeAsync([HostStateChange.Setting("language-server:csharp", "{\"disabled\":true}")]);
        Require(Prompts(runtime).Claude == all.Claude, "A retired per-server disabled flag still changes the server list.");
        await state.ChangeAsync([HostStateChange.Setting("language-server:csharp", "{}")]);

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
        Console.WriteLine("PASS prompt contributions: disabled plugins leave no trace, each adds only its own text, old wording and order kept, language services gate their guidance, files follow the current set, Code Intelligence names each project's languages");
    }

    /// <summary>The server ids the text lists as the project's, in order.</summary>
    private static string[] Listed(string text) =>
        [.. text.Split('\n').Where(line => line.StartsWith("- `", StringComparison.Ordinal) && line.Contains("` (", StringComparison.Ordinal) && line.EndsWith(" files", StringComparison.Ordinal) || line.EndsWith(" file", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])];

    /// <summary>
    /// The Code Intelligence text for a workspace names only that project's servers, ranked by file count, with examples
    /// on its own symbols; the first session never waits for the census and gets the workspace-independent text.
    /// </summary>
    private static async Task ProjectTexts(string directory, HostStateStore state, PluginRuntime runtime)
    {
        string Project(string name, params (string Path, string Text)[] files)
        {
            var root = Path.Combine(directory, "projects", name);
            foreach (var (path, text) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
                File.WriteAllText(Path.Combine(root, path), text);
            }
            return root;
        }
        string Section(string workspace) => runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section, workspace)[0];

        var csharp = Project("csharp",
            ("src/Market/OrderBook.cs", "namespace Market;\n\npublic sealed partial class OrderBook\n{\n    public void Match(Order order)\n    {\n    }\n}\n"),
            ("src/Market/OrderBook.Pricing.cs", "namespace Market;\n\npublic sealed partial class OrderBook\n{\n    internal decimal Spread() => 0;\n}\n"),
            ("src/Market/Order.cs", "namespace Market;\n\npublic sealed record Order(decimal Price);\n"),
            ("src/Program.cs", "System.Console.WriteLine();\n"),
            ("README.md", "# Market\n"));
        var typescript = Project("typescript",
            ("src/cart.ts", "export class ShoppingCart {\n  private items: string[] = [];\n\n  addItem(item: string): void {\n    this.items.push(item);\n  }\n}\n"),
            ("src/cart.view.ts", "import { ShoppingCart } from './cart';\nexport const view = new ShoppingCart();\n"),
            ("src/index.ts", "export * from './cart';\n"),
            ("styles/cart.css", ".cart { color: red; }\n"),
            ("styles/base.css", "body { margin: 0; }\n"));
        var c = Project("c",
            ("src/parser.c", "#include \"parser.h\"\n\nint parse_config(const char *path)\n{\n    return path != 0;\n}\n"),
            ("src/parser.h", "int parse_config(const char *path);\n"),
            ("src/main.c", "int main(void) { return 0; }\n"));

        foreach (var workspace in new[] { csharp, typescript, c })
        {
            // Launch never waits: before the census there is no profile and the text is the generic one for this path.
            Require(state.ProjectLanguages(workspace) is null, "A launch waited for the language census.");
            var first = Section(workspace);
            Require(Listed(first).Length == 0 && first.Contains(workspace, StringComparison.Ordinal) && first.Contains("\"query\": \"TypeName\"", StringComparison.Ordinal),
                "Before the census the text is not the generic one: " + first);
            await state.ProjectLanguagesPending(workspace);
        }

        var cs = Section(csharp);
        Require(Listed(cs).SequenceEqual(["csharp"]), "A C#-only project lists other servers: " + string.Join(", ", Listed(cs)));
        Require(cs.Contains("- `csharp` (C#: .cs", StringComparison.Ordinal) && cs.Contains("\"operation\": \"references\", \"query\": \"OrderBook\"", StringComparison.Ordinal) &&
            cs.Contains("\"operation\": \"symbolBody\", \"path\": \"src/Market/OrderBook.cs\", \"query\": \"Match\"", StringComparison.Ordinal) && cs.Contains("languageServers: [\"csharp\"]", StringComparison.Ordinal),
            "The C# project's examples do not use its symbols: " + cs);

        var ts = Section(typescript);
        Require(Listed(ts).SequenceEqual(["typescript", "css"]), "A TypeScript and CSS project lists: " + string.Join(", ", Listed(ts)));
        Require(ts.Contains("Examples in this project (JavaScript / TypeScript)", StringComparison.Ordinal) && ts.Contains("\"query\": \"ShoppingCart\"", StringComparison.Ordinal) &&
            ts.Contains("\"path\": \"src/cart.ts\", \"query\": \"addItem\"", StringComparison.Ordinal), "The TypeScript project's examples do not use its symbols: " + ts);

        var clang = Section(c);
        Require(Listed(clang).SequenceEqual(["clangd"]) && clang.Contains("\"query\": \"parse_config\"", StringComparison.Ordinal), "A C project is not clangd with its own function: " + clang);

        // The full catalog is never emitted, for a project or without one.
        foreach (var text in new[] { cs, ts, clang, Section(Path.Combine(directory, "projects", "missing")) })
            Require(Listed(text).Length <= ProjectLanguageProfile.Shown && LanguageServers.Catalog.Count(server => text.Contains("`" + server.Id + "` (", StringComparison.Ordinal)) <= ProjectLanguageProfile.Shown,
                "The text lists the server catalog.");

        // The agents' files: Claude appends a per-workspace file, Codex's launch text is composed for the workspace.
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", Path.Combine(directory, "project-files"));
        try
        {
            var file = SystemPrompt.WriteFor(csharp, runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList, csharp), runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section, csharp));
            Require(file != SystemPrompt.FilePath() && File.ReadAllText(file).Contains("\"query\": \"OrderBook\"", StringComparison.Ordinal), "Claude's session file is not the workspace's text.");
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(CodexSystemPrompt.Json(runtime.PromptTexts(CodexManifest.Id, PromptPlacement.ToolList, c), runtime.PromptTexts(CodexManifest.Id, PromptPlacement.Section, c)))!;
            Require(json.Contains("- `clangd` (", StringComparison.Ordinal), "Codex's launch text is not the workspace's.");
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", previous); }
    }
}