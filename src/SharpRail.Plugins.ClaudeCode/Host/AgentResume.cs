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
    public static string? ResumeCommand(string command, string sessionId) =>
        IsSessionId(sessionId) && Invocation(command) is { } kept ? $"{kept} --resume {sessionId}" : null;

    /// <summary>The same invocation asked to pick up the latest conversation in its directory itself.</summary>
    public static string? ContinueCommand(string command) => Invocation(command) is { } kept ? $"{kept} --continue" : null;

    /// <summary>
    /// The executable and its flags, nothing else: a positional prompt is dropped, because the command comes from the
    /// process table unquoted and a resumed session must not be handed the opening prompt again. A word after a flag is
    /// kept as that flag's value unless the flag is known to take none.
    /// </summary>
    private static string? Invocation(string command)
    {
        var words = command.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var kept = new List<string> { words[0] };
        for (var index = 1; index < words.Length; index++)
        {
            var word = words[index];
            if (word == "--") break;
            if (ContinueFlags.Contains(word)) continue;
            if (ResumeFlags.Contains(word))
            {
                // --resume with nothing after it is the interactive picker, not an id to drop.
                if (index + 1 < words.Length && !words[index + 1].StartsWith('-')) index++;
                continue;
            }
            if (!word.StartsWith('-')) continue;
            kept.Add(word);
            if (!BooleanFlags.Contains(word) && !word.Contains('=') && index + 1 < words.Length && !words[index + 1].StartsWith('-'))
                kept.Add(words[++index]);
        }
        return string.Join(' ', kept);
    }

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