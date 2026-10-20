using System.Globalization;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// Explains how a language-server process ended. Exit codes, stderr and message names are reported as facts; anything inferred
/// from a code is worded as suspected, because a server can also exit with 137 by itself.
/// </summary>
internal static class LanguageExits
{
    private const int DetailBudget = 24 * 1024;

    internal static LanguageExit Build(string server, string workspace, DateTime started, DateTime ended, int code, string? stopReason, bool failure,
        IReadOnlyList<string> stderr, IReadOnlyList<string> messages, IReadOnlyList<string> inflight, long memoryBytes, DateTime memoryAt)
    {
        var uptime = (int)Math.Max(0, (ended - started).TotalSeconds);
        var signal = Signal(code, out var meaning);
        var lastError = stderr.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";
        var summary = new StringBuilder();
        if (stopReason is not null)
        {
            summary.Append("Stopped by SharpRail: ").Append(stopReason).Append(" (exit code ").Append(code).Append(signal.Length > 0 ? ", " + signal : "").Append(')');
            if (failure && lastError.Length > 0) summary.Append("; last stderr line: ").Append(Clip(lastError, 160));
        }
        else
        {
            summary.Append(code == 0 ? "Exited with code 0 on its own; SharpRail did not ask it to stop" : $"Exited with code {code}" + (signal.Length > 0 ? $" ({signal}; {meaning})" : ""));
            if (lastError.Length > 0) summary.Append("; last stderr line: ").Append(Clip(lastError, 160));
            else summary.Append("; it wrote nothing to stderr");
        }
        if (inflight.Count > 0) summary.Append("; in flight: ").Append(inflight[0]);
        summary.Append(". Up for ").Append(Duration(uptime)).Append('.');

        var details = new StringBuilder();
        details.AppendLine($"Server: {server}");
        details.AppendLine($"Workspace: {workspace}");
        details.AppendLine($"Ended: {ended.ToString("u", CultureInfo.InvariantCulture)} (up {Duration(uptime)})");
        details.AppendLine($"Exit code: {code}" + (signal.Length > 0 ? $" ({signal})" : ""));
        details.AppendLine(stopReason is null ? "Requested by SharpRail: no" : $"Requested by SharpRail: yes, because {stopReason}");
        if (stopReason is null && meaning.Length > 0) details.AppendLine("Interpretation (suspected, not confirmed): " + meaning);
        if (memoryBytes > 0) details.AppendLine($"Resident memory at last sample ({memoryAt:HH:mm:ss} UTC): {Bytes(memoryBytes)}");
        var limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (limit > 0) details.AppendLine($"Memory available to the host process: {Bytes(limit)}");
        details.AppendLine(inflight.Count == 0 ? "Requests in flight: none" : "Requests in flight: " + string.Join(", ", inflight));
        details.AppendLine($"Last {messages.Count} JSON-RPC messages (oldest first):");
        foreach (var message in messages) details.AppendLine("  " + message);
        details.AppendLine($"Last {stderr.Count} stderr lines:");
        var errorText = new StringBuilder();
        foreach (var line in stderr) errorText.AppendLine("  " + line);
        if (stderr.Count == 0) errorText.AppendLine("  (nothing written to stderr)");
        var rest = Math.Max(1024, DetailBudget - details.Length);
        details.Append(errorText.Length > rest ? "  …\n" + errorText.ToString()[^rest..] : errorText.ToString());
        var full = details.ToString();
        return new(ended.ToString("o", CultureInfo.InvariantCulture), summary.ToString(), stopReason is null || failure, stopReason ?? "", code, signal, uptime, memoryBytes, full.Length > DetailBudget * 2 ? full[..(DetailBudget * 2)] : full);
    }

