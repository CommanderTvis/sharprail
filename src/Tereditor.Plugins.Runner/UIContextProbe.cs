using System.Text.Json.Nodes;

using Avalonia.Controls;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.Isolation.UI;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// <c>probe ui-context</c>: a UI-only plugin through the generated proxy, with no Avalonia platform. Its factories
/// return no control; the surface they would show is named by what the factory was called with, so the host can see
/// each factory ran with the arguments it sent.
/// </summary>
internal static class UIContextProbe
{
    private static string? created;

    internal static async Task<int> RunAsync(string? channel)
    {
        PluginJson.Register(IsolationUiJsonContext.Default);
        var init = new TaskCompletionSource<RunnerInit>(TaskCreationOptions.RunContinuationsAsynchronously);
        PeerRuntime? runtime = null;
        var peer = new IsolationPeer(FdTransport.ForRunner(channel), IsolationRole.Runner, new()
        {
            Calls = (call, token) => Serve(runtime!, init.Task, call, token),
            Control = received =>
            {
                if (received.Frame.Kind != IsoFrameKind.Init || init.Task.IsCompleted) throw new IsolationProtocolException($"The host sent {received.Frame.Kind} out of turn.");
                init.TrySetResult(IsoProtocol.DecodeInit(received.Frame.Payload));
            }
        });
        runtime = new PeerRuntime(peer);
        peer.Start();
        peer.Send(new IsoFrame
        {
            Kind = IsoFrameKind.Hello,
            Payload = IsoProtocol.Encode(new RunnerHello(IsoProtocol.Version, Environment.ProcessId, "probe " + RunnerProbe.UIContextMode, RunnerTables.Hash))
        });
        if (await Task.WhenAny(init.Task, peer.Completion) != init.Task) return 1;
        peer.Send(new IsoFrame { Kind = IsoFrameKind.Ready, Payload = IsoProtocol.Encode(new RunnerReady("probe")) });
        try { await peer.Completion; }
        catch (IsolationProtocolException) { return 1; }
        return 0;
    }

    private static async ValueTask<IsoResult> Serve(PeerRuntime runtime, Task<RunnerInit> init, IsoCall call, CancellationToken token)
    {
        if (call.Member == RunnerProbe.RunUIPlugin) return IsoResult.Of(await Plugin(runtime, await init, long.Parse(call.Payload)));
        if (call.TargetObject is { } lent)
            return IsoResult.Of(await PluginUIContextCallbacks.InvokeAsync(call.Member, lent, call.Payload, _ => "surface:" + created, token));
        throw new IsoCallException(IsoError.Unknown, $"The probe has no member {call.Member}.");
    }

    private static Control Factory(string what)
    {
        created = what;
        return null!;
    }

    private static async Task<string> Plugin(PeerRuntime runtime, RunnerInit init, long root)
    {
        IPluginUIContext context = new IsolatedPluginUIContext(runtime, root, new Local(init.PluginId));
        var seen = new List<string> { "id:" + context.Id };
        context.Notify(PluginNotificationKind.Info, "Hello", null);
        context.SideTool(new("board", workspace => Factory("board:" + workspace)));
        context.FileViewer(new(props => Factory("viewer:" + props.Path)) { Matches = path => path.EndsWith(".todo", StringComparison.Ordinal) });
        context.SettingsSection(new("Probe", "settings-3-line", () => Factory("settings")));
        context.HeaderItem(new(() => Factory("header")));
        context.Preference("layout").Set("wide");
        runtime.Mirror(PluginUIContextMembers.PluginPreference_Get, root, new IsoPayload().Raw(new IsoPayload().Value("layout").Finish()).Finish(), "\"wide\"");
        seen.Add("mirror:" + context.Preference("layout").Get());
        await context.UpdateSettingsAsync(new JsonObject { ["columns"] = 3 });
        context.Invalidate();
        try { context.CopyText("secret"); }
        catch (NotSupportedException pending) { seen.Add("pending:" + pending.Message); }
        try { context.SideTool(new("other", _ => Factory("other")) { Retarget = (_, _) => true }); }
        catch (NotSupportedException pending) { seen.Add("pending:" + pending.Message); }
        return string.Join("|", seen);
    }

    /// <summary>What the runner implements itself, as far as this probe needs.</summary>
    private sealed class Local(string id) : IPluginUIContextLocal
    {
        public string Id => id;
        public IPluginUILogger Log => throw new NotSupportedException("The probe has no logger.");
        public IDisposable OnReconnect(Action handler) => throw new NotSupportedException("The probe has no mirror feed.");
        public IDisposable OnSettings<T>(Action<T> handler) where T : class => throw new NotSupportedException("The probe has no mirror feed.");
        public ValueTask<byte[]> ReadAssetAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException("The probe has no assets.");
        public IDisposable WatchHost<T>(Func<PluginHostProjection, T> selector, Action<T, T> listener) => throw new NotSupportedException("The probe has no mirror feed.");
    }
}