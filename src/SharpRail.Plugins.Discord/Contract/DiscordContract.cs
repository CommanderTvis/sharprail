using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Discord;

/// <summary>What the client believes is worth publishing. The host decides whether any of it may leave.</summary>
/// <param name="FilePath">Worktree-relative path of the focused editor tab, or null when none is focused.</param>
public sealed record DiscordPresence(string ProjectId, string ProjectName, string? FilePath = null);

public enum DiscordConnectionState { Unconfigured, Unavailable, Connecting, Connected }

/// <summary>What is on the user's profile right now, exactly as Discord received it.</summary>
public sealed record DiscordPublished(string State, string? Details = null);

/// <param name="Detail">Why nothing is published, in the words the settings section shows.</param>
public sealed record DiscordStatus(DiscordConnectionState State, DiscordPublished? Published = null, string? Detail = null);

public sealed record DiscordPresenceParams(DiscordPresence? Presence = null);

public sealed record DiscordStatusParams;

public sealed record DiscordSettings
{
    public string ApplicationId { get; init; } = DiscordValues.ThinkRailApplicationId;
    /// <summary>Projects that never reach Discord at all, not even as an anonymous "working on something".</summary>
    public IReadOnlyList<string> BlockedProjectIds { get; init; } = [];
    /// <summary>Whether the file name is published alongside the project name.</summary>
    public bool ShareFileName { get; init; } = true;
}

public static partial class DiscordValues
{
    /// <summary>ThinkRail's own registered Discord application, so the presence has a name to wear out of the box.</summary>
    public const string ThinkRailApplicationId = "1542175111384404138";

    /// <summary>Discord application ids are snowflakes: a decimal integer, 15-25 digits today.</summary>
    public static bool IsApplicationId(string value) => ApplicationId().IsMatch(value);

    [GeneratedRegex("^[0-9]{15,25}$")]
    private static partial Regex ApplicationId();
}

public static class DiscordContract
{
    public static readonly PluginMethod<DiscordPresenceParams, DiscordStatus> Presence = new("presence");
    public static readonly PluginMethod<DiscordStatusParams, DiscordStatus> Status = new("status");
    public static readonly PluginChannel<DiscordStatus> StatusChannel = PluginChannel<DiscordStatus>.State("status", Status);
    public static readonly PluginContract Contract =
        PluginContract.Create<DiscordSettings>(DiscordPlugin.Id, 1, [Presence, Status], [StatusChannel]);
}

public static class DiscordPlugin
{
    public const string Id = "discord";

    public static readonly PluginManifest Manifest = new(Id, "Discord", "discord", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Shows what you're working on as Discord Rich Presence.",
        EnabledByDefault = false,
        Assets = "assets",
        Host = "SharpRail.Plugins.Discord.Host.dll",
        Ui = "SharpRail.Plugins.Discord.UI.dll"
    };
}