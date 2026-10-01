using System.Security.Cryptography;
using System.Text;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.PluginFixture;

public sealed record FixtureSettings { public string Greeting { get; init; } = "hello"; }
public sealed record EchoParams(string Text);
public sealed record EchoResult(string Text, string Greeting);
public sealed record CounterParams(string Workspace);
public sealed record CounterState(string Workspace, int Value);
public sealed record Ping(string Message);

public static class FixtureContract
{
    public static readonly PluginMethod<EchoParams, EchoResult> Echo = new("echo");
    public static readonly PluginMethod<CounterParams, CounterState> Counter = new("counter");
    public static readonly PluginMethod<CounterParams, CounterState> Bump = new("bump");
    public static readonly PluginChannel<CounterState> CounterChannel = PluginChannel<CounterState>.State("counter", Counter, "workspace");
    public static readonly PluginChannel<Ping> Pings = PluginChannel<Ping>.Event("pings");
    public static readonly PluginContract Contract = PluginContract.Create<FixtureSettings>("fixture", 1, [Echo, Counter, Bump], [CounterChannel, Pings]);
}

public sealed class FixtureHost : PluginHostModule
{
    public override PluginContract Contract => FixtureContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Method(FixtureContract.Echo, (parameters, _, _) =>
            ValueTask.FromResult(new EchoResult(parameters.Text, context.Settings<FixtureSettings>().Greeting)));
        context.Method(FixtureContract.Counter, (parameters, _, _) => ValueTask.FromResult(Read(context, parameters.Workspace)));
        context.Method(FixtureContract.Bump, (parameters, _, _) =>
        {
            var current = Read(context, parameters.Workspace);
            var next = current with { Value = current.Value + 1 };
            context.WriteState(StateName(parameters.Workspace), next.Value);
            context.Publish(FixtureContract.CounterChannel, next);
            return ValueTask.FromResult(next);
        });
        context.Route((request, _) => ValueTask.FromResult(request is { Method: "GET", Subpath: "hello" }
            ? new PluginHttpResponse(200) { Body = Encoding.UTF8.GetBytes("hello") }
            : new PluginHttpResponse(404)));
        context.Tool(new PluginTool<EchoParams>("fixture_echo", "Fixture echo", "Echoes its text back.",
            (arguments, _, _) => ValueTask.FromResult(new PluginToolResult(arguments.Text))));
        context.TerminalEnvironment(_ => new Dictionary<string, string> { ["SHARPRAIL_FIXTURE"] = "1" });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    private static CounterState Read(IPluginHostContext context, string workspace) => new(workspace, context.ReadState(StateName(workspace), 0));

    private static string StateName(string workspace) =>
        "counter-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)))[..12];
}