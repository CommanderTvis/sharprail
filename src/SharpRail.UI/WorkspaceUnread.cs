using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

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
        workbench.Signals.ClearFailed(workspaceRoot);
        _ = ShareAsync(HostStateChange.MarkRead(workspaceRoot, seen: true));
    }

    /// <summary>Mark as unread / Mark as read, toggling on the workspace's current mark.</summary>
    private MenuItem UnreadMenuItem(ContextMenu menu, WorkspaceRecord worktree)
    {
        bool Unread() => state.Current.UnreadWorkspaces.ContainsKey(worktree.Path);
        var item = Ui.Menu("", () =>
        {
            if (Unread()) { if (heldUnread == worktree.Path) heldUnread = ""; workbench.Signals.ClearFailed(worktree.Path); _ = ShareAsync(HostStateChange.MarkRead(worktree.Path)); }
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

    private static string AgentName(string kind) => kind switch { "claude" => "Claude Code", "codex" => "Codex", "omp" => "omp", var other => other };

    private string TabTitle(string workspace, string tab) =>
        state.Current.TerminalTitles.FirstOrDefault(title => title.Terminal.WorkspaceId == workspace && title.Terminal.TabKey == tab)?.Title is { Length: > 0 } title ? title
        : Layout.State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values.SelectMany(tabs => tabs).FirstOrDefault(open => open.Id == tab)?.Title ?? tab;

    private string FailedPhrase(string workspace, string tab) =>
        $"{AgentName(state.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == new TerminalRef(workspace, tab))?.Record.Kind ?? "agent")} in “{TabTitle(workspace, tab)}”";

    /// <summary>The tooltip of one failed agent terminal's mark, read from in-memory state when it opens.</summary>
    internal string FailedAgentTip(string workspace, string tab) => FailedPhrase(workspace, tab) + " hit an error";

    /// <summary>The workspace row's tooltip for a mark: a failure names up to three agents and their tabs, then how many more.</summary>
    internal string UnreadTip(string workspace, string reason)
    {
        if (reason != "failed" || workbench.Signals.FailedTabs(workspace) is not { Count: > 0 } tabs) return UnreadLook(reason).Tip;
        var named = string.Join(", ", tabs.Take(3).Select(tab => FailedPhrase(workspace, tab)));
        return named + (tabs.Count > 3 ? $" and {tabs.Count - 3} more" : "") + " hit an error";
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