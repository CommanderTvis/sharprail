using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

/// <summary>
/// The unread model (ThinkRail #770, STATUS A18): a workspace whose agent finished, failed or waits for input while the
/// user was not looking at it is unread in host state, shared by every window and client and kept across restarts.
/// Seeing it in a focused window marks it read; the workspace menu marks it unread or read by hand.
/// </summary>
public sealed partial class WorkbenchWindow
{
    // A workspace marked unread by hand while it is shown stays lit until the user leaves it and comes back.
    private string heldUnread = "";

    internal bool ShowsWorkspace(string workspace) => WorkspaceMounted && !atHome && workspaceRoot == workspace;

    /// <summary>Marks the workspace read in host state when this focused window shows it, unless a hand-set mark holds it.</summary>
    internal void MarkShownWorkspaceRead()
    {
        if (!IsActive || !WorkspaceMounted || atHome || heldUnread == workspaceRoot) return;
        if (!state.Current.UnreadWorkspaces.ContainsKey(workspaceRoot)) return;
        _ = ShareAsync(HostStateChange.MarkRead(workspaceRoot, seen: true));
    }

    /// <summary>Mark as unread / Mark as read, toggling on the workspace's current mark.</summary>
    private MenuItem UnreadMenuItem(ContextMenu menu, WorkspaceRecord worktree)
    {
        bool Unread() => state.Current.UnreadWorkspaces.ContainsKey(worktree.Path);
        var item = Ui.Menu("", () =>
        {
            if (Unread()) { if (heldUnread == worktree.Path) heldUnread = ""; _ = ShareAsync(HostStateChange.MarkRead(worktree.Path)); }
            else
            {
                if (ShowsWorkspace(worktree.Path)) heldUnread = worktree.Path;
                _ = ShareAsync(HostStateChange.MarkUnread(worktree.Path, "manual"));
            }
        });
        // A row's menu outlives broadcasts, so the toggle reads the mark each time it opens.
        item.Name = "WorkspaceReadToggle";
        void Label() => item.Header = Unread() ? "Mark as read" : "Mark as unread";
        Label();
        menu.Opening += (_, _) => Label();
        menu.Opened += (_, _) => Label();
        return item;
    }

    /// <summary>The rail's dot colour and tooltip for a mark: waiting input warns, a failure is danger, any other result is success.</summary>
    internal static (Avalonia.Media.IBrush Brush, string Tip) UnreadLook(string reason) => reason switch
    {
        "blocked" => (Ui.Warning, "An agent waits for you"),
        "failed" => (Ui.Danger, "An agent hit an error"),
        "manual" => (Ui.Success, "Marked unread"),
        _ => (Ui.Success, "An agent finished")
    };
}