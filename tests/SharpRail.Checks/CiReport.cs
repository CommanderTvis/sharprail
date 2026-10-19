using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpRail.Checks;

/// <summary>
/// What a failed run says on the GitHub Actions run page: a workflow annotation per failed case and a section of
/// the step summary, so the cause is readable without opening the log. Inactive outside GitHub Actions.
/// </summary>
internal sealed record CaseFailure(string Name, int Ordinal, string Message, string? File, int Line, TimeSpan Duration, string Tail);

internal sealed record CaseTiming(string Name, TimeSpan Duration, bool Failed);

internal static partial class CiReport
{
    private const int SlowestShown = 15;

    private static bool capturing;

    private const int TailLines = 30, BufferLimit = 64 * 1024;

    private static readonly object gate = new();
    private static readonly StringBuilder recent = new();

    internal static bool Active { get; private set; }

    /// <summary>Groups are workflow commands of the whole log; lanes interleave, so only a single process groups.</summary>
    internal static bool Grouping => Active && Environment.GetEnvironmentVariable(Runner.OwnerVariable) is not { Length: > 0 };

    internal static void Enable()
    {
        var github = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (capturing || !github && Environment.GetEnvironmentVariable(Runner.KeepGoingVariable) != "1") return;
        capturing = true;
        Active = github;
        Console.SetOut(new Tee(Console.Out));
        Console.SetError(new Tee(Console.Error));
    }

    internal static void BeginCase(string name)
    {
        if (!capturing) return;
        lock (gate) recent.Clear();
        if (Grouping) Console.WriteLine("::group::" + name);
    }

    internal static void EndCase()
    {
        if (Grouping) Console.WriteLine("::endgroup::");
    }

    /// <summary>Describes a failed case, taking the tail of the output it alone produced.</summary>
    internal static CaseFailure Describe(string name, int ordinal, Exception error, TimeSpan duration)
    {
        string tail;
        lock (gate) tail = string.Join('\n', recent.ToString().Split('\n').TakeLast(TailLines)).Trim('\r', '\n');
        var (file, line) = Location(error.ToString());
        return new(name, ordinal, FirstLine(error.Message), file, line, duration, tail);
    }

    /// <summary>Annotates the failure and appends it to the step summary.</summary>
    internal static void Failed(string name, Exception error, int passed, int skipped)
    {
        if (!Active) return;
        Publish([Describe(name, -1, error, TimeSpan.Zero)], [], passed, skipped, null, keepGoing: false);
    }

    /// <summary>
    /// The end of a run: with keep-going, a console table of every failure and the slowest cases; under Actions an
    /// annotation per failed case and a step summary with the failures, each one's own output tail and the timings.
    /// </summary>
    internal static void Publish(IReadOnlyList<CaseFailure> failures, IReadOnlyList<CaseTiming> timings, int passed, int skipped, string? shard, bool keepGoing)
    {
        if (keepGoing)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CHECKS {(failures.Count == 0 ? "PASS" : "FAIL")}: passed {passed}, failed {failures.Count}, skipped {skipped}"));
            foreach (var failure in failures)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAIL {failure.Name} ({failure.Duration.TotalSeconds:F1}s) {failure.Message} {(failure.File is null ? "" : $"[{failure.File}:{failure.Line}]")}"));
            foreach (var timing in timings.OrderByDescending(timing => timing.Duration).Take(SlowestShown))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TIME {timing.Duration.TotalSeconds,7:F1}s {timing.Name}{(timing.Failed ? " (failed)" : "")}"));
        }
        if (!Active) return;
        // A group left open would swallow the annotations' context in the log.
        EndCase();
        foreach (var failure in failures)
        {
            var where = failure.File is null ? "" : $"file={Property(failure.File)},line={failure.Line},";
            Console.WriteLine($"::error {where}title={Property(failure.Name)}::{Data(failure.Message)}");
        }
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } summary) return;
        var text = new StringBuilder();
        var slice = shard is null ? "" : $" (shard {shard})";
        if (failures.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### Failed checks{slice}: {failures.Count}")
                .AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"Passed {passed}, failed {failures.Count}, skipped {skipped} (outside this process's selection); {(keepGoing ? "keep-going: every selected case ran." : "the gate stops at the first failure.")}")
                .AppendLine()
                .AppendLine("| Case | Seconds | Message | Location |")
                .AppendLine("| --- | --- | --- | --- |");
            foreach (var failure in failures)
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{Cell(failure.Name)}` | {failure.Duration.TotalSeconds:F1} | {Cell(failure.Message)} | {(failure.File is null ? "" : $"`{Cell($"{failure.File}:{failure.Line}")}`")} |");
            text.AppendLine();
            foreach (var failure in failures)
                text.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>`{Cell(failure.Name)}`: last {TailLines} lines of its output</summary>")
                    .AppendLine()
                    .AppendLine("```")
                    .AppendLine(failure.Tail.Replace("```", new string('\'', 3), StringComparison.Ordinal))
                    .AppendLine("```")
                    .AppendLine("</details>")
                    .AppendLine();
        }
        if (timings.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>Slowest {Math.Min(SlowestShown, timings.Count)} of {timings.Count} cases{slice}</summary>")
                .AppendLine()
                .AppendLine("| Case | Seconds |")
                .AppendLine("| --- | --- |");
            foreach (var timing in timings.OrderByDescending(timing => timing.Duration).Take(SlowestShown))
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{Cell(timing.Name)}`{(timing.Failed ? " (failed)" : "")} | {timing.Duration.TotalSeconds:F1} |");
            text.AppendLine().AppendLine("</details>").AppendLine();
        }
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