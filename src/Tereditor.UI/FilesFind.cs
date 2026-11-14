using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.UI.Panels;

namespace Tereditor.UI;

/// <summary>
/// Go to file in the Files pane: a filter box under the header row and a flat list of the files whose path matches,
/// shown in place of the tree, which stays untouched underneath. All asks the host (<see cref="IProjectServices.FindFilesAsync"/>);
/// Changed ranks the window's Git snapshot in memory by the same rule (<see cref="FileNameMatch"/>).
/// </summary>
public sealed partial class WorkbenchWindow
{
    private const int FilesFindLimit = 200;
    private CancellationTokenSource? filesFindPending;
    private DispatcherTimer? filesFindDebounce;
    // Raised for every query and on close; a reply that belongs to an older one is dropped.
    private long filesFindGeneration;

    /// <summary>Whether the Files pane's filter box is open.</summary>
    internal bool FilesFindOpen => toolContent.GetValueOrDefault("files") is FilesPaneFrame panel && panel.FindControl<TextBox>("FilesFindQuery")!.IsVisible;

    private Button FilesFindButton()
    {
        var button = new Button
        {
            Name = "FilesFind",
            Content = Ui.Icon("search", null, 14),
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("quiet");
        AutomationProperties.SetName(button, "Go to file");
        ToolTip.SetTip(button, "Go to file");
        button.Click += (_, _) =>
        {
            if (FilesFindOpen && toolContent.GetValueOrDefault("files") is FilesPaneFrame panel) CloseFilesFind(panel);
            else OpenFilesFind();
        };
        return button;
    }

    private void WireFilesFind(FilesPaneFrame panel)
    {
        var box = panel.FindControl<TextBox>("FilesFindQuery")!;
        var results = panel.FindControl<ListBox>("FilesFindResults")!;
        var previous = "";
        box.TextChanged += (_, _) =>
        {
            var text = box.Text ?? "";
            var cleared = previous.Length > 0 && text.Length == 0;
            previous = text;
            if (!box.IsVisible) return;
            // Clearing the query closes the box, as Escape does.
            if (cleared) CloseFilesFind(panel);
            else RestartFilesFind();
        };
        // Tunnel, so the box's own handling of Up, Down and Enter never runs first.
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseFilesFind(panel);
                    break;
                case Key.Down or Key.Up when results.ItemCount > 0:
                    results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, results.ItemCount - 1);
                    results.ScrollIntoView(results.SelectedIndex);
                    break;
                case Key.Enter:
                    if (FilesFindSelection(panel) is { } path) { CloseFilesFind(panel); OpenFilesFindMatch(path, true); }
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        results.Tapped += (_, _) => { if (FilesFindSelection(panel) is { } path) OpenFilesFindMatch(path, false); };
        results.DoubleTapped += (_, _) => { if (FilesFindSelection(panel) is { } path) OpenFilesFindMatch(path, true); };
    }

    /// <summary>Mod+P: shows Files and focuses its filter box.</summary>
    private void GoToFile()
    {
        RevealFiles();
        // Revealing may build the Files tool; the box takes focus once it is in the window.
        Dispatcher.UIThread.Post(OpenFilesFind, DispatcherPriority.Loaded);
    }

    internal void OpenFilesFind()
    {
        if (toolContent.GetValueOrDefault("files") is not FilesPaneFrame panel) return;
        var box = panel.FindControl<TextBox>("FilesFindQuery")!;
        box.IsVisible = true;
        box.Focus();
        box.SelectAll();
        if (!string.IsNullOrWhiteSpace(box.Text)) RestartFilesFind();
    }

    /// <summary>Closes the filter box and shows the tree or Changed exactly as it was.</summary>
    private void CloseFilesFind(FilesPaneFrame panel)
    {
        filesFindDebounce?.Stop();
        filesFindPending?.Cancel();
        filesFindGeneration++;
        var box = panel.FindControl<TextBox>("FilesFindQuery")!;
        var hadFocus = box.IsFocused;
        box.IsVisible = false;
        box.Text = "";
        ShowFilesFindResults(panel, null, "");
        if (hadFocus) panel.Children.OfType<DrawnTree>().Single().Focus();
    }

