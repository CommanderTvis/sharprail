using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

using Tereditor.Checks.E2E;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks;

/// <summary>The Playground's getting-started card: shown in the Default workspace and on Welcome only when the workbench is the browser's tab host.</summary>
internal static class PlaygroundGuideChecks
{
    internal static void Run(string root)
    {
        Playground(Path.Combine(root, "playground"));
        Hosted(Path.Combine(root, "hosted"));
    }

    private static E2eWorkspace Open(string directory, bool playground)
    {
        var project = Path.Combine(directory, "sample-project");
        IsolatedGit.Repository(project);
        var app = new E2eWorkspace(project, openFiles: false, playground: playground);
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "fixture");
        Until(() => app.Window.WorkspaceMounted && app.Find<TextBlock>("BranchLabel").Text == "main");
        return app;
    }

    private static bool HasGuide(E2eWorkspace app) => Controls(app).Any(control => control.Name == "PlaygroundGuide" && control.IsVisible);

    private static void Playground(string directory)
    {
        using var app = Open(directory, playground: true);
        Until(() => HasGuide(app));
        var texts = Controls(app).OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
        Require(texts.Any(text => text.Contains("Everything happens in this browser tab", StringComparison.Ordinal)), "The Playground's Default workspace explains that everything happens in the tab.");
        foreach (var part in new[] { "No server and no account", "saved in this browser's storage", "Only Pi runs in a tab", "no node or npm" })
            Require(texts.Any(text => text.Contains(part, StringComparison.Ordinal)), "The explanation says: " + part);
        foreach (var step in new[] { "1.", "2.", "3." }) Require(texts.Contains(step), "The card lists step " + step);
        Require(!texts.Any(text => text.Contains("CORS", StringComparison.Ordinal)), "The card leaves the CORS proxy to developers.");
        var placeholder = Named<StackPanel>(app.Window, "WorkspacePlaceholder");
        var items = placeholder.Children.ToList();
        Require(items.FindIndex(item => item.Name == "PlaygroundGuide") > items.FindIndex(item => item is Button), "The guide sits under the workspace and its Open file button.");
        var clone = Named<Button>(app.Window, "PlaygroundGuideClone");
        Require(Named<Button>(app.Window, "PlaygroundGuideClaudeCredits") is not null && Named<Button>(app.Window, "PlaygroundGuideGoogleCredits") is not null, "Both credit links are there.");
        app.Click(clone);
        var dialog = Dialog(app, "CloneProjectDialog");
        dialog.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        app.UntilDialogsGone();
        Console.WriteLine("PASS playground guide: the Default workspace explains the tab and its three steps; the clone link opens the clone dialog");

        GoProjectHome(app);
        Until(() => HasGuide(app));
        app.Click(Named<Button>(app.Window, "PlaygroundGuideProviders"));
        Until(() => app.Window.OpenSettingsViews.Any());
        Console.WriteLine("PASS playground guide: Welcome shows it too and its providers link opens Settings");
    }

    private static void Hosted(string directory)
    {
        using var app = Open(directory, playground: false);
        Require(!HasGuide(app), "A hosted or desktop Default workspace shows no Playground card.");
        GoProjectHome(app);
        Require(!HasGuide(app), "A hosted or desktop Welcome shows no Playground card.");
        Console.WriteLine("PASS playground guide: absent outside the Playground");
    }
}