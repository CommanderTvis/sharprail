using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Acp;

public static class AcpManifest
{
    public const string Id = "acp";
    public const string Name = "ACP";

    public static readonly PluginManifest Manifest = new(Id, Name, "asset:acp.svg", "0.1.0", PluginApi.Generation, AcpContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        Description = "Early support: chats with any Agent Client Protocol agent from the public ACP registry, or one you add yourself.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Acp.Host.dll",
        Assets = "assets",
        UnavailableOn = [HostKind.PureWeb]
    };
}