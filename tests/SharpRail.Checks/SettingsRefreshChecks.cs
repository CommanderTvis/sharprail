using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class SettingsRefreshChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "settings-refresh");
        Directory.CreateDirectory(directory);
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), E2eTerminals.Plain);
        window.Show();
        Until(() => window.WorkspaceMounted);
        var settings = new SettingsWindow(window, () => { });
        settings.Show(window);
        try
        {
            var body = settings.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "SettingsBody");
            foreach (var section in new[] { "Appearance", "Layout", "Projects" })
            {
                settings.ShowSection(section);
                var page = body.Content;
                var revision = window.Workbench.State.Current.Revision;
                var change = window.Workbench.State.ChangeAsync(HostStateChange.TitleTerminal(new(directory, "terminal:refresh"), section));
                Until(() => change.IsCompleted && window.Workbench.State.Current.Revision > revision);
                change.GetAwaiter().GetResult();
                Require(ReferenceEquals(body.Content, page), "An unrelated terminal title broadcast rebuilt the " + section + " Settings page.");
                Require(settings.IsVisible, "A host broadcast dismissed Settings.");
            }
            settings.ShowSection("Appearance");
            var appearance = body.Content;
            var update = window.Workbench.State.ChangeAsync(HostStateChange.Setting("file-type-icons", "false"));
            Until(() => update.IsCompleted && !window.Workbench.State.Current.Settings.FileTypeIconsEnabled);
            update.GetAwaiter().GetResult();
            Require(!ReferenceEquals(body.Content, appearance), "Relevant appearance settings must still refresh the page.");
            Console.WriteLine("PASS Settings retain pages through terminal broadcasts and refresh relevant changes");
        }
        finally { settings.Close(); window.Close(); }
    }
}