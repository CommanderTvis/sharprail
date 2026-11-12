using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// Dialogs shown by the shared host live in the workbench window like Settings: no owned window, so clicking the owner has nothing
/// to re-order and cannot flicker the dialog, Escape closes, focus comes back, and a dialog never outgrows the window.
/// </summary>
internal static class DialogOverlayChecks
{
    internal static void Run(string root)
    {
        var project = Path.Combine(root, "dialog-overlay");
        Directory.CreateDirectory(project);
        using var app = new E2E.E2eWorkspace(project, openFiles: false);
        Until(() => app.Window.WorkspaceMounted);
        var window = app.Window;
        Clone(app, window);
        Escape(app, window);
        Clamp(app, window);
        Families(window);
        Console.WriteLine("PASS dialogs open in the window overlay: no owned window, a click outside is inert, Escape closes and returns focus, the result flows, and a tall dialog stays inside the window, and every dialog family behaves alike");
    }

    private static DialogWindow Single(Window window) => DialogWindow.On(window).OfType<DialogWindow>().Single();

    private static void Clone(E2E.E2eWorkspace app, Window window)
    {
        var activations = 0;
        EventHandler count = (_, _) => activations++;
        window.Activated += count; window.Deactivated += count;
        var overlays = window.GetVisualDescendants().OfType<SharpRail.UI.OverlayHost>().Count();
        var cloned = new List<(string Url, string Parent, string Folder, int? Depth)>();
        var task = Dialogs.CloneProject(window, "/parent", null, (url, parent, folder, depth) =>
        {
            cloned.Add((url, parent, folder, depth));
            return Task.FromResult(Path.Combine(parent, folder));
        });
        Until(() => DialogWindow.On(window).Count == 1);
        var dialog = Single(window);
        Require(dialog.InOverlay && window.OwnedWindows.Count == 0 && dialog.Tag as string == "CloneProjectDialog",
            "The Clone repository dialog opens inside the window, not as an owned window.");
        var closed = 0;
        dialog.Closed += (_, _) => closed++;
        var scrims = window.GetVisualDescendants().OfType<Border>().Count(border => ReferenceEquals(border.Background, SharpRail.Plugins.UI.Kit.Ui.Overlay));
        var before = activations;
        // Clicking the owner outside the card is what flickered an owned window; here nothing is re-ordered or re-created.
        foreach (var point in new[] { new Point(3, 3), new Point(window.Bounds.Width - 3, window.Bounds.Height - 3) })
        {
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Require(DialogWindow.On(window).Count == 1 && ReferenceEquals(Single(window), dialog) && closed == 0 && window.OwnedWindows.Count == 0,
            "Clicking outside the dialog neither closes nor re-creates it.");
        Require(activations == before, $"Clicking outside the dialog activates or deactivates nothing, saw {activations - before} events.");
        Require(window.GetVisualDescendants().OfType<Border>().Count(border => ReferenceEquals(border.Background, SharpRail.Plugins.UI.Kit.Ui.Overlay)) == scrims
            && window.GetVisualDescendants().OfType<SharpRail.UI.OverlayHost>().Count() == overlays + 1, "Clicking outside adds and removes no scrim or layer.");
        T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        Find<TextBox>("CloneProjectUrl").Text = "https://example.com/team/sample.git";
        Find<TextBox>("CloneProjectDepth").Text = "1";
        Until(() => Find<Button>("CloneProjectCreate").IsEnabled);
        app.Click(Find<Button>("CloneProjectCreate"), freshGesture: false);
        Until(() => task.IsCompleted);
        Require(task.Result == Path.Combine("/parent", "sample") && cloned.Single() == ("https://example.com/team/sample.git", "/parent", "sample", 1),
            "The Clone repository result and its inputs flow back to the caller unchanged.");
        Until(() => DialogWindow.On(window).Count == 0 && window.GetVisualDescendants().OfType<SharpRail.UI.OverlayHost>().Count() == overlays);
        Require(closed == 1, "The dialog closed once.");
        window.Activated -= count; window.Deactivated -= count;
    }

    private static void Escape(E2E.E2eWorkspace app, Window window)
    {
        var anchor = window.GetVisualDescendants().OfType<InputElement>().First(item => item.Focusable && item.IsEffectivelyVisible && item.IsEffectivelyEnabled && item is Button);
        anchor.Focus();
        Until(() => window.FocusManager!.GetFocusedElement() == anchor);
        var task = Dialogs.Confirm(window, "Remove worktree?", "Removes it.");
        Until(() => DialogWindow.On(window).Count == 1);
        Until(() => window.FocusManager!.GetFocusedElement() != anchor);
        Require(Single(window).InOverlay, "The confirmation opens in the overlay.");
        Single(window).KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => task.IsCompleted);
        Require(!task.Result, "Escape answers a confirmation with no.");
        Until(() => DialogWindow.On(window).Count == 0 && window.FocusManager!.GetFocusedElement() == anchor);
    }

