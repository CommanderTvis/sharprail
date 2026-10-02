using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>SharpRail checks for the spec-aware Welcome cards.</summary>
internal static class SpecsPanelE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "specs-panel-git"));
        SetUpCard(Path.Combine(root, "specs-panel-setup"));
    }

    private static void SetUpCard(string directory)
    {
        using var app = OpenFresh(directory);
        Until(() => HasWelcome(app));
        var bare = IsolatedGit.Repository(Path.Combine(directory, "no-specs"));
        Directory.CreateDirectory(Path.Combine(bare, ".thinkrail", "context"));
        File.WriteAllText(Path.Combine(bare, ".thinkrail", "context", "task.md"), "---\nid: scratch\ntype: task-spec\ntitle: Scratch\n---\n");
        app.Window.FolderPicker = () => Task.FromResult<string?>(bare);
        var open = app.Find<Button>("WelcomeCta");
        app.Click(open);
        Until(() => open.ContextMenu!.IsOpen);
        app.Click(open.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open project")), freshGesture: false);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == bare && HasWelcome(app));
        Until(() => Buttons(app).Any(button => button.Name == "WelcomeCta" && button.IsEffectivelyVisible));
        Button[] Actions() => Buttons(app).Where(button => button.Name == "WelcomeAction").ToArray();
        var cta = app.Find<Button>("WelcomeCta");
        Require(Text(cta).Contains("Set up project", StringComparison.Ordinal) && cta.Background == Ui.PrimarySubtle && Actions().Length == 2 &&
            Text(Actions()[0]).Contains("Create workspace", StringComparison.Ordinal) && Text(Actions()[1]).Contains("Work in project folder", StringComparison.Ordinal) &&
            Actions().All(action => action.Background == Ui.Sidebar),
            "A project whose only spec is an ephemeral task spec leads with Set up project, then Create workspace and Work in project folder.");
        app.Click(cta);
        var dialog = Dialog(app, "NewWorkspaceDialog");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !app.Window.OwnedWindows.Any());

        File.WriteAllText(Path.Combine(bare, "SPEC.md"), "---\nid: goal\ntype: goal-and-requirements\ntitle: Goal\n---\n");
        Until(() => HasWelcome(app) && Text(app.Find<Button>("WelcomeCta")).Contains("Create workspace", StringComparison.Ordinal));
        Require(Actions().Length == 1 && !Text(app.Find<StackPanel>("Welcome")).Contains("Set up project", StringComparison.Ordinal),
            "Once the project has a durable spec, Welcome returns to Create workspace and Work in project folder.");
        File.Delete(Path.Combine(bare, "SPEC.md"));
        Until(() => HasWelcome(app) && Text(app.Find<Button>("WelcomeCta")).Contains("Set up project", StringComparison.Ordinal));
        Console.WriteLine("PASS SharpRail: Welcome leads with Set up project until the project has a durable spec, and follows the worktree");
    }

}