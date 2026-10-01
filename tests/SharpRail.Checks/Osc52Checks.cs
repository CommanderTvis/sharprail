using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Ghostty.Avalonia;

namespace SharpRail.Checks;

// A private AppKit pasteboard and scripted permission decisions keep these checks
// away from the user's clipboard, while exercising the production callbacks.
internal static class Osc52Checks
{
    internal sealed class ClipboardScope : IDisposable
    {
        internal ClipboardScope() => Begin(false);
        public void Dispose() => End();
    }

    internal static string Sequence(string text, string selector = "c", string terminator = "\e\\") =>
        $"\e]52;{selector};{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}{terminator}";

    internal static void RunSkia()
    {
        Begin(false);
        try
        {
            using var view = new GhosttySkiaView();
            var replies = new List<byte>();
            view.Input += (_, data) => replies.AddRange(data.ToArray());
            void Write(string sequence) => view.Write(Encoding.UTF8.GetBytes(sequence));
            Set("sentinel");
            var sequence = Sequence("multiline\nGrüße 世界 🐈");
            foreach (var c in sequence[..^2]) Write(c.ToString());
            Require(Get() == "sentinel", "an incomplete ST sequence changed the clipboard");
            Write(sequence[^2..^1]);
            Write(sequence[^1..]);
            Require(Get() == "multiline\nGrüße 世界 🐈", "fragmented Unicode write");
            foreach (var selector in new[] { "c", "", "s", "p", "q" })
            {
                Write(Sequence("selector-" + selector, selector, "\a"));
                Require(Get() == "selector-" + selector, "BEL write to selector " + selector);
            }
            Require(Prompts() == 0, "writes unexpectedly requested permission");
            Set("sentinel");
            foreach (var invalid in new[] { "\e]52;c;!!!\a", "\e]52;c;eA==\x18", "\e]52;c;/w==\a" })
            {
                Write(invalid);
                Require(Get() == "sentinel", "malformed, cancelled or invalid UTF-8 write changed the clipboard");
            }
            Write(Sequence("hello\0world"));
            Require(Get() == "hello\0world", "embedded NUL was truncated");
            var large = new string('x', 32_768);
            Write(Sequence(large));
            Require(Get() == large, "large clipboard write was truncated");
            Write("\e]52;c;\a");
            Require(Get() == "", "empty write did not clear the clipboard");

            Set("private clipboard");
            Write("\e]52;c;?\a");
            Require(Prompts() == 1, "clipboard read did not request permission");
            Require(Encoding.UTF8.GetString([.. replies]) == "\e]52;c;\a", "denied read exposed data or omitted its empty response");
            Allow(true);
            foreach (var text in new[] { "Grüße\n世界", "", large })
            {
                Set(text);
                replies.Clear();
                Write("\e]52;c;?\e\\");
                Require(Encoding.UTF8.GetString([.. replies]) == Sequence(text), "approved clipboard read response");
            }
            Require(Prompts() == 4, "read permission was remembered without a grant");
            foreach (var selector in new[] { "s", "p" })
            {
                Set("selection read");
                replies.Clear();
                Write($"\e]52;{selector};?\a");
                Require(Encoding.UTF8.GetString([.. replies]) == Sequence("selection read", selector, "\a"), "clipboard read selector or BEL terminator");
            }
            Require(view.ReadScreen().Trim().Length == 0, "OSC 52 payload leaked onto the screen");
            Write("\ec" + Sequence("after reset"));
            Require(Get() == "after reset", "terminal reset lost clipboard callbacks");
            Console.WriteLine("PASS OSC 52 Skia: fragmented ST/BEL, Unicode, selectors, malformed/cancelled data, NUL, large/empty writes, denied/approved/empty reads, permission and reset");
        }
        finally { End(); }
    }

    internal static string Get()
    {
        var bytes = new byte[(int)Read(null, 0)];
        Read(bytes, (nuint)bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string ReadHex(string screen, string marker)
    {
        var lines = screen.Split('\n').Select(line => line.Trim()).ToArray();
        var start = Array.FindLastIndex(lines, line => line == marker + "_BEGIN");
        if (start < 0) File.WriteAllText(".bench/osc52-reply-screen.txt", screen);
        Require(start >= 0, "clipboard reply start marker is missing");
        var reply = string.Join(" ", lines.Skip(start + 1).TakeWhile(line => line != marker));
        return string.Concat(Regex.Matches(reply, @"\b[0-9a-f]{2}\b").Select(match => match.Value));
    }

    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("OSC 52: " + message); }

    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_begin")] internal static extern void Begin([MarshalAs(UnmanagedType.I1)] bool allow);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_end")] internal static extern void End();
    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_set")] internal static extern void Set([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_get")] private static extern nuint Read([Out] byte[]? buffer, nuint capacity);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_prompts")] internal static extern int Prompts();
    [DllImport("TerminalEvents", EntryPoint = "sr_check_clipboard_allow")] internal static extern void Allow([MarshalAs(UnmanagedType.I1)] bool allow);
}