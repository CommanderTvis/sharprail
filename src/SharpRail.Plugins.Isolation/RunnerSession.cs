using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Isolation;

/// <summary>The channel's handshake: the runner's <c>Hello</c>, the host's <c>Init</c> and the runner's <c>Ready</c>.</summary>
public static class IsoProtocol
{
    /// <summary>The channel protocol both sides speak; a runner that speaks another is refused.</summary>
    public const int Version = 1;

    public static string Encode(RunnerHello hello) => JsonSerializer.Serialize(hello, IsolationJsonContext.Default.RunnerHello);
    public static string Encode(RunnerInit init) => JsonSerializer.Serialize(init, IsolationJsonContext.Default.RunnerInit);
    public static string Encode(RunnerReady ready) => JsonSerializer.Serialize(ready, IsolationJsonContext.Default.RunnerReady);

    public static RunnerHello DecodeHello(string json) => Decode(json, IsolationJsonContext.Default.RunnerHello);
    public static RunnerInit DecodeInit(string json) => Decode(json, IsolationJsonContext.Default.RunnerInit);
    public static RunnerReady DecodeReady(string json) => Decode(json, IsolationJsonContext.Default.RunnerReady);

    private static T Decode<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { return JsonSerializer.Deserialize(json, type) ?? throw new IsolationProtocolException("An empty handshake frame."); }
        catch (JsonException error) { throw new IsolationProtocolException("A malformed handshake frame.", error); }
    }
}

/// <summary>The runner introduces itself. <see cref="ApiTableHash"/> is empty until the member tables exist.</summary>
public sealed record RunnerHello(int Protocol, int Pid, string Mode, string ApiTableHash = "", string Detail = "");

/// <summary>What the host tells a runner it started.</summary>
public sealed record RunnerInit(string PluginId = "", string Directory = "", string StateDirectory = "", string TempDirectory = "");

/// <summary>The runner is ready to serve.</summary>
public sealed record RunnerReady(string Detail = "");

[JsonSerializable(typeof(RunnerHello))]
[JsonSerializable(typeof(RunnerInit))]
[JsonSerializable(typeof(RunnerReady))]
internal sealed partial class IsolationJsonContext : JsonSerializerContext;

/// <summary>
/// The host's hold on one runner: the process, the channel and the handshake. A protocol violation kills the process;
/// disposing closes the channel (the runner exits on its end) and kills what is still running after a grace.
/// </summary>
public sealed class RunnerSession : IAsyncDisposable
{
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    private RunnerSession(ChildProcess process, IsolationPeer peer, RunnerHello hello)
    {
        Process = process;
        Peer = peer;
        Hello = hello;
    }

    public ChildProcess Process { get; }
    public IsolationPeer Peer { get; }
    public RunnerHello Hello { get; }

    /// <summary>
    /// Starts a runner and completes the handshake within <paramref name="timeout"/>, or kills it and throws. With
    /// <paramref name="apiTableHash"/>, a runner whose generated member tables hash differently is refused.
    /// </summary>
    public static async Task<RunnerSession> StartAsync(ChildProcessStart start, IsolationPeerOptions options, RunnerInit init, TimeSpan timeout,
        CancellationToken cancellationToken = default, string? apiTableHash = null)
    {
        var process = ChildProcess.Start(start);
        var hello = new TaskCompletionSource<RunnerHello>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<RunnerReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new IsolationPeer(process.Channel, IsolationRole.Host, new()
        {
            Calls = options.Calls,
            OneWays = options.OneWays,
            Log = options.Log,
            Control = received =>
            {
                var frame = received.Frame;
                switch (frame.Kind)
                {
                    case IsoFrameKind.Hello when !hello.Task.IsCompleted:
                        var introduced = IsoProtocol.DecodeHello(frame.Payload);
                        if (introduced.Protocol != IsoProtocol.Version)
                            throw new IsolationProtocolException($"The plugin runner speaks channel protocol {introduced.Protocol}; this host speaks {IsoProtocol.Version}.");
                        if (apiTableHash is not null && introduced.ApiTableHash != apiTableHash)
                            throw new IsolationProtocolException($"The plugin runner was built against plugin API table {introduced.ApiTableHash}; this host has {apiTableHash}.");
                        hello.TrySetResult(introduced);
                        return;
                    case IsoFrameKind.Ready when hello.Task.IsCompleted && !ready.Task.IsCompleted:
                        ready.TrySetResult(IsoProtocol.DecodeReady(frame.Payload));
                        return;
                    case IsoFrameKind.Hello or IsoFrameKind.Ready or IsoFrameKind.Init:
                        throw new IsolationProtocolException($"The plugin runner sent {frame.Kind} out of turn.");
                    default:
                        if (options.Control is null) throw new IsolationProtocolException($"The plugin runner sent an unexpected {frame.Kind} frame.");
                        options.Control(received);
                        return;
                }
            }
        });
        _ = peer.Completion.ContinueWith(_ =>
        {
            if (peer.Violation is not null) process.Kill();
        }, TaskScheduler.Default);
        peer.Start();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var introduced = await Handshake(hello.Task, peer, process, limit.Token).ConfigureAwait(false);
            peer.Send(new IsoFrame { Kind = IsoFrameKind.Init, Payload = IsoProtocol.Encode(init) });
            await Handshake(ready.Task, peer, process, limit.Token).ConfigureAwait(false);
            return new RunnerSession(process, peer, introduced);
        }
        catch
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<T> Handshake<T>(Task<T> step, IsolationPeer peer, ChildProcess process, CancellationToken cancellationToken)
    {
        var ended = Task.WhenAny(peer.Completion, process.Exited);
        try
        {
            if (await Task.WhenAny(step, ended).WaitAsync(cancellationToken).ConfigureAwait(false) == step) return await step.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new TimeoutException("The plugin runner did not start in time."); }
        throw new IOException(peer.Violation is { } violation
            ? "The plugin runner broke the channel protocol: " + violation
            : "The plugin runner exited before it was ready.");
    }

    public async ValueTask DisposeAsync()
    {
        await Peer.DisposeAsync().ConfigureAwait(false);
        try { await Process.Exited.WaitAsync(ExitGrace).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await Process.DisposeAsync().ConfigureAwait(false);
    }
}