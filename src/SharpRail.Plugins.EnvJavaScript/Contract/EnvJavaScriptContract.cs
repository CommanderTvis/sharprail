using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvJavaScript;

public sealed record EnvJavaScriptSettings;

public static class EnvJavaScriptContract
{
    public const string Id = "env-javascript";

    /// <summary>The id of this plugin's workspace environment step, which the Env policy orders.</summary>
    public const string StepId = "javascript";

    public static readonly PluginContract Contract = PluginContract.Create<EnvJavaScriptSettings>(Id, 1, [], []);
    public static readonly PluginManifest Manifest = new(Id, "JavaScript Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.JavaScript,
        Description = "Puts the workspace's node_modules/.bin on PATH in every terminal and agent.",
        EnabledByDefault = true,
        DependsOn = [new("env", 1)],
        Host = "SharpRail.Plugins.EnvJavaScript.Host.dll"
    };
}