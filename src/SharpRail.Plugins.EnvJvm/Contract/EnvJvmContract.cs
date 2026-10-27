using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvJvm;

public sealed record EnvJvmSettings;

public static class EnvJvmContract
{
    public const string Id = "env-jvm";

    /// <summary>The id of this plugin's workspace environment step, which the Env policy orders.</summary>
    public const string StepId = "jvm";

    public static readonly PluginContract Contract = PluginContract.Create<EnvJvmSettings>(Id, 1, [], []);
    public static readonly PluginManifest Manifest = new(Id, "JVM Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Jvm,
        Description = "Points JAVA_HOME and PATH at the JDK a workspace's .sdkmanrc or .java-version (jenv) chooses, in every terminal and agent.",
        EnabledByDefault = true,
        DependsOn = [new("env", 1)],
        Host = "SharpRail.Plugins.EnvJvm.Host.dll"
    };
}