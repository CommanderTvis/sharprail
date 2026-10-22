using System.Runtime.Versioning;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Ghostty.Avalonia;

namespace SharpRail.Checks;

// Prompt features of the Metal texture renderer (Ghostty 1.2.3) in a real Metal window, fed the OSC 133 marks Ghostty's
// zsh integration writes; the host checks run the real shell.
[SupportedOSPlatform("macos")]
internal static class NativePromptChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Metal prompts: " + message); }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Metal prompts: " + message);
            await Task.Delay(20);
        }
    }

    internal const string Prompt = "\u001b]133;A;cl=line\u0007% \u001b]133;B\u0007";

    // Three commands with forty lines of output each, then a fresh prompt.
    internal static string Transcript(Func<int, string>? output = null)
    {
        var text = new StringBuilder("\u001b[2J\u001b[H");
        for (var i = 1; i <= 3; i++)
        {
            text.Append(Prompt).Append($"echo JUMP_CMD{i}; seq 1 40\r\n\u001b]133;C\u0007");
            text.Append(output?.Invoke(i) ?? string.Join("", Enumerable.Range(1, 40).Select(n => n + "\r\n")));
            text.Append(i == 2 ? "\u001b]133;D;1\u0007" : "\u001b]133;D;0\u0007");
        }
        return text.Append(Prompt).ToString();
    }

    [SupportedOSPlatform("macos")]
    internal static async Task Run(Window window)
    {
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
        window.Content = terminal;
        await terminal.Ready;
        // A resize makes Ghostty clear prompt rows for the shell to redraw, which this script cannot; let layout settle.
        terminal.FocusTerminal();
        await Task.Delay(500);
        terminal.WriteOutput(Encoding.UTF8.GetBytes(Transcript()));
        await Until(() => terminal.ReadViewport().TrimEnd().EndsWith('%'), "the last prompt is on view: " + terminal.ReadViewport());
        await Jumps(terminal);
        await Output(terminal);
        await ClickToMove(window);
        Console.WriteLine("PASS Metal prompts: Command+Up/Down jumps between OSC 133 prompts; Command+triple-click and Copy Output of This Command take exactly one command's output; a plain click on the input line sends the arrows to it only between OSC 133 B and C");
    }

    private static string Top(GhosttyTextureView terminal) => terminal.ReadViewport().Split('\n')[0];

    private static async Task Jumps(GhosttyTextureView terminal)
    {
        async Task Jump(PhysicalKey key, string? expected)
        {
            terminal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key == PhysicalKey.ArrowUp ? Key.Up : Key.Down, PhysicalKey = key, KeyModifiers = KeyModifiers.Meta });
            if (expected is null) await Until(() => terminal.ReadViewport().TrimEnd().EndsWith('%'), "the bottom after the last prompt: " + terminal.ReadViewport());
            else await Until(() => Top(terminal).Contains(expected, StringComparison.Ordinal), "the top row showing " + expected + "; top: " + Top(terminal));
        }
        await Jump(PhysicalKey.ArrowUp, "JUMP_CMD3");
        await Jump(PhysicalKey.ArrowUp, "JUMP_CMD2");
        await Jump(PhysicalKey.ArrowUp, "JUMP_CMD1");
        await Jump(PhysicalKey.ArrowDown, "JUMP_CMD2");
        await Jump(PhysicalKey.ArrowDown, "JUMP_CMD3");
        await Jump(PhysicalKey.ArrowDown, null);
    }

    internal static Point Cell(GhosttyTextureView terminal, int column, int row) =>
        new(terminal.Bounds.Width / terminal.Size.Columns * (column + 0.5), terminal.Bounds.Height / terminal.Size.Rows * (row + 0.5));

    private static readonly string SeqOutput = string.Join("\n", Enumerable.Range(1, 40));

    private static async Task Output(GhosttyTextureView terminal)
    {
        var rows = terminal.ReadViewport().Split('\n');
        var row = Array.FindIndex(rows, line => line.Trim() == "30");
        Require(row >= 0, "the last command's output must be on view: " + string.Join("|", rows));
        var point = Cell(terminal, 0, row);
        var output = terminal.CommandOutputAt(point);
        Require(output == SeqOutput, "the output at a point must be exactly that command's output, got: [" + output + "]");
        Require(terminal.CommandOutputAt(Cell(terminal, 0, Array.FindLastIndex(rows, line => line.TrimEnd().EndsWith('%')))) is null, "a prompt row is no command's output.");
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        for (var i = 0; i < 3; i++)
        {
            terminal.RaiseEvent(new PointerPressedEventArgs(terminal, pointer, terminal, point, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton | RawInputModifiers.Meta, PointerUpdateKind.LeftButtonPressed), KeyModifiers.Meta, i + 1));
            terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, pointer, terminal, point, 2,
                new PointerPointProperties(RawInputModifiers.Meta, PointerUpdateKind.LeftButtonReleased), KeyModifiers.Meta, MouseButton.Left));
            await Task.Delay(60);
        }
        await Until(() => string.Join("\n", terminal.SelectedText.Split('\n').Select(line => line.TrimEnd())) == SeqOutput, "Command+triple-click selecting the output; selected: [" + terminal.SelectedText + "]");
        terminal.RaiseEvent(new PointerPressedEventArgs(terminal, pointer, terminal, point, 3,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
        terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, pointer, terminal, point, 4,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
        await Until(() => terminal.ClipboardMenu?.Items.OfType<MenuItem>().Any(item => item.Header as string == "Copy Output of This Command") == true,
            "the context menu offering Copy Output of This Command");
        terminal.ClipboardMenu!.Close();
    }

    private static async Task ClickToMove(Window window)
    {
        var io = new GhosttyExternalIo();
        var sent = new StringBuilder();
        io.Input += (_, bytes) => { lock (sent) sent.Append(Encoding.UTF8.GetString(bytes.Span)); };
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: io));
        window.Content = terminal;
        await terminal.Ready;
        string Sent() { lock (sent) { var text = sent.ToString(); sent.Clear(); return text; } }
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        async Task Click(Point point)
        {
            terminal.RaiseEvent(new PointerPressedEventArgs(terminal, pointer, terminal, point, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, pointer, terminal, point, 2,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await Task.Delay(300);
        }
        // A resize makes Ghostty clear prompt rows for the shell to redraw, which this script cannot; let focus and layout settle.
        terminal.FocusTerminal();
        await Task.Delay(500);
        // "% " is the prompt; the input "echo abcdefghij" follows, the cursor after it.
        terminal.WriteOutput(Encoding.UTF8.GetBytes("\u001b[2J\u001b[H" + Prompt + "echo abcdefghij"));
        await Until(() => terminal.ReadViewport().Contains("echo abcdefghij"), "the input line");
        Sent();
        await Click(Cell(terminal, 2 + 7, 0));
        var left = Sent();
        Require(left == string.Concat(Enumerable.Repeat("\u001b[D", 8)), "a click on c must send eight left arrows, sent: " + left.Replace("\u001b", "ESC"));
        // The shell moved its cursor to c; a click past the end of the input stops one cell after it.
        terminal.WriteOutput("\u001b[8D"u8);
        await Task.Delay(100);
        await Task.Delay(700);
        await Click(Cell(terminal, 40, 0));
        var right = Sent();
        Require(right == string.Concat(Enumerable.Repeat("\u001b[C", 8)), "a click past the input must move to its end, sent: " + right.Replace("\u001b", "ESC"));
        terminal.WriteOutput("\u001b[8C\r\n\u001b]133;C\u0007running"u8);
        await Task.Delay(100);
        await Click(Cell(terminal, 3, 1));
        Require(Sent().Length == 0, "a click while the command runs must send nothing.");
        terminal.WriteOutput("\u001b]133;D;0\u0007\r\n\u001b[?1049h"u8);
        await Task.Delay(100);
        await Click(Cell(terminal, 3, 1));
        Require(!Sent().Contains("\u001b[D", StringComparison.Ordinal), "a full-screen program must get no arrows.");
    }
}