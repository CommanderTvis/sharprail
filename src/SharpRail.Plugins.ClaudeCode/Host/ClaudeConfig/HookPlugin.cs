using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// SharpRail's own Claude Code plugin (hooks that post the terminal status), shipped as a self-contained marketplace in
/// this plugin's assets. Registering it writes the user's settings; Claude Code then runs it from its own cache, which
/// only its <c>plugin install</c>/<c>plugin update</c> commands move, so the cached version is the installed one.
/// </summary>
internal sealed class HookPlugin(string? assetsRoot)
{
    public const string MarketplaceName = "sharprail";
    public const string PluginId = "sharprail@sharprail";

    private string? maintainedFor;

    // A caller outside a real activation (a check) gets the staged copy beside this assembly.
    private string AssetsRoot => assetsRoot ?? Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets");

    // The marketplace as this build ships it.
    private string SourceRoot => Path.Combine(AssetsRoot, "marketplace");

    /// <summary>
    /// The marketplace directory Claude Code is pointed at; the plugin sits inside it, since a directory marketplace's
    /// sources must. A real activation copies the shipped one under SharpRail's state directory, so every build of the
    /// app (development, release, another worktree) registers the same path rather than taking the registration from
    /// one another and offering the install again.
    /// </summary>
    public string MarketplaceRoot => assetsRoot is null ? SourceRoot : Path.Combine(SystemPrompt.StateDirectory(), ClaudeCodeContract.Id, "marketplace");

    public string PluginRoot => Path.Combine(MarketplaceRoot, "claude-plugin");

    public string ShippedVersion()
    {
        var manifest = Json.ReadObject(Path.Combine(SourceRoot, "claude-plugin", ".claude-plugin", "plugin.json"));
        return Json.String(manifest?["version"]) ?? "0.0.0";
    }

    private static string UserSettingsPath() => Path.Combine(ClaudePaths.ClaudeHome(), "settings.json");

    // A missing file is an empty object; an unreadable one is null, which nothing writes over.
    private static JsonObject? ReadJsonObject(string path) => File.Exists(path) ? Json.ReadObject(path) : [];

    /// <summary>The version Claude Code actually runs: the copy in its plugin cache, as its own install registry names it.</summary>
    public static string? CachedVersion()
    {
        var registry = ReadJsonObject(Path.Combine(ClaudePaths.ClaudeHome(), "plugins", "installed_plugins.json"));
        if (registry?["plugins"] is not JsonObject plugins || plugins[PluginId] is not JsonArray entries) return null;
        var entry = entries.OfType<JsonObject>().FirstOrDefault(item => Json.String(item["scope"]) == "user") ?? entries.FirstOrDefault() as JsonObject;
        return Json.String(entry?["version"]);
    }

    /// <summary>The exact argv that brings Claude's cached copy to the shipped version.</summary>
    public static IReadOnlyList<string> RefreshCommand(string claudeCommand, string? cached) =>
        cached is null
            ? [ClaudeCommands.ClaudeBinary(claudeCommand), "plugin", "install", PluginId, "--scope", "user", "--yes"]
            : [ClaudeCommands.ClaudeBinary(claudeCommand), "plugin", "update", PluginId, "--scope", "user"];

    private static string? RegisteredVersion(JsonObject settings)
    {
        if (settings["enabledPlugins"] is not JsonObject enabled || !Json.IsTrue(enabled[PluginId])) return null;
        if (settings["extraKnownMarketplaces"] is not JsonObject markets || markets[MarketplaceName] is not JsonObject entry) return null;
        return Json.String(entry["sharprailVersion"]) ?? "0.0.0";
    }

    // A registration pointing somewhere else is broken, not installed: Claude Code reports it as a plugin error.
    private bool RegisteredElsewhere(JsonObject settings) =>
        settings["extraKnownMarketplaces"] is JsonObject markets && markets[MarketplaceName] is JsonObject entry &&
        entry["source"] is JsonObject source && Json.String(source["path"]) is { } path && path != MarketplaceRoot;

