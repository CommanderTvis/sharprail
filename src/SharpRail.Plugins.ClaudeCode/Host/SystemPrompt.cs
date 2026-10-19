namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The system prompt every session SharpRail starts appends with <c>--append-system-prompt-file</c>: that it runs in a
/// SharpRail workspace rather than a bare shell, which tools to reach for, and where worktrees go. The host refreshes the
/// file on every activation and for every terminal it starts, so the text matches the running build and the plugins enabled, until the user edits it; then it is left alone.
/// </summary>
internal static class SystemPrompt
{
    private const string Template = """
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        {{tool-guidance}}Call `set_title` once, as soon as the task is clear, with a `title` (3–6 words, in the user's language) for this terminal's tab.
        An unnamed managed workspace takes the first terminal title as its workspace name and branch slug; choose a concise task title. Manual workspace names are kept.
        Title work on a PR, issue or ticket `<Verb> #<number> <its exact title>`. Call it again only when the terminal moves to another task.

        Use `shipping-a-pr` only when the user's prompt indicates they want work on a pull request. A request to commit and/or push alone does not authorize using this skill.

        Reuse the current workspace when the user has already prepared it for this task, including a non-Default workspace.
        Create another workspace only when the task needs additional isolation or parallel work; then you MUST use `workspace_create` (Git worktree).
        Supply a concise `description` explaining the task: the user sees it as the workspace label in Projects.
        Supply `branch` when a specific new branch is needed and `baseBranch` for the requested base; otherwise the base is the calling terminal's workspace HEAD.
        Work in the returned `path`; the tool does not switch your terminal. NEVER use `git worktree add`; use this tool so the workspace is placed and described in SharpRail.

        When the user gives you several unrelated tasks or bug reports, suggest parallelizing them with a subagent and a separate SharpRail workspace per independent task, even if this session did not start as an orchestrator.
        Explain that this keeps each task's context focused and lets the user follow and review the work separately in SharpRail. Make the suggestion before processing the backlog sequentially; delegate only when authorized and subagent tools are available.

        Reduce clutter by removing workspaces you created once their work is preserved and they are no longer needed.
        Leave the checkout first, then call `workspace_delete` with its `path` rather than `git worktree remove`, so Projects updates at once; it refuses uncommitted work. Keep any workspace with an active agent.
        Removing a workspace removes its checkout, not its branch. Never remove workspaces you did not create or the Default workspace.

        NEVER push unless the user asks. NEVER bypass or disable commit signing: if signing or the SSH agent does not respond, the user is away; stop and report instead of working around it.

        New workspaces can start warm: `.sharprail/workspace.toml` in the project root (committed, yours to edit) lists git-ignored paths cloned from the Default workspace into workspaces created afterwards.
        Schema (TOML): `version = 1`, then `[clone]` with `include = ["node_modules", ".venv", "**/target"]` and optional `exclude = ["**/target/tmp"]`. Patterns are root-relative: `*`/`?` stay inside a segment, `**` spans segments, a name without `/` matches at any depth.
        Only ignored, untracked paths are cloned, and without the file nothing is. You MUST add an entry when you introduce a dependency directory or build cache worth sharing; the edit does not affect existing workspaces.

        """;

    private const string GuidanceMarker = "{{tool-guidance}}";

    private const string ToolsIntro = "Prefer available SharpRail MCP tools; disabled plugins may hide tools:\n\n";

    /// <summary>
    /// The text with each enabled plugin's tool guidance as list items ahead of the host's text, and the sections the host
    /// and the plugins contribute after it. They come from <c>IPluginHostContext.PromptTexts</c>, so a disabled plugin, or a
    /// feature that is switched off, leaves nothing in it.
    /// </summary>
    public static string Compose(IReadOnlyList<string> tools, IReadOnlyList<string> sections) =>
        Template.Replace(GuidanceMarker, tools.Count == 0 ? "" : ToolsIntro + string.Concat(tools.Select(item => "- " + item + "\n")) + "\n", StringComparison.Ordinal)
        + string.Concat(sections.Select(section => "\n" + section + "\n"));

    /// <summary>The text without any plugin's guidance or contributed sections.</summary>
    public static string Text => Compose([], []);

    /// <summary>
    /// The host state directory. The plugin API hands a plugin no raw state path, so this repeats the host's rule:
    /// <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.
    /// </summary>
    public static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured : Path.Combine(ClaudeEnvironment.Home(), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), ClaudeCodeContract.Id, "thinkrail-prompt-claude.md");

    // The hash of the text SharpRail last wrote. While the file still matches it, the file is SharpRail's to refresh;
    // once the user edits it, it is theirs. A file from before the stamp existed was always rewritten, so it is SharpRail's.
    private static string StampPath() => FilePath() + ".sharprail-default";

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Whether the user has changed the file since SharpRail last wrote it.</summary>
    public static bool Edited()
    {
        var path = FilePath();
        if (!File.Exists(path) || !File.Exists(StampPath())) return false;
        return Hash(File.ReadAllText(path)) != File.ReadAllText(StampPath()).Trim();
    }

    /// <summary>Writes this build's text with the plugins' <paramref name="tools"/> and <paramref name="sections"/> unless the user has edited the file; <paramref name="reset"/> writes it regardless.</summary>
    public static string Write(IReadOnlyList<string> tools, IReadOnlyList<string> sections, bool reset = false)
    {
        var path = FilePath();
        // The file was system-prompt.md before files were named by agent; an edited one moves over with its stamp.
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "system-prompt.md");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            File.Move(legacy, path);
            if (File.Exists(legacy + ".sharprail-default")) File.Move(legacy + ".sharprail-default", StampPath());
        }
        if (!reset && Edited()) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Compose(tools, sections);
        if (File.Exists(path) && File.ReadAllText(path) == text && File.Exists(StampPath()) && File.ReadAllText(StampPath()).Trim() == Hash(text)) return path;
        File.WriteAllText(path, text);
        File.WriteAllText(StampPath(), Hash(text));
        return path;
    }
}