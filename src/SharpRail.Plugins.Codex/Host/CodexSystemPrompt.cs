using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The developer instructions sessions SharpRail launches add with <c>developer_instructions</c>: that Codex runs in a
/// SharpRail workspace, then the sections the host and enabled plugins contribute, less those the project turns off in
/// <c>.sharprail/instructions.toml</c>. The host regenerates the file on every activation and the text for every terminal
/// it starts; neither is the user's to edit. Each terminal gets its text as a JSON string.
/// </summary>
public static class CodexSystemPrompt
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

    /// <summary>The host state directory, by the host's rule: <c>SHARPRAIL_STATE_DIR</c>, else <c>~/.sharprail</c>.</summary>
    internal static string StateDirectory() =>
        Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail");

    public static string FilePath() => Path.Combine(StateDirectory(), CodexManifest.Id, "thinkrail-prompt-codex.md");

    // The hash of the text SharpRail last wrote. A file that no longer matches it was edited by hand, which earlier
    // versions honoured; those edits are kept aside once (IgnoredEditsPath) and the file is regenerated.
    private static string StampPath() => FilePath() + ".sharprail-default";

    /// <summary>Where the user's earlier hand edits of the file are kept once SharpRail stopped using them.</summary>
    public static string IgnoredEditsPath() => Path.Combine(Path.GetDirectoryName(FilePath())!, "thinkrail-prompt-codex.custom.md");

    /// <summary>The kept copy of earlier hand edits, or null when there were none.</summary>
    public static string? IgnoredEdits() => File.Exists(IgnoredEditsPath()) ? IgnoredEditsPath() : null;

    private static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Writes this build's workspace-independent text with the plugins' <paramref name="tools"/> and
    /// <paramref name="sections"/>. A file the user edited under an earlier version is copied to
    /// <see cref="IgnoredEditsPath"/> first (never over an existing copy), so nothing they wrote is lost.
    /// </summary>
    public static string Write(IReadOnlyList<string> tools, IReadOnlyList<string> sections)
    {
        var path = FilePath();
        // The file was developer-instructions.md before files were named by agent; an edited one moves over with its stamp.
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "developer-instructions.md");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            File.Move(legacy, path);
            if (File.Exists(legacy + ".sharprail-default")) File.Move(legacy + ".sharprail-default", StampPath());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Compose(tools, sections);
        if (File.Exists(path) && File.Exists(StampPath()) && Hash(File.ReadAllText(path)) != File.ReadAllText(StampPath()).Trim() && !File.Exists(IgnoredEditsPath()))
            File.Copy(path, IgnoredEditsPath());
        // Always the composed text: a hand edit is overwritten, atomically, and the file stays read-only.
        GeneratedInstructions.Write(path, text);
        if (!File.Exists(StampPath()) || File.ReadAllText(StampPath()).Trim() != Hash(text)) File.WriteAllText(StampPath(), Hash(text));
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

    /// <summary>
    /// The text one session gets as a JSON string: composed from the workspace's <paramref name="tools"/> and
    /// <paramref name="sections"/>, so the Code Intelligence part names that project's languages and the project's
    /// turned-off sections are left out.
    /// </summary>
    public static string Json(IReadOnlyList<string> tools, IReadOnlyList<string> sections) => JsonSerializer.Serialize(Compose(tools, sections), Relaxed);
}