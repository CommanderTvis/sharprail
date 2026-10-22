using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

// A shell and its recent output, independent of the clients that view it. At most one attachment holds the
// shell: it drives it and decides its grid, and a new attachment from another client takes it over. Any number
// of others watch: they receive the same output and drive nothing.
internal sealed class HostedTerminal
{
    private const int RedrawRestoreMilliseconds = 50;
    private readonly Lock gate = new();
    private readonly TerminalRecorder recorder;
    private readonly TerminalShellState shell = new();
    private Attachment? current;
    private readonly List<Attachment> watchers = [];
    private string? client;
    private int? exitCode;
    private (int Columns, int Rows) grid;

    internal ITerminalProcess Process { get; }

    /// <summary>Raised after the shell accepted input from the attachment that drives it.</summary>
    internal Action? InputAccepted { get; init; }

    /// <summary>Receives, on the read pump, each progress, bell and notification in live output; never replays.</summary>
    internal Action<TerminalSignal>? Signaled { get; init; }
    internal string SessionId { get; init; } = "";
    private readonly TerminalSignalParser signals = new();

    internal HostedTerminal(ITerminalProcess process, int columns, int rows, byte[]? restored = null, int replayBytes = TerminalRecorder.SnapshotBytes)
    {
        Process = process;
        recorder = new(replayBytes);
        if (restored is { Length: > 0 }) recorder.Restore(restored);
        grid = (columns, rows);
        _ = Task.Run(Pump);
    }

    /// <summary>What the shell's integration reported so far.</summary>
    internal TerminalShellStatus Shell
    {
        get { lock (gate) return shell.Status; }
    }

    internal byte[] Recording()
    {
        lock (gate) return recorder.Snapshot();
    }

    private async Task Pump()
    {
        try
        {
            // The pump owns each chunk until delivery: attachments that queue it retain their own reference.
            await foreach (var chunk in Process.ReadAsync())
                try
                {
                    if (Signaled is { } signaled) signals.Scan(chunk.Span, SessionId, signaled);
                    while (true)
                    {
                        Task? space;
                        lock (gate)
                        {
                            space = current?.WaitForSpace(chunk.Length);
                            if (space is null)
                            {
                                recorder.Push(chunk.Span);
                                shell.Push(chunk.Span);
                                current?.Deliver(chunk, recorder.Position, grid);
                                foreach (var watcher in watchers) watcher.Deliver(chunk, recorder.Position, grid);
                                break;
                            }
                        }
                        // Never wait under the output lock: a detached or displaced holder must release us.
                        await space;
                    }
                }
                finally { chunk.Release(); }
        }
        catch (Exception) { }
        var code = await Process.Exit;
        // The shell's output has ended: whatever progress it showed is over.
        Signaled?.Invoke(new(SessionId, TerminalSignalKind.Progress) { Progress = TerminalProgressState.None });
        lock (gate)
        {
            exitCode = code;
            current?.Finish(code);
            foreach (var watcher in watchers) watcher.Finish(code);
            watchers.Clear();
        }
    }

    internal async ValueTask DisposeAsync()
    {
        // Closing a shell must still drain its PTY even when its client has stopped reading.
        lock (gate) current?.Stop();
        await Process.DisposeAsync();
    }

    // Replay and the switch to live delivery happen under the output lock, so every byte reaches the
    // attachment exactly once: either in its replay or as a live chunk.
    internal Attachment Attach(TerminalAttachRequest request, bool created, TerminalPrefill? prefill = null)
    {
        lock (gate)
        {
            // A yielding attach leaves the session with the client that holds it, and its grid with it.
            var held = request.Yield && current is not null && client != request.ClientId;
            if (request.Watch && (request.Resume || held))
            {
                var watcher = new Attachment(this, Process.Id, false, request.Resume ? recorder.From(request.Offset) ?? Reset() : Fresh(), recorder.Position, grid, watching: true);
                if (exitCode is { } ended) watcher.Finish(ended);
                else watchers.Add(watcher);
                return watcher;
            }
            // A resuming client that lost the session to another client must not take it back.
            if (held || request.Resume && client != request.ClientId) return Attachment.Displaced(this, Process.Id, recorder.Position, grid);
            var replay = request.Resume ? recorder.From(request.Offset) ?? Fresh() : Fresh();
            var attachment = new Attachment(this, Process.Id, created, replay, recorder.Position, grid) { Prefill = prefill };
            var previous = current;
            current = attachment; client = request.ClientId;
            previous?.Displace();
            if (exitCode is { } code) attachment.Finish(code);
            else if (request.Resume) { if (request.Columns > 0 && request.Rows > 0 && grid != (request.Columns, request.Rows)) Resize(request.Columns, request.Rows); }
            else if (!created) Fit(request.Columns, request.Rows);
            return attachment;
        }
    }

