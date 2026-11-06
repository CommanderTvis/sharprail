using System.Collections.Concurrent;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.Isolation.UI;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// <c>plugin</c>: one external plugin's process. It loads the UI half named in <c>Init</c> before it answers
/// <c>Ready</c>, then activates it once per client context the host lends (<see cref="RunnerPlugin"/>). A plugin's
/// views are not shown yet, so a factory the trusted side calls is refused before it runs.
/// </summary>
internal static class PluginMode
{
    private static readonly HashSet<int> Factories =
    [
        PluginUIContextMembers.SideToolRegistration_Create, PluginUIContextMembers.SettingsSectionRegistration_Create,
        PluginUIContextMembers.FileViewerRegistration_Create, PluginUIContextMembers.HeaderItemRegistration_Create,
    ];

    internal static async Task<int> RunAsync(string? channel)
    {
        PluginJson.Register(IsolationUiJsonContext.Default);
        var init = new TaskCompletionSource<RunnerInit>(TaskCreationOptions.RunContinuationsAsynchronously);
        Plugin? plugin = null;
        IsolationPeer? peer = null;
        peer = new IsolationPeer(FdTransport.ForRunner(channel), IsolationRole.Runner, new()
        {
            Calls = (call, token) => plugin is { } loaded ? loaded.Serve(call, token) : throw new IsoCallException(IsoError.Unknown, "No plugin is loaded."),
            Control = received =>
            {
                if (received.Frame.Kind != IsoFrameKind.Init || init.Task.IsCompleted) throw new IsolationProtocolException($"The host sent {received.Frame.Kind} out of turn.");
                init.TrySetResult(IsoProtocol.DecodeInit(received.Frame.Payload));
            },
        });
        peer.Start();
        peer.Send(new IsoFrame { Kind = IsoFrameKind.Hello, Payload = IsoProtocol.Encode(new RunnerHello(IsoProtocol.Version, Environment.ProcessId, RunnerPlugin.Mode, RunnerTables.Hash)) });
        if (await Task.WhenAny(init.Task, peer.Completion) != init.Task) return 1;
        var started = await init.Task;
        try { plugin = new Plugin(new PeerRuntime(peer), started, PluginLoadContext.LoadUI(started.Directory, started.Entry)); }
        catch (Exception error)
        {
            peer.Send(new IsoFrame { Kind = IsoFrameKind.Ready, Payload = IsoProtocol.Encode(new RunnerReady(Error: error.Message)) });
            await Task.WhenAny(peer.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
            return 1;
        }
        peer.Send(new IsoFrame { Kind = IsoFrameKind.Ready, Payload = IsoProtocol.Encode(new RunnerReady(started.PluginId)) });
        try { await peer.Completion; }
        catch (IsolationProtocolException) { return 1; }
        return 0;
    }

    /// <summary>The loaded UI half and its activations, one per client context.</summary>
    private sealed class Plugin(PeerRuntime runtime, RunnerInit init, PluginUIModule module)
    {
        private readonly ConcurrentDictionary<long, Activation> activations = new();

        public async ValueTask<IsoResult> Serve(IsoCall call, CancellationToken token)
        {
            if (call.TargetObject is { } lent)
            {
                if (Factories.Contains(call.Member)) throw new IsoCallException(IsoError.Failed, "Plugin views are not shown yet.");
                return IsoResult.Of(await PluginUIContextCallbacks.InvokeAsync(call.Member, lent, call.Payload,
                    _ => throw new IsoCallException(IsoError.Failed, "Plugin views are not shown yet."), token));
            }
            if (call.Member == RunnerPlugin.ActivateUI) return IsoResult.Of(Activate(RunnerPlugin.DecodeActivation(call.Payload)));
            if (call.Member == RunnerPlugin.DisposeUI)
            {
                if (activations.TryRemove(long.Parse(call.Payload, System.Globalization.CultureInfo.InvariantCulture), out var ended)) ended.Dispose();
                return IsoResult.Of("");
            }
            if (call.Member == RunnerPlugin.Mirror)
            {
                var mirror = RunnerPlugin.DecodeMirror(call.Payload);
                runtime.Mirror(mirror.Member, mirror.Context, mirror.Arguments, mirror.Json);
                if (mirror.Member == PluginUIContextMembers.PluginUIContext_Settings && activations.TryGetValue(mirror.Context, out var activation)) activation.SettingsChanged(mirror.Json);
                return IsoResult.Of("");
            }
            throw new IsoCallException(IsoError.Unknown, $"The plugin runner has no member {call.Member}.");
        }

        private string Activate(UIActivation request)
        {
            if (activations.ContainsKey(request.Context)) throw new IsoCallException(IsoError.InvalidParams, "That context is already active.");
            runtime.Mirror(PluginUIContextMembers.PluginUIContext_Settings, request.Context, new IsoPayload().Finish(), request.Settings);
            var activation = new Activation(init);
            var buffered = new Registrations(runtime, request.Context);
            IPluginUIContext context = new IsolatedPluginUIContext(buffered, request.Context, activation);
            activation.Disposer = module.Activate(context);
            activations[request.Context] = activation;
            return RunnerPlugin.Encode(buffered.Close());
        }
    }

    /// <summary>
    /// The runtime the plugin's context calls through: while <c>Activate</c> runs, one-ways on its context are kept and
    /// answered together in the activation's reply, so the trusted side replays every registration at once.
    /// </summary>
    private sealed class Registrations(PeerRuntime inner, long context) : IIsolatedRuntime
    {
        private readonly Lock gate = new();
        private List<UIRegistration>? kept = [];

        public List<UIRegistration> Close()
        {
            lock (gate)
            {
                var all = kept!;
                kept = null;
                return all;
            }
        }

        public void OneWay(int member, long target, string payload)
        {
            lock (gate)
                if (kept is not null && target == context)
                {
                    kept.Add(new(member, payload));
                    return;
                }
            inner.OneWay(member, target, payload);
        }

        public ValueTask<string> CallAsync(int member, long target, string payload, CancellationToken cancellationToken) => inner.CallAsync(member, target, payload, cancellationToken);
        public long Lend(object value) => inner.Lend(value);
        public T ReadMirror<T>(int member, long target, string payload) => inner.ReadMirror<T>(member, target, payload);
    }

    /// <summary>What the runner implements itself for one client context, and that context's activation.</summary>
    private sealed class Activation(RunnerInit init) : IPluginUIContextLocal, IDisposable
    {
        private readonly List<Action<string>> settingsObservers = [];

        public PluginDisposer? Disposer { get; set; }
        public string Id => init.PluginId;
        public IPluginUILogger Log { get; } = new Logger();

        public void SettingsChanged(string json)
        {
            Action<string>[] observers;
            lock (settingsObservers) observers = [.. settingsObservers];
            foreach (var observer in observers)
            {
                try { observer(json); }
                catch (Exception error) { Log.Error("A settings observer threw: " + error.Message); }
            }
        }

        public IDisposable OnSettings<T>(Action<T> handler) where T : class
        {
            Action<string> observer = json => handler(IsoPayload.Read<T>(json));
            lock (settingsObservers) settingsObservers.Add(observer);
            return new Subscription(() => { lock (settingsObservers) settingsObservers.Remove(observer); });
        }

        // One process serves every client and never reconnects to the host; a client's reconnect is the trusted side's.
        public IDisposable OnReconnect(Action handler) => new Subscription(() => { });

        public async ValueTask<byte[]> ReadAssetAsync(string path, CancellationToken cancellationToken = default)
        {
            if (init.Assets.Length == 0) throw new InvalidOperationException($"Plugin {Id} declares no assets.");
            var root = Path.GetFullPath(Path.Combine(init.Directory, init.Assets));
            var file = Path.GetFullPath(Path.Combine(root, path.TrimStart('/')));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                throw new FileNotFoundException($"Plugin {Id} has no asset {path}.");
            return await File.ReadAllBytesAsync(file, cancellationToken);
        }

        public IDisposable WatchHost<T>(Func<PluginHostProjection, T> selector, Action<T, T> listener) => throw IsolationPending.Member("IPluginUIContext.WatchHost");

        public void Dispose()
        {
            lock (settingsObservers) settingsObservers.Clear();
            try { Disposer?.Invoke(); }
            catch (Exception error) { Log.Error("The disposer threw: " + error.Message); }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    /// <summary>Writes to standard error, which the host reads into the plugin's log.</summary>
    private sealed class Logger : IPluginUILogger
    {
        private static void Write(string level, string message, IReadOnlyDictionary<string, string>? fields) =>
            Console.Error.WriteLine($"[{level}] {message}" + (fields is null ? "" : " " + string.Join(" ", fields.Select(field => field.Key + "=" + field.Value))));

        public void Debug(string message, IReadOnlyDictionary<string, string>? fields = null) => Write("debug", message, fields);
        public void Info(string message, IReadOnlyDictionary<string, string>? fields = null) => Write("info", message, fields);
        public void Warn(string message, IReadOnlyDictionary<string, string>? fields = null) => Write("warn", message, fields);
        public void Error(string message, IReadOnlyDictionary<string, string>? fields = null) => Write("error", message, fields);
    }
}