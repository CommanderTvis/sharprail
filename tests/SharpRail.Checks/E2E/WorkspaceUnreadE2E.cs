using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream #770 on SharpRail's unread model (STATUS A18): an agent result in a workspace the user is not looking at
/// leaves it unread in host state for every window; seeing it in a focused window reads it; the workspace menu marks it
/// unread (held while shown) or read, and Mark as read keeps a waiting agent's mark.
/// </summary>
internal static class WorkspaceUnreadE2E
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "workspace-unread");
        using var git = new IsolatedGit(Path.Combine(root, "workspace-unread-git"));
        using var app = OpenFixtureProject(directory);
        var project = app.Window.ProjectRoot;
        var other = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        app.Window.Activate();
        Until(() => app.Window.IsActive);
        Border Dot(E2eWorkspace client, string path) => Item(client, path).GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "WorkspaceUnread");
        string? Mark(string path) => app.Workbench.State.Current.UnreadWorkspaces.GetValueOrDefault(path);

        // A result in a workspace that is not shown leaves it unread, in every window.
        using var observer = app.NewWindow();
        app.Window.Activate();
        app.Workbench.RequestAttention(new(project, "terminal:1", "Claude finished", "") { Reason = "done" });
        Until(() => Mark(project) == "done" && Dot(app, project).IsVisible && Dot(observer, project).IsVisible);
        // A result in the shown workspace of the focused window is seen at once.
        app.Workbench.RequestAttention(new(other, "terminal:2", "Codex finished", "") { Reason = "done" });
        Settle(300);
        Require(Mark(other) is null, "A result the user is looking at must not mark its workspace unread.");
        // Arriving at the unread workspace in a focused window reads it.
        WorkspaceTabsE2E.Switch(app, project);
        app.Window.Activate();
        Until(() => Mark(project) is null && !Dot(app, project).IsVisible);
        Console.WriteLine("PASS upstream #770: a result not looked at marks its workspace unread for every window; seeing it reads it");

        // Mark as unread on the shown workspace stays lit until the user leaves and comes back.
        var menu = OpenWorkspaceMenu(app, project);
        var entries = menu.Items.OfType<MenuItem>().ToArray();
        Require(entries[0].Name == "WorkspaceCopyPath" && entries[0].Header is "Copy absolute path" && entries[1].Name == "WorkspaceOpenIn",
            "The workspace menu opens with Copy absolute path, then Open in.");
        var removal = entries.SingleOrDefault(item => item.Name == "WorkspaceRemove");
        Require(removal is null || removal.Header is "Remove worktree…" or "Remove from SharpRail", "Removal is not the workspace menu's last item.");
        Require(entries.Where(item => item.Icon is not null).Select(item => item.Name).Order()
            .SequenceEqual(new[] { "WorkspaceCopyPath", "WorkspaceRemove" }.Where(name => entries.Any(item => item.Name == name)).Order()),
            "Only Copy absolute path and Remove worktree carry icons in the workspace menu.");
        Until(() => entries[0].GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Any(part => part.Name == "PART_HeaderPresenter" && part.Bounds.Width > 0));
        var lefts = entries.Where(item => item.IsVisible).Select(item => Avalonia.VisualExtensions.TranslatePoint(
            item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(part => part.Name == "PART_HeaderPresenter"), default, item)!.Value.X).ToArray();
        Require(lefts.All(left => Math.Abs(left - lefts[0]) < 0.5), "The workspace menu aligns labels differently with and without an icon: " + string.Join(", ", lefts));
        app.Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "WorkspaceReadToggle" && Equals(item.Header, "Mark as unread")), freshGesture: false);
        Until(() => Mark(project) == "manual");
        app.Window.Activate(); Settle(300);
        Require(Mark(project) == "manual" && Dot(app, project).IsVisible, "A hand-set mark on the shown workspace holds while it stays shown.");
        WorkspaceTabsE2E.Switch(app, other);
        Require(Mark(project) == "manual", "Leaving keeps the mark.");
        WorkspaceTabsE2E.Switch(app, project);
        app.Window.Activate();
        Until(() => Mark(project) is null);
        // Mark as read clears a result but never a waiting agent; seeing the workspace does.
        app.Workbench.RequestAttention(new(other, "terminal:2", "Codex needs you", "") { Reason = "blocked" });
        Until(() => Mark(other) == "blocked");
        menu = OpenWorkspaceMenu(app, other);
        app.Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "WorkspaceReadToggle" && Equals(item.Header, "Mark as read")), freshGesture: false);
        Settle(300);
        Require(Mark(other) == "blocked", "Mark as read must not dismiss a waiting agent.");
        WorkspaceTabsE2E.Switch(app, other);
        app.Window.Activate();
        Until(() => Mark(other) is null);
        Console.WriteLine("PASS upstream #770: Mark as unread holds while shown and clears on return; Mark as read keeps a waiting agent's mark");

        // A failed agent is named on its workspace row and marked on its own tab row, both inside their rows.
        const string failedTab = "failed-agent";
        app.Window.Layout.NewTerminal(app.Center, failedTab);
        var agent = new TerminalRef(other, failedTab);
        app.State!.SetTerminalAgent(agent, new("claude", "/usr/bin/false"));
        Until(() => app.Workbench.State.Current.TerminalAgents.Any(record => record.Terminal == agent));
        WorkspaceTabsE2E.Switch(app, project);
        app.Window.Activate();
        app.Workbench.RequestAttention(new(other, failedTab, "Claude hit an error", "") { Reason = "failed" });
        Until(() => Mark(other) == "failed" && Dot(app, other).IsVisible);
        var tip = ToolTip.GetTip(Dot(app, other)) as string ?? "";
        Require(tip.Contains("Claude Code") && tip.Contains("in “Terminal") && tip.EndsWith("hit an error"), "The workspace dot must name the failed agent and tab: " + tip);
        var row = Item(app, other);
        var rowBounds = new Rect(row.TranslatePoint(default, app.Window) ?? default, row.Bounds.Size);
        Rect Within(Control control) => new(control.TranslatePoint(default, app.Window) ?? default, control.Bounds.Size);
        Require(rowBounds.Contains(Within(Dot(app, other))), "The workspace dot must sit inside its row.");
        Require(!Within(Dot(app, other)).Intersects(Within(row.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceMenu"))), "The workspace dot must not overlap the actions menu.");
        var nameBox = row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "WorkspaceName");
        Require(!Within(Dot(app, other)).Intersects(Within(nameBox)), "The workspace dot must have its own column, clear of the name: dot " + Within(Dot(app, other)) + ", name " + Within(nameBox));
        var tabRow = app.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, failedTab));
        var tabDot = tabRow.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalFailedMark");
        Require(tabDot.IsVisible && (ToolTip.GetTip(tabDot) as string ?? "").Contains("Claude Code"), "The failed agent's tab row must carry a named dot.");
        Require(Within(tabRow).Contains(Within(tabDot)), "The tab dot must sit inside its tab row.");
        WorkspaceTabsE2E.Switch(app, other);
        app.Window.Activate();
        Until(() => Mark(other) is null && !app.Workbench.Signals.Failed(SharpRail.UI.Terminal.TerminalLaunch.SessionFor(other, failedTab)));
        Console.WriteLine("PASS a failed agent is named on its workspace row and tab row, inside their rows; seeing the workspace clears both");
    }
}