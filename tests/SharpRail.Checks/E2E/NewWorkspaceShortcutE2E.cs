using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class NewWorkspaceShortcutE2E
{
    private const RawInputModifiers Mod = RawInputModifiers.Meta;

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "new-workspace-shortcut-git"));
        FromWelcome(Path.Combine(root, "shortcut-welcome"));
        Alias(Path.Combine(root, "shortcut-alias"));
        InsideWorkspace(Path.Combine(root, "shortcut-workspace"));
    }

    private static int Dialogs(E2eWorkspace app) => app.Window.OwnedWindows.Count(window => Equals(window.Tag, "NewWorkspaceDialog") && window.IsVisible);

    private static void ExpectDialog(E2eWorkspace app)
    {
        var dialog = Dialog(app, "NewWorkspaceDialog");
        Require(Dialogs(app) == 1 && Named<TextBlock>(dialog, "DialogHeading").Text == "Start work",
            "The shortcut must open one Start work dialog.");
        var probe = new Avalonia.Controls.Primitives.ToggleButton { Content = "probe", IsChecked = true };
        var fields = Named<StackPanel>(dialog, "DialogFields");
        fields.Children.Add(probe);
        dialog.UpdateLayout();
        var fill = (probe.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .First(presenter => presenter.Name == "PART_ContentPresenter").Background as Avalonia.Media.ISolidColorBrush)?.Color;
        fields.Children.Remove(probe);
        Require(fill == SharpRail.Plugins.UI.Kit.Ui.PrimaryMuted.Color, $"A checked toggle has a fill distinct from hover ({fill}).");
    }

    private static void Escape(E2eWorkspace app)
    {
        Press(app.Window.OwnedWindows.Single(window => Equals(window.Tag, "NewWorkspaceDialog")), Avalonia.Input.Key.Escape);
        Until(() => Dialogs(app) == 0);
    }

    private static void FromWelcome(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var add = app.Find<Button>("AddWorkspace");
        Require(AutomationProperties.GetName(add) is { } label && System.Text.RegularExpressions.Regex.IsMatch(label, @"^Start work \(.*N.*\)$"),
            "The add-workspace control must advertise its shortcut.");
        app.Click(app.Find<TextBlock>("WelcomeTitle"));
        Press(app.Window, Avalonia.Input.Key.N, Mod);
        ExpectDialog(app);
        Press(app.Window, Avalonia.Input.Key.N, Mod);
        Settle(200);
        ExpectDialog(app);
        Escape(app);
        Console.WriteLine("PASS upstream new-workspace-shortcut.spec.ts: Mod+N opens the Create workspace dialog for the selected project from the Welcome screen, and Escape closes it");
    }

    private static void Alias(string directory)
    {
        using var app = OpenFixtureProject(directory);
        app.Click(app.Find<TextBlock>("WelcomeTitle"));
        Press(app.Window, Avalonia.Input.Key.N, Mod | RawInputModifiers.Shift);
        Settle(300);
        Require(Dialogs(app) == 0, "Mod+Shift+N must not open the Create workspace dialog.");
        Press(app.Window, Avalonia.Input.Key.N, Mod | RawInputModifiers.Alt);
        ExpectDialog(app);
        Escape(app);
        Console.WriteLine("PASS upstream new-workspace-shortcut.spec.ts: The Mod+Alt+N alias opens the same dialog and Mod+Shift+N does not");
    }

    private static void InsideWorkspace(string directory)
    {
        using var app = OpenFixtureProject(directory);
        CreateWorkspaceViaDialog(app);
        Press(app.Window, Avalonia.Input.Key.Escape);
        Press(app.Window, Avalonia.Input.Key.N, Mod);
        ExpectDialog(app);
        Escape(app);
        Console.WriteLine("PASS upstream new-workspace-shortcut.spec.ts: Mod+N works inside an active workspace");
    }
}