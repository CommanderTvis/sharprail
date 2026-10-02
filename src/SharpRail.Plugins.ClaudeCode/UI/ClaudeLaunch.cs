using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.ClaudeCode.UI;

internal sealed record ClaudeLaunchPreset(string Id, string Label, string Args, string? Model = null);

internal sealed record ClaudeModel(string Id, string Label);

/// <summary>
/// The command lines a Claude Code session starts with. Every session SharpRail starts appends its system prompt file
/// and, by default, turns off the CLI's own agent view, each in the syntax of the shell that will read it.
/// </summary>
internal static partial class ClaudeLaunch
{
    /// <summary>The model aliases the claude CLI accepts, one list for the launcher and the running session's chip.</summary>
    public static readonly IReadOnlyList<ClaudeModel> Models = [new("opus", "Opus"), new("fable", "Fable"), new("sonnet", "Sonnet"), new("haiku", "Haiku")];

    /// <summary>The launcher's context menu, in groups shown separated; each entry is the tail of a claude command line.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<ClaudeLaunchPreset>> Menu =
    [
        [
            new("continue", "Continue the last conversation", "--continue"),
            new("resume", "Resume a session…", "--resume"),
            new("resume-fork", "Resume as a new session", "--resume --fork-session"),
            new("teleport", "Teleport a session here…", "--teleport")
        ],
        [.. Models.Select(model => new ClaudeLaunchPreset("model-" + model.Id, model.Label, "--model " + model.Id, model.Id))]
    ];

    /// <summary>Claude Code's own background-agent view, which SharpRail's workspaces and terminals replace.</summary>
    public const string AgentViewVariable = "CLAUDE_CODE_DISABLE_AGENT_VIEW";

    private static bool Windows(HostPlatform? platform) => platform == HostPlatform.Windows;

    /// <summary>One variable, set for this command only, in the syntax of the shell that will read it.</summary>
    public static string WithLaunchEnvironment(string line, HostPlatform? platform = null, string? windowsShell = null)
    {
        if (line.Length == 0) return line;
        if (!Windows(platform)) return $"{AgentViewVariable}=true {line}";
        return windowsShell == "cmd" ? $"set \"{AgentViewVariable}=true\" && {line}" : $"$env:{AgentViewVariable}='true'; {line}";
    }

    public static string LaunchCommand(string command, string args = "")
    {
        var head = command.Trim();
        var tail = args.Trim();
        if (head.Length == 0) return "";
        return tail.Length > 0 ? $"{head} {tail}" : head;
    }

    [GeneratedRegex(@"^[\w./~+=:@%-]+$")]
    private static partial Regex ShellWord();

    /// <summary>A picked path as one shell word: the setting is a command line, so a path with a space must arrive quoted.</summary>
    public static string ShellQuotePath(string path) => ShellWord().IsMatch(path) ? path : $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static string EnvironmentReference(string name, HostPlatform? platform, string? windowsShell) =>
        !Windows(platform) ? $"\"${name}\"" : windowsShell == "cmd" ? $"\"%{name}%\"" : $"$env:{name}";

    /// <summary>
    /// A whole session line: the prompt file first, so it can never be read as the value of <c>--resume</c> or
    /// <c>--teleport</c>, then the caller's arguments, then the agent-view prefix unless the setting turned it off.
    /// </summary>
    public static string SessionCommand(string command, string args, bool disableAgentView = true, bool appendSystemPrompt = true,
        HostPlatform? platform = null, string? windowsShell = null)
    {
        var promptFile = appendSystemPrompt ? $"--append-system-prompt-file {EnvironmentReference(ClaudeCodeContract.PromptFileVariable, platform, windowsShell)}" : "";
        var line = LaunchCommand(command, string.Join(' ', new[] { promptFile, args }.Where(part => part.Length > 0)));
        if (disableAgentView) line = WithLaunchEnvironment(line, platform, windowsShell);
        return !Windows(platform) && line.Length > 0 ? "SHARPRAIL_UI_LAUNCH=claude " + line : line;
    }
}