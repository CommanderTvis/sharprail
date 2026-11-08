using System.Text.Json;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Plugins.Review;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>The Review tool window is a plugin: a layout that still names the old core tool reads as the plugin's, and switching the plugin off removes the tool without touching the layout.</summary>
internal static class ReviewPluginChecks
{
    internal static void Migration()
    {
        var current = DockState.Preset("balanced");
        var legacy = JsonSerializer.Deserialize<DockState>(JsonSerializer.Serialize(current).Replace(DockState.ReviewTool, "review"))!;
        legacy.ToolRestorePositions["review"] = 1;
        Require(!LayoutSession.IsValid(legacy), "A layout naming the legacy core Review tool is not valid before migration.");
        legacy.MigrateLegacyTools();
        Require(LayoutSession.IsValid(legacy) && legacy.Groups.Any(group => group.Tools.Any(tab => tab.Id == DockState.ReviewTool)) && legacy.ToolRestorePositions.ContainsKey(DockState.ReviewTool),
            "The legacy review tool id migrates to the Review plugin's in tools and restore positions.");
        Console.WriteLine("PASS review plugin: a persisted review tab reads as plugin:review:review");
    }

    internal static void RunUi(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "review-plugin"));
        var profile = project + "-profile";
        Directory.CreateDirectory(profile);
        // A saved layout that names the Review tool, and a profile that has the plugin off.
        var store = new HostStateStore(profile);
        store.ChangeAsync([HostStateChange.PluginEnabled(ReviewPlugin.Id, false)]).AsTask().GetAwaiter().GetResult();
        using var app = new E2eWorkspace(project, openFiles: false, profileRoot: profile);
        bool Shown() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "Tab_plugin_review_review" or "ReviewPanel");
        var group = app.Window.Layout.State.Groups.Single(item => item.Tools.Any(tool => tool.Id == DockState.ReviewTool));
        Until(() => !app.Workbench.PluginRegistry.Active.Contains(ReviewPlugin.Id));
        Require(!Shown() && app.Window.Layout.Tabs(group.Id).All(tab => tab.Id != DockState.ReviewTool) && app.Window.Layout.Tools.All(tool => tool.Id != DockState.ReviewTool),
            "With the Review plugin off there is no Review tab, pane or catalog entry, and the layout still restores.");

        _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(ReviewPlugin.Id, true));
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "Tab_plugin_review_review"));
        app.ShowTool(DockState.ReviewTool);
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ReviewPanel"));
        _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(ReviewPlugin.Id, false));
        Until(() => !Shown());
        Console.WriteLine("PASS review plugin: switching it off removes the tool and its pane, and a layout naming it keeps its slot");
    }
}