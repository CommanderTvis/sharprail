using System.Diagnostics;
using System.Text;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>
/// The terminals' memory guard: limit calculation, the per-terminal state machine with a fake clock, sampler and killer (warning, stop,
/// kill, tree kill, protect list, agents, total limit, pressure), the process-tree membership, the settings round trip, one real
/// runaway child process under a small limit and the same in a real PTY terminal whose shell survives.
/// </summary>
internal static class TerminalMemoryGuardChecks
{
    private const long Gb = 1L << 30;

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fake : IGuardSampler, IGuardKiller
    {
        internal readonly Dictionary<int, List<GuardedProcess>> Trees = [];
        internal readonly List<string> Signals = [];
        internal bool ObeysTerminate, Pressure;
        public long PhysicalBytes { get; set; } = 32 * Gb;
        public bool UnderPressure() => Pressure;
        public void Refresh() { }
        public IReadOnlyList<GuardedProcess> Tree(int shellPid) => Trees.TryGetValue(shellPid, out var tree) ? [.. tree] : [];
        public void Terminate(GuardedProcess process) { Signals.Add("TERM " + process.Pid); if (ObeysTerminate) Remove(process.Pid); }
        public void Kill(GuardedProcess process) { Signals.Add("KILL " + process.Pid); Remove(process.Pid); }
        private void Remove(int pid) { foreach (var tree in Trees.Values) tree.RemoveAll(process => process.Pid == pid); }
        internal void Set(int shell, params GuardedProcess[] processes) => Trees[shell] = [.. processes];
    }

    private static GuardedProcess P(int pid, string name, double gigabytes, int parent = 10, string? agent = null) => new(pid, name, (long)(gigabytes * Gb), 1, parent, agent);

    private static (TerminalMemoryGuard Guard, Fake Fake, List<GuardEvent> Events, Func<TimeSpan> Now, Action<TimeSpan> Advance) Make(TerminalMemoryLimits limits)
    {
        var fake = new Fake();
        var events = new List<GuardEvent>();
        var now = TimeSpan.Zero;
        var guard = new TerminalMemoryGuard(fake, fake, () => limits, events.Add) { Clock = () => now, OwnPid = 1 };
        return (guard, fake, events, () => now, by => now += by);
    }

    private static TerminalMemoryLimits Limits(int perTerminalMb = 8192, int totalMb = 0, IReadOnlyList<string>? protect = null) =>
        new(true, perTerminalMb, totalMb == 0 ? 1_000_000 : totalMb, 10, protect ?? []);

    internal static async Task Run(string root)
    {
        LimitCalculation();
        Parsing();
        Membership();
        Transitions();
        await Settings(Path.Combine(root, "terminal-memory"));
        await Notices(Path.Combine(root, "terminal-memory-notices"));
        await RealProcess();
        await RealTerminal();
    }

    private static void LimitCalculation()
    {
        Require(TerminalMemoryLimits.AutomaticPerTerminal(4 * Gb) == 2 * Gb, "A small machine is bounded to 2 GiB per terminal.");
        Require(TerminalMemoryLimits.AutomaticPerTerminal(16 * Gb) == 4 * Gb, "A quarter of 16 GiB is 4 GiB.");
        Require(TerminalMemoryLimits.AutomaticPerTerminal(64 * Gb) == 16 * Gb, "A quarter of 64 GiB is 16 GiB.");
        Require(TerminalMemoryLimits.AutomaticPerTerminal(512 * Gb) == 32 * Gb, "A big machine is bounded to 32 GiB per terminal.");
        Require(TerminalMemoryLimits.AutomaticTotal(10 * Gb) == 6 * Gb, "The total limit is 60% of physical memory.");
        Require(TerminalMemoryLimits.Default.PerTerminalBytes(16 * Gb) == 4 * Gb && TerminalMemoryLimits.Default.TotalBytes(10 * Gb) == 6 * Gb, "Zero means automatic.");
        var fixedLimits = TerminalMemoryLimits.Parse("""{"perTerminalMb":100,"totalMb":2048}""");
        Require(fixedLimits.PerTerminalBytes(64 * Gb) == 100L << 20 && fixedLimits.TotalBytes(64 * Gb) == 2 * Gb, "A fixed value wins over the automatic one.");
        Require(TerminalMemoryGuard.Size((long)(7.9 * Gb)) == "7.9 GB" && TerminalMemoryGuard.Size(8 * Gb) == "8 GB" && TerminalMemoryGuard.Size((long)(12.3 * Gb)) == "12 GB", "Sizes read like '7.9 GB', '8 GB'.");
    }

