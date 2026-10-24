using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SharpRail.UI.Panels;

/// <summary>One listed entry: its name, the path the caller lists or returns for it, and whether it is a folder.</summary>
public sealed record PathBrowserEntry(string Name, string Path, bool IsDirectory, bool Muted = false);

/// <summary>One listed folder: its path, its shown label, its parent (null at the top) and the home folder, if any.</summary>
public sealed record PathBrowserFolder(string Path, string Label, string? Parent, string? Home, IReadOnlyList<PathBrowserEntry> Entries);

/// <summary>
/// Browses folders one at a time and closes with the chosen file's path. The caller lists a folder off the UI
/// thread; opening another folder or closing cancels the pending listing, and a failed listing shows inline
/// with Up and Home still usable. Home appears only when the listing names one.
/// </summary>
public sealed class PathBrowserDialog
{
    private readonly Func<string, CancellationToken, Task<PathBrowserFolder>> list;
    private readonly StackPanel entries;
    private readonly TextBlock location;
    private readonly TextBlock status;
    private readonly Button up;
    private readonly Button home;
    private CancellationTokenSource? pending;
    private PathBrowserFolder? shown;
    private string? homePath;

    public Window Window { get; }

    public PathBrowserDialog(string title, string start, Func<string, CancellationToken, Task<PathBrowserFolder>> list)
    {
        this.list = list;
        Window = Dialogs.Create(title, 520);
        Window.Tag = "PathBrowserDialog";
        var fields = Window.FindControl<StackPanel>("DialogFields")!;
        up = Ui.Button("Up", () => { if (shown?.Parent is { } parent) Open(parent); });
        up.Name = "PathBrowserUp";
        home = Ui.Button("Home", () => { if (homePath is { } path) Open(path); });
        home.Name = "PathBrowserHome"; home.IsVisible = false;
        location = Ui.Text("", Ui.Hint, 12);
        location.Name = "PathBrowserLocation"; location.VerticalAlignment = VerticalAlignment.Center;
        location.TextTrimming = Avalonia.Media.TextTrimming.LeadingCharacterEllipsis;
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(up, Dock.Left); DockPanel.SetDock(home, Dock.Left);
        up.Margin = home.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(up); header.Children.Add(home); header.Children.Add(location);
        fields.Children.Add(header);
        status = Ui.Text("", Ui.Hint, 12);
        status.Name = "PathBrowserStatus"; status.IsVisible = false; status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        fields.Children.Add(status);
        entries = new StackPanel { Name = "PathBrowserEntries", Spacing = 2 };
        fields.Children.Add(new ScrollViewer { Content = entries, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Window.FindControl<StackPanel>("DialogActions")!.Children.Add(Ui.Button("Cancel", () => Window.Close(null)));
        Window.Opened += (_, _) => Open(start);
        Window.Closed += (_, _) => pending?.Cancel();
    }

    public Task<string?> ShowAsync(Window owner) => Window.ShowDialog<string?>(owner);

    private async void Open(string path)
    {
        pending?.Cancel();
        var cancellation = pending = new CancellationTokenSource();
        entries.Children.Clear();
        status.Text = "Loading…"; status.IsVisible = true;
        status.Foreground = Ui.Hint;
        PathBrowserFolder folder;
        try { folder = await list(path, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested) return;
            // The failed folder stays the location, so Up and Home lead out of it.
            shown = new(path, path, shown?.Path == path ? shown.Parent : ParentOf(path), homePath, []);
            location.Text = path; up.IsEnabled = shown.Parent is not null;
            status.Text = "Couldn't open this folder: " + error.Message; status.Foreground = Ui.Danger;
            return;
        }
        if (cancellation.IsCancellationRequested) return;
        shown = folder;
        homePath = folder.Home ?? homePath;
        home.IsVisible = homePath is not null;
        up.IsEnabled = folder.Parent is not null;
        location.Text = folder.Label;
        status.IsVisible = folder.Entries.Count == 0;
        status.Text = "This folder is empty";
        foreach (var entry in folder.Entries)
        {
            var button = new Button
            {
                Name = entry.IsDirectory ? "PathBrowserFolder" : "PathBrowserFile",
                Tag = entry.Path,
                Content = Ui.Row(entry.IsDirectory ? "folderFill" : "fileText", entry.Name, entry.Muted ? Ui.Muted : Ui.TextBrush),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new(0),
                Padding = new Thickness(8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(button, entry.Path);
            button.Click += (_, _) =>
            {
                if (entry.IsDirectory) Open(entry.Path);
                else Window.Close(entry.Path);
            };
            entries.Children.Add(button);
        }
    }

    // The parent of a failed absolute host path, or of a workspace-relative one ("" is the workspace root).
    private static string? ParentOf(string path)
    {
        if (path.Length == 0) return null;
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return null;
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? "" : cut == 0 ? trimmed[..1] : trimmed[..cut];
    }
}