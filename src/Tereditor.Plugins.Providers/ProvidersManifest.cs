using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Providers;

public static class ProvidersManifest
{
    public const string Id = "providers";
    public const string Icon = "asset:providers.svg";

    public static readonly PluginManifest Manifest = new(Id, "Model providers", Icon, "0.1.0", PluginApi.Generation, ProvidersContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        Description = "Adds the API keys of any model provider to Pi and omp, in the files each of them reads.",
        Support = PluginSupportLevels.Early,
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Providers.Host.dll",
        Ui = "Tereditor.Plugins.Providers.UI.dll",
        Assets = "assets"
    };
}