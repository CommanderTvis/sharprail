using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.Plugins;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// The header's Remote segment (ThinkRail #771) and the place of a repo hosting plugin's item (W28): the segment turns the
/// host's branch status into the branch's next step. The host owns the facts and every mutation; this only derives the
/// action and runs it, binding it to the branch and commit it showed. A provider's pull request item and Review section
/// are the plugin's.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private BranchStatus? branchStatus;
    private string branchStatusKey = "";
    private long branchStatusLookup;
    private long branchStatusAt;
    private bool shipBusy;
    private bool statusLookupPending, shipExpectedShown;
    // Last-known facts of workspaces seen in this run: a switch back shows them at once, and the lookup replaces them in place.
    private readonly Dictionary<string, BranchStatus> statusCache = [];
    private readonly Dictionary<string, string> lastBranches = [];

    /// <summary>How long a switch's loading may last before the busy line shows; a quicker load never shows it.</summary>
    internal static readonly TimeSpan StatusBusyDelay = TimeSpan.FromMilliseconds(400);
    private Border BusyLine => this.FindControl<Border>("StatusBusy")!;
    private long busyTicket;
    private bool busyWaiting;

    /// <summary>True while the branch and Remote blocks wait for the workspace's git facts and keep their place meanwhile.</summary>
    private bool ShipExpected => !atHome && git.IsRepository && (gitLoading || (branchStatus is null && statusLookupPending));

    /// <summary>The busy line along the content's top edge is an overlay: it never takes room, so showing it cannot move the status area.</summary>
    internal bool StatusBusyShown => BusyLine.Opacity > 0;

    /// <summary>Forgets the facts seen in this run, as a first visit to every workspace would find them; checks use it.</summary>
    internal void ForgetBranchFacts() { statusCache.Clear(); lastBranches.Clear(); }

    /// <summary>A known status, even a cached one, is never "loading": its refresh runs unannounced.</summary>
    private bool IsStatusBusy => switchingWorkspace || (branchStatus is null && (ShipExpected || statusLookupPending));

    internal bool StatusLookupPending => statusLookupPending;
    internal bool StatusBusyPending => IsStatusBusy;

    private void SyncStatusBusy()
    {
        if (!IsStatusBusy)
        {
            busyTicket++; busyWaiting = false;
            BusyLine.Opacity = 0;
        }
        else if (!busyWaiting && !StatusBusyShown) { busyWaiting = true; _ = ShowBusyLaterAsync(++busyTicket); }
    }

    private async Task ShowBusyLaterAsync(long ticket)
    {
        try { await Task.Delay(StatusBusyDelay, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (ticket != busyTicket || !IsStatusBusy) return;
        BusyLine.Opacity = 1;
    }

    /// <summary>The branch status the header shows; checks read it.</summary>
    public BranchStatus? BranchStatus => branchStatus;

    /// <summary>The Remote segment's next step from a status, as an action name; one pure rule for the header and checks.</summary>
    internal static string RemoteAction(BranchStatus status) =>
        !status.HasOrigin ? "" : !status.Published ? "publish" : status.Behind > 0 ? "sync" : status.Ahead > 0 ? "push" : "";

    private void RequestBranchStatus(bool fresh, bool force = false)
    {
        // While the git facts load, the branch last seen in this workspace stands in, so its status starts at once.
        var branch = git.Branch.Length > 0 ? git.Branch : gitLoading ? lastBranches.GetValueOrDefault(workspaceRoot, "") : "";
        var key = WorkspaceMounted && !atHome && git.IsRepository && branch.Length > 0 ? workspaceRoot + "\0" + branch : "";
        var expected = ShipExpected;
        if (key != branchStatusKey)
        {
            branchStatusKey = key; branchStatus = statusCache.GetValueOrDefault(key); branchStatusAt = 0;
            // The read for the new key follows below, so its wait already counts while the blocks are laid out.
            statusLookupPending = key.Length > 0; expected = ShipExpected; shipExpectedShown = expected;
            UpdateShipSegments(); UpdateBranchSegment(); force = true;
        }
        else if (expected != shipExpectedShown) { shipExpectedShown = expected; UpdateShipSegments(); UpdateBranchSegment(); }
        SyncStatusBusy();
        if (key.Length == 0) return;
        // A watcher refresh asks at most every 15 seconds: each read may call gh.
        if (!force && !fresh && Environment.TickCount64 - branchStatusAt < 15_000) return;
        branchStatusAt = Environment.TickCount64;
        _ = LookupBranchStatusAsync(key, fresh);
    }

    private async Task LookupBranchStatusAsync(string key, bool fresh)
    {
        var lookup = ++branchStatusLookup;
        statusLookupPending = true;
        BranchStatus? status;
        try { status = await Task.Run(async () => await host.GetBranchStatusAsync(fresh, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Console.Error.WriteLine(error); status = null; }
        if (lookup != branchStatusLookup) return;
        statusLookupPending = false;
        if (key != branchStatusKey) { SyncStatusBusy(); return; }
        branchStatus = status;
        if (status is not null) statusCache[key] = status;
        UpdateShipSegments();
        SyncStatusBusy();
    }

    private void UpdateShipSegments()
    {
        var remoteRow = this.FindControl<DockPanel>("RemoteRow")!;
        var remoteBlock = this.FindControl<Grid>("RemoteBlock")!;
        remoteRow.Children.Clear();
        var status = branchStatus;
        repoSurface?.Raise();
        // Without a status the Remote block takes no room, so the branch block runs to the row's end; while one is expected it
        // keeps its place with an empty row, so the arrival fills it without moving its neighbours.
        remoteBlock.IsVisible = status is not null || ShipExpected;
        if (status is null) return;
        var remoteAction = RemoteAction(status);
        // Remote: its icon slot, its one action at the right end, and its state filling what is left (it trims, never overflows).
        var icon = ShipIcon("RemoteIcon", "network", status.HasOrigin ? Ui.Muted : Ui.Hint);
        remoteRow.Children.Add(icon);
        switch (remoteAction)
        {
            case "publish": AddDocked(ShipButton("RemotePublish", "Publish", () => _ = PushBranchAsync(status)), Dock.Right); break;
            case "push": AddDocked(ShipButton("RemotePush", $"Push {status.Ahead}", () => _ = PushBranchAsync(status)), Dock.Right); break;
            case "sync": AddDocked(ShipButton("RemoteSync", "Sync", () => _ = SyncBranchAsync(status)), Dock.Right); break;
        }
        if (!status.HasOrigin) AddDocked(ShipText("RemoteState", "No remote", Ui.Hint), Dock.Left);
        else if (!status.Published) AddDocked(ShipText("RemoteState", "Not published", Ui.Muted), Dock.Left);
        else if (status.Ahead <= 0 && status.Behind <= 0) AddDocked(ShipText("RemoteState", "In sync", Ui.Muted), Dock.Left);
        else
        {
            if (status.Ahead > 0) AddDocked(ShipText("RemoteAhead", "↑" + status.Ahead, Ui.TextBrush), Dock.Left);
            if (status.Behind > 0) AddDocked(ShipText("RemoteBehind", "↓" + status.Behind, Ui.Warning), Dock.Left);
        }

        void AddDocked(Control control, Dock dock)
        {
            if (dock == Dock.Left && control is TextBlock) control.Margin = new Thickness(0, 0, 8, 0);
            if (dock == Dock.Right) control.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(control, dock);
            remoteRow.Children.Add(control);
        }
    }

    private PluginRow<RepoHostUIRegistration>? shownRepoHostUi;

    /// <summary>Mounts the active repo hosting plugin's strip item in the segment after Remote, or empties it (W28).</summary>
    private void SyncRepoHostUi()
    {
        var row = workbench.PluginRegistry.ActiveRepoHostUi();
        if (ReferenceEquals(row, shownRepoHostUi)) return;
        shownRepoHostUi = row;
        var segment = this.FindControl<Border>("PullRequestSegment")!;
        var grid = (Grid)segment.Child!;
        var caption = (TextBlock)grid.Children[0];
        while (grid.Children.Count > 1) grid.Children.RemoveAt(1);
        if (row is null) { segment.IsVisible = false; return; }
        caption.Text = row.Value.Caption;
        Control? content = null;
        var item = Mount(row.PluginId, () => content = row.Value.LocationItem(RepoHostSurface));
        Grid.SetRow(item, 1);
        grid.Children.Add(item);
        // The plugin's control hides itself while it has nothing to show; the segment follows it.
        segment.IsVisible = content?.IsVisible ?? false;
        if (content is not null)
            content.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && ReferenceEquals(shownRepoHostUi, row)) segment.IsVisible = content.IsVisible; };
    }

    /// <summary>The Review panel's place for the repo hosting plugin's section; it follows the registry while shown (W28).</summary>
    internal ContentControl RepoHostReviewSection()
    {
        var host = new ContentControl { Name = "ReviewPullRequestHost" };
        PluginRow<RepoHostUIRegistration>? shown = null;
        void Sync()
        {
            var row = workbench.PluginRegistry.ActiveRepoHostUi();
            if (ReferenceEquals(row, shown)) return;
            shown = row;
            host.Content = row is null ? null : Mount(row.PluginId, () => row.Value.ReviewSection(RepoHostSurface));
        }
        void Changed(PluginTables tables)
        {
            if ((tables & (PluginTables.RepoHostUis | PluginTables.Active | PluginTables.Roster)) == 0) return;
            if (Dispatcher.UIThread.CheckAccess()) Sync(); else Dispatcher.UIThread.Post(Sync);
        }
        host.AttachedToVisualTree += (_, _) => { workbench.PluginRegistry.Changed += Changed; Sync(); };
        host.DetachedFromVisualTree += (_, _) => { workbench.PluginRegistry.Changed -= Changed; host.Content = null; shown = null; };
        return host;
    }

    // The field's leading icon, in the same column as the other location fields' icons.
    private static ContentControl ShipIcon(string name, string icon, IBrush brush) => new()
    {
        Name = name,
        Width = LayoutRoles.LocationIconSlot,
        Margin = new Thickness(0, 0, 4, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Content = Ui.Icon(icon, brush, 14)
    };

    private static TextBlock ShipText(string name, string text, IBrush brush)
    {
        var block = Ui.Text(text, brush, 12);
        block.Name = name; block.VerticalAlignment = VerticalAlignment.Center;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    private Button ShipButton(string name, string label, Action action)
    {
        var button = new Button
        {
            Name = name,
            Content = Ui.Text(label, Ui.TextBrush, 12),
            Height = 20,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !shipBusy
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private async Task<BranchActionResult?> BranchActionAsync(BranchStatus status, BranchAction action, string? failure = null)
    {
        if (shipBusy) return null;
        var key = branchStatusKey;
        shipBusy = true; UpdateShipSegments();
        try
        {
            var result = await Task.Run(async () => await host.ApplyBranchActionAsync(action, lifetime.Token), lifetime.Token);
            if (result.Outcome == "authFailed") Toasts.Push(ToastVariant.Error, "git push could not authenticate; check your SSH keys or credentials.", "Push failed");
            else if (result.Outcome == "done") Toasts.Push(ToastVariant.Success, result.Message, "Done");
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            Toasts.Push(ToastVariant.Error, error.Message, failure ?? "Couldn't update the branch");
            return null;
        }
        finally
        {
            shipBusy = false;
            if (key == branchStatusKey) { UpdateShipSegments(); RequestBranchStatus(true, force: true); }
        }
    }

    private async Task PushBranchAsync(BranchStatus status)
    {
        var result = await BranchActionAsync(status, new("push", status.Branch, status.Head), "Push failed");
        if (result?.Outcome != "confirmBase") return;
        // A branch that is its own base is shared: pushing it is asked first and names the branch.
        if (!await Dialogs.Confirm(this, $"Push {status.Branch}?", result.Message + " Push it to origin anyway?", "Push", "RemotePushConfirm")) return;
        await BranchActionAsync(status, new("push", status.Branch, status.Head, ConfirmBase: true), "Push failed");
    }

    /// <summary>Sync shows the shell-inert integration command to copy; it never force-pushes.</summary>
    private async Task SyncBranchAsync(BranchStatus status)
    {
        if (!ShellInert(status.Branch))
        {
            await Dialogs.Notice(this, "Integrate origin", $"Integrate origin/{status.Branch} in a terminal before pushing. Its name has shell-special characters, so no command is offered.");
            return;
        }
        var command = "git pull --rebase origin " + status.Branch;
        if (await Dialogs.Confirm(this, "Integrate origin", $"origin/{status.Branch} has {status.Behind} commit{(status.Behind == 1 ? "" : "s")} you don't have. " +
            $"Run this in a terminal, then push:\n{command}", "Copy command", "RemoteSyncCopy"))
            await CopyIntegrateAsync(command);
    }
}