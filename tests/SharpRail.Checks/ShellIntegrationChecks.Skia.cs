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
            }
            finally
            {
                window.Close();
                terminal.Close();
                Task.Run(async () => await pty.DisposeAsync()).GetAwaiter().GetResult();
            }
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Console.WriteLine("PASS shell integration (Skia): Command+Up/Down jumps between real zsh prompts");
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
}