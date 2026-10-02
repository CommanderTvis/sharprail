using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Discord.Host;

/// <summary>The host half: the IPC client and the presence decision, driven by the plugin's own settings.</summary>
public sealed class DiscordHost : PluginHostModule
{
    public override PluginContract Contract => DiscordContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        // Each activation gets its own runtime, so a disable and enable starts clean rather than inheriting a retry floor.
        var runtime = new DiscordRuntime(context.Settings<DiscordSettings>, status => context.Publish(DiscordContract.StatusChannel, status));
        context.Method(DiscordContract.Presence, (parameters, _, _) => new ValueTask<DiscordStatus>(runtime.PublishPresenceAsync(parameters.Presence)));
        context.Method(DiscordContract.Status, (_, _, _) => new ValueTask<DiscordStatus>(runtime.GetStatusAsync()));
        context.OnSettings<DiscordSettings>(_ => runtime.ApplySettingsChange());
        return ValueTask.FromResult<PluginDisposer?>(() => { runtime.Stop(); return ValueTask.CompletedTask; });
    }
}