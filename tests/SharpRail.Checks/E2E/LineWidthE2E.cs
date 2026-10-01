using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LineWidthE2E
{

    internal static void Run(string root)
    {
        Drafts(root);
        Convergence(root);
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP upstream line-width diff wrapping: the Scintilla diff is macOS-only.");
            return;
        }
        using var git = new IsolatedGit(Path.Combine(root, "line-width-git"));
        var longLine = string.Join(' ', Enumerable.Range(1, 80).Select(index => $"segment-{index:00}"));
        var directory = IsolatedGit.Repository(Path.Combine(root, "line-width-diff"), ("LONG_LINE.txt", longLine));
        using var app = new E2eWorkspace(directory, openFiles: false);
        // Wide enough that the diff opens split rather than inline.
        app.Window.Width = 1800;
        File.WriteAllText(Path.Combine(directory, "LONG_LINE.txt"), "changed " + longLine);
        var refresh = app.Window.RefreshAsync();
        Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        app.Click(app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        Until(() => app.Tabs.Count(tab => tab.Kind == "diff") == 1);
        Until(() => app.Window.GetLogicalDescendants().OfType<ScintillaEditor>()
            .Any(editor => editor.Name == "DiffNewText" && editor.Text.Contains("changed segment-01", StringComparison.Ordinal)));
        Settle(300);
        foreach (var name in new[] { "DiffOldText", "DiffNewText" })
        {
            var editor = app.Find<ScintillaEditor>(name);
            var line = editor.Text.Split('\n').Select((text, index) => (text, index)).Last(item => item.text.Contains("segment-80", StringComparison.Ordinal)).index;
            Require(editor.WrapCount(line) > 1, $"The {name} long line must wrap ({editor.WrapCount(line)} display lines).");
            Require(editor.WrapWidth == LineWidths.File(app.Window.Preferences.FileLineWidth, app.Window.Preferences.FileLineWidthBounded) && editor.HorizontalScroll.Maximum == 0,
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
        var saved = new ProfileStore(directory + "-profile").OpenState().Current.Settings;
        Require(saved is { FileLineWidth: 80, MarkdownLineWidth: 60, FileLineWidthBounded: true, MarkdownLineWidthBounded: false },
            "Saved line widths must persist in the local host state.");
        using (var reopened = new E2eWorkspace(directory, openFiles: false))
        {
            var settings = OpenLineWidth(reopened);
            Require(Named<TextBox>(settings, "FileLineWidthInput").Text == "80" && Named<TextBox>(settings, "MarkdownLineWidthInput").Text == "60" &&
                Named<CheckBox>(settings, "FileLineWidthBounded").IsChecked == true && Named<CheckBox>(settings, "MarkdownLineWidthBounded").IsChecked == false,
                "A fresh window must restore the saved line widths.");
            Close(settings);
        }
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: line-width controls validate drafts and persist");
    }

    /// <summary>Holds the next shared-state broadcast until released, like the reference's channel hold.</summary>
    private sealed class HeldState(IHostStateService inner) : IHostStateService
    {
        private TaskCompletionSource? held;
        private TaskCompletionSource? release;
        internal Task Held => held!.Task;
        internal void Arm() { held = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void Release() => release?.TrySetResult();
        public ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) => inner.GetStateAsync(cancellationToken);
        public ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default) => inner.ChangeAsync(changes, cancellationToken);
        public async IAsyncEnumerable<HostState> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var state in inner.WatchAsync(cancellationToken))
            {
                if (held is { Task.IsCompleted: false } pending)
                {
                    pending.TrySetResult();
                    await release!.Task.WaitAsync(cancellationToken);
                }
                yield return state;
            }
        }
    }

    // Upstream's chat measure is replaced by the Markdown width; the peer is a second window of the app.
    private static void Convergence(string root)
    {
        var directory = Path.Combine(root, "line-width-broadcast");
        HeldState? hold = null;
        using (var app = new E2eWorkspace(directory, openFiles: false, state: service => hold = new HeldState(service)))
        {
            var settings = OpenLineWidth(app);
            var markdownInput = Named<TextBox>(settings, "MarkdownLineWidthInput");
            Type(app, settings, markdownInput, "80");
            settings.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            settings.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Until(() => !Named<Button>(settings, "MarkdownLineWidthSave").IsEnabled && app.Window.Preferences.MarkdownLineWidth == 80);
            SaveWidth(app, settings, "File", 160);

            hold!.Arm();
            var markdownBounded = Named<CheckBox>(settings, "MarkdownLineWidthBounded");
            app.Click(markdownBounded);
            Until(() => hold.Held.IsCompleted);
            Settle(100);
            Require(markdownBounded.IsChecked == true && app.Window.Preferences.MarkdownLineWidthBounded,
                "The toggle must show the host's value until its broadcast arrives.");
            hold.Release();
            Until(() => markdownBounded.IsChecked == false);
            Require(Named<CheckBox>(settings, "FileLineWidthBounded").IsChecked == true, "The other width stays bounded.");
            Close(settings);

            using var peer = app.NewWindow();
            var peerSettings = OpenLineWidth(peer);
            settings = OpenLineWidth(app);
            Require(Named<TextBox>(peerSettings, "MarkdownLineWidthInput").Text == "80" && Named<TextBox>(peerSettings, "FileLineWidthInput").Text == "160" &&
                Named<CheckBox>(peerSettings, "MarkdownLineWidthBounded").IsChecked == false && Named<CheckBox>(peerSettings, "FileLineWidthBounded").IsChecked == true,
                "A second window shows the converged widths.");
            app.Click(Named<CheckBox>(settings, "FileLineWidthBounded"));
            Until(() => Named<CheckBox>(peerSettings, "FileLineWidthBounded").IsChecked == false);
            app.Click(Named<CheckBox>(settings, "FileLineWidthBounded"));
            Until(() => Named<CheckBox>(peerSettings, "FileLineWidthBounded").IsChecked == true);
            Close(peerSettings);
            Close(settings);
        }
        using (var reopened = new E2eWorkspace(directory, openFiles: false))
        {
            var settings = OpenLineWidth(reopened);
            Require(Named<TextBox>(settings, "MarkdownLineWidthInput").Text == "80" && Named<TextBox>(settings, "FileLineWidthInput").Text == "160" &&
                Named<CheckBox>(settings, "MarkdownLineWidthBounded").IsChecked == false && Named<CheckBox>(settings, "FileLineWidthBounded").IsChecked == true,
                "A fresh window must restore the converged widths.");
            Close(settings);
        }
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: line-width controls validate drafts, converge on broadcasts, and persist");
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