using System.Text;

using Ghostty.Avalonia;

using SharpRail.Host.Core;

namespace SharpRail.Checks;

internal static class TerminalReplayChecks
{
    internal static void Run()
    {
        const string visible = "old session\r\n\x1b[31mRésumé\x1b[0m\r\n\x1b]0;Title?\x07\x1b]8;;https://example.test/?q=1\x1b\\link\x1b]8;;\x1b\\";
        const string probes = "\x1b[6n\x1b]10;?\x07\x1b]11;?\x1b\\\x1b[?u\x1b[c";
        var bytes = Encoding.UTF8.GetBytes(visible + probes);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var recorder = new TerminalRecorder();
            recorder.Push(bytes.AsSpan(0, split));
            var partial = recorder.Snapshot();
            recorder.Push(bytes.AsSpan(split));
            var replay = recorder.Snapshot();
            Require(Encoding.UTF8.GetString(replay) == "\x1b[0m" + visible, "Replay lost display content or retained probes at split " + split);
            Require(recorder.From(0)!.AsSpan().SequenceEqual(bytes), "Live reconnect output must retain its exact query bytes.");
            if (OperatingSystem.IsMacOS())
            {
                var replies = new List<byte>();
                using var vt = new Vt(80, 24, 100, reply => replies.AddRange(reply.ToArray()));
                vt.Write(partial);
                vt.Write(replay);
                Require(replies.Count == 0, "Replayed queries wrote into the revived shell at split " + split);
                vt.Write(Encoding.UTF8.GetBytes(probes));
                Require(replies.Count > 0, "Live terminal queries must still be answered.");
            }
        }
        var restored = new TerminalRecorder();
        restored.Restore(bytes);
        Require(Encoding.UTF8.GetString(restored.Snapshot()) == "\x1b[0m" + visible, "Old recordings must be filtered on restore too.");
        var otherQueries = "\x1bZ\x1b[>0c\x1b[?6n\x1b[?2026$p\x1b[>q\x1b[14t\x1b[18t\x1bP$qm\x1b\\\x1bP+q544e\x1b\\\x1b]4;0;?\x07\x1b]52;c;?\x07";
        Require(TerminalReplay.WithoutQueries(Encoding.UTF8.GetBytes(otherQueries)).Length == 0, "Capability, mode, geometry and clipboard queries must not replay.");
        KittyGraphicsRevive();
        AlternateReplay();
        Console.WriteLine("PASS terminal replay: historical probes cannot type into a revived shell; split reads, old recordings, display content and live replies retained");
    }

    // An agent probes Kitty graphics on start; its recording is restored after a restart, revived, and restored
    // again. Answers to the replayed probes would precede the revive command in the new shell's input.
    private static void KittyGraphicsRevive()
    {
        const string probe = "\x1b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\x1b\\";
        const string silent = "\x1b_Ga=T,q=2,f=24,s=1,v=1;AAAA\x1b\\";
        var session = Encoding.UTF8.GetBytes("% claude --resume old\r\n" + probe + probe + probe + "agent output\r\n" + silent + "% ");
        var first = new TerminalRecorder();
        first.Push(session);
        var revived = new TerminalRecorder();
        revived.Restore(first.Snapshot());
        revived.Push("% "u8);
        var again = new TerminalRecorder();
        again.Restore(revived.Snapshot());
        foreach (var replay in new[] { first.Snapshot(), revived.Snapshot(), again.Snapshot() })
        {
            var text = Encoding.UTF8.GetString(replay);
            Require(!text.Contains("a=q"), "A replay kept the agent's Kitty graphics probe.");
            Require(text.Contains("agent output") && text.Contains(silent), "A replay lost display content or a silenced graphics command.");
            if (!OperatingSystem.IsMacOS()) continue;
            var replies = new List<byte>();
            using var vt = new Vt(80, 24, 100, reply => replies.AddRange(reply.ToArray()));
            vt.Write(replay);
            Require(replies.Count == 0, "Replayed Kitty graphics answered into the revived shell: " + Encoding.UTF8.GetString(replies.ToArray()).Replace("\x1b", "^["));
        }
        if (!OperatingSystem.IsMacOS()) return;
        var live = new List<byte>();
        using (var vt = new Vt(80, 24, 100, reply => live.AddRange(reply.ToArray())))
            vt.Write(Encoding.UTF8.GetBytes(probe));
        Require(Encoding.UTF8.GetString(live.ToArray()).Contains("Gi=31;OK"), "A live Kitty graphics probe must still be answered.");
    }

    private static void AlternateReplay()
    {
        const string before = "main screen\r\n";
        const string alternate = "\x1b[?1049h\x1b[2J\x1b[Hrecorded title\x1b[3;1HWorking 4\x1b[3;9H5\x1b[6n\x1b[?1000h";
        var bytes = Encoding.UTF8.GetBytes(before + alternate);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var recorder = new TerminalRecorder();
            recorder.Push(bytes.AsSpan(0, split));
            recorder.Push(bytes.AsSpan(split));
            var durable = recorder.Snapshot();
            var live = recorder.LiveScreen();
            Require(Encoding.UTF8.GetString(durable) == "\x1b[0m" + before, "Live alternate contents leaked into persisted recordings.");
            Require(Encoding.UTF8.GetString(live).Contains("recorded title", StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(live).Contains("\x1b[6n", StringComparison.Ordinal), "Alternate replay lost earlier content or retained a query at split " + split);
            if (OperatingSystem.IsMacOS())
            {
                var replies = new List<byte>();
                using var original = new Vt(80, 24, 100, _ => { });
                using var watched = new Vt(80, 24, 100, data => replies.AddRange(data.ToArray()));
                original.Write(bytes);
                watched.Write(durable);
                watched.Write(live);
                Require(watched.ScreenText() == original.ScreenText() && watched.ScreenText().Contains("Working 5", StringComparison.Ordinal),
                    "A fresh watcher must render the full existing alternate screen, including cursor-addressed updates.");
                Require(replies.Count == 0, "An alternate replay must not answer historical queries.");
            }
            recorder.Push("\x1b[?1049lreturned\r\n"u8);
            Require(recorder.LiveScreen().Length == 0 && !Encoding.UTF8.GetString(recorder.Snapshot()).Contains("recorded title", StringComparison.Ordinal),
                "Leaving the alternate screen must forget its contents and input modes.");
            recorder.Push("\x1b[?1049hnew screen"u8);
            Require(!Encoding.UTF8.GetString(recorder.LiveScreen()).Contains("recorded title", StringComparison.Ordinal), "A later alternate session inherited old contents.");
        }
        var restored = new TerminalRecorder();
        restored.Restore(bytes);
        Require(restored.LiveScreen().Length == 0, "Old recordings must never revive an alternate program's screen or input modes.");
        var bounded = new TerminalRecorder(32);
        bounded.Push("\x1b[?1049h"u8);
        bounded.Push(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("bounded line\r\n", 100))));
        Require(bounded.LiveScreen().Length <= 32 + 16, "Alternate recording must respect the configured replay bound.");
        var off = new TerminalRecorder(0);
        off.Push(bytes);
        Require(off.Snapshot().Length == 0 && off.LiveScreen().Length == 0, "Replay Off must also disable alternate recordings.");
        Console.WriteLine("PASS alternate replay: split modes, cursor updates, native VT screen parity, query filtering, durable hygiene, exit/reentry and bounds");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}