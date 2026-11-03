using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Providers.UI;

/// <summary>The Model providers plugin's UI half: one Settings page for the providers of Pi and omp.</summary>
public sealed class ProvidersUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Model providers", ProvidersManifest.Icon, () => new ProvidersView(context)));
        return null;
    }
}