    /// <summary>Names the signal behind a .NET exit code (128+n on Unix) or a Windows fault status; empty when the code is an ordinary exit.</summary>
    internal static string Signal(int code, out string meaning)
    {
        meaning = "";
        if (OperatingSystem.IsWindows())
        {
            switch ((uint)code)
            {
                case 0xC0000005: meaning = "an access violation: a native crash in the server or its runtime."; return "access violation";
                case 0xC00000FD: meaning = "a stack overflow."; return "stack overflow";
                case 0xC0000409: meaning = "a fatal stack-buffer or fail-fast error."; return "fail fast";
                case 0xE0434352: meaning = "an unhandled .NET exception; see stderr."; return "unhandled .NET exception";
                case 0xC000013A: meaning = "the console was closed or Ctrl+C was sent."; return "Ctrl+C";
                default: return "";
            }
        }
        var number = code is > 128 and <= 128 + 64 ? code - 128 : code is < 0 and >= -64 ? -code : 0;
        if (number == 0) return "";
        var bus = OperatingSystem.IsMacOS() ? 10 : 7;
        if (number == bus) { meaning = "a bus error: a native crash in the server or its runtime."; return "SIGBUS"; }
        switch (number)
        {
            case 1: meaning = "its terminal or parent went away (SIGHUP)."; return "SIGHUP";
            case 2: meaning = "an interrupt (SIGINT)."; return "SIGINT";
            case 3: meaning = "a quit request (SIGQUIT)."; return "SIGQUIT";
            case 4: meaning = "an illegal instruction: a native crash."; return "SIGILL";
            case 6: meaning = "the runtime or server called abort, commonly after an unhandled exception or fatal runtime error; see stderr."; return "SIGABRT";
            case 8: meaning = "an arithmetic fault: a native crash."; return "SIGFPE";
            case 9: meaning = "killed by SIGKILL. Often the kernel OOM killer or the macOS memory-pressure killer (jetsam), but also a manual kill -9; the cause is not confirmed."; return "SIGKILL";
            case 11: meaning = "a segmentation fault: a native crash in the server or its runtime."; return "SIGSEGV";
            case 13: meaning = "it wrote to a closed pipe (SIGPIPE)."; return "SIGPIPE";
            case 15: meaning = "something asked it to terminate (SIGTERM)."; return "SIGTERM";
            default: return "signal " + number;
        }
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    internal static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : seconds + "s";

    private static string Bytes(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GiB" : $"{value / (double)(1L << 20):0} MiB";
}

/// <summary>The last exit record per server per workspace, kept in the host's language storage so it survives pane reopening and host restarts.</summary>
internal sealed class LanguageExitStore(string directory)
{
    internal const int Capacity = 64;
    private readonly object gate = new();
    private Dictionary<string, LanguageExit>? entries;
    private string Path => System.IO.Path.Combine(directory, "exits.json");
    internal static string Key(string workspace, string server) => workspace + "\0" + server;

    private sealed record Entry(string Workspace, string Server, LanguageExit Exit);

    internal LanguageExit? Get(string workspace, string server)
    {
        lock (gate) return Load().GetValueOrDefault(Key(workspace, server));
    }

    internal void Set(string workspace, string server, LanguageExit exit)
    {
        lock (gate)
        {
            var all = Load();
            all[Key(workspace, server)] = exit;
            foreach (var old in all.OrderBy(pair => pair.Value.Time, StringComparer.Ordinal).Take(Math.Max(0, all.Count - Capacity)).ToArray()) all.Remove(old.Key);
            Save(all);
        }
    }

    internal void Remove(string workspace)
    {
        lock (gate)
        {
            var all = Load();
            var gone = all.Keys.Where(key => key.StartsWith(workspace + "\0", StringComparison.Ordinal)).ToArray();
            foreach (var key in gone) all.Remove(key);
            if (gone.Length > 0) Save(all);
        }
    }

    private Dictionary<string, LanguageExit> Load()
    {
        if (entries is not null) return entries;
        entries = [];
        try
        {
            if (File.Exists(Path))
                foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path)) ?? [])
                    if (entry.Exit is not null) entries[Key(entry.Workspace, entry.Server)] = entry.Exit;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return entries;
    }

    private void Save(Dictionary<string, LanguageExit> all)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all.Select(pair => { var parts = pair.Key.Split('\0'); return new Entry(parts[0], parts[1], pair.Value); })));
            File.Move(temporary, Path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}