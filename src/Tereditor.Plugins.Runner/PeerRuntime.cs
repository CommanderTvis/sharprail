using System.Collections.Concurrent;

using Tereditor.Plugins.Isolation;

namespace Tereditor.Plugins.Runner;

/// <summary>
/// The generated proxies' way to the trusted side over the runner's channel: calls and one-ways go out on the context's
/// handle, lent callbacks are minted in the channel's handle table, and mirrored reads come from local copies.
/// </summary>
internal sealed class PeerRuntime(IsolationPeer peer) : IIsolatedRuntime
{
    private readonly ConcurrentDictionary<(int Member, long Target, string Payload), string> mirrors = new();

    public async ValueTask<string> CallAsync(int member, long target, string payload, CancellationToken cancellationToken) =>
        (await peer.CallAsync(member, payload, target, cancellationToken: cancellationToken).ConfigureAwait(false)).Payload;

    public void OneWay(int member, long target, string payload) => peer.OneWay(member, payload, target);

    public long Lend(object value) => peer.Handles.Mint(value);

    public T ReadMirror<T>(int member, long target, string payload) => mirrors.TryGetValue((member, target, payload), out var json)
        ? IsoPayload.Read<T>(json)
        : throw new InvalidOperationException("The trusted side has not mirrored this value yet.");

    /// <summary>Stores a mirrored member's value for these arguments.</summary>
    public void Mirror(int member, long target, string payload, string json) => mirrors[(member, target, payload)] = json;
}