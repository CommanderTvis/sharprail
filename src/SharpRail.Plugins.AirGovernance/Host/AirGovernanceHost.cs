using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.AirGovernance.Host;

/// <summary>The host half: detects the user's Central CLI and contributes `central run` to the agents' revived launches.</summary>
public sealed class AirGovernanceHost : PluginHostModule
{
    public override PluginContract Contract => AirGovernanceContract.Contract;

    internal AirEnvironment? Environment { get; init; }

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var runtime = new AirGovernanceRuntime(Environment ?? AirEnvironment.Current(), context.Settings<AirSettings>,
            status => context.Publish(AirGovernanceContract.Changed, status));
        context.Method(AirGovernanceContract.Status, async (parameters, _, cancellationToken) => await runtime.StatusAsync(parameters.Probe, cancellationToken));
        context.Method(AirGovernanceContract.SignIn, async (_, _, _) => await runtime.SignInAsync());
        context.Method(AirGovernanceContract.SignOut, async (_, _, _) => await runtime.SignOutAsync());
        context.Method(AirGovernanceContract.StartProxy, async (_, _, _) => await runtime.StartProxyAsync());
        context.Method(AirGovernanceContract.Update, async (_, _, _) => await runtime.UpdateAsync());
        context.Method(AirGovernanceContract.AccessSources, async (_, _, cancellationToken) => await runtime.AccessSourcesAsync(cancellationToken));
        context.Method(AirGovernanceContract.SwitchAccess, async (parameters, _, _) => await runtime.SwitchAccessAsync(parameters.SourceId));
        // Revived terminals are rebuilt on the host, so the host half contributes too.
        context.AgentLaunch(launcherId => AirLaunch.Contribution(context.Settings<AirSettings>(), launcherId, runtime.CentralPath()));
        return ValueTask.FromResult<PluginDisposer?>(() => { runtime.Dispose(); return ValueTask.CompletedTask; });
    }
}