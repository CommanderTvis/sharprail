using System.Runtime.Versioning;
using System.Text;

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
        terminal.WriteOutput(Encoding.UTF8.GetBytes(Transcript()));
        await Until(() => terminal.ReadViewport().TrimEnd().EndsWith('%'), "the last prompt is on view: " + terminal.ReadViewport());
        await Jumps(terminal);
        Console.WriteLine("PASS Metal prompts: Command+Up/Down jumps between OSC 133 prompts");
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
}