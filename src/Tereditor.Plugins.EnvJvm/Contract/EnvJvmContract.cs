using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.EnvJvm;

public sealed record EnvJvmSettings;

public static class EnvJvmContract
{
    public const string Id = "env-jvm";

    /// <summary>The id of this plugin's workspace environment step, which the Env policy orders.</summary>
    public const string StepId = "jvm";

    public static readonly PluginContract Contract = PluginContract.Create<EnvJvmSettings>(Id, 1, [], [], EnvJvmJson.Default);
    public static readonly PluginManifest Manifest = new(Id, "JVM Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Jvm,
        // Runs JDK tooling on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Points JAVA_HOME and PATH at the JDK a workspace's .sdkmanrc or .java-version (jenv) chooses, in every terminal and agent.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        DependsOn = [new("env", 1)],
        Host = "Tereditor.Plugins.EnvJvm.Host.dll"
    };
}

[JsonSerializable(typeof(EnvJvmSettings))]
internal sealed partial class EnvJvmJson : JsonSerializerContext;