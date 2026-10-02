using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>Who Claude Code is signed in as, its version, and the usage limits it caches.</summary>
internal static class ClaudeAccountReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    // A round trip to the service, not a file read, so it gets its own longer budget.
    private static readonly TimeSpan UsageRefreshTimeout = TimeSpan.FromSeconds(45);

    // The fallback for a cache written before Claude Code described its own limits.
    private static readonly (string Id, string Label)[] NamedWindows = [("five_hour", "Session (5 hr)"), ("seven_day", "Weekly (7 day)")];

    private static readonly Dictionary<string, string> KindLabels = new() { ["session"] = "Session (5 hr)", ["weekly_all"] = "Weekly (7 day)" };

    private static ClaudeUsageSeverity SeverityOf(JsonNode? value) => Json.String(value) switch
    {
        "critical" => ClaudeUsageSeverity.Critical,
        "warning" => ClaudeUsageSeverity.Warning,
        _ => ClaudeUsageSeverity.Normal
    };

    private static string? Text(JsonNode? value) => Json.String(value) is { Length: > 0 } text ? text : null;

    private static double? Number(JsonNode? value) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number ? scalar.GetValue<double>() : null;

    private static int Percent(double value) => (int)Math.Max(0, Math.Min(100, Math.Round(value, MidpointRounding.AwayFromZero)));

    // Claude Code caches a self-describing list beside its per-key buckets: every entry carries its kind, severity and,
    // for a model-scoped week, the model's own display name, so a limit is labelled with the model it belongs to.
    private static List<ClaudeUsageWindow> WindowsFromLimits(JsonNode? value)
    {
        var windows = new List<ClaudeUsageWindow>();
        if (value is not JsonArray limits) return windows;
        for (var index = 0; index < limits.Count; index++)
        {
            if (limits[index] is not JsonObject entry || Number(entry["percent"]) is not { } percent) continue;
            var kind = Text(entry["kind"]) ?? "limit";
            var model = entry["scope"] is JsonObject scope && scope["model"] is JsonObject modelScope ? Text(modelScope["display_name"]) : null;
            var label = model is not null ? $"{model} limit" : KindLabels.GetValueOrDefault(kind);
            if (label is null) continue;
            windows.Add(new(model is not null ? $"{kind}:{model}" : $"{kind}:{index}", label, Percent(percent), SeverityOf(entry["severity"]))
            { ResetsAt = Text(entry["resets_at"]) });
        }
        return windows;
    }

    private static (List<ClaudeUsageWindow> Usage, string? FetchedAt) ReadUsage()
    {
        if (Json.ReadObject(ClaudePaths.ClaudeStatePath()) is not { } state || state["cachedUsageUtilization"] is not JsonObject cached) return ([], null);
        var utilization = cached["utilization"] as JsonObject ?? [];
        var usage = WindowsFromLimits(utilization["limits"]);
        if (usage.Count == 0)
            foreach (var (id, label) in NamedWindows)
                if (utilization[id] is JsonObject window && Number(window["utilization"]) is { } value)
                    usage.Add(new(id, label, Percent(value), ClaudeUsageSeverity.Normal) { ResetsAt = Text(window["resets_at"]) });
        var fetchedAt = Number(cached["fetchedAtMs"]) is { } milliseconds && double.IsFinite(milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return (usage, fetchedAt);
    }

    /// <summary>
    /// <c>/usage</c> in print mode: the only way to make Claude Code fetch the numbers again and rewrite the cache this
    /// pane reads. Run only when a person asks for it.
    /// </summary>
    private static Task RefreshUsageAsync(string claudeCommand) =>
        Bounded.RunAsync([ClaudeCommands.ClaudeBinary(claudeCommand), "-p", "/usage"], UsageRefreshTimeout);

    public static async Task<ClaudeAccount> ReadAsync(string claudeCommand, bool refresh)
    {
        if (refresh) await RefreshUsageAsync(claudeCommand);
        var (usage, fetchedAt) = ReadUsage();
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        var versionRun = await Bounded.RunAsync([binary, "--version"], Timeout);
        var version = versionRun.Ok ? versionRun.Out.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        var account = new ClaudeAccount(false, usage) { Version = version, UsageFetchedAt = fetchedAt };

        var run = await Bounded.RunAsync([binary, "auth", "status", "--json"], Timeout);
        if (!run.Ok) return account;
        JsonObject? status;
        try { status = JsonNode.Parse(run.Out) as JsonObject; }
        catch (JsonException) { return account; }
        if (status is null) return account;
        return account with
        {
            LoggedIn = Json.IsTrue(status["loggedIn"]),
            Email = Text(status["email"]),
            Organization = Text(status["orgName"]),
            Subscription = Text(status["subscriptionType"]),
            AuthMethod = Text(status["authMethod"])
        };
    }
}