namespace SharpRail.Plugins.Tailscale;

/// <summary>A client-local tailnet transport, independent of the host's installed Tailscale daemon.</summary>
public interface ITailscaleClientNetwork
{
    /// <summary>Creates an owned HTTP handler that reaches peers through this network without redirects.</summary>
    HttpMessageHandler CreateHandler();

    /// <summary>Resolves an eligible peer to its numeric bootstrap address without relying on system DNS.</summary>
    Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken);

    /// <summary>Lists online peers eligible for SharpRail bootstrap probing.</summary>
    Task<IReadOnlyList<TailscalePeer>> PeersAsync(CancellationToken cancellationToken);
}