    private byte[] Fresh() => exitCode is null ? [.. recorder.Snapshot(), .. recorder.LiveScreen()] : recorder.Snapshot();

    // A view that already shows older output starts over: a full terminal reset, then the snapshot.
    private byte[] Reset() => [.. "\x1bc"u8, .. Fresh()];

    // What a watcher that fell behind reads next, and from where its live output continues.
    internal (ReadOnlyMemory<byte> Data, long End, (int Columns, int Rows) Grid) Resynchronise(Attachment watcher)
    {
        lock (gate)
        {
            watcher.Behind = false;
            return (Reset(), recorder.Position, grid);
        }
    }

    // A fresh view needs the foreground program to repaint. Resizing to the same grid sends no SIGWINCH,
    // so it narrows the grid briefly and restores it later, as upstream does.
    private void Fit(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0) return;
        if (grid != (columns, rows)) { Resize(columns, rows); return; }
        var nudged = columns > 1 ? (columns - 1, rows) : rows > 1 ? (columns, rows - 1) : grid;
        if (nudged == grid) return;
        Resize(nudged.Item1, nudged.Item2, track: false);
        _ = Task.Delay(RedrawRestoreMilliseconds).ContinueWith(_ =>
        {
            lock (gate)
                if (exitCode is null && grid == (columns, rows)) Resize(columns, rows, track: false);
        }, TaskScheduler.Default);
    }

    private void Resize(int columns, int rows, bool track = true)
    {
        try { Process.ResizeAsync(columns, rows).AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { return; }
        if (!track) return;
        grid = (columns, rows);
        foreach (var watcher in watchers) watcher.Deliver(null, recorder.Position, grid);
    }

    private bool Drives(Attachment attachment)
    {
        if (attachment.Watching) return false;
        lock (gate)
        {
            if (ReferenceEquals(current, attachment)) return true;
        }
        attachment.Displace();
        return false;
    }

    internal async ValueTask WriteAsync(Attachment attachment, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!Drives(attachment)) return;
        await Process.WriteAsync(data, cancellationToken);
        InputAccepted?.Invoke();
    }

    internal void Resize(Attachment attachment, int columns, int rows)
    {
        if (!Drives(attachment)) return;
        lock (gate) if (grid != (columns, rows) && exitCode is null) Resize(columns, rows);
    }

    internal async ValueTask KillAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        if (Drives(attachment)) await Process.KillAsync(cancellationToken);
    }

    internal void Detach(Attachment attachment)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, attachment)) current = null;
            else watchers.Remove(attachment);
        }
    }
}

internal sealed class Attachment : ITerminalSession
{
    // What a reader that stopped reading may leave queued: the resume window, so nothing a resume could still
    // fetch is held twice.
    internal const int BacklogBytes = TerminalRecorder.ResumeBytes;
    private readonly HostedTerminal terminal;
    // Not single-reader: delivery drops the oldest chunks itself when the reader falls behind. An item without
    // data and without a grid marks where a watcher that fell behind resynchronises. Each queued chunk holds one
    // reference, released when the reader moves past it or delivery drops it.
    private readonly Channel<(TerminalChunk? Chunk, long End, (int Columns, int Rows) Grid)> output =
        Channel.CreateUnbounded<(TerminalChunk?, long, (int, int))>();
    private const int BatchBytes = 128 * 1024;
    private long backlog, grid;
    private readonly Lock flow = new();
    private TaskCompletionSource? space;
    private bool stopping;
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int reading, disposed;
    private long position;

    internal Attachment(HostedTerminal terminal, string id, bool created, byte[] replay, long position, (int Columns, int Rows) grid, bool watching = false)
    {
        this.terminal = terminal; Id = id; Created = created; Replay = replay; this.position = position; Watching = watching;
        this.grid = Pack(grid);
    }

    internal static Attachment Displaced(HostedTerminal terminal, string id, long position, (int Columns, int Rows) grid)
    {
        var attachment = new Attachment(terminal, id, false, [], position, grid);
        attachment.Displace();
        return attachment;
    }

    public string Id { get; }
    public bool Created { get; }
    public bool Watching { get; }
    public (int Columns, int Rows) Grid => ((int)(Interlocked.Read(ref grid) >> 32), (int)Interlocked.Read(ref grid));
    // Set and cleared under the terminal's output lock: nothing is queued until the reader resynchronises.
    internal bool Behind { get; set; }
    public ReadOnlyMemory<byte> Replay { get; }
    public long Position => Interlocked.Read(ref position);
    public Task<int> Exit => exit.Task;
    public Task Detached => detached.Task;
    public TerminalPrefill? Prefill { get; init; }

