using System.Threading.Channels;

namespace Tereditor.Plugins.Isolation;

/// <summary>
/// The host's side of an external plugin's UI half, as a client reaches it: the plugin runs in its one process on the
/// host, and each client opens its own session there.
/// </summary>
public interface IIsolatedUIHost
{
    /// <summary>
    /// Activates the UI half of an active external plugin on a new context for <paramref name="clientKey"/> and answers
    /// the session, holding what the activation registered. Throws when the plugin is not running isolated.
    /// </summary>
    ValueTask<IIsolatedUISession> OpenAsync(string pluginId, string clientKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// One client's context in an external plugin's process. What the plugin asks of its context arrives in order on
/// <see cref="Calls"/>; disposing it runs the plugin's disposer for this context. <see cref="Calls"/> completes when the
/// session ends, with an <see cref="IOException"/> when the plugin's process did.
/// </summary>
public interface IIsolatedUISession : IAsyncDisposable
{
    /// <summary>The member tables' hash the runner introduced itself with: the host context's, a dot, the UI context's.</summary>
    string ApiTableHash { get; }

    /// <summary>What <c>Activate</c> registered on this context, in order.</summary>
    IReadOnlyList<UIRegistration> Registrations { get; }

    /// <summary>Every later call and one-way on this context, in arrival order.</summary>
    ChannelReader<IsoUICall> Calls { get; }

    /// <summary>Calls a callback the plugin lent and answers its JSON result.</summary>
    ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken = default);

    /// <summary>Calls a <see langword="void"/> callback the plugin lent.</summary>
    void OneWay(int member, long handle, string payload);

    /// <summary>Stores a mirrored member's value in the plugin's process; completes once it is stored.</summary>
    ValueTask MirrorAsync(int member, string arguments, string json, CancellationToken cancellationToken = default);

    /// <summary>The surfaces this context's factories show, each a control the plugin's process renders.</summary>
    IPluginSurfaceService Surfaces { get; }
}

/// <summary>A call the plugin made on its context: answered when it awaits a result, or a one-way when it does not.</summary>
public sealed class IsoUICall(int member, string payload, bool answered)
{
    private readonly TaskCompletionSource<string>? reply = answered ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;

    public int Member { get; } = member;
    public string Payload { get; } = payload;
    /// <summary>Whether the plugin awaits an answer.</summary>
    public bool Answered => reply is not null;
    /// <summary>The answer the plugin awaits; an empty one at once for a one-way.</summary>
    public Task<string> Reply => reply?.Task ?? Task.FromResult("");

    public void Answer(string json) => reply?.TrySetResult(json);
    public void Fail(Exception error) => reply?.TrySetException(error);
    /// <summary>The session ended before anyone answered.</summary>
    public void Abandon() => reply?.TrySetCanceled();
}