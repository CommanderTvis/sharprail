using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpRail.Checks;

/// <summary>Checks of the runner itself: case selection, lanes, the repair loop, the idle-sleep assertion and interrupt cleanup.</summary>
internal static class RunnerChecks
{
    private const string FixtureCases = "SHARPRAIL_CHECKS_FIXTURE_CASES", FixtureFailure = "SHARPRAIL_CHECKS_FIXTURE_FAIL";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    /// <summary>A stand-in gate of numbered cases, so lanes and repair runs are exercised through the real runner without the real suite.</summary>
    internal static Action? FixtureGate => int.TryParse(Environment.GetEnvironmentVariable(FixtureCases), out var count)
        ? () =>
        {
            var failing = Environment.GetEnvironmentVariable(FixtureFailure);
            if (failing == Unfinished.Setup) throw new InvalidOperationException("Fixture setup failed.");
            for (var index = 0; index < count; index++)
            {
                var name = "c" + index.ToString(CultureInfo.InvariantCulture);
                Gate.Case(name, () =>
                {
                    if (name == failing) throw new InvalidOperationException("Fixture case failed: " + name);
                    Console.WriteLine("RAN " + name);
                });
            }
        }
    : null;

    internal static void Run(string root)
    {
        CheckSelection();
        CheckOptions();
        CheckLanes(Path.Combine(root, "runner"));
        IdleSleepAssertion();
        Interrupt();
        Packaged(Path.Combine(root, "packaged"));
        Console.WriteLine("PASS runner: shard composition, lanes, last-failed repair, idle-sleep assertion, interrupt cleanup and packaged-file identity");
    }

