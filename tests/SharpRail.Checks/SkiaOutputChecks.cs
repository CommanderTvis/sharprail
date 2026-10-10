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
        foreach (var cancel in new[] { false, true })
        {
            var source = new OutputSession();
            using var backend = TerminalBackends.Ghostty(source,
                () => TerminalRenderers.Skia)
                (new(root, "output-check", root, "client"));
            var view = (GhosttySkiaView)((Border)backend.View).Child!;
            var interleaved = false;
            var window = new Window { Width = 640, Height = 320, Content = backend.View };
            view.Input += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                interleaved = !view.ReadScreen().Contains("REPLAY_END", StringComparison.Ordinal);
                if (cancel) backend.Dispose();
            }, DispatcherPriority.Input);
            var exitObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!cancel) _ = ObserveExit();
            window.Show();
            try
            {
                var deadline = Awake.Now.AddSeconds(20);
                while (cancel ? !backend.Started.IsCompleted : !exitObserved.Task.IsCompleted)
                {
                    if (Awake.Now > deadline) throw new InvalidOperationException("Skia output did not drain.");
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(1);
                }
                if (!interleaved) throw new InvalidOperationException("Replay monopolized the UI thread.");
                if (cancel)
                {
                    if (!backend.Started.IsCanceled || !source.Disposed)
                        throw new InvalidOperationException("Disposal did not cancel replay and detach the session.");
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
        Console.WriteLine("PASS Skia output: replay yields to input, disposal cancels draining, UTF-8 and final output precede exit");
    }

    private sealed class OutputSession : ITerminalService, ITerminalSession
    {
        public string Id => "output-check";
        public bool Created => true;
        public bool Watching => false;
        public (int Columns, int Rows) Grid => (80, 24);
        public long Position => 0;
        public TerminalPrefill? Prefill => null;
        public bool Disposed { get; private set; }
        public ReadOnlyMemory<byte> Replay { get; } = Encoding.UTF8.GetBytes("\e[6n" + new string('r', 32 * 1024) + "\r\nREPLAY_END\r\n");
        public Task<int> Exit => Task.FromResult(7);
        public Task Detached { get; } = new TaskCompletionSource().Task;
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<ITerminalSession>(this);
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var output = Encoding.UTF8.GetBytes(new string('x', 32 * 1024) + "\r\nLIVE_END_漢é");
            yield return output.AsMemory(0, output.Length - 1);
            yield return output.AsMemory(output.Length - 1);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask KillAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}