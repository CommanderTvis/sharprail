using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// The host's own text for the instructions agent plugins give sessions (H20): what belongs to a core feature rather
/// than to a plugin, each gated on that feature. It comes ahead of every plugin's contributions.
/// </summary>
internal static class CorePrompt
{
    /// <summary>The Code Intelligence guidance: the <c>lsp_*</c> tools and <c>.sharprail/language.toml</c>. It is left out while language services are disabled.</summary>
    internal const string LanguageServices = """
        You MUST use `lsp_navigate` (definition, references, implementations, symbols, symbolBody, hover) for every question about a symbol before you grep or Read whole files. Use `operation: "symbols"` for an outline, then `operation: "symbolBody", query: "<qualified name>"` to read only that symbol; pass the exact qualified outline name as query rather than guessing a cursor position, and an explicit position to resolve ambiguity. Use scope=dependencies, then operation=source with the returned URI and source server, for library code. Navigation includes source excerpts: do not reread whole files they answer. Follow pagination/truncation metadata. A symbol query without `path` searches the workspace: `{"operation": "workspaceSymbols", "query": "Foo", "workspace": "<absolute path>"}`.
        Use grep ONLY for literals, comments, configuration, unsupported languages, or after `lsp_navigate` returned incomplete results; in that case state why in your report. NEVER repeat a successful semantic search with grep.
        You MUST run `lsp_diagnostics` with `paths` listing every file you edited before you declare the work done, and report pending, partial or unavailable analysis honestly (inspect per-file checked/pending/skipped/error coverage). A Release build or compiler run is NOT a substitute. Use `scope: "workspace"` only for project-wide changes.
        The `lsp_*` tools start the language server a file needs and wait for it up to 60 s, then report `initializing`: wait and call again, NEVER treat that as "no results" and NEVER fall back silently to grep. Servers run until their workspace is removed or the user stops them; `lsp_status` with `operation: "servers"` shows states and who started them, `lsp_status {"operation": "retry", "server": "<id>", "workspace": "<absolute workspace path>"}` starts one explicitly. If any call errors, read the error (it names a working call shape), fix the arguments and call again: NEVER abandon LSP because of a malformed call. Only a setting the user disabled (autoStart off, language services off) is respected; then say so in the final report, with the `lsp_status` evidence if a server cannot start.
        {{servers}}
        Pass the absolute `workspace` directory you are editing to EVERY `lsp_*` call, especially after creating or switching worktrees. Each workspace has its own language servers, shown in its LSP pane. A subagent inherits your MCP connection but not this prompt, and its shell `cd` does not retarget MCP: a call without `workspace` goes to your own workspace and reads the wrong files.
        Use `lsp_status` with operation=configuration and server=<id> to inspect workspace settings; configure saves a complete configuration object (read it first and preserve existing values), and restart saves/applies it to that workspace server. For missing libraries, set server-specific initializationOptions/settings or clangd includePaths/compilerFlags/compilationDatabasePath. Saved settings are visible in the LSP pane and copied independently from Default for new workspaces.

        When you brief a subagent for work in a workspace, the brief MUST contain, in short: its absolute workspace path; that it passes that path as `workspace` to every `lsp_*` call and to every other SharpRail tool it uses; the rules above and the ids of the language servers its task needs, copied from the list above (MUST use `lsp_navigate` for symbols before grep, MUST run `lsp_diagnostics` with `paths` on every edited file before reporting, a build is no substitute, the tools start servers on demand and it waits while one initializes, only user-disabled settings are respected); and that its final report MUST state how many `lsp_navigate` and `lsp_diagnostics` calls it made. You MUST check that count and reject a report with none for code work, sending the subagent back to do it. Create a workspace per independent task rather than sharing one path between parallel subagents that edit different branches. When you create a workspace for that work, start the servers its agents need then: `workspace_create({description, languageServers: ["csharp"]})` (`"auto"` starts every language the project has); they start in the background and stay for the life of the workspace.

        Project-wide language server choices live in `.sharprail/language.toml` in the project root (`version = 1`, one `[servers.<id>]` table per server: `autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`, `initializationOptions`, `settings`). Edit it directly to share portable options with the team; keep comments and unknown keys, and never put host paths such as a JDK or install location, or secrets, in it. `lsp_status` `configure` changes only this host's local override and does not touch the file. Changes to the file apply the next time a server starts.
        """;

    /// <summary>
    /// The Code Intelligence text with one line per server of the language catalog (id, implementation, file types), so it
    /// cannot drift from it. A server the user disabled in Settings is marked, never left out.
    /// </summary>
    internal static string Text(string? languageServersJson)
    {
        Dictionary<string, LanguageServerOptions>? options = null;
        try { options = string.IsNullOrWhiteSpace(languageServersJson) ? null : JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(languageServersJson, JsonSerializerOptions.Web); }
        catch (JsonException) { }
        var lines = LanguageServers.Catalog.Select(server =>
            $"- `{server.Id}`: {server.Implementation.Split(" · ")[0]}; {string.Join(" ", server.Extensions)}{(options?.GetValueOrDefault(server.Id)?.Disabled == true ? " [DISABLED by the user in Settings > LSP: do not start it, say so in your report]" : "")}");
        return LanguageServices.TrimEnd('\n').Replace("{{servers}}",
            "Language servers this SharpRail build manages (start with the `retry` call above; any file type listed here HAS a server, so NEVER conclude there is no LSP for it):\n" + string.Join("\n", lines), StringComparison.Ordinal);
    }

    /// <summary>The host's contributions; <paramref name="languageServicesEnabled"/> reads the Code Intelligence setting and <paramref name="languageServers"/> the user's per-server settings.</summary>
    internal static IReadOnlyList<PromptContribution> Contributions(Func<bool> languageServicesEnabled, Func<string?> languageServers) =>
    [
        new() { Id = "language-services", Text = Text(languageServers()), Placement = PromptPlacement.Section, When = languageServicesEnabled },
    ];
}