    private static long Pack((int Columns, int Rows) grid) => (long)grid.Columns << 32 | (uint)grid.Rows;

    // Called under the terminal's output lock before recording/delivering a chunk. An active reader
    // must receive every byte: dropping arbitrary output corrupts escape sequences and incremental TUIs.
    internal Task? WaitForSpace(int bytes)
    {
        lock (flow)
        {
            if (stopping || Interlocked.Read(ref backlog) + bytes <= BacklogBytes) return null;
            return (space ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    internal void Stop() { stopping = true; SignalSpace(); }

    private void SignalSpace()
    {
        lock (flow) { space?.TrySetResult(); space = null; }
    }

    // A stalled read-only watcher resynchronises from a snapshot instead of blocking the holder.
    internal void Deliver(TerminalChunk? chunk, long end, (int Columns, int Rows) grid)
    {
        if (stopping || Behind) return;
        chunk?.Retain();
        if (!output.Writer.TryWrite((chunk, end, grid))) { chunk?.Release(); return; }
        var queued = Interlocked.Add(ref backlog, chunk?.Length ?? 0);
        if (queued <= BacklogBytes) return;
        if (!Watching) throw new InvalidOperationException("Terminal holder exceeded its output capacity.");
        while (output.Reader.TryRead(out var dropped))
        {
            Interlocked.Add(ref backlog, -(dropped.Chunk?.Length ?? 0));
            dropped.Chunk?.Release();
        }
        Behind = output.Writer.TryWrite(default);
    }

    internal void Finish(int code)
    {
        output.Writer.TryComplete();
        exit.TrySetResult(code);
    }

    internal void Displace()
    {
        if (exit.Task.IsCompleted) return;
        output.Writer.TryComplete();
        detached.TrySetResult();
        SignalSpace();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        // A yielded chunk stays valid until the reader asks for the next one; then its reference is released.
        TerminalChunk? held = null;
        byte[]? batch = null;
        try
        {
            await foreach (var item in output.Reader.ReadAllAsync(cancellationToken))
            {
                ReadOnlyMemory<byte> data;
                long end;
                (int Columns, int Rows) size;
                if (item.Grid == default) (data, end, size) = terminal.Resynchronise(this);
                else
                {
                    (held, end, size) = item;
                    data = held?.Memory ?? default;
                    Interlocked.Add(ref backlog, -data.Length);
                    // Amortize RPC framing and browser thread hops over already queued PTY reads.
                    // A grid change or watcher resynchronisation must remain a separate boundary.
                    var batched = 0;
                    while (output.Reader.TryPeek(out var next) && next.Grid == size &&
                        next.End == end + (next.Chunk?.Length ?? 0) && data.Length + (next.Chunk?.Length ?? 0) <= BatchBytes)
                    {
                        if (!output.Reader.TryRead(out next)) break;
                        if (batched == 0)
                        {
                            batch ??= ArrayPool<byte>.Shared.Rent(BatchBytes);
                            data.Span.CopyTo(batch);
                            batched = data.Length;
                            held?.Release();
                            held = null;
                        }
                        if (next.Chunk is { } chunk)
                        {
                            chunk.Span.CopyTo(batch.AsSpan(batched));
                            batched += chunk.Length;
                            chunk.Release();
                        }
                        data = batch.AsMemory(0, batched);
                        end = next.End;
                        Interlocked.Add(ref backlog, -(next.Chunk?.Length ?? 0));
                    }
                    if (!Watching) SignalSpace();
                }
                Interlocked.Exchange(ref position, end);
                Interlocked.Exchange(ref grid, Pack(size));
                yield return data;
                held?.Release();
                held = null;
            }
        }
        finally
        {
            held?.Release();
            if (batch is not null) ArrayPool<byte>.Shared.Return(batch);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return terminal.WriteAsync(this, data, cancellationToken);
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        TerminalGrid.Require(columns, rows);
        terminal.Resize(this, columns, rows);
        return ValueTask.CompletedTask;
    }

    public ValueTask KillAsync(CancellationToken cancellationToken = default) => terminal.KillAsync(this, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        terminal.Detach(this);
        SignalSpace();
        output.Writer.TryComplete();
        exit.TrySetException(new ObjectDisposedException(nameof(Attachment)));
        _ = exit.Task.Exception;
        return ValueTask.CompletedTask;
    }
}