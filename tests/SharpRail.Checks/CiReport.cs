using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpRail.Checks;

/// <summary>
/// What a failed run says on the GitHub Actions run page: a workflow annotation per failed case and a section of
/// the step summary, so the cause is readable without opening the log. Inactive outside GitHub Actions.
/// </summary>
internal static partial class CiReport
{
    private const int TailLines = 30, BufferLimit = 64 * 1024;

    private static readonly object gate = new();
    private static readonly StringBuilder recent = new();

    internal static bool Active { get; private set; }

    /// <summary>Groups are workflow commands of the whole log; lanes interleave, so only a single process groups.</summary>
    internal static bool Grouping => Active && Environment.GetEnvironmentVariable(Runner.OwnerVariable) is not { Length: > 0 };

    internal static void Enable()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Active) return;
        Active = true;
        Console.SetOut(new Tee(Console.Out));
        Console.SetError(new Tee(Console.Error));
    }

    internal static void BeginCase(string name)
    {
        if (!Active) return;
        lock (gate) recent.Clear();
        if (Grouping) Console.WriteLine("::group::" + name);
    }

    internal static void EndCase()
    {
        if (Grouping) Console.WriteLine("::endgroup::");
    }

    /// <summary>Annotates the failure and appends it to the step summary.</summary>
    internal static void Failed(string name, Exception error, int passed, int skipped)
    {
        if (!Active) return;
        string tail;
        lock (gate) tail = string.Join('\n', recent.ToString().Split('\n').TakeLast(TailLines)).Trim('\r', '\n');
        var message = FirstLine(error.Message);
        var (file, line) = Location(error.ToString());
        // A group left open would swallow the annotation's context in the log.
        EndCase();
        var where = file is null ? "" : $"file={Property(file)},line={line},";
        Console.WriteLine($"::error {where}title={Property(name)}::{Data(message)}");
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } summary) return;
        var location = file is null ? "" : $"{file}:{line}";
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"### Failed check: `{name}`")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Passed {passed}, failed 1, skipped {skipped} (outside this process's selection); the gate stops at the first failure.")
            .AppendLine()
            .AppendLine("| Case | Message | Location |")
            .AppendLine("| --- | --- | --- |")
            .AppendLine(CultureInfo.InvariantCulture, $"| `{Cell(name)}` | {Cell(message)} | {(location.Length == 0 ? "" : $"`{Cell(location)}`")} |")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"<details><summary>Last {TailLines} lines of output</summary>")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(tail.Replace("```", "'''", StringComparison.Ordinal))
            .AppendLine("```")
            .AppendLine("</details>")
            .AppendLine();
        try { File.AppendAllText(summary, text.ToString()); }
        catch (IOException) { }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');

    // The first frame in tests/, skipping the assertion helpers that merely throw for their caller.
    private static (string? File, int Line) Location(string trace)
    {
        (string File, int Line, string Method)? first = null;
        foreach (Match frame in Frame().Matches(trace))
        {
            var path = frame.Groups["file"].Value.Replace('\\', '/');
            if (!path.Contains("/tests/", StringComparison.Ordinal)) continue;
            var method = frame.Groups["method"].Value;
            var found = (Relative(path), int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), method);
            first ??= found;
            if (!Helper().IsMatch(method)) return (found.Item1, found.Item2);
        }
        return first is { } fallback ? (fallback.File, fallback.Line) : (null, 0);
    }

    private static string Relative(string path)
    {
        var root = (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory()).Replace('\\', '/').TrimEnd('/') + "/";
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path[(path.IndexOf("/tests/", StringComparison.Ordinal) + 1)..];
    }

    private static string Data(string text) => text.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static string Property(string text) => Data(text).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*at (?<method>.+?) in (?<file>.+?):line (?<line>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Frame();

    [GeneratedRegex(@"\.(Require|Assert|Fail|Check|Expect)\w*(\(|$|<)")]
    private static partial Regex Helper();

    private sealed class Tee(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            Remember(value.ToString());
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is not null) Remember(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            inner.Write(buffer, index, count);
            Remember(new string(buffer, index, count));
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            Remember(value + "\n");
        }

        public override void Flush() => inner.Flush();

        private static void Remember(string text)
        {
            lock (gate)
            {
                recent.Append(text);
                if (recent.Length > BufferLimit) recent.Remove(0, recent.Length - BufferLimit);
            }
        }
    }
}