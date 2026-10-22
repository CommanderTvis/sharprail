using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// What a terminal's shell integration has said, read from its output as it streams past the host: the working
/// directory (OSC 7, or a <c>cwd=</c> option on an OSC 133 mark), whether the shell waits at its prompt or runs a command
/// (OSC 133 A/B, then C), and each finished command's exit code (OSC 133 D). Positions are host output offsets, so every
/// client of the session reads the same commands. Not thread-safe: the owner feeds and reads it under its output lock.
/// </summary>
internal sealed class TerminalShellState
{
    internal const int MaxSequence = 4096;
    internal const int MaxCommands = 256;
    private enum Scan { Text, Escape, Osc, OscEscape }
    private Scan scan;
    private readonly List<byte> sequence = [];
    private bool overflow;
    private long position;
    private long? commandStart;
    private readonly LinkedList<TerminalCommand> commands = new();

    internal string? Directory { get; private set; }
    internal TerminalShellPhase Phase { get; private set; }
    internal int? LastExitCode { get; private set; }
    internal bool Marked { get; private set; }
    /// <summary>Raised under the owner's lock when the phase or the last exit code changes.</summary>
    internal Action? Changed { get; set; }

    internal TerminalShellStatus Status => new(Phase, Directory, LastExitCode) { Commands = [.. commands], Marked = Marked };

    internal void Push(ReadOnlySpan<byte> chunk)
    {
        foreach (var value in chunk)
        {
            position++;
            switch (scan)
            {
                case Scan.Text:
                    if (value == 0x1b) scan = Scan.Escape;
                    break;
                case Scan.Escape:
                    if (value == (byte)']') { scan = Scan.Osc; sequence.Clear(); overflow = false; }
                    else scan = value == 0x1b ? Scan.Escape : Scan.Text;
                    break;
                case Scan.Osc:
                    if (value == 0x07) Complete();
                    else if (value == 0x1b) scan = Scan.OscEscape;
                    else if (value is 0x18 or 0x1a) scan = Scan.Text;
                    else if (sequence.Count < MaxSequence) sequence.Add(value);
                    else overflow = true;
                    break;
                case Scan.OscEscape:
                    if (value == (byte)'\\') Complete();
                    else { scan = value == (byte)']' ? Scan.Osc : Scan.Text; sequence.Clear(); overflow = false; }
                    break;
            }
        }
    }

    private void Complete()
    {
        scan = Scan.Text;
        if (overflow) return;
        var text = Encoding.UTF8.GetString([.. sequence]);
        if (text.StartsWith("7;", StringComparison.Ordinal)) { if (DirectoryOf(text[2..]) is { } directory) Directory = directory; return; }
        if (!text.StartsWith("133;", StringComparison.Ordinal) || text.Length < 5) return;
        var parts = text[4..].Split(';');
        foreach (var option in parts.Skip(1))
            if (option.StartsWith("cwd=", StringComparison.OrdinalIgnoreCase) && option.Length > 4 && Path.IsPathRooted(option[4..])) Directory = option[4..];
        var (phase, exit) = (Phase, LastExitCode);
        switch (parts[0])
        {
            case "A" or "P":
                // P marks a prompt's other parts (continuation, right prompt); only A starts one.
                if (parts[0] == "A") { Marked = true; Phase = TerminalShellPhase.Prompt; }
                break;
            case "B":
                Marked = true;
                Phase = TerminalShellPhase.Input;
                break;
            case "C":
                Marked = true;
                Phase = TerminalShellPhase.Running;
                commandStart = position;
                break;
            case "D":
                Marked = true;
                int? code = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                // A D with no command since the last one (zsh closes an empty prompt) records nothing.
                if (commandStart is { } start)
                {
                    LastExitCode = code;
                    commands.AddLast(new TerminalCommand(start, position, code));
                    if (commands.Count > MaxCommands) commands.RemoveFirst();
                }
                commandStart = null;
                Phase = TerminalShellPhase.Prompt;
                break;
        }
        if ((phase, exit) != (Phase, LastExitCode)) Changed?.Invoke();
    }

    // file://host/path (percent-encoded) and kitty-shell-cwd://host/path (raw), as Ghostty accepts them; a directory
    // on another machine (a host name that is not this one) is ignored, as ssh sessions report theirs.
    internal static string? DirectoryOf(string url)
    {
        string rest;
        bool encoded;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) { rest = url[7..]; encoded = true; }
        else if (url.StartsWith("kitty-shell-cwd://", StringComparison.OrdinalIgnoreCase)) { rest = url[18..]; encoded = false; }
        else return null;
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var host = rest[..slash];
        if (!LocalHost(host)) return null;
        var path = rest[slash..];
        if (encoded)
        {
            var query = path.IndexOfAny(['?', '#']);
            if (query >= 0) path = path[..query];
            path = Uri.UnescapeDataString(path);
        }
        return path.Length > 0 && !path.Contains('\0') ? path : null;
    }

    private static readonly Lazy<string[]> LocalNames = new(() =>
    {
        string? dns = null;
        try { dns = System.Net.Dns.GetHostName(); } catch (System.Net.Sockets.SocketException) { }
        return [.. new[] { Environment.MachineName, dns }.OfType<string>().Select(Short)];
    });

    private static string Short(string name) => name.Split('.')[0];

    private static bool LocalHost(string host) =>
        host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        LocalNames.Value.Contains(Short(host), StringComparer.OrdinalIgnoreCase);
}