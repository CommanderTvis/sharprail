namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The system prompt every session SharpRail starts appends with <c>--append-system-prompt-file</c>: that it runs in a
/// SharpRail workspace rather than a bare shell, then the sections the host and enabled plugins contribute (which tools to
/// reach for, where worktrees go, ...), less those the project turns off in <c>.sharprail/instructions.toml</c>. The host
/// regenerates the files on every activation and for every terminal it starts; they are never the user's to edit.
/// </summary>
internal static class SystemPrompt
{
    private const string Header = """
        # You are running inside SharpRail

        This is a SharpRail workspace terminal, not a bare shell; the user sees your files, editor, diff and terminals.

        """;

    private const string ToolsIntro = "Prefer available SharpRail MCP tools; disabled plugins may hide tools:\n\n";

    /// <summary>
    /// The header, each enabled plugin's tool guidance as list items, and the sections the host and the plugins contribute
    /// after it. They come from <c>IPluginHostContext.PromptTexts</c>, so a disabled plugin, a feature that is switched off
    /// or a section the project turns off leaves nothing in it.
    /// </summary>
    public static string Compose(IReadOnlyList<string> tools, IReadOnlyList<string> sections) =>
        Header + (tools.Count == 0 ? "" : "\n" + ToolsIntro + string.Concat(tools.Select(item => "- " + item + "\n")))
        + string.Concat(sections.Select(section => "\n" + section.TrimEnd('\n') + "\n"));

    /// <summary>The text without any plugin's guidance or contributed sections.</summary>
    public static string Text => Compose([], []);

    /// <summary>
    /// The host state directory. The plugin API hands a plugin no raw state path, so this repeats the host's rule:
    /// <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.
    /// </summary>
    public static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured : Path.Combine(ClaudeEnvironment.Home(), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), ClaudeCodeContract.Id, "thinkrail-prompt-claude.md");

    // The hash of the text SharpRail last wrote. A file that no longer matches it was edited by hand, which earlier
    // versions honoured; those edits are kept aside once (IgnoredEditsPath) and the file is regenerated.
    private static string StampPath() => FilePath() + ".sharprail-default";

    /// <summary>Where the user's earlier hand edits of the shared file are kept once SharpRail stopped using them.</summary>
    public static string IgnoredEditsPath() => Path.Combine(Path.GetDirectoryName(FilePath())!, "thinkrail-prompt-claude.custom.md");

    /// <summary>The kept copy of earlier hand edits, or null when there were none.</summary>
    public static string? IgnoredEdits() => File.Exists(IgnoredEditsPath()) ? IgnoredEditsPath() : null;

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The file one session appends: the text composed from the workspace's <paramref name="tools"/> and
    /// <paramref name="sections"/>, so its Code Intelligence part names that project's languages and the project's turned-off
    /// sections are left out, in a file of its own under <c>workspaces/</c>.
    /// </summary>
    public static string WriteFor(string workspace, IReadOnlyList<string> tools, IReadOnlyList<string> sections)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(workspace)))[..16];
        var path = Path.Combine(Path.GetDirectoryName(FilePath())!, "workspaces", key + ".md");
        var text = Compose(tools, sections);
        if (File.Exists(path) && File.ReadAllText(path) == text) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Writes this build's workspace-independent text with the plugins' <paramref name="tools"/> and
    /// <paramref name="sections"/>. A file the user edited under an earlier version is copied to
    /// <see cref="IgnoredEditsPath"/> first (never over an existing copy), so nothing they wrote is lost.
    /// </summary>
    public static string Write(IReadOnlyList<string> tools, IReadOnlyList<string> sections)
    {
        var path = FilePath();
        // The file was system-prompt.md before files were named by agent; an edited one moves over with its stamp.
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "system-prompt.md");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            File.Move(legacy, path);
            if (File.Exists(legacy + ".sharprail-default")) File.Move(legacy + ".sharprail-default", StampPath());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Compose(tools, sections);
        if (File.Exists(path) && File.Exists(StampPath()) && Hash(File.ReadAllText(path)) != File.ReadAllText(StampPath()).Trim() && !File.Exists(IgnoredEditsPath()))
            File.Copy(path, IgnoredEditsPath());
        if (File.Exists(path) && File.ReadAllText(path) == text && File.Exists(StampPath()) && File.ReadAllText(StampPath()).Trim() == Hash(text)) return path;
        File.WriteAllText(path, text);
        File.WriteAllText(StampPath(), Hash(text));
        return path;
    }
}