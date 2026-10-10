using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace SharpRail.Checks;

/// <summary>What the runner was asked to do before the gate or a focused mode sees its own arguments.</summary>
internal sealed record RunnerOptions(string[] Rest, int Lanes, Shard? Shard, Shard? Job, bool LastFailed, IReadOnlySet<string>? Cases, bool List)
{
    internal static int AutomaticLanes => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    internal static RunnerOptions Parse(string[] args, string? jobShard)
    {
        var rest = new List<string>();
        int? lanes = null;
        Shard? shard = null;
        var lastFailed = false; var list = false;
        HashSet<string>? cases = null;
        for (var index = 0; index < args.Length; index++)
        {
            string Value() => ++index < args.Length ? args[index] : throw new ArgumentException(args[index - 1] + " needs a value.");
            switch (args[index])
            {
                case "--lanes":
                    var count = Value();
                    lanes = count == "auto" ? AutomaticLanes
                        : int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var explicitLanes) && explicitLanes is >= 1 and <= 16 ? explicitLanes
                        : throw new ArgumentException("--lanes takes 1-16 or auto.");
                    break;
                case "--shard": shard = Checks.Shard.Parse(Value()); break;
                case "--case": cases = [.. Value().Split(',', StringSplitOptions.RemoveEmptyEntries)]; break;
                case "--last-failed": lastFailed = true; break;
                case "--list-cases": list = true; break;
                default: rest.Add(args[index]); break;
            }
        }
        // The job's slice is inherited by every child of the run: the fixtures this executable plays for its own
        // checks (a fake agent, a relay) carry arguments and are no slice of the gate.
        Shard? job = string.IsNullOrEmpty(jobShard) || rest.Count > 0 ? null : Checks.Shard.Parse(jobShard);
        var selecting = lanes is not null || shard is not null || job is not null || lastFailed || cases is not null || list;
        if (selecting && rest.Count > 0)
            throw new ArgumentException("Lanes, shards and case selection apply to the argument-free gate; a focused mode runs whole, in one lane.");
        if (shard is not null && job is not null)
            throw new ArgumentException($"{Runner.JobShardVariable} already selects this job's slice; the runner owns --shard.");
        if (shard is not null && lanes is not null)
            throw new ArgumentException("--lanes subdivides the run itself; it cannot be combined with --shard.");
        if (lastFailed && (lanes > 1 || shard is not null || job is not null || cases is not null))
            throw new ArgumentException("--last-failed is a serial repair run of what the last run left unfinished.");
        return new([.. rest], lanes ?? 1, shard, job, lastFailed, cases, list);
    }
}

internal static class Runner
{
    /// <summary>Set on a lane to the pid of the runner that owns its signals and the idle-sleep assertion.</summary>
    internal const string OwnerVariable = "SHARPRAIL_CHECKS_OWNER";
    internal const string JobShardVariable = "SHARPRAIL_CHECKS_JOB_SHARD";
    internal const string ResultVariable = "SHARPRAIL_CHECKS_RESULT";

    internal static string LastRun => Path.Combine(Directory.GetCurrentDirectory(), ".bench", "checks-last-run.txt");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--terminal-relay")) { Program.Checks(args); return 0; }
        if (args.SequenceEqual(["--tree-fixture"])) return RunnerChecks.TreeFixture();
        if (args.SequenceEqual(["--tree-fixture-leaf"])) return RunnerChecks.TreeFixtureLeaf();
        RunnerOptions options;
        try { options = RunnerOptions.Parse(args, Environment.GetEnvironmentVariable(JobShardVariable)); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        if (Environment.GetEnvironmentVariable(OwnerVariable) is { Length: > 0 }) ProcessTree.LeadGroup();
        else
        {
            IdleSleep.Hold();
            ProcessTree.Own();
        }
        if (options.Lanes > 1) return RunLanes(options);

        IReadOnlyList<Unfinished>? repair = null;
        if (options.LastFailed)
        {
            repair = File.Exists(LastRun) ? [.. File.ReadAllLines(LastRun).Where(line => line.Length > 0).Select(Unfinished.Parse)] : [];
            if (repair.Count == 0) { Console.WriteLine("The last run left nothing unfinished."); return 0; }
        }
        Gate.Configure(new(options.Shard ?? options.Job ?? Shard.Whole, repair, options.Cases), options.List);
        var gate = options.Rest.Length == 0 && !options.List;
        try
        {
            if (RunnerChecks.FixtureGate is { } fixture) fixture(); else Program.Checks(options.Rest);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (gate) Record(Gate.Remaining, options.Cases is null);
            Environment.Exit(1);
        }
        if (gate) Record([], options.Cases is null);
        return 0;
    }

