using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Codex;

public sealed record CodexLaunchPreset(string Id, string Label)
{
    public string? Subcommand { get; init; }
    public string? Args { get; init; }
    public string? Model { get; init; }
}

public sealed record CodexLaunchOptions(bool Mcp, bool Windows)
{
    /// <summary>Adds SharpRail's developer instructions from <see cref="CodexLaunch.PromptJsonVariable"/>; ignored on Windows.</summary>
    public bool AppendSystemPrompt { get; init; }
    public string? PermissionMode { get; init; }
    public string? Model { get; init; }
    public string? SystemPrompt { get; init; }
    public string? InitialPrompt { get; init; }
    /// <summary>A resume: of that session id, or with a null id of the last one.</summary>
    public bool Resume { get; init; }
    public string? ResumeSessionId { get; init; }
    public CodexLaunchPreset? Preset { get; init; }
}

/// <summary>The launch line, shared by the launcher and the host's revive prefill.</summary>
public static partial class CodexLaunch
{
    /// <summary>The developer instructions file's text as a JSON string, set in every terminal by the host.</summary>
    public const string PromptJsonVariable = "SHARPRAIL_CODEX_PROMPT_JSON";
    /// <summary>The folder SharpRail lists a project's workspaces from, set in every terminal of a project.</summary>
    public const string WorktreesVariable = "SHARPRAIL_WORKTREES_DIR";
    /// <summary>Tags the inherited status URL, so the first hook report says the session got SharpRail's instructions.</summary>
    public const string PromptLaunchPrefix = "THINKRAIL_CODEX_STATUS_URL=\"$THINKRAIL_CODEX_STATUS_URL?thinkrail_prompt=1\"";

    public static bool HasSharpRailPrompt(string command) => command.StartsWith(PromptLaunchPrefix + " ", StringComparison.Ordinal);

    public static string ShellQuote(string word) => SafeWord().IsMatch(word) ? word : "'" + word.Replace("'", "'\\''") + "'";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string ConfigArg(string key, string value) => "-c " + ShellQuote(key + "=" + JsonSerializer.Serialize(value, Json));

    // The MCP URL is minted per terminal by core, so it is left for the shell to expand.
    public const string McpOverride = "-c \"mcp_servers.thinkrail.url=\\\"$THINKRAIL_MCP_URL\\\"\"";

    public static string Line(string command, CodexLaunchOptions options)
    {
        var parts = new List<string> { command.Trim() is { Length: > 0 } trimmed ? trimmed : "codex" };
        if (options.Resume) parts.AddRange(["resume", options.ResumeSessionId is { Length: > 0 } id ? ShellQuote(id) : "--last"]);
        else if (options.Preset?.Subcommand is { } subcommand) parts.Add(subcommand);
        if (options.Mcp && !options.Windows) parts.Add(McpOverride);
        if (options.Model is { Length: > 0 } model) parts.AddRange(["--model", ShellQuote(model)]);
        var permissions = PermissionModes.FirstOrDefault(mode => mode.Id == options.PermissionMode);
        if (permissions?.Args is { } permissionArgs && PermissionModes.All(mode => mode.Id != options.Preset?.Id)) parts.Add(permissionArgs);
        if (options.Preset?.Args is { } args) parts.Add(args);
        var append = options.AppendSystemPrompt && !options.Windows;
        if (append)
        {
            // The shell expands the JSON string; a launcher prompt continues it, so Codex gets one override, not two.
            parts.Add("-c " + (options.SystemPrompt is { Length: > 0 } extra
                ? $"\"developer_instructions=${{{PromptJsonVariable}%\\\"}}\"" + ShellQuote(JsonSerializer.Serialize("\n\n" + extra, Json)[1..])
                : $"\"developer_instructions=${PromptJsonVariable}\""));
        }
        else if (options.SystemPrompt is { Length: > 0 } system) parts.Add(ConfigArg("developer_instructions", system));
        if (options.InitialPrompt is { Length: > 0 } prompt && !options.Resume) parts.Add(ShellQuote(prompt));
        var line = string.Join(" ", parts);
        return append ? PromptLaunchPrefix + " " + line : line;
    }

    /// <summary>The launcher's right-click menu in Codex's own vocabulary, grouped; there is no <c>--yolo</c> preset.</summary>
    public static IReadOnlyList<IReadOnlyList<CodexLaunchPreset>> Menu(Func<string, IReadOnlyList<string>?> enumValues) =>
    [
        .. new IReadOnlyList<CodexLaunchPreset>[]
        {
            [
                new("resume-last", "Continue the last session") { Subcommand = "resume --last" },
                new("resume", "Resume a session…") { Subcommand = "resume" },
                new("fork", "Fork a session…") { Subcommand = "fork" }
            ],
            [.. (enumValues("sandbox_mode") ?? []).Select(mode => new CodexLaunchPreset($"sandbox-{mode}", $"Sandbox: {mode}") { Args = $"-s {mode}" })],
            [new("full-auto", "Workspace write (approval on request)") { Args = "--sandbox workspace-write --ask-for-approval on-request" }],
            [new("search", "With live web search") { Args = "--search" }]
        }.Where(group => group.Count > 0)
    ];

    public static readonly IReadOnlyList<CodexLaunchPreset> PermissionModes =
    [
        new("default", "Use Codex configuration"),
        .. Menu(CodexConfigDocs.EnumValues).SelectMany(group => group).Where(preset => preset.Id.StartsWith("sandbox-", StringComparison.Ordinal) || preset.Id == "full-auto")
    ];

    [GeneratedRegex(@"^[A-Za-z0-9_./~+=:@%-]+$")]
    private static partial Regex SafeWord();
}