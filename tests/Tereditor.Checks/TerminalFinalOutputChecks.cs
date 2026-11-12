using Tereditor.Host.Client;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

// A shell that writes and exits right after the read pump's poll timed out must not lose that output. The hook holds the pump
// between its timed-out poll and its decision to stop, so the write and the exit land exactly in the window that once dropped them.
// macOS cannot reach that window: a process does not finish exiting from its terminal until the master side has drained its output.
internal static partial class TerminalHostChecks
{
    private static async Task FinalOutputSurvivesIdlePoll(string workspace)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP terminal final output: only Linux lets a shell finish exiting while its output is unread.");
            return;
        }
        var id = "final-output-" + Guid.NewGuid().ToString("N");
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var held = 0;
        var armed = false;
        PtySession.IdlePollHook = session =>
        {
            if (session != id || !Volatile.Read(ref armed) || Interlocked.Exchange(ref held, 1) != 0) return;
            idle.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var pidFile = Path.Combine(workspace, "final.pid");
        try
        {
            await using var local = new PtyTerminalService("/bin/sh");
            var terminals = new LocalTerminalAdapter(local);
            var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
            var screen = new Screen(session);
            try
            {
                // Hold the pump only once the shell is idle at its prompt, so the command runs while the pump is stopped.
                await screen.WaitFor("$", "final output");
                Volatile.Write(ref armed, true);
                await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await screen.Run("echo $$ > final.pid; printf 'FINAL_%s\\n' OUTPUT; exit 7");
                await Until(() => Task.FromResult(File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && !Running(pid)), "the shell exiting while the pump is held");
                release.Set();
                Require(await session.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 7, "final output: the exit status was not reported.");
                await screen.Completed;
                Require(screen.Text.Contains("FINAL_OUTPUT", StringComparison.Ordinal),
                    "Output the shell wrote just before it exited was lost when the read pump's poll had timed out first.");
            }
            finally { release.Set(); await session.DisposeAsync(); await terminals.CloseAsync(id); }
        }
        finally { PtySession.IdlePollHook = null; release.Set(); File.Delete(pidFile); }
        Console.WriteLine("PASS terminal final output: output written before exit survives a read pump poll that timed out just before the exit");
    }
}