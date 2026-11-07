using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Isolation;

using RunnerProcess = SharpRail.Plugins.Isolation.ChildProcess;

namespace SharpRail.Host.Core.Plugins.Isolation;

/// <summary>
/// The isolated runtime's seam (Security.SPEC.md): null in shipping compositions, set only by the checks until external
/// plugins are switched on. With it, an enabled external plugin with only a UI half runs in its own runner process.
/// </summary>
public sealed record PluginIsolation
{
    /// <summary>The plugin runner executable; null when this build has none.</summary>
    public required string? Runner { get; init; }

    /// <summary>Variables added to the runner's environment, which otherwise holds nothing of the host's.</summary>
    public IReadOnlyDictionary<string, string> RunnerEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Whether external plugins need the platform sandbox. None is applied yet, so a seam that requires one refuses
    /// them; only the checks run them unconfined.
    /// </summary>
    public bool RequireSandbox { get; init; } = true;

    /// <summary>How long a runner may take to start and load its plugin.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Why this seam refuses an external plugin that is otherwise runnable, or null when it runs it.</summary>
    internal string? Refusal(PluginManifest manifest)
    {
        if (Runner is null || !File.Exists(Runner)) return "This build has no plugin runner.";
        if (RequireSandbox) return "This host cannot sandbox external plugins yet.";
        if (manifest.Host is not null) return "External plugins with a host half are not supported in this version.";
        if (manifest.Ui is null) return "This external plugin has no UI half to run.";
        return null;
    }
}

/// <summary>
/// One activation of an external plugin: its runner process, from spawn to exit, and the UI sessions it serves, one
/// per client context. Disposing it ends every session and the process.
/// </summary>
internal sealed class IsolatedPlugin : IAsyncDisposable
{
    /// <summary>A UI session whose client leaves this many calls unread ends.</summary>
    private const int QueuedCalls = 1024;
    /// <summary>An activation that registers more than this is refused.</summary>
    private const int MostRegistrations = 1024;
    /// <summary>A call's arguments larger than this are refused.</summary>
    private const int PayloadChars = 256 * 1024;

    private readonly ConcurrentDictionary<long, Session> sessions = new();
    private RunnerSession? runner;
    private int lastSurface;
    private int requested;
    private Task? disposal;

    private IsolatedPlugin(string id) => Id = id;

    public string Id { get; }
    public int Pid => runner!.Process.Pid;
    public Task<int?> Exited => runner!.Process.Exited;
    /// <summary>Set before the host ends the process, so the exit is not a crash.</summary>
    public bool Requested => Volatile.Read(ref requested) != 0;

