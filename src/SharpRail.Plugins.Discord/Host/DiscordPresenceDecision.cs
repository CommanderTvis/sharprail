namespace SharpRail.Plugins.Discord.Host;

/// <summary>The one decision of what may reach Discord: publish an activity, clear it, or stay silent.</summary>
internal abstract record PresenceDecision
{
    public sealed record Publish(DiscordActivity Activity) : PresenceDecision;
    public sealed record Clear(string Detail) : PresenceDecision;
    public sealed record Silent(string Detail) : PresenceDecision;

    public static PresenceDecision For(DiscordPresence? presence, DiscordSettings settings, long startedAt)
    {
        if (!DiscordValues.IsApplicationId(settings.ApplicationId)) return new Silent("Add a Discord application id to start publishing.");
        if (presence is null) return new Clear("No project is open.");
        if (settings.BlockedProjectIds.Contains(presence.ProjectId)) return new Clear($"{presence.ProjectName} is blocked from Discord.");
        // The file, never the path that leads to it.
        var file = settings.ShareFileName && presence.FilePath is { Length: > 0 } path ? Path.GetFileName(path.Replace('\\', '/')) : null;
        return new Publish(new(file is null ? null : $"Editing {file}", presence.ProjectName, startedAt));
    }

    public static DiscordStatus Status(PresenceDecision decision, bool connected, string? failure) => decision switch
    {
        Silent silent => new(DiscordConnectionState.Unconfigured, null, silent.Detail),
        _ when failure is not null => new(DiscordConnectionState.Unavailable, null, failure),
        _ when !connected => new(DiscordConnectionState.Connecting),
        Clear clear => new(DiscordConnectionState.Connected, null, clear.Detail),
        Publish publish => new(DiscordConnectionState.Connected, new(publish.Activity.State, publish.Activity.Details)),
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };
}