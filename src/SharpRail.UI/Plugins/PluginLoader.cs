using System.Text.Json;

using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The app's plugin runtime: turns the host's roster into activated (or dormant) UI halves. The roster on each
/// shared-state snapshot is desired state and the activated set is actual state; one serialized reconciler converges
/// them, so a toggle, a rescan and a reconnect are the same path. A broken plugin stays dormant and logged.
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly Workbench workbench;
    private readonly IReadOnlyList<(PluginManifest Manifest, Func<PluginUIModule> Load)> builtins;
    private readonly Dictionary<string, PluginActivation> activations = [];
    private Task queue = Task.CompletedTask;
    private string rosterSignature = "";
    private bool started, stopped;

    public PluginLoader(Workbench workbench, IReadOnlyList<(PluginManifest, Func<PluginUIModule>)>? builtins = null)
    {
        this.workbench = workbench;
        this.builtins = builtins ?? BuiltinPlugins.All;
    }

    public PluginRegistry Registry { get; } = new();
    public EditorEvents Editors { get; } = new();
    internal IPluginService? Service => workbench.Plugins;
    internal Workbench Workbench => workbench;
    /// <summary>One id per app run and host, carried on every call and subscription so an addressed publish reaches this app.</summary>
    public string ClientKey { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Completes when every reconcile scheduled so far has run.</summary>
    public Task Idle => queue;
    internal event Action? Reconnected;
    internal event Action? Connected;

    public void Start()
    {
        if (started) return;
        started = true;
        foreach (var (manifest, _) in builtins) Registry.RegisterManifest(manifest);
        PluginIcons.ReadAsset = async (id, path, token) => Service is { } service ? await service.ReadFileAsync(id, path, token) : null;
        workbench.State.Changed += SharedChanged;
        workbench.State.ConnectionChanged += ConnectionChanged;
        ApplyRoster(workbench.State.Current.Plugins, force: true);
    }

    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        workbench.State.Changed -= SharedChanged;
        workbench.State.ConnectionChanged -= ConnectionChanged;
        foreach (var id in activations.Keys.ToArray()) Unmount(id);
    }

    public void Dispose() => Stop();

    private void SharedChanged(HostState previous, HostState next) => ApplyRoster(next.Plugins, force: false);

    private void ConnectionChanged(bool connected)
    {
        if (!connected) return;
        Connected?.Invoke();
        Reconnected?.Invoke();
        ApplyRoster(workbench.State.Current.Plugins, force: true);
    }

    private void ApplyRoster(IReadOnlyList<PluginRosterEntry> roster, bool force)
    {
        var signature = JsonSerializer.Serialize(roster, PluginJson.Options);
        if (!force && signature == rosterSignature) return;
        rosterSignature = signature;
        Registry.SetRoster(roster);
        Schedule();
    }

    /// <summary>Queues one convergence on the latest roster; a run reads the roster when it starts, so rapid changes coalesce.</summary>
    internal void Schedule() => queue = Run(queue);

    private async Task Run(Task previous)
    {
        try { await previous; }
        catch (Exception error) { Console.Error.WriteLine("Plugin reconcile failed: " + error); }
        if (stopped) return;
        await Dispatcher.UIThread.InvokeAsync(ReconcileAsync);
    }

    private async Task ReconcileAsync()
    {
        var roster = Registry.Roster;
        foreach (var id in activations.Keys.ToArray())
            if (roster.FirstOrDefault(entry => entry.Id == id) is not { Status: PluginStatus.Active } entry || activations[id].Entry.Ui != entry.Ui)
                Unmount(id);
        foreach (var entry in roster.Where(entry => entry.Status == PluginStatus.Active && !activations.ContainsKey(entry.Id)))
        {
            if (stopped) return;
            await MountAsync(entry);
        }
    }

    private async Task MountAsync(PluginRosterEntry entry)
    {
        try
        {
            PluginUIModule? module = null;
            var builtin = builtins.FirstOrDefault(candidate => candidate.Manifest.Id == entry.Id);
            if (builtin.Manifest is not null)
            {
                if (builtin.Manifest.WireVersion != entry.WireVersion)
                {
                    Console.Error.WriteLine($"Plugin {entry.Id} stays dormant: the host speaks wire version {entry.WireVersion}, this app's UI half {builtin.Manifest.WireVersion}.");
                    return;
                }
                module = builtin.Load();
            }
            else if (entry.Ui is not null)
            {
                if (Service is not { } service) return;
                var type = await Task.Run(() => ExternalPlugins.LoadAsync(service, entry));
                module = (PluginUIModule)Activator.CreateInstance(type)!;
            }
            var current = Registry.Entry(entry.Id);
            if (stopped || current is not { Status: PluginStatus.Active }) return;
            var activation = new PluginActivation(current);
            activations[entry.Id] = activation;
            if (module is not null)
            {
                var context = new PluginUIContext(this, current, activation);
                activation.Open = true;
                try { activation.Disposer = module.Activate(context); }
                finally { activation.Open = false; }
            }
            Registry.SetActive(entry.Id, true);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Plugin {entry.Id} failed to activate and stays dormant: {error}");
            if (activations.Remove(entry.Id, out var failed)) failed.Close();
            Registry.RemovePlugin(entry.Id);
        }
    }

    private void Unmount(string id)
    {
        if (!activations.Remove(id, out var activation)) return;
        activation.Unmounting = true;
        try { activation.Disposer?.Invoke(); }
        catch (Exception error) { Console.Error.WriteLine($"Plugin {id} failed to dispose cleanly: {error.Message}"); }
        activation.Close();
        Registry.RemovePlugin(id);
    }
}

/// <summary>
/// One activation of a plugin's UI half: its registration guard, the disposer <c>Activate</c> returned, and the
/// observers it started, all ended together when the plugin unmounts.
/// </summary>
internal sealed class PluginActivation(PluginRosterEntry entry)
{
    private readonly List<IDisposable> observers = [];

    public PluginRosterEntry Entry { get; } = entry;
    /// <summary>Open only while <c>Activate</c> runs.</summary>
    public bool Open { get; set; }
    public bool Unmounting { get; set; }
    public bool Closed { get; private set; }
    public PluginDisposer? Disposer { get; set; }

    public IDisposable Track(IDisposable observer)
    {
        if (Closed) { observer.Dispose(); return observer; }
        observers.Add(observer);
        return new EditorEvents.Subscription(() => { observers.Remove(observer); observer.Dispose(); });
    }

    public void Close()
    {
        Closed = true;
        foreach (var observer in observers.ToArray())
        {
            try { observer.Dispose(); }
            catch (Exception error) { Console.Error.WriteLine("Plugin observer failed to stop: " + error.Message); }
        }
        observers.Clear();
    }
}