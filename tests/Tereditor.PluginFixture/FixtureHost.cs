using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.PluginFixture;

public sealed record FixtureSettings { public string Greeting { get; set; } = "hello"; }
public sealed record EchoParams(string Text);
public sealed record EchoResult(string Text, string Greeting);
public sealed record CounterParams(string Workspace);
public sealed record CounterState(string Workspace, int Value);
public sealed record Ping(string Message);

public static class FixtureContract
{
    public static readonly PluginManifest Manifest = new("fixture", "Fixture", "puzzle-2-line", "1.0.0", PluginApi.Generation, 1)
    {
        Host = "Tereditor.PluginFixture.Host.dll",
        Ui = "Tereditor.PluginFixture.UI.dll",
        Assets = "assets",
        Contributes = new()
        {
            SideTools = [new("board", "Fixture board", "puzzle-2-line", PluginToolSide.Right)],
            FileViewers = [new() { Extensions = ["fixture"], Names = [], Read = PluginFileRead.Text }]
        }
    };

    public static readonly PluginMethod<EchoParams, EchoResult> Echo = new("echo");
    public static readonly PluginMethod<CounterParams, CounterState> Counter = new("counter");
    public static readonly PluginMethod<CounterParams, CounterState> Bump = new("bump");
    public static readonly PluginChannel<CounterState> CounterChannel = PluginChannel<CounterState>.State("counter", Counter, "workspace");
    public static readonly PluginChannel<Ping> Pings = PluginChannel<Ping>.Event("pings");
    public static readonly PluginContract Contract = PluginContract.Create<FixtureSettings>("fixture", 1, [Echo, Counter, Bump], [CounterChannel, Pings], FixtureJson.Default);
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
        context.TerminalEnvironment(_ => new Dictionary<string, string> { ["TEREDITOR_FIXTURE"] = "1" });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    private static CounterState Read(IPluginHostContext context, string workspace) => new(workspace, context.ReadState(StateName(workspace), 0));

    private static string StateName(string workspace) =>
        "counter-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)))[..12];
}

[JsonSerializable(typeof(FixtureSettings))]
[JsonSerializable(typeof(EchoParams))]
[JsonSerializable(typeof(EchoResult))]
[JsonSerializable(typeof(CounterParams))]
[JsonSerializable(typeof(CounterState))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(int))]
internal sealed partial class FixtureJson : JsonSerializerContext;