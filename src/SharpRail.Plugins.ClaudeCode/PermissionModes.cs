using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode;

/// <summary>One choice of Claude Code's <c>--permission-mode</c>, in the words the launcher and Settings show.</summary>
public sealed record ClaudePermissionMode(string Id, string Label, string Description)
{
    /// <summary>Skips every permission check, so the UI marks it the way Codex's full-access sandbox should be read.</summary>
    public bool Dangerous => ClaudePermissionModes.IsDangerous(Id);
}

/// <summary>The modes the host's CLI accepts and the default the user's own Claude settings name, if any.</summary>
public sealed record PermissionModesResult(IReadOnlyList<string> Modes)
{
    /// <summary>True when the list came from the installed CLI's help rather than SharpRail's fallback.</summary>
    public bool FromCli { get; init; }
    /// <summary><c>permissions.defaultMode</c> from the user's <c>~/.claude/settings.json</c>; null when it names none.</summary>
    public string? ConfiguredDefault { get; init; }
    /// <summary><c>advisorModel</c> from the same file; null when it names none.</summary>
    public string? ConfiguredAdvisor { get; init; }
}

/// <summary>Parses the CLI's mode list and labels each mode; an unknown mode keeps its own id as its label.</summary>
public static partial class ClaudePermissionModes
{
    /// <summary>Used when the CLI's help cannot be read or names no choices.</summary>
    public static readonly IReadOnlyList<string> Fallback = ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"];

    private static readonly Dictionary<string, (string Label, string Description)> Known = new(StringComparer.Ordinal)
    {
        ["default"] = ("Ask before acting", "Prompts for permission the first time each tool is used."),
        ["manual"] = ("Ask before acting", "Prompts for permission the first time each tool is used."),
        ["acceptEdits"] = ("Accept edits", "Accepts file edits without asking; other tools still ask."),
        ["plan"] = ("Plan", "Reads and plans only; nothing is edited or run."),
        ["auto"] = ("Auto", "Claude decides which actions need your approval."),
        ["dontAsk"] = ("Don't ask", "Denies anything not pre-approved instead of asking."),
        ["bypassPermissions"] = ("Bypass permissions", "Skips every permission check. Only for sandboxes with no internet access.")
    };

    public static bool IsDangerous(string id) => id == "bypassPermissions";

    public static ClaudePermissionMode Describe(string id) =>
        Known.TryGetValue(id, out var known) ? new(id, known.Label, known.Description) : new(id, id, "");

    /// <summary>The <c>choices</c> listed for <c>--permission-mode</c> in <c>claude --help</c>; empty when there are none.</summary>
    public static IReadOnlyList<string> Parse(string help)
    {
        var option = OptionChoices().Match(help);
        if (!option.Success) return [];
        return [.. Quoted().Matches(option.Groups[1].Value).Select(match => match.Groups[1].Value).Where(mode => mode.Length > 0).Distinct()];
    }

    [GeneratedRegex(@"--permission-mode\s+<[^>]*>[^(]*\(choices:\s*([^)]*)\)")]
    private static partial Regex OptionChoices();

    [GeneratedRegex("\"([A-Za-z]+)\"")]
    private static partial Regex Quoted();
}