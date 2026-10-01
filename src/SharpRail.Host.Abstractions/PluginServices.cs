using SharpRail.Plugins.Api;

namespace SharpRail.Host.Abstractions;

// A payload is the typed value in process and a JsonElement after the wire; PluginJson.Convert reads either.
// ClientKey names one app instance connected to the host, so a publish can be addressed back to it.
public sealed record PluginCallRequest(string PluginId, string Method, object? Params, string ClientKey);

// Key, for a keyed state channel, is an object whose JSON properties are the scope's key fields; the host
// delivers only pushes whose key fields equal them.
public sealed record PluginSubscription(string PluginId, string Channel, object? Key, string ClientKey);

/// <summary>
/// The host's plugin runtime as a client sees it. The roster also arrives on every <see cref="HostState"/>
/// snapshot; settings and plugin roots change through <see cref="IHostStateService"/>.
/// </summary>
public interface IPluginService
{
    ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>Re-reads the plugin roots, reconciles, and returns the new roster.</summary>
    ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default);
    /// <summary>Moves a failed plugin back to disabled, reconciles, and returns the new roster.</summary>
    ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Calls a plugin method; failures are <see cref="PluginCallException"/>s.</summary>
    ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default);
    /// <summary>Yields every publish on a known plugin's channel that reaches this client, until cancelled; turning the plugin off and on does not end it.</summary>
    IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, CancellationToken cancellationToken = default);
    /// <summary>Reads a file of a plugin's directory, contained in it: an external UI assembly, a sibling assembly or an asset. Null when missing.</summary>
    ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default);
}