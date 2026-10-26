using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

/// <summary>
/// The host tools one language server declares, each with the user's ways to provide it: a host-wide path of their own, or the install hint.
/// Shared by the Code Intelligence pane and Settings › Code Intelligence; paths change through host state, and both refresh on its broadcast.
/// </summary>
internal sealed partial class LanguageDependencyList : UserControl
{
    /// <summary>One host tool a server needs, as a row.</summary>
    internal sealed record DependencyRow(LanguageDependencyState Dependency, bool CanChangePaths)
    {
        public bool Missing => Dependency.State != "found";
        public string Text => Dependency.State switch
        {
            "found" => $"{Dependency.Executable}: {Dependency.Path} ({Dependency.Source switch { "override" => "your path", "home" => "from its home variable", _ => "PATH" }})"
                + (Dependency.Required ? "" : " · not needed for the current launch"),
            "rejected" => $"{Dependency.Executable}: your path cannot run. {Dependency.Problem}",
            _ => NeedsText(Dependency) + (Dependency.Required ? "" : " · not needed for the current launch") + (Dependency.Problem.Length > 0 ? " " + Dependency.Problem : "")
        };
        /// <summary>Whether the state line shows; a missing required tool's line is left to the hosting row's own status when it shows one.</summary>
        public bool ShowText => !(OmitMissing && Missing && Dependency.Required);
        public bool OmitMissing { get; init; }
        public string Hint => Dependency.InstallHint;
        public Avalonia.Media.IBrush Brush => Missing && Dependency.Required ? Ui.Danger : Ui.Muted;
        public bool CanChange => CanChangePaths;
        public bool CanClear => CanChangePaths && Dependency.Override;
    }

    /// <summary>Whether a server's detail already opens with the short Needs line, so the row shows the message once.</summary>
    internal static bool RepeatsNeeds(string? detail, string needs) => needs.Length > 0 && (detail ?? "").StartsWith(needs, StringComparison.Ordinal);

    /// <summary>The one wording of a missing tool: <c>Needs pwsh (PowerShell 7)</c>.</summary>
    internal static string NeedsText(LanguageDependencyState dependency) => $"Needs {dependency.Executable} ({dependency.Name})";

    /// <summary>Whether any tool the next managed start needs cannot be found.</summary>
    internal static bool HasMissing(IReadOnlyList<LanguageDependencyState>? dependencies) => (dependencies ?? []).Any(item => item.Required && item.State != "found");

    public static readonly StyledProperty<IReadOnlyList<LanguageDependencyState>?> DependenciesProperty =
        AvaloniaProperty.Register<LanguageDependencyList, IReadOnlyList<LanguageDependencyState>?>(nameof(Dependencies));

    /// <summary>The host state that owns tool paths; inherited, so a pane sets it once for every row it templates. Null hides Use path and Clear path.</summary>
    public static readonly StyledProperty<SharpRail.UI.State.SharedState?> HostStateProperty =
        AvaloniaProperty.Register<LanguageDependencyList, SharpRail.UI.State.SharedState?>(nameof(HostState), inherits: true);

    /// <summary>Raised with a message for the hosting surface's status line.</summary>
    public static readonly RoutedEvent<StatusEventArgs> StatusEvent =
        RoutedEvent.Register<LanguageDependencyList, StatusEventArgs>(nameof(Status), RoutingStrategies.Bubble);

    internal sealed class StatusEventArgs(string text) : RoutedEventArgs(StatusEvent) { public string Text { get; } = text; }

    public IReadOnlyList<LanguageDependencyState>? Dependencies { get => GetValue(DependenciesProperty); set => SetValue(DependenciesProperty, value); }
    public SharpRail.UI.State.SharedState? HostState { get => GetValue(HostStateProperty); set => SetValue(HostStateProperty, value); }
    public event EventHandler<StatusEventArgs>? Status { add => AddHandler(StatusEvent, value); remove => RemoveHandler(StatusEvent, value); }

    /// <summary>Leaves out missing required tools' state lines, for a row that already shows them as its status.</summary>
    public static readonly StyledProperty<bool> OmitMissingTextProperty =
        AvaloniaProperty.Register<LanguageDependencyList, bool>(nameof(OmitMissingText));
    public bool OmitMissingText { get => GetValue(OmitMissingTextProperty); set => SetValue(OmitMissingTextProperty, value); }

    public static void SetHostState(AvaloniaObject target, SharpRail.UI.State.SharedState? value) => target.SetValue(HostStateProperty, value);

    public LanguageDependencyList()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DependenciesProperty && change.Property != HostStateProperty && change.Property != OmitMissingTextProperty) return;
        var rows = this.FindControl<ItemsControl>("DependencyRows")!;
        var next = (Dependencies ?? []).Select(item => new DependencyRow(item, HostState is not null) { OmitMissing = OmitMissingText }).ToArray();
        if (!(rows.ItemsSource?.Cast<DependencyRow>() ?? []).SequenceEqual(next)) rows.ItemsSource = next;
    }

    private void Report(string text) => RaiseEvent(new StatusEventArgs(text));

    private async void UseDependencyPathClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row } || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new() { Title = $"Choose {row.Dependency.Executable} ({row.Dependency.Name})", AllowMultiple = false });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Choose a local file.");
            await SetToolAsync(row.Dependency.Executable, path);
        }
        catch (Exception error) { Report("Couldn't use that path: " + error.Message); }
    }

    private async void ClearDependencyPathClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row }) return;
        try { await SetToolAsync(row.Dependency.Executable, ""); }
        catch (Exception error) { Report("Couldn't clear the path: " + error.Message); }
    }

    /// <summary>Changes the host-wide tool path through the host; every surface's rows update when its broadcast arrives.</summary>
    internal async Task SetToolAsync(string executable, string path)
    {
        if (HostState is not { } state) return;
        await Task.Run(async () => await state.ChangeAsync(HostStateChange.Setting("language-tool:" + executable, path)));
        Report(path.Length > 0 ? $"Using {path} for {executable}. Start / Retry to use it." : $"{executable} is found on PATH again.");
    }

    private async void CopyDependencyHintClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DependencyRow row }) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("No clipboard.");
            await clipboard.SetTextAsync(row.Hint);
            Report("Install hint copied.");
        }
        catch (Exception error) { Report("Couldn't copy the hint: " + error.Message); }
    }
}