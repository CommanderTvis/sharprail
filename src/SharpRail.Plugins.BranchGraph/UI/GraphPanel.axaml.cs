using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.BranchGraph.UI;

/// <summary>
/// The Git Graph side tool: the project's branches drawn as commit history, lane-laid, paged and windowed. Clicking a
/// commit scopes the Changes panel to it; right-clicking copies its hash or its patch. Nothing here changes a repository.
/// </summary>
internal sealed partial class GraphPanel : UserControl
{
    // Rows kept beyond each edge of the viewport, so a fast scroll never shows the gap it opens.
    private const int Overscan = 12;
    private readonly IPluginUIContext context;
    private string workspaceId;
    private readonly TextBlock message;
    private readonly ScrollViewer scroll;
    private readonly Border body;
    private readonly StackPanel window;
    private readonly List<IDisposable> observers = [];
    private readonly SortedDictionary<int, GraphCommitRow> built = [];
    private History? history;
    private Dictionary<string, List<string>> marks = [];
    private string? projectId;
    private int generation, drawnLanes = 1, shownLanes = 1;
    private bool reading;

    private sealed record History(IReadOnlyList<GraphRow> Rows, IReadOnlyList<string> Carry, IReadOnlyList<GitGraphWorktree> Worktrees, bool HasMore);

    public GraphPanel(IPluginUIContext context, string workspaceId)
    {
        this.context = context;
        this.workspaceId = workspaceId;
        AvaloniaXamlLoader.Load(this);
        message = this.FindControl<TextBlock>("GraphMessage")!;
        scroll = this.FindControl<ScrollViewer>("GraphScroll")!;
        body = this.FindControl<Border>("GraphBody")!;
        window = this.FindControl<StackPanel>("GraphWindow")!;
        scroll.ScrollChanged += (_, _) => { Window(); MaybePage(); };
        AttachedToVisualTree += (_, _) =>
        {
            if (observers.Count > 0) return;
            // The project is the one this panel's own workspace belongs to, not whichever window has focus.
            observers.Add(context.WatchHost(OwnProject, (next, _) =>
            {
                projectId = next;
                history = null;
                Message("Reading history…");
                Read(null);
            }));
            // A write in the worktree can move any ref, so the history is read again from its tip, in place, because
            // tearing the list down would throw the reader back to the top mid-scroll.
            observers.Add(context.WatchHost(ProjectRevision, (_, _) => Read(null)));
            projectId = OwnProject(context.Host());
            Read(null);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            generation++;
            reading = false;
            foreach (var observer in observers) observer.Dispose();
            observers.Clear();
        };
    }

    private string? OwnProject(PluginHostProjection host) =>
        host.Workspaces.Values.SelectMany(workspaces => workspaces).FirstOrDefault(workspace => workspace.Id == workspaceId)?.ProjectId;

    private int ProjectRevision(PluginHostProjection host) => host.Workspaces.Values.SelectMany(workspaces => workspaces)
        .Where(workspace => workspace.ProjectId == projectId).Sum(workspace => host.WorkspaceRevisions.GetValueOrDefault(workspace.Id));

    internal bool Retarget(string workspace)
    {
        var host = context.Host();
        var nextProject = host.Workspaces.Values.SelectMany(workspaces => workspaces).FirstOrDefault(item => item.Id == workspace)?.ProjectId;
        if (projectId is null || nextProject != projectId) return false;
        workspaceId = workspace;
        return true;
    }

    /// <summary>The rows read so far, the lanes the rows on screen need, and how many rows are built, for the checks.</summary>
    internal int Rows => history?.Rows.Count ?? 0;
    internal int ShownLanes => shownLanes;
    internal int Built => built.Count;
    internal int Reads { get; private set; }
    internal string? ProjectId => projectId;
    internal string? MessageText => message.IsVisible ? message.Text : null;

    private void Message(string text, bool failed = false)
    {
        message.Text = text;
        message.Foreground = failed ? Ui.Danger : Ui.Muted;
        message.IsVisible = true;
        scroll.IsVisible = false;
    }

