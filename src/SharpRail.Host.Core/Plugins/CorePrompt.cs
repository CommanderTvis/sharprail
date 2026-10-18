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
        Use `lsp_status` with operation=configuration and server=<id> to inspect workspace settings; configure saves a complete configuration object, and restart saves/applies it to that workspace server. For missing libraries, set server-specific initializationOptions/settings or clangd includePaths/compilerFlags/compilationDatabasePath; preserve existing settings when updating. Saved settings are visible in the LSP pane and copied independently from Default for new workspaces. Prefer `lsp_navigate` over grepping for definitions, types, implementations and references when a language server is available. Use `operation: "hover"` for inferred types, signatures and documentation. Use `operation: "symbols"` for a compact outline, then `operation: "symbolBody", query: "<qualified name>"` to read only the relevant symbol. Use the exact qualified outline name as query for references/implementations/definitions instead of guessing a cursor position; resolve ambiguity with an explicit position. Use scope=dependencies to include library results, then operation=source with the returned URI and source server to read supported dependency sources. Keep the same workspace/session warm; poll initializing status rather than restarting between requests. Navigation includes source excerpts; avoid rereading whole files when those answer the question. Follow pagination/truncation metadata. Use grep for strings, comments, configuration, unknown owners, unsupported languages or incomplete LSP results; do not repeat successful semantic searches with grep without a reason.
        Use `lsp_diagnostics` with `paths` listing every file you edited. Reserve `scope: "workspace"` for changes needing project-wide inspection, including unopened files. Scans use running servers only; inspect per-file checked/pending/skipped/error coverage. Run this after edits and before reporting completion; report pending, partial or unavailable analysis honestly.
        Pass the absolute `workspace` directory you are editing to every LSP call, especially after creating or switching worktrees.
        Each workspace has its own language servers, started on first use and shown in its LSP pane. A subagent inherits your MCP connection but not this prompt, and its shell `cd` does not retarget MCP:
        an LSP call without `workspace` goes to your own workspace, so a subagent working in another worktree would read the wrong files.
        When delegating work in a workspace, write this into the subagent's brief: its absolute workspace path; "Pass that path as `workspace` to every `lsp_navigate`, `lsp_diagnostics` and `lsp_status` call";
        prefer `lsp_navigate` to grep for symbols, run `lsp_diagnostics` with `paths` on every file it edited before reporting, and poll `lsp_status` while a server is initializing instead of restarting it.
        Tell it to pass `workspace` to the other SharpRail tools it uses too. The subagent's calls reuse that workspace's warm servers, so create a workspace per independent task rather than sharing one path between parallel subagents that edit different branches.
        Language tools route by file and support multiple servers in one workspace. Use `lsp_status` with `operation: "servers"` to discover states, `operation: "catalog"` for implementations, and `operation: "retry", server: "<id>"` to start each language needed for a mixed-language scan. You do not need the user to start servers in the UI. Respect disabled settings; otherwise start any server a task needs yourself, at any time and without asking the user, including a different workspace's servers when you pass that `workspace`.
        `lsp_status` reports installation/indexing failures and supports retry. Allow initialization to finish before interpreting diagnostics.

        Project-wide language server choices live in `.sharprail/language.toml` in the project root (`version = 1`, one `[servers.<id>]` table per server: `autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`, `initializationOptions`, `settings`). Edit it directly to share portable options with the team; keep comments and unknown keys, and never put host paths such as a JDK or install location, or secrets, in it. `lsp_status` `configure` changes only this host's local override and does not touch the file. Changes to the file apply the next time a server starts.
        """;

    /// <summary>The host's contributions; <paramref name="languageServicesEnabled"/> reads the Code Intelligence setting.</summary>
    internal static IReadOnlyList<PromptContribution> Contributions(Func<bool> languageServicesEnabled) =>
    [
        new() { Id = "language-services", Text = LanguageServices.TrimEnd('\n'), Placement = PromptPlacement.Section, When = languageServicesEnabled },
    ];
}