using System.Collections.Concurrent;

using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.Isolation.UI;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// <c>plugin</c>: one external plugin's process. It loads the UI half named in <c>Init</c> before it answers
/// <c>Ready</c>, then activates it once per client context the host lends (<see cref="RunnerPlugin"/>). The plugin's
/// code runs on the Avalonia UI thread of <see cref="RunnerPlatform"/>; a factory runs only when the host opens a
/// surface for it, and its control is rendered by <see cref="SurfaceRenderer"/>.
/// </summary>
internal static class PluginMode
{
    private static readonly HashSet<int> Factories =
    [
        PluginUIContextMembers.SideToolRegistration_Create, PluginUIContextMembers.SettingsSectionRegistration_Create,
        PluginUIContextMembers.FileViewerRegistration_Create, PluginUIContextMembers.HeaderItemRegistration_Create,
    ];

    internal static int Run(string? channel) => RunnerPlatform.Run(() => RunAsync(channel));

    private static async Task<int> RunAsync(string? channel)
    {
        PluginJson.Register(IsolationUiJsonContext.Default);
        var init = new TaskCompletionSource<RunnerInit>(TaskCreationOptions.RunContinuationsAsynchronously);
        Plugin? plugin = null;
        IsolationPeer? peer = null;
        peer = new IsolationPeer(FdTransport.ForRunner(channel), IsolationRole.Runner, new()
        {
            Calls = (call, token) => plugin is { } loaded ? loaded.Serve(call, token) : throw new IsoCallException(IsoError.Unknown, "No plugin is loaded."),
            OneWays = call =>
            {
                if (plugin is { } loaded) loaded.OneWay(call);
            },
            Control = received =>
            {
                if (received.Frame.Kind != IsoFrameKind.Init || init.Task.IsCompleted) throw new IsolationProtocolException($"The host sent {received.Frame.Kind} out of turn.");
                init.TrySetResult(IsoProtocol.DecodeInit(received.Frame.Payload));
            },
            Log = message => Console.Error.WriteLine("[error] " + message),
        });
        peer.Start();
        peer.Send(new IsoFrame { Kind = IsoFrameKind.Hello, Payload = IsoProtocol.Encode(new RunnerHello(IsoProtocol.Version, Environment.ProcessId, RunnerPlugin.Mode, RunnerTables.Hash)) });
        if (await Task.WhenAny(init.Task, peer.Completion) != init.Task) return 1;
        var started = await init.Task;
        try
        {
            plugin = await Dispatcher.UIThread.InvokeAsync(() =>
                new Plugin(peer, new PeerRuntime(peer), started, PluginLoadContext.LoadUI(started.Directory, started.Entry)));
        }
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

    /// <summary>The loaded UI half, its activations (one per client context) and their surfaces.</summary>
    private sealed class Plugin(IsolationPeer peer, PeerRuntime runtime, RunnerInit init, PluginUIModule module)
    {
        private static readonly bool Diagnostics = Environment.GetEnvironmentVariable(SurfaceProtocol.DiagnosticsVariable) == "1";

        private readonly ConcurrentDictionary<long, Activation> activations = new();
        private readonly SurfaceRenderer surfaces = new(peer);

        public async ValueTask<IsoResult> Serve(IsoCall call, CancellationToken token)
        {
            if (call.TargetObject is { } lent)
            {
                if (Factories.Contains(call.Member)) throw new IsoCallException(IsoError.InvalidParams, "A factory runs only for a surface.");
                return IsoResult.Of(await Dispatcher.UIThread.InvokeAsync(() =>
                    PluginUIContextCallbacks.InvokeAsync(call.Member, lent, call.Payload, _ => throw new IsoCallException(IsoError.InvalidParams, "Not a factory."), token).AsTask()));
            }
            if (call.Member == RunnerPlugin.ActivateUI)
            {
                var request = RunnerPlugin.DecodeActivation(call.Payload);
                return IsoResult.Of(await Dispatcher.UIThread.InvokeAsync(() => Activate(request)));
            }
            if (call.Member == RunnerPlugin.DisposeUI)
            {
                var context = long.Parse(call.Payload, System.Globalization.CultureInfo.InvariantCulture);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    surfaces.CloseContext(context);
                    if (activations.TryRemove(context, out var ended)) ended.Dispose();
                });
                return IsoResult.Of("");
            }
            if (call.Member == RunnerPlugin.Mirror)
            {
                var mirror = RunnerPlugin.DecodeMirror(call.Payload);
                runtime.Mirror(mirror.Member, mirror.Context, mirror.Arguments, mirror.Json);
                if (mirror.Member == PluginUIContextMembers.PluginUIContext_Settings && activations.TryGetValue(mirror.Context, out var activation))
                    await Dispatcher.UIThread.InvokeAsync(() => activation.SettingsChanged(mirror.Json));
                return IsoResult.Of("");
            }
            if (call.Member == SurfaceProtocol.Open)
            {
                var open = SurfaceProtocol.Decode<SurfaceOpen>(call.Payload);
                await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(open, token));
                return IsoResult.Of("");
            }
            if (call.Member == SurfaceProtocol.Capture && Diagnostics)
            {
                var request = SurfaceProtocol.Decode<SurfaceCaptureRequest>(call.Payload);
                return IsoResult.Of(SurfaceProtocol.Encode(await Dispatcher.UIThread.InvokeAsync(() => surfaces.Capture(request))));
            }
            throw new IsoCallException(IsoError.Unknown, $"The plugin runner has no member {call.Member}.");
        }

        // A factory the plugin lent on this context runs, and its control becomes the surface the host numbered.
        private async Task OpenAsync(SurfaceOpen open, CancellationToken token)
        {
            if (!Factories.Contains(open.Member)) throw new IsoCallException(IsoError.InvalidParams, "Not a factory.");
            if (!activations.ContainsKey(open.Context)) throw new IsoCallException(IsoError.InvalidParams, "That context is not active.");
            if (peer.Handles.Resolve(open.Handle, out var factory, out _) != HandleState.Live || factory is null)
                throw new IsoCallException(IsoError.Released, "The factory is gone.");
            RunnerPlatform.ApplyTheme(open.Theme);
            await PluginUIContextCallbacks.InvokeAsync(open.Member, factory, open.Payload, control =>
            {
                surfaces.Mount(open.Context, open.Surface, control as Control ?? throw new IsoCallException(IsoError.Failed, "The factory made no control."));
                return "";
            }, token);
        }

        public void OneWay(IsoCall call)
        {
            Action apply = call.Member switch
            {
                var member when member == SurfaceProtocol.Resize => Bind(SurfaceProtocol.Decode<SurfaceResize>(call.Payload), surfaces.Resize),
                var member when member == SurfaceProtocol.Input => Bind(SurfaceProtocol.Decode<SurfaceInput>(call.Payload), surfaces.Input),
                var member when member == SurfaceProtocol.Ack => Bind(SurfaceProtocol.Decode<SurfaceAck>(call.Payload), surfaces.Acknowledge),
                var member when member == SurfaceProtocol.Focus => Bind(SurfaceProtocol.Decode<SurfaceFocus>(call.Payload), surfaces.Focus),
                var member when member == SurfaceProtocol.Close => Bind(SurfaceProtocol.Decode<SurfaceClose>(call.Payload), close => surfaces.Close(close.Surface)),
                var member when member == SurfaceProtocol.Theme => Bind(SurfaceProtocol.Decode<SurfaceTheme>(call.Payload), theme => RunnerPlatform.ApplyTheme(theme.Json)),
                _ => throw new IsoCallException(IsoError.Unknown, $"The plugin runner has no one-way {call.Member}."),
            };
            surfaces.Enqueue(apply);
        }

        private static Action Bind<T>(T value, Action<T> action) => () => action(value);

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