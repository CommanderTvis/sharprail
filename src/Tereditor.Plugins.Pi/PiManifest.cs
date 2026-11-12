using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Pi;

public static class PiManifest
{
    public const string Id = "pi";
    public const string Icon = "asset:pi.svg";

    public static readonly PluginManifest Manifest = new(Id, "Pi", Icon, "0.1.0", PluginApi.Generation, PiContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        Description = "Chats with the Pi coding agent over its RPC mode, with the model providers Pi is signed in to.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "SharpRail.Plugins.Pi.Host.dll",
        Ui = "SharpRail.Plugins.Pi.UI.dll",
        Assets = "assets",
        Contributes = new() { SideTools = [new("pane", "Pi", Icon, PluginToolSide.Right)] }
    };
}