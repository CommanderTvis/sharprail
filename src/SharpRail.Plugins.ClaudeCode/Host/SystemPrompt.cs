namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The system prompt every session SharpRail starts appends with <c>--append-system-prompt-file</c>: that it runs in a
/// SharpRail workspace rather than a bare shell, which tools to reach for, and where worktrees go. The host rewrites the
/// file on every activation, so the text always matches the running build.
/// </summary>
internal static class SystemPrompt
{
    private const string WorktreesVariable = ClaudeCodeContract.WorktreesVariable;

    public const string Text = $"""
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        Prefer available SharpRail MCP tools; disabled plugins may hide tools:

        - `visualize`: Mermaid diagrams or option comparisons instead of ASCII art/tables when clearer. Each call replaces the live view.
        - `spec_grep` / `spec_get` / `spec_graph`: search the spec graph before exploring code; update affected specs with `spec_create` / `spec_update` / `spec_delete` / `spec_validate`.
        - `blueprint_check`: verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.

        For a task needing a branch or parallel work, create a workspace (git worktree):

            git worktree add "${WorktreesVariable}/<branch>" -b <branch>

        Use the requested base branch. Create worktrees only in `${WorktreesVariable}` (already set); elsewhere, including `.claude/worktrees`, they are absent from SharpRail's rail. Removing a workspace in SharpRail removes its checkout. Never remove worktrees you did not create.

        """;

    /// <summary>
    /// The host state directory. The plugin API hands a plugin no raw state path, so this repeats the host's rule:
    /// <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.
    /// </summary>
    public static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured : Path.Combine(ClaudeEnvironment.Home(), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), ClaudeCodeContract.Id, "system-prompt.md");

    /// <summary>The folder SharpRail creates a project's workspaces in: beside its main worktree.</summary>
    public static string WorktreesDirectoryOf(string projectPath) => Path.TrimEndingDirectorySeparator(projectPath) + "-worktrees";

    public static string Write()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Text);
        return path;
    }
}