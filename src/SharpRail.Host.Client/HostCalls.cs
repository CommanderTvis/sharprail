using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public enum HostConnectionStatus { Connecting, Connected, Disconnected }

/// <summary>
/// One client's connection to its host, shared by the adapters of that client: its identity, which spans
/// reconnects so the host recognises a mutation sent again, and whether the host is currently reachable.
/// </summary>
public sealed class HostConnection
{
    private readonly Lock gate = new();
    private readonly SortedSet<long> unresolved = [];
    private long lastRequest;
    private int? hostVersion;
    private HostConnectionStatus status;
    private int generation;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    public HostConnectionStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>
    /// Counts connections: 1 for the first, one more per reconnect. Whatever a client read under an earlier
    /// generation may have changed unseen, so a new one is the trigger to read it again.
    /// </summary>
    public int Generation
    {
        get { lock (gate) return generation; }
    }

    /// <summary>Raised after <see cref="Status"/> changes, on the reporting thread.</summary>
    public event Action? Changed;

    /// <summary>Called by the owner of the host subscription when it starts or stops delivering.</summary>
    public void Report(bool connected)
    {
        lock (gate)
        {
            var next = connected ? HostConnectionStatus.Connected : HostConnectionStatus.Disconnected;
            if (status == next) return;
            status = next;
            if (connected) generation++;
        }
        Changed?.Invoke();
    }

    /// <summary>The host's protocol version from its latest handshake; null until one answers. It decides what the host can serve.</summary>
    public int? HostVersion
    {
        get { lock (gate) return hostVersion; }
        set { lock (gate) hostVersion = value; }
    }

    internal long Begin()
    {
        lock (gate) { unresolved.Add(++lastRequest); return lastRequest; }
    }

    internal void End(long request)
    {
        lock (gate) unresolved.Remove(request);
    }

    /// <summary>Every request still awaiting its reply, which lets the host drop the results of all others.</summary>
    internal string Unresolved()
    {
        lock (gate) return string.Join(',', unresolved);
    }
}

/// <summary>What the caller of a host operation may decide about how it travels.</summary>
public static class HostRequest
{
    private static readonly AsyncLocal<TimeSpan?> timeout = new();

    internal static TimeSpan? Timeout => timeout.Value;

    /// <summary>
    /// Until disposed, remote project and state calls started by this flow wait this long for their reply
    /// instead of the adapter's deadline. For a call the host answers only after a person acts; an
    /// embedded host has no deadline to replace.
    /// </summary>
    public static IDisposable WithTimeout(TimeSpan value)
    {
        var scope = new Scope(timeout.Value);
        timeout.Value = value;
        return scope;
    }

    private sealed class Scope(TimeSpan? previous) : IDisposable
    {
        public void Dispose() => timeout.Value = previous;
    }
}