    private static void Packaged(string directory)
    {
        var shipped = Path.Combine(directory, "SharpRail.app", "Contents", "MacOS");
        var staged = Path.Combine(directory, "stage");
        foreach (var folder in new[] { shipped, staged })
        {
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "SharpRail.UI.dll"), "SharpRail.UI 1.0");
            File.WriteAllText(Path.Combine(folder, "Framework.Abstractions.dll"), "Framework.Abstractions 8.0");
            File.WriteAllText(Path.Combine(folder, "nested", "resource.txt"), "resource");
        }
        File.WriteAllText(Path.Combine(staged, "SharpRail.Checks.dll"), "SharpRail.Checks 1.0");
        string[] loaded = [Path.Combine(staged, "SharpRail.UI.dll")];
        // A fixture assembly is its name and version in text.
        static AssemblyName? Identity(string file) => File.ReadAllText(file).Split(' ') is [var name, var version] && Version.TryParse(version, out var parsed) ? new(name) { Version = parsed } : null;
        void Stage(string name, string text) => File.WriteAllText(Path.Combine(staged, name), text);
        void Rejects(string message, IEnumerable<string>? assemblies = null)
        {
            try { PackagedApp.Verify(shipped, staged, assemblies ?? loaded, Identity); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
        }
        Require(PackagedApp.Verify(shipped, staged, loaded, Identity) is { Identical: 3, Superseded.Count: 0 }, "A stage holding the bundle's own files must pass.");
        Rejects("A product assembly loaded from elsewhere must fail.", [Path.Combine(directory, "elsewhere", "SharpRail.UI.dll")]);
        Rejects("An assembly the bundle does not ship must not count as the product.", [Path.Combine(staged, "SharpRail.Checks.dll")]);
        Stage("Framework.Abstractions.dll", "Framework.Abstractions 10.0");
        Require(PackagedApp.Verify(shipped, staged, loaded, Identity) is { Identical: 2, Superseded: ["Framework.Abstractions.dll"] },
            "A newer version of a shipped framework assembly may stand in for it, by name.");
        Stage("Framework.Abstractions.dll", "Framework.Abstractions 7.0");
        Rejects("An older framework assembly must fail.");
        Stage("Framework.Abstractions.dll", "Framework.Other 10.0");
        Rejects("A different assembly under a shipped name must fail.");
        Stage("Framework.Abstractions.dll", "Framework.Abstractions 8.0");
        Stage("SharpRail.UI.dll", "SharpRail.UI 2.0");
        Rejects("A product assembly that differs from the bundle must fail, whatever its version.");
        Stage("SharpRail.UI.dll", "SharpRail.UI 1.0");
        Stage(Path.Combine("nested", "resource.txt"), "edited");
        Rejects("A shipped file that is not an assembly must be identical.");
        File.Delete(Path.Combine(staged, "nested", "resource.txt"));
        Rejects("A shipped file missing from the stage must fail.");
        Directory.Delete(shipped, true);
        Rejects("A missing bundle must fail.");
    }

    private static void CheckSelection()
    {
        foreach (var (jobs, lanes) in new[] { (1, 1), (1, 4), (3, 1), (2, 3), (4, 2) })
            for (var ordinal = 0; ordinal < 60; ordinal++)
            {
                var owners = Enumerable.Range(1, jobs).SelectMany(job => Enumerable.Range(1, lanes).Select(lane => new Shard(lane, lanes).Within(new(job, jobs))))
                    .Where(shard => shard.Total == jobs * lanes && shard.Owns(ordinal));
                Require(owners.Count() == 1, $"Case {ordinal} must run exactly once across {jobs} jobs of {lanes} lanes.");
            }
        foreach (var text in new[] { "0/2", "3/2", "1", "a/b", "1/0", "-1/2", "1/2/3" })
            try { Shard.Parse(text); throw new InvalidOperationException("Accepted the shard " + text); }
            catch (ArgumentException) { }

        int[] Chosen(Selection selection) => [.. Enumerable.Range(0, 10).Where(ordinal => selection.Selects("c" + ordinal, ordinal))];
        var second = new Selection(new(2, 3));
        Require(Chosen(second).SequenceEqual([1, 4, 7]), "A shard must own every third case.");
        var afterFour = second.Remaining((4, "c4"));
        Require(afterFour.SequenceEqual([new Unfinished(4, new(2, 3), "c4")]), "A failed lane owes the failing case and the rest of its shard.");
        var repair = new Selection(Shard.Whole, afterFour);
        Require(Chosen(repair).SequenceEqual([4, 7]), "A repair run must cover only the failing case and what it kept from running.");
        Require(Chosen(new(Shard.Whole, repair.Remaining((7, "c7")))).SequenceEqual([7]), "A repair run that fails later must not repeat what it repaired.");
        var both = new Selection(Shard.Whole, [new(3, new(1, 3), "c3"), new(4, new(2, 3), "c4")]);
        Require(Chosen(both).SequenceEqual([3, 4, 6, 7, 9]), "Unfinished lanes must combine.");
        Require(Chosen(new(Shard.Whole, both.Remaining((6, "c6")))).SequenceEqual([6, 7, 9]), "A failed repair must keep every lane's remainder.");
        Require(Chosen(new(Shard.Whole, second.Remaining(null))).SequenceEqual([1, 4, 7]), "A lane that failed in setup owes its whole shard.");
        Require(Chosen(new(Shard.Whole, Only: new HashSet<string> { "c2", "c9" })).SequenceEqual([2, 9]), "Named cases must select only themselves.");
        Require(Unfinished.Parse(afterFour[0].ToString()) == afterFour[0], "The last-run record must round-trip.");
    }

    private static void CheckOptions()
    {
        static void Rejects(string? job, params string[] args)
        {
            try { RunnerOptions.Parse(args, job); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("The runner accepted: " + string.Join(' ', args) + " with job shard " + job);
        }
        Rejects("1/2", "--shard", "1/2");
        Rejects(null, "--lanes", "2", "--shard", "1/2");
        Rejects(null, "--lanes", "0");
        Rejects(null, "--lanes", "17");
        Rejects(null, "--lanes");
        Rejects(null, "--lanes", "2", "--terminals");
        Rejects(null, "--last-failed", "--lanes", "2");
        Rejects("2/3", "--last-failed");
        Rejects("3/2");
        var plain = RunnerOptions.Parse(["--terminals"], null);
        Require(plain is { Lanes: 1, Shard: null, Job: null, LastFailed: false, Cases: null } && plain.Rest.SequenceEqual(["--terminals"]),
            "A focused mode must reach the gate untouched.");
        var child = RunnerOptions.Parse(["--fake-codex", "control.json"], "1/2");
        Require(child is { Job: null } && child.Rest.SequenceEqual(["--fake-codex", "control.json"]),
            "A job's slice must not reach the children its checks start with arguments of their own.");
        var composed = RunnerOptions.Parse(["--lanes", "3"], "2/4");
        Require(composed is { Lanes: 3, Job: { Index: 2, Total: 4 } } && composed.Rest.Length == 0, "A job shard must compose with local lanes.");
        Require(RunnerOptions.Parse(["--lanes", "auto"], null).Lanes == RunnerOptions.AutomaticLanes && RunnerOptions.AutomaticLanes is >= 1 and <= 8,
            "The automatic lane count must stay within 1-8.");
        Require(RunnerOptions.Parse(["--last-failed", "--lanes", "1"], null).LastFailed, "A serial repair run must be accepted.");
    }

    private static (int Code, string[] Ran, string Output) RunGate(string directory, string? failing, string? job, params string[] args)
    {
        var start = Self(args);
        start.WorkingDirectory = directory;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.Environment[FixtureCases] = "10";
        if (failing is null) start.Environment.Remove(FixtureFailure); else start.Environment[FixtureFailure] = failing;
        if (job is null) start.Environment.Remove(Runner.JobShardVariable); else start.Environment[Runner.JobShardVariable] = job;
        start.Environment.Remove(Runner.OwnerVariable);
        start.Environment.Remove(Runner.ResultVariable);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var ran = output.Split('\n').Select(line => line.Trim()).Where(line => line.Contains("RAN ", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("RAN ", StringComparison.Ordinal) + 4)..]).Order(StringComparer.Ordinal).ToArray();
        return (process.ExitCode, ran, output + error.GetAwaiter().GetResult());
    }

    private static ProcessStartInfo Self(params string[] args)
    {
        var self = Environment.ProcessPath!;
        var start = new ProcessStartInfo(self);
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    private static void CheckLanes(string directory)
    {
        Directory.CreateDirectory(directory);
        var lastRun = Path.Combine(directory, ".bench", "checks-last-run.txt");
        string[] Names(params int[] ordinals) => [.. ordinals.Select(ordinal => "c" + ordinal)];
        void Expect((int Code, string[] Ran, string Output) run, int code, string[] ran, string message)
            => Require(run.Code == code && run.Ran.SequenceEqual(ran), $"{message} Exit {run.Code}, ran {string.Join(',', run.Ran)}.\n{run.Output}");

        var whole = RunGate(directory, null, null, "--lanes", "3");
        Expect(whole, 0, Names(0, 1, 2, 3, 4, 5, 6, 7, 8, 9), "Three lanes must run every case exactly once.");
        Require(whole.Output.Contains("PASS 10 cases across 3 lanes", StringComparison.Ordinal) && File.ReadAllText(lastRun).Length == 0,
            "A passing lane run must merge into one result and leave nothing unfinished.");
        Require(Enumerable.Range(1, 3).All(lane => whole.Output.Contains($"[lane {lane}] RAN ", StringComparison.Ordinal)), "Every lane must own cases.");
        Require(!Directory.EnumerateDirectories(Path.Combine(directory, ".bench"), "check-lanes-*").Any(), "Lane result files must not outlive the run.");

        var failed = RunGate(directory, "c4", null, "--lanes", "3");
        Expect(failed, 1, Names(0, 1, 2, 3, 5, 6, 8, 9), "A failing lane must stop at its failure while the others finish.");
        Require(File.ReadAllLines(lastRun).SequenceEqual(["4 2/3 c4"]) && failed.Output.Contains("FAIL lane 2 (shard 2/3) at c4", StringComparison.Ordinal),
            "The merged result must record the failing lane's unfinished cases.");
        Expect(RunGate(directory, "c4", null, "--last-failed"), 1, [], "A repair run that still fails must run nothing else.");
        Require(File.ReadAllLines(lastRun).SequenceEqual(["4 2/3 c4"]), "A failed repair must keep the record.");
        Expect(RunGate(directory, null, null, "--last-failed"), 0, Names(4, 7), "The repair run must cover only the failing case and its lane's remainder.");
        Require(File.ReadAllText(lastRun).Length == 0, "A passing repair must clear the record.");
        var nothing = RunGate(directory, null, null, "--last-failed");
        Expect(nothing, 0, [], "A repair run with nothing unfinished must run nothing.");

        Expect(RunGate(directory, "c5", null), 1, Names(0, 1, 2, 3, 4), "The serial gate must stop at its first failure.");
        Expect(RunGate(directory, null, null, "--last-failed"), 0, Names(5, 6, 7, 8, 9), "A serial failure leaves the failing case and everything after it.");
        Expect(RunGate(directory, Unfinished.Setup, null, "--lanes", "2"), 1, [], "A setup failure must fail every lane.");
        Expect(RunGate(directory, null, null, "--last-failed"), 0, Names(0, 1, 2, 3, 4, 5, 6, 7, 8, 9), "Lanes that failed in setup owe their whole shards.");

        Expect(RunGate(directory, null, "2/2", "--lanes", "2"), 0, Names(2, 3, 6, 7), "A job's lanes must subdivide only that job's slice.");
        Expect(RunGate(directory, null, "1/2"), 0, Names(0, 2, 4, 6, 8), "A job shard must select its slice without lanes.");
        Expect(RunGate(directory, null, null, "--shard", "3/4"), 0, Names(2, 6), "An explicit shard must select its slice.");
        Expect(RunGate(directory, null, "1/2", "--shard", "1/2"), 2, [], "An explicit shard must be rejected beside the job shard.");
        Expect(RunGate(directory, "c1", null, "--case", "c8,c3"), 0, Names(3, 8), "Named cases must run alone.");
        Require(File.ReadAllText(lastRun).Length == 0, "A run of named cases must not rewrite the record.");
        var listed = RunGate(directory, null, null, "--list-cases", "--shard", "2/5");
        Require(listed.Code == 0 && listed.Ran.Length == 0 && listed.Output.Contains("CASE c1", StringComparison.Ordinal) && listed.Output.Contains("CASE c6", StringComparison.Ordinal),
            "Listing must name the selected cases without running them.");
    }

    private static void IdleSleepAssertion()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Require(IdleSleep.Hold() is null, "The idle-sleep assertion must be a no-op off macOS.");
            return;
        }
        // A lane reuses its owner's assertion instead of holding another.
        var owner = Environment.GetEnvironmentVariable(Runner.OwnerVariable) is { Length: > 0 } pid ? pid : Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var held = Output("ps", "-axo", "pid=,ppid=,command=").Split('\n').Select(line => line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 3 && fields[1] == owner && fields[2].Trim() == "/usr/bin/caffeinate -i -w " + owner).ToArray();
        Require(held.Length == 1, "The runner must hold exactly one idle-sleep assertion scoped to its own pid.");
        Require(Output("pmset", "-g", "assertions").Split('\n').Any(line => line.Contains($"pid {held[0][0]}(caffeinate)", StringComparison.Ordinal) &&
            line.Contains("PreventUserIdleSystemSleep", StringComparison.Ordinal)), "The assertion must prevent idle system sleep.");
        try
        {
            IdleSleep.Hold("/usr/bin/false");
            throw new InvalidOperationException("An assertion that exits at startup must fail the run.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("exited during startup", StringComparison.Ordinal)) { }
    }

    private static string Output(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    /// <summary>An owning runner with a child in its own group, a grandchild in a lane's group and a child that ignores the signal.</summary>
    internal static int TreeFixture()
    {
        ProcessTree.Own();
        var plain = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 300" } })!;
        var deaf = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "trap '' TERM INT; sleep 300 & wait" } })!;
        var leaf = Self("--tree-fixture-leaf");
        leaf.RedirectStandardOutput = true;
        var lane = Process.Start(leaf)!;
        var nested = lane.StandardOutput.ReadLine();
        Console.WriteLine($"PIDS {plain.Id} {deaf.Id} {lane.Id} {nested}");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int TreeFixtureLeaf()
    {
        ProcessTree.LeadGroup();
        var deaf = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "trap '' TERM INT; exec sleep 300" } })!;
        Console.WriteLine(deaf.Id);
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static void Interrupt()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP interrupt cleanup: POSIX signals"); return; }
        foreach (var (signal, code) in new[] { (PosixSignal.SIGTERM, 143), (PosixSignal.SIGINT, 130) })
        {
            var start = Self("--tree-fixture");
            start.RedirectStandardOutput = true;
            using var owner = Process.Start(start)!;
            var line = owner.StandardOutput.ReadLine();
            Require(line?.StartsWith("PIDS ", StringComparison.Ordinal) == true, "The interrupt fixture did not start: " + line);
            var pids = line![5..].Split(' ').Select(int.Parse).ToArray();
            Require(pids.Length == 4 && ProcessTree.Snapshot().Count(process => pids.Contains(process.Pid)) == 4, "The interrupt fixture's descendants must be running.");
            var before = ProcessTree.Descendants(ProcessTree.Snapshot(), owner.Id);
            bool Alive() => before.Alive(ProcessTree.Snapshot());
            Require(before.Groups.Length == 1 && before.Pids.Length >= 3, "A lane must lead its own group while plain children share the owner's.");
            var sent = Stopwatch.StartNew();
            ProcessTree.Send(owner.Id, signal);
            Require(owner.WaitForExit(ProcessTree.Grace + TimeSpan.FromSeconds(20)), "An interrupted runner must exit.");
            Require(owner.ExitCode == code, $"An interrupted runner must report the signal: {owner.ExitCode}.");
            Require(sent.Elapsed >= ProcessTree.Grace - TimeSpan.FromMilliseconds(200), "Descendants that ignore the signal must get the grace period before being killed.");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Alive() && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Require(!Alive(), "Every descendant of an interrupted runner must be gone, including ones that ignore the signal.");
        }
    }
}