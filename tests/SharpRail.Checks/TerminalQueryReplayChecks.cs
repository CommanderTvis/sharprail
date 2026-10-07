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
        Console.WriteLine("PASS terminal replay: historical probes cannot type into a revived shell; split reads, old recordings, display content and live replies retained");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}