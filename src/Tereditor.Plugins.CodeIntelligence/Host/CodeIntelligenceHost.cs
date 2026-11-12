using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.CodeIntelligence.Host;

/// <summary>The host half has nothing to run: the plugin being active is the signal, and the dependents are ordered after it.</summary>
public sealed class CodeIntelligenceHost : PluginHostModule
{
    public override PluginContract Contract => CodeIntelligenceContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context) => ValueTask.FromResult<PluginDisposer?>(null);
}