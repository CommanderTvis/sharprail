using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// This window's view of settled workspaces: the shared 30-second clock, the active-selection latch, the shelf's
/// disclosure and paging, the Settle/Keep active actions and the one-time notice of the first automatic move.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private readonly DispatcherTimer settleClock = new() { Interval = WorkspaceSettling.Tick };
    // The workspace the latch was decided for, and whether it was live then: opening a settled workspace leaves it on
    // the shelf, and a row whose window elapses while you are in it stays put until you leave.
    private string? latchedFor;
    private string? latchedLive;
    private string? revealedSettled;
    // The project of the workspace the window landed on, shown expanded over the persisted collapsed set until
    // the window lands in another project or the user collapses it; the persisted set itself is untouched.
    private string? revealedProject;
    private string revealedFor = "";
    private readonly HashSet<string> expandedShelves = [];
    private readonly Dictionary<string, int> shownSettled = [];
    private readonly string settledSignature = "";
    private int? pendingAutoSettled;
    private readonly DispatcherTimer settledNoticeDelay = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>The host's clock is not consulted: partitions compare its stamps with this client's time.</summary>
    internal Func<long> SettlingClock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private bool SettlingSupported => state.SettlingSupported;

    private void WireSettling()
    {
        settleClock.Tick += (_, _) => RefreshSettling();
        settledNoticeDelay.Tick += (_, _) => { settledNoticeDelay.Stop(); ShowSettledNotice(); };
        LocationChanged += () =>
        {
            if (ObserveActiveWorkspace()) UpdateRailSelection();
            UpdateSettledCaption();
        };
        state.HandshakeChanged += SettlingHandshakeChanged;
        Closed += (_, _) => { settleClock.Stop(); settledNoticeDelay.Stop(); state.HandshakeChanged -= SettlingHandshakeChanged; };
        settleClock.Start();
    }

    private void SettlingHandshakeChanged(HostHandshake? handshake)
    {
        if (handshake is not null) RefreshSettling(force: true);
    }

    /// <summary>Re-derives every partition on the shared clock and redraws only when one changed.</summary>
    internal void RefreshSettling(bool force = false)
    {
        var signature = SettledSignature();
        if (!force && signature == settledSignature) { CheckSettledNotice(); return; }
        KeepingFocus(() => { UpdateRailSelection(); UpdateScopeLabels(); });
        CheckSettledNotice();
    }

    private string SettledSignature() => SettlingSupported + "\n" + string.Join("\n", state.Current.Projects.Select(project =>
        project + "\t" + string.Join(",", PartitionOf(project).Settled.Select(row => row.Workspace.Path + ":" + WorkspaceSettling.Chip(row, SettlingClock())))));

    private WorkspacePartition PartitionOf(string project, WorkspaceOrder order = WorkspaceOrder.Created)
    {
        ObserveActiveWorkspace();
        return WorkspaceSettling.Partition(RailWorkspaces(project), SettlingSupported, state.Current.Settings.SettleIdleDays, SettlingClock(), latchedLive, order);
    }

    private SettledRow? SettledRowOf(string path) => state.Current.Workspaces.FirstOrDefault(workspace => workspace.Path == path) is { } record
        ? PartitionOf(record.ProjectRoot).Settled.FirstOrDefault(row => row.Workspace.Path == path) : null;

    /// <summary>
    /// Decides the latch when the window lands on another workspace, reveals that workspace's project once per change of
    /// project, and shows a settled one on its shelf once per selection. Returns whether the rail needs redrawing.
    /// </summary>
    private bool ObserveActiveWorkspace()
    {
        var changed = false;
        var owner = atHome ? "" : projectRoot;
        if (owner.Length > 0 && owner != revealedFor)
        {
            revealedFor = owner;
            changed = profile.Data.CollapsedProjects.Contains(owner) && revealedProject != owner;
            revealedProject = owner;
        }
        var active = atHome || workspaceRoot.Length == 0 ? null : workspaceRoot;
        if (active == latchedFor) return changed;
        latchedFor = active;
        var record = active is null ? null : state.Current.Workspaces.FirstOrDefault(workspace => workspace.Path == active);
        latchedLive = record is not null && WorkspaceSettling.Reason(record, state.Current.Settings.SettleIdleDays, SettlingClock()) is null ? active : null;
        if (record is null || latchedLive is not null || !SettlingSupported || revealedSettled == active) return changed;
        revealedSettled = active;
        var settled = WorkspaceSettling.Partition(RailWorkspaces(record.ProjectRoot), true, state.Current.Settings.SettleIdleDays, SettlingClock(), latchedLive).Settled;
        var index = settled.ToList().FindIndex(row => row.Workspace.Path == active);
        if (index < 0) return changed;
        expandedShelves.Add(record.ProjectRoot);
        shownSettled[record.ProjectRoot] = Math.Max(ShownSettled(record.ProjectRoot), index + 1);
        return true;
    }

    private bool Collapsed(string project) => profile.Data.CollapsedProjects.Contains(project) && project != revealedProject;

    private int ShownSettled(string project) => shownSettled.GetValueOrDefault(project, WorkspaceSettling.ShelfPage);

    /// <summary>Real work in the open workspace re-arms its latch, so it stays live while you work in it.</summary>
    private void ObserveSettlingChange(HostState previous, HostState next)
    {
        if (latchedFor is null) return;
        var before = previous.Workspaces.FirstOrDefault(workspace => workspace.Path == latchedFor)?.LastActiveAt;
        var after = next.Workspaces.FirstOrDefault(workspace => workspace.Path == latchedFor)?.LastActiveAt;
        if (after > before) latchedLive = latchedFor;
        if (next.Settings.SettleIdleDays != previous.Settings.SettleIdleDays || !next.Workspaces.SequenceEqual(previous.Workspaces))
            Dispatcher.UIThread.Post(() => RefreshSettling());
    }

    private async Task SetSettledAsync(WorkspaceRecord workspace, bool settle)
    {
        try
        {
            await Task.Run(async () => await host.ApplyWorkspaceActionAsync(settle ? WorkspaceAction.Settle(workspace.Id) : WorkspaceAction.Unsettle(workspace.Id), lifetime.Token), lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(new IOException((settle ? "Couldn't settle the workspace. " : "Couldn't keep the workspace active. ") + error.Message)); }
    }

    /// <summary>The Settle or Keep active item for a workspace's menus; null where neither applies.</summary>
    private MenuItem? SettleMenuItem(WorkspaceRecord worktree)
    {
        if (!SettlingSupported || worktree.Kind == WorkspaceKinds.Default || worktree.Id.Length == 0) return null;
        var current = state.Current.Workspaces.FirstOrDefault(workspace => workspace.Id == worktree.Id) ?? worktree;
        var settled = SettledRowOf(current.Path) is not null;
        var item = Ui.Menu(settled ? "Keep active" : "Settle", () => _ = SetSettledAsync(current, !settled));
        item.Name = settled ? "WorkspaceKeepActive" : "WorkspaceSettle";
        return item;
    }

    private void UpdateSettledCaption()
    {
        var caption = this.FindControl<TextBlock>("ScopeSettled");
        if (caption is null) return;
        caption.IsVisible = !atHome && workspaceRoot.Length > 0 && SettledRowOf(workspaceRoot) is not null;
    }

    /// <summary>
    /// Announces, once per host, the first time the shelf took rows on its own (never ones settled by hand), after the
    /// count has held still for two seconds: an upgrade's backfill arrives as a burst, and the notice counts the burst.
    /// </summary>
    private void CheckSettledNotice()
    {
        if (!SettlingSupported || profile.Data.SettledShelfNotices.Contains(workbench.Endpoint)) return;
        var count = state.Current.Projects.Sum(project => PartitionOf(project).Settled.Count(row => row.Reason != SettledReason.ByYou));
        if (count == 0) { pendingAutoSettled = null; settledNoticeDelay.Stop(); return; }
        if (count == pendingAutoSettled) return;
        pendingAutoSettled = count;
        settledNoticeDelay.Stop(); settledNoticeDelay.Start();
    }

    private void ShowSettledNotice()
    {
        if (pendingAutoSettled is not { } count || !profile.Data.SettledShelfNotices.Add(workbench.Endpoint)) return;
        SaveProfile();
        var projects = state.Current.Projects.Where(project => PartitionOf(project).Settled.Any(row => row.Reason != SettledReason.ByYou)).ToArray();
        Toasts.Push(ToastVariant.Info,
            "Workspaces idle longer than the window in Settings › Workspaces move to their project's Settled shelf. Terminal input or a commit makes one live again; nothing on disk changes.",
            $"Moved {count} quiet {(count == 1 ? "workspace" : "workspaces")} to Settled", action: new("Show", () =>
            {
                foreach (var project in projects) { expandedShelves.Add(project); profile.Data.CollapsedProjects.Remove(project); }
                SaveProfile();
                UpdateRailSelection();
            }));
    }
}