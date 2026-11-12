using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Isolation.UI;

/// <summary>
/// An external plugin's UI half as a client holds it: the plugin runs in its own process on the host, and this module
/// replays what its activation registered onto the client's real context while the activation guard is open, then
/// dispatches every later call on the UI thread. A factory answers the control the client's surface factory makes for
/// a surface of the plugin's process.
/// </summary>
public sealed class RemotePluginUIModule : PluginUIModule, IAsyncDisposable
{
    private readonly IIsolatedUISession session;
    private readonly Func<Func<IPluginSurface>, Control> surfaces;
    private readonly Func<string> theme;
    private readonly Action ended;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mirroring = new(1, 1);
    private IPluginUIContext? context;
    private string mirrored = "";
    private int disposed;

    // The UI types the plugin's calls carry, with their enum converters.
    static RemotePluginUIModule() => PluginJson.Register(IsolationUiJsonContext.Default);

    private RemotePluginUIModule(IIsolatedUISession session, Action ended, Func<Func<IPluginSurface>, Control> surfaces, Func<string> theme)
    {
        this.session = session;
        this.ended = ended;
        this.surfaces = surfaces;
        this.theme = theme;
    }

    /// <summary>
    /// Opens a session for this client with an active external plugin, refusing a runner whose UI member table differs
    /// from the one this client dispatches with. <paramref name="ended"/> hears when the session ends without this
    /// client closing it: the plugin's process ended or the plugin was turned off. <paramref name="surfaces"/> makes the
    /// client's control for a plugin surface from what opens one, which it may call again once the last closed, and
    /// <paramref name="theme"/> answers the client's kit theme as JSON, which a surface's factory runs under.
    /// </summary>
    public static async Task<RemotePluginUIModule> OpenAsync(IIsolatedUIHost host, string pluginId, string clientKey, Action ended,
        Func<Func<IPluginSurface>, Control> surfaces, Func<string> theme, CancellationToken cancellationToken = default)
    {
        var session = await host.OpenAsync(pluginId, clientKey, cancellationToken).ConfigureAwait(false);
        if (session.ApiTableHash.Split('.') is not [_, var table] || table != PluginUIContextMembers.Hash)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The plugin runner was built against UI member table {session.ApiTableHash}; this app has {PluginUIContextMembers.Hash}.");
        }
        return new(session, ended, surfaces, theme);
    }

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        this.context = context;
        var callbacks = new Callbacks(this);
        foreach (var registration in session.Registrations)
        {
            // The UI thread never waits on what the plugin's process sent: a replayed call that would wait fails the activation.
            var replayed = PluginUIContextDispatcher.DispatchAsync(registration.Member, registration.Payload, context, callbacks, lifetime.Token);
            if (!replayed.IsCompleted)
            {
                lifetime.Cancel();
                throw new InvalidOperationException("The plugin's activation sent a call that waits; only registrations replay.");
            }
            replayed.GetAwaiter().GetResult();
        }
        mirrored = Settings();
        context.OnSettings<JsonObject>(changed => _ = MirrorSettingsAsync());
        _ = PumpAsync(callbacks);
        return () => _ = DisposeAsync().AsTask();
    }

    private string Settings() => context!.Settings<JsonObject>().ToJsonString();

    // The plugin reads its settings from a copy in its process; it is brought up to date before any answer, so a
    // plugin that awaited UpdateSettingsAsync reads what it wrote.
    private async Task MirrorSettingsAsync()
    {
        await mirroring.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            var settings = await Dispatcher.UIThread.InvokeAsync(Settings);
            if (settings == mirrored) return;
            await session.MirrorAsync(PluginUIContextMembers.PluginUIContext_Settings, new IsoPayload().Finish(), settings, lifetime.Token).ConfigureAwait(false);
            mirrored = settings;
        }
        finally { mirroring.Release(); }
    }

    private async Task PumpAsync(Callbacks callbacks)
    {
        try
        {
            await foreach (var call in session.Calls.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                await Dispatcher.UIThread.InvokeAsync(() => _ = DispatchAsync(call, callbacks));
        }
        catch (OperationCanceledException) { }
        catch (IOException error)
        {
            Console.Error.WriteLine($"Plugin {context?.Id}: {error.Message}");
            ended();
        }
    }

    private async Task DispatchAsync(IsoUICall call, Callbacks callbacks)
    {
        try
        {
            var result = await PluginUIContextDispatcher.DispatchAsync(call.Member, call.Payload, context!, callbacks, lifetime.Token);
            if (!call.Answered) return;
            await MirrorSettingsAsync();
            call.Answer(result);
        }
        catch (Exception error)
        {
            if (!call.Answered) Console.Error.WriteLine($"Plugin {context?.Id}: a call on its context failed: {error.Message}");
            else call.Fail(error is IsoCallException ? error : new IsoCallException(IsoError.Failed, error.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The way back into the plugin's process for the delegates the dispatcher builds.</summary>
    private sealed class Callbacks(RemotePluginUIModule module) : IIsolatedCallbacks
    {
        private readonly ConcurrentDictionary<(int Member, long Handle, string Payload), string> answers = new();

        public ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken) =>
            module.session.CallAsync(member, handle, payload, cancellationToken);

        public void OneWay(int member, long handle, string payload) => module.session.OneWay(member, handle, payload);

        // The UI thread never waits on the plugin: a resolver answers from the last answer for these arguments, or
        // as if the plugin had none, and asks again in the background, re-evaluating the plugin's predicates once it answers.
        public T Sync<T>(int member, long handle, string payload, string crossing, Func<string, T> read)
        {
            if (answers.TryGetValue((member, handle, payload), out var known)) return read(known);
            _ = AskAsync(member, handle, payload);
            return default!;
        }

        private async Task AskAsync(int member, long handle, string payload)
        {
            try
            {
                answers[(member, handle, payload)] = await module.session.CallAsync(member, handle, payload, module.lifetime.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref module.disposed) == 0) module.context?.Invalidate(); });
            }
            catch (Exception error) when (error is IsoCallException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        public object Surface(int member, long handle, string payload) => module.surfaces(() => module.session.Surfaces.Open(member, handle, payload, module.theme()));
    }
}