using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using SharpRail.Scintilla;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

using ThemeCatalog = SharpRail.UI.Rendering.Themes;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream editor.spec.ts, plus the Monaco cases of theme.spec.ts and line-width-settings.spec.ts
/// translated to the macOS Scintilla editor. Markdown source views stay SharpRail's read-only source view.
/// </summary>
internal static class EditorE2E
{
    private static readonly string LongLine = string.Join(' ', Enumerable.Range(1, 80).Select(index => $"segment-{index:00}"));

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "editor-git"));
        OpenFocusClose(root);
        Frontmatter(root);
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP upstream editor cases on Scintilla: the editor is macOS-only.");
            return;
        }
        PlainFile(root);
        Themes(root);
        FileWidth(root);
    }

    private static E2eWorkspace Workspace(string root, string name)
    {
        // Upstream's fixture files, committed so a created worktree contains them.
        var directory = IsolatedGit.Repository(Path.Combine(root, name),
            ("README.md", "# sample-project\n"), ("notes.txt", "plain-text-fixture\n"),
            ("SPEC.md", "---\nid: sample-root\ntype: goal-and-requirements\ntitle: Sample Project\n---\n\n## Goal\n\nA throwaway fixture project.\n"),
            ("LONG_LINE.txt", LongLine + "\n"));
        var app = new E2eWorkspace(directory, openFiles: false);
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        app.Click(app.Find<Button>("Tab_files"));
        return app;
    }

    private static string Text(Control root) => string.Join('\n', root.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text)
        .Concat(root.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(text => string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? []))));

    private static bool PreviewShown(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<MarkdownPreview>().Any(preview => preview.IsEffectivelyVisible);

    private static void OpenFocusClose(string root)
    {
        using var app = Workspace(root, "editor-open");
        app.Open("README.md", keep: true);
        Require(app.Tabs.Count(tab => tab.Path == "README.md") == 1, "Double-clicking README.md must open one center tab.");
        Until(() => Text(app.Find<MarkdownPreview>("MarkdownPreview")).Contains("sample-project", StringComparison.Ordinal));
        app.Click(app.Find<Button>("MarkdownSourceMode"));
        Until(() => !PreviewShown(app));
        Require(Text(app.Find<ScrollViewer>("MarkdownSource")).Contains("# sample-project", StringComparison.Ordinal), "Source mode must show the Markdown source.");
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Until(() => PreviewShown(app));

        app.Click(app.FileRow("README.md"), twice: true);
        Settle();
        Require(app.Tabs.Count(tab => tab.Kind != "terminal") == 1, "Re-opening README.md must focus its tab, not open another.");

        app.Click(app.Find<Control>("DockTab_markdown_README.md").GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
        Until(() => app.Tabs.All(tab => tab.Path != "README.md"));
        var ready = app.Find<StackPanel>("WorkspacePlaceholder");
        var text = Text(ready);
        Require(text.Contains("Workspace ready", StringComparison.OrdinalIgnoreCase) && text.Contains("scoped to this workspace", StringComparison.Ordinal),
            "Closing the last tab of a created workspace must show its ready placeholder.");
        Console.WriteLine("PASS upstream editor.spec.ts: opens a file in a center Monaco tab, focuses on re-open, and closes");
    }

    private static void Frontmatter(string root)
    {
        using var app = Workspace(root, "editor-frontmatter");
        app.Open("SPEC.md", keep: true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Until(() => Text(preview).Contains("Goal", StringComparison.Ordinal));
        // The fork shows frontmatter as a properties block whose values are form fields, outside the page's text;
        // here the read-only values are text, so the prose is checked without that block.
        var properties = preview.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "FrontmatterProperties");
        var prose = string.Join('\n', ((StackPanel)preview.Content!).Children.Where(block => block != properties).Select(Text));
        Require(!prose.Contains("goal-and-requirements", StringComparison.Ordinal) && !Text(preview).Contains("id: sample-root", StringComparison.Ordinal),
            "The rendered view must hide YAML frontmatter.");
        app.Click(app.Find<Button>("MarkdownSourceMode"));
        Until(() => !PreviewShown(app));
        Require(Text(app.Find<ScrollViewer>("MarkdownSource")).Contains("id: sample-root", StringComparison.Ordinal), "Source mode must show the frontmatter.");
        Console.WriteLine("PASS upstream editor.spec.ts (diverges): shows YAML frontmatter as a leading code block in the rendered view and in source");
    }

    private static ScintillaEditor OpenEditor(E2eWorkspace app, string path)
    {
        app.Open(path, keep: true);
        Until(() => app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Any(editor => editor.IsEffectivelyVisible && editor.Bounds.Width > 0));
        return app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Single(editor => editor.IsEffectivelyVisible);
    }

    private static void PlainFile(string root)
    {
        using var app = Workspace(root, "editor-plain");
        var editor = OpenEditor(app, "notes.txt");
        Require(app.Tabs.Any(tab => tab.Path == "notes.txt"), "notes.txt must open in a center tab.");
        Require(editor.Text.Contains("plain-text-fixture", StringComparison.Ordinal), "The editor must show the file contents.");
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name is "MarkdownSourceMode" or "MarkdownPreviewMode" && button.IsEffectivelyVisible)
            && !PreviewShown(app),
            "A non-Markdown file must open straight to the editor without a rendered-view toggle.");
        Console.WriteLine("PASS upstream editor.spec.ts: opens a non-markdown file straight to Monaco with no rendered-view toggle");
    }

    private static Color Pixel(Bitmap frame, Control control, Point point, double scaling)
    {
        var at = control.TranslatePoint(point, (Visual)TopLevel.GetTopLevel(control)!)!.Value * scaling;
        var pixel = new byte[4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4); }
        finally { pinned.Free(); }
        return frame.Format == Avalonia.Platform.PixelFormats.Bgra8888
            ? Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0])
            : Color.FromArgb(pixel[3], pixel[0], pixel[1], pixel[2]);
    }

    private static void Themes(string root)
    {
        using var app = Workspace(root, "editor-themes");
        var original = app.Window.Preferences.Theme;
        var mount = ThemeCatalog.All.First(theme => theme.IsLight).Id;
        Require(ThemeCatalog.All.Any(theme => theme.IsHighContrast), "The catalogue must include a high-contrast theme.");
        var settings = OpenSettings(app);
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + mount));
        CloseSettings(settings);
        var editor = OpenEditor(app, "notes.txt");
        Require(editor.Text.Contains("plain-text-fixture", StringComparison.Ordinal), "The editor must show the file contents.");
        foreach (var theme in ThemeCatalog.All.Select(theme => theme.Id).Append(original))
        {
            settings = OpenSettings(app);
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Theme_" + theme));
            CloseSettings(settings);
            Settle(300);
            using var frame = app.Window.CaptureRenderedFrame()!;
            var background = Pixel(frame, editor, new Point(editor.Bounds.Width - 24, editor.Bounds.Height - 8), app.Window.RenderScaling);
            Require(background == Ui.Surface.Color, $"The mounted editor must re-theme to {theme} (painted {background}, expected {Ui.Surface.Color}).");
            Require(editor.Text.Contains("plain-text-fixture", StringComparison.Ordinal), "Re-theming must keep the editor contents.");
            if (Ui.Theme.IsHighContrast)
                Require(Ui.Theme.Colors["selectionForeground"] is not null && Ui.Theme.Colors["editorSelectionForeground"] is not null,
                    $"{theme} must override the selected-text foreground for browser and editor selections.");
        }
        Console.WriteLine("PASS upstream theme.spec.ts: Monaco opens files and re-themes under every discovered manifest");
    }

    private static void FileWidth(string root)
    {
        using var app = Workspace(root, "editor-width");
        var editor = OpenEditor(app, "LONG_LINE.txt");
        Until(() => editor.WrapCount(0) > 1);
        var wrapped = editor.WrapCount(0);
        var settings = OpenSettings(app);
        settings.ShowSection("Line width");
        Until(() => settings.GetLogicalDescendants().OfType<TextBox>().Any(input => input.Name == "FileLineWidthInput"));
        LineWidthE2E.SaveWidth(app, settings, "File", 40);
        CloseSettings(settings);
        Until(() => editor.WrapCount(0) > wrapped);
        Require(app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Contains(editor), "Changing the file width must update the already-mounted editor.");
        Settle(300);
        Require(!app.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Any(bar => bar.Name == "EditorHorizontalScroll" && bar.IsVisible), "Wrapped source must not offer horizontal scrolling.");
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: the file width wraps source and updates an already-mounted editor");
    }

    private static SettingsWindow OpenSettings(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        return app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
    }

    private static void CloseSettings(SettingsWindow settings)
    {
        settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose").Focus();
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !settings.IsVisible);
    }
}