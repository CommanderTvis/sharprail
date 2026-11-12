using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>How a background usage read ended: it ran, it failed, or it did not run (no CLI, not signed in).</summary>
internal enum UsagePoll { Ok, Failed, Skipped }

/// <summary>Who Claude Code is signed in as, its version, and the usage limits <c>/usage</c> reports.</summary>
internal static class ClaudeAccountReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    // A round trip to the service, so it gets its own longer budget; a child past it is killed.
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(30);

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

    // The last successful /usage per binary, and why the latest attempt failed (absent after a success). A failure keeps the
    // last good numbers: the pane shows them with their age and the reason.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<ClaudeUsageWindow> Usage, string FetchedAt)> LastUsage = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastError = new();

    /// <summary>
    /// <c>/usage</c> in print mode, the official way to read the limits: it costs no tokens and starts no model turn. Its
    /// stream-json form carries the report with full timestamps; the plain text has no year. A failure keeps the earlier numbers.
    /// </summary>
    private static async Task<UsagePoll> FetchUsageAsync(string binary)
    {
        var run = await Bounded.RunAsync([binary, "-p", "--output-format", "stream-json", "--verbose", "/usage"], UsageTimeout);
        if (run.Ok && ParseUsage(run.Out, DateTimeOffset.UtcNow) is { } parsed)
        {
            LastUsage[binary] = parsed;
            LastError.TryRemove(binary, out _);
            return UsagePoll.Ok;
        }
        LastError[binary] = run.TimedOut ? "Claude Code did not answer in time."
            : run.LaunchFailed ? "Claude Code could not be started."
            : "Claude Code gave no usage numbers.";
        return UsagePoll.Failed;
    }

    /// <summary>
    /// One background read for <see cref="ClaudeUsagePoller"/>. Unless <paramref name="manual"/>, it runs only when the CLI starts and
    /// is signed in; otherwise it is skipped, without an error to show.
    /// </summary>
    public static async Task<UsagePoll> PollAsync(string claudeCommand, bool manual)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        if (!manual)
        {
            var status = await Bounded.RunAsync([binary, "auth", "status", "--json"], Timeout);
            if (!status.Ok) return UsagePoll.Skipped;
            try { if (!Json.IsTrue((JsonNode.Parse(status.Out) as JsonObject)?["loggedIn"])) return UsagePoll.Skipped; }
            catch (JsonException) { return UsagePoll.Skipped; }
        }
        return await FetchUsageAsync(binary);
    }

    public static async Task<ClaudeAccount> ReadAsync(string claudeCommand, bool refresh)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        // The pane gets what the last fetch found, or nothing: the host's poller and the refresh button fetch it.
        if (refresh) await FetchUsageAsync(binary);
        var (usage, fetchedAt) = LastUsage.TryGetValue(binary, out var last) ? (last.Usage, last.FetchedAt) : ([], null);
        var versionRun = await Bounded.RunAsync([binary, "--version"], Timeout);
        var version = versionRun.Ok ? versionRun.Out.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        var account = new ClaudeAccount(false, usage) { Version = version, UsageFetchedAt = fetchedAt, UsageError = LastError.GetValueOrDefault(binary) };

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