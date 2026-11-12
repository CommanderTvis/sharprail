using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.AirGateway.Host;

/// <summary>The host half: detects the user's Central CLI and contributes `central run`, and the account those launches run under, to the agents' launches.</summary>
public sealed class AirGatewayHost : PluginHostModule
{
    public override PluginContract Contract => AirGatewayContract.Contract;

    internal AirEnvironment? Environment { get; init; }

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var runtime = new AirGatewayRuntime(Environment ?? AirEnvironment.Current(), context.Settings<AirSettings>,
            status => context.Publish(AirGatewayContract.Changed, status));
        context.Method(AirGatewayContract.Status, async (parameters, _, cancellationToken) => await runtime.StatusAsync(parameters.Probe, cancellationToken));
        context.Method(AirGatewayContract.SignIn, async (_, _, _) => await runtime.SignInAsync());
        context.Method(AirGatewayContract.SignOut, async (_, _, _) => await runtime.SignOutAsync());
        context.Method(AirGatewayContract.StartProxy, async (_, _, _) => await runtime.StartProxyAsync());
        context.Method(AirGatewayContract.Update, async (_, _, _) => await runtime.UpdateAsync());
        context.Method(AirGatewayContract.AccessSources, async (_, _, cancellationToken) => await runtime.AccessSourcesAsync(cancellationToken));
        context.Method(AirGatewayContract.SwitchAccess, async (parameters, _, _) => await runtime.SwitchAccessAsync(parameters.SourceId));
        // Revived terminals are rebuilt on the host, so the host half contributes too.
        context.AgentLaunch(runtime.Contribution);
        return ValueTask.FromResult<PluginDisposer?>(() => { runtime.Dispose(); return ValueTask.CompletedTask; });
    }
}