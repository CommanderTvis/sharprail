using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Discord.UI;

/// <summary>The UI half: the settings section, and reporting what is open so the host can decide what to publish.</summary>
public sealed class DiscordUI : PluginUIModule
{
    private static readonly TimeSpan ReportDebounce = TimeSpan.FromMilliseconds(500);

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Discord", "asset:discord.svg", () => new DiscordSettingsView(context)));
        var timer = new DispatcherTimer { Interval = ReportDebounce };
        DiscordPresence? last = null;
        void Report()
        {
            timer.Stop();
            var presence = CurrentPresence(context.Host());
            if (SamePresence(presence, last)) return;
            last = presence;
            _ = SendAsync(context, presence);
        }
        timer.Tick += (_, _) => Report();
        context.WatchHost(CurrentPresence, (_, _) => { timer.Stop(); timer.Start(); });
        Report();
        return timer.Stop;
    }

    internal static bool SamePresence(DiscordPresence? left, DiscordPresence? right) =>
        left is null ? right is null : right is not null && left.ProjectId == right.ProjectId && left.FilePath == right.FilePath;

    internal static DiscordPresence? CurrentPresence(PluginHostProjection host)
    {
        if (host.ActiveWorkspaceId is null) return null;
        if (host.Projects.FirstOrDefault(project => project.Id == host.ContextProjectId) is not { } project) return null;
        var filePath = host.ActiveEditor is { Kind: not EditorKind.Diff } editor ? editor.Path : null;
        return new(project.Id, project.Name, filePath);
    }

    private static async Task SendAsync(IPluginUIContext context, DiscordPresence? presence)
    {
        try { await context.RequestAsync(DiscordContract.Presence, new DiscordPresenceParams(presence)); }
        catch (PluginCallException error) { context.Log.Debug("Presence was not reported: " + error.Message); }
    }
}