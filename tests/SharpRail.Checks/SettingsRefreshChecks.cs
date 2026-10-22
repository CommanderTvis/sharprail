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
                if (section == "Projects") continue;
                // The window's own project open can land after it mounts; only the Projects page lists it.
                var other = Path.Combine(root, "settings-refresh-" + section);
                Directory.CreateDirectory(other);
                var opened = window.Workbench.State.ChangeAsync(HostStateChange.OpenProject(other));
                Until(() => opened.IsCompleted && window.Workbench.State.Current.Projects.Contains(other));
                opened.GetAwaiter().GetResult();
                Require(ReferenceEquals(body.Content, page), "A recent project broadcast rebuilt the " + section + " Settings page.");
            }
            settings.ShowSection("Appearance");
            var appearance = body.Content;
            var update = window.Workbench.State.ChangeAsync(HostStateChange.Setting("file-type-icons", "false"));
            Until(() => update.IsCompleted && !window.Workbench.State.Current.Settings.FileTypeIconsEnabled);
            update.GetAwaiter().GetResult();
            Require(!ReferenceEquals(body.Content, appearance), "Relevant appearance settings must still refresh the page.");
            // The Linux window frame choice is a host setting shown on Linux only; elsewhere it stays hidden and inert.
            StackPanel Section() => settings.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "WindowDecorationsSection");
            Require(Section().IsVisible == LinuxWindowChrome.Available, "The window decorations row must show on Linux only.");
            var decorations = window.Workbench.State.ChangeAsync(HostStateChange.Setting("window-decorations", "client"));
            Until(() => decorations.IsCompleted && window.Workbench.State.Current.Settings.WindowDecorations == "client");
            decorations.GetAwaiter().GetResult();
            Require(Section().IsVisible == LinuxWindowChrome.Available &&
                settings.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "WindowDecorationChoices").Children.Count == (LinuxWindowChrome.Available ? 3 : 0),
                "The window decorations choices follow the host's broadcast on Linux and are absent elsewhere.");
            window.Workbench.State.ChangeAsync(HostStateChange.Setting("window-decorations", "auto")).GetAwaiter().GetResult();
            Console.WriteLine("PASS Settings retain pages through terminal broadcasts and refresh relevant changes");
        }
        finally { settings.Close(); window.Close(); }
    }
}