    private static void Parsing()
    {
        var parsed = TerminalMemoryLimits.Parse("""{"enabled":false,"perTerminalMb":4096,"graceSeconds":3,"protect":["Postgres"," mysqld ","postgres"]}""");
        Require(!parsed.Enabled && parsed.PerTerminalMb == 4096 && parsed.GraceSeconds == 3 && parsed.Protect.SequenceEqual(["Postgres", "mysqld"]), "The limits were not parsed: " + parsed.Serialize());
        Require(parsed.Protects("POSTGRES") && !parsed.Protects("node"), "The protect list compares names without case.");
        Require(TerminalMemoryLimits.Parse(parsed.Serialize()) is { } again && again.Serialize() == parsed.Serialize(), "The limits do not round trip.");
        Require(TerminalMemoryLimits.Parse("") == TerminalMemoryLimits.Default && TerminalMemoryLimits.Default.Enabled, "Empty means the defaults, on.");
        foreach (var bad in new[] { "not json", "[]", """{"perTerminalMb":10}""", """{"totalMb":-5}""", """{"graceSeconds":0}""", """{"protect":["a/b"]}""", """{"protect":[""]}""" })
            try { TerminalMemoryLimits.Parse(bad); Require(false, "Accepted " + bad); }
            catch (ArgumentException) { }
    }

