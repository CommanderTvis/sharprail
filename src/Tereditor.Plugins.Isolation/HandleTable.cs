namespace SharpRail.Plugins.Isolation;

/// <summary>What a handle id names on one connection.</summary>
public enum HandleState { Never, Live, Released, Revoked }

/// <summary>
/// The objects one side of a connection lent the other, by id. Ids count up from 1 and are never reused, so an id at or
/// below the last minted one that is no longer live is a tombstone: released, or revoked with a permission. Only an id
/// never minted on this connection is a protocol violation. Releasing the last reference disposes the object.
/// </summary>
public sealed class HandleTable
{
    private readonly Lock gate = new();
    private readonly Dictionary<long, (object Value, int References)> live = [];
    private readonly Dictionary<long, string> revoked = [];
    private long minted;
    private bool closed;

    /// <summary>Lends <paramref name="value"/> and returns its new id with one reference.</summary>
    public long Mint(object value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            live[++minted] = (value, 1);
            return minted;
        }
    }

    /// <summary>The object behind <paramref name="id"/> when it is live; otherwise why not.</summary>
    public HandleState Resolve(long id, out object? value, out string? permission)
    {
        lock (gate)
        {
            value = null;
            permission = null;
            if (live.TryGetValue(id, out var entry)) { value = entry.Value; return HandleState.Live; }
            if (revoked.TryGetValue(id, out permission)) return HandleState.Revoked;
            return id > 0 && id <= minted ? HandleState.Released : HandleState.Never;
        }
    }

    /// <summary>Adds a reference to a live handle; false when it is not live.</summary>
    public bool AddReference(long id)
    {
        lock (gate)
        {
            if (!live.TryGetValue(id, out var entry)) return false;
            live[id] = (entry.Value, entry.References + 1);
            return true;
        }
    }

    /// <summary>Drops one reference. A tombstoned id is left as it is; the caller treats <see cref="HandleState.Never"/> as a violation.</summary>
    public HandleState Release(long id)
    {
        object? disposed = null;
        HandleState state;
        lock (gate)
        {
            if (live.TryGetValue(id, out var entry))
            {
                if (entry.References > 1) live[id] = (entry.Value, entry.References - 1);
                else { live.Remove(id); disposed = entry.Value; }
                state = HandleState.Live;
            }
            else state = revoked.ContainsKey(id) ? HandleState.Revoked : id > 0 && id <= minted ? HandleState.Released : HandleState.Never;
        }
        Dispose(disposed);
        return state;
    }

    /// <summary>Withdraws a live handle whatever its references: later calls on it fail as denied by <paramref name="permission"/>.</summary>
    public bool Revoke(long id, string permission)
    {
        object? disposed;
        lock (gate)
        {
            if (!live.Remove(id, out var entry)) return false;
            revoked[id] = permission;
            disposed = entry.Value;
        }
        Dispose(disposed);
        return true;
    }

    /// <summary>Disposes every live object when the connection ends; minting after this throws.</summary>
    public void Close()
    {
        List<object> values;
        lock (gate)
        {
            closed = true;
            values = [.. live.Values.Select(entry => entry.Value)];
            live.Clear();
        }
        foreach (var value in values) Dispose(value);
    }

    private static void Dispose(object? value)
    {
        if (value is IDisposable disposable) disposable.Dispose();
        else if (value is IAsyncDisposable asyncDisposable) _ = asyncDisposable.DisposeAsync().AsTask();
    }
}