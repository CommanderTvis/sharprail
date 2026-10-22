using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvMise;

public sealed record EnvMiseSettings;

public static class EnvMiseContract
{
    public const string Id = "env-mise";

    /// <summary>Where this plugin's workspace environment step runs among the others.</summary>
    public const int Order = 100;

    public static readonly PluginContract Contract = PluginContract.Create<EnvMiseSettings>(Id, 1, [], []);
    public static readonly PluginManifest Manifest = new(Id, "mise Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Gives every terminal and agent the tools and variables of the workspace's mise configuration, including a new worktree's.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.EnvMise.Host.dll"
    };
}