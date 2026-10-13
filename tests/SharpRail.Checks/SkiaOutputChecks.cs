using System.Runtime.CompilerServices;
using System.Text;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

internal static class SkiaOutputChecks
{
    internal static void Run(string root)
    {
        foreach (var (cancel, live) in new[] { (false, false), (true, false), (true, true) })
        {
            var source = new OutputSession(live);
            using var backend = TerminalBackends.Ghostty(source,
                () => TerminalRenderers.Skia)
                (new(root, "output-check", root, "client"));
            var view = (GhosttySkiaView)((Border)backend.View).Child!;
            var interleaved = false;
            var window = new Window { Width = 640, Height = 320, Content = backend.View };
            view.Input += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                interleaved = !view.ReadScreen().Contains(live ? "LIVE_END" : "REPLAY_END", StringComparison.Ordinal);
                if (cancel) backend.Dispose();
            }, DispatcherPriority.Input);
            var exitObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!cancel) _ = ObserveExit();
            window.Show();
            try
            {
                var deadline = Awake.Now.AddSeconds(20);
                while (cancel ? !backend.Started.IsCompleted || !source.Disposed || live && !source.ReadStopped : !exitObserved.Task.IsCompleted)
                {
                    if (Awake.Now > deadline) throw new InvalidOperationException("Skia output did not drain.");
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(1);
                }
                if (!interleaved) throw new InvalidOperationException("Replay monopolized the UI thread.");
                if (cancel)
                {
                    if ((live ? !backend.Started.IsCompletedSuccessfully : !backend.Started.IsCanceled) || !source.Disposed)
                        throw new InvalidOperationException("Disposal did not cancel output and detach the session.");
                }
                else exitObserved.Task.GetAwaiter().GetResult();
            }
            finally { window.Close(); }

            async Task ObserveExit()
            {
                try
                {
                    await backend.Exited.ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (backend.Exited.Result != 7 || !view.ReadScreen().Contains("LIVE_END_漢é", StringComparison.Ordinal))
                            throw new InvalidOperationException("Exit overtook final output or split UTF-8 was corrupted.");
                    }, DispatcherPriority.Send);
                    exitObserved.TrySetResult();
                }
                catch (Exception error) { exitObserved.TrySetException(error); }
            }
        }
        WatchResize(root);
        Console.WriteLine("PASS Skia output: replay and live floods yield to input, disposal cancels the producer and draining, UTF-8 and final output precede exit");
    }

    private static void WatchResize(string root)
    {
        var source = new OutputSession(false, watched: true);
        using var backend = new SkiaTerminal(new(root, "watched-output", root, "client"), source);
        var window = new Window { Width = 640, Height = 320, Content = backend };
        window.Show();
        try
        {
            var deadline = Awake.Now.AddSeconds(10);
            while (!backend.Exited.IsCompleted)
            {
                if (Awake.Now > deadline) throw new InvalidOperationException("Watched output did not drain.");
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(1);
            }
            backend.Exited.GetAwaiter().GetResult();
            if (backend.Terminal.Size != new TerminalSize(10, 24) || backend.ReadScreen().TrimEnd() != "A        X")
                throw new InvalidOperationException($"Watcher output was parsed before its ordered grid resize: {backend.Terminal.Size}, {System.Text.Json.JsonSerializer.Serialize(backend.ReadScreen())}");
        }
        finally { window.Close(); }
    }

    private sealed class OutputSession(bool live, bool watched = false) : ITerminalService, ITerminalSession
    {
        public string Id => "output-check";
        public bool Created => true;
        public bool Watching => watched;
        public (int Columns, int Rows) Grid { get; private set; } = (80, 24);
        public long Position => 0;
        public TerminalPrefill? Prefill => null;
        public bool Disposed { get; private set; }
        private bool readStopped;
        public bool ReadStopped => Volatile.Read(ref readStopped);
        public ReadOnlyMemory<byte> Replay { get; } = live || watched ? [] : Encoding.UTF8.GetBytes("\e[6n" + new string('r', 32 * 1024) + "\r\nREPLAY_END\r\n");
        public Task<int> Exit => Task.FromResult(7);
        public Task Detached { get; } = new TaskCompletionSource().Task;
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<ITerminalSession>(this);
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (watched)
            {
                Grid = (10, 24);
                yield return "\e[2J\e[HA\e[999CX"u8.ToArray();
                yield break;
            }
            var output = Encoding.UTF8.GetBytes((live ? "\e[6n" : "") + new string('x', 2 * 1024 * 1024) + "\r\nLIVE_END_漢é");
            // Small host packets must cross the bounded read-ahead queue without losing their order.
            try
            {
                for (var offset = 0; offset < output.Length; offset += 127)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return output.AsMemory(offset, Math.Min(127, output.Length - offset));
                }
            }
            finally { Volatile.Write(ref readStopped, true); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask KillAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}