using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Core;
using SharpRail.Plugins.Api;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Projects groups a workspace under the terminal that created it when the preference is on, and only then.</summary>
internal static class RailGroupingE2E
{
    internal static void Run(string root)
    {
        using var app = OpenFixtureProject(Path.Combine(root, "rail-grouping"));
        var project = app.Window.ProjectRoot;
        var creatorWorkspace = CreateWorkspaceViaDialog(app);
        app.Window.Layout.NewTerminal(app.Center);
        var terminal = app.Window.Layout.Tabs(app.Center).Last(tab => tab.Kind == "terminal").Id;
        app.Window.Layout.Open(new("plugin:claude:chat-1", "Chat", "plugin", "claude:chat-1"), true, app.Center);
        var child = CreateWorkspaceViaDialog(app);
        var chatChild = CreateWorkspaceViaDialog(app);
        var orphan = CreateWorkspaceViaDialog(app);
        app.State!.RecordWorkspaceCreator(child, new TerminalRef(creatorWorkspace, terminal));
        app.State.RecordWorkspaceCreator(orphan, new TerminalRef(creatorWorkspace, "closed-terminal"));
        app.State.RecordWorkspaceCreator(chatChild, new TerminalRef(creatorWorkspace, "chat-1"));
        Until(() => app.State.Current.WorkspaceCreators.Count == 3);

        string[] Order() => Controls(app).OfType<Grid>().Where(item => item.Name == "WorkspaceItem" && item.IsEffectivelyVisible).Select(item => (string)item.Tag!).ToArray();
        Require(Order().SequenceEqual([project, orphan, chatChild, child, creatorWorkspace]), "Off, the rail stays flat, newest first: " + string.Join(", ", Order()));
        Require(!Controls(app).OfType<Border>().Any(guide => guide.Name == "WorkspaceNestGuide" && guide.IsEffectivelyVisible), "Off, no row shows a nesting guide.");
        Console.WriteLine("PASS rail grouping: off keeps the flat list");

        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OpenSettingsViews.Any(window => window.IsVisible));
        var settings = app.Window.OpenSettingsViews.Single(window => window.IsVisible);
        app.Click(Named<Button>(settings, "Settings_Projects"));
        var toggle = Named<CheckBox>(settings, "GroupWorkspacesUnderCreator");
        Require(toggle.IsChecked == false, "Grouping is off by default.");
        toggle.IsChecked = true;
        settings.Close(); Until(() => !settings.IsVisible);
        Require(app.Window.Preferences.GroupWorkspacesUnderCreator, "The switch must change the app preference.");
        Until(() => Order().SequenceEqual([project, orphan, creatorWorkspace, chatChild, child]));
        Require(Item(app, child).Margin.Left > Item(app, creatorWorkspace).Margin.Left && Item(app, orphan).Margin.Left == Item(app, creatorWorkspace).Margin.Left &&
            Named<Border>(Item(app, child), "WorkspaceNestGuide").IsEffectivelyVisible && !Named<Border>(Item(app, orphan), "WorkspaceNestGuide").IsVisible,
            "On, the created workspace sits indented under its creator's workspace; one whose creator tab is gone stays flat.");
        Require(Item(app, chatChild).Margin.Left > Item(app, creatorWorkspace).Margin.Left && Named<Border>(Item(app, chatChild), "WorkspaceNestGuide").IsEffectivelyVisible,
            "A workspace made by a chat tab nests under its creator too.");
        Console.WriteLine("PASS rail grouping: on nests under the creator and falls back to flat when the creator is gone");

        var reloaded = new ProfileStore(Path.Combine(root, "rail-grouping", "sample-project") + "-profile");
        Require(reloaded.Data.Preferences.GroupWorkspacesUnderCreator, "The preference must be stored and restored.");
        Require(new HostStateStore(app.State.DirectoryPath).Current.WorkspaceCreators.Any(entry => entry.Workspace == child && entry.Creator.TabKey == terminal),
            "The host must restore the recorded creator after a restart.");
        Console.WriteLine("PASS rail grouping: the preference and the creators are stored and restored");
    }
}