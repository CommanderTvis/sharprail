namespace SharpRail.Host.Abstractions;

// The client chooses the session id so it can later ask about the session by name.
public record TerminalStartRequest(string SessionId, string WorkspaceRoot, int Columns = 80, int Rows = 24);

public interface ITerminalService
{
    ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default);
    // True while a process other than the shell owns the terminal's foreground.
    ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default);
}

public interface ITerminalSession : IAsyncDisposable
{
    string Id { get; }
    // A single reader receives all output; it ends after the shell exits and its final output drains.
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    ValueTask KillAsync(CancellationToken cancellationToken = default);
    // The shell's exit code, or 128 plus the signal that ended it; completes after output ends.
    Task<int> Exit { get; }
}