    public HookPluginStatus Status(string claudeCommand = "claude")
    {
        var available = ShippedVersion();
        if (ReadJsonObject(UserSettingsPath()) is not { } settings) return new(HookPluginState.Unknown, available);
        var registered = RegisteredVersion(settings);
        var cached = CachedVersion();
        var change = $"{UserSettingsPath()}: register marketplace \"{MarketplaceName}\" -> {MarketplaceRoot}, enable plugin \"{PluginId}\" (v{available}); then " +
            string.Join(' ', RefreshCommand(claudeCommand, cached));
        if (registered is null) return new(HookPluginState.Absent, available, cached, change) { Problem = LastProblem };
        if (registered != available || RegisteredElsewhere(settings) || cached != available)
            return new(HookPluginState.Outdated, available, cached ?? registered, change) { Problem = LastProblem };
        return new(HookPluginState.Enabled, available, cached);
    }

    /// <summary>
    /// The status, with a registration the user already approved brought back into line: once per shipped version and
    /// activation, so a refresh Claude keeps refusing is not retried on every poll.
    /// </summary>
    public async Task<HookPluginStatus> StatusMaintainedAsync(string claudeCommand)
    {
        var status = Status(claudeCommand);
        if (status.State != HookPluginState.Outdated || maintainedFor == status.AvailableVersion) return status;
        maintainedFor = status.AvailableVersion;
        return await InstallAsync(claudeCommand);
    }

    /// <summary>Why the last refresh left Claude's copy behind, in Claude's own words; null after one that worked.</summary>
    public string? LastProblem { get; private set; }

    // Claude Code installs only from marketplaces in its own registry; a settings entry alone does not put one there.
    private static string? KnownMarketplacePath()
    {
        var known = ReadJsonObject(Path.Combine(ClaudePaths.ClaudeHome(), "plugins", "known_marketplaces.json"));
        return known?[MarketplaceName] is JsonObject entry && entry["source"] is JsonObject source ? Json.String(source["path"]) : null;
    }

    private void StageMarketplace()
    {
        if (MarketplaceRoot == SourceRoot) return;
        var staged = Json.ReadObject(Path.Combine(PluginRoot, ".claude-plugin", "plugin.json"));
        if (Json.String(staged?["version"]) == ShippedVersion() && Directory.Exists(MarketplaceRoot)) return;
        if (Directory.Exists(MarketplaceRoot)) Directory.Delete(MarketplaceRoot, true);
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(MarketplaceRoot, Path.GetRelativePath(SourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private async Task RefreshCacheAsync(string claudeCommand)
    {
        LastProblem = null;
        var cached = CachedVersion();
        if (cached == ShippedVersion()) return;
        var home = ClaudeEnvironment.Home();
        var claude = ClaudeCommands.ClaudeBinary(claudeCommand);
        try
        {
            var known = KnownMarketplacePath();
            if (known is not null && known != MarketplaceRoot)
                await ClaudeCommands.RunAsync([claude, "plugin", "marketplace", "remove", MarketplaceName], home, "marketplace refresh");
            if (known != MarketplaceRoot) await ClaudeCommands.RunAsync([claude, "plugin", "marketplace", "add", MarketplaceRoot], home, "marketplace registration");
            else await ClaudeCommands.RunAsync([claude, "plugin", "marketplace", "update", MarketplaceName], home, "marketplace refresh");
            await ClaudeCommands.RunAsync(RefreshCommand(claudeCommand, cached), home, "plugin refresh");
        }
        catch (InvalidOperationException error) { LastProblem = error.Message; }
    }

    public async Task<HookPluginStatus> InstallAsync(string claudeCommand)
    {
        try { StageMarketplace(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastProblem = error.Message; return Status(claudeCommand); }
        var path = UserSettingsPath();
        if (ReadJsonObject(path) is not { } settings) return Status(claudeCommand);
        var markets = settings["extraKnownMarketplaces"] as JsonObject ?? [];
        settings["extraKnownMarketplaces"] = markets;
        markets[MarketplaceName] = new JsonObject
        {
            ["source"] = new JsonObject { ["source"] = "directory", ["path"] = MarketplaceRoot },
            ["sharprailVersion"] = ShippedVersion()
        };
        var enabled = settings["enabledPlugins"] as JsonObject ?? [];
        settings["enabledPlugins"] = enabled;
        enabled[PluginId] = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Json.Stringify(settings) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Status(claudeCommand); }
        await RefreshCacheAsync(claudeCommand);
        return Status(claudeCommand);
    }
}