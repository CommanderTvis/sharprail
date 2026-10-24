using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Omp;

public sealed record OmpLaunchOptions
{
    public bool Mcp { get; init; } = true;
    public bool Windows { get; init; }
    public bool UiLaunch { get; init; }
    public string? Model { get; init; }
    public string? ApprovalMode { get; init; }
    public bool Advisor { get; init; }
    public bool OwnCodeIntelligence { get; init; }
    /// <summary>The tab's own session directory name under <see cref="OmpLaunch.SessionsVariable"/>; null leaves omp's default.</summary>
    public string? SessionId { get; init; }
    /// <summary>An absolute session directory, for a host-built revive line; wins over <see cref="SessionId"/>.</summary>
    public string? SessionDirectory { get; init; }
    /// <summary>Continue the latest session in the session directory (<c>-c</c>).</summary>
    public bool Continue { get; init; }
    /// <summary>Resume this session file (<c>-r &lt;path&gt;</c>); wins over <see cref="Continue"/>.</summary>
    public string? ResumePath { get; init; }
    public string? SystemPrompt { get; init; }
    public string? InitialPrompt { get; init; }
    public IReadOnlyList<string> Images { get; init; } = [];
    /// <summary>Other plugins' launch contributions, applied to the executable word.</summary>
    public IReadOnlyList<AgentLaunchContribution> Contributions { get; init; } = [];
}

/// <summary>The launch line, shared by the launcher and the host's revive prefill. Flags are omp's documented ones (https://omp.sh/docs/cli).</summary>
public static partial class OmpLaunch
{
    /// <summary>The profile whose <c>mcp.json</c> SharpRail owns; see https://omp.sh/docs/mcp.</summary>
    public const string Profile = "sharprail";
    /// <summary>The host-owned folder holding one session directory per launch, set in every terminal by the host.</summary>
    public const string SessionsVariable = "SHARPRAIL_OMP_SESSIONS";
    /// <summary>A command-scoped marker distinguishing UI launches from commands typed into a terminal.</summary>
    public const string UiLaunchPrefix = "SHARPRAIL_UI_LAUNCH=omp";
    public const string InstallUrl = "https://omp.sh/";

    /// <summary>The documented <c>--approval-mode</c> values.</summary>
    public static readonly IReadOnlyList<string> ApprovalModes = ["always-ask", "write", "yolo"];

    /// <summary>
    /// Suggested <c>--model</c> values: the built-in roles as <c>@role</c> (https://omp.sh/docs/roles) and the example
    /// selectors those docs use. Suggestions only; any id omp resolves is accepted.
    /// </summary>
    public static readonly IReadOnlyList<string> ModelSuggestions =
    [
        "@default", "@smol", "@slow", "@plan",
        "anthropic/claude-sonnet-4-6", "anthropic/claude-opus-4-6:high", "anthropic/claude-haiku-4-5",
        "openai-codex/gpt-5.3-codex:high", "openai/gpt-5.4", "google/gemini-3-pro"
    ];

    public static string ShellQuote(string word) => SafeWord().IsMatch(word) ? word : "'" + word.Replace("'", "'\\''") + "'";

    public static string Executable(string command) => command.Trim() is { Length: > 0 } trimmed ? trimmed : "omp";

    public static string Line(string command, OmpLaunchOptions options)
    {
        var executable = Executable(command);
        var parts = new List<string> { options.Contributions.Count > 0 ? AgentLaunchComposition.Apply(executable, "omp", options.Contributions, options.Windows) : executable };
        if (options.Mcp) parts.AddRange(["--profile", Profile]);
        // The per-launch session directory is expanded by the POSIX shell; Windows launches keep omp's default store.
        if (options.SessionDirectory is { Length: > 0 } directory) parts.AddRange(["--session-dir", ShellQuote(directory)]);
        else if (options.SessionId is { Length: > 0 } id && !options.Windows) parts.AddRange(["--session-dir", $"\"${SessionsVariable}/{id}\""]);
        if (options.ResumePath is { Length: > 0 } resume) parts.AddRange(["-r", ShellQuote(resume)]);
        else if (options.Continue) parts.Add("-c");
        if (options.Model is { Length: > 0 } model) parts.AddRange(["--model", ShellQuote(model)]);
        if (options.ApprovalMode is { Length: > 0 } approval) parts.AddRange(["--approval-mode", ShellQuote(approval)]);
        if (options.Advisor) parts.Add("--advisor");
        if (!options.OwnCodeIntelligence) parts.Add("--no-lsp");
        if (options.SystemPrompt is { Length: > 0 } system) parts.AddRange(["--append-system-prompt", ShellQuote(system)]);
        var resuming = options.Continue || options.ResumePath is { Length: > 0 };
        if (!resuming)
        {
            foreach (var image in options.Images) parts.Add(ShellQuote("@" + image));
            if (options.InitialPrompt is { Length: > 0 } prompt) parts.Add(ShellQuote(prompt));
        }
        var line = string.Join(" ", parts);
        return options.UiLaunch && !options.Windows ? UiLaunchPrefix + " " + line : line;
    }

    /// <summary>A new session directory name for one launch.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>The session directory name in a launch's arguments, as <see cref="Line"/> writes it; null when there is none.</summary>
    public static string? SessionIdOf(string arguments) => SessionDir().Match(arguments) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^[A-Za-z0-9_./~+=:@%-]+$")]
    private static partial Regex SafeWord();

    [GeneratedRegex(@"--session-dir\s+\S*?/([0-9a-f]{32})\b")]
    private static partial Regex SessionDir();
}