    private static void Membership()
    {
        static ProcessEntry E(int pid, int ppid, long group, string name = "x") => new(pid, ppid, group, name, 1);
        // macOS: terminal devices. A build server that was reparented to init keeps the terminal and is still the terminal's.
        var table = new[] { E(1, 0, 0), E(10, 1, 55, "zsh"), E(11, 10, 55), E(12, 11, 55), E(99, 1, 55, "VBCSCompiler"), E(100, 1, 0), E(101, 100, 77) };
        Require(ProcessEntries.Members(table, 10).Select(entry => entry.Pid).Order().SequenceEqual([11, 12, 99]), "A terminal's processes are its descendants and the processes on its terminal.");
        // Linux: the group is a session id, claimed only by a shell that leads its own session.
        var sessions = new[] { E(1, 0, 1), E(10, 1, 10), E(11, 10, 10), E(98, 1, 10), E(50, 1, 50), E(51, 50, 50), E(20, 1, 1) };
        Require(ProcessEntries.Members(sessions, 10, sessionIds: true).Select(entry => entry.Pid).Order().SequenceEqual([11, 98]), "Linux claims the shell's own session.");
        Require(ProcessEntries.Members(sessions, 20, sessionIds: true).Count == 0, "A shell that does not lead its session claims no session, or the host's own processes would join.");
        var stat = ProcessEntries.ParseLinuxStat(42, "42 (tmux: server (x)) S 7 42 42 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 987654 1000 100 18446744073709551615");
        Require(stat is { Pid: 42, Ppid: 7, Group: 42, Name: "tmux: server (x)", Started: 987654 }, "Parsing /proc stat with parentheses in the name: " + stat);
        if (OperatingSystem.IsWindows()) return;
        var own = ProcessEntries.Read().FirstOrDefault(entry => entry.Pid == Environment.ProcessId);
        Require(own.Pid == Environment.ProcessId, "The sampler does not see this process.");
        var parent = RunText("ps", "-o", "ppid=", "-p", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Require(own.Ppid.ToString(System.Globalization.CultureInfo.InvariantCulture) == parent.Trim(), $"The parent pid {own.Ppid} read natively differs from ps's {parent.Trim()}.");
        Require(own.Name.Length > 0 && own.Started > 0, "The sampler reads no name or start time.");
        Require(ProcessEntries.Bytes(Environment.ProcessId) is > 10_000_000, "The sampler reads no memory for this process.");
    }

    private static string RunText(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return text;
    }

    private static void Transitions()
    {
        // Warning: once per crossing of 80%, silent again below 70%, and again on the next crossing.
        {
            var (guard, fake, events, _, _) = Make(Limits());
            GuardTarget[] targets = [new("s", "Terminal in app", 10)];
            fake.Set(10, P(10, "zsh", 0.1), P(11, "node", 3));
            guard.Tick(targets);
            Require(events.Count == 0 && guard.UsageOf("s") is { State: GuardState.Ok }, "Under 80% nothing is said.");
            fake.Set(10, P(11, "node", 7.9));
            guard.Tick(targets); guard.Tick(targets);
            Require(events is [{ Kind: GuardEventKind.Warned, Text: "Terminal in app uses 7.9 GB of 8 GB: node" }], "The warning is one line naming the terminal and the largest process: " + string.Join("|", events.Select(e => e.Text)));
            Require(events[0].Banner == "Memory guard warns: this terminal uses 7.9 GB of its 8 GB limit, most of it node", "The warning's banner line: " + events[0].Banner);
            Require(guard.UsageOf("s") is { State: GuardState.Warned, Bytes: > 0 } warned && warned.Limit == 8 * Gb, "The usage shows the warned state against the limit.");
            fake.Set(10, P(11, "node", 6.0));
            guard.Tick(targets);
            Require(guard.UsageOf("s")!.Value.State == GuardState.Warned, "Between 70% and 80% the warning stays (hysteresis).");
            fake.Set(10, P(11, "node", 2));
            guard.Tick(targets);
            Require(guard.UsageOf("s")!.Value.State == GuardState.Ok, "Under 70% the terminal is Ok again.");
            fake.Set(10, P(11, "node", 7));
            guard.Tick(targets);
            Require(events.Count == 2, "A new crossing warns again.");
            Require(fake.Signals.Count == 0, "A warning ends nothing.");
        }
        // Over the limit: the largest process is asked to stop, killed after the grace, the shell is never touched.
        {
            var (guard, fake, events, _, advance) = Make(Limits());
            GuardTarget[] targets = [new("s", "Terminal in app", 10)];
            fake.Set(10, P(10, "zsh", 9.5), P(11, "node", 9.2), P(12, "git", 0.5));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11"]), "The largest descendant, not the shell, is asked to stop: " + string.Join(",", fake.Signals));
            Require(events[0].Text == "tereditor memory guard: stopped node (pid 11, 9.2 GB) because this terminal uses 9.7 GB, over its limit of 8 GB", "The notice text: " + events[0].Text);
            Require(events[0].Banner == "Memory guard stopped node (pid 11, 9.2 GB): this terminal used 9.7 GB, over its limit of 8 GB", "The banner line: " + events[0].Banner);
            Require(guard.UsageOf("s")!.Value.State == GuardState.Terminating, "The terminal is Terminating.");
            advance(TimeSpan.FromSeconds(9)); guard.Tick(targets);
            Require(fake.Signals.Count == 1, "Nothing more happens within the grace period.");
            advance(TimeSpan.FromSeconds(2)); guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11", "KILL 11"]) && events[1].Kind == GuardEventKind.Killed && guard.UsageOf("s")!.Value.State == GuardState.Killing, "After the grace the process is killed: " + string.Join(",", fake.Signals));
            Require(events[1].Text.StartsWith("tereditor memory guard: killed node (pid 11, 9.2 GB) because it did not stop within 10 s and this terminal uses", StringComparison.Ordinal), "The kill notice says who and why: " + events[1].Text);
            Require(events[1].Banner.StartsWith("Memory guard killed node (pid 11, 9.2 GB): it did not stop within 10 s, and this terminal used", StringComparison.Ordinal), "The kill's banner line: " + events[1].Banner);
            guard.Tick(targets);
            Require(guard.UsageOf("s")!.Value.State == GuardState.Ok && fake.Signals.Count == 2, "Once under the limit the terminal is Ok and the rest is left alone.");
        }
        // A process that obeys the stop request is not killed.
        {
            var (guard, fake, _, _, advance) = Make(Limits());
            fake.ObeysTerminate = true;
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "node", 9));
            guard.Tick(targets); advance(TimeSpan.FromSeconds(30)); guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11"]) && guard.UsageOf("s")!.Value.State == GuardState.Ok, "A process that exits when asked is not killed.");
        }
        // Still over a cycle after the kill: everything below the shell goes, except protected programs.
        {
            var (guard, fake, events, _, advance) = Make(Limits(protect: ["postgres"]));
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "node", 5), P(12, "rustc", 4.5), P(13, "cc", 4), P(14, "postgres", 1));
            guard.Tick(targets);
            advance(TimeSpan.FromSeconds(11)); guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11", "KILL 11"]), "First the largest: " + string.Join(",", fake.Signals));
            guard.Tick(targets);
            Require(events.Last().Kind == GuardEventKind.TreeKilled && fake.Signals.Contains("KILL 12") && fake.Signals.Contains("KILL 13") && !fake.Signals.Contains("KILL 14"), "The rest of the tree is killed, the protected program is not: " + string.Join(",", fake.Signals));
        }
        // Protect list: the biggest process is protected, so it is only warned about, once.
        {
            var (guard, fake, events, _, advance) = Make(Limits(protect: ["Postgres"]));
            GuardTarget[] targets = [new("s", "Database", 10)];
            fake.Set(10, P(11, "postgres", 9), P(12, "node", 1));
            guard.Tick(targets); guard.Tick(targets); advance(TimeSpan.FromMinutes(5)); guard.Tick(targets);
            Require(fake.Signals.Count == 0 && events is [{ Kind: GuardEventKind.Protected }], "A protected program is never ended and the warning is not repeated: " + string.Join(",", fake.Signals));
        }
        // Agents: the agent's heaviest subprocess is stopped, before any other program and never the agent while one is left.
        {
            var (guard, fake, events, _, _) = Make(Limits(perTerminalMb: 4096));
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "claude", 3), P(12, "python", 2), P(13, "cargo", 0.5, parent: 11), P(14, "rustc", 1, parent: 13));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 14"]), "The agent's heaviest descendant is stopped, not the agent or an unrelated program: " + string.Join(",", fake.Signals));
            Require(events[0].Text.StartsWith("tereditor memory guard: stopped rustc (pid 14, 1 GB) because", StringComparison.Ordinal), "The notice names the subprocess: " + events[0].Text);
        }
        {
            var (guard, fake, _, _, _) = Make(Limits());
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "claude", 7), P(12, "node", 1.5, parent: 11));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 12"]), "An agent far larger than its subprocess is still passed over while a subprocess is left: " + string.Join(",", fake.Signals));
        }
        // Last resort: an agent alone over the limit is ended; a native Claude Code is recognised by its version-named binary.
        {
            var (guard, fake, events, _, _) = Make(Limits(perTerminalMb: 4096));
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "2.1.323", 5, agent: "claude"));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11"]) && events[0].Text.StartsWith("tereditor memory guard: stopped claude (pid 11, 5 GB)", StringComparison.Ordinal), "An agent alone over the limit is ended, named as the user knows it: " + string.Join(",", events.Select(e => e.Text)));
        }
        // An agent over the limit by itself is asked to stop at once: ending its small subprocess could not help.
        {
            var (guard, fake, _, _, _) = Make(Limits());
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "claude", 9), P(12, "node", 0.1, parent: 11));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11"]), "An agent alone over the limit gets the stop request, not a later tree kill: " + string.Join(",", fake.Signals));
        }
        // An agent under the limit by itself with nothing else to end is only warned about, once.
        {
            var (guard, fake, events, _, _) = Make(Limits(perTerminalMb: 4096, protect: ["postgres"]));
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "claude", 3.5), P(12, "postgres", 1));
            guard.Tick(targets); guard.Tick(targets);
            Require(fake.Signals.Count == 0 && events is [{ Kind: GuardEventKind.Protected }] && events[0].Text.Contains("claude is an agent", StringComparison.Ordinal), "An agent under the limit is not ended: " + string.Join("|", events.Select(e => e.Text)));
        }
        // The tree kill spares an agent that is under the limit by itself.
        {
            var (guard, fake, events, _, advance) = Make(Limits(perTerminalMb: 4096));
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, P(11, "claude", 3), P(12, "node", 3, parent: 11), P(13, "cc", 2, parent: 11));
            guard.Tick(targets);
            advance(TimeSpan.FromSeconds(11)); guard.Tick(targets);
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 12", "KILL 12", "KILL 13"]) && events.Last() is { Kind: GuardEventKind.TreeKilled } last && last.Text.Contains("sparing claude", StringComparison.Ordinal), "The agent survives the tree kill: " + string.Join(",", fake.Signals) + " " + events.Last().Text);
        }
        Require(TerminalMemoryGuard.AgentOf("2.1.323", "/Users/u/.local/share/claude/versions/2.1.323") == "claude" && TerminalMemoryGuard.AgentOf("codex", null) == "codex"
            && TerminalMemoryGuard.AgentOf("node", "/usr/local/bin/node") is null && TerminalMemoryGuard.AgentOf("x", "/home/u/.local/share/claude/versions/2.0.1 (deleted)") == "claude", "Agents are recognised by name or executable path.");
        Require(TerminalMemoryGuard.AgentOf("pi", null) == "pi" && TerminalMemoryGuard.AgentOf("x", "/Users/u/.local/bin/pi") == "pi" && TerminalMemoryGuard.AgentOf("goose", null) == "goose"
            && TerminalMemoryGuard.AgentOf("x", @"C:\Users\u\goose-package\goose.exe") == "goose" && TerminalMemoryGuard.AgentOf("pip", "/usr/bin/pip") is null, "Pi and the ACP registry's goose are agents too, by name or executable path.");
        // Pi in a terminal is spared like Claude Code: its build is stopped, not Pi.
        {
            var (guard, fake, _, _, _) = Make(Limits(perTerminalMb: 4096));
            fake.Set(10, P(11, "pi", 3), P(12, "cargo", 2, parent: 11));
            guard.Tick([new("s", "t", 10)]);
            Require(fake.Signals.SequenceEqual(["TERM 12"]), "Pi's subprocess is stopped, not Pi: " + string.Join(",", fake.Signals));
        }
        // The host's own pid and a pid below another terminal are never victims of this terminal.
        {
            var (guard, fake, _, _, _) = Make(Limits(perTerminalMb: 4096));
            fake.Set(10, P(1, "tereditor", 20), P(11, "node", 5));
            guard.Tick([new("s", "t", 10)]);
            Require(fake.Signals.SequenceEqual(["TERM 11"]), "The host process is never a victim: " + string.Join(",", fake.Signals));
        }
        // Total limit: the biggest terminal pays, not everyone.
        {
            var (guard, fake, events, _, _) = Make(Limits(totalMb: 8192));
            GuardTarget[] targets = [new("a", "A", 10), new("b", "B", 20)];
            fake.Set(10, P(11, "node", 4));
            fake.Set(20, P(21, "java", 5));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 21"]) && events[0].Text.EndsWith("all terminals use 9 GB, over their total limit of 8 GB", StringComparison.Ordinal), "The biggest terminal's biggest process is stopped: " + string.Join(",", fake.Signals));
        }
        // System memory pressure: only a terminal using at least half its limit is touched.
        {
            var (guard, fake, events, _, _) = Make(Limits());
            fake.Pressure = true;
            GuardTarget[] targets = [new("a", "A", 10)];
            fake.Set(10, P(11, "vim", 0.6));
            guard.Tick(targets);
            Require(fake.Signals.Count == 0, "Pressure alone does not end a small program.");
            fake.Set(10, P(11, "node", 5));
            guard.Tick(targets);
            Require(fake.Signals.SequenceEqual(["TERM 11"]) && events.Last().Text.EndsWith("the system is under memory pressure", StringComparison.Ordinal), "Pressure ends the biggest offender: " + string.Join(",", events.Select(e => e.Text)));
            Require(events.Last().Banner == "Memory guard stopped node (pid 11, 5 GB): the system was under memory pressure", "The pressure banner line: " + events.Last().Banner);
        }
        // Off: nothing is sampled or ended.
        {
            var fake = new Fake();
            var guard = new TerminalMemoryGuard(fake, fake, () => TerminalMemoryLimits.Default with { Enabled = false }, _ => { });
            fake.Set(10, P(11, "node", 99));
            guard.Tick([new("s", "t", 10)]);
            Require(fake.Signals.Count == 0 && guard.UsageOf("s") is null, "A disabled guard does nothing.");
        }
        // A pid that was reused is not the sampled process.
        {
            var (guard, fake, _, _, advance) = Make(Limits());
            GuardTarget[] targets = [new("s", "t", 10)];
            fake.Set(10, new GuardedProcess(11, "node", 9 * Gb, 1));
            guard.Tick(targets);
            fake.Set(10, new GuardedProcess(11, "other", 9 * Gb, 2));
            advance(TimeSpan.FromSeconds(11)); guard.Tick(targets);
            Require(!fake.Signals.Take(1).Contains("KILL 11") && fake.Signals.Count(signal => signal.StartsWith("KILL", StringComparison.Ordinal)) == 0, "A reused pid is not killed as the victim: " + string.Join(",", fake.Signals));
        }
    }

    private static async Task Settings(string directory)
    {
        var state = new HostStateStore(directory);
        Require(state.Current.Settings.TerminalMemory == "", "The setting starts empty, the defaults.");
        await state.ChangeAsync([HostStateChange.Setting("terminal-memory", """{"perTerminalMb":4096,"protect":["postgres"]}""")]);
        Require(state.Current.Settings.TerminalMemory == """{"enabled":true,"perTerminalMb":4096,"totalMb":0,"graceSeconds":10,"protect":["postgres"]}""", "Stored normalized: " + state.Current.Settings.TerminalMemory);
        try { await state.ChangeAsync([HostStateChange.Setting("terminal-memory", """{"perTerminalMb":3}""")]); Require(false, "A tiny limit was accepted."); }
        catch (ArgumentException) { }
        var reopened = new HostStateStore(directory);
        Require(reopened.Current.Settings.TerminalMemory.Contains("\"perTerminalMb\":4096", StringComparison.Ordinal), "The setting did not survive a restart.");
        await reopened.ChangeAsync([HostStateChange.Setting("terminal-memory", "")]);
        Require(reopened.Current.Settings.TerminalMemory == "", "An empty value resets to the defaults.");
    }

    // The tab's banner in the shared state: notices collapse, a dismissal clears what the client saw for every client, a newer notice
    // survives a stale dismissal, the next notice shows the banner again counting from one, and an ended terminal takes its banner along.
    private static async Task Notices(string directory)
    {
        var state = new HostStateStore(directory);
        var tab = new Tereditor.Plugins.Api.TerminalRef(directory, "guard-tab");
        var other = new Tereditor.Plugins.Api.TerminalRef(directory, "other-tab");
        GuardEvent Stop(int pid) => new("s", GuardEventKind.Stopped, $"tereditor memory guard: stopped cc (pid {pid}, 1 GB) because the system is under memory pressure",
            DateTimeOffset.UtcNow, $"Memory guard stopped cc (pid {pid}, 1 GB): the system was under memory pressure");
        TerminalGuardNotice? Of(Tereditor.Plugins.Api.TerminalRef terminal) => state.Current.GuardNotices.SingleOrDefault(notice => notice.Terminal == terminal);
        state.NoteGuard(tab, Stop(1));
        Require(Of(tab) is { Count: 1, Text: "Memory guard stopped cc (pid 1, 1 GB): the system was under memory pressure", Lines.Count: 1 }, "One notice is the banner as it is.");
        state.NoteGuard(tab, Stop(2));
        state.NoteGuard(tab, Stop(3));
        state.NoteGuard(other, Stop(4));
        Require(Of(tab) is { Count: 3, Lines.Count: 3 } three && three.Text == "Memory guard stopped 3 processes in this terminal. Latest: stopped cc (pid 3, 1 GB): the system was under memory pressure"
            && Of(other) is { Count: 1 }, "Repeated notices collapse per tab: " + Of(tab)?.Text);
        for (var pid = 5; pid < 5 + TerminalGuardNotice.MaxLines; pid++) state.NoteGuard(tab, Stop(pid));
        Require(Of(tab) is { Count: 3 + TerminalGuardNotice.MaxLines } many && many.Lines.Count == TerminalGuardNotice.MaxLines && many.Lines[^1].Contains("pid 24,", StringComparison.Ordinal),
            "The list keeps the newest notices and the count keeps them all.");
        var seen = Of(tab)!.Sequence;
        state.NoteGuard(tab, Stop(99));
        await state.ChangeAsync([HostStateChange.DismissGuardNotice(tab, seen)]);
        Require(Of(tab) is { Sequence: > 0 } kept && kept.Sequence > seen, "A dismissal of an older notice keeps the newer one the client has not shown.");
        await state.ChangeAsync([HostStateChange.DismissGuardNotice(tab, Of(tab)!.Sequence)]);
        Require(Of(tab) is null && Of(other) is not null, "A dismissal clears that tab's banner only.");
        state.NoteGuard(tab, Stop(100));
        Require(Of(tab) is { Count: 1, Lines.Count: 1 }, "A new action after a dismissal shows the banner again, counting from one.");
        state.NoteGuard(tab, new("s", GuardEventKind.Warned, "tereditor memory guard: cc uses 1 GB", DateTimeOffset.UtcNow, "Memory guard warns: cc uses 1 GB"));
        Require(Of(tab) is { Count: 1, Lines.Count: 2, Text: "Memory guard warns: cc uses 1 GB" }, "A warning shows but does not count as a stop.");
        state.NoteGuard(tab, null);
        Require(Of(tab) is null, "An ended terminal takes its banner along.");
        Require(!File.ReadAllText(Path.Combine(directory, "state.json")).Contains("Memory guard", StringComparison.Ordinal), "Guard notices are never persisted.");
    }

    private static string? Perl() => OperatingSystem.IsWindows() ? null : new[] { "/usr/bin/perl", "/usr/local/bin/perl", "/bin/perl" }.FirstOrDefault(File.Exists);

    // A real runaway: a child of a throwaway shell allocates 300 MB under a 100 MB limit and is ended; the shell lives on.
    private static async Task RealProcess()
    {
        if (Perl() is not { } perl) { Console.WriteLine("SKIP terminal memory guard real process: no perl."); return; }
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"{perl} -e '$x = \"a\" x (300 << 20); sleep 60' & wait; sleep 120");
        using var shell = Process.Start(start)!;
        try
        {
            var sampler = new NativeGuardSampler();
            var events = new List<GuardEvent>();
            var limits = new TerminalMemoryLimits(true, 100, 0, 1, []);
            var guard = new TerminalMemoryGuard(sampler, sampler, () => limits, events.Add);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!events.Any(e => e.Kind is GuardEventKind.Stopped))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The guard did not stop the runaway child.");
                guard.Tick([new("real", "Real", shell.Id)]);
                await Task.Delay(100);
            }
            Require(events[0].Text.StartsWith("tereditor memory guard: stopped perl (pid ", StringComparison.Ordinal) && events[0].Text.EndsWith("over its limit of 0.1 GB", StringComparison.Ordinal), "The notice names the child: " + events[0].Text);
            while (sampler.Tree(shell.Id).Any(process => process.Name == "perl") && DateTime.UtcNow < deadline) { guard.Tick([new("real", "Real", shell.Id)]); sampler.Refresh(); await Task.Delay(100); }
            sampler.Refresh();
            Require(sampler.Tree(shell.Id).All(process => process.Name != "perl"), "The runaway child is still running.");
            Require(!shell.HasExited, "The shell was ended with its child.");
        }
        finally { if (!shell.HasExited) shell.Kill(true); }
    }

    // The same in a real PTY terminal: the notice appears in the tab's banner, never in its output, and the shell answers afterwards.
    private static async Task RealTerminal()
    {
        if (Perl() is not { } perl) { Console.WriteLine("SKIP terminal memory guard PTY: no perl."); return; }
        var directory = Directory.CreateTempSubdirectory("tereditor-guard").FullName;
        try
        {
            await using var pty = new PtyTerminalService("/bin/sh", terminalMemory: () => """{"perTerminalMb":100,"graceSeconds":1}""") { GuardInterval = TimeSpan.FromMilliseconds(200) };
            var state = new HostStateStore(Path.Combine(directory, "state")) { Terminals = pty };
            var tab = new Tereditor.Plugins.Api.TerminalRef(directory, "guard-tab");
            var attached = await pty.AttachAsync(new(PtyTerminalService.SessionFor(tab), directory, "client", 100, 30) { TabKey = tab.TabKey });
            var text = new StringBuilder(Encoding.UTF8.GetString(attached.Replay.Span));
            _ = Task.Run(async () =>
            {
                try { await foreach (var chunk in attached.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
                catch (Exception) { }
            });
            async Task Await(string expected, string what)
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    lock (text) if (text.ToString().Contains(expected, StringComparison.Ordinal)) return;
                    if (DateTime.UtcNow > deadline) { lock (text) throw new TimeoutException($"Timed out waiting for {what}: {text}"); }
                    await Task.Delay(50);
                }
            }
            // The shell integration's marks around a foreground command, as zsh and bash print them: neither while the command runs nor
            // after it ends does the guard write into the output; the tab's banner in the shared state says what it did.
            await attached.WriteAsync(Encoding.UTF8.GetBytes($"printf '\\033]133;C\\007'; {perl} -e '$x = \"a\" x (300 << 20); sleep 60'; printf '\\033]133;D;0\\007'\r"));
            var until = DateTime.UtcNow.AddSeconds(60);
            while (state.Current.GuardNotices.SingleOrDefault(notice => notice.Terminal == tab) is not { } notice || !notice.Text.StartsWith("Memory guard stopped perl (pid ", StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("The tab's banner never named the stop: " + string.Join("|", state.Current.GuardNotices.Select(item => item.Text)));
                await Task.Delay(50);
            }
            Require(state.Current.GuardNotices.Single(notice => notice.Terminal == tab).Text.EndsWith("over its limit of 0.1 GB", StringComparison.Ordinal), "The banner says why.");
            await Await("\u001b]133;D;0\u0007", "the command to end");
            await attached.WriteAsync(Encoding.UTF8.GetBytes("echo SHELL_ALIVE_$((6*7))\r"));
            await Await("SHELL_ALIVE_42", "the shell to answer after the guard acted");
            Require(pty.MemoryGuard!.Events().Any(e => e.Kind == GuardEventKind.Stopped), "The Processes pane has no event for the stop.");
            lock (text) Require(!text.ToString().Contains("emory guard", StringComparison.Ordinal), "No guard text reaches the terminal's output: " + text);
            var replay = await pty.AttachAsync(new(PtyTerminalService.SessionFor(tab), directory, "second", 100, 30) { TabKey = tab.TabKey });
            Require(!Encoding.UTF8.GetString(replay.Replay.Span).Contains("emory guard", StringComparison.Ordinal), "Nor its recording, which a later client replays.");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}