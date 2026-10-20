namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>What opening the model menu does with the host's cached picker list.</summary>
internal enum ModelListPlan
{
    /// <summary>Show the cached list; nothing reads the picker.</summary>
    Serve,
    /// <summary>Show the cached list now and read the picker once in the background.</summary>
    ServeAndRefresh,
    /// <summary>Nothing cached: read the picker, with progress.</summary>
    Read
}

/// <summary>
/// The model list the host cached, shared by every Claude terminal: concurrent menu opens share one host lookup, and at
/// most one terminal at a time walks the live picker to refresh it.
/// </summary>
internal sealed class ModelListSource(Func<CancellationToken, Task<ModelListResult>> load, Func<IReadOnlyList<string>, Task> save)
{
    private readonly Lock gate = new();
    private Task<ModelListResult>? lookup;
    private bool reading;

    public static ModelListPlan Plan(ModelListResult cached) =>
        cached.Models.Count == 0 ? ModelListPlan.Read : cached.Stale ? ModelListPlan.ServeAndRefresh : ModelListPlan.Serve;

    /// <summary>The host's cached list; opens that overlap share one request. A failed lookup reads as no cache.</summary>
    public async Task<ModelListResult> CachedAsync()
    {
        Task<ModelListResult> shared;
        lock (gate) shared = lookup ??= Lookup();
        return await shared;
    }

    private async Task<ModelListResult> Lookup()
    {
        try { return await Task.Run(() => load(CancellationToken.None)); }
        catch (Exception error) when (error is not OperationCanceledException) { return new([]); }
        finally { lock (gate) lookup = null; }
    }

    /// <summary>Claims the one refresh slot; null while another terminal is reading. Dispose the result to release it.</summary>
    public IDisposable? TryBeginRead()
    {
        lock (gate)
        {
            if (reading) return null;
            reading = true;
        }
        return new Release(() => { lock (gate) reading = false; });
    }

    /// <summary>Hands a list just read to the host. A failure keeps the host's last good list.</summary>
    public async Task StoreAsync(IReadOnlyList<string> models)
    {
        if (models.Count == 0) return;
        try { await Task.Run(() => save(models)); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}