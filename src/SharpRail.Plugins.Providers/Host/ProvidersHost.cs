using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Providers.Host;

/// <summary>The Model providers plugin's host half: it edits the credential files of Pi and omp on this host.</summary>
public sealed class ProvidersHost : PluginHostModule
{
    public override PluginContract Contract => ProvidersContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var service = new ProvidersService(ProvidersService.DefaultFiles(
            () => context.ReadState<List<CustomProvider>>("custom", []),
            custom => context.WriteState("custom", custom.ToList())));
        // Reads and writes touch files, so none of them runs on the caller's thread.
        context.Method(ProvidersContract.List, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.List(p.Agent), ct)));
        context.Method(ProvidersContract.SetKey, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.SetKey(p), ct)));
        context.Method(ProvidersContract.RemoveKey, (p, _, ct) => new ValueTask<ProvidersReport>(Task.Run(() => service.RemoveKey(p), ct)));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}