using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.EnvJavaScript;

public sealed record EnvJavaScriptSettings;

public static class EnvJavaScriptContract
{
    public const string Id = "env-javascript";

    /// <summary>The id of this plugin's workspace environment step, which the Env policy orders.</summary>
    public const string StepId = "javascript";

    public static readonly PluginContract Contract = PluginContract.Create<EnvJavaScriptSettings>(Id, 1, [], [], EnvJavaScriptJson.Default);
    public static readonly PluginManifest Manifest = new(Id, "JavaScript Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.JavaScript,
        // Runs JavaScript tooling on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Puts the workspace's node_modules/.bin on PATH in every terminal and agent.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        DependsOn = [new("env", 1)],
        Host = "Tereditor.Plugins.EnvJavaScript.Host.dll"
    };
}

[JsonSerializable(typeof(EnvJavaScriptSettings))]
internal sealed partial class EnvJavaScriptJson : JsonSerializerContext;