    private void RestartFilesFind()
    {
        if (filesFindDebounce is not { } timer)
        {
            timer = filesFindDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) => { timer.Stop(); _ = RunFilesFindAsync(); };
        }
        timer.Stop();
        timer.Start();
    }

    private async Task RunFilesFindAsync()
    {
        if (toolContent.GetValueOrDefault("files") is not FilesPaneFrame panel) return;
        var box = panel.FindControl<TextBox>("FilesFindQuery")!;
        filesFindPending?.Cancel();
        var cancellation = filesFindPending = new CancellationTokenSource();
        var generation = ++filesFindGeneration;
        var text = (box.Text ?? "").Trim();
        // An empty query shows the tree again; the box stays open for the next one.
        if (!box.IsVisible || text.Length == 0 || !WorkspaceMounted) { ShowFilesFindResults(panel, null, ""); return; }
        var request = projectRequest; var workspace = workspaceRoot;
        SearchHits found;
        if (filesChanged)
        {
            // Changed lists the window's one Git snapshot, already in memory; a deletion has no file to open.
            var ranked = FileNameMatch.Rank(ScopedChanges().Where(change => ChangeMark(change) is not null).Select(change => change.Path).Distinct(), text)
                .Take(FilesFindLimit + 1).Select(path => new SearchHit(path, 0, "")).ToArray();
            found = new(ranked.Take(FilesFindLimit).ToArray(), ranked.Length > FilesFindLimit);
        }
        else
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, lifetime.Token);
                found = await Task.Run(async () => await host.FindFilesAsync(text, linked.Token), linked.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                if (generation != filesFindGeneration || request != projectRequest || workspace != workspaceRoot) return;
                ShowFilesFindResults(panel, new([], false), text, "Search failed: " + error.Message);
                return;
            }
        }
        if (generation != filesFindGeneration || request != projectRequest || workspace != workspaceRoot || !box.IsVisible) return;
        ShowFilesFindResults(panel, found, text);
    }

    /// <summary>Shows the matches in place of the tree, or with null hides them and brings back what the mode shows.</summary>
    private void ShowFilesFindResults(FilesPaneFrame panel, SearchHits? found, string query, string? failure = null)
    {
        var results = panel.FindControl<ListBox>("FilesFindResults")!;
        var status = panel.FindControl<TextBlock>("FilesFindStatus")!;
        results.Items.Clear();
        panel.FindControl<DockPanel>("FilesFindPanel")!.IsVisible = found is not null;
        if (found is not null)
        {
            foreach (var hit in found.Hits) results.Items.Add(FilesFindRow(hit.Path, query));
            results.SelectedIndex = found.Hits.Count > 0 ? 0 : -1;
            status.Text = failure ?? (found.Hits.Count == 0 ? "No matching files" : $"Showing the first {found.Hits.Count} matches");
            status.IsVisible = failure is not null || found.Hits.Count == 0 || found.Truncated;
        }
        ApplyFilesMode(panel);
    }

    private static string? FilesFindSelection(FilesPaneFrame panel) =>
        panel.FindControl<ListBox>("FilesFindResults")!.SelectedItem is Control { Tag: string path } ? path : null;

    private void OpenFilesFindMatch(string path, bool keep)
    {
        if (WorkspaceMounted && toolContent.GetValueOrDefault("files")?.IsHitTestVisible == true) _ = BrowseDocumentAsync(path, keep);
    }

    /// <summary>One match: icon, the file name and then its folder dimmed, the matched characters in the accent colour.</summary>
    private Control FilesFindRow(string path, string query)
    {
        var positions = new List<int>();
        FileNameMatch.Score(path, query, positions);
        var matched = positions.ToHashSet();
        var slash = path.LastIndexOf('/');
        var row = new DockPanel { Name = "FilesFindMatch", Tag = path };
        if (FileIcon(path, false, "fileText", null) is { } icon)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
        }
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.TextBrush };
        AddFilesFindRuns(name.Inlines!, path, slash + 1, path.Length, matched);
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        if (slash > 0)
        {
            var folder = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.Hint,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AddFilesFindRuns(folder.Inlines!, path, 0, slash, matched);
            row.Children.Add(folder);
        }
        AutomationProperties.SetName(row, path);
        ToolTip.SetTip(row, path);
        return row;
    }

    private static void AddFilesFindRuns(InlineCollection inlines, string path, int start, int end, HashSet<int> matched)
    {
        for (var at = start; at < end;)
        {
            var hit = matched.Contains(at);
            var stop = at + 1;
            while (stop < end && matched.Contains(stop) == hit) stop++;
            var run = new Run(path[at..stop]);
            if (hit) { run.Foreground = Ui.Accent; run.FontWeight = FontWeight.SemiBold; }
            inlines.Add(run);
            at = stop;
        }
    }
}