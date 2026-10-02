using System.Text.Encodings.Web;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The developer instructions sessions SharpRail launches add with <c>developer_instructions</c>: that Codex runs in a
/// SharpRail workspace, which tools to reach for, and where worktrees go. The host rewrites the file on every activation,
/// so the text always matches the running build, and hands each terminal its text as a JSON string.
/// </summary>
public static class CodexSystemPrompt
{
    private const string WorktreesVariable = CodexLaunch.WorktreesVariable;

    public const string Text = $"""
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        Prefer available SharpRail MCP tools; disabled plugins may hide tools:

        - `visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view.
        - `spec_grep` / `spec_get` / `spec_graph`: search the spec graph before exploring code; update affected specs with `spec_create` / `spec_update` / `spec_delete` / `spec_validate`.
        - `blueprint_check`: verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.

        For a task needing a branch or parallel work, create a workspace (git worktree):

            git worktree add "${WorktreesVariable}/<branch>" -b <branch>

        Use the requested base branch. Create worktrees only in `${WorktreesVariable}` (already set); elsewhere they are absent from SharpRail's rail. Removing a workspace in SharpRail removes its checkout. Never remove worktrees you did not create.

        """;

    /// <summary>The host state directory, by the host's rule: <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.</summary>
    private static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), CodexManifest.Id, "developer-instructions.md");

    /// <summary>The folder SharpRail creates a project's workspaces in: beside its main worktree.</summary>
    public static string WorktreesDirectoryOf(string projectPath) => Path.TrimEndingDirectorySeparator(projectPath) + "-worktrees";

    public static void Write()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Text);
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