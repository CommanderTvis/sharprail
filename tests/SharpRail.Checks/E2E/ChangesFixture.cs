using System.Diagnostics;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Scintilla;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ChangesFixture
{
    internal static string? Source => Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE") is { Length: > 0 } source ? source : null;

    internal static (E2eWorkspace App, string Worktree) Open(string root, string name, string source)
    {
        var app = new E2eWorkspace(WorkspaceTabsE2E.Repository(root, name, source));
        return (app, WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1"));
    }

    /// <summary>
    /// Commits the workspace fixture's README and root SPEC.md, like upstream's sample project, on the base
    /// branch before creating the worktree, so branch-scope changes start clean.
    /// </summary>
    internal static (E2eWorkspace App, string Worktree) OpenSample(string root, string name, string source, params (string Path, string Text)[] files)
    {
        // Upstream's sample project is a local repository without a remote; without origin the
        // new workspace bases on the local branch that holds the committed sample. Remove it
        // before the window opens so no branch catalog is read with the remote still present.
        var repository = WorkspaceTabsE2E.Repository(root, name, source);
        Git(repository, "remote", "remove", "origin");
        var app = new E2eWorkspace(repository);
        var refreshes = app.Window.WatchRefreshes;
        foreach (var (path, text) in files) File.WriteAllText(Path.Combine(app.Root, path), text);
        Git(app.Root, ["add", "README.md", "SPEC.md", .. files.Select(file => file.Path)]);
        Commit(app.Root, "sample project");
        Until(() => app.Window.WatchRefreshes > refreshes);
        Settle(500);
        return (app, WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1"));
    }

    internal static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Require(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Trim();
    }

    internal static void Commit(string directory, string message) =>
        Git(directory, "-c", "user.email=e2e@thinkrail.test", "-c", "user.name=ThinkRail E2E", "commit", "-m", message);

    internal static string SeedCommitAndDirtyEdit(string worktree)
    {
        File.WriteAllText(Path.Combine(worktree, "committed.txt"), "committed by e2e\n");
        Git(worktree, "add", "committed.txt");
        Commit(worktree, "e2e scope commit");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\ndirty edit by e2e\n");
        return worktree;
    }

    internal static void ShowChanges(E2eWorkspace app) => app.Click(app.Find<Button>("Tab_changes"));

    internal static Border[] Frames(E2eWorkspace app) => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Border>()
        .Where(border => border.Classes.Contains("change-row")).ToArray();

    internal static Button RowButton(Border frame) => frame.GetLogicalDescendants().OfType<Button>().First();

    internal static string RowPath(Border frame) => AutomationProperties.GetName(RowButton(frame))!;

    internal static string[] Paths(E2eWorkspace app) => Frames(app).Select(RowPath).Order(StringComparer.Ordinal).ToArray();

    internal static Button Row(E2eWorkspace app, string path) => RowButton(Frames(app).Single(frame => RowPath(frame) == path));

    internal static void ClickRow(E2eWorkspace app, string path, bool twice = false)
    {
        Until(() => Paths(app).Contains(path));
        app.Window.MouseMove(new Point(app.Window.Bounds.Width - 2, app.Window.Bounds.Height - 2));
        Settle(550);
        app.Click(Row(app, path), twice, freshGesture: false);
    }

    internal static void UntilRows(E2eWorkspace app, params string[] paths) =>
        Until(() => Paths(app).SequenceEqual(paths.Order(StringComparer.Ordinal)));

    internal static string Text(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    internal static string ScopeLabel(E2eWorkspace app) => Text(app.Find<Button>("ChangesScope"));

    internal static bool Clean(E2eWorkspace app) => Text(app.Find<Control>("ChangesPanel")).Contains("Working tree clean", StringComparison.Ordinal);

    internal static IEnumerable<MenuItem> MenuItems(IEnumerable<object?> items) =>
        items.OfType<MenuItem>().SelectMany(item => new[] { item }.Concat(MenuItems(item.Items)));

    internal static bool HasMenuItem(E2eWorkspace app, string dropdown, Func<MenuItem, bool> match) =>
        MenuItems(app.Find<Button>(dropdown).ContextMenu!.Items).Any(match);

    /// <summary>Opens a toolbar menu; the scope menu reads its commit rows only then.</summary>
    internal static void OpenMenu(E2eWorkspace app, string dropdown)
    {
        app.Click(app.Find<Button>(dropdown));
        Until(() => app.Find<Button>(dropdown).ContextMenu!.IsOpen);
    }

    internal static void Pick(E2eWorkspace app, string dropdown, Func<MenuItem, bool> match)
    {
        OpenMenu(app, dropdown);
        Until(() => HasMenuItem(app, dropdown, match));
        // The scope menu re-reads its rows on open and replaces the ones it kept; let that land before a row is held.
        Settle();
        void Select(IEnumerable<object?> items)
        {
            var item = items.OfType<MenuItem>().First(item => match(item) || MenuItems(item.Items).Any(match));
            app.Click(item, freshGesture: false);
            if (match(item)) return;
            Until(() => item.IsSubMenuOpen);
            Select(item.Items);
        }
        Select(app.Find<Button>(dropdown).ContextMenu!.Items);
        Until(() => !app.Find<Button>(dropdown).ContextMenu!.IsOpen);
    }

    internal static Func<MenuItem, bool> Header(string title) => item => Equals(item.Header, title);

    internal static Func<MenuItem, bool> CommitItem(string subject) =>
        item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true && Equals(ToolTip.GetTip(item), subject);

    internal static void PickScope(E2eWorkspace app, string title) => Pick(app, "ChangesScope", Header(title));

    internal static void PickTarget(E2eWorkspace app, string branch) => Pick(app, "ChangesBranch", item => Equals(item.Tag, branch));

    internal static IEnumerable<DockTab> DiffTabs(E2eWorkspace app) =>
        app.Window.Layout.State.Workspaces[app.Window.WorkspaceRoot].Documents.Values.SelectMany(tabs => tabs).Where(tab => tab.Kind == "diff");

    internal static Control? Pane(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Control>().SingleOrDefault(control => control.Name == "DiffPane" && control.IsEffectivelyVisible);

    internal static string DiffText(E2eWorkspace app) => Pane(app) is { } pane
        // Off macOS the diff is one plain code block instead of Scintilla sides; its text is in its runs.
        ? string.Join("\n", pane.GetLogicalDescendants().OfType<ScintillaEditor>().Select(editor => editor.Text)
            .Concat(OperatingSystem.IsMacOS() ? [] : pane.GetLogicalDescendants().OfType<SelectableTextBlock>()
                .Select(block => block.Text ?? block.Inlines?.Text).OfType<string>()))
        : "";

    internal static int Count(string text, string needle) => Regex.Matches(Regex.Replace(text, @"\s+", " "), Regex.Escape(needle)).Count;

    /// <summary>Waits on the source diff text; a Markdown diff opens rendered, so this switches it to Source first, as upstream's review.spec.ts does.</summary>
    internal static void UntilDiff(E2eWorkspace app, Func<string, bool> condition) => Until(() =>
    {
        if (Pane(app)?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(toggle => toggle.Name == "DiffView_code" && toggle.IsEffectivelyVisible) is { IsChecked: false } source)
            app.Click(source);
        return condition(DiffText(app));
    });

    internal static double Right(Control control, Visual to) => control.TranslatePoint(new Point(control.Bounds.Width, 0), to)!.Value.X;

    internal static bool Clipped(TextBlock text)
    {
        var natural = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle };
        natural.Measure(Size.Infinity);
        return natural.DesiredSize.Width - text.Bounds.Width > 1;
    }

    internal static bool Transparent(IBrush? brush) => brush is null || brush is ISolidColorBrush { Color.A: 0 };

    internal static void CloseMenu(Button dropdown)
    {
        TopLevel.GetTopLevel(dropdown.ContextMenu!.Items.OfType<MenuItem>().First())!
            .KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !dropdown.ContextMenu.IsOpen);
    }
}