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
        ## Code intelligence: use `lsp_navigate` and `lsp_diagnostics`, not grep

        This is a large codebase, and grep answers code questions wrongly: it misses implementations, overloads, partial classes, generated code and platform-specific variants. A grep-only agent once missed that macOS uses a different terminal renderer and shipped a fix that never ran there. You MUST use `lsp_navigate` for every question about a symbol (where it is defined, who calls it, what implements it, what it looks like) before you grep or Read whole files. Name the symbol; no file or position is needed:
        - `{"operation": "references", "query": "TerminalView", "workspace": "<absolute path>"}` (also `definition`, `implementation`, `hover`); `Type/Member` or `Namespace.Type.Member` narrows a shared name, and an ambiguous result lists the candidates to pick from.
        - `{"operation": "workspaceSymbols", "query": "Foo", "workspace": "<absolute path>"}` finds a file; `{"operation": "symbols", "path": "src/Foo.cs"}` outlines it; `{"operation": "symbolBody", "path": "src/Foo.cs", "query": "Bar"}` reads one member instead of the file.
        - Explicit `line`/`column` are zero-based; preview prefixes are one-based line numbers. `scope: "dependencies"` and then `operation: "source"` read library code.
        Use grep ONLY for literals, comments, configuration, documentation and non-code files, or after `lsp_navigate` returned incomplete results; then say why in your report.

        You MUST run `lsp_diagnostics` with `paths` listing every file you edited before you declare the work done; a build is NOT a substitute, and partial, pending or skipped coverage is not clean.

        The `lsp_*` tools start the language server a file needs and wait for it up to 60 s, then report `initializing`: wait and call again, NEVER treat that as "no results" and NEVER fall back silently to grep. If a call errors, read the error (it names a working call shape), fix the arguments and call again: NEVER abandon LSP because of a malformed call. Only a setting the user disabled is respected; then say so in your report, with `lsp_status {"operation": "servers"}` evidence. `lsp_status {"operation": "retry", "server": "<id>", "workspace": "<absolute path>"}` starts a server; `configuration`/`configure` read and save its settings (read before replacing).
        {{servers}}

        Pass the absolute `workspace` you are editing to EVERY `lsp_*` call: a subagent's shell `cd` does not retarget MCP, so a call without it reads another checkout.

        Subagents do NOT receive this text. When you brief one for code work, the brief MUST contain its absolute workspace path, that it passes it to every SharpRail tool, the rules above (lsp_navigate before grep, lsp_diagnostics with paths before reporting, wait while servers initialize) with the server ids its task needs, and that its final report MUST state how many `lsp_navigate` and `lsp_diagnostics` calls it made. Check that count and reject a report with none for code work. Give each independent task its own workspace and start its servers there: `workspace_create({description, languageServers: ["csharp"]})`.

        Team-wide server options live in `.sharprail/language.toml` (`version = 1`, one `[servers.<id>]` table: `autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`, `initializationOptions`, `settings`); edit it directly, keep comments and unknown keys, and never put host paths or secrets in it. `lsp_status` `configure` changes only this host's override.
        """;

    /// <summary>
    /// The Code Intelligence text with one line per server of the language catalog (id, implementation, file types), so it
    /// cannot drift from it.
    /// </summary>
    internal static string Text(IReadOnlyList<LanguageServer>? catalog = null)
    {
        var lines = (catalog ?? LanguageServers.Catalog).Select(server =>
            $"- `{server.Id}`: {server.Implementation.Split(" · ")[0]}; {string.Join(" ", server.Extensions)}");
        return LanguageServices.TrimEnd('\n').Replace("{{servers}}",
            "Language servers this SharpRail build manages (start with the `retry` call above; any file type listed here HAS a server, so NEVER conclude there is no LSP for it):\n" + string.Join("\n", lines), StringComparison.Ordinal);
    }

    /// <summary>The host's contributions; <paramref name="languageServicesEnabled"/> reads the Code Intelligence setting and <paramref name="languageServers"/> the user's per-server settings.</summary>
    internal static IReadOnlyList<PromptContribution> Contributions(Func<bool> languageServicesEnabled, Func<IReadOnlyList<LanguageServer>>? catalog = null) =>
    [
        new() { Id = "language-services", Text = Text(catalog?.Invoke()), Placement = PromptPlacement.Section, When = languageServicesEnabled },
    ];
}