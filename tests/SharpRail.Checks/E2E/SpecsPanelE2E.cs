using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>SharpRail checks for the spec-aware Welcome cards and the Specs panel's load-failure hint.</summary>
internal static class SpecsPanelE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "specs-panel-git"));
        SetUpCard(Path.Combine(root, "specs-panel-setup"));
        LoadFailure(Path.Combine(root, "specs-panel-failure"));
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

    private static void LoadFailure(string directory)
    {
        using var app = new E2eWorkspace(Path.Combine(directory, "sample-project"), openFiles: false);
        app.Click(app.Find<Button>("Tab_specs"));
        var tree = app.Find<TreeView>("SpecsTree");
        bool Has(string title) => tree.GetLogicalDescendants().OfType<TreeViewItem>().Any(item => AutomationProperties.GetName(item) == title);
        Until(() => Has("Sample Project"));
        var failure = app.Find<StackPanel>("SpecsFailure");
        Require(!failure.IsVisible, "A loaded Specs panel shows no failure hint.");

        app.Host.SpecsFailure = new IOException("the spec index is unreadable");
        File.WriteAllText(Path.Combine(app.Root, "ADDED.md"), "---\nid: added\ntype: module-design\ntitle: Added later\nparent: sample-root\n---\n");
        Until(() => failure.IsVisible);
        Require(app.Find<TextBlock>("SpecsError").Text == "Specs could not be loaded: the spec index is unreadable" && Has("Sample Project") && !Has("Added later") &&
            ReferenceEquals(app.Find<TreeView>("SpecsTree"), tree) && !app.Find<TextBlock>("WorkspaceError").IsVisible,
            "A failed spec read keeps the previous tree and explains itself inline, not as a window error.");
        app.Click(app.Find<Button>("SpecsRetry"));
        Settle();
        Require(failure.IsVisible && Has("Sample Project"), "A Retry that fails again keeps the hint and the tree.");

        app.Host.SpecsFailure = null;
        app.Click(app.Find<Button>("SpecsRetry"));
        Until(() => !failure.IsVisible && Has("Added later") && Has("Sample Project"));
        Console.WriteLine("PASS SharpRail: a failed Specs read keeps the previous tree behind an inline hint, and Retry reloads it");
    }
}