using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The editable command a revived Codex tab is offered: the configured invocation with its supported options kept,
/// prompts, images and old selectors dropped, exactly one resume selector, and a fresh MCP override.
/// </summary>
public static partial class CodexResume
{
    private static readonly HashSet<string> ValueOptions =
    [
        "-c", "--config", "-m", "--model", "-p", "--profile", "-s", "--sandbox", "-a", "--ask-for-approval", "-C", "--cd", "--add-dir",
        "--enable", "--disable", "--local-provider", "--remote", "--remote-auth-token-env"
    ];

    private static readonly HashSet<string> Switches =
    [
        "--oss", "--search", "--no-alt-screen", "--approve-for-me", "--yolo", "--dangerously-bypass-approvals-and-sandbox",
        "--dangerously-bypass-hook-trust", "--strict-config", "--include-non-interactive"
    ];

    /// <summary>Whether a nonempty rollout for this UUID exists under <c>$CODEX_HOME/sessions</c>; archived sessions do not count.</summary>
    public static bool SessionExists(string sessionId, string? home = null)
    {
        if (!Uuid().IsMatch(sessionId)) return false;
        try
        {
            var sessions = Path.Combine(home ?? CodexConfig.Home(), "sessions");
            return Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, $"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories)
                .Any(path => new FileInfo(path).Length > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? Invocation(string command)
    {
        // SharpRail's own instructions are regenerated from the current setting, never carried over.
        var appended = CodexLaunch.HasSharpRailPrompt(command);
        if (appended) command = command[(CodexLaunch.PromptLaunchPrefix.Length + 1)..];
        if (command.StartsWith(CodexLaunch.UiLaunchPrefix + " ", StringComparison.Ordinal)) command = command[(CodexLaunch.UiLaunchPrefix.Length + 1)..];
        var tokens = new List<string>();
        var offset = 0;
        while (offset < command.Length)
        {
            if (char.IsWhiteSpace(command[offset])) { offset++; continue; }
            var match = Word().Match(command, offset);
            if (!match.Success || match.Index != offset) return null;
            tokens.Add(match.Value);
            offset += match.Length;
        }
        if (tokens.Count == 0 || tokens[0].StartsWith('-') || Assignment().IsMatch(tokens[0])) return null;
        var kept = new List<string> { tokens[0] };
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token == "--") break;
            if (!token.StartsWith('-')) continue;
            if (token is "--last" or "--all" or "--worktree") continue;
            if (token is "-i" or "--image") { index++; continue; }
            if (token.StartsWith("--image=", StringComparison.Ordinal)) continue;
            var key = token.Split('=', 2)[0];
            if (Switches.Contains(token)) kept.Add(token);
            else if (ValueOptions.Contains(key))
            {
                var equals = token.IndexOf('=');
                var value = equals < 0 ? (++index < tokens.Count ? tokens[index] : null) : token[(equals + 1)..];
                if (string.IsNullOrEmpty(value) || value.StartsWith('-')) return null;
                if (key is "-c" or "--config" && McpOverride().IsMatch(value)) continue;
                if (appended && key is "-c" or "--config" && value.Contains(CodexLaunch.PromptJsonVariable, StringComparison.Ordinal) &&
                    DeveloperInstructions().IsMatch(value))
                    continue;
                kept.Add(token);
                if (equals < 0) kept.Add(value);
            }
            else return null;
        }
        return string.Join(" ", kept);
    }

    /// <summary>The revive command, or null when the configured command cannot be rebuilt without damaging it.</summary>
    public static string? Command(string command, string? sessionId, bool mcp, bool windows, string? permissionMode, string? home = null,
        bool appendSystemPrompt = false, bool uiLaunch = false)
    {
        if (Invocation(command) is not { } invocation) return null;
        return CodexLaunch.Line(invocation, new(mcp, windows)
        {
            AppendSystemPrompt = appendSystemPrompt,
            UiLaunch = uiLaunch,
            PermissionMode = permissionMode,
            Resume = true,
            ResumeSessionId = sessionId is not null && SessionExists(sessionId, home) ? sessionId : null
        });
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    [GeneratedRegex("""(?:[^\s"'\\;|&<>`$()]|\\.|"(?:[^"\\]|\\.)*"|'[^']*')+""")]
    private static partial Regex Word();

    [GeneratedRegex(@"^\w+=")]
    private static partial Regex Assignment();

    [GeneratedRegex("""^['"]?mcp_servers\.thinkrail\.url=""")]
    private static partial Regex McpOverride();

    [GeneratedRegex("""^['"]?developer_instructions=""")]
    private static partial Regex DeveloperInstructions();
}