    private async void Read(History? previous)
    {
        if (projectId is not { } project) return;
        if (previous is not null && reading) return;
        var ticket = previous is null ? ++generation : generation;
        reading = true;
        GitGraph page;
        try { page = await context.RequestAsync(BranchGraphContract.Graph, new GitGraphParams(project, previous?.Rows.Count ?? 0)); }
        catch (PluginCallException error)
        {
            if (ticket == generation) { reading = false; Message("Could not read the history.", failed: true); context.Log.Warn(error.Message); }
            return;
        }
        if (ticket != generation) return;
        reading = false;
        Reads++;
        var (rows, carry) = GraphLanes.Layout(page.Commits, previous?.Carry);
        history = new([.. previous?.Rows ?? [], .. rows], carry, previous?.Worktrees ?? page.Worktrees, page.HasMore);
        Show();
    }

    private void MaybePage()
    {
        if (history is not { HasMore: true } current || scroll.Viewport.Height <= 0) return;
        var remaining = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
        if (remaining < LaneArt.RowHeight * Overscan) Read(current);
    }

    private void Show()
    {
        var rows = history!.Rows;
        if (rows.Count == 0) { Message("No commits on any branch yet."); return; }
        message.IsVisible = false;
        scroll.IsVisible = true;
        marks = [];
        foreach (var tree in history.Worktrees)
            (marks.TryGetValue(tree.Sha, out var names) ? names : marks[tree.Sha] = []).Add(tree.Name);
        drawnLanes = rows.Max(row => row.Width) + 1;
        body.Height = rows.Count * LaneArt.RowHeight;
        Window();
        scroll.UpdateLayout();
        MaybePage();
    }

    // Only the rows around the viewport exist; the scroller is sized by counting rows rather than building them.
    private void Window()
    {
        if (history is not { } current || current.Rows.Count == 0) return;
        var top = scroll.Offset.Y;
        var height = scroll.Viewport.Height;
        var first = Math.Max(0, (int)Math.Floor(top / LaneArt.RowHeight) - Overscan);
        var last = Math.Min(current.Rows.Count, (int)Math.Ceiling((top + height) / LaneArt.RowHeight) + Overscan);
        var previous = built.Values.ToDictionary(item => item.Model.Commit.Sha);
        // Every row on screen shares one gutter width, so the lanes stay in column while it changes.
        var shown = 1;
        for (var index = first; index < last; index++) shown = Math.Max(shown, current.Rows[index].Width + 1);
        shownLanes = shown;
        var next = new SortedDictionary<int, GraphCommitRow>();
        for (var index = first; index < last; index++)
        {
            var model = current.Rows[index];
            if (previous.TryGetValue(model.Commit.Sha, out var item))
            {
                item.Update(model, drawnLanes, shown, marks.GetValueOrDefault(model.Commit.Sha) ?? []);
                next[index] = item;
            }
            else next[index] = Row(model);
        }
        var controls = next.Values.ToArray();
        foreach (var control in window.Children.Where(control => !controls.Contains(control)).ToArray()) window.Children.Remove(control);
        for (var index = 0; index < controls.Length; index++)
        {
            var existing = window.Children.IndexOf(controls[index]);
            if (existing < 0) window.Children.Insert(index, controls[index]);
            else if (existing != index) window.Children.Move(existing, index);
        }
        built.Clear();
        foreach (var item in next) built.Add(item.Key, item.Value);
        window.Margin = new Thickness(0, first * LaneArt.RowHeight, 0, 0);
    }

    private GraphCommitRow Row(GraphRow row) => new(context, () => workspaceId, row, drawnLanes, shownLanes,
        marks.GetValueOrDefault(row.Commit.Sha) ?? [], sha => _ = CopyHashAsync(sha), sha => _ = CopyPatchAsync(sha));

    private async Task<bool> CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        try { await clipboard.SetTextAsync(text); return true; }
        catch { return false; }
    }

    private async Task CopyHashAsync(string sha)
    {
        if (!await CopyAsync(sha)) context.Notify(PluginNotificationKind.Error, "Could not copy commit hash");
    }

    private async Task CopyPatchAsync(string sha)
    {
        if (projectId is not { } project) return;
        try
        {
            var result = await context.RequestAsync(BranchGraphContract.Patch, new GitPatchParams(project, sha));
            if (!await CopyAsync(result.Patch)) context.Notify(PluginNotificationKind.Error, "Could not copy patch to clipboard");
        }
        catch (PluginCallException error) { context.Notify(PluginNotificationKind.Error, "Could not copy patch", error.Message); }
    }
}