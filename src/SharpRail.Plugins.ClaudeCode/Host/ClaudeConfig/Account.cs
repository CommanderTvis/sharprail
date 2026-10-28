using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>Who Claude Code is signed in as, its version, and the usage limits <c>/usage</c> reports.</summary>
internal static class ClaudeAccountReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    // A round trip to the service, so it gets its own longer budget.
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(45);

    // The fallback for a report without a limits list.
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

    // The report carries a self-describing list: every entry carries its kind, severity and,
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

    // The older named-window shape (five_hour, seven_day), should a report ever carry it without a limits list.
    private static List<ClaudeUsageWindow> NamedWindowsOf(JsonObject rateLimits)
    {
        var usage = new List<ClaudeUsageWindow>();
        foreach (var (id, label) in NamedWindows)
            if (rateLimits[id] is JsonObject window && Number(window["utilization"]) is { } value)
                usage.Add(new(id, label, Percent(value), ClaudeUsageSeverity.Normal) { ResetsAt = Text(window["resets_at"]) });
        return usage;
    }

    /// <summary>
    /// Finds the first <c>usage_report.rate_limits</c> in the stream-json output of <c>/usage</c>. Lines that are not JSON
    /// objects or carry no report are skipped; null when no line has one.
    /// </summary>
    internal static (List<ClaudeUsageWindow> Usage, string FetchedAt)? ParseUsage(string output, DateTimeOffset now)
    {
        foreach (var line in output.Split('\n'))
        {
            JsonObject? entry;
            try { entry = JsonNode.Parse(line.Trim()) as JsonObject; }
            catch (JsonException) { continue; }
            if (entry?["usage_report"] is not JsonObject report || report["rate_limits"] is not JsonObject rateLimits) continue;
            var usage = WindowsFromLimits(rateLimits["limits"]);
            if (usage.Count == 0) usage = NamedWindowsOf(rateLimits);
            var at = DateTimeOffset.TryParse(Text(entry["timestamp"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : now;
            return (usage, at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        }
        return null;
    }

    // The last successful /usage per binary: opening the pane reads this, the hourly sync and Refresh run the command again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<ClaudeUsageWindow> Usage, string FetchedAt)> LastUsage = new();

    /// <summary>
    /// <c>/usage</c> in print mode, the official way to read the limits: it costs no tokens. Its stream-json form carries
    /// the report with full timestamps; the plain text has no year. Unavailable (empty, no time) when the command fails.
    /// </summary>
    private static async Task<(List<ClaudeUsageWindow> Usage, string? FetchedAt)> FetchUsageAsync(string binary)
    {
        var run = await Bounded.RunAsync([binary, "-p", "--output-format", "stream-json", "--verbose", "/usage"], UsageTimeout);
        if (run.Ok && ParseUsage(run.Out, DateTimeOffset.UtcNow) is { } parsed)
        {
            LastUsage[binary] = parsed;
            return parsed;
        }
        LastUsage.TryRemove(binary, out _);
        return ([], null);
    }

    public static async Task<ClaudeAccount> ReadAsync(string claudeCommand, bool refresh)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        // Without a refresh the pane gets what the last fetch found, or nothing: its own first-view and hourly refresh fetch it.
        var (usage, fetchedAt) = refresh ? await FetchUsageAsync(binary)
            : LastUsage.TryGetValue(binary, out var last) ? (last.Usage, last.FetchedAt) : ([], null);
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