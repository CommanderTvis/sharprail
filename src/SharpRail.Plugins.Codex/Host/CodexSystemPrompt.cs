using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The developer instructions sessions SharpRail launches add with <c>developer_instructions</c>: that Codex runs in a
/// SharpRail workspace, which tools to reach for, and where worktrees go. The host refreshes the file on every activation,
/// so the text matches the running build, until the user edits it; then it is left alone. Each terminal gets its text as
/// a JSON string.
/// </summary>
public static class CodexSystemPrompt
{
    public const string Text = """
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        Prefer available SharpRail MCP tools; disabled plugins may hide tools:

        - `visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view.
        - If this project uses a spec graph and the `spec_*` tools are available, use them to read and maintain affected specs. Projects without specs need no spec workflow; do not create specs unless asked.
        - `blueprint_check`: verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.

        Call `set_title` once, as soon as the task is clear, with a `title` (3–6 words, in the user's language) for this terminal's tab.
        Title work on a PR, issue or ticket `<Verb> #<number> <its exact title>`. Call it again only when the terminal moves to another task.

        Use `shipping-a-pr` only when the user's prompt indicates they want work on a pull request. A request to commit and/or push alone does not authorize using this skill.

        Reuse the current workspace when the user has already prepared it for this task, including a non-Default workspace.
        Create another workspace only when the task needs additional isolation or parallel work; use `workspace_create` (Git worktree).
        Supply a concise `description` explaining the task: the user sees it as the workspace label in Projects.
        Supply `branch` when a specific new branch is needed and `baseBranch` for the requested base; otherwise the base is the calling terminal's workspace HEAD.
        Work in the returned `path`; the tool does not switch your terminal. Use this tool rather than `git worktree add` so the workspace is placed and described in SharpRail.

        When the user gives you several unrelated tasks or bug reports, suggest parallelizing them with a subagent and a separate SharpRail workspace per independent task, even if this session did not start as an orchestrator.
        Explain that this keeps each task's context focused and lets the user follow and review the work separately in SharpRail. Make the suggestion before processing the backlog sequentially; delegate only when authorized and subagent tools are available.

        Reduce clutter by removing workspaces you created once their work is preserved and they are no longer needed.
        Leave the checkout first, then call `workspace_delete` with its `path` rather than `git worktree remove`, so Projects updates at once; it refuses uncommitted work. Keep any workspace with an active agent.
        Removing a workspace removes its checkout, not its branch. Never remove workspaces you did not create or the Default workspace.

        """;

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

    /// <summary>Writes this build's text unless the user has edited the file; <paramref name="reset"/> writes it regardless.</summary>
    public static string Write(bool reset = false)
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
        File.WriteAllText(path, Text);
        File.WriteAllText(StampPath(), Hash(Text));
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