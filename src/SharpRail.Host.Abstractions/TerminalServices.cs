namespace SharpRail.Host.Abstractions;

// A terminal session belongs to the host, not to the client that shows it. The client names the session
// (a stable tab identity) and identifies itself, so that attaching elsewhere takes the session over.
// Offset is -1 for a fresh view, which receives a replay of recent output; a client that already shows
// output up to Offset resumes from there, and a resume never takes a session back from another client.
public record TerminalAttachRequest(string SessionId, string WorkspaceRoot, string ClientId, int Columns = 80, int Rows = 24, long Offset = -1)
{
    public bool Resume => Offset >= 0;
    // The tab's layout id, so the host knows the session's TerminalRef; empty from a client that does not send it.
    public string TabKey { get; init; } = "";
    // A yielding attach never takes a session from the client that holds it: it starts or joins a session
    // nobody holds, and otherwise returns already detached. Taking over is the plain attach.
    public bool Yield { get; init; }
    // A yielding attach that finds the session held watches it instead of returning detached, and a resume
    // keeps watching. A host that predates watching ignores it.
    public bool Watch { get; init; }
    // A command line the new shell runs as its program, through the user's login shell, before dropping into an
    // interactive shell in the same directory. Only an attach that starts the session uses it; nothing is typed.
    public string? Program { get; init; }
    // The session whose shell-reported working directory (OSC 7) a new shell starts in, as a terminal opened from another
    // one does; a session in another workspace, or one that reported none, leaves the workspace root.
    public string? DirectoryFrom { get; init; }
}

/// <summary>Where a terminal's shell is, by its shell integration's OSC 133 marks; Unknown before the first mark.</summary>
public enum TerminalShellPhase { Unknown, Prompt, Input, Running }

/// <summary>A command that finished: host output positions of its output's start (OSC 133 C) and end (D), and its exit code if reported.</summary>
public sealed record TerminalCommand(long Start, long End, int? ExitCode);

/// <summary>
/// What a terminal's shell integration reported, read by the host from the output it records: the phase, the working
/// directory (OSC 7, or an OSC 133 <c>cwd=</c> option), the last command's exit code and the recent commands.
/// </summary>
public sealed record TerminalShellStatus(TerminalShellPhase Phase, string? Directory, int? LastExitCode)
{
    /// <summary>The most recent finished commands, oldest first.</summary>
    public IReadOnlyList<TerminalCommand> Commands { get; init; } = [];
    /// <summary>Whether any OSC 133 mark arrived, so a consumer knows to prefer this over its own guesses.</summary>
    public bool Marked { get; init; }
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
    /// <summary>Stores a PNG on the host for pasting its path into this session.</summary>
    ValueTask<string> SaveClipboardImageAsync(string sessionId, ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default) => throw new NotSupportedException("This host does not accept clipboard images.");
    // Attaches to the session, starting its shell when none exists yet.
    ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default);
    // True while a process other than the shell owns the terminal's foreground.
    ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default);
    // Resolves one hovered logical line on the terminal host, using the current shell directory.
    ValueTask<IReadOnlyList<TerminalFileLink>> FindFileLinksAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<TerminalFileLink>>([]);
    // What the session's shell integration reported (OSC 133 phase and commands, OSC 7 directory); null for an unknown
    // session or a host that does not read it.
    ValueTask<TerminalShellStatus?> ShellStatusAsync(string sessionId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<TerminalShellStatus?>(null);
    // Ends the session's shell and forgets the session; closing a tab or its project ends it.
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
    // True for an attachment that watches a session another client holds: it receives the replay and the live
    // output, its input, resizes and kill are ignored, and it never detaches. Any number may watch.
    bool Watching { get; }
    // The grid the shell runs at, which its holder decides, as of the last chunk read. A watching reader
    // receives an empty chunk when it changes without output.
    (int Columns, int Rows) Grid { get; }
    // A single reader receives live output after the replay. It ends after the shell exits and its final
    // output drains, or when another client takes the session over. A watching reader that falls further
    // behind than the host keeps for it receives a terminal reset and a fresh snapshot instead of the gap.
    // A chunk is valid only until the reader asks for the next one: the host reuses pooled output buffers,
    // so a reader that keeps bytes beyond that copies them.
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default);
    // Input and resizes from a detached or watching client are ignored. A grid outside 1–32,767 is refused.
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    ValueTask KillAsync(CancellationToken cancellationToken = default);
    // The shell's exit code, or 128 plus the signal that ended it; completes after output ends.
    Task<int> Exit { get; }
    // Completes when another client attached to the session; this attachment then receives nothing more.
    // Already complete when a yielding attach found the session held by another client.
    Task Detached { get; }
    // Set on the attachment that started a shell for a tab whose agent record a plugin offered to revive: the
    // client types it once the shell's first output has arrived, pressing Return after it when Submit is set.
    TerminalPrefill? Prefill { get; }
}

public sealed record TerminalPrefill(string Text, bool Submit)
{
    // The host runs Text as the new shell's program instead of handing it to the client to type; a host that cannot
    // (Windows) hands it over as before.
    public bool Program { get; init; }
}

public sealed record TerminalFileLink(int Start, int Length, string Path, int Line, int Column, bool IsDirectory);