using System.Text.Json;

using Avalonia.Threading;

using SharpRail.Host.Abstractions;
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

    public SharedState(IHostStateService service, Preferences preferences, HostState? initial = null)
    {
        this.service = service; Preferences = preferences;
        if (initial is not null) { Apply(initial); Connected = true; }
        else Current = new();
    }

    public HostState Current { get; private set; } = new();
    /// <summary>The app's preferences; shared fields mirror the latest snapshot.</summary>
    public Preferences Preferences { get; }
    /// <summary>Whether a subscription currently delivers snapshots; a host given an initial snapshot starts connected.</summary>
    public bool Connected { get; private set; }
    /// <summary>Raised on the UI thread with the previous and the new snapshot.</summary>
    public event Action<HostState, HostState>? Changed;
    /// <summary>Raised on the UI thread when the subscription drops or is re-established.</summary>
    public event Action<bool>? ConnectionChanged;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public void Start()
    {
        if (started) return;
        started = true;
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
                    if (!Connected) { Connected = true; ConnectionChanged?.Invoke(true); }
                    var previous = Current;
                    Apply(state);
                    Changed?.Invoke(previous, state);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) { Console.Error.WriteLine("Host state subscription dropped: " + error.Message); }
            if (Connected) { Connected = false; ConnectionChanged?.Invoke(false); }
            try { await Task.Delay(RetryDelay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
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
                if (JsonSerializer.Deserialize<DockState>(preset.Layout) is not { } layout) continue;
                layout.MigrateLegacyTools();
                if (LayoutSession.IsValid(layout)) presets[preset.Name] = layout;
            }
            catch (JsonException) { }
        }
        Preferences.CustomPresets = presets;
    }

    public static string Serialize(DockState layout) => JsonSerializer.Serialize(layout);

    public void Dispose() => lifetime.Cancel();
}