    /// <summary>A dialog with 200 lines of text in a 600 px window stays inside it, with its title and buttons in reach.</summary>
    private static void Clamp(E2E.E2eWorkspace app, Window window)
    {
        window.Height = 600; Settle(100);
        var dialog = Dialogs.Create("Tall dialog", 560);
        dialog.InWindow = true;
        var box = new TextBox { Name = "TallText", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = string.Join('\n', Enumerable.Range(1, 200).Select(line => "line " + line)) };
        dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(box);
        dialog.FindControl<StackPanel>("DialogActions")!.Children.Add(Dialogs.Primary(Ui.Button("Done", () => dialog.Close(true))));
        var task = dialog.ShowDialog<bool>(window);
        Until(() => DialogWindow.On(window).Count == 1 && dialog.Surface!.Bounds.Height > 0);
        Settle(100);
        var card = dialog.Surface!;
        var origin = card.TranslatePoint(default, window)!.Value;
        Require(origin.Y >= 0 && origin.X >= 0 && origin.Y + card.Bounds.Height <= window.Bounds.Height + 0.5 && origin.X + card.Bounds.Width <= window.Bounds.Width + 0.5,
            $"The dialog stays inside the window: card {origin} {card.Bounds.Size}, window {window.Bounds.Size}.");
        var heading = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "DialogHeading");
        var done = dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name is null && button.Classes.Contains("primary"));
        foreach (var part in new Control[] { heading, done })
        {
            var corner = part.TranslatePoint(new Point(part.Bounds.Width, part.Bounds.Height), window)!.Value;
            Require(part.IsEffectivelyVisible && part.TranslatePoint(default, window)!.Value.Y >= 0 && corner.Y <= window.Bounds.Height,
                $"The {(part == heading ? "title" : "button row")} stays in the window.");
        }
        Require(dialog.GetLogicalDescendants().OfType<ScrollViewer>().Any(viewer => viewer.Name == "DialogFieldsScroll" && viewer.Extent.Height > viewer.Viewport.Height),
            "The body scrolls instead of growing past the window.");
        app.Click(done, freshGesture: false);
        Until(() => task.IsCompleted);
        Require(task.Result && DialogWindow.On(window).Count == 0, "The button at the bottom of an over-tall dialog can be clicked.");
        window.Height = 848;
    }

    /// <summary>Every dialog family opens in the overlay (no owned window), ignores clicks outside it, and closes on Escape with focus back.</summary>
    private static void Families(Window window)
    {
        window.Height = 848; Settle(100);
        var anchor = window.GetVisualDescendants().OfType<Button>().First(item => item.Focusable && item.IsEffectivelyVisible && item.IsEffectivelyEnabled);
        var families = new (string Name, Func<Task> Open)[]
        {
            ("Notice", () => Dialogs.Notice(window, "Couldn't open", "The reason.")),
            ("AskToSave", () => Dialogs.AskToSave(window, "Save changes?", false)),
            ("Confirm", () => Dialogs.Confirm(window, "Remove?", "Removes it.")),
            ("Prompt", () => Dialogs.Prompt(window, "Rename", ("Name", "a"))),
            ("PathName", () => Dialogs.PathName(window, "Rename file", "a.txt", "Rename", _ => false, _ => Task.CompletedTask)),
            ("HostPath", () => Dialogs.HostPath(window, "/", null, false)),
            ("CreateProject", () => Dialogs.CreateProject(window, "/", null, (_, _) => Task.FromResult(""))),
            ("CloneProject", () => Dialogs.CloneProject(window, "/", null, (_, _, _, _) => Task.FromResult(""))),
            ("FileChangedOnDisk", () => Dialogs.FileChangedOnDisk(window, "a.cs", true)),
            ("DiscardUnsaved", () => Dialogs.DiscardUnsaved(window, "a.cs", "main", "disk full")),
            ("SettingValue", () => SettingValueDialog.ShowAsync(window, new SettingValueDialogOptions("Check", "key", null, [], "Save"))),
            ("PathBrowser", () => new PathBrowserDialog("Choose", "/", (path, _) => Task.FromResult(new PathBrowserFolder(path, path, null, null, []))).ShowAsync(window)),
            ("ExistingWorktree", () => new ExistingWorktreeDialog(() => Task.FromResult<IReadOnlyList<SharpRail.Host.Abstractions.ExistingWorktree>>([]), _ => throw new InvalidOperationException()).ShowAsync(window)),
            ("Search", () => new SearchDialog((_, _) => Task.FromResult(new SharpRail.Host.Abstractions.SearchHits([], false))).ShowAsync(window)),
            ("About", () => { var about = Dialogs.About(window); return Task.CompletedTask; }),
        };
        foreach (var (name, open) in families)
        {
            anchor.Focus();
            Until(() => window.FocusManager!.GetFocusedElement() == anchor);
            var task = open();
            Until(() => DialogWindow.On(window).Count == 1, name + " opens.");
            var dialog = Single(window);
            var closed = 0;
            dialog.Closed += (_, _) => closed++;
            Require(dialog.InOverlay && window.OwnedWindows.Count == 0, $"{name} opens in the window overlay, not as an owned window.");
            Settle(100);
            var point = new Point(3, 3);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Require(DialogWindow.On(window).Count == 1 && closed == 0 && ReferenceEquals(Single(window), dialog), $"{name} survives a click outside.");
            // A dialog that put focus nowhere still closes on Escape once its card holds focus.
            if (dialog.Surface!.GetVisualDescendants().OfType<InputElement>().All(item => !item.IsFocused)) dialog.Surface!.Focus();
            Single(window).KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Until(() => DialogWindow.On(window).Count == 0, $"{name} closes on Escape.");
            Until(() => window.FocusManager!.GetFocusedElement() == anchor, $"{name} returns focus.");
            Require(closed == 1, $"{name} closes once.");
        }
    }
}