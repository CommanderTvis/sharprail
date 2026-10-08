namespace SharpRail.Host.Abstractions;

// A terminal session belongs to the host, not to the client that shows it. The client names the session
// (a stable tab identity) and identifies itself, so that attaching elsewhere takes the session over.
// Offset is -1 for a fresh view, which receives a replay of recent output; a client that already shows
// output up to Offset resumes from there, and a resume never takes a session back from another client.
public record TerminalAttachRequest(string SessionId, string WorkspaceRoot, string ClientId, int Columns = 80, int Rows = 24, long Offset = -1)
{
    public bool Resume => Offset >= 0;
}

// A PTY's size is a pair of unsigned shorts; a grid outside it is refused before any session work.
public static class TerminalGrid
{
    public const int Max = 32767;

    public static void Require(int columns, int rows)
    {
        if (columns is < 1 or > Max || rows is < 1 or > Max)
            throw new ArgumentOutOfRangeException(nameof(columns), "A terminal grid must have between 1 and 32,767 columns and rows.");
    }
}

public interface ITerminalService
{
    // Attaches to the session, starting its shell when none exists yet.
    ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default);
    // True while a process other than the shell owns the terminal's foreground.
    ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default);
    // Ends the session's shell and forgets the session; closing a tab is the only way a client ends one.
    ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default);
}

// One client's attachment to a host session. Disposing it detaches without ending the shell.
public interface ITerminalSession : IAsyncDisposable
{
    string Id { get; }
    // True when this attachment started the shell.
    bool Created { get; }
    // Recent output to show before live output: a snapshot for a fresh view, or what a resuming client missed.
    ReadOnlyMemory<byte> Replay { get; }
    // The host output position after the replay and every chunk read since; resuming from it replays nothing twice.
    long Position { get; }
    // A single reader receives live output after the replay. It ends after the shell exits and its final
    // output drains, or when another client takes the session over.
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default);
    // Input and resizes from a detached client are ignored. A grid outside 1–32,767 is refused.
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    ValueTask KillAsync(CancellationToken cancellationToken = default);
    // The shell's exit code, or 128 plus the signal that ended it; completes after output ends.
    Task<int> Exit { get; }
    // Completes when another client attached to the session; this attachment then receives nothing more.
    Task Detached { get; }
}