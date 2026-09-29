using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LineWidthE2E
{
    private static string TextOf(SelectableTextBlock block) => string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    internal static void Run(string root)
    {
        Drafts(root);
        using var git = new IsolatedGit(Path.Combine(root, "line-width-git"));
        var longLine = string.Join(' ', Enumerable.Range(1, 80).Select(index => $"segment-{index:00}"));
        var directory = IsolatedGit.Repository(Path.Combine(root, "line-width-diff"), ("LONG_LINE.txt", longLine));
        using var app = new E2eWorkspace(directory, openFiles: false);
        File.WriteAllText(Path.Combine(directory, "LONG_LINE.txt"), "changed " + longLine);
        var refresh = app.Window.RefreshAsync();
        Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        app.Click(app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        Until(() => app.Tabs.Count(tab => tab.Kind == "diff") == 1);
        Until(() => app.Window.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Any(block => block.Name == "DiffNewText" && TextOf(block).Contains("changed segment-01", StringComparison.Ordinal)));
        Settle(300);
        foreach (var name in new[] { "DiffOldText", "DiffNewText" })
        {
            var block = app.Find<SelectableTextBlock>(name);
            var scroll = block.GetLogicalAncestors().OfType<ScrollViewer>().First();
            var logicalLines = TextOf(block).TrimEnd('\n').Split('\n').Length;
            var rendered = block.TextLayout.TextLines.Count;
            Require(rendered > logicalLines, $"The {name} long line must wrap ({rendered} rendered vs {logicalLines} logical lines).");
            Require(block.Bounds.Width <= LineWidths.File(app.Window.Preferences) + 1 && scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"The {name} side must stay within the default file width without horizontal scrolling.");
        }
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: the default file width wraps both sides of a long-line diff");
    }

    // Upstream's chat measure is replaced by the Markdown width, since AI chat is a non-goal.
    private static void Drafts(string root)
    {
        var directory = Path.Combine(root, "line-width-drafts");
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            var settings = OpenLineWidth(app);
            var fileInput = Named<TextBox>(settings, "FileLineWidthInput");
            var fileSave = Named<Button>(settings, "FileLineWidthSave");
            var markdownInput = Named<TextBox>(settings, "MarkdownLineWidthInput");
            Require(fileInput.Text == "120" && markdownInput.Text == "78", "Line widths must start at the reference defaults.");
            Require(Named<CheckBox>(settings, "FileLineWidthBounded").IsChecked == true &&
                Named<CheckBox>(settings, "MarkdownLineWidthBounded").IsChecked == true, "Both widths must start bounded.");

            Type(app, settings, fileInput, "90");
            Require(fileSave.IsEnabled, "A valid changed draft must enable Save.");
            settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            settings.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Until(() => fileInput.Text == "120");
            Require(settings.IsVisible && !fileSave.IsEnabled, "Escape must revert the draft without closing Settings.");

            Type(app, settings, fileInput, "39");
            var error = Named<TextBlock>(settings, "FileLineWidthError");
            Require(fileInput.Classes.Contains("invalid") && error.IsEffectivelyVisible && error.Text == "Enter a whole number from 40 to 240.",
                "An out-of-range draft must be marked invalid with the reference message.");
            Require(!fileSave.IsEnabled, "An invalid draft must disable Save.");

            Type(app, settings, fileInput, "80");
            Require(fileSave.IsEnabled && !error.IsVisible, "A valid draft must clear the error and enable Save.");
            settings.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            settings.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Until(() => !fileSave.IsEnabled && app.Window.Preferences.FileLineWidth == 80);
            SaveWidth(app, settings, "Markdown", 60);

            app.Click(Named<CheckBox>(settings, "MarkdownLineWidthBounded"));
            Until(() => !app.Window.Preferences.MarkdownLineWidthBounded);
            Require(app.Window.Preferences.FileLineWidthBounded, "Toggling one width's limit must leave the other bounded.");
            Close(settings);
        }
        var saved = new ProfileStore(directory + "-profile").Data.Preferences;
        Require(saved is { FileLineWidth: 80, MarkdownLineWidth: 60, FileLineWidthBounded: true, MarkdownLineWidthBounded: false },
            "Saved line widths must persist in the profile.");
        using (var reopened = new E2eWorkspace(directory, openFiles: false))
        {
            var settings = OpenLineWidth(reopened);
            Require(Named<TextBox>(settings, "FileLineWidthInput").Text == "80" && Named<TextBox>(settings, "MarkdownLineWidthInput").Text == "60" &&
                Named<CheckBox>(settings, "FileLineWidthBounded").IsChecked == true && Named<CheckBox>(settings, "MarkdownLineWidthBounded").IsChecked == false,
                "A fresh window must restore the saved line widths.");
            Close(settings);
        }
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: line-width controls validate drafts and persist (broadcast part pending)");
    }

    internal static void SaveWidth(E2eWorkspace app, Window settings, string kind, int value)
    {
        var save = Named<Button>(settings, kind + "LineWidthSave");
        Type(app, settings, Named<TextBox>(settings, kind + "LineWidthInput"), value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Require(save.IsEnabled, "Save must be enabled for a valid changed width.");
        app.Click(save);
        Until(() => !save.IsEnabled);
    }

    private static void Type(E2eWorkspace app, Window settings, TextBox input, string text)
    {
        app.Click(input);
        input.SelectAll();
        settings.KeyTextInput(text);
        Until(() => input.Text == text);
    }

    private static T Named<T>(Window window, string name) where T : Control
    {
        Until(() => window.GetLogicalDescendants().OfType<T>().Any(control => control.Name == name));
        return window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    }

    private static SettingsWindow OpenLineWidth(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        app.Click(Named<Button>(settings, "Settings_Line_width"));
        return settings;
    }

    private static void Close(SettingsWindow settings)
    {
        Named<Button>(settings, "SettingsClose").Focus();
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !settings.IsVisible);
    }
}
