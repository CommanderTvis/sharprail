using System.Text.Json;

using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.State;

/// <summary>
/// The app's subscription to its host's shared state. Every window of the app reads it; changes
/// round-trip through the host and apply only when its snapshot arrives, so all clients converge.
/// A dropped subscription resubscribes and rehydrates from the host's current snapshot.
/// </summary>
public sealed class SharedState : IDisposable
{
    private readonly IHostStateService service;
    private readonly CancellationTokenSource lifetime = new();
    private bool started;

    public SharedState(IHostStateService service, Preferences preferences, HostState? initial = null, HostConnection? connection = null)
    {
        this.service = service; Preferences = preferences; Connection = connection ?? new();
        if (initial is not null) { Apply(initial); Connection.Report(true); }
        else Current = new();
    }

    public HostState Current { get; private set; } = new();
    /// <summary>The app's preferences; shared fields mirror the latest snapshot.</summary>
    public Preferences Preferences { get; }
    /// <summary>Whether a subscription currently delivers snapshots; a host given an initial snapshot starts connected.</summary>
    public bool Connected => Connection.Status == HostConnectionStatus.Connected;
    /// <summary>The connection this subscription reports to; its generation rises with every reconnect.</summary>
    public HostConnection Connection { get; }
    public int Generation => Connection.Generation;
    /// <summary>Raised on the UI thread with the previous and the new snapshot.</summary>
    public event Action<HostState, HostState>? Changed;
    /// <summary>Raised on the UI thread when the subscription drops or is re-established.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>The connected host's handshake; null until it answers and again after the connection drops.</summary>
    public HostHandshake? Handshake { get; private set; }
    /// <summary>Whether the connected host serves a feature introduced at that protocol version; false until its handshake answers.</summary>
    public bool Supports(int introducedAt) => HostCapabilities.Supports(Handshake?.ProtocolVersion, introducedAt);
    /// <summary>Raised on the UI thread when <see cref="Handshake"/> is set or cleared.</summary>
    public event Action<HostHandshake?>? HandshakeChanged;
    private int handshakeGeneration;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public void Start()
    {
        if (started) return;
        started = true;
        if (Connected) _ = FetchHandshakeAsync();
        Dispatcher.UIThread.Post(() => _ = WatchAsync());
    }

    public Task ChangeAsync(params HostStateChange[] changes) => service.ChangeAsync(changes, lifetime.Token).AsTask();

    public string Label(string path) => Current.WorkspaceLabels.GetValueOrDefault(path) ?? new DirectoryInfo(path).Name;

    private async Task WatchAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var state in service.WatchAsync(lifetime.Token))
                {
                    if (!Connected) { Connection.Report(true); ConnectionChanged?.Invoke(true); _ = FetchHandshakeAsync(); }
                    var previous = Current;
                    Apply(state);
                    Changed?.Invoke(previous, state);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) { Console.Error.WriteLine("Host state subscription dropped: " + error.Message); }
            if (Connected) { Connection.Report(false); SetHandshake(null); ConnectionChanged?.Invoke(false); }
            try { await Task.Delay(RetryDelay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetHandshake(HostHandshake? value)
    {
        handshakeGeneration++;
        if (Handshake == value) return;
        Handshake = value;
        HandshakeChanged?.Invoke(value);
    }

    /// <summary>Asks the host in the background; an answer from a superseded connection is dropped.</summary>
    private async Task FetchHandshakeAsync()
    {
        var generation = ++handshakeGeneration;
        try
        {
            var handshake = await Task.Run(async () => await service.GetHandshakeAsync(lifetime.Token), lifetime.Token);
            if (generation == handshakeGeneration && Connected) { Handshake = handshake; HandshakeChanged?.Invoke(handshake); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Console.Error.WriteLine("Host handshake failed: " + error.Message); }
    }

    private void Apply(HostState state)
    {
        Current = state;
        var settings = state.Settings;
        Preferences.Theme = settings.Theme.Length > 0 ? settings.Theme : Themes.DefaultId;
        Preferences.ThemeMode = settings.ThemeMode;
        Preferences.SystemThemePair = settings.SystemLight.Length > 0 && settings.SystemDark.Length > 0
            ? new() { Light = settings.SystemLight, Dark = settings.SystemDark } : null;
        ProfileStore.NormalizeTheme(Preferences);
        Preferences.FileLineWidth = LineWidths.IsValid(settings.FileLineWidth) ? settings.FileLineWidth : LineWidths.FileDefault;
        Preferences.FileLineWidthBounded = settings.FileLineWidthBounded;
        Preferences.MarkdownLineWidth = LineWidths.IsValid(settings.MarkdownLineWidth) ? settings.MarkdownLineWidth : LineWidths.MarkdownDefault;
        Preferences.MarkdownLineWidthBounded = settings.MarkdownLineWidthBounded;
        var presets = new Dictionary<string, DockState>();
        foreach (var preset in state.Presets)
        {
            try
            {
                if (JsonSerializer.Deserialize<DockState>(preset.Layout) is { } layout && LayoutSession.IsValid(layout)) presets[preset.Name] = layout;
            }
            catch (JsonException) { }
        }
        Preferences.CustomPresets = presets;
    }

    public static string Serialize(DockState layout) => JsonSerializer.Serialize(layout);

    public void Dispose() => lifetime.Cancel();
}