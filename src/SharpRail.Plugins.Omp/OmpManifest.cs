using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Omp;

public static class OmpManifest
{
    public const string Id = "omp";
    public const string Icon = "terminal";
    public static readonly string ContextTool = PluginIdentity.ToolId(Id, "context");

    public static readonly PluginManifest Manifest = new(Id, "omp", Icon, "0.1.0", PluginApi.Generation, OmpContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        Description = "Runs the omp coding agent in the terminal with SharpRail's MCP tools and shows its context files.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = false,
        Host = "SharpRail.Plugins.Omp.Host.dll",
        Ui = "SharpRail.Plugins.Omp.UI.dll",
        Contributes = new() { SideTools = [new("context", "omp", Icon, PluginToolSide.Right)] }
    };
}