using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class SettingsGitHubE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "settings-github"), openFiles: false);
        Settle(800);
        var emptyPath = Path.Combine(root, "settings-github-empty-path");
        Directory.CreateDirectory(emptyPath);
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", emptyPath);
        try
        {
            app.Click(app.Find<Button>("SettingsButton"));
            Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
            var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_GitHub"));
            Require(settings.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Local GitHub"),
                "Settings must show the Local GitHub block.");
            var status = settings.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "GitHubStatus");
            var text = settings.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "GitHubStatusText");
            var refresh = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "GitHubRefresh");
            Until(() => refresh.IsEnabled);
            Require(Equals(status.Tag, false) && text.Text == "Not connected", "A missing gh must read as not connected.");
            app.Click(refresh);
            Until(() => refresh.IsEnabled);
            Require(Equals(status.Tag, false) && text.Text == "Not connected", "Refreshing without gh must stay not connected.");
            settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Settle();
            Require(!settings.IsVisible, "Escape must close Settings.");
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
        Console.WriteLine("PASS upstream settings.spec.ts: settings shows the Local GitHub status block and degrades gh gracefully");
        PreferencesChord(app);
    }

    private static void PreferencesChord(E2eWorkspace app)
    {
        SettingsWindow[] Open() => app.Window.OwnedWindows.OfType<SettingsWindow>().Where(window => window.IsVisible).ToArray();
        void Press(RawInputModifiers modifiers)
        {
            app.Window.Focus();
            app.Window.KeyPress(Key.OemComma, modifiers, PhysicalKey.Comma, ",");
            app.Window.KeyRelease(Key.OemComma, modifiers, PhysicalKey.Comma, ",");
            Settle();
        }
        Press(RawInputModifiers.Control);
        Require(Open().Length == 0, "Ctrl+, must stay unbound on every platform.");
        Press(RawInputModifiers.Meta);
        if (!OperatingSystem.IsMacOS())
        {
            Require(Open().Length == 0, "Cmd+, must not open Settings off macOS.");
            Console.WriteLine("PASS upstream settings.spec.ts: macOS opens settings with its own Preferences chord, and other platforms do not");
            return;
        }
        Until(() => Open().Length == 1);
        var settings = Open()[0];
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Projects"));
        settings.Close(); Until(() => !settings.IsVisible);
        Press(RawInputModifiers.Meta | RawInputModifiers.Shift);
        Require(Open().Length == 0, "No other modifier may ride along with the Preferences chord.");
        Press(RawInputModifiers.Meta);
        Until(() => Open().Length == 1);
        settings = Open()[0];
        Require(settings.Section == "Projects" && settings.GetLogicalDescendants().OfType<StackPanel>().Any(list => list.Name == "RecentProjects"),
            "Settings must reopen on the section it was left on.");
        settings.Close(); Until(() => !settings.IsVisible);
        Console.WriteLine("PASS upstream settings.spec.ts: macOS opens settings with its own Preferences chord, and other platforms do not");
    }
}