using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The developer instructions sessions SharpRail launches add with <c>developer_instructions</c>: that Codex runs in a
/// SharpRail workspace, which tools to reach for, and where worktrees go. The host refreshes the file on every activation
/// and for every terminal it starts, so the text matches the running build and the enabled plugins, until the user edits
/// it; then it is left alone. Each terminal gets its text as
/// a JSON string.
/// </summary>
public static class CodexSystemPrompt
{
    private const string Template = """
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        Prefer available SharpRail MCP tools; disabled plugins may hide tools:

        {{tool-guidance}}- `visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view.
        - If this project uses a spec graph and the `spec_*` tools are available, use them to read and maintain affected specs. Projects without specs need no spec workflow; do not create specs unless asked.
        - `blueprint_check`: verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.

        Call `set_title` once, as soon as the task is clear, with a `title` (3–6 words, in the user's language) for this terminal's tab.
        An unnamed managed workspace takes the first terminal title as its workspace name and branch slug; choose a concise task title. Manual workspace names are kept.
        Title work on a PR, issue or ticket `<Verb> #<number> <its exact title>`. Call it again only when the terminal moves to another task.

        Use `shipping-a-pr` only when the user's prompt indicates they want work on a pull request. A request to commit and/or push alone does not authorize using this skill.

        Reuse the current workspace when the user has already prepared it for this task, including a non-Default workspace.
        Create another workspace only when the task needs additional isolation or parallel work; use `workspace_create` (Git worktree).
        Supply a concise `description` explaining the task: the user sees it as the workspace label in Projects.
        Supply `branch` when a specific new branch is needed and `baseBranch` for the requested base; otherwise the base is the calling terminal's workspace HEAD.
        Work in the returned `path`; the tool does not switch your terminal. Use this tool rather than `git worktree add` so the workspace is placed and described in SharpRail.

        When the user gives you several unrelated tasks or bug reports, suggest parallelizing them with a subagent and a separate SharpRail workspace per independent task, even if this session did not start as an orchestrator.
        Explain that this keeps each task's context focused and lets the user follow and review the work separately in SharpRail. Make the suggestion before processing the backlog sequentially; delegate only when authorized and subagent tools are available.

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

        Reduce clutter by removing workspaces you created once their work is preserved and they are no longer needed.
        Leave the checkout first, then call `workspace_delete` with its `path` rather than `git worktree remove`, so Projects updates at once; it refuses uncommitted work. Keep any workspace with an active agent.
        Removing a workspace removes its checkout, not its branch. Never remove workspaces you did not create or the Default workspace.

        """;

    private const string GuidanceMarker = "{{tool-guidance}}";

    /// <summary>
    /// The text with each enabled plugin's tool guidance, as list items, ahead of the built-in tool list. The guidance
    /// comes from <c>IPluginHostContext.ToolGuidanceSections</c>, so a disabled plugin leaves nothing in it.
    /// </summary>
    public static string Compose(IReadOnlyList<string> guidance) =>
        Template.Replace(GuidanceMarker, string.Concat(guidance.Select(item => "- " + item + "\n\n")), StringComparison.Ordinal);

    /// <summary>The text without any plugin's guidance.</summary>
    public static string Text => Compose([]);

    /// <summary>The host state directory, by the host's rule: <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.</summary>
    internal static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), CodexManifest.Id, "thinkrail-prompt-codex.md");

    // The hash of the text SharpRail last wrote. While the file still matches it, the file is SharpRail's to refresh;
    // once the user edits it, it is theirs. A file from before the stamp existed was always rewritten, so it is SharpRail's.
    private static string StampPath() => FilePath() + ".sharprail-default";

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Whether the user has changed the file since SharpRail last wrote it.</summary>
    public static bool Edited()
    {
        var path = FilePath();
        if (!File.Exists(path) || !File.Exists(StampPath())) return false;
        return Hash(File.ReadAllText(path)) != File.ReadAllText(StampPath()).Trim();
    }

    /// <summary>Writes this build's text with the plugins' <paramref name="guidance"/> unless the user has edited the file; <paramref name="reset"/> writes it regardless.</summary>
    public static string Write(IReadOnlyList<string> guidance, bool reset = false)
    {
        var path = FilePath();
        // The file was developer-instructions.md before files were named by agent; an edited one moves over with its stamp.
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "developer-instructions.md");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            File.Move(legacy, path);
            if (File.Exists(legacy + ".sharprail-default")) File.Move(legacy + ".sharprail-default", StampPath());
        }
        if (!reset && Edited()) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Compose(guidance);
        if (File.Exists(path) && File.ReadAllText(path) == text && File.Exists(StampPath()) && File.ReadAllText(StampPath()).Trim() == Hash(text)) return path;
        File.WriteAllText(path, text);
        File.WriteAllText(StampPath(), Hash(text));
        return path;
    }

    // Like JSON.stringify: only what JSON requires is escaped, so the shell and Codex see the text itself.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The file's current text as a JSON string, which the launch line's <c>developer_instructions</c> expands.</summary>
    public static string Json()
    {
        try { return JsonSerializer.Serialize(File.ReadAllText(FilePath()), Relaxed); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return JsonSerializer.Serialize(Text, Relaxed); }
    }
}