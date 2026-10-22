using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Ghostty.Avalonia;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

// Prompt features of the Skia renderer over a real integrated zsh: libghostty-vt's own OSC 133 marks.
internal static partial class ShellIntegrationChecks
{
    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

    private static void Until(Func<bool> condition, string description)
    {
        var deadline = Awake.Now.AddSeconds(20);
        while (!condition())
        {
            if (Awake.Now > deadline) throw new InvalidOperationException("Shell integration: timed out waiting for " + description);
            Pump();
            Thread.Sleep(10);
        }
    }

    internal static void RunSkia(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP shell integration renderer checks (libghostty-vt is built on macOS)"); return; }
        var fixture = Fixture(root);
        WithFixture(fixture, () =>
        {
            var pty = new PtyTerminalService("/bin/zsh", shellIntegration: () => true);
            var factory = TerminalBackends.Ghostty(new LocalTerminalAdapter(pty), () => TerminalRenderers.Skia);
            var terminal = new TerminalView(factory, new TerminalLaunch(Path.Combine(fixture, "workspace"), "skia-prompts-" + Guid.NewGuid().ToString("N"),
                Path.Combine(fixture, ".clipboard"), "skia-prompts"));
            var window = new Window { Width = 700, Height = 360, Content = terminal };
            window.Show(); Pump();
            try
            {
                GhosttySkiaView View() => terminal.Backend!.View.GetVisualDescendants().OfType<GhosttySkiaView>().Single();
                Until(() => terminal.Backend?.Started.IsCompletedSuccessfully == true, "the shell attached");
                terminal.Backend!.FocusTerminal(); Pump();
                Until(() => View().ReadViewport(false).Contains('%'), "the first prompt");
                SkiaJumps(window, View);
                SkiaOutput(window, View);
                SkiaClickToMove(window, View);
                SkiaBadges(window, View);
            }
            finally
            {
                window.Close();
                terminal.Close();
                Task.Run(async () => await pty.DisposeAsync()).GetAwaiter().GetResult();
            }
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Console.WriteLine("PASS shell integration (Skia): Command+Up/Down jumps between real zsh prompts; Command+triple-click and the menu's Copy Output of This Command take exactly one command's output; a click on the input line moves zsh's cursor, and a running command gets no arrows; a command that exits non-zero is badged on its command line");
    }

    private static void Type(Window window, string command)
    {
        window.KeyTextInput(command); Pump();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Pump();
    }

    private static string TopRow(GhosttySkiaView view) => view.ReadViewport(false).Split('\n')[0];

    private static void SkiaJumps(Window window, Func<GhosttySkiaView> view)
    {
        for (var i = 1; i <= 3; i++) Type(window, $"echo JUMP_CMD{i}; seq 1 40");
        Until(() => view().ReadScreen().Split('\n').Count(line => line.Trim() == "40") == 3, "three commands' output");
        Until(() => view().ReadViewport(false).TrimEnd().EndsWith('%'), "the last prompt");
        void Jump(Key key, string expected)
        {
            window.KeyPress(key, RawInputModifiers.Meta, key == Key.Up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);
            window.KeyRelease(key, RawInputModifiers.Meta, key == Key.Up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);
            Pump();
            Until(() => TopRow(view()).Contains(expected, StringComparison.Ordinal), "the viewport's top row showing " + expected + "; top: " + TopRow(view()));
        }
        Jump(Key.Up, "JUMP_CMD3");
        Jump(Key.Up, "JUMP_CMD2");
        Jump(Key.Up, "JUMP_CMD1");
        Jump(Key.Down, "JUMP_CMD2");
        Jump(Key.Down, "JUMP_CMD3");
        // The next prompt is in the active area, so the view returns to the bottom.
        window.KeyPress(Key.Down, RawInputModifiers.Meta, PhysicalKey.ArrowDown, null);
        window.KeyRelease(Key.Down, RawInputModifiers.Meta, PhysicalKey.ArrowDown, null);
        Until(() => view().ReadViewport(false).TrimEnd().EndsWith('%'), "the bottom after the last prompt");
    }

    internal static readonly string SeqOutput = "JUMP_CMD3\n" + string.Join("\n", Enumerable.Range(1, 40));

    private static void SkiaOutput(Window window, Func<GhosttySkiaView> view)
    {
        var rows = view().ReadViewport(false).Split('\n');
        var row = Array.FindIndex(rows, line => line.Trim() == "30");
        Require(row >= 0, "the last command's output must be on view: " + string.Join("|", rows));
        var point = view().CellCenter(0, row)!.Value;
        var output = view().CommandOutputAt(point);
        Require(output == SeqOutput, "the output at a point must be exactly that command's output, got: [" + output + "]");
        var prompt = Array.FindLastIndex(rows, line => line.TrimEnd().EndsWith('%'));
        Require(view().CommandOutputAt(view().CellCenter(0, prompt)!.Value) is null, "a prompt row is no command's output.");
        var position = view().TranslatePoint(point, window)!.Value;
        window.MouseDown(position, Avalonia.Input.MouseButton.Left); window.MouseUp(position, Avalonia.Input.MouseButton.Left); Pump();
        window.MouseDown(position, Avalonia.Input.MouseButton.Left); window.MouseUp(position, Avalonia.Input.MouseButton.Left); Pump();
        Require(view().SelectedText == "30", "a double click must select the word: [" + view().SelectedText + "]");
        Thread.Sleep(700);
        for (var i = 0; i < 3; i++)
        {
            window.MouseDown(position, Avalonia.Input.MouseButton.Left, RawInputModifiers.Meta);
            window.MouseUp(position, Avalonia.Input.MouseButton.Left, RawInputModifiers.Meta);
            Pump();
        }
        Until(() => view().SelectedText == SeqOutput, "Command+triple-click selecting the output; selected: [" + view().SelectedText + "]");
        window.MouseDown(position, Avalonia.Input.MouseButton.Right);
        window.MouseUp(position, Avalonia.Input.MouseButton.Right);
        Until(() => view().ClipboardMenu?.Items.OfType<MenuItem>().Any(item => item.Header as string == "Copy Output of This Command") == true,
            "the context menu offering Copy Output of This Command");
        view().ClipboardMenu!.Close();
        // The open menu took the right button's release; the view's next release completes that click instead.
        window.MouseDown(position, Avalonia.Input.MouseButton.Left); window.MouseUp(position, Avalonia.Input.MouseButton.Left);
        Pump();
    }

    private static Point CellOf(Window window, GhosttySkiaView view, string text, int offset)
    {
        var rows = view.ReadViewport(false).Split('\n');
        var row = Array.FindLastIndex(rows, line => line.Contains(text, StringComparison.Ordinal));
        Require(row >= 0, "no row shows " + text + ": " + string.Join("|", rows));
        return view.TranslatePoint(view.CellCenter(rows[row].IndexOf(text, StringComparison.Ordinal) + offset, row)!.Value, window)!.Value;
    }

    private static void SkiaClickToMove(Window window, Func<GhosttySkiaView> view)
    {
        window.KeyTextInput("echo CLICK_abcdefghij"); Pump();
        Until(() => view().ReadViewport(false).Contains("echo CLICK_abcdefghij", StringComparison.Ordinal), "the typed command line");
        Thread.Sleep(600);
        var c = CellOf(window, view(), "CLICK_abcdefghij", 8);
        window.MouseDown(c, Avalonia.Input.MouseButton.Left); window.MouseUp(c, Avalonia.Input.MouseButton.Left); Pump();
        Thread.Sleep(600); Pump();

        Type(window, "X");
        Until(() => view().ReadScreen().Contains("\nCLICK_abXcdefghij", StringComparison.Ordinal), "the click moving the cursor before c; screen: " + view().ReadViewport(false));
        // A running command is not at a prompt: the same click sends it nothing.
        Type(window, "cat");
        Thread.Sleep(300);
        window.KeyTextInput("RUNNING_abc"); Pump();
        Until(() => view().ReadViewport(false).Contains("RUNNING_abc", StringComparison.Ordinal), "cat's echo");
        var a = CellOf(window, view(), "RUNNING_abc", 8);
        window.MouseDown(a, Avalonia.Input.MouseButton.Left); window.MouseUp(a, Avalonia.Input.MouseButton.Left); Pump();
        Type(window, "");
        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d"); window.KeyRelease(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d"); Pump();
        Until(() => view().ReadViewport(false).TrimEnd().EndsWith('%'), "the prompt after cat");
        Require(!view().ReadScreen().Contains("^[[D", StringComparison.Ordinal) && view().ReadScreen().Contains("\nRUNNING_abc\n", StringComparison.Ordinal),
            "a click while a command runs must send it nothing: " + view().ReadViewport(false));
    }

    private static void SkiaBadges(Window window, Func<GhosttySkiaView> view)
    {
        Type(window, "echo BADGE_OK");
        Type(window, "(exit 4) && echo BADGE_FAIL_X");
        Until(() => view().ReadViewport(false).TrimEnd().EndsWith('%') && view().ReadViewport(false).Contains("BADGE_FAIL_X", StringComparison.Ordinal), "the failed command");
        Until(() => view().FailedCommandRows.Count > 0, "a failed-command badge");
        var rows = view().ReadViewport(false).Split('\n');
        var failed = Array.FindLastIndex(rows, line => line.Contains("(exit 4)", StringComparison.Ordinal));
        var passed = Array.FindLastIndex(rows, line => line.Contains("echo BADGE_OK", StringComparison.Ordinal));
        Require(view().FailedCommandRows.Contains(failed) && !view().FailedCommandRows.Contains(passed),
            $"the badge must mark the failed command's line {failed}, not {passed}: [{string.Join(",", view().FailedCommandRows)}]");
    }
}