    /// <summary>Starts the runner for <paramref name="manifest"/>'s UI half and waits until it has loaded it, or throws with why.</summary>
    public static async Task<IsolatedPlugin> StartAsync(PluginIsolation seam, PluginManifest manifest, string directory, string stateDirectory,
        Action<string> log, CancellationToken cancellationToken)
    {
        var plugin = new IsolatedPlugin(manifest.Id);
        var home = Path.Combine(stateDirectory, "plugin-state", manifest.Id, "files");
        var temp = Path.Combine(stateDirectory, "plugin-state", manifest.Id, "temp");
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);
        var environment = RunnerProcess.RunnerEnvironment(home, temp);
        foreach (var (name, value) in seam.RunnerEnvironment) environment[name] = value;
        var start = new ChildProcessStart { Program = seam.Runner!, Arguments = [RunnerPlugin.Mode], Environment = environment, WorkingDirectory = home, Log = log };
        var options = new IsolationPeerOptions
        {
            Calls = (call, token) => call.TargetObject is Session session
                ? session.CallAsync(call, token)
                : throw new IsoCallException(IsoError.Unknown, "The host serves nothing there."),
            OneWays = call =>
            {
                if (call.TargetObject is Session session) session.OneWay(call);
            },
            Log = log,
        };
        var init = new RunnerInit(manifest.Id, directory, home, temp, manifest.Ui!, manifest.Assets ?? "");
        plugin.runner = await RunnerSession.StartAsync(start, options, init, seam.StartTimeout, cancellationToken).ConfigureAwait(false);
        if (plugin.runner.Hello.ApiTableHash.Split('.')[0] != PluginHostContextMembers.Hash)
        {
            await plugin.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The plugin runner was built against another plugin API.");
        }
        if (plugin.runner.Ready.Error is { Length: > 0 } error)
        {
            await plugin.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }
        _ = plugin.runner.Peer.Completion.ContinueWith(_ => plugin.EndSessions(new IOException("The plugin's process ended.")), TaskScheduler.Default);
        return plugin;
    }

    /// <summary>Activates the UI half on a new context for a client, with <paramref name="settings"/> (the namespace without <c>enabled</c>) mirrored first.</summary>
    public async Task<IIsolatedUISession> OpenAsync(string settings, CancellationToken cancellationToken)
    {
        var peer = runner!.Peer;
        var session = new Session(this);
        var context = peer.Handles.Mint(session);
        session.Context = context;
        session.Relay = new UiSessionRelay(peer, context, () => Interlocked.Increment(ref lastSurface));
        sessions[context] = session;
        try
        {
            var reply = await peer.CallAsync(RunnerPlugin.ActivateUI, RunnerPlugin.Encode(new UIActivation(context, settings)), cancellationToken: cancellationToken).ConfigureAwait(false);
            session.Registrations = RunnerPlugin.DecodeRegistrations(reply.Payload);
            if (session.Registrations.Count > MostRegistrations || session.Registrations.Any(registration => registration.Payload.Length > PayloadChars))
                throw new IsoCallException(IsoError.InvalidParams, "The plugin registered more than a client takes.");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EndSessions(Exception reason)
    {
        foreach (var session in sessions.Values) session.End(reason);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref requested, 1) == 0) disposal = EndAsync();
        return new(disposal!);
    }

    private async Task EndAsync()
    {
        EndSessions(new IOException("The plugin was turned off."));
        if (runner is not null) await runner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The namespace a client's context starts with: the stored settings without <c>enabled</c>.</summary>
    public static string Settings(JsonElement? stored)
    {
        var settings = stored is { ValueKind: JsonValueKind.Object } value ? JsonObject.Create(value.Clone())! : [];
        settings.Remove("enabled");
        return settings.ToJsonString();
    }

    /// <summary>One client context: the handle the runner calls on, and the queue its calls wait in for the client.</summary>
    private sealed class Session(IsolatedPlugin plugin) : IIsolatedUISession
    {
        private readonly Channel<IsoUICall> calls = System.Threading.Channels.Channel.CreateUnbounded<IsoUICall>();
        private int disposed;

        public long Context { get; set; }
        public UiSessionRelay Relay { get; set; } = null!;
        public IPluginSurfaceService Surfaces => Relay;
        public string ApiTableHash => plugin.runner!.Hello.ApiTableHash;
        public IReadOnlyList<UIRegistration> Registrations { get; set; } = [];
        public ChannelReader<IsoUICall> Calls => calls.Reader;

        public async ValueTask<IsoResult> CallAsync(IsoCall call, CancellationToken cancellationToken)
        {
            var queued = Queue(call, answered: true);
            using var cancelled = cancellationToken.Register(queued.Abandon);
            return IsoResult.Of(await queued.Reply.ConfigureAwait(false));
        }

        public void OneWay(IsoCall call)
        {
            try
            {
                if (call.Member == SurfaceProtocol.Frame) Relay.Frame(call.Payload, call.Bytes);
                else if (call.Member == SurfaceProtocol.FocusLeave) Relay.FocusLeave(call.Payload);
                else Queue(call, answered: false);
            }
            catch (IsoCallException) { }
        }

        private IsoUICall Queue(IsoCall call, bool answered)
        {
            if (call.Payload.Length > PayloadChars) throw new IsoCallException(IsoError.InvalidParams, "The arguments are larger than a client takes.");
            if (calls.Reader.Count >= QueuedCalls)
            {
                End(new IOException("The plugin sent more than its client could take."));
                throw new IsoCallException(IsoError.Failed, "The client is not keeping up.");
            }
            var queued = new IsoUICall(call.Member, call.Payload, answered);
            if (!calls.Writer.TryWrite(queued)) throw new IsoCallException(IsoError.Disabled, "The client's session has ended.");
            return queued;
        }

        public async ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken = default) =>
            (await plugin.runner!.Peer.CallAsync(member, payload, handle, cancellationToken: cancellationToken).ConfigureAwait(false)).Payload;

        public void OneWay(int member, long handle, string payload) => plugin.runner!.Peer.OneWay(member, payload, handle);

        public async ValueTask MirrorAsync(int member, string arguments, string json, CancellationToken cancellationToken = default) =>
            await plugin.runner!.Peer.CallAsync(RunnerPlugin.Mirror, RunnerPlugin.Encode(new UIMirror(Context, member, arguments, json)), cancellationToken: cancellationToken).ConfigureAwait(false);

        public void End(Exception? reason)
        {
            Relay.End(reason);
            if (!calls.Writer.TryComplete(reason)) return;
            while (calls.Reader.TryRead(out var left)) left.Abandon();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            End(null);
            if (!plugin.sessions.TryRemove(Context, out _)) return;
            var peer = plugin.runner!.Peer;
            try { await peer.CallAsync(RunnerPlugin.DisposeUI, Context.ToString(System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) when (error is IsoCallException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
            peer.Handles.Release(Context);
        }
    }
}