using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>The wording and wire shapes of the extension's permission card: what each tool asks, the rewritten "don't ask again" answer, and the replies.</summary>
internal static class ChatPermissions
{
    public const string Rejected = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). STOP what you are doing and wait for the user to tell you how to proceed.";
    private const string RejectedWithReason = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). The user provided the following reason for the rejection: ";
    public const string StayInPlanMode = "User chose to stay in plan mode and continue planning";

    public static readonly IReadOnlyList<ChatDestination> Destinations =
    [
        new(ChatPermissionDestinations.LocalSettings, "this project (just you)", "Saves to .claude/settings.local.json (gitignored)"),
        new(ChatPermissionDestinations.UserSettings, "all projects", "Saves to ~/.claude/settings.json"),
        new(ChatPermissionDestinations.ProjectSettings, "this project (shared)", "Saves to .claude/settings.json (shared with team)"),
        new(ChatPermissionDestinations.Session, "this session", "Only for this session (not saved)"),
        new(ChatPermissionDestinations.CliArg, "startup options", "Only for this run (from the launch options)")
    ];

    private static string? Str(JsonElement element, string name) => ChatFolder.Str(element, name);

    private static string Base(JsonElement input, string key) =>
        Str(input, key) is { Length: > 0 } path ? Path.GetFileName(path.TrimEnd('/', '\\')) : "this file";

    /// <summary>The question the card asks for a tool.</summary>
    public static string Prompt(string tool, JsonElement input) => tool switch
    {
        "Bash" => "Allow this bash command?",
        "PowerShell" => "Allow this PowerShell command?",
        "Read" => $"Allow reading from {Base(input, "file_path")}?",
        "Edit" or "MultiEdit" => $"Make this edit to {Base(input, "file_path")}?",
        "Write" => $"Allow write to {Base(input, "file_path")}?",
        "Glob" => Str(input, "path") is { Length: > 0 } globPath ? $"Allow glob search in {globPath}?" : "Allow this glob search",
        "WebFetch" => "Allow fetching this url?",
        "WebSearch" => "Allow searching for this query?",
        "SandboxNetworkAccess" => "Allow network connection to this host?",
        "Skill" => $"Use skill /{(Str(input, "skill") ?? Str(input, "name") ?? "").TrimStart('/')}?",
        "ExitPlanMode" => "Accept this plan?",
        _ => $"Do you want to proceed with {tool}?"
    };

    private static string Rule(JsonElement rule) =>
        Str(rule, "toolName") is { } name ? (Str(rule, "ruleContent") is { Length: > 0 } content ? $"{name}({content})" : name) : "this";

    /// <summary>The "don't ask again" answer rewritten from the CLI's suggestions, without its destination.</summary>
    public static (string? Label, bool Scoped, string? Destination) Always(JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0) return (null, false, null);
        var rules = new List<string>();
        var directories = new List<string>();
        string? mode = null;
        string? destination = null;
        foreach (var suggestion in list.EnumerateArray())
        {
            destination ??= Str(suggestion, "destination");
            switch (Str(suggestion, "type"))
            {
                case "addRules" when suggestion.TryGetProperty("rules", out var found) && found.ValueKind == JsonValueKind.Array:
                    rules.AddRange(found.EnumerateArray().Select(Rule));
                    break;
                case "addDirectories" when suggestion.TryGetProperty("directories", out var dirs) && dirs.ValueKind == JsonValueKind.Array:
                    directories.AddRange(dirs.EnumerateArray().Select(entry => entry.GetString()).OfType<string>());
                    break;
                case "setMode":
                    mode = Str(suggestion, "mode");
                    break;
            }
        }
        if (rules.Count > 0)
            return (rules.Count switch { 1 => $"Yes, allow {rules[0]}", 2 => $"Yes, allow {rules[0]} and {rules[1]}", _ => $"Yes, allow {rules[0]} and {rules.Count - 1} more" }, true, destination);
        if (directories.Count > 0)
            return (directories.Count == 1 ? $"Yes, allow access to {directories[0]}" : $"Yes, allow access to {directories.Count} directories", true, destination);
        return mode switch
        {
            "acceptEdits" => ("Yes, allow all edits this session", false, destination),
            "default" => ("Yes, return to normal mode", false, destination),
            _ => ("Yes, and don't ask again", true, destination)
        };
    }

    /// <summary>The suggestions with the chosen destination written into each, as the reply carries them.</summary>
    public static JsonElement WithDestination(JsonElement suggestions, string? destination)
    {
        if (destination is null || suggestions.ValueKind != JsonValueKind.Array) return suggestions;
        var rewritten = suggestions.EnumerateArray().Select(entry =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(entry.GetRawText())!.AsObject();
            node["destination"] = destination;
            return node;
        });
        return JsonSerializer.SerializeToElement(rewritten.ToArray());
    }

    public static string DenyMessage(ChatAnswerParams answer, string tool)
    {
        var reason = (answer.Reason ?? answer.Message)?.Trim();
        if (tool == "ExitPlanMode")
        {
            var comments = answer.PlanComments is { Count: > 0 } list
                ? "Comments on the plan:\n" + string.Join("\n", list.Select(comment => $"[Re: \"{comment.Quote}\"] {comment.Comment}")) : null;
            var parts = new[] { string.IsNullOrEmpty(reason) ? (comments is null ? StayInPlanMode : null) : reason, comments }.Where(part => part is not null);
            return string.Join("\n\n", parts);
        }
        return string.IsNullOrEmpty(reason) ? Rejected : RejectedWithReason + reason;
    }
}