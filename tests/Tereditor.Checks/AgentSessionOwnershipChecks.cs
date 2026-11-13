using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Api;

namespace Tereditor.Checks;

// Who has an agent session open after a restart, against fake process tables (no real process is inspected or signalled):
// a tab's own earlier agent is recognised by its recorded pid and start time, a reused pid is not, a hand-started agent
// keeps the "did not start" refusal, and a terminal the host still runs reattaches without any offer.
internal static class AgentSessionOwnershipChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const string Session = "665db1bf-0000-4000-8000-000000000001";
    private static readonly long Started = new DateTime(2026, 11, 13, 2, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly TimeSpan Age = TimeSpan.FromHours(17);

    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "agent-session-ownership");
        Directory.CreateDirectory(directory);
        var self = Environment.ProcessId;
        // This host, as the process table shows it.
        var host = new ProcessRow(self, 1, TimeSpan.FromMinutes(5), "??", "/Applications/tereditor.app/Contents/MacOS/tereditor");
        // The revived tab's fresh shell, under this host.
        var shell = new ProcessRow(9100, self, TimeSpan.FromSeconds(3), "ttys009", "-zsh");
        // The Claude the tab ran before the restart: its shell is gone, it was reparented to init and lost its terminal.
        var orphan = new ProcessRow(47558, 1, Age, "??", "claude --resume " + Session);
        AgentSessionOwnership Ownership(Dictionary<int, long> starts, params ProcessRow[] rows) =>
            new(directory) { Table = () => [.. rows], StartOf = pid => starts.GetValueOrDefault(pid) };

        // b: the recorded pid with the same start time is the tab's own earlier agent.
        var ours = Ownership(new() { [orphan.Pid] = Started }, host, shell, orphan).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started));
        Require(ours is { Kind: SessionOwnerKind.Stray, Recorded: true, CanQuit: true } && ours.Pid == orphan.Pid,
            "The tab's own earlier Claude, still running, is recognised by pid and start time: " + ours);
        Require(ours!.Describe() == "is still running in an earlier tereditor terminal, as pid 47558 (started 17 hours ago). The tereditor run that started it has ended.",
            "The earlier terminal's agent is described as tereditor's: " + ours.Describe());
        Require(ours.QuitConfirmation().StartsWith("End pid 47558 ", StringComparison.Ordinal), "Ending it is offered behind a confirmation naming the pid.");
        Console.WriteLine("PASS agent session ownership: a tab's own earlier agent is recognised by pid and start time and offered as tereditor's");

        // A reused pid (same number, another start time) is not ours: one naming the session is foreign as before.
        var reused = Ownership(new() { [orphan.Pid] = Started + TimeSpan.TicksPerSecond }, host, shell, orphan).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started));
        Require(reused is { Kind: SessionOwnerKind.Foreign, Recorded: false, CanQuit: false } && reused.Describe().EndsWith("which tereditor did not start.", StringComparison.Ordinal),
            "A pid whose start time differs is not the recorded agent: " + reused);
        // ...and one that does not name the session blocks nothing.
        var stranger = orphan with { Arguments = "/usr/bin/vim notes.txt" };
        Require(Ownership(new() { [orphan.Pid] = Started + 1 }, host, shell, stranger).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started)) is null,
            "A reused pid running something else does not hold the session.");
        // An unknown start time never matches.
        Require(Ownership(new() { [orphan.Pid] = 0 }, host, shell, stranger).Inspect("claude", Session, [shell.Pid], (orphan.Pid, 0)) is null &&
            Ownership(new() { [orphan.Pid] = 0 }, host, shell, stranger).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started)) is null,
            "An unknown start time is never taken for the recorded agent.");
        Console.WriteLine("PASS agent session ownership: a reused pid or an unknown start time is not taken for the tab's agent");

        // c: no record, nothing of tereditor above it: the old refusal stands.
        var foreign = Ownership(new(), host, shell, orphan).Inspect("claude", Session, [shell.Pid]);
        Require(foreign is { Kind: SessionOwnerKind.Foreign, CanQuit: false } && foreign.Describe() == "is open in pid 47558 (started 17 hours ago), which tereditor did not start.",
            "A Claude tereditor did not start keeps the guard's text: " + foreign?.Describe());
        Console.WriteLine("PASS agent session ownership: an agent started outside tereditor keeps the \"did not start\" refusal");

        // The recorded agent wins over another process that only names the session, and a live tereditor above wins over both.
        var handStarted = new ProcessRow(300, 1, Age, "??", "claude --resume " + Session);
        var weighed = Ownership(new() { [orphan.Pid] = Started }, handStarted, host, shell, orphan).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started));
        Require(weighed is { Recorded: true } && weighed.Pid == orphan.Pid, "The tab's own agent is named over a foreign process naming the session: " + weighed);
        var otherShell = new ProcessRow(9200, self, Age, "ttys010", "-zsh");
        var live = new ProcessRow(9201, otherShell.Pid, Age, "ttys010", "claude --resume " + Session);
        var open = Ownership(new() { [orphan.Pid] = Started }, host, shell, orphan, otherShell, live).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started));
        Require(open is { Kind: SessionOwnerKind.Instance, CanQuit: false } && open.Pid == live.Pid, "A copy under a live tereditor is named first and never quittable: " + open);
        // The recorded agent under a live tereditor is that tereditor's, not a stray.
        var hosted = Ownership(new() { [live.Pid] = Started }, host, shell, otherShell, live).Inspect("claude", Session, [shell.Pid], (live.Pid, Started));
        Require(hosted is { Kind: SessionOwnerKind.Instance, CanQuit: false }, "The recorded agent still under a live tereditor is open there: " + hosted);
        // An ancestor of this host (the app started from that agent's terminal) is ours but never offered for ending.
        var parent = new ProcessRow(47600, orphan.Pid, Age, "??", "dotnet run --file scripts/tasks/native.cs");
        var child = host with { Ppid = parent.Pid };
        var ancestor = Ownership(new() { [orphan.Pid] = Started }, child, parent, shell, orphan).Inspect("claude", Session, [shell.Pid], (orphan.Pid, Started));
        Require(ancestor is { Kind: SessionOwnerKind.Stray, Recorded: true, CanQuit: false }, "An agent the running app descends from is never quittable: " + ancestor);
        Console.WriteLine("PASS agent session ownership: a live tereditor's copy is named first, and the app's own ancestor is never quittable");

        await Reattach(directory);
    }

    // a: a session the host still runs is attached to again; no revive and no offer.
    private static async Task Reattach(string directory)
    {
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var terminal = new TerminalRef(workspace, "terminal:orchestrator");
        var offers = 0;
        await using var host = new PtyTerminalService(shell: "/bin/sh")
        {
            RevivePrefill = known => known.TabKey == terminal.TabKey ? new TerminalPrefill("claude --resume " + Session, false) : null,
            ReviveGate = (_, _) => { offers++; return true; }
        };
        var session = PtyTerminalService.SessionFor(terminal);
        var first = await host.AttachAsync(new(session, workspace, "window-1") { TabKey = terminal.TabKey });
        Require(first.Created && offers == 1, "A shell started for a tab with an agent record offers the resume once.");
        await first.DisposeAsync();
        var again = await host.AttachAsync(new(session, workspace, "window-2") { TabKey = terminal.TabKey });
        Require(!again.Created && offers == 1, "Reattaching to a session the host still runs starts nothing and offers nothing.");
        await again.DisposeAsync();
        await host.CloseAsync(session);
        Console.WriteLine("PASS agent session ownership: a tab whose terminal is still hosted reattaches without an offer");
    }
}