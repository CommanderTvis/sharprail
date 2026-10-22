using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvPython;

public sealed record EnvPythonSettings;

public static class EnvPythonContract
{
    public const string Id = "env-python";

    /// <summary>Where this plugin's workspace environment step runs among the others.</summary>
    public const int Order = 300;

    public static readonly PluginContract Contract = PluginContract.Create<EnvPythonSettings>(Id, 1, [], []);
    public static readonly PluginManifest Manifest = new(Id, "Python Environment", "terminal-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Python,
        Description = "Activates the workspace's own .venv in every terminal and agent.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.EnvPython.Host.dll"
    };
}
