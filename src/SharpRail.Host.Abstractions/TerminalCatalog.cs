using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Host.Abstractions;

/// <summary>A terminal tab every client of the host agrees exists; where a client shows it is that client's business.</summary>
public sealed record TerminalTab(string Key, string Title)
{
    /// <summary>The terminal a workspace is given once, when the host first learns of it.</summary>
    public const string InitialKey = "terminal:initial";
    public const string InitialTitle = "Terminal 1";
    public const int MaxPerWorkspace = 256;
    public const int MaxKeyLength = 500;
    public const int MaxTitleLength = 1000;

    /// <summary>The host session behind a tab: stable across clients, windows and host restarts.</summary>
    public static string SessionFor(string workspaceRoot, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot + "\n" + key)).AsSpan(0, 16));
}

/// <summary>
/// Every known workspace's terminal tabs, in creation order. A workspace the host has not been told about
/// is absent; one whose terminals were all closed is present and empty. Snapshots are complete, so a client
/// that missed one rehydrates from the next.
/// </summary>
public sealed record TerminalCatalog(long Revision, IReadOnlyDictionary<string, IReadOnlyList<TerminalTab>> Workspaces)
{
    public static TerminalCatalog Empty { get; } = new(0, new Dictionary<string, IReadOnlyList<TerminalTab>>());
}

/// <summary>
/// Which terminals exist, separately from whether a shell runs behind them: reserving a tab starts nothing.
/// Every change returns the snapshot it produced and reaches every watcher.
/// </summary>
public interface ITerminalCatalogService
{
    /// <summary>
    /// Tells the host a client shows this workspace. A workspace it did not know receives the client's
    /// tabs, or the initial terminal when the client has none. A known one only gains tabs it lacks, never
    /// the initial terminal again, so closing that one is final.
    /// </summary>
    ValueTask<TerminalCatalog> OpenWorkspaceAsync(string workspaceRoot, IReadOnlyList<TerminalTab> tabs, CancellationToken cancellationToken = default);
    /// <summary>Records a client-minted tab; reserving a tab that exists changes nothing, its title included.</summary>
    ValueTask<TerminalCatalog> ReserveAsync(string workspaceRoot, TerminalTab tab, CancellationToken cancellationToken = default);
    /// <summary>Removes the tab for every client and ends its shell.</summary>
    ValueTask<TerminalCatalog> CloseTabAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default);
    /// <summary>Ends every terminal rooted in a removed workspace and forgets the workspace.</summary>
    ValueTask<TerminalCatalog> CloseWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    /// <summary>Yields the current snapshot, then every later one, until cancelled or disconnected.</summary>
    IAsyncEnumerable<TerminalCatalog> WatchAsync(CancellationToken cancellationToken = default);
}