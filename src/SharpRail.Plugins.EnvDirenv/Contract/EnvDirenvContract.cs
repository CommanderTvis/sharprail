using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvDirenv;

public sealed record EnvDirenvSettings;

public static class EnvDirenvContract
{
    public const string Id = "env-direnv";

    /// <summary>The id of this plugin's workspace environment step, which the Env policy orders.</summary>
    public const string StepId = "direnv";

    public static readonly PluginContract Contract = PluginContract.Create<EnvDirenvSettings>(Id, 1, [], []);
    public static readonly PluginManifest Manifest = new(Id, "direnv Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Gives every terminal and agent the environment of the workspace's .envrc through direnv, including a new worktree's.",
        EnabledByDefault = true,
        DependsOn = [new("env", 1)],
        Host = "SharpRail.Plugins.EnvDirenv.Host.dll"
    };
}