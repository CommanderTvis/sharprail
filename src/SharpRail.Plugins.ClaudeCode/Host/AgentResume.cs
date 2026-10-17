using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>Rebuilds the command that started a Claude session so a revived terminal resumes it instead of starting over.</summary>
internal static partial class AgentResume
{
    private static readonly HashSet<string> ResumeFlags = ["--resume", "-r"];
    private static readonly HashSet<string> ContinueFlags = ["--continue", "-c"];
    // Flags of the claude CLI that take no value, so the word after them is not theirs.
    private static readonly HashSet<string> BooleanFlags =
        ["--chrome", "--dangerously-skip-permissions", "--debug", "--fork-session", "--ide", "--print", "-p", "--verbose", "--version", "-v", "--help", "-h"];

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    public static bool IsSessionId(string value) => Uuid().IsMatch(value);

    /// <summary>
    /// The invocation asked to resume a session. A recorded command may already carry its own <c>--resume</c> or
    /// <c>--continue</c> from a previous restore; both are dropped rather than appended to, and every other choice the
    /// user made is kept in place.
    /// </summary>
    public static string? ResumeCommand(string command, string sessionId, string? model = null, string? effort = null, string? pluginDirectory = null) =>
        IsSessionId(sessionId) && Invocation(command, model, effort, pluginDirectory) is { } kept ? $"{kept} --resume {sessionId}" : null;

    /// <summary>The same invocation asked to pick up the latest conversation in its directory itself.</summary>
    public static string? ContinueCommand(string command, string? model = null, string? effort = null, string? pluginDirectory = null) => Invocation(command, model, effort, pluginDirectory) is { } kept ? $"{kept} --continue" : null;

    /// <summary>
    /// The executable and its flags, nothing else: a positional prompt is dropped, because the command comes from the
    /// process table unquoted and a resumed session must not be handed the opening prompt again. A word after a flag is
    /// kept as that flag's value unless the flag is known to take none.
    /// </summary>
    private static string? Invocation(string command, string? model, string? effort, string? pluginDirectory)
    {
        var words = command.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var kept = new List<string> { words[0] };
        for (var index = 1; index < words.Length; index++)
        {
            var word = words[index];
            if (word == "--") break;
            if (pluginDirectory is not null && word.Split('=', 2)[0] == "--plugin-dir")
            {
                var inline = word.IndexOf('=');
                var candidate = inline >= 0 ? word[(inline + 1)..] : "";
                var end = inline >= 0 ? index : index + 1;
                var removed = false;
                for (; end < words.Length; end++)
                {
                    if (end != index)
                    {
                        if (words[end].StartsWith('-')) break;
                        candidate = candidate.Length == 0 ? words[end] : candidate + " " + words[end];
                    }
                    var path = candidate.Trim('\'', '"').Replace('\\', '/');
                    if (path == pluginDirectory.Replace('\\', '/') ||
                        path.EndsWith("/plugins/claude-code/assets/marketplace/claude-plugin", StringComparison.Ordinal) ||
                        path.EndsWith("/.sharprail/claude-code/marketplace/claude-plugin", StringComparison.Ordinal))
                    { index = end; removed = true; break; }
                }
                if (removed) continue;
            }
            if (ContinueFlags.Contains(word)) continue;
            if (ResumeFlags.Contains(word))
            {
                // --resume with nothing after it is the interactive picker, not an id to drop.
                if (index + 1 < words.Length && !words[index + 1].StartsWith('-')) index++;
                continue;
            }
            if (!word.StartsWith('-')) continue;
            if ((model is { Length: > 0 } && word.Split('=', 2)[0] is "--model" or "-m") ||
                (effort is { Length: > 0 } && word.Split('=', 2)[0] == "--effort"))
            {
                if (!word.Contains('=') && index + 1 < words.Length && !words[index + 1].StartsWith('-')) index++;
                continue;
            }
            kept.Add(word);
            if (!BooleanFlags.Contains(word) && !word.Contains('=') && index + 1 < words.Length && !words[index + 1].StartsWith('-'))
                kept.Add(words[++index]);
        }
        if (model is { Length: > 0 }) kept.Add("--model " + Quote(model));
        if (effort is { Length: > 0 }) kept.Add("--effort " + Quote(effort));
        if (pluginDirectory is not null) kept.Add("--plugin-dir " + Quote(pluginDirectory));
        return string.Join(' ', kept);
    }

    private static string Quote(string value) => ShellWord().IsMatch(value) ? value : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"^[A-Za-z0-9_./~+=:@%-]+$")]
    private static partial Regex ShellWord();

    /// <summary>
    /// Where Claude stores a conversation: <c>~/.claude/projects/&lt;cwd with / and . as -&gt;/&lt;session&gt;.jsonl</c>, written only once
    /// the session has something to save. A session interrupted before that leaves an id that resolves to nothing, so
    /// the offer is made only when the conversation is on disk. <c>--resume</c> itself searches every project on the
    /// machine, so a session picked from the interactive list can belong to a directory this workspace never saw.
    /// </summary>
    public static string? TranscriptPath(string cwd, string sessionId)
    {
        if (!IsSessionId(sessionId)) return null;
        var projects = Path.Combine(ClaudeEnvironment.Home(), ".claude", "projects");
        var file = sessionId + ".jsonl";
        if (cwd.Length > 0)
        {
            var own = Path.Combine(projects, cwd.Replace('/', '-').Replace('.', '-'), file);
            if (File.Exists(own)) return own;
        }
        try
        {
            return Directory.GetDirectories(projects).Select(project => Path.Combine(project, file)).FirstOrDefault(File.Exists);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool SessionExists(string cwd, string sessionId) => TranscriptPath(cwd, sessionId) is not null;
}