    // A lane reports to its owner; a run that named its cases says nothing about the rest of the gate.
    private static void Record(IReadOnlyList<Unfinished> unfinished, bool whole)
    {
        if (Environment.GetEnvironmentVariable(ResultVariable) is { Length: > 0 } result)
            File.WriteAllLines(result, [.. Gate.Passed.Select(name => "pass " + name), .. unfinished.Select(entry => "unfinished " + entry)]);
        else if (whole) WriteLastRun(unfinished);
    }

    private static void WriteLastRun(IReadOnlyList<Unfinished> unfinished)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastRun)!);
        File.WriteAllLines(LastRun, unfinished.Select(entry => entry.ToString()));
    }

    /// <summary>
    /// Runs the gate as independent processes, each owning one shard and its own fixture directory, and
    /// merges their results into one verdict and one last-run record.
    /// </summary>
    private static int RunLanes(RunnerOptions options)
    {
        var results = Path.Combine(Directory.GetCurrentDirectory(), ".bench", "check-lanes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);
        var self = Environment.ProcessPath!;
        var output = new object();
        var lanes = Enumerable.Range(1, options.Lanes).Select(index =>
        {
            var shard = new Shard(index, options.Lanes).Within(options.Job ?? Shard.Whole);
            var start = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--shard"); start.ArgumentList.Add(shard.ToString());
            if (options.Cases is not null) { start.ArgumentList.Add("--case"); start.ArgumentList.Add(string.Join(',', options.Cases)); }
            if (options.List) start.ArgumentList.Add("--list-cases");
            start.Environment[OwnerVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            start.Environment[ResultVariable] = Path.Combine(results, $"lane-{index}.txt");
            start.Environment.Remove(JobShardVariable);
            var process = Process.Start(start)!;
            void Relay(object sender, DataReceivedEventArgs line)
            {
                if (line.Data is not null) lock (output) Console.WriteLine($"[lane {index}] {line.Data}");
            }
            process.OutputDataReceived += Relay; process.ErrorDataReceived += Relay;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return (Index: index, Shard: shard, Process: process, Result: start.Environment[ResultVariable]!);
        }).ToArray();

        var passed = new List<string>();
        var unfinished = new List<Unfinished>();
        foreach (var lane in lanes)
        {
            lane.Process.WaitForExit();
            var lines = File.Exists(lane.Result) ? File.ReadAllLines(lane.Result) : [];
            passed.AddRange(lines.Where(line => line.StartsWith("pass ", StringComparison.Ordinal)).Select(line => line[5..]));
            var left = lines.Where(line => line.StartsWith("unfinished ", StringComparison.Ordinal)).Select(line => Unfinished.Parse(line[11..])).ToList();
            // A lane that died without reporting still owes its whole shard.
            if (lane.Process.ExitCode != 0 && left.Count == 0 && !options.List) left.Add(new(0, lane.Shard, Unfinished.Setup));
            foreach (var entry in left) Console.WriteLine($"FAIL lane {lane.Index} (shard {lane.Shard}) at {entry.Name}");
            unfinished.AddRange(left);
            lane.Process.Dispose();
        }
        Directory.Delete(results, true);
        var repeated = passed.GroupBy(name => name).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (repeated.Length > 0) Console.WriteLine("FAIL cases ran in more than one lane: " + string.Join(", ", repeated));
        if (options.List) return 0;
        if (options.Cases is null) WriteLastRun(unfinished);
        if (unfinished.Count > 0 || repeated.Length > 0) return 1;
        Console.WriteLine($"PASS {passed.Count} cases across {options.Lanes} lanes");
        return 0;
    }
}