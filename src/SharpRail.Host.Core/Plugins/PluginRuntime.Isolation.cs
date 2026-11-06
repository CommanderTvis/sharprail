using SharpRail.Host.Core.Plugins.Isolation;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Isolation;

namespace SharpRail.Host.Core.Plugins;

// External plugins in the isolated runtime (Security.SPEC.md): one runner process per activation, owned by its
// activation tables from spawn, failed by the next pass when it exits on its own.
public sealed partial class PluginRuntime : IIsolatedUIHost
{
    // Why an enabled external plugin does not run here, or null when the isolation seam runs it.
    private string? ExternalRefusal(PluginEntry entry)
    {
        if (Seams.Isolation is not { } isolation) return ExternalUnsupported;
        if (isolation.Refusal(entry.Manifest) is { } refusal) return refusal;
        return Seams.StateDirectory is null || entry.Directory is null ? "This host keeps no plugin files." : null;
    }

    private async Task ActivateIsolatedAsync(PluginEntry entry)
    {
        if (ExternalRefusal(entry) is { } refusal)
        {
            lock (gate) (entry.State, entry.Reason, entry.PassRefused) = (PluginStatus.Refused, refusal, true);
            return;
        }
        var tables = new ActivationTables(registry.NextActivationId());
        lock (gate) entry.Activation = tables;
        IsolatedPlugin runner;
        try
        {
            runner = await IsolatedPlugin.StartAsync(Seams.Isolation!, entry.Manifest, entry.Directory!, Seams.StateDirectory!,
                line => Seams.Log(entry.Id, "info", line), lifetime.Token);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ReferenceEquals(entry.Activation, tables)) entry.Activation = null;
                (entry.State, entry.Reason) = (PluginStatus.Failed, error.Message);
            }
            return;
        }
        bool current;
        lock (gate)
        {
            current = ReferenceEquals(entry.Activation, tables);
            if (current)
            {
                tables.Runner = runner;
                (entry.State, entry.Reason) = (PluginStatus.Active, null);
            }
        }
        if (!current)
        {
            await runner.DisposeAsync();
            return;
        }
        _ = runner.Exited.ContinueWith(exited =>
        {
            lock (gate)
            {
                if (!ReferenceEquals(entry.Activation, tables) || runner.Requested) return;
                tables.Exit = exited.Result is { } code ? $"The plugin's process exited with code {code}." : "The plugin's process ended.";
            }
            _ = Schedule();
        }, TaskScheduler.Default);
    }

    // A runner that exited on its own fails its plugin: the activation stops routing and is released, and the state
    // stays failed with the reason until the user retries, which starts a new process.
    private async Task FailExitedAsync()
    {
        List<(PluginEntry Entry, ActivationTables Tables)> exited = [];
        lock (gate)
            foreach (var entry in registry.All)
                if (entry.Activation is { Exit: { } reason } tables)
                {
                    entry.Activation = null;
                    (entry.State, entry.Reason) = (PluginStatus.Failed, reason);
                    exited.Add((entry, tables));
                }
        foreach (var (entry, tables) in exited)
        {
            Log(entry.Id, "error", tables.Exit!, null);
            await ReleaseAsync(entry.Id, tables, dispose: false);
        }
        if (exited.Count > 0) Publish();
    }

    public async ValueTask<IIsolatedUISession> OpenAsync(string pluginId, string clientKey, CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        IsolatedPlugin? runner;
        lock (gate) runner = registry.Get(pluginId) is { State: PluginStatus.Active, Activation.Runner: { } running } ? running : null;
        if (runner is null) throw new InvalidOperationException($"Plugin {pluginId} is not running in its own process.");
        var stored = Seams.State.Current.PluginSettings.TryGetValue(pluginId, out var space) ? space : (System.Text.Json.JsonElement?)null;
        return await runner.OpenAsync(IsolatedPlugin.Settings(stored), cancellationToken);
    }

    /// <summary>The process id of an external plugin's runner, while it has one.</summary>
    internal int? RunnerPid(string pluginId)
    {
        lock (gate) return registry.Get(pluginId)?.Activation?.Runner?.Pid;
    }
}