using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

/// <summary>Chooses one of the project's worktrees that no workspace represents yet and attaches it in place.</summary>
public sealed class ExistingWorktreeDialog
{
    private readonly Func<Task<IReadOnlyList<ExistingWorktree>>> list;
    private readonly Func<string, Task<WorkspaceRecord>> attach;
    private readonly StackPanel rows = new() { Name = "ExistingWorktreeList", Spacing = 4 };
    private readonly TextBlock error = Ui.Text("", Ui.Danger);
    private int request;
    private bool opening;

    public Window Window { get; }

    public ExistingWorktreeDialog(Func<Task<IReadOnlyList<ExistingWorktree>>> list, Func<string, Task<WorkspaceRecord>> attach)
    {
        this.list = list; this.attach = attach;
        Window = Dialogs.Create("Open existing worktree", 560);
        Window.Tag = "ExistingWorktreeDialog";
        var description = Window.FindControl<TextBlock>("DialogExplanation")!;
        description.Text = "Choose a checkout already registered with this Git repository. SharpRail will use it in place without moving, renaming, or taking ownership of it.";
        description.IsVisible = true;
        var fields = Window.FindControl<StackPanel>("DialogFields")!;
        fields.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        error.Name = "ExistingWorktreeError"; error.IsVisible = false; error.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        fields.Children.Add(error);
        Window.FindControl<StackPanel>("DialogActions")!.Children.Add(Ui.Button("Cancel", () => { if (!opening) Window.Close(null); }));
        Window.Closing += (_, e) => e.Cancel = opening;
        Window.Opened += (_, _) => _ = LoadAsync();
    }

    public Task<WorkspaceRecord?> ShowAsync(Window owner) => Window.ShowDialog<WorkspaceRecord?>(owner);

    private async Task LoadAsync()
    {
        var current = ++request;
        error.IsVisible = false;
        rows.Children.Clear();
        rows.Children.Add(Note("ExistingWorktreeLoading", "Reading Git worktrees…"));
        IReadOnlyList<ExistingWorktree> candidates;
        try { candidates = await list(); }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            if (current != request) return;
            rows.Children.Clear();
            var message = Ui.Text("Couldn't list existing worktrees. " + failure.Message, Ui.Danger);
            message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            rows.Children.Add(message);
            var retry = Ui.Button("Retry", () => _ = LoadAsync(), "refresh");
            retry.Name = "ExistingWorktreeRetry"; retry.HorizontalAlignment = HorizontalAlignment.Left;
            rows.Children.Add(retry);
            return;
        }
        if (current != request) return;
        rows.Children.Clear();
        if (candidates.Count == 0) rows.Children.Add(Note("ExistingWorktreeEmpty", "No unattached worktrees found. Create one with Git, then reopen this chooser."));
        foreach (var candidate in candidates) rows.Children.Add(Row(candidate));
        rows.Children.OfType<Button>().FirstOrDefault(row => row.IsEnabled)?.Focus();
    }

    private static TextBlock Note(string name, string text)
    {
        var note = Ui.Text(text, Ui.Hint);
        note.Name = name; note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        return note;
    }

    private Button Row(ExistingWorktree candidate)
    {
        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(Ui.Text(candidate.IsDetached ? "Detached HEAD" : candidate.Branch, Ui.TextBrush));
        var path = Ui.Text(candidate.Path, Ui.Hint, 12); path.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        labels.Children.Add(path);
        if (candidate.IsDetached) labels.Children.Add(Ui.Text("Create a branch in this worktree before opening it.", Ui.Hint, 12));
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("14,8,*") };
        var icon = Ui.Icon("gitBranch", null, 14); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new(0, 2, 0, 0);
        Ui.Place(content, icon); Ui.Place(content, labels, 0, 2);
        var row = new Button
        {
            Name = "ExistingWorktreeCandidate",
            Tag = candidate.Path,
            Content = content,
            IsEnabled = !candidate.IsDetached,
            Padding = new(12, 8),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new(1),
            CornerRadius = new(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(row, candidate.IsDetached ? "Detached HEAD " + candidate.Path : candidate.Branch);
        ToolTip.SetTip(row, candidate.Path);
        row.Click += (_, _) => _ = OpenAsync(candidate.Path);
        return row;
    }

    private async Task OpenAsync(string path)
    {
        if (opening) return;
        opening = true; error.IsVisible = false; rows.IsEnabled = false;
        try
        {
            var workspace = await attach(path);
            opening = false;
            Window.Close(workspace);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            error.Text = "Couldn't open the existing worktree. " + failure.Message; error.IsVisible = true;
        }
        finally { opening = false; rows.IsEnabled = true; }
    }
}