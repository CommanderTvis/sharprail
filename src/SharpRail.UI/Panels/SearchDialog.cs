using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

/// <summary>
/// One query over the whole active worktree, asked and dismissed: results grouped by file, one row per
/// matching line. It closes with the chosen hit; Escape closes it with none.
/// </summary>
public sealed class SearchDialog
{
    private readonly Func<string, CancellationToken, Task<SearchHits>> search;
    private readonly TextBox query;
    private readonly StackPanel results;
    private readonly TextBlock status;
    private readonly DispatcherTimer debounce;
    private CancellationTokenSource? pending;

    public Window Window { get; }

    public SearchDialog(Func<string, CancellationToken, Task<SearchHits>> search)
    {
        this.search = search;
        Window = Dialogs.Create("Search in workspace", 640);
        Window.Tag = "SearchDialog";
        var fields = Window.FindControl<StackPanel>("DialogFields")!;
        query = new TextBox { Name = "SearchQuery", PlaceholderText = "Find in files…" };
        AutomationProperties.SetName(query, "Search query");
        fields.Children.Add(query);
        status = Ui.Text("", Ui.Hint, 12);
        status.Name = "SearchStatus"; status.IsVisible = false;
        fields.Children.Add(status);
        results = new StackPanel { Name = "SearchResults", Spacing = 2 };
        fields.Children.Add(new ScrollViewer { Content = results, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = RunAsync(); };
        query.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        Window.Opened += (_, _) => query.Focus();
        Window.Closed += (_, _) => { debounce.Stop(); pending?.Cancel(); };
    }

    public Task<SearchHit?> ShowAsync(Window owner) => Window.ShowDialog<SearchHit?>(owner);

    private async Task RunAsync()
    {
        pending?.Cancel();
        var cancellation = pending = new CancellationTokenSource();
        var text = query.Text ?? "";
        if (text.Length == 0) { results.Children.Clear(); status.IsVisible = false; return; }
        SearchHits found;
        try { found = await search(text, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested) return;
            results.Children.Clear();
            status.Text = "Search failed: " + error.Message; status.IsVisible = true;
            return;
        }
        if (cancellation.IsCancellationRequested) return;
        Render(found);
    }

    private void Render(SearchHits found)
    {
        results.Children.Clear();
        status.IsVisible = found.Hits.Count == 0 || found.Truncated;
        status.Text = found.Hits.Count == 0 ? "No matches" : $"Showing the first {found.Hits.Count} matches";
        foreach (var file in found.Hits.GroupBy(hit => hit.Path))
        {
            var header = Ui.Row("fileText", file.Key, Ui.TextBrush);
            header.Name = "SearchFile"; header.Tag = file.Key; header.Margin = new Thickness(0, 6, 0, 2);
            results.Children.Add(header);
            foreach (var hit in file)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
                var number = Ui.Text(hit.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), Ui.Hint, 12);
                number.HorizontalAlignment = HorizontalAlignment.Right; number.Margin = new Thickness(0, 0, 8, 0);
                Ui.Place(row, number);
                var line = Ui.Text(hit.Text.Trim(), Ui.TextBrush, 12);
                line.FontFamily = Ui.CodeFont; line.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
                Ui.Place(row, line, 0, 1);
                var button = new Button
                {
                    Name = "SearchHit",
                    Tag = hit,
                    Content = row,
                    Background = Avalonia.Media.Brushes.Transparent,
                    BorderThickness = new(0),
                    Padding = new Thickness(4, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(button, $"{hit.Path} line {hit.Line}");
                button.Click += (_, _) => Window.Close(hit);
                results.Children.Add(button);
            }
        }
    }
}
