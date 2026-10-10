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
    private Attachment? current;
    private readonly List<Attachment> watchers = [];
    private string? client;
    private int? exitCode;
    private (int Columns, int Rows) grid;

    internal PtySession Process { get; }

    internal HostedTerminal(PtySession process, int columns, int rows, byte[]? restored = null, int replayBytes = TerminalRecorder.SnapshotBytes)
    {
        Process = process;
        recorder = new(replayBytes);
        if (restored is { Length: > 0 }) recorder.Restore(restored);
        grid = (columns, rows);
        _ = Task.Run(Pump);
    }

    internal byte[] Recording()
    {
        lock (gate) return recorder.Snapshot();
    }

    private async Task Pump()
    {
        try
        {
            await foreach (var chunk in Process.ReadAsync())
                lock (gate)
                {
                    recorder.Push(chunk.Span);
                    current?.Deliver(chunk, recorder.Position, grid);
                    foreach (var watcher in watchers) watcher.Deliver(chunk, recorder.Position, grid);
                }
        }
        catch (Exception) { }
        var code = await Process.Exit;
        lock (gate)
        {
            exitCode = code;
            current?.Finish(code);
            foreach (var watcher in watchers) watcher.Finish(code);
            watchers.Clear();
        }
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

    private byte[] Fresh() => exitCode is null ? [.. recorder.Snapshot(), .. recorder.LiveModes()] : recorder.Snapshot();

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
        foreach (var watcher in watchers) watcher.Deliver(default, recorder.Position, grid);
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
        if (Drives(attachment)) await Process.WriteAsync(data, cancellationToken);
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
    // data and without a grid marks where a watcher that fell behind resynchronises.
    private readonly Channel<(ReadOnlyMemory<byte> Data, long End, (int Columns, int Rows) Grid)> output =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, long, (int, int))>();
    private long backlog, grid;
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

    // Called under the terminal's output lock, and never waits for the reader. A holder that cannot keep up
    // loses the oldest output, never the newest, and the host never holds more than the backlog for it;
    // positions still advance past the gap. A watcher that cannot keep up loses everything queued and, once
    // it reads again, starts over from a snapshot taken then, so a stalled one costs nothing meanwhile.
    internal void Deliver(ReadOnlyMemory<byte> chunk, long end, (int Columns, int Rows) grid)
    {
        if (Behind || !output.Writer.TryWrite((chunk, end, grid))) return;
        var queued = Interlocked.Add(ref backlog, chunk.Length);
        if (queued <= BacklogBytes) return;
        if (Watching)
        {
            while (output.Reader.TryRead(out var dropped)) Interlocked.Add(ref backlog, -dropped.Data.Length);
            Behind = output.Writer.TryWrite(default);
            return;
        }
        while (queued > BacklogBytes && output.Reader.TryRead(out var dropped))
            queued = Interlocked.Add(ref backlog, -dropped.Data.Length);
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
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        await foreach (var item in output.Reader.ReadAllAsync(cancellationToken))
        {
            var (data, end, size) = item.Grid == default ? terminal.Resynchronise(this) : item;
            if (item.Grid != default) Interlocked.Add(ref backlog, -data.Length);
            Interlocked.Exchange(ref position, end);
            Interlocked.Exchange(ref grid, Pack(size));
            yield return data;
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
        output.Writer.TryComplete();
        exit.TrySetException(new ObjectDisposedException(nameof(Attachment)));
        _ = exit.Task.Exception;
        return ValueTask.CompletedTask;
    }
}